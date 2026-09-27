# Teacher-ceiling lenient acceptance, per-query fallback, and concurrency — design

**Amends:** `docs/specs/2026-09-20-teacher-ceiling-design.md` §4 (serving), §6 (failure behaviour),
§8 (structural checks, repeat check), §11 (execution outline). Everything in that spec not named
here stands.
**Source data:** the v2 re-measurement under `docs/specs/2026-09-27-teacher-ceiling-attempt-fixes-design.md`
— ledgers, pass logs and `batch.log` preserved at
`~/iverson-benchmark-data/teacher-ceiling-measurement-2026-09-27-v2/artifacts-v2/`, analysed by
`prefix_check.py` in the same directory (output `prefix-check-output.txt`, md5
`7631d1f4e3d8c672eb69bb2a3806d95f`).
**Rulings:** Ben, 2026-09-27 — lenient acceptance, per-query fallback, `RETRY_BUDGET` 4, a 5% fallback
cap, and the concurrency/token-cap/timeout parameters below.

## 1. Why

The v2 re-measurement (8 queries, 2 invocations × 2 attempts each, 6 concurrent) accepted **2 of 8
queries, 2 of 30 real attempts**. Raising the completion budget removed `finish_reason=length`, and
exposed what it had hidden:

| Outcome (30 real attempts) | Count |
|---|---|
| wrong length (typically 35–49 ids; two collapses to 4–6) | 21 |
| client timeout at 600 s | 7 |
| accepted | 2 |

(Every v2 ledger also opens with four `HTTP 401` entries. They precede this batch — the pass logs
identify entries 5–8 as its two passes — and come from an earlier launch into the same directory
with a stale key. They are excluded throughout.)

At that rate the §6 row 2 all-or-nothing rule cannot produce a 300-query run file. But the short
lists are not garbage. Across the 23 lists returned:

- **0 out-of-pool ids**, ever; **1 duplicate** (the last element of one 39-id list).
- **19 of 23 have a valid prefix of ≥ 20 ids.** The other 4 are early collapses (4–6 ids at
  1,182–1,790 completion tokens).
- Omitted ids are spread through the pool (median A0′ rank 27; 40 of 307 omissions were in the A0′
  top 10), but only **2 of 29 in-pool relevant documents** were ever omitted, both in one 35-id list.
  In practice an omission is the model judging a document irrelevant.
- Repaired as in §2 below, the teacher's nDCG@10 against A0′ on these 8 queries is **+0.17 mean,
  3 large gains (873 +0.39, 1370 +0.61, 759 +0.37), 0 losses**. n = 8 and four queries are tied
  at 1.0 or 0.0 on both sides, so this is not a gate result; it is the reason the method is worth
  rescuing rather than abandoning.

## 2. Lenient acceptance (amends §3 "One call per query" and §6 row 1)

The prompt is unchanged (`PROMPT_TEMPLATE_SHA256` stays as recorded), so these measurements carry
over to the production run.

A reply is parsed as today (JSON array, ids normalised with `str()`). Then:

1. **The teacher's ranking is the reply's longest valid prefix**: elements in order, stopping at the
   first element that is not in the pool or repeats an earlier one. Everything after that point is
   discarded, including valid ids — a reply that has gone off the rails once is not trusted after it.
2. **Accept** if the prefix length is **≥ `min(20, pool size)`**. The `min` makes a complete
   permutation of a pool smaller than 20 acceptable (test fixtures use 3-document pools); on the
   real 50-document pools the threshold is 20. Why 20: the gate metric is nDCG@10, which reads only
   the top 10 positions; 20 gives a 2× margin and is what 19 of 23 real lists cleared.
3. **The written order** is the prefix, followed by the pool's remaining ids in A0′ order. An omitted
   id therefore ranks below every id the teacher placed. This is part of the method, not a repair
   artefact: an omission is the teacher's signal (§1), and the spec says so here so the verdict doc
   can say so too.
