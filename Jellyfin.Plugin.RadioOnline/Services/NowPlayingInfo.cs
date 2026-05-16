using System;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Stores metadata about the currently playing track for the NowPlaying endpoint.
/// Thread-safe via locking in RadioStateService.
/// </summary>
public class NowPlayingInfo
{
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int? Year { get; set; }
    public long? DurationTicks { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>
    /// ID of the parent album (MusicAlbum) that holds the Primary image (album art).
    /// Songs typically don't have Primary images — the artwork lives on the album folder.
    /// Falls back to ItemId if no album parent is found.
    /// </summary>
    public Guid AlbumId { get; set; }
    /// <summary>
    /// Genre of the album (e.g. "Rock", "Pop", "Jazz").
    /// Taken from the parent MusicAlbum's Genres list (first genre if multiple).
    /// Falls back to the song's Genres if no album parent is found.
    /// </summary>
    public string Genre { get; set; } = string.Empty;
}
