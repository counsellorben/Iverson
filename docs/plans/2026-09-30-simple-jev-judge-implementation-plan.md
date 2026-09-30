# SimpleJEV Relevance Judge — Offline Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-29-simple-jev-judge-design.md` (commit SHA: `18ba5177`)

**Goal:** Measure whether an open-weights model, served by SimpleJEV as a pointwise `noul` relevance judge, reorders the bge-base SciFact top-50 pool enough to pass the pre-registered gate (permutation p < 0.05 and nDCG@10 delta ≥ +0.047).

**Architecture:** One stdlib client, `jev_rerank.py`, sends one `/v1/classifier` request per query (the query as shared `state`, one question per pool document), re-sorts the pool by the judge's value, and writes a TREC run plus a provenance sidecar. It runs twice over: a free model screen against Featherless's public demo on NFCorpus, then gate passes against a self-hosted `hf_server` on a rented pod. A local stub proves the pipeline end to end before any money is spent; `report.py` (unchanged) scores everything.

**Tech stack:** Python 3.14 stdlib scripts under `Iverson.Server/Iverson.LoadTest/scripts/` (pytest 9.1.1 on the system interpreter); `report.py` with `ir_measures` 0.4.3 / `scipy` from `~/repositories/iverson-benchmark-corpora/python-libs`; SimpleJEV `hf_server` at commit `7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3` (Python ≥ 3.12, torch ≥ 2.6, transformers ≥ 5.16.1) on RunPod.

---

## Global Constraints

- The new scripts are stdlib-only, like their peers; they import siblings with the directory's `sys.path.insert(0, …)` + `import x  # noqa: E402` idiom (as `rrf_fuse.py:68-69` and `summarize_teacher_attempts.py:12-13` do).
- `docs/plans/` and `docs/specs/` are gitignored (`.gitignore:49`): commit anything there with `git add -f`.
- `~/repositories/iverson-benchmark-corpora/` is **not** a git repo: never `git add` there.
- Only **public** SciFact / NFCorpus data may be sent to the demo API (spec §2).
- **Task 4 rents a GPU.** The implementer stops at its Step 1 and asks Ben for an explicit go-ahead.
- **Gate pool ruling (Ben, 2026-09-30):** the gate keeps the spec's pool, `scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec`. It is the shipped embedder (bge-base) at a **512/448** chunk window, not the shipped 2048/1792 window (a `sci-2048` bge-base pool exists at `scifact-2048-2026-09-06/runs/sci-2048.chunks.trec`: nDCG@10 0.7476, R@50 0.9393). The gate doc states this.
- Never push; merging is Ben's call.

## File Structure

- Create `Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py` — the judge client: request builder, acceptance rule, stable tie-break, transport with retry/fallback, run writer, sidecar, CLI.
- Create `Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py` — its pytest suite (33 tests).
- Create `Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py` — the two-mode stub for the dry run.
- Create `docs/plans/2026-09-GATE-simple-jev-judge.md` — the gate verdict (Task 5).
- Modify `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` — one new §0 row and the paragraph under the table (Task 5).
- Artifacts (outside the repo): `~/repositories/iverson-benchmark-corpora/simple-jev-2026-09/{dryrun,screen,pod,gate}/`.

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` (and CDR rounds 1–2) at spec-write time and are NOT re-verified here. Trusted as ground truth:

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

**Demo probes (2026-09-29, SciFact query 1, public data):** 50 questions per request accepted by the 27B and
Gemma 26B models; distinct `noul` values 10/10 (4B, k=10), 50/50 (Gemma), 32/50 (27B); relevant doc
(baseline rank 9) moved to 2 / 3 / 5 (4B k=10 / 27B / Gemma), which is n=1 mechanics and not evidence;
determinism max |Δ| 0.014; listwise `choice` at k=50 → 400.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (2026-09-30):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `.worktrees/simple-jev-judge` and branch `simple-jev-judge` are unused | `git worktree list` has no `simple-jev`; `git branch --list 'simple-jev*'` empty |
| P2 | Signature | Reused `teacher_rerank` helpers exist with the called shapes | `teacher_rerank.py`: `load_run:75`, `load_corpus:117` → `{id: (title, text)}`, `load_queries:122`, `select_subsample(run_query_ids, n, subsample_seed):140`, `read_responses_ledger:214` (missing file → `{}`), `accepted_entry:253`, `append_response:264`, `make_record(query_id, status, content, order, reason, pass_id, reasoning=None, completion_tokens=None, prompt_tokens=None):272`, `validate_ledger_pass(path, ledger, expected_pass):306`, `sidecar_path_for:508`, `validate_inputs_before_any_model_call(pool, pool_query_order, corpus, queries):583`. Their refusal messages say `[teacher_rerank]` |
| P3 | Signature | `stub_vllm_server.load_queries_by_text(path)` → `{text: id}`, import has no side effects, query texts are distinct | `stub_vllm_server.py:85`; `main()` guarded `:185`; SciFact 300/300 and NFCorpus 323/323 distinct texts |
| P4 | Data | Run files are contiguous per query; `head -n 1000` of the NFCorpus pool is exactly its first 20 queries × 50 | 323 and 300 `uniq` runs of query ids; `head -n 1000 … \| uniq -c` → 20 × 50 |
| P5 | Data | `awk '$1=="129"'` on the SciFact pool yields 50 rows | counted: 50 |
| P6 | Command | `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/<file> -q` runs from the repo root | `test_teacher_rerank.py`: 44 passed; no conftest or ini files |
| P7 | Command | `report.py --pair` scores runs from `--run`; `--baseline B --run R` works with B absent from `--run`; the two flags are mutually exclusive | `report.py:641-660` (baseline read separately, excluded if listed), `:883` |
| P8 | Data | The NFCorpus bge-base pool has a sidecar composite | `nfcorpus-bge-base-2026-09-04/runs/bge-base.meta.json` → `7d3a15092f963723` (the same build as SciFact's) |
| P9 | Command | `pip install -e './hf-server'` installs the `simple-jev` console script | `hf-server/pyproject.toml`: `[project.scripts] simple-jev = "hf_server:main"`, `requires-python >=3.12`, `torch>=2.6`, `transformers>=5.16.1,<6` |
| P10 | Ordering | Task 2 needs Task 1's `jev_rerank.py`; Task 3 needs Task 1; Task 4 needs Tasks 1–3 (winner, dry-run pass); Task 5 needs Task 4 | no task imports a file a later task creates |
| P11 | Code | `HTTPError` carries `.code`, `.headers`, `.read()` and is caught before `OSError` | precedent `teacher_rerank.call_teacher`; `test_call_jev_*` pass against a real local server |
| P12 | Code | `hf_server` responses carry `answers[d]["noul"]` / `["score"]` and `usage.input_tokens`; the demo's did until 2026-09-30 | hf_server: `restore_binary_noul` (`hf_prompt_policies.py:182-186`), `common/response_scoring.py:318` (`score`), `:160` (`usage`). Demo: the 2026-09-29/30 probes read these keys, but since ≈18:50 UTC 2026-09-30 every demo request returns 301 → `featherless.ai/simple-jev/…` (HTML); re-checked 20:23 UTC. Task 3's `check-demo.sh` gates the screen on it (CIR-1 §3.1 (a)) |
| P13 | Command | `nDCG@10, R@50, AP, R@10, P@10` built from `from ir_measures import …` work on Python 3.14 | ran; reproduces spec §3's identity and reversed rows exactly |
| P14 | Consumer (Cat 6) | The ranked-changes §0 table ends at the relation-popularity row, and the paragraph under it counts "The seven rows added since" and names each row's window | `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md:50` (last row), `:52-59` (paragraph). Task 5 appends after `:50` and rewrites the paragraph |
| P15 | File path | `docs/plans/2026-09-GATE-simple-jev-judge.md` does not exist; `docs/plans/` is ignored, the ranked-changes doc is tracked | `ls` → none; `git check-ignore` → `.gitignore:49`; `git ls-files` lists the ranked-changes doc |
| P16 | Command | Commit messages are lowercase imperative, no prefix | `git log --format=%s -12` |
| P17 | Sibling set | Every `tr.*`, `stub_vllm_server.*`, `report.py` flag and `ir_measures` name the plan's code uses resolves at its point of use | P2, P3, P7, P13; the suite and the dry run import and call all of them |
| P18 | Code | `report.py` prints R@50 per run | `print_scores` (`report.py:379-386`) prints every measure in `[nDCG@10, R@50, AP]` |
| P19 | Data | The restricted qrels built with `tr.select_subsample(list(tr.load_run(BASE)), 50, 20260929)` name the same 50 ids the repeat pass judges | `jev_rerank.main` passes `list(pool.keys())` of the same file; `select_subsample` sorts internally (`teacher_rerank.py:140-147`). Snippet run: 50 ids, 55 qrels rows |
| P20 | Command | `scipy.stats.spearmanr` imports from `python-libs` on system python3 | ran: `spearmanr([1,2,3,4],[1,3,2,4]).statistic` = 0.8 |
| P21 | Code validity | The plan's `jev_rerank.py` and `test_jev_rerank.py` are correct as written | scratch copies: 33 passed; 27 one-line mutations (table in Task 1 Step 6) all killed |
| P22 | Code validity | The plan's stub and the two-sided dry run produce the spec §6.1 outcomes | scratch run, all 300 SciFact queries: identity 0.7452 / 0.9337, `--pair` exit 1 ("sequence differs on 0"); reversed 0.0011 / 0.9337, `--pair` exit 0 (300/300 reordered); wrong document text → 422 |
| P23 | Code validity | Task 5's snippets (restricted qrels, `--baseline` repeat comparison, worst-case bound, transfer Spearman, secondary metrics) run as written | each run on the dry-run artifacts; the bound rewrite put query 1 in reversed baseline order; identity-vs-reversed ledgers gave ρ −1.000; the secondary-metrics numbers equal spec §3 |
| P24 | Gate pool | The spec's pool is 512/448, not the shipped window | `scifact-bge-base-2026-09-04/keymap.json.stats.json`: `chunk_max_chars 512, chunk_step 448`; Ben ruled to keep it (Global Constraints) |
| U1 | Pod (UNVERIFIED) | The rented image has Python ≥ 3.12 and CUDA torch ≥ 2.6 | cannot be checked before renting; Task 4 Step 3 checks it first and re-rents on failure (cheap at that point). Stage 2's `vllm/vllm-openai:v0.30.0` image is **not** used: its entrypoint serves a model and holds the GPU |
| U2 | Pod | `runpodctl send`/`receive` with md5 checks moves files; `setsid nohup` keeps long runs alive | the stage-2 runbook (`.worktrees/student-distill/docs/plans/2026-09-28-student-distillation-runbook.md:118-124,137`) and the stage-1 SIGTERM finding |
| U3 | Pod | The weights land on the `/workspace` volume, which is sized separately from the container disk | RunPod storage docs (docs.runpod.io/pods/storage/types, fetched 2026-09-30): volume "Mount path: `/workspace` (default)"; the container disk is "temporary storage for the operating system and session data"; no default sizes stated. Task 4 Step 3's `df -h /workspace` checks it (CIR-1 §2.2) |

## Tasks

### Task 1: The judge client, test-first

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py`

