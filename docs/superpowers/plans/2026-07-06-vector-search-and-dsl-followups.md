# Vector-Search DSL + Join Equalization + DSL Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Qdrant vector search (`SearchSimilar`/`SearchChunks`) a real filter DSL across all 5 languages (currently unfiltered and payload-untyped), close the two remaining cross-language join divergences (Pipeline composite-key joins, plain-Search multi-hop joins) in Java/Python/TypeScript/Go, and clear the backlog of Minor-severity findings deliberately deferred from the three most recently completed DSL plans.

**Architecture:** Part A adds two new fields (`filter`, `filter_logic`) to `SearchSimilarRequest`/`SearchChunksRequest`, a new `QdrantFilterBuilder` server component that translates `SearchClause` lists into a Qdrant `Filter`, a payload-typing fix so filtered values are stored as their real type instead of coerced strings, and one canonical builder entry point (`Query.Similar`/`Query.Chunks`-equivalent) per language. Part B adds composite-key and explicit-left-type join overloads to the 4 non-C# languages' existing builders — no proto changes, since `PipelineJoin.on` is already `repeated` and `JoinSpec` keeps its existing single-pair shape (composite-key `JoinSpec` is a separate, out-of-scope proto change — see `project-joinspec-composite-key-followup` in memory). Part C is a grab-bag of test-coverage/hardening/cosmetic fixes against existing shipped code, grouped by file/area so each stays independently reviewable.

**Tech Stack:** C# (xUnit/FluentAssertions/NSubstitute, Testcontainers), Java (JUnit 5), Python (pytest), TypeScript (vitest), Go (`go test`), server xUnit + Testcontainers (StarRocks + Qdrant).

**Spec:** `docs/superpowers/specs/2026-07-06-vector-search-and-dsl-followups-design.md`

