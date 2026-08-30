using System;
using System.IO;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.ScheduledTasks;
using KoalaPaw.ServerCompanion.ThemeSongs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class ThemeExtractOutcomeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "koalapaw-theme-" + Guid.NewGuid().ToString("N"));

    public ThemeExtractOutcomeTests() => Directory.CreateDirectory(_tempDir);

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

    [Fact]
    public void RememberThemeOutcome_written_clearsFailure()
    {
        using var store = new AnalysisStore(_tempDir, NullLogger<AnalysisStore>.Instance);
        var id = Guid.NewGuid();
        store.RecordFailure(id, ThemeFromIntroExtractor.AnalyserName, "ffmpeg did not produce a theme MP3");

        SegmentScanTask.RememberThemeOutcome(store, id, new ThemeExtractResult(ThemeExtractStatus.Written, null));

        Assert.False(store.HasFailed(id, ThemeFromIntroExtractor.AnalyserName));
    }

    [Fact]
    public void RememberThemeOutcome_failed_recordsReason()
    {
        using var store = new AnalysisStore(_tempDir, NullLogger<AnalysisStore>.Instance);
        var id = Guid.NewGuid();

        SegmentScanTask.RememberThemeOutcome(
            store,
            id,
            new ThemeExtractResult(ThemeExtractStatus.Failed, "permission denied"));

        Assert.True(store.HasFailed(id, ThemeFromIntroExtractor.AnalyserName));
    }

    [Fact]
    public void RememberThemeOutcome_skipKinds_doNotRecordFailure()
    {
        using var store = new AnalysisStore(_tempDir, NullLogger<AnalysisStore>.Instance);
        var id = Guid.NewGuid();

        SegmentScanTask.RememberThemeOutcome(store, id, new ThemeExtractResult(ThemeExtractStatus.AlreadyPresent, null));
        SegmentScanTask.RememberThemeOutcome(store, id, new ThemeExtractResult(ThemeExtractStatus.NoClip, "too short"));

        Assert.False(store.HasFailed(id, ThemeFromIntroExtractor.AnalyserName));
    }
}
