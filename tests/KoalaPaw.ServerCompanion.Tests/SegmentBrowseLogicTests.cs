using System;
using Jellyfin.Database.Implementations.Enums;
using System.Linq;
using KoalaPaw.ServerCompanion.Api;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Segments;
using MediaBrowser.Model.MediaSegments;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The segment browse tiers' pure logic: library/series/season rollup grouping (empty, partial
/// and full rollups, tier filtering, name/index ordering), the episode list projection and the
/// scan-record / window DTO mappings.
/// </summary>
public class SegmentBrowseLogicTests
{
    private static readonly Guid Library1Id = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Library2Id = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Library3Id = new("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Series1Id = new("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Series2Id = new("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Season1Id = new("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Season2Id = new("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid Season3Id = new("00000000-0000-0000-0000-0000000000c3");

    // --- Scan-record mapping ---

    [Fact]
    public void ToScanDto_nullRecord_returnsNull()
    {
        Assert.Null(SegmentBrowseLogic.ToScanDto(null));
    }

    [Fact]
    public void ToScanDto_mapsEveryField()
    {
        var finished = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var dto = SegmentBrowseLogic.ToScanDto(ScanRecord(
            Season1Id,
            finished,
            outcome: AnalysisStore.SeasonOutcome.Partial,
            episodesSampled: 7,
            introEnd: 312.5,
            introConfidence: 0.93,
            outroLength: 41.0,
            outroConfidence: 0.81));

        Assert.NotNull(dto);
        Assert.Equal(finished, dto!.FinishedUtc);
        Assert.Equal("Partial", dto.Outcome);
        Assert.Equal(7, dto.EpisodesSampled);
        Assert.Equal(312.5, dto.IntroEndSeconds);
        Assert.Equal(0.93, dto.IntroConfidence);
        Assert.Equal(41.0, dto.OutroLengthSeconds);
        Assert.Equal(0.81, dto.OutroConfidence);
    }

    [Theory]
    [InlineData(AnalysisStore.SeasonOutcome.Detected, "Detected")]
    [InlineData(AnalysisStore.SeasonOutcome.Partial, "Partial")]
    [InlineData(AnalysisStore.SeasonOutcome.NoCorrelation, "NoCorrelation")]
    [InlineData(AnalysisStore.SeasonOutcome.FingerprintFailures, "FingerprintFailures")]
    [InlineData(AnalysisStore.SeasonOutcome.Error, "Error")]
    public void ToScanDto_outcomeMapsToItsEnumName(AnalysisStore.SeasonOutcome outcome, string expected)
    {
        var dto = SegmentBrowseLogic.ToScanDto(ScanRecord(Season1Id, DateTime.UtcNow, outcome: outcome));

        Assert.Equal(expected, dto!.Outcome);
    }

    // --- Window mapping ---

    [Fact]
    public void ToWindowDto_nullSegment_returnsNull()
    {
        Assert.Null(SegmentBrowseLogic.ToWindowDto(null));
    }

    [Theory]
    [InlineData(0L, 0L, 0.0, 0.0)]
    [InlineData(10_000_000L, 95_000_000L, 1.0, 9.5)]
    [InlineData(12_345_678L, 1_234_567_890L, 1.2345678, 123.456789)]
    public void ToWindowDto_convertsTicksToSeconds(long startTicks, long endTicks, double startSeconds, double endSeconds)
    {
        var dto = SegmentBrowseLogic.ToWindowDto(Segment(MediaSegmentType.Intro, startTicks, endTicks));

        Assert.NotNull(dto);
        Assert.Equal(startSeconds, dto!.StartSeconds);
        Assert.Equal(endSeconds, dto.EndSeconds);
    }

    // --- Library rollup ---

    [Fact]
    public void GroupLibraries_empty_returnsEmpty()
    {
        Assert.Empty(SegmentBrowseLogic.GroupLibraries([]));
    }

    [Fact]
    public void GroupLibraries_groupsByLibrary_rollsUpStates_skipsLibrarylessRows()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.Detected, libraryId: Library1Id, libraryName: "Kids"),
            Row(Season1Id, EpisodeSegmentState.Partial, libraryId: Library1Id, libraryName: "Kids"),
            Row(Season2Id, EpisodeSegmentState.Detected, libraryId: Library1Id, libraryName: "Kids"),
            Row(Season2Id, EpisodeSegmentState.Failed, libraryId: Library1Id, libraryName: "Kids", failureReason: "ffmpeg exploded"),
            Row(Season2Id, EpisodeSegmentState.NotScanned, libraryId: Library1Id, libraryName: "Kids"),
            Row(Season1Id, EpisodeSegmentState.Detected, libraryId: Library2Id, libraryName: "Movies"),
            Row(Season1Id, EpisodeSegmentState.Detected), // no library resolved — the tier skips it
        };

        var result = SegmentBrowseLogic.GroupLibraries(rows);

        Assert.Collection(
            result,
            library =>
            {
                Assert.Equal(Library1Id, library.LibraryId);
                Assert.Equal("Kids", library.Name);
                Assert.Equal(5, library.Episodes);
                Assert.Equal(2, library.Detected);
                Assert.Equal(1, library.Partial);
                Assert.Equal(1, library.Failed);
                Assert.Equal(1, library.NotScanned);
            },
            library =>
            {
                Assert.Equal(Library2Id, library.LibraryId);
                Assert.Equal("Movies", library.Name);
                Assert.Equal(1, library.Episodes);
                Assert.Equal(1, library.Detected);
                Assert.Equal(0, library.Partial);
                Assert.Equal(0, library.Failed);
                Assert.Equal(0, library.NotScanned);
            });
    }

