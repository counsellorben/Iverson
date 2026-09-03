# Cross-encoder reranking for vector search — design

Status: design, verified against the codebase 2026-09-03.
Source proposal: `docs/2026-08-29-reranker-design-parameters.md` (parameters P1–P8).

## 1. Why

Retrieval quality is bounded by *ordering*, not retrieval. On SciFact, R@50 = 0.910 and oracle@50 =
0.911 — the right documents are already in the candidate pool, and essentially the entire remaining
gap is the order they come back in.

The *oracle ceiling* (what nDCG@10 becomes if the candidates we already retrieve were reordered by
true relevance) is computable from preserved TREC run files:

| corpus | endpoint | actual | oracle@10 | headroom |
|---|---|---|---|---|
| SciFact | chunks | 0.6954 | 0.8273 | **+0.2154** |
| SciFact | similar | 0.6664 | 0.7955 | +0.2039 |
| NFCorpus | chunks | 0.3522 | 0.4157 | **+0.2067** |
| NFCorpus | similar | 0.3398 | 0.4020 | +0.1788 |

**+0.21 nDCG@10 against the ±0.02 every fusion-weight sweep produced** — an order of magnitude more
headroom than any constant-tuning lever remaining.

Only these four rows are evidence. The source proposal also tabulates ArguAna (+0.4197) and
FreshStack (+0.3799); both ran arctic under *nomic's* prefixes, and a badly-encoded query degrades
ordering, which **inflates** apparent reranking headroom. Those two rows are excluded from the case
for this work.

## 2. Scope

**In scope:** a cross-encoder reranking stage for `SearchChunks`, delivered in two phases, with a
statistical gate between them.

**Out of scope, deliberately:**

- **`SearchSimilar`.** Its candidates are whole field texts. Measured on the real SciFact workload,
  **7 of 50 candidates exceed the cross-encoder's 512-token window** (mean ~395 tokens, max ~696), so
  reranking it requires a truncation policy (head / tail / best-window) that is its own design
  decision with its own quality consequences. Chunks fit whole **at the 128-token chunking used
  for measurement**; at `main`'s default `[IversonChunk]` (`maxTokens = 512`, a 2,048-character
  window) a SciFact chunk averages ~308 tokens and **16.8 % exceed 512 tokens once the query is
  prepended**, so Phase 2 carries a truncation policy regardless — stated in §3.5, not deferred.
- **Migrating embeddings from Ollama to TEI.** Measured at 1.57× faster with a published quality
  upgrade at unchanged dimensions, and genuinely attractive — but changing the embedding model would
  move the baseline this reranker is measured against — the rule established by the embedding-prefix
  work, which recorded explicitly: do not fix the encoder and run the sweep at the same time. It
  would also invalidate every number in §1, all of which were computed from nomic-based run files.
  Reranking is query-time only and needs no re-ingest, so bundling buys no efficiency. Separate spec.
- **Re-tuning `WCentroid` / `w`.** Required eventually (see §7.4) but a separate arm family measured
  against R@K, run only after a reranker exists.

## 3. Architecture

### 3.1 Stage order

```
Qdrant topK
  -> ResultReranker      (fusion: base / centroid / decay)
  -> ResultDiversifier   (MMR)            <- composes the candidate pool
  -> max-passage select  <- NEW, enabled only: one winning chunk per parent, top K = 50 parents
  -> CrossEncoderScorer  <- NEW, enabled only: rescores the 50 winning chunks
  -> stream response
```

**The unit reranked is the document, scored through its max-passage chunk** — P2's unit, restored.
Reranking the top 50 *chunks* instead does not work: at the measured 3.85 chunks per document on the
Phase 1 baseline (`keymap.json.stats.json`: 19,967 chunks / 5,183 documents), 50 chunks collapse to
~13 documents after aggregation, which drops R@50 and breaks §7.1's invariance control. The
codebase already names this failure — `ChunkBudgetGuard.cs:3-17`: "a nominally-50-document budget
collapses to far fewer distinct documents after max-passage aggregation" — but the guard runs on
the *request* budget and never sees a post-rerank truncation. Both NEW steps run only when
`Enabled`; when disabled the stream is `Diversify`'s output unchanged (§4).

