# Intro & Outro dashboard panel — design (0.0.11.0)

## Problem

Intro/outro detection is opaque. After a scan the admin can see only:

- a run-wide aggregate (`GET /KoalaPaw/Segments/Report`: episodes scanned, intros/outros
  found, mean confidence — `AnalysisStore.SegmentScanReport`),
- the failure list (`GET /KoalaPaw/Failures/List`), mixed with loudness failures,
- Jellyfin's Scheduled Tasks page for progress while a run is live.

There is no way to answer "what did the detector decide for *this* show/season/episode, and
how sure was it?" Per-season confidence is computed in `IntroCorrelator.GuessIntro/GuessOutro`
and logged, then discarded. Successes exist only implicitly as MediaSegments in Jellyfin.

## Goal

A dedicated plugin dashboard page where the admin can:

1. Trigger the segment scan and watch live progress.
2. Browse Library → Series → Season → Episode and see, per item:
   - whether an Intro/Outro is in place, and its exact placement (start–end),
   - the detection confidence,
   - failure state and reason, or "not yet scanned".
3. Re-scan a specific season on demand (clear its failures, force re-analysis).

Non-goal: changing the detection algorithm itself. This release is observability.

## Where the data comes from

Per-episode status is a join of three existing sources plus one new one:

| Fact | Source |
|---|---|
| Intro/Outro placement (ticks) | `IMediaSegmentManager.GetSegmentsAsync(ep, [Intro, Outro], opts, filterByProvider: true)` — ground truth, what's actually staged |
| Failure state + reason | `AnalysisStore.GetFailures()` (analyser = `"segments"`) |
| Last-run aggregate | `AnalysisStore.GetSegmentScanReport()` (unchanged, TV app still reads it) |
| **Per-season confidence + outcome** | **new: persisted per-season scan records** |

### New store: per-season scan records

`SegmentScanTask` currently folds each `SeasonAnalysisResult` into the run aggregate and
loses it. We persist one record per season per run instead.

Extend `AnalysisStore` (same JSON-file + dirty-flag + timer-flush pattern as failures and
the report — third file, `season-scans.json`, in the plugin data folder):

```csharp
public sealed class SeasonScanRecord
{
    public Guid SeasonId { get; set; }
    public Guid SeriesId { get; set; }        // captured at scan time; series survives season merges
    public DateTime FinishedUtc { get; set; }
    public int EpisodesSampled { get; set; }
    public SeasonOutcome Outcome { get; set; } // Detected | Partial | NoCorrelation | FingerprintFailures | Error
    public double? IntroEndSeconds { get; set; }
    public double? IntroConfidence { get; set; }
    public double? OutroLengthSeconds { get; set; }
    public double? OutroConfidence { get; set; }
}

public enum SeasonOutcome { Detected, Partial, NoCorrelation, FingerprintFailures, Error }
```

`Dictionary<Guid, SeasonScanRecord>` keyed by season, overwritten each run — the panel shows
*latest known state*, not history. `IntroSeasonAnalyser.AnalyseAndStageAsync` already computes
everything needed; `SeasonAnalysisResult` gains the guess windows (it currently returns only
confidence) and the task maps it to a record. The existing run-wide `SegmentScanReport`
stays exactly as-is (backward compat with the TV app and config page).

**Honesty constraint:** confidence is *season-level*. `IntroCorrelator` correlates one window
across the season sample and applies it to every episode. The panel must present confidence on
the season row, and on episodes as "season confidence", never as a per-episode measurement.
Per-episode match rates against the chosen window are a possible follow-up, not this release.

## API

All under the existing `KoalaPaw` route prefix and `RequiresElevation` policy. New controller
`SegmentBrowseController` (keep `AnalysisController` for failures/triggers/report).

| Endpoint | Returns |
|---|---|
| `GET /KoalaPaw/Segments/Overview` | Per top-level library: episode count, with intro, with outro, failed, never scanned; `lastScanUtc`. |
| `GET /KoalaPaw/Segments/Series?libraryId={id}` | Per series: same rollups + worst-case flag (has failures). |
| `GET /KoalaPaw/Segments/Seasons?seriesId={id}` | Per season: episode counts by state, plus `SeasonScanRecord` (confidence, windows, outcome, finishedUtc) when present. |
| `GET /KoalaPaw/Segments/Episodes?seasonId={id}` | Per episode: index, name, runtime, intro `{startSeconds,endSeconds}` / outro `{startSeconds,endSeconds}` from MediaSegments, season confidence, `state`, failure reason. |
| `POST /KoalaPaw/Segments/Seasons/{seasonId}/Rescan` | 200 with the fresh `SeasonScanRecord`. **Direct analysis**: clears `"segments"` failure records for the season's episodes, then runs `IntroSeasonAnalyser` inline and persists the record — no queue, no batch cap, no failure suppression. Staging replaces our provider's rows, so no segment deletion is needed. 404 if season unknown. (Changed from the original delete-and-queue design: queueing the capped, GUID-ordered incremental task meant clicked seasons were wiped but not necessarily re-analysed.) |

