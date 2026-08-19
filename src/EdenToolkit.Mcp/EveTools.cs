using System.ComponentModel;
using EdenToolkit.Core;
using ModelContextProtocol.Server;

namespace EdenToolkit.Mcp;

[McpServerToolType]
public sealed class EveTools(EdenServices services)
{
    [McpServerTool(Name = "eve_esi_get"), Description("Query an unauthenticated public EVE ESI GET endpoint with CCP-aware caching. Use a relative path such as 'latest/status/'.")]
    public async Task<object> EsiGet([Description("Relative ESI path and query string; absolute URLs are rejected.")] string path,
        [Description("Bypass a fresh local cache entry and revalidate with ESI.")] bool refresh = false, CancellationToken cancellationToken = default)
    {
        var result = await services.Esi.GetAsync(path, refresh, cancellationToken);
        return new { result.Data, cache = new { result.FromCache, result.IsStale, result.ExpiresAt } };
    }

    [McpServerTool(Name = "eve_server_status"), Description("Get Tranquility server status and online player count from ESI.")]
    public Task<object> ServerStatus(CancellationToken cancellationToken = default) => EsiGet("latest/status/", cancellationToken: cancellationToken);

    [McpServerTool(Name = "eve_sde_update"), Description("Download the latest official JSONL EVE Static Data Export and rebuild the local English-name index. This is a large download.")]
    public Task<SdeStatus> UpdateSde([Description("Download even if the cached SDE appears current.")] bool force = false,
        CancellationToken cancellationToken = default) => services.Sde.UpdateAsync(force, cancellationToken);

    [McpServerTool(Name = "eve_sde_status"), Description("Report whether the SDE name index is installed, when it was updated, and its size.")]
    public Task<SdeStatus> SdeStatus(CancellationToken cancellationToken = default) => services.Sde.StatusAsync(cancellationToken);

    [McpServerTool(Name = "eve_name_by_id"), Description("Resolve an EVE type, group, category, market group, region, constellation, solar system, or NPC corporation ID to its official English SDE name.")]
    public Task<SdeName?> NameById([Description("Numeric EVE entity ID.")] long id, CancellationToken cancellationToken = default) => services.Sde.FindByIdAsync(id, cancellationToken);

    [McpServerTool(Name = "eve_search_names"), Description("Search official English SDE names. Prefix matches rank first.")]
    public Task<IReadOnlyList<SdeName>> SearchNames([Description("Case-insensitive substring to find.")] string query,
        [Description("Maximum matches, from 1 to 100.")] int limit = 20, CancellationToken cancellationToken = default) => services.Sde.SearchAsync(query, limit, cancellationToken);

    [McpServerTool(Name = "eve_sync_character"), Description("Refresh tracked character data including assets, wallet activity, industry jobs, and own market orders from ESI and transactionally store it in local SQLite.")]
    public Task<CharacterSyncResult> SyncCharacter([Description("Tracked EVE character ID.")] long characterId,
        [Description("Force ESI revalidation instead of accepting fresh HTTP cache entries.")] bool refresh = false,
        CancellationToken cancellationToken = default) => services.Tracking.SyncAsync(characterId, refresh, cancellationToken);

    [McpServerTool(Name = "eve_character_data"), Description("Query previously synced character location, assets, wallet, skills, wallet transactions, or industry jobs from local SQLite without calling ESI.")]
    public Task<CharacterSnapshot> CharacterData([Description("Tracked EVE character ID.")] long characterId,
        [Description("One of: location, assets, wallet, skills, transactions, jobs, journal, orders, order-history.")] string aspect,
        [Description("Maximum asset or skill rows to return.")] int limit = 1000,
        [Description("Asset or skill row offset.")] int offset = 0,
        [Description("Optional asset type ID or skill type ID.")] long? typeId = null,
        [Description("Optional asset location ID.")] long? locationId = null,
        [Description("Optional minimum trained skill level.")] int? minimumSkillLevel = null,
        [Description("Optional wallet transaction side: true for buys, false for sells.")] bool? isBuy = null,
        [Description("Optional industry job status.")] string? status = null,
        [Description("Optional inclusive lower date bound.")] DateTimeOffset? from = null,
        [Description("Optional inclusive upper date bound.")] DateTimeOffset? to = null,
        CancellationToken cancellationToken = default) => services.Tracking.QueryAsync(characterId, aspect,
            new(limit, offset, typeId, locationId, minimumSkillLevel, isBuy, status, from, to), cancellationToken);

    [McpServerTool(Name = "eve_market_quote"), Description("Get a compact current hub quote and regional historical statistics for an EVE item. Calculates best prices, 5%-depth VWAP prices, spread, and relevant order volume without exposing the raw order book.")]
    public Task<MarketQuoteAnalysis> MarketQuote([Description("Exact item name or numeric type ID.")] string item,
        [Description("Supported hub: Hek, Jita, Dodixie, or Amarr.")] string hub = "Hek",
        [Description("Number of recent regional history days to summarize.")] int historyDays = 30,
        [Description("Force ESI revalidation.")] bool refresh = false,
        CancellationToken cancellationToken = default) => services.Market.GetQuoteAsync(item, hub, historyDays, refresh, cancellationToken);

    [McpServerTool(Name = "eve_compare_market_hubs"), Description("Compare compact market quotes for an item across any subset of Hek, Jita, Dodixie, and Amarr.")]
    public Task<IReadOnlyList<MarketQuoteAnalysis>> CompareMarketHubs([Description("Exact item name or numeric type ID.")] string item,
        [Description("Comma-separated hubs; defaults to Hek,Jita.")] string hubs = "Hek,Jita",
        [Description("Recent regional history days.")] int historyDays = 30,
        CancellationToken cancellationToken = default) => services.Market.CompareHubsAsync(item,
            hubs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), historyDays, false, cancellationToken);

    [McpServerTool(Name = "eve_value_inventory"), Description("Deterministically value a character's SQLite-cached assets at Hek or Jita using explicit best-buy, best-sell, depth-buy, or depth-sell pricing.")]
    public Task<InventoryValuation> ValueInventory([Description("Tracked EVE character ID.")] long characterId,
        [Description("Supported hub: Hek, Jita, Dodixie, or Amarr.")] string hub = "Hek",
        [Description("Optional asset location ID filter.")] long? locationId = null,
        [Description("One of best-buy, best-sell, depth-buy, depth-sell.")] string valuation = "depth-buy",
        CancellationToken cancellationToken = default) => services.Inventory.ValueAsync(characterId, hub, locationId, valuation, cancellationToken);
}
