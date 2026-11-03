using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.C411Torbox.Services.Import;

/// <summary>
/// Téléchargement HTTP d'un fichier vers le disque, avec reprise.
/// Le fichier est écrit en « .c411part » (ignoré par Jellyfin), puis renommé une fois complet :
/// la bibliothèque ne voit jamais de fichier à moitié téléchargé.
/// </summary>
public class FileDownloader
{
    public const string HttpClientName = "C411Torbox.Download";

    public const string PartialExtension = ".c411part";

    private const int BufferSize = 1 << 20;

    private readonly IHttpClientFactory _httpClientFactory;

    public FileDownloader(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Télécharge <paramref name="url"/> vers <paramref name="destination"/> ; <paramref name="onBytes"/> reçoit les octets écrits.</summary>
    public virtual async Task DownloadAsync(Uri url, string destination, long expectedSize, Action<long> onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + PartialExtension;
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expectedSize > 0 && existing > expectedSize)
        {
            File.Delete(part);
            existing = 0;
        }

        if (expectedSize > 0 && existing == expectedSize)
        {
            onBytes(existing);
            File.Move(part, destination, overwrite: false);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ImportException("Téléchargement depuis Torbox impossible : " + NetworkErrors.Describe(ex), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ImportException(FormattableString.Invariant($"Torbox a refusé le téléchargement (HTTP {(int)response.StatusCode})."));
            }

            // Le serveur peut ignorer la reprise et renvoyer tout le fichier (200) : on repart de zéro.
            bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append)
            {
                existing = 0;
            }

            onBytes(existing);
            long written = existing;
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                        onBytes(written);
                    }

                    await file.FlushAsync(ct).ConfigureAwait(false);
                }
            }

            if (expectedSize > 0 && written != expectedSize)
            {
                throw new ImportException(FormattableString.Invariant($"Téléchargement incomplet ({written} octets sur {expectedSize})."));
            }
        }

        File.Move(part, destination, overwrite: false);
    }
}
