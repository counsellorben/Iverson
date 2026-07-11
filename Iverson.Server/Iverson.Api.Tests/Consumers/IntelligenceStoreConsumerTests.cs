using System.Reflection;
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
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class IntelligenceStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IVectorSchemaManager _vectorSchema;
    private readonly IVectorWriteService _vectorWrite;
    private readonly IEmbeddingService _embedding;
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly SchemaRegistry _registry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public IntelligenceStoreConsumerTests()
    {
        _consumer  = Substitute.For<IEventConsumer>();
        _embedding = Substitute.For<IEmbeddingService>();
        _sql       = Substitute.For<IRecordStoreQueryExecutor>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        _vectorSchema = Substitute.For<IVectorSchemaManager>();
        _vectorWrite  = Substitute.For<IVectorWriteService>();

        _vectorSchema.ApplyCollectionAsync(Arg.Any<CollectionSchema>()).Returns(Task.CompletedTask);
        _vectorWrite.UpsertNamedAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>())
            .Returns(Task.CompletedTask);
        _vectorWrite.DeleteAsync(Arg.Any<string>(), Arg.Any<ulong>()).Returns(Task.CompletedTask);
        _vectorWrite.DeleteByFilterAsync(Arg.Any<string>(), Arg.Any<Filter>()).Returns(Task.CompletedTask);
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(new float[768]);

        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private IntelligenceStoreConsumer BuildSut() =>
        new(_consumer, _vectorSchema, _vectorWrite, _embedding, _registry, NullLogger<IntelligenceStoreConsumer>.Instance);

    [Fact]
    public async Task HandleCreated_WithVectorField_CallsEmbedAndUpsertNamed()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("Great Title", Arg.Any<CancellationToken>())
                  .Returns(fakeVector);

        var payload = """{"Title":"Great Title","Body":"Some body text","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
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

        await _vectorWrite.Received().UpsertNamedAsync(
            "articles",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task HandleCreated_WithChunkField_SplitsTextAndUpserts()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
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
        await _vectorWrite.Received().UpsertNamedAsync(
            "articles_chunks",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task HandleDeleted_CallsVectorDelete()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var key = Guid.NewGuid().ToString();
        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Article",
            Key:           key,
            PayloadJson:   "{}",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).DeleteAsync("articles", Arg.Any<ulong>());
    }

    [Fact]
    public async Task HandleDeleteAsync_WithChunkFields_DeletesChunkPointsByParentIdFilter()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema()); // has ChunkFields (Body)

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Article",
            Key:           "article-123",
            PayloadJson:   "{}",
            TraceId:       "trace-chunk-delete",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).DeleteByFilterAsync(
            "articles_chunks",
            Arg.Is<Filter>(f => f.Must.Count == 1 && f.Must[0].Field.Key == "parent_id"
                              && f.Must[0].Field.Match.Keyword == "article-123"));
    }

    [Fact]
    public async Task SkipsEvent_WhenNoIntelligenceFlag()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
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
        await _vectorWrite.DidNotReceive().UpsertNamedAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task SkipsEmptyTextField_DoesNotCallEmbed()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        // Title is empty string — should not embed
        var payload = """{"Title":"","Body":"some body","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
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
            EventType:     EntityEventType.Created,
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
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           entityKey,
            PayloadJson:   payload,
            TraceId:       "trace-payload",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite.UpsertNamedAsync(
            "articles",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
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
            EventType:     EntityEventType.Created,
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
            EventType:     EntityEventType.Created,
            TypeName:      "Doc",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-7",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var upsertCount = 0;
        _vectorWrite.UpsertNamedAsync(
            "docs_chunks",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>())
            .Returns(ci =>
            {
                upsertCount++;
                return Task.CompletedTask;
            });

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        upsertCount.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task HandleCreated_PointPayload_ContainsTypedScalarAndFkColumns()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var entityKey  = Guid.NewGuid().ToString();
        var authorId   = "00000000-0000-0000-0000-000000000001";
        var payload    = $$$"""{"Title":"T","Body":"B","AuthorId":"{{{authorId}}}"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           entityKey,
            PayloadJson:   payload,
            TraceId:       "trace-typed",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite.UpsertNamedAsync(
            "articles",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["authorId"].Should().Be(authorId);
    }

    [Fact]
    public void ComputeChunkPointId_SameInputs_ProducesSameIdAcrossHashSeeds()
    {
        // Regression test for the process-restart chunk-ID instability bug: the old
        // implementation folded in string.GetHashCode(), which .NET randomizes per
        // process, so the same (parentId, fieldName, chunkIndex) must produce the
        // same point ID regardless of AppDomain string-hash-seed — this test can't
        // literally restart the process, so instead it asserts the method's result
        // is a pure function of its inputs, computed twice, with no reliance on any
        // process-global mutable state (the strongest test obtainable in-process).
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "ComputeChunkPointId", BindingFlags.NonPublic | BindingFlags.Static)!;

        var first  = (ulong)method.Invoke(null, [42UL, "Body", 3])!;
        var second = (ulong)method.Invoke(null, [42UL, "Body", 3])!;

        first.Should().Be(second);
        first.Should().NotBe(0UL);

        // Different fieldName must still produce a different id (collision resistance
        // is not weakened by removing GetHashCode()).
        var differentField = (ulong)method.Invoke(null, [42UL, "Title", 3])!;
        differentField.Should().NotBe(first);
    }

    [Fact]
    public void ComputeChunkPointId_IsStableAcrossHypotheticalProcessRestarts()
    {
        // Hard-codes the expected output for a fixed input so a future accidental
        // reintroduction of GetHashCode() (or any other process-seeded source) is
        // caught immediately — this exact numeric value must never change for these
        // inputs, in this process or any other.
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "ComputeChunkPointId", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (ulong)method.Invoke(null, [42UL, "Body", 3])!;

        // Compute the expected value independently (not by calling the method under test)
        // using the same FNV-1a + mixing formula, so this test would fail if the
        // implementation's formula changes even though it's still "deterministic".
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime       = 1099511628211UL;
        var fnv = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes("Body"))
        {
            fnv ^= b;
            fnv *= prime;
        }
        var expected = 42UL ^ ((fnv * 1000003UL + 3UL) * 0x9E3779B97F4A7C15UL);

        result.Should().Be(expected);
    }

    [Fact]
    public void KeyToUlong_NonGuidKey_IsStableAcrossHashSeeds()
    {
        // Regression test for the same hash-instability bug ComputeChunkPointId was fixed
        // for: the non-GUID fallback branch is unreachable today (keys are server-generated
        // UUIDv7, always GUID-parseable) but feeds directly into ComputeChunkPointId's
        // parentId, so it must not rely on string.GetHashCode() either. As with
        // ComputeChunkPointId, this asserts the method is a pure function of its input.
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "KeyToUlong", BindingFlags.NonPublic | BindingFlags.Static)!;

        var first  = (ulong)method.Invoke(null, ["not-a-guid-key"])!;
        var second = (ulong)method.Invoke(null, ["not-a-guid-key"])!;

        first.Should().Be(second);
        first.Should().NotBe(0UL);

        var differentKey = (ulong)method.Invoke(null, ["another-non-guid-key"])!;
        differentKey.Should().NotBe(first);
    }

    [Fact]
    public void KeyToUlong_NonGuidKey_UsesFnvHash()
    {
        // Hard-codes the expected output for a fixed input so a future accidental
        // reintroduction of GetHashCode() in the fallback branch is caught immediately.
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "KeyToUlong", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (ulong)method.Invoke(null, ["not-a-guid-key"])!;

        // Compute the expected value independently (not by calling the method under test)
        // using the same FNV-1a formula as FnvHash, so this test would fail if the
        // implementation's hash source changes even though it's still "deterministic".
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime       = 1099511628211UL;
        var expected = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes("not-a-guid-key"))
        {
            expected ^= b;
            expected *= prime;
        }

        result.Should().Be(expected);
    }

    [Fact]
    public async Task DispatchAsync_CreatedEvent_RoutesToUpsert()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var payload = """{"Title":"Great Title","Body":"Some body text","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-dispatch-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received().UpsertNamedAsync(
            "articles",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
        await _vectorWrite.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<ulong>());
    }

    [Fact]
    public async Task DispatchAsync_DeletedEvent_RoutesToDelete()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var key = Guid.NewGuid().ToString();

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Article",
            Key:           key,
            PayloadJson:   "{}",
            TraceId:       "trace-dispatch-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).DeleteAsync("articles", Arg.Any<ulong>());
        await _vectorWrite.DidNotReceive().UpsertNamedAsync(
            Arg.Any<string>(), Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(), Arg.Any<IReadOnlyDictionary<string, object>?>());
    }
}
