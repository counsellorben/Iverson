# Empty-chunk guard

## Problem

`SplitIntoChunks` (`IntelligenceStoreConsumer.cs:647`) yields `text[start..end].Trim()` with no
empty filter. A window spanning only whitespace trims to `""`, and the chunk-embedding call at
`IntelligenceStoreConsumer.cs:244` passes it straight to `EmbedAsync`. Ollama answers an empty
input with `{"embeddings":[]}`, and `EmbeddingService.cs:89`'s `.GetProperty("embeddings")[0]`
throws.

Nothing guards it: `IntelligenceStoreConsumer.cs:226` checks the whole field for whitespace,
never the individual chunk.

The throw is an ordinary exception, so `MessageDispatcher` retries three times with backoff and
then dead-letters (`MessageDispatcher.cs:26-27`). The document is **silently absent from the
vector index**, and every chunk in it is embedded three extra times first.

Measured frequency on the FreshStack corpus: 28 whitespace-only windows across 17 of 6,000
documents (0.28%), using the shipped chunk window. It killed a real ingest at document 383.

A second, milder instance of the same defect: an empty search query reaches the same call at
`ObjectSearchGrpcService.cs:197` and `:368`, where the surrounding handler reports
`Unavailable: "Embedding service unavailable: Index was outside the bounds of the array."` —
telling the caller to retry a request that can never succeed.

## Design

### 1. Stop emitting empty chunks

In `SplitIntoChunks`:

```csharp
var chunk = text[start..end].Trim();
if (chunk.Length > 0) yield return (chunk, index);
index++;
start += step;
```

The window is **dropped, not renumbered** — `index` advances regardless. A surviving chunk's
index, and therefore its Qdrant point id via `ComputeChunkPointId`, then does not shift when
whitespace elsewhere in the document changes. Gaps in `chunk_index` are inert: nothing reads it
back, and the write path deletes all points for `(parent_id, field)` before writing.

### 2. Reject empty input at the embedding boundary

In `EmbeddingService.EmbedAsync`:

```csharp
if (string.IsNullOrEmpty(text))
    throw new ArgumentException("Cannot embed empty input.", nameof(text));
```

`IsNullOrEmpty`, not `IsNullOrWhiteSpace`: a whitespace-only input embeds successfully today
(verified against Ollama), so rejecting it would be a behaviour change beyond this defect. The
chunker already trims, so no chunk reaches here as whitespace.

### 3. Correct the status for an empty query

At `ObjectSearchGrpcService.cs:197` and `:368`, add an arm ahead of the existing catch:

```csharp
catch (ArgumentException)
{
    throw new RpcException(new Status(StatusCode.InvalidArgument, "Query cannot be empty."));
}
```

Genuine embedding outages keep returning `Unavailable`. This is a deliberate behaviour change:
callers that today see `Unavailable` for an empty query will see `InvalidArgument`.

### 4. Tests

There are currently **no tests for `SplitIntoChunks` anywhere**. These are the first.

- **Chunker.** `"alpha" + new string(' ', 1200) + "beta"`, split with **explicit `maxTokens: 128,
  overlap: 16`** (→ `maxChars 512`, `step 448`), yields `("alpha", 0)` and `("beta", 2)`: index 1
  absent, surviving indices unchanged. The explicit parameters are required — under the schema
  defaults (`512/64` → `maxChars 2048`) this text collapses to a single chunk and exercises
  nothing. Reached via the `BindingFlags.NonPublic | BindingFlags.Static` reflection pattern the
  file already uses for `ComputeChunkPointId`.
- **EmbeddingService.** `EmbedAsync("")` throws `ArgumentException` and issues no HTTP request
  (`FakeHttpMessageHandler.LastRequest` stays null).
- **Search.** An empty query yields `InvalidArgument`, not `Unavailable`.

### Side effect

