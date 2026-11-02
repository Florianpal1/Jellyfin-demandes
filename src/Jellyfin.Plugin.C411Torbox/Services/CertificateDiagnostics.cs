using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>
/// Mémorise, par hôte, la raison du dernier refus de certificat HTTPS, pour l'afficher
/// dans le message d'erreur (le message .NET se contente de « rejected by the callback »).
/// </summary>
public class CertificateDiagnostics
{
    private readonly ConcurrentDictionary<string, string> _lastFailure = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string? host, string reason)
    {
        if (!string.IsNullOrEmpty(host))
        {
            _lastFailure[host] = reason;
        }
    }

    public string? Get(string? host)
        => !string.IsNullOrEmpty(host) && _lastFailure.TryGetValue(host, out var reason) ? reason : null;

    /// <summary>
    /// Contexte utile au diagnostic : noms couverts par le certificat reçu, émetteur,
    /// et adresses IP obtenues par le NAS pour l'hôte (pour repérer un DNS qui renvoie ailleurs).
    /// </summary>
    public static string DescribeContext(string? host, X509Certificate2? cert)
    {
        var parts = new List<string>();
        if (cert is not null)
        {
            var names = CertificateNames(cert);
            parts.Add("certificat reçu pour : " + (names.Count > 0 ? string.Join(", ", names) : cert.Subject));
            parts.Add("émetteur : " + cert.Issuer);
        }

        if (!string.IsNullOrEmpty(host))
        {
            try
            {
                var ips = System.Net.Dns.GetHostAddresses(host);
                parts.Add(host + " résolu par le NAS en : " + (ips.Length > 0 ? string.Join(", ", ips.Select(i => i.ToString())) : "aucune adresse"));
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                parts.Add("résolution DNS de " + host + " impossible : " + ex.Message);
            }
        }

        return parts.Count > 0 ? "[" + string.Join(" ; ", parts) + "]" : string.Empty;
    }

    internal static IReadOnlyList<string> CertificateNames(X509Certificate2 cert)
    {
        var names = cert.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            var cn = cert.GetNameInfo(X509NameType.DnsName, false);
            if (!string.IsNullOrEmpty(cn))
            {
                names.Add(cn);
            }
        }

        return names;
    }

    /// <summary>Explication en français d'un refus de certificat.</summary>
    public static string Explain(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags> chainStatus)
    {
        var reasons = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            reasons.Add("le serveur n'a envoyé aucun certificat");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            reasons.Add("le nom de l'URL ne correspond pas au certificat (le DNS du NAS renvoie peut-être vers un autre serveur que celui de votre navigateur)");
        }

        var flags = chainStatus.Aggregate(X509ChainStatusFlags.NoError, (acc, f) => acc | f);
        if (flags.HasFlag(X509ChainStatusFlags.PartialChain))
        {
            reasons.Add("chaîne incomplète : le serveur n'envoie pas le certificat intermédiaire (utiliser fullchain.pem), ou l'autorité est inconnue du NAS");
        }

        if (flags.HasFlag(X509ChainStatusFlags.UntrustedRoot))
        {
            reasons.Add("autorité racine inconnue du NAS (certificats racines du système trop anciens, ou certificat auto-signé)");
        }

        if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid))
        {
            reasons.Add("certificat expiré ou pas encore valide (vérifier aussi l'heure du NAS)");
        }

        if (flags.HasFlag(X509ChainStatusFlags.Revoked))
        {
            reasons.Add("certificat révoqué");
        }

        if (flags.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown) || flags.HasFlag(X509ChainStatusFlags.OfflineRevocation))
        {
            reasons.Add("impossible de vérifier la révocation du certificat (accès sortant du NAS vers l'autorité bloqué ?)");
        }

        if (reasons.Count == 0)
        {
            reasons.Add(errors.ToString() + (flags == X509ChainStatusFlags.NoError ? string.Empty : " / " + flags));
        }

        return string.Join(" ; ", reasons);
    }
}
