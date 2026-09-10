# Multivector re-run: removing the two control-favouring asymmetries — design

**Date:** 2026-09-10
**Status:** approved, not executed. No step below may run while the box is busy (§6).
**Parent gate:** `docs/plans/2026-09-GATE-multivector.md` — NO-GO, recorded 2026-09-06.
**Parent spec:** `docs/specs/2026-09-04-multivector-experiment-design.md` — §7 is the gate rule, §6 the protocol this one subsets.
**Ranked-changes item:** `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` §4 (Tier 2), choice 1.

## 1. The question

Does the multivector NO-GO survive once the two control-favouring asymmetries the gate itself
recorded are removed? The gate named both and said a re-attempt should settle them
(`2026-09-GATE-multivector.md:526-528`).

What the original gate measured — `gte-multivector-raw` vs `gte-chunks-raw`, 300 SciFact queries,
gte-modernbert-base at the 512/448 character window, 5,183 documents / 19,967 chunks (3.85 rows per
multivector point):

| # | Criterion | Deciding number | Bar | |
|---|---|---|---|---|
| 1 | nDCG@10 delta 95% CI lower bound | −0.0312 (Δ −0.0140, perm p 0.1210, MDE 0.0245) | > −0.02 | FAIL |
| 2 | R@50 delta 95% CI lower bound | −0.0515 (Δ −0.0300, perm p 0.0108, Holm p_adj 0.0216 at m=2, 11/300 queries changed) | > −0.02 | FAIL |
| 3 | p95 latency ratio | 0.61× (16.115 ms ÷ 26.459 ms) | ≤ 1.25× | PASS |

The two asymmetries, both biasing toward the control:

1. **Unequal index state.** `runs/raw-latency.json` records the chunks collection at
   `indexed_vectors_count` 16,678 of 19,967 points — one segment below `indexing_threshold`, searched
   exactly rather than through HNSW — while the multivector collection was 5,183 of 5,183. Roughly
   16% of the control was brute-force (perfect-recall) searched while the arm under test was 100%
   approximate.
2. **Unequal retrieval budget.** `multivector.py:314` asks HNSW for `CHUNK_TOP_K` = 250 chunks and
   collapses to 50 documents; `multivector.py:323` asks for `DOCUMENT_BUDGET` = 50 documents
   directly.

## 2. What verification changed

The design originally rested on the gate's stated mechanism for asymmetry 2: the control's 250-chunk
budget "gives HNSW many chances to surface a document through any of its chunks". A read-only probe
against the live chunks collection falsified the retrieval-budget half of that.

Querying one in-distribution vector at `limit` 50, `limit` 65, `limit` 250, and `limit` 50 with an
explicit `params.hnsw_ef` of 1000 returned **byte-identical top-50 rankings in all five pairwise
comparisons** (identical order, identical set). At this corpus scale the control's retrieval is
effectively exact; there is no approximation slack for an over-fetch to exploit on the control side.

Follow that through and the gate's mechanism does not survive as stated. If the control is exact it
returns the true top-250 chunks and collapses to the best 50 documents among them, so a document is
reachable only if its best chunk clears the global top-250. An *exact* multivector arm returns the
true top-50 documents by MaxSim over **all** of a document's chunks, with no 250-chunk cutoff — the
strictly better answer. Two exact arms could not produce the observed loss.

**So the load-bearing inference is that the multivector arm's HNSW over `max_sim` multi-row points is
genuinely approximate, and that — not the retrieval budget — is what the original gate measured.**
This is an inference from the control's exactness, not a direct measurement of the arm; §5.5 measures
it directly and is the first thing this experiment does.

This does not invalidate the re-run. It sharpens it, and it changes what a null result means.

## 3. Design

### 3.1 Restore side-by-side, never overwrite

All three collection names are already CLI parameters (`multivector.py:350-353`), so the gte
snapshots restore under **alias** names and the live collections are never touched:

| Snapshot (in `scifact-gte-qdrant-snapshots/`) | Restored as |
|---|---|
| `benchmark_documents_tenant_bypass-…` | `mvrerun_objects` |
| `benchmark_documents_chunks_tenant_bypass-…` | `mvrerun_chunks` |
| `benchmark_documents_multivector_tenant_bypass-…` | `mvrerun_multivector` |

