using System;
using System.Collections.Generic;
using System.Linq;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Segments;
using MediaBrowser.Model.MediaSegments;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>
/// Pure rollup and mapping seams for the segment browse tiers. Extracted as a static class so
/// the grouping, ordering and DTO mapping the TV app renders is unit-testable without Jellyfin
/// plumbing: rows carry plain scalars (ids and names the controller resolves once from entities)
/// instead of entities.
/// </summary>
internal static class SegmentBrowseLogic
{
    /// <summary>
    /// One episode's segment ground truth plus its resolved ancestors, as the browse tiers see it.
    /// SeasonParentId is the season's parent id — the series fallback key when the series item is unresolved.
    /// </summary>
    internal sealed record EpisodeRow(
        Guid EpisodeId,
        int? EpisodeIndexNumber,
        string EpisodeName,
        long? RunTimeTicks,
        Guid? LibraryId,
        string? LibraryName,
        Guid? SeriesId,
        string? SeriesName,
        Guid SeasonId,
        Guid SeasonParentId,
        string? SeasonName,
        int? SeasonIndexNumber,
        string State,
        string? FailureReason,
        MediaSegmentDto? Intro,
        MediaSegmentDto? Outro);

    /// <summary>Per-library episode rollups, ordered by name.</summary>
    internal static IReadOnlyList<SegmentBrowseController.LibrarySummaryDto> GroupLibraries(IEnumerable<EpisodeRow> rows) =>
        rows
            .Where(s => s.LibraryId is not null)
            .GroupBy(s => s.LibraryId!.Value)
            .Select(g =>
            {
                var counts = EpisodeSegmentState.Count(g.Select(s => s.State));
                return new SegmentBrowseController.LibrarySummaryDto
                {
                    LibraryId = g.Key,
                    Name = g.First().LibraryName,
                    Episodes = counts.Total,
                    Detected = counts.Detected,
                    Partial = counts.Partial,
                    Failed = counts.Failed,
                    NotScanned = counts.NotScanned,
                };
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Per-series episode rollups for one library, ordered by name.</summary>
    internal static IReadOnlyList<SegmentBrowseController.SeriesSummaryDto> GroupSeries(Guid libraryId, IEnumerable<EpisodeRow> rows) =>
        rows
            .Where(s => s.LibraryId == libraryId)
            .GroupBy(s => s.SeriesId ?? s.SeasonParentId)
            .Select(g =>
            {
                var counts = EpisodeSegmentState.Count(g.Select(s => s.State));
                return new SegmentBrowseController.SeriesSummaryDto
                {
                    SeriesId = g.Key,
                    Name = g.First().SeriesName,
                    Episodes = counts.Total,
                    Detected = counts.Detected,
                    Partial = counts.Partial,
                    Failed = counts.Failed,
                    NotScanned = counts.NotScanned,
                };
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Per-season episode rollups for one series, with the season's scan record when present,
    /// ordered by index then name. The scan lookup is a delegate so the store stays out of the seam.
    /// </summary>
    internal static IReadOnlyList<SegmentBrowseController.SeasonSummaryDto> GroupSeasons(
        Guid seriesId,
        IEnumerable<EpisodeRow> rows,
        Func<Guid, AnalysisStore.SeasonScanRecord?> seasonScanLookup) =>
        rows
            .Where(s => (s.SeriesId ?? s.SeasonParentId) == seriesId)
            .GroupBy(s => s.SeasonId)
            .Select(g =>
            {
                var counts = EpisodeSegmentState.Count(g.Select(s => s.State));
                return new SegmentBrowseController.SeasonSummaryDto
                {
                    SeasonId = g.Key,
                    Name = g.First().SeasonName,
                    IndexNumber = g.First().SeasonIndexNumber,
                    Episodes = counts.Total,
                    Detected = counts.Detected,
                    Partial = counts.Partial,
                    Failed = counts.Failed,
                    NotScanned = counts.NotScanned,
                    Scan = ToScanDto(seasonScanLookup(g.Key)),
                    // One reason speaks for the tier: per-episode reasons are nearly always the
                    // same message, and the episode table carries them individually anyway.
                    FailureReason = g.Select(s => s.FailureReason).FirstOrDefault(r => r is not null),
                };
            })
            .OrderBy(s => s.IndexNumber)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Episodes of one season with their in-place segment windows and season-level confidence, ordered by index then name.</summary>
    internal static IReadOnlyList<SegmentBrowseController.EpisodeDto> GroupEpisodes(
        Guid seasonId,
        IEnumerable<EpisodeRow> rows,
        AnalysisStore.SeasonScanRecord? scan) =>
        rows
            .Where(s => s.SeasonId == seasonId)
            .OrderBy(s => s.EpisodeIndexNumber)
            .ThenBy(s => s.EpisodeName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SegmentBrowseController.EpisodeDto
            {
                EpisodeId = s.EpisodeId,
                IndexNumber = s.EpisodeIndexNumber,
                Name = s.EpisodeName,
                RuntimeSeconds = s.RunTimeTicks.HasValue
                    ? s.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                    : null,
                State = s.State,
                Intro = ToWindowDto(s.Intro),
                Outro = ToWindowDto(s.Outro),
                FailureReason = s.FailureReason,
                IntroConfidence = scan?.IntroConfidence,
                OutroConfidence = scan?.OutroConfidence,
            })
            .ToList();

    /// <summary>
    /// Maps one season's persisted scan record to the DTO the browse tiers embed. Extracted as a
    /// static so the field mapping is unit-testable without Jellyfin plumbing.
    /// </summary>
    internal static SegmentBrowseController.SeasonScanDto? ToScanDto(AnalysisStore.SeasonScanRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        return new SegmentBrowseController.SeasonScanDto
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

    /// <summary>
    /// Maps a stored media segment to the episode window DTO (ticks → seconds); null passes
    /// through so "no segment" survives the mapping. Extracted as a static so the conversion is
    /// unit-testable without Jellyfin plumbing.
    /// </summary>
    internal static SegmentBrowseController.SegmentWindowDto? ToWindowDto(MediaSegmentDto? segment)
    {
        if (segment is null)
        {
            return null;
        }

        return new SegmentBrowseController.SegmentWindowDto
        {
            StartSeconds = segment.StartTicks / (double)TimeSpan.TicksPerSecond,
            EndSeconds = segment.EndTicks / (double)TimeSpan.TicksPerSecond,
        };
    }
}
