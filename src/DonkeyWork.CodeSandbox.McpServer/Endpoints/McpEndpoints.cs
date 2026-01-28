using System.Text.Json;
using DonkeyWork.CodeSandbox.McpServer.Models;
using DonkeyWork.CodeSandbox.McpServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace DonkeyWork.CodeSandbox.McpServer.Endpoints;

public static class McpEndpoints
{
    public static void MapMcpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mcp");

        group.MapPost("/start", StartAsync)
            .WithName("StartMcpServer")
            .WithSummary("Initialize and start an MCP stdio server")
            .WithDescription("Runs pre-exec scripts then launches the MCP server process. Returns when ready.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/", ProxyRequestAsync)
            .WithName("ProxyMcpRequest")
            .WithSummary("Proxy a JSON-RPC request to the MCP stdio server")
            .WithDescription("Accepts a JSON-RPC request body, writes it to the MCP server's stdin, reads the response from stdout.")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        group.MapGet("/status", GetStatus)
            .WithName("GetMcpStatus")
            .WithSummary("Get the current MCP server state")
            .Produces<McpServerStatusResponse>();

        group.MapDelete("/", Stop)
            .WithName("StopMcpServer")
            .WithSummary("Stop the MCP server process and reset to idle")
            .Produces(StatusCodes.Status200OK);
    }

    private static async Task<IResult> StartAsync(
        [FromBody] StartRequest request,
        StdioBridge bridge,
        CancellationToken cancellationToken)
    {
        if (bridge.State != McpServerState.Idle)
        {
            return Results.Conflict(new { error = $"MCP server is already in state: {bridge.State}" });
        }

        try
        {
            await bridge.StartAsync(request, cancellationToken);
            return Results.Ok(new { status = "ready" });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to start MCP server");
        }
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
