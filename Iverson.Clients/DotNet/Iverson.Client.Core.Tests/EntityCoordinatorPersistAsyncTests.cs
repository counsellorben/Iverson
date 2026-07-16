using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

[IversonEntity]
internal sealed class PersistAsyncTestEntity
{
    [IversonKey] public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class EntityCoordinatorPersistAsyncTests
{
    [Fact]
    public async Task PersistAsync_PassesSuppliedHeaders_ToPostAsync()
    {
        var persistence = Substitute.For<ObjectPersistenceService.ObjectPersistenceServiceClient>();
        Metadata? capturedHeaders = null;
        persistence
            .PostAsync(
                Arg.Any<PersistRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<PersistResponse>(
                Task.FromResult(new PersistResponse { Success = true, Key = "k" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<PersistAsyncTestEntity>(persistence: persistence);
        var headers = new Metadata { { "x-acting-user-authorization", "Bearer test-token" } };

        await sut.PersistAsync(new PersistAsyncTestEntity { Id = Guid.NewGuid(), Name = "x" }, headers);

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-acting-user-authorization")!.Value.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task PersistAsync_WithNoHeaders_PassesNull()
    {
        var persistence = Substitute.For<ObjectPersistenceService.ObjectPersistenceServiceClient>();
        Metadata? capturedHeaders = null;
        persistence
            .PostAsync(
                Arg.Any<PersistRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<PersistResponse>(
                Task.FromResult(new PersistResponse { Success = true, Key = "k" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<PersistAsyncTestEntity>(persistence: persistence);

        await sut.PersistAsync(new PersistAsyncTestEntity { Id = Guid.NewGuid(), Name = "x" });

        capturedHeaders.Should().BeNull();
    }
}
