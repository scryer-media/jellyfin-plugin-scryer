using Jellyfin.Plugin.Scryer.AndroidTv;
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
        // The plugin owns its DataProtection key ring. Jellyfin's injected provider is ephemeral
        // whenever the server has no writable user profile (the standard container deployment),
        // which silently invalidates every stored refresh grant on restart.
        serviceCollection.AddSingleton<ScryerDataProtection>();
        serviceCollection.AddSingleton<ScryerOAuthMetadataClient>();
        serviceCollection.AddSingleton<IScryerTokenStore, ScryerTokenStore>();
        serviceCollection.AddSingleton<IScryerJellyfinLinkService, ScryerJellyfinLinkService>();
        serviceCollection.AddSingleton<IScryerUserSessionService, ScryerUserSessionService>();
        serviceCollection.AddSingleton<IHostedService, ScryerPendingLinkCleanupService>();
        serviceCollection.AddSingleton<IScryerGraphqlService, ScryerGraphqlService>();
        serviceCollection.AddSingleton<ScryerDiscoveryChannel>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.Channels.IChannel>(provider => provider.GetRequiredService<ScryerDiscoveryChannel>());
        serviceCollection.AddSingleton<IScryerTvActionJournal, ScryerTvActionJournal>();
        serviceCollection.AddSingleton<IHostedService, ScryerTvFavoriteService>();
        serviceCollection.AddSingleton<ScryerOAuthFlowStore>();
        serviceCollection.AddSingleton<IScryerOAuthFlowService, ScryerOAuthFlowService>();
        serviceCollection.AddSingleton<ScryerInjectionStatus>();
        serviceCollection.AddSingleton<ScryerConnectionDiagnostics>();
        serviceCollection.AddSingleton<IStartupFilter, ScriptTagInjectionStartupFilter>();
    }
}
