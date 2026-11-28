# Working in this repository

Before changing product behavior or code:

1. Read this file, README.md, and docs/README.md.
2. Read the source-of-truth document for the area being changed.
3. Check the relevant accepted ADRs before changing a long-lived technical direction.

## Documentation authority

- Product scope and confirmed requirements: docs/product/specification.md
- System boundaries and deployment shape: docs/architecture/system-overview.md
- API conventions: docs/architecture/api-guidelines.md
- Offline behavior and sync protocol: docs/architecture/offline-sync.md
- Cross-device interaction: docs/ux/cross-device-flows.md
- Lesson publishing and learning progress: docs/learning/content-lifecycle.md
- Rationale and history for durable decisions: docs/decisions/

README.md and docs/README.md are navigation pages. Link to canonical specifications instead of copying them.

## Change discipline

- Use the labels Confirmed, Recommendation, and Open so proposed details are not mistaken for user-approved requirements.
- When implementation changes documented behavior, update the canonical document in the same change.
- Record a durable architecture choice as an ADR. Mark replaced decisions as superseded and link to their replacement; preserve the decision history.
- Generate API descriptions from endpoint metadata/OpenAPI when implementation exists. Do not maintain a second hand-written endpoint inventory that can drift.
- Keep docs focused by concern. Split a document when it becomes hard to scan or has a distinct owner/lifecycle.
- Treat user data as durable across every app and server release. Keep client storage outside replaceable binaries, make schema changes versioned and transactional, preserve existing rows and pending outbox operations, and create a recoverable snapshot before a local schema upgrade. Server migrations are append-only; back up the existing database volume before applying them and stop deployment if that backup fails. Never clear a database to make a new build start.
- Keep VPS services isolated when they share a Docker host: use project-scoped volumes and service networks, keep databases off the public edge network and without published ports, and route HTTP services through one designated reverse proxy. Reject network-subnet and host-port conflicts before deployment; keep CPU and memory limits within the shared host's measured capacity.

## Privacy and public repository

- Never commit real-user content, credentials, access tokens, or VPS secrets.
- Use synthetic or anonymized sample users, lessons, and screenshots.
- Keep local-only settings in the gitignored local-private/ directory or outside this repository.
