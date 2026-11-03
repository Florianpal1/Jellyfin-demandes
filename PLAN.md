# Plan — Plugin Jellyfin « C411 → Torbox »

> Statut : v0.1.0 développée, à tester sur le serveur réel (voir § 6) — plan révisé le 2026-11-02 après réponses aux questions Q1 à Q14
> Objectif : permettre aux utilisateurs Jellyfin de chercher un film ou une série, de choisir un torrent C411 (via Prowlarr), et de faire une demande. Les administrateurs valident les demandes, avec validation automatique selon des règles configurables. Les demandes validées sont envoyées directement à Torbox.
> Usage : privé, interface en français.

---

## 1. Vue d'ensemble

```
┌──────────────┐  recherche   ┌───────────────────────────────┐  API v1   ┌──────────┐      ┌──────┐
│ Utilisateur  │ ───────────▶ │  Plugin Jellyfin 12.2 (.NET10) │ ────────▶ │ Prowlarr │ ───▶ │ C411 │
│ (page web    │ ◀─────────── │                                │           └──────────┘      └──────┘
│  autonome)   │  résultats   │  - Recherche métadonnées       │
└──────────────┘              │    (fournisseurs Jellyfin)     │ checkcached ┌────────┐
       │ demande              │  - SearchService               │ ──────────▶ │ Torbox │
       ▼                      │  - RequestService (+ règles)   │createtorrent└───┬────┘
┌──────────────┐  valider /   │  - SQLite (demandes)           │                 │ télécharge
│ Admin        │  refuser     │  - Notifications               │                 ▼
└──────────────┘ ───────────▶ │  - Détection arrivée en biblio │ ◀── scan ── App « RealDebrid »
                              └───────────────────────────────┘  (hors périmètre : récupère les
                                                                   torrents terminés sur Torbox)
```

### Parcours utilisateur
1. **Recherche du média** : l'utilisateur tape un titre. Le plugin interroge les fournisseurs de métadonnées de Jellyfin (TMDB) et affiche des fiches avec affiche, année et synopsis.
2. **Choix du média** : film, ou série puis saison (pack) ou épisode précis.
3. **Déjà présent ?** Le plugin vérifie dans la bibliothèque Jellyfin (par identifiant TMDB/IMDb/TVDB). S'il est déjà là : badge « Déjà dans la bibliothèque » et demande bloquée.
4. **Recherche des torrents** : le plugin interroge C411 via Prowlarr (par identifiant si possible, sinon par titre + année / SxxEyy).
5. **Vérification Torbox** : un seul appel groupé `checkcached` sur les infohash des résultats.
6. **Affichage trié** : d'abord les torrents **en cache Torbox**, puis par **nombre de seeders** décroissant. Colonnes : titre, taille, seeders, leechers, badge « ⚡ Instantané (Torbox) ».
7. **Demande** → règles d'auto-validation (§ 2.5) → soit envoi immédiat à Torbox, soit file d'attente admin.
8. **Admin** valide (envoi Torbox) ou refuse (motif).
9. **Suivi** : statut Torbox lu périodiquement ; quand le média apparaît dans la bibliothèque Jellyfin, la demande passe à « Disponible » et l'utilisateur est notifié.

### Hors périmètre
- ~~Le téléchargement est fait par **Torbox** ; la récupération des fichiers terminés et leur rangement dans la bibliothèque sont faits par l'application **« RealDebrid »** existante.~~ Révisé le 2026-11-03 (v0.3.0) : voir § 7, le plugin peut désormais télécharger et ranger lui-même.
- Toujours hors périmètre : suivi automatique des séries (nouveaux épisodes à leur sortie), remplacement par une meilleure qualité, torrents ajoutés à Torbox en dehors du plugin.

---

## 2. Architecture technique

