using System.IO.Compression;
using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record SdeStatus(bool Installed, string? ETag, DateTimeOffset? UpdatedAt, int EntryCount, string Directory);
public sealed record SdeName(long Id, string Name, string Kind);

public sealed class SdeService(HttpClient httpClient, EdenOptions options)
{
    private const int CurrentIndexVersion = 2;
    private const string IndexFile = "names.json";
    private const string MetadataFile = "metadata.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly string[] IndexedFiles =
    [
        "types.jsonl", "groups.jsonl", "categories.jsonl", "marketGroups.jsonl",
        "mapRegions.jsonl", "mapConstellations.jsonl", "mapSolarSystems.jsonl", "npcCorporations.jsonl"
    ];
    private readonly string _directory = Path.Combine(options.CacheDirectory, "sde");
    private Dictionary<long, SdeName>? _index;

    public async Task<SdeStatus> UpdateAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var metadata = await ReadMetadataAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, options.SdeUri);
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        var canRevalidate = !force && metadata?.IndexVersion == CurrentIndexVersion;
        if (canRevalidate && metadata?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (canRevalidate && metadata?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified && metadata is not null)
            return await StatusAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var zipTemp = Path.Combine(_directory, $"sde-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var output = File.Create(zipTemp))
                await response.Content.CopyToAsync(output, cancellationToken);
            var index = await BuildIndexAsync(zipTemp, cancellationToken);
            var indexTemp = Path.Combine(_directory, IndexFile + ".tmp");
            await using (var output = File.Create(indexTemp))
                await JsonSerializer.SerializeAsync(output, index, JsonOptions, cancellationToken);
            File.Move(indexTemp, Path.Combine(_directory, IndexFile), true);

            var newMetadata = new SdeMetadata(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified,
                DateTimeOffset.UtcNow, index.Count, CurrentIndexVersion);
            await using var metadataStream = File.Create(Path.Combine(_directory, MetadataFile));
            await JsonSerializer.SerializeAsync(metadataStream, newMetadata, JsonOptions, cancellationToken);
            _index = index;
            return new(true, newMetadata.ETag, newMetadata.UpdatedAt, index.Count, _directory);
        }
        finally
        {
            if (File.Exists(zipTemp)) File.Delete(zipTemp);
        }
    }

    public async Task<SdeName?> FindByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(cancellationToken);
        return index.GetValueOrDefault(id);
    }

    public async Task<IReadOnlyList<SdeName>> SearchAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 100);
        var index = await GetIndexAsync(cancellationToken);
        return index.Values
            .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name.Length)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToArray();
    }

    public async Task<SdeStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync(cancellationToken);
        return new(metadata is not null && File.Exists(Path.Combine(_directory, IndexFile)), metadata?.ETag,
            metadata?.UpdatedAt, metadata?.EntryCount ?? 0, _directory);
    }

    private async Task<Dictionary<long, SdeName>> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (_index is not null) return _index;
        var path = Path.Combine(_directory, IndexFile);
        if (!File.Exists(path)) throw new InvalidOperationException("SDE is not installed. Run 'eden sde update' first.");
        await using var stream = File.OpenRead(path);
        return _index = await JsonSerializer.DeserializeAsync<Dictionary<long, SdeName>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The SDE name index is invalid. Run 'eden sde update --force'.");
    }

    private static async Task<Dictionary<long, SdeName>> BuildIndexAsync(string zipPath, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, SdeName>();
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var desired in IndexedFiles)
        {
            var entry = archive.Entries.FirstOrDefault(candidate =>
                Path.GetFileName(candidate.FullName).Equals(desired, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            var kind = Path.GetFileNameWithoutExtension(desired);
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!TryGetId(root, out var id) || !TryGetName(root, out var name)) continue;
                result[id] = new(id, name, kind);
            }
        }
        if (result.Count == 0) throw new InvalidDataException("The downloaded SDE contained none of the expected JSONL name datasets.");
        return result;
    }

    private static bool TryGetId(JsonElement root, out long id)
    {
        foreach (var property in new[] { "_key", "typeID", "groupID", "categoryID", "marketGroupID", "regionID", "constellationID", "solarSystemID", "corporationID" })
            if (root.TryGetProperty(property, out var value) && value.TryGetInt64(out id)) return true;
        id = 0;
        return false;
    }

    private static bool TryGetName(JsonElement root, out string name)
    {
        name = string.Empty;
        if (!root.TryGetProperty("name", out var value)) return false;
        if (value.ValueKind == JsonValueKind.String) name = value.GetString() ?? string.Empty;
        else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("en", out var english)) name = english.GetString() ?? string.Empty;
        return name.Length > 0;
    }

    private async Task<SdeMetadata?> ReadMetadataAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, MetadataFile);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SdeMetadata>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private sealed record SdeMetadata(string? ETag, DateTimeOffset? LastModified, DateTimeOffset UpdatedAt, int EntryCount,
        int IndexVersion = 0);
}
