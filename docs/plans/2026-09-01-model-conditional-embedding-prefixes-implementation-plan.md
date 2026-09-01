# Model-Conditional Embedding Prefixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-01-model-conditional-embedding-prefixes-design.md` (commit SHA: `01ced35`)

**Goal:** Resolve embedding prefixes from the configured model, overridable per deployment, applied identically by the C# and Python ingest paths and pinned against divergence by a generated contract, with the document title composed into the embedded corpus text.

**Architecture:** `EmbeddingServiceOptions` gains two nullable prefix properties; a public `EmbeddingPrefixes` table derives them from the model family when unset. `IEmbeddingService` replaces `EmbedAsync` with `EmbedDocumentAsync`/`EmbedQueryAsync`, each guarding raw text before composition. A generated `ingest-contract.json` carries the family→prefix mapping and per-family golden compositions; `ingest.py` resolves its own family from `--model` and replays its golden before `--drop` acts.

**Tech stack:** .NET 10 (`net10.0`), C# with `ImplicitUsings`; xunit + NSubstitute + FluentAssertions; Python 3 standard library only (`urllib`, `json`).

---

## Global Constraints

Four rules bind every task. Each is silent when broken — no compiler error.

1. **`null` means derive; `""` means deliberately none.** Arctic's document prefix *is* the empty string, so `""` is a legitimate configured value and cannot double as "unset". Never collapse the two.
2. **The family key is everything before the first `:` in the model id, and both languages derive it identically.** Diverging here reads different rows from the same file while the drift gate sees matching *data*.
3. **The task prefix is outermost.** It is applied after contextual composition, so the embedded string is `search_document: {context}\n\n{chunk}`.
4. **The guard tests the caller's raw text, before composition.** After composition the string is non-empty for any non-empty prefix, and the guard silently stops firing.

## File Structure

**Create**
- `Iverson.Server/Iverson.Embeddings/EmbeddingPrefixes.cs` — the family→prefix table and its lookup.
- `Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs` — emits `ingest-contract.json` and gates it against drift.
- `Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json` — the generated contract.

**Modify**
- `Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs` — two nullable prefix properties.
- `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs` — the API split.
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs` — resolution, composition helpers, guard placement, startup log.
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:140`, `:252` — document call sites.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:201`, `:376` — query call sites.
- `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:17` — `NoOpEmbeddingService`.
- `Iverson.Server/Iverson.LoadTest/scripts/ingest.py` — contract loading, `--model`, dimension probe, prefix, empty-window filter, golden replay.
- `Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py:225-232` — title composition.
- `docs/specs/2026-08-26-embedding-prefixes-and-title-design.md`, `docs/plans/2026-08-26-embedding-prefixes-and-title-implementation-plan.md` — supersession headers.

**Test**
- `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` — resolution, composition, guard.
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`, `Grpc/ObjectSearchGrpcServiceTests.cs`, `Grpc/ObjectSearchVectorIntegrationTests.cs`, `Schema/DocumentTemplateValidationTests.cs` — repointed references.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here (A1–A28). The load-bearing ones:

