# Offline and synchronization

**Confirmed:** basic offline capability; same-field edits to the same existing task must be surfaced for user resolution, while independent new tasks are preserved as separate inserts.
**Protocol status:** Life and Learning server endpoints and separate local SQLite transports are implemented. Avalonia and MAUI use system-browser sign-in and OS-secure refresh-token storage, and support manual Life/Learning synchronization and lesson fetching. MAUI also syncs on launch/resume, resolves field and edit-versus-delete conflicts, and recovers explicitly after a server epoch change. Desktop currently stores remote conflict snapshots but still needs conflict-resolution UX and epoch recovery. Neither client has been validated against a deployed server. Cached lessons can be browsed offline.

## Expected user experience

The clients should open cached plans and lessons without a connection. Common task edits and moves, completion, lesson answers, and short notes can be recorded offline and synced later. The UI distinguishes saved locally, pending sync, synchronized, and needs attention. It never discards a local answer or note silently.

## Data continuity across releases

**Confirmed:** app and backend updates must preserve plans, notes, workouts, learning progress, and pending local writes. Client SQLite lives in each operating system's persistent user-data directory, outside replaceable desktop binaries or mobile app resources. A user-level app update reuses the same database path and stable application ID. Local schema upgrades run transactionally; before a schema version advances, the client creates and integrity-checks a recoverable copy of the previous database. A client build refuses to open a database from a newer schema version rather than trying to rewrite it with older code. Feature migrations must preserve queued writes and unknown user data; startup must never clear the database.

Life item edits and their outbox entries commit in the same SQLite transaction. Learning progress uses its own persistent outbox. This keeps unsynchronized offline work on the device through an app update. The Avalonia desktop client stores its SQLite file under the user's local application data directory; MAUI stores it under the stable app data directory. Both clients can submit their independent Life and Learning queues and fetch lesson packs. Until a sync succeeds, unsent desktop edits remain only on that computer; desktop conflict resolution and server-epoch recovery are still outstanding.

Backend releases use immutable, checksum-verified, append-only SQL migrations. The deployment script checks the stable PostgreSQL data volume even when the stack is stopped, starts PostgreSQL on its existing image, and requires a successful encrypted off-host backup before pulling a release. A backup failure stops deployment before a migration can run. Daily backups include the three databases and required Identity key material. PostgreSQL restore rotates the Life/Learning epochs so mobile devices reconcile restored data with retained local state. Never remove the production PostgreSQL volume as a deployment or troubleshooting step.

These safeguards protect ordinary upgrades and supported server restores; they do not replace a second-device or external copy for a device that is lost, erased, or physically damaged. Until desktop sync is implemented, keep an independent copy of desktop-only data before replacing or erasing that computer.

A synchronization batch continues processing unrelated items when one item conflicts. A conflict remains visible and durable until the owner resolves it, including after closing the app or switching devices. The Life service owns planning, private text, and their sync/conflict records. The Learning service owns lesson content and learning progress sync. Clients synchronize each service boundary independently; there is no central sync microservice.

## Proposed conflict detection

- Persist an outbox mutation before showing it as saved. Give it a stable mutation ID for retry idempotency, a device ID, per-device sequence, entity/occurrence ID, base server revision, operation type, and changed fields or semantic action. Preserve causal order for one entity; independent entities can sync concurrently. Device time is for display and diagnostics, not conflict ordering.
- Apply a mutation when its base revision is current. If the server is newer, compare the local mutation's base values with the current server values for the fields it changes. Automatically merge edits to different fields on the same task.
- Create a conflict only when both devices changed the same field of the same existing task to different values. If both chose the same value, treat the edits as converged. The conflict payload contains the base, current server value, and local proposal.
- New tasks are independent inserts, including multiple tasks added on the same date or at the same position while replacing an older task. Keep every insert; do not deduplicate by date, title, or matching text. Insertions are merged as a set of distinct IDs, not as competing edits; the Life service assigns a canonical order after sync and clients use a stable provisional order while offline. An idempotency key prevents replaying the same insert twice but must not combine two intentional tasks. If an old task was edited on one device and deleted on another, resolve that edit-versus-delete conflict separately from any newly added tasks.
- Treat a recurrence rule as a field of its existing recurring task: divergent edits to that rule conflict. A one-off completion or move targets a dated occurrence, not the whole rule. Two different destination dates for the same occurrence conflict; moving separate tasks does not.
- Treat completion as an explicit action on one occurrence. A stale, unchanged “not complete” snapshot must not undo another device's completion. A concurrent explicit reopen versus complete on that occurrence is a conflict.
- Preserve independent appended notes as separate entries. For simultaneous edits to the same long note or lesson answer, preserve both versions and require a user choice or manual merge; do not automatically combine prose.
- Use deletion tombstones. A concurrent edit versus deletion of the same existing item is a conflict, and a stale device must not recreate a deleted item silently.

