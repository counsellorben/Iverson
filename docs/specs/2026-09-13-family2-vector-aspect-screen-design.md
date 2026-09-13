# Screening family 2 — does chunk-vector spread proxy query-aspect coverage? — design

**Date:** 2026-09-13
**Parent:** `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` §15, and the 2026-09-13
amendment to `docs/plans/2026-09-GATE-aspect-coverage-oracle.md`, which closed that item on the
measurability ground while recording that family 2 had never been screened.
**Status:** design, approved after one revision.

---

## 1. The question

The aspect-coverage oracle returned GO: reordering the top 50 by true per-document aspect count is worth
**+0.0609** α-nDCG@10 (95 % CI [+0.0541, +0.0678]), and a plain per-document aspect count reaches 93.5 %
of that. Nothing computable reaches it. Six score-derived signals proxy the oracle's aspect count at
ρ ≤ 0.0790, and the reason is structural: **a scalar score per chunk cannot say which part of the query
a chunk answered**.

That argument points at the one candidate family this project has never screened. The coverage-term screen
deferred it explicitly (`2026-09-10-coverage-term-screen-design.md` §4): the chunk-hit dump "records a
parent key and a score per hit, with no chunk identity and no chunk vector, so aspect coverage cannot be
reconstructed from it." This spec screens it, using the vectors themselves.

> **Does the spread of a document's matched chunk vectors, relative to the query, proxy the number of
> distinct query aspects that document covers?**

This is a screen, not a term. It ends in a verdict document.

---

## 2. Design

### 2.1 Inputs

| | |
|---|---|
| Chunk vectors | `freshstack-2048-qdrant-snapshots/benchmark_documents_chunks_tenant_bypass-…snapshot` (restored) |
| Centroid vectors | `freshstack-2048-qdrant-snapshots/benchmark_documents_tenant_bypass-…snapshot` (restored) |
| Query text | `freshstack-2048-2026-09-07/beir/queries.jsonl` (672 queries) |
| Chunk-hit pool | `chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.hits.tsv` — which chunks are in each query's pool, and the score to reproduce |
| Aspect labels | `freshstack-2048-2026-09-07/qrels.nugget.trec` |
| Key map | `freshstack-2048-2026-09-07/keymap.json` (parentKey → docId) |

### 2.2 The three candidate signals

For a relevant (query, document) pair: let `q̂` be the unit query vector and `C = {c_1 … c_n}` the
document's chunk vectors present in that query's pool.

| Signal | Definition | n = 1 value |
|---|---|---|
| **`residual_spread`** | project `q̂` out of each `c_i`, normalise the residuals, take the mean pairwise cosine **distance** among them | 0 |
| **`greedy_cover`** | walk chunks in descending score; add one to the cover when its maximum cosine to any already-covered chunk is below **τ**; the signal is the cover's size | 1 |
| **`effective_rank`** | participation ratio of the Gram matrix of `C`: `(Σλ)² / Σλ²` | 1 |

`residual_spread` is the one that directly encodes the structural argument in §1 — chunks are retrieved
*because* they match the query, so what distinguishes them is the component orthogonal to it.
`greedy_cover` mirrors the oracle's own greedy construction but is count-like and therefore the most
exposed to §2.4's null. `effective_rank` is continuous but query-independent.

**τ is a free parameter and this project has been bitten by one before.** The coverage-term screen's
`n_within_tau` used τ = 0.95, and its design review forced the whole ρ(τ) curve into the record because
the value steered the outcome. Same rule here: **τ = 0.90 is pre-registered, and the verdict document
publishes ρ over τ ∈ {0.80, 0.85, 0.90, 0.95}** whatever the result.

### 2.3 Population

**Primary: all 2,733 relevant (query, document) pairs** that have at least one chunk in the pool — the
same population the oracle's ranking A reorders, and the same one the score-derived probe used.

**Sensitivity: the 1,075 pairs with ≥ 2 aspects and ≥ 2 chunks**, the sub-population where a spread
signal can discriminate at all. Reported alongside; **the primary decides**, per the convention the
aspect-coverage gate set.

16.5 % of the primary population has exactly one chunk and takes the constant n = 1 value on every
signal. That is stated rather than filtered: a term would face those documents too.

### 2.4 Pass rule, pre-registered

**`n_chunks` is the null.** It is the degenerate signal item 15 exists to exclude, and it already
correlates with aspect count at **ρ = +0.0714**.

> A candidate **passes** iff, on the query-clustered bootstrap over the primary population, its
> Spearman ρ against aspect count exceeds `n_chunks`' ρ with the 95 % CI on the **difference** excluding
> zero, and Holm-adjusted p < 0.05 across the three candidates.

Holm family size is 3 — the candidates, fixed before the run. The bootstrap resamples **queries**, not
pairs: pairs within a query share a query vector and are not independent. The primary population's 2,733
pairs come from **610 queries** — the 62 queries with no relevant document in their 50 contribute no pair
— so 610 is the resampling unit count, not 672.

