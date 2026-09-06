# Tier 1 retrieval defaults — design (λ per endpoint, the chunk-window default, the object-vector representation)

**Status:** verified design, not yet planned.
**Input:** items 1–3 of `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` (Ben, 2026-09-06:
"1, 2 and 3", one spec).
**Depends on:** the embedding migration Phases 1–2 (merged; the box runs bge-base via TEI as a default
service), the benchmark harness (`Iverson.LoadTest`, `ingest.py`, `report.py`), and the FreshStack 6k
slice at `~/repositories/iverson-benchmark-corpora/freshstack-chunk512-2026-08-30/beir/` with the
five topics' nugget qrels under `freshstack-5topic/<topic>/freshstack/qrels.tsv`.

## 1. Why

Three production defaults sit on evidence that does not cover them:

- **The chunk window.** `IversonChunkAttribute` defaults to 512 tokens / 64 overlap, which the consumer
  turns into 2048/1792 characters. Every gate verdict to date (bge-base's pass, the reranker's fail, the
  multivector's fail) was measured at 512/448 characters, the only window with a baseline. The default
  every fresh deployment gets is the one window with no measurement under the shipped model.
- **MMR λ.** One value (0.70) serves both RPCs. Diversification off costs `SearchSimilar` 9 % (NFCorpus)
  and 12.8 % (FreshStack) of R@50, replicated; on `SearchChunks` the benchmark sees nothing — but only
  because the harness collapses chunks to documents before scoring. Production `SearchChunks` returns
  the chunk list itself, so the "neutral" finding is an artifact of the collapse, not evidence about what
  callers see. MMR's benefit has never been measured on either endpoint: BEIR-style qrels award nothing
  for diversity.
- **The object vector.** Under nomic on Ollama the object vector saw 2,048 tokens; under bge-base on
  TEI (`max_input_length` 512, `--auto-truncate`) it sees the document's head only. On SciFact's short
  documents `.similar` still improved (+0.0794 nDCG@10); on long documents the cost is unknown. The
  Phase 1 gate flagged it for re-measurement.

## 2. Scope

**In:** (A) `VectorRankingOptions.Lambda` split into `LambdaSimilar` and `LambdaChunks`, both 0.70 —
no behaviour change; (B) three bge-base ingests (SciFact 2048/1792; FreshStack 6k at 2048/1792 and
512/448), their API runs, a query-only λ sweep on FreshStack with α-nDCG@10 and a caller-visible
chunk-diversity measure, and a query-only head-vs-centroid `.similar` comparison; harness additions that
make those runs possible (`--chunk-budget-multiplier`, a chunk-diversity sidecar, `report.py
--nugget-qrels`, `similar_arms.py`, the pyndeval provider); (C) three written rules and their
consequences: the λ defaults, the gated client chunk-window default change, and a recorded verdict on the
object-vector representation.

**Out:** any change to what `SearchSimilar` searches (a centroid win produces a follow-up spec, not a
server change here); NFCorpus and ArguAna; the `SearchChunks.top_k` semantics (item 11 of the ranked
document); Helm entries for `VectorRanking` (none exist today; an absent section binds to defaults);
re-measuring the fusion triple.

### 2.1 Decisions taken during brainstorming (Ben, 2026-09-06)

| Decision | Choice | Why |
|---|---|---|
| Items | 1, 2 and 3 in one spec, three phases | they share every ingest; three specs would repeat ~48 h of embedding or consume each other's artifacts |
| Long-document corpus | FreshStack 6k slice, full (672 queries) | the only corpus with subtopic judgments; long enough for truncation to matter; a smaller subset weakens both items |
| FreshStack windows | **both** 2048/1792 and 512/448 | item 1 gets a full long-document comparison; items 2–3 run on both arms |
| λ change | split per endpoint, **both default 0.70** | no caller-visible change on unmeasured evidence; the sweep sets each default by rule |
| Window FAIL consequence | the client default change is **in this spec, gated** | one fewer cycle; the change set is enumerated below |
| Object-vector arms | raw `body_vector` (head-512) vs raw `body_centroid`, query-only | both vectors already exist on every object point; isolates the representation under one model |
| α-nDCG provider | **install gcc, use pyndeval** (Ben's action) | the canonical implementation; `pip --target` fails today only for want of a compiler (§9 row 6) |

## 3. Design

### 3.1 Arms and runs

All bge-base through `tei-embed` (a default service already on bge-base; no model recreate anywhere).

| Arm | Corpus | Window | Ingest | Chunks | Multiplier | Runs |
|---|---|---|---|---|---|---|
| `bge-base` (exists) | SciFact | 512/448 | M1 (`scifact-bge-base-2026-09-04`) | 19,967 | 5 | API `.chunks`/`.similar` exist |
| `sci-2048` | SciFact | 2048/1792 | new, ≈ 3 h | 6,587 | 5 | API at λ 0.70/0.70; `similar_arms.py` |
| `fs-2048` | FreshStack 6k | 2048/1792 | new, ≈ 20 h | 18,626 (3.10/doc) | 11 | API at λ 0.70/0.70; λ sweep (4 runs); `similar_arms.py` |
| `fs-512` | FreshStack 6k | 512/448 | new, ≈ 25 h | 64,763 (10.79/doc) | 11 | same as `fs-2048` |

The chunk-budget multiplier is a per-corpus constant so that each comparison holds its budget fixed: 5 on
both SciFact arms (as every SciFact run to date), 11 on both FreshStack arms — the guard's minimum at
10.79 chunks/doc (§9 row 4), applied to the 2048 arm too so the two FreshStack `.chunks` runs differ only
in window. Ingest ETAs scale the migration's measured 720 ms per 512-char bge-base embed; the `fs-512`
figure is the sum of 64,763 chunk embeds and 6,000 whole-body embeds.

**λ sweep** (FreshStack arms only, query-only): λ ∈ {0.50, 0.70, 0.85, 1.00}, applied to both endpoints at
once through the two new env vars, one API recreate + `benchmark-query` per value (≈ 30 min each for
672 queries, §9 row 5). The 0.70 run is the arm's base run.

**Head-vs-centroid** (all three new arms, query-only): `similar_arms.py` writes `head-raw.similar.trec`
and `centroid-raw.similar.trec` from raw named-vector searches on the object collection. Raw against
raw isolates the representation; the API's fused `.similar` run is reported beside them, never compared
to them.

### 3.2 Phase A — λ per endpoint

`VectorRankingOptions` (`Iverson.Vector/VectorRankingOptions.cs`) replaces `Lambda` with `LambdaSimilar`
and `LambdaChunks`, both `0.70`, each validated exactly as `Lambda` is today in
`ServiceCollectionExtensions.AddVectorRanking`: finiteness first (a non-finite value passes every
comparison), then `[0, 1]`. Because `AddVectorRanking` receives `IConfiguration`, it also reads
`VectorRanking:Lambda` and, if present, throws naming the two new keys — a deployment that set the old key
must not silently fall back to defaults.

`IResultDiversifier.Diversify(ranked, topK)` becomes `Diversify(ranked, topK, double lambda)`;
`ResultDiversifier` no longer reads λ from options (it keeps `IOptions<VectorRankingOptions>` out of its
constructor entirely — λ was its only use). `ObjectSearchGrpcService` takes
`IOptions<VectorRankingOptions>` beside its existing `IOptions<DecayOptions>` and passes
`LambdaSimilar` at the `SearchSimilar` call site and `LambdaChunks` at the `SearchChunks` one. The
compose `iverson-api` line `VectorRanking__Lambda=${VECTOR_RANKING_LAMBDA:-0.70}` becomes two lines,
`VectorRanking__LambdaSimilar=${VECTOR_RANKING_LAMBDA_SIMILAR:-0.70}` and
`VectorRanking__LambdaChunks=${VECTOR_RANKING_LAMBDA_CHUNKS:-0.70}`; the worker has no λ line today and
gains none. At 1.00 the diversifier's reduction to `Take(topK)` is bit-exact on both branches of the MMR
expression, as the reranker gate relied on.

### 3.3 Harness changes (`Iverson.LoadTest`)

- **`--chunk-budget-multiplier <int>`** (default 5) replaces `BenchmarkQueryScenario.ChunkBudgetMultiplier`.
  `ChunkBudgetGuard` is unchanged and evaluates the flag's value; the value is written into the
  `<label>.meta.json` sidecar beside the composite so a run records the budget it used.
- **`<label>.chunks.diversity.json`**, written by `benchmark-query` beside the run files: per query, the
  number of distinct `parent_key`s among the first 10 and the first 50 raw `SearchChunks` hits, taken from
  the hit list before `MaxPassageAggregator` collapses it — the only place the number exists. Plus the two
  means. This is the caller-visible diversity a `SearchChunks` consumer experiences.
- **`report.py --nugget-qrels <path>`**: adds `alpha_nDCG@10` (pyndeval provider, α = 0.5) to the measure
  list for every run scored in that invocation; the structural checks, per-query values and paired
  statistics are measure-generic and need no change. When a `<label>.chunks.diversity.json` sits beside a
  scored `.chunks.trec`, its two means are printed under the run's scores. `--nugget-qrels` and `--qrels`
  are both required for α-nDCG runs: the query-level file scores nDCG/R/AP, the nugget file scores α-nDCG.
- **`similar_arms.py`** beside `multivector.py`, stdlib-only, importing `ingest.py`'s `embed` and
  `qdrant_request`. Embeds each query once as `query_prefix + text` — `--query-prefix` is **required**
  (the ingest contract carries document prefixes only; bge-base's query instruction is
  `"Represent this sentence for searching relevant passages: "`, `EmbeddingPrefixes.cs:35`) — then per
  query runs two raw searches on the object collection, `body_vector` and `body_centroid`, limit 50,
  `with_payload: ["docId"]`, and writes the two `.similar.trec` files in `TrecRunWriter`'s format. Fails
  loud on any non-200, a dimension error, fewer than 50 hits, or a missing `docId`.
- **Nugget qrels for the slice**: a one-off filter of the five topics' `qrels.tsv` (query \t nugget \t
  corpus_id \t rel) to the slice's 672 query ids and 6,000 corpus ids, written once as
  `<run>/qrels.nugget.trec` (TREC 4-column, nugget in the iteration column) and reused by all FreshStack
  runs. `sample_corpus.py` does not do this today; the filter is a few lines added to it or a sibling
  script — the plan decides; the output file is the contract.
- **pyndeval** installed into `~/repositories/iverson-benchmark-corpora/python-libs` with the same
  `pip install --target` route the other libraries used, after gcc is present (Ben). `report.py`'s
  docstring already documents the `--target` mechanism.

### 3.4 Gated consequence — the client chunk-window default

Executed only when the window rule (§7.1) says FAIL. The change set, verified as the complete set of
places the default lives (§10 A17, A22):

| File | Change |
|---|---|
| `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonChunkAttribute.cs:10` | `maxTokens = 128, overlap = 16` |
| `Iverson.Clients/TypeScript/src/annotations.ts:166` | `maxTokens = 128, overlap = 16` |
| `Iverson.Clients/Go/iverson/tags.go:293-294` and the doc comments at `:168-171` | `128` / `16` |
| `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonChunk.java:18,21` | `default 128` / `default 16` |
| `Iverson.Clients/Python/iverson_client/annotations.py:42-43,64` | `128` / `16` |
| `Iverson.Server/Iverson.Api.Tests/Schema/IngestContractTests.cs:75` (+ its overlap constant) | `128` / `16`, per the file's own comment that these copy the attribute defaults |
| `Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json` | regenerated by that test: `chunkWindow` becomes 512/448, which is then `ingest.py`'s default window |

128 tokens × 4 = 512 characters and 16 × 4 = 64, i.e. the 512/448 window every gate measured. The sample
`Article.cs:14` pins 512/64 explicitly and is left alone. No server change: the consumer reads the window
from the registered schema, so already-registered types keep their window until re-registered, and the
spec's gate document says so. The client standard does not state the default and needs no edit.

## 4. Failure semantics — fail loud

Every count is checked: each ingest's sidecar must equal the chunker's prediction for its window (6,587 /
18,626 / 64,763 chunks; 5,183 / 6,000 documents); the guard must pass at the arm's multiplier; every run
file 50 × queries rows with full qrels coverage; `similar_arms.py` exits non-zero after writing what it
has on any failed query. The obsolete `VectorRanking:Lambda` key throws at startup. Box discipline is the
migration's: one arm at a time, nothing else running, every compose action single-service `--no-deps`,
never `stack.py` (it would stop `tei-embed`).

## 5. Component contract

| Component | Change |
|---|---|
| `Iverson.Vector/VectorRankingOptions.cs`, `ServiceCollectionExtensions.cs` | `LambdaSimilar`/`LambdaChunks`; validation; obsolete-key rejection |
| `Iverson.Vector/IResultDiversifier.cs`, `ResultDiversifier.cs` | λ parameter; options dependency removed |
| `Iverson.Api/Grpc/ObjectSearchGrpcService.cs:40-42, 302, 488` | `IOptions<VectorRankingOptions>`; per-endpoint λ at the two call sites |
| `Iverson.Server/docker-compose.yml:442-443` | two env lines replace one |
| `Iverson.Vector.Tests`, `Iverson.Api.Tests` | §8 |
| `Iverson.LoadTest/Program.cs`, `Scenarios/BenchmarkQueryScenario.cs` | `--chunk-budget-multiplier`; diversity sidecar |
| `Iverson.LoadTest/scripts/report.py`, `test_report.py` | `--nugget-qrels`, α-nDCG, diversity means |
| `Iverson.LoadTest/scripts/similar_arms.py`, `test_similar_arms.py` | new |
| `Iverson.LoadTest/scripts/sample_corpus.py` (or a sibling) | nugget-qrels filter for the slice |
| `docs/plans/2026-09-GATE-tier1-defaults.md` | three verdicts |
| gated (§3.4) | five client defaults, `IngestContractTests`, regenerated contract |
| outside the repo | `scifact-2048-<date>/`, `freshstack-2048-<date>/`, `freshstack-512-<date>/` run dirs and their Qdrant snapshot dirs; `python-libs/pyndeval` |

## 6. Evaluation protocol (Phase B)

Preconditions: Phase A merged; gcc installed and pyndeval importable from `python-libs`; the box on the
SciFact bge-base baseline (5,183 / 19,967, 768 dims, API at defaults, §9 row 7).

1. **`sci-2048`.** Copy `beir/` and `qrels.trec` from `scifact-run-2026-08-26` into the new run dir;
   `ingest.py --drop … --chunk-max-chars 2048 --chunk-step 1792`; expect 5,183 / 6,587; snapshot;
   clear the `BenchmarkDocument` schema row; `benchmark-query --config-label sci-2048` (multiplier 5);
   `similar_arms.py --query-prefix "Represent this sentence for searching relevant passages: "`;
   `report.py --run <dir>/runs --baseline …/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec`.
2. **`fs-2048`.** Copy the slice's `beir/`; write `qrels.trec` (query-level, as the earlier FreshStack
   runs did) and `qrels.nugget.trec`; ingest at 2048/1792; expect 6,000 / 18,626; snapshot; schema row;
   `benchmark-query --config-label fs-2048-l070 --chunk-budget-multiplier 11`; then for λ in
   {0.50, 0.85, 1.00}: recreate `iverson-api` with both env vars at λ (`--no-deps`), confirm via
   `docker inspect`, `benchmark-query --config-label fs-2048-l<λ>`; restore the API to defaults;
   `similar_arms.py`; `report.py --qrels --nugget-qrels --baseline fs-2048-l070.chunks.trec`.
