# Task 4: Rate Limiting

## Summary

Add per-sandbox rate limiting to the Credential Broker to prevent runaway token requests.

## Acceptance Criteria

- [ ] Configurable rate limit: max token requests per sandbox per minute (default: 60).
- [ ] Requests exceeding the limit get `429 Too Many Requests`.
- [ ] Rate limit state is in-memory (resets on Broker restart — acceptable for this use case).
- [ ] Rate limit headers in responses: `X-RateLimit-Remaining`, `X-RateLimit-Reset`.
- [ ] Unit tests for rate limiting logic.

## Implementation Hints

Use a sliding window counter per `sandbox_id`. ASP.NET Core has built-in rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`) that can be configured with a fixed window or sliding window policy partitioned by `sandbox_id` from the request body.

## Files to Modify

- `src/DonkeyWork.CodeSandbox.CredentialBroker/Program.cs` — add rate limiting middleware
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Configuration/BrokerConfiguration.cs` — add rate limit config

## Dependencies

- Milestone 2 (Broker exists)
