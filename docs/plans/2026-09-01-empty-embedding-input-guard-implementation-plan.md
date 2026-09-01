# Empty Embedding Input Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-01-empty-embedding-input-guard-design.md` (commit SHA: `f956a2564dcd397f4cd42e908aa9dee573cf70f3`)

**Goal:** Stop empty text reaching Ollama — drop all-whitespace chunk windows before embedding, and turn the resulting failure into a typed exception the gRPC boundary maps to `InvalidArgument`.

**Architecture:** `EmbeddingService.EmbedAsync` gains a typed guard so an empty input fails as `EmptyEmbeddingInputException` rather than `IndexOutOfRangeException`. The ingest consumer filters empty windows before the call so the guard never fires there; the two `ObjectSearchGrpcService` query paths catch the type and return `InvalidArgument` instead of the misleading `Unavailable`.

**Tech stack:** .NET 10 (`net10.0`), C# with `ImplicitUsings`; xunit + NSubstitute + FluentAssertions.

---

## Global Constraints

Two invariants from the spec bind every task. Both are silent when broken — no compiler error, no failing build.

1. **Chunk indexes are never renumbered.** `SplitIntoChunks` assigns `index++` inside the iterator, so filtering must happen *after* the generator. `ComputeChunkPointId` is pure arithmetic over the index value, so a renumbered index silently changes every downstream point id and breaks re-ingest stability.
2. **The new catch clause goes ABOVE the existing catch-all.** The catch-all carries `when (ex is not OperationCanceledException)`, so a clause placed after it raises no CS0160 — it compiles clean and never runs. Only a test detects this.

## File Structure

**Create**
- `Iverson.Server/Iverson.Embeddings/EmptyEmbeddingInputException.cs` — the typed failure, transport-neutral.

**Modify**
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:48` — guard at the top of `EmbedAsync`.
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:229` — filter empty windows.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:203` and `:374` — map the type to `InvalidArgument`.

**Test**
- `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` — the guard fires and issues no HTTP request.
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — empty windows dropped, indexes preserved.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — the clause ordering holds.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here (A1–A21 in the spec's table). The load-bearing ones for this plan:

- `Iverson.Api` references `Iverson.Embeddings` (`Iverson.Api.csproj:32`); exception convention is `sealed class X(string message) : Exception(message)` in its own file (`Iverson.Vector/FilterTranslationException.cs:8`).
- `EmbedAsync` is the only path to `/api/embed`; the init probe passes `"probe"`; exactly five production callers exist, all accounted for.
- `Index` is assigned inside the iterator (`IntelligenceStoreConsumer.cs:667`); `ComputeChunkPointId` is pure (`:696`); the `:226` field guard leaves at least one content-bearing window; `ComputeCentroid` requires non-empty (`:470`).
- Both query call sites sit inside a catch-all mapping to `Unavailable`; a clause after it compiles clean and never runs.
- `PrefixWithContextAsync` runs after the filter and would mask an empty chunk (`:628`).

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `Iverson.Embeddings/EmptyEmbeddingInputException.cs` does not yet exist | `ls` returned no such file |
| P2 | File path | `Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` exists | file present; `CreateService` helper at `:31` |
| P3 | File path | `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` exists | file present |
| P4 | File path | `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` exists | file present |
| P5 | Command | `dotnet test <csproj>` is the invocation; the two affected test projects are `Iverson.Embeddings.Tests` and `Iverson.Api.Tests` | both listed in `Iverson.slnx`; prior runs emit `Iverson.Api.Tests.dll (net10.0)` |
| P6 | Signature | `EmbeddingService` is constructible in tests without a live HTTP call | `EmbeddingServiceTests.cs:31` — `CreateService(HttpMessageHandler, string modelId)`; `FakeHttpMessageHandler` records `LastRequest` (`:16`) |
| P7 | Signature | Consumer harness exposes `_embedding`, `_vectorWrite`, `_registry`, `UnitVector()`, `BuildSut()`, `Serialize()` | `IntelligenceStoreConsumerTests.cs:26`, `:25`, `:29`, `:37`, `:97`, `:95` |
| P8 | Signature | gRPC tests invoke via `MakeStream<SearchResponse>()` + `TestServerCallContext.Create()`, and assert with `(await act.Should().ThrowAsync<RpcException>()).Where(e => e.Status.StatusCode == ...)` | `ObjectSearchGrpcServiceTests.cs:1137-1141`; `SearchChunksRequest { TypeName, Property, Query, TopK }` at `:1379` |
| P9 | Signature | The gRPC tests substitute `IEmbeddingService`, so an empty query throws nothing on its own — the substitute must be configured to throw | `ObjectSearchGrpcServiceTests.cs:41` — `Substitute.For<IEmbeddingService>()` |
| P10 | Ordering | Task 1 precedes Task 2 (Task 2's catch clauses reference Task 1's type). The consumer pre-filter inside Task 2 has no dependency on Task 1 | the filter references only `SplitIntoChunks` and `.Text` |
| P11 | Code validity | Primary-constructor exception syntax compiles here; `Exception` resolves without an explicit `using` | `Iverson.Embeddings.csproj` — `net10.0`, `ImplicitUsings enable`; `FilterTranslationException.cs:8` already uses the form |
| P12 | Consumer impact | `chunks` keeps type `List<(string Text, int Index)>` through `.Where(...).ToList()`, so `.Select` at `:237` and `.Count` at `:329` still compile | tuple element names propagate through `Where`/`ToList`; both uses are element-wise, not positional |
| P13 | Consumer impact | The guard lives in the concrete class; consumer and gRPC tests substitute `IEmbeddingService`, so no existing test changes behavior | `IntelligenceStoreConsumerTests.cs:74` stubs `EmbedAsync(Arg.Any<string>())` → `UnitVector()`; `ObjectSearchGrpcServiceTests.cs:41` likewise |
| P14 | Command | Commit convention is lowercase imperative, no prefix | `git log --oneline -12` — e.g. "refuse an unreachable chunk budget and record the build in every run" |
| P15 | Consumer impact | **The default fixture is the wrong schema for the new test.** `SchemaFixtures.ArticleSchema()` uses `ChunkDescriptor("Body", 512, 64, …)` → `maxChars` 2048, `step` 1792, needing a 3840-char run. The spec's 360 figure holds only for `50, 10` | `SchemaFixtures.cs:67` vs the inline custom schema at `IntelligenceStoreConsumerTests.cs:684`. **Resolution:** Task 2 declares its own `ChunkDescriptor("Body", 50, 10, …)`, following the `:684` precedent |
| P16 | Consumer impact | That custom schema leaves `Contextual` false, so `textToEmbed == chunkText` and the "never embedded whitespace" assertion is not confounded by enrichment | `ChunkDescriptor`'s `contextual` argument is omitted at `:684`; `contextualEnabled` at `:176` requires `ChunkFields.Any(cf => cf.Contextual)` |

## Tasks

### Task 1: The typed exception and the `EmbeddingService` guard

**Files:**
- Create: `Iverson.Server/Iverson.Embeddings/EmptyEmbeddingInputException.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:48`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs`

