namespace EdenToolkit.Core;

public sealed record EdenOptions
{
    public string CacheDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdenToolkit");
    public Uri EsiBaseUri { get; init; } = new("https://esi.evetech.net/");
    public Uri SdeUri { get; init; } = new("https://developers.eveonline.com/static-data/eve-online-static-data-latest-jsonl.zip");
    public string CompatibilityDate { get; init; } = "2026-08-19";
    public string UserAgent { get; init; } = "EdenToolkit/0.1 (EVE Online companion; contact: local-user)";
    public string EveClientId { get; init; } = "af9734cd1ffb43a2bfa969e0e1653cb5";
    public TimeSpan DefaultCacheLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public static EdenOptions FromEnvironment() => new()
    {
        CacheDirectory = Environment.GetEnvironmentVariable("EDEN_CACHE_DIR") is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : new EdenOptions().CacheDirectory,
        CompatibilityDate = Environment.GetEnvironmentVariable("EDEN_ESI_COMPATIBILITY_DATE") ?? "2026-08-19",
        UserAgent = Environment.GetEnvironmentVariable("EDEN_USER_AGENT") ?? "EdenToolkit/0.1 (EVE Online companion; contact: local-user)",
        EveClientId = Environment.GetEnvironmentVariable("EDEN_EVE_CLIENT_ID") ?? "af9734cd1ffb43a2bfa969e0e1653cb5"
    };
}
