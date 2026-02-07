# Task 4: Wallet Store Abstraction

## Summary

Define the `IWalletStore` interface and provide an in-memory implementation. This abstraction allows swapping in production backends (Azure Key Vault, HashiCorp Vault, etc.) later without changing the Broker logic.

## Acceptance Criteria

- [ ] `IWalletStore` interface defined with methods:
  - `Task<StoredToken?> GetTokenAsync(string userId, string upstreamHost, IEnumerable<string> scopes)`
  - `Task StoreTokenAsync(string userId, string upstreamHost, StoredToken token)`
  - `Task RemoveTokensAsync(string userId)` (for user offboarding)
- [ ] `StoredToken` model: `{ Value, TokenType, ExpiresAt, Scopes[] }`
- [ ] `InMemoryWalletStore` implementation using `ConcurrentDictionary`.
- [ ] For development/testing: pre-populated tokens can be configured via `appsettings.json`.
- [ ] Unit tests for store operations.

## Implementation Hints

Key design: the wallet stores tokens per `(userId, upstreamHost)` composite key.

```csharp
public interface IWalletStore
{
    Task<StoredToken?> GetTokenAsync(string userId, string upstreamHost, IEnumerable<string> scopes);
    Task StoreTokenAsync(string userId, string upstreamHost, StoredToken token);
    Task RemoveTokensAsync(string userId);
}
```

The in-memory implementation is intentionally simple. Production implementations would:
- Encrypt tokens at rest
- Handle OAuth refresh flows
- Integrate with identity providers

## Files to Create

- `src/DonkeyWork.CodeSandbox.CredentialBroker/Services/IWalletStore.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Services/InMemoryWalletStore.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Models/StoredToken.cs`

## Dependencies

- Task 1 (scaffold)
