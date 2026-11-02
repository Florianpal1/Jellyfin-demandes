using Jellyfin.Plugin.C411Torbox.Configuration;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Accès à la configuration courante (abstrait pour les tests).</summary>
public interface IConfigProvider
{
    PluginConfiguration Current { get; }
}

internal sealed class PluginConfigProvider : IConfigProvider
{
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
