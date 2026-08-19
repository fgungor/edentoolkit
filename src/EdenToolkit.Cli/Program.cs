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
            ["name", "id", var id] when long.TryParse(id, out var parsed) => await services.Sde.FindByIdAsync(parsed) ?? throw new KeyNotFoundException($"No SDE name found for ID {parsed}."),
            ["name", "search", var query, .. var rest] => await services.Sde.SearchAsync(query, GetLimit(rest)),
            ["character", "add", .. var rest] => await AddCharacterAsync(services, rest),
            ["character", "list"] => await services.Characters.ListAsync(),
            ["character", "remove", var id] when long.TryParse(id, out var parsed) => await RemoveCharacterAsync(services, parsed),
            ["character", "sync", "all", .. var rest] => await SyncAllAsync(services, rest.Contains("--refresh")),
            ["character", "sync", var id, .. var rest] when long.TryParse(id, out var parsed) => await services.Tracking.SyncAsync(parsed, rest.Contains("--refresh")),
            ["character", "show", var id, var kind] when long.TryParse(id, out var parsed) => await services.Tracking.ReadAsync(parsed, kind),
            ["character", "query", var id, var kind, .. var rest] when long.TryParse(id, out var parsed) =>
                await services.Tracking.QueryAsync(parsed, kind, GetCharacterQuery(rest)),
            ["market", "quote", var item, .. var rest] => await services.Market.GetQuoteAsync(item,
                GetOption(rest, "--hub") ?? "Hek", GetIntOption(rest, "--days") ?? 30, rest.Contains("--refresh")),
            ["market", "compare", var item, .. var rest] => await services.Market.CompareHubsAsync(item,
                (GetOption(rest, "--hubs") ?? "Hek,Jita").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                GetIntOption(rest, "--days") ?? 30, rest.Contains("--refresh")),
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

static async Task<IReadOnlyList<CharacterSyncResult>> SyncAllAsync(EdenServices services, bool refresh)
{
    var results = new List<CharacterSyncResult>();
    foreach (var character in await services.Characters.ListAsync())
        results.Add(await services.Tracking.SyncAsync(character.CharacterId, refresh));
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
    if (args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) is { } candidate && long.TryParse(candidate, out var parsed))
        characterId = parsed;
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
  eden name id <id>
  eden name search <text> [--limit <1-100>]
  eden character add [--client-id <id>] [--redirect-uri <uri>] [--no-browser]
  eden character list
  eden character remove <character-id>
  eden character sync <character-id|all> [--refresh]
  eden character show <character-id> <location|assets|wallet|skills|transactions|jobs|journal|orders|order-history>
  eden character query <character-id> <aspect> [--limit N] [--offset N]
                       [--type-id ID] [--location-id ID] [--min-level N]
                       [--side buy|sell] [--status STATUS] [--from DATE] [--to DATE]
  eden market quote <item-name-or-type-id> [--hub Hek|Jita|Dodixie|Amarr] [--days N] [--refresh]
  eden market compare <item-name-or-type-id> [--hubs Hek,Jita,Dodixie,Amarr] [--days N]
  eden inventory value [character-id] [--hub Hek|Jita|Dodixie|Amarr] [--location-id ID]
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
