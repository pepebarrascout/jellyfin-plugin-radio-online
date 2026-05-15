using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RadioOnline.Services;

/// <summary>
/// Client for communicating with Liquidsoap's Telnet server.
/// Sends commands over TCP to control the radio queue (append, skip, clear, status).
/// Liquidsoap responds with the command result followed by "END" on a new line.
/// </summary>
public class LiquidsoapClient : IDisposable
{
    private readonly ILogger<LiquidsoapClient> _logger;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private string _host;
    private int _port;
    private readonly int _connectTimeoutMs;
    private readonly int _readTimeoutMs;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Timestamp of the last successful connection to Liquidsoap.
    /// Used for proactive reconnection before the TCP connection goes stale.
    /// </summary>
    private DateTime _lastConnectionTime;

    /// <summary>
    /// Maximum age of a TCP connection before proactive reconnection.
    /// Prevents half-open connections from causing silent command failures.
    /// </summary>
    private static readonly TimeSpan ConnectionMaxAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Initializes a new instance of the <see cref="LiquidsoapClient"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="host">Liquidsoap Telnet server host (e.g., "localhost").</param>
    /// <param name="port">Liquidsoap Telnet server port (e.g., 8080).</param>
    public LiquidsoapClient(ILogger<LiquidsoapClient> logger, string host = "localhost", int port = 8080)
    {
        _logger = logger;
        _host = host;
        _port = port;
        _connectTimeoutMs = 5000;
        _readTimeoutMs = 3000;
    }

