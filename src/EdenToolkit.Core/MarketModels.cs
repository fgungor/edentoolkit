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

public sealed record MarketDepthLevel(decimal Price, long Volume, int Orders);

public sealed record MarketDepth(int TypeId, string TypeName, string Hub, long LocationId, int RegionId,
    decimal? BestBuy, decimal? BestSell, decimal? SpreadPercent,
    IReadOnlyList<MarketDepthLevel> BuyLevels, IReadOnlyList<MarketDepthLevel> SellLevels,
    decimal? BuyDepthPrice, decimal? SellDepthPrice, long BuyVolume, long SellVolume,
    DateTimeOffset Timestamp);

public sealed record TradingPosition(int TypeId, string TypeName, long CharacterId, string CharacterName, string Hub,
    long Inventory, decimal InventoryEstimatedCost, int ActiveBuyOrders, int ActiveSellOrders,
    long ActiveBuyVolume, long ActiveSellVolume, decimal BuyOrderIskCommitted, decimal SellInventoryValue, decimal? AverageActualBuyPrice7d,
    decimal? AverageActualSellPrice7d, long UnitsBought7d, long UnitsSold7d, decimal? RealizedProfit7d);

public sealed record OrderState(long OrderId, long CharacterId, string CharacterName, int TypeId, string TypeName,
    string Hub, bool IsBuy, decimal OurPrice, decimal? BestCompetingPrice, decimal? DistanceFromBestPercent,
    long OriginalVolume, long RemainingVolume, decimal PercentageFilled, decimal AgeDays,
    decimal RecentFillRatePerDay, decimal EstimatedMarketDailyVolume, decimal? EstimatedDaysToFill);

public sealed record TradeMetrics(decimal? SpreadAfterFeesPercent, decimal? ExpectedProfitPerUnit,
    decimal AverageDailyVolume7d, decimal AverageDailyVolume30d, long OurBuyVolume, long OurSellVolume,
    decimal? InventoryDays, decimal? EstimatedDaysToSell, decimal CapitalCommitted,
    decimal? CapitalTurnoverRate, decimal? ExpectedProfitPerDay, decimal? ExpectedReturnOnCapital);

public sealed record StationTradeState(MarketDepth Depth, MarketHistoryStats History,
    TradingPosition? Position, TradeMetrics Metrics, decimal SalesTaxPercent, decimal BrokerFeePercent);

public sealed record StationTradeCandidate(int TypeId, string Item, string Hub, decimal SpreadAfterFeesPercent,
    decimal DailyVolume, decimal BuyDepth, decimal SellDepth, decimal SuggestedPositionSize,
    decimal ExpectedDailyProfit, decimal ExpectedReturnOnCapital);

public sealed record MarketQuoteAnalysis(MarketQuote Quote, MarketHistoryStats History);

public sealed record InventoryValueLine(int TypeId, string Name, long Quantity, decimal? UnitPrice,
    decimal Value, bool HasMarketQuote);

public sealed record InventoryValuation(long CharacterId, string Hub, string Valuation, long? LocationId,
    decimal ImmediateLiquidationValue, decimal ReplacementValue, decimal SelectedValue,
    int ValuedTypes, int UnpricedTypes, IReadOnlyList<InventoryValueLine> Items, DateTimeOffset Timestamp);
