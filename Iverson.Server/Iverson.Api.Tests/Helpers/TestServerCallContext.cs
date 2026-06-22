using Grpc.Core;

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

    public static TestServerCallContext Create(CancellationToken ct = default) => new(ct);

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
