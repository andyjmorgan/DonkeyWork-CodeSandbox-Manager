# Task 4: Error Handling

## Summary

Ensure all failure modes produce clear, actionable error responses to the sandbox workload.

## Acceptance Criteria

- [ ] Broker unavailable: return HTTP 502 with body `{"error": "credential_broker_unavailable", "message": "..."}`
- [ ] Broker returns 403 (sandbox not authorized): return HTTP 403 with body `{"error": "not_authorized", "message": "..."}`
- [ ] Upstream connection failure: return HTTP 502 with body `{"error": "upstream_unreachable", "message": "..."}`
- [ ] Token expired mid-request (rare): retry once with fresh token, then fail.
- [ ] All errors logged with correlation context (sandbox_id, upstream_host, request method/path).
- [ ] No token values in error messages or logs.

## Implementation Hints

Define a consistent error response model:

```csharp
public record ProxyError(string Error, string Message);
```

Return as JSON body in the HTTP error response sent back to the sandbox.

## Files to Modify

- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/TlsMitmHandler.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/ProxyServer.cs`

## Dependencies

- Tasks 1-2 (injection flow exists to add error handling around)
