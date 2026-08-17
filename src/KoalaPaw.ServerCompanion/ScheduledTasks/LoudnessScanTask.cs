using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Loudness;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Background loudness scan for video items: ffmpeg audio-only → <see cref="BaseItem.NormalizationGain"/>.
/// Incremental (skips items that already have a gain or previously failed), one ffmpeg at a time,
/// BelowNormal priority.
/// </summary>
public class LoudnessScanTask : IScheduledTask, ILibraryPostScanTask
{
    private readonly ILogger<LoudnessScanTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly LoudnessProbe _probe;
    private readonly AnalysisStore _analysisStore;

    public LoudnessScanTask(
        ILogger<LoudnessScanTask> logger,
        ILibraryManager libraryManager,
        LoudnessProbe probe,
        AnalysisStore analysisStore)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _probe = probe;
        _analysisStore = analysisStore;
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
                Type = TaskTriggerInfoType.StartupTrigger,
            },
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
        var config = Plugin.Instance?.Configuration;
        if (config is not null && !config.LoudnessEnabled)
        {
            _logger.LogInformation("LoudnessScan: disabled in plugin configuration — skipping");
            progress.Report(100);
            return;
        }

        var maxItems = config?.MaxLoudnessItemsPerRun ?? 40;

        // IncludeItemTypes must be set in the query — a bare Recursive scan asks the repo to
        // deserialize every BaseItems row, and unknown/orphaned types throw
        // "Cannot deserialize unknown type" before we can filter in-process.
        var allVideos = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                Recursive = true,
                IsVirtualItem = false,
            })
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .ToList();

        // Forget outcome records for items that no longer exist in the library.
        _analysisStore.PruneLoudnessRecordsExcept(allVideos.Select(v => v.Id).ToList());

        var candidates = allVideos
            .Where(i => i.NormalizationGain is null)
            .Take(maxItems)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("LoudnessScan: nothing to do (no videos missing NormalizationGain in this batch window)");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("LoudnessScan: analysing {Count} video(s) (cap {Cap})", candidates.Count, maxItems);

        var done = 0;
        var ok = 0;
        var skippedFailed = 0;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_analysisStore.HasFailed(item.Id, "loudness"))
            {
                skippedFailed++;
                done++;
                progress.Report(100.0 * done / candidates.Count);
                continue;
            }

            var record = await MeasureAndApplyAsync(
                    _probe,
                    _libraryManager,
                    _analysisStore,
                    _logger,
                    item,
                    cancellationToken)
                .ConfigureAwait(false);
            if (record.Gain is not null)
            {
                ok++;
            }

            done++;
            progress.Report(100.0 * done / candidates.Count);
        }

        _logger.LogInformation(
            "LoudnessScan: finished — {Ok}/{Total} written, skipped {Skipped} previously-failed items",
            ok,
            candidates.Count,
            skippedFailed);
        progress.Report(100);
    }

    /// <summary>
    /// Measure one video and apply the outcome: writes <see cref="BaseItem.NormalizationGain"/> on
    /// success (forgetting any earlier failure), records a loudness failure otherwise, and always
    /// persists the per-item outcome record the dashboard reads. Shared by the scheduled task and
    /// the on-demand rescan endpoints so both write identical outcomes. A rescan passes
    /// CancellationToken.None deliberately — a disconnecting client must not abort measurement.
    /// </summary>
    internal static async Task<AnalysisStore.LoudnessScanRecord> MeasureAndApplyAsync(
        LoudnessProbe probe,
        ILibraryManager libraryManager,
        AnalysisStore store,
        ILogger logger,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var gain = await probe.MeasureNormalizationGainAsync(item.Path, cancellationToken)
                .ConfigureAwait(false);
            if (gain is null)
            {
                logger.LogWarning("LoudnessScan: no measurement for {Name} ({Id})", item.Name, item.Id);
                store.RecordFailure(item.Id, "loudness", "loudnorm returned no result");
                return ToScanRecord(item.Id, null, "loudnorm returned no result", DateTime.UtcNow);
            }

            item.NormalizationGain = gain;
            var parent = libraryManager.GetItemById(item.ParentId) ?? item;
            await libraryManager.UpdateItemAsync(
                item,
                parent,
                ItemUpdateType.MetadataEdit,
                cancellationToken).ConfigureAwait(false);
            store.RemoveFailure(item.Id, "loudness"); // a successful measurement invalidates earlier failures
            logger.LogInformation(
                "LoudnessScan: {Name} ({Id}) NormalizationGain={Gain}",
                item.Name,
                item.Id,
                gain.Value.ToString("0.###", CultureInfo.InvariantCulture));
            return ToScanRecord(item.Id, gain, null, DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LoudnessScan: failed for {Name} ({Id})", item.Name, item.Id);
            store.RecordFailure(item.Id, "loudness", ex.Message);
            return ToScanRecord(item.Id, null, ex.Message, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Maps one item's measurement to the persisted outcome record. Extracted as a static so the
    /// success/failure derivation is unit-testable without Jellyfin plumbing or a real ffmpeg.
    /// </summary>
    internal static AnalysisStore.LoudnessScanRecord ToScanRecord(
        Guid itemId,
        float? gain,
        string? failureReason,
        DateTime finishedUtc) =>
        new()
        {
            ItemId = itemId,
            Gain = gain,
            FailureReason = failureReason,
            FinishedUtc = finishedUtc,
        };

    /// <summary>Runs automatically after every library scan.</summary>
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken) =>
        ExecuteAsync(progress, cancellationToken);
}