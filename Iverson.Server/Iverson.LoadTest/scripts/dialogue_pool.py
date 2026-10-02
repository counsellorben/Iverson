"""MatchPattern dialogue pool: can a pre-registered SIMILARITY description pool match the keyword arm?

Spec: docs/specs/2026-10-02-matchpattern-dialogue-pool-design.md. Follows the 2026-09-29 dialogue-sequence
run (dialogue_patterns.py, unmodified), whose ingested DialogueTurn rows this script reads and never writes.

Subcommands, both taking --out (this run's directory) and --from-run (the 2026-09-29 run directory, whose
ingest.json and md5-pinned data/ are read, never written):

    select   dev: one calibration-shape call per pool phrasing (32), greedy max-combination of up to three
             phrasings per intent by the original single-intent theta_grid rule, the fidelity pin, then
             selection.json.
    score    test: 12 pool-semantic pair calls, 12 keyword pair calls, one single-intent call per selected
             phrasing; results.json with per-dialogue predictions, the paired-bootstrap CI and the
             non-inferiority verdict.

Every pass checks /build before and after against ingest.json's composite (the 2026-09-29 build); a
rebuilt stack aborts the run. Run with the python-libs PYTHONPATH (numpy) and the credentials pattern_leg
reads from the environment (the run's state file sets them)."""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dialogue_patterns as dp  # noqa: E402

pl, pb = dp.pl, dp.pb

# ── Pre-registered constants (spec "Pre-registration"; never edited after selection) ──────────

POOL = {
    "find_hotel": (
        "the customer is looking for a hotel to stay at",
        "the customer needs a place to stay, such as a hotel or guesthouse",
        "the customer asks about a hotel's stars, parking, wifi or price",
        "I need somewhere to stay tonight",
        "book a room at a guesthouse",
        "the customer wants accommodation in the north of town",
        "looking for a cheap 4 star hotel with free parking",
        "the customer asks to book a hotel for several nights",
    ),
    "find_restaurant": (
        "the customer is looking for a restaurant to eat at",
        "the customer wants somewhere to eat that serves a certain kind of food",
        "the customer asks about a restaurant's food type, price range or area",
        "I want to find a place to eat",
        "an expensive italian restaurant in the centre",
        "the customer wants to book a table for dinner",
        "looking for cheap chinese food",
        "the customer asks for a restaurant recommendation",
    ),
    "find_train": (
        "the customer is looking for a train",
        "the customer wants a train departing from or arriving at a station",
        "the customer asks about train times, departures or arrivals",
        "I need a train to cambridge on saturday",
        "a train leaving after 10:00 from london",
        "the customer wants to book train tickets",
        "what time does the train arrive",
        "the customer is travelling by train",
    ),
    "find_attraction": (
        "the customer is looking for an attraction such as a museum, college, park or theatre",
        "the customer asks about places to go and things to see in town",
        "the customer wants to visit a museum",
        "the customer wants a tourist attraction to visit",
        "is there a college or a church I can visit",
        "something fun to do in the centre of town",
        "the customer asks about entertainment, nightclubs or swimming pools",
        "the customer wants the entrance fee and address of a sight",
    ),
}

MAX_PHRASINGS = 3
FIDELITY_PIN = {"find_hotel": [1, 2, 0], "find_restaurant": [1], "find_train": [3, 5, 0],
                "find_attraction": [0, 4, 6]}
NON_INFERIORITY_BOUND = -0.02                   # GO when the CI lower bound of pool - keyword exceeds this
ARMS = ("pool_semantic", "ordered_keyword")
ORIGINAL_SEMANTIC_MACRO_F1 = 0.5102             # 2026-09-29 gate doc, ordered_semantic; point estimate only


def say(message):
    print(f"[dialogue_pool] {message}", flush=True)


def fail(message):
    sys.exit(f"[dialogue_pool] {message}")


# ── Selection (pure) ─────────────────────────────────────────────────────────────────────────

