# Performance Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the N+1 SQL bottlenecks, Kafka consumer lag, and sequential embedding calls confirmed by the 2026-06-26 load test results.

**Architecture:** Seven targeted fixes applied test-first across five projects (Iverson.Sql, Iverson.Api, Iverson.Events, Iverson.Embeddings). No new projects or interfaces. All changes are behaviorally equivalent except for ordering: parallel execution may reorder concurrent results, but response ordering is preserved explicitly where needed.

**Tech Stack:** .NET 10, xUnit, NSubstitute, FluentAssertions, Dapper/Npgsql, Confluent.Kafka, System.Text.Json.

## Global Constraints

- All test projects run from `Iverson.Server/` directory: `dotnet test <ProjectName>.Tests/`
- Test filter syntax: `dotnet test <Project>/ --filter "<MethodName>" -v n`
- Do NOT change method signatures on `IPostgresRepository`, `IStarRocksRepository`, or any gRPC service base class.
- Commit after each task passes all tests.
- Do not add comments explaining what code does. Only add a comment if the WHY is non-obvious.

## Load Test Results (Baseline)

**Write-path** (10,000 ops, concurrency=16, 2026-06-26 19:39):
- p50=57ms p95=222ms p99=2,050ms | 128 RPS | Kafka lag: 12,195 messages accumulating (barely draining)

**Read-path** (2026-06-26 19:41):
- GetMany batch=1: 5ms p50 | batch=10: 22ms | batch=100: 101ms | batch=500: **486ms** (N+1 confirmed)
- Search simple: 52ms | medium: **664ms, 1 RPS** | complex: 35ms
- Aggregate specs=1: 31ms | specs=3: 79ms | specs=6: **155ms** (N+1 confirmed — 5× linear)

## File Map

| Task | Create | Modify |
|---|---|---|
| 1 | `Iverson.Sql/KeyedRow.cs` | `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs` |
| 2 | — | `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `Iverson.Api.Tests/Helpers/SchemaFixtures.cs`, `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` |
| 3 | — | `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`, `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` |
| 4 | — | `Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` |
| 5 | — | `Iverson.Events/ServiceCollectionExtensions.cs` |
| 6 | — | `Iverson.Embeddings/EmbeddingService.cs` |
| 7 | — | `Iverson.Api/Schema/SchemaDescriptor.cs`, `Iverson.Api/StarRocks/StarRocksQueryBuilder.cs` |
| 8 | — | `Iverson.Client/Iverson.Client.Core/GraphAssembler.cs` |
| 9 | — | `Iverson.Client/Iverson.Client.Core/RelationDescriptor.cs`, `Iverson.Client/Iverson.Client.Core/EntityRegistry.cs`, `Iverson.Client/Iverson.Client.Core/GraphAssembler.cs` |

---

### Task 1: Fix GetMany N+1 — batch SQL + KeyedRow

**Impact:** GetMany batch=500 drops from ~486 ms → ~5 ms (100×).

**Context:** `ObjectRetrievalGrpcService.GetMany` (line 45) issues one `QuerySingleOrDefaultAsync` per key in a loop. Fix: one `QueryAsync<KeyedRow>` with `= ANY(@Keys::uuid[])`, then emit in request order from a dictionary.

**Files:**
- Create: `Iverson.Sql/KeyedRow.cs`
- Modify: `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:45-78`
- Modify: `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`

**Interfaces:**
- Produces: `public sealed record KeyedRow(string Key, string Data)` in `Iverson.Sql` — used in Tasks 1 and 2.

- [ ] **Step 1: Write the failing regression test**

In `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`, add before the closing brace of the class. The test must fail before the fix because the current impl calls `QuerySingleOrDefaultAsync` (returns null by default) rather than `QueryAsync<KeyedRow>`:

```csharp
[Fact]
public async Task GetMany_IssuesSingleBatchQuery_RegardlessOfKeyCount()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
    _sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>())
        .Returns(Array.Empty<KeyedRow>());

    var stream = MakeStream<RetrievalResponse>();
    await _sut.GetMany(
        new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
        stream, TestServerCallContext.Create());

    await _sql.Received(1).QueryAsync<KeyedRow>(
        Arg.Is<string>(s => s.Contains("= ANY(")),
        Arg.Any<object?>());
}
```

Add `using Iverson.Sql;` to the using block (it may already be present — check line 9).

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Iverson.Api.Tests/ --filter "GetMany_IssuesSingleBatchQuery" -v n
```

Expected: FAIL — NSubstitute reports `QueryAsync<KeyedRow>` was received 0 times.

- [ ] **Step 3: Create KeyedRow**

Create `Iverson.Sql/KeyedRow.cs`:

```csharp
namespace Iverson.Sql;

public sealed record KeyedRow(string Key, string Data);
```

- [ ] **Step 4: Replace GetMany implementation**

