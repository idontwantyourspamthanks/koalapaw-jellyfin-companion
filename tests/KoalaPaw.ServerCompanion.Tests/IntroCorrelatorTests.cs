using System;
using KoalaPaw.ServerCompanion.Segments;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class IntroCorrelatorTests
{
    [Fact]
    public void GuessIntro_matchingHeads_findsWindow()
    {
        var intro = BuildRun(length: ChromaprintProbe.SecondsToIndex(40), seed: 42);
        var a = Concat(intro, BuildRun(200, seed: 1));
        var b = Concat(intro, BuildRun(200, seed: 2));
        var c = Concat(intro, BuildRun(200, seed: 3));

        var guess = IntroCorrelator.GuessIntro([a, b, c]);
        Assert.NotNull(guess);
        Assert.InRange(guess!.Value.EndSeconds, 30, 55);
        Assert.True(guess.Value.Confidence >= IntroCorrelator.MinMatchRate);
    }

    [Fact]
    public void GuessIntro_unrelated_returnsNull()
    {
        var a = BuildRun(400, seed: 11);
        var b = BuildRun(400, seed: 99);
        Assert.Null(IntroCorrelator.GuessIntro([a, b]));
    }

    [Fact]
    public void GuessIntro_singlePrint_returnsNull()
    {
        Assert.Null(IntroCorrelator.GuessIntro([BuildRun(200, seed: 1)]));
    }

    private static uint[] BuildRun(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new uint[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (uint)rng.Next();
        }

        return data;
    }

    private static uint[] Concat(uint[] head, uint[] tail)
    {
        var all = new uint[head.Length + tail.Length];
        Array.Copy(head, 0, all, 0, head.Length);
        Array.Copy(tail, 0, all, head.Length, tail.Length);
        return all;
    }
}
