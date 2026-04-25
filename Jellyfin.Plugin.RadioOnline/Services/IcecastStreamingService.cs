using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Handles the actual streaming of audio data to an Icecast server.
/// Uses FFmpeg to encode audio and sends it via HTTP PUT to the Icecast mount point.
/// </summary>
public class IcecastStreamingService : IDisposable
{
    private readonly ILogger<IcecastStreamingService> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private Process? _ffmpegProcess;
    private bool _isStreaming;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IcecastStreamingService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="mediaEncoder">The media encoder service (FFmpeg access).</param>
    public IcecastStreamingService(ILogger<IcecastStreamingService> logger, IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Gets whether the service is currently streaming audio.
    /// </summary>
    public bool IsStreaming
    {
        get
        {
            lock (_lock)
            {
                return _isStreaming;
            }
        }
    }

    /// <summary>
    /// Starts streaming a single audio file to the configured Icecast server.
    /// </summary>
    /// <param name="filePath">The absolute path to the audio file to stream.</param>
    /// <param name="icecastUrl">The Icecast server URL.</param>
    /// <param name="icecastUsername">The Icecast source username.</param>
    /// <param name="icecastPassword">The Icecast source password.</param>
    /// <param name="icecastMountPoint">The Icecast mount point.</param>
    /// <param name="audioFormat">The target audio format ("m4a" or "ogg").</param>
    /// <param name="audioBitrate">The target audio bitrate in kbps.</param>
    /// <param name="streamName">The stream name metadata.</param>
    /// <param name="streamGenre">The stream genre metadata.</param>
    /// <param name="cancellationToken">Cancellation token to stop streaming.</param>
    /// <returns>A task representing the async operation. Returns true if completed normally.</returns>
    public async Task<bool> StreamFileAsync(
        string filePath,
        string icecastUrl,
        string icecastUsername,
        string icecastPassword,
        string icecastMountPoint,
        string audioFormat,
        int audioBitrate,
        string streamName,
        string streamGenre,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogError("Audio file not found: {FilePath}", filePath);
            return false;
        }

        lock (_lock)
        {
            if (_isStreaming)
            {
                _logger.LogWarning("Already streaming, stopping previous stream");
                StopStreamingInternal();
            }
        }

        var icecastSourceUrl = BuildIcecastSourceUrl(icecastUrl, icecastMountPoint);
        var ffmpegPath = _mediaEncoder.EncoderPath;

        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            _logger.LogError("FFmpeg not found at path: {FFmpegPath}", ffmpegPath);
            return false;
        }

        _logger.LogInformation(
            "Starting Icecast stream: {File} -> {Url} (format={Format}, bitrate={Bitrate}kbps)",
            Path.GetFileName(filePath), icecastSourceUrl, audioFormat, audioBitrate);

        var startInfo = BuildFFmpegArguments(
            ffmpegPath,
            filePath,
            icecastSourceUrl,
            icecastUsername,
            icecastPassword,
            audioFormat,
            audioBitrate,
            streamName,
            streamGenre);

