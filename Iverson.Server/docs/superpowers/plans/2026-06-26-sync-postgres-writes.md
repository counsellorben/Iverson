# Synchronous Postgres Writes + Fire-and-Forget Kafka Fan-out

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove Postgres from the Kafka fan-out and write it synchronously in each gRPC handler; make Kafka fire-and-forget for StarRocks and Qdrant projections only; fix the `::uuid[]` cast that blocks the primary key index in `ANY()` queries.

**Architecture:** Postgres is the system of record; gRPC handlers upsert directly before returning. Kafka becomes a projection bus for eventually-consistent stores (StarRocks, Qdrant) only. `RecordStoreConsumer` is deleted. `StoreTarget.Record` is removed from the enum. A new `IEventProducer.PublishFireAndForget<T>` method wraps the Confluent callback-based `producer.Produce()` and logs delivery failures without blocking the caller.

**Tech Stack:** .NET 10, xUnit, NSubstitute, FluentAssertions, Dapper/Npgsql, Confluent.Kafka.

## Global Constraints

- All test commands run from `Iverson.Server/`
- Do NOT change method signatures on `IPostgresRepository`, `IStarRocksRepository`, or any gRPC service base class
- Do NOT change the `ObjectMappingGrpcService` constructor signature — `IPostgresRepository _sql` is already injected
- `ObjectPersistenceGrpcService` gains one new constructor parameter: `IPostgresRepository sql` (added after `IEventProducer events`, before `SchemaRegistry registry`)
- Commit after each task passes all tests
- Do not add comments explaining what code does

## File Map

| Task | Create / Modify / Delete |
|------|--------------------------|
| 1 | Modify: `Iverson.Events/IEventProducer.cs`, `Iverson.Events/KafkaProducer.cs`, `Iverson.Events.Tests/KafkaProducerTests.cs` |
| 2 | Modify: `Iverson.Events/EntityEvent.cs`, `Iverson.Api/Schema/StoreTargeting.cs`, `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`, `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Iverson.Api/Program.cs`, `Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`, `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`; Delete: `Iverson.Api/Consumers/RecordStoreConsumer.cs`, `Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs` |
| 3 | Modify: `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`, `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` |

---

### Task 1: Add `PublishFireAndForget` to `IEventProducer` and `KafkaProducer`

**Files:**
- Modify: `Iverson.Events/IEventProducer.cs`
- Modify: `Iverson.Events/KafkaProducer.cs`
- Modify: `Iverson.Events.Tests/KafkaProducerTests.cs`

**Interfaces:**
- Produces: `void IEventProducer.PublishFireAndForget<T>(string topic, string key, T message) where T : class` — used by Task 2

**Context:** `IEventProducer.ProduceAsync` is the delivery-confirming overload — it `await`s broker ACK via `producer.ProduceAsync(...)`. The new method must use `producer.Produce(...)` (the synchronous callback-based overload) which enqueues the message in the producer's internal buffer and invokes the callback asynchronously when the broker ACKs or errors. No await, no blocking the caller. The mock for `IProducer<string, string>` in `KafkaProducerTests` is an NSubstitute mock; `Produce(topic, message, callback)` is a void method and NSubstitute stubs it by default.

- [ ] **Step 1: Write the failing test**

Add to `Iverson.Events.Tests/KafkaProducerTests.cs` inside the existing `KafkaProducerTests` class:

```csharp
[Fact]
public void PublishFireAndForget_CallsProduceDotProduce_NotProduceAsync()
{
    var (producer, kafkaProducer) = CreateProducer();
    var entityEvent = new EntityEvent(
        TypeName: "Player",
        Key: "player-1",
        PayloadJson: """{"score":42}""",
        TraceId: "trace-1",
        SchemaVersion: "1.0",
        OccurredAt: DateTimeOffset.UtcNow);

    producer.PublishFireAndForget(EntityTopics.Created, entityEvent.Key, entityEvent);

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

    var producer = new KafkaProducer(kafkaProducer, NullLogger<KafkaProducer>.Instance);

    // Should not throw — errors are logged, not propagated
    var act = () => producer.PublishFireAndForget(EntityTopics.Created, "k", new { x = 1 });
    act.Should().NotThrow();
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Iverson.Events.Tests/ --filter "PublishFireAndForget" -v n
```

Expected: FAIL — `'IEventProducer' does not contain a definition for 'PublishFireAndForget'`

- [ ] **Step 3: Add `PublishFireAndForget` to `IEventProducer`**

