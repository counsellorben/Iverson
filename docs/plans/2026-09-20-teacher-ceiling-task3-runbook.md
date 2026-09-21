# Teacher ceiling — Task 3/4 runbook (the paid session)

Operational companion to `docs/plans/2026-09-20-teacher-ceiling-implementation-plan.md` Tasks 3–4.
Tasks 1–2 are already built and committed on branch `teacher-ceiling`.

**The meter runs from step 2 to step 9.** Everything before step 2 is free and must pass first.
Budget: $2–5 per pass, under $20 including one failed pass and the repeat.

## Which machine runs what

Two machines. Getting this wrong is the most likely way to waste rented time.

| | **Dev box** | **Rented pod** |
|---|---|---|
| Runs | `report.py`, `stub_vllm_server.py`, `pytest` | vLLM, `teacher_rerank.py` |
| Has | the corpora, `python-libs`, the git repo | the GPU and the model weights |
| Steps | 1, 7, 8-scoring, 9-archive, 10 | 3, 4, 5, 6, 8-runs |

**The rule:** every `teacher_rerank.py` invocation runs **on the pod**; every `report.py` invocation
runs **on the dev box**. Artifacts move pod → dev by `scp`. `report.py` needs `python-libs`, which
is not on the pod; the GPU is not on the dev box.

`teacher_rerank.py` is stdlib-only, so the pod needs no install — just the script and three input
files copied up.

**Dev box, every shell:**
```bash
export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs   # report.py needs this
B=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
A=~/repositories/iverson-benchmark-corpora/teacher-ceiling-2026-09       # artifacts; mkdir -p it
S=Iverson.Server/Iverson.LoadTest/scripts                                # repo-relative
```

**Pod, after you SSH in** (step 3 copies these up):
```bash
B=/workspace/inputs          # rerank-a0prime.chunks.trec, corpus.jsonl, queries.jsonl
A=/workspace/artifacts       # mkdir -p; scp these DOWN before terminating
S=/workspace                 # teacher_rerank.py lives here
```
Do **not** export `PYTHONPATH` on the pod — nothing there reads it, and `report.py` is not on the pod.

Below, each step is tagged **[dev]**, **[pod]** or **[dashboard]**. Where a step's commands use `$B`,
`$A` or `$S`, use the block for that step's machine.

`~/repositories/iverson-benchmark-corpora/` is **not a git repository**. Never `git add` anything
there. The only thing that gets committed is the verdict doc in step 10.

---

## 1. [dev] Pre-flight — free, and it gates the spend

Run all three. If any disagrees, stop: the pipeline is wrong and renting a GPU would measure nothing.

```bash
python3 -m pytest $S/test_teacher_rerank.py -q                    # expect: 35 passed
```

**Both halves of the gate.** Start the stub, note the port it prints, run against it, score it.

```bash
python3 $S/stub_vllm_server.py --run $B/runs/rerank-a0prime.chunks.trec \
        --queries $B/beir/queries.jsonl                            # identity mode; prints its port
python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
        --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
        --base-url http://127.0.0.1:<port> --model stub --seed 1 --shuffle-seed 1 \
        --responses /tmp/pre-id.jsonl --out /tmp/pre-id.chunks.trec
python3 $S/report.py --run /tmp/pre-id.chunks.trec --qrels $B/qrels.trec
```
Expect **exactly** `nDCG@10 0.6980`, `R@50 0.9193`, and the line `build 31583db5aea49136`.

Repeat with `--order reversed` on the stub and a **fresh** `--responses` path:
```bash
python3 $S/stub_vllm_server.py --run $B/runs/rerank-a0prime.chunks.trec \
        --queries $B/beir/queries.jsonl --order reversed
# ... same teacher_rerank.py call, but --responses /tmp/pre-rev.jsonl --out /tmp/pre-rev.chunks.trec
```
Expect **exactly** `nDCG@10 0.0032`, `R@50 0.9193`.

Why both: a pipeline that discards the model's reply and re-emits the candidate order scores
0.6980 in *both* modes. The reversed pass is the only thing that catches it. Do not proceed on one.