MMR runs **before** the cross-encoder. This satisfies P4's actual concern — do not let MMR reorder a
reranker's output — while preserving a role P4's own measurements never tested. P4 measured λ=0.70
vs λ=1.00 when MMR ordered the *returned results* and found it never helps. In this design MMR
instead decides the **document spread of the pool the max-passage step selects its 50 parents from**,
and because a chunk
inherits its *parent object's* centroid (`ObjectSearchGrpcService.cs:275-277`), the centroid term is
constant across all chunks of one document. Removing MMR therefore lets one strong document crowd
the pool, and the reranker can only reorder what it is given. P4's evidence does not transfer to this
role, so λ is an open sweep parameter (§7.3), not a settled λ=1.0.

### 3.2 The centroid's role

Unchanged mechanically, and *purer* in effect. Measured, the centroid buys nothing on ordering
(nDCG@10 +0.0021, t=+0.36; AP +0.0052, t=+0.76 — both noise) and everything on recall (R@50 −0.0167,
t=−2.25 when removed, with 5 queries losing relevant documents and **zero** gaining any). With a
reranker downstream, ordering is no longer stage 1's job, so the centroid does exactly the one thing
it was measured to do: decide which 50 chunks reach the cross-encoder. It is **not** an input to the
cross-encoder, which sees `(query, chunk_text)` only.

Consequence: the final order is the cross-encoder's alone (β = 1.0, §7.2), so the fused score —
and therefore the centroid — has **no influence on final ordering**. It becomes a pool-selection
device. `WCentroid` should then be tuned against R@K rather
than nDCG@10 (§7.4).

### 3.3 Phase 1 — harness-side spike

**Requires no server change.** `benchmark-query` already requests
`DocumentBudget × ChunkBudgetMultiplier` = 250 chunks, which the server returns fused and
diversified, and the harness then max-passage aggregates to 50 documents. The spike inserts one step
*after* that aggregation:

```
SearchChunks (server unchanged) -> 250 fused+MMR'd chunks
  -> MaxPassageAggregator -> 50 documents, each with its winning chunk
  -> TEI /rerank  (query, winning chunk_text) x 50
  -> RESCORE: each document's Score := cross-encoder score
  -> re-sort by the new Score (DocumentRanking.CollapseByDocId on the rescored tuples)
  -> TREC run file
```

