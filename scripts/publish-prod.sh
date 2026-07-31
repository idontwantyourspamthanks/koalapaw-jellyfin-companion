#!/usr/bin/env bash
# Rsync the plugin catalog (repo/) to the production Hetzner volume on jellyfin.kswapps.com.
#
# Default target is the Kamal volume mount path on the same Hetzner box as walt-home.
# Override with:
#   PROD_HOST=ryan@135.181.254.147 REPO_PATH=/path/to/volume ./scripts/publish-prod.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

PROD_HOST="${PROD_HOST:-ryan@135.181.254.147}"
REPO_PATH="${REPO_PATH:-koalapaw_storage:/rails/storage/koalapaw-repo}"

if [[ ! -f "$ROOT/repo/manifest.json" ]]; then
  echo "No repo/manifest.json — run scripts/package.sh first" >&2
  exit 1
fi

echo "==> rsync $ROOT/repo/ → ${PROD_HOST}:${REPO_PATH}/"
rsync -av --delete \
  --exclude '.git/' \
  "$ROOT/repo/" \
  "${PROD_HOST}:${REPO_PATH}/"

echo
echo "==> Done. Verify at https://jellyfin.kswapps.com/koalapaw-repo/manifest.json"
