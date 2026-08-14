using System.Collections.Generic;

namespace KoalaPaw.ServerCompanion.Segments;

/// <summary>
/// Per-episode Intro/Outro segment state as shown by the segment dashboard. Pure derivation
/// (no Jellyfin types) so the TV app and unit tests can reuse it.
/// </summary>
public static class EpisodeSegmentState
{
    public const string Detected = "detected";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string NotScanned = "notScanned";

    /// <summary>
    /// Derive an episode's segment state from ground truth. A failure record only counts while
    /// no segments are in place — a later successful detection invalidates the failure — so
    /// stale records written by older plugin versions never shadow real segments.
    /// </summary>
    public static string Derive(bool hasIntro, bool hasOutro, bool hasFailure)
    {
        if (hasFailure && !hasIntro && !hasOutro)
        {
            return Failed;
        }

        if (hasIntro && hasOutro)
        {
            return Detected;
        }

        if (hasIntro != hasOutro)
        {
            return Partial;
        }

        return NotScanned;
    }

    /// <summary>Roll a set of per-episode states up into the dashboard's aggregate counts.</summary>
    public static SegmentStateCounts Count(IEnumerable<string> states)
    {
        var detected = 0;
        var partial = 0;
        var failed = 0;
        var notScanned = 0;
        foreach (var state in states)
        {
            switch (state)
            {
                case Detected:
                    detected++;
                    break;
                case Partial:
                    partial++;
                    break;
                case Failed:
                    failed++;
                    break;
                case NotScanned:
                    notScanned++;
                    break;
            }
        }

        return new SegmentStateCounts(detected, partial, failed, notScanned);
    }

    /// <summary>Aggregate counts for a set of per-episode segment states.</summary>
    public readonly record struct SegmentStateCounts(int Detected, int Partial, int Failed, int NotScanned)
    {
        /// <summary>Total number of episodes represented.</summary>
        public int Total => Detected + Partial + Failed + NotScanned;
    }
}
