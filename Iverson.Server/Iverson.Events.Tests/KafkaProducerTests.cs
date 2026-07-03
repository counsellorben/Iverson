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
    private static (KafkaProducer producer, IProducer<string, string> kafkaProducer, IFailedPublishSink sink) CreateProducer()
    {
        var kafkaProducer = Substitute.For<IProducer<string, string>>();
        kafkaProducer
            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>())
            .Returns(new DeliveryResult<string, string>());

        var sink = Substitute.For<IFailedPublishSink>();
        var producer = new KafkaProducer(kafkaProducer, sink, NullLogger<KafkaProducer>.Instance);
        return (producer, kafkaProducer, sink);
    }

    [Fact]
    public async Task ProduceAsync_Generic_CallsKafkaProducer_WithSerializedJson()
    {
        var (producer, kafkaProducer, _) = CreateProducer();
        var entityEvent = new EntityEvent(
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
        var (producer, kafkaProducer, _) = CreateProducer();
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

        var producer = new KafkaProducer(kafkaProducer, Substitute.For<IFailedPublishSink>(), NullLogger<KafkaProducer>.Instance);

        await producer.Invoking(p => p.ProduceAsync("topic", "key", "value"))
                      .Should().ThrowAsync<ProduceException<string, string>>();
    }

    [Fact]
    public void PublishFireAndForget_CallsProduceDotProduce_NotProduceAsync()
    {
        var (producer, kafkaProducer, _) = CreateProducer();
        var entityEvent = new EntityEvent(
            TypeName: "Player",
            Key: "player-1",
            PayloadJson: """{"score":42}""",
            TraceId: "trace-1",
            SchemaVersion: "1.0",
            OccurredAt: DateTimeOffset.UtcNow);

        producer.PublishFireAndForget(EntityTopics.Created, entityEvent.TypeName, entityEvent.Key, entityEvent);

        kafkaProducer.Received(1).Produce(
            Arg.Is(EntityTopics.Created),
            Arg.Is<Message<string, string>>(m => m.Key == entityEvent.Key),
            Arg.Any<Action<DeliveryReport<string, string>>?>());
    }

    [Fact]
    public void PublishFireAndForget_DeliveryErrorCallbackLogsError()
    {
        var kafkaProducer = Substitute.For<IProducer<string, string>>();
        DeliveryReport<string, string>? capturedReport = null;
        kafkaProducer
            .When(p => p.Produce(Arg.Any<string>(), Arg.Any<Message<string, string>>(),
                Arg.Any<Action<DeliveryReport<string, string>>?>()))
            .Do(call =>
            {
                var cb = call.ArgAt<Action<DeliveryReport<string, string>>?>(2);
                capturedReport = new DeliveryReport<string, string>
                {
                    Error = new Error(ErrorCode.BrokerNotAvailable, "broker gone")
                };
                cb?.Invoke(capturedReport);
            });

        var producer = new KafkaProducer(kafkaProducer, Substitute.For<IFailedPublishSink>(), NullLogger<KafkaProducer>.Instance);

        var act = () => producer.PublishFireAndForget(EntityTopics.Created, "Player", "k", new { x = 1 });
        act.Should().NotThrow();
    }

    [Fact]
    public async Task PublishFireAndForget_DeliveryReportHasError_RecordsFailureViaSink()
    {
        var kafkaProducer = Substitute.For<IProducer<string, string>>();
        var sink = Substitute.For<IFailedPublishSink>();
        sink.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        Action<DeliveryReport<string, string>>? capturedCallback = null;
        kafkaProducer
            .When(p => p.Produce(Arg.Any<string>(), Arg.Any<Message<string, string>>(),
                Arg.Any<Action<DeliveryReport<string, string>>?>()))
            .Do(call => capturedCallback = call.ArgAt<Action<DeliveryReport<string, string>>?>(2));

        var producer = new KafkaProducer(kafkaProducer, sink, NullLogger<KafkaProducer>.Instance);

        producer.PublishFireAndForget(EntityTopics.Created, "Player", "player-1", new { x = 1 });
        capturedCallback!.Invoke(new DeliveryReport<string, string>
        {
            Error = new Error(ErrorCode.BrokerNotAvailable, "broker gone")
        });

        // The delivery-report callback schedules the sink write asynchronously (fire-and-forget) —
        // give it a moment to run rather than asserting immediately.
        await Task.Delay(50);

        await sink.Received(1).RecordAsync("Player", "player-1", Arg.Is<string>(s => s.Contains("broker gone")));
    }

    [Fact]
    public async Task PublishFireAndForget_ProduceThrowsKafkaException_RecordsFailureViaSink()
    {
        var kafkaProducer = Substitute.For<IProducer<string, string>>();
        kafkaProducer
            .When(p => p.Produce(Arg.Any<string>(), Arg.Any<Message<string, string>>(),
                Arg.Any<Action<DeliveryReport<string, string>>?>()))
            .Do(_ => throw new KafkaException(new Error(ErrorCode.Local_QueueFull)));

        var sink = Substitute.For<IFailedPublishSink>();
        sink.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        var producer = new KafkaProducer(kafkaProducer, sink, NullLogger<KafkaProducer>.Instance);

        producer.PublishFireAndForget(EntityTopics.Created, "Player", "player-1", new { x = 1 });

        await Task.Delay(50);

        await sink.Received(1).RecordAsync("Player", "player-1", Arg.Any<string>());
    }
}
