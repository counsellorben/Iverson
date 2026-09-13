"""pytest suite for aspect_vectors.py -- the three candidate signals, the §2.7 reconstruction,
the query-clustered bootstrap and the Holm delegation (spec
docs/specs/2026-09-13-family2-vector-aspect-screen-design.md §2.2, §2.4, §2.7). Hand-built
fixtures only: no Qdrant, no TEI, no network. Run with:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \\
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_aspect_vectors.py -q

numpy and scipy are not importable without that PYTHONPATH."""
import math
import os
import re
import sys

import numpy as np
import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import aspect_vectors  # noqa: E402
import report          # noqa: E402


E0 = np.array([1.0, 0.0, 0.0])
E1 = np.array([0.0, 1.0, 0.0])
E2 = np.array([0.0, 0.0, 1.0])


def unit(v):
    return np.asarray(v, dtype=np.float64) / np.linalg.norm(v)


# --- Pre-registration guard --------------------------------------------------------------
#
# These four values are pre-registered in the design and steer the verdict. A later edit that
# quietly moved tau, the seed or the family size would leave every other test green.

def test_preregistered_constants():
    assert aspect_vectors.TAUS == (0.80, 0.85, 0.90, 0.95)
    assert aspect_vectors.TAU_PRIMARY == 0.90
    assert aspect_vectors.BOOTSTRAP_RESAMPLES == 10_000
    assert aspect_vectors.BOOTSTRAP_SEED == 20260913
    assert len(aspect_vectors.CANDIDATE_NAMES) == 3
    assert aspect_vectors.W_BASE == 0.45 and aspect_vectors.W_CENTROID == 0.45


# --- residual_spread ---------------------------------------------------------------------

def test_residual_spread_is_zero_at_n_one():
    assert aspect_vectors.residual_spread(np.array([unit([0.6, 0.8, 0.0])]), E0) == 0.0


def test_residual_spread_is_zero_on_two_identical_chunks():
    c = unit([0.6, 0.8, 0.0])
    assert aspect_vectors.residual_spread(np.vstack([c, c]), E0) == pytest.approx(0.0, abs=1e-12)


def test_residual_spread_is_one_when_the_residuals_are_orthogonal():
    # Both chunks share their whole query component; what is left is e1 for one and e2 for the
    # other, so the mean pairwise cosine DISTANCE among the residuals is 1 - 0 = 1.
    C = np.vstack([unit([1.0, 1.0, 0.0]), unit([1.0, 0.0, 1.0])])
    assert aspect_vectors.residual_spread(C, E0) == pytest.approx(1.0)


def test_residual_spread_exits_when_a_chunk_is_parallel_to_the_query():
    C = np.vstack([E0, unit([1.0, 1.0, 0.0])])
    with pytest.raises(SystemExit):
        aspect_vectors.residual_spread(C, E0)


# --- effective_rank ----------------------------------------------------------------------

def test_effective_rank_is_one_at_n_one():
    assert aspect_vectors.effective_rank(np.array([E0])) == pytest.approx(1.0)


def test_effective_rank_is_one_on_two_identical_unit_vectors():
    assert aspect_vectors.effective_rank(np.vstack([E0, E0])) == pytest.approx(1.0)


def test_effective_rank_is_two_on_two_orthogonal_unit_vectors():
    assert aspect_vectors.effective_rank(np.vstack([E0, E1])) == pytest.approx(2.0)


# --- greedy_cover ------------------------------------------------------------------------
#
# The tau-sensitivity §2.2 pre-registers the curve for: two chunks at cosine 0.92 are ONE
# covered chunk at tau = 0.90 and TWO at tau = 0.95. A fixture that answered the same at every
# tau would leave the whole curve unexercised.

COS_092 = np.vstack([E0, 0.92 * E0 + math.sqrt(1 - 0.92 ** 2) * E1])


def test_greedy_cover_collapses_the_pair_at_the_primary_tau():
    assert aspect_vectors.greedy_cover(COS_092, [1.0, 0.9], 0.90) == 1


def test_greedy_cover_separates_the_pair_at_tau_095():
    assert aspect_vectors.greedy_cover(COS_092, [1.0, 0.9], 0.95) == 2


def test_greedy_cover_is_one_at_n_one():
    assert aspect_vectors.greedy_cover(np.array([E0]), [1.0], 0.90) == 1


