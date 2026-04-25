using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Manages the weekly radio schedule by determining which playlist should play
/// at any given time based on the configured schedule entries.
/// Handles time slot matching, playlist duration management, and fallback logic.
/// </summary>
public class ScheduleManagerService
{
    private readonly ILogger<ScheduleManagerService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleManagerService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ScheduleManagerService(ILogger<ScheduleManagerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the active schedule entry for the current time (or a specific time).
    /// If no schedule entry matches, returns null (indicating random fill mode).
    /// </summary>
    /// <param name="scheduleEntries">The configured schedule entries.</param>
    /// <param name="dateTime">The date/time to check. Defaults to now.</param>
    /// <returns>The matching schedule entry, or null if unscheduled.</returns>
    public ScheduleEntry? GetActiveScheduleEntry(
        List<ScheduleEntry> scheduleEntries,
        DateTime? dateTime = null)
    {
        var now = dateTime ?? DateTime.Now;
        var currentDay = now.DayOfWeek;
        var currentTime = now.TimeOfDay;

        // Only schedule Monday through Friday as per requirements
        if (currentDay < DayOfWeek.Monday || currentDay > DayOfWeek.Friday)
        {
            _logger.LogDebug("Current day {Day} is weekend, no scheduled programming", currentDay);
            return null;
        }

        // Find all enabled entries for the current day that encompass the current time
        var matchingEntries = scheduleEntries
            .Where(e => e.IsEnabled
                        && e.DayOfWeek == currentDay
                        && string.IsNullOrEmpty(e.PlaylistId))  // Has an assigned playlist
            .ToList();

        // Filter out entries that have a playlist assigned
        var entriesWithPlaylist = scheduleEntries
            .Where(e => e.IsEnabled
                        && e.DayOfWeek == currentDay
                        && !string.IsNullOrEmpty(e.PlaylistId))
            .ToList();

        foreach (var entry in entriesWithPlaylist)
        {
            var startTime = entry.GetStartTimeSpan();
            var endTime = entry.GetEndTimeSpan();

            if (currentTime >= startTime && currentTime < endTime)
            {
                _logger.LogInformation(
                    "Active schedule: \"{Name}\" ({Day} {Start}-{End}, Playlist: {Playlist})",
                    entry.DisplayName, entry.DayOfWeek, entry.StartTime, entry.EndTime, entry.PlaylistId);

                return entry;
            }
        }

        _logger.LogDebug("No active schedule entry for {Day} at {Time}", currentDay, currentTime);
        return null;
    }

    /// <summary>
    /// Calculates the remaining time in the current schedule slot.
    /// </summary>
    /// <param name="entry">The active schedule entry.</param>
    /// <returns>The remaining duration in the current time slot.</returns>
    public TimeSpan GetRemainingTimeInSlot(ScheduleEntry entry)
    {
        var now = DateTime.Now.TimeOfDay;
        var endTime = entry.GetEndTimeSpan();
        var remaining = endTime - now;

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Calculates the time until the next schedule entry begins.
    /// Useful for determining how long to play random music before a scheduled slot.
    /// </summary>
    /// <param name="scheduleEntries">All configured schedule entries.</param>
    /// <returns>The time until the next scheduled entry, or null if none exists today.</returns>
    public TimeSpan? GetTimeUntilNextScheduleEntry(List<ScheduleEntry> scheduleEntries)
    {
        var now = DateTime.Now;
        var currentDay = now.DayOfWeek;
        var currentTime = now.TimeOfDay;

        if (currentDay < DayOfWeek.Monday || currentDay > DayOfWeek.Friday)
        {
            // Weekend - calculate until Monday morning
            var daysUntilMonday = (int)DayOfWeek.Monday - (int)currentDay;
            if (daysUntilMonday <= 0) daysUntilMonday += 7;
            var mondayMorning = now.Date.AddDays(daysUntilMonday);
            return mondayMorning - now;
        }

        // Find the next entry today that starts after current time
        var nextEntries = scheduleEntries
            .Where(e => e.IsEnabled
                        && e.DayOfWeek == currentDay
                        && !string.IsNullOrEmpty(e.PlaylistId)
                        && e.GetStartTimeSpan() > currentTime)
            .OrderBy(e => e.GetStartTimeSpan())
            .ToList();

        if (nextEntries.Count > 0)
        {
            var nextStart = nextEntries.First().GetStartTimeSpan();
            return nextStart - currentTime;
        }

        // No more entries today; calculate until Monday
        var daysToMonday = (int)DayOfWeek.Monday - (int)currentDay;
        if (daysToMonday <= 0) daysToMonday += 7;
        var nextMonday = now.Date.AddDays(daysToMonday);
        return nextMonday - now;
    }

    /// <summary>
    /// Gets the next schedule change information (when current state ends).
    /// </summary>
    /// <param name="scheduleEntries">All configured schedule entries.</param>
    /// <returns>A tuple with the next change time and whether it's a scheduled playlist.</returns>
    public (DateTime NextChange, bool IsScheduledPlaylist) GetNextScheduleChange(
        List<ScheduleEntry> scheduleEntries)
    {
        var now = DateTime.Now;
        var activeEntry = GetActiveScheduleEntry(scheduleEntries, now);

        if (activeEntry != null)
        {
            // Currently in a scheduled slot - it ends at the entry's end time
            var endTime = activeEntry.GetEndTimeSpan();
            var nextChange = now.Date.Add(endTime);
            return (nextChange, true);
        }

        // Not in a scheduled slot - find when the next one starts
        var timeUntilNext = GetTimeUntilNextScheduleEntry(scheduleEntries);
        if (timeUntilNext.HasValue)
        {
            return (now + timeUntilNext.Value, true);
        }

        // No scheduled entries at all
        return (now + TimeSpan.FromDays(7), false);
    }

    /// <summary>
    /// Validates a schedule entry for correctness.
    /// Checks that times are valid and do not conflict with existing entries.
    /// </summary>
    /// <param name="entry">The schedule entry to validate.</param>
    /// <param name="allEntries">All existing schedule entries for conflict checking.</param>
    /// <returns>A list of validation error messages. Empty if valid.</returns>
    public List<string> ValidateScheduleEntry(ScheduleEntry entry, List<ScheduleEntry> allEntries)
    {
        var errors = new List<string>();

        if (entry.DayOfWeek < DayOfWeek.Monday || entry.DayOfWeek > DayOfWeek.Friday)
        {
            errors.Add("Scheduling is only available for Monday through Friday.");
        }

        if (!TimeSpan.TryParse(entry.StartTime, out _))
        {
            errors.Add($"Invalid start time format: {entry.StartTime}. Use HH:mm format.");
        }

        if (!TimeSpan.TryParse(entry.EndTime, out _))
        {
            errors.Add($"Invalid end time format: {entry.EndTime}. Use HH:mm format.");
        }

        if (TimeSpan.TryParse(entry.StartTime, out var start) && TimeSpan.TryParse(entry.EndTime, out var end))
        {
            if (start >= end)
            {
                errors.Add("End time must be after start time.");
            }
        }

        // Check for overlapping entries on the same day
        var overlapping = allEntries
            .Where(e => e.IsEnabled
                        && e.DayOfWeek == entry.DayOfWeek
                        && e.PlaylistId != entry.PlaylistId)
            .Where(e =>
            {
                var s1 = entry.GetStartTimeSpan();
                var e1 = entry.GetEndTimeSpan();
                var s2 = e.GetStartTimeSpan();
                var e2 = e.GetEndTimeSpan();
                return s1 < e2 && s2 < e1;
            })
            .ToList();

        foreach (var overlap in overlapping)
        {
            errors.Add($"Time overlaps with existing schedule: \"{overlap.DisplayName}\" ({overlap.StartTime}-{overlap.EndTime})");
        }

        return errors;
    }
}
