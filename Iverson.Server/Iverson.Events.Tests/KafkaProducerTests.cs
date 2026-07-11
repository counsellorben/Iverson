using System.Text.Json;
using Confluent.Kafka;
using FluentAssertions;
using Iverson.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Events.Tests;

public sealed class KafkaProducerTests
{
    private static (KafkaProducer producer, IProducer<string, string> kafkaProducer) CreateProducer()
    {
        var kafkaProducer = Substitute.For<IProducer<string, string>>();
        kafkaProducer
            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>())
            .Returns(new DeliveryResult<string, string>());

        var producer = new KafkaProducer(kafkaProducer, NullLogger<KafkaProducer>.Instance);
        return (producer, kafkaProducer);
    }

    [Fact]
    public async Task ProduceAsync_Generic_CallsKafkaProducer_WithSerializedJson()
    {
        var (producer, kafkaProducer) = CreateProducer();
        var entityEvent = new EntityEvent(
            EventType: EntityEventType.Created,
            TypeName: "Player",
            Key: "player-1",
            PayloadJson: """{"score":42}""",
            TraceId: "trace-1",
            SchemaVersion: "1.0",
            OccurredAt: DateTimeOffset.UtcNow);

        await producer.ProduceAsync(EntityTopics.Created, entityEvent.Key, entityEvent);

        await kafkaProducer.Received(1).ProduceAsync(
            Arg.Is(EntityTopics.Created),
            Arg.Any<Message<string, string>>());

        var call = kafkaProducer.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IProducer<string, string>.ProduceAsync));
        var message = (Message<string, string>)call.GetArguments()[1]!;
        var parsed = JsonDocument.Parse(message.Value);
        parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task ProduceAsync_String_CallsKafkaProducer_WithCorrectTopicKeyValue()
    {
        var (producer, kafkaProducer) = CreateProducer();
        const string topic = "iverson.entity.created";
        const string key = "record-99";
        const string value = """{"hello":"world"}""";

        await producer.ProduceAsync(topic, key, value);

        await kafkaProducer.Received(1).ProduceAsync(
            Arg.Is(topic),
            Arg.Is<Message<string, string>>(m => m.Key == key && m.Value == value));
    }

    [Fact]
    public async Task ProduceAsync_PropagatesException_OnKafkaFailure()
    {
        var kafkaProducer = Substitute.For<IProducer<string, string>>();
        kafkaProducer
            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>())
            .Throws(new ProduceException<string, string>(
                new Error(ErrorCode.BrokerNotAvailable),
                new DeliveryResult<string, string>()));

        var producer = new KafkaProducer(kafkaProducer, NullLogger<KafkaProducer>.Instance);

        await producer.Invoking(p => p.ProduceAsync("topic", "key", "value"))
                      .Should().ThrowAsync<ProduceException<string, string>>();
    }
}