def test_greedy_cover_breaks_a_score_tie_by_pool_order():
    # c0 and c1 tie on score. Walking c0 first covers everything (c1 at 0.92 and c2 at 0.95 are
    # both above tau); walking c1 first admits c2, whose cosine to c1 is only 0.75. The answer
    # is therefore observably order-dependent, and kind="stable" fixes the order as the pool's.
    c1 = np.array([0.92, math.sqrt(1 - 0.92 ** 2), 0.0])
    c2 = np.array([0.95, -math.sqrt(1 - 0.95 ** 2), 0.0])
    C = np.vstack([E0, c1, c2])
    assert float(c1 @ c2) < 0.90
    assert aspect_vectors.greedy_cover(C, [1.0, 1.0, 0.5], 0.90) == 1


# --- fused / match_pool ------------------------------------------------------------------

QHAT = unit([1.0, 0.2, 0.1])
CENTROID = unit([0.9, 0.3, 0.2])
CHUNK_A = unit([1.0, 0.1, 0.0])
CHUNK_B = unit([0.2, 1.0, 0.3])


def test_fused_is_the_arithmetic_mean_at_equal_weights():
    expected = (float(QHAT @ CHUNK_A) + float(QHAT @ CENTROID)) / 2.0
    assert aspect_vectors.fused(QHAT, CHUNK_A, CENTROID, 0.45, 0.45) == pytest.approx(expected, abs=1e-15)


def test_fused_respects_unequal_weights():
    got = aspect_vectors.fused(QHAT, CHUNK_A, CENTROID, 0.8, 0.2)
    expected = 0.8 * float(QHAT @ CHUNK_A) + 0.2 * float(QHAT @ CENTROID)
    assert got == pytest.approx(expected, abs=1e-15)


def recorded_for(chunks, qhat=QHAT, centroid=CENTROID):
    return [aspect_vectors.fused(qhat, c, centroid, 0.45, 0.45) for c in chunks]


def test_match_pool_recovers_chunk_identity_from_an_exact_reproduction():
    chunks = np.vstack([CHUNK_A, CHUNK_B])
    matched, unmatched, ambiguous = aspect_vectors.match_pool(
        recorded_for(chunks)[::-1], chunks, CENTROID, QHAT, 0.45, 0.45)
    assert (matched, unmatched, ambiguous) == ([1, 0], 0, 0)


def test_match_pool_reports_an_ambiguous_row_and_contributes_nothing_to_C():
    # Two candidates that reproduce the same score: the row is ambiguous and is dropped, so C
    # never receives a chunk the evidence cannot name.
    chunks = np.vstack([CHUNK_A, CHUNK_A])
    matched, unmatched, ambiguous = aspect_vectors.match_pool(
        [recorded_for(chunks)[0]], chunks, CENTROID, QHAT, 0.45, 0.45)
    assert ambiguous >= 1
    assert matched == []
    assert unmatched == 0


def test_match_pool_reports_an_unmatched_row():
    chunks = np.vstack([CHUNK_A, CHUNK_B])
    matched, unmatched, ambiguous = aspect_vectors.match_pool(
        [0.123456], chunks, CENTROID, QHAT, 0.45, 0.45)
    assert (matched, unmatched, ambiguous) == ([], 1, 0)


def test_match_pool_stays_within_the_tolerance_budget():
    chunks = np.vstack([CHUNK_A, CHUNK_B])
    just_inside = recorded_for(chunks)[0] + aspect_vectors.MATCH_TOLERANCE * 0.9
    just_outside = recorded_for(chunks)[0] + aspect_vectors.MATCH_TOLERANCE * 1.1
    assert aspect_vectors.match_pool([just_inside], chunks, CENTROID, QHAT, 0.45, 0.45)[1] == 0
    assert aspect_vectors.match_pool([just_outside], chunks, CENTROID, QHAT, 0.45, 0.45)[1] == 1


def test_match_pool_can_assign_two_recorded_rows_to_one_chunk():
    # Why main() checks len(matched) != len(set(matched)): two recorded scores inside one
    # chunk's tolerance window each match it uniquely, and match_pool's own counters stay at
    # zero. Left unchecked, C would carry the same vector twice and n_chunks would overcount.
    chunks = np.vstack([CHUNK_A, CHUNK_B])
    score = recorded_for(chunks)[0]
    matched, unmatched, ambiguous = aspect_vectors.match_pool(
        [score, score + aspect_vectors.MATCH_TOLERANCE * 0.5], chunks, CENTROID, QHAT, 0.45, 0.45)
    assert (unmatched, ambiguous) == (0, 0)
    assert len(matched) != len(set(matched))