## Proposed conflict record and API result

A conflict record stores the entity/occurrence, conflicting fields, base version, server version, local version, whether each side deleted the item, origin device, and resolution state. Store it in the local database and sync it to the server so it can be resolved from another owner device. Keep task text out of diagnostic logs.

Each sync mutation has an independent result: applied, already applied, needs resolution, or rejected as invalid/unauthorized. A conflict on one mutation does not fail the whole batch. Use stable mutation IDs for idempotent retries. A resolution is a new mutation based on the latest server revision and changes only the fields the user resolved; preserve all other current server fields. If the server changes again before it arrives, create a new conflict rather than dropping either version.

## Proposed conflict UX

- Add a **Needs attention** inbox and a sync-status badge; do not interrupt all work with a modal.
- Explain which item and fields differ, when and on which device each version was edited, and show the base, server, and local values side by side.
- Offer **Use server version**, **Keep this device's version**, and **Edit a combined version**. For long text, provide side-by-side comparison and a manual editor.
- Keep both versions until the resolution has synced successfully. Continue syncing unrelated items while a conflict is unresolved.
- Record the resolution and retain a small audit trail so a later sync cannot silently reverse it.

## Local reminders

Scheduled Android notifications are derived from the latest local task/recurrence state. Rebuild affected occurrence alerts after sync, task movement, rule edits, unavailability changes, and device restart. If a phone is offline when another device changes a task, the old local alert can remain until that phone syncs; show pending-sync state.

## Offline scope and remaining decisions

- Recurrence, pins, deletion and leave edits are available offline as agreed in the product specification.
- Sync at launch and resume. Background execution is opportunistic.
- How long clients retain local outbox mutations and unresolved conflicts, and how to recover them after device loss.
- Whether the server keeps unresolved conflict records indefinitely or prunes resolved history after a retention period.
- The initial scope covers text and links.

## Recommended transaction boundaries

Write the changed item and its outgoing operation in one SQLite transaction. Derive reminders from committed state and reconcile after restart. Undo emits a compensating change to the date field. Undo after completion on another device must preserve that completion. A recurring occurrence retains the series ID and original scheduled date through a move. Even when two occurrences land on the same date, their identities remain distinct.

For each mutation the owning service commits the item revision, change-feed entry and mutation receipt together. On a lost HTTP response, retry with the same mutation ID. Return the original receipt for an identical request. Reject reuse of an ID with a different payload. Retain receipts across the supported retry window. For a stale device beyond that window reconcile its pending operations before resubmission. After a server restore rotates its sync epoch, the mobile client creates fresh mutation IDs, pulls the new feed from the beginning, and compares recovered local intent against the new server state. Recovered writes carry a `RecoveryCompare` flag: the server compares changed fields against the saved base even when the old revision number is no longer meaningful. Same-field differences become normal user-resolvable conflicts.

