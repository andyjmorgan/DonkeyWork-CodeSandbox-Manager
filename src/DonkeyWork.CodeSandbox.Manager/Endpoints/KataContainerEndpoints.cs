using DonkeyWork.CodeSandbox.Manager.Filters;
using DonkeyWork.CodeSandbox.Manager.Models;
using DonkeyWork.CodeSandbox.Manager.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DonkeyWork.CodeSandbox.Manager.Endpoints;

public static class KataContainerEndpoints
{
    public static void MapKataContainerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/kata")
            .WithTags("Kata Containers");

        group.MapPost("/", CreateContainer)
            .WithName("CreateContainer")
            .WithSummary("Create a new Kata container")
            .WithDescription("Creates a new Kata container with VM isolation using the specified container image and configuration")
            .Produces<KataContainerInfo>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("/", ListContainers)
            .WithName("ListContainers")
            .WithSummary("List all Kata containers")
            .WithDescription("Retrieves a list of all Kata containers running in the sandbox-containers namespace. Requires API key.")
            .Produces<List<KataContainerInfo>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        group.MapGet("/{podName}", GetContainer)
            .WithName("GetContainer")
            .WithSummary("Get a specific Kata container")
            .WithDescription("Retrieves detailed information about a specific Kata container by its pod name")
            .Produces<KataContainerInfo>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapDelete("/{podName}", DeleteContainer)
            .WithName("DeleteContainer")
            .WithSummary("Delete a Kata container")
            .WithDescription("Deletes a Kata container and terminates its associated VM")
            .Produces<DeleteContainerResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapDelete("/", DeleteAllContainers)
            .WithName("DeleteAllContainers")
            .WithSummary("Delete all Kata containers")
            .WithDescription("Deletes all Kata containers in the sandbox-containers namespace. Requires API key.")
            .Produces<DeleteAllContainersResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        group.MapPost("/{sandboxId}/execute", ExecuteCommand)
            .WithName("ExecuteCommand")
            .WithSummary("Execute a command in a sandbox")
            .WithDescription("Forwards a command execution request to the CodeExecution API running inside the specified sandbox. Returns Server-Sent Events (SSE) stream with output and completion events. Updates the sandbox's last activity timestamp.")
            .Produces<ExecutionEvent>(StatusCodes.Status200OK, "text/event-stream")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/allocate", AllocateSandbox)
            .WithName("AllocateSandbox")
            .WithSummary("Allocate a warm sandbox from the pool")
            .WithDescription("Atomically allocates a pre-warmed sandbox from the pool to a user. If no warm sandboxes are available, returns 503 Service Unavailable.")
            .Produces<KataContainerInfo>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("/pool/status", GetPoolStatus)
            .WithName("GetPoolStatus")
            .WithSummary("Get warm pool status")
            .WithDescription("Returns the current status of the warm sandbox pool including warm count and allocated count")
            .Produces<PoolStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task CreateContainer(
        CreateContainerRequest request,
        IKataContainerService containerService,
        ILogger<Program> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Create container endpoint called. WaitForReady: {WaitForReady}",
            request.WaitForReady
        );

        // Set headers for SSE
        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";

        try
        {
            // Stream events to client in SSE format
            await foreach (var evt in containerService.CreateContainerWithEventsAsync(request, cancellationToken))
            {
                logger.LogInformation(
                    "Streaming event. Type: {EventType}, PodName: {PodName}",
                    evt.EventType,
                    evt.PodName
                );

                // Write SSE format: "data: {...}\n\n"
                var json = System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType(), new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });

                var sseMessage = $"data: {json}\n\n";
                await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(sseMessage), cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid request to create container");

            var errorEvent = new ContainerFailedEvent
            {
                PodName = "(none)",
                Reason = $"Validation error: {ex.Message}"
            };

            var json = System.Text.Json.JsonSerializer.Serialize(errorEvent, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            var sseMessage = $"data: {json}\n\n";
            await context.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(sseMessage), cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create container at API layer");

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

    private static async Task<Results<Ok<List<KataContainerInfo>>, ProblemHttpResult>> ListContainers(
        IKataContainerService containerService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var containers = await containerService.ListContainersAsync(cancellationToken);
            return TypedResults.Ok(containers);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list containers at API layer");
            return TypedResults.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to list containers");
        }
    }

