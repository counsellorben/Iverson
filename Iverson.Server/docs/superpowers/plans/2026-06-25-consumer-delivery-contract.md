# Consumer Delivery Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the consumers' silent-loss / inconsistent failure handling with one shared retry → dead-letter → commit contract so no projection failure is ever dropped by an offset commit.

**Architecture:** A new `MessageDispatcher` in `Iverson.Events` owns the delivery policy (bounded retry with backoff for transient failures; immediate dead-letter for `PoisonMessageException`; dead-letter after the retry budget; never commit unless handled or safely dead-lettered). `KafkaConsumer` calls the dispatcher and commits the offset only after it returns. The three projection consumers stop swallowing exceptions and instead throw — `PoisonMessageException` for deterministic failures (malformed JSON), ordinary exceptions for transient ones.

**Tech Stack:** .NET 10, Confluent.Kafka 2.14.2, System.Diagnostics.Metrics, xunit 2.9.3, NSubstitute 5.3.0, FluentAssertions 7.0.0.

## Global Constraints

- Target framework: `net10.0`; `ImplicitUsings` enabled, `Nullable` enabled on all projects.
- Test stack: xunit 2.9.3, NSubstitute 5.3.0, FluentAssertions 7.0.0.
- Retry policy: **3 attempts**, exponential backoff **1s, 2s, 4s** (`attempt => 2^(attempt-1)` seconds); backoff must be injectable so tests run instantly.
- DLQ topic: the existing `EntityTopics.Dlq` (`"iverson.entity.dlq"`).
- DLQ message: **value = original event JSON verbatim**; metadata in **headers** (`dlq.source_topic`, `dlq.consumer_group`, `dlq.exception_type`, `dlq.exception_message` truncated to 512 chars, `dlq.attempts`, `dlq.failed_at` ISO-8601), plus the original `traceparent` header copied through.
- Commit semantics: offset committed **only** after the dispatcher returns normally (success or successful dead-letter). If the DLQ write fails, the dispatcher throws and the offset is **not** committed.
- `schema-not-found` stays a logged drop that commits (unchanged behavior).
- Metrics: `Meter "Iverson.Events"` with `Counter<long>` `consumer.retries` and `consumer.dlq_routed`. **No OTLP metrics exporter** is wired (no metrics backend in the stack). Visibility today is span tags + logs.
- Build: `dotnet build Iverson.Server/Iverson.Server.slnx`. Test: `dotnet test Iverson.Server/Iverson.Server.slnx`.

---

## File Map

**Create:**
- `Iverson.Events/PoisonMessageException.cs` — the deterministic-failure marker exception.
- `Iverson.Events/MessageDispatcher.cs` — `DispatchContext`, `MessageDispatcherOptions`, `MessageDispatcher` (the delivery contract).
- `Iverson.Events.Tests/MessageDispatcherTests.cs` — unit tests for the contract.

**Modify:**
- `Iverson.Events/Telemetry.cs` — add the `Meter` and two counters.
- `Iverson.Events/KafkaConsumer.cs` — take a `MessageDispatcher`; call it; commit only after it returns.
- `Iverson.Events/ServiceCollectionExtensions.cs` — construct and inject the dispatcher.
- `Iverson.Api/Consumers/RecordStoreConsumer.cs` — throw instead of swallow.
- `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — throw instead of swallow.
- `Iverson.Api/Consumers/EngagementStoreConsumer.cs` — throw on deserialize failure.
- `Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs` — update two tests to the new contract.
- `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — update one test to the new contract.
- `Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs` — update one test to the new contract.

---

### Task 1: `MessageDispatcher`, `PoisonMessageException`, and metrics

**Files:**
- Create: `Iverson.Server/Iverson.Events/PoisonMessageException.cs`
- Create: `Iverson.Server/Iverson.Events/MessageDispatcher.cs`
- Modify: `Iverson.Server/Iverson.Events/Telemetry.cs`
- Test: `Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs`

