# Empty embedding input: drop it at ingest, refuse it at the boundary

**Status: design, approved 2026-09-01.** Verified against `main` at `a03d353`.

## The problem

`EmbeddingService.EmbedAsync` sends its argument to Ollama's `/api/embed` and reads the
response as `.GetProperty("embeddings")[0]` (`EmbeddingService.cs:73`). For an empty input
Ollama returns `{"embeddings": []}`, so the indexer throws `IndexOutOfRangeException`.

Two production paths can reach it.

**Ingest.** `SplitIntoChunks` yields `text[start..end].Trim()` (`IntelligenceStoreConsumer.cs:667`)
with no empty filter. A window falling entirely inside a run of whitespace strips to `""`. The
guard at `:226` tests the whole field, never the individual window, so the empty string reaches
`EmbedAsync` at `:244`.

The consequence is **silent per-document loss, not a crash**. `MessageDispatcher.DispatchAsync`
(`MessageDispatcher.cs:38`) retries three times with backoff, dead-letters, and returns — so
`KafkaConsumer` commits the offset and ingestion continues. The document is never indexed, and it
lands in the DLQ tagged `IndexOutOfRangeException`, a type that says nothing about whitespace.
Measured frequency on real data: 28 windows across 17 of FreshStack's 6,000 documents (0.28%).

**Query.** `request.Query` goes straight to `EmbedAsync` at `ObjectSearchGrpcService.cs:201`
(`SearchSimilar`) and `:372` (`SearchChunks`) with no emptiness check. proto3 defaults an absent
string to `""`, so **every caller who omits `query` is told
`Unavailable — Embedding service unavailable`**. That is wrong twice: the service is healthy, and
the fault is the caller's. The catch-all at `:203` swallows the real cause.

A third caller, the object-vector path at `:140`, is already guarded by
`.Where(x => !string.IsNullOrWhiteSpace(x.text))` and needs no change.

Commit `4d835c0` fixed the equivalent bug in `ingest.py`, the Python benchmark ingest path only.
The C# path was never touched. Because `main` sends no document prefix at all, nothing masks the
case here — the mask described in `docs/2026-08-29-reranker-design-parameters.md` §3 (nomic's
non-empty `search_document: `) exists only on the `centroid-ablation` branch.

## Design

### 1. A typed exception

New file `Iverson.Server/Iverson.Embeddings/EmptyEmbeddingInputException.cs`:

```csharp
namespace Iverson.Embeddings;

/// <summary>
/// Text offered for embedding was empty or whitespace-only. Transport-neutral by design —
/// <see cref="EmbeddingService"/> has no dependency on gRPC or Kafka; callers that need a
/// transport-specific error (e.g. an RpcException) translate this at their boundary.
/// </summary>
public sealed class EmptyEmbeddingInputException(string message) : Exception(message);
```

This mirrors `Iverson.Vector/FilterTranslationException.cs` in shape and in doc-comment intent;
that type's comment already states this exact translate-at-the-boundary contract.

### 2. `EmbeddingService` guards its own input

At the top of `EmbedAsync`, before the request is built:

```csharp
if (string.IsNullOrWhiteSpace(text))
    throw new EmptyEmbeddingInputException("Cannot embed empty or whitespace-only text.");
```

`IsNullOrWhiteSpace`, not `Length == 0`, **so the backstop and the pre-filter agree**. Section 3
filters text that `SplitIntoChunks` has already `Trim()`ed, so "empty after trim" and
"whitespace-only before trim" denote the same set; a narrower predicate here would let `"   "`
past the backstop while the pre-filter drops it. It is also the predicate this codebase already
uses for the same question at `IntelligenceStoreConsumer.cs:226` and `:139`.

The initialization probe passes `"probe"` (`EmbeddingService.cs:37`), so the guard cannot break
service startup.

### 3. The ingest consumer pre-filters

At `IntelligenceStoreConsumer.cs:229`:

```csharp
var chunks = SplitIntoChunks(text, cf.MaxTokens, cf.Overlap)
    .Where(c => c.Text.Length > 0)
    .ToList();
```

**Indexes must not be renumbered.** `SplitIntoChunks` assigns `index++` inside the iterator
(`:667`), so filtering afterwards leaves every survivor's original index intact.
`ComputeChunkPointId` (`:696`) is pure arithmetic over `(parentId, fieldName, chunkIndex)`, so a
preserved index means a stable point id across re-ingests. This is the same ordering `ingest.py`
relies on, and it is why both sides filter after the generator rather than inside it.

**Filtering before the contextual-prefix call is load-bearing, not incidental.** With contextual
chunking enabled, an unfiltered empty window would reach `PrefixWithContextAsync`, which returns
`$"{prefix.Trim()}\n\n{chunkText}"` (`:628`). For an empty `chunkText` that is a non-empty string
containing only an LLM-generated prefix — so the guard in section 2 would never fire, and the
system would embed a pure hallucination and store a chunk point whose `text` payload is `""`. A
garbage vector entering the index silently is worse than the dead-letter. Filtering first also
avoids a wasted enrichment round-trip per empty window.

The list cannot come out empty: `:226` has already rejected an all-whitespace field, so at least
one window carries content. `ComputeCentroid` (`:470`) dereferences `vectors[0]` and requires a
non-empty list; that precondition is preserved.