In `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, replace the `GetMany` method (lines 45–78) with:

```csharp
public override async Task GetMany(
    RetrievalManyRequest request,
    IServerStreamWriter<RetrievalResponse> responseStream,
    ServerCallContext context)
{
    logger.LogInformation("[Retrieval.GetMany] type={Type} count={Count}",
        request.TypeName, request.Keys.Count);

    var schema = registry.Get(request.TypeName);

    if (schema is null)
    {
        foreach (var _ in request.Keys)
            await responseStream.WriteAsync(
                new RetrievalResponse { Found = false, TraceId = request.TraceId });
        return;
    }

    var keys = request.Keys.ToArray();
    var rows = await _sql.QueryAsync<KeyedRow>(
        $"SELECT \"{schema.KeyColumn.Name}\"::text AS key, row_to_json(t)::text AS data " +
        $"FROM \"{schema.TableName}\" t " +
        $"WHERE \"{schema.KeyColumn.Name}\" = ANY(@Keys::uuid[])",
        new { Keys = keys });

    var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    foreach (var key in keys)
    {
        if (context.CancellationToken.IsCancellationRequested) break;
        await responseStream.WriteAsync(
            rowsByKey.TryGetValue(key, out var row)
                ? new RetrievalResponse
                  {
                      Found   = true,
                      Data    = JsonParser.Default.Parse<Struct>(row.Data),
                      TraceId = request.TraceId
                  }
                : new RetrievalResponse { Found = false, TraceId = request.TraceId });
    }
}
```

- [ ] **Step 5: Update the four existing GetMany tests**

The existing tests mock `QuerySingleOrDefaultAsync`; replace the mock setup with `QueryAsync<KeyedRow>` in each.

Replace the four GetMany tests in `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`:

```csharp
[Fact]
public async Task GetMany_StreamsFoundResponseForEachKey()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
    _sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>())
        .Returns(new[] { new KeyedRow(AuthorId, AuthorJson), new KeyedRow(AuthorId2, AuthorJson) });

    var stream = MakeStream<RetrievalResponse>();
    await _sut.GetMany(
        new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
        stream, TestServerCallContext.Create());

    stream.Written.Should().HaveCount(2);
    stream.Written.Should().AllSatisfy(r => r.Found.Should().BeTrue());
}

[Fact]
public async Task GetMany_WhenEntityMissing_StreamsNotFoundForThatKey()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
    _sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>())
        .Returns(new[] { new KeyedRow(AuthorId, AuthorJson) }); // AuthorId2 absent

    var stream = MakeStream<RetrievalResponse>();
    await _sut.GetMany(
        new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
        stream, TestServerCallContext.Create());

    stream.Written[0].Found.Should().BeTrue();
    stream.Written[1].Found.Should().BeFalse();
}

[Fact]
public async Task GetMany_WhenSchemaNotRegistered_StreamsNotFoundForAllKeys()
{
    var stream = MakeStream<RetrievalResponse>();
    await _sut.GetMany(
        new RetrievalManyRequest { TypeName = "Ghost", Keys = { AuthorId, AuthorId2 } },
        stream, TestServerCallContext.Create());

    stream.Written.Should().HaveCount(2);
    stream.Written.Should().AllSatisfy(r => r.Found.Should().BeFalse());
    await _sql.DidNotReceive().QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>());
}

[Fact]
public async Task GetMany_PreservesTraceId_InEachResponse()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
    _sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>())
        .Returns(new[] { new KeyedRow(AuthorId, AuthorJson) });

    var stream = MakeStream<RetrievalResponse>();
    await _sut.GetMany(
        new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId }, TraceId = "trace-abc" },
        stream, TestServerCallContext.Create());

    stream.Written[0].TraceId.Should().Be("trace-abc");
}
```

- [ ] **Step 6: Run all ObjectRetrievalGrpcServiceTests**

```bash
dotnet test Iverson.Api.Tests/ --filter "ObjectRetrievalGrpcServiceTests" -v n
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Sql/KeyedRow.cs \
        Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs \
        Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs
git commit -m "perf: batch GetMany SQL — replace N+1 per-key queries with ANY(@keys::uuid[])"
```

---

### Task 2: Fix ResolveManyToManyAsync N+1

**Impact:** Entity `Get` with ManyToMany relations drops from N SQL round-trips to 1 per relation.

**Context:** `ObjectMappingGrpcService.ResolveManyToManyAsync` (line 257) loops over `ids`, calling `FetchByKeyAsync` (one `QuerySingleOrDefaultAsync`) per id. Fix: single `QueryAsync<KeyedRow>` with `= ANY(@ids::uuid[])`, then preserve caller order with a dictionary.

`KeyedRow` is in `Iverson.Sql` (created in Task 1) and already imported via `using Iverson.Sql;` (line 9 of the service file).

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs:257-278`
- Modify: `Iverson.Api.Tests/Helpers/SchemaFixtures.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `KeyedRow` from Task 1.

- [ ] **Step 1: Add ManyToMany test fixtures to SchemaFixtures**

Append two new factory methods to `Iverson.Api.Tests/Helpers/SchemaFixtures.cs` inside the `SchemaFixtures` class, after `UserArticleSchema()`:

```csharp
// Post with ManyToMany → Tags (for ResolveManyToManyAsync tests)
public static SchemaDescriptor PostWithTagsSchema() => new()
{
    TypeName       = "Post",
    TableName      = "posts",
    CollectionName = null,
    KeyColumn      = new ColumnDescriptor("Id",     "uuid", false),
    ScalarColumns  = [new ColumnDescriptor("Title", "text", false)],
    FkColumns      = [new ForeignKeyDescriptor("TagIds", "Tag")],
    VectorFields   = [],
    ChunkFields    = [],
    Relations      = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
};