def combine(score_maps):
    """{turn: max s over the given per-phrasing score maps}; every map covers the same turns."""
    first = score_maps[0]
    return {turn: max(m[turn] for m in score_maps) for turn in first}


def select_phrasings(per_phrasing_scores, gold, dialogue_ids):
    """Spec "Selection": greedily add phrasings (max-combined) while dialogue-level single-intent F1 at the
    theta_grid optimum strictly improves; the lowest pool index wins equal F1s; at most MAX_PHRASINGS.
    Returns {"chosen", "theta", "percentile", "f1", "at_grid_edge", "grid", "steps"}."""
    chosen, current, steps = [], None, []
    while len(chosen) < MAX_PHRASINGS:
        best = None                                           # (f1, index, grid)
        candidates = []
        for index in range(len(per_phrasing_scores)):
            if index in chosen:
                continue
            grid = dp.theta_grid(combine([per_phrasing_scores[i] for i in chosen + [index]]), gold, dialogue_ids)
            candidates.append({"index": index, "f1": grid["f1"]})
            if best is None or grid["f1"] > best[0]:          # strict: an equal F1 keeps the lower index
                best = (grid["f1"], index, grid)
        if best is None or (current is not None and not best[0] > current["f1"]):
            steps.append({"candidates": candidates, "added": None})
            break
        chosen.append(best[1])
        current = best[2]
        steps.append({"candidates": candidates, "added": best[1]})
    return {"chosen": chosen, "theta": current["theta"], "percentile": current["percentile"],
            "f1": current["f1"], "at_grid_edge": current["at_grid_edge"], "grid": current["grid"], "steps": steps}


def check_fidelity(picks):
    """Abort unless the live selection equals the offline replica's dev selection (spec "Fidelity pin")."""
    if picks != FIDELITY_PIN:
        fail(f"fidelity pin FAILED: live selection {picks} != offline replica {FIDELITY_PIN} -- the live "
             "SIMILARITY path differs from the replica; stop before any test call")


# ── Requests (pure) ──────────────────────────────────────────────────────────────────────────

def pool_define(phrasings, theta):
    """SIMILARITY(Utterance, 'd1') > theta OR ... -- identical to max over the phrasings > theta."""
    return " OR ".join(f"{pl.similarity('Utterance', p)} > {pl.format_theta(theta)}" for p in phrasings)


def pool_arm_request(split, x, y, defines):
    """The pool arm's call for one ordered pair: dialogue_patterns.arm_request's shape with the pool
    defines. `defines` is {intent: DEFINE expression}."""
    return (dp._split_rows(split).pattern(dp.ORDERED_PATTERN).define("A", defines[x]).define("B", defines[y])
            .define("Z", "TRUE").rows_per_match(pb.ONE_ROW).limit(dp.LIMIT).build())


# ── Scoring (pure) ───────────────────────────────────────────────────────────────────────────

def gate(ci):
    """Non-inferiority: GO when the 95% CI lower bound of pool - keyword macro-F1 exceeds -0.02."""
    return "GO" if ci[0] > NON_INFERIORITY_BOUND else "NO-GO"


def predictions_in_order(matches, dialogue_ids):
    """{arm: {pair: [bool per dialogue in dialogue_ids order]}} from {arm: {pair: set of DialogueIds}}."""
    return {arm: {pair: [d in matches[arm][pair] for d in dialogue_ids] for pair in dp.PAIRS} for arm in ARMS}


