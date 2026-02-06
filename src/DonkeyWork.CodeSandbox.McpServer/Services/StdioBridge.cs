using System.Diagnostics;
using System.Text.Json;
using DonkeyWork.CodeSandbox.McpServer.Models;

namespace DonkeyWork.CodeSandbox.McpServer.Services;

public class StdioBridge : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly ILogger<StdioBridge> _logger;

    private Process? _mcpProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private McpServerState _state = McpServerState.Idle;
    private string? _error;
    private DateTime? _startedAt;
    private DateTime? _lastRequestAt;
    private bool _disposed;

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

    public async Task StartAsync(StartRequest request, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_state != McpServerState.Idle)
                throw new InvalidOperationException($"Cannot start: current state is {_state}");

            _state = McpServerState.Initializing;
        }

        try
        {
            // Run pre-exec scripts sequentially
            foreach (var script in request.PreExecScripts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Running pre-exec script: {Script}", script);
                await RunPreExecScriptAsync(script, cancellationToken);
            }

            // Launch the MCP stdio server process
            _logger.LogInformation("Launching MCP server: {Command} {Arguments}",
                request.Command, string.Join(" ", request.Arguments));
            LaunchMcpProcess(request.Command, request.Arguments);

            // Verify the process is still running after a short delay
            await Task.Delay(100, cancellationToken);
            if (_mcpProcess is null || _mcpProcess.HasExited)
            {
                var exitCode = _mcpProcess?.ExitCode ?? -1;
                throw new InvalidOperationException($"MCP process exited immediately with code {exitCode}");
            }

            // Wait for the MCP server to be ready by sending an initialize handshake
            _logger.LogInformation("Waiting for MCP server to respond to initialize handshake...");
            await WaitForMcpReadyAsync(request.TimeoutSeconds, cancellationToken);

            lock (_stateLock)
            {
                _state = McpServerState.Ready;
                _startedAt = DateTime.UtcNow;
            }

            _logger.LogInformation("MCP server is ready (PID: {Pid})", _mcpProcess.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MCP server");

            lock (_stateLock)
            {
                _state = McpServerState.Error;
                _error = ex.Message;
            }

            throw;
        }
    }

    public async Task<string?> SendRequestAsync(string jsonRpcRequest, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (State != McpServerState.Ready)
            throw new InvalidOperationException($"MCP server is not ready: current state is {State}");

        // Determine if this is a notification (no id field = fire-and-forget)
        bool isNotification = IsNotification(jsonRpcRequest);

        if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken))
            throw new TimeoutException("Timed out waiting for MCP server availability");

        try
        {
            EnsureProcessAlive();

            // Write request to stdin
            await _stdin!.WriteLineAsync(jsonRpcRequest.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);

            lock (_stateLock)
                _lastRequestAt = DateTime.UtcNow;

            if (isNotification)
            {
                _logger.LogDebug("Sent notification (no response expected)");
                return null;
            }

            // Read response from stdout - read lines until we get valid JSON-RPC response
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            while (true)
            {
                var line = await _stdout!.ReadLineAsync(timeoutCts.Token);

                if (line is null)
                {
                    // Process closed stdout
                    lock (_stateLock)
                    {
                        _state = McpServerState.Error;
                        _error = "MCP server closed stdout unexpectedly";
                    }

                    throw new IOException("MCP server closed stdout unexpectedly");
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Validate it looks like a JSON-RPC response (has jsonrpc field and id or error)
                if (IsJsonRpcResponse(line))
                {
                    _logger.LogDebug("Received JSON-RPC response");
                    return line;
                }

                // Not a JSON-RPC response - likely debug output, log and skip
                _logger.LogDebug("Skipping non-JSON-RPC stdout line: {Line}", line);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for MCP server response");
        }
        finally
        {
            _semaphore.Release();
        }
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

    private async Task WaitForMcpReadyAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
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

            // Read response from stdout - wait for valid JSON-RPC response
            while (true)
            {
                if (_mcpProcess is null || _mcpProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        $"MCP process exited during initialization with code {_mcpProcess?.ExitCode ?? -1}");
                }

                var line = await _stdout!.ReadLineAsync(timeoutCts.Token);

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

                    // Send the initialized notification to complete the handshake
                    var initializedNotification = JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        method = "notifications/initialized"
                    });
                    await _stdin.WriteLineAsync(initializedNotification.AsMemory(), timeoutCts.Token);
                    await _stdin.FlushAsync(timeoutCts.Token);

                    return;
                }

                // Not a JSON-RPC response - likely debug output, log and continue waiting
                _logger.LogDebug("Skipping non-JSON-RPC stdout line during init: {Line}", line);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP server did not respond to initialize request within {timeoutSeconds} seconds");
        }
    }

    private async Task RunPreExecScriptAsync(string script, CancellationToken cancellationToken)
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
                _logger.LogInformation("[pre-exec stdout] {Line}", line);
        }, cancellationToken);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                _logger.LogWarning("[pre-exec stderr] {Line}", line);
        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Pre-exec script failed with exit code {process.ExitCode}: {script}");
    }

    private void LaunchMcpProcess(string command, string[] arguments)
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
        _stdout = _mcpProcess.StandardOutput;

        // Drain stderr to logs in background
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _mcpProcess.StandardError.ReadLineAsync() is { } line)
                    _logger.LogWarning("[mcp stderr] {Line}", line);
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

    private void KillMcpProcess()
    {
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
            _stdout = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        KillMcpProcess();
        _semaphore.Dispose();
    }
}