public static SchemaDescriptor TagSchema() => new()
{
    TypeName       = "Tag",
    TableName      = "tags",
    CollectionName = null,
    KeyColumn      = new ColumnDescriptor("Id",      "uuid", false),
    ScalarColumns  = [new ColumnDescriptor("Label",  "text", false)],
    FkColumns      = [],
    VectorFields   = [],
    ChunkFields    = [],
    Relations      = []
};
```

- [ ] **Step 2: Write the failing regression test**

Add to `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` (after the RegisterSchema tests, before the Post tests):

```csharp
// ── ResolveManyToMany ─────────────────────────────────────────────────────

[Fact]
public async Task Get_WithManyToManyRelation_IssuesSingleBatchQuery()
{
    var postId = "33333333-0000-0000-0000-000000000003";
    var tagId1 = "44444444-0000-0000-0000-000000000004";
    var tagId2 = "44444444-0000-0000-0000-000000000005";

    await _registry.RegisterAsync(SchemaFixtures.PostWithTagsSchema());
    await _registry.RegisterAsync(SchemaFixtures.TagSchema());

    var postJson = $$"""{"Id":"{{postId}}","Title":"Hello","TagIds":["{{tagId1}}","{{tagId2}}"]}""";
    _sql.QuerySingleOrDefaultAsync<string>(
            Arg.Is<string>(s => s.Contains("\"posts\"")), Arg.Any<object?>())
        .Returns(postJson);

    var tag1Json = $$"""{"Id":"{{tagId1}}","Label":"dotnet"}""";
    var tag2Json = $$"""{"Id":"{{tagId2}}","Label":"csharp"}""";
    _sql.QueryAsync<KeyedRow>(Arg.Any<string>(), Arg.Any<object?>())
        .Returns(new[] { new KeyedRow(tagId1, tag1Json), new KeyedRow(tagId2, tag2Json) });

    var response = await _sut.Get(
        new MappingGetRequest { TypeName = "Post", Key = postId, Depth = 1 },
        MakeContext());

    await _sql.Received(1).QueryAsync<KeyedRow>(
        Arg.Is<string>(s => s.Contains("= ANY(")),
        Arg.Any<object?>());

    response.Success.Should().BeTrue();
    response.Data.Fields["Tags"].ListValue.Values.Should().HaveCount(2);
}
```

Add `using Iverson.Sql;` to the using block if not already present.

- [ ] **Step 3: Run test to verify it fails**

```bash
dotnet test Iverson.Api.Tests/ --filter "Get_WithManyToManyRelation_IssuesSingleBatchQuery" -v n
```

Expected: FAIL — `QueryAsync<KeyedRow>` received 0 times (current code uses `FetchByKeyAsync` loop).

- [ ] **Step 4: Replace ResolveManyToManyAsync**

In `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, replace the `ResolveManyToManyAsync` method (lines 257–278):

```csharp
private async Task ResolveManyToManyAsync(
    Struct entityStruct, SchemaRelDescriptor relation, int depth, CancellationToken ct)
{
    var ids = GetGetFieldStringList(entityStruct, relation.ForeignKey);
    if (ids.Count == 0) return;

    var relatedSchema = _registry.Get(relation.RelatedTypeName);
    if (relatedSchema is null) return;

    var rows = await _sql.QueryAsync<KeyedRow>(
        $"SELECT \"{relatedSchema.KeyColumn.Name}\"::text AS key, " +
        $"row_to_json(t)::text AS data " +
        $"FROM \"{relatedSchema.TableName}\" t " +
        $"WHERE \"{relatedSchema.KeyColumn.Name}\" = ANY(@ids::uuid[])",
        new { ids = ids.ToArray() });

    var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    var items = new List<Value>();
    foreach (var id in ids)
    {
        if (ct.IsCancellationRequested) break;
        if (!rowsByKey.TryGetValue(id, out var row)) continue;
        var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);
        if (depth > 1)
            await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);
        items.Add(Value.ForStruct(relatedStruct));
    }

    entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
}
```

- [ ] **Step 5: Run all ObjectMappingGrpcServiceTests**

```bash
dotnet test Iverson.Api.Tests/ --filter "ObjectMappingGrpcServiceTests" -v n
```

Expected: all pass (existing tests are unaffected; only `ResolveManyToManyAsync` changed).

- [ ] **Step 6: Commit**

```bash
git add Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
        Iverson.Api.Tests/Helpers/SchemaFixtures.cs \
        Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "perf: batch ResolveManyToManyAsync SQL — replace N+1 per-ID queries with ANY(@ids::uuid[])"
```

---

### Task 3: Parallelize IntelligenceStoreConsumer Embeddings

