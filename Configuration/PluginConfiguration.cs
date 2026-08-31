using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Scryer.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ScryerApiBaseUrl { get; set; } = "https://api.scryer.media";

    public string ScryerApiKey { get; set; } = string.Empty;

    public bool EnableRequests { get; set; } = true;

    public bool EnableDownloadPage { get; set; } = true;
}
