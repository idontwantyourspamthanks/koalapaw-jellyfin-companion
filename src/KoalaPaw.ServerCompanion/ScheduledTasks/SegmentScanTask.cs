using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Segments;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Season-grouped intro + credits scan: chromaprint head/tail → correlate → MediaSegments.
/// Incremental: skips seasons where every episode already has both Intro and Outro, or where
/// all episodes previously failed analysis.
/// </summary>
public class SegmentScanTask : IScheduledTask, ILibraryPostScanTask
{
    private readonly ILogger<SegmentScanTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _segments;
    private readonly IntroSeasonAnalyser _analyser;
    private readonly AnalysisStore _analysisStore;

    public SegmentScanTask(
        ILogger<SegmentScanTask> logger,
        ILibraryManager libraryManager,
        IMediaSegmentManager segments,
        IntroSeasonAnalyser analyser,
        AnalysisStore analysisStore)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _segments = segments;
        _analyser = analyser;
        _analysisStore = analysisStore;
    }

    public string Name => "KoalaPaw intro & credits scan";

    public string Key => "KoalaPawIntroSegmentScan";

    public string Description =>
        "Fingerprints episode openings and endings; writes Jellyfin Intro/Outro MediaSegments when confident.";

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

        var maxSeasons = config?.MaxSeasonsPerRun ?? 8;

        // IncludeItemTypes in the query — bare Recursive GetItemList deserializes every row and
        // unknown/orphaned types throw before we can OfType<Episode>().
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Episode>()
            .Where(e => !string.IsNullOrWhiteSpace(e.Path) && e.ParentId != Guid.Empty)
            .ToList();

        var bySeason = episodes
            .GroupBy(e => e.ParentId)
            .Where(g => g.Count() >= 2)
            .OrderBy(g => g.Key)
            .ToList();

        var todo = new List<IGrouping<Guid, Episode>>();
        var skippedFailed = 0;
        foreach (var season in bySeason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (todo.Count >= maxSeasons)
            {
                break;
            }

            // Skip seasons where every episode previously failed segment analysis.
            if (season.All(ep => _analysisStore.HasFailed(ep.Id, "segments")))
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
                var hasIntro = existing.Any(s => s.Type == MediaSegmentType.Intro);
                var hasOutro = existing.Any(s => s.Type == MediaSegmentType.Outro);
                if (!hasIntro || !hasOutro)
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
            _logger.LogInformation("SegmentScan: nothing to do (no seasons missing Intro/Outro)");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("SegmentScan: analysing {Count} season(s) (cap {Cap})", todo.Count, maxSeasons);

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
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SegmentScan: season {SeasonId} failed", season.Key);
            }

            done++;
            progress.Report(100.0 * done / todo.Count);
        }

        // The TV's scan page reads this until the next real run — a "nothing to do" pass leaves the
        // previous report standing rather than overwriting it with zeros.
        _analysisStore.SetSegmentScanReport(new AnalysisStore.SegmentScanReport
        {
            EpisodesScanned = scanned,
            IntrosFound = introsFound,
            OutrosFound = outrosFound,
            IntroConfidence = introConfidences.Count > 0 ? introConfidences.Average() : null,
            OutroConfidence = outroConfidences.Count > 0 ? outroConfidences.Average() : null,
            FinishedUtc = DateTime.UtcNow,
        });

        _logger.LogInformation(
            "SegmentScan: finished — scanned {Scanned} episodes, found {Intros} intros and {Outros} outros, staged on {Staged} episode(s)",
            scanned,
            introsFound,
            outrosFound,
            staged);
        progress.Report(100);
    }

    /// <summary>Runs automatically after every library scan.</summary>
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken) =>
        ExecuteAsync(progress, cancellationToken);
}