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
/// Liquidsoap stays connected to Icecast at all times, eliminating the 1-2 second
/// gap between tracks that occurred with FFmpeg's per-file approach.
///
/// Architecture:
/// - Generates a dynamic .liq script from plugin configuration
/// - Launches Liquidsoap as a persistent subprocess
/// - Controls Liquidsoap via its built-in HTTP/Telnet API (same port)
/// - Writes playlist files (M3U) that Liquidsoap watches via inotify (reload_mode="watch")
/// - Sends HTTP commands to skip tracks, trigger reloads, etc.
///
/// The playlist source uses reload_mode="watch" which uses Linux inotify to detect
/// file changes nearly instantly. When we write a new M3U file and then send a
/// "skip" command, Liquidsoap picks up the new playlist for the next track.
/// </summary>
public class LiquidsoapStreamingService : IDisposable
{
    private readonly ILogger<LiquidsoapStreamingService> _logger;
    private Process? _liquidsoapProcess;
    private HttpClient? _httpClient;
    private readonly object _lock = new();
    private bool _isRunning;
    private int _controlPort;

    /// <summary>
    /// Base working directory for Liquidsoap files (script, playlist, logs).
    /// </summary>
    private const string WorkDir = "/tmp/radio-online";

    /// <summary>
    /// Path to the M3U playlist file that Liquidsoap watches.
    /// </summary>
    private const string PlaylistFile = "/tmp/radio-online/playlist.m3u";

    /// <summary>
    /// Path to the temporary M3U file (used for atomic file replacement).
    /// </summary>
    private const string PlaylistTempFile = "/tmp/radio-online/playlist.m3u.tmp";

    /// <summary>
    /// Path to the generated Liquidsoap script.
    /// </summary>
    private const string ScriptFile = "/tmp/radio-online/radio.liq";

    /// <summary>
    /// Default control port range for Liquidsoap HTTP/Telnet server.
    /// </summary>
    private const int ControlPortStart = 12345;
    private const int ControlPortEnd = 12355;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiquidsoapStreamingService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
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
    /// Gets the current Liquidsoap control port (for HTTP/Telnet commands).
    /// </summary>
    public int ControlPort => _controlPort;

