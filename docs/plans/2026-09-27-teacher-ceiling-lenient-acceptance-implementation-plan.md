# Teacher-Ceiling Lenient Acceptance, Fallback and Concurrency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-27-teacher-ceiling-lenient-acceptance-design.md` (commit SHA: `f2e52130362186cdd8406136d14100a03f35d110`)

**Goal:** Make `teacher_rerank.py` produce a 300-query run file from gpt-oss-120b's real reply behaviour — lenient prefix acceptance, a capped per-query fallback, a 4-attempt budget, a completion cap the server always hits before the client timeout, and `--concurrency N` — and bring the Task 3 runbook in line.

**Architecture:** All code changes are in one stdlib-only script and its pytest suite. A new `teacher_order` decides acceptance inside `score_query`'s attempt loop (`validate_permutation` still guards resumed orders); `score_query` gains a third return value that separates a corrupt resumed entry from an exhausted budget; `main` applies the `n // 20` fallback cap, writes fallbacks in A0′ order, and scores queries on a `ThreadPoolExecutor`, with a lock around ledger appends.

**Tech stack:** Python 3 stdlib (`concurrent.futures`, `threading`, `time`, `urllib`), pytest 9.1.1. No new dependency.

---

## Global Constraints

- Stdlib + pytest only — no new dependency (base spec §4, `teacher_rerank.py:17`).
- `validate_permutation` is unchanged; it keeps guarding the resume path (spec §2).
- The prompt is unchanged, so `PROMPT_TEMPLATE_SHA256` is unchanged (spec §2).
- Values, verbatim from the spec: prefix threshold `min(20, pool size)`; fallback cap `floor(0.05 × n)`; `RETRY_BUDGET = 4`; `MAX_COMPLETION_TOKENS = 16384`; client timeout 1,200 s; recommended `--concurrency 12`.
- A corrupt resumed accepted entry refuses the run whatever the fallback count (spec §3, CDR-1 §3.1).
- Every diff below was applied in sequence with `git apply` to a clean copy of `HEAD` (`f2e52130`) and tested there. Apply them exactly. Hand edits are fine only if the result is byte-identical.

## File Structure

