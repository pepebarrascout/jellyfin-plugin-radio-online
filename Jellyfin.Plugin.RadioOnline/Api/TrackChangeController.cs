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

    public TrackChangeController(
        ILogger<TrackChangeController> logger,
        ILibraryManager libraryManager,
        IUserManager userManager,
        RadioPlaybackSessionService playbackSession)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _playbackSession = playbackSession;
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

    private ActionResult HandleTrackChange(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogDebug("TrackChange received with empty path");
            return Ok();
        }

        var config = Plugin.Instance?.Configuration as PluginConfiguration;
        if (config == null || !config.EnablePlaybackReporting)
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

            // Get the configured user
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

            // Ensure virtual session exists and report playback
            var sessionReady = _playbackSession.EnsureSessionAsync(user).GetAwaiter().GetResult();
            if (sessionReady)
            {
                _playbackSession.ReportPlaybackStartAsync(audioItem).GetAwaiter().GetResult();
            }

            _logger.LogInformation(
                "TrackChange: {Artist} - {Title}",
                audioItem.Artists != null && audioItem.Artists.Count > 0
                    ? string.Join(", ", audioItem.Artists)
                    : "Unknown Artist",
                audioItem.Name ?? "Unknown");
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
