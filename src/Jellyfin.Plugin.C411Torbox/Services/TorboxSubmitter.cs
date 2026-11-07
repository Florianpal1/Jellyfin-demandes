using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.C411Torbox.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Torrent Torbox d'une demande envoyée : créé par le plugin, ou déjà présent sur le compte (<paramref name="Adopted"/>).</summary>
public sealed record TorboxSubmission(TorboxTorrent Torrent, string? Hash, bool Adopted)
{
    public bool Finished => Torrent.DownloadFinished || Torrent.Progress >= 1;
}

/// <summary>
/// Partie réseau de l'envoi d'une demande à Torbox : aucun accès à la base, aucun verrou.
/// <see cref="RequestService"/> l'appelle hors de son verrou puis enregistre le résultat.
/// </summary>
public class TorboxSubmitter
{
    private readonly IndexerProvider _indexers;
    private readonly TorboxClient _torbox;
    private readonly ILogger<TorboxSubmitter> _logger;

    public TorboxSubmitter(IndexerProvider indexers, TorboxClient torbox, ILogger<TorboxSubmitter> logger)
    {
        _indexers = indexers;
        _torbox = torbox;
        _logger = logger;
    }

    /// <summary>
    /// Ajoute le torrent de la demande sur Torbox, ou le rattache au torrent déjà présent (Torbox refuse les doublons).
    /// Lève une exception si l'envoi échoue.
    /// </summary>
    public async Task<TorboxSubmission> SubmitAsync(MediaRequest request, TorrentSource? source, CancellationToken ct)
    {
        // Torrent déjà présent sur le compte Torbox (ajouté à la main, hors plugin) : on le rattache au lieu de le renvoyer.
        var hash = request.InfoHash;
        var existing = await FindUsableAsync(hash, ct).ConfigureAwait(false);
        if (existing is null)
        {
            source ??= !string.IsNullOrEmpty(request.MagnetUrl)
                ? new TorrentSource(null, request.MagnetUrl)
                : !string.IsNullOrEmpty(request.DownloadUrl)
                    ? await _indexers.Current.DownloadAsync(request.DownloadUrl, ct).ConfigureAwait(false)
                    : throw new RequestException("Aucun lien de téléchargement pour ce torrent.");

            if (hash is null)
            {
                hash = source.File is not null ? TorrentHash.FromTorrentFile(source.File) : TorrentHash.FromMagnet(source.Magnet);
                existing = await FindUsableAsync(hash, ct).ConfigureAwait(false);
            }
        }

        if (existing is not null)
        {
            _logger.LogInformation("Demande {Id} : torrent déjà présent sur Torbox (id {TorrentId}), rattaché", request.Id, existing.Id);
            return new TorboxSubmission(existing, existing.Hash ?? hash, Adopted: true);
        }

        try
        {
            var created = await _torbox.CreateTorrentAsync(source!, request.TorrentTitle, ct).ConfigureAwait(false);
            hash = created.Hash ?? hash;
            return new TorboxSubmission(new TorboxTorrent(created.TorrentId ?? 0, hash, null, 0, null, false), hash, Adopted: false);
        }
        catch (TorboxException) when (hash is not null)
        {
            // Refus de Torbox : si c'est un doublon, le torrent figure dans la liste du compte.
            existing = await FindAsync(hash, ct).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            _logger.LogInformation("Demande {Id} : doublon refusé par Torbox, rattaché au torrent {TorrentId}", request.Id, existing.Id);
            return new TorboxSubmission(existing, existing.Hash ?? hash, Adopted: true);
        }
    }

    /// <summary>
    /// Comme <see cref="FindAsync"/>, mais un torrent en erreur sur Torbox est supprimé pour être ajouté à nouveau
    /// (c'est le sens de « Renvoyer » sur une demande passée en échec à cause de lui).
    /// </summary>
    private async Task<TorboxTorrent?> FindUsableAsync(string? hash, CancellationToken ct)
    {
        var existing = await FindAsync(hash, ct).ConfigureAwait(false);
        if (existing is null || !TorboxStates.IsError(existing.DownloadState))
        {
            return existing;
        }

        _logger.LogWarning("Torrent {TorrentId} en erreur sur Torbox ({State}) : supprimé puis ajouté à nouveau", existing.Id, existing.DownloadState);
        await _torbox.DeleteTorrentAsync(existing.Id, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Torrent du compte Torbox ayant cet infohash (null si absent, hash inconnu ou liste illisible).</summary>
    public async Task<TorboxTorrent?> FindAsync(string? hash, CancellationToken ct)
    {
        hash = TorrentHash.Normalize(hash);
        if (hash is null)
        {
            return null;
        }

        try
        {
            var list = await _torbox.GetMyListAsync(ct).ConfigureAwait(false);
            return list.FirstOrDefault(t => string.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase));
        }
        catch (TorboxException ex)
        {
            _logger.LogWarning(ex, "Liste des torrents Torbox illisible : vérification des doublons impossible");
            return null;
        }
    }
}
