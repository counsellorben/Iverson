# Result Diversification — MMR over Fetched Centroids

Date: 2026-07-30
Status: Approved design, not yet planned or implemented

**Part 4b of the metadata / tensor-search initiative.**

## Context

The initiative adds a metadata layer and server-side tensor scoring to Iverson to improve
semantic-search precision and cut downstream LLM token spend. Its parts are (1) metadata
foundation, (2) Ollama ingest enrichment, (3) tensor re-ranking/fusion, (4) derived vector
signals, and (5) an agent-facing schema/query surface.

Parts 1, 2, 4a and 3 are merged. Part 4a gave every chunked field a `<field>_centroid` named
vector on its object point; part 3 became that vector's first reader, fusing it with Qdrant's
cosine and a recency decay into a single score.

**Base branch:** `main`, at the part 3 merge `5834131`.

### What 4a deferred, and why this spec does not build it

4a's spec split part 4 in two and deferred "4b — cross-corpus cluster centroids" to its own
spec: k centroids across a type/tenant, needing a batch job, a choice of k, a recomputation
trigger and a staleness policy.

**This spec deliberately does not build that.** During brainstorming the consumer for cluster
centroids was named for the first time — result diversification — and Ben confirmed the
motivation is preventative: topical collapse has not been observed in a real corpus, and this
is anticipation rather than a measured failure.

Cross-corpus k-means is the most expensive item remaining on the initiative, and every one of
its required decisions (k, recomputation trigger, staleness tolerance) is a tuning choice with
no obvious right answer and no way to calibrate against a problem that has not been measured.
Diversification does not require it. Greedy maximal marginal relevance over the candidate pool
part 3 *already over-fetches* delivers topical diversity with no batch job, no k, and nothing
that can go stale — the properties that make cluster centroids expensive.

For `SearchSimilar` it also needs no new I/O at all, scoring against the object centroids part 3
already retrieves. For `SearchChunks` it needs one additional retrieve; §2 explains why the
free vector is the wrong one there.

Cluster centroids remain available as a later spec if diversification proves valuable and
corpus structure is wanted for its own sake. Nothing here forecloses them.

## Goal

Stop `SearchChunks` and `SearchSimilar` from returning a `top_k` whose entries are topically
redundant with one another, by selecting the returned set for a blend of relevance and mutual
dissimilarity rather than for relevance alone.

## Design

### 1. The mechanism

Selection replaces part 3's `Take(topK)`. Given the candidates part 3 has already fused and
sorted descending:

1. Select the highest-fused candidate unconditionally. A diversifier that can drop the best
   hit is a regression, not a feature.
2. Repeatedly select the candidate maximising

   ```
   mmr(c) = λ · fused(c) − (1 − λ) · maxSim(c)
   ```

   where `maxSim(c)` is the greatest cosine similarity between `c`'s diversity vector and that
   of any already-selected candidate.
3. Stop at `topK`, or when candidates run out.

**λ = 0.70**, a fixed server-side constant, matching part 3's precedent: no caller-supplied
weights, no per-request opt-out, no kill switch, no configuration knob. The value biases
toward relevance, leaving diversity to act as a tiebreaker rather than a force that reshuffles
good hits.

Blending `fused(c)` with a cosine in one linear expression is only meaningful because they
share a scale: the fused score is a weighted mean of a Qdrant cosine, a centroid cosine and a
decay in `[0,1]` (`ResultReranker.cs:35-51`), so it lives on the same similarity scale as the
term it is blended against.

**Ties break toward the earlier candidate in fused-descending order.** This is what makes the
degradation guarantee in §5 exact rather than approximate.

**`maxSim` is computed incrementally.** Each remaining candidate carries a running maximum,
updated against only the newly-selected candidate after each round, rather than recomputed
against the whole selected set. This is the standard efficient form of greedy MMR and costs
`topK × poolSize` similarity computations in total instead of `topK² × poolSize`.

### 2. The diversity vector

**The diversity vector must live at the same granularity as the thing being returned.** The two
RPCs return different kinds of entry, so they resolve it differently — this is the one place the
two paths genuinely diverge, and the divergence is load-bearing rather than incidental.

