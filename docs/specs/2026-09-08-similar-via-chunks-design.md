# SearchSimilar via Chunks — Design

Written 2026-09-08 against local `main` `6600810` (`Merge branch 'similar-centroid'`).
Input: the closing finding of `docs/plans/2026-09-GATE-similar-centroid.md` (amendment 2026-09-08)
and the Tier 1 runs in `docs/plans/2026-09-GATE-tier1-defaults.md`.

## 1. Why

Every object-vector representation for `SearchSimilar` has now been measured and closed. The head
vector and the chunk centroid retrieve the same documents once the 4× over-fetch and the 0.45/0.45
fusion have run (`2026-09-GATE-similar-centroid.md`, amendment table), and re-weighting the two is
bounded by an endpoint the shipped path already ties. What remains between `SearchSimilar` and
`SearchChunks` on long documents is the **chunk-level max-passage signal**, which no single vector
per document carries.

That gap is measured on the shipped configuration (`LambdaSimilar` 1.00), pairing the Tier 1
collapsed `.chunks` run against the `.similar` run at λ 1.00 in each arm
(`report-chunks-vs-similar-l100.txt` beside the run files):

| Arm | chunks/doc | nDCG@10 Δ | R@50 Δ | AP Δ |
|---|---|---|---|---|
| `fs-2048` | 3.10 | **+0.0132** [+0.0053, +0.0212] p_adj 0.0016 | **+0.0267** [+0.0153, +0.0381] p_adj 0.0002 | **+0.0118** p_adj 0.0004 |
| `fs-512` | 10.79 | +0.0099 [−0.0006, +0.0204] n.s. | **+0.0140** [+0.0009, +0.0272] p_adj 0.0342 | **+0.0087** p_adj 0.0442 |
| `sci-2048` | 1.27 | +0.0008 n.s. | −0.0117 n.s. | −0.0019 p_adj 0.0010 |

The collapsed `.chunks` run is the closest measurement of this design: the harness requests chunks
from `SearchChunks`, the server fetches 4× that request, fuses and MMR-selects, and the harness
collapses the returned chunks to parents by maximum score (`MaxPassageAggregator.cs:31-56`). The
served path differs in the size of the pool it fuses (§5). Serving that ranking from `SearchSimilar` is worth about a
hundredth of nDCG@10 and two to three hundredths of R@50 on a long-document corpus, and nothing on
abstracts. It is therefore an **option an operator enables per type after measuring the corpus**,
not a default.

## 2. Scope

`SearchSimilar` only, for a property that is both `[IversonEmbedding]` and `[IversonChunk]`
(`centroidPossible`, `ObjectSearchGrpcService.cs:228-229`), on a type the operator has listed in
configuration. Everything else keeps today's path unchanged.

### Out of scope

- `SearchChunks`. Its behaviour is preserved bit for bit; it gains a shared helper, nothing else.
- Any proto or client change. The option is server configuration (Ben, 2026-09-08).
- A cache or ceiling on the density read. Neither is shown to be needed; see Known issues.
- Chunk-level diversification on the routed path. The collapse undoes it.
- A new gate rule. §4 checks that the served ranking reproduces a same-binary collapsed chunk run
  built over the same raw pool, within a tolerance.

## 3. The change

### 3.1 Configuration

One new property on `VectorRankingOptions` (`Iverson.Vector/VectorRankingOptions.cs`):

```csharp
// Types whose SearchSimilar, for a chunked property, ranks documents by chunk retrieval with
// max-passage collapse instead of by the object vector. Empty by default. Enable per type after
// measuring the corpus: see docs/specs/2026-09-08-similar-via-chunks-design.md §1.
public List<string> SimilarViaChunksTypes { get; set; } = [];
```

`AddVectorRanking` (`ServiceCollectionExtensions.cs:6-36`) binds it through the existing
`section.Bind(opts)` — a list binds from indexed keys, `VectorRanking__SimilarViaChunksTypes__0` —
and adds one check alongside the numeric ones: every entry is trimmed and non-blank, else
`InvalidOperationException` naming the section. The existing checks are all numeric, so this is its
own comparison.

`docker-compose.yml` gains, beside `:445-446` on `iverson-api`:

```yaml
- VectorRanking__SimilarViaChunksTypes__0
```

Listed without a value, compose passes the variable through only when it is set in the launching
shell and leaves it unset otherwise, so the default configuration binds no entry. The `${…:-}`
form is not used: an empty string binds as one blank element (verified against the binder), which
the check above would reject at startup. The worker does not serve searches and gets nothing.

