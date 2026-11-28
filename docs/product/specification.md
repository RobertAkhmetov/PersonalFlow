# Product specification

**Status:** planning. This file is the source of truth for product scope.

## Purpose

PersonalFlow is a multi-device organizer for plans, text notes, and ongoing software-development learning. The learning area supports continuous growth in .NET; it is not a finite course.

Repository documentation describes product behavior using synthetic or anonymized examples. User-specific content is not part of tracked documentation, fixtures, or screenshots.

## Ownership and demonstrations

- The production service has one owner and may serve that owner's desktop and mobile devices. Public account registration and multi-owner tenancy are outside the current scope.
- Demonstrating the source code or a working build does not give viewers access to the owner's account or production API.
- Demonstrations use synthetic data in a local demo mode or an isolated demo environment. A demo client must not hold production credentials or connect to the production database.

## Confirmed product requirements

### Planning

- Show the upcoming week and month in clear calendar views.
- Allow editing a day plan and marking individual items complete.
- Support recurring items: daily, weekdays, weekends, selected weekdays, and selected days of a month.
- Support skipped dates and optional exclusions based on a user-defined unavailability category. Timed recurring events use the Europe/Moscow time zone, including while traveling.
- Move a task or dated occurrence to another date from calendar and task-detail views. Preserve its content, status, priority, and time when moved, including when offline.
- Make items prepared on desktop easy to find and select on the phone.
- Keep task completion and a one-off move tied to a dated occurrence of a recurring item; changing a whole series edits its recurrence rule.

### Reminders and mobile alarms

- For a task with an exact time, the default notification is at that time. The scheduled start and notification settings remain separate.
- On Android, schedule an additional local notification one hour before a timed task. Tasks without an exact time have no time-based alert. The at-time notification and the one-hour notice are separate alerts.
- Keep regular app notifications distinct from any native system alarm. Never create a ringing alarm just because a task has a time or repeats. Whether native AlarmKit/Clock integration belongs in the first release remains Open.
- Treat a native alarm, if added, as a device-local effect of a task; show whether it was scheduled and whether the platform denied or cannot provide it.
- If Android exact-alarm access is denied, show that precise timing is unavailable or may be delayed; never report the notification as precisely scheduled.
- Recommendation: hide task titles and details from lock-screen notification previews by default; let the user opt in to showing them.
- If no exact task time exists, ask for one before offering to create an alarm.

### Private text storage

- Provide a place to save personal text and messages.
- Support a self-chat-like flow and pinning important entries.

### Continuous learning

- Provide ongoing .NET learning adapted to demonstrated understanding and prior progress.
- Regular days contain about ten minutes of new theory.
- Sunday contains about five minutes of theory only; no exercise, test, coding task, or required homework.
- Lessons link to original primary sources, with links convenient to open while reading.
- A lesson may keep an answer, draft, and notes. Progress should capture evidence of understanding, not only whether a lesson was opened.
- Lesson cards focus on the topic and expected skill; exposing internal planning tags is not required.
- Discussing a lesson with Codex uses a user-reviewed copy/share package. The app itself does not call a server-side AI for daily lesson generation or discussion.

### Workouts

Store and edit a program of training days with flexible weekly frequency. See [workout plans](workouts.md) for calendar and logging scenarios.

### Platforms and data

- Target .NET 11 and use C# 15 features when they improve the code.
- Desktop: Avalonia for Windows and macOS. Linux desktop is outside the current target.
- Mobile: .NET MAUI with Blazor Hybrid for iOS and Android.
- Server: Ubuntu VPS with three coarse-grained .NET 11 Minimal API services: Identity, Life (planning and private text), and Learning.
- Deployment: Docker Compose with Caddy, the three API services, and one PostgreSQL instance.
- Server storage: PostgreSQL with a separate logical database and credentials owned by each service. Device storage: SQLite cache and outbox.
- Support basic offline access and later synchronization.
- Preserve plans, notes, workouts, learning progress, and pending offline writes across client and backend releases. Client data stays outside replaceable app binaries and is upgraded with recoverable, non-destructive schema migrations. Backend deployments back up existing database data before schema changes and do not proceed when the backup fails.
- Keep unrelated services and PostgreSQL instances isolated from PersonalFlow's data, networks, and ports when they share the VPS.
- Recommendation: route public HTTP containers on that VPS through one designated HTTP/HTTPS edge proxy, with each service's database kept on its own internal network.
- Initial distribution: a Windows portable folder with a user-level update path, a signed macOS app, Android APK, and iOS TestFlight. Windows app data stays outside replaceable binaries.

## Agreed release preferences

- Use the product name PersonalFlow and prepare the repository for public visibility. Keep secret material in ignored local files on the development computer. Only deployment-specific credentials required at runtime are provisioned to the server.
- Support iOS 16.4+ and Android API 24+. Sync at launch and resume. Support offline changes to recurrence, leave periods, pins and deletion. Background sync is opportunistic.
- Moving a recurring task affects one occurrence by default. Editing the series is a separate action.
- Schedule a local one-hour reminder on iOS as well. Use generic lock-screen previews by default.
- Offer a searchable chronological notes feed with pinned text and links. Start with text-only entries.
- After weekly content preparation in Codex, automatically validate and publish the lesson pack. Keep the last published pack available on failure.
- Automate release builds and user-level updates. Select the CI and signing workflow during release planning.

## Open product and technical decisions

- The .NET 11 solution compiles with EF Core 10/Npgsql 10, but PostgreSQL/Identity runtime operations, versioned SQL migration and restore paths, and complete MAUI builds remain compatibility gates before production release.
- Minimum supported Windows and macOS versions and desktop architectures.
- Whether and when to include native system alarms: iOS AlarmKit, Android Clock handoff, or Android app-owned alarm. These have different permission, control, and cancellation behavior.
- Minimum iOS version for AlarmKit; provide ordinary notifications on older iOS versions.
- How to handle an existing native alarm after its task is moved, skipped, or deleted.
- How users create and manage unavailability periods that suppress matching recurring items.
- Whether editing this and all following occurrences belongs in the first release.
- Lesson-pack file format and protected publishing flow.
- Ubuntu version, VPS resources, domain/DNS, backup destination and retention, deployment automation, and monitoring provider.
- Repository license; exact demo mode (local synthetic dataset or isolated staging API/database, including whether it needs multi-device synchronization).
- Windows update mechanism, macOS notarization/distribution details, and signing-key custody for all release channels.
- Local SQLite encryption and client-cache clearing on sign-out/device revocation.
- Automated build, compatibility, and release-validation matrix.
