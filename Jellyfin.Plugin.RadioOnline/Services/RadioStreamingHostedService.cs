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
/// Uses a silence bridge (fallback mount) during transitions to prevent listener disconnection:
/// 15 seconds before a schedule ends, starts a silence FFmpeg on the fallback mount,
/// then kills the main FFmpeg. Listeners are seamlessly moved to the silence mount by Icecast.
/// When the new schedule starts, the new FFmpeg connects and the silence process is stopped.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly IcecastStreamingService _icecastService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;

    /// <summary>
    /// Seconds before schedule ends to start the silence bridge.
    /// </summary>
    private const int TransitionLeadTimeSeconds = 15;

    /// <summary>
    /// Seconds to wait for FFmpeg (main or silence) to connect to Icecast.
    /// </summary>
    private const int ConnectionWaitSeconds = 5;

    /// <summary>
    /// Interval for checking schedule changes while streaming.
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
    /// Gets whether FFmpeg is currently streaming.
    /// </summary>
    public bool IsStreaming => _icecastService.IsStreaming;

    /// <summary>
    /// Executes the main radio streaming loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online service started (FFmpeg concat mode with silence bridge)");

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
                    StopSilenceBridge();
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Validate configuration
                if (!ValidateConfig(config))
                {
                    StopStreaming();
                    StopSilenceBridge();
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Get the active schedule entry for the current time
                var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

                if (activeEntry == null || string.IsNullOrEmpty(activeEntry.PlaylistId))
                {
                    // No active schedule - handle silence bridge state
                    if (_icecastService.IsSilenceStreaming)
                    {
                        _logger.LogInformation("Silence bridge active, waiting for next schedule...");
                        var timeUntilNext = _scheduleManager.GetTimeUntilNextScheduleEntry(config.ScheduleEntries);
                        if (timeUntilNext.HasValue && timeUntilNext.Value < TimeSpan.FromMinutes(5))
                        {
                            _logger.LogInformation("Next schedule in {Seconds:F0}s, waiting", timeUntilNext.Value.TotalSeconds);
                            await Task.Delay(timeUntilNext.Value, stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Safety: no schedule coming soon, stop silence to save resources
                            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                            StopSilenceBridge();
                        }

                        continue;
                    }

                    // Normal idle: stop streaming and wait
                    StopStreaming();
                    var timeUntil = _scheduleManager.GetTimeUntilNextScheduleEntry(config.ScheduleEntries);
                    if (timeUntil.HasValue && timeUntil.Value < TimeSpan.FromMinutes(5))
                    {
                        _logger.LogInformation("Next schedule in {Minutes:F0} min", timeUntil.Value.TotalMinutes);
                        await Task.Delay(timeUntil.Value, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // Check remaining time in current schedule slot
                var remaining = _scheduleManager.GetRemainingTimeInSlot(activeEntry);

                // If silence bridge is running (transition from previous schedule), start new stream
                if (_icecastService.IsSilenceStreaming)
                {
                    _logger.LogInformation(
                        "Starting new stream \"{Name}\" while silence bridge is active",
                        activeEntry.DisplayName);

                    await StreamPlaylistAsync(config, activeEntry, stoppingToken).ConfigureAwait(false);

                    // Wait for the new FFmpeg to connect to Icecast
                    _logger.LogInformation("Waiting {Seconds}s for new stream to connect...", ConnectionWaitSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(ConnectionWaitSeconds), stoppingToken).ConfigureAwait(false);

                    // Stop silence bridge - Icecast will move listeners back to the main mount
                    StopSilenceBridge();

                    _logger.LogInformation("Transition complete: silence bridge stopped");
                    continue;
                }

                // Schedule has very little time left and no silence started yet
                if (remaining <= TimeSpan.FromSeconds(TransitionLeadTimeSeconds))
                {
                    _logger.LogInformation(
                        "Schedule \"{Name}\" has only {Seconds:F0}s remaining, starting silence bridge and waiting",
                        activeEntry.DisplayName, remaining.TotalSeconds);

                    _icecastService.StartSilence(
                        config.IcecastUrl,
                        config.IcecastUsername,
                        config.IcecastPassword,
                        config.IcecastMountPoint,
                        config.AudioFormat,
                        config.AudioBitrate);

                    await Task.Delay(TimeSpan.FromSeconds(ConnectionWaitSeconds), stoppingToken).ConfigureAwait(false);

                    if (!_icecastService.IsSilenceStreaming)
                    {
                        _logger.LogWarning("Silence bridge failed to connect, listeners may experience a cut");
                    }

                    // Wait for the schedule to end so the next iteration picks up the new schedule
                    if (remaining > TimeSpan.FromSeconds(2))
                    {
                        var waitTime = remaining - TimeSpan.FromSeconds(2);
                        _logger.LogInformation("Waiting {Seconds:F0}s for schedule to end...", waitTime.TotalSeconds);
                        await Task.Delay(waitTime, stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // Normal flow: start streaming the active playlist
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
                StopSilenceBridge();
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
        StopSilenceBridge();
        _logger.LogInformation("Radio Online service stopped");
    }

    /// <summary>
    /// Streams a playlist to Icecast using FFmpeg concat demuxer.
    /// Generates the concat playlist file, starts FFmpeg, and monitors for schedule changes.
    /// 15 seconds before the schedule ends, starts the silence bridge to allow seamless transition.
    /// When the schedule ends or changes, kills FFmpeg and returns.
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

        // Track whether silence bridge has been started for this transition
        bool silenceStarted = false;

        // Monitor schedule while streaming
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), cancellationToken).ConfigureAwait(false);

            // Check if FFmpeg died unexpectedly
            if (!_icecastService.IsStreaming && !scheduleCts.IsCancellationRequested)
            {
                _logger.LogWarning("FFmpeg died unexpectedly during \"{Name}\"", activeEntry.DisplayName);
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

            // Check remaining time in schedule
            var remaining = _scheduleManager.GetRemainingTimeInSlot(activeEntry);

            // Start silence bridge 15 seconds before schedule ends
            if (remaining <= TimeSpan.FromSeconds(TransitionLeadTimeSeconds) && !silenceStarted)
            {
                silenceStarted = true;
                _logger.LogInformation(
                    "Schedule \"{Name}\" ending in {Seconds:F0}s, starting silence bridge",
                    activeEntry.DisplayName, remaining.TotalSeconds);

                _icecastService.StartSilence(
                    currentConfig.IcecastUrl,
                    currentConfig.IcecastUsername,
                    currentConfig.IcecastPassword,
                    currentConfig.IcecastMountPoint,
                    currentConfig.AudioFormat,
                    currentConfig.AudioBitrate);

                // Wait for silence FFmpeg to connect to Icecast
                _logger.LogInformation("Waiting {Seconds}s for silence bridge to connect...", ConnectionWaitSeconds);
                await Task.Delay(TimeSpan.FromSeconds(ConnectionWaitSeconds), cancellationToken).ConfigureAwait(false);

                if (!_icecastService.IsSilenceStreaming)
                {
                    _logger.LogWarning("Silence bridge failed to connect - listeners may experience a cut");
                }
                else
                {
                    _logger.LogInformation("Silence bridge connected successfully on {Mount}",
                        currentConfig.IcecastMountPoint.TrimEnd('/') + "-silence");
                }
            }

            // Check if schedule ended
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
                // Start silence bridge if not already started for this overlap
                if (!silenceStarted)
                {
                    silenceStarted = true;
                    _logger.LogInformation(
                        "Schedule overlap: starting silence bridge before switching to \"{New}\"",
                        currentEntry.DisplayName);

                    _icecastService.StartSilence(
                        currentConfig.IcecastUrl,
                        currentConfig.IcecastUsername,
                        currentConfig.IcecastPassword,
                        currentConfig.IcecastMountPoint,
                        currentConfig.AudioFormat,
                        currentConfig.AudioBitrate);

                    await Task.Delay(TimeSpan.FromSeconds(ConnectionWaitSeconds), cancellationToken).ConfigureAwait(false);
                }

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
    /// Stops the silence bridge if it is running.
    /// </summary>
    private void StopSilenceBridge()
    {
        if (_icecastService.IsSilenceStreaming)
        {
            _icecastService.StopSilence();
            _logger.LogInformation("Silence bridge stopped");
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
        StopSilenceBridge();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
