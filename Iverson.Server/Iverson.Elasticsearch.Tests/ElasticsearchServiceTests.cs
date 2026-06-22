using FluentAssertions;
using Iverson.Elasticsearch;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Xunit;

namespace Iverson.Elasticsearch.Tests;

public sealed class ElasticsearchServiceTests
{
    // ─── Interface contract tests (mocked) ───────────────────────────────────

    [Fact]
    public async Task IndexDocumentAsync_IsCalledWithCorrectIndexAndId()
    {
        var svc = Substitute.For<IElasticsearchService>();
        var doc = new { Name = "Allen Iverson" };
        const string indexName = "players";
        const string id = "player-3";

        await svc.IndexDocumentAsync(indexName, id, doc);

        await svc.Received(1).IndexDocumentAsync(indexName, id, doc);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_WhenNotFound()
    {
        var svc = Substitute.For<IElasticsearchService>();
        svc.GetDocumentAsync<object>(Arg.Any<string>(), Arg.Any<string>()).ReturnsNull();

        var result = await svc.GetDocumentAsync<object>("players", "missing-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyCollection_WhenNoMatches()
    {
        var svc = Substitute.For<IElasticsearchService>();
        svc.SearchAsync<object>(Arg.Any<string>(), Arg.Any<string>())
           .Returns(Array.Empty<object>());

        var result = await svc.SearchAsync<object>("players", "query");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteDocumentAsync_IsCalledWithCorrectArgs()
    {
        var svc = Substitute.For<IElasticsearchService>();
        const string indexName = "players";
        const string id = "player-3";

        await svc.DeleteDocumentAsync(indexName, id);

        await svc.Received(1).DeleteDocumentAsync(indexName, id);
    }

    [Fact]
    public async Task IndexExistsAsync_ReturnsFalse_WhenIndexNotFound()
    {
        var svc = Substitute.For<IElasticsearchService>();
        svc.IndexExistsAsync(Arg.Any<string>()).Returns(false);

        var result = await svc.IndexExistsAsync("non-existent-index");

        result.Should().BeFalse();
    }

    // ─── Schema record tests (pure value tests, no mocks) ────────────────────

    [Fact]
    public void IndexSchema_StoresIndexName()
    {
        var schema = new IndexSchema("players", new List<FieldMapping>());

        schema.IndexName.Should().Be("players");
    }

    [Fact]
    public void FieldMapping_StoresNameAndType()
    {
        var mapping = new FieldMapping("embedding", EsFieldType.DenseVector, VectorDims: 1536);

        mapping.Name.Should().Be("embedding");
        mapping.FieldType.Should().Be(EsFieldType.DenseVector);
        mapping.VectorDims.Should().Be(1536);
    }

    [Fact]
    public void EsFieldType_DenseVector_HasCorrectDiscriminant()
    {
        ((int)EsFieldType.DenseVector).Should().BeGreaterThan(0);
    }

}