# --- Population joins --------------------------------------------------------------------

def test_build_population_keeps_only_nugget_judged_documents_in_run_order():
    run = {"q1": ["d3", "d1", "d2"], "q2": ["d9"]}
    nuggets = {"q1": {"d1": {"n1", "n2"}, "d3": {"n1"}}}
    assert aspect_vectors.build_population(run, nuggets) == [("q1", "d3", 1), ("q1", "d1", 2)]


def test_invert_keymap_rejects_two_parents_claiming_one_document():
    assert aspect_vectors.invert_keymap({"pA": "d1", "pB": "d2"}) == {"d1": "pA", "d2": "pB"}
    with pytest.raises(SystemExit):
        aspect_vectors.invert_keymap({"pA": "d1", "pB": "d1"})


def test_hits_by_group_restricts_to_the_screened_groups_and_keeps_dump_order():
    rows = [("q1", "pA", 0.9), ("q1", "pB", 0.8), ("q1", "pA", 0.7), ("q2", "pA", 0.6)]
    assert aspect_vectors.hits_by_group(rows, {("q1", "pA")}) == {("q1", "pA"): [0.9, 0.7]}


def test_population_note_warns_when_the_population_is_not_the_preregistered_one():
    exact = aspect_vectors.population_note(
        aspect_vectors.EXPECTED_PAIRS, aspect_vectors.EXPECTED_QUERIES, aspect_vectors.EXPECTED_ROWS)
    assert "WARNING" not in exact
    assert "WARNING" in aspect_vectors.population_note(4360, 653, 12000)


# --- Preconditions (§3.1 E1-E6) ----------------------------------------------------------

def collection_info(points, vector_name, size=768, distance="Cosine"):
    return {"points_count": points,
            "config": {"params": {"vectors": {vector_name: {"size": size, "distance": distance}}}}}


def good_inputs():
    return {
        "chunk_info": collection_info(aspect_vectors.EXPECTED_CHUNK_POINTS, "body_vector"),
        "object_info": collection_info(aspect_vectors.EXPECTED_OBJECT_POINTS, "body_centroid"),
        "chunk_point": {"id": 1, "payload": {"parent_id": "pA", "chunk_index": "0"},
                        "vector": {"body_vector": [0.0] * 768}},
        "object_point": {"id": 2, "payload": {"key": "pA", "docId": "d1"},
                         "vector": {"body_centroid": [0.0] * 768}},
        "keymap_keys": {"pA"},
    }


def test_preconditions_hold_on_the_expected_collections():
    assert aspect_vectors.precondition_failures(**good_inputs()) == []


def test_precondition_e1_catches_a_short_collection():
    args = good_inputs()
    args["chunk_info"] = collection_info(18_000, "body_vector")
    assert any(f.startswith("E1") for f in aspect_vectors.precondition_failures(**args))


def test_precondition_e2_catches_the_wrong_dimension():
    args = good_inputs()
    args["chunk_info"] = collection_info(aspect_vectors.EXPECTED_CHUNK_POINTS, "body_vector", size=384)
    assert any(f.startswith("E2/E3") for f in aspect_vectors.precondition_failures(**args))


def test_precondition_e3_catches_a_missing_centroid_vector():
    args = good_inputs()
    args["object_info"] = collection_info(aspect_vectors.EXPECTED_OBJECT_POINTS, "body_vector")
    assert any("body_centroid" in f for f in aspect_vectors.precondition_failures(**args))


def test_precondition_e4_catches_a_parent_id_the_key_map_does_not_carry():
    args = good_inputs()
    args["chunk_point"]["payload"]["parent_id"] = "not-a-key"
    assert any(f.startswith("E4") for f in aspect_vectors.precondition_failures(**args))


def test_precondition_e5_catches_a_non_cosine_metric():
    args = good_inputs()
    args["object_info"] = collection_info(
        aspect_vectors.EXPECTED_OBJECT_POINTS, "body_centroid", distance="Dot")
    assert any(f.startswith("E5") for f in aspect_vectors.precondition_failures(**args))


