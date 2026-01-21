# DonkeyWork CodeSandbox

[![PR Build and Test](https://github.com/andyjmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/pr-build-test.yml/badge.svg)](https://github.com/andyjmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/pr-build-test.yml)
[![Release](https://github.com/andyjmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/release.yml/badge.svg)](https://github.com/andyjmorgan/DonkeyWork-CodeSandbox-Manager/actions/workflows/release.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

A unified monorepo containing both the **Manager API** (Kata container orchestration) and **Executor API** (sandboxed code execution with Python, Node.js, and bash support).

## Components

- **Manager API** (`src/DonkeyWork.CodeSandbox.Manager`): REST API service for managing Kata containers in a Kubernetes cluster
- **Executor API** (`src/DonkeyWork.CodeSandbox.Server`): HTTP+SSE server for executing commands inside sandboxed containers
- **Shared Contracts** (`src/DonkeyWork.CodeSandbox.Contracts`): Common models and contracts
- **Client Library** (`src/DonkeyWork.CodeSandbox.Client`): .NET client for consuming the Executor API

## Features

- **Create Kata Containers**: Dynamically create VM-isolated containers with custom configuration
- **List Containers**: Retrieve all Kata containers with their status and metadata
- **Get Container Details**: Fetch detailed information about a specific container
- **Delete Containers**: Remove containers and terminate their associated VMs
- **Health Checks**: Built-in health check endpoint for monitoring
- **OpenAPI Documentation**: Interactive API documentation via Scalar

## Architecture

- **Framework**: ASP.NET Core 10.0 (Minimal APIs)
- **Kubernetes Client**: Official Kubernetes C# client (v18.0.13)
- **Logging**: Serilog with structured logging
- **Configuration**: IOptions with data validation
- **Container Runtime**: Kata Containers (kata-qemu)

### System Overview

```mermaid
flowchart TB
    subgraph Client["Client Application"]
        CA[API Consumer]
    end

    subgraph Manager["Manager API :8668"]
        ME["/api/kata endpoints"]
        KCS[KataContainerService]
    end

    subgraph K8s["Kubernetes Cluster"]
        API[Kubernetes API Server]
        subgraph NS["sandbox-containers namespace"]
            subgraph KP1["Kata Pod 1"]
                VM1["Kata VM"]
                EX1["Executor API :8666"]
            end
            subgraph KP2["Kata Pod 2"]
                VM2["Kata VM"]
                EX2["Executor API :8666"]
            end
        end
    end

    CA -->|"REST/SSE"| ME
    ME --> KCS
    KCS -->|"Create/List/Delete Pods"| API
    API --> KP1
    API --> KP2
    KCS -->|"Execute Commands"| EX1
    KCS -->|"Execute Commands"| EX2
```

### Container Creation Flow

```mermaid
sequenceDiagram
    participant Client
    participant Manager as Manager API
    participant K8s as Kubernetes API
    participant Kata as Kata Pod
    participant Executor as Executor API

    Client->>Manager: POST /api/kata
    Manager->>Manager: Generate unique pod name
    Manager->>K8s: Create Pod (kata-qemu runtime)
    K8s-->>Manager: Pod created (Pending)
    Manager-->>Client: SSE: created event

    loop Wait for Pod Ready
        Manager->>K8s: Get Pod status
        K8s-->>Manager: Pod status
        Manager-->>Client: SSE: waiting event
    end

    K8s->>Kata: Start Kata VM
    Kata->>Kata: Boot VM + Start Executor API

    loop Health Check Executor API
        Manager->>Executor: GET /healthz
        alt Healthy
            Executor-->>Manager: 200 OK
            Manager-->>Client: SSE: healthcheck (healthy)
        else Not Ready
            Executor-->>Manager: Connection refused / timeout
            Manager-->>Client: SSE: healthcheck (unhealthy)
            Manager-->>Client: SSE: waiting event
        end
    end

    Manager-->>Client: SSE: ready event
```

### Command Execution Flow

```mermaid
sequenceDiagram
    participant Client
    participant Manager as Manager API
    participant Executor as Executor API (in Kata Pod)
    participant Process as Bash Process

    Client->>Manager: POST /api/kata/{sandboxId}/execute
    Manager->>Manager: Lookup Pod IP
    Manager->>Executor: POST /api/execute (SSE)

    Executor->>Process: Spawn /bin/bash -c "command"

    loop Stream Output
        Process-->>Executor: stdout/stderr
        Executor-->>Manager: SSE: OutputEvent
        Manager-->>Client: SSE: OutputEvent
    end

    Process-->>Executor: Exit code
    Executor-->>Manager: SSE: CompletedEvent
    Manager-->>Client: SSE: CompletedEvent
```

### Component Interaction

```mermaid
flowchart LR
    subgraph Manager["Manager API"]
        direction TB
        EP[Endpoints]
        SVC[KataContainerService]
        CFG[Configuration]
    end

    subgraph Executor["Executor API"]
        direction TB
        CTRL[ExecutionController]
        MP[ManagedProcess]
    end

    subgraph Shared["Contracts"]
        EE[ExecutionEvent]
        OE[OutputEvent]
        CE[CompletedEvent]
    end

    EP --> SVC
    SVC --> CFG
    SVC -.->|HTTP/SSE| CTRL
    CTRL --> MP
    MP --> OE
    MP --> CE
    OE --> EE
    CE --> EE
```

## Prerequisites

1. **Kubernetes Cluster**: k3s v1.33.5+ with Kata Containers enabled
2. **Namespace**: `sandbox-containers` namespace must exist
3. **RBAC**: ServiceAccount with appropriate permissions (see k8s/ folder)
4. **Runtime Class**: `kata-qemu` RuntimeClass configured in the cluster

## Configuration

The service is configured via `appsettings.json`:

```json
{
  "KataContainerManager": {
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
    "CleanupCompletedPods": true,
    "PodReadyTimeoutSeconds": 90
  }
}
```

### Configuration Options

- `TargetNamespace`: Kubernetes namespace where containers will be created
- `RuntimeClassName`: Runtime class for Kata isolation (must be "kata-qemu")
- `DefaultResourceRequests`: Default CPU and memory requests
- `DefaultResourceLimits`: Default CPU and memory limits
- `PodNamePrefix`: Prefix for generated pod names
- `CleanupCompletedPods`: Whether to automatically clean up completed pods
- `PodReadyTimeoutSeconds`: Timeout for waiting for pods to become ready (30-300 seconds)

## API Endpoints

All endpoints are prefixed with `/api/kata` to support future multi-runtime capabilities (Kata, gVisor, etc.).

### POST /api/kata
Create a new Kata container. The container image is fixed to the configured default executor image for security.

**Request Body:**
```json
{
  "labels": {
    "environment": "sandbox",
    "project": "test"
  },
  "environmentVariables": {
    "KEY": "value"
  },
  "command": ["/bin/sh"],
  "args": ["-c", "echo Hello && sleep 3600"],
  "resources": {
    "requests": {
      "memoryMi": 256,
      "cpuMillicores": 500
    },
    "limits": {
      "memoryMi": 1024,
      "cpuMillicores": 2000
    }
  },
  "waitForReady": true
}
```

**Response:** Server-Sent Events (SSE) stream with creation progress:
```
data: {"eventType":"created","podName":"kata-sandbox-a1b2c3d4","phase":"Pending"}

data: {"eventType":"waiting","podName":"kata-sandbox-a1b2c3d4","attemptNumber":1,"phase":"Pending","message":"Waiting for pod to be ready"}

data: {"eventType":"healthcheck","podName":"kata-sandbox-a1b2c3d4","isHealthy":true,"message":"Executor API is healthy","podIP":"10.42.1.15"}

data: {"eventType":"ready","podName":"kata-sandbox-a1b2c3d4","containerInfo":{...},"elapsedSeconds":15.2}
```

### GET /api/kata
List all Kata containers.

**Response:** `200 OK`
```json
[
  {
    "name": "kata-sandbox-a1b2c3d4",
    "phase": "Running",
    "isReady": true,
    "createdAt": "2026-01-13T10:30:00Z",
    "nodeName": "office1",
    "podIP": "10.42.1.15",
    "labels": {
      "app": "kata-manager",
      "runtime": "kata"
    },
    "image": "nginx:alpine"
  }
]
```

### GET /api/kata/{podName}
Get details of a specific container.

**Response:** `200 OK` (same structure as list response)

### DELETE /api/kata/{podName}
Delete a Kata container.

**Response:** `200 OK`
```json
{
  "success": true,
  "message": "Container kata-sandbox-a1b2c3d4 deleted successfully",
  "podName": "kata-sandbox-a1b2c3d4"
}
```

### GET /healthz
Health check endpoint.

**Response:** `200 OK` (Healthy) or `503 Service Unavailable` (Unhealthy)

## Running Locally

### Prerequisites
- .NET 10.0 SDK
- Access to a k3s cluster with kubeconfig configured
- kubectl configured
- Docker (for containerized deployment)

### Option 1: Run with Docker Compose (Recommended)

1. **Clone the repository**
   ```bash
   git clone https://github.com/andyjmorgan/DonkeyWork-CodeSandbox-Manager.git
   cd DonkeyWork-CodeSandbox-Manager
   ```

2. **Start the service**
   ```bash
   docker-compose up -d
   ```

3. **Access the API**
   - API: http://localhost:8668
   - Documentation: http://localhost:8668/scalar/v1
   - Health Check: http://localhost:8668/healthz

4. **View logs**
   ```bash
   docker-compose logs -f kata-container-manager
   ```

5. **Stop the service**
   ```bash
   docker-compose down
   ```

### Option 2: Run with .NET CLI

1. **Clone the repository**
   ```bash
   cd src/DonkeyWork.CodeSandbox-Manager
   ```

2. **Restore dependencies**
   ```bash
   dotnet restore
   ```

3. **Run the application**
   ```bash
   dotnet run
   ```

4. **Access the API**
   - API: http://localhost:5000 or https://localhost:5001
   - Documentation: http://localhost:5000/scalar/v1
   - Health Check: http://localhost:5000/healthz

## Deploying to Kubernetes

### 1. Apply Prerequisites

```bash
# Create namespace
kubectl apply -f k8s/namespace.yaml

# Create RBAC resources
kubectl apply -f k8s/serviceaccount.yaml
kubectl apply -f k8s/role.yaml
kubectl apply -f k8s/rolebinding.yaml
```

### 2. Pull Docker Image from GitHub Container Registry

The project uses GitHub Actions for CI/CD. Docker images are automatically built and published to GitHub Container Registry on every merge to main.

```bash
# Pull the latest image
docker pull ghcr.io/andyjmorgan/donkeywork-codesandbox-manager:latest

# Or pull a specific version
docker pull ghcr.io/andyjmorgan/donkeywork-codesandbox-manager:v1.0.0
```

Alternatively, you can build locally:

```bash
# Build the Docker image
docker build -t kata-manager-api:latest .

# Tag for your registry
docker tag kata-manager-api:latest your-registry/kata-manager-api:latest

# Push to registry
docker push your-registry/kata-manager-api:latest
```

### 3. Deploy the Application

```bash
# Update image in deployment.yaml if using a registry
kubectl apply -f k8s/deployment.yaml
```

### 4. Verify Deployment

```bash
# Check pod status
kubectl get pods -n default -l app=kata-manager-api

# Check logs
kubectl logs -n default -l app=kata-manager-api -f

# Test the health check
kubectl port-forward -n default svc/kata-manager-api 8080:80
curl http://localhost:8080/healthz
```

## CI/CD Pipeline

The project uses GitHub Actions for continuous integration and deployment with two main workflows:

### PR Build and Test Workflow

Triggered on pull requests to main, this workflow:
- Builds the .NET solution
- Runs all unit tests with code coverage
- Builds the Docker image (without pushing)
- Performs code linting and security scanning
- Uploads test results and artifacts

### Release Workflow

Triggered on merges to main, this workflow:
- Builds and tests the solution
- Determines semantic version using GitVersion
- Builds multi-architecture Docker images (amd64, arm64)
- Pushes images to GitHub Container Registry (ghcr.io)
- Creates a GitHub release with automatic changelog
- Updates Kubernetes deployment manifests
- Generates detailed release summaries

### Image Tags

Docker images are tagged with multiple formats:
- `latest` - Latest stable release
- `vX.Y.Z` - Semantic version (e.g., v1.2.3)
- `vX.Y` - Major.Minor version (e.g., v1.2)
- `vX` - Major version (e.g., v1)
- `main-<sha>` - Branch and commit SHA

### GitHub Secrets Required

For the release workflow to function, ensure the following secrets are configured:
- `GITHUB_TOKEN` - Automatically provided by GitHub Actions (no setup needed)

The workflow uses the built-in `GITHUB_TOKEN` with appropriate permissions for:
- Pushing Docker images to GitHub Container Registry
- Creating GitHub releases
- Updating deployment manifests

### Caching

Both workflows use aggressive caching strategies:
- .NET package caching via `setup-dotnet` action
- Docker layer caching using GitHub Actions cache
- Build artifact caching for faster subsequent builds

## Development

### Project Structure

```
DonkeyWork-CodeSandbox-Manager/
├── src/
│   ├── DonkeyWork.CodeSandbox.Manager/      # Manager API (container orchestration)
│   │   ├── Configuration/
│   │   │   └── KataContainerManager.cs      # Configuration models with validation
│   │   ├── Endpoints/
│   │   │   └── KataContainerEndpoints.cs    # Minimal API endpoints (/api/kata)
│   │   ├── Models/
│   │   │   ├── CreateContainerRequest.cs    # Request DTOs
│   │   │   ├── KataContainerInfo.cs         # Response DTOs
│   │   │   └── DeleteContainerResponse.cs
│   │   ├── Services/
│   │   │   ├── IKataContainerService.cs     # Service interface
│   │   │   └── KataContainerService.cs      # Kubernetes operations
│   │   ├── Program.cs                       # Application entry point
│   │   └── appsettings.json                 # Configuration
│   ├── DonkeyWork.CodeSandbox.Server/       # Executor API (code execution)
│   │   ├── Controllers/
│   │   │   └── ExecutionController.cs       # /api/execute endpoint
│   │   ├── Services/
│   │   │   └── ManagedProcess.cs            # Process management with streaming
│   │   └── Program.cs
│   ├── DonkeyWork.CodeSandbox.Contracts/    # Shared models
│   │   ├── Events/
│   │   │   └── ExecutionEvent.cs            # OutputEvent, CompletedEvent
│   │   └── Requests/
│   │       └── ExecuteCommand.cs
│   └── DonkeyWork.CodeSandbox.Client/       # .NET client library
├── test/
│   ├── DonkeyWork.CodeSandbox.Manager.Tests/
│   └── DonkeyWork.CodeSandbox.Server.IntegrationTests/
├── Dockerfile                               # Manager API container
├── docker-compose.yml                       # Local development setup
└── .github/workflows/
    ├── pr-build-test.yml                    # PR validation workflow
    └── release.yml                          # Release automation workflow
```

### Key Design Decisions

1. **Minimal APIs**: Uses ASP.NET Core minimal APIs for simpler, more performant endpoints
2. **IOptions Pattern**: Configuration is validated at startup using data annotations
3. **In-Cluster Auth**: Automatically uses ServiceAccount tokens when running in Kubernetes
4. **Scoped Services**: KataContainerService is scoped to match request lifetime
5. **Structured Logging**: Serilog provides structured logging with context

## Troubleshooting

### Container fails to create
- Verify the image exists and is accessible
- Check that the `sandbox-containers` namespace exists
- Ensure the RuntimeClass `kata-qemu` is configured
- Check RBAC permissions for the ServiceAccount

### Permission denied errors
- Verify Role and RoleBinding are correctly applied
- Ensure ServiceAccount is attached to the pod
- Check that the service can reach the Kubernetes API server

### Pods stuck in Pending
- Check cluster node capacity
- Verify Kata is installed on nodes
- Check pod events: `kubectl describe pod <pod-name> -n sandbox-containers`

### Health check failures
- Verify the application is running: `kubectl logs <pod-name>`
- Check if the service can connect to Kubernetes API
- Review configuration validation errors in logs

## Security Considerations

1. **Least Privilege**: Role only grants permissions in `sandbox-containers` namespace
2. **Non-Root Container**: Dockerfile creates and runs as non-root user (uid 1000)
3. **Resource Limits**: All containers have resource limits to prevent exhaustion
4. **Input Validation**: Image names and configuration are validated
5. **VM Isolation**: Kata containers provide hardware-level isolation

## Performance

- **Pod Creation**: 12-25 seconds (includes VM boot time)
- **Overhead**: +160Mi RAM, +250m CPU per Kata container
- **Recommended Rate**: 5-10 Kata pods per minute
- **Cluster Capacity**: 4 nodes, each supporting multiple Kata VMs

## References

- [MVP Documentation](MVP.md) - Detailed implementation guide
- [Kata Containers](https://katacontainers.io/) - Official Kata documentation
- [Kubernetes C# Client](https://github.com/kubernetes-client/csharp) - Client library
- [k3s Documentation](https://docs.k3s.io/) - k3s cluster documentation

## License

[Your License Here]

## Support

For cluster-specific issues, refer to:
- Kata documentation: `/mnt/lab/k3s/system/kata-containers/README.md`
- Check pod events and logs
- Verify RBAC permissions
