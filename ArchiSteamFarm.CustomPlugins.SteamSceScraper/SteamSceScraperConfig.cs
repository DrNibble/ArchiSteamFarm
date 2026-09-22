// ----------------------------------------------------------------------------------------------
// SteamSceScraper — plugin ASF de conversion de Steam-SCE-Scraper
// https://github.com/DrNibble/Steam-SCE-Scraper
// ----------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using ArchiSteamFarm.Core;

namespace ArchiSteamFarm.CustomPlugins.SteamSceScraper;

// Configuration globale du plugin, lue depuis les proprietes personnalisees
// de config/ASF.json via OnASFInit. Toutes les cles sont préfixées par
// "SteamSceScraper" pour eviter tout conflit avec ASF ou d'autres plugins.
internal static class SteamSceScraperConfig {
	internal const string Prefix = "SteamSceScraper";

	internal const string EnabledProperty = $"{Prefix}Enabled";
	internal const string DatabasePathProperty = $"{Prefix}DatabasePath";
	internal const string ConcurrencyLimitProperty = $"{Prefix}ConcurrencyLimit";
	internal const string InventoryPageDelayProperty = $"{Prefix}InventoryPageDelay";
	internal const string SyncHoursProperty = $"{Prefix}SyncHours";
	internal const string EventAppIdsProperty = $"{Prefix}EventAppIds";

	internal const bool EnabledDefault = true;
	internal const string DatabasePathDefault = "data/es_cache.sqlite";
	internal const byte ConcurrencyLimitDefault = 4;
	internal const int InventoryPageDelayDefault = 800;
	internal const byte SyncHoursDefault = 6;

	internal static bool Enabled { get; private set; } = EnabledDefault;
	internal static string DatabasePath { get; private set; } = DatabasePathDefault;
	internal static byte ConcurrencyLimit { get; private set; } = ConcurrencyLimitDefault;
	internal static int InventoryPageDelay { get; private set; } = InventoryPageDelayDefault;
	internal static byte SyncHours { get; private set; } = SyncHoursDefault;

	// AppIDs d'evenements Steam (Sales, Awards, etc.) — equivalent EVENT_APP_IDS du .env Node
	internal static ImmutableHashSet<string> EventAppIds { get; private set; } = [];

	internal static void Init(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties) {
		if (additionalConfigProperties == null) {
			ASF.ArchiLogger.LogGenericInfo($"{Prefix} : aucune propriete de configuration trouvee, valeurs par defaut utilisees.");

			return;
		}

		foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
			// On n'interprete que nos propres proprietes, les autres appartiennent a ASF ou a d'autres plugins
			if (!configProperty.StartsWith(Prefix, StringComparison.Ordinal)) {
				continue;
			}

			switch (configProperty) {
				case EnabledProperty when configValue.ValueKind == JsonValueKind.True:
					Enabled = true;

					break;
				case EnabledProperty when configValue.ValueKind == JsonValueKind.False:
					Enabled = false;

					break;
				case DatabasePathProperty when configValue.ValueKind == JsonValueKind.String:
					DatabasePath = configValue.GetString() ?? DatabasePathDefault;

					break;
				case ConcurrencyLimitProperty when configValue.ValueKind == JsonValueKind.Number && configValue.TryGetByte(out byte concurrencyLimit) && concurrencyLimit > 0:
					ConcurrencyLimit = concurrencyLimit;

					break;
				case InventoryPageDelayProperty when configValue.ValueKind == JsonValueKind.Number && configValue.TryGetInt32(out int inventoryPageDelay) && inventoryPageDelay >= 0:
					InventoryPageDelay = inventoryPageDelay;

					break;
				case SyncHoursProperty when configValue.ValueKind == JsonValueKind.Number && configValue.TryGetByte(out byte syncHours) && syncHours > 0:
					SyncHours = syncHours;

					break;
				case EventAppIdsProperty when configValue.ValueKind == JsonValueKind.String:
					EventAppIds = ParseAppIds(configValue.GetString());

					break;
				default:
					ASF.ArchiLogger.LogGenericWarning($"{Prefix} : propriete \"{configProperty}\" inconnue ou type invalide, ignoree.");

					break;
			}
		}

		ASF.ArchiLogger.LogGenericInfo($"{Prefix} : configuration chargee — actif={Enabled}, base={DatabasePath}, concurrence={ConcurrencyLimit}, delai inventaire={InventoryPageDelay} ms, sync={SyncHours} h, EVENT_APP_IDS={EventAppIds.Count}.");
	}

	private static ImmutableHashSet<string> ParseAppIds(string? appIds) => string.IsNullOrWhiteSpace(appIds)
		? []
		: appIds.Split(',').Select(static appId => appId.Trim()).Where(static appId => appId.Length > 0).ToImmutableHashSet(StringComparer.Ordinal);
}
