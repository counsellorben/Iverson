# Teacher Ceiling Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-20-teacher-ceiling-design.md` (commit SHA: `30d384a`)

**Goal:** Measure whether a self-hostable open reasoning model, reordering the 50 candidates Iverson already retrieves for each SciFact query, reaches nDCG@10 ≥ 0.753 — the threshold that decides whether per-tenant distilled rerankers get built at all.

**Architecture:** An offline script rescores a preserved TREC run. It reads `rerank-a0prime.chunks.trec`, builds one listwise prompt per query from `corpus.jsonl`, calls a vLLM-served model over its OpenAI-compatible route, validates each reply is a permutation of that query's 50 doc ids, and writes a new TREC run plus a build-identity sidecar. `report.py` scores it. No Iverson server, no Qdrant, no compose, no Authentik.

**Tech stack:** Python 3.14 stdlib only (`urllib.request`, `json`, `random`, `argparse`, `http.server`, `threading`, `hashlib`) plus `pytest` for the suite. vLLM serves the model on a rented 80 GB GPU. Scoring is the repo's existing `report.py` with `PYTHONPATH` pointed at the corpora `python-libs`.

---

## Global Constraints

Copied from the spec; every task holds to these.

- **No new dependency.** stdlib + pytest only — no `openai`, no `requests` (spec §4, A11).
- **Fail loud, never degrade.** No run file is written if any query is unscored; nothing is ever filled from fusion order (spec §6).
- **`~/repositories/iverson-benchmark-corpora/` is not a git repository.** Artifacts are preserved on disk there; never `git add` in that tree (P12 below).
- **`docs/plans/` and `docs/specs/` are gitignored** — commit with `git add -f` (spec A22).
- **Nothing in this plan changes server code.**

## File Structure

**Create:**
- `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py` — the rescorer: load run, build prompts, call the model, validate, resume, write run + sidecar.
- `Iverson.Server/Iverson.LoadTest/scripts/stub_vllm_server.py` — a stdlib fake of vLLM's `/v1/chat/completions` that answers with each query's ids in input-run order. Used only by Task 2.
- `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py` — pytest suite for everything except the live model call.
- `docs/plans/2026-09-GATE-teacher-ceiling.md` — the verdict (Task 4).

**Create outside the repo (not version controlled):**
- `~/repositories/iverson-benchmark-corpora/teacher-ceiling-2026-09/` — raw responses (one JSONL per pass), run files, sidecar, `report.py` output, run log.

**Modify:** none. This plan is create-only.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and by four CDR rounds; **not re-verified here**. The full list is `docs/specs/2026-09-20-teacher-ceiling-design.md` §10 (A1–A34). The rows this plan leans on directly:

- **A1–A6** — the A0′ pool is 300 queries × 50 rows scoring 0.6980 / 0.9193; all 4,083 doc ids resolve in `corpus.jsonl`; all 300 query ids resolve in `queries.jsonl`; qrels covers 300/300; ids are pure digits.
- **A7** — `report.py --pair` exits 1 on a pool violation, on 10% churn and on 0% churn; exit 0 on a valid rescore.
- **A8** — `report.py` needs `PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs`.
- **A9, A26** — the sidecar carries A0′'s composite `31583db5aea49136`; `report.py` finds it by **run filename**, so `--out` must be `<label>.chunks.trec`.
- **A11** — stdlib `urllib.request` is the in-repo HTTP pattern; `enrich_bench.py`'s `MAX_TOKENS = 256` is *not* inherited.
- **A27, A29** — delta equals `teacher − 0.6980` at full coverage; a partial-coverage run is scored against the qrels it is given, so the repeat needs a restricted qrels.
- **A31** — `report.py` counts duplicate `(query_id, doc_id)`, not `(qid, score)`; §8 check 5 needs its own computation.
- **A34** — resume keys on a success ledger, not on presence in the data log.

## Verified plan-level assumptions

