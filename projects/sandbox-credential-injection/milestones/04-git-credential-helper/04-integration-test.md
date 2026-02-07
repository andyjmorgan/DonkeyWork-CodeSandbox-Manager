# Task 4: Integration Test

## Summary

Test `git clone` of a private repository through the full stack.

## Acceptance Criteria

- [ ] `git clone https://github.com/org/private-repo.git` succeeds from inside the sandbox.
- [ ] Credential helper logs show the token request.
- [ ] Broker logs show the binding lookup and token issuance.
- [ ] Git traffic flows through proxy in passthrough mode (proxy logs confirm CONNECT tunnel).
- [ ] Negative test: `git clone` for an unauthorized repo/host fails with a clear error.

## Implementation Hints

For testing without a real private repo, use a mock Git server (e.g., `gitea` in docker-compose) with a known test token configured in the Broker's wallet store.

Alternatively, use a real GitHub PAT stored in the Broker for a known test repo — but be careful not to commit real tokens.

## Dependencies

- Tasks 1-3 of this milestone.
- Milestone 2 (Broker).
