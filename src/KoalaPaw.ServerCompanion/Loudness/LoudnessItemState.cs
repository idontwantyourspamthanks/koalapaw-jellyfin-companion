using System.Collections.Generic;

namespace KoalaPaw.ServerCompanion.Loudness;

/// <summary>
/// Per-item loudness state as shown by the loudness dashboard. Pure derivation (no Jellyfin
/// types) so the dashboard and unit tests can reuse it.
/// </summary>
public static class LoudnessItemState
{
    public const string Scanned = "scanned";
    public const string Failed = "failed";
    public const string NotScanned = "notScanned";

    /// <summary>
    /// Derive an item's loudness state from ground truth. A gain in place wins over any failure
    /// record — a later successful measurement invalidates the failure — so stale records never
    /// shadow a real gain.
    /// </summary>
    public static string Derive(bool hasGain, bool hasFailure)
    {
        if (hasGain)
        {
            return Scanned;
        }

        return hasFailure ? Failed : NotScanned;
    }

    /// <summary>Roll a set of per-item states up into the dashboard's aggregate counts.</summary>
    public static LoudnessStateCounts Count(IEnumerable<string> states)
    {
        var scanned = 0;
        var failed = 0;
        var notScanned = 0;
        foreach (var state in states)
        {
            switch (state)
            {
                case Scanned:
                    scanned++;
                    break;
                case Failed:
                    failed++;
                    break;
                case NotScanned:
                    notScanned++;
                    break;
            }
        }

        return new LoudnessStateCounts(scanned, failed, notScanned);
    }

    /// <summary>Aggregate counts for a set of per-item loudness states.</summary>
    public readonly record struct LoudnessStateCounts(int Scanned, int Failed, int NotScanned)
    {
        /// <summary>Total number of items represented.</summary>
        public int Total => Scanned + Failed + NotScanned;
    }
}
