using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Scryer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Scryer;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        var configuration = Configuration;
        if (configuration.RequiresLegacyRewrite)
        {
            configuration.MarkLegacyRewriteComplete();
            SaveConfiguration(configuration);
        }
    }

    public override string Name => "Scryer";

    public override Guid Id => Guid.Parse("6a9c9f2e-6b2f-4a1a-9d3d-2c7f2f5b3d10");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;

        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html"
            }
        };
    }
}
