# Task 1: Broker Service Scaffold

## Summary

Create the `DonkeyWork.CodeSandbox.CredentialBroker` project — a minimal API service with Serilog logging, health checks, and configuration following the same patterns as the Manager.

## Acceptance Criteria

- [ ] New project `src/DonkeyWork.CodeSandbox.CredentialBroker/` added to the solution.
- [ ] Minimal API with health endpoint at `GET /healthz`.
- [ ] Serilog structured logging configured.
- [ ] Configuration model for broker settings (port, allowed upstreams, sandbox identity validation).
- [ ] Runs on configurable port (default `8090`).
- [ ] Shared contracts project `src/DonkeyWork.CodeSandbox.CredentialBroker.Contracts/` for request/response models shared between Broker and its clients.

## Implementation Hints

Follow the existing `Program.cs` patterns from the Manager or Server projects. Use minimal APIs, `IOptions<T>` pattern, and Serilog.

### Key configuration model

```csharp
public class BrokerConfiguration
{
    public int Port { get; set; } = 8090;
    public List<UpstreamConfig> AllowedUpstreams { get; set; } = new();
}

public class UpstreamConfig
{
    public string Host { get; set; } = string.Empty;       // e.g. "graph.microsoft.com"
    public string TokenType { get; set; } = "bearer";       // "bearer" or "git_credential"
    public string? OAuthAudience { get; set; }               // e.g. "https://graph.microsoft.com"
    public List<string> AllowedScopes { get; set; } = new(); // e.g. ["User.Read"]
}
```

## Files to Create

- `src/DonkeyWork.CodeSandbox.CredentialBroker/DonkeyWork.CodeSandbox.CredentialBroker.csproj`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Program.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/Configuration/BrokerConfiguration.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker/appsettings.json`
- `src/DonkeyWork.CodeSandbox.CredentialBroker.Contracts/DonkeyWork.CodeSandbox.CredentialBroker.Contracts.csproj`
- `src/DonkeyWork.CodeSandbox.CredentialBroker.Contracts/Requests/TokenRequest.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker.Contracts/Responses/TokenResponse.cs`
- `src/DonkeyWork.CodeSandbox.CredentialBroker.Contracts/Requests/BindingRequest.cs`

## Files to Modify

- `DonkeyWork.CodeSandbox.sln` — add both new projects
