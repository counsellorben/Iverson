# MatchPattern Dialogue Pool — Design

**Date:** 2026-10-02
**Status:** Approved design, assumptions verified
**Follows:** `docs/specs/2026-09-29-matchpattern-dialogue-sequences-design.md` and its gate, `docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md` (GO, merged `519ae09d`).

## Problem

The dialogue-sequence gate passed: ordering helped the semantic arm, with ordered minus order-free macro-F1 of +0.1392, CI [0.1286, 0.1494]. But its pre-registered keyword control scored 0.7432 against the ordered semantic arm's 0.5102, a difference of −0.2330, CI [−0.2606, −0.2051]. So with those fixed descriptions, four keyword flags beat `SIMILARITY` defines.

The cause is mostly one description:
- **Six of the 12 pairs involve `find_attraction`, and they carry the gap.**
  - On those pairs, the semantic arm averages F1 0.31 and keywords 0.71.
  - On the other six, the arms are close: 0.744 against 0.778.
  - So about 86% of the test gap is the attraction pairs.
- **Attraction's θ is very loose.** It sits at p74, so 26% of dev turns exceed it, against 9–10% for the other intents. Its dialogue-level precision is 0.430 on dev and 0.432 on test.
- **The failure mode is precision, not recall.** An `A Z* B` pattern matches almost any dialogue that also mentions the other intent. Recall stays around 0.8, while attraction-pair precision falls to 0.12–0.42.

## Goal (user decisions)

A new gated experiment that tests whether a better-described semantic arm closes the gap to the keyword arm. The gate is non-inferiority: **GO if the 95% paired-bootstrap CI lower bound of (pool-semantic − ordered-keyword) test macro-F1 is greater than −0.02.**

## Approach (chosen: A — a pre-registered description pool with greedy selection)

Each intent gets a frozen pool of eight phrasings. On dev, a greedy rule picks up to three phrasings, combined by max, and θ is calibrated exactly as in the original run. The test split is used only for scoring.

Rejected alternatives, all measured on dev with the offline replica (see Verified assumptions):
- **B: three hand-written phrasings per intent, with no selection.** Dev macro-F1 0.681 against keywords' 0.723, a gap of −0.042, so a predictable NO-GO.
- **C: hybrid keyword OR similarity.**
  - Dev macro-F1 is 0.495 with the original descriptions and 0.673 with better ones, both below keywords alone, because similarity adds false positives.
  - It is not a semantic arm, so it doesn't answer the gate's question.
- **θ chosen by pair-level macro-F1 instead of single-intent F1.** On a 2-fold held-out split of dev it gains +0.003 to +0.009. That is too little to justify the extra machinery.

## Design

### 1. Pre-registration

Everything below is frozen in code before any test-split call.

**Pool.** Eight phrasings per intent, in this order; index 0 is listed first. Every intent except attraction keeps its original 2026-09-29 description at index 0. Attraction's original description is at index 3.

- `find_hotel`:
  0. the customer is looking for a hotel to stay at
  1. the customer needs a place to stay, such as a hotel or guesthouse
  2. the customer asks about a hotel's stars, parking, wifi or price
  3. I need somewhere to stay tonight
  4. book a room at a guesthouse
  5. the customer wants accommodation in the north of town
  6. looking for a cheap 4 star hotel with free parking
  7. the customer asks to book a hotel for several nights
- `find_restaurant`:
  0. the customer is looking for a restaurant to eat at
  1. the customer wants somewhere to eat that serves a certain kind of food
  2. the customer asks about a restaurant's food type, price range or area
  3. I want to find a place to eat
  4. an expensive italian restaurant in the centre
  5. the customer wants to book a table for dinner
  6. looking for cheap chinese food
  7. the customer asks for a restaurant recommendation
- `find_train`:
  0. the customer is looking for a train
  1. the customer wants a train departing from or arriving at a station
  2. the customer asks about train times, departures or arrivals
  3. I need a train to cambridge on saturday
  4. a train leaving after 10:00 from london
  5. the customer wants to book train tickets
  6. what time does the train arrive
  7. the customer is travelling by train
- `find_attraction`:
  0. the customer is looking for an attraction such as a museum, college, park or theatre
  1. the customer asks about places to go and things to see in town
  2. the customer wants to visit a museum
  3. the customer wants a tourist attraction to visit
  4. is there a college or a church I can visit
  5. something fun to do in the centre of town
  6. the customer asks about entertainment, nightclubs or swimming pools
  7. the customer wants the entrance fee and address of a sight

**Selection (dev only).** For each intent:
1. Fetch per-turn `SIMILARITY(Utterance, phrasing)` over the dev split, one calibration-shape call per phrasing.
2. Start from an empty selection. Repeatedly try adding each unselected phrasing:
   - the candidate's per-turn score is the max over the selected phrasings plus that one;
   - score it with the existing `theta_grid` rule (p50..p99 of the per-turn scores; dialogue-level single-intent F1 against the onset gold; ties go to the lower θ).
3. Add the best candidate only if its F1 is strictly greater than the current selection's. Among equal F1s, take the lowest pool index.
4. Stop after three phrasings, or when no candidate strictly improves.
5. θ is the `theta_grid` result of the final selection.

