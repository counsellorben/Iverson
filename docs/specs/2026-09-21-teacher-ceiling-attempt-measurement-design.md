# Teacher-ceiling attempt-failure-rate measurement — design

**Source:** follow-on to `docs/specs/2026-09-20-teacher-ceiling-design.md` (CDR'd 4 rounds) and its
implementation (`teacher_rerank.py`, `stub_vllm_server.py`, `.worktrees/teacher-ceiling`, branch
`teacher-ceiling`). Motivated by the live H100 pod session on 2026-09-21, which paused mid-Task-3
after 2/2 real queries hit genuine failures (see "Background").

## Background

The live GPU session tested 2 real queries against gpt-oss-120b on vLLM 0.29.0:

- Query 936: 5/5 attempts landed on exactly 46 of the required 50 ids, including 2 attempts made
  *after* adding `minItems`/`maxItems: 50` to the guided-JSON schema — the schema constraint did not
  fix it. No duplicate/near-duplicate document titles in the pool (checked offline).
- A second, different query exceeded the 8192-token `MAX_COMPLETION_TOKENS` budget without finishing
  at all on attempt 1 (`finish_reason: "length"`).

Spec `2026-09-20-teacher-ceiling-design.md` §6 row 2 is unconditional: **any query left unscored at
the end of a pass means no run file is written at all.** At an observed 2/2 real-query failure rate,
the full 300-query paid run risks spending its entire GPU budget and producing zero output. That is
the risk this measurement exists to quantify before either (a) raising `RETRY_BUDGET` /
`MAX_COMPLETION_TOKENS`, or (b) relaxing §6 row 2's refusal rule — both are follow-on decisions, not
part of this design.

## Scope

**In scope:** a measurement — 8 new SciFact queries (distinct from the 2 already tested), up to 4
attempts each — that produces real per-query attempt data, plus a script to summarize it into a
human-scannable table.

**Explicitly out of scope** (deferred to a later design, once this data exists):
- Picking final values for `RETRY_BUDGET` or `MAX_COMPLETION_TOKENS`.
- Relaxing spec §6 row 2's all-or-nothing refusal rule. That rule is load-bearing for A27's gate
  statistics (delta computed as a mean over the full 300-query intersection, MDE 0.043) — touching it
  requires recalculating the gate's statistical power at whatever n results, which this design does
  not attempt.
- The 300-query paid production run itself.
- Any concurrent/parallel query orchestration.

## Orchestration — zero changes to `teacher_rerank.py`

Each of 8 queries gets its own single-query pass, with its own `--responses`/`--out` file pair,
mirroring the live session's `smoke`/`smoke2` convention (`q101.responses.jsonl`/`q101.chunks.trec`,
… `q108.responses.jsonl`/`q108.chunks.trec`).

This is not a style choice — it is forced by `validate_ledger_pass` (`teacher_rerank.py:320-347`),
which walks every record in a `--responses` file and refuses to resume (`sys.exit`, before any model
call) the moment a record's stamped `pass` identity — `{run, shuffleSeed, subsample, subsampleSeed}`
— doesn't match the invoking run's own identity. Since each of the 8 queries needs its own
`--subsample-seed` to land on a different query id, a **shared** `--responses` file across queries
would hard-refuse on the second query's very first invocation.

**Query selection:** `--subsample 1 --subsample-seed <101..108>`. Verified against the real 300-query
pool (`/tmp/claude-1000/.../scratchpad/rerank-a0prime.chunks.trec`, loaded via `teacher_rerank.load_run`):
seeds 101–108 select 8 distinct query ids (770, 521, 1332, 783, 873, 1370, 759, 51) — no collisions
with each other or with query 936.

**Attempt ceiling — 2 invocations per query, no new CLI flag:** `score_query`'s retry loop
(`teacher_rerank.py:399+`) grants a fresh `RETRY_BUDGET` (currently 2) to every new process
invocation, independent of how many rejected entries already sit in the ledger — only an *accepted*
entry short-circuits it (`accepted_entry`, `:271-279`). So invoking the existing, unmodified script
**twice**, sequentially, with identical arguments and the same `--responses` path, gives exactly
4 attempts (invocation 1 = attempts 1–2, invocation 2 = attempts 3–4) — matching live evidence, where
query 936 reached 5 attempts across 3 invocations (2+2+1, the third cut short by the SSH-session
SIGTERM issue, since fixed).

Running the second invocation unconditionally (not just when the first failed) is safe and cheap: if
the first invocation already produced an accepted entry, `main()`'s per-query loop (`:596-605`) calls
`score_query`, which returns the accepted order immediately with no model call — confirmed by reading
`main()`'s flow directly, not just `score_query` in isolation.

