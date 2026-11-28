# Personal Flow

Personal Flow is a cross-device personal organizer with a continuous .NET learning area.

**Project status:** backend services and deployment assets are build-verified, and the backend is running on an existing VPS. HTTPS readiness is verified; owner provisioning, durable scheduled backups, and live client sign-in/sync remain outstanding.

PersonalFlow.slnx builds the .NET 11 APIs, shared core/storage and Avalonia desktop. PersonalFlow.Mobile.slnx contains the MAUI Blazor iOS/Android client separately so server builds do not require mobile workloads.

The Avalonia desktop and MAUI mobile clients support offline week/month planning, recurring rules, category-based absence periods, one-off occurrence moves/skips, and structured workout plans scheduled from the calendar with per-set logging. Both use local SQLite. Desktop and mobile support system-browser sign-in and manual Life/Learning synchronization. MAUI also supports launch/resume synchronization, conflict resolution, and explicit local-data recovery after a server epoch change; desktop conflict resolution and epoch recovery remain outstanding. Neither client has been validated against a deployed VPS.

Life and Learning include PostgreSQL sync endpoints, versioned schema migrations, and readiness checks. Identity includes OpenIddict authorization-code/PKCE and TOTP enrollment. Docker Compose, HTTPS routing, protected owner bootstrap, encrypted backups, and restore scripts are deployed or prepared. The three public HTTPS readiness endpoints respond successfully; live sign-in and synchronization are not verified. See [deployment runbook](deploy/README.md) and [implementation status](docs/implementation-status.md).

Use ~/.dotnet/dotnet if the shell resolves an older SDK. Build with `dotnet build PersonalFlow.slnx`; run desktop with `dotnet run --project src/PersonalFlow.Desktop`. Run executable checks with `dotnet run --project checks/PersonalFlow.Checks`.

See [implementation status](docs/implementation-status.md) for remaining work.

## Start here

- [Documentation map](docs/README.md)
- [VPS deployment and operations](deploy/README.md)
- [Product specification](docs/product/specification.md)
- [System overview](docs/architecture/system-overview.md)
- [API guidelines](docs/architecture/api-guidelines.md)
- [Offline and synchronization](docs/architecture/offline-sync.md)
- [Каталог экранов PersonalFlow](docs/ux/screens.html)
- [Cross-device UX](docs/ux/cross-device-flows.md)
- [Learning content lifecycle](docs/learning/content-lifecycle.md)
- [Architecture decisions](docs/decisions/README.md)
