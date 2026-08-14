using System;
using System.IO;
using System.Linq;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using KoalaPaw.ServerCompanion.Segments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// Per-season scan records: SeasonAnalysisResult → SeasonScanRecord mapping, JSON round-trip,
/// stale-season pruning, and the success-invalidation seam that clears "segments" failures.
/// Note on outcome coverage: the mapping derives Detected / Partial / NoCorrelation per the
/// contract; FingerprintFailures has no producer in this release's mapping (reserved for a
/// fingerprint-specific path), and Error is written by SegmentScanTask's catch block — both are
/// exercised at the store level by the all-outcomes round-trip test.
/// </summary>
public class SeasonScanRecordTests : IDisposable
{
    private readonly string _tempDir;

    private static readonly DateTime FinishedUtc = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    public SeasonScanRecordTests()
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

    private static AnalysisStore.SeasonScanRecord SeasonRecordFor(Guid seasonId) =>
        new()
        {
            SeasonId = seasonId,
            SeriesId = Guid.NewGuid(),
            FinishedUtc = FinishedUtc,
            EpisodesSampled = 4,
            Outcome = AnalysisStore.SeasonOutcome.NoCorrelation,
        };

    [Fact]
    public void ToSeasonScanRecord_bothGuesses_mapsDetected()
    {
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var result = new IntroSeasonAnalyser.SeasonAnalysisResult(
            8,
            8,
            8,
            8,
            0.91,
            0.84,
            new IntroCorrelator.IntroGuess(0, 42.5, 0.91),
            new IntroCorrelator.IntroGuess(0, 30.0, 0.84));

        var record = SegmentScanTask.ToSeasonScanRecord(seasonId, seriesId, FinishedUtc, result);

        Assert.Equal(seasonId, record.SeasonId);
        Assert.Equal(seriesId, record.SeriesId);
        Assert.Equal(FinishedUtc, record.FinishedUtc);
        Assert.Equal(8, record.EpisodesSampled);
        Assert.Equal(AnalysisStore.SeasonOutcome.Detected, record.Outcome);
        Assert.Equal(42.5, record.IntroEndSeconds);
        Assert.Equal(0.91, record.IntroConfidence);
        Assert.Equal(30.0, record.OutroLengthSeconds);
        Assert.Equal(0.84, record.OutroConfidence);
    }

    [Fact]
    public void ToSeasonScanRecord_introOnly_mapsPartial()
    {
        var result = new IntroSeasonAnalyser.SeasonAnalysisResult(
            8,
            8,
            8,
            0,
            0.91,
            null,
            new IntroCorrelator.IntroGuess(0, 42.5, 0.91),
            null);

        var record = SegmentScanTask.ToSeasonScanRecord(Guid.NewGuid(), Guid.NewGuid(), FinishedUtc, result);

        Assert.Equal(AnalysisStore.SeasonOutcome.Partial, record.Outcome);
        Assert.Equal(42.5, record.IntroEndSeconds);
        Assert.Equal(0.91, record.IntroConfidence);
        Assert.Null(record.OutroLengthSeconds);
        Assert.Null(record.OutroConfidence);
    }

    [Fact]
    public void ToSeasonScanRecord_outroOnly_mapsPartial()
    {
        var result = new IntroSeasonAnalyser.SeasonAnalysisResult(
            8,
            8,
            0,
            8,
            null,
            0.84,
            null,
            new IntroCorrelator.IntroGuess(0, 30.0, 0.84));

        var record = SegmentScanTask.ToSeasonScanRecord(Guid.NewGuid(), Guid.NewGuid(), FinishedUtc, result);

        Assert.Equal(AnalysisStore.SeasonOutcome.Partial, record.Outcome);
        Assert.Null(record.IntroEndSeconds);
        Assert.Null(record.IntroConfidence);
        Assert.Equal(30.0, record.OutroLengthSeconds);
        Assert.Equal(0.84, record.OutroConfidence);
    }

    [Fact]
    public void ToSeasonScanRecord_noGuesses_mapsNoCorrelation()
    {
        var result = new IntroSeasonAnalyser.SeasonAnalysisResult(8, 0, 0, 0, null, null, null, null);

        var record = SegmentScanTask.ToSeasonScanRecord(Guid.NewGuid(), Guid.NewGuid(), FinishedUtc, result);

        Assert.Equal(AnalysisStore.SeasonOutcome.NoCorrelation, record.Outcome);
        Assert.Equal(8, record.EpisodesSampled);
        Assert.Null(record.IntroEndSeconds);
        Assert.Null(record.IntroConfidence);
        Assert.Null(record.OutroLengthSeconds);
        Assert.Null(record.OutroConfidence);
    }

    [Fact]
    public void SeasonScanRecords_persistAcrossNewInstance_allOutcomes()
    {
        var records = Enum.GetValues<AnalysisStore.SeasonOutcome>()
            .Select((outcome, i) => new AnalysisStore.SeasonScanRecord
            {
                SeasonId = Guid.NewGuid(),
                SeriesId = Guid.NewGuid(),
                FinishedUtc = FinishedUtc.AddMinutes(i),
                EpisodesSampled = 8,
                Outcome = outcome,
                IntroEndSeconds = 42.5,
                IntroConfidence = 0.91,
                OutroLengthSeconds = 30.0,
                OutroConfidence = 0.84,
            })
            .ToList();

        using (var store = CreateStore())
        {
            foreach (var record in records)
            {
                store.SetSeasonScanRecord(record);
            }
        } // Dispose flushes

        using (var store = CreateStore())
        {
            Assert.Equal(records.Count, store.GetSeasonScanRecords().Count);
            foreach (var record in records)
            {
                var loaded = store.GetSeasonScanRecord(record.SeasonId);
                Assert.NotNull(loaded);
                Assert.Equal(record.SeriesId, loaded!.SeriesId);
                Assert.Equal(record.FinishedUtc, loaded.FinishedUtc);
                Assert.Equal(record.EpisodesSampled, loaded.EpisodesSampled);
                Assert.Equal(record.Outcome, loaded.Outcome);
                Assert.Equal(record.IntroEndSeconds, loaded.IntroEndSeconds);
                Assert.Equal(record.IntroConfidence, loaded.IntroConfidence);
                Assert.Equal(record.OutroLengthSeconds, loaded.OutroLengthSeconds);
                Assert.Equal(record.OutroConfidence, loaded.OutroConfidence);
            }
        }
    }

