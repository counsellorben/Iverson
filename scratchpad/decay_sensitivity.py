#!/usr/bin/env python3
"""
Task 2 (decay-weight-sensitivity): replay Task 1's captured reranker inputs
under synthetic age distributions and decide, by the spec's fixed rule,
whether triple B (0.45/0.45/0.10) is safe to ship in place of the swept
triple A (0.50/0.50/0.10).

Run with:
  PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \
    python3 scratchpad/decay_sensitivity.py

See task-2-brief.md for the exact interface this implements. Formulas below
are copied verbatim from the brief; do not "simplify" the dead branch in
fuse() — it is intentionally kept even though this dataset never exercises it.
"""

import csv
import json
import sys
from collections import defaultdict

import numpy as np
from scipy.stats import kendalltau
import ir_measures
from ir_measures import nDCG

CORPUS_DIR = "/home/ben/repositories/iverson-benchmark-corpora/freshstack-chunk256-2026-08-30"
CAPTURE_DIR = "/home/ben/repositories/iverson-benchmark-corpora/decay-capture-2026-08-31"
KEYMAP_PATH = f"{CORPUS_DIR}/keymap.json"
QRELS_PATH = f"{CORPUS_DIR}/qrels.trec"
QUERIES_PATH = f"{CORPUS_DIR}/beir/queries.jsonl"

FILES = {
    "m5": f"{CAPTURE_DIR}/fusion-capture-m5.csv",
    "m20": f"{CAPTURE_DIR}/fusion-capture-m20.csv",
}

DOCUMENT_BUDGET = 50  # BenchmarkQueryScenario.cs:37 -- the harness's DocumentBudget

GLOBAL_SEED = 20260831  # recorded so the scenario table is reproducible
SCENARIO_SEED_OFFSET = {"uniform": 0, "narrow": 1, "wide": 2, "bimodal": 3}
FILE_SEED_OFFSET = {"m5": 100, "m20": 200}


# ---------------------------------------------------------------------------
# fuse(): copied verbatim from task-2-brief.md. Do NOT alter the
# `not has_centroid and decay is None` branch -- hasCentroid is uniformly 1
# on this data, so that branch is dead here, and it stays exactly as written.
# ---------------------------------------------------------------------------
def fuse(wb, wc, wd, base, centroid, decay, has_centroid):
    if not has_centroid and decay is None:
        return base  # mirrors ResultReranker.cs:24-31
    num, den = wb * base, wb
    if has_centroid:
        num += wc * centroid
        den += wc
    if decay is not None:
        num += wd * decay
        den += wd
    return num / den


A = lambda b, c, d, h: fuse(0.50, 0.50, 0.10, b, c, d, h)  # swept triple, sum 1.10
B = lambda b, c, d, h: fuse(0.45, 0.45, 0.10, b, c, d, h)  # candidate triple, sum 1.00


def decay_for(age_days):
    return min(1.0, 0.5 ** (age_days / 180.0))


SCENARIOS = {
    "uniform": lambda rng, n: [365.0] * n,  # control: must reorder nothing
    "narrow": lambda rng, n: rng.uniform(0, 30, n),
    "wide": lambda rng, n: rng.uniform(0, 720, n),
    "bimodal": lambda rng, n: rng.choice([7.0, 730.0], n),
}


def to_documents(rows, fused, keymap):
    """Collapse captured candidates to one row per document, exactly as the
    harness does: MaxPassageAggregator.Aggregate (max score per parent,
    unresolved parents dropped) then DocumentRanking.CollapseByDocId (max
    score per docId). Since parentId -> docId is already unique here, one
    pass over parentId does both steps."""
    best = {}  # docId -> max fused score
    for cand_id, parent_id, _has_centroid, _base, _centroid in rows:
        doc = keymap.get(parent_id)
        if doc is None:
            continue
        s = fused[cand_id]
        if doc not in best or s > best[doc]:
            best[doc] = s
    return sorted(best.items(), key=lambda kv: -kv[1])[:DOCUMENT_BUDGET]


# ---------------------------------------------------------------------------
# Load fixed corpus-side data once.
# ---------------------------------------------------------------------------
def load_keymap():
    with open(KEYMAP_PATH) as f:
        return json.load(f)


