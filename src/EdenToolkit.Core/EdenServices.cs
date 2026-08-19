namespace EdenToolkit.Core;

public sealed class EdenServices : IDisposable
{
    private readonly HttpClient _httpClient;
    public EdenOptions Options { get; }
    public EsiClient Esi { get; }
    public SdeService Sde { get; }
    public CharacterStore Characters { get; }
    public CorporationStore Corporations { get; }
    public EveSsoService Sso { get; }
    public CharacterTrackingService Tracking { get; }
    public CorporationTrackingService CorporationTracking { get; }
    public CharacterDataRepository CharacterData { get; }
    public MarketDataService Market { get; }
    public InventoryService Inventory { get; }

    public EdenServices(EdenOptions? options = null, HttpMessageHandler? handler = null)
    {
        Options = options ?? EdenOptions.FromEnvironment();
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        var cache = new FileResponseCache(Options);
        Esi = new EsiClient(_httpClient, Options, cache);
        Sde = new SdeService(_httpClient, Options);
        Characters = new CharacterStore(Options);
        Corporations = new CorporationStore(Options);
        Sso = new EveSsoService(_httpClient, Options, Characters);
        CharacterData = new CharacterDataRepository(Options);
        Tracking = new CharacterTrackingService(Esi, Sso, Characters, CharacterData);
        CorporationTracking = new CorporationTrackingService(Esi, Sso, Characters, Corporations, CharacterData);
        var marketRepository = new MarketDataRepository(Options);
        Market = new MarketDataService(Esi, Sde, marketRepository);
        Inventory = new InventoryService(CharacterData, Market, Sde);
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<CharacterAndCorporationSyncResult> SyncCharacterAsync(long characterId, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var character = await Tracking.SyncAsync(characterId, refresh, cancellationToken);
        var corporation = await CorporationTracking.SyncForDirectorAsync(characterId, refresh, cancellationToken);
        return new(character, corporation);
    }
}

public sealed record CharacterAndCorporationSyncResult(CharacterSyncResult Character, CorporationSyncResult? Corporation);