**Ordering:** Tasks 1–8 (Part A server) must run in the order given — each depends on the previous (proto → payload typing → payload population → filter builder → search wiring → RPC wiring → integration test). Tasks 9–13 (Part A clients) each depend only on Task 1 (proto regen for that language) and can run in any order relative to each other, but Task 9 (C#) must precede Task 22 (C# hardening) since both touch `Query.cs`. Task 14 (docs) depends on 9–13. Tasks 15–18 (Part B) are independent of Part A and of each other. Task 19 (docs) depends on 15–18. Tasks 20–21 (Part C server) are independent of everything else. Tasks 22–26 (Part C clients) are independent of each other but Task 22 must run after Task 9.

## Global Constraints

**Model assignment (overrides subagent-driven-development's default per-task judgment):** use **Opus** for the pre-flight plan review, every per-task reviewer subagent, and the final whole-branch code reviewer subagent. For every implementer subagent (every task's Steps 1–N), the dispatching agent chooses **Haiku or Sonnet** per task, using its own judgment of that task's complexity (e.g. a mechanical rename or single-file cosmetic fix can go to Haiku; anything touching validation logic, cross-language parity, or SQL/filter-translation semantics should go to Sonnet). Always pass the model explicitly when dispatching — never let it inherit the session default.

- Proto source of truth: `Iverson.Clients/Common/Proto/object_search.proto`. After editing it, regenerate stubs: C# and Java regenerate automatically on `dotnet build`/`mvn compile` (MSBuild `Grpc.Tools` / `protobuf-maven-plugin`, no manual step). Python: `cd Iverson.Clients/Python && ./scripts/generate_protos.sh`. Go: `cd Iverson.Clients/Go && ./scripts/generate_protos.sh`. TypeScript: `cd Iverson.Clients/TypeScript && ./scripts/generate_protos.sh`.
- Build-time validation error types (established by the DSL-improvements plan, applies to every new client-side validation in this plan too): C# `InvalidOperationException`, Java `IllegalStateException`, Python `ValueError`, TypeScript `Error`, Go `error` returned from `Build()`.
- Case-insensitive comparisons throughout this plan use: C# `StringComparison.OrdinalIgnoreCase`, Java `.toLowerCase(java.util.Locale.ROOT)` (import `java.util.Locale`, not inline-FQN — see Task 24), Python `.lower()`, TypeScript `.toLowerCase()`, Go `strings.ToLower(...)`.
- Run suites:
  - Server: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"` (unit); drop the filter for the Docker-backed integration suite (Task 8).
  - `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj` (Qdrant unit + integration tests).
  - C# clients: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj`, `dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj`.
  - Java: `cd Iverson.Clients/Java/client && mvn -q test`.
  - Python: `cd Iverson.Clients/Python && python -m pytest -q`.
  - TS: `cd Iverson.Clients/TypeScript && npx vitest run`.
  - Go: `cd Iverson.Clients/Go && go test ./...`.
- Qdrant SDK facts (Qdrant.Client 1.18.1, confirmed against the installed package's XML docs): `Qdrant.Client.Grpc.Conditions` exposes `MatchKeyword(string field, string keyword)` (exact string match — there is no plain `Match(string,string)` overload), `Match(string field, bool value)`, `Match(string field, long value)`, `Match(string field, IReadOnlyList<string> keywords)` (match-any), and `Range(string field, Range range)`. `Condition` has `&`/`|`/`!` operator overloads; `Filter` has implicit conversion from `Condition` and settable `Must`/`Should`/`MustNot` repeated-condition properties. `QdrantClient.SearchAsync` already accepts an optional `filter: Filter?` parameter today (unused by any current caller) — no SDK-side change needed to wire filtering through.
- `SearchOperator` enum (proto, unchanged by this plan): `EQUALS=0, NOT_EQUALS=1, CONTAINS=2, STARTS_WITH=3, GREATER_THAN=4, LESS_THAN=5, GREATER_THAN_OR_EQUALS=6, LESS_THAN_OR_EQUALS=7, IN=8, VECTOR_SIMILAR=9, ENDS_WITH=10`.
- Vector-search payload keys are camelCase (`ToLowerInvariant` on the first character), matching the existing convention already used for the `key`/embedded-field-text entries in `IntelligenceStoreConsumer.HandleAsync` — Task 3 extends this to every scalar/FK column and Task 4's payload-index names must match.

---

### Task 1: Proto — add filter fields to SearchSimilarRequest/SearchChunksRequest

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_search.proto` (message blocks at lines 94–110)

**Interfaces:**
- Produces: `SearchSimilarRequest.Filter` (`repeated SearchClause`, field 6), `SearchSimilarRequest.FilterLogic` (`SearchLogic`, field 7), and the identical two fields on `SearchChunksRequest`, generated into all 5 languages' stubs. Every later task in Parts A consumes these exact field names (C# `request.Filter`/`request.FilterLogic`, Java `request.getFilterList()`/`request.getFilterLogic()`, Python `request.filter`/`request.filter_logic`, TS `request.filter`/`request.filterLogic`, Go `request.Filter`/`request.FilterLogic`).

- [ ] **Step 1: Edit the proto**

In `Iverson.Clients/Common/Proto/object_search.proto`, replace lines 94–110:

```protobuf
// Searches by embedding similarity on a property annotated with [IversonEmbedding].
// The server embeds `query` with the same model and searches the vector index.
message SearchSimilarRequest {
    string type_name = 1;  // registered entity type
    string property  = 2;  // property with [IversonEmbedding]
    string query     = 3;  // text to embed and compare
    uint32 top_k     = 4;
    string trace_id  = 5;
    repeated SearchClause filter        = 6;  // optional scalar/FK filter, ANDed/ORed with filter_logic
    SearchLogic           filter_logic = 7;   // default AND
}

// Searches by chunk embedding similarity on a property annotated with [IversonChunk].
// Returns individual passage chunks with the parent entity key and relevance score.
message SearchChunksRequest {
    string type_name = 1;  // registered entity type
    string property  = 2;  // property with [IversonChunk]
    string query     = 3;  // text to embed and compare
    uint32 top_k     = 4;
    string trace_id  = 5;
    // At most one EQUALS clause on the type's primary-key property; see ObjectSearchGrpcService.SearchChunks.
    repeated SearchClause filter        = 6;
    SearchLogic           filter_logic = 7;
}
```

- [ ] **Step 2: Regenerate stubs for the 3 scripted languages**

```bash
cd Iverson.Clients/Python && ./scripts/generate_protos.sh
cd ../Go && ./scripts/generate_protos.sh
cd ../TypeScript && ./scripts/generate_protos.sh
```

Expected: each prints its "generated" success line, no errors. C# and Java regenerate automatically on the next `dotnet build`/`mvn compile` (Tasks 2 and 10 respectively will trigger this).

- [ ] **Step 3: Build the server project to confirm the C# stubs compile**

Run: `dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj`
Expected: Build succeeds; `Iverson.Client.Contracts.SearchSimilarRequest` now has a public `Filter` (`RepeatedField<SearchClause>`) and `FilterLogic` property.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Clients/Common/Proto/object_search.proto Iverson.Clients/Python/iverson_client/generated Iverson.Clients/Go/generated Iverson.Clients/TypeScript/generated
git commit -m "feat(proto): add filter/filter_logic to SearchSimilarRequest and SearchChunksRequest"
```

---

### Task 2: Server — type-preserving Qdrant payload upserts

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IVectorService.cs`
- Modify: `Iverson.Server/Iverson.Vector/QdrantVectorService.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs`, `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing new (existing `PointStruct.Payload` is `MapField<string, Value>`; `Qdrant.Client.Grpc.Value` has implicit conversions from `string`/`long`/`double`/`bool`).
- Produces: `IVectorService.UpsertAsync`/`UpsertNamedAsync` payload parameter type changes from `Dictionary<string,string>?`/`IReadOnlyDictionary<string,string>?` to `IReadOnlyDictionary<string, object>?`. Task 3 and Task 5 consume this new signature.

- [ ] **Step 1: Write the failing tests**

Append to `QdrantVectorServiceTests.cs` (in the "Interface contract tests (mocked)" section):

```csharp
    [Fact]
    public async Task UpsertAsync_AcceptsTypedPayloadValues()
    {
        var vector = Substitute.For<IVectorService>();
        var payload = new Dictionary<string, object>
        {
            ["title"]     = "typed",
            ["wordCount"] = 42L,
            ["rating"]    = 4.5,
            ["published"] = true
        };

        await vector.UpsertAsync("articles", 1, [0.1f, 0.2f], payload);

        await vector.Received(1).UpsertAsync("articles", 1, Arg.Any<float[]>(),
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                p["title"].Equals("typed") && p["wordCount"].Equals(42L) &&
                p["rating"].Equals(4.5) && p["published"].Equals(true)));
    }
```

Append a new integration test to `QdrantIntegrationTests.cs` (real Qdrant, real typed round-trip):

```csharp
    [Fact]
    public async Task UpsertAsync_TypedPayload_RoundTripsThroughRealQdrant()
    {
        var collection = UniqueName();
        await _svc.EnsureCollectionAsync(collection, 4);

        await _svc.UpsertAsync(collection, 1, [0.1f, 0.2f, 0.3f, 0.4f], new Dictionary<string, object>
        {
            ["wordCount"] = 500L,
            ["rating"]    = 4.5,
            ["featured"]  = true
        });

        var results = await _svc.SearchAsync(collection, [0.1f, 0.2f, 0.3f, 0.4f], limit: 1);

        results.Should().ContainSingle();
        // Read-side projection (VectorSearchResult.Payload) stays string-typed by design —
        // this only asserts the upsert didn't throw and a point round-tripped.
        results[0].Payload.Should().ContainKey("wordCount");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj --filter "FullyQualifiedName~TypedPayload"`
Expected: FAIL — compile error, `UpsertAsync` doesn't accept `Dictionary<string, object>` yet.

- [ ] **Step 3: Change `IVectorService`**

In `IVectorService.cs`, replace the `UpsertAsync`/`UpsertNamedAsync` signatures:

```csharp
namespace Iverson.Vector;

public interface IVectorService
{
    Task EnsureCollectionAsync(
        string collectionName,
        ulong vectorSize);
    Task ApplyCollectionAsync(CollectionSchema schema);
    Task UpsertAsync(
        string collectionName,
        ulong id,
        float[] vector,
        IReadOnlyDictionary<string, object>? payload = null);
    Task UpsertNamedAsync(
        string collectionName,
        ulong id,
        IReadOnlyDictionary<string, float[]> namedVectors,
        IReadOnlyDictionary<string, object>? payload = null);
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        ulong limit = 10);
    Task<IReadOnlyList<VectorSearchResult>> SearchNamedAsync(
        string collectionName,
        string vectorName,
        float[] queryVector,
        ulong limit = 10);
    Task DeleteAsync(string collectionName, ulong id);
}

public record VectorSearchResult(
    ulong Id,
    double Score,
    IReadOnlyDictionary<string, string> Payload);
```

(`SearchNamedAsync`'s `Filter?` parameter is added separately in Task 5 — don't add it here, keep this task scoped to payload typing only.)

- [ ] **Step 4: Implement typed conversion in `QdrantVectorService`**

Add a private helper and use it in both upsert methods:

```csharp
    private static Value ToQdrantValue(object value) => value switch
    {
        string s           => s,
        bool b             => b,
        int i              => (long)i,
        long l             => l,
        float f            => (double)f,
        double d           => d,
        DateTime dt        => dt.ToString("o"),
        DateTimeOffset dto => dto.ToString("o"),
        _                  => value.ToString() ?? string.Empty
    };
```

Replace the two payload-assignment loops:

```csharp
        if (payload is not null)
            foreach (var (key, value) in payload)
                point.Payload[key] = ToQdrantValue(value);
```

(one occurrence in `UpsertAsync`, one in `UpsertNamedAsync` — both currently read `point.Payload[key] = value;` relying on the implicit `string`→`Value` conversion; replace both with the line above.)

- [ ] **Step 5: Run the Vector test project**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj`
Expected: PASS, including the new unit test. The integration test requires Docker; run it separately if Docker is available: `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj --filter "FullyQualifiedName~TypedPayload_RoundTrips"`.

- [ ] **Step 6: Fix call sites broken by the signature change**

Run: `dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj` — this will fail at `IntelligenceStoreConsumer.cs`'s two `UpsertNamedAsync` calls, which currently pass `Dictionary<string,string>`. `Dictionary<string,string>` does not implicitly convert to `IReadOnlyDictionary<string,object>`, so change the two payload dictionary declarations in `IntelligenceStoreConsumer.cs` (lines 96 and 139) from `Dictionary<string, string>` to `Dictionary<string, object>`:

```csharp
                var pointPayload = new Dictionary<string, object> { ["key"] = ev.Key };
```

and

```csharp
                    await vector.UpsertNamedAsync(
                        chunksCollection,
                        chunkId,
                        new Dictionary<string, float[]> { [vectorName] = chunkVector },
                        new Dictionary<string, object>
                        {
                            ["text"]        = chunkText,
                            ["parent_id"]   = ev.Key,
                            ["field"]       = cf.PropertyName,
                            ["chunk_index"] = chunkIndex.ToString()
                        });
```

This is a mechanical type change only — no behavior change yet (Task 3 adds the new scalar/FK population). Also fix `IntelligenceStoreConsumerTests.cs`'s `Arg.Do<IReadOnlyDictionary<string, string>?>` capture (line 231) to `Arg.Do<IReadOnlyDictionary<string, object>?>`, and its assertions at lines 238-239 (`capturedPayload!["key"].Should().Be(entityKey)`) still work unchanged since `object.Equals(string)` behaves correctly for boxed strings.

Run: `dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj` again.
Expected: Build succeeds.

- [ ] **Step 7: Run the full non-integration server suite**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add Iverson.Server/Iverson.Vector/IVectorService.cs Iverson.Server/Iverson.Vector/QdrantVectorService.cs \
        Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs \
        Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "feat(server): type-preserving Qdrant payload upserts (IVectorService payload param -> object)"
```

---

### Task 3: Server — populate scalar/FK columns into vector payload, camelCase payload-index names

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` (new test), `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` (new tests — none exist for `ToCollectionSchema` today)

**Interfaces:**
- Consumes: `IVectorService.UpsertNamedAsync`'s `IReadOnlyDictionary<string, object>?` payload param (Task 2), `SchemaDescriptor.ScalarColumns`/`FkColumns` (`ColumnDescriptor(Name, SqlType, IsNullable)` / `ForeignKeyDescriptor(ColumnName, ReferencedTypeName)`).
- Produces: every scalar/FK column's real, typed value in the named-vector upsert payload, camelCased; `SchemaBuilder.ToCollectionSchema`'s `PayloadIndex.FieldName` values are now camelCase, matching. Task 6 (`SearchSimilar` filter validation) relies on the camelCased payload keys matching the indexes this task declares.

- [ ] **Step 1: Write the failing tests**

Append to `IntelligenceStoreConsumerTests.cs`:

```csharp
    [Fact]
    public async Task HandleCreated_PointPayload_ContainsTypedScalarAndFkColumns()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var entityKey  = Guid.NewGuid().ToString();
        var authorId   = "00000000-0000-0000-0000-000000000001";
        var payload    = $$$"""{"Title":"T","Body":"B","AuthorId":"{{{authorId}}}"}""";
        var ev = new EntityEvent(
            TypeName:      "Article",
            Key:           entityKey,
            PayloadJson:   payload,
            TraceId:       "trace-typed",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vector.UpsertNamedAsync(
            "articles",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["authorId"].Should().Be(authorId);
    }
```

(`ArticleSchema()`'s `ScalarColumns` are `Title`/`Body` (both TEXT, already written as the embedded-field text under a different key), and `FkColumns` is `[new ForeignKeyDescriptor("AuthorId", "Author")]` — this test asserts the FK column specifically since it's the one not already covered by the existing embedded-field-text test.)

Create `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` additions (append — the file already exists with other `SchemaBuilder` tests):

```csharp
    [Fact]
    public void ToCollectionSchema_PayloadIndexNames_AreCamelCase()
    {
        var descriptor = SchemaFixtures.ArticleSchema();

        var schema = SchemaBuilder.ToCollectionSchema(descriptor);

        schema.PayloadIndexes.Select(p => p.FieldName).Should().Contain(["title", "body", "authorId"]);
        schema.PayloadIndexes.Select(p => p.FieldName).Should().NotContain(["Title", "Body", "AuthorId"]);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~PointPayload_ContainsTypedScalarAndFkColumns|FullyQualifiedName~PayloadIndexNames_AreCamelCase"`
Expected: FAIL — `capturedPayload` has no `"authorId"` key yet; `SchemaBuilder` doesn't exist as a public-enough symbol from the test project yet (it's `internal` — the test project must already have `InternalsVisibleTo` since `SchemaBuilderTests.cs` exists and presumably already tests it; confirm via `grep InternalsVisibleTo Iverson.Server/Iverson.Api/*.cs` if the build fails on visibility, but this is expected to already be wired since the file exists).

- [ ] **Step 3: Add typed extraction + camelCase helper, populate payload in `IntelligenceStoreConsumer`**

In `IntelligenceStoreConsumer.cs`, add a private helper near `ExtractString`:

```csharp
    private static object? ExtractTypedValue(JsonElement payload, string propertyName, string sqlType)
    {
        if (!payload.TryGetProperty(propertyName, out var v))
        {
            var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
            if (!payload.TryGetProperty(camel, out v)) return null;
        }
        if (v.ValueKind == JsonValueKind.Null) return null;

        return sqlType.ToUpperInvariant() switch
        {
            "INTEGER" or "BIGINT"         => v.TryGetInt64(out var l) ? l : null,
            "REAL" or "DOUBLE PRECISION"  => v.TryGetDouble(out var d) ? d : null,
            "BOOLEAN"                     => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null,
            _                             => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
        };
    }

    private static string ToCamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
```

In the named-vector upsert block (inside `if (namedVectors.Count > 0)`), change `pointPayload`'s declared type and add the scalar/FK population loop right after the existing embedded-field-text loop:

```csharp
                var pointPayload = new Dictionary<string, object> { ["key"] = ev.Key };
                foreach (var vf in schema.VectorFields)
                {
                    var fieldText = ExtractString(payload, vf.PropertyName);
                    if (!string.IsNullOrWhiteSpace(fieldText))
                        pointPayload[ToCamelCase(vf.PropertyName)] = fieldText;
                }
                foreach (var col in schema.ScalarColumns)
                {
                    var val = ExtractTypedValue(payload, col.Name, col.SqlType);
                    if (val is not null) pointPayload[ToCamelCase(col.Name)] = val;
                }
                foreach (var fk in schema.FkColumns)
                {
                    var val = ExtractTypedValue(payload, fk.ColumnName, "TEXT");
                    if (val is not null) pointPayload[ToCamelCase(fk.ColumnName)] = val;
                }
                await vector.UpsertNamedAsync(schema.CollectionName, pointId, namedVectors, pointPayload);
```

- [ ] **Step 4: CamelCase the payload-index names in `SchemaBuilder.ToCollectionSchema`**

In `SchemaBuilder.cs`, add the same helper and update `ToCollectionSchema`:

```csharp
    internal static string ToCamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    internal static CollectionSchema ToCollectionSchema(SchemaDescriptor d) => new(
        d.CollectionName!,
        d.VectorFields.Select(v => new NamedVector($"{v.PropertyName.ToSnakeCase()}_vector", v.Dimension)).ToList(),
        d.ScalarColumns
            .Select(c => new PayloadIndex(ToCamelCase(c.Name), SqlTypeToPayloadKind(c.SqlType)))
            .Concat(d.FkColumns.Select(fk => new PayloadIndex(ToCamelCase(fk.ColumnName), PayloadIndexKind.Keyword)))
            .ToList());
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS, including both new tests.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs
git commit -m "feat(server): populate scalar/FK columns into Qdrant payload, camelCase payload-index names"
```

---

### Task 4: Server — `QdrantFilterBuilder` component

**Files:**
- Create: `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs` (new file)

**Interfaces:**
- Consumes: `SearchClause`/`SearchOperator`/`SearchClauseType`/`SearchLogic`/`SearchValue` (`Iverson.Client.Contracts`), `Qdrant.Client.Grpc.Filter`/`Condition`/`Conditions`/`Range`.
- Produces: `public static class QdrantFilterBuilder { public static Filter Build(IReadOnlyList<SearchClause> clauses, SearchLogic logic, string rpcName); }`. Tasks 6 and 7 consume this exact signature.

- [ ] **Step 1: Write the failing tests**

Create `QdrantFilterBuilderTests.cs`:

```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Contracts;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Vector.Tests;

public class QdrantFilterBuilderTests
{
    private static SearchClause Clause(string property, SearchOperator op, SearchValue value,
        SearchClauseType clauseType = SearchClauseType.Filter) => new()
    {
        Property = property, Operator = op, Value = value, ClauseType = clauseType
    };

    private static SearchValue Str(string s)   => new() { StringVal = s };
    private static SearchValue Num(double n)   => new() { NumberVal = n };
    private static SearchValue Bool(bool b)    => new() { BoolVal = b };
    private static SearchValue List(params string[] vals)
    {
        var v = new SearchValue { StringList = new RepeatedString() };
        v.StringList.Values.AddRange(vals);
        return v;
    }

    [Fact]
    public void Build_EqualsString_ProducesMatchKeyword()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech"))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
        filter.Should.Should().BeEmpty();
        filter.MustNot.Should().BeEmpty();
    }

    [Fact]
    public void Build_EqualsBool_ProducesMatch()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("featured", SearchOperator.Equals, Bool(true))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_EqualsNumber_ProducesMatch()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("wordCount", SearchOperator.Equals, Num(500))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_NotEquals_RoutesToMustNot()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.NotEquals, Str("Tech"))], SearchLogic.And, "SearchSimilar");

        filter.MustNot.Should().ContainSingle();
        filter.Must.Should().BeEmpty();
    }

    [Fact]
    public void Build_MustNotClauseType_RoutesToMustNot()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech"), SearchClauseType.MustNot)],
            SearchLogic.And, "SearchSimilar");

        filter.MustNot.Should().ContainSingle();
    }

    [Fact]
    public void Build_NotEqualsAndMustNotClauseType_DoubleNegative_RoutesToMust()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.NotEquals, Str("Tech"), SearchClauseType.MustNot)],
            SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
        filter.MustNot.Should().BeEmpty();
    }

    [Fact]
    public void Build_GreaterThan_ProducesRangeCondition()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("wordCount", SearchOperator.GreaterThan, Num(100))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Theory]
    [InlineData(SearchOperator.GreaterThan)]
    [InlineData(SearchOperator.LessThan)]
    [InlineData(SearchOperator.GreaterThanOrEquals)]
    [InlineData(SearchOperator.LessThanOrEquals)]
    public void Build_RangeOperators_DoNotThrow(SearchOperator op)
    {
        var act = () => QdrantFilterBuilder.Build([Clause("wordCount", op, Num(100))], SearchLogic.And, "SearchSimilar");
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_In_ProducesMatchAnyCondition()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.In, List("Tech", "Science"))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_OrLogic_RoutesPositiveClausesToShould()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech")),
             Clause("category", SearchOperator.Equals, Str("Science"))],
            SearchLogic.Or, "SearchSimilar");

        filter.Should.Should().HaveCount(2);
        filter.Must.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SearchOperator.Contains)]
    [InlineData(SearchOperator.StartsWith)]
    [InlineData(SearchOperator.EndsWith)]
    [InlineData(SearchOperator.VectorSimilar)]
    public void Build_UnsupportedOperator_ThrowsInvalidArgumentNamingOperatorAndRpc(SearchOperator op)
    {
        var value = op == SearchOperator.VectorSimilar
            ? new SearchValue { FloatList = new RepeatedFloat { Values = { 0.1f } } }
            : Str("x");

        var act = () => QdrantFilterBuilder.Build([Clause("title", op, value)], SearchLogic.And, "SearchSimilar");

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains(op.ToString())
                     && e.Status.Detail.Contains("SearchSimilar"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj --filter "FullyQualifiedName~QdrantFilterBuilderTests"`
Expected: FAIL — `QdrantFilterBuilder` does not exist.

- [ ] **Step 3: Implement `QdrantFilterBuilder`**

Create `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs`:

```csharp
using Grpc.Core;
using Iverson.Client.Contracts;
using Qdrant.Client.Grpc;

namespace Iverson.Vector;

/// <summary>
/// Translates DSL <see cref="SearchClause"/> lists into a Qdrant <see cref="Filter"/>.
/// Used by SearchSimilar/SearchChunks — the SQL search paths reject VECTOR_SIMILAR and never
/// call this; this builder in turn rejects CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR since
/// Qdrant payload filtering has no equivalent of substring/prefix/suffix matching or nested
/// vector similarity.
/// </summary>
public static class QdrantFilterBuilder
{
    public static Filter Build(IReadOnlyList<SearchClause> clauses, SearchLogic logic, string rpcName)
    {
        var filter = new Filter();

        foreach (var clause in clauses)
        {
            var (condition, negate) = BuildCondition(clause, rpcName);
            var mustNot = negate ^ (clause.ClauseType == SearchClauseType.MustNot);

            if (mustNot)
                filter.MustNot.Add(condition);
            else if (logic == SearchLogic.Or)
                filter.Should.Add(condition);
            else
                filter.Must.Add(condition);
        }

        return filter;
    }

    private static (Condition Condition, bool Negate) BuildCondition(SearchClause clause, string rpcName) =>
        clause.Operator switch
        {
            SearchOperator.Equals    => (BuildEqualityCondition(clause.Property, clause.Value), false),
            SearchOperator.NotEquals => (BuildEqualityCondition(clause.Property, clause.Value), true),
            SearchOperator.GreaterThan          => (Conditions.Range(clause.Property, new Range { Gt  = clause.Value.NumberVal }), false),
            SearchOperator.LessThan             => (Conditions.Range(clause.Property, new Range { Lt  = clause.Value.NumberVal }), false),
            SearchOperator.GreaterThanOrEquals  => (Conditions.Range(clause.Property, new Range { Gte = clause.Value.NumberVal }), false),
            SearchOperator.LessThanOrEquals     => (Conditions.Range(clause.Property, new Range { Lte = clause.Value.NumberVal }), false),
            SearchOperator.In => (Conditions.Match(clause.Property, clause.Value.StringList.Values.ToList()), false),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Operator '{clause.Operator}' is not supported by {rpcName} filters. Supported operators: " +
                "EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS, LESS_THAN_OR_EQUALS, IN."))
        };

    private static Condition BuildEqualityCondition(string property, SearchValue value) => value.KindCase switch
    {
        SearchValue.KindOneofCase.StringVal => Conditions.MatchKeyword(property, value.StringVal),
        SearchValue.KindOneofCase.BoolVal   => Conditions.Match(property, value.BoolVal),
        SearchValue.KindOneofCase.NumberVal => Conditions.Match(property, Convert.ToInt64(value.NumberVal)),
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"EQUALS/NOT_EQUALS filter on '{property}' requires a string, bool, or numeric value."))
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj --filter "FullyQualifiedName~QdrantFilterBuilderTests"`
Expected: PASS (all 15 cases).

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs
git commit -m "feat(server): QdrantFilterBuilder translates SearchClause lists into Qdrant Filter"
```

---

### Task 5: Server — wire `Filter` through `SearchNamedAsync`

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IVectorService.cs`
- Modify: `Iverson.Server/Iverson.Vector/QdrantVectorService.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs`, `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs`

**Interfaces:**
- Consumes: `Qdrant.Client.Grpc.Filter` (Task 4 produces these).
- Produces: `IVectorService.SearchNamedAsync(string, string, float[], ulong limit = 10, Filter? filter = null)`. Task 6 and Task 7 consume this.

- [ ] **Step 1: Write the failing test**

Append to `QdrantIntegrationTests.cs` (uses `ApplyCollectionAsync`, not `EnsureCollectionAsync`, since `SearchNamedAsync` requires a named-vector collection):

```csharp
    [Fact]
    public async Task SearchNamedAsync_WithFilter_ReturnsOnlyMatchingPoints()
    {
        var collection = UniqueName();
        await _svc.ApplyCollectionAsync(new CollectionSchema(
            collection,
            [new NamedVector("title_vector", 4)],
            []));

        await _svc.UpsertNamedAsync(collection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = [0.1f, 0.2f, 0.3f, 0.4f] },
            new Dictionary<string, object> { ["wordCount"] = 100L });
        await _svc.UpsertNamedAsync(collection, 2,
            new Dictionary<string, float[]> { ["title_vector"] = [0.1f, 0.2f, 0.3f, 0.4f] },
            new Dictionary<string, object> { ["wordCount"] = 900L });

        var filter = new Filter();
        filter.Must.Add(Conditions.Range("wordCount", new Range { Gt = 500 }));

        var results = await _svc.SearchNamedAsync(collection, "title_vector", [0.1f, 0.2f, 0.3f, 0.4f], 10, filter);

        results.Should().ContainSingle();
        results[0].Id.Should().Be(2);
    }
```

Add `using Qdrant.Client.Grpc;` to `QdrantIntegrationTests.cs`'s using block if not already present (it already uses `Qdrant.Client` — check and add `Qdrant.Client.Grpc` alongside for `Filter`/`Conditions`/`Range`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj --filter "FullyQualifiedName~SearchNamedAsync_WithFilter"`
Expected: FAIL — compile error, `SearchNamedAsync` has no `filter` parameter.

- [ ] **Step 3: Add the parameter**

In `IVectorService.cs`, change the `SearchNamedAsync` signature (add `using Qdrant.Client.Grpc;` at the top of the file):

```csharp
using Qdrant.Client.Grpc;

namespace Iverson.Vector;

public interface IVectorService
{
    // ... EnsureCollectionAsync, ApplyCollectionAsync, UpsertAsync, UpsertNamedAsync, SearchAsync unchanged ...
    Task<IReadOnlyList<VectorSearchResult>> SearchNamedAsync(
        string collectionName,
        string vectorName,
        float[] queryVector,
        ulong limit = 10,
        Filter? filter = null);
    Task DeleteAsync(string collectionName, ulong id);
}
```

In `QdrantVectorService.cs`, update `SearchNamedAsync`:

```csharp
    public async Task<IReadOnlyList<VectorSearchResult>> SearchNamedAsync(
        string collectionName,
        string vectorName,
        float[] queryVector,
        ulong limit = 10,
        Filter? filter = null)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.search_named", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.vector_name", vectorName);
        activity?.SetTag("qdrant.limit", limit);
        activity?.SetTag("qdrant.filtered", filter is not null);

        var results = await client.SearchAsync(
            collectionName,
            queryVector,
            filter:          filter,
            limit:           limit,
            payloadSelector: true,
            vectorName:      vectorName);

        activity?.SetTag("qdrant.result_count", results.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return results.Select(r => new VectorSearchResult(
            r.Id.Num,
            r.Score,
            r.Payload.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.StringValue)
        )).ToList();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj`
Expected: PASS (Docker required for the integration test).

- [ ] **Step 5: Update existing mocked call sites**

The 4-arg mock setups in `ObjectSearchGrpcServiceTests.cs` (`_vector.SearchNamedAsync("articles", "title_vector", fakeVector, Arg.Any<ulong>())`) still compile unchanged since `filter` has a default — no edit needed here; this step is a no-op check. Run: `dotnet build Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` to confirm.
Expected: Build succeeds with no changes required.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Vector/IVectorService.cs Iverson.Server/Iverson.Vector/QdrantVectorService.cs Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs
git commit -m "feat(server): wire Filter parameter through IVectorService.SearchNamedAsync"
```

---

### Task 6: Server — `SearchSimilar` filter validation and wiring

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `QdrantFilterBuilder.Build` (Task 4), `IVectorService.SearchNamedAsync`'s `filter` param (Task 5), `SchemaDescriptor.ScalarColumns`/`FkColumns`.
- Produces: `SearchSimilar` validates every `request.Filter` clause's `property` against the schema, builds a `Filter` via `QdrantFilterBuilder`, camelCases property names before building (to match Task 3's payload-key convention), and passes it to `SearchNamedAsync`.

- [ ] **Step 1: Write the failing tests**

Append to `ObjectSearchGrpcServiceTests.cs` (in the SearchSimilar region):

```csharp
    [Fact]
    public async Task SearchSimilar_WithFilter_PassesTranslatedFilterToVectorService()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync("articles", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "test query", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "AuthorId", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "00000000-0000-0000-0000-000000000001" },
            ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        Filter? captured = null;
        await _vector.Received(1).SearchNamedAsync(
            "articles", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Do<Filter>(f => captured = f));
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchSimilar_FilterOnUnknownProperty_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Nope", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Nope"));
    }
```

Add `using Qdrant.Client.Grpc;` to the test file's using block.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~SearchSimilar_WithFilter|FullyQualifiedName~SearchSimilar_FilterOnUnknownProperty"`
Expected: FAIL — `SearchSimilar` doesn't read `request.Filter` yet.

- [ ] **Step 3: Implement**

In `ObjectSearchGrpcService.cs`, add `using Qdrant.Client.Grpc;` to the top, then replace the `SearchSimilar` method body (lines 81–130):

