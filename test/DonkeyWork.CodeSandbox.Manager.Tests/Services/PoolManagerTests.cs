using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Services.Pool;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace DonkeyWork.CodeSandbox.Manager.Tests.Services;

public class PoolManagerTests
{
    private readonly Mock<IKubernetes> _mockKubernetesClient;
    private readonly Mock<ICoreV1Operations> _mockCoreV1;
    private readonly Mock<ILogger<PoolManager>> _mockLogger;
    private readonly Mock<IOptions<KataContainerManager>> _mockOptions;
    private readonly KataContainerManager _config;
    private readonly PoolManager _poolManager;

    public PoolManagerTests()
    {
        _mockKubernetesClient = new Mock<IKubernetes>();
        _mockCoreV1 = new Mock<ICoreV1Operations>();
        _mockLogger = new Mock<ILogger<PoolManager>>();
        _mockOptions = new Mock<IOptions<KataContainerManager>>();

        _config = new KataContainerManager
        {
            TargetNamespace = "sandbox-containers",
            RuntimeClassName = "kata-qemu",
            PodNamePrefix = "kata-sandbox",
            WarmPoolSize = 10,
            McpWarmPoolSize = 0,  // Disable MCP pool for these tests
            MaxTotalContainers = 50,
            IdleTimeoutMinutes = 5,
            MaxContainerLifetimeMinutes = 15,
            DefaultResourceRequests = new ResourceConfig { MemoryMi = 512, CpuMillicores = 250 },
            DefaultResourceLimits = new ResourceConfig { MemoryMi = 1024, CpuMillicores = 500 }
        };

        _mockOptions.Setup(o => o.Value).Returns(_config);
        _mockKubernetesClient.Setup(k => k.CoreV1).Returns(_mockCoreV1.Object);

        _poolManager = new PoolManager(
            _mockKubernetesClient.Object,
            _mockOptions.Object,
            _mockLogger.Object);
    }

    #region GetPoolStatisticsAsync Tests

    [Fact]
    public async Task GetPoolStatisticsAsync_ReturnsCorrectStatistics()
    {
        // Arrange - PoolManager queries include container-type=sandbox filter
        SetupPodListForLabel("pool-status=creating,container-type=sandbox", 2);
        SetupPodListForLabel("pool-status=warm,container-type=sandbox", 8);
        SetupPodListForLabel("pool-status=allocated,container-type=sandbox", 5);
        SetupPodListForLabel("pool-status=manual,container-type=sandbox", 3);

        // Act
        var result = await _poolManager.GetPoolStatisticsAsync();

        // Assert
        Assert.Equal(2, result.Creating);
        Assert.Equal(8, result.Warm);
        Assert.Equal(5, result.Allocated);
        Assert.Equal(3, result.Manual);
        Assert.Equal(18, result.Total);
        Assert.Equal(10, result.TargetSize);
        Assert.Equal(50, result.MaxTotalContainers);
        Assert.Equal(80.0, result.ReadyPercentage); // 8/10 * 100
        // Utilization = (allocated + manual) / total = (5+3)/18 * 100 = 44.4%
        Assert.Equal(44.4, result.UtilizationPercentage, 1);
    }

    [Fact]
    public async Task GetPoolStatisticsAsync_WithNoContainers_ReturnsZeros()
    {
        // Arrange - PoolManager queries include container-type=sandbox filter
        SetupPodListForLabel("pool-status=creating,container-type=sandbox", 0);
        SetupPodListForLabel("pool-status=warm,container-type=sandbox", 0);
        SetupPodListForLabel("pool-status=allocated,container-type=sandbox", 0);
        SetupPodListForLabel("pool-status=manual,container-type=sandbox", 0);

        // Act
        var result = await _poolManager.GetPoolStatisticsAsync();

        // Assert
        Assert.Equal(0, result.Creating);
        Assert.Equal(0, result.Warm);
        Assert.Equal(0, result.Allocated);
        Assert.Equal(0, result.Manual);
        Assert.Equal(0, result.Total);
        Assert.Equal(0.0, result.ReadyPercentage);
        Assert.Equal(0.0, result.UtilizationPercentage);
    }

    [Fact]
    public async Task GetPoolStatisticsAsync_IncludesMaxTotalContainers()
    {
        // Arrange - PoolManager queries include container-type=sandbox filter
        SetupPodListForLabel("pool-status=creating,container-type=sandbox", 0);
        SetupPodListForLabel("pool-status=warm,container-type=sandbox", 5);
        SetupPodListForLabel("pool-status=allocated,container-type=sandbox", 0);
        SetupPodListForLabel("pool-status=manual,container-type=sandbox", 0);

        // Act
        var result = await _poolManager.GetPoolStatisticsAsync();

        // Assert
        Assert.Equal(50, result.MaxTotalContainers);
    }

    #endregion

    #region GetTotalContainerCountAsync Tests

