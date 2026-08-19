using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdenToolkit.Core;

public sealed class EveSsoService(HttpClient httpClient, EdenOptions options, CharacterStore store)
{
    public static readonly string[] TrackingScopes =
    [
        "esi-location.read_location.v1", "esi-assets.read_assets.v1",
        "esi-wallet.read_character_wallet.v1", "esi-skills.read_skills.v1"
    ];
    private static readonly Uri MetadataUri = new("https://login.eveonline.com/.well-known/oauth-authorization-server");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TrackedCharacter> AuthorizeAsync(string clientId, string redirectUri,
        Action<Uri>? authorizationReady = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var callback = ValidateLoopbackRedirect(redirectUri);
        var metadata = await GetMetadataAsync(cancellationToken);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = callback.AbsoluteUri,
            ["scope"] = string.Join(' ', TrackingScopes), ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        var authorizeUri = new Uri(metadata.AuthorizationEndpoint + "?" + Form(query));
        var callbackTask = WaitForCallbackAsync(callback, cancellationToken);
        authorizationReady?.Invoke(authorizeUri);

        var callbackValues = await callbackTask;
        if (callbackValues.GetValueOrDefault("state") != state) throw new InvalidDataException("SSO callback state did not match; authorization was rejected.");
        if (callbackValues.TryGetValue("error", out var error)) throw new InvalidOperationException($"EVE SSO authorization failed: {error}");
        if (!callbackValues.TryGetValue("code", out var code)) throw new InvalidDataException("SSO callback did not contain an authorization code.");

        var token = await RequestTokenAsync(metadata.TokenEndpoint, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["code_verifier"] = verifier, ["redirect_uri"] = callback.AbsoluteUri
        }, cancellationToken);
        var identity = await ValidateTokenAsync(token.AccessToken, clientId, metadata, cancellationToken);
        var missingScopes = TrackingScopes.Except(identity.Scopes, StringComparer.Ordinal).ToArray();
        if (missingScopes.Length > 0) throw new InvalidDataException($"EVE SSO token is missing required scopes: {string.Join(", ", missingScopes)}");
        var character = new TrackedCharacter(identity.CharacterId, identity.Name, clientId, callback.AbsoluteUri,
            identity.Scopes, DateTimeOffset.UtcNow);
        await store.SaveAsync(character, token.RefreshToken, cancellationToken);
        return character;
    }

    public async Task<string> GetAccessTokenAsync(long characterId, CancellationToken cancellationToken = default)
    {
        var credentials = await store.GetCredentialsAsync(characterId, cancellationToken)
            ?? throw new KeyNotFoundException($"Character {characterId} is not tracked. Run 'eden character add' first.");
        var metadata = await GetMetadataAsync(cancellationToken);
        var token = await RequestTokenAsync(metadata.TokenEndpoint, new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = credentials.RefreshToken,
            ["client_id"] = credentials.Character.ClientId
        }, cancellationToken);
        await ValidateTokenAsync(token.AccessToken, credentials.Character.ClientId, metadata, cancellationToken);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken) && token.RefreshToken != credentials.RefreshToken)
            await store.UpdateRefreshTokenAsync(characterId, token.RefreshToken, cancellationToken);
        return token.AccessToken;
    }

    public static void OpenBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task<SsoMetadata> GetMetadataAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MetadataUri);
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SsoMetadata>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("EVE SSO returned empty metadata.");
    }

    private async Task<TokenResponse> RequestTokenAsync(string endpoint, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(values) };
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"EVE SSO token request failed ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions) ?? throw new InvalidDataException("EVE SSO returned an invalid token response.");
    }

    private async Task<TokenIdentity> ValidateTokenAsync(string token, string clientId, SsoMetadata metadata, CancellationToken cancellationToken)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) throw new InvalidDataException("EVE SSO access token is not a JWT.");
        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw new InvalidDataException("Unsupported EVE SSO JWT algorithm.");
        var kid = header.RootElement.GetProperty("kid").GetString();
        using var jwksResponse = await httpClient.GetAsync(metadata.JwksUri, cancellationToken);
        jwksResponse.EnsureSuccessStatusCode();
        using var jwks = JsonDocument.Parse(await jwksResponse.Content.ReadAsByteArrayAsync(cancellationToken));
        var key = jwks.RootElement.GetProperty("keys").EnumerateArray().FirstOrDefault(item => item.GetProperty("kid").GetString() == kid);
        if (key.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("EVE SSO JWT signing key was not found.");
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Base64UrlDecode(key.GetProperty("n").GetString()!), Exponent = Base64UrlDecode(key.GetProperty("e").GetString()!) });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("EVE SSO JWT signature is invalid.");

        var claims = payload.RootElement;
        var issuer = claims.GetProperty("iss").GetString();
        if (issuer is not ("https://login.eveonline.com/" or "https://login.eveonline.com" or "login.eveonline.com")) throw new InvalidDataException("EVE SSO JWT issuer is invalid.");
        if (claims.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30) throw new InvalidDataException("EVE SSO JWT is expired.");
        var audiences = claims.GetProperty("aud").ValueKind == JsonValueKind.Array
            ? claims.GetProperty("aud").EnumerateArray().Select(value => value.GetString()).ToArray()
            : [claims.GetProperty("aud").GetString()];
        if (!audiences.Contains(clientId) || !audiences.Contains("EVE Online")) throw new InvalidDataException("EVE SSO JWT audience is invalid.");
        var subject = claims.GetProperty("sub").GetString() ?? string.Empty;
        if (!subject.StartsWith("CHARACTER:EVE:", StringComparison.Ordinal) || !long.TryParse(subject[14..], out var characterId))
            throw new InvalidDataException("EVE SSO JWT character subject is invalid.");
        var scopes = claims.TryGetProperty("scp", out var scopeClaim) && scopeClaim.ValueKind == JsonValueKind.Array
            ? scopeClaim.EnumerateArray().Select(value => value.GetString()!).ToArray() : [];
        return new(characterId, claims.GetProperty("name").GetString() ?? characterId.ToString(), scopes);
    }

    private static Uri ValidateLoopbackRedirect(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            uri.Host is not ("localhost" or "127.0.0.1") || uri.Port <= 0)
            throw new ArgumentException("Redirect URI must be an HTTP loopback URL with a port, such as http://localhost:52731/callback/.");
        return uri;
    }

    private static async Task<Dictionary<string, string>> WaitForCallbackAsync(Uri callback, CancellationToken cancellationToken)
    {
        var address = callback.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(callback.Host);
        var listener = new TcpListener(address, callback.Port);
        listener.Start();
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? throw new InvalidDataException("Empty OAuth callback.");
            var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? throw new InvalidDataException("Invalid OAuth callback.");
            var uri = new Uri($"http://{callback.Host}:{callback.Port}{target}");
            if (!string.Equals(uri.AbsolutePath, callback.AbsolutePath, StringComparison.Ordinal))
                throw new InvalidDataException("OAuth callback path did not match the registered redirect URI.");
            var values = ParseQuery(uri.Query);
            var message = values.ContainsKey("code") ? "EdenToolkit authorization complete. You may close this window." : "EdenToolkit authorization failed. Return to the terminal.";
            var body = Encoding.UTF8.GetBytes($"<!doctype html><title>EdenToolkit</title><h1>{WebUtility.HtmlEncode(message)}</h1>");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            return values;
        }
        finally { listener.Stop(); }
    }

    private static string Form(IEnumerable<KeyValuePair<string, string>> values) => string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part.ElementAtOrDefault(1) ?? string.Empty));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    private sealed record SsoMetadata(
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
        [property: JsonPropertyName("jwks_uri")] string JwksUri);
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
    private sealed record TokenIdentity(long CharacterId, string Name, string[] Scopes);
}
