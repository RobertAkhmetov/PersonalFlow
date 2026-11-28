# VPS deployment and operations

This runbook prepares the Ubuntu deployment for the three .NET 11 APIs. Desktop and mobile clients now have sign-in and manual sync paths, but these have not yet been validated against a deployed VPS; see [implementation status](../docs/implementation-status.md).

## Target and public endpoints

The initial PersonalFlow-only sizing estimate is a current Ubuntu Server LTS VPS with 2 vCPU, 4 GB RAM, and 40 GB SSD. Compose limits total about 1.35 GB at steady state, before the OS, Docker, and short-lived migration jobs. Because the host may run other applications and PostgreSQL instances, include their peak memory, CPU, and disk usage when choosing the VPS; the PersonalFlow estimate alone is not enough for a shared host. Revisit limits after observing real usage. This setup is a single VPS and has no high availability.

## Constrained profile for the existing VPS

**Confirmed:** the initial deployment is being started on the existing Ubuntu 26.04 VPS with 1 vCPU, about 1 GiB RAM, and 10 GB disk (8.6 GiB filesystem, about 5.5 GiB free at inspection).

**Recommendation:** use the host-specific memory and CPU caps in `local-private/production.env` and a 512 MiB swap file. The current local profile caps steady-state containers at 768 MiB and limits PostgreSQL to 20 connections with 64 MiB shared buffers. Keep an eye on memory pressure, swap use, and free disk after deployment. This profile trades throughput and headroom for running the requested first-owner stack on the existing VPS; the 2 vCPU / 4 GB / 40 GB baseline remains the recommended production size.

**Confirmed data safeguard:** an empty first deployment may start before the off-host backup remote is configured because no database volume exists to preserve. The preflight then requires backup configuration before any later release against that volume and before owner provisioning. Do not create the owner account or store user data until an encrypted off-host backup has completed successfully.

Use two DNS names, for example `api.<your-domain>` and `auth.<your-domain>`, each pointing to the VPS. Publish only TCP 22, 80, and 443; UDP 443 is optional for HTTP/3. Do not publish PostgreSQL or API container ports. The VPS provider firewall should match the host firewall. Use SSH keys and a dedicated deployment account. Membership in the Docker group grants root-equivalent access.

PersonalFlow Caddy is the designated public edge and owns host ports 80/443. The Compose project isolates PostgreSQL on an internal `personalflow_data` network, keeps APIs on a separate proxy network, and gives the proxy a shared `personalflow_edge` network for additional public HTTP apps. Keep each other app's database on its own internal network; only its web container joins `personalflow_edge`. Add its hostname route as a `.caddy` file under `local-private/caddy-sites/`. Do not start another container publishing ports 80/443. If an existing proxy already owns those ports, `preflight.sh` stops with a conflict; first consolidate routing behind one edge proxy.

Install Docker Engine and the Compose plugin using Docker's [Ubuntu installation guide](https://docs.docker.com/engine/install/ubuntu/). Add `age` and `rclone` on both the recovery Mac and VPS. API and database logs use Docker's rotating `local` logging driver, capped at five 10 MB files per container. The host must have `curl`, `python3`, `ss` from `iproute2`, `sudo`, and `systemd` as well.

## Prepare private configuration on the Mac

Run once from the repository root:

```sh
brew install age
./deploy/prepare-secrets.sh
```

The script creates ignored `local-private/production.env`, `local-private/production-secrets/`, `local-private/caddy-sites/`, and the recovery-only `local-private/backup-identity.txt`. It generates a separate runtime and migration DB credential per API, a token-signing certificate, the lesson-publisher key, a one-time owner password, and an age recipient. It never writes these values into tracked files. Do not copy `backup-identity.txt` to the VPS; keep a second offline recovery copy outside the repository.

Set `GHCR_NAMESPACE` to the lowercase GitHub owner/organization that receives the images. Configure `APP_DOMAIN`, `IDENTITY_DOMAIN`, and `ACME_EMAIL` in `production.env`. Configure an off-host storage provider with `rclone config`, save it as `local-private/rclone.conf`, and set `BACKUP_REMOTE` to a dedicated remote directory. Ensure the remote account cannot delete unrelated data. Set `RESTORE_HOST` to an SSH alias for the VPS. Keep `rclone.conf` mode 0600; it contains storage credentials.

