# Task 3: Sidecar Docker Image

## Summary

Create a Dockerfile for the auth proxy sidecar. This image runs alongside the sandbox workload in the same Kata pod.

## Context

The sidecar image should be minimal — it only needs the .NET runtime and the auth proxy binary. No development tools, no Python, no Node.js. Keep the image small to minimize warm pool overhead (the sidecar starts with every sandbox pod).

## Acceptance Criteria

- [ ] Dockerfile at `src/DonkeyWork.CodeSandbox.AuthProxy/Dockerfile`.
- [ ] Multi-stage build (SDK for build, aspnet runtime for final image).
- [ ] Runs as non-root user (match existing pattern: UID 10000).
- [ ] Exposes ports 8080 (proxy) and 8081 (health).
- [ ] Health check configured.
- [ ] Image builds successfully.
- [ ] Added to `docker-compose.yml` for local development.
- [ ] Image name configured in `KataContainerManager` configuration.

## Implementation Hints

### Dockerfile structure

Follow the same pattern as the existing executor Dockerfile (`src/DonkeyWork.CodeSandbox.Server/Dockerfile`):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy Directory.Packages.props for central package management
COPY ["Directory.Packages.props", "./"]
COPY nuget.config* ./

# Copy project file and restore
COPY ["src/DonkeyWork.CodeSandbox.AuthProxy/DonkeyWork.CodeSandbox.AuthProxy.csproj", "DonkeyWork.CodeSandbox.AuthProxy/"]
RUN dotnet restore "DonkeyWork.CodeSandbox.AuthProxy/DonkeyWork.CodeSandbox.AuthProxy.csproj"

# Copy source and build
COPY ["src/", "."]
WORKDIR "/src/DonkeyWork.CodeSandbox.AuthProxy"
RUN dotnet build "DonkeyWork.CodeSandbox.AuthProxy.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DonkeyWork.CodeSandbox.AuthProxy.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=publish /app/publish .

# No extra tools needed — this is a minimal proxy image

EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "DonkeyWork.CodeSandbox.AuthProxy.dll"]
```

### docker-compose.yml addition

```yaml
auth-proxy:
  build:
    context: .
    dockerfile: src/DonkeyWork.CodeSandbox.AuthProxy/Dockerfile
  ports:
    - "8080:8080"
    - "8081:8081"
  environment:
    ProxyConfiguration__ProxyPort: "8080"
    ProxyConfiguration__HealthPort: "8081"
    ProxyConfiguration__AllowedDomains__0: "httpbin.org"
    ProxyConfiguration__AllowedDomains__1: "graph.microsoft.com"
    ProxyConfiguration__AllowedDomains__2: "api.github.com"
    ProxyConfiguration__AllowedDomains__3: "github.com"
  networks:
    - kata-network
  healthcheck:
    test: ["CMD", "curl", "-f", "http://localhost:8081/healthz"]
    interval: 30s
    timeout: 10s
    retries: 3
```

### KataContainerManager config addition

```csharp
// In KataContainerManager.cs
[Required]
public string AuthProxyImage { get; set; } = "ghcr.io/andyjmorgan/donkeywork-codesandbox-authproxy:latest";

public ResourceConfig? AuthProxySidecarResourceRequests { get; set; }
public ResourceConfig? AuthProxySidecarResourceLimits { get; set; }
```

## Files to Create

- `src/DonkeyWork.CodeSandbox.AuthProxy/Dockerfile`

## Files to Modify

- `docker-compose.yml` — add auth-proxy service
- `src/DonkeyWork.CodeSandbox.Manager/Configuration/KataContainerManager.cs` — add sidecar image + resource config

## Dependencies

- Task 1 (Build Forward Proxy) — needs the project to exist before building the image.
