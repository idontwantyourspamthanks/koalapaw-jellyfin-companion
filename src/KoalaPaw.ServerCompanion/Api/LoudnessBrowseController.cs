using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Loudness;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>
/// Loudness dashboard browsing: gain/failure status per library, series, season, episode and
/// movie, plus on-demand rescans that force re-measurement of a scope.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")] // admin-only: exposes every item's loudness state and mutates metadata on rescan
[Route("KoalaPaw/Loudness")]
public class LoudnessBrowseController : ControllerBase
{
    private readonly AnalysisStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly LoudnessProbe _probe;
    private readonly ILogger<LoudnessBrowseController> _logger;

    public LoudnessBrowseController(
        AnalysisStore store,
        ILibraryManager libraryManager,
        LoudnessProbe probe,
        ILogger<LoudnessBrowseController> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>Per-library episode and movie loudness rollups plus the newest outcome's finish time.</summary>
    [HttpGet("Overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LoudnessOverviewDto> GetOverview()
    {
        var statuses = GetStatuses();
        var libraries = statuses
            .Where(s => s.Library is not null)
            .GroupBy(s => s.Library!.Id)
            .Select(g =>
            {
                var episodes = LoudnessItemState.Count(g.Where(s => s.Item is Episode).Select(s => s.State));
                var movies = LoudnessItemState.Count(g.Where(s => s.Item is Movie).Select(s => s.State));
                return new LoudnessLibrarySummaryDto
                {
                    LibraryId = g.Key,
                    Name = g.First().Library!.Name,
                    Episodes = ToCountsDto(episodes),
                    Movies = ToCountsDto(movies),
                };
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new LoudnessOverviewDto
        {
            LastScanUtc = MaxFinishedUtc(statuses.Select(s => s.FinishedUtc))?.ToUniversalTime().ToString("O"),
            Libraries = libraries,
        });
    }

    /// <summary>Per-series episode rollups for one library.</summary>
    [HttpGet("Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LoudnessSeriesSummaryDto>> GetSeries(Guid libraryId)
    {
        var statuses = GetStatuses(libraryId);
        var series = statuses
            .Where(s => s.Library?.Id == libraryId && s.Item is Episode)
            .GroupBy(s => s.Series?.Id ?? s.Season!.ParentId)
            .Select(g => new LoudnessSeriesSummaryDto
            {
                SeriesId = g.Key,
                Name = g.First().Series?.Name,
                Episodes = ToCountsDto(LoudnessItemState.Count(g.Select(s => s.State))),
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(series);
    }

    /// <summary>Per-season episode rollups for one series, with the newest outcome's finish time.</summary>
    [HttpGet("Seasons")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LoudnessSeasonSummaryDto>> GetSeasons(Guid seriesId)
    {
        var statuses = GetStatuses(seriesId);
        var seasons = statuses
            .Where(s => s.Item is Episode && (s.Series?.Id ?? s.Season!.ParentId) == seriesId)
            .GroupBy(s => s.Season!.Id)
            .Select(g =>
            {
                var grouped = g.ToList();
                return new LoudnessSeasonSummaryDto
                {
                    SeasonId = g.Key,
                    Name = g.First().Season!.Name,
                    IndexNumber = g.First().Season!.IndexNumber,
                    Episodes = ToCountsDto(LoudnessItemState.Count(grouped.Select(s => s.State))),
                    // One reason speaks for the tier: per-episode reasons are nearly always the
                    // same message, and the episode table carries them individually anyway.
                    FailureReason = grouped.Select(s => s.FailureReason).FirstOrDefault(r => r is not null),
                    LastScanUtc = MaxFinishedUtc(grouped.Select(s => s.FinishedUtc))?.ToUniversalTime().ToString("O"),
                };
            })
            .OrderBy(s => s.IndexNumber)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(seasons);
    }

    /// <summary>Episodes of one season with their measured gain and failure state.</summary>
    [HttpGet("Episodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LoudnessEpisodeDto>> GetEpisodes(Guid seasonId)
    {
        var statuses = GetStatuses(seasonId);
        var episodes = statuses
            .Where(s => s.Item is Episode && s.Season!.Id == seasonId)
            .OrderBy(s => s.Item.IndexNumber)
            .ThenBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new LoudnessEpisodeDto
            {
                EpisodeId = s.Item.Id,
                IndexNumber = s.Item.IndexNumber,
                Name = s.Item.Name,
                RuntimeSeconds = s.Item.RunTimeTicks.HasValue
                    ? s.Item.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                    : null,
                State = s.State,
                Gain = s.Gain,
                FailureReason = s.FailureReason,
                FinishedUtc = s.FinishedUtc?.ToUniversalTime().ToString("O"),
            })
            .ToList();
        return Ok(episodes);
    }

    /// <summary>Movies of one library with their measured gain and failure state.</summary>
    [HttpGet("Movies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LoudnessMovieDto>> GetMovies(Guid libraryId)
    {
        var statuses = GetStatuses(libraryId);
        var movies = statuses
            .Where(s => s.Item is Movie && s.Library?.Id == libraryId)
            .OrderBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new LoudnessMovieDto
            {
                MovieId = s.Item.Id,
                Name = s.Item.Name,
                RuntimeSeconds = s.Item.RunTimeTicks.HasValue
                    ? s.Item.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                    : null,
                State = s.State,
                Gain = s.Gain,
                FailureReason = s.FailureReason,
                FinishedUtc = s.FinishedUtc?.ToUniversalTime().ToString("O"),
            })
            .ToList();
        return Ok(movies);
    }

    /// <summary>Force-measure every episode of a series now, bypassing caps and suppression.</summary>
    [HttpPost("Series/{seriesId:guid}/Rescan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoudnessRescanResultDto>> RescanSeries(Guid seriesId)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series)
        {
            return NotFound();
        }

        var episodes = GetEpisodesInScope(seriesId);
        return await RescanItemsAsync(episodes).ConfigureAwait(false);
    }

    /// <summary>Force-measure every episode of a season now, bypassing caps and suppression.</summary>
    [HttpPost("Seasons/{seasonId:guid}/Rescan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoudnessRescanResultDto>> RescanSeason(Guid seasonId)
    {
        if (_libraryManager.GetItemById(seasonId) is not Season)
        {
            return NotFound();
        }

        var episodes = GetEpisodesInScope(seasonId);
        return await RescanItemsAsync(episodes).ConfigureAwait(false);
    }

    /// <summary>Force-measure one movie now, bypassing caps and suppression.</summary>
    [HttpPost("Movies/{movieId:guid}/Rescan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoudnessRescanResultDto>> RescanMovie(Guid movieId)
    {
        if (_libraryManager.GetItemById(movieId) is not Movie movie || string.IsNullOrWhiteSpace(movie.Path))
        {
            return NotFound();
        }

        return await RescanItemsAsync([movie]).ConfigureAwait(false);
    }

    /// <summary>
    /// All episodes under a series or season with a playable path. An explicit user action
    /// bypasses the scheduled task entirely — no batch caps, no failure suppression, and items
    /// with an existing gain are measured again (the scheduled run skips them).
    /// </summary>
    private List<BaseItem> GetEpisodesInScope(Guid parentId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = parentId,
            })
            .OfType<Episode>()
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Cast<BaseItem>()
            .ToList();
    }

    /// <summary>
    /// Forget earlier loudness failures for the scope, then measure every item. Deliberately NOT
    /// the request token: a client that disconnects mid-rescan (tab closed, navigation) must not
    /// abort measurement half-finished — finish and record the outcome.
    /// </summary>
    private async Task<ActionResult<LoudnessRescanResultDto>> RescanItemsAsync(IReadOnlyList<BaseItem> items)
    {
        foreach (var item in items)
        {
            _store.RemoveFailure(item.Id, "loudness");
        }

        var succeeded = 0;
        var outcomes = new List<LoudnessItemOutcomeDto>(items.Count);
        foreach (var item in items)
        {
            var record = await LoudnessScanTask.MeasureAndApplyAsync(
                    _probe,
                    _libraryManager,
                    _store,
                    _logger,
                    item,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (record.Gain is not null)
            {
                succeeded++;
            }

            outcomes.Add(new LoudnessItemOutcomeDto
            {
                ItemId = item.Id,
                Name = item.Name,
                Gain = record.Gain,
                FailureReason = record.FailureReason,
            });
        }

        return Ok(new LoudnessRescanResultDto
        {
            Scanned = items.Count,
            Succeeded = succeeded,
            Failed = items.Count - succeeded,
            Items = outcomes,
        });
    }

    /// <summary>
    /// Per-item loudness ground truth for the tier's subtree (Guid.Empty = every library), with
    /// ancestors and state resolved once so the tier rollups agree with the item lists. Ground
    /// truth is the item's NormalizationGain (what's actually applied); the persisted outcome
    /// record adds the measured gain, failure reason and finish time when the item no longer
    /// carries them.
    /// </summary>
    private IReadOnlyList<LoudnessItemStatus> GetStatuses(Guid parentId = default)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = parentId,
            })
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .ToList();

        var failuresByItem = new Dictionary<Guid, string>();
        foreach (var failure in _store.GetFailures())
        {
            if (failure.Analyser == "loudness")
            {
                failuresByItem[failure.ItemId] = failure.Reason;
            }
        }

        var statuses = new List<LoudnessItemStatus>(items.Count);
        foreach (var item in items)
        {
            BaseItem? season = null;
            BaseItem? series = null;
            if (item is Episode episode)
            {
                season = episode.ParentId == Guid.Empty ? null : _libraryManager.GetItemById(episode.ParentId);
                if (season is null)
                {
                    continue; // orphaned episode — nothing to browse it under
                }

                series = season.ParentId == Guid.Empty ? null : _libraryManager.GetItemById(season.ParentId);
            }

            var library = _libraryManager.GetCollectionFolders(item).FirstOrDefault();

            var record = _store.GetLoudnessScanRecord(item.Id);
            var gain = item.NormalizationGain ?? record?.Gain;
            var failureReason = failuresByItem.TryGetValue(item.Id, out var recorded)
                ? recorded
                : record?.FailureReason;

            statuses.Add(new LoudnessItemStatus(
                item,
                season,
                series,
                library,
                LoudnessItemState.Derive(hasGain: gain is not null, hasFailure: failureReason is not null),
                gain,
                failureReason,
                record?.FinishedUtc));
        }

        return statuses;
    }

    private static LoudnessCountsDto ToCountsDto(LoudnessItemState.LoudnessStateCounts counts) =>
        new()
        {
            Total = counts.Total,
            Scanned = counts.Scanned,
            Failed = counts.Failed,
            NotScanned = counts.NotScanned,
        };

    private static DateTime? MaxFinishedUtc(IEnumerable<DateTime?> values)
    {
        var max = values.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty().Max();
        return max == default ? null : max;
    }

    /// <summary>One item's loudness ground truth plus its resolved ancestors, as the browse tiers see it.</summary>
    private sealed record LoudnessItemStatus(
        BaseItem Item,
        BaseItem? Season,
        BaseItem? Series,
        Folder? Library,
        string State,
        double? Gain,
        string? FailureReason,
        DateTime? FinishedUtc);

    public sealed class LoudnessOverviewDto
    {
        [JsonPropertyName("lastScanUtc")]
        public string? LastScanUtc { get; set; }

        [JsonPropertyName("libraries")]
        public IReadOnlyList<LoudnessLibrarySummaryDto> Libraries { get; set; } = [];
    }

    public sealed class LoudnessLibrarySummaryDto
    {
        [JsonPropertyName("libraryId")]
        public Guid LibraryId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("episodes")]
        public LoudnessCountsDto Episodes { get; set; } = new();

        [JsonPropertyName("movies")]
        public LoudnessCountsDto Movies { get; set; } = new();
    }

    public sealed class LoudnessSeriesSummaryDto
    {
        [JsonPropertyName("seriesId")]
        public Guid SeriesId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("episodes")]
        public LoudnessCountsDto Episodes { get; set; } = new();
    }

    public sealed class LoudnessSeasonSummaryDto
    {
        [JsonPropertyName("seasonId")]
        public Guid SeasonId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("indexNumber")]
        public int? IndexNumber { get; set; }

        [JsonPropertyName("episodes")]
        public LoudnessCountsDto Episodes { get; set; } = new();

        [JsonPropertyName("failureReason")]
        public string? FailureReason { get; set; }

        [JsonPropertyName("lastScanUtc")]
        public string? LastScanUtc { get; set; }
    }

    public sealed class LoudnessCountsDto
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("scanned")]
        public int Scanned { get; set; }

        [JsonPropertyName("failed")]
        public int Failed { get; set; }

        [JsonPropertyName("notScanned")]
        public int NotScanned { get; set; }
    }

    public sealed class LoudnessEpisodeDto
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

        [JsonPropertyName("gain")]
        public double? Gain { get; set; }

        [JsonPropertyName("failureReason")]
        public string? FailureReason { get; set; }

        [JsonPropertyName("finishedUtc")]
        public string? FinishedUtc { get; set; }
    }

    public sealed class LoudnessMovieDto
    {
        [JsonPropertyName("movieId")]
        public Guid MovieId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("runtimeSeconds")]
        public double? RuntimeSeconds { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("gain")]
        public double? Gain { get; set; }

        [JsonPropertyName("failureReason")]
        public string? FailureReason { get; set; }

        [JsonPropertyName("finishedUtc")]
        public string? FinishedUtc { get; set; }
    }

    public sealed class LoudnessRescanResultDto
    {
        [JsonPropertyName("scanned")]
        public int Scanned { get; set; }

        [JsonPropertyName("succeeded")]
        public int Succeeded { get; set; }

        [JsonPropertyName("failed")]
        public int Failed { get; set; }

        [JsonPropertyName("items")]
        public IReadOnlyList<LoudnessItemOutcomeDto> Items { get; set; } = [];
    }

    public sealed class LoudnessItemOutcomeDto
    {
        [JsonPropertyName("itemId")]
        public Guid ItemId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("gain")]
        public double? Gain { get; set; }

        [JsonPropertyName("failureReason")]
        public string? FailureReason { get; set; }
    }
}
