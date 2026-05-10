using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RadioOnline.Configuration;

/// <summary>
/// Configuration model for the Radio Online plugin.
/// Contains Liquidsoap connection settings and weekly schedule programming.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Liquidsoap Settings ──────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the Liquidsoap Telnet server host.
    /// Default: "localhost".
    /// </summary>
    public string LiquidsoapHost { get; set; } = "localhost";

    /// <summary>
    /// Gets or sets the Liquidsoap Telnet server port.
    /// Default: 8080.
    /// </summary>
    public int LiquidsoapPort { get; set; } = 8080;

    // ── Path Mapping ────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the Jellyfin media root path on the host filesystem.
    /// Used for path translation when sending tracks to Liquidsoap.
    /// Example: "/media".
    /// </summary>
    public string JellyfinMediaPath { get; set; } = "/media";

    /// <summary>
    /// Gets or sets the corresponding music path inside the Liquidsoap container.
    /// Example: "/music".
    /// Paths sent to Liquidsoap have JellyfinMediaPath replaced with LiquidsoapMusicPath.
    /// </summary>
    public string LiquidsoapMusicPath { get; set; } = "/music";

    // ── Scheduling Settings ──────────────────────────────────────────────

    /// <summary>
    /// Gets or sets whether the radio automation is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the weekly schedule entries.
    /// </summary>
    public List<ScheduleEntry> ScheduleEntries { get; set; } = new();

    // ── Jellyfin Settings ────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the Jellyfin user ID used for library access.
    /// </summary>
    public string JellyfinUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to report track plays to Jellyfin statistics.
    /// When enabled, each track played on the radio increments the PlayCount
    /// and updates LastPlayedDate, enabling smart playlists (most played, least played, etc.).
    /// </summary>
    public bool EnablePlaybackReporting { get; set; } = true;
}
