# C# Service Implementation Guide - Kata Container Management

## Overview

This document provides complete implementation details for a C# service that dynamically creates and manages Kata containers in the k3s cluster. The service will run as a pod inside the cluster and manage isolated workloads in the `sandbox-containers` namespace.

**Last Updated:** January 12, 2026
**Cluster Version:** k3s v1.33.5+k3s1
**Kata Version:** 3.5.0
**Target Namespace:** `sandbox-containers`

---

## Architecture

### Service Deployment Model
```
┌─────────────────────────────────────────┐
│  K3s Cluster                            │
│                                         │
│  ┌───────────────────────────────────┐ │
│  │  C# Service Pod                   │ │
│  │  - Namespace: (your choice)       │ │
│  │  - ServiceAccount: kata-manager   │ │
│  │  - Reads: appsettings.json        │ │
│  │  - Creates/Deletes pods in        │ │
│  │    sandbox-containers namespace   │ │
│  └───────────────────────────────────┘ │
│                                         │
│  ┌───────────────────────────────────┐ │
│  │  sandbox-containers namespace     │ │
│  │                                   │ │
│  │  ┌─────────┐ ┌─────────┐        │ │
│  │  │ Kata Pod│ │ Kata Pod│ ...    │ │
│  │  │ (VM)    │ │ (VM)    │        │ │
│  │  └─────────┘ └─────────┘        │ │
│  └───────────────────────────────────┘ │
└─────────────────────────────────────────┘
```

---

## Cluster Connection Details

### API Server
- **Endpoint (In-Cluster):** `https://kubernetes.default.svc`
- **Endpoint (External):** `https://192.168.69.27:6443`
- **API Version:** `v1.33.5+k3s1`

### Authentication (In-Cluster)

When your C# service runs as a pod in k3s, authentication is automatic via ServiceAccount:

**Auto-Mounted Files:**
- **Token:** `/var/run/secrets/kubernetes.io/serviceaccount/token`
- **CA Certificate:** `/var/run/secrets/kubernetes.io/serviceaccount/ca.crt`
- **Namespace:** `/var/run/secrets/kubernetes.io/serviceaccount/namespace`

**Environment Variables (Auto-Set):**
- `KUBERNETES_SERVICE_HOST` = `10.43.0.1` (or similar)
- `KUBERNETES_SERVICE_PORT` = `443`

### C# Client Configuration

Using the official Kubernetes C# client:

```csharp
// NuGet Package: KubernetesClient
using k8s;

// Automatic in-cluster configuration
var config = KubernetesClientConfiguration.InClusterConfig();
var client = new Kubernetes(config);
```

---

## Namespace Configuration

### Target Namespace
**Name:** `sandbox-containers`

**Purpose:**
- All dynamically created Kata containers run here
- Simplifies cataloging and management
- Clear resource isolation and tracking

### Configuration (appsettings.json)

```json
{
  "Kubernetes": {
    "TargetNamespace": "sandbox-containers",
    "RuntimeClassName": "kata-qemu",
    "DefaultResourceRequests": {
      "MemoryMi": 128,
      "CpuMillicores": 250
    },
    "DefaultResourceLimits": {
      "MemoryMi": 512,
      "CpuMillicores": 1000
    },
    "PodNamePrefix": "kata-sandbox",
    "CleanupCompletedPods": true
  }
}
```

**Note:** The namespace must be created before deploying your service. See "Prerequisites" section below.

---

## RBAC Requirements

Your C# service needs a dedicated ServiceAccount with specific permissions.

### ServiceAccount
- **Name:** `kata-manager` (or your choice)
- **Namespace:** Where your C# service runs (can be different from `sandbox-containers`)

### Required Permissions

**In the `sandbox-containers` namespace only:**

| Resource | Verbs | Purpose |
|----------|-------|---------|
| `pods` | `create` | Create new Kata containers |
| `pods` | `get` | Retrieve specific pod details |
| `pods` | `list` | Catalog all active containers |
| `pods` | `watch` | Real-time status updates |
| `pods` | `delete` | Destroy containers on demand |
| `pods/log` | `get` | (Optional) Fetch container logs |
| `pods/status` | `get` | (Optional) Check pod status |

### RBAC Resources Needed

