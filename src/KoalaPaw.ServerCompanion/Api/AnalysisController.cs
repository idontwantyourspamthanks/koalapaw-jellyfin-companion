using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
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
        var failures = ToFailureDtos(_store.GetFailures(), id => _libraryManager.GetItemById(id)?.Name);
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
        var taskType = RetryScanTaskFor(analyser);
        if (taskType is null)
        {
            return BadRequest($"Unknown analyser '{analyser}'.");
        }

        if (!_store.RemoveFailure(itemId, analyser))
        {
            return NotFound();
        }

        QueueRetryScan(taskType);
        return Accepted(new RetryResult { Retried = true });
    }

    /// <summary>Queues the scan the analyser maps to (the generic queue API needs the concrete task type).</summary>
    private void QueueRetryScan(Type taskType)
    {
        if (taskType == typeof(LoudnessScanTask))
        {
            _taskManager.QueueIfNotRunning<LoudnessScanTask>();
        }
        else
        {
            _taskManager.QueueIfNotRunning<SegmentScanTask>();
        }
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

    /// <summary>
    /// Outcome of the last intro/credits scan — episodes analysed and intros/outros found, with
    /// mean correlation confidence (0–1). All zeros when no scan has completed yet.
    /// </summary>
    [HttpGet("Segments/Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SegmentScanReportDto> GetSegmentScanReport()
    {
        return Ok(ToReportDto(_store.GetSegmentScanReport()));
    }

    /// <summary>
    /// Maps a recorded analyser name to the scan task that re-analyses the items it failed on —
    /// theme failures ride the segment scan (theme extraction runs inside it). Extracted as a
    /// static so the retry switch is unit-testable without Jellyfin plumbing; null for unknown
    /// names, which the endpoint must reject instead of queueing a scan.
    /// </summary>
    internal static Type? RetryScanTaskFor(string analyser) => analyser switch
    {
        "loudness" => typeof(LoudnessScanTask),
        "segments" => typeof(SegmentScanTask),
        "themes" => typeof(SegmentScanTask),
        _ => null,
    };

    /// <summary>
    /// Maps recorded failures to their read-time DTOs, resolving item names through getItemName
    /// (null for items deleted since the failure). Extracted as a static with the lookup passed
    /// in as a delegate, as in DirectoryController.SelectListedUsers, so the mapping is
    /// unit-testable without Jellyfin plumbing.
    /// </summary>
    internal static IReadOnlyList<FailureDto> ToFailureDtos(
        IEnumerable<AnalysisStore.FailureRecord> failures,
        Func<Guid, string?> getItemName)
    {
        return failures
            .Select(r => new FailureDto
            {
                ItemId = r.ItemId,
                Name = getItemName(r.ItemId),
                Analyser = r.Analyser,
                Reason = r.Reason,
                Timestamp = r.Timestamp,
            })
            .ToList();
    }

    /// <summary>
    /// Maps the stored scan report to its API shape; null (no scan has completed yet) becomes all
    /// zeros per the endpoint contract. Extracted as a static so that contract is unit-testable
    /// without Jellyfin plumbing.
    /// </summary>
    internal static SegmentScanReportDto ToReportDto(AnalysisStore.SegmentScanReport? report) =>
        new()
        {
            EpisodesScanned = report?.EpisodesScanned ?? 0,
            IntrosFound = report?.IntrosFound ?? 0,
            OutrosFound = report?.OutrosFound ?? 0,
            IntroConfidence = report?.IntroConfidence,
            OutroConfidence = report?.OutroConfidence,
        };

    public sealed class SegmentScanReportDto
    {
        [JsonPropertyName("episodesScanned")]
        public int EpisodesScanned { get; set; }

        [JsonPropertyName("introsFound")]
        public int IntrosFound { get; set; }

        [JsonPropertyName("outrosFound")]
        public int OutrosFound { get; set; }

        [JsonPropertyName("introConfidence")]
        public double? IntroConfidence { get; set; }

        [JsonPropertyName("outroConfidence")]
        public double? OutroConfidence { get; set; }
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
