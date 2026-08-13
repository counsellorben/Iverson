using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

[IversonEntity]
internal sealed class IdentityResolutionTestEntity
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
}

public class EntityCoordinatorIdentityResolutionTests
{
    private const string MetadataKey = ActingUserMetadata.MetadataKey;

    private static (EntityCoordinator<IdentityResolutionTestEntity> Coordinator, Func<Metadata?> Captured)
        CreateSut(ActingUserIdentity? identity = null)
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

        var registry = new EntityRegistry([typeof(IdentityResolutionTestEntity).Assembly]);
        var retrieval = Substitute.For<ObjectRetrievalService.ObjectRetrievalServiceClient>();
        var assembler = new GraphAssembler(retrieval, registry, NullLogger<GraphAssembler>.Instance);

        var coordinator = new EntityCoordinator<IdentityResolutionTestEntity>(
            registry,
            assembler,
            mapping,
            Substitute.For<ObjectPersistenceService.ObjectPersistenceServiceClient>(),
            retrieval,
            Substitute.For<ObjectSearchService.ObjectSearchServiceClient>(),
            NullLogger<EntityCoordinator<IdentityResolutionTestEntity>>.Instance,
            identity);

        return (coordinator, () => capturedHeaders);
    }

    private static IdentityResolutionTestEntity NewEntity() => new() { Name = "x" };

    [Fact]
    public async Task BoundIdentity_WinsOverAmbientIdentity()
    {
        var ambient = new ActingUserIdentity(() => Task.FromResult("ambient-token"));
        var (sut, captured) = CreateSut(ambient);
        var bound = sut.WithActingUser(() => Task.FromResult("bound-token"));

        await bound.PostMappedAsync(NewEntity());

        captured().Should().NotBeNull();
        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer bound-token");
    }

    [Fact]
    public async Task AmbientIdentity_AppliesWhenNothingBound()
    {
        var ambient = new ActingUserIdentity(() => Task.FromResult("ambient-token"));
        var (sut, captured) = CreateSut(ambient);

        await sut.PostMappedAsync(NewEntity());

        captured().Should().NotBeNull();
        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer ambient-token");
    }

    [Fact]
    public async Task NothingConfigured_EmitsNoActingUserHeader()
    {
        var (sut, captured) = CreateSut(identity: null);

        await sut.PostMappedAsync(NewEntity());

        captured().Should().NotBeNull();
        captured()!.Get(MetadataKey).Should().BeNull();
    }

    [Fact]
    public async Task WithActingUser_DoesNotMutateReceiver()
    {
        var ambient = new ActingUserIdentity(() => Task.FromResult("ambient-token"));
        var (sut, captured) = CreateSut(ambient);

        // Binding a copy must not affect the original, unbound coordinator.
        _ = sut.WithActingUser(() => Task.FromResult("bound-token"));

        await sut.PostMappedAsync(NewEntity());

        captured().Should().NotBeNull();
        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer ambient-token");
    }

    [Fact]
    public async Task SuppliedHeaders_WithExistingActingUserEntry_PassThroughUntouched()
    {
        var ambient = new ActingUserIdentity(() => Task.FromResult("ambient-token"));
        var (sut, captured) = CreateSut(ambient);
        var headers = new Metadata { { MetadataKey, "Bearer explicit-token" } };

        await sut.PostMappedAsync(NewEntity(), headers);

        captured().Should().NotBeNull();
        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer explicit-token");
    }

    [Fact]
    public async Task SuppliedHeaders_WithUnrelatedEntries_ReceiveResolvedIdentityInAddition()
    {
        var ambient = new ActingUserIdentity(() => Task.FromResult("ambient-token"));
        var (sut, captured) = CreateSut(ambient);
        var headers = new Metadata { { "x-trace-id", "abc-123" } };

        await sut.PostMappedAsync(NewEntity(), headers);

        captured().Should().NotBeNull();
        captured()!.Get("x-trace-id")!.Value.Should().Be("abc-123");
        captured()!.Get(MetadataKey)!.Value.Should().Be("Bearer ambient-token");
    }

    [Fact]
    public async Task SuppliedHeaders_ReusedAcrossDifferentIdentities_AreNotMutated()
    {
        // Regression guard: a caller-owned Metadata bag with no acting-user entry, reused across
        // two calls on coordinators bound to two DIFFERENT identities, must not leak the first
        // call's resolved identity into the second — and must never end up carrying an
        // acting-user entry itself, since ResolveHeadersAsync must copy rather than mutate it.
        var (aliceCoordinator, aliceCaptured) = CreateSut();
        var (bobCoordinator, bobCaptured) = CreateSut();
        var alice = aliceCoordinator.WithActingUser(() => Task.FromResult("alice-token"));
        var bob = bobCoordinator.WithActingUser(() => Task.FromResult("bob-token"));

        var headers = new Metadata { { "x-trace-id", "1" } };

        await alice.PostMappedAsync(NewEntity(), headers);
        await bob.PostMappedAsync(NewEntity(), headers);

        aliceCaptured().Should().NotBeNull();
        aliceCaptured()!.Get(MetadataKey)!.Value.Should().Be("Bearer alice-token");

        bobCaptured().Should().NotBeNull();
        bobCaptured()!.Get(MetadataKey)!.Value.Should().Be("Bearer bob-token");

        headers.Get(MetadataKey).Should().BeNull();
        headers.Get("x-trace-id")!.Value.Should().Be("1");
    }
}