Restore is `POST /collections/<alias>/snapshots/upload?priority=snapshot` with `-F snapshot=@<file>`
— the destination is the collection named in the URL path, not the one the snapshot came from.

This drops spec §6 step 8's second destructive restore entirely. It also makes the experiment immune
to a fact discovered during verification: the live `benchmark_documents_chunks_tenant_bypass` is at
**18,622 points, not the 19,967** the gate's "box state at close" recorded — the chunk-coverage work
re-ingested over it. Under spec §6's overwrite-and-restore-back, that baseline would have had to be
re-established from a snapshot that no longer matches what is live.

Teardown is `DELETE /collections/<alias>` on all three. Nothing else is undone on the Qdrant side.

### 3.2 TEI swap

Query embedding needs gte-modernbert-base. `docker-compose.yml:180` templates both knobs, and the
model is already cached in the `tei_models` volume from 2026-09-06:

    TEI_MAX_BATCH_TOKENS=4096 EMBED_MODEL_ID=Alibaba-NLP/gte-modernbert-base \
      docker compose up -d --no-deps --force-recreate tei-embed

Wait for `/info` to report the gte model id and `max_input_length` 4096. Restore afterwards with
`EMBED_MODEL_ID=BAAI/bge-base-en-v1.5 … --force-recreate tei-embed` from a shell with neither
`BENCH_EMBED_MODEL` nor `TEI_MAX_BATCH_TOKENS` set.

`--no-deps` is mandatory: `iverson-api` (`docker-compose.yml:507`) and the worker
(`docker-compose.yml:576`) both declare `tei-embed: condition: service_healthy`. Neither is used by
this experiment — both gated arms are raw Qdrant — but a cascade would recreate them on the wrong
model. `stack.py` must not be called at any point (parent spec §11 A18).

### 3.3 Index equalisation

For each of `mvrerun_chunks` and `mvrerun_multivector`:

    PATCH /collections/<name>  {"optimizers_config": {"indexing_threshold": 1}}

then poll `GET /collections/<name>` until `indexed_vectors_count == points_count`, with a bounded
wait. A small **positive** threshold is what forces every segment to be HNSW-indexed.

The parent gate's remediation note suggests setting `indexing_threshold` "to 0 to force
always-indexed segments" (`2026-09-GATE-multivector.md:527`). That is inverted: in Qdrant, segments
*below* the threshold use plain (exact) search and `0` disables vector indexing altogether, forcing
plain search everywhere. The live collection corroborates the direction — `indexing_threshold` 10000
with `indexed_vectors_count` 16,673 of 18,622, i.e. one sub-threshold segment left unindexed.

### 3.4 Script changes (`Iverson.Server/Iverson.LoadTest/scripts/multivector.py`)

1. **`--mv-limit`, default 65.** The multivector arm asks HNSW for `--mv-limit` documents and
   truncates to `DOCUMENT_BUDGET`. 65 matches the control's *corpus fraction*: 250/19,967 = 1.2521%
   of chunk nodes; 1.2521% of 5,183 document nodes = 64.89 → 65, an over-fetch of 1.30×. The
   alternative of matching the control's 5× multiplier (250) was rejected: 250 of 5,183 document
   nodes is 4.82% of the corpus, ~3.85× the control's slice, which would bias the comparison toward
   the arm. Corpus fraction is the neutral match and is interpretable in both directions.
2. **Route the multivector arm through `collapse_by_doc`.** Today the arm writes TREC rows straight
   from each point's `payload.docId` with no dedupe, unlike the chunk arm — the gate flagged that two
   points sharing a `docId` would produce a malformed run. `collapse_by_doc` (`multivector.py:74`)
   already does max-per-doc → sort descending → **truncate after the collapse**, which is exactly the
   dedupe-then-truncate order `--mv-limit` requires. Reusing it fixes the dedupe and implements the
   truncation in one move; no new helper.
3. **`cmd_query`'s precondition gains `indexed_vectors_count == points_count`** for both collections.
   Today it checks only `info["status"] != "green"` (`multivector.py:268`), which is exactly why
   asymmetry 1 passed unnoticed: a collection sitting below its indexing threshold is green.
