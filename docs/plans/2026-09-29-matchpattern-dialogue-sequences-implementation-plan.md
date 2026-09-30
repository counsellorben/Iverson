# MatchPattern Dialogue-Sequence Use-Case Proof Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-29-matchpattern-dialogue-sequences-design.md` (commit SHA: `8bda7a3e`)

**Goal:** Prove, with a pre-registered experiment on MultiWOZ 2.2's human intent labels, that MatchPattern's ordered semantic sequences, whose steps are `SIMILARITY` predicates matched in order, beat an order-free baseline on macro-F1. The result is recorded in a gate document.

**Architecture:**
- **Script:** one new harness script, `dialogue_patterns.py`, with pure logic and live subcommands, plus its pytest file. It reuses `pattern_leg.py`'s authentication by importing it.
- **Type and data:** it registers a `DialogueTurn` type with the conformance-style authorization rules, writes MultiWOZ's dev and test user turns into `tenant_bypass`, and runs MatchPattern over them.
- **Live tasks:** two tasks run on the shared local stack. Phase 0 covers ingest, readiness and Probes S and T; Phases 1 and 2 cover calibration and scoring. The second task writes the gate document.
- **No production change.**

**Tech stack:** Python 3.14; the in-repo `iverson_client` SDK on `sys.path` through `pattern_leg`; grpcio; numpy 2.5.2 (under `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs`); pytest; the local compose stack (Qdrant, TEI bge-base, StarRocks, Authentik).

---

## Global Constraints

- **No production code changes.** The work adds one script, its tests and a gate document.
- **Pre-registered constants are never edited after data is scored:** the four intent descriptions, the keyword lists, the θ grid (p50–p99 of per-turn `s`, with ties going to the lower θ), bootstrap seed `20260929` with 10,000 resamples, and the gate rule.
- **θ is frozen in `theta.json` before any test-split call whose defines or measures use an intent description or θ.**
- **Runs never degrade silently.** Any of these aborts the run:
  - a MatchPattern error;
  - a result count equal to the `limit` sent;
  - a matched `DialogueId` outside the written set;
  - a NULL `s`, or a readiness timeout;
  - a `/build` composite mismatch.
- **Artefacts go under `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-<DATE>/`, never `/tmp`.**
- **The shell's `grep` skips gitignored paths, including `docs/`.** Use `command grep`.
- **Commits** use a lowercase imperative subject, a blank line, then `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Commit explicit paths only. `docs/` needs `git add -f`.
- **Tasks 2 and 3 write to the shared local stack:** one new type and 14,746 rows in `tenant_bypass`. The controller asks the user before dispatching Task 2.
- **Execution base (user decision, CIR round 1 §3.1):** local `main` is pushed to `origin` before execution. The execution worktree, created with the native worktree tool from `origin/main`, then contains `pattern_leg.py` and its two fixes (`a819b2a4`, `58d70f43`).
- **Every live step runs in a fresh shell.** It starts by sourcing the state file Task 2 writes, which re-reads every secret from `.env` each time and never stores one.

## Plan decisions beyond the spec (all mechanical; made while prototyping)

1. **A `ready` subcommand re-runs only the readiness check.** Without it, a readiness timeout after a completed write could be retried only by ingesting again, which would write every turn twice. `ingest` refuses to run twice, and `ingest-progress.jsonl` records each key as it is written.
2. **Readiness checks the exact set of (DialogueId, TurnIndex), not just a count.** An unknown or duplicate row aborts at once; missing rows and NULL `s` wait and retry. Readiness runs again at the start of `probe-t` (dev), `calibrate` (dev) and `score` (test). Its deadline is 3,600 s, polled every 60 s.
3. **`TenantId` and `OwnerId` come from the acting token's `tenant_id` and `sub` claims,** as the conformance drivers' `--tenant` and `--owner-id` do. `ingest` aborts unless the token names `tenant_bypass` and carries the `iverson-loadtest-bypass` group. The key is `uuid.UUID`, as on `PatternDoc`.
4. **Choices the spec left open:**
   - Probe T uses the first pair (find_hotel → find_restaurant).
   - Probe S uses the first dialogue in `test/dialogues_001.json`'s file order.
   - `probe-s.json` and `probe-t.json` are written in every case. `probe-t.json` is written even on STOP, because the timing is the finding.
5. **Overwrite guards:** `calibrate` never overwrites `theta.json`, and `score` requires `theta.json` and never overwrites `results.json`.
6. **F1 is computed as 2tp/(2tp+fp+fn),** which is equal to 2PR/(P+R) but gives identical floats for identical counts, as the θ tie rule needs. `theta.json` also records the full grid and an `at_grid_edge` flag.
7. **The bootstrap** draws all 1,000 test dialogues per resample, with one draw shared by all three arms. `reversed_negative` for (X, Y) is the gold-positive count of (Y, X).
8. **The stack's live limit values** (Phase 0) are read from `docker exec iverson-api env`, because no API exposes them.

## File Structure

- **Create:** `Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py`. The `DialogueTurn` entity, the pure logic, and the subcommands `ingest`, `ready`, `probe-s`, `probe-t`, `calibrate` and `score`.
- **Create:** `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py`. Offline tests with injected fakes.
- **Create:** `docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md`. The gate document (Task 3).
- **Outside the repository** (Tasks 2 and 3):
  - `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh`, the state file;
  - `/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-<DATE>/`, the `OUT` directory.

No existing file is modified.

## Inherited from spec

These assumptions were verified by `thorough-brainstorming` and by two rounds of critical design review. They are not re-verified here and are trusted as ground truth. The table is copied verbatim from the spec's "Verified assumptions":

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
| 16 | Environment | Before the push, `origin/main` does not contain `pattern_leg.py`. A worktree branched from it fails Task 1's tests with `ModuleNotFoundError: No module named 'pattern_leg'`. | CIR round 1 §3.1: `git rev-list --count origin/main..main` = 39, and the failure was reproduced on an `origin/main` worktree. |
| 16 | ONE ROW PER MATCH output carries `DialogueId` (the partition column), so each match maps back to its dialogue. | CDR round 1 engine probe (A5). |
| 17 | The dev θ argmax falls inside the p50–p99 grid for all four intents. | CDR round 1 R10, over the full dev split: p74–p91. |
| 18 | Similarity scoring is independent of θ. Each batch is fully scored before the matcher runs, so a placeholder θ gives representative timing. | `ObjectSearchGrpcService.MatchPattern.cs:126` (`ScoreBatchAsync`) runs before `compiled.Run` at `:132`. |
| 19 | A non-empty find frame marks where the customer starts asking about X, to within the gate's tolerance. No onset comes from carried-over slot values alone. 128 test onsets lag an explicit mention by one turn or more, but moving them back shifts the dev gate Δ by at most 0.0022 (+0.1345 against +0.1323). | CDR round 2, R1 and R1a (`r2_carry.py`, `r2_lag.py`, `r2_lag2.py` over the full splits). |

## Verified plan-level assumptions

Tasks 1's code was prototyped and run at `8bda7a3e`, in the throwaway worktree `.worktrees/dlg-proto`. The code below is byte-identical to the prototype; md5s are given in Task 1.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Paths | `dialogue_patterns.py`, `test_dialogue_patterns.py` and the gate document do not exist yet. | `ls` returned "No such file" for each at `8bda7a3e`. |
| 2 | Signature | `SchemaRegistrar(mapping_stub, *entity_classes)` takes a stub, built as `ObjectMappingServiceStub(channel)`. `register_all(trace_id="", authorization_by_type_name=None)` attaches the rules by type name and raises `RuntimeError` on `success=false`. | `iverson_client/core.py:159`, `:168`, `:176-179`, `:327`, `:333`; `conformance/driver.py:505`. |
| 3 | Signature | The proto fields are `RowPermission{role, can_read_all, can_write_all, can_delete_all}` and `AuthorizationRules{owner_field, row_permissions, field_permissions}`. The rules copy `Reregistrar.cs:106-114`. | `Iverson.Clients/Common/Proto/object_mapping.proto:77-82`, `:90-94`, `:100`. |
| 4 | Signature | `persist(entity)` returns `response.key` (a str) and raises on `success=false`. A key left as None is omitted from the payload. | `core.py:664-677`, `:425`; `annotations.py:318`. |
| 5 | Signature | The builder methods are `match_pattern()`, `where` (EQUALS, as a FILTER clause), `partition_by`, `order_by`, `pattern`, `define`, `measure`, `rows_per_match`, `limit` and `build`. `after_match` is unset, so the server applies PAST LAST ROW. The enums are `pb.ONE_ROW` and `pb.ALL_ROWS_SHOW_EMPTY`. | `iverson_client/match_pattern.py:44`, `:53-125`; `ObjectSearchGrpcService.MatchPattern.cs:321`; `object_search.proto:364`. |
| 6 | Behaviour | A ONE ROW result carries the partition column `DialogueId` plus the measures. ALL ROWS carries every visible column plus the measures. Numbers arrive as floats. | `PartitionMatcher.cs:244`, `:255`; `MatchRowsQueryBuilder.cs:36`, `:68-80`; `core.py:471-472`, `:822-828`. The engine probe printed ALL ROWS keys ending in `…TurnIndex, Utterance, s`. |
| 7 | Code validity | All 48 request strings the builders emit compile and run in the engine, including `MentionsX = 1` and `TurnIndex = 0` over int and long columns. Every arm's matched set equals an independent reference, and a same-turn-only dialogue matches neither order. | Prototype engine probe: 512 fixture rows, 0 compile errors, 0 mismatches over 36 arms. |
| 8 | Data | The script's own pure functions reproduce the spec's numbers: dev 1,000 dialogues and 7,374 user turns, test 1,000 and 7,372, 7 test ties and 16 dev ties, test positives 37–88. Under the naive rule, 420 of 1,734 test onsets are empty-state, with 157 ties. There are 0 blank utterances. | Run over the pinned files, whose md5s all match. |
| 9 | Command | `test_dialogue_patterns.py` needs `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs`, because the script imports numpy. The whole scripts suite needs `QDRANT__SERVICE__API_KEY` set to any value. | Prototype counts: 109 passed; whole suite 468 before, 577 after. |
| 10 | Command | The download base is `https://raw.githubusercontent.com/budzianowski/multiwoz/master/data/MultiWOZ_2.2`, and the pinned md5s match the files at that base. | The brainstorm downloaded the four files from exactly these URLs; `dialogue_patterns.py:128-133`. |
| 11 | Environment | The state file must override `IVERSON_CLIENT_SECRET` from `.env`'s `IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET`, because `bench-env.sh`'s copy is stale. The acting-user password is `.env`'s `IVERSON_BYPASS_PASSWORD`. | The RRF live run: a `client_credentials` grant gave 400 with the `bench-env.sh` secret and 200 with the `.env` one. `pattern_leg.py` reads `IVERSON_ACTING_USER_BYPASS_PASSWORD`. |
| 12 | Behaviour | `pattern_leg`'s service token is minted with the Authentik Host header, and a failed mint surfaces as an RPC error instead of hanging. `dialogue_patterns.py` gets both behaviours by importing `pattern_leg`. | Commits `a819b2a4` and `58d70f43` on main; `test_pattern_leg.py` passes 58. |
| 13 | Ordering | Task 1 has no dependency. Task 2 needs Task 1, and Task 3 needs Tasks 1 and 2. The script imports only `pattern_leg` and the SDK. | `command grep "^import\|^from"` over the prototype. |
| 14 | Environment | No server rebuild is needed: the plan changes no server code, and any current `iverson-api` image serves MatchPattern. Task 2 records the live `/build` composite. | The running stack's composite was `ded69e9492bdc081`, from after the MatchPattern merge. |
| 15 | Convention | The commit style is a lowercase imperative subject plus a `Co-Authored-By` trailer. | `git log --oneline`. |

## Tasks

### Task 1: `dialogue_patterns.py` and its tests

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py` (prototype md5 `97634749b56fbd1abf70ce9cbcf6fd89`)
- Create: `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py` (prototype md5 `fbae2b8fd726f19e1a0522cf510b66bf`)

**Interfaces:**
- Produces, for Tasks 2 and 3:
  - the subcommands `ingest`, `ready`, `probe-s`, `probe-t`, `calibrate` and `score`, each taking `--out OUT`, with optional `--build-url` and `--username`;
  - the files `OUT/ingest.json`, `ingest-progress.jsonl`, `probe-s.json`, `probe-t.json`, `theta.json` and `results.json`, and the data files `OUT/data/{dev,test}/dialogues_00{1,2}.json`.

- [ ] **Step 1: Write the tests**

```python
"""pytest suite for dialogue_patterns.py (spec docs/specs/2026-09-29-matchpattern-dialogue-sequences-design.md,
"Deliverables"). Run with the python-libs PYTHONPATH (dialogue_patterns imports numpy):

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \\
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py -q

No network: the coordinator (MatchPattern and persist), the mapping stub, the /build fetch, the clock,
the sleep and the downloader are fakes, and every dialogue is synthetic. The request builders run
through the real SDK, so the field assertions pin the exact protos a live run sends."""
import base64
import hashlib
import io
import json
import os
import re
import sys

import grpc
import numpy as np
import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dialogue_patterns as dp  # noqa: E402
from iverson_client import MatchPatternResult  # noqa: E402  (on sys.path via pattern_leg)
from iverson_client.core import _entity_to_struct  # noqa: E402
from iverson_client.generated import object_mapping_pb2 as mapping_pb  # noqa: E402

pb = dp.pb
HOTEL, RESTAURANT, TRAIN, ATTRACTION = dp.INTENTS
D_HOTEL = "the customer is looking for a hotel to stay at"
D_RESTAURANT = "the customer is looking for a restaurant to eat at"
D_TRAIN = "the customer is looking for a train"
D_ATTRACTION = "the customer wants a tourist attraction to visit"


# ── synthetic MultiWOZ 2.2 dialogues ────────────────────────────────────────────────────────

def frame(service, intent="NONE", slots=None, requested=None):
    return {"service": service, "state": {"active_intent": intent, "slot_values": slots or {},
                                          "requested_slots": requested or []}}


def dialogue(dialogue_id, user_turns):
    """`user_turns` is [(utterance, [frame, ...]), ...]; a SYSTEM turn follows each user turn, as in
    MultiWOZ, so user-turn extraction is exercised."""
    turns = []
    for n, (utterance, frames) in enumerate(user_turns):
        turns.append({"speaker": "USER", "turn_id": str(2 * n), "utterance": utterance, "frames": frames})
        turns.append({"speaker": "SYSTEM", "turn_id": str(2 * n + 1), "utterance": "sure.", "frames": []})
    return {"dialogue_id": dialogue_id, "services": [], "turns": turns}


def turn(utterance, *frames):
    return (utterance, list(frames))


