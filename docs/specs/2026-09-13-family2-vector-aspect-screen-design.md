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
| Chunk-hit pool | `chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.hits.tsv` — one row per hit: `queryId, parentKey, rank, score`. It names **parents, not chunks**; chunk identity is reconstructed per §2.7 |
| Top-50 ranking (run B) | `chunk-coverage-phase1-2026-09-09/runs/fs2048-pool.chunks.trec` — the 50 documents per query the oracle's rankings permute |
| Aspect labels | `freshstack-2048-2026-09-07/qrels.nugget.trec` |
| Key map | `freshstack-2048-2026-09-07/keymap.json` (parentKey → docId) |

### 2.2 The three candidate signals

For a relevant (query, document) pair: let `q̂` be the unit query vector and `C = {c_1 … c_n}` the
document's chunk vectors present in that query's pool, identified by the §2.7 reconstruction.

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

**Primary: the 2,733 relevant (query, document) pairs among the 50 documents run B retrieved for each
query** — the same population the oracle's ranking A reorders, and the same one the score-derived probe
used. All 2,733 have at least one chunk in the pool.

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

### 2.7 Chunk identity and the faithfulness check — run before any signal is computed

The chunk-hit dump's `score` is **not** raw cosine. Chunk candidates carry their parent document's
centroid (`ObjectSearchGrpcService.cs:646-654`), and `ResultReranker` fuses it, short-circuiting to the
base score only when there is neither centroid nor decay (`ResultReranker.cs:24-33`). `BenchmarkDocument`
has no timestamp field, so decay is absent. The recorded score is therefore:

```
score = (W_base · cos(q, chunkVector) + W_centroid · cos(q, parentCentroid)) / (W_base + W_centroid)
```

which at the shipped `0.45 / 0.45` is the mean of the two.

The dump records a parent key and a fused score per hit, **not a chunk id**. Chunk identity is therefore
*reconstructed*, and the reconstruction **is** the faithfulness check. For each `(queryId, parentKey)`
group the instrument enumerates that parent's chunks in the chunks collection (payload filter
`parent_id == parentKey`), computes the expression above for each, and matches each recorded score to the
unique chunk whose recomputed value lies within float tolerance. **`C` is the matched set.** A recorded
row with **no** match, or with **more than one**, aborts the run.

**The reconstruction runs over the `(queryId, parentKey)` groups of the §2.3 primary population — 2,733
groups, 9,184 recorded rows — not over all 172,704 groups of the dump.** That scope is load-bearing, not
a convenience. The recorded score is a float32 (`Score = (float)ranked.FusedScore`,
`ObjectSearchGrpcService.cs:588`), so even a perfect recomputation lands up to half a float32 ULP —
2.98e-08 — from its target. Over the whole dump the minimum separation between two of a parent's recorded
scores is 5.96e-08, exactly one ULP: no room for that floor, let alone for reproduction error — and the
error is non-zero by construction, because `EmbeddingService.cs:117` narrows each query-vector component
to `float` while `ingest.embed` keeps the JSON double. Over the screened groups the minimum separation is
8.94e-07, fifteen ULPs.

This is the check that makes every downstream number trustworthy, because it exercises the query prefix,
the model identity, the chunk vectors, the `parent_id` payload lookup, the centroid vectors and the
fusion constants in a single comparison — and it does so *by assigning* the chunk vectors rather than
merely reading them. It is also the only way to confirm the weights this dump was produced at: like λ,
they are not recorded in its sidecar.

`faithfulness.txt` reports rows checked, rows with no match, rows with an ambiguous match, and the
maximum residual over matched rows. **The unmatched and ambiguous counts are the falsifying
statistics**; the residual is a selection artifact of the matching and cannot exceed the tolerance by
construction.

### 2.8 Instrument and outputs

One new script, `Iverson.Server/Iverson.LoadTest/scripts/aspect_vectors.py`, alongside the other harness
scripts. It restores nothing itself — restore is an operator step — and it writes, into
`~/repositories/iverson-benchmark-corpora/family2-vector-screen-2026-09-13/`, following the dated-directory
convention chunk-coverage Phase 2 and the aspect oracle both used (that tree is not a git repository, so
the verdict document transcribes from it and must say so):