```csharp
    public override async Task SearchSimilar(
        SearchSimilarRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var vectorDesc = schema.VectorFields.FirstOrDefault(v =>
            string.Equals(v.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' has no [IversonEmbedding] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        Filter? filter = null;
        if (request.Filter.Count > 0)
        {
            var camelCased = request.Filter.Select(c =>
            {
                ValidateFilterProperty(schema, c.Property, "SearchSimilar");
                return new SearchClause
                {
                    Property   = ToCamelCase(c.Property),
                    Operator   = c.Operator,
                    Value      = c.Value,
                    ClauseType = c.ClauseType
                };
            }).ToList();
            filter = QdrantFilterBuilder.Build(camelCased, request.FilterLogic, "SearchSimilar");
        }

        logger.LogInformation("[SearchSimilar] type={Type} property={Prop} topK={K} filtered={Filtered}",
            request.TypeName, request.Property, request.TopK, filter is not null);

        float[] queryVector;
        try
        {
            queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"Embedding service unavailable: {ex.Message}"));
        }

        var vectorName = vectorDesc.PropertyName.ToSnakeCase() + "_vector";
        var topK       = (ulong)Math.Max(1, (int)request.TopK);
        var results    = await vector.SearchNamedAsync(schema.CollectionName, vectorName, queryVector, topK, filter);

        foreach (var r in results)
        {
            var protoStruct = new Struct();
            foreach (var kvp in r.Payload)
                protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);

            await responseStream.WriteAsync(
                new SearchResponse
                {
                    Data    = protoStruct,
                    Score   = (float)r.Score,
                    TraceId = request.TraceId
                },
                context.CancellationToken);
        }
    }
```

Add two private helpers near `RequireSchema`:

```csharp
    private static void ValidateFilterProperty(SchemaDescriptor schema, string property, string rpcName)
    {
        var known = schema.ScalarColumns.Any(c => string.Equals(c.Name, property, StringComparison.OrdinalIgnoreCase))
                 || schema.FkColumns.Any(fk => string.Equals(fk.ColumnName, property, StringComparison.OrdinalIgnoreCase));
        if (!known)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{rpcName}: filter property '{property}' is not a scalar or foreign-key column on '{schema.TypeName}'."));
    }

    private static string ToCamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
```

Note: `Value.ForString(kvp.Value)` at the `protoStruct.Fields` line is pre-existing code (unchanged) — `r.Payload` is still `IReadOnlyDictionary<string,string>` per Task 2's "read-side unchanged" decision, so this line is untouched; only the method signature/body above it changes.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(server): SearchSimilar validates and applies Qdrant filters"
```

---

### Task 7: Server — `SearchChunks` PK-equals-only filter restriction

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `Qdrant.Client.Grpc.Conditions.MatchKeyword`, `schema.KeyColumn.Name`.
- Produces: `SearchChunks` accepts at most one `EQUALS` clause on the type's primary-key property, mapped to a `Match` condition on the chunks collection's `parent_id` payload key; anything else throws `InvalidArgument`.

- [ ] **Step 1: Write the failing tests**

Append to `ObjectSearchGrpcServiceTests.cs` (in the SearchChunks region):

```csharp
    [Fact]
    public async Task SearchChunks_WithPkEqualsFilter_PassesParentIdMatchToVectorService()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("articles_chunks", "body_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-123" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        Filter? captured = null;
        await _vector.Received(1).SearchNamedAsync(
            "articles_chunks", "body_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Do<Filter>(f => captured = f));
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchChunks_FilterOnNonPkProperty_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "AuthorId", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchChunks_MoreThanOneFilterClause_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause { Property = "Id", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "a" } });
        request.Filter.Add(new SearchClause { Property = "Id", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "b" } });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~SearchChunks_WithPkEqualsFilter|FullyQualifiedName~SearchChunks_FilterOnNonPkProperty|FullyQualifiedName~SearchChunks_MoreThanOneFilterClause"`
Expected: FAIL.

- [ ] **Step 3: Implement**

In `ObjectSearchGrpcService.cs`, replace the `SearchChunks` method body (lines 134–184):

```csharp
    public override async Task SearchChunks(
        SearchChunksRequest request,
        IServerStreamWriter<ChunkSearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
            string.Equals(c.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' has no [IversonChunk] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        var filter = BuildChunksFilter(schema, request);

        logger.LogInformation("[SearchChunks] type={Type} property={Prop} topK={K} filtered={Filtered}",
            request.TypeName, request.Property, request.TopK, filter is not null);

        float[] queryVector;
        try
        {
            queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"Embedding service unavailable: {ex.Message}"));
        }

        var vectorName       = chunkDesc.PropertyName.ToSnakeCase() + "_vector";
        var chunksCollection = schema.CollectionName + "_chunks";
        var topK             = (ulong)Math.Max(1, (int)request.TopK);
        var results          = await vector.SearchNamedAsync(chunksCollection, vectorName, queryVector, topK, filter);

        foreach (var r in results)
        {
            r.Payload.TryGetValue("text",      out var chunkText);
            r.Payload.TryGetValue("parent_id", out var parentId);

            await responseStream.WriteAsync(
                new ChunkSearchResponse
                {
                    ParentKey = parentId  ?? string.Empty,
                    ChunkText = chunkText ?? string.Empty,
                    Score     = (float)r.Score,
                    TraceId   = request.TraceId
                },
                context.CancellationToken);
        }
    }
```

Add the private helper near `ValidateFilterProperty`:

```csharp
    private static Filter? BuildChunksFilter(SchemaDescriptor schema, SearchChunksRequest request)
    {
        if (request.Filter.Count == 0) return null;

        if (request.Filter.Count > 1)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "SearchChunks supports at most one filter clause: an EQUALS match on the type's " +
                $"primary-key property ('{schema.KeyColumn.Name}')."));

        var clause = request.Filter[0];
        if (clause.Operator != SearchOperator.Equals || clause.ClauseType != SearchClauseType.Filter)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "SearchChunks only supports a single EQUALS filter clause; other operators and " +
                "MUST_NOT clauses are rejected."));

        if (!string.Equals(clause.Property, schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"SearchChunks filter must target the primary-key property '{schema.KeyColumn.Name}', " +
                $"got '{clause.Property}'."));

        var filter = new Filter();
        filter.Must.Add(Conditions.MatchKeyword("parent_id", clause.Value.StringVal));
        return filter;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(server): SearchChunks restricts filters to a single PK-equals clause"
```

---

### Task 8: Server — Docker-backed integration test for filtered vector search

