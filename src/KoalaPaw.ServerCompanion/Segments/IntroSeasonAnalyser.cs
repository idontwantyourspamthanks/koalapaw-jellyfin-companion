using System;
using System.Collections.Generic;
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
/// Fingerprints a season's episode heads and tails, correlates Intro + Outro windows, and stages
/// both segment types together so a write never wipes the other.
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
    /// Analyse [episodes] (same season). Returns how many items received staged segments.
    /// </summary>
    public async Task<int> AnalyseAndStageAsync(
        IReadOnlyList<BaseItem> episodes,
        CancellationToken cancellationToken)
    {
        var withPath = episodes
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .Take(12)
            .ToList();
        if (withPath.Count < 2)
        {
            return 0;
        }

        var heads = new List<uint[]?>(withPath.Count);
        var tails = new List<uint[]?>(withPath.Count);
        foreach (var ep in withPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            heads.Add(await _probe.FingerprintHeadAsync(ep.Path, cancellationToken).ConfigureAwait(false));
            tails.Add(await _probe.FingerprintTailAsync(ep.Path, cancellationToken).ConfigureAwait(false));
        }

        var intro = IntroCorrelator.GuessIntro(heads);
        var outro = IntroCorrelator.GuessOutro(tails);

        if (intro is null && outro is null)
        {
            _logger.LogInformation(
                "SegmentAnalyser: no confident intro/outro for season sample ({Count} episodes)",
                withPath.Count);
            return 0;
        }

        if (intro is not null)
        {
            _logger.LogInformation(
                "SegmentAnalyser: intro 0–{End:0.#}s confidence={Conf:0.00} across {Count} episodes",
                intro.Value.EndSeconds,
                intro.Value.Confidence,
                withPath.Count);
        }

        if (outro is not null)
        {
            _logger.LogInformation(
                "SegmentAnalyser: outro last {Len:0.#}s confidence={Conf:0.00} across {Count} episodes",
                outro.Value.EndSeconds,
                outro.Value.Confidence,
                withPath.Count);
        }

        var staged = 0;
        foreach (var ep in episodes.Where(e => !string.IsNullOrWhiteSpace(e.Path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryOptions = _libraryManager.GetLibraryOptions(ep);
            var existing = (await _segments.GetSegmentsAsync(
                    ep,
                    [MediaSegmentType.Intro, MediaSegmentType.Outro],
                    libraryOptions,
                    filterByProvider: true)
                .ConfigureAwait(false)).ToList();

            var segments = new List<MediaSegmentDto>();

            if (intro is not null)
            {
                var introEnd = (long)(intro.Value.EndSeconds * TimeSpan.TicksPerSecond);
                if (introEnd > 0)
                {
                    segments.Add(new MediaSegmentDto
                    {
                        ItemId = ep.Id,
                        Type = MediaSegmentType.Intro,
                        StartTicks = 0,
                        EndTicks = introEnd,
                    });
                }
            }
            else
            {
                // Provider replace is all-or-nothing for our rows — keep a prior Intro.
                foreach (var keep in existing.Where(s => s.Type == MediaSegmentType.Intro))
                {
                    segments.Add(CloneForItem(keep, ep.Id));
                }
            }

            if (outro is not null && ep.RunTimeTicks is > 0)
            {
                var creditTicks = (long)(outro.Value.EndSeconds * TimeSpan.TicksPerSecond);
                var runtime = ep.RunTimeTicks.Value;
                if (creditTicks > 0 && creditTicks < runtime)
                {
                    segments.Add(new MediaSegmentDto
                    {
                        ItemId = ep.Id,
                        Type = MediaSegmentType.Outro,
                        StartTicks = runtime - creditTicks,
                        EndTicks = runtime,
                    });
                }
            }
            else
            {
                foreach (var keep in existing.Where(s => s.Type == MediaSegmentType.Outro))
                {
                    segments.Add(CloneForItem(keep, ep.Id));
                }
            }

            if (segments.Count == 0)
            {
                continue;
            }

            _pending.Set(ep.Id, segments);
            await _segments.RunSegmentPluginProviders(ep, libraryOptions, forceOverwrite: false, cancellationToken)
                .ConfigureAwait(false);
            _pending.Clear(ep.Id);
            staged++;
        }

        return staged;
    }

    private static MediaSegmentDto CloneForItem(MediaSegmentDto source, Guid itemId) =>
        new()
        {
            ItemId = itemId,
            Type = source.Type,
            StartTicks = source.StartTicks,
            EndTicks = source.EndTicks,
        };
}
