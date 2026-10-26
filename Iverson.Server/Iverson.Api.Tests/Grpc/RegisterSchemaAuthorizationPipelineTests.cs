using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// Regression coverage for the Critical finding fixed by this task: RegisterSchema previously had
// zero authorization check beyond the global FallbackPolicy's RequireAuthenticatedUser(), so any
// caller holding a JWT for any configured audience could register/redefine schemas. Unlike
// RegisterSchemaAuthorizationIntegrationTests (which constructs ObjectMappingGrpcService directly
// and therefore never exercises ASP.NET Core's authorization middleware at all), these tests boot
// the real Program.cs pipeline via AuthTestWebApplicationFactory/GrpcChannel — the same pattern
// ActingUserInterceptorTests uses — so the [Authorize(Policy = "SchemaAdmin")] attribute added to
// RegisterSchema is actually enforced by app.UseAuthorization() during these calls.
//
// A successful (policy-satisfied) call still can't be asserted end-to-end against real
// Postgres/StarRocks/Qdrant here — none of those are running in this pipeline-test sandbox, and
// AuthTestWebApplicationFactory only no-ops the *startup* hydration calls, not per-request calls
// a real RegisterSchema body would make. Instead, an empty SchemaRequest (no RootType) is used:
// RegisterSchema's very first line throws InvalidArgument before touching any store, so reaching
// that InvalidArgument (rather than Unauthenticated/PermissionDenied) is proof the authorization
// gate let the call through to the method body.
public class RegisterSchemaAuthorizationPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _factory;
    private readonly ObjectMappingService.ObjectMappingServiceClient _client;

    public RegisterSchemaAuthorizationPipelineTests(AuthTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = ClientFor(factory);
    }

    private static ObjectMappingService.ObjectMappingServiceClient ClientFor(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        return new ObjectMappingService.ObjectMappingServiceClient(channel);
    }

    private static Metadata Headers(string? token) => token is null
        ? new Metadata()
        : new Metadata { { "authorization", $"Bearer {token}" } };

    private Task<RpcException?> TryRegisterEmptySchemaAsync(Metadata headers) =>
        TryRegisterEmptySchemaAsync(_client, headers);

    private static async Task<RpcException?> TryRegisterEmptySchemaAsync(
        ObjectMappingService.ObjectMappingServiceClient client, Metadata headers)
    {
        try
        {
            await client.RegisterSchemaAsync(new SchemaRequest(), headers);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task RegisterSchema_NoToken_ThrowsUnauthenticated()
    {
        var ex = await TryRegisterEmptySchemaAsync(Headers(null));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task RegisterSchema_AuthenticatedWithNoGroupsOrSchemaAdminScope_ThrowsPermissionDenied()
    {
        var token = TestJwtFactory.CreateToken("test-service-audience", "ak-some-other-service");

        var ex = await TryRegisterEmptySchemaAsync(Headers(token));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task RegisterSchema_AuthenticatedWithAdminScope_PassesAuthorizationGate()
    {
        // CSR round 12 §2.4: the admin scope makes its holder an Operator, and Operator implies
        // SchemaAdmin (SchemaAdminAuthorizationPolicy).
        var token = TestJwtFactory.CreateToken(
            "test-service-audience",
            "ak-admin-scoped-service",
            extraClaims: [new Claim("scope", "openid admin profile")]);

        var ex = await TryRegisterEmptySchemaAsync(Headers(token));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterSchema_OperatorsGroupWithoutTheComposeFallback_ThrowsPermissionDenied()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "groups-only-operator",
            extraClaims: [new Claim("groups", "operators")]);

        var ex = await TryRegisterEmptySchemaAsync(Headers(token));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // Compose's dev-only fallback (Authorization:OperatorGroupFallback) makes the group an Operator again,
    // and so a SchemaAdmin.
    [Fact]
    public async Task RegisterSchema_OperatorsGroupWithTheComposeFallback_PassesAuthorizationGate()
    {
        var composeLike = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authorization:OperatorGroupFallback", "operators"));
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "groups-only-operator",
            extraClaims: [new Claim("groups", "operators")]);

        var ex = await TryRegisterEmptySchemaAsync(ClientFor(composeLike), Headers(token));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterSchema_SubjectOnTheOperatorList_PassesAuthorizationGate()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", AuthTestWebApplicationFactory.HumanOperatorSubject);

        var ex = await TryRegisterEmptySchemaAsync(Headers(token));

        // Reaching the method body's own InvalidArgument (empty request) — rather than
        // Unauthenticated/PermissionDenied — proves the SchemaAdmin gate let this call through.
        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterSchema_AuthenticatedWithSchemaAdminScope_PassesAuthorizationGate()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience",
            "ak-iverson-loadtest",
            extraClaims: [new Claim("scope", "openid schema_admin profile")]);

        var ex = await TryRegisterEmptySchemaAsync(Headers(token));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