**Files:**
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs` (new file)

**Interfaces:**
- Consumes: `QdrantContainerFixture` (existing, `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs` — this new test lives in `Iverson.Api.Tests` instead so it can construct a real `ObjectSearchGrpcService` with a mocked embedding service returning deterministic vectors, avoiding a live embedding-model dependency while still exercising real Qdrant filtering end-to-end).

- [ ] **Step 1: Write the failing tests**

Create `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`:

```csharp
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Qdrant.Client;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public sealed class QdrantGrpcContainerFixture : IAsyncLifetime
{
    private const int GrpcPort = 6334;
    private readonly DotNet.Testcontainers.Containers.IContainer _container =
        new ContainerBuilder()
            .WithImage("qdrant/qdrant:v1.13.6")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .WithPortBinding(6333, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(GrpcPort))
            .Build();

    public QdrantVectorService Service { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var client = new QdrantClient(_container.Hostname, _container.GetMappedPublicPort(GrpcPort), https: false);
        Service = new QdrantVectorService(client, NullLogger<QdrantVectorService>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class ObjectSearchVectorIntegrationTests : IClassFixture<QdrantGrpcContainerFixture>
{
    private readonly QdrantVectorService _vector;
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly SchemaRegistry _registry;

    public ObjectSearchVectorIntegrationTests(QdrantGrpcContainerFixture fx)
    {
        _vector = fx.Service;
        var sql = Substitute.For<IPostgresRepository>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
    }

    private static string UniqueName() => "art_" + Guid.NewGuid().ToString("N")[..8];

    private ObjectSearchGrpcService BuildSut() =>
        new(_registry, Substitute.For<IStarRocksRepository>(), _vector, _embedding,
            NullLogger<ObjectSearchGrpcService>.Instance);

    private static (IServerStreamWriter<T> writer, List<T> written) MakeStream<T>()
    {
        var written = new List<T>();
        var writer  = Substitute.For<IServerStreamWriter<T>>();
        writer.WriteAsync(Arg.Do<T>(written.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return (writer, written);
    }

    [Fact]
    public async Task SearchSimilar_WithRangeFilter_ReturnsOnlyMatchingTypedPayload()
    {
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);
        await _vector.ApplyCollectionAsync(new CollectionSchema(
            collection, [new NamedVector("title_vector", 4)], []));

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);

        await _vector.UpsertNamedAsync(collection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = vec },
            new Dictionary<string, object> { ["wordCount"] = 100L });
        await _vector.UpsertNamedAsync(collection, 2,
            new Dictionary<string, float[]> { ["title_vector"] = vec },
            new Dictionary<string, object> { ["wordCount"] = 900L });

        var sut = BuildSut();
        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 10 };
        request.Filter.Add(new SearchClause
        {
            Property = "WordCount", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 500 }, ClauseType = SearchClauseType.Filter
        });

        var (writer, written) = MakeStream<SearchResponse>();
        await sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchChunks_WithPkEqualsFilter_ReturnsOnlyThatParentsChunks()
    {
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);

        var chunksCollection = collection + "_chunks";
        await _vector.ApplyCollectionAsync(new CollectionSchema(
            chunksCollection, [new NamedVector("body_vector", 4)],
            [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]));

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);

        await _vector.UpsertNamedAsync(chunksCollection, 1,
            new Dictionary<string, float[]> { ["body_vector"] = vec },
            new Dictionary<string, object> { ["text"] = "chunk from parent A", ["parent_id"] = "parent-a" });
        await _vector.UpsertNamedAsync(chunksCollection, 2,
            new Dictionary<string, float[]> { ["body_vector"] = vec },
            new Dictionary<string, object> { ["text"] = "chunk from parent B", ["parent_id"] = "parent-b" });

        var sut = BuildSut();
        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 10 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-a" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await sut.SearchChunks(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle();
        written[0].ParentKey.Should().Be("parent-a");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (or error without Docker)**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~ObjectSearchVectorIntegrationTests"`
Expected: FAILs against the pre-Task-6/7 code (this task runs after 6/7 so it should already pass structurally) — if Docker is unavailable in the current environment, expect a container-startup error instead; note that and move on, this test's correctness was validated by Tasks 6–7's unit tests.

- [ ] **Step 3: Run with Docker available**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~ObjectSearchVectorIntegrationTests"`
Expected: PASS (2 tests) once Docker is available.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs
git commit -m "test(server): Docker-backed integration coverage for filtered SearchSimilar/SearchChunks"
```

---

### Task 9: C# client — `Query.Similar`/`Query.Chunks` builders + `EntityCoordinator` wrappers

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search/QuerySimilarBuilder.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search/QueryChunksBuilder.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/QuerySimilarBuilderTests.cs` (new), `QueryChunksBuilderTests.cs` (new)

**Interfaces:**
- Consumes: `SearchSimilarRequest.Filter`/`FilterLogic`, `SearchChunksRequest.Filter`/`FilterLogic` (Task 1), `SearchValueConverter.ToSearchValue` (internal, same assembly).
- Produces: `Query.Similar<T>(Expression<Func<T,object>> property)`, `Query.Chunks<T>(Expression<Func<T,object>> property)`, `QuerySimilarBuilder<T>`/`QueryChunksBuilder<T>` with `.Text(string)`, `.TopK(uint)`, `.Where<TValue>(Expression<Func<T,TValue>>, SearchOperator, TValue)`, `.WithLogic(SearchLogic)`, `.Build(string? traceId = null)`.

- [ ] **Step 1: Write the failing tests**

Create `QuerySimilarBuilderTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class QuerySimilarBuilderTests
{
    private sealed class TestArticle
    {
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
    }

    [Fact]
    public void Build_HappyPath_ProducesExpectedRequest()
    {
        SearchSimilarRequest request = Query.Similar<TestArticle>(a => a.Title)
            .Text("machine learning")
            .TopK(10)
            .Where(a => a.Category, EqualTo, "Tech")
            .Build();

        request.TypeName.Should().Be("TestArticle");
        request.Property.Should().Be("Title");
        request.Query.Should().Be("machine learning");
        request.TopK.Should().Be(10u);
        request.Filter.Should().ContainSingle(c => c.Property == "Category" && c.Operator == SearchOperator.Equals);
    }

    [Fact]
    public void Build_NoFilter_ProducesEmptyFilterList()
    {
        var request = Query.Similar<TestArticle>(a => a.Title).Text("x").Build();
        request.Filter.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SearchOperator.Contains)]
    [InlineData(SearchOperator.StartsWith)]
    [InlineData(SearchOperator.EndsWith)]
    [InlineData(SearchOperator.VectorSimilar)]
    public void Where_UnsupportedOperator_Throws(SearchOperator op)
    {
        var act = () => Query.Similar<TestArticle>(a => a.Title).Where(a => a.Category, op, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithLogic_Or_SetsFilterLogic()
    {
        var request = Query.Similar<TestArticle>(a => a.Title)
            .Where(a => a.Category, EqualTo, "Tech")
            .Where(a => a.Category, EqualTo, "Science")
            .WithLogic(SearchLogic.Or)
            .Build();

        request.FilterLogic.Should().Be(SearchLogic.Or);
    }
}
```

Create `QueryChunksBuilderTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class QueryChunksBuilderTests
{
    private sealed class TestArticle
    {
        public string Id { get; set; } = "";
        public string Body { get; set; } = "";
    }

    [Fact]
    public void Build_HappyPath_ProducesExpectedRequest()
    {
        SearchChunksRequest request = Query.Chunks<TestArticle>(a => a.Body)
            .Text("neural networks")
            .TopK(5)
            .Where(a => a.Id, EqualTo, "parent-123")
            .Build();

        request.TypeName.Should().Be("TestArticle");
        request.Property.Should().Be("Body");
        request.TopK.Should().Be(5u);
        request.Filter.Should().ContainSingle(c => c.Property == "Id" && c.Operator == SearchOperator.Equals);
    }

    [Fact]
    public void Where_NonEqualsOperator_Throws()
    {
        var act = () => Query.Chunks<TestArticle>(a => a.Body).Where(a => a.Id, GreaterThan, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Where_CalledTwice_ThrowsAtBuildTime()
    {
        var builder = Query.Chunks<TestArticle>(a => a.Body)
            .Where(a => a.Id, EqualTo, "a")
            .Where(a => a.Id, EqualTo, "b");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*at most one*");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj --filter "FullyQualifiedName~QuerySimilarBuilderTests|FullyQualifiedName~QueryChunksBuilderTests"`
Expected: FAIL — compile error, `Query.Similar`/`Query.Chunks` don't exist.

- [ ] **Step 3: Implement `QuerySimilarBuilder<T>`**

Create `QuerySimilarBuilder.cs`:

```csharp
using System.Linq.Expressions;
using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent builder for <see cref="SearchSimilarRequest"/> (Qdrant vector similarity search).
/// Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
/// LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR have no Qdrant
/// payload-filter equivalent and are rejected at the point of the <see cref="Where{TValue}"/> call.
/// </summary>
public sealed class QuerySimilarBuilder<T> where T : class
{
    private readonly string _typeName;
    private readonly string _property;
    private string _query = string.Empty;
    private uint _topK = 10;
    private SearchLogic _logic = SearchLogic.And;
    private readonly List<SearchClause> _filter = [];

    internal QuerySimilarBuilder(string typeName, string property)
    {
        _typeName = typeName;
        _property = property;
    }

    public QuerySimilarBuilder<T> Text(string query) { _query = query; return this; }
    public QuerySimilarBuilder<T> TopK(uint topK) { _topK = topK; return this; }
    public QuerySimilarBuilder<T> WithLogic(SearchLogic logic) { _logic = logic; return this; }

    public QuerySimilarBuilder<T> Where<TValue>(
        Expression<Func<T, TValue>> property, SearchOperator op, TValue value)
    {
        if (op is SearchOperator.Contains or SearchOperator.StartsWith
                or SearchOperator.EndsWith or SearchOperator.VectorSimilar)
            throw new InvalidOperationException(
                $"Operator '{op}' is not supported by SearchSimilar filters. Supported operators: " +
                "Equals, NotEquals, GreaterThan, LessThan, GreaterThanOrEquals, LessThanOrEquals, In.");

        _filter.Add(new SearchClause
        {
            Property   = PropertyName(property),
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        });
        return this;
    }

    public SearchSimilarRequest Build(string? traceId = null)
    {
        var request = new SearchSimilarRequest
        {
            TypeName    = _typeName,
            Property    = _property,
            Query       = _query,
            TopK        = _topK,
            FilterLogic = _logic,
            TraceId     = traceId ?? string.Empty
        };
        request.Filter.AddRange(_filter);
        return request;
    }

    private static string PropertyName<TValue>(Expression<Func<T, TValue>> expr) =>
        expr.Body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Title");
}
```

- [ ] **Step 4: Implement `QueryChunksBuilder<T>`**

Create `QueryChunksBuilder.cs`:

```csharp
using System.Linq.Expressions;
using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent builder for <see cref="SearchChunksRequest"/> (Qdrant chunk/RAG search).
/// Supports at most one filter clause: an EQUALS match on the entity's primary-key property —
/// the chunks collection's payload only indexes <c>parent_id</c>, so richer filtering isn't
/// available (mirrors the server-side restriction in <c>ObjectSearchGrpcService.SearchChunks</c>).
/// </summary>
public sealed class QueryChunksBuilder<T> where T : class
{
    private readonly string _typeName;
    private readonly string _property;
    private string _query = string.Empty;
    private uint _topK = 10;
    private SearchClause? _filter;

    internal QueryChunksBuilder(string typeName, string property)
    {
        _typeName = typeName;
        _property = property;
    }

    public QueryChunksBuilder<T> Text(string query) { _query = query; return this; }
    public QueryChunksBuilder<T> TopK(uint topK) { _topK = topK; return this; }

    public QueryChunksBuilder<T> Where<TValue>(
        Expression<Func<T, TValue>> property, SearchOperator op, TValue value)
    {
        if (op != SearchOperator.Equals)
            throw new InvalidOperationException(
                $"SearchChunks only supports an Equals filter on the primary-key property; got '{op}'.");
        if (_filter is not null)
            throw new InvalidOperationException("SearchChunks supports at most one filter clause.");

        _filter = new SearchClause
        {
            Property   = PropertyName(property),
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        };
        return this;
    }

    public SearchChunksRequest Build(string? traceId = null)
    {
        var request = new SearchChunksRequest
        {
            TypeName = _typeName,
            Property = _property,
            Query    = _query,
            TopK     = _topK,
            TraceId  = traceId ?? string.Empty
        };
        if (_filter is not null) request.Filter.Add(_filter);
        return request;
    }

    private static string PropertyName<TValue>(Expression<Func<T, TValue>> expr) =>
        expr.Body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Body");
}
```

- [ ] **Step 5: Add entry points to `Query`**

In `Query.cs`, add two methods (keep the existing `For`/`GroupBy`/`Pipeline` methods unchanged — the `Pipeline` entry-point consolidation happens in Task 22, not here):

```csharp
    public static QuerySimilarBuilder<T> Similar<T>(Expression<Func<T, object>> property) where T : class =>
        new(typeof(T).Name, PropertyNameObj(property));

    public static QueryChunksBuilder<T> Chunks<T>(Expression<Func<T, object>> property) where T : class =>
        new(typeof(T).Name, PropertyNameObj(property));

    private static string PropertyNameObj<TSource>(Expression<Func<TSource, object>> expr) =>
        expr.Body switch
        {
            MemberExpression member => member.Member.Name,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
                when unary.Operand is MemberExpression innerMember => innerMember.Member.Name,
            _ => throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Title")
        };
```

Add `using System.Linq.Expressions;` to `Query.cs`'s top if not already present.

- [ ] **Step 6: Run the builder tests**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj --filter "FullyQualifiedName~QuerySimilarBuilderTests|FullyQualifiedName~QueryChunksBuilderTests"`
Expected: PASS.

- [ ] **Step 7: Add `EntityCoordinator` wrappers, write their tests**

`EntityCoordinator.PipelineAsync`/`PipelineAsync<TResult>` have zero existing test coverage (confirmed — no test file exercises them), so this step also creates the first test file for `EntityCoordinator<T>`'s streaming helpers, covering the new methods only (the pre-existing `PipelineAsync` gap is closed separately in Task 22).

Create `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorVectorSearchTests.cs`:

```csharp
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

public class EntityCoordinatorVectorSearchTests
{
    private sealed class TestArticle
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    private static AsyncServerStreamingCall<TResponse> MakeCall<TResponse>(IReadOnlyList<TResponse> items)
    {
        var stream = Substitute.For<IAsyncStreamReader<TResponse>>();
        var queue  = new Queue<TResponse>(items);
        stream.MoveNext(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(queue.Count > 0));
        stream.Current.Returns(_ => queue.Count > 0 ? queue.Peek() : default!);
        stream.When(s => s.MoveNext(Arg.Any<CancellationToken>())).Do(_ => { if (queue.Count > 0) queue.Dequeue(); });
        return new AsyncServerStreamingCall<TResponse>(
            stream, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    [Fact]
    public async Task SearchSimilarAsync_StreamsResults()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var responses = new List<SearchResponse> { new() { Score = 0.9f, Data = new Struct() } };
        search.SearchSimilar(Arg.Any<SearchSimilarRequest>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var results = new List<SearchResult<TestArticle>>();
        await foreach (var r in coordinator.SearchSimilarAsync(Query.Similar<TestArticle>(a => a.Title).Text("q")))
            results.Add(r);

        results.Should().ContainSingle();
    }
}
```

Note: this test needs a `TestCoordinatorFactory.Create<T>` helper to construct an `EntityCoordinator<T>` with a mocked `ObjectSearchServiceClient` — check whether `EntityCoordinator<T>`'s constructor is `internal`/`public` and what else it requires (a `SchemaDescriptor`/entity registration, a logger) by reading `EntityCoordinator.cs`'s constructor before writing this helper; if construction requires more than the search client, add a minimal `TestCoordinatorFactory.cs` in the test project mirroring however `Iverson.Client.Core.Tests`' other test files (`SchemaRegistrarTests.cs`, `GraphAssemblerTests.cs`) already construct their subjects under test.

- [ ] **Step 8: Add `SearchSimilarAsync`/`SearchChunksAsync` to `EntityCoordinator<T>`**

In `EntityCoordinator.cs`, add two methods alongside `SearchAsync`/`PipelineAsync`:

```csharp
    public async IAsyncEnumerable<SearchResult<T>> SearchSimilarAsync(
        QuerySimilarBuilder<T> query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = query.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.SearchSimilar {Entity} property={Property}",
            _descriptor.EntityName, request.Property);

        var stream = search.SearchSimilar(request, cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            var entity = StructConverter.FromStruct<T>(response.Data);
            if (entity is not null) yield return new SearchResult<T>(entity, response.Score);
        }
    }

    public async IAsyncEnumerable<ChunkSearchResponse> SearchChunksAsync(
        QueryChunksBuilder<T> query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = query.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.SearchChunks {Entity} property={Property}",
            _descriptor.EntityName, request.Property);

        var stream = search.SearchChunks(request, cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
            yield return response;
    }
```

(`SearchChunksAsync` yields the raw `ChunkSearchResponse` rather than projecting onto `T`, since a chunk result — `ParentKey`/`ChunkText`/`Score` — doesn't correspond to a full `T` instance the way `SearchResponse.Data` does.)

- [ ] **Step 9: Run the full C# client test suites**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj && dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add Iverson.Clients/DotNet/Iverson.Client.Search/QuerySimilarBuilder.cs Iverson.Clients/DotNet/Iverson.Client.Search/QueryChunksBuilder.cs \
        Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs \
        Iverson.Clients/DotNet/Iverson.Client.Search.Tests/QuerySimilarBuilderTests.cs Iverson.Clients/DotNet/Iverson.Client.Search.Tests/QueryChunksBuilderTests.cs \
        Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorVectorSearchTests.cs
git commit -m "feat(dotnet): Query.Similar/Query.Chunks builders + EntityCoordinator vector-search wrappers"
```

---

### Task 10: Java client — `SimilarBuilder`/`ChunksBuilder`

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/SimilarBuilder.java`
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/ChunksBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/SimilarBuilderTest.java` (new), `ChunksBuilderTest.java` (new)

**Interfaces:**
- Consumes: `iverson.ObjectSearch.SearchSimilarRequest`/`SearchChunksRequest` (regenerated by Task 1's `mvn compile`), package-private `SearchValues.toSearchValue(Object)`.
- Produces: `Query.similar(String typeName, String property)`, `Query.chunks(String typeName, String property)`, both string-based like `Query.groupBy`/`Query.pipeline`.

- [ ] **Step 1: Write the failing tests**

Create `SimilarBuilderTest.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSimilarRequest;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class SimilarBuilderTest {

    @Test
    void build_happyPath_producesExpectedRequest() {
        SearchSimilarRequest req = Query.similar("Article", "Title")
            .text("machine learning")
            .topK(10)
            .where("Category", SearchOperator.EQUALS, "Tech")
            .build();

        assertEquals("Article", req.getTypeName());
        assertEquals("Title", req.getProperty());
        assertEquals("machine learning", req.getQuery());
        assertEquals(10, req.getTopK());
        assertEquals(1, req.getFilterCount());
        assertEquals("Category", req.getFilter(0).getProperty());
    }

    @Test
    void where_containsOperator_throws() {
        SimilarBuilder b = Query.similar("Article", "Title");
        assertThrows(IllegalStateException.class,
            () -> b.where("Category", SearchOperator.CONTAINS, "x"));
    }

    @Test
    void where_vectorSimilarOperator_throws() {
        SimilarBuilder b = Query.similar("Article", "Title");
        assertThrows(IllegalStateException.class,
            () -> b.where("Category", SearchOperator.VECTOR_SIMILAR, "x"));
    }
}
```

Create `ChunksBuilderTest.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchChunksRequest;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class ChunksBuilderTest {

    @Test
    void build_happyPath_producesExpectedRequest() {
        SearchChunksRequest req = Query.chunks("Article", "Body")
            .text("neural networks")
            .topK(5)
            .where("Id", SearchOperator.EQUALS, "parent-123")
            .build();

        assertEquals("Article", req.getTypeName());
        assertEquals("Body", req.getProperty());
        assertEquals(5, req.getTopK());
        assertEquals(1, req.getFilterCount());
        assertEquals("Id", req.getFilter(0).getProperty());
    }

    @Test
    void where_nonEqualsOperator_throws() {
        ChunksBuilder b = Query.chunks("Article", "Body");
        assertThrows(IllegalStateException.class,
            () -> b.where("Id", SearchOperator.GREATER_THAN, "x"));
    }

    @Test
    void where_calledTwice_throwsOnSecondCall() {
        ChunksBuilder b = Query.chunks("Article", "Body").where("Id", SearchOperator.EQUALS, "a");
        assertThrows(IllegalStateException.class,
            () -> b.where("Id", SearchOperator.EQUALS, "b"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Java/client && mvn -q test -Dtest=SimilarBuilderTest,ChunksBuilderTest`
Expected: FAIL — compile error, `Query.similar`/`Query.chunks` don't exist.

- [ ] **Step 3: Implement `SimilarBuilder`**

Create `SimilarBuilder.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSimilarRequest;

import java.util.ArrayList;
import java.util.List;

/**
 * Fluent builder for a {@link SearchSimilarRequest} (Qdrant vector similarity search).
 * Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
 * LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR are rejected.
 */
public final class SimilarBuilder {
    private final String typeName;
    private final String property;
    private String query = "";
    private int topK = 10;
    private SearchLogic logic = SearchLogic.AND;
    private final List<SearchClause> filter = new ArrayList<>();

    SimilarBuilder(String typeName, String property) {
        this.typeName = typeName;
        this.property = property;
    }

    public SimilarBuilder text(String query) { this.query = query; return this; }
    public SimilarBuilder topK(int topK) { this.topK = topK; return this; }
    public SimilarBuilder withLogic(SearchLogic logic) { this.logic = logic; return this; }

    public SimilarBuilder where(String field, SearchOperator op, Object value) {
        if (op == SearchOperator.CONTAINS || op == SearchOperator.STARTS_WITH
                || op == SearchOperator.ENDS_WITH || op == SearchOperator.VECTOR_SIMILAR) {
            throw new IllegalStateException("Operator " + op + " is not supported by SearchSimilar "
                + "filters. Supported operators: EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, "
                + "GREATER_THAN_OR_EQUALS, LESS_THAN_OR_EQUALS, IN.");
        }
        filter.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    public SearchSimilarRequest build() { return build(""); }

    public SearchSimilarRequest build(String traceId) {
        SearchSimilarRequest.Builder builder = SearchSimilarRequest.newBuilder()
            .setTypeName(typeName)
            .setProperty(property)
            .setQuery(query)
            .setTopK(topK)
            .setFilterLogic(logic)
            .setTraceId(traceId);
        builder.addAllFilter(filter);
        return builder.build();
    }
}
```

- [ ] **Step 4: Implement `ChunksBuilder`**

Create `ChunksBuilder.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.SearchChunksRequest;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchOperator;

/**
 * Fluent builder for a {@link SearchChunksRequest} (Qdrant chunk/RAG search). Supports at most
 * one filter clause: an EQUALS match on the entity's primary-key property.
 */
public final class ChunksBuilder {
    private final String typeName;
    private final String property;
    private String query = "";
    private int topK = 10;
    private SearchClause filter;

    ChunksBuilder(String typeName, String property) {
        this.typeName = typeName;
        this.property = property;
    }

    public ChunksBuilder text(String query) { this.query = query; return this; }
    public ChunksBuilder topK(int topK) { this.topK = topK; return this; }

    public ChunksBuilder where(String field, SearchOperator op, Object value) {
        if (op != SearchOperator.EQUALS) {
            throw new IllegalStateException(
                "SearchChunks only supports an EQUALS filter on the primary-key property; got " + op + ".");
        }
        if (filter != null) {
            throw new IllegalStateException("SearchChunks supports at most one filter clause.");
        }
        filter = SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build();
        return this;
    }

    public SearchChunksRequest build() { return build(""); }

    public SearchChunksRequest build(String traceId) {
        SearchChunksRequest.Builder builder = SearchChunksRequest.newBuilder()
            .setTypeName(typeName)
            .setProperty(property)
            .setQuery(query)
            .setTopK(topK)
            .setTraceId(traceId);
        if (filter != null) builder.addFilter(filter);
        return builder.build();
    }
}
```

- [ ] **Step 5: Add entry points to `Query`**

In `Query.java`, add after the `pipeline` method:

```java
    /**
     * Creates a {@link SimilarBuilder} for Qdrant vector similarity search on the given
     * embedded property.
     */
    public static SimilarBuilder similar(String typeName, String property) {
        return new SimilarBuilder(typeName, property);
    }

    /**
     * Creates a {@link ChunksBuilder} for Qdrant chunk/RAG search on the given chunked property.
     */
    public static ChunksBuilder chunks(String typeName, String property) {
        return new ChunksBuilder(typeName, property);
    }
```

- [ ] **Step 6: Run tests**

Run: `cd Iverson.Clients/Java/client && mvn -q test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/SimilarBuilder.java \
        Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/ChunksBuilder.java \
        Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/SimilarBuilderTest.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/ChunksBuilderTest.java
git commit -m "feat(java): SimilarBuilder/ChunksBuilder for Qdrant vector search"
```

---

### Task 11: Python client — `SimilarBuilder`/`ChunksBuilder`

**Files:**
- Create: `Iverson.Clients/Python/iverson_client/vector_search.py`
- Modify: `Iverson.Clients/Python/iverson_client/__init__.py`
- Test: `Iverson.Clients/Python/tests/test_vector_search.py` (new)

**Interfaces:**
- Consumes: `iverson_client.generated.object_search_pb2.SearchSimilarRequest`/`SearchChunksRequest` (regenerated by Task 1), `iverson_client.search._to_search_value` (already imported cross-module by `group_by.py` using this exact pattern).
- Produces: `similar(type_name, property) -> SimilarBuilder`, `chunks(type_name, property) -> ChunksBuilder`.

- [ ] **Step 1: Write the failing tests**

Create `test_vector_search.py`:

```python
import pytest

from iverson_client.generated import object_search_pb2 as pb
from iverson_client.vector_search import similar, chunks


def test_similar_build_happy_path_produces_expected_request():
    req = (
        similar("Article", "Title")
        .text("machine learning")
        .top_k(10)
        .where("Category", pb.EQUALS, "Tech")
        .build()
    )
    assert req.type_name == "Article"
    assert req.property == "Title"
    assert req.query == "machine learning"
    assert req.top_k == 10
    assert len(req.filter) == 1
    assert req.filter[0].property == "Category"


def test_similar_where_contains_operator_raises():
    b = similar("Article", "Title")
    with pytest.raises(ValueError):
        b.where("Category", pb.CONTAINS, "x")


def test_similar_where_vector_similar_operator_raises():
    b = similar("Article", "Title")
    with pytest.raises(ValueError):
        b.where("Category", pb.VECTOR_SIMILAR, "x")


def test_chunks_build_happy_path_produces_expected_request():
    req = (
        chunks("Article", "Body")
        .text("neural networks")
        .top_k(5)
        .where("Id", pb.EQUALS, "parent-123")
        .build()
    )
    assert req.type_name == "Article"
    assert req.property == "Body"
    assert req.top_k == 5
    assert len(req.filter) == 1
    assert req.filter[0].property == "Id"


def test_chunks_where_non_equals_operator_raises():
    b = chunks("Article", "Body")
    with pytest.raises(ValueError):
        b.where("Id", pb.GREATER_THAN, "x")


def test_chunks_where_called_twice_raises_on_second_call():
    b = chunks("Article", "Body").where("Id", pb.EQUALS, "a")
    with pytest.raises(ValueError):
        b.where("Id", pb.EQUALS, "b")
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Python && python -m pytest tests/test_vector_search.py -q`
Expected: FAIL — `iverson_client.vector_search` module doesn't exist.

- [ ] **Step 3: Implement `vector_search.py`**

Create `Iverson.Clients/Python/iverson_client/vector_search.py`:

```python
"""
Fluent builders for Qdrant vector search (SearchSimilar/SearchChunks).
"""
from __future__ import annotations

from typing import Any, List, Optional

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value

_UNSUPPORTED_SIMILAR_OPERATORS = {
    _pb.CONTAINS, _pb.STARTS_WITH, _pb.ENDS_WITH, _pb.VECTOR_SIMILAR,
}


class SimilarBuilder:
    """Fluent builder for a ``SearchSimilarRequest`` (Qdrant vector similarity search).

    Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
    LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR are rejected.
    """

    def __init__(self, type_name: str, property: str) -> None:
        self._type_name = type_name
        self._property = property
        self._query = ""
        self._top_k = 10
        self._logic = _pb.AND
        self._filter: List[_pb.SearchClause] = []

    def text(self, query: str) -> "SimilarBuilder":
        self._query = query
        return self

    def top_k(self, top_k: int) -> "SimilarBuilder":
        self._top_k = top_k
        return self

    def with_logic(self, logic: int) -> "SimilarBuilder":
        self._logic = logic
        return self

    def where(self, field: str, op: int, value: Any) -> "SimilarBuilder":
        if op in _UNSUPPORTED_SIMILAR_OPERATORS:
            raise ValueError(
                f"Operator {op} is not supported by SearchSimilar filters. Supported operators: "
                "EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS, "
                "LESS_THAN_OR_EQUALS, IN.")
        self._filter.append(_pb.SearchClause(
            property=field, operator=op, value=_to_search_value(value), clause_type=_pb.FILTER))
        return self

    def build(self, trace_id: str = "") -> _pb.SearchSimilarRequest:
        return _pb.SearchSimilarRequest(
            type_name=self._type_name, property=self._property, query=self._query,
            top_k=self._top_k, filter=self._filter, filter_logic=self._logic, trace_id=trace_id)


class ChunksBuilder:
    """Fluent builder for a ``SearchChunksRequest``. At most one EQUALS filter on the PK property."""

    def __init__(self, type_name: str, property: str) -> None:
        self._type_name = type_name
        self._property = property
        self._query = ""
        self._top_k = 10
        self._filter: Optional[_pb.SearchClause] = None

    def text(self, query: str) -> "ChunksBuilder":
        self._query = query
        return self

    def top_k(self, top_k: int) -> "ChunksBuilder":
        self._top_k = top_k
        return self

    def where(self, field: str, op: int, value: Any) -> "ChunksBuilder":
        if op != _pb.EQUALS:
            raise ValueError(
                f"SearchChunks only supports an EQUALS filter on the primary-key property; got {op}.")
        if self._filter is not None:
            raise ValueError("SearchChunks supports at most one filter clause.")
        self._filter = _pb.SearchClause(
            property=field, operator=op, value=_to_search_value(value), clause_type=_pb.FILTER)
        return self

    def build(self, trace_id: str = "") -> _pb.SearchChunksRequest:
        filters = [self._filter] if self._filter is not None else []
        return _pb.SearchChunksRequest(
            type_name=self._type_name, property=self._property, query=self._query,
            top_k=self._top_k, filter=filters, trace_id=trace_id)


def similar(type_name: str, property: str) -> SimilarBuilder:
    """Start a ``SimilarBuilder`` for the given entity type and embedded property."""
    return SimilarBuilder(type_name, property)


def chunks(type_name: str, property: str) -> ChunksBuilder:
    """Start a ``ChunksBuilder`` for the given entity type and chunked property."""
    return ChunksBuilder(type_name, property)
```

- [ ] **Step 4: Export from `__init__.py`**

In `iverson_client/__init__.py`, add the import and `__all__` entries:

```python
from iverson_client.vector_search import SimilarBuilder, ChunksBuilder, similar, chunks
```

and add `"SimilarBuilder", "ChunksBuilder", "similar", "chunks",` to the `__all__` list.

- [ ] **Step 5: Run tests**

Run: `cd Iverson.Clients/Python && python -m pytest -q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Python/iverson_client/vector_search.py Iverson.Clients/Python/iverson_client/__init__.py Iverson.Clients/Python/tests/test_vector_search.py
git commit -m "feat(python): SimilarBuilder/ChunksBuilder for Qdrant vector search"
```

---

### Task 12: TypeScript client — `SimilarBuilder`/`ChunksBuilder`

**Files:**
- Create: `Iverson.Clients/TypeScript/src/vector-search.ts`
- Modify: `Iverson.Clients/TypeScript/src/index.ts`
- Test: `Iverson.Clients/TypeScript/tests/vector-search.test.ts` (new)

**Interfaces:**
- Consumes: `SearchSimilarRequest`/`SearchChunksRequest`/`SearchClause`/`SearchOperator`/`SearchLogic`/`SearchClauseType` from `../generated/object_search.js` (regenerated by Task 1), `toSearchValue` from `./search.js`.
- Produces: `similar(typeName, property) -> SimilarBuilder`, `chunks(typeName, property) -> ChunksBuilder`.

- [ ] **Step 1: Write the failing tests**

Create `vector-search.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { similar, chunks } from '../src/vector-search.js';
import { SearchOperator } from '../src/search.js';

describe('SimilarBuilder', () => {
    it('build happy path produces expected request', () => {
        const req = similar('Article', 'Title')
            .text('machine learning')
            .topK(10)
            .where('Category', SearchOperator.EQUALS, 'Tech')
            .build();

        expect(req.typeName).toBe('Article');
        expect(req.property).toBe('Title');
        expect(req.query).toBe('machine learning');
        expect(req.topK).toBe(10);
        expect(req.filter).toHaveLength(1);
        expect(req.filter[0].property).toBe('Category');
    });

    it('where throws on CONTAINS operator', () => {
        const b = similar('Article', 'Title');
        expect(() => b.where('Category', SearchOperator.CONTAINS, 'x')).toThrow();
    });

    it('where throws on VECTOR_SIMILAR operator', () => {
        const b = similar('Article', 'Title');
        expect(() => b.where('Category', SearchOperator.VECTOR_SIMILAR, 'x')).toThrow();
    });
});

describe('ChunksBuilder', () => {
    it('build happy path produces expected request', () => {
        const req = chunks('Article', 'Body')
            .text('neural networks')
            .topK(5)
            .where('Id', SearchOperator.EQUALS, 'parent-123')
            .build();

        expect(req.typeName).toBe('Article');
        expect(req.property).toBe('Body');
        expect(req.topK).toBe(5);
        expect(req.filter).toHaveLength(1);
        expect(req.filter[0].property).toBe('Id');
    });

    it('where throws on non-EQUALS operator', () => {
        const b = chunks('Article', 'Body');
        expect(() => b.where('Id', SearchOperator.GREATER_THAN, 'x')).toThrow();
    });

    it('where throws on a second call', () => {
        const b = chunks('Article', 'Body').where('Id', SearchOperator.EQUALS, 'a');
        expect(() => b.where('Id', SearchOperator.EQUALS, 'b')).toThrow();
    });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/TypeScript && npx vitest run tests/vector-search.test.ts`
Expected: FAIL — `../src/vector-search.js` doesn't exist.

- [ ] **Step 3: Implement `vector-search.ts`**

Create `Iverson.Clients/TypeScript/src/vector-search.ts`:

```typescript
/**
 * Fluent builders for Qdrant vector search (SearchSimilar/SearchChunks).
 */
import {
    SearchChunksRequest,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchSimilarRequest,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

const UNSUPPORTED_SIMILAR_OPERATORS = new Set([
    SearchOperator.CONTAINS,
    SearchOperator.STARTS_WITH,
    SearchOperator.ENDS_WITH,
    SearchOperator.VECTOR_SIMILAR,
]);

/**
 * Fluent builder for a SearchSimilarRequest (Qdrant vector similarity search).
 * Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
 * LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR are rejected.
 */
export class SimilarBuilder {
    private _query = '';
    private _topK = 10;
    private _logic: SearchLogic = SearchLogic.AND;
    private _filter: SearchClause[] = [];

    constructor(private readonly typeName: string, private readonly property: string) {}

    text(query: string): this { this._query = query; return this; }
    topK(topK: number): this { this._topK = topK; return this; }
    withLogic(logic: SearchLogic): this { this._logic = logic; return this; }

    where(field: string, op: SearchOperator, value: unknown): this {
        if (UNSUPPORTED_SIMILAR_OPERATORS.has(op)) {
            throw new Error(
                `Operator ${op} is not supported by SearchSimilar filters. Supported operators: ` +
                'EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS, ' +
                'LESS_THAN_OR_EQUALS, IN.');
        }
        this._filter.push({
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.FILTER,
        });
        return this;
    }

    build(traceId = ''): SearchSimilarRequest {
        return {
            typeName: this.typeName,
            property: this.property,
            query: this._query,
            topK: this._topK,
            filter: this._filter,
            filterLogic: this._logic,
            traceId,
        };
    }
}

/**
 * Fluent builder for a SearchChunksRequest. Supports at most one filter clause: an EQUALS
 * match on the entity's primary-key property.
 */
export class ChunksBuilder {
    private _query = '';
    private _topK = 10;
    private _filter?: SearchClause;

    constructor(private readonly typeName: string, private readonly property: string) {}

    text(query: string): this { this._query = query; return this; }
    topK(topK: number): this { this._topK = topK; return this; }

    where(field: string, op: SearchOperator, value: unknown): this {
        if (op !== SearchOperator.EQUALS) {
            throw new Error(`SearchChunks only supports an EQUALS filter on the primary-key property; got ${op}.`);
        }
        if (this._filter !== undefined) {
            throw new Error('SearchChunks supports at most one filter clause.');
        }
        this._filter = {
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.FILTER,
        };
        return this;
    }

    build(traceId = ''): SearchChunksRequest {
        return {
            typeName: this.typeName,
            property: this.property,
            query: this._query,
            topK: this._topK,
            filter: this._filter !== undefined ? [this._filter] : [],
            traceId,
        };
    }
}

export function similar(typeName: string, property: string): SimilarBuilder {
    return new SimilarBuilder(typeName, property);
}

export function chunks(typeName: string, property: string): ChunksBuilder {
    return new ChunksBuilder(typeName, property);
}
```

- [ ] **Step 4: Export from `index.ts`**

In `index.ts`, add after the `pipeline` export line:

```typescript
export { SimilarBuilder, ChunksBuilder, similar, chunks } from './vector-search.js';
```

- [ ] **Step 5: Run tests**

Run: `cd Iverson.Clients/TypeScript && npx vitest run`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/TypeScript/src/vector-search.ts Iverson.Clients/TypeScript/src/index.ts Iverson.Clients/TypeScript/tests/vector-search.test.ts
git commit -m "feat(typescript): SimilarBuilder/ChunksBuilder for Qdrant vector search"
```

---

### Task 13: Go client — `SimilarBuilder`/`ChunksBuilder`

**Files:**
- Create: `Iverson.Clients/Go/iverson/vector_search.go`
- Test: `Iverson.Clients/Go/iverson_test/vector_search_test.go` (new)

**Interfaces:**
- Consumes: `pb.SearchSimilarRequest`/`SearchChunksRequest`/`SearchClause`/`SearchOperator`/`SearchLogic`/`SearchClauseType` (`github.com/iverson/clients/go/generated`, regenerated by Task 1), the package-private `toSearchValue(interface{}) *pb.SearchValue` helper (`search.go:213`, same package `iverson`).
- Produces: `NewSimilar(typeName, property string) *SimilarBuilder`, `NewChunks(typeName, property string) *ChunksBuilder`, matching the `NewQuery`/`NewGroupBy`/`NewPipeline` naming convention.

- [ ] **Step 1: Write the failing tests**

Create `vector_search_test.go`:

```go
package iverson_test

import (
	"testing"

	"github.com/iverson/clients/go/iverson"
	pb "github.com/iverson/clients/go/generated"
)

func TestSimilarBuild_HappyPath_ProducesExpectedRequest(t *testing.T) {
	req, err := iverson.NewSimilar("Article", "Title").
		Text("machine learning").
		TopK(10).
		Where("Category", pb.SearchOperator_EQUALS, "Tech").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TypeName != "Article" || req.Property != "Title" || req.Query != "machine learning" || req.TopK != 10 {
		t.Errorf("unexpected request: %+v", req)
	}
	if len(req.Filter) != 1 || req.Filter[0].Property != "Category" {
		t.Errorf("unexpected filter: %+v", req.Filter)
	}
}

func TestSimilarWhere_ContainsOperator_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewSimilar("Article", "Title").
		Where("Category", pb.SearchOperator_CONTAINS, "x").
		Build()
	if err == nil {
		t.Fatal("expected an error for CONTAINS operator")
	}
}

func TestSimilarWhere_VectorSimilarOperator_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewSimilar("Article", "Title").
		Where("Category", pb.SearchOperator_VECTOR_SIMILAR, "x").
		Build()
	if err == nil {
		t.Fatal("expected an error for VECTOR_SIMILAR operator")
	}
}

func TestChunksBuild_HappyPath_ProducesExpectedRequest(t *testing.T) {
	req, err := iverson.NewChunks("Article", "Body").
		Text("neural networks").
		TopK(5).
		Where("Id", pb.SearchOperator_EQUALS, "parent-123").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TypeName != "Article" || req.Property != "Body" || req.TopK != 5 {
		t.Errorf("unexpected request: %+v", req)
	}
	if len(req.Filter) != 1 || req.Filter[0].Property != "Id" {
		t.Errorf("unexpected filter: %+v", req.Filter)
	}
}

func TestChunksWhere_NonEqualsOperator_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewChunks("Article", "Body").
		Where("Id", pb.SearchOperator_GREATER_THAN, "x").
		Build()
	if err == nil {
		t.Fatal("expected an error for non-EQUALS operator")
	}
}

