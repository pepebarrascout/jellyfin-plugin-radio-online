using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Creates and manages a virtual Jellyfin playback session for the radio.
/// This makes the radio appear as an active client in Jellyfin's dashboard,
/// enabling scrobbling plugins (Last.fm, ListenBrainz, etc.) to detect
/// playback activity as they would with any normal client (Feishin, Finamp, etc.).
///
/// Flow:
///   1. EnsureSessionAsync() — creates a session via LogSessionActivity if not yet active
///   2. ReportPlaybackStartAsync() — fires PlaybackStart event (triggers scrobble registration)
///   3. ReportPlaybackStoppedAsync() — fires PlaybackStopped with PlayedToCompletion (triggers scrobble)
///
/// The session is identified by a fixed DeviceId so Jellyfin reuses the same session
/// across schedule changes. The session appears in the dashboard as "Radio Online" client.
/// </summary>
public class RadioPlaybackSessionService
{
    private readonly ILogger<RadioPlaybackSessionService> _logger;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Fixed client identifier for the virtual radio session.
    /// Must be unique across all Jellyfin clients.
    /// </summary>
    private const string AppName = "Radio Online";

    private const string DeviceId = "radio-online-plugin";
    private const string DeviceName = "Radio Server";
    private const string AppVersion = "1.0.0";

    /// <summary>
    /// The active Jellyfin session, or null if not yet created.
    /// </summary>
    private SessionInfo? _session;

    /// <summary>
    /// The PlaySessionId for the currently playing track (unique per track).
    /// </summary>
    private string _currentPlaySessionId = string.Empty;

    /// <summary>
    /// The Id of the currently playing item, to avoid duplicate start/stop events.
    /// </summary>
    private Guid _currentItemId;

    /// <summary>
    /// Whether a playback start has been reported for the current item (and not yet stopped).
    /// </summary>
    private bool _isPlaying;