1. **ServiceAccount** - Identity for your C# service
2. **Role** - Defines permissions in `sandbox-containers` namespace
3. **RoleBinding** - Links ServiceAccount to Role

**Important:** Use Role/RoleBinding (namespace-scoped), NOT ClusterRole/ClusterRoleBinding. This limits your service to only manage pods in `sandbox-containers`.

---

## Kata Container Specifications

### RuntimeClass Requirement

**Critical:** Every pod MUST include `runtimeClassName: kata-qemu` to run in a Kata VM.

**Without this field = standard container (runc)**
**With this field = Kata VM-isolated container**

### RuntimeClass Details
- **Name:** `kata-qemu`
- **Handler:** `kata-qemu`
- **Hypervisor:** QEMU/KVM
- **API Version:** `node.k8s.io/v1`

### Automatic Resource Overhead

Kubernetes automatically adds overhead to every Kata pod:
- **Memory:** +160Mi
- **CPU:** +250m

**Example:**
```
Your Request:    memory: 128Mi, cpu: 250m
Actual Schedule: memory: 288Mi, cpu: 500m (overhead added automatically)
```

### Startup Time
- **Standard containers:** 2-5 seconds
- **Kata containers:** 12-25 seconds (includes VM boot time)

**Implementation Note:** Add longer timeout/polling when waiting for Kata pods to reach Running state.

---

## Pod Creation

### Minimum Pod Specification

```yaml
apiVersion: v1
kind: Pod
metadata:
  name: kata-sandbox-{unique-id}
  namespace: sandbox-containers
  labels:
    app: your-service-name
    runtime: kata
    managed-by: csharp-service
    created-at: "2026-01-12T10:30:00Z"
spec:
  runtimeClassName: kata-qemu  # REQUIRED for Kata
  restartPolicy: Never  # or Always, OnFailure depending on use case
  containers:
  - name: workload
    image: your-image:tag
    resources:
      requests:
        memory: "128Mi"
        cpu: "250m"
      limits:
        memory: "512Mi"
        cpu: "1000m"
```

### C# Implementation Example

```csharp
using k8s;
using k8s.Models;

public async Task<V1Pod> CreateKataContainerAsync(
    string imageWithTag,
    Dictionary<string, string> labels = null,
    Dictionary<string, string> env = null)
{
    var uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
    var podName = $"kata-sandbox-{uniqueId}";

    var pod = new V1Pod
    {
        Metadata = new V1ObjectMeta
        {
            Name = podName,
            NamespaceProperty = "sandbox-containers",
            Labels = new Dictionary<string, string>
            {
                ["app"] = "your-service",
                ["runtime"] = "kata",
                ["managed-by"] = "csharp-service",
                ["created-at"] = DateTime.UtcNow.ToString("o")
            }
        },
        Spec = new V1PodSpec
        {
            RuntimeClassName = "kata-qemu",  // CRITICAL
            RestartPolicy = "Never",
            Containers = new List<V1Container>
            {
                new V1Container
                {
                    Name = "workload",
                    Image = imageWithTag,
                    Resources = new V1ResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity>
                        {
                            ["memory"] = new ResourceQuantity("128Mi"),
                            ["cpu"] = new ResourceQuantity("250m")
                        },
                        Limits = new Dictionary<string, ResourceQuantity>
                        {
                            ["memory"] = new ResourceQuantity("512Mi"),
                            ["cpu"] = new ResourceQuantity("1000m")
                        }
                    }
                }
            }
        }
    };

    // Merge additional labels if provided
    if (labels != null)
    {
        foreach (var kvp in labels)
        {
            pod.Metadata.Labels[kvp.Key] = kvp.Value;
        }
    }

    // Add environment variables if provided
    if (env != null)
    {
        pod.Spec.Containers[0].Env = env.Select(kvp =>
            new V1EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList();
    }

    return await _client.CreateNamespacedPodAsync(pod, "sandbox-containers");
}
```

### Important Pod Naming Rules
- **Max length:** 253 characters
- **Allowed characters:** lowercase alphanumeric, `-` (hyphen)
- **Must start/end with:** alphanumeric character
- **Must be unique** within namespace

**Recommended pattern:** `{prefix}-{timestamp-or-guid}`

---

## Pod Lifecycle Management

