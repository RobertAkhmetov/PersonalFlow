#!/bin/sh
set -eu
ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
ENV_FILE="$ROOT/local-private/production.env"
SECRETS="$ROOT/local-private/production-secrets"
[ -f "$ENV_FILE" ] || { echo "Missing local-private/production.env; run deploy/prepare-secrets.sh and configure it." >&2; exit 1; }
[ -d "$SECRETS" ] || { echo "Missing local-private/production-secrets." >&2; exit 1; }
[ "$(stat -c '%a' "$ROOT/local-private")" = "700" ] || { echo "local-private must have mode 0700." >&2; exit 1; }
[ "$(stat -c '%a' "$SECRETS")" = "700" ] || { echo "production-secrets must have mode 0700." >&2; exit 1; }
[ "$(stat -c '%a' "$ENV_FILE")" = "600" ] || { echo "production.env must have mode 0600." >&2; exit 1; }
CADDY_SITES="$ROOT/local-private/caddy-sites"
mkdir -p "$CADDY_SITES"
chmod 755 "$CADDY_SITES"
if [ ! -f "$CADDY_SITES/00-additional-services.caddy" ]; then
    printf '# Add one Caddy site block per additional public HTTP service.\n' > "$CADDY_SITES/00-additional-services.caddy"
    chmod 644 "$CADDY_SITES/00-additional-services.caddy"
fi
set -a
. "$ENV_FILE"
set +a
PRIVATE_SUBNET="${PRIVATE_SUBNET:-172.30.19.0/24}"
PROXY_ADDRESS="${PROXY_ADDRESS:-172.30.19.10}"
export PRIVATE_SUBNET PROXY_ADDRESS
: "${GHCR_NAMESPACE:?Set GHCR_NAMESPACE in production.env}"
: "${APP_DOMAIN:?Set APP_DOMAIN in production.env}"
: "${IDENTITY_DOMAIN:?Set IDENTITY_DOMAIN in production.env}"
: "${ACME_EMAIL:?Set ACME_EMAIL in production.env}"
for name in postgres-admin-password life-runtime-password life-migrator-password learning-runtime-password learning-migrator-password identity-runtime-password identity-migrator-password life-runtime-connection life-migrator-connection learning-runtime-connection learning-migrator-connection identity-runtime-connection identity-migrator-connection signing.pfx signing-password learning-publisher-key backup-recipients.txt; do
    [ -s "$SECRETS/$name" ] || { echo "Missing or empty secret file: $name" >&2; exit 1; }
done
for name in postgres-admin-password life-runtime-connection life-migrator-connection learning-runtime-connection learning-migrator-connection identity-runtime-connection identity-migrator-connection signing.pfx signing-password learning-publisher-key backup-recipients.txt; do
    [ "$(stat -c '%u:%a' "$SECRETS/$name")" = "0:400" ] || { echo "Secret $name must be root-owned with mode 0400." >&2; exit 1; }
done
for name in life-runtime-password life-migrator-password learning-runtime-password learning-migrator-password identity-runtime-password identity-migrator-password; do
    [ "$(stat -c '%u:%g:%a' "$SECRETS/$name")" = "0:70:440" ] || { echo "Database role secret $name must be root:70 with mode 0440 for postgres:18-alpine." >&2; exit 1; }