4. A prefix shorter than the threshold is a rejection, with reason
   `valid prefix too short: <n> < <threshold> (<what stopped it>)`.

The accepted ledger record stores the **written** 50-id order in `order` (so the resume path's
existing `validate_permutation` re-check, `teacher_rerank.py:433`, applies unchanged) and adds
`prefix_len`. `validate_permutation` itself is unchanged — it stays the check for stored orders; the
new rule is a separate function called only from the attempt loop (`:473`).

## 3. Per-query fallback with a 5% cap (amends §6 rows 1–2)

After `RETRY_BUDGET` rejected attempts, a query is a **fallback**: it is written in A0′ order
(delta 0 for that query).

- **Cap: `floor(0.05 × n)`** fallbacks, where n is the number of queries in the invocation (15 of
  300; 2 of a 50-query repeat; 0 for any invocation under 20 queries, including every single-query
  measurement invocation, which therefore behaves exactly as today). Over the cap, the run file and
  sidecar are **not written**, exit non-zero, and every failed query is listed with its last
  reason — today's refusal, now triggered by the cap (or by a corrupt resumed entry, below) instead
  of by the first failure.
- At or under the cap: the run file is written, stdout prints the fallback count and ids, and the
  sidecar records `"fallbackQueryIds": [...]` (empty list when none).
- The ledger gets no fallback record. A fallback is decided at write time from the ledger's
  all-rejected state, so resuming an interrupted pass re-attempts those queries with a fresh budget,
  as §6 row 4 already does for any all-rejected query.
- **A corrupt resumed entry is never a fallback** (Ben, 2026-09-27, CDR-1 §3.1). When a query's
  accepted ledger entry fails the resume re-check (`teacher_rerank.py:433-435`), the run is refused —
  no run file, no sidecar — whatever the fallback count. Only budget exhaustion (`:485`) produces a
  fallback. `score_query` returns the same `(None, reason)` shape for both today, so it must also
  report which of the two happened.

**Why this does not reopen `TeiRerankClient`'s objection.** §6 forbade "fill from fusion order"
because a silent fallback "writes a run file indistinguishable from a reranked one". This fallback
is counted, printed, recorded in the sidecar, and capped; the verdict doc reports it. The §11 dry-run
gate still catches a discarded-reply bug, but through a new check: the reversed pair (0.0032 /
0.9193) moves if a query falls back, while the identity pair (0.6980 / 0.9193) cannot, because a
fallback writes A0′ order — exactly what the identity stub returns. Both dry runs must therefore
also print `0 fallbacks`.

**Bias bound.** A fallback query counts as delta 0, but the teacher's true delta on it is unknown in
either direction, so the reported delta can be off by at most the fallback fraction times the
per-query delta range. The expected fallback rate at 4 attempts is ≈ 2% (per-attempt acceptance
19/30 → 0.37⁴ ≈ 1.9%, ≈ 6 of 300); the 5% cap sits ≈ 4 standard deviations above that. The verdict
doc reports the fallback count, and a PASS or FAIL within `fallbacks / n` of the +0.055 threshold
says so explicitly.

## 4. `RETRY_BUDGET` 4 (amends §6 row 1)

`RETRY_BUDGET` rises from 2 to 4 (one attempt plus three retries). Retries cost only on failing
queries. At the measured per-attempt acceptance of ≈ 63%, 2 attempts would leave ≈ 13.7% of queries
as fallbacks (≈ 41 of 300, over the cap); 4 leave ≈ 1.9%.

## 5. Completion cap and client timeout (amends §4)

`MAX_COMPLETION_TOKENS` falls from 32,768 to **16,384**; `call_teacher`'s `urlopen` timeout rises
from 600 s to **1,200 s**.

