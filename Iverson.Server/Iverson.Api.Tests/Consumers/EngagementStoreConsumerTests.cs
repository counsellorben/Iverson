using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class EngagementStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IStarRocksRepository _sr;
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
        _sr       = Substitute.For<IStarRocksRepository>();
        _sql      = Substitute.For<IPostgresRepository>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>())
           .Returns(Task.CompletedTask);
        _sr.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        _registry = new Api.Schema.SchemaRegistry(_sql, NullLogger<Api.Schema.SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private EngagementStoreConsumer BuildSut() =>
        new(_consumer, _sr, _registry, NullLogger<EngagementStoreConsumer>.Instance);

    [Fact]
    public async Task HandleUpsert_WithEngagementFlag_CallsUpsertAsync()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Engagement);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).UpsertAsync(
            Arg.Is<StarRocksTableSchema>(s => s.TableName == "authors"),
            Arg.Any<string>());
    }

    [Fact]
    public async Task HandleDelete_WithEngagementFlag_CallsDeleteAsync()
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

        await BuildSut().HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).DeleteAsync("authors", "Id", key);
    }

    [Fact]
    public async Task SkipsEvent_WhenNoEngagementFlag()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandlesMalformedJson_WithoutThrowing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var act = async () =>
            await BuildSut().HandleUpsertAsync("some-key", "NOT_VALID_JSON{{{", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DropsEvent_WhenSchemaNotRegistered()
    {
        var ev = new EntityEvent(
            TypeName:      "Unknown",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   "{}",
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>());
    }
}
