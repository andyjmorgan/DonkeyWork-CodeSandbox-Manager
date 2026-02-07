# Task 4: Update Executor Image

## Summary

Update the sandbox executor Dockerfile to trust the internal CA certificate, so that TLS connections through the MITM proxy are accepted by `curl`, `git`, Python `requests`, Node.js `https`, and the .NET runtime.

## Context

When the proxy performs TLS MITM, it presents certificates signed by the internal CA. The sandbox workload needs to trust this CA. Different tools have different trust store mechanisms:

- **System (curl, git, most CLI tools)**: `/usr/local/share/ca-certificates/` + `update-ca-certificates`
- **Python requests**: Uses certifi by default, but falls back to system on Debian if configured
- **Node.js**: Uses system trust store by default on recent versions, or `NODE_EXTRA_CA_CERTS`
- **.NET**: Uses system trust store on Linux

## Acceptance Criteria

- [ ] Executor Dockerfile updated to support mounting a CA certificate at a known path.
- [ ] Entrypoint or init script that:
  - Checks if a CA cert exists at the mount path (e.g., `/etc/proxy-ca/ca.crt`).
  - If present: copies it to `/usr/local/share/ca-certificates/proxy-ca.crt` and runs `update-ca-certificates`.
  - If not present: starts normally (backwards compatible — existing deployments without the proxy still work).
- [ ] Sets `NODE_EXTRA_CA_CERTS` environment variable pointing to the CA cert (for Node.js compatibility).
- [ ] Existing functionality (command execution, health checks) is unaffected.
- [ ] The `ca-certificates` package is already present (verify; install if missing).

## Implementation Hints

### Entrypoint wrapper script

Create a small shell script that handles CA setup before starting the .NET app:

```bash
#!/bin/bash
set -e

# Install proxy CA certificate if mounted
CA_MOUNT="/etc/proxy-ca/ca.crt"
if [ -f "$CA_MOUNT" ]; then
    cp "$CA_MOUNT" /usr/local/share/ca-certificates/proxy-ca.crt
    update-ca-certificates 2>/dev/null || true
    export NODE_EXTRA_CA_CERTS="$CA_MOUNT"
    echo "Proxy CA certificate installed"
fi

# Start the application
exec dotnet DonkeyWork.CodeSandbox.Server.dll "$@"
```

### Dockerfile changes

```dockerfile
# Ensure ca-certificates is installed (it likely already is in aspnet base image)
RUN apt-get update && apt-get install -y ca-certificates && rm -rf /var/lib/apt/lists/*

# Copy entrypoint wrapper
COPY src/DonkeyWork.CodeSandbox.Server/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENTRYPOINT ["/app/entrypoint.sh"]
```

### Mount path convention

The CA cert will be mounted from a Kubernetes Secret or ConfigMap at `/etc/proxy-ca/ca.crt`. This is configured in the pod spec (Task 5).

### Backwards compatibility

The entrypoint checks for the file's existence. If no CA cert is mounted (e.g., pods created before this feature), the executor starts normally. This ensures zero breaking changes to existing deployments.

## Files to Create

- `src/DonkeyWork.CodeSandbox.Server/entrypoint.sh`

## Files to Modify

- `src/DonkeyWork.CodeSandbox.Server/Dockerfile` — add ca-certificates, copy entrypoint, change ENTRYPOINT

## Dependencies

- Task 2 (CA Certificate Generation) — need to know the cert format and mount path.
- Independent of Task 1 and Task 3.
