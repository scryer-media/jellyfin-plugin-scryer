using Jellyfin.Plugin.Scryer.Services;
using Jellyfin.Plugin.Scryer.WebInjection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Scryer;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<ScryerApiClient>();
        serviceCollection.AddSingleton<RequestAttributionStore>();
        serviceCollection.AddSingleton<IStartupFilter, ScriptTagInjectionStartupFilter>();
    }
}
