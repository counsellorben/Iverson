using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// Regression coverage for Task 4's `.MapGrpcService<TenantLifecycleGrpcService>().RequireAuthorization("Operator")`
// wiring in Program.cs. Unlike the unit tests in TenantLifecycleGrpcServiceTests.cs (which construct
// TenantLifecycleGrpcService directly and never touch ASP.NET Core's authorization middleware), these
// tests boot the real Program.cs pipeline via AuthTestWebApplicationFactory/GrpcChannel — the same
// pattern RegisterSchemaAuthorizationPipelineTests uses — so the endpoint-level "Operator" policy is
// actually enforced by app.UseAuthorization() during these calls.
//
// ListTenants is used as the probe RPC: it takes an empty request (no InvalidArgument branch to
// conflate with authz outcomes) and its body only touches ITenantRepository, which
// AuthTestWebApplicationFactory already replaces with NoOpTenantRepository — so a call that clears
// the "Operator" gate returns a real (empty) ListTenantsResponse rather than throwing for an
// unrelated reason.
public class TenantLifecycleGrpcServiceAuthorizationPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient _client;

    public TenantLifecycleGrpcServiceAuthorizationPipelineTests(AuthTestWebApplicationFactory factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        _client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
    }

    private static Metadata Headers(string? token) => token is null
        ? new Metadata()
        : new Metadata { { "authorization", $"Bearer {token}" } };

    private async Task<RpcException?> TryListTenantsAsync(Metadata headers)
    {
        try
        {
            await _client.ListTenantsAsync(new ListTenantsRequest(), headers);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task ListTenants_NoToken_ThrowsUnauthenticated()
    {
        var ex = await TryListTenantsAsync(Headers(null));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task ListTenants_AuthenticatedWithNoGroupsOrAdminScope_ThrowsPermissionDenied()
    {
        var token = TestJwtFactory.CreateToken("test-service-audience", "ak-some-other-service");

        var ex = await TryListTenantsAsync(Headers(token));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task ListTenants_AuthenticatedWithOperatorsGroup_PassesAuthorizationGateAndReturnsResponse()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "human-operator",
            extraClaims: [new Claim("groups", "operators")]);

        var ex = await TryListTenantsAsync(Headers(token));

        // No exception at all — reaching a real (empty) ListTenantsResponse from NoOpTenantRepository
        // proves the Operator gate let this call through to the method body.
        ex.Should().BeNull();
    }

    [Fact]
    public async Task ListTenants_AuthenticatedWithAdminScope_PassesAuthorizationGateAndReturnsResponse()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "ak-iverson-automation",
            extraClaims: [new Claim("scope", "openid admin profile")]);

        var ex = await TryListTenantsAsync(Headers(token));

        ex.Should().BeNull();
    }
}
