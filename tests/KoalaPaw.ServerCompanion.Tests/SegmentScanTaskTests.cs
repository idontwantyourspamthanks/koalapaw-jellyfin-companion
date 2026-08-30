using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using KoalaPaw.ServerCompanion.ThemeSongs;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The scan orchestration decisions extracted from SegmentScanTask: which seasons get scanned
/// (grouping floor + ordering), the incremental skip rule (missing Intro/Outro), the
/// failure-suppression rule (all episodes failed), the scan-report aggregation math, and the
/// theme-backfill cap.
/// </summary>
public class SegmentScanTaskTests : IDisposable
{
    private readonly string _tempDir;

    private static readonly DateTime FinishedUtc = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    public SegmentScanTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "koalapaw-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private AnalysisStore CreateStore() =>
        new(_tempDir, NullLogger<AnalysisStore>.Instance);

    private static Episode Episode(Guid id, Guid seasonId, string path = "/tv/show/s01e01.mkv") =>
        new()
        {
            Id = id,
            ParentId = seasonId,
            Path = path,
        };

    private static Series Series(Guid id, string path) =>
        new()
        {
            Id = id,
            Path = path,
        };

    private string CreateSeriesDir(string name)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- Season selection ---

    [Fact]
    public void SelectSeasonsForScan_groupsBySeason_requiresAtLeastTwoEpisodes()
    {
        var single = Episode(Guid.NewGuid(), Guid.NewGuid());
        var seasonA = Guid.NewGuid();
        var seasonB = Guid.NewGuid();
        var episodes = new[]
        {
            single,
            Episode(Guid.NewGuid(), seasonA, "/tv/a/s01e01.mkv"),
            Episode(Guid.NewGuid(), seasonA, "/tv/a/s01e02.mkv"),
            Episode(Guid.NewGuid(), seasonB, "/tv/b/s01e01.mkv"),
            Episode(Guid.NewGuid(), seasonB, "/tv/b/s01e02.mkv"),
            Episode(Guid.NewGuid(), seasonB, "/tv/b/s01e03.mkv"),
        };

        var seasons = SegmentScanTask.SelectSeasonsForScan(episodes);

        Assert.Equal(2, seasons.Count);
        Assert.DoesNotContain(seasons, g => g.Key == single.ParentId);
        var byId = seasons.ToDictionary(g => g.Key);
        Assert.Equal(2, byId[seasonA].Count());
        Assert.Equal(3, byId[seasonB].Count());
    }

    [Fact]
    public void SelectSeasonsForScan_ordersSeasonsByParentId()
    {
        var first = new Guid("00000000-0000-0000-0000-000000000001");
        var second = new Guid("00000000-0000-0000-0000-000000000002");
        var third = new Guid("00000000-0000-0000-0000-000000000003");
        var episodes = new[]
        {
            Episode(Guid.NewGuid(), third, "/tv/c/s01e01.mkv"),
            Episode(Guid.NewGuid(), third, "/tv/c/s01e02.mkv"),
            Episode(Guid.NewGuid(), first, "/tv/a/s01e01.mkv"),
            Episode(Guid.NewGuid(), first, "/tv/a/s01e02.mkv"),
            Episode(Guid.NewGuid(), second, "/tv/b/s01e01.mkv"),
            Episode(Guid.NewGuid(), second, "/tv/b/s01e02.mkv"),
        };

        var seasons = SegmentScanTask.SelectSeasonsForScan(episodes);

        Assert.Equal([first, second, third], seasons.Select(g => g.Key));
    }

    [Fact]
    public void SelectSeasonsForScan_dropsPathlessAndOrphanedEpisodes()
    {
        var season = Guid.NewGuid();
        var episodes = new[]
        {
            Episode(Guid.NewGuid(), season, "  "), // whitespace path — nothing to fingerprint
            Episode(Guid.NewGuid(), Guid.Empty), // orphaned row without a season parent
            Episode(Guid.NewGuid(), Guid.Empty),
            Episode(Guid.NewGuid(), season, "/tv/a/s01e01.mkv"),
            Episode(Guid.NewGuid(), season, "/tv/a/s01e02.mkv"),
        };

        var seasons = SegmentScanTask.SelectSeasonsForScan(episodes);

        var only = Assert.Single(seasons);
        Assert.Equal(season, only.Key);
        Assert.Equal(2, only.Count());
    }

