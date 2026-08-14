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

    /// <summary>
    /// Outcome of the last intro/credits scan — episodes analysed and intros/outros found, with
    /// mean correlation confidence (0–1). All zeros when no scan has completed yet.
    /// </summary>
    [HttpGet("Segments/Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SegmentScanReportDto> GetSegmentScanReport()
    {
        var report = _store.GetSegmentScanReport();
        return Ok(new SegmentScanReportDto
        {
            EpisodesScanned = report?.EpisodesScanned ?? 0,
            IntrosFound = report?.IntrosFound ?? 0,
            OutrosFound = report?.OutrosFound ?? 0,
            IntroConfidence = report?.IntroConfidence,
            OutroConfidence = report?.OutroConfidence,
        });
    }

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
