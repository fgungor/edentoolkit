using System.IO.Compression;
using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record SdeStatus(bool Installed, string? ETag, DateTimeOffset? UpdatedAt, int EntryCount, string Directory);
public sealed record SdeName(long Id, string Name, string Kind);
public sealed record PlanetSchematicMaterial(long TypeId, bool IsInput, long Quantity);
public sealed record PlanetSchematic(int Id, string Name, int CycleTime, IReadOnlyList<PlanetSchematicMaterial> Materials);

public sealed class SdeService(HttpClient httpClient, EdenOptions options)
{
    private const int CurrentIndexVersion = 4;
    private const string IndexFile = "names.json";
    private const string MetadataFile = "metadata.json";
    private const string SchematicsFile = "planet-schematics.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly string[] IndexedFiles =
    [
        "types.jsonl", "groups.jsonl", "categories.jsonl", "marketGroups.jsonl",
        "mapRegions.jsonl", "mapConstellations.jsonl", "mapSolarSystems.jsonl", "mapPlanets.jsonl", "npcCorporations.jsonl"
    ];
    private readonly string _directory = Path.Combine(options.CacheDirectory, "sde");
    private Dictionary<string, SdeName>? _index;
    private Dictionary<int, PlanetSchematic>? _schematics;
    private FileStamp? _indexStamp;
    private FileStamp? _schematicsStamp;

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
            var schematics = await BuildSchematicsAsync(zipTemp, cancellationToken);
            var indexTemp = Path.Combine(_directory, IndexFile + ".tmp");
            await using (var output = File.Create(indexTemp))
                await JsonSerializer.SerializeAsync(output, index, JsonOptions, cancellationToken);
            File.Move(indexTemp, Path.Combine(_directory, IndexFile), true);
            var schematicsTemp = Path.Combine(_directory, SchematicsFile + ".tmp");
            await using (var output = File.Create(schematicsTemp))
                await JsonSerializer.SerializeAsync(output, schematics, JsonOptions, cancellationToken);
            File.Move(schematicsTemp, Path.Combine(_directory, SchematicsFile), true);

            var newMetadata = new SdeMetadata(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified,
                DateTimeOffset.UtcNow, index.Count, CurrentIndexVersion);
            await using var metadataStream = File.Create(Path.Combine(_directory, MetadataFile));
            await JsonSerializer.SerializeAsync(metadataStream, newMetadata, JsonOptions, cancellationToken);
            _index = index;
            _indexStamp = GetStamp(Path.Combine(_directory, IndexFile));
            _schematics = schematics;
            _schematicsStamp = GetStamp(Path.Combine(_directory, SchematicsFile));
            return new(true, newMetadata.ETag, newMetadata.UpdatedAt, index.Count, _directory);
        }
        finally
        {
            if (File.Exists(zipTemp)) File.Delete(zipTemp);
        }
    }

    public async Task<SdeName?> FindByIdAsync(long id, string kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var index = await GetIndexAsync(cancellationToken);
        return index.GetValueOrDefault(IndexKey(kind, id));
    }

    public async Task<IReadOnlyList<SdeName>> FindAllByIdAsync(long id,
        CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(cancellationToken);
        return index.Values.Where(item => item.Id == id).OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase).ToArray();
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

    public async Task<PlanetSchematic?> FindPlanetSchematicAsync(int schematicId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, SchematicsFile);
        var stamp = GetStamp(path);
        if (_schematics is null || _schematicsStamp != stamp)
        {
            if (!File.Exists(path)) throw new InvalidOperationException("PI schematic data is not installed. Run 'eden sde update --force'.");
            await using var stream = File.OpenRead(path);
            _schematics = await JsonSerializer.DeserializeAsync<Dictionary<int, PlanetSchematic>>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The PI schematic index is invalid. Run 'eden sde update --force'.");
            _schematicsStamp = stamp;
        }
        return _schematics.GetValueOrDefault(schematicId);
    }

    public async Task<SdeStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync(cancellationToken);
        return new(metadata is not null && File.Exists(Path.Combine(_directory, IndexFile)), metadata?.ETag,
            metadata?.UpdatedAt, metadata?.EntryCount ?? 0, _directory);
    }

    private async Task<Dictionary<string, SdeName>> GetIndexAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, IndexFile);
        var stamp = GetStamp(path);
        if (_index is not null && _indexStamp == stamp) return _index;
        if (!File.Exists(path)) throw new InvalidOperationException("SDE is not installed. Run 'eden sde update' first.");
        await using var stream = File.OpenRead(path);
        _index = await JsonSerializer.DeserializeAsync<Dictionary<string, SdeName>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The SDE name index is invalid. Run 'eden sde update --force'.");
        _indexStamp = stamp;
        return _index;
    }

    private static async Task<Dictionary<string, SdeName>> BuildIndexAsync(string zipPath, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SdeName>(StringComparer.OrdinalIgnoreCase);
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
                result[IndexKey(kind, id)] = new(id, name, kind);
            }
        }
        if (result.Count == 0) throw new InvalidDataException("The downloaded SDE contained none of the expected JSONL name datasets.");
        return result;
    }

    private static string IndexKey(string kind, long id) => $"{kind.Trim().ToLowerInvariant()}:{id}";

    private static async Task<Dictionary<int, PlanetSchematic>> BuildSchematicsAsync(string zipPath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, PlanetSchematic>();
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            Path.GetFileName(candidate.FullName).Equals("planetSchematics.jsonl", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The downloaded SDE does not contain planetSchematics.jsonl.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = root.GetProperty("_key").GetInt32();
            var name = root.GetProperty("name").GetProperty("en").GetString() ?? id.ToString();
            var materials = root.GetProperty("types").EnumerateArray().Select(item => new PlanetSchematicMaterial(
                item.GetProperty("_key").GetInt64(), item.GetProperty("isInput").GetBoolean(),
                item.GetProperty("quantity").GetInt64())).ToArray();
            result[id] = new(id, name, root.GetProperty("cycleTime").GetInt32(), materials);
        }
        return result;
    }

    private static bool TryGetId(JsonElement root, out long id)
    {
        foreach (var property in new[] { "_key", "typeID", "groupID", "categoryID", "marketGroupID", "regionID", "constellationID", "solarSystemID", "planetID", "corporationID" })
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
    private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);
    private static FileStamp? GetStamp(string path)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc);
    }
}
