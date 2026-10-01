# SimpleJEV relevance judge — offline gate (design)

Written 2026-09-29. Produced with thorough-brainstorming. The approaches were probed against the public
demo, and the 27 assumptions in §10 were verified (A24–A25 added by CDR-1's span check, A26 by CDR-2's, A27 by Ben's 2026-10-01 model ruling).

## 1. The question

Can an open-weights model, served by [SimpleJEV](https://github.com/featherless-ai/simple-jev) as a
**pointwise relevance judge**, reorder the shipped bge-base top-50 pool enough to matter for top-k precision
and recall?

SimpleJEV scores fixed questions against a shared context from the model's next-token logits and never
generates text. For each question it returns a softmax distribution over the allowed answer tokens. Its
`noul` question type returns a continuous truth value, and under the auto-selected `shared_*` prompt policies
that value is `P(yes)` over a no/yes choice (`hf_prompt_policies.py:182-186`). This is the same mechanism as
Qwen3-Reranker's yes/no scoring. What SimpleJEV adds is a generic server for any compatible open model, with
per-architecture prompt formats.

This is an **offline measurement with a pre-registered gate. Nothing in this spec changes server code.**
The server has no slot for a text-reading judge anyway: `IResultReranker.Rerank`
(`Iverson.Vector/IResultReranker.cs:13-16`) is synchronous and pure and receives no document text. A PASS
licenses a spec for that stage; §8 lists what a PASS or FAIL does and does not decide.

## 2. Rulings (Ben, 2026-09-29)

| Decision | Ruling |
|---|---|
| Scope | Offline gated measurement first; server integration only after a PASS, in its own spec |
| Hosting | The **gate** runs on a rented GPU pod self-hosting `hf_server`, which is how production would run it (self-hostable open weights, per the 2026-09-20 ruling). The free model screen on Featherless's public demo was **dropped** (Ben, 2026-10-01): the demo has redirected every request since ≈18:50 UTC 2026-09-30 |
| Pool / baseline | The shipped **bge-base** SciFact pool; gated against it |
| Gate model | **`Qwen/Qwen3.8-27B`**, named by Ben (2026-10-01) in place of the §5 screen: the highest `hf_server` development score of the five self-hostable candidates (445/477, `hf-server/README.md:120-129`) |
| Readout | Pointwise `noul` |
| Secondary metrics | R@10 and P@10 from one recorded `ir_measures` command; `report.py` unchanged |

## 3. Inputs (all on disk, outside the repo)

Root: `~/repositories/iverson-benchmark-corpora/`.

| Role | Path | md5 |
|---|---|---|
| Gate pool (300 q × 50) | `scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec` | `e51bf248d79044fd2177ca39dfc0e912` |
| Gate qrels | `scifact-bge-base-2026-09-04/qrels.trec` | `f7572e6aef242267104d7ad5cda8f729` |
| Gate corpus / queries | `scifact-bge-base-2026-09-04/beir/{corpus,queries}.jsonl` | — |

In both corpora the `text` field already begins with the title (5,183/5,183 and 3,633/3,633 docs), so the
judged document is `text` alone. Every pool doc id resolves in its corpus.

**Baseline and ceiling on the gate pool** (computed with `ir_measures` 0.4.3 from the files above):

| Ordering | nDCG@10 | R@50 | AP | R@10 | P@10 |
|---|---|---|---|---|---|
| bge-base (identity) | 0.7452 | 0.9337 | 0.7018 | 0.8770 | 0.0993 |
| relevant-first oracle | 0.9345 | 0.9337 | — | 0.9337 | — |
| reversed | 0.0011 | 0.9337 | 0.0212 | 0.0033 | 0.0003 |

Headroom is **+0.1893** nDCG@10, spread over 107 of the 300 queries. R@50 cannot move: a reranker cannot
bring in documents that first-stage retrieval missed, and SimpleJEV cannot generate or embed. This design
improves precision and recall **within the top 50**, not first-stage recall.

## 4. The judge request (frozen)

The wording is generic because NFCorpus queries are topics ("deafness", "DHA") and SciFact queries are
claims, and production use would be generic too. It is fixed here, and the sidecar records its
SHA-256.

One request per query, 50 questions, `model` = the served id:

```json
{
  "model": "<served id>",
  "state": "Search query: <query text>",
  "questions": {
    "d0": {
      "type": "noul",
      "instructions": "Is the following document relevant to the search query?\n\nDocument:\n<corpus text of pool doc 0>",
      "criteria": {
        "true": "The document contains information that answers or directly bears on the query.",
        "false": "The document does not."
      }
    },
    "d1": { "...": "same shape for pool doc 1" }
  }
}
```

Question ids are `d<i>`, where `i` is the doc's zero-based position in the baseline pool. The `score`
variant (implemented in `jev_rerank.py`; unused since the screen was dropped) replaces each question with:

```json
{
  "type": "score",
  "instructions": "How relevant is the following document to the search query?\n\nDocument:\n<text>",
  "criteria": [
    "Unrelated to the query",
    "Same topic, but does not answer the query",
    "Partially answers or bears on the query",
    "Directly answers or bears on the query"
  ]
}
```

**Response acceptance:** `answers` must contain every `d<i>`. Each `noul` must be finite and in [0, 1], and
each `score` finite and in [0, 3]. Anything else counts as a rejected attempt.

**Ordering:** documents sort by judge value descending, and **exact ties keep baseline order** (a stable sort
over the baseline sequence). The run file then gets synthetic, strictly decreasing scores `51 − position`, so
`ir_measures`' doc-id tie-break can never apply. Ties do occur: Qwen3.8-27B gave 32 distinct values out of 50
on query 1, with the ties in the saturated tail near 0.018.

## 5. Model choice and transport rules

**Model screen: dropped (Ben, 2026-10-01).** The screen was to rank five demo models on NFCorpus and pick the
gate model by a pre-registered rule. Since ≈18:50 UTC on 2026-09-30 the demo has answered every request with
`301` → `featherless.ai/simple-jev/…` (an HTML page), so it cannot run. The gate model is named instead:
**`Qwen/Qwen3.8-27B`**, with the `noul` question and the `shared_examples_binary` policy. It has the highest
`hf_server` development score of the five self-hostable candidates: 445/477, against Gemma-4-26B-A4B 437,
Qwen3.6-35B-A3B 432, Gemma-4-12B 427 and Qwen3.5-4B 374 (`hf-server/README.md:120-129`). It is chosen without
any SciFact data, so the gate stays free of selection bias.

**Transport and failure handling** (the gate passes in §6 use these rules):

- Requests go one at a time, at least 0.5 s apart (the demo is rate-limited to 2 RPS), with client timeout 300 s.
- Send `User-Agent: curl/8.5.0`. Python's default urllib agent gets a Cloudflare **403 error 1010** (probed).
  A 403 aborts the whole invocation: it is a configuration error, not a per-query failure.
- **Retryable, up to 4 attempts in total:** 429 (sleep `Retry-After`, default 1 s), 5xx, timeouts, connection
  errors, and rejected responses (§4 acceptance).
- **Not retryable:** 400 and 422 (over-limit or invalid). The query falls back immediately, and the body is
  logged in the ledger.
- A query that exhausts its attempts **falls back to baseline order** and is counted.
- **Fallback cap: 5%** (`floor(0.05·n)`). More than 15 of 300 refuses the gate run: no verdict is written, and
  the run is investigated and repeated.
- Ledger: one JSONL file per pass. It holds accepted and rejected entries, and resume keys on
  accepted entries only (the `teacher_rerank` convention). **Each pass has its own ledger** (CDR-3: a shared
  ledger replays pass 1).

## 6. Stage B — gate run (self-hosted `hf_server`, rented pod)

### 6.1 Dry run (local, free, before any rental)

`stub_jev_server.py` serves `/v1/classifier` locally. `jev_rerank.py` runs all 300 SciFact queries against it
in two modes, and each mode's output is scored with `report.py --pair <run>=bge-base.chunks.trec`:

| Mode | Stub reply | Required outcome |
|---|---|---|
| `identity` | `noul` for `d<i>` = `1 − i/50` (strictly decreasing with baseline rank) | nDCG@10 **0.7452**, R@50 0.9337; `--pair` **exits non-zero** with sequence differing on 0 queries |
| `reversed` | `noul` for `d<i>` = `i/50` | nDCG@10 **0.0011**, R@50 0.9337; `--pair` **exits 0** |

A one-sided dry run cannot fail, because "output equals baseline" is both its pass condition and the
signature of a discarded reply (the teacher-ceiling lesson). The stub **rejects with 422** any question whose
instructions do not contain the text of the doc at that pool position. This guards against the teacher-ceiling
defect where prompts carried a bare `[docid]` while every test stayed green.

### 6.2 Pod

- **GPU, sized to the model for weights plus the copied prefix cache (CDR-1 §3.1, option b).** Every branch
  carries the whole pool (A15). `hf_server` deep-copies the prefix cache and broadcasts it to each suffix
  batch of up to `--max-batch-size` rows (`hf_server.py:536-542`), and no flag bounds that memory
  (`:1136-1137`). Estimated full-attention KV in bf16 at query 129's ≈52k tokens, per batch row (sliding-window
  and linear-attention state excluded):

  | Model | Weights | KV per token | Per row | Weights + 32 rows | Card |
  |---|---|---|---|---|---|
  | Qwen3.5-4B | ≈9 GB | 32 KiB | ≈1.7 GB | ≈63 GB | 80 GB |
  | gemma-4-12B-it | ≈24 GB | 16 KiB | ≈0.85 GB | ≈51 GB | 80 GB |
  | gemma-4-26B-A4B-it | ≈52 GB | 20 KiB | ≈1.06 GB | ≈86 GB | H200 (141 GB) |
  | Qwen3.8-27B | ≈56 GB | 64 KiB | ≈3.4 GB | ≈165 GB | H200 |
  | Qwen3.6-35B-A3B | ≈72 GB | 20 KiB | ≈1.06 GB | ≈106 GB | H200 |

  At the default 32 rows, the 27B's estimate exceeds even an H200, so expect its pre-flight to settle at
  `--max-batch-size` 16 or lower. These are estimates; the §6.3 pre-flight decides. For Qwen3.8-27B the
  card is an H200.
- **Server:** simple-jev commit `7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3`, `pip install -e './hf-server'`,
  launched with `setsid nohup`:
  `ENABLE_OPEN_JEV_ADVANCED_METRICS=1 simple-jev --model <HF repo> --revision <full sha> --served-model-name <HF repo> --enforce-model-id
  --classifier-prompt-policy <policy> --max-request-branches 64 --max-model-len 65536 --dtype bfloat16`.
- **Policy pin:** `<policy>` is the value the **code's** `KNOWN_PROFILES` gives for the model
  (`hf_prompt_policies.py:41-52`), **not** the README's table, which is stale at this commit. Those values
  are: Qwen 4B, Qwen 27B and Gemma 26B-A4B → `shared_examples_binary`; Qwen 35B-A3B and Gemma 12B →
  `shared_repeat_state`. Set it explicitly, and confirm both the startup log's `AUTO PROMPT FORMAT` line (run
  once without the flag) and a response's `metadata.prompt_policy` (with `ENABLE_OPEN_JEV_ADVANCED_METRICS=1`).
