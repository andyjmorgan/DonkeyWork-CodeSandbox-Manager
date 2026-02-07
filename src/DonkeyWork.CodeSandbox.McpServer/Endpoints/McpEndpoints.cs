using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DonkeyWork.CodeSandbox.McpServer.Models;
using DonkeyWork.CodeSandbox.McpServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace DonkeyWork.CodeSandbox.McpServer.Endpoints;

public static class McpEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void MapMcpEndpoints(this WebApplication app)
    {
        // MCP protocol endpoints - for MCP Inspector / Streamable HTTP clients
        app.MapPost("/mcp", ProxyRequestAsync)
            .WithName("ProxyMcpRequest")
            .WithSummary("Proxy a JSON-RPC request to the MCP stdio server")
            .WithDescription("Accepts a JSON-RPC request body, writes it to the MCP server's stdin, reads the response from stdout.")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        app.MapGet("/mcp", NotificationStreamAsync)
            .WithName("McpNotificationStream")
            .WithSummary("SSE stream of server-initiated notifications and requests")
            .WithDescription("Opens a long-lived SSE connection that streams server-initiated JSON-RPC messages (notifications, elicitation requests, etc).");

        // Management endpoints
        var api = app.MapGroup("/api/mcp");

        api.MapPost("/start", StartAsync)
            .WithName("StartMcpServer")
            .WithSummary("Initialize and start an MCP stdio server")
            .WithDescription("Runs pre-exec scripts then launches the MCP server process. Streams SSE events during startup.");

        api.MapGet("/status", GetStatus)
            .WithName("GetMcpStatus")
            .WithSummary("Get the current MCP server state")
            .Produces<McpServerStatusResponse>();

        api.MapDelete("/", Stop)
            .WithName("StopMcpServer")
            .WithSummary("Stop the MCP server process and reset to idle")
            .Produces(StatusCodes.Status200OK);
    }

    private static async Task StartAsync(
        [FromBody] StartRequest request,
        StdioBridge bridge,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";

        if (bridge.State != McpServerState.Idle && bridge.State != McpServerState.Error)
        {
            var errorEvt = new McpStartEvent
            {
                EventType = "error",
                Message = $"MCP server is already in state: {bridge.State}"
            };
            await WriteSseEvent(context.Response, errorEvt, cancellationToken);
            return;
        }

        var channel = Channel.CreateUnbounded<McpStartEvent>();

        var startTask = Task.Run(async () =>
        {
            try
            {
                await bridge.StartAsync(request, channel.Writer, cancellationToken);
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new McpStartEvent
                {
                    EventType = "error",
                    Message = ex.Message
                });
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            await WriteSseEvent(context.Response, evt, cancellationToken);
        }

        await startTask;
    }

    private static async Task WriteSseEvent(HttpResponse response, McpStartEvent evt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(evt, SseJsonOptions);
        var sseMessage = $"data: {json}\n\n";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(sseMessage), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task<IResult> ProxyRequestAsync(
        HttpRequest httpRequest,
        StdioBridge bridge,
        CancellationToken cancellationToken)
    {
        if (bridge.State != McpServerState.Ready)
        {
            return Results.Json(
                new { error = $"MCP server is not ready: {bridge.State}" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        string body;
        using (var reader = new StreamReader(httpRequest.Body))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Results.BadRequest(new { error = "Request body is empty" });
        }

        try
        {
            var response = await bridge.SendRequestAsync(body, bridge.GetStatus().State == McpServerState.Ready ? 30 : 10, cancellationToken);

            if (response is null)
            {
                // Notification - no response expected
                return Results.Accepted();
            }

            return Results.Text(response, "application/json");
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "MCP server timeout");
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                new { error = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (IOException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway,
                title: "MCP server connection lost");
        }
    }

    private static async Task NotificationStreamAsync(
        StdioBridge bridge,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";

        var reader = bridge.GetNotificationReader();
        if (reader is null)
        {
            var msg = $"data: {{\"jsonrpc\":\"2.0\",\"method\":\"notifications/error\",\"params\":{{\"message\":\"MCP server is not running\"}}}}\n\n";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(msg), cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            return;
        }

        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken))
            {
                var sseMessage = $"data: {message}\n\n";
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(sseMessage), cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    }

    private static IResult GetStatus(StdioBridge bridge)
    {
        return Results.Ok(bridge.GetStatus());
    }

    private static IResult Stop(StdioBridge bridge)
    {
        bridge.Stop();
        return Results.Ok(new { status = "stopped" });
    }
}
