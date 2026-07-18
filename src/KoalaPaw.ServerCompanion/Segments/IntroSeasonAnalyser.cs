using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace KoalaPaw.ServerCompanion.Segments;

/// <summary>
/// Fingerprints a season's episode heads, correlates an Intro window, and stages results for
/// <see cref="KoalaPawMediaSegmentProvider"/> via <see cref="PendingSegmentResults"/>.
/// </summary>
public sealed class IntroSeasonAnalyser
{
    private readonly ChromaprintProbe _probe;
    private readonly PendingSegmentResults _pending;
    private readonly IMediaSegmentManager _segments;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<IntroSeasonAnalyser> _logger;

    public IntroSeasonAnalyser(
        ChromaprintProbe probe,
        PendingSegmentResults pending,
        IMediaSegmentManager segments,
        ILibraryManager libraryManager,
        ILogger<IntroSeasonAnalyser> logger)
    {
        _probe = probe;
        _pending = pending;
        _segments = segments;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Analyse [episodes] (same season). Returns how many items received a staged Intro.
    /// </summary>
    public async Task<int> AnalyseAndStageAsync(
        IReadOnlyList<BaseItem> episodes,
        CancellationToken cancellationToken)
    {
        var withPath = episodes
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Take(12) // enough pairs for correlation; caps Pi work on huge seasons
            .ToList();
        if (withPath.Count < 2)
        {
            return 0;
        }

        var fps = new List<uint[]?>(withPath.Count);
        foreach (var ep in withPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fps.Add(await _probe.FingerprintHeadAsync(ep.Path, cancellationToken).ConfigureAwait(false));
        }

        var guess = IntroCorrelator.GuessIntro(fps);
        if (guess is null)
        {
            _logger.LogInformation(
                "IntroAnalyser: no confident intro for season sample ({Count} episodes)",
                withPath.Count);
            return 0;
        }

        var startTicks = (long)(guess.Value.StartSeconds * TimeSpan.TicksPerSecond);
        var endTicks = (long)(guess.Value.EndSeconds * TimeSpan.TicksPerSecond);
        if (endTicks <= startTicks)
        {
            return 0;
        }

        _logger.LogInformation(
            "IntroAnalyser: intro 0–{End:0.#}s confidence={Conf:0.00} across {Count} episodes",
            guess.Value.EndSeconds,
            guess.Value.Confidence,
            withPath.Count);

        // Apply to every episode in the season list (not just the fingerprint sample).
        var staged = 0;
        foreach (var ep in episodes.Where(e => !string.IsNullOrWhiteSpace(e.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = new MediaSegmentDto
            {
                ItemId = ep.Id,
                Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                StartTicks = startTicks,
                EndTicks = endTicks,
            };
            _pending.Set(ep.Id, [dto]);
            var libraryOptions = _libraryManager.GetLibraryOptions(ep);
            await _segments.RunSegmentPluginProviders(ep, libraryOptions, forceOverwrite: false, cancellationToken)
                .ConfigureAwait(false);
            _pending.Clear(ep.Id);
            staged++;
        }

        return staged;
    }
}
