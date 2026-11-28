# Implementation status

Snapshot: 2026-11-28. This page reports what exists in the repository and what has been verified. Requirements remain in product/ and architecture/.

## Backend implemented

- Three independently deployable .NET 11 Minimal APIs: Identity, Life, and Learning. PostgreSQL databases and credentials are isolated per service.
- Life mutation receipts, retry idempotency, field-level merge/conflict records, change feed, and sync epochs.
- Learning immutable lesson packs, source metadata and Sunday validation, plus progress mutations, conflicts, change feed, and sync epochs.
- Identity owner provisioning, password/TOTP/recovery-code login, OpenID Connect authorization code with PKCE, refresh and revocation endpoints, persisted protected keys, and no public signup.
- Append-only, embedded SQL migrations with a PostgreSQL advisory lock, checksums, a separate migrator role, and schema-aware readiness endpoints.
- Production Compose definitions for PostgreSQL, three APIs, and Caddy; HTTPS routing; container resource limits; non-root API processes; Docker secrets copied to a private tmpfs; and one-shot migration/bootstrap jobs.
- Compose networking separates the internal PostgreSQL data network, API/proxy network, and shared public-edge network. Caddy routes other HTTP apps through private site snippets; preflight rejects Docker subnet and host-port collisions. Real co-hosting has not yet been exercised on a VPS.
- Secret preparation, deployment preflight, immutable-image deploy, lesson-pack publication, encrypted off-host backup, daily systemd timer, and database/key-ring restore with sync-epoch rotation.
- GitHub Actions workflow builds the solution and is configured to publish multi-architecture API images tagged by commit SHA.

## Client progress since the backend snapshot

- Refreshed the MAUI Blazor Hybrid mobile screens for planning, learning, notes, workouts, and settings; added the PersonalFlow vector app icon and splash mark.
- Replaced the Avalonia desktop single-screen prototype with a navigable desktop shell. Its local planner shows week and month cards, edits tasks, moves individual recurring occurrences, marks completion, manages daily/weekly/monthly rules and category-based absence periods, and can restore skipped occurrences.
- Added a desktop structured workout editor for ordered exercises and per-set repetition/load targets, legacy free-text plan reading, plan duplication, resumable local sessions with immutable plan snapshots, per-set actual results and notes, and session-only extra exercises.
- Added one-off and weekly workout scheduling on desktop and mobile. Calendar cards launch or resume one stable session per linked occurrence; finishing the session updates its result and calendar completion in one SQLite transaction. Repeat links, one-off links and session occurrence metadata are accepted by the Life sync contract.
- Developer-private seed data is optional and included only by local Debug builds of the clients. The Avalonia Release item group does not include that file; public repository docs and release inputs remain free of personal seed content.
- The Avalonia desktop project built on .NET SDK 11.0.100 with zero warnings and errors after the desktop planner and workout screens were added. Windows portable publishing and macOS signing/notarization remain unverified.
- The MAUI local planner supports week/month views, task editing, completion, recurrence rules (daily, weekdays/weekends, selected weekdays, monthly date, and intervals), category-based leave exclusions, per-occurrence move/skip/completion, and vacation period create/edit/delete.
- Workout templates now store ordered exercises and individual set targets; active sessions keep a template snapshot and actual repetitions, loads, completion state, and notes. Existing line-based workout plans are still readable and can be edited into the structured format.
- Local reminders are scheduled on iOS and Android for timed one-off and recurring tasks. The app requests OS notification permission; Android also requests exact-alarm access where the OS requires it. Notifications use generic lock-screen text and are rebuilt when plans change or the app resumes.
- Optional developer-local seed data lives in the Git-ignored `local-private/` directory and imports once into device-local SQLite for local Debug builds. No seed values are stored in tracked files.
- Disabled page pinch zoom and web text auto-scaling in the mobile WebView viewport. The .NET 11 Android Debug build and iOS simulator Debug build completed with zero warnings/errors. The app was installed/launched on an iPhone 17 simulator and the planner showed the seeded recurrence/vacation counts. Physical iPhone availability and actual notification delivery have not been verified in this pass.
- Both clients use local SQLite as the offline source of truth. MAUI supports HTTPS endpoint configuration, system-browser Authorization Code + PKCE, signature/issuer/audience/nonce validation, refresh-token storage through platform secure storage, manual Life/Learning synchronization, lesson-pack fetching, quiet launch/resume sync, and a mobile conflict inbox for comparing the original, local, and server values. Field conflicts can be resolved locally and queued as a new server mutation; text values can be edited before submission. Edit-versus-delete conflicts allow the user to keep the tombstone or restore the edited version. MAUI also has explicit Life/Learning recovery after a server epoch change: it preserves the pre-recovery shadow and conflicts, downloads the current server history first, and rebuilds recoverable local writes against that snapshot. Avalonia now supports HTTPS endpoint configuration, system-browser Authorization Code + PKCE with state and ID-token validation, refresh-token storage through Windows Credential Manager or macOS Keychain, manual Life/Learning sync, lesson fetching, and local display of outbox/conflict counts. Desktop conflict resolution, epoch recovery, launch/resume sync, and live server validation remain outstanding.
- Added a local SQLite schema version, atomic startup migrations, a verified pre-migration copy, and a guard against opening a newer schema with older client code. Deployment now detects the persistent PostgreSQL volume even when the stack is stopped and requires an encrypted remote backup before upgrading an existing server database. The update/recovery and container co-location contracts are documented in [ADR-0012](decisions/ADR-0012-data-preservation-across-releases.md) and [ADR-0013](decisions/ADR-0013-co-located-containers-and-shared-ingress.md).