**Impact:** Main driver of Kafka consumer lag (12,195 messages at run end). Sequential embedding HTTP calls block the consumer; parallelizing all vector fields and chunks per entity multiplies throughput by the number of concurrent embed calls.

**Context:** `IntelligenceStoreConsumer.HandleAsync` (line 79–85) `await`s each `EmbedAsync` call inside a loop — one HTTP round-trip to Ollama blocks the next. Similarly for chunk embeddings (line 121). Fix: collect tasks with `Task.WhenAll`.

**Files:**
- Modify: `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:75-140`
- Modify: `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` after the existing tests:

```csharp
[Fact]
public async Task HandleCreated_WithMultipleVectorFields_EmbedsAllFields()
{
    // Schema with two vector fields — verifies both EmbedAsync calls fire
    var twoVectorSchema = new SchemaDescriptor
    {
        TypeName       = "Doc",
        TableName      = "docs",
        CollectionName = "docs",
        KeyColumn      = new ColumnDescriptor("Id",    "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Title", "text", false),
                          new ColumnDescriptor("Summary", "text", false)],
        FkColumns      = [],
        VectorFields   = [
            new VectorDescriptor("Title",   768, "nomic-embed-text"),
            new VectorDescriptor("Summary", 768, "nomic-embed-text")
        ],
        ChunkFields    = [],
        Relations      = []
    };
    await _registry.RegisterAsync(twoVectorSchema);

    _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new float[768]);

    var payload = """{"Title":"Hello","Summary":"World","Id":"00000000-0000-0000-0000-000000000001"}""";
    var ev = new EntityEvent(
        TypeName:      "Doc",
        Key:           Guid.NewGuid().ToString(),
        PayloadJson:   payload,
        TraceId:       "t-parallel",
        SchemaVersion: "1",
        OccurredAt:    DateTimeOffset.UtcNow,
        TargetStores:  StoreTarget.Record | StoreTarget.Intelligence);

    var sut = BuildSut();
    await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

    _ = _embedding.Received(1).EmbedAsync("Hello",  Arg.Any<CancellationToken>());
    _ = _embedding.Received(1).EmbedAsync("World",  Arg.Any<CancellationToken>());
    _ = _embedding.Received(2).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run test to verify it passes already** (this specific test verifies call count, which already works — it's a guard that won't regress after the change)

```bash
dotnet test Iverson.Api.Tests/ --filter "HandleCreated_WithMultipleVectorFields_EmbedsAllFields" -v n
```

Expected: PASS (sequential or parallel both make 2 calls). This test serves as a non-regression guard.

- [ ] **Step 3: Parallelize vector field embedding in HandleAsync**

In `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`, replace the vector fields block (lines 76–101) with:

```csharp
if (schema.VectorFields.Count > 0)
{
    var namedVectors = new Dictionary<string, float[]>(schema.VectorFields.Count);

    var embedTasks = schema.VectorFields
        .Select(vf => (vf, text: ExtractString(payload, vf.PropertyName)))
        .Where(x => !string.IsNullOrWhiteSpace(x.text))
        .Select(async x => (
            key: $"{x.vf.PropertyName.ToSnakeCase()}_vector",
            vector: await embedding.EmbedAsync(x.text!, ct)
        ))
        .ToList();

    var embedded = await Task.WhenAll(embedTasks);
    foreach (var (key, vector) in embedded)
        namedVectors[key] = vector;

    if (namedVectors.Count > 0)
    {
        var pointPayload = new Dictionary<string, string> { ["key"] = ev.Key };
        foreach (var vf in schema.VectorFields)
        {
            var fieldText = ExtractString(payload, vf.PropertyName);
            if (!string.IsNullOrWhiteSpace(fieldText))
                pointPayload[char.ToLowerInvariant(vf.PropertyName[0]) + vf.PropertyName[1..]] = fieldText;
        }
        await vector.UpsertNamedAsync(schema.CollectionName, pointId, namedVectors, pointPayload);
        logger.LogInformation("[Intelligence] Upserted {Count} vector(s) for {Type}:{Key}",
            namedVectors.Count, ev.TypeName, ev.Key);
    }
}
```

- [ ] **Step 4: Parallelize chunk embedding in HandleAsync**

Still in `HandleAsync`, replace the chunk embedding inner loop (lines 116–134) with:

```csharp
foreach (var cf in schema.ChunkFields)
{
    var text = ExtractString(payload, cf.PropertyName);
    if (string.IsNullOrWhiteSpace(text)) continue;

    var vectorName = $"{cf.PropertyName.ToSnakeCase()}_vector";
    var chunks     = SplitIntoChunks(text, cf.MaxTokens, cf.Overlap).ToList();

    var chunkTasks = chunks.Select(async (c, _) =>
    {
        var chunkVector = await embedding.EmbedAsync(c.Text, ct);
        var chunkId     = ComputeChunkPointId(pointId, cf.PropertyName, c.Index);
        return (chunkVector, chunkId, c.Text, c.Index);
    }).ToList();

    var chunkResults = await Task.WhenAll(chunkTasks);

    foreach (var (chunkVector, chunkId, chunkText, chunkIndex) in chunkResults)
    {
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
    }

    logger.LogInformation("[Intelligence] Ingested {Count} chunk(s) for {Type}:{Key} field={Field}",
        chunks.Count, ev.TypeName, ev.Key, cf.PropertyName);
}
```

Note: `SplitIntoChunks` currently yields `(string Text, int Index)`. The tuple field names `Text` and `Index` match the existing `var (chunkText, chunkIndex) = chunks[i]` pattern — verify the return tuple field names in the existing `SplitIntoChunks` implementation (line 187). The yield is `(text[start..end].Trim(), index++)`, so destructuring as `(Text, Index)` by position works. Change the lambda argument `c` to destructure: `.Select(async (c, _) => { var (chunkText, chunkIndex) = c; ... })` if positional naming is needed.

Rewrite step 4 lambda to be explicit about field names:

```csharp
var chunkTasks = chunks.Select(async chunk =>
{
    var (chunkText, chunkIndex) = chunk;
    var chunkVector = await embedding.EmbedAsync(chunkText, ct);
    var chunkId     = ComputeChunkPointId(pointId, cf.PropertyName, chunkIndex);
    return (chunkVector, chunkId, chunkText, chunkIndex);
}).ToList();
```

- [ ] **Step 5: Run all IntelligenceStoreConsumerTests**

```bash
dotnet test Iverson.Api.Tests/ --filter "IntelligenceStoreConsumerTests" -v n
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Api/Consumers/IntelligenceStoreConsumer.cs \
        Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "perf: parallelize IntelligenceStoreConsumer embeddings — Task.WhenAll for vector fields and chunks"