### Pod Phases

| Phase | Meaning | Action |
|-------|---------|--------|
| `Pending` | Pod accepted, not yet running | Wait (scheduling, image pull) |
| `Running` | At least one container running | Check Ready condition |
| `Succeeded` | All containers completed successfully | Can delete |
| `Failed` | Containers terminated with errors | Can delete, check logs |
| `Unknown` | Communication error with node | Investigate |

### Checking Pod Ready Status

A pod in `Running` phase isn't necessarily ready to serve traffic.

```csharp
public bool IsPodReady(V1Pod pod)
{
    if (pod.Status?.Phase != "Running")
        return false;

    var readyCondition = pod.Status.Conditions?
        .FirstOrDefault(c => c.Type == "Ready");

    return readyCondition?.Status == "True";
}
```

### Waiting for Pod Ready

```csharp
public async Task<bool> WaitForPodReadyAsync(
    string podName,
    TimeSpan timeout,
    CancellationToken cancellationToken = default)
{
    var deadline = DateTime.UtcNow + timeout;

    while (DateTime.UtcNow < deadline)
    {
        var pod = await _client.ReadNamespacedPodAsync(
            podName,
            "sandbox-containers",
            cancellationToken: cancellationToken);

        if (IsPodReady(pod))
            return true;

        if (pod.Status?.Phase == "Failed")
            return false;

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    return false;
}
```

**Recommended Timeout for Kata Pods:** 60-90 seconds (includes VM boot time)

---

## Cataloging Active Kata Containers

### List All Kata Pods

```csharp
public async Task<List<V1Pod>> ListAllKataContainersAsync()
{
    var podList = await _client.ListNamespacedPodAsync(
        namespaceParameter: "sandbox-containers");

    // Filter for Kata runtime
    return podList.Items
        .Where(p => p.Spec.RuntimeClassName == "kata-qemu")
        .ToList();
}
```

### List with Label Selector

More efficient if you consistently label your pods:

```csharp
public async Task<List<V1Pod>> ListKataContainersByLabelAsync(
    string labelSelector)
{
    var podList = await _client.ListNamespacedPodAsync(
        namespaceParameter: "sandbox-containers",
        labelSelector: labelSelector);  // e.g., "app=your-service,runtime=kata"

    return podList.Items.ToList();
}
```

### Get Running Pods Only

```csharp
public async Task<List<V1Pod>> ListRunningKataContainersAsync()
{
    var allPods = await ListAllKataContainersAsync();

    return allPods
        .Where(p => p.Status?.Phase == "Running")
        .ToList();
}
```

### Catalog Data Structure

```csharp
public class KataContainerInfo
{
    public string Name { get; set; }
    public string Phase { get; set; }
    public bool IsReady { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string NodeName { get; set; }
    public string PodIP { get; set; }
    public Dictionary<string, string> Labels { get; set; }
    public ResourceUsage Resources { get; set; }
}

public async Task<List<KataContainerInfo>> GetKataContainerCatalogAsync()
{
    var pods = await ListAllKataContainersAsync();

    return pods.Select(p => new KataContainerInfo
    {
        Name = p.Metadata.Name,
        Phase = p.Status?.Phase,
        IsReady = IsPodReady(p),
        CreatedAt = p.Metadata.CreationTimestamp,
        NodeName = p.Spec.NodeName,
        PodIP = p.Status?.PodIP,
        Labels = p.Metadata.Labels,
        Resources = ExtractResourceUsage(p)
    }).ToList();
}
```

---

## Deleting Kata Containers

### Simple Delete

```csharp
public async Task DeleteKataContainerAsync(string podName)
{
    await _client.DeleteNamespacedPodAsync(
        name: podName,
        namespaceParameter: "sandbox-containers");
}
```

### Delete with Grace Period

```csharp
public async Task DeleteKataContainerAsync(
    string podName,
    int gracePeriodSeconds = 30)
{
    await _client.DeleteNamespacedPodAsync(
        name: podName,
        namespaceParameter: "sandbox-containers",
        body: new V1DeleteOptions
        {
            GracePeriodSeconds = gracePeriodSeconds
        });
}
```

### Force Delete (Immediate)

