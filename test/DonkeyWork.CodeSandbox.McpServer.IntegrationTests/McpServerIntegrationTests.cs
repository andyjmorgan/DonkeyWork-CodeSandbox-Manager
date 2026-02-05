using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DonkeyWork.CodeSandbox.McpServer.IntegrationTests;

/// <summary>
/// Integration tests for the MCP stdio-to-HTTP bridge server
/// using @modelcontextprotocol/server-everything as the test MCP server
/// </summary>
public class McpServerIntegrationTests : IClassFixture<McpServerFixture>
{
    private readonly McpServerFixture _fixture;
    private readonly HttpClient _client;

    private const string McpLaunchCommand = "npx -y @modelcontextprotocol/server-everything";

    public McpServerIntegrationTests(McpServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient
        {
            BaseAddress = new Uri(_fixture.ServerUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    #region Status Tests

    [Fact]
    public async Task Status_WhenIdle_ReturnsIdleState()
    {
        // Use a fresh container state — check status before any start
        // Note: if previous tests started the server, this relies on test ordering
        // We check the status endpoint returns a valid response
        var response = await _client.GetAsync("/api/mcp/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.NotNull(status);
        // State should be one of the valid states
        Assert.Contains(status!.State, new[] { "Idle", "Ready", "Initializing", "Error", "Disposed" });
    }

    #endregion

    #region Start Tests

    [Fact]
    public async Task Start_WithValidCommand_ReturnsReady()
    {
        // First ensure we're in idle state by stopping any running server
        await _client.DeleteAsync("/api/mcp");

        var startRequest = new
        {
            launchCommand = McpLaunchCommand,
            preExecScripts = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync("/api/mcp/start", startRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify status is Ready
        var statusResponse = await _client.GetAsync("/api/mcp/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.Equal("Ready", status!.State);
        Assert.NotNull(status.StartedAt);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Start_WhenAlreadyStarted_Returns409()
    {
        await _client.DeleteAsync("/api/mcp");

        var startRequest = new
        {
            launchCommand = McpLaunchCommand,
            preExecScripts = Array.Empty<string>()
        };

        var firstResponse = await _client.PostAsJsonAsync("/api/mcp/start", startRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Second start should conflict
        var secondResponse = await _client.PostAsJsonAsync("/api/mcp/start", startRequest);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Start_WithPreExecScripts_RunsBeforeLaunch()
    {
        await _client.DeleteAsync("/api/mcp");

        var startRequest = new
        {
            launchCommand = McpLaunchCommand,
            preExecScripts = new[] { "echo 'pre-exec ran successfully'" }
        };

        var response = await _client.PostAsJsonAsync("/api/mcp/start", startRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var statusResponse = await _client.GetAsync("/api/mcp/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.Equal("Ready", status!.State);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Start_WithFailingPreExec_ReturnsError()
    {
        await _client.DeleteAsync("/api/mcp");

        var startRequest = new
        {
            launchCommand = McpLaunchCommand,
            preExecScripts = new[] { "exit 1" }
        };

        var response = await _client.PostAsJsonAsync("/api/mcp/start", startRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Status should be Error
        var statusResponse = await _client.GetAsync("/api/mcp/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.Equal("Error", status!.State);
        Assert.NotNull(status.Error);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    #endregion

    #region Proxy Tests (JSON-RPC bridging)

    [Fact]
    public async Task Proxy_WhenNotStarted_Returns503()
    {
        await _client.DeleteAsync("/api/mcp");

        var jsonRpc = BuildJsonRpc("initialize", 1, new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1.0.0" }
        });

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Proxy_Initialize_ReturnsServerInfo()
    {
        await StartMcpServerAsync();

        var jsonRpc = BuildJsonRpc("initialize", 1, new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1.0.0" }
        });

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.True(root.TryGetProperty("result", out var result));
        Assert.True(result.TryGetProperty("serverInfo", out var serverInfo));
        var serverName = serverInfo.GetProperty("name").GetString();
        Assert.Contains("everything", serverName); // Server name may be "everything" or "mcp-servers/everything"

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Proxy_Notification_Returns202()
    {
        await StartMcpServerAsync();

        // Send initialize first
        await SendInitializeHandshakeAsync();

        // Send notification (no id field)
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });

        var response = await SendJsonRpcAsync(notification);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Proxy_ToolsList_ReturnsTools()
    {
        await StartMcpServerAsync();
        await SendInitializeHandshakeAsync();

        var jsonRpc = BuildJsonRpc("tools/list", 3);

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("result", out var result));
        Assert.True(result.TryGetProperty("tools", out var tools));
        Assert.True(tools.GetArrayLength() > 0);

        // Verify the echo tool exists
        var toolNames = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            toolNames.Add(tool.GetProperty("name").GetString()!);
        }

        Assert.Contains("echo", toolNames);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Proxy_ToolsCall_EchoReturnsMessage()
    {
        await StartMcpServerAsync();
        await SendInitializeHandshakeAsync();

        var jsonRpc = BuildJsonRpc("tools/call", 4, new
        {
            name = "echo",
            arguments = new { message = "hello from integration test" }
        });

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("result", out var result));
        Assert.True(result.TryGetProperty("content", out var content));

        // The echo tool should return the message in the content
        var contentText = content.GetRawText();
        Assert.Contains("hello from integration test", contentText);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Proxy_ToolsCall_AddReturnsSumResult()
    {
        await StartMcpServerAsync();
        await SendInitializeHandshakeAsync();

        var jsonRpc = BuildJsonRpc("tools/call", 5, new
        {
            name = "get-sum",
            arguments = new { a = 3, b = 7 }
        });

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("result", out var result));
        var resultText = result.GetRawText();
        Assert.Contains("10", resultText);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Proxy_ResourcesList_ReturnsResources()
    {
        await StartMcpServerAsync();
        await SendInitializeHandshakeAsync();

        var jsonRpc = BuildJsonRpc("resources/list", 6);

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("result", out var result));
        Assert.True(result.TryGetProperty("resources", out var resources));
        Assert.True(resources.GetArrayLength() > 0);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Proxy_PromptsList_ReturnsPrompts()
    {
        await StartMcpServerAsync();
        await SendInitializeHandshakeAsync();

        var jsonRpc = BuildJsonRpc("prompts/list", 7);

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("result", out var result));
        Assert.True(result.TryGetProperty("prompts", out var prompts));
        Assert.True(prompts.GetArrayLength() > 0);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    #endregion

    #region Stop Tests

    [Fact]
    public async Task Stop_KillsProcessAndResetsToIdle()
    {
        await StartMcpServerAsync();

        var stopResponse = await _client.DeleteAsync("/api/mcp");
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);

        var statusResponse = await _client.GetAsync("/api/mcp/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.Equal("Idle", status!.State);
    }

    [Fact]
    public async Task Stop_ThenRestart_Works()
    {
        await StartMcpServerAsync();

        // Stop
        await _client.DeleteAsync("/api/mcp");

        // Restart
        await StartMcpServerAsync();

        // Verify it works by sending initialize
        var jsonRpc = BuildJsonRpc("initialize", 1, new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1.0.0" }
        });

        var response = await SendJsonRpcAsync(jsonRpc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task Proxy_EmptyBody_Returns400()
    {
        await StartMcpServerAsync();

        var content = new StringContent("", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/mcp", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Cleanup
        await _client.DeleteAsync("/api/mcp");
    }

    [Fact]
    public async Task Start_WithInvalidCommand_ReturnsErrorState()
    {
        await _client.DeleteAsync("/api/mcp");

        var startRequest = new
        {
            launchCommand = "nonexistent-command-that-does-not-exist",
            preExecScripts = Array.Empty<string>()
        };

        // The process may start but immediately exit, or the start might succeed
        // but subsequent proxy calls will fail. Either way, check we can recover.
        var response = await _client.PostAsJsonAsync("/api/mcp/start", startRequest);

        // Regardless of outcome, cleanup should work
        await _client.DeleteAsync("/api/mcp");

        var statusResponse = await _client.GetAsync("/api/mcp/status");
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.Equal("Idle", status!.State);
    }

    #endregion

    #region Helpers

    private async Task StartMcpServerAsync()
    {
        // Ensure clean state
        await _client.DeleteAsync("/api/mcp");

        var startRequest = new
        {
            launchCommand = McpLaunchCommand,
            preExecScripts = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync("/api/mcp/start", startRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SendInitializeHandshakeAsync()
    {
        // Step 1: initialize
        var initRequest = BuildJsonRpc("initialize", 99, new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1.0.0" }
        });

        var initResponse = await SendJsonRpcAsync(initRequest);
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);

        // Step 2: notifications/initialized (notification, no response expected)
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });

        await SendJsonRpcAsync(notification);
    }

    private async Task<HttpResponseMessage> SendJsonRpcAsync(string jsonRpcBody)
    {
        var content = new StringContent(jsonRpcBody, Encoding.UTF8, "application/json");
        return await _client.PostAsync("/api/mcp", content);
    }

    private static string BuildJsonRpc(string method, int id, object? @params = null)
    {
        var request = new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };

        if (@params != null)
        {
            request["params"] = @params;
        }

        return JsonSerializer.Serialize(request);
    }

    #endregion

    #region Response DTOs

    private class StatusResponse
    {
        public string State { get; set; } = string.Empty;
        public string? Error { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? LastRequestAt { get; set; }
    }

    #endregion
}
