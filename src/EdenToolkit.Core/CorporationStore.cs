using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record TrackedCorporation(long CorporationId, string Name, long AuthorizingCharacterId,
    string AuthorizingCharacterName, DateTimeOffset LastSyncedAt);

public sealed class CorporationStore(EdenOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(options.CacheDirectory, "corporations.json");

    public async Task<IReadOnlyList<TrackedCorporation>> ListAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).OrderBy(corporation => corporation.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<TrackedCorporation> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        reference = reference.Trim();
        var corporations = await ReadAsync(cancellationToken);
        if (long.TryParse(reference, out var id))
            return corporations.FirstOrDefault(corporation => corporation.CorporationId == id)
                ?? throw new KeyNotFoundException($"Corporation {id} has not been synced.");
        var matches = corporations.Where(corporation => string.Equals(corporation.Name, reference,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) return matches[0];
        throw new KeyNotFoundException($"No tracked corporation matches '{reference}'. Sync a director character first.");
    }

    internal async Task SaveAsync(TrackedCorporation corporation, CancellationToken cancellationToken = default)
    {
        var corporations = await ReadAsync(cancellationToken);
        corporations.RemoveAll(item => item.CorporationId == corporation.CorporationId);
        corporations.Add(corporation);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, corporations, JsonOptions, cancellationToken);
        File.Move(temp, _path, true);
    }

    private async Task<List<TrackedCorporation>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<TrackedCorporation>>(stream, JsonOptions, cancellationToken) ?? [];
    }
}
