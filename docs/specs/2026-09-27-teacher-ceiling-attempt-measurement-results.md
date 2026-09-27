# Teacher-ceiling attempt-failure-rate measurement — results

**Source design:** `docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md`.
**Raw data:** `~/iverson-benchmark-data/teacher-ceiling-measurement-2026-09-27/` (not committed — ledgers,
pass logs, `measurement-summary.txt`; `md5sum measurement-summary.txt` =
`d60034ecd6606329c961e7a4431a6981` at time of writing).

## What ran

The design's 8 new queries (seeds 101–108, query ids 770/521/1332/783/873/1370/759/51), 2 sequential
`teacher_rerank.py` invocations each (4 attempts ceiling), against `openai/gpt-oss-120b` on vLLM 0.30.0,
MXFP4, H100-80GB. The batch's outer loop (`run_measurement_batch.sh`) was interrupted mid-run by an SSH
disconnect after seed 101 completed — the individual invocations were detached (`setsid nohup … &
disown`) but the outer loop itself was not, so it died to SIGHUP between seeds. Resumed via a
separately-detached invocation for seeds 102–108; no query was re-run, no data lost.

## Results — 8 new queries

| seed | query id | attempts | outcome |
|---|---|---|---|
| 101 | 770 | 4 | exhausted — 1 duplicate-id validation failure, then 2× `finish_reason=length` |
| 102 | 521 | 0 (crashed) | **both invocations raised an uncaught `HTTPError: 500` inside `call_teacher`, before any ledger entry was written.** Not a `score_query` rejection — the process died outright. Invisible to `summarize_teacher_attempts.py` (nothing to read); recorded manually in `measurement-summary.txt`. |
| 103 | 1332 | 4 | exhausted — 4× `finish_reason=length` |
| 104 | 783 | 1 | **accepted**, first attempt |
| 105 | 873 | 4 | exhausted — 4× `finish_reason=length` |
| 106 | 1370 | 4 | exhausted — 4× `finish_reason=length` |
| 107 | 759 | 4 | exhausted — 4× `finish_reason=length` |
| 108 | 51 | 4 | exhausted — 4× `finish_reason=length` |

**1/8 succeeded.** 6/8 exhausted their full attempt budget on `finish_reason=length` (the completion hit
the 8192-token `MAX_COMPLETION_TOKENS` cap before finishing). 1/8 crashed the client process outright on
an uncaught HTTP 500 from vLLM, on both invocations, producing no ledger data at all.

## Combined with the 2 queries tested live on 2026-09-21

(Per the design spec's Background section — those ledgers weren't located locally in this session, so
these two lines are as documented there, not independently re-inspected here.)

| query | attempts | outcome |
|---|---|---|
| 936 | 5 (2+2+1 across 3 invocations, third cut short by the since-fixed SSH SIGTERM issue) | exhausted — every attempt landed on exactly 46 of the required 50 ids, including 2 attempts made *after* adding `minItems`/`maxItems: 50` to the guided-JSON schema; no duplicate/near-duplicate titles found in the pool |
| (unlabeled) | 1+ | exhausted attempt 1 — exceeded the 8192-token `MAX_COMPLETION_TOKENS` budget (`finish_reason: length`) without finishing |

**Combined: 1 success out of 10 real queries tested (10%).** Failure modes observed, 4 distinct:
1. `finish_reason=length` — completion truncated at the 8192-token cap (dominant mode: 6/8 new queries, plus the unlabeled prior query)
2. Duplicate-id validation failure (query 770, alongside length-truncation on its other attempts)
3. Stuck-at-46-of-50 — schema constraints (`minItems`/`maxItems`) did not prevent it (query 936)
4. Uncaught HTTP 500 from vLLM, crashing the client before any ledger write (query 521) — not retried, not recorded as a `score_query` rejection reason, because the exception propagates out of `main()` uncaught

## What this measurement does and does not decide

Per the design's explicit scope, this is a data point, not a decision:

- **Not decided here:** final values for `RETRY_BUDGET` / `MAX_COMPLETION_TOKENS`; whether to relax spec
  `2026-09-20-teacher-ceiling-design.md` §6 row 2's all-or-nothing refusal rule; the 300-query production
  run itself.
- **What the data says about those follow-ons:** at a 10% observed success rate, an unmodified
  `RETRY_BUDGET`/`MAX_COMPLETION_TOKENS` and an unrelaxed §6 row 2 refusal rule would very likely spend
  the full 300-query GPU budget and write close to nothing — the risk the design set out to quantify is
  real, not hypothetical. `finish_reason=length` alone (6/8 + 1 more) suggests `MAX_COMPLETION_TOKENS` is
  the single highest-leverage lever to revisit first. The HTTP 500 crash (query 521) is a distinct
  robustness gap — `call_teacher` has no handling for a raw server error, unlike its handling of
  validation/parse failures — worth a look before any production run, independent of the retry-budget
  question.