func TestChunksWhere_CalledTwice_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewChunks("Article", "Body").
		Where("Id", pb.SearchOperator_EQUALS, "a").
		Where("Id", pb.SearchOperator_EQUALS, "b").
		Build()
	if err == nil {
		t.Fatal("expected an error for a second filter clause")
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Go && go test ./iverson_test/... -run "TestSimilar|TestChunks"`
Expected: FAIL — `iverson.NewSimilar`/`iverson.NewChunks` don't exist.

- [ ] **Step 3: Implement `vector_search.go`**

Create `Iverson.Clients/Go/iverson/vector_search.go`:

```go
package iverson

import (
	"fmt"

	pb "github.com/iverson/clients/go/generated"
)

// SimilarBuilder builds a SearchSimilarRequest (Qdrant vector similarity search) using a
// fluent API. Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN,
// GREATER_THAN_OR_EQUALS, LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/
// VECTOR_SIMILAR are rejected at Build() time.
type SimilarBuilder struct {
	typeName string
	property string
	query    string
	topK     uint32
	logic    pb.SearchLogic
	filter   []*pb.SearchClause
	err      error
}

// NewSimilar creates a SimilarBuilder for the given entity type and embedded property.
func NewSimilar(typeName, property string) *SimilarBuilder {
	return &SimilarBuilder{typeName: typeName, property: property, topK: 10, logic: pb.SearchLogic_AND}
}

func (s *SimilarBuilder) Text(query string) *SimilarBuilder { s.query = query; return s }
func (s *SimilarBuilder) TopK(topK uint32) *SimilarBuilder  { s.topK = topK; return s }
func (s *SimilarBuilder) WithLogic(logic pb.SearchLogic) *SimilarBuilder { s.logic = logic; return s }

func (s *SimilarBuilder) Where(field string, op pb.SearchOperator, value interface{}) *SimilarBuilder {
	switch op {
	case pb.SearchOperator_CONTAINS, pb.SearchOperator_STARTS_WITH,
		pb.SearchOperator_ENDS_WITH, pb.SearchOperator_VECTOR_SIMILAR:
		s.err = fmt.Errorf("operator %v is not supported by SearchSimilar filters", op)
		return s
	}
	s.filter = append(s.filter, &pb.SearchClause{
		Property: field, Operator: op, Value: toSearchValue(value), ClauseType: pb.SearchClauseType_FILTER,
	})
	return s
}

func (s *SimilarBuilder) Build(traceId ...string) (*pb.SearchSimilarRequest, error) {
	if s.err != nil {
		return nil, s.err
	}
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.SearchSimilarRequest{
		TypeName: s.typeName, Property: s.property, Query: s.query, TopK: s.topK,
		Filter: s.filter, FilterLogic: s.logic, TraceId: id,
	}, nil
}

// ChunksBuilder builds a SearchChunksRequest (Qdrant chunk/RAG search). Supports at most one
// filter clause: an EQUALS match on the entity's primary-key property.
type ChunksBuilder struct {
	typeName string
	property string
	query    string
	topK     uint32
	filter   *pb.SearchClause
	err      error
}

// NewChunks creates a ChunksBuilder for the given entity type and chunked property.
func NewChunks(typeName, property string) *ChunksBuilder {
	return &ChunksBuilder{typeName: typeName, property: property, topK: 10}
}

func (c *ChunksBuilder) Text(query string) *ChunksBuilder { c.query = query; return c }
func (c *ChunksBuilder) TopK(topK uint32) *ChunksBuilder  { c.topK = topK; return c }

func (c *ChunksBuilder) Where(field string, op pb.SearchOperator, value interface{}) *ChunksBuilder {
	if op != pb.SearchOperator_EQUALS {
		c.err = fmt.Errorf("SearchChunks only supports an EQUALS filter on the primary-key property; got %v", op)
		return c
	}
	if c.filter != nil {
		c.err = fmt.Errorf("SearchChunks supports at most one filter clause")
		return c
	}
	c.filter = &pb.SearchClause{
		Property: field, Operator: op, Value: toSearchValue(value), ClauseType: pb.SearchClauseType_FILTER,
	}
	return c
}

func (c *ChunksBuilder) Build(traceId ...string) (*pb.SearchChunksRequest, error) {
	if c.err != nil {
		return nil, c.err
	}
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	var filters []*pb.SearchClause
	if c.filter != nil {
		filters = []*pb.SearchClause{c.filter}
	}
	return &pb.SearchChunksRequest{
		TypeName: c.typeName, Property: c.property, Query: c.query, TopK: c.topK,
		Filter: filters, TraceId: id,
	}, nil
}
```

- [ ] **Step 4: Run tests**

Run: `cd Iverson.Clients/Go && go test ./...`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/Go/iverson/vector_search.go Iverson.Clients/Go/iverson_test/vector_search_test.go
git commit -m "feat(go): SimilarBuilder/ChunksBuilder for Qdrant vector search"
```

---

### Task 14: Docs — "Vector search" section + step-toolbox `derive()` wording fix

**Files:**
- Modify: `docs/one-query-five-languages.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Add a new "Vector search: SearchSimilar/SearchChunks builders" section**

Add a new `##` section after the existing "## Joins" section (or after whichever section precedes it once Task 19 has also landed — if Task 19 runs first, insert after the join section it touches instead). Fact-check every snippet against the actual merged code from Tasks 9–13 before finalizing (don't copy from this plan verbatim without re-verifying against the real files, per the process note already recorded in memory `project-pipeline-aggregations-design` about the Pipelines doc section):

```markdown
## Vector search: SearchSimilar/SearchChunks builders

Qdrant-backed similarity search (embedded properties via `[IversonEmbedding]`) and chunk/RAG
search (`[IversonChunk]`) now support an optional scalar/FK filter, applied server-side against
the Qdrant payload alongside the vector similarity ranking.

`SearchSimilar` filters accept `EQUALS`, `NOT_EQUALS`, `GREATER_THAN`, `LESS_THAN`,
`GREATER_THAN_OR_EQUALS`, `LESS_THAN_OR_EQUALS`, and `IN` — `CONTAINS`/`STARTS_WITH`/`ENDS_WITH`/
`VECTOR_SIMILAR` have no Qdrant payload-filter equivalent and are rejected at build time.

`SearchChunks` filters are more restricted: at most one `EQUALS` clause, and only on the entity's
primary-key property (the chunks collection's payload only indexes `parent_id`).

C#:
```csharp
Query.Similar<Article>(a => a.Title)
    .Text("machine learning")
    .TopK(10)
    .Where(a => a.Category, SearchOperators.Equals, "Tech")
    .Build();

Query.Chunks<Article>(a => a.Body)
    .Text("neural networks")
    .TopK(5)
    .Where(a => a.Id, SearchOperators.Equals, parentId)
    .Build();
```

Java:
```java
Query.similar("Article", "Title")
    .text("machine learning")
    .topK(10)
    .where("Category", SearchOperator.EQUALS, "Tech")
    .build();
```

Python:
```python
from iverson_client import similar, chunks

similar("Article", "Title").text("machine learning").top_k(10) \
    .where("Category", SearchOperator.EQUALS, "Tech").build()
```

TypeScript:
```typescript
similar('Article', 'Title')
    .text('machine learning')
    .topK(10)
    .where('Category', SearchOperator.EQUALS, 'Tech')
    .build();
```

Go:
```go
req, err := iverson.NewSimilar("Article", "Title").
    Text("machine learning").
    TopK(10).
    Where("Category", pb.SearchOperator_EQUALS, "Tech").
    Build()
```
```

- [ ] **Step 2: Fix the step-toolbox `derive()` wording nit (C3 item 17)**

Find the "step toolbox" table's `derive()` row (documented at `PipelineStepBuilder`/pipeline docs section) and correct any wording that attributes `derive()`'s expression validation to the client builder — it is server-side in all 5 languages (the doc's separate "two-layered validation" note elsewhere already says this correctly; this is a one-cell fix). Change the `derive()` row's description cell from wording like "validated scalar expression (client-checked)" to "validated scalar expression (validated server-side; see 'two-layered validation' above)".

- [ ] **Step 3: Commit**

```bash
git add docs/one-query-five-languages.md
git commit -m "docs: vector search builders section + derive() validation wording fix"
```

---

### Task 15: Java — Pipeline composite-key joins + QueryBuilder explicit-left-type joins

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/PipelineStepBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/QueryBuilder.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/PipelineBuilderTest.java`, `QueryBuilderTest.java`

**Interfaces:**
- Produces: `PipelineStepBuilder.join(String source, List<String[]> on, JoinKind kind)` (+ 3-arg default-INNER overload), each `String[]` being a `{left, right}` pair. `QueryBuilder<T>.join(String leftType, String leftField, String rightType, String rightField, JoinKind kind)` (+ 4-arg default-INNER overload) — mirrors C#'s `Join<TLeft,TRight>` capability (explicit left type) without generics, since `QueryBuilder<T>` here is not itself generic over the left type the way C#'s is.

- [ ] **Step 1: Write the failing tests**

Append to `PipelineBuilderTest.java` (or create it if it doesn't exist yet — check first; the class under test is `PipelineBuilder`/`PipelineStepBuilder`):

```java
    @Test
    void join_withCompositeKey_addsMultipleConditions() {
        PipelineRequest req = Query.pipeline("Article")
            .step("enriched", s -> s.join("Author",
                List.of(new String[]{"AuthorId", "Id"}, new String[]{"TenantId", "TenantId"})))
            .build();

        PipelineJoin join = req.getSteps(0).getJoins(0);
        assertEquals(2, join.getOnCount());
        assertEquals("AuthorId", join.getOn(0).getLeft());
        assertEquals("Id", join.getOn(0).getRight());
        assertEquals("TenantId", join.getOn(1).getLeft());
        assertEquals("TenantId", join.getOn(1).getRight());
    }
```

(Add `import java.util.List;` and `import iverson.ObjectSearch.PipelineJoin;` to the test file if not already present. Verify the exact fluent step-configuration shape — e.g. `.step(String, Function<PipelineStepBuilder,?>)` vs. a `Consumer` — against the existing single-pair join test in this file before finalizing; match its structure exactly, only substituting the composite-key `join(...)` call.)

Append to `QueryBuilderTest.java`:

```java
    @Test
    void join_explicitLeftType_setsLeftTypeIndependentlyOfBaseType() {
        SearchRequest req = Query.of(LineItem.class)
            .join("LineItem", "authorId", "Author", "id")
            .join("Author", "publisherId", "Publisher", "id")
            .build();

        assertEquals(2, req.getJoinsCount());
        assertEquals("Author", req.getJoins(1).getLeftType());
        assertEquals("Publisher", req.getJoins(1).getRightType());
    }
```

(Replace `LineItem` with whatever fixture class this test file already uses for `Query.of(...)` — check the file's existing fixtures before finalizing; the point of the assertion is that the second `.join(...)` call's `leftType` is `"Author"`, not the query's own base type.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Java/client && mvn -q test -Dtest=PipelineBuilderTest,QueryBuilderTest`
Expected: FAIL — compile error, neither overload exists yet.

- [ ] **Step 3: Implement the composite-key `join` overload in `PipelineStepBuilder`**

In `PipelineStepBuilder.java`, add alongside the existing single-pair overloads (near line 179-190):

```java
    public PipelineStepBuilder join(String source, List<String[]> on) {
        return join(source, on, JoinKind.INNER);
    }

    public PipelineStepBuilder join(String source, List<String[]> on, JoinKind kind) {
        PipelineJoin.Builder joinBuilder = PipelineJoin.newBuilder().setSource(source).setKind(kind);
        for (String[] pair : on) {
            joinBuilder.addOn(JoinCondition.newBuilder().setLeft(pair[0]).setRight(pair[1]).build());
        }
        step.addJoins(joinBuilder.build());
        return this;
    }
```

Add `import java.util.List;` to the file's imports if not already present (it already imports `java.util.ArrayList`/`Arrays`/`List` per the Global Constraints research — confirm before editing).

- [ ] **Step 4: Implement the explicit-left-type `join` overload in `QueryBuilder`**

In `QueryBuilder.java`, add alongside the existing overloads (near line 74-89):

```java
    /** Adds a join with an explicit left type — for multi-hop chains where the left side isn't this query's own base type. */
    public QueryBuilder<T> join(String leftType, String leftField, String rightType, String rightField) {
        return join(leftType, leftField, rightType, rightField, JoinKind.INNER);
    }

    public QueryBuilder<T> join(String leftType, String leftField, String rightType, String rightField, JoinKind kind) {
        joins.add(JoinSpec.newBuilder()
            .setLeftType(leftType)
            .setRightType(rightType)
            .setLeftField(leftField)
            .setRightField(rightField)
            .setKind(kind)
            .build());
        return this;
    }
```

- [ ] **Step 5: Run tests**

Run: `cd Iverson.Clients/Java/client && mvn -q test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/PipelineStepBuilder.java \
        Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/QueryBuilder.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/PipelineBuilderTest.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/QueryBuilderTest.java
git commit -m "feat(java): pipeline composite-key joins + explicit-left-type QueryBuilder joins"
```

---

### Task 16: Python — Pipeline composite-key joins + QueryBuilder explicit-left-type joins

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/pipeline.py`
- Modify: `Iverson.Clients/Python/iverson_client/search.py`
- Test: `Iverson.Clients/Python/tests/test_pipeline.py`, `test_query_builder.py`

**Interfaces:**
- Produces: `PipelineStepBuilder.join_all(self, source: str, on: List[Tuple[str,str]], kind: int = pb.JoinKind.INNER) -> "PipelineStepBuilder"`. `QueryBuilder.join_from(self, left_type: str, left_field: str, right_type: str, right_field: str, kind: int = pb.JoinKind.INNER) -> "QueryBuilder"` (Python has no true overloading, so both get distinct names rather than reusing `join`).

- [ ] **Step 1: Write the failing tests**

Append to `test_pipeline.py`:

```python
def test_join_all_with_composite_key_adds_multiple_conditions():
    request = (
        pipeline("Article")
        .step("enriched", lambda s: s.join_all("Author", [("AuthorId", "Id"), ("TenantId", "TenantId")]))
        .build()
    )
    join = request.steps[0].joins[0]
    assert len(join.on) == 2
    assert join.on[0].left == "AuthorId" and join.on[0].right == "Id"
    assert join.on[1].left == "TenantId" and join.on[1].right == "TenantId"
```

Append to `test_query_builder.py`:

```python
def test_join_from_explicit_left_type_sets_left_type_independently_of_base_type(self):
    req = (
        QueryBuilder("LineItem")
        .join_from("LineItem", "author_id", "Author", "id")
        .join_from("Author", "publisher_id", "Publisher", "id")
        .build()
    )
    assert len(req.joins) == 2
    assert req.joins[1].left_type == "Author"
    assert req.joins[1].right_type == "Publisher"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Python && python -m pytest tests/test_pipeline.py tests/test_query_builder.py -q`
Expected: FAIL — `join_all`/`join_from` don't exist yet.

- [ ] **Step 3: Implement `join_all` in `pipeline.py`**

In `pipeline.py`, add alongside the existing `join` method (near line 163-168):

```python
    def join_all(self, source: str, on: List[Tuple[str, str]],
                 kind: int = _pb.JoinKind.INNER) -> "PipelineStepBuilder":
        self._joins.append(_pb.PipelineJoin(
            source=source, kind=kind,
            on=[_pb.JoinCondition(left=l, right=r) for l, r in on]))
        return self
```

Add `from typing import List, Tuple` to the file's imports if `Tuple`/`List` aren't already imported.

- [ ] **Step 4: Implement `join_from` in `search.py`**

In `search.py`, add alongside the existing `join` method (near line 164-174):

```python
    def join_from(self, left_type: str, left_field: str, right_type: str, right_field: str,
                  kind: int = _pb.JoinKind.INNER) -> "QueryBuilder":
        """Add a join with an explicit left type — for multi-hop chains where the left side
        isn't this query's own base type."""
        self._joins.append(_pb.JoinSpec(
            left_type=left_type,
            right_type=right_type,
            left_field=left_field,
            right_field=right_field,
            kind=kind,
        ))
        return self
```

- [ ] **Step 5: Run tests**

Run: `cd Iverson.Clients/Python && python -m pytest -q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Python/iverson_client/pipeline.py Iverson.Clients/Python/iverson_client/search.py \
        Iverson.Clients/Python/tests/test_pipeline.py Iverson.Clients/Python/tests/test_query_builder.py
git commit -m "feat(python): pipeline composite-key joins + explicit-left-type QueryBuilder joins"
```

---

### Task 17: TypeScript — Pipeline composite-key joins + QueryBuilder explicit-left-type joins

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/pipeline.ts`
- Modify: `Iverson.Clients/TypeScript/src/search.ts`
- Test: `Iverson.Clients/TypeScript/tests/pipeline.test.ts`, `query-builder.test.ts`

**Interfaces:**
- Produces: `PipelineStepBuilder.joinAll(source: string, on: Array<{left: string, right: string}>, kind: JoinKind = JoinKind.INNER): this`. `QueryBuilder.joinFrom(leftType: string, leftField: string, rightType: string, rightField: string, kind: JoinKind = JoinKind.INNER): QueryBuilder`.

- [ ] **Step 1: Write the failing tests**

Append to `pipeline.test.ts`:

```typescript
it('joinAll with composite key adds multiple conditions', () => {
    const request = pipeline('Article')
        .step('enriched', s => s.joinAll('Author', [
            { left: 'AuthorId', right: 'Id' },
            { left: 'TenantId', right: 'TenantId' },
        ]))
        .build();

    const join = request.steps[0].joins[0];
    expect(join.on).toHaveLength(2);
    expect(join.on[0]).toMatchObject({ left: 'AuthorId', right: 'Id' });
    expect(join.on[1]).toMatchObject({ left: 'TenantId', right: 'TenantId' });
});
```

Append to `query-builder.test.ts`:

```typescript
it('joinFrom sets left type independently of the base type', () => {
    const req = new QueryBuilder('LineItem')
        .joinFrom('LineItem', 'authorId', 'Author', 'id')
        .joinFrom('Author', 'publisherId', 'Publisher', 'id')
        .build();

    expect(req.joins).toHaveLength(2);
    expect(req.joins[1].leftType).toBe('Author');
    expect(req.joins[1].rightType).toBe('Publisher');
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/TypeScript && npx vitest run tests/pipeline.test.ts tests/query-builder.test.ts`
Expected: FAIL — `joinAll`/`joinFrom` don't exist yet.

- [ ] **Step 3: Implement `joinAll` in `pipeline.ts`**

In `pipeline.ts`, add alongside the existing `join` method (near line 177-180):

```typescript
    joinAll(source: string, on: Array<{ left: string; right: string }>, kind: JoinKind = JoinKind.INNER): this {
        this._joins.push({ source, kind, on: on.map(({ left, right }) => ({ left, right })) });
        return this;
    }
```

- [ ] **Step 4: Implement `joinFrom` in `search.ts`**

In `search.ts`, add alongside the existing `join` method (near line 223-233):

```typescript
    /** Add a join with an explicit left type — for multi-hop chains where the left side isn't this query's own base type. */
    joinFrom(leftType: string, leftField: string, rightType: string, rightField: string, kind: JoinKind = JoinKind.INNER): QueryBuilder {
        this._joins.push({ leftType, rightType, leftField, rightField, kind });
        return this;
    }
```

- [ ] **Step 5: Run tests**

Run: `cd Iverson.Clients/TypeScript && npx vitest run`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/TypeScript/src/pipeline.ts Iverson.Clients/TypeScript/src/search.ts \
        Iverson.Clients/TypeScript/tests/pipeline.test.ts Iverson.Clients/TypeScript/tests/query-builder.test.ts
git commit -m "feat(typescript): pipeline composite-key joins + explicit-left-type QueryBuilder joins"
```

---

### Task 18: Go — Pipeline composite-key joins + QueryBuilder explicit-left-type joins

**Files:**
- Modify: `Iverson.Clients/Go/iverson/pipeline.go`
- Modify: `Iverson.Clients/Go/iverson/search.go`
- Test: `Iverson.Clients/Go/iverson_test/pipeline_test.go`, `search_test.go`

**Interfaces:**
- Produces: a client-side pair type `iverson.JoinCondition{Left, Right string}` (new, in `pipeline.go`), `PipelineStepBuilder.JoinOn(source string, on []JoinCondition, opts ...pb.JoinKind) *PipelineStepBuilder`. `QueryBuilder.JoinType(leftType, leftField, rightType, rightField string, opts ...pb.JoinKind) *QueryBuilder` (Go has no overloading, so both get distinct names).

- [ ] **Step 1: Write the failing tests**

Append to `pipeline_test.go`:

```go
func TestJoinOn_WithCompositeKey_AddsMultipleConditions(t *testing.T) {
	req, err := iverson.NewPipeline("Article").
		Step("enriched", func(s *iverson.PipelineStepBuilder) {
			s.JoinOn("Author", []iverson.JoinCondition{
				{Left: "AuthorId", Right: "Id"},
				{Left: "TenantId", Right: "TenantId"},
			})
		}).
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	join := req.Steps[0].Joins[0]
	if len(join.On) != 2 {
		t.Fatalf("expected 2 join conditions, got %d", len(join.On))
	}
	if join.On[0].Left != "AuthorId" || join.On[0].Right != "Id" {
		t.Errorf("unexpected first condition: %+v", join.On[0])
	}
	if join.On[1].Left != "TenantId" || join.On[1].Right != "TenantId" {
		t.Errorf("unexpected second condition: %+v", join.On[1])
	}
}
```

Append to `search_test.go`:

```go
func TestJoinType_SetsLeftTypeIndependentlyOfBaseType(t *testing.T) {
	req, err := iverson.NewQuery("LineItem").
		JoinType("LineItem", "AuthorId", "Author", "Id").
		JoinType("Author", "PublisherId", "Publisher", "Id").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Joins) != 2 {
		t.Fatalf("expected 2 joins, got %d", len(req.Joins))
	}
	if req.Joins[1].LeftType != "Author" || req.Joins[1].RightType != "Publisher" {
		t.Errorf("unexpected second join: %+v", req.Joins[1])
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Go && go test ./iverson_test/... -run "TestJoinOn|TestJoinType"`
Expected: FAIL — `JoinOn`/`JoinType`/`iverson.JoinCondition` don't exist yet.

- [ ] **Step 3: Implement `JoinCondition` + `JoinOn` in `pipeline.go`**

In `pipeline.go`, add near the top (after the package/import block) and alongside the existing `Join` method (near line 296-308):

```go
// JoinCondition is a client-side left/right column pair for a composite-key join.
type JoinCondition struct {
	Left  string
	Right string
}

// JoinOn joins the step's input against an earlier step's CTE or a registered entity type
// using one or more equality conditions (AND-ed) — the composite-key form of Join.
func (s *PipelineStepBuilder) JoinOn(source string, on []JoinCondition, opts ...pb.JoinKind) *PipelineStepBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	conditions := make([]*pb.JoinCondition, 0, len(on))
	for _, c := range on {
		conditions = append(conditions, &pb.JoinCondition{Left: c.Left, Right: c.Right})
	}
	s.step.Joins = append(s.step.Joins, &pb.PipelineJoin{Source: source, Kind: kind, On: conditions})
	return s
}
```

- [ ] **Step 4: Implement `JoinType` in `search.go`**

In `search.go`, add alongside the existing `Join` method (near line 87-102):

```go
// JoinType adds a join with an explicit left type — for multi-hop chains where the left side
// isn't this query's own base type.
func (q *QueryBuilder) JoinType(leftType, leftField, rightType, rightField string, opts ...pb.JoinKind) *QueryBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	q.joins = append(q.joins, &pb.JoinSpec{
		LeftType:   leftType,
		RightType:  rightType,
		LeftField:  leftField,
		RightField: rightField,
		Kind:       kind,
	})
	return q
}
```

- [ ] **Step 5: Run tests**

Run: `cd Iverson.Clients/Go && go test ./...`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Go/iverson/pipeline.go Iverson.Clients/Go/iverson/search.go \
        Iverson.Clients/Go/iverson_test/pipeline_test.go Iverson.Clients/Go/iverson_test/search_test.go
git commit -m "feat(go): pipeline composite-key joins + explicit-left-type QueryBuilder joins"
```

---

### Task 19: Docs — join-capability equalization

**Files:**
- Modify: `docs/one-query-five-languages.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Update the "## Joins" section**

The section currently documents the shared single-pair `join(leftField, rightType, rightField [, kind])` shape (line ~263) and shows only a C#-only typed multi-hop example (lines ~274-280). Fact-check against the merged code from Tasks 15–18, then add per-language composite-key and explicit-left-type examples, e.g.:

```markdown
Composite-key joins (multiple ON conditions) and multi-hop joins (explicit left type) are
available in all five languages:

- C#: `Join<TLeft,TRight>(left, right)` (multi-hop, via generics); Pipeline steps already
  supported composite-key joins via `Join(source, IReadOnlyList<(string,string)> on, kind)`.
- Java: `join(leftType, leftField, rightType, rightField[, kind])` (QueryBuilder, multi-hop);
  `join(source, List<String[]> on[, kind])` (PipelineStepBuilder, composite-key).
- Python: `join_from(left_type, left_field, right_type, right_field[, kind])` (multi-hop);
  `join_all(source, on: List[Tuple[str,str]][, kind])` (composite-key).
- TypeScript: `joinFrom(leftType, leftField, rightType, rightField[, kind])` (multi-hop);
  `joinAll(source, on: Array<{left,right}>[, kind])` (composite-key).
- Go: `JoinType(leftType, leftField, rightType, rightField, opts...)` (multi-hop);
  `JoinOn(source, on []JoinCondition, opts...)` (composite-key).

Composite-key `JoinSpec` joins (multiple ON conditions on plain `Search`/`GroupBy`, as opposed to
Pipeline steps) are not yet supported in any language — `JoinSpec` has a single `left_field`/
`right_field` pair on the wire; tracked as a separate future proto change.
```

- [ ] **Step 2: Update bullets 436-437 and the step-toolbox table row**

Change the "Build-time validation" section's bullet (currently claiming C#-only multi-hop) to note multi-hop and composite-key joins are now available in all 5 languages (cross-reference the new "## Joins" content instead of restating it). Update the step-toolbox table's `join(source, left, right, kind?)` row (line ~625) to also mention the composite-key form: `join(source, left, right, kind?)` / composite-key form per-language (see "## Joins").

- [ ] **Step 3: Commit**

```bash
git add docs/one-query-five-languages.md
git commit -m "docs: document composite-key and multi-hop joins across all 5 languages"
```

---

### Task 20: Server — pipeline test-coverage hardening (no behavior change)

**Files:**
- Modify: `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/StarRocks/PipelineIntegrationTests.cs`

**Interfaces:** none new — this task only adds/strengthens tests against existing `StarRocksPipelineBuilder`/`ObjectSearchGrpcService.Pipeline` behavior. Covers backlog items C1.1–C1.5 and C1.10 from the spec.

- [ ] **Step 1: (C1.1) Test `EmitSelectItem`'s `All=true`-from-a-non-input-join-source branch**

Append to `StarRocksPipelineBuilderTests.cs` (uses the file's existing `ArticleSchema()`/`EmptyRegistry()` helpers):

```csharp
    [Fact]
    public void Build_SelectAllFromJoinSource_EmitsJoinSourceWildcard()
    {
        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { Source = "by_author", All = true });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(agg);
        request.Steps.Add(enriched);

        var (sql, _) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        sql.Should().Contain("by_author.*");
    }
