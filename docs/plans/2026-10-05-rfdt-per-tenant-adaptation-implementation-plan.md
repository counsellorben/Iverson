# RFDT Per-Tenant Adaptation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Tasks 7–9 spend money on rented GPUs: Ben runs them himself from the runbook text; a subagent never rents anything.

**Source spec:** `docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md` (commit SHA: `67ed4ba4`)

**Goal:** Measure whether per-tenant LoRA students (Qwen3-Reranker-0.6B), distilled with RFDT from the SimpleJEV 27B judge asked one document per request, improve reranking on SciFact and FreshStack, and whether the gain is domain-specific.

**Architecture:** Offline only. A per-document mode in the existing `jev_rerank.py` client drives the judge (Phase 0 gate, Phase 1 labels) and the students (Phase 2 scoring) through one request builder; `tenant_pools.py` rebuilds bge-base pools for synthetic queries from the saved Qdrant snapshots; `synth_queries.py` writes the queries; `rfdt_records.py` turns judge ledgers into RFDT training records; `report.py` gains a reranker-vs-reranker pair rule. Everything is scored with the existing `report.py`.

**Tech stack:** Python 3 stdlib harness scripts (numpy + ir_measures from `$B/python-libs`), pytest; SimpleJEV `hf_server` + RFDT at `7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3`; `Qwen/Qwen3.8-27B` @ `1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0`; `Qwen/Qwen3-Reranker-0.6B`; vLLM (generation only); local Qdrant `v1.18.2` + TEI `cpu-1.8` under podman; RunPod.

---

## Global Constraints

- Pinned: simple-jev `7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3` (server and RFDT); teacher `Qwen/Qwen3.8-27B` revision `1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0`, `noul`, `--classifier-prompt-policy shared_examples_binary`; student base `Qwen/Qwen3-Reranker-0.6B`, revision pinned at first download and recorded.
- Every judge and student request carries **one document** (`--per-doc`). Single-document prompts are ≤ 13,081 tokens (teacher) / ≤ 12,558 (student): serve with `--max-model-len 16384`, train with `--max-length 16384`.
- Phase 0 gate per tenant: **PASS iff permutation p < 0.05 AND delta ≥ the tenant's bar** — SciFact **+0.047**, FreshStack **+0.0956** — with exit 0, R@50 unchanged, fallbacks ≤ 5%.
- Phase 2 verdicts per passing tenant, Holm over the two gated pairs: own − S0 and own − cross, each Holm p < 0.05 and delta > 0.
- Budget ≈ $100: Phase 0 $35 (pre-flight > $35 ⇒ top-20; top-20 still > $35 ⇒ run anyway, overrun charged to Phase 1), Phase 1 $40 (incl. the generation pod; < 300 queries per tenant ⇒ stop, ask Ben), Phase 2 $25. Phase 0's spend runs from pod launch to the end of the smoke test.
- Never `git add` anything under `~/repositories/iverson-benchmark-corpora`. Artifacts live in `$A = $B/rfdt-tenant-2026-10`.
- Every long pod process runs under `setsid nohup`; every pass log carries its UTC start and end.
- Commits end with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Never push or merge without asking.

## File Structure

All paths under `Iverson.Server/Iverson.LoadTest/scripts/` unless stated.

- **Modify** `jev_rerank.py` — `--per-doc` (one request per document, per-document attempts, no demo pacing sleep, per-query timing in the ledger), `--label-top N`, `--question-type choice` (the judge's yes/no surrogate, ranked by P(yes)); `pass_identity` and the sidecar record `perDoc`/`labelTop`.
- **Modify** `test_jev_rerank.py` — tests for all of the above.
- **Modify** `stub_jev_server.py` — single-question mode (noul and choice) for the two-sided dry run.
- **Modify** `report.py` — reranker-vs-reranker pairs: pool-set check plus each run's sidecar `fallbackCount` in place of the 25% reorder floor; `load_sidecar` / `sidecar_reranker` helpers.
- **Modify** `test_report.py` — tests for the pair rule.
- **Modify** `teacher_rerank.py` — `call_teacher(..., extra_body=None)`, merged into the request body.
- **Modify** `test_teacher_rerank.py` — tests for `extra_body`.
- **Create** `tenant_pools.py` — `dump` / `pools` / `fidelity`: the shipped bge-base SearchChunks rebuilt offline (TEI embedding, ×4 over-fetch, 0.45/0.45 fusion, MMR λ 0.70, max-passage).
- **Create** `test_tenant_pools.py`.
- **Create** `synth_queries.py` — one synthetic query per sampled tenant document, test queries excluded, thinking disabled.
- **Create** `test_synth_queries.py`.
- **Create** `rfdt_records.py` — judge ledger → RFDT `choice` records with P(yes) targets.
- **Create** `test_rfdt_records.py`.
- **Create** `docs/plans/2026-10-GATE-rfdt-per-tenant-adaptation.md` (Task 9); **Modify** `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` (one row, Task 9).

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and three critical-design-review rounds at spec-write time and are NOT re-verified here. Trusted as ground truth (spec §12, verbatim):

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

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-10-05). The code in Tasks 1–6 was proven in a scratch copy of the branch: the diffs and files below are byte-identical to that copy.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | Branch `rfdt-tenant` and `.worktrees/rfdt-tenant` do not exist; `simple-jev-judge` tip is `2b0a2e62` | `git branch --list rfdt-tenant` empty; `ls .worktrees/rfdt-tenant` → no such file; `git rev-parse --short simple-jev-judge` = `2b0a2e62` |
| P2 | File path | `tenant_pools.py`, `synth_queries.py`, `rfdt_records.py` and their tests are new | `git cat-file -e simple-jev-judge:<path>` fails for all six |
| P3 | File path | The gate doc is new; the ranked-changes doc's last measurement row is the SimpleJEV row (line 51) | `git cat-file -e` fails for the gate doc; `git show simple-jev-judge:docs/2026-09-06-…md` line 51 is `\| SimpleJEV relevance judge …`, line 52 blank |
| P4 | File path | `$B/rfdt-tenant-2026-10` does not exist | `ls` → no such file |
| P5 | File path | Each tenant's snapshot dir holds one chunks + one objects snapshot; the restore loop is `scifact-512-qdrant-snapshots/RESTORE.md`'s; both restore to the same un-fingerprinted collection names, so tenants are restored one at a time | `ls` of both dirs; RESTORE.md read; collection names `benchmark_documents_tenant_bypass`, `benchmark_documents_chunks_tenant_bypass` |
| P6 | Signature | `teacher_rerank` helpers the new code calls exist with the used signatures (`load_run`, `load_corpus`, `load_queries`, `select_subsample`, `accepted_entry`, `make_record`, `append_response`, `read_responses_ledger`, `validate_ledger_pass`, `validate_inputs_before_any_model_call`, `sidecar_path_for`, `call_teacher`, `RETRY_BUDGET`) | branch `teacher_rerank.py:75,140,349,508,523` and the 213 passing tests below |
| P7 | Signature | `check_pool` has two callers: `report.run_pair_statistics` (`report.py:820` after the change) and `rrf_fuse.classify_inert` (`rrf_fuse.py:284`); rrf_fuse's baselines carry `"reranker": null`, so the floor still applies to them | `command grep -rn "check_pool"`; `test_rrf_fuse.py` 27/27 after the change |
| P8 | Signature | hf_server's choice answer is `{type, confidence, probabilities{no,yes}, choice}` (plus `calibrated, margin, ties, scoring` with advanced metrics); a `choice` request is not rewritten by the binary-noul restore | `common/response_scoring.py:66-74,283-309`; `hf_server.py:935-946` |
| P9 | Signature | `stub_vllm_server.load_queries_by_text` exists (the stub reuses it) | `stub_jev_server.py` imports it today |
| P10 | Signature | RFDT `train.py` flags used exist: `--model --revision --train --validation --output --lora --lora-rank --dtype --max-length --batch-size --gradient-accumulation --gradient-checkpointing --epochs --max-steps --learning-rate`; `export.py`: `--model --revision --adapter --output` | `RFDT/train.py:191-224`, `RFDT/export.py:17-22` at `7bb4f0c7` |
| P11 | Signature | `RFDT/prepare.py --input --output [--validation-fraction 0.2 --seed 42]` writes `<output>/train.jsonl` and `<output>/validation.jsonl`; needs ≥ 2 groups | `prepare.py:140-147,224-232`, `:126-128` |
| P12 | Signature | `rfdt_records.py` output passes RFDT's own validation | all 15,000 records from the gate ledger: `data.validate_record` + `normalized_targets` 15,000 / 0 rejected; `prepare.py` exit 0 → 12,000 train / 3,000 validation, 240/60 queries, none on both sides |
| P13 | Command | Tests run as `python3 -m pytest -q <files>` from the scripts dir with `PYTHONPATH=$B/python-libs` | scratch run: 213 passed (jev_rerank 69, report 29, teacher_rerank 46, rrf_fuse 27, tenant_pools 23, synth_queries 12, rfdt_records 7) |
| P14 | Command | `report.py --run … --qrels … --pair RUN=BASE` exits 1 on a refused pair and 0 otherwise; prints `[pool]` then `[compare]` blocks | dry runs below (identity exit 1, reversed exit 0) |
| P15 | Command | Local podman has `docker.io/qdrant/qdrant:v1.18.2` (the version that wrote the snapshots) and `ghcr.io/huggingface/text-embeddings-inference:cpu-1.8`; TEI's route is `POST /v1/embeddings {model, input}`; Qdrant needs `api-key` from `QDRANT__SERVICE__API_KEY` | `docker images`; `docker-compose.yml:115-117,178-195`; `EmbeddingService.cs:99-147` |
| P16 | Command | `runpodctl` is on the dev box (`~/.local/bin/runpodctl`) and on RunPod pods | `command -v runpodctl`; used in the SimpleJEV gate runbook |
| P17 | Command | Qwen3.8 under vLLM disables thinking with `chat_template_kwargs: {"enable_thinking": false}` | model card at the pinned revision, README:459 |
| P18 | Command | hf_server flags used exist: `--model --revision --served-model-name --enforce-model-id --classifier-prompt-policy --max-model-len --dtype --max-batch-size --host --port` | `hf_server.py:1289-1330` |
| P19 | Ordering | T2 needs T1 (flags); T6 needs T1 (`build_request(..., "choice", ...)`); T3, T4, T5 are independent; T7 needs T1, T2, T3 (reported pair), T6 (smoke test); T8 needs T4, T5, T6; T9 needs T3 | imports checked in the scratch copy |
| P20 | Ordering | Task 4's fidelity gate (dev box, free) runs before any spend | it needs only the snapshots, TEI and the shipped run files |
| P21 | Code | Task 1: 69 tests pass (33 existing unchanged), 50/50 mutants killed | scratch |
| P22 | Code | Task 2 dry runs (SciFact pool, 15,000 requests each): per-doc noul and choice identity 0.7452 / exit 1 / md5 `3e46051e2386578c6664e8840c46eaf2`, reversed 0.0011 / exit 0 / md5 `c7c7a298c9dfa13cff930dde7e0b21da`; `--label-top 20` identity md5 `3e46051e…`, reversed 0.0215 / exit 0 / md5 `0c0946cb0d81e164e3ef1b578a8b5565` | scratch; label-top-20 choice reversed re-run by the controller: identical md5, delta −0.7237 |
| P23 | Code | Task 3: test_report 29 pass, 24/24 mutants killed; a 0%-reordered reranker pair exits 0 naming the rule | scratch |
| P24 | Code | `tenant_pools.py` reproduces the C# semantics: fetch ×4 (`ObjectSearchGrpcService.cs:577,951`), Qdrant default search params (`IntelligenceVectorService.cs:138-152`), fusion (`ResultReranker.cs:21-67`, `VectorRankingOptions.cs:83-96`), MMR over chunk vectors fed fused-descending, strict `>` ties (`ResultDiversifier.cs:30-107`, `ObjectSearchGrpcService.cs:587-611`), float32 Score (`:623`), collapse by max chunk score with first-appearance ties (`DocumentRanking.cs:30-61`); 23 tests, 32/32 mutants | scratch + C# read |
| P25 | Code | Task 5: synth_queries 12 tests (19/19 mutants), call_teacher `extra_body` 3/3 mutants; existing teacher tests unchanged | scratch |
| P26 | Code | Task 6: rfdt_records 7 tests, 17/17 mutants; records validate (P12) | scratch |
| P27 | Code | numpy is in `$B/python-libs` | tenant_pools tests import it |
| P28 | Consumer | `call_teacher`'s only production caller is `teacher_rerank.score_query` (`:443`), which passes no `extra_body` — its body is unchanged (a new test pins it) | `command grep -rn call_teacher` on the branch and main |
| P29 | Consumer | The `check_pool` change leaves every pair whose sidecar is missing or `reranker: null` on the 25% floor | P7; tests in Task 3 |
| P30 | Consumer | `pass_identity` now always carries `perDoc`/`labelTop`, so a ledger from the old client (the SimpleJEV gate's) no longer resumes — refused loudly before any request; that gate is finished | scratch test |
| P31 | Consumer | New sidecar keys (`perDoc`, `labelTop`) break no reader: report.py reads `composite` and (now) `reranker.fallbackCount` | `command grep` of sidecar readers |
| P32 | Sibling set | Every CLI flag used in Tasks 7–9's commands exists in the code that defines it | checked against the argparse blocks of `jev_rerank.py`, `tenant_pools.py`, `synth_queries.py`, `rfdt_records.py`, `report.py`, RFDT, hf_server |
| P33 | Sibling set | Every name a block reads is set in that block, in the prelude, or (pod shells) in its task's recovery block; Task 4 Step 7 and Task 8 Step 4 blocks `source $A/restore.sh` for the Qdrant key and `restore()` | CIR-1 §2.1: before the fix the FreshStack block failed in a fresh shell (exit 127); after: a fresh `env -i bash` with `source $A/restore.sh` prints the key and `restore is a function`, then fails only on the absent Qdrant (curl exit 7) |
| P34 | Code | `synth_queries.py` sends `max_tokens: 512` with `enable_thinking: false` — `call_teacher`'s default `MAX_COMPLETION_TOKENS` is 32,768, which with a ~13.5K-token FreshStack prompt would exceed a 16,384 `--max-model-len` | `teacher_rerank.py:45`; `extra_body` merges with `body.update` (overrides) |
| P35 | Command | Qdrant v1.18.2's REST surface has `/collections/{c}/snapshots/upload`, `/points/scroll` (`with_vector`) and `/points/search` (deprecated but served; `SearchRequest` takes `vector, limit, with_payload, with_vector`) | v1.18.2 OpenAPI fetched (CIR-1 T4f); snapshot names all `<collection>-6802952876034638-<date>.snapshot` |
| P36 | Signature | Every chunk `parent_id` in both snapshots is an object `key`, so `load_index` resolves every chunk | byte scan of all payload files: every scanned `parent_id` and `key` lies in the run's keymap key set (5,183 / 6,000); the 20 per tenant the scan missed are all keymap keys (CIR-1 T4g, R15) |
| P37 | Command | The `vllm-openai` image's start command reaches `vllm serve` as `--model …` | `ENTRYPOINT ["vllm","serve"]` (vLLM `docker/Dockerfile:1292`); `serve` falls back to `--model` with no `model_tag` (`vllm/entrypoints/cli/serve.py:56-57`) (CIR-1 T8a) |
| P38 | Ordering | The Task 7 pod bundle is import-complete: `jev_rerank`, `rfdt_records`, `teacher_rerank` import from the bundle dir with no third-party module | `python3 -I` import from the bundle dir; `pre-sample` 100 rows (84 / 97 queries), `pre-longest` 5 rows; the tar count check passes (CIR-1 T7c) |

## Tasks

Every dev-box shell for Tasks 1–6 starts with this prelude (cwd does not persist between shells):

```bash
R=/home/ben/repositories/Iverson/.worktrees/rfdt-tenant
S=$R/Iverson.Server/Iverson.LoadTest/scripts
B=/home/ben/repositories/iverson-benchmark-corpora
SF=$B/scifact-bge-base-2026-09-04
FS=$B/freshstack-2048-2026-09-07
A=$B/rfdt-tenant-2026-10
export PYTHONPATH=$B/python-libs
```

### Task 1: Per-document client (`jev_rerank.py`)

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py`

**Interfaces:**
- Produces: `--per-doc`, `--label-top N`, `--question-type choice`; `build_request(model, "choice", query, [text])` (used by Task 6); per-doc ledger `content` keys `values, metadata, startedAtUtc, requestSeconds, requests`; sidecar `reranker.perDoc`, `reranker.labelTop`.

- [ ] **Step 1: Create the worktree** (the controller may already have done this; the command is idempotent-safe to check first)

```bash
cd /home/ben/repositories/Iverson && git worktree list | command grep -q rfdt-tenant || git worktree add .worktrees/rfdt-tenant -b rfdt-tenant simple-jev-judge
git -C /home/ben/repositories/Iverson/.worktrees/rfdt-tenant log --oneline -1   # expect 2b0a2e62
```

- [ ] **Step 2: Apply the test diff** — save it as `/tmp/t1-tests.diff` and run `git -C $R apply /tmp/t1-tests.diff`

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py
@@ -5,13 +5,17 @@
 Covers everything before a real server: the §4 request shape and acceptance rule, the stable
 tie-break, the run writer, every §5 transport class (429 honours Retry-After, 4xx falls back at
 once, 403 aborts, 5xx/network/rejected bodies retried), pacing, resume, per-pass ledger isolation,
-the fallback cap, the sidecar's provenance, and call_jev against a real local HTTP server.
+the fallback cap, the sidecar's provenance, and call_jev against a real local HTTP server. Then the
+RFDT spec's (docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md) one-document-per-request
+mode: --per-doc, its per-document attempts, timing and missing pacing sleep, --label-top, and the
+yes/no `choice` question.
 
 No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
 import http.server
 import json
 import math
 import os
+import re
 import sys
 import threading
 
@@ -53,27 +57,48 @@
 
 
 class Scripted:
-    """Stands in for call_jev: returns the scripted replies in order and records every body."""
+    """Stands in for call_jev: returns the scripted replies in order and records every body. Given a
+    Clock, each call advances it by `cost` seconds -- the call's wall-clock duration."""
 
-    def __init__(self, *replies):
+    def __init__(self, *replies, clock=None, cost=0.0):
         self.replies = list(replies)
         self.bodies = []
+        self.clock, self.cost = clock, cost
 
     def __call__(self, base_url, body):
         self.bodies.append(body)
+        if self.clock is not None:
+            self.clock.now += self.cost
         return self.replies.pop(0)
 
 
+class Clock:
+    """A fake monotonic clock whose `sleep` advances it, so a test can tell call time from sleeps."""
+
+    def __init__(self):
+        self.now, self.sleeps = 100.0, []
+
+    def __call__(self):
+        return self.now
+
+    def sleep(self, seconds):
+        self.sleeps.append(seconds)
+        self.now += seconds
+
+
 POOL3 = {"q1": ["z", "y", "x"]}
 
 
-def run_one(tmp_path, *replies, pool=POOL3, extra=()):
-    """score_query for q1 with scripted replies; returns (order, fell_back, call, sleeps, ledger lines)."""
+def run_one(tmp_path, *replies, pool=POOL3, extra=(), cost=0.0):
+    """score_query for q1 with scripted replies, each call costing `cost` seconds on a fake clock;
+    returns (order, fell_back, call, sleeps, ledger lines)."""
     paths = make_inputs(tmp_path, pool)
     args = jev.build_arg_parser().parse_args(cli(tmp_path, paths, *extra))
     corpus = jev.tr.load_corpus(paths[1])
-    call, sleeps = Scripted(*replies), []
-    order, fell_back = jev.score_query("q1", pool["q1"], "query q1", corpus, args, {}, call=call, sleep=sleeps.append)
+    clock = Clock()
+    call, sleeps = Scripted(*replies, clock=clock, cost=cost), clock.sleeps
+    order, fell_back = jev.score_query("q1", pool["q1"], "query q1", corpus, args, {}, call=call,
+                                       sleep=clock.sleep, clock=clock)
     ledger = [json.loads(l) for l in open(args.responses)] if os.path.exists(args.responses) else []
     return order, fell_back, call, sleeps, ledger
 
@@ -307,3 +332,224 @@
 def test_retry_delay_defaults_to_one_second():
     assert (jev.retry_delay("2.5"), jev.retry_delay(None), jev.retry_delay("Wed, 21 Oct")) == (2.5, 1.0, 1.0)
     assert not math.isnan(jev.retry_delay("0"))
+
+
+# ── --per-doc: one document per request (RFDT spec §4) ──────────────────────────────────────
+
+PER_DOC = ("--per-doc",)
+
+
+def one(value, kind="noul", input_tokens=10, metadata=None):
+    """A single-question (d0) reply, as hf_server answers a one-document request."""
+    reply = answers([value], kind, input_tokens)
+    if metadata is not None:
+        reply["metadata"] = metadata
+    return "ok", reply, None
+
+
+def choice(yes, input_tokens=10):
+    """hf_server's public choice answer (common/response_scoring.py _assemble, advanced off)."""
+    p = {"no": 1 - yes, "yes": yes}
+    return "ok", {"answers": {"d0": {"type": "choice", "confidence": max(p.values()), "probabilities": p,
+                                     "choice": "yes" if yes > 0.5 else "no"}},
+                  "usage": {"input_tokens": input_tokens, "output_tokens": 0}}, None
+
+
+def test_per_doc_sends_one_build_request_body_per_pool_document_in_pool_order(tmp_path):
+    order, fell_back, call, _, ledger = run_one(tmp_path, one(0.1), one(0.9), one(0.5), extra=PER_DOC)
+    assert (order, fell_back) == (["y", "x", "z"], False)
+    assert call.bodies == [jev.build_request("m", "noul", "query q1", [f"T{d}. body of {d}"]) for d in "zyx"]
+    assert [r["status"] for r in ledger] == ["accepted"] and ledger[0]["order"] == ["y", "x", "z"]
+
+
+def test_per_doc_accepted_entry_records_values_first_metadata_timing_and_summed_tokens(tmp_path):
+    _, _, _, _, ledger = run_one(tmp_path, one(0.1, input_tokens=7, metadata={"template_version": "a"}),
+                                 one(0.9, input_tokens=8, metadata={"template_version": "b"}),
+                                 one(0.5, input_tokens=9), extra=PER_DOC, cost=1.5)
+    content = json.loads(ledger[0]["content"])
+    assert list(content) == ["values", "metadata", "startedAtUtc", "requestSeconds", "requests"]
+    assert content["values"] == [0.1, 0.9, 0.5] and content["metadata"] == {"template_version": "a"}
+    assert re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", content["startedAtUtc"])
+    assert (content["requestSeconds"], content["requests"]) == (4.5, 3)
+    assert ledger[0]["prompt_tokens"] == 24
+
+
+def test_per_doc_prompt_tokens_are_none_when_any_document_reports_none(tmp_path):
+    _, _, _, _, ledger = run_one(tmp_path, one(0.1), one(0.9, input_tokens=None), one(0.5), extra=PER_DOC)
+    assert ledger[0]["status"] == "accepted" and ledger[0]["prompt_tokens"] is None
+
+
+def test_per_doc_request_seconds_include_retries_but_not_429_sleeps_and_there_is_no_pacing_sleep(tmp_path):
+    _, fell_back, call, sleeps, ledger = run_one(
+        tmp_path, ("http", (429, "busy"), "7"), one(0.1), ("http", (500, "boom"), None), one(0.9), one(0.5),
+        extra=PER_DOC, cost=2.0)
+    content = json.loads(ledger[-1]["content"])
+    assert fell_back is False and len(call.bodies) == 5 and sleeps == [7.0]
+    assert (content["requestSeconds"], content["requests"]) == (10.0, 5)
+
+
+def test_per_doc_each_document_gets_its_own_four_attempts(tmp_path):
+    boom = ("http", (500, "boom"), None)
+    order, fell_back, call, _, _ = run_one(
+        tmp_path, boom, boom, boom, one(0.1), boom, boom, boom, one(0.9), one(0.5), extra=PER_DOC)
+    assert (order, fell_back) == (["y", "x", "z"], False) and len(call.bodies) == 9
+
+
+def test_per_doc_first_exhausted_document_falls_back_and_later_documents_are_not_sent(tmp_path):
+    order, fell_back, call, _, ledger = run_one(
+        tmp_path, one(0.1), *[("http", (500, "boom"), None)] * 4, extra=PER_DOC)
+    assert (order, fell_back) == (["z", "y", "x"], True)
+    assert len(call.bodies) == 5 and call.bodies[-1] == jev.build_request("m", "noul", "query q1", ["Ty. body of y"])
+    assert [(r["status"], r["reason"]) for r in ledger] == [("rejected", "d1: HTTP 500")] * 4
+
+
+@pytest.mark.parametrize("code", [400, 404, 422])
+def test_per_doc_a_4xx_ends_the_query_at_once(tmp_path, code):
+    order, fell_back, call, _, ledger = run_one(tmp_path, one(0.1), ("http", (code, "no"), None), extra=PER_DOC)
+    assert (order, fell_back) == (["z", "y", "x"], True)
+    assert len(call.bodies) == 2 and [r["reason"] for r in ledger] == [f"d1: HTTP {code}"]
+
+
+def test_per_doc_403_aborts_the_whole_invocation(tmp_path):
+    with pytest.raises(SystemExit):
+        run_one(tmp_path, one(0.1), ("http", (403, "error code: 1010"), None), extra=PER_DOC)
+
+
+def test_per_doc_rejected_bodies_and_network_errors_are_retried_with_prefixed_reasons(tmp_path):
+    order, fell_back, call, _, ledger = run_one(
+        tmp_path, one(0.1), one(0.9), ("network", "timed out", None), one(2.0), one(0.5), extra=PER_DOC)
+    assert (order, fell_back) == (["y", "x", "z"], False) and len(call.bodies) == 5
+    assert [r["reason"] for r in ledger[:2]] == ["d2: network error", "d2: d0: noul 2.0 outside [0.0, 1.0]"]
+
+
+def test_per_doc_resumes_an_accepted_entry_without_a_request(tmp_path):
+    paths = make_inputs(tmp_path, POOL3)
+    args = jev.build_arg_parser().parse_args(cli(tmp_path, paths, *PER_DOC))
+    ledger = {"q1": [{"query_id": "q1", "status": "accepted", "order": ["x", "z", "y"]}]}
+    call = Scripted()
+    assert jev.score_query("q1", POOL3["q1"], "query q1", {}, args, ledger, call=call,
+                           sleep=lambda s: None) == (["x", "z", "y"], False)
+    assert call.bodies == []
+
+
+
+def test_request_values_seconds_exclude_the_pacing_sleep(tmp_path):
+    """request_values's contract in both modes: seconds are the calls' own wall clock only."""
+    paths = make_inputs(tmp_path, POOL3)
+    args = jev.build_arg_parser().parse_args(cli(tmp_path, paths))
+    clock = Clock()
+    call = Scripted(("http", (500, "boom"), None), one(0.5), clock=clock, cost=1.0)
+    values, _, seconds, requests = jev.request_values("q1", {}, 1, args, {}, call, clock.sleep, clock, pace=True)
+    assert (values, seconds, requests, clock.sleeps) == ([0.5], 2.0, 2, [0.5, 0.5])
+
+POOL5 = {"q1": ["e", "d", "c", "b", "a"]}
+
+
+def test_label_top_judges_only_the_first_n_and_keeps_the_rest_in_baseline_order(tmp_path):
+    order, fell_back, call, _, ledger = run_one(
+        tmp_path, one(0.2), one(0.3), one(0.9), pool=POOL5, extra=(*PER_DOC, "--label-top", "3"))
+    assert (order, fell_back) == (["c", "d", "e", "b", "a"], False)
+    assert len(call.bodies) == 3 and "Tc. body of c" in call.bodies[2]["questions"]["d0"]["instructions"]
+    assert json.loads(ledger[0]["content"])["values"] == [0.2, 0.3, 0.9]
+
+
+def test_label_top_ties_keep_baseline_order(tmp_path):
+    order, _, _, _, _ = run_one(tmp_path, one(0.5), one(0.5), pool=POOL5, extra=(*PER_DOC, "--label-top", "2"))
+    assert order == ["e", "d", "c", "b", "a"]
+
+
+def test_label_top_above_the_pool_size_judges_the_whole_pool(tmp_path):
+    order, _, call, _, _ = run_one(tmp_path, one(0.1), one(0.9), one(0.5), extra=(*PER_DOC, "--label-top", "50"))
+    assert order == ["y", "x", "z"] and len(call.bodies) == 3
+
+
+@pytest.mark.parametrize("extra", [("--label-top", "3"), (*PER_DOC, "--label-top", "0")])
+def test_main_refuses_label_top_without_per_doc_or_below_one(tmp_path, monkeypatch, extra):
+    paths = make_inputs(tmp_path, POOL3)
+    monkeypatch.setattr(jev, "call_jev", Scripted())
+    with pytest.raises(SystemExit) as e:
+        jev.main(cli(tmp_path, paths, *extra))
+    assert "--label-top" in str(e.value)
+
+
+def test_main_per_doc_writes_the_run_without_sleeping(tmp_path, monkeypatch):
+    paths = make_inputs(tmp_path, POOL3)
+    slept = []
+    monkeypatch.setattr(jev.time, "sleep", slept.append)
+    monkeypatch.setattr(jev, "call_jev", Scripted(one(0.1), one(0.9), one(0.5)))
+    jev.main(cli(tmp_path, paths, *PER_DOC))
+    assert (tmp_path / "out.chunks.trec").read_text().split("\n")[0] == "q1 Q0 y 1 50.000000 simple-jev"
+    assert slept == []
+
+
+# ── the yes/no `choice` question (RFDT spec §5.5, §6.3) ─────────────────────────────────────
+
+def test_choice_question_is_the_binary_surrogate_of_the_noul_question():
+    """hf_prompt_policies.py:107-117 (pinned 7bb4f0c7): a noul question becomes a choice with the same
+    instructions and criteria {"no": false, "yes": true}, in that order (insertion order assigns A, B)."""
+    noul = jev.build_request("m", "noul", "cats", ["t"])["questions"]["d0"]
+    q = jev.build_request("m", "choice", "cats", ["t"])["questions"]["d0"]
+    assert q == {"type": "choice", "instructions": noul["instructions"],
+                 "criteria": {"no": noul["criteria"]["false"], "yes": noul["criteria"]["true"]}}
+    assert list(q["criteria"]) == ["no", "yes"]
+
+
+def test_wording_hash_is_unchanged_from_the_gate():
+    """The choice question adds no wording (it is built from the noul text), so the hash recorded by
+    the gate's jev-main.meta.json still identifies this wording."""
+    assert jev.WORDING_SHA256 == "924234c1956491ad5f42425648cb87ace1a280f462572ae57ea59d3f542c5eda"
+
+
+def test_judge_values_reads_a_choice_answers_yes_probability():
+    assert jev.judge_values(choice(0.25)[1], "choice", 1) == ([0.25], None)
+
+
+@pytest.mark.parametrize("answer", [
+    {"type": "choice", "choice": "yes"},
+    {"type": "choice", "probabilities": [0.1, 0.9]},
+    {"type": "choice", "probabilities": {"no": 0.9}},
+    {"type": "choice", "probabilities": {"no": -0.5, "yes": 1.5}},
+    {"type": "choice", "probabilities": {"no": 0.0, "yes": float("nan")}},
+    {"type": "choice", "probabilities": {"no": 0.0, "yes": True}},
+    {"type": "choice", "probabilities": {"no": 0.0, "yes": -0.01}},
+    "yes",
+])
+def test_judge_values_rejects_a_choice_answer_without_a_valid_yes_probability(answer):
+    values, reason = jev.judge_values({"answers": {"d0": answer}}, "choice", 1)
+    assert values is None and reason.startswith("d0: ")
+
+
+def test_per_doc_choice_ranks_by_yes_probability(tmp_path):
+    order, fell_back, call, _, ledger = run_one(
+        tmp_path, choice(0.1), choice(0.9), choice(0.5), extra=(*PER_DOC, "--question-type", "choice"))
+    assert (order, fell_back) == (["y", "x", "z"], False)
+    assert call.bodies[0]["questions"]["d0"]["type"] == "choice"
+    assert json.loads(ledger[0]["content"])["values"] == [0.1, 0.9, 0.5]
+
+
+# ── pass identity and sidecar carry the per-doc settings ────────────────────────────────────
+
+@pytest.mark.parametrize("first,second", [((), PER_DOC), (PER_DOC, ()),
+                                          (PER_DOC, (*PER_DOC, "--label-top", "2")),
+                                          ((*PER_DOC, "--label-top", "3"), (*PER_DOC, "--label-top", "2"))])
+def test_main_refuses_a_ledger_recorded_with_other_per_doc_settings(tmp_path, monkeypatch, first, second):
+    paths = make_inputs(tmp_path, POOL3)
+    monkeypatch.setattr(jev.time, "sleep", lambda s: None)
+    monkeypatch.setattr(jev, "call_jev", Scripted(*[("http", (500, "boom"), None)] * 12))
+    with pytest.raises(SystemExit):  # one query, cap 0: falls back, but its rejected attempts are ledgered
+        jev.main(cli(tmp_path, paths, *first))
+    with pytest.raises(SystemExit) as e:
+        jev.main(cli(tmp_path, paths, *second))
+    assert "DIFFERENT" in str(e.value)
+
+
+def test_sidecar_records_per_doc_and_label_top(tmp_path, monkeypatch):
+    paths = make_inputs(tmp_path, POOL3)
+    monkeypatch.setattr(jev, "call_jev", Scripted(one(0.1), one(0.9), one(0.5), ("ok", answers([0.1, 0.9, 0.5]), None)))
+    jev.main(cli(tmp_path, paths, *PER_DOC, "--label-top", "2"))
+    r = json.loads((tmp_path / "out.meta.json").read_text())["reranker"]
+    assert (r["perDoc"], r["labelTop"]) == (True, 2)
+    monkeypatch.setattr(jev.time, "sleep", lambda s: None)
+    jev.main(cli(tmp_path, paths, responses="ledger2.jsonl"))
+    r = json.loads((tmp_path / "out.meta.json").read_text())["reranker"]
+    assert (r["perDoc"], r["labelTop"]) == (False, None)
```

- [ ] **Step 3: Run the tests and see the new ones fail**

```bash
cd $S && python3 -m pytest -q test_jev_rerank.py 2>&1 | tail -3
```

Expected: failures in the new per-doc / label-top / choice tests (the 33 existing tests pass).

- [ ] **Step 4: Apply the source diff** — save it as `/tmp/t1-src.diff`, `git -C $R apply /tmp/t1-src.diff`

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py
@@ -12,6 +12,13 @@
 its attempts falls back to baseline order and is counted; above floor(0.05 * n) fallbacks no run
 file is written, and the ledger is kept so a rerun resumes.
 
+`--per-doc` (spec docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md §4) sends one request
+per pool document instead -- the same build_request body with a single question `d0` -- without the
+demo's pacing sleep; each document has its own attempts, and the first document that cannot be
+judged sends its query to the fallback. `--label-top N` judges only each pool's first N documents
+(the rest keep baseline order). `--question-type choice` is the yes/no surrogate hf_server builds
+from a noul question (RFDT spec §5.5, §6.3); a student is ranked by its P(yes).
+
 Stdlib only. Loaders, subsample, ledger, input and pass checks are reused from teacher_rerank.py
 (their refusal messages are prefixed `[teacher_rerank]`).
 
@@ -46,7 +53,9 @@
 MIN_INTERVAL_SECONDS = 0.5  # spec §5: the demo is rate-limited to 2 requests per second
 TIMEOUT_SECONDS = 300       # spec §5
 
-# The frozen judge wording (spec §4). WORDING_SHA256 is recorded in every sidecar.
+# The frozen judge wording (spec §4). WORDING_SHA256 is recorded in every sidecar. The `choice`
+# question adds no wording -- it is built from the noul text (build_question) -- so the hash is the
+# gate's, and the sidecar's questionType tells a choice run from a noul one.
 STATE_TEMPLATE = "Search query: {query}"
 INSTRUCTIONS = {
     "noul": "Is the following document relevant to the search query?\n\nDocument:\n{text}",
@@ -64,38 +73,54 @@
         "Directly answers or bears on the query",
     ],
 }