# ── fakes ───────────────────────────────────────────────────────────────────────────────────

def row(**data):
    return MatchPatternResult(data=data, match_number=1, classifier="")


def jwt(claims):
    body = base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip("=")
    return f"eyJhbGciOiJub25lIn0.{body}.sig"


BYPASS_TOKEN = jwt({"sub": "sub-123", "tenant_id": "tenant_bypass", "groups": ["iverson-loadtest-bypass", "operators"]})

SIM_RE = re.compile(r"^SIMILARITY\(Utterance, '((?:[^']|'')*)'\)(?: > (.+))?$")


class FakeEngine:
    """A MatchPattern + persist stand-in over a table of turns. Each turn is a dict with DialogueId,
    TurnIndex, Split, the four Mentions* ints and `sims` {text: s or None}. It honours exactly the
    request shapes dialogue_patterns builds, and its answers are what the engine probe showed the
    real engine gives: one ALL ROWS row per turn carrying s, or one ONE ROW row per matched
    dialogue carrying DialogueId."""

    def __init__(self, turns=(), respond=None):
        self.turns = list(turns)
        self.respond = respond
        self.log = []
        self.persisted = []

    def persist(self, entity):
        self.persisted.append(entity)
        return f"key-{len(self.persisted)}"

    def match_pattern(self, request):
        self.log.append(request)
        if self.respond is not None:
            return self.respond(request)
        where = {c.property: c.value.string_val for c in request.where}
        rows = [t for t in self.turns if all(str(t[k]) == v for k, v in where.items())]
        parts = {}
        for t in sorted(rows, key=lambda t: t["TurnIndex"]):
            parts.setdefault(t["DialogueId"], []).append(t)
        if request.rows_per_match == pb.ALL_ROWS_SHOW_EMPTY:
            text = SIM_RE.match(request.measures[0].expr).group(1).replace("''", "'")
            return [row(DialogueId=t["DialogueId"], TurnIndex=float(t["TurnIndex"]), s=t["sims"].get(text))
                    for p in parts.values() for t in p]
        if request.pattern == "A":
            return [row(DialogueId=d) for d, p in parts.items() for t in p if t["TurnIndex"] == 0]
        defines = {d.name: d.expr for d in request.define}
        out = []
        for d, p in parts.items():
            a, b = [self._holds(defines["A"], t) for t in p], [self._holds(defines["B"], t) for t in p]
            if sequence(a, b) or (request.pattern == dp.ORDER_FREE_PATTERN and sequence(b, a)):
                out.append(row(DialogueId=d))
        return out

    @staticmethod
    def _holds(expr, t):
        m = SIM_RE.match(expr)
        if m:
            s = t["sims"].get(m.group(1).replace("''", "'"))
            return s is not None and s > float(m.group(2))
        column, value = expr.split(" = ")
        return t[column] == int(value)


def sequence(a, b):
    """Some row with a before some later row with b (A Z* B)."""
    seen = False
    for x, y in zip(a, b):
        if seen and y:
            return True
        seen = seen or x
    return False


class FakeMappingStub:
    def __init__(self, success=True):
        self.requests = []
        self.success = success

    def RegisterSchema(self, request):
        self.requests.append(request)
        return mapping_pb.SchemaResponse(success=self.success, error="" if self.success else "nope")


class FakeRpcError(grpc.RpcError):
    def code(self):
        return grpc.StatusCode.DEADLINE_EXCEEDED

    def details(self):
        return "MatchPattern exceeded its time limit."


class Clock:
    def __init__(self, step=0.0):
        self.now, self.step = 0.0, step

    def __call__(self):
        self.now += self.step
        return self.now

    def sleep(self, seconds):
        self.now += seconds


def live_for(tmp_path, engine, composites=None, clock=None, mapping_stub=None, **kw):
    """A Live over fakes; /build answers `composites` in turn (default: "c1" forever)."""
    answers = iter(composites) if composites else None
    clock = clock or Clock()
    return dp.Live(str(tmp_path), engine, lambda url: next(answers) if answers else "c1", "http://b/build",
                   mapping_stub=mapping_stub or FakeMappingStub(), acting_token=BYPASS_TOKEN,
                   clock=clock, sleep=clock.sleep, **kw)


# ── user turns and onsets ───────────────────────────────────────────────────────────────────

def test_user_turns_are_the_user_speakers_in_order():
    d = dialogue("D", [turn("a"), turn("b"), turn("c")])
    assert [t["utterance"] for t in dp.user_turns(d)] == ["a", "b", "c"]
    assert all(t["speaker"] == "USER" for t in dp.user_turns(d))


def test_an_empty_state_frame_is_not_an_onset():
    """MultiWOZ stamps a later service's intent on turn 0 with an empty state; the onset is the
    first turn whose frame is non-empty. The naive rule would put find_train at turn 0."""
    d = dialogue("D", [
        turn("i need a hotel", frame("hotel", HOTEL, {"hotel-area": ["north"]}), frame("train", TRAIN)),
        turn("cheap please", frame("hotel", HOTEL, {"hotel-area": ["north"], "hotel-pricerange": ["cheap"]}),
             frame("train", TRAIN)),
        turn("and a train to ely", frame("hotel", HOTEL, {"hotel-area": ["north"]}),
             frame("train", TRAIN, {"train-destination": ["ely"]})),
    ])
    assert dp.onsets(d) == {HOTEL: 0, TRAIN: 2}


def test_a_requested_slot_alone_makes_an_onset():
    d = dialogue("D", [turn("hi", frame("attraction", ATTRACTION)),
                       turn("what museums are there?", frame("attraction", ATTRACTION, requested=["attraction-name"]))])
    assert dp.onsets(d) == {ATTRACTION: 1}


def test_only_the_intents_own_service_frame_counts_and_later_turns_do_not_move_an_onset():
    d = dialogue("D", [
        turn("x", frame("restaurant", HOTEL, {"restaurant-food": ["thai"]})),     # wrong service: ignored
        turn("y", frame("restaurant", RESTAURANT, {"restaurant-food": ["thai"]})),
        turn("z", frame("restaurant", RESTAURANT, {"restaurant-food": ["indian"]})),
    ])
    assert dp.onsets(d) == {RESTAURANT: 1}


def test_a_dialogue_that_goes_straight_to_booking_has_no_onset():
    d = dialogue("D", [turn("book it", frame("hotel", "book_hotel", {"hotel-name": ["acorn"]})),
                       turn("thanks", frame("hotel", "NONE", {"hotel-name": ["acorn"]}))])
    assert dp.onsets(d) == {}


def test_same_turn_onsets_are_a_tie():
    d = dialogue("D", [turn("a hotel and a train", frame("hotel", HOTEL, {"hotel-area": ["east"]}),
                            frame("train", TRAIN, {"train-day": ["monday"]}))])
    o = dp.onsets(d)
    assert o == {HOTEL: 0, TRAIN: 0}
    assert not dp.gold_positive(o, HOTEL, TRAIN) and not dp.gold_positive(o, TRAIN, HOTEL)
    assert dp.tie_count({"D": o}) == 1


# ── gold labels ─────────────────────────────────────────────────────────────────────────────

def test_the_12_pairs_are_every_ordered_pair_of_the_four_find_intents():
    assert dp.INTENTS == ("find_hotel", "find_restaurant", "find_train", "find_attraction")
    assert len(dp.PAIRS) == 12 and len(set(dp.PAIRS)) == 12
    assert all(x != y and x in dp.INTENTS and y in dp.INTENTS for x, y in dp.PAIRS)


def test_gold_labels_for_all_12_pairs():
    onsets = {"D1": {HOTEL: 1, TRAIN: 3, RESTAURANT: 3},            # hotel first; train/restaurant tied
              "D2": {ATTRACTION: 0, RESTAURANT: 2}}
    gold = dp.gold_labels(onsets, ["D1", "D2"])
    assert set(gold) == set(dp.PAIRS)
    expected_true = {(HOTEL, TRAIN): [True, False], (HOTEL, RESTAURANT): [True, False],
                     (ATTRACTION, RESTAURANT): [False, True]}
    for pair in dp.PAIRS:
        assert gold[pair] == expected_true.get(pair, [False, False]), pair


def test_single_intent_gold_is_having_an_onset():
    gold = dp.single_intent_gold({"D1": {HOTEL: 2}, "D2": {}}, ["D1", "D2"])
    assert gold == {HOTEL: [True, False], RESTAURANT: [False, False], TRAIN: [False, False],
                    ATTRACTION: [False, False]}


# ── keyword flags ───────────────────────────────────────────────────────────────────────────

def test_keyword_lists_are_the_pre_registered_ones():
    assert dp.KEYWORDS == {
        "hotel": ("hotel", "guesthouse", "guest house", "stay", "accommodation", "lodging"),
        "restaurant": ("restaurant", "food", "eat", "dine", "dinner", "lunch"),
        "train": ("train", "trains", "depart", "departing", "arrive", "arriving"),
        "attraction": ("attraction", "museum", "college", "park", "theatre", "cinema", "entertainment",
                       "church", "pool", "boat"),
    }


@pytest.mark.parametrize("utterance, domain", [
    ("I need a Guest House in the north", "hotel"),
    ("a GUESTHOUSE please", "hotel"),
    ("somewhere to stay.", "hotel"),
    ("the hotel's parking", "hotel"),
    ("where can I EAT?", "restaurant"),
    ("trains to ely", "train"),
    ("a boat trip", "attraction"),
])
def test_a_whole_word_keyword_sets_its_flag_case_insensitively(utterance, domain):
    flags = dp.keyword_flags(utterance)
    assert flags[f"mentions_{domain}"] == 1
    assert all(type(v) is int for v in flags.values())


@pytest.mark.parametrize("utterance", [
    "a guest in the house",            # both words, not the phrase
    "she stays at hotels",             # stay/hotel inside longer words
    "a great seat",                    # eat
    "is there parking?",               # park
    "the pools and the colleges",      # pool, college
    "the trainee's departure",         # train, depart
    "a restaurants guide",             # restaurant
])
def test_a_keyword_inside_a_longer_word_does_not_set_a_flag(utterance):
    assert dp.keyword_flags(utterance) == {"mentions_hotel": 0, "mentions_restaurant": 0,
                                           "mentions_train": 0, "mentions_attraction": 0}


def test_flags_are_per_domain_and_independent():
    assert dp.keyword_flags("a train and then dinner") == {
        "mentions_hotel": 0, "mentions_restaurant": 1, "mentions_train": 1, "mentions_attraction": 0}


# ── turns, the entity, registration ─────────────────────────────────────────────────────────

def test_build_turns_writes_user_turns_dev_first_with_positions_and_flags():
    splits = {"dev": [dialogue("DEV1", [turn("a hotel"), turn("thanks")])],
              "test": [dialogue("T1", [turn("a train")])]}
    assert dp.build_turns(splits) == [
        {"dialogue_id": "DEV1", "turn_index": 0, "split": "dev", "utterance": "a hotel",
         "mentions_hotel": 1, "mentions_restaurant": 0, "mentions_train": 0, "mentions_attraction": 0},
        {"dialogue_id": "DEV1", "turn_index": 1, "split": "dev", "utterance": "thanks",
         "mentions_hotel": 0, "mentions_restaurant": 0, "mentions_train": 0, "mentions_attraction": 0},
        {"dialogue_id": "T1", "turn_index": 0, "split": "test", "utterance": "a train",
         "mentions_hotel": 0, "mentions_restaurant": 0, "mentions_train": 1, "mentions_attraction": 0},
    ]


def test_build_turns_refuses_a_blank_utterance_and_a_repeated_dialogue_id():
    with pytest.raises(SystemExit, match="blank"):
        dp.build_turns({"dev": [dialogue("D", [turn("  ")])], "test": []})
    with pytest.raises(SystemExit, match="twice"):
        dp.build_turns({"dev": [dialogue("D", [turn("a")])], "test": [dialogue("D", [turn("b")])]})


def test_the_entity_payload_carries_no_key_and_int_flags():
    record = dp.build_turns({"dev": [dialogue("D", [turn("a hotel")])], "test": []})[0]
    payload = _entity_to_struct(dp.make_entity(record, "tenant_bypass", "sub-1"))
    assert "Id" not in payload.fields
    assert dict(payload) == {"TenantId": "tenant_bypass", "OwnerId": "sub-1", "DialogueId": "D", "TurnIndex": 0.0,
                             "Split": "dev", "Utterance": "a hotel", "MentionsHotel": 1.0,
                             "MentionsRestaurant": 0.0, "MentionsTrain": 0.0, "MentionsAttraction": 0.0}


def test_registration_sends_the_declared_type_with_the_conformance_rules():
    stub = FakeMappingStub()
    dp.register(stub)
    (request,) = stub.requests
    root = request.root_type
    assert root.type_name == "DialogueTurn"
    props = {p.name: p for p in root.properties}
    assert list(props) == ["Id", "TenantId", "OwnerId", "DialogueId", "TurnIndex", "Split", "Utterance",
                           "MentionsHotel", "MentionsRestaurant", "MentionsTrain", "MentionsAttraction"]
    assert props["Id"].is_key and props["Id"].clr_type == mapping_pb.CLR_GUID
    assert props["Utterance"].is_embedding and props["Utterance"].clr_type == mapping_pb.CLR_STRING
    for name in ("TurnIndex", "MentionsHotel", "MentionsRestaurant", "MentionsTrain", "MentionsAttraction"):
        assert props[name].clr_type == mapping_pb.CLR_INT32, name
    for name in ("TenantId", "OwnerId", "DialogueId", "Split"):
        assert props[name].clr_type == mapping_pb.CLR_STRING, name
    assert root.authorization == mapping_pb.AuthorizationRules(
        owner_field="OwnerId", row_permissions=[mapping_pb.RowPermission(
            role="iverson-loadtest-bypass", can_read_all=True, can_write_all=True, can_delete_all=True)])


def test_a_failed_registration_aborts():
    with pytest.raises(SystemExit, match="registering DialogueTurn failed"):
        dp.register(FakeMappingStub(success=False))


def test_acting_identity_reads_the_bypass_users_claims():
    assert dp.acting_identity(dp.jwt_claims(BYPASS_TOKEN)) == ("tenant_bypass", "sub-123")
    assert dp.acting_identity({"sub": "s", "tenant_id": "tenant_bypass", "groups": "iverson-loadtest-bypass"}) == \
        ("tenant_bypass", "s")


@pytest.mark.parametrize("claims", [
    {"sub": "s", "tenant_id": "tenant_smoke_test", "groups": ["iverson-loadtest-bypass"]},
    {"sub": "s", "tenant_id": "tenant_bypass", "groups": ["operators"]},
    {"tenant_id": "tenant_bypass", "groups": ["iverson-loadtest-bypass"]},
])
def test_any_other_acting_identity_aborts(claims):
    with pytest.raises(SystemExit, match="acting user must carry"):
        dp.acting_identity(claims)


