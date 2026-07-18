using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MediaBrowser.Model.MediaSegments;

namespace KoalaPaw.ServerCompanion.Segments;

/// <summary>
/// Side-channel for <see cref="KoalaPawMediaSegmentProvider"/>: the scan task parks analysed
/// segments here, then calls <c>RunSegmentPluginProviders</c> so Jellyfin deletes/replaces only
/// our provider's rows (never item-wide wipe).
/// </summary>
public sealed class PendingSegmentResults
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<MediaSegmentDto>> _pending = new();

    public void Set(Guid itemId, IReadOnlyList<MediaSegmentDto> segments) =>
        _pending[itemId] = segments;

    public bool TryTake(Guid itemId, out IReadOnlyList<MediaSegmentDto> segments) =>
        _pending.TryRemove(itemId, out segments!);

    public void Clear(Guid itemId) => _pending.TryRemove(itemId, out _);
}
