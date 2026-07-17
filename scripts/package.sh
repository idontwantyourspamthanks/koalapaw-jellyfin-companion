#!/usr/bin/env bash
# Build the plugin zip + update repo/manifest.json for Jellyfin's catalog format.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION="$(python3 -c "import yaml,sys; print(yaml.safe_load(open('build.yaml'))['version'])" 2>/dev/null \
  || grep -E '^version:' build.yaml | head -1 | sed 's/.*"\(.*\)".*/\1/')"
TARGET_ABI="$(grep -E '^targetAbi:' build.yaml | head -1 | sed 's/.*"\(.*\)".*/\1/')"
GUID="$(grep -E '^guid:' build.yaml | head -1 | sed 's/.*"\(.*\)".*/\1/')"
NAME="$(grep -E '^name:' build.yaml | head -1 | sed 's/.*"\(.*\)".*/\1/')"
OVERVIEW="$(grep -E '^overview:' build.yaml | head -1 | sed 's/.*"\(.*\)".*/\1/')"
OWNER="$(grep -E '^owner:' build.yaml | head -1 | sed 's/.*"\(.*\)".*/\1/')"
CATEGORY="$(grep -E '^category:' build.yaml | head -1 | sed 's/.*"\(.*\)".*/\1/')"

# Public base URL of the catalog (no trailing slash). Override for production later.
REPO_BASE_URL="${REPO_BASE_URL:-https://ryansraspi.hopto.org/koalapaw-repo}"

DIST="$ROOT/dist"
OUT_DIR="$ROOT/repo/$VERSION"
ZIP_NAME="koalapaw-server-companion_${VERSION}.zip"
ZIP_PATH="$OUT_DIR/$ZIP_NAME"
DLL_DIR="$ROOT/src/KoalaPaw.ServerCompanion/bin/Release/net9.0"

echo "==> Building $NAME $VERSION (targetAbi $TARGET_ABI)"
dotnet build "$ROOT/KoalaPaw.ServerCompanion.sln" -c Release --nologo

mkdir -p "$DIST/stage" "$OUT_DIR"
rm -rf "$DIST/stage"/*
cp "$DLL_DIR/KoalaPaw.ServerCompanion.dll" "$DIST/stage/"

TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
cat > "$DIST/stage/meta.json" <<EOF
{
  "category": "$CATEGORY",
  "changelog": "See repository manifest",
  "description": "$OVERVIEW",
  "guid": "$GUID",
  "name": "$NAME",
  "overview": "$OVERVIEW",
  "owner": "$OWNER",
  "targetAbi": "$TARGET_ABI",
  "timestamp": "$TIMESTAMP",
  "version": "$VERSION"
}
EOF

rm -f "$ZIP_PATH"
(
  cd "$DIST/stage"
  zip -9 -q "$ZIP_PATH" KoalaPaw.ServerCompanion.dll meta.json
)

CHECKSUM="$(md5sum "$ZIP_PATH" | awk '{print $1}')"
SOURCE_URL="$REPO_BASE_URL/$VERSION/$ZIP_NAME"

# Jellyfin custom repos expect a JSON *array* of packages.
python3 - "$ROOT/repo/manifest.json" <<PY
import json, sys
from pathlib import Path

manifest_path = Path(sys.argv[1])
entry = {
    "guid": "$GUID",
    "name": "$NAME",
    "description": "$OVERVIEW",
    "overview": "$OVERVIEW",
    "owner": "$OWNER",
    "category": "$CATEGORY",
    "versions": [{
        "version": "$VERSION",
        "changelog": "Skeleton: registers and logs a heartbeat scheduled task.",
        "targetAbi": "$TARGET_ABI",
        "sourceUrl": "$SOURCE_URL",
        "checksum": "$CHECKSUM",
        "timestamp": "$TIMESTAMP",
    }],
}

if manifest_path.exists():
    data = json.loads(manifest_path.read_text())
    if not isinstance(data, list):
        raise SystemExit("manifest.json must be a JSON array")
else:
    data = []

found = False
for i, pkg in enumerate(data):
    if pkg.get("guid") == entry["guid"]:
        # Keep older versions; replace same version if republishing.
        versions = [v for v in pkg.get("versions", []) if v.get("version") != entry["versions"][0]["version"]]
        versions.insert(0, entry["versions"][0])
        entry["versions"] = versions
        # Preserve description fields from existing if we already had richer copy.
        entry["description"] = pkg.get("description", entry["description"])
        data[i] = entry
        found = True
        break
if not found:
    data.append(entry)

manifest_path.write_text(json.dumps(data, indent=2) + "\n")
print(f"Wrote {manifest_path}")
PY

cp "$DIST/stage/meta.json" "$OUT_DIR/meta.json"
echo "==> Packaged $ZIP_PATH"
echo "    checksum (md5) $CHECKSUM"
echo "    sourceUrl      $SOURCE_URL"
echo "    catalog        $ROOT/repo/manifest.json"
echo
echo "Next: scripts/publish-pi.sh   # rsync repo/ to the Pi"
