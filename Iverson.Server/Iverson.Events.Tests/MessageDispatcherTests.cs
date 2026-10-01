using System.Diagnostics.Metrics;
using System.Text;
using Confluent.Kafka;
using FluentAssertions;
using Iverson.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        _producer
            .ProduceAsync(
                Arg.Any<string>(),
                Arg.Any<Message<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeliveryResult<string, string>()));
    }

    private MessageDispatcher BuildSut(int maxAttempts = 3) =>
        new(
            _producer,
            NullLogger<MessageDispatcher>.Instance,
            new MessageDispatcherOptions { MaxAttempts = maxAttempts, Backoff = _ => TimeSpan.Zero });

    private MessageDispatcher BuildSut(Func<Exception, bool> isTransient, int maxAttempts = 3) =>
        new(
            _producer,
            NullLogger<MessageDispatcher>.Instance,
            new MessageDispatcherOptions
            {
                MaxAttempts = maxAttempts,
                Backoff     = _ => TimeSpan.Zero,
                IsTransient = isTransient,
            });

    /// <summary>The predicate the transient-path tests inject: only a TimeoutException is transient.</summary>
    private static bool OnlyTimeoutsAreTransient(Exception ex) => ex is TimeoutException;

    private static DispatchContext Ctx(string value = """{"ok":true}""") =>
        new(
            "iverson.entity.created",
            "iverson.consumer.test",
            "key-1",
            value,
            new Headers());

    [Fact]
    public async Task Success_InvokesHandlerOnce_NoDlq()
    {
        var calls = 0;
        await BuildSut()
            .DispatchAsync(
                Ctx(),
                (_, _, _) => { calls++; return Task.CompletedTask; },
                CancellationToken.None);

        calls.Should().Be(1);
        await _producer.DidNotReceive()
            .ProduceAsync(
                Arg.Any<string>(),
                Arg.Any<Message<string, string>>(),
                Arg.Any<CancellationToken>());
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

        await BuildSut()
            .DispatchAsync(
                Ctx(),
                Handler,
                CancellationToken.None);

        calls.Should().Be(2);
        await _producer.DidNotReceive()
            .ProduceAsync(
                Arg.Any<string>(),
                Arg.Any<Message<string, string>>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailure_ExhaustsAttempts_RoutesToDlqAndReturns()
    {
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c) { calls++; throw new Exception("always"); }

        await BuildSut(maxAttempts: 3).DispatchAsync(Ctx(), Handler, CancellationToken.None);

        calls.Should().Be(3);
        await _producer.Received(1)
            .ProduceAsync(
                EntityTopics.Dlq,
                Arg.Is<Message<string, string>>(m => m.Key == "key-1"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PoisonMessage_RoutesToDlqImmediately_NoRetry()
    {
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c)
        {
            calls++;
            throw new PoisonMessageException("bad json");
        }

        await BuildSut()
            .DispatchAsync(
                Ctx(),
                Handler,
                CancellationToken.None);

        calls.Should().Be(1);
        await _producer.Received(1)
            .ProduceAsync(
                EntityTopics.Dlq,
                Arg.Any<Message<string, string>>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DlqProduceFailure_Throws_DoesNotSwallow()
    {
        _producer
            .ProduceAsync(
                EntityTopics.Dlq,
                Arg.Any<Message<string, string>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("kafka down"));

        Task Handler(string k, string v, CancellationToken c) => throw new PoisonMessageException("bad");

        var act = async () => await BuildSut().DispatchAsync(Ctx(), Handler, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("kafka down");
    }

    [Fact]
    public async Task DlqMessage_CarriesMetadataHeadersAndVerbatimValue()
    {
        Message<string, string>? captured = null;
        _producer
            .ProduceAsync(
                EntityTopics.Dlq,
                Arg.Do<Message<string, string>>(m => captured = m),
                Arg.Any<CancellationToken>())
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
        await BuildSut(maxAttempts: 3)
            .DispatchAsync(
                Ctx(),
                Handler,
                CancellationToken.None);

        listener.Dispose();
        measurements.GetValueOrDefault("consumer.retries").Should().Be(2);
        measurements.GetValueOrDefault("consumer.dlq_routed").Should().Be(1);
    }

    [Fact]
    public async Task TransientFailure_ExhaustsAttempts_WithPredicate_RethrowsOriginal_NoDlq_CountsHalt()
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

        var outage = new TimeoutException("dependency down");
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c) { calls++; throw outage; }

        var act = async () => await BuildSut(OnlyTimeoutsAreTransient, maxAttempts: 3)
            .DispatchAsync(Ctx(), Handler, CancellationToken.None);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(outage);
        listener.Dispose();

        calls.Should().Be(3);
        await _producer.DidNotReceive()
            .ProduceAsync(
                Arg.Any<string>(),
                Arg.Any<Message<string, string>>(),
                Arg.Any<CancellationToken>());
        measurements.GetValueOrDefault("consumer.transient_halts").Should().Be(1);
        measurements.GetValueOrDefault("consumer.dlq_routed").Should().Be(0);
    }

    [Fact]
    public async Task TransientFailure_WithPredicate_RecoversOnSecondAttempt_DoesNotThrow_NoDlq()
    {
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c)
        {
            calls++;
            if (calls == 1) throw new TimeoutException("blip");
            return Task.CompletedTask;
        }

        var act = async () => await BuildSut(OnlyTimeoutsAreTransient)
            .DispatchAsync(Ctx(), Handler, CancellationToken.None);

        await act.Should().NotThrowAsync();
        calls.Should().Be(2);
        await _producer.DidNotReceive()
            .ProduceAsync(
                Arg.Any<string>(),
                Arg.Any<Message<string, string>>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTransientFailure_WithPredicate_ExhaustsAttempts_StillRoutesToDlqAndReturns()
    {
        var calls = 0;
        Task Handler(string k, string v, CancellationToken c) { calls++; throw new InvalidOperationException("bad message"); }

        var act = async () => await BuildSut(OnlyTimeoutsAreTransient, maxAttempts: 3)
            .DispatchAsync(Ctx(), Handler, CancellationToken.None);

        await act.Should().NotThrowAsync();
        calls.Should().Be(3);
        await _producer.Received(1)
            .ProduceAsync(
                EntityTopics.Dlq,
                Arg.Is<Message<string, string>>(m => m.Key == "key-1"),
                Arg.Any<CancellationToken>());
    }

    // ── AddKafka wiring ─────────────────────────────────────────────────────────
    // AddKafka is the only path by which the API's transient classifier reaches the dispatcher the
    // consumers run under: if it dropped the predicate, every transient dependency failure would
    // dead-letter again and every test above would still pass. These resolve the real registration
    // (production backoff, so each takes ~3 s) and live in this class rather than their own so they
    // never run in parallel with the MeterListener assertions above.

    private MessageDispatcher ResolveFromAddKafka(Func<Exception, bool>? isTransient)
    {
        var services = new ServiceCollection();
        services.AddKafka(Substitute.For<IConfiguration>(), isTransient: isTransient);
        services.AddSingleton(_producer);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services.BuildServiceProvider().GetRequiredService<MessageDispatcher>();
    }

    [Fact]
    public async Task AddKafka_WithPredicate_DispatcherHaltsOnTransientFailure_InsteadOfDeadLettering()
    {
        var act = () => ResolveFromAddKafka(OnlyTimeoutsAreTransient)
            .DispatchAsync(Ctx(), (_, _, _) => throw new TimeoutException("dependency down"), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        await _producer.DidNotReceive()
            .ProduceAsync(Arg.Any<string>(), Arg.Any<Message<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddKafka_WithoutPredicate_DispatcherStillDeadLetters()
    {
        await ResolveFromAddKafka(isTransient: null)
            .DispatchAsync(Ctx(), (_, _, _) => throw new TimeoutException("dependency down"), CancellationToken.None);

        await _producer.Received(1)
            .ProduceAsync(
                EntityTopics.Dlq,
                Arg.Is<Message<string, string>>(m => m.Key == "key-1"),
                Arg.Any<CancellationToken>());
    }
}
