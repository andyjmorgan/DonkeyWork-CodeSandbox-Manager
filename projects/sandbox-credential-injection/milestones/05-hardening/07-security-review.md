# Task 7: Security Review

## Summary

Perform a structured threat model review and create a penetration testing checklist specific to the credential injection system.

## Acceptance Criteria

- [ ] Threat model document covering:
  - Credential exfiltration from sandbox (mitigated by: no creds in sandbox)
  - Proxy bypass (mitigated by: iptables + NetworkPolicy)
  - Sidecar compromise (mitigated by: sidecar has no long-lived tokens, only sandbox identity)
  - Broker compromise (mitigated by: mTLS, rate limiting, audit logging)
  - Replay attacks (mitigated by: short token TTL)
  - Scope escalation (mitigated by: Broker validates scopes against binding)
  - Malicious model instructions attempting to exfiltrate via approved APIs (mitigated by: minimal scopes, audit logging)
- [ ] Penetration testing checklist:
  - Can the sandbox directly reach the Broker? (should not)
  - Can the sandbox modify iptables? (should not, non-root)
  - Can the sandbox extract the CA private key? (should not, only mounted in sidecar)
  - Can the sandbox send requests to arbitrary IPs bypassing the proxy? (should not, iptables)
  - Can a sandbox with binding A get tokens for binding B? (should not, Broker validates sandbox_id)
  - Are tokens logged anywhere? (should not)
- [ ] All findings documented and tracked.

## Files to Create

- `docs/security/threat-model.md`
- `docs/security/pentest-checklist.md`

## Dependencies

- All previous milestones complete.
