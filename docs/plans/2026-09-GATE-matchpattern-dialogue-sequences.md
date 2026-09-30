# Gate: MatchPattern dialogue-sequence use-case proof

Live run of the design at `docs/specs/2026-09-29-matchpattern-dialogue-sequences-design.md`, over
MultiWOZ 2.2, `dev`/`test` split, through `Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py`.

## 1. Verdict

**GO.**

Under the spec's gate rule — the paired bootstrap 95% CI of (ordered-semantic macro-F1 − order-free-
semantic macro-F1) is entirely above 0 — the run is a GO:

- **Δ (ordered − order-free) = +0.1392**
- **95% CI = [0.1286, 0.1494]**

Both bounds are strictly positive; MatchPattern's ordering demonstrably adds value over an
order-free semantic baseline on this use case.

The pre-registered ordered keyword control scored macro-F1 0.7432 against the ordered semantic
arm's 0.5102 (ordered semantic − ordered keyword −0.2330, 95% CI [−0.2606, −0.2051], entirely
below 0; see §6). This GO answers only the pre-registered question, whether ordering helps the
semantic arm. It does not show that SIMILARITY-based defines are the best approach for this task:
with these fixed, untuned descriptions, keyword flags did better.

## 2. Data

- **Source:** MultiWOZ 2.2, `https://raw.githubusercontent.com/budzianowski/multiwoz/master/data/MultiWOZ_2.2`.
- **Files and md5s (as ingested):**
  - `dev/dialogues_001.json` = `ee1809dcf412ccba0a47d7c2db2d361e`
  - `dev/dialogues_002.json` = `295eaea9b341b3e21589e4b97c7ca335`
  - `test/dialogues_001.json` = `e37f05c2800286768d273aaf4a8e85a4`
  - `test/dialogues_002.json` = `2aa1b12f2cf210a7b466ef260f397e32`
- **Split sizes:** dev 1,000 dialogues / 7,374 user turns; test 1,000 dialogues / 7,372 user turns.
  Total 14,746 turns, matching `ingest.json`'s 2,000 dialogue keys.
- **Onset rule:** for each dialogue and intent X, the onset is the `TurnIndex` of the first user
  turn on which the frame for X's service has `active_intent = X` **and** a non-empty
  `slot_values` or `requested_slots`. An empty-state frame is never an onset. A dialogue is
  gold-positive for "X then Y" when both intents have an onset and onset(X) < onset(Y); a
  dialogue where both intents start on the same turn is gold-negative for both orders.
