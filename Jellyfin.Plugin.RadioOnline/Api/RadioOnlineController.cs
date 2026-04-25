using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Jellyfin.Plugin.RadioOnline.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RadioOnline.Api;

/// <summary>
/// API controller for the Radio Online plugin.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/RadioOnline")]
[Produces("application/json")]
public class RadioOnlineController : ControllerBase
{
    private readonly AudioProviderService _audioProvider;
    private readonly RadioStreamingHostedService _hostedService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioOnlineController"/> class.
    /// </summary>
    public RadioOnlineController(
        AudioProviderService audioProvider,
        RadioStreamingHostedService hostedService)
    {
        _audioProvider = audioProvider;
        _hostedService = hostedService;
    }

    /// <summary>
    /// Gets the current streaming status.
    /// </summary>
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
            isStreaming = _hostedService.IsStreaming,
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
    [HttpPost("ValidateSchedule")]
    public ActionResult<List<string>> ValidateSchedule([FromBody] ScheduleEntry entry)
    {
        var config = Plugin.Instance?.Configuration as PluginConfiguration;
        if (config == null)
        {
            return Ok(new List<string> { "Plugin not configured" });
        }

        var errors = new List<string>();

        if (!TimeSpan.TryParse(entry.StartTime, out var start))
            errors.Add("Invalid start time format. Use HH:mm.");
        if (!TimeSpan.TryParse(entry.EndTime, out var end))
            errors.Add("Invalid end time format. Use HH:mm.");
        if (start >= end)
            errors.Add("End time must be after start time.");

        return Ok(errors);
    }
}
