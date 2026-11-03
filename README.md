# Jellyfin-C411-Torbox

Plugin Jellyfin **12.2** (usage privé) :

- **Utilisateurs** : recherche d'un film, d'une série (fiches TMDB via Jellyfin) ou, depuis la 0.4.0, d'un **livre, d'une BD, d'un comics ou d'un journal** (fiches Google Books / Comic Vine si l'extension Jellyfin *Bookshelf* est installée, sinon recherche du titre saisi), choix d'un torrent C411 (API Torznab de C411 en direct, ou via Prowlarr) avec taille, seeders et leechers. Les torrents **déjà en cache Torbox** sont affichés en premier (⚡), puis les plus seedés. Demande en un clic.
- **Administrateurs** : validation / refus des demandes. Validation **automatique** si le torrent est en cache Torbox et sous le seuil de taille de son type, ou s'il est sous le seuil de taille (film / épisode / saison / livre) avec assez de seeders.
- **Torbox** : les demandes validées y sont ajoutées directement.
- **Téléchargement et rangement automatiques** (option, depuis la 0.3.0) : remplace l'application « RealDebrid » + Radarr / Sonarr. Une fois le torrent terminé sur Torbox, le plugin télécharge les fichiers sur le NAS (avec reprise), puis les range au format standard Radarr / Sonarr. Le média est connu d'après la fiche TMDB de la demande ; seuls les numéros d'épisode sont lus dans les noms de fichiers.
- Suivi : progression Torbox, puis progression du téléchargement vers le NAS, passage en « Disponible » quand le média arrive dans Jellyfin, notifications (dans la page + webhook Discord optionnel).

Le plan détaillé et les décisions sont dans [PLAN.md](PLAN.md).

## Installation

Prérequis de compilation : SDK .NET 10.

```powershell
./scripts/package.ps1
# ou, pour copier directement dans le dossier plugins du serveur :
./scripts/package.ps1 -PluginsPath "<dossier plugins de Jellyfin>"
```

Le script lance les tests, compile, puis produit `artifacts/C411 Torbox_<version>/` (DLL + `meta.json`) et une archive zip.

Copier le dossier `C411 Torbox_<version>` dans le dossier `plugins` de Jellyfin puis **redémarrer Jellyfin** :

| Installation | Dossier plugins |
|---|---|
| Docker | `/config/plugins` |
| Linux (paquet) | `/var/lib/jellyfin/plugins` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins` |

### Releases GitHub

À chaque **release publiée** sur GitHub, le workflow `.github/workflows/release.yml` lance les tests, compile le plugin et attache à la release :

- `c411-torbox_<version>.zip` (DLL + `meta.json`, à décompresser dans le dossier plugins) ;
- `c411-torbox_<version>.zip.sha256`.

La version du plugin est tirée du tag : `v0.2.0` → `0.2.0.0`. Créer une release :

```powershell
gh release create v0.2.0 --generate-notes
```

Pour régénérer l'archive d'une release existante : onglet *Actions* → *Release* → *Run workflow* avec le tag.

## Configuration

Tableau de bord → Extensions → **C411 Torbox** :

1. **Source des torrents** : par défaut **C411 en direct** → URL de l'API (`https://c411.org/api`, celle indiquée par C411 pour Radarr / Sonarr) et clé API C411, puis « Enregistrer et tester C411 ». Option « Via Prowlarr » si C411 est configuré dans un Prowlarr : URL et clé API de Prowlarr, puis choisir l'indexeur C411 dans la liste.
   Le NAS doit pouvoir joindre `c411.org` : si le test indique un certificat dont le nom ne correspond pas, le DNS du NAS renvoie vers un autre serveur (réglage réseau du NAS).
2. **Torbox** : clé API, puis « Enregistrer et tester Torbox ».
3. **Téléchargement et rangement automatiques** : cocher l'option, indiquer les dossiers Films, Séries et (facultatif) Livres (chemins vus par le serveur Jellyfin ; vide = premier dossier de la bibliothèque Jellyfin du bon type), puis « Enregistrer et vérifier les dossiers » (existence et droit d'écriture).
4. **Validation automatique** : seuils en Go (0 = désactivé) et nombre minimum de seeders.
5. **Notifications** : webhook Discord facultatif.

### Rangement (format Radarr / Sonarr)