**Interfaces:**
- Produces: `jev_rerank.py` (CLI in spec §9; functions `build_request`, `judge_values`, `order_by_judge`, `call_jev`, `retry_delay`, `pass_identity`, `score_query`, `write_run`, `write_sidecar`, `main`; constants `STATE_TEMPLATE`, `WORDING_SHA256`), used by Tasks 2–5.

Decisions this task implements beyond the spec's text (approved 2026-09-30): the pass identity includes pool, base URL, model, question type and subsample settings; any 4xx other than 403 and 429 falls back at once; over the cap `floor(0.05·n)` no run file is written and the process exits 1 (the ledger is kept, so rerunning resumes); a 0.5 s wait precedes every request.

- [ ] **Step 1: Create the worktree** (from local `main`, which carries the spec and this plan)

```bash
git -C /home/ben/repositories/Iverson worktree add .worktrees/simple-jev-judge -b simple-jev-judge main
```

Every later shell in this plan starts with this prelude (cwd does not persist between shells):

```bash
R=/home/ben/repositories/Iverson/.worktrees/simple-jev-judge
S=$R/Iverson.Server/Iverson.LoadTest/scripts
B=/home/ben/repositories/iverson-benchmark-corpora
SF=$B/scifact-bge-base-2026-09-04
NF=$B/nfcorpus-bge-base-2026-09-04
A=$B/simple-jev-2026-09
```

- [ ] **Step 2: Write the test file** `Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py`

```python
"""pytest suite for jev_rerank.py (spec docs/specs/2026-09-29-simple-jev-judge-design.md). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py -q

Covers everything before a real server: the §4 request shape and acceptance rule, the stable
tie-break, the run writer, every §5 transport class (429 honours Retry-After, 4xx falls back at
once, 403 aborts, 5xx/network/rejected bodies retried), pacing, resume, per-pass ledger isolation,
the fallback cap, the sidecar's provenance, and call_jev against a real local HTTP server.

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import http.server
import json
import math
import os
import sys
import threading

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import jev_rerank as jev  # noqa: E402


def write(path, text):
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    return str(path)


def make_inputs(tmp_path, pool):
    """A pool run, a corpus and a queries file for `pool` ({query_id: [doc_id, ...]})."""
    run = "".join(
        f"{qid} Q0 {doc} {rank} {100 - rank}.0 base\n"
        for qid, docs in pool.items() for rank, doc in enumerate(docs, start=1)
    )
    docs = sorted({doc for docs in pool.values() for doc in docs})
    corpus = "".join(json.dumps({"_id": d, "title": f"T{d}", "text": f"T{d}. body of {d}"}) + "\n" for d in docs)
    queries = "".join(json.dumps({"_id": q, "text": f"query {q}"}) + "\n" for q in pool)
    return (write(tmp_path / "pool.chunks.trec", run), write(tmp_path / "corpus.jsonl", corpus),
            write(tmp_path / "queries.jsonl", queries))


def cli(tmp_path, paths, *extra, model="m", responses="ledger.jsonl"):
    pool, corpus, queries = paths
    return ["--pool", pool, "--corpus", corpus, "--queries", queries, "--base-url", "http://judge",
            "--model", model, "--responses", str(tmp_path / responses),
            "--out", str(tmp_path / "out.chunks.trec"), "--composite", "abc123", *extra]


def answers(values, kind="noul", input_tokens=123):
    return {"answers": {f"d{i}": {"type": kind, kind: v} for i, v in enumerate(values)},
            "usage": {"input_tokens": input_tokens, "output_tokens": 0}}


class Scripted:
    """Stands in for call_jev: returns the scripted replies in order and records every body."""

    def __init__(self, *replies):
        self.replies = list(replies)
        self.bodies = []

    def __call__(self, base_url, body):
        self.bodies.append(body)
        return self.replies.pop(0)


POOL3 = {"q1": ["z", "y", "x"]}


def run_one(tmp_path, *replies, pool=POOL3, extra=()):
    """score_query for q1 with scripted replies; returns (order, fell_back, call, sleeps, ledger lines)."""
    paths = make_inputs(tmp_path, pool)
    args = jev.build_arg_parser().parse_args(cli(tmp_path, paths, *extra))
    corpus = jev.tr.load_corpus(paths[1])
    call, sleeps = Scripted(*replies), []
    order, fell_back = jev.score_query("q1", pool["q1"], "query q1", corpus, args, {}, call=call, sleep=sleeps.append)
    ledger = [json.loads(l) for l in open(args.responses)] if os.path.exists(args.responses) else []
    return order, fell_back, call, sleeps, ledger


# ── build_request (spec §4) ─────────────────────────────────────────────────────────────────

def test_build_request_has_one_question_per_pool_doc_in_order_carrying_its_text():
    body = jev.build_request("m", "noul", "cats", ["first text", "second text"])
    assert list(body["questions"]) == ["d0", "d1"]
    assert body["questions"]["d0"]["instructions"].endswith("Document:\nfirst text")
    assert body["questions"]["d1"]["instructions"].endswith("Document:\nsecond text")
    assert body["state"] == "Search query: cats" and body["model"] == "m"


def test_build_request_uses_the_frozen_noul_wording():
    q = jev.build_request("m", "noul", "cats", ["t"])["questions"]["d0"]
    assert q == {
        "type": "noul",
        "instructions": "Is the following document relevant to the search query?\n\nDocument:\nt",
        "criteria": {
            "true": "The document contains information that answers or directly bears on the query.",
            "false": "The document does not.",
        },
    }


def test_build_request_score_variant_uses_the_four_level_rubric():
    q = jev.build_request("m", "score", "cats", ["t"])["questions"]["d0"]
    assert q["instructions"] == "How relevant is the following document to the search query?\n\nDocument:\nt"
    assert q["criteria"] == ["Unrelated to the query", "Same topic, but does not answer the query",
                             "Partially answers or bears on the query", "Directly answers or bears on the query"]


# ── judge_values (spec §4 acceptance) ───────────────────────────────────────────────────────

def test_judge_values_accepts_complete_in_range_values():
    assert jev.judge_values(answers([0.0, 1.0, 0.5]), "noul", 3) == ([0.0, 1.0, 0.5], None)
    assert jev.judge_values(answers([0, 3, 1.5], "score"), "score", 3) == ([0.0, 3.0, 1.5], None)


def test_judge_values_rejects_a_missing_question_id():
    values, reason = jev.judge_values(answers([0.2, 0.3]), "noul", 3)
    assert values is None and "d2" in reason


@pytest.mark.parametrize("bad", [float("nan"), float("inf"), -0.01, 1.01, True, "0.5", None])
def test_judge_values_rejects_non_finite_out_of_range_and_non_numeric_noul(bad):
    values, reason = jev.judge_values(answers([0.5, bad]), "noul", 2)
    assert values is None and "d1" in reason


def test_judge_values_rejects_a_score_above_three():
    values, reason = jev.judge_values(answers([3.2], "score"), "score", 1)
    assert values is None and "d0" in reason


# ── order_by_judge (spec §4 tie-break) ──────────────────────────────────────────────────────

def test_order_by_judge_sorts_descending_and_keeps_baseline_order_on_ties():
    # Doc-id order (w < x < y < z) is the reverse of baseline order, so a doc-id tie-break fails.
    assert jev.order_by_judge(["z", "y", "x", "w"], [0.2, 0.9, 0.2, 0.9]) == ["y", "w", "z", "x"]


# ── write_run ───────────────────────────────────────────────────────────────────────────────

def test_write_run_writes_51_minus_position_and_the_simple_jev_tag(tmp_path):
    path = tmp_path / "r.chunks.trec"
    jev.write_run(str(path), {"q2": ["b", "a"], "q1": ["c"]}, ["q2", "q1"])
    assert path.read_text().splitlines() == [
        "q2 Q0 b 1 50.000000 simple-jev",
        "q2 Q0 a 2 49.000000 simple-jev",
        "q1 Q0 c 1 50.000000 simple-jev",
    ]


# ── score_query: the spec §5 transport classes ──────────────────────────────────────────────

def test_success_records_an_accepted_entry_with_order_values_and_input_tokens(tmp_path):
    order, fell_back, call, _, ledger = run_one(tmp_path, ("ok", answers([0.1, 0.9, 0.5]), None))
    assert (order, fell_back) == (["y", "x", "z"], False)
    assert [r["status"] for r in ledger] == ["accepted"]
    assert ledger[0]["order"] == ["y", "x", "z"] and ledger[0]["prompt_tokens"] == 123
    assert json.loads(ledger[0]["content"])["values"] == [0.1, 0.9, 0.5]
    assert "Ty. body of y" in call.bodies[0]["questions"]["d1"]["instructions"]


def test_429_sleeps_retry_after_then_retries(tmp_path):
    order, fell_back, call, sleeps, _ = run_one(
        tmp_path, ("http", (429, "slow down"), "7"), ("ok", answers([0.1, 0.9, 0.5]), None))
    assert (order, fell_back) == (["y", "x", "z"], False)
    assert len(call.bodies) == 2 and 7.0 in sleeps


@pytest.mark.parametrize("code", [400, 404, 422])
def test_other_4xx_fall_back_to_baseline_after_one_call(tmp_path, code):
    order, fell_back, call, _, ledger = run_one(tmp_path, ("http", (code, "no"), None))
    assert (order, fell_back) == (["z", "y", "x"], True)
    assert len(call.bodies) == 1 and ledger[0]["reason"] == f"HTTP {code}"


def test_403_aborts_the_whole_invocation(tmp_path):
    with pytest.raises(SystemExit):
        run_one(tmp_path, ("http", (403, "error code: 1010"), None))


def test_5xx_is_retried_up_to_four_attempts_then_falls_back(tmp_path):
    order, fell_back, call, _, ledger = run_one(tmp_path, *[("http", (500, "boom"), None)] * 4)
    assert (order, fell_back) == (["z", "y", "x"], True)
    assert len(call.bodies) == 4 and [r["status"] for r in ledger] == ["rejected"] * 4


def test_network_errors_and_rejected_bodies_are_retried(tmp_path):
    order, fell_back, call, _, _ = run_one(
        tmp_path, ("network", "timed out", None), ("ok", answers([0.5, 2.0, 0.1]), None),
        ("ok", answers([0.5, 0.2, 0.1]), None))
    assert (order, fell_back) == (["z", "y", "x"], False)
    assert len(call.bodies) == 3


def test_every_request_is_preceded_by_the_minimum_interval(tmp_path):
    _, _, call, sleeps, _ = run_one(tmp_path, ("http", (500, "boom"), None), ("ok", answers([0.1, 0.2, 0.3]), None))
    assert len(call.bodies) == 2 and sleeps == [0.5, 0.5]


def test_an_accepted_ledger_entry_is_reused_without_a_request(tmp_path):
    paths = make_inputs(tmp_path, POOL3)
    args = jev.build_arg_parser().parse_args(cli(tmp_path, paths))
    ledger = {"q1": [{"query_id": "q1", "status": "accepted", "order": ["x", "z", "y"]}]}
    call = Scripted()
    assert jev.score_query("q1", POOL3["q1"], "query q1", {}, args, ledger, call=call,
                           sleep=lambda s: None) == (["x", "z", "y"], False)
    assert call.bodies == []


# ── main: per-pass ledger isolation, the fallback cap, the sidecar ──────────────────────────

def test_main_refuses_a_ledger_recorded_by_another_model(tmp_path, monkeypatch):
    paths = make_inputs(tmp_path, POOL3)
    monkeypatch.setattr(jev.time, "sleep", lambda s: None)
    monkeypatch.setattr(jev, "call_jev", Scripted(("ok", answers([0.1, 0.9, 0.5]), None)))
    jev.main(cli(tmp_path, paths, model="arm-a"))
    with pytest.raises(SystemExit):
        jev.main(cli(tmp_path, paths, model="arm-b"))


def test_main_over_the_fallback_cap_writes_no_run_file(tmp_path, monkeypatch):
    paths = make_inputs(tmp_path, POOL3)  # 1 query: cap is 0
    monkeypatch.setattr(jev.time, "sleep", lambda s: None)
    monkeypatch.setattr(jev, "call_jev", Scripted(("http", (422, "too long"), None)))
    with pytest.raises(SystemExit):
        jev.main(cli(tmp_path, paths))
    assert not (tmp_path / "out.chunks.trec").exists()


def test_main_at_the_fallback_cap_writes_the_fallback_in_baseline_order(tmp_path, monkeypatch):
    pool = {f"q{n}": [f"a{n}", f"b{n}"] for n in range(20)}  # 20 queries: cap is 1
    paths = make_inputs(tmp_path, pool)
    replies = [("http", (422, "too long"), None)] + [("ok", answers([0.1, 0.9]), None)] * 19
    monkeypatch.setattr(jev.time, "sleep", lambda s: None)
    monkeypatch.setattr(jev, "call_jev", Scripted(*replies))
    jev.main(cli(tmp_path, paths))
    lines = (tmp_path / "out.chunks.trec").read_text().splitlines()
    assert lines[:4] == ["q0 Q0 a0 1 50.000000 simple-jev", "q0 Q0 b0 2 49.000000 simple-jev",
                         "q1 Q0 b1 1 50.000000 simple-jev", "q1 Q0 a1 2 49.000000 simple-jev"]
    sidecar = json.loads((tmp_path / "out.meta.json").read_text())
    assert sidecar["reranker"]["fallbackQueryIds"] == ["q0"] and sidecar["reranker"]["fallbackCount"] == 1


def test_sidecar_records_provenance_flags_verbatim_and_null_when_omitted(tmp_path, monkeypatch):
    paths = make_inputs(tmp_path, POOL3)
    monkeypatch.setattr(jev.time, "sleep", lambda s: None)
    monkeypatch.setattr(jev, "call_jev", Scripted(("ok", answers([0.1, 0.9, 0.5]), None)))
    jev.main(cli(tmp_path, paths, "--server-commit", "7bb4f0c7", "--revision", "sha1",
                 "--policy", "shared_examples_binary", "--max-batch-size", "16", "--max-model-len", "65536"))
    sidecar = json.loads((tmp_path / "out.meta.json").read_text())
    assert sidecar["composite"] == "abc123" and sidecar["configLabel"] == "simple-jev"
    r = sidecar["reranker"]
    assert (r["serverCommit"], r["revision"], r["policy"], r["maxBatchSize"], r["maxModelLen"]) == (
        "7bb4f0c7", "sha1", "shared_examples_binary", 16, 65536)
    assert r["preflightInputTokens"] is None and r["subsample"] is None
    assert r["wordingSha256"] == jev.WORDING_SHA256 and len(jev.WORDING_SHA256) == 64


# ── call_jev against a real local server ────────────────────────────────────────────────────

class _Handler(http.server.BaseHTTPRequestHandler):
    status, payload, headers_out, seen = 200, b"{}", {}, []

    def log_message(self, fmt, *args):
        pass

    def do_POST(self):
        body = self.rfile.read(int(self.headers["Content-Length"]))
        type(self).seen.append((self.path, self.headers.get("User-Agent"),
                                self.headers.get("Content-Type"), json.loads(body)))
        self.send_response(type(self).status)
        for name, value in type(self).headers_out.items():
            self.send_header(name, value)
        self.send_header("Content-Length", str(len(type(self).payload)))
        self.end_headers()
        self.wfile.write(type(self).payload)


@pytest.fixture
def server():
    _Handler.seen = []
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    yield f"http://127.0.0.1:{srv.server_address[1]}"
    srv.shutdown()
    srv.server_close()


def test_call_jev_posts_json_with_the_curl_user_agent_and_returns_the_parsed_body(server):
    _Handler.status, _Handler.payload, _Handler.headers_out = 200, b'{"answers": {}}', {}
    assert jev.call_jev(server, {"k": 1}) == ("ok", {"answers": {}}, None)
    assert _Handler.seen == [("/v1/classifier", "curl/8.5.0", "application/json", {"k": 1})]


def test_call_jev_returns_the_http_code_body_and_retry_after(server):
    _Handler.status, _Handler.payload, _Handler.headers_out = 429, b"queue full", {"Retry-After": "3"}
    assert jev.call_jev(server, {}) == ("http", (429, "queue full"), "3")


def test_call_jev_reports_a_non_json_200_as_a_network_kind_failure(server):
    _Handler.status, _Handler.payload, _Handler.headers_out = 200, b"<html>", {}
    kind, message, _ = jev.call_jev(server, {})
    assert kind == "network" and "non-JSON" in message


def test_retry_delay_defaults_to_one_second():
    assert (jev.retry_delay("2.5"), jev.retry_delay(None), jev.retry_delay("Wed, 21 Oct")) == (2.5, 1.0, 1.0)
    assert not math.isnan(jev.retry_delay("0"))
```

