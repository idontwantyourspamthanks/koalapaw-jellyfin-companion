# Dashboard failure retry + manual scan — design

Date: 2026-08-06. Status: approved by user.

## Problem

The Server Companion records analysis failures (loudness / segment scans) in
`Bookkeeping/AnalysisStore.cs` — a JSON store keyed by `(ItemId, Analyser)` whose whole point
is to suppress re-processing of known failures. But the only remediation today is
`ClearFailures()`, which wipes **all** records, and the only trigger for scans is the schedule.
The dashboard (`Web/config.html`) shows just a count and a Clear button.

## Decisions (from brainstorming)

- Retry is **queue-based**: clear the one failure record, then queue the existing batch task.
  No per-item immediate probing, no extracting analyser services from the tasks.
- "Scan now" is a **plain queue** of the existing incremental tasks — batch caps (40 items /
  8 seasons) and failure suppression still apply. No forced full-pass mode.
- New mutation endpoints are **admin-only** (`Policies.RequiresElevation`). The existing
  endpoints use plain `[Authorize]` (any logged-in user); hardening them too is a one-word
  change and included.

## Why queue-based works cleanly

- Loudness: failed items still have `NormalizationGain == null`, so they pass the candidate
  filter (`ScheduledTasks/LoudnessScanTask.cs:87-95`) once their failure record is removed.
- Segments: a season is skipped only when *every* episode failed
  (`ScheduledTasks/SegmentScanTask.cs:118`) or all segments exist — removing one episode's
  record re-enables its season.
- On-demand task triggering is supported by Jellyfin: `ITaskManager.QueueIfNotRunning<T>()`
  (verified in `MediaBrowser.Model.Tasks`, Jellyfin.Model 10.11.11). `T` is discovered via DI
  since both tasks implement `IScheduledTask`.
- If the underlying problem persists, the item simply re-fails and `RecordFailure` re-adds it.
- Segment retry is inherently per-season (correlation needs ≥2 episodes,
  `Segments/IntroSeasonAnalyser.cs:56-59`) — the UI just says "retry".

## Design

### 1. `AnalysisStore` (`Bookkeeping/AnalysisStore.cs`)

- Add `IReadOnlyList<FailureRecord> GetFailures()` — snapshot of all records.
- Add `bool RemoveFailure(Guid itemId, string analyser)` — removes one record and flushes
  (or relies on the 30s timer flush; match existing semantics).
- `FailureRecord` is unchanged. Item names are **resolved at read time** in the controller via
  `ILibraryManager.GetItemById`, not stored (names go stale; deleted items get a fallback
  label).

### 2. `AnalysisController` (`Api/AnalysisController.cs`)

New endpoints, all under the existing `[Route("KoalaPaw")]`:

- `GET /KoalaPaw/Failures/List` → array of
  `{ itemId, name, analyser, reason, timestamp }`. `name` resolved via `ILibraryManager`;
  deleted items → `null` name (UI shows "Deleted item").
- `POST /KoalaPaw/Failures/{itemId}/{analyser}/Retry` → `RemoveFailure(itemId, analyser)`;
  then `QueueIfNotRunning<LoudnessScanTask>()` for `analyser == "loudness"` or
  `QueueIfNotRunning<SegmentScanTask>()` for `analyser == "segments"`; → 202
  `{ retried: true }`. Unknown analyser → 400. Unknown record → 404.
- `POST /KoalaPaw/Scan/Loudness` → `QueueIfNotRunning<LoudnessScanTask>()` → 202.
- `POST /KoalaPaw/Scan/Segments` → `QueueIfNotRunning<SegmentScanTask>()` → 202.

Auth: class-level `[Authorize]` becomes `[Authorize(Policy = Policies.RequiresElevation)]`
(Jellyfin's admin policy) for the whole controller — the dashboard page is admin-only anyway,
and clearing/queuing scans should not be available to every user.

New constructor dependency: `ITaskManager` (from `MediaBrowser.Model.Tasks`, already
referenced via Jellyfin.Model) and `ILibraryManager`. Both are Jellyfin singletons, resolved
by DI — no registration changes needed in `PluginServiceRegistrator.cs` (the controller is
already DI-constructed).

### 3. Dashboard (`Web/config.html`)

- Extend the Failure Log `verticalSection` (currently just count + Clear, `:64-70`) with a
  list: item name, analyser badge, reason, timestamp, and a per-row **Retry** button. Rendered
  from `GET KoalaPaw/Failures/List` using the existing `ApiClient.ajax` pattern (`:99-107`).
- Add two **Scan now** buttons (Loudness / Segments) in the Analysis section (`:18-30`) or
  beside the Failure Log — POST the matching endpoint, show a "queued" toast/emby-toast.
- After retry, refresh the list (the record disappears immediately; the item re-appears only
  if it fails again).
- Keep the existing count + Clear-all for bulk cases.

### 4. Versioning

Feature release → `0.0.9.0`, bumped together in:

- `src/KoalaPaw.ServerCompanion/KoalaPaw.ServerCompanion.csproj:11-13`
  (`Version`/`AssemblyVersion`/`FileVersion`)
- `build.yaml:3` (`version`) + changelog entry (`:16`)

`scripts/package.sh` rebuilds the repo `manifest.json` + zip automatically. `PluginGuid` and
the hardcoded `pluginId` in config.html must not change. `targetAbi` stays `10.11.0.0`.

## Out of scope

- Immediate per-item probing (extracting shared analyser services from the tasks).
- Forced full-pass scans (bypassing caps / failure suppression).
- Per-item failure detail views, batch progress, scan history.
- Attempt counters on `FailureRecord`.

## Testing

- xUnit tests for `GetFailures` / `RemoveFailure` in
  `tests/KoalaPaw.ServerCompanion.Tests/AnalysisStoreTests.cs` style (temp-dir store,
  `NullLogger`): remove one record leaves others, remove-missing is false/no-op, list returns
  snapshot, persistence across store instances.
- No controller test infra exists in the project; keep the controller thin (orchestration
  only) so store-level coverage suffices. Manual verification on the Pi dev server: record a
  failure, retry from the dashboard, watch the scheduled task run.
