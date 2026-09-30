# Critical Implementation Review: 2026-09-30-simple-jev-judge-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-30-simple-jev-judge-implementation-plan.md
**Artifact HEAD at review:** 81a34247f7b9d17f6d98232bdb8c5bdd90019f13
**Verified plan-level assumptions section:** present

⚠️ 2 commits since plan-write time (SHA 18ba5177); cited file:line references re-checked under §1.

The two commits are `2434ad1e` (an unrelated design doc) and `81a34247` (this plan). Neither touches a file the plan cites.

The probe scratch is outside the repo, at `/tmp/claude-1000/-home-ben-repositories-Iverson/8ea909e2-a873-4454-bbef-4ce0a5c26326/scratchpad/cir1/`. It holds:

- The plan's four code blocks, extracted verbatim by line range: `jev_rerank.py` (plan lines 460–768), `test_jev_rerank.py` (138–446), `stub_jev_server.py` (883–997) and `mutate.py` (786–831).
- Copies of `teacher_rerank.py`, `stub_vllm_server.py` and `report.py` from `main`.
- Task 2's Steps 2–4 (`run2.sh`, `run3.sh`, `run4.sh`), Task 5's Steps 1–5 (`run5.sh`, `runbt.sh`, `run55.sh`) and Step 6 (`step6.sh`).
- `A/pf/`: Task 4 Step 8's pre-flight invocation, run on the query-129 pool against the stub and against a closed port (R7). These are the plan's own shell lines, preceded by a prelude that points `S` at the scratch copy and `A` at `cir1/A`.

The pinned md5s are:

