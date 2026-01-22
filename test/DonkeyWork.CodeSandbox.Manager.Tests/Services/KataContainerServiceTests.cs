using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Models;
using DonkeyWork.CodeSandbox.Manager.Services;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;

namespace DonkeyWork.CodeSandbox.Manager.Tests.Services;

public class KataContainerServiceTests
{
    private readonly Mock<IKubernetes> _mockKubernetesClient;
    private readonly Mock<ICoreV1Operations> _mockCoreV1;
    private readonly Mock<ILogger<KataContainerService>> _mockLogger;
    private readonly Mock<IOptions<KataContainerManager>> _mockOptions;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<IContainerRegistry> _mockRegistry;
    private readonly KataContainerManager _config;
    private readonly KataContainerService _service;

    public KataContainerServiceTests()
    {
        _mockKubernetesClient = new Mock<IKubernetes>();
        _mockCoreV1 = new Mock<ICoreV1Operations>();
        _mockLogger = new Mock<ILogger<KataContainerService>>();
        _mockOptions = new Mock<IOptions<KataContainerManager>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockRegistry = new Mock<IContainerRegistry>();

        _config = new KataContainerManager
        {
            TargetNamespace = "sandbox-containers",
            RuntimeClassName = "kata-qemu",
            PodNamePrefix = "kata-sandbox",
            DefaultResourceRequests = new ResourceConfig { MemoryMi = 512, CpuMillicores = 250 },
            DefaultResourceLimits = new ResourceConfig { MemoryMi = 1024, CpuMillicores = 500 }
        };

        _mockOptions.Setup(o => o.Value).Returns(_config);
        _mockKubernetesClient.Setup(k => k.CoreV1).Returns(_mockCoreV1.Object);

        _service = new KataContainerService(
            _mockKubernetesClient.Object,
            _mockOptions.Object,
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockRegistry.Object);
    }

    #region CreateContainerAsync Tests