def score_results(splits, matches, single_scores, selection):
    """Everything results.json holds except the call log and /build record. `matches` is {arm: {pair: set}},
    `single_scores` {intent: {(DialogueId, TurnIndex): s}} (max over the selected phrasings), `selection`
    {intent: {"theta", ...}}."""
    test = splits["test"]
    dialogue_ids = dp.split_ids(test)
    by_dialogue = {d["dialogue_id"]: dp.onsets(d) for d in test}
    gold = dp.gold_labels(by_dialogue, dialogue_ids)
    predictions = predictions_in_order(matches, dialogue_ids)
    pairs = []
    for x, y in dp.PAIRS:
        pairs.append({"x": x, "y": y, "gold_positive": sum(gold[(x, y)]), "reversed_negative": sum(gold[(y, x)]),
                      "arms": {arm: {**dp.prf(predictions[arm][(x, y)], gold[(x, y)]),
                                     "predicted": sum(predictions[arm][(x, y)])} for arm in ARMS}})
    macro = {arm: dp.macro_f1(p["arms"][arm]["f1"] for p in pairs) for arm in ARMS}
    draws = dp.bootstrap_macro_f1(predictions, gold)
    ci = dp.percentile_ci(draws["pool_semantic"] - draws["ordered_keyword"])
    single_gold = dp.single_intent_gold(by_dialogue, dialogue_ids)
    single = {intent: {**dp.prf(dp.single_intent_predictions(single_scores[intent], selection[intent]["theta"],
                                                             dialogue_ids), single_gold[intent]),
                       "gold_positive": sum(single_gold[intent])}
              for intent in dp.INTENTS}
    return {
        "dialogues": len(dialogue_ids), "dialogue_ids": dialogue_ids, "ties": dp.tie_count(by_dialogue),
        "pairs": pairs, "macro_f1": macro,
        "bootstrap": {"resamples": dp.BOOTSTRAP_RESAMPLES, "seed": dp.BOOTSTRAP_SEED,
                      "ci_percentiles": list(dp.CI_PERCENTILES),
                      "pool_minus_keyword": {"delta": macro["pool_semantic"] - macro["ordered_keyword"], "ci95": ci}},
        "non_inferiority_bound": NON_INFERIORITY_BOUND, "gate": gate(ci),
        "pool_minus_original_semantic": {"delta": macro["pool_semantic"] - ORIGINAL_SEMANTIC_MACRO_F1,
                                         "original": ORIGINAL_SEMANTIC_MACRO_F1},
        "single_intent": single,
        "predictions": {arm: {f"{x} -> {y}": predictions[arm][(x, y)] for x, y in dp.PAIRS} for arm in ARMS},
    }


# ── Live steps ───────────────────────────────────────────────────────────────────────────────

def load_from_run(from_run, splits):
    """The 2026-09-29 run's ingest.json, which must record a passed readiness check and match the data."""
    ingest = dp.read_json(dp.out_path(from_run, "ingest.json"), "the --from-run ingest")
    if not ingest.get("readiness"):
        fail(f"{from_run}/ingest.json records no passed readiness check")
    dp.check_ingest_matches_data(ingest, splits)
    return ingest


def cmd_select(live, splits, from_run):
    selection_path = dp.out_path(live.out, "selection.json")
    dp.refuse_existing(selection_path, "the selection is frozen once written (spec: pre-registered)")
    ingest = load_from_run(from_run, splits)
    dev = splits["dev"]
    dialogue_ids = dp.split_ids(dev)
    gold = dp.single_intent_gold({d["dialogue_id"]: dp.onsets(d) for d in dev}, dialogue_ids)
    expected = dp.written_turns(ingest, "dev")
    calls = []

    def body():
        dp.wait_until_ready(live, "dev", expected)
        scores = {}
        for intent in dp.INTENTS:
            scores[intent] = []
            for index, phrasing in enumerate(POOL[intent]):
                what = f"calibration {intent} [{index}]"
                scores[intent].append(dp.strict_turn_scores(
                    dp.timed_call(live, dp.all_rows_request("dev", phrasing), what, calls), expected, what))
        return scores

    scores, check = dp.checked(live, ingest["composite"], "select", body)
    selection = {intent: select_phrasings(scores[intent], gold[intent], dialogue_ids) for intent in dp.INTENTS}
    check_fidelity({intent: selection[intent]["chosen"] for intent in dp.INTENTS})
    for intent in dp.INTENTS:
        selection[intent]["phrasings"] = [POOL[intent][i] for i in selection[intent]["chosen"]]
    pl.write_json(selection_path, {"pool": {i: list(POOL[i]) for i in dp.INTENTS}, "selection": selection,
                                   "calls": calls, "build": check})
    for intent in dp.INTENTS:
        s = selection[intent]
        say(f"{intent}: phrasings {s['chosen']}, p{s['percentile']} theta {s['theta']!r}, dev F1 {s['f1']:.4f}"
            + ("  (AT THE GRID EDGE)" if s["at_grid_edge"] else ""))


