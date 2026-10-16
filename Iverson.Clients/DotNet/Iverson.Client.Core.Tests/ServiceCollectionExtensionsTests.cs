using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace Iverson.Client.Core.Tests;

/// <summary>
/// Closes CSR round-3 finding #4 for the .NET SDK: <c>AddIversonClient</c> must refuse to
/// attach an <c>actingUserTokenProvider</c> to a plaintext (h2c) endpoint unless the caller has
/// explicitly opted in via <c>allowInsecureChannelCallCredentials: true</c>. The acting-user
/// identity travels as raw <see cref="Grpc.Core.Metadata"/> (see
/// <see cref="ActingUserMetadata.WithActingUser"/>), not as <c>CallCredentials</c>, so
/// grpc-dotnet's own <c>UnsafeUseInsecureChannelCallCredentials</c> guard — which the round-2
/// fix relies on for the service <c>credentials</c>/<c>dataPlaneTokenProvider</c> path — cannot
/// see it. This mirrors the analogous guard already added to the Java, Python, and TypeScript
/// SDKs for the same finding.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static Func<Task<string>> ActingUserTokenProvider => () => Task.FromResult("acting-user-token");

    [Fact]
    public void AddIversonClient_WithActingUserTokenProviderOverPlaintext_ThrowsWithoutOptIn()
    {
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(
            grpcEndpoint: "http://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*allowInsecureChannelCallCredentials*");
    }

    [Fact]
    public void AddIversonClient_WithActingUserTokenProviderOverPlaintext_SucceedsWithOptIn()
    {
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(
            grpcEndpoint: "http://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider,
            allowInsecureChannelCallCredentials: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddIversonClient_WithActingUserTokenProviderOverTls_DoesNotRequireOptIn()
    {
        // Happy-path regression guard: a TLS endpoint must never require the opt-in, with or
        // without an acting-user token provider.
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddIversonClient_WithoutActingUserTokenProviderOverPlaintext_DoesNotRequireOptIn()
    {
        // Happy-path regression guard: no acting-user token provider at all must never trip
        // this guard, regardless of scheme.
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(grpcEndpoint: "http://localhost:5000");

        act.Should().NotThrow();
    }

    [Fact]
    public void AddIversonClient_WithPlaintextTokenEndpoint_ThrowsWithoutOptIn()
    {
        // CSR round-4 finding #2: reject plaintext OAuth2 token endpoints without explicit opt-in
        var services = new ServiceCollection();
        var credentials = new IversonClientCredentials(
            ClientId: "test-client",
            ClientSecret: "test-secret",
            TokenEndpoint: "http://localhost:9000/application/o/token/");

        var act = () => services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            credentials: credentials);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*allowInsecureChannelCallCredentials*");
    }

    [Fact]
    public void AddIversonClient_WithPlaintextTokenEndpoint_SucceedsWithOptIn()
    {
        var services = new ServiceCollection();
        var credentials = new IversonClientCredentials(
            ClientId: "test-client",
            ClientSecret: "test-secret",
            TokenEndpoint: "http://localhost:9000/application/o/token/");

        var act = () => services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            credentials: credentials,
            allowInsecureChannelCallCredentials: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddIversonClient_WithHttpsTokenEndpoint_DoesNotRequireOptIn()
    {
        // Happy-path regression guard: HTTPS token endpoints must never require the opt-in
        var services = new ServiceCollection();
        var credentials = new IversonClientCredentials(
            ClientId: "test-client",
            ClientSecret: "test-secret",
            TokenEndpoint: "https://auth.example.com/oauth/token");

        var act = () => services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            credentials: credentials);

        act.Should().NotThrow();
    }

    // WithActingUser (EntityCoordinator) refuses a per-call token when this bit is set; it must be
    // set exactly when the endpoint is plaintext and the caller did not opt in.
    [Theory]
    [InlineData("http://localhost:5000", false, true)]
    [InlineData("http://localhost:5000", true, false)]
    [InlineData("https://localhost:5000", false, false)]
    public void AddIversonClient_SetsRefusesPlaintextTokens_ForAPlaintextEndpointWithoutOptIn(
        string endpoint, bool optIn, bool expected)
    {
        var services = new ServiceCollection();
        services.AddIversonClient(grpcEndpoint: endpoint, allowInsecureChannelCallCredentials: optIn);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ActingUserIdentity>().RefusesPlaintextTokens.Should().Be(expected);
    }

    // CSR round-11 #22: AddIversonClient hands SchemaCatalogClient its ActingUserIdentity singleton,
    // so the catalog call carries the ambient acting-user token.
    [Fact]
    public async Task AddIversonClient_SchemaCatalogClient_SendsTheAmbientActingUserToken()
    {
        string? seen = null;
        var services = new ServiceCollection();
        services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider);
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b =>
            b.PrimaryHandler = new RecordingHandler(header => seen = header, ActingUserMetadata.MetadataKey)));
        using var provider = services.BuildServiceProvider();

        try { await provider.GetRequiredService<SchemaCatalogClient>().GetSchemaAsync(); } catch (RpcException) { }

        seen.Should().Be("Bearer acting-user-token");
    }

    // ---- CSR round-11 #9: the service-token provider ------------------------------------------------

    private static Func<Task<string>> ServiceTokenProvider => () => Task.FromResult("service-token");

    [Fact]
    public void AddIversonClient_WithCredentialsAndServiceTokenProvider_Throws()
    {
        var services = new ServiceCollection();
        var credentials = new IversonClientCredentials("test-client", "test-secret", "https://auth.example.com/oauth/token");

        var act = () => services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            credentials: credentials,
            serviceTokenProvider: ServiceTokenProvider);

        act.Should().Throw<InvalidOperationException>().WithMessage("*credentials*serviceTokenProvider*");
    }

    [Fact]
    public async Task AddIversonClient_WithServiceTokenProvider_AuthenticatesAllFourClients()
    {
        var seen = await AuthorizationHeadersAsync(serviceTokenProvider: ServiceTokenProvider);

        seen.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            [nameof(ObjectMappingService.ObjectMappingServiceClient)] = "Bearer service-token",
            [nameof(ObjectPersistenceService.ObjectPersistenceServiceClient)] = "Bearer service-token",
            [nameof(ObjectRetrievalService.ObjectRetrievalServiceClient)] = "Bearer service-token",
            [nameof(ObjectSearchService.ObjectSearchServiceClient)] = "Bearer service-token",
        });
    }

    [Fact]
    public async Task AddIversonClient_WithServiceTokenProvider_LeavesTheDataPlaneToItsOwnProvider()
    {
        var seen = await AuthorizationHeadersAsync(
            serviceTokenProvider: ServiceTokenProvider,
            dataPlaneTokenProvider: () => Task.FromResult("data-plane-token"));

        seen.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            [nameof(ObjectMappingService.ObjectMappingServiceClient)] = "Bearer service-token",
            [nameof(ObjectPersistenceService.ObjectPersistenceServiceClient)] = "Bearer data-plane-token",
            [nameof(ObjectRetrievalService.ObjectRetrievalServiceClient)] = "Bearer data-plane-token",
            [nameof(ObjectSearchService.ObjectSearchServiceClient)] = "Bearer data-plane-token",
        });
    }

    // Observed: grpc-dotnet refuses to build the first client (an InvalidOperationException naming
    // the TLS requirement), so nothing is sent and the token is never attached.
    [Fact]
    public async Task AddIversonClient_WithServiceTokenProviderOverPlaintext_RefusesToBuildAClientWithoutOptIn()
    {
        var act = () => AuthorizationHeadersAsync(
            serviceTokenProvider: ServiceTokenProvider, endpoint: "http://localhost:5000");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*requires TLS*");
    }

    [Fact]
    public async Task AddIversonClient_WithServiceTokenProviderOverPlaintext_SendsTheTokenWithOptIn()
    {
        var seen = await AuthorizationHeadersAsync(
            serviceTokenProvider: ServiceTokenProvider, endpoint: "http://localhost:5000", allowInsecure: true);

        seen.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            [nameof(ObjectMappingService.ObjectMappingServiceClient)] = "Bearer service-token",
            [nameof(ObjectPersistenceService.ObjectPersistenceServiceClient)] = "Bearer service-token",
            [nameof(ObjectRetrievalService.ObjectRetrievalServiceClient)] = "Bearer service-token",
            [nameof(ObjectSearchService.ObjectSearchServiceClient)] = "Bearer service-token",
        });
    }

    /// <summary>
    /// Registers the clients against <paramref name="endpoint"/>, replaces every client's transport
    /// with a handler that records the request's authorization header (keyed by the client's name)
    /// and fails the call, makes one unary call per client, and returns what each sent.
    /// </summary>
    private static async Task<Dictionary<string, string?>> AuthorizationHeadersAsync(
        Func<Task<string>>? serviceTokenProvider = null, Func<Task<string>>? dataPlaneTokenProvider = null,
        string endpoint = "https://localhost:5000", bool allowInsecure = false)
    {
        var seen = new Dictionary<string, string?>();
        var services = new ServiceCollection();
        services.AddIversonClient(
            grpcEndpoint: endpoint,
            dataPlaneTokenProvider: dataPlaneTokenProvider,
            serviceTokenProvider: serviceTokenProvider,
            allowInsecureChannelCallCredentials: allowInsecure);
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b =>
            b.PrimaryHandler = new RecordingHandler(header => seen[b.Name!] = header)));
        using var provider = services.BuildServiceProvider();

        await Swallow(provider.GetRequiredService<ObjectMappingService.ObjectMappingServiceClient>().GetSchemaAsync(new GetSchemaRequest()).ResponseAsync);
        await Swallow(provider.GetRequiredService<ObjectPersistenceService.ObjectPersistenceServiceClient>().PostAsync(new PersistRequest()).ResponseAsync);
        await Swallow(provider.GetRequiredService<ObjectRetrievalService.ObjectRetrievalServiceClient>().GetAsync(new RetrievalRequest()).ResponseAsync);
        await Swallow(provider.GetRequiredService<ObjectSearchService.ObjectSearchServiceClient>().AggregateAsync(new AggregateRequest()).ResponseAsync);
        return seen;

        static async Task Swallow(Task call)
        {
            try { await call; } catch (RpcException) { }
        }
    }

    private sealed class RecordingHandler(Action<string?> record, string header = "authorization") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            record(request.Headers.TryGetValues(header, out var values) ? values.Single() : null);
            throw new HttpRequestException("recorded");
        }
    }
}
