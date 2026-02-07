using System.Net;
using System.Text;
using System.Text.Json;
using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Models;
using DonkeyWork.CodeSandbox.Manager.Services.Pool;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services.Mcp;

public class McpContainerService : IMcpContainerService
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<McpContainerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public McpContainerService(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<McpContainerService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async IAsyncEnumerable<ContainerCreationEvent> CreateMcpServerAsync(
        CreateMcpServerRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ContainerCreationEvent>();

        var creationTask = CreateMcpServerInternalAsync(request, channel.Writer, cancellationToken);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }

        await creationTask;
    }

    private async Task CreateMcpServerInternalAsync(
        CreateMcpServerRequest request,
        System.Threading.Channels.ChannelWriter<ContainerCreationEvent> writer,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating MCP server container with image: {Image}", _config.McpServerImage);

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var podName = $"{_config.McpPodNamePrefix}-{uniqueId}";
        var startTime = DateTime.UtcNow;

        var pod = BuildMcpPodSpec(podName, request);

        try
        {
            var createdPod = await _client.CoreV1.CreateNamespacedPodAsync(
                pod,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully created MCP container: {PodName}", podName);

            writer.TryWrite(new ContainerCreatedEvent
            {
                PodName = podName,
                Phase = createdPod.Status?.Phase ?? "Pending"
            });

            // Wait for pod ready
            var timeout = TimeSpan.FromSeconds(_config.PodReadyTimeoutSeconds);
            var deadline = DateTime.UtcNow.Add(timeout);
            var pollInterval = TimeSpan.FromSeconds(2);
            var attemptNumber = 0;

            while (DateTime.UtcNow < deadline)
            {
                attemptNumber++;

                try
                {
                    var currentPod = await _client.CoreV1.ReadNamespacedPodAsync(
                        podName,
                        _config.TargetNamespace,
                        cancellationToken: cancellationToken);

                    if (IsPodReady(currentPod))
                    {
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;

                        writer.TryWrite(new ContainerReadyEvent
                        {
                            PodName = podName,
                            ContainerInfo = MapPodToContainerInfo(currentPod),
                            ElapsedSeconds = elapsed
                        });

                        // If command was provided, start the MCP process
                        if (!string.IsNullOrWhiteSpace(request.Command))
                        {
                            var commandDisplay = $"{request.Command} {string.Join(" ", request.Arguments)}";
                            writer.TryWrite(new McpServerStartingEvent
                            {
                                PodName = podName,
                                Message = $"Starting MCP process: {commandDisplay}"
                            });

                            try
                            {
                                var podIp = currentPod.Status?.PodIP
                                    ?? throw new InvalidOperationException("Pod has no IP");

                                // Consume SSE events from the MCP server start and forward to the creation stream
                                await foreach (var startEvt in StartMcpProcessOnPodSseAsync(podIp, request, cancellationToken))
                                {
                                    writer.TryWrite(new McpServerStartingEvent
                                    {
                                        PodName = podName,
                                        Message = $"[{startEvt.EventType}] {startEvt.Message}"
                                    });
                                }

                                var totalElapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                                writer.TryWrite(new McpServerStartedEvent
                                {
                                    PodName = podName,
                                    ServerInfo = MapPodToMcpServerInfo(currentPod, commandDisplay, McpProcessStatus.Ready),
                                    ElapsedSeconds = totalElapsed
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to start MCP process in {PodName}", podName);
                                writer.TryWrite(new McpServerStartFailedEvent
                                {
                                    PodName = podName,
                                    Reason = $"MCP process failed to start: {ex.Message}"
                                });
                            }
                        }

                        return;
                    }

                    if (currentPod.Status?.Phase == "Failed")
                    {
                        writer.TryWrite(new ContainerFailedEvent
                        {
                            PodName = podName,
                            Reason = "Pod failed during startup",
                            ContainerInfo = MapPodToContainerInfo(currentPod)
                        });
                        return;
                    }

                    var containerStatus = currentPod.Status?.ContainerStatuses?.FirstOrDefault();
                    string detailedMessage = $"Waiting for pod to be ready (attempt {attemptNumber})";

                    if (containerStatus?.State?.Waiting != null)
                    {
                        var waiting = containerStatus.State.Waiting;
                        var reason = waiting.Reason ?? "Unknown";
                        var message = waiting.Message ?? "";
                        detailedMessage = reason switch
                        {
                            "ContainerCreating" => "Creating container...",
                            "PodInitializing" => "Initializing pod...",
                            "ErrImagePull" => $"Error pulling image: {message}",
                            "ImagePullBackOff" => $"Failed to pull image, retrying: {message}",
                            _ => $"{reason}: {message}"
                        };
                    }
                    else if (containerStatus?.State?.Running != null)
                    {
                        detailedMessage = "Container running, waiting for readiness checks...";
                    }

                    writer.TryWrite(new ContainerWaitingEvent
                    {
                        PodName = podName,
                        AttemptNumber = attemptNumber,
                        Phase = currentPod.Status?.Phase ?? "Unknown",
                        Message = detailedMessage
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking MCP pod {PodName} status", podName);
                    writer.TryWrite(new ContainerWaitingEvent
                    {
                        PodName = podName,
                        AttemptNumber = attemptNumber,
                        Phase = "Unknown",
                        Message = $"Error checking status: {ex.Message}"
                    });
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            // Timeout
            writer.TryWrite(new ContainerFailedEvent
            {
                PodName = podName,
                Reason = $"Timeout after {_config.PodReadyTimeoutSeconds}s"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create MCP container");
            writer.TryWrite(new ContainerFailedEvent
            {
                PodName = podName,
                Reason = $"Creation failed: {ex.Message}"
            });
        }
        finally
        {
            writer.Complete();
        }
    }

    public async Task<List<McpServerInfo>> ListMcpServersAsync(CancellationToken cancellationToken = default)
    {
        var podList = await _client.CoreV1.ListNamespacedPodAsync(
            _config.TargetNamespace,
            labelSelector: $"{PoolManager.ContainerTypeLabel}={PoolManager.ContainerTypeMcp}",
            cancellationToken: cancellationToken);

        return podList.Items
            .Where(p => p.Spec.RuntimeClassName == _config.RuntimeClassName)
            .Select(p => MapPodToMcpServerInfo(p))
            .ToList();
    }

    public async Task<McpServerInfo?> GetMcpServerAsync(string podName, CancellationToken cancellationToken = default)
    {
        try
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(
                podName,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            return MapPodToMcpServerInfo(pod);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<DeleteContainerResponse> DeleteMcpServerAsync(string podName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting MCP server: {PodName}", podName);

        try
        {
            // Try to stop MCP process gracefully first
            try
            {
                await StopMcpProcessAsync(podName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not stop MCP process before deletion (may already be stopped)");
            }

            await _client.CoreV1.DeleteNamespacedPodAsync(
                podName,
                _config.TargetNamespace,
                body: new V1DeleteOptions { GracePeriodSeconds = 30 },
                cancellationToken: cancellationToken);

            return new DeleteContainerResponse
            {
                Success = true,
                Message = $"MCP server {podName} deleted successfully",
                PodName = podName
            };
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return new DeleteContainerResponse
            {
                Success = false,
                Message = $"MCP server {podName} not found",
                PodName = podName
            };
        }
        catch (Exception ex)
        {
            return new DeleteContainerResponse
            {
                Success = false,
                Message = $"Failed to delete MCP server: {ex.Message}",
                PodName = podName
            };
        }
    }

    public async Task<KataContainerInfo?> AllocateWarmMcpServerAsync(string userId, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 5;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var warmPods = await _client.CoreV1.ListNamespacedPodAsync(
                    _config.TargetNamespace,
                    labelSelector: $"pool-status=warm,{PoolManager.ContainerTypeLabel}={PoolManager.ContainerTypeMcp}",
                    cancellationToken: cancellationToken);

                if (!warmPods.Items.Any())
                {
                    _logger.LogWarning("No warm MCP servers available (attempt {Attempt}/{MaxRetries})",
                        attempt + 1, maxRetries);

                    if (attempt < maxRetries - 1)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                        continue;
                    }
                    return null;
                }

                var pod = warmPods.Items.First();
                var podName = pod.Metadata.Name;
                var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

                pod.Metadata.Labels["pool-status"] = "allocated";
                pod.Metadata.Labels["allocated-to"] = userId;
                pod.Metadata.Annotations ??= new Dictionary<string, string>();
                pod.Metadata.Annotations[PoolManager.AllocatedAtAnnotation] = nowTimestamp;
                pod.Metadata.Annotations[PoolManager.LastActivityAnnotation] = nowTimestamp;

                var updatedPod = await _client.CoreV1.ReplaceNamespacedPodAsync(
                    pod, podName, _config.TargetNamespace,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Allocated MCP server {PodName} to user {UserId}", podName, userId);

                return MapPodToContainerInfo(updatedPod);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogDebug("MCP allocation conflict on attempt {Attempt}, retrying", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }

        return null;
    }

    public async IAsyncEnumerable<McpStartProcessEvent> StartMcpProcessAsync(
        string podName,
        McpStartRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var commandDisplay = $"{request.Command} {string.Join(" ", request.Arguments)}";
        _logger.LogInformation("Starting MCP process in {PodName}: {Command}", podName, commandDisplay);

        var podIp = await GetPodIpAsync(podName, cancellationToken);
        var podUrl = $"http://{podIp}:8666";

        yield return new McpStartProcessEvent
        {
            EventType = "connecting",
            Message = $"Connecting to {podUrl}"
        };

        // Store launch command in annotation (for display purposes)
        try
        {
            var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var patch = new V1Patch(
                JsonSerializer.Serialize(new[]
                {
                    new { op = "add", path = $"/metadata/annotations/{PoolManager.McpLaunchCommandAnnotation.Replace("/", "~1")}", value = commandDisplay },
                    new { op = "replace", path = $"/metadata/annotations/{PoolManager.LastActivityAnnotation.Replace("/", "~1")}", value = nowTimestamp }
                }),
                V1Patch.PatchType.JsonPatch);

            await _client.CoreV1.PatchNamespacedPodAsync(
                patch, podName, _config.TargetNamespace,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store launch command annotation for {PodName}", podName);
        }

        await foreach (var evt in StartMcpProcessOnPodSseAsync(podIp, new CreateMcpServerRequest
        {
            Command = request.Command,
            Arguments = request.Arguments,
            PreExecScripts = request.PreExecScripts,
            TimeoutSeconds = request.TimeoutSeconds
        }, cancellationToken))
        {
            yield return evt;
        }
    }

    public async Task<string> ProxyMcpRequestAsync(string podName, string jsonRpcBody, CancellationToken cancellationToken = default)
    {
        var podIp = await GetPodIpAsync(podName, cancellationToken);

        // Update last activity
        _ = UpdateLastActivityAsync(podName, cancellationToken);

        var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.PostAsync(
            $"http://{podIp}:8666/mcp",
            new StringContent(jsonRpcBody, Encoding.UTF8, "application/json"),
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            // Notification - no response body
            return "{}";
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<McpStatusResponse> GetMcpStatusAsync(string podName, CancellationToken cancellationToken = default)
    {
        var podIp = await GetPodIpAsync(podName, cancellationToken);
        var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.GetAsync(
            $"http://{podIp}:8666/api/mcp/status",
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<McpStatusResponse>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }) ?? new McpStatusResponse { State = "Unknown" };
    }

    public async Task StopMcpProcessAsync(string podName, CancellationToken cancellationToken = default)
    {
        var podIp = await GetPodIpAsync(podName, cancellationToken);
        var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.DeleteAsync(
            $"http://{podIp}:8666/api/mcp",
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<PoolStatistics> GetMcpPoolStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allMcpPods = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                labelSelector: $"{PoolManager.ContainerTypeLabel}={PoolManager.ContainerTypeMcp}",
                cancellationToken: cancellationToken);

            var pods = allMcpPods.Items
                .Where(p => p.Spec.RuntimeClassName == _config.RuntimeClassName)
                .ToList();

            var creating = pods.Count(p => p.Metadata.Labels?.TryGetValue("pool-status", out var s1) == true && s1 == "creating");
            var warm = pods.Count(p => p.Metadata.Labels?.TryGetValue("pool-status", out var s2) == true && s2 == "warm");
            var allocated = pods.Count(p => p.Metadata.Labels?.TryGetValue("pool-status", out var s3) == true && s3 == "allocated");
            var manual = pods.Count(p => p.Metadata.Labels?.TryGetValue("pool-status", out var s4) == true && s4 == "manual");
            var total = creating + warm + allocated + manual;

            var readyPercentage = _config.McpWarmPoolSize > 0
                ? (warm / (double)_config.McpWarmPoolSize) * 100.0
                : 0.0;

            var activeContainers = allocated + manual;
            var utilizationPercentage = total > 0
                ? (activeContainers / (double)total) * 100.0
                : 0.0;

            return new PoolStatistics
            {
                Creating = creating,
                Warm = warm,
                Allocated = allocated,
                Manual = manual,
                Total = total,
                TargetSize = _config.McpWarmPoolSize,
                MaxTotalContainers = _config.MaxTotalContainers,
                ReadyPercentage = Math.Round(readyPercentage, 1),
                UtilizationPercentage = Math.Round(utilizationPercentage, 1)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get MCP pool statistics");
            return new PoolStatistics
            {
                TargetSize = _config.McpWarmPoolSize,
                MaxTotalContainers = _config.MaxTotalContainers
            };
        }
    }

    // --- Private helpers ---

    private V1Pod BuildMcpPodSpec(string podName, CreateMcpServerRequest request)
    {
        var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var labels = new Dictionary<string, string>
        {
            ["app"] = "kata-manager",
            ["runtime"] = "kata",
            ["managed-by"] = "CodeSandbox-Manager",
            [PoolManager.ContainerTypeLabel] = PoolManager.ContainerTypeMcp,
            ["pool-status"] = PoolManager.PoolStatusManual
        };

        if (request.Labels != null)
        {
            foreach (var kvp in request.Labels)
                labels[kvp.Key] = kvp.Value;
        }

        var annotations = new Dictionary<string, string>
        {
            [PoolManager.CreatedAtAnnotation] = nowTimestamp,
            [PoolManager.AllocatedAtAnnotation] = nowTimestamp,
            [PoolManager.LastActivityAnnotation] = nowTimestamp
        };

        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            var commandDisplay = $"{request.Command} {string.Join(" ", request.Arguments)}";
            annotations[PoolManager.McpLaunchCommandAnnotation] = commandDisplay;
        }

        var resourceReqs = _config.McpResourceRequests ?? _config.DefaultResourceRequests;
        var resourceLims = _config.McpResourceLimits ?? _config.DefaultResourceLimits;

        var container = new V1Container
        {
            Name = "workload",
            Image = _config.McpServerImage,
            ImagePullPolicy = "Always",
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["memory"] = new ResourceQuantity($"{resourceReqs.MemoryMi}Mi"),
                    ["cpu"] = new ResourceQuantity($"{resourceReqs.CpuMillicores}m")
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["memory"] = new ResourceQuantity($"{resourceLims.MemoryMi}Mi"),
                    ["cpu"] = new ResourceQuantity($"{resourceLims.CpuMillicores}m")
                }
            },
            Stdin = true,
            Tty = true
        };

        if (request.EnvironmentVariables != null && request.EnvironmentVariables.Count > 0)
        {
            container.Env = request.EnvironmentVariables
                .Select(kvp => new V1EnvVar { Name = kvp.Key, Value = kvp.Value })
                .ToList();
        }

        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = _config.TargetNamespace,
                Labels = labels,
                Annotations = annotations
            },
            Spec = new V1PodSpec
            {
                RuntimeClassName = _config.RuntimeClassName,
                RestartPolicy = "Never",
                Containers = new List<V1Container> { container }
            }
        };
    }

    private async IAsyncEnumerable<McpStartProcessEvent> StartMcpProcessOnPodSseAsync(
        string podIp,
        CreateMcpServerRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        var startPayload = new
        {
            preExecScripts = request.PreExecScripts,
            command = request.Command,
            arguments = request.Arguments,
            timeoutSeconds = request.TimeoutSeconds
        };

        var json = JsonSerializer.Serialize(startPayload);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"http://{podIp}:8666/api/mcp/start")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"MCP start failed ({response.StatusCode}): {error}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data: "))
                continue;

            var eventJson = line["data: ".Length..];
            McpStartProcessEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<McpStartProcessEvent>(eventJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize MCP start SSE event: {Json}", eventJson);
                continue;
            }

            if (evt is not null)
                yield return evt;
        }
    }

    private async Task<string> GetPodIpAsync(string podName, CancellationToken cancellationToken)
    {
        var pod = await _client.CoreV1.ReadNamespacedPodAsync(
            podName, _config.TargetNamespace,
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(pod.Status?.PodIP))
            throw new InvalidOperationException($"Pod {podName} does not have an IP address");

        return pod.Status.PodIP;
    }

    private async Task UpdateLastActivityAsync(string podName, CancellationToken cancellationToken)
    {
        try
        {
            var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var patch = new V1Patch(
                $"[{{\"op\": \"replace\", \"path\": \"/metadata/annotations/{PoolManager.LastActivityAnnotation.Replace("/", "~1")}\", \"value\": \"{nowTimestamp}\"}}]",
                V1Patch.PatchType.JsonPatch);

            await _client.CoreV1.PatchNamespacedPodAsync(
                patch, podName, _config.TargetNamespace,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update last activity for MCP server {PodName}", podName);
        }
    }

    private bool IsPodReady(V1Pod pod)
    {
        if (pod.Status?.Phase != "Running") return false;
        var readyCondition = pod.Status.Conditions?.FirstOrDefault(c => c.Type == "Ready");
        return readyCondition?.Status == "True";
    }

    private KataContainerInfo MapPodToContainerInfo(V1Pod pod)
    {
        var readyCondition = pod.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready");
        var lastActivity = PoolManager.ParseTimestampAnnotation(
            pod.Metadata.Annotations, PoolManager.LastActivityAnnotation);

        return new KataContainerInfo
        {
            Name = pod.Metadata.Name,
            Phase = pod.Status?.Phase ?? "Unknown",
            IsReady = readyCondition?.Status == "True",
            CreatedAt = pod.Metadata.CreationTimestamp,
            NodeName = pod.Spec?.NodeName,
            PodIP = pod.Status?.PodIP,
            Labels = pod.Metadata.Labels != null ? new Dictionary<string, string>(pod.Metadata.Labels) : null,
            Image = pod.Spec?.Containers?.FirstOrDefault()?.Image,
            LastActivity = lastActivity
        };
    }

    private McpServerInfo MapPodToMcpServerInfo(V1Pod pod, string? launchCommand = null, McpProcessStatus? mcpStatus = null)
    {
        var readyCondition = pod.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready");
        var lastActivity = PoolManager.ParseTimestampAnnotation(
            pod.Metadata.Annotations, PoolManager.LastActivityAnnotation);

        var storedCommand = launchCommand;
        if (storedCommand == null)
        {
            pod.Metadata.Annotations?.TryGetValue(PoolManager.McpLaunchCommandAnnotation, out storedCommand);
        }

        return new McpServerInfo
        {
            Name = pod.Metadata.Name,
            Phase = pod.Status?.Phase ?? "Unknown",
            IsReady = readyCondition?.Status == "True",
            CreatedAt = pod.Metadata.CreationTimestamp,
            NodeName = pod.Spec?.NodeName,
            PodIP = pod.Status?.PodIP,
            Labels = pod.Metadata.Labels != null ? new Dictionary<string, string>(pod.Metadata.Labels) : null,
            Image = pod.Spec?.Containers?.FirstOrDefault()?.Image,
            LastActivity = lastActivity,
            LaunchCommand = storedCommand,
            McpStatus = mcpStatus ?? McpProcessStatus.Unknown
        };
    }
}
