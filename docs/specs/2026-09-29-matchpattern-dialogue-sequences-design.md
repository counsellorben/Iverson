# MatchPattern Use-Case Proof: Semantic Intent Sequences in Dialogues

**Status:** approved design, 2026-09-29. The work produces a harness script, its tests and a gate document. It changes no production code.

## Goal

Prove, with a measured experiment on real labelled data, that MatchPattern delivers value that nothing else in Iverson can: finding **ordered semantic sequences**, meaning rows whose steps are recognised by vector similarity and which must occur in order. This is use-case family 2 of the row-pattern spec (`docs/specs/2026-09-17-row-pattern-matching-design.md`, Motivation).

The concrete question is: *"Which dialogues show a customer asking about X and then, later, about Y?"* This is asked of real task-oriented dialogues and scored against their human intent labels.

## Why this use case (from the brainstorm probes)

- **Unique capability.** An ordered pattern whose steps are `SIMILARITY` predicates cannot be expressed with `Search`, `Pipeline` or `SearchSimilar`, because none of them has row order combined with vector predicates.
- **Real, permissively licensed labels.** MultiWOZ 2.2 is MIT-licensed and gives a per-turn `active_intent` for every service.
- **Order genuinely matters in the data.** In the test split, every one of the 12 ordered pairs below has 37–88 dialogues in the asked order and 37–88 in the reverse order (§Verified assumptions 2). So "contains both", with no order, pays a real precision cost.
- **The outcome is uncertain.** Per-turn similarity separates the intents only partly: per-turn AUC was 0.609–0.888 on a 600-turn probe (§Verified assumptions 3). Dialogue-level sequence detection could succeed or fail.
- **The alternatives were weaker.**
  - RetailRocket funnels (family 1) prove capability and cost, but not accuracy.
  - FreshStack chunk spans (family 3) have only document-level labels, and a chunk-run signal was just shown to be NO-GO (`docs/plans/2026-09-GATE-matchpattern-rrf.md`).

## Data and ingest

- **Dataset.** MultiWOZ 2.2 from `github.com/budzianowski/multiwoz`, path `data/MultiWOZ_2.2/{dev,test}/dialogues_00{1,2}.json`. The script downloads these four files and aborts if any md5 differs from the pinned value (§Verified assumptions 1). The **dev** split (1,000 dialogues, 7,374 user turns) is for calibration; the **test** split (1,000 dialogues, 7,372 user turns) is for the result. The train split is not used.
- **Only USER turns are written.** `TurnIndex` is the turn's position among its dialogue's user turns, 0-based.
- **Iverson type `DialogueTurn`**, declared in the harness script with the Python SDK, following `PatternDoc` (`Iverson.Clients/Python/conformance/models.py:142-161`):
  - `id`: `iverson_key()`, server-generated. The script sends none and records the key `persist()` returns.
  - `tenant_id`, `owner_id`: `str`, as on `PatternDoc`.
  - `dialogue_id`: `str`, the PARTITION BY column.
  - `turn_index`: `int`, the ORDER BY column.
  - `split`: `str`, holding `dev` or `test`, and used as the `where` filter.
  - `utterance`: `iverson_embedding()`, the `SIMILARITY` target. Its vector is `utterance_vector`.
  - `mentions_hotel`, `mentions_restaurant`, `mentions_train`, `mentions_attraction`: `int` 0/1 keyword flags, computed at ingest from a fixed keyword list per domain (§Pre-registered constants). They are ints, not bools, because a boolean column can reach the engine as a number, and a define must evaluate to a boolean (§Verified assumptions 7). A define uses them as `MentionsTrain = 1`.
- **Registration.** `SchemaRegistrar(...).register_all(authorization_by_type_name={"DialogueTurn": rules})`, where `rules` is `OwnerField = "OwnerId"` plus a row permission for role `iverson-loadtest-bypass` with read, write and delete. These are the rules the conformance harness registers (`Reregistrar.cs:106-114`). The rules are required: a type with no rules is denied, and a denied MatchPattern returns an empty stream rather than an error (§Verified assumptions 6).
- **Writing.** One `persist()` per turn: 14,746 calls, as the bypass acting user in `tenant_bypass`.
- **Authentication.** Reuse `pattern_leg.py`'s pieces by importing them, not copying them: `TokenSession`, `mint_acting_user_token`, `mint_service_token_from_env`, `build_channel` and `fetch_build`. The script builds its own `EntityCoordinator(DialogueTurn, channel)`.
- **Readiness check.** Before any calibration or scoring, one MatchPattern per split (pattern `A+`, `A AS TRUE`, ALL ROWS, `where Split = '<split>'`, `limit` 10,000) measures `s = SIMILARITY(Utterance, 'a customer message')`. That readiness text is fixed and is not one of the four intent descriptions. Its row count must equal the number of turns written for that split, and every `s` must be non-NULL. Otherwise the script waits and retries, up to a fixed deadline, and then aborts. An unembedded turn scores NULL and would silently fail every define (§Verified assumptions 8).