A considered alternative — a new `--retry-budget N` CLI override, doing all 4 attempts inside one
invocation — was rejected: it has no behavioral difference from 2 invocations (same total attempts,
same per-attempt mechanics), and would add a new CLI surface, a new test, and touch a script that has
already been through 4 rounds of CDR, 2 rounds of CIR, SDD implementation, and a final whole-branch
review, for zero measurable benefit.

**Execution:** sequential across all 8 queries (not concurrent) — matches what's already proven live,
avoids untested concurrent-load behavior for a one-time measurement where wall-clock time isn't
critical. Each invocation launched detached (`setsid nohup ... & disown`), per the platform fix
already validated live (survived >6 minutes past the ~5m42s window that had previously killed 3
requests). The exact 16-invocation command sequence is an operational runbook addendum, not new code.

## New summarizer script

`Iverson.Server/Iverson.LoadTest/scripts/summarize_teacher_attempts.py`.

- Takes one or more `--responses` ledger file paths (positional args), so the 2 already-tested pod
  ledgers (query 936's and the token-budget-exceeded query's) can optionally be included later for a
  combined n=10 view, without any special-casing.
- Imports and reuses `read_responses_ledger` from `teacher_rerank.py` (confirmed importable with no
  module-level side effects — `teacher_rerank.py`'s only top-level executable statement is the
  `if __name__ == "__main__": main()` guard at `:619-620`) rather than reimplementing JSONL parsing.
- For each query id across the given files, prints one row: query id, total attempts recorded, final
  status (`accepted` / `exhausted`), and — for exhausted queries — each attempt's failure reason
  verbatim from the ledger's `reason` field (`finish_reason=length`, a JSON parse error, or a
  `validate_permutation` detail).
- Plain text table to stdout. No JSON output mode — the only consumer is a person deciding the
  follow-on design's parameters, not another script.

### Tests

`test_summarize_teacher_attempts.py`, same directory, same convention as `test_teacher_rerank.py`:
module docstring naming the `python3 -m pytest .../test_summarize_teacher_attempts.py -q` invocation,
`sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))`, stdlib + pytest only, hand-computable
fixtures. Cases: a query accepted on its first ledger entry; a query with 4 rejected entries and no
accepted one (exhausted); a query whose entries mix failure reasons (e.g. 2 length-truncations then 2
validation failures); multiple `--responses` files passed together.

## Cost estimate

16 invocations total (2 × 8 queries). Rough carry-forward from the live session's own estimate for
"8-10 more single queries" — roughly $6–12 of GPU time — not a hard commitment, since only 2 real
per-query timing data points exist so far.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `--subsample 1 --subsample-seed <N>` restricts a pass to exactly one query, and seeds 101–108 pick 8 distinct query ids with no collisions | Ran `select_subsample` (`teacher_rerank.py:158-164`) against the real preserved pool for seeds 101–108: 8/8 distinct (770, 521, 1332, 783, 873, 1370, 759, 51) |
| 2 | The proposed CLI flag set (`--run --corpus --queries --base-url --model --seed --shuffle-seed --responses --out --subsample --subsample-seed --api-key`) matches the script's current, actual flags | Read `build_arg_parser` (`teacher_rerank.py:535-548`) — every flag present exactly as named; types confirmed (`--subsample`/`--subsample-seed` are `int`) |
| 3 | Two sequential invocations with identical args against an existing `--responses` file resume correctly at the `main()` level: identity matches, remaining attempts run, and an already-accepted query short-circuits with no model call | Read `main()` (`teacher_rerank.py:576-618`) end to end: `pass_identity(args)` computed fresh each invocation from CLI args (deterministic given identical args) passed to `validate_ledger_pass`; per-query loop calls `score_query`, which checks `accepted_entry` before issuing any call |
| 4 | `teacher_rerank.py` is importable without executing `main()` as a side effect, so the summarizer can `from teacher_rerank import read_responses_ledger` | Read the file's only top-level executable statement: `if __name__ == "__main__": main()` at `:619-620`. Confirmed by actually importing it: `python3 -c "from teacher_rerank import read_responses_ledger, accepted_entry"` succeeded with no output/side effects |
| 5 | The new summarizer's tests should live alongside `test_teacher_rerank.py` and follow its exact convention | Read `test_teacher_rerank.py:1-29`: docstring names `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`; `sys.path.insert` line at `:25`; "No non-stdlib imports beyond pytest" stated at `:19`; matches the implementation plan's P9/P17 verified assumptions for the sibling `test_popularity_rerank.py` |
