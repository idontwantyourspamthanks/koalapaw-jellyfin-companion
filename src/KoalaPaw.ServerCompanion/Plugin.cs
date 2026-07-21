using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using KoalaPaw.ServerCompanion.Configuration;

namespace KoalaPaw.ServerCompanion;

/// <summary>
/// KoalaPaw Server Companion — loudness + intro MediaSegments analysers.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
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

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "KoalaPaw",
                EmbeddedResourcePath = "KoalaPaw.ServerCompanion.Web.config.html",
                EnableInMainMenu = true,
            },
        ];
    }
}