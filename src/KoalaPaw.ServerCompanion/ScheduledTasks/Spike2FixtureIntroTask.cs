using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KoalaPaw.ServerCompanion.Segments;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.ScheduledTasks;

/// <summary>
/// Manual fixture: write a 0–45s Intro on the Dentist episode so the client auto-skip path
/// can be eyeballed without waiting on season correlation. No default triggers.
/// </summary>
public class Spike2FixtureIntroTask : IScheduledTask
{
    private static readonly Guid DevEpisodeId = Guid.Parse("0075578c-e5b0-d149-0754-08f428d6b17f");

    public const long FixtureEndTicks = 45L * TimeSpan.TicksPerSecond;

    private readonly ILogger<Spike2FixtureIntroTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _segments;
    private readonly PendingSegmentResults _pending;

    public Spike2FixtureIntroTask(
        ILogger<Spike2FixtureIntroTask> logger,
        ILibraryManager libraryManager,
        IMediaSegmentManager segments,
        PendingSegmentResults pending)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _segments = segments;
        _pending = pending;
    }

    public string Name => "KoalaPaw spike 2 — fixture Intro segment";

    public string Key => "KoalaPawSpike2FixtureIntro";

    public string Description =>
        "DEV: writes a 0–45s Intro MediaSegment on the Dentist fixture episode. Manual only.";

    public string Category => "KoalaPaw";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(10);
        var item = _libraryManager.GetItemById(DevEpisodeId);
        if (item is null)
        {
            _logger.LogError("Spike2: episode {Id} not found", DevEpisodeId);
            progress.Report(100);
            return;
        }

        var dto = new MediaSegmentDto
        {
            ItemId = item.Id,
            Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
            StartTicks = 0,
            EndTicks = FixtureEndTicks,
        };
        _pending.Set(item.Id, [dto]);
        var opts = _libraryManager.GetLibraryOptions(item);
        await _segments.RunSegmentPluginProviders(item, opts, forceOverwrite: false, cancellationToken)
            .ConfigureAwait(false);
        _pending.Clear(item.Id);

        var written = await _segments.GetSegmentsAsync(
                item,
                [Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro],
                opts,
                filterByProvider: true)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Spike2 WRITE: id={Id} name=\"{Name}\" intros={Count} endTicks={End}",
            item.Id,
            item.Name,
            written.Count(),
            FixtureEndTicks);

        progress.Report(100);
    }
}
