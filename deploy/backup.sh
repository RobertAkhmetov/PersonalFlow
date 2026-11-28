#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
ENV_FILE="$ROOT/local-private/production.env"
set -a
. "$ENV_FILE"
set +a
: "${BACKUP_REMOTE:?Configure BACKUP_REMOTE in production.env}"
BACKUP_DIRECTORY="${BACKUP_DIRECTORY:-/var/backups/personalflow}"
export RCLONE_CONFIG="$ROOT/local-private/rclone.conf"
command -v age >/dev/null 2>&1 || { echo "age is required on the VPS." >&2; exit 1; }
command -v rclone >/dev/null 2>&1 || { echo "rclone is required on the VPS." >&2; exit 1; }
RECIPIENTS="$ROOT/local-private/production-secrets/backup-recipients.txt"
[ -s "$RECIPIENTS" ] || { echo "Backup encryption recipients are missing." >&2; exit 1; }
mkdir -p "$BACKUP_DIRECTORY"
chmod 700 "$BACKUP_DIRECTORY"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT HUP INT TERM
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
COMPOSE=(docker compose --env-file "$ENV_FILE" -f "$ROOT/deploy/compose.yaml")
"${COMPOSE[@]}" exec -T postgres sh -c 'export PGPASSWORD="$(cat /run/secrets/postgres-admin-password)"; exec pg_dumpall -h 127.0.0.1 -U personalflow_admin --globals-only' | age -R "$RECIPIENTS" -o "$WORK/globals.sql.age" -
IDENTITY_VOLUME="$("${COMPOSE[@]}" config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["volumes"]["identity_data_protection"]["name"])')"
docker run --rm --network none --read-only \
    --mount "type=volume,src=$IDENTITY_VOLUME,dst=/source,readonly" \
    --entrypoint tar postgres:18-alpine -C /source -cf - . \
    | age -R "$RECIPIENTS" -o "$WORK/identity-data-protection.tar.age" -
for db in life learning identity; do
    "${COMPOSE[@]}" exec -T postgres sh -c "export PGPASSWORD=\"\$(cat /run/secrets/postgres-admin-password)\"; exec pg_dump -h 127.0.0.1 -U personalflow_admin --format=custom --dbname=$db" | age -R "$RECIPIENTS" -o "$WORK/$db.dump.age" -
done
# The one-time owner bootstrap password is never included in backups.
cp -R "$ROOT/local-private/production-secrets" "$WORK/production-secrets"
rm -f "$WORK/production-secrets/owner-password"
ENCRYPTED="$BACKUP_DIRECTORY/personalflow-$STAMP.tar.age"
tar -cf - -C "$WORK" globals.sql.age life.dump.age learning.dump.age identity.dump.age identity-data-protection.tar.age production-secrets \
    -C "$ROOT/local-private" production.env \
    | age -R "$RECIPIENTS" -o "$ENCRYPTED" -
rclone copyto "$ENCRYPTED" "${BACKUP_REMOTE%/}/$(basename "$ENCRYPTED")"
find "$BACKUP_DIRECTORY" -type f -name 'personalflow-*.tar.age' -mtime +7 -delete
rclone delete --min-age 30d "$BACKUP_REMOTE"
echo "Encrypted PersonalFlow backup uploaded: $(basename "$ENCRYPTED")"
