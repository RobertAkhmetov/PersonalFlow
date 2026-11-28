# ADR-0002: Minimal API

- **Status:** Accepted (service boundaries defined by ADR-0008)
- **Date:** 2026-11-20

## Context

The clients need centrally stored account data, lessons, and offline synchronization. The user selected .NET 11 Minimal APIs and later chose three coarse-grained services in ADR-0008.

## Decision

Build Identity, Life, and Learning as separate ASP.NET Core Minimal API services targeting .NET 11. Within each service, group endpoints by feature and keep endpoint handlers thin. Keep validation, use cases, domain rules, persistence, and HTTP concerns in clear boundaries.

## Consequences

- Minimal API does not mean putting the entire application in Program.cs.
- Use framework validation and Problem Details where appropriate, generated OpenAPI, health endpoints, authorization, and carefully ordered middleware.
- Each Identity, Life, and Learning service is its own .NET 11 Minimal API process, as accepted in ADR-0008. Each service groups endpoints by feature and keeps validation, use cases, domain rules, persistence, and HTTP concerns in clear boundaries. Authentication and storage decisions are recorded in ADR-0010 and ADR-0009.