In `Iverson.Events/IEventProducer.cs`, add the new method:

```csharp
namespace Iverson.Events;

public interface IEventProducer
{
    Task ProduceAsync<T>(string topic, string key, T message) where T : class;
    Task ProduceAsync(string topic, string key, string message);
    void PublishFireAndForget<T>(string topic, string key, T message) where T : class;
}
```

- [ ] **Step 4: Implement in `KafkaProducer`**

In `Iverson.Events/KafkaProducer.cs`, add the implementation. The trace propagation block is identical to `ProduceAsync` — copy it. Do not start an Activity span (fire-and-forget; the consumer creates its own span).

```csharp
public void PublishFireAndForget<T>(string topic, string key, T message) where T : class
{
    var json = JsonSerializer.Serialize(message);

    var headers = new Headers();
    if (Activity.Current is { } current)
        headers.Add("traceparent", System.Text.Encoding.UTF8.GetBytes(
            $"00-{current.TraceId}-{current.SpanId}-{(current.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00")}"));

    producer.Produce(topic, new Message<string, string> { Key = key, Value = json, Headers = headers },
        report =>
        {
            if (report.Error.IsError)
                logger.LogError("Kafka delivery failed topic={Topic} key={Key}: {Error}",
                    topic, key, report.Error.Reason);
        });
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test Iverson.Events.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Events/IEventProducer.cs \
        Iverson.Events/KafkaProducer.cs \
        Iverson.Events.Tests/KafkaProducerTests.cs
git commit -m "feat: add PublishFireAndForget to IEventProducer — non-blocking Kafka produce with delivery error logging"
```

---

### Task 2: Synchronous Postgres writes + fire-and-forget Kafka + remove RecordStoreConsumer

**Context for this task:**
- `RecordStoreConsumer` writes Postgres via Kafka fan-out asynchronously. After this task it is deleted; gRPC handlers write Postgres synchronously using the same `json_populate_record` upsert SQL.
- `StoreTarget.Record` is removed from the enum — no consumer reads it anymore.
- `ObjectMappingGrpcService.Delete` already does a synchronous Postgres DELETE (line 183-185). Only `Post` and `Update` in that service need the synchronous upsert added.
- `ObjectMappingGrpcService` already has `_sql` injected — no constructor change.
- `ObjectPersistenceGrpcService` gains `IPostgresRepository sql` as a second constructor parameter (after `IEventProducer events`).
- Both services contain upsert SQL identical to what `RecordStoreConsumer.UpsertAsync` did. Do not extract a shared helper.
- `PublishFireAndForget` from Task 1 is available on `IEventProducer`.
- The `/admin/reconcile` endpoint in `Program.cs` already reads from Postgres and re-publishes to Kafka for projection rebuild — its `ProduceAsync` call is intentional (delivery-confirming, admin path) and must NOT be changed.

**Files:**
- Modify: `Iverson.Events/EntityEvent.cs`
- Modify: `Iverson.Api/Schema/StoreTargeting.cs`
- Modify: `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Api/Program.cs`
- Delete: `Iverson.Api/Consumers/RecordStoreConsumer.cs`
- Delete: `Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `void IEventProducer.PublishFireAndForget<T>` from Task 1

- [ ] **Step 1: Remove `StoreTarget.Record` from the enum**

Replace `Iverson.Events/EntityEvent.cs` enum section:

```csharp
[Flags]
public enum StoreTarget
{
    None         = 0,
    Engagement   = 1 << 1,  // StarRocks — engagement read store
    Intelligence = 1 << 2,  // Qdrant — vector/chunk fields

    All = Engagement | Intelligence
}
```

- [ ] **Step 2: Update `StoreTargeting.DetermineTargetStores` to start from `None`**

In `Iverson.Api/Schema/StoreTargeting.cs`, change `var stores = StoreTarget.Record;` to `var stores = StoreTarget.None;`:

```csharp
internal static StoreTarget DetermineTargetStores(SchemaDescriptor schema)
{
    var stores = StoreTarget.None;
    if (IsCompleteForIngestion(schema)) stores |= StoreTarget.Engagement;
    if (HasVectorOrChunkFields(schema)) stores |= StoreTarget.Intelligence;
    return stores;
}
```

- [ ] **Step 3: Add synchronous upsert + fire-and-forget Kafka to `ObjectPersistenceGrpcService`**

Modify `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`. The full updated class header and the two methods follow. Add `Iverson.Sql` to the usings if not already present.

Constructor — add `IPostgresRepository sql` after `IEventProducer events`:

```csharp
public sealed class ObjectPersistenceGrpcService(
    IEventProducer events,
    IPostgresRepository sql,
    SchemaRegistry registry,
    ILogger<ObjectPersistenceGrpcService> logger)
    : ObjectPersistenceService.ObjectPersistenceServiceBase
