using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the radio streaming loop using Liquidsoap.
/// Sends tracks to Liquidsoap's queue via Telnet based on the weekly schedule.
///
/// Architecture (v0.0.0.37 — Liquidsoap push metadata):
///   - SENDING: This service sends tracks to Liquidsoap via Telnet (queue.append).
///   - REPORTING: Liquidsoap tells US what's playing via HTTP POST to TrackChangeController.
///   - No guessing, no sync, no resync — Liquidsoap is the single source of truth.
///   - This service only manages the buffer: keep queue.length >= 3 at all times.
///
/// On fresh start (plugin restart):
///   - Query queue.current_track to find current position in playlist
///   - Query queue.length to know what's already buffered
///   - Continue sending from where Liquidsoap is
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly LiquidsoapClient _liquidsoapClient;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;
    private readonly RadioStateService _state;
    private readonly RadioPlaybackSessionService _playbackSession;

    /// <summary>
    /// Interval for checking schedule changes while streaming.
    /// </summary>
    private const int ScheduleCheckIntervalSeconds = 5;

    /// <summary>
    /// Interval for checking schedule changes when near the end of a slot.
    /// </summary>
    private const int ScheduleCheckNearEndIntervalSeconds = 1;

    /// <summary>
    /// When remaining time is less than this threshold, switch to near-end polling.
    /// </summary>
    private const int NearEndThresholdSeconds = 30;

    /// <summary>
    /// Minimum number of tracks to keep in Liquidsoap's queue (buffer depth).
    /// When queue.length drops below this, refill immediately.
    /// </summary>
    private const int MinBufferDepth = 3;

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
    /// Stores the playlist tracks with their Liquidsoap paths.
    /// Used for buffer management — knowing which track to send next.
    /// </summary>
    private record struct QueuedTrack(string LiquidsoapPath);

    private QueuedTrack[] _playlistTracks = Array.Empty<QueuedTrack>();

    /// <summary>
    /// Index of the next track to send to Liquidsoap's queue.
    /// Incremented after each successful queue.append.
    /// This does NOT represent what's currently playing — Liquidsoap tells us that via HTTP push.
    /// </summary>
    private int _nextTrackToSend;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        LiquidsoapClient liquidsoapClient,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider,
        RadioStateService state,
        RadioPlaybackSessionService playbackSession)
    {
        _logger = logger;
        _liquidsoapClient = liquidsoapClient;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
        _state = state;
        _playbackSession = playbackSession;
    }

    /// <summary>
    /// Executes the main radio scheduling loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online service started (Liquidsoap mode) — v0.0.0.37 — Push metadata architecture");

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
                    ResetState();

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

                        // After reconnect, re-sync our next-to-send position
                        if (_state.IsStreaming && _playlistTracks.Length > 0)
                        {
                            await ReSyncSendPositionAsync().ConfigureAwait(false);
                        }
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

                    ResetState();

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

                    // ── Buffer refill: keep Liquidsoap's queue full ──
                    await RefillQueueAsync().ConfigureAwait(false);

                    // Determine poll interval
                    var pollInterval = remaining < TimeSpan.FromSeconds(NearEndThresholdSeconds)
                        ? TimeSpan.FromSeconds(ScheduleCheckNearEndIntervalSeconds)
                        : TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds);

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
                ResetState();
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
    /// On fresh start (plugin restart), attempts to RESUME by querying what
    /// Liquidsoap is currently playing and continuing from that position.
    /// Does NOT clear the queue on resume — Liquidsoap keeps playing uninterrupted.
    /// </summary>
    private async Task LoadPlaylistToLiquidsoapAsync(
        PluginConfiguration config,
        ScheduleEntry activeEntry,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        // Detect if this is a fresh start (plugin restart) vs actual schedule change
        var isFreshStart = _currentScheduleKey == null;

        // Get audio files from Jellyfin playlist
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);
        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" is empty or not found", activeEntry.DisplayName);
            _currentScheduleKey = scheduleKey;
            _state.IsStreaming = false;
            return;
        }

        // Collect valid file paths
        var filePaths = new List<string>();
        foreach (var item in playlistItems)
        {
            var path = _audioProvider.GetAudioFilePath(item);
            if (path != null)
            {
                filePaths.Add(path);
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

        // Translate paths
        var trackList = new List<QueuedTrack>();
        foreach (var jellyfinPath in filePaths)
        {
            var liqPath = TranslatePath(jellyfinPath, config);
            if (liqPath != null)
            {
                trackList.Add(new QueuedTrack(liqPath));
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

        _logger.LogInformation(
            "Loading \"{Name}\" ({Day} {Start}-{End}) - {Count} tracks{Shuffle} to Liquidsoap",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime,
            _playlistTracks.Length, activeEntry.ShufflePlayback ? " [SHUFFLE]" : "");

        // ── RESUME: On fresh start, check if Liquidsoap is already playing from this playlist ──
        if (isFreshStart && !activeEntry.ShufflePlayback)
        {
            var resumed = await TryResumeAsync().ConfigureAwait(false);
            if (resumed)
            {
                _currentScheduleKey = scheduleKey;
                _state.IsStreaming = true;
                return;
            }
        }

        // ── FRESH START: Clear queue and load from track 0 ──
        ResetState();

        await RetryClearQueueAsync().ConfigureAwait(false);

        // Send first MinBufferDepth tracks
        var initialCount = Math.Min(MinBufferDepth, _playlistTracks.Length);
        var added = 0;
        for (var i = 0; i < initialCount; i++)
        {
            if (await _liquidsoapClient.AppendTrackAsync(_playlistTracks[i].LiquidsoapPath).ConfigureAwait(false))
            {
                added++;
                _nextTrackToSend = i + 1;
            }
        }

        _state.IsStreaming = added > 0;

        if (added > 0)
        {
            _currentScheduleKey = scheduleKey;
            _logger.LogInformation(
                "Queued {Added}/{Total} initial tracks for \"{Name}\" (buffer: {Buffer})",
                added, _playlistTracks.Length, activeEntry.DisplayName, MinBufferDepth);
        }
        else
        {
            _logger.LogError("Failed to queue any tracks for \"{Name}\" — will retry on next cycle", activeEntry.DisplayName);
            _currentScheduleKey = null;
            ResetState();
        }
    }

    /// <summary>
    /// Attempts to resume from where Liquidsoap currently is in the playlist.
    /// Queries queue.current_track and queue.length to determine the position.
    /// Does NOT clear the queue or skip — Liquidsoap keeps playing uninterrupted.
    /// </summary>
    private async Task<bool> TryResumeAsync()
    {
        try
        {
            // Query what Liquidsoap is currently playing
            var currentPath = await _liquidsoapClient.GetCurrentTrackAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(currentPath) || currentPath.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Resume check: Liquidsoap not playing anything, starting fresh");
                return false;
            }

            // Find matching track in our playlist
            var matchIndex = -1;
            for (var i = 0; i < _playlistTracks.Length; i++)
            {
                if (_playlistTracks[i].LiquidsoapPath.Equals(currentPath, StringComparison.Ordinal))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                _logger.LogInformation(
                    "Resume check: Liquidsoap playing \"{Path}\" which is not in playlist, starting fresh",
                    currentPath);
                return false;
            }

            // FOUND MATCH — resume from this position
            _logger.LogInformation(
                "RESUME: Liquidsoap is playing track {Index}/{Total} (\"{Path}\"), continuing without interrupting audio",
                matchIndex + 1, _playlistTracks.Length, currentPath);

            // Set next-to-send based on current position + what's already queued
            _nextTrackToSend = matchIndex + 1;

            // Check how many are already in the buffer
            var queueLength = await _liquidsoapClient.GetQueueLengthAsync().ConfigureAwait(false);
            if (queueLength > 0)
            {
                _nextTrackToSend = matchIndex + queueLength + 1;
                _logger.LogInformation(
                    "Queue has {QueueLength} track(s) buffered, next track to send: {Next}",
                    queueLength, _nextTrackToSend);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resume check failed, starting fresh");
            return false;
        }
    }

    /// <summary>
    /// After reconnect, re-synchronize our _nextTrackToSend position
    /// to avoid sending duplicate tracks to Liquidsoap.
    /// </summary>
    private async Task ReSyncSendPositionAsync()
    {
        try
        {
            var currentPath = await _liquidsoapClient.GetCurrentTrackAsync().ConfigureAwait(false);
            var queueLength = await _liquidsoapClient.GetQueueLengthAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(currentPath) || queueLength < 0)
            {
                _logger.LogDebug("ReSync: could not query Liquidsoap state");
                return;
            }

            // Find current track in playlist
            var matchIndex = -1;
            for (var i = 0; i < _playlistTracks.Length; i++)
            {
                if (_playlistTracks[i].LiquidsoapPath.Equals(currentPath, StringComparison.Ordinal))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                var newPosition = matchIndex + queueLength + 1;
                if (newPosition > _nextTrackToSend)
                {
                    _logger.LogInformation(
                        "ReSync: adjusted nextTrackToSend from {Old} to {New} (current={Current}, queued={Queued})",
                        _nextTrackToSend, newPosition, matchIndex + 1, queueLength);
                    _nextTrackToSend = newPosition;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReSync check failed");
        }
    }

    /// <summary>
    /// Keeps Liquidsoap's queue buffer full.
    /// Queries queue.length and sends tracks if below MinBufferDepth.
    /// Simple, reliable, no timing — just keep the buffer full.
    /// </summary>
    private async Task RefillQueueAsync()
    {
        if (_playlistTracks.Length == 0 || _nextTrackToSend >= _playlistTracks.Length)
            return;

        try
        {
            var queueLength = await _liquidsoapClient.GetQueueLengthAsync().ConfigureAwait(false);

            if (queueLength < 0)
            {
                // Query failed — try sending one track as precaution
                _logger.LogDebug("Queue length query failed, attempting buffer fill");
                await SendNextTrackAsync().ConfigureAwait(false);
                return;
            }

            // Calculate how many tracks we need to send
            var needed = MinBufferDepth - queueLength;

            while (needed > 0 && _nextTrackToSend < _playlistTracks.Length)
            {
                await SendNextTrackAsync().ConfigureAwait(false);
                needed--;
            }

            // Check if we've reached the end of the playlist
            if (_nextTrackToSend >= _playlistTracks.Length)
            {
                _logger.LogInformation("All {Count} tracks sent to Liquidsoap queue for current playlist", _playlistTracks.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Queue refill error, will retry next cycle");
        }
    }

    /// <summary>
    /// Sends the next track in the playlist to Liquidsoap's queue.
    /// </summary>
    private async Task<bool> SendNextTrackAsync()
    {
        if (_nextTrackToSend >= _playlistTracks.Length)
            return false;

        var track = _playlistTracks[_nextTrackToSend];
        if (await _liquidsoapClient.AppendTrackAsync(track.LiquidsoapPath).ConfigureAwait(false))
        {
            _logger.LogInformation("Queued track {Index}/{Total}: {Path}",
                _nextTrackToSend + 1, _playlistTracks.Length, track.LiquidsoapPath);
            _nextTrackToSend++;
            return true;
        }

        _logger.LogWarning("Failed to queue track {Index}/{Total}, retrying next cycle",
            _nextTrackToSend + 1, _playlistTracks.Length);
        return false;
    }

    /// <summary>
    /// Resets all tracking state.
    /// </summary>
    private void ResetState()
    {
        _playlistTracks = Array.Empty<QueuedTrack>();
        _nextTrackToSend = 0;
    }

    /// <summary>
    /// Builds a unique key for a schedule entry.
    /// Uses the entry's Id when available (v0.0.0.32+ multi-day entries).
    /// Falls back to day+time+playlist for legacy entries without an Id.
    /// </summary>
    private static string BuildScheduleKey(ScheduleEntry entry)
    {
        if (entry.Id != Guid.Empty)
            return entry.Id.ToString("N");
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
        ResetState();
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
        ResetState();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
