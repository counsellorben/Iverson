# Teacher-Ceiling Attempt-Failure Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-27-teacher-ceiling-attempt-fixes-design.md` (commit SHA: `01180fe86ab7f1b5150c0cf145b65c46bca6d3a5`)

**Goal:** Fix the four failure modes the teacher-ceiling attempt-measurement found (structured-decoding crashes/loops, an undersized completion budget with no diagnostic visibility, sequential-only execution, and an unguarded HTTP/timeout crash path), then re-run the 8-query measurement under the fixes.

**Architecture:** Two independent files change. `teacher_rerank.py`'s `call_teacher` drops vLLM guided decoding, widens its return shape to carry real usage/reasoning diagnostics, and catches HTTP/timeout errors instead of crashing; `score_query`/`make_record` thread the new fields through the ledger. `run_measurement_batch.sh`'s orchestration loop is restructured from one-query-at-a-time to batches of up to 6 concurrent queries, with the whole script (not just each model call) now the thing that gets detached from the invoking shell.

**Tech stack:** Python 3 stdlib + pytest (no new dependency, per the file's own existing constraint); bash; vLLM 0.30.0's OpenAI-compatible `/v1/chat/completions` route.

---

## File Structure

- **Modify:** `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py` — drop `structured_outputs`/`RESPONSE_SCHEMA`/`STRUCTURED_OUTPUT_PARAM`; raise `MAX_COMPLETION_TOKENS`; widen `call_teacher`'s return shape with usage/reasoning fields and HTTP/timeout error handling; thread the new fields through `make_record`/`score_query`; drop the sidecar's now-dead key.
- **Modify:** `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py` — remove the now-obsolete schema-length test; widen the 8 non-empty `ScriptedTeacher(...)` fixtures to match `call_teacher`'s new 5-tuple return shape.
- **Modify:** `Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh` — restructure the per-seed loop into concurrency-6 batches; self-detach the whole script instead of each invocation; default to fresh `/workspace/artifacts-v2/` output paths.

## Inherited from spec

The following were verified by the design spec (and its two critical-design-review rounds) and are **not** re-verified here:

| # | Assumption | Evidence (from spec) |
|---|---|---|
| 1 | `RESPONSE_SCHEMA`'s only consumers are `:58` (comment), `:66` (def), `:376` (docstring), `:390` (call site) | `grep -n RESPONSE_SCHEMA teacher_rerank.py` |
| 2 | `validate_permutation` already detects and rejects duplicate ids today, independent of the schema | Read `teacher_rerank.py:198-236` |
| 3 | `test_response_schema_constrains_array_length_to_exactly_fifty` asserts directly on `RESPONSE_SCHEMA` and must be removed | Read `test_teacher_rerank.py:379-391` |
| 4 | `read_responses_ledger` has no strict-schema check that new record fields would trip | Read `teacher_rerank.py:241-277` |
| 5 | `score_query` has exactly 4 `append_response` call sites (`:446, :452, :457, :461`) needing the new fields threaded through | Read `teacher_rerank.py:408-463` |
| 6 | vLLM 0.30.0's chat-completion response exposes `message.reasoning` (not `reasoning_content`) and a `usage` object with `completion_tokens`/`prompt_tokens` | Live request against this session's pod |
| 7 | `usage.completion_tokens_details.reasoning_tokens` is unreliable (read `0` on a response with visible reasoning text) | Same live request |
| 8 | Real per-query prompt sizes (18,547 / 23,427 / 19,653 tokens) are far below `MAX_MODEL_LEN` (131,072) | Live probe against 3 real queries' actual prompts |
| 9 | The pod's KV-cache pool supports 6 concurrent requests at the new budget (`6×56,195 ≈ 51%` of `655,317`) | `/metrics`, `kv_cache_size_tokens` |
| 10 | `RETRY_BUDGET` is 2, unchanged by this design | `grep -n RETRY_BUDGET teacher_rerank.py` → `:78` |
| 11 | No accepted upstream fix exists for the xgrammar/gpt-oss FSM failure | `vllm-project/vllm#22513`, `#37359` |
| 12 | Both `finish_reason=length` and the query-51 loop trace to xgrammar rejecting a token mid-generation | Live reproduction, server-side log (`backend_xgrammar.py:168`, token `200012`) |
| 13 | `urllib.error.HTTPError` exposes `.code`/`.reason`/`.read()` | Constructed real instance and read them |
| 14 | A read-phase timeout raises a bare exception `except HTTPError` alone misses; `except OSError` catches it plus `HTTPError` itself | `socket.timeout is TimeoutError` → `True`; both are `OSError` subclasses |

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (not covered by the spec's own table above):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path / content | `run_measurement_batch.sh` exists with the exact structure this plan's Task 2 diffs against (the `for SEED in $SEEDS; do for PASS in 1 2; do ... done; done` loop, the `A`/`SEEDS` env-var defaults, the `setsid nohup ... & disown` per-invocation launch) | Read the full file (95 lines) directly in this session |
| 1b | File path / content | `teacher_rerank.py`'s exact current text at every line range Task 1 diffs against (`:39`, `:42-80`, `:372-403`, `:299-307`, `:434-463`, `:526-529`), and `test_teacher_rerank.py`'s at `:379-392` and each of the 8 `ScriptedTeacher` sites, matches byte-for-byte what this plan's code blocks show as "current" | Read both files in full (629 and 939 lines respectively) directly in this session, immediately before drafting Task 1 |
| 2 | Function signature | `ScriptedTeacher.__call__` (`test_teacher_rerank.py:310-314`) returns `self.replies.pop(0)` directly as `call_teacher`'s stand-in — a queued tuple, not a wrapped call | Read `:297-314` |
| 3 | Sibling-set (all 8 non-empty `ScriptedTeacher` sites) | Every one of the 8 non-empty reply lists (`:408, 427, 504, 552, 586, 671, 889, 924`) contains only 2-element `(content_or_None, finish_reason)` tuples — no 3+-element or differently-shaped tuple exists among them | Read the exact text at each of the 8 line numbers directly (including the varied shapes: `None` content at `:552`, malformed-JSON strings at `:586`, `"length"` finish_reason at `:889`, single-line formatting at `:924`) |
| 4 | Test command | `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q` is the real, documented invocation | Read the test file's own module docstring, line 3 |
| 5 | Task ordering | Task 1 (Python) and Task 2 (bash) have no hidden cross-dependency — `run_measurement_batch.sh` references none of the symbols Task 1 changes | `grep -n "RESPONSE_SCHEMA\|STRUCTURED_OUTPUT_PARAM\|structured_outputs\|MAX_COMPLETION_TOKENS\|completion_tokens\|reasoning\|http_error" run_measurement_batch.sh` → no hits |
| 6 | Code-in-plan validity | The bash array-chunking technique (`"${SEED_ARR[@]:$i:$CONCURRENCY}"`, then `wait "${PIDS[@]}"`) splits 8 seeds into batches of 6 then 2, correctly | Ran the exact snippet locally: produced `batch: 101 102 103 104 105 106` then `batch: 107 108` |
| 7 | Consumer impact (Cat 6 — Task 1 modifies existing code) | `teacher_rerank` has exactly 2 importers repo-wide: `test_teacher_rerank.py` (handled by this plan's own Task 1) and `summarize_teacher_attempts.py`, which uses only `tr.read_responses_ledger`/`tr.accepted_entry` — neither touched by this plan | `grep -rn "import teacher_rerank\|from teacher_rerank"` across `Iverson.Server/`; then `grep -n "\btr\."` inside `summarize_teacher_attempts.py` → only those 2 calls |
| 8 | Consumer impact (Cat 6) | No test asserts on a ledger record's full dict equality or exact key set — adding 3 new keys to every `make_record` output breaks nothing | `grep -n "assert records ==\|assert record ==\|== \[{"` and `grep -n "\.keys()\|record\["` in `test_teacher_rerank.py` → no hits either way |
| 9 | Code-in-plan validity | `teacher_rerank.py`'s existing imports (`:33-40`) are already fully explicit per-submodule (`import urllib.request`, not `import urllib`) — this plan's new `import urllib.error` line matches that established convention, rather than relying on `urllib.request`'s transitive exposure of it | Read `:33-40`: every import is a specific module/submodule, none is a bare package import relying on attribute access into a submodule |
| 10 | Commit convention | This directory's commits are lowercase, imperative, descriptive — no Conventional-Commits prefix | `git log --oneline -10 -- teacher_rerank.py run_measurement_batch.sh` |

## Tasks

### Task 1: Drop `structured_outputs`, widen `call_teacher`'s diagnostics and error handling

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py`
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py`
- Test: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q`

- [ ] **Step 1: Remove the guided-decoding constants and their comment block; add `urllib.error`**

  Add the import (after the existing `import urllib.request` at `:39`):
  ```python
  import urllib.error
  ```

  Replace the serving-constants block (`:42-80`) — this removes `STRUCTURED_OUTPUT_PARAM`, `RESPONSE_SCHEMA`, and both their comment blocks, and raises `MAX_COMPLETION_TOKENS`:
  ```python
  # ── Serving constants (spec §4) ──────────────────────────────────────────────────────────────

  MAX_COMPLETION_TOKENS = 32768  # raised from 8192 (fixes design doc §2): the largest real prompt
  # measured live (23,427 tokens) plus this budget is 56,195, well under MAX_MODEL_LEN's 131,072.
  # Reasoning and the final answer share this one budget -- a reasoning model's chain-of-thought
  # can consume a large, variable share of it before any answer text appears.
  MAX_MODEL_LEN = 131072  # vLLM's --max-model-len for this teacher (128K); recorded in the sidecar.

  RETRY_BUDGET = 2  # one initial attempt + one retry (spec §6 row 1: "One retry ... on a second
  # failure the query is recorded unscored"). A resumed invocation gets a FRESH budget of 2, never a
  # reduced one carried over from a previous invocation's rejected ledger entries (spec §6 row 4).
  ```
  (`RETRY_BUDGET` itself is unchanged — it's repeated here only because it sat between the two blocks being removed.)

- [ ] **Step 2: Widen `call_teacher`'s return shape and add HTTP/timeout error handling**

  Replace the whole function (`:372-403`):
  ```python
  def call_teacher(base_url, model, prompt, seed, api_key=None):
      """POSTs to `{base_url}/v1/chat/completions` with stdlib `urllib.request`, the same transport
      shape as `enrich_bench.py:56-62` (temperature 0, stream false), plus `max_tokens:
      MAX_COMPLETION_TOKENS` (NOT enrich_bench.py's 256, spec §4) and a fixed `seed`. Returns
      `(content, finish_reason, reasoning, completion_tokens, prompt_tokens)` -- one consistent
      5-tuple shape across every path: a normal completion, a `finish_reason="length"` truncation,
      and a caught HTTP/timeout error (`finish_reason="http_error"`, with `reasoning`/
      `completion_tokens`/`prompt_tokens` all `None` -- no usage data exists for a failed request).
      The caller checks `finish_reason in ("length", "http_error")` before ever parsing `content`
      (spec §6 row 1 / design doc §4).

      `usage`/`reasoning` are read defensively (`.get()` chains, never direct indexing) so a reply
      missing either key degrades to `None` instead of raising `KeyError` -- vLLM 0.30.0 exposes
      `message.reasoning` (not `reasoning_content`) and a top-level `usage.completion_tokens`/
      `usage.prompt_tokens`; `usage.completion_tokens_details.reasoning_tokens` was checked live
      and found unreliable (read 0 on a response that plainly contained reasoning text), so it is
      not used.

      `api_key`, when given, is sent as `Authorization: Bearer <api_key>` -- some vLLM deployments
      (e.g. a RunPod template that launches `vllm serve --api-key` from a `VLLM_API_KEY` env var
      before the operator ever runs this script) require it; the header is simply omitted when
      `api_key` is None, so unauthenticated servers are unaffected.

      HTTP-level failures are caught, not left to crash the whole run (design doc §4): `HTTPError`
      (e.g. an xgrammar/Harmony FSM crash a `structured_outputs` request used to raise) records the
      status/reason/body; the broader `OSError` (covers `socket.timeout`/`TimeoutError`, an
      `OSError` subclass, and any other network-level failure `urlopen` can raise) records
      `str(e)`. `HTTPError` must stay listed first -- it is itself an `OSError` subclass, so the
      more specific clause has to come before the broader catch-all to be distinguished."""
      body = {
          "model": model,
          "messages": [{"role": "user", "content": prompt}],
          "temperature": 0,
          "stream": False,
          "max_tokens": MAX_COMPLETION_TOKENS,
          "seed": seed,
      }
      req = urllib.request.Request(
          f"{base_url}/v1/chat/completions",
          data=json.dumps(body).encode("utf-8"),
          method="POST",
      )
      req.add_header("Content-Type", "application/json")
      if api_key:
          req.add_header("Authorization", f"Bearer {api_key}")
      try:
          with urllib.request.urlopen(req, timeout=600) as resp:
              parsed = json.loads(resp.read())
      except urllib.error.HTTPError as e:
          body_text = e.read().decode("utf-8", errors="replace")[:2000]
          return f"HTTP {e.code}: {e.reason} -- {body_text}", "http_error", None, None, None
      except OSError as e:
          return str(e), "http_error", None, None, None
      choice = parsed["choices"][0]
      usage = parsed.get("usage") or {}
      return (
          choice["message"]["content"],
          choice.get("finish_reason"),
          choice["message"].get("reasoning"),
          usage.get("completion_tokens"),
          usage.get("prompt_tokens"),
      )
  ```

- [ ] **Step 3: Widen `make_record` with the three new fields**

  Replace (`:299-307`):
  ```python
  def make_record(query_id, status, content, order, reason, pass_id,
                   reasoning=None, completion_tokens=None, prompt_tokens=None):
      return {
          "query_id": str(query_id),
          "status": status,
          "content": content,
          "order": order,
          "reason": reason,
          "pass": pass_id,
          "reasoning": reasoning,
          "completion_tokens": completion_tokens,
          "prompt_tokens": prompt_tokens,
      }
  ```

- [ ] **Step 4: Thread the new fields through `score_query`, and add the `http_error` branch**

  Replace the retry loop (`:434-463`):
  ```python
      last_reason = None
      for _attempt in range(RETRY_BUDGET):
          content, finish_reason, reasoning, completion_tokens, prompt_tokens = call_teacher(
              args.base_url, args.model, prompt, args.seed, api_key=args.api_key
          )
          if finish_reason == "length":
              last_reason = (
                  f"finish_reason=length (the completion hit the {MAX_COMPLETION_TOKENS}-token "
                  "max_tokens budget before the reply was complete -- raise MAX_COMPLETION_TOKENS. "
                  f"A prompt over the {MAX_MODEL_LEN}-token max_model_len is the SEPARATE HTTP 400 "
                  "failure, not this one.)"
              )
              append_response(args.responses, make_record(
                  query_id, "rejected", content, None, last_reason, pass_id,
                  reasoning, completion_tokens, prompt_tokens))
              continue

          if finish_reason == "http_error":
              last_reason = f"http_error: {content}"
              append_response(args.responses, make_record(
                  query_id, "rejected", content, None, last_reason, pass_id,
                  reasoning, completion_tokens, prompt_tokens))
              continue

          ids, parse_error = parse_json_array(content)
          if parse_error is not None:
              last_reason = parse_error
              append_response(args.responses, make_record(
                  query_id, "rejected", content, None, last_reason, pass_id,
                  reasoning, completion_tokens, prompt_tokens))
              continue

          ok, result = validate_permutation(ids, expected_ids)
          if ok:
              append_response(args.responses, make_record(
                  query_id, "accepted", content, result, None, pass_id,
                  reasoning, completion_tokens, prompt_tokens))
              return result, None

          last_reason = result
          append_response(args.responses, make_record(
              query_id, "rejected", content, None, last_reason, pass_id,
              reasoning, completion_tokens, prompt_tokens))

      return None, last_reason
  ```

- [ ] **Step 5: Drop the sidecar's now-dead key**

  In `write_sidecar` (`:526-529`), remove the `structuredOutputParam` line:
  ```python
              "maxModelLen": MAX_MODEL_LEN,
              "maxCompletionTokens": MAX_COMPLETION_TOKENS,
              "promptTemplateSha256": PROMPT_TEMPLATE_SHA256,
  ```

- [ ] **Step 6: Remove the obsolete schema-length test**

  Delete `test_teacher_rerank.py:379-392` in full (the separator comment block, the live-evidence comment, and `test_response_schema_constrains_array_length_to_exactly_fifty` itself) — `RESPONSE_SCHEMA` no longer exists for it to assert on.

- [ ] **Step 7: Widen the 8 non-empty `ScriptedTeacher(...)` reply lists**

  Every 2-element `(content, finish_reason)` tuple in these 8 sites becomes a 5-element `(content, finish_reason, None, None, None)` tuple — `ScriptedTeacher` is a test double standing in for `call_teacher` directly, so its queued replies must match `call_teacher`'s new return arity exactly. The 3 new positions are always `None` in these fixtures (no test here inspects the new diagnostic fields):

  | Line | Test function | Current tuples | New tuples |
  |---|---|---|---|
  | `:408-411` | `test_main_passes_api_key_through_to_call_teacher` | `('["d1", "d2", "d3"]', "stop")`, `('["e1", "e2", "e3"]', "stop")` | append `, None, None, None` to each |
  | `:427-430` | `test_main_omits_api_key_when_not_given` | same two tuples as above | append `, None, None, None` to each |
  | `:504-507` | `test_main_presents_documents_to_the_model_in_shuffled_not_fusion_order` | `('["d2", "d3", "d1"]', "stop")`, `('["e2", "e3", "e1"]', "stop")` | append `, None, None, None` to each |
  | `:552-556` | `test_main_records_a_null_content_reply_to_responses_instead_of_crashing` | `(None, "stop")`, `(None, "stop")`, `('["e2", "e3", "e1"]', "stop")` | append `, None, None, None` to each (including the `None`-content ones) |
  | `:586-590` | `test_main_refuses_to_write_a_run_file_when_any_query_is_unscored` | `('["d3", "d1", "d2"]', "stop")`, `("not valid json", "stop")`, `("still not valid json", "stop")` | append `, None, None, None` to each |
  | `:671-673` | `test_main_resumes_skipping_accepted_and_reissuing_all_rejected_queries` | `('["e2", "e1", "e3"]', "stop")` | append `, None, None, None` |
  | `:889-893` | `test_length_finish_reason_blames_max_tokens_not_the_prompt` | `('["d2", "d3"', "length")`, `('["d2", "d3"', "length")`, `('["e2", "e3", "e1"]', "stop")` | append `, None, None, None` to each |
  | `:924` | `test_main_stamps_each_ledger_record_with_this_pass_and_resumes_from_it` | single-line: `ScriptedTeacher([('["d2", "d3", "d1"]', "stop"), ('["e2", "e3", "e1"]', "stop")])` | same single-line form, each tuple widened to 5 elements |

  The other 7 `ScriptedTeacher([])` sites (`:629, 734, 767, 799, 829, 867, 935`) are empty "must never be called" fixtures with no tuples inside — leave them untouched. The 2 `_RecordingAuthHandler`-backed tests (`:462-473`, `:476-487`) need no change either — neither unpacks `call_teacher`'s return value, and Step 2's defensive `.get()` parsing means their `usage`-less, `reasoning`-less reply body (`:449-451`) degrades to `None` rather than crashing.

- [ ] **Step 8: Run the suite**
  ```bash
  python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py -q
  ```

- [ ] **Step 9: Commit**
  ```bash
  git add Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py Iverson.Server/Iverson.LoadTest/scripts/test_teacher_rerank.py
  git commit -m "drop structured_outputs, widen call_teacher for usage/reasoning diagnostics, catch HTTP/timeout errors"
  ```

### Task 2: Parallelize `run_measurement_batch.sh` to 6 concurrent, self-detach, fresh paths

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh`

- [ ] **Step 1: Update the header comment's recommended invocation, and the artifacts-dir default**

  Replace the `# Optional:` comment block's `A` line and add a `CONCURRENCY` line:
  ```
  #   A              -- artifacts dir (default /workspace/artifacts-v2; created if missing) -- a
  #                     NEW default, not the original measurement's /workspace/artifacts, so a
  #                     re-run against the same seeds does not resume into pre-fix ledger entries
  #   CONCURRENCY    -- max concurrent teacher_rerank.py invocations per pass (default 6)
  ```
  Replace the `# Usage` comment block to show the now-required detached invocation (the prior run's
  actual failure was this outer script dying to an SSH SIGHUP between seeds — detaching each
  model-call invocation individually, as the script previously did, does not protect the loop that
  launches them):
  ```
  # Usage (after the runbook's steps 2-4: pod provisioned, vLLM serving, structured-output param
  # and seed support confirmed) -- run the WHOLE script detached, not just each invocation inside
  # it, so an SSH disconnect can't kill the loop between seeds:
  #   setsid nohup bash -c 'MODEL_ID=<model-id> VLLM_VERSION=<ver> INSTANCE_SKU=<sku> \
  #     bash run_measurement_batch.sh' > /workspace/artifacts-v2/batch.log 2>&1 < /dev/null &
  #   disown
  ```
  Change the `A` default assignment:
  ```bash
  A="${A:-/workspace/artifacts-v2}"
  ```
  Add, alongside the other env-var defaults:
  ```bash
  CONCURRENCY="${CONCURRENCY:-6}"
  ```

- [ ] **Step 2: Restructure the per-seed loop into concurrency-`CONCURRENCY` batches**

  Replace the `for SEED in $SEEDS; do ... done` block:
  ```bash
  read -ra SEED_ARR <<< "$SEEDS"

  for PASS in 1 2; do
    i=0
    while [ "$i" -lt "${#SEED_ARR[@]}" ]; do
      BATCH=("${SEED_ARR[@]:$i:$CONCURRENCY}")
      echo "--- pass $PASS batch [${BATCH[*]}]: launching ---"
      PIDS=()
      for SEED in "${BATCH[@]}"; do
        python3 "$S/teacher_rerank.py" \
          --run "$B/runs/rerank-a0prime.chunks.trec" \
          --corpus "$B/beir/corpus.jsonl" --queries "$B/beir/queries.jsonl" \
          --base-url "$BASE_URL" --model "$MODEL_ID" \
          --seed "$SEED" --shuffle-seed "$SEED" --subsample 1 --subsample-seed "$SEED" \
          --vllm-version "$VLLM_VERSION" --quantisation mxfp4 --instance-type "$INSTANCE_SKU" \
          "${API_KEY_ARGS[@]}" \
          --responses "$A/q$SEED.responses.jsonl" --out "$A/q$SEED.chunks.trec" \
          > "$A/q$SEED.pass$PASS.log" 2>&1 < /dev/null &
        PIDS+=("$!")
      done
      wait "${PIDS[@]}"
      for SEED in "${BATCH[@]}"; do
        echo "seed $SEED pass $PASS done -- $(tail -1 "$A/q$SEED.pass$PASS.log")"
      done
      i=$((i + CONCURRENCY))
    done
  done
  ```
  This drops the per-invocation `setsid nohup ... & disown` and the `pgrep`-based polling loop —
  neither is needed once the whole script's own session is already detached (Step 1's new usage
  comment) and `wait` on tracked PIDs replaces polling for a batch's completion. Per-query file
  isolation is unchanged (each `--responses`/`--out` pair is still seed-specific), so no new
  coordination between concurrently-running queries is introduced.

- [ ] **Step 3: Commit**
  ```bash
  git add Iverson.Server/Iverson.LoadTest/scripts/run_measurement_batch.sh
  git commit -m "parallelize run_measurement_batch.sh to 6 concurrent queries, self-detach the outer script, use fresh artifacts-v2 paths"
  ```

## Tasks NOT in this plan

(Explicitly out of scope, unchanged from the original measurement design, still applies:) picking final values for `RETRY_BUDGET`; relaxing spec `2026-09-20-teacher-ceiling-design.md` §6 row 2's refusal rule; the 300-query production run itself.

## Known issues inherited from spec

**From Design §1:** removing the length/uniqueness grammar constraint reintroduces the exact cost `test_response_schema_constrains_array_length_to_exactly_fifty`'s comment documents from the original 2026-09-21 session — a wrong-length or duplicate-id array now costs a full generation before `validate_permutation` catches it, same as before `minItems`/`maxItems` was added. No middle ground was found: the xgrammar failure looks tied to using `structured_outputs` against this model *at all*, not to which constraints the schema specifies (searched; no vLLM/gpt-oss combination config was found that keeps grammar enforcement while avoiding the FSM collision). Given the alternative (confirmed, currently-unfixable crash/loop risk) has no upstream fix available either, this trade was made deliberately, not by omission — see the source spec's Background.

**From Design §4:** `timeout=600` itself stays unchanged despite Design §2's 4x larger completion budget and Design §3's 6-way concurrency — no wall-clock generation timing exists anywhere in the source spec or the results doc to justify raising it, and obtaining that measurement was out of scope for the design step (no live pod). If a legitimate long generation hits this ceiling before finishing, the broadened guard now records it as an ordinary rejection rather than crashing — a truncation failure disguised as a timeout, not lost data. Revisit if the re-measurement shows this actually occurring.
