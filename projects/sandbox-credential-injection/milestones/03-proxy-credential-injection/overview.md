# Milestone 3: Proxy Credential Injection

## Goal

Wire the auth proxy to the Credential Broker so that requests to allowlisted domains automatically get `Authorization` headers injected with short-lived tokens. This is where the architecture becomes functional — after this milestone, `curl https://graph.microsoft.com/v1.0/me` from inside a sandbox returns real data.

## Success Criteria

- Proxy calls the Credential Broker to obtain tokens before forwarding to upstream.
- `Authorization: Bearer <token>` is injected into requests for allowlisted domains.
- Requests to non-allowlisted domains remain blocked.
- Token caching in the proxy avoids redundant Broker calls within TTL.
- Failed token acquisition results in a clear error returned to the sandbox (not a hang or cryptic failure).
- End-to-end test: sandbox `curl` to Graph API returns user data.

## Dependencies

- Milestone 1 (proxy forwards traffic)
- Milestone 2 (Broker issues tokens)

## Tasks

| # | Task | Description |
|---|------|-------------|
| 1 | [Broker Client in Proxy](./01-broker-client-in-proxy.md) | HTTP client in the proxy that calls the Broker's `/api/token` endpoint |
| 2 | [Header Injection](./02-header-injection.md) | Inject `Authorization` header into the MITM'd HTTP request before forwarding upstream |
| 3 | [In-Proxy Token Cache](./03-in-proxy-token-cache.md) | In-memory cache keyed by (sandbox_id, upstream, scopes) with TTL-based expiry |
| 4 | [Error Handling](./04-error-handling.md) | Clear error responses when Broker is unavailable or returns 403 |
| 5 | [End-to-End Test](./05-end-to-end-test.md) | Full flow test with Broker, proxy, and sandbox — mock or real upstream |
