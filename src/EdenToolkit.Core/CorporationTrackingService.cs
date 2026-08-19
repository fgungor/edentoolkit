using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdenToolkit.Core;

public sealed record CorporationSyncResult(long CorporationId, string CorporationName, long AuthorizedByCharacterId,
    DateTimeOffset SyncedAt, IReadOnlyList<CharacterSnapshot> Snapshots);

public sealed class CorporationTrackingService(EsiClient esi, EveSsoService sso, CharacterStore characters,
    CorporationStore corporations, CharacterDataRepository data)
{
    private static long StorageId(long corporationId) => -corporationId;

    public async Task<CorporationSyncResult?> SyncForDirectorAsync(long characterId, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var character = await characters.FindAsync(characterId, cancellationToken)
            ?? throw new KeyNotFoundException($"Character {characterId} is not tracked.");
        var missing = EveSsoService.CorporationScopes.Except(character.Scopes, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"{character.Name} was authorized before corporation access was added. Run 'eden character add' and authorize the character again. Missing scopes: {string.Join(", ", missing)}");
        var token = await sso.GetAccessTokenAsync(characterId, cancellationToken);
        var roles = await esi.GetAuthorizedAsync($"latest/characters/{characterId}/roles/", token, characterId, refresh, cancellationToken);
        if (!HasDirectorRole(roles.Data)) return null;
        var publicInfo = await esi.GetAsync($"latest/characters/{characterId}/", refresh, cancellationToken);
        var corporationId = publicInfo.Data.GetProperty("corporation_id").GetInt64();
        var corporationInfo = await esi.GetAsync($"latest/corporations/{corporationId}/", refresh, cancellationToken);
        var name = corporationInfo.Data.GetProperty("name").GetString() ?? corporationId.ToString();
        return await SyncCoreAsync(corporationId, name, character, token, refresh, cancellationToken);
    }

    public async Task<CorporationSyncResult> SyncAsync(string corporationReference, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var corporation = await corporations.ResolveAsync(corporationReference, cancellationToken);
        var character = await characters.FindAsync(corporation.AuthorizingCharacterId, cancellationToken)
            ?? throw new KeyNotFoundException($"Authorizing character {corporation.AuthorizingCharacterId} is no longer tracked.");
        var token = await sso.GetAccessTokenAsync(character.CharacterId, cancellationToken);
        return await SyncCoreAsync(corporation.CorporationId, corporation.Name, character, token, refresh, cancellationToken);
    }

    public async Task<CharacterSnapshot> QueryAsync(string corporationReference, string aspect, CharacterDataQuery query,
        CancellationToken cancellationToken = default)
    {
        var corporation = await corporations.ResolveAsync(corporationReference, cancellationToken);
        var snapshot = await data.ReadAsync(StorageId(corporation.CorporationId), NormalizeKind(aspect), query, cancellationToken);
        return snapshot with { CharacterId = corporation.CorporationId };
    }

    private async Task<CorporationSyncResult> SyncCoreAsync(long corporationId, string name, TrackedCharacter character,
        string token, bool refresh, CancellationToken cancellationToken)
    {
        var owner = StorageId(corporationId);
        var snapshots = new List<CharacterSnapshot>
        {
            await FetchPagedAsync(owner, "assets", $"latest/corporations/{corporationId}/assets/", token, character.CharacterId, refresh, cancellationToken),
            await FetchPagedAsync(owner, "jobs", $"latest/corporations/{corporationId}/industry/jobs/?include_completed=true", token, character.CharacterId, refresh, cancellationToken),
            await FetchPagedAsync(owner, "orders", $"latest/corporations/{corporationId}/orders/", token, character.CharacterId, refresh, cancellationToken),
            await FetchPagedAsync(owner, "order-history", $"latest/corporations/{corporationId}/orders/history/", token, character.CharacterId, refresh, cancellationToken),
            await FetchAsync(owner, "wallet", $"latest/corporations/{corporationId}/wallets/", token, character.CharacterId, refresh, cancellationToken),
            await FetchDivisionsAsync(owner, "journal", corporationId, "journal", token, character.CharacterId, refresh, cancellationToken),
            await FetchDivisionsAsync(owner, "transactions", corporationId, "transactions", token, character.CharacterId, refresh, cancellationToken)
        };
        var synced = DateTimeOffset.UtcNow;
        await corporations.SaveAsync(new(corporationId, name, character.CharacterId, character.Name, synced), cancellationToken);
        return new(corporationId, name, character.CharacterId, synced,
            snapshots.Select(snapshot => snapshot with { CharacterId = corporationId }).ToArray());
    }

    private async Task<CharacterSnapshot> FetchAsync(long owner, string kind, string path, string token, long characterId,
        bool refresh, CancellationToken cancellationToken)
    {
        var result = await esi.GetAuthorizedAsync(path, token, characterId, refresh, cancellationToken);
        var snapshot = new CharacterSnapshot(owner, kind, DateTimeOffset.UtcNow, result.Data, result.FromCache, result.IsStale);
        await data.SaveAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private async Task<CharacterSnapshot> FetchPagedAsync(long owner, string kind, string path, string token, long characterId,
        bool refresh, CancellationToken cancellationToken)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var first = await esi.GetAuthorizedAsync(path + separator + "page=1", token, characterId, refresh, cancellationToken);
        var rows = first.Data.EnumerateArray().Select(row => row.Clone()).ToList();
        var cached = first.FromCache; var stale = first.IsStale;
        for (var page = 2; page <= first.Pages; page++)
        {
            var result = await esi.GetAuthorizedAsync(path + separator + $"page={page}", token, characterId, refresh, cancellationToken);
            rows.AddRange(result.Data.EnumerateArray().Select(row => row.Clone())); cached &= result.FromCache; stale |= result.IsStale;
        }
        var snapshot = new CharacterSnapshot(owner, kind, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(rows), cached, stale);
        await data.SaveAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private async Task<CharacterSnapshot> FetchDivisionsAsync(long owner, string kind, long corporationId, string endpoint,
        string token, long characterId, bool refresh, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>(); var cached = true; var stale = false;
        for (var division = 1; division <= 7; division++)
        {
            var path = $"latest/corporations/{corporationId}/wallets/{division}/{endpoint}/";
            if (endpoint == "transactions")
            {
                long? fromId = null;
                while (true)
                {
                    var result = await esi.GetAuthorizedAsync(path + (fromId is null ? "" : $"?from_id={fromId}"),
                        token, characterId, refresh, cancellationToken);
                    var page = result.Data.EnumerateArray().Select(row => row.Clone()).ToArray();
                    AddDivision(rows, page, division); cached &= result.FromCache; stale |= result.IsStale;
                    if (page.Length < 2500) break;
                    var next = page.Min(row => row.GetProperty("transaction_id").GetInt64());
                    if (next == fromId) break; fromId = next;
                }
            }
            else
            {
                var first = await esi.GetAuthorizedAsync(path + "?page=1", token, characterId, refresh, cancellationToken);
                AddDivision(rows, first.Data.EnumerateArray(), division); cached &= first.FromCache; stale |= first.IsStale;
                for (var page = 2; page <= first.Pages; page++)
                {
                    var result = await esi.GetAuthorizedAsync(path + $"?page={page}", token, characterId, refresh, cancellationToken);
                    AddDivision(rows, result.Data.EnumerateArray(), division); cached &= result.FromCache; stale |= result.IsStale;
                }
            }
        }
        var snapshot = new CharacterSnapshot(owner, kind, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(rows), cached, stale);
        await data.SaveAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private static void AddDivision(List<JsonElement> target, IEnumerable<JsonElement> source, int division)
    {
        foreach (var row in source)
        {
            var node = JsonNode.Parse(row.GetRawText())!.AsObject(); node["division"] = division;
            target.Add(JsonSerializer.SerializeToElement(node));
        }
    }

    private static bool HasDirectorRole(JsonElement data) =>
        data.TryGetProperty("roles", out var roles) && roles.EnumerateArray().Any(role => role.GetString() == "Director");

    private static string NormalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "assets" or "wallet" or "transactions" or "jobs" or "journal" or "orders" or "order-history" => kind.ToLowerInvariant(),
        _ => throw new ArgumentException("Unsupported corporation data kind.", nameof(kind))
    };
}
