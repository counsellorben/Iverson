# RFDT per-tenant adaptation — design

**Date:** 2026-10-05. **Status:** design, approved section by section by Ben (2026-10-05); not yet reviewed.
**Follows:** the SimpleJEV judge gate (`docs/plans/2026-09-GATE-simple-jev-judge.md`, PASS: nDCG@10 +0.0713,
permutation p 0.0002, on branch `simple-jev-judge`) and its spec `docs/specs/2026-09-29-simple-jev-judge-design.md`.

## 1. Question

Do per-tenant LoRA adapters, distilled from the SimpleJEV 27B judge with SimpleJEV's own training workflow (RFDT),
improve reranking on each tenant's queries, and is the gain **domain-specific** or **general fine-tuning**?

Two corpora stand in for two tenants: **SciFact** (scientific claims) and **FreshStack** (code and technical Q&A).
The contrast is deliberate: if adapters are domain-specific, applying one tenant's adapter to the other's queries
should visibly underperform.

Offline only. No server change. Ben's rulings (2026-10-05): goal = per-tenant adaptation; tenants = SciFact +
FreshStack; budget ≈ **$100** GPU spend for the whole experiment; approach = **RFDT-native** (Option 2);
student base = **Qwen3-Reranker-0.6B**.

## 2. Why one document per request

The gated judge saw all 50 pool documents in every request: SimpleJEV's v1 prompt puts every question's
instructions in the shared prefix. That format cannot carry this experiment:

- **FreshStack does not fit.** Pool text per query: SciFact median 82,891 chars (61,814 median prompt tokens in the
  gate run); FreshStack median 301,236, max 398,415 chars — 3.6× SciFact, far past `--max-model-len 65536`.
- **RFDT cannot train on it.** `RFDT/train.py:101-118` materializes vocabulary logits at every position; a
  60K-token row is ≈ 30 GB of bf16 logits for a 248K-vocabulary student, before backward.

So every judge and student request in this design carries **one document**. A single-question request's prompt
holds only that document (it appears 2–3 times: prefix instructions plus the selected-question suffix). Measured
with the real tokenizers over every (test query, pool document) pair: **≤ 13,081 tokens** (Qwen3.8-27B,
`shared_examples_binary`) and **≤ 12,558** (Qwen3-Reranker-0.6B, `baseline` choice). About 91% of FreshStack and 11%
of SciFact student prompts exceed 2,048 tokens.

Whether the judge's gain survives without the other 49 documents in context is **unmeasured** — Phase 0 measures it.

## 3. Inputs

| Tenant | Pool (top-50 per query) | Queries | qrels | Baseline nDCG@10 | Pool oracle | Headroom | Phase 0 bar (25%) |
|---|---|---|---|---|---|---|---|
| SciFact | `$B/scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` (bge-base, 512/448) | 300 | `qrels.trec` | 0.7452 | 0.9345 | 0.1893 | **+0.047** |
| FreshStack | `$B/freshstack-2048-2026-09-07/runs/fs-2048-l070.chunks.trec` (bge-base, 2048/1792, λ 0.70) | 672 | `qrels.trec` (0/1; 5,445 relevant) | 0.2933 | 0.6755 | 0.3822 | **+0.0956** |

`$B` = `~/repositories/iverson-benchmark-corpora` (not a git repo; never `git add` anything there). The SciFact
pool is the SimpleJEV gate's pool. The FreshStack oracle orders each pool by qrels relevance (ties by pool order).

**Pinned software.** simple-jev commit `7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3` for the server *and* RFDT (RFDT
and `common/` are unchanged between this commit and upstream `9c11582`). Teacher `Qwen/Qwen3.8-27B` revision
`1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0`, `noul`, `--classifier-prompt-policy shared_examples_binary`. Student
base `Qwen/Qwen3-Reranker-0.6B` (`Qwen3ForCausalLM`), revision pinned at first download and recorded.

Artifacts: `$B/rfdt-tenant-2026-10/{phase0,phase1,phase2,gate}`.

