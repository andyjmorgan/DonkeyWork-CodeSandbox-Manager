# Research: Sandbox Credential Injection

This document consolidates the research and analysis performed before implementation.

## Source Documents

- [Architecture Proposal](../../docs/sandbox-credential-injection-architecture.md) — the original sidecar proxy architecture spec.
- [Architecture Evaluation](../../docs/sandbox-credential-injection-evaluation.md) — detailed evaluation against the existing codebase, cross-validated by Claude and ChatGPT.

## Key Decisions Made

### 1. C# forward proxy, not Envoy

**Decision**: Build a purpose-built C# forward proxy using Kestrel/SslStream.

**Rationale**: Envoy requires ext_proc/ext_authz/WASM/Lua for custom token injection logic — none of which are C#. A C# proxy keeps the entire stack in one language, simplifies debugging, and reduces operational surface. The requirement is narrow (forward proxy + MITM for a small allowlist + header injection).

### 2. TLS MITM for HTTP APIs (Pattern B)

**Decision**: Commit to TLS MITM for allowlisted API domains. Pattern A (CONNECT passthrough) cannot inject Authorization headers.

**Details**:
- Proxy terminates TLS from sandbox using an internal CA.
- Proxy inspects/modifies HTTP headers (inject `Authorization: Bearer <token>`).
- Proxy re-establishes TLS to the real upstream.
- Only allowlisted domains are MITM'd. Everything else is blocked.
- Internal CA private key lives only in the proxy sidecar container, never in the workload.

### 3. Git credential helper, not MITM for Git

**Decision**: Use Git's native credential helper protocol for Git authentication.

**Rationale**: Git's HTTPS auth involves redirects and provider-specific challenge flows that are fragile to MITM. The credential helper protocol is stable, provider-agnostic, and designed for exactly this scenario. The sidecar exposes a local HTTP endpoint that responds to credential helper requests.

### 4. No Token Agent — proxy calls Broker directly

**Decision**: Skip the optional Token Agent sidecar. The proxy calls the Credential Broker directly.

**Rationale**: The Broker already caches tokens server-side. Adding a local Token Agent duplicates caching, adds process management overhead, and creates another attack surface. If latency becomes an issue later, add in-memory caching to the proxy process itself.

### 5. Warm pool binding at allocation time

**Decision**: The Manager registers `(sandbox_id, user_id, allowed_scopes)` with the Credential Broker when allocating a warm pod. The sidecar operates in blocked mode until binding occurs.

**Rationale**: Warm pods are pre-created without user context. The Broker needs user mapping to issue tokens. The Manager already patches pod metadata at allocation — extending it to register with the Broker is the simplest integration point.

## Existing Codebase Integration Points

| Component | File | What Changes |
|-----------|------|-------------|
| Pod spec construction | `KataContainerService.BuildPodSpec()` | Add sidecar container, proxy env vars, cert mounts |
| Warm pool pod spec | `PoolManager.BuildWarmPodSpec()` | Same sidecar additions |
| Pool allocation | `PoolManager.AllocateWarmSandboxAsync()` | Register user binding with Broker |
| Container cleanup | `ContainerCleanupService` | Deregister binding from Broker |
| Executor Dockerfile | `src/DonkeyWork.CodeSandbox.Server/Dockerfile` | Install internal CA into trust store |
| Configuration | `KataContainerManager.cs` | Add sidecar resource config, proxy settings |
| Container creation request | `CreateContainerRequest` | Optionally carry credential policy info |

## Security Model

```
Sandbox workload:
  - Has: proxy env vars, internal CA cert (public only)
  - Does NOT have: tokens, secrets, refresh tokens, CA private key
  - Network: all egress through proxy (enforced by iptables + proxy env vars)

Auth Proxy sidecar:
  - Has: sandbox identity credential, internal CA cert + private key
  - Does NOT have: user tokens (fetches on demand from Broker)
  - Authenticates to Broker per-request

Credential Broker:
  - Has: sandbox-to-user mappings, access to Wallet Store
  - Validates sandbox identity before issuing tokens
  - Returns short-lived, minimal-scope tokens

Wallet Store:
  - Has: refresh tokens, PATs, OAuth client credentials
  - Only accessible by Credential Broker
  - Never reachable from sandbox or sidecar
```
