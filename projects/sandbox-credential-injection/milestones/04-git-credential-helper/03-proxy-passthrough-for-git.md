# Task 3: Proxy Passthrough for Git

## Summary

Configure the proxy to handle Git hosting domains in CONNECT passthrough mode (not MITM), since Git auth is handled by the credential helper, not by header injection.

## Acceptance Criteria

- [ ] Proxy configuration supports a separate list of "passthrough domains" (or a flag per domain in the allowlist).
- [ ] For passthrough domains: proxy accepts CONNECT, tunnels the TCP stream without terminating TLS.
- [ ] For MITM domains: behavior unchanged (terminate TLS, inject headers).
- [ ] `github.com`, `api.github.com`, `dev.azure.com`, `gitlab.com` default to passthrough mode.
- [ ] `graph.microsoft.com` and other API domains default to MITM mode.

## Implementation Hints

Extend the domain allowlist configuration:

```csharp
public class DomainPolicy
{
    public string Host { get; set; } = string.Empty;
    public ProxyMode Mode { get; set; } = ProxyMode.Mitm;  // Mitm or Passthrough
}

public enum ProxyMode
{
    Mitm,        // TLS termination + header injection
    Passthrough  // CONNECT tunnel, no TLS termination
}
```

In the CONNECT handler:

```csharp
var policy = GetDomainPolicy(targetHost);
if (policy is null)
{
    // Not in allowlist -> block
    return Send403();
}

if (policy.Mode == ProxyMode.Passthrough)
{
    // Just tunnel bytes between client and upstream
    return TunnelPassthrough(clientStream, targetHost, targetPort);
}
else
{
    // Full MITM
    return HandleMitm(clientStream, targetHost, targetPort);
}
```

## Files to Modify

- `src/DonkeyWork.CodeSandbox.AuthProxy/Configuration/ProxyConfiguration.cs`
- `src/DonkeyWork.CodeSandbox.AuthProxy/Proxy/ProxyServer.cs`

## Dependencies

- Milestone 1, Task 1 (proxy CONNECT handling exists)