- **`SearchSimilar`** — the candidate object's own `<property>_centroid`, already resolved for
  part 3's centroid signal at `ObjectSearchGrpcService.cs:246-250` and reused unchanged. The
  entries are objects and the centroid summarizes an object, so the granularity already matches
  and no new I/O is required.

- **`SearchChunks`** — the candidate chunk's **own vector**, fetched by a second retrieve
  against the **chunks** collection under `<property>_vector` for the over-fetched candidate ids.

  The parent centroid part 3 already has in hand is *not* usable here. Its granularity is the
  document, but the entries are passages, and the mismatch fails in both directions: two
  genuinely distinct passages from one document share a parent centroid exactly, drawing the
  maximum `cos = 1.0` penalty and letting the single most relevant document contribute only one
  passage; while two near-duplicate passages sitting in topically different documents draw a low
  penalty and are not suppressed at all — which is precisely the redundancy a RAG consumer reads.
  The chunk's own vector is the quantity the Goal names.

  The chunk vector is exactly what Qdrant matched the query against, so a cosine between two of
  them measures similarity in the same representation retrieval used. For a non-contextual chunk
  field that representation *is* the passage; for a contextual one it is the passage plus a
  document-context prefix shared by all of that document's chunks (A12). The named vector exists
  and carries this name:
  `SchemaBuilder.ToChunkCollectionSchema` declares `<property>_vector` per chunk field
  (`SchemaBuilder.cs:213`) and the consumer writes the chunk under that name
  (`IntelligenceStoreConsumer.cs:238-242`) — the same name `SearchChunks` already passes to
  `SearchNamedAsync`.

  Consequence to be explicit about: chunks sharing a parent are no longer suppressed merely for
  sharing one. For a non-contextual field they are suppressed exactly to the extent their passages
  resemble each other, so a long document's three distinct sections can all surface when all three
  answer the question. For a contextual field the shared prefix gives same-parent chunks a
  similarity floor, so some parent-level suppression remains — weaker than the parent centroid's
  exact `cos = 1.0`, and still responsive to passage content.

### 3. The contract

A new pure, I/O-free component in `Iverson.Vector`, beside `ResultReranker`:

```csharp
public sealed record DiversifyCandidate(ulong Id, double Score, float[]? DiversityVector);

public interface IResultDiversifier
{
    IReadOnlyList<RerankedResult> Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK);
}
```

Registered `services.AddSingleton<IResultDiversifier, ResultDiversifier>();` beside the
re-ranker at `ServiceCollectionExtensions.cs:50`. The types are `public`, since
`Iverson.Vector`'s `InternalsVisibleTo` names only `Iverson.Vector.Tests` and `Iverson.Api`
consumes them.

Two deliberate choices:

- **Not folded into `IResultReranker`.** Fusion and selection are separate concerns, and part 3's
  re-ranker carries a bit-exactness guarantee with tests to match; widening its contract would
  disturb both.
- **The diversity vector is supplied per candidate, not derived.** The diversifier is therefore
  ignorant of chunk-versus-object semantics — each RPC decides what "diversity" means for it —
  and stays trivially testable. This mirrors `RerankCandidate`'s shape.

`ranked` is expected in fused-descending order, as `Rerank` returns it.

### 4. Composition with part 3

The diversifier runs immediately after `Rerank`, replacing `.Take((int)topK)` at
`ObjectSearchGrpcService.cs:254` and `:395`. Re-ranking itself is untouched: the score returned
to the client remains the fused score, and the re-join from selected id back to the originating
search result is unchanged.

`Rerank` returns `RerankedResult(Id, FusedScore)`, which does not carry a vector, so each call
site pairs the ranked ids with their diversity vectors by id before calling `Diversify`. For
`SearchSimilar` that vector is the one already held in `RerankCandidate.Centroid`; for
`SearchChunks` it comes from the chunk-vector map described below, **not** from
`RerankCandidate.Centroid`, which remains the parent centroid and continues to serve part 3's
re-rank signal unchanged.

**`SearchChunks` acquires its diversity vectors with a second retrieve**, after the search and
alongside part 3's existing parent-centroid retrieve:

```
RetrieveNamedVectorAsync(chunksCollection, candidateIds, "<property>_vector")
```

inside its own `RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(chunksCollection, readOnly: true))`
scope, matching the sequential-scope pattern the file already uses. The collection and vector
name are the ones the search itself just used; the ids are the candidates' own. The two
retrieves address different collections and serve different signals, and neither is a
substitute for the other.