- **Ties:** `results.json` records `"ties": 7` for the test split (7 test intent pairs where both
  intents' onsets tie), matching the spec's stated count.

## 3. Pre-registered constants

Quoted from `Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py` (md5
`97634749b56fbd1abf70ce9cbcf6fd89`), which the run executed unmodified.

**Intent descriptions (`DESCRIPTIONS`):**
- `find_hotel`: "the customer is looking for a hotel to stay at"
- `find_restaurant`: "the customer is looking for a restaurant to eat at"
- `find_train`: "the customer is looking for a train"
- `find_attraction`: "the customer wants a tourist attraction to visit"

**Keyword lists (`KEYWORDS`, case-insensitive, whole-word match):**
- hotel: `hotel`, `guesthouse`, `guest house`, `stay`, `accommodation`, `lodging`
- restaurant: `restaurant`, `food`, `eat`, `dine`, `dinner`, `lunch`
- train: `train`, `trains`, `depart`, `departing`, `arrive`, `arriving`
- attraction: `attraction`, `museum`, `college`, `park`, `theatre`, `cinema`, `entertainment`,
  `church`, `pool`, `boat`

**θ grid (`THETA_PERCENTILES`):** `range(50, 100)` — p50..p99 in steps of 1, of that intent's
per-turn `s` over dev; ties go to the lower θ.

**Bootstrap seed (`BOOTSTRAP_SEED`):** `20260929`, with `BOOTSTRAP_RESAMPLES = 10_000` and
`CI_PERCENTILES = (2.5, 97.5)`.

## 4. θ values

From `theta.json` (Phase 1, dev split, 4 calibration calls):

| Intent | θ | Percentile | Dev F1 | At grid edge |
|---|---|---|---|---|
| find_hotel | 0.5332754075527191 | p90 | 0.8453 | false |
| find_restaurant | 0.5426613688468933 | p91 | 0.9089 | false |
| find_train | 0.5136097908020022 | p91 | 0.9694 | false |
| find_attraction | 0.4719292473793030 | p74 | 0.5837 | false |

All four argmaxes fall at p74–p91, inside the p50–p99 grid, matching the spec's expectation. No
`at_grid_edge` flag is set for any intent.

## 5. Results

Per-pair precision, recall and F1 for all three arms, and each pair's gold-positive and
reversed-negative counts, on test (from `results.json`):

| Pair (X → Y) | gold+ | rev− | Arm | P | R | F1 |
|---|---|---|---|---|---|---|
| find_hotel → find_restaurant | 37 | 52 | ordered_semantic | 0.3816 | 0.7838 | 0.5133 |
| | | | order_free_semantic | 0.2206 | 0.8108 | 0.3468 |
| | | | ordered_keyword | 0.5246 | 0.8649 | 0.6531 |
| find_hotel → find_train | 64 | 71 | ordered_semantic | 0.7067 | 0.8281 | 0.7626 |
| | | | order_free_semantic | 0.3506 | 0.8438 | 0.4954 |
| | | | ordered_keyword | 0.6522 | 0.9375 | 0.7692 |
| find_hotel → find_attraction | 49 | 43 | ordered_semantic | 0.1250 | 0.7959 | 0.2161 |
| | | | order_free_semantic | 0.1092 | 0.7959 | 0.1921 |
| | | | ordered_keyword | 0.6935 | 0.8776 | 0.7748 |
| find_restaurant → find_hotel | 52 | 37 | ordered_semantic | 0.4600 | 0.8846 | 0.6053 |
| | | | order_free_semantic | 0.3382 | 0.8846 | 0.4894 |
| | | | ordered_keyword | 0.6267 | 0.9038 | 0.7402 |
| find_restaurant → find_train | 88 | 65 | ordered_semantic | 0.7917 | 0.8636 | 0.8261 |
| | | | order_free_semantic | 0.5067 | 0.8636 | 0.6387 |
| | | | ordered_keyword | 0.6807 | 0.9205 | 0.7826 |
| find_restaurant → find_attraction | 67 | 64 | ordered_semantic | 0.1818 | 0.7761 | 0.2946 |
| | | | order_free_semantic | 0.1667 | 0.8657 | 0.2795 |
| | | | ordered_keyword | 0.6897 | 0.5970 | 0.6400 |
| find_train → find_hotel | 71 | 64 | ordered_semantic | 0.6778 | 0.8592 | 0.7578 |
| | | | order_free_semantic | 0.3961 | 0.8592 | 0.5422 |
| | | | ordered_keyword | 0.7889 | 1.0000 | 0.8820 |
| find_train → find_restaurant | 65 | 88 | ordered_semantic | 0.8333 | 0.7692 | 0.8000 |
| | | | order_free_semantic | 0.3400 | 0.7846 | 0.4744 |
| | | | ordered_keyword | 0.8028 | 0.8769 | 0.8382 |
| find_train → find_attraction | 78 | 87 | ordered_semantic | 0.2081 | 0.7949 | 0.3298 |
| | | | order_free_semantic | 0.1586 | 0.7949 | 0.2644 |
| | | | ordered_keyword | 0.8571 | 0.8462 | 0.8516 |
| find_attraction → find_hotel | 43 | 49 | ordered_semantic | 0.1314 | 0.7209 | 0.2222 |
| | | | order_free_semantic | 0.0896 | 0.7442 | 0.1600 |
| | | | ordered_keyword | 0.5510 | 0.6279 | 0.5870 |
| find_attraction → find_restaurant | 64 | 67 | ordered_semantic | 0.2476 | 0.7969 | 0.3778 |
| | | | order_free_semantic | 0.1609 | 0.8750 | 0.2718 |
| | | | ordered_keyword | 0.6923 | 0.7031 | 0.6977 |
| find_attraction → find_train | 87 | 78 | ordered_semantic | 0.2930 | 0.7241 | 0.4172 |
| | | | order_free_semantic | 0.1816 | 0.8161 | 0.2971 |
| | | | ordered_keyword | 0.7284 | 0.6782 | 0.7024 |

**Macro-F1 (12-pair average, test):**

| Arm | Macro-F1 |
|---|---|
| ordered_semantic | 0.5102 |
| order_free_semantic | 0.3710 |
| ordered_keyword | 0.7432 |

**Per-intent single-intent F1 on test** (dialogue-level detection at the calibrated θ):

| Intent | Precision | Recall | F1 | Gold+ |
|---|---|---|---|---|
| find_hotel | 0.8128 | 0.8863 | 0.8480 | 387 |
| find_restaurant | 0.9165 | 0.9269 | 0.9217 | 438 |
| find_train | 0.9559 | 0.9401 | 0.9479 | 484 |
| find_attraction | 0.4321 | 0.9098 | 0.5860 | 399 |

## 6. Reported comparisons

Ordered semantic − ordered keyword (reported, not gated):

- **Δ = −0.2330**
- **95% CI = [−0.2606, −0.2051]**

The ordered-keyword control beats ordered-semantic on macro-F1 here; similarity defines do not
beat keyword defines on this corpus and this description set. This comparison does not affect the
Section 1 verdict, which is gated only on ordered − order-free.

## 7. Phase 0

From `iverson-benchmark-corpora/matchpattern-dialogues-2026-09-29/phase0.md`:

- **Composite:** `ded69e9492bdc081`, unchanged across every recorded pass — ingest write,
  readiness, Probe S, Probe T, and (per `theta.json`/`results.json`'s own `build` records) also
  unchanged across calibrate and score.
- **Live limits (verified 2026-09-29, re-verified 2026-09-30 after a host reboot, unchanged):**
  `Patterns__Limits__MaxOutputRows=18623`, `Patterns__Limits__MaxExpressionLength=25000`. Both
  exceed this design's needs (`limit` 10,000, short expressions), so neither is a concern for any
  call in this record.
- **Ingest counts:** `DialogueTurn`, tenant `tenant_bypass`, owner id
  `a0d5ce58b207e7ad27bfc1130445c2d4e119b3169eb9c75a5cf048def6872847`. 14,746/14,746 turns written
  (dev 7,374 + test 7,372), matching `ingest.json`'s 2,000 dialogue keys. First test dialogue (for
  Probe S): `MUL0484.json`.
- **Incident:** at 2026-09-30 03:06 UTC the dev stack's Postgres crashed (a known, undiagnosed
  SIGPIPE backend crash). The worker's three fast retries fell inside Postgres's recovery window,
  so 3 `DialogueTurn` entity events were routed to the DLQ (keys `01a0f020-ad33-7c0d-8aa3-
  8783a41bc228`, `01a0f020-ad76-7361-9558-81e548785245`, `01a0f020-adab-7ea6-aae5-12b1ec063c16`;
  `PMUL2351.json` turns 2–4). The StarRocks rows for turns 3 and 4 were missing (turn 2's row
  already existed). The harness's exact-set readiness check refused to pass (timed out after
  3,611 s at 7,370/7,372 — `ready3.log`). After a host reboot the stack was restored on the same
  volumes and build, and exactly those 3 DLQ rows were replayed through the API's `/admin/dlq`
  replay endpoint (user-approved). StarRocks then held all 14,746 rows with no duplicates, and
  readiness passed on the first attempt for both splits (`ready4.log`): dev 7,374 rows / 1
  attempt / 13 s; test 7,372 rows / 1 attempt / 5 s.
