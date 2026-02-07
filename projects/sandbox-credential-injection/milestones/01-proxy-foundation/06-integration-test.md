# Task 6: Integration Test

## Summary

Validate the full traffic path end-to-end: a command executed inside the sandbox container routes through the auth proxy sidecar and successfully reaches an upstream service.

## Context

With Tasks 1-5 complete, we have:
- A proxy that forwards traffic for allowlisted domains (no auth injection yet).
- A sandbox image that trusts the internal CA.
- Pod specs that wire the two together.

This task validates that everything works together.

## Acceptance Criteria

- [ ] A test (manual procedure or automated) that:
  1. Starts the auth proxy and executor containers (via docker-compose or in a test harness).
  2. Configures the executor's `HTTPS_PROXY` to point to the auth proxy.
  3. Executes `curl https://httpbin.org/get` via the executor's `/api/execute` endpoint.
  4. Verifies the response contains valid JSON from httpbin.org.
  5. Verifies proxy logs show the request was intercepted and forwarded.
- [ ] A negative test: `curl https://example.com/` (not in allowlist) returns a 403 or connection error.
- [ ] Test can run in CI (docker-compose based) or locally.

## Implementation Hints

### Docker-compose test approach

The simplest approach is to use the existing docker-compose setup with the auth proxy added:

1. `docker-compose up -d code-execution-server auth-proxy`
2. `curl -X POST http://localhost:8666/api/execute -H 'Content-Type: application/json' -d '{"command": "curl -s https://httpbin.org/get", "timeoutSeconds": 30}'`
3. Parse the SSE response, verify it contains `"origin"` (httpbin's response).
4. Check auth-proxy container logs for the CONNECT request.

### Automated test approach

Add an integration test project or extend the existing `DonkeyWork.CodeSandbox.Server.IntegrationTests`:

```csharp
[Fact]
public async Task CurlThroughProxy_AllowedDomain_ReturnsSuccess()
{
    // Start auth proxy + executor using Testcontainers
    // Configure executor with HTTPS_PROXY pointing to proxy
    // Execute "curl -s https://httpbin.org/get" via /api/execute
    // Assert response contains valid JSON
}

[Fact]
public async Task CurlThroughProxy_BlockedDomain_Fails()
{
    // Execute "curl -s https://example.com/"
    // Assert response indicates connection refused or 403
}
```

### What to verify in proxy logs

```
[INF] CONNECT request: httpbin.org:443 - ALLOWED (MITM mode)
[INF] Upstream TLS connection established to httpbin.org:443
[INF] Request forwarded: GET / -> httpbin.org (200 OK)
```

And for blocked domains:
```
[WRN] CONNECT request: example.com:443 - BLOCKED (not in allowlist)
```

### Known considerations

- **DNS**: The auth proxy container needs DNS resolution to reach `httpbin.org`. In docker-compose, this works automatically. In Kubernetes, ensure DNS egress is allowed.
- **CA trust**: In docker-compose, the CA cert needs to be shared between containers via a volume mount. The test setup should handle this.
- **Timing**: The proxy needs to be healthy before the executor tries to use it. Use health checks or a startup delay.

## Files to Create

- Test script or test class (location TBD based on approach chosen)
- `docker-compose.test.yml` (if separate compose file needed for test orchestration)

## Files to Modify

- `docker-compose.yml` — may need adjustments for test scenarios

## Dependencies

- Tasks 1-5 must be complete.
- This is the final task in Milestone 1.