```

Add a private upsert helper at the bottom of the class (before the `StringExtensions` file-local class):

```csharp
private async Task UpsertAsync(SchemaDescriptor schema, string payloadJson)
{
    var allCols   = schema.ScalarColumns.Select(c => c.Name).ToList();
    var updateSet = allCols.Count > 0
        ? string.Join(", ", allCols.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
        : $"\"{schema.KeyColumn.Name}\" = EXCLUDED.\"{schema.KeyColumn.Name}\"";

    await sql.ExecuteAsync(
        $"""
        INSERT INTO "{schema.TableName}"
        SELECT * FROM json_populate_record(null::"{schema.TableName}", @Json::json)
        ON CONFLICT ("{schema.KeyColumn.Name}") DO UPDATE SET {updateSet}
        """,
        new { Json = payloadJson });
}
```

Update `Post` — replace `await events.ProduceAsync(...)` block:

```csharp
await UpsertAsync(schema, payloadJson);

events.PublishFireAndForget(
    EntityTopics.Created,
    key,
    new EntityEvent(
        request.TypeName,
        key,
        payloadJson,
        request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
        SchemaVersion,
        DateTimeOffset.UtcNow,
        targetStores));
```

Update `Update` — replace `await events.ProduceAsync(...)` block:

```csharp
await UpsertAsync(schema, payloadJson);

events.PublishFireAndForget(
    EntityTopics.Updated,
    key,
    new EntityEvent(
        request.TypeName,
        key,
        payloadJson,
        request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
        SchemaVersion,
        DateTimeOffset.UtcNow,
        targetStores));
```

- [ ] **Step 4: Add synchronous upsert + fire-and-forget Kafka to `ObjectMappingGrpcService`**

In `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`:

Add a private `UpsertAsync` helper (same SQL as above, but uses `_sql`):

```csharp
private async Task UpsertAsync(SchemaDescriptor schema, string payloadJson)
{
    var allCols   = schema.ScalarColumns.Select(c => c.Name).ToList();
    var updateSet = allCols.Count > 0
        ? string.Join(", ", allCols.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
        : $"\"{schema.KeyColumn.Name}\" = EXCLUDED.\"{schema.KeyColumn.Name}\"";

    await _sql.ExecuteAsync(
        $"""
        INSERT INTO "{schema.TableName}"
        SELECT * FROM json_populate_record(null::"{schema.TableName}", @Json::json)
        ON CONFLICT ("{schema.KeyColumn.Name}") DO UPDATE SET {updateSet}
        """,
        new { Json = payloadJson });
}
```

Update `Post` — after `var payloadJson = StructSerializer.SerializePayload(request.Payload);`, replace `await _events.ProduceAsync(...)`:

```csharp
await UpsertAsync(schema, payloadJson);
var targetStores = StoreTargeting.DetermineTargetStores(schema);

_events.PublishFireAndForget(
    EntityTopics.Created,
    key,
    new EntityEvent(
        request.TypeName,
        key,
        payloadJson,
        request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
        SchemaVersion,
        DateTimeOffset.UtcNow,
        targetStores));
```

Update `Update` — same pattern after `var payloadJson = ...`:

```csharp
await UpsertAsync(schema, payloadJson);
var targetStores = StoreTargeting.DetermineTargetStores(schema);

_events.PublishFireAndForget(
    EntityTopics.Updated,
    key,
    new EntityEvent(
        request.TypeName,
        key,
        payloadJson,
        request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
        SchemaVersion,
        DateTimeOffset.UtcNow,
        targetStores));
```

Update `Delete` — the Postgres DELETE at line 183-185 is already synchronous; only change `await _events.ProduceAsync(...)` to fire-and-forget:

```csharp
_events.PublishFireAndForget(
    EntityTopics.Deleted,
    request.Key,
    new EntityEvent(
        request.TypeName,
        request.Key,
        rowJson,
        request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
        SchemaVersion,
        DateTimeOffset.UtcNow,
        StoreTargeting.DetermineTargetStores(schema)));
```

- [ ] **Step 5: Remove `RecordStoreConsumer` from `Program.cs`**

In `Iverson.Api/Program.cs`, remove line:

```csharp
builder.Services.AddHostedService<RecordStoreConsumer>();
```

The `using Iverson.Api.Consumers;` at the top stays — `EngagementStoreConsumer` and `IntelligenceStoreConsumer` still use it.

- [ ] **Step 6: Delete `RecordStoreConsumer.cs` and its test file**

```bash
rm Iverson.Api/Consumers/RecordStoreConsumer.cs
rm Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs
```

- [ ] **Step 7: Update `ObjectPersistenceGrpcServiceTests`**

Full changes to `Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`:

1. Add `_sql` to `_sut` constructor (it already exists on the class — used for `SchemaRegistry`):

```csharp
_sut = new ObjectPersistenceGrpcService(_events, _sql, _registry, NullLogger<ObjectPersistenceGrpcService>.Instance);
```

2. Replace the `CaptureEntityEvent` helper to work with `PublishFireAndForget` (void):

```csharp
private EntityEvent? CaptureFireAndForgetEvent(string topic)
{
    EntityEvent? captured = null;
    _events.When(e => e.PublishFireAndForget(topic, Arg.Any<string>(), Arg.Any<EntityEvent>()))
           .Do(call => captured = call.ArgAt<EntityEvent>(2));
    return captured; // populated after sut call — caller must read after invoking sut
}
```

Note: the returned reference is null until the sut method is called. Tests that previously used `CaptureEntityEvent()` must be rewritten to call `CaptureFireAndForgetEvent(topic)` setup before the sut call and then read the captured field after.

3. Delete `Post_AlwaysIncludesRecord_InTargetStores` (the flag no longer exists).

4. Add tests:

```csharp
[Fact]
public async Task Post_ExecutesSqlUpsert_WithPayloadJson()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
    var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });

    await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

    await _sql.Received(1).ExecuteAsync(
        Arg.Is<string>(s => s.Contains("json_populate_record")),
        Arg.Any<object?>());
}

