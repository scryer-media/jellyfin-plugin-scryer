using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Scryer.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ScryerApiBaseUrl { get; set; } = "https://api.scryer.media";

    // Used for browser-facing image URLs when ScryerApiBaseUrl isn't reachable from the
    // client (e.g. host.docker.internal only resolves from inside the Jellyfin container).
    public string ScryerPublicBaseUrl { get; set; } = string.Empty;

    public string ScryerApiKey { get; set; } = string.Empty;

    public bool EnableRequests { get; set; } = true;

    public bool EnableDownloadPage { get; set; } = true;
}