**The step is a rescore, not a reorder — and it is followed by a re-sort.**
`TrecRunWriter.WriteAsync` (`TrecRunWriter.cs:23-31`) iterates the list it is handed positionally
and writes `rank = i + 1`; it sorts nothing. The only score-sort in the harness,
`DocumentRanking.CollapseByDocId` (`:27-31`), runs inside `MaxPassageAggregator.Aggregate`,
*upstream* of the rescore. So after `Score := cross-encoder score` the document list must be
re-sorted by the new score before it is written — reusing `DocumentRanking.CollapseByDocId` on the
rescored tuples, which also keeps the `Take(50)` semantics identical between arms. Without that
step a correct rescore writes a run file whose `rank` column contradicts its `score` column. (A
related erasure already affects MMR: `ResultDiversifier.cs:83` emits the *fused* score, so only
MMR's selection, never its ordering, reaches any run file.)

**Differs-from-control assertion.** Defined over the **per-query ranked doc-id sequence**: the
reranked arm's sequence must differ from the control's for at least **25 % of queries** before the
arm is scored — far below what any reranker that ran will produce (under MMR alone only 3 of 300
SciFact queries kept an identical full ordering) and well above the one-query noise floor. It is
deliberately not a run-file comparison — `TrecRunWriter.cs:31` writes the run tag into
every row and `BenchmarkQueryScenario.cs:217-221` uses `ConfigLabel` as both tag and filename, so
any two arms' files differ on every row by construction and a file comparison can never fail.

`MaxPassageAggregator` today emits only `(DocId, Score)`, and the harness discards `chunk_text` at
`BenchmarkQueryScenario.cs:309`; `parent_key`, `chunk_text` and `score` arrive together on each
`ChunkSearchResponse`, so Phase 1 retains the text and surfaces each document's winning chunk.

This is the same shape Phase 2 will have server-side (the reranked arms request
`top_k = CandidateCount` and receive one rescored winner per document, §3.5), so the spike
measures the real design rather than an approximation, while risking nothing in the server.

**Baseline:** the **chunked-512** SciFact collection, restored from
`~/repositories/iverson-benchmark-corpora/scifact-512-qdrant-snapshots/` per its `RESTORE.md`. This
is the 512-char chunking configuration, **not** main's 2048-char default; every §1 number was
computed on comparable runs, and restoring avoids a ~2 hour re-ingest.

**Phase 1 deliverables:** the rescore step (including retaining `chunk_text`, surfacing the
winning chunk per document, and the re-sort by the new score), the differs-from-control assertion,
a TEI compose service, and a reusable stats module (§7.5).

### 3.4 Gate

Phase 2 proceeds only if Phase 1 shows reranking beating the unreranked control on SciFact `chunks`
nDCG@10 by an effect clearing §7's bar: paired *t* **and** sign-flip permutation, Holm-corrected
across the declared arm family. P7 puts MDE at ≈0.019 and predicts 0.06–0.10 — a 3–5× MDE effect. If
the result lands inside noise, **Phase 2 does not happen.**

### 3.5 Phase 2 — server-side stage

New `ICrossEncoderScorer` / `CrossEncoderScorer` in `Iverson.Vector`, alongside `IResultReranker` and
`IResultDiversifier` and following their shape (interface, options class bound in
`ServiceCollectionExtensions`). Unlike those two it performs I/O, so it is the first non-pure member
of the trio; the interface exists partly so the disabled path is a no-op implementation rather than a
null check at the call site.

**Truncation policy.** Under `main`'s default chunking the winning chunk can exceed the
cross-encoder's 512-token window (§2). Phase 2's stated policy is TEI's `--auto-truncate` —
head-truncation at `max_input_length` — chosen explicitly rather than applied by default, and made
visible by the startup guard (§4), which logs `max_input_length` and `auto_truncate`. Whether
head-truncation costs quality on long chunks is unmeasured; it is the same question deferred for
`SearchSimilar`, and is answered by the same future truncation-policy measurement.

**Naming:** the existing `ResultReranker` performs *fusion*, not reranking in the IR sense. The new
type is named `CrossEncoderScorer` to avoid two "rerankers"; `ResultReranker` is left alone, since
renaming it would touch the whole search path for no functional gain.

**Server-side max-passage selection.** Phase 2 adds, inside the enabled branch only, the step the
harness performs in Phase 1 — over a pool sized for it, not for the caller's `top_k`.
`ResultDiversifier.Diversify` returns `Math.Min(topK, ranked.Count)` chunks
(`ResultDiversifier.cs:19`), so grouping *that* by parent at a caller's `top_k` of 50 yields ~13
parents at 3.85 chunks per document — the collapse §3.1 exists to avoid, re-entering through a
different operand. The enabled path therefore calls
`Diversify(diversityCandidates, Math.Max(topK, CandidateCount × 5))` — 5 being
`ChunkBudgetMultiplier`, the factor that makes 250 chunks reach ~65 parents today — and raises
`fetchLimit` (`ObjectSearchGrpcService.cs:408`, `topK × OverFetchFactor` with `OverFetchFactor = 4`
at `:747`) so that many chunks are actually fetched. It then groups that pool by `parent_id`, keeps
each parent's highest-fused-score chunk, takes the top `CandidateCount` parents, and hands their
winning chunks to the scorer. `chunk_text` and `parent_id` are on the result payload (`:492-493`),
and `IntelligenceStoreConsumer.KeyToUlong` (`:701-712`, already used at `:469`) maps `parent_id` to
the `ulong` the scorer contract wants.

**What a caller receives when reranking is enabled:** `SearchChunks` streams **only the
`CandidateCount` rescored winners** — one row per parent, in cross-encoder order, with `Score` =
cross-encoder score. On this path the response is a *document* ranking expressed as each
document's winning chunk, and `top_k` no longer counts chunks: a caller receives
`Math.Min(top_k, CandidateCount)` rows. This is a documented contract change, and the `top_k` /
`score` comments in `object_search.proto:111-118` are updated to say so. There is never a second
score domain in the stream. When reranking is disabled nothing above runs and the stream is exactly
today's — `Math.Min(top_k, pool)` chunks in `Diversify`'s order — which is what the bit-exact no-op
in §4 requires. `CandidateCount` is only ever read on the enabled path, consistent with §5's
enabled-only validation.

**What the arms request.** In Phase 2 the control arm **A0** keeps the harness's existing request,
`top_k = DocumentBudget × ChunkBudgetMultiplier` = 250 chunks, aggregated to 50 documents as today.
The reranked arms request `top_k = CandidateCount` = 50 and receive the 50 rescored winners, so
their aggregation is the identity and both arms' run files hold the same 50 documents per query —
the §7.1.2 invariance control holds by construction. This is the Phase 1 shape.

**Call-site change:** `ObjectSearchGrpcService.cs:488` (on `main`) currently calls
`diversifier.Diversify(...)` inline in a `foreach` header, with streaming beginning in the loop body.
The plan must hoist that into a local before the loop so the selection and the awaited scoring call
can be inserted between them.

## 4. Component contract

```csharp
public interface ICrossEncoderScorer
{
    Task<IReadOnlyList<ScoredCandidate>> ScoreAsync(
        string query,
        IReadOnlyList<(ulong Id, string Text)> candidates,
        CancellationToken ct);
}
```

- **Candidates are documents.** `Id` is the parent document's point id and `Text` is that
  document's winning (max-passage) chunk, so one call scores exactly `CandidateCount` pairs —
  which requires the pool the selection groups to hold at least that many distinct parents (§3.5).
- **Batching** at `BatchSize` (default 8). Not a guess: TEI reports `max_batch_requests: 8`,
  `max_client_batch_size: 32`, `max_batch_tokens: 16384`, and 8 is the size the measurements in §9
  used.
- **Disabled is a bit-exact no-op.** When `Enabled` is false the stage must not perturb ordering at
  all, matching the codebase's existing degradation guarantees (`WCentroid = 0.00` makes
  `fused == base` exactly; `Lambda = 1.00` makes MMR bit-for-bit `Take(topK)`).