## 4. Phase 0 — the judge, one document per request

**Client.** `jev_rerank.py` gains `--per-doc`: for each query it sends one request per pool document, each built by
the existing `build_request` with a single document (`d0`), and assembles the 50 values in pool order. One ledger
entry per query (the existing accepted/rejected tagging, resume, 5% fallback cap and sidecar are unchanged); a
query is accepted only when all 50 per-document requests are accepted. `stub_jev_server.py` gains the matching
single-question mode so the two-sided dry run (identity / reversed), on the SciFact pool, still proves the pipeline
end to end. In
`--per-doc` mode the client sends without the 0.5 s pacing sleep (`MIN_INTERVAL_SECONDS`, the public demo's
2-requests-per-second limit, `jev_rerank.py:46`); the 429 `Retry-After` backoff and the 4-attempt retry are unchanged.
Each accepted per-query ledger entry also records, in its `content` beside `values` and `metadata`, the UTC start of
the query's first request and the summed wall-clock seconds of all its requests (retries included); every pass, and
the smoke test, writes its UTC start and end to its log.

**Server.** `hf_server` serializes every forward under one lock and does not batch across requests
(`hf_server.py:441,481`), so Phase 0 is 48,600 sequential forwards (SciFact 15,000; FreshStack 33,600) plus
the repeat. Launch: `--max-model-len 16384`, `--dtype bfloat16`, advanced metrics on, everything else as the gate.

**Pod.** One 80 GB card (H100 or A100), PyTorch template, `setsid nohup` for every long process. Weights are 55.6 GB
(`total_size`, pinned revision); single-document prompts are ≤ 13.1K tokens.

**Pre-flight (sizes Phase 0 before it starts).** Time 100 end-to-end client requests (wall clock through
`jev_rerank --per-doc`) on a seeded random sample of each tenant's (test query, pool document) pairs, and project
Phase 0's cost from their mean × each pass's request count at the card's hourly price; separately send each
tenant's longest prompts and record peak `nvidia-smi` memory (Ben, CDR-2 §3.1). If the projection
exceeds **$35**, every pass labels only each pool's **top 20** (positions 21–50 keep baseline order) — the gate doc
then reports how many qrels-relevant documents sit at baseline ranks 21–50 (unreachable by construction). If the
top-20 projection still exceeds $35, Phase 0 runs at top 20 anyway and the overrun is charged to Phase 1's $40
(Ben, CDR-1 §3.1); Phase 1's sizing (§5.4) then works from what remains.

**Runs, in order.** (1) SciFact main pass, 300 queries. (2) SciFact repeat on the gate's 50-query subsample
(`--subsample 50 --subsample-seed 20260929`, separate ledger) — confirms per-document determinism. (3) FreshStack
main pass, 672 queries.

**Gate, per tenant.** `report.py --pair <run>=<baseline>`: **PASS iff permutation p < 0.05 AND delta ≥ the tenant's
bar (§3)**, with the gate's existing preconditions (exit 0, R@50 unchanged, fallbacks ≤ 5%). A tenant that fails
stops: no Phase 1 or 2 for it. If both fail, the experiment ends with that verdict.

**Student toolchain smoke test (Ben, CDR-1 §3.2), on the Phase 0 pod before any Phase 1 spend.** Run only if a
tenant passed. With the judge's server stopped: build ~20 §5.5-shaped records from Phase 0's own ledger, using the
longest FreshStack documents; run `RFDT/train.py --lora … --max-length 16384 --max-steps 2`, then `RFDT/export.py`;
serve the merged model with `hf_server --classifier-prompt-policy baseline` and send one per-document `choice`
request. Record peak `nvidia-smi` memory. Any failure stops before Phase 1. The judge's server is then relaunched for
Phase 1's labelling.

