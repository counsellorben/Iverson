# Empty-Chunk Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-29-empty-chunk-guard-design.md` (commit SHA: `460b952d0b4a673a978a8ad7d1bf8865743d6bdf`)

**Goal:** Stop whitespace-only chunk windows reaching the embedding service, where they throw and dead-letter the document, and report an empty search query as a bad request rather than a retryable outage.

**Architecture:** Three edits in three production files, each with a test. `SplitIntoChunks` stops emitting empty windows; `EmbedAsync` rejects empty input at the boundary that owns it; the two `ObjectSearchGrpcService` query sites map that rejection to `InvalidArgument`.

**Tech stack:** C# / .NET 10 (`net10.0`, `Nullable` and `ImplicitUsings` enabled), xunit 2.9.3, FluentAssertions, NSubstitute, Grpc.Core.

---

## File Structure

**Modify**
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:667-668` — `SplitIntoChunks` drops whitespace-only windows, preserving surviving indices
- `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:48` — `EmbedAsync` rejects empty input
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:199` and `:370` — map `ArgumentException` to `InvalidArgument`

**Test**
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — chunker behaviour
- `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` — the guard
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — status mapping, both endpoints

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and NOT re-verified here:

- **A1** `SplitIntoChunks` is the only chunker in production code — one hit for `LastIndexOf(' '` outside worktrees
- **A2** `step ≤ maxChars` and `step > 0` always, so the window loop terminates and makes forward progress; gapless coverage additionally requires `step ≤ maxChars − 50`, which every in-use configuration satisfies
- **A3/A6** No caller indexes chunks positionally or depends on their count — only `chunkResults.Length` (`:262`) and `chunks.Count` (`:329`), both for logging
- **A4** No existing test asserts a chunk count this changes — `ChunkSplitting_ProducesMultipleChunks_ForLongText` uses `new string('a', 3000)`, no whitespace
- **A5** Nothing reads `chunk_index` back
- **A7/A17** One production `IEmbeddingService`; the only other is a startup test fake
- **A8/A11** Four production `EmbedAsync` callers; `:140` is already guarded by `IsNullOrWhiteSpace`
- **A9** Ollama embeds whitespace-only input but returns `{"embeddings":[]}` for empty input
- **A10** No existing test calls `EmbedAsync` with empty or whitespace input
- **A13** No existing test asserts `Unavailable` for a search query
- **A14** (recorded failure) Default chunk window is `maxTokens 512` / `overlap 64` → `maxChars 2048`, `step 1792`; 512/448 is the benchmark's `128/16`
- **A15** `InternalsVisibleTo("Iverson.Api.Tests")` plus the `NonPublic | Static` reflection pattern
- **A16** `FakeHttpMessageHandler.LastRequest` supports asserting no HTTP request was issued
- **A19** The zero-chunk state the filter can produce is safe downstream

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1–P6 | File path | All three production files and all three test files exist at the exact paths above | `[ -f ]` on each: 42141, 3179, 47844, 103228, 8208, 159321 bytes respectively |
| P7 | Signature | `SplitIntoChunks(string text, int maxTokens, int overlap)` — the test's positional `Invoke` depends on this order | `IntelligenceStoreConsumer.cs:647` |
| P8 | Code validity | The replaced text is exactly `yield return (text[start..end].Trim(), index++);` followed by `start += step;` | `IntelligenceStoreConsumer.cs:667-668` |
| P9 | Signature | `EmbedAsync(string text, CancellationToken ct = default)` — the guard's `nameof(text)` depends on the parameter name | `EmbeddingService.cs:48` |
| P10 | Code validity | `RpcException`, `Status`, `StatusCode` are already in scope at both query sites | `ObjectSearchGrpcService.cs:201-202` and `:372-373` |
| P11 | Signature | `SearchSimilarRequest` / `SearchChunksRequest` expose a settable `Query` | `ObjectSearchGrpcServiceTests.cs:1013` constructs `new SearchSimilarRequest { …, Query = "test" }` |
| P12 | Code validity | `FakeHttpMessageHandler.LastRequest` is reachable from tests in its own class | `EmbeddingServiceTests.cs:16`, private nested type of the test class |
| P13 | Command | `dotnet test <csproj>` is valid for both test projects | `Microsoft.NET.Test.Sdk` 17.12.0 + `xunit` 2.9.3 in both `.csproj`; both listed in `Iverson.slnx:14,18` |
| P14 | Command | `--filter "FullyQualifiedName~<Class>"` is the right scoping tool | No Makefile/justfile in the repo; `dotnet test` is the only runner |
| **P15** | **Command** | **FAILED — the repo does not use Conventional Commits.** Over the last 60 non-merge commits: 48 have no prefix, 10 use non-Conventional prefixes (`spec:`), only 2 are Conventional. The dominant convention is a plain lowercase imperative sentence | `git log --oneline -60 --no-merges`, counted by pattern. Plan's commit messages corrected to match |
| P16 | Ordering | Tasks 1 and 2 touch disjoint production files, so neither blocks the other | Task 1 → `IntelligenceStoreConsumer.cs`; Task 2 → `EmbeddingService.cs` + `ObjectSearchGrpcService.cs` |
| P17 | Ordering | Task 2's gRPC tests do not depend on Task 2's `EmbeddingService` change | `ObjectSearchGrpcServiceTests.cs:40` — `_embedding = Substitute.For<IEmbeddingService>()`; the mapping test configures the mock to throw |
| P18 | Code validity | `IntelligenceStoreConsumerTests.cs` already imports `System.Reflection` and `FluentAssertions` | file header lines 1 and 3 |
| P19 | Code validity | That file's reflection idiom passes arguments as a **collection expression**, not `new object[]` | `IntelligenceStoreConsumerTests.cs:768` — `method.Invoke(null, [42UL, "Body", 3])!` |
| P20/P21 | Code validity | xunit `[Fact]` + FluentAssertions `Should()`; `ImplicitUsings` supplies `System.Linq` for `.ToList()`; named tuple elements are compile-time only so the `ValueTuple` cast compiles | `Iverson.Api.Tests.csproj:3,5,6` (`net10.0`, `Nullable`, `ImplicitUsings`) |
| P22 | Code validity | A derived `catch` placed before a filtered general `catch` compiles | Precedent: `MessageDispatcher.cs:51,61,65` orders `PoisonMessageException` → `OperationCanceledException` → `Exception` |
| P23–P25 | Consumer impact | Adding the guard and the catch arm breaks no existing caller or test | Covered by inherited A4, A10, A13; additionally no test asserts the `"Embedding service unavailable"` message (only the two production sites contain it) and no test constructs an empty `Query` |
| P26 | Sibling sweep | Every identifier the plan's code blocks name resolves at its point of use — `SplitIntoChunks`, `EmbedAsync`, `RpcException`, `Status`, `StatusCode`, `ArgumentException`, `FakeHttpMessageHandler`, `IntelligenceStoreConsumer`, `BindingFlags` | each confirmed above or BCL |
| P27 | Sibling sweep | The NSubstitute throw idiom and the status assertion the plan reuses in both gRPC tests already exist in the target file | `.Returns<Task<T>>(_ => throw …)` at `:517` and `:612`; `ThrowAsync<RpcException>().Where(e => e.Status.StatusCode == …)` at `:1017` |

