# MatchPattern Dialogue Pool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-02-matchpattern-dialogue-pool-design.md` (commit SHA: `d5bfc011`)

**Goal:** Test, under a pre-registered non-inferiority gate (the 95% CI lower bound of pool-semantic − ordered-keyword test macro-F1 must exceed −0.02), whether a frozen description pool closes the MatchPattern dialogue experiment's gap between `SIMILARITY` defines and keyword flags.

**Architecture:** A new harness script, `dialogue_pool.py`, reuses `dialogue_patterns.py` (unmodified) and reads the 2026-09-29 run's ingested `DialogueTurn` rows; it never writes them. `select` greedily picks up to three phrasings per intent on dev and checks a fidelity pin before anything touches test. `score` runs both gated arms once on test. A gate doc records the verdict.

**Tech stack:** Python 3.14; `dialogue_patterns`/`pattern_leg` and the in-repo `iverson_client` SDK; numpy (under the `python-libs` `PYTHONPATH`); pytest; the local compose stack on its un-rebuilt 2026-09-28 `iverson-api` image.

---

## Global Constraints

- **The code is the patch.** Task 1's code is ONE `git apply` patch, generated from a prototype that compiled and passed. It was verified to apply to `main` at `d5bfc011` and to reproduce both listed md5s. Never retype it: slice it from the task brief with the given command and check the md5s.
- **Pre-registration.** The test split is touched only by Task 3's `score`, exactly once; `results.json` refuses to be overwritten. Never compute test-split pool predictions any other way, and never edit `POOL`, `FIDELITY_PIN`, the selection rule or the gate after `select` has run.
- **No rebuild.** Never rebuild or recreate the stack. Start the existing containers with `docker start` only. Every pass checks `/build` against `ingest.json`'s composite `ded69e9492bdc081`; any other composite aborts the run (spec, Execution).
- **Live steps run in a normal session.** A worktree-isolated session refuses to source the state file or pass runtime env to python3, so Tasks 2 and 3 must run where that is allowed. Every live bash block starts by sourcing the state file. The state file re-reads every secret from `.env` each time it is sourced and never stores one. Never print a secret.
- **Never write into the 2026-09-29 run directory.** It is `--from-run`, read only.
- **Commits:** a lowercase imperative subject, a blank line, then `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` (two `-m` flags). Commit explicit paths only (`git commit … -- <paths>`). `docs/` is gitignored: use `git add -f`.
- **Shell `grep` skips gitignored paths, including `docs/`.** Use `command grep`.

## File Structure

- **Create:** `Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py` (Task 1).
  - The frozen `POOL`, `FIDELITY_PIN`, `MAX_PHRASINGS` and `NON_INFERIORITY_BOUND`.
  - Pure functions: `select_phrasings`, `combine`, `check_fidelity`, `pool_define`, `pool_arm_request`, `gate`, `predictions_in_order` and `score_results`.
  - `cmd_select` and `cmd_score`, plus `--out`/`--from-run` wiring that mirrors `dialogue_patterns.main`.
- **Test:** `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py` (Task 1). 19 tests:
  - pure tests of selection (strict improvement, ties to the lowest index, the cap of three);
  - the `OR` `DEFINE` and its escaping, and the arm request;
  - the gate bound, the fidelity pin and prediction order;
  - the refusal guards;
  - four end-to-end tests over `test_dialogue_patterns`' synthetic splits and `FakeEngine`, with a `PoolEngine` subclass that evaluates `OR` `DEFINE`s.
- **Create, outside the repository (Task 2):**
  - the state file `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogue-pool-shell.sh`;
  - under `OUT` (`/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogue-pool-2026-10-02/`, which already holds `design-probes/`): `selection.json`, `select.log`, then `results.json` and `score.log` (Task 3).
- **Create:** `docs/plans/2026-10-GATE-matchpattern-dialogue-pool.md` (Task 3).

## Inherited from spec

These assumptions were verified by `thorough-brainstorming` and reconfirmed by CDR rounds 1 and 2 (round 2 ✅). They are not re-verified here. The table is copied verbatim from the spec's "Verified assumptions":

| # | Assumption | Evidence |
|---|---|---|
| 1 | The offline replica reproduces the live run exactly. A turn's `SIMILARITY` is the cosine between the description's bge-base query embedding (with the prefix "Represent this sentence for searching relevant passages: ") and the turn's stored `utterance_vector`. | `SimilarityResolver.cs:17-31,50-60` (`EmbedQueryAsync`, `TensorPrimitives.CosineSimilarity`); `EmbeddingPrefixes.cs:37`. The replica (stored Qdrant vectors plus TEI `/embed` with the prefix) recomputed `theta.json`'s θs to 7 digits (hotel 0.5332754 both ways) and test macro-F1 0.5102 / keyword 0.7432 exactly. |
| 2 | Attraction's threshold is the main cause of the gap. | `iverson-benchmark-corpora/matchpattern-dialogue-pool-2026-10-02/design-probes/probe.py` on dev: 26.0% of turns exceed attraction's θ, against 10.0/9.0/9.0% for the others; attraction dialogue precision 0.430. Published per-pair table: the attraction pairs carry about 86% of the test gap. |
| 3 | On dev, approach A reaches the gate's bar: in-sample 0.7295 against keywords' 0.7227, and on a 2-fold held-out split −0.014 and +0.016. The greedy picks are hotel [1,2,0], restaurant [1], train [3,5,0], attraction [0,4,6]. | `iverson-benchmark-corpora/matchpattern-dialogue-pool-2026-10-02/design-probes/probe4.py` against the cached Qdrant vectors and live TEI: held-out A→B 0.7168 against 0.7006; B→A 0.7288 against 0.7424. |
| 4 | Alternatives B and C, and pair-tuned θ, lose on dev. | `iverson-benchmark-corpora/matchpattern-dialogue-pool-2026-10-02/design-probes/probe3.py`: three hand-written phrasings 0.6809; hybrid 0.4953 (original descriptions) and 0.6727 (better ones); pair-tuned θ held-out 0.6709 against 0.6680 and 0.6993 against 0.6902. |
| 5 | A `DEFINE` may `OR` several `SIMILARITY` terms. | `ExpressionParser.cs:10-11` (`or := and (OR and)*`), `:500` (`SIMILARITY`), `:765` (`SimilarityTermTable.GetOrAdd` per distinct text). |
| 6 | A pair call's distinct `SIMILARITY` terms (at most 6) stay under the per-request cap. | `PatternQueryLimitOptions.cs:20` `MaxSimilarityTerms = 10`, enforced at `ObjectSearchGrpcService.MatchPattern.cs:335`; no compose or deploy file overrides it. |
| 7 | String literals escape a quote by doubling it, and the SDK helper does it. | `ExpressionLexer.cs:38`, `:225-229`; `pattern_leg.py:148-154` (`escape_literal`, `similarity`). |
| 8 | A calibration-shape call can score any phrasing, one text per call. | `dialogue_patterns.py:288-293` (`all_rows_request(split, text)`). |
| 9 | The live machinery and response parsers can be reused from another script. | `dialogue_patterns.py:607-631` (`Live`, `timed_call`), `wait_until_ready`, `matched_dialogues`, `strict_turn_scores` (`:331-382`); `main` (`:885-905`) shows the wiring. |
| 10 | The keyword arm request is reusable unchanged. | `dialogue_patterns.py:312-321` (`arm_request("ordered_keyword", …)`). |
| 11 | The bootstrap accepts arbitrary arm names. | `bootstrap_macro_f1(predictions, gold)` takes `{arm: {pair: [bool]}}` (`dialogue_patterns.py:440-460`). |
| 12 | The stored vectors are intact. | Qdrant collection `dialogue_turns_tenant_bypass_a8j5vpgduxk1bsvma7542yqnk`: `points_count` 14,746, `utterance_vector` size 768. The StarRocks rows are checked at run time by the readiness step. |
| 13 | The output-row and expression limits cover the calls. | The 2026-09-29 gate doc records `MaxOutputRows=18623` and `MaxExpressionLength=25000`. Dev calibration calls return 7,374 rows; the longest pair `DEFINE` is under 1,000 characters. |
| 14 | The build the run must match is `ingest.json`'s composite, and the reused machinery checks it before and after every pass. | `ingest.json` `composite` = `ded69e9492bdc081`; `dialogue_patterns.checked` → `pattern_leg.run_checked_pass` (`pattern_leg.py:536-541`), called with `ingest["composite"]` by readiness (`dialogue_patterns.py:711`), calibrate (`:791`) and score (`:858`). |

