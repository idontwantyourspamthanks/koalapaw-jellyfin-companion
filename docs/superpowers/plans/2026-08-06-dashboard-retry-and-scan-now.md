# Dashboard Failure Retry + Manual Scan Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Server Companion dashboard per-failure Retry buttons and Loudness/Segments "Scan now" buttons, backed by new admin-only API endpoints that clear one failure record and queue the existing batch scan tasks.

**Architecture:** `AnalysisStore` gains `GetFailures()` and `RemoveFailure(itemId, analyser)`; `AnalysisController` gains a failure-list endpoint (item names resolved at read time via `ILibraryManager`), a retry endpoint (`RemoveFailure` + `ITaskManager.QueueIfNotRunning<T>()`), and two scan endpoints. Queue-based retry works because failed items still pass the tasks' candidate filters once their record is gone. The whole controller moves to Jellyfin's elevation policy (admin-only).

**Tech Stack:** C# / .NET 9, Jellyfin 10.11 plugin API, xUnit, legacy Jellyfin dashboard page (`ApiClient.ajax`).

Spec: `docs/superpowers/specs/2026-08-06-dashboard-retry-and-scan-now-design.md`

## Global Constraints

- Work in `koalapaw-server-companion/` (the plugin repo root). All `Run:` commands assume that cwd.
- Build: `dotnet build`. Tests: `dotnet test`. `TreatWarningsAsErrors` is ON — no nullable or unused-variable sloppiness.
- Analyser strings are exactly `"loudness"` and `"segments"` (used by `LoudnessScanTask.cs` and `IntroSeasonAnalyser.cs`).
- Version must move together in `src/KoalaPaw.ServerCompanion/KoalaPaw.ServerCompanion.csproj` (3 attributes) and `build.yaml` (`version` + changelog). `scripts/package.sh` regenerates `repo/manifest.json` — do not hand-edit the manifest.
- `PluginGuid` (`Plugin.cs:17`) and the `pluginId` in `Web/config.html:84` must NOT change. `targetAbi` stays `10.11.0.0`.
- The `Jellyfin.Api` package is NOT referenced, so `Policies.RequiresElevation` is unavailable — use the policy name literal `"RequiresElevation"` (Jellyfin registers it under that string).
- Queue-based retry only: no immediate per-item probing, no forced full-pass scans (approved design).
- Commit style in this repo: check `git log --oneline -5` and match.

---

### Task 1: `AnalysisStore.GetFailures` + `RemoveFailure`

**Files:**
- Modify: `src/KoalaPaw.ServerCompanion/Bookkeeping/AnalysisStore.cs:54-66,134-145`
- Test: `tests/KoalaPaw.ServerCompanion.Tests/AnalysisStoreTests.cs`

**Interfaces:**
- Produces (used by Task 2):
  - `public IReadOnlyList<AnalysisStore.FailureRecord> GetFailures()` — snapshot, newest first.
  - `public bool RemoveFailure(Guid itemId, string analyser)` — true when a record existed; flushes immediately like `ClearFailures` does.
  - `FailureRecord` becomes `public sealed class` (it must be — a public method can't expose an internal nested type, and `TreatWarningsAsErrors` makes inconsistent accessibility a build failure).

- [ ] **Step 1: Write the failing tests**

Append to `tests/KoalaPaw.ServerCompanion.Tests/AnalysisStoreTests.cs` (inside the class, before the closing brace):

```csharp
[Fact]
public void GetFailures_returnsAllRecordsNewestFirst()
{
    using var store = CreateStore();
    var a = Guid.NewGuid();
    var b = Guid.NewGuid();

    store.RecordFailure(a, "loudness", "first");
    store.RecordFailure(b, "segments", "second");

    var failures = store.GetFailures();

    Assert.Equal(2, failures.Count);
    Assert.Equal(b, failures[0].ItemId); // recorded later → newer timestamp → first
    Assert.Equal(a, failures[1].ItemId);
    Assert.Equal("segments", failures[0].Analyser);
    Assert.Equal("second", failures[0].Reason);
}

[Fact]
public void RemoveFailure_removesOnlyTheMatchingRecord()
{
    using var store = CreateStore();
    var a = Guid.NewGuid();
    var b = Guid.NewGuid();
    store.RecordFailure(a, "loudness", "r1");
    store.RecordFailure(a, "segments", "r2");
    store.RecordFailure(b, "loudness", "r3");

    Assert.True(store.RemoveFailure(a, "loudness"));

    Assert.False(store.HasFailed(a, "loudness"));
    Assert.True(store.HasFailed(a, "segments"), "same item, other analyser must survive");
    Assert.True(store.HasFailed(b, "loudness"), "other item must survive");
    Assert.Equal(2, store.GetFailureCount());
}

[Fact]
public void RemoveFailure_missingRecord_returnsFalse()
{
    using var store = CreateStore();

    Assert.False(store.RemoveFailure(Guid.NewGuid(), "loudness"));
}

[Fact]
public void RemoveFailure_persistsAcrossNewInstance()
{
    var a = Guid.NewGuid();
    var b = Guid.NewGuid();
    using (var store = CreateStore())
    {
        store.RecordFailure(a, "loudness", "r1");
        store.RecordFailure(b, "loudness", "r2");
        store.RemoveFailure(a, "loudness");
    } // Dispose flushes

    using (var store = CreateStore())
    {
        Assert.False(store.HasFailed(a, "loudness"));
        Assert.True(store.HasFailed(b, "loudness"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AnalysisStoreTests"`
Expected: FAIL — compile errors, `GetFailures`/`RemoveFailure` don't exist.

- [ ] **Step 3: Implement**

In `src/KoalaPaw.ServerCompanion/Bookkeeping/AnalysisStore.cs`:

a) Change the nested record's accessibility (line 134):

