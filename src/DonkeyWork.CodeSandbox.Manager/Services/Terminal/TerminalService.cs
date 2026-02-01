using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Services.Container;
using k8s;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services.Terminal;

/// <summary>
/// Service that bridges browser WebSocket connections to Kubernetes exec streams.
/// Implements the Kubernetes exec WebSocket protocol (channel.k8s.io/v4.channel.k8s.io).
/// </summary>
public class TerminalService : ITerminalService
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<TerminalService> _logger;
    private readonly IKataContainerService _containerService;

    // Track active sessions for resize operations
    private readonly ConcurrentDictionary<string, WebSocket> _activeSessions = new();

    // Kubernetes exec channel bytes
    private const byte StdinChannel = 0;
    private const byte StdoutChannel = 1;
    private const byte StderrChannel = 2;
    private const byte ResizeChannel = 4;

    public TerminalService(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<TerminalService> logger,
        IKataContainerService containerService)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
        _containerService = containerService;
    }

    public async Task HandleTerminalSessionAsync(
        string sandboxId,
        WebSocket browserWebSocket,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting terminal session for sandbox {SandboxId}", sandboxId);

        WebSocket? k8sWebSocket = null;

        try
        {
            // Update activity timestamp
            _ = _containerService.UpdateLastActivityAsync(sandboxId, cancellationToken);

            // Connect to Kubernetes exec
            k8sWebSocket = await ConnectToKubernetesExecAsync(sandboxId, cancellationToken);

            // Track session for resize operations
            _activeSessions[sandboxId] = k8sWebSocket;

            _logger.LogInformation("Connected to Kubernetes exec for sandbox {SandboxId}", sandboxId);

            // Create linked cancellation for when either side disconnects
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Bridge bidirectionally
            var browserToK8s = BridgeBrowserToK8sAsync(browserWebSocket, k8sWebSocket, sandboxId, linkedCts);
            var k8sToBrowser = BridgeK8sToBrowserAsync(k8sWebSocket, browserWebSocket, sandboxId, linkedCts);

            // Wait for either direction to complete (or error)
            await Task.WhenAny(browserToK8s, k8sToBrowser);

            // Cancel the other direction
            linkedCts.Cancel();

            _logger.LogInformation("Terminal session ended for sandbox {SandboxId}", sandboxId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal session error for sandbox {SandboxId}", sandboxId);
            throw;
        }
        finally
        {
            _activeSessions.TryRemove(sandboxId, out _);

            // Close WebSockets gracefully
            if (k8sWebSocket != null && k8sWebSocket.State == WebSocketState.Open)
            {
                try
                {
                    await k8sWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                }
                catch { /* Ignore close errors */ }
            }

            if (browserWebSocket.State == WebSocketState.Open)
            {
                try
                {
                    await browserWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                }
                catch { /* Ignore close errors */ }
            }
        }
    }

    public async Task ResizeTerminalAsync(
        string sandboxId,
        int cols,
        int rows,
        CancellationToken cancellationToken = default)
    {
        if (!_activeSessions.TryGetValue(sandboxId, out var k8sWebSocket))
        {
            _logger.LogWarning("No active session found for resize: {SandboxId}", sandboxId);
            return;
        }

        if (k8sWebSocket.State != WebSocketState.Open)
        {
            _logger.LogWarning("K8s WebSocket not open for resize: {SandboxId}", sandboxId);
            return;
        }

        try
        {
            // Kubernetes resize format: channel byte + JSON {"Width": cols, "Height": rows}
            var resizeJson = JsonSerializer.Serialize(new { Width = cols, Height = rows });
            var resizeBytes = Encoding.UTF8.GetBytes(resizeJson);

            var message = new byte[resizeBytes.Length + 1];
            message[0] = ResizeChannel;
            Buffer.BlockCopy(resizeBytes, 0, message, 1, resizeBytes.Length);

            await k8sWebSocket.SendAsync(
                new ArraySegment<byte>(message),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);

            _logger.LogDebug("Sent resize {Cols}x{Rows} to sandbox {SandboxId}", cols, rows, sandboxId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send resize to sandbox {SandboxId}", sandboxId);
        }
    }

    private async Task<WebSocket> ConnectToKubernetesExecAsync(
        string sandboxId,
        CancellationToken cancellationToken)
    {
        // Build exec URL with proper parameters
        // Using /bin/sh for broader compatibility, falling back from /bin/bash
        var webSocket = await _client.WebSocketNamespacedPodExecAsync(
            name: sandboxId,
            @namespace: _config.TargetNamespace,
            command: new[] { "/bin/bash" },
            container: "workload",
            stderr: true,
            stdin: true,
            stdout: true,
            tty: true,
            cancellationToken: cancellationToken);

        return webSocket;
    }

    /// <summary>
    /// Reads from browser WebSocket, prepends stdin channel byte, sends to K8s.
    /// Also handles special JSON messages for resize.
    /// </summary>
    private async Task BridgeBrowserToK8sAsync(
        WebSocket browserWebSocket,
        WebSocket k8sWebSocket,
        string sandboxId,
        CancellationTokenSource linkedCts)
    {
        var buffer = new byte[4096];
        var cancellationToken = linkedCts.Token;

        try
        {
            while (browserWebSocket.State == WebSocketState.Open &&
                   k8sWebSocket.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                var result = await browserWebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Browser WebSocket closed for sandbox {SandboxId}", sandboxId);
                    break;
                }

                if (result.Count == 0) continue;

                // Update activity on input
                _ = _containerService.UpdateLastActivityAsync(sandboxId, cancellationToken);

                // Check if this is a JSON control message (resize)
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (TryParseControlMessage(text, out var controlType, out var payload))
                    {
                        if (controlType == "resize" && payload != null)
                        {
                            await SendResizeToK8sAsync(k8sWebSocket, payload, cancellationToken);
                            continue;
                        }
                    }
                    // If not a control message, treat text as stdin
                    await SendStdinToK8sAsync(k8sWebSocket, buffer, result.Count, cancellationToken);
                }
                else
                {
                    // Binary data goes to stdin
                    await SendStdinToK8sAsync(k8sWebSocket, buffer, result.Count, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when session ends
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Browser WebSocket error for sandbox {SandboxId}", sandboxId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bridging browser to K8s for sandbox {SandboxId}", sandboxId);
        }
        finally
        {
            linkedCts.Cancel();
        }
    }

    /// <summary>
    /// Reads from K8s exec WebSocket, strips channel byte, sends to browser.
    /// </summary>
    private async Task BridgeK8sToBrowserAsync(
        WebSocket k8sWebSocket,
        WebSocket browserWebSocket,
        string sandboxId,
        CancellationTokenSource linkedCts)
    {
        var buffer = new byte[4096];
        var cancellationToken = linkedCts.Token;

        try
        {
            while (k8sWebSocket.State == WebSocketState.Open &&
                   browserWebSocket.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                var result = await k8sWebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("K8s WebSocket closed for sandbox {SandboxId}", sandboxId);
                    break;
                }

                if (result.Count <= 1) continue; // Need at least channel byte + data

                // First byte is channel indicator
                var channel = buffer[0];
                var dataLength = result.Count - 1;

                // Only forward stdout and stderr (channels 1 and 2)
                if (channel == StdoutChannel || channel == StderrChannel)
                {
                    // Send data without the channel byte
                    await browserWebSocket.SendAsync(
                        new ArraySegment<byte>(buffer, 1, dataLength),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken);
                }
                else if (channel == StderrChannel)
                {
                    // stderr - could wrap in a JSON message to distinguish, but for now just forward
                    await browserWebSocket.SendAsync(
                        new ArraySegment<byte>(buffer, 1, dataLength),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when session ends
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "K8s WebSocket error for sandbox {SandboxId}", sandboxId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bridging K8s to browser for sandbox {SandboxId}", sandboxId);
        }
        finally
        {
            linkedCts.Cancel();
        }
    }

    private async Task SendStdinToK8sAsync(WebSocket k8sWebSocket, byte[] data, int length, CancellationToken cancellationToken)
    {
        // Prepend stdin channel byte
        var message = new byte[length + 1];
        message[0] = StdinChannel;
        Buffer.BlockCopy(data, 0, message, 1, length);

        await k8sWebSocket.SendAsync(
            new ArraySegment<byte>(message),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken);
    }

    private async Task SendResizeToK8sAsync(WebSocket k8sWebSocket, JsonElement payload, CancellationToken cancellationToken)
    {
        try
        {
            var cols = payload.GetProperty("cols").GetInt32();
            var rows = payload.GetProperty("rows").GetInt32();

            var resizeJson = JsonSerializer.Serialize(new { Width = cols, Height = rows });
            var resizeBytes = Encoding.UTF8.GetBytes(resizeJson);

            var message = new byte[resizeBytes.Length + 1];
            message[0] = ResizeChannel;
            Buffer.BlockCopy(resizeBytes, 0, message, 1, resizeBytes.Length);

            await k8sWebSocket.SendAsync(
                new ArraySegment<byte>(message),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);

            _logger.LogDebug("Sent resize {Cols}x{Rows}", cols, rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse/send resize message");
        }
    }

    private bool TryParseControlMessage(string text, out string? type, out JsonElement? payload)
    {
        type = null;
        payload = null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElement))
            {
                type = typeElement.GetString();
                if (root.TryGetProperty("payload", out var payloadElement))
                {
                    payload = payloadElement.Clone();
                }
                return true;
            }
        }
        catch
        {
            // Not valid JSON control message
        }

        return false;
    }
}