Newly introduced by this plan and verified 2026-09-20 at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `scripts/teacher_rerank.py` does not exist yet | `ls` → `No such file or directory` |
| P2 | File path | `scripts/test_teacher_rerank.py` does not exist yet | same |
| P3 | File path | `scripts/stub_vllm_server.py` does not exist yet | same |
| P4 | File path | `~/…/teacher-ceiling-2026-09/` does not exist yet | `ls -d` → `No such file or directory` |
| P5 | File path | `docs/plans/2026-09-GATE-teacher-ceiling.md` does not exist yet | `ls` → `No such file or directory` |
| P6 | Code validity | `urllib.request`, `json`, `random`, `hashlib`, `argparse`, `http.server`, `threading`, `collections` all import | ran the import line; `python 3.14.4` |
| P7 | Code validity | **The per-query shuffle must seed from a string, not `hash()`** — `hash(str)` is randomized per process | two processes: `hash("852")` → **105**, then **823**. `random.Random('20260920:852')` → identical shuffle both times |
| P8 | Code validity | A stdlib `ThreadingHTTPServer` can stand in for vLLM's `/v1/chat/completions`, and the mandated `urllib` POST round-trips against it | ran an end-to-end probe: POST returned `finish_reason = stop` and the parsed permutation `['4346436','7583104','40212412']` |
| P9 | Command | `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/<test>.py -q` is valid from the repo root | ran it on the sibling suite → `32 passed in 0.28s` |
| P10 | Command | `report.py` flag spellings are `--run`, `--qrels`, `--baseline`, `--pair RUN=BASELINE` | `report.py --help` usage line |
| P11 | Command | `md5sum` is available for Task 4 | `/usr/bin/md5sum` |
| P12 | Consumer | `~/repositories/iverson-benchmark-corpora/` is **not** a git repo — artifact steps must not `git add` there | `git -C … rev-parse` → `fatal: not a git repository` |
| P13 | Consumer | Nothing globs `scripts/test_*.py` in a build or CI that the new suite would silently join | `command grep` over `*.yml`/`*.yaml`/`*.csproj`/`*.sh`/`*.slnx` → no hits |
| P14 | Ordering | Task 2 depends only on Task 1; Task 3 on Tasks 1–2; Task 4 on Task 3's artifacts. Tasks 3's provisioning, main run and repeat share one rented instance and are therefore one task | by construction — see the task headers |
| P15 | Code validity | `corpus.jsonl` records carry exactly `_id`/`title`/`text`; `queries.jsonl` carries `_id`/`text`. "Abstract" is the `text` field | all 5,183 corpus records have key set `('_id','text','title')` and all 300 query records `('_id','text')`; **0** records with an empty title or text |
| P16 | Signature | `popularity_rerank.py`'s pattern exists as cited | `load_run` at `:83`, `write_run` at `:226`, `--run` at `:251`, `--out` at `:255`; `write_run` emits `f"{query_id} Q0 {doc_id} {rank} {score:.6f} {tag}\n"` |
| P17 | File shape | `test_popularity_rerank.py`'s shape is as Task 1 Step 1 prescribes | docstring names the run command at `:1-5`; `sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))` at `:25`; `:19` states "No non-stdlib imports beyond pytest" |
| P18 | File path | The gate-doc precedent Task 4 cites exists | `ls docs/plans/2026-09-GATE-reranker-phase1.md` resolves |
| P19 | Consumer | The new script paths are **not** gitignored, so Tasks 1–2 commit with a plain `git add` | `git check-ignore -v` on the new script path returns nothing; sibling `popularity_rerank.py` is tracked |
| P20 | Code validity | Task 4's report output is re-derivable after the instance is destroyed | `report.py:113-114` — `PERMUTATION_SEED = 20260831`, `PERMUTATION_RESAMPLES = 10_000` are fixed constants, so re-running against the preserved run file reproduces the same p |
| P21 | Code validity | The repeat's subsample selection is reproducible across processes | `random.Random('repeat-2026-09').sample(sorted(qids), 50)` returned the identical 50 ids in two separate interpreters, over the real 300-query id set. Sorting before sampling is load-bearing |
| P22 | Code validity | Reply ids may decode as JSON **numbers**, and `str()`-normalisation is total over this id population | `set(json.loads('[7583104,…]')) == set(pool)` → **False**; with `str()` → **True**; an invented id still fails. All 4,083 ids are pure digits with 0 leading zeros (spec A28), so `str(int(x)) == x` for every one |
| P23 | Command | `PYTHONPATH` and `B` do not survive between tasks | `env -u PYTHONPATH … report.py` → `could not import ir_measures … It must be reached via PYTHONPATH`; with `B` unset, `$B/qrels.trec` expands to `/qrels.trec`, which `report.py` rejects as not found |

## Tasks

### Task 1: `teacher_rerank.py` and its suite

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py`

**Interfaces:**
- Produces: the CLI Task 2 and Task 3 invoke; the run-file + sidecar format `report.py` consumes.

- [ ] **Step 1: Write the pytest suite first.** Match `test_popularity_rerank.py`'s shape — module docstring naming the run command, `sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))`, stdlib + pytest only, hand-computable fixtures. Cover the six properties spec §5 names, plus the two the round-1 review added:
  - permutation validation rejects a reply with a missing id, a duplicated id, an invented id, or the wrong length, and accepts an exact permutation;
  - JSON parsing rejects a non-array and a non-JSON body;
  - the refusal-to-write rule: any query unscored ⇒ no run file written, non-zero exit, failures listed;
  - seeded-shuffle reproducibility: the same `--shuffle-seed` and query id give the same presentation order **in a separate process** (P7 — assert against a literal expected order, not against a second in-process call, or the test cannot catch a `hash()`-based seed);
  - run-file formatting: 6 whitespace-delimited columns, `score = 51 − position`, so ranks 1..50 map to 50..1 and no score is `0.000000`;
  - resume: a query with an **accepted** entry is skipped; a query whose entries are **all rejected** is re-issued with a fresh retry budget; where both exist the accepted entry wins (spec §6 row 4, A34);
  - **a reply whose ids are unquoted JSON numbers is accepted** as a valid permutation (P22) — this is the branch Task 2's stub cannot reach, since it emits ids as strings;
  - `--subsample N --subsample-seed S` selects the same N query ids on every invocation, asserted against a literal expected selection (P21).

- [ ] **Step 2: Run the suite; confirm it fails.**
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q
```