**Interfaces:**
- Produces: `Iverson.Embeddings.EmptyEmbeddingInputException`, consumed by Task 2's gRPC clauses.

- [ ] **Step 1: Create the exception type**

```csharp
namespace Iverson.Embeddings;

/// <summary>
/// Text offered for embedding was empty or whitespace-only. Transport-neutral by design —
/// <see cref="EmbeddingService"/> has no dependency on gRPC or Kafka; callers that need a
/// transport-specific error (e.g. an RpcException) translate this at their boundary.
/// </summary>
public sealed class EmptyEmbeddingInputException(string message) : Exception(message);
```

- [ ] **Step 2: Guard `EmbedAsync`**

The guard goes at the very top of the method body — **before** the `activity` lines at `:50-51`, not merely before the request is built. `text.Length` is dereferenced at `:51`, so a guard placed after it would throw `NullReferenceException` on a null input instead of the typed exception.

```csharp
public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(text))
        throw new EmptyEmbeddingInputException("Cannot embed empty or whitespace-only text.");

    using var activity = Telemetry.Source.StartActivity("embeddings.embed", ActivityKind.Client);
    // ... unchanged
```

- [ ] **Step 3: Test the guard**

Add to `EmbeddingServiceTests`. Assert more than "throws" — assert that **no HTTP request was issued**, which is what proves the guard runs before the call rather than the response parser happening to fail:

```csharp
[Theory]
[InlineData("")]
[InlineData("   ")]
[InlineData("\t\n")]
public async Task EmbedAsync_WithEmptyOrWhitespaceInput_ThrowsWithoutCallingOllama(string input)
{
    var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
    var sut     = CreateService(handler);

    var act = async () => await sut.EmbedAsync(input);

    await act.Should().ThrowAsync<EmptyEmbeddingInputException>();
    handler.LastRequest.Should().BeNull();
}
```

- [ ] **Step 4: Run the suite**

```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
```

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Embeddings/EmptyEmbeddingInputException.cs \
        Iverson.Server/Iverson.Embeddings/EmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs
git commit -m "refuse empty embedding input with a typed exception"
```

### Task 2: Both callers respond

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:229`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:203`, `:374`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `EmptyEmbeddingInputException` from Task 1.

- [ ] **Step 1: Filter empty windows in the consumer**

At `:229`, replacing the existing single-line `chunks` assignment:

```csharp
// A window falling entirely inside a run of whitespace strips to "". Filter AFTER the
// generator, never inside it: SplitIntoChunks assigns index++ per window, so dropping a
// window here preserves every survivor's ORIGINAL index and keeps ComputeChunkPointId
// stable across re-ingests. Filtering before PrefixWithContextAsync also matters — that
// method returns "{prefix}\n\n{chunkText}", which is non-empty even for an empty chunk,
// so a later guard could not see the problem.
var chunks = SplitIntoChunks(text, cf.MaxTokens, cf.Overlap)
    .Where(c => c.Text.Length > 0)
    .ToList();
