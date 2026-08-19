using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record CharacterSnapshot(long CharacterId, string Kind, DateTimeOffset FetchedAt, JsonElement Data,
    bool FromCache, bool IsStale);
public sealed record CharacterSyncResult(long CharacterId, DateTimeOffset SyncedAt, IReadOnlyList<CharacterSnapshot> Snapshots);

public sealed class CharacterTrackingService(EsiClient esi, EveSsoService sso, CharacterStore store, CharacterDataRepository data)
{
    public async Task<CharacterSyncResult> SyncAsync(long characterId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (await store.FindAsync(characterId, cancellationToken) is null)
            throw new KeyNotFoundException($"Character {characterId} is not tracked.");
        var token = await sso.GetAccessTokenAsync(characterId, cancellationToken);
        var snapshots = new List<CharacterSnapshot>
        {
            await FetchAsync(characterId, "location", $"latest/characters/{characterId}/location/", token, refresh, cancellationToken),
            await FetchAssetsAsync(characterId, token, refresh, cancellationToken),
            await FetchAsync(characterId, "wallet", $"latest/characters/{characterId}/wallet/", token, refresh, cancellationToken),
            await FetchAsync(characterId, "skills", $"latest/characters/{characterId}/skills/", token, refresh, cancellationToken)
        };
        return new(characterId, DateTimeOffset.UtcNow, snapshots);
    }

    public Task<CharacterSnapshot> ReadAsync(long characterId, string kind, CancellationToken cancellationToken = default) =>
        QueryAsync(characterId, kind, new(Limit: 100000), cancellationToken);

    public Task<CharacterSnapshot> QueryAsync(long characterId, string kind, CharacterDataQuery query,
        CancellationToken cancellationToken = default) => data.ReadAsync(characterId, NormalizeKind(kind), query, cancellationToken);

    private async Task<CharacterSnapshot> FetchAsync(long characterId, string kind, string path, string token,
        bool refresh, CancellationToken cancellationToken)
    {
        var result = await esi.GetAuthorizedAsync(path, token, characterId, refresh, cancellationToken);
        var snapshot = new CharacterSnapshot(characterId, kind, DateTimeOffset.UtcNow, result.Data, result.FromCache, result.IsStale);
        await data.SaveAsync(snapshot, cancellationToken);
        return await data.ReadAsync(characterId, kind, new(Limit: 100000), cancellationToken);
    }

    private async Task<CharacterSnapshot> FetchAssetsAsync(long characterId, string token, bool refresh, CancellationToken cancellationToken)
    {
        var first = await esi.GetAuthorizedAsync($"latest/characters/{characterId}/assets/?page=1", token, characterId, refresh, cancellationToken);
        var items = first.Data.EnumerateArray().Select(item => item.Clone()).ToList();
        var fromCache = first.FromCache;
        var stale = first.IsStale;
        for (var page = 2; page <= first.Pages; page++)
        {
            var result = await esi.GetAuthorizedAsync($"latest/characters/{characterId}/assets/?page={page}", token, characterId, refresh, cancellationToken);
            items.AddRange(result.Data.EnumerateArray().Select(item => item.Clone()));
            fromCache &= result.FromCache;
            stale |= result.IsStale;
        }
        var assetData = JsonSerializer.SerializeToElement(items);
        var snapshot = new CharacterSnapshot(characterId, "assets", DateTimeOffset.UtcNow, assetData, fromCache, stale);
        await data.SaveAsync(snapshot, cancellationToken);
        return await data.ReadAsync(characterId, "assets", new(Limit: 100000), cancellationToken);
    }

    private static string NormalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "location" or "assets" or "wallet" or "skills" => kind.ToLowerInvariant(),
        _ => throw new ArgumentException("Data kind must be location, assets, wallet, or skills.", nameof(kind))
    };
}