## Verification evidence

- `dotnet restore PersonalFlow.slnx --ignore-failed-sources` succeeded with packages available locally.
- `dotnet build PersonalFlow.slnx --no-restore` succeeded on .NET SDK 11.0.100 with zero warnings and errors after the API and migration changes.
- `dotnet run --project checks/PersonalFlow.Checks` passed domain, SQLite, workout scheduling/persistence, and eight client sync scenarios covering retries, conflict isolation, and epoch retention for Life and Learning. The optional PostgreSQL checks require `CHECK_DATABASE` and were not run.
- The .NET 11 MAUI Android Debug and iOS simulator Debug builds both succeeded with zero warnings and errors after adding the workout calendar flow. The Avalonia desktop Debug and Release builds also succeeded with zero warnings and errors.
- `dotnet build src/PersonalFlow.Mobile/PersonalFlow.Mobile.csproj -f net11.0-android -c Debug` succeeded on .NET SDK 11.0.100 with zero warnings/errors after installing Android API 37.
- The iOS simulator Debug build succeeded with .NET SDK 11.0.100 and Xcode 27.2, using `iossimulator-arm64`; the system-wide Xcode selection was left unchanged.
- After adding MAUI PKCE and manual synchronization, Android Debug and iOS Simulator Debug builds succeeded with .NET SDK 11.0.100 and zero warnings/errors. No live Identity/Life/Learning server was available for an end-to-end sign-in or data-sync run.
- After adding mobile launch/resume synchronization, Android Debug and iOS Simulator Debug builds succeeded with .NET SDK 11.0.100 and zero warnings/errors. The updated app was installed and launched on the booted iPhone 17 Simulator. Sync against a live VPS remains unverified.
- After adding the mobile Life/Learning conflict inbox and field resolution, the full API/shared/Desktop solution and Android/iOS Simulator Debug targets built with zero warnings/errors. The updated app was installed and launched on iPhone 17 Simulator. Conflict creation/resolution and stale-resolution behavior have not been exercised against a live server.
- After adding mobile edit-versus-delete resolution and the append-only Life/Learning SQL migrations, the full solution and Android/iOS Simulator Debug builds succeeded on .NET SDK 11.0.100 with zero warnings/errors. The updated app was installed and launched on the booted iPhone 17 Simulator (iOS 26.5). Conflict creation/resolution against a live database and VPS remains unverified.
- On 2026-11-28, after adding mobile sync-epoch recovery and restored-snapshot reconciliation, `PersonalFlow.slnx` and Android/iOS Simulator Debug builds succeeded on .NET SDK 11.0.100 with zero warnings/errors. The app was installed and launched on iPhone 17 Simulator. Recovery against a restored PostgreSQL database and VPS remains unverified; executable checks were not run in this pass.
- On 2026-11-28, after adding local schema snapshots and a pre-release backup guard, the full solution, Android Debug, and iOS Simulator Debug builds succeeded on .NET SDK 11.0.100 with zero warnings/errors. The updated app was installed and launched on iPhone 17 Simulator. No automated checks were run; the local migration recovery path and server backup/restore still need rehearsal before production use.
- On 2026-11-28, Compose configuration validation confirmed separate PersonalFlow ingress/data/edge networks, no published PostgreSQL host port, and only Caddy publishing 80/443. Deployment scripts pass shell syntax checks. The Docker daemon is unavailable, so Caddy snippet validation and multi-stack startup have not been run.
- On 2026-11-28, the Identity API and Avalonia desktop projects built with .NET SDK 11.0.100 and zero warnings/errors after connecting desktop PKCE sign-in, OS-secure refresh-token storage, and manual Life/Learning synchronization. Build intermediates were redirected to `/private/tmp` because the checkout's existing desktop `obj` directory is not writable in this environment. No live server sign-in, Windows Credential Manager, or macOS Keychain round trip was run.
- Shell syntax checks succeeded for deployment scripts.
- Initial SQL schemas were exported from the EF Core 10 PostgreSQL models and embedded in the service assemblies.
- The Docker daemon is not running in the local environment. Compose startup, image builds, actual backup/restore, GHCR publishing, and a VPS deployment have not been verified here. No end-to-end API/OIDC/database checks have been run.

