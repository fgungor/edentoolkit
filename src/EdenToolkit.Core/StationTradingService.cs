using System.Text.Json;

namespace EdenToolkit.Core;

public sealed class StationTradingService(MarketDataService market, CharacterStore characters,
    CharacterDataRepository data)
{
    public async Task<TradingPosition> GetPositionAsync(string item, string character, string hubName,
        int days = 7, CancellationToken cancellationToken = default)
    {
        var owner = await characters.ResolveAsync(character, cancellationToken);
        var type = await market.ResolveMarketTypeAsync(item, cancellationToken);
        var hub = market.ResolveHub(hubName);
        var assets = await TryReadAsync(owner.CharacterId, "assets", new(TypeId: type.Id, LocationId: hub.LocationId), cancellationToken);
        var orders = await TryReadAsync(owner.CharacterId, "orders", new(TypeId: type.Id, LocationId: hub.LocationId), cancellationToken);
        var from = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        var transactions = await TryReadAsync(owner.CharacterId, "transactions", new(TypeId: type.Id,
            LocationId: hub.LocationId, From: from), cancellationToken);

        var inventory = assets.Sum(x => Long(x, "quantity"));
        var buys = transactions.Where(x => Bool(x, "is_buy")).ToArray();
        var sells = transactions.Where(x => !Bool(x, "is_buy")).ToArray();
        var buyUnits = buys.Sum(x => Long(x, "quantity")); var sellUnits = sells.Sum(x => Long(x, "quantity"));
        var avgBuy = WeightedAverage(buys); var avgSell = WeightedAverage(sells);
        var activeBuys = orders.Where(x => Bool(x, "is_buy_order")).ToArray();
        var activeSells = orders.Where(x => !Bool(x, "is_buy_order")).ToArray();
        var realizedUnits = Math.Min(buyUnits, sellUnits);
        return new(checked((int)type.Id), type.Name, owner.CharacterId, owner.Name, hub.Name, inventory,
            avgBuy is null ? 0 : avgBuy.Value * inventory, activeBuys.Length, activeSells.Length,
            activeBuys.Sum(x => Long(x, "volume_remain")), activeSells.Sum(x => Long(x, "volume_remain")),
            activeBuys.Sum(x => Decimal(x, "price") * Long(x, "volume_remain")),
            activeSells.Sum(x => Decimal(x, "price") * Long(x, "volume_remain")), avgBuy, avgSell,
            buyUnits, sellUnits, avgBuy is null || avgSell is null ? null : (avgSell - avgBuy) * realizedUnits);
    }

    public async Task<OrderState> GetOrderStateAsync(long orderId, string? character = null,
        bool refreshMarket = false, CancellationToken cancellationToken = default)
    {
        var owners = character is null ? await characters.ListAsync(cancellationToken)
            : [await characters.ResolveAsync(character, cancellationToken)];
        foreach (var owner in owners)
        {
            var rows = await TryReadAsync(owner.CharacterId, "orders", new(), cancellationToken);
            var order = rows.FirstOrDefault(x => Long(x, "order_id") == orderId);
            if (order.ValueKind == JsonValueKind.Undefined) continue;
            var typeId = checked((int)Long(order, "type_id"));
            var location = Long(order, "location_id");
            var hub = MarketDataService.Hubs.Values.FirstOrDefault(x => x.LocationId == location)
                ?? throw new ArgumentException($"Order {orderId} is not at a supported trade hub.");
            var depth = await market.GetDepthAsync(typeId.ToString(), hub.Name, 100, refreshMarket, cancellationToken);
            var isBuy = Bool(order, "is_buy_order"); var ourPrice = Decimal(order, "price");
            var competing = (isBuy ? depth.BuyLevels : depth.SellLevels)
                .Where(x => x.Price != ourPrice).Select(x => (decimal?)x.Price).FirstOrDefault();
            var total = Long(order, "volume_total"); var remaining = Long(order, "volume_remain");
            var issued = Date(order, "issued"); var age = Math.Max(1m / 24m, (decimal)(DateTimeOffset.UtcNow - issued).TotalDays);
            var fillRate = (total - remaining) / age;
            var history = await market.GetHistoryAsync(typeId.ToString(), hub.Name, 7, false, cancellationToken);
            return new(orderId, owner.CharacterId, owner.Name, typeId, depth.TypeName, hub.Name, isBuy, ourPrice,
                competing, competing is null || competing == 0 ? null : Math.Abs(ourPrice - competing.Value) / competing * 100m,
                total, remaining, total == 0 ? 0 : (decimal)(total - remaining) / total * 100m, age, fillRate,
                history.AverageDailyVolume, fillRate <= 0 ? null : remaining / fillRate);
        }
        throw new KeyNotFoundException($"No active cached order {orderId} exists for the selected tracked character(s).");
    }

    public async Task<StationTradeState> GetStateAsync(string item, string hub, string? character = null,
        decimal salesTaxPercent = 3.6m, decimal brokerFeePercent = 3m, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var depth = await market.GetDepthAsync(item, hub, 20, refresh, cancellationToken);
        var history = await market.GetHistoryAsync(item, hub, 30, refresh, cancellationToken);
        TradingPosition? position = character is null ? null : await GetPositionAsync(item, character, hub, 7, cancellationToken);
        var recent = history.History.Take(7).ToArray();
        var volume7 = recent.Length == 0 ? 0m : recent.Average(x => (decimal)x.Volume);
        var volume30 = history.AverageDailyVolume;
        var profit = depth.BestBuy is not null && depth.BestSell is not null
            ? depth.BestSell * (1m - salesTaxPercent / 100m - brokerFeePercent / 100m) - depth.BestBuy * (1m + brokerFeePercent / 100m) : null;
        var spreadAfterFees = profit is not null && depth.BestBuy > 0 ? profit / depth.BestBuy * 100m : null;
        var inventory = position?.Inventory ?? 0; var sellDays = volume7 > 0 ? inventory / volume7 : (decimal?)null;
        var committed = position?.BuyOrderIskCommitted ?? 0;
        decimal? turnover = committed > 0 && volume7 > 0 && depth.BestBuy > 0 ? volume7 * depth.BestBuy.Value / committed : null;
        var dailyProfit = profit is null ? null : Math.Max(0, Math.Min(volume7 * .05m, depth.BuyVolume * .05m)) * profit;
        var metrics = new TradeMetrics(spreadAfterFees, profit, volume7, volume30,
            position?.ActiveBuyVolume ?? 0, position?.ActiveSellVolume ?? 0,
            sellDays, sellDays, committed, turnover, dailyProfit, committed > 0 && dailyProfit is not null ? dailyProfit / committed * 100m : null);
        return new(depth, history, position, metrics, salesTaxPercent, brokerFeePercent);
    }

    public async Task<IReadOnlyList<StationTradeCandidate>> FindCandidatesAsync(string hubName, decimal availableCapital,
        int maxItems = 50, decimal minimumSpreadAfterFeesPercent = 2m, decimal minimumDailyVolume = 10m,
        decimal salesTaxPercent = 3.6m, decimal brokerFeePercent = 3m, CancellationToken cancellationToken = default)
    {
        var hub = market.ResolveHub(hubName); maxItems = Math.Clamp(maxItems, 1, 100);
        var candidates = new List<StationTradeCandidate>();
        foreach (var quote in await market.CachedQuotesAsync(hub.Name, cancellationToken))
        {
            if (quote.DepthBuy is null || quote.DepthSell is null || quote.DepthBuy <= 0) continue;
            var profit = quote.DepthSell.Value * (1m - (salesTaxPercent + brokerFeePercent) / 100m)
                - quote.DepthBuy.Value * (1m + brokerFeePercent / 100m);
            var spread = profit / quote.DepthBuy.Value * 100m;
            var history = await market.CachedHistoryAsync(quote.TypeId, hub.RegionId, 30, cancellationToken);
            if (spread < minimumSpreadAfterFeesPercent || history.AverageDailyVolume < minimumDailyVolume) continue;
            var buyDepth = quote.DepthBuy.Value * quote.BuyVolume * .05m;
            var sellDepth = quote.DepthSell.Value * quote.SellVolume * .05m;
            var size = Math.Max(0, Math.Min(availableCapital / 5m, Math.Min(buyDepth, sellDepth)));
            if (size <= 0) continue;
            var unitsPerDay = Math.Min(history.AverageDailyVolume * .05m, size / quote.DepthBuy.Value);
            var dailyProfit = unitsPerDay * profit;
            candidates.Add(new(quote.TypeId, quote.TypeName, hub.Name, spread, history.AverageDailyVolume,
                buyDepth, sellDepth, size, dailyProfit, size == 0 ? 0 : dailyProfit / size * 100m));
        }
        return candidates.OrderByDescending(x => x.ExpectedDailyProfit).ThenByDescending(x => x.ExpectedReturnOnCapital)
            .Take(maxItems).ToArray();
    }

    private async Task<JsonElement[]> TryReadAsync(long characterId, string aspect, CharacterDataQuery query,
        CancellationToken cancellationToken)
    {
        try { return (await data.ReadAsync(characterId, aspect, query, cancellationToken)).Data.EnumerateArray().Select(x => x.Clone()).ToArray(); }
        catch (FileNotFoundException) { return []; }
    }

    private static decimal? WeightedAverage(JsonElement[] rows)
    {
        var quantity = rows.Sum(x => Long(x, "quantity"));
        return quantity == 0 ? null : rows.Sum(x => Decimal(x, "unit_price") * Long(x, "quantity")) / quantity;
    }
    private static long Long(JsonElement value, string name) => value.TryGetProperty(name, out var x) ? x.GetInt64() : 0;
    private static decimal Decimal(JsonElement value, string name) => value.TryGetProperty(name, out var x) ? x.GetDecimal() : 0;
    private static bool Bool(JsonElement value, string name) => value.TryGetProperty(name, out var x) && x.GetBoolean();
    private static DateTimeOffset Date(JsonElement value, string name) => value.TryGetProperty(name, out var x)
        ? DateTimeOffset.Parse(x.GetString()!) : DateTimeOffset.UtcNow;
}
