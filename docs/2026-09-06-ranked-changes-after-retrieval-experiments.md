# Changes needed after the retrieval experiments — ranked choices

> **Status: living index + decision record. Index (§0) reconciled 2026-09-10.** §0 is the durable half and
> is kept current: one row per gate document under `docs/plans/2026-09-GATE-*.md`, plus any non-gated work
> that produced a verdict. When a gate lands, its row is added there. The tiered items below are decision
> content and expire as they are settled; a settled item collapses to its verdict and a pointer to the gate
> that settled it.

Supersedes `docs/2026-08-28-proposed-code-changes-from-retrieval-experiments.md`, which was written while
ArguAna was still running and lists four items that have since shipped. Everything here is stated against
local main at `9eb99f7` and the four gate documents under `docs/plans/2026-09-GATE-*.md`. Nothing here has
been through `thorough-brainstorming`; each item is the input to that, not a substitute for it.

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

§0 is the durable half and is kept current: one row per gate document under
`docs/plans/2026-09-GATE-*.md`, plus any non-gated work that produced a verdict. When a gate lands, its row
is added there.

| Experiment | Verdict | What shipped | Record |
|---|---|---|---|
| Task prefixes + title composition (nomic) | +0.0134 nDCG@10, not significant | model-conditional prefix table (`EmbeddingPrefixes`), empty for models that need none | `centroid-weighting-proposal.md`, `project-nomic-embedding-task-prefixes` |
| Chunk window 512/448 chars | best of the ablation at 512/448, **and the shipped 2048/1792 default has since been measured and passed** (rule 7.1, 2026-09-07) | nothing shipped, by decision | `2026-09-GATE-embedding-migration.md` §"512/448 window ruling" |
| Centroid weight 0.333 → 0.500 | +0.0037…+0.0089 nDCG@10 on FreshStack (Holm-significant), −0.0005 n.s. on NFCorpus, −0.0082 significant on SciFact | triple B `0.45/0.45/0.10` shipped as `VectorRankingOptions` defaults; all five ranking constants are bound configuration | `centroid-weighting-proposal.md` §"Decision 2026-08-31" |
| Request-scaled centroid weighting (schedule on chunks/doc) | **refuted** — the optimum tracked prefix misconfiguration, not density | nothing (design rejected) | same, §"The chunks-per-document schedule, tested directly and refuted" |
| MMR λ 0.70 → 1.00 | `SearchChunks` neutral to four decimals; `SearchSimilar` pays 9 % (NFCorpus) and 12.8 % (FreshStack) of R@50 — replicated | nothing; λ is configurable (`VectorRanking__Lambda`) but one value serves both RPCs | same, §"The MMR finding" |
| Reranker Phase 1 (ms-marco MiniLM, bge-reranker-base) + A4 document input | **GATE FAILED**: +0.0082 n.s.; bge-reranker-base −0.1126 significantly worse; A4 −0.0050 n.s. | harness only (`reranker` compose profile, `TeiRerankClient`); no server change | `2026-09-GATE-reranker-phase1.md` |
| Embedding migration Phase 1 (TEI bge-base / bge-small vs nomic) | **PASS for bge-base** (+0.0492 nDCG@10, p_adj 0.0010; NFCorpus +0.0185 significant); bge-small FAIL by 0.0028 on the R@50 bound | TEI `/v1/embeddings` route, `/info` identity guard, per-model base URLs | `2026-09-GATE-embedding-migration.md` |
| Embedding migration Phase 2 (ship bge-base; TGI for enrichment) | embedding switch shipped; **enrichment gate FAILED** → Phase C′ | bge-base is the default everywhere (compose, Helm `activeEmbeddingModel`, code); `tei-embed` is a default service; nomic dropped; Ollama stays for enrichment (`qwen2.5:3b`); `tgi` profile-gated | `2026-09-GATE-enrichment-backend.md` |
| Multivector layout (one MaxSim point per doc, gte-modernbert-base) | **NO-GO, closed 2026-09-10 after a full re-run.** Both asymmetries removed: index state was inert (reproduced the original to 4 decimals), budget equalisation made the arm worse. At `params.exact` the arm ties the control exactly (Δ +0.0000, 0/300 changed) because MaxSim and max-passage collapse are the same function | `multivector.py` harness script (`--mv-hnsw-ef`, `--mv-exact`, `probe`); `TEI_MAX_BATCH_TOKENS` templated on `tei-embed` | `2026-09-GATE-multivector.md` + its two 2026-09-10 amendments |
| Tier 1 retrieval defaults | Rule 7.1 **PASS**, chunk-window default STANDS; Rule 7.2 λ split per endpoint; Rule 7.3 FIRES → follow-up | `LambdaSimilar` 1.00 / `LambdaChunks` 0.70; the `.chunks.diversity.json` sidecar. Chunk-window defaults unchanged by decision | `2026-09-GATE-tier1-defaults.md` |
| `SearchSimilar` centroid retrieval (rule 7.3 follow-up) | **FAIL** — head retrieval stands, `SimilarRetrievalVector` deleted; reconciled in the 2026-09-08 amendment | nothing; Task 1's wiring reverted | `2026-09-GATE-similar-centroid.md` |
| Chunk-coverage signal, Phase 1 | **Phase 2 warranted** — 85.0 % of top-50 slots carry a tail; β calibrated | harness only (`beta_invariant.py` and the β ladder) | `2026-09-GATE-chunk-coverage.md` |
| Chunk-coverage signal, Phase 2 | **NO β QUALIFIES** — significant negative on all five arms, not a null | nothing | `2026-09-GATE-chunk-coverage-phase2.md` |