**Interfaces:**
- Consumes: `IProducer<string,string>` (already registered), `EntityTopics.Dlq`, `Telemetry`.
- Produces:
  - `public sealed class PoisonMessageException : Exception` — ctors `(string)`, `(string, Exception)`.
  - `public sealed record DispatchContext(string SourceTopic, string ConsumerGroup, string Key, string Value, Headers Headers)`.
  - `public sealed class MessageDispatcherOptions { int MaxAttempts {get;init;}=3; Func<int,TimeSpan> Backoff {get;init;} }`.
  - `public sealed class MessageDispatcher(IProducer<string,string> producer, ILogger<MessageDispatcher> logger, MessageDispatcherOptions? options = null)` with `Task DispatchAsync(DispatchContext ctx, Func<string,string,CancellationToken,Task> handler, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Create `Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj`
Expected: COMPILE ERROR — `PoisonMessageException`, `DispatchContext`, `MessageDispatcherOptions`, `MessageDispatcher` do not exist.

- [ ] **Step 3: Create `PoisonMessageException`**

Create `Iverson.Server/Iverson.Events/PoisonMessageException.cs`:

```csharp
namespace Iverson.Events;

/// <summary>
/// Thrown by a projection handler when a message can never succeed regardless of
/// how many times it is retried (e.g. malformed JSON, an event that deserializes
/// to null). The dispatcher routes these straight to the DLQ without retrying.
/// </summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException(string message) : base(message) { }
    public PoisonMessageException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 4: Add the meter and counters to `Telemetry`**

Replace the entire contents of `Iverson.Server/Iverson.Events/Telemetry.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Iverson.Events;

internal static class Telemetry
{
    internal const string SourceName = "Iverson.Events";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");

    internal static readonly Meter Meter = new(SourceName, "1.0.0");

    internal static readonly Counter<long> ConsumerRetries =
        Meter.CreateCounter<long>("consumer.retries", description: "Projection handler retry attempts");

    internal static readonly Counter<long> ConsumerDlqRouted =
        Meter.CreateCounter<long>("consumer.dlq_routed", description: "Messages routed to the dead-letter queue");
}
```

- [ ] **Step 5: Create `MessageDispatcher`**

