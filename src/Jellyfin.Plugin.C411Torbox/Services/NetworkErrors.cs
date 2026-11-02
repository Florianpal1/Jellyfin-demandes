using System;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Jellyfin.Plugin.C411Torbox.Services;

/// <summary>Traduit les erreurs réseau .NET (souvent « see inner exception ») en message compréhensible.</summary>
public static class NetworkErrors
{
    /// <param name="ex">Exception réseau.</param>
    /// <param name="certificateReason">Raison détaillée d'un refus de certificat, si connue (voir <see cref="CertificateDiagnostics"/>).</param>
    public static string Describe(Exception ex, string? certificateReason = null)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case AuthenticationException when certificateReason is not null:
                    return "certificat HTTPS refusé par le NAS : " + certificateReason + ".";
                case AuthenticationException auth:
                    return "connexion HTTPS refusée (" + auth.Message
                        + "). La raison détaillée est dans les journaux Jellyfin (ligne « Certificat HTTPS refusé »).";
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return "connexion refusée : vérifiez l'adresse et le port.";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return "nom d'hôte introuvable : vérifiez l'adresse.";
                case SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable }:
                    return "serveur injoignable : vérifiez l'adresse et le réseau.";
                case TimeoutException or OperationCanceledException:
                    return "pas de réponse dans le délai imparti.";
            }
        }

        var inner = ex.GetBaseException();
        return inner == ex ? ex.Message : ex.Message + " (" + inner.Message + ")";
    }
}
