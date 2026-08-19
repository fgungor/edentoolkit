namespace EdenToolkit.Core;

public sealed class EdenServices : IDisposable
{
    private readonly HttpClient _httpClient;
    public EdenOptions Options { get; }
    public EsiClient Esi { get; }
    public SdeService Sde { get; }
    public CharacterStore Characters { get; }
    public EveSsoService Sso { get; }
    public CharacterTrackingService Tracking { get; }

    public EdenServices(EdenOptions? options = null, HttpMessageHandler? handler = null)
    {
        Options = options ?? EdenOptions.FromEnvironment();
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        var cache = new FileResponseCache(Options);
        Esi = new EsiClient(_httpClient, Options, cache);
        Sde = new SdeService(_httpClient, Options);
        Characters = new CharacterStore(Options);
        Sso = new EveSsoService(_httpClient, Options, Characters);
        Tracking = new CharacterTrackingService(Esi, Sso, Characters, Options);
    }

    public void Dispose() => _httpClient.Dispose();
}
