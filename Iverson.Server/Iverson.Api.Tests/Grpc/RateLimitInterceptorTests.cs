using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Grpc;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class RateLimitInterceptorTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly ObjectSearchService.ObjectSearchServiceClient _client;

    public RateLimitInterceptorTests(AuthTestWebApplicationFactory factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        _client = new ObjectSearchService.ObjectSearchServiceClient(channel);
    }

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
}
