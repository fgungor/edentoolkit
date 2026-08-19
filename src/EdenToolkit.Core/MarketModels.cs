namespace EdenToolkit.Core;

public sealed record MarketHub(string Name, long LocationId, int RegionId);

public sealed record MarketQuote(int TypeId, string TypeName, string Hub, long LocationId, int RegionId,
    decimal? BestBuy, decimal? BestSell, decimal? DepthBuy, decimal? DepthSell,
    long BuyVolume, long SellVolume, decimal? Spread, decimal? SpreadPercent, DateTimeOffset Timestamp);

public sealed record MarketHistoryDay(DateOnly Date, decimal Average, decimal Highest, decimal Lowest,
    long Volume, long OrderCount);

public sealed record MarketHistoryStats(int TypeId, int RegionId, int Days, decimal? AveragePrice,
    decimal? Highest, decimal? Lowest, decimal AverageDailyVolume, decimal AverageDailyOrders,
    long TotalVolume, IReadOnlyList<MarketHistoryDay> History);

public sealed record MarketQuoteAnalysis(MarketQuote Quote, MarketHistoryStats History);

public sealed record InventoryValueLine(int TypeId, string Name, long Quantity, decimal? UnitPrice,
    decimal Value, bool HasMarketQuote);

public sealed record InventoryValuation(long CharacterId, string Hub, string Valuation, long? LocationId,
    decimal ImmediateLiquidationValue, decimal ReplacementValue, decimal SelectedValue,
    int ValuedTypes, int UnpricedTypes, IReadOnlyList<InventoryValueLine> Items, DateTimeOffset Timestamp);
