# Milestone 1: Proxy Foundation

## Goal

Stand up the full traffic path — sandbox workload -> sidecar proxy -> upstream — without any credential injection. Validate that TLS MITM works, the internal CA is trusted, and the proxy correctly forwards traffic for allowlisted domains and blocks everything else.

## Success Criteria

- A C# forward proxy exists that handles HTTP CONNECT requests.
- For allowlisted domains: proxy performs TLS MITM (terminate + re-establish) and forwards the request unmodified.
- For non-allowlisted domains: proxy rejects with 403.
- An internal CA certificate is generated and used by the proxy for MITM cert issuance.
- The sidecar has its own Docker image.
- The executor (sandbox) image trusts the internal CA.
- `BuildPodSpec` and `BuildWarmPodSpec` add the sidecar container and set proxy env vars on the workload.
- Running `curl https://httpbin.org/get` from inside a sandbox routes through the proxy and returns a successful response.

## Dependencies

- None — this is the first milestone.

## Tasks

| # | Task | Description |
|---|------|-------------|
| 1 | [Build C# Forward Proxy](./01-build-forward-proxy.md) | Create the `DonkeyWork.CodeSandbox.AuthProxy` project with CONNECT handling, MITM, and domain allowlist |
| 2 | [CA Certificate Generation](./02-ca-cert-generation.md) | Create tooling/scripts to generate the internal CA used for MITM |
| 3 | [Sidecar Docker Image](./03-sidecar-docker-image.md) | Dockerfile for the auth proxy sidecar |
| 4 | [Update Executor Image](./04-update-executor-image.md) | Install internal CA into the sandbox trust store |
| 5 | [Update Pod Spec](./05-update-pod-spec.md) | Wire the sidecar into `BuildPodSpec` and `BuildWarmPodSpec` with proxy env vars |
| 6 | [Integration Test](./06-integration-test.md) | End-to-end test: curl from sandbox through proxy to upstream |

## Architecture (this milestone)

```
┌──────────────────────────────────────────────────────┐
│  Kata Pod                                            │
│                                                      │
│  ┌─────────────────────┐   ┌──────────────────────┐  │
│  │  Workload           │   │  Auth Proxy Sidecar  │  │
│  │                     │   │                      │  │
│  │  HTTPS_PROXY ──────────>│  :8080 (proxy)       │  │
│  │  trusts internal CA │   │  :8081 (health)      │  │
│  │                     │   │                      │──────> upstream
│  └─────────────────────┘   │  MITM for allowlist  │  │
│                            │  Block everything    │  │
│                            │  else                │  │
│                            └──────────────────────┘  │
└──────────────────────────────────────────────────────┘

No Credential Broker yet — proxy forwards without injecting auth headers.
```

## Out of Scope

- Credential injection (Milestone 3)
- Credential Broker service (Milestone 2)
- Git credential helper (Milestone 4)
- iptables enforcement (Milestone 5)
