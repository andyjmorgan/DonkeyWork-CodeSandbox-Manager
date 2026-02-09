using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using DonkeyWork.CodeSandbox.McpServer.Models;

namespace DonkeyWork.CodeSandbox.McpServer.Services;

public class StdioBridge : IDisposable
{
    private readonly Lock _stateLock = new();
    private readonly ILogger<StdioBridge> _logger;

    private Process? _mcpProcess;
    private StreamWriter? _stdin;
    private McpServerState _state = McpServerState.Idle;
    private string? _error;
    private DateTime? _startedAt;
    private DateTime? _lastRequestAt;
    private bool _disposed;

    // Background stdout reader routes messages to the right place
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
    private Channel<string>? _notificationChannel;
    private CancellationTokenSource? _stdoutReaderCts;

    public McpServerState State
    {
        get { lock (_stateLock) return _state; }
    }

    public McpServerStatusResponse GetStatus()
    {
        lock (_stateLock)
        {
            return new McpServerStatusResponse
            {
                State = _state,
                Error = _error,
                StartedAt = _startedAt,
                LastRequestAt = _lastRequestAt
            };
        }
    }

    public StdioBridge(ILogger<StdioBridge> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a ChannelReader for server-initiated notifications and requests.
    /// Returns null if the server is not running.
    /// </summary>
    public ChannelReader<string>? GetNotificationReader()
    {
        return _notificationChannel?.Reader;
    }

    public async Task StartAsync(StartRequest request, CancellationToken cancellationToken)
    {
        await StartAsync(request, null, cancellationToken);
    }

    public async Task StartAsync(StartRequest request, ChannelWriter<McpStartEvent>? events, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_state == McpServerState.Error)
            {
                _logger.LogInformation("Resetting from Error state before restart");
            }
            else if (_state != McpServerState.Idle)
            {
                throw new InvalidOperationException($"Cannot start: current state is {_state}");
            }

            _state = McpServerState.Initializing;
            _error = null;
        }

        // Clean up any previous process
        KillMcpProcess();

        var startTime = DateTime.UtcNow;

        void Emit(string eventType, string message, Action<McpStartEvent>? configure = null)
        {
            if (events is null) return;
            var evt = new McpStartEvent
            {
                EventType = eventType,
                Message = message,
                ElapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds
            };
            configure?.Invoke(evt);
            events.TryWrite(evt);
        }

        try
        {
            // Run pre-exec scripts sequentially
            foreach (var script in request.PreExecScripts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Running pre-exec script: {Script}", script);
                Emit("pre_exec_start", $"Running: {script}");
                await RunPreExecScriptAsync(script, events, cancellationToken);
                Emit("pre_exec_complete", $"Completed: {script}");
            }

            // Launch the MCP stdio server process
            var commandDisplay = $"{request.Command} {string.Join(" ", request.Arguments)}";
            _logger.LogInformation("Launching MCP server: {Command} {Arguments}",
                request.Command, string.Join(" ", request.Arguments));
            Emit("process_starting", $"Launching: {commandDisplay}");
            LaunchMcpProcess(request.Command, request.Arguments, events);

            Emit("process_started", $"Process started", e => e.Pid = _mcpProcess?.Id);

            // Verify the process is still running after a short delay
            await Task.Delay(100, cancellationToken);
            if (_mcpProcess is null || _mcpProcess.HasExited)
            {
                var exitCode = _mcpProcess?.ExitCode ?? -1;
                throw new InvalidOperationException($"MCP process exited immediately with code {exitCode}");
            }

            // Wait for the MCP server to be ready by sending an initialize handshake
            _logger.LogInformation("Waiting for MCP server to respond to initialize handshake...");
            Emit("handshake_starting", "Sending initialize handshake...");
            await WaitForMcpReadyAsync(request.TimeoutSeconds, events, cancellationToken);

            // Handshake complete - now start the background stdout reader
            StartStdoutReader();

            lock (_stateLock)
            {
                _state = McpServerState.Ready;
                _startedAt = DateTime.UtcNow;
            }

            _logger.LogInformation("MCP server is ready (PID: {Pid})", _mcpProcess.Id);
            Emit("ready", "MCP server is ready", e => e.Pid = _mcpProcess?.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MCP server");

            lock (_stateLock)
            {
                _state = McpServerState.Error;
                _error = ex.Message;
            }

            Emit("error", ex.Message);
            throw;
        }
    }

