using System.IO;
using KoalaPaw.ServerCompanion.Bookkeeping;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        serviceCollection.AddSingleton<ThemeSongs.ThemeFromIntroExtractor>();
        serviceCollection.AddSingleton<Recommendations.RecommendationService>();
        serviceCollection.AddSingleton<AnalysisStore>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<AnalysisStore>>();
            return new AnalysisStore(
                Path.Combine(appPaths.PluginConfigurationsPath, "KoalaPaw Server Companion"),
                logger);
        });
    }
}