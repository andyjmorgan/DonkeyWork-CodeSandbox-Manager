# Task 5: Update Pod Spec

## Summary

Modify `BuildPodSpec` and `BuildWarmPodSpec` in the Manager to add the auth proxy sidecar container, mount the CA certificate, and set proxy environment variables on the workload container.

## Context

Currently, pods are created with a single container named `workload`. This task adds a second container (`auth-proxy`) and the necessary volume mounts and environment variables to route traffic through the proxy.

### Existing pod spec structure (simplified)

```
Pod:
  Containers:
    - name: workload
      image: executor-image
      env: [user-provided env vars]
```

### Target pod spec structure

```
Pod:
  Volumes:
    - name: proxy-ca
      secret:
        secretName: sandbox-proxy-ca
        items:
          - key: tls.crt -> ca.crt
          - key: tls.key -> ca.key
  Containers:
    - name: workload
      image: executor-image
      env:
        - HTTP_PROXY=http://127.0.0.1:8080
        - HTTPS_PROXY=http://127.0.0.1:8080
        - NO_PROXY=localhost,127.0.0.1
        - NODE_EXTRA_CA_CERTS=/etc/proxy-ca/ca.crt
        [+ user-provided env vars]
      volumeMounts:
        - name: proxy-ca
          mountPath: /etc/proxy-ca
          readOnly: true
          subPath: ca.crt (only the public cert)
    - name: auth-proxy
      image: auth-proxy-image
      ports:
        - containerPort: 8080
        - containerPort: 8081
      env:
        - ProxyConfiguration__ProxyPort=8080
        - ProxyConfiguration__HealthPort=8081
        - ProxyConfiguration__CaCertificatePath=/certs/ca.crt
        - ProxyConfiguration__CaPrivateKeyPath=/certs/ca.key
        - ProxyConfiguration__AllowedDomains__0=graph.microsoft.com
        [+ domain allowlist from config]
      volumeMounts:
        - name: proxy-ca
          mountPath: /certs
          readOnly: true
      resources:
        requests: { memory: 64Mi, cpu: 100m }
        limits: { memory: 128Mi, cpu: 250m }
      readinessProbe:
        httpGet:
          path: /healthz
          port: 8081
        initialDelaySeconds: 2
        periodSeconds: 5
```

## Acceptance Criteria

- [ ] `BuildPodSpec` (in `KataContainerService.cs`) adds the sidecar container when proxy is enabled.
- [ ] `BuildWarmPodSpec` (in `PoolManager.cs`) adds the sidecar container when proxy is enabled.
- [ ] Proxy env vars (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`) are set on the workload container.
- [ ] CA cert volume is mounted:
  - Public cert only (`ca.crt`) into the workload container at `/etc/proxy-ca/`.
  - Full cert + key into the sidecar container at `/certs/`.
- [ ] Sidecar has its own resource requests/limits (from `KataContainerManager` config).
- [ ] Sidecar has a readiness probe on the health endpoint.
- [ ] Domain allowlist is passed to the sidecar via environment variables.
- [ ] Feature is toggleable — a config flag `EnableAuthProxy` (default `false`) controls whether the sidecar is added. This allows incremental rollout and backwards compatibility.
- [ ] User-provided environment variables on `CreateContainerRequest` are preserved (not overwritten by proxy env vars).
- [ ] Existing tests pass (update mocks/assertions as needed).

## Implementation Hints

### Configuration additions to `KataContainerManager.cs`

```csharp
// Auth proxy sidecar settings
public bool EnableAuthProxy { get; set; } = false;

[Required]
public string AuthProxyImage { get; set; } = "ghcr.io/andyjmorgan/donkeywork-codesandbox-authproxy:latest";

public ResourceConfig AuthProxySidecarResourceRequests { get; set; } = new() { MemoryMi = 64, CpuMillicores = 100 };
public ResourceConfig AuthProxySidecarResourceLimits { get; set; } = new() { MemoryMi = 128, CpuMillicores = 250 };

public int AuthProxyPort { get; set; } = 8080;
public int AuthProxyHealthPort { get; set; } = 8081;

public List<string> AuthProxyAllowedDomains { get; set; } = new()
{
    "graph.microsoft.com",
    "api.github.com",
    "github.com"
};

// Name of the Kubernetes Secret containing the CA cert and key
public string AuthProxyCaSecretName { get; set; } = "sandbox-proxy-ca";
```

### Pod spec modification pattern

In both `BuildPodSpec` and `BuildWarmPodSpec`, after creating the workload container:

```csharp
if (_config.EnableAuthProxy)
{
    // Add proxy env vars to workload container
    var proxyEnvVars = new List<V1EnvVar>
    {
        new() { Name = "HTTP_PROXY", Value = $"http://127.0.0.1:{_config.AuthProxyPort}" },
        new() { Name = "HTTPS_PROXY", Value = $"http://127.0.0.1:{_config.AuthProxyPort}" },
        new() { Name = "NO_PROXY", Value = "localhost,127.0.0.1" },
        new() { Name = "NODE_EXTRA_CA_CERTS", Value = "/etc/proxy-ca/ca.crt" },
    };
    container.Env = (container.Env ?? new List<V1EnvVar>()).Concat(proxyEnvVars).ToList();

    // Mount CA public cert into workload
    container.VolumeMounts = new List<V1VolumeMount>
    {
        new() { Name = "proxy-ca-public", MountPath = "/etc/proxy-ca", ReadOnlyProperty = true }
    };

    // Build sidecar container
    var sidecar = BuildAuthProxySidecar();

    // Add to pod spec
    pod.Spec.Containers.Add(sidecar);
    pod.Spec.Volumes = BuildProxyVolumes();
}
```

### Handling the CA Secret

The Kubernetes Secret `sandbox-proxy-ca` must exist in the target namespace before pods can reference it. This is an operational prerequisite — document it but don't create it automatically from the Manager.

For development (docker-compose), mount the CA cert files as bind mounts.

### User env var preservation

The existing code sets `container.Env` from `request.EnvironmentVariables`. The proxy env vars should be added *after* user vars, or merged carefully. If a user explicitly sets `HTTPS_PROXY`, the user's value should win (or we should reject it — discuss with team).

For now: add proxy env vars first, then append user vars (user vars override).

## Files to Modify

- `src/DonkeyWork.CodeSandbox.Manager/Configuration/KataContainerManager.cs` — add auth proxy config
- `src/DonkeyWork.CodeSandbox.Manager/Services/Container/KataContainerService.cs` — modify `BuildPodSpec`
- `src/DonkeyWork.CodeSandbox.Manager/Services/Pool/PoolManager.cs` — modify `BuildWarmPodSpec`
- `src/DonkeyWork.CodeSandbox.Manager/appsettings.json` — add default auth proxy config
- `docker-compose.yml` — add CA cert volume mounts for local dev

## Files to Create

- None (all changes are to existing files)

## Dependencies

- Task 1 (proxy project exists and has the expected config model)
- Task 3 (sidecar image name is known)
- Task 4 (CA mount path convention is established)

## Test Impact

- `KataContainerServiceTests` — update `BuildPodSpec` tests to verify sidecar presence when `EnableAuthProxy=true` and absence when `false`.
- `PoolManagerTests` — update `BuildWarmPodSpec` tests similarly.
- `KataContainerEndpointsTests` — may need mock updates for the new config properties.