def test_an_unreadable_token_aborts():
    with pytest.raises(SystemExit, match="not a readable JWT"):
        dp.jwt_claims("not-a-jwt")


# ── requests: exact strings ─────────────────────────────────────────────────────────────────

def assert_split_rows(request, split):
    assert request.type_name == "DialogueTurn" and request.source == pb.TYPE_ROWS
    assert len(request.where) == 1
    clause = request.where[0]
    assert (clause.property, clause.operator, clause.clause_type) == ("Split", pb.EQUALS, pb.FILTER)
    assert clause.value.WhichOneof("kind") == "string_val" and clause.value.string_val == split
    assert list(request.partition_by) == ["DialogueId"]
    assert [(s.property, s.descending) for s in request.order_by] == [("TurnIndex", False)]
    assert not request.HasField("after_match")             # server default: SKIP PAST LAST ROW
    assert request.limit == 10_000 and not request.subsets


@pytest.mark.parametrize("split", ["dev", "test"])
def test_readiness_request(split):
    request = dp.readiness_request(split)
    assert_split_rows(request, split)
    assert request.pattern == "A+"
    assert [(d.name, d.expr) for d in request.define] == [("A", "TRUE")]
    assert [(m.name, m.expr) for m in request.measures] == [("s", "SIMILARITY(Utterance, 'a customer message')")]
    assert request.rows_per_match == pb.ALL_ROWS_SHOW_EMPTY
    assert dp.READINESS_TEXT not in dp.DESCRIPTIONS.values()


@pytest.mark.parametrize("intent, text", [(HOTEL, D_HOTEL), (RESTAURANT, D_RESTAURANT), (TRAIN, D_TRAIN),
                                          (ATTRACTION, D_ATTRACTION)])
@pytest.mark.parametrize("split", ["dev", "test"])
def test_calibration_and_single_intent_requests(split, intent, text):
    request = dp.calibration_request(split, intent)
    assert_split_rows(request, split)
    assert request.pattern == "A+"
    assert [(d.name, d.expr) for d in request.define] == [("A", "TRUE")]
    assert [(m.name, m.expr) for m in request.measures] == [("s", f"SIMILARITY(Utterance, '{text}')")]
    assert request.rows_per_match == pb.ALL_ROWS_SHOW_EMPTY


def test_probe_s_request():
    request = dp.probe_s_request("MUL0484.json")
    assert request.type_name == "DialogueTurn" and request.source == pb.TYPE_ROWS
    (clause,) = request.where
    assert (clause.property, clause.operator, clause.value.string_val) == ("DialogueId", pb.EQUALS, "MUL0484.json")
    assert list(request.partition_by) == ["DialogueId"]
    assert [s.property for s in request.order_by] == ["TurnIndex"]
    assert request.pattern == "A"
    assert [(d.name, d.expr) for d in request.define] == [("A", "TurnIndex = 0")]
    assert not request.measures and request.rows_per_match == pb.ONE_ROW and request.limit == 10_000


def test_probe_t_request_is_ordered_semantic_on_dev_at_the_placeholder_theta():
    request = dp.probe_t_request()
    assert_split_rows(request, "dev")
    assert request.pattern == "A Z* B"
    assert [(d.name, d.expr) for d in request.define] == [
        ("A", f"SIMILARITY(Utterance, '{D_HOTEL}') > 0.5"),
        ("B", f"SIMILARITY(Utterance, '{D_RESTAURANT}') > 0.5"),
        ("Z", "TRUE")]
    assert request.rows_per_match == pb.ONE_ROW and not request.measures


THETA = {HOTEL: 0.5333333333333333, RESTAURANT: 0.54, TRAIN: 0.5136, ATTRACTION: 0.4719}


def test_ordered_semantic_request():
    request = dp.arm_request("ordered_semantic", "test", HOTEL, TRAIN, THETA)
    assert_split_rows(request, "test")
    assert request.pattern == "A Z* B"
    assert [(d.name, d.expr) for d in request.define] == [
        ("A", f"SIMILARITY(Utterance, '{D_HOTEL}') > 0.5333333333333333"),
        ("B", f"SIMILARITY(Utterance, '{D_TRAIN}') > 0.5136"),
        ("Z", "TRUE")]
    assert not request.measures and request.rows_per_match == pb.ONE_ROW


def test_order_free_semantic_request():
    request = dp.arm_request("order_free_semantic", "test", ATTRACTION, RESTAURANT, THETA)
    assert_split_rows(request, "test")
    assert request.pattern == "(A Z* B | B Z* A)"
    assert [(d.name, d.expr) for d in request.define] == [
        ("A", f"SIMILARITY(Utterance, '{D_ATTRACTION}') > 0.4719"),
        ("B", f"SIMILARITY(Utterance, '{D_RESTAURANT}') > 0.54"),
        ("Z", "TRUE")]
    assert not request.measures and request.rows_per_match == pb.ONE_ROW


@pytest.mark.parametrize("x, y, a, b", [
    (HOTEL, TRAIN, "MentionsHotel = 1", "MentionsTrain = 1"),
    (ATTRACTION, RESTAURANT, "MentionsAttraction = 1", "MentionsRestaurant = 1"),
])
def test_ordered_keyword_request(x, y, a, b):
    request = dp.arm_request("ordered_keyword", "test", x, y, {})
    assert_split_rows(request, "test")
    assert request.pattern == "A Z* B"
    assert [(d.name, d.expr) for d in request.define] == [("A", a), ("B", b), ("Z", "TRUE")]
    assert not request.measures and request.rows_per_match == pb.ONE_ROW


def test_descriptions_are_the_pre_registered_ones():
    assert dp.DESCRIPTIONS == {HOTEL: D_HOTEL, RESTAURANT: D_RESTAURANT, TRAIN: D_TRAIN, ATTRACTION: D_ATTRACTION}


# ── parsing ─────────────────────────────────────────────────────────────────────────────────

def test_one_row_results_give_the_matched_dialogues():
    rows = [row(DialogueId="D1"), row(DialogueId="D2"), row(DialogueId="D1")]
    assert dp.matched_dialogues(rows, {"D1", "D2", "D3"}, "t") == {"D1", "D2"}


def test_a_matched_dialogue_outside_the_written_set_aborts():
    with pytest.raises(SystemExit, match="not a written dialogue"):
        dp.matched_dialogues([row(DialogueId="DEV9")], {"D1"}, "t")


def test_a_one_row_result_without_dialogue_id_aborts():
    with pytest.raises(SystemExit, match="'DialogueId'"):
        dp.matched_dialogues([row(dialogueid="D1")], {"D1"}, "t")


def test_all_rows_results_give_per_turn_scores():
    rows = [row(DialogueId="D1", TurnIndex=1.0, s=0.4, Utterance="x"), row(DialogueId="D1", TurnIndex=0.0, s=None)]
    assert dp.turn_scores(rows, "t") == {("D1", 1): 0.4, ("D1", 0): None}


def test_a_repeated_or_fractional_turn_aborts():
    with pytest.raises(SystemExit, match="twice"):
        dp.turn_scores([row(DialogueId="D", TurnIndex=0.0, s=0.1)] * 2, "t")
    with pytest.raises(SystemExit, match="whole number"):
        dp.turn_scores([row(DialogueId="D", TurnIndex=0.5, s=0.1)], "t")


def test_coverage_problem():
    expected = {("D", 0), ("D", 1)}
    assert dp.coverage_problem({("D", 0): 0.1, ("D", 1): 0.2}, expected) is None
    assert dp.coverage_problem({("D", 0): 0.1}, expected)[0] is False                      # missing: wait
    assert dp.coverage_problem({("D", 0): 0.1, ("D", 1): None}, expected)[0] is False       # NULL: wait
    assert dp.coverage_problem({("D", 0): 0.1, ("D", 1): 0.2, ("E", 0): 0.3}, expected)[0] is True   # unknown


def test_strict_scores_abort_on_a_null_and_on_a_missing_turn():
    expected = {("D", 0), ("D", 1)}
    with pytest.raises(SystemExit, match="NULL"):
        dp.strict_turn_scores([row(DialogueId="D", TurnIndex=0.0, s=0.1), row(DialogueId="D", TurnIndex=1.0, s=None)],
                              expected, "t")
    with pytest.raises(SystemExit, match="1 of 2 turns"):
        dp.strict_turn_scores([row(DialogueId="D", TurnIndex=0.0, s=0.1)], expected, "t")


# ── metrics ─────────────────────────────────────────────────────────────────────────────────

def test_prf_on_a_hand_computed_case():
    m = dp.prf([True, True, True, False, False], [True, False, True, True, False])
    assert (m["tp"], m["fp"], m["fn"]) == (2, 1, 1)
    assert m["precision"] == pytest.approx(2 / 3) and m["recall"] == pytest.approx(2 / 3)
    assert m["f1"] == pytest.approx(2 / 3)


def test_prf_with_nothing_predicted_or_nothing_gold_is_zero_not_an_error():
    assert dp.prf([False, False], [True, False]) == {"tp": 0, "fp": 0, "fn": 1, "precision": 0.0, "recall": 0.0, "f1": 0.0}
    assert dp.prf([False], [False])["f1"] == 0.0


@pytest.mark.parametrize("tp, fp, fn", [(3, 1, 2), (1, 0, 9), (7, 7, 0), (10, 3, 3)])
def test_f1_from_counts_is_the_harmonic_mean_of_p_and_r(tp, fp, fn):
    p, r = tp / (tp + fp), tp / (tp + fn)
    assert dp.f1_from_counts(tp, fp, fn) == pytest.approx(2 * p * r / (p + r))


def test_macro_f1_is_the_mean_over_pairs():
    assert dp.macro_f1([0.5, 1.0, 0.0, 0.25]) == pytest.approx(0.4375)


def test_single_intent_prediction_is_any_turn_strictly_above_theta():
    scores = {("D1", 0): 0.2, ("D1", 1): 0.7, ("D2", 0): 0.5, ("D3", 0): 0.49}
    assert dp.single_intent_predictions(scores, 0.5, ["D1", "D2", "D3", "D4"]) == [True, False, False, False]


# ── theta grid ──────────────────────────────────────────────────────────────────────────────

def one_turn_scores(n=100):
    """Dialogue Di has one turn with s = i/100, so the linear p-th percentile is 0.0099 p."""
    ids = [f"D{i}" for i in range(n)]
    return {(f"D{i}", 0): i / 100 for i in range(n)}, ids


def test_theta_grid_is_p50_to_p99_and_takes_the_best_f1():
    scores, ids = one_turn_scores()
    gold = [i >= 90 for i in range(100)]
    result = dp.theta_grid(scores, gold, ids)
    assert [g[0] for g in result["grid"]] == list(range(50, 100))
    assert [g[1] for g in result["grid"]] == pytest.approx([0.0099 * p for p in range(50, 100)])
    assert (result["percentile"], result["f1"]) == (90, 1.0)
    assert result["theta"] == pytest.approx(0.891)
    assert result["at_grid_edge"] is False


def test_theta_grid_tie_goes_to_the_lower_theta():
    """Gold = D60..D69 and D90..D99. p90 (theta 0.891) predicts D90..D99: F1 = 20/30. p60 (theta
    0.594) predicts D60..D99: F1 = 40/60. Equal F1s, and everything between scores lower."""
    scores, ids = one_turn_scores()
    gold = [60 <= i < 70 or i >= 90 for i in range(100)]
    result = dp.theta_grid(scores, gold, ids)
    by_p = {p: f1 for p, _, f1 in result["grid"]}
    assert by_p[60] == by_p[90] == max(by_p.values()) == 2 / 3
    assert result["percentile"] == 60
    assert result["theta"] == pytest.approx(0.594)


def test_theta_grid_scores_dialogues_by_their_best_turn_and_flags_the_edge():
    # D0..D49 score 0.0 on every turn; D50..D99 have one turn at 0.9 among zeros. Every candidate
    # but p99 (0.9) predicts exactly D50..D99; the lowest is p50.
    scores = {}
    for i in range(100):
        for t in range(3):
            scores[(f"D{i}", t)] = 0.9 if (i >= 50 and t == 1) else 0.0
    gold = [i >= 50 for i in range(100)]
    result = dp.theta_grid(scores, gold, [f"D{i}" for i in range(100)])
    assert result["f1"] == 1.0 and result["percentile"] == 50 and result["at_grid_edge"] is True


# ── bootstrap and gate ──────────────────────────────────────────────────────────────────────

def small_experiment(n=60, seed=7):
    rng = np.random.default_rng(seed)
    gold = {pair: list(rng.random(n) < 0.3) for pair in dp.PAIRS}
    preds = {}
    for arm, noise in (("ordered_semantic", 0.1), ("order_free_semantic", 0.3), ("ordered_keyword", 0.4)):
        preds[arm] = {pair: [g != (rng.random() < noise) for g in gold[pair]] for pair in dp.PAIRS}
    return preds, gold


def test_bootstrap_constants():
    assert (dp.BOOTSTRAP_RESAMPLES, dp.BOOTSTRAP_SEED, dp.CI_PERCENTILES) == (10_000, 20260929, (2.5, 97.5))


def test_bootstrap_is_deterministic_under_its_seed():
    preds, gold = small_experiment()
    a = dp.bootstrap_macro_f1(preds, gold, n_resamples=300)
    b = dp.bootstrap_macro_f1(preds, gold, n_resamples=300)
    c = dp.bootstrap_macro_f1(preds, gold, n_resamples=300, seed=1)
    for arm in dp.ARMS:
        assert np.array_equal(a[arm], b[arm])
        assert not np.array_equal(a[arm], c[arm])


def test_bootstrap_golden_values_at_the_default_seed():
    """Pins what the default seed draws, so a changed seed (or draw order) cannot pass silently."""
    preds, gold = small_experiment()
    draws = dp.bootstrap_macro_f1(preds, gold, n_resamples=200)
    diff = draws["ordered_semantic"] - draws["order_free_semantic"]
    assert float(draws["ordered_semantic"][0]) == pytest.approx(GOLDEN_FIRST_DRAW, abs=1e-12)
    assert dp.percentile_ci(diff) == pytest.approx(GOLDEN_CI, abs=1e-12)


GOLDEN_FIRST_DRAW = 0.7899718799718799      # from the prototype run at seed 20260929
GOLDEN_CI = [0.172263711118383, 0.29976554584765214]


