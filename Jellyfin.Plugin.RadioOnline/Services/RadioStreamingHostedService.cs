using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the continuous radio streaming loop.
/// Streams audio to Icecast ONLY when a scheduled playlist is active.
/// When no schedule is active or the playlist ends, the Icecast connection is stopped.
/// When schedules overlap, the later-starting entry takes priority and interrupts the earlier one.
/// Uses FFmpeg concat demuxer for gapless track-to-track transitions within a playlist.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly IcecastStreamingService _icecastService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;

    /// <summary>
    /// How often to check for schedule changes while streaming (seconds).
    /// </summary>
    private const int ScheduleCheckIntervalSeconds = 5;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        IcecastStreamingService icecastService,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider)
    {
        _logger = logger;
        _icecastService = icecastService;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
    }

    /// <summary>
    /// Executes the main radio streaming loop.
    /// Only streams when a scheduled playlist is active.
    /// Stops Icecast when no schedule is active or the playlist ends.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online streaming service started");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = Plugin.Instance?.Configuration as PluginConfiguration;

                if (config == null || !config.IsEnabled)
                {
                    if (_icecastService.IsStreaming)
                    {
                        _logger.LogInformation("Plugin disabled, stopping Icecast stream");
                        _icecastService.StopStreaming();
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!ValidateConfig(config))
                {
                    if (_icecastService.IsStreaming)
                    {
                        _icecastService.StopStreaming();
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunStreamingCycle(config, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in streaming cycle, retrying in 15 seconds");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _icecastService.StopStreaming();
        _logger.LogInformation("Radio Online streaming service stopped");
    }

    /// <summary>
    /// Runs a single streaming cycle: checks schedule, streams playlist if active,
    /// stops when playlist ends or schedule changes.
    /// </summary>
    private async Task RunStreamingCycle(PluginConfiguration config, CancellationToken cancellationToken)
    {
        // Get the active schedule entry (handles overlaps by picking latest start time)
        var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

        if (activeEntry == null || string.IsNullOrEmpty(activeEntry.PlaylistId))
        {
            // No active schedule - ensure streaming is stopped and wait
            if (_icecastService.IsStreaming)
            {
                _logger.LogInformation("No active schedule, stopping Icecast stream");
                _icecastService.StopStreaming();
            }

            // Wait until next schedule starts or re-check interval
            var timeUntilNext = _scheduleManager.GetTimeUntilNextScheduleEntry(config.ScheduleEntries);
            if (timeUntilNext.HasValue && timeUntilNext.Value < TimeSpan.FromMinutes(5))
            {
                _logger.LogInformation("Next schedule in {Minutes:F1} minutes", timeUntilNext.Value.TotalMinutes);
                await Task.Delay(timeUntilNext.Value, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // We have an active schedule entry - get its playlist items
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);

        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" is empty or not found", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Collect valid file paths
        var filePaths = new List<string>();
        foreach (var audioItem in playlistItems)
        {
            var filePath = _audioProvider.GetAudioFilePath(audioItem);
            if (filePath != null)
            {
                filePaths.Add(filePath);
            }
            else
            {
                _logger.LogWarning("Skipping item with no valid path: {Name}", audioItem.Name);
            }
        }

        if (filePaths.Count == 0)
        {
            _logger.LogWarning("No valid audio files in playlist \"{Name}\"", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Starting stream for \"{Name}\" ({Day} {Start}-{End}) with {FileCount} files",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime, filePaths.Count);

        // Stream with schedule monitoring for overlap detection
        await StreamWithScheduleMonitoring(
            filePaths,
            activeEntry,
            config,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a playlist to Icecast while monitoring for schedule changes.
    /// If a new schedule entry starts (overlap), cancels the current stream.
    /// If the current schedule slot ends, cancels the stream.
    /// When the playlist finishes naturally, stops streaming and returns.
    /// </summary>
    private async Task StreamWithScheduleMonitoring(
        List<string> filePaths,
        ScheduleEntry activeEntry,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var streamTask = _icecastService.StreamPlaylistAsync(
            filePaths,
            config.IcecastUrl,
            config.IcecastUsername,
            config.IcecastPassword,
            config.IcecastMountPoint,
            config.AudioFormat,
            config.AudioBitrate,
            config.StreamName,
            config.StreamGenre,
            linkedCts.Token);

        // Monitor for schedule changes while streaming
        while (!streamTask.IsCompleted)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Main cancellation token fired - stop monitoring
                linkedCts.Cancel();
                break;
            }

            // Check if plugin was disabled
            var currentConfig = Plugin.Instance?.Configuration as PluginConfiguration;
            if (currentConfig == null || !currentConfig.IsEnabled)
            {
                _logger.LogInformation("Plugin disabled during streaming, stopping immediately");
                linkedCts.Cancel();
                break;
            }

            // Check current schedule status
            var currentEntry = _scheduleManager.GetActiveScheduleEntry(currentConfig.ScheduleEntries);

            if (currentEntry == null)
            {
                // Current schedule slot ended - stop streaming
                _logger.LogInformation("Schedule slot ended, stopping stream for \"{Name}\"", activeEntry.DisplayName);
                linkedCts.Cancel();
                break;
            }

            // Check if a different (later) schedule has taken over (overlap)
            if (currentEntry.PlaylistId != activeEntry.PlaylistId ||
                currentEntry.StartTime != activeEntry.StartTime)
            {
                _logger.LogInformation(
                    "Schedule overlap: \"{NewName}\" ({NewStart}-{NewEnd}) takes priority over \"{OldName}\" ({OldStart}-{OldEnd})",
                    currentEntry.DisplayName, currentEntry.StartTime, currentEntry.EndTime,
                    activeEntry.DisplayName, activeEntry.StartTime, activeEntry.EndTime);
                linkedCts.Cancel();
                break;
            }
        }

        // Wait for the stream task to complete (it may already be done)
        try
        {
            await streamTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Stream task ended: {Message}", ex.Message);
        }

        _logger.LogInformation("Stream cycle completed for \"{Name}\"", activeEntry.DisplayName);
    }

    /// <summary>
    /// Validates that the plugin configuration is complete and correct.
    /// </summary>
    private bool ValidateConfig(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.IcecastUrl))
        {
            _logger.LogWarning("Icecast URL is not configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.IcecastPassword))
        {
            _logger.LogWarning("Icecast password is not configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.IcecastMountPoint))
        {
            _logger.LogWarning("Icecast mount point is not configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.JellyfinUserId))
        {
            _logger.LogWarning("Jellyfin user ID is not configured");
            return false;
        }

        if (!config.AudioFormat.Equals("m4a", StringComparison.OrdinalIgnoreCase) &&
            !config.AudioFormat.Equals("ogg", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid audio format: {Format}. Use 'm4a' or 'ogg'", config.AudioFormat);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stops the streaming service gracefully.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Radio Online streaming service");
        _icecastService.StopStreaming();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
