using System.Text.Json;

namespace EdenToolkit.Core;

public sealed class MarketDataService(EsiClient esi, SdeService sde, MarketDataRepository repository)
{
    public static readonly IReadOnlyDictionary<string, MarketHub> Hubs = new Dictionary<string, MarketHub>(StringComparer.OrdinalIgnoreCase)
    {
        ["Hek"] = new("Hek", 60005686, 10000042),
        ["Jita"] = new("Jita", 60003760, 10000002),
        ["Dodixie"] = new("Dodixie", 60011866, 10000032),
        ["Amarr"] = new("Amarr", 60008494, 10000043)
    };

    public async Task<MarketQuoteAnalysis> GetQuoteAsync(string item, string hubName, int historyDays = 30,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        var type = await ResolveTypeAsync(item, cancellationToken);
        return await GetQuoteAsync(checked((int)type.Id), type.Name, GetHub(hubName), historyDays, refresh, true, cancellationToken);
    }

    public async Task<MarketDepth> GetDepthAsync(string item, string hubName, int levels = 10,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        levels = Math.Clamp(levels, 1, 100);
        var type = await ResolveTypeAsync(item, cancellationToken);
        var hub = GetHub(hubName);
        var orders = (await FetchOrdersAsync(checked((int)type.Id), hub.RegionId, refresh, cancellationToken))
            .Where(order => order.GetProperty("location_id").GetInt64() == hub.LocationId).ToArray();
        var buys = orders.Where(IsBuy).OrderByDescending(Price).ToArray();
        var sells = orders.Where(order => !IsBuy(order)).OrderBy(Price).ToArray();
        var buyVolume = buys.Sum(Volume); var sellVolume = sells.Sum(Volume);
        var result = new MarketDepth(checked((int)type.Id), type.Name, hub.Name, hub.LocationId, hub.RegionId,
            buys.Length == 0 ? null : Price(buys[0]), sells.Length == 0 ? null : Price(sells[0]),
            buys.Length == 0 || sells.Length == 0 || Price(buys[0]) <= 0 ? null : (Price(sells[0]) - Price(buys[0])) / Price(buys[0]) * 100m,
            Levels(buys, levels), Levels(sells, levels), DepthVwap(buys, buyVolume), DepthVwap(sells, sellVolume),
            buyVolume, sellVolume, DateTimeOffset.UtcNow);
        await repository.SaveQuoteAsync(new(result.TypeId, result.TypeName, result.Hub, result.LocationId, result.RegionId,
            result.BestBuy, result.BestSell, result.BuyDepthPrice, result.SellDepthPrice, result.BuyVolume, result.SellVolume,
            result.BestBuy is not null && result.BestSell is not null ? result.BestSell - result.BestBuy : null,
            result.SpreadPercent, result.Timestamp), cancellationToken);
        return result;
    }

    public async Task<MarketHistoryStats> GetHistoryAsync(string item, string hubOrRegion, int days = 30,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        var type = await ResolveTypeAsync(item, cancellationToken);
        var regionId = int.TryParse(hubOrRegion, out var numeric) ? numeric : GetHub(hubOrRegion).RegionId;
        return await FetchAndSummarizeHistoryAsync(checked((int)type.Id), regionId, days, refresh, cancellationToken);
    }

    internal MarketHub ResolveHub(string name) => GetHub(name);
    internal Task<SdeName> ResolveMarketTypeAsync(string item, CancellationToken cancellationToken) => ResolveTypeAsync(item, cancellationToken);
    internal Task<IReadOnlyList<MarketQuote>> CachedQuotesAsync(string hub, CancellationToken cancellationToken) =>
        repository.ReadQuotesAsync(GetHub(hub).Name, cancellationToken);
    internal async Task<MarketHistoryStats> CachedHistoryAsync(int typeId, int regionId, int days, CancellationToken cancellationToken)
    {
        var history = (await repository.ReadCachedHistoryAsync(typeId, regionId, days, cancellationToken)).ToArray();
        return Summarize(typeId, regionId, days, history);
    }

    public async Task<IReadOnlyList<MarketQuoteAnalysis>> CompareHubsAsync(string item, IEnumerable<string> hubs,
        int historyDays = 30, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var type = await ResolveTypeAsync(item, cancellationToken);
        var results = new List<MarketQuoteAnalysis>();
        foreach (var hubName in hubs) results.Add(await GetQuoteAsync(checked((int)type.Id), type.Name, GetHub(hubName), historyDays, refresh, true, cancellationToken));
        return results;
    }

    public async Task<MarketQuote> GetCachedOrRefreshQuoteAsync(int typeId, string typeName, string hubName,
        TimeSpan freshness, CancellationToken cancellationToken = default)
    {
        var hub = GetHub(hubName);
        var cached = await repository.ReadQuoteAsync(typeId, hub.Name, cancellationToken);
        if (cached is not null && cached.Timestamp >= DateTimeOffset.UtcNow - freshness) return cached;
        return (await GetQuoteAsync(typeId, typeName, hub, 30, false, false, cancellationToken)).Quote;
    }

