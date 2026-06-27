# Iverson System Performance Review

**Date:** 2026-06-25
**Scope:** Full server + client stack (static code analysis — no production metrics available)
**Reviewer:** system-performance-review skill

---

## Performance Summary

No production metrics exist — no `dotnet-counters` baseline, no Jaeger latency histograms, no consumer-lag dashboards. Every finding below is code-evidence only, ranked by structural risk. The **first action** before any code change is to run `dotnet-counters monitor --process-id <pid> System.Runtime Iverson.Events` and read Jaeger spans to confirm which hot paths are actually painful at operating load. That said, two structural issues are severe enough to act on regardless of load level.

**Strengths**
- All three consumers use correctly static, reused `JsonSerializerOptions` — no per-call options object allocation.
- `SchemaRegistry._schemas` is a `ConcurrentDictionary` with `OrdinalIgnoreCase` — correct for a read-heavy, write-once registry.
- `GraphAssembler.BatchAssembleSingleAsync` collapses per-entity FK lookups into one `GetMany` gRPC call — good batching instinct that partially addresses the N+1 risk.

---

## Critical Findings

### 1. N+1 SQL round-trips in `GetMany` and relation resolution

**What:** `ObjectRetrievalGrpcService.GetMany` (`Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:55-76`) iterates `request.Keys` and fires one `QuerySingleOrDefaultAsync` per key — one SQL round-trip per key. Separately, `ObjectMappingGrpcService.ResolveManyToManyAsync` (`Iverson.Api/Grpc/ObjectMappingGrpcService.cs:257-279`) does the same for each ID in a collection relation (`foreach var id in ids → FetchByKeyAsync`).

**Why it hurts:** N keys = N connections opened, N queries, N network round-trips to Postgres, all sequential. Fetching an article with 5 tags at `depth=1` fires 6 round-trips (1 article + 5 tags). `GetMany` with 10 tag IDs fires 10 queries. Connection-pool overhead on Npgsql is low but non-zero; at concurrent load, these serialize against the pool.

**Evidence:** `ObjectRetrievalGrpcService.cs:65-67` — `QuerySingleOrDefaultAsync` inside a `foreach` over `request.Keys`. `ObjectMappingGrpcService.cs:270` — `FetchByKeyAsync` inside `foreach (var id in ids)`.

**Fix:** Replace the per-key loops with a single query using `WHERE key = ANY(@keys)`:
```csharp
// GetMany: one query, all rows
var rows = await _sql.QueryAsync<string>(
    $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{schema.KeyColumn.Name}\" = ANY(@Keys::uuid[])",
    new { Keys = request.Keys.ToArray() });
```
For `ResolveManyToManyAsync`, same pattern — collect all IDs, one `IN`/`ANY` query, then fan out results to requestors. Expected improvement: latency for a 10-tag article fetch drops from ~10 round-trips to 2 (entity + tags).

---

### 2. Double deserialization + uncached `MakeGenericMethod` in `GraphAssembler.BatchAssembleCollectionAsync`

**What:** In `Iverson.Client.Core/GraphAssembler.cs:234-238`, `DeserializeStruct` is called twice per response item — once to extract the key (to look up owners), and again inside the `foreach` for each owner entity. `DeserializeStruct` itself uses `_fromStructMethod.MakeGenericMethod(targetType).Invoke(null, [data])` (`GraphAssembler.cs:256`) — reflection `MakeGenericMethod` + `Invoke` is called on every item, not once per type.

**Why it hurts:** For 100 items in a collection, that's 200+ `Struct→POCO` deserializations via JSON round-trip (`_formatter.Format(data)` → `JsonSerializer.Deserialize`), plus 200 `MakeGenericMethod` allocations (each allocates a new `MethodInfo`). The `MakeGenericMethod` cache in the runtime is based on type arguments; calling it on every item bypasses reuse.

**Evidence:** `GraphAssembler.cs:234-238` — `DeserializeStruct` called twice per loop body. `GraphAssembler.cs:256` — `_fromStructMethod.MakeGenericMethod(targetType).Invoke(...)` not cached per type.

