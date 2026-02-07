# Task 3: In-Proxy Token Cache

## Summary

Add an in-memory token cache to the proxy to avoid calling the Broker on every request.

## Acceptance Criteria

- [ ] Tokens cached in-memory keyed by `(upstream_host, scopes)` — there's only one sandbox per proxy, so sandbox_id is implicit.
- [ ] Cache respects `expires_at` from the Broker response.
- [ ] Proactive refresh: tokens are refreshed when 80% of TTL has elapsed.
- [ ] Cache never writes tokens to disk.
- [ ] Cache is bounded (max entries, eviction of expired entries).
- [ ] Unit tests for cache expiry and refresh logic.

## Implementation Hints

```csharp
public class TokenCache
{
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();

    public CachedToken? Get(string upstreamHost, IEnumerable<string> scopes)
    {
        var key = BuildKey(upstreamHost, scopes);
        if (_cache.TryGetValue(key, out var cached) && !cached.IsExpiredOrNearExpiry())
            return cached;
        return null;
    }

    public void Set(string upstreamHost, IEnumerable<string> scopes, TokenResult token)
    {
        var key = BuildKey(upstreamHost, scopes);
        _cache[key] = new CachedToken(token, DateTimeOffset.UtcNow);
    }
}
```

## Files to Create

- `src/DonkeyWork.CodeSandbox.AuthProxy/Services/TokenCache.cs`

## Files to Modify

- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/TlsMitmHandler.cs` — check cache before calling broker

## Dependencies

- Task 1 (broker client)
