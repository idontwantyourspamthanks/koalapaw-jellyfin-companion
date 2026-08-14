using KoalaPaw.ServerCompanion.Segments;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class EpisodeSegmentStateTests
{
    [Theory]
    // Stale failure + segments in place → detected (success invalidates failure).
    [InlineData(true, true, true, EpisodeSegmentState.Detected)]
    [InlineData(true, true, false, EpisodeSegmentState.Detected)]
    [InlineData(true, false, true, EpisodeSegmentState.Partial)]
    [InlineData(true, false, false, EpisodeSegmentState.Partial)]
    [InlineData(false, true, true, EpisodeSegmentState.Partial)]
    [InlineData(false, true, false, EpisodeSegmentState.Partial)]
    [InlineData(false, false, true, EpisodeSegmentState.Failed)]
    [InlineData(false, false, false, EpisodeSegmentState.NotScanned)]
    public void Derive_coversFullTruthTable(bool hasIntro, bool hasOutro, bool hasFailure, string expected)
    {
        Assert.Equal(expected, EpisodeSegmentState.Derive(hasIntro, hasOutro, hasFailure));
    }

    [Fact]
    public void StateNames_matchApiContract()
    {
        Assert.Equal("detected", EpisodeSegmentState.Detected);
        Assert.Equal("partial", EpisodeSegmentState.Partial);
        Assert.Equal("failed", EpisodeSegmentState.Failed);
        Assert.Equal("notScanned", EpisodeSegmentState.NotScanned);
    }

    [Fact]
    public void Count_rollsUpMixedStates()
    {
        var counts = EpisodeSegmentState.Count(
        [
            EpisodeSegmentState.Detected,
            EpisodeSegmentState.Partial,
            EpisodeSegmentState.Detected,
            EpisodeSegmentState.Failed,
            EpisodeSegmentState.NotScanned,
            EpisodeSegmentState.Detected,
        ]);

        Assert.Equal(6, counts.Total);
        Assert.Equal(3, counts.Detected);
        Assert.Equal(1, counts.Partial);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.NotScanned);
    }

    [Fact]
    public void Count_empty_returnsZeros()
    {
        var counts = EpisodeSegmentState.Count([]);

        Assert.Equal(0, counts.Total);
        Assert.Equal(0, counts.Detected);
        Assert.Equal(0, counts.Partial);
        Assert.Equal(0, counts.Failed);
        Assert.Equal(0, counts.NotScanned);
    }

    [Fact]
    public void Count_ignoresUnknownStates()
    {
        var counts = EpisodeSegmentState.Count([EpisodeSegmentState.Detected, "weird", EpisodeSegmentState.NotScanned]);

        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Detected);
        Assert.Equal(1, counts.NotScanned);
    }
}
