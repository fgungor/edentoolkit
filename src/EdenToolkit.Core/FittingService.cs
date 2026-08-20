using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record FittingItem(long TypeId, string TypeName, string Flag, string Category, long Quantity);
public sealed record FittingView(string OwnerKind, long OwnerId, long FittingId, string Name, string Description,
    long ShipTypeId, string ShipTypeName, string Source, long? LocationId, string? LocationFlag,
    IReadOnlyList<FittingItem> Items);

public sealed class FittingService(CharacterStore characters, CharacterDataRepository data, SdeService sde, EsiClient esi)
{
    public async Task<IReadOnlyList<FittingView>> CharacterFittingsAsync(string characterReference, string? query = null,
        CancellationToken cancellationToken = default)
    {
        var character = await characters.ResolveAsync(characterReference, cancellationToken);
        return await ReadAsync("character", character.CharacterId, character.CharacterId, query, cancellationToken);
    }

    public async Task<object> TypeDetailsAsync(long typeId, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var result = await esi.GetAsync($"latest/universe/types/{typeId}/", refresh, cancellationToken);
        return new { result.Data, cache = new { result.FromCache, result.IsStale, result.ExpiresAt } };
    }

    private async Task<IReadOnlyList<FittingView>> ReadAsync(string ownerKind, long ownerId, long storageId, string? query,
        CancellationToken cancellationToken)
    {
        var snapshot = await data.ReadAsync(storageId, "fittings", cancellationToken: cancellationToken);
        var result = new List<FittingView>();
        foreach (var fit in snapshot.Data.EnumerateArray())
        {
            var shipTypeId = fit.GetProperty("ship_type_id").GetInt64();
            var shipName = await TypeNameAsync(shipTypeId, cancellationToken);
            var name = fit.GetProperty("name").GetString() ?? fit.GetProperty("fitting_id").GetInt64().ToString();
            if (!string.IsNullOrWhiteSpace(query) && !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !shipName.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var items = new List<FittingItem>();
            foreach (var item in fit.GetProperty("items").EnumerateArray())
            {
                var flag = item.GetProperty("flag").GetString() ?? "Invalid";
                var typeId = item.GetProperty("type_id").GetInt64();
                items.Add(new(typeId, await TypeNameAsync(typeId, cancellationToken), flag, Category(flag),
                    item.GetProperty("quantity").GetInt64()));
            }
            result.Add(new(ownerKind, ownerId, fit.GetProperty("fitting_id").GetInt64(), name,
                fit.GetProperty("description").GetString() ?? string.Empty, shipTypeId, shipName,
                fit.TryGetProperty("source", out var source) ? source.GetString() ?? "unknown" : "character-saved",
                fit.TryGetProperty("location_id", out var location) ? location.GetInt64() : null,
                fit.TryGetProperty("location_flag", out var locationFlag) ? locationFlag.GetString() : null,
                items.OrderBy(item => CategoryOrder(item.Category)).ThenBy(item => item.Flag).ToArray()));
        }
        return result.OrderBy(fit => fit.ShipTypeName).ThenBy(fit => fit.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<string> TypeNameAsync(long typeId, CancellationToken cancellationToken) =>
        (await sde.FindByIdAsync(typeId, "types", cancellationToken))?.Name ?? typeId.ToString();

    private static string Category(string flag) => flag switch
    {
        var value when value.StartsWith("HiSlot", StringComparison.Ordinal) => "high",
        var value when value.StartsWith("MedSlot", StringComparison.Ordinal) => "mid",
        var value when value.StartsWith("LoSlot", StringComparison.Ordinal) => "low",
        var value when value.StartsWith("RigSlot", StringComparison.Ordinal) => "rig",
        var value when value.StartsWith("SubSystemSlot", StringComparison.Ordinal) => "subsystem",
        var value when value.StartsWith("ServiceSlot", StringComparison.Ordinal) => "service",
        "DroneBay" => "drone", "FighterBay" => "fighter", "Cargo" => "cargo", _ => "other"
    };
    private static int CategoryOrder(string category) => category switch
    { "high" => 0, "mid" => 1, "low" => 2, "rig" => 3, "subsystem" => 4, "service" => 5,
      "drone" => 6, "fighter" => 7, "cargo" => 8, _ => 9 };
}
