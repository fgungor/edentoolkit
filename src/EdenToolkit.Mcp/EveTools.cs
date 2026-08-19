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
}
