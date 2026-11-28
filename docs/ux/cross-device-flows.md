# Cross-device UX flows

**Status:** user goals confirmed. Mobile settings includes field and delete conflicts, plus an explicit recovery action after server epoch rotation. Desktop now supports sign-in and manual sync; desktop conflict UX, epoch recovery, and device/time attribution remain Open.

## Desktop

Use desktop for longer planning, month/week review, detailed task descriptions, and extended writing or coding-related preparation. Keep keyboard navigation and shortcuts available where they improve frequent editing.

## Phone

Make the phone useful for quick decisions and capture:

- Start at a clear Today/Next view.
- Find and select a task that was planned on desktop.
- See enough task context to know what to do next.
- Change priority/date/status, mark an occurrence complete, or add a short note.
- Open and read a lesson, write or continue an answer, and save progress.
- Show sync state without blocking local work.

Use touch-friendly controls and short, focused screens. Avoid forcing dense month-grid editing onto the phone when a compact agenda or day view is clearer.

## Move a task to another day

- On desktop, a task can be dragged to another date as a shortcut. Provide an equally available keyboard-accessible command and a task-menu action.
- On a phone, use an explicit **Move** action and a compact date picker; moving must not depend on drag-and-drop.
- Keep the task's time, notes, checklist, priority, and completion state. If the task has no time, keep it unscheduled. Show an undo action after the move.
- For a recurring item, explain the scope before saving: this occurrence creates a dated exception; the whole series changes the recurrence rule. A this-and-following option remains Open.
- The move should save locally while offline and show pending-sync state.

## Resolve a sync conflict

**Implemented on mobile:** after service sync, unresolved Life and Learning conflicts are cached across launches. The comparison shows the shared base, server value, and local proposal; the owner can choose server/local values per field or edit string values (with a multiline editor for long text). For edit-versus-delete, the owner can explicitly keep the deleted state or restore the edited version. The resolution is saved as an outbox mutation tied to the conflict ID and stays visible as waiting for sync until the server acknowledges it. Origin-device labels and edit timestamps are not yet present in the API conflict contract.

1. Sync completes for all independent changes, including separate new tasks added on the same date. Divergent field edits and concurrent deletion versus editing of the same item appear in **Needs attention**; the sync indicator explains that a choice is needed.
2. The conflict card identifies the task/note, changed fields, and the device/time for each version. It shows the last shared version, the server version, and this device's version.
3. The user chooses **Use server version**, **Keep this device's version**, or **Edit a combined version**. For a long note or lesson answer, the app keeps both drafts visible beside a manual editor.
4. Save the choice as a new change against the latest server revision. Keep the conflict visible until that change syncs successfully.
5. Continue working on unrelated tasks while the conflict waits. Never silently discard either side.

## Recover after server restore

**Implemented on mobile:** when Life or Learning reports a new sync epoch, automatic sync pauses and settings offers **Сверить сервер и сохранить локальные правки**. The app archives the previous service shadow and unresolved conflict snapshots locally, resets that service cursor, and downloads the restored change history before uploading anything. It then compares the retained local cache and pending operations with the restored server state. New local mutation IDs are generated so a retry cannot collide with receipts from before the restore. Independent changes are applied; same-field and delete-versus-edit differences appear in the regular conflict inbox. Old conflict snapshots remain available as read-only references. If recovery is interrupted, its downloaded cursor and phase are saved so the same operation can resume.

The older local conflict snapshots remain on that device only. This recovery path has been build-verified but not exercised against a restored PostgreSQL database or VPS. Legacy Life queue entries without a trustworthy saved base remain pending for a separate import workflow.

## Reminders and system alarms

Keep the task's start time separate from when an alert should happen. From task details, offer a normal notification and **Set a system alarm** as separate actions. Ask for the time if the task is unscheduled and ask for platform authorization only when the user chooses the alarm action.

Show the target device and current state (scheduled, permission needed, unsupported, or handed off to Clock). Moving, skipping, or deleting a task should update any alarm the app can manage; where a platform hands the request to another Clock app and cannot confirm later changes, tell the user to review that alarm. Do not imply that an alert was set when the handoff failed.

