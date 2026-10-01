# Critical Implementation Review: 2026-09-30-simple-jev-judge-implementation-plan (Round 2)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-09-30-simple-jev-judge-implementation-plan.md
**Artifact HEAD at review:** bbef7faf9e29a4b34bbb71a0b9a851b9b871f2ff
**Verified plan-level assumptions section:** present

⚠️ 12 commits since plan-write time (SHA 18ba5177); cited file:line references re-checked under §1.

Eleven of the twelve commits are docs-only. The twelfth is merge `519ae09d`, which landed on `main` during this round. It adds only `scripts/dialogue_patterns.py`, `scripts/test_dialogue_patterns.py` and `docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md` (`git diff --name-status bbef7faf..HEAD`). None of these is a file the plan reads, and none is a conftest or ini file (row M8).

**Amendment anchoring.** Round 1 recorded the SHA anchor `81a34247f7b9…`. `git rev-parse 81a34247:<plan>` = `adf544fc…`, but `git hash-object <plan>` = `ba52b670…`, so the content has changed. The hunk set is `git diff 81a34247 -- <plan>`: seven `@@` hunks in five areas (1 file changed, 43 insertions, 10 deletions). The areas are the P12 row, the new U3 row, Task 3 Steps 1–2 (three hunks), Task 4 Steps 2–3, and Task 5 Step 8. The forward window `81a34247..HEAD -- <plan>` holds only `bbef7faf` "applied 4 fixes from …critical-review-1…", which is in-band. The reverse window is empty, and `81a34247` is an ancestor of HEAD. There are no out-of-band amendments. All seven hunks are reviewed at fix-equivalent rigor in M1–M4 (mapping in M7).

**Probe scratch.** The probes ran outside the repo, in `/tmp/claude-1000/-home-ben-repositories-Iverson/8ea909e2-a873-4454-bbef-4ce0a5c26326/scratchpad/cir2/`. It holds:
- The plan's code blocks, extracted verbatim (`block00…block40`, plus `jev_rerank.py`, `test_jev_rerank.py`, `stub_jev_server.py` and `mutate.py`).
- `main`'s `teacher_rerank.py`, `stub_vllm_server.py`, `report.py` and `test_teacher_rerank.py`.
- `prelude.sh`, which repoints `R`, `S` and `A` into the scratch directory.
- `A/flaky/`, a local stand-in for the demo (`flaky_demo.py`). It answers like the stub for the first K POSTs, then returns `301` to an HTML page.

Pinned md5s:

| File | md5 |
|---|---|
| `A/dryrun/identity.chunks.trec` | `3e46051e2386578c6664e8840c46eaf2` |
| `A/dryrun/reversed.chunks.trec` | `c7c7a298c9dfa13cff930dde7e0b21da` |
| `A/gate/report-gate.txt` | `0817485e…` |
| `A/gate/report-bound.txt` | `f0f1a32a…` |
| `A/gate/report-repeat.txt` | `994611a5…` |
| `A/gate/secondary.txt` | `4855090f…` |
| `A/gate/transfer.txt` | `5199a032…` |
| `A/flaky/arms.status` (final; the revised fix's runs over all nine end states) | `bf564ec9…` |
| `A/flaky/run-arm-v2.sh` (§2.1's revised `run-arm.sh`, paths rewritten) | `292151a6…` |
| `ranked.md` (the ranked-changes doc after the Step 8 edit) | `8b490b70…` |

**Demo traffic.** Two POSTs went to `simple-jev-demo-api.featherless.ai` at about 23:48 UTC on 2026-09-30, sequentially, with `User-Agent: curl/8.5.0`. One was the plan's `check-demo.sh` (NFCorpus PLAIN-478). The other was `call_jev` with two SciFact query-1 docs, and urllib followed its redirect with one GET.

## 0. Coverage enumeration

### Tasks × surfaces

| # | Surface | Disposition |
|---|---|---|
| T1.1 | Task 1 prose: the decisions beyond the spec (pass identity; any other 4xx falls back; over-cap exit; the 0.5 s wait) | ok — [compat] `mutate.py` ran 27/27 `KILLED`, `survivors/pattern errors: 0`: M16 for the identity, M11 for the 4xx rule, M17/M18 for the cap, M14 for the wait. **over** (a wait or fallback where none is due): ok — [compat] `test_every_request_is_preceded_by_the_minimum_interval` asserts `sleeps == [0.5, 0.5]` for exactly two attempts. **under** (a rule missing): ok — [compat] the same kills. |
| T1.2 | Step 1 worktree command and prelude | ok — [existence] Re-run: `git worktree list \| grep -c simple-jev` = 0 and `git branch --list 'simple-jev*'` is empty. The base is local `main`, now `519ae09d`; M8 shows that merge touches nothing the plan reads. |
| T1.3 | Step 2 test file | ok — [compat] Extracted and run: `33 passed in 1.77s`. |
| T1.4 | Step 4 `jev_rerank.py` | ok — [compat] Exercised by the suite, both 300-query dry runs (T2.3), three stub "pod" passes (repeat 50, transfer 20, NFCorpus 323), and Task 5 Steps 1–5. |
| T1.5 | Steps 3/5 commands | ok — [compat] `test_teacher_rerank.py`: `44 passed`. |
| T1.6 | Step 6 mutation checker | ok — [totality] All 27 run: 27 `KILLED`, 0 survivors, 0 pattern errors. |
| T1.7 | Step 7 commit | ok — [existence] `git ls-files Iverson.Server/Iverson.LoadTest/scripts` lists 44 tracked files, including `teacher_rerank.py`, so the directory is tracked and not ignored. The two new paths need no `-f`, and the message is lowercase imperative (P16). |
| T2.1 | Stub `stub_jev_server.py` | ok — [compat] Served 600 SciFact, 323 NFCorpus and 20 NFCorpus requests with 0 fallbacks. |
| T2.2 | Task 2 Step 2 | ok — [compat] Ran. Output: `18791`, `18792`, `('http', (422, '{"detail": "d0: instructions lack the text of pool document 21456232"}'))`, character for character the plan's expected output. |
| T2.3 | Task 2 Step 3 | ok — [compat] Ran in 2 min 32 s. Both passes print `(300 queries, 0 fallbacks)`. |
| T2.4 | Task 2 Step 4 | ok — [compat] Ran. `identity exit 1` at 0.7452 / 0.9337 with `sequence differs on 0 (0.0%)` and `ARM INVALID`. `reversed exit 0` at 0.0011 / 0.9337 with `sequence differs on 300 (100.0%)`. Both md5s equal the plan's. |
| T2.5 | Task 2 Step 5 commit | ok — [existence] One new path, `stub_jev_server.py`, in the same tracked `scripts/` directory (`git ls-files` lists 44 files there, as in T1.7). |
| T3.1 | Task 3 Step 1 `check-demo.sh` (new in `bbef7faf`) | Mechanics ok — [compat] Ran the plan's script (paths rewritten) against the live demo at 23:48 UTC. It printed `arm1-qwen4b demo check: '301 https://featherless.ai/simple-jev/classifier'` and exited 1. Against a serving local stand-in it printed `'200 '` and exited 0. Its role in classifying arm outcomes → §2.1. |
| T3.2 | Task 3 Step 1 `run-arm.sh` (amended) | → §2.1 |
| T3.3 | Task 3 Step 2 gate, launch and monitor prose (amended) | → §2.1 |
| T3.4 | Task 3 Step 3 `report.py --run $A/screen` | ok — [compat] Ran over a directory shaped like `$A/screen`: a run file, sidecar, ledger, log, `*.check.json`, both scripts and `arms.status`. Exit 0. Only `arm1.chunks.trec` was scored (`resolve_run_paths` globs `*.trec`, `report.py:144-145`). The leader rule's roster → R7. |
| T3.5 | Task 3 Step 4 (arm 6, `score`, its demo check) | → §3.1. The foreground (not detached) launch is dropped as D1. |
| T3.6 | Task 3 Step 5 demo-id → repo/policy/card table | ok — [bidirectional] Read `hf_prompt_policies.py:41-52`. **Table → code:** 4B, 27B and 26B-A4B map to `shared_examples_binary`, and 35B-A3B and 12B to `shared_repeat_state`. **Code → table:** each of the five `KNOWN_PROFILES` rows has a table row. |
| T4.1 | Task 4 Step 1 (stop for the go-ahead) | ok — [existence] `command grep -n`: `:1176` "Step 1: STOP and ask Ben for the go-ahead" comes before `:1178` "Step 2: Rent the pod", and Global Constraints `:21` repeats it ("The implementer stops at its Step 1 and asks Ben"). |
| T4.2 | Task 4 Step 2 rental (amended: volume disk ≥ 150 GB) | ok — [existence] Fresh fetch of docs.runpod.io/pods/storage/types: the volume mounts at "/workspace (default)". The container disk is "temporary storage for the operating system and session data". No default sizes are stated. `HF_HOME=/workspace/hf` therefore writes to the sized volume. |
| T4.3 | Task 4 Step 3 image + `df -h /workspace` check (amended) | ok — [existence] `df -h <path>` reports the filesystem that holds `/workspace`, which is the one `HF_HOME` writes to, so the 100 GB check can fail when the volume is too small. Separately, the two scripts shipped to the pod parse at `ast.parse(feature_version=(3,12))` and `(3,11)`, and they import only old stdlib modules (span item 2). |
| T4.4 | Task 4 Step 4 install + revision | ok — [existence] `hf-server/pyproject.toml:10-14`: `requires-python >=3.12`, `torch>=2.6`, `transformers>=5.16.1,<6`, `[project.scripts]`. |
| T4.5 | Task 4 Step 5 auto-policy log line | ok — [existence] `resolve_prompt_policy` logs `AUTO PROMPT FORMAT: %s -> %s` at **warning** level (`hf_prompt_policies.py:79-80`). It runs inside `load_service`, before `uvicorn.run` (`hf_server.py:1335-1336`), so the last-resort handler sends it to stderr, which `2>&1` captures. `/health` is at `hf_server.py:1049-1051`, and the default port is 8000 (`:1331`). |
| T4.6 | Task 4 Step 6 upload layout | ok — [existence] Path by path: the tar root `-C /tmp/jev-up .` extracts in `/workspace/in` = `$I`, which yields `$I/scripts/{jev_rerank,teacher_rerank}.py`, `$I/sf/{corpus,queries}.jsonl`, `$I/sf/runs/bge-base.chunks.trec` and the matching `$I/nf/…` files. Steps 8–11 read exactly these. |
| T4.7 | Task 4 Step 7 launch | Flags ok — [existence] every flag is in `hf_server.py:1289-1332`. The block's `MBS=32` → §2.2. |
| T4.8 | Task 4 Step 8 pre-flight, `PFT`, `PROV` | Branch mechanics ok — [compat] the 129-row pool gives 50 rows (`awk`), and the cap is `1*5//100 = 0`. The block's `QT=noul` → §2.2. |
| T4.9 | Task 4 Step 9 transfer | ok — [totality] `head -n 1000 … \| uniq -c` gives 20 runs of exactly 50. A stub run of the plan's invocation shape on that file wrote `(20 queries, 0 fallbacks)`. |
| T4.10 | Task 4 Step 10 main pass | ok — [compat] Same invocation shape as the dry run, plus `$PROV`. The stub repeat pass ran with `--server-commit x --max-batch-size 16` parsed and written to the sidecar. |
| T4.11 | Task 4 Step 11 repeat pass (no explicit wait before Steps 12–13) | dropped — [existence] With `concurrency=1` and `queue_size=16` (`hf_server.py:1262`, `:835`, `:870-871`), the two one-at-a-time clients alternate requests, so the 50-query repeat finishes long before the 300-query main pass that Step 10 monitors to completion. No outcome changes. |
| T4.12 | Task 4 Step 12 revision/policy cross-check | ok — [existence] Advanced metadata is merged into the top-level `response["metadata"]` (`hf_server.py:949-954`). `model_revision` is the `--revision` argument and `prompt_policy` is the resolved policy (`:1264-1273`). `build_response`'s own metadata holds only `template_version` and `calibration` (`response_scoring.py:164-168`), so nothing overrides them. |
| T4.13 | Task 4 Steps 13–15 | ok — [existence] Same `tar … && md5sum … && runpodctl send/receive` shape as the stage-2 runbook (`2026-09-28-student-distillation-runbook.md:118-124`). |
| T5.1 | Task 5 Step 1 gate | ok — [compat] Ran with the reversed dry run standing in for `jev-main`. Output: `[pool] … sequence differs on 300 (100.0%)`, then `[compare] jev-main.chunks.trec vs bge-base.chunks.trec (nDCG@10)` with `delta -0.7442` and `permutation p = 0.0002`, and the fallback print `0 []`. |
| T5.2 | Task 5 Step 2 worst-case bound | ok — [compat] Ran with 0 fallbacks (exit 0). Also ran with `fallbackQueryIds ['1','3']` on an identity main: the bound differs from main on exactly `['1','3']`, both are `reversed(baseline)`, and there are 15,000 rows. That run's `--pair` exits 1 only because an identity main reorders 2/300; in the real flow the main has already passed `--pair`, and the bound only adds reordering. |
| T5.3 | Task 5 Step 3 secondary metrics | ok — [compat] Ran. `bge-base.chunks.trec {'R@50': 0.9337, 'AP': 0.7018, 'nDCG@10': 0.7452, 'P@10': 0.0993, 'R@10': 0.877}` matches spec §3's identity row. |
| T5.4 | Task 5 Step 4 restricted qrels + `--baseline` | ok — [compat] Ran on a real stub repeat pass (`--subsample 50 --subsample-seed 20260929`). Output: `50 query ids, 55 qrels rows`, exit 0, and `report-repeat.txt` prints the repeat-vs-main `delta` and `queries changed 0 / 50`. |
| T5.5 | Task 5 Step 5 transfer | ok — [compat] Ran with a 323-query NFCorpus "demo" ledger (identity stub) against a 20-query "pod" ledger (reversed stub). Output: `20 queries in both ledgers  mean rho -1.000  mean\|diff\| 0.5000`. |
| T5.6 | Task 5 Step 6 md5 globs | ok — [existence] Plan `:1439` globs, each with its producing line from `command grep -n` over the plan:<br>`$A/pod/*.chunks.trec` and `$A/pod/*.meta.json` ← Task 4 Step 13 `tar czf … -C $O .` (`:1308`), whose `$O` is filled by `--out $O/pre-$MBS.chunks.trec` (`:1243`), `$O/transfer.chunks.trec` (`:1263`), `$O/jev-main.chunks.trec` (`:1274`) and `$O/jev-repeat.chunks.trec` (`:1285`), each writing its sidecar.<br>`$A/gate/*.chunks.trec` ← `BOUND=$A/gate/bound.chunks.trec` (`:1348`).<br>`$A/gate/*.txt` ← `report-gate.txt` (`:1338`), `report-bound.txt` (`:1362`), `secondary.txt` (`:1370`), `report-repeat.txt` (`:1399`) and `transfer.txt` (`:1408`).<br>`$A/screen/*.chunks.trec` ← `run-arm.sh --out $OUT/$1.chunks.trec` (`:1110`).<br>`$A/dryrun/*.chunks.trec` ← `--out $A/dryrun/$mode.chunks.trec` (`:1033`).<br>None of these lines is in a `bbef7faf` hunk (new-file ranges from M7: `:86-92`, `:101-107`, `:1069-1109`, `:1112-1124`, `:1130-1136`, `:1175-1191`, `:1470-1476`). |
| T5.7 | Task 5 Step 7 gate-doc contents | ok — [existence] Item → source file, read from plan `:1442-1451` against the producing lines in T5.6:<br>(1) verdict, delta, p ← `report-gate.txt`.<br>(2) inputs ← spec §3's table and the Global Constraints pool note.<br>(3) screen ← `$A/screen/screen.md` (Task 3 Step 5, `:1154`).<br>(4) pod facts ← `$A/pod/pod.md` (Task 4 Step 14, `:1313`) plus Step 12's printed set, re-runnable on the dev box from `$A/pod/main.responses.jsonl` (in the `:1308` tar).<br>(5) `[pool]`, `[scores]`, `[compare]` ← `report-gate.txt`, and fallbacks ← `jev-main.meta.json` (Step 1's print).<br>(6) ← `report-bound.txt`.<br>(7) ← `secondary.txt`, `report-repeat.txt` and `transfer.txt`.<br>(8) ← spec §11 and the repeat delta.<br>(9) ← `$A/gate/md5s.txt` (`:1439`) and command lines.<br>Every `$A/gate` file named here was produced in this round's scratch runs (T5.1–T5.5). |
| T5.8 | Task 5 Step 8 row + paragraph (amended order check) | ok — [compat] Applied to a copy of the current doc. The relation-popularity row is still at `:50`. The `old` text occurs once (`count 1`). The new check printed `50-| Relation-popularity…`, `51:| SimpleJEV relevance judge…` and `52-`. |
| T5.9 | Task 5 Step 9 commit | ok — [existence] `.gitignore:49` ignores `docs/plans/` (hence `-f`), and `git ls-files` lists the ranked-changes doc. |

### Cross-task interface contracts

| # | Contract | Disposition |
|---|---|---|
| C1 | Task 1 CLI ↔ every invocation (Task 2 Step 3, `run-arm.sh`, Task 4 Steps 8–11), and `check-demo.sh` ↔ `jev.build_request(model, type, query, texts)` | ok — [bidirectional] **Invocation → parser:** every flag used exists in `build_arg_parser`. `check-demo.sh` calls `build_request` with the 4-arg signature (ran: it built a 119,874-byte body). **Parser → invocation:** every required flag is supplied at each call site. |
| C2 | `STATE_TEMPLATE` ↔ `stub_jev_server.STATE_PREFIX` | ok — [compat] 943/943 stub requests resolved their query. |
| C3 | Task 3 winner → Task 4 `REPO`/`POLICY`/`QT` | → §2.2 (`QT` is re-assigned inside Step 8's block) |
| C4 | Winning-arm ledger (persisted) → Task 5 Step 5 `content["values"]` | ok — [presence] A real ledger record (`A/flaky/armX-lost.responses.jsonl`) has the keys `completion_tokens, content, order, pass, prompt_tokens, query_id, reason, reasoning, status`. Its accepted `content` decodes to `values` + `metadata`, and T5.5 consumed it. |
| C5 | Pre-flight ledger → `PFT` → `$PROV` | ok — [presence] Same record key set: `prompt_tokens` is present, and it is filled from `usage.input_tokens` (`response_scoring.py:160`). |
| C6 | Pod artifacts (persisted) → Task 5 | ok — [compat] Task 5 Steps 1–5 ran on stub-produced `jev-main`, `jev-repeat` and `transfer` files and their sidecars. |
| C7 | Main ledger `content.metadata` → Step 12 | ok — [presence] Dump of a real accepted record (`A/pod/repeat.responses.jsonl` line 1, written by the plan's `score_query`): keys `completion_tokens, content, order, pass, prompt_tokens, query_id, reason, reasoning, status`; `json.loads(content)` keys `['metadata', 'values']`. Step 12 reads `json.loads(record["content"])["metadata"] or {}`, so the key is present, and the stub's `null` maps to `{}`. That the value is a dict carrying `model_revision` and `prompt_policy` on hf_server is a code read (`hf_server.py:949-954`, `:1264-1273`; T4.12), not a dump, because no hf_server response can be produced here. |
| C8 | `select_subsample` call site 1 (`jev_rerank.main`) | ok — [compat] The repeat pass wrote `(50 queries, 0 fallbacks)`. |
| C9 | `select_subsample` call site 2 (Task 5 Step 4) | ok — [compat] Its `assert ids == set(load_run(REPEAT))` passed against call site 1's output. |
| C10 | Sidecar `composite` → `report.py` build line | ok — [compat] `report-gate.txt` shows `build 7d3a15092f963723` for both runs. |
| C11 | `check-demo.sh` exit code → `run-arm.sh` → `arms.status` (persisted) | → §2.1 |
| C12 | `arms.status` (persisted) → Task 3 Steps 3–4 selection | → §2.1 |

### Rule-like content

| # | Rule | Disposition |
|---|---|---|
| R1 | `judge_values` acceptance | **over:** ok — [compat] The parametrized test (NaN, ±inf, −0.01, 1.01, `True`, `"0.5"`, `None`) passes, and M4–M6 are killed. **under:** ok — [compat] 1,316/1,316 stub replies were accepted. `score` is the zero-based mean over the levels (`response_scoring.py:309-318`), so 4 levels give [0, 3], matching `VALUE_RANGE`. |
| R2 | `order_by_judge` stable tie-break | **over:** ok — [compat] M7 is killed. **under:** ok — [totality] The reversed dry run scores 0.0011 over 300/300 queries. |
| R3 | Transport classes | **over:** ok — [compat] Run: `call_jev` on the live demo returned `('network', 'non-JSON 200 body: …', None)`, so a 301 is retried as a network failure. That matches spec §5's classes; the arm-level consequence is R6. **under:** ok — [existence] Timeouts and `RemoteDisconnected` are `OSError` subclasses (CIR-1 R3). |
| R4 | Fallback cap `n*5//100` | **over:** ok — [compat] Flaky run: 20/40 fallbacks against a cap of 2 → exit 1 and no run file. **under:** ok — [compat] `test_main_at_the_fallback_cap_…` passes, and M18 is killed. |
| R5 | `pass_identity` | **over-merge:** ok — [presence] A real record's `pass` holds `pool, baseUrl, model, questionType, subsample, subsampleSeed`. **under-merge:** ok — [compat] Run: the flaky arm `arm4-qwen35b`, rerun after exit 1, resumed its 20 accepted entries and ended `exit 0`. |
| R6 | Arm-outcome classification (`check-demo.sh` + `run-arm.sh` → `arms.status`) | **over** (a demo outage recorded as a judge outcome, `exit 1`): → §2.1. **under** (a judge outcome recorded as an outage): ok — [compat] Run: a stand-in demo that serves but rejects every judge answer gave `armY-invalid … exit 1`. Non-redirect check failures → §3.1. Population matrix in R6a. |
| R6a | Population closure for R6/R7: arm end states × (classification, roster). Directions per cell: **A** = a judge outcome recorded or treated as a demo outage (wrongly rerun); **B** = a demo outage recorded or treated as a judge outcome (`exit 0`/`exit 1`), or an unfinished arm left out of the roster check. Runs used the plan's `run-arm.sh` against the local stand-in (`A/flaky/`); "fix" means §2.1's revised script (`run-arm-v2.sh`, md5 `292151a6…`). | (1) Serving, valid. **A:** ok — [compat] plan script: `arm4-qwen35b … exit 0` after resume; fix: `v2-valid … exit 0`. **B:** ok — [compat] its ledger has 0 `non-JSON 200 body` records, so no outage was hidden, and its `exit 0` admits it to Step 3 (T3.4).<br>(2) Serving, judge-invalid. **A:** ok — [compat] plan script: `armY-invalid … exit 1`; fix: `v2-invalid … exit 1` on both the first run and a rerun, so it is not looped through reruns. **B:** ok — [compat] 0 `non-JSON 200 body` records, and the post-check printed `'200 '`.<br>(3) Down before the arm. **A:** ok — [compat] Run `v2-down`: the only file the arm left is `v2-down.check.json` (no `.log`, no ledger), so `jev_rerank.py` never ran (`run-arm-v2.sh:8` exits first) and no judge outcome exists to misfile. **B:** classification ok — [compat] `demo-unavailable` on the live 301 and on `v2-down`; roster → §2.1 (Step 3 is not blocked on a `demo-unavailable` arm).<br>(4) Persistent redirect outage mid-arm. **A:** ok — [presence] Ledger dump of `v2-persistent.responses.jsonl`: `accepted` ×20 (20 distinct queries) and `rejected / network error / "non-JSON 200 body: Expec…"` ×80, with no other rejection reason. Every failure is the redirect page; the judge rejected nothing. The fix's line reads `demo-lost`, not a judge outcome. **B:** → §2.1 (plan script: `arm4-qwen35b … exit 1`, 80 `non-JSON` rejections); fix: `v2-persistent … demo-lost (exit 1, 80 redirected attempts)`.<br>(5) 403. **A:** ok — [compat] Run through the revised script with the stand-in answering 403 to every POST: `v2-403 … demo-unavailable (v2-403 demo check: '403 ')`, and `jev_rerank.py` never ran (no `v2-403.log`). A 403 is a configuration error, not a judge outcome. Recording it as unavailable blocks the arm until the check passes, which matches the prose's "stop and fix". **B:** ok — [compat] Same run: no run file, so it cannot be admitted to Step 3. For a 403 that starts mid-arm, `jev_rerank` aborts (`test_403_aborts_the_whole_invocation`) and the post-check's `[ "$STATUS" = "200 " ]` fails on the 403. That is a code read of `run-arm-v2.sh:14-17`: it records `demo-lost`.<br>(6) Interrupted, no line. **A:** ok — [compat] Read `run-arm-v2.sh`: a status line is written only at `:8` (failed pre-check), `:17` (`demo-lost`) or `:19` (`exit $RC`), and `:17`/`:19` run only after `jev_rerank.py` (`:10`) returns. Run: SIGTERM to the arm's whole process group mid-run (`v2-interrupted`; the stand-in had already been stopped, hence the 21 `network error` records after 11 `accepted`) left no `arms.status` line and no run file. No outage label is written, so nothing triggers a wrongful outage rerun; the rerun comes only from the roster rule, as for an outage. **B:** → §2.1 (the plan's "no arm ends valid" rule does not block Step 3 on a missing line).<br>(7) Outage that starts and ends inside one arm. **A:** ok — [presence] Ledger dump of `v2-transient.responses.jsonl`: `rejected / network error / "non-JSON 200 body: Expec…"` ×40 (exactly the redirected POSTs 22–61, at 4 attempts each for the 10 fallback queries) and `accepted` ×40 (30 in the first run, 10 on the rerun), with no other rejection reason. The judge rejected nothing. **B:** → §2.1. [compat] Run: the stand-in redirected POSTs 22–61 and then served again. Both the plan script and §2.1's first-proposed fix (`run-arm-fixed.sh`) recorded `armT-transient … exit 1` (10 fallbacks, 40 `non-JSON` rejections, post-check `'200 '`). The revised fix recorded `v2-transient … demo-lost (exit 1, 40 redirected attempts)`, and the rerun recorded `exit 0`.<br>(8) Non-redirect outage mid-arm (server vanishes). **A:** ok — [presence] Ledger dump of `v2-vanish.responses.jsonl`: `accepted` ×11 and `rejected / network error / "<urlopen error [Errno 11…"` ×116, with no other rejection reason. Every failure is a transport error after the stand-in was killed; the judge rejected nothing. **B:** → §2.1 (the plan script records `exit 1`); fix: `v2-vanish … demo-lost (exit 1, 0 redirected attempts)` via the post-check (116 `network error` rejections).<br>(9) Check fails with a non-redirect status (e.g. a 4xx for `score`). **A:** UNVERIFIED: whether the real demo returns such a status for a model or type it cannot judge; it cannot be probed while the demo redirects. The mechanics are run: a stand-in answering 422 to the `score` check only gave `v2-check422 … score demo-unavailable (v2-check422 demo check: '422 ')`, a wait-and-rerun label for what spec §5 would call an invalid arm. → §3.1. **B:** ok — [compat] Same run: `jev_rerank.py` never ran (the only `v2-check422.*` file is the check body), so no run file can be admitted to Step 3. |
| R7 | Step 3/4 selection roster ("the valid arm (`exit 0`)…"; "If no arm ends valid, Task 3 is not done") | **over** (an arm admitted that should not be): ok — [existence] Only an `exit 0` arm writes a run file, which is what `report.py --run $A/screen` scores. **under** (an arm left out because the demo failed, not the judge): → §2.1. |
| R8 | Pre-flight halving loop ("halve `MBS` … repeat Step 7 and this step") | **over** (halving when not needed): dropped — CDR-2 R15/M2 already settled this; not re-raised. **under** (the loop does not reduce `MBS`): → §2.2. |
| R8a | Population closure for R8: shell variables carried across Task 4 steps (`:1174`) × blocks that assign them. Assignment sites, from `command grep -nE "(^\|[; ])(REPO\|SHA\|POLICY\|MBS\|I\|O\|QT\|PFT\|PROV)="` over the plan: `:1196` `REPO`/`POLICY`, `:1197` `SHA`, `:1228` `MBS=32`, `:1239` `I`/`O`/`QT=noul`, `:1253` `PFT`, `:1254` `PROV`. Directions per cell: **over** = a repeated block overwrites a value the loop changed or a later step needs; **under** = a value the next step needs is not (re)derived. | `REPO`, `SHA`, `POLICY`. **over:** ok — [existence] assigned only at `:1196-1197` (Step 4), which the halving loop ("repeat Step 7 and this step") does not repeat. **under:** ok — [existence] the loop leaves them unchanged, and Steps 7–11 read them as Step 4 set them.<br>`MBS`. **over:** → §2.2 (`:1228` `MBS=32` undoes the halving; run in §2.2). **under:** ok — [existence] `PROV` is rebuilt from the current `MBS` at `:1254`, after the exit-0 pre-flight.<br>`I`, `O`. **over:** ok — [existence] `:1239` re-assigns the identical constants `/workspace/in` and `/workspace/out`, so no carried value changes. **under:** ok — [existence] they are set at `:1239`, before their first use at `:1240`.<br>`QT`. **over:** → §2.2 (`:1239` `QT=noul` clobbers `score`). **under:** ok — [existence] it is set at `:1239`, before its first use at `:1242`; after §2.2's fix it is set in Step 4, earlier still.<br>`PFT`, `PROV`. **over:** ok — [existence] assigned only at `:1253-1254`, after the exit-0 branch, from the final `pre-$MBS` ledger. **under:** ok — [existence] Steps 9–11 read `$PROV`, which embeds `PFT` and the final `MBS`.<br>Task 1 and Task 5 preludes (`R S B SF NF A`, and `BASE QRELS MAIN`). **over:** ok — [existence] they are constants, so re-running a prelude is idempotent. **under:** ok — [existence] each shell starts with its prelude (`:125`, `:1328`). |
| R9 | Step 12 equality `{(SHA, POLICY)}` | **over:** ok — [existence] `model_revision` is the `revision` that `from_pretrained` loads (`hf_server.py:1220-1242`, `:1273`). **under:** ok — [existence] An explicit policy returns `requested` unchanged (`hf_prompt_policies.py:58-60`). |
| R10 | Worst-case bound | **over:** ok — [compat] Only `['1','3']` differ (T5.2). **under:** ok — [compat] Both equal `reversed(baseline)`. |
| R11 | Step 8 order check (amended) | **under:** ok — [compat] It printed `50-` / `51:` / `52-` on the correct insertion. **over:** ok — [existence] On a row inserted above `:50`, the `-B1` line is not the relation-popularity row, so the check fails. |
| R12 | Stub doc-text guard | **over:** ok — [compat] The negative probe returned 422 (T2.2). **under:** ok — [totality] 300/300 accepted per mode. |

### Round-2 mandatory rows

| # | Row | Disposition |
|---|---|---|
| M1 | (a) CIR-1 §2.1 fix site (Task 5 Step 8 check) and restatement P14 | ok — [compat] Run: `grep -n` over the current ranked-changes doc gives `50:| Relation-popularity signal …`, `55:… The seven rows added since were measured at the` and `56:shipped window instead: …`, so P14's `:50` and `:52-59` anchors hold. Run: Step 8's row insert and paragraph replace on a copy (`old` count 1) followed by the amended check printed `50-| Relation-popularity…`, `51:| SimpleJEV relevance judge…` and `52-` (`ranked.md`, md5 `8b490b70…`). |
| M2 | (a) CIR-1 §2.2 fix sites (Task 4 Steps 2–3) and restatement U3 | ok — [existence] Plan `:1178` now reads "container disk ≥ 150 GB **and volume disk ≥ 150 GB** (`HF_HOME` is on the `/workspace` volume)", and the Step 3 block adds `df -h /workspace` with "at least 100 GB available". A fresh fetch of docs.runpod.io/pods/storage/types returned U3's quotes verbatim: "/workspace (default)" for the volume mount; "temporary storage for the operating system and session data" for the container disk; no default sizes stated. |
| M3 | (a) CIR-1 §3.1(a) fix sites: `check-demo.sh`, `run-arm.sh`'s check line, Step 2's gate and monitor prose, and restatement P12 | → §2.1 |
| M4 | (b) CIR-1 §3.1(a)'s option text, applied verbatim in spirit: "Add the same one-line check to `run-arm.sh` before each arm, so that an outage mid-screen fails that arm fast instead of grinding through its 323 queries" ∩ this round's mid-arm probe | → §2.1. The probe shows an outage during an arm still grinds through every remaining query (80 rejected `network error` records for 20 queries) and is recorded as `exit 1`. |
| M5 | (b) P12's "re-checked 20:23 UTC" ∩ this round's 23:48 UTC probe | ok — [compat] The live `check-demo.sh` printed `301 https://featherless.ai/simple-jev/classifier`. P12 as amended ("the demo's did until 2026-09-30") still holds, and Ben's option (a) wait stands. |
| M6 | (b) CDR-2 R16/M2/F14 (rows = `min(MBS, max_batch_tokens // width)`) ∩ §2.2's fix | ok — [existence] Read `hf_server.py:533`: `limit = min(self.max_batch_size, self.max_batch_tokens // width)`. §2.2's fix changes only which `MBS` value reaches `--max-batch-size` (run in §2.2: `MBS=${MBS:-32}` keeps 16 after halving). It does not change how the server uses that value, so CDR-2 M2's settled point (halving is a no-op wherever `max_batch_tokens // width` < `MBS`) is untouched and is not re-raised. |
| M7 | (c) Amendment hunks `git diff 81a34247 -- <plan>` | ok — [existence] `git diff … \| grep "^@@"` gives seven hunks (`1 file changed, 43 insertions(+), 10 deletions(-)`), each mapped to its row:<br>`@@ -86,7 +86,7 @@` (P12) → M3/M5.<br>`@@ -101,6 +101,7 @@` (U3) → M2.<br>`@@ -1068,16 +1069,41 @@` (Task 3 Step 1, `check-demo.sh` and `run-arm.sh`) → M3, T3.1, T3.2.<br>`@@ -1086,7 +1112,13 @@` (Step 2 gate) → M3, T3.3.<br>`@@ -1098,7 +1130,7 @@` (monitor prose) → M3, M4, T3.3.<br>`@@ -1143,16 +1175,17 @@` (Task 4 Steps 2–3) → M2, T4.2, T4.3.<br>`@@ -1437,7 +1470,7 @@` (Task 5 Step 8 check) → M1.<br>`git log 81a34247..HEAD -- <plan>` lists only `bbef7faf` "applied 4 fixes from …", so every hunk is in-band. |
| M8 | Drift: merge `519ae09d` during this round | ok — [negative] `git diff --name-status bbef7faf..HEAD` shows three `A` paths. `ls scripts \| grep -i "conftest\|pytest.ini\|jev"` finds nothing, so A21 (no `*jev*` collision) and P6 (no conftest) still hold. The ranked-changes doc is unchanged (`git log -1` = `7aab0856`). |

### Dropped candidates

| # | Candidate | Disposition |
|---|---|---|
| D1 | Task 3 Step 4 runs arm 6 (40–80 min) in the foreground, unlike arms 1–5 (`setsid nohup`) | dropped — An interruption leaves no `arms.status` line and a resumable ledger. That is loud and resumable, not a wrong outcome, and §2.1's roster gate now covers the "no line" state. |
| D3 | New GATE docs on `main` (e.g. `2026-09-GATE-matchpattern-dialogue-sequences.md`, from `519ae09d`) may get §0 rows before Task 5 runs | dropped — Task 5 edits the worktree's copy, which is frozen at branch time, where `:50` holds (T5.8). A later row on `main` is a merge-time conflict, which is Ben's call. |
| D4 | `pkill …; sleep 10` may not free GPU memory before the relaunch | dropped — speculative, and a failed relaunch is loud. |

**Totals: 75 rows. 57 ok; 14 → findings (T3.1's classification part, T3.2, T3.3, T3.5, T4.7, T4.8, C3, C11, C12, R6, R7, R8, M3, M4 → §2.1/§2.2/§3.1); 4 dropped (T4.11, D1, D3, D4).** R6a and R8a are population-closure matrix rows; their cells are dispositioned inline and counted with R6/R8.

## 1. Verified-plan-assumptions cross-check

- P1 still holds. [negative] `git worktree list | grep -c simple-jev` → `0`; `git branch --list 'simple-jev*' | wc -l` → `0`.
- P2 still holds. [existence] `grep -n "^def"` on `teacher_rerank.py`: `load_run:75`, `load_corpus:117`, `load_queries:122`, `select_subsample:140`, `read_responses_ledger:214`, `accepted_entry:253`, `append_response:264`, `make_record:272`, `validate_ledger_pass:306`, `sidecar_path_for:508`, `validate_inputs_before_any_model_call:583`. The signatures were read at those lines.
- P3 still holds. [compat] `stub_vllm_server.py`: `def load_queries_by_text:85` and `if __name__` at `:185`. The plan's stub resolved 943/943 states through it (600 SciFact, 323 NFCorpus and 20 NFCorpus requests, 0 fallbacks).
- P4 still holds. [totality] `awk '{print $1}' … | uniq | wc -l` gives 323 (NFCorpus) and 300 (SciFact), equal to the distinct counts. `head -n 1000` on NFCorpus gives `uniq -c` = 20 runs of exactly 50.
- P5 still holds. [totality] `awk '$1=="129"' … | wc -l` → `50`.
- P6 still holds. [compat] `test_teacher_rerank.py` → `44 passed`. `ls scripts | grep -i "conftest\|pytest.ini"` is empty, and there is no `pytest.ini`, `setup.cfg`, `tox.ini` or `pyproject.toml` at the repo root (M8).
- P7 still holds. [compat] `report.py:641-660` was read: the `--baseline` file is excluded from the runs if listed and read separately. Run: Task 5 Step 4's `--run REPEAT --baseline MAIN` exited 0 and printed `[compare] jev-repeat.chunks.trec vs jev-main.chunks.trec`. The mutual-exclusion exit is at `:886`, a cite offset CIR-1 already noted, so it is not re-raised.
- P8 still holds. [presence] `grep -o '"composite": *"[0-9a-f]*"'` on both `bge-base.meta.json` files → `7d3a15092f963723` for each.
- P9 still holds. [existence] `hf-server/pyproject.toml:10-14`: `requires-python = ">=3.12"`, `torch>=2.6`, `transformers>=5.16.1,<6`, `[project.scripts]`.
- P10 still holds. [negative] `command grep -n` for every `import` line in the plan's code blocks:
  - Task 1's test imports `jev_rerank` (`:159`), created in the same task (Step 4).
  - `jev_rerank` imports `teacher_rerank` (`:500`), which is on `main`.
  - Task 2's stub imports `stub_vllm_server` and `teacher_rerank` (`:917-918`), both on `main`.
  - Task 2 Step 2 and Task 3's `check-demo.sh` import `jev_rerank` (`:1015`, `:1086`), from Task 1.
  - Task 5's snippets import `teacher_rerank`, `jev_rerank`, `ir_measures` and `scipy` (`:1351`, `:1372-1373`, `:1389`, `:1411-1412`), all of which exist by Task 5.
  - No line imports a file from a later task.
- P11 still holds. [compat] Run against a real local `ThreadingHTTPServer`: `test_call_jev_returns_the_http_code_body_and_retry_after` passes (`("http", (429, "queue full"), "3")`), which exercises `.code`, `.read()` and `.headers`; `test_call_jev_posts_json_…` passes on a 200.
- P12, as amended, still holds. [existence] The hf_server half: `restore_binary_noul` sets `{'type': 'noul', 'noul': …['yes']}` (`hf_prompt_policies.py:182-186`), `"score": mean` (`response_scoring.py:318`) and `"usage": {"input_tokens": …}` (`:160`). [compat] The demo half: the live `check-demo.sh` printed `301 https://featherless.ai/simple-jev/classifier` at 23:48 UTC (M5).
- P13 still holds. [compat] Run of Task 5 Step 3: `bge-base.chunks.trec {'R@50': 0.9337, 'AP': 0.7018, 'nDCG@10': 0.7452, 'P@10': 0.0993, 'R@10': 0.877}` and the reversed run `{… 'nDCG@10': 0.0011, 'AP': 0.0212, 'R@10': 0.0033, 'P@10': 0.0003}`, matching spec §3's identity and reversed rows.
- P14 still holds. [compat] `grep -n` gives the relation-popularity row at `:50` and the paragraph text at `:55-56` (M1).
- P15 still holds. [negative] `ls docs/plans/2026-09-GATE-simple-jev-judge.md` → "No such file or directory". `git check-ignore -v docs/plans/x.md` → `.gitignore:49:**/docs/plans/`. `git ls-files` lists the ranked-changes doc.
- P16 still holds. [existence] `git log --format=%s -8`: every plan, review and fix commit is lowercase imperative with no prefix. The one exception is the merge's git-default `Merge branch '…'`, which is not a plan commit.
- P17 still holds. [bidirectional] C1: every flag used is defined in `build_arg_parser`, and every required flag is supplied at each call site; `check-demo.sh` calls `build_request` with its 4-arg signature (ran).
- P18 still holds. [compat] `report.py:379-386` was read, and `report-gate.txt` prints `R@50 0.9337` in each `[scores]` block.
- P19 still holds. [compat] Run of Task 5 Step 4 → `50 query ids, 55 qrels rows`, with its `assert ids == set(load_run(REPEAT))` passing.
- P20 still holds. [compat] Run of Task 5 Step 5 with `PYTHONPATH=…/python-libs` → `20 queries in both ledgers  mean rho -1.000`.
- P21 still holds. [totality] `33 passed`; `mutate.py` → 27/27 `KILLED`, `survivors/pattern errors: 0`.
- P22 still holds. [compat] Run of Task 2 Steps 2–4: `identity exit 1` at 0.7452 / 0.9337, `reversed exit 0` at 0.0011 / 0.9337, md5s `3e46051e…` and `c7c7a298…`, and the 422 negative probe verbatim.
- P23 still holds. [compat] Task 5 Steps 1–5 ran on the stub artifacts (T5.1–T5.5). The bound run with `['1','3']` reversed exactly those two queries.
- P24 still holds. [presence] `scifact-bge-base-2026-09-04/keymap.json.stats.json`: `"chunk_max_chars": 512`, `"chunk_step": 448`.
- U1 still holds as the plan states it: an unverified pod property with a runtime check. [existence] Step 3's block (`:1182-1186`) prints `python3 --version`, the torch version and CUDA availability before anything is installed, and Step 3's prose requires terminating and re-renting on failure. Span item 2 verifies the client scripts at the 3.12 floor.
- U2 still holds. [existence] Stage-2 runbook `:112-124` was read: `tar czf … && md5sum … && runpodctl send` and `runpodctl receive <code> && md5sum …   # must match`. `setsid nohup python3 …` follows at `:137`.
- U3 still holds. [existence] A fresh fetch of docs.runpod.io/pods/storage/types quotes the volume "Mount path … /workspace (default)" and the container disk as "temporary storage for the operating system and session data", with no default sizes stated (M2).

**Span check. Two uncovered dependencies:**
1. **The demo accepts `score`-type questions.** Arm 6, and `check-demo.sh` for arm 6, depend on it. UNVERIFIED: A24 covers PLAIN-478 for `noul` only, and the spec's demo probes are `noul` and `choice`. CIR-1's one `score` probe hit the redirect (`cir1/probe-score-resp.json` = `["network", "non-JSON 200 body: …"]`). It cannot be verified while the demo redirects → §3.1.
2. **The two scripts shipped to the pod run on Python 3.12, the floor U1 allows.** [existence] Verified in-round at parse tier: `ast.parse(feature_version=(3,12))` and `(3,11)` pass for `jev_rerank.py` and `teacher_rerank.py`, and they import only long-standing stdlib modules. A failure would also be loud at Step 8, before any pass runs.

## 2. Literal-wrongness findings

### 2.1 A demo outage during an arm is recorded as an invalid arm, and the screen can pick a winner without it

**Description.** CIR-1 §3.1(a), the option Ben chose, made Task 3 wait for the demo. Its option text claimed that a check in `run-arm.sh` "before each arm" makes "an outage mid-screen fail that arm fast instead of grinding through its 323 queries".

The applied fix checks only before an arm starts. If the demo goes away *during* an arm:
1. Every remaining query burns 4 attempts (`301` → GET → `non-JSON 200 body` → retried).
2. The arm ends over the cap, and `run-arm.sh` appends `… exit 1`. That is the same line a judge-invalid arm writes. It is not `demo-unavailable`, the only status the Step 2 prose routes to a rerun.
3. Step 3 then takes the leader from the arms with `exit 0`.

Step 2's prose also requires a rerun only when "no arm ends valid". So an arm that never started (`demo-unavailable`), or that was interrupted and left no line, does not block Steps 3–4 either.

The demo has already disappeared once without notice, and the screen runs 4–8 h. One outage can therefore remove one or more arms from a pre-registered selection, silently, because of a third-party outage rather than the judge. That contradicts the wait-for-the-demo ruling the fix implements.

**Evidence.**
- [compat] **Run, the outage itself.** The plan's `run-arm.sh` (paths and URL rewritten to a local stand-in that serves 21 POSTs and then answers `301` → an HTML page, as the demo does now). The check printed `'200 '`. The arm ended with `20 / 40 queries fell back, over the cap of 2`. The ledger counted `('accepted', None): 20, ('rejected', 'network error'): 80`, and `arms.status` read `arm4-qwen35b … noul exit 1`.
- [compat] **Run, the falsifier.** A stand-in that stays up but rejects every judge answer also gives `exit 1` (`armY-invalid`). The two states are therefore indistinguishable in `arms.status`.
- [compat] **Run, resume.** After restoring the stand-in, rerunning the same line resumed and appended `… exit 0`. The recovery works once the arm is recognised.
- [existence] **Plan text.** `:1133`: "`demo-unavailable` in `arms.status` means … rerun that arm's line … If no arm ends valid, Task 3 is not done". `:1142`: "The leader is the valid arm (`exit 0` in `arms.status`)". `:1130`: Step 2 launches with `> /dev/null 2>&1`, so the check's printed status is discarded for arms 1–5.

**Proposed fix.**

1. In `run-arm.sh`, replace the check line with the two lines below. The second records how many redirected (`non-JSON 200 body`) attempts the arm's ledger already holds before this invocation.
   ```bash
   CHK=$(bash $OUT/check-demo.sh "$1" "$2" "$3") || { echo "$1 $2 $3 demo-unavailable ($CHK)" >> $OUT/arms.status; exit 1; }
   N0=$(grep -c 'non-JSON 200 body' $OUT/$1.responses.jsonl 2>/dev/null); N0=${N0:-0}
   ```
2. In `run-arm.sh`, replace the final line `echo "$1 $2 $3 exit $?" >> $OUT/arms.status` with the block below. A failed arm is recorded as `demo-lost` if this invocation added redirected attempts (a redirect outage, whether or not it has ended) or if a post-check fails (an outage still in progress, of any kind). Otherwise its exit status stands.
   ```bash
   RC=$?
   N1=$(grep -c 'non-JSON 200 body' $OUT/$1.responses.jsonl 2>/dev/null); N1=${N1:-0}
   if [ $RC -ne 0 ] && { [ $N1 -gt $N0 ] || ! bash $OUT/check-demo.sh "$1-post" "$2" "$3" > /dev/null; }; then
     echo "$1 $2 $3 demo-lost (exit $RC, $((N1 - N0)) redirected attempts)" >> $OUT/arms.status
   else
     echo "$1 $2 $3 exit $RC" >> $OUT/arms.status
   fi
   ```
3. In Step 2's monitor prose, add `demo-lost` next to `demo-unavailable`: "rerun that arm's line once `check-demo.sh` passes (its ledger resumes)".
4. Replace "If no arm ends valid, Task 3 is not done: wait for the demo and rerun." with: "Before Step 3, each of arms 1–5 must have a **last** `arms.status` line ending `exit 0` or `exit 1`. An arm whose last line is `demo-unavailable …` or `demo-lost …`, or that has no line (interrupted), is rerun first. Step 4 applies the same rule to arm 6."

**Change to this finding during the slot-audit close-out.** The first version of this fix (`run-arm-fixed.sh`) used only the post-check. It was verified in-round to fail one case: an outage that starts and ends inside one arm was still recorded as `armT-transient … exit 1` (10 fallbacks, 40 `non-JSON` rejections, post-check `'200 '`). Steps 1–2 above add the per-invocation redirect count that closes it. The finding's substance and the recommendation are unchanged.

Evidence: [totality] Run. The revised `run-arm.sh` (`A/flaky/run-arm-v2.sh`, md5 `292151a6…`) was run through all nine arm end states in R6a against the local stand-in (`A/flaky/arms.status`, md5 `bf564ec9…`):

| End state | `arms.status` line |
|---|---|
| Persistent redirect outage mid-arm | `v2-persistent … demo-lost (exit 1, 80 redirected attempts)` |
| Outage that starts and ends inside one arm (POSTs 22–61 redirected) | `v2-transient … demo-lost (exit 1, 40 redirected attempts)` |
| Rerun of that arm with the demo serving | `v2-transient … exit 0` (resumed) |
| Server vanished mid-arm (no redirect, 116 `network error` rejections; the post-check branch) | `v2-vanish … demo-lost (exit 1, 0 redirected attempts)` |
| Demo down at start | `v2-down … demo-unavailable (v2-down demo check: '301 http://127.0.0.1:18796/simple-jev/classifier')` |
| Judge-invalid arm with the demo serving, run twice | `v2-invalid … exit 1` both times. This is the falsifying direction: no `demo-lost` and no rerun loop, because a rerun adds no redirected attempts. |
| Valid arm | `v2-valid … exit 0` |
| 403 on every POST (R6a (5)) | `v2-403 … demo-unavailable (v2-403 demo check: '403 ')`; `jev_rerank.py` never ran |
| Interrupted: SIGTERM to the arm's process group (R6a (6)) | no line written (status lines exist only at `run-arm-v2.sh:8`, `:17`, `:19`); no run file |
| Check answered 422 for `score`, demo otherwise serving (R6a (9)) | `v2-check422 … score demo-unavailable (v2-check422 demo check: '422 ')`. Its meaning (wait, or invalid) is §3.1. |

`grep -c` on a ledger with no matches prints `0`, and on a missing ledger (first run) `${N:-0}` gives 0. The `-post` label writes its own `$1-post.check.json`, so it does not overwrite the arm's check body.

### 2.2 Repeating Steps 7–8 as written resets `MBS` to 32 and `QT` to `noul`

**Description.** Task 4 Step 8's OOM branch says to "halve `MBS` (32 → 16 → 8 …), repeat Step 7 and this step". But Step 7's block opens with `MBS=32`, and Step 8's block opens with `I=/workspace/in; O=/workspace/out; QT=noul`.

Repeating the blocks as written does two things:
1. **It undoes the halving.** The server relaunches at 32, and the pre-flight resumes the same rejected-only `pre-32` ledger and fails the same way, on a billed pod. The loop cannot converge.
2. **It can switch the question type.** When arm 6 won, `QT` is reset from `score` to `noul`. `QT` then flows unchanged into Steps 9–11 (`--question-type $QT`), so the main, repeat and transfer passes would judge with the wrong question. Nothing later compares the sidecar's `questionType` with the winner: Step 12 checks only revision and policy.

**Evidence.**
- [compat] Run. With prior state `MBS=16; QT=score`, the plan's own first lines of Step 7 (`block22.sh:1`) and Step 8 (`block23.sh:1`) leave `MBS=32 QT=noul`, followed by `server-32.log, pre-32.responses.jsonl, --question-type noul`.
- [existence] Plan `:1228` `MBS=32`, `:1239` `…; QT=noul`, `:1249` "halve `MBS` … repeat Step 7 and this step", and `:1263`, `:1273`, `:1284` `--question-type $QT`.

**Proposed fix.**
- In Task 4 Step 7's block, change `MBS=32` to `MBS=${MBS:-32}`.
- Change Step 8's first line to `I=/workspace/in; O=/workspace/out`.
- Move the question type into Step 4's block, beside `REPO`/`POLICY`: `QT=noul   # score if arm 6 won (Task 3 Step 4)`. In Step 8's prose, replace "`QT` is `noul`, or `score` if arm 6 won" with "`QT` was set in Step 4".

Evidence: [compat] Run: with `MBS` unset, `MBS=${MBS:-32}` gives 32; after `MBS=16`, re-running it keeps 16; the shortened Step 8 line leaves `QT=score` intact. [existence] No other block in Task 4 assigns `MBS` or `QT` (R8a, from `grep -n "MBS=\|QT="` over the plan: only `:1228`, `:1239`).

## 3. Forced decisions

### 3.1 What a demo check that fails without a redirect means for an arm, given that `score` has never been observed on the demo

**The choice.** How Task 3 treats an arm whose `check-demo.sh` fails with a status other than the outage's `301`, such as a `400` or `422` from a demo that is otherwise serving. The case most likely to hit is arm 6, the `score` rubric arm.

**Why it is forced.**
- The amended plan maps *every* failed check to `demo-unavailable`, which means "wait and rerun".
- Spec §5 makes an arm that cannot be judged "invalid for selection". Before the CIR-1 fix, a 4xx produced exactly that: every query falls back at once, and the arm exits 1.
- A model- or type-level rejection will not clear by waiting, so under the current plan Task 3 would never finish.
- Whether the demo returns such a status cannot be checked today. The demo has redirected since about 18:50 UTC on 2026-09-30 (still so at 23:48), and no review has observed a `score` request answered by it (span item 1). `noul` on PLAIN-478 is covered by A24 for all five models.

**Options.**
- **(a) Rule now: a non-redirect 4xx check failure makes the arm invalid.** While another arm's check returns `200`, an arm whose check fails with a 4xx other than 403/429 is recorded as invalid, for example `rejected (<status>)`. This is spec §5's semantics for an arm the demo cannot judge. Applied to arm 6, the winner comes from arms 1–5.
  Evidence: [existence] Spec §5: "More than 16 of 323 makes a screen arm invalid for selection", and "Not retryable: 400 and 422 … falls back immediately". [compat] With §2.1's fix, the check's status is captured in the `arms.status` line (`armZ` run), so the rule can be applied by reading it. UNVERIFIED: that the demo ever returns such a status for `score`.
- **(b) Verify before launching, and rule only if it fails.** Add a `score` check to Step 2's gate, next to the `noul` one: `bash $A/screen/check-demo.sh arm0-score featherless-ai/Qwen3.5-4B-classifier score`. If it fails while the `noul` check passes, stop and ask Ben before launching.
  Evidence: [compat] `check-demo.sh` takes `score` as `$3` and builds a `score` body. Run: `armZ … score` built `armZ.check.json` and recorded the status. UNVERIFIED: the demo's answer, which is the thing this option measures. It costs one public NFCorpus request.
- **(c) Accept the risk.** Keep the plan as is. If arm 6's check keeps failing, the operator sees the status and asks Ben at that point.
  Evidence: [existence] Arm 6 runs in the foreground (Task 3 Step 4, `:1147`), so `check-demo.sh`'s `echo "$1 demo check: '$STATUS'"` reaches the terminal even without §2.1's fix. Arms 1–5 run under `> /dev/null 2>&1` (`:1130`), so for them the status is visible only with §2.1's fix.

**Why no option dominates.** (b) adds evidence at the cost of one request, but if the check fails it still needs (a)'s ruling or Ben's call, so it complements (a) rather than replacing it. (a) settles the semantics in advance but reinterprets the wait-for-the-demo ruling for a case that may never arise. (c) costs nothing and decides only if the case occurs, at the price of a possibly indefinite wait if nobody reads the status. Any combination still requires Ben to say whether a persistent non-redirect rejection means "invalid" or "wait".

## 4. Previously addressed

- CIR-1 §2.1 (the order check could not confirm the insertion): resolved. Task 5 Step 8 now uses `grep -n -B1 -A1`, which was run on the inserted copy and printed `50-` / `51:` / `52-` (T5.8, R11).
- CIR-1 §2.2 (the weights' disk was unsized): resolved. Step 2 rents a volume ≥ 150 GB, Step 3 checks `df -h /workspace` ≥ 100 GB, and U3 records the source (M2).
- CIR-1 §3.1 (the demo no longer serves): resolved by Ben's option (a). `check-demo.sh` gates Step 2 and every arm, and Tasks 3–5 wait. The demo still redirects at 23:48 UTC, which is the wait working as chosen. The mid-arm gap in that fix is new and is §2.1.
- CIR-1 §1 (P12's demo half failed): resolved. P12 has been rewritten to state the redirect and the gate.

## 5. Recommendation

🛑 **Surface forced decisions to user.** §3.1 needs Ben's ruling on what a non-redirect demo-check failure means for an arm, since `score` has never been seen on the demo. §2.1 (an outage during an arm silently drops it from the selection) and §2.2 (the pre-flight loop's blocks reset `MBS` and `QT`) must also be applied before SDD.