- `services.Configure<EmbeddingServiceOptions>` binds the `"Embeddings"` section (A1); an unset env var leaves a `string?` property `null` (A3).
- Nomic's pair is `"search_document: "` / `"search_query: "`; arctic's is `""` / `"Represent this sentence for searching relevant passages: "`, trailing spaces included (A5, A6).
- Exactly two implementors of `IEmbeddingService` (A8); 89 `EmbedAsync` references across 10 files with four production call sites (A9); nothing outside `Iverson.Server/` calls it (A10); no reflection or DI-by-name on it (A28).
- No contract or drift gate exists on `main` (A13). `ingest.py` has no `--model` argument and `embed()` hard-codes the model (A17). The reuse gate compares raw text (A18). `4d835c0`'s filter applies to main's shape (A19).
- `sample_corpus.py` is the sole `corpus.jsonl` writer (A21); the C# path reads `text` into `Body` (A22); the composed corpus strips the title (A23).

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `Iverson.Embeddings/EmbeddingPrefixes.cs` does not exist | `ls` returned no such file |
| P2 | File path | `Iverson.Api.Tests/Schema/IngestContractTests.cs` does not exist | `ls` returned no such file |
| P3 | File path | `Iverson.LoadTest/scripts/ingest-contract.json` does not exist | `ls` returned no such file |
| P4 | File path | `Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` exists | `NoOpEmbeddingService` declared at `:17` |
| P6 | File path | `Iverson.slnx` is at the repo root | present; the contract test walks up to this marker |
| P7 | File path | Both 2026-08-26 artifacts exist | `docs/specs/…-design.md`, `docs/plans/…-implementation-plan.md` |
| P8 | Signature | `EmbeddingService` takes `IOptions<EmbeddingServiceOptions>` and `ILogger<EmbeddingService>` in its primary constructor, so resolution and logging are both available | `EmbeddingService.cs:9-13` |
| P10 | Signature | `Telemetry.Source` / `Telemetry.HttpClientName` stay reachable when `EmbedAsync` becomes private | `EmbeddingService.cs:53`, `:59` — same assembly, no accessibility interaction |
| P11 | Signature | Reflection reaches the helpers the contract goldens | `KeyToUlong` is `internal static` (`IntelligenceStoreConsumer.cs:685`), `ComputeChunkPointId` is `private static` (`:704`); the existing precedent binds both via `BindingFlags.NonPublic \| BindingFlags.Static` at `IntelligenceStoreConsumerTests.cs:766` |
| P12 | Signature | `ensure_collection(name, vectors_config, payload_indexes=())` takes the dimension inside `vectors_config` | `ingest.py:261` |
| P14 | Command | `dotnet test <csproj>` per project; three projects are affected | `Iverson.Embeddings.Tests`, `Iverson.Api.Tests`, `Iverson.Vector.Tests` all in `Iverson.slnx` |
| P15 | Command | `IVERSON_REGENERATE_INGEST_CONTRACT=1 dotnet test … --filter IngestContract` is the regenerate shape | recorded in the branch contract's own `_generated.regenerate` field |
| P16 | Command | Commit convention is lowercase imperative, no prefix | `git log --oneline -12` |
| P17 | Command | **No Python test runner exists** — no `pytest.ini`, no `conftest.py`, no test files beside the scripts. Task 4 verifies by import, not by suite | `find` returned nothing; see Task 4 Step 7 |
| P18 | Ordering | **Adding two methods to `IEmbeddingService` breaks `NoOpEmbeddingService` immediately**, so Task 1 must update it and must build `Iverson.Api.Tests` | `StartupNoOpFakes.cs:17` implements the interface with `EmbedAsync` at `:22` |
| P19 | Ordering | Task 3 depends only on Task 1's public surface (`EmbeddingPrefixes`, the helpers), not on Task 2 | the emit reads the table and reflects on `ComposeDocumentInput` |
| P20 | Ordering | Task 4 requires Task 3's `ingest-contract.json` to exist on disk | `ingest.py` loads it at module level after Task 4 |
| P21 | Code validity | `public static class` returning a named tuple compiles here | `net10.0`, `ImplicitUsings` enabled (`Iverson.Embeddings.csproj`) |
| P23 | Code validity | `ingest.py` is import-safe offline, so `verify_contract()` can be exercised without Ollama or Qdrant | `def main()` at `:456`, `if __name__ == "__main__":` at `:580`; every `urlopen` is inside a function (`:227`, `:315`) |
| P24 | Consumer impact | NSubstitute substitutes auto-implement new interface members, so only the two concrete implementors need editing | `Substitute.For<IEmbeddingService>()` appears across many test files; none declares members |
| P26 | Consumer impact | `SchemaRegistrationOrchestrator.cs:55` calls `EnsureInitializedAsync`, untouched by the split | read of that line |
| P28 | Command | Task 2's coverage step has a collector to produce output | `Iverson.Api.Tests.csproj:18` — `coverlet.collector` 6.0.2; without it `--collect:"XPlat Code Coverage"` emits nothing |
| P29 | Command | `sample_corpus.py` takes the two arguments Task 5 Step 2 passes | `--corpus-dir` at `:138`, `--out-dir` at `:142`, both `required=True` |

## Tasks

### Task 1: The prefix mechanism, added alongside

