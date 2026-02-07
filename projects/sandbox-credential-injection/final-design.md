# Final Design: Sandbox Credential Injection

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  Kata Pod                                               │
│                                                         │
│  ┌──────────────────────┐   ┌────────────────────────┐  │
│  │  Workload Container  │   │  Auth Proxy Sidecar    │  │
│  │                      │   │  (C# forward proxy)    │  │
│  │  - LLM agent         │──>│                        │  │
│  │  - curl, git, SDKs   │   │  - HTTPS CONNECT proxy │  │
│  │  - HTTPS_PROXY set   │   │  - Domain allowlist    │  │
│  │  - Internal CA trust │   │  - TLS MITM (selected) │  │
│  │  - Git cred helper   │   │  - Header injection    │  │
│  │                      │   │  - Sandbox identity    │──────┐
│  └──────────────────────┘   └────────────────────────┘  │   │
│         iptables: only sidecar port allowed              │   │
└─────────────────────────────────────────────────────────┘   │
                                                              │
                              ┌────────────────────────────┐  │
                              │  Credential Broker         │  │
                              │  (C# service, separate ns) │<─┘
                              │                            │
                              │  - Validates sandbox ID    │
                              │  - Resolves user binding   │
                              │  - OAuth token management  │
                              │  - Token caching           │
                              │  - Audit logging           │
                              │  - Rate limiting           │
                              │                            │
                              │        |                   │
                              └────────┼───────────────────┘
                                       │
                              ┌────────v───────────────────┐
                              │  Wallet Store              │
                              │  (Encrypted secret store)  │
                              │                            │
                              │  - Refresh tokens          │
                              │  - PATs                    │
                              │  - OAuth client creds      │
                              └────────────────────────────┘
```

## Components

### 1. Auth Proxy Sidecar (`DonkeyWork.CodeSandbox.AuthProxy`)

A C# forward proxy built on Kestrel.

**Modes of operation per request**:
- **MITM mode** (allowlisted API domains): Terminate TLS from sandbox, inspect HTTP, inject `Authorization` header, re-establish TLS to upstream.
- **Block mode** (everything else): Reject with 403.

**Ports**:
- `8080` — forward proxy listener (workload's `HTTPS_PROXY` target)
- `8081` — admin/health + git credential helper endpoint

**Configuration** (via environment variables or mounted config):
- Allowlist of domains eligible for MITM + injection
- Credential Broker endpoint URL
- Sandbox identity credential (mounted secret or projected SA token)
- Internal CA certificate + private key (mounted secret)

### 2. Credential Broker (`DonkeyWork.CodeSandbox.CredentialBroker`)

A C# service running outside the sandbox boundary.

**Endpoints**:
- `POST /api/token` — request a token for an upstream
  - Input: `{ sandbox_id, upstream_host, scopes[] }`
  - Output: `{ access_token, token_type, expires_at }`
  - Validates sandbox identity, resolves user binding, checks allowed scopes
- `POST /api/bindings` — register sandbox-to-user binding
  - Input: `{ sandbox_id, user_id, allowed_upstreams[] }`
  - Called by the Manager at allocation time
- `DELETE /api/bindings/{sandbox_id}` — deregister binding
  - Called by the Manager at teardown

### 3. Wallet Store

Abstracted behind an interface. Initial implementation can be in-memory or a simple encrypted store. Production would use Azure Key Vault, HashiCorp Vault, or similar.

### 4. Changes to Existing Components

**Executor Dockerfile**: Install internal CA into `/usr/local/share/ca-certificates/` and run `update-ca-certificates`.

**`KataContainerManager` config**: Add sidecar image, sidecar resource limits, proxy port, broker endpoint.

**`BuildPodSpec` / `BuildWarmPodSpec`**: Add sidecar container, mount CA cert, set `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` env vars on workload.

**`AllocateWarmSandboxAsync`**: Call Broker to register user binding.

**Container cleanup**: Call Broker to deregister binding.

## Request Flows

### curl to Graph API

```
1. Sandbox: curl https://graph.microsoft.com/v1.0/me
2. curl reads HTTPS_PROXY -> sends CONNECT graph.microsoft.com:443 to proxy
3. Proxy matches graph.microsoft.com against allowlist -> MITM mode
4. Proxy accepts CONNECT, establishes TLS with sandbox (using internal CA cert for graph.microsoft.com)
5. Sandbox sends HTTP request over the TLS tunnel
6. Proxy reads the HTTP request, calls Credential Broker:
   POST /api/token { sandbox_id, upstream: "graph.microsoft.com", scopes: ["https://graph.microsoft.com/.default"] }
7. Broker validates sandbox, resolves user, returns access_token
8. Proxy injects: Authorization: Bearer <token>
9. Proxy opens real TLS connection to graph.microsoft.com, forwards request
10. Response flows back: upstream -> proxy -> sandbox
```

### git clone

```
1. Sandbox: git clone https://github.com/org/repo.git
2. Git invokes credential helper: curl http://127.0.0.1:8081/git-credential/get
   with stdin: protocol=https\nhost=github.com
3. Sidecar calls Credential Broker for github.com token
4. Returns: protocol=https\nhost=github.com\nusername=x-access-token\npassword=<token>
5. Git uses the credential for HTTPS auth (through proxy in passthrough mode)
```

## Milestone Plan

See [milestones/](./milestones/) for the breakdown:

1. **[Proxy Foundation](./milestones/01-proxy-foundation/overview.md)** — Build the proxy, CA cert, sidecar image, pod integration. No credential injection yet.
2. **[Credential Broker](./milestones/02-credential-broker/overview.md)** — Broker service, user binding, token endpoint.
3. **[Proxy Credential Injection](./milestones/03-proxy-credential-injection/overview.md)** — Wire proxy to Broker, inject headers, domain policy.
4. **[Git Credential Helper](./milestones/04-git-credential-helper/overview.md)** — Git auth via credential helper protocol.
5. **[Hardening](./milestones/05-hardening/overview.md)** — iptables, NetworkPolicy, audit logging, cert rotation.
