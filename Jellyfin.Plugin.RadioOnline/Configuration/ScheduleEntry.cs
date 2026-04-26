using System;

namespace Jellyfin.Plugin.RadioOnline.Configuration;

/// <summary>
/// Represents a single schedule entry in the weekly radio programming.
/// Maps a specific day-of-week and time slot to a Jellyfin playlist.
/// </summary>
public class ScheduleEntry
{
    /// <summary>
    /// Gets or sets the day of the week (Monday through Friday).
    /// </summary>
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    /// <summary>
    /// Gets or sets the start time of the schedule slot in "HH:mm" format (24-hour).
    /// </summary>
    public string StartTime { get; set; } = "00:00";

    /// <summary>
    /// Gets or sets the end time of the schedule slot in "HH:mm" format (24-hour).
    /// </summary>
    public string EndTime { get; set; } = "23:59";

    /// <summary>
    /// Gets or sets the Jellyfin playlist ID to play during this slot.
    /// If empty or null, random music from the library will be used instead.
    /// </summary>
    public string PlaylistId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a friendly display name for this schedule entry.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this schedule entry is active.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to shuffle the playlist tracks.
    /// When true, tracks are played in random order each time the schedule activates.
    /// When false (default), tracks play in the order defined in Jellyfin.
    /// </summary>
    public bool ShufflePlayback { get; set; } = false;

    /// <summary>
    /// Parses the StartTime string into a TimeSpan.
    /// </summary>
    /// <returns>The parsed TimeSpan, or TimeSpan.Zero if parsing fails.</returns>
    public TimeSpan GetStartTimeSpan()
    {
        if (TimeSpan.TryParse(StartTime, out var result))
        {
            return result;
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Parses the EndTime string into a TimeSpan.
    /// </summary>
    /// <returns>The parsed TimeSpan, or TimeSpan.FromHours(23.9833) if parsing fails.</returns>
    public TimeSpan GetEndTimeSpan()
    {
        if (TimeSpan.TryParse(EndTime, out var result))
        {
            return result == TimeSpan.Zero ? TimeSpan.FromHours(24) : result;
        }

        return TimeSpan.FromHours(24);
    }
}
