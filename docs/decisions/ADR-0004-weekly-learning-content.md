# ADR-0004: Weekly learning content

- **Status:** Accepted
- **Date:** 2026-11-20

## Context

The app should support regular personalized learning without a model service running for every daily lesson or answer.

## Decision

Generate lesson packs in Codex on a weekly cadence, check claims against primary sources, and publish prepared content to the server. The app displays stored lessons and records answers/progress; Codex discussion is initiated through a user-reviewed copy/share package.

## Consequences

- The server needs lesson storage and a protected publishing path, not an AI inference service.
- Lesson content needs stable IDs/versions, source links, and a correction history.
- The lesson schema, publication UI/tool, and progress export format remain Open.
