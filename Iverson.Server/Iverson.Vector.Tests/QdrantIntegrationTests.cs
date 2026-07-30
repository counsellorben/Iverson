using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Grpc.Core;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Xunit;
using Range = Qdrant.Client.Grpc.Range;

namespace Iverson.Vector.Tests;

public sealed class QdrantContainerFixture : IAsyncLifetime
{
    private const int GrpcPort = 6334;

    private readonly DotNet.Testcontainers.Containers.IContainer _container =
        new ContainerBuilder()
            .WithImage("qdrant/qdrant:v1.18.2")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .WithPortBinding(6333,     assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(GrpcPort))
            .Build();

    public IntelligenceVectorService Service { get; private set; } = null!;
    public IntelligenceCollectionManager CollectionManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var host       = _container.Hostname;
        var mappedPort = _container.GetMappedPublicPort(GrpcPort);

        var qdrantClient  = new QdrantClient(host, mappedPort, https: false);
        Service           = new IntelligenceVectorService(qdrantClient);
        CollectionManager = new IntelligenceCollectionManager(qdrantClient, "test-api-key", NullLogger<IntelligenceCollectionManager>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class QdrantIntegrationTests(QdrantContainerFixture fixture)
    : IClassFixture<QdrantContainerFixture>
{
    private readonly IntelligenceVectorService _svc = fixture.Service;
    private readonly IntelligenceCollectionManager _mgr = fixture.CollectionManager;

    // Each test gets its own collection name to avoid state leakage
    private static string UniqueName() =>
        "col_" + Guid.NewGuid().ToString("N")[..8];

    // ── EnsureCollectionAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsureCollectionAsync_CreatesCollection_WhenNotExists()
    {
        var name = UniqueName();

        await _mgr.EnsureCollectionAsync(name, vectorSize: 4);

        // Confirm: upsert a point without error
        var act = async () => await _svc.UpsertAsync(name, 1UL, [0.1f, 0.2f, 0.3f, 0.4f]);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureCollectionAsync_IsIdempotent_WhenCalledTwice()
    {
        var name = UniqueName();

        await _mgr.EnsureCollectionAsync(name, vectorSize: 4);

        var act = async () => await _mgr.EnsureCollectionAsync(name, vectorSize: 4);
        await act.Should().NotThrowAsync();
    }

    // ── UpsertAsync / SearchAsync (unnamed vector) ────────────────────────────

    [Fact]
    public async Task UpsertAsync_ThenSearch_ReturnsInsertedPoint()
    {
        var name   = UniqueName();
        var vector = new float[] { 1f, 0f, 0f, 0f };

        await _mgr.EnsureCollectionAsync(name, vectorSize: 4);
        await _svc.UpsertAsync(name, 42UL, vector, new Dictionary<string, object> { ["label"] = "iverson" });

        var results = await _svc.SearchAsync(name, vector, limit: 5);

        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == 42UL);
        results.First(r => r.Id == 42UL).Payload["label"].Should().Be("iverson");
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenCollectionIsEmpty()
    {
        var name = UniqueName();
        await _mgr.EnsureCollectionAsync(name, vectorSize: 4);

        var results = await _svc.SearchAsync(name, [0.1f, 0.2f, 0.3f, 0.4f], limit: 5);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ReturnsClosestVector_ByCosineSimilarity()
    {
        var name = UniqueName();
        await _mgr.EnsureCollectionAsync(name, vectorSize: 3);

        await _svc.UpsertAsync(name, 1UL, [1f, 0f, 0f]);
        await _svc.UpsertAsync(name, 2UL, [0f, 1f, 0f]);
        await _svc.UpsertAsync(name, 3UL, [0f, 0f, 1f]);

        // Query most similar to point 1
        var results = await _svc.SearchAsync(name, [1f, 0f, 0f], limit: 1);

        results.Should().ContainSingle().Which.Id.Should().Be(1UL);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesPoint()
    {
        var name   = UniqueName();
        var vector = new float[] { 1f, 0f, 0f, 0f };

        await _mgr.EnsureCollectionAsync(name, vectorSize: 4);
        await _svc.UpsertAsync(name, 99UL, vector);

        await _svc.DeleteAsync(name, 99UL);

        var results = await _svc.SearchAsync(name, vector, limit: 10);
        results.Should().NotContain(r => r.Id == 99UL);
    }

    // ── ApplyCollectionAsync (named vectors) ──────────────────────────────────

    [Fact]
    public async Task ApplyCollectionAsync_CreatesNamedVectorCollection()
    {
        var schema = new CollectionSchema(
            UniqueName(),
            [new NamedVector("title_vector", 4)],
            [new PayloadIndex("type", PayloadIndexKind.Keyword)]);

        await _mgr.ApplyCollectionAsync(schema);

        // Confirm: upsert a named vector without error
        var act = async () => await _svc.UpsertNamedAsync(
            schema.CollectionName, 1UL,
            new Dictionary<string, float[]> { ["title_vector"] = [0.1f, 0.2f, 0.3f, 0.4f] });
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyCollectionAsync_IsIdempotent_WhenCalledTwice()
    {
        var schema = new CollectionSchema(
            UniqueName(),
            [new NamedVector("bio_vector", 4)],
            []);

        await _mgr.ApplyCollectionAsync(schema);

        var act = async () => await _mgr.ApplyCollectionAsync(schema);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyCollectionAsync_MigratesCollection_WhenNewVectorAdded()
    {
        var name = UniqueName();

        var v1 = new CollectionSchema(name, [new NamedVector("title_vector", 4)], []);
        await _mgr.ApplyCollectionAsync(v1);

        // Seed a point
        await _svc.UpsertNamedAsync(name, 1UL,
            new Dictionary<string, float[]> { ["title_vector"] = [0.1f, 0.2f, 0.3f, 0.4f] });

        // Add a second named vector → triggers migration
        var v2 = new CollectionSchema(name,
            [new NamedVector("title_vector", 4), new NamedVector("body_vector", 4)], []);

        var act = async () => await _mgr.ApplyCollectionAsync(v2);
        await act.Should().NotThrowAsync();
    }

    // ── UpsertNamedAsync / SearchNamedAsync ───────────────────────────────────

    [Fact]
    public async Task UpsertNamedAsync_ThenSearchNamed_ReturnsInsertedPoint()
    {
        var name   = UniqueName();
        var schema = new CollectionSchema(name, [new NamedVector("title_vector", 4)], []);
        await _mgr.ApplyCollectionAsync(schema);

        await _svc.UpsertNamedAsync(name, 7UL,
            new Dictionary<string, float[]> { ["title_vector"] = [1f, 0f, 0f, 0f] },
            new Dictionary<string, object>  { ["title"]        = "The Answer" });

        var results = await _svc.SearchNamedAsync(name, "title_vector", [1f, 0f, 0f, 0f], limit: 5);

        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.Id == 7UL);
        results.First(r => r.Id == 7UL).Payload["title"].Should().Be("The Answer");
    }

    [Fact]
    public async Task SearchNamedAsync_ReturnsBestMatch_ByCosineSimilarity()
    {
        var name   = UniqueName();
        var schema = new CollectionSchema(name, [new NamedVector("embed", 3)], []);
        await _mgr.ApplyCollectionAsync(schema);

        await _svc.UpsertNamedAsync(name, 1UL, new Dictionary<string, float[]> { ["embed"] = [1f, 0f, 0f] });
        await _svc.UpsertNamedAsync(name, 2UL, new Dictionary<string, float[]> { ["embed"] = [0f, 1f, 0f] });
        await _svc.UpsertNamedAsync(name, 3UL, new Dictionary<string, float[]> { ["embed"] = [0f, 0f, 1f] });

        var results = await _svc.SearchNamedAsync(name, "embed", [1f, 0f, 0f], limit: 1);

        results.Should().ContainSingle().Which.Id.Should().Be(1UL);
    }

    [Fact]
    public async Task UpsertNamedAsync_WithMultipleVectors_AllSearchable()
    {
        var name   = UniqueName();
        var schema = new CollectionSchema(name,
            [new NamedVector("title_vector", 4), new NamedVector("body_vector", 4)], []);
        await _mgr.ApplyCollectionAsync(schema);

        await _svc.UpsertNamedAsync(name, 10UL, new Dictionary<string, float[]>
        {
            ["title_vector"] = [1f, 0f, 0f, 0f],
            ["body_vector"]  = [0f, 1f, 0f, 0f],
        });

        var titleResults = await _svc.SearchNamedAsync(name, "title_vector", [1f, 0f, 0f, 0f], limit: 1);
        var bodyResults  = await _svc.SearchNamedAsync(name, "body_vector",  [0f, 1f, 0f, 0f], limit: 1);

        titleResults.Should().ContainSingle().Which.Id.Should().Be(10UL);
        bodyResults.Should().ContainSingle().Which.Id.Should().Be(10UL);
    }

    [Fact]
    public async Task UpsertAsync_TypedPayload_RoundTripsThroughRealQdrant()
    {
        var collection = UniqueName();
        await _mgr.EnsureCollectionAsync(collection, 4);

        await _svc.UpsertAsync(collection, 1, [0.1f, 0.2f, 0.3f, 0.4f], new Dictionary<string, object>
        {
            ["wordCount"] = 500L,
            ["rating"]    = 4.5,
            ["featured"]  = true
        });

        var results = await _svc.SearchAsync(collection, [0.1f, 0.2f, 0.3f, 0.4f], limit: 1);

        results.Should().ContainSingle();
        // Read-side projection (VectorSearchResult.Payload) stays string-typed by design —
        // this only asserts the upsert didn't throw and a point round-tripped.
        results[0].Payload.Should().ContainKey("wordCount");
    }

    [Fact]
    public async Task SearchNamedAsync_WithFilter_ReturnsOnlyMatchingPoints()
    {
        var collection = UniqueName();
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            collection,
            [new NamedVector("title_vector", 4)],
            []));

        await _svc.UpsertNamedAsync(collection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = [0.1f, 0.2f, 0.3f, 0.4f] },
            new Dictionary<string, object> { ["wordCount"] = 100L });
        await _svc.UpsertNamedAsync(collection, 2,
            new Dictionary<string, float[]> { ["title_vector"] = [0.1f, 0.2f, 0.3f, 0.4f] },
            new Dictionary<string, object> { ["wordCount"] = 900L });

        var filter = new Filter();
        filter.Must.Add(Conditions.Range("wordCount", new Range { Gt = 500 }));

        var results = await _svc.SearchNamedAsync(collection, "title_vector", [0.1f, 0.2f, 0.3f, 0.4f], 10, filter);

        results.Should().ContainSingle();
        results[0].Id.Should().Be(2);
    }

    // ── UpdateNamedVectorsAsync (partial named-vector update) ─────────────────
    //
    // These tests verify, against a real Qdrant, the two facts the IntelligenceStoreConsumer's
    // update-then-fallback design depends on but that were previously only assumed in mocked
    // tests: (1) update_vectors against a point that was never written surfaces as gRPC NotFound,
    // and (2) a partial named-vector update leaves untouched named vectors on the same point
    // unchanged (i.e. it truly is a partial update, not a full-vector replace).

    [Fact]
    public async Task UpdateNamedVectorsAsync_PointNeverWritten_ThrowsNotFound()
    {
        var name = UniqueName();
        var schema = new CollectionSchema(name, [new NamedVector("title_centroid", 4)], []);
        await _mgr.ApplyCollectionAsync(schema);

        var act = async () => await _svc.UpdateNamedVectorsAsync(
            name, 123UL,
            new Dictionary<string, float[]> { ["title_centroid"] = [0.1f, 0.2f, 0.3f, 0.4f] });

        var thrown = await act.Should().ThrowAsync<RpcException>();
        thrown.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateNamedVectorsAsync_UpdatesOnlyTargetedVector_LeavesOtherVectorUnchanged()
    {
        var name = UniqueName();
        var schema = new CollectionSchema(
            name,
            [new NamedVector("title_vector", 4), new NamedVector("title_centroid", 4)],
            []);
        await _mgr.ApplyCollectionAsync(schema);

        var originalTitleVector = new float[] { 1f, 0f, 0f, 0f };
        await _svc.UpsertNamedAsync(name, 5UL, new Dictionary<string, float[]>
        {
            ["title_vector"]   = originalTitleVector,
            ["title_centroid"] = [0f, 1f, 0f, 0f],
        });

        var updatedCentroid = new float[] { 0f, 0f, 1f, 0f };
        await _svc.UpdateNamedVectorsAsync(name, 5UL,
            new Dictionary<string, float[]> { ["title_centroid"] = updatedCentroid });

        // The updated vector reflects the new value.
        var centroidResults = await _svc.SearchNamedAsync(name, "title_centroid", updatedCentroid, limit: 1);
        centroidResults.Should().ContainSingle().Which.Id.Should().Be(5UL);

        // The untouched named vector on the same point survived unchanged.
        var titleResults = await _svc.SearchNamedAsync(name, "title_vector", originalTitleVector, limit: 1);
        titleResults.Should().ContainSingle().Which.Id.Should().Be(5UL);
    }

    // ── RetrieveNamedVectorAsync ────────────────────────────────────────────

    [Fact]
    public async Task RetrieveNamedVectorAsync_ReturnsOnlyRequestedVector_ForMatchingId()
    {
        var name   = UniqueName();
        var schema = new CollectionSchema(
            name,
            [new NamedVector("title_vector", 4), new NamedVector("body_vector", 4)],
            []);
        await _mgr.ApplyCollectionAsync(schema);

        var titleVector = new float[] { 1f, 0f, 0f, 0f };
        var bodyVector  = new float[] { 0f, 1f, 0f, 0f };
        await _svc.UpsertNamedAsync(name, 1UL, new Dictionary<string, float[]>
        {
            ["title_vector"] = titleVector,
            ["body_vector"]  = bodyVector,
        });

        var result = await _svc.RetrieveNamedVectorAsync(name, [1UL], "title_vector");

        result.Should().ContainKey(1UL).WhoseValue.Should().BeEquivalentTo(titleVector);
    }

    [Fact]
    public async Task RetrieveNamedVectorAsync_OmitsIds_WithNoSuchPoint()
    {
        var name   = UniqueName();
        var schema = new CollectionSchema(name, [new NamedVector("title_vector", 4)], []);
        await _mgr.ApplyCollectionAsync(schema);

        await _svc.UpsertNamedAsync(name, 1UL,
            new Dictionary<string, float[]> { ["title_vector"] = [1f, 0f, 0f, 0f] });

        var result = await _svc.RetrieveNamedVectorAsync(name, [1UL, 999UL], "title_vector");

        result.Should().ContainKey(1UL);
        result.Should().NotContainKey(999UL);
    }

    [Fact]
    public async Task RetrieveNamedVectorAsync_MergesAcrossBatches_WhenMoreThan512Ids()
    {
        var name   = UniqueName();
        var schema = new CollectionSchema(name, [new NamedVector("title_vector", 4)], []);
        await _mgr.ApplyCollectionAsync(schema);

        // Collection uses Cosine distance, which normalizes stored vectors — vary
        // direction (not magnitude) per id so identity is verifiable after storage.
        const int count = 600;
        for (ulong i = 1; i <= count; i++)
        {
            var vector = i % 2 == 0 ? new float[] { 1f, 0f, 0f, 0f } : new float[] { 0f, 1f, 0f, 0f };
            await _svc.UpsertNamedAsync(name, i,
                new Dictionary<string, float[]> { ["title_vector"] = vector });
        }

        var ids = Enumerable.Range(1, count).Select(i => (ulong)i).ToList();
        var result = await _svc.RetrieveNamedVectorAsync(name, ids, "title_vector");

        result.Should().HaveCount(count);
        result[1UL].Should().BeEquivalentTo(new float[] { 0f, 1f, 0f, 0f });
        result[600UL].Should().BeEquivalentTo(new float[] { 1f, 0f, 0f, 0f });
    }
}