def test_bootstrap_draws_are_macro_f1_of_a_resample():
    """With every dialogue drawn once (the identity resample) a draw equals the point macro-F1; here
    a perfect arm scores 1.0 in every resample, and two identical arms differ by exactly 0."""
    preds, gold = small_experiment()
    preds["ordered_semantic"] = {pair: list(gold[pair]) for pair in dp.PAIRS}
    preds["order_free_semantic"] = preds["ordered_semantic"]
    draws = dp.bootstrap_macro_f1(preds, gold, n_resamples=50)
    assert np.all(draws["ordered_semantic"] == 1.0)
    assert np.all(draws["ordered_semantic"] - draws["order_free_semantic"] == 0.0)


def test_bootstrap_resamples_dialogues_with_replacement():
    """One gold-positive dialogue per pair, predicted right by one arm: a resample that misses it
    scores F1 0 for that pair, so the draws vary; they are never above the full-sample value."""
    n = 30
    gold = {pair: [i == 0 for i in range(n)] for pair in dp.PAIRS}
    preds = {arm: {pair: [i == 0 for i in range(n)] for pair in dp.PAIRS} for arm in ("a", "b")}
    preds["b"] = {pair: [False] * n for pair in dp.PAIRS}
    draws = dp.bootstrap_macro_f1(preds, gold, n_resamples=400)
    assert set(np.unique(draws["a"])) == {0.0, 1.0}
    assert 0.55 < draws["a"].mean() < 0.72          # P(dialogue 0 is drawn) = 1 - (29/30)^30 = 0.638
    assert np.all(draws["b"] == 0.0)


def test_percentile_ci_is_the_2_5_and_97_5_percentiles_in_order():
    lo, hi = dp.percentile_ci(np.arange(0, 1001, dtype=float))
    assert (lo, hi) == (25.0, 975.0)
    preds, gold = small_experiment()
    draws = dp.bootstrap_macro_f1(preds, gold, n_resamples=300)
    lo, hi = dp.percentile_ci(draws["ordered_semantic"] - draws["ordered_keyword"])
    assert lo <= hi


@pytest.mark.parametrize("ci, verdict", [([0.001, 0.2], "GO"), ([0.0, 0.2], "NO-GO"), ([-0.05, 0.2], "NO-GO"),
                                         ([-0.3, -0.1], "NO-GO")])
def test_gate_is_go_only_when_the_ci_is_entirely_above_zero(ci, verdict):
    assert dp.gate(ci) == verdict


# ── data download ───────────────────────────────────────────────────────────────────────────

def test_the_pinned_files_are_the_specs():
    assert dp.DATA_FILES == (
        ("dev", "dialogues_001.json", "ee1809dcf412ccba0a47d7c2db2d361e"),
        ("dev", "dialogues_002.json", "295eaea9b341b3e21589e4b97c7ca335"),
        ("test", "dialogues_001.json", "e37f05c2800286768d273aaf4a8e85a4"),
        ("test", "dialogues_002.json", "2aa1b12f2cf210a7b466ef260f397e32"))
    assert dp.DATA_BASE_URL.endswith("/budzianowski/multiwoz/master/data/MultiWOZ_2.2")


def fake_files(monkeypatch, contents):
    """Pin DATA_FILES to synthetic contents {(split, name): bytes} and return a fake urlopen."""
    monkeypatch.setattr(dp, "DATA_FILES", tuple((s, n, hashlib.md5(b).hexdigest()) for (s, n), b in contents.items()))
    fetched = []

    def urlopen(url, timeout=None):
        fetched.append(url)
        split, name = url.split("/")[-2:]
        return io.BytesIO(served.get((split, name), contents[(split, name)]))
    served = {}
    return urlopen, fetched, served


def test_ensure_data_downloads_missing_files_and_checks_them(tmp_path, monkeypatch):
    contents = {("dev", "dialogues_001.json"): b"[1]", ("test", "dialogues_001.json"): b"[2]"}
    urlopen, fetched, _ = fake_files(monkeypatch, contents)
    md5s = dp.ensure_data(str(tmp_path), urlopen=urlopen)
    assert fetched == [f"{dp.DATA_BASE_URL}/dev/dialogues_001.json", f"{dp.DATA_BASE_URL}/test/dialogues_001.json"]
    assert (tmp_path / "dev" / "dialogues_001.json").read_bytes() == b"[1]"
    assert md5s["test/dialogues_001.json"] == hashlib.md5(b"[2]").hexdigest()
    dp.ensure_data(str(tmp_path), urlopen=urlopen)                  # present and right: no second download
    assert len(fetched) == 2


def test_a_downloaded_md5_mismatch_aborts_and_is_not_put_in_place(tmp_path, monkeypatch):
    urlopen, _, served = fake_files(monkeypatch, {("dev", "dialogues_001.json"): b"[1]"})
    served[("dev", "dialogues_001.json")] = b"[tampered]"
    with pytest.raises(SystemExit, match="md5"):
        dp.ensure_data(str(tmp_path), urlopen=urlopen)
    assert not (tmp_path / "dev" / "dialogues_001.json").exists()


def test_a_present_file_with_the_wrong_md5_aborts(tmp_path, monkeypatch):
    urlopen, fetched, _ = fake_files(monkeypatch, {("dev", "dialogues_001.json"): b"[1]"})
    (tmp_path / "dev").mkdir()
    (tmp_path / "dev" / "dialogues_001.json").write_bytes(b"[other]")
    with pytest.raises(SystemExit, match="md5"):
        dp.ensure_data(str(tmp_path), urlopen=urlopen)
    assert fetched == []


# ── the live flow over fakes ────────────────────────────────────────────────────────────────

def F(service, intent, slot):
    return frame(service, intent, {f"{service}-x": [slot]})


# Test split: T1 hotel then train; T2 train then hotel; T3 hotel and train on one turn (a tie);
# T4 restaurant only. Dev: two dialogues for calibration.
SPLITS = {
    "dev": [dialogue("V1", [turn("a hotel to stay", F("hotel", HOTEL, "a")), turn("a train", F("train", TRAIN, "b"))]),
            dialogue("V2", [turn("food", F("restaurant", RESTAURANT, "c")), turn("a museum", F("attraction", ATTRACTION, "d"))])],
    "test": [dialogue("T1", [turn("a hotel", F("hotel", HOTEL, "a")), turn("hmm"), turn("a train", F("train", TRAIN, "b"))]),
             dialogue("T2", [turn("a train", F("train", TRAIN, "b")), turn("a hotel", F("hotel", HOTEL, "a"))]),
             dialogue("T3", [turn("hotel and train", F("hotel", HOTEL, "a"), F("train", TRAIN, "b"))]),
             dialogue("T4", [turn("dinner", F("restaurant", RESTAURANT, "c"))])],
}
SIMS = {                              # (DialogueId, TurnIndex) -> {description: s}; everything else 0.2
    ("V1", 0): {D_HOTEL: 0.8}, ("V1", 1): {D_TRAIN: 0.8}, ("V2", 0): {D_RESTAURANT: 0.8}, ("V2", 1): {D_ATTRACTION: 0.8},
    ("T1", 0): {D_HOTEL: 0.9}, ("T1", 2): {D_TRAIN: 0.9}, ("T2", 0): {D_TRAIN: 0.9}, ("T2", 1): {D_HOTEL: 0.9},
    ("T3", 0): {D_HOTEL: 0.9, D_TRAIN: 0.9}, ("T4", 0): {D_RESTAURANT: 0.9},
}


def engine_turns(splits=SPLITS, readiness=0.3):
    turns = []
    for record in dp.build_turns(splits):
        key = (record["dialogue_id"], record["turn_index"])
        sims = {text: SIMS.get(key, {}).get(text, 0.2) for text in dp.DESCRIPTIONS.values()}
        sims[dp.READINESS_TEXT] = readiness
        turns.append({"DialogueId": key[0], "TurnIndex": key[1], "Split": record["split"], "sims": sims,
                      **{dp.FLAG_COLUMNS[d]: record[dp.FLAG_FIELDS[d]] for d in dp.KEYWORDS}})
    return turns


def ingested(tmp_path, engine=None, **kw):
    engine = engine or FakeEngine(engine_turns())
    stub = FakeMappingStub()
    live = live_for(tmp_path, engine, mapping_stub=stub, **kw)
    dp.cmd_ingest(live, SPLITS, {"m": "d5"})
    return live, engine, stub


def test_ingest_registers_writes_every_user_turn_and_records_keys(tmp_path):
    live, engine, stub = ingested(tmp_path)
    assert len(stub.requests) == 1 and stub.requests[0].root_type.authorization.owner_field == "OwnerId"
    assert len(engine.persisted) == 11
    first = engine.persisted[0]
    assert (first.dialogue_id, first.turn_index, first.split, first.tenant_id, first.owner_id, first.id) == \
        ("V1", 0, "dev", "tenant_bypass", "sub-123", None)
    ingest = json.loads((tmp_path / "ingest.json").read_text())
    assert ingest["counts"] == {"dev": 4, "test": 7}
    assert ingest["dialogues"] == {"V1": "dev", "V2": "dev", "T1": "test", "T2": "test", "T3": "test", "T4": "test"}
    assert ingest["keys"]["T1"] == ["key-5", "key-6", "key-7"]              # after dev's 4 turns
    assert ingest["first_test_dialogue"] == "T1" and ingest["composite"] == "c1"
    assert [r["split"] for r in ingest["readiness"]] == ["dev", "test"]
    assert not (tmp_path / "ingest-progress.jsonl").exists()
    readiness = [r for r in engine.log if r.rows_per_match == pb.ALL_ROWS_SHOW_EMPTY]
    assert [r.where[0].value.string_val for r in readiness] == ["dev", "test"]


def test_ingest_refuses_to_run_twice(tmp_path):
    ingested(tmp_path)
    with pytest.raises(SystemExit, match="already"):
        dp.cmd_ingest(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS, {})


def test_an_interrupted_ingest_leaves_its_keys_and_blocks_a_rerun(tmp_path):
    engine = FakeEngine(engine_turns())
    calls = []

    def persist(entity):
        calls.append(entity)
        if len(calls) == 3:
            raise RuntimeError("persist failed: boom")
        return f"key-{len(calls)}"
    engine.persist = persist
    with pytest.raises(SystemExit, match="persist failed at turn 3"):
        dp.cmd_ingest(live_for(tmp_path, engine), SPLITS, {})
    lines = (tmp_path / "ingest-progress.jsonl").read_text().splitlines()
    assert [json.loads(line)["key"] for line in lines] == ["key-1", "key-2"]
    with pytest.raises(SystemExit, match="did not finish"):
        dp.cmd_ingest(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS, {})


def test_ingest_aborts_on_a_build_mismatch_after_writing(tmp_path):
    with pytest.raises(SystemExit, match="BUILD MISMATCH"):
        ingested(tmp_path, composites=["c1", "c1", "c2"])
    assert not (tmp_path / "ingest.json").exists()


def test_readiness_waits_for_embeddings_then_passes(tmp_path):
    turns = engine_turns()
    turns[0]["sims"][dp.READINESS_TEXT] = None             # V1 turn 0 not embedded yet
    engine = FakeEngine(turns)
    clock = Clock()
    original = engine.match_pattern

    def match_pattern(request):
        rows = original(request)
        turns[0]["sims"][dp.READINESS_TEXT] = 0.3           # embedded by the next attempt
        return rows
    engine.match_pattern = match_pattern
    live, _, _ = ingested(tmp_path, engine=engine, clock=clock)
    ingest = json.loads((tmp_path / "ingest.json").read_text())
    assert ingest["readiness"][0]["attempts"] == 2 and clock.now == dp.READINESS_POLL_SECONDS


def test_readiness_times_out_and_aborts(tmp_path):
    turns = [t for t in engine_turns() if (t["DialogueId"], t["TurnIndex"]) != ("V2", 1)]   # never visible
    clock = Clock()
    with pytest.raises(SystemExit, match="readiness dev TIMED OUT"):
        ingested(tmp_path, engine=FakeEngine(turns), clock=clock)
    assert clock.now >= dp.READINESS_DEADLINE_SECONDS


def test_readiness_aborts_at_once_on_a_row_that_was_never_written(tmp_path):
    turns = engine_turns() + [{**engine_turns()[0], "DialogueId": "GHOST"}]
    clock = Clock()
    with pytest.raises(SystemExit, match="never written"):
        ingested(tmp_path, engine=FakeEngine(turns), clock=clock)
    assert clock.now == 0.0


def test_ready_reruns_readiness_after_a_timeout(tmp_path):
    turns = engine_turns()
    turns[0]["sims"][dp.READINESS_TEXT] = None
    with pytest.raises(SystemExit, match="TIMED OUT"):
        ingested(tmp_path, engine=FakeEngine(turns))
    assert json.loads((tmp_path / "ingest.json").read_text())["readiness"] is None
    with pytest.raises(SystemExit, match="no passed readiness"):
        dp.cmd_probe_s(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS)
    dp.cmd_ready(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS)
    assert json.loads((tmp_path / "ingest.json").read_text())["readiness"][1]["rows"] == 7


def test_probe_s_passes_on_exactly_one_row_for_the_first_test_dialogue(tmp_path):
    live, engine, _ = ingested(tmp_path)
    dp.cmd_probe_s(live, SPLITS)
    probe = json.loads((tmp_path / "probe-s.json").read_text())
    assert probe["dialogue_id"] == "T1" and probe["row"] == {"DialogueId": "T1"}
    assert engine.log[-1].where[0].value.string_val == "T1"


@pytest.mark.parametrize("answer", [[], [row(DialogueId="T1")] * 2, [row(DialogueId="T2")]])
def test_probe_s_fails_on_anything_but_one_row_for_that_dialogue(tmp_path, answer):
    ingested(tmp_path)
    with pytest.raises(SystemExit, match="probe S FAIL"):
        dp.cmd_probe_s(live_for(tmp_path, FakeEngine(respond=lambda r: answer)), SPLITS)


def test_probe_t_times_one_dev_call_and_records_it(tmp_path):
    ingested(tmp_path)
    engine = FakeEngine(engine_turns())
    dp.cmd_probe_t(live_for(tmp_path, engine, clock=Clock(step=1.0)), SPLITS)
    probe = json.loads((tmp_path / "probe-t.json").read_text())
    assert probe["passed"] and probe["placeholder_theta"] == 0.5 and probe["rows_in_split"] == 4
    assert probe["pattern"] == "A Z* B" and probe["limit"] == 10_000
    assert engine.log[-1].where[0].value.string_val == "dev"


def test_probe_t_stops_when_the_call_is_not_under_30_seconds(tmp_path):
    ingested(tmp_path)
    with pytest.raises(SystemExit, match="probe T STOP"):
        dp.cmd_probe_t(live_for(tmp_path, FakeEngine(engine_turns()), clock=Clock(step=30.0)), SPLITS)
    assert json.loads((tmp_path / "probe-t.json").read_text())["passed"] is False


