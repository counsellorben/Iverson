using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// Regression coverage for Task 5's `.MapGrpcService<TenantAdminGrpcService>().RequireAuthorization("TenantAdmin")`
// wiring in Program.cs. Unlike the unit tests in TenantAdminGrpcServiceTests.cs (which construct
// TenantAdminGrpcService directly and never touch ASP.NET Core's authorization middleware), these
// tests boot the real Program.cs pipeline via AuthTestWebApplicationFactory/GrpcChannel — the same
// pattern TenantLifecycleGrpcServiceAuthorizationPipelineTests uses — so the endpoint-level
// "TenantAdmin" policy is actually enforced by app.UseAuthorization() during these calls.
//
// ListUsers is used as the probe RPC (empty request, no InvalidArgument branch). Its body also
// calls RequireActiveTenantAsync/ITenantStatusCache before ever reaching IAuthentikAdminClient,
// so both dependencies are substituted here (mirroring ActingUserInterceptorSuspensionTests'
// substitution pattern) to isolate "did the TenantAdmin policy gate this call" from unrelated
// downstream behavior.
public class TenantAdminGrpcServiceAuthorizationPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;

    public TenantAdminGrpcServiceAuthorizationPipelineTests(AuthTestWebApplicationFactory factory) =>
        _baseFactory = factory;

    private (TenantAdminGrpcService.TenantAdminGrpcServiceClient client, ILogger<Iverson.Api.Grpc.AuditLog> loggerSpy)
        CreateClient()
    {
        var loggerSpy = Substitute.For<ILogger<Iverson.Api.Grpc.AuditLog>>();
        var fakeTenantStatusCache = Substitute.For<ITenantStatusCache>();
        fakeTenantStatusCache.GetStatusAsync(Arg.Any<string>()).Returns("active");
        var fakeAuthentikAdminClient = Substitute.For<IAuthentikAdminClient>();
        fakeAuthentikAdminClient.ListUsersByTenantAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IEnumerable<AuthentikUser>>([]));

        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILogger<Iverson.Api.Grpc.AuditLog>>();
                services.AddSingleton(loggerSpy);
                services.RemoveAll<ITenantStatusCache>();
                services.AddSingleton(fakeTenantStatusCache);
                services.RemoveAll<IAuthentikAdminClient>();
                services.AddSingleton(fakeAuthentikAdminClient);
            }));
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        return (new TenantAdminGrpcService.TenantAdminGrpcServiceClient(channel), loggerSpy);
    }

    private static Metadata Headers(string? token) => token is null
        ? new Metadata()
        : new Metadata { { "authorization", $"Bearer {token}" } };

    private async Task<RpcException?> TryListUsersAsync(
        TenantAdminGrpcService.TenantAdminGrpcServiceClient client, Metadata headers)
    {
        try
        {
            await client.ListUsersAsync(new ListUsersRequest(), headers);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task ListUsers_NoToken_ThrowsUnauthenticated()
    {
        var (client, _) = CreateClient();

        var ex = await TryListUsersAsync(client, Headers(null));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task ListUsers_AuthenticatedWithoutTenantAdminsGroup_ThrowsPermissionDeniedAndAudits()
    {
        var (client, loggerSpy) = CreateClient();
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "not-a-tenant-admin",
            extraClaims: [new Claim("tenant_id", "acme"), new Claim("groups", "some-other-group")]);

        var ex = await TryListUsersAsync(client, Headers(token));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.PermissionDenied);
        // Rejected by the "TenantAdmin" policy itself (before the RPC body ever runs) — proven by
        // AuditingAuthorizationMiddlewareResultHandler's "PolicyRejected" audit log firing, which
        // only happens for a policy-level Forbidden result, not for an in-body RpcException.
        loggerSpy.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("PolicyRejected")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ListUsers_AuthenticatedWithTenantAdminsGroup_PassesAuthorizationGateAndReturnsResponse()
    {
        var (client, loggerSpy) = CreateClient();
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "human-tenant-admin",
            extraClaims: [new Claim("tenant_id", "acme"), new Claim("groups", "tenant-admins")]);

        var ex = await TryListUsersAsync(client, Headers(token));

        // No exception at all — reaching a real (empty) ListUsersResponse from the fake
        // IAuthentikAdminClient proves the TenantAdmin gate let this call through to the method
        // body (which also cleared the inline RequireActiveTenantAsync check via the fake cache).
        ex.Should().BeNull();
        // And crucially, the policy-rejection audit path must NOT have fired for an authorized caller.
        loggerSpy.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
