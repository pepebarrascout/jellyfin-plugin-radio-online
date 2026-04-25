using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RadioOnline.Configuration;

/// <summary>
/// Configuration model for the Radio Online plugin.
/// Contains Icecast server settings and weekly schedule programming.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Icecast Server Settings ──────────────────────────────────────────

    /// <summary>
    /// Gets or sets the Icecast server URL (e.g., http://your-server:8000).
    /// </summary>
    public string IcecastUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Icecast source username (typically "source").
    /// </summary>
    public string IcecastUsername { get; set; } = "source";

    /// <summary>
    /// Gets or sets the Icecast source password.
    /// </summary>
    public string IcecastPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Icecast mount point (e.g., /radio or /stream).
    /// Must start with a forward slash.
    /// </summary>
    public string IcecastMountPoint { get; set; } = "/radio";

    /// <summary>
    /// Gets or sets the audio format to send to Icecast.
    /// Supported values: "m4a" (AAC in MPEG-4 container) or "ogg" (Ogg Vorbis/Opus).
    /// </summary>
    public string AudioFormat { get; set; } = "ogg";

    /// <summary>
    /// Gets or sets the audio bitrate in kbps.
    /// </summary>
    public int AudioBitrate { get; set; } = 128;

    /// <summary>
    /// Gets or sets the stream name metadata sent to Icecast listeners.
    /// </summary>
    public string StreamName { get; set; } = "Jellyfin Radio Online";

    /// <summary>
    /// Gets or sets the stream description metadata sent to Icecast listeners.
    /// </summary>
    public string StreamDescription { get; set; } = "Automated online radio powered by Jellyfin";

    /// <summary>
    /// Gets or sets the stream genre metadata sent to Icecast listeners.
    /// </summary>
    public string StreamGenre { get; set; } = "Various";

    /// <summary>
    /// Gets or sets the public flag for the Icecast stream (true = listed in directory).
    /// </summary>
    public bool StreamPublic { get; set; } = false;

    // ── Scheduling Settings ──────────────────────────────────────────────

    /// <summary>
    /// Gets or sets whether the radio automation is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the weekly schedule entries.
    /// Each entry maps a specific day and time slot to a playlist.
    /// </summary>
    public List<ScheduleEntry> ScheduleEntries { get; set; } = new();

    // ── Jellyfin Settings ────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the Jellyfin user ID used for library access.
    /// This user must have access to all relevant media libraries.
    /// </summary>
    public string JellyfinUserId { get; set; } = string.Empty;
}