-VALUE_RANGE = {"noul": (0.0, 1.0), "score": (0.0, 3.0)}
+VALUE_RANGE = {"noul": (0.0, 1.0), "score": (0.0, 3.0), "choice": (0.0, 1.0)}
 WORDING_SHA256 = hashlib.sha256(
     json.dumps({"state": STATE_TEMPLATE, "instructions": INSTRUCTIONS, "criteria": CRITERIA},
                sort_keys=True).encode("utf-8")
 ).hexdigest()
 
 
+def build_question(question_type, text):
+    """One document's question. `choice` is exactly the surrogate hf_server's binary policies build
+    from a noul question (simple-jev 7bb4f0c7 hf_prompt_policies.py:107-117): the noul instructions,
+    and criteria `no` = the noul's false, `yes` = its true, in that order (insertion order assigns
+    the model labels A, B) -- the RFDT records' question (RFDT spec §5.5)."""
+    if question_type == "choice":
+        return {"type": "choice", "instructions": INSTRUCTIONS["noul"].format(text=text),
+                "criteria": {"no": CRITERIA["noul"]["false"], "yes": CRITERIA["noul"]["true"]}}
+    return {"type": question_type, "instructions": INSTRUCTIONS[question_type].format(text=text),
+            "criteria": CRITERIA[question_type]}
+
+
 def build_request(model, question_type, query_text, doc_texts):
     """The spec §4 body: the query as the shared state and one question per pool document, keyed
     d0..d{n-1} in baseline order."""
-    questions = {
-        f"d{i}": {
-            "type": question_type,
-            "instructions": INSTRUCTIONS[question_type].format(text=text),
-            "criteria": CRITERIA[question_type],
-        }
-        for i, text in enumerate(doc_texts)
-    }
+    questions = {f"d{i}": build_question(question_type, text) for i, text in enumerate(doc_texts)}
     return {"model": model, "state": STATE_TEMPLATE.format(query=query_text), "questions": questions}
 
 
+def answer_value(answer, question_type):
+    """The judged value inside one answer, unvalidated: a choice answer's `probabilities.yes` (the
+    shape common/response_scoring.py:283-309 returns, which restore_binary_noul reads), otherwise
+    the field named after the question type. None when the answer does not have it."""
+    if not isinstance(answer, dict):
+        return None
+    if question_type == "choice":
+        probabilities = answer.get("probabilities")
+        return probabilities.get("yes") if isinstance(probabilities, dict) else None
+    return answer.get(question_type)
+
+
 def judge_values(response, question_type, n):
     """Spec §4 acceptance: every d<i> present, each value a finite number inside its type's range
-    (noul [0, 1], score [0, 3]). Returns (values, None) or (None, reason)."""
+    (noul [0, 1], score [0, 3], choice P(yes) [0, 1]). Returns (values, None) or (None, reason)."""
     answers = response.get("answers") if isinstance(response, dict) else None
     if not isinstance(answers, dict):
         return None, "response has no answers object"
     low, high = VALUE_RANGE[question_type]
     values = []
     for i in range(n):
-        answer = answers.get(f"d{i}")
-        value = answer.get(question_type) if isinstance(answer, dict) else None
+        value = answer_value(answers.get(f"d{i}"), question_type)
         if isinstance(value, bool) or not isinstance(value, (int, float)):
             return None, f"d{i}: no numeric {question_type} value"
         if not math.isfinite(value) or not low <= value <= high:
@@ -152,49 +177,47 @@
         "questionType": args.question_type,
         "subsample": args.subsample,
         "subsampleSeed": args.subsample_seed,
+        "perDoc": args.per_doc,
+        "labelTop": args.label_top,
     }
 
 
-def score_query(query_id, pool_ids, query_text, corpus, args, ledger, call=None, sleep=None):
-    """Returns (order, fell_back). An accepted ledger entry is reused without a request (resume).
-    Otherwise up to MAX_ATTEMPTS requests, each appended to the ledger (spec §5): 403 aborts the
-    whole invocation; 429 sleeps Retry-After and retries; 400, 422 and every other 4xx fall back at
-    once; 5xx, network errors and rejected bodies are retried. Fallback is baseline order."""
-    call = call or call_jev
-    sleep = sleep or time.sleep
-    pass_id = pass_identity(args)
-    accepted = tr.accepted_entry(ledger.get(str(query_id), []))
-    if accepted is not None:
-        order = accepted.get("order") or []
-        if sorted(order) != sorted(pool_ids):
-            sys.exit(f"[jev_rerank] resumed ledger entry for query {query_id!r} is not a "
-                     "permutation of its pool")
-        return order, False
+def utc_stamp():
+    """Now, in UTC, as the sidecar's `recordedAtUtc` and the ledger's `startedAtUtc` record it."""
+    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
 
-    body = build_request(args.model, args.question_type, query_text,
-                         [corpus[doc_id][1] for doc_id in pool_ids])
+
+def request_values(query_id, body, n, args, pass_id, call, sleep, clock, pace, label=""):
+    """Sends one body under the spec §5 transport rules: up to MAX_ATTEMPTS requests, each rejected
+    one appended to the ledger with its reason prefixed by `label`. 403 aborts the whole invocation;
+    429 sleeps Retry-After and retries; 400, 422 and every other 4xx stop at once; 5xx, network
+    errors and rejected bodies are retried. `pace` sleeps MIN_INTERVAL_SECONDS before every request
+    (the demo's limit; off in --per-doc mode, RFDT spec §4).
+
+    Returns (values, payload, seconds, requests): values None when no attempt was accepted; seconds
+    sums each request's own wall clock on `clock`, so neither sleep is counted."""
+    seconds, requests = 0.0, 0
     for _attempt in range(MAX_ATTEMPTS):
-        sleep(MIN_INTERVAL_SECONDS)
+        if pace:
+            sleep(MIN_INTERVAL_SECONDS)
+        started = clock()
         kind, payload, retry_after = call(args.base_url, body)
+        seconds += clock() - started
+        requests += 1
         if kind == "ok":
-            values, reason = judge_values(payload, args.question_type, len(pool_ids))
+            values, reason = judge_values(payload, args.question_type, n)
             if values is not None:
-                order = order_by_judge(pool_ids, values)
-                content = json.dumps({"values": values, "metadata": payload.get("metadata")})
-                input_tokens = (payload.get("usage") or {}).get("input_tokens")
-                tr.append_response(args.responses, tr.make_record(
-                    query_id, "accepted", content, order, None, pass_id, prompt_tokens=input_tokens))
-                return order, False
+                return values, payload, seconds, requests
             tr.append_response(args.responses, tr.make_record(
-                query_id, "rejected", json.dumps(payload)[:2000], None, reason, pass_id))
+                query_id, "rejected", json.dumps(payload)[:2000], None, label + reason, pass_id))
             continue
         if kind == "network":
             tr.append_response(args.responses, tr.make_record(
-                query_id, "rejected", payload, None, "network error", pass_id))
+                query_id, "rejected", payload, None, label + "network error", pass_id))
             continue
         code, text = payload
         tr.append_response(args.responses, tr.make_record(
-            query_id, "rejected", text, None, f"HTTP {code}", pass_id))
+            query_id, "rejected", text, None, f"{label}HTTP {code}", pass_id))
         if code == 403:
             sys.exit(f"[jev_rerank] HTTP 403 from {args.base_url} -- a configuration error, not a "
                      f"per-query failure (spec §5); aborting. Body: {text[:300]}")
