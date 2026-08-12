#!/usr/bin/env bash
# Rsync the plugin catalog (repo/) to the production Hetzner volume on jellyfin.kswapps.com.
#
# The catalog is served from the Docker named volume "koalapaw_repo", mounted at
# /rails/storage/koalapaw-repo inside the koalapaw-backend container (config/deploy.yml).
# On the host that volume lives at /var/lib/docker/volumes/koalapaw_repo/_data — that is
# the rsync target. SSH is root@ (Kamal's default user; there is no ryan user on the box).
#
# Override with:
#   PROD_HOST=root@135.181.254.147 REPO_PATH=/var/lib/docker/volumes/koalapaw_repo/_data ./scripts/publish-prod.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

PROD_HOST="${PROD_HOST:-root@135.181.254.147}"
REPO_PATH="${REPO_PATH:-/var/lib/docker/volumes/koalapaw_repo/_data}"

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