## Tasks

### Task 1: Drop whitespace-only chunk windows

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:667-668`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

- [ ] **Step 1: Add the failing test**

```csharp
    [Fact]
    public void SplitIntoChunks_DropsWhitespaceOnlyWindow_AndKeepsSurvivingIndices()
    {
        // maxTokens 128 / overlap 16 → maxChars 512, step 448, so the window at [448, 960)
        // falls entirely inside the 1,200-space run and trims to "". The explicit parameters
        // are load-bearing: under the schema defaults (512/64 → maxChars 2048) this text is a
        // single chunk and would exercise nothing.
        var text = "alpha" + new string(' ', 1200) + "beta";

        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "SplitIntoChunks", BindingFlags.NonPublic | BindingFlags.Static)!;

        var chunks = ((IEnumerable<(string Text, int Index)>)method.Invoke(null, [text, 128, 16])!)
            .ToList();

        // Index 1 is absent, and "beta" keeps index 2 rather than shifting down to 1 — a
        // surviving chunk's index feeds ComputeChunkPointId, so shifting would move its point id.
        chunks.Should().Equal(("alpha", 0), ("beta", 2));
    }
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj \
  --filter "FullyQualifiedName~IntelligenceStoreConsumerTests.SplitIntoChunks_DropsWhitespaceOnlyWindow"
```

Expected failure: three chunks, with `("", 1)` between them.

- [ ] **Step 3: Apply the filter**

In `SplitIntoChunks`, replace `yield return (text[start..end].Trim(), index++);` with:

```csharp
            // A window spanning only whitespace trims to "". Embedding "" sends Ollama an empty
            // input, which comes back {"embeddings":[]} and throws at EmbeddingService's
            // .GetProperty("embeddings")[0] — retried three times, then dead-lettered, leaving
            // the document silently absent from the vector index. Drop the window rather than
            // renumber, so a surviving chunk's index — and thus its point id — does not shift
            // when whitespace elsewhere in the document changes.
            var chunk = text[start..end].Trim();
            if (chunk.Length > 0) yield return (chunk, index);
            index++;
```

- [ ] **Step 4: Run the new test and the whole consumer class**

```bash
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj \
  --filter "FullyQualifiedName~IntelligenceStoreConsumerTests"
