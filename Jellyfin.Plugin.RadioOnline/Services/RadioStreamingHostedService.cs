using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the radio streaming loop.
/// Uses FFmpeg with concat demuxer and -stream_loop -1 for continuous gapless playback.
/// When a schedule becomes active, writes a concat playlist.txt and starts FFmpeg.
/// Monitors the schedule every 5 seconds - kills and restarts FFmpeg when the schedule changes.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly IcecastStreamingService _icecastService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;

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
    /// Gets whether FFmpeg is currently streaming.
    /// </summary>
    public bool IsStreaming => _icecastService.IsStreaming;

    /// <summary>
    /// Executes the main radio streaming loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online service started (FFmpeg concat mode)");

        // Wait for Jellyfin to fully initialize
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

                // Check if plugin is enabled
                if (config == null || !config.IsEnabled)
                {
                    StopStreaming();
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Validate configuration
                if (!ValidateConfig(config))
                {
                    StopStreaming();
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Get the active schedule entry for the current time
                var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

                if (activeEntry == null || string.IsNullOrEmpty(activeEntry.PlaylistId))
                {
                    // No active schedule - stop streaming and wait
                    StopStreaming();

                    var timeUntilNext = _scheduleManager.GetTimeUntilNextScheduleEntry(config.ScheduleEntries);
                    if (timeUntilNext.HasValue && timeUntilNext.Value < TimeSpan.FromMinutes(5))
                    {
                        _logger.LogInformation("Next schedule in {Minutes:F0} min", timeUntilNext.Value.TotalMinutes);
                        await Task.Delay(timeUntilNext.Value, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // Start streaming the active playlist
                await StreamPlaylistAsync(config, activeEntry, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming cycle error, retrying in 15s");
                StopStreaming();
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

        StopStreaming();
        _logger.LogInformation("Radio Online service stopped");
    }

    /// <summary>
    /// Streams a playlist to Icecast using FFmpeg concat demuxer.
    /// Generates the concat playlist file, starts FFmpeg, and monitors for schedule changes.
    /// When the schedule changes or ends, kills FFmpeg and returns (the main loop handles the transition).
    /// </summary>
    private async Task StreamPlaylistAsync(
        PluginConfiguration config,
        ScheduleEntry activeEntry,
        CancellationToken cancellationToken)
    {
        // Get audio files from Jellyfin playlist
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);
        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" is empty or not found", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Collect valid file paths in playlist order
        var filePaths = new List<string>();
        foreach (var item in playlistItems)
        {
            var path = _audioProvider.GetAudioFilePath(item);
            if (path != null)
                filePaths.Add(path);
        }

        if (filePaths.Count == 0)
        {
            _logger.LogWarning("No valid audio files in playlist \"{Name}\"", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Streaming \"{Name}\" ({Day} {Start}-{End}) - {Count} tracks",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime, filePaths.Count);

        // Use a linked cancellation token so we can kill FFmpeg when the schedule changes
        using var scheduleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start FFmpeg concat streaming in a background task
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
            scheduleCts.Token);

        // Monitor schedule while streaming
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), cancellationToken).ConfigureAwait(false);

            // Check if FFmpeg died unexpectedly
            if (!_icecastService.IsStreaming && !scheduleCts.IsCancellationRequested)
            {
                _logger.LogWarning("FFmpeg died unexpectedly during \"{Name}\"", activeEntry.DisplayName);
                // Wait for the task to complete, then restart
                try
                {
                    await streamTask.ConfigureAwait(false);
                }
                catch { }
                return;
            }

            // Check if plugin was disabled
            var currentConfig = Plugin.Instance?.Configuration as PluginConfiguration;
            if (currentConfig == null || !currentConfig.IsEnabled)
            {
                _logger.LogInformation("Plugin disabled, stopping stream");
                scheduleCts.Cancel();
                break;
            }

            // Check if schedule is still active
            var currentEntry = _scheduleManager.GetActiveScheduleEntry(currentConfig.ScheduleEntries);

            if (currentEntry == null)
            {
                _logger.LogInformation("Schedule ended for \"{Name}\"", activeEntry.DisplayName);
                scheduleCts.Cancel();
                break;
            }

            // Check if a different playlist has priority (overlap)
            if (!string.Equals(currentEntry.PlaylistId, activeEntry.PlaylistId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Schedule overlap: \"{New}\" takes priority over \"{Old}\"",
                    currentEntry.DisplayName, activeEntry.DisplayName);
                scheduleCts.Cancel();
                break;
            }
        }

        // Wait for FFmpeg to finish after cancellation
        try
        {
            await streamTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when we cancelled
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Stream task exception: {Message}", ex.Message);
        }

        // Small delay before potentially starting a new stream
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Stops the current FFmpeg stream.
    /// </summary>
    private void StopStreaming()
    {
        if (_icecastService.IsStreaming)
        {
            _icecastService.StopStreaming();
        }
    }

    /// <summary>
    /// Validates that the plugin has all required configuration.
    /// </summary>
    private bool ValidateConfig(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.IcecastUrl)) return false;
        if (string.IsNullOrWhiteSpace(config.IcecastPassword)) return false;
        if (string.IsNullOrWhiteSpace(config.IcecastMountPoint)) return false;
        if (string.IsNullOrWhiteSpace(config.JellyfinUserId)) return false;
        return true;
    }

    /// <summary>
    /// Stops the streaming service gracefully.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        StopStreaming();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
