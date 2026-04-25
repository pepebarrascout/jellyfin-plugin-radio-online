using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Handles the actual streaming of audio data to an Icecast server.
/// Uses FFmpeg to encode audio and sends it via HTTP PUT to the Icecast mount point.
/// Supports both single-file streaming and gapless playlist streaming via concat demuxer.
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
    /// Streams a list of audio files to Icecast as a single continuous stream.
    /// Uses FFmpeg's concat demuxer for gapless transitions between tracks,
    /// avoiding the connection drop that occurs when starting separate FFmpeg processes.
    /// </summary>
    /// <param name="filePaths">List of absolute paths to audio files to stream in order.</param>
    /// <param name="icecastUrl">The Icecast server URL.</param>
    /// <param name="icecastUsername">The Icecast source username.</param>
    /// <param name="icecastPassword">The Icecast source password.</param>
    /// <param name="icecastMountPoint">The Icecast mount point.</param>
    /// <param name="audioFormat">The target audio format ("m4a" or "ogg").</param>
    /// <param name="audioBitrate">The target audio bitrate in kbps.</param>
    /// <param name="streamName">The stream name metadata.</param>
    /// <param name="streamGenre">The stream genre metadata.</param>
    /// <param name="cancellationToken">Cancellation token to stop streaming.</param>
    /// <returns>A task representing the async operation. Returns true if completed normally or cancelled.</returns>
    public async Task<bool> StreamPlaylistAsync(
        List<string> filePaths,
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
        if (filePaths == null || filePaths.Count == 0)
        {
            _logger.LogWarning("No files to stream");
            return false;
        }

        // Validate all files exist before starting FFmpeg
        var validPaths = new List<string>();
        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                validPaths.Add(path);
            }
            else
            {
                _logger.LogWarning("Skipping missing file: {FilePath}", path);
            }
        }

        if (validPaths.Count == 0)
        {
            _logger.LogError("No valid audio files found in playlist");
            return false;
        }

        _logger.LogInformation(
            "Starting gapless Icecast playlist stream: {Count} files -> {Mount} (format={Format}, bitrate={Bitrate}kbps)",
            validPaths.Count, icecastMountPoint, audioFormat, audioBitrate);

        // Create a temporary concat file list for FFmpeg's concat demuxer
        string? tempFileListPath = null;
        try
        {
            tempFileListPath = Path.GetTempFileName();
            // FFmpeg concat demuxer needs .txt extension
            var concatPath = tempFileListPath + ".txt";
            File.Move(tempFileListPath, concatPath);
            tempFileListPath = concatPath;

            // Write concat file list - each line: file 'path'
            var sb = new StringBuilder();
            foreach (var path in validPaths)
            {
                // Escape single quotes in file path
                var escapedPath = path.Replace("'", "'\\''");
                sb.AppendLine($"file '{escapedPath}'");
            }

            File.WriteAllText(tempFileListPath, sb.ToString());
            _logger.LogDebug("Created FFmpeg concat file list at: {Path} with {Count} entries", tempFileListPath, validPaths.Count);

            var icecastSourceUrl = BuildIcecastSourceUrl(icecastUrl, icecastMountPoint);
            var ffmpegPath = _mediaEncoder.EncoderPath;

            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                _logger.LogError("FFmpeg not found at path: {FFmpegPath}", ffmpegPath);
                return false;
            }

            var startInfo = BuildFFmpegConcatArguments(
                ffmpegPath,
                tempFileListPath,
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

            lock (_lock)
            {
                if (_isStreaming)
                {
                    _logger.LogWarning("Already streaming, stopping previous stream");
                    StopStreamingInternal();
                }

                _isStreaming = true;
            }

            if (!_ffmpegProcess.Start())
            {
                _logger.LogError("Failed to start FFmpeg concat process");
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
                    if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                    {
                        _logger.LogInformation("Cancelling FFmpeg concat stream");
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
                _logger.LogInformation("FFmpeg concat streaming completed successfully ({Count} files)", validPaths.Count);
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("FFmpeg concat streaming cancelled ({Count} files processed)", validPaths.Count);
                return true; // Cancellation is expected, not an error
            }

            _logger.LogError("FFmpeg concat exited with code {ExitCode}", exitCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { _isStreaming = false; }
            _logger.LogInformation("Playlist stream cancelled");
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock) { _isStreaming = false; }
            _logger.LogError(ex, "Error streaming playlist to Icecast");
            return false;
        }
        finally
        {
            CleanupProcess();

            // Clean up temp concat file
            if (tempFileListPath != null)
            {
                try
                {
                    if (File.Exists(tempFileListPath))
                    {
                        File.Delete(tempFileListPath);
                        _logger.LogDebug("Cleaned up temp concat file: {Path}", tempFileListPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Error cleaning up temp file: {Message}", ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Starts streaming a single audio file to the configured Icecast server.
    /// Kept for backwards compatibility; prefer StreamPlaylistAsync for gapless playback.
    /// </summary>
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
        return await StreamPlaylistAsync(
            new List<string> { filePath },
            icecastUrl,
            icecastUsername,
            icecastPassword,
            icecastMountPoint,
            audioFormat,
            audioBitrate,
            streamName,
            streamGenre,
            cancellationToken).ConfigureAwait(false);
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
    /// Builds FFmpeg process start info for concat demuxer streaming.
    /// Uses -f concat with -safe 0 to read a file list, producing gapless output.
    /// The -re flag ensures real-time pacing. The -ar 44100 -ac 2 options
    /// normalize audio output across different input formats for smooth transitions.
    /// </summary>
    private ProcessStartInfo BuildFFmpegConcatArguments(
        string ffmpegPath,
        string concatFilePath,
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

        // Concat demuxer input - reads the file list
        arguments.Append("-f concat -safe 0 ");
        arguments.Append($"-i \"{concatFilePath}\" ");

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

        _logger.LogDebug("FFmpeg concat arguments: {Arguments}", arguments.ToString());

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