    [Fact]
    public void GroupLibraries_ordersByName_caseInsensitively()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.NotScanned, libraryId: Library1Id, libraryName: "zoo"),
            Row(Season1Id, EpisodeSegmentState.NotScanned, libraryId: Library2Id, libraryName: "Apple"),
            Row(Season1Id, EpisodeSegmentState.NotScanned, libraryId: Library3Id, libraryName: "applepie"),
        };

        var result = SegmentBrowseLogic.GroupLibraries(rows);

        Assert.Equal(["Apple", "applepie", "zoo"], result.Select(l => l.Name));
    }

    // --- Series rollup ---

    [Fact]
    public void GroupSeries_empty_returnsEmpty()
    {
        Assert.Empty(SegmentBrowseLogic.GroupSeries(Series1Id, []));
    }

    [Fact]
    public void GroupSeries_groupsBySeries_rollsUpCounts()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.Detected, libraryId: Library1Id, seriesId: Series1Id, seriesName: "Show A"),
            Row(Season2Id, EpisodeSegmentState.Failed, libraryId: Library1Id, seriesId: Series1Id, seriesName: "Show A", failureReason: "boom"),
            Row(Season2Id, EpisodeSegmentState.Partial, libraryId: Library1Id, seriesId: Series2Id, seriesName: "Show B"),
        };

        var result = SegmentBrowseLogic.GroupSeries(Library1Id, rows);

        Assert.Collection(
            result,
            series =>
            {
                Assert.Equal(Series1Id, series.SeriesId);
                Assert.Equal("Show A", series.Name);
                Assert.Equal(2, series.Episodes);
                Assert.Equal(1, series.Detected);
                Assert.Equal(0, series.Partial);
                Assert.Equal(1, series.Failed);
                Assert.Equal(0, series.NotScanned);
            },
            series =>
            {
                Assert.Equal(Series2Id, series.SeriesId);
                Assert.Equal("Show B", series.Name);
                Assert.Equal(1, series.Episodes);
                Assert.Equal(1, series.Partial);
            });
    }

    [Fact]
    public void GroupSeries_skipsRowsFromOtherLibraries()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.Detected, libraryId: Library2Id, seriesId: Series1Id, seriesName: "Other library"),
        };

        Assert.Empty(SegmentBrowseLogic.GroupSeries(Library1Id, rows));
    }

    [Fact]
    public void GroupSeries_fallsBackToSeasonParentIdWhenSeriesUnresolved()
    {
        var rows = new[]
        {
            // Series never resolved: the season's parent (the series id) is the grouping key, name absent.
            Row(Season1Id, EpisodeSegmentState.Detected, libraryId: Library1Id, seasonParentId: Series1Id),
            Row(Season1Id, EpisodeSegmentState.NotScanned, libraryId: Library1Id, seasonParentId: Series1Id),
        };

        var result = SegmentBrowseLogic.GroupSeries(Library1Id, rows);

        var series = Assert.Single(result);
        Assert.Equal(Series1Id, series.SeriesId);
        Assert.Null(series.Name);
        Assert.Equal(2, series.Episodes);
        Assert.Equal(1, series.Detected);
        Assert.Equal(1, series.NotScanned);
    }

    // --- Season rollup ---

    [Fact]
    public void GroupSeasons_empty_returnsEmpty()
    {
        Assert.Empty(SegmentBrowseLogic.GroupSeasons(Series1Id, [], _ => null));
    }

    [Fact]
    public void GroupSeasons_groupsBySeason_withCounts_scanRecordAndFailureReason()
    {
        var finished = new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.Detected, seriesId: Series1Id, seasonName: "Season 1", seasonIndexNumber: 1),
            Row(Season1Id, EpisodeSegmentState.Failed, seriesId: Series1Id, seasonName: "Season 1", seasonIndexNumber: 1, failureReason: "ffmpeg died"),
            Row(Season1Id, EpisodeSegmentState.Partial, seriesId: Series1Id, seasonName: "Season 1", seasonIndexNumber: 1),
            Row(Season2Id, EpisodeSegmentState.Detected, seriesId: Series1Id, seasonName: "Season 2", seasonIndexNumber: 2),
        };

        var result = SegmentBrowseLogic.GroupSeasons(
            Series1Id,
            rows,
            seasonId => seasonId == Season1Id ? ScanRecord(Season1Id, finished, episodesSampled: 3) : null);

        Assert.Collection(
            result,
            season =>
            {
                Assert.Equal(Season1Id, season.SeasonId);
                Assert.Equal("Season 1", season.Name);
                Assert.Equal(1, season.IndexNumber);
                Assert.Equal(3, season.Episodes);
                Assert.Equal(1, season.Detected);
                Assert.Equal(1, season.Partial);
                Assert.Equal(1, season.Failed);
                Assert.Equal(0, season.NotScanned);
                Assert.NotNull(season.Scan);
                Assert.Equal("Detected", season.Scan!.Outcome);
                Assert.Equal(3, season.Scan.EpisodesSampled);
                Assert.Equal("ffmpeg died", season.FailureReason);
            },
            season =>
            {
                Assert.Equal(Season2Id, season.SeasonId);
                Assert.Equal("Season 2", season.Name);
                Assert.Equal(2, season.IndexNumber);
                Assert.Equal(1, season.Episodes);
                Assert.Null(season.Scan);
                Assert.Null(season.FailureReason);
            });
    }

    [Fact]
    public void GroupSeasons_skipsRowsFromOtherSeries_butKeepsSerieslessSeasonMatches()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.Detected, seriesId: Series2Id, seriesName: "Other show"),
            Row(Season2Id, EpisodeSegmentState.Detected, seasonParentId: Series1Id), // series unresolved: matches via the season's parent
        };

        var result = SegmentBrowseLogic.GroupSeasons(Series1Id, rows, _ => null);

        var season = Assert.Single(result);
        Assert.Equal(Season2Id, season.SeasonId);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("first", null, "first")]
    [InlineData(null, "second", "second")]
    [InlineData("first", "second", "first")]
    public void GroupSeasons_failureReason_takesFirstNonNull(string? first, string? second, string? expected)
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.Failed, seriesId: Series1Id, failureReason: first),
            Row(Season1Id, EpisodeSegmentState.Failed, seriesId: Series1Id, failureReason: second),
        };

        var result = SegmentBrowseLogic.GroupSeasons(Series1Id, rows, _ => null);

        var season = Assert.Single(result);
        Assert.Equal(expected, season.FailureReason);
    }

    [Fact]
    public void GroupSeasons_ordersByIndexThenName()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.Detected, seriesId: Series1Id, seasonName: "Zeta", seasonIndexNumber: 2),
            Row(Season2Id, EpisodeSegmentState.Detected, seriesId: Series1Id, seasonName: "Beta", seasonIndexNumber: 1),
            Row(Season3Id, EpisodeSegmentState.Detected, seasonParentId: Series1Id, seasonName: "alpha"),
        };

        var result = SegmentBrowseLogic.GroupSeasons(Series1Id, rows, _ => null);

        // Null index sorts first (Comparer<int?>.Default), then index, then name case-insensitively.
        Assert.Equal([Season3Id, Season2Id, Season1Id], result.Select(s => s.SeasonId));
        Assert.Equal(["alpha", "Beta", "Zeta"], result.Select(s => s.Name));
    }

    // --- Episode list ---

    [Fact]
    public void GroupEpisodes_empty_returnsEmpty()
    {
        Assert.Empty(SegmentBrowseLogic.GroupEpisodes(Season1Id, [], null));
    }

    [Fact]
    public void GroupEpisodes_mapsEveryField()
    {
        var scan = ScanRecord(Season1Id, new DateTime(2026, 8, 30, 9, 0, 0, DateTimeKind.Utc), introConfidence: 0.9, outroConfidence: 0.8);
        var episodeId = Guid.NewGuid();
        var rows = new[]
        {
            Row(
                Season1Id,
                EpisodeSegmentState.Partial,
                episodeId: episodeId,
                episodeIndexNumber: 4,
                episodeName: "Pilot",
                runtimeTicks: 12_500_000,
                failureReason: "half scanned",
                intro: Segment(MediaSegmentType.Intro, 0, 30_000_000),
                outro: Segment(MediaSegmentType.Outro, 1_000_000_000, 1_100_000_000)),
        };

        var result = SegmentBrowseLogic.GroupEpisodes(Season1Id, rows, scan);

        var dto = Assert.Single(result);
        Assert.Equal(episodeId, dto.EpisodeId);
        Assert.Equal(4, dto.IndexNumber);
        Assert.Equal("Pilot", dto.Name);
        Assert.Equal(1.25, dto.RuntimeSeconds);
        Assert.Equal(EpisodeSegmentState.Partial, dto.State);
        Assert.NotNull(dto.Intro);
        Assert.Equal(0.0, dto.Intro!.StartSeconds);
        Assert.Equal(3.0, dto.Intro.EndSeconds);
        Assert.NotNull(dto.Outro);
        Assert.Equal(100.0, dto.Outro!.StartSeconds);
        Assert.Equal(110.0, dto.Outro.EndSeconds);
        Assert.Equal("half scanned", dto.FailureReason);
        Assert.Equal(0.9, dto.IntroConfidence);
        Assert.Equal(0.8, dto.OutroConfidence);
    }

    [Fact]
    public void GroupEpisodes_withoutRuntimeSegmentsOrScan_mapsNulls()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.NotScanned, episodeIndexNumber: 1),
        };

        var dto = Assert.Single(SegmentBrowseLogic.GroupEpisodes(Season1Id, rows, null));

        Assert.Null(dto.RuntimeSeconds);
        Assert.Null(dto.Intro);
        Assert.Null(dto.Outro);
        Assert.Null(dto.FailureReason);
        Assert.Null(dto.IntroConfidence);
        Assert.Null(dto.OutroConfidence);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, 0.0)]
    [InlineData(10_000_000L, 1.0)]
    [InlineData(12_345_678L, 1.2345678)]
    public void GroupEpisodes_runtimeTicksConvertToSeconds(long? ticks, double? expected)
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.NotScanned, runtimeTicks: ticks),
        };

        var dto = Assert.Single(SegmentBrowseLogic.GroupEpisodes(Season1Id, rows, null));

        Assert.Equal(expected, dto.RuntimeSeconds);
    }

    [Fact]
    public void GroupEpisodes_skipsOtherSeasons_andOrdersByIndexThenName()
    {
        var rows = new[]
        {
            Row(Season1Id, EpisodeSegmentState.NotScanned, episodeIndexNumber: 2, episodeName: "b"),
            Row(Season1Id, EpisodeSegmentState.NotScanned, episodeIndexNumber: 1, episodeName: "z"),
            Row(Season2Id, EpisodeSegmentState.NotScanned, episodeIndexNumber: 1, episodeName: "other season"),
            Row(Season1Id, EpisodeSegmentState.NotScanned, episodeIndexNumber: 1, episodeName: "a"),
            Row(Season1Id, EpisodeSegmentState.NotScanned, episodeName: "special"),
        };

        var result = SegmentBrowseLogic.GroupEpisodes(Season1Id, rows, null);

        // Null index sorts first (Comparer<int?>.Default), then index, then name case-insensitively.
        Assert.Equal([null, 1, 1, 2], result.Select(e => e.IndexNumber));
        Assert.Equal(["special", "a", "z", "b"], result.Select(e => e.Name));
    }

    // --- Helpers ---

    private static SegmentBrowseLogic.EpisodeRow Row(
        Guid seasonId,
        string state,
        Guid? libraryId = null,
        string? libraryName = null,
        Guid? seriesId = null,
        string? seriesName = null,
        string? seasonName = null,
        int? seasonIndexNumber = null,
        Guid? seasonParentId = null,
        Guid? episodeId = null,
        int? episodeIndexNumber = null,
        string? episodeName = null,
        long? runtimeTicks = null,
        string? failureReason = null,
        MediaSegmentDto? intro = null,
        MediaSegmentDto? outro = null) =>
        new(
            episodeId ?? Guid.NewGuid(),
            episodeIndexNumber,
            episodeName ?? "Episode",
            runtimeTicks,
            libraryId,
            libraryName,
            seriesId,
            seriesName,
            seasonId,
            seasonParentId ?? Guid.Empty,
            seasonName ?? "Season",
            seasonIndexNumber,
            state,
            failureReason,
            intro,
            outro);

    private static MediaSegmentDto Segment(MediaSegmentType type, long startTicks, long endTicks) =>
        new() { Type = type, StartTicks = startTicks, EndTicks = endTicks };

    private static AnalysisStore.SeasonScanRecord ScanRecord(
        Guid seasonId,
        DateTime finishedUtc,
        AnalysisStore.SeasonOutcome outcome = AnalysisStore.SeasonOutcome.Detected,
        int episodesSampled = 0,
        double? introEnd = null,
        double? introConfidence = null,
        double? outroLength = null,
        double? outroConfidence = null) =>
        new()
        {
            SeasonId = seasonId,
            FinishedUtc = finishedUtc,
            Outcome = outcome,
            EpisodesSampled = episodesSampled,
            IntroEndSeconds = introEnd,
            IntroConfidence = introConfidence,
            OutroLengthSeconds = outroLength,
            OutroConfidence = outroConfidence,
        };
}