**Check the `build` line, not just the two numbers.** A wrong sidecar composite still scores
0.6980/0.9193 but prints `BUILD MISMATCH` on every Task 3 comparison. Cheap to catch here, annoying
to catch later.

---

## 2. [dashboard] Provision

One 80 GB GPU, per-second billing. RunPod Community A100 80 GB ≈ $1.39/hr or H100 ≈ $1.99/hr
(third-party listings from August 2026 — confirm the rate at rental). Vast.ai H100 from ~$1.49/hr.

Avoid the hyperscalers for this: Azure's cheapest 80 GB equivalent is ≈ $3.67/hr, and AWS has no
single-GPU H100 SKU at all.

---

## 3. [pod] Serve the model, and copy the inputs up

gpt-oss-120b (Apache 2.0, MXFP4, 128K context) under vLLM, `--max-model-len 131072`.

The worst-case prompt in this corpus is 137,152 characters ≈ 34K tokens, so 128K leaves ample room —
but the budget is not the thing to trim if you hit trouble.

**Copy the inputs up first** (from the dev box, ~6 MB total; the pod needs nothing installed):

```bash
ssh -p <port> root@<host> 'mkdir -p /workspace/inputs/runs /workspace/inputs/beir /workspace/artifacts'
scp -P <port> Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py root@<host>:/workspace/
scp -P <port> $B/runs/rerank-a0prime.chunks.trec  root@<host>:/workspace/inputs/runs/
scp -P <port> $B/beir/corpus.jsonl $B/beir/queries.jsonl root@<host>:/workspace/inputs/beir/
```

The `runs/` and `beir/` subdirectories are deliberate: they mirror the dev box's layout, so every
`$B/runs/...` and `$B/beir/...` path in steps 5, 6 and 8 is copy-pasteable verbatim on the pod once
you have exported the pod's `$B`. Do not flatten them.

**Keep the `--run` path identical across every pass on the pod.** The ledger stamps that path into
each record and refuses a resume whose path differs — that is the guard that stops a repeat pass
silently replaying the main run, and it means you cannot resume a pod-started pass on the dev box.

---

## 4. [pod] Resolve the two execution-time checks — before the 300-query run

Both are inherited unknowns (spec A13, A14). Answer them with one cheap call, not on query 1 of 300.

- **Structured-output parameter name.** vLLM is mid-rename: `guided_json` → `structured_outputs`.
  `teacher_rerank.py` sends `STRUCTURED_OUTPUT_PARAM = "guided_json"`. Confirm against the installed
  version; if it has moved, change that constant.
- **`seed` support** on the OpenAI-compatible route. If the server rejects it, drop it and rely on
  `temperature: 0` — the repeat check in step 8 is what measures the residual non-determinism, so a
  missing `seed` degrades the noise floor's tightness, not the gate.

Record the vLLM version and the parameter name you settled on. They go into step 5's flags and the
run log.

---

## 5. [pod] Smoke-test one query against the real model

```bash
python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
  --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
  --base-url http://127.0.0.1:8000 --model <model-id> --seed 7 --shuffle-seed 7 \
  --subsample 1 --subsample-seed smoke \
  --vllm-version <ver> --quantisation mxfp4 --instance-type <sku> \
  --responses $A/smoke.responses.jsonl --out $A/smoke.chunks.trec
```

This costs pennies and exercises the one path the free gate structurally cannot: the real model's
reply shape. Read `$A/smoke.responses.jsonl` and confirm the reply is a 50-element JSON array and
`finish_reason` is `stop`, not `length`.

---

## 6. [pod] The main run — 300 queries

```bash
python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
  --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
  --base-url http://127.0.0.1:8000 --model <model-id> --seed 20260920 --shuffle-seed 20260920 \
  --vllm-version <ver> --quantisation mxfp4 --instance-type <sku> \
  --responses $A/main.responses.jsonl --out $A/teacher-ceiling.chunks.trec
```

