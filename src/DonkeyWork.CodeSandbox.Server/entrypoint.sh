#!/bin/bash
set -e

# Install proxy CA certificate if mounted
CA_MOUNT="/etc/proxy-ca/ca.crt"
if [ -f "$CA_MOUNT" ]; then
    cp "$CA_MOUNT" /usr/local/share/ca-certificates/proxy-ca.crt
    update-ca-certificates 2>/dev/null || true
    export NODE_EXTRA_CA_CERTS="$CA_MOUNT"
    echo "Proxy CA certificate installed"
fi

# Start the application
exec dotnet DonkeyWork.CodeSandbox.Server.dll "$@"