- `pair-signals.tsv` — one row per relevant (query, document) pair: `queryId, docId, aspects, n_chunks,
  residual_spread, greedy_cover, effective_rank`, with `greedy_cover` emitted once per τ
- `faithfulness.txt` — the §2.7 reconstruction: rows checked (the screened population's 9,184), rows with
  no match, rows with an ambiguous
  match, the maximum residual over matched rows, verdict
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
| V20 | **The dump names parents, not chunks** | header `queryId parentKey rank score`, 4 fields on all 369,601 lines; `BenchmarkQueryScenario.cs:414` types `RawHits` as `(string ParentKey, double Score)`, and `ChunkSearchResponse` (`ObjectSearchGrpcService.cs:585-590`) carries no chunk id — `ChunkText` is received and discarded |
| V21 | The pool is a pure fused-score prefix, so pool membership is decided by score alone | 0 score inversions against rank order across all 369,600 rows — unreachable had `ResultDiversifier` reordered the over-fetched 2,200 candidates |
| V22 | Score-matching is near-unambiguous **on the screened population** | over 172,704 `(query, parent)` groups, 18 of 196,896 adjacent within-group score pairs lie within 1e-6; minimum gap 5.96e-08, one float32 ULP at 0.7 — that whole-dump figure leaves no tolerance budget, which is why §2.7 scopes the reconstruction to the screened groups, where the minimum separation is 8.94e-07 |
| V23 | The base term is recomputable from a scrolled vector | no `quantization` symbol anywhere under `Iverson.Vector/` or `Iverson.Api/`; `ingest.py:379` creates collections with no `quantization_config` |
| V24 | `fs2048-pool.chunks.trec` is run B, and the top-50 restriction is what fixes the population | its per-query 50-document sets are set-identical to `aspect-oracle-2026-09-13/oracle-A.trec` on 672/672 queries (order differs on 596); the restricted join reproduces 2,733 / 610 / 62 and V2's distribution exactly, the unrestricted join gives 4,360 / 653; `qrels.trec` and `qrels.nugget.trec` induce the identical 5,445-pair relevance set |
| V25 | The restored snapshot is the collection state the dump was produced from | the ingest that wrote these collections finished `2026-09-07T10:48:17Z` (`keymap.json.stats.json`); the two snapshots are stamped `10:48:59` and `10:49:03`, 42 s and 46 s later |
| V26 | The Qdrant scroll path authenticates | `ingest.py:149`'s `QDRANT_API_KEY` is byte-identical to the `qdrant:` block's `QDRANT__SERVICE__API_KEY` in `docker-compose.yml`; `qdrant_request` attaches it at `:285` |
| V27 | The instrument's query vector is **not** bit-identical to the server's | `EmbeddingService.cs:117` narrows each component with `(float)e.GetDouble()` while `ingest.py:558` returns the parsed JSON double — so a non-zero reproduction error is guaranteed, and §2.7's tolerance needs a positive budget |
| V28 | **The recorded score is float32-quantized, which sets the tolerance budget** | all 369,600 recorded scores round-trip exactly through `np.float32`; the minimum within-group separation is 5.960464e-08 = `np.spacing(np.float32(0.7))` = 1.00 ULP over the whole dump and 8.940697e-07 = 15.00 ULP over the screened population, against a half-ULP reproduction floor of 2.98e-08 |

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
| E7 | **§2.7's reconstruction matches every recorded row of the screened population to exactly one chunk** — no unmatched row, no ambiguous row — which is also the only available confirmation that the dump was produced at `WBase`/`WCentroid` = 0.45/0.45 |

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
  16). E7 is the only check that they were 0.45/0.45, and it fires as a count of unmatched or ambiguous
  rows rather than as a score deviation; if it fails, the cause is ambiguous between the weights, the
  prefix, the model, the match tolerance and whether the restored collections are the state the dump was
  produced from.
