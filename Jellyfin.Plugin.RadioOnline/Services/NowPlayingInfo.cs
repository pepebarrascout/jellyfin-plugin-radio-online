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
}
