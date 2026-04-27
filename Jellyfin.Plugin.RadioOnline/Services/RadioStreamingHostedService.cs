using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the radio streaming loop using Liquidsoap.
/// Monitors the weekly schedule and sends tracks to Liquidsoap's queue via Telnet.
/// When a schedule becomes active, clears the queue and pushes all playlist tracks.
/// When a schedule ends or changes, clears the queue and Liquidsoap plays silence.
/// Liquidsoap handles encoding, streaming to Icecast, and silence gaps.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly LiquidsoapClient _liquidsoapClient;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;
    private readonly RadioStateService _state;

    /// <summary>
    /// Interval for checking schedule changes while streaming.
    /// </summary>
    private const int ScheduleCheckIntervalSeconds = 5;

    /// <summary>
    /// Tracks the currently active schedule entry to detect changes.
    /// </summary>
    private string? _currentPlaylistId;

    /// <summary>
    /// Tracks whether the plugin was previously enabled, to detect state changes.
    /// </summary>
    private bool _wasEnabled;

    /// <summary>
    /// Tracks whether we already warned about Liquidsoap being unreachable (avoids log spam).
    /// </summary>
    private bool _warnedLiquidsoapDown;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        LiquidsoapClient liquidsoapClient,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider,
        RadioStateService state)
    {
        _logger = logger;
        _liquidsoapClient = liquidsoapClient;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
        _state = state;
    }

    /// <summary>
    /// Executes the main radio scheduling loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online service started (Liquidsoap mode)");

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
                    // Detect transition: was enabled, now disabled
                    if (_wasEnabled)
                    {
                        _wasEnabled = false;
                        _warnedLiquidsoapDown = false;
                        _logger.LogInformation("Radio automatizada desactivada - pausando servicio");

                        // Clear queue and disconnect cleanly
                        try
                        {
                            await _liquidsoapClient.ClearQueueAsync().ConfigureAwait(false);
                        }
                        catch { }

                        _liquidsoapClient.Disconnect();
                    }

                    _state.IsStreaming = false;
                    _currentPlaylistId = null;

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // ── Plugin enabled ──

                // Detect transition: was disabled, now enabled
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

                // Check Liquidsoap connectivity - attempt to connect if not connected
                if (!_liquidsoapClient.IsConnected)
                {
                    // Try to establish the connection
                    var connected = await _liquidsoapClient.TestConnectionAsync().ConfigureAwait(false);

                    if (!connected)
                    {
                        if (!_warnedLiquidsoapDown)
                        {
                            _logger.LogWarning("Liquidsoap no disponible en {Host}:{Port} - se reintentara en cada ciclo",
                                config.LiquidsoapHost, config.LiquidsoapPort);
                            _warnedLiquidsoapDown = true;
                        }

                        // Wait longer when Liquidsoap is down to avoid spam
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    // Connection succeeded - reset warning flag
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
                    // No active schedule - clear queue if we were streaming
                    if (_state.IsStreaming)
                    {
                        _logger.LogInformation("No active schedule, clearing Liquidsoap queue");
                        await _liquidsoapClient.ClearQueueAsync().ConfigureAwait(false);
                        _state.IsStreaming = false;
                        _currentPlaylistId = null;
                    }

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

                // Check if the schedule has changed
                if (!string.Equals(_currentPlaylistId, activeEntry.PlaylistId, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Schedule change: {OldPlaylist} -> {NewPlaylist} ({Name})",
                        _currentPlaylistId ?? "(none)",
                        activeEntry.PlaylistId,
                        activeEntry.DisplayName);

                    await LoadPlaylistToLiquidsoapAsync(config, activeEntry, stoppingToken).ConfigureAwait(false);
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
                _state.IsStreaming = false;
                _currentPlaylistId = null;
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
        _state.IsStreaming = false;
        _logger.LogInformation("Radio Online service stopped");
    }

    /// <summary>
    /// Loads a playlist's tracks into the Liquidsoap queue.
    /// Clears the existing queue first, then pushes all tracks.
    /// </summary>
    private async Task LoadPlaylistToLiquidsoapAsync(
        PluginConfiguration config,
        ScheduleEntry activeEntry,
        CancellationToken cancellationToken)
    {
        // Get audio files from Jellyfin playlist
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);
        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" is empty or not found", activeEntry.DisplayName);
            _currentPlaylistId = activeEntry.PlaylistId;
            _state.IsStreaming = false;
            return;
        }

        // Collect valid file paths
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
            _currentPlaylistId = activeEntry.PlaylistId;
            _state.IsStreaming = false;
            return;
        }

        // Shuffle playback if enabled
        if (activeEntry.ShufflePlayback)
        {
            ShuffleList(filePaths);
            _logger.LogInformation("Shuffle enabled for \"{Name}\" - randomized {Count} tracks", activeEntry.DisplayName, filePaths.Count);
        }

        // Translate paths: Jellyfin path -> Liquidsoap path
        var liquidsoapPaths = filePaths
            .Select(p => TranslatePath(p, config))
            .Where(p => p != null)
            .Cast<string>()
            .ToArray();

        if (liquidsoapPaths.Length == 0)
        {
            _logger.LogWarning("No valid paths after translation for playlist \"{Name}\"", activeEntry.DisplayName);
            _currentPlaylistId = activeEntry.PlaylistId;
            _state.IsStreaming = false;
            return;
        }

        // Clear the queue and load new tracks
        _logger.LogInformation(
            "Loading \"{Name}\" ({Day} {Start}-{End}) - {Count} tracks{Shuffle} to Liquidsoap",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime,
            liquidsoapPaths.Length, activeEntry.ShufflePlayback ? " [SHUFFLE]" : "");

        await _liquidsoapClient.ClearQueueAsync().ConfigureAwait(false);

        var added = await _liquidsoapClient.AppendTracksAsync(liquidsoapPaths, cancellationToken).ConfigureAwait(false);

        _currentPlaylistId = activeEntry.PlaylistId;
        _state.IsStreaming = added > 0;

        if (added > 0)
        {
            _logger.LogInformation(
                "Queued {Added}/{Total} tracks for \"{Name}\" to Liquidsoap",
                added, liquidsoapPaths.Length, activeEntry.DisplayName);
        }
        else
        {
            _logger.LogError("Failed to queue any tracks for \"{Name}\"", activeEntry.DisplayName);
        }
    }

    /// <summary>
    /// Translates a Jellyfin filesystem path to the corresponding Liquidsoap path.
    /// Replaces the Jellyfin media root with the Liquidsoap music path.
    /// Example: /media/Music/Album/song.m4a -> /music/Music/Album/song.m4a
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

            // If the path doesn't start with the media root, return as-is
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

        _liquidsoapClient.Disconnect();
        _state.IsStreaming = false;
        _currentPlaylistId = null;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