- [ ] **Step 3: Run the tests; they fail because the module does not exist**

```bash
cd $R && python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py -q
```

Expected: collection error, `ModuleNotFoundError: No module named 'jev_rerank'`.

- [ ] **Step 4: Write** `Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py`

```python
#!/usr/bin/env python3
"""SimpleJEV pointwise relevance judge over a preserved TREC pool (spec
docs/specs/2026-09-29-simple-jev-judge-design.md). One `/v1/classifier` request per query: the query
is the shared `state`, and each pool document is one question (`noul` by default, `score` for the
screen's rubric arm). The pool is re-sorted by the judge's value, exact ties kept in baseline order,
and written as a 6-column TREC run with rank-derived scores (`51 - position`), so ir_measures'
doc-id tie-break can never apply (spec §4).

Serves both stages: the model screen against the public demo API (NFCorpus, spec §5) and the gate
passes against a self-hosted hf_server on a rented pod (SciFact, spec §6). A query that exhausts
its attempts falls back to baseline order and is counted; above floor(0.05 * n) fallbacks no run
file is written, and the ledger is kept so a rerun resumes.

Stdlib only. Loaders, subsample, ledger, input and pass checks are reused from teacher_rerank.py
(their refusal messages are prefixed `[teacher_rerank]`).

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py \\
        --pool nfcorpus-bge-base-2026-09-04/runs/bge-base.chunks.trec \\
        --corpus nfcorpus-bge-base-2026-09-04/beir/corpus.jsonl \\
        --queries nfcorpus-bge-base-2026-09-04/beir/queries.jsonl \\
        --base-url https://simple-jev-demo-api.featherless.ai \\
        --model featherless-ai/Qwen3.5-4B-classifier --question-type noul \\
        --responses simple-jev-2026-09/screen/arm1-qwen4b.responses.jsonl \\
        --out simple-jev-2026-09/screen/arm1-qwen4b.chunks.trec --composite 7d3a15092f963723
"""
import argparse
import hashlib
import json
import math
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import teacher_rerank as tr  # noqa: E402  (loaders, subsample, ledger, input/pass checks -- reused, never copied)

RUN_TAG = "simple-jev"
USER_AGENT = "curl/8.5.0"   # spec §5: Python's default urllib agent gets Cloudflare 403 error 1010
MAX_ATTEMPTS = 4            # spec §5: up to 4 attempts in total
MIN_INTERVAL_SECONDS = 0.5  # spec §5: the demo is rate-limited to 2 requests per second
TIMEOUT_SECONDS = 300       # spec §5

# The frozen judge wording (spec §4). WORDING_SHA256 is recorded in every sidecar.
STATE_TEMPLATE = "Search query: {query}"
INSTRUCTIONS = {
    "noul": "Is the following document relevant to the search query?\n\nDocument:\n{text}",
    "score": "How relevant is the following document to the search query?\n\nDocument:\n{text}",
}
CRITERIA = {
    "noul": {
        "true": "The document contains information that answers or directly bears on the query.",
        "false": "The document does not.",
    },
    "score": [
        "Unrelated to the query",
        "Same topic, but does not answer the query",
        "Partially answers or bears on the query",
        "Directly answers or bears on the query",
    ],
}
VALUE_RANGE = {"noul": (0.0, 1.0), "score": (0.0, 3.0)}
WORDING_SHA256 = hashlib.sha256(
    json.dumps({"state": STATE_TEMPLATE, "instructions": INSTRUCTIONS, "criteria": CRITERIA},
               sort_keys=True).encode("utf-8")
).hexdigest()


def build_request(model, question_type, query_text, doc_texts):
    """The spec §4 body: the query as the shared state and one question per pool document, keyed
    d0..d{n-1} in baseline order."""
    questions = {
        f"d{i}": {
            "type": question_type,
            "instructions": INSTRUCTIONS[question_type].format(text=text),
            "criteria": CRITERIA[question_type],
        }
        for i, text in enumerate(doc_texts)
    }
    return {"model": model, "state": STATE_TEMPLATE.format(query=query_text), "questions": questions}


def judge_values(response, question_type, n):
    """Spec §4 acceptance: every d<i> present, each value a finite number inside its type's range
    (noul [0, 1], score [0, 3]). Returns (values, None) or (None, reason)."""
    answers = response.get("answers") if isinstance(response, dict) else None
    if not isinstance(answers, dict):
        return None, "response has no answers object"
    low, high = VALUE_RANGE[question_type]
    values = []
    for i in range(n):
        answer = answers.get(f"d{i}")
        value = answer.get(question_type) if isinstance(answer, dict) else None
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None, f"d{i}: no numeric {question_type} value"
        if not math.isfinite(value) or not low <= value <= high:
            return None, f"d{i}: {question_type} {value!r} outside [{low}, {high}]"
        values.append(float(value))
    return values, None


def order_by_judge(pool_ids, values):
    """Judge value descending. Python's sort is stable, so exact ties keep baseline order (spec §4)."""
    ranked = sorted(range(len(pool_ids)), key=lambda i: -values[i])
    return [pool_ids[i] for i in ranked]


def call_jev(base_url, body):
    """POSTs `body` to `{base_url}/v1/classifier`. Returns (kind, payload, retry_after):
    ("ok", parsed_json, None), ("http", (code, body_text), Retry-After header or None), or
    ("network", message, None). HTTPError is caught before OSError because it subclasses it; a
    200 whose body is not JSON is reported as a network-kind failure, so it is retried."""
    req = urllib.request.Request(
        f"{base_url}/v1/classifier", data=json.dumps(body).encode("utf-8"), method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("User-Agent", USER_AGENT)
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT_SECONDS) as resp:
            raw = resp.read()
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", errors="replace")[:2000]
        return "http", (e.code, text), e.headers.get("Retry-After")
    except OSError as e:
        return "network", str(e), None
    try:
        return "ok", json.loads(raw), None
    except ValueError as e:
        return "network", f"non-JSON 200 body: {e}", None


def retry_delay(retry_after):
    """Seconds to wait after a 429: the Retry-After value, or 1 s when it is absent or not a number."""
    try:
        return max(float(retry_after), 0.0)
    except (TypeError, ValueError):
        return 1.0


def pass_identity(args):
    """What makes two invocations the same pass (spec §5: each pass has its own ledger). Stricter
    than teacher_rerank's: the endpoint, model and question type are included, so a demo ledger can
    never be replayed on the pod and one screen arm's ledger never into another."""
    return {
        "pool": os.path.abspath(args.pool),
        "baseUrl": args.base_url,
        "model": args.model,
        "questionType": args.question_type,
        "subsample": args.subsample,
        "subsampleSeed": args.subsample_seed,
    }


def score_query(query_id, pool_ids, query_text, corpus, args, ledger, call=None, sleep=None):
    """Returns (order, fell_back). An accepted ledger entry is reused without a request (resume).
    Otherwise up to MAX_ATTEMPTS requests, each appended to the ledger (spec §5): 403 aborts the
    whole invocation; 429 sleeps Retry-After and retries; 400, 422 and every other 4xx fall back at
    once; 5xx, network errors and rejected bodies are retried. Fallback is baseline order."""
    call = call or call_jev
    sleep = sleep or time.sleep
    pass_id = pass_identity(args)
    accepted = tr.accepted_entry(ledger.get(str(query_id), []))
    if accepted is not None:
        order = accepted.get("order") or []
        if sorted(order) != sorted(pool_ids):
            sys.exit(f"[jev_rerank] resumed ledger entry for query {query_id!r} is not a "
                     "permutation of its pool")
        return order, False

    body = build_request(args.model, args.question_type, query_text,
                         [corpus[doc_id][1] for doc_id in pool_ids])
    for _attempt in range(MAX_ATTEMPTS):
        sleep(MIN_INTERVAL_SECONDS)
        kind, payload, retry_after = call(args.base_url, body)
        if kind == "ok":
            values, reason = judge_values(payload, args.question_type, len(pool_ids))
            if values is not None:
                order = order_by_judge(pool_ids, values)
                content = json.dumps({"values": values, "metadata": payload.get("metadata")})
                input_tokens = (payload.get("usage") or {}).get("input_tokens")
                tr.append_response(args.responses, tr.make_record(
                    query_id, "accepted", content, order, None, pass_id, prompt_tokens=input_tokens))
                return order, False
            tr.append_response(args.responses, tr.make_record(
                query_id, "rejected", json.dumps(payload)[:2000], None, reason, pass_id))
            continue
        if kind == "network":
            tr.append_response(args.responses, tr.make_record(
                query_id, "rejected", payload, None, "network error", pass_id))
            continue
        code, text = payload
        tr.append_response(args.responses, tr.make_record(
            query_id, "rejected", text, None, f"HTTP {code}", pass_id))
        if code == 403:
            sys.exit(f"[jev_rerank] HTTP 403 from {args.base_url} -- a configuration error, not a "
                     f"per-query failure (spec §5); aborting. Body: {text[:300]}")
        if code == 429:
            sleep(retry_delay(retry_after))
            continue
        if 400 <= code < 500:
            break  # 400, 422 and every other 4xx: not retryable, fall back at once
    return list(pool_ids), True


def write_run(path, scored_pool, pool_query_order):
    """6-column TREC with `score = 51 - position` (spec §4) and this script's own run tag."""
    with open(path, "w", encoding="utf-8") as f:
        for query_id in pool_query_order:
            for position, doc_id in enumerate(scored_pool[query_id], start=1):
                f.write(f"{query_id} Q0 {doc_id} {position} {51 - position:.6f} {RUN_TAG}\n")


def write_sidecar(out_path, args, fallback_ids):
    """The run's .meta.json (spec §6.3): the pool's own composite, so report.py sees one build,
    and a reranker block whose provenance fields come verbatim from the record-only flags (spec §9)."""
    sidecar = {
        "configLabel": RUN_TAG,
        "composite": args.composite,
        "recordedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "reranker": {
            "baseUrl": args.base_url,
            "modelId": args.model,
            "questionType": args.question_type,
            "wordingSha256": WORDING_SHA256,
            "serverCommit": args.server_commit,
            "revision": args.revision,
            "policy": args.policy,
            "maxBatchSize": args.max_batch_size,
            "maxModelLen": args.max_model_len,
            "preflightInputTokens": args.preflight_input_tokens,
            "subsample": args.subsample,
            "subsampleSeed": args.subsample_seed,
            "fallbackCount": len(fallback_ids),
            "fallbackQueryIds": fallback_ids,
        },
    }
    with open(tr.sidecar_path_for(out_path), "w", encoding="utf-8") as f:
        json.dump(sidecar, f, indent=2)
        f.write("\n")


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pool", required=True, help="baseline TREC run whose per-query pools are judged")
    ap.add_argument("--corpus", required=True, help="BEIR corpus.jsonl (keys: _id, title, text)")
    ap.add_argument("--queries", required=True, help="BEIR queries.jsonl (keys: _id, text)")
    ap.add_argument("--base-url", required=True, help="server root; requests go to {base-url}/v1/classifier")
    ap.add_argument("--model", required=True, help="served model id sent in every request")
    ap.add_argument("--question-type", choices=["noul", "score"], default="noul")
    ap.add_argument("--responses", required=True, help="JSONL ledger for this pass (appended to; resumable)")
    ap.add_argument("--out", required=True, help="run file; must end <label>.chunks.trec so report.py finds the sidecar")
    ap.add_argument("--composite", required=True, help="the pool's own build composite, from its .meta.json")
    ap.add_argument("--subsample", type=int, default=None, help="if given (with --subsample-seed), judge only N query ids")
    ap.add_argument("--subsample-seed", type=int, default=None, help="seed for --subsample's selection")
    provenance = "recorded verbatim in the sidecar's reranker block; never sent to the server; omit to record null"
    ap.add_argument("--server-commit", default=None, help=provenance)
    ap.add_argument("--revision", default=None, help=provenance)
    ap.add_argument("--policy", default=None, help=provenance)
    ap.add_argument("--max-batch-size", type=int, default=None, help=provenance)
    ap.add_argument("--max-model-len", type=int, default=None, help=provenance)
    ap.add_argument("--preflight-input-tokens", type=int, default=None, help=provenance)
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    if (args.subsample is None) != (args.subsample_seed is None):
        sys.exit("--subsample and --subsample-seed must be given together")

    pool = tr.load_run(args.pool)
    corpus = tr.load_corpus(args.corpus)
    queries = tr.load_queries(args.queries)

    pool_query_order = list(pool.keys())
    if args.subsample is not None:
        if args.subsample > len(pool_query_order):
            sys.exit(f"--subsample {args.subsample} exceeds the pool's {len(pool_query_order)} query ids")
        pool_query_order = tr.select_subsample(pool_query_order, args.subsample, args.subsample_seed)

    tr.validate_inputs_before_any_model_call(pool, pool_query_order, corpus, queries)
    ledger = tr.read_responses_ledger(args.responses)
    tr.validate_ledger_pass(args.responses, ledger, pass_identity(args))

    scored_pool = {}
    fallback_ids = []
    for query_id in pool_query_order:
        order, fell_back = score_query(query_id, pool[query_id], queries[query_id], corpus, args, ledger)
        scored_pool[query_id] = order
        if fell_back:
            fallback_ids.append(query_id)

    cap = len(pool_query_order) * 5 // 100  # floor(0.05 * n), in integers (spec §5)
    if len(fallback_ids) > cap:
        sys.exit(
            f"[jev_rerank] {len(fallback_ids)} / {len(pool_query_order)} queries fell back, over the "
            f"cap of {cap} (spec §5) -- no run file written; the ledger is kept, so rerunning resumes. "
            f"Fallbacks: {fallback_ids}"
        )

    write_run(args.out, scored_pool, pool_query_order)
    write_sidecar(args.out, args, fallback_ids)
    print(f"[jev_rerank] wrote {args.out} ({len(pool_query_order)} queries, {len(fallback_ids)} fallbacks)")


if __name__ == "__main__":
    main()
```

