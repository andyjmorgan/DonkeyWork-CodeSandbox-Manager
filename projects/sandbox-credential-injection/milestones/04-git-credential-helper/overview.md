# Milestone 4: Git Credential Helper

## Goal

Enable `git clone`, `git push`, and `git fetch` from inside the sandbox using Git's native credential helper protocol. No TLS MITM needed for Git traffic — the credential helper talks to the sidecar directly.

## Success Criteria

- `git clone https://github.com/org/private-repo.git` works from inside the sandbox with no manual auth configuration.
- Tokens are obtained from the Credential Broker via the sidecar.
- Works with GitHub, Azure DevOps, and GitLab (provider-agnostic).
- Git traffic flows through the proxy in CONNECT passthrough mode (no MITM).

## Dependencies

- Milestone 2 (Broker issues tokens)
- Milestone 1 (proxy exists, sandbox image can be configured)

## Tasks

| # | Task | Description |
|---|------|-------------|
| 1 | [Credential Helper Endpoint](./01-credential-helper-endpoint.md) | HTTP endpoint on the sidecar that responds to Git credential helper protocol |
| 2 | [Sandbox Git Configuration](./02-sandbox-git-configuration.md) | Configure git in the executor image to use the credential helper |
| 3 | [Proxy Passthrough for Git](./03-proxy-passthrough-for-git.md) | Ensure Git HTTPS traffic goes through proxy in CONNECT (passthrough) mode, not MITM |
| 4 | [Integration Test](./04-integration-test.md) | Test git clone through the full stack |