**If the chunk-vector retrieve throws, log and continue with every diversity vector absent** —
selection degrades to the fused order, exactly as §5 specifies for absent vectors. A failed
diversification must never fail a search, matching part 3's treatment of a failed centroid
retrieve.

**Part 3's over-fetch gate stays correct as written.** It fetches `1 × topK` only when
`!centroidPossible && decayField is null` (`:213`). MMR can only ever act when a diversity
vector exists, which requires `centroidPossible` — and whenever that holds, the gate already
takes the `4 × topK` branch. `SearchChunks` never gates at all (`:341-345`), so its pool is
always `4 × topK`. No change to the gate, and no new condition to keep in sync.

### 5. Degradation and edge cases

Following part 3's rule that an absent signal is never replaced by a substituted value:

- **Either vector absent → no similarity term for that pair**, and no penalty. Such a candidate
  scores `λ · fused(c)`, which keeps it on the same scale as penalised candidates.
- **With every diversity vector absent, `mmr(c) = λ · fused(c)` for all candidates.** Since λ is
  positive and identical across candidates, the ordering is exactly the fused ordering, and
  with ties breaking toward the earlier candidate the selected set and its order are
  **identical to today's `Take(topK)`, bit-for-bit.**

Two numeric hazards must be handled, and they require **different** treatment. This was
established empirically rather than from documentation — see the verified-assumptions table:

- **Differing vector lengths throw `ArgumentException`.** Length equality must therefore be
  checked *before* calling `TensorPrimitives.CosineSimilarity`, as part 3 does at
  `ResultReranker.cs:20`. A mismatched pair is treated as having no similarity term. In practice
  both compared vectors come from one collection under one vector name — object centroids for
  `SearchSimilar`, chunk vectors for `SearchChunks` — and Qdrant fixes a named vector's dimension
  at the collection level, so they cannot differ in length. The guard is therefore defensive
  rather than load-bearing — but the failure mode it prevents is a 500 on a search request,
  not a mis-ordering.
- **A zero-magnitude vector returns `NaN`.** A `NaN` similarity is treated as absent. Left
  unguarded, a `NaN` reaching a hand-rolled forward argmax is selected when it appears first in
  iteration order, because every `>` comparison against it is false.

Other cases:

- **Pool smaller than `topK`** — every candidate is returned; MMR determines their order.
- **Empty result set** — returns empty; no similarity work.
- **`topK` of 1** — the highest-fused candidate, selected by step 1 with no similarity work.

### 6. Behavioural change to callers

MMR changes the **order** of returned results, not merely which results are returned: the
top three will no longer necessarily be the three highest-fused candidates. That is the intent
of diversification, but it is a second change to result semantics landing on the same clients
that part 3 already changed by redefining `score` as a fused value. Both RPCs' `score` fields
already carry a comment recording part 3's change (`object_search.proto:86-90` and `:126-129`);
this change is documented alongside it.

### 7. Testing

Formula and selection, in `Iverson.Vector.Tests`:

- λ arithmetic on a hand-computed fixture with all vectors present;
- a lower-fused but dissimilar candidate is promoted over a higher-fused near-duplicate;
- the highest-fused candidate is always selected first, including when it is the most redundant;
- **every diversity vector absent → selected set and order identical to `Take(topK)`, asserted
  exactly**;
- one vector absent → that pair contributes no penalty;
- differing lengths → treated as absent and, specifically, **does not throw**;
- a zero-magnitude vector → `NaN` treated as absent, and a `NaN`-first candidate is not selected
  ahead of a strictly better one;
- pool smaller than `topK`, empty pool, and `topK` of 1.

Service level, in `Iverson.Api.Tests`:

- `SearchChunks` — the diversity vectors come from a retrieve against the **chunks** collection
  under `<property>_vector`, distinct from part 3's parent-centroid retrieve against the object
  collection; both retrieves occur, addressing different collections;
- `SearchChunks` — two near-identical chunks are suppressed relative to a dissimilar one **even
  when all three share a parent**, and two dissimilar chunks sharing a parent are *not*
  suppressed — the pair of cases that distinguishes chunk-level from parent-level diversity;