- Modify: `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py` — acceptance rule, constants, ledger fields (Task 1); fallback, cap, corrupt-resume refusal, sidecar key (Task 2); `--concurrency` and the ledger lock (Task 3).
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py` — new tests for each task; four existing tests' reply queues grow from 2 to 4 failing attempts (Task 1).
- Modify: `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md` — operational steps for the new behaviour (Task 4).

## Inherited from spec

Verified by `thorough-brainstorming` and CDR-1 at spec-write time (spec §10) and not re-verified here:

- **L1** `validate_permutation` has exactly two call sites: resume (`:433`) and the attempt loop (`:473`).
- **L2** A stored repaired order passes the resume re-check (it is a permutation of the pool by construction).
- **L3** `main` is the only place the run file is written or refused (`:637-646`).
- **L4** `load_run` returns each query's ids in A0′ rank order (all 300 queries checked).
- **L5** Existing refusal tests keep their meaning under the cap (2-query fixture, cap 0).
- **L6** `RETRY_BUDGET` consumers: `teacher_rerank.py:51, 420, 442`; `test_teacher_rerank.py:718, 791`.
- **L7** `MAX_COMPLETION_TOKENS` consumers: `:45, 352, 383, 448-449, 549`.
- **L8** The client timeout appears once (`:395`); no test references 600.
- **L9** `score_query` shares no mutable state across queries; its only side effect is `append_response`.
- **L10** `append_response` is the only ledger writer; five call sites in `score_query`.
- **L11** `report.py` reads only `composite` from the sidecar.
- **L12** `report.py --pair`'s pool check tolerates A0′-order queries under the cap.
- **L13** `summarize_teacher_attempts.py` reads only `status` and `reason`.
- **L14** Only the five scripts in `scripts/` import or run `teacher_rerank`.
- **L15** The stub's two dry-run modes still pin their numbers (full 50-id replies).
- **L16** Per-request decode ≈ 24.8 tokens/s, flat across 1–5 concurrent.
- **L17** Largest prompt 30,719 tokens; KV pool 655,317.
- **L18** The lenient rule accepts 19/23 real lists and 8/8 queries.
- **L19** Threaded appends did not corrupt locally; the lock covers other filesystems.
- **L20** vLLM abort-on-disconnect is unverified and designed around.
- **L21** `score_query` has exactly two unscored producers (`:433-435`, `:485`), indistinguishable to `main` today; one caller (`main`, `:631`).
- **L22** Every real pool has exactly 50 ids.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Command | `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q` runs the suite from the repo root | Ran at `f2e52130`: `44 passed` |
| 2 | Consumer impact | The tests whose reply queues hold only 2 failing attempts for a query are exactly those at `:596`, `:687`, `:721`, `:1024` | `command grep -n "ScriptedTeacher(\["` lists 17 sites; the 10 non-empty queues are `:394, 413, 596, 639, 687, 721, 806, 1024, 1059, 1091`; read each — the other six queue only accepted replies |
| 3 | Consumer impact | Existing refusal assertions check `"1 / 2"` (`:610`, `:735`) and reason substrings; Task 2's message keeps the `k / n queries unscored` wording | Read `:604-611`, `:729-739`, `:968-974`; all pass after Task 2 (row 9) |
| 4 | Consumer impact | `write_sidecar`'s callers are `main` (`:645`) and four tests (`:255, 264, 273, 286`) calling `write_sidecar(path, args)`; a defaulted third parameter keeps them working | `command grep -n "write_sidecar("`; all four pass after Task 2 |
| 5 | Signature | `score_query`'s only caller is `main` (`:631`), so its 3-tuple return touches one call site | `command grep -n "score_query(" scripts/*.py` → the def at `:415` and `:631` only |
| 6 | Signature | `pass_identity` (`:287-303`) excludes `--concurrency`, so re-issuing a pass at a lower concurrency resumes rather than refusing | Read `:298-303`: `run`, `shuffleSeed`, `subsample`, `subsampleSeed` only |
| 7 | Code validity | The prompt carries `Query: <text>\n` (`PROMPT_INSTRUCTIONS`, `:64`), so `KeyedTeacher` can key replies on it; `"Query: query 1\n"` does not match query 10's prompt | Read `:62-68`; Task 3's tests pass with 20 queries |
| 8 | Code validity | `monkeypatch.setattr(tr, "time", Namespace(monotonic=...))` replaces only `teacher_rerank`'s reference to the `time` module, not pytest's | Task 1's elapsed-time test passes, and the rest of the suite is unaffected |
| 9 | Ordering | Each task's test diff, then its source diff, applies with `git apply` in the order 1 → 2 → 3 on a clean copy of `f2e52130`; tests fail before the source and pass after | Ran: Task 1 `13 failed, 40 passed` → `53 passed`; Task 2 `4 failed, 52 passed` → `56 passed`; Task 3 `2 failed, 56 passed` → `58 passed` |
| 10 | Code validity | The new tests catch the defects they exist for | Seven mutations of the final code, each killed: keeping ids after a duplicate (1 failure); threshold without `min` (17); dropping `elapsed_s` from the `http_error` site (1); cap `n // 10` (2); corrupt entry counted as a fallback (1); `--concurrency` ignored (1, via the barrier); fallback written reversed (1). Removing the lock is not caught — accepted per spec L19 |
| 11 | Code validity | With the final code, the runbook's free dry-run gate still reproduces both pinned pairs and prints `0 fallbacks`, at `--concurrency 4` | Ran against the real A0′ pool and `stub_vllm_server.py`: identity `nDCG@10 0.6980`, `R@50 0.9193`; reversed `0.0032`, `0.9193`; `build 31583db5aea49136` and `(300 queries, 0 fallbacks)` both times |
| 12 | Consumer impact | `run_measurement_batch.sh` reads only `tail -1` of each pass log, and single-query invocations have a cap of 0, so no `fallback` line ever follows the `wrote` line there | `run_measurement_batch.sh:88`; `n // 20 = 0` for n = 1 |
| 13 | File path | Every runbook anchor Task 4 edits is present exactly once, and after the edit no `8192`, "five checks", "50-element" or "Budget: $2–5" text remains | Applied the edits to a copy with a uniqueness assertion per anchor; `command grep -c "8192\|five\|50-element\|Budget: \$2–5"` → 0 (6 on the original); the diff `git apply`s to the original runbook and is byte-identical to the edited copy |
| 14 | Command | The two shell one-liners Task 4 adds to the runbook run as written | Ran both against sample files: `1 4960 200.0 24.8` and `['q7']` |
| 15 | Commit convention | Subjects are lowercase imperative with no prefix, ending with a `Co-Authored-By` trailer; `docs/plans` is gitignored and needs `git add -f` | `git log --oneline -15`; base spec A22 |
| 16 | Consumer impact | Every runbook passage that restates a value the spec changed (reply shape, `length` handling, attempts, budget, timeout, cost, check count, fallbacks) is edited by Task 4 | CIR-1 span check: all 498 lines of the applied runbook read, every restating passage dispositioned (review §2.1 matrix); the two it found unedited (`:7` budget, `:219-221` smoke check) are now Task 4 hunks, confirmed by row 13's grep |

## Tasks

### Task 1: Lenient acceptance, retry budget, completion cap, client timeout, ledger timing

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py`

**Interfaces:**
- Produces: `teacher_order(candidate_ids, expected_ids) -> (order | None, prefix_len, reason | None)`; `MIN_PREFIX = 20`; `RETRY_BUDGET = 4`; `make_record(..., elapsed_s=None, prefix_len=None)`; `import time` at module level (Task 1's test patches `tr.time`).

- [ ] **Step 1: Apply the test changes and watch them fail**

Adds 8 `teacher_order` unit tests and one test that sends one attempt through each of `score_query`'s five ledger-write sites. The four existing tests from plan row 2 get four failing replies instead of two.

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
@@ -92,6 +92,63 @@
 
 
 # --------------------------------------------------------------------------------------------
+# teacher_order -- lenient acceptance (lenient-acceptance design §2): the longest valid prefix,
+# accepted at >= min(20, pool size) ids, then the rest of the pool in A0' order
+# --------------------------------------------------------------------------------------------
+
+POOL50 = [str(1000 + i) for i in range(50)]  # A0' order: 1000, 1001, ..., 1049
+REVERSED50 = list(reversed(POOL50))          # a reply that differs from A0' at every position
+
+
+def test_teacher_order_accepts_a_full_permutation_as_a_prefix_of_fifty():
+    assert tr.teacher_order(REVERSED50, POOL50) == (REVERSED50, 50, None)
+
+
+def test_teacher_order_accepts_a_twenty_id_prefix_and_fills_the_rest_in_pool_order():
+    order, prefix_len, reason = tr.teacher_order(REVERSED50[:20], POOL50)
+    assert (prefix_len, reason) == (20, None)
+    assert order == REVERSED50[:20] + POOL50[:30]  # 1049..1030, then 1000..1029 in A0' order
+
+
+def test_teacher_order_rejects_a_nineteen_id_prefix():
+    assert tr.teacher_order(REVERSED50[:19], POOL50) == (
+        None, 19, "valid prefix too short: 19 < 20 (reply ended after 19 ids)")
+
+
+def test_teacher_order_stops_at_the_first_duplicate_and_discards_everything_after_it():
+    # 26 valid, unplaced ids follow the duplicate; keeping them would write REVERSED50 unchanged.
+    reply = REVERSED50[:24] + [REVERSED50[0]] + REVERSED50[24:]
+    order, prefix_len, reason = tr.teacher_order(reply, POOL50)
+    assert (prefix_len, reason) == (24, None)
+    assert order == REVERSED50[:24] + POOL50[:26]
+
+
+def test_teacher_order_stops_at_the_first_invented_id_and_discards_everything_after_it():
+    reply = REVERSED50[:21] + ["9999"] + REVERSED50[21:]
+    order, prefix_len, reason = tr.teacher_order(reply, POOL50)
+    assert (prefix_len, reason) == (21, None)
+    assert order == REVERSED50[:21] + POOL50[:29]
+
+
+def test_teacher_order_names_what_stopped_a_too_short_prefix():
+    assert tr.teacher_order(REVERSED50[:5] + ["9999"] + REVERSED50[5:], POOL50) == (
+        None, 5, "valid prefix too short: 5 < 20 (stopped at invented id '9999')")
+    assert tr.teacher_order(REVERSED50[:5] + [REVERSED50[1]], POOL50) == (
+        None, 5, f"valid prefix too short: 5 < 20 (stopped at duplicated id {REVERSED50[1]!r})")
+
+
+def test_teacher_order_normalises_unquoted_json_numbers():
+    order, prefix_len, reason = tr.teacher_order([int(d) for d in REVERSED50], POOL50)
+    assert (order, prefix_len, reason) == (REVERSED50, 50, None)
+
+
+def test_teacher_order_threshold_is_the_pool_size_for_a_pool_smaller_than_twenty():
+    assert tr.teacher_order(["d2", "d3", "d1"], ["d1", "d2", "d3"]) == (["d2", "d3", "d1"], 3, None)
+    assert tr.teacher_order(["d2", "d3"], ["d1", "d2", "d3"]) == (
+        None, 2, "valid prefix too short: 2 < 3 (reply ended after 2 ids)")
+
+
+# --------------------------------------------------------------------------------------------
 # parse_json_array -- rejects a non-array and a non-JSON body
 # --------------------------------------------------------------------------------------------
 
@@ -587,35 +644,38 @@
 def test_main_records_http_error_to_responses_and_continues_retry_loop(tmp_path, monkeypatch):
     """The http_error finish_reason from call_teacher must be handled as a retriable failure
     (like finish_reason='length'), recording a rejected entry and continuing the retry loop.
-    A query with http_error on both attempts must be left unscored (refusal-to-write rule)."""
+    A query with http_error on all RETRY_BUDGET (4) attempts must be left unscored (refusal-to-write
+    rule: the 2-query fixture's fallback cap is 0)."""
     run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
     responses_path = str(tmp_path / "responses.jsonl")
     out_path = str(tmp_path / "teacher.chunks.trec")
 
-    # q1: both attempts return http_error; q2: succeeds on first attempt
+    # q1: all four attempts return http_error; q2: succeeds on first attempt
     scripted = ScriptedTeacher([
         ("HTTP 500: Internal Server Error -- FSM crash", "http_error", None, None, None),
         ("HTTP 502: Bad Gateway", "http_error", None, None, None),
+        ("HTTP 503: Service Unavailable", "http_error", None, None, None),
+        ("timed out", "http_error", None, None, None),
         ('["e2", "e3", "e1"]', "stop", None, None, None),
     ])
     monkeypatch.setattr(tr, "call_teacher", scripted)
 
     argv = base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
     with pytest.raises(SystemExit) as exc_info:
-        tr.main(argv)  # refused -- q1 unscored due to http_error on both attempts
+        tr.main(argv)  # refused -- q1 unscored due to http_error on all four attempts
 
     assert exc_info.value.code != 0
     message = str(exc_info.value)
     assert "q1" in message
     assert "1 / 2" in message  # "1 / 2 queries unscored"
     assert not os.path.exists(out_path), "no run file may be written when any query is unscored"
-    assert scripted.calls == 3
+    assert scripted.calls == 5
 
-    # Verify q1's two http_error attempts were durably logged
+    # Verify q1's four http_error attempts were durably logged
     with open(responses_path, encoding="utf-8") as f:
         records = [json.loads(line) for line in f if line.strip()]
     q1_records = [r for r in records if r["query_id"] == "q1"]
-    assert len(q1_records) == 2
+    assert len(q1_records) == 4
     assert all(r["status"] == "rejected" for r in q1_records)
     assert all("http_error:" in r["reason"] for r in q1_records), (
         "reason must start with 'http_error:' followed by the error details"
@@ -681,12 +741,10 @@
     responses_path = str(tmp_path / "responses.jsonl")
     out_path = str(tmp_path / "teacher.chunks.trec")
 
-    # q1: both attempts return content=None (as vLLM does for a reasoning model whose answer
+    # q1: all four attempts return content=None (as vLLM does for a reasoning model whose answer
     # landed in reasoning_content) -- q1 is left unscored, but must not crash, and every attempt
     # must still be durably logged.
-    scripted = ScriptedTeacher([
-        (None, "stop", None, None, None),
-        (None, "stop", None, None, None),
+    scripted = ScriptedTeacher([(None, "stop", None, None, None)] * 4 + [
         ('["e2", "e3", "e1"]', "stop", None, None, None),
     ])
     monkeypatch.setattr(tr, "call_teacher", scripted)
@@ -701,7 +759,7 @@
     with open(responses_path, encoding="utf-8") as f:
         records = [json.loads(line) for line in f if line.strip()]
     q1_records = [r for r in records if r["query_id"] == "q1"]
-    assert len(q1_records) == 2, "both null-content attempts must be recorded, not silently lost"
+    assert len(q1_records) == 4, "every null-content attempt must be recorded, not silently lost"
     assert all(r["status"] == "rejected" for r in q1_records)
     assert all(r["content"] is None for r in q1_records)
 
@@ -715,13 +773,15 @@
     responses_path = str(tmp_path / "responses.jsonl")
     out_path = str(tmp_path / "teacher.chunks.trec")
 
-    # q1 succeeds on its first attempt; q2 fails invalid JSON on both of its RETRY_BUDGET (2)
-    # attempts and is therefore left unscored -- the run file must not be written even though q1
-    # itself scored fine, and never filled from fusion order for the query that failed.
+    # q1 succeeds on its first attempt; q2 fails invalid JSON on all four of its RETRY_BUDGET (4)
+    # attempts and is therefore left unscored -- the 2-query fixture's fallback cap is 0, so the run
+    # file must not be written even though q1 itself scored fine.
     scripted = ScriptedTeacher([
         ('["d3", "d1", "d2"]', "stop", None, None, None),
         ("not valid json", "stop", None, None, None),
         ("still not valid json", "stop", None, None, None),
+        ("not json either", "stop", None, None, None),
+        ("nor this", "stop", None, None, None),
     ])
     monkeypatch.setattr(tr, "call_teacher", scripted)
 
@@ -736,12 +796,12 @@
     # to len(failures)+99 yields "100 / 2 queries unscored", and a bare "1" still matches that,
     # because "1" also occurs inside the embedded reason "Expecting value: line 1 column 1".
     assert not os.path.exists(out_path), "no run file may be written when any query is unscored"
-    assert scripted.calls == 3
+    assert scripted.calls == 5
 
     # every attempt, including q1's accepted one, was still durably logged to --responses
     with open(responses_path, encoding="utf-8") as f:
         records = [json.loads(line) for line in f if line.strip()]
-    assert len(records) == 3
+    assert len(records) == 5
     assert records[0]["status"] == "accepted"
     assert records[0]["query_id"] == "q1"
     assert all(r["status"] == "rejected" for r in records[1:])
@@ -1021,9 +1081,7 @@
     responses_path = str(tmp_path / "responses.jsonl")
     out_path = str(tmp_path / "teacher.chunks.trec")
 
-    scripted = ScriptedTeacher([
-        ('["d2", "d3"', "length", None, None, None),   # q1: truncated completion, both attempts
-        ('["d2", "d3"', "length", None, None, None),
+    scripted = ScriptedTeacher([('["d2", "d3"', "length", None, None, None)] * 4 + [  # q1: truncated, all 4
         ('["e2", "e3", "e1"]', "stop", None, None, None),
     ])
     monkeypatch.setattr(tr, "call_teacher", scripted)
@@ -1042,7 +1100,7 @@
 
     with open(responses_path, encoding="utf-8") as f:
         q1_records = [json.loads(l) for l in f if l.strip() and json.loads(l)["query_id"] == "q1"]
-    assert len(q1_records) == 2
+    assert len(q1_records) == 4
     assert all("max_tokens" in r["reason"] for r in q1_records)
 
 
@@ -1110,3 +1168,43 @@
     assert q2_record["reasoning"] == "other reasoning text"
     assert q2_record["completion_tokens"] == 333
     assert q2_record["prompt_tokens"] == 444
+
+
+# --------------------------------------------------------------------------------------------
+# lenient-acceptance design §6: every attempt records its wall time (elapsed_s), so the per-request
+# decode rate at --concurrency 12 is readable from the ledger; replies judged by the prefix rule
+# also record prefix_len. One attempt through each of score_query's five append sites.
+# --------------------------------------------------------------------------------------------
+
+def test_main_records_elapsed_s_on_every_attempt_and_prefix_len_on_judged_replies(tmp_path, monkeypatch):
+    run_path, corpus_path, queries_path = make_fixture_files(tmp_path)
+    responses_path = str(tmp_path / "responses.jsonl")
+    out_path = str(tmp_path / "teacher.chunks.trec")
+
+    scripted = ScriptedTeacher([
+        ("timed out", "http_error", None, None, None),     # q1: http_error site
+        ('["d2", "d3"]', "stop", None, None, None),        # q1: prefix 2 < 3, rejected
+        ('["d2", "d3", "d1"]', "stop", None, None, None),  # q1: accepted
+        ('["e2", "e3"', "length", None, None, None),       # q2: length site
+        ("not json", "stop", None, None, None),            # q2: parse-error site
+        ('["e2", "e3", "e1"]', "stop", None, None, None),  # q2: accepted
+    ])
+    monkeypatch.setattr(tr, "call_teacher", scripted)
+    # Replace only teacher_rerank's reference to the time module, not the real one pytest uses.
+    ticks = iter([0.0, 1200.0, 1300.0, 1302.5, 1400.0, 1401.25,
+                  1500.0, 1660.0, 1700.0, 1700.75, 1800.0, 1800.5])
+    monkeypatch.setattr(tr, "time", Namespace(monotonic=lambda: next(ticks)))
+
+    tr.main(base_argv(run_path, corpus_path, queries_path, responses_path, out_path))
+
+    with open(responses_path, encoding="utf-8") as f:
+        records = [json.loads(line) for line in f if line.strip()]
+    assert [(r["query_id"], r["status"], r["prefix_len"], r["elapsed_s"]) for r in records] == [
+        ("q1", "rejected", None, 1200.0),
+        ("q1", "rejected", 2, 2.5),
+        ("q1", "accepted", 3, 1.25),
+        ("q2", "rejected", None, 160.0),
+        ("q2", "rejected", None, 0.75),
+        ("q2", "accepted", 3, 0.5),
+    ]
+    assert records[1]["reason"] == "valid prefix too short: 2 < 3 (reply ended after 2 ids)"
```

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`
Expected: `13 failed, 40 passed` (the 9 new tests fail on the missing `teacher_order`/`elapsed_s`; the 4 re-queued tests fail because `RETRY_BUDGET` is still 2).

- [ ] **Step 2: Apply the source changes**

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
@@ -7,9 +7,10 @@
 
 One call per query. The prompt carries the query text and all 50 documents (title + abstract from
 `corpus.jsonl`), each labelled by doc id, presented in a seeded shuffle (NOT fusion order -- an
-anchored teacher would measure the old ranking as much as itself). The model must reply with a JSON
-array containing exactly those 50 doc ids, most relevant first; the run file's score column is
-rank-derived (`score = 51 - position`), so ties are structurally impossible.
+anchored teacher would measure the old ranking as much as itself). The model is asked for a JSON
+array of all 50 doc ids, most relevant first; its longest valid prefix is accepted when it holds at
+least 20 ids, and the rest of the pool follows in A0' order (lenient-acceptance design §2). The run
+file's score column is rank-derived (`score = 51 - position`), so ties are structurally impossible.
 
 Patterned on `popularity_rerank.py` (`load_run`, `write_run`, argparse `--run`/`--out`) -- per-script
 `load_run` helpers are this directory's convention; this script does not import that sibling's.
@@ -36,22 +37,27 @@
 import os
 import random
 import sys
+import time
 import urllib.error
 import urllib.request
 from datetime import datetime, timezone
 
 # ── Serving constants (spec §4) ──────────────────────────────────────────────────────────────
 
-MAX_COMPLETION_TOKENS = 32768  # raised from 8192 (fixes design doc §2): the largest real prompt
-# measured live (23,427 tokens) plus this budget is 56,195, well under MAX_MODEL_LEN's 131,072.
-# Reasoning and the final answer share this one budget -- a reasoning model's chain-of-thought
-# can consume a large, variable share of it before any answer text appears.
+MAX_COMPLETION_TOKENS = 16384  # lenient-acceptance design §5: at the measured ~24.8 tokens/s per
+# request this ends in ~661 s, so the server always stops a runaway generation (finish_reason=length)
+# before call_teacher's 1,200 s client timeout fires -- no orphaned server-side generation, whatever
+# vLLM does on a client disconnect. The largest valid reply measured was 9,504 tokens. Reasoning and
+# the final answer share this one budget.
 MAX_MODEL_LEN = 131072  # vLLM's --max-model-len for this teacher (128K); recorded in the sidecar.
 
-RETRY_BUDGET = 2  # one initial attempt + one retry (spec §6 row 1: "One retry ... on a second
-# failure the query is recorded unscored"). A resumed invocation gets a FRESH budget of 2, never a
+RETRY_BUDGET = 4  # one attempt + three retries (lenient-acceptance design §4); after the fourth
+# rejection the query is a fallback (§3). A resumed invocation gets a FRESH budget of 4, never a
 # reduced one carried over from a previous invocation's rejected ledger entries (spec §6 row 4).
 
+MIN_PREFIX = 20  # lenient-acceptance design §2: a reply is accepted when its longest valid prefix
+# holds at least min(MIN_PREFIX, pool size) ids. nDCG@10 reads only the top 10; 20 is a 2x margin.
+
 RUN_TAG = "teacher-ceiling"
 SIDECAR_COMPOSITE = "31583db5aea49136"  # A0'’s own composite (spec §5 "Sidecar") -- the retrieval
 # build that produced the pool is the one under comparison; a teacher-derived composite here would
@@ -209,6 +215,36 @@
     return True, normalized
 
 
+def teacher_order(candidate_ids, expected_ids):
+    """Lenient acceptance (lenient-acceptance design §2). The teacher's ranking is the reply's
+    longest valid prefix: elements in order, each normalised with `str()` (P22), stopping at the
+    first one that is not in the pool or repeats an earlier one -- everything after that point is
+    discarded, valid ids included. Accepted when the prefix holds at least
+    `min(MIN_PREFIX, len(expected_ids))` ids; the written order is then the prefix followed by the
+    pool's remaining ids in `expected_ids` (A0') order, so an omitted id ranks below every id the
+    teacher placed.
+
+    Returns (order, prefix_len, None) on acceptance and (None, prefix_len, reason) on rejection.
+    `validate_permutation` is NOT replaced: it still guards the resume path's stored orders."""
+    expected_set = set(expected_ids)
+    prefix, seen, stopped_by = [], set(), None
+    for element in candidate_ids:
+        doc_id = str(element)
+        if doc_id not in expected_set:
+            stopped_by = f"stopped at invented id {doc_id!r}"
+            break
+        if doc_id in seen:
+            stopped_by = f"stopped at duplicated id {doc_id!r}"
+            break
+        seen.add(doc_id)
+        prefix.append(doc_id)
+    threshold = min(MIN_PREFIX, len(expected_ids))
+    if len(prefix) < threshold:
+        why = stopped_by or f"reply ended after {len(prefix)} ids"
+        return None, len(prefix), f"valid prefix too short: {len(prefix)} < {threshold} ({why})"
+    return prefix + [doc_id for doc_id in expected_ids if doc_id not in seen], len(prefix), None
+
+
 # ── Responses ledger (resume, spec §6 row 4 / A34) ──────────────────────────────────────────
 
 def read_responses_ledger(path):
@@ -270,7 +306,8 @@
 
 
 def make_record(query_id, status, content, order, reason, pass_id,
-                 reasoning=None, completion_tokens=None, prompt_tokens=None):
+                 reasoning=None, completion_tokens=None, prompt_tokens=None,
+                 elapsed_s=None, prefix_len=None):
     return {
         "query_id": str(query_id),
         "status": status,
@@ -281,6 +318,8 @@
         "reasoning": reasoning,
         "completion_tokens": completion_tokens,
         "prompt_tokens": prompt_tokens,
+        "elapsed_s": elapsed_s,
+        "prefix_len": prefix_len,
     }
 
 
@@ -392,7 +431,7 @@
     if api_key:
         req.add_header("Authorization", f"Bearer {api_key}")
     try:
-        with urllib.request.urlopen(req, timeout=600) as resp:
+        with urllib.request.urlopen(req, timeout=1200) as resp:  # §5: > MAX_COMPLETION_TOKENS at 24.8 tok/s
             parsed = json.loads(resp.read())
     except urllib.error.HTTPError as e:
         body_text = e.read().decode("utf-8", errors="replace")[:2000]
@@ -418,7 +457,7 @@
 
     Resume: a query with an accepted ledger entry is skipped entirely -- no call is issued, and its
     accepted order is reused (spec §6 row 4). Otherwise this invocation gets a FRESH RETRY_BUDGET
-    (2) attempts regardless of how many rejected entries already sit in the ledger from a previous
+    (4) attempts regardless of how many rejected entries already sit in the ledger from a previous
     invocation -- the ledger is a durable log of every attempt, not a countdown.
 
     A resumed order is re-validated against this query's pool before it is reused: the script's
@@ -440,26 +479,30 @@
 
     last_reason = None
     for _attempt in range(RETRY_BUDGET):
+        started = time.monotonic()
         content, finish_reason, reasoning, completion_tokens, prompt_tokens = call_teacher(
             args.base_url, args.model, prompt, args.seed, api_key=args.api_key
         )
+        elapsed_s = round(time.monotonic() - started, 3)
         if finish_reason == "length":
             last_reason = (
                 f"finish_reason=length (the completion hit the {MAX_COMPLETION_TOKENS}-token "
-                "max_tokens budget before the reply was complete -- raise MAX_COMPLETION_TOKENS. "
+                "max_tokens budget before the reply was complete -- the expected end of a runaway "
+                "generation under lenient-acceptance design §5; do not raise the budget past what "
+                "the client timeout covers. "
                 f"A prompt over the {MAX_MODEL_LEN}-token max_model_len is the SEPARATE HTTP 400 "
                 "failure, not this one.)"
             )
             append_response(args.responses, make_record(
                 query_id, "rejected", content, None, last_reason, pass_id,
-                reasoning, completion_tokens, prompt_tokens))
+                reasoning, completion_tokens, prompt_tokens, elapsed_s))
             continue
 
         if finish_reason == "http_error":
             last_reason = f"http_error: {content}"
             append_response(args.responses, make_record(
                 query_id, "rejected", content, None, last_reason, pass_id,
-                reasoning, completion_tokens, prompt_tokens))
+                reasoning, completion_tokens, prompt_tokens, elapsed_s))
             continue
 
         ids, parse_error = parse_json_array(content)
@@ -467,20 +510,20 @@
             last_reason = parse_error
             append_response(args.responses, make_record(
                 query_id, "rejected", content, None, last_reason, pass_id,
-                reasoning, completion_tokens, prompt_tokens))
+                reasoning, completion_tokens, prompt_tokens, elapsed_s))
             continue
 
-        ok, result = validate_permutation(ids, expected_ids)
-        if ok:
+        order, prefix_len, reject_reason = teacher_order(ids, expected_ids)
+        if order is not None:
             append_response(args.responses, make_record(
-                query_id, "accepted", content, result, None, pass_id,
-                reasoning, completion_tokens, prompt_tokens))
-            return result, None
+                query_id, "accepted", content, order, None, pass_id,
+                reasoning, completion_tokens, prompt_tokens, elapsed_s, prefix_len))
+            return order, None
 
-        last_reason = result
+        last_reason = reject_reason
         append_response(args.responses, make_record(
             query_id, "rejected", content, None, last_reason, pass_id,
-            reasoning, completion_tokens, prompt_tokens))
+            reasoning, completion_tokens, prompt_tokens, elapsed_s, prefix_len))
 
     return None, last_reason
 
```

- [ ] **Step 3: Run the suite**

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`
Expected: `53 passed`.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
git commit -m "accept a teacher reply's longest valid prefix of >= 20 ids, raise RETRY_BUDGET to 4, cap completions at 16384 under a 1200 s timeout, record elapsed_s and prefix_len

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Per-query fallback, the 5% cap, and corrupt-resume refusal

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py`

**Interfaces:**
- Consumes: Task 1's `score_query` attempt loop (`return order, None` becomes `return order, None, False`) and `RETRY_BUDGET = 4` (the new tests queue `tr.RETRY_BUDGET` failures).
- Produces: `score_query -> (order, reason, corrupt_resume)`; `write_sidecar(out_path, args, fallback_query_ids=())` writing a top-level `"fallbackQueryIds"`; stdout `(<n> queries, <k> fallbacks)` then one `  fallback <qid>: <reason>` line per fallback; test helpers `make_many_fixture_files`, `reversed_reply`, `FAILED_REPLY`, `written_order` (Task 3 reuses the first three).

- [ ] **Step 1: Apply the test changes and watch them fail**

Adds a 20-query fixture (the smallest n with a cap of 1) and three tests: one fallback is written in A0′ order; two fallbacks are refused; a corrupt resumed entry is refused with 0 fallbacks under the cap. The existing sidecar test also asserts `fallbackQueryIds == []`.

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
@@ -323,6 +323,7 @@
         sidecar = json.load(f)
     assert sidecar["reranker"]["subsample"] is None
     assert sidecar["reranker"]["subsampleSeed"] is None
+    assert sidecar["fallbackQueryIds"] == []  # lenient-acceptance design §3: empty list, not absent
 
 
 def test_write_sidecar_records_serving_identity_flags_when_given(tmp_path):
@@ -1208,3 +1209,101 @@
         ("q2", "accepted", 3, 0.5),
     ]
     assert records[1]["reason"] == "valid prefix too short: 2 < 3 (reply ended after 2 ids)"
+
+
+# --------------------------------------------------------------------------------------------
+# lenient-acceptance design §3: a query that exhausts RETRY_BUDGET is a fallback, written in A0'
+# order, up to floor(0.05 * n) of them; over the cap, or on any corrupt resumed entry, no run file.
+# The 2-query fixture above has a cap of 0, so these need n >= 20 -- the smallest n whose cap is 1.
+# --------------------------------------------------------------------------------------------
+
+def make_many_fixture_files(tmp_path, n=20):
+    """n queries, three docs each: query qK's A0' pool is kK-1, kK-2, kK-3, in that order."""
+    run_lines, corpus_lines, query_lines = [], [], []
+    for k in range(1, n + 1):
+        for rank in (1, 2, 3):
+            doc_id = f"k{k}-{rank}"
+            run_lines.append(f"q{k} Q0 {doc_id} {rank} {1 - rank / 10:.1f} rerank-a0prime\n")
+            corpus_lines.append(json.dumps({"_id": doc_id, "title": f"title {doc_id}", "text": f"text {doc_id}"}))
+        query_lines.append(json.dumps({"_id": f"q{k}", "text": f"query {k}"}))
+    return (
+        write(tmp_path / "many.chunks.trec", "".join(run_lines)),
+        write(tmp_path / "many-corpus.jsonl", "\n".join(corpus_lines) + "\n"),
+        write(tmp_path / "many-queries.jsonl", "\n".join(query_lines) + "\n"),
+    )
+
+
+def reversed_reply(k):
+    """An accepted reply for query qK that differs from its A0' order."""
+    return (json.dumps([f"k{k}-3", f"k{k}-2", f"k{k}-1"]), "stop", None, None, None)
+
+
+FAILED_REPLY = ("not valid json", "stop", None, None, None)
+
+
+def written_order(out_path, query_id):
+    with open(out_path, encoding="utf-8") as f:
+        return [line.split()[2] for line in f if line.split()[0] == query_id]
+
+
+def test_main_writes_a_fallback_query_in_a0prime_order_when_under_the_cap(tmp_path, monkeypatch, capsys):
+    run_path, corpus_path, queries_path = make_many_fixture_files(tmp_path)
+    responses_path = str(tmp_path / "responses.jsonl")
+    out_path = str(tmp_path / "teacher.chunks.trec")
+
+    replies = []
+    for k in range(1, 21):
+        replies += [FAILED_REPLY] * tr.RETRY_BUDGET if k == 7 else [reversed_reply(k)]
+    monkeypatch.setattr(tr, "call_teacher", ScriptedTeacher(replies))
+
+    tr.main(base_argv(run_path, corpus_path, queries_path, responses_path, out_path))
+
+    assert written_order(out_path, "q7") == ["k7-1", "k7-2", "k7-3"]  # the fallback: A0' order
+    assert written_order(out_path, "q8") == ["k8-3", "k8-2", "k8-1"]  # a teacher-ordered query
+    with open(tr.sidecar_path_for(out_path), encoding="utf-8") as f:
+        assert json.load(f)["fallbackQueryIds"] == ["q7"]
+    stdout = capsys.readouterr().out
+    assert "(20 queries, 1 fallbacks)" in stdout
+    assert "fallback q7: invalid JSON" in stdout
+
+
+def test_main_refuses_when_fallbacks_exceed_the_cap(tmp_path, monkeypatch):
+    run_path, corpus_path, queries_path = make_many_fixture_files(tmp_path)
+    responses_path = str(tmp_path / "responses.jsonl")
+    out_path = str(tmp_path / "teacher.chunks.trec")
+
+    replies = []
+    for k in range(1, 21):
+        replies += [FAILED_REPLY] * tr.RETRY_BUDGET if k in (7, 13) else [reversed_reply(k)]
+    monkeypatch.setattr(tr, "call_teacher", ScriptedTeacher(replies))
+
+    with pytest.raises(SystemExit) as exc_info:
+        tr.main(base_argv(run_path, corpus_path, queries_path, responses_path, out_path))
+
+    message = str(exc_info.value)
+    assert "2 / 20 queries unscored" in message
+    assert "2 fallbacks against a cap of 1" in message
+    assert "q7" in message and "q13" in message
+    assert not os.path.exists(out_path)
+    assert not os.path.exists(tr.sidecar_path_for(out_path))
+
+
+def test_main_refuses_a_corrupt_resumed_entry_even_with_fallbacks_under_the_cap(tmp_path, monkeypatch):
+    run_path, corpus_path, queries_path = make_many_fixture_files(tmp_path)
+    responses_path = str(tmp_path / "responses.jsonl")
+    out_path = str(tmp_path / "teacher.chunks.trec")
+
+    corrupt = {"query_id": "q3", "status": "accepted", "content": '["x3","x2","x1"]',
+               "order": ["x3", "x2", "x1"], "reason": None, "pass": ledger_pass(run_path)}
+    write(responses_path, json.dumps(corrupt) + "\n")
+    scripted = ScriptedTeacher([reversed_reply(k) for k in range(1, 21) if k != 3])
+    monkeypatch.setattr(tr, "call_teacher", scripted)
+
+    with pytest.raises(SystemExit) as exc_info:
+        tr.main(base_argv(run_path, corpus_path, queries_path, responses_path, out_path))
+
+    message = str(exc_info.value)
+    assert "1 corrupt resumed entries; 0 fallbacks against a cap of 1" in message
+    assert "q3: resumed ledger entry is not a permutation" in message
+    assert scripted.calls == 19  # q3's accepted entry is never re-asked
+    assert not os.path.exists(out_path)
```

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`
Expected: `4 failed, 52 passed`.

- [ ] **Step 2: Apply the source changes**

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
@@ -452,8 +452,11 @@
 # ── Per-query scoring: resume + retry budget (spec §6 rows 1 and 4) ────────────────────────────
 
 def score_query(query_id, expected_ids, query_text, corpus, args, ledger):
-    """Returns (order, failure_reason). `order` is a list of doc ids (the query's new ranking,
-    most relevant first) on success, None on failure. Exactly one of the two is None.
+    """Returns (order, failure_reason, corrupt_resume). `order` is a list of doc ids (the query's
+    new ranking, most relevant first) on success, None on failure; exactly one of the first two is
+    None. `corrupt_resume` is True only when the failure is a resumed accepted entry that fails its
+    re-check -- `main` refuses the run for that whatever the fallback cap, while an exhausted retry
+    budget (False) makes the query a fallback (lenient-acceptance design §3, CDR-1 §3.1).
 
     Resume: a query with an accepted ledger entry is skipped entirely -- no call is issued, and its
     accepted order is reused (spec §6 row 4). Otherwise this invocation gets a FRESH RETRY_BUDGET
@@ -471,8 +474,8 @@
     if accepted is not None:
         ok, result = validate_permutation(accepted.get("order") or [], expected_ids)
         if not ok:
-            return None, f"resumed ledger entry is not a permutation of this query's pool: {result}"
-        return result, None
+            return None, f"resumed ledger entry is not a permutation of this query's pool: {result}", True
+        return result, None, False
 
     shuffled = shuffled_doc_order(expected_ids, args.shuffle_seed, query_id)
     prompt = build_prompt(query_text, shuffled, corpus)
@@ -518,14 +521,14 @@
             append_response(args.responses, make_record(
                 query_id, "accepted", content, order, None, pass_id,
                 reasoning, completion_tokens, prompt_tokens, elapsed_s, prefix_len))
-            return order, None
+            return order, None, False
 
         last_reason = reject_reason
         append_response(args.responses, make_record(
             query_id, "rejected", content, None, last_reason, pass_id,
             reasoning, completion_tokens, prompt_tokens, elapsed_s, prefix_len))
 
-    return None, last_reason
+    return None, last_reason, False
 
 
 # ── Run-file writer: score = 51 - position (spec §3) ────────────────────────────────────────
@@ -563,7 +566,7 @@
     return base + ".meta.json"
 
 
-def write_sidecar(out_path, args):
+def write_sidecar(out_path, args, fallback_query_ids=()):
     """Writes `<label>.meta.json` beside the run file, carrying A0's own composite (spec: "the
     retrieval build that produced the pool is the same one under comparison") and the teacher's
     identity under `reranker`. report.py reads only `composite`; every other key is inert to it.
@@ -575,11 +578,16 @@
 
     `vllmVersion`/`quantisation`/`instanceType` come from the optional `--vllm-version`,
     `--quantisation`, `--instance-type` flags (controller ruling, Task 1 fix round 1) and are
-    `null` when the flag was omitted -- never guessed here."""
+    `null` when the flag was omitted -- never guessed here.
+
+    `fallbackQueryIds` lists the queries written in A0' order after exhausting RETRY_BUDGET
+    (lenient-acceptance design §3); `report.py` reads only `composite`, so it is inert there and
+    exists for the verdict doc's fallback count."""
     sidecar = {
         "configLabel": "teacher-ceiling",
         "composite": SIDECAR_COMPOSITE,
         "recordedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
+        "fallbackQueryIds": list(fallback_query_ids),
         "reranker": {
             "baseUrl": args.base_url,
             "modelId": args.model,
@@ -667,26 +675,38 @@
     validate_ledger_pass(args.responses, ledger, pass_identity(args))
 
     scored_pool = {}
-    failures = []
+    fallbacks = []
+    corrupt = []
     for query_id in pool_query_order:
         expected_ids = pool[query_id]
         query_text = queries[query_id]
-        order, reason = score_query(query_id, expected_ids, query_text, corpus, args, ledger)
-        if order is None:
-            failures.append((query_id, reason))
-        else:
+        order, reason, corrupt_resume = score_query(query_id, expected_ids, query_text, corpus, args, ledger)
+        if order is not None:
             scored_pool[query_id] = order
+        elif corrupt_resume:
+            corrupt.append((query_id, reason))
+        else:
+            fallbacks.append((query_id, reason))
 
-    if failures:
-        listing = "\n".join(f"  {qid}: {reason}" for qid, reason in failures)
+    # lenient-acceptance design §3: at most floor(0.05 * n) fallbacks -- n // 20 is that exactly,
+    # in integers -- and never a corrupt resumed entry, whatever the fallback count (CDR-1 §3.1).
+    n = len(pool_query_order)
+    cap = n // 20
+    if corrupt or len(fallbacks) > cap:
+        listing = "\n".join(f"  {qid}: {reason}" for qid, reason in corrupt + fallbacks)
         sys.exit(
-            f"[teacher_rerank] {len(failures)} / {len(pool_query_order)} queries unscored -- "
-            f"no run file written (never filled from fusion order):\n{listing}"
+            f"[teacher_rerank] {len(corrupt) + len(fallbacks)} / {n} queries unscored -- "
+            f"no run file written ({len(corrupt)} corrupt resumed entries; {len(fallbacks)} "
+            f"fallbacks against a cap of {cap}):\n{listing}"
         )
 
+    for query_id, _reason in fallbacks:
+        scored_pool[query_id] = list(pool[query_id])  # A0' order: delta 0 for this query
     write_run(args.out, scored_pool, pool_query_order)
-    write_sidecar(args.out, args)
-    print(f"[teacher_rerank] wrote {args.out} ({len(pool_query_order)} queries, all scored)")
+    write_sidecar(args.out, args, [query_id for query_id, _reason in fallbacks])
+    print(f"[teacher_rerank] wrote {args.out} ({n} queries, {len(fallbacks)} fallbacks)")
+    for query_id, reason in fallbacks:
+        print(f"  fallback {query_id}: {reason}")
 
 
 if __name__ == "__main__":
```

- [ ] **Step 3: Run the suite**

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`
Expected: `56 passed`.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
git commit -m "write a query that exhausts its retry budget in A0' order under a floor(5% x n) cap, refuse any corrupt resumed entry, record fallbackQueryIds in the sidecar

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: `--concurrency N`

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py`

**Interfaces:**
- Consumes: Task 2's 3-tuple from `score_query` and its test helpers `make_many_fixture_files`, `reversed_reply`, `FAILED_REPLY`.
- Produces: the `--concurrency` flag (default 1; below 1 exits), used by Task 4's runbook commands.

- [ ] **Step 1: Apply the test changes and watch them fail**

`KeyedTeacher` answers by the query text in the prompt, because `ScriptedTeacher`'s FIFO queue would hand replies to whichever thread asks first. One test checks that `--concurrency 4` writes the same run file and ledger records as sequential. The other uses a `threading.Barrier(4, timeout=10)` to show the calls really overlap: if the flag were ignored, the first call would wait alone and time out.

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
@@ -1307,3 +1307,71 @@
     assert "q3: resumed ledger entry is not a permutation" in message
     assert scripted.calls == 19  # q3's accepted entry is never re-asked
     assert not os.path.exists(out_path)
+
+
+# --------------------------------------------------------------------------------------------
+# lenient-acceptance design §6: --concurrency N scores queries on a thread pool. ScriptedTeacher's
+# FIFO queue would hand a reply to whichever thread asks first, so these tests answer by the query
+# text in the prompt instead.
+# --------------------------------------------------------------------------------------------
+
+class KeyedTeacher:
+    """Replies by the query text the prompt carries (`Query: <text>\\n`, PROMPT_INSTRUCTIONS), so a
+    reply never depends on call order. With `barrier`, the first `barrier.parties` calls wait on it:
+    they pass only if that many calls are in flight at once."""
+
+    def __init__(self, replies_by_query_text, barrier=None):
+        self.replies = replies_by_query_text
+        self.barrier = barrier
+        self.lock = threading.Lock()
+        self.calls = 0
+
+    def __call__(self, base_url, model, prompt, seed, api_key=None):
+        with self.lock:
+            self.calls += 1
+            wait = self.barrier is not None and self.calls <= self.barrier.parties
+        if wait:
+            self.barrier.wait()
+        for text, reply in self.replies.items():
+            if f"Query: {text}\n" in prompt:
+                return reply
+        raise AssertionError("no reply scripted for this prompt")
+
+
+def keyed_replies_with_q7_failing():
+    replies = {f"query {k}": reversed_reply(k) for k in range(1, 21)}
+    replies["query 7"] = FAILED_REPLY  # exhausts RETRY_BUDGET -> the one fallback under a cap of 1
+    return replies
+
+
+def test_main_with_concurrency_writes_the_same_run_and_ledger_as_sequential(tmp_path, monkeypatch):
+    run_path, corpus_path, queries_path = make_many_fixture_files(tmp_path)
+    outputs = {}
+    for concurrency in (1, 4):
+        responses_path = str(tmp_path / f"responses-{concurrency}.jsonl")
+        out_path = str(tmp_path / f"teacher-{concurrency}.chunks.trec")
+        teacher = KeyedTeacher(keyed_replies_with_q7_failing())
+        monkeypatch.setattr(tr, "call_teacher", teacher)
+        tr.main(base_argv(run_path, corpus_path, queries_path, responses_path, out_path)
+                + ["--concurrency", str(concurrency)])
+        assert teacher.calls == 19 + tr.RETRY_BUDGET
+        with open(out_path, encoding="utf-8") as f:
+            run_text = f.read()
+        with open(responses_path, encoding="utf-8") as f:
+            ledger = sorted((r["query_id"], r["status"], r["content"]) for r in map(json.loads, f))
+        outputs[concurrency] = (run_text, ledger)
+    assert outputs[4] == outputs[1]
+
+
+def test_main_with_concurrency_really_overlaps_model_calls(tmp_path, monkeypatch):
+    # If --concurrency were ignored, the first call would wait alone at the barrier and time out
+    # (BrokenBarrierError), failing the run instead of writing it.
+    run_path, corpus_path, queries_path = make_many_fixture_files(tmp_path)
+    responses_path = str(tmp_path / "responses.jsonl")
+    out_path = str(tmp_path / "teacher.chunks.trec")
+    teacher = KeyedTeacher(keyed_replies_with_q7_failing(), barrier=threading.Barrier(4, timeout=10))
+    monkeypatch.setattr(tr, "call_teacher", teacher)
+
+    tr.main(base_argv(run_path, corpus_path, queries_path, responses_path, out_path) + ["--concurrency", "4"])
+
+    assert os.path.exists(out_path)
```

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`
Expected: `2 failed, 56 passed` (argparse rejects `--concurrency`).

- [ ] **Step 2: Apply the source changes**

```diff
--- a/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
+++ b/Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py
@@ -37,9 +37,11 @@
 import os
 import random
 import sys
+import threading
 import time
 import urllib.error
 import urllib.request
+from concurrent.futures import ThreadPoolExecutor
 from datetime import datetime, timezone
 
 # ── Serving constants (spec §4) ──────────────────────────────────────────────────────────────
@@ -297,12 +299,19 @@
     return None
 
 
+_LEDGER_LOCK = threading.Lock()
+
+
 def append_response(path, record):
     """Appends one JSONL record. Opened and closed per call (not held open across the whole run) so
     that an interrupted pass (spec §6 row 4: "Instance dies mid-run") has every prior attempt
-    durably on disk, not buffered in a file object that never got flushed."""
-    with open(path, "a", encoding="utf-8") as f:
-        f.write(json.dumps(record) + "\n")
+    durably on disk, not buffered in a file object that never got flushed.
+
+    Serialised under `_LEDGER_LOCK`: with `--concurrency` > 1 several threads append to one ledger,
+    and a record can run to tens of KB of reasoning text (lenient-acceptance design §6)."""
+    with _LEDGER_LOCK:
+        with open(path, "a", encoding="utf-8") as f:
+            f.write(json.dumps(record) + "\n")
 
 
 def make_record(query_id, status, content, order, reason, pass_id,
@@ -628,6 +637,7 @@
     ap.add_argument("--quantisation", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
     ap.add_argument("--api-key", default=None, help="sent as 'Authorization: Bearer <key>' if given; omitted otherwise (some vLLM deployments require it, e.g. RunPod's VLLM_API_KEY template)")
     ap.add_argument("--instance-type", default=None, help="serving identity recorded verbatim in the sidecar's reranker block; omit to record null")
+    ap.add_argument("--concurrency", type=int, default=1, help="queries scored in parallel (default 1); 12 for the production run, bounded by KV-cache memory (lenient-acceptance design §6)")
     return ap
 
 
@@ -658,6 +668,8 @@
     args = build_arg_parser().parse_args(argv)
     if (args.subsample is None) != (args.subsample_seed is None):
         sys.exit("--subsample and --subsample-seed must be given together")
+    if args.concurrency < 1:
+        sys.exit("--concurrency must be at least 1")
 
     pool = load_run(args.run)
     corpus = load_corpus(args.corpus)
@@ -674,13 +686,17 @@
     ledger = read_responses_ledger(args.responses)
     validate_ledger_pass(args.responses, ledger, pass_identity(args))
 
+    def score(query_id):
+        return score_query(query_id, pool[query_id], queries[query_id], corpus, args, ledger)
+
+    # executor.map yields results in pool_query_order, whatever order the threads finish in.
+    with ThreadPoolExecutor(max_workers=args.concurrency) as executor:
+        results = list(executor.map(score, pool_query_order))
+
     scored_pool = {}
     fallbacks = []
     corrupt = []
-    for query_id in pool_query_order:
-        expected_ids = pool[query_id]
-        query_text = queries[query_id]
-        order, reason, corrupt_resume = score_query(query_id, expected_ids, query_text, corpus, args, ledger)
+    for query_id, (order, reason, corrupt_resume) in zip(pool_query_order, results):
         if order is not None:
             scored_pool[query_id] = order
         elif corrupt_resume:
```

- [ ] **Step 3: Run the suite**

Run: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`
Expected: `58 passed`.

- [ ] **Step 4: Run the free dry-run gate end-to-end (no GPU)**

From the repo root, with the preserved inputs:

```bash
S=Iverson.Server/Iverson.LoadTest/scripts
B=~/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
export PYTHONPATH=~/repositories/iverson-benchmark-corpora/python-libs
python3 $S/stub_vllm_server.py --run $B/runs/rerank-a0prime.chunks.trec --queries $B/beir/queries.jsonl   # prints its port
python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec --corpus $B/beir/corpus.jsonl \
  --queries $B/beir/queries.jsonl --base-url http://127.0.0.1:<port> --model stub --seed 1 --shuffle-seed 1 \
  --concurrency 4 --responses /tmp/dry-id.jsonl --out /tmp/dry-id.chunks.trec
python3 $S/report.py --run /tmp/dry-id.chunks.trec --qrels $B/qrels.trec
```

Expected: `teacher_rerank.py` ends `(300 queries, 0 fallbacks)`; `report.py` prints `build 31583db5aea49136`, `nDCG@10 0.6980`, `R@50 0.9193`. Repeat with the stub's `--order reversed` and fresh `/tmp/dry-rev.*` paths: `0.0032` and `0.9193`, again `0 fallbacks`. Stop the stub afterwards.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
git commit -m "add --concurrency N: score queries on a thread pool, serialise ledger appends under a lock

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 4: Runbook — the operational side of the new behaviour

**Files:**
- Modify: `docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md`

**Interfaces:**
- Consumes: the `--concurrency` flag (Task 3), the stdout line and `fallback` lines, `fallbackQueryIds` (Task 2), `elapsed_s` (Task 1), and the 58-test count.

The edits: the budget line's cost estimate; step 1's test count and `0 fallbacks` expectation for both dry runs (and why the identity pair cannot see a fallback); step 5's smoke-test expectations (a reply shorter than 50 ids is normal); `--concurrency 12` in steps 6 and 8; step 6's decode-rate check and how to drop to `--concurrency 6` by resuming; check 6 in step 7 with its sidecar one-liner; per-pass fallbacks in step 8; fallbacks and the bias bound in step 10's verdict; the corrected `length` playbook row plus rows for timeouts, the cap refusal and a corrupt entry; the unverified-list entry for speed at 12.

- [ ] **Step 1: Apply the runbook diff**

````diff
--- a/docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md
+++ b/docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md
@@ -4,7 +4,9 @@
 Tasks 1–2 are already built and committed on branch `teacher-ceiling`.
 
 **The meter runs from step 2 to step 9.** Everything before step 2 is free and must pass first.
-Budget: $2–5 per pass, under $20 including one failed pass and the repeat.
+Budget: ≈ 4.2 GPU-hours for the main run plus both repeats at `--concurrency 12` (≈ 8.4 at 6), from
+lenient-acceptance design §6, which replaces the earlier "$2–5 per pass" estimate. Multiply by the
+rental rate confirmed in step 2.
 
 ## Which machine runs what
 
@@ -61,7 +63,7 @@
 Run all three. If any disagrees, stop: the pipeline is wrong and renting a GPU would measure nothing.
 
 ```bash
-python3 -m pytest $S/test_teacher_rerank.py -q                    # expect: 35 passed
+python3 -m pytest $S/test_teacher_rerank.py -q                    # expect: 58 passed
 ```
 
 **Both halves of the gate.** Start the stub, note the port it prints, run against it, score it.
@@ -75,7 +77,8 @@
         --responses /tmp/pre-id.jsonl --out /tmp/pre-id.chunks.trec
 python3 $S/report.py --run /tmp/pre-id.chunks.trec --qrels $B/qrels.trec
 ```
-Expect **exactly** `nDCG@10 0.6980`, `R@50 0.9193`, and the line `build 31583db5aea49136`.
+Expect **exactly** `nDCG@10 0.6980`, `R@50 0.9193`, and the line `build 31583db5aea49136`, and
+`teacher_rerank.py`'s last line ending `(300 queries, 0 fallbacks)`.
 
 Repeat with `--order reversed` on the stub and a **fresh** `--responses` path:
 ```bash
@@ -83,11 +86,15 @@
         --queries $B/beir/queries.jsonl --order reversed
 # ... same teacher_rerank.py call, but --responses /tmp/pre-rev.jsonl --out /tmp/pre-rev.chunks.trec
 ```
-Expect **exactly** `nDCG@10 0.0032`, `R@50 0.9193`.
+Expect **exactly** `nDCG@10 0.0032`, `R@50 0.9193`, and again `(300 queries, 0 fallbacks)`.
 
 Why both: a pipeline that discards the model's reply and re-emits the candidate order scores
 0.6980 in *both* modes. The reversed pass is the only thing that catches it. Do not proceed on one.
 
+Why the fallback count too: a fallback writes A0′ order, which is exactly what the identity stub
+returns, so the identity pair cannot see one. Only the `0 fallbacks` line can (lenient-acceptance
+design §3).
+
 **Check the `build` line, not just the two numbers.** A wrong sidecar composite still scores
 0.6980/0.9193 but prints `BUILD MISMATCH` on every Task 3 comparison. Cheap to catch here, annoying
 to catch later.
@@ -212,8 +219,12 @@
 ```
 
 This costs pennies and exercises the one path the free gate structurally cannot: the real model's
-reply shape. Read `$A/smoke.responses.jsonl` and confirm the reply is a 50-element JSON array and
-`finish_reason` is `stop`, not `length`.
+reply shape. Expect `$A/smoke.log`'s last line to end `(1 queries, 0 fallbacks)`, and the `accepted`
+record in `$A/smoke.responses.jsonl` to carry `prefix_len` ≥ 20. A reply shorter than 50 ids is
+normal: its valid prefix is kept and the rest of the pool follows in A0′ order (lenient-acceptance
+design §2). Rejected attempts before it (`finish_reason=length`, `valid prefix too short`,
+`http_error`) are one failed attempt each. `1 / 1 queries unscored` means all 4 attempts failed (a
+single-query invocation has a fallback cap of 0), so read their reasons before starting step 6.
 
 ---
 
@@ -223,7 +234,7 @@
 setsid nohup python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
   --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
   --base-url http://127.0.0.1:8000 --model <model-id> --seed 20260920 --shuffle-seed 20260920 \
-  --vllm-version <ver> --quantisation mxfp4 --instance-type <sku> \
+  --vllm-version <ver> --quantisation mxfp4 --instance-type <sku> --concurrency 12 \
   --responses $A/main.responses.jsonl --out $A/teacher-ceiling.chunks.trec \
   > $A/main.log 2>&1 < /dev/null &
 disown
@@ -238,9 +249,29 @@
 If the instance dies mid-run, re-issue the **identical** command. Resume is enforced, not advisory:
 the ledger records the run path and both seeds, and refuses loudly if you change any of them.
 
+**Check the decode rate once the first attempts land** (≈ 5 minutes in). `--concurrency 12` is sized
+from KV-cache memory; per-request speed was measured only up to 5–6 concurrent (lenient-acceptance
+design §6). The last column is tokens/s per request, ≈ 24.8 at 1–5 concurrent:
+
+```bash
+python3 -c "import json
+for r in map(json.loads, open('$A/main.responses.jsonl')):
+    if r.get('completion_tokens') and r.get('elapsed_s'):
+        print(r['query_id'], r['completion_tokens'], r['elapsed_s'], round(r['completion_tokens'] / r['elapsed_s'], 1))"
+```
+
+Below ≈ 14 tokens/s a runaway generation can outlive the 1,200 s client timeout. Stop the run
+(`pkill -f "$PATTERN"`) and re-issue the same command with `--concurrency 6`. `--concurrency` is not
+part of the pass identity, so this resumes; queries in flight when you stopped are asked again with a
+fresh budget.
+
+The run ends with `(300 queries, K fallbacks)` in `$A/main.log`, then one `fallback <qid>: <reason>`
+line per fallback. More than 15 fallbacks, or any corrupt resumed ledger entry, refuses the run file
+(see the failure playbook).
+
 ---
 
-## 7. [dev] The five structural checks — pull the run file down first
+## 7. [dev] The six structural checks — pull the run file down first
 
 Bring the run file **and its sidecar** down — `report.py` finds the sidecar by filename, so a run
 file without its `.meta.json` scores fine but prints `BUILD UNKNOWN`:
@@ -274,6 +305,7 @@
 | 3 | `R@50` identical to `0.9193` at 4 dp | read off `[scores]`; a deviation means the pool was corrupted |
 | 4 | No result above the oracle **0.9196** | above it means label leakage, not a good teacher — stop and investigate |
 | 5 | Zero duplicate `(qid, score)` pairs | **`report.py` does not compute this** — it counts duplicate `(query_id, doc_id)`. Run it yourself (below) |
+| 6 | At most 15 fallbacks, no corrupt resumed entry | `teacher_rerank.py` refuses the run file otherwise; read the ids off the sidecar (below) for the verdict |
 
 Check 5, explicitly — expect `0`:
 
@@ -281,7 +313,13 @@
 awk '{print $1, $5}' $A/teacher-ceiling.chunks.trec | sort | uniq -d | wc -l
 ```
 
-Run it on the **teacher** run, not the baseline: scores there are rank-derived (`51 − position`) so
+Check 6's fallback ids, for the verdict doc:
+
+```bash
+python3 -c "import json; print(json.load(open('$A/teacher-ceiling.meta.json'))['fallbackQueryIds'])"
+```
+
+Run check 5 on the **teacher** run, not the baseline: scores there are rank-derived (`51 − position`) so
 ties are structurally impossible, and any duplicate means a writer bug. The same command on
 `rerank-a0prime.chunks.trec` returns `7` — those are real ties in the fusion scores, all at rank ≥23,
 and they are a property of the baseline, not a defect.
@@ -297,7 +335,7 @@
   python3 $S/teacher_rerank.py --run $B/runs/rerank-a0prime.chunks.trec \
     --corpus $B/beir/corpus.jsonl --queries $B/beir/queries.jsonl \
     --base-url http://127.0.0.1:8000 --model <model-id> --seed 20260920 --shuffle-seed 20260920 \
-    --subsample 50 --subsample-seed 20260920 \
+    --subsample 50 --subsample-seed 20260920 --concurrency 12 \
     --responses $A/repeat-$P.responses.jsonl --out $A/repeat-$P.chunks.trec
 done
 ```
@@ -326,6 +364,10 @@
 
 Reported, not gating. Confirm `distinct queries 50` and `covered by this run 50 / 50` in the output.
 
+Each pass's sidecar (`$A/repeat-$P.meta.json`) records its own `fallbackQueryIds` (cap 2 of 50);
+report both. A query that falls back in one pass but not the other counts as an ordering difference,
+so say how many of the differences are fallback-driven (lenient-acceptance design §7).
+
 ---
 
 ## 9. [both] Preserve and destroy
@@ -367,8 +409,10 @@
 ```
 
 Write `docs/plans/2026-09-GATE-teacher-ceiling.md` in the form of
-`docs/plans/2026-09-GATE-reranker-phase1.md`: method, serving identity, the five checks with their
-results, the repeat's noise floor, and the verdict.
+`docs/plans/2026-09-GATE-reranker-phase1.md`: method, serving identity, the six checks with their
+results, the fallback count and ids, the repeat's noise floor, and the verdict. A fallback counts as
+delta 0, but the teacher's true delta on it is unknown, so if the delta lands within
+`fallbacks / 300` of +0.055, the verdict says so explicitly (lenient-acceptance design §3).
 
 **PASS requires both:** permutation `p < 0.05` **and** delta ≥ **+0.055** (nDCG@10 ≥ 0.753).
 They are independent — the Phase 1 A2 arm cleared significance with a delta of −0.1126.
@@ -386,7 +430,10 @@
 
 | Symptom | Cause | Do |
 |---|---|---|
-| `finish_reason=length` rejections | the **completion** hit `max_tokens` (8192) | raise `MAX_COMPLETION_TOKENS`. This is *not* the prompt-budget failure |
+| `finish_reason=length` rejections | the **completion** hit `max_tokens` (16,384): the expected end of a runaway generation | nothing; it is one failed attempt. Do **not** raise `MAX_COMPLETION_TOKENS`: past ≈ 29,000 tokens a runaway outlives the 1,200 s client timeout at ≈ 24.8 tokens/s. This is *not* the prompt-budget failure |
+| `http_error: timed out` | a request ran past the 1,200 s client timeout | check step 6's decode rate; below ≈ 14 tokens/s, lower `--concurrency` |
+| `… queries unscored -- no run file written (0 corrupt resumed entries; K fallbacks against a cap of 15)` | more than 15 queries failed all 4 attempts | re-issue the identical command: resume asks only the failed queries again, with a fresh budget. If it recurs, stop: the measured rate was ≈ 2%, so read the ledger's reasons |
+| `… (N corrupt resumed entries; …)` with N > 0 | an accepted ledger entry that is not a permutation of its pool: a hand edit or a bug | stop and investigate; do not edit the ledger to get past it |
 | HTTP 400 from vLLM | prompt exceeded `max_model_len` | raise `--max-model-len`; unlikely at 128K |
 | `ledger records a different pass` + exit 1 | you reused a `--responses` path, or changed a seed or `--run` mid-pass | use a fresh path per pass; do not edit seeds to "retry" |
 | `<path>:<line>: malformed ledger line` | crash mid-append | delete that last line and re-run; it is by definition not an accepted entry |
@@ -402,9 +449,8 @@
 
 ## What is still unverified going in
 
-- The `8192` completion budget was never measured — no tokenizer is reachable on the dev box. An
-  inadequate budget costs a pass, loudly, via `finish_reason=length`; it cannot silently corrupt the
-  gate.
+- Per-request speed at `--concurrency 12`: ≈ 24.8 tokens/s was measured only up to 5–6 concurrent.
+  Step 6's rate check covers it.
 - The real model's reply shape is exercised for the first time at step 5. That is why step 5 exists
   and why it is one query, not three hundred.
 
````

- [ ] **Step 2: Check no stale values remain**

Run: `command grep -c "8192\|five\|50-element\|Budget: \$2–5" docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md`
Expected: `0` (grep exits 1 on zero matches; that is the expected result).

- [ ] **Step 3: Commit**

```bash
git add -f docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md
git commit -m "update the teacher-ceiling runbook for lenient acceptance, fallbacks and --concurrency 12

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- docs/plans/2026-09-20-teacher-ceiling-task3-runbook.md
```

## Tasks NOT in this plan

- **`--enforce-eager`** in the pod's start command (`vllm serve ... --enforce-eager ...`) is the
  likely cause of the ≈ 25 tokens/s per-request rate. Dropping it could shorten the run several-fold,
  but it is untested, and CUDA graphs take GPU memory, shrinking the KV pool that §6's N = 12 is
  computed from. If it is tried, re-read `kv_cache_size_tokens` after launch and recompute N.
- A top-k prompt ("return the 20 most relevant ids"). The lenient rule keeps the measured prompt;
  a new prompt would be untested.
- Everything the base spec's §12 lists.