### 3.2 The routing decision

`SearchSimilar`'s preamble is unchanged through the embedding of the query
(`ObjectSearchGrpcService.cs:133-214`): `RequireSchema`, authorization (denied → empty stream),
the `vectorDesc` lookup, the collection check, the field-authorization check, the object filter
build, ownership, the log line, and `EmbedQueryAsync`. After it, the request is **routed** when all
of the following hold, and otherwise takes today's head path with no change:

1. `schema.TypeName` is in `SimilarViaChunksTypes` (`OrdinalIgnoreCase`, the registry's own
   comparison, `SchemaRegistry.cs:11`).
2. `schema.ChunkFields` has a descriptor for `vectorDesc.PropertyName` — the existing
   `centroidPossible` test, now also yielding the descriptor.
3. The request filter is **chunk-expressible**, tested in two steps. First, the logic:
   `request.Filter.Count <= 1 || request.FilterLogic == SearchLogic.And`. `BuildChunksFilter`
   never reads `filter_logic` (the file's only read is `:179`, on the object path) and `SearchChunks`
   silently ANDs an OR request today, so the logic test must live here and not in the shared
   function. Second, the clauses: every clause is an EQUALS filter clause on the key column or a
   metadata column, which is what `BuildChunksFilter` (`:870-905`) enforces; it is reshaped to take
   `(SchemaDescriptor, IReadOnlyList<SearchClause>, allowedFields)` and a `TryBuildChunksFilter`
   wrapper returns `false` where it would throw. `SearchChunks` keeps its throwing behaviour by
   calling the same function and rethrowing.
4. Both point counts read successfully (§3.4) and the object count is positive.

Each failed condition on a *listed* type logs one `LogInformation` line,
`[SearchSimilar] type={Type} not routed via chunks: {Reason}`, so an operator can see why a request
kept the head path. Conditions 1 and 2 are cheap and evaluated first; the counts are read only when
1–3 hold, so an unrouted request never pays for them.

The routed branch uses the chunk filter from condition 3 with `ApplyOwnership` applied exactly as
`SearchChunks` does (`:368-372`); the owner field and every metadata column are denormalised onto
each chunk point at ingest (`IntelligenceStoreConsumer.cs:307-314`), so row-level authorization is
identical on both paths. The object filter built in the preamble is not used on the routed branch.

### 3.3 The shared chunk pipeline

The middle of `SearchChunks` — from the vector-name derivation (`:402`) through
`reranker.Rerank` (`:478`), excluding the chunk-vector retrieve at `:449-455` — moves into a private
helper on the service:

```csharp
private async Task<ChunkPipelineResult> SearchChunksFusedAsync(
    SchemaDescriptor schema, ChunkDescriptor chunkDesc, AuthzResult decision,
    float[] queryVector, Filter? filter, ulong chunkLimit)
```

returning the raw `results` (for `parent_id` and `text` payload reads), the fused
`IReadOnlyList<RerankedResult>` from `Rerank`, and the parent-centroid dictionary it fetched. Inside
it is today's code verbatim: resolve `chunksCollection` (`:403`), mint the scoped key (`:414`),
`SearchNamedAsync` with `chunkLimit` (`:417`), `NotFound` → empty (`:419-424`), distinct parent ids
via `KeyToUlong` (`:425-431`), the centroid retrieve with degrade (`:433-438`), decay
(`:457-458`), candidates (`:460-468`), `Rerank`.

`SearchChunks` calls it with `topK * OverFetchFactor` (its current `fetchLimit`, `:410`), then
continues unchanged: chunk-vector retrieve, `Diversify` with `LambdaChunks`, stream. Its existing
tests are the regression guard for the extraction.

### 3.4 The density-scaled budget

For a routed request the chunk limit is the harness's `ChunkBudgetGuard` arithmetic
(`ChunkBudgetGuard.cs:29-41`) with the minimum multiplier applied automatically and the server's
over-fetch on top:

```
chunksPerDoc = ceil(chunkPoints / objectPoints)      // both from the tenant's collections
chunkLimit   = topK * chunksPerDoc * OverFetchFactor  // OverFetchFactor = 4, :749
```

At 1.27 chunks/doc a request for 10 documents fetches 80 chunks; at 10.79 it fetches 440. The two
counts come from one new method on `IVectorQueryService` (`IVectorRoles.cs:4-18`):

```csharp
Task<ulong> GetPointCountAsync(string collectionName);
```

implemented in `IntelligenceVectorService` over `client.GetCollectionInfoAsync(...).PointsCount`
(Qdrant.Client 1.18.1, the call `IntelligenceCollectionManager.cs:65` already makes), with the
same telemetry activity shape as the other methods and `NotFound` propagated as an exception. Both
collection names come from `ResolveCollectionName(schema.CollectionName, decision.TenantValue,
isChunks)` (`IntelligenceTenantScope.cs:11`), so the counts are the tenant's own. Each is read under
its collection's scoped read key, as the searches are.

### 3.5 Collapse, diversify, hydrate, stream

1. **Collapse.** Group the fused chunk results by `parent_id` (`KeyToUlong`,
   `IntelligenceStoreConsumer.cs:701`), keep each parent's maximum `FusedScore`, order descending.
   Ties keep first-seen order, which is the reranked order — the rule `DocumentRanking.CollapseByDocId`
   applies in the harness. A chunk whose payload lacks `parent_id` is dropped.
2. **Diversify.** Feed the collapsed parents to `diversifier.Diversify(..., topK, _ranking.LambdaSimilar)`
   as `DiversifyCandidate(parentId, maxScore, centroid)` with the centroid the helper already
   fetched (`IResultDiversifier.cs:8,14`). At the shipped 1.00 this is `Take(topK)` bit for bit;
   below 1.00 it is the same document-level diversification the head path applies, on the same
   vector.
3. **Hydrate.** One new method on `IVectorQueryService`:

   ```csharp
   Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>>> RetrievePayloadAsync(
       string collectionName, IReadOnlyList<ulong> ids);
   ```

   implemented over `client.RetrieveAsync(collection, ids, payloadSelector: true, vectorSelector:
   false)` in the 512-id batches `RetrieveNamedVectorAsync` uses (`IntelligenceVectorService.cs:155-165`),
   mapping payload values with the same `ToCanonicalString` `SearchNamedAsync` uses (`:137-140`).
   Called once with the top-`topK` parent ids against the object collection under its scoped key.
4. **Stream.** The response block at `:292-323` — `columnLookup`, `ConvertPayloadValue`,
   `UpperFirst` fallback, `MaskDisallowedFields(..., exemptField: "Key")`, `SearchResponse` — moves
   into a private method `WriteSimilarResponseAsync(schema, decision, payload, score, request,
   responseStream, ct)` that both branches call, so the two responses cannot drift. The routed
   branch streams each hydrated parent in collapsed order with its collapsed score. A parent absent
   from the hydration result is skipped with one `LogWarning`; the stream then carries fewer than
   `topK` rows for that request.

### 3.6 Errors and degradation

No new status codes.

| Condition | Behaviour |
|---|---|
| Count read throws on either collection | Fall back to the head path; logged reason `counts unavailable` |
| Object count is 0 | Fall back; reason `empty object collection` |
| Chunks collection `NotFound` at search | Helper returns empty; the stream is empty (as `SearchChunks`, `:419-424`) |
| Centroid retrieve fails | Existing degrade: fusion without the centroid term, diversifier sees absent vectors |
| Hydration `RpcException` | `Unavailable` with the message, the head path's code for a failed Qdrant call (`:217-218`) |
| Individual parent missing at hydration | Skipped and logged (§3.5.4). Window: `HandleDeleteAsync` deletes the object point (`IntelligenceStoreConsumer.cs:553`) before the chunks (`:562`) |
| Embedding failures | Raised in the preamble, unchanged |

Every fallback is decided before any chunk search runs, so a request never pays for both paths.

## 4. The reproduction check

No ingest, no new rule. The served ranking must reproduce the harness's collapsed run.

1. Restore `freshstack-2048-qdrant-snapshots/` per its `RESTORE.md` (both collections, 6,000 and
   18,622 points).
2. Rebuild `iverson-api` from the branch; start it with
   `VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument` and `VectorRanking__LambdaChunks=1.00`
   (the shipped `LambdaSimilar` 1.00 unchanged); confirm both with `docker inspect`.
3. One `benchmark-query` with `--config-label fs2048-routed --chunk-budget-multiplier 4` into
   `freshstack-2048-2026-09-07/runs/`. `DocumentBudget` is 50 (`BenchmarkQueryScenario.cs:42`), so
   the run's `SearchChunks` requests 200 chunks and the server fetches 800 raw; the run's routed
   `SearchSimilar` fetches 50 × 4 × 4 = 800 raw at this arm's 3.10 chunks/doc. `ChunkBudgetGuard`
   passes (200 / 3.10 ≈ 64 ≥ 50). The run writes `fs2048-routed.chunks.trec` (the baseline) and
   `fs2048-routed.similar.trec` (the path under test) from one binary.
4. `report.py --qrels qrels.trec --baseline runs/fs2048-routed.chunks.trec --run
   runs/fs2048-routed.similar.trec` → `report-routed.txt`. A second invocation against the Tier 1
   `fs-2048-l070.chunks.trec` is recorded for continuity only; its `BUILD MISMATCH` warning is
   expected and it is not the rule.
5. Restore the box: `docker compose up -d --no-deps iverson-api` from a shell without the variable.

## 5. The rule

> **PASS** if `fs2048-routed.similar` vs `fs2048-routed.chunks` is within **±0.005** on both nDCG@10
> and R@50 (point estimate), and the report's structural block shows 672 / 672 queries covered
> with no duplicate doc ids.
>
> Anything else is a **FAIL**, and the branch does not merge until the cause is found.

The two rankings are fused on one binary over the same 800-chunk raw pool and differ only in what
they collapse: the harness collapses the first 200 fused chunks (λ 1.00 selection) while the routed
path collapses all 800. They agree wherever the first 200 fused chunks hold at least 50 distinct
parents, so a non-zero delta locates queries where they do not. The Tier 1 `fs-2048-l070.chunks`
run is not a valid reference for the rule: it was fused over a 2,200-chunk raw pool (550 requested
× 4) and MMR-selected at λ 0.70 on a different binary, so neither pool contains the other.

## 6. Consequences

- **PASS:** merge. `SimilarViaChunksTypes` stays empty by default; the ranked-changes document
  gains a line under item 3 recording that the option exists and what it is worth per corpus.
- **FAIL:** the branch stays unmerged. Diagnosis starts with the budget — the routed raw pool
  versus the pool the baseline fused, and the 200-versus-800 collapse boundary — and only then the
  harness pipeline (`MaxPassageAggregator`, `ChunkDiversity`), which is the reference.

The option is permanent, unlike `SimilarRetrievalVector`: its value is corpus-conditional, so the
deployment decides.

## 7. Testing

`ObjectSearchGrpcServiceTests.cs`, using the existing `IVectorQueryService` substitute (`:41`) and
the constructor at `:74-79` with `SimilarViaChunksTypes` set per test:

- Routed: listed type, chunked property, no filter → `SearchNamedAsync` called on the **chunks**
  collection with limit `topK × ceil(c/d) × 4` for stubbed counts (two densities, e.g. 13/10 → 2
  and 108/10 → 11), object search **not** called, `RetrievePayloadAsync` called with exactly the
  top-`topK` collapsed ids, stream in collapsed order with collapsed scores.
- Collapse: a parent with two chunks appears once with its maximum fused score.
- Missing parent at hydration is skipped; stream has one fewer row.
- Not routed, each asserting the object search ran and the chunks search did not: unlisted type;
  listed type with an embedded-only property; listed type with a non-EQUALS clause; with a scalar
  column clause; with `FilterLogic` OR over two clauses; count read throwing; object count 0.
- Ownership: a routed request with `OwnershipRequired` passes the owner condition in the chunks
  filter.
- Field masking: a routed response masks a disallowed column and keeps `Key`.
- `SearchChunks` after the extraction: existing tests pass unchanged; one new test asserts its
  chunk search limit is still `topK × 4`.
- `VectorRankingOptionsTests.cs` (`:71-99` pattern): blank entry throws; default is empty; a
  listed entry round-trips through `Bind`.
- `IntelligenceVectorService`: `GetPointCountAsync` and `RetrievePayloadAsync` are covered by the
  Qdrant integration tests in the same style as `RetrieveNamedVectorAsync`.

## Verified assumptions

Verified 2026-09-08 against `main` `6600810`.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `VectorRankingOptions` binds via `section.Bind` with a validation site | `ServiceCollectionExtensions.cs:6-36`; only `Lambda` is a rejected key (`:9-13`) |
| A2 | Compose carries `VectorRanking__*` on `iverson-api` only | `docker-compose.yml:445-446` |
| A3 | Type-name lookup is case-insensitive | `SchemaRegistry.cs:11` `StringComparer.OrdinalIgnoreCase` |
| A4 | `SearchSimilar` has `vectorDesc` and can find the chunk descriptor | `ObjectSearchGrpcService.cs:143-158`; `:228-229` over `schema.ChunkFields` |
| A5 | `SearchClause`/`SearchLogic` are shared by both requests; `BuildChunksFilter` is static and throws | `object_search.proto:34,74,79`; `ObjectSearchGrpcService.cs:870-905` |
| A6 | The `SearchChunks` middle is separable at the stated boundaries | `:402-478`; the chunk-vector retrieve at `:449-455` depends only on `results` and is after the centroid retrieve |
| A7 | Reader interface lacks count/payload retrieve; Qdrant client has both primitives | `IVectorRoles.cs:4-18`; Qdrant.Client 1.18.1 XML docs list `GetCollectionInfoAsync`, `CollectionInfo.PointsCount`, `RetrieveAsync(..., bool payloadSelector, ...)` |
| A8 | `Rerank` yields `(Id, FusedScore)` | `IResultReranker.cs:3-9` |
| A9 | Diversifier takes an optional vector and returns `RerankedResult` | `IResultDiversifier.cs:8,14`; λ 1.00 = `Take(topK)` per the Tier 1 gate |
| A10 | `VectorSearchResult(Id, Score, Payload<string,string>)`; `KeyToUlong` is reachable | `IVectorRoles.cs:49-52`; `IntelligenceStoreConsumer.cs:701` (`internal static`, same assembly) |
| A11 | The response block depends only on schema, decision, payload, score, trace id | `ObjectSearchGrpcService.cs:292-323` |
| A12 | Collections are per tenant for both chunks and objects | `IntelligenceTenantScope.cs:11`; call sites `:223`, `:403` |
| A13 | Tests substitute `IVectorQueryService` and construct the service with options | `ObjectSearchGrpcServiceTests.cs:41,74-79`; `VectorRankingOptionsTests.cs:71-99` |
| A14 | `BenchmarkDocument.Body` is embedded and chunked; fs-2048 restore is documented; budget is 50 | `BenchmarkDocument.cs:16-18`; `freshstack-2048-qdrant-snapshots/RESTORE.md`; `BenchmarkQueryScenario.cs:42` |
| A15 | Nothing rejects a new list key on the section | Only `Lambda` is rejected, `ServiceCollectionExtensions.cs:9` |
| A16 | Payload by id carries the same columns the search payload does, and the mapping is reusable | Same point; `SearchNamedAsync` maps with `ToCanonicalString` (`IntelligenceVectorService.cs:137-140`); `RetrieveAsync` accepts `payloadSelector: true` |
| A17 | Delete removes the object point before the chunks | `IntelligenceStoreConsumer.cs:553` then `:562` |
| A18 | Both RPC preambles share one shape, so the branch after `SearchSimilar`'s is valid | `:133-158` and `:336-356` |
| A19 | Ownership and metadata are on chunk points, so the chunk filter enforces the same rows | `IntelligenceStoreConsumer.cs:307-314`; `ObjectSearchGrpcService.cs:368-372` |
| A20 | A collection-scoped read key can read that collection's point count | Live probe (CDR round 1): `GET /collections/{name}` returned 200 with `points_count` under a token scoped to that collection and 403 under a token scoped to the other |
| A21 | A chunk's `parent_id` and its parent's object point id agree under `KeyToUlong` | `IntelligenceStoreConsumer.cs:548` (object point id = `KeyToUlong(ev.Key)`) and `:310` (`parent_id` = `ev.Key`); the `SearchChunks` centroid retrieve at `ObjectSearchGrpcService.cs:431-444` already depends on it |
| A22 | An empty-string list entry binds as one blank element, not as no entry | CDR round 1 empirical run, `Microsoft.Extensions.Configuration.Binder` 10.0.11, env and in-memory providers: `count=1 entries=['']` |

## Known issues

- **No ceiling on the chunk limit.** A type with pathological density (hundreds of chunks per
  document) and a large `top_k` produces a large Qdrant limit. It is slow, not wrong. Add a ceiling
  only when a deployment shows one is needed.
- **Two collection-info calls per routed request.** Accepted for now; a cache is deferred until
  measured latency says otherwise.
- **The routed and head paths rank differently for the same type** depending on the filter, because
  a non-expressible filter falls back. Chosen over rejection so enabling the option never breaks an
  existing caller (Ben, 2026-09-08).
- **`points_count` includes points of every field.** A type with two chunked properties counts
  both properties' chunks in one collection, over-estimating density for either property alone.
  The budget is then larger than needed, never smaller.
