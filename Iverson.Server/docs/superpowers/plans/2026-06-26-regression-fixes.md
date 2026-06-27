# Regression Fix Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore write-path and small-batch GetMany latency to at-or-below baseline after two regressions introduced by the 2026-06-26 performance fix plan.

**Background:** Load test run at 22:18 (post-fix) versus 19:39 (baseline) revealed:
- Write path p50: 57ms → 106ms (+85%), RPS: 128 → 101 (−21%)
- GetMany batch=1 p50: 5.2ms → 18.7ms (3.6×), p95: 14.4ms → 93.1ms (6.5×)
- GetMany batch=10 p50: 21.8ms → 26.8ms (+23%)
- GetMany batch=100/500: ✅ improved as intended (N+1 fix working correctly)
- Aggregate specs=6: ✅ improved as intended

**Root causes confirmed:**

1. **`LingerMs=5`** — `KafkaProducer.ProduceAsync` is the delivery-confirming overload; it `await`s broker ACK before returning. `LingerMs=5` holds each message up to 5ms waiting for the batch to fill. At the load test's ~101 RPS with 4KB messages, the 64KB batch never fills consistently, so most writes pay the full linger penalty. Write path p50 increase is directly traceable here. The p99 improvement (2050ms → 817ms) is a genuine win from `BatchSize`+`CompressionType` and must be preserved.

2. **`::uuid[]` text cast in `ANY` queries** — `GetMany` and `ResolveManyToManyAsync` pass `string[]` as the Dapper parameter, which Npgsql sends as `text[]`. The `::uuid[]` cast in the SQL then coerces the array at query time. PostgreSQL cannot push the index condition through a runtime cast expression, so it falls back to a sequential scan (or a costly recheck plan) for small arrays. For batch=1 the old code used `QuerySingleOrDefaultAsync<string>` with a direct `= @Key::uuid` equality — a single typed parameter that always uses the primary key index.

**Architecture:** Two targeted fixes, no new types or abstractions. `BatchSize` and `CompressionType=Lz4` on the Kafka producer are retained.

**Tech Stack:** .NET 10, xUnit, NSubstitute, FluentAssertions, Dapper/Npgsql, Confluent.Kafka.

## Global Constraints

- All test commands run from `Iverson.Server/`
- Do NOT change method signatures on `IPostgresRepository`, `IStarRocksRepository`, or any gRPC service base class
- Commit after each task passes all tests
- Do not add comments explaining what code does

## Baseline vs Post-Fix Numbers (reference)

| Metric | Baseline 19:39 | Post-fix 22:18 | Target |
|--------|---------------|----------------|--------|
| Write p50 | 57 ms | 106 ms | ≤60 ms |
| Write RPS | 128 | 101 | ≥120 |
| GetMany b=1 p50 | 5.2 ms | 18.7 ms | ≤7 ms |
| GetMany b=1 p95 | 14.4 ms | 93.1 ms | ≤20 ms |
| GetMany b=100 p50 | 101 ms | 36 ms | ≤40 ms (retain) |
| GetMany b=500 p50 | 486 ms | 122 ms | ≤130 ms (retain) |
| Write p99 | 2050 ms | 817 ms | ≤900 ms (retain) |

## File Map

| Task | Modify |
|------|--------|
| 1 | `Iverson.Events/ServiceCollectionExtensions.cs` |
| 2 | `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`, `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` |

---

### Task 1: Remove LingerMs — restore write-path latency floor

**Impact:** Removes the up-to-5ms per-write linger penalty. `BatchSize` and `CompressionType=Lz4` are retained; they batch messages that arrive within the same poll cycle without imposing a latency floor.

**Context:** `Iverson.Events/ServiceCollectionExtensions.cs` lines 13–17. The producer is registered as a singleton and used by `KafkaProducer.ProduceAsync`, which `await`s `producer.ProduceAsync(...)` — the delivery-confirming overload — before returning to the gRPC handler. `LingerMs` is appropriate for fire-and-forget background pipelines, not for synchronous request/response paths.

**Files:**
- Modify: `Iverson.Events/ServiceCollectionExtensions.cs`

No unit test covers `ProducerConfig` construction. Run `Iverson.Events.Tests/` as a smoke check; functional verification is the load test.

- [ ] **Step 1: Remove LingerMs**

In `Iverson.Events/ServiceCollectionExtensions.cs`, remove the `LingerMs` line:

```csharp
// Before:
services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        LingerMs         = 5,
        BatchSize        = 65536,
        CompressionType  = CompressionType.Lz4
    }).Build());

// After:
services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        BatchSize        = 65536,
        CompressionType  = CompressionType.Lz4
    }).Build());
```

- [ ] **Step 2: Run Events tests**

