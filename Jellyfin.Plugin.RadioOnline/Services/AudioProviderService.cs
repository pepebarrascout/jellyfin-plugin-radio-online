using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Provides audio items from Jellyfin's library and playlists.
/// Handles retrieving playlist items in their defined order and item metadata.
/// No random music functionality - streaming only occurs when a scheduled playlist is active.
/// </summary>
public class AudioProviderService
{
    private readonly ILogger<AudioProviderService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioProviderService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="playlistManager">The Jellyfin playlist manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    public AudioProviderService(
        ILogger<AudioProviderService> logger,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        IUserManager userManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets all audio items from a specific playlist in their defined order.
    /// Uses LinkedChildren to preserve the exact order the user arranged in Jellyfin.
    /// </summary>
    /// <param name="playlistId">The GUID of the playlist.</param>
    /// <param name="userId">The user ID for access validation.</param>
    /// <returns>An ordered list of audio items from the playlist, or an empty list if not found.</returns>
    public List<Audio> GetPlaylistItems(string playlistId, string userId)
    {
        try
        {
            if (!Guid.TryParse(playlistId, out var playlistGuid))
            {
                _logger.LogError("Invalid playlist ID format: {PlaylistId}", playlistId);
                return new List<Audio>();
            }

            var playlist = _libraryManager.GetItemById(playlistGuid) as Playlist;

            if (playlist == null)
            {
                _logger.LogError("Playlist not found: {PlaylistId}", playlistId);
                return new List<Audio>();
            }

            // Use LinkedChildren to preserve the exact playlist order
            var audioItems = new List<Audio>();
            foreach (var linkedChild in playlist.LinkedChildren)
            {
                try
                {
                    var itemId = linkedChild.ItemId;
                    if (!itemId.HasValue) continue;
                    var item = _libraryManager.GetItemById(itemId.Value);
                    if (item is Audio audio && !string.IsNullOrEmpty(audio.Path))
                    {
                        audioItems.Add(audio);
                    }
                }
                catch
                {
                    // Skip unavailable items silently
                }
            }

            _logger.LogDebug("Playlist \"{Name}\": {Count} tracks", playlist.Name, audioItems.Count);

            return audioItems;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving playlist items for {PlaylistId}", playlistId);
            return new List<Audio>();
        }
    }

    /// <summary>
    /// Gets all available playlists in Jellyfin for a user.
    /// </summary>
    /// <param name="userId">The user ID for access validation.</param>
    /// <returns>A list of playlist info tuples (id, name).</returns>
    public List<(string Id, string Name)> GetAvailablePlaylists(string userId)
    {
        try
        {
            if (!TryGetUser(userId, out _))
            {
                _logger.LogError("User not found: {UserId}", userId);
                return new List<(string, string)>();
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Playlist },
            };

            var result = _libraryManager.GetItemsResult(query);
            var playlists = result.Items
                .Select(p => (p.Id.ToString("N"), p.Name))
                .ToList();

            _logger.LogDebug("Found {Count} playlists", playlists.Count);
            return playlists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving playlists for user {UserId}", userId);
            return new List<(string, string)>();
        }
    }

    /// <summary>
    /// Calculates the total duration of a list of audio items.
    /// </summary>
    /// <param name="audioItems">The audio items to calculate duration for.</param>
    /// <returns>The total duration as a TimeSpan.</returns>
    public TimeSpan CalculateTotalDuration(List<Audio> audioItems)
    {
        var totalTicks = audioItems
            .Where(a => a.RunTimeTicks.HasValue)
            .Sum(a => a.RunTimeTicks!.Value);

        return TimeSpan.FromTicks(totalTicks);
    }

    /// <summary>
    /// Gets the filesystem path for an audio item.
    /// </summary>
    /// <param name="audioItem">The audio item.</param>
    /// <returns>The filesystem path, or null if unavailable.</returns>
    public string? GetAudioFilePath(Audio audioItem)
    {
        if (string.IsNullOrEmpty(audioItem.Path))
        {
            _logger.LogWarning("Audio item has no path: {Name} (ID: {Id})", audioItem.Name, audioItem.Id);
            return null;
        }

        if (!System.IO.File.Exists(audioItem.Path))
        {
            _logger.LogWarning("Audio file not found: {Path}", audioItem.Path);
            return null;
        }

        return audioItem.Path;
    }

    /// <summary>
    /// Validates that a user exists by their ID string.
    /// </summary>
    /// <param name="userId">The user ID string.</param>
    /// <param name="userGuid">The parsed user GUID if valid.</param>
    /// <returns>True if the user exists, false otherwise.</returns>
    private bool TryGetUser(string userId, out Guid userGuid)
    {
        userGuid = Guid.Empty;
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out userGuid))
        {
            return false;
        }

        try
        {
            var user = _userManager.GetUserById(userGuid);
            return user != null;
        }
        catch
        {
            return false;
        }
    }
}
