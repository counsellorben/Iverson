# Teacher-ceiling attempt-failure fixes and re-measurement — design

**Source:** follow-on to `docs/specs/2026-09-27-teacher-ceiling-attempt-measurement-results.md` (the
8-new-query + 2-original measurement: 1/10 succeeded). This design addresses the failure modes that
measurement surfaced, and re-measures under the fixes.

## Background

The results doc identified four failure classes across 10 real queries against `openai/gpt-oss-120b`
on vLLM. Live investigation on a fresh pod (vLLM 0.30.0) traced two of them to a single root cause:

- **`finish_reason=length` (7/10 queries)** and **the query-51 degenerate loop** (10,529 lines of
  output, only 42 unique — a blank-whitespace filler loop that never closed its JSON array) both trace
  to vLLM's **xgrammar** structured-decoding backend (`teacher_rerank.py`'s `structured_outputs`
  parameter) colliding with gpt-oss's Harmony output format. Reproduced live: a fresh request against
  query 51's exact prompt raised `backend_xgrammar.py:168: Failed to advance FSM ... grammar rejected
  tokens [200012] ... Terminating request` — an uncaught HTTP 500, the same class of crash query 521
  hit in the original measurement. Token 200012 is very likely one of Harmony's channel-transition
  control tokens, which the plain JSON-array grammar was never told about.
- This is a known, actively-tracked, **unresolved** vLLM bug, not a local misconfiguration:
  [vllm-project/vllm#22513](https://github.com/vllm-project/vllm/issues/22513) is titled almost
  exactly this symptom for gpt-oss 20b/120b. The one workaround reported there (`--async-scheduling`
  removal) doesn't apply — this session's server was never launched with that flag. No fix exists for
  this session's exact setup.
- Duplicate-id failures (query 770 here, and very plausibly query 936's "stuck at 46/50" from the
  original live session) are **not** a validation gap — `validate_permutation`
  (`teacher_rerank.py:198-209`) already detects and correctly rejects duplicates today. The gap is
  that nothing currently *prevents* the model from generating them in the first place.

## Scope

**In scope:**
1. Drop `structured_outputs` (and its now-dead supporting code) from `teacher_rerank.py`.
2. Raise `MAX_COMPLETION_TOKENS` and log real per-attempt diagnostics (token usage, raw reasoning
   text) to the ledger, so this and future rounds can see how close a budget runs instead of guessing.
3. Parallelize the re-measurement — up to 6 concurrent queries instead of 1 at a time.
4. Re-run all 8 of the original measurement's queries (seeds 101-108) under the fixes, into fresh
   ledger files (not resumed into the existing ones, to avoid mixing pre-fix and post-fix attempts in
   one file).

**Explicitly out of scope** (unchanged from the original measurement design, still applies):
picking final values for `RETRY_BUDGET`; relaxing spec `2026-09-20-teacher-ceiling-design.md` §6 row
2's refusal rule; the 300-query production run itself.

## Design

### 1. Drop `structured_outputs`

Remove from `teacher_rerank.py`:
- The `structured_outputs` key from `call_teacher`'s request body (`:390`).
- `RESPONSE_SCHEMA` (`:66`) and `STRUCTURED_OUTPUT_PARAM` (`:61`) — both become dead code once their
  only call site is gone (confirmed: `grep RESPONSE_SCHEMA` shows no other consumer).
- The vLLM guided-decoding-param-rename comment block (`:48-60`) — describes how to wrap
  `RESPONSE_SCHEMA` for a parameter that no longer exists in this script.
- `call_teacher`'s docstring reference to guided decoding (`:375-376`).
- `test_teacher_rerank.py`'s `test_response_schema_constrains_array_length_to_exactly_fifty`
  (`:379-391`) — asserts on a constant that no longer exists.

**Known issue, accepted as out of scope:** removing the length/uniqueness grammar constraint
reintroduces the exact cost `test_response_schema_constrains_array_length_to_exactly_fifty`'s comment
documents from the original 2026-09-21 session — a wrong-length or duplicate-id array now costs a full
generation before `validate_permutation` catches it, same as before `minItems`/`maxItems` was added.
No middle ground was found: the xgrammar failure looks tied to using `structured_outputs` against this
model *at all*, not to which constraints the schema specifies (searched; no vLLM/gpt-oss combination
config was found that keeps grammar enforcement while avoiding the FSM collision). Given the
alternative (confirmed, currently-unfixable crash/loop risk) has no upstream fix available either,
this trade was made deliberately, not by omission — see Background.

### 2. Raise `MAX_COMPLETION_TOKENS`, log real diagnostics

`MAX_COMPLETION_TOKENS` (`:44`) rises from 8192 to **32768**. Headroom check: the largest real prompt
measured live (query 770, 23,427 tokens) plus 32,768 completion tokens = 56,195, well under
`MAX_MODEL_LEN`'s 131,072.

`call_teacher` (`:372-403`) additionally reads and returns `usage.completion_tokens`,
`usage.prompt_tokens`, and `message.get("reasoning")` from the response (confirmed live: vLLM 0.30.0's
actual chat-completion response has `message.reasoning` — not `reasoning_content` as an older comment
in this file speculated — and a top-level `usage` object; `usage.completion_tokens_details
.reasoning_tokens` was also checked and found unreliable — it read `0` on a response that plainly
contained reasoning text, so it is not used).

`make_record` (`:299-307`) gains `completion_tokens`, `prompt_tokens`, and `reasoning` fields.
`read_responses_ledger` (`:241-277`) only requires `query_id` to exist on a record — confirmed no
strict-schema check that new fields would trip. All three `append_response` call sites inside
`score_query`'s retry loop (`:446` length-rejection, `:452` parse-error-rejection, `:457` accepted)
thread the new fields through consistently.

Not in scope: updating `summarize_teacher_attempts.py` to surface these new fields in its table. The
raw ledger already carries them for direct inspection (as this session already did, informally, to
diagnose the query-51 loop) — no formatting work is needed to satisfy this round's diagnostic goal.

### 3. Parallelize to 6 concurrent, re-run all 8 queries

`run_measurement_batch.sh`'s per-query sequential loop is restructured: for each of the 2
passes, launch up to 6 of the 8 queries' invocations concurrently (backgrounded with plain `&`,
tracking PIDs, then `wait` on that batch) before starting the remaining 2 and moving to the next pass.
Each query's own 2 passes stay in order relative to *itself* (unchanged); different queries have no
shared state (separate `--responses`/`--out` per seed, already true in the original design) so no
new coordination is needed between them.

Headroom check (KV-cache): real prompt sizes run 18,547-23,427 tokens (measured live for queries 783,
770, and 51). Worst case, 6 concurrent requests at the new 32,768-token completion budget:
`6 × (23,427 + 32,768) = 337,170` tokens, ≈51% of the pod's measured 655,317-token KV-cache pool
(`kv_cache_size_tokens` from `/metrics`) — comfortable margin.

The **outer orchestrating script itself** is launched via `setsid nohup ... & disown` this time (not
just the individual model-call invocations, which is what happened in the original measurement) — the
prior run's actual failure was the outer loop dying to an SSH SIGHUP between seeds, not any individual
invocation. Once the outer script's own session is detached, invocations backgrounded with plain `&`
inside it are already immune to the same failure without needing individual detachment.

Fresh output paths: a new `/workspace/artifacts-v2/` directory (parallel to the original
`/workspace/artifacts/`), so `--responses`/`--out` for each seed land at
`/workspace/artifacts-v2/q101.responses.jsonl` etc. Reusing the original `/workspace/artifacts/`
paths against the same seeds would resume into those files (per `validate_ledger_pass`'s identity
check on `--run`/`--shuffle-seed`/`--subsample`/`--subsample-seed`, all unchanged), mixing pre-fix
xgrammar-era rejections with post-fix attempts in one ledger.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `RESPONSE_SCHEMA`'s only consumer is the `structured_outputs` request parameter | `grep -n RESPONSE_SCHEMA teacher_rerank.py` — 4 hits: definition (`:66`), a comment (`:58`), a docstring mention (`:376`), and the one call site (`:390`). No other consumer. |
| 2 | `validate_permutation` already detects and rejects duplicate ids today, independent of the schema | Read `teacher_rerank.py:198-209`: docstring states "Checked in order -- wrong length, then duplicates, then set mismatch"; matches query 770's observed `"duplicated id(s)"` rejection reason exactly. |
| 3 | `test_response_schema_constrains_array_length_to_exactly_fifty` asserts directly on `RESPONSE_SCHEMA` and must be removed, not just updated | Read `test_teacher_rerank.py:379-391`: asserts `tr.RESPONSE_SCHEMA["minItems"] == 50` etc. — the constant it asserts on is being deleted. |
| 4 | `read_responses_ledger` has no strict-schema check that new record fields would trip | Read `teacher_rerank.py:241-277`: only checks `"query_id" not in record`; every other field is read loosely via `.get()` elsewhere (e.g. `accepted_entry`, `:280-288`). |
| 5 | `score_query` has exactly 3 `append_response` call sites needing the new fields threaded through consistently | Read `teacher_rerank.py:408-459`: length-rejection (`:446`), parse-error-rejection (`:452`), accepted (`:457`) — confirmed exhaustive by reading the whole function body, not just grepping `append_response`. |
| 6 | vLLM 0.30.0's actual chat-completion response exposes reasoning text as `message.reasoning`, and a `usage` object with `completion_tokens`/`prompt_tokens` | Live request against `/v1/chat/completions` on this session's pod: response had `"message": {"content": "test", "reasoning": "The user says: ...", ...}` and `"usage": {"prompt_tokens": 74, "completion_tokens": 44, ...}`. |
| 7 | `usage.completion_tokens_details.reasoning_tokens` is not a reliable signal | Same live request: field read `0` despite the response's `reasoning` field containing 27 words of visible reasoning text. |
| 8 | Real per-query prompt sizes are far below `MAX_MODEL_LEN` (131,072), giving headroom for both a larger completion budget and 6-way concurrency | Live probe against 3 real queries' actual prompts (built via `teacher_rerank.py`'s own `build_prompt`): 18,547 / 23,427 / 19,653 `prompt_tokens`. |
| 9 | The pod's KV-cache pool can support 6 concurrent requests at the new completion budget | `/metrics`: `kv_cache_size_tokens="655317"`. `6 × (23427 + 32768) = 337170` ≈ 51% of capacity, using the *worst observed* prompt size, not an average. |
| 10 | `RETRY_BUDGET` (unchanged by this design) is 2, unrelated to any of the three fixes | `grep -n RETRY_BUDGET teacher_rerank.py` — `:78`, `= 2`. |
| 11 | No accepted upstream fix exists for the xgrammar/gpt-oss FSM failure that would let `structured_outputs` be kept safely | Web research: `vllm-project/vllm#22513` (exact symptom match, gpt-oss 20b/120b) — closed without a confirmed general fix; the one reported workaround (`--async-scheduling` removal) doesn't apply since this session's server never used that flag. `#37359` (related mechanism, `is_reasoning_end()`/Harmony channel detection) closed stale, unresolved. Explicitly setting `--reasoning-parser openai_gptoss` is reported elsewhere to cause a *different* error in non-streaming mode, so it is not a workaround either. |