- **Measured decode rate: ≈ 24.8 tokens/s per request, flat across 1–5 concurrent requests.** The
  batch's 16 invocation end times (pass-log mtimes) each match `Σ completion_tokens / 24.8 + 600 s
  per timeout` from the batch start to within ≈ 8 s, at every concurrency the batch ran (e.g. q108
  pass 2's final attempt decoded 852 tokens in 34 s alone; q107 pass 2 decoded 7,628 tokens in
  305 s beside one other request; the first 6-way batch fits a start of 16:12:13 from four
  independent end times).
- At that rate 16,384 tokens take ≈ 661 s, so under a 1,200 s timeout **the server always ends a
  runaway generation (`finish_reason=length`, token count recorded) before the client gives up**.
  That makes orphaned server-side generations impossible by construction — whether vLLM 0.30.0
  aborts a request when a non-streaming client disconnects is unverified (conflicting upstream
  reports, some middleware-dependent), and this design does not rely on it. The 1,200 s timeout
  still fires first only if the per-request rate falls below ≈ 13.7 tokens/s.
- **No currently-succeeding reply is lost.** The largest valid list in 23 was 9,504 completion
  tokens. Today's 600 s timeout already ends every reply at ≈ 14,900 tokens, so a 16,384 cap only
  ever gives a long reply more room than it has now.
- The `finish_reason=length` rejection reason stops advising "raise MAX_COMPLETION_TOKENS" — under
  this design a length stop is the expected end of a runaway generation. It keeps naming
  `max_tokens` and distinguishing `max_model_len` (the assertions of
  `test_length_finish_reason_blames_max_tokens_not_the_prompt`).

## 6. Concurrency (amends §5's CLI and §11)

`teacher_rerank.py` scores queries one at a time (`main`, `:628-635`); the v2 batch's 6-way
concurrency came from `run_measurement_batch.sh` launching one process per single-query
invocation, and the production run has no such mechanism. Sequentially, 400 query-runs (300 main +
two 50-query repeats) at the measured ≈ 453 s per query would take ≈ 50 hours.

- **`--concurrency N`** (default 1): `main` scores `pool_query_order` on a
  `concurrent.futures.ThreadPoolExecutor(N)`, collecting results by query id; `write_run` still
  writes in `pool_query_order`, so output is independent of completion order. `score_query` shares
  no mutable state across queries (`ledger` and `args` are read-only after load); the one shared
  side effect, `append_response`, takes a module-level `threading.Lock`. Local probe: 12 threads ×
  150 appends of 120 KB records produced 0 unparseable lines without a lock, twice — but the pod's
  `/workspace` may be a network volume with weaker append semantics, and the lock is two lines.
- **Recommended N = 12 for the production run**, bounded by KV-cache memory, not speed:

  | Quantity | Value | Source |
  |---|---|---|
  | Largest prompt of the 300 | 30,719 tokens (query 852, 138,174 chars) | `build_prompt` over the pod's own corpus, at the most conservative measured 4.498 chars/token (range 4.498–4.584 over 3 live probes, whose local char counts match the pod's exactly) |
  | Per-request worst case | 30,719 + 16,384 = 47,103 tokens | §5 cap |
  | KV-cache pool | 655,317 tokens | `/metrics` `kv_cache_size_tokens` on the v2 pod |
  | 90% of pool ÷ worst case | 12.5 → **12** | |

  Speed is only measured to 5–6 concurrent; 12 is an extrapolation on speed, not on memory. The
  ledger therefore gains **`elapsed_s`** per attempt (wall time around the `call_teacher` call,
  measured in `score_query` so `call_teacher`'s return tuple is unchanged), making the per-request
  rate at 12 directly readable from the first completed attempts.
- **Estimated runtime** at 12 concurrent: 400 query-runs × ≈ 453 s ÷ 12 ≈ **4.2 GPU-hours** (≈ 8.4 at
  6). Per-query 453 s = expected 1.56 attempts × mean 291 s per attempt, the latter from the v2
  mix (23 lists at a mean 4,427 tokens ÷ 24.8 = 178 s; 7 runaways at ≈ 661 s under §5's cap). This
  replaces §11's "$2–5 per pass" estimate.

## 7. Amended structural and repeat checks (§8)

- Checks 1–5 are unchanged and still hold by construction: every written order, lenient or fallback,
  is a permutation of the query's pool, so pool invariance and R@50 = 0.9193 are untouched. A
  fallback query counts as unreordered for check 2's ≥ 25% floor; at ≤ 5% fallbacks that floor is
  not in reach of the fallback count.
- **New check 6: fallback count ≤ `floor(0.05 × n)`, and no corrupt resumed entry** — enforced by the script (§3); the verdict doc
  records the count and ids from the sidecar.
- Spec A27's premise ("§6 row 2 forbids a partial run file, so the intersection is all 300") still
  holds: a fallback query is written, so the run file is always complete.
- **Repeat check:** each repeat pass reports its own fallback count. A query that falls back in one
  pass but not the other counts as an ordering difference; the report names how many of the
  differences are fallback-driven.

## 8. Tests affected

- New: the lenient-prefix function (full permutation; prefix exactly at threshold; one below; stop
  at first duplicate; stop at first invented id; `min(20, pool size)` on a 3-document pool); fallback
  under and over the cap on a ≥ 20-query fixture (the existing fixtures have < 20 queries, so their
  cap is 0 and every existing refusal test keeps its meaning); a corrupt resumed entry still refuses
  on a ≥ 20-query fixture whose fallbacks are under the cap (§3); sidecar `fallbackQueryIds`;
  `--concurrency` output identical to sequential; `elapsed_s` and `prefix_len` recorded.
- Changed: every test whose `ScriptedTeacher` queue is sized to two attempts for a failing query —
  under `RETRY_BUDGET` 4 a failing query consumes the next query's replies (e.g.
  `test_length_finish_reason_blames_max_tokens_not_the_prompt`, `:1019-1046`, asserts
  `len(q1_records) == 2`). The implementation plan enumerates them.
- Unchanged: `validate_permutation`'s strict tests (`:46-95`) — the function is unchanged and still
  guards the resume path.

## 9. Out of scope

- **`--enforce-eager`** in the pod's start command (`vllm serve ... --enforce-eager ...`) is the
  likely cause of the ≈ 25 tokens/s per-request rate. Dropping it could shorten the run several-fold,
  but it is untested, and CUDA graphs take GPU memory, shrinking the KV pool that §6's N = 12 is
  computed from. If it is tried, re-read `kv_cache_size_tokens` after launch and recompute N.
- A top-k prompt ("return the 20 most relevant ids"). The lenient rule keeps the measured prompt;
  a new prompt would be untested.
- Everything the base spec's §12 lists.

## 10. Verified assumptions

Verified 2026-09-27 against the repo (`main` at `7cb008ff`) and the v2 artifacts.

| # | Assumption | Evidence |
|---|---|---|
| L1 | `validate_permutation` has exactly two call sites: resume and the attempt loop | `grep -n "validate_permutation(" teacher_rerank.py` → `:433` (resume), `:473` (attempt loop), plus the def at `:171` |
| L2 | A stored repaired order passes the resume re-check | The written order is prefix + remaining pool ids, a permutation of the pool by construction; `:433` validates `order` with `validate_permutation`, which accepts any permutation |
| L3 | `main` is the only place the run file is written or refused | `teacher_rerank.py:637-646` — the `failures` refusal, then `write_run` and `write_sidecar`; no other caller |
| L4 | `load_run` returns each query's ids in A0′ rank order | `load_run` appends in file order (`:92-95`); the A0′ file is rank- and score-sorted within every query — checked all 300: 0 out of order |
| L5 | Existing refusal tests keep their meaning under the cap | Every refusal test uses `make_fixture_files`' small fixture (< 20 queries), so `floor(0.05 × n) = 0` and any failure still refuses — e.g. `:713-738`, `:587-611`, `:679-699` |
| L6 | `RETRY_BUDGET` consumers | `teacher_rerank.py:51` (def), `:420` (docstring), `:442` (loop); `test_teacher_rerank.py:718`, `:791` (comments). Tests with two-attempt reply queues change (§8) |
| L7 | `MAX_COMPLETION_TOKENS` consumers | `:45` (def + comment), `:352` (docstring), `:383` (request body), `:448-449` (length reason), `:549` (sidecar) |
| L8 | The client timeout appears once | `:395` `urlopen(req, timeout=600)`; no test references 600 |
| L9 | `score_query` shares no mutable state across queries | Read `:415-485`: reads `ledger`, `args`, `corpus`; its only side effect is `append_response`; `call_teacher` is a pure HTTP call |
| L10 | `append_response` is the only ledger writer | `grep -n 'open(.*"a"'` → `:268` only; 5 call sites, all in `score_query` (`:453, 460, 468, 475, 481`) |
| L11 | `report.py` reads only `composite` from the sidecar | `report.py:184-202` `load_build_composite` returns `data.get("composite")`; no other key read — `fallbackQueryIds` is inert to it |
| L12 | `report.py --pair`'s pool check tolerates A0′-order queries | `check_pool` (`report.py:732-766`) requires equal doc sets (true for every written order) and ≥ 25% of queries reordered (≥ 95% of queries are teacher-ordered under the cap) |
| L13 | `summarize_teacher_attempts.py` tolerates the new fields | It reads only `status` via `tr.accepted_entry` and `reason` (`:17-29`); lenient acceptances are `status: "accepted"` |
| L14 | Nothing outside these scripts imports `teacher_rerank` | `grep -rl teacher_rerank Iverson.Server` → only `run_measurement_batch.sh`, `stub_vllm_server.py`, `summarize_teacher_attempts.py`, `teacher_rerank.py`, `test_teacher_rerank.py` |
| L15 | The stub's two dry-run modes still pin their numbers | `stub_vllm_server.py` replies with each query's full 50 ids (`:136-145`), a prefix of 50 → written unchanged; fallbacks 0 |
| L16 | Per-request decode ≈ 24.8 tok/s, flat across 1–5 concurrent | Timing fit over all 16 invocations (§5): each end time = `Σ completion_tokens / 24.8 + 600 s × timeouts` from its batch start, within ≈ 8 s |
| L17 | Largest prompt 30,719 tokens; KV pool 655,317 | `build_prompt` over `scifact-run-2026-08-26/beir/corpus.jsonl` for all 300 queries: max 138,174 chars (query 852); local chars equal the pod's for 770/51/783 (105,386 / 90,082 / 83,749); `/metrics` `kv_cache_size_tokens="655317"` |
| L18 | The lenient rule accepts 19/23 real lists and 8/8 queries | Re-applied to the v2 ledgers: prefix ≥ 20 on 19 of 23 lists; every query has ≥ 1 |
| L19 | Threaded appends are safe with a lock | Probe: 12 threads × 150 × 120 KB records, 0 corrupt lines without a lock (twice); the lock covers filesystems the probe did not |
| L20 | Whether vLLM aborts a request on non-streaming client disconnect | **Unverified, and designed around** (§5): the cap/timeout pair ends generation server-side first |
| L21 | `score_query` has exactly two producers of an unscored query, indistinguishable to `main` today | Read `teacher_rerank.py:433-435` (resume re-check failure) and `:485` (budget exhausted): both return `(None, str)`; the only caller is `main` at `:631` (`command grep -n "score_query("` over `scripts/`) |
| L22 | Every real pool has exactly 50 ids, so §2's threshold is always 20 | `load_run` over all 300 queries of the A0′ run: pool sizes `{50}` (CDR-1 U3, re-run) |
