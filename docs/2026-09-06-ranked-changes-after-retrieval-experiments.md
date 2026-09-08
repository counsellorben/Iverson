# Changes needed after the retrieval experiments — ranked choices

**Status: decision document, 2026-09-06.** Supersedes
`docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md`, which was written while ArguAna was
still running and lists four items that have since shipped. Everything here is stated against local main at
`9eb99f7` and the four gate documents under `docs/plans/2026-09-GATE-*.md`. Nothing here has been through
`thorough-brainstorming`; each item is the input to that, not a substitute for it.

The experiments this closes out are the ones scored on top-k relevance metrics (nDCG@10, R@50, AP) through
the `Iverson.LoadTest` benchmark harness: the prefix and title work, the chunk-window ablation, the
centroid / fusion-weight / decay sweeps, the MMR λ sweeps, the reranker Phase 1 and its document-input
trial, the embedding migration Phases 1 and 2 (including the enrichment-backend gate), and the multivector
layout experiment.

Items are ranked by **strength of evidence × production impact ÷ cost**. Each item lists its choices with
the recommended one first. "Choice" means a real either/or that a spec would have to pick; where the
evidence already decides, the item says so and the choice is only whether to act now.

---

## 0. Where things stand

| Experiment | Verdict | What shipped | Record |
|---|---|---|---|
| Task prefixes + title composition (nomic) | +0.0134 nDCG@10, not significant | model-conditional prefix table (`EmbeddingPrefixes`), empty for models that need none | `centroid-weighting-proposal.md`, `project-nomic-embedding-task-prefixes` |
| Chunk window 512/448 chars | best of the ablation (0.7072 vs 0.6820 cumulative, t = 2.20, does not survive correction) | **nothing** — production default is still 512 tokens × 4 = 2048/1792 chars; the 512-char window lives only in the harness corpus and the unmerged `chunk-size-512-experiment` branch | `2026-09-GATE-embedding-migration.md` §"512/448 window ruling" |
| Centroid weight 0.333 → 0.500 | +0.0037…+0.0089 nDCG@10 on FreshStack (Holm-significant), −0.0005 n.s. on NFCorpus, −0.0082 significant on SciFact | triple B `0.45/0.45/0.10` shipped as `VectorRankingOptions` defaults; all five ranking constants are bound configuration | `centroid-weighting-proposal.md` §"Decision 2026-08-31" |
| Request-scaled centroid weighting (schedule on chunks/doc) | **refuted** — the optimum tracked prefix misconfiguration, not density | nothing (design rejected) | same, §"The chunks-per-document schedule, tested directly and refuted" |
| MMR λ 0.70 → 1.00 | `SearchChunks` neutral to four decimals; `SearchSimilar` pays 9 % (NFCorpus) and 12.8 % (FreshStack) of R@50 — replicated | nothing; λ is configurable (`VectorRanking__Lambda`) but one value serves both RPCs | same, §"The MMR finding" |
| Reranker Phase 1 (ms-marco MiniLM, bge-reranker-base) + A4 document input | **GATE FAILED**: +0.0082 n.s.; bge-reranker-base −0.1126 significantly worse; A4 −0.0050 n.s. | harness only (`reranker` compose profile, `TeiRerankClient`); no server change | `2026-09-GATE-reranker-phase1.md` |
| Embedding migration Phase 1 (TEI bge-base / bge-small vs nomic) | **PASS for bge-base** (+0.0492 nDCG@10, p_adj 0.0010; NFCorpus +0.0185 significant); bge-small FAIL by 0.0028 on the R@50 bound | TEI `/v1/embeddings` route, `/info` identity guard, per-model base URLs | `2026-09-GATE-embedding-migration.md` |
| Embedding migration Phase 2 (ship bge-base; TGI for enrichment) | embedding switch shipped; **enrichment gate FAILED** → Phase C′ | bge-base is the default everywhere (compose, Helm `activeEmbeddingModel`, code); `tei-embed` is a default service; nomic dropped; Ollama stays for enrichment (`qwen2.5:3b`); `tgi` profile-gated | `2026-09-GATE-enrichment-backend.md` |
| Multivector layout (one MaxSim point per doc, gte-modernbert-base) | **NO-GO**: R@50 −0.0300 significant, nDCG@10 CI too wide; latency 0.61× passes; gte ≈ bge-base n.s. | `multivector.py` harness script; `TEI_MAX_BATCH_TOKENS` templated on `tei-embed` | `2026-09-GATE-multivector.md` |

