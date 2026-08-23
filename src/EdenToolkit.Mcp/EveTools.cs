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

    [McpServerTool(Name = "eve_name_by_id"), Description("Resolve an EVE ID within an explicit SDE namespace. IDs are not globally unique across namespaces.")]
    public async Task<IReadOnlyList<SdeName>> NameById([Description("Numeric EVE entity ID.")] long id,
        [Description("Optional exact namespace such as types, marketGroups, groups, categories, mapRegions, mapConstellations, mapSolarSystems, mapPlanets, or npcCorporations. Omit to return every namespace match.")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(kind)) return await services.Sde.FindAllByIdAsync(id, cancellationToken);
        var match = await services.Sde.FindByIdAsync(id, kind, cancellationToken);
        return match is null ? [] : [match];
    }

    [McpServerTool(Name = "eve_search_names"), Description("Search official English SDE names. Prefix matches rank first.")]
    public Task<IReadOnlyList<SdeName>> SearchNames([Description("Case-insensitive substring to find.")] string query,
        [Description("Maximum matches, from 1 to 100.")] int limit = 20, CancellationToken cancellationToken = default) => services.Sde.SearchAsync(query, limit, cancellationToken);

    [McpServerTool(Name = "eve_list_characters"), Description("List tracked EVE characters and their names and IDs. Use this to discover available characters when a user does not specify one.")]
    public Task<IReadOnlyList<TrackedCharacter>> ListCharacters(CancellationToken cancellationToken = default) =>
        services.Characters.ListAsync(cancellationToken);

    [McpServerTool(Name = "eve_sync_character"), Description("Refresh tracked character data including assets, wallet activity, industry jobs, market orders, saved fittings, and planetary industry. If the character is a director, also sync corporation data. Store all results in local SQLite.")]
    public async Task<CharacterAndCorporationSyncResult> SyncCharacter([Description("Tracked character ID, full name, or unique first name. If the character is a corporation director, corporation data is also synced.")] string character,
        [Description("Force ESI revalidation instead of accepting fresh HTTP cache entries.")] bool refresh = false,
        CancellationToken cancellationToken = default) => await services.SyncCharacterAsync(
            (await services.Characters.ResolveAsync(character, cancellationToken)).CharacterId, refresh, cancellationToken);

    [McpServerTool(Name = "eve_character_data"), Description("Query previously synced character location, assets, wallet, skills, transactions, jobs, orders, or planetary-industry data from local SQLite without calling ESI.")]
    public async Task<CharacterSnapshot> CharacterData([Description("Tracked character ID, full name, or unique first name.")] string character,
        [Description("One of: location, assets, wallet, skills, transactions, jobs, journal, orders, order-history, fittings, pi.")] string aspect,
        [Description("Maximum asset or skill rows to return.")] int limit = 1000,
        [Description("Asset or skill row offset.")] int offset = 0,
        [Description("Optional asset type ID or skill type ID.")] long? typeId = null,
        [Description("Optional asset location ID.")] long? locationId = null,
        [Description("Optional minimum trained skill level.")] int? minimumSkillLevel = null,
        [Description("Optional wallet transaction side: true for buys, false for sells.")] bool? isBuy = null,
        [Description("Optional industry job status.")] string? status = null,
        [Description("Optional inclusive lower date bound.")] DateTimeOffset? from = null,
        [Description("Optional inclusive upper date bound.")] DateTimeOffset? to = null,
        CancellationToken cancellationToken = default) => await services.Tracking.QueryAsync(
            (await services.Characters.ResolveAsync(character, cancellationToken)).CharacterId, aspect,
            new(limit, offset, typeId, locationId, minimumSkillLevel, isBuy, status, from, to), cancellationToken);

    [McpServerTool(Name = "eve_pi_data"), Description("Query cached planetary-industry colonies, production schematics and inputs/outputs, extractor programs, routes, and pin/launchpad inventory for a tracked character.")]
    public async Task<CharacterSnapshot> PlanetaryIndustry(
        [Description("Tracked character ID, full name, or unique first name.")] string character,
        CancellationToken cancellationToken = default) => await services.PlanetaryIndustry.ReadAsync(
            (await services.Characters.ResolveAsync(character, cancellationToken)).CharacterId, cancellationToken);

    [McpServerTool(Name = "eve_character_fittings"), Description("Query cached character-saved fittings enriched with official ship, module, charge, drone, fighter, and cargo type names and slot categories.")]
    public Task<IReadOnlyList<FittingView>> CharacterFittings(
        [Description("Tracked character ID, full name, or unique first name.")] string character,
        [Description("Optional case-insensitive fitting-name or hull-name filter.")] string? query = null,
        CancellationToken cancellationToken = default) => services.Fittings.CharacterFittingsAsync(character, query, cancellationToken);

    [McpServerTool(Name = "eve_fitting_type_details"), Description("Get cached public ESI type details, including dogma attributes and effects, for a hull, module, charge, drone, or other fitting item.")]
    public Task<object> FittingTypeDetails([Description("EVE type ID from a fitting result.")] long typeId,
        [Description("Force ESI revalidation.")] bool refresh = false,
        CancellationToken cancellationToken = default) => services.Fittings.TypeDetailsAsync(typeId, refresh, cancellationToken);

    [McpServerTool(Name = "eve_list_corporations"), Description("List corporations discovered through tracked director characters.")]
    public Task<IReadOnlyList<TrackedCorporation>> ListCorporations(CancellationToken cancellationToken = default) =>
        services.Corporations.ListAsync(cancellationToken);

    [McpServerTool(Name = "eve_sync_corporation"), Description("Sync a previously discovered corporation's assets, owned blueprints with runs/ME/TE, wallet divisions, transactions and journal, industry/research jobs, and market orders.")]
    public Task<CorporationSyncResult> SyncCorporation([Description("Corporation name or ID.")] string corporation,
        [Description("Force ESI revalidation.")] bool refresh = false, CancellationToken cancellationToken = default) =>
        services.CorporationTracking.SyncAsync(corporation, refresh, cancellationToken);

    [McpServerTool(Name = "eve_corporation_data"), Description("Query previously synced corporation data from the same local SQLite tables used for character data, without calling ESI.")]
    public Task<CharacterSnapshot> CorporationData([Description("Corporation name or ID.")] string corporation,
        [Description("One of: assets, blueprints, wallet, transactions, jobs, journal, orders, order-history.")] string aspect,
        [Description("Maximum rows.")] int limit = 1000, [Description("Row offset.")] int offset = 0,
        [Description("Optional type ID.")] long? typeId = null, [Description("Optional location ID.")] long? locationId = null,
        [Description("Optional transaction/order side: true for buys, false for sells.")] bool? isBuy = null,
        [Description("Optional job/order/journal status.")] string? status = null,
        [Description("Optional inclusive lower date bound.")] DateTimeOffset? from = null,
        [Description("Optional inclusive upper date bound.")] DateTimeOffset? to = null,
        CancellationToken cancellationToken = default) => services.CorporationTracking.QueryAsync(corporation, aspect,
            new(limit, offset, typeId, locationId, null, isBuy, status, from, to), cancellationToken);

    [McpServerTool(Name = "eve_manufacturing_recipe"), Description("Return the SDE manufacturing recipe for an exact product or blueprint type, including base materials, output quantity, and base time.")]
    public async Task<object> ManufacturingRecipe([Description("Exact product/blueprint name or type ID.")] string item,
        CancellationToken cancellationToken = default)
    {
        var matches = long.TryParse(item, out var id)
            ? new[] { await services.Sde.FindByIdAsync(id, "types", cancellationToken) }.Where(value => value is not null).Cast<SdeName>().ToArray()
            : (await services.Sde.SearchAsync(item, 100, cancellationToken)).Where(value => value.Kind == "types" && value.Name.Equals(item, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new KeyNotFoundException($"No exact item type matches '{item}'.");
        var byBlueprint = await services.Sde.FindManufacturingByBlueprintAsync(matches[0].Id, cancellationToken);
        var recipes = byBlueprint is null ? await services.Sde.FindManufacturingByProductAsync(matches[0].Id, cancellationToken) : [byBlueprint];
        return new { item = matches[0], recipes };
    }

    [McpServerTool(Name = "eve_production_capacity"), Description("Calculate how many units a corporation can manufacture from its cached blueprint copies and cached asset stacks, applying each blueprint's remaining runs and material efficiency to SDE recipes.")]
    public Task<ProductionCapacity> ProductionCapacity([Description("Corporation name or ID.")] string corporation,
        [Description("Exact product/blueprint name or type ID.")] string item,
        CancellationToken cancellationToken = default) => services.ProductionCapacity.CalculateAsync(corporation, item, cancellationToken);

    [McpServerTool(Name = "eve_market_quote"), Description("Get a compact current hub quote and regional historical statistics for an EVE item. Calculates best prices, 5%-depth VWAP prices, spread, and relevant order volume without exposing the raw order book.")]
    public Task<MarketQuoteAnalysis> MarketQuote([Description("Exact item name or numeric type ID.")] string item,
        [Description("Supported hub: Hek, Jita, Dodixie, or Amarr.")] string hub = "Hek",
        [Description("Number of recent regional history days to summarize.")] int historyDays = 30,
        [Description("Force ESI revalidation.")] bool refresh = false,
        CancellationToken cancellationToken = default) => services.Market.GetQuoteAsync(item, hub, historyDays, refresh, cancellationToken);

    [McpServerTool(Name = "eve_market_depth"), Description("Get current aggregated buy/sell price levels at a supported hub, including best prices and shallow 5%-volume depth prices.")]
    public Task<MarketDepth> MarketDepth([Description("Exact item name or numeric type ID.")] string item,
        [Description("Supported hub: Hek, Jita, Dodixie, or Amarr.")] string hub = "Jita",
        [Description("Maximum aggregated price levels per side.")] int levels = 10,
        [Description("Force ESI revalidation.")] bool refresh = false,
        CancellationToken cancellationToken = default) => services.Market.GetDepthAsync(item, hub, levels, refresh, cancellationToken);

    [McpServerTool(Name = "eve_market_history"), Description("Get cached-and-refreshed ESI daily market history and deterministic liquidity statistics for an item and region/hub.")]
    public Task<MarketHistoryStats> MarketHistory([Description("Exact item name or numeric type ID.")] string item,
        [Description("Supported hub name or numeric EVE region ID.")] string region = "Jita",
        [Description("Number of recent days, 1-3650.")] int days = 30,
        [Description("Force ESI revalidation.")] bool refresh = false,
        CancellationToken cancellationToken = default) => services.Market.GetHistoryAsync(item, region, days, refresh, cancellationToken);

    [McpServerTool(Name = "eve_trading_position"), Description("Combine SQLite-cached inventory, active orders, and durable wallet transactions into a character's station-trading position for one item.")]
    public Task<TradingPosition> TradingPosition([Description("Exact item name or numeric type ID.")] string item,
        [Description("Tracked character ID, full name, or unique first name.")] string character,
        [Description("Supported hub.")] string hub = "Jita", [Description("Recent transaction window in days.")] int days = 7,
        CancellationToken cancellationToken = default) => services.StationTrading.GetPositionAsync(item, character, hub, days, cancellationToken);

    [McpServerTool(Name = "eve_order_state"), Description("Measure a cached active order against current competition, fill progress, age, recent fill rate, and historical market turnover. Returns measurements, not a reprice decision.")]
    public Task<OrderState> OrderState([Description("Cached EVE market order ID.")] long orderId,
        [Description("Optional tracked character; omit to search all tracked characters.")] string? character = null,
        [Description("Force public market revalidation.")] bool refreshMarket = false,
        CancellationToken cancellationToken = default) => services.StationTrading.GetOrderStateAsync(orderId, character, refreshMarket, cancellationToken);

    [McpServerTool(Name = "eve_station_trade_candidates"), Description("Rank a manageable candidate set from compact cached hub snapshots using after-fee spread, liquidity, depth, capital, and expected ISK/day. Query interesting items first to populate the snapshot universe.")]
    public Task<IReadOnlyList<StationTradeCandidate>> StationTradeCandidates(
        [Description("Supported hub.")] string hub, [Description("ISK available to deploy.")] decimal availableCapital,
        [Description("Maximum candidates, 1-100.")] int maxItems = 50,
        [Description("Minimum spread after configured fees.")] decimal minimumSpreadAfterFeesPercent = 2m,
        [Description("Minimum regional average daily units.")] decimal minimumDailyVolume = 10m,
        [Description("Character sales tax percentage.")] decimal salesTaxPercent = 3.6m,
        [Description("Broker fee percentage per order side.")] decimal brokerFeePercent = 3m,
        CancellationToken cancellationToken = default) => services.StationTrading.FindCandidatesAsync(hub, availableCapital,
            maxItems, minimumSpreadAfterFeesPercent, minimumDailyVolume, salesTaxPercent, brokerFeePercent, cancellationToken);

    [McpServerTool(Name = "eve_station_trade_state"), Description("Focused station-trading view combining current depth, 30-day history, optional character position, fees, turnover, return-on-capital, and expected ISK/day metrics.")]
    public Task<StationTradeState> StationTradeState([Description("Exact item name or numeric type ID.")] string item,
        [Description("Supported hub.")] string hub = "Jita",
        [Description("Optional tracked character to include cached inventory, orders, and transactions.")] string? character = null,
        [Description("Character sales tax percentage.")] decimal salesTaxPercent = 3.6m,
        [Description("Broker fee percentage per order side.")] decimal brokerFeePercent = 3m,
        [Description("Force public market revalidation.")] bool refresh = false,
        CancellationToken cancellationToken = default) => services.StationTrading.GetStateAsync(item, hub, character,
            salesTaxPercent, brokerFeePercent, refresh, cancellationToken);

    [McpServerTool(Name = "eve_compare_market_hubs"), Description("Compare compact market quotes for an item across any subset of Hek, Jita, Dodixie, and Amarr.")]
    public Task<IReadOnlyList<MarketQuoteAnalysis>> CompareMarketHubs([Description("Exact item name or numeric type ID.")] string item,
        [Description("Comma-separated hubs; defaults to Hek,Jita.")] string hubs = "Hek,Jita",
        [Description("Recent regional history days.")] int historyDays = 30,
        CancellationToken cancellationToken = default) => services.Market.CompareHubsAsync(item,
            hubs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), historyDays, false, cancellationToken);

    [McpServerTool(Name = "eve_value_inventory"), Description("Deterministically value a character's SQLite-cached assets at Hek or Jita using explicit best-buy, best-sell, depth-buy, or depth-sell pricing.")]
    public async Task<InventoryValuation> ValueInventory([Description("Tracked character ID, full name, or unique first name.")] string character,
        [Description("Supported hub: Hek, Jita, Dodixie, or Amarr.")] string hub = "Hek",
        [Description("Optional asset location ID filter.")] long? locationId = null,
        [Description("One of best-buy, best-sell, depth-buy, depth-sell.")] string valuation = "depth-buy",
        CancellationToken cancellationToken = default) => await services.Inventory.ValueAsync(
            (await services.Characters.ResolveAsync(character, cancellationToken)).CharacterId, hub, locationId, valuation, cancellationToken);
}
