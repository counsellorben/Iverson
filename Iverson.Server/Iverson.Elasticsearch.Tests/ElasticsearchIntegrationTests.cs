using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Elastic.Clients.Elasticsearch;
using FluentAssertions;
using Iverson.Elasticsearch;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.Elasticsearch.Tests;

public sealed class ElasticsearchContainerFixture : IAsyncLifetime
{
    // Use ES 9.x to match Elastic.Clients.Elasticsearch 9.x; security disabled for simplicity.
    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("docker.elastic.co/elasticsearch/elasticsearch:9.0.1")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("xpack.security.enabled", "false")
        .WithPortBinding(9200, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health")))
        .Build();

    public ElasticsearchService Service { get; private set; } = null!;
    public ElasticsearchClient Client  { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var port     = _container.GetMappedPublicPort(9200);
        var settings = new ElasticsearchClientSettings(new Uri($"http://{_container.Hostname}:{port}"));
        Client  = new ElasticsearchClient(settings);
        Service = new ElasticsearchService(Client, NullLogger<ElasticsearchService>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class ElasticsearchIntegrationTests(ElasticsearchContainerFixture fixture)
    : IClassFixture<ElasticsearchContainerFixture>
{
    private readonly ElasticsearchService _svc = fixture.Service;
    private readonly ElasticsearchClient _client = fixture.Client;

    // ── helpers ───────────────────────────────────────────────────────────────

    // Use a fresh index name per test to avoid state leakage
    private static string UniqueIndex() =>
        "test-" + Guid.NewGuid().ToString("N")[..8];

    private async Task RefreshAsync(string indexName) =>
        await _client.Indices.RefreshAsync(indexName);

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IndexDocument_ThenGet_RoundTrips()
    {
        var index = UniqueIndex();
        var doc   = new PlayerDoc("Allen Iverson", "PG", 76);

        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "player-3", doc);
        await RefreshAsync(index);

        var retrieved = await _svc.GetDocumentAsync<PlayerDoc>(index, "player-3");

        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Allen Iverson");
        retrieved.Position.Should().Be("PG");
        retrieved.JerseyNumber.Should().Be(76);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_WhenDocumentDoesNotExist()
    {
        var index = UniqueIndex();
        await _svc.CreateIndexAsync(index);

        var result = await _svc.GetDocumentAsync<PlayerDoc>(index, "nonexistent-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDocumentAsync_RemovesDocument()
    {
        var index = UniqueIndex();
        var doc   = new PlayerDoc("Kobe Bryant", "SG", 8);

        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "player-8", doc);
        await RefreshAsync(index);

        await _svc.DeleteDocumentAsync(index, "player-8");
        await RefreshAsync(index);

        var result = await _svc.GetDocumentAsync<PlayerDoc>(index, "player-8");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_ReturnsResults_AfterIndexing()
    {
        var index = UniqueIndex();

        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "p1", new PlayerDoc("Allen Iverson", "PG", 3));
        await _svc.IndexDocumentAsync(index, "p2", new PlayerDoc("Kobe Bryant",   "SG", 8));
        await RefreshAsync(index);

        var results = await _svc.SearchAsync<PlayerDoc>(index, "Iverson");

        results.Should().NotBeEmpty();
        results.Should().ContainSingle(p => p.Name == "Allen Iverson");
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenQueryMatchesNothing()
    {
        var index = UniqueIndex();

        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "p1", new PlayerDoc("Allen Iverson", "PG", 3));
        await RefreshAsync(index);

        var results = await _svc.SearchAsync<PlayerDoc>(index, "zzznomatch");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexExistsAsync_ReturnsFalse_BeforeCreation()
    {
        var index = UniqueIndex();

        var exists = await _svc.IndexExistsAsync(index);

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task CreateIndexAsync_MakesIndexExist()
    {
        var index = UniqueIndex();

        await _svc.CreateIndexAsync(index);

        var exists = await _svc.IndexExistsAsync(index);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task CreateIndexAsync_IsIdempotent_WhenCalledTwice()
    {
        var index = UniqueIndex();

        await _svc.CreateIndexAsync(index);

        // Should not throw
        var act = async () => await _svc.CreateIndexAsync(index);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplyMappingAsync_CreatesIndex_WhenNotExists()
    {
        var schema = new IndexSchema(UniqueIndex(), new List<FieldMapping>
        {
            new("Name",     EsFieldType.Text),
            new("Position", EsFieldType.Keyword),
        });

        await _svc.ApplyMappingAsync(schema);

        var exists = await _svc.IndexExistsAsync(schema.IndexName);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyMappingAsync_AddsNewFields_WhenSchemaExpands()
    {
        var indexName = UniqueIndex();

        var v1 = new IndexSchema(indexName, new List<FieldMapping>
        {
            new("Name", EsFieldType.Text),
        });
        await _svc.ApplyMappingAsync(v1);

        var v2 = new IndexSchema(indexName, new List<FieldMapping>
        {
            new("Name",         EsFieldType.Text),
            new("JerseyNumber", EsFieldType.Integer),
        });

        var act = async () => await _svc.ApplyMappingAsync(v2);
        await act.Should().NotThrowAsync();

        // Verify the new field is usable
        await _svc.IndexDocumentAsync(indexName, "p1", new { Name = "Allen", JerseyNumber = 3 });
        await RefreshAsync(indexName);
        var results = await _svc.SearchAsync<object>(indexName, "Allen");
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ApplyMappingAsync_RebuildsIndex_WhenFieldRemoved()
    {
        var indexName = UniqueIndex();

        var v1 = new IndexSchema(indexName, new List<FieldMapping>
        {
            new("Name",     EsFieldType.Text),
            new("Nickname", EsFieldType.Keyword),
        });
        await _svc.ApplyMappingAsync(v1);
        await _svc.IndexDocumentAsync(indexName, "p1", new { Name = "Allen Iverson", Nickname = "The Answer" });
        await RefreshAsync(indexName);

        // Remove "Nickname" — triggers reindex
        var v2 = new IndexSchema(indexName, new List<FieldMapping>
        {
            new("Name", EsFieldType.Text),
        });

        var act = async () => await _svc.ApplyMappingAsync(v2);
        await act.Should().NotThrowAsync();

        // Index should still exist and data should have been reindexed
        var exists = await _svc.IndexExistsAsync(indexName);
        exists.Should().BeTrue();

        await RefreshAsync(indexName);
        var results = await _svc.SearchAsync<object>(indexName, "Allen");
        results.Should().NotBeEmpty();
    }

    // ── AggregateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task AggregateAsync_TermsAgg_ReturnsBucketsPerDistinctValue()
    {
        var index = UniqueIndex();
        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "p1", new PlayerDoc("Allen Iverson", "PG", 3));
        await _svc.IndexDocumentAsync(index, "p2", new PlayerDoc("Kobe Bryant",   "SG", 8));
        await _svc.IndexDocumentAsync(index, "p3", new PlayerDoc("Gary Payton",   "PG", 20));
        await RefreshAsync(index);

        var specs = new List<AggregationDescriptor>
        {
            new("position_terms", AggregationKind.Terms, "position.keyword", Size: 10)
        };

        var results = await _svc.AggregateAsync(index, string.Empty, specs);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("position_terms");
        results[0].Kind.Should().Be(AggregationKind.Terms);
        results[0].Buckets.Should().NotBeNull();
        var buckets = results[0].Buckets!;
        buckets.Should().HaveCount(2);
        buckets.Should().Contain(b => b.Key == "PG" && b.DocCount == 2);
        buckets.Should().Contain(b => b.Key == "SG" && b.DocCount == 1);
    }

    [Fact]
    public async Task AggregateAsync_AvgAgg_ReturnsCorrectAverage()
    {
        var index = UniqueIndex();
        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "p1", new PlayerDoc("Allen Iverson", "PG", 3));
        await _svc.IndexDocumentAsync(index, "p2", new PlayerDoc("Kobe Bryant",   "SG", 8));
        await _svc.IndexDocumentAsync(index, "p3", new PlayerDoc("Gary Payton",   "PG", 20));
        await RefreshAsync(index);

        var specs = new List<AggregationDescriptor>
        {
            new("jersey_avg", AggregationKind.Avg, "jerseyNumber")
        };

        var results = await _svc.AggregateAsync(index, string.Empty, specs);

        results.Should().HaveCount(1);
        results[0].MetricValue.Should().BeApproximately((3 + 8 + 20) / 3.0, 0.01);
    }

    [Fact]
    public async Task AggregateAsync_MultipleAggs_ReturnsAllResults()
    {
        var index = UniqueIndex();
        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "p1", new PlayerDoc("Allen Iverson", "PG", 3));
        await _svc.IndexDocumentAsync(index, "p2", new PlayerDoc("Kobe Bryant",   "SG", 8));
        await RefreshAsync(index);

        var specs = new List<AggregationDescriptor>
        {
            new("position_terms", AggregationKind.Terms,  "position.keyword", Size: 5),
            new("jersey_min",     AggregationKind.Min,    "jerseyNumber"),
            new("jersey_max",     AggregationKind.Max,    "jerseyNumber"),
            new("jersey_sum",     AggregationKind.Sum,    "jerseyNumber"),
        };

        var results = await _svc.AggregateAsync(index, string.Empty, specs);

        results.Should().HaveCount(4);
        results.Should().Contain(r => r.Name == "position_terms" && r.Buckets!.Count == 2);
        results.Should().Contain(r => r.Name == "jersey_min" && r.MetricValue == 3);
        results.Should().Contain(r => r.Name == "jersey_max" && r.MetricValue == 8);
        results.Should().Contain(r => r.Name == "jersey_sum" && r.MetricValue == 11);
    }

    [Fact]
    public async Task AggregateAsync_WithQueryFilter_AggregatesOnlyMatchingDocs()
    {
        var index = UniqueIndex();
        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "p1", new PlayerDoc("Allen Iverson", "PG", 3));
        await _svc.IndexDocumentAsync(index, "p2", new PlayerDoc("Kobe Bryant",   "SG", 8));
        await _svc.IndexDocumentAsync(index, "p3", new PlayerDoc("Gary Payton",   "PG", 20));
        await RefreshAsync(index);

        var specs = new List<AggregationDescriptor>
        {
            new("jersey_sum", AggregationKind.Sum, "jerseyNumber")
        };

        // Filter to only PG players (jersey 3 + 20 = 23)
        var results = await _svc.AggregateAsync(index, "position.keyword:PG", specs);

        results.Should().HaveCount(1);
        results[0].MetricValue.Should().BeApproximately(23.0, 0.01);
    }

    [Fact]
    public async Task AggregateAsync_RangeAgg_ReturnsBuckets()
    {
        var index = UniqueIndex();
        await _svc.CreateIndexAsync(index);
        await _svc.IndexDocumentAsync(index, "p1", new PlayerDoc("Player A", "PG", 3));
        await _svc.IndexDocumentAsync(index, "p2", new PlayerDoc("Player B", "SG", 8));
        await _svc.IndexDocumentAsync(index, "p3", new PlayerDoc("Player C", "PG", 20));
        await RefreshAsync(index);

        var specs = new List<AggregationDescriptor>
        {
            new("jersey_range", AggregationKind.Range, "jerseyNumber",
                RangeBuckets:
                [
                    new RangeBucketDescriptor("low",  null, 10.0),
                    new RangeBucketDescriptor("high",  10.0, null)
                ])
        };

        var results = await _svc.AggregateAsync(index, string.Empty, specs);

        results.Should().HaveCount(1);
        results[0].Buckets.Should().NotBeNull();
        var lowBucket  = results[0].Buckets!.FirstOrDefault(b => b.Key == "low");
        var highBucket = results[0].Buckets!.FirstOrDefault(b => b.Key == "high");
        lowBucket.Should().NotBeNull();
        highBucket.Should().NotBeNull();
        lowBucket!.DocCount.Should().Be(2);  // jerseys 3 and 8
        highBucket!.DocCount.Should().Be(1); // jersey 20
    }

    private sealed record PlayerDoc(string Name, string Position, int JerseyNumber);
}