- [ ] **Step 5: Run both suites**

```bash
cd $R && python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py -q \
  && python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q
```

Expected: `33 passed`, then `44 passed` (the reused module is untouched).

- [ ] **Step 6: Mutation check** — every mutation must be killed by at least one test

Write the checker outside the repo, run it from the scripts directory, then confirm the source is restored:

```bash
mkdir -p $A && cat > $A/mutate.py <<'MUTATE'
"""One-line mutations of jev_rerank.py; each must make at least one test fail. Restores the file."""
import subprocess, sys
M = [
 ("M1", 'INSTRUCTIONS[question_type].format(text=text)', 'INSTRUCTIONS[question_type].format(text="")'),
 ("M2", '"noul": "Is the following document relevant to the search query?', '"noul": "Is this document relevant?'),
 ("M3", '"criteria": CRITERIA[question_type],', '"criteria": CRITERIA["noul"],'),
 ("M4", 'answer = answers.get(f"d{i}")', 'answer = answers.get(f"d{i}", {question_type: 0.5})'),
 ("M5", 'if not math.isfinite(value) or not low <= value <= high:', 'if False:'),
 ("M6", 'if isinstance(value, bool) or not isinstance(value, (int, float)):', 'if not isinstance(value, (int, float)):'),
 ("M7", 'key=lambda i: -values[i])', 'key=lambda i: (-values[i], pool_ids[i]))'),
 ("M8", '{51 - position:.6f}', '{50 - position:.6f}'),
 ("M9", 'RUN_TAG = "simple-jev"', 'RUN_TAG = "teacher-ceiling"'),
 ("M10", 'sleep(retry_delay(retry_after))', 'pass'),
 ("M11", 'break  # 400, 422', 'continue  # 400, 422'),
 ("M12", 'if code == 403:', 'if code == 999:'),
 ("M13", 'MAX_ATTEMPTS = 4 ', 'MAX_ATTEMPTS = 3 '),
 ("M14", '        sleep(MIN_INTERVAL_SECONDS)\n', ''),
 ("M15", 'if accepted is not None:', 'if False:'),
 ("M16", '        "model": args.model,\n        "questionType"', '        "questionType"'),
 ("M17", 'if len(fallback_ids) > cap:', 'if len(fallback_ids) >= cap:'),
 ("M18", 'cap = len(pool_query_order) * 5 // 100', 'cap = len(pool_query_order)'),
 ("M19", '"revision": args.revision,', '"revision": None,'),
 ("M20", 'USER_AGENT = "curl/8.5.0"', 'USER_AGENT = "Python-urllib/3.14"'),
 ("M21", 'e.headers.get("Retry-After")', 'None'),
 ("M22", 'return "network", f"non-JSON 200 body: {e}", None', 'return "ok", {}, None'),
 ("M23", '        return 1.0\n', '        return 0.0\n'),
 ("M24", 'prompt_tokens=input_tokens', 'prompt_tokens=None'),
 ("M25", '"composite": args.composite,', '"composite": None,'),
 ("M26", '"true": "The document contains information that answers or directly bears on the query.",', '"true": "Relevant.",'),
 ("M27", 'return [pool_ids[i] for i in ranked]', 'return list(reversed([pool_ids[i] for i in ranked]))'),
]
orig = open("jev_rerank.py", encoding="utf-8").read()
bad = 0
try:
    for name, a, b in M:
        if orig.count(a) != 1:
            print(name, "PATTERN COUNT", orig.count(a)); bad += 1; continue
        open("jev_rerank.py", "w", encoding="utf-8").write(orig.replace(a, b))
        r = subprocess.run([sys.executable, "-m", "pytest", "test_jev_rerank.py", "-q", "-p", "no:cacheprovider"],
                           capture_output=True, text=True)
        failed = sorted({l.split("::")[1].split(" ")[0].split("[")[0] for l in r.stdout.splitlines() if l.startswith("FAILED")})
        print(name, "KILLED" if failed else "SURVIVED", failed)
        bad += not failed
finally:
    open("jev_rerank.py", "w", encoding="utf-8").write(orig)
print("survivors/pattern errors:", bad)
MUTATE
cd $S && python3 $A/mutate.py && git -C $R diff --exit-code && git -C $R status --short
```

