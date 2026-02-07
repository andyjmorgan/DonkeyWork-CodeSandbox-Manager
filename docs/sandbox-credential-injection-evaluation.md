# Architecture Evaluation: Sandbox Credential Injection

This document evaluates the proposed [Sandbox Credential Injection Architecture](./sandbox-credential-injection-architecture.md) against the existing DonkeyWork CodeSandbox Manager codebase. It identifies strengths, concerns, gaps, and concrete recommendations.

---

## Overall Assessment

The architecture is sound in its security model and separation of concerns. The core principle — sandbox never holds credentials, auth is injected transparently at the network layer — is the right approach for this threat model. However, several aspects need refinement when mapped against the existing codebase, particularly around the warm pool lifecycle, Envoy complexity, and the TLS MITM decision.

---

## Strengths

### 1. Clean security boundary
The sandbox workload having zero access to credential material is the strongest aspect. Combined with Kata VM-level isolation, this gives defense in depth: even if an attacker escapes the container, they land in a VM with no credentials and locked-down egress.

### 2. Natural fit with existing pod model
The codebase already builds pod specs programmatically in `KataContainerService.BuildPodSpec()` (`src/DonkeyWork.CodeSandbox.Manager/Services/Container/KataContainerService.cs:476`). Adding a sidecar container is a straightforward extension — add a second `V1Container` to the `Containers` list, mount shared ConfigMaps/Secrets, and set environment variables on the workload container.

### 3. Credential Broker as a C# service
The team already has a mature C# control plane (the Manager service). Building the Credential Broker as another C# service in this solution is natural. It can share the same solution structure, NuGet dependencies, logging (Serilog), and deployment pipeline.

### 4. Audit trail
The architecture correctly calls out audit logging on the broker. This is critical for compliance and incident response, and fits well with the existing structured logging via Serilog.

---

## Concerns and Recommendations

### 1. Envoy may be over-engineered for this use case

**Concern**: Envoy is a powerful general-purpose proxy, but the actual requirement here is narrow: forward-proxy a small allowlist of HTTPS domains with header injection. Envoy's forward proxy + TLS MITM + external token acquisition flow requires either:
- **ext_authz filter**: Can inject headers on auth decisions, but is designed for allow/deny, not arbitrary token fetching with caching logic.
- **ext_proc filter**: Full request/response manipulation, but complex to configure and debug.
- **WASM plugin**: Custom logic in the data path, requires non-C# languages (Rust, Go, AssemblyScript).
- **Lua filter**: Simpler but limited, and still not C#.

None of these keep custom logic in C#. You'd end up maintaining Envoy YAML/JSON config + a separate filter language + the C# Token Agent + the C# Credential Broker. That's a lot of moving parts.

**Recommendation**: Consider a purpose-built C# forward proxy instead of Envoy. The requirements are:
- Listen for HTTP CONNECT requests (forward proxy).
- Match SNI/Host against an allowlist.
- For matched domains: terminate TLS, inject `Authorization` header, re-establish TLS upstream.
- For unmatched domains: reject.
- Call the Credential Broker for tokens.

This can be built in C# with `System.Net.Security.SslStream` and `YARP` (Microsoft's reverse proxy library, already .NET native) or even `Kestrel` directly. It keeps the entire stack in one language, simplifies debugging, and reduces the operational surface. The proxy binary can be small (~20-30MB container image).

If Envoy is preferred for organizational reasons (existing expertise, service mesh integration), then use `ext_authz` with a C# gRPC service as the external authorizer — this is the simplest Envoy integration path that keeps logic in C#.

### 2. TLS MITM is unavoidable — commit to Pattern B

**Concern**: The architecture presents Pattern A and Pattern B as options, but for the stated requirements (inject `Authorization: Bearer` headers into `curl` and SDK calls to Graph/GitHub), Pattern B (MITM for selected domains) is the only viable path. Pattern A cannot inject HTTP headers into an encrypted stream.