Create `Iverson.Server/Iverson.Events/MessageDispatcher.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

/// <summary>Immutable context for one message being dispatched.</summary>
public sealed record DispatchContext(
    string SourceTopic,
    string ConsumerGroup,
    string Key,
    string Value,
    Headers Headers);

/// <summary>Retry/backoff knobs. Defaults: 3 attempts, exponential 1s/2s/4s.</summary>
public sealed class MessageDispatcherOptions
{
    public int MaxAttempts { get; init; } = 3;
    public Func<int, TimeSpan> Backoff { get; init; } =
        attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
}

/// <summary>
/// Runs a projection handler under the delivery contract:
///   - ordinary exception  → retry (bounded, with backoff), then dead-letter;
///   - PoisonMessageException → dead-letter immediately (no retry);
///   - success or successful dead-letter → return normally (caller commits the offset);
///   - DLQ write itself fails → throw (caller must NOT commit — halt rather than lose).
/// </summary>
public sealed class MessageDispatcher(
    IProducer<string, string> producer,
    ILogger<MessageDispatcher> logger,
    MessageDispatcherOptions? options = null)
{
    private readonly MessageDispatcherOptions _options = options ?? new MessageDispatcherOptions();

    public async Task DispatchAsync(
        DispatchContext ctx,
        Func<string, string, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await handler(ctx.Key, ctx.Value, ct);
                return;
            }
            catch (PoisonMessageException ex)
            {
                logger.LogCritical(ex,
                    "[Dispatch] Poison message topic={Topic} key={Key} — routing to DLQ",
                    ctx.SourceTopic, ctx.Key);
                await DeadLetterAsync(ctx, ex, attempt + 1, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt >= _options.MaxAttempts)
                {
                    logger.LogCritical(ex,
                        "[Dispatch] Exhausted {Max} attempts topic={Topic} key={Key} — routing to DLQ",
                        _options.MaxAttempts, ctx.SourceTopic, ctx.Key);
                    await DeadLetterAsync(ctx, ex, attempt, ct);
                    return;
                }

                Telemetry.ConsumerRetries.Add(1);
                Activity.Current?.SetTag("messaging.retry_count", attempt);
                logger.LogWarning(ex,
                    "[Dispatch] Transient failure attempt {Attempt}/{Max} topic={Topic} key={Key}",
                    attempt, _options.MaxAttempts, ctx.SourceTopic, ctx.Key);
                await Task.Delay(_options.Backoff(attempt), ct);
            }
        }
    }

    private async Task DeadLetterAsync(DispatchContext ctx, Exception ex, int attempts, CancellationToken ct)
    {
        var headers = new Headers
        {
            { "dlq.source_topic",      Encoding.UTF8.GetBytes(ctx.SourceTopic) },
            { "dlq.consumer_group",    Encoding.UTF8.GetBytes(ctx.ConsumerGroup) },
            { "dlq.exception_type",    Encoding.UTF8.GetBytes(ex.GetType().FullName ?? "Unknown") },
            { "dlq.exception_message", Encoding.UTF8.GetBytes(Truncate(ex.Message, 512)) },
            { "dlq.attempts",          Encoding.UTF8.GetBytes(attempts.ToString()) },
            { "dlq.failed_at",         Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")) },
        };

        var traceparent = ctx.Headers.FirstOrDefault(h => h.Key == "traceparent")?.GetValueBytes();
        if (traceparent is not null)
            headers.Add("traceparent", traceparent);

        try
        {
            await producer.ProduceAsync(
                EntityTopics.Dlq,
                new Message<string, string> { Key = ctx.Key, Value = ctx.Value, Headers = headers },
                ct);
        }
        catch (Exception produceEx)
        {
            logger.LogCritical(produceEx,
                "[Dispatch] FAILED to write to DLQ topic={Topic} key={Key} — offset will NOT be committed",
                ctx.SourceTopic, ctx.Key);
            throw;
        }

        Telemetry.ConsumerDlqRouted.Add(1);
        Activity.Current?.SetTag("messaging.dlq", true)
                         .SetTag("messaging.dlq.reason", ex.GetType().Name);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Events.Tests/Iverson.Events.Tests.csproj`
Expected: PASS — 7 new tests green, output pristine.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Events/PoisonMessageException.cs \
        Iverson.Server/Iverson.Events/MessageDispatcher.cs \
        Iverson.Server/Iverson.Events/Telemetry.cs \
        Iverson.Server/Iverson.Events.Tests/MessageDispatcherTests.cs
git commit -m "feat: add MessageDispatcher delivery contract (retry/DLQ) + metrics"
```

---

### Task 2: Integrate the dispatcher into `KafkaConsumer`

**Files:**
- Modify: `Iverson.Server/Iverson.Events/KafkaConsumer.cs`
- Modify: `Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `MessageDispatcher.DispatchAsync(DispatchContext, Func<string,string,CancellationToken,Task>, CancellationToken)` from Task 1.
- Produces: `KafkaConsumer(string bootstrapServers, ILogger<KafkaConsumer> logger, MessageDispatcher dispatcher)` — the new 3-arg constructor.

> No unit test: `KafkaConsumer.ConsumeAsync` wraps the real librdkafka consume loop and is the deliberately-thin shell around the dispatcher (whose logic Task 1 covers). This task is verified by build + the unchanged full suite. A reviewer gates on the wiring correctness.

- [ ] **Step 1: Replace `KafkaConsumer.cs`**

Replace the entire contents of `Iverson.Server/Iverson.Events/KafkaConsumer.cs`:

```csharp
using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

public class KafkaConsumer(
    string bootstrapServers,
    ILogger<KafkaConsumer> logger,
    MessageDispatcher dispatcher) : IEventConsumer
{
    public async Task ConsumeAsync(string topic, string groupId, Func<string, string, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        await EnsureTopicExistsAsync(topic, cancellationToken);

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);
        logger.LogInformation("Subscribed to topic {Topic} as group {GroupId}", topic, groupId);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(cancellationToken);
                if (result is null) continue;

                ActivityContext parentContext = ExtractTraceContext(result.Message.Headers);

                using var activity = Telemetry.Source.StartActivity(
                    "kafka.consume",
                    ActivityKind.Consumer,
                    parentContext);

                activity?.SetTag("messaging.system", "kafka");
                activity?.SetTag("messaging.source", topic);
                activity?.SetTag("messaging.source_kind", "topic");
                activity?.SetTag("messaging.consumer_group", groupId);
                activity?.SetTag("messaging.message_id", result.Message.Key);
                activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
                activity?.SetTag("messaging.kafka.offset", result.Offset.Value);
                activity?.SetTag("messaging.operation", "receive");

                var ctx = new DispatchContext(
                    topic, groupId, result.Message.Key, result.Message.Value, result.Message.Headers);

                try
                {
                    await dispatcher.DispatchAsync(ctx, handler, cancellationToken);
                    consumer.Commit(result);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The DLQ write itself failed — do not commit. Halt this consumer
                    // so we never advance past an uncommitted message (block rather than lose).
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.RecordException(ex);
                    logger.LogCritical(ex,
                        "[Consumer] Halting topic={Topic} group={Group} — offset not committed", topic, groupId);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Consume error on topic {Topic}", topic);
                await Task.Delay(1000, cancellationToken);
            }
        }

        consumer.Close();
    }

    private async Task EnsureTopicExistsAsync(string topic, CancellationToken ct)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
                logger.LogInformation("Created Kafka topic {Topic}", topic);
                return;
            }
            catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Kafka not ready for topic {Topic}, retrying: {Message}", topic, ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }

    private static ActivityContext ExtractTraceContext(Headers headers)
    {
        var traceparentBytes = headers.FirstOrDefault(h => h.Key == "traceparent")?.GetValueBytes();
        if (traceparentBytes is null) return default;

        try
        {
            var traceparent = System.Text.Encoding.UTF8.GetString(traceparentBytes);
            var parts = traceparent.Split('-');
            if (parts.Length < 4) return default;

            var traceId = ActivityTraceId.CreateFromString(parts[1].AsSpan());
            var spanId = ActivitySpanId.CreateFromString(parts[2].AsSpan());
            var flags = parts[3] == "01" ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;

            return new ActivityContext(traceId, spanId, flags, isRemote: true);
        }
        catch
        {
            return default;
        }
    }
}
```

- [ ] **Step 2: Wire the dispatcher in `ServiceCollectionExtensions`**

Replace the entire contents of `Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs`:

```csharp
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKafka(this IServiceCollection services, string bootstrapServers)
    {
        services.AddSingleton<IProducer<string, string>>(_ =>
            new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build());

        services.AddSingleton<IEventProducer, KafkaProducer>();

        services.AddSingleton<IEventConsumer>(sp =>
        {
            var dispatcher = new MessageDispatcher(
                sp.GetRequiredService<IProducer<string, string>>(),
                sp.GetRequiredService<ILogger<MessageDispatcher>>());
            return new KafkaConsumer(
                bootstrapServers,
                sp.GetRequiredService<ILogger<KafkaConsumer>>(),
                dispatcher);
        });

        return services;
    }
}
```

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build Iverson.Server/Iverson.Server.slnx`
Expected: build succeeds, 0 errors.

Run: `dotnet test Iverson.Server/Iverson.Server.slnx`
Expected: all existing tests still pass (the consumer tests still assert the old swallow behavior until Tasks 3–5 — they should remain green here because the consumers are unchanged in this task).

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/Iverson.Events/KafkaConsumer.cs \
        Iverson.Server/Iverson.Events/ServiceCollectionExtensions.cs
git commit -m "feat: route consumer messages through MessageDispatcher; commit only after handled/dead-lettered"
```