def cmd_score(live, splits, from_run):
    results_path = dp.out_path(live.out, "results.json")
    dp.refuse_existing(results_path, "the test split is scored once")
    selection = dp.read_json(dp.out_path(live.out, "selection.json"), "select")["selection"]
    if set(selection) != set(dp.INTENTS):
        fail(f"selection.json holds {sorted(selection)}, not the four intents")
    ingest = load_from_run(from_run, splits)
    expected = dp.written_turns(ingest, "test")
    written = dp.written_dialogues(ingest, "test")
    defines = {intent: pool_define(selection[intent]["phrasings"], selection[intent]["theta"])
               for intent in dp.INTENTS}
    calls = []

    def body():
        dp.wait_until_ready(live, "test", expected)
        matches = {arm: {} for arm in ARMS}
        for x, y in dp.PAIRS:
            what = f"pool_semantic {x} -> {y}"
            matches["pool_semantic"][(x, y)] = dp.matched_dialogues(
                dp.timed_call(live, pool_arm_request("test", x, y, defines), what, calls), written, what)
        for x, y in dp.PAIRS:
            what = f"ordered_keyword {x} -> {y}"
            matches["ordered_keyword"][(x, y)] = dp.matched_dialogues(
                dp.timed_call(live, dp.arm_request("ordered_keyword", "test", x, y, None), what, calls), written, what)
        single = {}
        for intent in dp.INTENTS:
            maps = []
            for phrasing in selection[intent]["phrasings"]:
                what = f"single-intent {intent} '{phrasing}'"
                maps.append(dp.strict_turn_scores(
                    dp.timed_call(live, dp.all_rows_request("test", phrasing), what, calls), expected, what))
            single[intent] = combine(maps)
        return matches, single

    (matches, single), check = dp.checked(live, ingest["composite"], "score", body)
    results = {**score_results(splits, matches, single, selection), "selection": selection, "calls": calls,
               "build": check}
    pl.write_json(results_path, results)
    b = results["bootstrap"]["pool_minus_keyword"]
    say(f"macro-F1 {results['macro_f1']}")
    say(f"pool - keyword {b['delta']:+.4f} CI {b['ci95']} (GO when the lower bound > {NON_INFERIORITY_BOUND})")
    say(f"GATE: {results['gate']}")


# ── Wiring ───────────────────────────────────────────────────────────────────────────────────

COMMANDS = {"select": cmd_select, "score": cmd_score}


def build_arg_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in COMMANDS:
        p = sub.add_parser(name)
        p.add_argument("--out", required=True, help="this run's directory")
        p.add_argument("--from-run", required=True, help="the 2026-09-29 run directory (ingest.json, data/)")
        p.add_argument("--build-url", default=os.environ.get("IVERSON_HTTP_URL", "http://localhost:8081") + "/build")
        p.add_argument("--username", default=pl.BYPASS_USERNAME, help="the acting user to mint a token for")
    return ap


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    os.makedirs(args.out, exist_ok=True)
    dp.ensure_data(os.path.join(args.from_run, "data"))       # md5-checks the pinned files; downloads nothing present
    splits = dp.load_splits(os.path.join(args.from_run, "data"))
    session = pl.TokenSession(lambda: pl.mint_acting_user_token(args.username), pl.mint_service_token_from_env)
    channel, _ = pl.connect(session)
    try:
        live = dp.Live(args.out, dp.EntityCoordinator(dp.DialogueTurn, channel), pl.fetch_build, args.build_url,
                       acting_token=session.acting_user_token())
        COMMANDS[args.cmd](live, splits, args.from_run)
    finally:
        channel.close()


if __name__ == "__main__":
    main()
