using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Grpc;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// Boots the real Program.cs pipeline (same base fixture as ActingUserInterceptorTests), substituting
// ITenantStatusCache per test via WithWebHostBuilder so a suspended/deleted/unknown tenant status
// can be asserted without a real Postgres-backed ITenantRepository — mirrors the substitution
// pattern in AuditingAuthorizationMiddlewareResultHandlerTests.cs.
public class ActingUserInterceptorSuspensionTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;

    public ActingUserInterceptorSuspensionTests(AuthTestWebApplicationFactory factory) =>
        _baseFactory = factory;

    private ObjectSearchService.ObjectSearchServiceClient CreateClientWithFakeTenantStatusCache(
        ITenantStatusCache fakeCache)
    {
        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITenantStatusCache>();
                services.AddSingleton(fakeCache);
            }));
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        return new ObjectSearchService.ObjectSearchServiceClient(channel);
    }

    private static Metadata HeadersWithActingUser(string tenantId)
    {
        var headers = new Metadata
        {
            { "authorization", $"Bearer {TestJwtFactory.CreateToken("test-service-audience", "ak-test-service")}" }
        };
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience",
            "test-human-sub",
            extraClaims: [new Claim("tenant_id", tenantId)]);
        headers.Add(ActingUserInterceptor.MetadataKey, $"Bearer {actingUserToken}");
        return headers;
    }

    private async Task<RpcException?> TryAggregateAsync(
        ObjectSearchService.ObjectSearchServiceClient client, Metadata headers)
    {
        try
        {
            await client.AggregateAsync(new AggregateRequest(), headers);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task Call_SuspendedTenant_ThrowsPermissionDenied()
    {
        var fakeCache = Substitute.For<ITenantStatusCache>();
        fakeCache.GetStatusAsync("suspended-tenant").Returns("suspended");
        var client = CreateClientWithFakeTenantStatusCache(fakeCache);

        var ex = await TryAggregateAsync(client, HeadersWithActingUser("suspended-tenant"));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Call_DeletedTenant_ThrowsPermissionDenied()
    {
        var fakeCache = Substitute.For<ITenantStatusCache>();
        fakeCache.GetStatusAsync("deleted-tenant").Returns("deleted");
        var client = CreateClientWithFakeTenantStatusCache(fakeCache);

        var ex = await TryAggregateAsync(client, HeadersWithActingUser("deleted-tenant"));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Call_UnknownTenant_NullStatus_ThrowsPermissionDenied()
    {
        var fakeCache = Substitute.For<ITenantStatusCache>();
        fakeCache.GetStatusAsync("unknown-tenant").Returns((string?)null);
        var client = CreateClientWithFakeTenantStatusCache(fakeCache);

        var ex = await TryAggregateAsync(client, HeadersWithActingUser("unknown-tenant"));

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Call_ActiveTenant_DoesNotThrowPermissionDenied()
    {
        var fakeCache = Substitute.For<ITenantStatusCache>();
        fakeCache.GetStatusAsync("active-tenant").Returns("active");
        var client = CreateClientWithFakeTenantStatusCache(fakeCache);

        var ex = await TryAggregateAsync(client, HeadersWithActingUser("active-tenant"));

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — expected, business logic not auth
        ex!.StatusCode.Should().NotBe(StatusCode.PermissionDenied);
        ex.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }
}
