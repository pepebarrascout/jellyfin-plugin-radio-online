using System;

namespace Jellyfin.Plugin.RadioOnline.Configuration;

/// <summary>
/// Represents a single schedule entry in the weekly radio programming.
/// Supports multiple days of the week via individual day booleans.
/// Falls back to the legacy single DayOfWeek when no day boolean is set,
/// ensuring backward compatibility with configs created before v0.0.0.32.
/// </summary>
public class ScheduleEntry
{
    /// <summary>
    /// Unique identifier for this schedule entry.
    /// Used to detect schedule changes across day boundaries for the same entry.
    /// Generated on first edit/save in the UI; empty for legacy entries.
    /// </summary>
    public Guid Id { get; set; } = Guid.Empty;

    /// <summary>
    /// Legacy single day of the week.
    /// Only used when no individual day boolean (Monday..Sunday) is set.
    /// Kept for backward compatibility with configs from v0.0.0.31 and earlier.
    /// </summary>
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

    /// <summary>
    /// Whether this schedule entry is active on Monday.
    /// </summary>
    public bool Monday { get; set; }

    /// <summary>
    /// Whether this schedule entry is active on Tuesday.
    /// </summary>
    public bool Tuesday { get; set; }

    /// <summary>
    /// Whether this schedule entry is active on Wednesday.
    /// </summary>
    public bool Wednesday { get; set; }

    /// <summary>
    /// Whether this schedule entry is active on Thursday.
    /// </summary>
    public bool Thursday { get; set; }

    /// <summary>
    /// Whether this schedule entry is active on Friday.
    /// </summary>
    public bool Friday { get; set; }

    /// <summary>
    /// Whether this schedule entry is active on Saturday.
    /// </summary>
    public bool Saturday { get; set; }

    /// <summary>
    /// Whether this schedule entry is active on Sunday.
    /// </summary>
    public bool Sunday { get; set; }

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
    /// If empty or null, no playback occurs.
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
    /// Gets or sets whether this schedule entry is paused.
    /// When true, the entry is completely ignored during schedule matching
    /// regardless of day, time, or playlist settings.
    /// Unlike IsEnabled (which is managed internally), IsPaused is a
    /// user-facing quick toggle to temporarily disable a program.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Gets or sets whether to shuffle the playlist tracks.
    /// When true, tracks are played in random order each time the schedule activates.
    /// When false (default), tracks play in the order defined in Jellyfin.
    /// </summary>
    public bool ShufflePlayback { get; set; }

    /// <summary>
    /// Returns true if any individual day boolean (Monday..Sunday) is set.
    /// Used to determine whether to use the new multi-day system or fall back
    /// to the legacy single DayOfWeek property.
    /// </summary>
    public bool HasDaySelection()
    {
        return Monday || Tuesday || Wednesday || Thursday
            || Friday || Saturday || Sunday;
    }

    /// <summary>
    /// Returns true if this schedule entry is active on the given day of the week.
    /// When day booleans are set (HasDaySelection), uses those.
    /// Otherwise falls back to comparing against the legacy DayOfWeek property.
    /// </summary>
    public bool IsActiveOnDay(DayOfWeek day)
    {
        if (HasDaySelection())
        {
            return day switch
            {
                DayOfWeek.Monday => Monday,
                DayOfWeek.Tuesday => Tuesday,
                DayOfWeek.Wednesday => Wednesday,
                DayOfWeek.Thursday => Thursday,
                DayOfWeek.Friday => Friday,
                DayOfWeek.Saturday => Saturday,
                DayOfWeek.Sunday => Sunday,
                _ => false
            };
        }

        // Backward compatibility: legacy single-day entries
        return day == DayOfWeek;
    }

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
