using System;
using System.IO;
using System.Linq;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The scan orchestration decisions extracted from LoudnessScanTask: candidate selection
/// (already-scanned exclusion + per-run cap), the failure-suppression rule, and the
/// apply/update/failure decision for one measurement.
/// </summary>
public class LoudnessScanTaskTests : IDisposable
{
    private readonly string _tempDir;

    private static readonly DateTime FinishedUtc = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    public LoudnessScanTaskTests()
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

    // BaseItem is abstract; the seams only touch Id/Name/Path/NormalizationGain,
    // so a minimal concrete subclass is enough.
    private sealed class TestVideo : BaseItem;

    private static BaseItem Video(Guid id, float? gain = null) =>
        new TestVideo
        {
            Id = id,
            Name = "Video " + id,
            Path = "/video/" + id + ".mkv",
            NormalizationGain = gain,
        };

    // --- Candidate selection ---

    [Fact]
    public void SelectLoudnessCandidates_skipsItemsThatAlreadyHaveGain()
    {
        var scanned = Video(Guid.NewGuid(), gain: 2.5f);
        var fresh = Video(Guid.NewGuid(), gain: null);

        var candidates = LoudnessScanTask.SelectLoudnessCandidates([scanned, fresh], maxItems: 40);

        var only = Assert.Single(candidates);
        Assert.Equal(fresh.Id, only.Id);
    }

    [Fact]
    public void SelectLoudnessCandidates_capsAtMaxItems_keepingOrder()
    {
        var videos = Enumerable.Range(0, 5).Select(_ => Video(Guid.NewGuid())).ToList();

        var candidates = LoudnessScanTask.SelectLoudnessCandidates(videos, maxItems: 3);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(videos.Take(3).Select(v => v.Id), candidates.Select(c => c.Id));
    }

    [Fact]
    public void SelectLoudnessCandidates_exactlyAtCap_keepsAll()
    {
        var videos = Enumerable.Range(0, 3).Select(_ => Video(Guid.NewGuid())).ToList();

        var candidates = LoudnessScanTask.SelectLoudnessCandidates(videos, maxItems: 3);

        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void SelectLoudnessCandidates_zeroCap_returnsEmpty()
    {
        Assert.Empty(LoudnessScanTask.SelectLoudnessCandidates([Video(Guid.NewGuid())], maxItems: 0));
    }

    [Fact]
    public void SelectLoudnessCandidates_allAlreadyScanned_returnsEmpty()
    {
        // 0f is a real measured gain, not "unset" — it must still exclude the item.
        var videos = new[] { Video(Guid.NewGuid(), gain: -8.25f), Video(Guid.NewGuid(), gain: 0f) };

        Assert.Empty(LoudnessScanTask.SelectLoudnessCandidates(videos, maxItems: 40));
    }

    [Fact]
    public void SelectLoudnessCandidates_emptyInput_returnsEmpty()
    {
        Assert.Empty(LoudnessScanTask.SelectLoudnessCandidates([], maxItems: 40));
    }

    [Fact]
    public void SelectLoudnessCandidates_alreadyScannedItemsDoNotConsumeCap()
    {
        var scanned = Video(Guid.NewGuid(), gain: 2.5f);
        var fresh1 = Video(Guid.NewGuid());
        var fresh2 = Video(Guid.NewGuid());

        var candidates = LoudnessScanTask.SelectLoudnessCandidates([scanned, fresh1, fresh2], maxItems: 1);

        var only = Assert.Single(candidates);
        Assert.Equal(fresh1.Id, only.Id);
    }

    // --- Failure suppression ---

    [Fact]
    public void HasLoudnessFailure_failedItem_returnsTrue()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();
        store.RecordFailure(itemId, "loudness", "loudnorm returned no result");

        Assert.True(LoudnessScanTask.HasLoudnessFailure(store, itemId));
    }

    [Fact]
    public void HasLoudnessFailure_cleanItem_returnsFalse()
    {
        using var store = CreateStore();

        Assert.False(LoudnessScanTask.HasLoudnessFailure(store, Guid.NewGuid()));
    }

