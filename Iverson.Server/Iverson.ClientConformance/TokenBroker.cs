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
        var actingUserBaseUrl = tokenEndpoint is not null
            ? tokenEndpoint[..tokenEndpoint.IndexOf("/application/o/token/", StringComparison.Ordinal)]
            : "http://localhost:9000";
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

    public Task<string> GetActingTokenAsync(CancellationToken ct = default) =>
        _actingUserTokenProvider.GetTokenAsync(ct);

    public Task<string> GetOwnerIdAsync(CancellationToken ct = default) =>
        _actingUserTokenProvider.GetSubAsync(ct);

    public string TenantId => _tenantId;

    /// <summary>
    /// Ensures <see cref="TenantId"/> exists, creating it only when absent. Requires client
    /// credentials (<c>IVERSON_CLIENT_ID</c>/<c>IVERSON_CLIENT_SECRET</c>/<c>IVERSON_TOKEN_ENDPOINT</c>)
    /// scoped to admin/schema_admin.
    /// </summary>
    public async Task EnsureTenantProvisionedAsync(CancellationToken ct = default)
    {
        if (_clientCredentials is null)
            throw new InvalidOperationException(
                "Client credentials not configured (IVERSON_CLIENT_ID/IVERSON_CLIENT_SECRET/IVERSON_TOKEN_ENDPOINT) — cannot provision the tenant.");

        var adminToken = await MintClientCredentialsTokenAsync(_clientCredentials, ct);

        using var channel = GrpcChannel.ForAddress(_grpcUrl);
        var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
        var headers = new Metadata { { "authorization", $"Bearer {adminToken}" } };

        var existing = await client.ListTenantsAsync(new ListTenantsRequest(), headers, cancellationToken: ct);
        if (existing.Tenants.Any(t => t.TenantId == _tenantId))
            return;

        var tenantAdminUsername = Env("IVERSON_LOADTEST_TENANT_ADMIN_USERNAME", "iverson-loadtest-tenant-admin");
        var tenantAdminEmail = Env("IVERSON_LOADTEST_TENANT_ADMIN_EMAIL", "iverson-loadtest-tenant-admin@iverson.local");
        var tenantAdminPassword = Env("IVERSON_LOADTEST_TENANT_ADMIN_PASSWORD", "dev-only-not-for-production-tenant-admin-password-0123456789");

        await client.CreateTenantAsync(new CreateTenantRequest
        {
            TenantId = _tenantId,
            DisplayName = "Iverson LoadTest (dynamic)",
            AdminUsername = tenantAdminUsername,
            AdminEmail = tenantAdminEmail,
            AdminInitialPassword = tenantAdminPassword,
        }, headers, cancellationToken: ct);
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