def test_precondition_e6_catches_a_scroll_that_returned_no_vector():
    args = good_inputs()
    args["object_point"] = {"id": 2, "payload": {"key": "pA"}, "vector": {}}
    assert any(f.startswith("E6") for f in aspect_vectors.precondition_failures(**args))


def test_precondition_e1_catches_a_missing_collection():
    args = good_inputs()
    args["chunk_info"] = None
    assert aspect_vectors.precondition_failures(**args)[0].startswith("E1")


# --- The query-clustered bootstrap (Global Constraint 4) ---------------------------------

QUERY_IDS = ["qA", "qA", "qA", "qB", "qB", "qC"]   # deliberately unequal pair counts


def test_pairs_by_query_blocks_each_query_in_first_appearance_order():
    order, blocks = aspect_vectors.pairs_by_query(QUERY_IDS)
    assert order == ["qA", "qB", "qC"]
    assert blocks["qA"].tolist() == [0, 1, 2]
    assert blocks["qB"].tolist() == [3, 4]
    assert blocks["qC"].tolist() == [5]


def test_every_resample_is_a_union_of_whole_queries():
    """The falsifiable test for Global Constraint 4: pairs within a query share a query vector,
    so a resample must move them together. If resampling ever drew pairs, some resample would
    hold 2 of qA's 3 indices -- this asserts that never happens, over 500 draws."""
    order, blocks = aspect_vectors.pairs_by_query(QUERY_IDS)
    rng = np.random.default_rng(7)
    for _ in range(500):
        idx = aspect_vectors.resample_pair_indices(order, blocks, rng)
        counts = {i: int(np.count_nonzero(idx == i)) for i in range(len(QUERY_IDS))}
        for query_id in order:
            members = blocks[query_id].tolist()
            multiplicities = {counts[i] for i in members}
            assert len(multiplicities) == 1, (
                f"query {query_id} was split: its pairs appear {multiplicities} times in one resample")
        # Exactly len(order) queries are drawn, so the resample's size is the sum of the drawn
        # queries' block sizes -- never a fixed pair count.
        assert sum(counts.values()) == len(idx)
        assert len(idx) == sum(counts[blocks[q][0]] * len(blocks[q]) for q in order)


def test_resample_draws_one_query_per_unit_never_one_pair():
    order, blocks = aspect_vectors.pairs_by_query(QUERY_IDS)
    rng = np.random.default_rng(11)
    sizes = {len(aspect_vectors.resample_pair_indices(order, blocks, rng)) for _ in range(200)}
    # 3 draws from {3, 2, 1}-sized blocks: every attainable size is a sum of three block sizes.
    attainable = {a + b + c for a in (3, 2, 1) for b in (3, 2, 1) for c in (3, 2, 1)}
    assert sizes <= attainable


def synthetic_rows(n_queries=14, pairs_per_query=3, spread_beats_count=True):
    """A population where residual_spread tracks the aspect count and n_chunks does not."""
    rows = []
    for q in range(n_queries):
        for p in range(pairs_per_query):
            aspects = 1 + ((q + p) % 4)
            rows.append({
                "query_id": f"q{q}",
                "doc_id": f"q{q}d{p}",
                "aspects": aspects,
                "n_chunks": 1 + ((q * 7 + p * 3) % 4),
                "residual_spread": (aspects / 10.0 if spread_beats_count else (p % 3) / 10.0),
                "covers": {tau: 1 + (p % 3) for tau in aspect_vectors.TAUS},
                "effective_rank": 1.0 + (p % 2),
            })
    return rows


def test_the_bootstrap_reproduces_exactly_under_a_fixed_seed():
    rows = synthetic_rows()
    matrix = aspect_vectors.signal_matrix(rows, aspect_vectors.TAU_PRIMARY)
    query_ids = [r["query_id"] for r in rows]
    first = aspect_vectors.bootstrap_differences(matrix, query_ids, n_resamples=100, seed=20260913)
    second = aspect_vectors.bootstrap_differences(matrix, query_ids, n_resamples=100, seed=20260913)
    np.testing.assert_array_equal(first[0], second[0])
    np.testing.assert_array_equal(first[1], second[1])
    other = aspect_vectors.bootstrap_differences(matrix, query_ids, n_resamples=100, seed=1)
    assert not np.array_equal(first[0], other[0])