    public async Task<string?> SendRequestAsync(string jsonRpcRequest, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (State != McpServerState.Ready)
            throw new InvalidOperationException($"MCP server is not ready: current state is {State}");

        // Determine if this is a notification (no id field = fire-and-forget)
        bool isNotification = IsNotification(jsonRpcRequest);

        EnsureProcessAlive();

        if (isNotification)
        {
            // Fire-and-forget: write to stdin, no response expected
            await WriteToStdinAsync(jsonRpcRequest, cancellationToken);

            lock (_stateLock)
                _lastRequestAt = DateTime.UtcNow;

            _logger.LogDebug("Sent notification (no response expected)");
            return null;
        }

        // Extract the request id so we can match the response
        var requestId = ExtractId(jsonRpcRequest);
        if (requestId is null)
            throw new InvalidOperationException("Could not extract id from JSON-RPC request");

        // Extract method name for logging
        var method = ExtractMethod(jsonRpcRequest);
        _logger.LogInformation("Sending request id={Id} method={Method}, registering TCS", requestId, method);

        // Register the TCS BEFORE writing to stdin to avoid a race where
        // the background reader sees the response before the TCS is registered.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        try
        {
            // Now write request to stdin (synchronized to prevent interleaving)
            await WriteToStdinAsync(jsonRpcRequest, cancellationToken);
            _logger.LogInformation("Written request id={Id} to stdin, pending count={Count}", requestId, _pendingRequests.Count);

            lock (_stateLock)
                _lastRequestAt = DateTime.UtcNow;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await using var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            return await tcs.Task;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for MCP server response");
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Sends a JSON-RPC response back to the MCP server (for answering server-initiated requests like elicitations).
    /// </summary>
    public async Task SendResponseAsync(string jsonRpcResponse, CancellationToken cancellationToken)
    {
        if (State != McpServerState.Ready)
            throw new InvalidOperationException($"MCP server is not ready: current state is {State}");

        EnsureProcessAlive();
        await WriteToStdinAsync(jsonRpcResponse, cancellationToken);
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_state == McpServerState.Disposed)
                return;

            _state = McpServerState.Disposed;
        }

        KillMcpProcess();

        lock (_stateLock)
        {
            _state = McpServerState.Idle;
            _error = null;
            _startedAt = null;
            _lastRequestAt = null;
        }

        _logger.LogInformation("MCP server stopped");
    }

    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    private async Task WriteToStdinAsync(string message, CancellationToken cancellationToken)
    {
        await _stdinLock.WaitAsync(cancellationToken);
        try
        {
            await _stdin!.WriteLineAsync(message.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    private void StartStdoutReader()
    {
        _notificationChannel = Channel.CreateUnbounded<string>();
        _stdoutReaderCts = new CancellationTokenSource();
        var ct = _stdoutReaderCts.Token;
        var stdout = _mcpProcess!.StandardOutput;

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Background stdout reader started");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await stdout.ReadLineAsync(ct);

                    if (line is null)
                    {
                        // Process closed stdout
                        lock (_stateLock)
                        {
                            if (_state == McpServerState.Ready)
                            {
                                _state = McpServerState.Error;
                                _error = "MCP server closed stdout unexpectedly";
                            }
                        }

                        // Fail all pending requests
                        foreach (var kvp in _pendingRequests)
                        {
                            kvp.Value.TrySetException(new IOException("MCP server closed stdout unexpectedly"));
                        }
                        _pendingRequests.Clear();
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    _logger.LogInformation("Stdout received line ({Length} chars)", line.Length);

                    // Try to parse as JSON-RPC
                    if (!TryParseJsonRpc(line, out var hasId, out var id, out var isResponse, out var hasMethod))
                    {
                        _logger.LogWarning("Skipping non-JSON-RPC stdout line: {Line}", line);
                        continue;
                    }

                    if (isResponse && id is not null && _pendingRequests.TryRemove(id, out var tcs))
                    {
                        // This is a response to a pending request
                        _logger.LogInformation("Routing response for id={Id}, pending remaining={Count}", id, _pendingRequests.Count);
                        tcs.TrySetResult(line);
                    }
                    else if (hasMethod)
                    {
                        // Server-initiated notification or request
                        _logger.LogDebug("Server message received: {Line}", line);
                        _notificationChannel?.Writer.TryWrite(line);
                    }
                    else if (isResponse)
                    {
                        // Response to an unknown/expired request
                        _logger.LogWarning("Received response for unknown request id={Id}", id);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping unrecognized JSON-RPC message: {Line}", line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stdout reader crashed");
                lock (_stateLock)
                {
                    if (_state == McpServerState.Ready)
                    {
                        _state = McpServerState.Error;
                        _error = $"Stdout reader crashed: {ex.Message}";
                    }
                }

                foreach (var kvp in _pendingRequests)
                {
                    kvp.Value.TrySetException(ex);
                }
                _pendingRequests.Clear();
            }
            finally
            {
                _notificationChannel?.Writer.TryComplete();
            }
        }, ct);
    }

    private Task WaitForMcpReadyAsync(int timeoutSeconds, CancellationToken cancellationToken)
        => WaitForMcpReadyAsync(timeoutSeconds, null, cancellationToken);

    private async Task WaitForMcpReadyAsync(int timeoutSeconds, ChannelWriter<McpStartEvent>? events, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        // Note: during init, stdout is read directly (background reader not started yet)
        var stdout = _mcpProcess!.StandardOutput;

        void Emit(string eventType, string message, string? stream = null)
        {
            events?.TryWrite(new McpStartEvent
            {
                EventType = eventType,
                Message = message,
                Stream = stream,
                ElapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds
            });
        }

        // Send an MCP initialize request to verify the server is ready
        var initializeRequest = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "__init_probe__",
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "readiness-probe", version = "1.0.0" }
            }
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            // Write the initialize request to stdin
            await _stdin!.WriteLineAsync(initializeRequest.AsMemory(), timeoutCts.Token);
            await _stdin.FlushAsync(timeoutCts.Token);