    private async Task<MarketQuoteAnalysis> GetQuoteAsync(int typeId, string typeName, MarketHub hub, int historyDays,
        bool refresh, bool includeHistory, CancellationToken cancellationToken)
    {
        var orders = await FetchOrdersAsync(typeId, hub.RegionId, refresh, cancellationToken);
        var atHub = orders.Where(order => order.GetProperty("location_id").GetInt64() == hub.LocationId).ToArray();
        var buys = atHub.Where(IsBuy).OrderByDescending(Price).ToArray();
        var sells = atHub.Where(order => !IsBuy(order)).OrderBy(Price).ToArray();
        var buyVolume = buys.Sum(Volume);
        var sellVolume = sells.Sum(Volume);
        decimal? bestBuy = buys.Length == 0 ? null : Price(buys[0]);
        decimal? bestSell = sells.Length == 0 ? null : Price(sells[0]);
        var spread = bestBuy is not null && bestSell is not null ? bestSell - bestBuy : null;
        var spreadPercent = spread is not null && bestBuy > 0 ? spread / bestBuy * 100m : null;
        var quote = new MarketQuote(typeId, typeName, hub.Name, hub.LocationId, hub.RegionId, bestBuy, bestSell,
            DepthVwap(buys, buyVolume), DepthVwap(sells, sellVolume), buyVolume, sellVolume, spread, spreadPercent, DateTimeOffset.UtcNow);
        await repository.SaveQuoteAsync(quote, cancellationToken);

        var history = includeHistory
            ? await FetchAndSummarizeHistoryAsync(typeId, hub.RegionId, historyDays, refresh, cancellationToken)
            : EmptyHistory(typeId, hub.RegionId, historyDays);
        return new(quote, history);
    }

    private async Task<JsonElement[]> FetchOrdersAsync(int typeId, int regionId, bool refresh, CancellationToken cancellationToken)
    {
        var first = await esi.GetAsync($"latest/markets/{regionId}/orders/?order_type=all&type_id={typeId}&page=1", refresh, cancellationToken);
        var orders = first.Data.EnumerateArray().Select(value => value.Clone()).ToList();
        for (var page = 2; page <= first.Pages; page++)
        {
            var result = await esi.GetAsync($"latest/markets/{regionId}/orders/?order_type=all&type_id={typeId}&page={page}", refresh, cancellationToken);
            orders.AddRange(result.Data.EnumerateArray().Select(value => value.Clone()));
        }
        return orders.ToArray();
    }

    private async Task<MarketHistoryStats> FetchAndSummarizeHistoryAsync(int typeId, int regionId, int days,
        bool refresh, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, 3650);
        var result = await esi.GetAsync($"latest/markets/{regionId}/history/?type_id={typeId}", refresh, cancellationToken);
        var history = result.Data.EnumerateArray().Select(value => new MarketHistoryDay(
            DateOnly.Parse(value.GetProperty("date").GetString()!), value.GetProperty("average").GetDecimal(),
            value.GetProperty("highest").GetDecimal(), value.GetProperty("lowest").GetDecimal(),
            value.GetProperty("volume").GetInt64(), value.GetProperty("order_count").GetInt64())).ToArray();
        await repository.SaveHistoryAsync(typeId, regionId, history, cancellationToken);
        return Summarize(typeId, regionId, days, (await repository.ReadHistoryAsync(typeId, regionId, days, cancellationToken)).ToArray());
    }

    private async Task<SdeName> ResolveTypeAsync(string item, CancellationToken cancellationToken)
    {
        if (long.TryParse(item, out var id))
        {
            var found = await sde.FindByIdAsync(id, "types", cancellationToken);
            if (found is not null) return found;
            throw new KeyNotFoundException($"No item type exists with ID {id}.");
        }
        var matches = (await sde.SearchAsync(item, 100, cancellationToken)).Where(value => value.Kind == "types").ToArray();
        var exact = matches.FirstOrDefault(value => value.Name.Equals(item, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        if (matches.Length == 1) return matches[0];
        if (matches.Length == 0) throw new KeyNotFoundException($"No item type matches '{item}'.");
        throw new ArgumentException($"Item name '{item}' is ambiguous. Matches include: {string.Join(", ", matches.Take(10).Select(value => value.Name))}");
    }

    private static MarketHub GetHub(string name) => Hubs.TryGetValue(name, out var hub) ? hub
        : throw new ArgumentException($"Unknown market hub '{name}'. Supported hubs: {string.Join(", ", Hubs.Keys)}");
    private static bool IsBuy(JsonElement order) => order.GetProperty("is_buy_order").GetBoolean();
    private static decimal Price(JsonElement order) => order.GetProperty("price").GetDecimal();
    private static long Volume(JsonElement order) => order.GetProperty("volume_remain").GetInt64();

    private static IReadOnlyList<MarketDepthLevel> Levels(JsonElement[] orders, int count) => orders
        .GroupBy(Price).OrderBy(group => orders.Length > 0 && IsBuy(orders[0]) ? -group.Key : group.Key)
        .Take(count).Select(group => new MarketDepthLevel(group.Key, group.Sum(Volume), group.Count())).ToArray();

    private static decimal? DepthVwap(JsonElement[] orders, long totalVolume)
    {
        if (orders.Length == 0 || totalVolume <= 0) return null;
        var target = Math.Max(1L, (long)Math.Ceiling(totalVolume * 0.05m));
        long filled = 0; decimal value = 0;
        foreach (var order in orders)
        {
            var take = Math.Min(Volume(order), target - filled);
            value += Price(order) * take; filled += take;
            if (filled >= target) break;
        }
        return filled == 0 ? null : value / filled;
    }

    private static MarketHistoryStats Summarize(int typeId, int regionId, int days, MarketHistoryDay[] history) => new(typeId,
        regionId, days, history.Length == 0 ? null : history.Average(value => value.Average),
        history.Length == 0 ? null : history.Max(value => value.Highest), history.Length == 0 ? null : history.Min(value => value.Lowest),
        history.Length == 0 ? 0 : history.Average(value => (decimal)value.Volume),
        history.Length == 0 ? 0 : history.Average(value => (decimal)value.OrderCount), history.Sum(value => value.Volume), history);
    private static MarketHistoryStats EmptyHistory(int typeId, int regionId, int days) => new(typeId, regionId, days, null, null, null, 0, 0, 0, []);
}
