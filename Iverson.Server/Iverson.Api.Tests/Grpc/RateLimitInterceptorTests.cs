using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class RateLimitInterceptorTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;
    private readonly ObjectSearchService.ObjectSearchServiceClient _client;

    public RateLimitInterceptorTests(AuthTestWebApplicationFactory factory)
    {
        _baseFactory = factory;
        _client = new ObjectSearchService.ObjectSearchServiceClient(ChannelTo(factory));
    }

    private static GrpcChannel ChannelTo(WebApplicationFactory<Program> factory) =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });

    private static Metadata ServiceOnlyHeaders() => new()
    {
        { "authorization", $"Bearer {TestJwtFactory.CreateToken("test-service-audience", "ak-test-service")}" }
    };

    [Fact]
    public async Task A_single_call_within_the_limit_is_not_rate_limited()
    {
        RpcException? ex = null;
        try { await _client.AggregateAsync(new AggregateRequest(), ServiceOnlyHeaders()); }
        catch (RpcException e) { ex = e; }

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().NotBe(StatusCode.ResourceExhausted);
    }

    // ── CSR round 12 §2.1: the admin services' own budget ──────────────────────
    //
    // Each test gets a host of its own, so its limiters start empty, with the IdP and the tenant
    // status faked so ListUsers answers without either.

    private GrpcChannel AdminHost()
    {
        var tenantStatus = Substitute.For<ITenantStatusCache>();
        tenantStatus.GetStatusAsync(Arg.Any<string>()).Returns("active");
        var idp = Substitute.For<IIdpAdminClient>();
        idp.ListUsersByTenantAsync(Arg.Any<string>()).Returns(Task.FromResult<IEnumerable<IdpUser>>([]));
        return ChannelTo(_baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITenantStatusCache>();
            services.AddSingleton(tenantStatus);
            services.RemoveAll<IIdpAdminClient>();
            services.AddSingleton(idp);
        })));
    }

    // A tenant admin of acme who is also an operator by the admin scope, so one token may call both
    // admin services and the data plane.
    private static Metadata AdminHeaders(string subject) => new()
    {
        {
            "authorization",
            "Bearer " + TestJwtFactory.CreateToken("test-service-audience", subject, extraClaims:
            [
                new Claim("tenant_id", "acme"),
                new Claim("groups", "tenant-admins"),
                new Claim("scope", "openid admin profile"),
            ])
        }
    };

    private static async Task<StatusCode> StatusOf(Func<Task> call)
    {
        try
        {
            await call();
            return StatusCode.OK;
        }
        catch (RpcException ex)
        {
            return ex.StatusCode;
        }
    }

    [Fact]
    public async Task The_31st_TenantAdmin_call_within_a_minute_is_refused_while_other_rpcs_still_pass()
    {
        var channel = AdminHost();
        var tenantAdmin = new TenantAdminGrpcService.TenantAdminGrpcServiceClient(channel);
        var lifecycle = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
        var search = new ObjectSearchService.ObjectSearchServiceClient(channel);
        var headers = AdminHeaders("admin-rpc-limit-user");

        for (var i = 0; i < 30; i++)
            (await StatusOf(async () => await tenantAdmin.ListUsersAsync(new ListUsersRequest(), headers)))
                .Should().Be(StatusCode.OK, $"call {i + 1} of 30 is within the budget");

        (await StatusOf(async () => await tenantAdmin.ListUsersAsync(new ListUsersRequest(), headers)))
            .Should().Be(StatusCode.ResourceExhausted);

        // TenantLifecycle has its own bucket for this caller, and the data plane its own limiter.
        (await StatusOf(async () => await lifecycle.ListTenantsAsync(new ListTenantsRequest(), headers)))
            .Should().Be(StatusCode.OK);
        (await StatusOf(async () => await search.AggregateAsync(new AggregateRequest(), headers)))
            .Should().NotBe(StatusCode.ResourceExhausted);
    }

    [Fact]
    public async Task One_callers_spent_TenantAdmin_budget_leaves_another_callers_untouched()
    {
        var channel = AdminHost();
        var tenantAdmin = new TenantAdminGrpcService.TenantAdminGrpcServiceClient(channel);
        var first = AdminHeaders("admin-rpc-first-user");

        for (var i = 0; i < 31; i++)
            await StatusOf(async () => await tenantAdmin.ListUsersAsync(new ListUsersRequest(), first));

        (await StatusOf(async () => await tenantAdmin.ListUsersAsync(new ListUsersRequest(), AdminHeaders("admin-rpc-second-user"))))
            .Should().Be(StatusCode.OK);
    }

    // A call spelled in another case reaches the same handler (routing is case-insensitive), so it
    // spends the same bucket.
    [Fact]
    public async Task A_TenantAdmin_call_spelled_in_another_case_spends_the_same_budget()
    {
        var channel = AdminHost();
        var headers = AdminHeaders("admin-rpc-case-user");
        var upperCased = new Method<ListUsersRequest, ListUsersResponse>(
            MethodType.Unary, "IVERSON.TENANTADMINGRPCSERVICE", "ListUsers",
            Marshallers.Create(r => r.ToByteArray(), ListUsersRequest.Parser.ParseFrom),
            Marshallers.Create(r => r.ToByteArray(), ListUsersResponse.Parser.ParseFrom));
        var invoker = channel.CreateCallInvoker();

        for (var i = 0; i < 30; i++)
            (await StatusOf(async () => await invoker.AsyncUnaryCall(upperCased, null, new CallOptions(headers), new ListUsersRequest())))
                .Should().Be(StatusCode.OK, $"call {i + 1} of 30 routes to ListUsers");

        (await StatusOf(async () => await new TenantAdminGrpcService.TenantAdminGrpcServiceClient(channel)
                .ListUsersAsync(new ListUsersRequest(), headers)))
            .Should().Be(StatusCode.ResourceExhausted);
    }
}