@@ -202,8 +225,75 @@
             sleep(retry_delay(retry_after))
             continue
         if 400 <= code < 500:
-            break  # 400, 422 and every other 4xx: not retryable, fall back at once
-    return list(pool_ids), True
+            break  # 400, 422 and every other 4xx: not retryable, stop at once
+    return None, None, seconds, requests
+
+
+def score_query(query_id, pool_ids, query_text, corpus, args, ledger, call=None, sleep=None, clock=None):
+    """Returns (order, fell_back). An accepted ledger entry is reused without a request (resume).
+    Otherwise the query is judged by request_values -- one request for the whole pool, or with
+    --per-doc one per document (score_per_doc) -- and an accepted result is appended to the ledger.
+    Fallback is baseline order."""
+    call = call or call_jev
+    sleep = sleep or time.sleep
+    clock = clock or time.monotonic
+    pass_id = pass_identity(args)
+    accepted = tr.accepted_entry(ledger.get(str(query_id), []))
+    if accepted is not None:
+        order = accepted.get("order") or []
+        if sorted(order) != sorted(pool_ids):
+            sys.exit(f"[jev_rerank] resumed ledger entry for query {query_id!r} is not a "
+                     "permutation of its pool")
+        return order, False
+    if args.per_doc:
+        return score_per_doc(query_id, pool_ids, query_text, corpus, args, pass_id, call, sleep, clock)
+
+    body = build_request(args.model, args.question_type, query_text,
+                         [corpus[doc_id][1] for doc_id in pool_ids])
+    values, payload, _, _ = request_values(query_id, body, len(pool_ids), args, pass_id, call, sleep,
+                                           clock, pace=True)
+    if values is None:
+        return list(pool_ids), True
+    order = order_by_judge(pool_ids, values)
+    content = json.dumps({"values": values, "metadata": payload.get("metadata")})
+    input_tokens = (payload.get("usage") or {}).get("input_tokens")
+    tr.append_response(args.responses, tr.make_record(
+        query_id, "accepted", content, order, None, pass_id, prompt_tokens=input_tokens))
+    return order, False
+
+
+def score_per_doc(query_id, pool_ids, query_text, corpus, args, pass_id, call, sleep, clock):
+    """RFDT spec §4: one build_request body per judged document (question `d0`), each with its own
+    MAX_ATTEMPTS and no pacing sleep; rejected attempts are ledgered with reasons prefixed `d<i>: `.
+    The first document not accepted ends the query in baseline order, and the documents after it
+    are never sent. --label-top N judges the first N documents only: they are re-sorted by value
+    (ties in baseline order), and every later document keeps its baseline place after them.
+
+    The one accepted entry's content holds the values, the first response's metadata, the UTC start
+    of the query's first request, the summed seconds of every request (retries included, sleeps
+    not) and the request count; prompt_tokens sums usage.input_tokens, None when any reply lacks it."""
+    judged = pool_ids[:args.label_top]  # None judges the whole pool
+    started_at = utc_stamp()
+    values, metadata, seconds, requests, prompt_tokens = [], None, 0.0, 0, 0
+    for i, doc_id in enumerate(judged):
+        body = build_request(args.model, args.question_type, query_text, [corpus[doc_id][1]])
+        doc_values, payload, doc_seconds, doc_requests = request_values(
+            query_id, body, 1, args, pass_id, call, sleep, clock, pace=False, label=f"d{i}: ")
+        seconds += doc_seconds
+        requests += doc_requests
+        if doc_values is None:
+            return list(pool_ids), True
+        if i == 0:
+            metadata = payload.get("metadata")
+        values.append(doc_values[0])
+        input_tokens = (payload.get("usage") or {}).get("input_tokens")
+        prompt_tokens = None if prompt_tokens is None or input_tokens is None else prompt_tokens + input_tokens
+    order = order_by_judge(judged, values) + list(pool_ids[len(judged):])
+    content = json.dumps({"values": values, "metadata": metadata, "startedAtUtc": started_at,
+                          "requestSeconds": seconds, "requests": requests})
+    tr.append_response(args.responses, tr.make_record(
+        query_id, "accepted", content, order, None, pass_id, prompt_tokens=prompt_tokens))
+    return order, False
 
 
 def write_run(path, scored_pool, pool_query_order):
