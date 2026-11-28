# ADR-0007: Offline conflict resolution

- **Status:** Accepted for same-field and edit-versus-delete conflict behavior, plus mobile recovery after a server sync-epoch change
- **Date:** 2026-11-20

## Context

The owner edits data on multiple devices, sometimes offline. The user confirmed that conflicting changes must be shown for resolution. Silent last-write-wins behavior could discard plans, notes, or lesson answers. The user clarified that a conflict concerns competing changes to the same existing task; independent tasks added on one date must be preserved and merged.

## Decision

- Persist local mutations in an idempotent outbox with a stable mutation ID and the server revision from which the edit was made.
- Automatically merge edits to different fields of the same task.
- Surface a conflict when both sides change the same field of the same existing task to different values. Preserve the base, server, and local values for the owner to resolve.
- Treat independent new tasks as separate inserts, including multiple additions on the same day or at the same position. Merge them as distinct IDs and give them a canonical order after sync; do not deduplicate by content. A stable mutation ID only prevents replaying the same insert.
- Continue syncing unrelated mutations while conflicts wait.
- Resolve through an explicit choice to use the server value, keep the local value, or edit a combined value. For long text, keep both versions and require manual review.
- Save the resolution as a new mutation against the latest revision. Keep the conflict open until that mutation syncs.
- When a server restore rotates its sync epoch, pause automatic writes until the owner starts recovery. Preserve the pre-recovery local shadow and conflict snapshots, download the current change history from the beginning, then rebuild recoverable writes with new mutation IDs. Compare each recovered write against the saved base and the restored server body; independent fields merge, while same-field or delete-versus-edit differences create normal conflicts. Keep old conflict snapshots read-only on the device.

## Consequences

- Life owns task, note, and conflict synchronization. Learning owns lesson answers and learning progress sync. There is no central sync service.
- The sync API needs per-mutation outcomes, stable retries, and conflict payloads rather than failing an entire batch because one item conflicts. C# 15 union types are a candidate for modeling internal outcomes exhaustively; serialize them to an explicit versioned API DTO rather than exposing domain types directly.
- Clients need a durable **Needs attention** view and must retain unresolved data.
- A restore must not replay a frozen pre-restore request or silently overwrite the restored server. Recovery needs its own local snapshot phase so it can resume after interruption, plus a server comparison mode that does not trust old revision numbers.
- Exact offline support for deletes/rule edits, conflict-history retention, and a few occurrence-specific edge cases remain Open; see offline-sync.md.