def load_query_order():
    """queries.jsonl load order == qrels.trec first-occurrence order (verified
    by hand before writing this script). i = (callIndex - min)//2 indexes
    into this list."""
    qids = []
    with open(QUERIES_PATH) as f:
        for line in f:
            d = json.loads(line)
            qids.append(d["_id"])
    return qids


def load_qrels():
    # read_trec_qrels returns a ONE-SHOT GENERATOR. calc_aggregate() consumes
    # it fully on the first call; every subsequent call against the same
    # object silently sees zero judgments and returns nan (discovered by
    # hand while debugging this script -- reproduced with a 2-call repro
    # before this fix). Materialize once and reuse the list everywhere.
    return list(ir_measures.read_trec_qrels(QRELS_PATH))


# ---------------------------------------------------------------------------
# Stream one capture CSV, grouped by callIndex. Verifies the four assertions
# from the interface notes as it goes -- a violation raises, it does not
# silently continue, because it means the loader is wrong.
# ---------------------------------------------------------------------------
def load_capture(path):
    calls = defaultdict(list)  # callIndex -> [(candidate_id, parent_id, has_centroid, base, centroid)]
    has_centroid_values = set()
    with open(path, newline="") as f:
        reader = csv.reader(f)
        for row in reader:
            call_index = int(row[0])
            candidate_id = row[1]
            parent_id = row[2]
            has_centroid = int(row[3])
            base = float(row[4])
            centroid = float(row[5])
            has_centroid_values.add(has_centroid)
            calls[call_index].append((candidate_id, parent_id, has_centroid, base, centroid))

    call_indices = sorted(calls.keys())
    n_calls = len(call_indices)
    assert n_calls == 1344, f"{path}: expected 1344 distinct callIndex, got {n_calls}"

    min_ci = call_indices[0]
    even_count = odd_count = 0
    row_count_by_parity = {0: set(), 1: set()}
    for ci in call_indices:
        n_rows = len(calls[ci])
        parity = (ci - min_ci) % 2
        row_count_by_parity[parity].add(n_rows)
        if parity == 0:
            even_count += 1
        else:
            odd_count += 1
    assert even_count == 672, f"{path}: expected 672 even (similar) calls, got {even_count}"
    assert odd_count == 672, f"{path}: expected 672 odd (chunks) calls, got {odd_count}"
    assert row_count_by_parity[0] == {200}, (
        f"{path}: even-call row counts not uniformly 200: {row_count_by_parity[0]}"
    )
    # odd-call row count depends on the multiplier (m5 -> 1000, m20 -> 4000);
    # caller checks the exact value against the expected multiplier.
    assert len(row_count_by_parity[1]) == 1, (
        f"{path}: odd-call row counts not uniform: {row_count_by_parity[1]}"
    )

    assert has_centroid_values == {1}, (
        f"{path}: hasCentroid is not uniformly 1: values seen = {has_centroid_values}"
    )

    odd_row_count = next(iter(row_count_by_parity[1]))
    return calls, min_ci, odd_row_count


def check_keymap_coverage(calls, keymap, path):
    pids = set()
    for rows in calls.values():
        for _cid, pid, _h, _b, _c in rows:
            pids.add(pid)
    misses = [p for p in pids if p not in keymap]
    print(
        f"  keymap coverage for {path}: {len(pids)} distinct parentId, "
        f"{len(misses)} misses ({100.0 * len(misses) / len(pids):.4f}% miss rate)"
    )
    if misses:
        print(f"    SAMPLE MISSES (up to 10): {misses[:10]}")
    return misses


# ---------------------------------------------------------------------------
# Per-scenario, per-triple ranking + comparison
# ---------------------------------------------------------------------------
def assign_decay(calls, scenario_name, seed):
    """One synthetic age draw per distinct parentId in this file, reused for
    every occurrence of that parentId across all calls in the file (A20:
    ages are per-document, not per-candidate-row)."""
    pids = set()
    for rows in calls.values():
        for _cid, pid, _h, _b, _c in rows:
            pids.add(pid)
    sorted_pids = sorted(pids)  # deterministic draw order independent of dict iteration
    rng = np.random.default_rng(seed)
    ages = SCENARIOS[scenario_name](rng, len(sorted_pids))
    return {pid: decay_for(age) for pid, age in zip(sorted_pids, ages)}


