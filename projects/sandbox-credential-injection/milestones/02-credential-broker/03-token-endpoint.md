# Task 3: Token Endpoint

## Summary

Implement `POST /api/token` — the core endpoint that the auth proxy calls to get access tokens for upstream services.

## Acceptance Criteria

- [ ] `POST /api/token` accepts:
  ```json
  {
    "sandbox_id": "kata-sandbox-abc123",
    "upstream_host": "graph.microsoft.com",
    "scopes": ["https://graph.microsoft.com/.default"]
  }
  ```
- [ ] Validates:
  - `sandbox_id` has an active binding (else `403`)
  - `upstream_host` is in the binding's allowed upstreams (else `403`)
  - requested scopes are subset of allowed scopes (else `403`)
- [ ] Returns:
  ```json
  {
    "access_token": "<token>",
    "token_type": "bearer",
    "expires_at": "2025-01-01T00:00:00Z"
  }
  ```
- [ ] Calls the Wallet Store to obtain/refresh the actual token for the resolved user.
- [ ] Audit logs every token request (sandbox_id, upstream, scopes, success/failure — never the token itself).
- [ ] Rate limiting: configurable max requests per sandbox per minute.
- [ ] Unit tests for validation logic and happy path.

## Implementation Hints

The token endpoint is the most security-sensitive part of the system. Every validation step should fail closed.

```csharp
app.MapPost("/api/token", async (TokenRequest request, IBindingStore bindings, IWalletStore wallet, ILogger logger) =>
{
    // 1. Look up binding
    var binding = bindings.GetBinding(request.SandboxId);
    if (binding is null)
    {
        logger.LogWarning("Token request from unknown sandbox: {SandboxId}", request.SandboxId);
        return Results.StatusCode(403);
    }

    // 2. Check upstream is allowed
    var upstream = binding.AllowedUpstreams.FirstOrDefault(u => u.Host == request.UpstreamHost);
    if (upstream is null) { ... return 403; }

    // 3. Check scopes
    if (!request.Scopes.All(s => upstream.Scopes.Contains(s))) { ... return 403; }

    // 4. Get token from wallet
    var token = await wallet.GetTokenAsync(binding.UserId, request.UpstreamHost, request.Scopes);

    // 5. Return
    return Results.Ok(new TokenResponse { AccessToken = token.Value, TokenType = "bearer", ExpiresAt = token.ExpiresAt });
});
```

For this milestone, the wallet store can return a hardcoded/configurable test token. Real OAuth integration comes in Milestone 3.

## Files to Create

- `src/DonkeyWork.CodeSandbox.CredentialBroker/Endpoints/TokenEndpoints.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Services/ITokenService.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Services/TokenService.cs`

## Dependencies

- Task 1 (scaffold), Task 2 (binding store), Task 4 (wallet store)
