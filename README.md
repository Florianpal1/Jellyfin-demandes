# Jellyfin-C411-Torbox

Plugin Jellyfin **12.2** (usage privé) :

- **Utilisateurs** : recherche d'un film ou d'une série (fiches TMDB via Jellyfin), choix d'un torrent C411 (via Prowlarr) avec taille, seeders et leechers. Les torrents **déjà en cache Torbox** sont affichés en premier (⚡), puis les plus seedés. Demande en un clic.
- **Administrateurs** : validation / refus des demandes. Validation **automatique** si le torrent est en cache Torbox, ou s'il est sous le seuil de taille (film / épisode / saison) avec assez de seeders.
- **Torbox** : les demandes validées y sont ajoutées directement. La récupération des fichiers reste assurée par l'application « RealDebrid » existante.
- Suivi : progression Torbox, passage en « Disponible » quand le média arrive dans Jellyfin, notifications (dans la page + webhook Discord optionnel).

Le plan détaillé et les décisions sont dans [PLAN.md](PLAN.md).

## Installation

Prérequis de compilation : SDK .NET 10.

```powershell
./scripts/package.ps1
# ou, pour copier directement dans le dossier plugins du serveur :
./scripts/package.ps1 -PluginsPath "<dossier plugins de Jellyfin>"
```

Le script lance les tests, compile, puis produit `artifacts/C411 Torbox_0.1.0.0/` (DLL + `meta.json`) et une archive zip.

Copier le dossier `C411 Torbox_0.1.0.0` dans le dossier `plugins` de Jellyfin puis **redémarrer Jellyfin** :

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

1. **Prowlarr** : URL (vue depuis le serveur Jellyfin) et clé API, puis « Enregistrer et tester Prowlarr » → choisir l'indexeur **C411** dans la liste.
2. **Torbox** : clé API, puis « Enregistrer et tester Torbox ».
3. **Validation automatique** : seuils en Go (0 = désactivé) et nombre minimum de seeders.
4. **Notifications** : webhook Discord facultatif.

L'intervalle de suivi Torbox (10 min par défaut) se règle dans Tableau de bord → Tâches planifiées → « Suivi des demandes Torbox ».

## Utilisation

- **Utilisateurs** : `https://<votre-jellyfin>/C411Torbox/ui` (le lien exact est affiché dans la page de configuration). La page reprend la session du client web Jellyfin ; sinon elle propose de se connecter avec le compte Jellyfin. Navigateur uniquement (pas dans les applis TV / mobiles).
- **Administrateurs** : onglet « Administration » de la même page, ou menu **Demandes C411** du tableau de bord.

## Développement

```powershell
dotnet build
dotnet test
```

- `src/Jellyfin.Plugin.C411Torbox` : le plugin (API, services, pages web embarquées).
- `tests/Jellyfin.Plugin.C411Torbox.Tests` : tests unitaires et scénarios complets (SQLite réel, Prowlarr/Torbox simulés).

Les packages `Jellyfin.Controller` / `Jellyfin.Model` sont épinglés en **12.2.0** et `Microsoft.Data.Sqlite` en **10.0.11** (version embarquée par Jellyfin 12.2). Ces assemblies ne sont pas copiées avec le plugin : elles sont fournies par le serveur.