```csharp
public sealed class FailureRecord
```

b) Add the two methods right after `ClearFailures` (after line 63):

```csharp
/// <summary>Snapshot of all recorded failures, newest first.</summary>
public IReadOnlyList<FailureRecord> GetFailures() =>
    _records.Values.OrderByDescending(r => r.Timestamp).ToList();

/// <summary>Remove one failure record so the item is eligible again on the next scan run.</summary>
public bool RemoveFailure(Guid itemId, string analyser)
{
    if (!_records.TryRemove((itemId, analyser), out _))
    {
        return false;
    }

    _dirty = true;
    Flush();
    return true;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: ALL tests pass (existing 11 + new 4).

- [ ] **Step 5: Commit**

```bash
git add src/KoalaPaw.ServerCompanion/Bookkeeping/AnalysisStore.cs tests/KoalaPaw.ServerCompanion.Tests/AnalysisStoreTests.cs
git commit -m "Add GetFailures and RemoveFailure to AnalysisStore"
```

---

### Task 2: Controller endpoints (list, retry, scan) + elevation policy

**Files:**
- Modify: `src/KoalaPaw.ServerCompanion/Api/AnalysisController.cs` (full rewrite — it's 40 lines)

**Interfaces:**
- Consumes: `AnalysisStore.GetFailures()` / `RemoveFailure(Guid, string)` (Task 1); `ITaskManager.QueueIfNotRunning<T>()` from `MediaBrowser.Model.Tasks`; `ILibraryManager.GetItemById(Guid)` from `MediaBrowser.Controller.Library`; `ScheduledTasks.LoudnessScanTask`, `ScheduledTasks.SegmentScanTask`.
- Produces (consumed by Task 3's JS):
  - `GET /KoalaPaw/Failures/List` → 200 `[{ itemId, name, analyser, reason, timestamp }]` (`name` null when the item was deleted).
  - `POST /KoalaPaw/Failures/{itemId}/{analyser}/Retry` → 202 `{ "retried": true }`; 404 unknown record; 400 unknown analyser.
  - `POST /KoalaPaw/Scan/Loudness` → 202 `{ "queued": true }`.
  - `POST /KoalaPaw/Scan/Segments` → 202 `{ "queued": true }`.
  - Existing `GET /KoalaPaw/Failures` ({count}) and `POST /KoalaPaw/Failures/Clear` are unchanged in shape.
  - ALL endpoints now require admin (`[Authorize(Policy = "RequiresElevation")]`).

- [ ] **Step 1: Rewrite the controller**

Replace the whole content of `src/KoalaPaw.ServerCompanion/Api/AnalysisController.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>API for the dashboard config page: failure list/clear/retry and manual scan triggers.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")] // admin-only: clearing failures and queueing scans mutate server state
[Route("KoalaPaw")]
public class AnalysisController : ControllerBase
{
    private readonly AnalysisStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;

