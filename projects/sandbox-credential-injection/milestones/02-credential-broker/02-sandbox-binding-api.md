# Task 2: Sandbox Binding API

## Summary

Implement the binding endpoints that the Manager calls to register and deregister sandbox-to-user mappings.

## Acceptance Criteria

- [ ] `POST /api/bindings` — register a binding.
  - Input: `{ sandbox_id, user_id, allowed_upstreams: [{ host, scopes[] }] }`
  - Returns: `201 Created`
  - Rejects duplicate `sandbox_id` with `409 Conflict`
- [ ] `DELETE /api/bindings/{sandbox_id}` — deregister a binding.
  - Returns: `204 No Content` (or `404` if not found)
- [ ] `GET /api/bindings/{sandbox_id}` — look up a binding (for debugging/admin).
  - Returns the binding record or `404`
- [ ] Bindings stored in-memory (concurrent dictionary). Persistent storage is out of scope for now.
- [ ] Unit tests for binding CRUD operations.

## Implementation Hints

```csharp
public class SandboxBinding
{
    public string SandboxId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public List<AllowedUpstream> AllowedUpstreams { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public class AllowedUpstream
{
    public string Host { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
}
```

Use a `ConcurrentDictionary<string, SandboxBinding>` keyed by `sandbox_id`.

## Files to Create

- `src/DonkeyWork.CodeSandbox.CredentialBroker/Services/IBindingStore.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Services/InMemoryBindingStore.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Endpoints/BindingEndpoints.cs`

## Dependencies

- Task 1 (scaffold must exist)
