using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the radio streaming loop using Liquidsoap.
/// Monitors the weekly schedule and sends tracks to Liquidsoap's queue via Telnet.
/// Tracks are sent one-by-one with a 1-track buffer for accurate playback reporting.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly LiquidsoapClient _liquidsoapClient;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;
    private readonly RadioStateService _state;
    private readonly IUserManager _userManager;
    private readonly RadioPlaybackSessionService _playbackSession;

    /// <summary>
    /// Interval for checking schedule changes while streaming.
    /// </summary>
    private const int ScheduleCheckIntervalSeconds = 5;

    /// <summary>
    /// Interval for checking schedule changes when near the end of a slot.
    /// Checks every second for precise schedule ending.
    /// </summary>
    private const int ScheduleCheckNearEndIntervalSeconds = 1;

    /// <summary>
    /// When remaining time is less than this threshold, switch to near-end polling.
    /// </summary>
    private const int NearEndThresholdSeconds = 30;

    /// <summary>
    /// Crossfade duration in seconds (must match radio.liq).
    /// </summary>
    private const int CrossfadeSeconds = 3;

    /// <summary>
    /// Margin before track end to advance to next track (seconds).
    /// </summary>
    private const int TrackAdvanceMarginSeconds = 5;

    /// <summary>
    /// Default duration for tracks without metadata (3 minutes).
    /// </summary>
    private static readonly TimeSpan DefaultTrackDuration = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Tracks the currently active schedule slot to detect changes.
    /// </summary>
    private string? _currentScheduleKey;

    /// <summary>
    /// Tracks whether the plugin was previously enabled, to detect state changes.
    /// </summary>
    private bool _wasEnabled;

    /// <summary>
    /// Tracks whether we already warned about Liquidsoap being unreachable (avoids log spam).
    /// </summary>
    private bool _warnedLiquidsoapDown;

    /// <summary>
    /// Stores the playlist tracks with metadata for sequential playback reporting.
    /// Tracks are sent one at a time with a 1-track buffer.
    /// </summary>
    private record struct QueuedTrack(string LiquidsoapPath, Audio AudioItem, TimeSpan Duration);

    private QueuedTrack[] _playlistTracks = Array.Empty<QueuedTrack>();

    /// <summary>
    /// Index of the currently playing track in _playlistTracks.
    /// -1 means no track is being tracked.
    /// </summary>
    private int _currentTrackIndex = -1;

    /// <summary>
    /// Index of the last track successfully queued in Liquidsoap.
    /// Used to ensure we only advance to tracks that are confirmed in the queue.
    /// </summary>
    private int _lastQueuedTrackIndex = -1;

    /// <summary>
    /// Index of the last track that was reported to Jellyfin.
    /// Prevents double-reporting when advance is called multiple times for the same track.
    /// </summary>
    private int _lastReportedTrackIndex = -1;

    /// <summary>
    /// When the current track started playing (approximately).
    /// </summary>
    private DateTime _currentTrackStartedAt;

    /// <summary>
    /// Whether the sequential track reporting system is active.
    /// </summary>
    private bool _trackReportingActive;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        LiquidsoapClient liquidsoapClient,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider,
        RadioStateService state,
        IUserManager userManager,
        RadioPlaybackSessionService playbackSession)
    {
        _logger = logger;
        _liquidsoapClient = liquidsoapClient;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
        _state = state;
        _userManager = userManager;
        _playbackSession = playbackSession;
    }

    /// <summary>
    /// Executes the main radio scheduling loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online service started (Liquidsoap mode) — v0.0.0.31 — PlayCount via ISessionManager only (no manual UserData)");

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

                // ── Plugin disabled: do nothing, don't touch Liquidsoap ──
                if (config == null || !config.IsEnabled)
                {
                    if (_wasEnabled)
                    {
                        _wasEnabled = false;
                        _warnedLiquidsoapDown = false;
                        _logger.LogInformation("Radio automatizada desactivada - pausando servicio");

                        await RetryClearQueueAsync().ConfigureAwait(false);
                        _liquidsoapClient.Disconnect();
                        await _playbackSession.EndSessionAsync().ConfigureAwait(false);
                    }

                    _state.IsStreaming = false;
                    _currentScheduleKey = null;
                    ResetTrackingState();

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // ── Plugin enabled ──

                if (!_wasEnabled)
                {
                    _wasEnabled = true;
                    _warnedLiquidsoapDown = false;
                    _logger.LogInformation("Radio automatizada activada - iniciando servicio");
                }

                // Validate configuration
                if (!ValidateConfig(config))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Update Liquidsoap connection settings if host/port changed
                _liquidsoapClient.UpdateConnection(config.LiquidsoapHost, config.LiquidsoapPort);

                // Check Liquidsoap connectivity
                if (!_liquidsoapClient.IsConnected)
                {
                    var connected = await _liquidsoapClient.TestConnectionAsync().ConfigureAwait(false);

                    if (!connected)
                    {
                        if (!_warnedLiquidsoapDown)
                        {
                            _logger.LogWarning("Liquidsoap no disponible en {Host}:{Port} - se reintentara en cada ciclo",
                                config.LiquidsoapHost, config.LiquidsoapPort);
                            _warnedLiquidsoapDown = true;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_warnedLiquidsoapDown)
                    {
                        _logger.LogInformation("Liquidsoap reconectado en {Host}:{Port}", config.LiquidsoapHost, config.LiquidsoapPort);
                        _warnedLiquidsoapDown = false;
                    }
                }

                // Get the active schedule entry for the current time
                var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

                if (activeEntry == null || string.IsNullOrEmpty(activeEntry.PlaylistId))
                {
                    // No active schedule - clear queue and skip current track if we were streaming
                    if (_state.IsStreaming)
                    {
                        _logger.LogInformation("No active schedule, clearing Liquidsoap queue and skipping current track");
                        await StopCurrentStreamingAsync(stoppingToken).ConfigureAwait(false);
                    }

                    ResetTrackingState();

                    // Wait until next schedule
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

                // Build a unique key for this schedule slot (day + times + playlist)
                var scheduleKey = BuildScheduleKey(activeEntry);

                // Check if the schedule has changed
                if (!string.Equals(_currentScheduleKey, scheduleKey, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Schedule change: [{OldKey}] -> [{NewKey}] (\"{Name}\")",
                        _currentScheduleKey ?? "(none)",
                        scheduleKey,
                        activeEntry.DisplayName);

                    await LoadPlaylistToLiquidsoapAsync(config, activeEntry, scheduleKey, stoppingToken).ConfigureAwait(false);
                }
                else if (_state.IsStreaming)
                {
                    // Same schedule still active - check remaining time in slot
                    var remaining = _scheduleManager.GetRemainingTimeInSlot(activeEntry);
                    if (remaining <= TimeSpan.Zero)
                    {
                        _logger.LogInformation("Schedule time slot ended, clearing queue and skipping current track");
                        await StopCurrentStreamingAsync(stoppingToken).ConfigureAwait(false);

                        // If there's a next schedule starting now, immediately process it
                        var nextEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);
                        if (nextEntry != null && !string.IsNullOrEmpty(nextEntry.PlaylistId))
                        {
                            var nextKey = BuildScheduleKey(nextEntry);
                            _logger.LogInformation("Next schedule immediately active: \"{Name}\"", nextEntry.DisplayName);
                            await LoadPlaylistToLiquidsoapAsync(config, nextEntry, nextKey, stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var timeUntil = _scheduleManager.GetTimeUntilNextScheduleEntry(config.ScheduleEntries);
                            if (timeUntil.HasValue && timeUntil.Value < TimeSpan.FromMinutes(5))
                            {
                                _logger.LogInformation("Next schedule in {Minutes:F0} min", timeUntil.Value.TotalMinutes);
                                await Task.Delay(timeUntil.Value, stoppingToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), stoppingToken).ConfigureAwait(false);
                            }
                        }
                        continue;
                    }

                    // Advance to next track if current is about to end (always, regardless of reporting)
                    if (_trackReportingActive)
                    {
                        try
                        {
                            await AdvanceTrackAsync(config).ConfigureAwait(false);
                        }
                        catch (Exception advanceEx)
                        {
                            _logger.LogWarning(advanceEx, "Track advance error, will retry next cycle");
                        }
                    }

                    // Determine poll interval
                    var pollInterval = remaining < TimeSpan.FromSeconds(NearEndThresholdSeconds)
                        ? TimeSpan.FromSeconds(ScheduleCheckNearEndIntervalSeconds)
                        : TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds);

                    // Also poll faster when near track end for precise advance
                    if (_trackReportingActive && _currentTrackIndex >= 0 && _currentTrackIndex < _playlistTracks.Length)
                    {
                        var currentDuration = _playlistTracks[_currentTrackIndex].Duration;
                        var elapsed = DateTime.UtcNow - _currentTrackStartedAt;
                        var trackRemaining = currentDuration - elapsed;
                        if (trackRemaining < TimeSpan.FromSeconds(10) && trackRemaining > TimeSpan.Zero)
                        {
                            pollInterval = TimeSpan.FromSeconds(1);
                        }
                    }

                    await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming cycle error, retrying in 15s");

                try
                {
                    await RetryClearQueueAsync().ConfigureAwait(false);
                }
                catch (Exception clearEx)
                {
                    _logger.LogWarning(clearEx, "Failed to clear queue after cycle error");
                }

                _state.IsStreaming = false;
                _currentScheduleKey = null;
                ResetTrackingState();
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

        // Cleanup on shutdown
        try
        {
            if (_state.IsStreaming)
            {
                await _liquidsoapClient.ClearQueueAsync().ConfigureAwait(false);
            }
        }
        catch { }

        _liquidsoapClient.Disconnect();
        await _playbackSession.EndSessionAsync().ConfigureAwait(false);
        _state.IsStreaming = false;
        _currentScheduleKey = null;
        _logger.LogInformation("Radio Online service stopped");
    }

    /// <summary>
    /// Loads a playlist's tracks into the Liquidsoap queue.
    /// Sends the first 2 tracks (1 playing + 1 buffer), remaining tracks are sent one-by-one
    /// as each track approaches its end via AdvanceTrackAsync.
    /// </summary>
    private async Task LoadPlaylistToLiquidsoapAsync(
        PluginConfiguration config,
        ScheduleEntry activeEntry,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        // Get audio files from Jellyfin playlist
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);
        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" is empty or not found", activeEntry.DisplayName);
            _currentScheduleKey = scheduleKey;
            _state.IsStreaming = false;
            return;
        }

        // Collect valid file paths with metadata
        var filePaths = new List<string>();
        var pathToItemMap = new Dictionary<string, Audio>();
        foreach (var item in playlistItems)
        {
            var path = _audioProvider.GetAudioFilePath(item);
            if (path != null)
            {
                filePaths.Add(path);
                pathToItemMap[path] = item;
            }
        }

        if (filePaths.Count == 0)
        {
            _logger.LogWarning("No valid audio files in playlist \"{Name}\"", activeEntry.DisplayName);
            _currentScheduleKey = scheduleKey;
            _state.IsStreaming = false;
            return;
        }

        // Shuffle playback if enabled
        if (activeEntry.ShufflePlayback)
        {
            ShuffleList(filePaths);
            _logger.LogInformation("Shuffle enabled for \"{Name}\" - randomized {Count} tracks", activeEntry.DisplayName, filePaths.Count);
        }

        // Translate paths and build track list with durations
        ResetTrackingState();
        var trackList = new List<QueuedTrack>();
        foreach (var jellyfinPath in filePaths)
        {
            var liqPath = TranslatePath(jellyfinPath, config);
            if (liqPath != null && pathToItemMap.TryGetValue(jellyfinPath, out var audioItem))
            {
                var duration = audioItem.RunTimeTicks.HasValue && audioItem.RunTimeTicks.Value > 0
                    ? TimeSpan.FromTicks(audioItem.RunTimeTicks.Value)
                    : DefaultTrackDuration;

                trackList.Add(new QueuedTrack(liqPath, audioItem, duration));
            }
        }

        if (trackList.Count == 0)
        {
            _logger.LogWarning("No valid paths after translation for playlist \"{Name}\"", activeEntry.DisplayName);
            _currentScheduleKey = scheduleKey;
            _state.IsStreaming = false;
            return;
        }

        _playlistTracks = trackList.ToArray();

        // Clear queue before loading new tracks
        _logger.LogInformation(
            "Loading \"{Name}\" ({Day} {Start}-{End}) - {Count} tracks{Shuffle} to Liquidsoap",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime,
            _playlistTracks.Length, activeEntry.ShufflePlayback ? " [SHUFFLE]" : "");

        if (_state.IsStreaming)
        {
            await RetryClearQueueAsync().ConfigureAwait(false);

            try
            {
                await _liquidsoapClient.SkipAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to skip current track before playlist change");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RetryClearQueueAsync().ConfigureAwait(false);
        }

        // Send first 2 tracks (1 playing + 1 buffer)
        var initialCount = Math.Min(2, _playlistTracks.Length);
        var added = 0;
        for (var i = 0; i < initialCount; i++)
        {
            if (await _liquidsoapClient.AppendTrackAsync(_playlistTracks[i].LiquidsoapPath).ConfigureAwait(false))
                added++;
        }

        _currentScheduleKey = scheduleKey;
        _state.IsStreaming = added > 0;

        if (added > 0)
        {
            _currentTrackIndex = 0;
            _lastQueuedTrackIndex = added - 1;
            _currentTrackStartedAt = DateTime.UtcNow;
            _trackReportingActive = true;

            _logger.LogInformation(
                "Queued {Added}/{Total} initial tracks for \"{Name}\" (one-by-one mode with buffer)",
                added, _playlistTracks.Length, activeEntry.DisplayName);

            // Report first track immediately (only once)
            if (config.EnablePlaybackReporting)
            {
                _lastReportedTrackIndex = 0;
                await ReportTrackPlaybackAsync(_playlistTracks[0].AudioItem, config).ConfigureAwait(false);
            }
        }
        else
        {
            _logger.LogError("Failed to queue any tracks for \"{Name}\"", activeEntry.DisplayName);
            ResetTrackingState();
        }
    }

    /// <summary>
    /// Advances to the next track in the playlist when the current one is about to end.
    /// Key design principles:
    ///   - Index only advances when the next track is confirmed queued in Liquidsoap.
    ///   - If queuing fails, the same track is retried on the next cycle (never skipped).
    ///   - Tracks are only reported once (tracked via _lastReportedTrackIndex).
    ///   - Always maintains at least 1 buffered track ahead in the queue.
    /// </summary>
    private async Task AdvanceTrackAsync(PluginConfiguration config)
    {
        if (_currentTrackIndex < 0 || _currentTrackIndex >= _playlistTracks.Length)
            return;

        var current = _playlistTracks[_currentTrackIndex];
        var elapsed = DateTime.UtcNow - _currentTrackStartedAt;
        var trackRemaining = current.Duration - elapsed;

        // Time to advance? (current track about to end)
        // Use negative threshold to allow multiple attempts if needed
        if (trackRemaining > TimeSpan.FromSeconds(TrackAdvanceMarginSeconds))
            return;

        var nextIndex = _currentTrackIndex + 1;

        if (nextIndex >= _playlistTracks.Length)
        {
            _trackReportingActive = false;
            _logger.LogInformation("All {Count} tracks processed for current playlist", _playlistTracks.Length);
            return;
        }

        // Ensure the next track is queued in Liquidsoap BEFORE advancing the index
        // This is the key guarantee: index only moves when the track is confirmed
        if (nextIndex > _lastQueuedTrackIndex)
        {
            var nextTrack = _playlistTracks[nextIndex];
            if (await _liquidsoapClient.AppendTrackAsync(nextTrack.LiquidsoapPath).ConfigureAwait(false))
            {
                _lastQueuedTrackIndex = nextIndex;
                _logger.LogInformation("Queued track {Index}/{Total}: {Path}",
                    nextIndex + 1, _playlistTracks.Length, nextTrack.LiquidsoapPath);
            }
            else
            {
                // Failed to queue - do NOT advance, retry next cycle (never skip)
                _logger.LogWarning("Failed to queue track {Index}/{Total}, retrying next cycle",
                    nextIndex + 1, _playlistTracks.Length);
                return;
            }
        }

        // Next track is confirmed in queue — now advance the index
        _currentTrackIndex = nextIndex;
        _currentTrackStartedAt = DateTime.UtcNow;

        // Report the now-playing track (only once)
        if (config.EnablePlaybackReporting && nextIndex != _lastReportedTrackIndex)
        {
            _lastReportedTrackIndex = nextIndex;
            await ReportTrackPlaybackAsync(_playlistTracks[nextIndex].AudioItem, config).ConfigureAwait(false);
        }

        // Try to buffer one more track ahead (best-effort, non-blocking)
        var bufferIndex = nextIndex + 1;
        if (bufferIndex < _playlistTracks.Length && bufferIndex > _lastQueuedTrackIndex)
        {
            var bufferTrack = _playlistTracks[bufferIndex];
            if (await _liquidsoapClient.AppendTrackAsync(bufferTrack.LiquidsoapPath).ConfigureAwait(false))
            {
                _lastQueuedTrackIndex = bufferIndex;
                _logger.LogDebug("Buffered track {Index}/{Total}: {Path}",
                    bufferIndex + 1, _playlistTracks.Length, bufferTrack.LiquidsoapPath);
            }
            else
            {
                // Buffer failed - not critical, will retry on next advance cycle
                _logger.LogWarning("Failed to buffer track {Index}/{Total}, will retry later",
                    bufferIndex + 1, _playlistTracks.Length);
            }
        }
        else if (bufferIndex >= _playlistTracks.Length)
        {
            _logger.LogInformation("Last track ({Index}/{Total}) now playing, no more to buffer",
                _currentTrackIndex + 1, _playlistTracks.Length);
        }
    }

    /// <summary>
    /// Reports a track play to Jellyfin's playback session system.
    /// Creates a virtual session (if not yet active) and fires PlaybackStart/PlaybackStopped events.
    /// This makes the radio appear as an active client in Jellyfin's dashboard and triggers
    /// scrobbling plugins (Last.fm, ListenBrainz) to detect the playback activity.
    /// PlayCount, LastPlayedDate and scrobbling are all handled by Jellyfin's internal
    /// playback event handlers (OnPlaybackStart / OnPlaybackStopped) — no manual UserData needed.
    /// </summary>
    private async Task ReportTrackPlaybackAsync(Audio audioItem, PluginConfiguration config)
    {
        if (!Guid.TryParse(config.JellyfinUserId, out var userId))
        {
            _logger.LogWarning("Invalid user ID for playback reporting");
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            _logger.LogWarning("User not found for playback reporting: {UserId}", userId);
            return;
        }

        try
        {
            // Ensure virtual session exists (creates or reuses)
            var sessionReady = await _playbackSession.EnsureSessionAsync(user).ConfigureAwait(false);
            if (sessionReady)
            {
                // Fire PlaybackStart event (now-playing in dashboard + scrobble registration)
                // On track change, this automatically stops the previous track (PlaybackStopped),
                // which triggers Jellyfin's internal PlayCount++ for the previous track.
                await _playbackSession.ReportPlaybackStartAsync(audioItem).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Reported playback start: {Artist} - {Title}",
                audioItem.Artists != null && audioItem.Artists.Count > 0
                    ? string.Join(", ", audioItem.Artists)
                    : "Unknown Artist",
                audioItem.Name ?? "Unknown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report playback for {ItemName}", audioItem.Name);
        }
    }

    /// <summary>
    /// Resets all track tracking state.
    /// </summary>
    private void ResetTrackingState()
    {
        _playlistTracks = Array.Empty<QueuedTrack>();
        _currentTrackIndex = -1;
        _lastQueuedTrackIndex = -1;
        _lastReportedTrackIndex = -1;
        _trackReportingActive = false;
    }

    /// <summary>
    /// Builds a unique key for a schedule slot using day, start time, end time, and playlist ID.
    /// </summary>
    private static string BuildScheduleKey(ScheduleEntry entry)
    {
        return $"{entry.DayOfWeek}|{entry.StartTime}|{entry.EndTime}|{entry.PlaylistId}";
    }

    /// <summary>
    /// Stops the current streaming by clearing the Liquidsoap queue and skipping the current track.
    /// </summary>
    private async Task StopCurrentStreamingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RetryClearQueueAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear queue during stop");
        }

        try
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _liquidsoapClient.SkipAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to skip current track during stop");
        }

        _state.IsStreaming = false;
        _currentScheduleKey = null;
        ResetTrackingState();
    }

    /// <summary>
    /// Attempts to clear the Liquidsoap queue with up to 3 retries on failure.
    /// </summary>
    private async Task RetryClearQueueAsync()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var cleared = await _liquidsoapClient.ClearQueueAsync().ConfigureAwait(false);
            if (cleared)
            {
                _logger.LogDebug("Queue cleared successfully on attempt {Attempt}", attempt);
                return;
            }

            _logger.LogWarning("Queue clear failed on attempt {Attempt}/3, retrying in 2s", attempt);
            if (attempt < 3)
            {
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }

        _logger.LogError("Failed to clear Liquidsoap queue after 3 attempts");
    }

    /// <summary>
    /// Translates a Jellyfin filesystem path to the corresponding Liquidsoap path.
    /// </summary>
    private string? TranslatePath(string jellyfinPath, PluginConfiguration config)
    {
        try
        {
            var jellyfinRoot = config.JellyfinMediaPath.TrimEnd('/');
            var liquidsoapRoot = config.LiquidsoapMusicPath.TrimEnd('/');

            if (jellyfinPath.StartsWith(jellyfinRoot, StringComparison.OrdinalIgnoreCase))
            {
                return liquidsoapRoot + jellyfinPath.Substring(jellyfinRoot.Length);
            }

            _logger.LogWarning("Path does not start with media root: {Path} (root: {Root})", jellyfinPath, jellyfinRoot);
            return jellyfinPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error translating path: {Path}", jellyfinPath);
            return jellyfinPath;
        }
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
    /// Validates that the plugin has all required configuration.
    /// </summary>
    private bool ValidateConfig(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.LiquidsoapHost))
        {
            _logger.LogWarning("Liquidsoap host not configured");
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.JellyfinMediaPath))
        {
            _logger.LogWarning("Jellyfin media path not configured");
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.LiquidsoapMusicPath))
        {
            _logger.LogWarning("Liquidsoap music path not configured");
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.JellyfinUserId))
        {
            _logger.LogWarning("Jellyfin user not configured");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stops the streaming service gracefully.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_state.IsStreaming)
            {
                await _liquidsoapClient.ClearQueueAsync().ConfigureAwait(false);
            }
        }
        catch { }

        await _playbackSession.EndSessionAsync().ConfigureAwait(false);
        _liquidsoapClient.Disconnect();
        _state.IsStreaming = false;
        _currentScheduleKey = null;
        ResetTrackingState();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
