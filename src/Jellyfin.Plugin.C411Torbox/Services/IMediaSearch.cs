using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Recherche de fiches médias (titre, ou identifiant « tmdb:438631 » / « imdb:tt1160419 » / « tvdb:81189 »).</summary>
public interface IMediaSearch
{
    Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, MediaKind kind, int? year, CancellationToken ct);
}