    /// <summary>
    /// The runtime ticks of the currently playing item.
    /// Stored on PlaybackStart so that PlaybackStopped can report positionTicks = runtime,
    /// ensuring Jellyfin considers the track as PlayedToCompletion (triggers PlayCount++ and scrobble).
    /// </summary>
    private long _currentItemRunTimeTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioPlaybackSessionService"/> class.
    /// </summary>
    public RadioPlaybackSessionService(
        ILogger<RadioPlaybackSessionService> logger,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Gets whether the virtual session is active.
    /// </summary>
    public bool IsSessionActive => _session != null;

    /// <summary>
    /// Ensures a Jellyfin session exists for the radio.
    /// Creates a new session or reuses the existing one.
    /// The session will appear in Jellyfin's dashboard as an active client.
    /// </summary>
    /// <param name="user">The Jellyfin user associated with the radio.</param>
    /// <returns>True if the session is ready for playback reporting.</returns>
    public async Task<bool> EnsureSessionAsync(User user)
    {
        try
        {
            if (_session != null)
            {
                // Session already exists — verify it's still valid by logging activity
                _session = await _sessionManager.LogSessionActivity(
                    AppName,
                    AppVersion,
                    DeviceId,
                    DeviceName,
                    "127.0.0.1",
                    user).ConfigureAwait(false);

                return true;
            }

            // Create a new virtual session
            _session = await _sessionManager.LogSessionActivity(
                AppName,
                AppVersion,
                DeviceId,
                DeviceName,
                "127.0.0.1",
                user).ConfigureAwait(false);

            _logger.LogInformation(
                "Virtual playback session created: {SessionId} (user: {UserName}, device: {DeviceName})",
                _session.Id, user.Username, DeviceName);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create virtual playback session");
            _session = null;
            return false;
        }
    }

    /// <summary>
    /// Reports that a track has started playing on the radio.
    /// Fires Jellyfin's PlaybackStart event, which triggers:
    ///   - Now-playing display in Jellyfin dashboard
    ///   - Scrobble registration in Last.fm / ListenBrainz plugins
    ///   - Playback start notification to all connected clients
    ///
    /// Automatically stops the previous track if one was playing.
    /// Stores the item's RunTimeTicks for later use in PlaybackStopped.
    /// </summary>
    /// <param name="audioItem">The audio item being played.</param>
    public async Task ReportPlaybackStartAsync(Audio audioItem)
    {
        if (_session == null)
        {
            _logger.LogWarning("Cannot report playback start: no active session");
            return;
        }

        // Stop the previous track first (if any)
        if (_isPlaying && _currentItemId != Guid.Empty)
        {
            await ReportPlaybackStoppedAsync().ConfigureAwait(false);
        }

        try
        {
            _currentPlaySessionId = Guid.NewGuid().ToString("N");
            _currentItemId = audioItem.Id;
            _currentItemRunTimeTicks = audioItem.RunTimeTicks.HasValue && audioItem.RunTimeTicks.Value > 0
                ? audioItem.RunTimeTicks.Value
                : TimeSpan.FromMinutes(3).Ticks;

            var startInfo = new PlaybackStartInfo
            {
                ItemId = audioItem.Id,
                SessionId = _session.Id,
                CanSeek = false,
                IsPaused = false,
                IsMuted = false,
                PlayMethod = PlayMethod.DirectPlay,
                PositionTicks = 0,
                MediaSourceId = audioItem.Id.ToString("N"),
                PlaySessionId = _currentPlaySessionId,
                VolumeLevel = 100
            };

            await _sessionManager.OnPlaybackStart(startInfo).ConfigureAwait(false);
            _isPlaying = true;

            _logger.LogDebug(
                "Session playback start: {Artist} - {Title} (PlaySessionId: {PlaySessionId})",
                audioItem.Artists != null && audioItem.Artists.Count > 0
                    ? string.Join(", ", audioItem.Artists)
                    : "Unknown",
                audioItem.Name,
                _currentPlaySessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report playback start for {ItemName}", audioItem.Name);
            _isPlaying = false;
        }
    }

    /// <summary>
    /// Reports that a track has stopped playing on the radio.
    /// Fires Jellyfin's PlaybackStopped event with positionTicks set to the item's
    /// full runtime, ensuring Jellyfin considers it PlayedToCompletion.
    /// This triggers both PlayCount++ (via Jellyfin's internal handler) and scrobble
    /// submission to Last.fm / ListenBrainz.
    /// </summary>
    public async Task ReportPlaybackStoppedAsync()
    {
        if (_session == null || !_isPlaying || _currentItemId == Guid.Empty)
            return;

        try
        {
            var stopInfo = new PlaybackStopInfo
            {
                ItemId = _currentItemId,
                SessionId = _session.Id,
                PositionTicks = _currentItemRunTimeTicks,
                Failed = false,
                MediaSourceId = _currentItemId.ToString("N"),
                PlaySessionId = _currentPlaySessionId
            };

            await _sessionManager.OnPlaybackStopped(stopInfo).ConfigureAwait(false);
            _logger.LogDebug(
                "Session playback stopped (PlaySessionId: {PlaySessionId})",
                _currentPlaySessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report playback stop for item {ItemId}", _currentItemId);
        }
        finally
        {
            _isPlaying = false;
            _currentItemId = Guid.Empty;
            _currentPlaySessionId = string.Empty;
            _currentItemRunTimeTicks = 0;
        }
    }

    /// <summary>
    /// Ends the virtual playback session.
    /// Stops any currently playing track and removes the session from Jellyfin.
    /// Call this when the plugin is disabled or Jellyfin is shutting down.
    /// </summary>
    public async Task EndSessionAsync()
    {
        // Stop any currently playing track
        if (_isPlaying)
        {
            await ReportPlaybackStoppedAsync().ConfigureAwait(false);
        }

        // End the session itself
        if (_session != null)
        {
            try
            {
                await _sessionManager.ReportSessionEnded(_session.Id).ConfigureAwait(false);
                _logger.LogInformation("Virtual playback session ended: {SessionId}", _session.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to end virtual session");
            }

            _session = null;
        }
    }
}
