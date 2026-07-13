using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Api.Grpc;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ActingUserInterceptorTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly ObjectSearchService.ObjectSearchServiceClient _client;

    public ActingUserInterceptorTests(AuthTestWebApplicationFactory factory)
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

    private async Task<RpcException?> TryAggregateAsync(Metadata headers)
    {
        try
        {
            await _client.AggregateAsync(new AggregateRequest(), headers);
            return null;
        }
        catch (RpcException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task Call_NoActingUserToken_DoesNotFailAuth()
    {
        var ex = await TryAggregateAsync(ServiceOnlyHeaders());

        ex.Should().NotBeNull(); // FailedPrecondition from RequireSchema — expected, business logic not auth
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Call_ValidActingUserToken_DoesNotFailAuth()
    {
        var headers = ServiceOnlyHeaders();
        headers.Add(ActingUserInterceptor.MetadataKey,
            $"Bearer {TestJwtFactory.CreateToken("test-actinguser-audience", "test-human-sub")}");

        var ex = await TryAggregateAsync(headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().NotBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Call_InvalidActingUserToken_ThrowsUnauthenticated()
    {
        var headers = ServiceOnlyHeaders();
        headers.Add(ActingUserInterceptor.MetadataKey,
            $"Bearer {TestJwtFactory.CreateToken("wrong-audience", "test-human-sub")}");

        var ex = await TryAggregateAsync(headers);

        ex.Should().NotBeNull();
        ex!.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}
