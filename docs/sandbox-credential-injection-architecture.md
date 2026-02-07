# Sandbox Credential Injection Architecture

## Goal

Allow an LLM-driven workload running inside a Kata-isolated sandbox to use standard CLI tools (curl, git, SDKs, etc.) against approved external services (e.g., Microsoft Graph, Git providers) without ever placing user credentials/tokens inside the sandbox. Prevent credential exfiltration while still enabling "normal" command-line flows.

---

## High-level pattern

Egress credential injection via sidecar proxy:
- Sandbox container never receives OAuth client secrets, refresh tokens, PATs, or long-lived user tokens.
- All outbound traffic from the sandbox routes through an Envoy sidecar.
- Envoy selectively intercepts requests to approved domains and:
  1. obtains a short-lived scoped access token from a Credential Broker (outside sandbox),
  2. injects it into the request (e.g., `Authorization: Bearer ...`, or Git auth),
  3. forwards the request upstream.
- Traffic to non-approved destinations is either blocked (preferred) or passed through without auth injection, depending on policy.

---

## Components

### 1) Sandbox workload (Kata + "exec manager")
- Runs user/LLM commands (bash, curl, git, etc.).
- Has no direct network route to credential store.
- Has outbound network configured to go through the proxy sidecar:
  - `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY` (for cluster-local / metadata / health endpoints).
  - Trust store includes an internal CA (or specific proxy cert chain) so it can establish TLS to the proxy when doing HTTPS proxying.

### 2) Envoy sidecar container (same pod/task as sandbox)
- Runs Envoy configured as:
  - Forward proxy for HTTP and HTTPS (CONNECT).
  - Selective credential injection for specific upstream domains.
  - Egress policy enforcement (allowlist domains, block the rest).
- Holds no user secrets.
- Holds a sandbox identity credential (shared secret, mTLS cert, or projected service account token) used only to authenticate to the Credential Broker.

### 3) Credential Broker (C# service)
- Runs outside the sandbox boundary (host side / control plane / separate trusted namespace).
- Responsible for:
  - mapping `sandbox_id` -> `user_id` -> allowed resources/scopes,
  - performing OAuth flows / refresh token management against identity providers,
  - issuing ephemeral access tokens appropriate for the target upstream and request context,
  - returning tokens to Envoy (or to a small local helper) over a mutually authenticated channel.
- Token caching:
  - May cache refreshed upstream tokens server-side.
  - Returns short-lived tokens to Envoy (or token metadata: expiry, scopes, audience).
- Enforces least privilege and audit logging.

### 4) User Wallet Store (LWAT store)
- Stores long-lived / refreshable secrets (refresh tokens, PATs, etc.).
- Never reachable from the sandbox or sidecar directly.
- Only the Credential Broker accesses it.

---

## Identity and trust model

### Sandbox <-> Envoy (within pod)
- Sandbox is configured to send traffic via Envoy (proxy env vars or iptables redirection).
- For HTTPS proxying, sandbox must trust the proxy's certificate chain:
  - Recommended: Internal CA used to mint a cert for Envoy ("proxy cert"), and install the CA into sandbox trust store.
  - Avoid "wildcard public certs" if possible; internal CA is the established pattern in service meshes and controlled environments.

### Envoy <-> Credential Broker
- Must be authenticated strongly.
- Options (choose one):
  1. **mTLS**: Envoy presents a client cert minted per-sandbox/pod; broker verifies and maps identity.
  2. **Shared secret**: sidecar gets a per-sandbox secret projected at runtime; used to sign requests to broker.
  3. **Kubernetes service account token** (if applicable): broker validates JWT + audience.
- Broker returns tokens only for:
  - approved sandbox identity,
  - approved user context,
  - approved upstream + scopes.

---

## Egress routing / proxying details

### Basic routing
- Sandbox tools do standard calls:
  - `curl https://graph.microsoft.com/v1.0/me`
  - `git clone https://github.com/org/repo.git`
- Sandbox has:
  - `HTTPS_PROXY=http://127.0.0.1:<envoy_port>` (or the sidecar listener address)
  - `HTTP_PROXY=http://127.0.0.1:<envoy_port>`
  - `NO_PROXY=localhost,127.0.0.1,<cluster-domains>,<credential-broker-address-if-needed>` (prefer broker not reachable anyway)

### Domain-based interception

Envoy config enforces:
- Allowlist domains like:
  - `graph.microsoft.com`
  - `api.github.com`, `github.com`
  - `dev.azure.com` / `visualstudio.com` (if needed)
- For those domains:
  - run "token acquisition + injection" step
- For all others:
  - deny by default (preferred) OR pass through without injection.

---

## "SSL MITM" reality check and recommended approach

There are two distinct patterns; pick based on how much you truly need to inspect/modify.

### Pattern A (recommended): No MITM; use standard HTTPS proxy CONNECT
- Sandbox talks to Envoy as an HTTPS proxy (CONNECT).
- Envoy forwards encrypted TLS to upstream.
- Credential injection is done without decrypting payload by using one of:
  - Per-upstream auth mechanisms that don't require header rewriting (limited), or
  - Request-level controls outside MITM (e.g., broker returns pre-signed URLs), or
  - Accept that for true Authorization header injection, you generally need visibility into HTTP headers.

In practice, if you must inject Authorization headers for arbitrary curl/SDK traffic, you usually need Pattern B.

### Pattern B: Explicit MITM for selected domains
- Envoy (or a dedicated MITM proxy) terminates TLS from the sandbox for selected SNI/domains, inspects HTTP, injects headers, then re-initiates TLS to upstream.
- Requires:
  - an internal CA trusted by the sandbox,
  - dynamic per-domain cert generation or a cert valid for the proxy endpoint (common in MITM proxies),
  - careful scoping: MITM only for the small allowlist that truly needs token injection.
