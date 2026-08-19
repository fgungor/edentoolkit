using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record CacheMetadata(
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt,
    string? ETag,
    DateTimeOffset? LastModified,
    string ContentType)
{
    public int Pages { get; init; } = 1;
}

public sealed class FileResponseCache(EdenOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(options.CacheDirectory, "esi");

    public string KeyFor(Uri uri)
        => KeyFor(uri.AbsoluteUri);

    public string KeyFor(string value)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));
        return hash;
    }

    public async Task<(CacheMetadata Metadata, byte[] Content)?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var metadataPath = Path.Combine(_directory, key + ".json");
        var contentPath = Path.Combine(_directory, key + ".data");
        if (!File.Exists(metadataPath) || !File.Exists(contentPath)) return null;

        try
        {
            await using var stream = File.OpenRead(metadataPath);
            var metadata = await JsonSerializer.DeserializeAsync<CacheMetadata>(stream, JsonOptions, cancellationToken);
            if (metadata is null) return null;
            return (metadata, await File.ReadAllBytesAsync(contentPath, cancellationToken));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task WriteAsync(string key, CacheMetadata metadata, byte[] content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var unique = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
        var metadataTemp = Path.Combine(_directory, key + "." + unique + ".tmp.json");
        var contentTemp = Path.Combine(_directory, key + "." + unique + ".tmp.data");
        await File.WriteAllBytesAsync(contentTemp, content, cancellationToken);
        await using (var stream = File.Create(metadataTemp))
            await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
        File.Move(contentTemp, Path.Combine(_directory, key + ".data"), true);
        File.Move(metadataTemp, Path.Combine(_directory, key + ".json"), true);
    }
}