Every gated number above was measured at the **512/448-character window on SciFact**, with NFCorpus and
FreshStack as secondary corpora. That single fact drives item 1.

---

## Tier 1 — production defaults that the evidence does not yet cover

### 1. The production chunk window has never been measured under the shipped model

**What is wrong.** `IversonChunkAttribute` defaults to 512 tokens / 64 overlap, which the consumer turns into
2048/1792 characters. Every gate verdict — bge-base's pass, the reranker's fail, the multivector's fail — was
measured at 512/448 characters, because that was the only window with a nomic baseline. The Phase 1 gate says
so explicitly: "the pass is measured under the 512-character chunk window, not main's 2,048 default … Phase 2
does not inherit a measurement for it." The 512-character window was also the ablation's best configuration,
and at 2048 characters the centroid is a degenerate copy of the object vector for 87 % of BEIR documents.
So the default that every fresh deployment gets is the one configuration with no measurement behind it.

**Choices, ranked.**

1. **Measure the default window under bge-base before deciding anything.** One SciFact ingest at
   2048/1792 through `ingest.py --chunk-max-chars 2048 --chunk-step 1792` (bge-base, TEI), snapshot,
   `benchmark-query`, `report.py --baseline` against the M1 run. Roughly 3 h on this box (6,587 chunks
   plus body embeds), no code change. If the default window is non-inferior, item 1 closes with a note; if it
   is worse, choice 2 becomes evidence-backed.
2. **Change the default to 128 tokens / 16 overlap** (= 512/64 characters, the measured window). A one-line
   attribute default plus the five client mirrors of it, but it changes every deployment's chunk count 3.85×
   and forces a re-ingest of every chunked type. Do not do this on the ablation evidence alone — the
   cumulative gain did not survive multiple-comparison correction.
3. **Leave 512 tokens and document the gap.** Cheapest, and honest, but it leaves the shipped default as the
   one unmeasured configuration indefinitely.

**Evidence lacking for any change:** a long-document corpus measured at both windows under bge-base.
FreshStack at 2048/1792 is 73 % multi-chunk and would be the right instrument; SciFact at that window is
81 % single-chunk and answers only the short-document case.

### 2. λ is one knob for two RPCs that pay opposite prices

**What is known.** Turning MMR off (λ = 1.00) costs `SearchChunks` nothing on any measure on two corpora,
because diversifying among chunks is undone by the max-passage collapse. On `SearchSimilar` it raises R@50 by
+0.0216 (NFCorpus, t = 4.46) and +0.0558 (FreshStack, t = 9.69) — the largest, best-replicated effect the
project has produced. The benefit side of MMR is unmeasured: BEIR-style qrels award nothing for diversity.
Triple B also narrowed the margin by which MMR promotes a dissimilar candidate (0.3500 vs 0.3475 in the
promotion test), so the shipped 0.70 sits closer to a boundary than it did.

**Choices, ranked.**

1. **Split λ per endpoint, default `SearchChunks` to 1.00, leave `SearchSimilar` at 0.70.** Two options
   values instead of one (`VectorRankingOptions.LambdaSimilar` / `LambdaChunks`, or a per-RPC override
   binding to the existing `Lambda`). `SearchChunks` at 1.00 is a free win: identical rankings to four
   decimals and the MMR pass is skipped. `SearchSimilar` stays as shipped because its diversity benefit is
   unmeasured. Small change; `ResultDiversifier` already reduces to `Take(topK)` bit-for-bit at 1.00.
