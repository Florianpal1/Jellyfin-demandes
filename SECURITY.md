# Politique de sécurité

Plugin Jellyfin **C411 Torbox**, à usage privé.

## Versions suivies

Seule la **dernière version publiée** (release GitHub) reçoit des correctifs de sécurité.

| Version | Suivie |
|---|---|
| Dernière release | ✅ |
| Versions précédentes | ❌ (mettre à jour) |

Le plugin cible **Jellyfin 12.2** : les correctifs de sécurité du serveur lui-même relèvent du projet Jellyfin.

## Signaler une vulnérabilité

**Ne pas ouvrir d'issue publique** pour une faille de sécurité.

Le dépôt est privé : contacter directement le propriétaire du dépôt sur GitHub, ou ouvrir une issue avec le seul titre « Sécurité : demande de contact », sans détail.

À fournir :
- la version du plugin et de Jellyfin ;
- la description du problème et son impact (fuite de clé API, accès sans droits, écriture de fichiers hors bibliothèque…) ;
- les étapes pour le reproduire.

Ne jamais joindre de clé API (C411, Torbox, Prowlarr), de passkey, de lien magnet privé ni d'URL de webhook Discord : les remplacer par `***`.

Délais visés : accusé de réception sous 7 jours, correctif ou contournement sous 30 jours selon la gravité.

## Périmètre

Concerne le code de ce dépôt :
- les endpoints `C411Torbox/*` (contrôle des droits utilisateur / administrateur) ;
- la page autonome `/C411Torbox/ui` et la page de configuration ;
- la gestion des secrets (clés API, liens de téléchargement) ;
- le téléchargement et le rangement des fichiers sur le NAS (depuis la 0.3.0).

Hors périmètre : Jellyfin lui-même, C411, Torbox, Prowlarr, Discord, la configuration réseau / DNS du NAS.

## Mesures en place

- **Secrets** : clés API stockées dans la configuration du plugin (côté serveur, réservée aux administrateurs) ; liens magnet et de téléchargement jamais renvoyés au navigateur ; aucune clé écrite dans les journaux par le plugin.
- **Droits** : endpoints d'administration protégés par la politique Jellyfin `RequiresElevation` ; un utilisateur ne voit que ses propres demandes.
- **HTTPS** : certificat de C411 toujours vérifié ; l'option « certificat auto-signé » ne concerne que Prowlarr sur le réseau local.
- **Fichiers** (rangement automatique) : seuls les vidéos et sous-titres sont copiés (jamais d'exécutable ni d'archive) ; noms nettoyés et destinations confinées au dossier du média ; fichiers incomplets écrits en `.c411part` ; espace disque vérifié avant téléchargement.
- **Dépendances** : alertes et mises à jour Dependabot (`.github/dependabot.yml`). `Jellyfin.Controller`, `Jellyfin.Model` et `Microsoft.Data.Sqlite` restent épinglés sur les versions fournies par le serveur ; seuls les correctifs (patch) sont proposés automatiquement.