```csharp
public async Task ForceDeleteKataContainerAsync(string podName)
{
    await _client.DeleteNamespacedPodAsync(
        name: podName,
        namespaceParameter: "sandbox-containers",
        body: new V1DeleteOptions
        {
            GracePeriodSeconds = 0
        });
}
```

### Bulk Delete by Label

```csharp
public async Task DeleteKataContainersByLabelAsync(string labelSelector)
{
    var pods = await _client.ListNamespacedPodAsync(
        namespaceParameter: "sandbox-containers",
        labelSelector: labelSelector);

    var deleteTasks = pods.Items.Select(p =>
        DeleteKataContainerAsync(p.Metadata.Name));

    await Task.WhenAll(deleteTasks);
}
```

### Cleanup Completed Pods

```csharp
public async Task CleanupCompletedPodsAsync()
{
    var pods = await ListAllKataContainersAsync();

    var completedPods = pods.Where(p =>
        p.Status?.Phase == "Succeeded" ||
        p.Status?.Phase == "Failed");

    foreach (var pod in completedPods)
    {
        await DeleteKataContainerAsync(pod.Metadata.Name);
    }
}
```

**Note:** When a Kata pod is deleted, the QEMU VM process on the node is automatically terminated and cleaned up.

---

## Watching for Real-Time Updates

### Watch Pod Events

```csharp
public async Task WatchKataContainersAsync(CancellationToken cancellationToken)
{
    var watcher = _client.ListNamespacedPodWithHttpMessagesAsync(
        namespaceParameter: "sandbox-containers",
        watch: true,
        cancellationToken: cancellationToken);

    await foreach (var (type, item) in watcher.WatchAsync<V1Pod, V1PodList>(
        onError: ex => Console.WriteLine($"Watch error: {ex.Message}"),
        cancellationToken: cancellationToken))
    {
        // Filter for Kata pods only
        if (item.Spec.RuntimeClassName != "kata-qemu")
            continue;

        switch (type)
        {
            case WatchEventType.Added:
                Console.WriteLine($"Kata pod added: {item.Metadata.Name}");
                break;
            case WatchEventType.Modified:
                Console.WriteLine($"Kata pod modified: {item.Metadata.Name} - Phase: {item.Status?.Phase}");
                break;
            case WatchEventType.Deleted:
                Console.WriteLine($"Kata pod deleted: {item.Metadata.Name}");
                break;
        }
    }
}
```

---

## Error Handling

### Common API Errors

| HTTP Status | Exception | Meaning | Action |
|-------------|-----------|---------|--------|
| 409 | `HttpOperationException` | Pod name already exists | Generate new unique name |
| 403 | `HttpOperationException` | Forbidden (RBAC) | Check ServiceAccount permissions |
| 404 | `HttpOperationException` | Pod not found | Already deleted or never existed |
| 422 | `HttpOperationException` | Invalid pod spec | Validate spec, check required fields |
| 500 | `HttpOperationException` | API server error | Retry with backoff |

### Error Handling Example

```csharp
public async Task<V1Pod> CreateKataContainerWithRetryAsync(
    string imageWithTag,
    int maxRetries = 3)
{
    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
        try
        {
            return await CreateKataContainerAsync(imageWithTag);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Name collision, retry with new name
            continue;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // RBAC issue, don't retry
            throw new InvalidOperationException("Service lacks permission to create pods", ex);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode >= 500)
        {
            // Server error, retry with backoff
            if (attempt < maxRetries - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                continue;
            }
            throw;
        }
    }

    throw new InvalidOperationException($"Failed to create pod after {maxRetries} attempts");
}
```

---

## Resource Management

### Resource Limits Best Practices

**Minimum Resources (Small workload):**
```
requests:
  memory: 64Mi   (actual: 224Mi with overhead)
  cpu: 100m      (actual: 350m with overhead)
```

**Recommended Resources (General workload):**
```
requests:
  memory: 128Mi  (actual: 288Mi with overhead)
  cpu: 250m      (actual: 500m with overhead)
limits:
  memory: 512Mi
  cpu: 1000m
```

**Heavy Workload:**
```
requests:
  memory: 512Mi  (actual: 672Mi with overhead)
  cpu: 500m      (actual: 750m with overhead)
limits:
  memory: 2048Mi
  cpu: 2000m
```

### Checking Node Capacity

