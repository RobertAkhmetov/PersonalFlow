# System overview

**Status:** implemented backend topology; deployed to an existing VPS with HTTPS readiness verified. Owner provisioning, durable scheduled backups, restore rehearsal, and client integration remain unverified. See [implementation status](../implementation-status.md).

## Topology

1. Avalonia desktop client for Windows and macOS.
2. .NET MAUI Blazor Hybrid mobile client for iOS and Android.
3. Caddy HTTPS ingress/reverse proxy on the Linux VPS.
4. Three independently deployable .NET 11 ASP.NET Core Minimal API services: Identity, Life, and Learning.
5. One PostgreSQL server instance with an isolated logical database and credentials for each service.
6. SQLite on each device for cached data and the offline outbox.
7. Weekly lesson packs prepared in Codex and published to the Learning service.

The production account belongs to one owner and can be used from multiple owner-controlled devices. Demonstration builds and demo data are isolated from the production API and data.

Linux is a server target, not a desktop client target. There is no separate Blazor web client in the current scope.

## Server responsibilities

The VPS needs more than the API process and database:

- Linux updates, firewall, SSH access, and a non-root application process.
- A public HTTPS endpoint and reverse proxy. Caddy is a Recommendation for simple TLS automation; Nginx is also viable.
- Docker Compose runs Caddy, Identity API, Life API, Learning API, and PostgreSQL. Only Caddy exposes public ports; PostgreSQL and service ports remain on private Compose networks.
- API configuration and secrets are mounted or injected at runtime and stay outside the repository.
- Each service applies only its own embedded, append-only SQL migrations as a one-shot release job. A PostgreSQL advisory lock serializes migrations, stored checksums detect edits to applied scripts, and API readiness verifies the applied migration set.
- Encrypted backups uploaded off-host and a scripted restore procedure; restore has not yet been rehearsed.
- Health checks, structured logs, disk/backup monitoring, and alerts.
- A protected lesson-pack publishing operation.
- One owner account with TOTP and recovery codes; public sign-up is not in scope. OIDC refresh/revocation endpoints exist, while client session management and device UI remain outstanding.

Plain-text lessons and source metadata can initially live in the relational database. Object storage is only needed if binary attachments or a substantial media library become part of the product.

No runtime AI service or remote-push worker is required for the first notification release. Android reminders are scheduled locally by each phone, so the alert can fire without network access. A remote FCM service would be a separate future feature if cross-device delivery is later required.

## Mobile reminders and alarms

- Scheduled tasks and recurrence rules remain the shared data; notifications and native alarms are device-local projections maintained by the mobile client.
- Use platform adapters behind MAUI for OS-specific behavior. iOS AlarmKit supports app-owned alarms on iOS 26 and later after authorization; it is distinct from Apple's Clock app. Android's `ACTION_SET_ALARM` intent hands alarm creation to a Clock app. An Android app-owned exact alarm is a separate option with special-access and power-use implications.
- A Clock-app handoff may not provide a stable handle for later edits or cancellation. Until verified on supported Android devices, do not promise automatic reconciliation for handed-off alarms; retain an in-app notification/reminder state and explain when the user must review Clock.
- Keep ordinary reminders available as local notifications, with platform permissions and visible fallback state. On Android 13 and later, notification permission is runtime-controlled; Android notification channels are also user-configurable.
- On Android, schedule two local alerts for a timed task: one hour before, and at its scheduled time. Use Europe/Moscow when calculating recurring occurrences. Tasks without a time do not get timed alerts.
- Rebuild local schedules after sync, task moves, recurrence changes, vacation exclusions, app updates, and device restart. Use stable identifiers per occurrence and alert type so refreshes replace schedules instead of duplicating them. If a task changes on another device while this phone is offline, its previous local alert may remain until the next sync.
- Android 13 and later requires notification permission. Precise local scheduling may also require the user-granted “Alarms & reminders” special access on Android 12 and later. Ask in context, show whether each permission is active, and explain if a notice may be delayed or suppressed. If the exact-alarm access is denied, never label an alert as precisely scheduled.
- Keep lock-screen content generic by default; show the task details after the user opens the app.