        _ffmpegProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        _ffmpegProcess.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("FFmpeg stdout: {Data}", e.Data);
            }
        };

        _ffmpegProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogWarning("FFmpeg stderr: {Data}", e.Data);
            }
        };

        try
        {
            lock (_lock)
            {
                _isStreaming = true;
            }

            if (!_ffmpegProcess.Start())
            {
                _logger.LogError("Failed to start FFmpeg process");
                lock (_lock) { _isStreaming = false; }
                return false;
            }

            _ffmpegProcess.BeginOutputReadLine();
            _ffmpegProcess.BeginErrorReadLine();

            // Wait for the process to finish or be cancelled
            using (cancellationToken.Register(() =>
            {
                try
                {
                    if (!_ffmpegProcess.HasExited)
                    {
                        _logger.LogInformation("Cancelling FFmpeg stream for: {File}", Path.GetFileName(filePath));
                        _ffmpegProcess.Kill(true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Error killing FFmpeg: {Message}", ex.Message);
                }
            }))
            {
                await _ffmpegProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            var exitCode = _ffmpegProcess.ExitCode;
            lock (_lock)
            {
                _isStreaming = false;
            }

            if (exitCode == 0)
            {
                _logger.LogInformation("FFmpeg completed streaming: {File} (exit code: 0)", Path.GetFileName(filePath));
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("FFmpeg streaming cancelled for: {File}", Path.GetFileName(filePath));
                return true; // Cancellation is expected behavior, not an error
            }

            _logger.LogError("FFmpeg exited with code {ExitCode} for file: {File}", exitCode, Path.GetFileName(filePath));
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { _isStreaming = false; }
            _logger.LogInformation("Stream cancelled for: {File}", Path.GetFileName(filePath));
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock) { _isStreaming = false; }
            _logger.LogError(ex, "Error streaming file to Icecast: {File}", filePath);
            return false;
        }
        finally
        {
            CleanupProcess();
        }
    }

    /// <summary>
    /// Builds the Icecast source URL from server URL and mount point.
    /// </summary>
    private string BuildIcecastSourceUrl(string baseUrl, string mountPoint)
    {
        var url = baseUrl.TrimEnd('/');
        if (!mountPoint.StartsWith('/'))
        {
            mountPoint = '/' + mountPoint;
        }

        return $"{url}{mountPoint}";
    }

    /// <summary>
    /// Builds the FFmpeg process start info with the correct encoding arguments.
    /// </summary>
    private ProcessStartInfo BuildFFmpegArguments(
        string ffmpegPath,
        string inputFile,
        string icecastUrl,
        string username,
        string password,
        string format,
        int bitrate,
        string streamName,
        string streamGenre)
    {
        var arguments = new StringBuilder();

        // Input options
        arguments.Append("-hide_banner -loglevel warning ");
        arguments.Append("-re "); // Read input at native frame rate (real-time streaming)
        arguments.Append($"-i \"{inputFile}\" ");

        // Metadata for Icecast
        arguments.Append($"-metadata title=\"{EscapeMetadata(streamName)}\" ");
        arguments.Append($"-metadata genre=\"{EscapeMetadata(streamGenre)}\" ");
        arguments.Append("-metadata artist=\"Jellyfin Radio Online\" ");

        // Audio encoding options based on format
        if (format.Equals("m4a", StringComparison.OrdinalIgnoreCase))
        {
            // AAC in MPEG-4 container
            arguments.Append("-c:a aac ");
            arguments.Append($"-b:a {bitrate}k ");
            arguments.Append("-ar 44100 ");
            arguments.Append("-ac 2 ");
            arguments.Append("-f adts ");
        }
        else
        {
            // Ogg Vorbis format (default)
            arguments.Append("-c:a libvorbis ");
            arguments.Append($"-b:a {bitrate}k ");
            arguments.Append("-ar 44100 ");
            arguments.Append("-ac 2 ");
            arguments.Append("-f ogg ");
        }

        // Output to Icecast via HTTP PUT with source credentials
        var escapedPassword = password.Replace("\"", "\\\"");
        arguments.Append($"icecast://{username}:{escapedPassword}@{icecastUrl.TrimStart("http://".ToCharArray())}");

        _logger.LogDebug("FFmpeg arguments: {Arguments}", arguments.ToString());

        return new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }

    /// <summary>
    /// Escapes metadata string values for FFmpeg.
    /// </summary>
    private static string EscapeMetadata(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Stops the current streaming session.
    /// </summary>
    public void StopStreaming()
    {
        lock (_lock)
        {
            StopStreamingInternal();
        }
    }

    /// <summary>
    /// Internal method to stop streaming (caller must hold _lock).
    /// </summary>
    private void StopStreamingInternal()
    {
        if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
        {
            try
            {
                _ffmpegProcess.Kill(true);
                _logger.LogInformation("Stopped FFmpeg streaming process");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping FFmpeg process");
            }
        }

        _isStreaming = false;
    }

    /// <summary>
    /// Cleans up the FFmpeg process resources.
    /// </summary>
    private void CleanupProcess()
    {
        if (_ffmpegProcess != null)
        {
            _ffmpegProcess.OutputDataReceived -= null;
            _ffmpegProcess.ErrorDataReceived -= null;
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }
    }

    /// <summary>
    /// Disposes of the streaming service resources.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            StopStreamingInternal();
            CleanupProcess();
        }

        GC.SuppressFinalize(this);
    }
}
