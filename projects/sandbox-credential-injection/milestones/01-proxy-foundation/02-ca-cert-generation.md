# Task 2: CA Certificate Generation

## Summary

Create tooling to generate the internal Certificate Authority (CA) used by the auth proxy sidecar for TLS MITM. The CA cert (public) is installed in the sandbox trust store. The CA cert + private key are mounted only into the sidecar container.

## Context

When the proxy performs TLS MITM, it needs to present certificates that the sandbox workload trusts. It does this by:

1. Having an internal CA (generated once, or per-deployment).
2. Dynamically generating leaf certificates for each domain (e.g., `graph.microsoft.com`), signed by the CA.
3. The sandbox workload trusts the CA, so it trusts the dynamically generated leaf certs.

The CA private key is sensitive — it must never enter the sandbox workload container.

## Acceptance Criteria

- [ ] A script or C# tool that generates:
  - CA certificate (`ca.crt`) — PEM format
  - CA private key (`ca.key`) — PEM format
  - Configurable subject name, validity period
- [ ] Clear documentation on how to run the generation tool.
- [ ] The generated CA cert can be used by `CertificateGenerator` in the proxy to sign domain certs.
- [ ] The generated CA cert can be installed in a Debian-based container's trust store via `update-ca-certificates`.
- [ ] For development: the proxy can optionally auto-generate a CA on startup if no cert files are present.

## Implementation Hints

### Approach: C# tool + dev fallback

**Production path**: A standalone script (bash + openssl, or a small C# console app) generates the CA cert and key. These are stored as a Kubernetes Secret and mounted into pods.

**Development path**: The proxy detects missing CA cert files on startup and generates an ephemeral CA in-memory. This makes local development/testing seamless without pre-generating certs.

### Generation script (bash + openssl)

```bash
#!/bin/bash
# generate-ca.sh
set -euo pipefail

OUTPUT_DIR="${1:-.}"
VALIDITY_DAYS="${2:-365}"
SUBJECT="/CN=DonkeyWork CodeSandbox Internal CA/O=DonkeyWork/OU=CodeSandbox"

openssl req -x509 -newkey rsa:4096 -keyout "$OUTPUT_DIR/ca.key" -out "$OUTPUT_DIR/ca.crt" \
  -sha256 -days "$VALIDITY_DAYS" -nodes -subj "$SUBJECT" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign"

echo "CA certificate generated:"
echo "  Certificate: $OUTPUT_DIR/ca.crt"
echo "  Private key: $OUTPUT_DIR/ca.key"
```

### C# in-memory generation (for dev fallback)

```csharp
using var rsa = RSA.Create(4096);
var req = new CertificateRequest(
    "CN=DonkeyWork CodeSandbox Internal CA, O=DonkeyWork, OU=CodeSandbox",
    rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
    certificateAuthority: true, hasPathLengthConstraint: true,
    pathLengthConstraint: 0, critical: true));

req.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

var caCert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(365));
```

### Kubernetes deployment (later)

The CA cert and key would be stored as a Kubernetes Secret:
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: sandbox-proxy-ca
  namespace: sandbox-containers
type: kubernetes.io/tls
data:
  tls.crt: <base64-encoded ca.crt>
  tls.key: <base64-encoded ca.key>
```

This is out of scope for this task — just ensure the cert format is compatible.

## Files to Create

- `scripts/generate-ca.sh` — bash script for production CA generation
- CA generation logic integrated into `CertificateGenerator.cs` from Task 1 (dev fallback)

## Files to Modify

- None (this is standalone tooling)

## Dependencies

- None — this can be done in parallel with Task 1.
