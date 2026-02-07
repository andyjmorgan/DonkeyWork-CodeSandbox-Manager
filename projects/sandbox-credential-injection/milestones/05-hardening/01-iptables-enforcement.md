# Task 1: iptables Proxy Enforcement

## Summary

Add iptables rules inside the sandbox workload container to prevent bypassing the proxy. Even if the workload ignores `HTTPS_PROXY`, all outbound TCP traffic (except to the proxy port and localhost) is dropped.

## Acceptance Criteria

- [ ] An init container or entrypoint script configures iptables rules before the workload starts.
- [ ] Rules allow: traffic to `127.0.0.1:8080` (proxy), `127.0.0.1:8081` (sidecar admin), and loopback.
- [ ] Rules drop: all other outbound TCP traffic from the workload container.
- [ ] DNS (UDP port 53) is allowed (needed by some tools that resolve before proxying).
- [ ] The workload container user (UID 10000) cannot modify iptables (requires `NET_ADMIN` capability which is only granted to the init container, not the workload).
- [ ] Existing functionality (health checks, command execution on port 8666) is unaffected.

## Implementation Hints

### Init container approach (recommended)

Use a privileged init container that sets up iptables rules, then exits. The workload container runs as non-root without `NET_ADMIN` capability, so it cannot undo the rules.

```yaml
initContainers:
  - name: proxy-iptables-init
    image: alpine:latest
    securityContext:
      capabilities:
        add: ["NET_ADMIN"]
    command:
      - sh
      - -c
      - |
        # Allow loopback
        iptables -A OUTPUT -o lo -j ACCEPT
        # Allow established connections
        iptables -A OUTPUT -m state --state ESTABLISHED,RELATED -j ACCEPT
        # Allow DNS
        iptables -A OUTPUT -p udp --dport 53 -j ACCEPT
        # Allow traffic to proxy port
        iptables -A OUTPUT -p tcp --dport 8080 -d 127.0.0.1 -j ACCEPT
        iptables -A OUTPUT -p tcp --dport 8081 -d 127.0.0.1 -j ACCEPT
        # Drop everything else from the workload user
        iptables -A OUTPUT -m owner --uid-owner 10000 -j DROP
```

### Kata container considerations

Verify that iptables work inside Kata VMs. Kata runs a lightweight VM — iptables should work since the guest kernel supports netfilter, but test this explicitly.

## Files to Modify

- `src/DonkeyWork.CodeSandbox.Manager/Services/Container/KataContainerService.cs` — add init container to pod spec
- `src/DonkeyWork.CodeSandbox.Manager/Services/Pool/PoolManager.cs` — same for warm pods

## Dependencies

- Milestone 1, Task 5 (pod spec structure established)
