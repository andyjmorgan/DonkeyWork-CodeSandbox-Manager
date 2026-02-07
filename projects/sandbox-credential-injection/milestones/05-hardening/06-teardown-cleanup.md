# Task 6: Teardown Cleanup

## Summary

Ensure all credential-related state is cleaned up when a sandbox is torn down.

## Acceptance Criteria

- [ ] When a sandbox pod is deleted (manually or by cleanup service):
  - Broker binding is deregistered
  - Cached tokens in the Broker for that sandbox are evicted
  - Proxy's in-memory token cache is destroyed (proxy process dies with the pod)
- [ ] When a user is "offboarded" (all their sandboxes removed):
  - Wallet store tokens for that user can be explicitly revoked (if supported by upstream provider)
- [ ] Orphan detection: periodic Broker job that checks for bindings where the pod no longer exists and cleans them up.
- [ ] Tests for cleanup flows.

## Implementation Hints

The proxy's in-memory cache is automatically destroyed when the pod is deleted (sidecar container stops). The main concern is Broker-side state.

### Orphan cleanup

```csharp
// Background service in the Broker
public class OrphanBindingCleanupService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            var bindings = _bindingStore.GetAllBindings();
            foreach (var binding in bindings)
            {
                if (!await _kubeClient.PodExistsAsync(binding.SandboxId))
                {
                    _bindingStore.RemoveBinding(binding.SandboxId);
                    _logger.LogInformation("Cleaned up orphan binding for {SandboxId}", binding.SandboxId);
                }
            }
        }
    }
}
```

## Files to Create

- `src/DonkeyWork.CodeSandbox.CredentialBroker/Services/Background/OrphanBindingCleanupService.cs`

## Files to Modify

- `src/DonkeyWork.CodeSandbox.CredentialBroker/Program.cs` — register background service

## Dependencies

- Milestone 2, Task 5 (Manager integration for delete calls)
