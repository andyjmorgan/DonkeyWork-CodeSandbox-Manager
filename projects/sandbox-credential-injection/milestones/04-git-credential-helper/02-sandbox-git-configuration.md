# Task 2: Sandbox Git Configuration

## Summary

Configure Git in the executor image to use a credential helper that calls the sidecar endpoint.

## Acceptance Criteria

- [ ] A credential helper script installed in the executor image.
- [ ] Git configured globally to use it (`git config --global credential.helper`).
- [ ] The helper uses `curl` to call `http://127.0.0.1:8081/git-credential/$1`.
- [ ] Works with `git clone`, `git push`, `git fetch`, `git pull`.
- [ ] Backwards compatible — if the sidecar isn't running, git falls back to normal behavior (prompts or fails, but doesn't break).

## Implementation Hints

### Credential helper script

Create `/usr/local/bin/git-credential-broker`:

```bash
#!/bin/bash
# Git credential helper that delegates to the auth proxy sidecar
# Usage: git config --global credential.helper broker

ACTION="$1"
INPUT=$(cat)

case "$ACTION" in
    get)
        RESPONSE=$(echo "$INPUT" | curl -sf -X POST http://127.0.0.1:8081/git-credential/get -d @- 2>/dev/null)
        if [ $? -eq 0 ] && [ -n "$RESPONSE" ]; then
            echo "$RESPONSE"
        fi
        ;;
    store|erase)
        # Acknowledge but don't act
        echo "$INPUT" | curl -sf -X POST "http://127.0.0.1:8081/git-credential/$ACTION" -d @- 2>/dev/null || true
        ;;
esac
```

### Dockerfile additions

```dockerfile
# Install git credential helper
COPY src/DonkeyWork.CodeSandbox.Server/git-credential-broker /usr/local/bin/git-credential-broker
RUN chmod +x /usr/local/bin/git-credential-broker

# Configure git to use it (system-wide)
RUN git config --system credential.helper broker
```

### Entrypoint update

The existing `entrypoint.sh` (from Milestone 1, Task 4) doesn't need changes — git config is baked into the image.

## Files to Create

- `src/DonkeyWork.CodeSandbox.Server/git-credential-broker` (shell script)

## Files to Modify

- `src/DonkeyWork.CodeSandbox.Server/Dockerfile` — install git, copy helper, configure

## Dependencies

- Task 1 (sidecar endpoint exists)
- Milestone 1, Task 4 (executor image updates established)
