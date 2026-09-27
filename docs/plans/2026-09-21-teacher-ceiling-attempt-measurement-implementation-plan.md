# Teacher-ceiling attempt-failure-rate measurement — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md` (commit SHA: `f2c2a139fbfe321b4b8c23683b29f19a0ea414b5`)

**Goal:** measure real per-query attempt behavior for gpt-oss-120b on 8 new SciFact queries (up to 4 attempts each), to give a later, separate design the data it needs to pick `RETRY_BUDGET`/`MAX_COMPLETION_TOKENS` values and decide whether to relax spec §6 row 2's all-or-nothing refusal rule.

**Architecture:** zero changes to the existing, already-reviewed `teacher_rerank.py` — each of 8 queries gets 2 sequential invocations of the unmodified script (4 attempts total), each into its own `--responses`/`--out` file pair. A new, separate summarizer script turns the resulting ledgers into a scannable table. The exact command sequence is appended to the existing runbook.

**Tech stack:** Python 3.14 stdlib only, plus `pytest` for the new test suite — no new dependency (matches the existing `teacher_rerank.py`/`stub_vllm_server.py` convention).

---

## File Structure

- Create: `Iverson.Server/Iverson.LoadTest/scripts/summarize_teacher_attempts.py` — the summarizer.
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py` — its pytest suite.
- Modify: `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md` — append the 8-query measurement batch as a new numbered section, and fix Steps 5/6's existing commands to survive the SSH-session-tied SIGTERM bug (see Task 2, Step 1).

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are NOT re-verified here:

- `--subsample 1 --subsample-seed <N>` restricts a pass to exactly one query; seeds 101–108 select 8 distinct query ids (770, 521, 1332, 783, 873, 1370, 759, 51) with no collisions with each other or with query 936 — verified by running `select_subsample` against the real 300-query pool.
- The full CLI flag set (`--run --corpus --queries --base-url --model --seed --shuffle-seed --responses --out --subsample --subsample-seed --api-key`) matches `build_arg_parser` exactly (`teacher_rerank.py:535-548`).
- Two sequential invocations with identical args against an existing `--responses` file resume correctly at the `main()` level: `pass_identity(args)` is deterministic given identical args, `validate_ledger_pass` accepts a match, and an already-accepted query short-circuits before any model call.
- `teacher_rerank.py` is importable with no module-level side effects (`if __name__ == "__main__": main()` is the only top-level executable statement, `:619-620`).
- The new test file's convention (docstring naming the run command, `sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))`, stdlib + pytest only) matches `test_teacher_rerank.py`'s.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.LoadTest/scripts/summarize_teacher_attempts.py` does not yet exist | `ls` at that path: `No such file or directory` |
| 2 | File path | `Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py` does not yet exist | `ls` at that path: `No such file or directory` |
| 3 | File path | `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md` exists, is 399 lines, working tree clean | `wc -l` → 399; `git status --porcelain` → empty |
| 4 | Function signature | `read_responses_ledger(path)` returns `{query_id(str): [record, ...]}`; `accepted_entry(records)` returns the first record with `status == "accepted"` or `None`; `make_record` produces records with keys `query_id, status, content, order, reason, pass` | Fresh read, `teacher_rerank.py:232-298` |
| 5 | Test/build command | `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py -q` will collect correctly — no `pytest.ini`/`conftest.py` restricts discovery | `find . -maxdepth 3 -iname "pytest.ini" -o -iname "conftest.py"` → no results |
| 6 | Task ordering | Task 1 (new script + tests) has no dependency on Task 2; Task 2's final step consumes Task 1's script — no circular dependency | Task 1 is create-only with no references to Task 2's artifacts; Task 2's Interfaces section names Task 1's script as a consumed input |
| 7 | Code validity | `argparse` with `nargs="+"` for one-or-more positional paths is valid stdlib syntax at the repo's Python version | `python3 --version` → `Python 3.14.4` (stdlib `argparse.nargs="+"` has been standard since Python 2.7) |
| 8 (Cat 6 — Task 2 modifies the runbook) | Consumer impact | Nothing else in the repo references `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md`'s path or its internal step numbers, so appending a new section is safe | `grep -rl "2026-09-20-teacher-ceiling-task3-runbook" docs/` → no results |
| 9 | Code-in-plan validity | Runbook Step 5's code block is exactly lines 200–207; Step 6's is exactly lines 217–223 (the exact commands Task 2 Step 1 rewrites) | Fresh read, `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md:198-232` |
| 10 | Sibling-set (seed-triple convention) | The runbook's own smoke-test precedent sets `--seed`, `--shuffle-seed`, and `--subsample-seed` to the *same* value for a single-query pass (`7` in the existing Step 5) — Task 2 follows this exact convention for each of the 8 new seeds | Fresh read, `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md:200-207`: `--seed 7 --shuffle-seed 7 ... --subsample 1 --subsample-seed 7` |
| 11 | Rule-like (completion-detection mechanism) | A `pgrep -f` pattern anchored to both the script name and the invocation's unique `--responses` path (`"teacher_rerank.py.*--responses <path>"`) correctly tracks a real `teacher_rerank.py` process for its full lifetime, and does not match unrelated background commands that a bare filename-substring pattern would; a two-phase `until <appears>; while <disappears>` structure avoids a race where the first check could run before the backgrounded process exists | `docs/criticalreviews/2026-09-21-teacher-ceiling-attempt-measurement-implementation-plan-critical-review-1.md` Finding 1 (proves the original `PID=$!`/`kill -0` mechanism broken); verified in the `update-implementation-plan` round applying this fix: an anchored pattern correctly ignored unrelated background processes that a bare substring pattern matched, and a `while pgrep; do sleep; done` loop tracked a real process for its full observed lifetime with no early exit |

