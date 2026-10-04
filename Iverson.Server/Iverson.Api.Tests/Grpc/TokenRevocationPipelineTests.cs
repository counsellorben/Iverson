using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Grpc;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// CSR round-10 #11: both JwtBearer schemes refuse a token whose sub was revoked at or after the
// token's iat. Boots the real Program.cs pipeline (same base fixture and probe RPC as
// ActingUserInterceptorSuspensionTests) with the revocation repository substituted, so the real
// OnTokenValidated handler and the real TokenRevocationCache both run.
public class TokenRevocationPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string RevokedSub = "revoked-user-uid";
    private static readonly DateTimeOffset RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

    private readonly AuthTestWebApplicationFactory _baseFactory;

    public TokenRevocationPipelineTests(AuthTestWebApplicationFactory factory) =>
        _baseFactory = factory;

    private ObjectSearchService.ObjectSearchServiceClient CreateClient()
    {
        var revocations = Substitute.For<ITokenRevocationRepository>();
        revocations.ListAsync().Returns([(RevokedSub, RevokedAt)]);
        var tenantStatusCache = Substitute.For<ITenantStatusCache>();
        tenantStatusCache.GetStatusAsync("active-tenant").Returns("active");

        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITokenRevocationRepository>();
                services.AddSingleton(revocations);
                services.RemoveAll<ITenantStatusCache>();
                services.AddSingleton(tenantStatusCache);
            }));
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        return new ObjectSearchService.ObjectSearchServiceClient(channel);
    }

    // The existing pipeline-test tokens carry no iat; these carry one from before the revocation,
    // the shape every token a user held when they were removed or demoted has.
    private static Claim IssuedBeforeRevocation() =>
        new("iat", RevokedAt.AddMinutes(-5).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64);

    private static string ServiceToken(string subject) =>
        TestJwtFactory.CreateToken("test-service-audience", subject, extraClaims: [IssuedBeforeRevocation()]);

    // A login after the revocation, e.g. by a re-activated user.
    private static Claim IssuedAfterRevocation() =>
        new("iat", RevokedAt.AddSeconds(30).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64);

    private static string ServiceTokenIssuedAfterRevocation(string subject) =>
        TestJwtFactory.CreateToken("test-service-audience", subject, extraClaims: [IssuedAfterRevocation()]);

    private static string ActingUserToken(string subject) =>
        TestJwtFactory.CreateToken(
            "test-actinguser-audience",
            subject,
            extraClaims: [IssuedBeforeRevocation(), new Claim("tenant_id", "active-tenant")]);

    private static string ActingUserTokenIssuedAfterRevocation(string subject) =>
        TestJwtFactory.CreateToken(
            "test-actinguser-audience",
            subject,
            extraClaims: [IssuedAfterRevocation(), new Claim("tenant_id", "active-tenant")]);

    private static async Task<RpcException?> TryAggregateAsync(
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
    public async Task PrimaryScheme_RevokedSub_ThrowsUnauthenticated()
    {
        var headers = new Metadata { { "authorization", $"Bearer {ServiceToken(RevokedSub)}" } };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task PrimaryScheme_SubNotRevoked_IsAuthenticated()
    {
        var headers = new Metadata { { "authorization", $"Bearer {ServiceToken("still-active-uid")}" } };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — business logic, not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    // A login after the revocation (a re-activated user) is accepted: the handler passes the
    // token's iat through rather than refusing the sub outright.
    [Fact]
    public async Task PrimaryScheme_RevokedSub_TokenIssuedAfterRevocation_IsAuthenticated()
    {
        var headers = new Metadata { { "authorization", $"Bearer {ServiceTokenIssuedAfterRevocation(RevokedSub)}" } };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — business logic, not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task ActingUser_RevokedSub_ThrowsUnauthenticated()
    {
        var headers = new Metadata
        {
            { "authorization", $"Bearer {ServiceToken("ak-test-service")}" },
            { ActingUserInterceptor.MetadataKey, $"Bearer {ActingUserToken(RevokedSub)}" }
        };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
        ex.Status.Detail.Should().Be("Acting-user token is invalid.");
    }

    // The ActingUser-scheme counterpart of PrimaryScheme_RevokedSub_TokenIssuedAfterRevocation_IsAuthenticated.
    [Fact]
    public async Task ActingUser_RevokedSub_TokenIssuedAfterRevocation_IsAuthenticated()
    {
        var headers = new Metadata
        {
            { "authorization", $"Bearer {ServiceToken("ak-test-service")}" },
            { ActingUserInterceptor.MetadataKey, $"Bearer {ActingUserTokenIssuedAfterRevocation(RevokedSub)}" }
        };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — business logic, not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
        ex.StatusCode.Should().NotBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task ActingUser_SubNotRevoked_IsAuthenticated()
    {
        var headers = new Metadata
        {
            { "authorization", $"Bearer {ServiceToken("ak-test-service")}" },
            { ActingUserInterceptor.MetadataKey, $"Bearer {ActingUserToken("still-active-uid")}" }
        };

        var ex = await TryAggregateAsync(CreateClient(), headers);

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — business logic, not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
        ex.StatusCode.Should().NotBe(StatusCode.PermissionDenied);
    }
}