3. **`fs-512`.** As step 2 at 512/448 (expect 64,763 chunks), labels `fs-512-l<λ>`, plus the window
   comparison `report.py --baseline fs-512-l070.chunks.trec --run fs-2048-l070.chunks.trec`.
4. **Restore** the SciFact bge-base baseline and the API at defaults (migration Task 7 step 5).
5. **Gate document** `docs/plans/2026-09-GATE-tier1-defaults.md`: arms table, every compare block
   verbatim, the diversity means per λ, the three verdicts (§7), the box state at close.
6. **Consequences**: set the two λ defaults per §7.2 (one commit, gate cited beside the values); execute
   §3.4 if §7.1 says FAIL.

## 7. Rules

### 7.1 Window rule
Two `.chunks` comparisons, each with the migration's rule: the 95 % CI lower bound of the nDCG@10 delta
and of the R@50 delta must both exceed −0.02. (a) `sci-2048` vs `bge-base` (M1); (b) `fs-2048-l070` vs
`fs-512-l070`. **The default stands only if both pass.** Either FAIL executes §3.4. `.similar` runs are
reported, not gated.

### 7.2 λ rule (from the FreshStack sweeps, both arms)
- **`LambdaSimilar`**: the λ with the highest α-nDCG@10 on `.similar`, if that λ beats 0.70 on α-nDCG@10
  with Holm p_adj < 0.05 on at least one arm and is not significantly worse on the other; otherwise, if no
  λ separates from 0.70 on α-nDCG@10 on either arm, **1.00** — the R@50 price is measured and the
  benefit is not. Ties between qualifying values go to the larger λ.