| File | md5 |
|---|---|
| `mutate.out` | `940ae043…` |
| `A/dryrun/report-identity.txt` | `e423d00f…` |
| `A/dryrun/report-reversed.txt` | `d6f99fa7…` |
| `A/gate/report-gate.txt` | `2557caf4…` |
| `A/gate/report-repeat.txt` | `a2c5f80a…` |
| `A/gate/secondary.txt` | `c455e5da…` |
| `A/gate/transfer.txt` | `c17f356d…` |
| `ranked.md` (the ranked-changes doc with the Task 5 row inserted) | `ec860702…` |
| `probe-score-body.json` | `41dc7c94…` |
| `probe-score-resp.json` | `961898bf…` |
| `probe2-headers.txt` | `8f2540ea…` |
| `probe2-body.bin` | `0104c301…` |
| `probe3-body.bin` | `8c033a01…` |
| `base.Dockerfile` (RunPod's base image) | `e7815a12…` |

Demo traffic: 7 requests to `simple-jev-demo-api.featherless.ai` between 18:52 and 18:55 UTC on 2026-09-30, plus 3 to the page it redirects to. They were sent one at a time, at least 1 s apart, with `User-Agent: curl/8.5.0` and only public SciFact data or the demo page's own example.

## 0. Coverage enumeration

### Tasks × surfaces

| # | Surface | Disposition |
|---|---|---|
| T1.1 | Task 1 prose: the "decisions beyond the spec" (the pass identity, any other 4xx falls back, the over-cap exit, the 0.5 s wait) | ok. [totality] Each decision is in the code and has a test that kills its mutation (`mutate.out`): M16 for the identity, M11 for the 4xx rule, M17 and M18 for the cap, M14 for the wait. These are rules. The two directions for the identity, the 4xx rule and the cap are dispositioned in R5, R3 and R4. The wait's two directions are here: **under** (a request sent without the wait): ok. [compat] M14 is killed by `test_every_request_is_preceded_by_the_minimum_interval` (`sleeps == [0.5, 0.5]` for two attempts). **over** (a wait that is not needed): ok. [compat] That test also shows exactly one wait per request, and a wait only adds time, so it cannot change any ledger entry or run file. |
| T1.2 | Step 1 worktree command and prelude | ok. [existence] P1 was re-run: `git worktree list` has no `simple-jev`, and `git branch --list 'simple-jev*'` is empty. `git -C <repo>` resolves `.worktrees/...` relative to the repo. The branch base is local `main`, which is 7 ahead of `origin/main` and so carries the spec and the plan. |
| T1.3 | Step 2 test file (code) | ok. [compat] Ran the extracted file: `33 passed`, and `--collect-only` collects 33. |
| T1.4 | Step 4 `jev_rerank.py` (code) | ok. [compat] It was exercised by the suite, by the 300-query dry run (T2.3) and by every Task 5 snippet (T5.1–T5.5). |
| T1.5 | Steps 3 and 5 commands | ok. [compat] `test_teacher_rerank.py` from `main`, run from a scratch copy: `44 passed`. |
| T1.6 | Step 6 mutation checker | ok. [totality] Ran all 27 mutations: every one ends `KILLED`, and the last line is `survivors/pattern errors: 0`. The source md5 is unchanged afterwards (`md5sum -c` OK). The `git status` expectation holds: `__pycache__/` is ignored (`.gitignore:82`) and `.pytest_cache` ignores itself. |
| T1.7 | Step 7 commit | ok. [existence] Both paths are under the tracked `scripts/` directory. The message is lowercase imperative (P16). |
| T2.1 | Stub `stub_jev_server.py` (code) | ok. [compat] It served both 300-query passes (T2.3). |
| T2.2 | Task 2 Step 2 (start both stubs, negative probe) | ok. [compat] Ran it. The output was `18791`, `18792`, then `('http', (422, '{"detail": "d0: instructions lack the text of pool document 21456232"}'))`, character for character the plan's expected output. |
| T2.3 | Task 2 Step 3 (two parallel 300-query passes) | ok. [compat] Ran it in 2 min 35 s. Both passes print `(300 queries, 0 fallbacks)`. |
| T2.4 | Task 2 Step 4 (score both with `--pair`) | ok. [compat] Ran it. `identity exit 1`, 0.7452 / 0.9337, `sequence differs on 0 (0.0%)`, `ARM INVALID`. `reversed exit 0`, 0.0011 / 0.9337, `sequence differs on 300 (100.0%)`. The md5s are `3e46051e…` and `c7c7a298…`, exactly the plan's. |
| T3.1 | Task 3 Step 1 `run-arm.sh` | ok. [existence] Read: `$?` after the redirected `python3` line is the script's exit status, so the `arms.status` line records it. The paths are absolute. |
| T3.2 | Task 3 Step 2 (five arms against the demo) | → §3.1. The demo endpoint now answers every request with a 301 to a web page. |
| T3.3 | Task 3 Steps 3–4 (`report.py --run $A/screen`) | ok. [existence] `resolve_run_paths` globs `*.trec` in a directory (`report.py:144-145`). Only arm run files end in `.trec` in `$A/screen/`, since the ledgers are `.jsonl` and the logs `.log`. |
| T3.4 | Task 3 Step 5 winner → HF repo / policy / card table | ok. [bidirectional] Each of the five rows' policies matches spec §6.2 and the `KNOWN_PROFILES` names (`hf_prompt_policies.py:41-52`: 4B, 27B and 26B-A4B → `shared_examples_binary`; 35B-A3B and 12B → `shared_repeat_state`), and every `KNOWN_PROFILES` row maps back to one table row. |
| T4.1 | Task 4 Step 1 (stop for the go-ahead) | ok. [existence] Plan read: Step 1 says "Do not rent anything without an explicit yes" and comes before Step 2's rental. Global Constraints repeats it ("The implementer stops at its Step 1 and asks Ben"). |
| T4.2 | Task 4 Step 2 (rent: PyTorch template, container disk ≥ 150 GB) | → §2.2. |
| T4.3 | Task 4 Step 3 (image check) | ok. [existence] Docker Hub lists `runpod/pytorch:*-torch260…torch2130-ubuntu2404` tags (pushed 2026-09-30). RunPod's base Dockerfile sets `PIP_BREAK_SYSTEM_PACKAGES=1` and links `python` to `/usr/bin/python3.12` (`base.Dockerfile:28,97`). An image that satisfies U1 therefore exists, and Step 3's check with re-rent covers the variants that do not. |
| T4.4 | Task 4 Step 4 (clone, checkout, `pip install -e`, HF sha) | ok. [existence] `git ls-remote` resolves the clone URL. After `git fetch`, `7bb4f0c7` is an ancestor of `origin/main`, so it can still be checked out. `pyproject.toml` declares `simple-jev = "hf_server:main"`. The editable module finds `common/` through `_checkout_root` (`hf_server.py:50-52`). |
| T4.5 | Task 4 Step 5 (policy auto-select check) | ok. [existence] With `--classifier-prompt-policy` omitted, `resolve_prompt_policy` warns `'AUTO PROMPT FORMAT: %s -> %s …'` (`hf_prompt_policies.py:79-80`). Python's last-resort handler sends that warning to stderr, which `2>&1` captures. `/health` exists (`hf_server.py:1049-1051`). `pkill -f "simple-jev --model"` does not match an interactive shell's own command line. |
| T4.6 | Task 4 Step 6 (upload layout) | ok. [existence] Path by path: the tar is built with `-C /tmp/jev-up .` and extracted in `/workspace/in` (= `$I`). That gives `$I/scripts/jev_rerank.py` and `$I/scripts/teacher_rerank.py` side by side, as `jev_rerank`'s sibling import needs. It gives `$I/sf/corpus.jsonl`, `$I/sf/queries.jsonl` and `$I/sf/runs/bge-base.chunks.trec`, which Steps 8, 10 and 11 read. It gives `$I/nf/corpus.jsonl`, `$I/nf/queries.jsonl` and `$I/nf/runs/bge-base.chunks.trec`, which Step 9 reads. Steps 8–11 use no other `$I` path except the two files they create themselves (`q129.chunks.trec`, `nf-first20.chunks.trec`). |
| T4.7 | Task 4 Step 7 (launch) | ok. [existence] Every flag exists (`hf_server.py:1289-1330`, with `--classifier-prompt-policy` at `:1294-1297`). The env var name matches `:864-866`. |
| T4.8 | Task 4 Step 8 (pre-flight, PFT, PROV) | ok. [presence] The `awk` gives 50 rows (P5, re-counted). The cap for 1 query is `1*5//100 = 0`, so any fallback exits 1. PFT reads `prompt_tokens`, which `score_query` fills from `usage.input_tokens`. hf_server always emits that field (`response_scoring.py:160`, fed at `hf_server.py:940`). A real accepted ledger record (`A/dryrun/reversed.responses.jsonl` line 1) has the keys `completion_tokens, content, order, pass, prompt_tokens, query_id, reason, reasoning, status`. |
| T4.9 | Task 4 Step 9 (transfer, `head -n 1000`) | ok. [totality] `head -n 1000 … \| uniq -c` gives 20 runs of exactly 50. The NFCorpus pool has 323 contiguous query runs = 323 distinct ids. |
| T4.10 | Task 4 Step 10 (main pass) | ok. [compat] It is the same invocation shape as the dry run, plus `$PROV`. Run: Step 8's exact `$PROV` string, with sample values, parsed by `build_arg_parser` gives `server_commit 7bb4f0c7…`, `revision`, `policy shared_examples_binary`, `max_batch_size 16`, `max_model_len 65536` and `preflight_input_tokens 51234`. The sidecar test shows they are written verbatim. |
| T4.11 | Task 4 Step 11 (repeat pass, launched while Step 10 may still be running) | ok. [existence] The two passes cannot overlap on the GPU. The HF service has `concurrency=1` (`hf_server.py:1262`), and capacity is 1 + `queue_size=16` (`:835`, `:871`), so the second request waits rather than doubling KV memory. |
| T4.12 | Task 4 Step 12 (revision/policy cross-check) | ok. [existence] Advanced metadata is merged into the top-level `response["metadata"]` (`hf_server.py:949-954`). `model_revision` is the `--revision` argument (`:1273`), and `prompt_policy` is the resolved policy (`:1266`). `build_response`'s own metadata has neither key (`response_scoring.py:165-168`), so nothing overrides them. `score_query` stores `payload.get("metadata")` in `content`. |
| T4.13 | Task 4 Steps 13–15 (bring back, record, terminate) | ok. [existence] These follow the runbooks. Stage-2 runbook `:432-438`: `tar czf … && md5sum … && runpodctl send`, then on the dev box `runpodctl receive <code> && md5sum … && tar xzf …`, the same shape as Step 13. Stage-1 runbook `:449`: "Then **terminate** the pod — not \"stop\"", which Step 15 matches. |
| T5.1 | Task 5 Step 1 (gate) | ok. [compat] Ran with the reversed dry run standing in for `jev-main`. The `[pool]` and `[compare] … (nDCG@10)` blocks print, the build line reads `7d3a15092f963723`, and the fallback print is `0 []`. |
| T5.2 | Task 5 Step 2 (worst-case bound) | ok. [compat] Ran with 0 fallbacks (`report-bound.txt`, exit 0). Also ran with the sidecar's `fallbackQueryIds` set to `['1','3']` (`runbt.sh`): query 1 comes out in reversed baseline order (its rank 1 is baseline's rank-50 doc `35621259`), with 15,000 rows. |
| T5.3 | Task 5 Step 3 (secondary metrics) | ok. [compat] Ran it. The `bge-base.chunks.trec` line is `AP 0.7018, R@10 0.877, P@10 0.0993, R@50 0.9337, nDCG@10 0.7452`, spec §3's identity row. |
| T5.4 | Task 5 Step 4 (restricted qrels, `--baseline` comparison) | ok. [compat] Ran it on a real 50-query stub repeat pass: `50 query ids, 55 qrels rows`, exit 0. `report-repeat.txt` prints the repeat-vs-main `delta` and `queries changed 0 / 50`. |
| T5.5 | Task 5 Step 5 (transfer Spearman) | ok. [compat] Ran it on the identity/reversed ledgers: `300 queries in both ledgers  mean rho -1.000  mean\|diff\| 0.5000`. |
| T5.6 | Task 5 Step 6 (md5 pin) | ok. [compat] Each glob against the step that writes into it: `$A/pod/*.chunks.trec` and `$A/pod/*.meta.json` ← Task 4 Step 13's tar of `$O`, which Steps 8–11 fill (the `pre-$MBS`, `transfer`, `jev-main` and `jev-repeat` run files and sidecars); `$A/gate/*.chunks.trec` ← Task 5 Step 2 (`bound.chunks.trec`); `$A/gate/*.txt` ← Steps 1–5 (`report-gate`, `report-bound`, `secondary`, `report-repeat`, `transfer`); `$A/screen/*.chunks.trec` ← Task 3's `run-arm.sh` (`--out $OUT/$1.chunks.trec`); `$A/dryrun/*.chunks.trec` ← Task 2 Step 3. Run: the plan's Step 6 line, executed on the scratch artifacts of those steps (with one stand-in arm file under `A/screen/`), listed 13 files, one or more per glob, with no "No such file" error. |
| T5.7 | Task 5 Step 7 (gate doc contents) | ok. [existence] Each of the nine items against the step that produces its source: (1) verdict, delta, p ← `report-gate.txt` (Step 1); (2) inputs ← spec §3's table and md5s, plus the pool note from Global Constraints; (3) screen table ← `$A/screen/screen.md` (Task 3 Step 5); (4) pod facts ← `$A/pod/pod.md` (Task 4 Step 14). Step 12's cross-check prints to the terminal only, but its input `main.responses.jsonl` comes back in Task 4 Step 13's tar, so the snippet can be re-run on the dev box; (5) `[pool]`, `[scores]` and `[compare]` blocks ← `report-gate.txt`, and the fallback count ← Step 1's `jev-main.meta.json` print; (6) ← `report-bound.txt` (Step 2); (7) ← `secondary.txt` (Step 3), `report-repeat.txt` (Step 4), `transfer.txt` (Step 5); (8) ← spec §11 and item 7's repeat delta; (9) ← `md5s.txt` (Step 6) and the command lines of Steps 1–5 and Task 4 Steps 7–11. Every file named here was produced in the scratch runs of T5.1–T5.6, except `screen.md` and `pod.md`, which are hand-written by the steps named. |
| T5.8 | Task 5 Step 8 (row append plus paragraph rewrite) | Insert position and paragraph text: ok. [existence] Line 50 is the relation-popularity row. Lines 55–56 contain "The seven rows added since were measured at the" / "shipped window instead:" verbatim. There are 7 rows after the multivector row, so "eight" is correct once one is added. **Order check:** → §2.1. |
| T5.9 | Task 5 Step 9 (commit) | ok. [existence] `docs/plans/` is ignored (`.gitignore:49`), so it uses `add -f`. The ranked-changes doc is tracked (`git ls-files` lists it). |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | Task 1's CLI ↔ every invocation in Tasks 2–4 | ok. [bidirectional] Invocation → parser: every flag used (`--pool --corpus --queries --base-url --model --question-type --responses --out --composite --subsample --subsample-seed --server-commit --revision --policy --max-batch-size --max-model-len --preflight-input-tokens`) is defined in `build_arg_parser`. Parser → invocations: every required flag is supplied in each of Task 2 Step 3, Task 3 `run-arm.sh`, and Task 4 Steps 8–11. |
| C2 | `jev_rerank.STATE_TEMPLATE` ↔ `stub_jev_server.STATE_PREFIX` | ok. [compat] 300/300 queries resolved and were accepted in both dry-run passes. |
| C3 | Task 3 winner → Task 4 `REPO`/`POLICY`/`QT`/card | ok. [existence] Producer: Task 3 Step 5 records the winner (demo id and question type) in `screen.md` and maps the demo id to HF repo, policy and card in its table. Consumers: Task 4 Step 1 states the card; Step 4 sets `REPO=<HF repo from Task 3's table>; POLICY=<policy from Task 3's table>`; Step 8 sets `QT` to `noul`, or `score` if arm 6 won. Every value Task 4 needs has a column or a line in Task 3's output. The table's mapping is checked in T3.4. |
| C4 | Task 3 winning-arm ledger (persisted) → Task 5 Step 5 `content["values"]` | ok. [presence] The accepted record's `content` key set is `['metadata', 'values']`, with 50 values (`A/dryrun/reversed.responses.jsonl` line 1). The same `score_query` writes the demo arms' ledgers. |
| C5 | Pre-flight ledger (persisted) → `PFT` → `$PROV` | ok. [presence] See T4.8. |
| C6 | Pod artifacts (persisted) → Task 5 | ok. [presence] The sidecar `reranker` key set is `baseUrl, fallbackCount, fallbackQueryIds, maxBatchSize, maxModelLen, modelId, policy, preflightInputTokens, questionType, revision, serverCommit, subsample, subsampleSeed, wordingSha256`. That covers every field spec §6.3 item 2 requires, and the `fallbackQueryIds` that Step 2 reads. |
| C7 | Main ledger `content.metadata` (persisted) → Task 4 Step 12 | ok. [presence] The `metadata` key is present in `content` (`null` from the stub, a dict from hf_server with advanced metrics; T4.12). |
| C8 | `select_subsample` call site 1: `jev_rerank.main` (`list(pool.keys())` of the SciFact pool) | ok. [compat] The repeat pass ran with `--subsample 50 --subsample-seed 20260929`. |
| C9 | `select_subsample` call site 2: Task 5 Step 4 (`list(tr.load_run(BASE))`) | ok. [compat] Its `assert ids == set(load_run(REPEAT))` passed against call site 1's output (T5.4). |
| C10 | Sidecar `composite` → `report.py` build line | ok. [compat] `report-gate.txt` shows `build 7d3a15092f963723` for both runs, with no MISMATCH. |
| C11 | Run files → `report.py --pair` (dry run, gate, bound) | ok. [compat] See T2.4, T5.1 and T5.2. |

