#!/bin/bash
# generate-ca.sh — Generate the internal CA certificate and key for the auth proxy sidecar.
#
# Usage:
#   ./scripts/generate-ca.sh [OUTPUT_DIR] [VALIDITY_DAYS]
#
# Examples:
#   ./scripts/generate-ca.sh                        # Output to current dir, 365 days
#   ./scripts/generate-ca.sh ./certs 730            # Output to ./certs, 2 years
#
# The generated files:
#   ca.crt  — PEM-encoded CA certificate (install in trust stores)
#   ca.key  — PEM-encoded CA private key (mount only into sidecar)

set -euo pipefail

OUTPUT_DIR="${1:-.}"
VALIDITY_DAYS="${2:-365}"
SUBJECT="/CN=DonkeyWork CodeSandbox Internal CA/O=DonkeyWork/OU=CodeSandbox"

mkdir -p "$OUTPUT_DIR"

openssl req -x509 -newkey rsa:4096 \
  -keyout "$OUTPUT_DIR/ca.key" \
  -out "$OUTPUT_DIR/ca.crt" \
  -sha256 \
  -days "$VALIDITY_DAYS" \
  -nodes \
  -subj "$SUBJECT" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign"

echo ""
echo "CA certificate generated successfully:"
echo "  Certificate: $OUTPUT_DIR/ca.crt"
echo "  Private key: $OUTPUT_DIR/ca.key"
echo "  Validity:    $VALIDITY_DAYS days"
echo ""
echo "To inspect: openssl x509 -in $OUTPUT_DIR/ca.crt -text -noout"
