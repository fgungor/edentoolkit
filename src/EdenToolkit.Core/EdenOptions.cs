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
    public bool Adam4EveEnabled { get; init; }
    public Uri Adam4EveBaseUri { get; init; } = new("https://api.adam4eve.eu/");
    public string? Adam4EveUserAgent { get; init; }
    public TimeSpan Adam4EveMinimumRequestInterval { get; init; } = TimeSpan.FromSeconds(5);
    public decimal Adam4EveTargetMarketParticipation { get; init; } = 0.05m;

    public static EdenOptions FromEnvironment() => new()
    {
        CacheDirectory = Environment.GetEnvironmentVariable("EDEN_CACHE_DIR") is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : new EdenOptions().CacheDirectory,
        CompatibilityDate = Environment.GetEnvironmentVariable("EDEN_ESI_COMPATIBILITY_DATE") ?? "2026-08-19",
        UserAgent = Environment.GetEnvironmentVariable("EDEN_USER_AGENT") ?? "EdenToolkit/0.1 (EVE Online companion; contact: local-user)",
        EveClientId = Environment.GetEnvironmentVariable("EDEN_EVE_CLIENT_ID") ?? "af9734cd1ffb43a2bfa969e0e1653cb5",
        Adam4EveEnabled = bool.TryParse(Environment.GetEnvironmentVariable("EDEN_ADAM4EVE_ENABLED"), out var enabled) && enabled,
        Adam4EveBaseUri = new Uri(Environment.GetEnvironmentVariable("EDEN_ADAM4EVE_BASE_URL") ?? "https://api.adam4eve.eu/"),
        Adam4EveUserAgent = Environment.GetEnvironmentVariable("EDEN_ADAM4EVE_USER_AGENT"),
        Adam4EveMinimumRequestInterval = TimeSpan.FromSeconds(double.TryParse(
            Environment.GetEnvironmentVariable("EDEN_ADAM4EVE_MIN_INTERVAL_SECONDS"), out var seconds) ? Math.Max(0, seconds) : 5),
        Adam4EveTargetMarketParticipation = decimal.TryParse(
            Environment.GetEnvironmentVariable("EDEN_ADAM4EVE_TARGET_PARTICIPATION"), out var participation)
                ? Math.Clamp(participation, 0.001m, 1m) : 0.05m
    };
}
