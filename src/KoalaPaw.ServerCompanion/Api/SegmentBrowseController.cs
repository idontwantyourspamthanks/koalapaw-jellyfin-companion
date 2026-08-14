using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using KoalaPaw.ServerCompanion.Segments;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>
/// Segment dashboard browsing: Intro/Outro status per library, series, season and episode,
/// plus on-demand per-season rescans.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")] // admin-only: exposes every item's segment state and mutates segments on rescan
[Route("KoalaPaw/Segments")]
public class SegmentBrowseController : ControllerBase
{
    private readonly AnalysisStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _segments;
    private readonly ITaskManager _taskManager;
    private readonly PendingSegmentResults _pending;

    public SegmentBrowseController(
        AnalysisStore store,
        ILibraryManager libraryManager,
        IMediaSegmentManager segments,
        ITaskManager taskManager,
        PendingSegmentResults pending)
    {
        _store = store;
        _libraryManager = libraryManager;
        _segments = segments;
        _taskManager = taskManager;
        _pending = pending;
    }

    /// <summary>Per-library episode rollups plus the last scan's finish time.</summary>
    [HttpGet("Overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SegmentOverviewDto>> GetOverview()
    {
        var statuses = await GetEpisodeStatusesAsync().ConfigureAwait(false);
        var libraries = statuses
            .Where(s => s.Library is not null)
            .GroupBy(s => s.Library!.Id)
            .Select(g =>
            {
                var counts = EpisodeSegmentState.Count(g.Select(s => s.State));
                return new LibrarySummaryDto
                {
                    LibraryId = g.Key,
                    Name = g.First().Library!.Name,
                    Episodes = counts.Total,
                    Detected = counts.Detected,
                    Partial = counts.Partial,
                    Failed = counts.Failed,
                    NotScanned = counts.NotScanned,
                };
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var report = _store.GetSegmentScanReport();
        return Ok(new SegmentOverviewDto
        {
            LastScanUtc = report?.FinishedUtc.ToUniversalTime().ToString("O"),
            Libraries = libraries,
        });
    }

    /// <summary>Per-series episode rollups for one library.</summary>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SeriesSummaryDto>>> GetSeries(Guid libraryId)
    {
        var statuses = await GetEpisodeStatusesAsync(libraryId).ConfigureAwait(false);
        var series = statuses
            .Where(s => s.Library?.Id == libraryId)
            .GroupBy(s => s.Series?.Id ?? s.Season.ParentId)
            .Select(g =>
            {
                var counts = EpisodeSegmentState.Count(g.Select(s => s.State));
                return new SeriesSummaryDto
                {
                    SeriesId = g.Key,
                    Name = g.First().Series?.Name,
                    Episodes = counts.Total,
                    Detected = counts.Detected,
                    Partial = counts.Partial,
                    Failed = counts.Failed,
                    NotScanned = counts.NotScanned,
                };
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(series);
    }

    /// <summary>Per-season episode rollups for one series, with the season's scan record when present.</summary>
    [HttpGet("Seasons")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SeasonSummaryDto>>> GetSeasons(Guid seriesId)
    {
        var statuses = await GetEpisodeStatusesAsync(seriesId).ConfigureAwait(false);
        var seasons = statuses
            .Where(s => (s.Series?.Id ?? s.Season.ParentId) == seriesId)
            .GroupBy(s => s.Season.Id)
            .Select(g =>
            {
                var counts = EpisodeSegmentState.Count(g.Select(s => s.State));
                return new SeasonSummaryDto
                {
                    SeasonId = g.Key,
                    Name = g.First().Season.Name,
                    IndexNumber = g.First().Season.IndexNumber,
                    Episodes = counts.Total,
                    Detected = counts.Detected,
                    Partial = counts.Partial,
                    Failed = counts.Failed,
                    NotScanned = counts.NotScanned,
                    Scan = ToScanDto(_store.GetSeasonScanRecord(g.Key)),
                };
            })
            .OrderBy(s => s.IndexNumber)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(seasons);
    }

    /// <summary>Episodes of one season with their in-place segment windows and season-level confidence.</summary>
    [HttpGet("Episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EpisodeDto>>> GetEpisodes(Guid seasonId)
    {
        var statuses = await GetEpisodeStatusesAsync(seasonId).ConfigureAwait(false);
        var scan = _store.GetSeasonScanRecord(seasonId);
        var episodes = statuses
            .Where(s => s.Season.Id == seasonId)
            .OrderBy(s => s.Episode.IndexNumber)
            .ThenBy(s => s.Episode.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new EpisodeDto
            {
                EpisodeId = s.Episode.Id,
                IndexNumber = s.Episode.IndexNumber,
                Name = s.Episode.Name,
                RuntimeSeconds = s.Episode.RunTimeTicks.HasValue
                    ? s.Episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                    : null,
                State = s.State,
                Intro = ToWindowDto(s.Intro),
                Outro = ToWindowDto(s.Outro),
                FailureReason = s.FailureReason,
                IntroConfidence = scan?.IntroConfidence,
                OutroConfidence = scan?.OutroConfidence,
            })
            .ToList();
        return Ok(episodes);
    }

    /// <summary>
    /// Clear the season's segment failures and delete our provider's Intro/Outro rows, then queue
    /// a scan so the season is re-analysed. Deleting first is required because the scan skips
    /// seasons whose episodes already have both segment types.
    /// </summary>
    [HttpPost("Seasons/{seasonId:guid}/Rescan")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RescanResultDto>> RescanSeason(Guid seasonId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(seasonId) is not Season)
        {
            return NotFound();
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = seasonId,
            })
            .OfType<Episode>()
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .ToList();

        foreach (var episode in episodes)
        {
            _store.RemoveFailure(episode.Id, "segments");

            // Provider-scoped delete: staging an empty list makes RunSegmentPluginProviders
            // replace only KoalaPaw's rows with nothing (same path the analyser uses to stage).
            _pending.Set(episode.Id, []);
            try
            {
                await _segments.RunSegmentPluginProviders(
                        episode,
                        _libraryManager.GetLibraryOptions(episode),
                        forceOverwrite: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Always evict the staged entry — leaving it behind would leak into a later,
                // unrelated GetMediaSegments call for this item.
                _pending.Clear(episode.Id);
            }
        }

        _taskManager.QueueIfNotRunning<SegmentScanTask>();
        return Accepted(new RescanResultDto { Queued = true });
    }

    /// <summary>
    /// Episode segment ground truth for the tier's subtree (Guid.Empty = every library), with
    /// ancestors and state resolved once so the tier rollups agree with the episode list.
    /// </summary>
    private async Task<IReadOnlyList<EpisodeStatus>> GetEpisodeStatusesAsync(Guid parentId = default)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = parentId,
            })
            .OfType<Episode>()
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .ToList();

        var failuresByItem = new Dictionary<Guid, string>();
        foreach (var failure in _store.GetFailures())
        {
            if (failure.Analyser == "segments")
            {
                failuresByItem[failure.ItemId] = failure.Reason;
            }
        }

        var statuses = new List<EpisodeStatus>(episodes.Count);
        foreach (var episode in episodes)
        {
            var season = episode.ParentId == Guid.Empty ? null : _libraryManager.GetItemById(episode.ParentId);
            if (season is null)
            {
                continue; // orphaned episode — nothing to browse it under
            }

            var series = season.ParentId == Guid.Empty ? null : _libraryManager.GetItemById(season.ParentId);
            var library = _libraryManager.GetCollectionFolders(episode).FirstOrDefault();

            var libraryOptions = _libraryManager.GetLibraryOptions(episode);
            var existing = (await _segments.GetSegmentsAsync(
                    episode,
                    [MediaSegmentType.Intro, MediaSegmentType.Outro],
                    libraryOptions,
                    filterByProvider: true)
                .ConfigureAwait(false)).ToList();
            var intro = existing.FirstOrDefault(s => s.Type == MediaSegmentType.Intro);
            var outro = existing.FirstOrDefault(s => s.Type == MediaSegmentType.Outro);
            var hasFailure = failuresByItem.TryGetValue(episode.Id, out var failureReason);

            statuses.Add(new EpisodeStatus(
                episode,
                season,
                series,
                library,
                intro,
                outro,
                hasFailure ? failureReason : null,
                EpisodeSegmentState.Derive(intro is not null, outro is not null, hasFailure)));
        }

        return statuses;
    }

    private static SeasonScanDto? ToScanDto(AnalysisStore.SeasonScanRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        return new SeasonScanDto
        {
            FinishedUtc = record.FinishedUtc,
            Outcome = record.Outcome.ToString(),
            EpisodesSampled = record.EpisodesSampled,
            IntroEndSeconds = record.IntroEndSeconds,
            IntroConfidence = record.IntroConfidence,
            OutroLengthSeconds = record.OutroLengthSeconds,
            OutroConfidence = record.OutroConfidence,
        };
    }

    private static SegmentWindowDto? ToWindowDto(MediaSegmentDto? segment)
    {
        if (segment is null)
        {
            return null;
        }

        return new SegmentWindowDto
        {
            StartSeconds = segment.StartTicks / (double)TimeSpan.TicksPerSecond,
            EndSeconds = segment.EndTicks / (double)TimeSpan.TicksPerSecond,
        };
    }

    /// <summary>One episode's segment ground truth plus its resolved ancestors, as the browse tiers see it.</summary>
    private sealed record EpisodeStatus(
        Episode Episode,
        BaseItem Season,
        BaseItem? Series,
        Folder? Library,
        MediaSegmentDto? Intro,
        MediaSegmentDto? Outro,
        string? FailureReason,
        string State);

    public sealed class SegmentOverviewDto
    {
        [JsonPropertyName("lastScanUtc")]
        public string? LastScanUtc { get; set; }

        [JsonPropertyName("libraries")]
        public IReadOnlyList<LibrarySummaryDto> Libraries { get; set; } = [];
    }

    public sealed class LibrarySummaryDto
    {
        [JsonPropertyName("libraryId")]
        public Guid LibraryId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("episodes")]
        public int Episodes { get; set; }

        [JsonPropertyName("detected")]
        public int Detected { get; set; }

        [JsonPropertyName("partial")]
        public int Partial { get; set; }

        [JsonPropertyName("failed")]
        public int Failed { get; set; }

        [JsonPropertyName("notScanned")]
        public int NotScanned { get; set; }
    }

    public sealed class SeriesSummaryDto
    {
        [JsonPropertyName("seriesId")]
        public Guid SeriesId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("episodes")]
        public int Episodes { get; set; }

        [JsonPropertyName("detected")]
        public int Detected { get; set; }

        [JsonPropertyName("partial")]
        public int Partial { get; set; }

        [JsonPropertyName("failed")]
        public int Failed { get; set; }

        [JsonPropertyName("notScanned")]
        public int NotScanned { get; set; }
    }

    public sealed class SeasonSummaryDto
    {
        [JsonPropertyName("seasonId")]
        public Guid SeasonId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("indexNumber")]
        public int? IndexNumber { get; set; }

        [JsonPropertyName("episodes")]
        public int Episodes { get; set; }

        [JsonPropertyName("detected")]
        public int Detected { get; set; }

        [JsonPropertyName("partial")]
        public int Partial { get; set; }

        [JsonPropertyName("failed")]
        public int Failed { get; set; }

        [JsonPropertyName("notScanned")]
        public int NotScanned { get; set; }

        [JsonPropertyName("scan")]
        public SeasonScanDto? Scan { get; set; }
    }

    public sealed class SeasonScanDto
    {
        [JsonPropertyName("finishedUtc")]
        public DateTime FinishedUtc { get; set; }

        [JsonPropertyName("outcome")]
        public string Outcome { get; set; } = string.Empty;

        [JsonPropertyName("episodesSampled")]
        public int EpisodesSampled { get; set; }

        [JsonPropertyName("introEndSeconds")]
        public double? IntroEndSeconds { get; set; }

        [JsonPropertyName("introConfidence")]
        public double? IntroConfidence { get; set; }

        [JsonPropertyName("outroLengthSeconds")]
        public double? OutroLengthSeconds { get; set; }

        [JsonPropertyName("outroConfidence")]
        public double? OutroConfidence { get; set; }
    }

    public sealed class EpisodeDto
    {
        [JsonPropertyName("episodeId")]
        public Guid EpisodeId { get; set; }

        [JsonPropertyName("indexNumber")]
        public int? IndexNumber { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("runtimeSeconds")]
        public double? RuntimeSeconds { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("intro")]
        public SegmentWindowDto? Intro { get; set; }

        [JsonPropertyName("outro")]
        public SegmentWindowDto? Outro { get; set; }

        [JsonPropertyName("failureReason")]
        public string? FailureReason { get; set; }

        [JsonPropertyName("introConfidence")]
        public double? IntroConfidence { get; set; }

        [JsonPropertyName("outroConfidence")]
        public double? OutroConfidence { get; set; }
    }

    public sealed class SegmentWindowDto
    {
        [JsonPropertyName("startSeconds")]
        public double StartSeconds { get; set; }

        [JsonPropertyName("endSeconds")]
        public double EndSeconds { get; set; }
    }

    public sealed class RescanResultDto
    {
        [JsonPropertyName("queued")]
        public bool Queued { get; set; }
    }
}