def test_a_timed_out_call_aborts(tmp_path):
    ingested(tmp_path)
    turns = engine_turns()
    base = FakeEngine(turns)

    def respond(request):
        if request.rows_per_match == pb.ONE_ROW:
            raise FakeRpcError()
        return base.match_pattern(request)
    with pytest.raises(SystemExit, match="DEADLINE_EXCEEDED"):
        dp.cmd_probe_t(live_for(tmp_path, FakeEngine(respond=respond)), SPLITS)


def calibrated(tmp_path):
    live, engine, _ = ingested(tmp_path)
    dp.cmd_calibrate(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS)
    return json.loads((tmp_path / "theta.json").read_text())


def test_calibrate_freezes_four_thetas_from_dev(tmp_path):
    theta = calibrated(tmp_path)
    assert set(theta["theta"]) == set(dp.INTENTS)
    # dev per-turn s for hotel: [0.8, 0.2, 0.2, 0.2] -> p50..p99 all within [0.2, 0.8); V1 is the only
    # hotel-gold dialogue and every candidate below 0.8 predicts exactly it: F1 1.0 at p50, the lowest.
    assert theta["by_intent"][HOTEL]["percentile"] == 50 and theta["by_intent"][HOTEL]["f1"] == 1.0
    assert theta["theta"][HOTEL] == pytest.approx(0.2)
    assert [c["what"] for c in theta["calls"]] == [f"calibration {i}" for i in dp.INTENTS]
    with pytest.raises(SystemExit, match="frozen"):
        dp.cmd_calibrate(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS)


def test_calibrate_aborts_on_a_null_similarity(tmp_path):
    ingested(tmp_path)
    turns = engine_turns()
    turns[1]["sims"][D_HOTEL] = None
    with pytest.raises(SystemExit, match="NULL"):
        dp.cmd_calibrate(live_for(tmp_path, FakeEngine(turns)), SPLITS)
    assert not (tmp_path / "theta.json").exists()


def test_score_runs_the_40_calls_and_writes_the_verdict(tmp_path):
    calibrated(tmp_path)
    engine = FakeEngine(engine_turns())
    dp.cmd_score(live_for(tmp_path, engine), SPLITS)
    results = json.loads((tmp_path / "results.json").read_text())
    scored = engine.log[1:]                                  # [0] is the readiness check
    assert len(scored) == 40 and len(results["calls"]) == 40
    assert all(r.where[0].value.string_val == "test" for r in engine.log)
    assert sum(r.rows_per_match == pb.ONE_ROW for r in scored) == 36
    assert [r.measures[0].expr for r in scored[36:]] == [f"SIMILARITY(Utterance, '{d}')" for d in dp.DESCRIPTIONS.values()]
    pairs = {(p["x"], p["y"]): p for p in results["pairs"]}
    ht = pairs[(HOTEL, TRAIN)]
    assert (ht["gold_positive"], ht["reversed_negative"]) == (1, 1)                 # T1; T2
    # dev-calibrated thetas are 0.2, so every test turn qualifies for every description:
    # The dev-calibrated thetas are 0.2 (see test_calibrate_freezes...), and s > 0.2 is strict, so only
    # the 0.9 turns qualify: ordered finds T1; order-free T1 and T2; T3's same-turn pair matches neither.
    assert ht["arms"]["ordered_semantic"]["predicted"] == ht["arms"]["ordered_semantic"]["tp"] == 1
    assert ht["arms"]["order_free_semantic"]["predicted"] == 2
    assert ht["arms"]["ordered_keyword"]["predicted"] == ht["arms"]["ordered_keyword"]["tp"] == 1   # T1's words
    assert results["ties"] == 1                                                     # T3
    assert set(results["macro_f1"]) == set(dp.ARMS) and results["gate"] in ("GO", "NO-GO")
    b = results["bootstrap"]
    assert b["resamples"] == 10_000 and b["seed"] == 20260929
    assert b["ordered_minus_order_free"]["ci95"][0] <= b["ordered_minus_order_free"]["ci95"][1]
    assert set(results["single_intent"]) == set(dp.INTENTS)
    with pytest.raises(SystemExit, match="scored once"):
        dp.cmd_score(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS)


def test_score_refuses_to_run_before_calibration(tmp_path):
    ingested(tmp_path)
    with pytest.raises(SystemExit, match="run calibrate first"):
        dp.cmd_score(live_for(tmp_path, FakeEngine(engine_turns())), SPLITS)


def test_score_aborts_on_a_count_at_limit(tmp_path):
    calibrated(tmp_path)
    base = FakeEngine(engine_turns())

    def respond(request):
        if request.rows_per_match == pb.ONE_ROW:
            return [row(DialogueId="T1")] * request.limit
        return base.match_pattern(request)
    with pytest.raises(SystemExit, match="truncated"):
        dp.cmd_score(live_for(tmp_path, FakeEngine(respond=respond)), SPLITS)
    assert not (tmp_path / "results.json").exists()


def test_score_aborts_on_a_matched_dialogue_that_is_not_a_written_test_dialogue(tmp_path):
    calibrated(tmp_path)
    base = FakeEngine(engine_turns())

    def respond(request):
        if request.rows_per_match == pb.ONE_ROW:
            return [row(DialogueId="V1")]                    # a dev dialogue
        return base.match_pattern(request)
    with pytest.raises(SystemExit, match="not a written dialogue"):
        dp.cmd_score(live_for(tmp_path, FakeEngine(respond=respond)), SPLITS)


@pytest.mark.parametrize("composites, where", [(["c9"], "before"), (["c1", "c9"], "after")])
def test_score_aborts_on_a_build_mismatch(tmp_path, composites, where):
    calibrated(tmp_path)
    engine = FakeEngine(engine_turns())
    with pytest.raises(SystemExit, match=f"BUILD MISMATCH at score \\({where}\\)"):
        dp.cmd_score(live_for(tmp_path, engine, composites=composites), SPLITS)
    assert not (tmp_path / "results.json").exists()
    if where == "before":
        assert engine.log == []


def test_score_results_on_hand_built_matches():
    """score_results over known matches: ordered finds T1 for hotel->train, order-free also T2."""
    matches = {arm: {pair: set() for pair in dp.PAIRS} for arm in dp.ARMS}
    matches["ordered_semantic"][(HOTEL, TRAIN)] = {"T1"}
    matches["order_free_semantic"][(HOTEL, TRAIN)] = {"T1", "T2"}
    single = {i: {("T1", 0): 0.1, ("T1", 1): 0.1, ("T1", 2): 0.1, ("T2", 0): 0.1, ("T2", 1): 0.1, ("T3", 0): 0.1,
                  ("T4", 0): 0.1} for i in dp.INTENTS}
    single[RESTAURANT][("T4", 0)] = 0.9
    results = dp.score_results(SPLITS, matches, single, {i: 0.5 for i in dp.INTENTS})
    ht = {(p["x"], p["y"]): p for p in results["pairs"]}[(HOTEL, TRAIN)]["arms"]
    assert ht["ordered_semantic"]["f1"] == 1.0 and ht["order_free_semantic"]["precision"] == 0.5
    assert results["macro_f1"]["ordered_semantic"] == pytest.approx(1 / 12)
    assert results["macro_f1"]["order_free_semantic"] == pytest.approx((2 / 3) / 12)
    assert results["single_intent"][RESTAURANT]["f1"] == 1.0 and results["single_intent"][HOTEL]["f1"] == 0.0


def test_a_readiness_call_at_limit_aborts_rather_than_waiting(tmp_path):
    def respond(request):
        return [row(DialogueId="V1", TurnIndex=float(i), s=0.3) for i in range(request.limit)]
    clock = Clock()
    with pytest.raises(SystemExit, match="truncated"):
        ingested(tmp_path, engine=FakeEngine(respond=respond), clock=clock)
    assert clock.now == 0.0
```

- [ ] **Step 2: Run them and watch them fail**

Run: `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py -q`
Expected: a collection error (`No module named 'dialogue_patterns'`).

- [ ] **Step 3: Write the script**

```python
#!/usr/bin/env python3
"""The MatchPattern dialogue-sequence use-case proof (spec
docs/specs/2026-09-29-matchpattern-dialogue-sequences-design.md).

The question: "which dialogues show a customer asking about X and then, later, about Y?", asked of
MultiWOZ 2.2's test split through MatchPattern (TYPE_ROWS) and scored against the dataset's own
intent labels. Every rule, string and constant below is fixed by the spec; the spec section each
one comes from is named where it is defined.

Subcommands, all taking --out (the run's OUT directory; everything this script writes goes there):

    ingest     Download and md5-check the four MultiWOZ 2.2 files into OUT/data/, register
               DialogueTurn with the conformance harness's authorization rules, write every dev and
               test USER turn with one persist() each, record the returned keys and a
               DialogueId -> split map to OUT/ingest.json, then run the readiness check.
    ready      Re-run only the readiness check (for a readiness that timed out after a completed
               write); records its result in OUT/ingest.json. Never writes a row.
    probe-s    Phase 0 step 2: `where DialogueId = <first test dialogue>`, A, A AS TurnIndex = 0,
               ONE ROW; must return exactly one row, for that dialogue. Writes OUT/probe-s.json.
    probe-t    Phase 0 step 3: one ordered-semantic call over the whole dev split at the placeholder
               theta 0.5; must finish under the server's 30 s timeout. Writes OUT/probe-t.json.
    calibrate  Phase 1: four calibration calls on dev, the theta grid (p50..p99, dialogue-level
               single-intent F1, ties to the lower theta), frozen to OUT/theta.json.
    score      Phase 2: 36 pair calls (12 pairs x 3 arms) and 4 single-intent calls on test, the
               metrics, the paired bootstrap and the gate. Writes OUT/results.json.

Order: ingest (or ingest, then ready) -> probe-s -> probe-t -> calibrate -> score. calibrate refuses
to overwrite theta.json (the thetas are frozen before any test-split call that uses a description,
spec "Pre-registered constants"); score refuses to run without it and refuses to overwrite
results.json.

Failure is loud (spec "Integrity"): a gRPC error, a result count equal to the `limit` sent, a
matched DialogueId that is not a written dialogue of the split asked for, a NULL similarity, a
readiness check that has not passed by its deadline, or a /build composite that differs from the one
recorded at ingest (GET before and after every subcommand) exits non-zero before that subcommand's
output file is written. The one exception is probe-t's STOP (a call that completed, but not under
30 s): probe-t.json is written with "passed": false, because that timing is the finding, and the
script then exits non-zero.

Identity and transport are pattern_leg.py's, imported, never copied: TokenSession,
mint_acting_user_token, mint_service_token_from_env, build_channel (through pattern_leg.connect,
whose BenchmarkDocument coordinator is discarded), fetch_build, execute and the /build check
helpers. Rows are written as iverson-loadtest-bypass-user, whose token carries tenant_id
tenant_bypass and the iverson-loadtest-bypass group; ingest refuses any other acting identity.

numpy is needed (the theta percentiles and the bootstrap), so run with the python-libs PYTHONPATH:

    . /home/ben/iverson-benchmark-data/bench-env.sh
    export IVERSON_ACTING_USER_BYPASS_PASSWORD=...
    export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
    python3 Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py ingest --out OUT
    python3 Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py probe-s --out OUT
    python3 Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py probe-t --out OUT
    python3 Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py calibrate --out OUT
    python3 Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py score --out OUT
"""
import argparse
import base64
import hashlib
import json
import os
import re
import sys
import time
import urllib.request
import uuid

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pattern_leg as pl  # noqa: E402  (also puts the SDK root on sys.path)
from iverson_client import (  # noqa: E402
    EntityCoordinator, SchemaRegistrar, iverson_embedding, iverson_entity, iverson_key, match_pattern)
from iverson_client.generated import object_mapping_pb2 as mapping_pb  # noqa: E402
from iverson_client.generated import object_mapping_pb2_grpc as mapping_grpc  # noqa: E402

pb = pl.pb

# ── Pre-registered constants (spec "Pre-registered constants"; never edited after scoring) ──

INTENTS = ("find_hotel", "find_restaurant", "find_train", "find_attraction")
SERVICE = {intent: intent[len("find_"):] for intent in INTENTS}        # the frame's `service`
PAIRS = tuple((x, y) for x in INTENTS for y in INTENTS if x != y)        # the 12 ordered questions

DESCRIPTIONS = {
    "find_hotel": "the customer is looking for a hotel to stay at",
    "find_restaurant": "the customer is looking for a restaurant to eat at",
    "find_train": "the customer is looking for a train",
    "find_attraction": "the customer wants a tourist attraction to visit",
}

KEYWORDS = {
    "hotel": ("hotel", "guesthouse", "guest house", "stay", "accommodation", "lodging"),
    "restaurant": ("restaurant", "food", "eat", "dine", "dinner", "lunch"),
    "train": ("train", "trains", "depart", "departing", "arrive", "arriving"),
    "attraction": ("attraction", "museum", "college", "park", "theatre", "cinema", "entertainment",
                   "church", "pool", "boat"),
}

THETA_PERCENTILES = tuple(range(50, 100))      # p50..p99 in steps of 1
BOOTSTRAP_RESAMPLES = 10_000
BOOTSTRAP_SEED = 20260929
CI_PERCENTILES = (2.5, 97.5)                   # 95% percentile interval

# ── The experiment's fixed shapes (spec "Data and ingest", "The three arms", "Phases") ───────

TYPE_NAME = "DialogueTurn"
SPLITS = ("dev", "test")
LIMIT = 10_000                                 # every call; at or below the default MaxOutputRows
READINESS_TEXT = "a customer message"          # fixed, and not one of the four descriptions
PROBE_T_THETA = 0.5                            # the placeholder theta, for both intents
PROBE_T_PAIR = PAIRS[0]                        # find_hotel -> find_restaurant
SERVER_TIMEOUT_SECONDS = 30.0                  # PatternQueryLimitOptions.TimeoutSeconds default
READINESS_DEADLINE_SECONDS = 3600.0
READINESS_POLL_SECONDS = 60.0
ORDERED_PATTERN = "A Z* B"
ORDER_FREE_PATTERN = "(A Z* B | B Z* A)"
ARMS = ("ordered_semantic", "order_free_semantic", "ordered_keyword")

# ── Identity and authorization (spec "Registration", Reregistrar.cs:106-114) ────────────────

OWNER_FIELD = "OwnerId"
BYPASS_ROLE = "iverson-loadtest-bypass"
BYPASS_TENANT = "tenant_bypass"

# ── The dataset (spec "Data and ingest", Verified assumptions 1) ────────────────────────────

