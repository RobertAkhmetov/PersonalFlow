#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
ARCHIVE="${1:?Usage: deploy/recover-secrets.sh /path/to/personalflow-backup.tar.age}"
KEY="${AGE_IDENTITY:-$ROOT/local-private/backup-identity.txt}"
[ -s "$KEY" ] || { echo "Recovery-only age identity is missing. Set AGE_IDENTITY or place it at local-private/backup-identity.txt." >&2; exit 1; }
command -v age >/dev/null 2>&1 || { echo "Install age first." >&2; exit 1; }
[ ! -e "$ROOT/local-private/production.env" ] || { echo "Refusing to overwrite local-private/production.env." >&2; exit 1; }
[ ! -e "$ROOT/local-private/production-secrets" ] || { echo "Refusing to overwrite local-private/production-secrets." >&2; exit 1; }
mkdir -p "$ROOT/local-private"
chmod 700 "$ROOT/local-private"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT HUP INT TERM
age -d -i "$KEY" "$ARCHIVE" > "$WORK/backup.tar"
tar -xf "$WORK/backup.tar" -C "$WORK" production.env production-secrets
[ -s "$WORK/production.env" ] && [ -d "$WORK/production-secrets" ] || { echo "Backup does not contain the production configuration." >&2; exit 1; }
mkdir -m 700 "$ROOT/local-private/production-secrets"
cp -R "$WORK/production-secrets/." "$ROOT/local-private/production-secrets/"
cp "$WORK/production.env" "$ROOT/local-private/production.env"
chmod 600 "$ROOT/local-private/production.env" "$ROOT/local-private/production-secrets"/*
echo "Recovered production configuration locally. Recreate local-private/rclone.conf separately; it is intentionally not in backups. Never copy the age identity to the VPS."
