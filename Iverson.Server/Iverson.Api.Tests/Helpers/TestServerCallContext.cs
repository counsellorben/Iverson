using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Iverson.Api.Tests.Helpers;

public class TestServerCallContext : ServerCallContext
{
    private readonly CancellationToken _ct;
    private readonly Metadata _requestHeaders;

    public TestServerCallContext(CancellationToken ct = default, Metadata? requestHeaders = null)
    {
        _ct = ct;
        _requestHeaders = requestHeaders ?? new Metadata();
    }

    /// <summary>
    /// gRPC's <c>ServerCallContext.GetHttpContext()</c> extension only works when hosted by
    /// ASP.NET Core (a real <c>HttpContextServerCallContext</c>) — or, as a documented fallback,
    /// when the plain <c>ServerCallContext.UserState</c> dictionary has a "__HttpContext" entry.
    /// Passing <paramref name="user"/> here populates that fallback so unit tests can exercise
    /// service methods (e.g. RegisterSchema's admin-operation audit log) that read the caller's
    /// identity via <c>context.GetHttpContext().User</c>.
    /// </summary>
    public static TestServerCallContext Create(CancellationToken ct = default, ClaimsPrincipal? user = null)
    {
        var context = new TestServerCallContext(ct);
        if (user is not null)
            context.UserState["__HttpContext"] = new DefaultHttpContext { User = user };
        return context;
    }

    protected override string MethodCore => "Test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "localhost";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => _ct;
    protected override Metadata ResponseTrailersCore => new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new("", new Dictionary<string, List<AuthProperty>>());
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
