# Task 5: Manager Integration

## Summary

Wire the Manager's allocation and cleanup flows to call the Credential Broker's binding API, so that sandbox-to-user mappings are registered when a pod is allocated and deregistered when it's cleaned up.

## Acceptance Criteria

- [ ] `PoolManager.AllocateWarmSandboxAsync()` calls `POST /api/bindings` after successful allocation.
- [ ] `KataContainerService.DeleteContainerAsync()` calls `DELETE /api/bindings/{sandbox_id}`.
- [ ] `ContainerCleanupService` calls `DELETE /api/bindings/{sandbox_id}` when cleaning up expired/idle containers.
- [ ] An `IBrokerClient` interface abstracts the HTTP calls to the Broker.
- [ ] Broker endpoint URL is configurable in `KataContainerManager`.
- [ ] Failure to register with the Broker does not block sandbox allocation (log warning, continue — the sandbox just won't have credential access).
- [ ] Failure to deregister is logged but doesn't prevent pod deletion.
- [ ] Unit tests with mocked `IBrokerClient`.

## Implementation Hints

### Broker client

```csharp
public interface IBrokerClient
{
    Task<bool> RegisterBindingAsync(string sandboxId, string userId, List<AllowedUpstream> upstreams, CancellationToken ct);
    Task<bool> DeregisterBindingAsync(string sandboxId, CancellationToken ct);
}
```

Use `IHttpClientFactory` for the HTTP client (consistent with existing Manager patterns).

### Configuration addition

```csharp
// In KataContainerManager.cs
public string? CredentialBrokerUrl { get; set; }  // e.g. "http://credential-broker.sandbox-system:8090"
```

When `CredentialBrokerUrl` is null/empty, the broker client is a no-op (feature disabled).

### Allocation flow change

In `AllocateWarmSandboxAsync`, after the pod is successfully patched:

```csharp
if (!string.IsNullOrEmpty(_config.CredentialBrokerUrl))
{
    var registered = await _brokerClient.RegisterBindingAsync(podName, userId, allowedUpstreams, cancellationToken);
    if (!registered)
    {
        _logger.LogWarning("Failed to register sandbox {PodName} with credential broker", podName);
    }
}
```

## Files to Create

- `src/DonkeyWork.CodeSandbox.Manager/Services/Broker/IBrokerClient.cs`
- `src/DonkeyWork.CodeSandbox.Manager/Services/Broker/BrokerClient.cs`
- `src/DonkeyWork.CodeSandbox.Manager/Services/Broker/NoOpBrokerClient.cs` (when feature disabled)

## Files to Modify

- `src/DonkeyWork.CodeSandbox.Manager/Services/Pool/PoolManager.cs` — call broker on allocate
- `src/DonkeyWork.CodeSandbox.Manager/Services/Container/KataContainerService.cs` — call broker on delete
- `src/DonkeyWork.CodeSandbox.Manager/Services/Background/ContainerCleanupService.cs` — call broker on cleanup
- `src/DonkeyWork.CodeSandbox.Manager/Configuration/KataContainerManager.cs` — add broker URL config
- `src/DonkeyWork.CodeSandbox.Manager/Program.cs` — register IBrokerClient in DI

## Dependencies

- Tasks 1-2 (Broker endpoints must be defined)
