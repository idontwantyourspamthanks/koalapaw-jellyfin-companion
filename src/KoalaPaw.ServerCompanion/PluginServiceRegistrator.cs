using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaPaw.ServerCompanion;

/// <summary>Registers the MediaSegments provider so Jellyfin owns our segment provider id.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IMediaSegmentProvider, Segments.KoalaPawMediaSegmentProvider>();
        serviceCollection.AddSingleton<Segments.PendingSegmentResults>();
        serviceCollection.AddSingleton<Segments.ChromaprintProbe>();
        serviceCollection.AddSingleton<Segments.IntroSeasonAnalyser>();
        serviceCollection.AddSingleton<Loudness.LoudnessProbe>();
    }
}
