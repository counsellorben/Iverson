"""pytest suite for dialogue_pool.py (spec docs/specs/2026-10-02-matchpattern-dialogue-pool-design.md,
"Testing"). Run with the python-libs PYTHONPATH (dialogue_patterns imports numpy):

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \\
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_pool.py -q

No network: the selection, define builder, gate and prediction ordering are pure, and the live commands are
exercised only up to their refusal guards, which run before any call."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dialogue_pool as dpool  # noqa: E402

dp = dpool.dp
HOTEL, RESTAURANT, TRAIN, ATTRACTION = dp.INTENTS


# ── greedy selection ─────────────────────────────────────────────────────────────────────────

def scores_for(per_dialogue):
    """One phrasing's per-turn scores: {(DialogueId, 0): s} from {DialogueId: s}."""
    return {(d, 0): s for d, s in per_dialogue.items()}


IDS = ["d1", "d2", "d3", "d4"]
GOLD = [True, True, False, False]          # d1, d2 have the intent


def test_selection_adds_the_best_phrasing_first_then_stops_without_strict_improvement():
    perfect = scores_for({"d1": 0.9, "d2": 0.9, "d3": 0.1, "d4": 0.1})   # separates gold exactly: F1 1.0
    noisy = scores_for({"d1": 0.9, "d2": 0.2, "d3": 0.8, "d4": 0.1})
    result = dpool.select_phrasings([noisy, perfect, noisy], GOLD, IDS)
    assert result["chosen"] == [1]                       # nothing can beat F1 1.0, so it stops after one
    assert result["f1"] == 1.0
    assert result["steps"][-1]["added"] is None


def test_equal_f1_goes_to_the_lowest_pool_index():
    perfect = scores_for({"d1": 0.9, "d2": 0.9, "d3": 0.1, "d4": 0.1})
    result = dpool.select_phrasings([scores_for({"d1": 0.1, "d2": 0.1, "d3": 0.9, "d4": 0.9}), perfect, perfect],
                                    GOLD, IDS)
    assert result["chosen"][0] == 1


def test_a_phrasing_is_added_only_when_it_strictly_raises_f1():
    only_d1 = scores_for({"d1": 0.9, "d2": 0.1, "d3": 0.1, "d4": 0.1})    # alone: recall 1/2
    only_d2 = scores_for({"d1": 0.1, "d2": 0.9, "d3": 0.1, "d4": 0.1})    # combined with only_d1: F1 1.0
    result = dpool.select_phrasings([only_d1, only_d2], GOLD, IDS)
    assert result["chosen"] == [0, 1]
    assert result["f1"] == 1.0


def test_selection_is_capped_at_three_phrasings():
    ids = [f"d{i}" for i in range(8)]
    gold = [True] * 4 + [False] * 4
    # phrasing k recognises only gold dialogue k, so every added phrasing strictly raises recall
    pool = [{(f"d{j}", 0): (0.9 if j == k else 0.1) for j in range(8)} for k in range(4)]
    assert dpool.select_phrasings(pool, gold, ids)["chosen"] == [0, 1, 2]


# ── the OR define and the arm request ────────────────────────────────────────────────────────

def test_pool_define_ors_one_similarity_term_per_phrasing():
    assert dpool.pool_define(["a b", "c"], 0.5) == "SIMILARITY(Utterance, 'a b') > 0.5 OR SIMILARITY(Utterance, 'c') > 0.5"


def test_pool_define_escapes_a_quote_inside_a_phrasing():
    assert "'the customer asks about a hotel''s stars'" in dpool.pool_define(["the customer asks about a hotel's stars"], 0.5)


def test_pool_arm_request_is_ordered_one_row_over_the_split():
    request = dpool.pool_arm_request("test", HOTEL, TRAIN, {HOTEL: "X > 1", TRAIN: "Y > 2"})
    assert request.pattern == dp.ORDERED_PATTERN
    assert [(d.name, d.expr) for d in request.define] == [("A", "X > 1"), ("B", "Y > 2"), ("Z", "TRUE")]
    assert request.rows_per_match == dp.pb.ONE_ROW and request.limit == dp.LIMIT


# ── gate, fidelity pin, prediction order ─────────────────────────────────────────────────────

@pytest.mark.parametrize("lower, verdict", [(-0.0199, "GO"), (-0.02, "NO-GO"), (-0.0201, "NO-GO")])
def test_non_inferiority_gate(lower, verdict):
    assert dpool.gate([lower, 0.1]) == verdict


def test_a_mismatching_selection_fails_the_fidelity_pin():
    picks = dict(dpool.FIDELITY_PIN, find_train=[3, 5])
    with pytest.raises(SystemExit, match="fidelity pin FAILED"):
        dpool.check_fidelity(picks)
    dpool.check_fidelity(dict(dpool.FIDELITY_PIN))           # the pinned picks pass


