using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

[IversonEntity]
internal sealed class MappedWriteTestEntity
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
}

public class EntityCoordinatorMappedWriteTests
{
    [Fact]
    public async Task PostMappedAsync_PassesSuppliedHeaders_ToPostAsync()
    {
        var mapping = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();
        Metadata? capturedHeaders = null;
        mapping
            .PostAsync(
                Arg.Any<MappingWriteRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<MappingResponse>(
                Task.FromResult(new MappingResponse { Success = true, Data = new Struct() }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<MappedWriteTestEntity>(mapping: mapping);
        var headers = new Metadata { { "x-trace-id", "Bearer test-token" } };

        await sut.PostMappedAsync(new MappedWriteTestEntity { Name = "x" }, headers);

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-trace-id")!.Value.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task UpdateMappedAsync_PassesSuppliedHeaders_ToUpdateAsync()
    {
        var mapping = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();
        Metadata? capturedHeaders = null;
        mapping
            .UpdateAsync(
                Arg.Any<MappingWriteRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<MappingResponse>(
                Task.FromResult(new MappingResponse { Success = true, Data = new Struct() }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<MappedWriteTestEntity>(mapping: mapping);
        var headers = new Metadata { { "x-trace-id", "Bearer test-token" } };

        await sut.UpdateMappedAsync(new MappedWriteTestEntity { Id = Guid.NewGuid(), Name = "x" }, headers);

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-trace-id")!.Value.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task GetMappedAsync_PassesSuppliedHeaders_ToGetAsync()
    {
        var mapping = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();
        Metadata? capturedHeaders = null;
        mapping
            .GetAsync(
                Arg.Any<MappingGetRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<MappingResponse>(
                Task.FromResult(new MappingResponse { Success = true, Data = new Struct() }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<MappedWriteTestEntity>(mapping: mapping);
        var headers = new Metadata { { "x-trace-id", "Bearer test-token" } };

        await sut.GetMappedAsync("some-key", headers: headers);

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-trace-id")!.Value.Should().Be("Bearer test-token");
    }
}