`--out` **must** end `.chunks.trec` or the sidecar is not found (`report.py` derives it by stripping
`.trec` then `.chunks`).

If the instance dies mid-run, re-issue the **identical** command. Resume is enforced, not advisory:
the ledger records the run path and both seeds, and refuses loudly if you change any of them.

---

## 7. [dev] The five structural checks — scp the run file down first

Bring the run file **and its sidecar** down — `report.py` finds the sidecar by filename, so a run
file without its `.meta.json` scores fine but prints `BUILD UNKNOWN`:

```bash
mkdir -p $A
scp -P <port> root@<host>:/workspace/artifacts/teacher-ceiling.chunks.trec \
              root@<host>:/workspace/artifacts/teacher-ceiling.meta.json  $A/
```

Then score on the dev box (`$B`, `$A`, `$S` here are the **dev box** block):

```bash
python3 $S/report.py --run $A/teacher-ceiling.chunks.trec --qrels $B/qrels.trec \
  --pair $A/teacher-ceiling.chunks.trec=$B/runs/rerank-a0prime.chunks.trec \
  | tee $A/report-output.txt
```

`tee` matters: step 10 takes the md5 of that output, and §9 of the spec preserves it.

| # | Check | How it shows |
|---|---|---|
| 1 | Pool invariance — doc set changed on 0 of 300 | `--pair` exits 1 with `ARM INVALID: pool changed` otherwise |
| 2 | ≥25% of queries reordered | `--pair` exits 1 with `ARM INVALID … at least 25% is required` otherwise |
| 3 | `R@50` identical to `0.9193` at 4 dp | read off `[scores]`; a deviation means the pool was corrupted |
| 4 | No result above the oracle **0.9196** | above it means label leakage, not a good teacher — stop and investigate |
| 5 | Zero duplicate `(qid, score)` pairs | **`report.py` does not compute this** — it counts duplicate `(query_id, doc_id)`. Run it yourself (below) |

Check 5, explicitly — expect `0`:

```bash
awk '{print $1, $5}' $A/teacher-ceiling.chunks.trec | sort | uniq -d | wc -l
```

Run it on the **teacher** run, not the baseline: scores there are rank-derived (`51 − position`) so
ties are structurally impossible, and any duplicate means a writer bug. The same command on
`rerank-a0prime.chunks.trec` returns `7` — those are real ties in the fusion scores, all at rank ≥23,
and they are a property of the baseline, not a defect.

---

## 8. [pod runs, dev scores] The repeat — noise floor

Two passes, **each with its own `--responses` path**, same `--subsample-seed`:

```bash
for P in 1 2; do
  python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
    --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
    --base-url http://127.0.0.1:8000 --model <model-id> --seed 20260920 --shuffle-seed 20260920 \
    --subsample 50 --subsample-seed repeat-2026-09 \
    --responses $A/repeat-$P.responses.jsonl --out $A/repeat-$P.chunks.trec
done
```

Build a qrels restricted to those 50 query ids, then compare with `--baseline`, **never `--pair`**:

```bash
cut -d' ' -f1 $A/repeat-1.chunks.trec | sort -u > /tmp/sub50.ids
awk 'NR==FNR{ids[$1];next} $1 in ids' /tmp/sub50.ids $B/qrels.trec > $A/qrels-sub50.trec
python3 $S/report.py --run $A/repeat-2.chunks.trec --qrels $A/qrels-sub50.trec \
        --baseline $A/repeat-1.chunks.trec
```

`qrels.trec` is **TAB**-separated while the run files are space-separated. The `awk` above handles
both (its default field splitting covers tabs), but `cut -d' ' -f1` on the qrels returns the whole
line and will silently miscount if you verify the result that way — check with
`awk '{print $1}' $A/qrels-sub50.trec | sort -u | wc -l`, expect `50`.

Two traps, both already burned once in design review:

- **Against the full 300-query qrels the delta is divided by 6** (`ir_measures` scores every qrels
  query, so 50 real differences are averaged over 300 denominators) and the `query sets differ`
  warning is suppressed. The restricted qrels is what makes the number real.
