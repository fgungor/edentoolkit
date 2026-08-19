using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record TrackedCharacter(long CharacterId, string Name, string ClientId, string RedirectUri,
    string[] Scopes, DateTimeOffset AddedAt);

internal sealed record StoredCharacter(long CharacterId, string Name, string ClientId, string RedirectUri,
    string[] Scopes, DateTimeOffset AddedAt, string ProtectedRefreshToken);

public sealed class CharacterStore(EdenOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(options.CacheDirectory, "characters.json");

    public async Task<IReadOnlyList<TrackedCharacter>> ListAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Select(ToPublic).OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<TrackedCharacter?> FindAsync(long characterId, CancellationToken cancellationToken = default)
    {
        var found = (await ReadAsync(cancellationToken)).FirstOrDefault(character => character.CharacterId == characterId);
        return found is null ? null : ToPublic(found);
    }

    internal async Task<(TrackedCharacter Character, string RefreshToken)?> GetCredentialsAsync(long characterId,
        CancellationToken cancellationToken = default)
    {
        var found = (await ReadAsync(cancellationToken)).FirstOrDefault(character => character.CharacterId == characterId);
        return found is null ? null : (ToPublic(found), Unprotect(found.ProtectedRefreshToken));
    }

    internal async Task SaveAsync(TrackedCharacter character, string refreshToken, CancellationToken cancellationToken = default)
    {
        var characters = await ReadAsync(cancellationToken);
        characters.RemoveAll(item => item.CharacterId == character.CharacterId);
        characters.Add(new(character.CharacterId, character.Name, character.ClientId, character.RedirectUri,
            character.Scopes, character.AddedAt, Protect(refreshToken)));
        await WriteAsync(characters, cancellationToken);
    }

    internal async Task UpdateRefreshTokenAsync(long characterId, string refreshToken, CancellationToken cancellationToken = default)
    {
        var characters = await ReadAsync(cancellationToken);
        var index = characters.FindIndex(item => item.CharacterId == characterId);
        if (index < 0) throw new KeyNotFoundException($"Character {characterId} is not tracked.");
        characters[index] = characters[index] with { ProtectedRefreshToken = Protect(refreshToken) };
        await WriteAsync(characters, cancellationToken);
    }

    public async Task<bool> RemoveAsync(long characterId, CancellationToken cancellationToken = default)
    {
        var characters = await ReadAsync(cancellationToken);
        var removed = characters.RemoveAll(item => item.CharacterId == characterId) > 0;
        if (removed) await WriteAsync(characters, cancellationToken);
        return removed;
    }

    private async Task<List<StoredCharacter>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<StoredCharacter>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteAsync(List<StoredCharacter> characters, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, characters, JsonOptions, cancellationToken);
        File.Move(temp, _path, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static TrackedCharacter ToPublic(StoredCharacter character) => new(character.CharacterId, character.Name,
        character.ClientId, character.RedirectUri, character.Scopes, character.AddedAt);

    private static string Protect(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string token)
    {
        var bytes = Convert.FromBase64String(token);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