Every gated number above was measured at the **512/448-character window on SciFact**, with NFCorpus and
FreshStack as secondary corpora. That single fact drives item 1.

---

## Tier 1 — production defaults that the evidence does not yet cover

### 1. The production chunk window has never been measured under the shipped model — CLOSED 2026-09-07

Rule 7.1 PASS on both halves; the chunk-window default STANDS. Spec §3.4 was not executed: the five client
attribute defaults stay at 512 tokens / 64 overlap. Record: `docs/plans/2026-09-GATE-tier1-defaults.md`

### 2. λ is one knob for two RPCs that pay opposite prices — CLOSED 2026-09-07

`LambdaSimilar` 1.00, `LambdaChunks` 0.70 shipped (`VectorRankingOptions.cs:24-25`). Record:
`docs/plans/2026-09-GATE-tier1-defaults.md`

⚠ This item called λ = 1.00 on `SearchChunks` a free win. It was an artifact of the harness's max-passage
collapse, which reduces chunks to documents before scoring and so cannot see chunk-level MMR. Production
`SearchChunks` returns the chunk list: at λ = 1.00 a request for ten chunks yields 5.2 distinct source
documents instead of 7.2 on `fs-512`.

### 3. `SearchSimilar` now embeds only the first 512 tokens of a document — RESOLVED 2026-09-08

`VectorRanking:SimilarViaChunksTypes` ships the operator-configurable alternative
(`VectorRankingOptions.cs:30`); the rule 7.3 follow-up gated FAIL and was reconciled. Records:
`docs/plans/2026-09-GATE-tier1-defaults.md`, `docs/plans/2026-09-GATE-similar-centroid.md`

**Empty.** Every production default this campaign identified as unmeasured has now been measured. The
fusion triple's A-vs-B choice (item 8) is a separate case — not unmeasured but *undecidable* here, since no
corpus this project has can judge recency.

---

## Tier 2 — cheap re-measurements before a verdict is treated as final

### 4. Multivector — CLOSED 2026-09-10

NO-GO. The re-run removed both asymmetries and the layout is the same function as the control: at
`params.exact` the arm ties it exactly. Record: `docs/plans/2026-09-GATE-multivector.md` and its two
2026-09-10 amendments

⚠ This item asserted both asymmetries biased the control upward. After index equalisation the **arm** held
the larger corpus fraction (1.93 % vs 1.25 %), so the retrieval-budget asymmetry favoured the arm. The
index-state asymmetry was real and inert — equalising it reproduced the original gate to four decimal
places.

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

