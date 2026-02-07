# Task 5: Certificate Rotation

## Summary

Support rotating the internal CA certificate without rebuilding images or recreating pods.

## Acceptance Criteria

- [ ] The proxy watches its CA cert files for changes and reloads without restart.
- [ ] The sandbox trust store can be updated by mounting a ConfigMap/Secret that Kubernetes updates in-place.
- [ ] Document the rotation procedure: generate new CA, update Secret, pods pick up changes.
- [ ] Graceful transition: during rotation, both old and new CA are trusted temporarily.

## Implementation Hints

### Proxy-side

Use a `FileSystemWatcher` or periodic poll to detect changes to `/certs/ca.crt` and `/certs/ca.key`. When changed, reload the CA into the `CertificateGenerator`.

### Sandbox-side

Kubernetes Secret volumes are updated automatically when the Secret changes (with a delay). The sandbox's `update-ca-certificates` would need to be re-run. Options:
- Periodic re-check in the entrypoint (cron-like)
- Accept that cert rotation only takes effect on new pods (simpler)

For MVP: accept that rotation requires new pods. Document the procedure.

## Files to Modify

- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/CertificateGenerator.cs` — support reloading CA

## Dependencies

- Milestone 1 (CA and proxy exist)