```
Films/Dune (2021) [tmdbid-438631]/Dune (2021) Bluray-1080p.mkv
Films/Dune (2021) [tmdbid-438631]/Dune (2021) Bluray-1080p.fr.srt
Séries/The Bear (2022) [tmdbid-136315]/Season 01/The Bear - S01E02 - Mains propres WEBDL-1080p.mkv
Livres/Astérix le Gaulois (1961)/Astérix le Gaulois (1961).cbz
```

- Fichiers retenus : la vidéo principale d'un film ; pour une série, l'épisode demandé ou tous les épisodes de la saison (S01E02, S01E02E03, 1x02…). Les extraits (*sample*), bonus, `.nfo`, exécutables, etc. sont ignorés. Les épisodes déjà présents dans Jellyfin ne sont pas retéléchargés.
- Livres : tous les fichiers `epub`, `pdf`, `mobi`, `azw3`, `fb2`, `djvu`, `cbz`, `cbr`, `cb7`, `cbt`. Un fichier seul est renommé d'après la fiche ; un pack (intégrale, collection, numéros de presse) garde le nom de chaque fichier, à plat dans le dossier du livre.
- Livres : catégories C411 configurables (par défaut 7000 Livres, 7010 Magazines, 7020 E-books, 7030 Comics). Un livre est « déjà dans la bibliothèque » seulement s'il est reconnu par son identifiant (Google Books, Comic Vine, ISBN) ; la demande passe en « Disponible » quand un livre de même titre, ou rangé dans son dossier, apparaît dans la bibliothèque Livres de Jellyfin.
- Une série déjà présente dans Jellyfin garde son dossier (et ses dossiers de saison) actuels, comme dans Sonarr.
- Titres d'épisodes : ils viennent des fournisseurs de métadonnées de Jellyfin (TMDB, en français). Sans titre, le fichier est nommé `The Bear - S01E02 WEBDL-1080p.mkv`.
- Le téléchargement se fait en `.c411part` (ignoré par Jellyfin), puis le fichier est renommé une fois complet : Jellyfin ne voit jamais de fichier incomplet. Un arrêt du serveur ou une coupure réseau reprend là où le téléchargement s'était arrêté.
- Une demande à la fois. L'espace libre est vérifié avant de commencer (taille du torrent + 1 Go de marge).
- En cas d'échec (dossier introuvable, disque plein, épisode absent du torrent…), la demande passe en « Échec du rangement » avec la raison, et les admins sont notifiés. Bouton **Relancer** dans l'onglet Administration.
- Option : supprimer le torrent de Torbox une fois rangé.

### Passer de l'application « RealDebrid » + Radarr / Sonarr au plugin

1. Dans l'application « RealDebrid », désactiver la récupération automatique des torrents Torbox (sinon chaque fichier est téléchargé deux fois).
2. Cocher l'option du plugin, vérifier les dossiers.
3. Radarr / Sonarr peuvent être arrêtés pour ce qui passe par le plugin. Les demandes déjà terminées sur Torbox sont rangées tout de suite, sauf si le média est déjà dans Jellyfin (passage direct en « Disponible »).

L'intervalle de suivi Torbox (10 min par défaut) se règle dans Tableau de bord → Tâches planifiées → « Suivi des demandes Torbox ». Quand le rangement automatique est activé, Torbox est en plus consulté toutes les 2 minutes tant qu'une demande y est en cours.

## Utilisation

- **Utilisateurs** : `https://<votre-jellyfin>/demandes/ui` (`/C411Torbox/ui` avant la 0.5.0) (le lien exact est affiché dans la page de configuration). La page reprend la session du client web Jellyfin ; sinon elle propose de se connecter avec le compte Jellyfin. Navigateur uniquement (pas dans les applis TV / mobiles).
- **Administrateurs** : onglet « Administration » de la même page, ou menu **Demandes** du tableau de bord.

## Développement

```powershell
dotnet build
dotnet test
```

- `src/Jellyfin.Plugin.C411Torbox` : le plugin (API, services, pages web embarquées).
- `tests/Jellyfin.Plugin.C411Torbox.Tests` : tests unitaires et scénarios complets (SQLite réel, C411/Prowlarr/Torbox simulés).

Les packages `Jellyfin.Controller` / `Jellyfin.Model` sont épinglés en **12.2.0** et `Microsoft.Data.Sqlite` en **10.0.11** exactement (version embarquée par Jellyfin 12.2, patch compris ; Dependabot ne propose que les correctifs de `Jellyfin.Controller` / `Jellyfin.Model` et ignore Sqlite). Ces assemblies ne sont pas copiées avec le plugin : elles sont fournies par le serveur.
