using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Segments;
using KoalaPaw.ServerCompanion.ThemeSongs;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Season-grouped intro + credits scan: chromaprint head/tail → correlate → MediaSegments.
/// Incremental: skips seasons where every episode already has both Intro and Outro, or where
/// all episodes previously failed analysis. After a confident intro, also cuts a short
/// <c>theme.mp3</c> from that opening into the series folder when one is not already there.
/// </summary>
public class SegmentScanTask : IScheduledTask, ILibraryPostScanTask
{
    private readonly ILogger<SegmentScanTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _segments;
    private readonly IntroSeasonAnalyser _analyser;
    private readonly AnalysisStore _analysisStore;
    private readonly ThemeFromIntroExtractor _themeExtractor;

    public SegmentScanTask(
        ILogger<SegmentScanTask> logger,
        ILibraryManager libraryManager,
        IMediaSegmentManager segments,
        IntroSeasonAnalyser analyser,
        AnalysisStore analysisStore,
        ThemeFromIntroExtractor themeExtractor)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _segments = segments;
        _analyser = analyser;
        _analysisStore = analysisStore;
        _themeExtractor = themeExtractor;
    }

    public string Name => "KoalaPaw intro & credits scan";

    public string Key => "KoalaPawIntroSegmentScan";

    public string Description =>
        "Fingerprints episode openings and endings; writes Jellyfin Intro/Outro MediaSegments when confident, and a theme.mp3 from the intro when the series has none.";

    public string Category => "KoalaPaw";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger,
            },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(12).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not null && !config.SegmentDetectionEnabled)
        {
            _logger.LogInformation("SegmentScan: disabled in plugin configuration — skipping");
            progress.Report(100);
            return;
        }

        // IncludeItemTypes in the query — bare Recursive GetItemList deserializes every row and
        // unknown/orphaned types throw before we can OfType<Episode>().
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Episode>();

        var bySeason = SelectSeasonsForScan(episodes);

        // Forget scan records for seasons that no longer exist in the library.
        _analysisStore.PruneSeasonRecordsExcept(bySeason.Select(g => g.Key).ToList());

        var todo = new List<IGrouping<Guid, Episode>>();
        var skippedFailed = 0;
        foreach (var season in bySeason)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip seasons where every episode previously failed segment analysis.
            if (SeasonFullyFailed(_analysisStore, season))
            {
                skippedFailed++;
                continue;
            }

            var needs = false;
            foreach (var ep in season)
            {
                var opts = _libraryManager.GetLibraryOptions(ep);
                var existing = (await _segments.GetSegmentsAsync(
                        ep,
                        [MediaSegmentType.Intro, MediaSegmentType.Outro],
                        opts,
                        filterByProvider: true)
                    .ConfigureAwait(false)).ToList();
                if (MissingIntroOrOutro(existing))
                {
                    needs = true;
                    break;
                }
            }

            if (needs)
            {
                todo.Add(season);
            }
        }

        if (skippedFailed > 0)
        {
            _logger.LogInformation("SegmentScan: skipped {Count} previously-failed season(s)", skippedFailed);
        }

        if (todo.Count == 0)
        {
            _logger.LogInformation("SegmentScan: nothing to fingerprint (no seasons missing Intro/Outro)");
        }
        else
        {
            _logger.LogInformation("SegmentScan: analysing {Count} season(s)", todo.Count);
        }

        var done = 0;
        var staged = 0;
        var scanned = 0;
        var introsFound = 0;
        var outrosFound = 0;
        var introConfidences = new List<double>();
        var outroConfidences = new List<double>();
        foreach (var season in todo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seriesId = season.First().SeriesId;
            try
            {
                var result = await _analyser.AnalyseAndStageAsync(season.Cast<BaseItem>().ToList(), cancellationToken)
                    .ConfigureAwait(false);
                scanned += result.EpisodesScanned;
                staged += result.EpisodesStaged;
                introsFound += result.IntrosFound;
                outrosFound += result.OutrosFound;
                if (result.IntroConfidence is { } introConf)
                {
                    introConfidences.Add(introConf);
                }

                if (result.OutroConfidence is { } outroConf)
                {
                    outroConfidences.Add(outroConf);
                }

                _analysisStore.SetSeasonScanRecord(ToSeasonScanRecord(season.Key, seriesId, DateTime.UtcNow, result));

                if (result.Intro is { } intro)
                {
                    var episode = season.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Path));
                    var series = _libraryManager.GetItemById(seriesId) as Series;
                    if (episode is not null && series is not null)
                    {
                        var endTicks = (long)(intro.EndSeconds * TimeSpan.TicksPerSecond);
                        await ApplyThemeExtractAsync(series, episode.Path, 0, endTicks, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SegmentScan: season {SeasonId} failed", season.Key);
                _analysisStore.SetSeasonScanRecord(new AnalysisStore.SeasonScanRecord
                {
                    SeasonId = season.Key,
                    SeriesId = seriesId,
                    FinishedUtc = DateTime.UtcNow,
                    Outcome = AnalysisStore.SeasonOutcome.Error,
                });
            }

            done++;
            if (todo.Count > 0)
            {
                progress.Report(90.0 * done / todo.Count);
            }
        }

        if (todo.Count > 0)
        {
            // The TV's scan page reads this until the next real run — a "nothing to fingerprint"
            // pass leaves the previous report standing rather than overwriting it with zeros.
            _analysisStore.SetSegmentScanReport(BuildSegmentScanReport(
                scanned,
                introsFound,
                outrosFound,
                introConfidences,
                outroConfidences,
                DateTime.UtcNow));

            _logger.LogInformation(
                "SegmentScan: finished — scanned {Scanned} episodes, found {Intros} intros and {Outros} outros, staged on {Staged} episode(s)",
                scanned,
                introsFound,
                outrosFound,
                staged);
        }

        await BackfillThemesFromExistingIntrosAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        return;
    }

    internal const int MaxThemeBackfillPerRun = 15;
    /// <summary>
    /// Pick which seasons this run scans: episodes grouped by season (ParentId), only seasons
    /// with at least two episodes, ordered deterministically by season id. Extracted as a
    /// static so the season-selection rule is unit-testable without an ILibraryManager.
    /// </summary>
    internal static IReadOnlyList<IGrouping<Guid, Episode>> SelectSeasonsForScan(IEnumerable<Episode> episodes) =>
        episodes
            .Where(e => !string.IsNullOrWhiteSpace(e.Path) && e.ParentId != Guid.Empty)
            .GroupBy(e => e.ParentId)
            .Where(g => g.Count() >= 2)
            .OrderBy(g => g.Key)
            .ToList();

    /// <summary>
    /// True when every episode in the season has a recorded "segments" failure — such seasons
    /// are skipped so an uncorrelatable season isn't retried every 12 h run forever. Extracted
    /// as a static so the failure-suppression rule is unit-testable without an
    /// AnalysisStore-backed library.
    /// </summary>
    internal static bool SeasonFullyFailed(AnalysisStore store, IEnumerable<Episode> episodes) =>
        episodes.All(ep => store.HasFailed(ep.Id, "segments"));

    /// <summary>
    /// True when the episode's existing segments miss an Intro or an Outro — the incremental
    /// rule that only re-scans seasons with at least one incomplete episode. Extracted as a
    /// static so the skip rule is unit-testable without IMediaSegmentManager plumbing.
    /// </summary>
    internal static bool MissingIntroOrOutro(IEnumerable<MediaSegmentDto> existingSegments) =>
        !existingSegments.Any(s => s.Type == MediaSegmentType.Intro) ||
        !existingSegments.Any(s => s.Type == MediaSegmentType.Outro);

    /// <summary>
    /// Aggregate one run's per-season totals into the report the TV's scan page reads: plain
    /// sums plus mean confidence per type, null when no season contributed a sample. Extracted
    /// as a static so the aggregation math is unit-testable without an AnalysisStore.
    /// </summary>
    internal static AnalysisStore.SegmentScanReport BuildSegmentScanReport(
        int episodesScanned,
        int introsFound,
        int outrosFound,
        IReadOnlyCollection<double> introConfidences,
        IReadOnlyCollection<double> outroConfidences,
        DateTime finishedUtc) =>
        new()
        {
            EpisodesScanned = episodesScanned,
            IntrosFound = introsFound,
            OutrosFound = outrosFound,
            IntroConfidence = introConfidences.Count > 0 ? introConfidences.Average() : null,
            OutroConfidence = outroConfidences.Count > 0 ? outroConfidences.Average() : null,
            FinishedUtc = finishedUtc,
        };

    /// <summary>
    /// Pick series for the theme backfill: no theme file/folder yet, no recorded theme-extract
    /// failure, capped at <see cref="MaxThemeBackfillPerRun"/> so ffmpeg extracts can't blow a
    /// single run's budget. Extracted as a static so the cap and filter rules are
    /// unit-testable without an ILibraryManager.
    /// </summary>
    internal static IReadOnlyList<Series> SelectThemesToBackfill(IEnumerable<Series> series, AnalysisStore store) =>
        series
            .Where(s => !string.IsNullOrWhiteSpace(s.Path))
            .Where(s => !ThemeFromIntroLogic.FolderAlreadyHasTheme(s.Path))
            .Where(s => !store.HasFailed(s.Id, ThemeFromIntroExtractor.AnalyserName))
            .Take(MaxThemeBackfillPerRun)
            .ToList();

    /// <summary>
    /// Series that already have KoalaPaw intros from an earlier run but never got a theme file
    /// (the old scan skipped them once Intro+Outro existed). Cap ffmpeg extracts per run.
    /// </summary>
    private async Task BackfillThemesFromExistingIntrosAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var seriesList = SelectThemesToBackfill(
            _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Series>(),
            _analysisStore);

        if (seriesList.Count == 0)
        {
            _logger.LogInformation("ThemeFromIntro: no series missing a theme extra");
            return;
        }

        _logger.LogInformation("ThemeFromIntro: backfilling {Count} series from existing intros", seriesList.Count);
        var done = 0;
        var written = 0;
        foreach (var series in seriesList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryExtractThemeFromExistingIntroAsync(series, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }

            done++;
            progress.Report(90.0 + (10.0 * done / seriesList.Count));
        }

        _logger.LogInformation("ThemeFromIntro: backfill wrote {Written}/{Total} theme file(s)", written, seriesList.Count);
    }

    private async Task<bool> TryExtractThemeFromExistingIntroAsync(Series series, CancellationToken cancellationToken)
    {
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                ParentId = series.Id,
            })
            .OfType<Episode>()
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Take(24)
            .ToList();

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opts = _libraryManager.GetLibraryOptions(episode);
            var intro = (await _segments.GetSegmentsAsync(
                    episode,
                    [MediaSegmentType.Intro],
                    opts,
                    filterByProvider: true)
                .ConfigureAwait(false))
                .FirstOrDefault(s => s.Type == MediaSegmentType.Intro);
            if (intro is null)
            {
                continue;
            }

            return await ApplyThemeExtractAsync(
                    series,
                    episode.Path,
                    intro.StartTicks,
                    intro.EndTicks,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    internal static void RememberThemeOutcome(AnalysisStore store, Guid seriesId, ThemeExtractResult result)
    {
        switch (result.Status)
        {
            case ThemeExtractStatus.Written:
                store.RemoveFailure(seriesId, ThemeFromIntroExtractor.AnalyserName);
                break;
            case ThemeExtractStatus.Failed:
                store.RecordFailure(seriesId, ThemeFromIntroExtractor.AnalyserName, result.Detail ?? "theme extract failed");
                break;
        }
    }

    private async Task<bool> ApplyThemeExtractAsync(
        Series series,
        string episodePath,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken)
    {
        if (ThemeFromIntroLogic.FolderAlreadyHasTheme(series.Path) ||
            _analysisStore.HasFailed(series.Id, ThemeFromIntroExtractor.AnalyserName))
        {
            return false;
        }

        var result = await _themeExtractor.ExtractAsync(
                series.Path,
                episodePath,
                startTicks,
                endTicks,
                cancellationToken)
            .ConfigureAwait(false);
        RememberThemeOutcome(_analysisStore, series.Id, result);
        return result.Status == ThemeExtractStatus.Written;
    }

    /// <summary>
    /// Maps one season's analysis result to the persisted scan record. Extracted as a static so
    /// the outcome derivation (Detected / Partial / NoCorrelation) is unit-testable without
    /// Jellyfin plumbing.
    /// </summary>
    internal static AnalysisStore.SeasonScanRecord ToSeasonScanRecord(
        Guid seasonId,
        Guid seriesId,
        DateTime finishedUtc,
        IntroSeasonAnalyser.SeasonAnalysisResult result)
    {
        var outcome = (result.Intro is not null, result.Outro is not null) switch
        {
            (true, true) => AnalysisStore.SeasonOutcome.Detected,
            (true, false) or (false, true) => AnalysisStore.SeasonOutcome.Partial,
            _ => AnalysisStore.SeasonOutcome.NoCorrelation,
        };

        return new AnalysisStore.SeasonScanRecord
        {
            SeasonId = seasonId,
            SeriesId = seriesId,
            FinishedUtc = finishedUtc,
            EpisodesSampled = result.EpisodesScanned,
            Outcome = outcome,
            IntroEndSeconds = result.Intro?.EndSeconds,
            IntroConfidence = result.Intro?.Confidence,
            OutroLengthSeconds = result.Outro?.EndSeconds,
            OutroConfidence = result.Outro?.Confidence,
        };
    }

    /// <summary>Runs automatically after every library scan.</summary>
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken) =>
        ExecuteAsync(progress, cancellationToken);
}