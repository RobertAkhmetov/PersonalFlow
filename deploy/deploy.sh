#!/bin/sh
set -eu
ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
ENV_FILE="$ROOT/local-private/production.env"
"$ROOT/deploy/preflight.sh"
compose() { docker compose --env-file "$ENV_FILE" -f "$ROOT/deploy/compose.yaml" "$@"; }
DB_VOLUME="$(compose config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["volumes"]["postgres_data"]["name"])')"
if docker volume inspect "$DB_VOLUME" >/dev/null 2>&1; then
    # A stopped stack still has valuable data. Bring only PostgreSQL up on its current
    # image, then require an encrypted off-host backup before pulling a release that
    # might apply schema migrations. A failed backup aborts the deployment via set -e.
    compose up --detach --wait --wait-timeout 120 postgres
    sudo "$ROOT/deploy/backup.sh"
fi
compose pull
compose run --rm --no-deps caddy caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
compose up --detach --remove-orphans --wait --wait-timeout 180
set -a
. "$ENV_FILE"
set +a
for url in "https://$IDENTITY_DOMAIN/.well-known/openid-configuration" "https://$APP_DOMAIN/life/health/ready" "https://$APP_DOMAIN/learning/health/ready"; do
    # Caddy reports healthy before its first ACME issuance necessarily completes.
    # Retry transient TLS alerts as well as refused connections during that window.
    curl --fail --silent --show-error --retry 20 --retry-all-errors --retry-delay 3 --max-time 8 "$url" >/dev/null
done
echo "PersonalFlow APIs are healthy. Before provisioning the owner, configure and verify the encrypted off-host backup remote."
