using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EdenToolkit.Core;

public sealed record EsiResult(JsonElement Data, bool FromCache, bool IsStale, DateTimeOffset ExpiresAt, int Pages = 1);

public sealed class EsiClient(HttpClient httpClient, EdenOptions options, FileResponseCache cache)
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true };

    public async Task<EsiResult> GetAsync(string pathAndQuery, bool refresh = false, CancellationToken cancellationToken = default)
        => await GetCoreAsync(pathAndQuery, null, null, refresh, cancellationToken);

    public async Task<EsiResult> GetAuthorizedAsync(string pathAndQuery, string accessToken, long characterId,
        bool refresh = false, CancellationToken cancellationToken = default)
        => await GetCoreAsync(pathAndQuery, accessToken, $"character:{characterId}:", refresh, cancellationToken);

    private async Task<EsiResult> GetCoreAsync(string pathAndQuery, string? accessToken, string? cachePartition,
        bool refresh, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(pathAndQuery, UriKind.Absolute, out _))
            throw new ArgumentException("ESI path must be relative, for example 'latest/status/'.", nameof(pathAndQuery));
        var path = pathAndQuery.TrimStart('/');
        if (path.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("ESI path cannot contain '..'.", nameof(pathAndQuery));
        var uri = new Uri(options.EsiBaseUri, path);
        var key = cache.KeyFor((cachePartition ?? "public:") + uri.AbsoluteUri);
        var cached = await cache.ReadAsync(key, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (!refresh && cached is { } fresh && fresh.Metadata.ExpiresAt > now)
            return Parse(fresh.Content, true, false, fresh.Metadata.ExpiresAt, fresh.Metadata.Pages);

        try
        {
            using var response = await SendWithRetryAsync(uri, accessToken, cached?.Metadata, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is { } unchanged)
            {
                var expires = GetExpiry(response, now, options.DefaultCacheLifetime);
                var metadata = unchanged.Metadata with { StoredAt = now, ExpiresAt = expires };
                await cache.WriteAsync(key, metadata, unchanged.Content, cancellationToken);
                return Parse(unchanged.Content, true, false, expires, unchanged.Metadata.Pages);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = bytes.Length == 0 ? response.ReasonPhrase : System.Text.Encoding.UTF8.GetString(bytes);
                throw new EsiException((int)response.StatusCode, detail ?? "ESI request failed.",
                    response.Headers.TryGetValues("X-Esi-Error-Limit-Remain", out var values) ? values.FirstOrDefault() : null);
            }

            var expiry = GetExpiry(response, now, options.DefaultCacheLifetime);
            var pages = response.Headers.TryGetValues("X-Pages", out var pageValues) && int.TryParse(pageValues.FirstOrDefault(), out var parsedPages) ? parsedPages : 1;
            var newMetadata = new CacheMetadata(now, expiry, response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified, response.Content.Headers.ContentType?.ToString() ?? "application/json") { Pages = pages };
            await cache.WriteAsync(key, newMetadata, bytes, cancellationToken);
            return Parse(bytes, false, false, expiry, pages);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && cached is not null && !cancellationToken.IsCancellationRequested)
        {
            return Parse(cached.Value.Content, true, true, cached.Value.Metadata.ExpiresAt, cached.Value.Metadata.Pages);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, string? accessToken, CacheMetadata? cached,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(options.UserAgent);
            request.Headers.TryAddWithoutValidation("X-Compatibility-Date", options.CompatibilityDate);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (cached?.ETag is { } tag) request.Headers.TryAddWithoutValidation("If-None-Match", tag);
            if (cached?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (attempt >= 2 || !ShouldRetry(response.StatusCode)) return response;
            var delay = RetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) => (int)statusCode is 420 or 429 or 502 or 503 or 504;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var delay = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null && response.Headers.TryGetValues("X-Esi-Error-Limit-Reset", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var resetSeconds)) delay = TimeSpan.FromSeconds(resetSeconds);
        var actual = delay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return actual < TimeSpan.Zero ? TimeSpan.Zero : actual > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : actual;
    }

    private static DateTimeOffset GetExpiry(HttpResponseMessage response, DateTimeOffset now, TimeSpan fallback) =>
        response.Content.Headers.Expires
        ?? (response.Headers.CacheControl?.MaxAge is { } maxAge ? now + maxAge : now + fallback);

    private static EsiResult Parse(byte[] bytes, bool fromCache, bool stale, DateTimeOffset expires, int pages)
    {
        using var document = JsonDocument.Parse(bytes, DocumentOptions);
        return new(document.RootElement.Clone(), fromCache, stale, expires, pages);
    }
}

public sealed class EsiException(int statusCode, string message, string? errorLimitRemaining) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ErrorLimitRemaining { get; } = errorLimitRemaining;
}
