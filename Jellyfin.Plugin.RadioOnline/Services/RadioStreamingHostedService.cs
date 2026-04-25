using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Background hosted service that manages the continuous radio streaming loop.
/// This service runs continuously while the plugin is enabled, managing the
/// schedule-aware playback cycle: checking the schedule, selecting playlists
/// or random music, streaming to Icecast, and handling transitions between slots.
/// </summary>
public class RadioStreamingHostedService : BackgroundService
{
    private readonly ILogger<RadioStreamingHostedService> _logger;
    private readonly IcecastStreamingService _icecastService;
    private readonly ScheduleManagerService _scheduleManager;
    private readonly AudioProviderService _audioProvider;
    private readonly Random _random = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioStreamingHostedService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="icecastService">The Icecast streaming service.</param>
    /// <param name="scheduleManager">The schedule manager service.</param>
    /// <param name="audioProvider">The audio provider service.</param>
    public RadioStreamingHostedService(
        ILogger<RadioStreamingHostedService> logger,
        IcecastStreamingService icecastService,
        ScheduleManagerService scheduleManager,
        AudioProviderService audioProvider)
    {
        _logger = logger;
        _icecastService = icecastService;
        _scheduleManager = scheduleManager;
        _audioProvider = audioProvider;
    }

    /// <summary>
    /// Executes the main radio streaming loop.
    /// Continuously checks the schedule and streams appropriate audio to Icecast.
    /// </summary>
    /// <param name="stoppingToken">The cancellation token for service shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Radio Online streaming service started");