    [Fact]
    public async Task CreateContainerAsync_WithValidRequest_ReturnsContainerInfo()
    {
        // Arrange
        var request = new CreateContainerRequest();

        var createdPod = CreateSamplePod("kata-sandbox-12345678", _config.DefaultImage);
        var response = new HttpOperationResponse<V1Pod>
        {
            Body = createdPod
        };

        _mockCoreV1
            .Setup(x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.CreateContainerAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("kata-sandbox-", result.Name);
        Assert.Equal(_config.DefaultImage, result.Image);

        VerifyLogInformation(_mockLogger, $"Creating Kata container with image: {_config.DefaultImage}");
        VerifyLogInformation(_mockLogger, "Successfully created Kata container:");
    }

    [Fact]
    public async Task CreateContainerAsync_WithLabels_CreatesContainerWithLabels()
    {
        // Arrange
        var request = new CreateContainerRequest
        {
            Labels = new Dictionary<string, string>
            {
                ["custom-label"] = "test-value",
                ["environment"] = "development"
            }
        };

        var createdPod = CreateSamplePod("kata-sandbox-87654321", _config.DefaultImage);
        var response = new HttpOperationResponse<V1Pod>
        {
            Body = createdPod
        };

        _mockCoreV1
            .Setup(x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.CreateContainerAsync(request);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateContainerAsync_AlwaysUsesDefaultImage()
    {
        // Arrange
        var request = new CreateContainerRequest();

        var createdPod = CreateSamplePod("kata-sandbox-12345678", _config.DefaultImage);
        var response = new HttpOperationResponse<V1Pod>
        {
            Body = createdPod
        };

        _mockCoreV1
            .Setup(x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.CreateContainerAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(_config.DefaultImage, result.Image);
    }

    [Fact]
    public async Task CreateContainerAsync_WhenKubernetesThrowsException_ThrowsAndLogsError()
    {
        // Arrange
        var request = new CreateContainerRequest();

        var kubernetesException = new Exception("Kubernetes API error");

        _mockCoreV1
            .Setup(x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(kubernetesException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(
            () => _service.CreateContainerAsync(request));

        Assert.Equal("Kubernetes API error", exception.Message);
        VerifyLogError(_mockLogger, $"Failed to create Kata container with image: {_config.DefaultImage}");
    }

    #endregion

    #region ListContainersAsync Tests

    [Fact]
    public async Task ListContainersAsync_WithKataContainers_ReturnsFilteredList()
    {
        // Arrange
        var podList = new V1PodList
        {
            Items = new List<V1Pod>
            {
                CreateSamplePod("kata-sandbox-1", "nginx:latest"),
                CreateSamplePod("kata-sandbox-2", "ubuntu:latest", runtimeClassName: "kata-qemu"),
                CreateSamplePod("regular-pod", "redis:latest", runtimeClassName: "runc")
            }
        };

        var response = new HttpOperationResponse<V1PodList>
        {
            Body = podList
        };

        _mockCoreV1
            .Setup(x => x.ListNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.ListContainersAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.All(result, c => Assert.NotNull(c.Name));
        Assert.Contains(result, c => c.Name == "kata-sandbox-1");
        Assert.Contains(result, c => c.Name == "kata-sandbox-2");
        Assert.DoesNotContain(result, c => c.Name == "regular-pod");

        VerifyLogInformation(_mockLogger, "Listing all Kata containers in namespace: sandbox-containers");
        VerifyLogInformation(_mockLogger, "Found 2 Kata containers");
    }

    [Fact]
    public async Task ListContainersAsync_WithNoKataContainers_ReturnsEmptyList()
    {
        // Arrange
        var podList = new V1PodList
        {
            Items = new List<V1Pod>
            {
                CreateSamplePod("regular-pod-1", "nginx:latest", runtimeClassName: "runc"),
                CreateSamplePod("regular-pod-2", "ubuntu:latest", runtimeClassName: "runc")
            }
        };

        var response = new HttpOperationResponse<V1PodList>
        {
            Body = podList
        };

        _mockCoreV1
            .Setup(x => x.ListNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.ListContainersAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
        VerifyLogInformation(_mockLogger, "Found 0 Kata containers");
    }

    [Fact]
    public async Task ListContainersAsync_WhenKubernetesThrowsException_ThrowsAndLogsError()
    {
        // Arrange
        var kubernetesException = new Exception("Kubernetes API error");

        _mockCoreV1
            .Setup(x => x.ListNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(kubernetesException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(
            () => _service.ListContainersAsync());

        Assert.Equal("Kubernetes API error", exception.Message);
        VerifyLogError(_mockLogger, "Failed to list Kata containers");
    }

    #endregion

    #region GetContainerAsync Tests

    [Fact]
    public async Task GetContainerAsync_WithExistingKataContainer_ReturnsContainerInfo()
    {
        // Arrange
        var podName = "kata-sandbox-12345";
        var pod = CreateSamplePod(podName, "nginx:latest");

        var response = new HttpOperationResponse<V1Pod>
        {
            Body = pod
        };

        _mockCoreV1
            .Setup(x => x.ReadNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.GetContainerAsync(podName);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(podName, result.Name);
        Assert.Equal("nginx:latest", result.Image);
        VerifyLogInformation(_mockLogger, $"Getting Kata container: {podName}");
    }

    [Fact]
    public async Task GetContainerAsync_WithNonKataContainer_ReturnsNull()
    {
        // Arrange
        var podName = "regular-pod";
        var pod = CreateSamplePod(podName, "nginx:latest", runtimeClassName: "runc");

        var response = new HttpOperationResponse<V1Pod>
        {
            Body = pod
        };

        _mockCoreV1
            .Setup(x => x.ReadNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.GetContainerAsync(podName);

        // Assert
        Assert.Null(result);
        VerifyLogWarning(_mockLogger, $"Pod {podName} is not a Kata container");
    }

    [Fact]
    public async Task GetContainerAsync_WithNonExistingPod_ReturnsNull()
    {
        // Arrange
        var podName = "non-existing-pod";
        var httpResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
        var wrapper = new k8s.Autorest.HttpResponseMessageWrapper(httpResponse, string.Empty);

        _mockCoreV1
            .Setup(x => x.ReadNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException { Response = wrapper });

        // Act
        var result = await _service.GetContainerAsync(podName);

        // Assert
        Assert.Null(result);
        VerifyLogWarning(_mockLogger, $"Kata container not found: {podName}");
    }

    [Fact]
    public async Task GetContainerAsync_WhenKubernetesThrowsOtherException_ThrowsAndLogsError()
    {
        // Arrange
        var podName = "kata-sandbox-12345";
        var kubernetesException = new Exception("Kubernetes API error");

        _mockCoreV1
            .Setup(x => x.ReadNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(kubernetesException);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(
            () => _service.GetContainerAsync(podName));

        Assert.Equal("Kubernetes API error", exception.Message);
        VerifyLogError(_mockLogger, $"Failed to get Kata container: {podName}");
    }

    #endregion

    #region DeleteContainerAsync Tests

    [Fact]
    public async Task DeleteContainerAsync_WithExistingPod_ReturnsSuccessResponse()
    {
        // Arrange
        var podName = "kata-sandbox-12345";

        var response = new HttpOperationResponse<V1Pod>
        {
            Body = new V1Pod()
        };

        _mockCoreV1
            .Setup(x => x.DeleteNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<V1DeleteOptions>(),
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _service.DeleteContainerAsync(podName);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal($"Container {podName} deleted successfully", result.Message);
        Assert.Equal(podName, result.PodName);
        VerifyLogInformation(_mockLogger, $"Deleting Kata container: {podName}");
        VerifyLogInformation(_mockLogger, $"Successfully deleted Kata container: {podName}");
    }

    [Fact]
    public async Task DeleteContainerAsync_WithNonExistingPod_ReturnsFailureResponse()
    {
        // Arrange
        var podName = "non-existing-pod";
        var httpResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
        var wrapper = new k8s.Autorest.HttpResponseMessageWrapper(httpResponse, string.Empty);

        _mockCoreV1
            .Setup(x => x.DeleteNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<V1DeleteOptions>(),
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException { Response = wrapper });

        // Act
        var result = await _service.DeleteContainerAsync(podName);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal($"Container {podName} not found", result.Message);
        Assert.Equal(podName, result.PodName);
        VerifyLogWarning(_mockLogger, $"Kata container not found for deletion: {podName}");
    }

    [Fact]
    public async Task DeleteContainerAsync_WhenKubernetesThrowsException_ReturnsFailureResponse()
    {
        // Arrange
        var podName = "kata-sandbox-12345";
        var kubernetesException = new Exception("Kubernetes API error");

        _mockCoreV1
            .Setup(x => x.DeleteNamespacedPodWithHttpMessagesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<V1DeleteOptions>(),
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(kubernetesException);

        // Act
        var result = await _service.DeleteContainerAsync(podName);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("Failed to delete container", result.Message);
        Assert.Equal(podName, result.PodName);
        VerifyLogError(_mockLogger, $"Failed to delete Kata container: {podName}");
    }

    #endregion

    #region Helper Methods

    private V1Pod CreateSamplePod(string name, string image, string runtimeClassName = "kata-qemu")
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = _config.TargetNamespace,
                CreationTimestamp = DateTime.UtcNow,
                Labels = new Dictionary<string, string>
                {
                    ["app"] = "kata-manager",
                    ["runtime"] = "kata"
                }
            },
            Spec = new V1PodSpec
            {
                RuntimeClassName = runtimeClassName,
                NodeName = "node-1",
                Containers = new List<V1Container>
                {
                    new V1Container
                    {
                        Name = "workload",
                        Image = image
                    }
                }
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                PodIP = "10.244.0.5",
                Conditions = new List<V1PodCondition>
                {
                    new V1PodCondition
                    {
                        Type = "Ready",
                        Status = "True"
                    }
                }
            }
        };
    }

    private static void VerifyLogInformation(Mock<ILogger<KataContainerService>> mockLogger, string message)
    {
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(message)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private static void VerifyLogWarning(Mock<ILogger<KataContainerService>> mockLogger, string message)
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

    private static void VerifyLogError(Mock<ILogger<KataContainerService>> mockLogger, string message)
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
