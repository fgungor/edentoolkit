using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using EdenToolkit.Core;

namespace EdenToolkit.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "EdenToolkit.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EsiClient_CachesFreshResponses()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "{\"players\":42}", TimeSpan.FromHours(1)));
        using var services = CreateServices(handler);

        var first = await services.Esi.GetAsync("latest/status/");
        var second = await services.Esi.GetAsync("latest/status/");

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(42, second.Data.GetProperty("players").GetInt32());
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task EsiClient_RejectsAbsoluteUrls()
    {
        using var services = CreateServices(new StubHandler(_ => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<ArgumentException>(() => services.Esi.GetAsync("https://example.com/steal"));
    }

    [Fact]
    public async Task EsiClient_PartitionsAuthenticatedCacheByCharacter()
    {
        var handler = new StubHandler(request => Response(HttpStatusCode.OK,
            $"{{\"authorization\":\"{request.Headers.Authorization?.Parameter}\"}}", TimeSpan.FromHours(1)));
        using var services = CreateServices(handler);

        var first = await services.Esi.GetAuthorizedAsync("latest/characters/1/wallet/", "token-one", 1);
        var second = await services.Esi.GetAuthorizedAsync("latest/characters/1/wallet/", "token-two", 2);
        var cached = await services.Esi.GetAuthorizedAsync("latest/characters/1/wallet/", "new-token", 1);

        Assert.Equal("token-one", first.Data.GetProperty("authorization").GetString());
        Assert.Equal("token-two", second.Data.GetProperty("authorization").GetString());
        Assert.Equal("token-one", cached.Data.GetProperty("authorization").GetString());
        Assert.True(cached.FromCache);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SdeUpdate_BuildsEnglishNameIndex()
    {
        var zip = MakeZip(("types.jsonl", "{\"_key\":34,\"name\":{\"en\":\"Tritanium\",\"de\":\"Tritanium\"}}\n"),
            ("mapSolarSystems.jsonl", "{\"_key\":30000142,\"name\":{\"en\":\"Jita\"}}\n"));
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"build-1\"");
            return response;
        });
        using var services = CreateServices(handler);

        var status = await services.Sde.UpdateAsync();
        var tritanium = await services.Sde.FindByIdAsync(34);
        var search = await services.Sde.SearchAsync("jit");

        Assert.Equal(2, status.EntryCount);
        Assert.Equal("Tritanium", tritanium?.Name);
        Assert.Equal(30000142, Assert.Single(search).Id);
    }

    [Fact]
    public async Task CharacterDataRepository_StoresAndQueriesDecomposedObjects()
    {
        var repository = new CharacterDataRepository(new EdenOptions { CacheDirectory = _temp });
        var fetched = DateTimeOffset.UtcNow;
        await repository.SaveAsync(Snapshot(7, "location", "{\"solar_system_id\":30000142}", fetched));
        await repository.SaveAsync(Snapshot(7, "wallet", "1234.5", fetched));
        await repository.SaveAsync(Snapshot(7, "assets", """
            [{"item_id":1,"type_id":34,"location_id":60003760,"location_type":"station","location_flag":"Hangar","quantity":10,"is_singleton":false},
             {"item_id":2,"type_id":35,"location_id":60008494,"location_type":"station","location_flag":"Hangar","quantity":20,"is_singleton":false}]
            """, fetched));
        await repository.SaveAsync(Snapshot(7, "skills", """
            {"total_sp":3000,"skills":[
              {"skill_id":3300,"active_skill_level":5,"trained_skill_level":5,"skillpoints_in_skill":2000},
              {"skill_id":3301,"active_skill_level":3,"trained_skill_level":3,"skillpoints_in_skill":1000}]}
            """, fetched));

        var location = await repository.ReadAsync(7, "location");
        var assets = await repository.ReadAsync(7, "assets", new(TypeId: 34));
        var skills = await repository.ReadAsync(7, "skills", new(MinimumSkillLevel: 5));

        Assert.Equal(30000142, location.Data.GetProperty("solar_system_id").GetInt64());
        Assert.Equal(1, Assert.Single(assets.Data.EnumerateArray()).GetProperty("item_id").GetInt64());
        Assert.Equal(3300, Assert.Single(skills.Data.GetProperty("skills").EnumerateArray()).GetProperty("skill_id").GetInt64());
        Assert.Equal(3000, skills.Data.GetProperty("total_sp").GetInt64());

        await repository.DeleteCharacterAsync(7);
        await Assert.ThrowsAsync<FileNotFoundException>(() => repository.ReadAsync(7, "assets"));
    }

    private static CharacterSnapshot Snapshot(long characterId, string kind, string json, DateTimeOffset fetched)
    {
        using var document = JsonDocument.Parse(json);
        return new(characterId, kind, fetched, document.RootElement.Clone(), false, false);
    }

    private EdenServices CreateServices(HttpMessageHandler handler) => new(new EdenOptions
    {
        CacheDirectory = _temp,
        EsiBaseUri = new Uri("https://esi.test/"),
        SdeUri = new Uri("https://sde.test/latest.zip")
    }, handler);

    private static HttpResponseMessage Response(HttpStatusCode status, string json, TimeSpan maxAge)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = maxAge };
        return response;
    }

    private static byte[] MakeZip(params (string Name, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