**Files:**
- Create: `Iverson.Server/Iverson.Embeddings/EmbeddingPrefixes.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:17`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs`

**Interfaces:**
- Produces: `EmbeddingPrefixes.For(string)`, `ComposeDocumentInput(string, string)`, `ComposeQueryInput(string, string)`, and the two new interface methods — all consumed by Tasks 2 and 3.

This task is deliberately **additive**: `EmbedAsync` stays on the interface so the tree compiles and every existing test still passes. Task 2 removes it.

- [ ] **Step 1: Add the nullable prefix options**

```csharp
public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";
    public string  BaseUrl        { get; set; } = "http://localhost:11434";
    public string  ModelId        { get; set; } = "nomic-embed-text";

    // null means "derive from ModelId"; "" means "deliberately no prefix". These are different:
    // arctic's document prefix IS the empty string, so "" cannot double as unset.
    public string? DocumentPrefix { get; set; }
    public string? QueryPrefix    { get; set; }
}
```

- [ ] **Step 2: Create the derivation table**

```csharp
namespace Iverson.Embeddings;

/// <summary>
/// Task prefixes are model-specific. Running snowflake-arctic-embed under nomic's prefixes measured
/// 0.2236 nDCG@10 on NFCorpus against 0.3304 with its own — a 32% relative loss from four tokens of
/// misconfiguration, which nothing in code or tests noticed.
///
/// public, not internal: IngestContractTests (in Iverson.Api.Tests) emits this table into the
/// ingest contract, and Iverson.Embeddings grants InternalsVisibleTo to nothing.
/// </summary>
public static class EmbeddingPrefixes
{
    public const string DefaultDocument = "";
    public const string DefaultQuery    = "";

    // Keyed by model FAMILY. Ollama ids carry tags — "snowflake-arctic-embed:s",
    // "nomic-embed-text:latest" — so the family is everything before the first ':'.
    // This same rule is applied Python-side in ingest.py; see Global Constraint 2.
    public static readonly IReadOnlyDictionary<string, (string Document, string Query)> Table =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["nomic-embed-text"]        = ("search_document: ", "search_query: "),
            ["snowflake-arctic-embed"] = ("", "Represent this sentence for searching relevant passages: "),
        };

    public static string Family(string modelId)
    {
        var colon = modelId.IndexOf(':');
        return colon < 0 ? modelId : modelId[..colon];
    }

    public static (string Document, string Query) For(string modelId) =>
        Table.TryGetValue(Family(modelId), out var pair) ? pair : (DefaultDocument, DefaultQuery);
}
```

- [ ] **Step 3: Add the two methods to the interface, keeping `EmbedAsync` for now**

```csharp
public interface IEmbeddingService
{
    int           Dimension { get; }
    string        ModelId   { get; }
    Task          InitializeAsync(CancellationToken ct = default);
    Task          EnsureInitializedAsync(CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);   // removed in Task 2
    Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default);
    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);
}
```

- [ ] **Step 4: Resolve the prefixes and implement the two methods**

In `EmbeddingService`, resolve once at construction and add the pure helpers plus the two public methods. The helpers take the prefix as a **parameter** — it is instance state now, so a `static` helper cannot read it, and an instance helper would force the contract's golden case onto a constructed service.

```csharp
private readonly string _documentPrefix =
    options.Value.DocumentPrefix ?? EmbeddingPrefixes.For(options.Value.ModelId).Document;
private readonly string _queryPrefix =
    options.Value.QueryPrefix ?? EmbeddingPrefixes.For(options.Value.ModelId).Query;

internal static string ComposeDocumentInput(string prefix, string text) => prefix + text;
internal static string ComposeQueryInput(string prefix, string text)    => prefix + text;

public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(text))
        throw new EmptyEmbeddingInputException("Cannot embed empty or whitespace-only text.");
    return EmbedAsync(ComposeDocumentInput(_documentPrefix, text), ct);
}