- **Revisions observed 2026-09-29** (all ungated): Qwen3.5-4B `851bf6e806ef…`, Qwen3.8-27B `1d4bf0f2ff60…`,
  Qwen3.6-35B-A3B `995ad96eacd9…`, gemma-4-12B-it `707f0a3b8a3c…`, gemma-4-26B-A4B-it `4d7ae4984b7d…`. The
  full sha used goes in the sidecar.
- **Access:** `hf_server` has no auth and binds 127.0.0.1, so **the client runs on the pod**. Files move with
  `runpodctl send/receive`, because RunPod's SSH gateway has no exec channel or SFTP.

### 6.3 Passes

0. **Pre-flight (blocking; CDR-1 §2.1 and §3.1):** with `ENABLE_OPEN_JEV_ADVANCED_METRICS=1`, send the §4
   request for SciFact query 129, the largest branch in either corpus (150,319 rendered chars, ≈52k tokens;
   the largest NFCorpus branch is ≈130K chars). It must return HTTP 200 before any other pass runs.
   - On a 500 or CUDA out-of-memory error: restart the server with `--max-batch-size` halved (32 → 16 → 8 …)
     and resend until it returns 200.
   - On a 422: the tokenizer is denser than the demo-measured 2.90–3.05 chars/token and the branch exceeds
     `--max-model-len`. Stop; do not run the passes.
   - Record the final `--max-batch-size` and the pre-flight's `usage.input_tokens` in the sidecar's
     `reranker` block, by passing them to `jev_rerank.py` as `--max-batch-size` and
     `--preflight-input-tokens` (§9) on every pod pass.
