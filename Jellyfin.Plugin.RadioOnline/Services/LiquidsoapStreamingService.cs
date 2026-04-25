using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RadioOnline.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Manages a persistent Liquidsoap process for continuous Icecast streaming.
/// Liquidsoap stays connected to Icecast at all times, eliminating gaps between tracks.
/// </summary>
public class LiquidsoapStreamingService : IDisposable
{
    private readonly ILogger<LiquidsoapStreamingService> _logger;
    private Process? _liquidsoapProcess;
    private HttpClient? _httpClient;
    private readonly object _lock = new();
    private bool _isRunning;
    private int _controlPort;

    private const string WorkDir = "/tmp/radio-online";
    private const string PlaylistFile = "/tmp/radio-online/playlist.m3u";
    private const string PlaylistTempFile = "/tmp/radio-online/playlist.m3u.tmp";
    private const string ScriptFile = "/tmp/radio-online/radio.liq";

    /// <summary>
    /// Common absolute paths where liquidsoap binary is typically installed.
    /// Jellyfin runs as a systemd service with restricted PATH, so we cannot
    /// rely on Environment.GetEnvironmentVariable("PATH") alone.
    /// </summary>
    private static readonly string[] KnownLiquidsoapPaths =
    {
        "/usr/bin/liquidsoap",
        "/usr/local/bin/liquidsoap",
        "/bin/liquidsoap",
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="LiquidsoapStreamingService"/> class.
    /// </summary>
    public LiquidsoapStreamingService(ILogger<LiquidsoapStreamingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets whether Liquidsoap is currently running and streaming.
    /// </summary>
    public bool IsStreaming
    {
        get
        {
            lock (_lock)
            {
                return _isRunning && _liquidsoapProcess != null && !_liquidsoapProcess.HasExited;
            }
        }
    }

    /// <summary>
    /// Gets the current Liquidsoap control port.
    /// </summary>
    public int ControlPort => _controlPort;

    /// <summary>
    /// Starts the Liquidsoap process with the given configuration.
    /// </summary>
    public async Task<bool> StartStreamingAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_isRunning && _liquidsoapProcess != null && !_liquidsoapProcess.HasExited)
            {
                _logger.LogInformation("Liquidsoap already running (PID: {Pid}, port: {Port})", _liquidsoapProcess.Id, _controlPort);
                return true;
            }
        }

        Directory.CreateDirectory(WorkDir);

        _controlPort = FindAvailablePort(12345, 12355);

        // Generate and validate the .liq script
        var script = GenerateScript(config);
        await File.WriteAllTextAsync(ScriptFile, script, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Liquidsoap script generated at {Path}", ScriptFile);

        // Find liquidsoap binary (check known absolute paths first, then PATH)
        var liquidsoapPath = FindLiquidsoapBinary();
        if (string.IsNullOrEmpty(liquidsoapPath))
        {
            _logger.LogError("Liquidsoap binary not found. Install it: apt install liquidsoap");
            return false;
        }
        _logger.LogInformation("Found liquidsoap at: {Path}", liquidsoapPath);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var startInfo = new ProcessStartInfo
        {
            FileName = liquidsoapPath,
            Arguments = $"\"{ScriptFile}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = WorkDir,
        };

        _liquidsoapProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        // Collect stderr to detect startup errors
        var errorBuilder = new StringBuilder();

        _liquidsoapProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        _logger.LogInformation("Starting Liquidsoap (port: {Port}, format: {Format}/{Bitrate}kbps)",
            _controlPort, config.AudioFormat, config.AudioBitrate);

        try
        {
            if (!_liquidsoapProcess.Start())
            {
                _logger.LogError("Failed to start Liquidsoap process");
                CleanupProcess();
                return false;
            }

            _liquidsoapProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception starting Liquidsoap");
            CleanupProcess();
            return false;
        }

        // Wait for control port to respond
        if (!await WaitForReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            // Check if process already exited (script error)
            if (_liquidsoapProcess.HasExited)
            {
                var errors = errorBuilder.ToString().Trim();
                _logger.LogError("Liquidsoap exited immediately with code {Code}. Errors:\n{Errors}",
                    _liquidsoapProcess.ExitCode, string.IsNullOrEmpty(errors) ? "(none captured)" : errors);
            }
            else
            {
                _logger.LogError("Liquidsoap control port {Port} not responding after 10s", _controlPort);
                // Still running but not responding - kill it
                try { _liquidsoapProcess.Kill(true); } catch { }
            }

            CleanupProcess();
            return false;
        }

        // Log any warnings from Liquidsoap startup
        var startupErrors = errorBuilder.ToString().Trim();
        if (!string.IsNullOrEmpty(startupErrors))
        {
            _logger.LogWarning("Liquidsoap startup messages:\n{Messages}", startupErrors);
        }

        lock (_lock) { _isRunning = true; }

        _logger.LogInformation("Liquidsoap started OK (PID: {Pid}, port: {Port})", _liquidsoapProcess.Id, _controlPort);
        return true;
    }

    /// <summary>
    /// Writes the playlist M3U file. Liquidsoap detects changes via inotify.
    /// </summary>
    public async Task SetPlaylistAsync(List<string> filePaths, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var path in filePaths)
        {
            sb.AppendLine(path);
        }

        await File.WriteAllTextAsync(PlaylistTempFile, sb.ToString(), cancellationToken).ConfigureAwait(false);
        File.Move(PlaylistTempFile, PlaylistFile, overwrite: true);