- **Earlier readiness attempts:** `ingest.log` records ingest's own readiness check passing on
  dev, then failing on test attempt 20 with MatchPattern `DEADLINE_EXCEEDED` (ingest exited 1),
  after all 14,746 writes had already finished. `ready.log`, the first `ready` rerun, failed on
  its dev attempt 1, again with `DEADLINE_EXCEEDED`. Both `DEADLINE_EXCEEDED` failures came during
  the host-load peak (controller's `uptime` reading, 5-minute load average 24.85 on a 4-CPU host,
  ~23:11 local on 2026-09-29, while TEI embedded the backlog). `ready2.log`, a second `ready`
  rerun, was stopped deliberately by the controller right after it minted its token, before it
  made any MatchPattern call; it produced no result — it did not hit the 30 s limit, and it did
  not fail at minting. (`phase0.md`'s line describing `ready2.log` as "aborted during token
  minting" is inaccurate; this is the correct account.) No call in calibrate or score is affected
  by any of this; both ran after the load peak and after `ready4.log`'s clean pass.
- **Probe S:** 1 row, for `MUL0484.json`, in 1.53 s. Passed.
- **Probe T** (find_hotel → find_restaurant, dev, θ 0.5 placeholder, pattern `A Z* B`): 250 dev
  dialogues matched, 3.23 s — well under the server's 30 s call limit. Passed.

## 8. Integrity

- **Every call exited cleanly.** `calibrate` (4 calls, `EXIT=0`) and `score` (40 calls, `EXIT=0`)
  both completed to `EXIT=0`; the script aborts before writing its output file on any gRPC error,
  a matched `DialogueId` outside the split's written set, a NULL similarity, or a `/build`
  composite mismatch (script docstring, "Failure is loud"). Neither run aborted.
- **No count equal to `limit`:** `limit` is 10,000 for every call; scanning `results.json`'s 40
  `calls` entries and `theta.json`'s 4 `calls` entries, none has `rows == 10000`.
- **Matched ids are a subset of the written ids:** enforced in-process by `matched_dialogues()`,
  which the script's own integrity contract aborts on for any matched `DialogueId` that is not a
  written dialogue of the split asked for. Both runs exited 0, so no such id occurred.
- **`/build` composite equal throughout:** `theta.json`'s `build` = `{"pass": "calibrate",
  "before": "ded69e9492bdc081", "after": "ded69e9492bdc081"}`; `results.json`'s `build` =
  `{"pass": "score", "before": "ded69e9492bdc081", "after": "ded69e9492bdc081"}`. Both equal the
  Phase 0 composite recorded at ingest.

## 9. md5s of every artefact

```
20c34f74f00159f8c7c925f23767e0ec  OUT/ingest.json
f739c28d8562da670db2684b3c7b85df  OUT/probe-s.json
0f287351ccc58faad84ac75ef9bed3eb  OUT/probe-t.json
4f6b887fe44ef07cb124b294fbda3e37  OUT/results.json
bd9e4859b54cf97da9aaa238e52c3c8d  OUT/theta.json
ee1809dcf412ccba0a47d7c2db2d361e  OUT/data/dev/dialogues_001.json
295eaea9b341b3e21589e4b97c7ca335  OUT/data/dev/dialogues_002.json
e37f05c2800286768d273aaf4a8e85a4  OUT/data/test/dialogues_001.json
2aa1b12f2cf210a7b466ef260f397e32  OUT/data/test/dialogues_002.json
```

`OUT` = `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-2026-09-29`.

Script and test file (unchanged by this task, quoted for completeness):

```
97634749b56fbd1abf70ce9cbcf6fd89  Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py
644fe8fbfcf2024f976325a23efc96c6  Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py
```

## 10. Deviations

From the plan's "Plan decisions beyond the spec" section (all mechanical; made while prototyping):

1. **A `ready` subcommand re-runs only the readiness check.** Without it, a readiness timeout
   after a completed write could be retried only by ingesting again, which would write every turn
   twice. `ingest` refuses to run twice, and `ingest-progress.jsonl` records each key as it is
   written.
2. **Readiness checks the exact set of (DialogueId, TurnIndex), not just a count.** An unknown or
   duplicate row aborts at once; missing rows and NULL `s` wait and retry. Readiness runs again at
   the start of `probe-t` (dev), `calibrate` (dev) and `score` (test). Its deadline is 3,600 s,
   polled every 60 s.
3. **`TenantId` and `OwnerId` come from the acting token's `tenant_id` and `sub` claims,** as the
   conformance drivers' `--tenant` and `--owner-id` do. `ingest` aborts unless the token names
   `tenant_bypass` and carries the `iverson-loadtest-bypass` group. The key is `uuid.UUID`, as on
   `PatternDoc`.
4. **Choices the spec left open:**
   - Probe T uses the first pair (find_hotel → find_restaurant).
   - Probe S uses the first dialogue in `test/dialogues_001.json`'s file order.
   - `probe-s.json` and `probe-t.json` are written in every case. `probe-t.json` is written even
     on STOP, because the timing is the finding.
5. **Overwrite guards:** `calibrate` never overwrites `theta.json`, and `score` requires
   `theta.json` and never overwrites `results.json`.
6. **F1 is computed as 2tp/(2tp+fp+fn),** which is equal to 2PR/(P+R) but gives identical floats
   for identical counts, as the θ tie rule needs. `theta.json` also records the full grid and an
   `at_grid_edge` flag.
7. **The bootstrap** draws all 1,000 test dialogues per resample, with one draw shared by all
   three arms. `reversed_negative` for (X, Y) is the gold-positive count of (Y, X).
8. **The stack's live limit values** (Phase 0) are read from `docker exec iverson-api env`,
   because no API exposes them.

**SDD deviation (this task):** the test file `test_dialogue_patterns.py` differs from the plan's
pinned prototype md5 (now `644fe8fbfcf2024f976325a23efc96c6`) because a review found the
`reversed_negative` test could not fail; one test was strengthened. The script's md5
(`97634749b56fbd1abf70ce9cbcf6fd89`) is unchanged.

**Phase 0 environment note:** Phase 0 ran with the RRF bench overrides still live —
`Patterns__Limits__MaxOutputRows=18623` and `Patterns__Limits__MaxExpressionLength=25000` — which
this design needs neither of.

## 11. Known limits

- **Per-pair samples are modest,** at 37–88 positives per pair.
- **Onset lag:** 128 test onsets come a turn or more after the customer's first mention, which
  shifts the dev gate difference by at most 0.0022 (spec VA19).
- **The descriptions are fixed and untuned,** so a NO-GO speaks only for them and this θ grid.
  (This run is a GO, but the same caveat bounds how far the result generalizes: it speaks for
  these four fixed descriptions and this θ grid, not for MatchPattern ordering in general.)