## Ground truth and the questions

- **Onset.** For each dialogue and intent X, the onset is the `TurnIndex` of the first user turn on which the frame for X's service has `active_intent` = X **and** a non-empty `slot_values` or `requested_slots`. Onsets are used rather than every labelled turn, because MultiWOZ carries an intent forward through follow-up turns. An empty-state frame is not an onset, because MultiWOZ stamps a dialogue's other services with the intent before the customer mentions them, often at turn 0: 420 of 1,734 test onsets would otherwise fall on such a frame (CDR round 1 §2.1). A dialogue that goes straight to booking a service, so that it never has a non-empty find frame, has no onset for that intent.
- **Gold label.** A dialogue is gold-positive for "X then Y" when both intents have an onset and onset(X) < onset(Y). A dialogue where both intents start on the same turn is gold-negative for both orders. That happens for 7 test intent pairs.
- **The 12 questions.** Every ordered pair among `find_hotel`, `find_restaurant`, `find_train` and `find_attraction`.
  - Taxi is excluded, because it almost never comes first.
  - The book intents are excluded, because find→book never reverses.

## The three arms

Every arm runs through MatchPattern (TYPE_ROWS), one call per pair per arm, with:
- `partition_by DialogueId`, `order_by TurnIndex`, `where Split = 'test'`;
- ONE ROW PER MATCH, and `limit` 10,000;
- the default `after_match`, PAST LAST ROW.

A dialogue is predicted positive when it yields at least one match.

1. **Ordered semantic.** `PATTERN (A Z* B)`, with `A AS SIMILARITY(Utterance, 'dX') > θX`, `B AS SIMILARITY(Utterance, 'dY') > θY` and `Z AS TRUE`.
2. **Order-free semantic,** the baseline of what you get without ordering: `PATTERN (A Z* B | B Z* A)`, with the same defines.
3. **Ordered keyword,** the non-semantic control: `PATTERN (A Z* B)` with `A AS MentionsX = 1`, `B AS MentionsY = 1` and `Z AS TRUE`.

## Pre-registered constants (fixed before any data is scored, never edited afterwards)

- **Intent descriptions:**
  - `find_hotel`: "the customer is looking for a hotel to stay at"
  - `find_restaurant`: "the customer is looking for a restaurant to eat at"
  - `find_train`: "the customer is looking for a train"
  - `find_attraction`: "the customer wants a tourist attraction to visit"
- **Keyword lists** (case-insensitive, whole-word match on the utterance):
  - hotel: `hotel`, `guesthouse`, `guest house`, `stay`, `accommodation`, `lodging`
  - restaurant: `restaurant`, `food`, `eat`, `dine`, `dinner`, `lunch`
  - train: `train`, `trains`, `depart`, `departing`, `arrive`, `arriving`
  - attraction: `attraction`, `museum`, `college`, `park`, `theatre`, `cinema`, `entertainment`, `church`, `pool`, `boat`
- **θ calibration,** on the dev split only. For each intent, the θ is the candidate that maximises dialogue-level single-intent F1, where a dialogue is predicted to contain the intent when any user turn has `s > θ`, against the gold label "the intent has an onset".
  - Candidates are the 50th to 99th percentiles, in steps of 1, of that intent's per-turn `s` over dev.
  - A tie goes to the lower θ.
  - The four θ values are written to `theta.json` before any test-split call whose defines or measures use an intent description or θ. The pair patterns are never tuned.
- **Bootstrap:** 10,000 resamples of test dialogues with a fixed seed (`20260929`) and a 95% percentile CI.

## Measurement and gate

- **Metrics.** For each of the 12 pairs and each arm: precision, recall and F1 of dialogue-level detection on test. The headline is **macro-F1** over the 12 pairs. Each pair's gold-positive and reversed-negative counts are reported too.
- **Gate:**
  - **GO:** ordered-semantic macro-F1 minus order-free-semantic macro-F1 has a paired bootstrap 95% CI entirely above 0. MatchPattern's ordering then demonstrably adds value.
  - **NO-GO:** otherwise.
