#!/bin/sh
set -eu
ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
ENV_FILE="$ROOT/local-private/production.env"
KEY_FILE="$ROOT/local-private/production-secrets/learning-publisher-key"
PACK="${1:?Usage: deploy/publish-lesson-pack.sh /path/to/lesson-pack.json}"
[ -r "$ENV_FILE" ] || { echo "Missing local-private/production.env." >&2; exit 1; }
[ -r "$KEY_FILE" ] || { echo "Missing Learning publisher key." >&2; exit 1; }
[ -r "$PACK" ] || { echo "Cannot read lesson pack: $PACK" >&2; exit 1; }
python3 - "$PACK" <<'PY'
import json, sys
from pathlib import Path
pack=json.loads(Path(sys.argv[1]).read_text())
if not isinstance(pack, dict) or not isinstance(pack.get('lessons'), list) or not pack['lessons']:
    raise SystemExit('Lesson pack JSON must be an object with a non-empty lessons array.')
PY
set -a
. "$ENV_FILE"
set +a
: "${APP_DOMAIN:?Set APP_DOMAIN in production.env}"
key="$(cat "$KEY_FILE")"
case "$key" in ''|*[!a-fA-F0-9]*) echo "Publisher key must contain only hexadecimal characters." >&2; exit 1;; esac
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT HUP INT TERM
umask 077
printf 'header = "X-PersonalFlow-Publisher-Key: %s"\n' "$key" > "$WORK/curl.conf"
unset key
curl --config "$WORK/curl.conf" --silent --show-error --fail-with-body --max-time 90 \
    --header 'Content-Type: application/json' --data-binary "@$PACK" \
    "https://$APP_DOMAIN/learning/v1/packs"
