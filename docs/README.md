# Documentation map

This folder contains the canonical product and engineering specifications for Personal Flow. Filenames use searchable English slugs, and each topic has a single source-of-truth document.

## Source of truth by subject

- [Workout plans](product/workouts.md): program editor, calendar assignments and session history.
- [Product specification](product/specification.md): product purpose, requirements, platform targets, confirmed and open scope.
- [System overview](architecture/system-overview.md): clients, service boundaries, server responsibilities, and deployment shape.
- [VPS deployment and operations](../deploy/README.md): Ubuntu setup, secret provisioning, release, backup, and restore.
- [API guidelines](architecture/api-guidelines.md): Minimal API structure, validation, errors, middleware, and contract rules.
- [Offline and synchronization](architecture/offline-sync.md): offline capabilities, pending writes, and unresolved conflict policy.
- [Каталог экранов PersonalFlow](ux/screens.html)
- [Cross-device UX](ux/cross-device-flows.md): how work moves between desktop and phone, including the Codex handoff.
- [Learning content lifecycle](learning/content-lifecycle.md): weekly authoring, source links, publishing, and progress.
- [Decision records](decisions/README.md): durable decisions and their rationale, including microservices, storage, and authentication.

## Current delivery status

Backend source and deployment assets are implemented and build-verified, but have not been deployed to a VPS. Avalonia and MAUI have authenticated Life/Learning sync paths; desktop conflict resolution and epoch recovery, plus live validation against a deployed server, remain outstanding. See [implementation status](implementation-status.md).

## Status labels

- **Confirmed** — explicitly agreed by the user.
- **Recommendation** — proposed direction that still needs approval before it becomes a requirement.
- **Open** — not decided; do not implement by assumption when it changes data, security, or user-visible behavior.
- **Implemented** — present in the repository and verified for the stated scope.

A decision record explains why a direction was chosen. The relevant specification remains the source for detailed behavior. Update both when a decision changes user-visible requirements.
