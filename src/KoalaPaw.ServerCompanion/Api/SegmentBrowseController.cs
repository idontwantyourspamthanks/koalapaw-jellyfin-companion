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
using KoalaPaw.ServerCompanion.ThemeSongs;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
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
    private readonly IntroSeasonAnalyser _analyser;
    private readonly ThemeFromIntroExtractor _themeExtractor;

    public SegmentBrowseController(
        AnalysisStore store,
        ILibraryManager libraryManager,
        IMediaSegmentManager segments,
        IntroSeasonAnalyser analyser,
        ThemeFromIntroExtractor themeExtractor)
    {
        _store = store;
        _libraryManager = libraryManager;
        _segments = segments;
        _analyser = analyser;
        _themeExtractor = themeExtractor;
    }

    /// <summary>Per-library episode rollups plus the last scan's finish time.</summary>
    [HttpGet("Overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SegmentOverviewDto>> GetOverview()
    {
        var statuses = await GetEpisodeStatusesAsync().ConfigureAwait(false);
        var libraries = SegmentBrowseLogic.GroupLibraries(statuses);

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
        var series = SegmentBrowseLogic.GroupSeries(libraryId, statuses);
        return Ok(series);
    }

    /// <summary>Per-season episode rollups for one series, with the season's scan record when present.</summary>
    [HttpGet("Seasons")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SeasonSummaryDto>>> GetSeasons(Guid seriesId)
    {
        var statuses = await GetEpisodeStatusesAsync(seriesId).ConfigureAwait(false);
        var seasons = SegmentBrowseLogic.GroupSeasons(
            seriesId,
            statuses,
            seasonId => _store.GetSeasonScanRecord(seasonId));
        return Ok(seasons);
    }

    /// <summary>Episodes of one season with their in-place segment windows and season-level confidence.</summary>
    [HttpGet("Episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EpisodeDto>>> GetEpisodes(Guid seasonId)
    {
        var statuses = await GetEpisodeStatusesAsync(seasonId).ConfigureAwait(false);
        var scan = _store.GetSeasonScanRecord(seasonId);
        var episodes = SegmentBrowseLogic.GroupEpisodes(seasonId, statuses, scan);
        return Ok(episodes);
    }

    /// <summary>
    /// Analyse this season immediately and return its fresh scan record. An explicit user
    /// action bypasses the scheduled task entirely — no batch caps, no failure suppression,
    /// no waiting for the next 12 h run. Staging replaces our provider's rows, so previously
    /// detected segments are refreshed rather than needing deletion first.
    /// </summary>
    [HttpPost("Seasons/{seasonId:guid}/Rescan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeasonScanDto>> RescanSeason(Guid seasonId)
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
            .Cast<BaseItem>()
            .ToList();

        // Forget earlier failures so this season is eligible again everywhere, then analyse now.
        foreach (var episode in episodes)
        {
            _store.RemoveFailure(episode.Id, "segments");
        }

        // Deliberately NOT the request token: a client that disconnects mid-rescan (tab closed,
        // navigation) must not abort analysis half-staged — finish and record the outcome.
        var result = await _analyser.AnalyseAndStageAsync(episodes, CancellationToken.None)
            .ConfigureAwait(false);

        var seriesId = episodes.OfType<Episode>().FirstOrDefault()?.SeriesId ?? Guid.Empty;
        var record = SegmentScanTask.ToSeasonScanRecord(seasonId, seriesId, DateTime.UtcNow, result);
        _store.SetSeasonScanRecord(record);

        if (result.Intro is { } intro)
        {
            var episode = episodes.OfType<Episode>().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Path));
            var series = seriesId == Guid.Empty ? null : _libraryManager.GetItemById(seriesId) as Series;
            if (episode is not null && series is not null)
            {
                _store.RemoveFailure(series.Id, ThemeFromIntroExtractor.AnalyserName);
                var endTicks = (long)(intro.EndSeconds * TimeSpan.TicksPerSecond);
                var extract = await _themeExtractor.ExtractAsync(
                        series.Path,
                        episode.Path,
                        0,
                        endTicks,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                SegmentScanTask.RememberThemeOutcome(_store, series.Id, extract);
            }
        }

        return Ok(SegmentBrowseLogic.ToScanDto(record));
    }

    /// <summary>
    /// Episode segment ground truth for the tier's subtree (Guid.Empty = every library), with
    /// ancestors and state resolved once so the tier rollups agree with the episode list.
    /// </summary>
    private async Task<IReadOnlyList<SegmentBrowseLogic.EpisodeRow>> GetEpisodeStatusesAsync(Guid parentId = default)
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

        var statuses = new List<SegmentBrowseLogic.EpisodeRow>(episodes.Count);
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

            statuses.Add(new SegmentBrowseLogic.EpisodeRow(
                episode.Id,
                episode.IndexNumber,
                episode.Name,
                episode.RunTimeTicks,
                library?.Id,
                library?.Name,
                series?.Id,
                series?.Name,
                season.Id,
                season.ParentId,
                season.Name,
                season.IndexNumber,
                EpisodeSegmentState.Derive(intro is not null, outro is not null, hasFailure),
                hasFailure ? failureReason : null,
                intro,
                outro));
        }

        return statuses;
    }

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

        [JsonPropertyName("failureReason")]
        public string? FailureReason { get; set; }
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
}