Expected: 27 lines ending `KILLED`, then `survivors/pattern errors: 0`; `git diff` is empty and `status` lists only the two new untracked files.

| Mutation | What it breaks | Killed by (at least) |
|---|---|---|
| M1 | document text left out of the instructions | `test_build_request_has_one_question_per_pool_doc_in_order_carrying_its_text` |
| M2, M26 | frozen `noul` wording or criteria changed | `test_build_request_uses_the_frozen_noul_wording` |
| M3 | `score` questions sent with `noul` criteria | `test_build_request_score_variant_uses_the_four_level_rubric` |
| M4 | a missing `d<i>` accepted | `test_judge_values_rejects_a_missing_question_id` |
| M5 | NaN / ±inf / out-of-range accepted | `test_judge_values_rejects_non_finite_out_of_range_and_non_numeric_noul` |
| M6 | a JSON boolean accepted as a number | the same parametrized test (`True`) |
| M7 | ties broken by doc id instead of baseline order | `test_order_by_judge_sorts_descending_and_keeps_baseline_order_on_ties` |
| M8, M9 | wrong score column or run tag | `test_write_run_writes_51_minus_position_and_the_simple_jev_tag` |
| M10 | 429 retried without honouring `Retry-After` | `test_429_sleeps_retry_after_then_retries` |
| M11 | 400/404/422 retried | `test_other_4xx_fall_back_to_baseline_after_one_call` |
| M12 | 403 treated as a per-query failure | `test_403_aborts_the_whole_invocation` |
| M13 | three attempts instead of four | `test_5xx_is_retried_up_to_four_attempts_then_falls_back` |
| M14 | no pacing before a request | `test_every_request_is_preceded_by_the_minimum_interval` |
| M15 | resume ignored | `test_an_accepted_ledger_entry_is_reused_without_a_request` |
| M16 | another arm's ledger replayed | `test_main_refuses_a_ledger_recorded_by_another_model` |
| M17, M18 | fallback cap off by one / disabled | `test_main_at_the_fallback_cap_…`, `test_main_over_the_fallback_cap_writes_no_run_file` |
| M19, M25 | provenance or composite dropped from the sidecar | `test_sidecar_records_provenance_flags_verbatim_and_null_when_omitted` |
| M20 | Python's default user agent sent | `test_call_jev_posts_json_with_the_curl_user_agent_and_returns_the_parsed_body` |
| M21 | `Retry-After` not returned | `test_call_jev_returns_the_http_code_body_and_retry_after` |
| M22 | a non-JSON 200 accepted | `test_call_jev_reports_a_non_json_200_as_a_network_kind_failure` |
| M23 | missing `Retry-After` waits 0 s | `test_retry_delay_defaults_to_one_second` |
| M24 | `usage.input_tokens` not recorded | `test_success_records_an_accepted_entry_with_order_values_and_input_tokens` |
| M27 | order written ascending | `test_order_by_judge_…` and four others |

- [ ] **Step 7: Commit**

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/jev_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_jev_rerank.py
git -C $R commit -m "add the SimpleJEV relevance-judge client and its tests"
```

### Task 2: Stub server and the two-sided dry run

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py`

**Interfaces:**
- Consumes: `jev_rerank.py` (Task 1).
- Produces: the dry-run pass that Task 4 requires before any rental.

- [ ] **Step 1: Write** `Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py`

```python
#!/usr/bin/env python3
"""Stub SimpleJEV `/v1/classifier` server for the two-sided dry run (spec
docs/specs/2026-09-29-simple-jev-judge-design.md §6.1). Stands in for the rented pod so jev_rerank.py
can be exercised end to end -- request construction, acceptance, ordering, the run writer and the
scorer -- for zero cost.

It recovers the query from the request's `state` ("Search query: <text>", jev_rerank.py's
STATE_TEMPLATE) and answers question d<i> with noul `1 - i/n` in `identity` mode (the baseline order
back) or `i/n` in `reversed` mode. Two exactly-pinned outcomes are what make the dry run able to
fail: a pipeline that discarded the reply and wrote baseline order would score identically in both
modes (the teacher-ceiling lesson).

It answers 422 when any question's instructions lack the text of the pool document at that position
(the teacher-ceiling defect where prompts carried a bare doc id while every test stayed green), when
the question ids do not match the pool, or for any non-noul question.

Stdlib only. Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py \\
        --pool scifact-bge-base-2026-09-04/runs/bge-base.chunks.trec \\
        --corpus scifact-bge-base-2026-09-04/beir/corpus.jsonl \\
        --queries scifact-bge-base-2026-09-04/beir/queries.jsonl \\
        --mode identity --port 18791

Prints the bound port to stdout, then serves until killed.
"""
import argparse
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import stub_vllm_server  # noqa: E402  (load_queries_by_text -- reused, never copied)
import teacher_rerank as tr  # noqa: E402  (load_run, load_corpus -- reused, never copied)

STATE_PREFIX = "Search query: "  # jev_rerank.STATE_TEMPLATE's fixed prefix


def reply(body, pool, corpus, queries_by_text, mode):
    """Returns (status, payload) for one /v1/classifier body."""
    state = body.get("state")
    if not isinstance(state, str) or not state.startswith(STATE_PREFIX):
        return 422, {"detail": f"state must start with {STATE_PREFIX!r}"}
    query_id = queries_by_text.get(state[len(STATE_PREFIX):])
    if query_id is None:
        return 422, {"detail": "state names no known query"}
    doc_ids = pool[query_id]
    n = len(doc_ids)
    questions = body.get("questions") or {}
    if sorted(questions) != sorted(f"d{i}" for i in range(n)):
        return 422, {"detail": f"question ids do not match query {query_id}'s {n}-document pool"}
    result = {}
    for i, doc_id in enumerate(doc_ids):
        question = questions[f"d{i}"]
        if question.get("type") != "noul":
            return 422, {"detail": f"d{i}: this stub answers noul questions only"}
        if corpus[doc_id][1] not in (question.get("instructions") or ""):
            return 422, {"detail": f"d{i}: instructions lack the text of pool document {doc_id}"}
        value = 1 - i / n if mode == "identity" else i / n
        result[f"d{i}"] = {"type": "noul", "noul": value}
    return 200, {"model": body.get("model"), "answers": result,
                 "usage": {"input_tokens": 0, "output_tokens": 0}}


def make_handler(pool, corpus, queries_by_text, mode):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):
            pass  # keep the dry run's console quiet; failures still surface as HTTP statuses

        def do_POST(self):
            if self.path != "/v1/classifier":
                self.send_error(404, f"unhandled path {self.path!r}")
                return
            try:
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                status, payload = reply(body, pool, corpus, queries_by_text, mode)
            except Exception as e:  # fail loud: a 500 is retried and then counted as a fallback
                status, payload = 500, {"detail": f"{type(e).__name__}: {e}"}
            data = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    return Handler


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pool", required=True, help="the baseline TREC run jev_rerank.py judges")
    ap.add_argument("--corpus", required=True, help="BEIR corpus.jsonl (keys: _id, title, text)")
    ap.add_argument("--queries", required=True, help="BEIR queries.jsonl (keys: _id, text)")
    ap.add_argument("--mode", choices=["identity", "reversed"], required=True)
    ap.add_argument("--port", type=int, default=0, help="port on 127.0.0.1; 0 (default) picks an ephemeral port")
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    handler = make_handler(tr.load_run(args.pool), tr.load_corpus(args.corpus),
                           stub_vllm_server.load_queries_by_text(args.queries), args.mode)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), handler)
    print(server.server_address[1], flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Start both stub modes and run the negative probe** (prelude from Task 1 first)

```bash
mkdir -p $A/dryrun
python3 $S/stub_jev_server.py --pool $SF/runs/bge-base.chunks.trec --corpus $SF/beir/corpus.jsonl \
  --queries $SF/beir/queries.jsonl --mode identity --port 18791 > $A/dryrun/stub-identity.port 2>&1 &