**Recommendation**: Drop Pattern A from the design and commit to Pattern B for allowlisted domains. Be explicit that:
- The sandbox trust store will include an internal CA.
- The proxy will dynamically generate certificates for allowlisted domains (or use a single proxy endpoint cert).
- Only allowlisted domains are MITM'd; everything else is blocked.
- The internal CA private key lives only in the proxy sidecar (never in the sandbox workload).

This is a well-understood enterprise pattern (Zscaler, corporate HTTPS inspection, Istio sidecar). Framing it clearly avoids ambiguity during implementation.

### 3. Git: use a credential helper, not MITM

**Concern**: The architecture acknowledges that Git-over-HTTPS is "tricky" with MITM, then leans toward proxy interception anyway. In practice, Git's HTTPS auth flow involves multiple sequential requests with different auth challenges across redirects, and different Git hosting providers (GitHub, Azure DevOps, GitLab) handle auth differently (Basic, Bearer, custom). MITM-based header injection for Git is fragile.

**Recommendation**: Use Git's native credential helper protocol for Git operations. This is a well-defined, stable protocol:

```
# In sandbox, configure:
git config --global credential.helper '!f() { curl -s http://127.0.0.1:<agent_port>/git-credential/$1; }; f'
```

The Token Agent (or a dedicated endpoint on the sidecar) responds to `get` requests with:
```
protocol=https
host=github.com
username=x-access-token
password=<short-lived-token>
```

This approach:
- Works with every Git hosting provider without provider-specific MITM logic.
- Uses Git's built-in protocol, so `git clone`, `git push`, `git fetch` all work transparently.
- Avoids MITM complexity for Git traffic entirely.
- Is how GitHub CLI, Azure DevOps, and most credential managers already work.

Reserve MITM proxy injection for HTTP API calls (Graph, REST APIs) where `curl`/SDKs expect standard `Authorization` headers.

### 4. Warm pool lifecycle gap

**Concern**: This is the most significant gap in the architecture. The existing warm pool (`src/DonkeyWork.CodeSandbox.Manager/Services/Pool/`) pre-creates pods without user context. Pods sit in a "warm" state with `pool-status=warm` labels until allocated. At allocation time, the pod is patched with `pool-status=allocated` and activity timestamps are set.

The Credential Broker needs `sandbox_id -> user_id` mapping to issue tokens. But warm pods have no user binding. The architecture doesn't address when and how this binding is established.

**Recommendation**: The binding must happen at allocation time. The flow should be:

1. **Pool creates warm pod**: Sidecar starts but operates in "no-auth" mode (blocks all protected-domain requests, or returns 503).
2. **User requests a sandbox**: Manager allocates a warm pod and patches it with user context (new label/annotation: `user-id=<id>`).
3. **Manager notifies sidecar of binding**: Either:
   - (a) Sidecar polls an endpoint or watches a file/ConfigMap for its user binding, or
   - (b) Manager calls a sidecar admin endpoint (`POST /admin/bind` with `{ user_id, allowed_scopes }`) at allocation time, or
   - (c) The Credential Broker receives the binding from the Manager and the sidecar simply presents its `sandbox_id` — the broker resolves the user. This is simplest.

Option (c) is recommended: the Manager's allocation logic already patches pod metadata. Extend it to also register the `(sandbox_id, user_id, allowed_scopes)` mapping with the Credential Broker. The sidecar doesn't need to know the user — it just presents its sandbox identity, and the broker looks up the rest.

This also means: on sandbox teardown (which already exists in `ContainerCleanupService`), the Manager must also revoke/delete the mapping in the Credential Broker.

### 5. The Token Agent may be unnecessary

**Concern**: The architecture proposes an optional C# Token Agent as a local sidecar that caches tokens and proxies requests to the Credential Broker. With the Credential Broker already doing server-side caching, the Token Agent adds a component that:
- Duplicates caching logic.
- Adds another process to monitor and restart.
- Creates another attack surface (local HTTP endpoint in the pod).