**Reported, not gated.** SciFact one-document vs all-50-in-context (the gate's `jev-main` run, +0.0713): how much
of the gain came from cross-document context. This reranker-vs-reranker pair uses §6.4's amended pool check.

## 5. Phase 1 — training data, per passing tenant

**5.1 Synthetic queries.** Tenants have documents but no query log, so queries are written from the tenant's own
documents: sample documents with a seeded RNG, one query per document, de-duplicate, and drop any text equal to one
of the tenant's test queries (`beir/queries.jsonl`: 300 SciFact, 672 FreshStack). Test queries are never used for
training. Adapted from `student-distill`'s `generate_synthetic_queries.py` (already one-query-per-document against an
OpenAI-compatible chat endpoint; its sample size, target and corpus input become parameters). **Generator:**
`Qwen/Qwen3.8-27B` (same pinned revision) under vLLM, **with thinking disabled** (the model thinks by default), on its
own short-lived vLLM pod, so no tenant text leaves self-hosted hardware.

**5.2 Pools.** Each synthetic query's top 50 is rebuilt on the dev box from the saved Qdrant snapshots
(`scifact-bge-base-qdrant-snapshots`, `freshstack-2048-qdrant-snapshots`), reproducing the shipped `SearchChunks`:
- query embedded by bge-base on TEI (`docker-compose.yml` `tei-embed`, :8091) with the query prefix
  `Represent this sentence for searching relevant passages: ` (`EmbeddingPrefixes.cs:37`);
- fetch `topK × 4` chunks (`OverFetchFactor`), `topK = 50 × chunkBudgetMultiplier` (SciFact 5 → 250 of 1,000;
  FreshStack 11 → 550 of 2,200);
- fused score `(0.45·cos(q, chunk) + 0.45·cos(q, parent body_centroid)) / 0.9` (`ResultReranker`,
  `VectorRankingOptions`; no decay or popularity signal on the benchmark schema);
- greedy MMR at **λ 0.70** over the chunks' `body_vector`s (`ResultDiversifier`), selecting `topK`;
- max-passage collapse to 50 documents (`MaxPassageAggregator`).

This generalizes `student-distill`'s `snapshot_pools.py`, which hard-codes nomic/Ollama, 1,000/250 and no MMR.
**Fidelity gate before any Phase 1 spend:** on the tenant's test queries, rebuilt pools must reach **≥ 0.90 mean top-50
overlap** with the shipped run file. The server searched Qdrant's HNSW index, not exactly; if an exact scan misses
the bar, retrieve from the restored snapshots through Qdrant itself, then re-check.