The mobile implementation needs native platform adapters behind the MAUI client. On iOS 26 and later, AlarmKit can schedule app-owned one-time and weekly alarms after authorization; it does not add alarms to Apple's Clock app. On Android, ACTION_SET_ALARM hands a request to an alarm-clock app selected by the system and can include a time, label, and weekdays. This is a handoff, so behavior and later cancellation depend on that app. For tasks that only need a reminder, a local notification is the less intrusive default.

## Lesson to Codex handoff

1. The app autosaves the answer and notes.
2. “Discuss in Codex” prepares a package containing the lesson/version, selected question or text, the user's answer, and source links.
3. The user previews and copies/shares that package.
4. The discussion happens in Codex, not through a live AI connection inside the app.
5. The user can save a concise takeaway or “review this later” marker back in the app.

Include only the lesson, selected question or text, answer, and source links that the user previews. Preserve the draft and lesson position when they return.

## Sources inside lessons

Place links close to the relevant explanation and list all references at the end. Show the original title and, when possible, the exact section. Opening a source should not lose the lesson position or unsaved answer.

## PersonalFlow visual drafts

[Desktop week and mobile task move](mockups/planner.png) · [Mobile month, lesson and notes](mockups/mobile.png)

Use the bottom tabs for primary mobile navigation. Add a task directly from its day card. Opening the Codex share action shows an editable preview before copying or sharing. All illustrated content is synthetic.

## Interaction details for review

[Interactive task move and conflict sketch](mockups/move.html) uses synthetic data in memory.

**Recommendation.** Preserve the selected date, scroll position and keyboard focus when opening a task or returning from a lesson. When a moved task leaves the current day, announce its new date and offer Undo and Open day. A tap on a checkbox completes the task. A tap on its title opens details. Use distinct accessible names including the title. On a crowded month show a count and open the full agenda on tap. With large text, stack day cards and conflict alternatives vertically. Pair color with text for today, selection and pending changes. Offer the move command wherever drag is available. [W3C guidance on dragging](https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements.html)

**Recommendation.** Search cached notes offline. Distinguish an empty result from an incomplete cache. Tapping a pinned preview opens the original message in chronological context. Keep a new message draft when switching tabs or opening search.

On first launch offline, an empty cache should say that an initial download requires a connection. On a returning device, open the local day immediately and show its last successful sync time on demand. If a write fails locally, retain the editable input and offer retry. Show a saved indicator after SQLite commits.

## Recurrence setup and lesson continuation sketches

[Configure a series and move one occurrence](mockups/recurrence-move.png). Recommendation. Offer everyday, weekday and weekend presets alongside custom days. Display the next three effective dates after exclusions so the user can check the schedule before saving. Enabling vacation exclusions reveals a category picker. When two occurrences land in the same time slot, show their origin under the title and keep separate completion controls. An offline move confirms local storage and preserves the pending sync indicator.

[Continue a phone exercise on desktop](mockups/learning-handoff.png). Recommendation. Save the selected option, optional thought and desktop continuation marker together. Once the Learning service acknowledges them, show “Доступно на ПК”. While offline use “Появится на ПК после синхронизации”. Desktop resumes from the learning inbox at the pinned exercise version. Label the selected option and the author's free-text thought separately. Closing the exercise keeps its draft. Opening the prepared rubric is a separate action from marking practice complete.

## Finding a pinned message

[Search and chronological context on mobile](mockups/notes-search-context.png)

**Recommendation.** Combine a text query with the pinned filter. Open the result by message ID and briefly highlight its original position. Back restores the query, filter, scroll position and focused result. If the message was deleted on another device, keep the result list available and explain what changed. The composer retains its draft independently of the search. Submitting from an older date appends a new entry at the current time. Keep the reading position and offer a jump to the saved entry.

## Reviewing and sharing a lesson answer

[Self-check and editable discussion preview on desktop](mockups/lesson-self-review-share.png)

**Recommendation.** Store self-check marks with the attempt and rubric version. Each mark is the learner's assessment. Preserve the actual answer as evidence. Build the editable discussion preview from the selected lesson excerpt, answer, question and full source URL including the section anchor. Edits inside this preview affect only the outgoing text. Copy exactly the visible selection of content, confirm successful clipboard writes, and leave the text available for manual selection if copying fails. On narrow screens open a full-screen preview and return focus to the lesson action when dismissed.