- **`LambdaChunks`**: **1.00** if λ 1.00 vs 0.70 changes the mean distinct parents in the top-10 chunk
  hits by less than 1.0 on both arms; otherwise 0.70 stays. α-nDCG on the collapsed `.chunks` ranking is
  reported but cannot see chunk-level MMR and does not decide.

### 7.3 Representation rule
Raw `centroid-raw.similar` vs raw `head-raw.similar` on `fs-2048`, `fs-512` and `sci-2048`. If the
centroid is better on both nDCG@10 and R@50 with Holm p_adj < 0.05 on both FreshStack arms and its CI
lower bounds exceed −0.02 on SciFact, the gate document recommends a follow-up spec that makes
`SearchSimilar` search `<property>_centroid` for chunked properties. Anything else is recorded and closed.

## 8. Testing

- `VectorRankingOptionsTests`: both new fields validated (non-finite, out of range), the obsolete
  `Lambda` key rejected with the message naming both new keys, defaults 0.70/0.70.
- `ResultDiversifierTests`: λ passed explicitly; the 1.00 reduction to `Take(topK)` on both branches.
- `Iverson.Api.Tests`: one wiring test per endpoint proving a non-default configured value reaches the
  diversifier at that call site and the other endpoint's value does not — a hard-coded 0.70 at either
  site must fail exactly one of them (the half-life wiring lesson, `project-vector-ranking-configuration`).
