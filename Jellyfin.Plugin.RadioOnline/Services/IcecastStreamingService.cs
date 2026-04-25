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
/// Streams one audio file at a time using FFmpeg with the -re flag for real-time pacing.
/// This ensures all listeners hear synchronized audio and allows schedule checks between tracks.
/// The calling service (RadioStreamingHostedService) is responsible for iterating through
/// playlist tracks and checking the schedule between each one.
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
    /// Gets the file name of the currently streaming track, or null if not streaming.
    /// </summary>
    public string? CurrentTrackName { get; private set; }

    /// <summary>
    /// Streams a single audio file to Icecast in real-time.
    /// The -re flag forces FFmpeg to read the input at its native frame rate,
    /// ensuring the output is paced in real-time. This is critical because:
    /// - Without -re, FFmpeg encodes as fast as possible, flooding Icecast with data.
    ///   Icecast buffers this data, and different clients connecting at different times
    ///   hear completely different parts of the playlist (massive desynchronization).
    /// - With -re, FFmpeg reads at the audio's natural speed (e.g., 1 second of audio
    ///   takes 1 second to process), so all clients hear the same audio in real-time,
    ///   just like a traditional radio broadcast.
    /// </summary>
    /// <param name="filePath">Absolute path to the audio file to stream.</param>
    /// <param name="icecastUrl">The Icecast server URL.</param>
    /// <param name="icecastUsername">The Icecast source username.</param>
    /// <param name="icecastPassword">The Icecast source password.</param>
    /// <param name="icecastMountPoint">The Icecast mount point.</param>
    /// <param name="audioFormat">The target audio format ("m4a" or "ogg").</param>
    /// <param name="audioBitrate">The target audio bitrate in kbps.</param>
    /// <param name="streamName">The stream name metadata.</param>
    /// <param name="streamGenre">The stream genre metadata.</param>
    /// <param name="cancellationToken">Cancellation token to stop streaming.</param>
    /// <returns>True if the file was streamed successfully or cancelled; false on error.</returns>
    public async Task<bool> StreamSingleFileAsync(
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
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _logger.LogWarning("File not found: {FilePath}", filePath);
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        CurrentTrackName = fileName;

        _logger.LogInformation(
            "Starting real-time stream: {FileName} -> {Mount} ({Format}/{Bitrate}kbps)",
            fileName, icecastMountPoint, audioFormat, audioBitrate);

        var icecastSourceUrl = BuildIcecastSourceUrl(icecastUrl, icecastMountPoint);
        var ffmpegPath = _mediaEncoder.EncoderPath;

        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            _logger.LogError("FFmpeg not found at path: {FFmpegPath}", ffmpegPath);
            CurrentTrackName = null;
            return false;
        }

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

        lock (_lock)
        {
            if (_isStreaming)
            {
                _logger.LogWarning("Already streaming, stopping previous stream");
                StopStreamingInternal();
            }

            _isStreaming = true;
        }

        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogDebug("FFmpeg stdout: {Data}", e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogDebug("FFmpeg stderr: {Data}", e.Data);
                }
            };

            _ffmpegProcess = process;

            if (!process.Start())
            {
                _logger.LogError("Failed to start FFmpeg process for {FileName}", fileName);
                lock (_lock) { _isStreaming = false; }
                CurrentTrackName = null;
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for the process to finish or be cancelled
            using (cancellationToken.Register(() =>
            {
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        _logger.LogInformation("Cancelling FFmpeg stream for {FileName}", fileName);
                        process.Kill(true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Error killing FFmpeg: {Message}", ex.Message);
                }
            }))
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            var exitCode = process.ExitCode;
            lock (_lock)
            {
                _isStreaming = false;
            }
            CurrentTrackName = null;

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("FFmpeg stream cancelled for {FileName}", fileName);
                return true; // Cancellation is expected, not an error
            }

            if (exitCode == 0)
            {
                _logger.LogInformation("FFmpeg stream completed: {FileName}", fileName);
                return true;
            }

            _logger.LogError("FFmpeg exited with code {ExitCode} for {FileName}", exitCode, fileName);
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { _isStreaming = false; }
            CurrentTrackName = null;
            _logger.LogInformation("Stream cancelled for {FileName}", fileName);
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock) { _isStreaming = false; }
            CurrentTrackName = null;
            _logger.LogError(ex, "Error streaming {FileName} to Icecast", fileName);
            return false;
        }
        finally
        {
            _ffmpegProcess = null;
            CleanupProcess(process);
        }
    }

    /// <summary>
    /// Builds FFmpeg process start info for single-file streaming.
    /// The -re flag is critical: it limits reading speed to the input's native frame rate,
    /// producing output in real-time. Without it, FFmpeg processes as fast as CPU allows,
    /// which breaks client synchronization when streaming to Icecast.
    /// The -ar 44100 -ac 2 options normalize audio output for consistent playback.
    /// </summary>
    private ProcessStartInfo BuildFFmpegArguments(
        string ffmpegPath,
        string inputFilePath,
        string icecastUrl,
        string username,
        string password,
        string format,
        int bitrate,
        string streamName,
        string streamGenre)
    {
        var arguments = new StringBuilder();

        // Global options
        arguments.Append("-hide_banner -loglevel warning ");

        // -re: REAL-TIME PACING - Read input at its native frame rate.
        // This is the most important flag for radio streaming. Without it,
        // FFmpeg encodes at maximum CPU speed, flooding the Icecast buffer.
        // Listeners connecting at different times would hear different audio.
        arguments.Append("-re ");

        // Input file
        arguments.Append($"-i \"{inputFilePath}\" ");

        // Metadata for Icecast
        arguments.Append($"-metadata title=\"{EscapeMetadata(streamName)}\" ");
        arguments.Append($"-metadata genre=\"{EscapeMetadata(streamGenre)}\" ");
        arguments.Append("-metadata artist=\"Jellyfin Radio Online\" ");

        // Audio encoding options based on format
        if (format.Equals("m4a", StringComparison.OrdinalIgnoreCase))
        {
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

        // Output to Icecast via icecast:// protocol with source credentials
        var escapedPassword = password.Replace("\"", "\\\"");
        var cleanUrl = icecastUrl.Replace("http://", string.Empty).Replace("https://", string.Empty);
        arguments.Append($"icecast://{username}:{escapedPassword}@{cleanUrl}");

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
        CurrentTrackName = null;
    }

    /// <summary>
    /// Cleans up a specific FFmpeg process resources.
    /// </summary>
    private void CleanupProcess(Process? process)
    {
        if (process != null)
        {
            process.OutputDataReceived -= null;
            process.ErrorDataReceived -= null;
            process.Dispose();
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
            CleanupProcess(_ffmpegProcess);
        }

        GC.SuppressFinalize(this);
    }
}