        _logger.LogInformation("Playlist written: {Count} tracks", filePaths.Count);
    }

    /// <summary>
    /// Clears the playlist.
    /// </summary>
    public async Task ClearPlaylistAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(PlaylistTempFile, "#EXTM3U\n", cancellationToken).ConfigureAwait(false);
        File.Move(PlaylistTempFile, PlaylistFile, overwrite: true);
    }

    /// <summary>
    /// Stops the Liquidsoap process and releases resources.
    /// </summary>
    public void StopStreaming()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        if (_liquidsoapProcess != null && !_liquidsoapProcess.HasExited)
        {
            try
            {
                _liquidsoapProcess.Kill(false);
                if (!_liquidsoapProcess.WaitForExit(3000))
                {
                    _liquidsoapProcess.Kill(true);
                }
                _logger.LogInformation("Liquidsoap stopped (PID: {Pid})", _liquidsoapProcess.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping Liquidsoap");
            }
        }

        _isRunning = false;
        CleanupProcess();
    }

    private void CleanupProcess()
    {
        if (_liquidsoapProcess != null)
        {
            _liquidsoapProcess.ErrorDataReceived -= null;
            _liquidsoapProcess.Dispose();
            _liquidsoapProcess = null;
        }

        if (_httpClient != null)
        {
            _httpClient.Dispose();
            _httpClient = null;
        }
    }

    /// <summary>
    /// Generates a Liquidsoap 2.x compatible .liq script.
    /// Uses simplified crossfade and standard output.icecast syntax.
    /// </summary>
    private string GenerateScript(PluginConfiguration config)
    {
        var host = ParseHost(config.IcecastUrl);
        var port = ParsePort(config.IcecastUrl);

        var sb = new StringBuilder();
        sb.AppendLine("# Radio Online Plugin - Liquidsoap script");
        sb.AppendLine($"set(\"server.telnet\", true)");
        sb.AppendLine($"set(\"server.telnet.port\", {_controlPort})");
        sb.AppendLine($"set(\"log.stdout\", true)");
        sb.AppendLine($"set(\"log.level\", 3)");
        sb.AppendLine();

        // Playlist source - normal mode (sequential), watch for file changes
        sb.AppendLine("music = playlist(");
        sb.AppendLine("  id=\"radio\",");
        sb.AppendLine("  mode=\"normal\",");
        sb.AppendLine("  reload_mode=\"watch\",");
        sb.AppendLine("  reload_interval=1,");
        sb.AppendLine($"  \"{PlaylistFile}\"");
        sb.AppendLine(")");
        sb.AppendLine();

        // Simple crossfade - Liquidsoap 2.x compatible
        sb.AppendLine("music = crossfade(music)");
        sb.AppendLine();

        // Safety wrapper for empty playlist
        sb.AppendLine("radio = mksafe(music)");
        sb.AppendLine();

        // Output to Icecast
        sb.Append("output.icecast(");

        if (config.AudioFormat.Equals("ogg", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"%ogg(%vorbis),");
        }
        else if (config.AudioFormat.Equals("m4a", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"%ffmpeg(%audio(codec=\"aac\",b=\"{config.AudioBitrate}k\")),");
        }
        else
        {
            sb.AppendLine($"%ogg(%vorbis),");
        }

        sb.AppendLine($"  host=\"{host}\",");
        sb.AppendLine($"  port={port},");
        sb.AppendLine($"  password=\"{config.IcecastPassword.Replace("\\", "\\\\").Replace("\"", "\\\"")}\",");
        sb.AppendLine($"  mount=\"{config.IcecastMountPoint}\",");
        sb.AppendLine($"  name=\"{config.StreamName.Replace("\"", "\\\"")}\",");
        sb.AppendLine($"  genre=\"{config.StreamGenre.Replace("\"", "\\\"")}\"");
        sb.AppendLine("  radio)");
        sb.AppendLine();

        return sb.ToString();
    }

    private async Task<bool> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < 20; i++) // 20 * 500ms = 10s
        {
            if (cancellationToken.IsCancellationRequested) return false;

            try
            {
                if (_httpClient != null)
                {
                    var response = await _httpClient.GetAsync(
                        $"http://localhost:{_controlPort}/", cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }
            catch { }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Finds liquidsoap binary. Checks known absolute paths first (systemd PATH is restricted),
    /// then falls back to searching the system PATH.
    /// </summary>
    private static string? FindLiquidsoapBinary()
    {
        // Check known absolute paths first (most likely locations)
        foreach (var path in KnownLiquidsoapPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Fallback: search PATH (may not work under systemd)
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, "liquidsoap");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ParseHost(string url)
    {
        var cleaned = url.Replace("http://", "").Replace("https://", "");
        return cleaned.Split(':')[0].Split('/')[0].Trim();
    }

    private static int ParsePort(string url)
    {
        var cleaned = url.Replace("http://", "").Replace("https://", "");
        if (cleaned.Contains(':'))
        {
            var portStr = cleaned.Split(':')[1].Split('/')[0];
            if (int.TryParse(portStr, out var port)) return port;
        }
        return 8000;
    }

    private static int FindAvailablePort(int start, int end)
    {
        for (int port = start; port <= end; port++)
        {
            try
            {
                using var listener = TcpListener.Create(port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch { }
        }
        return start;
    }

    /// <summary>
    /// Disposes of all resources.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            StopInternal();
        }
        GC.SuppressFinalize(this);
    }
}
