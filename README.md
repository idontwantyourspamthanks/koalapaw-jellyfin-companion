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
  nothing on weak matches). When an intro is found, it also cuts a short sting (up to 20 seconds)
  from that opening into `theme.mp3` on the series folder so KoalaPaw TV can loop it — skipped if
  `theme.*` or `theme-music/` is already there. Movies and collections are not given a theme this
  way. The audio is always from an episode already on the server; there are still **zero outbound
  network calls**.

Both run as scheduled tasks (daily + interval), incrementally — items/seasons that already have
results are skipped — one ffmpeg at a time at below-normal priority so a Pi stays usable.

Stable plugin GUID: `cea40e65-c04f-4854-b6c4-3428360087fe` (do not change).

## Requirements

- .NET 10 SDK (build)
- Jellyfin **12.0 or newer** on the server (`targetAbi` `12.0.0.0`)
- `jellyfin-ffmpeg` configured in Jellyfin (the plugin drives `IMediaEncoder.EncoderPath`)
- `zip`, `rsync`, `curl` for package/publish scripts

## Catalog

Built artefacts land in `repo/`. Each publish rewrites `sourceUrl` so that catalog points at itself: production at `https://jellyfin.kswapps.com/koalapaw-repo`, the Pi at `https://ryansraspi.hopto.org/koalapaw-repo`.

```bash
./scripts/package.sh          # build DLL, zip, update repo/manifest.json
./scripts/publish-prod.sh     # rsync repo/ to jellyfin.kswapps.com
./scripts/publish-pi.sh       # optional: same catalog on the Pi
```

Then in **Jellyfin Dashboard → Plugins → Repositories** add:

`https://jellyfin.kswapps.com/koalapaw-repo/manifest.json`

Install **KoalaPaw Server Companion** → restart Jellyfin → Dashboard → Scheduled Tasks should show
the KoalaPaw tasks (**video loudness scan**, **intro & credits scan**, plus a small heartbeat). Run
one once; server logs should show it analysing and writing fields.

### Republish / update

1. Bump `version` in `build.yaml` (e.g. `0.0.7.0`) and add its `changelog:` entry
2. `./scripts/package.sh && ./scripts/publish-prod.sh`
3. Dashboard → Plugins → KoalaPaw → update / reinstall → **restart**

(Plugins load at startup — there is no hot reload.)

## Layout

```text
src/KoalaPaw.ServerCompanion/
  Configuration/                # plugin configuration
  Loudness/                     # LoudnessProbe — ffmpeg loudnorm → NormalizationGain
  ThemeSongs/                   # cut theme.mp3 from a detected intro (no network)
  ScheduledTasks/               # loudness scan, intro/credits scan (+ theme backfill), heartbeat
  Segments/                     # chromaprint probe, intro/outro correlation, segment provider
scripts/package.sh              # build DLL + zip + manifest (changelog from build.yaml)
scripts/publish-prod.sh         # rsync to jellyfin.kswapps.com
scripts/publish-pi.sh           # optional rsync to the Pi
repo/                           # catalog output (generated; committed optional)
build.yaml                      # name, guid, version, targetAbi, changelog
tests/                          # xUnit tests for the pure analysis logic
```

## Related

Client blueprints live in `koalapaw-androidtv`:

- `docs/notes/koalapaw-plugin-platform.md`
- `docs/notes/do-it-for-them-ux.md`