[Fact]
public async Task Post_PublishesFireAndForget_WithEngagementTarget()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

    EntityEvent? captured = null;
    _events.When(e => e.PublishFireAndForget(EntityTopics.Created, Arg.Any<string>(), Arg.Any<EntityEvent>()))
           .Do(call => captured = call.ArgAt<EntityEvent>(2));

    var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
    await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

    captured.Should().NotBeNull();
    captured!.TargetStores.Should().Be(StoreTarget.Engagement);
}

[Fact]
public async Task Update_ExecutesSqlUpsert_WithPayloadJson()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
    var payload = MakePayload(new()
    {
        ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
        ["Name"] = Value.ForString("Alice")
    });

    await _sut.Update(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

    await _sql.Received(1).ExecuteAsync(
        Arg.Is<string>(s => s.Contains("json_populate_record")),
        Arg.Any<object?>());
}
```

5. Update `Post_IncludesEngagement_WhenSchemaIsEngagementEligible` — the `_events.ProduceAsync` capture must change to the `_events.When(...).Do(...)` pattern shown above.

6. Update `Post_ExcludesEngagement_WhenSchemaHasOneToMany` — same pattern change.

7. Update `Post_IncludesIntelligence_WhenVectorFieldsPresent` — same pattern change.

8. Update `Post_ExcludesIntelligence_WhenNoVectorFields` — same pattern change.

9. Update `Post_PayloadJson_ContainsNativeTypes_NotStringified` — same pattern change.

For each test that previously used:
```csharp
_events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
       .Returns(Task.CompletedTask);
