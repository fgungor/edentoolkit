using System.Text.Json;
using EdenToolkit.Core;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    try
    {
        if (args is [] or ["help"] or ["--help"] or ["-h"]) { PrintHelp(); return 0; }
        using var services = new EdenServices();
        object output = args switch
        {
            ["esi", "get", var path, .. var rest] => await GetEsiAsync(services, path, rest.Contains("--refresh")),
            ["status"] => await GetEsiAsync(services, "latest/status/", false),
            ["sde", "update", .. var rest] => await services.Sde.UpdateAsync(rest.Contains("--force")),
            ["sde", "status"] => await services.Sde.StatusAsync(),
            ["name", "id", var kind, var id] when long.TryParse(id, out var parsed) =>
                await services.Sde.FindByIdAsync(parsed, kind) ?? throw new KeyNotFoundException($"No SDE {kind} name found for ID {parsed}."),
            ["name", "id", var id] when long.TryParse(id, out var parsed) => await services.Sde.FindAllByIdAsync(parsed),
            ["name", "search", var query, .. var rest] => await services.Sde.SearchAsync(query, GetLimit(rest)),
            ["character", "add", .. var rest] => await AddCharacterAsync(services, rest),
            ["character", "list"] => await services.Characters.ListAsync(),
            ["character", "remove", var character] => await RemoveCharacterAsync(services, (await services.Characters.ResolveAsync(character)).CharacterId),
            ["character", "sync", "all", .. var rest] => await SyncAllAsync(services, rest.Contains("--refresh")),
            ["character", "sync", var character, .. var rest] => await services.SyncCharacterAsync((await services.Characters.ResolveAsync(character)).CharacterId, rest.Contains("--refresh")),
            ["character", "show", var character, var kind] => await services.Tracking.ReadAsync((await services.Characters.ResolveAsync(character)).CharacterId, kind),
            ["character", "query", var character, var kind, .. var rest] =>
                await services.Tracking.QueryAsync((await services.Characters.ResolveAsync(character)).CharacterId, kind, GetCharacterQuery(rest)),
            ["corporation", "list"] => await services.Corporations.ListAsync(),
            ["corporation", "sync", var corporation, .. var rest] => await services.CorporationTracking.SyncAsync(corporation, rest.Contains("--refresh")),
            ["corporation", "show", var corporation, var kind] => await services.CorporationTracking.QueryAsync(corporation, kind, new(Limit: 100000)),
            ["corporation", "query", var corporation, var kind, .. var rest] =>
                await services.CorporationTracking.QueryAsync(corporation, kind, GetCharacterQuery(rest)),
            ["production", "capacity", var corporation, var item] => await services.ProductionCapacity.CalculateAsync(corporation, item),
            ["fittings", var character, .. var rest] => await services.Fittings.CharacterFittingsAsync(character,
                GetOption(rest, "--query")),
            ["market", "quote", var item, .. var rest] => await services.Market.GetQuoteAsync(item,
                GetOption(rest, "--hub") ?? "Hek", GetIntOption(rest, "--days") ?? 30, rest.Contains("--refresh")),
            ["market", "compare", var item, .. var rest] => await services.Market.CompareHubsAsync(item,
                (GetOption(rest, "--hubs") ?? "Hek,Jita").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                GetIntOption(rest, "--days") ?? 30, rest.Contains("--refresh")),
            ["market", "depth", var item, .. var rest] => await services.Market.GetDepthAsync(item,
                GetOption(rest, "--hub") ?? "Jita", GetIntOption(rest, "--levels") ?? 10, rest.Contains("--refresh")),
            ["market", "history", var item, .. var rest] => await services.Market.GetHistoryAsync(item,
                GetOption(rest, "--region") ?? GetOption(rest, "--hub") ?? "Jita", GetIntOption(rest, "--days") ?? 30,
                rest.Contains("--refresh")),
            ["market", "position", var item, var character, .. var rest] => await services.StationTrading.GetPositionAsync(item,
                character, GetOption(rest, "--hub") ?? "Jita", GetIntOption(rest, "--days") ?? 7),
            ["market", "order", var id, .. var rest] when long.TryParse(id, out var orderId) =>
                await services.StationTrading.GetOrderStateAsync(orderId, GetOption(rest, "--character"), rest.Contains("--refresh")),
            ["market", "focus", var item, .. var rest] => await services.StationTrading.GetStateAsync(item,
                GetOption(rest, "--hub") ?? "Jita", GetOption(rest, "--character"),
                GetDecimalOption(rest, "--sales-tax") ?? 3.6m, GetDecimalOption(rest, "--broker-fee") ?? 3m,
                rest.Contains("--refresh")),
            ["market", "candidates", .. var rest] => await services.StationTrading.FindCandidatesAsync(
                GetOption(rest, "--hub") ?? "Jita", GetDecimalOption(rest, "--capital")
                    ?? throw new ArgumentException("market candidates requires --capital ISK."), GetIntOption(rest, "--max-items") ?? 50,
                GetDecimalOption(rest, "--min-spread") ?? 2m, GetDecimalOption(rest, "--min-volume") ?? 10m,
                GetDecimalOption(rest, "--sales-tax") ?? 3.6m, GetDecimalOption(rest, "--broker-fee") ?? 3m),
            ["inventory", "value", .. var rest] => await ValueInventoryAsync(services, rest),
            _ => throw new ArgumentException("Unknown or incomplete command. Run 'eden help'.")
        };
        Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
        return 0;
    }
    catch (Exception exception) { Console.Error.WriteLine(exception.Message); return exception is ArgumentException ? 2 : 1; }
}

