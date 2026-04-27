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
    private readonly RadioStateService _state;
    private readonly LiquidsoapClient _liquidsoapClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioOnlineController"/> class.
    /// </summary>
    public RadioOnlineController(
        AudioProviderService audioProvider,
        RadioStateService state,
        LiquidsoapClient liquidsoapClient)
    {
        _audioProvider = audioProvider;
        _state = state;
        _liquidsoapClient = liquidsoapClient;
    }

    /// <summary>
    /// Gets the current streaming status.
    /// </summary>
    [HttpGet("Status")]
    public async Task<ActionResult<object>> GetStatus()
    {
        var config = Plugin.Instance?.Configuration as PluginConfiguration;
        if (config == null)
        {
            return NotFound(new { error = "Plugin not found" });
        }

        var liquidsoapConnected = false;
        var liquidsoapStatus = string.Empty;
        try
        {
            liquidsoapConnected = await _liquidsoapClient.TestConnectionAsync().ConfigureAwait(false);
            if (liquidsoapConnected)
            {
                liquidsoapStatus = await _liquidsoapClient.GetStatusAsync().ConfigureAwait(false);
            }
        }
        catch { }

        return Ok(new
        {
            isEnabled = config.IsEnabled,
            isStreaming = _state.IsStreaming,
            liquidsoapConnected,
            liquidsoapStatus,
            liquidsoapHost = config.LiquidsoapHost,
            liquidsoapPort = config.LiquidsoapPort,
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