A candidate that beats `n_chunks` is, by construction, not a chunk count in disguise. That is the
question §15 asks, and it is the question this rule answers — nothing more.

### 2.5 The contract extension

`ingest-contract.json` today carries `documentPrefixes` and `defaultDocumentPrefix` and **no query
prefixes**. bge-base's document prefix is empty but its query prefix is
`"Represent this sentence for searching relevant passages: "`, so a query embedded Python-side without it
is not the vector retrieval uses.

This spec adds `queryPrefixes` and `defaultQueryPrefix`, emitted by `IngestContractTests` from the same
`EmbeddingPrefixes.Table` that already feeds the document side, and `ingest.py` gains `query_prefix_for()`
mirroring `document_prefix_for()`. The table is carried **whole** — every family in it, not only
bge-base — because the same divergence applies to each.

This is the only server-side change, and it removes a live cross-language hazard rather than adding one:
`EmbeddingPrefixes.cs`'s own comment documents at length how a lookup divergence here embeds a corpus
differently while every test stays green.

### 2.6 Infrastructure

Two **single-service** starts, each `--no-deps`: `qdrant` (compose `:106`) and `tei-embed` (`:173`).
Both snapshots are restored with the documented upload loop. **`iverson-api` is never started** — its
composite `3ffafcd26416ed30` is perishable and unreproducible, and nothing here needs it.

Chunk vectors, centroids and query embeddings all go through `ingest.qdrant_request()` and
`ingest.embed()`, which `multivector.py` already uses for exactly these jobs.

### 2.7 The faithfulness check — run before any signal is computed

The chunk-hit dump's `score` is **not** raw cosine. Chunk candidates carry their parent document's
centroid (`ObjectSearchGrpcService.cs:646-654`), and `ResultReranker` fuses it, short-circuiting to the
base score only when there is neither centroid nor decay (`ResultReranker.cs:24-33`). `BenchmarkDocument`
has no timestamp field, so decay is absent. The recorded score is therefore:

```
score = (W_base · cos(q, chunkVector) + W_centroid · cos(q, parentCentroid)) / (W_base + W_centroid)
```

which at the shipped `0.45 / 0.45` is the mean of the two.

**The instrument recomputes that for every row of the dump and compares to the recorded value.** A
mismatch beyond float tolerance **aborts the run**. This is the check that makes every downstream number
trustworthy, because it exercises the query prefix, the model identity, the chunk vectors, the `parent_id`
payload lookup, the centroid vectors and the fusion constants in a single comparison. It is also the
only way to confirm the weights this dump was produced at: like λ, they are not recorded in its sidecar.

### 2.8 Instrument and outputs

One new script, `Iverson.Server/Iverson.LoadTest/scripts/aspect_vectors.py`, alongside the other harness
scripts. It restores nothing itself — restore is an operator step — and it writes, into
`~/repositories/iverson-benchmark-corpora/family2-vector-screen-2026-09-13/`, following the dated-directory
convention chunk-coverage Phase 2 and the aspect oracle both used (that tree is not a git repository, so
the verdict document transcribes from it and must say so):

- `pair-signals.tsv` — one row per relevant (query, document) pair: `queryId, docId, aspects, n_chunks,
  residual_spread, greedy_cover, effective_rank`, with `greedy_cover` emitted once per τ
- `faithfulness.txt` — the §2.7 comparison: rows checked, max absolute deviation, verdict
- `screen.txt` — ρ per candidate per population, the bootstrap CI on each difference against `n_chunks`,
  Holm-adjusted p, and the ρ(τ) curve

The verdict is recorded in `docs/plans/2026-09-GATE-family2-vector-aspect-screen.md` under the
gate-document convention, and gets its row in the ranked-changes §0 table. Item 15's closure text is
amended to reflect whichever way this lands.

---