With the filter in place an empty window never reaches `PrefixWithContextAsync`, so the phantom
chunk under contextual chunking — a point with `text: ""` carrying the document context's vector,
matchable by queries but showing no text — cannot be written either. Same defect, one step later.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | `SplitIntoChunks` is the only chunker in production code | one hit for `LastIndexOf(' '` outside worktrees: `IntelligenceStoreConsumer.cs:663` |
| A2 | `step ≤ maxChars` and `step > 0` always, so the window loop terminates and makes forward progress | `step = max(maxChars − overlapChars, maxChars/2)`; zero violations across `maxTokens`×`overlap` = 600×600. This does **not** establish gapless coverage: the word-boundary adjustment (`IntelligenceStoreConsumer.cs:661-664`) pulls `end` back by up to 50 characters, so coverage is gapless only when `step ≤ maxChars − 50`. Every configuration in use satisfies it (default 2048/1792; benchmark 512/448), but `overlap = 0` — reachable, since `SchemaBuilder.cs:70-72` passes `ChunkOverlap` through unvalidated, unlike the document path's default at `:157` — drops up to 50 characters per boundary (measured: 13 at `maxTokens 128`). A zero-overlap chunk field can therefore yield an empty chunk list |
| A3/A6 | No caller indexes chunks positionally or depends on their count | only uses are `chunkResults.Length` (`:262`) and `chunks.Count` (`:329`), both for logging |
| A4 | No existing test asserts a chunk count that this changes | `ChunkSplitting_ProducesMultipleChunks_ForLongText` uses `new string('a', 3000)` — no whitespace, so no empty windows |
| A5 | Nothing reads `chunk_index` back | written at `:297`; elsewhere only as a reserved-key name in `SchemaBuilder.cs:26` and `SchemaRegistrationOrchestrator.cs:603` |
| A7/A17 | One production `IEmbeddingService`; the only other is a startup test fake | `EmbeddingService`, plus `NoOpEmbeddingService` in `StartupNoOpFakes.cs:17` |
| A8/A11 | Four production `EmbedAsync` callers; the guard covers all | `:140` (already guarded by `IsNullOrWhiteSpace`), `:244` (this defect), `ObjectSearchGrpcService.cs:197` and `:368` (user query) |
| A9 | Ollama embeds whitespace-only input but not empty input | live: `input:"   "` → 1 embedding, dim 384; `input:""` → `{"embeddings":[]}` |
| A10 | No existing test calls `EmbedAsync` with empty or whitespace input | no matches across `Iverson.Server/` |
| A13 | No existing test asserts `Unavailable` for a search query | the only `Unavailable` assertion is `Pipeline_StarRocksNotReady_ThrowsUnavailable` |
| A15 | Tests can reach a private static member without changing production visibility | `InternalsVisibleTo("Iverson.Api.Tests")` in `Iverson.Api.csproj:10`, and `GetMethod("ComputeChunkPointId", BindingFlags.NonPublic \| BindingFlags.Static)` at `IntelligenceStoreConsumerTests.cs:765` |
| A16 | The embedding tests can assert that no HTTP request was issued | `FakeHttpMessageHandler.LastRequest` at `EmbeddingServiceTests.cs:16` |
| A14 | **FAILED.** Default chunk window is not 512/448 | `SchemaBuilder.cs:156-157`: defaults are `maxTokens 512`, `overlap 64` → `maxChars 2048`, `step 1792`. 512/448 is the *benchmark's* `128/16`. Fix is mechanical: the chunker test passes explicit parameters |
| A19 | The zero-chunk state the filter can now produce is safe downstream | `Task.WhenAll` on an empty task list completes normally (`:249`); the centroid write is gated on `centroidInput.Count > 0` (`:287`), so `ComputeCentroid` — which throws on an empty list — never receives one; the chunk upsert sits inside the `foreach` over `chunkResults` (`:290`), so no empty-list write is issued; `chunks.Count` feeds only a log line (`:329`) |

## Not doing: a backfill

An earlier draft included recovering already-dead-lettered documents. It was cut after checking:
the dev stack's DLQ holds **65 rows, all `MySqlConnector.MySqlException`** (StarRocks connection
failures), and the reconciliation queue is empty. Zero rows are attributable to this defect, and
there is no deployed environment — CI has `codeql.yml` and `deploy-validate.yml`, no deploy job.
A backfill would recover nothing.

The corpora known to contain whitespace-only windows were ingested through `ingest.py`, whose
document prefix made the input non-empty; the C# consumer has never processed one.

## Known issues, accepted as out of scope

**`POST /admin/dlq/{id}/replay` republishes the stale stored value** (`Program.cs:382-392`), for
every source topic. Replaying a dead-lettered event for an entity updated since it failed would
regress the index to the older state. Should this defect ever dead-letter a document, recovery is
a current-state republish — the path `ReconciliationService.ProcessOneAsync` already takes,
re-reading via `FetchByKeyAsync` — not this endpoint. Unrelated to the chunker fix; recorded so
the hazard is not rediscovered from scratch.

**`ingest.py`'s chunker replica** must carry the same filter to stay faithful. It already does
(drop the window, preserve the index), but that change lives on an unmerged experiment branch,
not on `main`.