public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(text))
        throw new EmptyEmbeddingInputException("Cannot embed empty or whitespace-only text.");
    return EmbedAsync(ComposeQueryInput(_queryPrefix, text), ct);
}
```

The guard tests **raw** text, before composition — Global Constraint 4.

- [ ] **Step 5: Name the resolved pair in the existing startup log**

`EnsureInitializedAsync` already logs at `EmbeddingService.cs:39-40`. Extend that one line rather than adding a warning, so an unfamiliar model announces its empty fallback where an operator already looks:

```csharp
logger.LogInformation(
    "EmbeddingService initialized: model={Model} dimension={Dimension} documentPrefix={DocPrefix} queryPrefix={QueryPrefix}",
    ModelId, _dimension, _documentPrefix, _queryPrefix);
```

- [ ] **Step 6: Add the two methods to `NoOpEmbeddingService`**

Required for the tree to compile — it is a concrete implementor (`StartupNoOpFakes.cs:17`). Match what it already does:

```csharp
public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default) => Task.FromResult(new float[4]);
public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)    => Task.FromResult(new float[4]);
```

- [ ] **Step 7: Test resolution, composition and the guard**

Add to `EmbeddingServiceTests`, using the existing `CreateService(handler, modelId)` helper:

```csharp
[Theory]
[InlineData("nomic-embed-text",          "search_document: ", "search_query: ")]
[InlineData("nomic-embed-text:latest",   "search_document: ", "search_query: ")]
[InlineData("snowflake-arctic-embed:s",  "",                  "Represent this sentence for searching relevant passages: ")]
[InlineData("some-unknown-model",        "",                  "")]
public void For_ResolvesByFamily_StrippingAnyTag(string modelId, string doc, string query)
{
    var pair = EmbeddingPrefixes.For(modelId);
    pair.Document.Should().Be(doc);
    pair.Query.Should().Be(query);
}

[Fact]
public async Task EmbedDocumentAsync_PrependsTheResolvedPrefix()
{
    var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
    var sut     = CreateService(handler, "nomic-embed-text");

    await sut.EmbedDocumentAsync("hello");

    handler.LastRequestBody.Should().Contain("search_document: hello");
}
```

The guard test must run **with a non-empty prefix configured** — under an empty prefix it would pass against a guard wrongly placed after composition:

```csharp
[Theory]
[InlineData("")]
[InlineData("   ")]
public async Task EmbedDocumentAsync_WithEmptyInput_ThrowsEvenWhenAPrefixWouldMakeItNonEmpty(string input)
{
    var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
    var sut     = CreateService(handler, "nomic-embed-text");   // non-empty document prefix

    var act = async () => await sut.EmbedDocumentAsync(input);

    await act.Should().ThrowAsync<EmptyEmbeddingInputException>();
    handler.LastRequest.Should().BeNull();
}
```

Also assert an explicit `""` override is honoured and distinguishable from unset: construct the service with `new EmbeddingServiceOptions { ModelId = "nomic-embed-text", DocumentPrefix = "" }` and assert the request body carries no `search_document: `. This requires a `CreateService` overload taking options; add one rather than changing the existing helper's signature, so the 16 existing references keep compiling.

- [ ] **Step 8: Run both affected suites**

`Iverson.Api.Tests` is included because Step 6 changed a file in it.

```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 9: Commit**

```bash
git add Iverson.Server/Iverson.Embeddings/EmbeddingPrefixes.cs \
        Iverson.Server/Iverson.Embeddings/EmbeddingServiceOptions.cs \
        Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings/EmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs
git commit -m "resolve embedding prefixes from the configured model"
```

