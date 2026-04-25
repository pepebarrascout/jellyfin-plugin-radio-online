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
/// Handles streaming audio to an Icecast server using FFmpeg.
/// Uses the concat demuxer with -stream_loop -1 for continuous gapless playback
/// of an entire playlist. The playlist is written as an FFmpeg concat format file.
/// When the schedule changes, the process is killed and restarted with a new playlist.
/// </summary>
public class IcecastStreamingService : IDisposable
{
    private readonly ILogger<IcecastStreamingService> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private Process? _ffmpegProcess;
    private bool _isStreaming;
    private readonly object _lock = new();

    private const string WorkDir = "/tmp/radio-online";
    private const string PlaylistFile = "/tmp/radio-online/playlist.txt";
    private const string PlaylistTempFile = "/tmp/radio-online/playlist.txt.tmp";

    /// <summary>
    /// Initializes a new instance of the <see cref="IcecastStreamingService"/> class.
    /// </summary>
    public IcecastStreamingService(ILogger<IcecastStreamingService> logger, IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Gets whether FFmpeg is currently streaming.
    /// </summary>
    public bool IsStreaming
    {
        get
        {
            lock (_lock)
            {
                return _isStreaming && _ffmpegProcess != null && !_ffmpegProcess.HasExited;
            }
        }
    }

    /// <summary>
    /// Writes an FFmpeg concat playlist file and starts streaming it to Icecast.
    /// Uses -f concat with -safe 0 and -stream_loop -1 for continuous gapless playback.
    /// </summary>
    /// <param name="filePaths">Absolute paths to audio files in playlist order.</param>
    /// <param name="icecastUrl">Icecast server URL (e.g., http://server:8000).</param>
    /// <param name="icecastUsername">Icecast source username.</param>
    /// <param name="icecastPassword">Icecast source password.</param>
    /// <param name="icecastMountPoint">Icecast mount point (e.g., /radio).</param>
    /// <param name="audioFormat">Audio format: "ogg" or "m4a".</param>
    /// <param name="audioBitrate">Audio bitrate in kbps.</param>
    /// <param name="streamName">Stream name metadata.</param>
    /// <param name="streamGenre">Stream genre metadata.</param>
    /// <param name="cancellationToken">Token to cancel streaming.</param>
    /// <returns>True if streaming started successfully.</returns>
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
        // Stop any existing stream first
        StopStreaming();

        if (filePaths.Count == 0)
        {
            _logger.LogWarning("Cannot stream empty playlist");
            return false;
        }

        // Create work directory
        Directory.CreateDirectory(WorkDir);

        // Write concat playlist file
        WriteConcatPlaylist(filePaths);

        // Get FFmpeg path from Jellyfin
        var ffmpegPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            _logger.LogError("FFmpeg not found at path: {FFmpegPath}", ffmpegPath);
            return false;
        }

