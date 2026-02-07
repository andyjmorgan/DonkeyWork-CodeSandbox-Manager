# Task 3: Audit Logging

## Summary

Implement structured audit logging for all credential operations across the Broker and proxy.

## Acceptance Criteria

- [ ] Broker logs every token request: `{ timestamp, sandbox_id, user_id, upstream_host, scopes, result (success/denied/error), latency_ms }`.
- [ ] Broker logs every binding operation: `{ timestamp, sandbox_id, user_id, operation (register/deregister) }`.
- [ ] Proxy logs every intercepted request: `{ timestamp, sandbox_id, upstream_host, method, path, had_token_injected, status_code }`.
- [ ] Token values are NEVER logged.
- [ ] Audit logs are written to a separate Serilog sink (distinct from operational logs) for easy collection.
- [ ] Log format is machine-parseable (JSON).

## Implementation Hints

Use Serilog's `ForContext` to create a dedicated audit logger:

```csharp
var auditLogger = Log.ForContext("AuditEvent", true);
auditLogger.Information("Token issued for {SandboxId} -> {UpstreamHost} with scopes {Scopes}",
    sandboxId, upstreamHost, scopes);
```

Configure a separate file sink filtered by the `AuditEvent` property.

## Files to Modify

- `src/DonkeyWork.CodeSandbox.CredentialBroker/` — add audit logging to token and binding endpoints
- `src/DonkeyWork.CodeSandbox.AuthProxy/` — add audit logging to MITM handler

## Dependencies

- Milestones 2-3 (endpoints exist to add logging to)
