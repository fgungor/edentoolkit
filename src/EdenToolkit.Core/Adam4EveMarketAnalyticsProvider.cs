using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EdenToolkit.Core;

public sealed class Adam4EveMarketAnalyticsProvider : IMarketAnalyticsProvider
{
    private const string SourceName = "Adam4EVE";
    private readonly HttpClient _http;
    private readonly EdenOptions _options;
    private readonly Adam4EveCache _cache;
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    public bool Enabled => _options.Adam4EveEnabled;

    public Adam4EveMarketAnalyticsProvider(HttpClient http, EdenOptions options)
    {
        _http = http; _options = options; _cache = new(options);
        if (Enabled && !HasContact(options.Adam4EveUserAgent))
            throw new InvalidOperationException("Adam4EVE is enabled but EDEN_ADAM4EVE_USER_AGENT does not contain a contact method.");
    }

    public async Task<TradeActivity?> GetTradeActivityAsync(int typeId, long locationId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;
        if (to < from) throw new ArgumentException("Trade activity end date must not precede start date.");
        long buyVolume = 0, sellVolume = 0; int buyTrades = 0, sellTrades = 0;
        decimal buyIsk = 0, sellIsk = 0, buyPriceValue = 0, sellPriceValue = 0;
        decimal? highBuy = null, lowBuy = null, highSell = null, lowSell = null;
        var fetchedAt = DateTimeOffset.MinValue; var found = false;
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var json = await GetCachedJsonAsync("tracker", typeId, locationId, key,
                date < DateOnly.FromDateTime(DateTime.UtcNow) ? TimeSpan.FromDays(3650) : TimeSpan.FromMinutes(30),
                $"v1/tracker?typeID={typeId}&date={key}&locationID={locationId}&withGone=0", cancellationToken);
            fetchedAt = DateTimeOffset.UtcNow;
            if (json.ValueKind != JsonValueKind.Object) continue;
            found = true;
            Accumulate(json, "buy", ref buyVolume, ref buyTrades, ref buyIsk, ref buyPriceValue, ref highBuy, ref lowBuy);
            Accumulate(json, "sell", ref sellVolume, ref sellTrades, ref sellIsk, ref sellPriceValue, ref highSell, ref lowSell);
        }
        return !found ? null : new(typeId, locationId, from, to, buyVolume, sellVolume, buyTrades, sellTrades,
            buyIsk, sellIsk, buyVolume == 0 ? null : buyPriceValue / buyVolume,
            sellVolume == 0 ? null : sellPriceValue / sellVolume, highBuy, lowBuy, highSell, lowSell,
            SourceName + " tracker", "estimated from sampled order-book changes", fetchedAt);
    }

    public async Task<AnalyticsPriceHistory?> GetPriceHistoryAsync(int typeId, int regionId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;
        var range = $"{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        var json = await GetCachedJsonAsync("price-history", typeId, regionId, range,
            to < DateOnly.FromDateTime(DateTime.UtcNow) ? TimeSpan.FromDays(3650) : TimeSpan.FromHours(6),
            $"v1/market_price_history?typeID={typeId}&regionID={regionId}&start={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}", cancellationToken);
        if (json.ValueKind != JsonValueKind.Array) return null;
        var points = json.EnumerateArray().Select(x => new HubMarketHistoryPoint(typeId, regionId,
            DateOnly.Parse(Text(x, "price_date"), CultureInfo.InvariantCulture), Dec(x, "buy_price_low"),
            Dec(x, "buy_price_avg"), Dec(x, "buy_price_high"), Dec(x, "sell_price_low"),
            Dec(x, "sell_price_avg"), Dec(x, "sell_price_high"), Long(x, "buy_volume_low"),
            Long(x, "buy_volume_avg"), Long(x, "buy_volume_high"), Long(x, "sell_volume_low"),
            Long(x, "sell_volume_avg"), Long(x, "sell_volume_high"), SourceName + " regional order-book history")).ToArray();
        decimal? spread = points.Length == 0 ? null : points.Where(x => x.BuyPriceAverage > 0)
            .Select(x => (x.SellPriceAverage - x.BuyPriceAverage) / x.BuyPriceAverage * 100m).DefaultIfEmpty().Average();
        return new(typeId, regionId, from, to, points, spread, SourceName + " regional order-book history", DateTimeOffset.UtcNow);
    }

    public async Task<MarketPercentiles?> GetPercentilesAsync(int typeId, int regionId,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;
        var json = await GetCachedJsonAsync("percentiles", typeId, regionId, "current", TimeSpan.FromMinutes(10),
            $"v1/market_percentiles?typeID={typeId}&locationID={regionId}", cancellationToken);
        if (json.ValueKind != JsonValueKind.Object) return null;
        var timestamp = DateTimeOffset.TryParse(Text(json, "lupdate"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        return new(typeId, regionId, NullableDec(json, "percentile_buy"), NullableDec(json, "percentile_sell"),
            timestamp, SourceName + " regional 5% percentile");
    }

    private async Task<JsonElement> GetCachedJsonAsync(string kind, int typeId, long locationId, string range,
        TimeSpan lifetime, string relativePath, CancellationToken cancellationToken)
    {
        if (await _cache.ReadAsync(kind, typeId, locationId, range, cancellationToken) is { } cached &&
            cached.FetchedAt + lifetime > DateTimeOffset.UtcNow) return cached.Data;
        await _rateLock.WaitAsync(cancellationToken);
        try
        {
            var delay = _options.Adam4EveMinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.Adam4EveBaseUri, relativePath));
            request.Headers.UserAgent.ParseAdd(_options.Adam4EveUserAgent!);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _lastRequest = DateTimeOffset.UtcNow;
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var data = document.RootElement.Clone();
            await _cache.SaveAsync(kind, typeId, locationId, range, data, _lastRequest, cancellationToken);
            return data;
        }
        finally { _rateLock.Release(); }
    }

    private static void Accumulate(JsonElement root, string side, ref long volume, ref int trades, ref decimal isk,
        ref decimal priceValue, ref decimal? high, ref decimal? low)
    {
        if (!root.TryGetProperty(side, out var value) || value.ValueKind != JsonValueKind.Object) return;
        var amount = Long(value, "amount"); var average = Dec(value, "avg");
        volume += amount; trades += checked((int)Long(value, "orderNum")); isk += Dec(value, "iskValue");
        priceValue += average * amount;
        var h = NullableDec(value, "high"); var l = NullableDec(value, "low");
        if (h is not null) high = high is null ? h : Math.Max(high.Value, h.Value);
        if (l is not null) low = low is null ? l : Math.Min(low.Value, l.Value);
    }
    private static bool HasContact(string? value) => !string.IsNullOrWhiteSpace(value) &&
        (value.Contains('@') || value.Contains("http", StringComparison.OrdinalIgnoreCase) || value.Contains("contact:", StringComparison.OrdinalIgnoreCase));
    private static string Text(JsonElement x, string name) => x.TryGetProperty(name, out var p) ? p.ToString() : "";
    private static decimal Dec(JsonElement x, string name) => decimal.TryParse(Text(x, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static decimal? NullableDec(JsonElement x, string name) => decimal.TryParse(Text(x, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static long Long(JsonElement x, string name) => long.TryParse(Text(x, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

internal sealed record Adam4EveCacheEntry(JsonElement Data, DateTimeOffset FetchedAt);

internal sealed class Adam4EveCache
{
    private readonly string _connectionString;
    public Adam4EveCache(EdenOptions options)
    {
        Directory.CreateDirectory(options.CacheDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.CacheDirectory, "characters.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = """
            CREATE TABLE IF NOT EXISTS adam4eve_cache (
              kind TEXT NOT NULL, type_id INTEGER NOT NULL, location_id INTEGER NOT NULL, range_key TEXT NOT NULL,
              fetched_at TEXT NOT NULL, raw_json TEXT NOT NULL, PRIMARY KEY(kind,type_id,location_id,range_key));
            CREATE INDEX IF NOT EXISTS ix_adam4eve_cache_fetched ON adam4eve_cache(fetched_at);
            """; command.ExecuteNonQuery();
    }
    public async Task<Adam4EveCacheEntry?> ReadAsync(string kind, int typeId, long locationId, string range, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT fetched_at,raw_json FROM adam4eve_cache WHERE kind=$kind AND type_id=$type AND location_id=$location AND range_key=$range;";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$type", typeId); command.Parameters.AddWithValue("$location", locationId); command.Parameters.AddWithValue("$range", range);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return null;
        using var document = JsonDocument.Parse(reader.GetString(1));
        return new(document.RootElement.Clone(), DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
    }
    public async Task SaveAsync(string kind, int typeId, long locationId, string range, JsonElement data, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = """
            INSERT INTO adam4eve_cache(kind,type_id,location_id,range_key,fetched_at,raw_json) VALUES($kind,$type,$location,$range,$fetched,$json)
            ON CONFLICT(kind,type_id,location_id,range_key) DO UPDATE SET fetched_at=excluded.fetched_at,raw_json=excluded.raw_json;
            """;
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$type", typeId); command.Parameters.AddWithValue("$location", locationId); command.Parameters.AddWithValue("$range", range);
        command.Parameters.AddWithValue("$fetched", fetchedAt.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$json", data.GetRawText());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