**Fix (two-part):**
1. Deserialize once per item, reuse:
```csharp
var deserialized = DeserializeStruct(response.Data, relation.RelatedType);
var itemKey = relatedDescriptor.KeyProperty.GetValue(deserialized)?.ToString();
if (itemKey is null || !keyToEntityIndices.TryGetValue(itemKey, out var ownerIndices)) continue;
foreach (var idx in ownerIndices)
    buckets[idx].Add(deserialized!); // reuse same object — POCOs are not shared mutable state
```
2. Cache the closed generic method per type in a `ConcurrentDictionary<Type, MethodInfo>` so `MakeGenericMethod` is called once per POCO type, not once per item.

---

## High Findings

**Kafka producer not configured for batching**
- File: `Iverson.Events/ServiceCollectionExtensions.cs:11-12`
- `ProducerConfig` has only `BootstrapServers`. Default `linger.ms=0` sends every `ProduceAsync` call immediately with no batching.
- Fix: add `LingerMs = 5, BatchSize = 65536, CompressionType = CompressionType.Lz4` to `ProducerConfig`.
- Expected: 5–20× producer throughput improvement at moderate event rates.

**`EmbeddingService.EmbedAsync` reads full response to string before parsing**
- File: `Iverson.Embeddings/EmbeddingService.cs:55-63`
- `ReadAsStringAsync` allocates a ~15KB string; `JsonDocument.Parse(string)` allocates again internally. The LINQ `.EnumerateArray().Select().ToArray()` adds an enumerator allocation on the 768-iteration float loop.
- Fix: `await response.Content.ReadAsStreamAsync(ct)` + `JsonDocument.ParseAsync(stream)` to halve allocation per embed. Replace LINQ with a pre-allocated `float[]` + `foreach`.

**Sequential embedding calls in `IntelligenceStoreConsumer`**
- File: `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:79-86, 121`
- Vector fields and chunk embeddings are all `await`-ed sequentially in `foreach` loops. Each is a separate HTTP call to Ollama.
- Fix: consider `Task.WhenAll` to pipeline embed requests. Measure first — Ollama is single-threaded for inference so gain is limited to HTTP overhead.

---

## Medium Findings

- **`ToSnakeCase()` called per-message on static data** — `IntelligenceStoreConsumer.cs:84, 114`. Property names are fixed at schema registration. Store pre-computed snake_case names in `VectorDescriptor` and `ChunkDescriptor`.

- **`ResolveColumn` linear scan per clause** — `StarRocksQueryBuilder.cs:134-137`. Creates a LINQ pipeline on every clause. Replace with a `Dictionary<string, string>` on `SchemaDescriptor` built at registration time.

- **`StarRocksRepository.UpsertAsync` rebuilds SQL strings on every call** — `StarRocksRepository.cs:81-82`. Column lists for a given schema are static. Cache `colList`/`paramList` strings keyed by `schema.TableName` in a `ConcurrentDictionary<string, string>`.

- **Aggregate N+1** (self-documented TODO at `ObjectSearchGrpcService.cs:188`) — one SQL round-trip per aggregation spec. Combine compatible aggregations (same `WHERE` clause) into one query using `GROUPING SETS` or StarRocks window functions.

---

## Recommended Next Step

**Fix the N+1 in `GetMany` and `ResolveManyToManyAsync` first.** These are O(N) round-trips that turn entity hydration from 2 queries to 2+N queries. The fix is a single SQL change per site: replace per-key `SELECT … WHERE key = @k` with `SELECT … WHERE key = ANY(@keys::uuid[])`.

**Before doing anything else**, establish a baseline:
```bash
dotnet-counters monitor --process-id $(pidof Iverson.Api) \
  System.Runtime \
  Iverson.Events
```
Watch `gc-heap-size`, `gen-0-gc-count`, `threadpool-queue-length`, and `active-timer-count` under load. This will confirm whether the code-analysis findings are live bottlenecks or whether Ollama HTTP latency is the dominant cost (in which case the embedding pipeline is the real target).
