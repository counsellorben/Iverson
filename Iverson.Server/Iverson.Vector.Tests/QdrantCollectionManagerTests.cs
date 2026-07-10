using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class QdrantCollectionManagerTests
{
    [Fact]
    public void QdrantCollectionManager_ImplementsIVectorSchemaManager()
    {
        typeof(QdrantCollectionManager).Should().Implement<IVectorSchemaManager>();
    }

    [Fact]
    public async Task ApplyCollectionAsync_IsCalledWithSchema()
    {
        var svc = Substitute.For<IVectorSchemaManager>();
        var schema = new CollectionSchema(
            "players",
            new List<NamedVector> { new("bio_embedding", 1536) },
            new List<PayloadIndex> { new("team", PayloadIndexKind.Keyword) });

        await svc.ApplyCollectionAsync(schema);

        await svc.Received(1).ApplyCollectionAsync(
            Arg.Is<CollectionSchema>(s => s.CollectionName == "players"));
    }
}