1. **Transfer check: dropped** with the screen (Ben, 2026-10-01). It compared the screen's demo ledger with the pod.
2. **Main pass:** 300 SciFact queries → `jev-<model>.chunks.trec` plus `jev-<model>.meta.json`. The sidecar
   carries **bge-base's own composite `7d3a15092f963723`**, so `report.py` does not print BUILD MISMATCH, and a
   `reranker` block: simple-jev commit, HF repo and full revision, policy, question type, wording SHA-256,
   endpoint, `--max-batch-size` and `--max-model-len` as run, and fallback count with query ids.
3. **Repeat pass (noise floor):** a 50-query subsample,
   `teacher_rerank.select_subsample(ids, 50, 20260929)`, written to a **separate ledger** and run file. It is
   scored against qrels **restricted to those 50 ids**, because `report.py` averages partial runs over every
   qrels query (precedent: `teacher-ceiling-2026-09/replay/qrels-sub50.trec`). The judge is not
   deterministic: the same 4B demo request sent twice differed by up to 0.014 in one `noul` value.

## 7. Verdict

```
report.py --run jev-<model>.chunks.trec --run bge-base.chunks.trec \
          --qrels scifact-bge-base-2026-09-04/qrels.trec \
          --pair jev-<model>.chunks.trec=bge-base.chunks.trec
```

