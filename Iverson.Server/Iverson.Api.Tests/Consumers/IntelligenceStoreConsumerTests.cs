using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class IntelligenceStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IVectorService _vector;
    private readonly IEmbeddingService _embedding;
    private readonly IPostgresRepository _sql;
    private readonly SchemaRegistry _registry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public IntelligenceStoreConsumerTests()
    {
        _consumer  = Substitute.For<IEventConsumer>();
        _vector    = Substitute.For<IVectorService>();
        _embedding = Substitute.For<IEmbeddingService>();
        _sql       = Substitute.For<IPostgresRepository>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _vector.ApplyCollectionAsync(Arg.Any<CollectionSchema>()).Returns(Task.CompletedTask);
        _vector.UpsertNamedAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(Task.CompletedTask);
        _vector.DeleteAsync(Arg.Any<string>(), Arg.Any<ulong>()).Returns(Task.CompletedTask);
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(new float[768]);

        _registry = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private IntelligenceStoreConsumer BuildSut() =>
        new(_consumer, _vector, _embedding, _registry, NullLogger<IntelligenceStoreConsumer>.Instance);

    [Fact]
    public async Task HandleCreated_WithVectorField_CallsEmbedAndUpsertNamed()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("Great Title", Arg.Any<CancellationToken>())
                  .Returns(fakeVector);

        var payload = """{"Title":"Great Title","Body":"Some body text","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.Received().EmbedAsync(
            "Great Title",
            Arg.Any<CancellationToken>());

        await _vector.Received().UpsertNamedAsync(
            "articles",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Fact]
    public async Task HandleCreated_WithChunkField_SplitsTextAndUpserts()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        // Should upsert at least once into the chunks collection
        await _vector.Received().UpsertNamedAsync(
            "articles_chunks",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Fact]
    public async Task HandleDeleted_CallsVectorDelete()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var key = Guid.NewGuid().ToString();
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           key,
            PayloadJson:   "{}",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vector.Received(1).DeleteAsync("articles", Arg.Any<ulong>());
    }

    [Fact]
    public async Task SkipsEvent_WhenNoIntelligenceFlag()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Title":"Test"}""",
            TraceId:       "trace-4",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement); // no Intelligence

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _vector.DidNotReceive().UpsertNamedAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Fact]
    public async Task SkipsEmptyTextField_DoesNotCallEmbed()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        // Title is empty string — should not embed
        var payload = """{"Title":"","Body":"some body","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.DidNotReceive().EmbedAsync("", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedFailure_Propagates()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns<float[]>(_ => throw new Exception("Ollama timeout"));

        var payload = """{"Title":"Test Title","Body":"Some body","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-6",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("Ollama timeout");
    }

    [Fact]
    public async Task HandleCreated_PointPayload_ContainsKeyAndCamelCaseFields()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var entityKey = Guid.NewGuid().ToString();
        var titleText = "My Test Title";
        var payload   = $$$"""{"Title":"{{{titleText}}}","Body":"Some body text","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           entityKey,
            PayloadJson:   payload,
            TraceId:       "trace-payload",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        IReadOnlyDictionary<string, string>? capturedPayload = null;
        _vector.UpsertNamedAsync(
            "articles",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Do<IReadOnlyDictionary<string, string>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["key"].Should().Be(entityKey);
        capturedPayload["title"].Should().Be(titleText);
    }

    [Fact]
    public async Task HandleCreated_WithMultipleVectorFields_EmbedsAllFields()
    {
        // Schema with two vector fields — verifies both EmbedAsync calls fire
        var twoVectorSchema = new SchemaDescriptor
        {
            TypeName       = "Doc",
            TableName      = "docs",
            CollectionName = "docs",
            KeyColumn      = new ColumnDescriptor("Id",    "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Title", "text", false),
                              new ColumnDescriptor("Summary", "text", false)],
            FkColumns      = [],
            VectorFields   = [
                new VectorDescriptor("Title",   768, "nomic-embed-text"),
                new VectorDescriptor("Summary", 768, "nomic-embed-text")
            ],
            ChunkFields    = [],
            Relations      = []
        };
        await _registry.RegisterAsync(twoVectorSchema);

        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(new float[768]);

        var payload = """{"Title":"Hello","Summary":"World","Id":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            TypeName:      "Doc",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "t-parallel",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.Received(1).EmbedAsync("Hello",  Arg.Any<CancellationToken>());
        _ = _embedding.Received(1).EmbedAsync("World",  Arg.Any<CancellationToken>());
        _ = _embedding.Received(2).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkSplitting_ProducesMultipleChunks_ForLongText()
    {
        // Custom schema: maxTokens=50 (200 chars), overlap=10 (40 chars) → step=160 chars
        // A 3000-char body → at least 10 chunks (3000 / 160 ≈ 18.75)
        var customSchema = new SchemaDescriptor
        {
            TypeName       = "Doc",
            TableName      = "docs",
            CollectionName = "docs",
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Body", "text", false)],
            FkColumns      = [],
            VectorFields   = [],
            ChunkFields    = [new ChunkDescriptor("Body", 50, 10, "text-embedding-3-small", 1536)],
            Relations      = []
        };
        await _registry.RegisterAsync(customSchema);

        var longBody = new string('a', 3000);
        var payload  = $$$"""{"Body":"{{{longBody}}}"}""";
        var ev = new EntityEvent(
            TypeName:      "Doc",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-7",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var upsertCount = 0;
        _vector.UpsertNamedAsync(
            "docs_chunks",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(ci =>
            {
                upsertCount++;
                return Task.CompletedTask;
            });

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        upsertCount.Should().BeGreaterThanOrEqualTo(10);
    }
}
