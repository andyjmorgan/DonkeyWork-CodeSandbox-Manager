# Task 6: Broker Docker Image

## Summary

Create a Dockerfile for the Credential Broker and add it to docker-compose for local development.

## Acceptance Criteria

- [ ] Dockerfile at `src/DonkeyWork.CodeSandbox.CredentialBroker/Dockerfile`.
- [ ] Multi-stage build matching existing patterns.
- [ ] Runs as non-root (UID 10000).
- [ ] Added to `docker-compose.yml` on port 8090.
- [ ] Health check configured.
- [ ] Manager's docker-compose config includes `CredentialBrokerUrl` pointing to the broker service.

## Implementation Hints

Follow the same Dockerfile pattern as the Manager. The broker is a standard ASP.NET minimal API — no special tools needed in the image.

```yaml
credential-broker:
  build:
    context: .
    dockerfile: src/DonkeyWork.CodeSandbox.CredentialBroker/Dockerfile
  ports:
    - "8090:8090"
  environment:
    ASPNETCORE_URLS: "http://+:8090"
    BrokerConfiguration__Port: "8090"
  networks:
    - kata-network
  healthcheck:
    test: ["CMD", "curl", "-f", "http://localhost:8090/healthz"]
    interval: 30s
    timeout: 10s
    retries: 3
```

## Files to Create

- `src/DonkeyWork.CodeSandbox.CredentialBroker/Dockerfile`

## Files to Modify

- `docker-compose.yml`

## Dependencies

- Task 1 (project must exist)
