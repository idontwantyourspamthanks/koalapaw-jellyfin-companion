#!/usr/bin/env bash
# Build the plugin, package it, and rsync the versioned artifact to the LAN server.
#
#   ./scripts/deploy-lan.sh
#
# Override the target with:
#   LAN_HOST=ryan@192.168.68.53 LAN_REPO_PATH=/mnt/bigdrive/Content/web/koalapaw-repo ./scripts/deploy-lan.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LAN_HOST="${LAN_HOST:-ryan@192.168.68.53}"
LAN_REPO_PATH="${LAN_REPO_PATH:-/mnt/bigdrive/Content/web/koalapaw-repo}"

# 1. Build + package (zip, meta.json, manifest.json)
"$ROOT/scripts/package.sh"

VERSION="$(python3 -c "import yaml; print(yaml.safe_load(open('$ROOT/build.yaml'))['version'])")"
OUT_DIR="$ROOT/repo/$VERSION"

if [[ ! -d "$OUT_DIR" ]]; then
  echo "No repo/$VERSION — package.sh did not produce output" >&2
  exit 1
fi

# 2. Rsync the versioned directory + manifest to the LAN server
echo "==> rsync repo/$VERSION/ → ${LAN_HOST}:${LAN_REPO_PATH}/${VERSION}/"
rsync -av "$OUT_DIR/" "${LAN_HOST}:${LAN_REPO_PATH}/${VERSION}/"

echo "==> rsync manifest.json → ${LAN_HOST}:${LAN_REPO_PATH}/manifest.json"
rsync -av "$ROOT/repo/manifest.json" "${LAN_HOST}:${LAN_REPO_PATH}/manifest.json"

echo
echo "==> Deployed $VERSION to ${LAN_HOST}:${LAN_REPO_PATH}/${VERSION}/"
echo "    In Jellyfin Dashboard → Plugins → check for update or reinstall."