- **Startup guard (P6).** When enabled, call TEI `/info`, assert the returned `model_id` matches
  configuration, and log model + `max_input_length` + `auto_truncate`. Mismatch fails startup. P6
  states input format costs more than every other parameter combined; this is the cheap analogue of
  the dimension guard that caught run C.

## 5. Configuration

```
Reranker:
  Enabled:        false
  BaseUrl:        http://reranker:80
  Model:          cross-encoder/ms-marco-MiniLM-L-6-v2
  CandidateCount: 50     # K
  BatchSize:      8
  TimeoutSeconds: 30
```

Bound as `RerankerOptions` and validated at registration, mirroring `AddVectorRanking`
(`Iverson.Vector/ServiceCollectionExtensions.cs:55-78`) with two deliberate differences:

- **Validation runs only when `Enabled` is true.** `BaseUrl` and `Model` have no sensible defaults,
  so validating them unconditionally would fail startup on every existing deployment the moment this
  ships — with reranking off.
- **Validation is per-type.** `CandidateCount`, `BatchSize` and `TimeoutSeconds` are integers
  requiring `> 0`; `BaseUrl` and `Model` require non-empty, with `BaseUrl` a valid absolute URI. No
  `double` remains in the block (β is fixed at 1.0 and not configurable, §7.2), so the
  finiteness-first rule `AddVectorRanking` applies has no member to apply to here.

**Deployment:** a `reranker` service in `docker-compose.yml` behind a Compose profile (v2.40.3
supports `profiles:`), so it does not run unless enabled. `Reranker__BaseUrl` mirrors the existing
`Embeddings__BaseUrl` / `Enrichment__BaseUrl` convention. For Helm, a `reranker` subchart following
the `charts/ollama` pattern with an `enabled:` gate.

## 6. Failure semantics — fail loud

When reranking is enabled and the scorer is unreachable, times out, or returns an error, **the search
request fails**. There is no degrade-to-fused-order path.

Rationale: reranking is off by default, so anyone who enabled it opted into the dependency; and a
silent fallback produces a run file indistinguishable from a reranked one, which scores as a null
result and reads as "reranking doesn't help" rather than "reranking didn't run". This project has
paid for that failure mode already — 14 empty run files from a silent failure, and a threshold
comparison that failed *open* on `+Infinity`.

## 7. Evaluation protocol

### 7.1 Quality

1. **Primary endpoint declared before any arm runs:** nDCG@10 on `SearchChunks`, SciFact.
2. **R@50 is a negative control, not an outcome.** Reranking rescores the 50 *documents* the
   max-passage aggregation already selected (§3.1); it cannot change which 50 documents are in the
   pool, so R@50 must be invariant and the run file must hold the same 50 rows per query as the
   control. If either moves, the pool changed and the arm is invalid.
3. **The oracle ceiling is a hard upper bound.** SciFact oracle@50 = 0.911. Any result above it means
   qrels leakage or a scoring bug, not a good reranker.
