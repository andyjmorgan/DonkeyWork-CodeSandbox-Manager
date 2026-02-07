# Task 1: Credential Helper Endpoint

## Summary

Add an HTTP endpoint to the auth proxy sidecar's admin port (8081) that responds to Git credential helper requests.

## Context

Git's credential helper protocol sends requests on stdin and reads responses on stdout. A custom helper script in the sandbox translates this to HTTP calls to the sidecar:

```
# Git sends to helper stdin:
protocol=https
host=github.com

# Helper calls sidecar:
POST http://127.0.0.1:8081/git-credential/get
Body: protocol=https\nhost=github.com

# Sidecar returns:
protocol=https
host=github.com
username=x-access-token
password=<short-lived-token>
```

## Acceptance Criteria

- [ ] `POST /git-credential/get` endpoint on the sidecar's admin port (8081).
- [ ] Parses Git credential helper input format (key=value pairs, newline delimited).
- [ ] Extracts `host` from the request.
- [ ] Calls the Credential Broker for a token for that host.
- [ ] Returns Git credential helper output format with `username` and `password`.
- [ ] `POST /git-credential/store` — no-op (acknowledge but don't store).
- [ ] `POST /git-credential/erase` — no-op (acknowledge).
- [ ] Returns 403 if the host is not in the allowed upstreams.
- [ ] Unit tests for parsing and response formatting.

## Implementation Hints

### Username conventions by provider

| Provider | Username | Password |
|----------|----------|----------|
| GitHub | `x-access-token` | PAT or OAuth token |
| Azure DevOps | `x-access-token` | PAT |
| GitLab | `oauth2` | OAuth token |

The Broker can return the appropriate username along with the token, or the proxy can map based on host.

### Response format

```
protocol=https
host=github.com
username=x-access-token
password=ghp_xxxxxxxxxxxx

```

Note the trailing blank line — it signals end of response in the credential helper protocol.

## Files to Create

- `src/DonkeyWork.CodeSandbox.AuthProxy/Endpoints/GitCredentialEndpoint.cs`

## Files to Modify

- `src/DonkeyWork.CodeSandbox.AuthProxy/Program.cs` or health endpoint setup — register the new endpoint

## Dependencies

- Milestone 2 (Broker token endpoint)
- Milestone 1, Task 1 (proxy project exists with admin port)