Before creating many pods, check cluster capacity:

```csharp
public async Task<Dictionary<string, NodeCapacity>> GetNodeCapacityAsync()
{
    var nodes = await _client.ListNodeAsync();

    return nodes.Items.ToDictionary(
        n => n.Metadata.Name,
        n => new NodeCapacity
        {
            AllocatableMemoryMi = ParseResourceQuantity(n.Status.Allocatable["memory"]),
            AllocatableCpu = ParseResourceQuantity(n.Status.Allocatable["cpu"]),
            CapacityMemoryMi = ParseResourceQuantity(n.Status.Capacity["memory"]),
            CapacityCpu = ParseResourceQuantity(n.Status.Capacity["cpu"])
        });
}
```

**Note:** All 4 nodes support Kata (office1, office2, office3, office4). Scheduler will distribute pods automatically.

---

## Advanced Configurations

### Adding Environment Variables

```csharp
pod.Spec.Containers[0].Env = new List<V1EnvVar>
{
    new V1EnvVar { Name = "CONFIG_KEY", Value = "config_value" },
    new V1EnvVar { Name = "API_ENDPOINT", Value = "https://api.example.com" }
};
```

### Adding Volume Mounts

```csharp
// EmptyDir volume (temporary, deleted with pod)
pod.Spec.Volumes = new List<V1Volume>
{
    new V1Volume
    {
        Name = "scratch-space",
        EmptyDir = new V1EmptyDirVolumeSource()
    }
};

pod.Spec.Containers[0].VolumeMounts = new List<V1VolumeMount>
{
    new V1VolumeMount
    {
        Name = "scratch-space",
        MountPath = "/tmp/workspace"
    }
};
```

### Adding ConfigMap

```csharp
// Reference existing ConfigMap
pod.Spec.Volumes = new List<V1Volume>
{
    new V1Volume
    {
        Name = "config-volume",
        ConfigMap = new V1ConfigMapVolumeSource
        {
            Name = "my-config"
        }
    }
};

pod.Spec.Containers[0].VolumeMounts = new List<V1VolumeMount>
{
    new V1VolumeMount
    {
        Name = "config-volume",
        MountPath = "/etc/config"
    }
};
```

### Setting Command and Args

```csharp
pod.Spec.Containers[0].Command = new List<string> { "/bin/sh" };
pod.Spec.Containers[0].Args = new List<string> { "-c", "echo Hello && sleep 3600" };
```

### Network Policies

If you need network isolation, apply NetworkPolicy in `sandbox-containers` namespace. By default, all pods can communicate with each other.

---

## Monitoring and Logging

### Get Pod Logs

```csharp
public async Task<string> GetPodLogsAsync(
    string podName,
    string containerName = null,
    int tailLines = 100)
{
    return await _client.ReadNamespacedPodLogAsync(
        name: podName,
        namespaceParameter: "sandbox-containers",
        container: containerName,  // null = first container
        tailLines: tailLines);
}
```

### Stream Pod Logs

```csharp
public async Task StreamPodLogsAsync(
    string podName,
    Action<string> onLogLine,
    CancellationToken cancellationToken)
{
    var stream = await _client.ReadNamespacedPodLogAsync(
        name: podName,
        namespaceParameter: "sandbox-containers",
        follow: true,
        cancellationToken: cancellationToken);

    using var reader = new StreamReader(stream);
    while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync();
        onLogLine(line);
    }
}
```

### Get Pod Events

```csharp
public async Task<List<Corev1Event>> GetPodEventsAsync(string podName)
{
    var events = await _client.ListNamespacedEventAsync(
        namespaceParameter: "sandbox-containers",
        fieldSelector: $"involvedObject.name={podName}");

    return events.Items.OrderBy(e => e.LastTimestamp).ToList();
}
```

---

## Performance Considerations

### Pod Creation Rate
- **Kata pods:** Take 12-25 seconds to start
- **Recommendation:** Don't create more than 5-10 Kata pods per minute
- **Cluster capacity:** 4 nodes, each can handle multiple Kata VMs

### Resource Overhead
- **Per pod:** +160Mi RAM, +250m CPU
- **10 Kata pods:** ~1.6GB RAM overhead, ~2.5 CPU cores overhead
- **Plan accordingly** when scaling

