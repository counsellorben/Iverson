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