4. **`cmd_query` refuses to overwrite existing run files.** The gate's `gte-chunks-raw.chunks.trec`
   and `gte-multivector-raw.chunks.trec` are unreproducible evidence and the run labels are constants
   (`multivector.py:35-36`), so a re-run pointed at the old directory would destroy them. §4 uses a
   fresh run directory, which is sufficient on its own; the refusal is the fail-closed guard for the
   case where someone points it at the old one anyway, matching the `--scores-path` collision refusal
   the chunk-coverage work added for this same hazard class.
5. **New `probe` subcommand** implementing §3.5.

`test_multivector.py` covers `collapse_by_doc`, `rank_chunk_hits`, `trec_lines`, `group_rows`,
`resolve_parents` and `summarize_latency` as pure functions with no Qdrant or TEI. Changes 1, 2 and 4
are testable there; 3 and 5 are live-stack behaviour.

### 3.5 The exactness probe — runs FIRST, before the gated run

Over the first 30 queries, query **both** restored collections at `limit` 50, `limit` 65,
`limit` 250, and `limit` 50 with explicit `params.hnsw_ef` 1000, and compare the resulting top-50
document rankings (order and set) across all four.

This decides whether §3.4's over-fetch corrects anything:

- **All identical on the multivector arm** → the arm is exact too, the over-fetch is a literal no-op,
  and asymmetry 2 was never real. The re-run reduces to the index-state correction alone, and the
  NO-GO stands on *stronger* ground than it did — the loss is a property of MaxSim retrieval at this
  scale, not of the budget.
- **They differ** → the arm is approximate, the over-fetch at 65 is a genuine correction, and the
  gated run proceeds as designed.

Either outcome is a reportable finding and goes in the gate amendment. The probe costs ~30 queries
against each collection and is the cheapest decisive measurement in the experiment.

### 3.6 The gated run and scoring

`multivector.py query` over all 300 queries into a fresh run directory, then:

    report.py --run <newdir>/runs --qrels <newdir>/qrels.trec \
      --stats-path <gte-run>/keymap.json.stats.json \
      --baseline <newdir>/runs/gte-chunks-raw.chunks.trec

`--pair` is **not** used: it enforces pool invariance, which a layout change cannot satisfy, and
would declare the multivector arm invalid (parent spec §11 A13). `PERMUTATION_SEED`, resample count
and `HOLM_ALPHA` are left untouched. Holm corrects at m = 2, as in the original gate.

The decision rule is **unchanged** — parent spec §7, all three criteria, as tabulated in §1. Two
consequences worth stating in advance:

- Criterion 3 is the one that passed, and the over-fetch costs the arm latency. At 1.30× the risk is
  much smaller than the 5× alternative would have carried, and there is margin (0.61× against a
  1.25× ceiling) — but the re-run could in principle fix criteria 1-2 and break 3.
- A continued FAIL at matched search effort indicts the **layout**, not the budget. That is a
  stronger and more useful negative than the original gate recorded.

## 4. Run directory

A fresh `~/repositories/iverson-benchmark-corpora/scifact-gte-mvrerun-2026-09-10/`, with `beir/` and
`qrels.trec` copied from `scifact-gte-2026-09-06/` (`cmd_query` reads `<run-dir>/beir/queries.jsonl`
and writes `<run-dir>/runs/`). `scifact-gte-2026-09-06/runs/` is **read-only for this experiment** —
it holds the gate's evidence.

