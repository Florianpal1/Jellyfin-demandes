using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Autres titres d'un média (ex. titre anglais), pour reconnaître et rechercher ses releases.</summary>
public interface ITitleAliasProvider
{
    Task<IReadOnlyList<string>> GetAlternateNamesAsync(TorrentSearchRequest request, CancellationToken ct);
}
