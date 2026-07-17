using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using KoalaPaw.ServerCompanion.Configuration;

namespace KoalaPaw.ServerCompanion;

/// <summary>
/// KoalaPaw Server Companion — one plugin, two analysers (loudness + intro/credits).
/// Skeleton plus loudness analyser: loads cleanly and schedules video NormalizationGain scans.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>Stable forever — used by Jellyfin catalogs and meta.json.</summary>
    public static readonly Guid PluginGuid = new("cea40e65-c04f-4854-b6c4-3428360087fe");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "KoalaPaw Server Companion";

    public override Guid Id => PluginGuid;

    public override string Description =>
        "Skip intros and even out video loudness. Writes standard Jellyfin NormalizationGain and MediaSegments.";

    public static Plugin? Instance { get; private set; }
}