def test_the_bootstrap_difference_is_candidate_minus_null_within_a_resample():
    rows = synthetic_rows()
    matrix = aspect_vectors.signal_matrix(rows, aspect_vectors.TAU_PRIMARY)
    draws, diffs = aspect_vectors.bootstrap_differences(
        matrix, [r["query_id"] for r in rows], n_resamples=25, seed=3)
    assert draws.shape == (25, 4) and diffs.shape == (25, 3)
    np.testing.assert_allclose(diffs, draws[:, :3] - draws[:, [3]])


def test_bootstrap_p_is_two_sided_and_never_zero():
    assert aspect_vectors.bootstrap_p(np.full(999, 0.4)) == pytest.approx(2.0 / 1000.0)
    assert aspect_vectors.bootstrap_p(np.full(999, -0.4)) == pytest.approx(2.0 / 1000.0)
    assert aspect_vectors.bootstrap_p(np.concatenate([np.full(500, 0.4), np.full(499, -0.4)])) > 0.9


def test_spearman_against_first_reports_each_column_against_the_target():
    matrix = np.column_stack([[1.0, 2, 3, 4], [1.0, 2, 3, 4], [4.0, 3, 2, 1]])
    np.testing.assert_allclose(aspect_vectors.spearman_against_first(matrix), [1.0, -1.0])


def test_analyse_finds_a_candidate_that_beats_the_null():
    rows = synthetic_rows()
    result = aspect_vectors.analyse(rows, aspect_vectors.TAU_PRIMARY, n_resamples=400, seed=5)
    name = aspect_vectors.CANDIDATE_NAMES[0]      # residual_spread, built to track aspects
    assert result["rho"][name] > result["rho_null"]
    assert result["diff"][name] > 0.0
    assert result["ci"][name][0] > 0.0
    adjusted = aspect_vectors.holm_family([result["p"][n] for n in aspect_vectors.CANDIDATE_NAMES])
    assert aspect_vectors.verdict_line(result, name, adjusted[0]) == "PASS"


def test_verdict_fails_when_the_ci_straddles_zero():
    result = {"diff": {"x": 0.1}, "ci": {"x": (-0.01, 0.2)}}
    assert aspect_vectors.verdict_line(result, "x", 0.001) == "FAIL"


def test_verdict_fails_when_holm_p_is_not_significant():
    result = {"diff": {"x": 0.1}, "ci": {"x": (0.01, 0.2)}}
    assert aspect_vectors.verdict_line(result, "x", 0.06) == "FAIL"


def test_rho_curve_covers_every_preregistered_tau():
    curve = aspect_vectors.rho_curve(synthetic_rows())
    assert set(curve) == set(aspect_vectors.TAUS)
    assert all(np.isfinite(v) for v in curve.values())


# --- Holm delegation ---------------------------------------------------------------------

def test_holm_family_returns_report_holm_adjusts_own_output():
    pvalues = [0.01, 0.04, 0.03]
    assert aspect_vectors.holm_family(pvalues) == report.holm_adjust(pvalues)


def test_holm_family_delegates_rather_than_reimplementing(monkeypatch):
    sentinel = ["delegated", "to", "report"]
    monkeypatch.setattr(aspect_vectors.report, "holm_adjust", lambda p: sentinel)
    assert aspect_vectors.holm_family([0.1, 0.2, 0.3]) is sentinel


def test_holm_family_refuses_a_family_that_is_not_the_preregistered_three():
    with pytest.raises(SystemExit):
        aspect_vectors.holm_family([0.01, 0.02])


# --- Output files ------------------------------------------------------------------------

def test_pair_signals_carries_one_greedy_cover_column_per_tau(tmp_path):
    path = tmp_path / "pair-signals.tsv"
    aspect_vectors.write_pair_signals(str(path), synthetic_rows(n_queries=2, pairs_per_query=2))
    lines = path.read_text(encoding="utf-8").strip().split("\n")
    header = lines[0].split("\t")
    assert header[:5] == ["queryId", "docId", "aspects", "n_chunks", "residual_spread"]
    assert [h for h in header if h.startswith("greedy_cover")] == [
        f"greedy_cover_tau{tau:.2f}" for tau in aspect_vectors.TAUS]
    assert header[-1] == "effective_rank"
    assert len(lines) == 5
    assert all(len(line.split("\t")) == len(header) for line in lines[1:])


