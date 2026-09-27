using FluentAssertions;
using Iverson.Patterns;
using NSubstitute;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Vector.Tests;

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ChunkRowSourceIntegrationTests(QdrantContainerFixture fixture) : IClassFixture<QdrantContainerFixture>
{
    private readonly IntelligenceVectorService _svc = fixture.Service;
    private readonly IntelligenceCollectionManager _mgr = fixture.CollectionManager;
    private readonly IntelligenceTenantScope _scope = new("test-signing-key-0123456789abcdef");
    private ulong _nextId = 1;

    private async Task<string> CollectionAsync(string vectorName = "body_vector")
    {
        var name = "ck_" + Guid.NewGuid().ToString("N")[..8];
        await _mgr.ApplyCollectionAsync(new CollectionSchema(name, [new NamedVector(vectorName, 4)], []));
        return name;
    }

    private Task AddChunkAsync(string collection, string parent, int index, string field = "Body",
        string owner = "u1", string vectorName = "body_vector", float first = 1f) =>
        _svc.UpsertNamedAsync(collection, _nextId++,
            new Dictionary<string, float[]> { [vectorName] = [first, 0f, 0f, index] },
            new Dictionary<string, object>
            {
                ["text"] = $"{parent}#{index}", ["parent_id"] = parent, ["field"] = field,
                ["chunk_index"] = index.ToString(), ["ownerId"] = owner,
            });

    private static async Task<List<ChunkRow>> ReadAllAsync(IChunkRowSource source, ChunkRowQuery query)
    {
        var rows = new List<ChunkRow>();
        await foreach (var row in source.ReadAsync(query)) rows.Add(row);
        return rows;
    }

    private QdrantChunkRowSource Source(IVectorQueryService? vector = null) => new(vector ?? _svc, _scope);

    private static ChunkRowQuery Query(string collection, string? vectorName = null, Filter? filter = null,
        int batchRows = 2_000, int maxRowsScanned = 100_000) =>
        new(collection, filter, "Body", vectorName, batchRows, maxRowsScanned);

    [Fact]
    public async Task Chunks_arrive_grouped_by_parent_in_ordinal_order_and_by_numeric_chunk_index()
    {
        var col = await CollectionAsync();
        foreach (var (parent, index) in new[] { ("b", 0), ("a", 10), ("B", 0), ("a", 2), ("a", 0), ("a", 1) })
            await AddChunkAsync(col, parent, index);

        var rows = await ReadAllAsync(Source(), Query(col));

        rows.Select(r => (r.ParentKey, r.ChunkIndex)).Should().Equal(
            ("B", 0), ("a", 0), ("a", 1), ("a", 2), ("a", 10), ("b", 0));
        rows[1].Text.Should().Be("a#0");
    }

    [Fact]
    public async Task Only_the_requested_field_and_the_callers_filter_are_read()
    {
        var col = await CollectionAsync();
        await AddChunkAsync(col, "a", 0);
        await AddChunkAsync(col, "a", 1, field: "Title");
        await AddChunkAsync(col, "b", 0, owner: "u2");

        var ownership = IntelligenceFilterBuilder.ApplyOwnership(null, ownershipRequired: true, "ownerId", "u1");
        var rows = await ReadAllAsync(Source(), Query(col, filter: ownership));

        rows.Select(r => (r.ParentKey, r.ChunkIndex)).Should().Equal(("a", 0));
    }

    [Fact]
    public async Task Vectors_come_back_only_when_requested()
    {
        var col = await CollectionAsync();
        await AddChunkAsync(col, "a", 3, first: 0.5f);

        (await ReadAllAsync(Source(), Query(col))).Single().Vector.Should().BeNull();
        // Qdrant's Cosine collections store the vector normalised to unit length.
        (await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"))).Single().Vector
            .Should().Equal(new[] { 0.5f, 0f, 0f, 3f }.Select(x => x / MathF.Sqrt(9.25f)), (a, e) => Math.Abs(a - e) < 1e-6f);
    }

    [Fact]
    public async Task A_missing_collection_yields_no_rows()
    {
        var rows = await ReadAllAsync(Source(), Query("ck_missing_" + Guid.NewGuid().ToString("N")[..8], vectorName: "body_vector"));

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_collection_without_the_vector_re_issues_the_scroll_without_it()
    {
        var col = await CollectionAsync(vectorName: "other_vector");
        await AddChunkAsync(col, "a", 0, vectorName: "other_vector");

        var rows = await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"));

        rows.Should().ContainSingle().Which.Vector.Should().BeNull();
    }

    [Fact]
    public async Task Phase_one_over_MaxRowsScanned_raises_the_budget_exception()
    {
        var col = await CollectionAsync();
        for (var i = 0; i < 5; i++) await AddChunkAsync(col, "a", i);

        var act = () => ReadAllAsync(Source(), Query(col, maxRowsScanned: 4));

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxRowsScanned");
        (await ReadAllAsync(Source(), Query(col, maxRowsScanned: 5))).Should().HaveCount(5);
    }

    [Theory]
    [InlineData(3, 2)]   // [a(2)] then [b(2) c(1)]
    [InlineData(2, 3)]   // [a] [b] [c]
    [InlineData(1, 3)]   // a parent larger than BatchRows forms a batch on its own
    public async Task Phase_two_batches_whole_parents_by_BatchRows(int batchRows, int expectedPhaseTwoScrolls)
    {
        var col = await CollectionAsync();
        foreach (var (parent, index) in new[] { ("a", 0), ("a", 1), ("b", 0), ("b", 1), ("c", 0) })
            await AddChunkAsync(col, parent, index);

        var phaseTwo = 0;
        var spy = Substitute.For<IVectorQueryService>();
        spy.ScrollAsync(default!, default, default!, default, default, default, default).ReturnsForAnyArgs(ci =>
        {
            if (ci.ArgAt<IReadOnlyList<string>>(2).Contains("text")) phaseTwo++;
            return _svc.ScrollAsync(ci.ArgAt<string>(0), ci.ArgAt<Filter?>(1), ci.ArgAt<IReadOnlyList<string>>(2),
                ci.ArgAt<string?>(3), ci.ArgAt<uint>(4), ci.ArgAt<PointId?>(5), ci.ArgAt<CancellationToken>(6));
        });

        var rows = await ReadAllAsync(Source(spy), Query(col, batchRows: batchRows));

        rows.Should().HaveCount(5);
        phaseTwo.Should().Be(expectedPhaseTwoScrolls);
    }
}