def score_triple(rows, triple_fn, decay_map):
    fused = {}
    for cand_id, parent_id, has_centroid, base, centroid in rows:
        decay = decay_map.get(parent_id)
        fused[cand_id] = triple_fn(base, centroid, decay, bool(has_centroid))
    return fused


def compare_rankings(doc_ranking_a, doc_ranking_b):
    """doc_ranking_{a,b}: list of (docId, score), already sorted desc, length <= DOCUMENT_BUDGET."""
    top10_a = {d for d, _ in doc_ranking_a[:10]}
    top10_b = {d for d, _ in doc_ranking_b[:10]}
    set_changed = top10_a != top10_b

    rank_a = {d: i for i, (d, _) in enumerate(doc_ranking_a)}
    rank_b = {d: i for i, (d, _) in enumerate(doc_ranking_b)}
    common = set(rank_a) & set(rank_b)
    if common:
        displacement = np.mean([abs(rank_a[d] - rank_b[d]) for d in common])
    else:
        displacement = float("nan")

    if len(common) >= 2:
        ranks_a_common = [rank_a[d] for d in common]
        ranks_b_common = [rank_b[d] for d in common]
        tau, _ = kendalltau(ranks_a_common, ranks_b_common)
    else:
        tau = float("nan")

    return set_changed, displacement, len(common), tau


def run_file(file_label, path, keymap, qids_by_i, qrels):
    print(f"\n=== {file_label}: {path} ===")
    calls, min_ci, odd_row_count = load_capture(path)
    expected_odd = {"m5": 1000, "m20": 4000}[file_label]
    assert odd_row_count == expected_odd, (
        f"{file_label}: expected odd-call row count {expected_odd}, got {odd_row_count}"
    )
    print(f"  loaded {len(calls)} calls, min callIndex={min_ci}, odd rows/call={odd_row_count} (OK)")
    check_keymap_coverage(calls, keymap, path)

    call_indices = sorted(calls.keys())

    results = {}  # scenario -> dict of aggregate stats
    ndcg_records = []  # rows for the report table

    for scenario_name in SCENARIOS:
        seed = GLOBAL_SEED + FILE_SEED_OFFSET[file_label] + SCENARIO_SEED_OFFSET[scenario_name]
        decay_map = assign_decay(calls, scenario_name, seed)

        n_calls = 0
        n_changed = 0
        displacements = []
        taus = []

        # run files for nDCG, split by path (similar=even, chunks=odd)
        run_rows = {"A": {"similar": [], "chunks": []}, "B": {"similar": [], "chunks": []}}

        for ci in call_indices:
            rows = calls[ci]
            i = (ci - min_ci) // 2
            path_name = "similar" if (ci - min_ci) % 2 == 0 else "chunks"
            qid = qids_by_i[i]

            fused_a = score_triple(rows, A, decay_map)
            fused_b = score_triple(rows, B, decay_map)

            docs_a = to_documents(rows, fused_a, keymap)
            docs_b = to_documents(rows, fused_b, keymap)

            changed, disp, n_common, tau = compare_rankings(docs_a, docs_b)
            n_calls += 1
            if changed:
                n_changed += 1
            if not np.isnan(disp):
                displacements.append(disp)
            if not np.isnan(tau):
                taus.append(tau)

            for triple_label, docs in (("A", docs_a), ("B", docs_b)):
                for rank, (doc_id, score) in enumerate(docs):
                    run_rows[triple_label][path_name].append((qid, doc_id, rank, score))

        frac_changed = n_changed / n_calls
        mean_disp = float(np.mean(displacements)) if displacements else float("nan")
        mean_tau = float(np.mean(taus)) if taus else float("nan")

        results[scenario_name] = {
            "n_calls": n_calls,
            "n_changed": n_changed,
            "frac_changed": frac_changed,
            "mean_displacement": mean_disp,
            "mean_kendall_tau": mean_tau,
            "seed": seed,
        }

        # nDCG@10, per triple, per path -- recorded, not decisive
        for triple_label in ("A", "B"):
            for path_name in ("similar", "chunks"):
                rows_for_run = run_rows[triple_label][path_name]
                run = defaultdict(dict)
                for qid, doc_id, rank, score in rows_for_run:
                    run[qid][doc_id] = score
                if run:
                    measure_result = ir_measures.calc_aggregate([nDCG @ 10], qrels, run)
                    ndcg = measure_result[nDCG @ 10]
                else:
                    ndcg = float("nan")
                ndcg_records.append(
                    {
                        "file": file_label,
                        "scenario": scenario_name,
                        "triple": triple_label,
                        "path": path_name,
                        "ndcg@10": ndcg,
                    }
                )

        print(
            f"  [{scenario_name:8s}] seed={seed} calls={n_calls} "
            f"top10-set-changed={n_changed}/{n_calls} ({100*frac_changed:.2f}%) "
            f"mean_disp={mean_disp:.4f} mean_tau={mean_tau:.4f}"
        )

    return results, ndcg_records