### 2.1 Cible
- **Jellyfin 12.2** ([release v12.2](https://github.com/jellyfin/jellyfin/releases/tag/v12.2)).
- **C# / .NET 10** (`net10.0`, framework déclaré par le package NuGet `Jellyfin.Controller` 12.2.0). ⚠️ Installer le **SDK .NET 10** sur le poste (seuls 6.0 et 9.0 sont présents).
- **Packages NuGet** `Jellyfin.Controller` / `Jellyfin.Model` épinglés en **12.2.0** (les 12.3.0 et 12.4.0 existent déjà : ne pas les utiliser tant que le serveur est en 12.2).
- Point de départ : template officiel `jellyfin-plugin-template`, à vérifier/adapter aux API de la 12.2 dès l'étape 1.
- **SQLite** via `Microsoft.Data.Sqlite`, **déjà embarqué par Jellyfin** : référencer la même version que le serveur et ne pas copier la DLL avec le plugin (risque de conflit de chargement).

### 2.2 Structure du projet
```
Jellyfin.Plugin.C411Torbox/
├── Plugin.cs                         # BasePlugin<PluginConfiguration>, IHasWebPages
├── PluginServiceRegistrator.cs       # injection de dépendances
├── Configuration/
│   ├── PluginConfiguration.cs
│   └── configPage.html/.js           # page de config admin (Tableau de bord > Extensions)
├── Api/
│   ├── UiController.cs               # GET /demandes/ui → page utilisateur (ressources embarquées)
│   ├── MediaController.cs            # recherche métadonnées + état « déjà en bibliothèque »
│   ├── SearchController.cs           # recherche torrents pour un média donné
│   ├── RequestsController.cs         # créer une demande, mes demandes, mes notifications
│   └── AdminController.cs            # validation / refus / renvoi / historique (RequiresElevation)
├── Services/
│   ├── MetadataSearchService.cs      # IProviderManager (TMDB via Jellyfin) : titres, affiches, saisons
│   ├── LibraryLookupService.cs       # ILibraryManager : présence par ProviderIds, saisons/épisodes
│   ├── ProwlarrClient.cs             # API Prowlarr v1 (search, téléchargement .torrent)
│   ├── TorrentHashHelper.cs          # calcul infohash depuis un .torrent (bencode) si absent
│   ├── TorboxClient.cs               # checkcached, createtorrent, mylist
│   ├── SearchService.cs              # fusion Prowlarr + cache Torbox + tri
│   ├── ApprovalRules.cs              # règles d'auto-validation (testables isolément)
│   ├── RequestService.cs             # machine d'états, dédoublonnage
│   └── NotificationService.cs        # notifications in-app (+ webhook optionnel)
├── Data/
│   └── RequestRepository.cs          # SQLite : demandes, notifications, migrations de schéma
├── Tasks/
│   └── TorboxStatusTask.cs           # IScheduledTask : statut Torbox (lecture seule)
├── Events/
│   └── LibraryWatcher.cs             # ILibraryManager.ItemAdded → demande « Disponible »
├── Models/                           # DTO
└── Web/                              # ressources embarquées (FR)
    ├── app.html / app.js / app.css   # UI utilisateur : recherche, mes demandes, notifications
    └── admin.html / admin.js         # UI admin (aussi dans le tableau de bord)
```

### 2.3 Configuration (admin)
| Paramètre | Type | Défaut |
|---|---|---|
| URL Prowlarr | string | ex. `http://prowlarr:9696` |
| Clé API Prowlarr | string (secret) | — |
| Indexeur C411 dans Prowlarr | sélection (liste chargée depuis Prowlarr) | — |
| Clé API Torbox | string (secret) | — |
| Seuil d'auto-validation **films** | Go | `15` (0 = désactivé) |
| Seuil d'auto-validation **séries** (épisode) | Go | `5` |
| Seuil d'auto-validation **séries** (pack de saison) | Go | `30` |
| Seeders minimum pour l'auto-validation | int | `5` |
| Auto-validation si déjà en cache Torbox | bool | `true` |
| Catégories Prowlarr films / séries | liste d'ID | `2000` / `5000` |
| Limite de calcul des infohash (si Prowlarr ne les fournit pas) | int | `15` |
| Webhook de notification (Discord, optionnel) | URL | vide |

> Les clés API ne sont **jamais** renvoyées au navigateur : tous les appels Prowlarr/Torbox passent par le serveur.
> Pas de quota ni de restriction d'utilisateurs (réponse Q8) : tout utilisateur Jellyfin connecté peut faire des demandes.

### 2.4 Modèle de données (SQLite, fichier dans le dossier de données du plugin)
```
Requests
  Id, UserId, UserName, CreatedAt
  MediaType (Film | Série), MediaTitle, Year, TmdbId, ImdbId, TvdbId, PosterUrl
  Season (null si film), Episode (null si pack ou film)
  TorrentTitle, SizeBytes, Seeders, Leechers, InfoHash, ProwlarrGuid, IndexerId
  WasCachedOnTorbox, AutoApprovalReason (Cache | Taille | null)
  Status : Pending | Approved | AutoApproved | Rejected | SentToTorbox
           | DownloadingOnTorbox | CompletedOnTorbox | Available | Failed
  ReviewedBy, ReviewedAt, RejectReason
  TorboxTorrentId, TorboxProgress, LastError, UpdatedAt

Notifications
  Id, UserId (null = tous les admins), RequestId, Message, CreatedAt, ReadAt
```
- Dédoublonnage par `InfoHash` : une 2ᵉ demande du même torrent est rattachée à la première (les deux demandeurs sont notifiés).
- Versionnage simple du schéma (table `SchemaVersion`) pour les futures migrations.

### 2.5 Règles d'auto-validation (réponse Q7)
Évaluées dans cet ordre, côté serveur, avec des données relues côté serveur (jamais celles envoyées par le navigateur) :
1. Torrent **déjà en cache Torbox** et **taille ≤ seuil** du type (seuil à 0 : pas de borne) → **auto-validé** (quels que soient les seeders), motif « Cache ».
2. Sinon, si **taille ≤ seuil** du type (film / épisode / pack de saison) **et seeders ≥ minimum** → **auto-validé**, motif « Taille ».
3. Sinon → **En attente** de validation admin.

Couvert par des tests unitaires sur toutes les combinaisons (cache × taille × seeders × type).

### 2.6 Recherche de métadonnées (réponse Q6)
- Via l'`IProviderManager` de Jellyfin (`GetRemoteSearchResults` pour `Movie` / `Series`), qui utilise le fournisseur TMDB déjà configuré sur le serveur → **pas de clé TMDB supplémentaire**. Résultats : titre, année, affiche, synopsis, `ProviderIds` (Tmdb, Imdb, Tvdb).
- Liste des saisons/épisodes d'une série : via les fournisseurs Jellyfin si exposé ; sinon, saisie manuelle « Saison N / Épisode M » (à confirmer à l'étape 4, voir § 4).

### 2.7 Intégration C411 via Prowlarr (réponse Q2-bis)
Utilisation de l'**API JSON native de Prowlarr** (plus simple que le flux Torznab XML, et donne directement seeders **et** leechers) :
- `GET /api/v1/indexer` → liste des indexeurs (choix de C411 dans la config, test de connexion).
- `GET /api/v1/search?query=…&type=movie|tvsearch|search&indexerIds=<C411>&categories=…` avec en-tête `X-Api-Key`.
  - Films : `type=movie`, requête par identifiant (`{ImdbId:tt…}` / `{TmdbId:…}`) si C411 le supporte dans Prowlarr, sinon titre + année.
  - Séries : `type=tvsearch` avec `{Season:NN}` / `{Episode:NN}` (ou `{TvdbId:…}`), sinon « Titre S01E03 » / « Titre S01 ».
- Champs utilisés : `title`, `size`, `seeders`, `leechers`, `infoHash`, `magnetUrl`, `downloadUrl`, `guid`, `publishDate`, `categories`.
- Envoi à Torbox : le `.torrent` est téléchargé via le `downloadUrl` (proxifié par Prowlarr), puis envoyé à Torbox en multipart ; `magnetUrl` si disponible.
- ⚠️ **Si Prowlarr ne fournit pas l'`infoHash`** pour C411 : il faut télécharger chaque `.torrent` pour calculer le hash avant `checkcached`. Cela peut compter comme des « téléchargements » sur C411 pour chaque résultat affiché. Dans ce cas : limiter le calcul aux N premiers résultats (ex. 15) et le mettre en cache. À vérifier à l'étape 4.
- Cache court des recherches (5 min) pour limiter la charge sur Prowlarr/C411.

### 2.8 Intégration Torbox (API v1, `https://api.torbox.app/v1/api`, en-tête `Authorization: Bearer <clé>`)
- `GET /torrents/checkcached?hash=h1,h2,…&format=object` → disponibilité instantanée (appel groupé, cache mémoire quelques minutes).
- `POST /torrents/createtorrent` (multipart : `file` .torrent **ou** `magnet`, `name`) → ajout. **C'est la seule action d'écriture du plugin sur Torbox.**
- `GET /torrents/mylist` → lecture de la progression pour afficher le statut.
- Erreurs : en cas d'échec (limite du plan, 429, réseau), la demande passe en `Failed` avec le message, et l'admin peut la **renvoyer** manuellement. Pas de file d'attente automatique (réponse Q5).
- Endpoints et paramètres à confirmer sur la documentation Torbox à l'étape 3.

### 2.9 Présence dans la bibliothèque (réponse Q10)
- `ILibraryManager` : recherche d'éléments par `ProviderIds` (Tmdb/Imdb/Tvdb), fiable puisqu'on part d'une fiche de métadonnées et non d'un titre de torrent.
- Films : présent → demande bloquée.
- Séries : vérification au niveau saison/épisode ; une saison partiellement présente reste demandable (épisodes manquants signalés).
- Le même mécanisme, branché sur l'événement `ItemAdded`, fait passer une demande en **« Disponible »** quand l'application « RealDebrid » a déposé les fichiers et que Jellyfin les a indexés.

### 2.10 Notifications (réponse Q11)
| Événement | Destinataire |
|---|---|
| Nouvelle demande en attente | Admins |
| Demande auto-validée | Admins (pour information, dans l'historique) |
| Demande validée / refusée (avec motif) | Demandeur |
| Échec d'envoi à Torbox | Admins + demandeur |
| Média disponible dans Jellyfin | Demandeur(s) |

Canaux : **notifications dans la page du plugin** (cloche avec compteur, stockées en SQLite) + **webhook Discord optionnel**. (Jellyfin n'a plus de système de notifications intégré depuis la 10.9 ; à revérifier sur la 12.2.)

### 2.11 Interfaces web (FR)
- **Page utilisateur** `/demandes/ui` (page autonome, réponse Q3) : réutilise le jeton de session Jellyfin du navigateur ; si absent, redirection vers la connexion Jellyfin puis retour. Onglets : Recherche, Mes demandes, Notifications.
- **Page admin** (dans le tableau de bord via `IHasWebPages`, et onglet de la page autonome pour les admins) : demandes en attente (média, torrent, taille, seeders, cache Torbox, demandeur), Valider / Refuser (motif) / Renvoyer, historique complet filtrable (y compris auto-validées).
- Non disponible dans les applis natives (TV, mobile) : navigateur uniquement (accepté).

### 2.12 Sécurité
- Endpoints utilisateur : `[Authorize]` ; endpoints admin : `[Authorize(Policy = "RequiresElevation")]`.
- La demande référence un résultat de recherche mis en cache **côté serveur** (taille, seeders, hash, cache Torbox relus côté serveur) : le navigateur ne peut pas tricher pour passer sous le seuil.
- Clés API jamais exposées ; journalisation des actions admin.

---

## 3. Découpage en étapes

| # | Étape | Critère de validation |
|---|---|---|
| 1 | SDK .NET 10 ; squelette du plugin en `net10.0` contre `Jellyfin.Controller` 12.2.0 ; installation manuelle | Le plugin apparaît dans Tableau de bord > Extensions |
| 2 | Configuration + page admin de config + boutons « Tester » (Prowlarr : liste des indexeurs ; Torbox) | Clés et seuils enregistrés, connexions testées |
| 3 | `TorboxClient` + tests (HTTP mocké) | Ajout d'un torrent de test réussi depuis un endpoint de debug |
| 4 | `ProwlarrClient` + `TorrentHashHelper` + tests sur réponses enregistrées ; vérifier la présence de `infoHash` et les recherches par ID pour C411 | Taille / seeders / leechers / hash corrects |
| 5 | `MetadataSearchService` + `LibraryLookupService` | Recherche « Dune » → fiches avec affiches ; film présent détecté |
| 6 | `SearchService` (fusion + cache Torbox + tri) | Tri vérifié par tests : cache d'abord, puis seeders |
| 7 | SQLite + `RequestRepository` + `ApprovalRules` + `RequestService` | Tests des règles (§ 2.5) et du dédoublonnage |
| 8 | Page utilisateur (recherche, choix saison/épisode, demandes) | Parcours complet dans le navigateur |
| 9 | Page admin (validation, refus, renvoi, historique) | Validation → torrent présent sur Torbox |
| 10 | `TorboxStatusTask` + `LibraryWatcher` | Statut jusqu'à « Disponible » après passage de l'app « RealDebrid » |
| 11 | Notifications in-app + webhook | Chaque événement du § 2.10 génère sa notification |
| 12 | Script de build/déploiement (copie de la DLL dans `plugins/` du serveur) + README d'installation | Installation sur le serveur réel en une commande |

---

## 4. Points d'interrogation

### ✅ Décisions prises

| # | Question | Décision |
|---|---|---|
| Q1 | Version Jellyfin | **12.2** → NuGet `12.2.0`, `net10.0`. |
| Q2 / Q2-bis | Accès à C411 | Via **Prowlarr** (déjà utilisé par Radarr/Sonarr), API JSON v1. |
| Q3 | UI utilisateur | Page web autonome servie par le plugin. |
| Q4 / Q4-bis / Q4-ter | Fichiers | Torbox télécharge ; l'app « RealDebrid » récupère et range. **Rien à gérer côté plugin.** |
| Q5 | Limites Torbox | Pas de file d'attente : le plugin envoie seulement à Torbox ; en cas d'échec, renvoi manuel. |
| Q6 | Recherche | **Tout** : films, séries par saison (packs) et épisode, fiches avec affiche/synopsis. |
| Q7 | Auto-validation | Si en cache Torbox ; sinon seuils de taille distincts films/séries **et** seeders minimum. |
| Q8 | Abus | Aucun quota, aucune restriction d'utilisateurs. |
| Q9 | Tri | En cache Torbox d'abord, puis seeders décroissants. Pas de score ni de filtres. |
| Q10 | Déjà en bibliothèque | Oui : badge + demande bloquée. |
| Q11 | Notifications | Oui (in-app + webhook Discord optionnel). |
| Q12 | Stockage | SQLite. |
| Q13 | Langue | Français. |
| Q14 | Distribution | Usage privé, installation manuelle. |

### 🔎 À vérifier pendant le développement (non bloquant)
- **Prowlarr / C411 (étape 4)** : l'indexeur renvoie-t-il l'`infoHash` ? Supporte-t-il la recherche par ID IMDb/TMDB/TVDB et par saison/épisode ? Sinon : calcul du hash sur les N premiers résultats et recherche par titre.
- **Saisons/épisodes (étape 5)** : les fournisseurs de métadonnées de Jellyfin 12.2 exposent-ils la liste des épisodes d'une série non présente en bibliothèque ? Sinon, saisie manuelle du numéro de saison/épisode.
- **Notifications (étape 11)** : canal par défaut = in-app. Faut-il le webhook Discord, ou autre chose ?
- **Seuils par défaut** (§ 2.3) : valeurs proposées, modifiables dans la config.
- **Endpoints Torbox (étape 3)** : à valider sur la documentation officielle.

---

## 5. Risques identifiés

| Risque | Impact | Mitigation |
|---|---|---|
| `infoHash` absent dans Prowlarr pour C411 | Moyen | Calcul depuis le `.torrent` sur les N premiers résultats, avec cache |
| Recherche par ID non supportée par C411 → résultats moins précis | Moyen | Repli titre + année / SxxEyy |
| Conflit de DLL SQLite avec Jellyfin | Moyen | Référencer la version de Jellyfin, ne pas embarquer la DLL |
| API plugin changeante entre 12.2, 12.3, 12.4… | Moyen | Packages épinglés en 12.2.0 ; recompiler à chaque mise à jour du serveur |
| Échec d'envoi Torbox (limite du plan) | Faible | Statut « Échec » + renvoi manuel par l'admin |
| UI absente des applis natives | Faible | Accepté |
| Fuite des clés API | Élevé | Clés uniquement côté serveur |

---

## 6. État d'avancement (2026-11-02)

Version **0.1.0** écrite : étapes 1 à 12 implémentées, compilées contre Jellyfin 12.2.0, **47 tests automatisés au vert** (règles d'auto-validation, tri, calcul d'infohash, parsing Prowlarr/Torbox, scénarios complets demande → validation → Torbox → disponible avec SQLite réel).

Écarts par rapport au plan :
- L'intervalle de suivi Torbox ne figure pas dans la config du plugin : il se règle dans *Tâches planifiées* (mécanisme standard de Jellyfin).
- « Renvoyer » une demande en échec = la valider à nouveau (même bouton côté admin).
- Choix saison/épisode : saisie du numéro (pas de liste d'épisodes).

**Non vérifié à ce stade** (nécessite le serveur réel) :
1. Chargement du plugin dans Jellyfin 12.2 et accès à SQLite fourni par le serveur.
2. Format réel des réponses Prowlarr pour C411 (présence de `infoHash`, recherche par identifiant).
3. Réponses réelles de Torbox (`checkcached`, `createtorrent`, `mylist`) : endpoints validés sur leur spécification OpenAPI, formats de réponse non documentés et parsés de façon tolérante.
4. Réutilisation de la session du client web Jellyfin par la page autonome (sinon formulaire de connexion).
5. Affichage de la page admin dans le tableau de bord (iframe : peut être bloqué si un reverse proxy envoie `X-Frame-Options: DENY`).

Prochaine étape : installer sur le serveur (`scripts/package.ps1`), configurer, et faire un premier test avec un petit torrent.

## 7. Téléchargement et rangement automatiques (v0.3.0, 2026-11-03)

Objectif : remplacer l'application « RealDebrid » + Radarr / Sonarr par le plugin, pour les demandes faites dans le plugin.

Décisions (réponses du 2026-11-03) :
- Fichiers **téléchargés sur le NAS** (pas de `.strm`) depuis les liens directs Torbox (`torrents/requestdl`).
- **Demandes uniquement** : pas de suivi des séries ni de remplacement de qualité.
- Seuls les torrents des demandes du plugin sont rangés ; le média est connu par la fiche TMDB de la demande (pas de devinette sur le nom du torrent).
- Nommage **standard Radarr / Sonarr**, avec `[tmdbid-…]` dans le nom de dossier.

Fonctionnement :
- Nouveaux statuts : `Importing` (téléchargement vers le NAS, avec progression), `Imported` (rangé, en attente du scan Jellyfin), `ImportFailed` (raison + bouton « Relancer »). `Imported` → `Available` par la détection existante.
- `ImportWorker` (tâche de fond) : toutes les 30 s, traite la plus ancienne demande `CompletedOnTorbox` ; consulte Torbox toutes les 2 min tant qu'une demande y est en cours. Au démarrage, un import interrompu repart (reprise HTTP `Range` du fichier `.c411part`).
- `ImportPlanner` (logique pure, testée) choisit les fichiers et calcule les noms ; `ReleaseParser` lit SxxEyy / 1x02 / qualité / langue des sous-titres.
- Série déjà dans Jellyfin : rangement dans son dossier existant (et dossier de saison existant).
- Garde-fous : option désactivée par défaut (pour ne pas doublonner l'application « RealDebrid »), espace disque vérifié, destinations confinées au dossier du média, seuls vidéos et sous-titres copiés.
- Schéma SQLite v2 : colonnes `ImportProgress`, `ImportedPath` (migration automatique depuis la v1).

**Non vérifié à ce stade** (nécessite le serveur réel) :
1. Réponse réelle de `torrents/mylist?id=…` (liste `files` avec `id`, `name`, `size`) et de `torrents/requestdl`.
2. Reprise `Range` sur les liens CDN de Torbox (sans reprise, le fichier repart de zéro, ce qui reste correct).
3. Scan ciblé déclenché par `ILibraryMonitor.ReportFileSystemChanged` sur le QNAP (sinon, le scan planifié de Jellyfin trouve les fichiers).
4. Titres d'épisodes via le fournisseur TMDB de Jellyfin.

## 8. Livres, BD, comics et presse (v0.4.0, issue #16, 2026-11-03)

Objectif : demander des livres comme les films et les séries.

- Nouveau type `Book` (portée `Book`) : un seul type pour livres, BD, comics, mangas et presse, C411 les rangeant dans des catégories voisines (configurables, défaut `7000,7010,7020,7030`).
- Fiches : `IProviderManager.GetRemoteSearchResults<Book, BookInfo>`, alimenté par l'extension Jellyfin **Bookshelf** (Google Books, Comic Vine) si elle est installée. Une fiche « recherche libre » (texte saisi tel quel) est toujours proposée : presse et éditions absentes des fournisseurs.
- Recherche C411 : par titre seul (`t=search`), sans l'année (rarement présente dans les noms de releases de livres).
- Déjà en bibliothèque : uniquement par identifiant (GoogleBooks / ComicVine / Isbn), pour ne pas bloquer à tort un titre homonyme. Disponibilité : identifiant, ou livre ajouté depuis la demande dans le dossier rangé ou portant le même titre.
- Auto-validation : seuil `BookMaxSizeGb` (2 Go par défaut).
- Rangement : `Livres/Titre (Année)/Titre (Année).ext` pour un fichier seul ; un pack garde les noms de fichiers, à plat. Dossier `BooksPath` (vide : bibliothèque Jellyfin de type Livres) ; facultatif.
- Schéma SQLite v3 : colonne `BookIds` (« GoogleBooks=…;Isbn=… »).

**Non vérifié à ce stade** : catégories réelles de C411 pour les livres (`t=caps`), clés exactes des identifiants posés par Bookshelf sur le serveur.