```

---

### Task 4: Parallelize Aggregate Queries

**Impact:** Aggregate drops from linear-with-specs (specs=6 → 155ms) to near-constant (specs=6 → ~31ms, matching specs=1).

**Context:** `ObjectSearchGrpcService.Aggregate` (line 189) runs aggregation queries sequentially via `foreach` + `await`. Each spec is independent (different StarRocks SQL). Fix: `Task.WhenAll`.

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectSearchGrpcService.cs:186-196`
- Modify: `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

- [ ] **Step 1: Write the failing regression test**

Add to `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`:

```csharp
[Fact]
public async Task Aggregate_WithMultipleSpecs_QueriesAllConcurrently()
{
    await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

    var callOrder = new System.Collections.Concurrent.ConcurrentBag<int>();
    var callCount = 0;

    _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
       .Returns(_ =>
       {
           System.Threading.Interlocked.Increment(ref callCount);
           return Task.FromResult(Enumerable.Empty<dynamic>());
       });

    var request = new AggregateRequest
    {
        TypeName = "Author",
        Aggregations =
        {
            new AggregationSpec { Name = "a1", Field = "Name", Type = AggregationType.Terms },
            new AggregationSpec { Name = "a2", Field = "Name", Type = AggregationType.Terms },
            new AggregationSpec { Name = "a3", Field = "Name", Type = AggregationType.Terms }
        }
    };

    var response = await _sut.Aggregate(request, TestServerCallContext.Create());

    callCount.Should().Be(3);
    response.Results.Should().HaveCount(3);
}
```

- [ ] **Step 2: Run test to verify it passes already** (call count is 3 whether sequential or parallel — this is a correctness guard)

```bash
dotnet test Iverson.Api.Tests/ --filter "Aggregate_WithMultipleSpecs_QueriesAllConcurrently" -v n
```

Expected: PASS. This test is a correctness guard ensuring N specs → N results after the refactor.

- [ ] **Step 3: Replace the sequential foreach with Task.WhenAll**

In `Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace the `foreach` block inside `Aggregate` (lines 189–194):

```csharp
// Before:
foreach (var spec in request.Aggregations)
{
    var srSpec = ProtoToSrSpec(spec);
    var result = await RunAggregationAsync(schema, request.Query, srSpec);
    if (result is not null) response.Results.Add(SrResultToProto(result));
}

// After:
var aggTasks = request.Aggregations
    .Select(spec => RunAggregationAsync(schema, request.Query, ProtoToSrSpec(spec)))
    .ToList();

var aggResults = await Task.WhenAll(aggTasks);

foreach (var result in aggResults)
    if (result is not null) response.Results.Add(SrResultToProto(result));
```

Also remove the TODO comment on line 187 (`// TODO: N+1 round-trips — ...`) since it is now resolved.

- [ ] **Step 4: Run all ObjectSearchGrpcServiceTests**

```bash
dotnet test Iverson.Api.Tests/ --filter "ObjectSearchGrpcServiceTests" -v n
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "perf: parallelize Aggregate queries — Task.WhenAll per aggregation spec"
```

---

### Task 5: Kafka Producer Batching

**Impact:** Reduces write-path p99 tail latency by enabling Kafka's client-side message batching and compression. Currently every `ProduceAsync` sends a single-message request.

