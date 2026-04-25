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
/// Background hosted service that manages the radio streaming loop using Liquidsoap.
/// Liquidsoap runs as a persistent process, maintaining a continuous connection to Icecast.
/// This eliminates the 1-2 second gap between tracks that occurred with FFmpeg.
///
/// How it works:
/// 1. When a schedule activates, the service starts Liquidsoap (if not already running)
///    and writes the playlist M3U file that Liquidsoap watches via inotify.
/// 2. Liquidsoap plays tracks in order with crossfade transitions (no gaps).
/// 3. Every 5 seconds, the service checks if the schedule is still active.
/// 4. If a schedule overlap is detected (later schedule starts), the service writes
///    the new playlist file and sends a "skip" command to Liquidsoap.
/// 5. When no schedule is active, the service clears the playlist and stops Liquidsoap.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly LiquidsoapStreamingService _liquidsoapService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;

    /// <summary>
    /// How often (seconds) to check schedule status during active streaming.
    /// </summary>
    private const int ScheduleCheckIntervalSeconds = 5;

    /// <summary>
    /// Delay after writing playlist before sending skip command (ms).
    /// Ensures inotify has time to fire before Liquidsoap needs to reload.
    /// </summary>
    private const int PlaylistWriteSettleMs = 200;

    /// <summary>
    /// Delay before killing Liquidsoap after playlist is cleared (seconds).
    /// Gives Liquidsoap time to finish the current track's fade-out.
    /// </summary>
    private const int GracefulStopDelaySeconds = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        LiquidsoapStreamingService liquidsoapService,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider)
    {
        _logger = logger;
        _liquidsoapService = liquidsoapService;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
    }

    /// <summary>
    /// Executes the main radio streaming loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online streaming service started (Liquidsoap mode)");

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

                if (config == null || !config.IsEnabled)
                {
                    if (_liquidsoapService.IsStreaming)
                    {
                        _logger.LogInformation("Plugin disabled, stopping Liquidsoap");
                        await StopLiquidsoapGracefullyAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!ValidateConfig(config))
                {
                    if (_liquidsoapService.IsStreaming)
                    {
                        await StopLiquidsoapGracefullyAsync(stoppingToken).ConfigureAwait(false);
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

        // Ensure clean shutdown
        if (_liquidsoapService.IsStreaming)
        {
            await StopLiquidsoapGracefullyAsync(stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Radio Online streaming service stopped");
    }

    /// <summary>
    /// Runs a single streaming cycle: checks schedule, starts/stops Liquidsoap,
    /// and manages the playlist based on the active schedule entry.
    /// </summary>
    private async Task RunStreamingCycle(PluginConfiguration config, CancellationToken cancellationToken)
    {
        // Get the active schedule entry (handles overlaps by picking latest start time)
        var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

        if (activeEntry == null || string.IsNullOrEmpty(activeEntry.PlaylistId))
        {
            // No active schedule - stop Liquidsoap if running
            if (_liquidsoapService.IsStreaming)
            {
                _logger.LogInformation("No active schedule, stopping Liquidsoap");
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
            }

            // Wait until next schedule starts
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

        // We have an active schedule - start Liquidsoap and set the playlist
        if (!_liquidsoapService.IsStreaming)
        {
            var started = await _liquidsoapService.StartStreamingAsync(config, cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                _logger.LogError("Failed to start Liquidsoap, will retry");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Get playlist items in order
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);

        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" is empty or not found", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Collect valid file paths in playlist order
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
            "Activating schedule \"{Name}\" ({Day} {Start}-{End}) with {Count} tracks via Liquidsoap",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime, filePaths.Count);

        // Write the playlist file - Liquidsoap will detect the change and start playing
        await _liquidsoapService.SetPlaylistAsync(filePaths, cancellationToken).ConfigureAwait(false);

        // Monitor the schedule while Liquidsoap plays
        await MonitorScheduleWhileStreamingAsync(activeEntry, filePaths.Count, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Monitors the schedule while Liquidsoap is streaming.
    /// Checks every few seconds for:
    /// - Plugin being disabled
    /// - Current schedule slot ending
    /// - Schedule overlap (later schedule taking priority)
    ///
    /// For overlap handling:
    /// 1. Write the new playlist file (atomic rename triggers inotify instantly)
    /// 2. Wait briefly for inotify to propagate
    /// 3. Send skip command to Liquidsoap (advances to next track from new playlist)
    /// </summary>
    private async Task MonitorScheduleWhileStreamingAsync(
        ScheduleEntry activeEntry,
        int trackCount,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), cancellationToken).ConfigureAwait(false);

            if (!_liquidsoapService.IsStreaming)
            {
                _logger.LogWarning("Liquidsoap process died unexpectedly during monitoring");
                break;
            }

            // Re-read config (may have been updated)
            var currentConfig = Plugin.Instance?.Configuration as PluginConfiguration;
            if (currentConfig == null || !currentConfig.IsEnabled)
            {
                _logger.LogInformation("Plugin disabled during streaming, stopping Liquidsoap");
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            // Check if the schedule slot is still active
            var currentEntry = _scheduleManager.GetActiveScheduleEntry(currentConfig.ScheduleEntries);

            if (currentEntry == null)
            {
                // Schedule slot ended - clear playlist and stop
                _logger.LogInformation(
                    "Schedule slot ended for \"{Name}\", stopping transmission",
                    activeEntry.DisplayName);
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            // Check for schedule overlap (different playlist taking over)
            if (!string.Equals(currentEntry.PlaylistId, activeEntry.PlaylistId, StringComparison.Ordinal) ||
                !string.Equals(currentEntry.StartTime, activeEntry.StartTime, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Schedule overlap: \"{NewName}\" ({NewStart}-{NewEnd}) takes priority over \"{OldName}\" ({OldStart}-{OldEnd})",
                    currentEntry.DisplayName, currentEntry.StartTime, currentEntry.EndTime,
                    activeEntry.DisplayName, activeEntry.StartTime, activeEntry.EndTime);

                // The new schedule will be picked up on the next cycle of RunStreamingCycle.
                // For now, just clear the playlist and stop so the new cycle can start fresh.
                // This avoids potential playlist conflicts.
                _logger.LogInformation("Clearing playlist and stopping Liquidsoap for schedule transition");
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            // Schedule is still active and unchanged - continue monitoring
            // Liquidsoap handles the actual playback, crossfade, and track transitions
        }

        _logger.LogInformation("Monitor ended for schedule \"{Name}\" ({Tracks} tracks)", activeEntry.DisplayName, trackCount);
    }

    /// <summary>
    /// Gracefully stops Liquidsoap by clearing the playlist, waiting for the current
    /// track to finish its fade-out, then killing the process.
    /// </summary>
    private async Task StopLiquidsoapGracefullyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Clear playlist so Liquidsoap stops after current track
            await _liquidsoapService.ClearPlaylistAsync(cancellationToken).ConfigureAwait(false);

            // Give Liquidsoap time to finish current track's crossfade/fade-out
            await Task.Delay(TimeSpan.FromSeconds(GracefulStopDelaySeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during graceful stop, forcing immediate stop");
        }

        _liquidsoapService.StopStreaming();
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
        if (_liquidsoapService.IsStreaming)
        {
            _liquidsoapService.StopStreaming();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
