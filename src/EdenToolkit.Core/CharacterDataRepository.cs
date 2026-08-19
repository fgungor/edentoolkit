using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace EdenToolkit.Core;

public sealed record CharacterDataQuery(int Limit = 1000, int Offset = 0, long? TypeId = null,
    long? LocationId = null, int? MinimumSkillLevel = null);

public sealed class CharacterDataRepository
{
    private readonly string _connectionString;

    public CharacterDataRepository(EdenOptions options)
    {
        var path = Path.Combine(options.CacheDirectory, "characters.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        Initialize();
    }

    public async Task SaveAsync(CharacterSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO sync_state(character_id, aspect, fetched_at, from_cache, is_stale)
            VALUES($character, $aspect, $fetched, $cached, $stale)
            ON CONFLICT(character_id, aspect) DO UPDATE SET
              fetched_at=excluded.fetched_at, from_cache=excluded.from_cache, is_stale=excluded.is_stale;
            """, cancellationToken, ("$character", snapshot.CharacterId), ("$aspect", snapshot.Kind),
            ("$fetched", snapshot.FetchedAt.ToString("O", CultureInfo.InvariantCulture)), ("$cached", snapshot.FromCache ? 1 : 0),
            ("$stale", snapshot.IsStale ? 1 : 0));

        switch (snapshot.Kind)
        {
            case "location":
                await ReplaceSingletonAsync(connection, transaction, "character_locations", snapshot, cancellationToken);
                break;
            case "wallet":
                await ReplaceSingletonAsync(connection, transaction, "character_wallets", snapshot, cancellationToken);
                break;
            case "assets":
                await ReplaceAssetsAsync(connection, transaction, snapshot, cancellationToken);
                break;
            case "skills":
                await ReplaceSkillsAsync(connection, transaction, snapshot, cancellationToken);
                break;
            default: throw new ArgumentException($"Unsupported character data aspect '{snapshot.Kind}'.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CharacterSnapshot> ReadAsync(long characterId, string aspect, CharacterDataQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new();
        query = query with { Limit = Math.Clamp(query.Limit, 1, 100000), Offset = Math.Max(0, query.Offset) };
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var state = await ReadStateAsync(connection, characterId, aspect, cancellationToken)
            ?? throw new FileNotFoundException($"No {aspect} data exists for character {characterId}. Run 'eden character sync {characterId}'.");
        var data = aspect switch
        {
            "location" => await ReadSingletonAsync(connection, "character_locations", characterId, cancellationToken),
            "wallet" => await ReadSingletonAsync(connection, "character_wallets", characterId, cancellationToken),
            "assets" => await ReadAssetsAsync(connection, characterId, query, cancellationToken),
            "skills" => await ReadSkillsAsync(connection, characterId, query, cancellationToken),
            _ => throw new ArgumentException("Data aspect must be location, assets, wallet, or skills.", nameof(aspect))
        };
        return new(characterId, aspect, state.FetchedAt, data, state.FromCache, state.IsStale);
    }

    public async Task DeleteCharacterAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var table in new[] { "character_assets", "character_skills", "character_skill_summaries", "character_locations", "character_wallets", "sync_state" })
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE character_id=$character;", cancellationToken, ("$character", characterId));
        await transaction.CommitAsync(cancellationToken);
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS sync_state (
              character_id INTEGER NOT NULL, aspect TEXT NOT NULL, fetched_at TEXT NOT NULL,
              from_cache INTEGER NOT NULL, is_stale INTEGER NOT NULL,
              PRIMARY KEY(character_id, aspect));
            CREATE TABLE IF NOT EXISTS character_locations (
              character_id INTEGER PRIMARY KEY, raw_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS character_wallets (
              character_id INTEGER PRIMARY KEY, raw_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS character_assets (
              character_id INTEGER NOT NULL, item_id INTEGER NOT NULL, type_id INTEGER NOT NULL,
              location_id INTEGER NOT NULL, location_type TEXT, location_flag TEXT, quantity INTEGER NOT NULL,
              is_singleton INTEGER NOT NULL, raw_json TEXT NOT NULL,
              PRIMARY KEY(character_id, item_id));
            CREATE INDEX IF NOT EXISTS ix_assets_character_type ON character_assets(character_id, type_id);
            CREATE INDEX IF NOT EXISTS ix_assets_character_location ON character_assets(character_id, location_id);
            CREATE TABLE IF NOT EXISTS character_skill_summaries (
              character_id INTEGER PRIMARY KEY, raw_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS character_skills (
              character_id INTEGER NOT NULL, skill_id INTEGER NOT NULL, active_level INTEGER NOT NULL,
              trained_level INTEGER NOT NULL, skillpoints INTEGER NOT NULL, raw_json TEXT NOT NULL,
              PRIMARY KEY(character_id, skill_id));
            CREATE INDEX IF NOT EXISTS ix_skills_character_level ON character_skills(character_id, trained_level);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private static async Task ReplaceSingletonAsync(SqliteConnection connection, SqliteTransaction transaction, string table,
        CharacterSnapshot snapshot, CancellationToken cancellationToken) => await ExecuteAsync(connection, transaction,
        $"INSERT INTO {table}(character_id, raw_json) VALUES($character, $json) ON CONFLICT(character_id) DO UPDATE SET raw_json=excluded.raw_json;",
        cancellationToken, ("$character", snapshot.CharacterId), ("$json", snapshot.Data.GetRawText()));

    private static async Task ReplaceAssetsAsync(SqliteConnection connection, SqliteTransaction transaction,
        CharacterSnapshot snapshot, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM character_assets WHERE character_id=$character;", cancellationToken,
            ("$character", snapshot.CharacterId));
        foreach (var item in snapshot.Data.EnumerateArray())
            await ExecuteAsync(connection, transaction, """
                INSERT INTO character_assets(character_id,item_id,type_id,location_id,location_type,location_flag,quantity,is_singleton,raw_json)
                VALUES($character,$item,$type,$location,$locationType,$flag,$quantity,$singleton,$json);
                """, cancellationToken, ("$character", snapshot.CharacterId), ("$item", GetInt64(item, "item_id")),
                ("$type", GetInt64(item, "type_id")), ("$location", GetInt64(item, "location_id")),
                ("$locationType", GetString(item, "location_type")), ("$flag", GetString(item, "location_flag")),
                ("$quantity", GetInt64(item, "quantity")), ("$singleton", GetBoolean(item, "is_singleton") ? 1 : 0),
                ("$json", item.GetRawText()));
    }

    private static async Task ReplaceSkillsAsync(SqliteConnection connection, SqliteTransaction transaction,
        CharacterSnapshot snapshot, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM character_skills WHERE character_id=$character;", cancellationToken,
            ("$character", snapshot.CharacterId));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO character_skill_summaries(character_id,raw_json) VALUES($character,$json)
            ON CONFLICT(character_id) DO UPDATE SET raw_json=excluded.raw_json;
            """, cancellationToken, ("$character", snapshot.CharacterId), ("$json", snapshot.Data.GetRawText()));
        foreach (var skill in snapshot.Data.GetProperty("skills").EnumerateArray())
            await ExecuteAsync(connection, transaction, """
                INSERT INTO character_skills(character_id,skill_id,active_level,trained_level,skillpoints,raw_json)
                VALUES($character,$skill,$active,$trained,$points,$json);
                """, cancellationToken, ("$character", snapshot.CharacterId), ("$skill", GetInt64(skill, "skill_id")),
                ("$active", GetInt64(skill, "active_skill_level")), ("$trained", GetInt64(skill, "trained_skill_level")),
                ("$points", GetInt64(skill, "skillpoints_in_skill")), ("$json", skill.GetRawText()));
    }

