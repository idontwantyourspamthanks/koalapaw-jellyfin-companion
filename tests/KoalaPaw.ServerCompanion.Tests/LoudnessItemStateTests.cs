using KoalaPaw.ServerCompanion.Loudness;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class LoudnessItemStateTests
{
    [Theory]
    // A gain in place wins over any failure record — a later successful measurement
    // invalidates the failure, so stale records never shadow a real gain.
    [InlineData(true, true, LoudnessItemState.Scanned)]
    [InlineData(true, false, LoudnessItemState.Scanned)]
    [InlineData(false, true, LoudnessItemState.Failed)]
    [InlineData(false, false, LoudnessItemState.NotScanned)]
    public void Derive_coversFullTruthTable(bool hasGain, bool hasFailure, string expected)
    {
        Assert.Equal(expected, LoudnessItemState.Derive(hasGain, hasFailure));
    }

    [Fact]
    public void StateNames_matchApiContract()
    {
        Assert.Equal("scanned", LoudnessItemState.Scanned);
        Assert.Equal("failed", LoudnessItemState.Failed);
        Assert.Equal("notScanned", LoudnessItemState.NotScanned);
    }

    [Fact]
    public void Count_rollsUpMixedStates()
    {
        var counts = LoudnessItemState.Count(
        [
            LoudnessItemState.Scanned,
            LoudnessItemState.Failed,
            LoudnessItemState.Scanned,
            LoudnessItemState.NotScanned,
            LoudnessItemState.Failed,
            LoudnessItemState.Scanned,
        ]);

        Assert.Equal(6, counts.Total);
        Assert.Equal(3, counts.Scanned);
        Assert.Equal(2, counts.Failed);
        Assert.Equal(1, counts.NotScanned);
    }

    [Fact]
    public void Count_empty_returnsZeros()
    {
        var counts = LoudnessItemState.Count([]);

        Assert.Equal(0, counts.Total);
        Assert.Equal(0, counts.Scanned);
        Assert.Equal(0, counts.Failed);
        Assert.Equal(0, counts.NotScanned);
    }

    [Fact]
    public void Count_ignoresUnknownStates()
    {
        var counts = LoudnessItemState.Count([LoudnessItemState.Scanned, "weird", LoudnessItemState.Failed]);

        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Scanned);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(0, counts.NotScanned);
    }
}
