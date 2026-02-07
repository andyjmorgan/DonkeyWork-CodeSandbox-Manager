# Task 5: End-to-End Test

## Summary

Full flow test: sandbox -> proxy -> broker -> upstream, with actual token injection.

## Acceptance Criteria

- [ ] Test with a mock upstream that validates the `Authorization` header is present and correct.
- [ ] Test with Broker pre-configured with a test binding and test token.
- [ ] `curl` from sandbox receives the upstream response (not a proxy error).
- [ ] Proxy logs show token acquisition and injection.
- [ ] Negative test: sandbox with no binding gets a clear 403.
- [ ] Can run via docker-compose or test harness.

## Implementation Hints

Use a mock HTTP server (e.g., WireMock.Net or a simple Kestrel app) as the "upstream" that:
1. Checks for `Authorization: Bearer test-token-123` header.
2. Returns 200 if present, 401 if missing.

Configure the Broker with a pre-populated binding and a test token in the in-memory wallet store.

## Dependencies

- All previous Milestone 3 tasks.
- Milestone 2 (Broker running).
- Milestone 1 (proxy and pod spec working).
