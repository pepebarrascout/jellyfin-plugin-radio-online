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
/// 60 seconds before a schedule ends, starts a silence FFmpeg on the fallback mount,
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
    private const int TransitionLeadTimeSeconds = 60;

    /// <summary>
    /// Seconds to wait for FFmpeg (main or silence) to connect to Icecast.
    /// </summary>
    private const int ConnectionWaitSeconds = 15;

    /// <summary>
    /// Interval for checking schedule changes while streaming.
    /// </summary>
    private const int ScheduleCheckIntervalSeconds = 5;

    /// <summary>
    /// Maximum time the silence bridge is allowed to run without a main stream.
    /// Safety timeout to prevent orphan silence processes.
    /// </summary>
    private const int SilenceMaxRunSeconds = 300;

    /// <summary>
    /// Tracks when the silence bridge was started, for safety timeout.
    /// </summary>
    private DateTime _silenceStartedAt = DateTime.MinValue;

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
        _logger.LogInformation("Radio Online service started (FFmpeg concat mode with silence bridge, {LeadTime}s lead)", TransitionLeadTimeSeconds);

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

                // Safety: kill silence if it has been running too long without a main stream
                CheckSilenceTimeout();

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

                    // Wait for the new FFmpeg to connect and start streaming data to Icecast
                    // Icecast needs time to detect the main source is back and move listeners from fallback
                    _logger.LogInformation("Waiting {Seconds}s for new stream to connect and stabilize...", ConnectionWaitSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(ConnectionWaitSeconds), stoppingToken).ConfigureAwait(false);

                    // Only stop silence if the main stream is confirmed running
                    // This prevents orphan silence processes and ensures Icecast has moved listeners back
                    if (_icecastService.IsStreaming)
                    {
                        _logger.LogInformation("Main stream confirmed running, stopping silence bridge");
                        StopSilenceBridge();
                        _logger.LogInformation("Transition complete: silence bridge stopped");
                    }
                    else
                    {
                        _logger.LogWarning("Main stream not running after {Seconds}s wait, keeping silence bridge active", ConnectionWaitSeconds);
                    }

                    continue;
                }

                // Schedule has very little time left and no silence started yet
                if (remaining <= TimeSpan.FromSeconds(TransitionLeadTimeSeconds))
                {
                    _logger.LogInformation(
                        "Schedule \"{Name}\" has only {Seconds:F0}s remaining, starting silence bridge and waiting",
                        activeEntry.DisplayName, remaining.TotalSeconds);

                    StartSilenceBridge(config);

                    // Wait for the schedule to end so the next iteration picks up the new schedule
                    if (remaining > TimeSpan.FromSeconds(3))
                    {
                        var waitTime = remaining - TimeSpan.FromSeconds(3);
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
    /// 60 seconds before the schedule ends, starts the silence bridge to allow seamless transition.
    /// When the schedule ends or changes, kills FFmpeg and returns.
    /// Supports shuffle mode: if the schedule entry has ShufflePlayback enabled,
    /// the track order is randomized each time the playlist starts.
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

        // Shuffle playback if enabled
        if (activeEntry.ShufflePlayback)
        {
            ShuffleList(filePaths);
            _logger.LogInformation("Shuffle enabled for \"{Name}\" - randomized {Count} tracks", activeEntry.DisplayName, filePaths.Count);
        }

        _logger.LogInformation(
            "Streaming \"{Name}\" ({Day} {Start}-{End}) - {Count} tracks{Shuffle}",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime,
            filePaths.Count, activeEntry.ShufflePlayback ? " [SHUFFLE]" : "");

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
                StopSilenceBridge();
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

            // Start silence bridge before schedule ends (60s lead time)
            if (remaining <= TimeSpan.FromSeconds(TransitionLeadTimeSeconds) && !silenceStarted)
            {
                silenceStarted = true;
                _logger.LogInformation(
                    "Schedule \"{Name}\" ending in {Seconds:F0}s, starting silence bridge",
                    activeEntry.DisplayName, remaining.TotalSeconds);

                StartSilenceBridge(currentConfig);
            }

            // Check if schedule ended
            var currentEntry = _scheduleManager.GetActiveScheduleEntry(currentConfig.ScheduleEntries);

            if (currentEntry == null)
            {
                _logger.LogInformation("Schedule ended for \"{Name}\"", activeEntry.DisplayName);

                // Ensure silence is running before killing main stream
                if (!silenceStarted)
                {
                    silenceStarted = true;
                    _logger.LogInformation("Schedule ended without silence bridge - starting emergency silence");
                    StartSilenceBridge(currentConfig);
                    await Task.Delay(TimeSpan.FromSeconds(ConnectionWaitSeconds), cancellationToken).ConfigureAwait(false);
                }

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

                    StartSilenceBridge(currentConfig);
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
    /// Shuffles a list in-place using Fisher-Yates algorithm.
    /// </summary>
    private void ShuffleList(List<string> list)
    {
        var n = list.Count;
        for (var i = n - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Starts the silence bridge with logging and timestamp tracking.
    /// </summary>
    private void StartSilenceBridge(PluginConfiguration config)
    {
        if (_icecastService.IsSilenceStreaming)
        {
            _logger.LogDebug("Silence bridge already running, skipping start");
            return;
        }

        _logger.LogInformation("Starting silence bridge on {Mount}-silence...", config.IcecastMountPoint.TrimEnd('/'));
        _icecastService.StartSilence(
            config.IcecastUrl,
            config.IcecastUsername,
            config.IcecastPassword,
            config.IcecastMountPoint,
            config.AudioFormat,
            config.AudioBitrate);

        _silenceStartedAt = DateTime.UtcNow;

        _logger.LogInformation("Waiting {Seconds}s for silence bridge to connect...", ConnectionWaitSeconds);
    }

    /// <summary>
    /// Safety check: stops silence bridge if it has been running too long.
    /// </summary>
    private void CheckSilenceTimeout()
    {
        if (!_icecastService.IsSilenceStreaming) return;

        var elapsed = (DateTime.UtcNow - _silenceStartedAt).TotalSeconds;
        if (elapsed > SilenceMaxRunSeconds)
        {
            _logger.LogWarning(
                "Silence bridge has been running for {Elapsed:F0}s (max {Max}s), forcing stop",
                elapsed, SilenceMaxRunSeconds);
            StopSilenceBridge();
        }
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
        _silenceStartedAt = DateTime.MinValue;
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
