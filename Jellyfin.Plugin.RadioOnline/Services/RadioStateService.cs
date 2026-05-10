namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Thread-safe shared state service for radio streaming.
/// Decouples the background hosted service from API controllers,
/// avoiding DI resolution issues with hosted services in Jellyfin.
/// </summary>
public class RadioStateService
{
    private volatile bool _isStreaming;

    /// <summary>
    /// Gets or sets whether the radio is currently streaming tracks to Liquidsoap.
    /// Written by RadioStreamingHostedService, read by RadioOnlineController.
    /// </summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => _isStreaming = value;
    }
}
