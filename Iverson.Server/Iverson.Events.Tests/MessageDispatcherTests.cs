using System.Diagnostics.Metrics;
using System.Text;
using Confluent.Kafka;
using FluentAssertions;
using Iverson.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Events.Tests;

public sealed class MessageDispatcherTests
{
    private readonly IProducer<string, string> _producer = Substitute.For<IProducer<string, string>>();

    public MessageDispatcherTests()
    {
        _producer.ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new DeliveryResult<string, string>()));
    }

    private MessageDispatcher BuildSut(int maxAttempts = 3) =>
        new(_producer, NullLogger<MessageDispatcher>.Instance,
            new MessageDispatcherOptions { MaxAttempts = maxAttempts, Backoff = _ => TimeSpan.Zero });

    private static DispatchContext Ctx(string value = """{"ok":true}""") =>
        new("iverson.entity.created", "iverson.consumer.test", "key-1", value, new Headers());

    [Fact]
    public async Task Success_InvokesHandlerOnce_NoDlq()
    {
        var calls = 0;
        await BuildSut().DispatchAsync(Ctx(), (_, _, _) => { calls++; return Task.CompletedTask; }, CancellationToken.None);

        calls.Should().Be(1);
        await _producer.DidNotReceive().ProduceAsync(
            Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailure_RecoversOnSecondAttempt_NoDlq()
    {
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c)
        {
            calls++;
            if (calls == 1) throw new Exception("transient");
            return Task.CompletedTask;
        }

        await BuildSut().DispatchAsync(Ctx(), Handler, CancellationToken.None);

        calls.Should().Be(2);
        await _producer.DidNotReceive().ProduceAsync(
            Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailure_ExhaustsAttempts_RoutesToDlqAndReturns()
    {
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c) { calls++; throw new Exception("always"); }

        await BuildSut(maxAttempts: 3).DispatchAsync(Ctx(), Handler, CancellationToken.None);

        calls.Should().Be(3);
        await _producer.Received(1).ProduceAsync(
            EntityTopics.Dlq,
            Arg.Is<Message<string, string>>(m => m.Key == "key-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PoisonMessage_RoutesToDlqImmediately_NoRetry()
    {
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c) { calls++; throw new PoisonMessageException("bad json"); }

        await BuildSut().DispatchAsync(Ctx(), Handler, CancellationToken.None);

        calls.Should().Be(1);
        await _producer.Received(1).ProduceAsync(
            EntityTopics.Dlq, Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DlqProduceFailure_Throws_DoesNotSwallow()
    {
        _producer.ProduceAsync(EntityTopics.Dlq, Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(new Exception("kafka down"));

        Task Handler(string k, string v, CancellationToken c) => throw new PoisonMessageException("bad");

        var act = async () => await BuildSut().DispatchAsync(Ctx(), Handler, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("kafka down");
    }

    [Fact]
    public async Task DlqMessage_CarriesMetadataHeadersAndVerbatimValue()
    {
        Message<string, string>? captured = null;
        _producer.ProduceAsync(EntityTopics.Dlq, Arg.Do<Message<string, string>>(m => captured = m), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new DeliveryResult<string, string>()));

        Task Handler(string k, string v, CancellationToken c) => throw new PoisonMessageException("bad json");

        await BuildSut().DispatchAsync(Ctx(), Handler, CancellationToken.None);

        captured.Should().NotBeNull();
        string Header(string key) => Encoding.UTF8.GetString(captured!.Headers.GetLastBytes(key));
        Header("dlq.source_topic").Should().Be("iverson.entity.created");
        Header("dlq.consumer_group").Should().Be("iverson.consumer.test");
        Header("dlq.exception_type").Should().Contain("PoisonMessageException");
        captured!.Value.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public async Task Metrics_CountRetriesAndDlqRouted()
    {
        var measurements = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Iverson.Events") l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, val, _, _) =>
        {
            measurements.TryGetValue(inst.Name, out var cur);
            measurements[inst.Name] = cur + val;
        });
        listener.Start();

        Task Handler(string k, string v, CancellationToken c) => throw new Exception("always");
        await BuildSut(maxAttempts: 3).DispatchAsync(Ctx(), Handler, CancellationToken.None);

        listener.Dispose();
        measurements.GetValueOrDefault("consumer.retries").Should().Be(2);
        measurements.GetValueOrDefault("consumer.dlq_routed").Should().Be(1);
    }
}
