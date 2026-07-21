using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using KoalaPaw.ServerCompanion.Loudness;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Background loudness scan for video items: ffmpeg audio-only → <see cref="BaseItem.NormalizationGain"/>.
/// Incremental (skips items that already have a gain), one ffmpeg at a time, BelowNormal priority.
/// </summary>
public class LoudnessScanTask : IScheduledTask
{
    /// <summary>Cap per run so a first install on a huge library doesn't hog the Pi overnight.</summary>
    public const int MaxItemsPerRun = 40;

    private readonly ILogger<LoudnessScanTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly LoudnessProbe _probe;

    public LoudnessScanTask(
        ILogger<LoudnessScanTask> logger,
        ILibraryManager libraryManager,
        LoudnessProbe probe)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _probe = probe;
    }

    public string Name => "KoalaPaw video loudness scan";

    public string Key => "KoalaPawVideoLoudnessScan";

    public string Description =>
        "Measures loudness on movies/episodes missing NormalizationGain and writes Jellyfin's standard field.";

    public string Category => "KoalaPaw";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
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
        // IncludeItemTypes must be set in the query — a bare Recursive scan asks the repo to
        // deserialize every BaseItems row, and unknown/orphaned types throw
        // "Cannot deserialize unknown type" before we can filter in-process.
        var candidates = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                Recursive = true,
                IsVirtualItem = false,
            })
            .Where(i => i.NormalizationGain is null && !string.IsNullOrWhiteSpace(i.Path))
            .Take(MaxItemsPerRun)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("LoudnessScan: nothing to do (no videos missing NormalizationGain in this batch window)");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("LoudnessScan: analysing {Count} video(s) (cap {Cap})", candidates.Count, MaxItemsPerRun);

        var done = 0;
        var ok = 0;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var gain = await _probe.MeasureNormalizationGainAsync(item.Path, cancellationToken)
                    .ConfigureAwait(false);
                if (gain is null)
                {
                    _logger.LogWarning("LoudnessScan: no measurement for {Name} ({Id})", item.Name, item.Id);
                }
                else
                {
                    item.NormalizationGain = gain;
                    var parent = _libraryManager.GetItemById(item.ParentId) ?? item;
                    await _libraryManager.UpdateItemAsync(
                        item,
                        parent,
                        ItemUpdateType.MetadataEdit,
                        cancellationToken).ConfigureAwait(false);
                    ok++;
                    _logger.LogInformation(
                        "LoudnessScan: {Name} ({Id}) NormalizationGain={Gain}",
                        item.Name,
                        item.Id,
                        gain.Value.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoudnessScan: failed for {Name} ({Id})", item.Name, item.Id);
            }

            done++;
            progress.Report(100.0 * done / candidates.Count);
        }

        _logger.LogInformation("LoudnessScan: finished — {Ok}/{Total} written", ok, candidates.Count);
        progress.Report(100);
    }
}