```

- [ ] **Step 2: (C1.2) Test case-mismatched join-source-name resolution**

```csharp
    [Fact]
    public void Build_JoinSourceNameDifferentCase_ResolvesViaOrdinalIgnoreCase()
    {
        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "BY_AUTHOR", Kind = JoinKind.Inner }; // different case
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(agg);
        request.Steps.Add(enriched);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().NotThrow();
    }
```

- [ ] **Step 3: (C1.5) Tighten two loose substring assertions to full-message assertions**

Locate the existing validation-failure tests asserting only a fragment via `.Where(e => e.Status.Detail.Contains(...))` for the select-alias and window-alias identifier checks (search the file for `"is not a valid identifier"`), and change them to assert the exact message. If a test named e.g. `Build_SelectAliasNotValidIdentifier_Throws` exists asserting only `.Contains("select alias")`, tighten it:

```csharp
    [Fact]
    public void Build_SelectAliasNotValidIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "s1" };
        step.Select.Add(new SelectItem { Column = "Title", Alias = "bad alias" });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail == "Step 's1': select alias 'bad alias' is not a valid identifier.");
    }

    [Fact]
    public void Build_WindowAliasNotValidIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "s1" };
        step.Windows.Add(new WindowFunction { Alias = "bad alias", Kind = WindowFunctionKind.RowNumber, OrderBy = "AuthorId" });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail == "Step 's1': window alias 'bad alias' is not a valid identifier.");
    }