- This is an established enterprise pattern (forward proxy with TLS inspection), but treat it as high-risk and heavily audited.

**Implementation note**: Envoy can do forward proxying and TLS termination, but "full featured TLS MITM + arbitrary header injection based on upstream domain + dynamic token retrieval" may be easier if Envoy delegates some logic to:
- a local helper service, or
- a purpose-built forward proxy component,

while Envoy remains the traffic director/policy gate.

---

## Where custom logic lives (C# friendly)

You do not need to write C++.

### Recommended split:

**Envoy: traffic plumbing + policy**
- Listener(s) for proxy traffic
- Allowlist enforcement
- Routing to upstream clusters
- Calls out to external services for auth/token decisions

**C# "Token Agent" (optional sidecar-local helper)**
- A small HTTP service running alongside Envoy (either:
  - in the same sidecar container image, or
  - as a second sidecar container in the same pod)
- Responsibilities:
  - receive "token needed for {domain, resource, method, path}" requests from Envoy,
  - call Credential Broker with sandbox identity,
  - cache tokens in-memory keyed by (user_id, upstream, scopes/audience),
  - return token + expiry to Envoy.

This keeps your custom code in C# and keeps Envoy config simpler.

---

## Token caching strategy

### Minimal viable caching
- Cache in the Credential Broker primarily (server-side):
  - broker maintains refresh tokens and upstream access tokens, refreshes as needed.
- Envoy (or the Token Agent) caches only very short-lived access tokens:
  - keyed by (sandbox_id, upstream, audience/scopes)
  - respects expiry; refresh a bit early (e.g., at 80-90% of TTL)
  - never writes tokens to disk

### Failure behavior
- If broker unavailable:
  - fail closed for protected domains (return clear error to sandbox)
- If token invalid/expired:
  - one retry path: fetch fresh token and retry once (avoid loops)

---

## Detailed request flow (Graph example)

1. LLM in sandbox generates:
   ```
   curl https://graph.microsoft.com/v1.0/me
   ```
2. curl uses `HTTPS_PROXY` -> sends proxy request to Envoy.
3. Envoy matches destination `graph.microsoft.com` against allowlist.
4. Envoy requests token:
   - either directly to Credential Broker, or via local C# Token Agent
   - includes sandbox identity proof + desired "resource descriptor" (Graph audience/scopes)
5. Credential Broker:
   - authenticates sandbox identity
   - resolves associated `user_id`
   - validates requested upstream/scopes allowed
   - obtains/refreshes upstream OAuth token from wallet store
   - returns short-lived access token (+ expiry)
6. Envoy injects:
   - `Authorization: Bearer <token>`
   - (optional) additional headers for telemetry: `x-sandbox-id`, `x-user-context-id` (non-sensitive)
7. Envoy forwards request to Graph.
8. Response returns to sandbox.

---

## Git flow (HTTPS)

Two common approaches:

### A) Header injection for Git HTTP requests (requires MITM visibility)
- If you MITM `github.com` / `dev.azure.com`, inject Authorization or appropriate Git auth headers.

### B) Credential helper pattern (no MITM, but changes how git authenticates)
- Configure git in sandbox to use a credential helper that talks to a local endpoint (still avoids storing creds).
- Downside: more tooling/config in sandbox; upside: avoids TLS MITM complexity.

Given your desire to avoid "100 million tools" and keep CLI natural, you leaned toward proxy interception. Just note Git-over-HTTPS can be tricky without either MITM or a helper.

---

## Prompting / model instructions (important operational piece)

To prevent the LLM from looping on "I need a token":
- Tell the model:
  - Approved services (Graph, Git hosts, etc.) are reachable.
  - It should not attempt to fetch tokens or add Authorization headers.
  - "Authentication is applied automatically by the network layer when calling approved domains."
- Also tell it:
  - If it receives 401/403, it should report the error and retry at most once (or just surface it), not attempt interactive login.

This keeps commands simple and prevents it from trying to exfiltrate or request secrets.

---

## Security controls checklist

- Default-deny egress from sandbox except:
  - Envoy proxy listener
  - required internal endpoints (DNS if needed, time sync if needed, etc.)
- Envoy allowlist of domains + ports.
- Token Broker validates:
  - sandbox identity
  - user binding
  - allowed upstreams/scopes
  - rate limits
- Tokens:
  - short TTL
  - minimal scopes
  - no disk persistence
- Observability:
  - audit logs on broker: who requested what token for what upstream
  - Envoy access logs keyed by sandbox_id (but never log tokens)
- Rotation:
  - rotate sandbox identity secrets/certs regularly
  - revoke on sandbox teardown

---

## Deliverables for a code agent

1. **Envoy sidecar image**
   - Envoy binary + config template
   - bootstrap config: listeners, clusters, allowlist routing, callout to token agent/broker
2. **Sandbox image / runtime config**
   - set `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`
   - install internal CA into trust store (only if MITM / TLS inspection used)
3. **Credential Broker service**
   - endpoints like `POST /token` with body: `{ sandbox_id, upstream, audience/scopes, request_metadata }`
   - validates identity, returns `{ access_token, expires_at }`
   - integrates with wallet store + refresh logic
4. **(Optional) C# Token Agent**
   - local cache + broker client
   - simple API for Envoy to request tokens
   - in-memory cache keyed appropriately
5. **Policy config**
   - allowlist of domains -> required scopes/audience -> token type
   - mapping `sandbox_id` -> `user_id`
   - rate limits + failure mode rules