## 5. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A2 | The 3 gte snapshots are present and plausibly complete | `scifact-gte-qdrant-snapshots/`: 98,686,976 / 135,059,968 / 107,074,560 bytes. Integrity is only proven by the restore itself (§7). |
| A3 | No hard-coded collection name survives in the query path | `multivector.py:351-353` — all three are CLI args; `:29` is a default only. |
| A4 | Nothing in run files or scoring depends on collection **name** | `group_rows`/`resolve_parents`/`rank_chunk_hits` key on payload `parent_id`, `key`, `docId` (`multivector.py:45-94`). |
| A5 | ~341 MB of aliases fit | `df`: 839 GB available on `/`. |
| A6 | TEI is templated for model + batch tokens | `docker-compose.yml:180`; the gte requirement is documented in the comment at `:177-179`. |
| A7 | `--no-deps` is required and sufficient | `docker-compose.yml:507` (api) and `:576` (worker) both declare `tei-embed: condition: service_healthy`. |
| A8 | Threshold direction; the gate's "0" is inverted | Live: `indexing_threshold` 10000 with `indexed_vectors_count` 16,673 of 18,622 — the sub-threshold segment is the unindexed one. |
| A11 | Corpus-fraction match is 65 | 250/19,967 = 1.252066%; × 5,183 = 64.8946 → 65; 65/50 = 1.30×. Equal-limit alternative: 250/5,183 = 4.8235%. |
| A12 | `report.py` takes explicit run paths | `report.py:828` `--run` is `action="append"`; `:842` `--baseline`. Accepts a directory or a file. |
| A13 | The three criteria are as stated | Parent spec §7, tabulated in `2026-09-GATE-multivector.md:468-472`. |
| A14 | 300 queries and matching qrels exist | `scifact-gte-2026-09-06/beir/queries.jsonl` 300 lines; `qrels.trec` 339 rows over 300 distinct query ids. |
| A15 | No `--mv-limit` exists today | `multivector.py:323` passes `DOCUMENT_BUDGET` as the multivector `limit`. |
| A16 | The changed logic is unit-testable offline | `test_multivector.py` — 12 tests over the pure functions, no Qdrant, no TEI. |
| A17 | Re-running into the old directory would destroy gate evidence | `scifact-gte-2026-09-06/runs/` holds `gte-chunks-raw.chunks.trec` (639,872 B) and `gte-multivector-raw.chunks.trec` (714,759 B); labels are constants at `multivector.py:35-36`. |
| A18 | Dedupe must precede truncation | `collapse_by_doc` (`multivector.py:74`) collapses then truncates; the `short` check at `:330` compares against `DOCUMENT_BUDGET` after that. |
| A19 | The dev-only Qdrant key and restore loop work | Used successfully for every read in this verification pass. |
| A21 | `ingest.embed` is model-agnostic | `ingest.py:533` — `embed(text, model, document_prefix, embed_url)` posts `{model, input}` to `/v1/embeddings`. |

**A10 — falsified as originally stated, and the design changed in response.** See §2. Verified by
read-only probe: `limit` 50 / 65 / 250 / `hnsw_ef` 1000 against the live chunks collection returned
identical top-50 rankings in all five pairwise comparisons.

### Deferred to execution — self-verifying at the run's first step

| # | Assumption | Where it is checked |
|---|---|---|
| A1 | Qdrant restores a snapshot into the collection named in the URL path, so an alias name works | §3.1 restore; point counts must read 5,183 / 19,967 / 5,183. If this fails the design must fall back to parent spec §6's overwrite-and-restore-back, which is a **plan-shape change requiring re-approval**. |
| A9 | `PATCH optimizers_config` converges `indexed_vectors_count` to `points_count` in bounded time | §3.3 poll. |

## 6. Preconditions

1. **The box is idle.** Criterion 3 is a p95 latency ratio. At the time of writing the box was at
   load average 10.44 with 1.0 GiB free, running a .NET test suite (Testcontainers, Ryuk, several
   `dotnet` processes) that this session did not start. Verify load and free memory, and that no
   `testcontainers-*` container is running, before §3.5 or §3.6.
2. Nothing else runs for the duration of the gated run.
3. `iverson-qdrant` and `iverson-tei-embed` are up; the API and worker are irrelevant to both gated
   arms and are left alone.

## 7. Out of scope

- **Adoption.** Even a clean PASS does not adopt the layout: the parent gate records that MaxSim does
  not report which row won, so `SearchChunks`'s chunk text/index contract would need a local argmax
  over retrieved rows plus chunk texts in the point payload — a second round-trip and payload growth,
  neither measured. That cost belongs to an adoption spec.
- **The long-document question.** This stays a layout verdict at 512/448 on SciFact. At 2048/1792
  SciFact is 81% single-chunk and the arm degenerates.
- **The `gte-chunks-api` arm** and the model observation. Both were context in the original gate, not
  gated criteria; neither is re-run, so the API is never involved.
- Re-deciding gte-modernbert-base as a model (ranked-changes §7, closed).

## 8. Known issues inherited from the parent gate

- `BUILD UNKNOWN` will print for both arms: `multivector.py` writes no build sidecar. Inert for the
  gated pair — both arms come from the same script in the same interleaved loop against the same
  Qdrant.
- The two modes are interleaved per query sharing one embedding, with the multivector query always
  second, so any warm-up effect is charged to one arm. A fully alternating order would remove this
  last ordering confound; it is not addressed here.
