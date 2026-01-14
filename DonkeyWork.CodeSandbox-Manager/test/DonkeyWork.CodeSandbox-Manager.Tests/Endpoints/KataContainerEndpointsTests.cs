using DonkeyWork.CodeSandbox_Manager.Endpoints;
using DonkeyWork.CodeSandbox_Manager.Models;
using DonkeyWork.CodeSandbox_Manager.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;

namespace DonkeyWork.CodeSandbox_Manager.Tests.Endpoints;

public class KataContainerEndpointsTests
{
    private readonly Mock<IKataContainerService> _mockContainerService;
    private readonly Mock<ILogger<Program>> _mockLogger;

    public KataContainerEndpointsTests()
    {
        _mockContainerService = new Mock<IKataContainerService>();
        _mockLogger = new Mock<ILogger<Program>>();
    }

    #region CreateContainer Tests

    [Fact]
    public async Task CreateContainer_WithValidRequest_ReturnsCreatedResult()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = "nginx:latest"
        };

        var containerInfo = new KataContainerInfo
        {
            Name = "kata-sandbox-12345678",
            Phase = "Running",
            IsReady = true,
            Image = "nginx:latest",
            CreatedAt = DateTime.UtcNow
        };

        _mockContainerService
            .Setup(x => x.CreateContainerAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(containerInfo);

        // Act
        var result = await InvokeCreateContainer(request);

        // Assert
        Assert.IsType<Created<KataContainerInfo>>(result.Result);
        var createdResult = (Created<KataContainerInfo>)result.Result;
        Assert.NotNull(createdResult.Value);
        Assert.Equal("kata-sandbox-12345678", createdResult.Value.Name);
        Assert.Equal("/api/kata/kata-sandbox-12345678", createdResult.Location);

        _mockContainerService.Verify(
            x => x.CreateContainerAsync(request, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateContainer_WithArgumentException_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = ""
        };

        _mockContainerService
            .Setup(x => x.CreateContainerAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Image name is required", nameof(request.Image)));

        // Act
        var result = await InvokeCreateContainer(request);

        // Assert
        Assert.IsType<BadRequest<object>>(result.Result);
        var badRequestResult = (BadRequest<object>)result.Result;
        Assert.NotNull(badRequestResult.Value);

        VerifyLogWarning(_mockLogger, "Invalid request to create container");
    }

    [Fact]
    public async Task CreateContainer_WithInvalidImageFormat_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = "Invalid@Image"
        };

        _mockContainerService
            .Setup(x => x.CreateContainerAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid image name format: Invalid@Image", nameof(request.Image)));

        // Act
        var result = await InvokeCreateContainer(request);

        // Assert
        Assert.IsType<BadRequest<object>>(result.Result);
        VerifyLogWarning(_mockLogger, "Invalid request to create container");
    }

    [Fact]
    public async Task CreateContainer_WithServiceException_ReturnsProblem()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = "nginx:latest"
        };

        _mockContainerService
            .Setup(x => x.CreateContainerAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Kubernetes API error"));

        // Act
        var result = await InvokeCreateContainer(request);

        // Assert
        Assert.IsType<ProblemHttpResult>(result.Result);
        var problemResult = (ProblemHttpResult)result.Result;
        Assert.Equal(StatusCodes.Status500InternalServerError, problemResult.StatusCode);
        Assert.Equal("Failed to create container", problemResult.ProblemDetails.Title);
        Assert.Equal("Kubernetes API error", problemResult.ProblemDetails.Detail);

        VerifyLogError(_mockLogger, "Failed to create container at API layer");
    }

    #endregion

    #region ListContainers Tests

    [Fact]
    public async Task ListContainers_WithContainers_ReturnsOkWithList()
    {
        // Arrange
        var containers = new List<KataContainerInfo>
        {
            new KataContainerInfo
            {
                Name = "kata-sandbox-1",
                Phase = "Running",
                IsReady = true,
                Image = "nginx:latest"
            },
            new KataContainerInfo
            {
                Name = "kata-sandbox-2",
                Phase = "Pending",
                IsReady = false,
                Image = "ubuntu:latest"
            }
        };

        _mockContainerService
            .Setup(x => x.ListContainersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(containers);

        // Act
        var result = await InvokeListContainers();

        // Assert
        Assert.IsType<Ok<List<KataContainerInfo>>>(result.Result);
        var okResult = (Ok<List<KataContainerInfo>>)result.Result;
        Assert.NotNull(okResult.Value);
        Assert.Equal(2, okResult.Value.Count);
        Assert.Equal("kata-sandbox-1", okResult.Value[0].Name);
        Assert.Equal("kata-sandbox-2", okResult.Value[1].Name);

        _mockContainerService.Verify(
            x => x.ListContainersAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListContainers_WithNoContainers_ReturnsOkWithEmptyList()
    {
        // Arrange
        var containers = new List<KataContainerInfo>();

        _mockContainerService
            .Setup(x => x.ListContainersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(containers);

        // Act
        var result = await InvokeListContainers();

        // Assert
        Assert.IsType<Ok<List<KataContainerInfo>>>(result.Result);
        var okResult = (Ok<List<KataContainerInfo>>)result.Result;
        Assert.NotNull(okResult.Value);
        Assert.Empty(okResult.Value);
    }

    [Fact]
    public async Task ListContainers_WithServiceException_ReturnsProblem()
    {
        // Arrange
        _mockContainerService
            .Setup(x => x.ListContainersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Kubernetes API error"));

        // Act
        var result = await InvokeListContainers();

        // Assert
        Assert.IsType<ProblemHttpResult>(result.Result);
        var problemResult = (ProblemHttpResult)result.Result;
        Assert.Equal(StatusCodes.Status500InternalServerError, problemResult.StatusCode);
        Assert.Equal("Failed to list containers", problemResult.ProblemDetails.Title);
        Assert.Equal("Kubernetes API error", problemResult.ProblemDetails.Detail);

        VerifyLogError(_mockLogger, "Failed to list containers at API layer");
    }

    #endregion

    #region GetContainer Tests

    [Fact]
    public async Task GetContainer_WithExistingContainer_ReturnsOkWithContainer()
    {
        // Arrange
        var podName = "kata-sandbox-12345";
        var containerInfo = new KataContainerInfo
        {
            Name = podName,
            Phase = "Running",
            IsReady = true,
            Image = "nginx:latest",
            CreatedAt = DateTime.UtcNow
        };

        _mockContainerService
            .Setup(x => x.GetContainerAsync(podName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(containerInfo);

        // Act
        var result = await InvokeGetContainer(podName);

        // Assert
        Assert.IsType<Ok<KataContainerInfo>>(result.Result);
        var okResult = (Ok<KataContainerInfo>)result.Result;
        Assert.NotNull(okResult.Value);
        Assert.Equal(podName, okResult.Value.Name);
        Assert.Equal("nginx:latest", okResult.Value.Image);

        _mockContainerService.Verify(
            x => x.GetContainerAsync(podName, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetContainer_WithNonExistingContainer_ReturnsNotFound()
    {
        // Arrange
        var podName = "non-existing-pod";

        _mockContainerService
            .Setup(x => x.GetContainerAsync(podName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((KataContainerInfo?)null);

        // Act
        var result = await InvokeGetContainer(podName);

        // Assert
        Assert.IsType<NotFound<object>>(result.Result);
        var notFoundResult = (NotFound<object>)result.Result;
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task GetContainer_WithServiceException_ReturnsProblem()
    {
        // Arrange
        var podName = "kata-sandbox-12345";

        _mockContainerService
            .Setup(x => x.GetContainerAsync(podName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Kubernetes API error"));

        // Act
        var result = await InvokeGetContainer(podName);

        // Assert
        Assert.IsType<ProblemHttpResult>(result.Result);
        var problemResult = (ProblemHttpResult)result.Result;
        Assert.Equal(StatusCodes.Status500InternalServerError, problemResult.StatusCode);
        Assert.Equal("Failed to get container", problemResult.ProblemDetails.Title);
        Assert.Equal("Kubernetes API error", problemResult.ProblemDetails.Detail);

        VerifyLogError(_mockLogger, $"Failed to get container at API layer: {podName}");
    }

    #endregion

    #region DeleteContainer Tests

    [Fact]
    public async Task DeleteContainer_WithExistingContainer_ReturnsOkWithSuccessResponse()
    {
        // Arrange
        var podName = "kata-sandbox-12345";
        var deleteResponse = new DeleteContainerResponse
        {
            Success = true,
            Message = $"Container {podName} deleted successfully",
            PodName = podName
        };

        _mockContainerService
            .Setup(x => x.DeleteContainerAsync(podName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResponse);

        // Act
        var result = await InvokeDeleteContainer(podName);

        // Assert
        Assert.IsType<Ok<DeleteContainerResponse>>(result.Result);
        var okResult = (Ok<DeleteContainerResponse>)result.Result;
        Assert.NotNull(okResult.Value);
        Assert.True(okResult.Value.Success);
        Assert.Equal(podName, okResult.Value.PodName);

        _mockContainerService.Verify(
            x => x.DeleteContainerAsync(podName, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteContainer_WithNonExistingContainer_ReturnsNotFound()
    {
        // Arrange
        var podName = "non-existing-pod";
        var deleteResponse = new DeleteContainerResponse
        {
            Success = false,
            Message = $"Container {podName} not found",
            PodName = podName
        };

        _mockContainerService
            .Setup(x => x.DeleteContainerAsync(podName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deleteResponse);

        // Act
        var result = await InvokeDeleteContainer(podName);

        // Assert
        Assert.IsType<NotFound<DeleteContainerResponse>>(result.Result);
        var notFoundResult = (NotFound<DeleteContainerResponse>)result.Result;
        Assert.NotNull(notFoundResult.Value);
        Assert.False(notFoundResult.Value.Success);
        Assert.Equal(podName, notFoundResult.Value.PodName);
    }

    [Fact]
    public async Task DeleteContainer_WithServiceException_ReturnsProblem()
    {
        // Arrange
        var podName = "kata-sandbox-12345";

        _mockContainerService
            .Setup(x => x.DeleteContainerAsync(podName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Kubernetes API error"));

        // Act
        var result = await InvokeDeleteContainer(podName);

        // Assert
        Assert.IsType<ProblemHttpResult>(result.Result);
        var problemResult = (ProblemHttpResult)result.Result;
        Assert.Equal(StatusCodes.Status500InternalServerError, problemResult.StatusCode);
        Assert.Equal("Failed to delete container", problemResult.ProblemDetails.Title);
        Assert.Equal("Kubernetes API error", problemResult.ProblemDetails.Detail);

        VerifyLogError(_mockLogger, $"Failed to delete container at API layer: {podName}");
    }

    #endregion

    #region Helper Methods

    private async Task<Results<Created<KataContainerInfo>, BadRequest<object>, ProblemHttpResult>> InvokeCreateContainer(
        CreateContainerRequest request)
    {
        var methodInfo = typeof(KataContainerEndpoints).GetMethod(
            "CreateContainer",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(methodInfo);

        var result = methodInfo.Invoke(null, new object[]
        {
            request,
            _mockContainerService.Object,
            _mockLogger.Object,
            CancellationToken.None
        });

        Assert.NotNull(result);
        var task = (Task<Results<Created<KataContainerInfo>, BadRequest<object>, ProblemHttpResult>>)result;
        return await task;
    }

    private async Task<Results<Ok<List<KataContainerInfo>>, ProblemHttpResult>> InvokeListContainers()
    {
        var methodInfo = typeof(KataContainerEndpoints).GetMethod(
            "ListContainers",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(methodInfo);

        var result = methodInfo.Invoke(null, new object[]
        {
            _mockContainerService.Object,
            _mockLogger.Object,
            CancellationToken.None
        });

        Assert.NotNull(result);
        var task = (Task<Results<Ok<List<KataContainerInfo>>, ProblemHttpResult>>)result;
        return await task;
    }

    private async Task<Results<Ok<KataContainerInfo>, NotFound<object>, ProblemHttpResult>> InvokeGetContainer(
        string podName)
    {
        var methodInfo = typeof(KataContainerEndpoints).GetMethod(
            "GetContainer",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(methodInfo);

        var result = methodInfo.Invoke(null, new object[]
        {
            podName,
            _mockContainerService.Object,
            _mockLogger.Object,
            CancellationToken.None
        });

        Assert.NotNull(result);
        var task = (Task<Results<Ok<KataContainerInfo>, NotFound<object>, ProblemHttpResult>>)result;
        return await task;
    }

    private async Task<Results<Ok<DeleteContainerResponse>, NotFound<DeleteContainerResponse>, ProblemHttpResult>> InvokeDeleteContainer(
        string podName)
    {
        var methodInfo = typeof(KataContainerEndpoints).GetMethod(
            "DeleteContainer",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(methodInfo);

        var result = methodInfo.Invoke(null, new object[]
        {
            podName,
            _mockContainerService.Object,
            _mockLogger.Object,
            CancellationToken.None
        });

        Assert.NotNull(result);
        var task = (Task<Results<Ok<DeleteContainerResponse>, NotFound<DeleteContainerResponse>, ProblemHttpResult>>)result;
        return await task;
    }

    private static void VerifyLogWarning(Mock<ILogger<Program>> mockLogger, string message)
    {
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(message)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private static void VerifyLogError(Mock<ILogger<Program>> mockLogger, string message)
    {
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(message)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    #endregion
}
