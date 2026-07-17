using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Spike 1: write <see cref="BaseItem.NormalizationGain"/> on a video Episode via
/// <see cref="ILibraryManager.UpdateItemAsync"/>, then re-read. Manual-run only.
/// Proves the loudness contract before building the real analyser.
/// </summary>
public class Spike1NormalizationGainTask : IScheduledTask
{
    /// <summary>Distinctive sentinel so we can spot it in API JSON / DB.</summary>
    public const float SentinelGain = -12.345f;

    /// <summary>"Dentist" episode on the dev Pi — swap if that library changes.</summary>
    private static readonly Guid DevEpisodeId = Guid.Parse("0075578c-e5b0-d149-0754-08f428d6b17f");

    private readonly ILogger<Spike1NormalizationGainTask> _logger;
    private readonly ILibraryManager _libraryManager;

    public Spike1NormalizationGainTask(
        ILogger<Spike1NormalizationGainTask> logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "KoalaPaw spike 1 — NormalizationGain on video";

    public string Key => "KoalaPawSpike1NormalizationGain";

    public string Description =>
        "DEV ONLY: writes NormalizationGain=-12.345 on a known episode, re-reads it. " +
        "Then check GET /Items/{id} and a FullRefresh. No schedule — run manually.";

    public string Category => "KoalaPaw";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(10);
        var item = _libraryManager.GetItemById(DevEpisodeId);
        if (item is null)
        {
            _logger.LogError(
                "Spike1: episode {Id} not found — pick another Id in Spike1NormalizationGainTask",
                DevEpisodeId);
            progress.Report(100);
            return;
        }

        if (item is not Episode and not Video)
        {
            _logger.LogWarning(
                "Spike1: {Name} ({Id}) is {Type}, expected Episode/Video — continuing anyway",
                item.Name,
                item.Id,
                item.GetType().Name);
        }

        var before = item.NormalizationGain;
        item.NormalizationGain = SentinelGain;
        var parent = _libraryManager.GetItemById(item.ParentId) ?? item;
        await _libraryManager.UpdateItemAsync(
            item,
            parent,
            ItemUpdateType.MetadataEdit,
            cancellationToken).ConfigureAwait(false);

        progress.Report(60);
        var again = _libraryManager.GetItemById(item.Id);
        var after = again?.NormalizationGain;

        _logger.LogInformation(
            "Spike1 WRITE: id={Id} name=\"{Name}\" type={Type} before={Before} wrote={Wrote} reRead={After}",
            item.Id,
            item.Name,
            item.GetType().Name,
            before?.ToString(CultureInfo.InvariantCulture) ?? "null",
            SentinelGain.ToString(CultureInfo.InvariantCulture),
            after?.ToString(CultureInfo.InvariantCulture) ?? "null");

        progress.Report(100);
    }
}