```
Replace with:
```csharp
_events.When(e => e.PublishFireAndForget(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
       .Do(call => captured = call.ArgAt<EntityEvent>(2));
```

- [ ] **Step 8: Update `ObjectMappingGrpcServiceTests`**

In `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`:

1. Remove the `_events.ProduceAsync(...)` stub from the constructor (the void `PublishFireAndForget` doesn't need a stub):

Remove line from constructor:
```csharp
_events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>())
       .Returns(Task.CompletedTask);
```

2. Replace `CaptureKafkaEvent` helper:

```csharp
private EntityEvent? CaptureKafkaEvent(string topic)
{
    EntityEvent? captured = null;
    _events.When(e => e.PublishFireAndForget(topic, Arg.Any<string>(), Arg.Any<EntityEvent>()))
           .Do(call => captured = call.ArgAt<EntityEvent>(2));
    return captured; // populated after sut call
}
```

Note: `CaptureKafkaEvent` returns a null reference that is populated after the sut call. Tests that read `evt!.Key` must use a local variable instead; see pattern below.

3. Delete `Post_DoesNotExecuteUpsertSql` and replace with:

```csharp
[Fact]
public async Task Post_ExecutesUpsertSql_DirectlyToPostgres()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

    var payload = MakePayload(new()
    {
        ["Id"]   = Value.ForString(AuthorId),
        ["Name"] = Value.ForString("Alice")
    });
    await _sut.Post(
        new MappingWriteRequest { TypeName = "Author", Payload = payload },
        TestServerCallContext.Create());

    await _sql.Received(1).ExecuteAsync(
        Arg.Is<string>(s => s.Contains("json_populate_record")),
        Arg.Any<object?>());
}
```

4. Update all tests that use `_events.ProduceAsync(...)` capture pattern to use `_events.When(e => e.PublishFireAndForget(...)).Do(...)`. For example, `Post_WithMissingKey_GeneratesValidGuid`:

```csharp
[Fact]
public async Task Post_WithMissingKey_GeneratesValidGuid()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

    EntityEvent? evt = null;
    _events.When(e => e.PublishFireAndForget(EntityTopics.Created, Arg.Any<string>(), Arg.Any<EntityEvent>()))
           .Do(call => evt = call.ArgAt<EntityEvent>(2));

    var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
    await _sut.Post(
        new MappingWriteRequest { TypeName = "Author", Payload = payload },
        TestServerCallContext.Create());

    Guid.TryParse(evt!.Key, out var g).Should().BeTrue();
    g.Should().NotBe(Guid.Empty);
}
```

Apply the same `When...Do` substitution to: `Post_WithExistingKey_PreservesClientKey`, `Post_EmitsCreatedEvent_WithCorrectTypeName`, all Update and Delete tests that capture `EntityEvent`. Any `await _events.Received(1).ProduceAsync(...)` assertion must change to `_events.Received(1).PublishFireAndForget(...)`.

Any `await _events.DidNotReceive().ProduceAsync(...)` must change to `_events.DidNotReceive().PublishFireAndForget(...)`.

- [ ] **Step 9: Run Api tests**

```bash
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 10: Run full solution build to catch any reference to deleted `StoreTarget.Record`**

```bash
dotnet build Iverson.Server.sln
```

Expected: 0 errors, 0 warnings about `StoreTarget.Record`.

- [ ] **Step 11: Commit**

```bash
git add Iverson.Events/EntityEvent.cs \
        Iverson.Api/Schema/StoreTargeting.cs \
        Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs \
        Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Api/Program.cs \
        Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs \
        Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git rm Iverson.Api/Consumers/RecordStoreConsumer.cs \
       Iverson.Api.Tests/Consumers/RecordStoreConsumerTests.cs
git commit -m "feat: write Postgres synchronously in gRPC handlers — remove RecordStoreConsumer, make Kafka fire-and-forget for projection stores only"
```

---

### Task 3: Fix `Guid[]` UUID parameters in `ANY()` queries

**Context for this task:** `ObjectRetrievalGrpcService.GetMany` (line ~64) and `ObjectMappingGrpcService.ResolveManyToManyAsync` (line ~268) pass `string[]` as Dapper parameters. Npgsql infers `text[]`; the SQL `= ANY(@Keys::uuid[])` runtime-casts it, which prevents PostgreSQL from using the primary key index. Fix: convert strings to `Guid[]` before binding, remove the `::uuid[]` cast from SQL. Guid.Parse is safe — keys originate from `IversonKey` properties and are always valid GUIDs; invalid values would have failed schema validation upstream.

Task 2 has already modified `ObjectMappingGrpcService.cs` (Post, Update, Delete methods). This task only touches `ResolveManyToManyAsync` in that file. Do not alter the fire-and-forget changes from Task 2.

The existing test `GetMany_IssuesSingleBatchQuery_RegardlessOfKeyCount` already asserts `s.Contains("= ANY(")` — tighten it to also assert `!s.Contains("::uuid[]")`. Same for `Get_WithManyToManyRelation_IssuesSingleBatchQuery`. Delete the dead private `Contains(object param, string value)` helper in `ObjectRetrievalGrpcServiceTests` (it is no longer called after this task).

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` (lines ~63-68)
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (lines ~268-273, `ResolveManyToManyAsync` only)
- Modify: `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: no changes from Tasks 1/2 that affect these methods

