# Critical Design Review: 2026-09-27-teacher-ceiling-lenient-acceptance-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-09-27-teacher-ceiling-lenient-acceptance-design.md`
**Artifact HEAD at review:** 02dc6236074694c4e4e5e847477c99b19e699e18
**Verified Assumptions section:** present (§10, L1–L20)

Round 1: no prior reviews match `2026-09-27-teacher-ceiling-lenient-acceptance-design-critical-review-*.md`. The amendment-hunk family (c) is skipped because this is round 1. The anchor was checked: `git ls-files` tracks the spec, `git status --porcelain` is empty, and `git rev-parse HEAD:<spec>` = `git hash-object <spec>` = `54297e7a…`.

Rulings not reopened as design alternatives: lenient acceptance with a valid prefix of at least `min(20, pool size)`, per-query fallback to A0′ order, the `floor(0.05 × n)` cap, and `RETRY_BUDGET` 4.

Probe scripts are in the session scratchpad: `cdr_probe.py`, `cdr_producers.py` and `cdr_compat.py`. Their outputs are quoted inline. The teacher suite at HEAD is green: `python3 -m pytest …/test_teacher_rerank.py -q` gives `44 passed`.

## 0. Coverage enumeration

### Sections

| # | Section | Disposition |
|---|---|---|
| S0 | Header (Amends / Source data / Rulings) | ok, `[existence]`. `md5sum prefix-check-output.txt` returned `7631d1f4e3d8c672eb69bb2a3806d95f`, which matches the pinned md5. `ls artifacts-v2/` shows 8 `*.responses.jsonl`, 16 `*.pass{1,2}.log` and `batch.log`. |
| S1 | §1 Why (the v2 numbers) | ok, `[totality]`. I re-read `prefix-check-output.txt`, which covers all 62 ledger entries. Kinds: `timeout 7, 401 32, list 23`. `>=20: 19`. `any out-of-pool: 0`. `any duplicate: 1` (q108's 39-id list, uniq 38, prefix 38). `2 of 29 in-pool relevant` dropped, both in q873's 35-id list. Mean delta `+0.1737`. "Repaired as in §2" is inexact: `prefix_check.py`'s `repair` also keeps valid ids after the break and averages the 4 sub-20 lists. But with 0 out-of-pool ids and the only duplicate at the last position, `repair` equals §2's prefix + A0′ on all 23 lists. Dropping the 4 sub-threshold lists changes only q783 (0.448 → 0.454), which moves the mean by +0.0008. The spec labels this number "not a gate result", so it is not load-bearing. |
| S2 | §2 Lenient acceptance | Rules R1–R3 and R8 cover it. The ledger-shape arrow is A1. |
| S3 | §3 Per-query fallback and 5% cap | Rules R4–R5. R5 producer (a) → §3.1. |
| S3a | §3 "Why this does not reopen `TeiRerankClient`'s objection" (dry-run gate claim) | dropped. The claim that the identity pair "move[s] if even one query falls back" is false. The identity stub answers in input-run order (`stub_vllm_server.py:56-82`, `order="input-run"` keeps file order). A fallback writes `pool[q]`, which is also file order (`teacher_rerank.py:75-98`, L4). So an identity-mode fallback is byte-identical to an accepted identity reply. The reversed pair does move. The spec also requires "both dry runs must now also print `0 fallbacks`", which catches the case the identity pair misses. The gate as specified still detects a discarded reply, so this fails the literal-wrongness test. |
| S3b | §3 Bias bound | ok, `[totality]` over all 30 real attempts (`cdr_probe.py`). Accepted under §2: 19. q = 0.3667, q⁴ = 0.0181, so 300 × 0.0181 = 5.4 expected fallbacks, sd 2.31, and a cap of 15 sits ≈ 4.1 sd above the mean. Per-query outcome strings: 770 AAAA, 521 AArr, 1332 AA, 783 rAAA, 873 rAAr, 1370 ArAr, 759 rrrA, 51 AArA. Unbiased E[p²] = 0.125 against 0.134 under independence, and E[p³] = 0.031 against 0.049, so the data show no overdispersion. The worst-case `fallbacks/n` bound follows from \|d_q\| ≤ 1. |
| S4 | §4 `RETRY_BUDGET` 4 | ok. Not load-bearing: the budget is a ruling. q² = 0.1344 (the spec's 13.7% comes from rounding q to 0.37). Either value is ≈ 40–41 of 300, above the cap. |
| S5 | §5 Completion cap and client timeout | ok, `[existence]` for the arithmetic, which rests on L16 (§1). 16,384/24.8 = 660.6 s, below 1,200 s. 16,384/1,200 = 13.65 tok/s. 600 × 24.8 = 14,880. The largest list is 9,504 tokens (q873, `prefix-check-output.txt`). The length-reason text keeps `max_tokens` and `max_model_len`, which the assertions at `test_teacher_rerank.py:1036-1041` require. The rate at N = 12 is handled at U4. |
| S6 | §6 Concurrency | ok, `[existence]` for the shared-state claim: a read of `:415-485` finds reads of `ledger`, `args` and `corpus` only, and the single side effect `append_response` (L10). ok, `[totality]` for the runtime inputs: `cdr_probe.py` over all 30 attempts gives E[attempts \| cap 4] = 1.55 and a mean of 291.0 s per attempt, so 1.55 × 291 ≈ 451 s. 400 × 453/3600 = 50.3 h, and ÷ 12 = 4.2 h. ok, `[totality]` for the KV row: `cdr_probe.py` runs `build_prompt` over all 300 queries and finds the largest at 138,174 chars (q852), or 30,719 tokens at 4.498 chars/token, against the spec's 30,716. 90% × 655,317 / 47,103 = 12.52, so N is still 12. The rate at N = 12 is covered by U4. A candidate that the "`--concurrency` output identical to sequential" test needs a query-keyed fake because `ScriptedTeacher` pops FIFO (`:306-315`) was dropped: it is an implementation-time concern and belongs to CIR. |
| S7 | §7 Amended checks | ok, `[existence]`. A read of `check_pool` (`report.py:732-766`) shows it tests per-query set equality (`:750`) and the fraction reordered (`:751-752`). A fallback's `pool[q]` has the same set and counts as not reordered, so at most 5% of queries fall back, well inside the 75% allowed to be unreordered. A27 holds because fallbacks are written. |
| S8 | §8 Tests affected | ok, `[totality]` over every `ScriptedTeacher` reply in the suite. `command grep -n "ScriptedTeacher(\["` gives 17 sites; the non-empty queues are at `:394, 413, 596, 639, 687, 721, 806, 1024, 1059, 1091`, and their replies are listed at `:395-1093`. Every `"stop"` list reply is an exact permutation of the pool of the query it was written for. The rest are `http_error`, `None`, invalid JSON or `length`. So no reply flips from reject to accept under the lenient rule when its own query consumes it. The cross-query consumption under budget 4 is the class §8 already names. `command grep -c "pytest.raises(SystemExit)"` gives 11, and every one uses `make_fixture_files` (2 queries, so the cap is 0) or reads the ledger directly (`:988`). |
| S9 | §9 Out of scope | ok. It defines no mechanics. |
| S10 | §10 Verified assumptions | See §1. |

### Rules and operands

| # | Rule | Disposition |
|---|---|---|
| R1 | Longest valid prefix. Operands: reply elements after `str()` against pool ids from `load_run` (whitespace-split str). | over: ok, `[existence]`. The rule text (§2 item 1) stops at the first element that is out of the pool or repeated, so no invented or duplicated id can enter the prefix. §8 tests both stops. / under: ok, `[totality]`. The pool side is clean: base-spec A28 found every id is pure digits with no leading zeros, and `str(int)` reproduces it. On the reply side, all 23 real lists had 0 out-of-pool ids (`prefix-check-output.txt`: `any out-of-pool: 0`). |
| R2 | Accept if the prefix is at least `min(20, len(expected_ids))`. | over: ok, `[totality]`. `cdr_probe.py` finds pool sizes `{50}` across all 300 queries, so the real threshold is always 20. / under: ok, `[totality]`. Every 3-id `"stop"` permutation reply in the suite is still accepted, because 3-document pools need the full permutation (S8). |
| R3 | Written order is the prefix plus the remaining pool ids in A0′ order. Operand: "A0′ order" = `pool[query_id]`. | over: ok, `[totality]`. `cdr_probe.py` over all 300 queries: `rank-out-of-order 0 score-out-of-order 0`, so no id is placed above a higher-ranked A0′ id among the remainder. / under: ok, `[totality]`. The same probe gives `load_run order == file-rank order: True`, so no query's remainder is left unordered. |
| R4 | Cap = `floor(0.05 × n)` with n = `len(pool_query_order)`. | over: ok, `[existence]`. A read of `:615-619` shows n includes queries accepted on resume, which matches "number of queries in the invocation". / under: ok, `[totality]`. n < 20 gives a cap of 0, and every one of the 11 current refusal tests uses a 2-query fixture (S8). |
| R5 | Eligibility predicate "fallback" (§3: "After `RETRY_BUDGET` rejected attempts … decided at write time from the ledger's all-rejected state"). The producers of an unscored query (`order is None`) are the two `score_query` return sites, `:435` and `:485`, found by reading `:415-485`. | producer (b), `:485` budget exhausted. over: ok, `[existence]`. It is reached only after `RETRY_BUDGET` rejected appends (`:442-483`). / under: ok, `[existence]`. Every exhausted query returns `(None, last_reason)` here (`cdr_producers.py`: `exhausted: (None, 'invalid JSON: …')`). / producer (a), `:435` resume re-validation failure: → §3.1. |
| R6 | Eligibility predicate `status == "accepted"`. | over: ok, `[negative]`. `command grep -n '"accepted"' teacher_rerank.py` finds `:259` (reader) and `:476` (the single writer, whose `result` is validated). / under: ok, `[existence]`. The consumers, `accepted_entry` (`:258-260`, used by resume at `:431` and by `summarize_teacher_attempts.py:23`), match `status` only, which the lenient branch still writes as `"accepted"`. |
| R7 | Identity: per-query keys under concurrency. | over: ok, `[existence]`. The ledger is keyed by `str(query_id)` (`:248`, `:275`), and §6 collects results by query id, so two distinct queries cannot share a slot. / under: ok, `[existence]`. `write_run` iterates `pool_query_order` (`:499`), so no query is left unwritten because of completion order. |
| R8 | Order of checks: `finish_reason` before the parse and prefix rule. | over: ok, `[existence]`. `length` and `http_error` are rejected before parsing (`:446-463`), so a truncated array with 20+ valid ids is never accepted. / under: ok, `[existence]`. §2 says "parsed as today", so no stop reply loses a path it has now. |

### Data-flow arrows

| # | Arrow (ending at an operation) | Disposition |
|---|---|---|
| A1 | Lenient written order → `append_response` → **persist** (JSONL) → `read_responses_ledger` → `score_query` resume → `validate_permutation(accepted["order"], expected_ids)` at `:433` | ok, `[compat]`. `cdr_compat.py` takes every real v2 reply that passes the §2 threshold and applies the §2 rule to build its written order. It builds the record with `make_record` plus `prefix_len` and `elapsed_s`, persists it with the real `append_response`, reads it back with the real `read_responses_ledger`, and feeds it to the real `score_query` resume path. Result: `19 / 19` return the written order with no reason. |
| A2 | Accepted order or fallback `pool[q]` → `scored_pool` → `write_run` (`:644`) | ok, `[existence]`. `write_run` needs `scored_pool[q]` for every q in `pool_query_order` (`:499-500`). A fallback supplies `pool[q]`, so the file is complete. |
| A3 | Fallback ids → `write_sidecar` → **persist** `.meta.json` → `report.py` | ok, `[negative]`. `command grep -n 'sidecar_path_for(\|meta.json\|json.load(' report.py` finds the `.meta.json` path opened only at `:194-199` (`load_build_composite`, returning `data.get("composite")` at `:202`). `:213` derives the diversity sidecar path (`.diversity.json`, loaded at `:230`), and `:399` loads the `--stats` file. |
| A4 | Fallback count → stdout → dry-run gate check for `0 fallbacks` | ok, `[existence]`. §3 mandates the print. S3a explains why that print, not the identity pair, is what carries the gate. |
| A5 | `elapsed_s` → **persist** ledger → operator rate (`completion_tokens / elapsed_s`) | ok, `[presence]`, stratified by state. `cdr_compat.py` dumps the key set of all 62 v2 records: one shape, `('completion_tokens', 'content', 'order', 'pass', 'prompt_tokens', 'query_id', 'reason', 'reasoning', 'status')`. Among the 23 records that reached the usage-populating state (non-`http_error`), 23 have a non-None `completion_tokens`. The 39 `http_error` records carry None by design (`:399-401`). No code reads either field. |
| A6 | `prefix_len` → **persist** ledger | ok, `[existence]`. There is no reader, and `read_responses_ledger` requires only `query_id` (`:246`). The A1 round trip carried `prefix_len` without error. |
| A7 | Ledger → `summarize_teacher_attempts.summarize_ledger` | ok, `[existence]`. It reads only `accepted_entry` and `reason` (`:20-28`). |
| A8 | Written run → `report.py --pair` → `check_pool` | ok, `[existence]`. See S7. |
| A9 | Repeat passes → `report.py --baseline` | ok, `[existence]`. Base-spec A33: `check_pool` is called only inside `run_pair_statistics`, so no floor applies. |
| A10 | Stub → dry run → plain `report.py --run` | ok, `[existence]`. The stub replies with full 50-id lists (`stub_vllm_server.py:143-145`), so the prefix is 50, the written order is unchanged, and there are 0 fallbacks (L15). |

## 1. Verified-assumptions cross-check

| # | Status | Fresh-read evidence |
|---|---|---|
| L1 | still holds | `command grep -n "validate_permutation("` finds `:171` def, `:433`, `:473`. |
| L2 | still holds | See A1: 19/19 real written orders pass `:433`. |
| L3 | still holds | `write_run` and `write_sidecar` are called only at `:644-645`. Every other hit is a test or a different script's own function. |
| L4 | still holds | `cdr_probe.py` over 300 queries: 0 out of order. |
| L5 | still holds | All 11 `SystemExit` sites use the 2-query fixture or a direct ledger read (S8). |
| L6 | still holds | Grep finds `teacher_rerank.py:51, 420, 442` and `test_teacher_rerank.py:718, 791`. |
| L7 | still holds | Grep finds `:45, 352, 383, 448-449, 549`. |
| L8 | still holds | `:395` is the only `timeout=600`. The test hits are `thread.join(timeout=5)` and a `timeout=None` mock parameter. |
| L9 | still holds | Read `:415-485`. |
| L10 | still holds | The only append-mode `open(...)` is `:268`. The call sites are `:453, 460, 468, 475, 481`. |
| L11 | still holds | See A3. |
| L12 | still holds | `report.py:732-766`, with `POOL_MIN_REORDERED_FRACTION = 0.25` at `:700`. |
| L13 | still holds | `summarize_teacher_attempts.py:16-29`. |
| L14 | still holds | `command grep -rl teacher_rerank Iverson.Server` finds the 5 named files plus `__pycache__`. The single `import` is `summarize_teacher_attempts.py:13`. |
| L15 | still holds | `stub_vllm_server.py:143-145`. |
| L16 | still holds (sanity check on 7 of 16 end times) | Start 16:12:13 from q104 p1 (5,830 tok → 16:16:08 ✓) and q103 p1 (9,727 → 16:18:45 ✓). q108 p1 predicts 16:31:44 against 16:31:43. q102 p2 predicts 16:50:12 against 16:50:12. q101 p2 predicts 16:55:00 against 16:55:00. q107 p2 predicts 17:09:16.6 against 17:09:14. q108 p2 predicts 17:14:43 against 17:14:43. |
| L17 | still holds, with an arithmetic slip | `cdr_probe.py`: max 138,174 chars (q852). 770/51/783 are 105,386 / 90,082 / 83,749 chars. 138,174/4.498 = **30,719**, not 30,716. N is still ⌊12.52⌋ = 12. |
| L18 | still holds | `cdr_probe.py` gives `lists 23 accepted under §2 19`, and every query has at least one accepted list. |
| L19 | dropped | Not load-bearing. The design adds the lock whatever the probe showed, and an in-process `threading.Lock` serialises every `append_response` write, independent of the filesystem's append semantics. Not re-run. |
| L20 | dropped | The spec does not depend on vLLM's disconnect behaviour; it designs around it. The residual dependency, that the rate stays ≥ 13.7 tok/s, is U4. |

**Span check** (design dependencies that no listed item covers):

- **U1.** Which unscored-query producers become fallbacks. Not covered: L9 and L10 cover shared state, not how `order is None` is routed. → §3.1.
- **U2.** Whether the identity dry-run pair can see a fallback. Not covered. Verified false by reading the code (S3a). Dropped: the mandated `0 fallbacks` print covers it, so there is no behavioural gap.
- **U3.** Every real pool is exactly 50, so the threshold is 20. Not covered by an L-row. Verified in-round, `[totality]`: `cdr_probe.py` gives pool sizes `{50}` over all 300 queries.
- **U4.** The per-request decode rate at N = 12 must stay ≥ 13.7 tok/s for §5's "server ends first" guarantee. Not covered by a verified row; L16 measures only 1–5 concurrent. Dropped as a §3 candidate because the spec has already made the verify/accept/defer choice. It accepts the risk in writing (§6: "Speed is only measured to 5–6 concurrent; 12 is an extrapolation on speed", §5: "fires first only if the per-request rate falls below ≈ 13.7 tokens/s") and adds `elapsed_s` so the rate can be measured at execution time. A §3 item would ask again for a choice the spec has recorded.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

### 3.1 Does a resumed accepted entry that fails re-validation become a fallback or a hard refusal?

**The choice.** `score_query` has two producers of an unscored query:

- (a) The resume path's re-validation failure, `:433-435`: `"resumed ledger entry is not a permutation of this query's pool: …"`.
- (b) Budget exhaustion, `:485`.

`cdr_producers.py` runs the real `score_query` on both and shows they return the identical `(None, <str>)` shape:

```
resume-corrupt: (None, "resumed ledger entry is not a permutation of this query's pool: …")
exhausted:      (None, 'invalid JSON: …')
```

`main` merges both into one `failures` list (`:631-633`). The spec routes (b) explicitly. It says two contradictory things about (a):

- §3's definition covers only (b): "After `RETRY_BUDGET` rejected attempts, a query is a **fallback** … decided at write time from the ledger's all-rejected state". A query that reaches (a) has an *accepted* entry and made zero attempts in this invocation.
- §3's refusal wording covers both: "every failed query is listed with its last reason — **today's refusal, now triggered by the cap instead of by the first failure**". Today's refusal (`:637-642`) is exactly the `failures` list that includes (a).

**Why it's forced.** The implementation has to route `order is None` one way or the other in `main`, and the spec's text supports both routes. No existing test decides it. The only test of producer (a), `test_main_rejects_a_resumed_order_that_is_not_a_permutation_of_the_pool` (`:948-974`), uses the 2-query fixture, where the cap is 0. It exits non-zero whether (a) is treated as a hard refusal or as a fallback over a cap of 0. So the plan cannot take the answer from the suite, and at n ≥ 20 the two readings produce different run files.

**Options.**

- **A. Hard refusal regardless of cap.** A resume re-validation failure is never a fallback. `main` refuses with no run file and no sidecar, as today, even when the fallback count is under the cap. This keeps the base spec's "a hand-edited or otherwise corrupt entry cannot reach `write_run` unchecked" doctrine (`:424-428`) as fail-loud at every n. It needs `score_query` to signal which producer it hit, because the `(None, str)` return cannot say so today.
  Evidence: `[existence]` The `cdr_producers.py` run above shows identical return shapes from the real `score_query`. A read of `:626-642` shows `main` receives only `(order, reason)`.

- **B. Counts as a fallback toward the cap.** The query is written in A0′ order, listed in `fallbackQueryIds`, and counted in `floor(0.05 × n)`. This follows the literal "now triggered by the cap" wording, and `main`'s single `failures` path stays as it is. The cost: the ledger holds an accepted teacher answer for that query, and the run file silently uses A0′ order instead. The count is visible, but the reason is not unless the operator reads the per-id reason.
  Evidence: `[existence]` A read of `:631-635` shows that treating every `order is None` as a fallback needs no new signal. `[negative]` That a corrupt accepted entry can only come from a manual edit or a bug: `command grep -n '"accepted"' teacher_rerank.py` finds the single writer at `:476`, which stores a validated `result`. `validate_ledger_pass` (`:306-333`) refuses records stamped by another pass.

- **C. Treat the corrupt entry as absent and re-attempt with a fresh budget.** The query is re-issued to the model as if it had no accepted entry. This spends money on a query the ledger says was answered, and it hides the corruption unless it is logged. It also changes the existing test: under C, `ScriptedTeacher([])` at `:964` is popped on a re-attempt and raises `IndexError` instead of the expected `SystemExit`.
  Evidence: `[existence]` A read of `ScriptedTeacher.__call__` (`:311-315`, `self.replies.pop(0)` on an empty list) and of the test's queue at `:964`.

**Why no option dominates.** A is the only one that keeps fail-loud behaviour for corrupt data at every n, but it can refuse a run whose teacher work is otherwise complete. B keeps that run and costs visibility. C keeps the run and costs spend. These trade the same three quantities (visibility, completeness, spend) in different directions, and no mix of them gets both fail-loud behaviour and a written run for the same corrupt entry.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3.1 needs a ruling on how the resume re-validation producer is routed. §2 is empty. With 3.1 resolved in the spec, the design is ready for implementation planning.
