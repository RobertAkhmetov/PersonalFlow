#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
ENV_FILE="$ROOT/local-private/production.env"
set -a
. "$ENV_FILE"
set +a
: "${RESTORE_HOST:?Set RESTORE_HOST to an SSH alias for the VPS in production.env before recovery}"
ARCHIVE="${1:?Usage: deploy/restore.sh /path/to/personalflow-backup.tar.age}"
KEY="$ROOT/local-private/backup-identity.txt"
[ -s "$KEY" ] || { echo "The recovery-only age private key is missing on this Mac." >&2; exit 1; }
command -v age >/dev/null 2>&1 || { echo "Install age on this Mac." >&2; exit 1; }
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT HUP INT TERM
age -d -i "$KEY" "$ARCHIVE" | tar -xf - -C "$WORK"
age -d -i "$KEY" -o "$WORK/identity-data-protection.tar" "$WORK/identity-data-protection.tar.age"
for db in life learning identity; do
    age -d -i "$KEY" -o "$WORK/$db.dump" "$WORK/$db.dump.age"
done
tar -C "$WORK" -cf - life.dump learning.dump identity.dump identity-data-protection.tar | ssh "$RESTORE_HOST" 'sudo /opt/personalflow/deploy/restore-host.sh'
echo "Database restore finished; the server rotated both sync epochs. Devices will preserve local work and require a fresh server snapshot/recovery decision."