4. **Statistics:** paired *t* **and** sign-flip permutation p, both always — the t-test alone is what
   produced the SciFact centroid claim that had to be retracted. Plus 95% CI, Cohen's *d_z*, and Holm
   across the declared family.
5. **Never bpref on BEIR** — it reduces algebraically to recall there, since BEIR qrels carry no
   `rel=0` rows. It is meaningful on FreshStack's 35,876 judged negatives.
6. **Two corpora, chosen for opposite properties.** SciFact primary (ordering is the whole remaining
   gap). NFCorpus secondary as the recall-bound counter-case (R@50 = 0.249), where P8 predicts
   reranking underperforms — reporting it guards against generalising from one corpus, which is how
   the fusion peak was over-generalised before.

### 7.2 Arm family (declared in advance, Holm-corrected)

| arm | model | λ | β |
|---|---|---|---|
| **A0** control — no reranking | — | 0.70 | — |
| **A1** primary | ms-marco-MiniLM-L-6-v2 | 0.70 | 1.0 |
| A2 | bge-reranker-base | 0.70 | 1.0 |
| A3 | ms-marco | **1.00** | 1.0 |

Primary comparison: **A1 vs A0**. A3 tests whether P4's "MMR off" transfers to MMR's new
pool-composition role (§3.1). β is fixed at 1.0 — the final score is the cross-encoder's alone — and
is not a configuration value; whether the fused score still earns weight in the final order is
folded into the `w` re-sweep family (§7.4). K = 50 is `DocumentBudget`, and because the unit
reranked is the **document** (§3.1), that is exactly 50 pairs per query — P2's cost model as stated.

### 7.3 λ is open, not settled

See §3.1. P4's λ=1.0 recommendation was measured against a role MMR no longer has.

### 7.4 The `w` re-sweep is a separate family

Measured against R@K rather than nDCG@10, run only after a reranker is chosen. Mixing it into the
family above would inflate the correction and confound stage-1 tuning with stage-2 effect. This
family also owns the question a β < 1 arm would have asked — whether the fused score, and so the
centroid, still earns weight in the final order — since answering it requires a stated
normalisation between the cross-encoder's model-specific score range and the fused score's [0,1]
domain, which is itself a decision to be swept, not assumed. Evidence
that it is currently mis-set for the post-reranker job: shipped `w` = 0.500, while NFCorpus measured
ordering-optimal at 0.167 and **recall-optimal at 0.333**, and SciFact peaked at 0.333 on R@50.

### 7.5 Statistical tooling

`scratchpad/stats.py`, cited by P7, **no longer exists**. Phase 1 rebuilds it as a committed module
on `scipy 1.18.1` (`ttest_rel`, `permutation_test`) plus `ir_measures 0.4.3`, both verified importable
from `~/repositories/iverson-benchmark-corpora/python-libs`. Committing it prevents a third
disappearance.

### 7.6 Throughput measurement

Every latency figure produced under this spec must follow these rules, which exist because a cold-box
measurement during this design ran 4–5× optimistic **and inverted a ranking between two systems**:

1. One server active per measurement; everything else stopped or paused.
2. Record free RAM, tmpfs usage, and load average at start and end.
3. **Never mount a model cache under `/tmp`** — it is a 4.9 GB tmpfs (RAM) on this box.
4. Interleave arms; never measure A to completion then B.
5. Control block first and last; **>20% divergence voids the run**.
6. Report cold and sustained separately — they differ ~4× here and can invert rankings.
7. Every quoted latency states what else was running.

### 7.7 Run integrity (existing machinery, reused)

Build attribution via the `/build` composite (already refuses to start unattributed);
`ChunkBudgetGuard` (already refuses a budget that cannot reach `DocumentBudget`); and the structural
run-file checks — row count, distinct queries, non-zero scores, qrels coverage, duplicate doc ids.

## 8. Testing

`CrossEncoderScorer` is tested against a fake `HttpMessageHandler` — the pattern already used in
`Iverson.Embeddings.Tests/EmbeddingServiceTests.cs` and three other files — not a live container.
Testcontainers is deliberately avoided: an `IClassFixture` is constructed once per test *class*, and
container contention has already crashed a session on this box.

Coverage: batching at the configured size; **fail-loud on non-success and on timeout**; the startup
guard rejecting a model mismatch; and the disabled path as a **bit-exact** no-op.