## Verified plan-level assumptions

Task 1's code was prototyped on the throwaway branch `pool-proto` (`.worktrees/pool-proto`), two commits on `d5bfc011`. The patch below is `git diff d5bfc011 pool-proto`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Paths | `dialogue_pool.py` and `test_dialogue_pool.py` are new, and the patch creates only these two files. `OUT` exists and holds only `design-probes/`, so neither `selection.json` nor `results.json` exists yet. | The patch's `+++ b/` list. `ls` of `OUT` shows `design-probes` only. |
| 2 | Signature | Every `dialogue_patterns` / `pattern_leg` symbol the script uses exists with the called shape: <br>• `theta_grid(scores, gold, ids)`, `strict_turn_scores(rows, expected, what)`, `matched_dialogues(rows, written, what)`; <br>• `all_rows_request(split, text)`, `arm_request("ordered_keyword", split, x, y, None)`, `_split_rows`, `ORDERED_PATTERN`, `LIMIT`; <br>• `wait_until_ready`, `checked(live, expected, name, body)`, `timed_call`, `Live(out, coordinator, fetch_build, build_url, …)`; <br>• `read_json`, `out_path`, `refuse_existing`, `check_ingest_matches_data`, `written_turns`, `written_dialogues`, `split_ids`, `onsets`, `gold_labels`, `single_intent_gold`, `single_intent_predictions`, `prf`, `macro_f1`, `tie_count`, `bootstrap_macro_f1`, `percentile_ci`, `ensure_data`, `load_splits`; <br>• `EntityCoordinator`, `DialogueTurn`; <br>• `pl.similarity`, `pl.format_theta`, `pl.write_json`, `pl.TokenSession`, `pl.connect`, `pl.fetch_build`, `pl.mint_acting_user_token`, `pl.mint_service_token_from_env`, `pl.BYPASS_USERNAME`. | `dialogue_patterns.py:274-321`, `:331-470`, `:565-640`, `:885-905`; `pattern_leg.py:148-163`. The 19 tests call every one except `main()`'s wiring symbols (`TokenSession`, `connect`, `fetch_build`, `mint_*`, `BYPASS_USERNAME`, `ensure_data`, `load_splits`, `EntityCoordinator`, `DialogueTurn`), which CIR round 1 exercised by driving `main()` against the real 2026-09-29 directory. |
| 3 | Signature | `load_ready_ingest` reads `ingest.json` from `live.out`, so the script loads the from-run `ingest.json` itself (`load_from_run`): it requires a passed readiness check and runs `check_ingest_matches_data`. `ensure_data` md5-checks the pinned files that are present and downloads nothing. | `dialogue_patterns.py:725-730` (`load_ready_ingest`), `:487-511` (`ensure_data`). CDR round 1 ran `ensure_data`, `load_ready_ingest` and `check_ingest_matches_data` on the real 2026-09-29 directory: no download, 4 md5s, pass. |
| 4 | Command | Expected counts: <br>• `test_dialogue_pool.py`: 19 passed; <br>• whole scripts suite (`Iverson.Server/Iverson.LoadTest/scripts`): 577 → 596, with the 1 pre-existing warning. | Measured on `main` (577 passed, 1 warning) and on the prototype (596 passed, 1 warning). |
| 5 | Code validity | The prototype's own `select_phrasings` reproduces `FIDELITY_PIN` on real dev data, and its predicted θs are hotel p90 0.5580, restaurant p89 0.5233, train p90 0.6486 and attraction p92 0.5465. | Offline replica (stored Qdrant vectors plus TEI `/embed` with the bge-base query prefix) through `dialogue_pool.select_phrasings`: `FIDELITY_PIN match: True`. |
| 6 | Mutation | Each guard is falsifiable. Each mutant was applied to the prototype, `test_dialogue_pool.py` run, and the file restored byte for byte: <br>• `cmd_select` skipping the pin fails `test_a_failed_fidelity_pin_writes_no_selection`; <br>• ties to the higher index fail 3 selection tests; <br>• the gate admitting −0.02 fails `test_non_inferiority_gate[-0.02-NO-GO]`; <br>• `AND` instead of `OR` fails the define test; <br>• no strict-improvement stop fails 3 tests; <br>• a cap of 2 fails the cap test; <br>• reversed prediction order fails the score test; <br>• the keyword arm sent pool requests fails the score test (after its define assertions were added); <br>• no `/build` check in `select` fails `test_select_aborts_on_a_rebuilt_stack`; <br>• dropping the grid fails `test_select_runs_one_call_per_phrasing_and_writes_the_selection`; <br>• letting `score` run without a selection fails `test_score_refuses_to_run_without_a_selection`. | 11 of 11 mutants killed. |
| 7 | Consumer impact | `dialogue_patterns.py` and `test_dialogue_patterns.py` are unchanged; the new test file only imports the latter's fixtures. Pytest has no conftest or `pytest.ini` here. | Whole suite 596 passed on the prototype, including all 577 existing tests. `ls` of the scripts dir and `Iverson.LoadTest` shows no `conftest.py` or `pytest.ini`. |
| 8 | Environment | The stack is the 14 `iversonserver` compose containers, all stopped. `iverson-api` serves gRPC on 8080 and `/build` over HTTP on 8081 (both bound to 127.0.0.1) and carries `Patterns__Limits__MaxOutputRows=18623` and `MaxExpressionLength=25000`, which `docker start` keeps. Its image computes composite `ded69e9492bdc081`. | `docker ps -a` labels (project `iversonserver`); `docker inspect iverson-api` port bindings and env; CDR round 2 hashed the image's 8 `Iverson.*.dll` MVIDs with `BuildIdentity.cs:22-42`. 12 of the 14 have a `Config.Healthcheck`; `iverson-worker` and `iverson-zookeeper` have none (`docker inspect`). |
| 9 | Environment | Credentials follow the 2026-09-29 state file: <br>• `bench-env.sh` for `IVERSON_GRPC_URL`, `IVERSON_CLIENT_ID`, the token endpoint and scope; <br>• `IVERSON_CLIENT_SECRET` overridden from `.env`'s `IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET` (the `bench-env.sh` copy is stale); <br>• `IVERSON_ACTING_USER_BYPASS_PASSWORD` from `.env`'s `IVERSON_BYPASS_PASSWORD`. | 2026-09-29 plan, Task 2 setup and its VA 11; `bench-env.sh` exports (values not printed). |
| 10 | Ordering | Task 2 needs Task 1's script, and Task 3 needs Task 2's `selection.json`. `cmd_score` refuses without it. | `cmd_score` reads `selection.json` before any call; `test_score_refuses_to_run_without_a_selection`. |
| 11 | Call count | Logged calls are 32 in `select` and 34 in `score` (12 + 12 + one per selected phrasing: 3 + 1 + 3 + 3). Each subcommand also makes one or more readiness calls, which are not logged. | `cmd_select`/`cmd_score` log through `timed_call` only; `wait_until_ready` calls `pl.execute` directly (`dialogue_patterns.py:634-657`). |
| 12 | Convention | The commit style is a lowercase imperative subject plus the trailer. | `git log --format=%s -8`. |
| 13 | Environment | Every bind-mount source of the 14 containers exists, so `docker start` can succeed. | Verified by CIR round 1 (`docker inspect` mounts; every source path present). |
| 14 | Integrity | `main()` reads the 2026-09-29 directory and never writes it. | CIR round 1 drove the real `main()` against it: the directory's digest was unchanged. |
| 15 | Environment | The live script needs no `QDRANT__SERVICE__API_KEY`, which the state file does not set. | CIR round 1: the script imports and runs `--help` without it. |

