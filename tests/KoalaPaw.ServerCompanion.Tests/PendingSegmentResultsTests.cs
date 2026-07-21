using System;
using System.Collections.Generic;
using KoalaPaw.ServerCompanion.Segments;
using MediaBrowser.Model.MediaSegments;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class PendingSegmentResultsTests
{
    [Fact]
    public void TryTake_afterSet_returnsSegmentsAndEvicts()
    {
        var pending = new PendingSegmentResults();
        var id = Guid.NewGuid();
        var segments = Segments(id, 1);
        pending.Set(id, segments);

        Assert.True(pending.TryTake(id, out var taken));
        Assert.Same(segments, taken);

        // Evicted — a second take must miss so a stale result can't leak into a later request.
        Assert.False(pending.TryTake(id, out _));
    }

    [Fact]
    public void TryTake_missing_returnsFalse()
    {
        var pending = new PendingSegmentResults();
        Assert.False(pending.TryTake(Guid.NewGuid(), out _));
    }

    [Fact]
    public void Clear_removesEntry()
    {
        var pending = new PendingSegmentResults();
        var id = Guid.NewGuid();
        pending.Set(id, Segments(id, 1));

        pending.Clear(id);

        Assert.False(pending.TryTake(id, out _));
    }

    [Fact]
    public void Clear_missing_isNoOp()
    {
        var pending = new PendingSegmentResults();
        pending.Clear(Guid.NewGuid()); // must not throw
    }

    [Fact]
    public void Set_overwritesPriorEntry()
    {
        var pending = new PendingSegmentResults();
        var id = Guid.NewGuid();
        pending.Set(id, Segments(id, 1));
        var replacement = Segments(id, 2);

        pending.Set(id, replacement);

        Assert.True(pending.TryTake(id, out var taken));
        Assert.Same(replacement, taken);
    }

    private static IReadOnlyList<MediaSegmentDto> Segments(Guid itemId, int count)
    {
        var list = new List<MediaSegmentDto>();
        for (var i = 0; i < count; i++)
        {
            list.Add(new MediaSegmentDto { ItemId = itemId });
        }

        return list;
    }
}