### Rule-like content

| # | Rule | Disposition |
|---|---|---|
| R1 | `judge_values` acceptance | **over** (a bad response accepted): ok. [compat] The parametrized test rejects NaN, ±inf, −0.01, 1.01, `True`, `"0.5"` and `None`, and M4, M5 and M6 are killed. **under** (a valid response rejected): ok. [compat] 600/600 stub responses with values 0.0–0.98 were accepted. hf_server's binary `noul` is `probabilities['yes']` (`hf_prompt_policies.py:182-186`), and `score` is the expected index under key `"score"` (`response_scoring.py:318`). |
| R2 | `order_by_judge` stable tie-break | **over** (ties broken by doc id): ok. [compat] M7 is killed. **under** (distinct values mis-ordered): ok. [totality] The reversed dry run scores 0.0011 over all 300 queries, and M27 is killed. |
| R3 | Transport classes (403 aborts; 429 retries with Retry-After; any other 4xx falls back at once; 5xx, network errors and rejected bodies retry) | **over** (retrying what is a configuration error): → §3.1. A 301 on the endpoint makes urllib re-issue the request as GET and receive a 200 HTML page, which is retried as `network` (run: `probe-score-resp.json` = `["network", "non-JSON 200 body: …"]`). **under** (a transient failure not retried): ok. [existence] `issubclass(RemoteDisconnected, OSError)` is True, and timeouts are `OSError`. `IncompleteRead` is not an `OSError`, and the candidate that it crashes the run was dropped: the crash is loud (a traceback and exit 1), and the ledger resumes. |
| R4 | Fallback cap `n*5//100` | **over** (a run above 5% admitted): ok. [existence] 300→15, 323→16, 20→1, 50→2, 1→0, matching spec §5's strict "more than 15/16". M17 is killed. **under** (a run at or below the cap refused): ok. [compat] The test at 1-of-20 writes the run file, and M18 is killed. |
| R5 | `pass_identity` (identity key) | **over-merge** (two passes share a key): ok. [presence] A real record's `pass` holds `pool, baseUrl, model, questionType, subsample, subsampleSeed`. Screen arms differ on `model`/`questionType`. The demo and the pod differ on `baseUrl`. Main and repeat differ on `subsample`. Every pass also has its own ledger file. **under-merge** (a legitimate resume refused): ok. [existence] `pool` is absolutised, and every other field comes from the same command line. The provenance flags are deliberately excluded, so re-launching after an MBS change resumes. |
| R6 | Screen selection (valid = `exit 0`; highest NFCorpus nDCG@10; ties to the earlier arm) | **over** (an invalid arm selected): ok. [existence] An arm over the cap writes no run file (`jev_rerank.main`), so `report.py` cannot score it. **under** (a valid arm excluded): ok. [existence] A resumed arm appends a later `exit 0` line to `arms.status`. |
| R7 | Pre-flight branches (500/OOM → halve MBS; 422 → stop), as the plan's Step 8 implements them through `jev_rerank.py`'s exit code and ledger | **over** (a working setup is halved or stopped): ok. [compat] Run: the plan's Step 8 invocation on the real query-129 pool (`awk '$1=="129"'`, 50 rows) against the stub exited 0 and wrote `accepted None 0` to the ledger (`A/pf/pre-ok.responses.jsonl`), so a 200 reads as a pass with a PFT to take. A 422 that is not about length would stop a correct setup; CDR-2 R15 dropped that as the right action with the wrong diagnosis, and it is not re-raised. **under** (a failing setup reads as a pass): ok. [compat] Run: the same invocation against a closed port exited 1 with `1 / 1 queries fell back, over the cap of 0`, wrote no run file, and left four `rejected network error` records (`A/pf/pre-down.responses.jsonl`). A 500 takes the same retry-then-fallback path (`test_5xx_is_retried_up_to_four_attempts_then_falls_back`), and a 422 falls back at once (`test_main_over_the_fallback_cap_writes_no_run_file`, 1 query, exit). With a cap of 0 for one query, no non-200 outcome can read `exit 0`. |
| R8 | Stub doc-text guard | **over** (a defective prompt accepted): ok. [compat] The negative probe returns 422 (T2.2). **under** (a correct prompt rejected): ok. [totality] 300/300 accepted per mode. |
| R9 | Step 12 equality `{(SHA, POLICY)}` | **over** (passes on a mis-served model): ok. [existence] `model_revision` is the same `revision` that `from_pretrained` loads (`hf_server.py:1220-1242`, `:1273`). **under** (fails on a correct run): ok. [existence] The values come from the launch flags, and the explicit-policy path returns `requested` unchanged (`hf_prompt_policies.py:58-60`). |
| R10 | Worst-case bound (fallback queries → reversed baseline) | **over** (a non-fallback query altered): ok. [compat] Run: with `fallbackQueryIds` set to `['1','3']` (`runbt.sh`), the bound differs from the main run on exactly `['1', '3']`, and 298 of the other 298 queries are identical. With 0 fallbacks the bound run was scored unchanged (`report-bound.txt`, exit 0). **under** (a fallback query left unreversed): ok. [compat] Same run: both `1` and `3` equal `list(reversed(baseline pool))` (True), and query 1's rank 1 is baseline's rank-50 doc `35621259`. |
| R11 | Task 5 Step 8 order check (`grep -n "^| " … \| tail -3`: "the new row must be the last table row") | **under** (fails a correct insertion): → §2.1. **over** (passes a wrong insertion): → §2.1, because the check never inspects the §0 table at all. |

