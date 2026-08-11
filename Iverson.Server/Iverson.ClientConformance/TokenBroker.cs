using Grpc.Core;
using Grpc.Net.Client;
using IdentityModel.Client;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Iverson.LoadTest.Auth;
using Microsoft.Extensions.Logging;

namespace Iverson.ClientConformance;

/// <summary>
/// Mints tokens and ensures the tenant every driver runs against exists. The harness runs in the
/// acting user's own tenant (<c>IVERSON_LOADTEST_TENANT_ID</c>) rather than a dedicated
/// conformance tenant — provisioning a conformance-specific tenant would additionally require
/// provisioning the acting-user identity into it first, which is out of scope here.
/// </summary>
public sealed class TokenBroker : IDisposable
{
    private readonly string _grpcUrl;
    private readonly string _tenantId;
    private readonly IversonClientCredentials? _clientCredentials;
    private readonly ActingUserTokenProvider _actingUserTokenProvider;

    public TokenBroker(string grpcUrl, ILoggerFactory loggerFactory)
    {
        _grpcUrl = grpcUrl;

        var clientId = Environment.GetEnvironmentVariable("IVERSON_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("IVERSON_CLIENT_SECRET");
        var tokenEndpoint = Environment.GetEnvironmentVariable("IVERSON_TOKEN_ENDPOINT");
        var clientScope = Environment.GetEnvironmentVariable("IVERSON_CLIENT_SCOPE");
        _clientCredentials = clientId is not null && clientSecret is not null && tokenEndpoint is not null
            ? new IversonClientCredentials(clientId, clientSecret, tokenEndpoint, clientScope,
                HostHeader: Env("IVERSON_ACTING_USER_HOST_HEADER", "authentik-server:9000"))
            : null;

        _tenantId = Env("IVERSON_LOADTEST_TENANT_ID", "iverson-loadtest-dynamic");

        var actingUserClientId = Env("IVERSON_ACTING_USER_CLIENT_ID", "dev-iverson-loadtest-human-client-id");
        var actingUserRedirectUri = Env("IVERSON_ACTING_USER_REDIRECT_URI", "http://localhost/placeholder-callback");
        var actingUserBypassUsername = Env("IVERSON_ACTING_USER_BYPASS_USERNAME", "iverson-loadtest-bypass-user");
        var actingUserBypassPassword = Env("IVERSON_ACTING_USER_BYPASS_PASSWORD", "dev-only-not-for-production-bypass-password-0123456789");
        var actingUserHostHeader = Env("IVERSON_ACTING_USER_HOST_HEADER", "authentik-server:9000");
        var actingUserBaseUrl = DeriveAuthentikBaseUrl(tokenEndpoint);
        // Compose is the only target this task supports; a "kind" target would need the same
        // cache-path vocabulary mapping LoadTest's Program.cs does ("containers"/"kind" ->
        // "compose"/"kind"), which is out of scope until a --target flag exists.
        const string actingUserCacheTarget = "compose";

        _actingUserTokenProvider = new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            new AuthentikIdentityConfig(
                actingUserBypassUsername, actingUserBypassPassword, actingUserClientId, actingUserRedirectUri,
                actingUserBaseUrl, actingUserHostHeader, actingUserCacheTarget),
            loggerFactory.CreateLogger<AuthentikFlowExecutorClient>()));
    }

    /// <summary>
    /// Derives the Authentik base URL the same way <c>Iverson.LoadTest/Program.cs:48-51</c> does:
    /// strip the token endpoint down to its origin when <c>IVERSON_TOKEN_ENDPOINT</c> is set,
    /// otherwise fall back to the compose default. This is the single source of truth for the
    /// value — <see cref="Program"/> computes it once here and feeds the same string to both
    /// <see cref="TokenBroker"/> and <see cref="Preflight"/> so the two never diverge.
    /// </summary>
    public static string DeriveAuthentikBaseUrl(string? tokenEndpoint) => tokenEndpoint is not null
        ? tokenEndpoint[..tokenEndpoint.IndexOf("/application/o/token/", StringComparison.Ordinal)]
        : "http://localhost:9000";

    /// <summary>
    /// The SERVICE identity token — what the server reads out of the <c>authorization</c> header
    /// and evaluates <c>schema_admin</c>/tenant scopes against. Distinct from
    /// <see cref="GetActingTokenAsync"/>, which is the end-user identity the row/field
    /// authorization is evaluated against and rides in <c>x-acting-user-authorization</c>.
    /// </summary>
    public Task<string> GetServiceTokenAsync(CancellationToken ct = default) =>
        _clientCredentials is null
            ? throw new InvalidOperationException(
                "Client credentials not configured (IVERSON_CLIENT_ID/IVERSON_CLIENT_SECRET/IVERSON_TOKEN_ENDPOINT).")
            : MintClientCredentialsTokenAsync(_clientCredentials, ct);

    public Task<string> GetActingTokenAsync(CancellationToken ct = default) =>
        _actingUserTokenProvider.GetTokenAsync(ct);

    public Task<string> GetOwnerIdAsync(CancellationToken ct = default) =>
        _actingUserTokenProvider.GetSubAsync(ct);

    public string TenantId => _tenantId;

    /// <summary>
    /// The tenant every driver must stamp on the rows it writes: the acting user's own
    /// <c>tenant_id</c> claim.
    ///
    /// This is NOT <see cref="TenantId"/> (<c>IVERSON_LOADTEST_TENANT_ID</c>). The server derives
    /// the tenant it scopes reads and writes to from the acting-user token alone
    /// (<c>ObjectMappingGrpcService.cs:245</c>), and rejects any row whose tenant column
    /// disagrees with that claim as a tenant mismatch. Passing the provisioning tenant id to the
    /// drivers instead would have every language write rows the server then refuses to read
    /// back — a harness bug that reads as five identical client defects.
    /// </summary>
    public async Task<string> GetActingTenantAsync(CancellationToken ct = default)
    {
        var claim = ReadClaim(await GetActingTokenAsync(ct), "tenant_id");
        return claim ?? throw new InvalidOperationException(
            "The acting-user token carries no 'tenant_id' claim — the server cannot scope any " +
            "read or write without one. Check the acting-user OAuth provider's scope mappings.");
    }

    private static string? ReadClaim(string jwt, string claim)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
        return document.RootElement.TryGetProperty(claim, out var value) ? value.GetString() : null;
    }

    private static async Task<string> MintClientCredentialsTokenAsync(IversonClientCredentials creds, CancellationToken ct)
    {
        using var http = new HttpClient();
        var request = new ClientCredentialsTokenRequest
        {
            Address = creds.TokenEndpoint,
            ClientId = creds.ClientId,
            ClientSecret = creds.ClientSecret,
            Scope = creds.Scope,
        };
        if (creds.HostHeader is { Length: > 0 } host)
            request.Headers.Host = host;

        var response = await http.RequestClientCredentialsTokenAsync(request, ct);
        if (response.IsError)
            throw new InvalidOperationException($"Failed to acquire admin-automation token: {response.Error}");
        return response.AccessToken!;
    }

    private static string Env(string key, string def) =>
        Environment.GetEnvironmentVariable(key) ?? def;

    public void Dispose() => _actingUserTokenProvider.Dispose();
}