Every test must be written to fail against a specific mutation, and the mutations must be *run* to
prove it — not asserted.

## 9. Measurements taken during design

On the 4-core / 9 GB / 15 W WSL2 box, against 50 real SciFact candidates (mean 1,581 chars).

**Reranking latency — COLD BOX, treat as optimistic:**

| server / model | 512-char chunks | full documents |
|---|---|---|
| TEI / ms-marco-MiniLM-L-6-v2 (22M) | 40 ms/pair — 10 min/arm | 200 ms/pair — 50 min/arm |
| TEI / bge-reranker-base (278M) | 311 ms/pair — 78 min/arm | 1,113 ms/pair — 4.6 h/arm |
| llama.cpp / bge-reranker-v2-m3 Q4 (568M) | 1,940 ms/pair — 8.1 h/arm | HTTP 500 (context overflow) |

These were taken before the throughput protocol in §7.6 existed and are **cold-box figures, likely
4–5× optimistic**. They are internally comparable — the TEI-over-llama.cpp conclusion stands — but
**absolute arm durations must be re-measured under §7.6 before being planned against.** The
llama.cpp comparison is additionally confounded: no small MiniLM GGUF was available, so it ran a
568M model against TEI's 278M.

**Why TEI over llama.cpp:** llama.cpp's `/reranking` endpoint works and returned correct scores, but
TEI serves HF cross-encoders by name with no GGUF conversion, ships `--auto-truncate` and a batch
API, and was 5–6× faster per pair even allowing for the model-size confound.

## 10. Verified assumptions

Checked against the codebase on 2026-09-03. Evidence is path:line or command output.

