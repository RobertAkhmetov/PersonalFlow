# ADR-0012: Preserve user data across releases

- Status: Accepted
- Date: 2026-11-28

## Context

Plans, notes, workouts, learning progress, sync cursors, and pending offline writes are the product's core data. A new desktop/mobile binary or API schema must not reset these records. Devices may be offline for long periods, so a backend change must also remain compatible with clients that have not yet upgraded. A server restore can roll back already acknowledged writes and needs explicit client reconciliation.

## Decision

- Store each client database in a stable operating-system user-data directory, outside replaceable application binaries/resources. Keep the application ID and database path stable across normal app updates.
- Keep a local SQLite schema version. Run all schema changes for one release in a transaction, create and integrity-check a pre-upgrade SQLite snapshot before changing an existing database, and refuse to open a database created by a newer schema. Never clear local user tables or pending outboxes during startup or an upgrade.
- Preserve the transaction boundary that writes an item and its outbox mutation together. Learning progress has its own durable queue. Recovery and migration code may retire a legacy representation only after it has committed the equivalent current representation.
- Keep PostgreSQL SQL migrations append-only and checksum-verified. Use additive expand-and-contract changes while old clients can remain installed. Before a release can migrate an existing deployment volume, require an encrypted off-host backup of all databases and Identity data-protection keys; if it fails, stop before pulling and applying the release. Use a stable named PostgreSQL volume.
- Keep daily encrypted off-host backups and retain the independent recovery key outside the VPS. After a server restore, rotate the Life and Learning sync epochs and reconcile local changes instead of replacing the device database with the restored snapshot.

## Consequences

- Ordinary app updates preserve device-local records and pending operations. The desktop client does not yet sync with the server, so its data currently exists only on that computer; app updates do not protect against loss or erasure of the device.
- A local snapshot adds storage use when a client schema is upgraded. It remains next to the local database and is subject to the same device access controls.
- Database backups and restore logic are prepared but have not yet been exercised against a live VPS. The safeguards reduce upgrade risk but are not a substitute for a rehearsed restore.
- Downgrading to an older client after a schema change is not supported; the older client must refuse to open a newer database rather than risk changing it.

## Revisit when

- Desktop synchronization becomes available.
- Local database size requires a documented snapshot retention policy.
- Automated upgrade/restore rehearsal is added to CI or staging.