### Task 2: Remove `EmbedAsync` and repoint every caller

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:140`, `:252`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:201`, `:376`
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs`, `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`, `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`

**Interfaces:**
- Consumes: the two methods Task 1 added.

**The compile error is the checklist.** Removing `EmbedAsync` from the interface breaks every one of the 89 references at once; that is the point of the shape, and this task is not done until the tree compiles again.

- [ ] **Step 1: Remove `EmbedAsync` from the interface and make it private**

Delete the `EmbedAsync` line from `IEmbeddingService`. In `EmbeddingService`, change `public async Task<float[]> EmbedAsync` to `private async Task<float[]> EmbedAsync`, and **delete its guard** — after this change its only callers are the two guarded public methods and `EnsureInitializedAsync`'s `"probe"`, so it cannot receive empty input. Delete `EmbedAsync` from `NoOpEmbeddingService`.

- [ ] **Step 2: Repoint the four production call sites**

| site | becomes |
|---|---|
| `IntelligenceStoreConsumer.cs:140` — object vector | `EmbedDocumentAsync` |
| `IntelligenceStoreConsumer.cs:252` — chunk vector | `EmbedDocumentAsync` |
| `ObjectSearchGrpcService.cs:201` — `SearchSimilar` | `EmbedQueryAsync` |
| `ObjectSearchGrpcService.cs:376` — `SearchChunks` | `EmbedQueryAsync` |

A wrong choice here raises no error and produces only a worse number, so Step 4 asserts each one.

- [ ] **Step 3: Repoint the test references**

Distribution: `ObjectSearchGrpcServiceTests` 44, `EmbeddingServiceTests` 16, `IntelligenceStoreConsumerTests` 15, `ObjectSearchVectorIntegrationTests` 4, `DocumentTemplateValidationTests` 1. Query-path stubs become `EmbedQueryAsync`; document-path stubs become `EmbedDocumentAsync`. Do not add a catch-all stub for both — that would restore exactly the silent-attachment the removal exists to prevent.

- [ ] **Step 4: Assert the call sites use the right method**

```csharp
_ = _embedding.Received(1).EmbedDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
_ = _embedding.DidNotReceive().EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
```

— in a consumer test, and the mirror image (`EmbedQueryAsync` received, `EmbedDocumentAsync` not) in a `SearchSimilar` and a `SearchChunks` test.

Also assert **the dimension probe stays unprefixed** (spec §9). It reaches the now-private
`EmbedAsync` directly, so a future refactor routing it through `EmbedDocumentAsync` would silently
prefix it:

```csharp
[Fact]
public async Task EnsureInitializedAsync_ProbesWithoutAPrefix()
{
    var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 0f, 0f]));
    var sut     = CreateService(handler, "nomic-embed-text");   // non-empty document prefix

    await sut.EnsureInitializedAsync();

    handler.LastRequestBody.Should().Contain("\"probe\"");
    handler.LastRequestBody.Should().NotContain("search_document: ");
}
```

- [ ] **Step 5: Branch-coverage diff on the repointed tests**

A re-pointed test can keep passing while silently losing the branch it existed to cover, which has happened in this repository. Compare branch coverage for the five test files against a padded base rather than accepting a green suite:

```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj \
  --collect:"XPlat Code Coverage" --results-directory /tmp/cov-after
```

Compare `Iverson.Api`'s branch percentage against the same command run on `HEAD~1`. Report both numbers in the task report; a drop means a repointed test lost a branch.

- [ ] **Step 6: Run all three suites**

```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
```

- [ ] **Step 7: Commit**

```bash
git add Iverson.Server/Iverson.Embeddings/IEmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings/EmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs \
        Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs \
        Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs
git commit -m "split embedding into document and query intents"
```

### Task 3: The generated ingest contract

**Files:**
- Create: `Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — extract `ChunkWindow`

**Interfaces:**
- Consumes: `EmbeddingPrefixes.Table`, `EmbeddingPrefixes.DefaultDocument`, `ComposeDocumentInput` from Task 1.
- Produces: `ingest-contract.json`, consumed by Task 4.

- [ ] **Step 1: Extract the chunk-window arithmetic so the emit can read it**

`SplitIntoChunks` computes `maxChars` and `step` inline and carries the word-boundary lookback as a
bare `50` (`IntelligenceStoreConsumer.cs:671`), so there is nothing for the contract to read. Lift
them, leaving `SplitIntoChunks` calling the new helper — the shape
`centroid-ablation:IntelligenceStoreConsumer.cs:658` already proved:

```csharp
// internal, not inlined into SplitIntoChunks, because these numbers are a cross-language contract
// rather than an implementation detail: ingest.py must window text identically, and
// IngestContractTests emits them so the two sides cannot drift apart silently.
internal static (int MaxChars, int Step, int Lookback) ChunkWindow(int maxTokens, int overlap)
{
    var maxChars     = maxTokens * 4;
    var overlapChars = overlap * 4;
    var step         = Math.Max(maxChars - overlapChars, maxChars / 2);
    return (maxChars, step, 50);
}
```