- `SearchChunks` — the chunk-vector retrieve throwing leaves the results in fused order rather
  than failing the call, and exactly `topK` results are streamed;
- `SearchSimilar` — diversification applies on a dual-annotated property, on an embedding-only
  property the results are unchanged from the fused order, and **no** additional retrieve is
  issued beyond part 3's existing centroid fetch;
- `SearchChunks` — **existing test update:** `SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds`
  must discriminate the two retrieves by collection rather than matching `Arg.Any<string>()`, so it
  continues to assert the parent-centroid retrieve's batching without being overwritten by the
  chunk-vector retrieve.

## Verified assumptions

Verified against the codebase at `main@5834131` before this spec was written.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `Rerank` returns `RerankedResult(ulong Id, double FusedScore)` sorted fused-descending, and both RPCs trim with `Take((int)topK)` | `IResultReranker.cs:9`; `ResultReranker.cs:58`; `ObjectSearchGrpcService.cs:254`, `:395` |
| A2 | The fused score shares a numeric scale with cosine similarity, so one λ blend is meaningful | `ResultReranker.cs:35-51` — weighted mean of a Qdrant cosine, a centroid cosine and a decay in `[0,1]` |
| A3 | **Corrected during verification.** `TensorPrimitives.CosineSimilarity` returns `NaN` for a zero-magnitude vector but **throws `ArgumentException`** for differing lengths and for two empty spans | Executed directly against `System.Numerics.Tensors` 10.0.10 in a scratch xUnit probe. The design originally assumed both cases yielded `NaN`; they do not, and they need different handling |
| A3b | A `NaN` reaching a naive forward argmax is selected when it appears first; LINQ `MaxBy` and `OrderByDescending` treat `NaN` as smallest | Same probe: `NaN`-first forward scan returned the `NaN` index; `MaxBy` returned the true maximum |
| A4 | `SearchChunks` already resolves each candidate's parent centroid at the point selection happens. **Scope note:** this covers part 3's re-rank signal only — §2 establishes that the parent centroid is the wrong granularity to use as `SearchChunks`' diversity vector, which A11 covers instead | `ObjectSearchGrpcService.cs:386-388`, from the distinct-parent-id map built at `:366-379` |
| A5 | `SearchSimilar` already resolves each candidate's own centroid, keyed by candidate id | `ObjectSearchGrpcService.cs:246-250` |
| A6 | A stored centroid can in principle be degenerate: `ComputeCentroid` divides by each chunk vector's magnitude with no zero guard | `IntelligenceStoreConsumer.cs:375-381`. See "Out of scope, but known" — not fixed here |
| A7 | Part 3's over-fetch gate remains correct with MMR added: MMR needs `centroidPossible`, which already forces the `4 ×` branch | `ObjectSearchGrpcService.cs:213`; `SearchChunks` never gates, `:341-345` |
| A8 | The re-ranker's DI registration site takes a sibling singleton, and cross-assembly types must be `public` | `ServiceCollectionExtensions.cs:50`; `Iverson.Vector.csproj:10-12` names only `Iverson.Vector.Tests` |
| A9 | No existing test depends on trim or ordering behaviour in a way MMR breaks | Every test reaching the selection step was checked, not a sample: `ObjectSearchGrpcServiceTests.cs:2333` and `:2445` supply empty centroid maps, so MMR no-ops; `:2485` throws; `:2362`, `:2394` and `:2515` fetch no centroids at all; `:2421` leaves the retrieve unstubbed; `:2540` has a non-empty map but a **single** result, making selection trivial; `:2575`'s two centroids are mutually orthogonal, giving a zero penalty either way. Eight of the nine still pass unchanged. **`:2445` (`SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds`) does not**: its stub matches `Arg.Any<string>()` for the collection, so it intercepts both of `SearchChunks`' retrieves and its captured locals are overwritten by the second — `Received(1)`, `capturedIds.ContainSingle()`, `capturedCollection` and `capturedVectorName` all fail. §7 requires it to be updated |
| A10 | *(Recurrence)* Every requirement above holds for **both** RPCs — diversity-vector source, degradation path, and an exactly-`topK` streaming test | A5 and A11 for the vector source; A7 for the pool; `ObjectSearchGrpcServiceTests.cs:2333` and `:2421` for the streaming tests |
| A11 | Chunk points carry a named vector `<property>_vector` in the chunks collection, retrievable by point id — the vector `SearchChunks`' diversity signal requires | `SchemaBuilder.cs:213` declares one `NamedVector($"{c.PropertyName.ToSnakeCase()}_vector", c.Dimension)` per chunk field on the `_chunks` collection; `IntelligenceStoreConsumer.cs:238-242` upserts each chunk under exactly that name. It is the same name `SearchChunks` already passes to `SearchNamedAsync` (`ObjectSearchGrpcService.cs:352`), and `RetrieveNamedVectorAsync` (A1's sibling, part 3 Task 1) is collection-agnostic |
| A12 | What a chunk vector encodes depends on the contextual-prefix feature: the consumer embeds `PrefixWithContextAsync(documentContext, chunkText)` when `contextualEnabled && cf.Contextual`, and the bare `chunkText` otherwise. `documentContext` — the object summary, or the parent text's first `ParentTextContextChars` — is identical for every chunk of that field in that document | `IntelligenceStoreConsumer.cs:191-202`, embed call at `:202`. No un-prefixed chunk vector is stored, so this is the only chunk representation available |