```bash
dotnet test Iverson.Events.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 3: Commit**

```bash
git add Iverson.Events/ServiceCollectionExtensions.cs
git commit -m "fix: remove LingerMs from Kafka producer — restore write-path latency for synchronous delivery-confirmed sends"
```

---

### Task 2: Fix UUID array parameters — restore GetMany index usage

**Impact:** Eliminates the `text[]` → `uuid[]` cast that prevents PostgreSQL from using the primary key index in `ANY` queries. Restores GetMany batch=1 p50 to baseline (~5ms). Large-batch improvements from Task 1 of the performance plan are fully preserved.

**Context:** Both `ObjectRetrievalGrpcService.GetMany` (line 64–68) and `ObjectMappingGrpcService.ResolveManyToManyAsync` (line 268–273) pass `string[]` as the Dapper `@Keys`/`@ids` parameter. Npgsql infers the PostgreSQL type as `text[]`. The SQL then casts it at runtime: `= ANY(@Keys::uuid[])`. PostgreSQL's planner cannot use a B-tree index on the `uuid` column through a runtime-cast expression — it switches to a sequential scan or a suboptimal plan, which is fast for batch=500 (covering many rows) but catastrophic for batch=1 (sequential scan on 400K rows to return 1 result).

The fix: convert string keys to `Guid[]` before building the Dapper parameter. Npgsql sends `Guid[]` as native PostgreSQL `uuid[]`. The SQL becomes `= ANY(@Keys)` with no cast. The planner sees a `uuid[] = uuid` comparison and uses the primary key index.

The single-key `Get` method (line 30–31) is not affected — it already uses `@Key::uuid` with a single typed parameter and the correct index plan.

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` (lines 63–68)
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (lines 260–273)
- Modify: `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

- [ ] **Step 1: Fix GetMany in ObjectRetrievalGrpcService**

Replace lines 63–68 in `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`:

```csharp
// Before:
var keys = request.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
var rows = await _sql.QueryAsync<KeyedRow>(
    $"SELECT \"{schema.KeyColumn.Name}\"::text AS key, row_to_json(t)::text AS data " +
    $"FROM \"{schema.TableName}\" t " +
    $"WHERE \"{schema.KeyColumn.Name}\" = ANY(@Keys::uuid[])",
    new { Keys = keys });

// After:
var keys     = request.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
var keyGuids = keys.Select(Guid.Parse).ToArray();
var rows     = await _sql.QueryAsync<KeyedRow>(
    $"SELECT \"{schema.KeyColumn.Name}\"::text AS key, row_to_json(t)::text AS data " +
    $"FROM \"{schema.TableName}\" t " +
    $"WHERE \"{schema.KeyColumn.Name}\" = ANY(@Keys)",
    new { Keys = keyGuids });
```

`Guid.Parse` is safe here: keys originate from `IversonKey` properties which are always `Guid`; any non-UUID value passed by a client would have already failed schema validation upstream.

- [ ] **Step 2: Fix ResolveManyToManyAsync in ObjectMappingGrpcService**

Replace lines 268–273 in `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`:

```csharp
// Before:
var rows = await _sql.QueryAsync<KeyedRow>(
    $"SELECT \"{relatedSchema.KeyColumn.Name}\"::text AS key, " +
    $"row_to_json(t)::text AS data " +
    $"FROM \"{relatedSchema.TableName}\" t " +
    $"WHERE \"{relatedSchema.KeyColumn.Name}\" = ANY(@ids::uuid[])",
    new { ids = ids.ToArray() });

// After:
var idGuids = ids.Select(Guid.Parse).ToArray();
var rows    = await _sql.QueryAsync<KeyedRow>(
    $"SELECT \"{relatedSchema.KeyColumn.Name}\"::text AS key, " +
    $"row_to_json(t)::text AS data " +
    $"FROM \"{relatedSchema.TableName}\" t " +
    $"WHERE \"{relatedSchema.KeyColumn.Name}\" = ANY(@ids)",
    new { ids = idGuids });
```

- [ ] **Step 3: Update GetMany regression test assertion**

In `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`, tighten the `GetMany_IssuesSingleBatchQuery_RegardlessOfKeyCount` assertion to guard against the cast returning:

```csharp
await _sql.Received(1).QueryAsync<KeyedRow>(
    Arg.Is<string>(s => s.Contains("= ANY(") && !s.Contains("::uuid[]")),
    Arg.Any<object?>());
```

Also delete the dead `Contains(object param, string value)` private static helper that was identified in the Task 1 review (it is no longer called by any test).

- [ ] **Step 4: Update ManyToMany regression test assertion**

In `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`, tighten the `Get_WithManyToManyRelation_IssuesSingleBatchQuery` assertion:

```csharp
await _sql.Received(1).QueryAsync<KeyedRow>(
    Arg.Is<string>(s => s.Contains("= ANY(") && !s.Contains("::uuid[]")),
    Arg.Any<object?>());
```

- [ ] **Step 5: Run ObjectRetrievalGrpcServiceTests**

```bash
dotnet test Iverson.Api.Tests/ --filter "ObjectRetrievalGrpcServiceTests" -v n
```

Expected: all pass.

- [ ] **Step 6: Run ObjectMappingGrpcServiceTests**

```bash
dotnet test Iverson.Api.Tests/ --filter "ObjectMappingGrpcServiceTests" -v n
```

Expected: all pass.

- [ ] **Step 7: Run full suite**

```bash
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs \
        Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs \
        Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "fix: pass Guid[] to ANY() queries — remove text[]::uuid[] cast that blocked primary key index"
```

---

## Self-Review

**Root cause coverage:**
- Write path p50/p95/RPS regression (LingerMs=5) → Task 1 ✓
- GetMany batch=1/10 p50/p95 regression (text[]::uuid[] cast) → Task 2 ✓
- GetMany batch=100/500 improvement preserved (ANY() query retained, only cast removed) ✓
- Write p99 improvement preserved (BatchSize + CompressionType retained) ✓
- Aggregate specs=6 improvement unaffected (separate code path) ✓

**Out of scope:**
- Kafka lag increase — EngagementStoreConsumer (StarRocks) throughput is a pre-existing ceiling unaffected by either regression fix; addressed separately if load test confirms it limits write RPS.
- `ExtractString` double-call in IntelligenceStoreConsumer — pre-existing, not a regression; BenchmarkArticle has no embedding fields so it does not appear in load test numbers.
- `ResolveFkProperty` / `InferForeignKey` duplication — code quality, not performance.