done
case "${GHCR_NAMESPACE:-}" in *replace*|*[!a-z0-9._-]*) echo "Set GHCR_NAMESPACE to the lowercase GitHub owner or organization." >&2; exit 1;; esac
case "${APP_DOMAIN:-}" in *.example.net|*replace*) echo "Set APP_DOMAIN to a real DNS name." >&2; exit 1;; esac
case "${IDENTITY_DOMAIN:-}" in *.example.net|*replace*) echo "Set IDENTITY_DOMAIN to a real DNS name." >&2; exit 1;; esac
case "${ACME_EMAIL:-}" in *@*.*) :;; *) echo "Set ACME_EMAIL to a valid address." >&2; exit 1;; esac
case "${ACME_EMAIL:-}" in *example.net|*replace*) echo "Replace the ACME_EMAIL placeholder." >&2; exit 1;; esac
[ "$APP_DOMAIN" != "$IDENTITY_DOMAIN" ] || { echo "APP_DOMAIN and IDENTITY_DOMAIN must be different hostnames." >&2; exit 1; }
python3 - "$OWNER_ID" "$IMAGE_TAG" <<'PYVALID'
import ipaddress, re, sys
if not re.fullmatch(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", sys.argv[1]):
    raise SystemExit("OWNER_ID must be the generated GUID.")
if not re.fullmatch(r"sha-[0-9a-f]{40}", sys.argv[2]):
    raise SystemExit("IMAGE_TAG must be an immutable sha-<40 hex> GHCR tag.")
network=ipaddress.ip_network(__import__("os").environ.get("PRIVATE_SUBNET", "172.30.19.0/24"), strict=True)
proxy=ipaddress.ip_address(__import__("os").environ.get("PROXY_ADDRESS", "172.30.19.10"))
if proxy not in network or proxy in (network.network_address, network.broadcast_address):
    raise SystemExit("PROXY_ADDRESS must be a usable IP inside PRIVATE_SUBNET.")
PYVALID
[ ! -f "$ROOT/local-private/backup-identity.txt" ] || { echo "The age private key must remain on the recovery Mac, not on the VPS." >&2; exit 1; }
command -v docker >/dev/null 2>&1 || { echo "Docker Engine with the Compose plugin is required." >&2; exit 1; }
command -v ss >/dev/null 2>&1 || { echo "ss from iproute2 is required to check host listeners on ports 80/443." >&2; exit 1; }
docker compose version >/dev/null
docker info >/dev/null
compose() { docker compose --env-file "$ENV_FILE" -f "$ROOT/deploy/compose.yaml" "$@"; }
compose config --quiet
DB_VOLUME="$(compose config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["volumes"]["postgres_data"]["name"])')"
if docker volume inspect "$DB_VOLUME" >/dev/null 2>&1; then
    [ -f "$ROOT/local-private/rclone.conf" ] || { echo "This deployment already has a database volume. Configure local-private/rclone.conf before deploying or bootstrapping the owner." >&2; exit 1; }
    [ "$(stat -c '%a' "$ROOT/local-private/rclone.conf")" = "600" ] || { echo "rclone.conf must have mode 0600." >&2; exit 1; }
    case "${BACKUP_REMOTE:-}" in *replace*|"") echo "Set BACKUP_REMOTE to the configured encrypted backup destination." >&2; exit 1;; esac
    command -v age >/dev/null 2>&1 || { echo "age is required for encrypted backups and restores." >&2; exit 1; }
    command -v rclone >/dev/null 2>&1 || { echo "rclone is required for off-host backups." >&2; exit 1; }
else
    echo "Fresh PersonalFlow install: no database volume exists, so no pre-release backup is needed. Configure and verify off-host backups before provisioning the owner account."
fi
python3 - "$PRIVATE_SUBNET" "$PROXY_ADDRESS" <<'PYNETWORK'
import ipaddress, json, subprocess, sys

requested = ipaddress.ip_network(sys.argv[1], strict=True)
proxy = ipaddress.ip_address(sys.argv[2])
if proxy not in requested or proxy in (requested.network_address, requested.broadcast_address, requested.network_address + 1):
    raise SystemExit("PROXY_ADDRESS must be a usable non-gateway address inside PRIVATE_SUBNET.")

ids = subprocess.check_output(["docker", "network", "ls", "-q"], text=True).split()
networks = json.loads(subprocess.check_output(["docker", "network", "inspect", *ids], text=True)) if ids else []
for network in networks:
    name = network.get("Name", "")
    subnets = [ipaddress.ip_network(item["Subnet"], strict=False) for item in (network.get("IPAM", {}).get("Config") or []) if item.get("Subnet")]
    if name == "personalflow_default":
        if requested not in subnets:
            raise SystemExit(f"Existing PersonalFlow network uses {subnets}, not {requested}; do not replace a live network automatically.")
        continue
    for subnet in subnets:
        if requested.version == subnet.version and requested.overlaps(subnet):
            raise SystemExit(f"PRIVATE_SUBNET {requested} overlaps Docker network '{name}' ({subnet}). Choose an unused subnet in local-private/production.env.")

ids = subprocess.check_output(["docker", "ps", "-aq"], text=True).split()
containers = json.loads(subprocess.check_output(["docker", "container", "inspect", *ids], text=True)) if ids else []
for container in containers:
    labels = container.get("Config", {}).get("Labels") or {}
    if labels.get("com.docker.compose.project") == "personalflow" and labels.get("com.docker.compose.service") == "caddy":
        continue
    ports = container.get("HostConfig", {}).get("PortBindings") or {}
    collision = any(binding.get("HostPort") in {"80", "443"} for bindings in ports.values() if bindings for binding in bindings)
    if collision:
        name = container.get("Name", "").lstrip("/")
        raise SystemExit(f"Container '{name}' already publishes host port 80 or 443. Use one shared public reverse proxy before deploying PersonalFlow.")
PYNETWORK
OWN_CADDY="$(docker compose --env-file "$ENV_FILE" -f "$ROOT/deploy/compose.yaml" ps -q caddy)"
if [ -z "$OWN_CADDY" ]; then
    HOST_HTTP_LISTENERS="$(ss -H -lntu | awk '$5 ~ /:(80|443)$/ { print $5 }')"
    [ -z "$HOST_HTTP_LISTENERS" ] || { echo "Host ports 80/443 are already listening on: $HOST_HTTP_LISTENERS. Use one shared public reverse proxy before deploying PersonalFlow." >&2; exit 1; }
fi
echo "Preflight passed. Ensure DNS A/AAAA records point to this VPS and firewall allows TCP 80/443 before deployment."
