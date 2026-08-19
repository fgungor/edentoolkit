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
            await FetchAsync(characterId, "skills", $"latest/characters/{characterId}/skills/", token, refresh, cancellationToken),
            await FetchTransactionsAsync(characterId, token, refresh, cancellationToken),
            await FetchAsync(characterId, "jobs", $"latest/characters/{characterId}/industry/jobs/?include_completed=true", token, refresh, cancellationToken),
            await FetchPagedAsync(characterId, "journal", $"latest/characters/{characterId}/wallet/journal/", token, refresh, cancellationToken),
            await FetchPagedAsync(characterId, "orders", $"latest/characters/{characterId}/orders/", token, refresh, cancellationToken),
            await FetchPagedAsync(characterId, "order-history", $"latest/characters/{characterId}/orders/history/", token, refresh, cancellationToken)
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

    private async Task<CharacterSnapshot> FetchTransactionsAsync(long characterId, string token, bool refresh,
        CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        long? fromId = null;
        var fromCache = true;
        var stale = false;
        while (true)
        {
            var suffix = fromId is null ? string.Empty : $"?from_id={fromId.Value}";
            var result = await esi.GetAuthorizedAsync($"latest/characters/{characterId}/wallet/transactions/{suffix}",
                token, characterId, refresh, cancellationToken);
            var page = result.Data.EnumerateArray().Select(item => item.Clone()).ToArray();
            items.AddRange(page);
            fromCache &= result.FromCache;
            stale |= result.IsStale;
            if (page.Length < 2500) break;
            var next = page.Min(item => item.GetProperty("transaction_id").GetInt64());
            if (fromId == next) break;
            fromId = next;
        }
        var snapshot = new CharacterSnapshot(characterId, "transactions", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(items), fromCache, stale);
        await data.SaveAsync(snapshot, cancellationToken);
        return await data.ReadAsync(characterId, "transactions", new(Limit: 100000), cancellationToken);
    }

    private async Task<CharacterSnapshot> FetchPagedAsync(long characterId, string kind, string basePath, string token,
        bool refresh, CancellationToken cancellationToken)
    {
        var first = await esi.GetAuthorizedAsync(basePath + "?page=1", token, characterId, refresh, cancellationToken);
        var items = first.Data.EnumerateArray().Select(item => item.Clone()).ToList();
        var fromCache = first.FromCache; var stale = first.IsStale;
        for (var page = 2; page <= first.Pages; page++)
        {
            var result = await esi.GetAuthorizedAsync(basePath + $"?page={page}", token, characterId, refresh, cancellationToken);
            items.AddRange(result.Data.EnumerateArray().Select(item => item.Clone()));
            fromCache &= result.FromCache; stale |= result.IsStale;
        }
        var snapshot = new CharacterSnapshot(characterId, kind, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(items), fromCache, stale);
        await data.SaveAsync(snapshot, cancellationToken);
        return await data.ReadAsync(characterId, kind, new(Limit: 100000), cancellationToken);
    }

    private static string NormalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "location" or "assets" or "wallet" or "skills" or "transactions" or "jobs" or "journal" or "orders" or "order-history" or "pi" => kind.ToLowerInvariant(),
        _ => throw new ArgumentException("Unsupported character data kind.", nameof(kind))
    };
}