```

(If tests with these exact names already exist with a looser assertion, replace their assertion body with the `==` check above rather than adding duplicates — check the file first with `grep -n "is not a valid identifier" StarRocksPipelineBuilderTests.cs` before editing.)

- [ ] **Step 4: (C1.3, C1.4) Add `Pipeline` gRPC method coverage for `StarRocksNotReadyException` and `TraceId`**

Append to `ObjectSearchGrpcServiceTests.cs`:

```csharp
    [Fact]
    public async Task Pipeline_StarRocksNotReady_ThrowsUnavailable()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns<Task<IEnumerable<dynamic>>>(_ => throw new StarRocksNotReadyException("warming up"));

        var request = new PipelineRequest { TypeName = "Article" };
        var (writer, _) = MakeStream<SearchResponse>();

        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task Pipeline_StreamsResults_PropagatesTraceId()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new List<dynamic> { new Dictionary<string, object?> { ["Title"] = "T" } });

        var request = new PipelineRequest { TypeName = "Article", TraceId = "trace-xyz" };
        var (writer, written) = MakeStream<SearchResponse>();

        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle(r => r.TraceId == "trace-xyz");
    }
```

- [ ] **Step 5: (C1.10) Tighten the 3 integration tests to assert specific row identities**

In `PipelineIntegrationTests.cs`, replace `TopNPerGroup_ReturnsTwoNewestPerAuthor`'s final assertion (given the seed data: author A has articles `1`-`4` with `PublishedAt` Jan–Apr, author B has `5`-`6` with Jan–Feb — top 2 newest per author are `3`,`4` for A and `5`,`6` for B):

```csharp
        rows.Should().HaveCount(4);
        ((IEnumerable<dynamic>)rows).Select(r => (string)r.Id).Should().BeEquivalentTo(["3", "4", "5", "6"]);
```

Replace `DerivedRatio_PercentOfTotal`'s final assertion (A has 4 of 6 articles, B has 2 of 6 → 66.67%/33.33%):

```csharp
        var byAuthorPct = ((IEnumerable<dynamic>)rows)
            .ToDictionary(r => (string)r.AuthorId, r => Convert.ToDouble(r.pct));
        byAuthorPct["A"].Should().BeApproximately(66.6667, 0.01);
        byAuthorPct["B"].Should().BeApproximately(33.3333, 0.01);
```

Replace `JoinCteAgainstBase_EnrichesRowsWithAggregates`'s final assertion (every row carries its author's article count — A's 4 rows each show `articles=4`, B's 2 rows each show `articles=2`):

```csharp
        rows.Should().HaveCount(6);
        foreach (var row in (IEnumerable<dynamic>)rows)
        {
            var authorId = (string)row.AuthorId;
            var articles = Convert.ToInt32(row.articles);
            (authorId == "A" ? 4 : 2).Should().Be(articles);
        }
```

- [ ] **Step 6: Run tests**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS. Then, with Docker available: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~PipelineIntegrationTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/StarRocks/PipelineIntegrationTests.cs
git commit -m "test(server): pipeline test-coverage hardening (join-source resolution, TraceId, StarRocksNotReady, tightened assertions)"
```

---

### Task 21: Server — small hardening fixes (MetricSpec.Name validation, derive comment-blocking, DRY dedupe, BuildHaving message assertion)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs`

**Interfaces:** none new — narrow, behavior-preserving-except-for-the-3-fixes changes to `StarRocksPipelineBuilder`. Covers backlog items C2.11, C2.12, C3.14, C1.6.

- [ ] **Step 1: Write the failing tests for the 2 real behavior changes**

Append to `StarRocksPipelineBuilderTests.cs`:

```csharp
    [Fact]
    public void Build_MetricAliasNotValidIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "s1" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "bad alias", Type = AggregationType.Count });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail == "Step 's1': metric alias 'bad alias' is not a valid identifier.");
    }

    [Theory]
    [InlineData("WordCount -- drop everything")]
    [InlineData("WordCount /* comment */ + 1")]
    public void Build_DeriveExprWithSqlCommentSequence_Throws(string expr)
    {
        var step = new PipelineStep { Name = "s1" };
        step.Derive.Add(new DeriveColumn { Alias = "d", Expr = expr });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("forbidden character"));
    }
```

Append to `StarRocksQueryBuilderTests.cs` (tightening the existing `BuildHaving_VectorSimilarClause_ThrowsInvalidArgument` test — locate it first via `grep -n "BuildHaving_VectorSimilarClause_ThrowsInvalidArgument" StarRocksQueryBuilderTests.cs` and replace its assertion body in place rather than duplicating the test):

```csharp
    [Fact]
    public void BuildHaving_VectorSimilarClause_ThrowsInvalidArgument()
    {
        var param = new DynamicParameters();
        var act = () => StarRocksQueryBuilder.BuildHaving(
            [VectorClause()], SearchLogic.And, param);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("VECTOR_SIMILAR")
                     && e.Status.Detail.Contains("SearchSimilar"));
    }
```

- [ ] **Step 2: Run tests to verify the 2 new ones fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~MetricAliasNotValidIdentifier|FullyQualifiedName~DeriveExprWithSqlCommentSequence"`
Expected: FAIL — current code doesn't validate `MetricSpec.Name` as an identifier, and doesn't block `--`/`/* */`.

- [ ] **Step 3: Add `MetricSpec.Name` identifier validation**

In `StarRocksPipelineBuilder.cs`, change the metric-alias check (line 154) from:

```csharp
                if (string.IsNullOrEmpty(m.Name))
                    throw Invalid($"Step '{step.Name}': every metric requires an alias.");
```

to:

```csharp
                if (string.IsNullOrEmpty(m.Name) || !IdentifierRx.IsMatch(m.Name))
                    throw Invalid($"Step '{step.Name}': metric alias '{m.Name}' is not a valid identifier.");
```

- [ ] **Step 4: Block SQL comment sequences in derive expressions**

In `ValidateDeriveExpr` (lines 273-278), change:

```csharp
        if (d.Expr.Contains(';') || d.Expr.Contains('\'') || d.Expr.Contains('`'))
            throw Invalid($"Step '{stepName}': derive '{d.Alias}' contains a forbidden character " +
                          "(no semicolons, quotes, or backticks).");
```

to:

```csharp
        if (d.Expr.Contains(';') || d.Expr.Contains('\'') || d.Expr.Contains('`') ||
            d.Expr.Contains("--") || d.Expr.Contains("/*") || d.Expr.Contains("*/"))
            throw Invalid($"Step '{stepName}': derive '{d.Alias}' contains a forbidden character " +
                          "(no semicolons, quotes, backticks, or SQL comment sequences).");
```

- [ ] **Step 5: Dedupe the schema→column-dict loop**

Add a private helper near the top of the class:

```csharp
    private static Dictionary<string, string> ColumnsFor(SchemaDescriptor schema)
    {
        var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [schema.KeyColumn.Name] = schema.KeyColumn.Name
        };
        foreach (var c in schema.ScalarColumns) cols[c.Name] = c.Name;
        return cols;
    }
```

Replace the loop in `TrackAndValidate` (lines 43-49):

```csharp
        steps.Add(new StepColumns(BaseStepName, ColumnsFor(schema)));
```

Replace the loop in `ResolveJoinSources` (lines 230-235):

```csharp
            sources[joinedSchema.TypeName] = new StepColumns(joinedSchema.TypeName, ColumnsFor(joinedSchema));
