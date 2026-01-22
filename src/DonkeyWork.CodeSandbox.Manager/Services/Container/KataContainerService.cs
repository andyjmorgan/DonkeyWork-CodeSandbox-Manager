using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DonkeyWork.CodeSandbox.Manager.Configuration;
using DonkeyWork.CodeSandbox.Manager.Models;
using DonkeyWork.CodeSandbox.Manager.Services.Pool;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox.Manager.Services.Container;

public class KataContainerService : IKataContainerService
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<KataContainerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public KataContainerService(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<KataContainerService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<KataContainerInfo> CreateContainerAsync(
        CreateContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating Kata container with image: {Image}, WaitForReady: {WaitForReady}",
            _config.DefaultImage, request.WaitForReady);

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var podName = $"{_config.PodNamePrefix}-{uniqueId}";

        var pod = BuildPodSpec(podName, request);

        try
        {
            var createdPod = await _client.CoreV1.CreateNamespacedPodAsync(
                pod,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully created Kata container: {PodName}", podName);

            // If WaitForReady is true, wait for the pod to be ready before returning
            if (request.WaitForReady)
            {
                _logger.LogInformation("Waiting for pod {PodName} to be ready (timeout: {TimeoutSeconds}s)",
                    podName, _config.PodReadyTimeoutSeconds);

                var isReady = await WaitForPodReadyAsync(podName, cancellationToken);

                if (!isReady)
                {
                    _logger.LogWarning("Pod {PodName} did not become ready within timeout", podName);
                }

                // Fetch the latest pod state after waiting
                var finalPod = await _client.CoreV1.ReadNamespacedPodAsync(
                    podName,
                    _config.TargetNamespace,
                    cancellationToken: cancellationToken);

                return MapPodToContainerInfo(finalPod);
            }

            return MapPodToContainerInfo(createdPod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Kata container with image: {Image}", _config.DefaultImage);
            throw;
        }
    }

    public async IAsyncEnumerable<ContainerCreationEvent> CreateContainerWithEventsAsync(
        CreateContainerRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ContainerCreationEvent>();

        // Start the creation process in the background
        var creationTask = CreateContainerInternalAsync(request, channel.Writer, cancellationToken);

        // Stream events to caller as they arrive
        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }

        // Ensure creation completes
        await creationTask;
    }

    private async Task CreateContainerInternalAsync(
        CreateContainerRequest request,
        System.Threading.Channels.ChannelWriter<ContainerCreationEvent> writer,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating Kata container with image: {Image} (streaming mode)", _config.DefaultImage);

        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var podName = $"{_config.PodNamePrefix}-{uniqueId}";
        var startTime = DateTime.UtcNow;

        var pod = BuildPodSpec(podName, request);

        V1Pod? createdPod = null;
        try
        {
            createdPod = await _client.CoreV1.CreateNamespacedPodAsync(
                pod,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully created Kata container: {PodName}", podName);

            // Emit created event
            writer.TryWrite(new ContainerCreatedEvent
            {
                PodName = podName,
                Phase = createdPod.Status?.Phase ?? "Pending"
            });

            // If WaitForReady is true, stream waiting events
            if (request.WaitForReady)
            {
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

                        // Check if pod is ready
                        if (IsPodReady(currentPod))
                        {
                            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                            _logger.LogInformation("Pod {PodName} is ready after {AttemptNumber} attempts", podName, attemptNumber);

                            writer.TryWrite(new ContainerReadyEvent
                            {
                                PodName = podName,
                                ContainerInfo = MapPodToContainerInfo(currentPod),
                                ElapsedSeconds = elapsed
                            });

                            return;
                        }

                        // Check if pod has failed
                        if (currentPod.Status?.Phase == "Failed")
                        {
                            _logger.LogWarning("Pod {PodName} failed during startup", podName);

                            writer.TryWrite(new ContainerFailedEvent
                            {
                                PodName = podName,
                                Reason = "Pod failed during startup",
                                ContainerInfo = MapPodToContainerInfo(currentPod)
                            });

                            return;
                        }

                        // Get detailed container status
                        var containerStatus = currentPod.Status?.ContainerStatuses?.FirstOrDefault();
                        string detailedMessage = $"Waiting for pod to be ready (attempt {attemptNumber})";

                        if (containerStatus?.State?.Waiting != null)
                        {
                            var waiting = containerStatus.State.Waiting;
                            var reason = waiting.Reason ?? "Unknown";
                            var message = waiting.Message ?? "";

                            // Provide user-friendly messages
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

                        // Emit waiting event with detailed status
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
                        _logger.LogWarning(ex, "Error checking pod {PodName} status on attempt {AttemptNumber}", podName, attemptNumber);

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

                // Timeout - fetch final state and emit failed event
                _logger.LogWarning("Timeout waiting for pod {PodName} to be ready after {TimeoutSeconds}s",
                    podName, _config.PodReadyTimeoutSeconds);

                try
                {
                    var finalPod = await _client.CoreV1.ReadNamespacedPodAsync(
                        podName,
                        _config.TargetNamespace,
                        cancellationToken: cancellationToken);

                    writer.TryWrite(new ContainerFailedEvent
                    {
                        PodName = podName,
                        Reason = $"Timeout after {_config.PodReadyTimeoutSeconds}s",
                        ContainerInfo = MapPodToContainerInfo(finalPod)
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch final pod state after timeout");
                    writer.TryWrite(new ContainerFailedEvent
                    {
                        PodName = podName,
                        Reason = $"Timeout after {_config.PodReadyTimeoutSeconds}s (unable to fetch final state)",
                        ContainerInfo = null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Kata container with image: {Image}", _config.DefaultImage);

            writer.TryWrite(new ContainerFailedEvent
            {
                PodName = podName,
                Reason = $"Creation failed: {ex.Message}",
                ContainerInfo = createdPod != null ? MapPodToContainerInfo(createdPod) : null
            });
        }
        finally
        {
            writer.Complete();
        }
    }

    public async Task<List<KataContainerInfo>> ListContainersAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing all Kata containers in namespace: {Namespace}", _config.TargetNamespace);

        try
        {
            var podList = await _client.CoreV1.ListNamespacedPodAsync(
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            var kataContainers = podList.Items
                .Where(p => p.Spec.RuntimeClassName == _config.RuntimeClassName)
                .Select(MapPodToContainerInfo)
                .ToList();

            _logger.LogInformation("Found {Count} Kata containers", kataContainers.Count);

            return kataContainers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Kata containers");
            throw;
        }
    }

    public async Task<KataContainerInfo?> GetContainerAsync(
        string podName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting Kata container: {PodName}", podName);

        try
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(
                podName,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            if (pod.Spec.RuntimeClassName != _config.RuntimeClassName)
            {
                _logger.LogWarning("Pod {PodName} is not a Kata container", podName);
                return null;
            }

            return MapPodToContainerInfo(pod);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Kata container not found: {PodName}", podName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Kata container: {PodName}", podName);
            throw;
        }
    }

    public async Task<DeleteContainerResponse> DeleteContainerAsync(
        string podName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting Kata container: {PodName}", podName);

        try
        {
            await _client.CoreV1.DeleteNamespacedPodAsync(
                podName,
                _config.TargetNamespace,
                body: new V1DeleteOptions { GracePeriodSeconds = 30 },
                cancellationToken: cancellationToken);

            _logger.LogInformation("Successfully deleted Kata container: {PodName}", podName);

            return new DeleteContainerResponse
            {
                Success = true,
                Message = $"Container {podName} deleted successfully",
                PodName = podName
            };
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Kata container not found for deletion: {PodName}", podName);
            return new DeleteContainerResponse
            {
                Success = false,
                Message = $"Container {podName} not found",
                PodName = podName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Kata container: {PodName}", podName);
            return new DeleteContainerResponse
            {
                Success = false,
                Message = $"Failed to delete container: {ex.Message}",
                PodName = podName
            };
        }
    }

    public async Task<DeleteAllContainersResponse> DeleteAllContainersAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting all Kata containers in namespace: {Namespace}", _config.TargetNamespace);

        var response = new DeleteAllContainersResponse();

        try
        {
            var containers = await ListContainersAsync(cancellationToken);

            foreach (var container in containers)
            {
                try
                {
                    await _client.CoreV1.DeleteNamespacedPodAsync(
                        container.Name,
                        _config.TargetNamespace,
                        body: new V1DeleteOptions { GracePeriodSeconds = 0 },
                        cancellationToken: cancellationToken);

                    response.DeletedPods.Add(container.Name);
                    response.DeletedCount++;
                    _logger.LogInformation("Deleted Kata container: {PodName}", container.Name);
                }
                catch (Exception ex)
                {
                    response.FailedPods.Add(container.Name);
                    response.FailedCount++;
                    _logger.LogWarning(ex, "Failed to delete Kata container: {PodName}", container.Name);
                }
            }

            _logger.LogInformation("Deleted {DeletedCount} containers, {FailedCount} failed",
                response.DeletedCount, response.FailedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all Kata containers");
            throw;
        }

        return response;
    }

    private V1Pod BuildPodSpec(string podName, CreateContainerRequest request)
    {
        var labels = new Dictionary<string, string>
        {
            ["app"] = "kata-manager",
            ["runtime"] = "kata",
            ["managed-by"] = "CodeSandbox-Manager",
            ["created-at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
        };

        if (request.Labels != null)
        {
            foreach (var kvp in request.Labels)
            {
                labels[kvp.Key] = kvp.Value;
            }
        }

        var container = new V1Container
        {
            Name = "workload",
            Image = _config.DefaultImage,
            ImagePullPolicy = "Always",
            Resources = BuildResourceRequirements(request.Resources),
            Stdin = true,  // Keep stdin open to prevent ConsoleLifetime from triggering shutdown
            Tty = true
        };

        if (request.EnvironmentVariables != null && request.EnvironmentVariables.Count > 0)
        {
            container.Env = request.EnvironmentVariables
                .Select(kvp => new V1EnvVar { Name = kvp.Key, Value = kvp.Value })
                .ToList();
        }

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = _config.TargetNamespace,
                Labels = labels
            },
            Spec = new V1PodSpec
            {
                RuntimeClassName = _config.RuntimeClassName,
                RestartPolicy = "Never",
                Containers = new List<V1Container> { container }
            }
        };

        return pod;
    }

    private V1ResourceRequirements BuildResourceRequirements(ResourceRequirements? resources)
    {
        var requests = new Dictionary<string, ResourceQuantity>();
        var limits = new Dictionary<string, ResourceQuantity>();

        if (resources?.Requests != null)
        {
            if (resources.Requests.MemoryMi.HasValue)
                requests["memory"] = new ResourceQuantity($"{resources.Requests.MemoryMi}Mi");
            if (resources.Requests.CpuMillicores.HasValue)
                requests["cpu"] = new ResourceQuantity($"{resources.Requests.CpuMillicores}m");
        }
        else
        {
            requests["memory"] = new ResourceQuantity($"{_config.DefaultResourceRequests.MemoryMi}Mi");
            requests["cpu"] = new ResourceQuantity($"{_config.DefaultResourceRequests.CpuMillicores}m");
        }

        if (resources?.Limits != null)
        {
            if (resources.Limits.MemoryMi.HasValue)
                limits["memory"] = new ResourceQuantity($"{resources.Limits.MemoryMi}Mi");
            if (resources.Limits.CpuMillicores.HasValue)
                limits["cpu"] = new ResourceQuantity($"{resources.Limits.CpuMillicores}m");
        }
        else
        {
            limits["memory"] = new ResourceQuantity($"{_config.DefaultResourceLimits.MemoryMi}Mi");
            limits["cpu"] = new ResourceQuantity($"{_config.DefaultResourceLimits.CpuMillicores}m");
        }

        return new V1ResourceRequirements
        {
            Requests = requests,
            Limits = limits
        };
    }

    private KataContainerInfo MapPodToContainerInfo(V1Pod pod)
    {
        var readyCondition = pod.Status?.Conditions?
            .FirstOrDefault(c => c.Type == "Ready");

        var containerImage = pod.Spec?.Containers?.FirstOrDefault()?.Image;

        // Read last activity from annotation (source of truth)
        var lastActivity = PoolManager.ParseTimestampAnnotation(
            pod.Metadata.Annotations,
            PoolManager.LastActivityAnnotation);

        return new KataContainerInfo
        {
            Name = pod.Metadata.Name,
            Phase = pod.Status?.Phase ?? "Unknown",
            IsReady = readyCondition?.Status == "True",
            CreatedAt = pod.Metadata.CreationTimestamp,
            NodeName = pod.Spec?.NodeName,
            PodIP = pod.Status?.PodIP,
            Labels = pod.Metadata.Labels != null ? new Dictionary<string, string>(pod.Metadata.Labels) : null,
            Image = containerImage,
            LastActivity = lastActivity
        };
    }

    private async Task<bool> WaitForPodReadyAsync(string podName, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_config.PodReadyTimeoutSeconds);
        var deadline = DateTime.UtcNow.Add(timeout);
        var pollInterval = TimeSpan.FromSeconds(2);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var pod = await _client.CoreV1.ReadNamespacedPodAsync(
                    podName,
                    _config.TargetNamespace,
                    cancellationToken: cancellationToken);

                // Check if pod is ready
                if (IsPodReady(pod))
                {
                    _logger.LogInformation("Pod {PodName} is ready", podName);
                    return true;
                }

                // Check if pod has failed
                if (pod.Status?.Phase == "Failed")
                {
                    _logger.LogWarning("Pod {PodName} failed during startup", podName);
                    return false;
                }

                _logger.LogDebug("Pod {PodName} not ready yet, phase: {Phase}", podName, pod.Status?.Phase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking pod {PodName} status", podName);
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        _logger.LogWarning("Timeout waiting for pod {PodName} to be ready after {TimeoutSeconds}s",
            podName, _config.PodReadyTimeoutSeconds);
        return false;
    }

    private bool IsPodReady(V1Pod pod)
    {
        if (pod.Status?.Phase != "Running")
            return false;

        var readyCondition = pod.Status.Conditions?
            .FirstOrDefault(c => c.Type == "Ready");

        return readyCondition?.Status == "True";
    }

    // Execution passthrough implementation
    public async Task ExecuteCommandAsync(
        string sandboxId,
        ExecutionRequest request,
        Stream responseStream,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Executing command in sandbox {SandboxId}: {Command}", sandboxId, request.Command);

        // Update last activity time (fire and forget - don't block command execution)
        _ = UpdateLastActivityAsync(sandboxId, cancellationToken);

        // Get pod IP
        var podIp = await GetPodIpAsync(sandboxId, cancellationToken);

        // Create HTTP request to CodeExecution API
        var httpClient = _httpClientFactory.CreateClient();
        var apiUrl = $"http://{podIp}:8666/api/execute";

        var jsonContent = JsonSerializer.Serialize(request);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to sandbox {SandboxId} CodeExecution API", sandboxId);
            throw;
        }

        // Stream the response directly through without parsing
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(responseStream, cancellationToken);

        _logger.LogInformation("Command execution stream completed for sandbox {SandboxId}", sandboxId);
    }

    public async Task<string> GetPodIpAsync(string sandboxId, CancellationToken cancellationToken = default)
    {
        try
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(
                sandboxId,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            if (string.IsNullOrWhiteSpace(pod.Status?.PodIP))
            {
                throw new InvalidOperationException($"Pod {sandboxId} does not have an IP address yet");
            }

            return pod.Status.PodIP;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Sandbox {sandboxId} not found", ex);
        }
    }

    public async Task UpdateLastActivityAsync(string sandboxId, CancellationToken cancellationToken = default)
    {
        try
        {
            var nowTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            // Use JSON patch to update only the annotation
            var patch = new V1Patch(
                $"[{{\"op\": \"replace\", \"path\": \"/metadata/annotations/{PoolManager.LastActivityAnnotation.Replace("/", "~1")}\", \"value\": \"{nowTimestamp}\"}}]",
                V1Patch.PatchType.JsonPatch);

            await _client.CoreV1.PatchNamespacedPodAsync(
                patch,
                sandboxId,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            _logger.LogDebug("Updated last activity annotation for sandbox {SandboxId}", sandboxId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update last activity for sandbox {SandboxId}", sandboxId);
        }
    }

    public async Task<DateTime?> GetLastActivityAsync(string sandboxId, CancellationToken cancellationToken = default)
    {
        try
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(
                sandboxId,
                _config.TargetNamespace,
                cancellationToken: cancellationToken);

            return PoolManager.ParseTimestampAnnotation(
                pod.Metadata.Annotations,
                PoolManager.LastActivityAnnotation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get last activity for sandbox {SandboxId}", sandboxId);
            return null;
        }
    }

    private async Task CleanupFailedPodAsync(string podName, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Cleaning up failed pod {PodName}", podName);
            await _client.CoreV1.DeleteNamespacedPodAsync(
                podName,
                _config.TargetNamespace,
                body: new V1DeleteOptions { GracePeriodSeconds = 0 },
                cancellationToken: cancellationToken);
            _logger.LogInformation("Successfully cleaned up failed pod {PodName}", podName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up failed pod {PodName}", podName);
        }
    }
}