**Note on the `setsid nohup ... & disown` pattern used in Task 2:** the launch itself (`setsid nohup ... & disown`) was validated live during the 2026-09-21 GPU session (a request survived >6 minutes past the ~5m42s window that had previously killed 3 requests) but was never written back into the committed runbook — `grep -rn "setsid" docs/plans/*.md` finds it nowhere in this line of work, so the launch still rests on operator testimony rather than repo evidence. The *completion-detection* half (originally `PID=$!`/`kill -0`) was found broken by `critical-implementation-review` round 1 — under job control, plain `setsid` (no `-f`/`--wait`) forks internally and the PID `$!` captures exits almost immediately, well before the real process does — and was replaced with a `pgrep -f`-based check, verified locally (not live) to track a real process for its full lifetime. Per that review's Forced decision 2, `setsid --wait` (which would also fix PID-tracking) was deliberately NOT adopted, to avoid introducing an unverified change to the launch mechanism itself; the `pgrep` replacement touches only completion-detection, leaving the live-validated launch untouched.

## Tasks

### Task 1: `summarize_teacher_attempts.py` and its suite

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/summarize_teacher_attempts.py`
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py`

**Interfaces:**
- Consumes: `teacher_rerank.read_responses_ledger` and `teacher_rerank.accepted_entry` (existing, unmodified).
- Produces: the script Task 2's final step runs against the 8 measurement ledgers.

- [ ] **Step 1: Write the pytest suite first.** Match `test_teacher_rerank.py`'s shape — module docstring naming the run command, `sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))`, stdlib + pytest only, `tmp_path` fixture for ledger files. Four cases, per the spec:

```python
"""pytest suite for summarize_teacher_attempts.py (spec
docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py -q

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import summarize_teacher_attempts as sta  # noqa: E402


def write_ledger(path, records):
    with open(path, "w", encoding="utf-8") as f:
        for record in records:
            f.write(json.dumps(record) + "\n")
    return str(path)


def record(query_id, status, reason=None, pass_id="p1"):
    return {
        "query_id": query_id,
        "status": status,
        "content": "...",
        "order": None,
        "reason": reason,
        "pass": pass_id,
    }


def test_query_accepted_on_first_entry(tmp_path):
    path = write_ledger(tmp_path / "a.jsonl", [record("1", "accepted")])
    rows = sta.summarize_ledger(path)
    assert rows == [("1", 1, "accepted", [])]


def test_query_exhausted_after_four_rejected(tmp_path):
    records = [record("1", "rejected", reason=f"reason {i}") for i in range(4)]
    path = write_ledger(tmp_path / "a.jsonl", records)
    rows = sta.summarize_ledger(path)
    assert rows == [("1", 4, "exhausted", ["reason 0", "reason 1", "reason 2", "reason 3"])]


def test_mixed_failure_reasons(tmp_path):
    records = [
        record("1", "rejected", reason="finish_reason=length"),
        record("1", "rejected", reason="finish_reason=length"),
        record("1", "rejected", reason="not a permutation: missing ids"),
        record("1", "rejected", reason="not a permutation: missing ids"),
    ]
    path = write_ledger(tmp_path / "a.jsonl", records)
    rows = sta.summarize_ledger(path)
    assert rows[0][2] == "exhausted"
    assert rows[0][3] == [
        "finish_reason=length",
        "finish_reason=length",
        "not a permutation: missing ids",
        "not a permutation: missing ids",
    ]


def test_multiple_files_produce_one_row_per_file_per_query(tmp_path):
    path_a = write_ledger(tmp_path / "a.jsonl", [record("1", "accepted")])
    path_b = write_ledger(tmp_path / "b.jsonl", [record("2", "rejected", reason="x")])
    table = sta.format_table(
        [(path_a, sta.summarize_ledger(path_a)), (path_b, sta.summarize_ledger(path_b))]
    )
    assert "a.jsonl" in table and "1" in table
    assert "b.jsonl" in table and "2" in table
```

- [ ] **Step 2: Implement the script.**

```python
"""Summarizes one or more teacher_rerank.py --responses ledger files into a per-query
attempt-count and outcome table, to inform the follow-on retry/budget decision (spec
docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md). Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/summarize_teacher_attempts.py <ledger.jsonl> [<ledger.jsonl> ...]

No non-stdlib imports."""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import teacher_rerank as tr  # noqa: E402


def summarize_ledger(path):
    """Returns [(query_id, attempt_count, status, reasons), ...] for one ledger file, in
    `read_responses_ledger`'s dict insertion order. `reasons` is empty for an accepted query --
    only exhausted queries carry their rejected attempts' reasons."""
    ledger = tr.read_responses_ledger(path)
    rows = []
    for query_id, records in ledger.items():
        accepted = tr.accepted_entry(records)
        status = "accepted" if accepted is not None else "exhausted"
        reasons = [] if accepted is not None else [
            r.get("reason") for r in records if r.get("status") == "rejected"
        ]
        rows.append((query_id, len(records), status, reasons))
    return rows


def format_table(path_rows):
    """`path_rows` is [(path, [(query_id, attempt_count, status, reasons), ...]), ...]. One
    printed row per (file, query) -- a query_id repeated across files is never merged."""
    lines = [f"{'file':<24} {'query_id':<10} {'attempts':<9} {'status':<10} reasons"]
    for path, rows in path_rows:
        base = os.path.basename(path)
        for query_id, attempt_count, status, reasons in rows:
            reason_text = "; ".join(reasons) if reasons else "-"
            lines.append(f"{base:<24} {query_id:<10} {attempt_count:<9} {status:<10} {reason_text}")
    return "\n".join(lines)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("responses", nargs="+", help="one or more --responses ledger JSONL paths")
    args = ap.parse_args(argv)

    path_rows = [(path, summarize_ledger(path)) for path in args.responses]
    print(format_table(path_rows))


if __name__ == "__main__":
    main()
```

- [ ] **Step 3: Run the suite.**
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py -q
```
All cases pass before proceeding.

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/summarize_teacher_attempts.py Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py
git commit -m "add summarize_teacher_attempts.py and its suite"
```

### Task 2: The measurement batch (rented pod, live session)

Everything that costs money happens here — same discipline as the original plan's Task 3. Rents a GPU; a controller executing this plan via SDD should stop-and-ask before this task, exactly as the original plan's Ruling 2 did.