- [ ] **Step 3: Implement `teacher_rerank.py`.** Pattern it on `popularity_rerank.py` (`load_run`, `write_run`, argparse with `--run`/`--out`). CLI per spec §5:
```
teacher_rerank.py --run <a0prime.chunks.trec> --corpus <corpus.jsonl> --queries <queries.jsonl>
                  --base-url <vllm> --model <id> --seed N --shuffle-seed N
                  --responses <raw.jsonl> --out <teacher.chunks.trec>
                  [--subsample N --subsample-seed S]
```
  Behaviour, all from spec §§3–6:
  - one call per query; prompt carries the query text and all 50 documents (title + abstract), each labelled by doc id;
  - documents presented in a **seeded shuffle**, seeded per query as `random.Random(f"{shuffle_seed}:{qid}")` (P7);
  - POST to `{base_url}/v1/chat/completions` with stdlib `urllib.request`, `temperature: 0`, `stream: false`, `max_tokens: 8192`, and vLLM guided decoding to a JSON-array schema — the executing session confirms the structured-output parameter name against the installed vLLM version and records it (spec §4, A14);
  - a reply with `finish_reason: "length"` is a §6 row-1 failure and is never parsed;
  - each raw response appended to `--responses` **tagged accepted or rejected** by the permutation check; resume skips a query with an accepted entry, re-issues one whose entries are all rejected;
  - **reply ids are normalised with `str()` per element before the permutation comparison**, and the `--responses` ledger's query key is written and read as a string — spec A28 anticipates ids decoding as JSON numbers, and an unnormalised `set(int) == set(str)` is `False` for every query, which would reject every reply and produce no run file (P22). Pin the guided-decoding schema's item type to string (`{"type":"array","items":{"type":"string"}}`) as well;
  - `--subsample N --subsample-seed S` (optional, used only by Task 3 Step 5) selects N query ids with `random.Random(f"{subsample_seed}").sample(sorted(run_query_ids), N)` — **sorted before sampling**, which is what makes it reproducible (P21) — and records both values in the sidecar;
  - write the run file only if every query scored; write `<label>.meta.json` beside it carrying `"composite": "31583db5aea49136"` and the `reranker` block from spec §5.

- [ ] **Step 4: Run the suite; confirm green.** Same command as Step 2.

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py \
        Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
git commit -m "add teacher_rerank.py offline listwise rescorer and its suite"
```

### Task 2: Dry run against a stub — the free gate before any spend

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/stub_vllm_server.py`

**Interfaces:**
- Consumes: Task 1's CLI and run-file writer.
- Produces: the pass/fail signal that authorises Task 3's spend.

- [ ] **Step 1: Write `stub_vllm_server.py`.** A `ThreadingHTTPServer` (P8) that serves `POST /v1/chat/completions`, reads the A0′ run file once at startup, and answers each request with that query's doc ids **in input-run order** — i.e. it ignores the shuffled presentation and reconstructs fusion order (spec §11 step 2). It returns an OpenAI-shaped body: `{"choices": [{"message": {"content": "<json array>"}, "finish_reason": "stop"}]}`. It binds `127.0.0.1` on an ephemeral port and prints the port.

- [ ] **Step 2: Run the dry run against it.** Point `teacher_rerank.py --base-url` at the stub. Write its run file and responses to any scratch path outside the repo — the dry run is not part of §9's preserved artifact set, so it does not go in `teacher-ceiling-2026-09/`.

