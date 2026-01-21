using DonkeyWork.CodeSandbox.Manager.Endpoints;
using DonkeyWork.CodeSandbox.Manager.Models;
using DonkeyWork.CodeSandbox.Manager.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DonkeyWork.CodeSandbox.Manager.Tests.Endpoints;

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
    public async Task CreateContainer_WithValidRequest_StreamsCreatedEvent()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = "nginx:latest",
            WaitForReady = false
        };

        var events = new List<ContainerCreationEvent>
        {
            new ContainerCreatedEvent
            {
                PodName = "kata-sandbox-12345678",
                Phase = "Pending"
            }
        };

        _mockContainerService
            .Setup(x => x.CreateContainerWithEventsAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(events));

        // Act
        var (sseEvents, headers) = await InvokeCreateContainer(request);

        // Assert
        Assert.Equal("text/event-stream", headers["Content-Type"]);
        Assert.Single(sseEvents);
        Assert.Equal("created", sseEvents[0].EventType);
        Assert.Equal("kata-sandbox-12345678", sseEvents[0].PodName);

        _mockContainerService.Verify(
            x => x.CreateContainerWithEventsAsync(request, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateContainer_WithWaitForReady_StreamsMultipleEvents()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = "nginx:latest",
            WaitForReady = true
        };

        var containerInfo = new KataContainerInfo
        {
            Name = "kata-sandbox-12345678",
            Phase = "Running",
            IsReady = true,
            Image = "nginx:latest"
        };

        var events = new List<ContainerCreationEvent>
        {
            new ContainerCreatedEvent { PodName = "kata-sandbox-12345678", Phase = "Pending" },
            new ContainerWaitingEvent { PodName = "kata-sandbox-12345678", AttemptNumber = 1, Phase = "Pending", Message = "Waiting..." },
            new ContainerWaitingEvent { PodName = "kata-sandbox-12345678", AttemptNumber = 2, Phase = "Running", Message = "Waiting..." },
            new ContainerReadyEvent { PodName = "kata-sandbox-12345678", ContainerInfo = containerInfo, ElapsedSeconds = 10.5 }
        };

        _mockContainerService
            .Setup(x => x.CreateContainerWithEventsAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(events));

        // Act
        var (sseEvents, _) = await InvokeCreateContainer(request);

        // Assert
        Assert.Equal(4, sseEvents.Count);
        Assert.Equal("created", sseEvents[0].EventType);
        Assert.Equal("waiting", sseEvents[1].EventType);
        Assert.Equal("waiting", sseEvents[2].EventType);
        Assert.Equal("ready", sseEvents[3].EventType);
    }

    [Fact]
    public async Task CreateContainer_WithArgumentException_StreamsFailedEvent()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = "Invalid@Image"
        };

        _mockContainerService
            .Setup(x => x.CreateContainerWithEventsAsync(request, It.IsAny<CancellationToken>()))
            .Throws(new ArgumentException("Invalid image name format"));

        // Act
        var (sseEvents, _) = await InvokeCreateContainer(request);

        // Assert
        Assert.Single(sseEvents);
        Assert.Equal("failed", sseEvents[0].EventType);
        Assert.Contains("Validation error", ((ContainerFailedEvent)sseEvents[0]).Reason);

        VerifyLogWarning(_mockLogger, "Invalid request to create container");
    }

    [Fact]
    public async Task CreateContainer_WithServiceException_StreamsFailedEvent()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Image = "nginx:latest"
        };

        _mockContainerService
            .Setup(x => x.CreateContainerWithEventsAsync(request, It.IsAny<CancellationToken>()))
            .Throws(new Exception("Kubernetes API error"));

        // Act
        var (sseEvents, _) = await InvokeCreateContainer(request);

        // Assert
        Assert.Single(sseEvents);
        Assert.Equal("failed", sseEvents[0].EventType);
        Assert.Contains("Unexpected error", ((ContainerFailedEvent)sseEvents[0]).Reason);

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

    private async Task<(List<ContainerCreationEvent> Events, Dictionary<string, string> Headers)> InvokeCreateContainer(
        CreateContainerRequest request)
    {
        var methodInfo = typeof(KataContainerEndpoints).GetMethod(
            "CreateContainer",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(methodInfo);

        // Create a mock HttpContext with a MemoryStream for the response body
        var responseStream = new MemoryStream();
        var mockResponse = new Mock<HttpResponse>();
        var headers = new Dictionary<string, string>();

        mockResponse.Setup(r => r.Body).Returns(responseStream);
        mockResponse.Setup(r => r.Headers).Returns(new HeaderDictionary());
        mockResponse.SetupSet(r => r.Headers[It.IsAny<string>()] = It.IsAny<Microsoft.Extensions.Primitives.StringValues>())
            .Callback<string, Microsoft.Extensions.Primitives.StringValues>((key, value) => headers[key] = value.ToString());

        var mockHttpContext = new Mock<HttpContext>();
        mockHttpContext.Setup(c => c.Response).Returns(mockResponse.Object);
        mockHttpContext.Setup(c => c.RequestAborted).Returns(CancellationToken.None);

        var result = methodInfo.Invoke(null, new object[]
        {
            request,
            _mockContainerService.Object,
            _mockLogger.Object,
            mockHttpContext.Object,
            CancellationToken.None
        });

        Assert.NotNull(result);
        var task = (Task)result;
        await task;

        // Parse SSE events from the response stream
        responseStream.Position = 0;
        var reader = new StreamReader(responseStream);
        var sseContent = await reader.ReadToEndAsync();

        var events = ParseSseEvents(sseContent);
        return (events, headers);
    }

    private static List<ContainerCreationEvent> ParseSseEvents(string sseContent)
    {
        var events = new List<ContainerCreationEvent>();
        var lines = sseContent.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("data: "))
            {
                var json = line.Substring(6);
                var jsonDoc = JsonDocument.Parse(json);
                var eventType = jsonDoc.RootElement.GetProperty("eventType").GetString();

                ContainerCreationEvent? evt = eventType switch
                {
                    "created" => JsonSerializer.Deserialize<ContainerCreatedEvent>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
                    "waiting" => JsonSerializer.Deserialize<ContainerWaitingEvent>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
                    "ready" => JsonSerializer.Deserialize<ContainerReadyEvent>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
                    "failed" => JsonSerializer.Deserialize<ContainerFailedEvent>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
                    _ => null
                };

                if (evt != null)
                {
                    events.Add(evt);
                }
            }
        }

        return events;
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
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