    [Fact]
    public void SelectSeasonsForScan_noQualifyingSeason_returnsEmpty()
    {
        var episodes = new[]
        {
            Episode(Guid.NewGuid(), Guid.NewGuid()), // lone episode in its season
            Episode(Guid.NewGuid(), Guid.NewGuid(), "  "),
        };

        Assert.Empty(SegmentScanTask.SelectSeasonsForScan(episodes));
    }

    // --- Failure suppression ---

    [Fact]
    public void SeasonFullyFailed_allEpisodesFailed_returnsTrue()
    {
        using var store = CreateStore();
        var season = Guid.NewGuid();
        var episodes = new[] { Episode(Guid.NewGuid(), season), Episode(Guid.NewGuid(), season) };
        foreach (var ep in episodes)
        {
            store.RecordFailure(ep.Id, "segments", "no confident intro/outro correlation");
        }

        Assert.True(SegmentScanTask.SeasonFullyFailed(store, episodes));
    }

    [Fact]
    public void SeasonFullyFailed_anyEpisodeClean_returnsFalse()
    {
        using var store = CreateStore();
        var season = Guid.NewGuid();
        var episodes = new[] { Episode(Guid.NewGuid(), season), Episode(Guid.NewGuid(), season) };
        store.RecordFailure(episodes[0].Id, "segments", "no confident intro/outro correlation");

        Assert.False(SegmentScanTask.SeasonFullyFailed(store, episodes));
    }

    [Fact]
    public void SeasonFullyFailed_loudnessFailuresDoNotCount()
    {
        using var store = CreateStore();
        var season = Guid.NewGuid();
        var episodes = new[] { Episode(Guid.NewGuid(), season), Episode(Guid.NewGuid(), season) };
        store.RecordFailure(episodes[0].Id, "loudness", "loudnorm returned no result");
        store.RecordFailure(episodes[1].Id, "loudness", "loudnorm returned no result");

        Assert.False(SegmentScanTask.SeasonFullyFailed(store, episodes));
    }

    // --- Incremental skip rule ---

    [Theory]
    [InlineData(false, false, true)] // no segments at all → needs a scan
    [InlineData(true, false, true)] // intro only → still missing outro
    [InlineData(false, true, true)] // outro only → still missing intro
    [InlineData(true, true, false)] // both present → incremental skip
    public void MissingIntroOrOutro_coversFullTruthTable(bool hasIntro, bool hasOutro, bool expected)
    {
        var segments = new List<MediaSegmentDto>();
        if (hasIntro)
        {
            segments.Add(new MediaSegmentDto { Type = MediaSegmentType.Intro });
        }

        if (hasOutro)
        {
            segments.Add(new MediaSegmentDto { Type = MediaSegmentType.Outro });
        }

        Assert.Equal(expected, SegmentScanTask.MissingIntroOrOutro(segments));
    }

    [Fact]
    public void MissingIntroOrOutro_duplicateSegmentsOfSameType_doNotCount()
    {
        var segments = new[]
        {
            new MediaSegmentDto { Type = MediaSegmentType.Intro },
            new MediaSegmentDto { Type = MediaSegmentType.Intro },
            new MediaSegmentDto { Type = MediaSegmentType.Outro },
        };

        Assert.False(SegmentScanTask.MissingIntroOrOutro(segments));
    }

    // --- Scan-report aggregation ---

    [Fact]
    public void BuildSegmentScanReport_sumsCountsAndAveragesConfidences()
    {
        var report = SegmentScanTask.BuildSegmentScanReport(
            episodesScanned: 24,
            introsFound: 3,
            outrosFound: 2,
            introConfidences: [0.75, 0.75, 0.75],
            outroConfidences: [0.5, 0.25],
            finishedUtc: FinishedUtc);

        Assert.Equal(24, report.EpisodesScanned);
        Assert.Equal(3, report.IntrosFound);
        Assert.Equal(2, report.OutrosFound);
        Assert.Equal(0.75, report.IntroConfidence);
        Assert.Equal(0.375, report.OutroConfidence);
        Assert.Equal(FinishedUtc, report.FinishedUtc);
    }

    [Fact]
    public void BuildSegmentScanReport_noSamples_keepsNullConfidences()
    {
        var report = SegmentScanTask.BuildSegmentScanReport(0, 0, 0, [], [], FinishedUtc);

        Assert.Equal(0, report.EpisodesScanned);
        Assert.Equal(0, report.IntrosFound);
        Assert.Equal(0, report.OutrosFound);
        Assert.Null(report.IntroConfidence);
        Assert.Null(report.OutroConfidence);
        Assert.Equal(FinishedUtc, report.FinishedUtc);
    }

