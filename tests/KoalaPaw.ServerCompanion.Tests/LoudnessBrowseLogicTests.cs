using System;
using System.Collections.Generic;
using KoalaPaw.ServerCompanion.Api;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Loudness;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The loudness browse tiers' pure logic: the per-item gain/failure precedence merge behind
/// GetStatuses (NormalizationGain over the record's measured gain, live failure over the
/// record's historical one) and the newest-finish-time rollup.
/// </summary>
public class LoudnessBrowseLogicTests
{
    private static readonly Guid ItemId = new("00000000-0000-0000-0000-0000000000d1");
    private static readonly DateTime Day1 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day3 = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

    // --- Gain / failure precedence (GetStatuses ground truth) ---

    [Fact]
    public void ResolveItemStatus_normalizationGainWinsOverRecordGain()
    {
        // The gain already applied on the item is what plays; the record's older measurement must not shadow it.
        var status = LoudnessBrowseLogic.ResolveItemStatus(
            ItemId,
            new Dictionary<Guid, string>(),
            normalizationGain: 1.5,
            record: new AnalysisStore.LoudnessScanRecord { ItemId = ItemId, Gain = -2.25f });

        Assert.Equal(LoudnessItemState.Scanned, status.State);
        Assert.Equal(1.5, status.Gain);
    }

    [Fact]
    public void ResolveItemStatus_fallsBackToRecordGain()
    {
        var status = LoudnessBrowseLogic.ResolveItemStatus(
            ItemId,
            new Dictionary<Guid, string>(),
            normalizationGain: null,
            record: new AnalysisStore.LoudnessScanRecord { ItemId = ItemId, Gain = -3.25f });

        Assert.Equal(LoudnessItemState.Scanned, status.State);
        Assert.Equal(-3.25, status.Gain);
    }

    [Fact]
    public void ResolveItemStatus_liveFailureBeatsRecordFailure()
    {
        var failures = new Dictionary<Guid, string> { [ItemId] = "ffmpeg crashed" };

        var status = LoudnessBrowseLogic.ResolveItemStatus(
            ItemId,
            failures,
            normalizationGain: null,
            record: new AnalysisStore.LoudnessScanRecord { ItemId = ItemId, FailureReason = "stale failure" });

        Assert.Equal(LoudnessItemState.Failed, status.State);
        Assert.Equal("ffmpeg crashed", status.FailureReason);
    }

    [Fact]
    public void ResolveItemStatus_recordFailureUsedWhenNoLiveFailure()
    {
        var status = LoudnessBrowseLogic.ResolveItemStatus(
            ItemId,
            new Dictionary<Guid, string>(),
            normalizationGain: null,
            record: new AnalysisStore.LoudnessScanRecord { ItemId = ItemId, FailureReason = "no audio stream" });

        Assert.Equal(LoudnessItemState.Failed, status.State);
        Assert.Equal("no audio stream", status.FailureReason);
    }

    [Fact]
    public void ResolveItemStatus_neverScanned_returnsNotScanned()
    {
        var status = LoudnessBrowseLogic.ResolveItemStatus(ItemId, new Dictionary<Guid, string>(), null, null);

        Assert.Equal(LoudnessItemState.NotScanned, status.State);
        Assert.Null(status.Gain);
        Assert.Null(status.FailureReason);
    }

    [Theory]
    // (hasGain, hasFailure) → dashboard state; a gain in place always wins over any failure.
    [InlineData(true, false, LoudnessItemState.Scanned)]
    [InlineData(true, true, LoudnessItemState.Scanned)]
    [InlineData(false, true, LoudnessItemState.Failed)]
    [InlineData(false, false, LoudnessItemState.NotScanned)]
    public void ResolveItemStatus_coversStateTruthTable(bool hasGain, bool hasFailure, string expected)
    {
        var failures = hasFailure
            ? new Dictionary<Guid, string> { [ItemId] = "boom" }
            : new Dictionary<Guid, string>();
        var record = hasFailure ? new AnalysisStore.LoudnessScanRecord { ItemId = ItemId } : null;

        var status = LoudnessBrowseLogic.ResolveItemStatus(ItemId, failures, hasGain ? 1.0 : null, record);

        Assert.Equal(expected, status.State);
    }

    [Fact]
    public void ResolveItemStatus_gainWinsEvenWithLiveFailure()
    {
        var failures = new Dictionary<Guid, string> { [ItemId] = "stale" };

        var status = LoudnessBrowseLogic.ResolveItemStatus(ItemId, failures, 0.5, null);

        Assert.Equal(LoudnessItemState.Scanned, status.State);
        Assert.Equal(0.5, status.Gain);
    }

    [Fact]
    public void ResolveItemStatus_presentButEmptyLiveFailure_stillFails()
    {
        // Precedence is decided by the entry's presence, not its truthiness — a rescan clears the
        // entry outright, so an empty reason still speaks for the item.
        var failures = new Dictionary<Guid, string> { [ItemId] = string.Empty };

        var status = LoudnessBrowseLogic.ResolveItemStatus(ItemId, failures, null, null);

        Assert.Equal(LoudnessItemState.Failed, status.State);
        Assert.Equal(string.Empty, status.FailureReason);
    }

    // --- Newest finish time ---

    [Fact]
    public void MaxFinishedUtc_mixedTimestamps_returnsNewest()
    {
        Assert.Equal(Day3, LoudnessBrowseLogic.MaxFinishedUtc([Day2, Day3, Day1, null]));
    }

    [Fact]
    public void MaxFinishedUtc_allNulls_returnsNull()
    {
        Assert.Null(LoudnessBrowseLogic.MaxFinishedUtc([null, null]));
    }

    [Fact]
    public void MaxFinishedUtc_empty_returnsNull()
    {
        Assert.Null(LoudnessBrowseLogic.MaxFinishedUtc([]));
    }

    [Fact]
    public void MaxFinishedUtc_singleValueAmongNulls_returnsIt()
    {
        Assert.Equal(Day1, LoudnessBrowseLogic.MaxFinishedUtc([null, Day1, null]));
    }
}
