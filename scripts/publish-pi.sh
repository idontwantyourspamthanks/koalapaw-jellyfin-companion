#!/usr/bin/env bash
# Rsync the local catalog (repo/) to the Pi web root.
#
# Default remote path matches:
#   /mnt/bigdrive/Content/web/koalapaw-repo  →  https://ryansraspi.hopto.org/koalapaw-repo/
#
# Override with:
#   PI_HOST=ryan@ryansraspi.local PI_REPO_PATH=/mnt/bigdrive/Content/web/koalapaw-repo ./scripts/publish-pi.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PI_HOST="${PI_HOST:-ryan@ryansraspi.hopto.org}"
PI_REPO_PATH="${PI_REPO_PATH:-/mnt/bigdrive/Content/web/koalapaw-repo}"

if [[ ! -f "$ROOT/repo/manifest.json" ]]; then
  echo "No repo/manifest.json — run scripts/package.sh first" >&2
  exit 1
fi

PI_BASE_URL="${PI_BASE_URL:-https://ryansraspi.hopto.org/koalapaw-repo}"

echo "==> rsync $ROOT/repo/ → ${PI_HOST}:${PI_REPO_PATH}/"
rsync -av --delete \
  --exclude '.git/' \
  --exclude 'manifest.json' \
  "$ROOT/repo/" \
  "${PI_HOST}:${PI_REPO_PATH}/"

manifest="$(mktemp)"
python3 "$ROOT/scripts/rewrite-manifest-urls.py" "$ROOT/repo/manifest.json" "$PI_BASE_URL" > "$manifest"
echo "==> manifest sourceUrls → $PI_BASE_URL"
rsync -av "$manifest" "${PI_HOST}:${PI_REPO_PATH}/manifest.json"
rm -f "$manifest"

echo "==> Checking public manifest"
manifest="$(mktemp)"
curl -fsS "https://ryansraspi.hopto.org/koalapaw-repo/manifest.json" -o "$manifest"
python3 - "$manifest" << 'PY'
import json, sys
latest = json.load(open(sys.argv[1]))[0]["versions"][0]
print(f"  {latest['version']}  targetAbi {latest['targetAbi']}")
print(f"  {latest['sourceUrl']}")
PY
rm -f "$manifest"
echo
echo "In Jellyfin Dashboard → Plugins → Repositories, add:"
echo "  ${PI_BASE_URL}/manifest.json"