## Out of scope, but known

**4a's `ComputeCentroid` has no zero-magnitude guard.** `IntelligenceStoreConsumer.cs:375-381`
divides every chunk vector by its own magnitude without checking it is non-zero. A
zero-magnitude chunk vector yields `x / 0` → `NaN` across all dimensions, and that `NaN`
centroid is stored.

This already affects **part 3 in production**: a `NaN` centroid makes `CosineSimilarity` return
`NaN`, the fused score becomes `NaN`, and `OrderByDescending` sinks that document to the bottom
of every result set — silently, with no error logged anywhere.

Deliberately **not** folded into this spec's scope. For it to fire, an embedding model would
have to return an exact zero vector for non-blank text, which the deployed `nomic-embed-text`
realistically will not produce, so it is a robustness gap rather than an active defect. Ben was
shown this finding and elected not to expand scope. The diversifier's own `NaN` guard (§5) means
4b is unaffected either way; the fix, whenever wanted, is one guard in `ComputeCentroid`.

## Known issues

**Diversification cost grows with `top_k × pool size`.** The incremental form in §1 costs
`topK × 4 × topK` similarity computations — quadratic in `top_k`. At the small `top_k` values
RAG consumers actually use this is negligible (`top_k = 10` is 400 comparisons), but part 3
deliberately left `top_k` unclamped, so `top_k = 1000` would mean roughly four million 768-dimension
cosine computations on the request thread. This is accepted on the same reasoning part 3
accepted its uncapped `4N` fetch: the cost is proportional to what the caller explicitly asked
for, and clamping it would be a silent behaviour change. If it ever bites, the fix is a
threshold above which diversification is skipped — not a cap on `top_k`.

**`SearchChunks` gains a second Qdrant round trip per search.** The chunk-vector retrieve (§4)
is an additional call over `4 × top_k` ids, on top of part 3's parent-centroid retrieve. It is
what buys passage-level diversity rather than document-level, and it is paged by the same
batching `RetrieveNamedVectorAsync` already applies. `SearchSimilar` is unaffected and still
issues no retrieve beyond part 3's.

**λ is uncalibrated.** 0.70 is a reasonable default from the MMR literature, not a value tuned
against this corpus, and the motivating problem has not been measured. λ is a compile-time
constant precisely so it can be revised centrally once there is evidence to revise it against.

## Not in this spec

- **Cross-corpus cluster centroids.** Deferred by 4a and still deferred; see Context above.
- **Topic discovery / corpus browsing.** Would need the cluster artifact this spec does not build.
- **A per-parent hard cap.** Not included. With chunk-level diversity vectors (§2), same-parent
  chunks are no longer suppressed merely for sharing a parent — only for actually resembling one
  another, which is the intended behavior. If same-document crowding turns out to need a hard
  limit independent of passage similarity, that is a separate decision on measured evidence.
- **Caller-supplied λ or a per-request opt-out.** Fixed server-side constants, per part 3.
- **The DSL `Search` path and `VECTOR_SIMILAR` clauses.** Scope is `SearchChunks` and
  `SearchSimilar`, matching part 3.