**Totals: 59 rows. 54 ok; 5 → findings (T3.2 and R3 → §3.1; T4.2 → §2.2; T5.8 and R11 → §2.1); 0 dropped as rows.** One sub-candidate inside R3's under-direction, the `IncompleteRead` crash, was dropped with its reason given.

## 1. Verified-plan-assumptions cross-check

- P1 still holds. Re-run: no `simple-jev` worktree and no such branch.
- P2 still holds. `grep -n "^def"` finds each helper at the cited line: `load_run:75`, `load_corpus:117`, `load_queries:122`, `select_subsample:140`, `read_responses_ledger:214`, `accepted_entry:253`, `append_response:264`, `make_record:272`, `validate_ledger_pass:306`, `sidecar_path_for:508`, `validate_inputs_before_any_model_call:583`.
- P3 still holds. `load_queries_by_text:85`, with the guard at `:185`. The stub resolved all 300 SciFact states (T2.3).
- P4 still holds. 20 × 50, and 323 contiguous runs = 323 distinct ids.
- P5 still holds. The count is 50.
- P6 still holds. `44 passed` (scratch copy).
- P7 still holds, with a cite correction. The mutual-exclusion exit is at `report.py:886`, not `:883`. The behaviour was re-run in T5.4.
- P8 still holds. Both `bge-base.meta.json` files read `"composite": "7d3a15092f963723"`.
- P9 still holds. See `pyproject.toml` (T4.4).
- P10 still holds. No task imports a later task's file.
- P11 still holds. The `call_jev` tests pass against a real local server.
- **P12 failed for the demo half.** Fresh probes of the demo, at 18:52, 18:54 and 18:55 UTC on 2026-09-30, all returned `HTTP/2 301` with `location: https://featherless.ai/simple-jev/classifier`, for both the plan's body and the demo page's own curl example (`probe2-headers.txt`). The plan's `call_jev` follows the redirect as GET and gets `("network", "non-JSON 200 body: …")` (`probe-score-resp.json`). The target rejects POST with 405. So no demo response of any shape can be observed today. The hf_server half still holds: `answers[d]["noul"]` comes from `restore_binary_noul`, `"score"` is at `response_scoring.py:318`, and `usage.input_tokens` is at `:160`. → §3.1.
- P13 still holds. Re-run in T5.3.
- P14 still holds for the line anchors. The order check it feeds is §2.1.
- P15 still holds. `ls` finds no gate doc, and the ignore rule is `.gitignore:49`.
- P16 still holds. `git log --format=%s -6` is all lowercase imperative.
- P17 still holds. See C1.
- P18 still holds. `print_scores` prints `R@50` (seen in `report-gate.txt`).
- P19 still holds. `50 query ids, 55 qrels rows`.
- P20 still holds. See T5.5.
- P21 still holds. 33 passed; 27/27 killed.
- P22 still holds. The outcomes and both md5s are identical.
- P23 still holds. T5.1–T5.5 were run.
- P24 still holds. `keymap.json.stats.json` has `chunk_max_chars 512, chunk_step 448`.
- U1 still holds as a dependency that Step 3 checks. Read-tier evidence that it can be met is in T4.3.
- U2 still holds. The stage-2 runbook uses `runpodctl send/receive` with md5 at `:118-124` and `setsid nohup` at `:137`.