@@ -220,7 +310,7 @@
     sidecar = {
         "configLabel": RUN_TAG,
         "composite": args.composite,
-        "recordedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
+        "recordedAtUtc": utc_stamp(),
         "reranker": {
             "baseUrl": args.base_url,
             "modelId": args.model,
@@ -234,6 +324,8 @@
             "preflightInputTokens": args.preflight_input_tokens,
             "subsample": args.subsample,
             "subsampleSeed": args.subsample_seed,
+            "perDoc": args.per_doc,
+            "labelTop": args.label_top,
             "fallbackCount": len(fallback_ids),
             "fallbackQueryIds": fallback_ids,
         },
@@ -250,12 +342,17 @@
     ap.add_argument("--queries", required=True, help="BEIR queries.jsonl (keys: _id, text)")
     ap.add_argument("--base-url", required=True, help="server root; requests go to {base-url}/v1/classifier")
     ap.add_argument("--model", required=True, help="served model id sent in every request")
-    ap.add_argument("--question-type", choices=["noul", "score"], default="noul")
+    ap.add_argument("--question-type", choices=["noul", "score", "choice"], default="noul",
+                    help="choice: the yes/no surrogate of noul, ranked by P(yes) (RFDT spec §6.3)")
     ap.add_argument("--responses", required=True, help="JSONL ledger for this pass (appended to; resumable)")
     ap.add_argument("--out", required=True, help="run file; must end <label>.chunks.trec so report.py finds the sidecar")
     ap.add_argument("--composite", required=True, help="the pool's own build composite, from its .meta.json")
     ap.add_argument("--subsample", type=int, default=None, help="if given (with --subsample-seed), judge only N query ids")
     ap.add_argument("--subsample-seed", type=int, default=None, help="seed for --subsample's selection")
+    ap.add_argument("--per-doc", action="store_true",
+                    help="one request per pool document, without the pacing sleep (RFDT spec §4)")
+    ap.add_argument("--label-top", type=int, default=None,
+                    help="with --per-doc: judge only each pool's first N documents; the rest keep baseline order")
     provenance = "recorded verbatim in the sidecar's reranker block; never sent to the server; omit to record null"
     ap.add_argument("--server-commit", default=None, help=provenance)
     ap.add_argument("--revision", default=None, help=provenance)
@@ -270,6 +367,8 @@
     args = build_arg_parser().parse_args(argv)
     if (args.subsample is None) != (args.subsample_seed is None):
         sys.exit("--subsample and --subsample-seed must be given together")
+    if args.label_top is not None and (not args.per_doc or args.label_top < 1):
+        sys.exit("--label-top needs --per-doc and N >= 1")
 
     pool = tr.load_run(args.pool)
     corpus = tr.load_corpus(args.corpus)
```

- [ ] **Step 5: Run the tests**

```bash
cd $S && python3 -m pytest -q test_jev_rerank.py test_teacher_rerank.py 2>&1 | tail -1
```

Expected: `113 passed` (69 + 44).

- [ ] **Step 6: Commit**

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py
git -C $R commit -m "add the per-document, label-top and choice modes to the SimpleJEV client

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Single-question stub and the two-sided dry run

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py`

**Interfaces:**
- Consumes: Task 1's `--per-doc`, `--label-top`, `--question-type choice`.

- [ ] **Step 1: Apply the stub diff** — `/tmp/t2-stub.diff`, `git -C $R apply /tmp/t2-stub.diff`

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py
@@ -14,6 +14,14 @@
 (the teacher-ceiling defect where prompts carried a bare doc id while every test stayed green), when
 the question ids do not match the pool, or for any non-noul question.
 
+Single-question mode (jev_rerank.py --per-doc, spec docs/specs/2026-10-05-rfdt-per-tenant-adaptation-
+design.md §4): a request whose only question is `d0` names its document by text alone, so the stub
+finds the ONE pool position i whose corpus text the instructions contain (none or several: 422) and
+answers with that position's value. A noul question gets `{"type": "noul", "noul": v}`; a yes/no
+choice question (criteria keys exactly `no`, `yes`) gets hf_server's public choice answer with
+probabilities {no: 1 - v, yes: v}. Only SciFact pools are safe here: no SciFact pool holds two
+identical documents or one inside another, while 20 of FreshStack's 672 pools do (RFDT spec V30).
+
 Stdlib only. Run with:
 
     python3 Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py \\
@@ -37,6 +45,36 @@
 STATE_PREFIX = "Search query: "  # jev_rerank.STATE_TEMPLATE's fixed prefix
 
 
+def position_value(i, n, mode):
+    """The value for pool position i of n: 1 - i/n reproduces the baseline order, i/n reverses it."""
+    return 1 - i / n if mode == "identity" else i / n
+
+
+def choice_answer(yes):
+    """hf_server's public answer to a {no, yes} choice (simple-jev 7bb4f0c7
+    common/response_scoring.py:283-309, filtered to PUBLIC_FIELDS :66-74): argmax keeps the first
+    label on an exact tie, so `no` wins at 0.5."""
+    probabilities = {"no": 1 - yes, "yes": yes}
+    winner = "yes" if yes > probabilities["no"] else "no"
+    return {"type": "choice", "confidence": probabilities[winner], "probabilities": probabilities,
+            "choice": winner}
+
+
+def single_answer(question, doc_ids, corpus, mode):
+    """(status, answer-or-error) for a one-document request's question `d0`."""
+    instructions = question.get("instructions") or ""
+    positions = [i for i, doc_id in enumerate(doc_ids) if corpus[doc_id][1] in instructions]
+    if len(positions) != 1:
+        return 422, {"detail": f"d0: instructions hold the text of {len(positions)} pool documents, "
+                               "not exactly one"}
+    value = position_value(positions[0], len(doc_ids), mode)
+    if question.get("type") == "noul":
+        return 200, {"type": "noul", "noul": value}
+    if question.get("type") == "choice" and list(question.get("criteria") or {}) == ["no", "yes"]:
+        return 200, choice_answer(value)
+    return 422, {"detail": "d0: this stub answers noul and {no, yes} choice questions only"}
+
+
 def reply(body, pool, corpus, queries_by_text, mode):
     """Returns (status, payload) for one /v1/classifier body."""
     state = body.get("state")
@@ -48,6 +86,12 @@
     doc_ids = pool[query_id]
     n = len(doc_ids)
     questions = body.get("questions") or {}
+    usage = {"input_tokens": 0, "output_tokens": 0}
+    if list(questions) == ["d0"]:
+        status, answer = single_answer(questions["d0"], doc_ids, corpus, mode)
+        if status != 200:
+            return status, answer
+        return 200, {"model": body.get("model"), "answers": {"d0": answer}, "usage": usage}
     if sorted(questions) != sorted(f"d{i}" for i in range(n)):
         return 422, {"detail": f"question ids do not match query {query_id}'s {n}-document pool"}
     result = {}
@@ -57,10 +101,8 @@
             return 422, {"detail": f"d{i}: this stub answers noul questions only"}
         if corpus[doc_id][1] not in (question.get("instructions") or ""):
             return 422, {"detail": f"d{i}: instructions lack the text of pool document {doc_id}"}
-        value = 1 - i / n if mode == "identity" else i / n
-        result[f"d{i}"] = {"type": "noul", "noul": value}
-    return 200, {"model": body.get("model"), "answers": result,
-                 "usage": {"input_tokens": 0, "output_tokens": 0}}
+        result[f"d{i}"] = {"type": "noul", "noul": position_value(i, n, mode)}
+    return 200, {"model": body.get("model"), "answers": result, "usage": usage}
 
 
 def make_handler(pool, corpus, queries_by_text, mode):
```

- [ ] **Step 2: Run the six dry runs** (SciFact pool only: FreshStack pools hold identical-text documents the stub cannot place, spec V30). Each run sends 15,000 local requests (6,000 for label-top) and takes about 20 s.

```bash
D=$A/dryrun; mkdir -p $D
for arm in "noul:--question-type noul" "choice:--question-type choice" "top20:--question-type noul --label-top 20"; do
  name=${arm%%:*}; flags=${arm#*:}
  for mode in identity reversed; do
    python3 $S/stub_jev_server.py --pool $SF/runs/bge-base.chunks.trec --corpus $SF/beir/corpus.jsonl \
      --queries $SF/beir/queries.jsonl --mode $mode > $D/port.txt & SPID=$!
    until [ -s $D/port.txt ]; do sleep 0.2; done; PORT=$(head -1 $D/port.txt)
    python3 $S/jev_rerank.py --pool $SF/runs/bge-base.chunks.trec --corpus $SF/beir/corpus.jsonl \
      --queries $SF/beir/queries.jsonl --base-url http://127.0.0.1:$PORT --model stub --per-doc $flags \
      --responses $D/$name-$mode.responses.jsonl --out $D/$name-$mode.chunks.trec --composite 7d3a15092f963723
    kill $SPID; rm -f $D/port.txt
    python3 $S/report.py --run $D/$name-$mode.chunks.trec --run $SF/runs/bge-base.chunks.trec --qrels $SF/qrels.trec \
      --pair $D/$name-$mode.chunks.trec=$SF/runs/bge-base.chunks.trec > $D/$name-$mode.report.txt 2>&1
    echo "$name $mode exit $?"
  done
done
md5sum $D/*.chunks.trec
command grep -h -A1 "\[compare\].*(nDCG@10)" $D/*-reversed.report.txt
```

Expected (exact): every identity run `exit 1` (the reorder floor refuses an unchanged ordering), md5 `3e46051e2386578c6664e8840c46eaf2`, nDCG@10 0.7452; `noul-reversed` and `choice-reversed` `exit 0`, md5 `c7c7a298c9dfa13cff930dde7e0b21da`, delta −0.7442 (nDCG@10 0.0011); `top20-reversed` `exit 0`, md5 `0c0946cb0d81e164e3ef1b578a8b5565`, delta −0.7237 (nDCG@10 0.0215). Each ledger has 300 accepted entries carrying `startedAtUtc`, `requestSeconds`, `requests` (50, or 20 for top20).

- [ ] **Step 3: Commit** (the dry-run artifacts stay in `$A`, never committed)

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py
git -C $R commit -m "teach the SimpleJEV stub single-question noul and choice requests

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: Reranker-vs-reranker pairs in `report.py`

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/report.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_report.py`

- [ ] **Step 1: Apply the test diff** (`/tmp/t3-tests.diff`) and run `cd $S && python3 -m pytest -q test_report.py 2>&1 | tail -3` — the new tests fail.

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/test_report.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_report.py
@@ -5,6 +5,7 @@
 
 report.py imports ir_measures lazily inside functions, so importing it here needs no PYTHONPATH;
 running the paired section does."""
+import json
 import os
 import re
 import sys
@@ -169,6 +170,95 @@
     assert "0 of 4" in str(e.value) and "25" in str(e.value)
 
 
+
+# ── check_pool's reranker-vs-reranker rule (RFDT spec §6.4) ─────────────────────────────────
+
+def write_sidecar(run_path, reranker, composite="c"):
+    """The run's <label>.meta.json, at report.py's own path; `reranker` as jev_rerank (a block) or a
+    baseline (None) records it."""
+    with open(report.sidecar_path_for(str(run_path)), "w", encoding="utf-8") as f:
+        json.dump({"composite": composite, "reranker": reranker}, f)
+
+
+def reranker_runs(tmp_path, run_fallbacks, base_fallbacks, n_run=20, n_base=20):
+    """Two byte-for-byte identical rankings (0% reordered) over n queries each, both with a
+    reranker sidecar block; a fallbacks value of ... omits fallbackCount."""
+    rows = [(f"q{i}", ["d1", "d2", "d3"]) for i in range(max(n_run, n_base))]
+    a0, a1 = tmp_path / "a0.chunks.trec", tmp_path / "a1.chunks.trec"
+    write_run(a0, rows[:n_base], "a0")
+    write_run(a1, rows[:n_run], "a1")
+    for path, count in ((a0, base_fallbacks), (a1, run_fallbacks)):
+        write_sidecar(path, {"modelId": "m"} if count is ... else {"modelId": "m", "fallbackCount": count})
+    return str(a1), str(a0)
+
+
+
+def test_load_build_composite_reads_the_sidecar_and_tolerates_a_missing_or_corrupt_one(tmp_path):
+    run = tmp_path / "a0.chunks.trec"
+    assert report.load_build_composite(str(run)) is None
+    write_sidecar(run, None, composite="7d3a15092f963723")
+    assert report.load_build_composite(str(run)) == "7d3a15092f963723"
+    (tmp_path / "a0.meta.json").write_text('{"composite": ')
+    assert report.load_build_composite(str(run)) is None
+
+def test_reranker_pair_skips_the_reorder_floor_within_the_fallback_cap(tmp_path, capsys):
+    report.check_pool(*reranker_runs(tmp_path, 1, 0))  # 20 queries: cap floor(0.05 * 20) = 1
+    out = capsys.readouterr().out
+    assert "sequence differs on 0 (0.0%)" in out and "reranker vs reranker" in out, out
+
+
+@pytest.mark.parametrize("run_fallbacks,base_fallbacks", [(2, 0), (0, 2)])
+def test_reranker_pair_over_the_fallback_cap_is_refused(tmp_path, run_fallbacks, base_fallbacks):
+    with pytest.raises(SystemExit) as e:
+        report.check_pool(*reranker_runs(tmp_path, run_fallbacks, base_fallbacks))
+    assert "fallbackCount 2" in str(e.value) and "cap 1" in str(e.value)
+
+
+def test_reranker_pair_caps_each_run_by_its_own_query_count(tmp_path):
+    # The baseline has 40 queries (cap 2), the run 20 (cap 1): 2 fallbacks pass for the baseline only.
+    report.check_pool(*reranker_runs(tmp_path, 1, 2, n_run=20, n_base=40))
+    with pytest.raises(SystemExit):
+        report.check_pool(*reranker_runs(tmp_path, 2, 1, n_run=20, n_base=40))
+
+
+@pytest.mark.parametrize("bad", [..., None, "0", 0.0, True, -1])
+def test_reranker_pair_without_an_integer_fallback_count_is_refused(tmp_path, bad):
+    with pytest.raises(SystemExit) as e:
+        report.check_pool(*reranker_runs(tmp_path, bad, 0))
+    assert "fallbackCount" in str(e.value)
+
+
+@pytest.mark.parametrize("baseline_sidecar", ["null", "missing", "corrupt"])
+def test_a_pair_against_a_non_reranker_baseline_keeps_the_reorder_floor(tmp_path, capsys, baseline_sidecar):
+    run, base = reranker_runs(tmp_path, 0, 0)
+    if baseline_sidecar == "null":
+        write_sidecar(base, None)  # the bge-base baselines carry "reranker": null
+    elif baseline_sidecar == "missing":
+        os.remove(report.sidecar_path_for(base))
+    else:
+        with open(report.sidecar_path_for(base), "w") as f:
+            f.write('{"reranker": {')
+    with pytest.raises(SystemExit) as e:
+        report.check_pool(run, base)
+    assert "0 of 20" in str(e.value) and "25%" in str(e.value)
+    assert "reorder floor" in capsys.readouterr().out
+
+
+def test_a_reranker_sidecar_on_the_baseline_side_only_keeps_the_reorder_floor(tmp_path):
+    run, base = reranker_runs(tmp_path, 0, 0)
+    write_sidecar(run, None)
+    with pytest.raises(SystemExit) as e:
+        report.check_pool(run, base)
+    assert "25%" in str(e.value)
+
+
+def test_reranker_pair_still_refuses_a_changed_document_set(tmp_path):
+    run, base = reranker_runs(tmp_path, 0, 0)
+    write_run(run, [(f"q{i}", ["d1", "d2", "d9" if i == 0 else "d3"]) for i in range(20)], "a1")
+    with pytest.raises(SystemExit) as e:
+        report.check_pool(run, base)
+    assert "pool changed" in str(e.value)
+
 def test_pair_and_baseline_are_mutually_exclusive(tmp_path, qrels_path, monkeypatch):
     a0 = tmp_path / "a0.chunks.trec"
     write_run(a0, BASE, "a0")
```

- [ ] **Step 2: Apply the source diff** (`/tmp/t3-src.diff`)

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/report.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/report.py
@@ -191,15 +191,29 @@
     the same as a missing one, not raised: this is called after scoring has completed, so an
     uncaught JSONDecodeError here would discard the scoring work rather than just leave the
     build column unknown."""
+    data = load_sidecar(run_path)
+    return None if data is None else data.get("composite")
+
+
+def load_sidecar(run_path):
+    """The run's parsed sidecar, or None when it does not exist or is not valid JSON (the
+    tolerance load_build_composite documents)."""
     sidecar = sidecar_path_for(run_path)
     if not os.path.exists(sidecar):
         return None
     with open(sidecar, encoding="utf-8") as f:
         try:
-            data = json.load(f)
+            return json.load(f)
         except json.JSONDecodeError:
             return None
-    return data.get("composite")
+
+
+def sidecar_reranker(run_path):
+    """The sidecar's `reranker` block when it is a JSON object (jev_rerank.py writes one), else
+    None: a baseline records "reranker": null, and a missing or unreadable sidecar is unknown."""
+    data = load_sidecar(run_path)
+    reranker = data.get("reranker") if isinstance(data, dict) else None
+    return reranker if isinstance(reranker, dict) else None
 
 
 def diversity_sidecar_for(run_path):
@@ -698,6 +712,7 @@
 # ── Step 4b: declared-pair statistics (--pair RUN=BASELINE) ────────────────────────────
 
 POOL_MIN_REORDERED_FRACTION = 0.25   # spec §3.3: differs-from-control, per-query ranked doc-id sequence
+POOL_MAX_FALLBACK_PERCENT = 5        # RFDT spec §6.4: jev_rerank's own cap, floor(0.05 * queries)
 
 
 def parse_pairs(values):
@@ -735,7 +750,15 @@
     changed and the arm is invalid -- and (2) the ranked SEQUENCE must differ for at least
     POOL_MIN_REORDERED_FRACTION of the queries, or the reranker did not run (a silent fallback
     would otherwise score as a null result and read as 'reranking does not help'). Both fail
-    loud via sys.exit; nothing downstream may run on an invalid arm. Prints one [pool] line."""
+    loud via sys.exit; nothing downstream may run on an invalid arm. Prints one [pool] line,
+    naming the rule applied.
+
+    A reranker-vs-reranker pair -- BOTH sidecars carry a `reranker` object, not null -- may
+    legitimately reorder few queries (two rerankers can agree), so (2) is replaced by each run's
+    own sidecar fallbackCount being an integer within floor(5%) of that run's query count
+    (RFDT spec §6.4): a reranker that did not run shows up as fallbacks there. Every other pair,
+    including one with a missing sidecar, keeps (2)."""
+    reranker_pair = all(sidecar_reranker(p) is not None for p in (run_path, baseline_path))
     run_seq = ranked_doc_ids(run_path)
     base_seq = ranked_doc_ids(baseline_path)
     common = sorted(set(run_seq) & set(base_seq))
@@ -748,7 +771,9 @@
     print(
         f"[pool] {os.path.basename(run_path)}  vs  {os.path.basename(baseline_path)}: "
         f"{len(common):,} queries, set changed on {len(set_changed):,}, "
-        f"sequence differs on {len(reordered):,} ({fraction * 100:.1f}%)"
+        f"sequence differs on {len(reordered):,} ({fraction * 100:.1f}%); rule: "
+        + (f"reranker vs reranker (each run's fallbackCount <= {POOL_MAX_FALLBACK_PERCENT}% of its queries)"
+           if reranker_pair else f"reorder floor {POOL_MIN_REORDERED_FRACTION * 100:.0f}%")
     )
     if set_changed:
         sys.exit(
@@ -757,6 +782,17 @@
             f"(first: {set_changed[0]}). Reranking cannot change which documents are in the pool "
             "(spec §7.1.2); this arm was produced by a different pool and must not be scored."
         )
+    if reranker_pair:
+        for path, seq in ((run_path, run_seq), (baseline_path, base_seq)):
+            count = sidecar_reranker(path).get("fallbackCount")
+            cap = len(seq) * POOL_MAX_FALLBACK_PERCENT // 100
+            if isinstance(count, bool) or not isinstance(count, int) or not 0 <= count <= cap:
+                sys.exit(
+                    f"[pool] ARM INVALID: {os.path.basename(path)}'s sidecar records fallbackCount "
+                    f"{count!r}; a reranker-vs-reranker pair needs an integer within the cap "
+                    f"{cap} (floor({POOL_MAX_FALLBACK_PERCENT}% of its {len(seq)} queries), RFDT spec §6.4)."
+                )
+        return
     if fraction < POOL_MIN_REORDERED_FRACTION:
         sys.exit(
             f"[pool] ARM INVALID: ranked sequence differs from {os.path.basename(baseline_path)} "
```

- [ ] **Step 3: Run the tests, including the second `check_pool` caller**

```bash
cd $S && python3 -m pytest -q test_report.py test_rrf_fuse.py 2>&1 | tail -1
```

Expected: `56 passed` (29 + 27; the one pre-existing scipy warning is unchanged).

- [ ] **Step 4: Check the rule on real files** (needs Task 2's dry-run outputs; skip if Task 2 has not run yet)

```bash
python3 $S/report.py --run $A/dryrun/noul-reversed.chunks.trec --run $A/dryrun/choice-reversed.chunks.trec --qrels $SF/qrels.trec \
  --pair $A/dryrun/noul-reversed.chunks.trec=$A/dryrun/choice-reversed.chunks.trec 2>&1 | command grep "\[pool\]"; echo "exit ${PIPESTATUS[0]}"
```

Expected: a `[pool]` line naming the reranker-vs-reranker rule with 0.0% reordered, and `exit 0`.

- [ ] **Step 5: Commit**

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/report.py Iverson.Server/Iverson.LoadTest/scripts/test_report.py
git -C $R commit -m "let report.py pair two rerankers on their fallback counts instead of the reorder floor

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: bge-base pools from the snapshots, and the fidelity gate

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/tenant_pools.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_tenant_pools.py`

**Interfaces:**
- Produces: `tenant_pools.py pools … --retrieval <exact|qdrant>` and the retrieval mode that passed fidelity per tenant, recorded in `$A/fidelity.txt` (Task 8 uses it).

- [ ] **Step 1: Write the test file** `test_tenant_pools.py`

```python
"""pytest suite for tenant_pools.py (spec docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md
§5.2). Needs numpy:

    PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs \\
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_tenant_pools.py -q

No TEI, no Qdrant: both are stubbed at post_json."""
import json
import os
import sys

import numpy as np
import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tenant_pools as tp  # noqa: E402


def vecs(rows):
    return tp.unit(np.array(rows, dtype=np.float32))


def test_constants_match_the_shipped_search():
    assert tp.QUERY_PREFIX == "Represent this sentence for searching relevant passages: "
    assert (tp.DOCS, tp.FETCH_FACTOR, tp.W_BASE, tp.W_CENTROID, tp.LAMBDA) == (50, 4, 0.45, 0.45, 0.70)


def test_fusion_lets_a_strong_centroid_outrank_a_stronger_chunk():
    # Chunk 0 (doc A) matches the query better than chunk 1 (doc B), but B's centroid is aligned
    # with the query and A's is orthogonal, so the fused score puts B first.
    q = np.array([1.0, 0.0], dtype=np.float32)
    chunks, cents = vecs([[1.0, 0.1], [1.0, 0.5]]), vecs([[0.0, 1.0], [1.0, 0.0]])
    candidates = tp.exact_candidates(q, chunks, np.array([0, 1]), fetch=2)
    ranked = tp.pool_for_query(q, *candidates, cents, ["A", "B"], top_k=2, lam=1.0)
    assert [d for d, _ in ranked] == ["B", "A"]
    assert ranked[0][1] == pytest.approx((0.45 * float(chunks[1] @ q) + 0.45 * 1.0) / 0.9)
    assert ranked[1][1] == pytest.approx((0.45 * float(chunks[0] @ q) + 0.45 * 0.0) / 0.9)
    # MMR runs over the FUSED order: its unconditional first pick is B's chunk, not A's.
    assert [d for d, _ in tp.pool_for_query(q, *candidates, cents, ["A", "B"], top_k=1)] == ["B"]


def test_exact_candidates_fetch_the_nearest_chunks_best_first():
    q = np.array([1.0, 0.0], dtype=np.float32)
    chunks = vecs([[1.0, 0.3], [1.0, 0.0], [1.0, 0.9], [1.0, 0.1]])
    base, vectors, parents = tp.exact_candidates(q, chunks, np.array([7, 8, 9, 10]), fetch=3)
    assert list(parents) == [8, 10, 7]
    assert np.allclose(vectors, chunks[[1, 3, 0]]) and np.allclose(base, chunks[[1, 3, 0]] @ q)


def test_mmr_picks_the_top_fused_first_then_trades_score_for_diversity():
    # B is nearly A (cos 0.8) and scores higher than C, which is orthogonal to A. After A:
    # mmr(B) = 0.7*0.9 - 0.3*0.8 = 0.39, mmr(C) = 0.42, mmr(D) = 0.413 -> C. After A, C: B's penalty is
    # its MAX similarity to the selected set (0.8, to A -- not 0.6, to the last pick C) -> D (0.413)
    # beats B (0.39); last-pick-only would give B 0.45.
    scores = np.array([1.0, 0.9, 0.6, 0.59])
    vectors = np.array([[1.0, 0.0, 0.0], [0.8, 0.6, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]])
    assert tp.mmr_select(scores, vectors, top_k=4) == [0, 2, 3, 1]
    assert tp.mmr_select(scores, vectors, top_k=4, lam=1.0) == [0, 1, 2, 3]   # lam 1 is fused order
    assert tp.mmr_select(scores, vectors, top_k=2) == [0, 2]


def test_mmr_weights_score_by_lambda_and_similarity_by_one_minus_lambda():
    # mmr(1) = 0.7*0.9 - 0.3*0.5 = 0.48 > mmr(2) = 0.7*0.2 = 0.14 -> 1; swapping the weights
    # (0.3*0.9 - 0.7*0.5 = -0.08 < 0.06) would pick 2.
    scores = np.array([1.0, 0.9, 0.2])
    vectors = np.array([[1.0, 0.0, 0.0], [0.5, np.sqrt(0.75), 0.0], [0.0, 0.0, 1.0]])
    assert tp.mmr_select(scores, vectors, top_k=2) == [0, 1]


def test_mmr_tie_keeps_the_earlier_candidate():
    # 1 and 2 have equal fused scores and are both orthogonal to 0: an exact MMR tie.
    scores = np.array([0.9, 0.5, 0.5])
    vectors = np.eye(3)
    assert tp.mmr_select(scores, vectors, top_k=3) == [0, 1, 2]


def test_mmr_handles_empty_and_short_inputs():
    assert tp.mmr_select(np.array([]), np.zeros((0, 2)), top_k=5) == []
    assert tp.mmr_select(np.array([0.3, 0.2]), np.eye(2), top_k=5) == [0, 1]
    assert tp.mmr_select(np.array([0.3, 0.2]), np.eye(2), top_k=0) == []


def test_collapse_scores_a_document_by_its_best_chunk_not_its_first():
    ranked = tp.collapse([("A", 0.5), ("B", 0.7), ("A", 0.9), ("C", 0.6)])
    assert [d for d, _ in ranked] == ["A", "B", "C"]
    assert ranked[0][1] == pytest.approx(0.9)
    assert tp.collapse([("A", 0.9), ("B", 0.7), ("A", 0.5)]) == [("A", pytest.approx(0.9)), ("B", pytest.approx(0.7))]


def test_collapse_orders_by_score_not_by_stream_order():
    # The MMR stream is not score-ordered; the collapse re-sorts (DocumentRanking.CollapseByDocId).
    assert [d for d, _ in tp.collapse([("A", 0.5), ("C", 0.6), ("B", 0.7)])] == ["B", "C", "A"]


def test_collapse_ties_keep_first_appearance_at_float32_precision():
    # 0.5 + 1e-12 narrows to 0.5 as the proto float does, so B and A tie and B (seen first) leads.
    ranked = tp.collapse([("B", 0.5), ("A", 0.5 + 1e-12), ("C", 0.4)])
    assert ranked == [("B", 0.5), ("A", 0.5), ("C", pytest.approx(0.4))]


def test_collapse_keeps_at_most_docs_documents():
    assert [d for d, _ in tp.collapse([(c, 1.0 - i / 10) for i, c in enumerate("ABCDE")], docs=3)] == list("ABC")


def test_pool_selects_top_k_chunks_by_mmr_before_collapsing():
    # Three near-duplicate chunks of A (fused ~0.80) lead B's chunk (fused 0.75); at top_k 2, MMR's
    # second pick is B's dissimilar chunk (0.7*0.75 - 0.3*0.5 = 0.375), not A's second chunk
    # (0.7*0.80 - 0.3*1.0 = 0.26), so B makes the pool -- plain Take(2) would miss it.
    q = np.array([1.0, 0.0, 0.0], dtype=np.float32)
    chunks = vecs([[1.0, 0.05, 0.0], [1.0, 0.06, 0.0], [0.5, 0.0, 0.75 ** 0.5], [1.0, 0.07, 0.0]])
    parents = np.array([0, 0, 1, 0])
    cents = vecs([[0.6, 0.8, 0.0], [1.0, 0.0, 0.0]])
    candidates = tp.exact_candidates(q, chunks, parents, fetch=4)
    assert [d for d, _ in tp.pool_for_query(q, *candidates, cents, ["A", "B"], top_k=2)] == ["A", "B"]
    assert [d for d, _ in tp.pool_for_query(q, *candidates, cents, ["A", "B"], top_k=2, lam=1.0)] == ["A"]


def write_dump(dump_dir, chunk_rows, parents, centroid_rows, objects):
    os.makedirs(dump_dir, exist_ok=True)
    np.save(os.path.join(dump_dir, "chunk_vecs.npy"), np.array(chunk_rows, dtype=np.float32))
    np.save(os.path.join(dump_dir, "centroids.npy"), np.array(centroid_rows, dtype=np.float32))
    with open(os.path.join(dump_dir, "chunk_parents.json"), "w") as f:
        json.dump(parents, f)
    with open(os.path.join(dump_dir, "objects.json"), "w") as f:
        json.dump(objects, f)


def test_load_index_maps_chunks_to_parent_rows_and_doc_ids(tmp_path):
    write_dump(str(tmp_path), [[3.0, 4.0], [1.0, 0.0]], ["k2", "k1"], [[0.0, 2.0], [2.0, 0.0]],
               [["k1", "d1"], ["k2", "d2"]])
    chunks, parent_row, cents, row_doc, row_of_key = tp.load_index(str(tmp_path))
    assert np.allclose(chunks, [[0.6, 0.8], [1.0, 0.0]]) and np.allclose(cents, [[0.0, 1.0], [1.0, 0.0]])
    assert list(parent_row) == [1, 0] and row_doc == ["d1", "d2"] and row_of_key == {"k1": 0, "k2": 1}


def test_embed_query_posts_the_prefixed_query_to_tei_v1_embeddings(monkeypatch):
    calls = []
    monkeypatch.setattr(tp, "post_json", lambda url, body: calls.append((url, body)) or {"data": [{"embedding": [1, 2]}]})
    assert tp.embed_query("http://tei", "statins") == [1, 2]
    assert calls == [("http://tei/v1/embeddings", {"model": "BAAI/bge-base-en-v1.5",
                                                   "input": tp.QUERY_PREFIX + "statins"})]


def run_pools(tmp_path, monkeypatch, multiplier, extra=()):
    write_dump(str(tmp_path / "dump"), [[1.0, 0.0], [1.0, 0.5]], ["k1", "k2"], [[1.0, 0.0], [0.0, 1.0]],
               [["k1", "d1"], ["k2", "d2"]])
    (tmp_path / "queries.jsonl").write_text(json.dumps({"_id": "syn-1", "text": "q"}) + "\n")
    out = tmp_path / "pools.trec"
    monkeypatch.setattr(tp, "embed_query", lambda tei, text: [2.0, 0.0])
    tp.main(["pools", "--dump-dir", str(tmp_path / "dump"), "--tei", "http://tei", "--queries",
             str(tmp_path / "queries.jsonl"), "--chunk-multiplier", str(multiplier), "--out", str(out), *extra])
    return out


@pytest.mark.parametrize("multiplier", [5, 11])
def test_pools_fetches_four_times_the_chunk_budget(tmp_path, monkeypatch, multiplier):
    seen = []
    real = tp.exact_candidates
    monkeypatch.setattr(tp, "exact_candidates", lambda *a: seen.append(a[-1]) or real(*a))
    selected = []
    real_mmr = tp.mmr_select
    monkeypatch.setattr(tp, "mmr_select", lambda s, v, k, lam: selected.append(k) or real_mmr(s, v, k, lam))
    out = run_pools(tmp_path, monkeypatch, multiplier)
    assert seen == [4 * 50 * multiplier] and selected == [50 * multiplier]
    assert [l.split()[:4] for l in out.read_text().splitlines()] == [["syn-1", "Q0", "d1", "1"], ["syn-1", "Q0", "d2", "2"]]


def test_pools_qdrant_retrieval_sends_the_servers_search_and_uses_its_hits(tmp_path, monkeypatch):
    calls = []

    def fake_post(url, body):
        calls.append((url, body))
        return {"result": [  # Qdrant's order: d2's chunk first, so d2 leads unless fusion reorders
            {"id": 9, "score": 0.9, "payload": {"parent_id": "k2"}, "vector": {"body_vector": [0.0, 3.0]}},
            {"id": 8, "score": 0.8, "payload": {"parent_id": "k1"}, "vector": {"body_vector": [3.0, 0.0]}},
        ]}
    monkeypatch.setattr(tp, "post_json", fake_post)
    out = run_pools(tmp_path, monkeypatch, 5, ["--retrieval", "qdrant", "--qdrant", "http://qd"])
    url, body = calls[0]
    assert url == "http://qd/collections/benchmark_documents_chunks_tenant_bypass/points/search"
    assert body == {"vector": {"name": "body_vector", "vector": [1.0, 0.0]}, "limit": 1000,
                    "with_payload": ["parent_id"], "with_vector": ["body_vector"]}
    # fused: d1 = (0.45*0.8 + 0.45*1)/0.9 = 0.9; d2 = (0.45*0.9 + 0.45*0)/0.9 = 0.45
    lines = [l.split() for l in out.read_text().splitlines()]
    assert [(l[2], l[4]) for l in lines] == [("d1", "0.900000"), ("d2", "0.450000")]


def test_qdrant_candidates_keep_qdrants_hit_order_and_unit_vectors(monkeypatch):
    monkeypatch.setattr(tp, "post_json", lambda url, body: {"result": [
        {"id": 9, "score": 0.9, "payload": {"parent_id": "k2"}, "vector": {"body_vector": [0.0, 3.0]}},
        {"id": 8, "score": 0.8, "payload": {"parent_id": "k1"}, "vector": {"body_vector": [3.0, 0.0]}},
    ]})
    base, vectors, parents = tp.qdrant_candidates("http://qd", np.array([1.0, 0.0]), {"k1": 0, "k2": 1}, fetch=2)
    assert list(base) == [0.9, 0.8] and list(parents) == [1, 0]
    assert np.allclose(vectors, [[0.0, 1.0], [1.0, 0.0]])


def test_pools_qdrant_retrieval_requires_a_qdrant_url(tmp_path, monkeypatch):
    with pytest.raises(SystemExit) as exc:
        run_pools(tmp_path, monkeypatch, 5, ["--retrieval", "qdrant"])
    assert "--qdrant" in str(exc.value)


def test_post_json_sends_the_qdrant_api_key_only_when_set(monkeypatch):
    sent = []

    class Resp:
        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

        def read(self):
            return b"{}"
    monkeypatch.setattr(tp.urllib.request, "urlopen", lambda req, timeout: sent.append(req) or Resp())
    monkeypatch.delenv("QDRANT__SERVICE__API_KEY", raising=False)
    tp.post_json("http://x", {})
    monkeypatch.setenv("QDRANT__SERVICE__API_KEY", "k")
    tp.post_json("http://x", {})
    assert [(r.has_header("Api-key"), r.get_header("Api-key")) for r in sent] == [(False, None), (True, "k")]


def test_overlap_counts_set_overlap_and_identical_lists_on_common_queries():
    ref = {"1": ["a", "b", "c", "d"], "2": ["e", "f", "g", "h"], "3": ["x", "y", "z", "w"]}
    run = {"1": ["a", "b", "c", "d"], "2": ["f", "e", "g", "q"]}
    mean, identical, n = tp.overlap(run, ref)
    assert (n, identical) == (2, 1)
    assert mean == pytest.approx((1.0 + 0.75) / 2)


def test_fidelity_passes_at_the_bar_and_exits_below_it(tmp_path, capsys):
    ref, run = tmp_path / "ref.trec", tmp_path / "run.trec"
    tp.write_run(str(ref), [("1", [(d, 1.0) for d in "abcdefghij"])])
    tp.write_run(str(run), [("1", [(d, 1.0) for d in "abcdefghiz"])])        # 9/10 overlap
    tp.main(["fidelity", "--run", str(run), "--reference", str(ref), "--min-overlap", "0.90"])
    assert "mean top-50 overlap 0.9000; identical ordered lists 0/1" in capsys.readouterr().out
    with pytest.raises(SystemExit) as exc:
        tp.main(["fidelity", "--run", str(run), "--reference", str(ref), "--min-overlap", "0.91"])
    assert "FAIL" in str(exc.value)
    tp.write_run(str(run), [("2", [("a", 1.0)])])                             # no common queries
    with pytest.raises(SystemExit):
        tp.main(["fidelity", "--run", str(run), "--reference", str(ref), "--min-overlap", "0.0"])


def test_run_round_trips_through_write_and_teacher_rerank_load_run(tmp_path):
    path = str(tmp_path / "pools.trec")
    tp.write_run(path, [("syn-1", [("10", 0.9), ("20", 0.8)]), ("syn-2", [("30", 0.7)])])
    assert tp.tr.load_run(path) == {"syn-1": ["10", "20"], "syn-2": ["30"]}
    assert open(path).readline().split() == ["syn-1", "Q0", "10", "1", "0.900000", "tenant-pools"]
```

- [ ] **Step 2: Run it and see it fail** — `cd $S && python3 -m pytest -q test_tenant_pools.py 2>&1 | tail -2` → `ModuleNotFoundError: No module named 'tenant_pools'`.

- [ ] **Step 3: Write** `tenant_pools.py`

```python
#!/usr/bin/env python3
"""Top-50 pools for synthetic queries, rebuilt on the dev box from the saved bge-base Qdrant snapshots
(spec docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md §5.2). Generalizes student-distill's
snapshot_pools.py (nomic/Ollama, 1,000/250, no MMR) to the shipped SearchChunks and benchmark harness:

  1. embed QUERY_PREFIX + text on TEI's /v1/embeddings, the route the server uses
     (EmbeddingService.cs:99-118; prefix EmbeddingPrefixes.cs:37, read from ingest-contract.json);
  2. fetch FETCH_FACTOR * top_k chunks by cosine (ObjectSearchGrpcService.cs:577, OverFetchFactor :951):
     an exact scan over the dump (--retrieval exact), or Qdrant's own search with default params,
     as the server calls it (--retrieval qdrant; IntelligenceVectorService.cs:138-144);
  3. fused = (W_BASE * cos(q, chunk) + W_CENTROID * cos(q, parent body_centroid)) / (W_BASE + W_CENTROID)
     (ResultReranker.cs:37-59: BenchmarkDocument has no timestamp column, so no decay term, and no
     popularity signal is configured), ordered fused-descending, ties in retrieval order (:65-67);
  4. greedy MMR at LAMBDA over the chunks' own body_vectors, selecting top_k (ResultDiversifier.cs:12-109,
     fed at ObjectSearchGrpcService.cs:604-611);
  5. max-passage collapse to DOCS documents: a document scores its best selected chunk's fused score,
     narrowed to float32 (the response's Score is a proto float, ObjectSearchGrpcService.cs:623), best
     first, ties by first appearance in the MMR stream (DocumentRanking.cs:30-46).

top_k = DOCS * --chunk-multiplier (BenchmarkQueryScenario.cs:421): SciFact 5, FreshStack 11.

Three subcommands:

    dump      scroll the restored Qdrant collections into --dump-dir (numpy + json)
    pools     embed --queries through TEI and write a 6-column TREC run
    fidelity  compare a pools run with the shipped run; exit 1 below --min-overlap

Qdrant calls send `api-key: $QDRANT__SERVICE__API_KEY` when that variable is set (ingest.py's key).
Needs numpy (PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs)."""
import argparse
import json
import os
import sys
import urllib.request

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import teacher_rerank as tr  # noqa: E402  (load_run, load_queries -- reused, never copied)

# The snapshots' own collection names (RESTORE.md), not ingest.py's fingerprinted defaults.
CHUNKS_COLLECTION = "benchmark_documents_chunks_tenant_bypass"
OBJECTS_COLLECTION = "benchmark_documents_tenant_bypass"
CHUNK_VECTOR, CENTROID_VECTOR = "body_vector", "body_centroid"
MODEL_ID = "BAAI/bge-base-en-v1.5"
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "ingest-contract.json"), encoding="utf-8") as _f:
    QUERY_PREFIX = json.load(_f)["embedding"]["queryPrefixes"][MODEL_ID]
DOCS = 50                       # DocumentBudget (BenchmarkQueryScenario.cs:42)
FETCH_FACTOR = 4                # OverFetchFactor (ObjectSearchGrpcService.cs:951)
W_BASE, W_CENTROID = 0.45, 0.45  # VectorRankingOptions.cs:83-84
LAMBDA = 0.70                   # LambdaChunks (VectorRankingOptions.cs:96)
RUN_TAG = "tenant-pools"


def post_json(url, body, timeout=600):
    req = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    api_key = os.environ.get("QDRANT__SERVICE__API_KEY")
    if api_key:
        req.add_header("api-key", api_key)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read())


def scroll(qdrant, collection, vector_name):
    points, offset = [], None
    while True:
        body = {"limit": 2000, "with_payload": True, "with_vector": [vector_name]}
        if offset is not None:
            body["offset"] = offset
        result = post_json(f"{qdrant}/collections/{collection}/points/scroll", body)["result"]
        points += result["points"]
        offset = result.get("next_page_offset")
        if offset is None:
            return points


def dump(qdrant, dump_dir):
    os.makedirs(dump_dir, exist_ok=True)
    chunks = scroll(qdrant, CHUNKS_COLLECTION, CHUNK_VECTOR)
    objects = scroll(qdrant, OBJECTS_COLLECTION, CENTROID_VECTOR)
    np.save(os.path.join(dump_dir, "chunk_vecs.npy"),
            np.array([p["vector"][CHUNK_VECTOR] for p in chunks], dtype=np.float32))
    with open(os.path.join(dump_dir, "chunk_parents.json"), "w", encoding="utf-8") as f:
        json.dump([p["payload"]["parent_id"] for p in chunks], f)
    np.save(os.path.join(dump_dir, "centroids.npy"),
            np.array([p["vector"][CENTROID_VECTOR] for p in objects], dtype=np.float32))
    with open(os.path.join(dump_dir, "objects.json"), "w", encoding="utf-8") as f:
        json.dump([[p["payload"]["key"], p["payload"]["docId"]] for p in objects], f)
    print(f"[dump] {len(chunks)} chunks, {len(objects)} objects -> {dump_dir}")


def unit(m):
    return m / np.linalg.norm(m, axis=-1, keepdims=True)


def load_index(dump_dir):
    """(chunk vectors, chunk -> parent row, centroids, parent row -> docId, parent key -> row),
    vectors unit-normalised."""
    chunks = unit(np.load(os.path.join(dump_dir, "chunk_vecs.npy")))
    centroids = unit(np.load(os.path.join(dump_dir, "centroids.npy")))
    with open(os.path.join(dump_dir, "objects.json"), encoding="utf-8") as f:
        objects = json.load(f)
    with open(os.path.join(dump_dir, "chunk_parents.json"), encoding="utf-8") as f:
        parents = json.load(f)
    row_of_key = {key: row for row, (key, _doc) in enumerate(objects)}
    chunk_parent_row = np.array([row_of_key[p] for p in parents])
    return chunks, chunk_parent_row, centroids, [doc for _key, doc in objects], row_of_key


def exact_candidates(query_vec, chunks, chunk_parent_row, fetch):
    """The `fetch` chunks nearest by cosine, best first (ties in dump order): (base scores, chunk
    vectors, parent rows)."""
    base = chunks @ query_vec
    fetched = np.argsort(-base, kind="stable")[:fetch]
    return base[fetched], chunks[fetched], chunk_parent_row[fetched]


def qdrant_candidates(qdrant, query_vec, row_of_key, fetch):
    """The same triple from Qdrant's own search on the chunks collection: the server's call
    (named vector, limit, default search params), plus each hit's stored vector for MMR."""
    hits = post_json(f"{qdrant}/collections/{CHUNKS_COLLECTION}/points/search", {
        "vector": {"name": CHUNK_VECTOR, "vector": [float(x) for x in query_vec]},
        "limit": fetch, "with_payload": ["parent_id"], "with_vector": [CHUNK_VECTOR],
    })["result"]
    return (np.array([h["score"] for h in hits], dtype=np.float64),
            unit(np.array([h["vector"][CHUNK_VECTOR] for h in hits], dtype=np.float32)),
            np.array([row_of_key[h["payload"]["parent_id"]] for h in hits]))


def fuse(query_vec, base, parent_centroids):
    """ResultReranker's weighted mean of the base and centroid similarities, in double."""
    centroid_sim = parent_centroids.astype(np.float64) @ query_vec.astype(np.float64)
    return (W_BASE * base.astype(np.float64) + W_CENTROID * centroid_sim) / (W_BASE + W_CENTROID)


def mmr_select(scores, vectors, top_k, lam=LAMBDA):
    """Greedy MMR over fused-descending candidates (ResultDiversifier.Diversify): index 0 first,
    then argmax of lam * score - (1 - lam) * max cosine to the selected chunks; np.argmax keeps the
    earlier candidate on a tie, as the C# strict `>` does. Returns indices in selection order."""
    take = min(top_k, len(scores))
    if take <= 0:
        return []
    vectors = unit(vectors.astype(np.float64))
    taken = np.zeros(len(scores), dtype=bool)
    max_sim = np.full(len(scores), -np.inf)
    picks = []
    pick = 0
    while True:
        picks.append(pick)
        taken[pick] = True
        if len(picks) == take:
            return picks
        max_sim = np.maximum(max_sim, vectors @ vectors[pick])
        mmr = lam * scores - (1 - lam) * max_sim
        mmr[taken] = -np.inf
        pick = int(np.argmax(mmr))


def collapse(selected, docs=DOCS):
    """[(docId, score)] from [(docId, chunk score)] in stream order: each document's maximum, as
    float32, best first; sorted() is stable, so ties keep first appearance."""
    best = {}
    for doc, score in selected:
        score = float(np.float32(score))
        if doc not in best or score > best[doc]:
            best[doc] = score
    return sorted(best.items(), key=lambda kv: -kv[1])[:docs]


def pool_for_query(query_vec, base, vectors, parent_rows, centroids, row_doc, top_k, lam=LAMBDA, docs=DOCS):
    """[(docId, score)] for one unit-normalised query over its fetched candidates (retrieval order)."""
    fused = fuse(query_vec, base, centroids[parent_rows])
    order = np.argsort(-fused, kind="stable")
    picks = mmr_select(fused[order], vectors[order], top_k, lam)
    return collapse([(row_doc[parent_rows[order[i]]], fused[order[i]]) for i in picks], docs)


def embed_query(tei, text):
    reply = post_json(f"{tei}/v1/embeddings", {"model": MODEL_ID, "input": QUERY_PREFIX + text})
    return reply["data"][0]["embedding"]


def write_run(path, pools):
    with open(path, "w", encoding="utf-8") as f:
        for query_id, ranked in pools:
            for rank, (doc, score) in enumerate(ranked, start=1):
                f.write(f"{query_id} Q0 {doc} {rank} {score:.6f} {RUN_TAG}\n")


def overlap(candidate, reference):
    """(mean top-50 set overlap, identical ordered lists, queries compared) over the queries both
    runs contain."""
    common = [q for q in reference if q in candidate]
    fractions = [len(set(candidate[q]) & set(reference[q])) / len(reference[q]) for q in common]
    identical = sum(candidate[q] == reference[q] for q in common)
    return (sum(fractions) / len(fractions) if common else 0.0), identical, len(common)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("dump")
    d.add_argument("--qdrant", required=True)
    d.add_argument("--dump-dir", required=True)
    p = sub.add_parser("pools")
    p.add_argument("--dump-dir", required=True)
    p.add_argument("--tei", required=True, help="TEI root serving bge-base, e.g. http://127.0.0.1:8091")
    p.add_argument("--queries", required=True, help="BEIR queries.jsonl (_id, text)")
    p.add_argument("--chunk-multiplier", type=int, choices=[5, 11], required=True,
                   help="the pool's chunkBudgetMultiplier: SciFact 5, FreshStack 11")
    p.add_argument("--retrieval", choices=["exact", "qdrant"], default="exact")
    p.add_argument("--qdrant", help="Qdrant root holding the restored snapshots (--retrieval qdrant)")
    p.add_argument("--out", required=True)
    f = sub.add_parser("fidelity")
    f.add_argument("--run", required=True)
    f.add_argument("--reference", required=True)
    f.add_argument("--min-overlap", type=float, default=0.90)
    args = ap.parse_args(argv)

    if args.cmd == "dump":
        dump(args.qdrant, args.dump_dir)
    elif args.cmd == "pools":
        if args.retrieval == "qdrant" and not args.qdrant:
            sys.exit("--retrieval qdrant needs --qdrant")
        chunks, chunk_parent_row, centroids, row_doc, row_of_key = load_index(args.dump_dir)
        top_k = DOCS * args.chunk_multiplier
        fetch = FETCH_FACTOR * top_k
        pools, short = [], 0
        for query_id, text in tr.load_queries(args.queries).items():
            vec = unit(np.array(embed_query(args.tei, text), dtype=np.float32))
            if args.retrieval == "exact":
                candidates = exact_candidates(vec, chunks, chunk_parent_row, fetch)
            else:
                candidates = qdrant_candidates(args.qdrant, vec, row_of_key, fetch)
            ranked = pool_for_query(vec, *candidates, centroids, row_doc, top_k)
            short += len(ranked) < DOCS
            pools.append((query_id, ranked))
        write_run(args.out, pools)
        print(f"[pools] wrote {args.out} ({len(pools)} queries, top_k {top_k}, fetch {fetch}, "
              f"{args.retrieval} retrieval; {short} pools under {DOCS} documents)")
    else:
        mean, identical, n = overlap(tr.load_run(args.run), tr.load_run(args.reference))
        print(f"[fidelity] {n} queries: mean top-50 overlap {mean:.4f}; identical ordered lists {identical}/{n}")
        if n == 0 or mean < args.min_overlap:
            sys.exit(f"[fidelity] FAIL: below {args.min_overlap}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run the tests** — `cd $S && python3 -m pytest -q test_tenant_pools.py 2>&1 | tail -1` → `23 passed`.

- [ ] **Step 5: Commit the code**

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/tenant_pools.py Iverson.Server/Iverson.LoadTest/scripts/test_tenant_pools.py
git -C $R commit -m "add the bge-base pool builder for synthetic queries

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 6: Start a throwaway Qdrant and TEI** on ports that cannot clash with a benchmark stack (`docker` is podman on this box)

```bash
export QDRANT__SERVICE__API_KEY=rfdt-local-qdrant-key-0123456789abcdef
docker run -d --name rfdt-qdrant -p 127.0.0.1:16333:6333 -e QDRANT__SERVICE__API_KEY docker.io/qdrant/qdrant:v1.18.2
docker run -d --name rfdt-tei -p 127.0.0.1:18091:80 -v tei_models:/data \
  ghcr.io/huggingface/text-embeddings-inference:cpu-1.8 --model-id BAAI/bge-base-en-v1.5 --auto-truncate --max-batch-tokens 16384
until curl -sf http://127.0.0.1:18091/health; do sleep 5; done; echo TEI up
until curl -sf -H "api-key: $QDRANT__SERVICE__API_KEY" http://127.0.0.1:16333/collections >/dev/null; do sleep 2; done; echo Qdrant up
```

Then write `$A/restore.sh`, which every Task 4 Step 7 and Task 8 Step 4 block sources (each step runs in a fresh shell):

```bash
mkdir -p $A && cat > $A/restore.sh <<'SH'
# Sourced by every Task 4 Step 7 and Task 8 Step 4 block: the throwaway Qdrant's key and restore().
export QDRANT__SERVICE__API_KEY=rfdt-local-qdrant-key-0123456789abcdef
restore() {  # $1 = snapshot dir; replaces both collections
  for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do
    curl -s -X DELETE -H "api-key: $QDRANT__SERVICE__API_KEY" http://127.0.0.1:16333/collections/$c >/dev/null || true
  done
  for f in $1/*.snapshot; do
    c=$(basename "$f"); c="${c%%-6802952876034638*}"
    curl -sf -X POST -H "api-key: $QDRANT__SERVICE__API_KEY" \
      "http://127.0.0.1:16333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f" >/dev/null
  done
}
SH
```

- [ ] **Step 7: Per tenant — restore, dump, build test-query pools, check fidelity.** Both tenants restore to the same collection names, so one tenant at a time; the dump survives the next restore.

```bash
set -e
source $A/restore.sh
mkdir -p $A/dumps $A/fidelity
restore $B/scifact-bge-base-qdrant-snapshots
python3 $S/tenant_pools.py dump --qdrant http://127.0.0.1:16333 --dump-dir $A/dumps/scifact
python3 $S/tenant_pools.py pools --dump-dir $A/dumps/scifact --tei http://127.0.0.1:18091 \
  --queries $SF/beir/queries.jsonl --chunk-multiplier 5 --out $A/fidelity/scifact-exact.trec
python3 $S/tenant_pools.py fidelity --run $A/fidelity/scifact-exact.trec --reference $SF/runs/bge-base.chunks.trec \
  && echo "scifact exact" >> $A/fidelity.txt
```

Expected: `[dump] 19967 chunks, 5183 objects`, then a `[fidelity]` line with mean top-50 overlap ≥ 0.90. If it exits 1, in a shell that has run `source $A/restore.sh`, rerun the `pools` step with `--retrieval qdrant --qdrant http://127.0.0.1:16333 --out $A/fidelity/scifact-qdrant.trec` and its `fidelity` check, appending `scifact qdrant` on success. If both fail: **STOP** — spec §8, no spend.

Then FreshStack, the same way:

```bash
set -e
source $A/restore.sh
restore $B/freshstack-2048-qdrant-snapshots
python3 $S/tenant_pools.py dump --qdrant http://127.0.0.1:16333 --dump-dir $A/dumps/freshstack
python3 $S/tenant_pools.py pools --dump-dir $A/dumps/freshstack --tei http://127.0.0.1:18091 \
  --queries $FS/beir/queries.jsonl --chunk-multiplier 11 --out $A/fidelity/freshstack-exact.trec
python3 $S/tenant_pools.py fidelity --run $A/fidelity/freshstack-exact.trec --reference $FS/runs/fs-2048-l070.chunks.trec \
  && echo "freshstack exact" >> $A/fidelity.txt
```

Expected: `[dump] 18622 chunks, 6000 objects`; overlap ≥ 0.90, or the `qdrant` fallback as above (in a shell that has run `source $A/restore.sh`; the Qdrant collections then hold FreshStack — restore SciFact again before a SciFact `--retrieval qdrant` run). `$A/fidelity.txt` ends with one line per tenant naming the mode that passed.

- [ ] **Step 8: Leave the containers stopped but kept** (Task 8 reuses them): `docker stop rfdt-qdrant rfdt-tei`.

### Task 5: Synthetic-query generator

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/synth_queries.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_synth_queries.py`
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py` (`call_teacher(..., extra_body=None)`)
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py`

- [ ] **Step 1: Apply the `teacher_rerank` test diff** (`/tmp/t5-tr-tests.diff`) and write `test_synth_queries.py`

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
@@ -473,6 +473,51 @@
         thread.join(timeout=5)
 
 
+class _RecordingBodyHandler(_RecordingAuthHandler):
+    """The same valid reply, recording the parsed request body it was sent."""
+
+    seen_body = None
+
+    def do_POST(self):
+        length = int(self.headers["Content-Length"])
+        _RecordingBodyHandler.seen_body = json.loads(self.rfile.read(length))
+        reply = json.dumps({"choices": [{"message": {"content": "[]"}, "finish_reason": "stop"}]}).encode("utf-8")
+        self.send_response(200)
+        self.send_header("Content-Type", "application/json")
+        self.send_header("Content-Length", str(len(reply)))
+        self.end_headers()
+        self.wfile.write(reply)
+
+
+def _call_teacher_body(**kwargs):
+    _RecordingBodyHandler.seen_body = None
+    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _RecordingBodyHandler)
+    thread = threading.Thread(target=server.serve_forever, daemon=True)
+    thread.start()
+    try:
+        tr.call_teacher(f"http://127.0.0.1:{server.server_address[1]}", "m", "p", 7, **kwargs)
+        return _RecordingBodyHandler.seen_body
+    finally:
+        server.shutdown()
+        thread.join(timeout=5)
+
+
+def test_call_teacher_body_is_unchanged_without_extra_body():
+    assert _call_teacher_body() == {
+        "model": "m", "messages": [{"role": "user", "content": "p"}], "temperature": 0,
+        "stream": False, "max_tokens": tr.MAX_COMPLETION_TOKENS, "seed": 7,
+    }
+
+
+def test_call_teacher_merges_extra_body_into_the_request_and_its_keys_win():
+    body = _call_teacher_body(extra_body={"chat_template_kwargs": {"enable_thinking": False}, "max_tokens": 64})
+    assert body == {
+        "model": "m", "messages": [{"role": "user", "content": "p"}], "temperature": 0,
+        "stream": False, "max_tokens": 64, "seed": 7,
+        "chat_template_kwargs": {"enable_thinking": False},
+    }
+
+
 class _HTTPErrorResponseHandler(http.server.BaseHTTPRequestHandler):
     """Responds with a non-2xx status to test the HTTPError exception handler."""
 
```

```python
"""pytest suite for synth_queries.py (spec docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md §5.1).
Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_synth_queries.py -q

Stdlib + pytest only; the generator is monkeypatched out at teacher_rerank.call_teacher."""
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import synth_queries as sq  # noqa: E402
import teacher_rerank as tr  # noqa: E402


def test_sample_documents_is_seeded_sorted_and_input_order_independent():
    ids = [str(i) for i in range(2000)]
    first = sq.sample_documents(ids, 1100, "20260928")
    assert first == sq.sample_documents(list(reversed(ids)), 1100, "20260928")
    assert len(first) == 1100 and len(set(first)) == 1100
    assert first[:3] == ["1738", "1159", "1743"]          # student-distill's pinned sample, same seed
    assert sq.sample_documents(ids, 1100, "1") != first


def test_clean_reply_strips_whitespace_and_one_pair_of_quotes():
    assert sq.clean_reply('  "statin therapy and LDL"  ') == ("statin therapy and LDL", None)
    assert sq.clean_reply("“do statins lower LDL?”") == ("do statins lower LDL?", None)
    assert sq.clean_reply("plain query") == ("plain query", None)


def test_clean_reply_rejects_empty_none_and_overlong_replies():
    assert sq.clean_reply("   ") == (None, "empty reply")
    assert sq.clean_reply('""') == (None, "empty reply")
    assert sq.clean_reply(None) == (None, "empty reply")
    assert sq.clean_reply("x" * 301) == (None, "reply longer than 300 characters")
    assert sq.clean_reply("x" * 300) == ("x" * 300, None)


def test_normalised_folds_case_and_whitespace():
    assert sq.normalised("Do  Statins\tLower LDL?") == sq.normalised("do statins lower ldl?")


class FakeTeacher:
    """Replies by document: the prompt carries the document's title line. Records every call's
    keyword arguments."""

    def __init__(self, replies):
        self.replies = replies  # title -> list of (content, finish_reason), consumed in order
        self.kwargs = []

    def __call__(self, base_url, model, prompt, seed, api_key=None, extra_body=None):
        self.kwargs.append({"api_key": api_key, "extra_body": extra_body})
        title = prompt.split("Document:\n", 1)[1].split("\n", 1)[0]
        content, finish = self.replies[title].pop(0)
        return content, finish, None, 42, 100


def run(tmp_path, monkeypatch, n_docs, replies, target, sample_size, tests=("unrelated test query",), extra=()):
    corpus = tmp_path / "corpus.jsonl"
    corpus.write_text("".join(json.dumps({"_id": f"d{i}", "title": f"t{i}", "text": "body"}) + "\n"
                              for i in range(n_docs)))
    queries = tmp_path / "test-queries.jsonl"
    queries.write_text("".join(json.dumps({"_id": str(i), "text": t}) + "\n" for i, t in enumerate(tests)))
    teacher = FakeTeacher(replies)
    monkeypatch.setattr(tr, "call_teacher", teacher)
    ledger, out = tmp_path / "gen.jsonl", tmp_path / "queries.jsonl"
    argv = ["--corpus", str(corpus), "--exclude-queries", str(queries), "--sample-size", str(sample_size),
            "--target", str(target), "--sample-seed", "20260928", "--base-url", "x", "--model", "m",
            "--seed", "1", "--ledger", str(ledger), "--out", str(out), *extra]
    return argv, ledger, out, teacher


def test_main_keeps_usable_non_duplicate_queries_in_sample_order_with_retries(tmp_path, monkeypatch):
    # Seeded sample order of d0..d5 is d3, d0, d5, d2, d1, d4 (pinned below).
    replies = {f"t{i}": [(f"query {i}", "stop")] for i in range(6)}
    replies["t3"] = [("query 0", "stop")]                                     # first in sample order
    replies["t0"] = [("QUERY  0", "stop")]                                    # duplicates d3's, normalised
    replies["t2"] = [("x", "length"), ('"query two"', "stop")]                # usable on the 2nd attempt
    replies["t4"] = [("", "stop")] * tr.RETRY_BUDGET                          # never usable
    argv, ledger, out, _ = run(tmp_path, monkeypatch, 6, replies, target=3, sample_size=6)
    assert sq.sample_documents([f"d{i}" for i in range(6)], 6, "20260928") == ["d3", "d0", "d5", "d2", "d1", "d4"]

    sq.main(argv)

    # d0's "QUERY  0" duplicates d3's "query 0" once normalised and is dropped; the target of 3 is
    # then reached at d2, so d1 and d4 never count.
    assert [json.loads(l) for l in out.read_text().splitlines()] == [
        {"_id": "syn-d3", "text": "query 0"},
        {"_id": "syn-d5", "text": "query 5"},
        {"_id": "syn-d2", "text": "query two"},
    ]
    attempts = [json.loads(l) for l in ledger.read_text().splitlines()]
    assert [a["reason"] for a in attempts if a["doc_id"] == "d2"] == ["finish_reason=length", None]
    assert len([a for a in attempts if a["doc_id"] == "d4"]) == tr.RETRY_BUDGET


def test_main_drops_a_reply_equal_to_a_test_query_after_normalising(tmp_path, monkeypatch, capsys):
    # Sample order d3, d0, d5, d2, d1, d4; d3's reply is test query 1 up to case and whitespace.
    replies = {f"t{i}": [(f"query {i}", "stop")] for i in range(6)}
    replies["t3"] = [("  do STATINS   lower ldl? ", "stop")]
    argv, _ledger, out, _ = run(tmp_path, monkeypatch, 6, replies, target=2, sample_size=6,
                                tests=("unrelated", "Do statins lower LDL?"))
    sq.main(argv)
    assert [json.loads(l)["_id"] for l in out.read_text().splitlines()] == ["syn-d0", "syn-d5"]
    assert "0 unusable, 0 duplicate and 1 test-query replies" in capsys.readouterr().out


def test_main_sends_no_thinking_extra_body_and_the_api_key_on_every_call(tmp_path, monkeypatch):
    replies = {"t0": [("", "stop"), ("q0", "stop")], "t1": [("q1", "stop")]}
    argv, _ledger, _out, teacher = run(tmp_path, monkeypatch, 2, replies, target=2, sample_size=2,
                                       extra=["--api-key", "sk"])
    sq.main(argv)
    assert teacher.kwargs == [{"api_key": "sk", "extra_body": {"chat_template_kwargs": {"enable_thinking": False}, "max_tokens": 512}}] * 3


def test_main_writes_nothing_when_too_few_queries_are_usable(tmp_path, monkeypatch):
    replies = {f"t{i}": [("", "stop")] * tr.RETRY_BUDGET for i in range(3)}
    replies["t0"] = [("only one", "stop")]
    argv, _ledger, out, _ = run(tmp_path, monkeypatch, 3, replies, target=2, sample_size=3)
    with pytest.raises(SystemExit) as exc:
        sq.main(argv)
    assert "only 1 usable queries from 3 documents (2 unusable" in str(exc.value)
    assert not out.exists()


def test_main_refuses_an_existing_ledger(tmp_path, monkeypatch):
    argv, ledger, _out, _ = run(tmp_path, monkeypatch, 1, {"t0": [("q", "stop")]}, target=1, sample_size=1)
    ledger.write_text("{}\n")
    with pytest.raises(SystemExit) as exc:
        sq.main(argv)
    assert "exists" in str(exc.value)


@pytest.mark.parametrize("target,sample_size", [(3, 2), (1, 4), (0, 1)])
def test_main_refuses_impossible_sizes_before_any_call(tmp_path, monkeypatch, target, sample_size):
    argv, ledger, _out, teacher = run(tmp_path, monkeypatch, 3, {}, target=target, sample_size=sample_size)
    with pytest.raises(SystemExit) as exc:
        sq.main(argv)
    assert "--sample-size" in str(exc.value) and teacher.kwargs == [] and not ledger.exists()
```

- [ ] **Step 2: Run and see them fail** — `cd $S && python3 -m pytest -q test_teacher_rerank.py test_synth_queries.py 2>&1 | tail -3`.

- [ ] **Step 3: Apply the `teacher_rerank` diff** (`/tmp/t5-tr.diff`) and write `synth_queries.py`

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
@@ -346,7 +346,7 @@
     return PROMPT_INSTRUCTIONS.format(query=query_text, documents=documents)
 
 
-def call_teacher(base_url, model, prompt, seed, api_key=None):
+def call_teacher(base_url, model, prompt, seed, api_key=None, extra_body=None):
     """POSTs to `{base_url}/v1/chat/completions` with stdlib `urllib.request`, the same transport
     shape as `enrich_bench.py:56-62` (temperature 0, stream false), plus `max_tokens:
     MAX_COMPLETION_TOKENS` (NOT enrich_bench.py's 256, spec §4) and a fixed `seed`. Returns
@@ -374,7 +374,11 @@
     status/reason/body; the broader `OSError` (covers `socket.timeout`/`TimeoutError`, an
     `OSError` subclass, and any other network-level failure `urlopen` can raise) records
     `str(e)`. `HTTPError` must stay listed first -- it is itself an `OSError` subclass, so the
-    more specific clause has to come before the broader catch-all to be distinguished."""
+    more specific clause has to come before the broader catch-all to be distinguished.
+
+    `extra_body`, when given, is merged into the top level of the request body last, so its keys
+    win -- e.g. `{"chat_template_kwargs": {"enable_thinking": False}}` for a model that thinks by
+    default (RFDT design §5.1). None (every existing caller) sends the body unchanged."""
     body = {
         "model": model,
         "messages": [{"role": "user", "content": prompt}],
@@ -383,6 +387,8 @@
         "max_tokens": MAX_COMPLETION_TOKENS,
         "seed": seed,
     }
+    if extra_body:
+        body.update(extra_body)
     req = urllib.request.Request(
         f"{base_url}/v1/chat/completions",
         data=json.dumps(body).encode("utf-8"),
```

```python
#!/usr/bin/env python3
"""Synthetic training queries for one tenant (spec docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md
§5.1): the generator is shown one corpus document at a time and asked, with a generic instruction, for one
search query that document helps answer -- what a tenant with documents but no query log can do.
Parameterized from student-distill's generate_synthetic_queries.py.

Documents are sampled with `random.Random(--sample-seed).sample(sorted(doc_ids), --sample-size)`; the
first --target usable queries in sample order are kept, with ids `syn-<docId>`. A reply is dropped when
its normalised text (case- and whitespace-folded) repeats an earlier kept query or equals one of the
tenant's test queries (--exclude-queries, BEIR queries.jsonl): test queries never become training
queries. Every attempt is appended to --ledger. Fewer than --target usable queries writes nothing and
exits non-zero.

The generator (Qwen3.8-27B under vLLM) thinks by default; every request sends
`chat_template_kwargs: {"enable_thinking": false}`.

    python3 synth_queries.py --corpus $B/scifact-bge-base-2026-09-04/beir/corpus.jsonl \\
        --exclude-queries $B/scifact-bge-base-2026-09-04/beir/queries.jsonl \\
        --sample-size 1100 --target 1000 --sample-seed 20261005 \\
        --base-url http://127.0.0.1:8000 --model Qwen/Qwen3.8-27B --seed 20261005 \\
        --api-key "$VLLM_API_KEY" --concurrency 24 \\
        --ledger $A/phase1/scifact/generation.jsonl --out $A/phase1/scifact/synthetic-queries.jsonl

Stdlib only; the model call is teacher_rerank.call_teacher."""
import argparse
import json
import os
import random
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import teacher_rerank as tr  # noqa: E402  (load_corpus, load_queries, call_teacher, append_response)

GENERATION_PROMPT = (
    "You are helping build a search system over a collection of documents.\n\n"
    "Document:\n{document}\n\n"
    "Write one search query that a user of this document collection might issue, and that this "
    "document helps answer. Reply with the query only."
)
# Qwen3.8 thinks by default (model card); the query is one short line, and teacher_rerank's
# MAX_COMPLETION_TOKENS (32,768) would push prompt + max_tokens past the vLLM pod's --max-model-len.
EXTRA_BODY = {"chat_template_kwargs": {"enable_thinking": False}, "max_tokens": 512}
MAX_QUERY_CHARS = 300
QUOTE_PAIRS = {'"': '"', "'": "'", "“": "”", "‘": "’"}


def sample_documents(doc_ids, sample_size, sample_seed):
    """The seeded, reproducible document sample (sorting first makes it independent of input order)."""
    return random.Random(sample_seed).sample(sorted(doc_ids), sample_size)


def clean_reply(content):
    """(query, None) for a usable reply -- stripped of whitespace and one pair of surrounding quotes,
    non-empty, at most MAX_QUERY_CHARS -- else (None, reason)."""
    query = (content or "").strip()
    if len(query) >= 2 and QUOTE_PAIRS.get(query[0]) == query[-1]:
        query = query[1:-1].strip()
    if not query:
        return None, "empty reply"
    if len(query) > MAX_QUERY_CHARS:
        return None, f"reply longer than {MAX_QUERY_CHARS} characters"
    return query, None


def normalised(query):
    """The duplicate and exclusion key: case- and whitespace-normalised."""
    return " ".join(query.lower().split())


def generate_one(doc_id, corpus, args):
    """Up to tr.RETRY_BUDGET attempts for one document; every attempt is appended to the ledger.
    Returns the usable query, or None."""
    title, text = corpus[doc_id]
    prompt = GENERATION_PROMPT.format(document=f"{title}\n{text}")
    for _attempt in range(tr.RETRY_BUDGET):
        content, finish_reason, _reasoning, completion_tokens, _prompt_tokens = tr.call_teacher(
            args.base_url, args.model, prompt, args.seed, api_key=args.api_key, extra_body=EXTRA_BODY
        )
        if finish_reason == "length":
            query, reason = None, "finish_reason=length"
        elif finish_reason == "http_error":
            query, reason = None, f"http_error: {content}"
        else:
            query, reason = clean_reply(content)
        tr.append_response(args.ledger, {
            "doc_id": doc_id, "query": query, "reason": reason, "content": content,
            "finish_reason": finish_reason, "completion_tokens": completion_tokens,
        })
        if query is not None:
            return query
    return None


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus", required=True, help="the tenant's BEIR corpus.jsonl (_id, title, text)")
    ap.add_argument("--exclude-queries", required=True, help="the tenant's test queries.jsonl; never kept")
    ap.add_argument("--sample-size", type=int, required=True, help="documents sampled, one query each")
    ap.add_argument("--target", type=int, required=True, help="usable queries to keep, in sample order")
    ap.add_argument("--sample-seed", required=True, help="seed string for the document sample")
    ap.add_argument("--base-url", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--seed", type=int, required=True)
    ap.add_argument("--api-key", default=None)
    ap.add_argument("--concurrency", type=int, default=1)
    ap.add_argument("--ledger", required=True, help="every attempt, appended; must not exist yet")
    ap.add_argument("--out", required=True, help="queries.jsonl to write (_id, text)")
    args = ap.parse_args(argv)
    if os.path.exists(args.ledger):
        sys.exit(f"refusing to start: {args.ledger} exists -- a generation pass starts from an empty ledger")

    corpus = tr.load_corpus(args.corpus)
    excluded = {normalised(text) for text in tr.load_queries(args.exclude_queries).values()}
    if not 0 < args.target <= args.sample_size <= len(corpus):
        sys.exit(f"need 0 < --target ({args.target}) <= --sample-size ({args.sample_size}) "
                 f"<= corpus size ({len(corpus)})")
    sample = sample_documents(corpus, args.sample_size, args.sample_seed)
    with ThreadPoolExecutor(max_workers=args.concurrency) as executor:
        replies = list(executor.map(lambda doc_id: generate_one(doc_id, corpus, args), sample))

    kept, seen, unusable, duplicates, test_matches = [], set(), 0, 0, 0
    for doc_id, query in zip(sample, replies):
        if query is None:
            unusable += 1
            continue
        if normalised(query) in excluded:
            test_matches += 1
            continue
        if normalised(query) in seen:
            duplicates += 1
            continue
        seen.add(normalised(query))
        kept.append({"_id": f"syn-{doc_id}", "text": query})
        if len(kept) == args.target:
            break
    counts = f"{unusable} unusable, {duplicates} duplicate and {test_matches} test-query replies"
    if len(kept) < args.target:
        sys.exit(f"[synth] only {len(kept)} usable queries from {len(sample)} documents ({counts}) "
                 "-- no queries file written")
    with open(args.out, "w", encoding="utf-8") as f:
        for record in kept:
            f.write(json.dumps(record) + "\n")
    print(f"[synth] wrote {args.out} ({len(kept)} queries; {counts} among the documents scanned)")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run the tests** — `cd $S && python3 -m pytest -q test_teacher_rerank.py test_synth_queries.py 2>&1 | tail -1` → `58 passed` (46 + 12).

- [ ] **Step 5: Commit**

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py \
  Iverson.Server/Iverson.LoadTest/scripts/synth_queries.py Iverson.Server/Iverson.LoadTest/scripts/test_synth_queries.py
git -C $R commit -m "add the per-tenant synthetic-query generator, with thinking disabled through call_teacher

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 6: RFDT record writer

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/rfdt_records.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_rfdt_records.py`

**Interfaces:**
- Consumes: Task 1's `build_request(..., "choice", ...)` and per-doc ledger `values`.

- [ ] **Step 1: Write the test file**

```python
"""pytest suite for rfdt_records.py (spec docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md §5.5).
Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_rfdt_records.py -q

Stdlib + pytest only."""
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import jev_rerank as jev  # noqa: E402
import rfdt_records as rr  # noqa: E402
import teacher_rerank as tr  # noqa: E402

def write_inputs(tmp_path, pool, entries):
    """pool: {qid: [docId, ...]}; entries: [(qid, status, values or None)] appended in order."""
    (tmp_path / "pool.trec").write_text("".join(
        f"{q} Q0 {d} {r} {51 - r} bge\n" for q, docs in pool.items() for r, d in enumerate(docs, start=1)))
    docs = sorted({d for ds in pool.values() for d in ds})
    (tmp_path / "corpus.jsonl").write_text("".join(
        json.dumps({"_id": d, "title": f"title {d}", "text": f"text {d}"}) + "\n" for d in docs))
    (tmp_path / "queries.jsonl").write_text("".join(
        json.dumps({"_id": q, "text": f"query {q}"}) + "\n" for q in pool))
    with open(tmp_path / "ledger.jsonl", "w") as f:
        for qid, status, values in entries:
            order = jev.order_by_judge(pool[qid][:len(values)], values) + pool[qid][len(values):] if values else None
            content = json.dumps({"values": values, "metadata": None}) if values else "boom"
            f.write(json.dumps(tr.make_record(qid, status, content, order, None, {})) + "\n")
    return ["--ledger", str(tmp_path / "ledger.jsonl"), "--pool", str(tmp_path / "pool.trec"),
            "--queries", str(tmp_path / "queries.jsonl"), "--corpus", str(tmp_path / "corpus.jsonl"),
            "--out", str(tmp_path / "records.jsonl")]


def records(tmp_path):
    return [json.loads(l) for l in (tmp_path / "records.jsonl").read_text().splitlines()]


def test_record_shape_is_one_choice_question_with_the_judges_yes_probability(tmp_path):
    rr.main(write_inputs(tmp_path, {"q1": ["a", "b"]}, [("q1", "accepted", [0.25, 0.75])]))
    first = records(tmp_path)[0]
    assert first == {
        "id": "q1:a",
        "group_id": "q1",
        "template_version": "v1",
        "request": {
            "model": "student",
            "state": "Search query: query q1",
            "questions": {"d0": {
                "type": "choice",
                "instructions": "Is the following document relevant to the search query?\n\nDocument:\ntext a",
                "criteria": {"no": "The document does not.",
                             "yes": "The document contains information that answers or directly bears on the query."},
            }},
        },
        "targets": {"d0": {"probabilities": {"yes": 0.25, "no": 0.75}}},
    }
    assert list(first["request"]["questions"]["d0"]["criteria"]) == ["no", "yes"]   # label order A=no, B=yes


def test_every_record_request_is_exactly_the_scoring_request(tmp_path):
    rr.main(write_inputs(tmp_path, {"q1": ["a", "b", "c"]}, [("q1", "accepted", [0.1, 0.9, 0.5])]))
    for rec, doc, p in zip(records(tmp_path), "abc", [0.1, 0.9, 0.5]):
        assert rec["id"] == f"q1:{doc}"
        assert rec["request"] == jev.build_request("student", "choice", "query q1", [f"text {doc}"])
        assert rec["targets"]["d0"]["probabilities"] == {"yes": p, "no": pytest.approx(1 - p)}


def test_fallback_queries_are_skipped_and_counted(tmp_path, capsys):
    pool = {"q1": ["a"], "q2": ["b"], "q3": ["c"], "q4": ["d"]}
    entries = [("q1", "accepted", [0.5]), ("q2", "rejected", None), ("q4", "rejected", None), ("q4", "accepted", [0.2])]
    rr.main(write_inputs(tmp_path, pool, entries))
    assert [r["id"] for r in records(tmp_path)] == ["q1:a", "q4:d"]
    out = capsys.readouterr().out
    assert "2 records from 2 accepted queries; 2 of 4 queries skipped (no accepted entry): ['q2', 'q3']" in out


def test_only_the_labelled_prefix_becomes_records_under_the_top_n_rule(tmp_path):
    rr.main(write_inputs(tmp_path, {"q1": ["a", "b", "c", "d"]}, [("q1", "accepted", [0.3, 0.8])]))
    assert [(r["id"], r["targets"]["d0"]["probabilities"]["yes"]) for r in records(tmp_path)] == [("q1:a", 0.3), ("q1:b", 0.8)]


def test_records_carry_the_document_text_not_its_title(tmp_path):
    rr.main(write_inputs(tmp_path, {"q1": ["a"]}, [("q1", "accepted", [0.5])]))
    instructions = records(tmp_path)[0]["request"]["questions"]["d0"]["instructions"]
    assert instructions.endswith("Document:\ntext a") and "title a" not in instructions


def test_a_pool_that_does_not_match_the_ledger_is_refused(tmp_path):
    argv = write_inputs(tmp_path, {"q1": ["a", "b", "c"]}, [("q1", "accepted", [0.1, 0.9, 0.5])])
    (tmp_path / "pool.trec").write_text("q1 Q0 c 1 3 x\nq1 Q0 b 2 2 x\nq1 Q0 a 3 1 x\n")   # same docs, reranked
    with pytest.raises(SystemExit) as exc:
        rr.main(argv)
    assert "do not line up with --pool" in str(exc.value)


def test_more_values_than_pool_documents_is_refused(tmp_path):
    argv = write_inputs(tmp_path, {"q1": ["a", "b"]}, [("q1", "accepted", [0.1, 0.9])])
    (tmp_path / "pool.trec").write_text("q1 Q0 a 1 2 x\n")
    with pytest.raises(SystemExit) as exc:
        rr.main(argv)
    assert "(2 values, 1 pool documents)" in str(exc.value)
```

- [ ] **Step 2: Run and see it fail** — `ModuleNotFoundError: No module named 'rfdt_records'`.

- [ ] **Step 3: Write** `rfdt_records.py`

```python
#!/usr/bin/env python3
"""RFDT training records from a jev_rerank --per-doc ledger (spec
docs/specs/2026-10-05-rfdt-per-tenant-adaptation-design.md §5.5): the RFDT/prepare.py input for one tenant.

One record per (query, labelled pool document): the request is exactly what the student is scored
with -- jev_rerank.build_request(..., "choice", query, [document text]), one `choice` question d0
whose criteria are the judge's binary surrogate {no, yes} -- and the target is the judge's P(yes),
`{"probabilities": {"yes": p, "no": 1 - p}}`, with p the ledger's value for that document.
`group_id` is the query id, so RFDT's split keeps a query's records together. Ledger values are in
pool order (d0..d{n-1}); n may be below the pool size when Phase 0's top-20 rule applied. A query
with no accepted ledger entry (a fallback) has no labels and is skipped and counted.

    python3 rfdt_records.py --ledger $A/phase1/scifact/labels.responses.jsonl \\
        --pool $A/phase1/scifact/pools.trec \\
        --queries $A/phase1/scifact/synthetic-queries.jsonl \\
        --corpus $B/scifact-bge-base-2026-09-04/beir/corpus.jsonl \\
        --out $A/phase1/scifact/records.jsonl

Stdlib only."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import jev_rerank as jev  # noqa: E402  (build_request, order_by_judge -- the one request shape)
import teacher_rerank as tr  # noqa: E402  (loaders, ledger reader, input check)

REQUEST_MODEL = "student"  # required by the request schema; RFDT's --model picks the trained model
TEMPLATE_VERSION = "v1"


def query_records(query_id, query_text, pool_ids, values, corpus):
    """One record per labelled document: values[i] is the judge's P(yes) for pool_ids[i]."""
    return [{
        "id": f"{query_id}:{doc_id}",
        "group_id": query_id,
        "template_version": TEMPLATE_VERSION,
        "request": jev.build_request(REQUEST_MODEL, "choice", query_text, [corpus[doc_id][1]]),
        "targets": {"d0": {"probabilities": {"yes": p, "no": 1 - p}}},
    } for doc_id, p in zip(pool_ids, values)]


def labelled_values(query_id, pool_ids, entry):
    """The accepted entry's values, refused unless they line up with this pool: at most one per
    document, and the entry's order ranks the labelled documents exactly as their values do (so a
    reranked run passed as --pool, or another pass's ledger, cannot mislabel silently)."""
    values = json.loads(entry["content"])["values"]
    labelled = pool_ids[:len(values)]
    ranked = [doc_id for doc_id in entry.get("order") or [] if doc_id in set(labelled)]
    if len(values) > len(pool_ids) or ranked != jev.order_by_judge(labelled, values):
        sys.exit(f"[rfdt_records] query {query_id!r}: the ledger's values do not line up with --pool "
                 f"({len(values)} values, {len(pool_ids)} pool documents)")
    return values


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ledger", required=True, help="jev_rerank --per-doc responses ledger")
    ap.add_argument("--pool", required=True, help="the TREC pool that ledger judged")
    ap.add_argument("--queries", required=True, help="queries.jsonl (_id, text)")
    ap.add_argument("--corpus", required=True, help="BEIR corpus.jsonl (_id, title, text)")
    ap.add_argument("--out", required=True, help="RFDT records JSONL to write")
    args = ap.parse_args(argv)

    pool = tr.load_run(args.pool)
    corpus = tr.load_corpus(args.corpus)
    queries = tr.load_queries(args.queries)
    tr.validate_inputs_before_any_model_call(pool, list(pool), corpus, queries)
    ledger = tr.read_responses_ledger(args.ledger)

    records, skipped = [], []
    for query_id, pool_ids in pool.items():
        entry = tr.accepted_entry(ledger.get(query_id, []))
        if entry is None:
            skipped.append(query_id)
            continue
        values = labelled_values(query_id, pool_ids, entry)
        records += query_records(query_id, queries[query_id], pool_ids, values, corpus)
    with open(args.out, "w", encoding="utf-8") as f:
        for record in records:
            f.write(json.dumps(record, ensure_ascii=False) + "\n")
    print(f"[rfdt_records] wrote {args.out}: {len(records)} records from {len(pool) - len(skipped)} "
          f"accepted queries; {len(skipped)} of {len(pool)} queries skipped (no accepted entry): {skipped}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run the whole touched suite**

```bash
cd $S && python3 -m pytest -q test_jev_rerank.py test_report.py test_teacher_rerank.py test_rrf_fuse.py \
  test_tenant_pools.py test_synth_queries.py test_rfdt_records.py 2>&1 | tail -1
```

Expected: `213 passed` (one pre-existing warning).

- [ ] **Step 5: Commit**

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/rfdt_records.py Iverson.Server/Iverson.LoadTest/scripts/test_rfdt_records.py
git -C $R commit -m "add the RFDT record writer for judge ledgers

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 7: Phase 0 — the judge one document per request (PAID)

**Files:** none in the repo. Artifacts: `$A/phase0/`.

**Interfaces:**
- Consumes: Tasks 1, 2, 3, 6.
- Produces: per-tenant verdicts in `$A/phase0/verdict.txt`; `$A/phase0/label-top.txt`; Phase 0 ledgers with timing; the smoke-test record in `$A/phase0/pod.md`.

Pod shells share these variables. If the pod shell is lost, paste this block again (and `LABEL_TOP=$(cat $O/label-top.txt)` once Step 8 has run):

```bash
export HF_HOME=/workspace/hf
REPO=Qwen/Qwen3.8-27B; SHA=1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0; POLICY=shared_examples_binary
SJ_COMMIT=7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3; STUDENT=Qwen/Qwen3-Reranker-0.6B
I=/workspace/in; O=/workspace/out
PROV="--server-commit $SJ_COMMIT --revision $SHA --policy $POLICY --max-model-len 16384"
```

- [ ] **Step 1: STOP and ask Ben for the go-ahead.** State: one 80 GB card (H100 or A100), PyTorch template, Phase 0 ceiling $35 (overrun charged to Phase 1), and that Tasks 1–6 are committed and the fidelity gate passed for both tenants.

- [ ] **Step 2: Rent the pod.** RunPod, one 80 GB card, a **PyTorch** template (not `vllm/vllm-openai`), container disk ≥ 50 GB, **volume disk ≥ 150 GB** (`HF_HOME` is on `/workspace`). Record the launch time (UTC) and the hourly price in `$A/phase0/pod.md` on the dev box (`mkdir -p $A/phase0` first).

- [ ] **Step 3: Check the image**

```bash
python3 --version
python3 -c "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0))"
df -h /workspace; nvidia-smi --query-gpu=memory.total --format=csv
```

Required: Python ≥ 3.12, torch ≥ 2.6, `True`, ≥ 100 GB free on `/workspace`, ~80 GB card. Otherwise terminate and re-rent.

- [ ] **Step 4: Install SimpleJEV + RFDT at the pinned commit and fetch both models**

```bash
set -e
cd /workspace && git clone https://github.com/featherless-ai/simple-jev.git && cd simple-jev && git checkout $SJ_COMMIT
python3 -m pip install -e './hf-server' && python3 -m pip install -r RFDT/requirements.txt
python3 -c "from huggingface_hub import snapshot_download as s; s('$REPO', revision='$SHA')"
python3 -c "from huggingface_hub import HfApi; print(HfApi().model_info('$STUDENT').sha)" | tee /workspace/student-sha.txt
STUDENT_SHA=$(cat /workspace/student-sha.txt)
python3 -c "from huggingface_hub import snapshot_download as s; s('$STUDENT', revision='$STUDENT_SHA')"
mkdir -p $I $O
```

Record `STUDENT_SHA` in `pod.md`; from here on `STUDENT_SHA=$(cat /workspace/student-sha.txt)` belongs to the recovery block.

- [ ] **Step 5: Build and send the input bundle (dev box)**

```bash
set -e
P0=$A/phase0; mkdir -p $P0/bundle/{scripts,sf,fs}
cp $S/{jev_rerank,teacher_rerank,rfdt_records}.py $P0/bundle/scripts/
cp $SF/beir/{corpus,queries}.jsonl $P0/bundle/sf/; cp $SF/runs/bge-base.chunks.trec $P0/bundle/sf/pool.trec
cp $FS/beir/{corpus,queries}.jsonl $P0/bundle/fs/; cp $FS/runs/fs-2048-l070.chunks.trec $P0/bundle/fs/pool.trec
python3 - $P0/bundle <<'PY'
# Pre-flight sub-pools (spec §4): a seeded 100-pair sample per tenant (timing) and the 5 longest
# (query, document) pairs per tenant (memory). Each written as a TREC sub-pool, documents in pool order.
import json, random, sys
root = sys.argv[1]
for t in ("sf", "fs"):
    pool = {}
    for line in open(f"{root}/{t}/pool.trec"):
        q, _, d, *_ = line.split(); pool.setdefault(q, []).append(d)
    text = {json.loads(l)["_id"]: json.loads(l).get("text", "") for l in open(f"{root}/{t}/corpus.jsonl")}
    query = {json.loads(l)["_id"]: json.loads(l)["text"] for l in open(f"{root}/{t}/queries.jsonl")}
    pairs = sorted((q, d) for q, ds in pool.items() for d in ds)
    picks = {"sample": random.Random("20261005-preflight").sample(pairs, 100),
             "longest": sorted(pairs, key=lambda p: -(len(text[p[1]]) + len(query[p[0]])))[:5]}
    for name, chosen in picks.items():
        keep = set(chosen)
        with open(f"{root}/{t}/pre-{name}.trec", "w") as f:
            for q, ds in pool.items():
                for rank, d in enumerate([d for d in ds if (q, d) in keep], start=1):
                    f.write(f"{q} Q0 {d} {rank} {51 - rank} pre\n")
PY
cd $P0 && tar czf in.tar.gz -C bundle .
test "$(tar tzf in.tar.gz | command grep -c -E 'jev_rerank.py|teacher_rerank.py|rfdt_records.py|pool.trec|corpus.jsonl|queries.jsonl|pre-sample.trec|pre-longest.trec')" -eq 13
md5sum in.tar.gz && runpodctl send in.tar.gz
```

On the pod: `cd $I && runpodctl receive <code> && md5sum in.tar.gz && tar xzf in.tar.gz && ls $I/scripts $I/sf $I/fs` (md5 must match).

- [ ] **Step 6: Launch the judge**

```bash
cd /workspace && setsid nohup env ENABLE_OPEN_JEV_ADVANCED_METRICS=1 HF_HOME=$HF_HOME simple-jev \
  --model $REPO --revision $SHA --served-model-name $REPO --enforce-model-id \
  --classifier-prompt-policy $POLICY --max-model-len 16384 --dtype bfloat16 > /workspace/judge.log 2>&1 &
until curl -sf 127.0.0.1:8000/health; do sleep 15; done; echo; tail -n 3 /workspace/judge.log
```

- [ ] **Step 7: Pre-flight — memory on the longest prompts**

```bash
nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -lms 500 > $O/mem-pre.log & MPID=$!
for t in sf fs; do
  python3 $I/scripts/jev_rerank.py --pool $I/$t/pre-longest.trec --corpus $I/$t/corpus.jsonl --queries $I/$t/queries.jsonl \
    --base-url http://127.0.0.1:8000 --model $REPO --per-doc --responses $O/pre-longest-$t.responses.jsonl \
    --out $O/pre-longest-$t.chunks.trec --composite preflight; echo "$t exit $?"
done
kill $MPID; sort -n $O/mem-pre.log | tail -1
```

Required: both `exit 0`; peak memory (MiB) below the card's total. A 5xx / OOM here: **STOP** and report (the 27B and a ≤ 13.1K-token prompt are expected to fit 80 GB, spec V4).

- [ ] **Step 8: Pre-flight — timing, projection and `LABEL_TOP`**

```bash
for t in sf fs; do
  python3 $I/scripts/jev_rerank.py --pool $I/$t/pre-sample.trec --corpus $I/$t/corpus.jsonl --queries $I/$t/queries.jsonl \
    --base-url http://127.0.0.1:8000 --model $REPO --per-doc --responses $O/pre-sample-$t.responses.jsonl \
    --out $O/pre-sample-$t.chunks.trec --composite preflight; echo "$t exit $?"
done
PRICE=<the card's $/h>   # from pod.md
python3 - $O $PRICE <<'PY'
import json, sys
out, price = sys.argv[1], float(sys.argv[2])
rate = {}
for t in ("sf", "fs"):
    secs = reqs = 0
    for r in map(json.loads, open(f"{out}/pre-sample-{t}.responses.jsonl")):
        if r["status"] == "accepted":
            c = json.loads(r["content"]); secs += c["requestSeconds"]; reqs += c["requests"]
    rate[t] = secs / reqs
requests = {"sf": 15000 + 2500, "fs": 33600}   # SciFact main + repeat; FreshStack main (spec §4)
full = sum(rate[t] * requests[t] for t in rate) / 3600 * price
top20 = full * 20 / 50
label_top = 50 if full <= 35 else 20
print(f"s/request sf {rate['sf']:.3f} fs {rate['fs']:.3f}; projection full ${full:.2f}, top-20 ${top20:.2f}; LABEL_TOP {label_top}"
      + ("  (top-20 still over $35: run anyway, overrun charged to Phase 1 -- spec §4)" if label_top == 20 and top20 > 35 else ""))
open(f"{out}/label-top.txt", "w").write(f"{label_top}\n")
PY
LABEL_TOP=$(cat $O/label-top.txt)
```

Record the printed line in `pod.md`.

- [ ] **Step 9: SciFact main pass** (detached; ≈ the projection's SciFact share)

```bash
cd /workspace && setsid nohup sh -c "date -u +%FT%TZ; python3 $I/scripts/jev_rerank.py --pool $I/sf/pool.trec \
  --corpus $I/sf/corpus.jsonl --queries $I/sf/queries.jsonl --base-url http://127.0.0.1:8000 --model $REPO \
  --per-doc --label-top $LABEL_TOP --responses $O/sf-main.responses.jsonl --out $O/sf-main.chunks.trec \
  --composite 7d3a15092f963723 $PROV; echo exit \$?; date -u +%FT%TZ" > $O/sf-main.log 2>&1 &
```

Progress (any shell, after the recovery block): `python3 -c "import json,collections,sys; print(collections.Counter((r['status'], r['reason'] and r['reason'].split(': ')[-1].split(':')[0]) for r in map(json.loads, open(sys.argv[1]))))" $O/sf-main.responses.jsonl`. Done when `$O/sf-main.log` ends with `exit 0` and a date. A rising `rejected` count of HTTP 500: read `judge.log` before anything else.

- [ ] **Step 10: SciFact repeat pass** (after Step 9 finishes)

```bash
cd /workspace && setsid nohup sh -c "date -u +%FT%TZ; python3 $I/scripts/jev_rerank.py --pool $I/sf/pool.trec \
  --corpus $I/sf/corpus.jsonl --queries $I/sf/queries.jsonl --base-url http://127.0.0.1:8000 --model $REPO \
  --per-doc --label-top $LABEL_TOP --subsample 50 --subsample-seed 20260929 \
  --responses $O/sf-repeat.responses.jsonl --out $O/sf-repeat.chunks.trec \
  --composite 7d3a15092f963723 $PROV; echo exit \$?; date -u +%FT%TZ" > $O/sf-repeat.log 2>&1 &
```

- [ ] **Step 11: FreshStack main pass** (after Step 10; the longest pass)

```bash
cd /workspace && setsid nohup sh -c "date -u +%FT%TZ; python3 $I/scripts/jev_rerank.py --pool $I/fs/pool.trec \
  --corpus $I/fs/corpus.jsonl --queries $I/fs/queries.jsonl --base-url http://127.0.0.1:8000 --model $REPO \
  --per-doc --label-top $LABEL_TOP --responses $O/fs-main.responses.jsonl --out $O/fs-main.chunks.trec \
  --composite 9714c660b365fad1 $PROV; echo exit \$?; date -u +%FT%TZ" > $O/fs-main.log 2>&1 &
```

- [ ] **Step 12: Metadata cross-check** — every accepted entry's metadata names the pinned revision and policy

```bash
python3 - $O <<'PY'
import json, sys
for name in ("sf-main", "sf-repeat", "fs-main"):
    seen = set()
    for r in map(json.loads, open(f"{sys.argv[1]}/{name}.responses.jsonl")):
        if r["status"] == "accepted":
            m = json.loads(r["content"])["metadata"] or {}
            seen.add((m.get("model_revision"), m.get("prompt_policy")))
    print(name, seen)
PY
```

Required: each prints exactly `{('1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0', 'shared_examples_binary')}`.

- [ ] **Step 13: Bring the Phase 0 outputs back**

```bash
cp /workspace/judge.log $O/ && tar czf /workspace/phase0.tar.gz -C $O . && md5sum /workspace/phase0.tar.gz && runpodctl send /workspace/phase0.tar.gz
```

Dev box: `cd $A/phase0 && runpodctl receive <code> && md5sum phase0.tar.gz && tar xzf phase0.tar.gz && rm phase0.tar.gz` (md5 must match).

- [ ] **Step 14: Score the gate (dev box)**

```bash
cd $A/phase0
python3 $S/report.py --run sf-main.chunks.trec --run $SF/runs/bge-base.chunks.trec --qrels $SF/qrels.trec \
  --pair sf-main.chunks.trec=$SF/runs/bge-base.chunks.trec > report-sf.txt 2>&1; echo "sf exit $?"
python3 $S/report.py --run fs-main.chunks.trec --run $FS/runs/fs-2048-l070.chunks.trec --qrels $FS/qrels.trec \
  --pair fs-main.chunks.trec=$FS/runs/fs-2048-l070.chunks.trec > report-fs.txt 2>&1; echo "fs exit $?"
python3 $S/report.py --run sf-main.chunks.trec --run $B/simple-jev-2026-09/pod/jev-main.chunks.trec --qrels $SF/qrels.trec \
  --pair sf-main.chunks.trec=$B/simple-jev-2026-09/pod/jev-main.chunks.trec > report-sf-vs-all50.txt 2>&1; echo "all50 exit $?"
command grep -A3 "\[compare\].*(nDCG@10)" report-sf.txt report-fs.txt report-sf-vs-all50.txt
command grep -h "fallbackCount" sf-main.meta.json fs-main.meta.json
python3 - <<'PY'
# Determinism on the 50-query repeat (spec §4): bit-identical per-document values.
import json
def load(p): return {r["query_id"]: json.loads(r["content"])["values"] for r in map(json.loads, open(p)) if r["status"] == "accepted"}
m, r = load("sf-main.responses.jsonl"), load("sf-repeat.responses.jsonl")
both = [q for q in r if q in m]
same = sum(m[q] == r[q] for q in both)
print(f"repeat: {same}/{len(both)} queries bit-identical ({len(r) - len(both)} repeat queries fell back in the main pass); "
      f"max |delta| {max((max(abs(a - b) for a, b in zip(m[q], r[q])) for q in both), default=0.0):.6f}")
PY
```

Verdict per tenant from its `[compare] … (nDCG@10)` block, with `exit 0`, R@50 delta exactly +0.0000 and `fallbackCount` ≤ 15 (SciFact) / ≤ 33 (FreshStack): **PASS iff permutation p < 0.05 AND delta ≥ +0.047 (SciFact) / ≥ +0.0956 (FreshStack)**. The all-50 comparison is reported only (it uses Task 3's reranker-vs-reranker rule; any exit code is recorded, not acted on). Write `$A/phase0/verdict.txt`: one line per tenant, `scifact PASS|FAIL delta p` and `freshstack PASS|FAIL delta p`.

If **both FAIL**: terminate the pod (record the time in `pod.md`) and go to Task 9 Step 7 (the write-up). Otherwise continue.

- [ ] **Step 15: Student toolchain smoke test (pod; spec §4, Ben CDR-1 §3.2)**

```bash
set -e
pkill -f "simple-jev --model" || true; sleep 10
STUDENT_SHA=$(cat /workspace/student-sha.txt); SM=$O/smoke; mkdir -p $SM
echo "smoke start $(date -u +%FT%TZ)" | tee $SM/smoke.log
python3 - $O $I $SM <<'PY'
# The two FreshStack queries whose labelled documents are longest (RFDT's split needs >= 2 groups).
import json, sys
out, inp, sm = sys.argv[1:4]
text = {json.loads(l)["_id"]: json.loads(l).get("text", "") for l in open(f"{inp}/fs/corpus.jsonl")}
pool = {}
for line in open(f"{inp}/fs/pool.trec"):
    q, _, d, *_ = line.split(); pool.setdefault(q, []).append(d)
acc = [r for r in map(json.loads, open(f"{out}/fs-main.responses.jsonl")) if r["status"] == "accepted"]
n = {r["query_id"]: len(json.loads(r["content"])["values"]) for r in acc}
best = sorted(n, key=lambda q: -max(len(text[d]) for d in pool[q][:n[q]]))[:2]
with open(f"{sm}/ledger.jsonl", "w") as f:
    for r in acc:
        if r["query_id"] in best:
            f.write(json.dumps(r) + "\n")
with open(f"{sm}/pool.trec", "w") as f:
    for line in open(f"{inp}/fs/pool.trec"):
        if line.split()[0] in best:
            f.write(line)
print("smoke queries", best)
PY
python3 $I/scripts/rfdt_records.py --ledger $SM/ledger.jsonl --pool $SM/pool.trec --queries $I/fs/queries.jsonl \
  --corpus $I/fs/corpus.jsonl --out $SM/records.jsonl
python3 /workspace/simple-jev/RFDT/prepare.py --input $SM/records.jsonl --output $SM/data
nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits -lms 500 > $SM/mem.log & MPID=$!
python3 /workspace/simple-jev/RFDT/train.py --model $STUDENT --revision $STUDENT_SHA --train $SM/data/train.jsonl \
  --validation $SM/data/validation.jsonl --output $SM/adapter --lora --lora-rank 16 --learning-rate 0.0001 \
  --dtype bfloat16 --batch-size 2 --gradient-accumulation 8 --gradient-checkpointing --max-length 16384 --max-steps 2
kill $MPID; echo "train peak MiB $(sort -n $SM/mem.log | tail -1)" | tee -a $SM/smoke.log
python3 /workspace/simple-jev/RFDT/export.py --model $STUDENT --revision $STUDENT_SHA --adapter $SM/adapter --output $SM/merged
setsid nohup simple-jev --model $SM/merged --served-model-name student --enforce-model-id \
  --classifier-prompt-policy baseline --max-model-len 16384 --dtype bfloat16 --port 8001 > $SM/serve.log 2>&1 &
until curl -sf 127.0.0.1:8001/health; do sleep 5; done
python3 $I/scripts/jev_rerank.py --pool $SM/pool.trec --corpus $I/fs/corpus.jsonl --queries $I/fs/queries.jsonl \
  --base-url http://127.0.0.1:8001 --model student --question-type choice --per-doc --subsample 1 --subsample-seed 1 \
  --responses $SM/score.responses.jsonl --out $SM/score.chunks.trec --composite smoke
pkill -f "simple-jev --model $SM/merged"
echo "smoke end $(date -u +%FT%TZ)" | tee -a $SM/smoke.log
```

Required: every command exits 0 (`set -e` stops at the first failure); `jev_rerank` prints `wrote … (1 queries, 0 fallbacks)`. Any failure: **STOP before Phase 1** and report it. Record the train peak memory and `smoke end` in `pod.md` — Phase 0's spend ends here (spec §7). Leave the pod running for Task 8.

### Task 8: Phase 1 — synthetic queries, pools and labels (PAID)

**Files:** none in the repo. Artifacts: `$A/phase1/`.

**Interfaces:**
- Consumes: Task 7's verdicts, `LABEL_TOP`, ledgers (rates); Task 4's dumps and `$A/fidelity.txt`; Tasks 5, 6.
- Produces: per passing tenant `$A/phase1/<t>/data/{train,validation}.jsonl` (on the pod at `$O/p1/<t>/data/`).

Shells: dev-box blocks use the Tasks 1–6 prelude; pod-A blocks use Task 7's recovery block plus `STUDENT_SHA=$(cat /workspace/student-sha.txt)` and `LABEL_TOP=$(cat $O/label-top.txt)`.

Tenant names: `scifact` (pod short name `sf`, chunk multiplier 5, corpus `$SF`) and `freshstack` (`fs`, 11, `$FS`). Run each per-tenant block only for tenants with `PASS` in `$A/phase0/verdict.txt`.

- [ ] **Step 1: Rent the generation pod.** RunPod, one 80 GB card, the vLLM template (`vllm/vllm-openai:latest`). **Before first boot**, set the container start command to `--model Qwen/Qwen3.8-27B --revision 1d4bf0f2ff6012fd82039f2fa52739d0dd7c60c0 --max-model-len 16384` (the template otherwise auto-launches its default model). Keep the template's `VLLM_API_KEY`. Record launch time and price in `$A/phase1/pods.md`. When it is up: `curl -s -H "Authorization: Bearer $VLLM_API_KEY" 127.0.0.1:8000/v1/models` lists `Qwen/Qwen3.8-27B`.

- [ ] **Step 2: Send the generator bundle (dev box)**

```bash
set -e
P1=$A/phase1; mkdir -p $P1/gen-bundle/{scripts,sf,fs}
cp $S/{synth_queries,teacher_rerank}.py $P1/gen-bundle/scripts/
cp $SF/beir/{corpus,queries}.jsonl $P1/gen-bundle/sf/; cp $FS/beir/{corpus,queries}.jsonl $P1/gen-bundle/fs/
cd $P1 && tar czf gen.tar.gz -C gen-bundle . && test "$(tar tzf gen.tar.gz | command grep -c -E 'synth_queries.py|teacher_rerank.py|corpus.jsonl|queries.jsonl')" -eq 6
md5sum gen.tar.gz && runpodctl send gen.tar.gz
```

On the generation pod: `mkdir -p /workspace/gen && cd /workspace/gen && runpodctl receive <code> && md5sum gen.tar.gz && tar xzf gen.tar.gz`.

- [ ] **Step 3: Generate 1,000 queries per passing tenant (generation pod)**

```bash
cd /workspace/gen
for t in sf fs; do   # drop a tenant that did not pass
  python3 scripts/synth_queries.py --corpus $t/corpus.jsonl --exclude-queries $t/queries.jsonl \
    --sample-size 1100 --target 1000 --sample-seed 20261005 \
    --base-url http://127.0.0.1:8000 --model Qwen/Qwen3.8-27B --seed 20261005 \
    --api-key "$VLLM_API_KEY" --concurrency 24 \
    --ledger $t-generation.jsonl --out $t-synthetic-queries.jsonl; echo "$t exit $?"
done
tar czf gen-out.tar.gz *-generation.jsonl *-synthetic-queries.jsonl && md5sum gen-out.tar.gz && runpodctl send gen-out.tar.gz
```

Required: each `exit 0` with `wrote … (1000 queries; …)`. Also run `command grep -c '<think>\|</think>' *-synthetic-queries.jsonl` in `/workspace/gen` and require the printed count `0` for every file (`grep -c` exits 1 when the count is 0; judge the printed count, not the exit code) — `enable_thinking: false` (P17) is the only guard, and a short leaked think block would otherwise become a kept training query. Fewer than 1,000 usable: rerun with `--sample-size 1300` and a fresh `--ledger` name. Dev box: `cd $A/phase1 && runpodctl receive <code>`, check md5, `tar xzf gen-out.tar.gz`. **Terminate the generation pod**; record its end time and cost in `pods.md`.

- [ ] **Step 4: Pools for the synthetic queries (dev box)** — restart Task 4's containers and use the retrieval mode `$A/fidelity.txt` names for the tenant

```bash
set -e
export QDRANT__SERVICE__API_KEY=rfdt-local-qdrant-key-0123456789abcdef
docker start rfdt-qdrant rfdt-tei; until curl -sf http://127.0.0.1:18091/health; do sleep 5; done
cd $A/phase1
python3 $S/tenant_pools.py pools --dump-dir $A/dumps/scifact --tei http://127.0.0.1:18091 \
  --queries sf-synthetic-queries.jsonl --chunk-multiplier 5 --retrieval exact --out sf-pools.trec
python3 $S/tenant_pools.py pools --dump-dir $A/dumps/freshstack --tei http://127.0.0.1:18091 \
  --queries fs-synthetic-queries.jsonl --chunk-multiplier 11 --retrieval exact --out fs-pools.trec
docker stop rfdt-qdrant rfdt-tei
```

If `$A/fidelity.txt` says `qdrant` for a tenant: run `source $A/restore.sh && restore <that tenant's snapshot dir>` first and use `--retrieval qdrant --qdrant http://127.0.0.1:16333`.

- [ ] **Step 5: Size Phase 1 — one N for every passing tenant (dev box)**

```bash
PRICE_A=<pod A $/h>; GEN_COST=<generation pod $ from pods.md>; P0_SPEND=<(smoke end - pod A launch) h x PRICE_A>
python3 - $A $PRICE_A $GEN_COST $P0_SPEND <<'PY'
# Spec §5.4: each passing tenant priced at its own Phase 0 main-pass rate; one N for all (Ben, plan Section 4).
import json, sys
a, price, gen, p0 = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
passing = [l.split()[0] for l in open(f"{a}/phase0/verdict.txt") if " PASS " in f" {l} "]
short = {"scifact": "sf", "freshstack": "fs"}
label_top = int(open(f"{a}/phase0/label-top.txt").read())
per_query = 0.0
for t in passing:
    secs = reqs = 0
    for r in map(json.loads, open(f"{a}/phase0/{short[t]}-main.responses.jsonl")):
        if r["status"] == "accepted":
            c = json.loads(r["content"]); secs += c["requestSeconds"]; reqs += c["requests"]
    per_query += secs / reqs * label_top / 3600 * price   # $ per synthetic query for this tenant
remaining = 40 - max(0.0, p0 - 35) - gen
n = min(1000, int(remaining / per_query)) if per_query else 0
print(f"passing {passing}; remaining ${remaining:.2f}; $/query (all passing) {per_query:.4f}; N = {n}")
open(f"{a}/phase1/n.txt", "w").write(f"{n}\n")
if n < 300:
    sys.exit("N < 300: STOP and ask Ben (spec §5.4)")
PY
```

If it exits with `N < 300`: **STOP** and ask Ben (accept fewer, or spend more).

- [ ] **Step 6: Cut the first N queries and send the labelling bundle (dev box)**

```bash
set -e
N=$(cat $A/phase1/n.txt); cd $A/phase1; mkdir -p lab-bundle/sf lab-bundle/fs
for t in sf fs; do   # passing tenants only
  head -n $N $t-synthetic-queries.jsonl > lab-bundle/$t/queries.jsonl
  python3 -c "import json,sys; keep={json.loads(l)['_id'] for l in open(sys.argv[1])}; [sys.stdout.write(l) for l in open(sys.argv[2]) if l.split()[0] in keep]" \
    lab-bundle/$t/queries.jsonl $t-pools.trec > lab-bundle/$t/pools.trec
  echo "$t: $(wc -l < lab-bundle/$t/queries.jsonl) queries, $(cut -d' ' -f1 lab-bundle/$t/pools.trec | sort -u | wc -l) pools"
done
tar czf lab.tar.gz -C lab-bundle . && md5sum lab.tar.gz && runpodctl send lab.tar.gz
```

Pod A: `mkdir -p $I/p1 && cd $I/p1 && runpodctl receive <code> && md5sum lab.tar.gz && tar xzf lab.tar.gz`.

- [ ] **Step 7: Label (pod A)** — relaunch the judge (Task 7 Step 6's command), then per passing tenant, one after the other:

```bash
LABEL_TOP=$(cat $O/label-top.txt); mkdir -p $O/p1
t=sf   # then t=fs
cd /workspace && setsid nohup sh -c "date -u +%FT%TZ; python3 $I/scripts/jev_rerank.py --pool $I/p1/$t/pools.trec \
  --corpus $I/$t/corpus.jsonl --queries $I/p1/$t/queries.jsonl --base-url http://127.0.0.1:8000 --model $REPO \
  --per-doc --label-top $LABEL_TOP --responses $O/p1/$t-labels.responses.jsonl --out $O/p1/$t-labels.chunks.trec \
  --composite synthetic-$t $PROV; echo exit \$?; date -u +%FT%TZ" > $O/p1/$t-labels.log 2>&1 &
```

Done when the log ends `exit 0`. A refused run (fallbacks over 5%): **STOP** (spec §8).

- [ ] **Step 8: RFDT records and split (pod A)**

```bash
set -e
for t in sf fs; do   # passing tenants only
  mkdir -p $O/p1/$t
  python3 $I/scripts/rfdt_records.py --ledger $O/p1/$t-labels.responses.jsonl --pool $I/p1/$t/pools.trec \
    --queries $I/p1/$t/queries.jsonl --corpus $I/$t/corpus.jsonl --out $O/p1/$t/records.jsonl
  python3 /workspace/simple-jev/RFDT/prepare.py --input $O/p1/$t/records.jsonl --output $O/p1/$t/data
done
```

Required: `prepare.py` prints `Wrote … training and … validation records` per tenant. A rejected record: **STOP** (spec §8).

### Task 9: Phase 2 — students, verdicts and the gate doc (PAID, then local)

**Files:**
- Create: `docs/plans/2026-10-GATE-rfdt-per-tenant-adaptation.md`
- Modify: `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` (one row after line 51)

**Interfaces:**
- Consumes: Task 8's train/validation files; Task 3's pair rule; Task 7's verdicts and Phase 0 deltas.

Shells: dev-box blocks use the Tasks 1–6 prelude; pod-A blocks use Task 7's recovery block plus `STUDENT_SHA=$(cat /workspace/student-sha.txt)` and `LABEL_TOP=$(cat $O/label-top.txt)`.

- [ ] **Step 1: Train one adapter per passing tenant (pod A)** — stop the judge first; one tenant at a time

```bash
pkill -f "simple-jev --model" || true; sleep 10
STUDENT_SHA=$(cat /workspace/student-sha.txt); mkdir -p $O/p2
t=sf   # then t=fs
cd /workspace && setsid nohup sh -c "date -u +%FT%TZ; python3 /workspace/simple-jev/RFDT/train.py --model $STUDENT \
  --revision $STUDENT_SHA --train $O/p1/$t/data/train.jsonl --validation $O/p1/$t/data/validation.jsonl \
  --output $O/p2/$t-adapter --lora --lora-rank 16 --learning-rate 0.0001 --epochs 3 --dtype bfloat16 \
  --batch-size 2 --gradient-accumulation 8 --gradient-checkpointing --max-length 16384; echo exit \$?; date -u +%FT%TZ" \
  > $O/p2/$t-train.log 2>&1 &
```

Done when the log ends `exit 0`; `$O/p2/$t-adapter/eval_results.json` exists. CUDA OOM: rerun with `--batch-size 1 --gradient-accumulation 16` (spec §8). A too-long row: **STOP** (spec §8).

- [ ] **Step 2: Merge each adapter**

```bash
for t in sf fs; do   # passing tenants only
  python3 /workspace/simple-jev/RFDT/export.py --model $STUDENT --revision $STUDENT_SHA \
    --adapter $O/p2/$t-adapter --output $O/p2/$t-merged; echo "$t exit $?"
done
```

- [ ] **Step 3: Score every arm on every test pool it applies to** — one student server at a time on port 8001

Arms: `s0` = `$STUDENT` (with `--revision $STUDENT_SHA`), `sf` = `$O/p2/sf-merged`, `fs` = `$O/p2/fs-merged`. Score each arm that exists on each passing tenant's test pool, plus each adapter on the other tenant's test pool (cross, or the "scored on both test sets" case of spec §6.4). Composites: sf `7d3a15092f963723`, fs `9714c660b365fad1`.

```bash
score() {  # $1 arm name, $2 model path or id, $3 extra launch flags, then test tenants
  arm=$1; model=$2; extra=$3; shift 3
  setsid nohup simple-jev --model $model $extra --served-model-name student --enforce-model-id \
    --classifier-prompt-policy baseline --max-model-len 16384 --dtype bfloat16 --port 8001 > $O/p2/serve-$arm.log 2>&1 &
  until curl -sf 127.0.0.1:8001/health; do sleep 5; done
  for t in "$@"; do
    comp=$([ $t = sf ] && echo 7d3a15092f963723 || echo 9714c660b365fad1)
    (date -u +%FT%TZ; python3 $I/scripts/jev_rerank.py --pool $I/$t/pool.trec --corpus $I/$t/corpus.jsonl \
      --queries $I/$t/queries.jsonl --base-url http://127.0.0.1:8001 --model student --question-type choice --per-doc \
      --responses $O/p2/$arm-on-$t.responses.jsonl --out $O/p2/$arm-on-$t.chunks.trec --composite $comp; echo "exit $?"; date -u +%FT%TZ) \
      > $O/p2/$arm-on-$t.log 2>&1
  done
  pkill -f "simple-jev --model $model"; sleep 5
}
score s0 $STUDENT "--revision $STUDENT_SHA" sf fs
score sf $O/p2/sf-merged "" sf fs     # if scifact passed
score fs $O/p2/fs-merged "" sf fs     # if freshstack passed
command grep -h "exit\|wrote" $O/p2/*-on-*.log
```

Long scoring is best run detached: wrap the three `score` calls in a script and launch it with `setsid nohup bash <script> > $O/p2/score.log 2>&1 &`. Every `*-on-*.log` must end `exit 0`.

- [ ] **Step 4: Bring Phase 1–2 outputs back and terminate the pod**

```bash
tar czf /workspace/phase12.tar.gz --exclude='*-merged' --exclude='checkpoint-*' -C $O p1 p2 label-top.txt \
  && md5sum /workspace/phase12.tar.gz && runpodctl send /workspace/phase12.tar.gz
```

Dev box: `mkdir -p $A/phase2 && cd $A/phase2 && runpodctl receive <code> && md5sum phase12.tar.gz && tar xzf phase12.tar.gz`. **Terminate pod A** in the RunPod console; record the end time, total cost and each phase's spend in `$A/phase0/pod.md`.

- [ ] **Step 5: Verdicts (dev box)** — one invocation per passing tenant, its two gated pairs only (Holm m = 2; m = 1 when the other tenant has no adapter)

```bash
cd $A/phase2/p2
T=sf; Q=$SF/qrels.trec; OTHER=fs   # then T=fs Q=$FS/qrels.trec OTHER=sf
python3 $S/report.py --run $T-on-$T.chunks.trec --run s0-on-$T.chunks.trec --run $OTHER-on-$T.chunks.trec --qrels $Q \
  --pair $T-on-$T.chunks.trec=s0-on-$T.chunks.trec --pair $T-on-$T.chunks.trec=$OTHER-on-$T.chunks.trec \
  > ../verdict-$T.txt 2>&1; echo "exit $?"
BASE=$([ $T = sf ] && echo $SF/runs/bge-base.chunks.trec || echo $FS/runs/fs-2048-l070.chunks.trec)
python3 $S/report.py --run $T-on-$T.chunks.trec --run $BASE --qrels $Q --pair $T-on-$T.chunks.trec=$BASE \
  > ../vs-baseline-$T.txt 2>&1; echo "exit $?"
command grep -A8 "\[compare\].*(nDCG@10)" ../verdict-$T.txt ../vs-baseline-$T.txt
```

Drop the `--pair …=$OTHER-on-$T…` and its `--run` when the other tenant did not pass. Per tenant: **adaptation helps** iff own − S0 has Holm p < 0.05 and delta > 0; **domain-specific** iff own − cross has Holm p < 0.05 and delta > 0 (else "one general adapter would do", or "not applicable" with no cross). Reported: own − bge-base, and the share kept = (own − baseline) / (Phase 0 teacher − baseline), both deltas from their `[compare]` blocks. For a tenant that failed Phase 0 but has another tenant's adapter scored on it, report that run against `s0-on-<t>` the same way, not gated.

- [ ] **Step 6: Pin md5s**

```bash
md5sum $A/phase0/*.chunks.trec $A/phase0/*.meta.json $A/phase2/p2/*.chunks.trec $A/phase2/*.txt $A/phase0/report-*.txt | tee $A/md5s.txt
```

- [ ] **Step 7: Write** `docs/plans/2026-10-GATE-rfdt-per-tenant-adaptation.md` from the files above, every number copied from its output file, in this order:
  1. Title and verdict lines: Phase 0 per tenant (PASS/FAIL, delta, permutation p, bar); Phase 2 per passing tenant (adaptation helps / domain-specific / not applicable, each with delta and Holm p). Link the spec (`67ed4ba4`) and this plan.
  2. Inputs: spec §3's table; the fidelity results (`$A/fidelity.txt`, overlap figures).
  3. Phase 0: pod facts (card, price, launch/end, `LABEL_TOP` and the pre-flight line, peak memory), the `[compare]` blocks verbatim, fallback counts, the repeat's determinism line, the reported one-document vs all-50 block, the metadata cross-check.
  4. Smoke test: peak training memory, `smoke end`, Phase 0 spend.
  5. Phase 1: generation pod facts, usable/unusable/duplicate/excluded counts, N and its sizing line, labelling fallbacks, record and split counts.
  6. Phase 2: training logs' `eval_results.json` (reported, not gated), the verdict blocks verbatim, own − bge-base and the share kept.
  7. Spend per phase against §7's ceilings.
  8. Caveats: spec §11's known issues; the top-20 rule if it applied (count of qrels-relevant documents at baseline ranks 21–50 per tenant); any stop or rerun.
  9. Artifacts: `$A/md5s.txt` verbatim and the command lines used.

  If both tenants failed Phase 0, the doc stops after item 3 (plus 7–9) with the verdict "Phase 0 FAIL — no adapters trained".

- [ ] **Step 8: Append the index row after line 51** of `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md`, in the table's four-column form:

```markdown
| RFDT per-tenant adaptation (Qwen3-Reranker-0.6B LoRA students from the SimpleJEV 27B judge, one document per request; SciFact + FreshStack) | **[Phase 0 per tenant; Phase 2 per tenant]** | harness only (`jev_rerank.py --per-doc`, `tenant_pools.py`, `synth_queries.py`, `rfdt_records.py`, `report.py` reranker pairs); no server change | `2026-10-GATE-rfdt-per-tenant-adaptation.md` |
```

Check: `command grep -n -B1 -A1 "RFDT per-tenant adaptation" $R/docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` prints the SimpleJEV row as `51-`, the new row as `52:` and a blank `53-`.

- [ ] **Step 9: Commit**

```bash
git -C $R add -f docs/plans/2026-10-GATE-rfdt-per-tenant-adaptation.md
git -C $R add docs/2026-09-06-ranked-changes-after-retrieval-experiments.md
git -C $R commit -m "record the RFDT per-tenant adaptation verdict

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

## Tasks NOT in this plan

Server integration and per-tenant serving; the 50-documents-per-request format; a third tenant; the
`student-distill` pipeline (unchanged); listwise or blended scoring.

## Known issues inherited from spec

- **The FreshStack bar is high.** 25% of a 0.3822 headroom is +0.0956 (vs SciFact's +0.047); the judge may well
  fail it. Accepted with the bar's formula (Ben, Section 1).
- **The per-document gain is unknown** until Phase 0; the gated +0.0713 had all 50 documents in context.
- **The student's base quality under SimpleJEV's prompt is untested.** Qwen3-Reranker-0.6B was tuned on its own
  template; S0 measures where it starts.
- **Pool reconstruction reproduces a shipped pipeline offline** (MMR, HNSW); the 0.90 fidelity gate bounds the
  mismatch but does not remove it.
- **RFDT is new upstream:** its README says NVIDIA execution and live teacher labelling are untested there.
- **The 27B judge's labels on synthetic queries are not ground truth;** the students are only ever judged against qrels on the real test queries.
