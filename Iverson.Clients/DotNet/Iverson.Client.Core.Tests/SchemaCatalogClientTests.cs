using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Contracts;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

public class SchemaCatalogClientTests
{
    private static AsyncUnaryCall<GetSchemaResponse> MakeUnaryCall(GetSchemaResponse response) =>
        new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    [Fact]
    public async Task GetSchemaAsync_ReturnsTypes_FromStub()
    {
        var mapping = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();
        var response = new GetSchemaResponse();
        response.Types_.Add(new SchemaType { Name = "Article" });
        mapping.GetSchemaAsync(
                Arg.Any<GetSchemaRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeUnaryCall(response));

        var client = new SchemaCatalogClient(mapping);

        var result = await client.GetSchemaAsync();

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Article");
    }

    [Fact]
    public async Task GetSchemaAsync_WithActingUserProvider_SendsActingUserHeader()
    {
        var mapping = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();
        Metadata? capturedHeaders = null;
        mapping.GetSchemaAsync(
                Arg.Any<GetSchemaRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(MakeUnaryCall(new GetSchemaResponse()));

        var client = new SchemaCatalogClient(mapping, () => Task.FromResult("test-token"));

        await client.GetSchemaAsync();

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-acting-user-authorization")!.Value.Should().Be("Bearer test-token");
    }
}