def test_predictions_follow_the_dialogue_id_order():
    matches = {arm: {pair: {"b"} for pair in dp.PAIRS} for arm in dpool.ARMS}
    predictions = dpool.predictions_in_order(matches, ["a", "b", "c"])
    assert all(v == [False, True, False] for arm in dpool.ARMS for v in predictions[arm].values())


# ── refusal guards (they run before any call) ────────────────────────────────────────────────

def live_at(tmp_path):
    return dp.Live(str(tmp_path), None, lambda url: "c1", "http://b/build")


def test_select_refuses_an_existing_selection(tmp_path):
    (tmp_path / "selection.json").write_text("{}")
    with pytest.raises(SystemExit, match="frozen once written"):
        dpool.cmd_select(live_at(tmp_path), {}, str(tmp_path))


def test_score_refuses_an_existing_results_file(tmp_path):
    (tmp_path / "results.json").write_text("{}")
    with pytest.raises(SystemExit, match="scored once"):
        dpool.cmd_score(live_at(tmp_path), {}, str(tmp_path))


def test_score_refuses_to_run_without_a_selection(pool_run):
    from_run, out = pool_run                                 # a ready from-run: only selection.json is missing
    with pytest.raises(SystemExit, match="selection.json is missing -- run select first"):
        dpool.cmd_score(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)
    assert not (out / "results.json").exists()


# ── the live flow over fakes (synthetic splits and engine from test_dialogue_patterns) ───────

import json  # noqa: E402

import test_dialogue_patterns as tdp  # noqa: E402

DESCRIPTION = {HOTEL: tdp.D_HOTEL, RESTAURANT: tdp.D_RESTAURANT, TRAIN: tdp.D_TRAIN, ATTRACTION: tdp.D_ATTRACTION}
TEST_POOL = {intent: (DESCRIPTION[intent], f"an unhelpful {intent} phrasing") for intent in dp.INTENTS}


class PoolEngine(tdp.FakeEngine):
    """tdp.FakeEngine, plus the pool arm's OR of SIMILARITY terms."""

    @staticmethod
    def _holds(expr, t):
        return any(tdp.FakeEngine._holds(part, t) for part in expr.split(" OR "))


def pool_turns():
    """Phrasing 0 of each intent scores like the 2026-09-29 description in tdp.SIMS; phrasing 1 is 0.2
    everywhere, so selection keeps phrasing 0 alone (F1 1.0, nothing can strictly beat it)."""
    turns = []
    for record in dp.build_turns(tdp.SPLITS):
        key = (record["dialogue_id"], record["turn_index"])
        sims = {dp.READINESS_TEXT: 0.3}
        for intent in dp.INTENTS:
            sims[TEST_POOL[intent][0]] = tdp.SIMS.get(key, {}).get(DESCRIPTION[intent], 0.2)
            sims[TEST_POOL[intent][1]] = 0.2
        turns.append({"DialogueId": key[0], "TurnIndex": key[1], "Split": record["split"], "sims": sims,
                      **{dp.FLAG_COLUMNS[d]: record[dp.FLAG_FIELDS[d]] for d in dp.KEYWORDS}})
    return turns


@pytest.fixture
def pool_run(tmp_path, monkeypatch):
    """A 2026-09-29-style run dir with a passed readiness, and an empty pool OUT dir."""
    from_run = tmp_path / "run0"
    from_run.mkdir()
    tdp.ingested(from_run)
    monkeypatch.setattr(dpool, "POOL", TEST_POOL)
    monkeypatch.setattr(dpool, "FIDELITY_PIN", {intent: [0] for intent in dp.INTENTS})
    out = tmp_path / "pool"
    out.mkdir()
    return str(from_run), out


def test_select_runs_one_call_per_phrasing_and_writes_the_selection(pool_run):
    from_run, out = pool_run
    engine = PoolEngine(pool_turns())
    dpool.cmd_select(tdp.live_for(out, engine), tdp.SPLITS, from_run)
    saved = json.loads((out / "selection.json").read_text())
    assert [c["what"] for c in saved["calls"]] == [f"calibration {i} [{j}]" for i in dp.INTENTS for j in (0, 1)]
    assert all(saved["selection"][i]["chosen"] == [0] for i in dp.INTENTS)
    assert saved["selection"][HOTEL]["phrasings"] == [tdp.D_HOTEL]
    assert [g[0] for g in saved["selection"][HOTEL]["grid"]] == list(dp.THETA_PERCENTILES)  # grid reported
    assert saved["build"]["before"] == saved["build"]["after"] == "c1"


