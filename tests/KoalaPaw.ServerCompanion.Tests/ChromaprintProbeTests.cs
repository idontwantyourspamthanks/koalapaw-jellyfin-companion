using System;
using KoalaPaw.ServerCompanion.Segments;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class ChromaprintProbeTests
{
    [Fact]
    public void ParseFingerprintBytes_littleEndianLayout_pinsByteOrder()
    {
        // chromaprint raw + Buffer.BlockCopy reinterpret bytes in native order. Every platform we
        // ship to (x64, ARM) is little-endian, so the first byte is the least-significant — pin that
        // assumption so a future endian change is caught loudly rather than corrupting fingerprints.
        if (!BitConverter.IsLittleEndian)
        {
            return;
        }

        var bytes = new byte[] { 0x78, 0x56, 0x34, 0x12, 0x01, 0x00, 0x00, 0x00 };

        var items = ChromaprintProbe.ParseFingerprintBytes(bytes);

        Assert.NotNull(items);
        Assert.Equal(new[] { 0x12345678u, 0x00000001u }, items);
    }

    [Fact]
    public void ParseFingerprintBytes_roundTripsUintValues()
    {
        var expected = new[] { 0u, 1u, 0xDEADBEEFu, 0x12345678u, uint.MaxValue };
        var bytes = new byte[expected.Length * 4];
        Buffer.BlockCopy(expected, 0, bytes, 0, bytes.Length);

        var items = ChromaprintProbe.ParseFingerprintBytes(bytes);

        Assert.Equal(expected, items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ParseFingerprintBytes_lengthNotPositiveMultipleOfFour_returnsNull(int length)
    {
        Assert.Null(ChromaprintProbe.ParseFingerprintBytes(new byte[length]));
    }

    [Fact]
    public void IndexSecondsConversions_roundTrip()
    {
        Assert.Equal(0.0, ChromaprintProbe.IndexToSeconds(0));
        Assert.Equal(0, ChromaprintProbe.SecondsToIndex(0));

        // ~155s is the practical max head audio IntroCorrelator inspects; converting out and back
        // should land within one item (~0.124s).
        var index = ChromaprintProbe.SecondsToIndex(155);
        Assert.InRange(ChromaprintProbe.IndexToSeconds(index), 154.9, 155.1);
    }

    [Fact]
    public void HeadSeconds_coversMaxIntroWindow()
    {
        // The head window must fingerprint at least as far as intro detection ever looks.
        Assert.True(ChromaprintProbe.DefaultHeadSeconds >= IntroCorrelator.MaxIntroSeconds);
    }
}