def test_faithfulness_names_the_falsifying_statistics_and_the_artifact(tmp_path):
    path = tmp_path / "faithfulness.txt"
    aspect_vectors.write_faithfulness(str(path), 9184, 0, 0, 0, 3.1e-8, 2733, 610, [])
    text = path.read_text(encoding="utf-8")
    assert "verdict: PASS" in text
    assert re.search(r"rows checked\s+: 9184", text)
    assert "falsifying statistics" in text
    assert "selection artifact" in text
    assert "WARNING" not in text


def test_faithfulness_fails_and_lists_the_offending_groups(tmp_path):
    path = tmp_path / "faithfulness.txt"
    aspect_vectors.write_faithfulness(str(path), 10, 1, 2, 0, None, 3, 2, ["q1 / d1: 1 unmatched"])
    text = path.read_text(encoding="utf-8")
    assert "verdict: FAIL" in text
    assert "q1 / d1: 1 unmatched" in text
    assert "n/a" in text


def test_screen_publishes_the_rho_curve_and_the_holm_column(tmp_path):
    rows = synthetic_rows()
    primary = aspect_vectors.analyse(rows, aspect_vectors.TAU_PRIMARY, n_resamples=200, seed=5)
    sensitivity = aspect_vectors.analyse(
        [r for r in rows if r["aspects"] >= 2 and r["n_chunks"] >= 2],
        aspect_vectors.TAU_PRIMARY, n_resamples=200, seed=5)
    adjusted = aspect_vectors.holm_family([primary["p"][n] for n in aspect_vectors.CANDIDATE_NAMES])
    path = tmp_path / "screen.txt"
    aspect_vectors.write_screen(str(path), primary, sensitivity, adjusted,
                                {"primary": aspect_vectors.rho_curve(rows)}, 9184)
    text = path.read_text(encoding="utf-8")
    for tau in aspect_vectors.TAUS:
        assert f"tau {tau:.2f}" in text
    assert "Holm p" in text
    assert "resamples of QUERIES (never pairs)" in text
    for name in aspect_vectors.CANDIDATE_NAMES:
        assert name in text


# --- pair_row: the seam between the reconstruction and the statistics ---------------------
#
# These cover what used to be main()'s loop body and could only be reached with Qdrant and TEI
# running: Global Constraint 7's realisation, the recorded[k] <-> C[k] alignment greedy_cover
# depends on, and the reaction to a duplicate assignment.

CHUNK_C = unit([0.1, 0.2, 1.0])


def call_pair_row(candidates, recorded, aspects=3, qhat=QHAT, centroid=CENTROID):
    return aspect_vectors.pair_row("q1", "d1", "pA", aspects, qhat, candidates, centroid, recorded)


def test_pair_row_counts_the_pool_not_the_parents_whole_chunk_set():
    """Global Constraint 7: n_chunks is the count of the pair's chunks IN THE POOL. The parent
    here holds three chunks and the pool recorded two, so an implementation that counted
    `candidates` would answer 3 and fail this test."""
    candidates = np.vstack([CHUNK_A, CHUNK_B, CHUNK_C])
    pooled = np.vstack([CHUNK_A, CHUNK_C])
    result = call_pair_row(candidates, recorded_for(pooled))
    assert len(candidates) == 3
    assert result["row"]["n_chunks"] == 2 != len(candidates)
    # C is the matched subset, not the parent's chunk set: both continuous signals agree with the
    # two pooled chunks alone and not with all three.
    assert result["row"]["effective_rank"] == pytest.approx(aspect_vectors.effective_rank(pooled))
    assert result["row"]["effective_rank"] != pytest.approx(aspect_vectors.effective_rank(candidates))
    assert result["row"]["residual_spread"] == pytest.approx(
        aspect_vectors.residual_spread(pooled, QHAT))
    assert (result["recon_failure"], result["signal_failure"]) == (None, None)
    assert result["max_residual"] == pytest.approx(0.0, abs=1e-15)


