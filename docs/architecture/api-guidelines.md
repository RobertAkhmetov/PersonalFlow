# API guidelines

**Confirmed direction:** three .NET 11 ASP.NET Core Minimal API services.

**Status:** partially implemented. Endpoints, authorization, rate limits, Problem Details, proxy handling, health checks, and migrations exist. See [implementation status](../implementation-status.md) for the production and client integration boundary.

## Structure

- Keep one Program.cs composition root per service: Identity, Life, and Learning each run as an independently deployable Minimal API process.
- Group endpoints by feature inside the service that owns the data. Identity handles account and token flows; Life handles planning, notes, and their sync; Learning handles lesson packs and learning progress.
- Keep route handlers small. They translate HTTP input/output and call application use cases.
- Put use cases and business rules in application/domain code; keep database and external integrations in that service's infrastructure.
- Use request/response DTOs at the HTTP boundary instead of exposing persistence entities. Each service publishes its own OpenAPI contract.
- Keep each service's database and migrations private to it. No service reads another service's tables, shares foreign keys across service databases, or makes synchronous calls to another service during a client request.
- Avoid a custom API gateway. Caddy terminates HTTPS and routes client requests to the appropriate service.

## Validation and errors

- Validate request shape at the API boundary. Current Life, Learning, and Identity routes contain endpoint-specific validation; a shared validation layer can be introduced when it reduces duplication without obscuring domain rules.
- Validate syntax and request shape at the API boundary; enforce domain invariants in application/domain logic.
- Return consistent Problem Details for expected client errors and unhandled server errors. Do not expose stack traces, database details, or secrets in production responses.
- Use endpoint filters only for endpoint-scoped concerns. Use middleware for cross-cutting HTTP pipeline concerns.

## HTTP pipeline and security

- Process forwarded headers only from configured trusted proxies, before code that relies on scheme or client address.
- Use centralized exception handling and status-code responses.
- Require owner authentication and authorization for private data and lesson publishing. Identity is the OpenID Connect issuer; Life and Learning validate its signed access tokens locally using cached issuer metadata. Do not expose public registration or demo access to the production account.
- Native clients use Authorization Code with PKCE through the system browser. Store refresh credentials only in the operating system's secure credential store; never embed a client secret in a native app.
- Apply rate limits to login, publishing, and expensive write paths; treat rate limiting as one control, not complete DDoS protection.
- Do not enable broad CORS by default. Current clients are native desktop/mobile apps; add narrowly scoped origins only if a browser client is introduced.
- Never embed a server secret in an app binary.

The current hosting pipeline applies trusted forwarded headers, request limits, Problem Details, JSON logs, authentication/authorization, and endpoint rate limits. Review middleware order whenever authentication, proxy, or CORS behavior changes.

## Contracts and operations

- Generate OpenAPI from endpoint metadata and keep the generated contract aligned with implementation.
- Keep changes compatible with installed clients where practical; desktop and mobile clients can remain offline and update later.
- Design sync writes to be safely retried. Return per-mutation outcomes so a conflict on one item does not discard unrelated successful operations. The proposed idempotency and concurrency protocol is specified in offline-sync.md.
- Provide liveness/readiness health endpoints. Current API readiness checks PostgreSQL connectivity and the applied migration checksums; responses do not expose dependency details.
- Keep OpenAPI/interactive API documentation private or development-only unless a deliberate public API is approved.

## Primary references

- [ASP.NET Core 11 validation](https://learn.microsoft.com/aspnet/core/fundamentals/validation?view=aspnetcore-11.0)
- [Minimal API error handling and Problem Details](https://learn.microsoft.com/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-11.0)
- [OpenAPI for Minimal APIs](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/openapi)
- [ASP.NET Core rate limiting](https://learn.microsoft.com/aspnet/core/performance/rate-limit)
