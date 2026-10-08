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
/// <remarks>
/// The cache lives in an <see cref="AuthentikOrchestratorTokenCache"/> that outlives the handler, because
/// <c>IHttpClientFactory</c> rebuilds the handler chain every couple of minutes. A token Authentik revokes
/// early stays cached until it expires (at most 5 minutes), because the handler does not retry on an
/// error status: Authentik answers an invalid bearer with 403, the same status as a permission refusal.
/// </remarks>
internal sealed class AuthentikOrchestratorTokenHandler(
    string assertionPath, string clientId, TimeProvider timeProvider, AuthentikOrchestratorTokenCache cache)
    : DelegatingHandler
{
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    public AuthentikOrchestratorTokenHandler(string assertionPath, string clientId, TimeProvider timeProvider)
        : this(assertionPath, clientId, timeProvider, new AuthentikOrchestratorTokenCache())
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(request.RequestUri!, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsFresh(AuthentikOrchestratorTokenCache.CachedToken? cached) =>
        cached is not null && timeProvider.GetUtcNow() < cached.ExpiresAt - RefreshMargin;

    private async Task<string> GetAccessTokenAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        var cached = cache.Current;
        if (IsFresh(cached))
            return cached!.Value;

        await cache.Mint.WaitAsync(cancellationToken);
        try
        {
            cached = cache.Current;
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
                    || doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("access_token", out var accessToken)
                    || accessToken.ValueKind != JsonValueKind.String
                    || accessToken.GetString() is not { Length: > 0 } value)
                    throw new InvalidOperationException("Authentik token exchange returned no access_token.");

                // Authentik always sends a positive integer expires_in; without one the token serves this call only.
                if (doc.RootElement.TryGetProperty("expires_in", out var expiresIn)
                    && expiresIn.ValueKind == JsonValueKind.Number
                    && expiresIn.TryGetInt32(out var seconds)
                    && seconds > 0)
                    cache.Current = new AuthentikOrchestratorTokenCache.CachedToken(value, timeProvider.GetUtcNow() + TimeSpan.FromSeconds(seconds));
                return value;
            }
        }
        finally
        {
            cache.Mint.Release();
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
}

/// <summary>
/// The orchestrator access token and the semaphore that serialises minting it, shared by every
/// <see cref="AuthentikOrchestratorTokenHandler"/> the HTTP client factory builds. It lives for the
/// process, so it is not disposed.
/// </summary>
internal sealed class AuthentikOrchestratorTokenCache
{
    private CachedToken? _current;

    internal sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    internal SemaphoreSlim Mint { get; } = new(1, 1);

    internal CachedToken? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