```

- [ ] **Step 6: Run the full non-integration server suite**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS. If any existing test constructed a `MetricSpec` with a non-identifier name expecting success (e.g. a name containing a space), update it to use a valid identifier name instead — search for `MetricSpec { Name =` across the test file for any such case.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs \
        Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs \
        Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs
git commit -m "fix(server): validate MetricSpec.Name as identifier, block SQL comments in derive exprs, dedupe column-dict loop"
```

---

### Task 22: C# client — GroupByBuilder key/alias collision check, Pipeline entry-point consolidation, EntityCoordinator.PipelineAsync tests

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/GroupByBuilder.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/GroupByBuilderTests.cs`, `PipelineBuilderTests.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorPipelineTests.cs`

**Interfaces:**
- Consumes: `TestCoordinatorFactory` (created in Task 9, Step 7, in `Iverson.Client.Core.Tests`) — reused here for the new `PipelineAsync` tests.
- Produces: `GroupByBuilder.Build()` now throws on key/metric-alias collision. `Pipeline.For`/`Pipeline.For<T>()` are removed; `Query.Pipeline<T>()` is added alongside the existing `Query.Pipeline(string)` so `Query.Pipeline` is the sole entry point (covers C2.13, C1.9, C4.18, C1.7).

- [ ] **Step 1: Write the failing tests for the 2 real behavior changes**

Append to `GroupByBuilderTests.cs`:

```csharp
    [Fact]
    public void Build_KeyCollidesWithMetricAlias_Throws()
    {
        var builder = Query.GroupBy("Article").Keys("total").Sum("Price", "total");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*total*");
    }

    [Fact]
    public void Build_HavingReferencesMetricAlias_CaseInsensitive_IsAllowed()
    {
        var act = () => Query.GroupBy("Article")
            .Keys("Category")
            .Sum("WordCount", "Total")
            .Having("TOTAL", SearchOperator.GreaterThan, 100)
            .Build();
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_OrderByReferencesKey_CaseInsensitive_IsAllowed()
    {
        var act = () => Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .OrderBy("CATEGORY")
            .Build();
        act.Should().NotThrow();
    }
```

- [ ] **Step 2: Write the Pipeline-entry-point-consolidation test**

Append to `QueryBuilderTests.cs` (or `PipelineBuilderTests.cs` — whichever already imports `Query` and `PipelineBuilder`):

```csharp
    [Fact]
    public void QueryPipeline_Generic_MatchesStringOverloadTypeName()
    {
        var request = Query.Pipeline<TestArticle>().Build();
        request.TypeName.Should().Be("TestArticle");
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj --filter "FullyQualifiedName~KeyCollidesWithMetricAlias|FullyQualifiedName~QueryPipeline_Generic"`
Expected: FAIL — collision isn't detected yet; `Query.Pipeline<T>()` doesn't exist yet.

- [ ] **Step 4: Add the key/metric-alias collision check**

In `GroupByBuilder.cs`, change the key loop in `Build()` (line 144):

```csharp
        foreach (var k in _keys)
            if (!aliasSet.Add(k))
                throw new InvalidOperationException($"Key '{k}' collides with an existing metric alias.");
```

- [ ] **Step 5: Consolidate the Pipeline entry point**

In `PipelineBuilder.cs`, delete the `Pipeline` static class entirely (lines 5-10):

```csharp
public static class Pipeline
{
    public static PipelineBuilder For(string typeName) => new(typeName);
    public static PipelineBuilder For<T>() where T : class => new(typeof(T).Name);
}
```

In `Query.cs`, add a generic overload alongside the existing `Pipeline(string typeName)` method:

```csharp
    public static PipelineBuilder Pipeline<T>() where T : class => new(typeof(T).Name);
```

- [ ] **Step 6: Fix call sites broken by removing `Pipeline.For`**

Run: `dotnet build Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj` — this will fail everywhere `Pipeline.For(...)` was used (all 12 facts in `PipelineBuilderTests.cs`, per the earlier research, plus `Iverson.Client.Sample/Program.cs`'s pipeline sample). Replace every `Pipeline.For(` with `Query.Pipeline(` in both files:

```bash
sed -i 's/Pipeline\.For(/Query.Pipeline(/g' Iverson.Clients/DotNet/Iverson.Client.Search.Tests/PipelineBuilderTests.cs
sed -i 's/Pipeline\.For(/Query.Pipeline(/g' Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs
```

Run: `dotnet build Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj` again.
Expected: Build succeeds. If `PipelineBuilderTests.cs` or `Program.cs` don't already `using Iverson.Client.Search;` (for `Query`), add it.

- [ ] **Step 7: Add `EntityCoordinator.PipelineAsync` test coverage**

Create `EntityCoordinatorPipelineTests.cs` in `Iverson.Client.Core.Tests`, reusing the `TestCoordinatorFactory` helper created in Task 9:

```csharp
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

public class EntityCoordinatorPipelineTests
{
    private sealed class TestArticle
    {
        public string Id { get; set; } = "";
        public string AuthorId { get; set; } = "";
    }

    private sealed class AuthorArticleCount
    {
        public string AuthorId { get; set; } = "";
        public long Articles { get; set; }
    }

    [Fact]
    public async Task PipelineAsync_StreamsUntypedRows()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var data = new Struct();
        data.Fields["authorId"] = Value.ForString("A");
        var responses = new List<SearchResponse> { new() { Data = data } };
        search.Pipeline(Arg.Any<PipelineRequest>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(TestStreamHelper.MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in coordinator.PipelineAsync(Query.Pipeline<TestArticle>()))
            rows.Add(row);

        rows.Should().ContainSingle();
        rows[0]["authorId"].Should().Be("A");
    }

    [Fact]
    public async Task PipelineAsyncTyped_ProjectsOntoResultType()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var data = new Struct();
        data.Fields["authorId"] = Value.ForString("A");
        data.Fields["articles"] = Value.ForNumber(4);
        var responses = new List<SearchResponse> { new() { Data = data } };
        search.Pipeline(Arg.Any<PipelineRequest>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(TestStreamHelper.MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var rows = new List<AuthorArticleCount>();
        await foreach (var row in coordinator.PipelineAsync<AuthorArticleCount>(Query.Pipeline<TestArticle>()))
            rows.Add(row);

        rows.Should().ContainSingle(r => r.AuthorId == "A" && r.Articles == 4);
    }
}
```

Note: this reuses the same `AsyncServerStreamingCall<TResponse>` construction pattern introduced by Task 9's `MakeCall` helper — factor that helper out into a shared `TestStreamHelper.MakeCall<TResponse>(IReadOnlyList<TResponse>)` in the test project if Task 9 didn't already do so, and update Task 9's test to call the shared helper too (avoid duplicating the streaming-mock plumbing across 2 files).

- [ ] **Step 8: Run the full C# client test suites**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj && dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Iverson.Clients/DotNet/Iverson.Client.Search/GroupByBuilder.cs Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs \
        Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs Iverson.Clients/DotNet/Iverson.Client.Search.Tests/GroupByBuilderTests.cs \
        Iverson.Clients/DotNet/Iverson.Client.Search.Tests/PipelineBuilderTests.cs Iverson.Clients/DotNet/Iverson.Client.Sample/Program.cs \
        Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorPipelineTests.cs
git commit -m "fix(dotnet): GroupByBuilder key/alias collision check, consolidate Pipeline entry point onto Query.Pipeline, PipelineAsync tests"
```

---

### Task 23: Java client — GroupByBuilder collision check, case-insensitive test coverage, cosmetic nits

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/GroupByBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/PipelineStepBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/GroupByBuilderTest.java`

**Interfaces:** none new — behavior-preserving except the collision check (C2.13). Also covers C1.9, C3.15, C3.16, and C1.8 (canonical Pipeline validation rules 1/4).

- [ ] **Step 1: Write the failing tests**

Append to `GroupByBuilderTest.java`:

```java
    @Test
    void keyCollidesWithMetricAliasThrows() {
        var b = Query.groupBy("Article").keys("total").sum("Price", "total");
        assertThrows(IllegalStateException.class, b::build);
    }

    @Test
    void havingReferencesMetricAlias_caseInsensitive_isAllowed() {
        assertDoesNotThrow(() -> Query.groupBy("Article")
            .keys("Category").sum("WordCount", "Total")
            .having("TOTAL", SearchOperator.GREATER_THAN, 100)
            .build());
    }

    @Test
    void orderByReferencesKey_caseInsensitive_isAllowed() {
        assertDoesNotThrow(() -> Query.groupBy("Article")
            .keys("Category").countAll("n")
            .orderBy("CATEGORY")
            .build());
    }
```

- [ ] **Step 2: Run tests to verify the collision test fails**

Run: `cd Iverson.Clients/Java/client && mvn -q test -Dtest=GroupByBuilderTest`
Expected: FAIL on `keyCollidesWithMetricAliasThrows` — no collision detection yet; the 2 case-insensitive tests should already pass (confirming existing behavior, not a regression check).

- [ ] **Step 3: Add the collision check and fix the cosmetic nits**

In `GroupByBuilder.java`, add proper imports at the top (replacing the inline fully-qualified names):

```java
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
```

Replace the validation block (lines 231-244) using the new imports and adding the collision check:

```java
    public GroupByRequest build(String traceId) {
        Set<String> aliases = new HashSet<>();
        for (MetricSpec m : metrics)
            if (!aliases.add(m.getName().toLowerCase(Locale.ROOT)))
                throw new IllegalStateException("Duplicate metric alias '" + m.getName() + "'.");
        for (String k : keys)
            if (!aliases.add(k.toLowerCase(Locale.ROOT)))
                throw new IllegalStateException("Key '" + k + "' collides with an existing metric alias.");

        for (SearchClause h : having)
            if (!aliases.contains(h.getProperty().toLowerCase(Locale.ROOT)))
                throw new IllegalStateException("HAVING references '" + h.getProperty()
                    + "', which is neither a metric alias nor a key.");
        for (SearchSort s : orderBy)
            if (!aliases.contains(s.getProperty().toLowerCase(Locale.ROOT)))
                throw new IllegalStateException("orderBy references '" + s.getProperty()
                    + "', which is neither a metric alias nor a key.");
```

In `PipelineStepBuilder.java`, fix the `count(field)` DRY nit (lines 155-156):

```java
    public PipelineStepBuilder count(String field)               { return count(field, field + "_count"); }
    public PipelineStepBuilder count(String field, String alias) { return addMetric(alias, AggregationType.COUNT, field, null); }
```

- [ ] **Step 4: Run tests**

Run: `cd Iverson.Clients/Java/client && mvn -q test`
Expected: PASS.

- [ ] **Step 5: (C1.8) Add canonical Pipeline validation-rule test coverage**

Open `PipelineStepBuilder.java`'s `build()`/`buildStep()` validation method and confirm it already enforces (mirroring C#'s `PipelineStepBuilder.BuildStep()`, `Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs:255-280`): rule 1 (a step name must be non-empty and not the reserved name `"base"`) and rule 4 (declaring `metrics` without a `groupBy` is rejected). If both checks exist (they should, per the client-pipeline plan's spec), add one test per rule to `PipelineBuilderTest.java` asserting the exact exception your inspection shows is thrown (do not guess the message — read the real `throw` statement and assert against it), e.g.:

```java
    @Test
    void step_withReservedNameBase_throws() {
        assertThrows(IllegalStateException.class, () ->
            Query.pipeline("Article").step("base", s -> s).build());
    }

    @Test
    void step_withMetricsButNoGroupBy_throws() {
        assertThrows(IllegalStateException.class, () ->
            Query.pipeline("Article").step("s1", s -> s.count("id")).build());
    }
```

(Adjust the exact trigger shape to whatever the real validation method requires if these don't reproduce the failure — the goal is closing the untested-but-implemented gap, not changing behavior.)

- [ ] **Step 6: Run tests again**

Run: `cd Iverson.Clients/Java/client && mvn -q test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/GroupByBuilder.java \
        Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/PipelineStepBuilder.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/GroupByBuilderTest.java \
        Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/PipelineBuilderTest.java
git commit -m "fix(java): GroupByBuilder key/alias collision check, count() DRY fix, import cleanup, canonical validation test coverage"
```

---

### Task 24: Python client — GroupByBuilder collision check + case-insensitive test coverage

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/group_by.py`
- Modify: `Iverson.Clients/Python/tests/test_group_by.py`
- Modify: `Iverson.Clients/Python/iverson_client/pipeline.py` (Step 5 only, if applicable)
- Modify: `Iverson.Clients/Python/tests/test_pipeline.py` (Step 5 only)

**Interfaces:** none new — behavior-preserving except the collision check (C2.13). Also covers C1.9 and C1.8.

- [ ] **Step 1: Write the failing tests**

Append to `test_group_by.py`:

```python
def test_key_collides_with_metric_alias_raises():
    b = group_by("Article").keys("total").sum("Price", "total")
    with pytest.raises(ValueError):
        b.build()


def test_having_references_metric_alias_case_insensitive_is_allowed():
    b = (group_by("Article").keys("Category").sum("WordCount", "Total")
         .having("TOTAL", pb.GREATER_THAN, 100))
    b.build()  # should not raise


def test_order_by_references_key_case_insensitive_is_allowed():
    b = group_by("Article").keys("Category").count_all("n").order_by("CATEGORY")
    b.build()  # should not raise
```

(Confirm `test_group_by.py` already imports `pb` as `from iverson_client.generated import object_search_pb2 as pb` — add if missing.)

- [ ] **Step 2: Run tests to verify the collision test fails**

Run: `cd Iverson.Clients/Python && python -m pytest tests/test_group_by.py -q`
Expected: FAIL on `test_key_collides_with_metric_alias_raises`.

- [ ] **Step 3: Add the collision check**

In `group_by.py`, change the key-aliasing line in `build()` (line 180):

```python
    for k in self._keys:
        key = k.lower()
        if key in aliases:
            raise ValueError(f"Key '{k}' collides with an existing metric alias.")
        aliases.add(key)
```

- [ ] **Step 4: Run tests**

Run: `cd Iverson.Clients/Python && python -m pytest -q`
Expected: PASS.

- [ ] **Step 5: (C1.8) Add canonical Pipeline validation-rule test coverage**

Open `pipeline.py`'s step-build validation and confirm it already enforces (mirroring C#'s `PipelineStepBuilder.BuildStep()`, `Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs:255-280`): rule 1 (a step name must be non-empty and not the reserved name `"base"`) and rule 4 (declaring `metrics` without a `group_by` is rejected). If both checks exist, add one test per rule to `test_pipeline.py` asserting the exact exception your inspection shows is raised:

```python
def test_step_with_reserved_name_base_raises():
    with pytest.raises(ValueError):
        pipeline("Article").step("base", lambda s: s).build()


def test_step_with_metrics_but_no_group_by_raises():
    with pytest.raises(ValueError):
        pipeline("Article").step("s1", lambda s: s.count("id")).build()
```

(Adjust the exact trigger shape if these don't reproduce the failure — the goal is closing the untested-but-implemented gap, not changing behavior.)

- [ ] **Step 6: Run tests again**

Run: `cd Iverson.Clients/Python && python -m pytest -q`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/Python/iverson_client/group_by.py Iverson.Clients/Python/tests/test_group_by.py \
        Iverson.Clients/Python/iverson_client/pipeline.py Iverson.Clients/Python/tests/test_pipeline.py
git commit -m "fix(python): GroupByBuilder key/alias collision check, canonical validation test coverage"
```

---

### Task 25: TypeScript client — GroupByBuilder collision check + case-insensitive test coverage

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/group-by.ts`
- Modify: `Iverson.Clients/TypeScript/tests/group-by.test.ts`
- Modify: `Iverson.Clients/TypeScript/src/pipeline.ts` (Step 5 only, if applicable)
- Modify: `Iverson.Clients/TypeScript/tests/pipeline.test.ts` (Step 5 only)

**Interfaces:** none new — behavior-preserving except the collision check (C2.13). Also covers C1.9 and C1.8.

- [ ] **Step 1: Write the failing tests**

Append to `group-by.test.ts`:

```typescript
it('throws when a key collides with a metric alias', () => {
    const b = groupBy('Article').keys('total').sum('Price', 'total');
    expect(() => b.build()).toThrow();
});

it('allows HAVING to reference a metric alias case-insensitively', () => {
    const b = groupBy('Article').keys('Category').sum('WordCount', 'Total')
        .having('TOTAL', SearchOperator.GREATER_THAN, 100);
    expect(() => b.build()).not.toThrow();
});

it('allows orderBy to reference a key case-insensitively', () => {
    const b = groupBy('Article').keys('Category').countAll('n').orderBy('CATEGORY');
    expect(() => b.build()).not.toThrow();
});
```

- [ ] **Step 2: Run tests to verify the collision test fails**

Run: `cd Iverson.Clients/TypeScript && npx vitest run tests/group-by.test.ts`
Expected: FAIL on the collision test.

- [ ] **Step 3: Add the collision check**

In `group-by.ts`, change the key-aliasing loop in `build()` (line 222):

```typescript
    for (const k of this._keys) {
        const key = k.toLowerCase();
        if (aliases.has(key)) throw new Error(`Key '${k}' collides with an existing metric alias.`);
        aliases.add(key);
    }
```

- [ ] **Step 4: Run tests**

Run: `cd Iverson.Clients/TypeScript && npx vitest run`
Expected: PASS.

- [ ] **Step 5: (C1.8) Add canonical Pipeline validation-rule test coverage**

Open `pipeline.ts`'s step-build validation and confirm it already enforces (mirroring C#'s `PipelineStepBuilder.BuildStep()`, `Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs:255-280`): rule 1 (a step name must be non-empty and not the reserved name `"base"`) and rule 4 (declaring `metrics` without a `groupBy` is rejected). If both checks exist, add one test per rule to `pipeline.test.ts`:

```typescript
it('throws when a step uses the reserved name "base"', () => {
    expect(() => pipeline('Article').step('base', s => s).build()).toThrow();
});

it('throws when a step declares metrics without groupBy', () => {
    expect(() => pipeline('Article').step('s1', s => s.count('id')).build()).toThrow();
});
```

(Adjust the exact trigger shape if these don't reproduce the failure — the goal is closing the untested-but-implemented gap, not changing behavior.)

- [ ] **Step 6: Run tests again**

Run: `cd Iverson.Clients/TypeScript && npx vitest run`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/TypeScript/src/group-by.ts Iverson.Clients/TypeScript/tests/group-by.test.ts \
        Iverson.Clients/TypeScript/src/pipeline.ts Iverson.Clients/TypeScript/tests/pipeline.test.ts
git commit -m "fix(typescript): GroupByBuilder key/alias collision check, canonical validation test coverage"
```

---

### Task 26: Go client — GroupByBuilder collision check + case-insensitive test coverage

**Files:**
- Modify: `Iverson.Clients/Go/iverson/group_by.go`
- Modify: `Iverson.Clients/Go/iverson_test/group_by_test.go`
- Modify: `Iverson.Clients/Go/iverson/pipeline.go` (Step 5 only, if applicable)
- Modify: `Iverson.Clients/Go/iverson_test/pipeline_test.go` (Step 5 only)

**Interfaces:** none new — behavior-preserving except the collision check (C2.13). Also covers C1.9 and C1.8.

- [ ] **Step 1: Write the failing tests**

Append to `group_by_test.go`:

```go
func TestGroupByKeyCollidesWithMetricAlias_Errors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("total").Sum("Price", "total").Build()
	if err == nil {
		t.Fatal("expected key/metric-alias collision error")
	}
}

func TestGroupByHaving_ReferencesMetricAlias_CaseInsensitive_IsAllowed(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").Sum("WordCount", "Total").
		Having("TOTAL", pb.SearchOperator_GREATER_THAN, iverson.NumberValue(100)).
		Build()
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}
}

func TestGroupByOrderBy_ReferencesKey_CaseInsensitive_IsAllowed(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").OrderBy("CATEGORY").
		Build()
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}
}
```

Check the file's existing tests for the real helper used to construct a `*pb.SearchValue` for `Having(...)` (e.g. a `NumberValue(...)`/`toSearchValue(...)`-equivalent already used elsewhere in `group_by_test.go`) and use that exact helper name instead of the placeholder `iverson.NumberValue(100)` above if it differs.

- [ ] **Step 2: Run tests to verify the collision test fails**

Run: `cd Iverson.Clients/Go && go test ./iverson_test/... -run "TestGroupByKeyCollidesWithMetricAlias|TestGroupByHaving_ReferencesMetricAlias|TestGroupByOrderBy_ReferencesKey"`
Expected: FAIL on the collision test.

- [ ] **Step 3: Add the collision check**

In `group_by.go`, change the key-aliasing loop in `Build()` (lines 232-234):

```go
	for _, k := range g.keys {
		key := strings.ToLower(k)
		if aliases[key] {
			return nil, fmt.Errorf("key %q collides with an existing metric alias", k)
		}
		aliases[key] = true
	}
```

- [ ] **Step 4: Run tests**

Run: `cd Iverson.Clients/Go && go test ./...`
Expected: PASS.

- [ ] **Step 5: (C1.8) Add canonical Pipeline validation-rule test coverage**

Open `pipeline.go`'s step-build validation and confirm it already enforces (mirroring C#'s `PipelineStepBuilder.BuildStep()`, `Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs:255-280`): rule 1 (a step name must be non-empty and not the reserved name `"base"`) and rule 4 (declaring `metrics` without a `GroupBy` is rejected). If both checks exist, add one test per rule to `pipeline_test.go`:

```go
func TestStep_WithReservedNameBase_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("base", func(s *iverson.PipelineStepBuilder) {}).
		Build()
	if err == nil {
		t.Fatal("expected an error for reserved step name \"base\"")
	}
}

func TestStep_WithMetricsButNoGroupBy_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("s1", func(s *iverson.PipelineStepBuilder) { s.Count("id") }).
		Build()
	if err == nil {
		t.Fatal("expected an error for metrics without groupBy")
	}
}
```

(Adjust the exact trigger shape if these don't reproduce the failure — the goal is closing the untested-but-implemented gap, not changing behavior.)

- [ ] **Step 6: Run tests again**

Run: `cd Iverson.Clients/Go && go test ./...`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Iverson.Clients/Go/iverson/group_by.go Iverson.Clients/Go/iverson_test/group_by_test.go \
        Iverson.Clients/Go/iverson/pipeline.go Iverson.Clients/Go/iverson_test/pipeline_test.go
git commit -m "fix(go): GroupByBuilder key/alias collision check, canonical validation test coverage"
```

---

## Final whole-branch review

After all 26 tasks pass their own reviews, run a final whole-branch review (Opus) across every file touched by this plan — the three prior DSL plans each caught at least one cross-task bug only visible at this final pass (SQL-injection gap, joins+windows/derive ambiguous-column gap, cross-language window-parity gap — see `project-pipeline-aggregations-design` memory). Specifically re-check:

- Cross-language consistency of the new `SimilarBuilder`/`ChunksBuilder` APIs (Tasks 9–13) — same operator-rejection set, same PK-equals-only restriction, same method names' idiomatic mapping.
- Cross-language consistency of the new join overloads (Tasks 15–18) — same composite-key/explicit-left-type capability, same parameter order.
- That Task 3's camelCase payload-key convention and Task 4's `QdrantFilterBuilder` (which receives already-camelCased properties from Task 6) don't drift if either is touched again later.
- That Task 22's `Pipeline.For` removal didn't leave any stale references (docs, sample apps, other test files) — grep the whole `Iverson.Clients/DotNet` tree for `Pipeline.For` after Task 22 lands.

