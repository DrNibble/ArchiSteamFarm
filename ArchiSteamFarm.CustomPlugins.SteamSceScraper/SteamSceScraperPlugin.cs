// ----------------------------------------------------------------------------------------------
// SteamSceScraper — plugin ASF de conversion de Steam-SCE-Scraper
// https://github.com/DrNibble/Steam-SCE-Scraper
// ----------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;

namespace ArchiSteamFarm.CustomPlugins.SteamSceScraper;

// Phase P0 : socle du plugin — chargement, configuration et commande de statut.
// Le scraping (badges Steam, SCE, marche) et la base SQLite arrivent en P1-P3.
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class SteamSceScraperPlugin : IASF, IBot, IBotCommand2 {
	private const string RootCommand = "SSE";
	private const string StatusSubcommand = "STATUS";
	private const string DatabaseSubcommand = "DB";

	private const EAccess MinimumAccess = EAccess.FamilySharing;

	// References vers les bots attaches au plugin (nettoyees dans OnBotDestroy)
	private static readonly ConcurrentDictionary<string, Bot> Bots = new(StringComparer.OrdinalIgnoreCase);

	// Base SQLite es_cache.sqlite (phase P1) — reutilise la base du projet Node
	private static EsCacheDatabase? database;

	[JsonInclude]
	public string Name => nameof(SteamSceScraperPlugin);

	[JsonInclude]
	public Version Version => typeof(SteamSceScraperPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} v{Version} charge (phase P1 : stockage SQLite). Le scraping arrive en P2-P3.");

		return Task.CompletedTask;
	}

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		SteamSceScraperConfig.Init(additionalConfigProperties);

		if (SteamSceScraperConfig.Enabled) {
			InitializeDatabase();
		}

		return Task.CompletedTask;
	}

	public Task OnBotInit(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Bots[bot.BotName] = bot;

		return Task.CompletedTask;
	}

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Bots.TryRemove(bot.BotName, out _);

		return Task.CompletedTask;
	}

	public Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);

		if (args.Length == 0 || !string.Equals(args[0], RootCommand, StringComparison.OrdinalIgnoreCase)) {
			return Task.FromResult<string?>(null);
		}

		if (access < MinimumAccess) {
			return Task.FromResult<string?>(null);
		}

		if (args.Length < 2) {
			return Task.FromResult<string?>($"{RootCommand} {StatusSubcommand} : affiche l'etat du plugin.\n{RootCommand} {DatabaseSubcommand} : resume de la base SQLite.");
		}

		switch (args[1].ToUpperInvariant()) {
			case StatusSubcommand:
				return Task.FromResult<string?>(GetStatus());
			case DatabaseSubcommand:
				return Task.FromResult<string?>(GetDatabaseStatus());
			default:
				return Task.FromResult<string?>(null);
		}
	}

	private string GetStatus() =>
		$"{Name} v{Version} — phase P1 (stockage)\n" +
		$"Active : {(SteamSceScraperConfig.Enabled ? "oui" : "non")} | Bots attaches : {Bots.Count}\n" +
		$"Base : {GetDatabaseShortStatus()} | Sync : {SteamSceScraperConfig.SyncHours} h (des P4)\n" +
		$"Concurrence : {SteamSceScraperConfig.ConcurrencyLimit} | Delai inventaire : {SteamSceScraperConfig.InventoryPageDelay} ms | EVENT_APP_IDS : {SteamSceScraperConfig.EventAppIds.Count}\n" +
		"Scraping badges/SCE/marche non installe : arrive en P2-P3.";

	private static string GetDatabaseShortStatus() {
		if (database == null) {
			return "non initialisee";
		}

		try {
			DatabaseSummary summary = database.GetSummary();

			return $"OK ({summary.Games} jeux, {summary.Cards} cartes)";
		} catch (Exception e) {
			return $"erreur ({e.Message})";
		}
	}

	private static string GetDatabaseStatus() {
		if (database == null) {
			return "Base non initialisee (plugin desactive ou erreur d'ouverture).";
		}

		try {
			DatabaseSummary summary = database.GetSummary();

			return
				$"Base : {database.DatabasePath}\n" +
				$"Jeux : {summary.Games} | Cartes : {summary.Cards} | Badge appids : {summary.BadgeAppIds}\n" +
				$"Credits SCE : {summary.SceCredit ?? "n/a"} | Offers en attente : {summary.ScePendingOffers ?? "n/a"}";
		} catch (Exception e) {
			return $"Erreur d'acces a la base : {e.Message}";
		}
	}

	private static void InitializeDatabase() {
		try {
			database = new EsCacheDatabase(SteamSceScraperConfig.DatabasePath);
			database.Initialize();

			DatabaseSummary summary = database.GetSummary();
			ASF.ArchiLogger.LogGenericInfo($"{SteamSceScraperConfig.Prefix} : base initialisee {database.DatabasePath} — {summary.Games} jeux, {summary.Cards} cartes, {summary.BadgeAppIds} badge appids.");
		} catch (Exception e) {
			database = null;
			ASF.ArchiLogger.LogGenericError($"{SteamSceScraperConfig.Prefix} : impossible d'initialiser la base {SteamSceScraperConfig.DatabasePath} : {e}");
		}
	}
}
