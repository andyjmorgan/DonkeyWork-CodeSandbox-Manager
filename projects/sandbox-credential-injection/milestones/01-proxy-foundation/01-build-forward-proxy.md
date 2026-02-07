# Task 1: Build C# Forward Proxy

## Summary

Create a new C# project `DonkeyWork.CodeSandbox.AuthProxy` — a forward proxy that handles HTTP CONNECT tunneling with optional TLS MITM for allowlisted domains. In this milestone, MITM mode forwards requests without modifying them (no credential injection yet).

## Context

The proxy sits as a sidecar container in the same Kata pod as the sandbox workload. The workload's `HTTPS_PROXY` environment variable points to this proxy. When the workload runs `curl https://graph.microsoft.com/v1.0/me`:

1. `curl` sends `CONNECT graph.microsoft.com:443` to the proxy.
2. Proxy checks the domain against the allowlist.
3. If allowed (MITM mode): proxy accepts the CONNECT, establishes a TLS connection with the client using a dynamically generated cert (signed by the internal CA), reads the HTTP request, and forwards it over a real TLS connection to the upstream.
4. If not allowed: proxy returns `403 Forbidden` and closes the connection.

## Acceptance Criteria

- [ ] New project `src/DonkeyWork.CodeSandbox.AuthProxy/` added to the solution.
- [ ] Proxy listens on a configurable port (default `8080`) for forward proxy traffic.
- [ ] Health endpoint on a separate port (default `8081`) at `GET /healthz`.
- [ ] Handles `CONNECT` method for HTTPS proxying.
- [ ] Domain allowlist loaded from configuration (environment variable or JSON config).
- [ ] For allowlisted domains:
  - Accepts the CONNECT tunnel.
  - Performs TLS termination with the client using dynamically generated certificates signed by the internal CA.
  - Reads the plaintext HTTP request from the client.
  - Opens a TLS connection to the real upstream.
  - Forwards the request (unmodified for now) and streams the response back.
- [ ] For non-allowlisted domains:
  - Returns `HTTP 403` to the CONNECT request.
- [ ] Structured logging via Serilog (consistent with other projects in the solution).
- [ ] Unit tests for domain matching logic.

## Implementation Hints

### Project structure

```
src/DonkeyWork.CodeSandbox.AuthProxy/
├── DonkeyWork.CodeSandbox.AuthProxy.csproj
├── Program.cs
├── Configuration/
│   └── ProxyConfiguration.cs          # Allowlist, ports, CA cert paths
├── Proxy/
│   ├── ProxyServer.cs                 # TCP listener, CONNECT handling
│   ├── TlsMitmHandler.cs              # TLS termination + upstream forwarding
│   └── CertificateGenerator.cs        # Dynamic cert generation per-domain
├── Health/
│   └── HealthEndpoint.cs              # Kestrel minimal API for /healthz
└── appsettings.json
```

### Key implementation details

**TCP listener approach**: Use `TcpListener` (or Kestrel raw connections via `IConnectionListenerFactory`) to accept connections. Parse the `CONNECT` request line manually since it's not a standard HTTP request body.

**CONNECT handling**:
```
Client sends: CONNECT graph.microsoft.com:443 HTTP/1.1\r\nHost: graph.microsoft.com:443\r\n\r\n
Proxy parses: extract target host + port
Proxy checks allowlist
If allowed: respond "HTTP/1.1 200 Connection Established\r\n\r\n"
Then: wrap client stream in SslStream (server mode, using generated cert)
```

**Dynamic certificate generation**: Use `System.Security.Cryptography.X509Certificates` to generate a cert for the requested domain, signed by the internal CA. Cache generated certs in-memory by domain to avoid regenerating on every request.

**TLS to upstream**: Open a `TcpClient` to the real upstream, wrap in `SslStream` (client mode), forward the HTTP request bytes, stream the response back.

**Bidirectional streaming**: After the initial request/response, keep the connection open for pipelining. Use `Task.WhenAny` with two copy loops (client->upstream, upstream->client) or similar pattern.

### NuGet dependencies

- `Serilog.AspNetCore` (match version from `Directory.Packages.props`)
- No YARP needed at this stage — raw TCP/TLS is more appropriate for CONNECT handling

### Configuration model

```csharp
public class ProxyConfiguration
{
    public int ProxyPort { get; set; } = 8080;
    public int HealthPort { get; set; } = 8081;
    public List<string> AllowedDomains { get; set; } = new();
    public string CaCertificatePath { get; set; } = "/certs/ca.crt";
    public string CaPrivateKeyPath { get; set; } = "/certs/ca.key";
}
```

## Files to Create

- `src/DonkeyWork.CodeSandbox.AuthProxy/DonkeyWork.CodeSandbox.AuthProxy.csproj`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Program.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Configuration/ProxyConfiguration.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/ProxyServer.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/TlsMitmHandler.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/CertificateGenerator.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Health/HealthEndpoint.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/appsettings.json`

## Files to Modify

- `DonkeyWork.CodeSandbox.sln` — add the new project
- `Directory.Packages.props` — add any new NuGet package versions if needed

## Dependencies

- Task 2 (CA Certificate Generation) — the proxy needs a CA cert to sign dynamic certs. For development/testing, the proxy can generate a self-signed CA on startup if no cert is mounted.