Nothing about the arithmetic changes. `Iverson.Api.Tests` already references `Iverson.Api`, so the
emit can call it directly.

- [ ] **Step 2: Write the emit-and-gate test**

One test that both writes the contract (under `IVERSON_REGENERATE_INGEST_CONTRACT=1`) and, by default, asserts a fresh emit equals the committed copy, failing with a diff. Generator and gate are one artefact, which is what keeps "generated" from decaying into "stale".

Locate the file by walking up from `AppContext.BaseDirectory` to the `Iverson.slnx` marker, then to `Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json`. This is a new pattern here, with no precedent to copy.

Emit **exactly these five top-level keys** — `chunkWindow`, `distance`, `collectionNaming`,
`embedding`, `golden` — and nothing else. This is the closed set Task 4 reads; anything further is a
field with no consumer, which the spec's own reasoning for excluding `queryPrefix` rules out.

In particular do **not** carry forward the branch contract's `objectCollection` / `chunksCollection`
blocks. Their `payloadIndexes` and `vectorNames` derive from `SchemaBuilder.ToCollectionSchema`
(`SchemaBuilder.cs:332`), whose list order comes from `Type.GetProperties()` — an order the CLR does
not guarantee, and which also yields a duplicate index for an FK-named scalar column. Emitting them
would require de-duplicating and ordinal-sorting to stop the gate flaking against its own committed
copy. Ben's decision, 2026-09-01: collection-creation parity is **not** pinned by this contract.

The five keys carry no such hazard: the only enumerated collections are `EmbeddingPrefixes.Table`
and the golden map, both literal dictionaries built once by collection initializer and never mutated.

```json
{
  "chunkWindow":   { "maxChars": 2048, "step": 1792, "wordBoundaryLookback": 50 },
  "distance":      "Cosine",
  "collectionNaming": { "base": "benchmark_documents", "template": "{base}{suffix}_{tenant}",
                        "objectSuffix": "", "chunksSuffix": "_chunks" },
  "embedding": {
    "documentPrefixes": {
      "nomic-embed-text": "search_document: ",
      "snowflake-arctic-embed": ""
    },
    "defaultDocumentPrefix": ""
  },
  "golden": {
    "documentComposition": {
      "nomic-embed-text":       { "text": "the quick brown fox", "composed": "search_document: the quick brown fox" },
      "snowflake-arctic-embed": { "text": "the quick brown fox", "composed": "the quick brown fox" },
      "__default__":            { "text": "the quick brown fox", "composed": "the quick brown fox" }
    }
  }
}
```

Every value is read out of the C# path, never written by hand: `chunkWindow` from
`IntelligenceStoreConsumer.ChunkWindow(512, 64)` — the benchmark entity's values, since
`BenchmarkDocument.cs:17` carries a bare `[IversonChunk]` and `IversonChunkAttribute`'s defaults are
`maxTokens = 512, overlap = 64` — `documentPrefixes` from `EmbeddingPrefixes.Table`,
`defaultDocumentPrefix` from `EmbeddingPrefixes.DefaultDocument`, and each `documentComposition`
entry from a reflective call to `ComposeDocumentInput` with that family's prefix and a fixed sample
string.

**Each case carries its input as well as its expected output.** A golden holding only the composed
string gives the Python side nothing to compose *from*: recovering the input by stripping the prefix
makes the check `prefix + composed.removeprefix(prefix) == composed`, which is true for every prefix
including a wrong one. `centroid-ablation:ingest.py:646-648` replays the branch's equivalent the same
way, from `text` against `composed`.

**Query prefixes are deliberately not emitted** — queries are embedded in `Iverson.Api`, so nothing Python-side reads them, and a field with no consumer is dead surface.

**One golden per family, plus the `__default__` case.** A single golden would pin whichever family the emit composed with, and `verify_contract()` — which resolves for its own `--model` — would then fail for every other family, blocking the arctic ingest this mechanism exists to enable.

Reflection for `ComposeDocumentInput` uses `BindingFlags.NonPublic | BindingFlags.Static`, the convention already used at `IntelligenceStoreConsumerTests.cs:766`.

- [ ] **Step 3: Generate the contract and confirm the gate bites**

