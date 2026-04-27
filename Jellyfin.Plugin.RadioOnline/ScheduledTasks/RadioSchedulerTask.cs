using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.ScheduledTasks;

/// <summary>
/// Scheduled task for the Radio Online plugin.
/// Provides dashboard visibility and manual trigger capability for the radio streaming service.
/// This task primarily serves as a health check and status reporter for the radio automation.
/// </summary>
public class RadioSchedulerTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogger<RadioSchedulerTask> _logger;
    private readonly ILocalizationManager _localizationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioSchedulerTask"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="localizationManager">The localization manager.</param>
    public RadioSchedulerTask(ILogger<RadioSchedulerTask> logger, ILocalizationManager localizationManager)
    {
        _logger = logger;
        _localizationManager = localizationManager;
    }

    /// <inheritdoc />
    public string Name => "Radio Online - Scheduler Check";

    /// <inheritdoc />
    public string Key => "RadioOnlineSchedulerCheck";

    /// <inheritdoc />
    public string Description =>
        "Checks the radio online schedule status and logs the current programming state. " +
        "The actual streaming runs as a background service.";

    /// <inheritdoc />
    public string Category => _localizationManager.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== Radio Online Schedule Status Check ===");

        try
        {
            var config = Plugin.Instance?.Configuration as Configuration.PluginConfiguration;

            if (config == null)
            {
                _logger.LogWarning("Radio Online plugin configuration not available");
                progress.Report(100);
                return Task.CompletedTask;
            }

            progress.Report(10);

            if (!config.IsEnabled)
            {
                _logger.LogInformation("Radio Online is currently DISABLED");
                progress.Report(100);
                return Task.CompletedTask;
            }

            _logger.LogInformation("Radio Online is ENABLED");
            _logger.LogInformation("  Liquidsoap Server: {Host}:{Port}", config.LiquidsoapHost, config.LiquidsoapPort);
            _logger.LogInformation("  Media Path: {MediaPath} -> {MusicPath}", config.JellyfinMediaPath, config.LiquidsoapMusicPath);
            _logger.LogInformation("  Schedule Entries: {Count}", config.ScheduleEntries.Count);

            progress.Report(50);

            foreach (var entry in config.ScheduleEntries)
            {
                _logger.LogInformation(
                    "  Schedule: {Day} {Start}-{End} -> Playlist: {Playlist} ({Status})",
                    entry.DayOfWeek, entry.StartTime, entry.EndTime,
                    string.IsNullOrEmpty(entry.PlaylistId) ? "Random" : entry.PlaylistId,
                    entry.IsEnabled ? "Active" : "Disabled");
            }

            _logger.LogInformation("=== End Status Check ===");
            progress.Report(100);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during radio scheduler check");
            throw;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks,
            },
        ];
    }
}