**5.3 Labels.** The judge labels every pool document of every synthetic query with Phase 0's client and settings
(and Phase 0's top-20 rule if it applied).

**5.4 Volume.** Up to **1,000 queries per tenant**, sized from Phase 0's measured wall-clock seconds per request (per
passing tenant: the summed per-entry seconds of that tenant's Phase 0 main-pass ledger ÷ its request count; each
passing tenant's Phase 1 requests are priced at its own rate) so Phase 1 fits **$40** less any Phase 0 overrun (§4). If the projection falls below **300 queries per tenant**, stop and ask Ben (accept fewer, or spend more).

**5.5 RFDT records.** One record per (query, document): `state` = the query; one `choice` question whose
instructions carry the document and whose criteria are the judge's own binary surrogate — `no`/`yes` with the noul
question's false/true criteria (`hf_prompt_policies.py:107-117` turns each `noul` into exactly this choice); target
`{"probabilities": {"yes": p, "no": 1 − p}}` with p the judge's P(yes) (`restore_binary_noul`, `:182-186`);
`group_id` = query id. **Not** `noul` targets: RFDT accepts noul only in [0.01, 0.99] (`RFDT/data.py:139-142`), and
in the gate's ledger 81% of judge scores are below 0.01, the 10th-best is below 0.01 in 71% of queries, and the
clipped top 10 keeps only 5.9 distinct values. Records go through `RFDT/prepare.py` (default 20% validation split by
connected group; every target supplied, so no teacher API is called).

## 6. Phase 2 — students

**6.1 Base.** `Qwen/Qwen3-Reranker-0.6B`, shared by every arm.

**6.2 Training.** One adapter per passing tenant: `RFDT/train.py --lora --lora-rank 16 --learning-rate 0.0001
--epochs 3 --dtype bfloat16 --batch-size 2 --gradient-accumulation 8 --gradient-checkpointing
--max-length 16384` (LoRA on all linear
layers, `train.py:261-268`). The model after the last epoch is kept — no epoch selection. `eval_results.json` is
reported, not gated. Merge with `RFDT/export.py`.

**6.3 Scoring.** `hf_server` serves each student pinned to `--classifier-prompt-policy baseline` (RFDT's training
format). `jev_rerank --per-doc` gains the yes/no `choice` question identical to §5.5's records and ranks each pool by
the student's P(yes) (the choice response's `probabilities.yes`); ties keep baseline order; runs are written with
the existing rank-derived writer and sidecar. Arms per tenant test pool: **S0** (base, no adapter), **own** adapter,
**cross** (the other tenant's adapter). Same pod as Phase 1's labelling, after the judge's server is stopped.

**6.4 Verdicts, per tenant** (Holm over the two gated comparisons, `report.py`'s permutation test). Both gated
pairs compare two rerankers, which can reorder fewer than 25% of queries and so fail `check_pool`'s reorder floor
(`report.py:700,732-766`; one refusal exits the whole invocation). For reranker-vs-reranker pairs, `report.py` keeps
`check_pool`'s pool-set check but replaces the 25% floor with each run's sidecar `fallbackCount` being within the 5%
cap (Ben, CDR-2 §3.2). Pairs against the bge-base baseline keep the floor.
- **Adaptation helps:** own − S0, Holm p < 0.05 and delta > 0.
- **Domain-specific:** own − cross, Holm p < 0.05 and delta > 0. Not significant while adaptation helps ⇒ one
  general adapter would do.
- **Reported:** own − bge-base baseline, and the share of the teacher's gain kept:
  (own − baseline) / (Phase 0 teacher − baseline).

If only one tenant passed Phase 0, only its adapter is trained; it is scored on both test sets, and
"domain-specific" is reported as not applicable.

## 7. Budget and stop rules

| Phase | Ceiling | Stop rule |
|---|---|---|
| 0 | $35 | pre-flight projection > $35 ⇒ top-20 labelling; top-20 projection still > $35 ⇒ run anyway, overrun charged to Phase 1 |
| 1 | $40 (incl. the vLLM generation pod) | projection < 300 queries per tenant ⇒ stop, ask Ben |
| 2 | $25 | — |

Card prices are re-read at rental. Every pod launch and termination time is recorded. Phase 0's spend runs from pod
launch to the end of the student toolchain smoke test; later time on that pod is Phase 1's.

## 8. Failure handling

- Judge 5xx / OOM / network: the existing retry, ledger and 5% fallback cap; a fallback query keeps baseline order.
  A refused run (cap exceeded) stops the phase — no verdict from a partial run.
- Pool fidelity < 0.90 after the Qdrant-search fallback: stop before Phase 1 labelling; no spend.
- RFDT `prepare.py` rejects a record: stop (RFDT never truncates or silently drops).
- `train.py` rejects a row as too long: stop (RFDT never truncates).
- Training OOM: `--batch-size 1`, keeping the effective batch with `--gradient-accumulation 16`.

## 9. Harness changes (repo)

- `jev_rerank.py`: `--per-doc`, without the demo pacing sleep, per-query timing in the ledger; the yes/no `choice` question type for students. **One** request-shape definition
  serves every role (Phase 0 teacher, Phase 1 teacher, RFDT records, S0/own/cross scoring).
- `stub_jev_server.py`: single-question mode for the two-sided dry run.
- `report.py`: for reranker-vs-reranker pairs, the pool-set check plus each run's sidecar `fallbackCount` in place of
  the 25% reorder floor (§6.4).
- A bge-base pool builder (generalized `snapshot_pools.py`, with MMR) and its fidelity check.
- A synthetic-query generator (parameterized `generate_synthetic_queries.py`).
- An RFDT record writer (ledger → `prepare.py` input).

Nothing else in the repo consumes `jev_rerank.py` (only its tests and the stub).

## 10. Out of scope

Server integration and per-tenant serving; the 50-documents-per-request format; a third tenant; the
`student-distill` pipeline (unchanged); listwise or blended scoring.

## 11. Known issues / accepted

- **The FreshStack bar is high.** 25% of a 0.3822 headroom is +0.0956 (vs SciFact's +0.047); the judge may well
  fail it. Accepted with the bar's formula (Ben, Section 1).
- **The per-document gain is unknown** until Phase 0; the gated +0.0713 had all 50 documents in context.
- **The student's base quality under SimpleJEV's prompt is untested.** Qwen3-Reranker-0.6B was tuned on its own
  template; S0 measures where it starts.
- **Pool reconstruction reproduces a shipped pipeline offline** (MMR, HNSW); the 0.90 fidelity gate bounds the
  mismatch but does not remove it.
- **RFDT is new upstream:** its README says NVIDIA execution and live teacher labelling are untested there.
- **The 27B judge's labels on synthetic queries are not ground truth;** the students are only ever judged against qrels on the real test queries.

## 12. Verified assumptions (2026-10-05)

| # | Assumption | Evidence |
|---|---|---|
| V1 | A single-question noul request under `shared_examples_binary` is a {no, yes} choice returning normalized P(yes) | `hf_prompt_policies.py:107-117`, `:182-186` |
| V2 | A single-question prompt holds only that document | rendered with `prepare_policy` on FreshStack's longest docs: one doc, 2–3 occurrences |
| V3 | `/v1/classifier` forwards are serialized, no cross-request batching | `hf_server.py:441,481,934,1075` |
| V4 | The 27B fits an 80 GB card | `model.safetensors.index.json` `total_size` 55,562,855,904 B |
| V5 | Single-doc prompts ≤ 16,384 tokens; the student needs `train.py --max-length 16384` | real tokenizers over all 15,000 + 33,600 test-pool pairs: ≤ 13,081 (Qwen3.8, `shared_examples_binary`), ≤ 12,558 (Qwen3-Reranker, `baseline` choice); `train.py:213` defaults `--max-length` to 2,048 and `hf_server.py:351-354` rejects longer branches |
| V6 | jev_rerank's ledger/fallback/sidecar can wrap a per-doc mode | `score_query` is per query with an injectable `call` (`jev_rerank.py:158-176`) |
| V7 | `report.py`/ir_measures score the FreshStack pool | baseline 0.2933 / R@50 0.5443, oracle 0.6755 computed |
| V8 | FreshStack pool = bge-base, 2048/1792, λ 0.70 | `ingest.log` lines 1–2; `RESTORE.md`; composite `9714c660b365fad1` = tier-1 gate's λ sweep |
| V9 | SciFact pool = bge-base 512/448 | `ingest.log`; `RESTORE.md` |
| V10 | vLLM serves Qwen3.8-27B; thinking is on by default | model card (pinned revision): vLLM listed; "thinking mode by default" |
| V11 | The generator is already document-conditioned | `generate_synthetic_queries.py:77-96` |
| V12 | Snapshots exist for both pools (chunk + object collections) | `scifact-bge-base-qdrant-snapshots/`, `freshstack-2048-qdrant-snapshots/` (payload keys inside unverified) |
| V13 | bge-base queries carry the instruction prefix | `EmbeddingPrefixes.cs:37`; `api-identity.txt` |
| V14 | TEI serves bge-base on the dev box | `docker-compose.yml:178-195`; `tei-info.txt` |
| V15 | Shipped retrieval = over-fetch ×4, 0.45/0.45 fusion, MMR λ 0.70 on chunk vectors, max-passage, HNSW | `ObjectSearchGrpcService.cs:577,611,951`; `ResultReranker`; `ResultDiversifier`; `VectorRankingOptions.cs:27` |
| V16 | RFDT `choice` targets accept probabilities over every candidate | `RFDT/data.py:100-127` |
| V17 | RFDT noul targets clip to [0.01, 0.99] | `RFDT/data.py:139-142`; gate ledger: 81% of scores < 0.01 |
| V18 | RFDT trains under the baseline formatter; serve with `--classifier-prompt-policy baseline` | `train.py:50` (no policy); RFDT README |
| V19 | RFDT loads Qwen3-Reranker-0.6B and applies LoRA | `train.py:36-38` (`AutoModelForCausalLM`), `:261-268` (`all-linear`); config `Qwen3ForCausalLM` |
| V20 | Choice responses expose per-candidate probabilities | `restore_binary_noul` reads `answer['probabilities']['yes']` |
| V21 | RFDT splits keep a query's records together | `prepare.py:100-131` (union of context and `group_id`) |
| V22 | RFDT deps fit hf_server's (Python ≥ 3.12, transformers ≥ 5.16.1, peft ≥ 0.18) | `RFDT/requirements.txt`; `hf-server/pyproject.toml:10-11` |
| V23 | RFDT exists, unchanged, at the pinned commit | `git ls-tree 7bb4f0c7`; `git diff 7bb4f0c7 9c11582 -- RFDT common` empty |
| V24 | Nothing else consumes `jev_rerank.py` | only `stub_jev_server.py`, `test_jev_rerank.py` |
| V25 | Ties and run writing are handled | `jev_rerank.write_run` rank-derived scores, baseline tie-break |
| V26 | Test-query exclusion lists exist | `beir/queries.jsonl`: 300 SciFact, 672 FreshStack |
| V27 | The yes/no choice labels are single-token stable for both tokenizers (no request 422s on the label check) | `hf_server.py:357-371`; probe: on both tokenizers, the label prefix plus `A` / `B` each add exactly one token (ids 32, 33), with the prefix unchanged |
| V28 | The client's 0.5 s pacing sleep is demo-only and removable without breaking the 429 backoff/retry | `jev_rerank.py:46,176-177,201-203`; run with `MIN_INTERVAL_SECONDS` 0 → 429 then success: order returned, sleeps `[0, 1.0, 0]` |
| V29 | Phase 1 prompts (synthetic query × any corpus document) stay ≤ 16,384 tokens | all 6,000 FreshStack corpus documents with a worst-case 300-char query: judge max 13,519, student max 12,908 (smoke records max 12,575) |
| V30 | The stub recovers each document's pool position from a one-document request on the SciFact dry-run pool | all 300 SciFact pools: no two documents identical or contained in one another; FreshStack fails this (20 of 672 pools hold identical texts), so the dry run uses SciFact |
| V31 | A seeded random sample represents Phase 0's per-request work; the longest-document basis does not | judge prompt tokens per request: longest-document mean 1.66× (SciFact) and 1.25× (FreshStack) the population mean; a seeded 100-pair sample mean 0.98× and 1.03× |
| V32 | Sidecars carry the fallback count the amended pair check reads; ledgers carry no timing today | `jev-main.meta.json` `reranker.fallbackCount` (`jev_rerank.py:237-238`); gate ledger keys `completion_tokens, content, order, pass, prompt_tokens, query_id, reason, reasoning, status`, `content` = `metadata, values` |
| V33 | The pre-flight's random sample runs through `jev_rerank --per-doc` as a sub-pool (1–2 documents per query) | run: a seeded 100-pair sample as a TREC sub-pool through `load_run` → `validate_inputs_before_any_model_call` → `score_query` (stub reply) → `write_run`: 100 rows, 0 fallbacks |
| V34 | Adding timing keys beside `values`/`metadata` in an accepted entry's `content` breaks no ledger reader | the only reader of a jev ledger's `content` is `test_jev_rerank.py:159` (reads `["values"]`); the gate's metadata check reads `["metadata"]` |
| V35 | Sidecars distinguish reranker runs from baselines | `bge-base.meta.json` and `fs-2048-l070.meta.json` carry `"reranker": null`; jev_rerank sidecars carry a `reranker` block with `fallbackCount` (`jev-main.meta.json`) |