| # | Assumption | Result |
|---|---|---|
| A1 | `ChunkSearchResponse` carries chunk text | **Confirmed** — `chunk_text` is field 2, `Common/Proto/object_search.proto:128` |
| A2 | Harness requests `DocumentBudget × ChunkBudgetMultiplier` chunks | **Confirmed** — `BenchmarkQueryScenario.cs:300`, `.TopK((uint)(DocumentBudget * ChunkBudgetMultiplier))` on `main` |
| A3 | `MaxPassageAggregator.Aggregate` takes chunks + budget | **Confirmed** — `MaxPassageAggregator.cs:32` |
| A4 | `Iverson.Vector` has no outbound HTTP dependency | **Confirmed** — `Iverson.Vector.csproj` lists no HTTP package; this is a new dependency |
| A5 | Chunk path is `async` and streaming has not begun | **Confirmed with caveat** — `ObjectSearchGrpcService.cs:330` is `async`, but `Diversify` is called inline in the `foreach` header and must be hoisted |
| A6 | `Diversify` returns an orderable, truncatable list | **Confirmed** — `IResultDiversifier.cs:13` returns `IReadOnlyList<RerankedResult>` |
| A7 | `ServiceCollectionExtensions` binds options from `IConfiguration` | **Confirmed** — `AddVectorRanking(this IServiceCollection, IConfiguration)` at `:55` |
| A8 | Chunk `"text"` reachable before the streaming loop | **Confirmed** — available via `byId`, built before the loop |
| A9 | Compose supports `profiles:` | **Confirmed** — Docker Compose 2.40.3 |
| A10 | Helm has an optional-component pattern | **Confirmed** — `charts/ollama` subchart plus `enabled:` gates in `values.yaml` |
| A11 | `EmbeddingService` uses named `HttpClient` registration | **Confirmed** — `Iverson.Embeddings/ServiceCollectionExtensions.cs:12,30` |
| A12 | TEI `/rerank` returns index + score | **Confirmed empirically** — exercised during design |
| A13 | TEI `/info` exposes `model_id` | **Confirmed empirically** — also reports `max_input_length`, `auto_truncate` |
| A14 | A fake `HttpMessageHandler` pattern exists | **Confirmed** — `EmbeddingServiceTests.cs` and 3 other files |
| A15 | Nothing depends on `Diversify` being the last ranking step | **Confirmed** — no `WithStrictOrdering` / `ContainInOrder` assertions on chunk results; the bit-exact disabled path (§4) protects the rest, and the enabled path streams only the `CandidateCount` rescored winners, one per parent (§3.5) — a documented contract change, not a silent one |
| A16 | Options validation generalises across all members | **REFINED** — validation must run only when `Enabled`, and is per-type; with `Blend` removed (§7.2) no `double` remains, so no finiteness check applies |
| A17 | SciFact artifacts present and reusable | **Confirmed** — `corpus.jsonl`, `queries.jsonl`, `qrels.trec` (339 rows, matching the published split) |
| A18 | `scratchpad/stats.py` exists | **FAILED** — does not exist; rebuilt in Phase 1 on scipy 1.18.1 + ir_measures 0.4.3 (§7.5) |
| A19 | A collection can be restored without re-ingest | **Confirmed with caveat** — `scifact-512-qdrant-snapshots/` exists with `RESTORE.md`, but it is the **chunked-512** config, not main's 2048 default |
| A20 | Chunks per document on the Phase 1 baseline | **Confirmed (CDR round 1)** — 19,967 chunks / 5,183 documents = 3.85, from `scifact-run-2026-08-26/keymap.json.stats.json`; this is why the unit reranked must be the document (§3.1) |
| A21 | `MaxPassageAggregator`'s aggregation ranks by score, not input order; the run-file writer does NOT sort | **Corrected (CDR round 2)** — `DocumentRanking.cs:27-31` `OrderByDescending(kv => kv.Value).Take(limit)` is reached only inside `MaxPassageAggregator.Aggregate` (`:48`), upstream of the rescore; `TrecRunWriter.cs:23-31` iterates positionally with `rank = i + 1` and sorts nothing — which is why §3.3 re-sorts after the rescore |
| A22 | Chunk token size under `main`'s default chunking | **Confirmed (CDR round 1)** — `IversonChunkAttribute.cs:10` defaults `maxTokens = 512`; `IntelligenceStoreConsumer.cs:664-670` makes that 2,048 characters; 16.8 % of SciFact chunks then exceed 512 tokens with the query prepended (§2) |
| A23 | The harness can recover each document's winning chunk text | **Confirmed (CDR round 1)** — `parent_key`, `chunk_text` and `score` arrive on the same `ChunkSearchResponse`; the harness currently discards `chunk_text` at `BenchmarkQueryScenario.cs:309` and Phase 1 retains it (§3.3) |
| A24 | How many chunks `Diversify` emits | **Confirmed (CDR round 2)** — `ResultDiversifier.cs:19` `take = Math.Min(topK, ranked.Count)`, loop bounded at `:37`; this is why §3.5 sizes the reranker's pool independently of the caller's `top_k` |
| A25 | The run-file writer does not sort | **Confirmed (CDR round 2)** — `TrecRunWriter.cs:23-31` writes rows positionally with `rank = i + 1`; this is why §3.3 re-sorts after the rescore |
| A26 | The winning chunk does not survive max-passage aggregation | **Confirmed (CDR round 2)** — `ChunkAggregation.Ranked` is `(string DocId, double Score)` (`MaxPassageAggregator.cs:12-14`) and `DocumentRanking.cs:19-25` reduces through a `Dictionary<string,double>`; Phase 1 extends it (§3.3). The max-tracking lives in `CollapseByDocId`, which `RunSimilarAsync` (`BenchmarkQueryScenario.cs:292`) also calls |
| A27 | A `parent_id` string can be mapped to the `ulong` the scorer contract wants | **Confirmed (CDR round 2)** — `IntelligenceStoreConsumer.KeyToUlong` (`:701-712`) is `internal`, in the same assembly as the call site, and already used for this at `ObjectSearchGrpcService.cs:469` |
| A28 | How many chunks the server fetches before ranking | **Confirmed (CDR round 2)** — `fetchLimit = topK * OverFetchFactor` at `ObjectSearchGrpcService.cs:408`, `OverFetchFactor = 4` at `:747`; at `top_k = 50` that is 200 chunks ≈ 52 parents, which is why §3.5 raises it |

## 11. Known issues, accepted as out of scope

- **The reranking latencies in §9 are cold-box** and must be re-measured under §7.6 before arm
  durations are planned against them.
- **`gte-base-en-v1.5` cannot be served by TEI** at 1.8.3 or 1.9.3. Irrelevant to this spec (it is an
  embedding model) but it constrains the deferred migration, since it is the strongest model in the
  published quality table.
- **The `chunked-512` baseline is from an unmerged experiment branch**
  (`chunk-size-512-experiment`). Phase 1 measures against that configuration; results do not
  automatically transfer to main's 2048-char default.
- **`SearchSimilar` reranking is unaddressed**, pending a truncation policy (§2).
