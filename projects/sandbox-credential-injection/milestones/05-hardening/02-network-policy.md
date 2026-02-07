# Task 2: Kubernetes NetworkPolicy

## Summary

Create Kubernetes NetworkPolicy resources that restrict pod egress at the cluster network level.

## Acceptance Criteria

- [ ] NetworkPolicy applied to `sandbox-containers` namespace.
- [ ] Sandbox pods can only egress to:
  - Cluster DNS (kube-dns service, port 53 UDP/TCP)
  - Credential Broker service (if in a different namespace)
  - Internet (through the sidecar — the pod as a whole needs egress, but only the sidecar should use it)
- [ ] Sandbox pods cannot reach:
  - Kubernetes API server
  - Other namespaces (except Broker)
  - Cloud metadata endpoints (169.254.169.254)
- [ ] NetworkPolicy YAML files created and documented.
- [ ] Validated with the project's CNI plugin and Kata runtime.

## Implementation Hints

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: sandbox-egress-policy
  namespace: sandbox-containers
spec:
  podSelector:
    matchLabels:
      app: kata-manager
      runtime: kata
  policyTypes:
    - Egress
  egress:
    # Allow DNS
    - to: []
      ports:
        - protocol: UDP
          port: 53
        - protocol: TCP
          port: 53
    # Allow HTTPS to internet (sidecar needs this)
    - to: []
      ports:
        - protocol: TCP
          port: 443
    # Allow to Credential Broker
    - to:
        - namespaceSelector:
            matchLabels:
              name: sandbox-system
      ports:
        - protocol: TCP
          port: 8090
```

Note: NetworkPolicy alone doesn't prevent the workload container from bypassing the proxy — it operates at the pod level, not the container level. iptables (Task 1) handles intra-pod enforcement. NetworkPolicy adds a second defense layer.

### Block metadata endpoint

```yaml
    # Explicitly deny cloud metadata
    - to:
        - ipBlock:
            cidr: 0.0.0.0/0
            except:
              - 169.254.169.254/32
```

## Files to Create

- `k8s/network-policies/sandbox-egress-policy.yaml`

## Dependencies

- None (can be developed independently)
