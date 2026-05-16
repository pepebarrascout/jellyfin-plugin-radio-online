using System;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Api;

/// <summary>
/// Receives track change notifications from Liquidsoap via HTTP.
/// Liquidsoap's on_track callback sends a GET/POST to this endpoint
/// with the filename of the currently playing track.
/// The controller looks up the corresponding Jellyfin Audio item and
/// reports it to ISessionManager for accurate playback tracking.
///
/// Also provides a public NowPlaying endpoint for external apps/websites
/// to retrieve current track metadata and album artwork URL.
///
/// This is the SINGLE source of truth for what is playing on the radio.
/// No guessing, no sync — Liquidsoap tells us directly.
/// </summary>
[ApiController]
[Route("RadioOnline/[action]")]
public class TrackChangeController : ControllerBase
{
    private readonly ILogger<TrackChangeController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly RadioPlaybackSessionService _playbackSession;
    private readonly RadioStateService _state;

    public TrackChangeController(
        ILogger<TrackChangeController> logger,
        ILibraryManager libraryManager,
        IUserManager userManager,
        RadioPlaybackSessionService playbackSession,
        RadioStateService state)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _playbackSession = playbackSession;
        _state = state;
    }

    /// <summary>
    /// Receives track change notification from Liquidsoap.
    /// Called by Liquidsoap's on_track callback via HTTP GET.
    /// Query parameter: path = Liquidsoap file path of the currently playing track.
    /// </summary>
    [HttpGet]
    public ActionResult TrackChange([FromQuery] string? path)
    {
        return HandleTrackChange(path);
    }

    /// <summary>
    /// Receives track change notification from Liquidsoap (POST variant).
    /// </summary>
    [HttpPost]
    public ActionResult TrackChangePost([FromQuery] string? path)
    {
        return HandleTrackChange(path);
    }

    /// <summary>
    /// Public endpoint that returns metadata and artwork URL for the currently playing track.
    /// Used by external apps and websites to display real-time now-playing information.
    /// No authentication required.
    /// 
    /// Optional query parameter: maxWidth (default 720) — controls album art resolution.
    /// 
    /// Example response:
    /// {
    ///   "artist": "Martin Garrix",
    ///   "title": "Gold Skies",
    ///   "album": "Gold Skies",
    ///   "year": 2014,
    ///   "duration": "4:23",
    ///   "artworkUrl": "http://server:8096/Items/abc/Images/Primary?maxWidth=720"
    /// }
    /// </summary>
    [HttpGet]
    public ActionResult NowPlaying([FromQuery] int maxWidth = 720)
    {
        var track = _state.CurrentTrack;
        if (track == null)
        {
            return Ok(new { isPlaying = false });
        }

        // Build artwork URL using the request's scheme + host (works behind Cloudflare tunnel, reverse proxy, etc.)
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var artworkUrl = $"{baseUrl}/Items/{track.ItemId}/Images/Primary?maxWidth={maxWidth}";

        // Format duration
        string? duration;
        if (track.DurationTicks.HasValue && track.DurationTicks.Value > 0)
        {
            var ts = TimeSpan.FromTicks(track.DurationTicks.Value);
            duration = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
        else
        {
            duration = (string?)null;
        }

        return Ok(new
        {
            isPlaying = true,
            artist = track.Artist,
            title = track.Title,
            album = track.Album,
            year = track.Year,
            duration,
            artworkUrl
        });
    }

    private ActionResult HandleTrackChange(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogDebug("TrackChange received with empty path");
            return Ok();
        }

        var config = Plugin.Instance?.Configuration as PluginConfiguration;
        if (config == null)
        {
            return Ok();
        }

        try
        {
            // Reverse-translate: Liquidsoap path → Jellyfin path
            var jellyfinPath = TranslateLiqPathToJellyfin(path, config);

            // Find the Audio item in Jellyfin's library by path
            var audioItem = FindAudioByPath(jellyfinPath!);
            if (audioItem == null)
            {
                _logger.LogDebug("TrackChange: no Audio item found for path \"{Path}\"", path);
                return Ok();
            }

            // Store current track info for the NowPlaying endpoint
            _state.CurrentTrack = new NowPlayingInfo
            {
                Artist = audioItem.Artists != null && audioItem.Artists.Count > 0
                    ? string.Join(", ", audioItem.Artists)
                    : string.Empty,
                Title = audioItem.Name ?? string.Empty,
                Album = audioItem.Album ?? string.Empty,
                Year = audioItem.ProductionYear,
                DurationTicks = audioItem.RunTimeTicks,
                ItemId = audioItem.Id
            };

            // Report playback to Jellyfin (if enabled)
            if (config.EnablePlaybackReporting)
            {
                if (!Guid.TryParse(config.JellyfinUserId, out var userId))
                {
                    _logger.LogWarning("TrackChange: invalid user ID");
                    return Ok();
                }

                var user = _userManager.GetUserById(userId);
                if (user == null)
                {
                    _logger.LogWarning("TrackChange: user not found {UserId}", userId);
                    return Ok();
                }

                var sessionReady = _playbackSession.EnsureSessionAsync(user).GetAwaiter().GetResult();
                if (sessionReady)
                {
                    _playbackSession.ReportPlaybackStartAsync(audioItem).GetAwaiter().GetResult();
                }
            }

            _logger.LogInformation(
                "TrackChange: {Artist} - {Title}",
                _state.CurrentTrack.Artist,
                _state.CurrentTrack.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrackChange error for path \"{Path}\"", path);
        }

        return Ok();
    }

    /// <summary>
    /// Translates a Liquidsoap file path back to a Jellyfin filesystem path.
    /// Reverse of RadioStreamingHostedService.TranslatePath.
    /// </summary>
    private static string? TranslateLiqPathToJellyfin(string liqPath, PluginConfiguration config)
    {
        try
        {
            var liqRoot = config.LiquidsoapMusicPath.TrimEnd('/');
            var jellyfinRoot = config.JellyfinMediaPath.TrimEnd('/');

            if (liqPath.StartsWith(liqRoot, StringComparison.OrdinalIgnoreCase))
            {
                return jellyfinRoot + liqPath.Substring(liqRoot.Length);
            }

            return liqPath;
        }
        catch
        {
            return liqPath;
        }
    }

    /// <summary>
    /// Finds an Audio item in Jellyfin's library by its filesystem path.
    /// </summary>
    private Audio? FindAudioByPath(string path)
    {
        try
        {
            // Normalize the path for comparison
            var normalizedPath = path.Replace('\\', '/');

            // Query by path using ILibraryManager
            var query = new InternalItemsQuery
            {
                Path = normalizedPath,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Limit = 1,
            };

            var result = _libraryManager.GetItemsResult(query);
            return result.Items.FirstOrDefault() as Audio;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to find audio item by path \"{Path}\"", path);
            return null;
        }
    }
}