echo $! > $A/dryrun/stub-identity.pid
python3 $S/stub_jev_server.py --pool $SF/runs/bge-base.chunks.trec --corpus $SF/beir/corpus.jsonl \
  --queries $SF/beir/queries.jsonl --mode reversed --port 18792 > $A/dryrun/stub-reversed.port 2>&1 &
echo $! > $A/dryrun/stub-reversed.pid
sleep 3 && cat $A/dryrun/stub-*.port
S=$S SF=$SF python3 - <<'PY'
import os, sys
sys.path.insert(0, os.environ["S"])
import jev_rerank as jev
pool = jev.tr.load_run(os.environ["SF"] + "/runs/bge-base.chunks.trec")
queries = jev.tr.load_queries(os.environ["SF"] + "/beir/queries.jsonl")
qid = next(iter(pool))
body = jev.build_request("stub", "noul", queries[qid], ["not the document"] * len(pool[qid]))
print(jev.call_jev("http://127.0.0.1:18791", body)[:2])
PY
```

Expected: `18791`, `18792`, then `('http', (422, '{"detail": "d0: instructions lack the text of pool document 21456232"}'))`.

- [ ] **Step 3: Run both 300-query passes** (≈ 150 s each; they run in parallel)

```bash
for mode in identity reversed; do
  port=$([ $mode = identity ] && echo 18791 || echo 18792)
  python3 $S/jev_rerank.py --pool $SF/runs/bge-base.chunks.trec --corpus $SF/beir/corpus.jsonl \
    --queries $SF/beir/queries.jsonl --base-url http://127.0.0.1:$port --model stub \
    --responses $A/dryrun/$mode.responses.jsonl --out $A/dryrun/$mode.chunks.trec --composite 7d3a15092f963723 &
done; wait; kill $(cat $A/dryrun/stub-*.pid)
```

Expected: two `[jev_rerank] wrote … (300 queries, 0 fallbacks)` lines.

- [ ] **Step 4: Score both with `--pair`; both outcomes must match exactly**

```bash
for mode in identity reversed; do
  PYTHONPATH=$B/python-libs python3 $S/report.py --run $A/dryrun/$mode.chunks.trec --run $SF/runs/bge-base.chunks.trec \
    --qrels $SF/qrels.trec --pair $A/dryrun/$mode.chunks.trec=$SF/runs/bge-base.chunks.trec > $A/dryrun/report-$mode.txt 2>&1
  echo "$mode exit $?"; grep -E "^\[scores\]|nDCG@10 |R@50 |^\[pool\]" $A/dryrun/report-$mode.txt
