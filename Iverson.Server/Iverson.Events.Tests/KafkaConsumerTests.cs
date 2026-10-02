using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using Iverson.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Events.Tests;

public sealed class KafkaConsumerTests
{
    private static (KafkaConsumer consumer, IConsumer<string, string> fakeConsumer, IAdminClient fakeAdmin, CancellationToken ct) CreateConsumer()
    {
        var fakeConsumer = Substitute.For<IConsumer<string, string>>();
        // The first Consume() call cancels the token and throws, as the real client does on shutdown,
        // so ConsumeAsync's loop hits its `catch (OperationCanceledException) when (...) { break; }`
        // handler immediately and the test doesn't hang in an infinite polling loop.
        var cts = new CancellationTokenSource();
        fakeConsumer.When(x => x.Consume(Arg.Any<CancellationToken>())).Do(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var fakeAdmin = Substitute.For<IAdminClient>();
        fakeAdmin.CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>()).Returns(Task.CompletedTask);

        var producer = Substitute.For<IProducer<string, string>>();
        var dispatcher = new MessageDispatcher(producer, NullLogger<MessageDispatcher>.Instance);

        var consumer = new KafkaConsumer(
            new KafkaOptions { BootstrapServers = "localhost:9092" },
            NullLogger<KafkaConsumer>.Instance,
            dispatcher,
            _ => fakeConsumer,
            _ => fakeAdmin);

        return (consumer, fakeConsumer, fakeAdmin, cts.Token);
    }

    [Fact]
    public async Task ConsumeAsync_UsesInjectedConsumerFactory_NotAConcreteConfluentClient()
    {
        var (consumer, fakeConsumer, _, ct) = CreateConsumer();

        await consumer.ConsumeAsync(
            "topic",
            "group",
            (_, _, _) => Task.CompletedTask,
            ct);

        fakeConsumer.Received(1).Subscribe("topic");
        fakeConsumer.Received(1).Close();
    }

    [Fact]
    public async Task ConsumeAsync_UsesInjectedAdminClientFactory_ToEnsureTopicExists()
    {
        var (consumer, _, fakeAdmin, ct) = CreateConsumer();

        await consumer.ConsumeAsync(
            "topic",
            "group",
            (_, _, _) => Task.CompletedTask,
            ct);

        await fakeAdmin.Received(1).CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>());
    }

    [Fact]
    public async Task ConsumeRawAsync_UsesInjectedConsumerFactory_NotAConcreteConfluentClient()
    {
        var (consumer, fakeConsumer, _, ct) = CreateConsumer();

        await consumer.ConsumeRawAsync(
            "topic",
            "group",
            (_, _, _, _) => Task.CompletedTask,
            ct);

        fakeConsumer.Received(1).Subscribe("topic");
    }

    [Fact]
    public async Task ConsumeAsync_DispatchThrows_DoesNotCommit_AndRethrows()
    {
        // The halt path: a message whose dispatch throws must never have its offset committed,
        // and ConsumeAsync must rethrow so ConsumerResilience restarts the loop and the broker
        // redelivers from the last committed offset. Dispatch is made to throw by a poison message
        // whose DLQ write fails — that path increments none of the dispatcher's counters, so this
        // test cannot perturb MessageDispatcherTests' MeterListener assertions running in parallel.
        var result = new ConsumeResult<string, string>
        {
            Message              = new Message<string, string> { Key = "key-1", Value = "{}", Headers = new Headers() },
            TopicPartitionOffset = new TopicPartitionOffset("topic", new Partition(0), new Offset(7)),
        };
        var fakeConsumer = Substitute.For<IConsumer<string, string>>();
        fakeConsumer.Consume(Arg.Any<CancellationToken>()).Returns(result);

        var fakeAdmin = Substitute.For<IAdminClient>();
        fakeAdmin.CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>()).Returns(Task.CompletedTask);

        var producer = Substitute.For<IProducer<string, string>>();
        producer
            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KafkaException(ErrorCode.Local_Transport));
        var dispatcher = new MessageDispatcher(producer, NullLogger<MessageDispatcher>.Instance);

        var consumer = new KafkaConsumer(
            new KafkaOptions { BootstrapServers = "localhost:9092" },
            NullLogger<KafkaConsumer>.Instance,
            dispatcher,
            _ => fakeConsumer,
            _ => fakeAdmin);

        var act = () => consumer.ConsumeAsync(
            "topic",
            "group",
            (_, _, _) => throw new PoisonMessageException("bad json"),
            CancellationToken.None);

        await act.Should().ThrowAsync<KafkaException>();
        fakeConsumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, string>>());
        fakeConsumer.Received(1).Consume(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_ShutdownDuringDispatch_ReturnsNormally_WithoutCommitting()
    {
        // Only a cancelled consumer token is shutdown: the loop exits quietly and the uncommitted message is
        // redelivered on the next start. (A cancellation while the token is live halts instead — see
        // MessageDispatcherTests, which owns the tests that touch the dispatcher's counters.)
        var result = new ConsumeResult<string, string>
        {
            Message              = new Message<string, string> { Key = "key-1", Value = "{}", Headers = new Headers() },
            TopicPartitionOffset = new TopicPartitionOffset("topic", new Partition(0), new Offset(7)),
        };
        var fakeConsumer = Substitute.For<IConsumer<string, string>>();
        fakeConsumer.Consume(Arg.Any<CancellationToken>()).Returns(result);
        var fakeAdmin = Substitute.For<IAdminClient>();
        fakeAdmin.CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>()).Returns(Task.CompletedTask);

        var consumer = new KafkaConsumer(
            new KafkaOptions { BootstrapServers = "localhost:9092" },
            NullLogger<KafkaConsumer>.Instance,
            new MessageDispatcher(Substitute.For<IProducer<string, string>>(), NullLogger<MessageDispatcher>.Instance),
            _ => fakeConsumer,
            _ => fakeAdmin);

        using var cts = new CancellationTokenSource();
        var act = () => consumer.ConsumeAsync(
            "topic",
            "group",
            (_, _, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
            cts.Token);

        await act.Should().NotThrowAsync();
        fakeConsumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, string>>());
        fakeConsumer.Received(1).Close();
    }
}
