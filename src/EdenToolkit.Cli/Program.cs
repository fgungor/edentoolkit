using System.Text.Json;
using EdenToolkit.Core;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    using var services = new EdenServices();
    try
    {
        if (args is [] or ["help"] or ["--help"] or ["-h"]) { PrintHelp(); return 0; }
        object output = args switch
        {
            ["esi", "get", var path, .. var rest] => await GetEsiAsync(services, path, rest.Contains("--refresh")),
            ["status"] => await GetEsiAsync(services, "latest/status/", false),
            ["sde", "update", .. var rest] => await services.Sde.UpdateAsync(rest.Contains("--force")),
            ["sde", "status"] => await services.Sde.StatusAsync(),
            ["name", "id", var id] when long.TryParse(id, out var parsed) => await services.Sde.FindByIdAsync(parsed) ?? throw new KeyNotFoundException($"No SDE name found for ID {parsed}."),
            ["name", "search", var query, .. var rest] => await services.Sde.SearchAsync(query, GetLimit(rest)),
            _ => throw new ArgumentException("Unknown or incomplete command. Run 'eden help'.")
        };
        Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
        return 0;
    }
    catch (Exception exception) { Console.Error.WriteLine(exception.Message); return exception is ArgumentException ? 2 : 1; }
}

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

Environment:
  EDEN_CACHE_DIR                 Override the local cache directory
  EDEN_USER_AGENT                Set an ESI-compliant identifying User-Agent
  EDEN_ESI_COMPATIBILITY_DATE    Override the ESI compatibility date

Examples:
  eden esi get "latest/markets/10000002/orders/?type_id=34&order_type=all&page=1"
  eden name search "Raven"
""");
