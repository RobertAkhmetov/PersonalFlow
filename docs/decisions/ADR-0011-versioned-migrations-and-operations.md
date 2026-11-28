# ADR-0011: Versioned SQL migrations and VPS operations

- Status: Accepted for the initial single-VPS deployment
- Date: 2026-11-25

## Context

PersonalFlow runs three independently deployable APIs with separate PostgreSQL databases on one modest Ubuntu VPS. `EnsureCreated` is not a production migration strategy: schema upgrades need ordering, locking, drift detection, and clear separation from runtime privileges. The initial schemas can be exported from the EF Core models, but the local environment does not have a working .NET EF migration toolchain for the selected versions.

## Decision

Each service embeds append-only SQL migration files in its API assembly. A shared hosting library applies them in lexical version order using a PostgreSQL transaction-scoped advisory lock and records a SHA-256 checksum for every version. A service refuses readiness if the database is unavailable, an applied script is missing or changed, or the expected migration set is not current. One-shot Compose migration jobs connect with a dedicated migrator role; API processes use separate runtime roles with DML grants.

The initial production host is a single Ubuntu VPS managed with Docker Compose. Caddy terminates HTTPS; PostgreSQL and API ports remain private. Secrets are local-only before deployment, mounted through Compose secrets, and copied into a private in-memory filesystem before the API drops to its non-root UID. PostgreSQL dumps and the Identity data-protection key ring are encrypted with age before off-host transfer. The decryption private key stays off the VPS. Restore rotates Life and Learning sync epochs.

## Consequences

- Applied SQL files are immutable. Schema changes add a new migration; they never edit a released migration.
- Migration files need careful review and a restore rehearsal because this repository does not yet run migrations against a live-like PostgreSQL service in CI.
- The shared PostgreSQL process is a resource-efficient deployment but not high availability. A single-host failure requires restore.
- Compose secret files and the server host remain part of the trusted boundary. The image build and VPS deployment are separate from this repository build.
- The generated token-signing certificate is valid for five years; rotation is a scheduled operational task that needs a validated key-ring/token compatibility plan.
- Initial SQL is derived from EF Core's model script. EF Core remains the application persistence abstraction; schema evolution uses the embedded SQL runner for the initial deployment.

## Revisit when

- Automated PostgreSQL migration/integration checks are available.
- A staging VPS can validate upgrades and restores.
- High availability or multiple hosts become a requirement.
- The signing certificate rotation plan is implemented and verified.