        // Wait a bit for Jellyfin to fully initialize before starting
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
                    // Plugin not enabled, wait and check again
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Validate configuration
                if (!ValidateConfig(config))
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Check if we should be streaming right now
                await RunStreamingCycle(config, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in streaming cycle, retrying in 30 seconds");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Radio Online streaming service stopped");
    }

    /// <summary>
    /// Runs a single streaming cycle: determines what to play and streams it.
    /// </summary>
    private async Task RunStreamingCycle(PluginConfiguration config, CancellationToken cancellationToken)
    {
        // Get the active schedule entry for the current time
        var activeEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);

        List<Audio> audioQueue;

        if (activeEntry != null && !string.IsNullOrEmpty(activeEntry.PlaylistId))
        {
            // We are in a scheduled time slot with an assigned playlist
            var playlistItems = _audioProvider.GetPlaylistItems(activeEntry.PlaylistId, config.JellyfinUserId);

            if (playlistItems.Count > 0)
            {
                var remainingTime = _scheduleManager.GetRemainingTimeInSlot(activeEntry);
                var playlistDuration = _audioProvider.CalculateTotalDuration(playlistItems);

                if (playlistDuration <= remainingTime)
                {
                    // Playlist fits within the time slot - play it, then fill remainder with random
                    _logger.LogInformation(
                        "Playlist \"{Name}\" fits in slot ({Duration:F1}min / {Remaining:F1}min). Adding random fill.",
                        activeEntry.DisplayName, playlistDuration.TotalMinutes, remainingTime.TotalMinutes);

                    var randomFill = _audioProvider.GetRandomMusic(config.JellyfinUserId, config.RandomFillLimit);
                    var shuffledRandom = _audioProvider.ShuffleItems(randomFill);
                    audioQueue = playlistItems.Concat(shuffledRandom).ToList();
                }
                else
                {
                    // Playlist exceeds the time slot - trim it
                    _logger.LogInformation(
                        "Playlist \"{Name}\" exceeds slot ({Duration:F1}min / {Remaining:F1}min). Trimming to fit.",
                        activeEntry.DisplayName, playlistDuration.TotalMinutes, remainingTime.TotalMinutes);

                    audioQueue = TrimAudioToDuration(playlistItems, remainingTime);
                }
            }
            else
            {
                _logger.LogWarning("Playlist \"{Name}\" is empty or not found, using random music", activeEntry.DisplayName);
                audioQueue = _audioProvider.GetRandomMusic(config.JellyfinUserId, config.RandomFillLimit);
            }
        }
        else
        {
            // No active schedule - play random music
            _logger.LogInformation("No active schedule, playing random music");
            audioQueue = _audioProvider.GetRandomMusic(config.JellyfinUserId, config.RandomFillLimit);
        }

        if (audioQueue.Count == 0)
        {
            _logger.LogWarning("No audio items available to stream. Waiting 30 seconds.");
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Stream each audio file in sequence
        foreach (var audioItem in audioQueue)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Re-check the schedule before each track - schedule may have changed
            var currentEntry = _scheduleManager.GetActiveScheduleEntry(config.ScheduleEntries);
            if (currentEntry == null && activeEntry != null)
            {
                // We were in a scheduled slot but it ended
                _logger.LogInformation("Schedule slot ended, switching to new programming");
                break;
            }

            // If we're in a different slot now, break to re-evaluate
            if (currentEntry != null && activeEntry != null && currentEntry.PlaylistId != activeEntry.PlaylistId)
            {
                _logger.LogInformation("Schedule changed, switching to new playlist");
                break;
            }

            var filePath = _audioProvider.GetAudioFilePath(audioItem);
            if (filePath == null)
            {
                _logger.LogWarning("Skipping item with no valid path: {Name}", audioItem.Name);
                continue;
            }

            var success = await _icecastService.StreamFileAsync(
                filePath,
                config.IcecastUrl,
                config.IcecastUsername,
                config.IcecastPassword,
                config.IcecastMountPoint,
                config.AudioFormat,
                config.AudioBitrate,
                config.StreamName,
                config.StreamGenre,
                cancellationToken).ConfigureAwait(false);

            if (!success)
            {
                _logger.LogWarning("Failed to stream: {Name}. Moving to next track.", audioItem.Name);
            }

            // Small delay between tracks for Icecast to sync
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Trims a list of audio items to fit within a maximum duration.
    /// Items are included sequentially until the time limit would be exceeded.
    /// </summary>
    private List<Audio> TrimAudioToDuration(List<Audio> audioItems, TimeSpan maxDuration)
    {
        var result = new List<Audio>();
        var accumulated = TimeSpan.Zero;

        foreach (var item in audioItems)
        {
            if (!item.RunTimeTicks.HasValue || item.RunTimeTicks.Value <= 0)
            {
                result.Add(item);
                continue;
            }

            var itemDuration = TimeSpan.FromTicks(item.RunTimeTicks.Value);

            if (accumulated + itemDuration > maxDuration)
            {
                // This item would exceed the time limit - stop here
                break;
            }

            result.Add(item);
            accumulated += itemDuration;
        }

        _logger.LogInformation(
            "Trimmed playlist from {Original} to {Trimmed} items ({Duration:F1}min / {Max:F1}min)",
            audioItems.Count, result.Count, accumulated.TotalMinutes, maxDuration.TotalMinutes);

        return result;
    }

    /// <summary>
    /// Validates that the plugin configuration is complete and correct.
    /// </summary>
    private bool ValidateConfig(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.IcecastUrl))
        {
            _logger.LogWarning("Icecast URL is not configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.IcecastPassword))
        {
            _logger.LogWarning("Icecast password is not configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.IcecastMountPoint))
        {
            _logger.LogWarning("Icecast mount point is not configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.JellyfinUserId))
        {
            _logger.LogWarning("Jellyfin user ID is not configured");
            return false;
        }

        if (!config.AudioFormat.Equals("m4a", StringComparison.OrdinalIgnoreCase) &&
            !config.AudioFormat.Equals("ogg", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid audio format: {Format}. Use 'm4a' or 'ogg'", config.AudioFormat);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stops the streaming service gracefully.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Radio Online streaming service");
        _icecastService.StopStreaming();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