DATA_BASE_URL = "https://raw.githubusercontent.com/budzianowski/multiwoz/master/data/MultiWOZ_2.2"
DATA_FILES = (                                  # (split, file name, md5), in load order
    ("dev", "dialogues_001.json", "ee1809dcf412ccba0a47d7c2db2d361e"),
    ("dev", "dialogues_002.json", "295eaea9b341b3e21589e4b97c7ca335"),
    ("test", "dialogues_001.json", "e37f05c2800286768d273aaf4a8e85a4"),
    ("test", "dialogues_002.json", "2aa1b12f2cf210a7b466ef260f397e32"),
)


@iverson_entity
class DialogueTurn:
    """One MultiWOZ USER turn (spec "Data and ingest"), shaped like PatternDoc
    (Iverson.Clients/Python/conformance/models.py). The key is server-generated: it stays None, so
    _entity_to_struct omits it and persist() returns the key the server assigned. The four flags
    are ints, not bools, because a define must evaluate to a boolean and a StarRocks BOOLEAN may
    arrive as a number (spec Verified assumptions 7); a define reads them as `MentionsTrain = 1`."""
    id: uuid.UUID = iverson_key()
    tenant_id: str = None
    owner_id: str = None
    dialogue_id: str = None                      # PARTITION BY DialogueId
    turn_index: int = None                       # ORDER BY TurnIndex
    split: str = None                            # where Split = 'dev' | 'test'
    utterance: str = iverson_embedding()         # SIMILARITY(Utterance, ...) -> utterance_vector
    mentions_hotel: int = None
    mentions_restaurant: int = None
    mentions_train: int = None
    mentions_attraction: int = None


assert DialogueTurn._iverson_meta["type_name"] == TYPE_NAME

FLAG_FIELDS = {domain: f"mentions_{domain}" for domain in KEYWORDS}         # Python member
FLAG_COLUMNS = {domain: f"Mentions{domain.capitalize()}" for domain in KEYWORDS}   # wire column


def say(message):
    print(f"[dialogue_patterns] {message}", flush=True)


def fail(message):
    sys.exit(f"[dialogue_patterns] {message}")


# ── Ground truth (pure; spec "Ground truth and the questions") ───────────────────────────────

def user_turns(dialogue):
    """The dialogue's USER turns in order; a turn's TurnIndex is its position in this list."""
    return [turn for turn in dialogue["turns"] if turn["speaker"] == "USER"]


def _frame(turn, service):
    for frame in turn["frames"]:
        if frame["service"] == service:
            return frame
    return None


def onsets(dialogue):
    """{intent: TurnIndex} of each find intent's onset: the first user turn whose frame for the
    intent's service has active_intent == the intent AND a non-empty slot_values or
    requested_slots. An empty-state frame is never an onset: MultiWOZ stamps a dialogue's other
    services with their intent before the customer mentions them. A dialogue with no non-empty
    find frame for an intent has no onset for it."""
    found = {}
    for index, turn in enumerate(user_turns(dialogue)):
        for intent in INTENTS:
            if intent in found:
                continue
            frame = _frame(turn, SERVICE[intent])
            if frame is None:
                continue
            state = frame["state"]
            if state["active_intent"] == intent and (state["slot_values"] or state["requested_slots"]):
                found[intent] = index
    return found


def gold_positive(onset, x, y):
    """"X then Y": both have an onset and onset(X) < onset(Y). A same-turn tie is negative both ways."""
    return x in onset and y in onset and onset[x] < onset[y]


def gold_labels(onsets_by_dialogue, dialogue_ids):
    """{(x, y): [bool per dialogue, in dialogue_ids order]} for all 12 pairs."""
    return {(x, y): [gold_positive(onsets_by_dialogue[d], x, y) for d in dialogue_ids] for x, y in PAIRS}


def single_intent_gold(onsets_by_dialogue, dialogue_ids):
    """{intent: [bool per dialogue]}: the intent has an onset (spec "theta calibration")."""
    return {intent: [intent in onsets_by_dialogue[d] for d in dialogue_ids] for intent in INTENTS}


def tie_count(onsets_by_dialogue):
    """(dialogue, unordered intent pair) cases whose two onsets fall on the same turn."""
    return sum(1 for o in onsets_by_dialogue.values()
               for i, x in enumerate(INTENTS) for y in INTENTS[i + 1:]
               if x in o and y in o and o[x] == o[y])


# ── Keyword flags (pure; spec "Pre-registered constants") ───────────────────────────────────

KEYWORD_PATTERNS = {
    domain: re.compile(r"\b(?:" + "|".join(re.escape(word) for word in words) + r")\b", re.IGNORECASE)
    for domain, words in KEYWORDS.items()
}


def keyword_flags(utterance):
    """{mentions_<domain>: 0 | 1}: 1 when any of the domain's keywords occurs as a whole word
    (case-insensitive). "guest house" is matched as the two-word phrase."""
    return {FLAG_FIELDS[domain]: int(bool(pattern.search(utterance)))
            for domain, pattern in KEYWORD_PATTERNS.items()}


def build_turns(dialogues_by_split):
    """Every USER turn of both splits as a row record, dev before test and file order within a
    split. A blank utterance or a DialogueId seen twice aborts: the write path skips blank text
    (IntelligenceStoreConsumer), so readiness would stall on it, and a repeated id would merge two
    dialogues into one partition."""
    records, seen = [], set()
    for split in SPLITS:
        for dialogue in dialogues_by_split[split]:
            dialogue_id = dialogue["dialogue_id"]
            if dialogue_id in seen:
                fail(f"dialogue id {dialogue_id!r} appears twice across the splits")
            seen.add(dialogue_id)
            for index, turn in enumerate(user_turns(dialogue)):
                utterance = turn["utterance"]
                if not utterance.strip():
                    fail(f"{split} dialogue {dialogue_id} turn {index} has a blank utterance")
                records.append({"dialogue_id": dialogue_id, "turn_index": index, "split": split,
                                "utterance": utterance, **keyword_flags(utterance)})
    return records


def make_entity(record, tenant_id, owner_id):
    entity = DialogueTurn()
    entity.tenant_id = tenant_id
    entity.owner_id = owner_id
    for name, value in record.items():
        setattr(entity, name, value)
    return entity


# ── Requests (pure) ─────────────────────────────────────────────────────────────────────────

def similarity_define(intent, theta):
    return f"{pl.similarity('Utterance', DESCRIPTIONS[intent])} > {pl.format_theta(theta)}"


def keyword_define(intent):
    return f"{FLAG_COLUMNS[SERVICE[intent]]} = 1"


def _split_rows(split):
    """TYPE_ROWS over one split: partition_by DialogueId, order_by TurnIndex, where Split = split."""
    return (match_pattern(TYPE_NAME).where("Split", pb.EQUALS, split)
            .partition_by("DialogueId").order_by("TurnIndex"))


def all_rows_request(split, text):
    """The calibration shape: A+, A AS TRUE, ALL ROWS, s = SIMILARITY(Utterance, text). A+ over
    TRUE matches each whole partition once, so the call emits one row per turn of the split."""
    return (_split_rows(split).pattern("A+").define("A", "TRUE")
            .measure("s", pl.similarity("Utterance", text))
            .rows_per_match(pb.ALL_ROWS_SHOW_EMPTY).limit(LIMIT).build())


def readiness_request(split):
    return all_rows_request(split, READINESS_TEXT)


def calibration_request(split, intent):
    """Phase 1 on dev; on test it is the Phase 2 single-intent call."""
    return all_rows_request(split, DESCRIPTIONS[intent])


def probe_s_request(dialogue_id):
    return (match_pattern(TYPE_NAME).where("DialogueId", pb.EQUALS, dialogue_id)
            .partition_by("DialogueId").order_by("TurnIndex")
            .pattern("A").define("A", "TurnIndex = 0")
            .rows_per_match(pb.ONE_ROW).limit(LIMIT).build())


def arm_request(arm, split, x, y, theta):
    """One pair's call for one arm; ONE ROW, the default AFTER MATCH SKIP PAST LAST ROW. `theta`
    is {intent: theta} (unused by the keyword arm)."""
    if arm == "ordered_keyword":
        pattern, a, b = ORDERED_PATTERN, keyword_define(x), keyword_define(y)
    else:
        pattern = ORDERED_PATTERN if arm == "ordered_semantic" else ORDER_FREE_PATTERN
        a, b = similarity_define(x, theta[x]), similarity_define(y, theta[y])
    return (_split_rows(split).pattern(pattern).define("A", a).define("B", b).define("Z", "TRUE")
            .rows_per_match(pb.ONE_ROW).limit(LIMIT).build())


def probe_t_request():
    x, y = PROBE_T_PAIR
    return arm_request("ordered_semantic", "dev", x, y, {x: PROBE_T_THETA, y: PROBE_T_THETA})


# ── Result parsing (pure) ───────────────────────────────────────────────────────────────────

def matched_dialogues(rows, written_ids, what):
    """ONE ROW: the set of DialogueIds with at least one match. Each must be a written dialogue of
    the split asked for (spec "Integrity")."""
    matched = set()
    for row in rows:
        dialogue_id = pl.field(row, "DialogueId", what)
        if dialogue_id not in written_ids:
            fail(f"{what}: matched DialogueId {dialogue_id!r} is not a written dialogue of this split")
        matched.add(dialogue_id)
    return matched


def turn_scores(rows, what):
    """ALL ROWS: {(DialogueId, TurnIndex): s or None}. TurnIndex arrives as a proto number (a
    float); a fractional one or a repeated turn aborts (a repeat is a double ingest)."""
    scores = {}
    for row in rows:
        turn = (pl.field(row, "DialogueId", what),
                pl.whole_number(pl.field(row, "TurnIndex", what), "TurnIndex", what, 0))
        if turn in scores:
            fail(f"{what}: turn {turn} appears twice -- the split was written more than once?")
        scores[turn] = pl.field(row, "s", what)
    return scores


def coverage_problem(scores, expected_turns):
    """None when `scores` covers exactly `expected_turns` with a non-NULL s; else (fatal, message).
    Missing turns and NULL scores are not fatal (the write path is asynchronous, so readiness waits
    for them); a turn that was never written is fatal."""
    unknown = set(scores) - expected_turns
    if unknown:
        return True, f"{len(unknown)} row(s) for turns that were never written, e.g. {sorted(unknown)[:3]}"
    missing = expected_turns - set(scores)
    if missing:
        return False, f"{len(scores)} of {len(expected_turns)} turns visible, e.g. missing {sorted(missing)[:3]}"
    nulls = sorted(t for t, s in scores.items() if s is None)
    if nulls:
        return False, f"{len(nulls)} turn(s) score NULL (not embedded yet), e.g. {nulls[:3]}"
    return None


def strict_turn_scores(rows, expected_turns, what):
    """{(DialogueId, TurnIndex): s} for a calibration or single-intent call, which must cover
    every written turn of the split with a finite s: anything else aborts."""
    scores = turn_scores(rows, what)
    problem = coverage_problem(scores, expected_turns)
    if problem is not None:
        fail(f"{what}: {problem[1]}")
    return {turn: pl.finite_number(s, "s", what) for turn, s in scores.items()}


# ── Metrics (pure) ──────────────────────────────────────────────────────────────────────────

def f1_from_counts(tp, fp, fn):
    """2tp / (2tp + fp + fn), 0 when undefined; equal counts give the identical float, which the
    theta tie rule relies on (2PR/(P+R) can round two equal F1s apart)."""
    denominator = 2 * tp + fp + fn
    return 2 * tp / denominator if denominator else 0.0


def prf(predicted, gold):
    tp = sum(1 for p, g in zip(predicted, gold) if p and g)
    fp = sum(1 for p, g in zip(predicted, gold) if p and not g)
    fn = sum(1 for p, g in zip(predicted, gold) if g and not p)
    return {"tp": tp, "fp": fp, "fn": fn,
            "precision": tp / (tp + fp) if tp + fp else 0.0,
            "recall": tp / (tp + fn) if tp + fn else 0.0,
            "f1": f1_from_counts(tp, fp, fn)}


def macro_f1(f1s):
    f1s = list(f1s)
    return sum(f1s) / len(f1s)


def dialogue_max(scores):
    """{DialogueId: max per-turn s}."""
    best = {}
    for (dialogue_id, _), s in scores.items():
        if dialogue_id not in best or s > best[dialogue_id]:
            best[dialogue_id] = s
    return best


def single_intent_predictions(scores, theta, dialogue_ids):
    """[bool per dialogue]: some user turn has s > theta."""
    best = dialogue_max(scores)
    return [d in best and best[d] > theta for d in dialogue_ids]


def theta_grid(scores, gold, dialogue_ids):
    """Choose one intent's theta (spec "theta calibration"): candidates are the p50..p99
    percentiles (numpy's linear interpolation) of the intent's per-turn s; each is scored by
    dialogue-level single-intent F1 against `gold`; the highest F1 wins and a tie goes to the lower
    theta. Returns {"theta", "percentile", "f1", "at_grid_edge", "grid": [[p, theta, f1], ...]}."""
    values = np.asarray(list(scores.values()), dtype=np.float64)
    if values.size < 2:
        fail(f"theta needs at least 2 per-turn scores, got {values.size}")
    grid = []
    for p, theta in zip(THETA_PERCENTILES, np.percentile(values, THETA_PERCENTILES)):
        theta = float(theta)
        grid.append([p, theta, prf(single_intent_predictions(scores, theta, dialogue_ids), gold)["f1"]])
    p, theta, f1 = max(grid, key=lambda g: (g[2], -g[1], -g[0]))
    return {"theta": theta, "percentile": p, "f1": f1,
            "at_grid_edge": p in (THETA_PERCENTILES[0], THETA_PERCENTILES[-1]), "grid": grid}


# ── Bootstrap and gate (pure) ───────────────────────────────────────────────────────────────

def bootstrap_macro_f1(predictions, gold, n_resamples=BOOTSTRAP_RESAMPLES, seed=BOOTSTRAP_SEED):
    """Paired bootstrap over test dialogues: each resample draws len(dialogues) dialogues with
    replacement (one draw shared by every arm, which is what makes a difference paired) and
    recomputes every arm's macro-F1 over the 12 pairs. `predictions` is {arm: {pair: [bool per
    dialogue]}}, `gold` {pair: [bool per dialogue]}. Returns {arm: np.ndarray of n_resamples}."""
    arms = list(predictions)
    g = np.array([gold[pair] for pair in PAIRS], dtype=np.float64).T                  # (n, P)
    p = np.stack([np.array([predictions[arm][pair] for pair in PAIRS], dtype=np.float64).T
                  for arm in arms], axis=1)                                             # (n, A, P)
    n = g.shape[0]
    tp = (p * g[:, None, :]).reshape(n, -1)
    fp = (p * (1.0 - g[:, None, :])).reshape(n, -1)
    fn = ((1.0 - p) * g[:, None, :]).reshape(n, -1)
    rng = np.random.default_rng(seed)
    draws = np.empty((n_resamples, len(arms)), dtype=np.float64)
    for b in range(n_resamples):
        weights = np.bincount(rng.integers(0, n, size=n), minlength=n).astype(np.float64)
        denominator = 2.0 * (weights @ tp) + weights @ fp + weights @ fn
        f1 = np.divide(2.0 * (weights @ tp), denominator, out=np.zeros_like(denominator), where=denominator > 0)
        draws[b] = f1.reshape(len(arms), len(PAIRS)).mean(axis=1)
    return {arm: draws[:, i] for i, arm in enumerate(arms)}


