using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KoalaPaw.ServerCompanion.Segments;

/// <summary>
/// Clean-room pairwise intro detection: find a common head window across episode fingerprints.
/// Emits nothing when confidence is weak.
/// </summary>
public static class IntroCorrelator
{
    /// <summary>Max bit differences (of 32) to count two fingerprint items as a match.</summary>
    public const int MaxHamming = 6;

    /// <summary>Minimum fraction of items that must match in the candidate window.</summary>
    public const double MinMatchRate = 0.70;

    public const double MinIntroSeconds = 15;
    public const double MaxIntroSeconds = 150;

    /// <summary>How far (in fingerprint items) to search for alignment near the start.</summary>
    public const int MaxStartShiftItems = 40; // ~5s

    /// <summary>Consecutive mismatches that end the intro window after min length.</summary>
    public const int MissStreakLimit = 6;

    public readonly record struct IntroGuess(double StartSeconds, double EndSeconds, double Confidence);

    /// <summary>
    /// Correlate tail fingerprints for a common credits suffix.
    /// [EndSeconds] is the credit length measured back from EOF (StartSeconds stays 0).
    /// </summary>
    public static IntroGuess? GuessOutro(IReadOnlyList<uint[]?> tailFingerprints)
    {
        var reversed = tailFingerprints
            .Select(f => f is null ? null : ReverseCopy(f))
            .ToList();
        return GuessIntro(reversed);
    }

    private static uint[] ReverseCopy(uint[] source)
    {
        var copy = new uint[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            copy[i] = source[source.Length - 1 - i];
        }

        return copy;
    }

    /// <summary>
    /// Correlate fingerprints from one season. Needs ≥2 non-null prints.
    /// Returns null when unsure.
    /// </summary>
    public static IntroGuess? GuessIntro(IReadOnlyList<uint[]?> fingerprints)
    {
        var prints = fingerprints.Where(f => f is { Length: > 50 }).Cast<uint[]>().ToList();
        if (prints.Count < 2)
        {
            return null;
        }

        var ends = new List<double>();
        var rates = new List<double>();
        for (var i = 0; i < prints.Count; i++)
        {
            for (var j = i + 1; j < prints.Count; j++)
            {
                var pair = BestHeadMatch(prints[i], prints[j]);
                if (pair is null)
                {
                    continue;
                }

                ends.Add(pair.Value.EndSeconds);
                rates.Add(pair.Value.MatchRate);
            }
        }

        if (ends.Count == 0)
        {
            return null;
        }

        ends.Sort();
        rates.Sort();
        var end = ends[ends.Count / 2];
        var rate = rates[rates.Count / 2];
        if (end < MinIntroSeconds || end > MaxIntroSeconds || rate < MinMatchRate)
        {
            return null;
        }

        return new IntroGuess(StartSeconds: 0, EndSeconds: end, Confidence: rate);
    }

    internal static (double EndSeconds, double MatchRate)? BestHeadMatch(uint[] a, uint[] b)
    {
        var minItems = ChromaprintProbe.SecondsToIndex(MinIntroSeconds);
        var maxItems = ChromaprintProbe.SecondsToIndex(MaxIntroSeconds);
        var bestRate = 0.0;
        var bestLen = 0;

        for (var shift = -MaxStartShiftItems; shift <= MaxStartShiftItems; shift++)
        {
            var a0 = Math.Max(0, shift);
            var b0 = Math.Max(0, -shift);
            var overlap = Math.Min(a.Length - a0, b.Length - b0);
            if (overlap < minItems)
            {
                continue;
            }

            var limit = Math.Min(overlap, maxItems);
            var matches = 0;
            var missStreak = 0;
            var prefix = 0;
            for (var k = 0; k < limit; k++)
            {
                if (Hamming(a[a0 + k], b[b0 + k]) <= MaxHamming)
                {
                    matches++;
                    missStreak = 0;
                    prefix = k + 1;
                }
                else
                {
                    missStreak++;
                    if (k + 1 >= minItems && missStreak >= MissStreakLimit)
                    {
                        break;
                    }
                }
            }

            if (prefix < minItems)
            {
                continue;
            }

            var rate = matches / (double)prefix;
            if (rate < MinMatchRate)
            {
                continue;
            }

            if (rate > bestRate || (Math.Abs(rate - bestRate) < 0.02 && prefix > bestLen))
            {
                bestRate = rate;
                bestLen = prefix;
            }
        }

        if (bestLen < minItems || bestRate < MinMatchRate)
        {
            return null;
        }

        return (ChromaprintProbe.IndexToSeconds(bestLen), bestRate);
    }

    internal static int Hamming(uint x, uint y) => BitOperations.PopCount(x ^ y);
}