def test_pair_row_fails_the_pair_on_a_duplicate_assignment():
    """Two recorded rows inside one chunk's tolerance window each match it uniquely, so
    match_pool's own counters both stay at zero. pair_row must still refuse the pair: C would
    otherwise carry that vector twice and n_chunks would overcount."""
    candidates = np.vstack([CHUNK_A, CHUNK_B])
    score = recorded_for(candidates)[0]
    result = call_pair_row(candidates, [score, score + aspect_vectors.MATCH_TOLERANCE * 0.5])
    assert (result["n_unmatched"], result["n_ambiguous"]) == (0, 0)
    assert result["n_duplicated"] == 1
    assert result["row"] is None
    assert result["max_residual"] is None
    assert "duplicate assignment" in result["recon_failure"]


def test_pair_row_fails_the_pair_on_an_unmatched_or_ambiguous_row():
    candidates = np.vstack([CHUNK_A, CHUNK_B])
    unmatched = call_pair_row(candidates, [0.123456])
    assert unmatched["n_unmatched"] == 1 and unmatched["row"] is None
    assert "1 unmatched" in unmatched["recon_failure"]
    twins = np.vstack([CHUNK_A, CHUNK_A])
    ambiguous = call_pair_row(twins, [recorded_for(twins)[0]])
    assert ambiguous["n_ambiguous"] == 1 and ambiguous["row"] is None


# The alignment fixture: three coplanar chunks, and a query vector OUT of their plane so no
# residual vanishes. cos(c0,c1) = 0.92, cos(c0,c2) = 0.95, cos(c1,c2) = 0.75, and the query
# ranks them c0 > c2 > c1. Walking from c0 covers everything at tau = 0.90 (both others are
# above it); walking from either of the other two admits a second chunk. The cover is therefore
# 1 only if the walk starts at the highest-SCORING chunk -- which is true only if C's rows and
# the recorded scores name the same chunks in the same order.
ALIGN_C0 = np.array([1.0, 0.0, 0.0])
ALIGN_C1 = np.array([0.92, math.sqrt(1 - 0.92 ** 2), 0.0])
ALIGN_C2 = np.array([0.95, -math.sqrt(1 - 0.95 ** 2), 0.0])
ALIGN_CHUNKS = np.vstack([ALIGN_C0, ALIGN_C1, ALIGN_C2])
ALIGN_QHAT = unit([1.0, 0.0, 1.0])


def test_the_alignment_fixture_has_the_geometry_the_next_test_assumes():
    assert float(ALIGN_C0 @ ALIGN_C1) == pytest.approx(0.92)
    assert float(ALIGN_C0 @ ALIGN_C2) == pytest.approx(0.95)
    assert float(ALIGN_C1 @ ALIGN_C2) == pytest.approx(0.7516, abs=1e-4)
    scores = [float(ALIGN_QHAT @ c) for c in ALIGN_CHUNKS]
    assert scores[0] > scores[2] > scores[1]


def test_pair_row_keeps_C_aligned_with_the_recorded_scores_under_any_dump_order():
    """greedy_cover walks the pool in descending RECORDED score, so C[k] must be the chunk that
    reproduced recorded[k]. Feeding the same three hits in two different dump orders must give
    the identical row -- and the expected cover is derived from the geometry, not from the code:
    at tau = 0.90 the highest-scoring chunk c0 covers both others (0.92 and 0.95 are above tau),
    so the answer is 1; at tau = 0.95 c1 clears it at 0.92 and the answer is 2. A C indexed in
    the parent's chunk order instead would start the walk at the wrong chunk and answer 2 at
    tau = 0.90."""
    scores = recorded_for(ALIGN_CHUNKS, qhat=ALIGN_QHAT)
    in_order = call_pair_row(ALIGN_CHUNKS, scores, qhat=ALIGN_QHAT)
    permuted = call_pair_row(ALIGN_CHUNKS, [scores[1], scores[2], scores[0]], qhat=ALIGN_QHAT)

    for result in (in_order, permuted):
        assert result["recon_failure"] is None
        assert result["row"]["n_chunks"] == 3
        assert result["row"]["covers"][0.80] == 1
        assert result["row"]["covers"][0.90] == 1
        assert result["row"]["covers"][0.95] == 2
    assert in_order["row"]["covers"] == permuted["row"]["covers"]
    assert in_order["row"]["residual_spread"] == pytest.approx(permuted["row"]["residual_spread"])
    assert in_order["row"]["effective_rank"] == pytest.approx(permuted["row"]["effective_rank"])
