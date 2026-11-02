using System;
using System.Collections.Generic;
using Jellyfin.Plugin.C411Torbox.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.C411Torbox;

/// <summary>
/// Point d'entrée du plugin « C411 → Torbox ».
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Identifiant unique du plugin.</summary>
    public static readonly Guid PluginId = Guid.Parse("a1beec8b-cc16-4382-96ca-8d4cdbfd032c");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "C411 Torbox";

    public override Guid Id => PluginId;

    public override string Description => "Recherche de torrents C411 (via Prowlarr), demandes utilisateurs avec validation admin et envoi vers Torbox.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = "C411Torbox",
                EmbeddedResourcePath = ns + ".Configuration.configPage.html",
            },
            new PluginPageInfo
            {
                Name = "C411TorboxRequests",
                DisplayName = "Demandes C411",
                EmbeddedResourcePath = ns + ".Configuration.requestsPage.html",
                EnableInMainMenu = true,
                MenuIcon = "playlist_add_check",
            },
        ];
    }
}