def percentile_ci(differences):
    lo, hi = np.percentile(np.asarray(differences, dtype=np.float64), CI_PERCENTILES)
    return [float(lo), float(hi)]


def gate(ci):
    """GO when the 95% CI of ordered minus order-free macro-F1 lies entirely above 0."""
    return "GO" if ci[0] > 0 else "NO-GO"


# ── Data (download, md5) ────────────────────────────────────────────────────────────────────

def md5_of(path):
    digest = hashlib.md5()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def data_path(data_dir, split, name):
    return os.path.join(data_dir, split, name)


def ensure_data(data_dir, urlopen=urllib.request.urlopen):
    """The four pinned files under data_dir/<split>/. A present file must already match its md5
    (a mismatch aborts; it is never silently replaced); a missing one is downloaded to a .part
    file, md5-checked, and only then renamed into place. Returns {path: md5}."""
    md5s = {}
    for split, name, expected in DATA_FILES:
        path = data_path(data_dir, split, name)
        if not os.path.exists(path):
            os.makedirs(os.path.dirname(path), exist_ok=True)
            url = f"{DATA_BASE_URL}/{split}/{name}"
            say(f"downloading {url}")
            try:
                with urlopen(url, timeout=120) as response, open(path + ".part", "wb") as f:
                    f.write(response.read())
            except Exception as e:  # noqa: BLE001 -- every failure means "the pinned file is unavailable"
                fail(f"downloading {url} failed: {type(e).__name__}: {e}")
            got = md5_of(path + ".part")
            if got != expected:
                fail(f"{url}: md5 {got} != pinned {expected} (left at {path}.part) -- aborting")
            os.replace(path + ".part", path)
        got = md5_of(path)
        if got != expected:
            fail(f"{path}: md5 {got} != pinned {expected} -- aborting")
        md5s[f"{split}/{name}"] = got
    return md5s


def load_splits(data_dir):
    splits = {split: [] for split in SPLITS}
    for split, name, _ in DATA_FILES:
        with open(data_path(data_dir, split, name), encoding="utf-8") as f:
            splits[split].extend(json.load(f))
    return splits


# ── Identity and registration ───────────────────────────────────────────────────────────────

def jwt_claims(token):
    """The unverified payload of a JWT (the server verifies it; this only reads who it names)."""
    try:
        payload = token.split(".")[1]
        return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    except Exception as e:  # noqa: BLE001
        fail(f"the acting-user token is not a readable JWT: {type(e).__name__}")


def acting_identity(claims):
    """(tenant_id, sub) of the bypass acting user; any other identity aborts. The server force-sets
    the tenant column from the token and leaves the owner field alone for a bypass caller
    (AuthorizationFieldMasking), so the row's TenantId/OwnerId are stamped from these claims, as
    the conformance driver stamps --tenant/--owner-id."""
    groups = claims.get("groups") or []
    groups = [groups] if isinstance(groups, str) else list(groups)
    if claims.get("tenant_id") != BYPASS_TENANT or BYPASS_ROLE not in groups or not claims.get("sub"):
        fail(f"the acting user must carry tenant_id {BYPASS_TENANT!r}, group {BYPASS_ROLE!r} and a sub; got "
             f"tenant_id {claims.get('tenant_id')!r}, groups {groups}")
    return claims["tenant_id"], claims["sub"]


def authorization_rules():
    """The conformance harness's rules (Reregistrar.cs:106-114). Without them the type is denied,
    and a denied MatchPattern is an empty stream, not an error (spec Verified assumptions 6)."""
    return mapping_pb.AuthorizationRules(
        owner_field=OWNER_FIELD,
        row_permissions=[mapping_pb.RowPermission(role=BYPASS_ROLE, can_read_all=True, can_write_all=True,
                                                  can_delete_all=True)])


def register(mapping_stub):
    try:
        SchemaRegistrar(mapping_stub, DialogueTurn).register_all(
            authorization_by_type_name={TYPE_NAME: authorization_rules()})
    except Exception as e:  # noqa: BLE001 -- RuntimeError (success=false) or a gRPC error
        fail(f"registering {TYPE_NAME} failed: {type(e).__name__}: {e}")


# ── Files ───────────────────────────────────────────────────────────────────────────────────

def out_path(out, name):
    return os.path.join(out, name)


def read_json(path, what):
    if not os.path.exists(path):
        fail(f"{path} is missing -- run {what} first")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def refuse_existing(path, why):
    if os.path.exists(path):
        fail(f"{path} already exists: {why}")


def written_turns(ingest, split):
    """{(DialogueId, TurnIndex)} written for `split`, from ingest.json."""
    return {(d, i) for d, s in ingest["dialogues"].items() if s == split for i in range(len(ingest["keys"][d]))}


def written_dialogues(ingest, split):
    return {d for d, s in ingest["dialogues"].items() if s == split}


def split_ids(dialogues):
    return [d["dialogue_id"] for d in dialogues]


def check_ingest_matches_data(ingest, splits):
    """ingest.json must describe exactly the md5-checked data's user turns."""
    for split in SPLITS:
        for dialogue in splits[split]:
            d = dialogue["dialogue_id"]
            if ingest["dialogues"].get(d) != split or len(ingest["keys"].get(d, ())) != len(user_turns(dialogue)):
                fail(f"ingest.json does not match the data for {split} dialogue {d}")
    if len(ingest["dialogues"]) != sum(len(v) for v in splits.values()):
        fail("ingest.json records dialogues that are not in the data")


# ── Live steps ──────────────────────────────────────────────────────────────────────────────

class Live:
    """Everything a subcommand touches outside this process. Tests substitute fakes for all of it."""

    def __init__(self, out, coordinator, fetch_build, build_url, mapping_stub=None, acting_token=None,
                 clock=time.monotonic, sleep=time.sleep,
                 readiness_deadline=READINESS_DEADLINE_SECONDS, readiness_poll=READINESS_POLL_SECONDS):
        self.out = out
        self.coordinator = coordinator
        self.fetch_build = fetch_build
        self.build_url = build_url
        self.mapping_stub = mapping_stub
        self.acting_token = acting_token
        self.clock = clock
        self.sleep = sleep
        self.readiness_deadline = readiness_deadline
        self.readiness_poll = readiness_poll


def timed_call(live, request, what, calls):
    """pl.execute (aborts on a gRPC error or a count at limit), timed and logged into `calls`."""
    start = live.clock()
    rows = pl.execute(live.coordinator, request, what)
    seconds = live.clock() - start
    calls.append({"what": what, "rows": len(rows), "seconds": seconds})
    return rows


def wait_until_ready(live, split, expected_turns):
    """Spec "Readiness check": one ALL ROWS call over the split measuring
    s = SIMILARITY(Utterance, 'a customer message'); it passes when its rows cover exactly the
    written turns with every s non-NULL. Missing or NULL turns are waited for (the write path
    embeds asynchronously) until the deadline, then abort; a row for a turn never written aborts
    at once, since waiting cannot fix it."""
    start = live.clock()
    attempts = 0
    while True:
        attempts += 1
        what = f"readiness {split} attempt {attempts}"
        rows = pl.execute(live.coordinator, readiness_request(split), what)
        problem = coverage_problem(turn_scores(rows, what), expected_turns)
        elapsed = live.clock() - start
        if problem is None:
            say(f"readiness {split}: {len(rows)} rows, every s non-NULL, after {attempts} attempt(s), {elapsed:.0f}s")
            return {"split": split, "rows": len(rows), "attempts": attempts, "seconds": elapsed}
        fatal, message = problem
        if fatal:
            fail(f"{what}: {message}")
        if elapsed >= live.readiness_deadline:
            fail(f"readiness {split} TIMED OUT after {elapsed:.0f}s ({attempts} attempts): {message} -- aborting")
        say(f"{what}: not ready: {message}; retrying in {live.readiness_poll:.0f}s")
        live.sleep(live.readiness_poll)


def checked(live, expected, name, body):
    """pl.run_checked_pass: /build before, the body, /build after, both against `expected`."""
    return pl.run_checked_pass(live.fetch_build, live.build_url, expected, name, body)


def cmd_ingest(live, splits, md5s):
    ingest_path = out_path(live.out, "ingest.json")
    progress_path = out_path(live.out, "ingest-progress.jsonl")
    refuse_existing(ingest_path, "ingest has already run; a second ingest would write every turn twice")
    refuse_existing(progress_path, "an earlier ingest wrote rows and did not finish; its keys are listed there -- "
                                   "delete those rows and the file before ingesting again")
    composite = live.fetch_build(live.build_url)
    tenant_id, owner_id = acting_identity(jwt_claims(live.acting_token))
    records = build_turns(splits)

    def write_all():
        register(live.mapping_stub)
        say(f"registered {TYPE_NAME}; writing {len(records)} turns")
        keys = {}
        with open(progress_path, "w", encoding="utf-8") as progress:
            for n, record in enumerate(records, start=1):
                try:
                    key = live.coordinator.persist(make_entity(record, tenant_id, owner_id))
                except Exception as e:  # noqa: BLE001 -- a gRPC error or success=false
                    fail(f"persist failed at turn {n} of {len(records)} ({record['dialogue_id']} "
                         f"{record['turn_index']}): {type(e).__name__}: {e}; keys so far are in {progress_path}")
                if not key:
                    fail(f"persist returned no key at turn {n} ({record['dialogue_id']} {record['turn_index']})")
                keys.setdefault(record["dialogue_id"], []).append(key)
                progress.write(json.dumps({"dialogue_id": record["dialogue_id"], "turn_index": record["turn_index"],
                                           "split": record["split"], "key": key}) + "\n")
                if n % 1000 == 0 or n == len(records):
                    progress.flush()
                    say(f"wrote {n}/{len(records)} turns")
        return keys

    keys, write_check = checked(live, composite, "ingest write", write_all)
    ingest = {
        "composite": composite, "type_name": TYPE_NAME, "tenant_id": tenant_id, "owner_id": owner_id,
        "md5s": md5s, "counts": {s: sum(1 for r in records if r["split"] == s) for s in SPLITS},
        "first_test_dialogue": splits["test"][0]["dialogue_id"],
        "dialogues": {d["dialogue_id"]: s for s in SPLITS for d in splits[s]},
        "keys": keys, "readiness": None, "build_checks": [write_check],
    }
    pl.write_json(ingest_path, ingest)
    os.remove(progress_path)
    say(f"wrote {ingest_path}: {ingest['counts']}")
    record_readiness(live, ingest, ingest_path)


def record_readiness(live, ingest, ingest_path):
    readiness, check = checked(live, ingest["composite"], "readiness",
                               lambda: [wait_until_ready(live, s, written_turns(ingest, s)) for s in SPLITS])
    ingest["readiness"] = readiness
    ingest["build_checks"].append(check)
    pl.write_json(ingest_path, ingest)


def cmd_ready(live, splits):
    ingest_path = out_path(live.out, "ingest.json")
    ingest = read_json(ingest_path, "ingest")
    check_ingest_matches_data(ingest, splits)
    record_readiness(live, ingest, ingest_path)


def load_ready_ingest(live, splits):
    ingest = read_json(out_path(live.out, "ingest.json"), "ingest")
    if not ingest.get("readiness"):
        fail("ingest.json records no passed readiness check -- run ready first")
    check_ingest_matches_data(ingest, splits)
    return ingest


def cmd_probe_s(live, splits):
    ingest = load_ready_ingest(live, splits)
    dialogue_id = ingest["first_test_dialogue"]
    calls = []

    def body():
        rows = timed_call(live, probe_s_request(dialogue_id), "probe S", calls)
        if len(rows) != 1 or pl.field(rows[0], "DialogueId", "probe S") != dialogue_id:
            fail(f"probe S FAIL: expected exactly one row for {dialogue_id!r}, got "
                 f"{[r.data for r in rows][:5]} ({len(rows)} row(s))")
        return rows[0].data

    row, check = checked(live, ingest["composite"], "probe-s", body)
    pl.write_json(out_path(live.out, "probe-s.json"),
                  {"dialogue_id": dialogue_id, "row": row, "calls": calls, "build": check})
    say(f"probe S PASS: one row for {dialogue_id}")


def cmd_probe_t(live, splits):
    ingest = load_ready_ingest(live, splits)
    request = probe_t_request()
    calls = []

    def body():
        wait_until_ready(live, "dev", written_turns(ingest, "dev"))
        rows = timed_call(live, request, "probe T", calls)
        return matched_dialogues(rows, written_dialogues(ingest, "dev"), "probe T")

    matched, check = checked(live, ingest["composite"], "probe-t", body)
    seconds = calls[0]["seconds"]
    result = {"pair": list(PROBE_T_PAIR), "placeholder_theta": PROBE_T_THETA, "split": "dev",
              "rows_in_split": ingest["counts"]["dev"], "seconds": seconds, "matched_dialogues": len(matched),
              "pattern": request.pattern, "define": [[d.name, d.expr] for d in request.define],
              "limit": request.limit, "passed": seconds < SERVER_TIMEOUT_SECONDS, "calls": calls, "build": check}
    pl.write_json(out_path(live.out, "probe-t.json"), result)      # written on STOP too: the timing is the finding
    if not result["passed"]:
        fail(f"probe T STOP: the call took {seconds:.1f}s, not under the {SERVER_TIMEOUT_SECONDS:.0f}s timeout -- "
             "return to the user; never raise a limit to make it fit")
    say(f"probe T PASS: {seconds:.2f}s, {len(matched)} dev dialogue(s) matched")