The only other use of `chunks` is the log at `:329`, which today over-reports
`Ingested {Count} chunk(s)` when windows are empty. After the filter it reports what was written.

### 4. The query paths translate it

In `SearchSimilar` and `SearchChunks`, **above** the existing catch-all:

```csharp
catch (EmptyEmbeddingInputException ex)
{
    throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
}
```

Same mapping `ObjectSearchGrpcService.cs:179` already applies to `FilterTranslationException`.

**Clause order is load-bearing and the compiler will not enforce it.** The existing catch-all is
`catch (Exception ex) when (ex is not OperationCanceledException)`. Because it carries a `when`
filter, placing the new clause *after* it raises no CS0160 — it compiles clean and is simply never
reached at runtime, leaving the misleading `Unavailable` in place. Only a test catches this.

Catching was chosen over validating `request.Query` up front: up-front validation would restate the
emptiness rule in a third location and let it drift from `EmbeddingService`'s definition.

## Testing

- **Consumer:** a body with an interior whitespace run long enough to swallow a whole window. The
  window arithmetic is `maxChars = maxTokens * 4` and `step = max(maxChars - overlap * 4,
  maxChars / 2)`, and a window is empty only when it lies wholly inside the run — so a run of at
  least `maxChars + step` characters guarantees one. At the existing test schema's
  `maxTokens: 50, overlap: 10` that is 200 + 160 = **360** whitespace characters.
  Assert `EmbedAsync` is never called with empty or whitespace-only text, and — the assertion that
  actually falsifies a wrong implementation — that the surviving chunks' `chunk_index` payload
  values are **non-contiguous**. An implementation that renumbers passes a "never embeds empty"
  test and fails this one.
- **`EmbeddingService`:** `EmbedAsync("")` and `EmbedAsync("   ")` throw
  `EmptyEmbeddingInputException`; a non-empty input still returns its vector.
- **gRPC:** `SearchSimilar` and `SearchChunks` with an empty `Query` return `InvalidArgument`, not
  `Unavailable`. This is the test that pins section 4's clause ordering.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | `Iverson.Api` references `Iverson.Embeddings` | `Iverson.Api.csproj:32` |
| A2 | Exception convention is `sealed class X(string message) : Exception(message)` in its own file | `Iverson.Vector/FilterTranslationException.cs:8` |
| A3 | `EmbedAsync` is the only path to `/api/embed` | `EmbeddingService.cs:48`, `:60`; `IEmbeddingService.cs:9` |
| A4 | The init probe passes non-empty text | `EmbeddingService.cs:37` — `EmbedAsync("probe", ct)` |
| A5 | `Index` is assigned inside the iterator, so post-filtering preserves it | `IntelligenceStoreConsumer.cs:667` — `yield return (text[start..end].Trim(), index++)` |
| A6 | The tuple element is named `Text` | `:647` — `IEnumerable<(string Text, int Index)>` |
| A7 | Nothing downstream needs a contiguous or fixed-length chunk list | Only other use is the log at `:329` |
| A8 | `ComputeChunkPointId` is a pure function of the index value | `:696-697` |
| A9 | The field-level guard runs before the split | `:226` immediately precedes `:229` |
| A10 | `ComputeCentroid` requires a non-empty list | `:470-472` dereferences `vectors[0]` |
| A11 | Both query call sites sit in a catch-all mapping to `Unavailable` | `ObjectSearchGrpcService.cs:199-207`, `:370-378` |
| A12 | A clause placed after the filtered catch-all compiles clean and never runs | The catch-all carries `when (ex is not OperationCanceledException)`, so CS0160 does not apply |
| A13 | Callers depend on `IEmbeddingService`, so the type must be public from a shared project | `IntelligenceStoreConsumer.cs:32`, `ObjectSearchGrpcService.cs:35` |
| A14 | No existing test asserts the current behavior | No hits for `EmbedAsync("")` or an empty `Query` in any test project |
| A15 | Exactly five production callers, all accounted for | probe `:37`; guarded `:140`; fixed `:244`; mapped `:201`, `:372` |
| A16 | `EnrichmentService` never embeds | No `EmbedAsync` in `EnrichmentService.cs` |
| A17 | `PrefixWithContextAsync` runs after the filter and can mask an empty chunk | `:628` returns `$"{prefix.Trim()}\n\n{chunkText}"` |
| A18 | No reference cycle | `Iverson.Embeddings` is a leaf of `Iverson.Api` |
| A19 | A test project can see the new type | `Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` exists |
| A20 | A harness exists to drive an empty `Query` | `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` |
| A21 | The dispatcher retries 3x, dead-letters, then commits | `MessageDispatcher.cs:17-90` |

## Out of scope

- **`ingest.py`.** Already fixed at `4d835c0`; this design deliberately mirrors its shape rather
  than changing it.
- **The `centroid-ablation` branch.** Its `ingest-contract.json` drift gate pins the C#/Python
  chunking contract. When that branch lands, the empty-window rule becomes another parity item the
  contract should pin — a note for that branch, not work here.
- **DLQ behavior.** Unchanged. After this fix the ingest path no longer produces this class of
  dead letter; the retry-then-dead-letter contract itself is untouched.
- **Model-conditional prefixes** (item 2 of `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md`).
  Separate work. This fix is a precondition for it: that change sets `DocumentPrefix = ""`, which
  is what made the empty input reachable on the benchmark path in the first place.