    /// <summary>
    /// Gets whether the client is currently connected to Liquidsoap.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            try
            {
                return _tcpClient != null && _tcpClient.Connected;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Appends a single track to the Liquidsoap queue.
    /// </summary>
    /// <param name="filePath">The file path as seen by Liquidsoap (e.g., /music/Album/song.m4a).</param>
    /// <returns>True if the track was added successfully.</returns>
    public async Task<bool> AppendTrackAsync(string filePath)
    {
        var response = await SendCommandAsync($"queue.append {filePath}").ConfigureAwait(false);
        var success = response.Contains("added", StringComparison.OrdinalIgnoreCase);
        if (success)
        {
            _logger.LogDebug("Queued track: {Path}", filePath);
        }
        else
        {
            _logger.LogWarning("Failed to queue track: {Path} (response: {Response})", filePath, response);
        }

        return success;
    }

    /// <summary>
    /// Appends multiple tracks to the Liquidsoap queue sequentially.
    /// </summary>
    /// <param name="filePaths">The file paths as seen by Liquidsoap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tracks successfully added.</returns>
    public async Task<int> AppendTracksAsync(string[] filePaths, CancellationToken cancellationToken = default)
    {
        var added = 0;
        foreach (var path in filePaths)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (await AppendTrackAsync(path).ConfigureAwait(false))
                added++;

            // Small delay to avoid overwhelming the Telnet server
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    /// <summary>
    /// Skips the currently playing track in the Liquidsoap queue.
    /// </summary>
    /// <returns>True if the skip was successful.</returns>
    public async Task<bool> SkipAsync()
    {
        var response = await SendCommandAsync("queue.skip").ConfigureAwait(false);
        var success = response.Contains("skipped", StringComparison.OrdinalIgnoreCase);
        if (success)
        {
            _logger.LogInformation("Skipped current track");
        }

        return success;
    }

    /// <summary>
    /// Clears all tracks from the Liquidsoap queue.
    /// </summary>
    /// <returns>True if the clear was successful.</returns>
    public async Task<bool> ClearQueueAsync()
    {
        var response = await SendCommandAsync("queue.clear").ConfigureAwait(false);
        var success = response.Contains("cleared", StringComparison.OrdinalIgnoreCase);
        if (success)
        {
            _logger.LogInformation("Liquidsoap queue cleared");
        }

        return success;
    }

    /// <summary>
    /// Gets the current status from the Liquidsoap server.
    /// </summary>
    /// <returns>The status response string.</returns>
    public async Task<string> GetStatusAsync()
    {
        return await SendCommandAsync("queue.status").ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the file path of the currently playing track from Liquidsoap.
    /// Returns the Liquidsoap path (e.g., /music/Album/song.mp3) or "none" if nothing is playing.
    /// </summary>
    /// <returns>The file path of the current track, or empty string on failure.</returns>
    public async Task<string> GetCurrentTrackAsync()
    {
        var response = await SendCommandAsync("queue.current_track").ConfigureAwait(false);
        return response.Trim();
    }

    /// <summary>
    /// Gets the current number of tracks in the Liquidsoap queue.
    /// Uses the queue.length Telnet command (requires radio.liq to register it).
    /// Returns -1 on failure.
    /// </summary>
    /// <returns>The number of tracks in the queue, or -1 if the query failed.</returns>
    public async Task<int> GetQueueLengthAsync()
    {
        try
        {
            var response = await SendCommandAsync("queue.length").ConfigureAwait(false);
            if (int.TryParse(response.Trim(), out var length))
            {
                return length;
            }

            _logger.LogWarning("Failed to parse queue length response: {Response}", response);
            return -1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error querying queue length");
            return -1;
        }
    }

    /// <summary>
    /// Tests the connection to the Liquidsoap server.
    /// </summary>
    /// <returns>True if the connection is working.</returns>
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var status = await GetStatusAsync().ConfigureAwait(false);
            return status.Contains("running", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Liquidsoap connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Sends a raw command to the Liquidsoap Telnet server and returns the response.
    /// Handles connection, command sending, and response reading.
    /// On IOException (broken pipe / stale connection), retries once after reconnecting.
    /// </summary>
    private async Task<string> SendCommandAsync(string command)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SendCommandInternalAsync(command).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Internal command send with retry logic for stale connections.
    /// </summary>
    private async Task<string> SendCommandInternalAsync(string command)
    {
        try
        {
            EnsureConnected();
            if (_stream == null)
            {
                _logger.LogWarning("Cannot send command: not connected to Liquidsoap at {Host}:{Port}", _host, _port);
                return string.Empty;
            }

            // Send the command followed by newline
            var commandBytes = Encoding.ASCII.GetBytes(command + "\n");
            await _stream.WriteAsync(commandBytes).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);

            // Read response until "END" marker
            return await ReadResponseAsync().ConfigureAwait(false);
        }
        catch (System.IO.IOException)
        {
            // Stale/broken connection - disconnect and retry once
            _logger.LogDebug("Stale connection detected, reconnecting and retrying: {Command}", command);
            Disconnect();

            try
            {
                EnsureConnected();
                if (_stream == null)
                {
                    _logger.LogWarning("Reconnect failed for command: {Command}", command);
                    return string.Empty;
                }

                var commandBytes = Encoding.ASCII.GetBytes(command + "\n");
                await _stream.WriteAsync(commandBytes).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);

                return await ReadResponseAsync().ConfigureAwait(false);
            }
            catch (Exception retryEx)
            {
                _logger.LogWarning(retryEx, "Retry failed for command: {Command}", command);
                Disconnect();
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending command to Liquidsoap: {Command}", command);
            Disconnect();
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads the Telnet response until the "END" marker.
    /// Liquidsoap terminates each response with a line containing only "END".
    /// </summary>
    private async Task<string> ReadResponseAsync()
    {
        if (_stream == null)
            return string.Empty;

        var sb = new StringBuilder();
        var buffer = new byte[4096];
        var lineBuffer = new StringBuilder();

        try
        {
            using var cts = new CancellationTokenSource(_readTimeoutMs);
            while (!cts.IsCancellationRequested)
            {
                var readTask = _stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (await Task.WhenAny(readTask, Task.Delay(_readTimeoutMs, cts.Token)).ConfigureAwait(false) != readTask)
                {
                    _logger.LogWarning("Timeout reading Liquidsoap response");
                    break;
                }

                var bytesRead = await readTask.ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                var text = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                foreach (var c in text)
                {
                    if (c == '\n')
                    {
                        var line = lineBuffer.ToString().TrimEnd('\r');
                        if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
                            return sb.ToString().Trim();

                        if (line.Length > 0)
                            sb.AppendLine(line);

                        lineBuffer.Clear();
                    }
                    else
                    {
                        lineBuffer.Append(c);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout reading response from Liquidsoap");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading Liquidsoap response");
        }

        // If we didn't get END, return whatever we have
        var partial = sb.ToString().Trim();
        return string.IsNullOrEmpty(partial) ? lineBuffer.ToString().Trim() : partial;
    }

    /// <summary>
    /// Ensures a TCP connection to the Liquidsoap Telnet server is active.
    /// Creates a new connection if needed or reconnects if the existing one is dead.
    /// </summary>
    private void EnsureConnected()
    {
        if (_disposed)
            return;

        // Check if existing connection should be proactively renewed
        if (_tcpClient != null)
        {
            // Proactive reconnection: force a fresh connection every ConnectionMaxAge
            // to prevent half-open connections from causing silent command failures.
            // Socket.Poll is unreliable for detecting stale TCP connections.
            if ((DateTime.UtcNow - _lastConnectionTime) > ConnectionMaxAge)
            {
                _logger.LogDebug("Proactive reconnect: connection age {Age}s exceeded max {Max}s",
                    (int)(DateTime.UtcNow - _lastConnectionTime).TotalSeconds,
                    (int)ConnectionMaxAge.TotalSeconds);
                Disconnect();
            }
            else
            {
                try
                {
                    // Still within age limit — check socket is connected
                    if (_tcpClient.Connected && _tcpClient.Client.Poll(0, SelectMode.SelectWrite))
                    {
                        return; // Connection appears good and is fresh enough
                    }
                }
                catch
                {
                    // Connection is dead
                }

                Disconnect();
            }
        }

        // Create new connection
        try
        {
            _tcpClient = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = _readTimeoutMs,
                SendTimeout = _readTimeoutMs,
            };

            var connectTask = _tcpClient.ConnectAsync(_host, _port);
            if (connectTask.Wait(_connectTimeoutMs))
            {
                _stream = _tcpClient.GetStream();
                _lastConnectionTime = DateTime.UtcNow;
                _logger.LogInformation("Connected to Liquidsoap Telnet at {Host}:{Port}", _host, _port);
            }
            else
            {
                _logger.LogWarning("Timeout connecting to Liquidsoap at {Host}:{Port}", _host, _port);
                Disconnect();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to Liquidsoap at {Host}:{Port}", _host, _port);
            Disconnect();
        }
    }

    /// <summary>
    /// Disconnects from the Liquidsoap Telnet server.
    /// </summary>
    public void Disconnect()
    {
        try
        {
            _stream?.Close();
            _stream?.Dispose();
        }
        catch { }

        try
        {
            _tcpClient?.Close();
            _tcpClient?.Dispose();
        }
        catch { }

        _stream = null;
        _tcpClient = null;
        _lastConnectionTime = DateTime.MinValue;
    }

    /// <summary>
    /// Updates the connection settings.
    /// </summary>
    public void UpdateConnection(string host, int port)
    {
        if (_host != host || _port != port)
        {
            _logger.LogInformation("Liquidsoap connection settings changed: {OldHost}:{OldPort} -> {NewHost}:{NewPort}", _host, _port, host, port);
            _host = host;
            _port = port;
            Disconnect();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disconnect();
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
