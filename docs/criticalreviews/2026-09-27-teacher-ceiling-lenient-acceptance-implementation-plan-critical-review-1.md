# Critical Implementation Review: 2026-09-27-teacher-ceiling-lenient-acceptance-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-27-teacher-ceiling-lenient-acceptance-implementation-plan.md
**Artifact HEAD at review:** 640f4834228cb35f4bec24f028e3760230d9d3c4
**Verified plan-level assumptions section:** present

⚠️ 1 commits since plan-write time (SHA f2e52130); cited file:line references re-checked under §1. (The one commit is `640f4834`, the plan's own; no code or runbook file changed.)

Method: all seven ```` ```diff ```` / ````` ````diff ````` blocks were extracted and `git apply`-ed in order to a detached worktree of `640f4834` (scratchpad `wt/`), with the suite run after every block; the Task 3 Step 4 stub dry run was run in both modes; nine source mutations were run against the final suite; the final `teacher_order` was run over every real reply in the v2 ledgers.

## 0. Coverage enumeration

**Task 1 — lenient acceptance, budget, cap, timeout, ledger timing**

- T1 step prose (8 `teacher_order` tests, one five-site test, 4 re-queued tests), population `command grep -n 'ScriptedTeacher(\['` → 17 sites, 7 empty, 10 non-empty: over (a test re-queued that did not need it): [totality] ok — each of the four re-queued (`:596, :687, :721, :1024`) was read and queues exactly two failing replies for one query / under (a two-failure queue missed): [totality] ok — each of the other six (`:394, :413, :639, :806, :1059, :1091`) was read and queues only accepted replies. The 7 empty sites (`:764, :869, :902, :934, :964, :1002, :1070`): over: dropped — an empty queue holds no failing reply to re-queue / under: n/a.
- T1 test diff: [compat] ok — `git apply block0.diff` clean; suite `13 failed, 40 passed`, as the plan states.
- T1 source diff: [compat] ok — `git apply block1.diff` clean; suite `53 passed`.
- T1 commands (pytest, `git add` two files, commit): ok — pytest form ran from repo root; the main checkout's unrelated `docs/security/tma.md` is unstaged, so the plain `git commit` after the two-file `git add` cannot sweep it in.
- T1 dynamic — `monkeypatch.setattr(tr, "time", Namespace(...))`: [negative] ok — `command grep -n '\btime\.\|import time'` on the applied `teacher_rerank.py` → `:41` import, `:494` and `:498` `time.monotonic()` only; after Task 3 the test runs through `ThreadPoolExecutor(max_workers=1)`, so the 12 ticks are still consumed in call order; passes in the final `58 passed`.
- T1 dynamic — `elapsed_s`/`prefix_len` reach the persisted ledger: [presence] ok — first record of the real dry-run ledger (`dry-reversed.jsonl`) has keys `['completion_tokens', 'content', 'elapsed_s', 'order', 'pass', 'prefix_len', 'prompt_tokens', 'query_id', 'reason', 'reasoning', 'status']`.
- T1 dynamic — length reason keeps `max_tokens` / `max_model_len` wording (spec §5): [compat] ok — `test_length_finish_reason_blames_max_tokens_not_the_prompt` passes on the final code.

**Task 2 — fallback, cap, corrupt-resume refusal**

- T2 step prose: [totality] ok — 20-query fixture is the smallest n with cap 1 (`20 // 20 = 1`, `19 // 20 = 0`).
- T2 test diff: [compat] ok — applied clean; `4 failed, 52 passed`.
- T2 source diff: [compat] ok — applied clean; `56 passed`.
- T2 commands: ok — same shape as Task 1's.
- T2 dynamic — fallback written in A0′ order: [compat] ok — `list(pool[query_id])`, and `load_run` keeps file (rank) order (L4); mutation "fallback written reversed" → `1 failed`.
- T2 dynamic — refusal leaves neither run file nor sidecar: [absence] ok — `sys.exit` precedes `write_run`/`write_sidecar`; the over-cap test (passing) asserts both paths absent, and a real 1-query refused run (C6) left only `smoke2.responses.jsonl` (`ls smoke2.*`; no `.chunks.trec`, no `.meta.json`).

**Task 3 — `--concurrency N`**

- T3 step prose (KeyedTeacher keyed on `Query: <text>\n`; barrier proves overlap), matching rule over the 20 fixture prompts: over (a key matches another query's prompt): [compat] ok — `PROMPT_INSTRUCTIONS` renders `"Query: {query}\n\n"` (`teacher_rerank.py:64`), so `"Query: query 1\n"` is not a substring of query 10–20's `"Query: query 1N\n"`; the equivalence test compares the whole run file and ledger, which a wrong reply would change / under (a query's own key fails to match): [compat] ok — an unmatched prompt raises `AssertionError("no reply scripted for this prompt")`, and both Task 3 tests pass through the real `main`, with `teacher.calls == 19 + tr.RETRY_BUDGET` asserted. `threading` and `Namespace` are already imported by the test file (`:22`, `:24`).
- T3 test diff: [compat] ok — applied clean; `2 failed, 56 passed`.
- T3 source diff: [compat] ok — applied clean; `58 passed`.
- T3 dynamic — exceptions inside a worker: ok — [negative] `command grep -n "sys.exit\|^def "` on the applied script puts every `sys.exit` inside `load_run`, `read_responses_ledger`, `validate_ledger_pass`, `validate_inputs_before_any_model_call` or `main`, none of which is in `score_query`'s call graph (`pass_identity`, `accepted_entry`, `validate_permutation`, `shuffled_doc_order`, `build_prompt`, `call_teacher`, `parse_json_array`, `teacher_order`, `append_response`, `make_record`). So no `SystemExit` is swallowed in a thread; any other exception is re-raised by `list(executor.map(...))`, as the sequential loop raised it.
- T3 dynamic — ledger writes under 12 threads: ok — [negative] `command grep -n 'open('` on the applied script → the only append-mode open is `:313` `open(path, "a")`, the others being read-mode (`:92`, `:115`, `:270`), `urlopen` (`:443`) and the run/sidecar writers (`:553`, `:616`), which run after the pool. `command grep -n _LEDGER_LOCK` → `:302` definition and `:312` `with _LEDGER_LOCK:` directly around `:313`, so one lock-holder writes at a time, so a SIGTERM mid-append can only truncate the file's last line, which the existing playbook row (`malformed ledger line` → delete the last line) already covers.
- T3 dynamic — runbook's `pkill` then `--concurrency 6` relaunch leaves up to 12 server-side generations running (L20 unverified): dropped — even if vLLM never aborts them, each orphan ends at its `max_tokens`. [existence] `MAX_COMPLETION_TOKENS = 16384` is in the applied source, and vLLM honouring `max_tokens` was observed live (spec §1: raising the budget "removed `finish_reason=length`"). The client's two endings of a runaway, `length` and `http_error`, both append a `rejected` record and `continue` (applied `score_query` `:499-518`). So the effect is a bounded slowdown of new requests, not a failure to produce the run file.
- T3 Step 4 dry run (both stub modes, `--concurrency 4`): [compat] ok — ran with the plan's inputs: identity `(300 queries, 0 fallbacks)`, `build 31583db5aea49136`, `nDCG@10 0.6980`, `R@50 0.9193`; reversed `(300 queries, 0 fallbacks)`, `0.0032`, `0.9193`.
- T3 commit: ok.

**Task 4 — runbook**

- T4 diff: [compat] ok — `git apply block6.diff` clean onto the tracked runbook.
- T4 Step 2 command: ok — `command grep -c "8192\|five"` on the applied runbook prints `0`.
- T4 one-liners: [compat] ok — check-6 one-liner run verbatim against the real dry-run sidecar (copied to `$A/teacher-ceiling.meta.json`) prints `[]`; decode-rate one-liner run verbatim against a real ledger written by the applied script (a 3-query `--concurrency 3` run against a local server that returns `usage.completion_tokens: 4960`) prints one row per record, e.g. `936 4960 0.52 9538.5`; against a two-record hand sample it prints `q7 4960 200.0 24.8` and skips the `completion_tokens: null` record.
- T4 commit (`git add -f` + `git commit … -- <file>`): ok — pathspec-scoped.
- T4 runbook restatements of values the amendment replaced (the rule "the runbook matches the amended spec" × every passage of the applied runbook): over: [totality] ok — every cell flagged cites the spec section that amends it (matrix under §2.1) / under: → §2.1 (two passages missed).

**Cross-task and persistence-boundary contracts**

- C1 Task 2 consumes Task 1's `return order, None` / `RETRY_BUDGET` context: ok — block3 applied clean onto block1's output.
- C2 Task 3 consumes Task 2's 3-tuple and `make_many_fixture_files`/`reversed_reply`/`FAILED_REPLY`: ok — block4/block5 applied clean; `58 passed`.
- C3 (persistence) ledger record → runbook step 6 decode-rate one-liner (reads `query_id`, `completion_tokens`, `elapsed_s`): [presence] ok — the dry-run ledger's key dump above has `elapsed_s`; the real v2 ledger records have `completion_tokens` (key dump: `['completion_tokens', 'content', 'order', 'pass', 'prompt_tokens', 'query_id', 'reason', 'reasoning', 'status']`).
- C4 (persistence) sidecar → runbook check-6 one-liner (reads `fallbackQueryIds`): [presence] ok — dry-run sidecar keys `['composite', 'configLabel', 'fallbackQueryIds', 'recordedAtUtc', 'reranker']`, value `[]`.
- C5 stdout `wrote … (<n> queries, <k> fallbacks)` → runbook steps 1 and 6, and `run_measurement_batch.sh:88`'s `tail -1`: [compat] ok — dry runs print `(300 queries, 0 fallbacks)`; a 1-query run prints `(1 queries, 0 fallbacks)` as its last line (cap 0, so no `fallback` line can follow).
- C6 refusal message → playbook rows (a pattern match from message to row): over (a refusal matches the wrong row): [bidirectional] ok — of the 16 playbook rows (`:427-442`), `command grep -n unscored` hits only `:429`; the 14 old rows key on other text (`finish_reason=length`, `timed out`, HTTP 400, ledger/ARM/BUILD messages, ssh errors). The two new rows are disjoint on the corrupt count: the cap row's pattern reads `(0 corrupt resumed entries; …)` and the corrupt row's `(N corrupt resumed entries; …)` with N > 0, so a refusal with both corrupt entries and over-cap fallbacks matches only the corrupt row, which is the row that must win (spec §3) / under (a refusal matches no row): [compat] ok — a 1-query run against a dead port prints `1 / 1 queries unscored -- no run file written (0 corrupt resumed entries; 1 fallbacks against a cap of 0)`, which the cap row's pattern matches. [negative] `command grep -n "queries unscored"` on the applied script hits only `:714`, so the message format string (`{len(corrupt)} corrupt resumed entries; {len(fallbacks)} fallbacks against a cap of {cap}`) is the only unscored-query refusal in the applied script (`:713`, the one `sys.exit` in `main` after scoring).
- C7 (persistence) earlier-invocation ledger records (with or without the new fields) → `score_query` resume: [negative] ok — `command grep -n "elapsed_s\|prefix_len"` on the applied script hits only `make_record` (`:319-331`), the attempt loop (`:498-538`) and a docstring (`:229`); the resume branch reads only `status` (via `accepted_entry`) and `order`.
- C8 `fallbackQueryIds` → `report.py`: [negative] ok — inherited L11 (trusted per the plan's inherited list); `command grep -n "elapsed_s\|prefix_len\|fallbackQueryIds"` on `report.py` hits only its unrelated sidecar `elapsed_seconds`.
- C9 new ledger fields → `summarize_teacher_attempts.py`: [negative] ok — inherited L13; `command grep -n "elapsed_s\|prefix_len"` on it → no hit.

**Rule-like content**

- R1 `teacher_order` acceptance (longest valid prefix ≥ `min(20, pool)`), run over all 23 real v2 lists through `parse_json_array` → `teacher_order` against the real A0′ pools: over: [totality] ok — the 4 rejected are the collapses (prefix 4, 4, 5, 6); the one list with a trailing duplicate (query 51, 39 ids) is accepted at prefix 38, as spec §2 rule 1 prescribes / under: [totality] ok — every list with a ≥ 20 prefix is accepted: `lists 23 accepted 19 queries with >=1 8 / 8`.
- R2 corrupt-vs-fallback classification: population = the four returns of the patched `score_query` (`awk '/^def score_query/,/^def write_run/' | command grep -n return`). Cell 1, resume re-check failure `return None, …, True`: over (misclassed as a fallback): [compat] ok — mutation `elif corrupt_resume:` → `elif False:` gives `1 failed` / under: [negative] ok — the four-return grep shows it is the only `True` return. Cell 2, resume re-check pass `return result, None, False`: over (a scored return classed corrupt): [negative] ok — its third value is the literal `False` / under (a scored return treated as unscored): [compat] ok — `main` tests `order is not None` before `corrupt_resume`, and the resume tests (e.g. `:806`, `scripted.calls == 1`) pass with the resumed query written. Cell 3, accepted reply `return order, None, False`: over: [negative] ok — third value literal `False` / under: [compat] ok — the fallback test's teacher-ordered `q8` is written as `["k8-3", "k8-2", "k8-1"]`, passing. Cell 4, budget exhausted `return None, last_reason, False`: over (misclassed as corrupt): [negative] ok — its third value is the literal `False`, and the grep shows no other `True` return / under (dropped rather than counted a fallback): [compat] ok — the fallback test (`q7` exhausts its budget) writes `q7` in A0′ order and lists it in `fallbackQueryIds`, passing.
- R3 cap `n // 20`: over (a cap above `floor(0.05 × n)`): [compat] ok — mutation `n // 10` gives `2 failed`; `n // 20` equals `floor(0.05 × n)` exactly in integers for every n / under (a cap below it): [totality] ok — `n // 20` is exact for every integer n by definition of floor division, so nothing falls below. The mutation `(n + 1) // 20` survives (`58 passed`), but at the only real n (1, 50, 300) it gives 0, 2, 15, the same as `n // 20`, so no real input separates the two. It is a test gap, not a wrong cap.
- R4 `teacher_order` stop-at-first-bad-element (discard everything after): over (keeps ids after a duplicate): [compat] ok — mutation (`continue` in place of `break` at the duplicate) gives `2 failed` / under (stops before a bad element): [totality] ok — R1's run over all 23 real lists: every stop is at a collapse end, or at the one real duplicate (query 51).

## 1. Verified-plan-assumptions cross-check

1. Still holds — ran at `640f4834` (code identical to `f2e52130`): `44 passed`.
2. Still holds — `command grep -n 'ScriptedTeacher(\['` → 17 sites; non-empty queues at `:394, 413, 596, 639, 687, 721, 806, 1024, 1059, 1091`; the six not re-queued each queue only accepted replies (read).
3. Still holds — `"1 / 2"` at `:610` and `:735`; both tests pass after Task 2.
4. Still holds — `write_sidecar(` callers: `:645` and tests `:255, 264, 273, 286`; all pass after Task 2.
5. Still holds — `score_query(` in `scripts/`: def `:415` and `:631` only.
6. Still holds — `pass_identity` (`:298-303`) returns `run`, `shuffleSeed`, `subsample`, `subsampleSeed` only.
7. Still holds — `:64` `"Query: {query}\n\n"`.
8. Still holds — the elapsed-time test passes and the rest of the suite is unaffected (`58 passed`).
9. Still holds — reproduced exactly: `13 failed, 40 passed` → `53 passed`; `4 failed, 52 passed` → `56 passed`; `2 failed, 56 passed` → `58 passed`.
10. Still holds — all seven named mutations re-run on the final code, each killed: keeping ids after a duplicate → `2 failed` (R4); threshold without `min` → `17 failed`; `elapsed_s` dropped from the `http_error` site → `1 failed`; cap `n // 10` → `2 failed` (R3); corrupt entry counted as a fallback → `1 failed` (R2); `--concurrency` ignored (`max_workers=1`) → `1 failed`; fallback written reversed → `1 failed` (T2). An extra mutation, `prefix_len` dropped from the rejected-reply site, → `1 failed`.
11. Still holds — reproduced (T3 Step 4 row).
12. Still holds — `run_measurement_batch.sh:88` `tail -1`; a 1-query run's last line is the `wrote` line (C5).
13. Still holds as scoped — anchors apply uniquely and the grep prints `0`. Its scope is Task 4's own anchors and two tokens; see the span check.
14. Still holds — both one-liners re-run (T4 row).
15. Still holds — `git log --format=%s -8`: lowercase imperative subjects without prefixes.

Span check: one uncovered dependency — no row covers the runbook passages Task 4 does **not** edit that restate a behaviour or value the amendment replaced (row 13 covers only the edited anchors and the tokens `8192`/`five`). Verified in-round: [totality] all 498 lines of the applied runbook were read, and every passage restating an amended value is dispositioned in §2.1's matrix. Two of them are missed → §2.1.

## 2. Literal-wrongness findings

1. **Task 4 leaves two runbook passages that restate what the amendment replaced; one of them halts the paid session at the smoke test.**

   Description. The plan's goal includes "bring the Task 3 runbook in line", and spec §2/§5/§6 change the expected reply shape, the fate of a `length` stop, and the cost estimate. Task 4's diff has no hunk for either of these:

   - (a) Runbook step 5 (main checkout `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md:214-216`, unchanged after Task 4 at `:219-221`): "Read `$A/smoke.responses.jsonl` and confirm the reply is a 50-element JSON array and `finish_reason` is `stop`, not `length`." Under spec §2 a reply is accepted at a ≥ 20-id valid prefix, and under spec §5 and the playbook row Task 4 itself adds, a `length` stop is "nothing; it is one failed attempt". Most real accepted replies are not 50 elements. The operator following step 5 will read a correctly accepted smoke query as a failed check and stop before the main run, or be left with no instruction.
   - (b) Runbook line 7: "Budget: $2–5 per pass, under $20 including one failed pass and the repeat." Spec §6 (`:165-168`) says its 4.2 GPU-hour estimate at `--concurrency 12` "replaces §11's '$2–5 per pass' estimate". The runbook still states the old figure.

   Evidence. [totality] `teacher_order` over all 23 real v2 lists: 19 accepted, of which 17 have length < 50 (770: 43, 38, 39, 44; 521: 46, 44; 1332: 49; 783: 49, 44; 873: 35, 46; 1370: 47, 48; 759: 39; 51: 38, 39→prefix 38, 37) and 2 are 50 (1332, 783). So a correctly accepted smoke reply fails step 5's "50-element" check with probability ≈ 17/19. `command grep -n "50-element\|\$2–5"` on the applied runbook still hits `:220` and `:7`.

   Population matrix. The rule is "each runbook passage that restates an amended behaviour or value matches the amendment". The population is every passage of the applied runbook, all 498 lines read. Over (a passage flagged that restates nothing the amendment changed) and under (a restating passage missed) are dispositioned per cell:
   - test count, step 1 `:64`: over: [existence] ok — it is the suite count, which Task 1–3 change / under: [compat] ok — `58 passed`, reproduced.
   - dry-run expectations, step 1 `:78-94`: over: [existence] ok — spec §3 amends them / under: [compat] ok — both modes reproduced with `(300 queries, 0 fallbacks)`.
   - budget `:7`: over: [existence] ok — spec §6 `:168` names it as replaced / under: → §2.1(b).
   - smoke reply shape and `finish_reason`, step 5 `:219-221`: over: [existence] ok — spec §2 and §5 change both / under: → §2.1(a).
   - main-run command, step 6 `:231`: over: [existence] ok — spec §6 amends it / under: [existence] ok — `command grep -n -- "--concurrency 12"` → `:231`.
   - check count `:268`, `:302`, `:406`: over: [existence] ok — spec §7 adds check 6 / under: [negative] ok — `command grep -c five` → `0`.
   - repeat commands, step 8 `:332`: over: [existence] ok — spec §6 amends them / under: [existence] ok — `command grep -n -- "--concurrency 12"` → `:332`; per-pass fallbacks at `:361`.
   - verdict contents, step 10 `:406-409`: over: [existence] ok — spec §3 and §7 amend them / under: [existence] ok — `command grep -n "six checks\|fallback count and ids"` → `:406`, `:407`.
   - `length` playbook row and unverified list (8192): over: [existence] ok — spec §5 amends them / under: [negative] ok — `command grep -c 8192` → `0`.
   - all-or-nothing wording (`all scored`, `never filled`) and client timeout `600`: over: [existence] ok — spec §3 and §5 amend them / under: [negative] ok — `command grep -c "600\|all scored\|never filled"` → `0`.
   - every other passage (steps 2–4 provisioning and serving, step 7's pull-down and scoring commands, step 9 preserve/destroy, the 12 playbook rows the amendment does not touch): over: n/a, not flagged / under: [totality] ok — all read in the 498-line pass; none restates a reply-shape, attempt, budget, timeout, cost, check-count or fallback value.
   - §11 addendum "more than 2 attempts … (4 attempts total)", `:455-460`: over: n/a, not flagged / under: dropped — it restates the old budget, but it is the historical record of the measurement batch whose question the lenient design answers, not a step in the production path (steps 1–10).
   - worst-case prompt "137,152 characters ≈ 34K tokens", `:125`: over: n/a, not flagged / under: [negative] ok — not an amended value: `command grep -c "137,152\|34K\|worktrees\|STRUCTURED_OUTPUT"` on the lenient spec → `0`, and its header (`:3-5`) amends only §4, §6, §8 and §11, and the passage's only use ("128K leaves ample room") also holds for spec §6's 30,719 tokens.
   - header and dev-box block (`$W=…/.worktrees/teacher-ceiling`, "unmerged branch", `:3-4`, `:30`, `:37-41`) and step 4's `STRUCTURED_OUTPUT_PARAM`: over: n/a, not flagged / under: [negative] ok — these predate the amendment and restate no value it changes (same grep → `0`). They also fail loudly: `git worktree list` shows no `.worktrees/teacher-ceiling`, so step 1's command errors at once rather than running stale code.

   Proposed fix. Add two hunks to Task 4's runbook diff (and, if wanted, extend Step 2's grep to `8192\|five\|50-element\|\$2–5`):

   (a) Replace step 5's closing paragraph (main checkout `:214-216`; `:219-221` after Task 4's diff), "This costs pennies … not `length`.", with:
   > This costs pennies and exercises the one path the free gate structurally cannot: the real model's reply shape. Expect `$A/smoke.log`'s last line to end `(1 queries, 0 fallbacks)`, and the `accepted` record in `$A/smoke.responses.jsonl` to carry `prefix_len` ≥ 20. A reply shorter than 50 ids is normal: its valid prefix is kept and the rest of the pool follows in A0′ order (lenient-acceptance design §2). Rejected attempts before it (`finish_reason=length`, `valid prefix too short`, `http_error`) are one failed attempt each. `1 / 1 queries unscored` means all 4 attempts failed (a single-query invocation has a fallback cap of 0), so read their reasons before starting step 6.

   (b) Replace line 7 with:
   > Budget: ≈ 4.2 GPU-hours for the main run plus both repeats at `--concurrency 12` (≈ 8.4 at 6), from lenient-acceptance design §6, which replaces the earlier "$2–5 per pass" estimate. Multiply by the rental rate confirmed in step 2.

   Evidence: [compat] a 1-query stub run of the final code printed `… (1 queries, 0 fallbacks)` as its last line, and its ledger record was `('accepted', 50)`, so `prefix_len` is recorded on the accepted record. A 1-query run against a dead port printed `1 / 1 queries unscored -- … (0 corrupt resumed entries; 1 fallbacks against a cap of 0)`. [existence] the "4 attempts" count is `RETRY_BUDGET = 4` in the applied source. [existence] The 4.2 / 8.4 GPU-hour figures and the replacement wording are verbatim from spec §6 `:165-168` (read).

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes — §2.1's two runbook hunks go into Task 4 before SDD; the code tasks (1–3) reproduce exactly as written.