    [Fact]
    public void HasLoudnessFailure_otherAnalyserFailure_returnsFalse()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();
        store.RecordFailure(itemId, "segments", "no confident intro/outro correlation");

        Assert.False(LoudnessScanTask.HasLoudnessFailure(store, itemId));
    }

    // --- Gain apply / update / failure decision ---

    [Fact]
    public void ApplyMeasurementOutcome_newGain_recordsSuccessAndClearsPriorFailure()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();
        store.RecordFailure(itemId, "loudness", "old failure");

        var record = LoudnessScanTask.ApplyMeasurementOutcome(store, itemId, -8.25f, null, FinishedUtc);

        Assert.Equal(itemId, record.ItemId);
        Assert.Equal(-8.25f, record.Gain);
        Assert.Null(record.FailureReason);
        Assert.Equal(FinishedUtc, record.FinishedUtc);
        Assert.False(store.HasFailed(itemId, "loudness"));
    }

    [Fact]
    public void ApplyMeasurementOutcome_newGain_withoutPriorFailure_stillSucceeds()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();

        var record = LoudnessScanTask.ApplyMeasurementOutcome(store, itemId, 2.5f, null, FinishedUtc);

        Assert.Equal(2.5f, record.Gain);
        Assert.Null(record.FailureReason);
        Assert.False(store.HasFailed(itemId, "loudness"));
    }

    [Fact]
    public void ApplyMeasurementOutcome_nullGain_recordsFailure()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();

        var record = LoudnessScanTask.ApplyMeasurementOutcome(store, itemId, null, "loudnorm returned no result", FinishedUtc);

        Assert.Null(record.Gain);
        Assert.Equal("loudnorm returned no result", record.FailureReason);
        Assert.Equal(FinishedUtc, record.FinishedUtc);
        Assert.True(store.HasFailed(itemId, "loudness"));
    }

    [Fact]
    public void ApplyMeasurementOutcome_nullGain_withPriorSuccess_switchesToFailure()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();
        LoudnessScanTask.ApplyMeasurementOutcome(store, itemId, 2.5f, null, FinishedUtc);

        var record = LoudnessScanTask.ApplyMeasurementOutcome(store, itemId, null, "loudnorm returned no result", FinishedUtc.AddMinutes(1));

        Assert.Null(record.Gain);
        Assert.Equal("loudnorm returned no result", record.FailureReason);
        Assert.True(store.HasFailed(itemId, "loudness"));
    }

    [Fact]
    public void ApplyMeasurementOutcome_failure_setsSuppressionFlagForScanLoop()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();
        LoudnessScanTask.ApplyMeasurementOutcome(store, itemId, null, "loudnorm returned no result", FinishedUtc);

        // The failure suppresses the item at scan time, not selection time: ExecuteAsync picks
        // candidates on NormalizationGain is null and consults HasLoudnessFailure in the per-item
        // loop, so the item stays a candidate but is skipped rather than re-measured.
        Assert.True(LoudnessScanTask.HasLoudnessFailure(store, itemId));
        Assert.Single(LoudnessScanTask.SelectLoudnessCandidates([Video(itemId)], maxItems: 40));
    }

    [Fact]
    public void ApplyMeasurementOutcome_success_requalifiesPreviouslyFailedItem()
    {
        using var store = CreateStore();
        var itemId = Guid.NewGuid();
        store.RecordFailure(itemId, "loudness", "old failure");
        LoudnessScanTask.ApplyMeasurementOutcome(store, itemId, 2.5f, null, FinishedUtc);

        // Success cleared the failure — but the item now has a gain, so it is "scanned" (skipped
        // at candidate selection), not "failed" (suppressed at scan time).
        Assert.False(LoudnessScanTask.HasLoudnessFailure(store, itemId));
        Assert.Empty(LoudnessScanTask.SelectLoudnessCandidates([Video(itemId, gain: 2.5f)], maxItems: 40));
    }
}