**Span check. One uncovered dependency:**
- **The pod has room for the model weights where the plan downloads them.** Step 2 sizes the container disk, but `HF_HOME=/workspace/hf` puts the weights on the separately sized `/workspace` volume. No assumption covers this. It was verified at read tier to be a gap, and it becomes §2.2.

A second dependency is not a span gap. The demo endpoint serving the API when Task 3 runs is the listed P12 and inherited A24, and it failed above (→ §3.1).

## 2. Literal-wrongness findings

### 2.1 Task 5 Step 8's order check cannot confirm the insertion, because the file has later tables

**Description.** Step 8 appends the SimpleJEV row after line 50. It then says: "Check the order with `grep -n "^| " docs/2026-09-06-ranked-changes-after-retrieval-experiments.md | tail -3`: the new row must be the last table row."

The doc has two more tables after §0, at lines 268–276, so `tail -3` always shows the last of those. A correct insertion therefore fails the check. An implementer who follows the check literally either stops, or "fixes" the order by moving the row to the end of the file, which puts it in the wrong table. The check also cannot catch a wrong insertion inside §0, because it never looks at §0.

This is the append-order failure the check was added to catch, left unverified. The consequence is a mis-placed or un-verified §0 row, which is the one index spec §7 requires.

**Evidence.**
- [compat] Run. `grep -n "^| "` over the current doc gives table lines `33 35 … 50 268 270 … 276`.
- [compat] Run. The row was inserted at line 51 of a copy (`ranked.md`, md5 `ec860702…`), exactly as Step 8 says. The plan's check then prints lines `275:`, `276:` and `277:` (the `embedding-prefixes-and-title` / `decay-share-triple-b` / `bump-ollama…` branch table), not the new row.