            _logger.LogDebug("Sent initialize probe, waiting for response...");
            Emit("handshake_sent", "Initialize probe sent, waiting for response...");

            // Read response from stdout - wait for valid JSON-RPC response
            while (true)
            {
                if (_mcpProcess is null || _mcpProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        $"MCP process exited during initialization with code {_mcpProcess?.ExitCode ?? -1}");
                }

                var line = await stdout.ReadLineAsync(timeoutCts.Token);

                if (line is null)
                {
                    throw new InvalidOperationException("MCP server closed stdout during initialization");
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Check if this is a valid JSON-RPC response to our initialize request
                if (IsJsonRpcResponse(line))
                {
                    _logger.LogInformation("MCP server responded to initialize probe");
                    Emit("handshake_response", "Server responded to initialize probe");

                    // Send the initialized notification to complete the handshake
                    var initializedNotification = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        method = "notifications/initialized"
                    });
                    await _stdin.WriteLineAsync(initializedNotification.AsMemory(), timeoutCts.Token);
                    await _stdin.FlushAsync(timeoutCts.Token);

                    Emit("handshake_complete", "Handshake complete");
                    return;
                }

                // Not a JSON-RPC response - likely debug output, log and continue waiting
                _logger.LogDebug("Skipping non-JSON-RPC stdout line during init: {Line}", line);
                Emit("stdout_line", line, "stdout");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP server did not respond to initialize request within {timeoutSeconds} seconds");
        }
    }

    private Task RunPreExecScriptAsync(string script, CancellationToken cancellationToken)
        => RunPreExecScriptAsync(script, null, cancellationToken);

    private async Task RunPreExecScriptAsync(string script, ChannelWriter<McpStartEvent>? events, CancellationToken cancellationToken)
    {
        var workingDirectory = Directory.Exists("/home/user")
            ? "/home/user"
            : Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{script.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.Start();

        // Log output in background
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                _logger.LogInformation("[pre-exec stdout] {Line}", line);
                events?.TryWrite(new McpStartEvent { EventType = "pre_exec_output", Message = line, Stream = "stdout" });
            }
        }, cancellationToken);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                _logger.LogWarning("[pre-exec stderr] {Line}", line);
                events?.TryWrite(new McpStartEvent { EventType = "pre_exec_output", Message = line, Stream = "stderr" });
            }
        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Pre-exec script failed with exit code {process.ExitCode}: {script}");
    }

    private void LaunchMcpProcess(string command, string[] arguments)
        => LaunchMcpProcess(command, arguments, null);

    private void LaunchMcpProcess(string command, string[] arguments, ChannelWriter<McpStartEvent>? events)
    {
        var workingDirectory = Directory.Exists("/home/user")
            ? "/home/user"
            : Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();

        // Build proper argument string - each argument properly quoted if needed
        var argumentString = string.Join(" ", arguments.Select(arg =>
            arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg));

        _logger.LogDebug("Starting process: {Command} {Args} in {WorkingDir}",
            command, argumentString, workingDirectory);

        _mcpProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = argumentString,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        _mcpProcess.Exited += (_, _) =>
        {
            lock (_stateLock)
            {
                if (_state == McpServerState.Ready)
                {
                    _state = McpServerState.Error;
                    _error = $"MCP server process exited unexpectedly with code {_mcpProcess.ExitCode}";
                    _logger.LogError("MCP server process exited unexpectedly with code {ExitCode}", _mcpProcess.ExitCode);
                }
            }
        };

        _mcpProcess.Start();
        _stdin = _mcpProcess.StandardInput;

        // Drain stderr to logs and events in background
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _mcpProcess.StandardError.ReadLineAsync() is { } line)
                {
                    _logger.LogWarning("[mcp stderr] {Line}", line);
                    events?.TryWrite(new McpStartEvent { EventType = "mcp_stderr", Message = line, Stream = "stderr" });
                }
            }
            catch
            {
                // Process exited
            }
        });

        _logger.LogInformation("MCP server process started with PID {Pid}", _mcpProcess.Id);
    }

    private void EnsureProcessAlive()
    {
        if (_mcpProcess is null || _mcpProcess.HasExited)
        {
            lock (_stateLock)
            {
                _state = McpServerState.Error;
                _error = "MCP server process is not running";
            }

            throw new InvalidOperationException("MCP server process is not running");
        }
    }

    private static bool IsNotification(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return !doc.RootElement.TryGetProperty("id", out _);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
            {
                return idProp.ValueKind switch
                {
                    JsonValueKind.String => idProp.GetString(),
                    JsonValueKind.Number => idProp.GetRawText(),
                    _ => idProp.GetRawText()
                };
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractMethod(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("method", out var methodProp))
                return methodProp.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsJsonRpcResponse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.TryGetProperty("jsonrpc", out _) &&
                   (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a JSON-RPC message to determine its type.
    /// </summary>
    private static bool TryParseJsonRpc(string line, out bool hasId, out string? id, out bool isResponse, out bool hasMethod)
    {
        hasId = false;
        id = null;
        isResponse = false;
        hasMethod = false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("jsonrpc", out _))
                return false;

            if (root.TryGetProperty("id", out var idProp))
            {
                hasId = true;
                id = idProp.ValueKind switch
                {
                    JsonValueKind.String => idProp.GetString(),
                    JsonValueKind.Number => idProp.GetRawText(),
                    _ => idProp.GetRawText()
                };
            }

            isResponse = root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _);
            hasMethod = root.TryGetProperty("method", out _);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void KillMcpProcess()
    {
        // Stop the background reader
        _stdoutReaderCts?.Cancel();
        _stdoutReaderCts?.Dispose();
        _stdoutReaderCts = null;
        _notificationChannel?.Writer.TryComplete();
        _notificationChannel = null;

        // Fail all pending requests
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();

        if (_mcpProcess is null)
            return;

        try
        {
            _stdin?.Close();

            if (!_mcpProcess.HasExited)
                _mcpProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore
        }
        finally
        {
            _mcpProcess.Dispose();
            _mcpProcess = null;
            _stdin = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        KillMcpProcess();
        _stdinLock.Dispose();
    }
}