    [Fact]
    public async Task GetTotalContainerCountAsync_CountsOnlyKataContainers()
    {
        // Arrange
        var podList = new V1PodList
        {
            Items = new List<V1Pod>
            {
                CreateSamplePod("kata-sandbox-1", "kata-qemu"),
                CreateSamplePod("kata-sandbox-2", "kata-qemu"),
                CreateSamplePod("regular-pod", "runc") // Should not be counted
            }
        };

        SetupAllPodsListResponse(podList);

        // Act
        var result = await _poolManager.GetTotalContainerCountAsync();

        // Assert
        Assert.Equal(2, result);
    }

    #endregion

    #region BackfillPoolAsync Tests

    [Fact]
    public async Task BackfillPoolAsync_WhenPoolIsFull_DoesNotCreateNewPods()
    {
        // Arrange - BackfillPoolAsync queries include container-type filter
        SetupPodListForLabel("pool-status=creating,container-type=sandbox", 5);
        SetupPodListForLabel("pool-status=warm,container-type=sandbox", 5);
        SetupAllPodsListResponse(CreatePodList(10));

        // Act
        await _poolManager.BackfillPoolAsync();

        // Assert - no pods should be created since pool is full
        _mockCoreV1.Verify(
            x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BackfillPoolAsync_WhenAtMaxContainerLimit_DoesNotCreateNewPods()
    {
        // Arrange - pool has deficit but we're at max container limit
        _config.MaxTotalContainers = 10;
        SetupPodListForLabel("pool-status=creating,container-type=sandbox", 2);
        SetupPodListForLabel("pool-status=warm,container-type=sandbox", 3);
        SetupAllPodsListResponse(CreatePodList(10)); // Already at max

        // Act
        await _poolManager.BackfillPoolAsync();

        // Assert
        _mockCoreV1.Verify(
            x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        VerifyLogWarning(_mockLogger, "Max container limit reached");
    }

    [Fact]
    public async Task BackfillPoolAsync_LimitsCreationToAvailableCapacity()
    {
        // Arrange - need 5 to fill pool but only 2 capacity available
        _config.MaxTotalContainers = 10;
        _config.WarmPoolSize = 10;
        SetupPodListForLabel("pool-status=creating,container-type=sandbox", 2);
        SetupPodListForLabel("pool-status=warm,container-type=sandbox", 3); // Need 5 more for pool
        SetupAllPodsListResponse(CreatePodList(8)); // Only 2 capacity available

        var createdResponse = new HttpOperationResponse<V1Pod>
        {
            Body = CreateSamplePod("kata-sandbox-new", "kata-qemu")
        };

        _mockCoreV1
            .Setup(x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdResponse);

        // Act
        await _poolManager.BackfillPoolAsync();

        // Assert - should only create 2 (available capacity), not 5 (deficit)
        _mockCoreV1.Verify(
            x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        VerifyLogWarning(_mockLogger, "Limiting sandbox backfill");
    }

    [Fact]
    public async Task BackfillPoolAsync_WithDeficitAndCapacity_CreatesPods()
    {
        // Arrange - BackfillPoolAsync queries include container-type filter
        SetupPodListForLabel("pool-status=creating,container-type=sandbox", 0);
        SetupPodListForLabel("pool-status=warm,container-type=sandbox", 5); // Need 5 more
        SetupAllPodsListResponse(CreatePodList(5)); // Plenty of capacity

        var createdResponse = new HttpOperationResponse<V1Pod>
        {
            Body = CreateSamplePod("kata-sandbox-new", "kata-qemu")
        };

        _mockCoreV1
            .Setup(x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdResponse);

        // Act
        await _poolManager.BackfillPoolAsync();

        // Assert - should create 5 pods
        _mockCoreV1.Verify(
            x => x.CreateNamespacedPodWithHttpMessagesAsync(
                It.IsAny<V1Pod>(),
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    #endregion

    #region Helper Methods

    private void SetupPodListForLabel(string labelSelector, int count)
    {
        var podList = CreatePodList(count);
        var response = new HttpOperationResponse<V1PodList>
        {
            Body = podList
        };

        _mockCoreV1
            .Setup(x => x.ListNamespacedPodWithHttpMessagesAsync(
                _config.TargetNamespace,
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
                labelSelector,
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    private void SetupAllPodsListResponse(V1PodList podList)
    {
        var response = new HttpOperationResponse<V1PodList>
        {
            Body = podList
        };

        _mockCoreV1
            .Setup(x => x.ListNamespacedPodWithHttpMessagesAsync(
                _config.TargetNamespace,
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
                null,
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<int?>(), It.IsAny<bool?>(), It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    private V1PodList CreatePodList(int count)
    {
        var items = new List<V1Pod>();
        for (int i = 0; i < count; i++)
        {
            items.Add(CreateSamplePod($"kata-sandbox-{i}", "kata-qemu"));
        }
        return new V1PodList { Items = items };
    }

    private V1Pod CreateSamplePod(string name, string runtimeClassName)
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
                        Image = "test-image"
                    }
                }
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                PodIP = "10.244.0.5"
            }
        };
    }

    private static void VerifyLogWarning(Mock<ILogger<PoolManager>> mockLogger, string message)
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

    #endregion
}