    [Fact]
    public void SetSeasonScanRecord_sameSeason_overwritesPriorRecord()
    {
        using var store = CreateStore();
        var seasonId = Guid.NewGuid();
        store.SetSeasonScanRecord(new AnalysisStore.SeasonScanRecord
        {
            SeasonId = seasonId,
            SeriesId = Guid.NewGuid(),
            FinishedUtc = FinishedUtc,
            EpisodesSampled = 3,
            Outcome = AnalysisStore.SeasonOutcome.NoCorrelation,
        });

        store.SetSeasonScanRecord(new AnalysisStore.SeasonScanRecord
        {
            SeasonId = seasonId,
            SeriesId = Guid.NewGuid(),
            FinishedUtc = FinishedUtc.AddHours(1),
            EpisodesSampled = 8,
            Outcome = AnalysisStore.SeasonOutcome.Detected,
            IntroEndSeconds = 42.5,
            IntroConfidence = 0.91,
            OutroLengthSeconds = 30.0,
            OutroConfidence = 0.84,
        });

        var record = store.GetSeasonScanRecord(seasonId);
        Assert.NotNull(record);
        Assert.Equal(AnalysisStore.SeasonOutcome.Detected, record!.Outcome);
        Assert.Equal(8, record.EpisodesSampled);
        Assert.Single(store.GetSeasonScanRecords());
    }

    [Fact]
    public void GetSeasonScanRecord_unknownSeason_returnsNull()
    {
        using var store = CreateStore();

        Assert.Null(store.GetSeasonScanRecord(Guid.NewGuid()));
    }

    [Fact]
    public void PruneSeasonRecordsExcept_removesOnlyStaleSeasons()
    {
        using var store = CreateStore();
        var live = Guid.NewGuid();
        var stale = Guid.NewGuid();
        store.SetSeasonScanRecord(SeasonRecordFor(live));
        store.SetSeasonScanRecord(SeasonRecordFor(stale));

        var removed = store.PruneSeasonRecordsExcept([live]);

        Assert.Equal(1, removed);
        Assert.NotNull(store.GetSeasonScanRecord(live));
        Assert.Null(store.GetSeasonScanRecord(stale));
    }

    [Fact]
    public void PruneSeasonRecordsExcept_noStaleSeasons_returnsZero()
    {
        using var store = CreateStore();
        var live = Guid.NewGuid();
        store.SetSeasonScanRecord(SeasonRecordFor(live));

        Assert.Equal(0, store.PruneSeasonRecordsExcept([live]));
        Assert.NotNull(store.GetSeasonScanRecord(live));
    }

    [Fact]
    public void PruneSeasonRecordsExcept_persistsAcrossNewInstance()
    {
        var live = Guid.NewGuid();
        var stale = Guid.NewGuid();
        using (var store = CreateStore())
        {
            store.SetSeasonScanRecord(SeasonRecordFor(live));
            store.SetSeasonScanRecord(SeasonRecordFor(stale));
            store.PruneSeasonRecordsExcept([live]);
        } // Dispose flushes

        using (var store = CreateStore())
        {
            Assert.NotNull(store.GetSeasonScanRecord(live));
            Assert.Null(store.GetSeasonScanRecord(stale));
        }
    }

    [Fact]
    public void ClearSegmentsFailure_stagedEpisodeDropsStaleFailure()
    {
        // The full AnalyseAndStageAsync path needs a real ffmpeg (ChromaprintProbe is sealed and
        // runs a process) plus fakes for IMediaSegmentManager/ILibraryManager, and this test
        // project has no mocking framework (xunit only). The analyser therefore routes success
        // invalidation through this static seam, called right after each successful
        // RunSegmentPluginProviders staging — so this test proves the rule itself: an episode
        // that failed once but now has segments staged must not keep a stale "segments" failure.
        using var store = CreateStore();
        var episodeId = Guid.NewGuid();
        store.RecordFailure(episodeId, "segments", "no confident intro/outro correlation");
        Assert.True(store.HasFailed(episodeId, "segments"));

        IntroSeasonAnalyser.ClearSegmentsFailure(store, episodeId);

        Assert.False(store.HasFailed(episodeId, "segments"));
    }

    [Fact]
    public void ClearSegmentsFailure_leavesOtherAnalyserFailuresAlone()
    {
        using var store = CreateStore();
        var episodeId = Guid.NewGuid();
        store.RecordFailure(episodeId, "segments", "tail fingerprint failed");
        store.RecordFailure(episodeId, "loudness", "loudnorm returned no result");

        IntroSeasonAnalyser.ClearSegmentsFailure(store, episodeId);

        Assert.False(store.HasFailed(episodeId, "segments"));
        Assert.True(store.HasFailed(episodeId, "loudness"));
    }
}
