# ADR-0005: Single owner and isolated demonstrations

- **Status:** Accepted
- **Date:** 2026-11-20

## Context

The app is for one owner across multiple devices. The owner also wants to demonstrate the source code and a working build to other people. Demonstrations must not expose private plans, notes, learning progress, credentials, or the production VPS.

## Decision

- Keep the production service single-owner. It has no public registration or general multi-tenant access in the current scope.
- Permit multiple owner-controlled devices to authenticate to the same account.
- Treat source-code visibility and production-data access as separate permissions. Showing the repository or an app build does not grant access to the production account.
- Use synthetic demo data in a local demo configuration or an isolated staging API/database. A demo client never contains production credentials and cannot connect to the production database.
- Keep user-specific information in gitignored local files or in the secured runtime data store; tracked examples and screenshots use synthetic content.

## Consequences

- Authentication and per-device revocation are still required even for one account.
- A public demo does not require multi-user support. If viewers need interactive demo accounts or shared data, that is a new product requirement.
- Repository visibility, license, and whether the demo needs remote synchronization remain Open.