- **Reported, not gated:**
  - ordered semantic against ordered keyword (Δ macro-F1 and its CI), which says whether similarity defines beat keyword defines;
  - every arm's absolute macro-F1;
  - each intent's single-intent F1 on test.
- **Integrity,** where any failure aborts the run:
  - every MatchPattern call ends cleanly, and no result count equals the `limit` sent;
  - every matched `DialogueId` is a test dialogue that was written;
  - the `/build` composite is equal before and after each phase;
  - the readiness check passed.

## Phases

- **Phase 0, probes on the live stack:**
  1. **Ingest.** Register the type, write both splits and pass the readiness check.
  2. **Probe S (sanity).** `where DialogueId = <the first test dialogue>`, pattern `A`, `A AS TurnIndex = 0`, ONE ROW PER MATCH must return exactly one row, for that dialogue.
  3. **Probe T (timing).** One ordered-semantic call over the whole **dev** split (7,374 rows, the same size as test), with a fixed placeholder θ of 0.5 for each intent, finishes under the server's 30 s timeout. The gate document records the placeholder. Similarity scoring, which dominates the cost, does not depend on θ (§Verified assumptions 18). If it does not, stop and return to the user; never raise a limit to make it fit.
- **Phase 1:** calibrate θ on dev and freeze `theta.json`.
- **Phase 2:** score the 12 pairs × 3 arms on test (36 calls), then compute the metrics, the bootstrap and the gate.

## Deliverables

- **`Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py`,** with subcommands for ingest, probes, calibrate and score.
- **`Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py`,** in the existing pytest style, with no network. It covers:
  - onset derivation, including same-turn onsets and an empty-state frame, which is not an onset;
  - gold labels for all 12 pairs;
  - the keyword flags;
  - request building for all 3 arms, with the exact pattern, define and filter strings;
  - the θ grid and its tie rule;
  - precision, recall, F1 and macro-F1;
  - the bootstrap with a fixed seed;
  - the abort rules: a count at `limit`, an unknown matched `DialogueId`, a readiness timeout and a `/build` mismatch.
- **`docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md`,** containing:
  - the dataset md5s and split sizes;
  - the descriptions and keyword lists;
  - the θ values;
  - per-pair and macro metrics for every arm;
  - the CIs and the verdict;
  - the Phase 0 timings.
- **Artefacts** go under `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-<date>/`, never `/tmp`.

## Out of scope

- Any production change.
- Descriptions written or tuned by an LLM.
- The train split.
- The taxi and book intents.
- Sequences of three or more intents.
- The RetailRocket and chunk-span use cases.
- Latency comparisons against other engines.

## Known issues accepted

