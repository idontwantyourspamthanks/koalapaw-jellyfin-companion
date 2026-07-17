#!/usr/bin/env bash
# Rsync the local catalog (repo/) to the Pi web root.
#
# Default remote path matches:
#   /mnt/bigdrive/Content/web/koalapaw-repo  →  https://ryansraspi.hopto.org/koalapaw-repo/
#
# Override with:
#   PI_HOST=pi@ryansraspi.local PI_REPO_PATH=/mnt/bigdrive/Content/web/koalapaw-repo ./scripts/publish-pi.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PI_HOST="${PI_HOST:-pi@ryansraspi.hopto.org}"
PI_REPO_PATH="${PI_REPO_PATH:-/mnt/bigdrive/Content/web/koalapaw-repo}"

if [[ ! -f "$ROOT/repo/manifest.json" ]]; then
  echo "No repo/manifest.json — run scripts/package.sh first" >&2
  exit 1
fi

echo "==> rsync $ROOT/repo/ → ${PI_HOST}:${PI_REPO_PATH}/"
rsync -av --delete \
  --exclude '.git/' \
  "$ROOT/repo/" \
  "${PI_HOST}:${PI_REPO_PATH}/"

echo "==> Checking public manifest"
curl -fsS "https://ryansraspi.hopto.org/koalapaw-repo/manifest.json" | head -c 200
echo
echo
echo "In Jellyfin Dashboard → Plugins → Repositories, add:"
echo "  https://ryansraspi.hopto.org/koalapaw-repo/manifest.json"
echo "Then install \"KoalaPaw Server Companion\" and restart."