---

### Task 3: `RecordStoreConsumer` conforms to the contract

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/RecordStoreConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs`

**Interfaces:**
- Consumes: `PoisonMessageException` from Task 1.

- [ ] **Step 1: Update the two contradicting tests (write the new expectations first)**

In `Iverson.Server/Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs`, ensure `using Iverson.Events;` is present at the top (add it if missing).

Replace the test method `HandlesMalformedJson_WithoutThrowing` (currently around line 189-198) with:

```csharp
    [Fact]
    public async Task HandlesMalformedJson_ThrowsPoisonMessageException()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync("some-key", "NOT_VALID_JSON{{{{", CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
    }
```

Replace the test method `HandlesSqlException_DoesNotPropagate` (currently around line 200-223) with:

```csharp
    [Fact]
    public async Task SqlException_Propagates()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _sql.ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>())
            .Returns<int>(_ => throw new Exception("DB connection failed"));

        var ev = new EntityEvent(
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"name":"Alice"}""",
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record);

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("DB connection failed");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~RecordStoreConsumerTests"`
Expected: FAIL — `HandlesMalformedJson_ThrowsPoisonMessageException` fails (consumer currently swallows and returns), and `SqlException_Propagates` fails (consumer currently swallows).

- [ ] **Step 3: Update `RecordStoreConsumer` to throw**

In `Iverson.Server/Iverson.Api/Consumers/RecordStoreConsumer.cs`:

Add `using Iverson.Events;` if not already present (it is — confirm).

Replace `HandleAsync` (currently lines ~30-66) with:

```csharp
    internal async Task HandleAsync(string key, string value, CancellationToken ct)
    {
        var entityEvent = Deserialize(key, value);
        if (!entityEvent.TargetStores.HasFlag(StoreTarget.Record)) return;

        var schema = registry.Get(entityEvent.TypeName);
        if (schema is null)
        {
            logger.LogError(
                "[Record] Dropped event — no schema registered for type={Type} key={Key}. " +
                "Call RegisterSchema before producing events for this type.",
                entityEvent.TypeName, key);
            Activity.Current?.SetTag("dropped_event", true)
                             .SetTag("dropped_event.reason", "schema_not_found")
                             .SetTag("dropped_event.type", entityEvent.TypeName);
            return;
        }

        await UpsertAsync(schema, entityEvent.PayloadJson);
    }
```

Replace `HandleDeleteAsync` (currently lines ~68-108) with:

```csharp
    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var entityEvent = Deserialize(key, value);
        if (!entityEvent.TargetStores.HasFlag(StoreTarget.Record)) return;

        var schema = registry.Get(entityEvent.TypeName);
        if (schema is null)
        {
            logger.LogError(
                "[Record] Dropped event — no schema registered for type={Type} key={Key}. " +
                "Call RegisterSchema before producing events for this type.",
                entityEvent.TypeName, key);
            Activity.Current?.SetTag("dropped_event", true)
                             .SetTag("dropped_event.reason", "schema_not_found")
                             .SetTag("dropped_event.type", entityEvent.TypeName);
            return;
        }

        await sql.ExecuteAsync(
            $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = entityEvent.Key });

        logger.LogInformation("[Record] Deleted {Type}:{Key}", entityEvent.TypeName, entityEvent.Key);
    }
```

Replace the `Deserialize` method (currently lines ~130-... returning `EntityEvent?`) with:

```csharp
    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? entityEvent;
        try
        {
            entityEvent = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Record] Malformed event JSON key={key}", ex);
        }

        return entityEvent ?? throw new PoisonMessageException($"[Record] Event deserialized to null key={key}");
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~RecordStoreConsumerTests"`
Expected: PASS — all RecordStoreConsumer tests green.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/RecordStoreConsumer.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs
git commit -m "refactor: RecordStoreConsumer throws on failure (delivery contract)"
```

---

### Task 4: `IntelligenceStoreConsumer` conforms to the contract

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

**Interfaces:**
- Consumes: `PoisonMessageException` from Task 1.

- [ ] **Step 1: Update the contradicting test**

In `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`, ensure `using Iverson.Events;` is present (add if missing).

Replace the test method `EmbedFailure_DoesNotPropagate` (currently around line 185-207) with:

```csharp
    [Fact]
    public async Task EmbedFailure_Propagates()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns<float[]>(_ => throw new Exception("Ollama timeout"));

        var payload = """{"Title":"Test Title","Body":"Some body","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-6",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Record | StoreTarget.Intelligence);

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("Ollama timeout");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~IntelligenceStoreConsumerTests"`
Expected: FAIL — `EmbedFailure_Propagates` fails because the consumer currently catches the embed exception.

