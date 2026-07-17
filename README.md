# KoalaPaw Server Companion

Open-source **Jellyfin plugin** for KoalaPaw: one install, two analysers (video loudness +
intro/credits). Writes Jellyfin’s own `NormalizationGain` and MediaSegments so any client —
especially KoalaPaw on Android TV — stays thin.

**Licence:** GPL-3.0 (links in-process against Jellyfin). Makes **zero outbound network calls**.

This is the skeleton: plugin loads, registers a heartbeat scheduled task, and publishes via a
Jellyfin plugin catalog (path B). Analysers come next.

Stable plugin GUID: `cea40e65-c04f-4854-b6c4-3428360087fe` (do not change).

## Requirements

- .NET 9 SDK (build)
- Jellyfin **10.11.x** on the server (`targetAbi` `10.11.0.0`)
- `zip`, `rsync`, `curl` for package/publish scripts

## Dev catalog (your Pi)

Built artefacts land in `repo/` and rsync to:

```text
/mnt/bigdrive/Content/web/koalapaw-repo/
  → https://ryansraspi.hopto.org/koalapaw-repo/manifest.json
```

```bash
./scripts/package.sh          # build DLL, zip, update repo/manifest.json
./scripts/publish-pi.sh       # rsync repo/ to the Pi (override PI_HOST / PI_REPO_PATH if needed)
```

Then in **Jellyfin Dashboard → Plugins → Repositories** add:

`https://ryansraspi.hopto.org/koalapaw-repo/manifest.json`

Install **KoalaPaw Server Companion** → restart Jellyfin → Dashboard → Scheduled Tasks should show
**KoalaPaw companion heartbeat**. Run it once; server logs should say the companion is alive.

### Republish / update

1. Bump `version` in `build.yaml` (e.g. `0.0.2.0`)
2. `./scripts/package.sh && ./scripts/publish-pi.sh`
3. Dashboard → Plugins → KoalaPaw → update / reinstall → **restart**

(Plugins load at startup — there is no hot reload.)

### Production later

Set `REPO_BASE_URL=https://repo.koalapaw.app` when packaging; host the same `repo/` layout on that
origin. The Android client will eventually write that URL via the “do it for them” flow.

## Layout

```text
src/KoalaPaw.ServerCompanion/   # .NET plugin
scripts/package.sh              # build + manifest
scripts/publish-pi.sh           # rsync to hopto web root
repo/                           # catalog output (generated; committed optional)
build.yaml                      # name, guid, version, targetAbi
```

## Related

Client blueprints live in `koalapaw-androidtv`:

- `docs/notes/koalapaw-plugin-platform.md`
- `docs/notes/do-it-for-them-ux.md`
