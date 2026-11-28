# ADR-0003: Basic offline support

- **Status:** Accepted direction; protocol Open
- **Date:** 2026-11-20

## Context

Plans, lessons, answers, and notes must remain useful when a device temporarily loses network access. The server remains the central shared store.

## Decision

Support a basic offline experience in both desktop and mobile clients, including cached reading and later synchronization of common user changes.

## Consequences

- Clients need local persistence and a visible pending-sync state.
- API writes must tolerate retries and detect conflicting edits.
- The exact offline operations and deletion semantics are not decided by this ADR. Conflict behavior is recorded in ADR-0007 and detailed sync behavior in offline-sync.md. Storage is recorded in ADR-0009.
