namespace Jellyfin.Plugin.C411Torbox.Configuration;

/// <summary>Source des torrents.</summary>
public enum IndexerMode
{
    /// <summary>API Torznab de C411 en direct (https://c411.org/api), comme dans Radarr/Sonarr.</summary>
    Torznab = 0,

    /// <summary>Via un Prowlarr dans lequel C411 est configuré.</summary>
    Prowlarr = 1,
}
