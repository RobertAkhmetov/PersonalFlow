#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
PRIVATE="$ROOT/local-private"
SECRETS="$PRIVATE/production-secrets"
CADDY_SITES="$PRIVATE/caddy-sites"
if [ -e "$SECRETS" ] && [ "$(find "$SECRETS" -type f -print -quit)" ]; then
    echo "Production secrets already exist; refusing to overwrite them." >&2
    exit 1
fi
command -v openssl >/dev/null 2>&1 || { echo "OpenSSL is required." >&2; exit 1; }
command -v age-keygen >/dev/null 2>&1 || { echo "Install age on this Mac first (for example: brew install age)." >&2; exit 1; }
mkdir -p "$SECRETS" "$CADDY_SITES"
chmod 700 "$PRIVATE" "$SECRETS"
chmod 755 "$CADDY_SITES"
if [ ! -f "$CADDY_SITES/00-additional-services.caddy" ]; then
    printf '# Add one Caddy site block per additional public HTTP service.\n' > "$CADDY_SITES/00-additional-services.caddy"
    chmod 644 "$CADDY_SITES/00-additional-services.caddy"
fi
make_secret() { openssl rand -hex 32 > "$SECRETS/$1"; chmod 600 "$SECRETS/$1"; }
make_secret postgres-admin-password
make_secret life-runtime-password
make_secret life-migrator-password
make_secret learning-runtime-password
make_secret learning-migrator-password
make_secret identity-runtime-password
make_secret identity-migrator-password
make_secret learning-publisher-key
printf 'PF-%s-Aa9!\n' "$(openssl rand -hex 24)" > "$SECRETS/owner-password"
chmod 600 "$SECRETS/owner-password"

age-keygen -o "$PRIVATE/backup-identity.txt" >/dev/null 2>&1
chmod 600 "$PRIVATE/backup-identity.txt"
sed -n 's/^# public key: //p' "$PRIVATE/backup-identity.txt" > "$SECRETS/backup-recipients.txt"
chmod 600 "$SECRETS/backup-recipients.txt"

for service in life learning identity; do
    runtime_password="$(cat "$SECRETS/$service-runtime-password")"
    migrator_password="$(cat "$SECRETS/$service-migrator-password")"
    printf 'Host=postgres;Database=%s;Username=%s;Password=%s;Maximum Pool Size=10;Timeout=15;Command Timeout=30;Include Error Detail=false' \
        "$service" "$service" "$runtime_password" > "$SECRETS/$service-runtime-connection"
    printf 'Host=postgres;Database=%s;Username=%s_migrator;Password=%s;Maximum Pool Size=2;Timeout=15;Command Timeout=60;Include Error Detail=false' \
        "$service" "$service" "$migrator_password" > "$SECRETS/$service-migrator-connection"
    chmod 600 "$SECRETS/$service-runtime-connection" "$SECRETS/$service-migrator-connection"
    unset runtime_password migrator_password
done

openssl rand -hex 32 > "$SECRETS/signing-password"
chmod 600 "$SECRETS/signing-password"
CERT_TMP="$(mktemp -d)"
trap 'rm -rf "$CERT_TMP"' EXIT HUP INT TERM
openssl req -x509 -newkey rsa:3072 -nodes -keyout "$CERT_TMP/signing.key" -out "$CERT_TMP/signing.crt" -days 1825 -subj '/CN=PersonalFlow token signing' >/dev/null 2>&1
openssl pkcs12 -export -out "$SECRETS/signing.pfx" -inkey "$CERT_TMP/signing.key" -in "$CERT_TMP/signing.crt" -passout "file:$SECRETS/signing-password" >/dev/null 2>&1
chmod 600 "$SECRETS/signing.pfx"
rm -rf "$CERT_TMP"
trap - EXIT HUP INT TERM

OWNER_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
cp "$ROOT/deploy/production.env.example" "$PRIVATE/production.env"
python3 - "$PRIVATE/production.env" "$OWNER_ID" <<'PYENV'
from pathlib import Path
import sys
p=Path(sys.argv[1]); s=p.read_text().replace('OWNER_ID=replace-with-generated-guid', 'OWNER_ID='+sys.argv[2]); p.write_text(s)
PYENV
chmod 600 "$PRIVATE/production.env"
echo "Generated owner-only secret files in local-private/production-secrets."
echo "Edit local-private/production.env with the GitHub image namespace, real domains, and a published image SHA before deployment."
echo "Keep local-private/backup-identity.txt off the VPS; copy only its public recipient file."
