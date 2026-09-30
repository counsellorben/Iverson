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
    # Verify reversed_negative is gold_positive of the reverse pair: test with custom splits where counts differ.
    # Dialogues: X (hotel then restaurant) and Y (restaurant only).
    # gold_positive(HOTEL, RESTAURANT) = 1 (X); gold_positive(RESTAURANT, HOTEL) = 0.
    custom_splits = {
        "test": [dialogue("X", [turn("hotel", F("hotel", HOTEL, "a")), turn("restaurant", F("restaurant", RESTAURANT, "b"))]),
                 dialogue("Y", [turn("restaurant", F("restaurant", RESTAURANT, "c"))])],
    }
    matches2 = {arm: {pair: set() for pair in dp.PAIRS} for arm in dp.ARMS}
    matches2["ordered_semantic"][(HOTEL, RESTAURANT)] = {"X"}
    single2 = {i: {("X", 0): 0.1, ("X", 1): 0.1, ("Y", 0): 0.1} for i in dp.INTENTS}
    results2 = dp.score_results(custom_splits, matches2, single2, {i: 0.5 for i in dp.INTENTS})
    hr = {(p["x"], p["y"]): p for p in results2["pairs"]}[(HOTEL, RESTAURANT)]
    assert hr["gold_positive"] == 1 and hr["reversed_negative"] == 0  # Catches the mutant


def test_a_readiness_call_at_limit_aborts_rather_than_waiting(tmp_path):
    def respond(request):
        return [row(DialogueId="V1", TurnIndex=float(i), s=0.3) for i in range(request.limit)]
    clock = Clock()
    with pytest.raises(SystemExit, match="truncated"):
        ingested(tmp_path, engine=FakeEngine(respond=respond), clock=clock)
    assert clock.now == 0.0