Primary platform references: [Apple AlarmKit](https://developer.apple.com/documentation/AlarmKit), [Apple AlarmKit scheduling and authorization](https://developer.apple.com/documentation/AlarmKit/scheduling-an-alarm-with-alarmkit), [Android `AlarmClock`](https://developer.android.com/reference/android/provider/AlarmClock), [Android exact-alarm guidance](https://developer.android.com/develop/background-work/services/alarms), [Android notification permission](https://developer.android.com/develop/ui/views/notifications/notification-permission).

## Safe demonstration

**Confirmed direction:** source code and app behavior may be demonstrated to other people while production remains single-owner.

**Recommendation:** support two explicit configurations: production clients use the owner's secured API; demo clients use synthetic fixtures and either a local fake service or a separately deployed staging API with its own database. Never make a production account/token available to viewers, and never use production data in demo screenshots or logs. A demo should not silently switch between production and staging based on an editable URL.

Whether the repository itself is public and whether a demo needs multi-device synchronization are Open.

## Recommended service boundaries and solution shape

**Confirmed direction:** use a small microservice architecture, kept intentionally coarse-grained for a single-owner product and a modest VPS. Keep the code in one repository and solution, but build and deploy each service as its own process/container.

- **Identity** owns the owner account, sign-in, OAuth 2.0/OpenID Connect tokens, and per-device sessions/revocation.
- **Life** owns plans, recurring tasks, leave periods, private text entries, pins, and synchronization/conflict records for those items.
- **Learning** owns lesson packs, source links, answers, notes, and learning progress.

Do not split each feature into its own service. Clients call the relevant API directly through Caddy. Caddy provides TLS and routing; it is not a separately developed API gateway. Life and Learning validate Identity-issued access tokens from cached signing metadata and do not call Identity for each request. The services do not make synchronous request chains to one another. There is no broker, Redis, Kubernetes, service mesh, or central sync service in the initial deployment. If a real cross-service workflow later needs events, add an outbox and a broker only with that requirement.

**Compose deployment:** run Caddy, Identity, Life, Learning, and PostgreSQL as five containers on one VPS. Keep database ports private, use a stable named PostgreSQL volume, health checks, non-root service users, resource limits, structured logs, and external encrypted backups. Compose keeps operations simple but does not provide high availability. Current starting limits are in `deploy/compose.yaml`; tune them after a VPS is selected and measured.

### Co-located Docker services

PersonalFlow uses three named Docker networks with separate roles:

- `personalflow_default` is the private API/ingress network. Caddy and the three APIs use it; Caddy has a fixed private address so ASP.NET accepts forwarded headers only from that proxy.
- `personalflow_data` is an internal-only network for PostgreSQL, migration/bootstrap jobs, and API database connections. Caddy and unrelated stacks do not join it, so the public proxy cannot reach the database directly.
- `personalflow_edge` is the shared reverse-proxy network. Caddy uses it to route other public HTTP services. Those services attach only their web-facing container to this network and keep their database on their own internal network. A PostgreSQL container must not join `personalflow_edge` or publish host port 5432.

PersonalFlow Caddy is the only container in this deployment that publishes host ports 80/443. Other HTTP services use distinct hostnames and site blocks in the ignored `local-private/caddy-sites/` folder; their containers do not publish 80/443. `deploy/preflight.sh` rejects any other container configured to publish those ports, even if currently stopped, checks host listeners on 80/443, and rejects an overlapping Docker subnet before a release starts. The default private subnet can be changed in `local-private/production.env` if the VPS already uses it. Compose project and volume names are PersonalFlow-scoped, so another stack can use its own `postgres` service name and volumes without sharing PersonalFlow's data. `deploy.sh` validates all imported Caddy site files before bringing up a release.

If a separate reverse proxy already owns host ports 80/443, there must still be only one designated public edge proxy. The current deployment expects PersonalFlow Caddy to own those ports; preflight stops rather than competing with an existing proxy. Configure a single shared ingress before deploying in that situation.

To expose another Compose web service through the shared Caddy, connect only that web container to the pre-created `personalflow_edge` network and place a site block in `local-private/caddy-sites/`, for example:

```caddyfile
notes.example.net {
    reverse_proxy notes-app:8080
}
```

The other Compose project can use this network declaration:

```yaml
services:
  notes-app:
    expose: ["8080"]
    networks: [private, public-edge]
  database:
    networks: [private]

networks:
  private:
    internal: true
  public-edge:
    external: true
    name: personalflow_edge
```

Do not add `ports: ["80:..."]` or `ports: ["443:..."]` to that service. Its database should join only its own private network and should not publish port 5432. Keep other services' memory/CPU limits within the VPS capacity, since Docker resource limits cap each stack but do not reserve CPU or RAM for it.

A reasonable PersonalFlow.slnx layout has separate Identity, Life, and Learning service projects, service-owned application/domain and persistence code, shared client core/storage, Avalonia desktop, and MAUI mobile projects. Keep shared server code limited to stable cross-cutting infrastructure; do not share persistence entities or domain models between services. Generate client contracts from each service's OpenAPI description. Share client-side recurrence, synchronization, and notification-planning rules only where both clients need identical behavior. Use Avalonia UI for desktop and MAUI Blazor/Razor components for mobile; do not build a shared UI abstraction across them.

**Confirmed storage:** PostgreSQL on the VPS and SQLite on each device. Each API service owns a separate logical PostgreSQL database, database role, schema, and migrations, even though all three databases run on one PostgreSQL instance. Do not use cross-service foreign keys or read another service's tables.

**EF Core implementation:** the current services use EF Core 10 with Npgsql EF Core 10 and target .NET 11. The portable solution compiles with this combination. Compilation does not establish supported runtime compatibility: PostgreSQL operations, ASP.NET Core Identity storage, migration/restore execution, and MAUI builds remain release gates. Microsoft's EF Core support table should be checked again when those runtime checks are run. Keep the product target on .NET 11. [EF Core release/support table](https://learn.microsoft.com/en-us/ef/core/what-is-new/), [Npgsql EF Core 10 release notes](https://www.npgsql.org/efcore/release-notes/10.0.html).

## Boundaries

- Clients call the relevant service APIs through Caddy; clients never connect directly to PostgreSQL.
- Each service owns its data and migrations. Server data remains the shared source of truth; clients keep local SQLite caches and outboxes for offline use.
- Personal progress/answers are separate from reusable lesson-pack content.
- Client-specific UI stays in Avalonia or MAUI/Blazor. Shared contracts and application rules can live in shared .NET libraries.
- EF Core/provider compatibility, VPS sizing and backup destination, minimum OS versions, and some product behaviors remain Open.

## Release and recovery recommendations

CI is configured to build multi-architecture service images tagged by source commit. The release workflow should deploy a specific immutable SHA tag for each service; production image publication has not yet been verified. See `deploy/README.md`. Set a shared connection budget for the three database pools after measuring available RAM. Size the host from peak resident memory during a week sync, lesson publication and a backup, alongside idle consumption. Cap log growth and keep free disk space for a database migration. If Learning is unavailable, show its cached lesson and retry independently while Life continues to accept planner writes.

Use additive schema and HTTP contract changes while older installed clients are active. Apply database migrations as a release step. Keep the previous application image usable across the migration window. A readiness probe should exercise the individual service's private database. An alert can identify a service, operation and correlation ID while omitting task titles, note bodies, answers and tokens.

Back up all service databases, required key material and the information needed to restore their roles. Keep encrypted backup copies outside the VPS and document where recovery keys can be recovered independently. Rehearse a restore in an isolated environment. Verify an owner can sign in, see a synthetic recurring exception and reopen a versioned lesson answer. Backup frequency, retention and destination depend on the owner's acceptable data-loss window.

Every release backs up an existing PostgreSQL volume before applying a schema migration, including when the stack was stopped. The release halts if the encrypted off-host backup cannot complete. Keep migrations append-only and use expand-and-contract changes while older clients may still be installed. The on-device client database has a schema version, transactional upgrades and an integrity-checked pre-upgrade copy; mobile epoch recovery reconciles local work after a server restore. Desktop supports manual server sync, but still needs conflict resolution and epoch recovery. Until an offline change syncs successfully, its server copy does not exist. A device loss is outside the protection provided by app upgrades or server backups.