### 15. A coverage term that is not a chunk count in disguise

Phase 2 rejected `max_chunk + β·Σ(next 3)`, but its tail term correlates with tail *count* at 0.9970 over
all 172,704 pooled pairs, and because β multiplies the whole term the degeneracy is β-invariant. What was
falsified is count-weighted promotion of multi-chunk documents; a coverage term whose value does not
collapse onto the count is untested. FreshStack-2048 and its snapshots are on disk and `beta_invariant.py`
is built, so the open work is a term, not an instrument. Record this explicitly so the Phase 2 result is
not mis-cited as "coverage was tried and failed" — a misreading `docs/plans/2026-09-GATE-chunk-coverage-phase2.md`
itself warns against.

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
**Choice:** stays deferred on the grounds that still hold — the proto change across five clients, the
authorization fork, and the second uncalibrated fusion. The previously stated blocker, item 2's diversity
measurement, was met on 2026-09-07 (α-nDCG@10 swept on both FreshStack arms); when this item is picked up
it needs its own spec.

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
languages, a new spec); do nothing. Item 4's re-run makes this sharper than a mechanism note: retrieving
chunks and collapsing by parent on the max IS max-passage scoring, proven bit-identical to exact MaxSim on 243
of 300 queries, so the only thing that degrades a caller's document-level recall is truncating the chunk list
too early. Over-request and collapse is not a workaround; it is the scoring function.

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
| `tier1-retrieval-defaults` (worktree `.worktrees/tier1-retrieval-defaults`) | fully merged (0 ahead); the work landed on main 2026-09-07 but the worktree and branch are still present | `git worktree remove .worktrees/tier1-retrieval-defaults && git branch -d tier1-retrieval-defaults` |
| `chunk-size-512-experiment` | 6 ahead; harness-side 512-char chunking and the ingest-contract generator, most of it since re-landed on main | item 1 was decided 2026-09-07, so the condition is met — delete now; nothing on it is the production default change |
| `benchmark-exclude-self` | 2 ahead | same content as the `centroid-ablation` cherry-pick; delete once one of them lands |
| `embedding-prefixes-and-title` | 5 ahead; its content reached main by other commits | delete |
| `decay-share-triple-b` | 1 ahead, redundant (same content under a different hash) | `git branch -D` |
| `bump-ollama-0.12.11`, `retrieval-quality-benchmark`, `single-chunk-field`, `model-conditional-embedding-prefixes`, `test-container-serialization`, `worktree-embedding-model-configuration` | fully merged | `git branch -d` |

### 14. Operational hazards recorded during the experiments, worth a line in the runbook

- `tei-embed` is a default service whose model comes only from `EMBED_MODEL_ID` in the invoking shell; any
  `docker compose up` that touches it from a shell without the variable recreates it on bge-base, and the
  api/worker `/info` guards then fail against a non-default model. Harmless in production (bge-base is the
  default), fatal mid-experiment.
- `iverson-postgres` and `iverson-authentik-server` were both created from main's path
  (`/home/ben/repositories/Iverson/Iverson.Server`), but `iverson-api` was created from
  `.worktrees/chunk-coverage-phase2/Iverson.Server`, a worktree that no longer exists. `iverson-api`'s
  composite is not reproducible from a deleted worktree, so it cannot simply be recreated in place. Only
  single-service `--no-deps` actions are safe until the stack is recreated from main deliberately (verified
  2026-09-10 via `podman inspect --format '{{.Name}} :: {{index .Config.Labels
  "com.docker.compose.project.working_dir"}}'`).
- Qdrant's `wait=true` covers the write, not the optimizer; a fresh collection is exact-searched until the
  indexer finishes, and a collection can sit green with a sub-threshold segment forever. Any latency or recall
  comparison between two collections must check `indexed_vectors_count == points_count` on both.

### 16. `benchmark-query`'s run sidecar records no λ

Attesting which λ a past run used needs a four-part reconstruction: descending-order violation counts,
distinct-parent means against the spec's table, rank-1 identity across queries, and MVID equality. A λ
field in the sidecar retires all of it.

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
