using KoalaPaw.ServerCompanion.Loudness;
using Xunit;

namespace KoalaPaw.ServerCompanion.Tests;

public class LoudnessProbeParseTests
{
    [Fact]
    public void ParseInputIntegrated_readsLoudnormJson()
    {
        const string stderr = """
            [Parsed_loudnorm_0 @ 0x1]
            {
            	"input_i" : "-23.49",
            	"input_tp" : "-8.65",
            	"input_lra" : "4.80"
            }
            size=N/A
            """;

        var lufs = LoudnessProbe.ParseInputIntegrated(stderr);
        Assert.Equal(-23.49f, lufs);

        var gain = LoudnessProbe.TargetLufs - lufs!.Value;
        Assert.Equal(5.49f, gain, precision: 2);
    }

    [Fact]
    public void ParseInputIntegrated_missing_returnsNull()
    {
        Assert.Null(LoudnessProbe.ParseInputIntegrated("no json here"));
    }
}