    [Fact]
    public void BuildSegmentScanReport_singleSample_usesIt()
    {
        var report = SegmentScanTask.BuildSegmentScanReport(8, 1, 0, [0.91], [], FinishedUtc);

        Assert.Equal(0.91, report.IntroConfidence);
        Assert.Null(report.OutroConfidence);
    }

    [Fact]
    public void BuildSegmentScanReport_introAndOutroAveragedIndependently()
    {
        var report = SegmentScanTask.BuildSegmentScanReport(16, 2, 1, [0.5, 0.75], [0.25], FinishedUtc);

        Assert.Equal(0.625, report.IntroConfidence);
        Assert.Equal(0.25, report.OutroConfidence);
    }

    // --- Theme backfill ---

    [Fact]
    public void SelectThemesToBackfill_keepsSeriesWithoutTheme()
    {
        var series = Series(Guid.NewGuid(), CreateSeriesDir("clean"));

        var selected = SegmentScanTask.SelectThemesToBackfill([series], CreateStore());

        var only = Assert.Single(selected);
        Assert.Equal(series.Id, only.Id);
    }

    [Fact]
    public void SelectThemesToBackfill_excludesSeriesWithThemeFile()
    {
        var dir = CreateSeriesDir("with-file");
        File.WriteAllText(Path.Combine(dir, "theme.mp3"), "not really audio");
        var series = Series(Guid.NewGuid(), dir);

        Assert.Empty(SegmentScanTask.SelectThemesToBackfill([series], CreateStore()));
    }

    [Fact]
    public void SelectThemesToBackfill_excludesSeriesWithThemeMusicFolder()
    {
        var dir = CreateSeriesDir("with-folder");
        Directory.CreateDirectory(Path.Combine(dir, ThemeFromIntroLogic.ThemeMusicFolderName));
        var series = Series(Guid.NewGuid(), dir);

        Assert.Empty(SegmentScanTask.SelectThemesToBackfill([series], CreateStore()));
    }

    [Fact]
    public void SelectThemesToBackfill_excludesSeriesWithThemeExtractFailure()
    {
        using var store = CreateStore();
        var failed = Series(Guid.NewGuid(), CreateSeriesDir("failed"));
        var clean = Series(Guid.NewGuid(), CreateSeriesDir("clean"));
        store.RecordFailure(failed.Id, ThemeFromIntroExtractor.AnalyserName, "theme extract failed");

        var selected = SegmentScanTask.SelectThemesToBackfill([failed, clean], store);

        var only = Assert.Single(selected);
        Assert.Equal(clean.Id, only.Id);
    }

    [Fact]
    public void SelectThemesToBackfill_otherAnalyserFailure_doesNotCount()
    {
        using var store = CreateStore();
        var series = Series(Guid.NewGuid(), CreateSeriesDir("other-failure"));
        store.RecordFailure(series.Id, "segments", "no confident intro/outro correlation");

        var selected = SegmentScanTask.SelectThemesToBackfill([series], store);

        Assert.Single(selected);
    }

    [Fact]
    public void SelectThemesToBackfill_dropsSeriesWithoutPath()
    {
        var pathless = Series(Guid.NewGuid(), " ");
        var withPath = Series(Guid.NewGuid(), CreateSeriesDir("with-path"));

        var selected = SegmentScanTask.SelectThemesToBackfill([pathless, withPath], CreateStore());

        var only = Assert.Single(selected);
        Assert.Equal(withPath.Id, only.Id);
    }

    [Fact]
    public void SelectThemesToBackfill_capsAtMaxThemeBackfillPerRun()
    {
        var many = Enumerable.Range(0, SegmentScanTask.MaxThemeBackfillPerRun + 1)
            .Select(i => Series(Guid.NewGuid(), CreateSeriesDir("series-" + i)))
            .ToList();

        var selected = SegmentScanTask.SelectThemesToBackfill(many, CreateStore());

        Assert.Equal(SegmentScanTask.MaxThemeBackfillPerRun, selected.Count);
    }

    [Fact]
    public void SelectThemesToBackfill_exactlyAtCap_keepsAll()
    {
        var many = Enumerable.Range(0, SegmentScanTask.MaxThemeBackfillPerRun)
            .Select(i => Series(Guid.NewGuid(), CreateSeriesDir("series-" + i)))
            .ToList();

        var selected = SegmentScanTask.SelectThemesToBackfill(many, CreateStore());

        Assert.Equal(SegmentScanTask.MaxThemeBackfillPerRun, selected.Count);
    }
}