static async Task<TrackedCharacter> AddCharacterAsync(EdenServices services, string[] args)
{
    var clientId = GetOption(args, "--client-id") ?? services.Options.EveClientId;
    var redirect = GetOption(args, "--redirect-uri") ?? Environment.GetEnvironmentVariable("EDEN_EVE_REDIRECT_URI")
        ?? "http://localhost:52731/callback/";
    return await services.Sso.AuthorizeAsync(clientId, redirect, uri =>
    {
        Console.Error.WriteLine($"Authorize the character at:\n{uri}\n");
        if (!args.Contains("--no-browser")) EveSsoService.OpenBrowser(uri);
    });
}

static async Task<IReadOnlyList<CharacterAndCorporationSyncResult>> SyncAllAsync(EdenServices services, bool refresh)
{
    var results = new List<CharacterAndCorporationSyncResult>();
    foreach (var character in await services.Characters.ListAsync())
        results.Add(await services.SyncCharacterAsync(character.CharacterId, refresh));
    return results;
}

static async Task<object> RemoveCharacterAsync(EdenServices services, long characterId)
{
    var removed = await services.Characters.RemoveAsync(characterId);
    await services.CharacterData.DeleteCharacterAsync(characterId);
    return new { characterId, removed, cachedDataPurged = true };
}

static async Task<InventoryValuation> ValueInventoryAsync(EdenServices services, string[] args)
{
    long characterId;
    if (args.FirstOrDefault() is { } candidate && !candidate.StartsWith("--", StringComparison.Ordinal))
        characterId = (await services.Characters.ResolveAsync(candidate)).CharacterId;
    else
    {
        var characters = await services.Characters.ListAsync();
        characterId = characters.Count == 1 ? characters[0].CharacterId
            : throw new ArgumentException("Specify a character ID when zero or multiple characters are tracked.");
    }
    return await services.Inventory.ValueAsync(characterId, GetOption(args, "--hub") ?? "Hek",
        GetLongOption(args, "--location-id"), GetOption(args, "--valuation") ?? "depth-buy");
}

static string? GetOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static CharacterDataQuery GetCharacterQuery(string[] args) => new(
    Limit: GetIntOption(args, "--limit") ?? 1000,
    Offset: GetIntOption(args, "--offset") ?? 0,
    TypeId: GetLongOption(args, "--type-id"),
    LocationId: GetLongOption(args, "--location-id"),
    MinimumSkillLevel: GetIntOption(args, "--min-level"),
    IsBuy: GetSide(args),
    Status: GetOption(args, "--status"),
    From: GetDateOption(args, "--from"),
    To: GetDateOption(args, "--to"));

