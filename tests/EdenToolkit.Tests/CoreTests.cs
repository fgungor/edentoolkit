using System.IO.Compression;
using System.Net;
using System.Text;
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
