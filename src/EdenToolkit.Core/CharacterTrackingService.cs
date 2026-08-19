using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record CharacterSnapshot(long CharacterId, string Kind, DateTimeOffset FetchedAt, JsonElement Data,
    bool FromCache, bool IsStale);
public sealed record CharacterSyncResult(long CharacterId, DateTimeOffset SyncedAt, IReadOnlyList<CharacterSnapshot> Snapshots);

public sealed class CharacterTrackingService(EsiClient esi, EveSsoService sso, CharacterStore store, EdenOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _directory = Path.Combine(options.CacheDirectory, "characters");

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

    public async Task<CharacterSnapshot> ReadAsync(long characterId, string kind, CancellationToken cancellationToken = default)
    {
        kind = NormalizeKind(kind);
        var path = SnapshotPath(characterId, kind);
        if (!File.Exists(path)) throw new FileNotFoundException($"No {kind} snapshot exists for character {characterId}. Run 'eden character sync {characterId}'.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CharacterSnapshot>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"The {kind} snapshot is invalid.");
    }

    private async Task<CharacterSnapshot> FetchAsync(long characterId, string kind, string path, string token,
        bool refresh, CancellationToken cancellationToken)
    {
        var result = await esi.GetAuthorizedAsync(path, token, characterId, refresh, cancellationToken);
        var snapshot = new CharacterSnapshot(characterId, kind, DateTimeOffset.UtcNow, result.Data, result.FromCache, result.IsStale);
        await SaveAsync(snapshot, cancellationToken);
        return snapshot;
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
        var data = JsonSerializer.SerializeToElement(items, JsonOptions);
        var snapshot = new CharacterSnapshot(characterId, "assets", DateTimeOffset.UtcNow, data, fromCache, stale);
        await SaveAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private async Task SaveAsync(CharacterSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = SnapshotPath(snapshot.CharacterId, snapshot.Kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
        File.Move(temp, path, true);
    }

    private string SnapshotPath(long characterId, string kind) => Path.Combine(_directory, characterId.ToString(), NormalizeKind(kind) + ".json");
    private static string NormalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "location" or "assets" or "wallet" or "skills" => kind.ToLowerInvariant(),
        _ => throw new ArgumentException("Data kind must be location, assets, wallet, or skills.", nameof(kind))
    };
}
