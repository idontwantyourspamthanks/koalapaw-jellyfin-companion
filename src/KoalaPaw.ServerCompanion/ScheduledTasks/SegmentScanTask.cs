using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
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
/// Season-grouped intro scan: chromaprint heads → correlate → write MediaSegments (Intro).
/// Incremental: skips seasons where every episode already has an Intro from any provider.
/// </summary>
public class SegmentScanTask : IScheduledTask
{
    public const int MaxSeasonsPerRun = 8;

    private readonly ILogger<SegmentScanTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _segments;
    private readonly IntroSeasonAnalyser _analyser;

    public SegmentScanTask(
        ILogger<SegmentScanTask> logger,
        ILibraryManager libraryManager,
        IMediaSegmentManager segments,
        IntroSeasonAnalyser analyser)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _segments = segments;
        _analyser = analyser;
    }

    public string Name => "KoalaPaw intro segment scan";

    public string Key => "KoalaPawIntroSegmentScan";

    public string Description =>
        "Fingerprints episode openings and writes Jellyfin Intro MediaSegments when confident.";

    public string Category => "KoalaPaw";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
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
        foreach (var season in bySeason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (todo.Count >= MaxSeasonsPerRun)
            {
                break;
            }

            var needs = false;
            foreach (var ep in season)
            {
                var opts = _libraryManager.GetLibraryOptions(ep);
                var existing = await _segments.GetSegmentsAsync(
                        ep,
                        [Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro],
                        opts,
                        filterByProvider: true)
                    .ConfigureAwait(false);
                if (!existing.Any())
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

        if (todo.Count == 0)
        {
            _logger.LogInformation("SegmentScan: nothing to do (no seasons missing Intro)");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("SegmentScan: analysing {Count} season(s) (cap {Cap})", todo.Count, MaxSeasonsPerRun);

        var done = 0;
        var staged = 0;
        foreach (var season in todo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                staged += await _analyser.AnalyseAndStageAsync(season.Cast<BaseItem>().ToList(), cancellationToken)
                    .ConfigureAwait(false);
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

        _logger.LogInformation("SegmentScan: finished — staged intros on {Staged} episode(s)", staged);
        progress.Report(100);
    }
}
