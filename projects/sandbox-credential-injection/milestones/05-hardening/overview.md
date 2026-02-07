# Milestone 5: Hardening

## Goal

Lock down the system for production readiness: enforce proxy usage with iptables, add Kubernetes NetworkPolicies, implement audit logging, rate limiting, and certificate rotation.

## Success Criteria

- Sandbox workload cannot bypass the proxy even if it ignores `HTTPS_PROXY` env vars.
- Kubernetes NetworkPolicy blocks direct egress from the pod (except DNS and Broker).
- Every token request is audit-logged with full context.
- Rate limiting prevents runaway token requests.
- CA certificates can be rotated without rebuilding images.
- Sandbox teardown revokes/cleans up all associated state.

## Dependencies

- Milestones 1-4 (full functionality in place)

## Tasks

| # | Task | Description |
|---|------|-------------|
| 1 | [iptables Proxy Enforcement](./01-iptables-enforcement.md) | Drop outbound traffic from workload that doesn't go through the proxy |
| 2 | [Kubernetes NetworkPolicy](./02-network-policy.md) | Pod-level egress policy allowing only DNS, Broker, and proxy |
| 3 | [Audit Logging](./03-audit-logging.md) | Structured audit logs on Broker and proxy for all token operations |
| 4 | [Rate Limiting](./04-rate-limiting.md) | Per-sandbox rate limits on token requests |
| 5 | [Certificate Rotation](./05-cert-rotation.md) | Support rotating the internal CA without pod recreation |
| 6 | [Teardown Cleanup](./06-teardown-cleanup.md) | Ensure all bindings, cached tokens, and state are cleaned up on sandbox teardown |
| 7 | [Security Review](./07-security-review.md) | Threat model review and penetration testing checklist |
