using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using Iverson.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Events.Tests;

public sealed class KafkaConsumerTests
{
    private static (KafkaConsumer consumer, IConsumer<string, string> fakeConsumer, IAdminClient fakeAdmin) CreateConsumer()
    {
        var fakeConsumer = Substitute.For<IConsumer<string, string>>();
        // Throwing OperationCanceledException from the first Consume() call causes
        // ConsumeAsync's loop to hit its `catch (OperationCanceledException) { break; }`
        // handler immediately, so the test doesn't hang in an infinite polling loop.
        fakeConsumer.When(x => x.Consume(Arg.Any<CancellationToken>())).Throw(new OperationCanceledException());

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

        return (consumer, fakeConsumer, fakeAdmin);
    }

    [Fact]
    public async Task ConsumeAsync_UsesInjectedConsumerFactory_NotAConcreteConfluentClient()
    {
        var (consumer, fakeConsumer, _) = CreateConsumer();

        await consumer.ConsumeAsync("topic", "group", (_, _, _) => Task.CompletedTask, CancellationToken.None);

        fakeConsumer.Received(1).Subscribe("topic");
        fakeConsumer.Received(1).Close();
    }

    [Fact]
    public async Task ConsumeAsync_UsesInjectedAdminClientFactory_ToEnsureTopicExists()
    {
        var (consumer, _, fakeAdmin) = CreateConsumer();

        await consumer.ConsumeAsync("topic", "group", (_, _, _) => Task.CompletedTask, CancellationToken.None);

        await fakeAdmin.Received(1).CreateTopicsAsync(Arg.Any<IEnumerable<TopicSpecification>>());
    }

    [Fact]
    public async Task ConsumeRawAsync_UsesInjectedConsumerFactory_NotAConcreteConfluentClient()
    {
        var (consumer, fakeConsumer, _) = CreateConsumer();

        await consumer.ConsumeRawAsync("topic", "group", (_, _, _, _) => Task.CompletedTask, CancellationToken.None);

        fakeConsumer.Received(1).Subscribe("topic");
    }
}
