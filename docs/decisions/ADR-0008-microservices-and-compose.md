# ADR-0008: Coarse-grained microservices on Docker Compose

- **Status:** Accepted
- **Date:** 2026-11-20

## Context

The application is expected to run on a modest Linux VPS and the user explicitly chose microservices over a modular monolith, with Docker Compose for deployment. The system has distinct identity, personal-life, and learning responsibilities, but does not need high-volume event processing or many independently scaling components.

## Decision

- Keep one repository and .NET solution, but run three independent .NET 11 ASP.NET Core Minimal API services:
  - **Identity** owns the single-owner account, sign-in, tokens, and device sessions.
  - **Life** owns planning, recurrence, leave periods, private text, pins, and their offline synchronization/conflicts.
  - **Learning** owns lesson packs, source links, answers, notes, and learning progress.
- Run Caddy, the three APIs, and one PostgreSQL instance with Docker Compose on the VPS. Expose only Caddy publicly.
- Each API owns a separate logical PostgreSQL database, database role, schema, migrations, and data lifecycle. Services do not access one another's tables or use cross-database foreign keys.
- Clients call the APIs directly through Caddy. Caddy provides TLS termination and routing; do not build a separate gateway process.
- Avoid synchronous service-to-service calls in client request flows. No broker, Redis, Kubernetes, or service mesh is required initially. Add asynchronous integration events and an outbox only when a real cross-service workflow needs them.
- Keep service boundaries coarse-grained. Do not make a service for each individual feature.

## Consequences

- The deployment uses five containers and therefore more memory and operational work than one API process. Set resource limits only after the VPS resources are known; health checks, structured logs, off-host backups, and a restore drill are required.
- PostgreSQL is a shared server process for efficiency, but data ownership remains isolated through separate databases and credentials. A host-level outage affects every API.
- Each service can be built, versioned, and deployed independently. API contracts remain separate and are published through per-service OpenAPI documents.
- End-to-end workflows must not depend on synchronous calls from one business service to another. If such a workflow is later needed, use asynchronous propagation and accept eventual consistency.

## Primary references

- [Microsoft guidance on communication between microservices](https://learn.microsoft.com/en-us/dotnet/standard/microservices-architecture/architect-microservice-container-applications/communication-in-microservice-architecture)
- [Microsoft guidance on service-owned data](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/architect-microservice-container-applications/data-sovereignty-per-microservice)
- [Docker Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/)
