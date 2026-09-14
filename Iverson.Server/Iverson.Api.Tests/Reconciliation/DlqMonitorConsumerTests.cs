using System.Text.Json;
using Confluent.Kafka;
using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Reconciliation;

public class DlqMonitorConsumerTests
{
    [Fact]
    public async Task HandleAsync_RecordsMessageWithHeaderMetadata()
    {
        var dlq = Substitute.For<IDlqRepository>();
        var sut = new Iverson.Api.Reconciliation.DlqMonitorConsumer(
            Substitute.For<Iverson.Events.IEventConsumer>(),
            dlq,
            new SchemaRegistry(Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance),
            NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

        var headers = new Headers
        {
            { "dlq.source_topic",      "iverson.entity.created"u8.ToArray() },
            { "dlq.consumer_group",    "iverson.consumer.intelligence"u8.ToArray() },
            { "dlq.exception_type",    "System.InvalidOperationException"u8.ToArray() },
            { "dlq.exception_message", "boom"u8.ToArray() },
            { "dlq.attempts",          "3"u8.ToArray() },
            { "dlq.failed_at",         System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")) },
        };

        await sut.HandleAsync("article-123", "{\"foo\":\"bar\"}", headers, CancellationToken.None);

        await dlq.Received(1).InsertAsync(Arg.Is<DlqMessage>(m =>
            m.MessageKey == "article-123" &&
            m.SourceTopic == "iverson.entity.created" &&
            m.ExceptionType == "System.InvalidOperationException" &&
            m.Attempts == 3));
    }

    [Fact]
    public async Task HandleAsync_MissingOptionalHeaders_StillRecordsWithNullExceptionFields()
    {
        var dlq = Substitute.For<IDlqRepository>();
        var sut = new Iverson.Api.Reconciliation.DlqMonitorConsumer(
            Substitute.For<Iverson.Events.IEventConsumer>(), dlq,
            new SchemaRegistry(Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance),
            NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

        var headers = new Headers
        {
            { "dlq.source_topic",   "iverson.entity.created"u8.ToArray() },
            { "dlq.consumer_group", "iverson.consumer.intelligence"u8.ToArray() },
            { "dlq.attempts",       "1"u8.ToArray() },
            { "dlq.failed_at",      System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")) },
        };

        await sut.HandleAsync("article-456", "{}", headers, CancellationToken.None);

        await dlq.Received(1).InsertAsync(Arg.Is<DlqMessage>(m =>
            m.MessageKey == "article-456" &&
            m.ExceptionType == null));
    }

    [Fact]
    public async Task HandleAsync_OffsetBearingFailedAtHeader_ParsesToUtcKind()
    {
        // dlq.failed_at is always written via DateTimeOffset.UtcNow.ToString("O"), which
        // includes an explicit "+00:00" offset. DateTime.TryParse with no DateTimeStyles
        // converts offset-bearing strings to the host's *local* time zone (Kind=Local),
        // which Npgsql then refuses to bind against a timestamptz column. The fix must
        // always yield Kind=Utc regardless of the host's local time zone.
        DlqMessage? captured = null;
        var dlq = Substitute.For<IDlqRepository>();
        dlq.InsertAsync(Arg.Do<DlqMessage>(m => captured = m)).Returns(Task.CompletedTask);
        var sut = new Iverson.Api.Reconciliation.DlqMonitorConsumer(
            Substitute.For<Iverson.Events.IEventConsumer>(), dlq,
            new SchemaRegistry(Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance),
            NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

        var failedAt = new DateTimeOffset(2026, 7, 12, 16, 23, 52, TimeSpan.Zero);
        var headers = new Headers
        {
            { "dlq.source_topic",      "iverson.entity.created"u8.ToArray() },
            { "dlq.consumer_group",    "iverson.consumer.intelligence"u8.ToArray() },
            { "dlq.attempts",          "1"u8.ToArray() },
            { "dlq.failed_at",         System.Text.Encoding.UTF8.GetBytes(failedAt.ToString("O")) },
        };

        await sut.HandleAsync("article-789", "{}", headers, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.FailedAt.Kind.Should().Be(DateTimeKind.Utc);
        captured.FailedAt.Should().Be(failedAt.UtcDateTime);
    }

    [Fact]
    public async Task HandleAsync_RegisteredSchemaWithTenantColumn_ExtractsTenantIdFromPayload()
    {
        // Positive-path coverage for the derivation path itself: the other tests above all send
        // an event whose TypeName the registry has never heard of (or no TypeName at all), so
        // registry.Get(...) always returns null and the TenantColumn lookup + ExtractString call
        // are never actually exercised. A real registered schema with a TenantColumn, and a
        // payload that actually carries a value under that column, proves the full path —
        // registry lookup succeeds, JsonDocument.Parse succeeds, ExtractString pulls the right
        // value out — works end to end, not just that the null-guards prevent a crash.
        var dlq = Substitute.For<IDlqRepository>();
        var registry = new SchemaRegistry(
            Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // TenantColumn == "TenantId"

        var sut = new Iverson.Api.Reconciliation.DlqMonitorConsumer(
            Substitute.For<Iverson.Events.IEventConsumer>(), dlq, registry,
            NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

        var entityEvent = new EntityEvent(
            EventType:      EntityEventType.Created,
            TypeName:       "Author",
            Key:            "author-77",
            PayloadJson:    "{\"Name\":\"Ada\",\"TenantId\":\"tenant-77\"}",
            TraceId:        "trace-1",
            SchemaVersion:  "v1",
            OccurredAt:     DateTimeOffset.UtcNow);
        var value = JsonSerializer.Serialize(
            entityEvent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var headers = new Headers
        {
            { "dlq.source_topic",   "iverson.entity.created"u8.ToArray() },
            { "dlq.consumer_group", "iverson.consumer.intelligence"u8.ToArray() },
            { "dlq.attempts",       "1"u8.ToArray() },
            { "dlq.failed_at",      System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")) },
        };

        await sut.HandleAsync("author-77", value, headers, CancellationToken.None);

        await dlq.Received(1).InsertAsync(Arg.Is<DlqMessage>(m =>
            m.MessageKey == "author-77" &&
            m.TenantId == "tenant-77"));
    }
}
