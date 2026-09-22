# Contexte de reprise — projet Steam-SCE → plugin ASF

> Document écrit le 22/09/2026 pour permettre de reprendre le travail depuis
> n'importe quel compte/outil. Tout ce qui suit est auto-suffisant.

## 1. Objectif global

Convertir le projet **Steam-SCE-Scraper** (Node.js + PHP, propriété de DrNibble) en
**plugin ArchiSteamFarm (C#)**, par phases P0 à P7 décrites dans
[`docs/proposition-plugin-steam-sce.md`](proposition-plugin-steam-sce.md) (même branche).

## 2. Les deux dépôts et leur état

### DrNibble/Steam-SCE-Scraper (source, terminé pour l'instant)

- Scraper Node.js : badges/inventaire/historique Steam (`steam.js`), steamcardexchange.net
  (`sce.js`), marché Steam (`market.js` + `marketQueue.js` : file prioritaire + token bucket),
  cache SQLite `data/es_cache.sqlite` (`db.js`, node:sqlite), analyse (`analyze.js`),
  orchestration (`sync.js`), CLI (`index.js`).
- Frontend **PHP** (`php/index.php`) qui lit la même base en lecture seule
  (sections « Completables via SCE », « A deposer au Bot », « Badges Trade-In Desactive »).
- Config via `node/.env` (suivi dans le dépôt) et `node/.env.example`.
- Derniers travaux : `EVENT_APP_IDS` déplacé dans `.env` (variable séparée par virgules,
  lue dans `utils.js`) ; correctif `$depositList` (`ownedTotal`/`setCards` depuis
  `total_owned_qty`/`set_cards` de la table `games`).
- Branche `main` à jour, plus de travail en cours.

### DrNibble/ArchiSteamFarm (fork ASF, travail en cours)

- Fork de JustArchiNET/ArchiSteamFarm (net10.0, LangVersion preview, Nullable activé,
  `TreatWarningsAsErrors`, packages centralisés dans `Directory.Packages.props`).
- Branche de travail : **`proposal/steam-sce-custom-plugin`** (ne pas toucher `main`).
  - `a4a2bf3` — docs : proposition de conversion (fichier ci-dessus).
  - `19fac6d` — feat : socle P0 du plugin SteamSceScraper.

## 3. P0 livrée (état actuel du code)

Projet `ArchiSteamFarm.CustomPlugins.SteamSceScraper/` (4 fichiers, dans la solution
`ArchiSteamFarm.slnx`) :

| Fichier | Rôle |
|---|---|
| `ArchiSteamFarm.CustomPlugins.SteamSceScraper.csproj` | calqué sur PeriodicGC ; `ProjectReference` vers ASF `ExcludeAssets="all"` ; PostBuild copie vers `ArchiSteamFarm/bin/<cfg>/<tfm>/plugins/<AssemblyName>/` |
| `SteamSceScraperPlugin.cs` | `internal sealed class : IASF, IBot, IBotCommand2`, `[Export(typeof(IPlugin))]` (MEF) ; suit les bots dans un `ConcurrentDictionary<string, Bot>` (`OnBotInit`/`OnBotDestroy`) ; commande **`!sse status`** (EAccess.FamilySharing minimum, retourne `null` si commande non reconnue) |
| `SteamSceScraperConfig.cs` | config lue dans `OnASFInit` depuis les propriétés `SteamSceScraper*` de `config/ASF.json` : `Enabled`, `DatabasePath` (défaut `data/es_cache.sqlite`), `ConcurrencyLimit` (4), `InventoryPageDelay` (800 ms), `SyncHours` (6), `EventAppIds` (virgules) |
| `AssemblyInfo.cs` | `[assembly: CLSCompliant(false)]` |

Vérifié : `dotnet build` 0 warning/0 erreur, et ASF chargé avec le plugin
(« SteamSceScraperPlugin has been loaded successfully! »).

## 4. Prochaines étapes (P1 → P7, détails dans la proposition)

1. **P1 — Stockage** : `Storage/EsCacheDatabase.cs` avec `Microsoft.Data.Sqlite`
   (nouvelle dépendance : l'ajouter avec sa version dans `Directory.Packages.props`,
   sans `IncludeAssets="compile"`). Réutiliser le **même fichier et schéma**
   `data/es_cache.sqlite` (table `meta` avec version, `games`, `cards` ; timestamps en ms).
   Le PHP doit continuer de fonctionner sans changement.
2. **P2 — Scraping Steam** : `Steam/BadgeScraper.cs` via `bot.ArchiWebHandler.WebBrowser`
   (`UrlGetToHtmlDocument`, `UrlGetToJsonObject`) — remplace `steam.js` + `auth.js`.
3. **P3 — SCE + marché** : `Sce/SceScraper.cs` (cookie `PHPSESSID` en config) +
   `Market/MarketQueue.cs` (`System.Threading.Channels` + SemaphoreSlim) ;
   parsing HTML avec HtmlAgilityPack (autre nouvelle dépendance).
4. **P4 — Analyse + commandes** : `Analysis/BadgeAnalyzer.cs` + `SyncOrchestrator.cs`
   (timer à la PeriodicGC), commandes `!sse sync`, `!sse report`.
5. **P5 — API IPC** : `IPC/SteamSceController.cs : ArchiController`
   (`[Route("/Api/SteamSce")]`), JSON + Swagger (modèle SteamTokenDumper).
6. **P6 — Trade offers** : `ArchiWebHandler` n'expose PAS d'envoi (seulement
   Accept/Decline/Cancel/GetTradeOffers — vérifié) ; écrire l'appel direct
   `POST /trade/new` via le WebBrowser du bot (spike préalable recommandé).
7. **P7 — Rangement** : désactiver le repo Node.

## 5. Conventions et commandes utiles

- **Langue** : échanges, commits et doc en français (identifiants de code en anglais).
- **Identité git** pour les deux dépôts : `DrNibble <33020759+DrNibble@users.noreply.github.com>`
  (config locale déjà posée dans les clones ; utiliser `git -c user.name=... -c user.email=...`
  si besoin). L'auteur historique de Steam-SCE-Scraper utilisait
  `DrNibble <papa.telindus+020@gmail.com>`.
- **Build du plugin** : `dotnet build ArchiSteamFarm.CustomPlugins.SteamSceScraper/ArchiSteamFarm.CustomPlugins.SteamSceScraper.csproj -c Debug`
- **Test de chargement** : exécuter le binaire ASF depuis `ArchiSteamFarm/bin/Debug/net10.0/`
  et vérifier dans les logs « SteamSceScraperPlugin has been loaded successfully! ».
- Les interfaces ASF disponibles sont dans `ArchiSteamFarm/Plugins/Interfaces/`
  (`IASF`, `IBot`, `IBotCommand2`, `IBotModules`, `IBotTradeOffer2`, `IWebInterface`...).
- Modèles à imiter : `ArchiSteamFarm.CustomPlugins.ExamplePlugin/` (API WebBrowser,
  contrôleur IPC, OnASFInit), `ArchiSteamFarm.CustomPlugins.PeriodicGC/` (timer minimal),
  `ArchiSteamFarm.OfficialPlugins.SteamTokenDumper/` (plugin complet avec config + IPC).
- Cloner le fork ASF depuis `https://github.com/DrNibble/ArchiSteamFarm.git` et travailler
  sur `proposal/steam-sce-custom-plugin`.
