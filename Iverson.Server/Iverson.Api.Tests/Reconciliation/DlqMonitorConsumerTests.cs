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
}