done
md5sum $A/dryrun/*.chunks.trec | tee $A/dryrun/md5s.txt
```

Expected (verified at plan-write time):
- `identity exit 1`; the identity run's `nDCG@10    0.7452`, `R@50       0.9337`; `[pool] … sequence differs on 0 (0.0%)` and `ARM INVALID`.
- `reversed exit 0`; the reversed run's `nDCG@10    0.0011`, `R@50       0.9337`; `[pool] … sequence differs on 300 (100.0%)`.
- md5s `3e46051e2386578c6664e8840c46eaf2` (identity) and `c7c7a298c9dfa13cff930dde7e0b21da` (reversed).

Any other outcome stops the plan: the pipeline is not using the judge's reply.

- [ ] **Step 5: Commit**

```bash
git -C $R add Iverson.Server/Iverson.LoadTest/scripts/stub_jev_server.py
git -C $R commit -m "add the SimpleJEV stub server for the two-sided dry run"
```

### Task 3: Model screen on the public demo (NFCorpus, $0, ≈ 4–8 h)

**Files:** none in the repo. Artifacts in `$A/screen/`.

**Interfaces:**
- Consumes: `jev_rerank.py` (Task 1).
- Produces: the winner (demo model id and question type) and its ledger, used by Tasks 4 and 5.

- [ ] **Step 1: Write the demo check and the arm runner** (prelude from Task 1 first)

```bash
mkdir -p $A/screen && cat > $A/screen/check-demo.sh <<'CHECK'
#!/usr/bin/env bash
# Blocking demo check (CIR-1 §3.1, option a): one real §4 request -- NFCorpus PLAIN-478, the largest body --
# for model $2 and question type $3 must return 200 with no redirect. Since 2026-09-30 the demo has answered
# every request with 301 -> featherless.ai/simple-jev/... (HTML), which jev_rerank.py would follow as a GET
# and retry into a fallback-cap failure. Exit 0: the demo serves. Exit 1: it does not (status printed).
export S=/home/ben/repositories/Iverson/.worktrees/simple-jev-judge/Iverson.Server/Iverson.LoadTest/scripts
OUT=/home/ben/repositories/iverson-benchmark-corpora/simple-jev-2026-09/screen
python3 - "$2" "$3" > $OUT/$1.check.json <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["S"])
import jev_rerank as jev
nf = "/home/ben/repositories/iverson-benchmark-corpora/nfcorpus-bge-base-2026-09-04"
pool = jev.tr.load_run(nf + "/runs/bge-base.chunks.trec")
corpus = jev.tr.load_corpus(nf + "/beir/corpus.jsonl")
query = jev.tr.load_queries(nf + "/beir/queries.jsonl")["PLAIN-478"]
print(json.dumps(jev.build_request(sys.argv[1], sys.argv[2], query, [corpus[d][1] for d in pool["PLAIN-478"]])))
PY
STATUS=$(curl -s -m 300 -A curl/8.5.0 -H 'Content-Type: application/json' --data-binary @$OUT/$1.check.json \
  -o /dev/null -w '%{http_code} %{redirect_url}' https://simple-jev-demo-api.featherless.ai/v1/classifier)
echo "$1 demo check: '$STATUS'"
[ "$STATUS" = "200 " ]
CHECK
cat > $A/screen/run-arm.sh <<'ARM'
#!/usr/bin/env bash
# One screen arm (spec §5): $1 label, $2 demo model id, $3 question type. The arm runs only if the blocking
# demo check passes first; an arm that ends over the fallback cap (jev_rerank.py exits 1 and writes no run
# file) is invalid for selection.
S=/home/ben/repositories/Iverson/.worktrees/simple-jev-judge/Iverson.Server/Iverson.LoadTest/scripts
NF=/home/ben/repositories/iverson-benchmark-corpora/nfcorpus-bge-base-2026-09-04
OUT=/home/ben/repositories/iverson-benchmark-corpora/simple-jev-2026-09/screen
bash $OUT/check-demo.sh "$1" "$2" "$3" || { echo "$1 $2 $3 demo-unavailable" >> $OUT/arms.status; exit 1; }
python3 $S/jev_rerank.py --pool $NF/runs/bge-base.chunks.trec --corpus $NF/beir/corpus.jsonl \
  --queries $NF/beir/queries.jsonl --base-url https://simple-jev-demo-api.featherless.ai \
  --model "$2" --question-type "$3" --responses $OUT/$1.responses.jsonl \
  --out $OUT/$1.chunks.trec --composite 7d3a15092f963723 > $OUT/$1.log 2>&1
echo "$1 $2 $3 exit $?" >> $OUT/arms.status
ARM
```

- [ ] **Step 2: Run arms 1–5 in order, detached** — only once the demo is serving (CIR-1 §3.1, option a; Ben, 2026-09-30)

```bash
bash $A/screen/check-demo.sh arm1-qwen4b featherless-ai/Qwen3.5-4B-classifier noul
```

Required: `arm1-qwen4b demo check: '200 '` and exit 0. Otherwise the demo is still down (on 2026-09-30 this printed `'301 https://featherless.ai/simple-jev/classifier'`): do not launch. Task 3, and so Tasks 4–5, wait; re-run the check later.

```bash
setsid nohup bash -c "
bash $A/screen/run-arm.sh arm1-qwen4b featherless-ai/Qwen3.5-4B-classifier noul
bash $A/screen/run-arm.sh arm2-gemma12b featherless-ai/gemma-4-12B-it-classifier noul
bash $A/screen/run-arm.sh arm3-gemma26b featherless-ai/gemma-4-26B-A4B-classifier noul
bash $A/screen/run-arm.sh arm4-qwen35b featherless-ai/Qwen3.6-35B-A3B-classifier noul
bash $A/screen/run-arm.sh arm5-qwen27b featherless-ai/Qwen3.8-27B-classifier noul
" > /dev/null 2>&1 &
```

Monitor with `cat $A/screen/arms.status; wc -l $A/screen/*.responses.jsonl`. A 403 in any arm's log means the user agent or endpoint is wrong: stop and fix, don't continue. An interrupted arm resumes by rerunning its line (same ledger). `demo-unavailable` in `arms.status` means the demo stopped serving before that arm: once `check-demo.sh` passes again, rerun that arm's line (its ledger resumes). If no arm ends valid, Task 3 is not done: wait for the demo and rerun.

- [ ] **Step 3: Score arms 1–5 and pick the `noul` leader**

```bash
PYTHONPATH=$B/python-libs python3 $S/report.py --run $A/screen --qrels $NF/qrels.trec > $A/screen/report-arms1-5.txt 2>&1
grep -E "^\[scores\]|nDCG@10 " $A/screen/report-arms1-5.txt; cat $A/screen/arms.status
```

The leader is the valid arm (`exit 0` in `arms.status`) with the highest `nDCG@10`; exact ties go to the earlier arm.

- [ ] **Step 4: Run arm 6 (the leader's model with the `score` rubric) and score all six**

```bash
bash $A/screen/run-arm.sh arm6-score <leader's demo model id> score
PYTHONPATH=$B/python-libs python3 $S/report.py --run $A/screen --qrels $NF/qrels.trec > $A/screen/report-arms1-6.txt 2>&1
grep -E "^\[scores\]|nDCG@10 " $A/screen/report-arms1-6.txt; cat $A/screen/arms.status
```

The **winner** is the valid arm with the highest NFCorpus nDCG@10 across all six, with ties going to the earlier arm (spec §5). If arm 6 wins, the gate uses `--question-type score` on that model; otherwise `noul`.

- [ ] **Step 5: Record the screen** in `$A/screen/screen.md`: one row per arm (label, model, question type, NFCorpus nDCG@10, fallback count from the arm's `.meta.json` or its log, valid yes/no), the winner, and `md5sum $A/screen/*.chunks.trec`. Map the winner for Task 4:

| Demo model id | HF repo | Policy (code's `KNOWN_PROFILES`) | Card (spec §6.2) |
|---|---|---|---|
| `featherless-ai/Qwen3.5-4B-classifier` | `Qwen/Qwen3.5-4B` | `shared_examples_binary` | 80 GB |
| `featherless-ai/gemma-4-12B-it-classifier` | `google/gemma-4-12B-it` | `shared_repeat_state` | 80 GB |
| `featherless-ai/gemma-4-26B-A4B-classifier` | `google/gemma-4-26B-A4B-it` | `shared_examples_binary` | H200 |
| `featherless-ai/Qwen3.6-35B-A3B-classifier` | `Qwen/Qwen3.6-35B-A3B` | `shared_repeat_state` | H200 |
| `featherless-ai/Qwen3.8-27B-classifier` | `Qwen/Qwen3.8-27B` | `shared_examples_binary` | H200 (expect `--max-batch-size` ≤ 16) |

No commit (nothing in the repo changes).

### Task 4: Gate passes on a rented pod (PAID)

**Files:** none in the repo. Artifacts land in `$A/pod/`.

**Interfaces:**
- Consumes: Task 1's two scripts, Task 2's passing dry run, Task 3's winner.
- Produces: `jev-main.chunks.trec`, `jev-repeat.chunks.trec`, `transfer.responses.jsonl` (plus sidecars, ledgers and server logs) for Task 5.

The pod steps share one ephemeral machine, so they are one task. Launch every long-running process with `setsid nohup` (the stage-1 SIGTERM finding). Steps 4–13 on the pod share one shell's variables (`REPO`, `SHA`, `POLICY`, `MBS`, `I`, `O`, `QT`, `PFT`, `PROV`); if that shell is lost, re-set them from the values recorded so far before continuing.

- [ ] **Step 1: STOP and ask Ben for the go-ahead.** State the winner, the card from Task 3's table, and the expected spend (≈ 1–2 GPU-hours plus the model download). Do not rent anything without an explicit yes.

- [ ] **Step 2: Rent the pod** on RunPod: the card from the table, a **PyTorch** template (not `vllm/vllm-openai`, whose entrypoint serves a model and holds the GPU), container disk ≥ 150 GB **and volume disk ≥ 150 GB** (`HF_HOME` is on the `/workspace` volume). Record the start time.

- [ ] **Step 3: Check the image before installing anything** (U1)

```bash
python3 --version
python3 -c "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0))"
df -h /workspace
```

Required: Python ≥ 3.12, torch ≥ 2.6, `True`, and at least 100 GB available on `/workspace`. Otherwise terminate and re-rent with a newer template or a larger volume.

- [ ] **Step 4: Install SimpleJEV at the pinned commit and resolve the model revision**

```bash
export HF_HOME=/workspace/hf
cd /workspace && git clone https://github.com/featherless-ai/simple-jev.git && cd simple-jev \
  && git checkout 7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3 && python3 -m pip install -e './hf-server'
REPO=<HF repo from Task 3's table>; POLICY=<policy from Task 3's table>
SHA=$(python3 -c "from huggingface_hub import HfApi; print(HfApi().model_info('$REPO').sha)"); echo $SHA
```

Compare `$SHA`'s prefix with spec §6.2's observed revision. A different revision is allowed (the full sha goes in the sidecar) but is noted in the gate doc.

- [ ] **Step 5: Confirm the policy the code auto-selects** (spec §6.2)

```bash
cd /workspace && setsid nohup env HF_HOME=/workspace/hf simple-jev --model $REPO --revision $SHA \
  --max-model-len 65536 --dtype bfloat16 > /workspace/server-auto.log 2>&1 &
until curl -sf 127.0.0.1:8000/health; do sleep 15; done; grep "AUTO PROMPT FORMAT" /workspace/server-auto.log
pkill -f "simple-jev --model"; sleep 10
```

The log line must name `$POLICY`. If it does not, stop and report to Ben.

- [ ] **Step 6: Send the inputs** (on the dev box, prelude first)

```bash
rm -rf /tmp/jev-up && mkdir -p /tmp/jev-up/scripts /tmp/jev-up/sf/runs /tmp/jev-up/nf/runs
cp $S/jev_rerank.py $S/teacher_rerank.py /tmp/jev-up/scripts/
cp $SF/beir/corpus.jsonl $SF/beir/queries.jsonl /tmp/jev-up/sf/ && cp $SF/runs/bge-base.chunks.trec /tmp/jev-up/sf/runs/
cp $NF/beir/corpus.jsonl $NF/beir/queries.jsonl /tmp/jev-up/nf/ && cp $NF/runs/bge-base.chunks.trec /tmp/jev-up/nf/runs/
tar czf /tmp/jev-inputs.tar.gz -C /tmp/jev-up . && md5sum /tmp/jev-inputs.tar.gz && runpodctl send /tmp/jev-inputs.tar.gz
```

On the pod: `mkdir -p /workspace/in /workspace/out && cd /workspace/in && runpodctl receive <code> && md5sum jev-inputs.tar.gz && tar xzf jev-inputs.tar.gz` (the md5 must match).

- [ ] **Step 7: Launch the server** with `MBS=32` first

```bash
MBS=32
cd /workspace && setsid nohup env ENABLE_OPEN_JEV_ADVANCED_METRICS=1 HF_HOME=/workspace/hf simple-jev \
  --model $REPO --revision $SHA --served-model-name $REPO --enforce-model-id \
  --classifier-prompt-policy $POLICY --max-request-branches 64 --max-model-len 65536 --dtype bfloat16 \
  --max-batch-size $MBS > /workspace/server-$MBS.log 2>&1 &
until curl -sf 127.0.0.1:8000/health; do sleep 15; done
```

- [ ] **Step 8: Pass 0, the blocking pre-flight** (query 129, the largest branch in either corpus). `QT` is `noul`, or `score` if arm 6 won.

```bash
I=/workspace/in; O=/workspace/out; QT=noul
awk '$1=="129"' $I/sf/runs/bge-base.chunks.trec > $I/q129.chunks.trec
python3 $I/scripts/jev_rerank.py --pool $I/q129.chunks.trec --corpus $I/sf/corpus.jsonl --queries $I/sf/queries.jsonl \
  --base-url http://127.0.0.1:8000 --model $REPO --question-type $QT \
  --responses $O/pre-$MBS.responses.jsonl --out $O/pre-$MBS.chunks.trec --composite 7d3a15092f963723
echo "exit $?"
python3 -c "import json,sys; [print(r['status'], r['reason']) for r in map(json.loads, open(sys.argv[1]))]" $O/pre-$MBS.responses.jsonl
```

- `exit 0`: pass. Take `PFT` from the accepted entry, then go on.
- `HTTP 500`, or `network error` with a CUDA out-of-memory error in `server-$MBS.log`: `pkill -f "simple-jev --model"`, halve `MBS` (32 → 16 → 8 …), repeat Step 7 and this step.
- `HTTP 422`: the tokenizer is denser than measured and the branch exceeds `--max-model-len`. Stop, terminate the pod (Step 15), and report to Ben.

```bash
PFT=$(python3 -c "import json,sys; print(next(r['prompt_tokens'] for r in map(json.loads, open(sys.argv[1])) if r['status']=='accepted'))" $O/pre-$MBS.responses.jsonl); echo $PFT
PROV="--server-commit 7bb4f0c745b2a160776b1d41ed4cdc02967f6cf3 --revision $SHA --policy $POLICY --max-batch-size $MBS --max-model-len 65536 --preflight-input-tokens $PFT"
```

- [ ] **Step 9: Pass 1, the transfer check** (the first 20 NFCorpus queries in pool order)

```bash
head -n 1000 $I/nf/runs/bge-base.chunks.trec > $I/nf-first20.chunks.trec
setsid nohup python3 $I/scripts/jev_rerank.py --pool $I/nf-first20.chunks.trec --corpus $I/nf/corpus.jsonl \
  --queries $I/nf/queries.jsonl --base-url http://127.0.0.1:8000 --model $REPO --question-type $QT \
  --responses $O/transfer.responses.jsonl --out $O/transfer.chunks.trec --composite 7d3a15092f963723 $PROV \
  > $O/transfer.log 2>&1 &
```

Wait for `[jev_rerank] wrote` in `$O/transfer.log`.

- [ ] **Step 10: Pass 2, the main pass** (all 300 SciFact queries)

```bash
setsid nohup python3 $I/scripts/jev_rerank.py --pool $I/sf/runs/bge-base.chunks.trec --corpus $I/sf/corpus.jsonl \
  --queries $I/sf/queries.jsonl --base-url http://127.0.0.1:8000 --model $REPO --question-type $QT \
  --responses $O/main.responses.jsonl --out $O/jev-main.chunks.trec --composite 7d3a15092f963723 $PROV \
  > $O/main.log 2>&1 &
```

Monitor with `wc -l $O/main.responses.jsonl; tail -n 2 $O/main.log`. If it exits 1 (over 15 fallbacks), no verdict is possible: diagnose from the ledger and `server-$MBS.log`, fix, and rerun the same command (it resumes).

- [ ] **Step 11: Pass 3, the repeat pass** (a separate ledger)

```bash
setsid nohup python3 $I/scripts/jev_rerank.py --pool $I/sf/runs/bge-base.chunks.trec --corpus $I/sf/corpus.jsonl \
  --queries $I/sf/queries.jsonl --base-url http://127.0.0.1:8000 --model $REPO --question-type $QT \
  --responses $O/repeat.responses.jsonl --out $O/jev-repeat.chunks.trec --composite 7d3a15092f963723 $PROV \
  --subsample 50 --subsample-seed 20260929 > $O/repeat.log 2>&1 &
```

- [ ] **Step 12: Cross-check the served revision and policy** in the main pass's responses

```bash
python3 - $O/main.responses.jsonl <<'PY'
import json, sys
seen = set()
for record in map(json.loads, open(sys.argv[1])):
    if record["status"] == "accepted":
        meta = json.loads(record["content"])["metadata"] or {}
        seen.add((meta.get("model_revision"), meta.get("prompt_policy")))
print(seen)
PY
```

Required: exactly `{('<$SHA>', '<$POLICY>')}`.

- [ ] **Step 13: Bring everything back**

```bash
cp /workspace/server-*.log $O/ && tar czf /workspace/jev-pod.tar.gz -C $O . && md5sum /workspace/jev-pod.tar.gz && runpodctl send /workspace/jev-pod.tar.gz
```

On the dev box: `mkdir -p $A/pod && cd $A/pod && runpodctl receive <code> && md5sum jev-pod.tar.gz && tar xzf jev-pod.tar.gz && rm jev-pod.tar.gz` (the md5 must match).

- [ ] **Step 14: Record the pod facts** in `$A/pod/pod.md`: card, template, start and end times, cost, `$REPO`, `$SHA`, `$POLICY`, the final `MBS`, `PFT`, and each pass's wall time.

- [ ] **Step 15: Terminate the pod** in the RunPod console, and confirm it is gone.

No commit (nothing in the repo changes).

### Task 5: Verdict, gate doc and index row

**Files:**
- Create: `docs/plans/2026-09-GATE-simple-jev-judge.md`
- Modify: `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` (append after `:50`; rewrite `:52-59`)

**Interfaces:**
- Consumes: Task 4's `$A/pod/` artifacts and Task 3's winner ledger.

Every shell starts with the Task 1 prelude plus:

```bash
BASE=$SF/runs/bge-base.chunks.trec; QRELS=$SF/qrels.trec; MAIN=$A/pod/jev-main.chunks.trec
mkdir -p $A/gate
```

- [ ] **Step 1: The gate**

```bash
PYTHONPATH=$B/python-libs python3 $S/report.py --run $MAIN --run $BASE --qrels $QRELS --pair $MAIN=$BASE > $A/gate/report-gate.txt 2>&1
echo "exit $?"; cat $A/gate/report-gate.txt
python3 -c "import json; r=json.load(open('$A/pod/jev-main.meta.json'))['reranker']; print(r['fallbackCount'], r['fallbackQueryIds'])"
```

Preconditions: `exit 0`; the `jev-main` run's `R@50` reads exactly `0.9337`; `fallbackCount` ≤ 15. The verdict comes from the `[compare] jev-main.chunks.trec vs bge-base.chunks.trec (nDCG@10)` block: **PASS** iff `permutation p` < 0.05 **and** `delta` ≥ +0.0470; otherwise **FAIL**.

- [ ] **Step 2: The worst-case bound** (every fallback query set to reversed baseline order)

```bash
S=$S MAIN=$MAIN BASE=$BASE BOUND=$A/gate/bound.chunks.trec python3 - <<'PY'
import json, os, shutil, sys
sys.path.insert(0, os.environ["S"])
import teacher_rerank as tr, jev_rerank as jev
main, base, bound = os.environ["MAIN"], os.environ["BASE"], os.environ["BOUND"]
fallbacks = json.load(open(tr.sidecar_path_for(main), encoding="utf-8"))["reranker"]["fallbackQueryIds"]
run, pool = tr.load_run(main), tr.load_run(base)
for qid in fallbacks:
    run[qid] = list(reversed(pool[qid]))
jev.write_run(bound, run, list(run))
shutil.copy(tr.sidecar_path_for(main), tr.sidecar_path_for(bound))
print(f"{len(fallbacks)} fallback queries set to reversed baseline order -> {bound}")
PY
PYTHONPATH=$B/python-libs python3 $S/report.py --run $A/gate/bound.chunks.trec --run $BASE --qrels $QRELS \
  --pair $A/gate/bound.chunks.trec=$BASE > $A/gate/report-bound.txt 2>&1; echo "exit $?"
```

With zero fallbacks the bound equals the main result; say so in the gate doc.

- [ ] **Step 3: Secondary metrics** (reported, not gated)

```bash
QRELS=$QRELS MAIN=$MAIN BASE=$BASE PYTHONPATH=$B/python-libs python3 - <<'PY' | tee $A/gate/secondary.txt
import os
import ir_measures
from ir_measures import AP, P, R, nDCG
qrels = list(ir_measures.read_trec_qrels(os.environ["QRELS"]))
for path in (os.environ["MAIN"], os.environ["BASE"]):
    agg = ir_measures.calc_aggregate([nDCG @ 10, R @ 50, AP, R @ 10, P @ 10], qrels, ir_measures.read_trec_run(path))
    print(os.path.basename(path), {str(m): round(v, 4) for m, v in agg.items()})
PY
```

The `bge-base.chunks.trec` line must equal spec §3's identity row (0.7452 / 0.9337 / 0.7018 / 0.877 / 0.0993).

- [ ] **Step 4: The repeat pass** (noise floor), scored against qrels restricted to its 50 ids and compared with `--baseline`. `--pair` would refuse a repeat that reorders fewer than 25% of queries, as the teacher-ceiling round 2 found.

```bash
S=$S BASE=$BASE QRELS=$QRELS SUB=$A/gate/qrels-sub50.trec REPEAT=$A/pod/jev-repeat.chunks.trec python3 - <<'PY'
import os, sys
sys.path.insert(0, os.environ["S"])
import teacher_rerank as tr
ids = set(tr.select_subsample(list(tr.load_run(os.environ["BASE"])), 50, 20260929))
assert ids == set(tr.load_run(os.environ["REPEAT"])), "repeat run's query ids differ from the subsample"
with open(os.environ["QRELS"], encoding="utf-8") as src:
    rows = [line for line in src if line.split()[0] in ids]
with open(os.environ["SUB"], "w", encoding="utf-8") as dst:
    dst.writelines(rows)
print(f"{len(ids)} query ids, {len(rows)} qrels rows -> {os.environ['SUB']}")
PY
PYTHONPATH=$B/python-libs python3 $S/report.py --run $A/pod/jev-repeat.chunks.trec --baseline $MAIN \
  --qrels $A/gate/qrels-sub50.trec > $A/gate/report-repeat.txt 2>&1; echo "exit $?"
```

Expected: `50 query ids, 55 qrels rows` (verified), then the repeat-vs-main `delta` and `queries changed` in `report-repeat.txt`.

- [ ] **Step 5: The transfer check** (reported, not gated). `DEMO_LEDGER` is the winning arm's ledger from Task 3.

```bash
S=$S DEMO_LEDGER=$A/screen/<winning arm>.responses.jsonl POD_LEDGER=$A/pod/transfer.responses.jsonl \
PYTHONPATH=$B/python-libs python3 - <<'PY' | tee $A/gate/transfer.txt
import json, os, sys
sys.path.insert(0, os.environ["S"])
import teacher_rerank as tr
from scipy.stats import spearmanr

def values(path):
    out = {}
    for qid, records in tr.read_responses_ledger(path).items():
        record = tr.accepted_entry(records)
        if record is not None:
            out[qid] = json.loads(record["content"])["values"]
    return out

demo, pod = values(os.environ["DEMO_LEDGER"]), values(os.environ["POD_LEDGER"])
common = [qid for qid in pod if qid in demo]
rhos, diffs = [], []
for qid in common:
    rho = spearmanr(demo[qid], pod[qid]).statistic
    diff = sum(abs(a - b) for a, b in zip(demo[qid], pod[qid])) / len(pod[qid])
    rhos.append(rho)
    diffs.append(diff)
    print(f"{qid:>12}  rho {rho:+.3f}  mean|diff| {diff:.4f}")
print(f"{len(common)} queries in both ledgers  mean rho {sum(rhos) / len(rhos):+.3f}  "
      f"mean|diff| {sum(diffs) / len(diffs):.4f}")
PY
```

- [ ] **Step 6: Pin md5s**

```bash
md5sum $A/pod/*.chunks.trec $A/pod/*.meta.json $A/gate/*.chunks.trec $A/gate/*.txt $A/screen/*.chunks.trec $A/dryrun/*.chunks.trec | tee $A/gate/md5s.txt
```

- [ ] **Step 7: Write** `docs/plans/2026-09-GATE-simple-jev-judge.md` from the files above, in this order, with every number copied from its output file:
1. Title and verdict line: **PASS** or **FAIL**, the nDCG@10 delta, the permutation p, and the gate (p < 0.05 and delta ≥ +0.047). Link the spec (`18ba5177`) and this plan.
2. Inputs: spec §3's table with md5s, plus the pool note: the shipped embedder at the **512/448** window, kept by Ben's 2026-09-30 ruling (`sci-2048` at the shipped window scores 0.7476 / 0.9393 unreranked).
3. Stage A screen: `$A/screen/screen.md`'s table and the winner.
4. Stage B pod: `$A/pod/pod.md`'s facts (card, cost, `$REPO`, `$SHA`, `$POLICY`, final `--max-batch-size`, pre-flight input tokens, wall times) and Step 12's cross-check.
5. Gate: the `[pool]` line, both runs' `[scores]` blocks and the nDCG@10 `[compare]` block from `report-gate.txt`, verbatim, and the fallback count.
6. Worst-case bound: `report-bound.txt`'s nDCG@10 `[compare]` block, and whether the gate still holds.
7. Reported, not gated: `secondary.txt`, the repeat delta and queries-changed from `report-repeat.txt`, and `transfer.txt`'s summary line.
8. Caveats: spec §11's three known issues; the judge is nondeterministic (the repeat delta sizes the noise floor).
9. Artifacts: `$A/gate/md5s.txt` verbatim, plus the command lines used (Steps 1–5 of this task, and Task 4 Steps 7–11 with the real values).

- [ ] **Step 8: Update the ranked-changes index.** Append one row after line 50 (the relation-popularity row), in the table's four-column form, filling the three bracketed values from Step 1 and Task 3:

```markdown
| SimpleJEV relevance judge (pointwise noul, [winner], bge-base SciFact pool at 512/448) | **[PASS or FAIL]** — nDCG@10 [delta], permutation p [p]; gate p < 0.05 and delta ≥ +0.047 | harness only (`jev_rerank.py`, `stub_jev_server.py`); no server change | `2026-09-GATE-simple-jev-judge.md` |
```

Then, in the paragraph under the table, replace this text (it spans a line break at "the"):

```text
The seven rows added since were measured at the
shipped window instead:
```

with:

```text
The eight rows added since were measured at the shipped window instead, except the
SimpleJEV judge gate, which reused the 512/448 bge-base SciFact pool:
```

Check the order with `grep -n -B1 -A1 "SimpleJEV relevance judge" docs/2026-09-06-ranked-changes-after-retrieval-experiments.md`: it must print the relation-popularity row as `50-| Relation-popularity signal …`, the new row as `51:| SimpleJEV relevance judge …`, and a blank `52-` line.

- [ ] **Step 9: Commit**

```bash
git -C $R add -f docs/plans/2026-09-GATE-simple-jev-judge.md
git -C $R add docs/2026-09-06-ranked-changes-after-retrieval-experiments.md
git -C $R commit -m "record the SimpleJEV relevance-judge gate verdict"
```

## Tasks NOT in this plan

- **Out of scope for this spec:** server code, RFDT, first-stage recall, listwise `choice` ranking, and blending
  the judge score with the fused score.

## Known issues inherited from spec

- **The screen model may not be the best SciFact model.** NFCorpus is a different task: topical queries,
  graded, about 38 relevant docs per query. Ben accepted this (2026-09-29) as the price of a leak-free
  selection.
- **The demo's serving stack differs from `hf_server`.** The screen ranks models through a third party's
  unpinned configuration. The §6.3 transfer check reports how far that carried; it does not gate.
- **SimpleJEV's README policy table is stale** at `7bb4f0c7`. It is handled by pinning from code (§6.2), and
  the discrepancy was not reported upstream.