    private static async Task<JsonElement> ReadSingletonAsync(SqliteConnection connection, string table, long characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT raw_json FROM {table} WHERE character_id=$character;";
        command.Parameters.AddWithValue("$character", characterId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidDataException("SQLite snapshot row is missing.");
        return Parse(json);
    }

    private static async Task<JsonElement> ReadAssetsAsync(SqliteConnection connection, long characterId, CharacterDataQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_json FROM character_assets
            WHERE character_id=$character AND ($type IS NULL OR type_id=$type) AND ($location IS NULL OR location_id=$location)
            ORDER BY item_id LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$character", characterId);
        command.Parameters.AddWithValue("$type", (object?)query.TypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$location", (object?)query.LocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);
        return JsonSerializer.SerializeToElement((await ReadJsonRowsAsync(command, cancellationToken)).Select(Parse).ToArray());
    }

    private static async Task<JsonElement> ReadSkillsAsync(SqliteConnection connection, long characterId, CharacterDataQuery query,
        CancellationToken cancellationToken)
    {
        var summary = JsonNode.Parse((string?)await ScalarAsync(connection,
            "SELECT raw_json FROM character_skill_summaries WHERE character_id=$character;", characterId, cancellationToken)
            ?? throw new InvalidDataException("SQLite skill summary is missing."))!.AsObject();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_json FROM character_skills
            WHERE character_id=$character AND ($type IS NULL OR skill_id=$type) AND ($level IS NULL OR trained_level >= $level)
            ORDER BY skill_id LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$character", characterId);
        command.Parameters.AddWithValue("$type", (object?)query.TypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$level", (object?)query.MinimumSkillLevel ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);
        summary["skills"] = new JsonArray((await ReadJsonRowsAsync(command, cancellationToken)).Select(value => JsonNode.Parse(value)).ToArray());
        return JsonSerializer.SerializeToElement(summary);
    }

    private static async Task<(DateTimeOffset FetchedAt, bool FromCache, bool IsStale)?> ReadStateAsync(SqliteConnection connection,
        long characterId, string aspect, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fetched_at,from_cache,is_stale FROM sync_state WHERE character_id=$character AND aspect=$aspect;";
        command.Parameters.AddWithValue("$character", characterId);
        command.Parameters.AddWithValue("$aspect", aspect);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1) != 0, reader.GetInt64(2) != 0) : null;
    }

    private static async Task<List<string>> ReadJsonRowsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(reader.GetString(0));
        return rows;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, long characterId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$character", characterId);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static long GetInt64(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetInt64() : 0;
    private static string? GetString(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetString() : null;
    private static bool GetBoolean(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.GetBoolean();
}
