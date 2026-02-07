# Task 2: Header Injection

## Summary

Modify the proxy's MITM handler to inject `Authorization` headers into intercepted HTTP requests before forwarding to upstream.

## Acceptance Criteria

- [ ] After reading the HTTP request from the sandbox, the proxy:
  1. Calls the Broker client to get a token for the target domain.
  2. Adds or replaces the `Authorization` header with `Bearer <token>`.
  3. Optionally adds `X-Sandbox-Id` header for upstream telemetry (non-sensitive).
  4. Forwards the modified request to upstream.
- [ ] If the sandbox already included an `Authorization` header, it is **replaced** (prevent token injection from sandbox).
- [ ] If token acquisition fails, return an HTTP 502 Bad Gateway to the sandbox with a descriptive error body.
- [ ] Logging: log the injection event (domain, scopes) but never log the token value.

## Implementation Hints

In `TlsMitmHandler`, after reading the raw HTTP request from the client SslStream:

```csharp
// Parse the HTTP request (method, path, headers)
// Call broker for token
var token = await _brokerClient.GetTokenAsync(targetHost, scopes, ct);
if (token is null)
{
    // Return 502 to client
    await SendErrorResponse(clientStream, 502, "Token acquisition failed");
    return;
}

// Inject/replace Authorization header
request.Headers["Authorization"] = $"{token.TokenType} {token.AccessToken}";

// Forward to upstream
```

The HTTP parsing/rewriting can be simple string manipulation for this stage — full HTTP parsing libraries are optional.

## Files to Modify

- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/TlsMitmHandler.cs`

## Dependencies

- Task 1 (broker client exists)
- Milestone 1, Task 1 (MITM handler exists)
