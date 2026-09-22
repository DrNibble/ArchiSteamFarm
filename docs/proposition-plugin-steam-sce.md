# Proposition : convertir Steam-SCE-Scraper en plugin ASF (CustomPlugins)

> Document de proposition — branche `proposal/steam-sce-custom-plugin`.
> Aucune implémentation C# n'est encore livrée ici : ce document décrit l'architecture
> cible, la correspondance module par module et un plan de migration par phases.

## 1. Objectif

Migrer le projet [Steam-SCE-Scraper](https://github.com/DrNibble/Steam-SCE-Scraper)
(Node.js + PHP) vers un **CustomPlugin ArchiSteamFarm**, afin de :

- **supprimer l'authentification Steam maison** (steam-session, `STEAM_COOKIE`
  copié à la main dans `.env`) : ASF gère déjà les sessions Steam des bots.
  Le cookie SCE (`PHPSESSID`) reste nécessaire en config tant que
  steamcardexchange.net n'a pas d'alternative ;
- **supprimer le processus Node à planifier séparément** : le scraper tourne dans le
  processus ASF (timers, restart automatique avec ASF) ;
- **bénéficier de l'IPC/API web d'ASF** pour exposer le rapport en HTTP, en remplacement
  progressif du frontend PHP ;
- mutualiser le rate-limiting, les logs et la configuration avec ceux d'ASF.

## 2. État des lieux — architecture actuelle

| Composant Node/PHP | Rôle |
|---|---|
| `node/src/auth.js` | Connexion Steam (QR / mot de passe, refresh token) |
| `node/src/steam.js` | Scraping badges / inventaire / historique trades Steam |
| `node/src/sce.js` | Scraping steamcardexchange.net (prix, stock, credits) |
| `node/src/market.js` + `marketQueue.js` | Prix marché Steam (orderbook, pricehistory, buy orders) + worker temps réel avec file prioritaire et token bucket |
| `node/src/db.js` | Cache SQLite `data/es_cache.sqlite` (node:sqlite) |
| `node/src/analyze.js` | Analyse des badges (complétion, coûts, cartes chères, déposables) |
| `node/src/sync.js` | Orchestration (workflow, queue, concurrence) |
| `node/src/index.js` | CLI (`--badges`, `--gamecards`, `--history`, `--market`, ...) |
| `node/src/utils.js` | HTTP, normalisation, `isSteamEvent()` (EVENT_APP_IDS depuis `.env`) |
| `php/` | Rapport HTML lu sur la même base SQLite (sections « Completables via SCE », « A déposer au Bot », ...) |

## 3. Options de migration

### Option A — Plugin natif C# (recommandée)

Réécriture en C# dans une bibliothèque `ArchiSteamFarm.CustomPlugins.SteamSceScraper`,
sur le modèle de `ArchiSteamFarm.CustomPlugins.ExamplePlugin` déjà présent dans ce dépôt.

- Avantages : une seule brique à déployer (ASF), sessions Steam gérées par ASF,
  commandes chat intégrées (`!sse`), API HTTP intégrée à l'IPC d'ASF, pas de
  dépendance Node sur la machine.