- **`--pair` refuses a near-deterministic second pass** with `ARM INVALID … at least 25% is
  required`, which is precisely the outcome this check exists to observe.

Reported, not gating. Confirm `distinct queries 50` and `covered by this run 50 / 50` in the output.

---

## 9. [both] Preserve and destroy

**Pull everything off the pod before terminating — `/workspace` does not survive.**

```bash
scp -P <port> -r root@<host>:/workspace/artifacts/. $A/
ls $A    # expect: teacher-ceiling.chunks.trec + .meta.json, main/repeat-1/repeat-2 responses JSONLs,
         # smoke.* , and the run log
```

Preserve in `$A/` on the dev box: all response JSONLs, the run file, the sidecar, the full
`report.py` output (`report-output.txt` from step 7), and the run log. **No `git add` in that tree** —
it is not a git repo.

Then **terminate** the pod — not "stop". A stopped pod still bills for storage. If you created a
network volume for the model weights and you are done with the experiment, delete that too; it bills
continuously.

Check the artifacts are readable on the dev box *before* you terminate. Recovering a terminated pod's
disk is not possible.

---

## 10. [dev] The verdict — Task 4, free, offline

```bash
md5sum $A/teacher-ceiling.chunks.trec $A/report-output.txt
git add -f docs/plans/2026-09-GATE-teacher-ceiling.md     # docs/plans is gitignored
```

Write `docs/plans/2026-09-GATE-teacher-ceiling.md` in the form of
`docs/plans/2026-09-GATE-reranker-phase1.md`: method, serving identity, the five checks with their
results, the repeat's noise floor, and the verdict.

**PASS requires both:** permutation `p < 0.05` **and** delta ≥ **+0.055** (nDCG@10 ≥ 0.753).
They are independent — the Phase 1 A2 arm cleared significance with a delta of −0.1126.

A PASS triggers the stage 2 spec. **A FAIL closes the per-tenant reranking line, and the verdict
says so in those words.**

The report output is re-derivable after the instance is gone — `report.py`'s permutation statistics
use fixed constants (`PERMUTATION_SEED = 20260831`, 10,000 resamples), so re-running against the
preserved run file reproduces the same p.

---

## Failure playbook

| Symptom | Cause | Do |
|---|---|---|
| `finish_reason=length` rejections | the **completion** hit `max_tokens` (8192) | raise `MAX_COMPLETION_TOKENS`. This is *not* the prompt-budget failure |
| HTTP 400 from vLLM | prompt exceeded `max_model_len` | raise `--max-model-len`; unlikely at 128K |
| `ledger records a different pass` + exit 1 | you reused a `--responses` path, or changed a seed or `--run` mid-pass | use a fresh path per pass; do not edit seeds to "retry" |
| `<path>:<line>: malformed ledger line` | crash mid-append | delete that last line and re-run; it is by definition not an accepted entry |
| `ARM INVALID: pool changed` | wrong `--run`, or the pool was corrupted | check `--run` points at `rerank-a0prime.chunks.trec` |
| `ARM INVALID … at least 25% is required` | the teacher barely reordered — **or** you used `--pair` on the repeat | on the main run, investigate; on the repeat, use `--baseline` |
| `BUILD UNKNOWN` | sidecar not found | `--out` must end `.chunks.trec` |
| `BUILD MISMATCH … confounded` | sidecar composite is not `31583db5aea49136` | fix the sidecar; no re-spend needed, it is a JSON file |
| Result **above 0.9196** | label leakage, not a good teacher | stop; something is feeding relevance into the ordering |
| `could not import ir_measures` | `PYTHONPATH` unset in this shell | re-export it; it is per-shell |

## What is still unverified going in

- The `8192` completion budget was never measured — no tokenizer is reachable on the dev box. An
  inadequate budget costs a pass, loudly, via `finish_reason=length`; it cannot silently corrupt the
  gate.
- The real model's reply shape is exercised for the first time at step 5. That is why step 5 exists
  and why it is one query, not three hundred.