2. **Measure diversity before touching `SearchSimilar`.** Implement α-nDCG over FreshStack's nugget qrels
   (they are subtopic judgments; the converter already emits them, iteration column = nugget) and score
   λ ∈ {0.5, 0.7, 0.85, 1.0} on `SearchSimilar`. This needs the harness to emit a query-level qrels file too
   (`qrels.query.tsv`, ~6 lines, deliberately not written in the debt-closure work) so R@50 stops being
   halved by the subtopic collapse. Roughly one FreshStack ingest at bge-base (several hours) plus four query
   runs.
3. **Set λ = 1.00 everywhere.** Not licensed: it optimises the benchmark's blind spot.

### 3. `SearchSimilar` now embeds only the first 512 tokens of a document

**What changed silently.** Under nomic on Ollama the object vector saw a 2,048-token context; TEI serves
bge-base at 512 tokens with `--auto-truncate`, so every document longer than that now gets an object vector of
its head only. Chunk vectors are unaffected. On SciFact (short documents) the `.similar` path still improved
(+0.0794 nDCG@10, +0.0626 R@50, both significant), so the switch was right — but the gate flagged that "any
Phase 2 decision that leans on whole-document embeddings should re-measure," and nothing has.

**Choices, ranked.**

1. **Measure on a long-document corpus** (FreshStack godot slice, mean 3,900 chars ≈ 1,000 tokens) —
   `.similar` R@50 under bge-base/TEI against the existing arctic and nomic runs. Can share the ingest with
   item 2's choice 2.
2. **Derive the object vector from the chunk centroid for over-length documents** instead of the truncated
   head. No extra embedding (the centroid is already computed), but it changes what `SearchSimilar` ranks on
   and needs its own measurement.
3. **Accept.** Documents beyond 512 tokens rank by their opening; the `_centroid` signal still covers the
   whole document in the fusion. Reasonable for abstract-shaped corpora, unmeasured for anything else.

**Update 2026-09-08.** The new `VectorRanking:SimilarViaChunksTypes` option routes `SearchSimilar` through
chunk retrieval for listed types instead of the truncated head embedding; on the `fs-2048` arm the served
ranking reproduces a same-binary collapsed chunk run within tolerance (nDCG@10 delta +0.0000, R@50 delta
+0.0000, both within ±0.005), so choice 2 above now has an operator-configurable alternative available.

---

## Tier 2 — cheap re-measurements before a verdict is treated as final

### 4. Multivector: the NO-GO carries two asymmetries a re-run could remove for the cost of an hour

**What the gate recorded.** R@50 −0.0300 (significant, 11 of 300 queries changed) and nDCG@10 −0.0140 (n.s.,
CI [−0.0312, +0.0032]). Two mechanisms sit inside those numbers: the per-chunk arm over-fetches 5× (250 chunks
→ 50 docs) while the multivector arm asks HNSW for 50 documents directly; and the control collection had one
segment below the indexing threshold, so ~16 % of it was exact-searched while the arm under test was 100 %
approximate. Both bias the control upward on R@50. The gte snapshots (`scifact-gte-qdrant-snapshots/`, three
collections) are on disk, so a re-run costs a restore, a `build`, and a 5-minute `query` — no ingest.

**Choices, ranked.**

1. **Re-run once with the two asymmetries removed, then close.** Force-optimise (or set
   `indexing_threshold` 0 on) both collections so neither has a sub-threshold segment; add an over-fetch to
   the multivector query (`limit` 250, truncate to 50) so both arms get comparable HNSW budgets; extend
   `cmd_query`'s precondition to require `indexed_vectors_count == points_count`. If R@50 is still
   significantly negative the layout is dead on this corpus; if it is not, the adoption cost in choice 2
   decides.
2. **Close as NO-GO and record the adoption cost that was never measured.** MaxSim does not report the
   winning row, so `SearchChunks`'s chunk text/index contract needs a local argmax over retrieved rows plus
   chunk texts in the point payload — a second round-trip and payload growth that eat into the 0.61× latency
   margin. Adoption would also need a write-time `docId` dedupe.
3. **Re-attempt on a long-document corpus.** Only worth it after choice 1 passes; SciFact at 512/448 is the
   layout-friendly case (3.85 rows per point) and it still lost.