**Proposed fix.** In Task 5 Step 8, replace the check sentence with the following:

> Check the order with `grep -n -B1 -A1 "SimpleJEV relevance judge" docs/2026-09-06-ranked-changes-after-retrieval-experiments.md`. It must print the relation-popularity row as `50-| Relation-popularity signal …`, the new row as `51:| SimpleJEV relevance judge …`, and a blank `52-` line.

Evidence: [compat] This exact command was run on the inserted copy `ranked.md`. It printed `50-| Relation-popularity signal (citation count …`, `51:| SimpleJEV relevance judge (pointwise noul, …` and `52-`. On a row inserted above line 50, the `-B1` line would not be the relation-popularity row, so the check can fail.

### 2.2 Task 4 sizes the container disk, but the model weights are downloaded to the separately sized `/workspace` volume

**Description.** Task 4 Step 2 rents "container disk ≥ 150 GB". Steps 4, 5 and 7 then set `HF_HOME=/workspace/hf`, and Steps 4 and 5 clone and run from `/workspace`, so every weight download lands under `/workspace`.

On RunPod, `/workspace` is the pod's volume disk, which is sized separately from the container disk. The plan never sizes or checks that volume. The four winners the plan routes to an H200 or 80 GB card download 24–72 GB of bf16 weights (spec §6.2 table: about 24, 52, 56 and 72 GB). If the volume is smaller than the winner's weights, Step 5's first `simple-jev` launch fails mid-download on a billed pod. Its `until curl -sf …/health` loop then never exits, because the server never comes up.

