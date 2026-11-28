#!/usr/bin/env bash
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run the restore host step through sudo." >&2; exit 1; }
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
ENV_FILE="$ROOT/local-private/production.env"
COMPOSE=(docker compose --env-file "$ENV_FILE" -f "$ROOT/deploy/compose.yaml")
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT HUP INT TERM
cat > "$WORK/incoming.tar"
tar -xf "$WORK/incoming.tar" -C "$WORK"
for db in life learning identity; do [ -s "$WORK/$db.dump" ] || { echo "Restore archive is missing $db.dump." >&2; exit 1; }; done
[ -f "$WORK/identity-data-protection.tar" ] || { echo "Restore archive is missing Identity data protection keys." >&2; exit 1; }
"$ROOT/deploy/backup.sh"
"${COMPOSE[@]}" stop caddy identity life learning || true
"${COMPOSE[@]}" up -d --wait postgres
for db in life learning identity; do
    "${COMPOSE[@]}" exec -T postgres sh -c "export PGPASSWORD=\"\$(cat /run/secrets/postgres-admin-password)\"; dropdb -h 127.0.0.1 -U personalflow_admin --if-exists $db; createdb -h 127.0.0.1 -U personalflow_admin --owner=${db}_migrator $db; psql -h 127.0.0.1 -U personalflow_admin -d postgres -c 'REVOKE CONNECT ON DATABASE $db FROM PUBLIC' -c 'GRANT CONNECT ON DATABASE $db TO $db'"
    cat "$WORK/$db.dump" | "${COMPOSE[@]}" exec -T postgres sh -c "export PGPASSWORD=\"\$(cat /run/secrets/postgres-admin-password)\"; exec pg_restore -h 127.0.0.1 -U personalflow_admin --dbname=$db --exit-on-error"
done
"${COMPOSE[@]}" run --rm --no-deps identity-migrate
cat "$WORK/identity-data-protection.tar" | "${COMPOSE[@]}" run --rm --no-deps -T --user 1654:1654 --entrypoint /bin/sh identity -ec 'find /var/lib/personalflow/data-protection -mindepth 1 -delete; tar -C /var/lib/personalflow/data-protection -xf -'
"${COMPOSE[@]}" run --rm --no-deps life-migrate --rotate-sync-epoch
"${COMPOSE[@]}" run --rm --no-deps learning-migrate --rotate-sync-epoch
"${COMPOSE[@]}" up --detach --remove-orphans --wait --wait-timeout 180
echo "Restored databases and rotated Life/Learning sync epochs."
