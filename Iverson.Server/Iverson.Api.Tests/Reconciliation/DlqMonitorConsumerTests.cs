using Confluent.Kafka;
using FluentAssertions;
using Iverson.Sql;
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
            Substitute.For<Iverson.Events.IEventConsumer>(), dlq,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

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
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

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
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

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
}