**Arm.** Ordered `A Z* B`, `ONE ROW`, the same `_split_rows` shape as the original arms. Each intent's `DEFINE` is `SIMILARITY(Utterance, 'd1') > θ OR SIMILARITY(Utterance, 'd2') > θ …`, with literals escaped by `pattern_leg.similarity`. This is equivalent to max over the selection > θ.

**Comparison arm.** The original ordered keyword arm (`dialogue_patterns.arm_request("ordered_keyword", …)`), unchanged and re-run on test. The 2026-09-29 `results.json` holds no per-dialogue predictions, which the paired bootstrap needs.

**Gate.** **GO** if the CI lower bound of (pool-semantic − ordered-keyword) test macro-F1 is greater than −0.02; otherwise **NO-GO**. The bootstrap is `dialogue_patterns.bootstrap_macro_f1` and `percentile_ci`, unchanged:
- seed 20260929;
- 10,000 resamples;
- 2.5/97.5 percentiles;
- the 12 ordered pairs;
- the same onset rule and gold.

**Reported, not gated:**
- per-pair P, R and F1 for both arms;
- macro-F1 for both arms;
- per-intent single-intent P, R and F1 on test at the selected θ;
- the selection, θ, every grid, and every greedy step's candidate F1s;
- pool-semantic minus the original semantic arm's published 0.5102, as a point difference with no CI.

### 2. Execution

**One new script: `Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py`.**
- It imports `dialogue_patterns` for everything shared:
  - data loading and md5 pins;
  - onsets and gold;
  - `keyword_define` and `arm_request("ordered_keyword", …)`;
  - `Live`, `timed_call`, `wait_until_ready` and the token and channel wiring;
  - `theta_grid`, `prf`, `macro_f1`, `bootstrap_macro_f1` and `percentile_ci`.
- `dialogue_patterns.py` is not modified, so the published run stays reproducible.
- `--out` is a new directory. `--from-run` points at the 2026-09-29 run directory, whose `ingest.json` (the written turns) and `data/` (the md5-pinned MultiWOZ files) the script reads. It writes nothing there.

**Subcommands.** Each refuses to overwrite its output file.
1. **`select` (dev):**
   - runs the existing readiness check over dev;
   - makes 32 calibration-shape calls (`all_rows_request("dev", phrasing)`), each held to the existing strict coverage check;
   - runs the greedy selection and θ calibration;
   - checks the fidelity pin below;
   - writes `selection.json`, with every call's row count, timing and build.
2. **`score` (test):**
   - reads `selection.json`;
   - runs readiness over test;
   - makes 12 pool-semantic pair calls, 12 keyword pair calls, and one single-intent calibration-shape call per selected phrasing (10 with the expected picks);
   - writes `results.json`, which includes **per-dialogue predictions** for both gated arms, the CI and the verdict.

**Calls.** 32 + 12 + 12 + 10 = **66** MatchPattern calls, all reads. Nothing is ingested; the run uses the 14,746 `DialogueTurn` rows already stored. The full local stack must be up.

**Fidelity pin.** `select` must reproduce the offline replica's dev selection exactly:

| Intent | Pool indices |
|---|---|
| hotel | [1, 2, 0] |
| restaurant | [1] |
| train | [3, 5, 0] |
| attraction | [0, 4, 6] |

Any other pick means the live path differs from the replica. `select` then stops before writing `selection.json`, and no test call is made until the difference is understood.

**Artefacts.**
- Logs, `selection.json` and `results.json` go to `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogue-pool-2026-10-02/`.
- The gate doc goes to `docs/plans/2026-10-GATE-matchpattern-dialogue-pool.md`. It pins the md5s of `dialogue_pool.py`, `selection.json` and `results.json`.

## Testing

Unit tests in `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py`, run under the existing conventions: `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs` for numpy, and `QDRANT__SERVICE__API_KEY` set to any value for the scripts suite.
1. **Greedy selection:**
   - a phrasing is added only on a strict F1 improvement;
   - among equal F1s the lowest index wins;
   - the cap is three;
   - an empty improvement stops the loop.
2. **The `OR` `DEFINE` builder:** one term per selected phrasing, with θ via `pl.format_theta`. A phrasing containing `'` is escaped as `''`.
3. **Gate rule:** −0.0199 is GO; −0.02 and −0.0201 are NO-GO.
4. **The fidelity pin:** a mismatching selection aborts before `selection.json` is written.
5. **Refuse-to-overwrite:** an existing `selection.json` or `results.json` aborts.
6. **`score` integrity:** it refuses to run without `selection.json`, and the pair predictions it records follow the `ids` order of the test split.

## Known limitations (accepted)

- **The pool was written after seeing dev results.** Phrasings were drafted with dev's attraction failures in view, and several name domain words ("museum", "guesthouse", "train"). That tunes on dev only; test is untouched until `score`. The result speaks for this pool, not for `SIMILARITY` defines in general.
- **The test keyword arm is stronger than on dev** (0.7432 against 0.7227), so the dev margins (+0.007 in-sample; −0.014 and +0.016 held-out) do not predict the test outcome.
- **The original semantic arm is compared only by point difference.** Its per-dialogue predictions were never recorded.

## Out of scope

- Hybrid keyword-and-similarity arms.
- θ chosen by pair-level F1.
- Any change to the MatchPattern engine, the ingested data, `dialogue_patterns.py` or the original gate doc.
- Other corpora or intents.

## Verified assumptions

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