- [ ] **Step 3: Score it — plain `--run`/`--qrels`, never `--pair`.** The artifact is order-identical to A0′, and `check_pool`'s ≥25%-reordered floor correctly rejects an unchanged ordering (spec §11 step 2, A7).
```bash
export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs
B=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
python3 Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --run <dry-run>.chunks.trec --qrels $B/qrels.trec
```
  **Acceptance: exactly `nDCG@10 0.6980` and `R@50 0.9193`.** Anything else stops the plan here — the pipeline is wrong and no GPU is rented.

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/stub_vllm_server.py
git commit -m "add stub vLLM server for the teacher-ceiling dry run"
```

### Task 3: The rented-instance session

Everything that costs money happens here, in one session on one instance. Provisioning, the main run and the repeat are **one task** because they share an ephemeral rented box — splitting them across fresh subagents would strand it (P14).

**Files:**
- Create (outside the repo): `~/repositories/iverson-benchmark-corpora/teacher-ceiling-2026-09/` and its contents.

**Interfaces:**
- Consumes: Task 1's script, Task 2's passing gate.
- Produces: the run file, sidecar, three response JSONLs, `report.py` output and run log that Task 4 reads.

- [ ] **Step 1: Provision and serve.** One 80 GB GPU (RunPod Community A100 ≈ $1.39/hr or H100 ≈ $1.99/hr — confirm at rental, spec A17). Serve gpt-oss-120b with vLLM, `max_model_len` 128K.

- [ ] **Step 2: Record serving identity** before any run: vLLM version, the structured-output parameter name as installed, model id and revision, quantisation. These go in the sidecar's `reranker` block and the run log.

- [ ] **Step 3: Run all 300 queries**, writing `--responses` to its own JSONL under the artifacts directory.

- [ ] **Step 4: Score the main run and verify all five structural checks** (spec §8). Checks 1–2 are enforced by `--pair`, which exits 1 on violation; checks 3–5 are read off the output, and **check 5 needs its own computation** because `report.py` counts duplicate `(query_id, doc_id)`, not `(qid, score)` (A31).
```bash
export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs
B=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
python3 Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --run <teacher>.chunks.trec --qrels $B/qrels.trec \
  --pair <teacher>.chunks.trec=$B/runs/rerank-a0prime.chunks.trec
```
  Pool invariance: doc set changed on 0 of 300. ≥25% reordered. R@50 identical to 0.9193 at four decimals. No result above the oracle 0.9196 — above it means label leakage. Zero duplicate `(qid, score)` pairs.

- [ ] **Step 5: Run the 50-query repeat.** Both passes select the subsample with `--subsample 50 --subsample-seed <S>`, the same S for each, **each pass writing its own `--responses` file** and neither reading the main run's (spec §8, A33) — otherwise resume replays pass 1 and the noise floor reads as exactly zero. Build a qrels restricted to the subsample's own 50 query ids, then compare with `--baseline`, **never `--pair`** (A29: against the full 300-query qrels the delta is averaged over 300 denominators and reported at one-sixth its true size, with the `query sets differ` warning suppressed).
```bash
# the same PYTHONPATH line as Step 4, if this runs in a separate shell ($B is not used here)
export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs
python3 Iverson.Server/Iverson.LoadTest/scripts/report.py \
  --run <pass2>.chunks.trec --qrels <qrels-sub50>.trec --baseline <pass1>.chunks.trec
```
  Report how many orderings differ and the nDCG@10 delta between passes. Reported, not gating.

- [ ] **Step 6: Preserve every artifact on disk and destroy the instance.** Raw responses (one JSONL per pass: main, repeat 1, repeat 2), run files, sidecar, full `report.py` output, run log. **No `git add` in the corpora tree — it is not a git repository** (P12).

### Task 4: The verdict

**Files:**
- Create: `docs/plans/2026-09-GATE-teacher-ceiling.md`

**Interfaces:**
- Consumes: Task 3's preserved artifacts.

- [ ] **Step 1: Pin the md5s.**
```bash
md5sum <teacher>.chunks.trec <report-output>.txt
```
  Both go in the verdict doc — the convention that makes a surprising result provably measured.

- [ ] **Step 2: Write the verdict** against spec §8, in the gate-doc form of `docs/plans/2026-09-GATE-reranker-phase1.md`: method, serving identity, the five structural checks with their results, the repeat's noise floor, and the gate verdict. **PASS requires both** permutation p < 0.05 **and** delta ≥ +0.055 (nDCG@10 ≥ 0.753). A PASS triggers the stage 2 spec. **A FAIL closes the per-tenant reranking line, and the verdict says so in those words.**

- [ ] **Step 3: Commit** — `docs/plans/` is gitignored, so force-add (A22).
```bash
git add -f docs/plans/2026-09-GATE-teacher-ceiling.md
git commit -m "record teacher-ceiling gate verdict"
```

## Tasks NOT in this plan

Inherited from spec §12. A new spec → plan cycle is required to add any of these.

Stage 2 (query generation, LoRA distillation, per-tenant adapters), stage 3 (NFCorpus repeat and adapter cross-application), `SearchSimilar`, multi-LoRA serving, deletion/retraining policy for per-tenant models, and every server-side change. None of it is designed here, and none of it proceeds unless §8's gate passes.
