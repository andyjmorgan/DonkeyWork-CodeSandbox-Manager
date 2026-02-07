# Task 1: Broker Client in Proxy

## Summary

Add an HTTP client to the auth proxy that calls the Credential Broker's `POST /api/token` endpoint to obtain access tokens for upstream services.

## Acceptance Criteria

- [ ] `IBrokerTokenClient` interface in the proxy project.
- [ ] Implementation uses `HttpClient` to call the Broker.
- [ ] Broker URL is configurable (environment variable / appsettings).
- [ ] Includes the sandbox identity in the request (sandbox_id from configuration).
- [ ] Handles Broker responses: 200 (success), 403 (forbidden), 5xx (unavailable).
- [ ] Unit tests with mocked HTTP responses.

## Implementation Hints

```csharp
public interface IBrokerTokenClient
{
    Task<TokenResult?> GetTokenAsync(string upstreamHost, IEnumerable<string> scopes, CancellationToken ct);
}

public record TokenResult(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);
```

The proxy's sandbox identity (`sandbox_id`) is configured at startup — it's the pod name, injected via the Kubernetes downward API or an environment variable set by the Manager in the pod spec.

## Files to Create

- `src/DonkeyWork.CodeSandbox.AuthProxy/Services/IBrokerTokenClient.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Services/BrokerTokenClient.cs`

## Dependencies

- Milestone 2 (Broker token endpoint exists)
