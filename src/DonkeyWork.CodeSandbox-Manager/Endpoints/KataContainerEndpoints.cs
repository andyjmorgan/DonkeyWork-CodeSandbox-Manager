using DonkeyWork.CodeSandbox_Manager.Models;
using DonkeyWork.CodeSandbox_Manager.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DonkeyWork.CodeSandbox_Manager.Endpoints;

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
    }

    private static async Task<Results<Created<KataContainerInfo>, BadRequest<object>, ProblemHttpResult>> CreateContainer(
        CreateContainerRequest request,
        IKataContainerService containerService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var container = await containerService.CreateContainerAsync(request, cancellationToken);
            return TypedResults.Created($"/api/kata/{container.Name}", container);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid request to create container");
            return TypedResults.BadRequest((object)new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create container at API layer");
            return TypedResults.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to create container");
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
}