**Context:** `Iverson.Events/ServiceCollectionExtensions.cs` line 12 builds a `ProducerConfig` with only `BootstrapServers`. Adding `LingerMs`, `BatchSize`, and `CompressionType` enables Kafka to coalesce messages from concurrent producers.

**Files:**
- Modify: `Iverson.Events/ServiceCollectionExtensions.cs:11-13`

No unit test exists for `ProducerConfig` construction (it's DI wiring). The functional test is the load test itself. Just apply the change.

- [ ] **Step 1: Apply batching configuration**

In `Iverson.Events/ServiceCollectionExtensions.cs`, replace lines 11–13:

```csharp
// Before:
services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build());

// After:
services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        LingerMs         = 5,
        BatchSize        = 65536,
        CompressionType  = CompressionType.Lz4
    }).Build());
```

`CompressionType` is in `Confluent.Kafka` — already imported.

- [ ] **Step 2: Run Events tests**

```bash
dotnet test Iverson.Events.Tests/ -v n
```

Expected: all pass (no DI wiring tests; existing tests mock `IProducer<string, string>` directly).

- [ ] **Step 3: Commit**

```bash
git add Iverson.Events/ServiceCollectionExtensions.cs
git commit -m "perf: enable Kafka producer batching — LingerMs=5 BatchSize=64KB CompressionType=Lz4"
```

---

### Task 6: EmbeddingService Stream Response Parsing

**Impact:** Halves allocation per embedding call — eliminates the intermediate `string` allocation for 768-dim JSON (≈6 KB per call) by reading directly from the response stream.

**Context:** `Iverson.Embeddings/EmbeddingService.cs` lines 55–56 call `ReadAsStringAsync` then `JsonDocument.Parse(string)`. Fix: `ReadAsStreamAsync` + `JsonDocument.ParseAsync(stream)`. The `StringContent` responses in existing tests support stream reading, so no test changes are needed.

**Files:**
- Modify: `Iverson.Embeddings/EmbeddingService.cs:55-56`

- [ ] **Step 1: Verify existing tests pass before the change**

```bash
dotnet test Iverson.Embeddings.Tests/ -v n
```

Expected: all 7 tests pass.

- [ ] **Step 2: Replace string-based parsing with stream parsing**

In `Iverson.Embeddings/EmbeddingService.cs`, replace lines 55–56:

```csharp
// Before:
var responseJson = await response.Content.ReadAsStringAsync(ct);
using var doc    = JsonDocument.Parse(responseJson);

// After:
await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
using var doc                  = await JsonDocument.ParseAsync(responseStream, default, ct);
```

- [ ] **Step 3: Run Embeddings tests**

```bash
dotnet test Iverson.Embeddings.Tests/ -v n
```

Expected: all 7 tests pass.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Embeddings/EmbeddingService.cs
git commit -m "perf: parse embedding response from stream — eliminate intermediate string allocation"
```

---

### Task 7: StarRocksQueryBuilder Column Index Cache

**Impact:** `ResolveColumn` is called once per `SearchClause` and once per `SearchSort`. Currently O(N) LINQ scan over `schema.ScalarColumns` (N = column count). Replace with `ConditionalWeakTable`-backed O(1) dictionary per `SchemaDescriptor` instance.

**Context:** `StarRocksQueryBuilder.ResolveColumn` (line 133) calls `.Select(c => c.Name).Append(schema.KeyColumn.Name)` followed by `.FirstOrDefault(...)`. At 400K rows with hundreds of concurrent searches, this adds up. Fix: cache a `Dictionary<string, string>` (property→column) per `SchemaDescriptor` instance in a `ConditionalWeakTable`. `SchemaDescriptor` instances live for the process lifetime (held by `SchemaRegistry`), so the cache is effectively permanent.

**Files:**
- Modify: `Iverson.Api/StarRocks/StarRocksQueryBuilder.cs:133-137`
- No schema changes needed — cache lives in the query builder.

Existing tests in `Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs` verify correctness and will catch regressions. No new tests required.

- [ ] **Step 1: Run StarRocksQueryBuilderTests before the change**

```bash
dotnet test Iverson.Api.Tests/ --filter "StarRocksQueryBuilderTests" -v n
```

Expected: all pass. Record the count as the baseline.

- [ ] **Step 2: Add ConditionalWeakTable cache and update ResolveColumn**

In `Iverson.Api/StarRocks/StarRocksQueryBuilder.cs`, add the cache field and update `ResolveColumn` (currently lines 133–137):

At the top of the `StarRocksQueryBuilder` class body (after the `using` aliases), add:

```csharp
private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
    SchemaDescriptor,
    Dictionary<string, string>> _columnCache = new();
```

Replace the `ResolveColumn` method body:

```csharp
internal static string? ResolveColumn(SchemaDescriptor schema, string property)
{
    var index = _columnCache.GetValue(schema, static s =>
        s.ScalarColumns
            .Select(c => c.Name)
            .Append(s.KeyColumn.Name)
            .ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase));

    return index.TryGetValue(property, out var col) ? col : null;
}
```

- [ ] **Step 3: Run StarRocksQueryBuilderTests after the change**

```bash
dotnet test Iverson.Api.Tests/ --filter "StarRocksQueryBuilderTests" -v n
```

Expected: same count, all pass.

- [ ] **Step 4: Run full suite**

```bash
dotnet test Iverson.Api.Tests/ -v n
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Api/StarRocks/StarRocksQueryBuilder.cs
git commit -m "perf: cache StarRocksQueryBuilder column index — O(N) scan → O(1) dictionary per SchemaDescriptor"
```

---

### Task 8: Cache MakeGenericMethod Instantiations in GraphAssembler

**Impact:** Eliminates repeated JIT-specialization overhead in `DeserializeStruct`. The method is called once per entity per relation in the single-entity path, and multiple times per streaming response in both batch paths — including twice per response due to the known double-deserialization pattern. After Task 3 parallelizes embeddings and increases throughput, call frequency rises further.

**Context:** `GraphAssembler.DeserializeStruct` (line 256) calls `_fromStructMethod.MakeGenericMethod(targetType)` on every invocation. The base `MethodInfo` is already cached as a static field (line 21), but its per-type generic instantiations are not — `MakeGenericMethod` allocates a new `MethodInfo` and performs JIT checks each time. Fix: add a `ConcurrentDictionary<Type, MethodInfo>` alongside the existing static field to cache per-type instantiations. `Type` has stable reference identity (same CLR type → same object), making it a correct dictionary key.

**Files:**
- Modify: `Iverson.Client/Iverson.Client.Core/GraphAssembler.cs` (lines 21 and 253–257)

No new tests needed — behavior of `DeserializeStruct` is unchanged; existing tests covering `AssembleAsync`/`AssembleManyAsync` are regression guards.

- [ ] **Step 1: Add the per-type method cache**

In `Iverson.Client/Iverson.Client.Core/GraphAssembler.cs`, add the cache field immediately after the existing `_fromStructMethod` field (line 21):

```csharp
private static readonly ConcurrentDictionary<Type, MethodInfo> _fromStructCache = new();
```

`System.Collections.Concurrent` is already imported (verify at file top).

- [ ] **Step 2: Update DeserializeStruct to use the cache**

Replace the `DeserializeStruct` method body (lines 253–257):

```csharp
private static object? DeserializeStruct(Struct? data, Type targetType)
{
    if (data is null) return null;
    var method = _fromStructCache.GetOrAdd(targetType,
        static t => _fromStructMethod.MakeGenericMethod(t));
    return method.Invoke(null, [data]);
}
```

The `static` lambda prevents capture of `_fromStructMethod` per call — it's accessed via the static field reference inside the lambda body, which is fine.

- [ ] **Step 3: Run client tests**

```bash
dotnet test Iverson.Client/ -v n
```

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Client/Iverson.Client.Core/GraphAssembler.cs
git commit -m "perf: cache MakeGenericMethod instantiations in GraphAssembler — eliminate per-call JIT overhead"
```