    public AnalysisController(
        AnalysisStore store,
        ILibraryManager libraryManager,
        ITaskManager taskManager)
    {
        _store = store;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
    }

    [HttpGet("Failures")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FailureSummary> GetFailures()
    {
        return Ok(new FailureSummary { Count = _store.GetFailureCount() });
    }

    /// <summary>All recorded failures with item names resolved at read time (null when deleted).</summary>
    [HttpGet("Failures/List")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<FailureDto>> ListFailures()
    {
        var failures = _store.GetFailures()
            .Select(r => new FailureDto
            {
                ItemId = r.ItemId,
                Name = _libraryManager.GetItemById(r.ItemId)?.Name,
                Analyser = r.Analyser,
                Reason = r.Reason,
                Timestamp = r.Timestamp,
            })
            .ToList();
        return Ok(failures);
    }

    [HttpPost("Failures/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearFailures()
    {
        _store.ClearFailures();
        return NoContent();
    }

    /// <summary>Forget one failure and queue the matching scan so the item is re-analysed next run.</summary>
    [HttpPost("Failures/{itemId:guid}/{analyser}/Retry")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RetryResult> RetryFailure(Guid itemId, string analyser)
    {
        Action queue = analyser switch
        {
            "loudness" => () => _taskManager.QueueIfNotRunning<LoudnessScanTask>(),
            "segments" => () => _taskManager.QueueIfNotRunning<SegmentScanTask>(),
            _ => null!,
        };
        if (queue is null)
        {
            return BadRequest($"Unknown analyser '{analyser}'.");
        }

        if (!_store.RemoveFailure(itemId, analyser))
        {
            return NotFound();
        }

        queue();
        return Accepted(new RetryResult { Retried = true });
    }

    /// <summary>Queue the incremental loudness scan (batch caps and failure suppression still apply).</summary>
    [HttpPost("Scan/Loudness")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<QueuedResult> ScanLoudness()
    {
        _taskManager.QueueIfNotRunning<LoudnessScanTask>();
        return Accepted(new QueuedResult { Queued = true });
    }

    /// <summary>Queue the incremental intro/credits scan (batch caps and failure suppression still apply).</summary>
    [HttpPost("Scan/Segments")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<QueuedResult> ScanSegments()
    {
        _taskManager.QueueIfNotRunning<SegmentScanTask>();
        return Accepted(new QueuedResult { Queued = true });
    }

    public sealed class FailureSummary
    {
        public int Count { get; set; }
    }

    public sealed class FailureDto
    {
        public Guid ItemId { get; set; }

        public string? Name { get; set; }

        public string Analyser { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }
    }

    public sealed class RetryResult
    {
        public bool Retried { get; set; }
    }

    public sealed class QueuedResult
    {
        public bool Queued { get; set; }
    }
}
```

Notes for the implementer:
- If `ILibraryManager.GetItemById` throws for deleted items rather than returning null (it
  changed across Jellyfin versions), wrap the lookup: `try { ... } catch { null }`. Build first;
  the compiler's nullability analysis will tell you which it is — with nullable enabled a
  non-nullable return type makes `?.Name` warn (error under TreatWarningsAsErrors), in which case
  use the try/catch form.
- `QueueIfNotRunning<T>()` is on `MediaBrowser.Model.Tasks.ITaskManager` (verified in
  Jellyfin.Model 10.11.11). The tasks are auto-discovered by Jellyfin; no DI registration
  changes are needed — `ITaskManager` and `ILibraryManager` are Jellyfin singletons injected
  into controllers automatically.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: build succeeds with zero warnings (TreatWarningsAsErrors).

- [ ] **Step 3: Run full test suite**

Run: `dotnet test`
Expected: all tests pass (no controller tests exist — the controller is deliberately thin
orchestration; store logic is covered by Task 1's tests).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaPaw.ServerCompanion/Api/AnalysisController.cs
git commit -m "Add failure list/retry and scan-now endpoints, require elevation"
```

---

### Task 3: Dashboard UI — failure list with Retry + Scan now buttons

**Files:**
- Modify: `src/KoalaPaw.ServerCompanion/Web/config.html:18-30,64-70,82-167`

**Interfaces:**
- Consumes: the four endpoints from Task 2 (`Failures/List`, `Failures/{id}/{analyser}/Retry`, `Scan/Loudness`, `Scan/Segments`) via the existing `ApiClient.ajax` pattern.
- Produces: nothing programmatic — visual deliverable, verified on a live server in Task 4.

- [ ] **Step 1: Add Scan buttons to the Analysis section**

Replace the Analysis `verticalSection` (lines 18-30) with:

```html
<div class="verticalSection">
    <h3 class="sectionTitle">Analysis</h3>

    <label class="checkboxContainer">
        <input type="checkbox" is="emby-checkbox" id="loudnessEnabled" />
        <span>Enable loudness scanning</span>
    </label>

    <label class="checkboxContainer">
        <input type="checkbox" is="emby-checkbox" id="segmentDetectionEnabled" />
        <span>Enable intro/credits detection</span>
    </label>

    <div style="margin-top: 1em; display: flex; gap: 0.5em;">
        <button is="emby-button" type="button" id="scanLoudness" class="raised block">
            <span>Scan loudness now</span>
        </button>
        <button is="emby-button" type="button" id="scanSegments" class="raised block">
            <span>Scan intros/credits now</span>
        </button>
    </div>
    <div class="fieldDescription">Queues the incremental scan: per-run batch caps apply, and items in the failure log are skipped.</div>
</div>
```

- [ ] **Step 2: Replace the Failure Log section**

Replace lines 64-70 with:

```html
<div class="verticalSection">
    <h3 class="sectionTitle">Failure Log</h3>
    <p>Tracked analysis failures: <strong id="failureCount">?</strong></p>
    <div id="failureList"></div>
    <button is="emby-button" type="button" id="clearFailures" class="raised button-submit block">
        <span>Clear failure log</span>
    </button>
</div>
```

- [ ] **Step 3: Add the JS**

Inside the existing IIFE, after the `loadFailureCount` function (line ~108), add:

```javascript
function loadFailures(page) {
    ApiClient.ajax({
        type: 'GET',
        url: ApiClient.getUrl('KoalaPaw/Failures/List'),
        dataType: 'json'
    }).then(function (failures) {
        var list = page.querySelector('#failureList');
        list.innerHTML = '';
        failures.forEach(function (f) {
            var row = document.createElement('div');
            row.style.cssText = 'display:flex;align-items:center;gap:0.75em;margin:0.4em 0;';

            var label = document.createElement('div');
            label.style.flex = '1';
            var name = f.name || 'Deleted item';
            var when = new Date(f.timestamp).toLocaleString();
            label.innerHTML = '<strong></strong> <span class="secondaryText"></span><br><span class="fieldDescription"></span>';
            label.querySelector('strong').textContent = name;
            label.querySelector('.secondaryText').textContent = '(' + f.analyser + ', ' + when + ')';
            label.querySelector('.fieldDescription').textContent = f.reason;

            var btn = document.createElement('button');
            btn.setAttribute('is', 'emby-button');
            btn.type = 'button';
            btn.className = 'raised block';
            btn.innerHTML = '<span>Retry</span>';
            btn.addEventListener('click', function () {
                retryFailure(page, f.itemId, f.analyser);
            });

            row.appendChild(label);
            row.appendChild(btn);
            list.appendChild(row);
        });
    }).catch(function () {
        page.querySelector('#failureList').innerHTML = '';
    });
}

function retryFailure(page, itemId, analyser) {
    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('KoalaPaw/Failures/' + itemId + '/' + analyser + '/Retry')
    }).then(function () {
        loadFailures(page);
        loadFailureCount(page);
        Dashboard.processPluginConfigurationUpdateResult();
    });
}

function scanNow(page, kind) {
    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('KoalaPaw/Scan/' + kind)
    }).then(function () {
        Dashboard.processPluginConfigurationUpdateResult();
    });
}
```

(textContent is used for all server-supplied strings — no innerHTML injection from failure
reasons or item names.)

- [ ] **Step 4: Wire the events**

In the `pageshow` handler (line ~148-154), add `loadFailures(page);` after `loadFailureCount(page);`.

After the existing `#clearFailures` listener (line ~163-166), add:

```javascript
document.querySelector('#scanLoudness')
    .addEventListener('click', function () {
        scanNow(document.querySelector('#KoalaPawConfigPage'), 'Loudness');
    });

document.querySelector('#scanSegments')
    .addEventListener('click', function () {
        scanNow(document.querySelector('#KoalaPawConfigPage'), 'Segments');
    });
```

- [ ] **Step 5: Build (embeds the page)**

Run: `dotnet build`
Expected: build succeeds (config.html is an EmbeddedResource — a build confirms it's picked up).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaPaw.ServerCompanion/Web/config.html
git commit -m "Dashboard: failure list with retry buttons and scan-now triggers"
```

---

### Task 4: Version bump 0.0.9.0 + package + manual verification

**Files:**
- Modify: `src/KoalaPaw.ServerCompanion/KoalaPaw.ServerCompanion.csproj:11-13`
- Modify: `build.yaml:3,16`

- [ ] **Step 1: Bump the version**

In `KoalaPaw.ServerCompanion.csproj` change all three:

```xml
<Version>0.0.9.0</Version>
<AssemblyVersion>0.0.9.0</AssemblyVersion>
<FileVersion>0.0.9.0</FileVersion>
```

In `build.yaml`:

```yaml
version: "0.0.9.0"
```

and prepend to the `changelog` block:

```yaml
changelog: >
  0.0.9.0 — Dashboard: per-failure retry (clears the record and queues the scan), loudness/segments "scan now" buttons, failure list with item names; API endpoints now require admin elevation.
  0.0.8.0 — Failure tracking (AnalysisStore) so undetectable items aren't retried forever; ...
```

(keep the rest of the existing changelog lines intact — shown truncated here for brevity only)

- [ ] **Step 2: Build + test**

Run: `dotnet build && dotnet test`
Expected: build clean, all tests pass.

- [ ] **Step 3: Package**

Run: `./scripts/package.sh`
Expected: builds the zip, updates `repo/manifest.json` with a 0.0.9.0 entry (verify with
`git diff repo/manifest.json` — new version block, checksum, changelog).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaPaw.ServerCompanion/KoalaPaw.ServerCompanion.csproj build.yaml repo/
git commit -m "Release 0.0.9.0: dashboard retry and scan-now"
```

- [ ] **Step 5: Manual verification on the Pi dev server (user)**

Publish to the Pi (dev/staging repo, per the agreed workflow — NOT prod):

```bash
REPO_BASE_URL=http://ryansraspi.hopto.org/koalapaw-repo ./scripts/package.sh  # if not already run
./scripts/publish-pi.sh
```

On the dev Jellyfin server, update the plugin to 0.0.9.0, then:
1. Dashboard → KoalaPaw: failure log lists any existing failures with item names, analyser,
   reason, and a Retry button each. (To manufacture a failure: point the server at an
   unreadable/corrupt media file and run a scan.)
2. Retry → the row disappears, the matching scheduled task appears under Dashboard → Scheduled
   Tasks as running/queued; the item is re-analysed (and re-fails into the log if the problem
   persists — expected).
3. "Scan loudness now" / "Scan intros/credits now" → the corresponding scheduled task runs.
4. Confirm a non-admin user cannot call the endpoints (403) — e.g. open
   `/KoalaPaw/Failures/List` in a browser session logged in as a regular user.
5. Only after a soak: `./scripts/publish-prod.sh` to push to production.

---

## Self-review notes (plan author)

- Spec coverage: store methods (Task 1), four endpoints + elevation policy (Task 2), dashboard
  list + retry + scan buttons (Task 3), version bump trio + package (Task 4), Pi-first manual
  verification (Task 4 step 5). Deleted-item names → null → "Deleted item" (Task 2 DTO +
  Task 3 JS). Re-failure re-records — noted in Task 4 verification.
- Out-of-scope items from the spec (immediate probing, force-full-pass, attempt counters,
  per-item detail) are implemented nowhere in this plan — correct.
- Type consistency: `GetFailures()`/`RemoveFailure(Guid, string): bool` (Task 1) consumed
  verbatim in Task 2; DTO field names (`itemId`, `name`, `analyser`, `reason`, `timestamp`)
  match the JSON camelCase the JS reads in Task 3; endpoint paths match between Task 2 and
  Task 3 (`KoalaPaw/Failures/List`, `.../Retry`, `KoalaPaw/Scan/Loudness`, `KoalaPaw/Scan/Segments`).
