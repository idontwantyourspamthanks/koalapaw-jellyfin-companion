#!/usr/bin/env python3
"""Rewrite manifest sourceUrl hosts so each catalog points at itself.

package.sh stores one URL in repo/manifest.json. publish-pi.sh and publish-prod.sh
each upload their own copy, with every zip URL on that host.
"""
import json
import sys
from pathlib import Path
from urllib.parse import urlparse

if len(sys.argv) != 3:
    raise SystemExit("usage: rewrite-manifest-urls.py manifest.json https://host/koalapaw-repo")

manifest_path, base = sys.argv[1], sys.argv[2].rstrip("/")
data = json.loads(Path(manifest_path).read_text())
marker = "/koalapaw-repo/"

for pkg in data:
    for version in pkg.get("versions", []):
        url = version.get("sourceUrl", "")
        path = urlparse(url).path
        idx = path.find(marker)
        if idx == -1:
            raise SystemExit(f"unexpected sourceUrl {url}")
        version["sourceUrl"] = base + "/" + path[idx + len(marker):]

json.dump(data, sys.stdout, indent=2)
sys.stdout.write("\n")