## Still outstanding

- Deploy to a real Ubuntu VPS with real DNS, GHCR access, and an off-host backup destination; validate first boot, HTTPS renewal, owner bootstrap, backup upload, and isolated restore.
- Add and verify the next versioned SQL migrations as schema changes are made; rehearse upgrade and rollback/recovery procedures.
- Plan and validate token-signing and data-protection certificate rotation before the generated five-year certificate expires.
- Add desktop conflict resolution and sync-epoch recovery while retaining pending local work.
- Validate all online flows on Windows, macOS, iOS, and Android. Client API paths are implemented but have not been validated against a deployed server.
- Finish cross-platform desktop interaction review and publish a Windows portable build and signed/notarized macOS build; these distribution artifacts have not been produced.
- Verify notification permission and delivery behavior on physical iOS and Android devices, including Android's exact-alarm and reboot restoration paths.
- Connect the existing recurrence, vacation, workout, and reminder experiences to sync where appropriate; reminder schedules must remain device-local.
- Complete signed desktop/mobile distribution and verify the release artifact excludes developer-local profile data.

## Deployment boundary

The backend is prepared for deployment, but it has not been proven in a production environment. Both clients include API configuration and manual sync paths; a VPS is still required to validate sign-in and cross-device behavior. Desktop conflict handling and epoch recovery are not implemented yet. See the [VPS deployment runbook](../deploy/README.md).

- Added an offline Learning progress transport keyed by lesson and content version, with a separate queue and cursor, HTTPS enforcement, immutable retries, conflict isolation, and epoch retention. MAUI wires it to authentication, manual sync, and launch/resume sync attempts; Avalonia wires it to authentication and manual sync.
- Existing version 1 lesson answers migrate into the Learning queue while their unsupported old Life outbox entries are removed after the new representation is committed.
