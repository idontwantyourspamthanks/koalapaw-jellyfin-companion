using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;

namespace KoalaPaw.ServerCompanion.Segments;

/// <summary>
/// Jellyfin MediaSegments provider. Real analysis lives in the scan task;
/// this type owns the provider name/id and applies pending results (or keeps existing).
/// </summary>
public sealed class KoalaPawMediaSegmentProvider : IMediaSegmentProvider
{
    public const string ProviderName = "KoalaPaw";

    private readonly PendingSegmentResults _pending;

    public KoalaPawMediaSegmentProvider(PendingSegmentResults pending)
    {
        _pending = pending;
    }

    public string Name => ProviderName;

    public ValueTask<bool> Supports(BaseItem item) =>
        ValueTask.FromResult(item is Episode && !string.IsNullOrWhiteSpace(item.Path));

    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaBrowser.Model.MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pending.TryTake(request.ItemId, out var pending))
        {
            return Task.FromResult(pending);
        }

        return Task.FromResult(request.ExistingSegments);
    }

    /// <summary>
    /// Nothing to prune. Segment rows live in Jellyfin, and the theme sting is a series extra, not per-item extracted data.
    /// </summary>
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
