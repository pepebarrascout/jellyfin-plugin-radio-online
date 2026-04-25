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
/// Background hosted service that manages the continuous radio streaming loop.
/// Streams audio to Icecast ONLY when a scheduled playlist is active.
/// When no schedule is active or the playlist ends, the Icecast connection is stopped.
/// When schedules overlap, the later-starting entry takes priority and interrupts the earlier one.
///
/// Architecture: Each track in the playlist is streamed as a SEPARATE FFmpeg process
/// with the -re flag (real-time pacing). Between each track, the schedule is re-evaluated.
/// This design ensures:
/// 1. Correct playback order (tracks are processed sequentially in playlist order)
/// 2. Schedule overlap detection works (check happens between tracks)
/// 3. All listeners hear synchronized audio (-re flag prevents buffer flooding)
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly IcecastStreamingService _icecastService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;

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
        _logger.LogInformation("Radio Online streaming service started (track-by-track mode with -re real-time pacing)");

        // Wait for Jellyfin to fully initialize before starting
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
    /// Runs a single streaming cycle: checks schedule, retrieves playlist files,
    /// and streams them track-by-track with schedule monitoring between each track.
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

        // We have an active schedule entry - get its playlist items in order
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
            "Starting track-by-track stream for \"{Name}\" ({Day} {Start}-{End}) with {FileCount} tracks",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime, filePaths.Count);

        // Stream each track individually, checking schedule between tracks
        await StreamTrackByTrack(
            filePaths,
            activeEntry,
            config,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a playlist to Icecast one track at a time, with schedule verification
    /// between each track. This approach solves three critical problems:
    ///
    /// 1. PLAYBACK ORDER: Each track is processed sequentially in playlist order.
    ///    The AudioProviderService preserves order via LinkedChildren, and we iterate
    ///    the file list from index 0 to the end without any reordering.
    ///
    /// 2. SCHEDULE OVERLAP: Before each track starts, we re-evaluate the active schedule.
    ///    If the current time slot has ended, or a later schedule has taken over, we stop
    ///    immediately. This makes overlap priority work correctly - the new schedule's
    ///    streaming cycle will start on the next iteration of the main loop.
    ///
    /// 3. CLIENT SYNCHRONIZATION: Each FFmpeg process uses the -re flag to pace output
    ///    in real-time. All listeners connecting during a track hear the same audio,
    ///    with only normal latency differences (1-2 seconds) like any internet radio.
    ///
    /// There is a brief silence (1-2 seconds) between tracks while FFmpeg starts up
    /// and reconnects to Icecast. This is acceptable for scheduled radio programming.
    /// </summary>
    private async Task StreamTrackByTrack(
        List<string> filePaths,
        ScheduleEntry activeEntry,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < filePaths.Count; i++)
        {
            // ── Pre-track schedule verification ──────────────────────────

            // Re-read config in case it was updated while streaming previous track
            var currentConfig = Plugin.Instance?.Configuration as PluginConfiguration;
            if (currentConfig == null || !currentConfig.IsEnabled)
            {
                _logger.LogInformation("Plugin disabled, stopping before track {Index}/{Total}", i + 1, filePaths.Count);
                break;
            }

            // Check if the schedule time slot is still active
            var currentEntry = _scheduleManager.GetActiveScheduleEntry(currentConfig.ScheduleEntries);
            if (currentEntry == null)
            {
                _logger.LogInformation(
                    "Schedule slot ended before track {Index}/{Total} - stopping transmission",
                    i + 1, filePaths.Count);
                break;
            }

            // Check if a different (later-starting) schedule has taken over due to overlap
            if (!string.Equals(currentEntry.PlaylistId, activeEntry.PlaylistId, StringComparison.Ordinal) ||
                !string.Equals(currentEntry.StartTime, activeEntry.StartTime, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Schedule overlap detected before track {Index}/{Total}: " +
                    "\"{NewName}\" ({NewStart}-{NewEnd}) takes priority over \"{OldName}\" ({OldStart}-{OldEnd})",
                    i + 1, filePaths.Count,
                    currentEntry.DisplayName, currentEntry.StartTime, currentEntry.EndTime,
                    activeEntry.DisplayName, activeEntry.StartTime, activeEntry.EndTime);
                break;
            }

            // ── Stream the track ─────────────────────────────────────────

            var fileName = Path.GetFileName(filePaths[i]);
            _logger.LogInformation(
                "Streaming track {Index}/{Total}: {FileName}",
                i + 1, filePaths.Count, fileName);

            var success = await _icecastService.StreamSingleFileAsync(
                filePaths[i],
                currentConfig.IcecastUrl,
                currentConfig.IcecastUsername,
                currentConfig.IcecastPassword,
                currentConfig.IcecastMountPoint,
                currentConfig.AudioFormat,
                currentConfig.AudioBitrate,
                currentConfig.StreamName,
                currentConfig.StreamGenre,
                cancellationToken);

            if (!success && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Track {Index}/{Total} ({FileName}) failed, continuing to next track",
                    i + 1, filePaths.Count, fileName);
                // Continue to next track instead of breaking - a single bad file
                // shouldn't stop the entire playlist
            }
        }

        _logger.LogInformation(
            "Stream cycle completed for \"{Name}\" ({Total} tracks processed)",
            activeEntry.DisplayName, filePaths.Count);
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
