using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdenToolkit.Core;

public sealed class PlanetaryIndustryService(EsiClient esi, EveSsoService sso, CharacterStore characters,
    CharacterDataRepository data, SdeService sde)
{
    public async Task<CharacterSnapshot> SyncAsync(long characterId, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var character = await characters.FindAsync(characterId, cancellationToken);
        if (character is null)
            throw new KeyNotFoundException($"Character {characterId} is not tracked.");
        if (!character.Scopes.Contains("esi-planets.manage_planets.v1", StringComparer.Ordinal))
            throw new InvalidOperationException($"{character.Name} was authorized before PI access was added. Run 'eden character add' and authorize the character again.");
        var token = await sso.GetAccessTokenAsync(characterId, cancellationToken);
        var summaries = await esi.GetAuthorizedAsync($"latest/characters/{characterId}/planets/", token,
            characterId, refresh, cancellationToken);
        var colonies = new JsonArray();
        var fromCache = summaries.FromCache; var stale = summaries.IsStale;
        foreach (var summary in summaries.Data.EnumerateArray())
        {
            var planetId = summary.GetProperty("planet_id").GetInt64();
            var layout = await esi.GetAuthorizedAsync($"latest/characters/{characterId}/planets/{planetId}/",
                token, characterId, refresh, cancellationToken);
            fromCache &= layout.FromCache; stale |= layout.IsStale;
            colonies.Add(await BuildColonyAsync(summary, layout.Data, cancellationToken));
        }
        var snapshot = new CharacterSnapshot(characterId, "pi", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new JsonObject { ["colonies"] = colonies }), fromCache, stale);
        await data.SaveAsync(snapshot, cancellationToken);
        return await data.ReadAsync(characterId, "pi", cancellationToken: cancellationToken);
    }

    public Task<CharacterSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken = default) =>
        data.ReadAsync(characterId, "pi", cancellationToken: cancellationToken);

    private async Task<JsonObject> BuildColonyAsync(JsonElement summary, JsonElement layout,
        CancellationToken cancellationToken)
    {
        var colony = JsonNode.Parse(summary.GetRawText())!.AsObject();
        colony["planet_name"] = await NameAsync(summary.GetProperty("planet_id").GetInt64(), cancellationToken);
        colony["solar_system_name"] = await NameAsync(summary.GetProperty("solar_system_id").GetInt64(), cancellationToken);
        var pins = new JsonArray();
        foreach (var pinElement in layout.GetProperty("pins").EnumerateArray())
        {
            var pin = JsonNode.Parse(pinElement.GetRawText())!.AsObject();
            var pinTypeName = await NameAsync(pinElement.GetProperty("type_id").GetInt64(), cancellationToken);
            pin["type_name"] = pinTypeName;
            pin["is_launchpad"] = pinTypeName?.Contains("Launchpad", StringComparison.OrdinalIgnoreCase) == true;
            if (pinElement.TryGetProperty("contents", out var contents))
            {
                var enriched = new JsonArray();
                foreach (var content in contents.EnumerateArray())
                {
                    var item = JsonNode.Parse(content.GetRawText())!.AsObject();
                    item["type_name"] = await NameAsync(content.GetProperty("type_id").GetInt64(), cancellationToken);
                    enriched.Add(item);
                }
                pin["contents"] = enriched;
            }
            if (pinElement.TryGetProperty("extractor_details", out var extractor))
            {
                var details = JsonNode.Parse(extractor.GetRawText())!.AsObject();
                if (extractor.TryGetProperty("product_type_id", out var product))
                    details["product_type_name"] = await NameAsync(product.GetInt64(), cancellationToken);
                pin["extractor_details"] = details;
            }
            var schematicId = GetSchematicId(pinElement);
            if (schematicId is { } id && await sde.FindPlanetSchematicAsync(id, cancellationToken) is { } schematic)
            {
                var factory = pin["factory_details"] as JsonObject ?? new JsonObject();
                factory["schematic_id"] = schematic.Id; factory["schematic_name"] = schematic.Name;
                factory["cycle_time_seconds"] = schematic.CycleTime;
                factory["inputs"] = await MaterialsAsync(schematic.Materials.Where(material => material.IsInput), cancellationToken);
                factory["outputs"] = await MaterialsAsync(schematic.Materials.Where(material => !material.IsInput), cancellationToken);
                pin["factory_details"] = factory;
            }
            pins.Add(pin);
        }
        colony["pins"] = pins;
        colony["links"] = JsonNode.Parse(layout.GetProperty("links").GetRawText());
        var routes = new JsonArray();
        foreach (var routeElement in layout.GetProperty("routes").EnumerateArray())
        {
            var route = JsonNode.Parse(routeElement.GetRawText())!.AsObject();
            route["content_type_name"] = await NameAsync(routeElement.GetProperty("content_type_id").GetInt64(), cancellationToken);
            routes.Add(route);
        }
        colony["routes"] = routes;
        return colony;
    }

    private async Task<JsonArray> MaterialsAsync(IEnumerable<PlanetSchematicMaterial> materials,
        CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (var material in materials)
            result.Add(new JsonObject { ["type_id"] = material.TypeId,
                ["type_name"] = await NameAsync(material.TypeId, cancellationToken), ["quantity"] = material.Quantity });
        return result;
    }

    private async Task<string?> NameAsync(long id, CancellationToken cancellationToken) =>
        (await sde.FindByIdAsync(id, cancellationToken))?.Name;

    private static int? GetSchematicId(JsonElement pin) =>
        pin.TryGetProperty("factory_details", out var factory) && factory.TryGetProperty("schematic_id", out var nested)
            ? nested.GetInt32()
            : pin.TryGetProperty("schematic_id", out var direct) ? direct.GetInt32() : null;
}
