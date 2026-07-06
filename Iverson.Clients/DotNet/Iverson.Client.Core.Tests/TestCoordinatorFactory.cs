using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Iverson.Client.Core.Tests;

/// <summary>
/// Constructs an <see cref="EntityCoordinator{T}"/> for tests that only exercise one of its
/// gRPC-backed operations (e.g. the ObjectSearch streaming wrappers). <c>EntityCoordinator&lt;T&gt;</c>'s
/// constructor requires all four service clients plus an <see cref="EntityRegistry"/> and a
/// <see cref="GraphAssembler"/>, so callers pass the client(s) they actually need to stub and get
/// substitutes wired in for the rest — mirroring how <c>SchemaRegistrarTests</c>/<c>GraphAssemblerTests</c>
/// build an <see cref="EntityRegistry"/> by scanning <typeparamref name="T"/>'s assembly for
/// <c>[IversonEntity]</c>-annotated types.
/// </summary>
internal static class TestCoordinatorFactory
{
    public static EntityCoordinator<T> Create<T>(
        ObjectSearchService.ObjectSearchServiceClient? search = null,
        ObjectMappingService.ObjectMappingServiceClient? mapping = null,
        ObjectPersistenceService.ObjectPersistenceServiceClient? persistence = null,
        ObjectRetrievalService.ObjectRetrievalServiceClient? retrieval = null)
        where T : class
    {
        var registry = new EntityRegistry([typeof(T).Assembly]);
        retrieval ??= Substitute.For<ObjectRetrievalService.ObjectRetrievalServiceClient>();
        var assembler = new GraphAssembler(retrieval, registry, NullLogger<GraphAssembler>.Instance);

        return new EntityCoordinator<T>(
            registry,
            assembler,
            mapping ?? Substitute.For<ObjectMappingService.ObjectMappingServiceClient>(),
            persistence ?? Substitute.For<ObjectPersistenceService.ObjectPersistenceServiceClient>(),
            retrieval,
            search ?? Substitute.For<ObjectSearchService.ObjectSearchServiceClient>(),
            NullLogger<EntityCoordinator<T>>.Instance);
    }
}