### 5. bge-small: failed by 0.0028 at n = 300, with an 8.71× ingest speed-up on the table

**What the gate recorded.** nDCG@10 +0.0070 (passes), R@50 CI lower bound −0.0228 against a −0.02 margin
(fails). The minimum detectable effect at 80 % power was ~0.035, so the miss is within the resolution of a
300-query sample. Ingest was 8.71× faster than nomic on this box; 384 dims halve Qdrant memory.

**Choices, ranked.**

1. **Leave closed unless ingest throughput becomes the binding constraint for a deployment.** The rule was
   applied as written; the model is measurably not better on quality and the only argument for it is cost.
2. **Reopen with a pooled sample** (SciFact + NFCorpus queries under one paired test, or a larger BEIR
   corpus) if a laptop or edge profile needs the 384-dim footprint. The Phase 1 snapshots for M2 exist
   (`scifact-bge-small-qdrant-snapshots/`), so SciFact costs nothing to re-score; NFCorpus would need an
   ingest (~1 h at bge-small).
3. **Adopt for the laptop profile only.** Two models in production means two collection shapes and two
   identity guards; not worth it on a near-miss.

---

## Tier 3 — closed by the evidence; no production change

### 6. Reranking

Cross-encoder reranking of the winning chunk (ms-marco MiniLM-L6) gained +0.0082 n.s.; bge-reranker-base was
significantly worse (−0.1126); feeding the full document instead of the chunk was worse again (−0.0050 n.s.).
The oracle headroom (+0.21) did not convert because the shipped fusion is already at the MiniLM-CE ceiling.
**Choice:** close. Keep `TeiRerankClient` and the `reranker` compose profile as harness tooling (they cost
nothing when off); do not carry a reranker into the server. Revisit only with a stronger cross-encoder on a
GPU host, where the latency budget the spec allowed (120 s per batch) is not the constraint.

### 7. gte-modernbert-base

Not distinguishable from bge-base on SciFact (nDCG@10 −0.0129, R@50 +0.0173, AP −0.0019, all n.s.) at 1.30×
the ingest cost, and it needs `TEI_MAX_BATCH_TOKENS=4096` to start on a 9 GB box. **Choice:** stay on
bge-base. The templated `--max-batch-tokens` on `tei-embed` stays (default unchanged) because it is what lets
the harness try the next 8k-context model without a compose edit.

### 8. Request-scaled centroid weighting and the fusion triple

The chunks-per-document schedule was refuted; a single constant (`w = 0.500`, triple B) generalised across a
3.4× density range. The centroid-absent branch was never exercised by any captured row and stays analytic;
A vs B on real timestamps cannot be decided by any corpus this project has. **Choice:** no change. The five
constants are configuration, so a deployment with a recency-judged corpus can move them without a release.

### 9. Multi-property vector search

Costed 2026-08-26 and deferred: a proto change across five clients, an authorization fork (reject vs drop
unauthorised properties), and a second uncalibrated fusion stacked on the first. Not a BEIR lever.
**Choice:** stays deferred behind item 2's diversity measurement; when it is picked up it needs its own spec.

---

## Tier 4 — follow-ups the gates left open, and housekeeping

### 10. Enrichment backend (Phase C′)

- **Cloud 3B measurement** — Ben's call. `enrich_bench.py` and the profile-gated `tgi` service are reusable
  as-is on a `c7i.2xlarge`-class AMX node; this box cannot host the 3B model.
- **`MaxSourceChars = 8_000`** in `EnrichmentConsumer` was sized for Ollama's 4,096-token head-first
  truncation. Choices: keep (matches the backend that stayed); make it configuration alongside
  `EnrichmentServiceOptions` so a TGI deployment with a larger context can raise it; or derive it from the
  backend's reported context length. Recommended: keep until a second backend actually ships.
- `README.md` (lines 20, 64, 98) and `Iverson.Server/docs/security/tma.md` (§I) still describe embeddings as
  Ollama/nomic. A documentation pass is owed; the tma entry also needs a TEI row.