- [ ] **Step 1: Tighten existing tests to assert no `::uuid[]` cast**

In `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`, update `GetMany_IssuesSingleBatchQuery_RegardlessOfKeyCount`:

```csharp
await _sql.Received(1).QueryAsync<KeyedRow>(
    Arg.Is<string>(s => s.Contains("= ANY(") && !s.Contains("::uuid[]")),
    Arg.Any<object?>());
```

Also delete the dead private helper at the bottom of that test class:

```csharp
private static bool Contains(object param, string value)
{
    var json = System.Text.Json.JsonSerializer.Serialize(param);
    return json.Contains(value);
}
```

In `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`, update `Get_WithManyToManyRelation_IssuesSingleBatchQuery`:

```csharp
await _sql.Received(1).QueryAsync<KeyedRow>(
    Arg.Is<string>(s => s.Contains("= ANY(") && !s.Contains("::uuid[]")),
    Arg.Any<object?>());
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Iverson.Api.Tests/ --filter "GetMany_IssuesSingleBatchQuery|ManyToManyRelation_IssuesSingleBatchQuery" -v n
```

Expected: FAIL — the current SQL still contains `::uuid[]`.

- [ ] **Step 3: Fix `GetMany` in `ObjectRetrievalGrpcService`**

Replace lines ~63-68 in `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`:

```csharp
var keys     = request.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
var keyGuids = keys.Select(Guid.Parse).ToArray();
var rows     = await _sql.QueryAsync<KeyedRow>(
    $"SELECT \"{schema.KeyColumn.Name}\"::text AS key, row_to_json(t)::text AS data " +
    $"FROM \"{schema.TableName}\" t " +
    $"WHERE \"{schema.KeyColumn.Name}\" = ANY(@Keys)",
    new { Keys = keyGuids });
```

- [ ] **Step 4: Fix `ResolveManyToManyAsync` in `ObjectMappingGrpcService`**

Replace lines ~268-273 in `Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (`ResolveManyToManyAsync` only):

```csharp
var idGuids = ids.Select(Guid.Parse).ToArray();
var rows    = await _sql.QueryAsync<KeyedRow>(
    $"SELECT \"{relatedSchema.KeyColumn.Name}\"::text AS key, " +
    $"row_to_json(t)::text AS data " +
    $"FROM \"{relatedSchema.TableName}\" t " +
    $"WHERE \"{relatedSchema.KeyColumn.Name}\" = ANY(@ids)",
    new { ids = idGuids });
```

- [ ] **Step 5: Run targeted tests**

```bash
dotnet test Iverson.Api.Tests/ --filter "ObjectRetrievalGrpcServiceTests" -v n
```

Expected: all pass.

```bash
dotnet test Iverson.Api.Tests/ --filter "ObjectMappingGrpcServiceTests" -v n
```

Expected: all pass.

- [ ] **Step 6: Run full suite**

```bash
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs \
        Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs \
        Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "fix: pass Guid[] to ANY() queries — remove text[]::uuid[] cast that blocked primary key index"
```

---

## Self-Review

**Spec coverage:**
- Synchronous Postgres writes in Post/Update (ObjectPersistenceGrpcService, ObjectMappingGrpcService) → Task 2 ✓
- Delete path already synchronous in ObjectMappingGrpcService; only Kafka needed to change → Task 2 ✓
- RecordStoreConsumer deleted → Task 2 ✓
- StoreTarget.Record removed from enum → Task 2 ✓
- Kafka fire-and-forget for Engagement and Intelligence stores → Task 2 ✓
- LingerMs retained (no change needed — fire-and-forget makes it harmless and useful for burst batching) ✓
- UUID Guid[] fix for GetMany and ResolveManyToMany → Task 3 ✓
- /admin/reconcile uses ProduceAsync (intentional, delivery-confirming admin path) — explicitly called out in Task 2 constraints ✓

**Placeholder scan:** No placeholders, TBDs, or "similar to above" — all code blocks are fully specified.

**Type consistency:**
- `PublishFireAndForget<T>(string topic, string key, T message)` — consistent across IEventProducer, KafkaProducer, and all call sites in Tasks 2 and 3
- `UpsertAsync(SchemaDescriptor schema, string payloadJson)` — identical signature in both gRPC services; not shared (YAGNI)
- `StoreTarget.Engagement` and `StoreTarget.Intelligence` — used consistently in all assertions and event construction after `Record` removal
