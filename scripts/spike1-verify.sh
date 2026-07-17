#!/usr/bin/env bash
# Spike 1 helper — create a throwaway API key, read NormalizationGain, optional FullRefresh.
# Usage (on the Pi, or via ssh):
#   SPIKE_TOKEN=... ./scripts/spike1-verify.sh                 # read only
#   SPIKE_TOKEN=... ./scripts/spike1-verify.sh --refresh       # FullRefresh then re-read
set -euo pipefail

ITEM_ID="${SPIKE_ITEM_ID:-0075578c-e5b0-d149-0754-08f428d6b17f}"
BASE="${JELLYFIN_URL:-http://127.0.0.1:8096}"
TOKEN="${SPIKE_TOKEN:?Set SPIKE_TOKEN to a Jellyfin API key}"

hdr=(-H "X-Emby-Token: ${TOKEN}" -H "Accept: application/json")

echo "==> GET /Items/${ITEM_ID}"
curl -fsS "${hdr[@]}" \
  "${BASE}/Items/${ITEM_ID}?fields=Path" | python3 -c '
import json,sys
d=json.load(sys.stdin)
print("Name:", d.get("Name"))
print("Type:", d.get("Type"))
print("NormalizationGain:", d.get("NormalizationGain"))
print("Id:", d.get("Id"))
'

if [[ "${1:-}" == "--refresh" ]]; then
  echo "==> POST FullRefresh replaceAllMetadata"
  curl -fsS -X POST "${hdr[@]}" \
    "${BASE}/Items/${ITEM_ID}/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=true&replaceAllImages=false"
  echo
  echo "==> waiting 15s for refresh…"
  sleep 15
  echo "==> GET again"
  curl -fsS "${hdr[@]}" \
    "${BASE}/Items/${ITEM_ID}" | python3 -c '
import json,sys
d=json.load(sys.stdin)
print("Name:", d.get("Name"))
print("NormalizationGain:", d.get("NormalizationGain"))
'
fi
