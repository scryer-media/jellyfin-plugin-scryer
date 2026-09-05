using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Scryer.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    private string _scryerInternalBaseUrl = "https://api.scryer.media";
    private string _scryerPublicBaseUrl = "https://app.scryer.media";
    private string _oauthClientId = string.Empty;
    private string _jellyfinPublicBaseUrl = string.Empty;
    private bool _requiresLegacyRewrite;

    /// <summary>URL used by the Jellyfin server for Scryer requests and OAuth discovery.</summary>
    public string ScryerInternalBaseUrl
    {
        get => _scryerInternalBaseUrl;
        set => _scryerInternalBaseUrl = ScryerConfigurationValidator.NormalizeBaseUrl(value);
    }

    /// <summary>Explicit opt-in for cleartext server-to-server OAuth on trusted private networks.</summary>
    public bool AllowInsecureInternalScryerHttp { get; set; }

    /// <summary>Public Scryer URL used as the OAuth authorization-server authority.</summary>
    public string ScryerPublicBaseUrl
    {
        get => _scryerPublicBaseUrl;
        set => _scryerPublicBaseUrl = ScryerConfigurationValidator.NormalizeBaseUrl(value);
    }

    /// <summary>Registered Scryer OAuth public-client identifier. It is not a secret.</summary>
    public string OAuthClientId
    {
        get => _oauthClientId;
        set => _oauthClientId = ScryerConfigurationValidator.NormalizeClientId(value);
    }

    /// <summary>Public Jellyfin URL from which the exact OAuth callback URI is derived.</summary>
    public string JellyfinPublicBaseUrl
    {
        get => _jellyfinPublicBaseUrl;
        set => _jellyfinPublicBaseUrl = ScryerConfigurationValidator.NormalizeBaseUrl(value);
    }

    public bool EnableDiscovery { get; set; } = true;

    public bool EnableRequests { get; set; } = true;

    public bool EnableCalendar { get; set; } = true;

    public bool EnableDownloads { get; set; } = true;

    /// <summary>
    /// Opt-in for the native Android TV discovery channel. It is off by default because
    /// Jellyfin persists channel items into its own library database, one set of rows per user.
    /// </summary>
    public bool EnableAndroidTvChannel { get; set; }

    /// <summary>
    /// Largest number of discovery rails the Android TV channel will publish. Scryer decides how
    /// many sections to return and Jellyfin persists every published folder as a library row per
    /// user, so the plugin caps what it hands over rather than storing whatever arrives.
    /// </summary>
    public int MaxAndroidTvRails { get; set; } = ScryerAndroidTvLimits.DefaultRails;

    /// <summary>
    /// Largest number of titles the Android TV channel will publish inside one rail. Each title
    /// becomes a library row and a downloaded poster for the user who opened the rail.
    /// </summary>
    public int MaxAndroidTvItemsPerRail { get; set; } = ScryerAndroidTvLimits.DefaultItemsPerRail;

    public ScryerDiagnosticVerbosity DiagnosticVerbosity { get; set; } = ScryerDiagnosticVerbosity.Basic;

    /// <summary>
    /// Deserialization-only bridge for configurations written before RFC 153. The
    /// legacy API key is intentionally not migrated or exposed.
    /// </summary>
    [JsonIgnore]
    [System.Obsolete("Legacy configuration migration only.")]
    public string ScryerApiBaseUrl
    {
        get => string.Empty;
        set
        {
            _requiresLegacyRewrite = true;
            if (!string.IsNullOrWhiteSpace(value))
            {
                ScryerInternalBaseUrl = value;
            }
        }
    }

    [JsonIgnore]
    [System.Obsolete("Legacy configuration migration only.")]
    public bool EnableDownloadPage
    {
        get => EnableDownloads;
        set
        {
            _requiresLegacyRewrite = true;
            EnableDownloads = value;
        }
    }

    [JsonIgnore]
    [System.Obsolete("Legacy credential scrub only.")]
    public string ScryerApiKey
    {
        get => string.Empty;
        set => _requiresLegacyRewrite = true;
    }

    public bool ShouldSerializeScryerApiBaseUrl() => false;

    public bool ShouldSerializeEnableDownloadPage() => false;

    public bool ShouldSerializeScryerApiKey() => false;

    internal bool RequiresLegacyRewrite => _requiresLegacyRewrite;

    internal void MarkLegacyRewriteComplete() => _requiresLegacyRewrite = false;

    [JsonIgnore]
    public ScryerConfigurationValidation Validation => ScryerConfigurationValidator.Validate(this);
}

public enum ScryerDiagnosticVerbosity
{
    Off = 0,
    Basic = 1,
    Detailed = 2
}