    private static async Task<Results<Ok<KataContainerInfo>, NotFound<object>, ProblemHttpResult>> GetContainer(
        string podName,
        IKataContainerService containerService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var container = await containerService.GetContainerAsync(podName, cancellationToken);

            if (container == null)
            {
                return TypedResults.NotFound((object)new { error = $"Container {podName} not found" });
            }

            return TypedResults.Ok(container);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get container at API layer: {PodName}", podName);
            return TypedResults.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to get container");
        }
    }

    private static async Task<Results<Ok<DeleteContainerResponse>, NotFound<DeleteContainerResponse>, ProblemHttpResult>> DeleteContainer(
        string podName,
        IKataContainerService containerService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await containerService.DeleteContainerAsync(podName, cancellationToken);

            if (!response.Success)
            {
                return TypedResults.NotFound(response);
            }

            return TypedResults.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete container at API layer: {PodName}", podName);
            return TypedResults.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to delete container");
        }
    }

    private static async Task<Results<Ok<DeleteAllContainersResponse>, ProblemHttpResult>> DeleteAllContainers(
        IKataContainerService containerService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await containerService.DeleteAllContainersAsync(cancellationToken);
            return TypedResults.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete all containers at API layer");
            return TypedResults.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to delete all containers");
        }
    }

    private static async Task ExecuteCommand(
        string sandboxId,
        ExecutionRequest request,
        IKataContainerService containerService,
        ILogger<Program> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            // Set SSE headers
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            // Stream response directly from CodeExecution API to client
            await containerService.ExecuteCommandAsync(sandboxId, request, context.Response.Body, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Invalid operation while executing command in sandbox {SandboxId}", sandboxId);

            // Write error event
            var errorJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                error = ex.Message,
                sandboxId
            });
            await context.Response.WriteAsync($"data: {errorJson}\n\n", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute command in sandbox {SandboxId}", sandboxId);

            // Write error event
            var errorJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                error = "Failed to execute command",
                message = ex.Message,
                sandboxId
            });
            await context.Response.WriteAsync($"data: {errorJson}\n\n", cancellationToken);
        }
    }

    private static async Task<Results<Ok<KataContainerInfo>, ServiceUnavailable, BadRequest<string>, ProblemHttpResult>> AllocateSandbox(
        AllocateSandboxRequest request,
        IPoolManager poolManager,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                return TypedResults.BadRequest("UserId is required");
            }

            logger.LogInformation("Allocating warm sandbox for user: {UserId}", request.UserId);

            var container = await poolManager.AllocateWarmSandboxAsync(request.UserId, cancellationToken);

            if (container == null)
            {
                logger.LogWarning("No warm sandboxes available for user: {UserId}", request.UserId);
                return TypedResults.ServiceUnavailable();
            }

            logger.LogInformation("Successfully allocated sandbox {PodName} to user {UserId}",
                container.Name, request.UserId);

            return TypedResults.Ok(container);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to allocate sandbox for user: {UserId}", request.UserId);
            return TypedResults.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to allocate sandbox");
        }
    }

    private static async Task<Results<Ok<PoolStatusResponse>, ProblemHttpResult>> GetPoolStatus(
        IPoolManager poolManager,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var stats = await poolManager.GetPoolStatisticsAsync(cancellationToken);

            var response = new PoolStatusResponse
            {
                Creating = stats.Creating,
                Warm = stats.Warm,
                Allocated = stats.Allocated,
                Total = stats.Total,
                TargetSize = stats.TargetSize,
                ReadyPercentage = stats.ReadyPercentage,
                UtilizationPercentage = stats.UtilizationPercentage,
                // Legacy fields for backward compatibility
                WarmCount = stats.Warm,
                AllocatedCount = stats.Allocated,
                TotalCount = stats.Total
            };

            return TypedResults.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get pool status");
            return TypedResults.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to get pool status");
        }
    }
}

public record PoolStatusResponse
{
    public int Creating { get; init; }
    public int Warm { get; init; }
    public int Allocated { get; init; }
    public int Total { get; init; }
    public int TargetSize { get; init; }
    public double ReadyPercentage { get; init; }
    public double UtilizationPercentage { get; init; }

    // Legacy fields for backward compatibility
    public int WarmCount { get; init; }
    public int AllocatedCount { get; init; }
    public int TotalCount { get; init; }
}
