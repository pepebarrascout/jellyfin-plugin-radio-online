using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the radio streaming loop.
/// Supports two streaming engines:
/// - FFmpeg (default): Uses Jellyfin's bundled FFmpeg. Works inside Docker containers.
/// - Liquidsoap: Requires Liquidsoap installed INSIDE the Jellyfin container.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly IcecastStreamingService _icecastService;
    private readonly LiquidsoapStreamingService _liquidsoapService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;

    private const int ScheduleCheckIntervalSeconds = 5;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        IcecastStreamingService icecastService,
        LiquidsoapStreamingService liquidsoapService,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider)
    {
        _logger = logger;
        _icecastService = icecastService;
        _liquidsoapService = liquidsoapService;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
    }

    /// <summary>
    /// Gets whether any streaming engine is currently active.
    /// </summary>
    public bool IsStreaming =>
        _icecastService.IsStreaming || _liquidsoapService.IsStreaming;

    /// <summary>
    /// Gets the name of the currently active streaming engine, or null.
    /// </summary>
    public string? ActiveEngine =>
        _liquidsoapService.IsStreaming ? "liquidsoap" :
        _icecastService.IsStreaming ? "ffmpeg" : null;

    /// <summary>
    /// Executes the main radio streaming loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online service started");

        // Wait for Jellyfin to fully initialize before accessing plugins/config
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
                    StopAllStreaming();
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Validate configuration
                if (!ValidateConfig(config))
                {
                    StopAllStreaming();
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Determine active schedule entry
                var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

                if (activeEntry == null || string.IsNullOrEmpty(activeEntry.PlaylistId))
                {
                    StopAllStreaming();

                    var timeUntilNext = _scheduleManager.GetTimeUntilNextScheduleEntry(config.ScheduleEntries);
                    if (timeUntilNext.HasValue && timeUntilNext.Value < TimeSpan.FromMinutes(5))
                    {
                        _logger.LogInformation("Next schedule in {Minutes:F0} min, waiting", timeUntilNext.Value.TotalMinutes);
                        await Task.Delay(timeUntilNext.Value, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // Choose streaming engine based on configuration
                var engine = config.StreamingEngine?.Trim().ToLowerInvariant() ?? "ffmpeg";

                if (engine == "liquidsoap")
                {
                    await RunLiquidsoapCycleAsync(config, activeEntry, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    // Default: FFmpeg (works inside Docker containers)
                    await RunFFmpegCycleAsync(config, activeEntry, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming cycle error, retrying in 15s");
                StopAllStreaming();
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

        StopAllStreaming();
        _logger.LogInformation("Radio Online service stopped");
    }

    /// <summary>
    /// Runs the FFmpeg streaming cycle: streams tracks one-by-one with schedule checks between tracks.
    /// This engine uses Jellyfin's bundled FFmpeg and works inside Docker containers.
    /// There is a brief pause (~1-2s) between tracks due to FFmpeg reconnection.
    /// </summary>
    private async Task RunFFmpegCycleAsync(
        PluginConfiguration config,
        ScheduleEntry activeEntry,
        CancellationToken cancellationToken)
    {
        // Make sure Liquidsoap is not running
        if (_liquidsoapService.IsStreaming)
        {
            _liquidsoapService.StopStreaming();
        }

        // Get playlist items
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);
        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" empty or not found", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Collect valid file paths
        var filePaths = new List<string>();
        foreach (var item in playlistItems)
        {
            var path = _audioProvider.GetAudioFilePath(item);
            if (path != null) filePaths.Add(path);
        }

        if (filePaths.Count == 0)
        {
            _logger.LogWarning("No valid audio files in playlist \"{Name}\"", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Streaming \"{Name}\" ({Day} {Start}-{End}) via FFmpeg - {Count} tracks",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime, filePaths.Count);

        // Stream each track sequentially
        for (int i = 0; i < filePaths.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Check schedule still active before each track
            var currentEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

            if (currentEntry == null)
            {
                _logger.LogInformation("Schedule ended during playback of \"{Name}\"", activeEntry.DisplayName);
                break;
            }

            // Check if a different playlist has priority (overlap)
            if (!string.Equals(currentEntry.PlaylistId, activeEntry.PlaylistId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Schedule overlap: \"{New}\" takes priority over \"{Old}\"",
                    currentEntry.DisplayName, activeEntry.DisplayName);
                break;
            }

            var filePath = filePaths[i];
            var fileName = Path.GetFileName(filePath);

            _logger.LogInformation(
                "Track {Index}/{Total}: {FileName}",
                i + 1, filePaths.Count, fileName);

            var success = await _icecastService.StreamSingleFileAsync(
                filePath,
                config.IcecastUrl,
                config.IcecastUsername,
                config.IcecastPassword,
                config.IcecastMountPoint,
                config.AudioFormat,
                config.AudioBitrate,
                config.StreamName,
                config.StreamGenre,
                cancellationToken).ConfigureAwait(false);

            if (!success && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("FFmpeg stream failed for {FileName}, skipping", fileName);
            }
        }

        _logger.LogInformation("FFmpeg cycle completed for \"{Name}\"", activeEntry.DisplayName);
    }

    /// <summary>
    /// Runs the Liquidsoap streaming cycle: starts a persistent Liquidsoap process
    /// and monitors for schedule changes. Requires Liquidsoap installed inside the container.
    /// </summary>
    private async Task RunLiquidsoapCycleAsync(
        PluginConfiguration config,
        ScheduleEntry activeEntry,
        CancellationToken cancellationToken)
    {
        // Make sure FFmpeg is not running
        if (_icecastService.IsStreaming)
        {
            _icecastService.StopStreaming();
        }

        // Start Liquidsoap if not already running
        if (!_liquidsoapService.IsStreaming)
        {
            var started = await _liquidsoapService.StartStreamingAsync(config, cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                _logger.LogError(
                    "Liquidsoap failed to start. If Jellyfin runs in Docker, " +
                    "install Liquidsoap inside the container or switch engine to FFmpeg in plugin config.");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Get playlist files
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);
        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" empty or not found", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        var filePaths = new List<string>();
        foreach (var item in playlistItems)
        {
            var path = _audioProvider.GetAudioFilePath(item);
            if (path != null) filePaths.Add(path);
        }

        if (filePaths.Count == 0)
        {
            _logger.LogWarning("No valid audio files in playlist \"{Name}\"", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Streaming \"{Name}\" ({Day} {Start}-{End}) via Liquidsoap - {Count} tracks",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime, filePaths.Count);

        await _liquidsoapService.SetPlaylistAsync(filePaths, cancellationToken).ConfigureAwait(false);

        // Monitor for schedule changes
        await MonitorScheduleAsync(activeEntry, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Monitors the schedule while Liquidsoap is streaming.
    /// Stops Liquidsoap when the schedule ends or a different playlist takes priority.
    /// </summary>
    private async Task MonitorScheduleAsync(
        ScheduleEntry activeEntry,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), cancellationToken).ConfigureAwait(false);

            if (!_liquidsoapService.IsStreaming)
            {
                _logger.LogWarning("Liquidsoap process died during \"{Name}\"", activeEntry.DisplayName);
                break;
            }

            var currentConfig = Plugin.Instance?.Configuration as PluginConfiguration;
            if (currentConfig == null || !currentConfig.IsEnabled)
            {
                _logger.LogInformation("Plugin disabled, stopping Liquidsoap");
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            var currentEntry = _scheduleManager.GetActiveScheduleEntry(currentConfig.ScheduleEntries);

            if (currentEntry == null)
            {
                _logger.LogInformation("Schedule ended for \"{Name}\"", activeEntry.DisplayName);
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            if (!string.Equals(currentEntry.PlaylistId, activeEntry.PlaylistId, StringComparison.Ordinal) ||
                !string.Equals(currentEntry.StartTime, activeEntry.StartTime, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Schedule overlap: \"{New}\" takes priority over \"{Old}\"",
                    currentEntry.DisplayName, activeEntry.DisplayName);
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
        }
    }

    /// <summary>
    /// Gracefully stops Liquidsoap: clears playlist, waits, then kills the process.
    /// </summary>
    private async Task StopLiquidsoapGracefullyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _liquidsoapService.ClearPlaylistAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful Liquidsoap stop failed, forcing");
        }

        _liquidsoapService.StopStreaming();
    }

    /// <summary>
    /// Stops all streaming engines.
    /// </summary>
    private void StopAllStreaming()
    {
        if (_icecastService.IsStreaming)
        {
            _icecastService.StopStreaming();
        }

        if (_liquidsoapService.IsStreaming)
        {
            _liquidsoapService.StopStreaming();
        }
    }

    private bool ValidateConfig(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.IcecastUrl))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.IcecastPassword))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.IcecastMountPoint))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.JellyfinUserId))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Stops the streaming service gracefully.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        StopAllStreaming();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
