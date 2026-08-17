using System;
using System.IO;
using System.Linq;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// Per-item loudness outcome records: measurement → record mapping (success with gain, failure
/// with reason) and the AnalysisStore JSON round-trip, overwrite, and stale-item pruning.
/// </summary>
public class LoudnessScanRecordTests : IDisposable
{
    private readonly string _tempDir;

    private static readonly DateTime FinishedUtc = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    public LoudnessScanRecordTests()
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

    [Fact]
    public void ToScanRecord_success_mapsGainAndNullReason()
    {
        var itemId = Guid.NewGuid();

        var record = LoudnessScanTask.ToScanRecord(itemId, 2.5f, null, FinishedUtc);

        Assert.Equal(itemId, record.ItemId);
        Assert.Equal(2.5f, record.Gain);
        Assert.Null(record.FailureReason);
        Assert.Equal(FinishedUtc, record.FinishedUtc);
    }

    [Fact]
    public void ToScanRecord_negativeGain_roundTrips()
    {
        var record = LoudnessScanTask.ToScanRecord(Guid.NewGuid(), -8.25f, null, FinishedUtc);

        Assert.Equal(-8.25f, record.Gain);
        Assert.Null(record.FailureReason);
    }

    [Fact]
    public void ToScanRecord_failure_mapsReasonAndNullGain()
    {
        var record = LoudnessScanTask.ToScanRecord(Guid.NewGuid(), null, "loudnorm returned no result", FinishedUtc);

        Assert.Null(record.Gain);
        Assert.Equal("loudnorm returned no result", record.FailureReason);
        Assert.Equal(FinishedUtc, record.FinishedUtc);
    }

    [Fact]
    public void LoudnessRecords_persistAcrossNewInstance_successAndFailure()
    {
        var successId = Guid.NewGuid();
        var failureId = Guid.NewGuid();

        using (var store = CreateStore())
        {
            store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord
            {
                ItemId = successId,
                Gain = 2.5f,
                FinishedUtc = FinishedUtc,
            });
            store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord
            {
                ItemId = failureId,
                FailureReason = "loudnorm returned no result",
                FinishedUtc = FinishedUtc.AddMinutes(1),
            });
        } // Dispose flushes

        using (var store = CreateStore())
        {
            var success = store.GetLoudnessScanRecord(successId);
            Assert.NotNull(success);
            Assert.Equal(2.5f, success!.Gain);
            Assert.Null(success.FailureReason);
            Assert.Equal(FinishedUtc, success.FinishedUtc);

            var failure = store.GetLoudnessScanRecord(failureId);
            Assert.NotNull(failure);
            Assert.Null(failure!.Gain);
            Assert.Equal("loudnorm returned no result", failure.FailureReason);
            Assert.Equal(FinishedUtc.AddMinutes(1), failure.FinishedUtc);
        }
    }

    [Fact]
    public void SetLoudnessScanRecord_sameItem_overwritesPriorRecord()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();
        store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord
        {
            ItemId = itemId,
            FailureReason = "first attempt failed",
            FinishedUtc = FinishedUtc,
        });

        store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord
        {
            ItemId = itemId,
            Gain = 3.1f,
            FinishedUtc = FinishedUtc.AddHours(1),
        });

        var record = store.GetLoudnessScanRecord(itemId);
        Assert.NotNull(record);
        Assert.Equal(3.1f, record!.Gain);
        Assert.Null(record.FailureReason);
        Assert.Single(store.GetLoudnessScanRecords());
    }

    [Fact]
    public void GetLoudnessScanRecord_unknownItem_returnsNull()
    {
        using var store = CreateStore();

        Assert.Null(store.GetLoudnessScanRecord(Guid.NewGuid()));
    }

    [Fact]
    public void GetLoudnessScanRecords_returnsNewestFirst()
    {
        using var store = CreateStore();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord { ItemId = a, Gain = 1f, FinishedUtc = FinishedUtc });
        store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord { ItemId = b, Gain = 2f, FinishedUtc = FinishedUtc.AddMinutes(5) });

        var records = store.GetLoudnessScanRecords();

        Assert.Equal(2, records.Count);
        Assert.Equal(b, records[0].ItemId); // finished later → newest first
        Assert.Equal(a, records[1].ItemId);
    }

    [Fact]
    public void PruneLoudnessRecordsExcept_removesOnlyStaleItems()
    {
        using var store = CreateStore();
        var live = Guid.NewGuid();
        var stale = Guid.NewGuid();
        store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord { ItemId = live, Gain = 1f, FinishedUtc = FinishedUtc });
        store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord { ItemId = stale, Gain = 2f, FinishedUtc = FinishedUtc });

        var removed = store.PruneLoudnessRecordsExcept([live]);

        Assert.Equal(1, removed);
        Assert.NotNull(store.GetLoudnessScanRecord(live));
        Assert.Null(store.GetLoudnessScanRecord(stale));
    }

    [Fact]
    public void PruneLoudnessRecordsExcept_noStaleItems_returnsZero()
    {
        using var store = CreateStore();
        var live = Guid.NewGuid();
        store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord { ItemId = live, Gain = 1f, FinishedUtc = FinishedUtc });

        Assert.Equal(0, store.PruneLoudnessRecordsExcept([live]));
        Assert.NotNull(store.GetLoudnessScanRecord(live));
    }

    [Fact]
    public void PruneLoudnessRecordsExcept_persistsAcrossNewInstance()
    {
        var live = Guid.NewGuid();
        var stale = Guid.NewGuid();
        using (var store = CreateStore())
        {
            store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord { ItemId = live, Gain = 1f, FinishedUtc = FinishedUtc });
            store.SetLoudnessScanRecord(new AnalysisStore.LoudnessScanRecord { ItemId = stale, Gain = 2f, FinishedUtc = FinishedUtc });
            store.PruneLoudnessRecordsExcept([live]);
        } // Dispose flushes

        using (var store = CreateStore())
        {
            Assert.NotNull(store.GetLoudnessScanRecord(live));
            Assert.Null(store.GetLoudnessScanRecord(stale));
        }
    }
}