**Files:**
- Modify: `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md` (fix Steps 5/6's detachment gap; append the new measurement section).
- Create (outside the repo, on the pod's `$A` artifacts dir): `q101.responses.jsonl`/`q101.chunks.trec` … `q108.responses.jsonl`/`q108.chunks.trec`.

**Interfaces:**
- Consumes: Task 1's `summarize_teacher_attempts.py`; the existing runbook's pod-connection and provisioning steps (§§1–4) if the pod from the prior live session is no longer active.

- [ ] **Step 1: Fix the runbook's Steps 5/6 detachment gap, and append the new section.** Steps 5 and 6 (`docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md:200-207` and `:217-223`) still launch `teacher_rerank.py` as a plain foreground command — the exact shape that hit the SSH-session-tied SIGTERM bug live. Replace each with the launch-plus-detection pattern below: launch via `setsid nohup ... & disown`, matching what the live session validated; completion detected via `pgrep -f`, not `PID=$!`/`kill -0` (see `docs/criticalreviews/2026-09-21-teacher-ceiling-attempt-measurement-implementation-plan-critical-review-1.md` Finding 1 for why the PID form doesn't track the real process under an interactive shell's job control).

Step 5 becomes:
```bash
setsid nohup python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
  --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
  --base-url http://127.0.0.1:8000 --model <model-id> --seed 7 --shuffle-seed 7 \
  --subsample 1 --subsample-seed 7 \
  --vllm-version <ver> --quantisation mxfp4 --instance-type <sku> \
  --responses $A/smoke.responses.jsonl --out $A/smoke.chunks.trec \
  > $A/smoke.log 2>&1 < /dev/null &
disown
PATTERN="teacher_rerank.py.*--responses $A/smoke.responses.jsonl"
until pgrep -f "$PATTERN" > /dev/null; do sleep 1; done
while pgrep -f "$PATTERN" > /dev/null; do sleep 15; done
```

Step 6 becomes:
```bash
setsid nohup python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
  --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
  --base-url http://127.0.0.1:8000 --model <model-id> --seed 20260920 --shuffle-seed 20260920 \
  --vllm-version <ver> --quantisation mxfp4 --instance-type <sku> \
  --responses $A/main.responses.jsonl --out $A/teacher-ceiling.chunks.trec \
  > $A/main.log 2>&1 < /dev/null &
disown
PATTERN="teacher_rerank.py.*--responses $A/main.responses.jsonl"
until pgrep -f "$PATTERN" > /dev/null; do sleep 1; done
while pgrep -f "$PATTERN" > /dev/null; do sleep 15; done
```

Then append this new section at the end of the file (after "## What is still unverified going in", which stays the file's last section):

````markdown
---

## 11. [pod] Attempt-failure-rate measurement batch (8 queries)

Free-standing addendum, added 2026-09-21. Answers: before raising `RETRY_BUDGET` or
`MAX_COMPLETION_TOKENS`, or relaxing spec §6 row 2's refusal rule, how often does a real query
actually need more than 2 attempts? 8 new queries (seeds 101–108), 2 invocations each (4 attempts
total) of the unmodified script -- see
`docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md` for why 2 invocations, not a
new `--retry-budget` flag.

```bash
for SEED in 101 102 103 104 105 106 107 108; do
  for PASS in 1 2; do
    setsid nohup python3 $S/teacher_rerank.py \
      --run $B/runs/rerank-a0prime.chunks.trec \
      --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
      --base-url http://127.0.0.1:8000 --model <model-id> \
      --seed $SEED --shuffle-seed $SEED --subsample 1 --subsample-seed $SEED \
      --vllm-version <ver> --quantisation mxfp4 --instance-type <sku> \
      --responses $A/q$SEED.responses.jsonl --out $A/q$SEED.chunks.trec \
      > $A/q$SEED.pass$PASS.log 2>&1 < /dev/null &
    disown
    PATTERN="teacher_rerank.py.*--responses $A/q$SEED.responses.jsonl"
    until pgrep -f "$PATTERN" > /dev/null; do sleep 1; done
    while pgrep -f "$PATTERN" > /dev/null; do sleep 15; done
    echo "seed $SEED pass $PASS done -- $(tail -1 $A/q$SEED.pass$PASS.log)"
  done
done
```

Each query's second invocation is unconditional, even if the first already succeeded -- an accepted
entry short-circuits with no model call, so it costs seconds, not minutes.

Then run the summarizer (copy it up with `runpodctl send`/`receive`, same as `teacher_rerank.py`):

```bash
python3 $S/summarize_teacher_attempts.py $A/q101.responses.jsonl $A/q102.responses.jsonl \
  $A/q103.responses.jsonl $A/q104.responses.jsonl $A/q105.responses.jsonl \
  $A/q106.responses.jsonl $A/q107.responses.jsonl $A/q108.responses.jsonl
```

To fold in the 2 queries already tested live in the prior session (query 936's ledger and the
token-budget-exceeded query's), pass their `--responses` paths as additional arguments -- whatever
they were named in that session's `$A`.

Pull the 8 new ledgers down (`runpodctl send`) before terminating the instance, same as step 9.
````

- [ ] **Step 2: Commit the runbook changes.**
```bash
git add docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md
git commit -m "runbook: fix steps 5/6 SIGTERM detachment gap, add the 8-query measurement batch"
```

- [ ] **Step 3: Provision or reuse the pod.** If the pod from the prior 2026-09-21 session is still active and vLLM is still serving gpt-oss-120b, confirm with a smoke `curl` against `$base_url/v1/models` and skip to Step 4. Otherwise, re-provision per the runbook's existing §§2–4 (dashboard provision, serve the model, copy the three input files up) before continuing.

- [ ] **Step 4: Copy the new summarizer script up**, same `runpodctl send`/`receive` mechanism already used for `teacher_rerank.py`.

- [ ] **Step 5: Verify the detection mechanism on seed 101, then let the §11 loop run.** Before letting the loop run unattended, watch seed 101's pass 1 invocation once: confirm via `pgrep -af "teacher_rerank.py.*--responses $A/q101.responses.jsonl"` that the process appears while running and disappears once `tail -1 $A/q101.pass1.log` shows the script's own completion or failure line. Once confirmed for one iteration, let the loop continue unattended for the rest. Confirm the loop completes for all 8 seeds — each `q<seed>.responses.jsonl` should hold, for its one query, exactly one of: (a) a single accepted record and nothing else, (b) up to 4 rejected records with no accepted one (exhausted), or (c) 1-3 rejected records followed by one accepted record (the query needed a retry before succeeding — an expected outcome, not a defect, and exactly what this measurement is designed to observe).

- [ ] **Step 6: Run the summarizer** against all 8 ledgers (optionally including the 2 prior session's ledgers, per the runbook's note). Save its output — this is the deliverable a later design session reads to pick `RETRY_BUDGET`/`MAX_COMPLETION_TOKENS` and decide on §6 row 2.

- [ ] **Step 7: Pull the 8 ledgers (and logs) down, and decide on the pod.** Preserve `q101..q108.responses.jsonl` and the summarizer's output on the dev box. Terminate the pod once preserved, unless the operator wants it left running for a follow-on decision made the same session.

## Known issues inherited from spec

The `setsid nohup ... & disown` *launch* has no committed precedent to verify against (see "Verified plan-level assumptions," the note after row 10) — it is reconstructed from the live session's own account, and its SSH-disconnect-survival property remains validated only by that live session, not by any static review. Its original completion-detection mechanism (`PID=$!`/`kill -0`) was found broken and replaced with a `pgrep`-based check by `critical-implementation-review` round 1 (see `docs/criticalreviews/2026-09-21-teacher-ceiling-attempt-measurement-implementation-plan-critical-review-1.md`); that replacement is verified locally for process-tracking but, like the launch itself, not against a real live disconnect. If either half turns out not to hold live, that is itself informative for the next design.
