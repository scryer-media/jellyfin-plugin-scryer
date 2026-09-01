using Jellyfin.Plugin.Scryer.OAuth;
using Jellyfin.Plugin.Scryer.Services;
using Jellyfin.Plugin.Scryer.WebInjection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Scryer;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(nameof(ScryerGraphqlService))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        serviceCollection.AddSingleton<IScryerOAuthConfigurationProvider, PluginScryerOAuthConfigurationProvider>();
        serviceCollection.AddSingleton<ScryerOAuthMetadataClient>();
        serviceCollection.AddSingleton<IScryerTokenStore, ScryerTokenStore>();
        serviceCollection.AddSingleton<IScryerJellyfinLinkService, ScryerJellyfinLinkService>();
        serviceCollection.AddSingleton<IScryerUserSessionService, ScryerUserSessionService>();
        serviceCollection.AddSingleton<IHostedService, ScryerPendingLinkCleanupService>();
        serviceCollection.AddSingleton<IScryerGraphqlService, ScryerGraphqlService>();
        serviceCollection.AddSingleton<ScryerOAuthFlowStore>();
        serviceCollection.AddSingleton<IScryerOAuthFlowService, ScryerOAuthFlowService>();
        serviceCollection.AddSingleton<ScryerInjectionStatus>();
        serviceCollection.AddSingleton<ScryerConnectionDiagnostics>();
        serviceCollection.AddSingleton<IStartupFilter, ScriptTagInjectionStartupFilter>();
    }
}