```

All green, including the pre-existing `ChunkSplitting_ProducesMultipleChunks_ForLongText`.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "drop whitespace-only chunk windows before embedding"
```

### Task 2: Reject empty embedding input and report it as a bad request

Independent of Task 1 — may land in either order.

**Files:**
- Modify: `Iverson.Server/Iverson.Embeddings/EmbeddingService.cs:48`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:199` and `:370`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

- [ ] **Step 1: Add the failing tests**

In `EmbeddingServiceTests`:

```csharp
    [Fact]
    public async Task EmbedAsync_ThrowsArgumentException_AndIssuesNoRequest_OnEmptyInput()
    {
        var handler = new FakeHttpMessageHandler(SuccessResponse([1f, 2f, 3f]));
        var svc     = CreateService(handler);

        await svc.Invoking(s => s.EmbedAsync(""))
            .Should().ThrowAsync<ArgumentException>();

        handler.LastRequest.Should().BeNull();
    }
```

In `ObjectSearchGrpcServiceTests`, one per endpoint — the fix is made at two call sites, so a single test would leave the other unguarded:

```csharp
    [Fact]
    public async Task SearchSimilar_ThrowsInvalidArgument_WhenQueryIsEmpty()
    {
        _embedding.EmbedAsync("", Arg.Any<CancellationToken>())
            .Returns<Task<float[]>>(_ => throw new ArgumentException("Cannot embed empty input.", "text"));

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Author", Property = "Name", Query = "" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }
```

And the `SearchChunks` counterpart, on the registered chunk field its neighbouring tests use
(`Article` / `Body`, per `ObjectSearchGrpcServiceTests.cs:1377`):

```csharp
    [Fact]
    public async Task SearchChunks_ThrowsInvalidArgument_WhenQueryIsEmpty()
    {
        _embedding.EmbedAsync("", Arg.Any<CancellationToken>())
            .Returns<Task<float[]>>(_ => throw new ArgumentException("Cannot embed empty input.", "text"));

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }
```

- [ ] **Step 2: Run both and watch them fail**

```bash
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj \
  --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
```

Expected: `IndexOutOfRangeException` instead of `ArgumentException` in the first; `Unavailable` instead of `InvalidArgument` in the other two.

- [ ] **Step 3: Add the guard**

At the top of `EmbedAsync`, ahead of the activity span so a rejected call opens no span and never dereferences `text`:

```csharp
        // Ollama answers an empty input with {"embeddings":[]}, which makes the [0] below throw
        // IndexOutOfRangeException — an opaque failure that reads as an embedding-service outage.
        // IsNullOrEmpty, not IsNullOrWhiteSpace: a whitespace-only input embeds successfully.
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Cannot embed empty input.", nameof(text));
```

- [ ] **Step 4: Map it at both query sites**

Ahead of the existing `catch (Exception ex) when (ex is not OperationCanceledException)` at `ObjectSearchGrpcService.cs:199` and again at `:370`:

```csharp
        catch (ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Query cannot be empty."));
        }
```

Genuine outages keep returning `Unavailable`.

- [ ] **Step 5: Re-run both suites**

```bash
dotnet test Iverson.Server/Iverson.Embeddings.Tests/Iverson.Embeddings.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj \
  --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
```

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.Embeddings/EmbeddingService.cs \
        Iverson.Server/Iverson.Embeddings.Tests/EmbeddingServiceTests.cs \
        Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "reject empty embedding input and report it as InvalidArgument"
```

## Tasks NOT in this plan

Inherited from the spec's "Not doing: a backfill", verbatim:

> An earlier draft included recovering already-dead-lettered documents. It was cut after checking: the dev stack's DLQ holds **65 rows, all `MySqlConnector.MySqlException`** (StarRocks connection failures), and the reconciliation queue is empty. Zero rows are attributable to this defect, and there is no deployed environment — CI has `codeql.yml` and `deploy-validate.yml`, no deploy job. A backfill would recover nothing.
>
> The corpora known to contain whitespace-only windows were ingested through `ingest.py`, whose document prefix made the input non-empty; the C# consumer has never processed one.

## Known issues inherited from spec

> **`POST /admin/dlq/{id}/replay` republishes the stale stored value** (`Program.cs:382-392`), for every source topic. Replaying a dead-lettered event for an entity updated since it failed would regress the index to the older state. Should this defect ever dead-letter a document, recovery is a current-state republish — the path `ReconciliationService.ProcessOneAsync` already takes, re-reading via `FetchByKeyAsync` — not this endpoint. Unrelated to the chunker fix; recorded so the hazard is not rediscovered from scratch.
>
> **`ingest.py`'s chunker replica** must carry the same filter to stay faithful. It already does (drop the window, preserve the index), but that change lives on an unmerged experiment branch, not on `main`.
