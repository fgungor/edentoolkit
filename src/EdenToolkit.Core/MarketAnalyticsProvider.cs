namespace EdenToolkit.Core;

public interface IMarketAnalyticsProvider
{
    bool Enabled { get; }
    Task<TradeActivity?> GetTradeActivityAsync(int typeId, long locationId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken = default);
    Task<AnalyticsPriceHistory?> GetPriceHistoryAsync(int typeId, int regionId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken = default);
    Task<MarketPercentiles?> GetPercentilesAsync(int typeId, int regionId,
        CancellationToken cancellationToken = default);
}

public sealed class DisabledMarketAnalyticsProvider : IMarketAnalyticsProvider
{
    public bool Enabled => false;
    public Task<TradeActivity?> GetTradeActivityAsync(int typeId, long locationId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) => Task.FromResult<TradeActivity?>(null);
    public Task<AnalyticsPriceHistory?> GetPriceHistoryAsync(int typeId, int regionId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) => Task.FromResult<AnalyticsPriceHistory?>(null);
    public Task<MarketPercentiles?> GetPercentilesAsync(int typeId, int regionId, CancellationToken cancellationToken = default) => Task.FromResult<MarketPercentiles?>(null);
}
