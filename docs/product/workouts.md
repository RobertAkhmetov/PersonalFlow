# Workout plans

**Confirmed.** Store and edit a personal program with multiple training days and variable weekly frequency. Real programs belong in private storage.

**Recommendation.** Life owns the program, calendar assignments and workout log. A training day is a reusable ordered template. A calendar occurrence assigns a day to a date. Starting a session snapshots the exercises and targets. Later template edits apply to future starts. An active session or historical result retains its original targets.

## Program editor

Create, rename, copy, reorder and archive days. Within each day add, replace, reorder and remove exercises. Offer move up/down alongside dragging. Support a common target for all sets and individual set targets. An exercise can use repetitions, a range, time, distance or a free-text goal. Load is optional. Record the unit and basis when known (per implement, total or machine setting). Accept decimal comma and point. Preserve original free text and unknown units. Keep blank loads distinct from zero.

## Calendar and logging

Schedule weekly or once, or start an unscheduled session. Leave the schedule empty until dates are chosen. Adding an extra session does not change the recurring schedule. Moving or skipping one occurrence preserves the series. On archive show affected future assignments and offer to cancel or replace them. Use the planner's local alerts for timed sessions.

On mobile show target, previous comparable performance and actual entry separately. Mark sets, change actual weight/reps, skip a set or add an exercise for this session only. Persist each edit. On restart offer to resume at the last exercise. On early finish summarize completed and skipped sets. A calendar completion without details records attendance only. History supports lookup by date and exercise and explicit corrections. Compare like units, equipment and load basis.

## Offline behavior and acceptance

Save each draft change and its outgoing operation in one SQLite transaction. Bind the session to a stable occurrence ID. Repeated starts from the calendar converge on the same session. For parallel offline starts match planned sets by occurrence and template set identity. Preserve independent extra sets by their own IDs. Divergent edits to the same field use the existing conflict view. Completing a linked session updates its calendar state in the same Life transaction.

Verify these scenarios during implementation: an extra session leaves the next appointment intact, editing targets preserves a historical snapshot, restarting offline restores a partially completed set, retrying a finish creates one result, and a free-text exercise can be completed without inventing numeric values.

## Client implementation status

**Implemented (2026-11-26):** desktop and mobile can schedule a training day once or weekly, with Moscow time, selected weekdays, interval and optional category-based vacation exclusion. Calendar entries open a stable workout session; an unfinished session resumes instead of creating a duplicate. Finishing a linked session saves its result and marks the one-off plan item or recurring occurrence complete in one local SQLite transaction. Moving an appointment keeps its workout link. Removing a training template leaves its calendar dates as ordinary tasks and preserves existing session snapshots.

The Avalonia client can create, edit, duplicate and delete training days; reorder exercises; add and remove set targets; and save repetitions, decimal load, unit and exercise notes as a versioned structured plan. Older line-based plans remain readable. Session views record actual repetitions, load, completion and notes, and allow an extra exercise in the active session without changing the reusable program. MAUI keeps its mobile structured editor and session history.

Previous-performance comparison and set reordering are not implemented. The desktop UI does not yet offer detailed correction of a finished result. These remain open implementation work and must not be inferred from this status note.
