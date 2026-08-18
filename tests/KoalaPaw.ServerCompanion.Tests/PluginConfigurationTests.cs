using KoalaPaw.ServerCompanion.Configuration;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void Defaults_matchExpectedValues()
    {
        var config = new PluginConfiguration();

        Assert.True(config.LoudnessEnabled);
        Assert.True(config.SegmentDetectionEnabled);
        Assert.Empty(config.ListedUsers);
        Assert.Equal(-18f, config.TargetLufs);
        Assert.Equal(40, config.MaxLoudnessItemsPerRun);
        Assert.Equal(200, config.FingerprintHeadSeconds);
        Assert.Equal(300, config.FingerprintTailSeconds);
    }

    [Fact]
    public void Properties_areSettable()
    {
        var config = new PluginConfiguration
        {
            LoudnessEnabled = false,
            SegmentDetectionEnabled = false,
            TargetLufs = -16f,
            MaxLoudnessItemsPerRun = 10,
            FingerprintHeadSeconds = 120,
            FingerprintTailSeconds = 180,
        };

        Assert.False(config.LoudnessEnabled);
        Assert.False(config.SegmentDetectionEnabled);
        Assert.Equal(-16f, config.TargetLufs);
        Assert.Equal(10, config.MaxLoudnessItemsPerRun);
        Assert.Equal(120, config.FingerprintHeadSeconds);
        Assert.Equal(180, config.FingerprintTailSeconds);
    }
}