The stage-1 and stage-2 precedent is different. Their 150 GB container disk held the weights, because the vLLM image downloads to its default cache on the container disk (runbook `:104`). This plan moved the cache without moving the sizing.

**Evidence.**
- [existence] RunPod's storage documentation (docs.runpod.io/pods/storage/types, fetched 2026-09-30) says the volume disk mounts at `/workspace`, and that the container disk is separate, "system-managed", "temporary storage for the operating system and session data".
- [existence] Plan read: `HF_HOME=/workspace/hf` at plan lines 1160, 1172 and 1196. The only disk instruction is "container disk ≥ 150 GB" (line 1146).
- UNVERIFIED: the volume size of RunPod's PyTorch template when left at its default. A search summary says 20 GB, but no primary source was read. Whether the default actually breaks depends on it. The plan leaves that size to whatever the renter's template says, and nothing checks it.

**Proposed fix.** Make the disk that holds the weights the disk that is sized, and check it before downloading:
1. In Task 4 Step 2, change "container disk ≥ 150 GB" to "container disk ≥ 150 GB **and volume disk ≥ 150 GB** (`HF_HOME` is on the `/workspace` volume)".
2. Add to Step 3: `df -h /workspace`. Required: at least 100 GB available. Otherwise terminate and re-rent with a larger volume.

Evidence: [existence] RunPod docs (above): `/workspace` is the volume mount, so sizing the volume sizes the filesystem that `HF_HOME=/workspace/hf` writes to. The 100 GB threshold exceeds the largest weight set in spec §6.2 (about 72 GB, Qwen3.6-35B-A3B) with room for the tokenizer and cache overhead. `df -h <path>` reports the filesystem that holds `<path>`, so the check can fail when the volume is too small. UNVERIFIED: that RunPod's rental form lets you set the volume disk at 150 GB on the chosen card. The form exposes a volume size, but its per-card limits were not read.

