using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KoalaPaw.ServerCompanion.Bookkeeping;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class AnalysisStoreTests : IDisposable
{
    private readonly string _tempDir;

    public AnalysisStoreTests()
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
    public void HasFailed_afterRecordFailure_returnsTrue()
    {
        using var store = CreateStore();
        var id = Guid.NewGuid();

        store.RecordFailure(id, "loudness", "loudnorm returned no result");

        Assert.True(store.HasFailed(id, "loudness"));
    }

    [Fact]
    public void HasFailed_unknownItem_returnsFalse()
    {
        using var store = CreateStore();

        Assert.False(store.HasFailed(Guid.NewGuid(), "loudness"));
    }

    [Fact]
    public void HasFailed_differentAnalyser_returnsFalse()
    {
        using var store = CreateStore();
        var id = Guid.NewGuid();

        store.RecordFailure(id, "loudness", "reason");

        Assert.False(store.HasFailed(id, "segments"));
    }

    [Fact]
    public void GetFailureCount_tracksDistinctPairs()
    {
        using var store = CreateStore();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        store.RecordFailure(a, "loudness", "r1");
        store.RecordFailure(a, "segments", "r2");
        store.RecordFailure(b, "loudness", "r3");

        Assert.Equal(3, store.GetFailureCount());
    }

    [Fact]
    public void RecordFailure_sameKey_overwrites()
    {
        using var store = CreateStore();
        var id = Guid.NewGuid();

        store.RecordFailure(id, "loudness", "first");
        store.RecordFailure(id, "loudness", "second");

        Assert.Equal(1, store.GetFailureCount());
        Assert.True(store.HasFailed(id, "loudness"));
    }

    [Fact]
    public void ClearFailures_removesAll()
    {
        using var store = CreateStore();
        store.RecordFailure(Guid.NewGuid(), "loudness", "r1");
        store.RecordFailure(Guid.NewGuid(), "segments", "r2");

        store.ClearFailures();

        Assert.Equal(0, store.GetFailureCount());
    }

    [Fact]
    public void Persistence_survivesNewInstance()
    {
        var id = Guid.NewGuid();

        using (var store = CreateStore())
        {
            store.RecordFailure(id, "loudness", "persisted");
        } // Dispose flushes

        using (var store = CreateStore())
        {
            Assert.True(store.HasFailed(id, "loudness"));
            Assert.Equal(1, store.GetFailureCount());
        }
    }

    [Fact]
    public void Persistence_clearSurvivesNewInstance()
    {
        using (var store = CreateStore())
        {
            store.RecordFailure(Guid.NewGuid(), "loudness", "r1");
            store.ClearFailures();
        }

        using (var store = CreateStore())
        {
            Assert.Equal(0, store.GetFailureCount());
        }
    }

    [Fact]
    public void SegmentScanReport_withoutScan_isNull()
    {
        using var store = CreateStore();

        Assert.Null(store.GetSegmentScanReport());
    }

    [Fact]
    public void SegmentScanReport_persistsAcrossNewInstance()
    {
        var finished = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

        using (var store = CreateStore())
        {
            store.SetSegmentScanReport(new AnalysisStore.SegmentScanReport
            {
                EpisodesScanned = 42,
                IntrosFound = 12,
                OutrosFound = 8,
                IntroConfidence = 0.91,
                OutroConfidence = 0.85,
                FinishedUtc = finished,
            });
        } // Dispose flushes

        using (var store = CreateStore())
        {
            var report = store.GetSegmentScanReport();
            Assert.NotNull(report);
            Assert.Equal(42, report!.EpisodesScanned);
            Assert.Equal(12, report.IntrosFound);
            Assert.Equal(8, report.OutrosFound);
            Assert.Equal(0.91, report.IntroConfidence);
            Assert.Equal(0.85, report.OutroConfidence);
            Assert.Equal(finished, report.FinishedUtc);
        }
    }

    [Fact]
    public void CorruptFile_startsFresh()
    {
        File.WriteAllText(Path.Combine(_tempDir, "analysis-failures.json"), "NOT JSON {{{");

        using var store = CreateStore();

        Assert.Equal(0, store.GetFailureCount());
    }

    [Fact]
    public async Task ConcurrentAccess_doesNotThrow()
    {
        using var store = CreateStore();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var id = Guid.NewGuid();
            store.RecordFailure(id, "loudness", $"thread-{i}");
            _ = store.HasFailed(id, "loudness");
            _ = store.GetFailureCount();
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(8, store.GetFailureCount());
    }

    [Fact]
    public void GetFailures_returnsAllRecordsNewestFirst()
    {
        using var store = CreateStore();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        store.RecordFailure(a, "loudness", "first");
        store.RecordFailure(b, "segments", "second");

        var failures = store.GetFailures();

        Assert.Equal(2, failures.Count);
        Assert.Equal(b, failures[0].ItemId); // recorded later → newer timestamp → first
        Assert.Equal(a, failures[1].ItemId);
        Assert.Equal("segments", failures[0].Analyser);
        Assert.Equal("second", failures[0].Reason);
    }

    [Fact]
    public void RemoveFailure_removesOnlyTheMatchingRecord()
    {
        using var store = CreateStore();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.RecordFailure(a, "loudness", "r1");
        store.RecordFailure(a, "segments", "r2");
        store.RecordFailure(b, "loudness", "r3");

        Assert.True(store.RemoveFailure(a, "loudness"));

        Assert.False(store.HasFailed(a, "loudness"));
        Assert.True(store.HasFailed(a, "segments"), "same item, other analyser must survive");
        Assert.True(store.HasFailed(b, "loudness"), "other item must survive");
        Assert.Equal(2, store.GetFailureCount());
    }

    [Fact]
    public void RemoveFailure_missingRecord_returnsFalse()
    {
        using var store = CreateStore();

        Assert.False(store.RemoveFailure(Guid.NewGuid(), "loudness"));
    }

    [Fact]
    public void RemoveFailure_persistsAcrossNewInstance()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using (var store = CreateStore())
        {
            store.RecordFailure(a, "loudness", "r1");
            store.RecordFailure(b, "loudness", "r2");
            store.RemoveFailure(a, "loudness");
        } // Dispose flushes

        using (var store = CreateStore())
        {
            Assert.False(store.HasFailed(a, "loudness"));
            Assert.True(store.HasFailed(b, "loudness"));
        }
    }
}