- **Preconditions:** `--pair` exits 0 (identical per-query doc sets, at least 25% of queries reordered); R@50
  is **exactly** 0.9337; fallbacks are at most 15.
- **Gate (pre-registered):** permutation **p < 0.05** and **delta ≥ +0.047** nDCG@10. The threshold is 25%
  of the +0.1893 headroom (0.25 × 0.18928 = 0.0473), the same rule stage 1 applied on A0′ (+0.055). It lies
  within the 0.032–0.054 MDE range `report.py` has printed for SciFact comparisons.
- **Worst-case bound:** re-score with every fallback query's judge ordering replaced by reversed order, and
  state whether the gate still holds (stage 1's convention).
- **Reported, not gated:** R@10, P@10 and AP for both runs, from one `ir_measures.calc_aggregate([nDCG@10,
  R@50, AP, R@10, P@10], …)` command recorded verbatim in the gate doc; the repeat-pass delta; the §5 model-choice
  note.
- **Gate doc:** `docs/plans/2026-09-GATE-simple-jev-judge.md`, containing the output md5s of every run file
  and report, and the command lines. Add its row to §0 of
  `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` (one row per `docs/plans/*GATE*.md`).

## 8. What PASS and FAIL decide

- **PASS** licenses a spec for a server-side judge stage: async, after fusion and max-passage, with access to
  document text, as a `simple-jev` sidecar under a compose profile. It does not license shipping one.
- **FAIL** closes SimpleJEV as a **zero-shot** judge on this pool. It does not decide RFDT distillation or the
  per-tenant student line (`student-distill`); each of those needs its own decision.
- **Out of scope for this spec:** server code, RFDT, first-stage recall, listwise `choice` ranking, and blending
  the judge score with the fused score.

**Why not listwise `choice`** (probed on the demo, SciFact query 1): one `choice` question over the pool
fits at k=5 (4,634 tokens) but at k=50 returns **400, "maximum context length is 32768"** (median pool
82,895 chars, max 126,752). Its probabilities also saturate: at k=5 the top candidate got 0.9988 and ranks
2–5 got 6e-4 to 3e-5, so any ranking below rank 1 would rest on noise-scale values.

## 9. Components

All in `Iverson.Server/Iverson.LoadTest/scripts/`, stdlib-only, on a new worktree/branch `simple-jev-judge`
from `main` (`main` equals `origin/main` at writing).

| Unit | New / reused | Responsibility |
|---|---|---|
| `jev_rerank.py` | new | CLI: `--pool --corpus --queries --base-url --model --question-type {noul,score} --responses --out --composite [--subsample N --subsample-seed S]`, plus record-only provenance flags `--server-commit --revision --policy --max-batch-size --max-model-len --preflight-input-tokens`. Each provenance flag is written verbatim into the sidecar's `reranker` block, is never sent to the server, and records `null` when omitted (the `teacher_rerank.py` `--vllm-version` / `--quantisation` / `--instance-type` precedent). Builds §4 requests, applies §5 transport rules, orders per §4, and writes the run file (**its own writer**, run tag `simple-jev`) and the sidecar. |
| `teacher_rerank.py` helpers | reused by import, unmodified | `load_run`, `load_corpus` (returns `(title, text)`; the judge uses `[1]`), `load_queries`, `select_subsample`, `read_responses_ledger`, `accepted_entry`, `append_response`, `make_record`. **Not** `write_run`, which hard-codes `RUN_TAG = "teacher-ceiling"` (`:55`, `:490-505`). |
| `stub_jev_server.py` | new | §6.1 stub: `identity` and `reversed` modes, document-text assertion. Separate from `stub_vllm_server.py` because the protocol differs. |
| `test_jev_rerank.py` | new | Request shape (wording, one question per doc, doc text present); acceptance rules; tie-break is stable over baseline order; synthetic scores are `51 − position`; retry classes (429 honours `Retry-After`, 400/422 fall back at once, 403 aborts); fallback cap; resume from accepted entries only; per-pass ledger isolation. Each test must be falsified by a named mutation. |
| `report.py` | reused, unchanged | Scoring, `--pair`, permutation test, MDE. |
| Artifacts | outside the repo | `~/repositories/iverson-benchmark-corpora/simple-jev-2026-09/`: ledgers, run files, logs. That directory is not a git repo, so nothing there is `git add`ed. |

## 10. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | `teacher_rerank` loaders and subsample fit a non-teacher pool | `teacher_rerank.py:75-127,140-147`; `load_corpus` returns `(title, text)` |
| A2 | Importing `teacher_rerank` is side-effect-free and stdlib-only | imports `:33-41`; `main()` guarded `:649` |
| A3 | The ledger resumes from accepted entries only | `accepted_entry` `:253-261`; `make_record` stamps `status` `:272-284` |
| A4 | `write_run` is reusable — **moved** | it hard-codes `RUN_TAG = "teacher-ceiling"` (`:55`); `jev_rerank.py` has its own writer |
| A5 | `report.py --pair` enforces pool invariance and at least 25% reordering | `report.py:700,731-766,851` |
| A6 | The composite is read from the sidecar; bge-base has one | `report.py:184-202`; `bge-base.meta.json` `"composite": "7d3a15092f963723"` |
| A7 | bge-base scores 0.7452 / 0.9337 | `2026-09-GATE-embedding-migration.md:135`; reproduced with `ir_measures` |
| A8 | Qrels restriction precedent exists | `teacher-ceiling-2026-09/replay/qrels-sub50.trec` |
| A9 | NFCorpus qrels are 4-column, graded, and cover all 323 run queries | rel 1 ×11,758, rel 2 ×576; 323/323 |
| A10 | Corpus `text` starts with the title | 5,183/5,183 SciFact; 3,633/3,633 NFCorpus |
| A11 | `hf_server` flags, default host, no auth | `hf_server.py:1289-1330`; API_REFERENCE "no authentication" |
| A12 | `noul` is continuous `P(yes)` under `shared_*` | `hf_prompt_policies.py:107-120,182-186`; wired `hf_server.py:946` |
| A13 | The 5 screen models exist on HF and have a profile — **moved** | all 5 repos resolve, ungated. The **code** profiles (`hf_prompt_policies.py:41-52`) differ from the README table, so §6.2 pins from the code |
| A14 | No RWKV backend | `hf_server.py:1300` `choices=["transformers", "laya"]` |
| A15 | Every branch fits `--max-model-len 65536` — **moved** (CDR-1 §2.1) | The v1 builder lists every question's instructions in the shared prefix (`common/prompt_builder.py:173-181`, placed in each branch's system message at `hf_server.py:272`) and repeats the selected one twice (`prompt_builder.py:257-263`), so each branch carries all 50 documents. SciFact branches render at 69,731–150,319 chars (CDR-1, via `PromptCompiler.compile(render_only=True)`; re-estimated 2026-09-30 from raw instruction lengths at 68,736–146,393, max query 129). The largest NFCorpus branch is ≈130K chars (PLAIN-478). At the demo-measured 2.90–3.05 chars/token the longest is ≈52k tokens. All five models declare `max_position_embeddings` 262144 |
| A16 | Error shapes | `hf_server` 422 `:1002`, 429 + `Retry-After: 1` `:1090`; demo 400 (probed) |
| A17 | Synthetic scores pass the structural check | scores 50..1 are never 0 (`report.py:260-278`) |
| A18 | Every pool doc resolves | 0 missing in both pools |
| A19 | Threshold arithmetic | 0.25 × 0.18928 = 0.0473; observed SciFact MDEs 0.032–0.054 (`GATE-embedding-migration.md:151-203`, `teacher-ceiling-2026-09/replay/report-output-repeat.txt:22,44`) |
| A20 | Ranked-changes coverage rule | `ranked-changes…md` §0 |
| A21 | No name collision; `teacher_rerank.py` unmodified | no `*jev*` in `scripts/` |
| A22 | Worktree base | `git rev-list` main vs origin/main = 0 / 0 |
| A23 | GPU sizing (weights plus prefix-cache copies) | HF safetensors param counts: 4.66B, 11.96B, 25.81B, 27.78B, 35.95B. HF `config.json` full-attention layers × KV heads × head dim: 4B 8×4×256, 12B 8×1×512 (global), 26B-A4B 5×2×512 (global), 27B 16×4×256, 35B-A3B 10×2×256. The prefix cache is deep-copied per suffix batch (`hf_server.py:536-542`), with no memory bound (`:1136-1137`). The query-129 pre-flight decides |
| A24 | The demo accepts the largest 50-question NFCorpus body | PLAIN-478 (the largest NFCorpus branch) returned 200 with 50 answers on Qwen3.8-27B (78,248 input tokens) and Gemma-4-26B-A4B (136,410), re-probed 2026-09-30. CDR-1 reported all five models |
| A25 | `hf_server` serves all five screen models on the prefix-cache path | That path raises unless the cache supports `reorder_cache` (`hf_server.py:510-513`); `hf-server/README.md:120-129` reports hf_server development scores (/477) for all five under their `shared_*` policies |
| A26 | Every sidecar `reranker` field has a source (CDR-2 §2.1) | hf_server exposes no commit or `--max-batch-size` / `--max-model-len` in any response; the revision and policy appear only in advanced metadata (`hf_server.py:864-869`, `:949-954`, `:1264-1275`), so they come from record-only flags (precedent `teacher_rerank.py:533-535,576-579`). Advanced metrics do not change scores (`common/response_scoring.py:100`), and binary `noul` answers stay `{type, noul}` (`hf_prompt_policies.py:182-186`, applied before the metadata merge at `hf_server.py:946-954`) |
| A27 | The gate model is `Qwen/Qwen3.8-27B` with policy `shared_examples_binary` (Ben, 2026-10-01) | `hf-server/README.md:120-129`: 445/477, the highest of the five candidates; profile 'Qwen dense 27B' → `shared_examples_binary` (`hf_prompt_policies.py:44-45`); revision `1d4bf0f2ff60…` observed 2026-09-29; card H200 per §6.2 |

**Demo probes (2026-09-29, SciFact query 1, public data):** 50 questions per request accepted by the 27B and
Gemma 26B models; distinct `noul` values 10/10 (4B, k=10), 50/50 (Gemma), 32/50 (27B); relevant doc
(baseline rank 9) moved to 2 / 3 / 5 (4B k=10 / 27B / Gemma), which is n=1 mechanics and not evidence;
determinism max |Δ| 0.014; listwise `choice` at k=50 → 400.

## 11. Known issues, accepted

- **The gate model is named, not selected on this task.** Qwen3.8-27B is chosen by `hf_server`'s own
  development scores, a general decision benchmark rather than retrieval relevance, so one of the other four
  candidates may judge this pool better. Ben accepted this (2026-10-01) when the demo became unavailable.
- **SimpleJEV's README policy table is stale** at `7bb4f0c7`. It is handled by pinning from code (§6.2), and
  the discrepancy was not reported upstream.
