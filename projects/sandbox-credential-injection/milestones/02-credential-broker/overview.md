# Milestone 2: Credential Broker

## Goal

Build the Credential Broker service — a C# API that runs outside the sandbox boundary. It manages the mapping from sandbox identity to user identity, and issues short-lived access tokens for approved upstream services.

## Success Criteria

- A standalone C# service (`DonkeyWork.CodeSandbox.CredentialBroker`) exists and runs.
- The Manager can register/deregister sandbox-to-user bindings via the Broker API.
- The Broker can return access tokens for supported upstreams (mocked/stubbed identity providers initially).
- The Broker validates sandbox identity before issuing tokens.
- Audit logging records every token request.

## Dependencies

- Milestone 1 (proxy exists, but credential injection is not wired yet — that's Milestone 3).

## Tasks

| # | Task | Description |
|---|------|-------------|
| 1 | [Broker Service Scaffold](./01-broker-service-scaffold.md) | Create the project, API endpoints, configuration |
| 2 | [Sandbox Binding API](./02-sandbox-binding-api.md) | `POST /api/bindings` and `DELETE /api/bindings/{sandbox_id}` |
| 3 | [Token Endpoint](./03-token-endpoint.md) | `POST /api/token` — validate sandbox, resolve user, return token |
| 4 | [Wallet Store Abstraction](./04-wallet-store-abstraction.md) | Interface + in-memory implementation for token storage |
| 5 | [Manager Integration](./05-manager-integration.md) | Wire allocation/cleanup to call Broker binding API |
| 6 | [Broker Docker Image](./06-broker-docker-image.md) | Dockerfile and docker-compose integration |
