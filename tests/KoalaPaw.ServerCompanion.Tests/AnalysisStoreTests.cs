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
}