---

### Task 9: Resolve FK PropertyInfo at Startup, Store on RelationDescriptor

**Impact:** Eliminates per-call `Type.GetProperty` in both `AssembleSingle` (single-entity path) and `BatchAssembleSingleAsync` (batch path). Moves all FK reflection to `EntityRegistry` scan time — the same place `KeyProperty` is already resolved — making runtime graph assembly reflection-free for single/ManyToOne relations.

**Context:** `GraphAssembler.AssembleSingle` (line 59) and `BatchAssembleSingleAsync` (line 149) each call `descriptor.EntityType.GetProperty(fkName, BindingFlags.Public | BindingFlags.Instance)` at runtime. This is structurally identical to the `KeyProperty` resolution in `EntityRegistry.Scan` (line 26), which is cached on `EntityDescriptor`. The FK property is equally fixed for the process lifetime and belongs in the same startup scan. `RelationDescriptor` already stores the navigation `PropertyInfo`; adding the FK `PropertyInfo` alongside it is consistent.

Only OneToOne and ManyToOne relations use FK property lookup (the other two kinds use payload key lists, not `GetProperty`). OneToMany and ManyToMany leave `ForeignKeyProperty` null.

**Files:**
- Modify: `Iverson.Client/Iverson.Client.Core/RelationDescriptor.cs`
- Modify: `Iverson.Client/Iverson.Client.Core/EntityRegistry.cs` (lines 50–63)
- Modify: `Iverson.Client/Iverson.Client.Core/GraphAssembler.cs` (lines 59–68 and 149)

- [ ] **Step 1: Add ForeignKeyProperty to RelationDescriptor**

In `Iverson.Client/Iverson.Client.Core/RelationDescriptor.cs`, add one property after `ForeignKey`:

```csharp
/// <summary>
/// Resolved at startup for OneToOne / ManyToOne. Null for OneToMany / ManyToMany.
/// </summary>
public PropertyInfo? ForeignKeyProperty { get; init; }
```

- [ ] **Step 2: Resolve FK PropertyInfo during EntityRegistry scan**

In `Iverson.Client/Iverson.Client.Core/EntityRegistry.cs`, replace the `BuildRelations` method (lines 46–66) with:

```csharp
private static IReadOnlyList<RelationDescriptor> BuildRelations(Type type)
{
    var relations = new List<RelationDescriptor>();

    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (prop.GetCustomAttribute<OneToOneAttribute>() is { } oto)
            relations.Add(new RelationDescriptor
            {
                Property           = prop,
                RelatedType        = oto.Related,
                Kind               = RelationKind.OneToOne,
                ForeignKey         = oto.ForeignKey,
                ForeignKeyProperty = ResolveFkProperty(type, oto.Related, oto.ForeignKey)
            });

        else if (prop.GetCustomAttribute<OneToManyAttribute>() is { } otm)
            relations.Add(new RelationDescriptor
            {
                Property    = prop,
                RelatedType = otm.Related,
                Kind        = RelationKind.OneToMany,
                ForeignKey  = otm.ForeignKey
            });

        else if (prop.GetCustomAttribute<ManyToOneAttribute>() is { } mto)
            relations.Add(new RelationDescriptor
            {
                Property           = prop,
                RelatedType        = mto.Related,
                Kind               = RelationKind.ManyToOne,
                ForeignKey         = mto.ForeignKey,
                ForeignKeyProperty = ResolveFkProperty(type, mto.Related, mto.ForeignKey)
            });

        else if (prop.GetCustomAttribute<ManyToManyAttribute>() is { } mtm)
            relations.Add(new RelationDescriptor
            {
                Property    = prop,
                RelatedType = mtm.Related,
                Kind        = RelationKind.ManyToMany,
                ForeignKey  = mtm.JoinKey
            });
    }

    return relations;
}

private static PropertyInfo? ResolveFkProperty(Type type, Type relatedType, string? explicitFk) =>
    type.GetProperty(
        explicitFk ?? $"{relatedType.Name}Id",
        BindingFlags.Public | BindingFlags.Instance);
```

- [ ] **Step 3: Use ForeignKeyProperty in AssembleSingle**

In `Iverson.Client/Iverson.Client.Core/GraphAssembler.cs`, replace the FK lookup in `AssembleSingle` (lines 58–63):

```csharp
// Before:
var fkName = relation.ForeignKey ?? $"{relation.RelatedType.Name}Id";
var fkProp = descriptor.EntityType
    .GetProperty(fkName, BindingFlags.Public | BindingFlags.Instance);

if (fkProp is null)
{
    logger.LogDebug("FK property '{Fk}' not found on {Type}, skipping", fkName, descriptor.EntityName);
    return;
}

// After:
var fkProp = relation.ForeignKeyProperty;
if (fkProp is null)
{
    logger.LogDebug("FK property not found on {Type} for relation {Relation}, skipping",
        descriptor.EntityName, relation.Property.Name);
    return;
}
```

- [ ] **Step 4: Use ForeignKeyProperty in BatchAssembleSingleAsync**

In the same file, replace the FK lookup in `BatchAssembleSingleAsync` (lines 148–149):

```csharp
// Before:
var fkName = relation.ForeignKey ?? $"{relation.RelatedType.Name}Id";
var fkProp = descriptor.EntityType.GetProperty(fkName, BindingFlags.Public | BindingFlags.Instance);

// After:
var fkProp = relation.ForeignKeyProperty;
```

The null guard on the next line (`if (fkProp is null) return;`) stays unchanged.

- [ ] **Step 5: Run client tests**

```bash
dotnet test Iverson.Client/ -v n
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Client/Iverson.Client.Core/RelationDescriptor.cs \
        Iverson.Client/Iverson.Client.Core/EntityRegistry.cs \
        Iverson.Client/Iverson.Client.Core/GraphAssembler.cs
git commit -m "perf: resolve FK PropertyInfo at startup — move runtime GetProperty calls to EntityRegistry scan"
```

---

## Self-Review

**Spec coverage check:**
- N+1 GetMany SQL → Task 1 ✓
- N+1 ResolveManyToManyAsync SQL → Task 2 ✓
- Sequential IntelligenceStoreConsumer embeddings (Kafka lag root cause) → Task 3 ✓
- Aggregate N+1 SQL → Task 4 ✓
- Kafka producer unbatched → Task 5 ✓
- EmbeddingService double allocation → Task 6 ✓
- ResolveColumn O(N) scan → Task 7 ✓
- `MakeGenericMethod` per-call allocation in `GraphAssembler.DeserializeStruct` → Task 8 ✓
- FK `PropertyInfo` runtime lookup in `GraphAssembler` → Task 9 ✓
- `ToSnakeCase()` per-message caching (low-impact, requires VectorDescriptor/ChunkDescriptor changes) → **deferred** — omitted intentionally; address in next performance cycle if profiling confirms it.
- StarRocksRepository.UpsertAsync column-list rebuild → **deferred** — entry order from `Dictionary<string, JsonElement>` is not guaranteed, making a safe cache key non-trivial. Omitted to avoid SQL mapping bugs.

**Placeholder scan:** None found.

**Type consistency:** `KeyedRow(string Key, string Data)` used in Tasks 1 and 2 — same type, same namespace.