- **The shared stack is still in the RRF benchmark's state.** It has `MaxExpressionLength=25000` and `MaxOutputRows=18623` set. This design needs neither: its expressions are short, and its `limit` of 10,000 is at or below the default. Phase 0 records the live limit values.
- **Writes go to `tenant_bypass` on the shared stack.** One new type and 14,746 rows are written there.
- **Per-pair sample sizes are modest,** with 37–88 positives per pair. Per-pair F1 is noisy, which is why the gate is on macro-F1 with a bootstrap CI.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | The MultiWOZ 2.2 dev and test files download without authentication and are MIT-licensed. | `curl` returned 200. The `LICENSE` reads "The MIT License (MIT) Copyright (c) 2019 Paweł Budzianowski". md5s: dev001 `ee1809dcf412ccba0a47d7c2db2d361e`, dev002 `295eaea9b341b3e21589e4b97c7ca335`, test001 `e37f05c2800286768d273aaf4a8e85a4`, test002 `2aa1b12f2cf210a7b466ef260f397e32`. |
| 2 | Dev has 1,000 dialogues and 7,374 user turns; test has 1,000 and 7,372. Under the non-empty find onset rule, every test pair has gold-positive counts from 37 to 88 and reversed counts from 37 to 88, and 7 test intent pairs are tied (16 in dev). Under the rejected naive rule (first frame carrying the intent), 420 of 1,734 test onsets fall on an empty-state frame, and there are 157 ties. | Onset computation over the downloaded files, re-run in the CDR round 1 update. The naive rule gives 34–85 positives and 157 ties; the find non-empty rule gives 37–88 and 7. |
| 3 | Query-mode `SIMILARITY` of an intent description against document-mode utterance embeddings partly separates intents. | Live TEI bge-base over 600 user turns: per-turn AUC was train 0.888, hotel 0.772, taxi 0.744 and attraction 0.609. |
| 4 | The Python SDK declares an entity with `iverson_key()`, `iverson_embedding()`, `int` and `str` fields. The type name is the class name, and field names become PascalCase. `SchemaRegistrar.register_all(authorization_by_type_name=…)` attaches the rules. Registration needs `schema_admin`, which the bench service scope carries. | `annotations.py:154`, `:367`; `core.py:38-47`, `:97-99`, `:159-188`, `:327-333`; `ObjectMappingGrpcService.cs:42`; `bench-env.sh` scope. |
| 5 | Keys are server-generated and a client-set key is rejected. There is no batch write, so the write is one `persist()` per object. | `EntityKeyAccessor.cs:41-45`; `object_persistence.proto:9-10`; `core.py:664-677`. |
| 6 | A type with no authorization rules is denied (`AuthorizationDecision(true, …)`). A denied MatchPattern returns an empty stream. The conformance harness registers `OwnerField = OwnerId` plus a bypass-role row permission. | `RowFieldAuthorizationEvaluator.cs:11-12`; `ObjectSearchGrpcService.MatchPattern.cs:58`; `Reregistrar.cs:92`, `:106-114`. |
| 7 | A bare column define must evaluate to a `bool`: a number throws "must evaluate to a boolean", and comparing bigint with boolean throws. Whether StarRocks `BOOLEAN` arrives as `bool` or a number is untested, so the flags are `int` with `= 1`. | `ExpressionEvaluator.cs:18-23`; `SqlValues.cs:17-19`, `:70-71`; `ExpressionParser.cs:80-89`. |
| 8 | On TYPE_ROWS, `SIMILARITY` is legal on an embedding property, scores against `<snake_case>_vector`, and gives NULL for a row with no vector. Vectors are embedded asynchronously on the write path. | `MatchPattern.cs:358-365`; `SimilarityResolver.cs:58-59`, `:77-85`; `IntelligenceStoreConsumer.cs:131-147`. |
| 9 | TYPE_ROWS takes `partition_by` and `order_by` over visible StarRocks columns, and needs at least one `order_by`. An EQUALS `where` on a string property is allowed. | `MatchRowsQueryBuilder.cs:23-31`, `:87-89`; `MatchPattern.cs:304-305`; `StarRocksQueryBuilder.cs:1009`; `MatchRowsQueryBuilderTests.cs:66`. |
| 10 | The pattern grammar supports `(A Z* B | B Z* A)` and `Z*`, and `Z AS TRUE` is valid. `after_match` defaults to PAST LAST ROW, and a partition can yield several matches. | `PatternParser.cs:8-12`; `ExpressionParser.cs:379-381`; `MatchPattern.cs:321`; `PartitionMatcher.cs:53-101`. |
| 11 | A call over about 7,400 rows with two similarity terms is within the default limits: `MaxRowsScanned` 100,000, `MaxPartitionRows` 10,000, `MaxSimilarityTerms` 10, `MaxOutputRows` 10,000 and timeout 30 s. Timing is Probe T. | `PatternQueryLimitOptions.cs:21-27`. |
| 12 | `pattern_leg.py` imports cleanly, and exposes `TokenSession`, `mint_acting_user_token`, `mint_service_token_from_env`, `build_channel` and `fetch_build`. Its `execute` aborts when a count equals `limit`, so an explicit `limit` is always set. | `pattern_leg.py:244`, `:330`, `:348-358`, `:515`, `:662`, `:676`; `test_pattern_leg.py:18` imports it. |
| 13 | numpy 2.5.2 and scipy 1.18.1 are available under the `python-libs` `PYTHONPATH`. A seeded numpy bootstrap already exists as a precedent. | `iverson-benchmark-corpora/python-libs`; `aspect_vectors.py:360-379`. |
| 14 | The work adds new files only; no existing file is modified. | The design's deliverables list only new files. |
| 15 | No dev or test user utterance is blank, so the write path embeds every turn and readiness cannot stall on a skipped one. | 0 of 14,746 user utterances are blank. `IntelligenceStoreConsumer.cs:138` skips blank text. |
| 16 | ONE ROW PER MATCH output carries `DialogueId` (the partition column), so each match maps back to its dialogue. | CDR round 1 engine probe (A5). |
| 17 | The dev θ argmax falls inside the p50–p99 grid for all four intents. | CDR round 1 R10, over the full dev split: p74–p91. |
| 18 | Similarity scoring is independent of θ. Each batch is fully scored before the matcher runs, so a placeholder θ gives representative timing. | `ObjectSearchGrpcService.MatchPattern.cs:126` (`ScoreBatchAsync`) runs before `compiled.Run` at `:132`. |
