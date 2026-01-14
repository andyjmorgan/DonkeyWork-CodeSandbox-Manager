using System.Text.RegularExpressions;
using DonkeyWork.CodeSandbox_Manager.Configuration;
using DonkeyWork.CodeSandbox_Manager.Models;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace DonkeyWork.CodeSandbox_Manager.Services;

public class KataContainerService : IKataContainerService
{
    private readonly IKubernetes _client;
    private readonly KataContainerManager _config;
    private readonly ILogger<KataContainerService> _logger;

    public KataContainerService(
        IKubernetes client,
        IOptions<KataContainerManager> config,
        ILogger<KataContainerService> logger)
    {
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<KataContainerInfo> CreateContainerAsync(
        CreateContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating Kata container with image: {Image}", request.Image);

        if (string.IsNullOrWhiteSpace(request.Image))
        {
            throw new ArgumentException("Image name is required", nameof(request.Image));
        }

        if (!IsValidImageName(request.Image))
        {
            throw new ArgumentException($"Invalid image name format: {request.Image}", nameof(request.Image));
        }

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

            return MapPodToContainerInfo(createdPod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Kata container with image: {Image}", request.Image);
            throw;
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

    private V1Pod BuildPodSpec(string podName, CreateContainerRequest request)
    {
        var labels = new Dictionary<string, string>
        {
            ["app"] = "kata-manager",
            ["runtime"] = "kata",
            ["managed-by"] = "csharp-service",
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
            Image = request.Image,
            Resources = BuildResourceRequirements(request.Resources)
        };

        if (request.EnvironmentVariables != null && request.EnvironmentVariables.Count > 0)
        {
            container.Env = request.EnvironmentVariables
                .Select(kvp => new V1EnvVar { Name = kvp.Key, Value = kvp.Value })
                .ToList();
        }

        if (request.Command != null && request.Command.Count > 0)
        {
            container.Command = request.Command;
        }

        if (request.Args != null && request.Args.Count > 0)
        {
            container.Args = request.Args;
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

        return new KataContainerInfo
        {
            Name = pod.Metadata.Name,
            Phase = pod.Status?.Phase ?? "Unknown",
            IsReady = readyCondition?.Status == "True",
            CreatedAt = pod.Metadata.CreationTimestamp,
            NodeName = pod.Spec?.NodeName,
            PodIP = pod.Status?.PodIP,
            Labels = pod.Metadata.Labels != null ? new Dictionary<string, string>(pod.Metadata.Labels) : null,
            Image = containerImage
        };
    }

    private bool IsValidImageName(string image)
    {
        var regex = new Regex(@"^[a-z0-9\-\.]+(/[a-z0-9\-\.]+)*(:[a-zA-Z0-9\-\.]+)?$");
        return regex.IsMatch(image);
    }
}
