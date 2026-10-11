using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IdentityModel.Client;
using Iverson.Client.Core;

namespace Iverson.LoadTest.Auth;

/// <summary>
/// The admin-automation identity LoadTest provisions tenants and registers schemas with, from
/// <c>IVERSON_CLIENT_ID</c>, <c>IVERSON_TOKEN_ENDPOINT</c>, <c>IVERSON_CLIENT_SCOPE</c> and either
/// <c>IVERSON_CLIENT_SECRET</c> (compose) or <c>IVERSON_CLIENT_ASSERTION_FILE</c>. On Helm the
/// admin-automation client has no usable secret (CSR round-11 #9): the caller mints a short-lived
/// token of the <c>&lt;release&gt;-admin-automation</c> ServiceAccount into a file, and LoadTest sends
/// it as a jwt-bearer client assertion. The file is read again for every token request, so a run
/// that outlasts one Operator token (1 hour) picks up a refreshed file.
/// </summary>
public sealed class AdminAutomationCredentials : IDisposable
{
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public AdminAutomationCredentials(
        string clientId, string tokenEndpoint, string? clientSecret, string? assertionFile,
        string? scope, string? hostHeader, HttpMessageHandler? handler = null)
    {
        ClientId = clientId;
        TokenEndpoint = tokenEndpoint;
        ClientSecret = clientSecret;
        AssertionFile = assertionFile;
        Scope = scope;
        HostHeader = hostHeader;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public string ClientId { get; }
    public string TokenEndpoint { get; }
    public string? ClientSecret { get; }
    public string? AssertionFile { get; }
    public string? Scope { get; }
    public string? HostHeader { get; }

    public bool UsesAssertion => AssertionFile is not null;

    /// <summary>
    /// The configured identity, or null when there is none: no client id, no token endpoint, or
    /// neither a secret nor an assertion file. An assertion file takes precedence over a secret.
    /// </summary>
    public static AdminAutomationCredentials? FromEnvironment(Func<string, string?> env, string? hostHeader)
    {
        var clientId = env("IVERSON_CLIENT_ID");
        var tokenEndpoint = env("IVERSON_TOKEN_ENDPOINT");
        var secret = env("IVERSON_CLIENT_SECRET");
        var assertionFile = env("IVERSON_CLIENT_ASSERTION_FILE") is { Length: > 0 } file ? file : null;
        if (clientId is null || tokenEndpoint is null || (secret is null && assertionFile is null))
            return null;
        return new AdminAutomationCredentials(
            clientId, tokenEndpoint, assertionFile is null ? secret : null, assertionFile,
            env("IVERSON_CLIENT_SCOPE"), hostHeader);
    }

    /// <summary>The SDK's own client-credentials identity, in secret mode only.</summary>
    public IversonClientCredentials? SdkCredentials =>
        UsesAssertion ? null : new IversonClientCredentials(ClientId, ClientSecret!, TokenEndpoint, Scope, HostHeader);

    /// <summary>An Operator access token, re-used until a minute before it expires.</summary>
    public async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _token;

        await _lock.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _token;

            using var request = BuildRequest();
            var response = await _http.RequestClientCredentialsTokenAsync(request);
            if (response.IsError)
                throw new InvalidOperationException($"Failed to acquire admin-automation token: {response.Error}");
            _token = response.AccessToken!;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn - 60);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private ClientCredentialsTokenRequest BuildRequest()
    {
        var request = new ClientCredentialsTokenRequest
        {
            Address = TokenEndpoint,
            ClientId = ClientId,
            Scope = Scope,
        };
        if (AssertionFile is not null)
        {
            if (!File.Exists(AssertionFile))
                throw new InvalidOperationException(
                    $"IVERSON_CLIENT_ASSERTION_FILE names '{AssertionFile}', which does not exist. Mint a token with " +
                    "`kubectl create token <release>-admin-automation --audience iverson-authentik-admin-automation " +
                    "--duration 10m` and write it there.");
            request.ClientAssertion = new ClientAssertion
            {
                Type = JwtBearerAssertionType,
                Value = File.ReadAllText(AssertionFile).Trim(),
            };
            request.ClientCredentialStyle = ClientCredentialStyle.PostBody;
        }
        else
        {
            request.ClientSecret = ClientSecret;
        }
        // Authentik's issuer_mode:global stamps the token's `iss` from the request's Host header, so a
        // token minted through a host-exposed port names an issuer the API rejects unless the request
        // carries the in-network host (IversonClientCredentials.HostHeader).
        if (HostHeader is { Length: > 0 } host)
            request.Headers.Host = host;
        return request;
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}
