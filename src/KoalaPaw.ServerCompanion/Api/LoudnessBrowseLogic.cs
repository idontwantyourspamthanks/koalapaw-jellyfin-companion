using System;
using System.Collections.Generic;
using System.Linq;
using KoalaPaw.ServerCompanion.Bookkeeping;
using KoalaPaw.ServerCompanion.Loudness;

namespace KoalaPaw.ServerCompanion.Api;

/// <summary>
/// Pure precedence seams for the loudness browse tiers. Extracted as a static class so the
/// gain/failure merge that decides what the dashboard shows and the newest-finish-time rollup
/// are unit-testable without Jellyfin plumbing.
/// </summary>
internal static class LoudnessBrowseLogic
{
    /// <summary>One item's resolved loudness ground truth, as the browse tiers see it.</summary>
    internal sealed record ItemStatus(string State, double? Gain, string? FailureReason);

    /// <summary>
    /// Resolves one item's dashboard ground truth. The gain is the item's NormalizationGain (what
    /// is actually applied) when present, falling back to the persisted record's measured gain; a
    /// live failure entry (what a rescan clears) beats the record's historical one. Extracted as
    /// a static so the precedence rules are unit-testable without Jellyfin plumbing.
    /// </summary>
    internal static ItemStatus ResolveItemStatus(
        Guid itemId,
        IReadOnlyDictionary<Guid, string> failuresByItem,
        double? normalizationGain,
        AnalysisStore.LoudnessScanRecord? record)
    {
        var gain = normalizationGain ?? record?.Gain;
        var failureReason = failuresByItem.TryGetValue(itemId, out var recorded)
            ? recorded
            : record?.FailureReason;

        return new ItemStatus(
            LoudnessItemState.Derive(hasGain: gain is not null, hasFailure: failureReason is not null),
            gain,
            failureReason);
    }

    /// <summary>
    /// Newest non-null finish time across a tier's items, or null when nothing has finished.
    /// Extracted as a static so the "ignore nulls, ignore empty" rule is unit-testable without
    /// Jellyfin plumbing.
    /// </summary>
    internal static DateTime? MaxFinishedUtc(IEnumerable<DateTime?> values)
    {
        var max = values.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty().Max();
        return max == default ? null : max;
    }
}