```bash
cd /home/ben/repositories/Iverson
IVERSON_REGENERATE_INGEST_CONTRACT=1 dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter IngestContract
```

The second run must pass against the committed file. Then hand-edit one character of `ingest-contract.json`, re-run, and confirm it **fails** — a gate that cannot fail is not a gate. Restore the file afterwards.

- [ ] **Step 4: Run the suite and commit**

```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs \
        Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs \
        Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json
git commit -m "generate the ingest contract from the C# write path"
```

### Task 4: `ingest.py` reads the contract and resolves its own prefix

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/ingest.py`

**Interfaces:**
- Consumes: `ingest-contract.json` from Task 3.

- [ ] **Step 1: Load the contract at module level**

Replace `MAX_CHARS` (`:153`), `STEP` (`:154`), the collection-name constants (`:126-127`) and the `"Cosine"` literals (`:504`, `:509`) with values read from `ingest-contract.json`, resolved relative to this script's own directory.

- [ ] **Step 2: Add `--model` and wire it to `embed()`**

`ingest.py` has no `--model` argument today, and `embed()` hard-codes `{"model": "nomic-embed-text"}` at `:309`. Add the argument, default `nomic-embed-text`, and pass it through to the request body.

- [ ] **Step 3: Resolve the document prefix by family**

Global Constraint 2 — the same rule Task 1 implements in C#:

```python
def family(model_id):
    return model_id.split(":", 1)[0]

# NOT a module-level constant: the prefix depends on --model, and args are parsed inside main()
# (ingest.py:483). A function keeps one resolution path that both main() and verify_contract() call,
# which is what lets Step 7 verify the real thing without invoking main().
def document_prefix_for(model_id):
    return CONTRACT["embedding"]["documentPrefixes"].get(
        family(model_id), CONTRACT["embedding"]["defaultDocumentPrefix"])
```

`embed()` takes the resolved prefix from its caller, and `verify_contract(model_id)` takes the model
id and resolves through `document_prefix_for` — the same call `main()` makes.

Apply it inside `embed()` (`:308`), the single `/api/embed` wrapper. This leaves the reuse gate at `:369` comparing raw text and therefore still valid.

- [ ] **Step 4: Probe Ollama for the dimension**

The contract carries `distance` but **no dimension** — it excludes `modelId` and `dimension` deliberately, because configuration and a startup probe own them. So `ensure_collection`'s `size` comes from a probe, not the contract: embed a short fixed string with `--model` and take `len()` of the returned vector.

- [ ] **Step 5: Filter empty chunk windows**

Immediately after `chunks = list(split_into_chunks(body))` (`:362`):

```python
chunks = [c for c in chunks if c[0]]
```

This design makes an empty document prefix reachable through configuration, and the first all-whitespace window would otherwise kill the run at `:331`. Dropping the window rather than renumbering preserves every surviving chunk's original index, so `chunk_point_id` stays stable — the same rule the C# path follows.

- [ ] **Step 6: Replay the golden in `verify_contract()`**

Run it immediately after `parse_args()` and **before `--drop` acts** — dropping against a drifted contract is as damaging as ingesting against one. It replays the resolved family's case by composing `case["text"]` with this script's own resolved
prefix and comparing against `case["composed"]`, exiting non-zero on mismatch — falling back to the
`__default__` case for an unmatched family. Composing from the golden's own `text` is what makes this
a cross-language check rather than a self-comparison.

- [ ] **Step 7: Verify without a live stack**

There is no Python test runner in this repository (P17), so verification is by import — `ingest.py` is import-safe offline, with `main()` at `:456` behind a `__main__` guard and every `urlopen` inside a function.

```bash
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
python3 -m py_compile ingest.py
python3 ingest.py --help | grep -- --model
python3 -c "
import ingest
ingest.verify_contract('snowflake-arctic-embed:s')
ingest.verify_contract('nomic-embed-text:latest')
print('verify_contract OK for both tagged ids')
"
```

Both tagged ids are required: an untagged id passes against a Python side that never strips, so it cannot detect a broken family rule. These two invocations are what assert the C# and Python resolutions agree — the C# side emitted the goldens, the Python side replays them.

- [ ] **Step 8: Commit**

```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/Iverson.LoadTest/scripts/ingest.py
git commit -m "resolve the document prefix from the contract in ingest.py"
```

### Task 5: Title composition and the supersession markers

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py:225-232`
- Modify: `docs/specs/2026-08-26-embedding-prefixes-and-title-design.md`
- Modify: `docs/plans/2026-08-26-embedding-prefixes-and-title-implementation-plan.md`

