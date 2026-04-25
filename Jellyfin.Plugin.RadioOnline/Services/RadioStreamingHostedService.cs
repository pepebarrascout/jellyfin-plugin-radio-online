using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the radio streaming loop using Liquidsoap.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly LiquidsoapStreamingService _liquidsoapService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;

    private const int ScheduleCheckIntervalSeconds = 5;
    private const int GracefulStopDelaySeconds = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        LiquidsoapStreamingService liquidsoapService,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider)
    {
        _logger = logger;
        _liquidsoapService = liquidsoapService;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
    }

    /// <summary>
    /// Executes the main radio streaming loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online service started (Liquidsoap mode)");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = Plugin.Instance?.Configuration as PluginConfiguration;

                if (config == null || !config.IsEnabled)
                {
                    if (_liquidsoapService.IsStreaming)
                    {
                        _logger.LogInformation("Plugin disabled, stopping Liquidsoap");
                        await StopLiquidsoapGracefullyAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!ValidateConfig(config))
                {
                    if (_liquidsoapService.IsStreaming)
                    {
                        await StopLiquidsoapGracefullyAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunStreamingCycle(config, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Streaming cycle error, retrying in 15s");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        if (_liquidsoapService.IsStreaming)
        {
            _liquidsoapService.StopStreaming();
        }

        _logger.LogInformation("Radio Online service stopped");
    }

    private async Task RunStreamingCycle(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

        if (activeEntry == null || string.IsNullOrEmpty(activeEntry.PlaylistId))
        {
            if (_liquidsoapService.IsStreaming)
            {
                _logger.LogInformation("No active schedule, stopping Liquidsoap");
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
            }

            var timeUntilNext = _scheduleManager.GetTimeUntilNextScheduleEntry(config.ScheduleEntries);
            if (timeUntilNext.HasValue && timeUntilNext.Value < TimeSpan.FromMinutes(5))
            {
                await Task.Delay(timeUntilNext.Value, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Start Liquidsoap if not running
        if (!_liquidsoapService.IsStreaming)
        {
            var started = await _liquidsoapService.StartStreamingAsync(config, cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                _logger.LogError("Liquidsoap failed to start, retrying in 30s");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Get playlist files
        var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);
        if (playlistItems.Count == 0)
        {
            _logger.LogWarning("Playlist \"{Name}\" empty or not found", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        var filePaths = new List<string>();
        foreach (var item in playlistItems)
        {
            var path = _audioProvider.GetAudioFilePath(item);
            if (path != null) filePaths.Add(path);
        }

        if (filePaths.Count == 0)
        {
            _logger.LogWarning("No valid files in playlist \"{Name}\"", activeEntry.DisplayName);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Streaming \"{Name}\" ({Day} {Start}-{End}) - {Count} tracks via Liquidsoap",
            activeEntry.DisplayName, activeEntry.DayOfWeek, activeEntry.StartTime, activeEntry.EndTime, filePaths.Count);

        await _liquidsoapService.SetPlaylistAsync(filePaths, cancellationToken).ConfigureAwait(false);

        await MonitorScheduleAsync(activeEntry, config, cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorScheduleAsync(
        ScheduleEntry activeEntry,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(ScheduleCheckIntervalSeconds), cancellationToken).ConfigureAwait(false);

            if (!_liquidsoapService.IsStreaming)
            {
                _logger.LogWarning("Liquidsoap died during monitoring for \"{Name}\"", activeEntry.DisplayName);
                break;
            }

            var currentConfig = Plugin.Instance?.Configuration as PluginConfiguration;
            if (currentConfig == null || !currentConfig.IsEnabled)
            {
                _logger.LogInformation("Plugin disabled, stopping Liquidsoap");
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            var currentEntry = _scheduleManager.GetActiveScheduleEntry(currentConfig.ScheduleEntries);

            if (currentEntry == null)
            {
                _logger.LogInformation("Schedule ended for \"{Name}\", stopping", activeEntry.DisplayName);
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }

            if (!string.Equals(currentEntry.PlaylistId, activeEntry.PlaylistId, StringComparison.Ordinal) ||
                !string.Equals(currentEntry.StartTime, activeEntry.StartTime, StringComparison.Ordinal))
            {
                _logger.LogInformation("Overlap: \"{New}\" takes priority over \"{Old}\"",
                    currentEntry.DisplayName, activeEntry.DisplayName);
                await StopLiquidsoapGracefullyAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task StopLiquidsoapGracefullyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _liquidsoapService.ClearPlaylistAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(GracefulStopDelaySeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful stop failed, forcing stop");
        }

        _liquidsoapService.StopStreaming();
    }

    private bool ValidateConfig(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.IcecastUrl))
        {
            _logger.LogWarning("Icecast URL not configured");
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.IcecastPassword))
        {
            _logger.LogWarning("Icecast password not configured");
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.IcecastMountPoint))
        {
            _logger.LogWarning("Icecast mount point not configured");
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.JellyfinUserId))
        {
            _logger.LogWarning("Jellyfin user ID not configured");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Stops the streaming service gracefully.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_liquidsoapService.IsStreaming)
        {
            _liquidsoapService.StopStreaming();
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