### Cleanup Strategy
```csharp
// Run periodically (e.g., every 5 minutes)
public async Task PeriodicCleanupAsync()
{
    await CleanupCompletedPodsAsync();

    // Optional: Delete pods older than X hours
    await DeleteOldPodsAsync(TimeSpan.FromHours(24));
}
```

---

## Security Best Practices

### 1. Use Least Privilege RBAC
- Only grant permissions needed in `sandbox-containers` namespace
- Don't use ClusterRole/ClusterRoleBinding

### 2. Set Resource Limits
- Always define `limits` to prevent resource exhaustion
- Pod cannot exceed limits even if node has capacity

### 3. Validate Input
```csharp
public bool IsValidImageName(string image)
{
    // Validate image name format
    var regex = new Regex(@"^[a-z0-9\-\.]+(/[a-z0-9\-\.]+)*:[a-zA-Z0-9\-\.]+$");
    return regex.IsMatch(image);
}
```

### 4. Set Pod Security Standards
Consider setting pod security admission in namespace:
```yaml
apiVersion: v1
kind: Namespace
metadata:
  name: sandbox-containers
  labels:
    pod-security.kubernetes.io/enforce: baseline
    pod-security.kubernetes.io/audit: restricted
    pod-security.kubernetes.io/warn: restricted
```

### 5. Network Policies
Limit network access if workloads are untrusted.

---

## Testing Your Implementation

### Test 1: Create Single Pod

```csharp
[Test]
public async Task CreateSingleKataContainer_Success()
{
    var pod = await CreateKataContainerAsync("nginx:alpine");

    Assert.IsNotNull(pod);
    Assert.AreEqual("kata-qemu", pod.Spec.RuntimeClassName);
    Assert.AreEqual("sandbox-containers", pod.Metadata.NamespaceProperty);

    var ready = await WaitForPodReadyAsync(pod.Metadata.Name, TimeSpan.FromSeconds(60));
    Assert.IsTrue(ready);
}
```

### Test 2: List Pods

```csharp
[Test]
public async Task ListKataContainers_ReturnsCatalog()
{
    var catalog = await GetKataContainerCatalogAsync();

    Assert.IsNotNull(catalog);
    Assert.IsTrue(catalog.All(c => c.Name.StartsWith("kata-")));
}
```

### Test 3: Delete Pod

```csharp
[Test]
public async Task DeleteKataContainer_Success()
{
    var pod = await CreateKataContainerAsync("nginx:alpine");
    await WaitForPodReadyAsync(pod.Metadata.Name, TimeSpan.FromSeconds(60));

    await DeleteKataContainerAsync(pod.Metadata.Name);

    // Wait for deletion
    await Task.Delay(5000);

    var pods = await ListAllKataContainersAsync();
    Assert.IsFalse(pods.Any(p => p.Metadata.Name == pod.Metadata.Name));
}
```

### Manual Verification

After creating a pod, verify it's running in a VM:

```bash
# List pods in namespace
kubectl get pods -n sandbox-containers -o wide

# Get node where pod is running
NODE=$(kubectl get pod <pod-name> -n sandbox-containers -o jsonpath='{.spec.nodeName}')

# SSH to node and check for QEMU process
ssh localuser@<node-ip> 'ps aux | grep qemu | grep -v grep'
```

You should see a QEMU process for each Kata pod.

---

## Prerequisites Checklist

Before deploying your C# service:

- [ ] `sandbox-containers` namespace created
- [ ] ServiceAccount created (e.g., `kata-manager`)
- [ ] Role created with pod permissions in `sandbox-containers`
- [ ] RoleBinding links ServiceAccount to Role
- [ ] Your C# service pod spec includes:
    - `serviceAccountName: kata-manager`
    - `automountServiceAccountToken: true` (default, can omit)
- [ ] appsettings.json configured with namespace and runtime class
- [ ] Kubernetes C# client NuGet package installed
- [ ] Network connectivity from your service pod to API server

---

## Troubleshooting

### Issue: "Failed to create pod sandbox: runtime not found"
**Cause:** Pod missing `runtimeClassName: kata-qemu`
**Solution:** Always set `spec.runtimeClassName = "kata-qemu"`

