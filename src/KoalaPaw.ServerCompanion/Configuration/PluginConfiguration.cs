using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace KoalaPaw.ServerCompanion.Configuration;

/// <summary>Plugin settings — analysis toggles and tunables plus the TV sign-in directory, all with sensible defaults.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Users offered on the unauthenticated TV sign-in picker, with their kid flag and avatar.
    /// Only users without a configured password are ever listed, and only ids still present on
    /// the server are returned.
    /// </summary>
    public List<ListedUserEntry> ListedUsers { get; set; } = [];

    public bool LoudnessEnabled { get; set; } = true;

    public bool SegmentDetectionEnabled { get; set; } = true;

    public float TargetLufs { get; set; } = -18f;

    public int MaxLoudnessItemsPerRun { get; set; } = 40;

    public int FingerprintHeadSeconds { get; set; } = 200;

    public int FingerprintTailSeconds { get; set; } = 300;

    /// <summary>One admin-opted-in entry for the TV sign-in picker.</summary>
    public sealed class ListedUserEntry
    {
        public Guid UserId { get; set; }

        public bool Kid { get; set; }

        public string Avatar { get; set; } = string.Empty;
    }
}