**Recommendation**: For the initial implementation, skip the Token Agent. Have the proxy (whether Envoy or custom C#) call the Credential Broker directly on each request that needs a token. The Broker handles caching. This simplifies the architecture to three components instead of four.

If latency from Broker calls becomes a problem (unlikely given these are already making external API calls that dwarf the Broker RTT), add a simple in-memory cache in the proxy process itself — no separate service needed.

### 6. Resource overhead accounting

**Concern**: Current pod resource limits are 512Mi RAM / 1000m CPU (defaults in `KataContainerManager.cs`). An Envoy sidecar typically consumes 50-128Mi RAM and 100-250m CPU. A custom C# proxy would be similar. With a warm pool of 10 pods and max of 50, this adds up.

**Recommendation**:
- Account for sidecar resources separately from workload resources in the pod spec. The existing `BuildPodSpec` sets resources on the workload container only — the sidecar needs its own resource block.
- Consider adding sidecar resource configuration to `KataContainerManager`:
  ```csharp
  public ResourceConfig? SidecarResourceRequests { get; set; }
  public ResourceConfig? SidecarResourceLimits { get; set; }
  ```
- Default to ~64Mi/100m for the sidecar. This means each pod now requests ~192Mi/350m and limits at ~576Mi/1100m.

### 7. Network policy enforcement with Kata

**Concern**: The architecture specifies "default-deny egress from sandbox except Envoy proxy listener." In standard Kubernetes, this is done with `NetworkPolicy` resources. However, with Kata containers (`kata-qemu` runtime class), network policy enforcement depends on the CNI plugin running on the host — the policy is enforced at the pod network interface, not inside the VM. This should work correctly for pod-level egress policy, but verify that:
- The CNI plugin enforces policies on Kata pods (most do, since policy is at the veth pair level).
- Egress rules can target `127.0.0.1` / localhost within the pod (they typically can't — intra-pod traffic bypasses NetworkPolicy).

**Recommendation**: Since intra-pod traffic (workload -> sidecar on localhost) bypasses NetworkPolicy, the lockdown approach should be:
- **NetworkPolicy**: Block all egress from the pod except to the Credential Broker's ClusterIP and DNS.
- **Sidecar enforcement**: The proxy itself enforces the domain allowlist (it is the only egress path since the workload has `HTTPS_PROXY` set and no direct route out).
- **iptables in sandbox (defense in depth)**: If the sandbox image allows it, add iptables rules that drop all outbound traffic not destined for the sidecar port. This prevents the workload from bypassing the proxy by ignoring `HTTPS_PROXY`.

The iptables approach is the standard pattern used by Istio sidecars and is well-proven.

### 8. DNS resolution

**Concern**: Not addressed in the architecture. When the sandbox runs `curl https://graph.microsoft.com/...`, it needs to resolve `graph.microsoft.com`. With the proxy configured via `HTTPS_PROXY`, `curl` sends a `CONNECT graph.microsoft.com:443` to the proxy — the proxy resolves the DNS, not the sandbox. This is fine. But some tools or SDKs may resolve DNS before connecting to the proxy, which means the sandbox needs DNS access.

**Recommendation**: Allow DNS egress from the pod (port 53 to cluster DNS). This is standard and doesn't leak credentials. The proxy is still the enforcement point for actual connections.

### 9. Scope management per upstream

**Concern**: The architecture mentions "allowed resources/scopes" but doesn't detail how scopes are configured or what granularity is supported. For Graph API alone, there are hundreds of permission scopes. For Git, the scope model differs between providers.

**Recommendation**: Define a simple policy model to start:
```json
{
  "policies": [
    {
      "upstream": "graph.microsoft.com",
      "token_type": "bearer",
      "oauth_audience": "https://graph.microsoft.com",
      "scopes": ["User.Read", "Mail.Read"],
      "methods": ["GET"],
      "path_prefix": "/v1.0/"
    },
    {
      "upstream": "github.com",
      "token_type": "git_credential",
      "scopes": ["repo"],
      "methods": ["*"]
    }
  ]
}
```

Start narrow (few scopes, read-only where possible) and widen based on need. The Credential Broker should reject requests for scopes not in the policy even if the user's OAuth token has broader permissions.

---

## Proposed Simplified Architecture

Based on the above, here's a streamlined version:

```
┌─────────────────────────────────────────────────────────┐
│  Kata Pod                                               │
│                                                         │
│  ┌──────────────────────┐   ┌────────────────────────┐  │
│  │  Workload Container  │   │  Auth Proxy Sidecar    │  │
│  │                      │   │  (C# forward proxy)    │  │
│  │  - LLM agent         │──▶│                        │  │
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
                              │  (C# service, separate ns) │◀─┘
                              │                            │
                              │  - Validates sandbox ID    │
                              │  - Resolves user binding   │
                              │  - OAuth token management  │
                              │  - Token caching           │
                              │  - Audit logging           │
                              │  - Rate limiting           │
                              │                            │
                              │        │                   │
                              └────────┼───────────────────┘
                                       │
                              ┌────────▼───────────────────┐
                              │  Wallet Store              │
                              │  (Encrypted secret store)  │
                              │                            │
                              │  - Refresh tokens          │
                              │  - PATs                    │
                              │  - OAuth client creds      │
                              └────────────────────────────┘
```

Key differences from the original:
1. **C# forward proxy** instead of Envoy (simpler, single-language stack).
2. **No separate Token Agent** — proxy calls Broker directly.
3. **Git credential helper** for Git auth (no MITM for Git traffic).
4. **MITM only for HTTP API domains** (Graph, etc.).
5. **iptables in sandbox** for defense-in-depth proxy enforcement.

---

## Integration Points with Existing Codebase

### Pod spec construction
`KataContainerService.BuildPodSpec()` at line 476 needs to:
- Add sidecar container to `V1PodSpec.Containers`.
- Add `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` env vars to the workload container.
- Mount ConfigMap with proxy config (allowlist, broker endpoint).
- Mount Secret with internal CA cert (for workload trust store) and sandbox identity credential (for sidecar).
- Add init container or startup script to configure iptables and git credential helper in the workload.

### Container creation request
`CreateContainerRequest` may need to carry credential policy info:
- Which upstream services this sandbox should have access to.
- Which user to bind (or this comes from the allocation flow).

### Pool allocation
`PoolManager.AllocatePodAsync()` needs to:
- Register sandbox-to-user binding with the Credential Broker.
- Optionally signal the sidecar that it's now active.

### Container cleanup
`ContainerCleanupService` needs to:
- Deregister sandbox-to-user binding from the Credential Broker.
- Trigger token revocation if supported by upstream providers.

### New solution projects
- `DonkeyWork.CodeSandbox.CredentialBroker` — the Broker service.
- `DonkeyWork.CodeSandbox.AuthProxy` — the sidecar forward proxy.
- `DonkeyWork.CodeSandbox.CredentialBroker.Contracts` — shared request/response models.

---

## Implementation Priority

1. **Credential Broker** — the core service. Get the `sandbox_id -> user_id -> token` flow working first with a test harness.
2. **Auth Proxy sidecar** — the forward proxy with MITM for a single domain (e.g., `graph.microsoft.com`). Validate the end-to-end flow.
3. **Pod spec integration** — wire the sidecar into `BuildPodSpec` and test with the warm pool.
4. **Git credential helper** — add Git auth support.
5. **Policy configuration** — formalize the allowlist/scopes config model.
6. **Hardening** — iptables, NetworkPolicy, audit logging, rate limiting, cert rotation.

---

## Open Questions

1. **Wallet Store technology**: What backs the wallet store? Azure Key Vault? HashiCorp Vault? Encrypted database? This affects the Broker's integration layer.
2. **OAuth providers**: Which identity providers are in scope? Entra ID (for Graph)? GitHub OAuth Apps? GitHub Apps? Each has different token acquisition flows.
3. **Multi-tenancy**: Can multiple users share a Credential Broker instance, or is it one-per-cluster / one-per-namespace?
4. **Token revocation**: When a sandbox is torn down, should upstream tokens be actively revoked, or just allowed to expire (given short TTLs)?
5. **Proxy bypass risk**: If the workload container runs as root (current Kata setup), it could modify iptables to bypass the proxy. Should the workload container be non-root with dropped capabilities? (The executor Dockerfile already uses UID 10000, but verify Kata doesn't override this.)
