// ----------------------------------------------------------------------------------------------
// SteamSceScraper — plugin ASF de conversion de Steam-SCE-Scraper
// https://github.com/DrNibble/Steam-SCE-Scraper
// ----------------------------------------------------------------------------------------------

namespace ArchiSteamFarm.CustomPlugins.SteamSceScraper;

// Resume de l'etat de la base es_cache.sqlite (compteurs + metas SCE utiles)
internal sealed record DatabaseSummary(int Games, int Cards, int BadgeAppIds, string? SceCredit, string? ScePendingOffers);
