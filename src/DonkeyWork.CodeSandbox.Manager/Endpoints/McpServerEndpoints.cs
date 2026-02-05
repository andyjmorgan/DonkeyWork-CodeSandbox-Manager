using DonkeyWork.CodeSandbox.Manager.Models;
using DonkeyWork.CodeSandbox.Manager.Services.Mcp;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DonkeyWork.CodeSandbox.Manager.Endpoints;

public static class McpServerEndpoints
{
    public static void MapMcpServerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mcp-servers")
            .WithTags("MCP Servers");

        group.MapPost("/", CreateMcpServer)
            .WithName("CreateMcpServer")
            .WithSummary("Create a new MCP server container")
            .WithDescription("Creates a Kata container with the MCP server image. If launchCommand is provided, also starts the MCP process. Returns SSE stream with creation events.");

        group.MapGet("/", ListMcpServers)
            .WithName("ListMcpServers")
            .WithSummary("List all MCP server containers")
            .Produces<List<McpServerInfo>>(StatusCodes.Status200OK);

        group.MapGet("/{podName}", GetMcpServer)
            .WithName("GetMcpServer")
            .WithSummary("Get a specific MCP server")
            .Produces<McpServerInfo>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{podName}", DeleteMcpServer)
            .WithName("DeleteMcpServer")
            .WithSummary("Delete an MCP server container")
            .Produces<DeleteContainerResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/allocate", AllocateMcpServer)
            .WithName("AllocateMcpServer")
            .WithSummary("Allocate a warm MCP server from the pool")
            .Produces<KataContainerInfo>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{podName}/start", StartMcpProcess)
            .WithName("StartMcpProcess")
            .WithSummary("Start (arm) the MCP process inside a container")
            .WithDescription("Sends the launch command and pre-exec scripts to start the MCP stdio process inside an already-running container.");

        group.MapPost("/{podName}/proxy", ProxyMcpRequest)
            .WithName("ProxyMcpRequest")
            .WithSummary("Proxy a JSON-RPC request to the MCP server")
            .WithDescription("Forwards raw JSON-RPC body to the MCP server running inside the container and returns the response.");

        group.MapGet("/{podName}/status", GetMcpStatus)
            .WithName("GetMcpStatus")
            .WithSummary("Get MCP process status")
            .Produces<McpStatusResponse>(StatusCodes.Status200OK);

        group.MapDelete("/{podName}/process", StopMcpProcess)
            .WithName("StopMcpProcess")
            .WithSummary("Stop the MCP process (keep pod alive)")
            .WithDescription("Stops the MCP stdio process inside the container but keeps the pod running for reuse.");

        group.MapGet("/pool/status", GetMcpPoolStatus)
            .WithName("GetMcpPoolStatus")
            .WithSummary("Get MCP warm pool status")
            .Produces<PoolStatusResponse>(StatusCodes.Status200OK);
    }

    private static async Task CreateMcpServer(
        CreateMcpServerRequest request,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Create MCP server endpoint called. LaunchCommand: {Command}", request.LaunchCommand ?? "(none)");

        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";

        try
        {
            await foreach (var evt in mcpService.CreateMcpServerAsync(request, cancellationToken))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType(), new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });

                var sseMessage = $"data: {json}\n\n";
                await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(sseMessage), cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create MCP server");

            var errorEvent = new ContainerFailedEvent
            {
                PodName = "(none)",
                Reason = $"Unexpected error: {ex.Message}"
            };

            var json = System.Text.Json.JsonSerializer.Serialize(errorEvent, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            var sseMessage = $"data: {json}\n\n";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(sseMessage), cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static async Task<Results<Ok<List<McpServerInfo>>, ProblemHttpResult>> ListMcpServers(
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var servers = await mcpService.ListMcpServersAsync(cancellationToken);
            return TypedResults.Ok(servers);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list MCP servers");
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to list MCP servers");
        }
    }

    private static async Task<Results<Ok<McpServerInfo>, NotFound<object>, ProblemHttpResult>> GetMcpServer(
        string podName,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var server = await mcpService.GetMcpServerAsync(podName, cancellationToken);
            if (server == null)
                return TypedResults.NotFound((object)new { error = $"MCP server {podName} not found" });
            return TypedResults.Ok(server);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get MCP server: {PodName}", podName);
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to get MCP server");
        }
    }

    private static async Task<Results<Ok<DeleteContainerResponse>, NotFound<DeleteContainerResponse>, ProblemHttpResult>> DeleteMcpServer(
        string podName,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await mcpService.DeleteMcpServerAsync(podName, cancellationToken);
            if (!response.Success)
                return TypedResults.NotFound(response);
            return TypedResults.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete MCP server: {PodName}", podName);
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to delete MCP server");
        }
    }

    private static async Task<Results<Ok<KataContainerInfo>, StatusCodeHttpResult, BadRequest<string>, ProblemHttpResult>> AllocateMcpServer(
        AllocateSandboxRequest request,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
                return TypedResults.BadRequest("UserId is required");

            var container = await mcpService.AllocateWarmMcpServerAsync(request.UserId, cancellationToken);
            if (container == null)
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

            return TypedResults.Ok(container);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to allocate MCP server");
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to allocate MCP server");
        }
    }

    private static async Task<Results<Ok, ProblemHttpResult>> StartMcpProcess(
        string podName,
        McpStartRequest request,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await mcpService.StartMcpProcessAsync(podName, request, cancellationToken);
            return TypedResults.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start MCP process in {PodName}", podName);
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to start MCP process");
        }
    }

    private static async Task ProxyMcpRequest(
        string podName,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);

            var response = await mcpService.ProxyMcpRequestAsync(podName, body, cancellationToken);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to proxy MCP request to {PodName}", podName);
            context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken);
        }
    }

    private static async Task<Results<Ok<McpStatusResponse>, ProblemHttpResult>> GetMcpStatus(
        string podName,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await mcpService.GetMcpStatusAsync(podName, cancellationToken);
            return TypedResults.Ok(status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get MCP status for {PodName}", podName);
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to get MCP status");
        }
    }

    private static async Task<Results<Ok, ProblemHttpResult>> StopMcpProcess(
        string podName,
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await mcpService.StopMcpProcessAsync(podName, cancellationToken);
            return TypedResults.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop MCP process in {PodName}", podName);
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to stop MCP process");
        }
    }

    private static async Task<Results<Ok<PoolStatusResponse>, ProblemHttpResult>> GetMcpPoolStatus(
        IMcpContainerService mcpService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var stats = await mcpService.GetMcpPoolStatisticsAsync(cancellationToken);

            var response = new PoolStatusResponse
            {
                Creating = stats.Creating,
                Warm = stats.Warm,
                Allocated = stats.Allocated,
                Total = stats.Total,
                TargetSize = stats.TargetSize,
                ReadyPercentage = stats.ReadyPercentage,
                UtilizationPercentage = stats.UtilizationPercentage,
                WarmCount = stats.Warm,
                AllocatedCount = stats.Allocated,
                TotalCount = stats.Total
            };

            return TypedResults.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get MCP pool status");
            return TypedResults.Problem(detail: ex.Message, statusCode: 500, title: "Failed to get MCP pool status");
        }
    }
}