- Inconvénients : effort de réécriture complet (~2 500 lignes de JS), plus un
  changement de stack (C#/.NET).

### Option B — Wrapper (déconseillée, mais rapide)

Le plugin C# ne fait que lancer/surveiller le processus Node existant et relayer
l'état vers ASF (commandes de base, exposition du rapport PHP via proxy).

- Avantages : migration en quelques jours, zéro réécriture.
- Inconvénients : la double stack subsiste, l'authentification maison reste
  nécessaire, aucun des bénéfices réels (sauf l'hébergement unique).

**Recommandation : option A**, éventuellement précédée d'un prototype option B
jetable pour valider l'intérêt avant d'investir.

## 4. Architecture cible du plugin

### 4.1 Structure du projet

```
ArchiSteamFarm.CustomPlugins.SteamSceScraper/
├── ArchiSteamFarm.CustomPlugins.SteamSceScraper.csproj
├── SteamSceScraperPlugin.cs        # IPlugin + IASF + IBot + IBotCommand2
├── Config/
│   ├── SteamSceConfig.cs           # options globales (préfixe "SteamSceScraper")
│   └── BotConfigExtension.cs       # options par bot (activé, badges ciblés...)
├── Steam/
│   ├── BadgeScraper.cs             # ex steam.js (badges, inventaire, historique)
│   └── EventAppIds.cs              # ex EVENT_APP_IDS (config, plus .env)
├── Sce/
│   ├── SceScraper.cs               # ex sce.js (gamepages, stocks, credits)
│   └── SceCookieManager.cs         # cookie SCE (seul site externe nécessitant une session)
├── Market/
│   ├── MarketScraper.cs            # ex market.js
│   └── MarketQueue.cs              # ex marketQueue.js (file + token bucket)
├── Storage/
│   └── EsCacheDatabase.cs          # ex db.js — même schéma SQLite
├── Analysis/
│   ├── BadgeAnalyzer.cs            # ex analyze.js
│   └── SyncOrchestrator.cs          # ex sync.js + timers
└── IPC/
    ├── SteamSceController.cs        # ex php/index.php → API JSON ASF
    └── Responses/                   # DTOs des sections du rapport
```

### 4.2 Projet C# (`csproj`)

Sur le modèle exact du plugin d'exemple du dépôt :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="JetBrains.Annotations.Sources" PrivateAssets="all" />
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" IncludeAssets="compile" />
    <PackageReference Include="Microsoft.Data.Sqlite" />
    <PackageReference Include="SteamKit2" IncludeAssets="compile" />
    <PackageReference Include="System.Composition.AttributedModel" IncludeAssets="compile" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ArchiSteamFarm\ArchiSteamFarm.csproj"
                      ExcludeAssets="all" Private="false" />
  </ItemGroup>

  <Target Name="PostBuild" AfterTargets="PostBuildEvent">
    <Copy SourceFolders="$(TargetDir)"
          DestinationFolder="..\ArchiSteamFarm\bin\$(Configuration)\$(TargetFramework)\plugins\$(AssemblyName)\"
          SkipUnchangedFiles="true" />
  </Target>
</Project>
```

### 4.3 Interfaces ASF implémentées

D'après `ArchiSteamFarm/Plugins/Interfaces/` et l'ExamplePlugin de ce dépôt :

| Interface | Usage dans SteamSceScraper |
|---|---|
| `IPlugin` (via `[Export(typeof(IPlugin))]`, MEF) | Contrat de base obligatoire |
| `IASF` (`OnASFInit`) | Lecture de la config globale étendue (préfixe `SteamSceScraper*`) : remplace le `.env` |
| `IBot` / `IBotModules` | Attacher le scraper à un bot (son `ArchiWebHandler.WebBrowser`), config par bot dans `Bot.json` |
| `IBotCommand2` (`OnBotCommand`) | Commandes chat : `!sse sync`, `!sse status`, `!sse deposit <appid>`, `!sse report` (ex-CLI `index.js`) |
| `IBotConnection` | Suspendre/reprendre les cycles de scraping selon l'état du bot Steam |
| `IWebInterface` *(à valider)* | Intégration éventuelle à l'UI ASF (page dédiée), en complément de l'API |

Le scraping périodique (ex-`sync.js`) s'appuie sur un `System.Threading.Timer`,
comme le fait `ArchiSteamFarm.CustomPlugins.PeriodicGC` dans ce dépôt.

### 4.4 Accès HTTP : `WebBrowser` d'ASF

- Requêtes **Steam** (badges, inventaire, marché) : `bot.ArchiWebHandler.WebBrowser`
  → sessions, cookies et rate-limiting gérés par ASF ; **plus besoin de `auth.js`,
  de refresh tokens ni de `STEAM_COOKIE`**.
- Requêtes **SCE** (steamcardexchange.net) : `WebBrowser` d'ASF ou du bot + cookie
  `PHPSESSID` SCE fourni en config (seule session externe qui reste nécessaire) ;
  méthodes `UrlGetToJsonObject`, `UrlGetToHtmlDocument` (cf. `CatAPI.cs` de
  l'ExamplePlugin) ; parsing HTML via `HtmlAgilityPack` en remplacement de
  cheerio.

> Notes d'implémentation : `Microsoft.Data.Sqlite` et `HtmlAgilityPack` sont des
> dépendances **nouvelles** (non fournies par ASF) — à référencer sans
> `IncludeAssets="compile"` (assets runtime, incluant les binaires natifs
> SQLite pour `Microsoft.Data.Sqlite`), et à ajouter avec leur version dans
> `Directory.Packages.props` du dépôt (gestion centralisée des packages) lors
> de l'implémentation. À valider : comportement du chargement d'assets natifs
> dans le dossier `plugins/` d'ASF.

### 4.5 Stockage : garder `es_cache.sqlite`

`Storage/EsCacheDatabase.cs` réutilise **le même fichier et le même schéma** que le
projet Node (`data/es_cache.sqlite`, via `Microsoft.Data.Sqlite`), avec un cache
`WAL` et le même préfixe de version en table `meta`. Conséquence :

- le **frontend PHP continue de fonctionner sans changement** pendant toute la
  migration (lecture seule de la base) ;
- les données historiques (prix, stocks, historique) sont conservées.

### 4.6 Frontend : migration en 3 phases

1. **Phase 1 (immédiate)** : PHP existant inchangé, lisant la même base SQLite
   écrite désormais par le plugin C#.
2. **Phase 2** : contrôleur API `SteamSceController : ArchiController`
   (`[Route("/Api/SteamSce")]`, modèle `SteamTokenDumperController` de ce dépôt)
   exposant en JSON les sections du rapport : `/Api/SteamSce/Completables`,
   `/Api/SteamSce/Deposit`, `/Api/SteamSce/Status` — documentation Swagger
   gratuite via l'IPC d'ASF.
3. **Phase 3 (option)** : petite page web autonome ou intégration à ASF-ui
   consommant cette API, en remplacement définitif du PHP.

### 4.7 Dépôt de cartes au bot SCE (trade offers)

Aujourd'hui : bouton d'envoi automatique côté PHP (assetIds vers `TRADE_PARTNER`
avec `TRADE_TOKEN`). Dans le plugin :

- **côté configuration** : `SteamSceScraperSceTradePartner` /
  `SteamSceScraperSceTradeToken` en config globale ASF ;
- **côté exécution** : vérifié dans le code de ce dépôt — `ArchiWebHandler` expose `AcceptTradeOffer`, `DeclineTradeOffer`, `CancelTradeOffer` et `GetTradeOffers`, mais **pas** de méthode publique d'envoi. L'envoi devra donc passer par un appel direct du `WebBrowser` du bot au endpoint Steam `POST /trade/new` (avec le token du destinataire), éventuellement en s'inspirant de l'implémentation d'`AcceptTradeOffer` (`ArchiSteamFarm/Steam/Integration/ArchiWebHandler.cs`). **Spike de validation recommandé** en début de phase P6.

## 5. Correspondance module par module

| Node actuel | Cible C# | Notes |
|---|---|---|
| `auth.js` | **supprimé** | sessions ASF |
| `steam.js` | `Steam/BadgeScraper.cs` | via `bot.ArchiWebHandler.WebBrowser` |
| `sce.js` | `Sce/SceScraper.cs` | cookie SCE en config ; HtmlAgilitPack |
| `market.js` | `Market/MarketScraper.cs` | via WebBrowser |
| `marketQueue.js` | `Market/MarketQueue.cs` | `Channel<T>` + SemaphoreSlim (token bucket) |
| `db.js` | `Storage/EsCacheDatabase.cs` | même base, Microsoft.Data.Sqlite |
| `analyze.js` | `Analysis/BadgeAnalyzer.cs` | LINQ |
| `sync.js` | `Analysis/SyncOrchestrator.cs` | Timer + verrou par bot |
| `index.js` (CLI) | `IBotCommand2.OnBotCommand` | commandes chat `!sse ...` |
| `utils.js` | utilitaires C# du plugin | `isSteamEvent` + config ASF pour EVENT_APP_IDS |
| `php/index.php` | phase 2 : `IPC/SteamSceController.cs` | API JSON ASF + Swagger |
| `.env` | config ASF étendue | clés `SteamSceScraper*` dans `config/ASF.json` |

## 6. Configuration (remplace le `.env`)

Propriétés personnalisées lues dans `OnASFInit` (préfixe obligatoire pour éviter
les collisions, cf. ExamplePlugin) — à ajouter dans `config/ASF.json` :

```json
{
  "SteamSceScraperEnabled": true,
  "SteamSceScraperDatabasePath": "data/es_cache.sqlite",
  "SteamSceScraperSceCookie": "PHPSESSID=...; cookie_consent=1",
  "SteamSceScraperSceTradePartner": "83905207",
  "SteamSceScraperSceTradeToken": "tEx7-bXd",
  "SteamSceScraperEventAppIds": "335590,866860,1797760",
  "SteamSceScraperConcurrencyLimit": 4,
  "SteamSceScraperInventoryPageDelay": 800,
  "SteamSceScraperSyncHours": 6,
  "SteamSceScraperUsdToEur": null
}
```

Et par bot (`config/Bot.json`) :

```json
{
  "SteamSceScraperEnabled": true
}
```

## 7. Plan de migration par phases

> **Statut : phases P0 et P1 implémentées** sur cette branche — projet
> `ArchiSteamFarm.CustomPlugins.SteamSceScraper` : P0 = socle (config ASF
> `SteamSceScraper*`, commande `!sse status`) ; P1 = stockage
> (`Storage/EsCacheDatabase.cs` sur `Microsoft.Data.Sqlite`, meme base et meme
> schéma que le Node, WAL, commandes `!sse db`). Build et chargement vérifiés
> dans ASF. Les phases suivantes sont à venir.

| Phase | Contenu | Critère de sortie |
|---|---|---|
| **P0 — Socle** (1-2 j) | csproj + `SteamSceScraperPlugin` (IPlugin/IASF/IBot), config, logs ASF, `!sse status` | plugin chargé par ASF, visible dans `/Api/Plugins` |
| **P1 — Stockage** (2-3 j) | `EsCacheDatabase.cs` : schéma, migrations `meta`, réutilisation de la base existante | le plugin lit les jeux/cartes écrits par le Node |
| **P2 — Scraping Steam** (3-5 j) | `BadgeScraper` : badges, inventaire, historique (via AWH du bot) | synchronisation badges/inventaire complète |
| **P3 — SCE + marché** (3-5 j) | `SceScraper` (cookie config) + `MarketScraper`/`MarketQueue` | prix et stocks SCE/marché à jour dans SQLite |
| **P4 — Analyse + commandes** (2-3 j) | `BadgeAnalyzer`, `SyncOrchestrator` (timer), commandes `!sse sync`/`report` | le Node n'est plus nécessaire au quotidien |
| **P5 — API IPC** (2-3 j) | `SteamSceController` (JSON + Swagger), sections du rapport | rapport consultable en HTTP via ASF |
| **P6 — Trade offers** (2-4 j) | envoi automatique au bot SCE (spike préalable, cf. §4.7) | équivalent du bouton « Envoyer » du PHP |
| **P7 — Rangement** (1 j) | désactiver le repo Node, archivage, doc | — |

Estimation indicative totale : **3 à 4 semaines** de travail à temps plein,
reconductible : les phases P2-P3 peuvent livrer de manière incrémentale (le PHP
continue de fonctionner sur la même base tout du long).

## 8. Risques et points à valider avant implémentation

1. **Envoi de trade offers par un plugin** (§4.7) : confirmé comme non couvert par
   l'API publique d'`ArchiWebHandler` — l'appel direct au endpoint Steam devra
   être écrit à la main dans le plugin (authentification par la session du bot).
   C'est le seul point d'architecture restant à prototyper (spike P6).
2. **Rate-limiting SCE/Steam marché** : la file Node (token bucket, priorités,
   stale-while-revalidate) doit être répliquée fidèlement en C#
   (`System.Threading.Channels`) pour ne pas dégrader le comportement.
3. **`node:sqlite` → `Microsoft.Data.Sqlite`** : schéma identique, mais vérifier
   les types (INTEGER timestamps en ms) et le mode WAL multi-processus
   (plugin ASF et PHP lecteur en parallèle : OK en WAL, à tester).
4. **Sensibilité des données** : la base contient l'historique des prix — la
   rendre accessible uniquement via IPC protégé (mot de passe ASF), contrairement
   au PHP actuel servi sans authentification.
5. **Cycle de release ASF** : le plugin référence `ArchiSteamFarm.csproj` du
   fork ; suivre l'amont JustArchiNET pour les mises à jour des interfaces
   (risque de breaking changes d'interfaces mineures).

## 9. Références (code de ce dépôt)

- `ArchiSteamFarm.CustomPlugins.ExamplePlugin/ExamplePlugin.cs` — IASF, IBot,
  IBotCommand2, OnASFInit (config étendue), cycle de vie des bots
- `ArchiSteamFarm.CustomPlugins.ExamplePlugin/CatAPI.cs` + `CatController.cs` —
  appels HTTP via `WebBrowser`, contrôleur API custom (`ArchiController`)
- `ArchiSteamFarm.CustomPlugins.PeriodicGC/PeriodicGCPlugin.cs` — timer périodique
- `ArchiSteamFarm.OfficialPlugins.SteamTokenDumper/` — exemple complet : config
  globale étendue, cache, contrôleur IPC, localization
- `ArchiSteamFarm/Plugins/Interfaces/` — contrat des interfaces disponibles