Episode `state` derivation (server-side, so the TV app could reuse it later):

- `failed` — failure record exists **and** no segments in place
- `detected` — has intro **and** outro
- `partial` — exactly one of intro/outro
- `notScanned` — neither, no failure record

**Success invalidates failure.** Today `RemoveFailure` is only called by the Retry endpoint,
so an episode that failed once and is later detected would keep its failure record forever.
`IntroSeasonAnalyser.AnalyseAndStageAsync` must clear the `"segments"` failure record for
every episode it successfully stages segments onto (`_analysisStore.RemoveFailure(ep.Id,
"segments")` after a successful provider run). With that in place `failed`-first ordering can
never go stale; the "and no segments in place" guard above is belt-and-braces for records
written by older plugin versions.

Trigger + progress need **no new plumbing**:

- Trigger: existing `POST /KoalaPaw/Scan/Segments`.
- Progress: poll Jellyfin's built-in `GET /ScheduledTasks` and find key
  `KoalaPawIntroSegmentScan` — it already exposes `State` and `CurrentProgressPercentage`
  because `SegmentScanTask.ExecuteAsync` reports per-season. Panel polls every 2 s while
  `State == "Running"`, then refreshes the tree once.

## Dashboard page

New embedded page `Web/segments.html`, registered in `Plugin.GetPages()` as a second
`PluginPageInfo` ("KoalaPaw Intro & Outro", `EnableInMainMenu = true`). Kept separate from
`config.html` — that page is settings + failure log; this one is a browser. Same vanilla
emby-* components and `ApiClient` fetch style as `config.html` (mind the PascalCase JSON
gotcha from 0.0.9.1 — set `JsonPropertyName` on every DTO).

Layout, top to bottom:

1. **Scan controls** — "Scan now" button (POST Scan/Segments), progress bar bound to the
   ScheduledTasks poll, last-run summary line ("Last scan 03:12 — 42 episodes, 8 intros,
   8 outros, mean confidence 0.91").
2. **Tree** — collapsible Library → Series → Season rows with count badges
   (`12 ✓ · 1 ~ · 2 ✗`), each tier lazy-loaded from its endpoint.
3. **Season detail** — on expand: outcome, confidence, detected windows, finished time,
   "Rescan this season" button.
4. **Episode table** — columns: #, title, Intro (`0:00–0:47`), Outro (`21:33–end`),
   confidence badge (green ≥ 0.85, amber 0.70–0.85 — 0.70 is `IntroCorrelator.MinMatchRate`),
   state icon, failure reason on hover.

## Scan task changes

- `SeasonAnalysisResult` gains `IntroGuess`/`OutroGuess` windows (end seconds + confidence,
  both nullable) — or returns the record shape directly.
- `SegmentScanTask.ExecuteAsync` writes a `SeasonScanRecord` per season after each
  `AnalyseAndStageAsync` call, and on the `catch (Exception)` path records `Outcome.Error`.
- `IntroSeasonAnalyser.AnalyseAndStageAsync` clears `"segments"` failure records for every
  episode it stages onto (success invalidates earlier failures — see state derivation).
- Everything else (skip logic, triggers, report) untouched. (Later in the branch: the
  max-seasons-per-run cap was removed entirely — rescans and scheduled runs process every
  eligible season.)

## Edge cases

- **Deleted items/seasons:** names resolved at read time (existing pattern in
  `Failures/List`); stale `SeasonScanRecord`s whose season no longer exists are skipped in
  responses and pruned on flush.
- **Season skipped by caps/suppression:** simply has no new record — panel shows previous
  state, which is correct ("last known").
- **Confidence attribution:** episodes carry the season's confidence; UI labels it as such.
- **Rescan is synchronous:** the request returns when analysis completes (typically well under
  a minute per season); concurrent rescans of different seasons are safe (per-item staging,
  thread-safe store), and duplicate clicks on one season merely redo the same work.

## Tests (tests/KoalaPaw.ServerCompanion.Tests)

Unit-heavy, per repo convention:

- `SeasonScanRecord` mapping: `SeasonAnalysisResult` → record for each `SeasonOutcome`.
- State derivation: detected/partial/failed/notScanned from (segments, failure) combos,
  including segments-present + stale failure record → `detected`.
- **Success invalidation:** episode with an existing `"segments"` failure record has it
  cleared when `AnalyseAndStageAsync` stages segments onto it; afterwards derives `detected`,
  not `failed`.
- Overview/series/season rollup aggregation over a fixed item tree.
- Rescan: clears the right failure records, stages empty lists for the right episodes only.
- Store round-trip: records survive flush/load; stale-season pruning.

## Release mechanics

- Branch `0.0.11.0` (this branch).
- Bump `Version`/`AssemblyVersion`/`FileVersion` in `KoalaPaw.ServerCompanion.csproj` and
  `version` + changelog in `build.yaml` to `0.0.11.0`.
- Package via `scripts/package.sh` → `repo/0.0.11.0/` + `manifest.json` at release time.
