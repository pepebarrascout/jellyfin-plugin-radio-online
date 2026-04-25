using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RadioOnline.Api;

/// <summary>
/// API controller for the Radio Online plugin.
/// Provides endpoints for managing the radio streaming configuration,
/// schedule entries, and runtime status.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/RadioOnline")]
[Produces("application/json")]
public class RadioOnlineController : ControllerBase
{
    private readonly AudioProviderService _audioProvider;
    private readonly IcecastStreamingService _icecastService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioOnlineController"/> class.
    /// </summary>
    /// <param name="audioProvider">The audio provider service.</param>
    /// <param name="icecastService">The Icecast streaming service.</param>
    public RadioOnlineController(AudioProviderService audioProvider, IcecastStreamingService icecastService)
    {
        _audioProvider = audioProvider;
        _icecastService = icecastService;
    }

    /// <summary>
    /// Gets the current streaming status.
    /// </summary>
    /// <returns>The current streaming status.</returns>
    [HttpGet("Status")]
    public ActionResult<object> GetStatus()
    {
        var config = Plugin.Instance?.Configuration as PluginConfiguration;
        if (config == null)
        {
            return NotFound(new { error = "Plugin not found" });
        }

        return Ok(new
        {
            isEnabled = config.IsEnabled,
            isStreaming = _icecastService.IsStreaming,
            icecastUrl = config.IcecastUrl,
            mountPoint = config.IcecastMountPoint,
            audioFormat = config.AudioFormat,
            audioBitrate = config.AudioBitrate,
            scheduleEntriesCount = config.ScheduleEntries.Count,
        });
    }

    /// <summary>
    /// Gets all available playlists from Jellyfin.
    /// </summary>
    /// <returns>A list of playlist IDs and names.</returns>
    [HttpGet("Playlists")]
    public ActionResult<List<object>> GetPlaylists()
    {
        var config = Plugin.Instance?.Configuration as PluginConfiguration;
        if (config == null || string.IsNullOrEmpty(config.JellyfinUserId))
        {
            return Ok(new List<object>());
        }

        var playlists = _audioProvider.GetAvailablePlaylists(config.JellyfinUserId);
        var result = playlists.Select(p => new { id = p.Id, name = p.Name }).Cast<object>().ToList();
        return Ok(result);
    }

    /// <summary>
    /// Validates a schedule entry.
    /// </summary>
    /// <param name="entry">The schedule entry to validate.</param>
    /// <returns>A list of validation errors (empty if valid).</returns>
    [HttpPost("ValidateSchedule")]
    public ActionResult<List<string>> ValidateSchedule([FromBody] ScheduleEntry entry)
    {
        var config = Plugin.Instance?.Configuration as PluginConfiguration;
        if (config == null)
        {
            return Ok(new List<string> { "Plugin not configured" });
        }

        // Use a simple validation - supports all 7 days of the week
        var errors = new List<string>();

        if (!TimeSpan.TryParse(entry.StartTime, out var start))
        {
            errors.Add("Invalid start time format. Use HH:mm.");
        }

        if (!TimeSpan.TryParse(entry.EndTime, out var end))
        {
            errors.Add("Invalid end time format. Use HH:mm.");
        }

        if (start >= end)
        {
            errors.Add("End time must be after start time.");
        }

        return Ok(errors);
    }
}
