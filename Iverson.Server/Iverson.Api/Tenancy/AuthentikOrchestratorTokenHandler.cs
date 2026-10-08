using System.Net.Http.Headers;
using System.Text.Json;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round-10 #4 (C2): authenticates the Authentik admin client with the API's own Kubernetes
/// service-account token instead of a long-lived Authentik API token. The kubelet-projected token at
/// the assertion path is exchanged at Authentik's token endpoint (client_credentials with a
/// jwt-bearer client assertion) for a short-lived access token, which is cached until
/// <see cref="RefreshMargin"/> before it expires. The exchange goes through this handler's own inner
/// handler, so it uses the same CA-trusting hop as the admin calls.
/// </summary>
internal sealed class AuthentikOrchestratorTokenHandler(string assertionPath, string clientId, TimeProvider timeProvider)
    : DelegatingHandler
{
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _mint = new(1, 1);
    private CachedToken? _cached;

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(request.RequestUri!, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsFresh(CachedToken? cached) =>
        cached is not null && timeProvider.GetUtcNow() < cached.ExpiresAt - RefreshMargin;

    private async Task<string> GetAccessTokenAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        var cached = _cached;
        if (IsFresh(cached))
            return cached!.Value;

        await _mint.WaitAsync(cancellationToken);
        try
        {
            cached = _cached;
            if (IsFresh(cached))
                return cached!.Value;

            var assertion = await ReadAssertionAsync(cancellationToken);
            using var exchange = new HttpRequestMessage(HttpMethod.Post, new Uri(requestUri, "/application/o/token/"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = assertion,
                    ["scope"] = "goauthentik.io/api",
                }),
            };

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(exchange, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new InvalidOperationException($"Authentik token exchange failed: {ex.Message}", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Authentik token exchange failed: HTTP {(int)response.StatusCode} {ErrorCode(body)}".TrimEnd() + ".");

                using var doc = ParseOrNull(body);
                if (doc is null
                    || !doc.RootElement.TryGetProperty("access_token", out var accessToken)
                    || accessToken.ValueKind != JsonValueKind.String
                    || accessToken.GetString() is not { Length: > 0 } value)
                    throw new InvalidOperationException("Authentik token exchange returned no access_token.");

                var lifetime = doc.RootElement.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : TimeSpan.Zero;
                _cached = new CachedToken(value, timeProvider.GetUtcNow() + lifetime);
                return value;
            }
        }
        finally
        {
            _mint.Release();
        }
    }

    private async Task<string> ReadAssertionAsync(CancellationToken cancellationToken)
    {
        string assertion;
        try
        {
            assertion = await File.ReadAllTextAsync(assertionPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Authentik orchestrator assertion file '{assertionPath}' could not be read.", ex);
        }

        assertion = assertion.Trim();
        if (assertion.Length == 0)
            throw new InvalidOperationException($"Authentik orchestrator assertion file '{assertionPath}' is empty.");
        return assertion;
    }

    // Only Authentik's error code (for example invalid_grant) reaches the message; never the body.
    private static string ErrorCode(string body)
    {
        using var doc = ParseOrNull(body);
        return doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()!
            : "";
    }

    private static JsonDocument? ParseOrNull(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _mint.Dispose();
        base.Dispose(disposing);
    }
}