## 3. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| V1 | A spread signal has content on this population | 83.5 % of the 2,733 relevant pairs have ≥ 2 chunks; 68.9 % have ≥ 3 |
| V2 | Chunk counts are small and capped, so count-like signals are coarse | distribution 1:450, 2:400, 3:487, 4:615, 5:674, 6:106, 7:1 — maximum 7 |
| V3 | The discriminating sub-population is substantial | 1,258 pairs (46.0 %) carry ≥ 2 aspects; 1,075 (39.3 %) carry ≥ 2 aspects **and** ≥ 2 chunks |
| V4 | `n_chunks` is a meaningful null, not a straw man | ρ = +0.0714 against aspect count over the same 2,733 pairs |
| V5 | Score-derived signals set the context for "vectors buy something" | best is `spread` at +0.0790; also mean_tail +0.0761, dispersion +0.0722, max_score +0.0572, n_within_095 +0.0428 |
| V6 | bge-base needs a query prefix and no document prefix | `EmbeddingPrefixes.cs`: `["BAAI/bge-base-en-v1.5"] = ("", "Represent this sentence for searching relevant passages: ")` |
| V7 | Python has no query prefix today | `ingest-contract.json`'s `embedding` object holds exactly `documentPrefixes` and `defaultDocumentPrefix` |
| V8 | The contract is emitted from the C# table by a test that can reach it | `Iverson.Api.Tests/Schema/IngestContractTests.cs`; `EmbeddingPrefixes` is `public` with a comment saying it is public *for* that test |
| V9 | Qdrant and TEI are independently startable | `Iverson.Server/docker-compose.yml`: `qdrant:` at `:106`, `tei-embed:` at `:173`, `iverson-api:` at `:426` |
| V10 | The harness already has the two primitives | `ingest.py`: `QDRANT_URL` `:148`, `DEFAULT_EMBED_URL` `:150`, `qdrant_request` `:279`, `embed` `:533`, `document_prefix_for` `:417`; `multivector.py` calls `ingest.embed` and `ingest.qdrant_request` |
| V11 | TEI needs no download | weights cached at `~/.cache/tei-bench-models/models--BAAI--bge-base-en-v1.5` |
| V12 | Restore is documented and mechanical | `scifact-512-qdrant-snapshots/RESTORE.md` — POST each `.snapshot` to `/collections/<name>/snapshots/upload?priority=snapshot` |
| V13 | **The dump's score is fused, not raw cosine** | `ObjectSearchGrpcService.cs:646-654` resolves the parent centroid from `r.Payload["parent_id"]` into the `RerankCandidate`; `ResultReranker.cs:24-33` short-circuits to `BaseScore` only when centroid **and** decay are both absent |
| V14 | Decay is absent for this corpus, so the fusion has exactly two terms | `BenchmarkDocument` carries no timestamp/decay field |
| V15 | The fusion constants are known | `VectorRankingOptions.cs:14-16` — `WBase` 0.45, `WCentroid` 0.45, `WDecay` 0.10 |
| V16 | The centroids needed to reproduce the fused score are on disk | both snapshots present: chunks 123,537,920 bytes, objects 124,247,040 bytes |
| V17 | The statistics libraries are available | `numpy`, `scipy`, `requests` import under the python-libs PYTHONPATH; `qdrant_client` is absent, so the REST API is used |
| V18 | Snapshot scale and geometry | `RESTORE.md`: 6,000 object points, 18,622 chunk points, 768 dims, bge-base, 2048/1792 |
| V19 | The key map joins the dump to the qrels | `keymap.json` is a flat 6,000-entry `{parentKey: docId}` object; 0 unresolved keys over the whole hits dump |

### 3.1 Execution-time preconditions

These cannot be verified without a running Qdrant, which is the experiment. Each is a **precondition the
instrument asserts and aborts on**, not an assumption taken on faith:

| # | Precondition |
|---|---|
| E1 | The restored chunks collection reports 18,622 points, the object collection 6,000 |
| E2 | The chunk vector is named `body_vector` and reports 768 dimensions |
| E3 | The object collection's centroid vector exists under the name the chunks path resolves, and is 768-dimensional |
| E4 | Chunk payloads carry `parent_id`, and its value form matches `keymap.json`'s keys |
| E5 | Both collections' distance metric is Cosine |
| E6 | The scroll API returns vectors when asked, for both collections |
| E7 | **§2.7's fused-score reproduction matches the recorded dump within float tolerance** — which is also the only available confirmation that the dump was produced at `WBase`/`WCentroid` = 0.45/0.45 |

---

## 4. Out of scope

- **Designing or building a term.** This screen decides whether a signal exists; it does not specify one.
- **Any ranking arm, any α-nDCG@10 measurement, any new run.** No arm is scored here.
- **`iverson-api`.** Never started; its composite is perishable.
- **Any corpus but FreshStack-2048.** It is the only one with subtopic labels.
- **Re-opening the oracle's GO.** Nothing here touches the +0.0609 or how it was obtained.

## 5. Known issues accepted as out of scope

- **A pass does not make a term worth building.** The aspect-coverage gate's conversion figure puts a
  realised term at ≈ +0.002 against a measured MDE of 0.0097, whatever signal it is built on. A clean pass
  tells you a signal exists and that vectors carry what scores do not; it does not predict a detectable
  gain, and the verdict document must say so.
- **16.5 % of the primary population is structurally inert.** Single-chunk pairs take a constant on every
  candidate and can only dilute. They are kept because a shipped term would face them; the §2.3
  sensitivity is where the signal is actually exercised.
- **`greedy_cover` and `effective_rank` are bounded above by `n_chunks`.** They are the two candidates most
  exposed to §2.4's null by construction. That is the point of the null, not a flaw in it — but a failure
  by those two is weaker evidence against the family than a failure by `residual_spread`.
- **One corpus, one window, one model.** The result binds FreshStack-2048 under bge-base at 2048/1792.
- **The fusion weights are not recorded in the dump's sidecar**, the same gap as λ (ranked-changes item
  16). E7 is the only check that they were 0.45/0.45; if it fails, the cause is ambiguous between the
  weights, the prefix and the model.
