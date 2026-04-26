using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _serviceProvider;
    private Process? _ffmpegProcess;
    private Process? _silenceProcess;
    private bool _isStreaming;
    private bool _isSilenceStreaming;
    private readonly object _lock = new();

    private const string WorkDir = "/tmp/radio-online";
    private const string PlaylistFile = "/tmp/radio-online/playlist.txt";
    private const string PlaylistTempFile = "/tmp/radio-online/playlist.txt.tmp";

    /// <summary>
    /// Initializes a new instance of the <see cref="IcecastStreamingService"/> class.
    /// Uses IServiceProvider to lazily resolve IMediaEncoder at runtime,
    /// avoiding DI failures if IMediaEncoder isn't ready during plugin initialization.
    /// </summary>
    public IcecastStreamingService(
        ILogger<IcecastStreamingService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
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
    /// Gets whether the silence bridge FFmpeg is currently streaming to the fallback mount.
    /// </summary>
    public bool IsSilenceStreaming
    {
        get
        {
            lock (_lock)
            {
                return _isSilenceStreaming && _silenceProcess != null && !_silenceProcess.HasExited;
            }
        }
    }

    /// <summary>
    /// Gets the FFmpeg binary path by resolving IMediaEncoder from Jellyfin,
    /// with fallback to common installation paths.
    /// </summary>
    private string GetFFmpegPath()
    {
        // Try Jellyfin's media encoder first
        try
        {
            var mediaEncoder = _serviceProvider.GetService<IMediaEncoder>();
            if (mediaEncoder != null && !string.IsNullOrEmpty(mediaEncoder.EncoderPath))
            {
                if (File.Exists(mediaEncoder.EncoderPath))
                {
                    return mediaEncoder.EncoderPath;
                }
                _logger.LogWarning("IMediaEncoder returned non-existent path: {Path}", mediaEncoder.EncoderPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve IMediaEncoder from DI");
        }

        // Fallback: common Jellyfin ffmpeg locations
        var fallbackPaths = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
            "/jellyfin/jellyfin-ffmpeg/ffmpeg",
        };

        foreach (var path in fallbackPaths)
        {
            if (File.Exists(path))
            {
                _logger.LogInformation("Using fallback FFmpeg path: {Path}", path);
                return path;
            }
        }

        _logger.LogError("FFmpeg not found. Streaming will not work.");
        return string.Empty;
    }

    /// <summary>
    /// Writes an FFmpeg concat playlist file and starts streaming it to Icecast.
    /// Uses -f concat with -safe 0 and -stream_loop -1 for continuous gapless playback.
    /// </summary>
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

        // Get FFmpeg path (lazy resolution)
        var ffmpegPath = GetFFmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            _logger.LogError("FFmpeg not found, cannot stream");
            return false;
        }

        // Build FFmpeg command
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
    /// Kills the process tree, waits for exit, then disposes it.
    /// </summary>
    public void StopStreaming()
    {
        Process? processToKill = null;

        lock (_lock)
        {
            processToKill = _ffmpegProcess;
            _ffmpegProcess = null;
            _isStreaming = false;
        }

        KillProcessAndWait(processToKill, "streaming");
    }

    /// <summary>
    /// Starts a silence FFmpeg process to the fallback mount point.
    /// This is used during schedule transitions to prevent listener disconnection.
    /// The silence mount is derived from the main mount: /radio becomes /radio-silence.
    /// Requires Icecast to be configured with fallback-mount pointing to this silence mount.
    /// </summary>
    /// <returns>True if the silence process started successfully.</returns>
    public bool StartSilence(
        string icecastUrl,
        string icecastUsername,
        string icecastPassword,
        string icecastMountPoint,
        string audioFormat,
        int audioBitrate)
    {
        StopSilence();

        var ffmpegPath = GetFFmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            _logger.LogError("FFmpeg not found, cannot start silence bridge");
            return false;
        }

        // Derive silence mount: /radio -> /radio-silence
        var silenceMount = icecastMountPoint.TrimEnd('/') + "-silence";
        if (!silenceMount.StartsWith('/'))
            silenceMount = '/' + silenceMount;

        var arguments = BuildSilenceArguments(
            icecastUrl, icecastUsername, icecastPassword,
            silenceMount, audioFormat, audioBitrate);

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

        try
        {
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data) &&
                    e.Data.Contains("Error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Silence FFmpeg: {Data}", e.Data);
                }
            };

            process.Exited += (_, _) =>
            {
                lock (_lock)
                {
                    _isSilenceStreaming = false;
                }
                _logger.LogInformation("Silence FFmpeg process exited");
            };

            if (!process.Start())
            {
                _logger.LogError("Failed to start silence FFmpeg process");
                return false;
            }

            process.BeginErrorReadLine();

            lock (_lock)
            {
                _silenceProcess = process;
                _isSilenceStreaming = true;
            }

            _logger.LogInformation(
                "Silence bridge started on {Mount} ({Format}/{Bitrate}kbps)",
                silenceMount, audioFormat, audioBitrate);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting silence FFmpeg");
            return false;
        }
    }

    /// <summary>
    /// Stops the silence bridge FFmpeg process.
    /// Kills the process tree, waits for it to exit, then disposes it.
    /// </summary>
    public void StopSilence()
    {
        Process? processToKill = null;

        lock (_lock)
        {
            processToKill = _silenceProcess;
            _silenceProcess = null;
            _isSilenceStreaming = false;
        }

        KillProcessAndWait(processToKill, "silence");
    }

    /// <summary>
    /// Kills a process (and its tree), waits for exit, and disposes it.
    /// On Linux, uses native kill command as fallback to ensure process tree is fully terminated.
    /// Icecast may keep mount points open if FFmpeg child processes survive the parent kill.
    /// </summary>
    private void KillProcessAndWait(Process? process, string label)
    {
        if (process == null) return;

        var pid = 0;
        try
        {
            pid = process.Id;
        }
        catch { }

        try
        {
            var hasExited = false;
            try { hasExited = process.HasExited; } catch { }

            if (!hasExited)
            {
                // Step 1: Try .NET Kill with process tree
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug("Kill(true) failed for {Label}: {Msg}", label, ex.Message);
                }

                try
                {
                    process.WaitForExit(5000);
                }
                catch (Exception) { }

                // Step 2: Verify exit, use native kill as fallback on Linux
                try { hasExited = process.HasExited; } catch { }

                if (!hasExited && pid > 0)
                {
                    _logger.LogWarning("{Label} FFmpeg (PID {Pid}) still alive after Kill(true), using native kill", label, pid);

                    // Kill the process group on Linux to catch any child processes
                    try
                    {
                        if (OperatingSystem.IsLinux())
                        {
                            // kill -- -pid kills the entire process group
                            var killProc = Process.Start(new ProcessStartInfo
                            {
                                FileName = "kill",
                                Arguments = $"-- -{pid}",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            });
                            killProc?.WaitForExit(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Native kill group failed for {Label}: {Msg}", label, ex.Message);
                    }

                    // Step 3: Direct SIGKILL on the process itself
                    try
                    {
                        if (OperatingSystem.IsLinux())
                        {
                            var killProc2 = Process.Start(new ProcessStartInfo
                            {
                                FileName = "kill",
                                Arguments = $"-9 {pid}",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            });
                            killProc2?.WaitForExit(3000);
                        }
                        else
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Final kill failed for {Label}: {Msg}", label, ex.Message);
                    }

                    try { process.WaitForExit(3000); } catch { }

                }

                // Final verification
                try { hasExited = process.HasExited; } catch { }
                if (hasExited)
                {
                    _logger.LogInformation("Stopped {Label} FFmpeg (PID {Pid})", label, pid);
                }
                else
                {
                    _logger.LogError("FAILED to stop {Label} FFmpeg (PID {Pid}) - process may be orphaned", label, pid);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping {Label} FFmpeg (PID {Pid})", label, pid);
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// Writes an FFmpeg concat playlist file from the given file paths.
    /// </summary>
    private void WriteConcatPlaylist(List<string> filePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# FFmpeg concat playlist - Radio Online");

        foreach (var path in filePaths)
        {
            var escapedPath = path.Replace("'", "'\\''");
            sb.AppendLine($"file '{escapedPath}'");
        }

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

        args.Append("-hide_banner -loglevel warning ");
        args.Append("-re ");
        args.Append("-stream_loop -1 ");
        args.Append("-f concat -safe 0 ");
        args.Append($"-i \"{PlaylistFile}\" ");

        args.Append($"-metadata title=\"{EscapeMetadata(streamName)}\" ");
        args.Append($"-metadata genre=\"{EscapeMetadata(streamGenre)}\" ");
        args.Append("-metadata artist=\"Jellyfin Radio Online\" ");

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
            args.Append("-c:a libvorbis ");
            args.Append($"-b:a {audioBitrate}k ");
            args.Append("-ar 44100 ");
            args.Append("-ac 2 ");
            args.Append("-f ogg ");
        }

        var escapedPassword = icecastPassword.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var cleanUrl = icecastUrl.Replace("http://", string.Empty).Replace("https://", string.Empty);
        var mount = icecastMountPoint;
        if (!mount.StartsWith('/'))
            mount = '/' + mount;

        args.Append($"icecast://{icecastUsername}:{escapedPassword}@{cleanUrl}{mount}");

        return args.ToString();
    }

    private static string EscapeMetadata(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Builds FFmpeg arguments for the silence bridge stream.
    /// Uses anullsrc to generate silence audio.
    /// </summary>
    private string BuildSilenceArguments(
        string icecastUrl,
        string icecastUsername,
        string icecastPassword,
        string silenceMount,
        string audioFormat,
        int audioBitrate)
    {
        var args = new StringBuilder();

        args.Append("-hide_banner -loglevel warning ");
        args.Append("-re ");
        args.Append("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 ");

        if (audioFormat.Equals("m4a", StringComparison.OrdinalIgnoreCase))
        {
            args.Append("-c:a aac ");
            args.Append($"-b:a {audioBitrate}k ");
            args.Append("-ar 44100 -ac 2 ");
            args.Append("-content_type audio/aac ");
            args.Append("-f adts ");
        }
        else
        {
            args.Append("-c:a libvorbis ");
            args.Append($"-b:a {audioBitrate}k ");
            args.Append("-ar 44100 -ac 2 ");
            args.Append("-f ogg ");
        }

        var escapedPassword = icecastPassword.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var cleanUrl = icecastUrl.Replace("http://", string.Empty).Replace("https://", string.Empty);

        args.Append($"icecast://{icecastUsername}:{escapedPassword}@{cleanUrl}{silenceMount}");

        return args.ToString();
    }

    private void CleanupProcess(Process? process)
    {
        if (process != null)
        {
            process.OutputDataReceived -= null;
            process.ErrorDataReceived -= null;
            process.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            StopStreaming();
            StopSilence();
        }
        GC.SuppressFinalize(this);
    }
}