- [ ] **Step 1: Compose the title into the embedded text**

In the `corpus.jsonl` writer:

```python
title = row.get("title") or ""
text  = row.get("text") or ""
if title.strip():
    text = f"{title.strip()}\n\n{text}"
```

**The guard and the interpolation must test and interpolate the same string.** Guarding on raw `title` while interpolating `title.strip()` composes `"\n\n" + text` for a whitespace-only title. The branch that produced the corpus the spec cites hit exactly this; it affected 3 documents. The separate `title` field stays in the row for display and filtering.

- [ ] **Step 2: Confirm the composition on real data**

```bash
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
python3 -m py_compile sample_corpus.py
```

Then regenerate against the raw source and inspect one row — **not** against `~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/beir/`, whose `corpus.jsonl` already carries composed titles and would double them:

```bash
python3 sample_corpus.py --corpus-dir /home/ben/iverson-benchmark-data/scifact-full --out-dir /tmp/scifact-title-check
head -1 /tmp/scifact-title-check/beir/corpus.jsonl | python3 -c "
import sys, json
d = json.loads(sys.stdin.read())
assert d['text'].startswith(d['title'].strip()), 'title not composed into text'
print('composed OK:', repr(d['text'][:80]))
"
```

- [ ] **Step 3: Mark the superseded artifacts**

Add one line under the title of each, so neither continues to present the const-based design as current:

```markdown
**Superseded by `docs/specs/2026-09-01-model-conditional-embedding-prefixes-design.md`.** Prefixes are
configuration derived from the model there, not `const`. Do not execute this document.
```

- [ ] **Step 4: Commit**

```bash
cd /home/ben/repositories/Iverson
git add Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py
git add -f docs/specs/2026-08-26-embedding-prefixes-and-title-design.md \
           docs/plans/2026-08-26-embedding-prefixes-and-title-implementation-plan.md
git commit -m "compose the document title into the embedded corpus text"
```

`docs/specs` and `docs/plans` are gitignored in this repository, which is why those two paths need `-f`.

## Tasks NOT in this plan

Inherited from the spec's "Out of scope":

- **Landing `embedding-prefixes-and-title` or `centroid-ablation`.** This spec re-derives against `main`; those branches remain unmerged and are not touched.
- **Retiring the superseded spec's artifacts.** Deleting them is out of scope; marking them is not, and Task 5 Step 3 does it.
- **Measuring arctic on SciFact.** The mechanism makes it expressible; running it is a separate decision.
- **Multi-property search and cross-vector fusion.** A separate project.

## Known issues inherited from spec

**Pre-existing collections hold prefix-less, title-less vectors.** Ben's decision, carried forward from 2026-08-26: any collection written before this change holds vectors that are stale in exactly the way a model change makes them stale, and must be re-embedded to be comparable. No migration tooling is built; this is a dev stack with no production data to protect.

**The cited delta is combined and not attributable between prefixes and title.** Separating them costs two further full ingests to explain a result that is not significant either way.

**`corpus.jsonl` is not a verbatim BEIR corpus file** — its `text` carries the title. Deliberate benchmark preparation, stated here rather than left to be discovered by diffing against upstream.

**Centroid numerics differ between the pipelines, independently of this work.** `ComputeCentroid` sums into `float[]` with `MathF.Sqrt` while `ingest.py` computes in float64, so the two produce centroids differing at roughly 1e-7. The golden centroid check states a tolerance rather than asserting exact equality. Far below anything that reorders a result set.

**The contract pins the settings and the goldened algorithms, not all behaviour.** A divergence in a Python code path with no golden case remains undetectable.

**The derivation table is a hard-coded list of two model families.** A third model requires either a code change or explicit configuration. Configuration is the escape hatch, which is why the override exists; the table is not designed to be exhaustive.