Give Life and Learning separate pull cursors. A small single-owner deployment can serialize feed version assignment with a database row lock held until commit. A plain sequence allocated before commit can expose a later number before an earlier transaction becomes visible. Persist a downloaded page and its cursor together. When history expires, replace the server snapshot locally and reapply queued edits against their saved bases. Preserve drafts and conflicts during this operation. [PostgreSQL sequence semantics](https://www.postgresql.org/docs/current/functions-sequence.html) · [Android offline data architecture](https://developer.android.com/topic/architecture/data-layer/offline-first)

## Implemented client transport (2026-11-21)

The shared SQLite client stores the local pre-edit body and base revision together with each outgoing operation. Before first delivery it freezes the request JSON; retries reuse that exact payload and mutation ID. An acknowledged predecessor advances unsent dependent operations to its accepted revision. Pull stores a page and its cursor in one transaction and preserves items with local pending writes. A conflict is durable and blocks only that entity. Learning answers are excluded from the Life transport.

Workout calendar links extend the existing Life JSON contracts additively: `PlanItem` and `Recurrence` carry `WorkoutDayId`; `TrainingLog` records `ScheduleItemId`, `ScheduleSeriesId`, and `ScheduleOriginalDate`. The Life sync field allowlist accepts these fields, so no PostgreSQL column migration is required. Finishing a linked session writes its result and the one-off item or recurring occurrence completion together with their outbox mutations in one local SQLite transaction.

Earlier protocol checks covered simulated lost responses, reopening SQLite, dependent edits, conflict isolation, and Life/Learning epoch retention. The mobile recovery flow archives the previous shadow before clearing it, downloads the new service feed first, reconciles the local cache with that feed, and recreates writes with new IDs. The database transaction marks the snapshot phase so an interrupted recovery can resume without replacing IDs that may already have been accepted. Conflicts from the prior epoch remain read-only in the local inbox; conflicts against the restored state use the normal resolution path. Live restore/recovery against PostgreSQL has not been exercised. Older Life operations without trustworthy saved bases remain preserved rather than automatically uploaded and still need an explicit import path.

## Local sync diagnostics

**Implemented:** desktop and mobile settings show the combined local outbox size, unresolved Life/Learning conflicts, and recorded sync-epoch changes. Mobile also shows conflicts fetched from the server. These counts are local snapshots and do not imply that a server connection is currently active.

The mobile UI exposes account setup, manual sync, the last successful synchronization time, and quiet launch/resume synchronization attempts. It fetches unresolved Life and Learning conflicts after a successful service sync, stores their snapshot locally, compares base/local/server values, and queues a selected merge with the server conflict ID. Edit-versus-delete conflicts offer explicit choices to keep the tombstone or restore the edited version. The server supersedes an older open conflict when a new conflict appears while a resolution is stale. An epoch conflict now offers an explicit recovery action; the app retains local state, downloads the current server history, rebases recoverable writes, and leaves field conflicts for user review. Previous conflict snapshots are view-only. Desktop settings expose HTTPS service addresses, system-browser sign-in, manual sync, and local outbox/conflict counts; refresh tokens use Windows Credential Manager or macOS Keychain. Desktop sync downloads conflict snapshots but does not yet offer conflict resolution or epoch recovery.

## Learning progress transport (Implemented 2026-11-27)

Learning progress is keyed locally by lesson ID and immutable content version. The client stores `Answer`, `Notes`, `Evidence`, `Status`, and `ViewedAt` in a separate `learning_outbox`, `learning_sync_base`, and `learning_shadow`; the shared sync cursor is scoped by service. This keeps Learning writes independent of Life task and note mutations.

The HTTPS transport checks the Learning epoch, freezes each mutation for idempotent retry, applies acknowledgements and downloaded change pages with the cursor in SQLite transactions, continues past a conflict on one lesson version, and preserves queued drafts when the epoch changes. On explicit recovery it replays the current Learning feed before rebuilding pending progress mutations; rolled-back progress is compared and requeued instead of being overwritten by the restored snapshot. Old `LessonAnswer` records are copied into version 1 progress before their unsupported Life queue entries are retired. MAUI wires this transport to sign-in, manual sync and launch/resume attempts; Avalonia wires it to sign-in and manual sync, while desktop epoch recovery remains outstanding.

The shared transports accept authenticated `HttpClient` instances. Native clients use system-browser Authorization Code + PKCE, validate state and ID-token signature/issuer/audience/nonce, store only the rotating refresh token in the operating system's secure credential store, and attach short-lived access tokens to Life/Learning requests. Manual sync uploads and downloads Life state, Learning progress, immutable lesson content, and unresolved conflict snapshots. Field edits and deletion choices are new mutations tied to the conflict ID and current server revision; the server conflict stays unresolved until acknowledgement. Both clients render cached lessons with version-specific answer, notes, evidence, and source links. Desktop conflict UX, desktop epoch recovery, and live Identity/PostgreSQL recovery validation remain outstanding. Mobile sync is opportunistic at app launch/resume; desktop sync is manual. Neither is a guaranteed operating-system background worker.