static int? GetIntOption(string[] args, string name) => int.TryParse(GetOption(args, name), out var value) ? value : null;
static long? GetLongOption(string[] args, string name) => long.TryParse(GetOption(args, name), out var value) ? value : null;
static decimal? GetDecimalOption(string[] args, string name) => decimal.TryParse(GetOption(args, name),
    System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
static DateTimeOffset? GetDateOption(string[] args, string name) => DateTimeOffset.TryParse(GetOption(args, name), out var value) ? value : null;
static bool? GetSide(string[] args) => GetOption(args, "--side")?.ToLowerInvariant() switch
{
    "buy" => true,
    "sell" => false,
    null => null,
    _ => throw new ArgumentException("--side must be 'buy' or 'sell'.")
};

static async Task<object> GetEsiAsync(EdenServices services, string path, bool refresh)
{
    var result = await services.Esi.GetAsync(path, refresh);
    return new { result.Data, cache = new { result.FromCache, result.IsStale, result.ExpiresAt } };
}

static int GetLimit(string[] args)
{
    var index = Array.IndexOf(args, "--limit");
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var limit) ? limit : 20;
}

static void PrintHelp() => Console.WriteLine("""
EdenToolkit — cached EVE Online public data

Usage:
  eden status
  eden esi get <relative-path-and-query> [--refresh]
  eden sde update [--force]
  eden sde status
  eden name id [kind] <id>
  eden name search <text> [--limit <1-100>]
  eden character add [--client-id <id>] [--redirect-uri <uri>] [--no-browser]
  eden character list
  eden character remove <character-name-or-id>
  eden character sync <character-name-or-id|all> [--refresh]
  eden character show <character-name-or-id> <location|assets|wallet|skills|transactions|jobs|journal|orders|order-history|pi>
  eden character query <character-name-or-id> <aspect> [--limit N] [--offset N]
                       [--type-id ID] [--location-id ID] [--min-level N]
                       [--side buy|sell] [--status STATUS] [--from DATE] [--to DATE]
  eden corporation list
  eden corporation sync <corporation-name-or-id> [--refresh]
  eden corporation show <corporation-name-or-id> <assets|blueprints|wallet|transactions|jobs|journal|orders|order-history>
  eden corporation query <corporation-name-or-id> <aspect> [the same filters as character query]
  eden production capacity <corporation-name-or-id> <product-or-blueprint-name-or-type-id>
  eden fittings <character-name-or-id> [--query <fitting-or-hull-name>]
  eden market quote <item-name-or-type-id> [--hub Hek|Jita|Dodixie|Amarr] [--days N] [--refresh]
  eden market compare <item-name-or-type-id> [--hubs Hek,Jita,Dodixie,Amarr] [--days N]
  eden market depth <item> [--hub HUB] [--levels N] [--refresh]
  eden market history <item> [--region HUB|REGION_ID] [--days N] [--refresh]
  eden market position <item> <character> [--hub HUB] [--days N]
  eden market order <order-id> [--character CHARACTER] [--refresh]
  eden market focus <item> [--hub HUB] [--character CHARACTER] [--sales-tax PCT] [--broker-fee PCT] [--refresh]
  eden market candidates --hub HUB --capital ISK [--max-items N] [--min-spread PCT] [--min-volume UNITS]
  eden inventory value [character-name-or-id] [--hub Hek|Jita|Dodixie|Amarr] [--location-id ID]
                       [--valuation best-buy|best-sell|depth-buy|depth-sell]

Environment:
  EDEN_CACHE_DIR                 Override the local cache directory
  EDEN_USER_AGENT                Set an ESI-compliant identifying User-Agent
  EDEN_ESI_COMPATIBILITY_DATE    Override the ESI compatibility date
  EDEN_EVE_CLIENT_ID             Override the built-in EVE SSO application client ID
  EDEN_EVE_REDIRECT_URI          Registered loopback callback URI

Examples:
  eden esi get "latest/markets/10000002/orders/?type_id=34&order_type=all&page=1"
  eden name search "Raven"
  eden market quote "Hobgoblin II" --hub Hek
""");