Before first deployment, the publisher has not yet created a release image. Push the repository to GitHub and let `.github/workflows/backend.yml` publish the three images. Make the `ghcr.io/<GHCR_NAMESPACE>/personalflow-{identity,life,learning}` packages publicly readable, or configure Docker login on the VPS with a read-only package token. Copy the full `sha-<40 lowercase hex>` tag for the commit to `IMAGE_TAG`; do not deploy `latest`.

## Prepare Ubuntu and deploy

Create `/opt/personalflow`, check out the public repository there, and install Docker Engine. Copy these from the Mac into `/opt/personalflow/local-private/` using a protected SSH session:

- `production.env`
- `rclone.conf`
- the complete `production-secrets/` directory, including `owner-password` for the one-time bootstrap

Do not copy `backup-identity.txt`. Copy `caddy-sites/` as well if other public services use the shared Caddy. On the VPS, keep the private directory private and make secret mounts readable only to the container identities that need them. Docker Compose file-backed secrets are bind mounts, so the host file permissions control access; Compose cannot remap `uid`, `gid`, and `mode` for these sources ([Docker Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets)). Run:

```sh
chmod 700 local-private local-private/production-secrets
chmod 600 local-private/production.env local-private/rclone.conf
sudo chown root:root local-private/production-secrets/*
sudo chmod 0400 local-private/production-secrets/*
for name in life-runtime-password life-migrator-password learning-runtime-password learning-migrator-password identity-runtime-password identity-migrator-password; do
  sudo chown root:70 "local-private/production-secrets/$name"
  sudo chmod 0440 "local-private/production-secrets/$name"
done
```

