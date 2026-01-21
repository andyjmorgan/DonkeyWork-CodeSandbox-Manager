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
            .WithDescription("Retrieves a list of all Kata containers running in the sandbox-containers namespace")
            .Produces<List<KataContainerInfo>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

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

        group.MapPost("/{sandboxId}/execute", ExecuteCommand)
            .WithName("ExecuteCommand")
            .WithSummary("Execute a command in a sandbox")
            .WithDescription("Forwards a command execution request to the CodeExecution API running inside the specified sandbox. Returns Server-Sent Events (SSE) stream with output and completion events. Updates the sandbox's last activity timestamp.")
            .Produces<ExecutionEvent>(StatusCodes.Status200OK, "text/event-stream")
            .ProducesProblem(StatusCodes.Status404NotFound)
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
            "Create container endpoint called. Image: {Image}, WaitForReady: {WaitForReady}",
            request.Image ?? "(default)",
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
}