```

- [ ] **Step 2: Map the exception in both query paths**

In `SearchSimilar` (before the catch-all at `:203`) and `SearchChunks` (before `:374`). **Order is load-bearing — see Global Constraints.**

```csharp
catch (EmptyEmbeddingInputException ex)
{
    throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    throw new RpcException(new Status(StatusCode.Unavailable,
        $"Embedding service unavailable: {ex.Message}"));
}
```

Add `using Iverson.Embeddings;` if the file does not already have it.

- [ ] **Step 3: Test the consumer filter**

Declare a custom schema rather than using `SchemaFixtures.ArticleSchema()` — see P15. With `maxTokens: 50, overlap: 10` the window arithmetic is `maxChars` 200, `step` 160, so a 360-character interior run is the smallest that guarantees a fully-whitespace window.

The body `"alpha" + new string(' ', 360) + "omega"` is 370 chars and yields exactly three windows: index 0 → `"alpha"`, index 1 → `""` (positions 160–359, all spaces), index 2 → `"omega"`. So the surviving indexes are **0 and 2** — the gap is the assertion that fails a renumbering implementation.

```csharp
[Fact]
public async Task ChunkSplitting_WithAllWhitespaceWindow_DropsItAndPreservesSurvivingIndexes()
{
    var schema = new SchemaDescriptor
    {
        TypeName       = "Doc",
        TableName      = "docs",
        CollectionName = "docs",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Body", "text", false)],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [new ChunkDescriptor("Body", 50, 10, "text-embedding-3-small", 1536)],
        Relations      = [],
        TenantColumn   = "TenantId"
    };
    await _registry.RegisterAsync(schema);

    var body    = "alpha" + new string(' ', 360) + "omega";
    var payload = $$"""{"Body":"{{body}}"}""";
    var ev = new EntityEvent(
        EventType:     EntityEventType.Created,
        TypeName:      "Doc",
        Key:           Guid.NewGuid().ToString(),
        PayloadJson:   payload,
        TraceId:       "trace-empty-window",
        SchemaVersion: "1",
        OccurredAt:    DateTimeOffset.UtcNow,
        TargetStores:  StoreTarget.Intelligence);

    var indexes = new List<string>();
    _vectorWrite
        .UpsertNamedAsync(
            "docs_chunks_test-tenant",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>())
        .Returns(ci =>
        {
            var p = ci.Arg<IReadOnlyDictionary<string, object>?>();
            if (p is not null && p.TryGetValue("chunk_index", out var idx))
                indexes.Add((string)idx);
            return Task.CompletedTask;
        });

    await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

    // The gap is the point: a renumbering implementation yields "0","1" and fails here.
    indexes.Should().Equal("0", "2");
    _ = _embedding.DidNotReceive().EmbedAsync(
        Arg.Is<string>(s => string.IsNullOrWhiteSpace(s)), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 4: Test the clause ordering**

Per P9 the embedding service is a substitute, so configure it to throw. This test pins the **mapping**, not the guard — Task 1's test proves an empty input produces the exception; this proves the boundary translates it rather than swallowing it into `Unavailable`.

```csharp
[Fact]
public async Task SearchSimilar_WithEmptyQuery_ThrowsInvalidArgumentNotUnavailable()
{
    await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
    _embedding.EmbedAsync("", Arg.Any<CancellationToken>())
              .Returns<float[]>(_ => throw new EmptyEmbeddingInputException("Cannot embed empty or whitespace-only text."));

    var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "", TopK = 5 };
    var (writer, _) = MakeStream<SearchResponse>();
    var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

    (await act.Should().ThrowAsync<RpcException>())
        .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
}
```

Add the matching `SearchChunks_WithEmptyQuery_ThrowsInvalidArgumentNotUnavailable`, using
`new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "", TopK = 5 }` and
`_sut.SearchChunks(request, writer, TestServerCallContext.Create())`. Both endpoints need their own
test — the clauses are separate and one can be misordered while the other is right.

- [ ] **Step 5: Run both suites**

```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
```

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "drop all-whitespace chunk windows and refuse an empty search query"
```

## Tasks NOT in this plan

Inherited from the spec's "Out of scope":

- **`ingest.py`.** Already fixed at `4d835c0`; this design deliberately mirrors its shape rather than changing it.
- **The `centroid-ablation` branch.** Its `ingest-contract.json` drift gate pins the C#/Python chunking contract. When that branch lands, the empty-window rule becomes another parity item the contract should pin — a note for that branch, not work here.
- **DLQ behavior.** Unchanged. After this fix the ingest path no longer produces this class of dead letter; the retry-then-dead-letter contract itself is untouched.
- **Model-conditional prefixes** (item 2 of `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md`). Separate work. This fix is a precondition for it: that change sets `DocumentPrefix = ""`, which is what made the empty input reachable on the benchmark path in the first place.
