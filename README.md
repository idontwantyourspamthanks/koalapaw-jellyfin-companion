# KoalaPaw Server Companion

Open-source **Jellyfin plugin** for KoalaPaw: one install, two analysers (video loudness +
intro/credits). Writes Jellyfin’s own `NormalizationGain` and MediaSegments so any client —
especially KoalaPaw on Android TV — stays thin.

**Licence:** GPL-3.0 (links in-process against Jellyfin). Makes **zero outbound network calls**.

Everything runs on the server against your existing library — no external services:

- **Loudness scan** — measures integrated loudness with jellyfin-ffmpeg `loudnorm` and writes
  Jellyfin’s standard `NormalizationGain` field (ReplayGain 2.0: `-18 − LUFS`) on movies/episodes
  that don’t have one yet.
- **Intro & credits scan** — chromaprint-fingerprints episode openings and endings, correlates a
  season’s episodes, and writes Jellyfin `Intro`/`Outro` MediaSegments when it’s confident (it emits
  nothing on weak matches).

Both run as scheduled tasks (daily + interval), incrementally — items/seasons that already have
results are skipped — one ffmpeg at a time at below-normal priority so a Pi stays usable.

Stable plugin GUID: `cea40e65-c04f-4854-b6c4-3428360087fe` (do not change).

## Requirements

- .NET 9 SDK (build)
- Jellyfin **10.11.x** on the server (`targetAbi` `10.11.0.0`)
- `jellyfin-ffmpeg` configured in Jellyfin (the plugin drives `IMediaEncoder.EncoderPath`)
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
the KoalaPaw tasks (**video loudness scan**, **intro & credits scan**, plus a small heartbeat). Run
one once; server logs should show it analysing and writing fields.

### Republish / update

1. Bump `version` in `build.yaml` (e.g. `0.0.7.0`) and add its `changelog:` entry
2. `./scripts/package.sh && ./scripts/publish-pi.sh`
3. Dashboard → Plugins → KoalaPaw → update / reinstall → **restart**

(Plugins load at startup — there is no hot reload.)

### Production later

Set `REPO_BASE_URL=https://repo.koalapaw.app` when packaging; host the same `repo/` layout on that
origin. The Android client will eventually write that URL via the “do it for them” flow.

## Layout

```text
src/KoalaPaw.ServerCompanion/
  Configuration/                # plugin configuration
  Loudness/                     # LoudnessProbe — ffmpeg loudnorm → NormalizationGain
  ScheduledTasks/               # loudness scan, intro/credits scan, heartbeat
  Segments/                     # chromaprint probe, intro/outro correlation, segment provider
scripts/package.sh              # build DLL + zip + manifest (changelog from build.yaml)
scripts/publish-pi.sh           # rsync to hopto web root
repo/                           # catalog output (generated; committed optional)
build.yaml                      # name, guid, version, targetAbi, changelog
tests/                          # xUnit tests for the pure analysis logic
```

## Related

Client blueprints live in `koalapaw-androidtv`:

- `docs/notes/koalapaw-plugin-platform.md`
- `docs/notes/do-it-for-them-ux.md`
