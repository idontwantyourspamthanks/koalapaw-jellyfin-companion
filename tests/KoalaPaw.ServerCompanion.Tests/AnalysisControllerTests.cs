using System;
using System.Collections.Generic;
using KoalaPaw.ServerCompanion.Api;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

/// <summary>
/// The pure decisions behind the dashboard failure API: which scan a retry queues for each
/// analyser, how recorded failures map to their read-time DTOs, and the no-scan-yet report shape.
/// </summary>
public class AnalysisControllerTests
{
    // --- Retry: analyser → scan task ---

    [Theory]
    [InlineData("loudness", typeof(LoudnessScanTask))]
    [InlineData("segments", typeof(SegmentScanTask))]
    [InlineData("themes", typeof(SegmentScanTask))] // theme failures ride the segment scan
    public void RetryScanTaskFor_mapsEachAnalyserToItsScanTask(string analyser, Type expectedTask)
    {
        Assert.Equal(expectedTask, AnalysisController.RetryScanTaskFor(analyser));
    }

    [Theory]
    [InlineData("Loudness")] // case-sensitive: retry keys must match the stored analyser name
    [InlineData("loudness ")]
    [InlineData("subtitles")]
    [InlineData("")]
    public void RetryScanTaskFor_unknownAnalyser_returnsNull(string analyser)
    {
        Assert.Null(AnalysisController.RetryScanTaskFor(analyser));
    }

    // --- Failure list mapping ---

    [Fact]
    public void ToFailureDtos_carriesEveryRecordFieldThrough()
    {
        var itemId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        var dtos = AnalysisController.ToFailureDtos(
            [new AnalysisStore.FailureRecord { ItemId = itemId, Analyser = "loudness", Reason = "loudnorm returned no result", Timestamp = timestamp }],
            _ => "Episode Name");

        var dto = Assert.Single(dtos);
        Assert.Equal(itemId, dto.ItemId);
        Assert.Equal("Episode Name", dto.Name);
        Assert.Equal("loudness", dto.Analyser);
        Assert.Equal("loudnorm returned no result", dto.Reason);
        Assert.Equal(timestamp, dto.Timestamp);
    }

    [Fact]
    public void ToFailureDtos_deletedItem_resolvesNameToNull()
    {
        var dtos = AnalysisController.ToFailureDtos(
            [new AnalysisStore.FailureRecord { ItemId = Guid.NewGuid(), Analyser = "segments" }],
            _ => null);

        Assert.Null(Assert.Single(dtos).Name);
    }

    [Fact]
    public void ToFailureDtos_preservesStoreOrder()
    {
        var first = new AnalysisStore.FailureRecord { ItemId = Guid.NewGuid(), Analyser = "loudness" };
        var second = new AnalysisStore.FailureRecord { ItemId = Guid.NewGuid(), Analyser = "segments" };

        var dtos = AnalysisController.ToFailureDtos([first, second], _ => "any");

        Assert.Equal(2, dtos.Count);
        Assert.Equal(first.ItemId, dtos[0].ItemId);
        Assert.Equal(second.ItemId, dtos[1].ItemId);
    }

    [Fact]
    public void ToFailureDtos_noFailures_returnsEmptyList()
    {
        Assert.Empty(AnalysisController.ToFailureDtos([], _ => "any"));
    }

    // --- Segment scan report mapping ---

    [Fact]
    public void ToReportDto_noScanYet_mapsToAllZeros()
    {
        var dto = AnalysisController.ToReportDto(null);

        Assert.Equal(0, dto.EpisodesScanned);
        Assert.Equal(0, dto.IntrosFound);
        Assert.Equal(0, dto.OutrosFound);
        Assert.Null(dto.IntroConfidence);
        Assert.Null(dto.OutroConfidence);
    }

    [Fact]
    public void ToReportDto_completedScan_carriesAllFieldsThrough()
    {
        var dto = AnalysisController.ToReportDto(new AnalysisStore.SegmentScanReport
        {
            EpisodesScanned = 12,
            IntrosFound = 7,
            OutrosFound = 5,
            IntroConfidence = 0.91,
            OutroConfidence = 0.84,
        });

        Assert.Equal(12, dto.EpisodesScanned);
        Assert.Equal(7, dto.IntrosFound);
        Assert.Equal(5, dto.OutrosFound);
        Assert.Equal(0.91, dto.IntroConfidence);
        Assert.Equal(0.84, dto.OutroConfidence);
    }
}