- [ ] **Step 3: Update `IntelligenceStoreConsumer` to throw**

In `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`, add `using Iverson.Events;` (it is present — confirm).

Replace the start of `HandleAsync` — the deserialize/flag guard and the payload-parse block (currently lines ~44-71) — with:

```csharp
    internal async Task HandleAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Intelligence)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null || schema.CollectionName is null)
        {
            logger.LogError(
                "[Intelligence] Dropped event — no schema registered for type={Type} key={Key}.",
                ev.TypeName, key);
            Activity.Current?.SetTag("dropped_event", true)
                             .SetTag("dropped_event.reason", "schema_not_found")
                             .SetTag("dropped_event.type", ev.TypeName);
            return;
        }

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(ev.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Intelligence] Malformed payload JSON type={ev.TypeName} key={key}", ex);
        }
```

Within the named-vector loop, replace the per-field embed `try/catch` (currently lines ~85-93) with the bare call:

```csharp
                namedVectors[$"{vf.PropertyName.ToSnakeCase()}_vector"] =
                    await embedding.EmbedAsync(text, ct);
```

Within the chunk loop, replace the per-chunk `try/catch` (currently lines ~129-149) with the bare body:

```csharp
                    var chunkVector = await embedding.EmbedAsync(chunkText, ct);
                    var chunkId     = ComputeChunkPointId(pointId, cf.PropertyName, chunkIndex);

                    await vector.UpsertNamedAsync(
                        chunksCollection,
                        chunkId,
                        new Dictionary<string, float[]> { [vectorName] = chunkVector },
                        new Dictionary<string, string>
                        {
                            ["text"]        = chunkText,
                            ["parent_id"]   = ev.Key,
                            ["field"]       = cf.PropertyName,
                            ["chunk_index"] = chunkIndex.ToString()
                        });
```

Replace the start of `HandleDeleteAsync` — the deserialize/flag guard (currently lines ~158-161) — with:

```csharp
    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Intelligence)) return;
```

Replace the delete `try/catch` (currently lines ~177-184) with the bare call:

```csharp
        await vector.DeleteAsync(schema.CollectionName, pointId);
```

Replace the `Deserialize` method (currently lines ~262-273 returning `EntityEvent?`) with:

```csharp
    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Intelligence] Malformed event JSON key={key}", ex);
        }

        return ev ?? throw new PoisonMessageException($"[Intelligence] Event deserialized to null key={key}");
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~IntelligenceStoreConsumerTests"`
Expected: PASS — all IntelligenceStoreConsumer tests green.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "refactor: IntelligenceStoreConsumer throws on failure (delivery contract)"
```

---

### Task 5: `EngagementStoreConsumer` conforms to the contract

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs`

**Interfaces:**
- Consumes: `PoisonMessageException` from Task 1.

- [ ] **Step 1: Update the contradicting test**

In `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs`, ensure `using Iverson.Events;` is present (it is — `StoreTarget` is used; confirm).

Replace the test method `HandlesMalformedJson_WithoutThrowing` (currently around line 107-116) with:

