using System.Text.Json;

namespace EdenToolkit.Core;

public sealed class InventoryService(CharacterDataRepository characterData, MarketDataService market, SdeService sde)
{
    public async Task<InventoryValuation> ValueAsync(long characterId, string hub = "Hek", long? locationId = null,
        string valuation = "depth-buy", CancellationToken cancellationToken = default)
    {
        valuation = NormalizeValuation(valuation);
        var snapshot = await characterData.ReadAsync(characterId, "assets",
            new(Limit: 100000, LocationId: locationId), cancellationToken);
        var groups = snapshot.Data.EnumerateArray().GroupBy(item => item.GetProperty("type_id").GetInt32())
            .Select(group => new { TypeId = group.Key, Quantity = group.Sum(item => item.GetProperty("quantity").GetInt64()) })
            .OrderBy(group => group.TypeId).ToArray();
        var lines = new List<InventoryValueLine>(groups.Length);
        decimal liquidation = 0, replacement = 0, selected = 0;
        foreach (var group in groups)
        {
            var type = await sde.FindByIdAsync(group.TypeId, "types", cancellationToken);
            var name = type?.Name ?? group.TypeId.ToString();
            var quote = await market.GetCachedOrRefreshQuoteAsync(group.TypeId, name, hub, TimeSpan.FromMinutes(5), cancellationToken);
            var liquidationPrice = quote.DepthBuy ?? quote.BestBuy;
            var replacementPrice = quote.DepthSell ?? quote.BestSell;
            liquidation += (liquidationPrice ?? 0) * group.Quantity;
            replacement += (replacementPrice ?? 0) * group.Quantity;
            var unitPrice = valuation switch
            {
                "best-buy" => quote.BestBuy,
                "best-sell" => quote.BestSell,
                "depth-buy" => quote.DepthBuy ?? quote.BestBuy,
                "depth-sell" => quote.DepthSell ?? quote.BestSell,
                _ => null
            };
            var value = (unitPrice ?? 0) * group.Quantity;
            selected += value;
            lines.Add(new(group.TypeId, name, group.Quantity, unitPrice, value, unitPrice is not null));
        }
        var ordered = lines.OrderByDescending(line => line.Value).ThenBy(line => line.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(characterId, hub, valuation, locationId, liquidation, replacement, selected,
            ordered.Count(line => line.HasMarketQuote), ordered.Count(line => !line.HasMarketQuote), ordered, DateTimeOffset.UtcNow);
    }

    private static string NormalizeValuation(string value) => value.ToLowerInvariant() switch
    {
        "buy" or "best-buy" => "best-buy",
        "sell" or "best-sell" => "best-sell",
        "depth-buy" => "depth-buy",
        "depth-sell" => "depth-sell",
        _ => throw new ArgumentException("Valuation must be best-buy, best-sell, depth-buy, or depth-sell.", nameof(value))
    };
}