def apply_decision_rule(results):
    """Rule (spec, verbatim): if the top-10 set is unchanged for >=99% of
    calls under BOTH wide and bimodal, ship triple B; otherwise the choice
    is a product decision."""
    wide_ok = results["wide"]["frac_changed"] <= 0.01
    bimodal_ok = results["bimodal"]["frac_changed"] <= 0.01
    ship_b = wide_ok and bimodal_ok
    return ship_b, wide_ok, bimodal_ok


def main():
    print(f"GLOBAL_SEED={GLOBAL_SEED}")
    keymap = load_keymap()
    qids_by_i = load_query_order()
    assert len(qids_by_i) == 672, f"expected 672 queries, got {len(qids_by_i)}"
    qrels = load_qrels()
    print(f"loaded keymap ({len(keymap)} entries), {len(qids_by_i)} queries, qrels")

    all_results = {}
    all_ndcg = []
    for file_label, path in FILES.items():
        results, ndcg_records = run_file(file_label, path, keymap, qids_by_i, qrels)
        all_results[file_label] = results
        all_ndcg.extend(ndcg_records)

        # falsification test: uniform control must show EXACTLY zero top-10 set changes
        uniform_changed = results["uniform"]["n_changed"]
        if uniform_changed != 0:
            print(
                f"\n!!! FALSIFICATION: {file_label} uniform control changed "
                f"{uniform_changed}/{results['uniform']['n_calls']} top-10 sets. "
                f"Implementation is wrong -- stop trusting the sweep. !!!"
            )
        else:
            print(f"  uniform control OK for {file_label}: 0/{results['uniform']['n_calls']} changed")

    print("\n=== Decision rule, per multiplier arm ===")
    verdicts = {}
    for file_label in FILES:
        ship_b, wide_ok, bimodal_ok = apply_decision_rule(all_results[file_label])
        verdicts[file_label] = (ship_b, wide_ok, bimodal_ok)
        print(
            f"  {file_label}: wide<=1% changed = {wide_ok} "
            f"({100*all_results[file_label]['wide']['frac_changed']:.2f}%), "
            f"bimodal<=1% changed = {bimodal_ok} "
            f"({100*all_results[file_label]['bimodal']['frac_changed']:.2f}%) "
            f"=> {'SHIP TRIPLE B' if ship_b else 'HOLD (product decision)'}"
        )

    agree = verdicts["m5"][0] == verdicts["m20"][0]
    print(f"\nArms agree: {agree}")

    # dump machine-readable results for the doc-writing step
    out = {
        "global_seed": GLOBAL_SEED,
        "results": all_results,
        "ndcg": all_ndcg,
        "verdicts": {k: {"ship_b": v[0], "wide_ok": v[1], "bimodal_ok": v[2]} for k, v in verdicts.items()},
        "arms_agree": agree,
    }
    with open("/tmp/decay_sensitivity_results.json", "w") as f:
        json.dump(out, f, indent=2, default=str)
    print("\nwrote /tmp/decay_sensitivity_results.json")


if __name__ == "__main__":
    main()