### 11. `SearchChunks.top_k` counts chunks and the server does not dedupe by parent

A caller who wants N documents must over-request and collapse client-side, exactly as the harness does
(`ChunkBudgetMultiplier = 5`). No client documentation says so. **Choices:** document it in the client standard
and each client's search docs (recommended, cheap); add a document-level `top_k` option to the proto (five
languages, a new spec); do nothing. The multivector experiment's "over-fetch is where the recall lives"
finding is the same mechanism seen from the other side.

### 12. Stale and contradictory documents

- `docs/specs/2026-08-29-empty-chunk-guard-design.md` and its plan specify an `ArgumentException` inside the
  generator; what shipped (`c4eb11c`) is a typed `EmptyEmbeddingInputException` from `EmbeddingService`.
  Executing either document would undo the shipped design. **Add a superseded banner to both** — the sharpest
  item in this tier.
- `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md` — superseded by this document; a
  status line pointing here was added alongside this file.
- `docs/centroid-weighting-proposal.md:795` still says the stale-image incident is "unaddressed in the
  harness"; build identity has shipped in every run sidecar since 2026-08-31.

### 13. Branches and worktrees left by the experiments

| Ref | State | Recommendation |
|---|---|---|
| `centroid-ablation` (worktree `.worktrees/embedding-prefixes-and-title`) | 9 ahead; arctic prefixes and the empty-window drop are superseded by main; `--exclude-self` for ArguAna (`c733098`) is not on main | cherry-pick `c733098` if ArguAna is ever re-run (its 1,298 self-matching queries need it), then delete the worktree and branch |
| `chunk-size-512-experiment` | 6 ahead; harness-side 512-char chunking and the ingest-contract generator, most of it since re-landed on main | delete after item 1 is decided; nothing on it is the production default change |
| `benchmark-exclude-self` | 2 ahead | same content as the `centroid-ablation` cherry-pick; delete once one of them lands |
| `embedding-prefixes-and-title` | 5 ahead; its content reached main by other commits | delete |
| `decay-share-triple-b` | 1 ahead, redundant (same content under a different hash) | `git branch -D` |
| `bump-ollama-0.12.11`, `retrieval-quality-benchmark`, `single-chunk-field`, `model-conditional-embedding-prefixes`, `test-container-serialization`, `worktree-embedding-model-configuration` | fully merged | `git branch -d` |

### 14. Operational hazards recorded during the experiments, worth a line in the runbook

- `tei-embed` is a default service whose model comes only from `EMBED_MODEL_ID` in the invoking shell; any
  `docker compose up` that touches it from a shell without the variable recreates it on bge-base, and the
  api/worker `/info` guards then fail against a non-default model. Harmless in production (bge-base is the
  default), fatal mid-experiment.
- The live compose containers were created from worktree paths that no longer exist; a tier-wide `up` from
  main recreates `postgres` and `authentik-server`. Only single-service `--no-deps` actions are safe until the
  stack is recreated from main deliberately.
- Qdrant's `wait=true` covers the write, not the optimizer; a fresh collection is exact-searched until the
  indexer finishes, and a collection can sit green with a sub-threshold segment forever. Any latency or recall
  comparison between two collections must check `indexed_vectors_count == points_count` on both.

---

## What this document does not propose

- **Changing the fusion triple or removing the centroid.** Removing it costs −0.0263 nDCG@10 on FreshStack
  (Holm-significant); raising it further is significantly worse on SciFact.
- **Changing the production embedding model again.** bge-base beat nomic by the widest margin in the project's
  history and matched gte; the next candidate is the one that can be served at 8k context on this box, and
  none was found.
- **Adding bpref or other judged-negative measures to the default report.** BEIR qrels contain no `rel = 0`
  rows, so bpref reduces to recall there; it is meaningful only on FreshStack.
- **Request-shape changes to `SearchSimilarRequest` / `SearchChunksRequest`** beyond item 11's documentation.
  No evidence supports a caller-set ranking knob, and a bad caller choice degrades ranking with no error.
