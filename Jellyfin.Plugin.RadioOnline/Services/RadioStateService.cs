using System;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Thread-safe shared state service for radio streaming.
/// Decouples the background hosted service from API controllers,
/// avoiding DI resolution issues with hosted services in Jellyfin.
/// </summary>
public class RadioStateService
{
    private volatile bool _isStreaming;
    private readonly object _nowPlayingLock = new();
    private NowPlayingInfo? _nowPlaying;

    /// <summary>
    /// Gets or sets whether the radio is currently streaming tracks to Liquidsoap.
    /// Written by RadioStreamingHostedService, read by RadioOnlineController.
    /// </summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => _isStreaming = value;
    }

    /// <summary>
    /// Stores the currently playing track info for the NowPlaying endpoint.
    /// Thread-safe.
    /// </summary>
    public NowPlayingInfo? CurrentTrack
    {
        get
        {
            lock (_nowPlayingLock) { return _nowPlaying; }
        }
        set
        {
            lock (_nowPlayingLock) { _nowPlaying = value; }
        }
    }
}