```csharp
    [Fact]
    public async Task HandlesMalformedJson_ThrowsPoisonMessageException()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var act = async () =>
            await BuildSut().HandleUpsertAsync("some-key", "NOT_VALID_JSON{{{", CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~EngagementStoreConsumerTests"`
Expected: FAIL — `HandlesMalformedJson_ThrowsPoisonMessageException` fails because the consumer currently catches and returns.

- [ ] **Step 3: Update `EngagementStoreConsumer` to throw on deserialize failure**

In `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs`:

Replace the `HandleUpsertAsync` guard line:

```csharp
        var ev = Deserialize(key, value);
        if (ev is null || !ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;
```

with:

```csharp
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;
```

Replace the `HandleDeleteAsync` guard line (identical text):

```csharp
        var ev = Deserialize(key, value);
        if (ev is null || !ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;
```

with:

```csharp
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;
```

Replace the `Deserialize` method (currently returning `EntityEvent?` with a catch that returns null) with:

```csharp
    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Engagement] Malformed event JSON key={key}", ex);
        }

        return ev ?? throw new PoisonMessageException($"[Engagement] Event deserialized to null key={key}");
    }
```

Add `using Iverson.Events;` at the top if not already present (it is — `StoreTarget` is used; confirm).

- [ ] **Step 4: Run the full Api test suite to verify**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj`
Expected: PASS — all Api tests green, including the updated EngagementStoreConsumer tests.

- [ ] **Step 5: Run the whole solution to confirm nothing regressed**

Run: `dotnet test Iverson.Server/Iverson.Server.slnx`
Expected: PASS — all suites green, output pristine (pre-existing unrelated warnings excepted).

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs
git commit -m "refactor: EngagementStoreConsumer throws on deserialize failure (delivery contract)"
```

---

## Self-Review

**Spec coverage:**
- §1 contract in shared loop → Tasks 1 (dispatcher) + 2 (KafkaConsumer integration). ✅
- §2 two failure classes (transient retry / poison immediate) → Task 1 (`DispatchAsync` switch on `PoisonMessageException` vs `Exception`). ✅
- §2 `schema-not-found` stays a logged drop → Tasks 3/4 keep the drop+return path. ✅
- §3 commit semantics (commit only after return; DLQ-failure → no commit) → Task 1 (throw on DLQ failure) + Task 2 (commit after `DispatchAsync`, `break` without commit on throw). ✅
- §4 DLQ message shape (verbatim value, metadata headers, traceparent) → Task 1 `DeadLetterAsync` + test `DlqMessage_CarriesMetadataHeadersAndVerbatimValue`. ✅
- §5 consumer edits (Record/Intelligence throw + per-field propagation; Engagement deserialize) → Tasks 3/4/5. ✅
- §6 observability (Meter + 2 counters, span tags, logs; no OTLP metrics exporter) → Task 1 (`Telemetry`, counters, span tags, logs); no Program.cs change by design. ✅
- §7 testing (extracted testable unit; loop is thin shell; named test cases) → Task 1 tests cover all seven listed cases; Task 2 documents why the loop has no unit test. ✅

**Placeholder scan:** none — every code/test step shows full content; every run step shows command + expected result.

**Type consistency:** `MessageDispatcher`, `DispatchContext`, `MessageDispatcherOptions`, `PoisonMessageException`, `Telemetry.ConsumerRetries`, `Telemetry.ConsumerDlqRouted`, `EntityTopics.Dlq` are defined in Task 1 and referenced with identical signatures in Tasks 2–5. `KafkaConsumer`'s new 3-arg constructor (Task 2) matches the `AddKafka` construction (Task 2). The consumers' new `private static EntityEvent Deserialize(string, string)` signature is consistent across Tasks 3/4/5.

**Note for the executor:** Tasks 3, 4, and 5 each make a consumer throw. Until its own task runs, that consumer's *unmodified* tests still pass under Task 2 (the dispatcher wraps but does not change a swallowing handler's observable return). Execute tasks in order; do not run a consumer's source change without its paired test change in the same task.