def test_a_failed_fidelity_pin_writes_no_selection(pool_run, monkeypatch):
    from_run, out = pool_run
    monkeypatch.setattr(dpool, "FIDELITY_PIN", {intent: [1] for intent in dp.INTENTS})
    with pytest.raises(SystemExit, match="fidelity pin FAILED"):
        dpool.cmd_select(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)
    assert not (out / "selection.json").exists()


def test_select_aborts_on_a_rebuilt_stack(pool_run):
    from_run, out = pool_run
    with pytest.raises(SystemExit):
        dpool.cmd_select(tdp.live_for(out, PoolEngine(pool_turns()), composites=["c2"]), tdp.SPLITS, from_run)
    assert not (out / "selection.json").exists()


def test_score_runs_both_arms_and_records_per_dialogue_predictions(pool_run):
    from_run, out = pool_run
    dpool.cmd_select(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)
    engine = PoolEngine(pool_turns())
    dpool.cmd_score(tdp.live_for(out, engine), tdp.SPLITS, from_run)
    results = json.loads((out / "results.json").read_text())
    scored = engine.log[1:]                                  # [0] is the readiness check
    assert len(scored) == len(results["calls"]) == 12 + 12 + 4
    assert all(r.where[0].value.string_val == "test" for r in engine.log)
    assert results["dialogue_ids"] == ["T1", "T2", "T3", "T4"]
    x, y = dp.PAIRS[0]
    theta = json.loads((out / "selection.json").read_text())["selection"]
    assert [d.expr for d in scored[0].define[:2]] == [dpool.pool_define([DESCRIPTION[x]], theta[x]["theta"]),
                                                      dpool.pool_define([DESCRIPTION[y]], theta[y]["theta"])]
    assert [d.expr for d in scored[12].define[:2]] == [dp.keyword_define(x), dp.keyword_define(y)]
    pool_ht = results["predictions"]["pool_semantic"][f"{HOTEL} -> {TRAIN}"]
    assert pool_ht == [True, False, False, False]           # T1 hotel then train; T2 reversed; T3 a same-turn tie
    assert set(results["predictions"]) == {"pool_semantic", "ordered_keyword"}
    assert results["gate"] in ("GO", "NO-GO") and results["non_inferiority_bound"] == -0.02
    assert "ci95" in results["bootstrap"]["pool_minus_keyword"]
    with pytest.raises(SystemExit, match="scored once"):
        dpool.cmd_score(tdp.live_for(out, PoolEngine(pool_turns())), tdp.SPLITS, from_run)


def test_score_results_orients_the_delta_and_ci_as_pool_minus_keyword():
    """Pool predicts exactly the gold dialogues of every pair and keyword predicts none, so the delta is
    positive, the CI's upper bound is positive, and the verdict is GO; flipping the orientation fails this."""
    test = tdp.SPLITS["test"]
    ids = dp.split_ids(test)
    gold = dp.gold_labels({d["dialogue_id"]: dp.onsets(d) for d in test}, ids)
    matches = {"pool_semantic": {p: {d for d, g in zip(ids, gold[p]) if g} for p in dp.PAIRS},
               "ordered_keyword": {p: set() for p in dp.PAIRS}}
    single = {i: {(d, 0): 0.0 for d in ids} for i in dp.INTENTS}
    results = dpool.score_results(tdp.SPLITS, matches, single, {i: {"theta": 0.5} for i in dp.INTENTS})
    b = results["bootstrap"]["pool_minus_keyword"]
    assert b["delta"] == results["macro_f1"]["pool_semantic"] - results["macro_f1"]["ordered_keyword"] > 0
    assert 0 <= b["ci95"][0] <= b["ci95"][1] and b["ci95"][1] > 0
    assert results["gate"] == "GO"


def test_score_combines_every_selected_phrasing_for_single_intent(pool_run):
    """A hand-written two-phrasing hotel selection: only the second phrasing scores T4 (no hotel) above theta,
    so the combined single-intent prediction has one false positive; first-phrasing-only would have none."""
    from_run, out = pool_run
    selection = {i: {"chosen": [0], "phrasings": [TEST_POOL[i][0]], "theta": 0.5} for i in dp.INTENTS}
    selection[HOTEL] = {"chosen": [0, 1], "phrasings": list(TEST_POOL[HOTEL]), "theta": 0.5}
    (out / "selection.json").write_text(json.dumps({"selection": selection}))
    turns = pool_turns()
    for t in turns:
        if (t["DialogueId"], t["TurnIndex"]) == ("T4", 0):
            t["sims"][TEST_POOL[HOTEL][1]] = 0.95
    engine = PoolEngine(turns)
    dpool.cmd_score(tdp.live_for(out, engine), tdp.SPLITS, from_run)
    results = json.loads((out / "results.json").read_text())
    assert len(results["calls"]) == 12 + 12 + 5                # two hotel single-intent calls, one per other intent
    assert results["single_intent"][HOTEL]["fp"] == 1          # T4 via the second phrasing