The six database role-password files are group-readable by GID 70, used by the `postgres:18-alpine` image ([official image definition](https://github.com/docker-library/postgres/blob/master/Dockerfile-alpine.template)); the API entrypoints read root-owned secret files and copy only their assigned values to a private tmpfs before dropping privileges. `preflight.sh` checks these owners and modes. Keep the generated owner password available on the Mac only long enough to complete the initial sign-in.

Set up Docker authentication if GHCR packages are private, confirm both DNS records and firewall rules, then run from `/opt/personalflow`:

```sh
./deploy/preflight.sh
./deploy/deploy.sh
```

The first deployment creates isolated `identity`, `life`, and `learning` databases and roles. Compose starts PostgreSQL, applies each service's versioned SQL migration as a one-shot job, then starts the APIs and Caddy. Readiness requires both a reachable database and the expected migration checksums. Preflight checks subnet overlap, host processes listening on 80/443, and port mappings from both running and stopped containers; deploy validates the imported Caddy configuration before applying the release. Caddy obtains and renews HTTPS certificates automatically; see [Caddy automatic HTTPS](https://caddyserver.com/docs/automatic-https).

The Compose PostgreSQL volume has a stable name, `personalflow_postgres_data`. Do not run `docker compose down --volumes`, delete this volume, or replace it while upgrading or troubleshooting; doing so deletes the live databases. `deploy.sh` checks whether this volume exists even if its container is stopped, starts PostgreSQL with the current image, and creates an encrypted off-host backup before pulling a release. If backup or upload fails, `set -e` stops the release before the migration step. A first deployment with no volume skips this pre-release backup because there is no existing database to preserve.

Provision the one owner account once:

```sh
./deploy/bootstrap-owner.sh
```

Use the initial password kept on the Mac to sign in to the Identity web page and enroll TOTP. Save the displayed recovery codes in a password manager or offline record. After successful provisioning the script deletes the one-time password from the VPS. Delete the local one-time copy after enrollment. There is no public signup.

## Lesson publishing

Prepare the JSON lesson pack using the `LessonPack` shape in `src/PersonalFlow.Learning.Api/LearningDb.cs`, validate its source URLs and Sunday rules, then publish it from the Mac:

```sh
./deploy/publish-lesson-pack.sh /path/to/approved-pack.json
```

The key is read from the ignored local secret file and placed in a mode-0600 temporary curl config, not a command-line argument or shell history. The API validates and stores each pack version immutably. Keep draft packs outside the public repository if they contain private progress data.

## Daily encrypted backups

The backup job dumps PostgreSQL globals and all three databases, exports the Identity data-protection key ring, packages the server config needed for recovery, encrypts the archive with `age`, uploads it with `rclone`, retains encrypted local files for seven days, and deletes remote files older than 30 days. The one-time owner password is excluded. The age private key stays on the recovery Mac.

Install the timer on Ubuntu:

```sh
sudo install -m 0644 deploy/personalflow-backup.service /etc/systemd/system/
sudo install -m 0644 deploy/personalflow-backup.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now personalflow-backup.timer
systemctl list-timers personalflow-backup.timer
```

Check `journalctl -u personalflow-backup.service` after the first scheduled run and confirm the encrypted archive appears in the configured remote. Monitor free disk and remote quota. A backup is not verified until a restore has been rehearsed in an isolated environment.

## Restore

Restore is destructive to the running databases; the host script first takes a fresh encrypted backup, then replaces database contents, restores Identity's protected key ring, rotates the Life and Learning sync epochs, and starts the stack. The epoch change makes clients retain local data and request recovery instead of silently accepting an older server snapshot.

On the recovery Mac, download the selected encrypted archive from the configured remote and run:

```sh
./deploy/restore.sh /path/to/personalflow-YYYYMMDDTHHMMSSZ.tar.age
```

The script decrypts locally and streams plaintext only through SSH to `sudo /opt/personalflow/deploy/restore-host.sh`. Keep SSH access and the age recovery key available independently of the VPS. If the recovery Mac loses its production configuration, recover it locally first with `./deploy/recover-secrets.sh /path/to/personalflow-...tar.age`; this refuses to overwrite existing production config. Recreate `rclone.conf` separately. If the recovery key is lost, these backups cannot be decrypted.

## Releases, migrations, and credential changes

Deploy only immutable CI image tags. `deploy.sh` backs up the existing database volume before pulling images and waiting for readiness, whether the stack started out running or stopped. Database migrations are embedded SQL files under each API's `Migrations/` directory. They run under the migration role, are applied under a PostgreSQL advisory lock, and their SHA-256 checksums are stored in `__PersonalFlowMigrations`. Never edit an applied file; append the next timestamped migration and deploy with the new image. Keep migrations additive and use expand-and-contract while old clients may remain offline. Client databases are stored outside the app bundles; local schema upgrades are transactional and retain a verified pre-migration snapshot.

The generated token-signing PFX is valid for five years. There is no automated certificate rotation procedure yet; schedule an explicit rotation and token/key-ring compatibility review well before expiration. Rotate DB passwords and publisher credentials by updating the relevant runtime/migrator secrets and connection strings together, then redeploy; do not edit these values ad hoc in Compose. Keep owner recovery codes separate from server backups.

## Current deployment boundary

The backend is running on the existing 1 vCPU / about 1 GiB RAM Ubuntu VPS. PostgreSQL, Identity, Life, Learning, and Caddy are healthy; versioned migrations completed, and all three public HTTPS readiness endpoints returned HTTP 200 with valid TLS certificates. The configured hostnames use nip.io because no owned DNS zone was provided.

**Confirmed data safeguard:** a pre-migration database and Identity key-ring snapshot was encrypted with age, transferred to the recovery Mac over a temporary SSH relay, and verified. That relay has been removed after deployment. No durable external backup provider or daily timer is configured, so do not provision the owner or store user data until an encrypted off-host destination is available and verified. The restore path has not been rehearsed.

Desktop/mobile authentication and API synchronization are implemented in the clients but have not been validated against the deployed services. HTTPS certificate renewal, co-hosting with another application, and isolated restore also remain unverified. The deployment uses a constrained host profile; see above for memory and CPU limits.
