using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record ProductionBlueprint(long ItemId, long BlueprintTypeId, int MaterialEfficiency,
    int TimeEfficiency, long RunsAvailable, long RunsUsed, long OutputUnits);
public sealed record ProductionMaterial(long TypeId, string Name, long BaseQuantityPerRun, long Available,
    long Consumed, long Remaining);
public sealed record ProductionCapacity(long CorporationId, string CorporationName, long BlueprintTypeId,
    string BlueprintName, long ProductTypeId, string ProductName, long ProductQuantityPerRun,
    long BuildableRuns, long BuildableUnits, IReadOnlyList<ProductionBlueprint> Blueprints,
    IReadOnlyList<ProductionMaterial> Materials, DateTimeOffset CalculatedAt);

public sealed class ProductionCapacityService(CorporationStore corporations, CharacterDataRepository data, SdeService sde)
{
    public async Task<ProductionCapacity> CalculateAsync(string corporationReference, string item,
        CancellationToken cancellationToken = default)
    {
        var corporation = await corporations.ResolveAsync(corporationReference, cancellationToken);
        var owner = -corporation.CorporationId;
        var blueprintSnapshot = await data.ReadAsync(owner, "blueprints", cancellationToken: cancellationToken);
        var assetSnapshot = await data.ReadAsync(owner, "assets", new(Limit: 100000), cancellationToken);
        var recipe = await ResolveRecipeAsync(item, cancellationToken);
        var owned = blueprintSnapshot.Data.EnumerateArray()
            .Where(row => row.GetProperty("type_id").GetInt64() == recipe.BlueprintTypeId)
            .OrderByDescending(row => row.GetProperty("material_efficiency").GetInt32())
            .ThenByDescending(row => row.GetProperty("runs").GetInt64()).ToArray();
        if (owned.Length == 0) throw new InvalidOperationException($"The corporation owns no blueprint items of type {recipe.BlueprintTypeId}.");

        var available = assetSnapshot.Data.EnumerateArray()
            .GroupBy(row => row.GetProperty("type_id").GetInt64())
            .ToDictionary(group => group.Key, group => group.Sum(row => Math.Max(0, row.GetProperty("quantity").GetInt64())));
        var initial = recipe.Materials.ToDictionary(material => material.TypeId,
            material => available.GetValueOrDefault(material.TypeId));
        var used = recipe.Materials.ToDictionary(material => material.TypeId, _ => 0L);
        var blueprintResults = new List<ProductionBlueprint>(); long totalRuns = 0;

        foreach (var blueprint in owned)
        {
            var runs = blueprint.GetProperty("runs").GetInt64();
            var copyOrOriginal = blueprint.GetProperty("quantity").GetInt64();
            var maximum = copyOrOriginal == -2 ? Math.Max(0, runs) : long.MaxValue / 4;
            var me = blueprint.GetProperty("material_efficiency").GetInt32();
            var affordable = MaxAffordableRuns(recipe.Materials, available, me, maximum);
            foreach (var material in recipe.Materials)
            {
                var quantity = Required(material.Quantity, affordable, me);
                available[material.TypeId] = available.GetValueOrDefault(material.TypeId) - quantity;
                used[material.TypeId] += quantity;
            }
            totalRuns += affordable;
            blueprintResults.Add(new(blueprint.GetProperty("item_id").GetInt64(), recipe.BlueprintTypeId, me,
                blueprint.GetProperty("time_efficiency").GetInt32(), runs, affordable,
                checked(affordable * recipe.ProductQuantity)));
        }

        var materials = new List<ProductionMaterial>();
        foreach (var material in recipe.Materials)
            materials.Add(new(material.TypeId, await TypeNameAsync(material.TypeId, cancellationToken), material.Quantity,
                initial[material.TypeId], used[material.TypeId], available.GetValueOrDefault(material.TypeId)));
        return new(corporation.CorporationId, corporation.Name, recipe.BlueprintTypeId,
            await TypeNameAsync(recipe.BlueprintTypeId, cancellationToken), recipe.ProductTypeId,
            await TypeNameAsync(recipe.ProductTypeId, cancellationToken), recipe.ProductQuantity, totalRuns,
            checked(totalRuns * recipe.ProductQuantity), blueprintResults, materials, DateTimeOffset.UtcNow);
    }

    private async Task<ManufacturingRecipe> ResolveRecipeAsync(string item, CancellationToken cancellationToken)
    {
        SdeName type;
        if (long.TryParse(item, out var id))
            type = await sde.FindByIdAsync(id, "types", cancellationToken)
                ?? throw new KeyNotFoundException($"No item type exists with ID {id}.");
        else
        {
            var matches = (await sde.SearchAsync(item, 100, cancellationToken))
                .Where(value => value.Kind == "types").ToArray();
            type = matches.FirstOrDefault(value => value.Name.Equals(item, StringComparison.OrdinalIgnoreCase))
                ?? (matches.Length == 1 ? matches[0] : throw new KeyNotFoundException($"No unambiguous item type matches '{item}'."));
        }
        if (await sde.FindManufacturingByBlueprintAsync(type.Id, cancellationToken) is { } byBlueprint) return byBlueprint;
        var byProduct = await sde.FindManufacturingByProductAsync(type.Id, cancellationToken);
        return byProduct.Count == 1 ? byProduct[0]
            : throw new KeyNotFoundException($"No unique manufacturing recipe exists for '{type.Name}'.");
    }

    private static long MaxAffordableRuns(IReadOnlyList<ManufacturingMaterial> materials,
        IReadOnlyDictionary<long, long> available, int me, long maximum)
    {
        if (maximum <= 0) return 0;
        long high = maximum;
        foreach (var material in materials)
        {
            var stock = available.GetValueOrDefault(material.TypeId);
            high = Math.Min(high, material.Quantity <= 0 ? high : stock / Math.Max(1, material.Quantity * Math.Max(0, 100 - me) / 100));
        }
        high = Math.Min(maximum, Math.Max(1, high + 2));
        while (high < maximum && CanAfford(materials, available, me, high))
            high = Math.Min(maximum, high > long.MaxValue / 2 ? maximum : high * 2);
        long low = 0;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (CanAfford(materials, available, me, middle)) low = middle; else high = middle - 1;
        }
        return low;
    }

    private static bool CanAfford(IEnumerable<ManufacturingMaterial> materials, IReadOnlyDictionary<long, long> available,
        int me, long runs) => materials.All(material => Required(material.Quantity, runs, me) <= available.GetValueOrDefault(material.TypeId));

    private static long Required(long baseQuantity, long runs, int me)
    {
        if (runs == 0) return 0;
        var calculated = decimal.Ceiling((decimal)baseQuantity * runs * Math.Max(0, 100 - me) / 100m);
        return Math.Max(runs, checked((long)calculated));
    }

    private async Task<string> TypeNameAsync(long typeId, CancellationToken cancellationToken) =>
        (await sde.FindByIdAsync(typeId, "types", cancellationToken))?.Name ?? typeId.ToString();
}
