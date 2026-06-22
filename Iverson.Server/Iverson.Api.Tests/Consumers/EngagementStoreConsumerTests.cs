using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Tests.Helpers;
using Iverson.Elasticsearch;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class EngagementStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IElasticsearchService _es;
    private readonly IPostgresRepository _sql;
    private readonly Api.Schema.SchemaRegistry _registry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public EngagementStoreConsumerTests()
    {
        _consumer = Substitute.For<IEventConsumer>();
        _es       = Substitute.For<IElasticsearchService>();
        _sql      = Substitute.For<IPostgresRepository>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _es.IndexDocumentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
           .Returns(Task.CompletedTask);
        _es.DeleteDocumentAsync(Arg.Any<string>(), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        _registry = new Api.Schema.SchemaRegistry(_sql, NullLogger<Api.Schema.SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private EngagementStoreConsumer BuildSut() =>
        new(_consumer, _es, _sql, _registry, NullLogger<EngagementStoreConsumer>.Instance);

    [Fact]
    public async Task HandleCreated_WithEngagementFlag_CallsIndexDocument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Engagement);

        var sut = BuildSut();
        await sut.HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _es.Received(1).IndexDocumentAsync(
            "authors",
            ev.Key,
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task HandleDeleted_WithEngagementFlag_CallsDeleteDocument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var key = Guid.NewGuid().ToString();
        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           key,
            PayloadJson:   "{}",
            TraceId:       "trace-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Engagement);

        var sut = BuildSut();
        await sut.HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _es.Received(1).DeleteDocumentAsync("authors", key);
    }

    [Fact]
    public async Task SkipsEvent_WhenNoEngagementFlag()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record); // no Engagement

        var sut = BuildSut();
        await sut.HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _es.DidNotReceive().IndexDocumentAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FanoutFlag_ReIndexesDependents()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var authorKey  = Guid.NewGuid().ToString();
        var articleKey = Guid.NewGuid().ToString();

        // SQL returns a JSON row for the dependent article
        var articleJson = $$$"""{"Id":"{{{articleKey}}}","Title":"Great Article","AuthorId":"{{{authorKey}}}"}""";
        _sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(new List<string> { articleJson });

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           authorKey,
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-4",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Engagement | StoreTarget.EngagementFanout);

        var sut = BuildSut();
        await sut.HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        // Should index Author directly AND re-index Article via fanout
        await _es.Received().IndexDocumentAsync(
            "articles",
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task FanOut_IsNoOp_WhenNoDependentSchemas()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // no dependents registered

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Engagement | StoreTarget.EngagementFanout);

        var sut = BuildSut();
        await sut.HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        // Only the author's own direct index; no fanout SQL calls
        await _sql.DidNotReceive().QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>());
    }

    [Fact]
    public async Task HandlesMalformedJson_WithoutThrowing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var sut = BuildSut();
        var act = async () => await sut.HandleUpsertAsync("some-key", "NOT_VALID_JSON{{{{", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
