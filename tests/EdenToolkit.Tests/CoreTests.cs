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
    public async Task CharacterStore_ResolvesIdFullNameAndUniqueFirstName()
    {
        Directory.CreateDirectory(_temp);
        await File.WriteAllTextAsync(Path.Combine(_temp, "characters.json"), """
            [{"characterId":7,"name":"Alice Example","clientId":"client","redirectUri":"http://localhost/","scopes":[],"addedAt":"2026-08-19T00:00:00Z","protectedRefreshToken":""},
             {"characterId":8,"name":"Bob Example","clientId":"client","redirectUri":"http://localhost/","scopes":[],"addedAt":"2026-08-19T00:00:00Z","protectedRefreshToken":""}]
            """);
        var store = new CharacterStore(new EdenOptions { CacheDirectory = _temp });

        Assert.Equal(7, (await store.ResolveAsync("7")).CharacterId);
        Assert.Equal(7, (await store.ResolveAsync("alice example")).CharacterId);
        Assert.Equal(7, (await store.ResolveAsync("ALICE")).CharacterId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ResolveAsync("Carol"));
    }

    [Fact]
    public async Task CharacterStore_RejectsAmbiguousFirstName()
    {
        Directory.CreateDirectory(_temp);
        await File.WriteAllTextAsync(Path.Combine(_temp, "characters.json"), """
            [{"characterId":7,"name":"Alice One","clientId":"client","redirectUri":"http://localhost/","scopes":[],"addedAt":"2026-08-19T00:00:00Z","protectedRefreshToken":""},
             {"characterId":8,"name":"Alice Two","clientId":"client","redirectUri":"http://localhost/","scopes":[],"addedAt":"2026-08-19T00:00:00Z","protectedRefreshToken":""}]
            """);
        var store = new CharacterStore(new EdenOptions { CacheDirectory = _temp });

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.ResolveAsync("Alice"));
        Assert.Contains("Alice One", error.Message);
        Assert.Contains("Alice Two", error.Message);
    }

    [Fact]
    public async Task SdeUpdate_BuildsEnglishNameIndex()
    {
        var zip = MakeZip(("agentTypes.jsonl", "{\"_key\":1,\"name\":\"NonAgent\"}\n"),
            ("types.jsonl", "{\"_key\":34,\"name\":{\"en\":\"Tritanium\",\"de\":\"Tritanium\"}}\n"),
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
        await repository.SaveAsync(Snapshot(7, "transactions", """
            [{"transaction_id":100,"date":"2026-08-18T10:00:00Z","type_id":34,"location_id":60003760,"quantity":1000,"unit_price":5.5,"is_buy":true,"client_id":99},
             {"transaction_id":101,"date":"2026-08-19T10:00:00Z","type_id":35,"location_id":60003760,"quantity":50,"unit_price":9.0,"is_buy":false,"client_id":98}]
            """, fetched));
        await repository.SaveAsync(Snapshot(7, "jobs", """
            [{"job_id":200,"activity_id":1,"blueprint_type_id":681,"product_type_id":165,"facility_id":60003760,"runs":10,"successful_runs":10,"status":"delivered","cost":123.4,"start_date":"2026-08-17T10:00:00Z","end_date":"2026-08-18T10:00:00Z","completed_date":"2026-08-18T11:00:00Z"}]
            """, fetched));
        await repository.SaveAsync(Snapshot(7, "journal", """
            [{"id":300,"date":"2026-08-19T11:00:00Z","ref_type":"market_transaction","amount":500,"balance":10000}]
            """, fetched));
        await repository.SaveAsync(Snapshot(7, "orders", """
            [{"order_id":400,"type_id":34,"location_id":60005686,"is_buy_order":false,"price":120,"volume_remain":5,"volume_total":10,"issued":"2026-08-19T12:00:00Z"}]
            """, fetched));

        var location = await repository.ReadAsync(7, "location");
        var assets = await repository.ReadAsync(7, "assets", new(TypeId: 34));
        var skills = await repository.ReadAsync(7, "skills", new(MinimumSkillLevel: 5));
        var purchases = await repository.ReadAsync(7, "transactions", new(IsBuy: true));
        var jobs = await repository.ReadAsync(7, "jobs", new(TypeId: 165, Status: "delivered"));
        var journal = await repository.ReadAsync(7, "journal", new(Status: "market_transaction"));
        var orders = await repository.ReadAsync(7, "orders", new(TypeId: 34, IsBuy: false));

        Assert.Equal(30000142, location.Data.GetProperty("solar_system_id").GetInt64());
        Assert.Equal(1, Assert.Single(assets.Data.EnumerateArray()).GetProperty("item_id").GetInt64());
        Assert.Equal(3300, Assert.Single(skills.Data.GetProperty("skills").EnumerateArray()).GetProperty("skill_id").GetInt64());
        Assert.Equal(3000, skills.Data.GetProperty("total_sp").GetInt64());
        Assert.Equal(100, Assert.Single(purchases.Data.EnumerateArray()).GetProperty("transaction_id").GetInt64());
        Assert.Equal(200, Assert.Single(jobs.Data.EnumerateArray()).GetProperty("job_id").GetInt64());
        Assert.Equal(300, Assert.Single(journal.Data.EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(400, Assert.Single(orders.Data.EnumerateArray()).GetProperty("order_id").GetInt64());

        await repository.DeleteCharacterAsync(7);
        await Assert.ThrowsAsync<FileNotFoundException>(() => repository.ReadAsync(7, "assets"));
    }

    [Fact]
    public async Task MarketDataService_ComputesHubDepthAndHistoryWithoutPersistingOrders()
    {
        var zip = MakeZip(("types.jsonl", "{\"_key\":34,\"name\":{\"en\":\"Tritanium\"}}\n"));
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("latest.zip", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            if (path.Contains("/orders/", StringComparison.Ordinal))
            {
                var response = Response(HttpStatusCode.OK, """
                    [{"is_buy_order":true,"location_id":60005686,"price":100,"volume_remain":5},
                     {"is_buy_order":true,"location_id":60005686,"price":90,"volume_remain":95},
                     {"is_buy_order":false,"location_id":60005686,"price":120,"volume_remain":5},
                     {"is_buy_order":false,"location_id":60005686,"price":130,"volume_remain":95},
                     {"is_buy_order":false,"location_id":999,"price":1,"volume_remain":1000}]
                    """, TimeSpan.FromMinutes(5));
                response.Headers.Add("X-Pages", "1");
                return response;
            }
            if (path.Contains("/history/", StringComparison.Ordinal))
                return Response(HttpStatusCode.OK, """
                    [{"date":"2026-08-18","average":105,"highest":125,"lowest":85,"volume":1000,"order_count":20},
                     {"date":"2026-08-19","average":115,"highest":135,"lowest":95,"volume":2000,"order_count":30}]
                    """, TimeSpan.FromHours(1));
            throw new InvalidOperationException(request.RequestUri.ToString());
        });
        using var services = CreateServices(handler);
        await services.Sde.UpdateAsync();

        var analysis = await services.Market.GetQuoteAsync("Tritanium", "Hek", 30);
        var persisted = await new MarketDataRepository(services.Options).ReadQuoteAsync(34, "Hek");
        await services.CharacterData.SaveAsync(Snapshot(7, "assets", """
            [{"item_id":1,"type_id":34,"location_id":60005686,"location_type":"station","location_flag":"Hangar","quantity":10,"is_singleton":false}]
            """, DateTimeOffset.UtcNow));
        var inventory = await services.Inventory.ValueAsync(7, "Hek");

        Assert.Equal(100m, analysis.Quote.BestBuy);
        Assert.Equal(120m, analysis.Quote.BestSell);
        Assert.Equal(100m, analysis.Quote.DepthBuy);
        Assert.Equal(120m, analysis.Quote.DepthSell);
        Assert.Equal(20m, analysis.Quote.SpreadPercent);
        Assert.Equal(110m, analysis.History.AveragePrice);
        Assert.Equal(1500m, analysis.History.AverageDailyVolume);
        Assert.Equal(120m, persisted?.BestSell);
        Assert.Equal(1000m, inventory.ImmediateLiquidationValue);
        Assert.Equal(1200m, inventory.ReplacementValue);
        Assert.Equal(new MarketHub("Dodixie", 60011866, 10000032), MarketDataService.Hubs["Dodixie"]);
        Assert.Equal(new MarketHub("Amarr", 60008494, 10000043), MarketDataService.Hubs["Amarr"]);
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
