using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Manages the weekly radio schedule by determining which playlist should play
/// at any given time based on the configured schedule entries.
/// Handles time slot matching, overlap resolution, and fallback logic.
/// When two schedules overlap, the one with the latest start time takes priority.
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
    /// If multiple entries are active at the same time (overlap), the entry with
    /// the latest start time takes priority, as it represents the newer program
    /// that should preempt the earlier one.
    /// Returns null if no schedule entry matches (indicating no streaming).
    /// Supports all 7 days of the week including Saturday and Sunday.
    /// </summary>
    /// <param name="scheduleEntries">The configured schedule entries.</param>
    /// <param name="dateTime">The date/time to check. Defaults to now.</param>
    /// <returns>The matching schedule entry with highest priority, or null if unscheduled.</returns>
    public ScheduleEntry? GetActiveScheduleEntry(
        List<ScheduleEntry> scheduleEntries,
        DateTime? dateTime = null)
    {
        var now = dateTime ?? DateTime.Now;
        var currentDay = now.DayOfWeek;
        var currentTime = now.TimeOfDay;

        // Find all enabled entries for the current day with a playlist assigned
        // that are active at the current time
        var activeEntries = scheduleEntries
            .Where(e => e.IsEnabled
                        && e.DayOfWeek == currentDay
                        && !string.IsNullOrEmpty(e.PlaylistId))
            .Where(e =>
            {
                var startTime = e.GetStartTimeSpan();
                var endTime = e.GetEndTimeSpan();
                return currentTime >= startTime && currentTime < endTime;
            })
            .ToList();

        if (activeEntries.Count == 0)
        {
            return null;
        }

        if (activeEntries.Count == 1)
        {
            var entry = activeEntries[0];
            _logger.LogInformation("Active: \"{Name}\" ({Day} {Start}-{End})",
                entry.DisplayName, entry.DayOfWeek, entry.StartTime, entry.EndTime);
            return entry;
        }

        // Multiple entries overlap - the one with the LATEST start time wins
        // (e.g., if Lista 1 is 10:00-10:35 and Lista 2 is 10:30-11:00,
        // at 10:30 Lista 2 starts and has priority over Lista 1)
        var winner = activeEntries
            .OrderByDescending(e => e.GetStartTimeSpan())
            .First();

        _logger.LogInformation(
            "Schedule overlap detected at {Time}: {Count} entries active. Selected \"{Name}\" ({Start}-{End}) as it starts latest.",
            currentTime, activeEntries.Count, winner.DisplayName, winner.StartTime, winner.EndTime);

        return winner;
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
    /// Searches across all 7 days, wrapping to the next week if needed.
    /// </summary>
    /// <param name="scheduleEntries">All configured schedule entries.</param>
    /// <returns>The time until the next scheduled entry, or null if none exists.</returns>
    public TimeSpan? GetTimeUntilNextScheduleEntry(List<ScheduleEntry> scheduleEntries)
    {
        var now = DateTime.Now;
        var currentTime = now.TimeOfDay;

        for (var daysAhead = 0; daysAhead < 7; daysAhead++)
        {
            var checkDate = now.Date.AddDays(daysAhead);
            var checkDay = checkDate.DayOfWeek;

            var nextEntries = scheduleEntries
                .Where(e => e.IsEnabled
                            && e.DayOfWeek == checkDay
                            && !string.IsNullOrEmpty(e.PlaylistId))
                .OrderBy(e => e.GetStartTimeSpan())
                .ToList();

            foreach (var entry in nextEntries)
            {
                var entryStart = entry.GetStartTimeSpan();
                var entryDateTime = checkDate.Add(entryStart);

                if (entryDateTime > now)
                {
                    return entryDateTime - now;
                }
            }
        }

        return null;
    }
}
