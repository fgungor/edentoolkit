using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EdenToolkit.Core;

public sealed class MarketDataRepository
{
    private readonly string _connectionString;

    public MarketDataRepository(EdenOptions options)
    {
        var path = Path.Combine(options.CacheDirectory, "characters.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        Initialize();
    }

    public async Task SaveQuoteAsync(MarketQuote quote, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO market_quotes(type_id,hub,location_id,region_id,type_name,best_buy,best_sell,depth_buy,depth_sell,
              buy_volume,sell_volume,spread,spread_percent,quoted_at)
            VALUES($type,$hub,$location,$region,$name,$bestBuy,$bestSell,$depthBuy,$depthSell,$buyVolume,$sellVolume,$spread,$spreadPercent,$at)
            ON CONFLICT(type_id,hub) DO UPDATE SET location_id=excluded.location_id,region_id=excluded.region_id,
              type_name=excluded.type_name,best_buy=excluded.best_buy,best_sell=excluded.best_sell,depth_buy=excluded.depth_buy,
              depth_sell=excluded.depth_sell,buy_volume=excluded.buy_volume,sell_volume=excluded.sell_volume,
              spread=excluded.spread,spread_percent=excluded.spread_percent,quoted_at=excluded.quoted_at;
            """;
        Add(command, "$type", quote.TypeId); Add(command, "$hub", quote.Hub); Add(command, "$location", quote.LocationId);
        Add(command, "$region", quote.RegionId); Add(command, "$name", quote.TypeName); Add(command, "$bestBuy", quote.BestBuy);
        Add(command, "$bestSell", quote.BestSell); Add(command, "$depthBuy", quote.DepthBuy); Add(command, "$depthSell", quote.DepthSell);
        Add(command, "$buyVolume", quote.BuyVolume); Add(command, "$sellVolume", quote.SellVolume); Add(command, "$spread", quote.Spread);
        Add(command, "$spreadPercent", quote.SpreadPercent); Add(command, "$at", quote.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MarketQuote?> ReadQuoteAsync(int typeId, string hub, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type_name,location_id,region_id,best_buy,best_sell,depth_buy,depth_sell,buy_volume,sell_volume,
              spread,spread_percent,quoted_at FROM market_quotes WHERE type_id=$type AND hub=$hub;
            """;
        Add(command, "$type", typeId); Add(command, "$hub", hub);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(typeId, reader.GetString(0), hub, reader.GetInt64(1), reader.GetInt32(2), Decimal(reader, 3),
            Decimal(reader, 4), Decimal(reader, 5), Decimal(reader, 6), reader.GetInt64(7), reader.GetInt64(8),
            Decimal(reader, 9), Decimal(reader, 10), DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<MarketQuote>> ReadQuotesAsync(string hub, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type_id FROM market_quotes WHERE hub=$hub ORDER BY quoted_at DESC;";
        Add(command, "$hub", hub);
        var ids = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        var result = new List<MarketQuote>();
        foreach (var id in ids)
            if (await ReadQuoteAsync(id, hub, cancellationToken) is { } quote) result.Add(quote);
        return result;
    }

    public Task<IReadOnlyList<MarketHistoryDay>> ReadCachedHistoryAsync(int typeId, int regionId, int days,
        CancellationToken cancellationToken = default) => ReadHistoryAsync(typeId, regionId, days, cancellationToken);

    public async Task SaveHistoryAsync(int typeId, int regionId, IEnumerable<MarketHistoryDay> history,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var day in history)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO market_history(type_id,region_id,day,average,highest,lowest,volume,order_count)
                VALUES($type,$region,$day,$average,$highest,$lowest,$volume,$orders)
                ON CONFLICT(type_id,region_id,day) DO UPDATE SET average=excluded.average,highest=excluded.highest,
                  lowest=excluded.lowest,volume=excluded.volume,order_count=excluded.order_count;
                """;
            Add(command, "$type", typeId); Add(command, "$region", regionId); Add(command, "$day", day.Date.ToString("yyyy-MM-dd"));
            Add(command, "$average", day.Average); Add(command, "$highest", day.Highest); Add(command, "$lowest", day.Lowest);
            Add(command, "$volume", day.Volume); Add(command, "$orders", day.OrderCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MarketHistoryDay>> ReadHistoryAsync(int typeId, int regionId, int days,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT day,average,highest,lowest,volume,order_count FROM market_history
            WHERE type_id=$type AND region_id=$region ORDER BY day DESC LIMIT $days;
            """;
        Add(command, "$type", typeId); Add(command, "$region", regionId); Add(command, "$days", Math.Clamp(days, 1, 3650));
        var result = new List<MarketHistoryDay>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(1), CultureInfo.InvariantCulture), Convert.ToDecimal(reader.GetDouble(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(3), CultureInfo.InvariantCulture), reader.GetInt64(4), reader.GetInt64(5)));
        return result;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS market_quotes (
              type_id INTEGER NOT NULL, hub TEXT NOT NULL, location_id INTEGER NOT NULL, region_id INTEGER NOT NULL,
              type_name TEXT NOT NULL, best_buy REAL, best_sell REAL, depth_buy REAL, depth_sell REAL,
              buy_volume INTEGER NOT NULL, sell_volume INTEGER NOT NULL, spread REAL, spread_percent REAL,
              quoted_at TEXT NOT NULL, PRIMARY KEY(type_id,hub));
            CREATE INDEX IF NOT EXISTS ix_market_quotes_hub_time ON market_quotes(hub,quoted_at);
            CREATE TABLE IF NOT EXISTS market_history (
              type_id INTEGER NOT NULL, region_id INTEGER NOT NULL, day TEXT NOT NULL, average REAL NOT NULL,
              highest REAL NOT NULL, lowest REAL NOT NULL, volume INTEGER NOT NULL, order_count INTEGER NOT NULL,
              PRIMARY KEY(type_id,region_id,day));
            CREATE INDEX IF NOT EXISTS ix_market_history_lookup ON market_history(type_id,region_id,day DESC);
            PRAGMA user_version=3;
            """; command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static decimal? Decimal(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture);
}