- `Iverson.LoadTest.Tests`: the diversity counts on a fixed hit list (distinct parents at 10 and 50,
  duplicates within the window, fewer than 50 hits); the multiplier flag reaching the guard and the
  sidecar.
- `test_report.py`: α-nDCG appears only with `--nugget-qrels`; the diversity means print from a sidecar;
  a nugget qrels file with iteration = subtopic scores α-nDCG@10 on a hand-computable case.
- `test_similar_arms.py`: TREC line format, the required prefix composition, fail-loud on an under-filled
  query. Request shapes are pinned by §9 rows 8–9.

## 9. Measurements taken during design (2026-09-06, live)

| # | What | Result |
|---|---|---|
| 1 | λ consumers | `ResultDiversifier.cs:77-78` only; compose `:442-443` (api only); two Vector test files; no Api mock of `Diversify` |
| 2 | FreshStack 6k slice composition | angular 1,228 / godot 848 / langchain 2,309 / laravel 1,224 / yolo 391 docs; 129 + 99 + 203 + 184 + 57 = 672 queries; `qrels.tsv` (nugget) present for all five topics |
| 3 | slice chunk counts | 2048/1792 → 18,626 (3.10/doc); 512/448 → 64,763 (10.79/doc); the `freshstack-chunk512-2026-08-30` run was the **2048-character** window (its 18,622 chunks) |
| 4 | `ChunkBudgetGuard` at 10.79/doc | multiplier 5 reaches 23 documents → REFUSED; minimum multiplier 11 |
| 5 | FreshStack query-run duration | ≈ 30–35 min per `benchmark-query` (672 queries; the 2026-08-30 sweep's file times) |
| 6 | α-nDCG provider | `ir_measures.alpha_nDCG@10` needs `pyndeval`; not in `python-libs`; `pip install --target` fails: `x86_64-linux-gnu-gcc` absent (Python 3.14.4, source-only wheel); `read_trec_qrels` keeps the iteration column |
| 7 | live box | 5,183 / 19,967 points, 768 dims; object points carry `body_vector` + `body_centroid` + `docId`; `tei-embed` on bge-base |
| 8 | ingest contract | keys `documentPrefixes`/`defaultDocumentPrefix` only (no query prefixes); `chunkWindow` 2048/1792 |
| 9 | named-vector search shape | `{"vector": {"name": …, "vector": …}}` verified live on `body_vector` (multivector spec §10); `body_centroid` is the same named-vector kind |
| 10 | SciFact run-dir corpus at 2048/1792 | 6,587 chunks (2026-09-04 probe) |

## 10. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | `Lambda` default 0.70; validation finiteness-first then [0,1]; `Options.Create` registration | `VectorRankingOptions.cs:14-17`; `ServiceCollectionExtensions.cs:55-80` |
| A2 | `ResultDiversifier` is λ's only consumer | grep; `ResultDiversifier.cs:77-78` |
| A3 | `ObjectSearchGrpcService` ctor takes `IOptions<DecayOptions>`; two `Diversify` call sites | `:40-42, :302, :488` |
| A4 | dependents of `Lambda` / `VectorRanking__Lambda` | compose `:442-443`; `VectorRankingOptionsTests.cs:57-65`; `ResultDiversifierTests.cs`; docs only otherwise; Helm none |
| A5 | `AddVectorRanking(services, IConfiguration)` can read the obsolete key | `:55-58` |
| A6/A23 | no Api-level λ wiring test exists; no Api test mocks `Diversify` | grep |
| A7 | multiplier const `:41` used `:113`/`:369`; `CommandFlags` `StrFlag` pattern `Program.cs:389-416`; sidecar `JsonObject` `:214-222` | read |
| A8/A24 | raw hits with `ParentKey` in scope before the collapse | `BenchmarkQueryScenario.cs:370-385` |
| A9/A21 | α-nDCG needs pyndeval; not buildable without gcc | §9 row 6 |
| A10 | `read_trec_qrels` keeps iteration; nDCG/R/AP ignore it | ran |
| A11 | slice composition and nugget qrels availability | §9 row 2 |
| A12 | chunk counts and the multiplier at each window | §9 rows 3–4 |
| A13 | SciFact 2048/1792 → 6,587 | §9 row 10 |
| A14/A15 | live baseline; object points carry both vectors + `docId` | §9 row 7 |
| A16 | contract has no query prefixes → `--query-prefix` required | §9 row 8; `EmbeddingPrefixes.cs:35` |
| A17/A22 | the default lives in five client files + `IngestContractTests.cs:75` (copied by design, comment `:40-43`) + the regenerated contract; `Article.cs:14` is an explicit pin | read |
| A18 | the client standard does not state the default; conformance does not pin it | grep |
| A19 | ≈ 30 min per FreshStack query run | §9 row 5 |
| A20 | λ env on api only; no Helm entries | compose; `deploy/helm` grep |
| A25 | raw `.similar` runs score as `build: unknown` | multivector spec A13 |

## 11. Known issues, accepted as out of scope

- **≈ 48 h of ingest** across the three arms, one at a time, plus ≈ 5 h of query runs. Nothing else on
  the box while an arm runs.
- **α-nDCG is a diversity measure over subtopics, not a relevance measure**; the λ rule deliberately
  lets it decide `LambdaSimilar` because relevance-only measures cannot see MMR's benefit. The
  `.chunks` α-nDCG is reported for completeness but cannot see chunk-level MMR.
- **The API's fused `.similar` run is not the representation comparison.** Fusion adds the centroid at
  0.45 to the head vector on that path; the raw head-vs-centroid arms are what §7.3 reads.
- **FreshStack's query-level `qrels.trec`** (as used by the earlier runs) halves R@50 relative to the
  nugget file's judged pairs; it is the same file the earlier FreshStack numbers used, so deltas are
  comparable, absolute recall is not.
- **The window rule's SciFact half compares against M1** (512/448) whose API run was made by build
  `7d3a15092f963723`; `report.py` will print `BUILD MISMATCH`, benign per the migration's same-build
  control.
- **Already-registered types keep their chunk window** after a default change; there is no migration and
  the registration guard blocks an in-place window change. The gate document says so.