## 3. Forced decisions

### 3.1 The public demo API no longer serves the classifier, so Task 3's screen cannot name a winner

**The choice.** How Task 3 is to select the gate model while `https://simple-jev-demo-api.featherless.ai/v1/classifier` redirects every request to a web page.

**Why it is forced.** At 18:52, 18:54 and 18:55 UTC on 2026-09-30, about an hour after the plan was committed (17:39 UTC), every request to the demo host returned `301` to `featherless.ai/simple-jev/...`. That held for a POST of the plan's §4 body, for the demo page's own documented curl example, for `GET /v1/models` and for `GET /`. POST to the redirect target returns 405.

The plan's client does not stop on this:

1. urllib re-issues the 301 as a GET and receives a 200 HTML page.
2. `call_jev` classes that page as a `network` failure, which is retryable (`probe-score-resp.json`).
3. Every query burns 4 attempts and falls back, and each arm exits 1 over the 16-query cap.
4. Step 2's detached loop then moves on to the next arm.

The plan's monitoring note watches only for a 403, and it has no branch for "no valid arm". So all five arms end invalid, and Steps 3–5 cannot pick a leader or a winner. Task 4 consumes that winner (C3).

Spec §2's ruling fixes the screen on the demo. Replacing the demo, or dropping the screen, is a change to Ben's ruling, not a plan-level fix. Waiting is the only option that keeps the ruling, and the wait is unbounded.

**Options.**
- **(a) Keep the demo screen, and gate Task 3 on the demo being back.** Before Step 2, add a blocking check. Send one §4-shaped NFCorpus request (for example `PLAIN-478`, as in A24) per arm model with `curl -s -o /dev/null -w "%{http_code} %{redirect_url}"`, and require `200` with no redirect. Add the same one-line check to `run-arm.sh` before each arm, so that an outage mid-screen fails that arm fast instead of grinding through its 323 queries. Until the check passes, Task 3 (and therefore Tasks 4–5) waits. The trade-off is an unbounded delay, at no cost and with no change to the spec.
  Evidence: [compat] Run: `curl -w "%{http_code} %{redirect_url}"` printed `301 https://featherless.ai/simple-jev/classifier` for the documented example body, so the check fails today, as it should. [compat] Run: `call_jev` on the same endpoint returned `("network", "non-JSON 200 body: …", None)`, so without the check the client retries instead of stopping. UNVERIFIED: whether and when the demo returns. The page at the redirect target still documents the old endpoint (`probe3-body.bin`), which suggests the redirect is not a deliberate relocation of the API, but nothing confirms that.
- **(b) Run the screen on self-hosted hf_server instead of the demo.** Rent a pod per screen model, or one H200 serving the models in turn, and run the five NFCorpus arms plus arm 6 against `hf_server` with `jev_rerank.py` unchanged. This changes spec §2's "$0 screen on the demo" ruling and needs Ben's budget approval and a spec amendment. As a side effect, it removes known issue 2 (the demo's serving stack differs from `hf_server`), and with it most of the transfer check's purpose.
  Evidence: [existence] Inherited A25: hf_server serves all five models on the prefix-cache path (`hf-server/README.md:120-129`). [existence] `jev_rerank.py` is endpoint-agnostic (`--base-url`), and its passes are keyed by `baseUrl` (R5). UNVERIFIED: the cost. The screen is 6 × 323 NFCorpus requests at the pod's unmeasured latency, and the demo's 7–26 s per request is not a pod measurement.
- **(c) Drop the screen and have Ben name the gate model.** Task 3 is replaced by a ruling, for example from hf_server's own development scores. This changes spec §5's pre-registered selection rule. The gate itself is unaffected, but the selection is no longer measured on NFCorpus, and the transfer check (Task 4 Step 9, Task 5 Step 5) loses its demo ledger.
  Evidence: [existence] `hf-server/README.md:120-129` reports development scores for all five models (inherited A25). [existence] Task 5 Step 5 reads `$A/screen/<winning arm>.responses.jsonl`, which would not exist, so that step would be dropped or reported as not run.

**Why no option dominates.** (a) is the only option that keeps both of Ben's rulings (spec §2 and §5), but its delay is unbounded. (b) removes the delay at a rental cost and changes the §2 ruling. (c) removes both the delay and the cost, but gives up the pre-registered selection. A hybrid, such as "(a) with a deadline, then (b) or (c)", still requires choosing which fallback, which is the same choice between (b) and (c). None of them can be picked without Ben changing, or confirming, a spec ruling.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3.1 needs Ben's choice, because the demo screen cannot run while the endpoint redirects. P12's demo half failed in §1, and that failure is what §3.1 carries. §2.1 (the order check) and §2.2 (the pod's disk for the weights) must also be applied before SDD.
