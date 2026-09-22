// ----------------------------------------------------------------------------------------------
// SteamSceScraper — plugin ASF de conversion de Steam-SCE-Scraper
// https://github.com/DrNibble/Steam-SCE-Scraper
// ----------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ArchiSteamFarm.CustomPlugins.SteamSceScraper;

// Couche de stockage : reutilise la base SQLite du projet Node (meme fichier,
// meme schema — cf. node/src/db.js de Steam-SCE-Scraper) afin que le frontend
// PHP continue de fonctionner pendant la migration. Phase P1 : initialisation
// et lectures ; les ecritures (scraping) arrivent en P2-P3.
// Connexions courtes avec pooling — le PHP peut lire la base en parallele (WAL).
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Toutes les requetes sont des constantes internes du plugin, aucune donnee utilisateur n'y est concatenee.")]
internal sealed class EsCacheDatabase {
	// Schema identique au SCHEMA de node/src/db.js
	private const string Schema = """
		CREATE TABLE IF NOT EXISTS meta (
			key   TEXT PRIMARY KEY,
			value TEXT
		);

		CREATE TABLE IF NOT EXISTS games (
			appid                          TEXT PRIMARY KEY,
			gamename                       TEXT,
			disabled                       INTEGER DEFAULT 0,
			fetched_at                     INTEGER,
			lasttrade                      INTEGER,
			set_cards                      INTEGER,
			total_owned_qty                INTEGER DEFAULT 0,
			is_completable_via_trade       INTEGER DEFAULT 0,
			is_completable_via_sce         INTEGER DEFAULT 0,
			is_completable_via_sce_wobudget INTEGER DEFAULT 0,
			is_completable_via_sce_doublon INTEGER DEFAULT 0,
			has_expensive_card_json        TEXT,
			total_cost_sce                 INTEGER DEFAULT 0,
			missing_count                  INTEGER DEFAULT 0,
			badge_crafted                  INTEGER
		);

		CREATE TABLE IF NOT EXISTS cards (
			id                        INTEGER PRIMARY KEY AUTOINCREMENT,
			appid                     TEXT NOT NULL,
			name                      TEXT,
			card_index                INTEGER,
			qty                       INTEGER DEFAULT 0,
			hash                      TEXT,
			icon_url                  TEXT,
			art_url                   TEXT,
			inv_json                  TEXT,
			sce_stock                 INTEGER DEFAULT 0,
			sce_worth                 INTEGER DEFAULT 0,
			sce_price                 INTEGER DEFAULT 0,
			sce_market_price_usd      REAL DEFAULT 0,
			steam_market_price_eur     REAL,
			steam_market_sales_7d      INTEGER DEFAULT 0,
			steam_market_fetched_at    INTEGER,
			sce_quick_trade           TEXT,
			UNIQUE(appid, hash),
			FOREIGN KEY(appid) REFERENCES games(appid) ON DELETE CASCADE
		);

		CREATE TABLE IF NOT EXISTS badge_appids (
			appid     TEXT PRIMARY KEY,
			gamename  TEXT,
			disabled  INTEGER DEFAULT 0,
			fetched_at INTEGER
		);
		""";

	// Migrations identiques au Node : ignorees silencieusement si les colonnes existent deja
	private static readonly string[] Migrations = [
		"ALTER TABLE cards ADD COLUMN steam_market_price_eur REAL",
		"ALTER TABLE cards ADD COLUMN steam_market_sales_7d INTEGER DEFAULT 0",
		"ALTER TABLE cards ADD COLUMN steam_market_fetched_at INTEGER",
		"ALTER TABLE games ADD COLUMN badge_crafted INTEGER"
	];

	internal string DatabasePath { get; }
	internal bool IsInitialized { get; private set; }

	private readonly string connectionString;

