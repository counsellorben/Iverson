using System.Security.Claims;
using FluentAssertions;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR round 12 §2.3: before every message of a server-streaming RPC, the principals' revocation and the
/// acting user's tenant are checked again, through both <c>WriteAsync</c> forms. Drives the interceptor
/// directly over the real caches, and changes what their repositories return in the middle of a stream.
/// </summary>
public class StreamRevalidationInterceptorTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

    private readonly AuthTestWebApplicationFactory _factory;
    private readonly ITokenRevocationRepository _revocationRows = Substitute.For<ITokenRevocationRepository>();
    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly TokenRevocationCache _revocations;
    private readonly TenantStatusCache _tenantStatuses;

    public StreamRevalidationInterceptorTests(AuthTestWebApplicationFactory factory)
    {
        _factory = factory;
        _revocationRows.ListAsync().Returns(Enumerable.Empty<(string Sub, DateTimeOffset RevokedAt)>());
        _tenants.GetAsync(TenantId).Returns(Tenant("active"));
        _revocations = new TokenRevocationCache(_revocationRows, TimeProvider.System);
        _tenantStatuses = new TenantStatusCache(_tenants, new MemoryCache(new MemoryCacheOptions()));
    }

    private static TenantRow Tenant(string status) => new(TenantId, "Tenant A", status, DateTime.UtcNow);

    private static ClaimsPrincipal Principal(string sub, string? tenantId = null)
    {
        var claims = new List<Claim>
        {
            new("sub", sub),
            new("iat", IssuedAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };
        if (tenantId is not null)
            claims.Add(new Claim("tenant_id", tenantId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static readonly ClaimsPrincipal Service = Principal("ak-test-service");
    private static readonly ClaimsPrincipal ActingUser = Principal("acting-user-sub", TenantId);

    // The call as ActingUserInterceptor leaves it: the service token on HttpContext.User, the acting user
    // on the request's IActingUserAccessor.
    private static ServerCallContext CallContext(CancellationToken callCancellation)
    {
        var services = new ServiceCollection()
            .AddSingleton<IActingUserAccessor>(new ActingUserAccessor { ActingUser = ActingUser })
            .BuildServiceProvider();
        var context = new TestServerCallContext(callCancellation);
        context.UserState["__HttpContext"] = new DefaultHttpContext { User = Service, RequestServices = services };
        return context;
    }

    private void Revoke(string sub)
    {
        _revocationRows.ListAsync().Returns([(sub, DateTimeOffset.UtcNow)]);
        _revocations.Invalidate();
    }

    private void SetTenantStatus(string status)
    {
        _tenants.GetAsync(TenantId).Returns(Tenant(status));
        _tenantStatuses.Invalidate(TenantId);
    }

    private sealed class RecordingWriter : IServerStreamWriter<string>
    {
        public List<string> Written { get; } = [];
        public List<CancellationToken> TokensSeen { get; } = [];
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(string message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(string message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            TokensSeen.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }

    private Task StreamAsync(ServerCallContext context, RecordingWriter writer, Func<IServerStreamWriter<string>, Task> rpc) =>
        new StreamRevalidationInterceptor(_revocations, _tenantStatuses)
            .ServerStreamingServerHandler<string, string>("request", writer, context, (_, stream, _) => rpc(stream));

    private static Task WriteAsync(IServerStreamWriter<string> stream, string message, bool twoArgumentForm, CancellationToken token) =>
        twoArgumentForm ? stream.WriteAsync(message, token) : stream.WriteAsync(message);

    [Fact]
    public async Task NothingChanged_BothWriteFormsWriteThrough_PassingTheTokenAndTheWriteOptions()
    {
        using var call = new CancellationTokenSource();
        var writer = new RecordingWriter();
        var options = new WriteOptions(WriteFlags.NoCompress);

        await StreamAsync(CallContext(call.Token), writer, async stream =>
        {
            stream.WriteOptions = options;
            await stream.WriteAsync("one");
            await stream.WriteAsync("two", call.Token);
            stream.WriteOptions.Should().BeSameAs(options);
        });

        writer.Written.Should().Equal("one", "two");
        writer.TokensSeen.Should().Equal(call.Token);
        writer.WriteOptions.Should().BeSameAs(options);
    }

    [Theory]
    [InlineData("acting-user-sub", false)]
    [InlineData("acting-user-sub", true)]
    [InlineData("ak-test-service", false)]
    [InlineData("ak-test-service", true)]
    public async Task ARevokedPrincipal_EndsTheStreamUnauthenticated_AtItsNextMessage(string revokedSub, bool twoArgumentForm)
    {
        using var call = new CancellationTokenSource();
        var writer = new RecordingWriter();

        var act = () => StreamAsync(CallContext(call.Token), writer, async stream =>
        {
            await WriteAsync(stream, "one", twoArgumentForm, call.Token);
            Revoke(revokedSub);
            await WriteAsync(stream, "two", twoArgumentForm, call.Token);
        });

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        writer.Written.Should().Equal("one");
    }

    [Theory]
    [InlineData("suspended", false)]
    [InlineData("suspended", true)]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    public async Task ATenantNoLongerActive_EndsTheStreamPermissionDenied_AtItsNextMessage(string status, bool twoArgumentForm)
    {
        using var call = new CancellationTokenSource();
        var writer = new RecordingWriter();

        var act = () => StreamAsync(CallContext(call.Token), writer, async stream =>
        {
            await WriteAsync(stream, "one", twoArgumentForm, call.Token);
            SetTenantStatus(status);
            await WriteAsync(stream, "two", twoArgumentForm, call.Token);
        });

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Status.Detail.Should().Be("Tenant 'tenant-a' is not active.");
        writer.Written.Should().Equal("one");
    }

    [Fact]
    public void TheInterceptor_IsRegisteredAfterActingUserInterceptor()
    {
        var interceptors = _factory.Services.GetRequiredService<IOptions<GrpcServiceOptions>>().Value.Interceptors
            .Select(i => i.Type)
            .ToList();

        interceptors.Should().ContainInOrder(typeof(ActingUserInterceptor), typeof(StreamRevalidationInterceptor));
    }
}
