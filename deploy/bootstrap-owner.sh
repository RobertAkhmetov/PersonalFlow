#!/bin/sh
set -eu
ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
ENV_FILE="$ROOT/local-private/production.env"
"$ROOT/deploy/preflight.sh"
[ -s "$ROOT/local-private/production-secrets/owner-password" ] || { echo "The one-time owner password is missing." >&2; exit 1; }
docker compose --profile bootstrap --env-file "$ENV_FILE" -f "$ROOT/deploy/compose.yaml" run --rm identity-bootstrap
rm -f "$ROOT/local-private/production-secrets/owner-password"
echo "Owner provisioned. Read the initial password from local-private/production-secrets/owner-password, sign in through the app, enroll TOTP, and store the recovery codes offline."