	internal EsCacheDatabase(string databasePath) {
		ArgumentException.ThrowIfNullOrEmpty(databasePath);

		// Resolution par rapport au repertoire d'execution d'ASF : les chemins
		// relatifs (ex. "data/es_cache.sqlite") atterrissent a cote d'ASF, comme
		// le Node resolvait par rapport a node/. Un chemin absolu reste absolu.
		DatabasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, databasePath));

		connectionString = new SqliteConnectionStringBuilder {
			DataSource = DatabasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Pooling = true
		}.ToString();
	}

	internal void Initialize() {
		Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? throw new InvalidOperationException(nameof(DatabasePath)));

		using SqliteConnection connection = OpenConnection();

		using (SqliteCommand command = connection.CreateCommand()) {
			command.CommandText = Schema;
			command.ExecuteNonQuery();
		}

		foreach (string migration in Migrations) {
			try {
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = migration;
				command.ExecuteNonQuery();
			} catch (SqliteException) {
				// La colonne existe deja sur une base creee par une version precedente
			}
		}

		IsInitialized = true;
	}

	// Equivalent de getGamesWithCards() du PHP : SELECT * FROM games WHERE disabled = 0 ORDER BY appid
	internal IReadOnlyList<IReadOnlyDictionary<string, object?>> GetGamesWithCards() {
		const string query = "SELECT * FROM games WHERE disabled = 0 ORDER BY appid";

		using SqliteConnection connection = OpenConnection();
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = query;

		return ReadRows(command);
	}

	// Equivalent de getCardsForGame() du PHP : SELECT * FROM cards WHERE appid = @appid ORDER BY card_index
	internal IReadOnlyList<IReadOnlyDictionary<string, object?>> GetCardsForGame(string appId) {
		ArgumentException.ThrowIfNullOrEmpty(appId);

		const string query = "SELECT * FROM cards WHERE appid = @appId ORDER BY card_index";

		using SqliteConnection connection = OpenConnection();
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = query;
		command.Parameters.AddWithValue("@appId", appId);

		return ReadRows(command);
	}

	// Equivalent de getMeta() du Node (cle/valeur de la table meta)
	internal string? GetMeta(string key) {
		ArgumentException.ThrowIfNullOrEmpty(key);

		const string query = "SELECT value FROM meta WHERE key = @key";

		using SqliteConnection connection = OpenConnection();
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = query;
		command.Parameters.AddWithValue("@key", key);

		return command.ExecuteScalar() as string;
	}

	internal DatabaseSummary GetSummary() {
		using SqliteConnection connection = OpenConnection();

		return new DatabaseSummary(
			(int) ExecuteScalarLong(connection, "SELECT COUNT(*) FROM games"),
			(int) ExecuteScalarLong(connection, "SELECT COUNT(*) FROM cards"),
			(int) ExecuteScalarLong(connection, "SELECT COUNT(*) FROM badge_appids"),
			GetMeta(connection, "scecredit"),
			GetMeta(connection, "scePendingOffers")
		);
	}

	private SqliteConnection OpenConnection() {
		SqliteConnection connection = new(connectionString);
		connection.Open();

		// PRAGMAs par connexion — WAL rend possible la lecture parallele par le PHP
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = """
			PRAGMA journal_mode = WAL;
			PRAGMA synchronous = NORMAL;
			PRAGMA busy_timeout = 5000;
			PRAGMA foreign_keys = ON;
			""";

		command.ExecuteNonQuery();

		return connection;
	}

	private static string? GetMeta(SqliteConnection connection, string key) {
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "SELECT value FROM meta WHERE key = @key";
		command.Parameters.AddWithValue("@key", key);

		return command.ExecuteScalar() as string;
	}

	private static long ExecuteScalarLong(SqliteConnection connection, string query) {
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = query;

		return command.ExecuteScalar() as long? ?? 0;
	}

	private static List<IReadOnlyDictionary<string, object?>> ReadRows(SqliteCommand command) {
		List<IReadOnlyDictionary<string, object?>> rows = [];

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read()) {
			Dictionary<string, object?> row = new(StringComparer.Ordinal);

			for (int i = 0; i < reader.FieldCount; i++) {
				row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
			}

			rows.Add(row);
		}

		return rows;
	}
}