        // Build FFmpeg command:
        // -re: real-time pacing (critical for synchronized listening)
        // -stream_loop -1: loop the entire playlist infinitely
        // -f concat -safe 0: use concat demuxer with absolute paths
        var arguments = BuildConcatArguments(
            icecastUrl,
            icecastUsername,
            icecastPassword,
            icecastMountPoint,
            audioFormat,
            audioBitrate,
            streamName,
            streamGenre);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = WorkDir,
        };

        _logger.LogInformation(
            "Starting FFmpeg concat stream: {Count} tracks -> {Mount} ({Format}/{Bitrate}kbps)",
            filePaths.Count, icecastMountPoint, audioFormat, audioBitrate);

        lock (_lock)
        {
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

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    // Only log actual warnings/errors, not stats
                    if (e.Data.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                        e.Data.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                        e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("FFmpeg: {Data}", e.Data);
                    }
                }
            };

            _ffmpegProcess = process;

            if (!process.Start())
            {
                _logger.LogError("Failed to start FFmpeg process");
                lock (_lock) { _isStreaming = false; }
                _ffmpegProcess = null;
                return false;
            }

            process.BeginErrorReadLine();

            // Wait for the process to finish or be cancelled
            using (cancellationToken.Register(() =>
            {
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch { }
            }))
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_lock) { _isStreaming = false; }
            _ffmpegProcess = null;

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("FFmpeg concat stream stopped (schedule change or plugin stop)");
                return true;
            }

            // Process exited on its own - log it
            _logger.LogWarning("FFmpeg concat stream exited with code {ExitCode}", process.ExitCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { _isStreaming = false; }
            _ffmpegProcess = null;
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock) { _isStreaming = false; }
            _ffmpegProcess = null;
            _logger.LogError(ex, "Error during FFmpeg concat streaming");
            return false;
        }
        finally
        {
            CleanupProcess(process);
        }
    }

    /// <summary>
    /// Stops the current FFmpeg streaming process.
    /// </summary>
    public void StopStreaming()
    {
        lock (_lock)
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
            _ffmpegProcess = null;
        }
    }

    /// <summary>
    /// Writes an FFmpeg concat playlist file from the given file paths.
    /// Each line has the format: file '/absolute/path/to/audio.m4a'
    /// Single quotes within paths are properly escaped.
    /// </summary>
    private void WriteConcatPlaylist(List<string> filePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# FFmpeg concat playlist - Radio Online");

        foreach (var path in filePaths)
        {
            // Escape single quotes in the path for the concat format
            var escapedPath = path.Replace("'", "'\\''");
            sb.AppendLine($"file '{escapedPath}'");
        }

        // Atomic write: write to temp file, then move
        File.WriteAllText(PlaylistTempFile, sb.ToString());
        File.Move(PlaylistTempFile, PlaylistFile, overwrite: true);

        _logger.LogDebug("Concat playlist written: {Count} tracks at {Path}", filePaths.Count, PlaylistFile);
    }

    /// <summary>
    /// Builds FFmpeg arguments for concat demuxer streaming with continuous loop.
    /// </summary>
    private string BuildConcatArguments(
        string icecastUrl,
        string icecastUsername,
        string icecastPassword,
        string icecastMountPoint,
        string audioFormat,
        int audioBitrate,
        string streamName,
        string streamGenre)
    {
        var args = new StringBuilder();

        // Global options
        args.Append("-hide_banner -loglevel warning ");

        // -re: real-time pacing - read input at native frame rate
        // Critical for radio: without this, FFmpeg floods Icecast with data
        args.Append("-re ");

        // -stream_loop -1: loop the concat playlist infinitely
        args.Append("-stream_loop -1 ");

        // Input: concat demuxer with safe 0 (allows absolute paths)
        args.Append("-f concat -safe 0 ");
        args.Append($"-i \"{PlaylistFile}\" ");

        // Metadata for Icecast
        args.Append($"-metadata title=\"{EscapeMetadata(streamName)}\" ");
        args.Append($"-metadata genre=\"{EscapeMetadata(streamGenre)}\" ");
        args.Append("-metadata artist=\"Jellyfin Radio Online\" ");

        // Audio encoding
        if (audioFormat.Equals("m4a", StringComparison.OrdinalIgnoreCase))
        {
            args.Append("-c:a aac ");
            args.Append($"-b:a {audioBitrate}k ");
            args.Append("-ar 44100 ");
            args.Append("-ac 2 ");
            args.Append("-content_type audio/aac ");
            args.Append("-f adts ");
        }
        else
        {
            // OGG Vorbis (default)
            args.Append("-c:a libvorbis ");
            args.Append($"-b:a {audioBitrate}k ");
            args.Append("-ar 44100 ");
            args.Append("-ac 2 ");
            args.Append("-f ogg ");
        }

        // Output: Icecast via icecast:// protocol
        var escapedPassword = icecastPassword.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var cleanUrl = icecastUrl.Replace("http://", string.Empty).Replace("https://", string.Empty);
        var mount = icecastMountPoint;
        if (!mount.StartsWith('/'))
            mount = '/' + mount;

        args.Append($"icecast://{icecastUsername}:{escapedPassword}@{cleanUrl}{mount}");

        return args.ToString();
    }

    /// <summary>
    /// Escapes metadata string values for FFmpeg.
    /// </summary>
    private static string EscapeMetadata(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Cleans up process event handlers.
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
    /// Disposes of all resources.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            StopStreaming();
        }
        GC.SuppressFinalize(this);
    }
}
