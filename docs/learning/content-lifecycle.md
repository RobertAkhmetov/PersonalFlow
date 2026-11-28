# Learning content lifecycle

**Confirmed:** weekly lesson authoring in Codex; no daily AI service connected to the VPS.

**Status:** backend contract, publishing CLI, separate offline Learning progress transport, immutable local lesson cache, cached-lesson screens, MAUI account sign-in/manual and launch/resume sync, and mobile progress-conflict comparison/resolution are implemented. Avalonia also supports account sign-in and manual Learning sync. Weekly authoring/review remains manual; desktop conflict resolution and epoch recovery remain outstanding.

## Weekly workflow

1. Prepare a compact progress summary from the app. This export is a Recommendation and should include only the learning state needed to select the next topics and difficulty.
2. Draft the next week's lesson pack in Codex.
3. Check technical claims against primary sources and verify every link.
4. Review the Sunday lesson separately: about five minutes of theory only, with no exercise, test, coding task, or required homework.
5. Publish the approved pack to the server through a protected import/publishing path.
6. Clients download the new pack and retain previously completed lesson records.

The app stores versioned answers, notes, evidence, and reading state locally and can synchronize them through an authenticated Learning API transport. Desktop and mobile can browse cached lesson versions offline, open their original source links, and keep progress attached to the selected version. MAUI can sign in through the system browser and manually synchronize Life and Learning, including published lesson downloads. These paths have not been exercised against a live VPS. The app does not generate daily questions or grade responses with a server-side model.

## Lesson content contract

A lesson should carry enough metadata to be searchable and render consistently:

- Stable lesson ID and content version.
- Planned date/week and topic.
- Estimated theory duration.
- Theory with examples and senior-level mechanisms/trade-offs.
- Practice or diagnostic prompt on non-Sunday lessons, when appropriate.
- A transparent self-check rubric or explanation of what a strong answer should cover.
- Source references: title, original URL, relevant section/anchor, and date checked.
- Optional revision note when a published lesson is corrected.

The API accepts an immutable `LessonPack` JSON payload at `POST /v1/packs`; `deploy/publish-lesson-pack.sh` sends it to the protected publisher endpoint. A lesson may include `selfCheck`, shown separately from theory; it is optional for backward compatibility with already-published pack versions. Pack data and private progress use separate tables. See the record types in `src/PersonalFlow.Learning.Api/LearningDb.cs` for the current wire shape.

## Evidence and discussion

Progress should distinguish reading from demonstrated understanding. Useful evidence includes an explanation of a mechanism, a decision under constraints, diagnosis of a failure, or a defended trade-off.

The app can store the user's draft and a short result from a Codex discussion. Detailed interactive discussion remains in Codex and uses the user-reviewed copy/share package described in cross-device-flows.md.

Use synthetic or anonymized examples in public lesson packs. Keep real-user content out of tracked documentation and fixtures.

## Recommended continuation and publication behavior

A phone exercise can ask the reader to predict a result, choose a diagnosis and reveal its rationale. Save the choice and optional explanation separately. A recognition question provides evidence for that question. Deeper practical work can be marked “Продолжить на ПК”. On desktop open the same lesson version, exercise and draft. Preserve a pending handoff locally when the phone is offline. Identify the next action in the desktop learning view after sync. Keep Sunday theory-only.

The API stores immutable pack versions and a content digest, validates lesson IDs, versions, dates, HTTPS source URLs, size and Sunday constraints, and returns the prior receipt for an identical retry. New versions do not overwrite old lessons. The present schema stores lesson rows and pack rows; it does not yet model an active-pack pointer or exercise-level IDs. Publication tooling and client download/progress synchronization should evolve together.

## Client progress synchronization

**Implemented:** local progress is keyed by `(LessonId, ContentVersion)` and stores answer, notes, evidence, status, and viewed time. Its independent SQLite queue uses stable mutation IDs, frozen retries, a service-scoped cursor, conflict isolation, and sync-epoch recovery. Existing version 1 `LessonAnswer` records migrate before their old unsendable Life queue entries are removed.

**Open:** implement desktop system-browser sign-in and OS-secure token storage plus manual or automatic sync, validate both clients against the deployed services, and add recovery for deletion and sync-epoch conflicts. Mobile launch/resume sync is opportunistic and does not run as a guaranteed OS background worker.
