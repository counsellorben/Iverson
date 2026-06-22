using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class RecordStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IPostgresRepository _sql;
    private readonly Api.Schema.SchemaRegistry _registry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RecordStoreConsumerTests()
    {
        _consumer = Substitute.For<IEventConsumer>();
        _sql      = Substitute.For<IPostgresRepository>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        _registry = new Api.Schema.SchemaRegistry(_sql, NullLogger<Api.Schema.SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private RecordStoreConsumer BuildSut() =>
        new(_consumer, _sql, _registry, NullLogger<RecordStoreConsumer>.Instance);

    [Fact]
    public async Task HandleCreated_CallsUpsertSql_WithPayloadJson()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sql.Received().ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task HandleDeleted_CallsDeleteSql_WithKey()
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
            TargetStores:  StoreTarget.Record);

        var sut = BuildSut();
        await sut.HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sql.Received().ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task SkipsEvent_WhenTargetStoreDoesNotIncludeRecord()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement); // no Record flag

        _sql.ClearReceivedCalls();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sql.DidNotReceive().ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task SkipsEvent_WhenSchemaNotRegistered()
    {
        // Do NOT register any schema
        var ev = new EntityEvent(
            TypeName:      "UnknownType",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-4",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record);

        _sql.ClearReceivedCalls();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sql.DidNotReceive().ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task HandlesMalformedJson_WithoutThrowing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync("some-key", "NOT_VALID_JSON{{{{", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandlesSqlException_DoesNotPropagate()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _sql.ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>())
            .Returns<int>(_ => throw new Exception("DB connection failed"));

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record);

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
