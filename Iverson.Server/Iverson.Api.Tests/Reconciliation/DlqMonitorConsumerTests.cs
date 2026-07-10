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
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var sut = new Iverson.Api.Reconciliation.DlqMonitorConsumer(
            Substitute.For<Iverson.Events.IEventConsumer>(), sql,
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

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"INSERT INTO \"{Iverson.Api.Reconciliation.DlqSchema.TableName}\"")),
            Arg.Is<object>(o =>
                (string)o.GetType().GetProperty("SourceTopic")!.GetValue(o)! == "iverson.entity.created" &&
                (string)o.GetType().GetProperty("ExceptionType")!.GetValue(o)! == "System.InvalidOperationException" &&
                (int)o.GetType().GetProperty("Attempts")!.GetValue(o)! == 3));
    }

    [Fact]
    public async Task HandleAsync_MissingOptionalHeaders_StillRecordsWithNullExceptionFields()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var sut = new Iverson.Api.Reconciliation.DlqMonitorConsumer(
            Substitute.For<Iverson.Events.IEventConsumer>(), sql,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Iverson.Api.Reconciliation.DlqMonitorConsumer>.Instance);

        var headers = new Headers
        {
            { "dlq.source_topic",   "iverson.entity.created"u8.ToArray() },
            { "dlq.consumer_group", "iverson.consumer.intelligence"u8.ToArray() },
            { "dlq.attempts",       "1"u8.ToArray() },
            { "dlq.failed_at",      System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")) },
        };

        await sut.HandleAsync("article-456", "{}", headers, CancellationToken.None);

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"INSERT INTO \"{Iverson.Api.Reconciliation.DlqSchema.TableName}\"")),
            Arg.Is<object>(o =>
                o.GetType().GetProperty("ExceptionType")!.GetValue(o) == null));
    }
}