def cmd_calibrate(live, splits):
    theta_path = out_path(live.out, "theta.json")
    refuse_existing(theta_path, "theta is frozen once written (spec: never edited afterwards)")
    ingest = load_ready_ingest(live, splits)
    dev = splits["dev"]
    dialogue_ids = split_ids(dev)
    gold = single_intent_gold({d["dialogue_id"]: onsets(d) for d in dev}, dialogue_ids)
    expected = written_turns(ingest, "dev")
    calls = []

    def body():
        wait_until_ready(live, "dev", expected)
        return {intent: strict_turn_scores(timed_call(live, calibration_request("dev", intent),
                                                      f"calibration {intent}", calls), expected,
                                           f"calibration {intent}")
                for intent in INTENTS}

    scores, check = checked(live, ingest["composite"], "calibrate", body)
    thetas = {intent: theta_grid(scores[intent], gold[intent], dialogue_ids) for intent in INTENTS}
    pl.write_json(theta_path, {"theta": {i: thetas[i]["theta"] for i in INTENTS}, "by_intent": thetas,
                               "descriptions": DESCRIPTIONS, "calls": calls, "build": check})
    for intent in INTENTS:
        t = thetas[intent]
        say(f"theta {intent}: p{t['percentile']} = {t['theta']!r}, dev F1 {t['f1']:.4f}"
            + ("  (AT THE GRID EDGE)" if t["at_grid_edge"] else ""))


def score_results(splits, arm_matches, single_scores, theta):
    """Everything results.json holds except the call log and /build record, from the parsed
    responses: `arm_matches` {arm: {pair: set of DialogueIds}}, `single_scores` {intent:
    {(DialogueId, TurnIndex): s}}, `theta` {intent: theta}."""
    test = splits["test"]
    dialogue_ids = split_ids(test)
    by_dialogue = {d["dialogue_id"]: onsets(d) for d in test}
    gold = gold_labels(by_dialogue, dialogue_ids)
    predictions = {arm: {pair: [d in arm_matches[arm][pair] for d in dialogue_ids] for pair in PAIRS}
                   for arm in ARMS}
    pairs = []
    for x, y in PAIRS:
        pairs.append({"x": x, "y": y, "gold_positive": sum(gold[(x, y)]), "reversed_negative": sum(gold[(y, x)]),
                      "arms": {arm: {**prf(predictions[arm][(x, y)], gold[(x, y)]),
                                     "predicted": sum(predictions[arm][(x, y)])} for arm in ARMS}})
    macro = {arm: macro_f1(p["arms"][arm]["f1"] for p in pairs) for arm in ARMS}
    draws = bootstrap_macro_f1(predictions, gold)
    comparisons = {}
    for name, other in (("ordered_minus_order_free", "order_free_semantic"),
                        ("ordered_minus_keyword", "ordered_keyword")):
        comparisons[name] = {"delta": macro["ordered_semantic"] - macro[other],
                             "ci95": percentile_ci(draws["ordered_semantic"] - draws[other])}
    single_gold = single_intent_gold(by_dialogue, dialogue_ids)
    single = {intent: {**prf(single_intent_predictions(single_scores[intent], theta[intent], dialogue_ids),
                             single_gold[intent]), "gold_positive": sum(single_gold[intent])}
              for intent in INTENTS}
    return {"theta": theta, "dialogues": len(dialogue_ids), "ties": tie_count(by_dialogue), "pairs": pairs,
            "macro_f1": macro, "bootstrap": {"resamples": BOOTSTRAP_RESAMPLES, "seed": BOOTSTRAP_SEED,
                                              "ci_percentiles": list(CI_PERCENTILES), **comparisons},
            "gate": gate(comparisons["ordered_minus_order_free"]["ci95"]), "single_intent": single}


def cmd_score(live, splits):
    results_path = out_path(live.out, "results.json")
    refuse_existing(results_path, "the test split is scored once")
    theta = read_json(out_path(live.out, "theta.json"), "calibrate")["theta"]
    if set(theta) != set(INTENTS):
        fail(f"theta.json holds {sorted(theta)}, not the four intents")
    ingest = load_ready_ingest(live, splits)
    expected = written_turns(ingest, "test")
    written = written_dialogues(ingest, "test")
    calls = []

    def body():
        wait_until_ready(live, "test", expected)
        matches = {arm: {} for arm in ARMS}
        for arm in ARMS:
            for x, y in PAIRS:
                what = f"{arm} {x} -> {y}"
                matches[arm][(x, y)] = matched_dialogues(
                    timed_call(live, arm_request(arm, "test", x, y, theta), what, calls), written, what)
        single = {intent: strict_turn_scores(timed_call(live, calibration_request("test", intent),
                                                        f"single-intent {intent}", calls), expected,
                                             f"single-intent {intent}")
                  for intent in INTENTS}
        return matches, single

    (matches, single), check = checked(live, ingest["composite"], "score", body)
    results = {**score_results(splits, matches, single, theta), "calls": calls, "build": check}
    pl.write_json(results_path, results)
    b = results["bootstrap"]
    say(f"macro-F1 {results['macro_f1']}")
    say(f"ordered - order-free {b['ordered_minus_order_free']['delta']:+.4f} CI {b['ordered_minus_order_free']['ci95']}; "
        f"ordered - keyword {b['ordered_minus_keyword']['delta']:+.4f} CI {b['ordered_minus_keyword']['ci95']}")
    say(f"GATE: {results['gate']}")


# ── Wiring ──────────────────────────────────────────────────────────────────────────────────

COMMANDS = {"ingest": cmd_ingest, "ready": cmd_ready, "probe-s": cmd_probe_s, "probe-t": cmd_probe_t,
            "calibrate": cmd_calibrate, "score": cmd_score}


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in COMMANDS:
        p = sub.add_parser(name)
        p.add_argument("--out", required=True, help="the run's OUT directory (data under OUT/data/)")
        p.add_argument("--build-url", default=os.environ.get("IVERSON_HTTP_URL", "http://localhost:8081") + "/build")
        p.add_argument("--username", default=pl.BYPASS_USERNAME, help="the acting user to mint a token for")
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    os.makedirs(args.out, exist_ok=True)
    md5s = ensure_data(os.path.join(args.out, "data"))
    splits = load_splits(os.path.join(args.out, "data"))
    session = pl.TokenSession(lambda: pl.mint_acting_user_token(args.username), pl.mint_service_token_from_env)
    channel, _ = pl.connect(session)          # pattern_leg's channel; its BenchmarkDocument coordinator is unused
    try:
        live = Live(args.out, EntityCoordinator(DialogueTurn, channel), pl.fetch_build, args.build_url,
                    mapping_stub=mapping_grpc.ObjectMappingServiceStub(channel),
                    acting_token=session.acting_user_token())
        if args.cmd == "ingest":
            cmd_ingest(live, splits, md5s)
        else:
            COMMANDS[args.cmd](live, splits)
    finally:
        channel.close()


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run the tests and the whole scripts suite**

Run: `PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py -q`
Expected: `109 passed`. No network is used.

Run: `QDRANT__SERVICE__API_KEY=unused-by-tests PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts -q`
Expected: `577 passed` (468 before this task).

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py
git commit -m "add the matchpattern dialogue-sequence use-case harness" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 2: Live run, part 1: state file, ingest, Probes S and T

This task writes one new type and 14,746 rows into `tenant_bypass` on the shared local stack. **The controller asks the user before dispatching it.**

**Files:**
- Create, outside the repository: the state file and the `OUT` directory.
- Nothing is committed.

**Interfaces:**
- Consumes Task 1's script.
- Produces, for Task 3:
  - `OUT/ingest.json`;
  - `OUT/probe-s.json` and `OUT/probe-t.json`;
  - `OUT/phase0.md`;
  - the state file.

Each step runs in a fresh shell, so nothing a step sets survives to the next one. The setup below writes every shared variable and credential export to one state file, and **every later bash block begins by sourcing it**. The file holds commands, never secret values: both secrets are re-read from `.env` each time it is sourced.

Run the setup once, from the root of the worktree the tasks execute in. The inner heredoc's terminator is `STATE_EOF`, so no line of it can end an outer heredoc.
```bash
STATE=/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
printf 'cd %q\nDATE=%q\n' "$(pwd)" "$(date +%F)" > "$STATE"
cat >> "$STATE" <<'STATE_EOF'
OUT=/home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-$DATE
ENVF=/home/ben/repositories/Iverson/Iverson.Server/.env
DP=Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
source /home/ben/iverson-benchmark-data/bench-env.sh
export IVERSON_CLIENT_SECRET="$(command grep '^IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET=' "$ENVF" | cut -d= -f2-)"
export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(command grep '^IVERSON_BYPASS_PASSWORD=' "$ENVF" | cut -d= -f2-)"
STATE_EOF
. "$STATE"
mkdir -p "$OUT"
```

- [ ] **Step 1: Check the stack and record its identity and limits**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
curl -sf http://localhost:8081/build | python3 -c 'import sys,json;print(json.load(sys.stdin)["composite"])'
docker exec iverson-api env | command grep -E '^Patterns__Limits__' || echo "no Patterns__Limits__ overrides (defaults apply)"
```
Expected: a composite, and either the overrides or the defaults line. Record both for `phase0.md`. The spec's calls need only the defaults: `limit` 10,000 and short expressions.

- [ ] **Step 2: Ingest (registration, 14,746 writes, readiness)**

This is long-running: 14,746 sequential writes, then readiness polls for up to an hour while TEI embeds. Run it in the background, then check the log after about 3 minutes and again when it finishes.
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
python3 "$DP" ingest --out "$OUT" > "$OUT/ingest.log" 2>&1; echo "EXIT=$?" >> "$OUT/ingest.log"
```
Expected: the last line is `EXIT=0`, and `OUT/ingest.json` records 14,746 keys, the DialogueId→split map and the composite.
- If the log shows an abort early on, stop and report it; for example `UNAUTHENTICATED`, a registration error, or a token that isn't `tenant_bypass`.
- If only readiness timed out after every write finished, run `python3 "$DP" ready --out "$OUT"` (same sourcing). **Never run `ingest` twice.**

- [ ] **Step 3: Probe S**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
python3 "$DP" probe-s --out "$OUT"; echo "EXIT=$?"; cat "$OUT/probe-s.json"
```
Expected: `EXIT=0`, and exactly one row, for the first test dialogue.

- [ ] **Step 4: Probe T (dev, placeholder θ 0.5)**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
python3 "$DP" probe-t --out "$OUT"; echo "EXIT=$?"; cat "$OUT/probe-t.json"
```
Expected: `EXIT=0` and a call time under 30 s.
- If the result is a STOP (non-zero exit, `passed: false`), end the task and report `probe-t.json`. The controller returns to the user.
- Never raise a limit or a timeout to make the call fit.

- [ ] **Step 5: Record Phase 0**

Source the state file first. Write `$OUT/phase0.md` with:
- the date and the composite;
- the live limit values from Step 1;
- the ingest counts and readiness outcome;
- `probe-s.json`;
- `probe-t.json`.

No commit: everything here lives outside the repository.

### Task 3: Live run, part 2: calibrate, score, and the gate document

**Files:**
- Create: `docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md`.
- Outside the repository: `OUT/theta.json` and `OUT/results.json`.

**Interfaces:**
- Consumes Task 1's script, and Task 2's state file, `ingest.json` and `phase0.md`.

- [ ] **Step 1: Calibrate θ on dev (Phase 1)**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
python3 "$DP" calibrate --out "$OUT" > "$OUT/calibrate.log" 2>&1; echo "EXIT=$?"; tail -3 "$OUT/calibrate.log"; cat "$OUT/theta.json"
```
Expected: `EXIT=0`, and a `theta.json` holding four θ values, the full grid and the `at_grid_edge` flags. The spec expects the dev argmax at p74–p91, inside the grid. An argmax at the grid's edge is recorded, not changed. Stop on any non-zero exit. Every abort is deliberate; never rerun with a relaxed check.

- [ ] **Step 2: Score on test (Phase 2, 40 calls)**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
python3 "$DP" score --out "$OUT" > "$OUT/score.log" 2>&1; echo "EXIT=$?"; tail -5 "$OUT/score.log"
python3 -c "import json;r=json.load(open('$OUT/results.json'));print(r['gate'])"
```
Expected: `EXIT=0`, and `results.json` holding:
- per-pair and per-arm precision, recall and F1;
- each arm's macro-F1;
- the bootstrap CIs for ordered − order-free and for ordered − keyword;
- per-intent single-intent F1;
- the gate verdict.

Stop on any non-zero exit.

- [ ] **Step 3: Write the gate document**

Source the state file first, then run `md5sum "$OUT"/*.json "$OUT"/data/*/*.json`. Create `docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md` with these sections, filled from the files named:
1. **Verdict.** GO or NO-GO under the spec's gate: the paired bootstrap 95% CI of ordered − order-free macro-F1 is entirely above 0. Give the Δ and its CI.
2. **Data.** The dataset URLs and md5s, the split sizes (dialogues and user turns), and the onset rule with its tie counts. The source is `ingest.json` and the spec.
3. **Pre-registered constants.** The four descriptions, the keyword lists, the θ grid, and the seed.
4. **θ values.** From `theta.json`, including the per-intent dev F1 at θ and any grid-edge flags.
5. **Results.** Per-pair precision, recall and F1 for all three arms; each pair's gold-positive and reversed-negative counts; each arm's macro-F1; and per-intent single-intent F1 on test.
6. **Reported comparisons.** Ordered semantic − ordered keyword, with its Δ and CI.
7. **Phase 0,** from `phase0.md`: the composite, the live limits, the ingest counts, readiness, Probe S, and Probe T's timing on dev with θ 0.5.
8. **Integrity.** Every call exited cleanly, with no count equal to `limit`; the matched ids are a subset of the written ids; and the `/build` composite was equal throughout.
9. **md5s of every artefact.**
10. **Deviations.** This plan's "Plan decisions beyond the spec" section, 1–8.
11. **Known limits.**
    - **Per-pair samples are modest,** at 37–88 positives per pair.
    - **Onset lag:** 128 test onsets come a turn or more after the customer's first mention, which shifts the dev gate difference by at most 0.0022 (spec VA19).
    - **The descriptions are fixed and untuned,** so a NO-GO speaks only for them and this θ grid.

- [ ] **Step 4: Commit the gate document**
```bash
. /home/ben/repositories/iverson-benchmark-corpora/matchpattern-dialogues-shell.sh
git add -f docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md
git commit -m "record the matchpattern dialogue-sequence gate verdict" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" -- docs/plans/2026-09-GATE-matchpattern-dialogue-sequences.md
```

## Tasks NOT in this plan

- Any production change.
- Descriptions written or tuned by an LLM.
- The train split.
- The taxi and book intents.
- Sequences of three or more intents.
- The RetailRocket and chunk-span use cases.
- Latency comparisons against other engines.

## Known issues inherited from spec

- **The shared stack is still in the RRF benchmark's state.** It has `MaxExpressionLength=25000` and `MaxOutputRows=18623` set. This design needs neither: its expressions are short, and its `limit` of 10,000 is at or below the default. Phase 0 records the live limit values.
- **Writes go to `tenant_bypass` on the shared stack.** One new type and 14,746 rows are written there.
- **Per-pair sample sizes are modest,** with 37–88 positives per pair. Per-pair F1 is noisy, which is why the gate is on macro-F1 with a bootstrap CI.