### Issue: "Forbidden: pods is forbidden"
**Cause:** ServiceAccount lacks RBAC permissions
**Solution:** Verify Role and RoleBinding are applied correctly

### Issue: Pod stuck in Pending
**Possible causes:**
- Insufficient node resources
- Image pull errors
- Scheduling constraints

**Debug:**
```csharp
var pod = await _client.ReadNamespacedPodAsync(podName, "sandbox-containers");
var conditions = pod.Status?.Conditions;
// Check conditions for details
```

### Issue: Pod stuck in ContainerCreating for >60s
**Possible causes:**
- Image pull taking long
- Kata VM boot issue on node
- vhost modules not loaded

**Debug:**
```bash
kubectl describe pod <pod-name> -n sandbox-containers
# Check Events section
```

### Issue: Can't delete pod
**Cause:** Pod may be in terminating state with finalizers
**Solution:**
```csharp
// Force delete after grace period
await ForceDeleteKataContainerAsync(podName);
```

---

## API Reference Quick Guide

### Core Operations

| Operation | Method | Endpoint |
|-----------|--------|----------|
| Create Pod | `POST` | `/api/v1/namespaces/sandbox-containers/pods` |
| Get Pod | `GET` | `/api/v1/namespaces/sandbox-containers/pods/{name}` |
| List Pods | `GET` | `/api/v1/namespaces/sandbox-containers/pods` |
| Delete Pod | `DELETE` | `/api/v1/namespaces/sandbox-containers/pods/{name}` |
| Watch Pods | `GET` | `/api/v1/namespaces/sandbox-containers/pods?watch=true` |
| Get Logs | `GET` | `/api/v1/namespaces/sandbox-containers/pods/{name}/log` |

### C# Client Methods

```csharp
// Create
await _client.CreateNamespacedPodAsync(pod, "sandbox-containers");

// Get
await _client.ReadNamespacedPodAsync(name, "sandbox-containers");

// List
await _client.ListNamespacedPodAsync("sandbox-containers");

// List with label selector
await _client.ListNamespacedPodAsync("sandbox-containers", labelSelector: "app=myapp");

// Delete
await _client.DeleteNamespacedPodAsync(name, "sandbox-containers");

// Get logs
await _client.ReadNamespacedPodLogAsync(name, "sandbox-containers");

// Watch
_client.ListNamespacedPodWithHttpMessagesAsync("sandbox-containers", watch: true);
```

---

## Complete Service Structure Example

```
YourService/
├── appsettings.json              # Configuration
├── KubernetesConfig.cs           # Config model
├── IKataContainerService.cs      # Interface
├── KataContainerService.cs       # Implementation
├── Models/
│   ├── KataContainerInfo.cs      # Catalog model
│   └── CreateContainerRequest.cs # Request model
└── Program.cs                    # Startup

ServiceAccount and RBAC (Kubernetes manifests):
├── serviceaccount.yaml
├── role.yaml
└── rolebinding.yaml
```

---

## Next Steps

1. **Create Prerequisites:**
    - Namespace: `sandbox-containers`
    - ServiceAccount, Role, RoleBinding for your service

2. **Implement C# Service:**
    - Install `KubernetesClient` NuGet package
    - Configure in-cluster authentication
    - Implement create/list/delete methods

3. **Deploy Your Service:**
    - Build container image
    - Deploy to k3s with correct ServiceAccount
    - Test pod creation/deletion

4. **Monitor and Iterate:**
    - Check logs for errors
    - Monitor resource usage in `sandbox-containers`
    - Implement cleanup routines

---

## Additional Resources

- **Kubernetes C# Client:** https://github.com/kubernetes-client/csharp
- **Kubernetes API Reference:** https://kubernetes.io/docs/reference/generated/kubernetes-api/v1.33/
- **Kata Containers Docs:** https://katacontainers.io/
- **k3s Documentation:** https://docs.k3s.io/

---

## Support

For issues specific to this cluster:
1. Check Kata documentation: `/mnt/lab/k3s/system/kata-containers/README.md`
2. Review pod events: `kubectl describe pod <name> -n sandbox-containers`
3. Check API server connectivity from your service pod
4. Verify RBAC permissions are correctly applied

---

**Document Version:** 1.0
**Last Updated:** January 12, 2026
**Status:** Ready for Implementation