## Tasks

### Task 1: The dialogue pool harness

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py`

- [ ] **Step 1: Apply the patch.** `BRIEF` is the path of this task brief. The command slices the brief's only `diff` block byte for byte; never retype it. From the repository root:

```bash
python3 - "$BRIEF" > /tmp/claude-1000/pool-task1.patch <<'PY'
import re, sys
fence = chr(96) * 3
s = open(sys.argv[1]).read()
print(re.search(fence + "diff\n(.*?)" + fence, s, re.S).group(1), end="")
PY
git apply --check /tmp/claude-1000/pool-task1.patch && git apply /tmp/claude-1000/pool-task1.patch
```

If `--check` fails, stop and report. Do not hand-edit. The patch:

```diff
diff --git a/Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py b/Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py
new file mode 100644
index 00000000..5f3431cb
--- /dev/null
+++ b/Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py
@@ -0,0 +1,313 @@
+"""MatchPattern dialogue pool: can a pre-registered SIMILARITY description pool match the keyword arm?
+
+Spec: docs/specs/2026-10-02-matchpattern-dialogue-pool-design.md. Follows the 2026-09-29 dialogue-sequence
+run (dialogue_patterns.py, unmodified), whose ingested DialogueTurn rows this script reads and never writes.
+
+Subcommands, both taking --out (this run's directory) and --from-run (the 2026-09-29 run directory, whose
+ingest.json and md5-pinned data/ are read, never written):
+
+    select   dev: one calibration-shape call per pool phrasing (32), greedy max-combination of up to three
+             phrasings per intent by the original single-intent theta_grid rule, the fidelity pin, then
+             selection.json.
+    score    test: 12 pool-semantic pair calls, 12 keyword pair calls, one single-intent call per selected
+             phrasing; results.json with per-dialogue predictions, the paired-bootstrap CI and the
+             non-inferiority verdict.
+
+Every pass checks /build before and after against ingest.json's composite (the 2026-09-29 build); a
+rebuilt stack aborts the run. Run with the python-libs PYTHONPATH (numpy) and the credentials pattern_leg
+reads from the environment (the run's state file sets them)."""
+import argparse
+import os
+import sys
+
+sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
+import dialogue_patterns as dp  # noqa: E402
+
+pl, pb = dp.pl, dp.pb
+
+# ── Pre-registered constants (spec "Pre-registration"; never edited after selection) ──────────
+
+POOL = {
+    "find_hotel": (
+        "the customer is looking for a hotel to stay at",
+        "the customer needs a place to stay, such as a hotel or guesthouse",
+        "the customer asks about a hotel's stars, parking, wifi or price",
+        "I need somewhere to stay tonight",
+        "book a room at a guesthouse",
+        "the customer wants accommodation in the north of town",
+        "looking for a cheap 4 star hotel with free parking",
+        "the customer asks to book a hotel for several nights",
+    ),
+    "find_restaurant": (
+        "the customer is looking for a restaurant to eat at",
+        "the customer wants somewhere to eat that serves a certain kind of food",
+        "the customer asks about a restaurant's food type, price range or area",
+        "I want to find a place to eat",
+        "an expensive italian restaurant in the centre",
+        "the customer wants to book a table for dinner",
+        "looking for cheap chinese food",
+        "the customer asks for a restaurant recommendation",
+    ),
+    "find_train": (
+        "the customer is looking for a train",
+        "the customer wants a train departing from or arriving at a station",
+        "the customer asks about train times, departures or arrivals",
+        "I need a train to cambridge on saturday",
+        "a train leaving after 10:00 from london",
+        "the customer wants to book train tickets",
+        "what time does the train arrive",
+        "the customer is travelling by train",
+    ),
+    "find_attraction": (
+        "the customer is looking for an attraction such as a museum, college, park or theatre",
+        "the customer asks about places to go and things to see in town",
+        "the customer wants to visit a museum",
+        "the customer wants a tourist attraction to visit",
+        "is there a college or a church I can visit",
+        "something fun to do in the centre of town",
+        "the customer asks about entertainment, nightclubs or swimming pools",
+        "the customer wants the entrance fee and address of a sight",
+    ),
+}
+
+MAX_PHRASINGS = 3
+FIDELITY_PIN = {"find_hotel": [1, 2, 0], "find_restaurant": [1], "find_train": [3, 5, 0],
+                "find_attraction": [0, 4, 6]}
+NON_INFERIORITY_BOUND = -0.02                   # GO when the CI lower bound of pool - keyword exceeds this
+ARMS = ("pool_semantic", "ordered_keyword")
+ORIGINAL_SEMANTIC_MACRO_F1 = 0.5102             # 2026-09-29 gate doc, ordered_semantic; point estimate only
+
+
+def say(message):
+    print(f"[dialogue_pool] {message}", flush=True)
+
+
+def fail(message):
+    sys.exit(f"[dialogue_pool] {message}")
+
+
+# ── Selection (pure) ─────────────────────────────────────────────────────────────────────────
+
+def combine(score_maps):
+    """{turn: max s over the given per-phrasing score maps}; every map covers the same turns."""
+    first = score_maps[0]
+    return {turn: max(m[turn] for m in score_maps) for turn in first}
+
+
+def select_phrasings(per_phrasing_scores, gold, dialogue_ids):
+    """Spec "Selection": greedily add phrasings (max-combined) while dialogue-level single-intent F1 at the
+    theta_grid optimum strictly improves; the lowest pool index wins equal F1s; at most MAX_PHRASINGS.
+    Returns {"chosen", "theta", "percentile", "f1", "at_grid_edge", "grid", "steps"}."""
+    chosen, current, steps = [], None, []
+    while len(chosen) < MAX_PHRASINGS:
+        best = None                                           # (f1, index, grid)
+        candidates = []
+        for index in range(len(per_phrasing_scores)):
+            if index in chosen:
+                continue
+            grid = dp.theta_grid(combine([per_phrasing_scores[i] for i in chosen + [index]]), gold, dialogue_ids)
+            candidates.append({"index": index, "f1": grid["f1"]})
+            if best is None or grid["f1"] > best[0]:          # strict: an equal F1 keeps the lower index
+                best = (grid["f1"], index, grid)
+        if best is None or (current is not None and not best[0] > current["f1"]):
+            steps.append({"candidates": candidates, "added": None})
+            break
+        chosen.append(best[1])
+        current = best[2]
+        steps.append({"candidates": candidates, "added": best[1]})
+    return {"chosen": chosen, "theta": current["theta"], "percentile": current["percentile"],
+            "f1": current["f1"], "at_grid_edge": current["at_grid_edge"], "grid": current["grid"], "steps": steps}
+
+
+def check_fidelity(picks):
+    """Abort unless the live selection equals the offline replica's dev selection (spec "Fidelity pin")."""
+    if picks != FIDELITY_PIN:
+        fail(f"fidelity pin FAILED: live selection {picks} != offline replica {FIDELITY_PIN} -- the live "
+             "SIMILARITY path differs from the replica; stop before any test call")
+
+
+# ── Requests (pure) ──────────────────────────────────────────────────────────────────────────
+
+def pool_define(phrasings, theta):
+    """SIMILARITY(Utterance, 'd1') > theta OR ... -- identical to max over the phrasings > theta."""
+    return " OR ".join(f"{pl.similarity('Utterance', p)} > {pl.format_theta(theta)}" for p in phrasings)
+
+
+def pool_arm_request(split, x, y, defines):
+    """The pool arm's call for one ordered pair: dialogue_patterns.arm_request's shape with the pool
+    defines. `defines` is {intent: DEFINE expression}."""
+    return (dp._split_rows(split).pattern(dp.ORDERED_PATTERN).define("A", defines[x]).define("B", defines[y])
+            .define("Z", "TRUE").rows_per_match(pb.ONE_ROW).limit(dp.LIMIT).build())
+
+
+# ── Scoring (pure) ───────────────────────────────────────────────────────────────────────────
+
+def gate(ci):
+    """Non-inferiority: GO when the 95% CI lower bound of pool - keyword macro-F1 exceeds -0.02."""
+    return "GO" if ci[0] > NON_INFERIORITY_BOUND else "NO-GO"
+
+
+def predictions_in_order(matches, dialogue_ids):
+    """{arm: {pair: [bool per dialogue in dialogue_ids order]}} from {arm: {pair: set of DialogueIds}}."""
+    return {arm: {pair: [d in matches[arm][pair] for d in dialogue_ids] for pair in dp.PAIRS} for arm in ARMS}
+
+
+def score_results(splits, matches, single_scores, selection):
+    """Everything results.json holds except the call log and /build record. `matches` is {arm: {pair: set}},
+    `single_scores` {intent: {(DialogueId, TurnIndex): s}} (max over the selected phrasings), `selection`
+    {intent: {"theta", ...}}."""
+    test = splits["test"]
+    dialogue_ids = dp.split_ids(test)
+    by_dialogue = {d["dialogue_id"]: dp.onsets(d) for d in test}
+    gold = dp.gold_labels(by_dialogue, dialogue_ids)
+    predictions = predictions_in_order(matches, dialogue_ids)
+    pairs = []
+    for x, y in dp.PAIRS:
+        pairs.append({"x": x, "y": y, "gold_positive": sum(gold[(x, y)]), "reversed_negative": sum(gold[(y, x)]),
+                      "arms": {arm: {**dp.prf(predictions[arm][(x, y)], gold[(x, y)]),
+                                     "predicted": sum(predictions[arm][(x, y)])} for arm in ARMS}})
+    macro = {arm: dp.macro_f1(p["arms"][arm]["f1"] for p in pairs) for arm in ARMS}
+    draws = dp.bootstrap_macro_f1(predictions, gold)
+    ci = dp.percentile_ci(draws["pool_semantic"] - draws["ordered_keyword"])
+    single_gold = dp.single_intent_gold(by_dialogue, dialogue_ids)
+    single = {intent: {**dp.prf(dp.single_intent_predictions(single_scores[intent], selection[intent]["theta"],
+                                                             dialogue_ids), single_gold[intent]),
+                       "gold_positive": sum(single_gold[intent])}
+              for intent in dp.INTENTS}
+    return {
+        "dialogues": len(dialogue_ids), "dialogue_ids": dialogue_ids, "ties": dp.tie_count(by_dialogue),
+        "pairs": pairs, "macro_f1": macro,
+        "bootstrap": {"resamples": dp.BOOTSTRAP_RESAMPLES, "seed": dp.BOOTSTRAP_SEED,
+                      "ci_percentiles": list(dp.CI_PERCENTILES),
+                      "pool_minus_keyword": {"delta": macro["pool_semantic"] - macro["ordered_keyword"], "ci95": ci}},
+        "non_inferiority_bound": NON_INFERIORITY_BOUND, "gate": gate(ci),
+        "pool_minus_original_semantic": {"delta": macro["pool_semantic"] - ORIGINAL_SEMANTIC_MACRO_F1,
+                                         "original": ORIGINAL_SEMANTIC_MACRO_F1},
+        "single_intent": single,
+        "predictions": {arm: {f"{x} -> {y}": predictions[arm][(x, y)] for x, y in dp.PAIRS} for arm in ARMS},
+    }
+
+
+# ── Live steps ───────────────────────────────────────────────────────────────────────────────
+
+def load_from_run(from_run, splits):
+    """The 2026-09-29 run's ingest.json, which must record a passed readiness check and match the data."""
+    ingest = dp.read_json(dp.out_path(from_run, "ingest.json"), "the --from-run ingest")
+    if not ingest.get("readiness"):
+        fail(f"{from_run}/ingest.json records no passed readiness check")
+    dp.check_ingest_matches_data(ingest, splits)
+    return ingest
+
+
+def cmd_select(live, splits, from_run):
+    selection_path = dp.out_path(live.out, "selection.json")
+    dp.refuse_existing(selection_path, "the selection is frozen once written (spec: pre-registered)")
+    ingest = load_from_run(from_run, splits)
+    dev = splits["dev"]
+    dialogue_ids = dp.split_ids(dev)
+    gold = dp.single_intent_gold({d["dialogue_id"]: dp.onsets(d) for d in dev}, dialogue_ids)
+    expected = dp.written_turns(ingest, "dev")
+    calls = []
+
+    def body():
+        dp.wait_until_ready(live, "dev", expected)
+        scores = {}
+        for intent in dp.INTENTS:
+            scores[intent] = []
+            for index, phrasing in enumerate(POOL[intent]):
+                what = f"calibration {intent} [{index}]"
+                scores[intent].append(dp.strict_turn_scores(
+                    dp.timed_call(live, dp.all_rows_request("dev", phrasing), what, calls), expected, what))
+        return scores
+
+    scores, check = dp.checked(live, ingest["composite"], "select", body)
+    selection = {intent: select_phrasings(scores[intent], gold[intent], dialogue_ids) for intent in dp.INTENTS}
+    check_fidelity({intent: selection[intent]["chosen"] for intent in dp.INTENTS})
+    for intent in dp.INTENTS:
+        selection[intent]["phrasings"] = [POOL[intent][i] for i in selection[intent]["chosen"]]
+    pl.write_json(selection_path, {"pool": {i: list(POOL[i]) for i in dp.INTENTS}, "selection": selection,
+                                   "calls": calls, "build": check})
+    for intent in dp.INTENTS:
+        s = selection[intent]
+        say(f"{intent}: phrasings {s['chosen']}, p{s['percentile']} theta {s['theta']!r}, dev F1 {s['f1']:.4f}"
+            + ("  (AT THE GRID EDGE)" if s["at_grid_edge"] else ""))
+
+
+def cmd_score(live, splits, from_run):
+    results_path = dp.out_path(live.out, "results.json")
+    dp.refuse_existing(results_path, "the test split is scored once")
+    selection = dp.read_json(dp.out_path(live.out, "selection.json"), "select")["selection"]
+    if set(selection) != set(dp.INTENTS):
+        fail(f"selection.json holds {sorted(selection)}, not the four intents")
+    ingest = load_from_run(from_run, splits)
+    expected = dp.written_turns(ingest, "test")
+    written = dp.written_dialogues(ingest, "test")
+    defines = {intent: pool_define(selection[intent]["phrasings"], selection[intent]["theta"])
+               for intent in dp.INTENTS}
+    calls = []
+
+    def body():
+        dp.wait_until_ready(live, "test", expected)
+        matches = {arm: {} for arm in ARMS}
+        for x, y in dp.PAIRS:
+            what = f"pool_semantic {x} -> {y}"
+            matches["pool_semantic"][(x, y)] = dp.matched_dialogues(
+                dp.timed_call(live, pool_arm_request("test", x, y, defines), what, calls), written, what)
+        for x, y in dp.PAIRS:
+            what = f"ordered_keyword {x} -> {y}"
+            matches["ordered_keyword"][(x, y)] = dp.matched_dialogues(
+                dp.timed_call(live, dp.arm_request("ordered_keyword", "test", x, y, None), what, calls), written, what)
+        single = {}
+        for intent in dp.INTENTS:
+            maps = []
+            for phrasing in selection[intent]["phrasings"]:
+                what = f"single-intent {intent} '{phrasing}'"
+                maps.append(dp.strict_turn_scores(
+                    dp.timed_call(live, dp.all_rows_request("test", phrasing), what, calls), expected, what))
+            single[intent] = combine(maps)
+        return matches, single
+
+    (matches, single), check = dp.checked(live, ingest["composite"], "score", body)
+    results = {**score_results(splits, matches, single, selection), "selection": selection, "calls": calls,
+               "build": check}
+    pl.write_json(results_path, results)
+    b = results["bootstrap"]["pool_minus_keyword"]
+    say(f"macro-F1 {results['macro_f1']}")
+    say(f"pool - keyword {b['delta']:+.4f} CI {b['ci95']} (GO when the lower bound > {NON_INFERIORITY_BOUND})")
+    say(f"GATE: {results['gate']}")
+
+
+# ── Wiring ───────────────────────────────────────────────────────────────────────────────────
+
+COMMANDS = {"select": cmd_select, "score": cmd_score}
+
+
+def build_arg_parser():
+    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
+    sub = ap.add_subparsers(dest="cmd", required=True)
+    for name in COMMANDS:
+        p = sub.add_parser(name)
+        p.add_argument("--out", required=True, help="this run's directory")
+        p.add_argument("--from-run", required=True, help="the 2026-09-29 run directory (ingest.json, data/)")
+        p.add_argument("--build-url", default=os.environ.get("IVERSON_HTTP_URL", "http://localhost:8081") + "/build")
+        p.add_argument("--username", default=pl.BYPASS_USERNAME, help="the acting user to mint a token for")
+    return ap
+
+
+def main(argv=None):
+    args = build_arg_parser().parse_args(argv)
+    os.makedirs(args.out, exist_ok=True)
+    dp.ensure_data(os.path.join(args.from_run, "data"))       # md5-checks the pinned files; downloads nothing present
+    splits = dp.load_splits(os.path.join(args.from_run, "data"))
+    session = pl.TokenSession(lambda: pl.mint_acting_user_token(args.username), pl.mint_service_token_from_env)
+    channel, _ = pl.connect(session)
+    try:
+        live = dp.Live(args.out, dp.EntityCoordinator(dp.DialogueTurn, channel), pl.fetch_build, args.build_url,
+                       acting_token=session.acting_user_token())
+        COMMANDS[args.cmd](live, splits, args.from_run)
+    finally:
+        channel.close()
+
+
+if __name__ == "__main__":
+    main()
diff --git a/Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py b/Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py
new file mode 100644
index 00000000..ee53f23a
--- /dev/null
+++ b/Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py
@@ -0,0 +1,220 @@
+"""pytest suite for dialogue_pool.py (spec docs/specs/2026-10-02-matchpattern-dialogue-pool-design.md,
+"Testing"). Run with the python-libs PYTHONPATH (dialogue_patterns imports numpy):
+
+    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \\
+        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py -q
+
+No network: the selection, define builder, gate and prediction ordering are pure, and the live commands are
+exercised only up to their refusal guards, which run before any call."""
+import os
+import sys
+
+import pytest
+
+sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
+import dialogue_pool as dpool  # noqa: E402
+
+dp = dpool.dp
+HOTEL, RESTAURANT, TRAIN, ATTRACTION = dp.INTENTS
+
+
+# ── greedy selection ─────────────────────────────────────────────────────────────────────────
+
+def scores_for(per_dialogue):
+    """One phrasing's per-turn scores: {(DialogueId, 0): s} from {DialogueId: s}."""
+    return {(d, 0): s for d, s in per_dialogue.items()}
+
+
+IDS = ["d1", "d2", "d3", "d4"]
+GOLD = [True, True, False, False]          # d1, d2 have the intent
+
+
+def test_selection_adds_the_best_phrasing_first_then_stops_without_strict_improvement():
+    perfect = scores_for({"d1": 0.9, "d2": 0.9, "d3": 0.1, "d4": 0.1})   # separates gold exactly: F1 1.0
+    noisy = scores_for({"d1": 0.9, "d2": 0.2, "d3": 0.8, "d4": 0.1})
+    result = dpool.select_phrasings([noisy, perfect, noisy], GOLD, IDS)
+    assert result["chosen"] == [1]                       # nothing can beat F1 1.0, so it stops after one
+    assert result["f1"] == 1.0
+    assert result["steps"][-1]["added"] is None
+
+
+def test_equal_f1_goes_to_the_lowest_pool_index():
+    perfect = scores_for({"d1": 0.9, "d2": 0.9, "d3": 0.1, "d4": 0.1})
+    result = dpool.select_phrasings([scores_for({"d1": 0.1, "d2": 0.1, "d3": 0.9, "d4": 0.9}), perfect, perfect],
+                                    GOLD, IDS)
+    assert result["chosen"][0] == 1
+
+
+def test_a_phrasing_is_added_only_when_it_strictly_raises_f1():
+    only_d1 = scores_for({"d1": 0.9, "d2": 0.1, "d3": 0.1, "d4": 0.1})    # alone: recall 1/2
+    only_d2 = scores_for({"d1": 0.1, "d2": 0.9, "d3": 0.1, "d4": 0.1})    # combined with only_d1: F1 1.0
+    result = dpool.select_phrasings([only_d1, only_d2], GOLD, IDS)
+    assert result["chosen"] == [0, 1]
+    assert result["f1"] == 1.0
+
+
+def test_selection_is_capped_at_three_phrasings():
+    ids = [f"d{i}" for i in range(8)]
+    gold = [True] * 4 + [False] * 4
+    # phrasing k recognises only gold dialogue k, so every added phrasing strictly raises recall
+    pool = [{(f"d{j}", 0): (0.9 if j == k else 0.1) for j in range(8)} for k in range(4)]
+    assert dpool.select_phrasings(pool, gold, ids)["chosen"] == [0, 1, 2]
+
+
+# ── the OR define and the arm request ────────────────────────────────────────────────────────
+
+def test_pool_define_ors_one_similarity_term_per_phrasing():
+    assert dpool.pool_define(["a b", "c"], 0.5) == "SIMILARITY(Utterance, 'a b') > 0.5 OR SIMILARITY(Utterance, 'c') > 0.5"
+
+
+def test_pool_define_escapes_a_quote_inside_a_phrasing():
+    assert "'the customer asks about a hotel''s stars'" in dpool.pool_define(["the customer asks about a hotel's stars"], 0.5)
+
+
+def test_pool_arm_request_is_ordered_one_row_over_the_split():
+    request = dpool.pool_arm_request("test", HOTEL, TRAIN, {HOTEL: "X > 1", TRAIN: "Y > 2"})
+    assert request.pattern == dp.ORDERED_PATTERN
+    assert [(d.name, d.expr) for d in request.define] == [("A", "X > 1"), ("B", "Y > 2"), ("Z", "TRUE")]
+    assert request.rows_per_match == dp.pb.ONE_ROW and request.limit == dp.LIMIT
+
+
+# ── gate, fidelity pin, prediction order ─────────────────────────────────────────────────────
+
+@pytest.mark.parametrize("lower, verdict", [(-0.0199, "GO"), (-0.02, "NO-GO"), (-0.0201, "NO-GO")])
+def test_non_inferiority_gate(lower, verdict):
+    assert dpool.gate([lower, 0.1]) == verdict
+
+
+def test_a_mismatching_selection_fails_the_fidelity_pin():
+    picks = dict(dpool.FIDELITY_PIN, find_train=[3, 5])
+    with pytest.raises(SystemExit, match="fidelity pin FAILED"):
+        dpool.check_fidelity(picks)
+    dpool.check_fidelity(dict(dpool.FIDELITY_PIN))           # the pinned picks pass
+
+
+def test_predictions_follow_the_dialogue_id_order():
+    matches = {arm: {pair: {"b"} for pair in dp.PAIRS} for arm in dpool.ARMS}
+    predictions = dpool.predictions_in_order(matches, ["a", "b", "c"])
+    assert all(v == [False, True, False] for arm in dpool.ARMS for v in predictions[arm].values())
+
+
+# ── refusal guards (they run before any call) ────────────────────────────────────────────────
+
+def live_at(tmp_path):
+    return dp.Live(str(tmp_path), None, lambda url: "c1", "http://b/build")
+
+
+def test_select_refuses_an_existing_selection(tmp_path):
+    (tmp_path / "selection.json").write_text("{}")
+    with pytest.raises(SystemExit, match="frozen once written"):
+        dpool.cmd_select(live_at(tmp_path), {}, str(tmp_path))
+
+
+def test_score_refuses_an_existing_results_file(tmp_path):
+    (tmp_path / "results.json").write_text("{}")
+    with pytest.raises(SystemExit, match="scored once"):
+        dpool.cmd_score(live_at(tmp_path), {}, str(tmp_path))
+
+
+def test_score_refuses_to_run_without_a_selection(pool_run):
+    from_run, out = pool_run                                 # a ready from-run: only selection.json is missing
+    with pytest.raises(SystemExit, match="selection.json is missing -- run select first"):
+        dpool.cmd_score(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)
+    assert not (out / "results.json").exists()
+
+
+# ── the live flow over fakes (synthetic splits and engine from test_dialogue_patterns) ───────
+
+import json  # noqa: E402
+
+import test_dialogue_patterns as tdp  # noqa: E402
+
+DESCRIPTION = {HOTEL: tdp.D_HOTEL, RESTAURANT: tdp.D_RESTAURANT, TRAIN: tdp.D_TRAIN, ATTRACTION: tdp.D_ATTRACTION}
+TEST_POOL = {intent: (DESCRIPTION[intent], f"an unhelpful {intent} phrasing") for intent in dp.INTENTS}
+
+
+class PoolEngine(tdp.FakeEngine):
+    """tdp.FakeEngine, plus the pool arm's OR of SIMILARITY terms."""
+
+    @staticmethod
+    def _holds(expr, t):
+        return any(tdp.FakeEngine._holds(part, t) for part in expr.split(" OR "))
+
+
+def pool_turns():
+    """Phrasing 0 of each intent scores like the 2026-09-29 description in tdp.SIMS; phrasing 1 is 0.2
+    everywhere, so selection keeps phrasing 0 alone (F1 1.0, nothing can strictly beat it)."""
+    turns = []
+    for record in dp.build_turns(tdp.SPLITS):
+        key = (record["dialogue_id"], record["turn_index"])
+        sims = {dp.READINESS_TEXT: 0.3}
+        for intent in dp.INTENTS:
+            sims[TEST_POOL[intent][0]] = tdp.SIMS.get(key, {}).get(DESCRIPTION[intent], 0.2)
+            sims[TEST_POOL[intent][1]] = 0.2
+        turns.append({"DialogueId": key[0], "TurnIndex": key[1], "Split": record["split"], "sims": sims,
+                      **{dp.FLAG_COLUMNS[d]: record[dp.FLAG_FIELDS[d]] for d in dp.KEYWORDS}})
+    return turns
+
+
+@pytest.fixture
+def pool_run(tmp_path, monkeypatch):
+    """A 2026-09-29-style run dir with a passed readiness, and an empty pool OUT dir."""
+    from_run = tmp_path / "run0"
+    from_run.mkdir()
+    tdp.ingested(from_run)
+    monkeypatch.setattr(dpool, "POOL", TEST_POOL)
+    monkeypatch.setattr(dpool, "FIDELITY_PIN", {intent: [0] for intent in dp.INTENTS})
+    out = tmp_path / "pool"
+    out.mkdir()
+    return str(from_run), out
+
+
+def test_select_runs_one_call_per_phrasing_and_writes_the_selection(pool_run):
+    from_run, out = pool_run
+    engine = PoolEngine(pool_turns())
+    dpool.cmd_select(tdp.live_for(out, engine), tdp.SPLITS, from_run)
+    saved = json.loads((out / "selection.json").read_text())
+    assert [c["what"] for c in saved["calls"]] == [f"calibration {i} [{j}]" for i in dp.INTENTS for j in (0, 1)]
+    assert all(saved["selection"][i]["chosen"] == [0] for i in dp.INTENTS)
+    assert saved["selection"][HOTEL]["phrasings"] == [tdp.D_HOTEL]
+    assert [g[0] for g in saved["selection"][HOTEL]["grid"]] == list(dp.THETA_PERCENTILES)  # grid reported
+    assert saved["build"]["before"] == saved["build"]["after"] == "c1"
+
+
+def test_a_failed_fidelity_pin_writes_no_selection(pool_run, monkeypatch):
+    from_run, out = pool_run
+    monkeypatch.setattr(dpool, "FIDELITY_PIN", {intent: [1] for intent in dp.INTENTS})
+    with pytest.raises(SystemExit, match="fidelity pin FAILED"):
+        dpool.cmd_select(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)
+    assert not (out / "selection.json").exists()
+
+
+def test_select_aborts_on_a_rebuilt_stack(pool_run):
+    from_run, out = pool_run
+    with pytest.raises(SystemExit):
+        dpool.cmd_select(tdp.live_for(out, PoolEngine(pool_turns()), composites=["c2"]), tdp.SPLITS, from_run)
+    assert not (out / "selection.json").exists()
+
+
+def test_score_runs_both_arms_and_records_per_dialogue_predictions(pool_run):
+    from_run, out = pool_run
+    dpool.cmd_select(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)
+    engine = PoolEngine(pool_turns())
+    dpool.cmd_score(tdp.live_for(out, engine), tdp.SPLITS, from_run)
+    results = json.loads((out / "results.json").read_text())
+    scored = engine.log[1:]                                  # [0] is the readiness check
+    assert len(scored) == len(results["calls"]) == 12 + 12 + 4
+    assert all(r.where[0].value.string_val == "test" for r in engine.log)
+    assert results["dialogue_ids"] == ["T1", "T2", "T3", "T4"]
+    x, y = dp.PAIRS[0]
+    theta = json.loads((out / "selection.json").read_text())["selection"]
+    assert [d.expr for d in scored[0].define[:2]] == [dpool.pool_define([DESCRIPTION[x]], theta[x]["theta"]),
+                                                      dpool.pool_define([DESCRIPTION[y]], theta[y]["theta"])]
+    assert [d.expr for d in scored[12].define[:2]] == [dp.keyword_define(x), dp.keyword_define(y)]
+    pool_ht = results["predictions"]["pool_semantic"][f"{HOTEL} -> {TRAIN}"]
+    assert pool_ht == [True, False, False, False]           # T1 hotel then train; T2 reversed; T3 a same-turn tie
+    assert set(results["predictions"]) == {"pool_semantic", "ordered_keyword"}
+    assert results["gate"] in ("GO", "NO-GO") and results["non_inferiority_bound"] == -0.02
+    assert "ci95" in results["bootstrap"]["pool_minus_keyword"]
+    with pytest.raises(SystemExit, match="scored once"):
+        dpool.cmd_score(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)
```

- [ ] **Step 2: Check the md5s.** From the repository root:

```bash
md5sum Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py
```

Expected:
```
29cf489b55451d2e8bbf67ce303108c2  Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py
3278f560906568d902772a546f498182  Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py
```

- [ ] **Step 3: Run the tests.** From the repository root:

```bash
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs QDRANT__SERVICE__API_KEY=x python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py -q -p no:cacheprovider
PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs QDRANT__SERVICE__API_KEY=x python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts -q -p no:cacheprovider
```

Expected:
- The first command reports `19 passed`.
- The second reports `596 passed, 1 warning`, up from 577. The warning is pre-existing.

- [ ] **Step 4: Commit.** From the repository root:

```bash
git add Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py
git commit -m "add the matchpattern dialogue pool harness" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py
```

### Task 2: Live `select` on dev

**Files:**
- Create, outside the repository: the state file, `$OUT/selection.json` and `$OUT/select.log`.

**Interfaces:**
- Consumes: Task 1's `dialogue_pool.py`.
- Produces: `$OUT/selection.json`, which Task 3's `score` reads.

This task makes only read calls; it writes nothing to the stack. It must run in a normal (not worktree-isolated) session, from the root of the checkout that holds Task 1's commit.

- [ ] **Step 1: Write the state file.** Run this once, from the repository root. It stores commands only, never a secret value:

```bash
STATE=/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogue-pool-shell.sh
printf 'cd %q\n' "$(pwd)" > "$STATE"
cat >> "$STATE" <<'STATE_EOF'
OUT=/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogue-pool-2026-10-02
FROM=/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-2026-09-29
ENVF=/home/ben/repositories/Iverson/Iverson.Server/.env
DPL=Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
source /home/ben/iverson-benchmark-data/bench-env.sh
export IVERSON_CLIENT_SECRET="$(command grep '^IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET=' "$ENVF" | cut -d= -f2-)"
export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(command grep '^IVERSON_BYPASS_PASSWORD=' "$ENVF" | cut -d= -f2-)"
STATE_EOF
. "$STATE" && ls "$OUT" && test -f "$FROM/ingest.json" && echo "state ok"
```

Expected: `design-probes`, then `state ok`.

- [ ] **Step 2: Start the existing stack, with no rebuild, and check its build.**

```bash
docker start iverson-postgres iverson-redis iverson-zookeeper iverson-qdrant iverson-tei-embed iverson-ollama iverson-jaeger iverson-prometheus
docker start iverson-kafka iverson-starrocks
docker start iverson-authentik-server iverson-authentik-worker
docker start iverson-api iverson-worker
HC="iverson-postgres iverson-redis iverson-qdrant iverson-tei-embed iverson-ollama iverson-jaeger iverson-prometheus iverson-kafka iverson-starrocks iverson-authentik-server iverson-authentik-worker iverson-api"
for i in $(seq 1 60); do bad=$(docker inspect -f '{{.Name}} {{.State.Running}} {{.State.Health.Status}}' $HC | command grep -v ' true healthy$'); [ -z "$bad" ] && break; sleep 10; done; echo "not healthy: ${bad:-none}"
for i in $(seq 1 60); do c=$(curl -sf http://localhost:8081/build 2>/dev/null | python3 -c 'import sys,json;print(json.load(sys.stdin)["composite"])' 2>/dev/null) && [ -n "$c" ] && break; sleep 5; done; echo "composite=$c"
docker exec iverson-api env | command grep -E '^Patterns__Limits__'
```

Run this step with a 600000 ms Bash timeout; the default 120000 ms would cut the waits short.

Expected:
- `not healthy: none`: every healthchecked container is running and healthy. Any other output after 10 minutes means **stop and report**.
- `composite=ded69e9492bdc081`.
- The two `Patterns__Limits__` lines (`MaxOutputRows=18623`, `MaxExpressionLength=25000`).

Any other composite, or no answer after 5 minutes, means **stop and report**. Never rebuild to make it fit.

- [ ] **Step 3: Run `select`.**

```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogue-pool-shell.sh
python3 "$DPL" select --out "$OUT" --from-run "$FROM" > "$OUT/select.log" 2>&1; echo "EXIT=$?"; tail -6 "$OUT/select.log"
```

Expected:
- `EXIT=0`.
- Four `phrasings …` lines: hotel `[1, 2, 0]`, restaurant `[1]`, train `[3, 5, 0]`, attraction `[0, 4, 6]`, none at the grid edge.
- θs near the offline prediction (hotel ≈ 0.5580, restaurant ≈ 0.5233, train ≈ 0.6486, attraction ≈ 0.5465).
- `selection.json` written, with 32 logged calls and its `build` before/after both `ded69e9492bdc081`.

On `fidelity pin FAILED`, a `/build` mismatch, a readiness timeout or any non-zero exit, **stop and report** with the log. No `selection.json` exists then, and Task 3 must not run.

### Task 3: Live `score` on test, and the gate doc

**Files:**
- Create, outside the repository: `$OUT/results.json` and `$OUT/score.log`.
- Create: `docs/plans/2026-10-GATE-matchpattern-dialogue-pool.md`.

**Interfaces:**
- Consumes: Task 2's `$OUT/selection.json` and the running stack.

- [ ] **Step 1: Run `score`, exactly once.**

```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogue-pool-shell.sh
python3 "$DPL" score --out "$OUT" --from-run "$FROM" > "$OUT/score.log" 2>&1; echo "EXIT=$?"; tail -4 "$OUT/score.log"
```

Expected:
- `EXIT=0`.
- The log's last lines give both arms' macro-F1, `pool - keyword <delta> CI [lo, hi]`, and `GATE: GO` or `GATE: NO-GO`.
- `results.json` written, with 34 logged calls.

On a non-zero exit, **stop and report** with the log. Never re-run `score` after `results.json` exists.

- [ ] **Step 2: Stop the stack.** This returns the containers to the stopped state they were found in:

```bash
docker stop iverson-worker iverson-api iverson-authentik-worker iverson-authentik-server iverson-starrocks iverson-kafka iverson-prometheus iverson-jaeger iverson-ollama iverson-tei-embed iverson-qdrant iverson-zookeeper iverson-redis iverson-postgres
```

- [ ] **Step 3: Write the gate doc.** Source the state file first, then run `md5sum "$DPL" "$OUT/selection.json" "$OUT/results.json"`. Create `docs/plans/2026-10-GATE-matchpattern-dialogue-pool.md` with these sections, filled only from the files named. Quote numbers exactly as written there.
  1. **Verdict.**
     - `GO` or `NO-GO`, the Δ (pool − keyword) and its 95% CI, all from `results.json` `bootstrap.pool_minus_keyword` and `gate`.
     - The rule in one sentence: GO when the CI lower bound exceeds −0.02.
  2. **Pre-registration.**
     - The pool (from `selection.json` `pool`) and the selection rule (from the spec).
     - The selection per intent: chosen indices, phrasings, θ, percentile and dev F1, and its θ grid, from `selection.json` `selection`.
     - The fidelity pin and the note that it passed.
  3. **Results.**
     - The per-pair P, R and F1 table for both arms, with each pair's gold-positive and reversed-negative counts (`results.json` `pairs`).
     - The macro-F1 table (`macro_f1`).
     - The per-intent single-intent P, R and F1 on test (`single_intent`).
  4. **Reported, not gated.** `pool_minus_original_semantic`: the point difference against the published 0.5102, with no CI.
  5. **Run record.**
     - The 2026-09-29 run directory used as `--from-run`.
     - The composite and the before/after `/build` records of both passes.
     - The live limits from Task 2 Step 2.
     - Logged call counts and the total seconds from each file's `calls`.
     - Any incident during the run.
     - The three md5s.

- [ ] **Step 4: Commit the gate doc.** From the repository root:

```bash
git add -f docs/plans/2026-10-GATE-matchpattern-dialogue-pool.md
git commit -m "add the matchpattern dialogue pool gate verdict" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- docs/plans/2026-10-GATE-matchpattern-dialogue-pool.md
```

## Tasks NOT in this plan

- Hybrid keyword-and-similarity arms.
- θ chosen by pair-level F1.
- Any change to the MatchPattern engine, the ingested data, `dialogue_patterns.py` or the original gate doc.
- Other corpora or intents.

## Known issues inherited from spec

- **The pool was written after seeing dev results.** Phrasings were drafted with dev's attraction failures in view, and several name domain words ("museum", "guesthouse", "train"). That tunes on dev only; test is untouched until `score`. The result speaks for this pool, not for `SIMILARITY` defines in general.
- **The test keyword arm is stronger than on dev** (0.7432 against 0.7227), so the dev margins (+0.007 in-sample; −0.014 and +0.016 held-out) do not predict the test outcome.
- **The original semantic arm is compared only by point difference.** Its per-dialogue predictions were never recorded.
