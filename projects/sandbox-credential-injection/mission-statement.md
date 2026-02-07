# Mission Statement: Sandbox Credential Injection

## Problem

LLM-driven workloads running inside Kata-isolated sandboxes need to interact with external services (Microsoft Graph, GitHub, Azure DevOps, etc.) using standard CLI tools (`curl`, `git`, SDKs). Today, this requires placing user credentials inside the sandbox, which creates exfiltration risk — a compromised or malicious workload could steal tokens.

## Goal

Enable sandboxed workloads to authenticate to approved external services transparently, without any credential material ever entering the sandbox. From the LLM's perspective, `curl https://graph.microsoft.com/v1.0/me` should just work — authentication happens invisibly at the network layer.

## Approach

Egress credential injection via a sidecar proxy:

1. **Auth Proxy sidecar** runs alongside the workload in the same Kata pod. All outbound HTTPS traffic routes through it.
2. For approved domains, the proxy performs TLS MITM (using an internal CA trusted by the sandbox), injects `Authorization` headers with short-lived tokens, then forwards upstream.
3. For Git operations, a credential helper in the sandbox requests tokens from the sidecar without MITM complexity.
4. A **Credential Broker** service (outside the sandbox) manages the mapping of `sandbox_id -> user_id -> tokens`, performs OAuth flows, and returns ephemeral access tokens to the proxy.
5. Everything else is blocked by default.

## Non-Goals (for now)

- Arbitrary protocol support (only HTTPS + Git-over-HTTPS)
- WebSocket proxying
- Outbound traffic to unapproved domains
- Multi-cloud identity federation

## Success Criteria

- Sandbox workload can `curl` approved APIs and `git clone/push` approved repos with zero credential configuration.
- No credential material (tokens, secrets, refresh tokens) is ever present inside the sandbox VM.
- Token acquisition, caching, and refresh are fully managed outside the sandbox.
- The system works with the existing warm pool (pre-created pods get user binding at allocation time).