    /// <summary>
    /// Starts the Liquidsoap process with the given configuration.
    /// Generates a .liq script, creates the working directory, and launches Liquidsoap.
    /// Liquidsoap connects to Icecast and begins watching the playlist file.
    /// </summary>
    /// <param name="config">The plugin configuration containing Icecast and format settings.</param>
    /// <param name="cancellationToken">Cancellation token to abort startup.</param>
    /// <returns>True if Liquidsoap started successfully and is ready for commands.</returns>
    public async Task<bool> StartStreamingAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_isRunning && _liquidsoapProcess != null && !_liquidsoapProcess.HasExited)
            {
                _logger.LogInformation("Liquidsoap is already running on port {Port}", _controlPort);
                return true;
            }
        }

        // Ensure working directory exists
        Directory.CreateDirectory(WorkDir);

        // Find an available control port
        _controlPort = FindAvailablePort(ControlPortStart, ControlPortEnd);

        // Generate the Liquidsoap script from configuration
        var script = GenerateScript(config);
        await File.WriteAllTextAsync(ScriptFile, script, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Generated Liquidsoap script at {Path}", ScriptFile);

        // Create HTTP client for communicating with Liquidsoap's control API
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        // Verify liquidsoap binary is available
        var liquidsoapPath = FindLiquidsoapBinary();
        if (string.IsNullOrEmpty(liquidsoapPath))
        {
            _logger.LogError("Liquidsoap binary not found in PATH. Please install Liquidsoap.");
            _httpClient.Dispose();
            _httpClient = null;
            return false;
        }

        // Launch Liquidsoap process
        var startInfo = new ProcessStartInfo
        {
            FileName = liquidsoapPath,
            Arguments = $"-v \"{ScriptFile}\"",
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

        _liquidsoapProcess.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("Liquidsoap: {Data}", e.Data);
            }
        };

        _liquidsoapProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                // Liquidsoap logs to stderr by default
                _logger.LogDebug("LS> {Data}", e.Data);
            }
        };

        _logger.LogInformation("Starting Liquidsoap (control port: {Port}, format: {Format}/{Bitrate}kbps)",
            _controlPort, config.AudioFormat, config.AudioBitrate);

        try
        {
            if (!_liquidsoapProcess.Start())
            {
                _logger.LogError("Failed to start Liquidsoap process");
                CleanupProcess();
                return false;
            }

            _liquidsoapProcess.BeginOutputReadLine();
            _liquidsoapProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception starting Liquidsoap process");
            CleanupProcess();
            return false;
        }

        // Wait for Liquidsoap to be ready (control port responding)
        if (!await WaitForReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Liquidsoap failed to start - control port {Port} not responding after 15 seconds", _controlPort);
            StopInternal();
            return false;
        }

        lock (_lock) { _isRunning = true; }

        _logger.LogInformation("Liquidsoap started successfully (PID: {Pid}, control port: {Port})",
            _liquidsoapProcess.Id, _controlPort);
        return true;
    }

    /// <summary>
    /// Writes the playlist file with the given audio file paths.
    /// Liquidsoap detects the change via inotify and reloads on next track request.
    /// Uses atomic file replacement (write to temp, then rename) for instant inotify detection.
    /// </summary>
    /// <param name="filePaths">Ordered list of absolute paths to audio files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetPlaylistAsync(List<string> filePaths, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var path in filePaths)
        {
            sb.AppendLine(path);
        }

        var content = sb.ToString();

        // Atomic write: write to temp file, then rename
        // rename() on Linux is atomic on the same filesystem, so inotify fires instantly
        await File.WriteAllTextAsync(PlaylistTempFile, content, cancellationToken).ConfigureAwait(false);
        File.Move(PlaylistTempFile, PlaylistFile, overwrite: true);

        _logger.LogInformation("Playlist written with {Count} tracks (atomic replacement)", filePaths.Count);
    }

    /// <summary>
    /// Clears the playlist by writing an empty M3U file.
    /// After the current track finishes, Liquidsoap will have nothing to play
    /// and mksafe will output silence. The caller should then stop Liquidsoap.
    /// </summary>
    public async Task ClearPlaylistAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(PlaylistTempFile, "#EXTM3U\n", cancellationToken).ConfigureAwait(false);
        File.Move(PlaylistTempFile, PlaylistFile, overwrite: true);
        _logger.LogInformation("Playlist cleared");
    }

    /// <summary>
    /// Sends a skip command to Liquidsoap, making it advance to the next track.
    /// If the playlist file was recently changed, the new playlist will be loaded.
    /// </summary>
    public async Task SkipTrackAsync(CancellationToken cancellationToken)
    {
        if (_httpClient == null || !_isRunning) return;

        try
        {
            var response = await _httpClient.PostAsync(
                $"http://localhost:{_controlPort}/radio.skip",
                null, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Liquidsoap skip command sent successfully");
            }
            else
            {
                _logger.LogWarning("Liquidsoap skip command returned {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send skip command to Liquidsoap on port {Port}", _controlPort);
        }
    }

    /// <summary>
    /// Queries Liquidsoap for the currently playing track metadata.
    /// </summary>
    /// <returns>Track metadata string, or null if no track is playing or Liquidsoap is not responding.</returns>
    public async Task<string?> GetNowPlayingAsync(CancellationToken cancellationToken)
    {
        if (_httpClient == null || !_isRunning) return null;

        try
        {
            var response = await _httpClient.GetStringAsync(
                $"http://localhost:{_controlPort}/radio.get",
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(response) || response.Contains("no available"))
            {
                return null;
            }

            return response.Trim();
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// Internal stop method (caller must hold _lock).
    /// </summary>
    private void StopInternal()
    {
        if (_liquidsoapProcess != null && !_liquidsoapProcess.HasExited)
        {
            try
            {
                // Try graceful shutdown first (SIGTERM)
                _liquidsoapProcess.Kill(false);
                if (!_liquidsoapProcess.WaitForExit(3000))
                {
                    _liquidsoapProcess.Kill(true); // Force kill (SIGKILL)
                }
                _logger.LogInformation("Stopped Liquidsoap process (PID: {Pid})", _liquidsoapProcess.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping Liquidsoap process");
            }
        }

        _isRunning = false;
        CleanupProcess();
    }

    /// <summary>
    /// Cleans up process and HTTP client resources.
    /// </summary>
    private void CleanupProcess()
    {
        if (_liquidsoapProcess != null)
        {
            _liquidsoapProcess.OutputDataReceived -= null;
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
    /// Generates a Liquidsoap (.liq) script from the plugin configuration.
    /// The script configures a playlist source with watch-based auto-reload,
    /// crossfade transitions, mksafe silence fallback, and Icecast output.
    /// </summary>
    private string GenerateScript(PluginConfiguration config)
    {
        var icecastHost = ParseIcecastHost(config.IcecastUrl);
        var icecastPort = ParseIcecastPort(config.IcecastUrl);

        var sb = new StringBuilder();

        sb.AppendLine("-- Radio Online Plugin - Auto-generated Liquidsoap script");
        sb.AppendLine("-- Generated: " + DateTime.Now.ToString("O"));
        sb.AppendLine();
        sb.AppendLine("-- Control interface (Telnet + HTTP on same port)");
        sb.AppendLine($"set(\"server.telnet\", true)");
        sb.AppendLine($"set(\"server.telnet.port\", {_controlPort})");
        sb.AppendLine($"set(\"log.file.path\", false)");
        sb.AppendLine($"set(\"log.stdout\", true)");
        sb.AppendLine($"set(\"log.level\", 3)");
        sb.AppendLine();

        // Playlist source with inotify-based auto-reload
        sb.AppendLine("-- Playlist source: reads M3U file, watches for changes via inotify");
        sb.AppendLine("-- mode=\"normal\" plays in order, no random/shuffle");
        sb.AppendLine("-- reload_mode=\"watch\" uses Linux inotify for instant file change detection");
        sb.AppendLine("music = playlist(");
        sb.AppendLine("  id=\"radio\",");
        sb.AppendLine("  mode=\"normal\",");
        sb.AppendLine($"  reload_mode=\"watch\",");
        sb.AppendLine($"  reload_interval=1,");
        sb.AppendLine($"  \"{PlaylistFile}\"");
        sb.AppendLine(")");
        sb.AppendLine();

        // Crossfade transition between tracks
        sb.AppendLine("-- Crossfade: smooth transition between tracks (0.5s fade out + 0.5s fade in)");
        sb.AppendLine("def radio_transition(a, b) =");
        sb.AppendLine("  add(normalize=false,");
        sb.AppendLine("    [fade.final(duration=0.5, a),");
        sb.AppendLine("     fade.initial(duration=0.5, b),");
        sb.AppendLine("     source.skip(a)])");
        sb.AppendLine("end");
        sb.AppendLine("music = crossfade(radio_transition, music)");
        sb.AppendLine();

        // Safety wrapper: outputs silence when playlist is empty
        sb.AppendLine("-- Safety: prevent source errors when playlist is empty");
        sb.AppendLine("radio = mksafe(music)");
        sb.AppendLine();

        // Icecast output
        sb.Append("output.icecast(");

        if (config.AudioFormat.Equals("ogg", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"%ogg(%vorbis(bitrate={config.AudioBitrate})),");
        }
        else if (config.AudioFormat.Equals("m4a", StringComparison.OrdinalIgnoreCase))
        {
            // AAC in ADTS container - supported by Icecast 2.4+
            sb.AppendLine("%ffmpeg(format=\"adts\",%audio(codec=\"aac\",b=\"{config.AudioBitrate}k\")),");
        }
        else
        {
            // Default to OGG Vorbis
            sb.AppendLine($"%ogg(%vorbis(bitrate={config.AudioBitrate})),");
        }

        sb.AppendLine($"  host=\"{icecastHost}\",");
        sb.AppendLine($"  port={icecastPort},");
        sb.AppendLine($"  password=\"{EscapeLiqString(config.IcecastPassword)}\",");
        sb.AppendLine($"  mount=\"{config.IcecastMountPoint}\",");
        sb.AppendLine($"  name=\"{EscapeLiqString(config.StreamName)}\",");
        sb.AppendLine($"  genre=\"{EscapeLiqString(config.StreamGenre)}\",");
        sb.AppendLine("  radio)");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Waits for Liquidsoap to be ready by polling its control port.
    /// </summary>
    private async Task<bool> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < 30; i++) // 30 attempts * 500ms = 15 seconds max
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                if (_httpClient != null)
                {
                    var response = await _httpClient.GetAsync(
                        $"http://localhost:{_controlPort}/",
                        cancellationToken).ConfigureAwait(false);

                    // Any HTTP response means the port is listening
                    return true;
                }
            }
            catch
            {
                // Port not ready yet - this is expected during startup
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Finds the liquidsoap binary on the system PATH.
    /// </summary>
    private static string? FindLiquidsoapBinary()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidates = new[] { "liquidsoap" };
            foreach (var name in candidates)
            {
                var fullPath = Path.Combine(dir, name);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the hostname from an Icecast URL.
    /// </summary>
    private static string ParseIcecastHost(string url)
    {
        var cleaned = url.Replace("http://", string.Empty).Replace("https://", string.Empty);
        var hostPart = cleaned.Split(':')[0];
        return hostPart.Trim();
    }

    /// <summary>
    /// Parses the port number from an Icecast URL.
    /// </summary>
    private static int ParseIcecastPort(string url)
    {
        var cleaned = url.Replace("http://", string.Empty).Replace("https://", string.Empty);
        if (cleaned.Contains(':'))
        {
            var portStr = cleaned.Split(':').ElementAtOrDefault(1)?.Split('/').FirstOrDefault();
            if (int.TryParse(portStr, out var port))
            {
                return port;
            }
        }

        return 8000; // Default Icecast port
    }

    /// <summary>
    /// Finds an available TCP port in the given range.
    /// </summary>
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
            catch
            {
                // Port is in use, try next
            }
        }

        return start; // Fallback to start port
    }

    /// <summary>
    /// Escapes a string for use in a Liquidsoap script string literal.
    /// </summary>
    private static string EscapeLiqString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Disposes of all resources used by the Liquidsoap streaming service.
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
