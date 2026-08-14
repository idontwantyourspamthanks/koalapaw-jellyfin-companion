using MediaBrowser.Model.Plugins;

namespace KoalaPaw.ServerCompanion.Configuration;

/// <summary>Plugin settings — analysis toggles and tunables, all with sensible defaults.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public bool LoudnessEnabled { get; set; } = true;

    public bool SegmentDetectionEnabled { get; set; } = true;

    public float TargetLufs { get; set; } = -18f;

    public int MaxLoudnessItemsPerRun { get; set; } = 40;

    public int FingerprintHeadSeconds { get; set; } = 200;

    public int FingerprintTailSeconds { get; set; } = 300;
}
