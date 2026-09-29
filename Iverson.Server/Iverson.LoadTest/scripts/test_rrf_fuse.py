"""pytest suite for rrf_fuse.py (spec docs/specs/2026-09-28-matchpattern-rrf-test-design.md, Phase 3
and the degeneracy guard). Run with:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \\
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_rrf_fuse.py -q

PYTHONPATH is needed: the INERT tests run report.check_pool (ir_measures) and the guard tests run
scipy.stats.spearmanr. Every fixture is hand-computable."""
import json
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rrf_fuse  # noqa: E402


def write_run(path, rows, tag="t"):
    """rows: [(qid, [docid, ...])] -- ranks 1..n in list order, scores n..1."""
    with open(path, "w", encoding="utf-8") as f:
        for qid, docids in rows:
            for i, docid in enumerate(docids):
                f.write(f"{qid} Q0 {docid} {i + 1} {float(len(docids) - i):.6f} {tag}\n")


# ── RRF arithmetic ──────────────────────────────────────────────────────────────────────

def test_rrf_score_sums_both_reciprocal_ranks_at_k_60():
    assert rrf_fuse.rrf_score(1, 3) == 1 / 61 + 1 / 63


def test_rrf_score_omits_the_pattern_term_when_the_candidate_has_no_pattern_rank():
    """Absent, not zero-ranked and not floored: exactly 1/(60 + rank_b)."""
    assert rrf_fuse.rrf_score(4, None) == 1 / 64


def test_fuse_query_orders_by_rrf_and_uses_the_missing_branch_for_unmatched_candidates():
    # baseline a,b,c,d; pattern ranks d first, b second; a and c have no run.
    fused = rrf_fuse.fuse_query(["a", "b", "c", "d"], ["d", "b"])
    expected = {"a": 1 / 61, "b": 1 / 62 + 1 / 62, "c": 1 / 63, "d": 1 / 64 + 1 / 61}
    assert dict(fused) == expected
    assert [d for d, _ in fused] == ["b", "d", "a", "c"]


def test_fuse_query_refuses_a_pattern_document_that_is_not_a_baseline_candidate():
    with pytest.raises(SystemExit):
        rrf_fuse.fuse_query(["a", "b"], ["z"])


# ── swapped ranks: strict order, strictly decreasing written score ──────────────────────

def test_swapped_ranks_tie_exactly_and_the_baseline_rank_breaks_the_tie(tmp_path):
    """Baseline rank 3 / pattern rank 7 versus baseline rank 7 / pattern rank 3: the RRF values are
    bit-identical, so only the baseline-rank tie-break makes the order strict. Doc ids run AGAINST
    baseline rank ("z3" is rank 3, "a7" rank 7), so a doc-id tie-break -- what ir_measures does --
    would put a7 first and fail here."""
    baseline = ["z1", "y2", "z3", "x4", "w5", "v6", "a7", "u8", "t9", "s10"]
    pattern = ["u8", "t9", "a7", "s10", "z1", "y2", "z3"]  # a7 at pattern rank 3, z3 at pattern rank 7
    fused = rrf_fuse.fuse_query(baseline, pattern)
    score = dict(fused)
    assert score["z3"] == score["a7"]                      # 1/63 + 1/67 == 1/67 + 1/63
    order = [d for d, _ in fused]
    assert order.index("z3") == order.index("a7") - 1      # adjacent, baseline rank 3 first

    path = tmp_path / "fused.chunks.trec"
    rrf_fuse.write_fused_run(str(path), {"q1": fused}, ["q1"], "tag")
    rows = [line.split() for line in path.read_text().splitlines()]
    written = [float(r[4]) for r in rows]
    assert [int(r[3]) for r in rows] == list(range(1, 11))
    assert written == [51.0 - rank for rank in range(1, 11)]
    assert all(a > b for a, b in zip(written, written[1:]))


# ── document set equals the baseline's ──────────────────────────────────────────────────

def test_fuse_run_keeps_every_baseline_document_and_no_other(tmp_path):
    baseline = {"q1": ["a", "b", "c"], "q2": ["d", "e", "f"]}
    fused = rrf_fuse.fuse_run(baseline, ["q1", "q2"], {"q1": ["c"]})
    assert {q: sorted(d for d, _ in docs) for q, docs in fused.items()} == \
        {"q1": ["a", "b", "c"], "q2": ["d", "e", "f"]}
    # q2 has no pattern row: every second term is absent, so the baseline order survives.
    assert [d for d, _ in fused["q2"]] == ["d", "e", "f"]


def test_fuse_run_refuses_a_pattern_query_absent_from_the_baseline():
    with pytest.raises(SystemExit):
        rrf_fuse.fuse_run({"q1": ["a"]}, ["q1"], {"q9": ["a"]})


def test_load_ranked_run_refuses_a_rank_column_that_disagrees_with_file_order(tmp_path):
    p = tmp_path / "r.trec"
    p.write_text("q1 Q0 a 2 1.0 t\nq1 Q0 b 1 0.5 t\n", encoding="utf-8")
    with pytest.raises(SystemExit):
        rrf_fuse.load_ranked_run(str(p))


# ── build composite ─────────────────────────────────────────────────────────────────────

PASSES = ("calibration", "p50", "p75", "p90")


def build_json(composite="c1", baseline="c1", overrides=None):
    checks = [{"pass": p, "before": composite, "after": composite} for p in PASSES]
    for index, side, value in overrides or []:
        checks[index][side] = value
    return {"baseline_composite": baseline, "checks": checks}


def test_measured_composite_returns_the_composite_when_every_check_agrees():
    assert rrf_fuse.measured_composite(build_json(), "c1", PASSES) == "c1"


@pytest.mark.parametrize("build, sidecar", [
    (build_json(baseline="c0"), "c1"),                                   # build.json baseline != sidecar
    (build_json(overrides=[(2, "after", "c2")]), "c1"),                  # one check disagrees
    (build_json(overrides=[(0, "before", "c2")]), "c1"),                 # calibration before disagrees
    ({"baseline_composite": "c1", "checks": build_json()["checks"][:3]}, "c1"),  # p90 never checked
    (build_json(), None),                                                # baseline sidecar has no composite
])
def test_measured_composite_aborts_on_any_mismatch_or_missing_check(build, sidecar):
    with pytest.raises(SystemExit):
        rrf_fuse.measured_composite(build, sidecar, PASSES)


def test_write_sidecar_is_found_by_report_and_carries_the_measured_composite(tmp_path):
    run = tmp_path / "fused-p50-run_len.chunks.trec"
    run.write_text("", encoding="utf-8")
    path = rrf_fuse.write_sidecar(str(run), {"configLabel": "base", "composite": "old", "x": 1}, "c9",
                                  "fused-p50-run_len")
    assert path == str(tmp_path / "fused-p50-run_len.meta.json")
    with open(path, encoding="utf-8") as f:
        assert json.load(f) == {"configLabel": "fused-p50-run_len", "composite": "c9", "x": 1}


# ── INERT: both sides of the 25% rule ───────────────────────────────────────────────────

BASE8 = [(f"q{i}", ["a", "b", "c"]) for i in range(8)]


def reorder_first(n):
    return [(q, list(reversed(d)) if i < n else d) for i, (q, d) in enumerate(BASE8)]


def test_an_arm_reordering_exactly_25_percent_is_not_inert(tmp_path):
    base, arm = tmp_path / "b.chunks.trec", tmp_path / "a.chunks.trec"
    write_run(base, BASE8)
    write_run(arm, reorder_first(2))       # 2 of 8 = 25%
    assert rrf_fuse.classify_inert(str(arm), str(base)) == {"reordered_fraction": 0.25, "inert": False}


def test_an_arm_reordering_under_25_percent_is_inert_and_does_not_end_the_run(tmp_path):
    base, arm = tmp_path / "b.chunks.trec", tmp_path / "a.chunks.trec"
    write_run(base, BASE8)
    write_run(arm, reorder_first(1))       # 1 of 8 = 12.5%
    assert rrf_fuse.classify_inert(str(arm), str(base)) == {"reordered_fraction": 0.125, "inert": True}


def test_classify_inert_aborts_when_the_document_set_changed(tmp_path):
    base, arm = tmp_path / "b.chunks.trec", tmp_path / "a.chunks.trec"
    write_run(base, BASE8)
    write_run(arm, [("q0", ["a", "b", "z"])] + reorder_first(8)[1:])
    with pytest.raises(SystemExit):
        rrf_fuse.classify_inert(str(arm), str(base))


# ── Degeneracy guard: the floor, and both sides of the 0.95 threshold ───────────────────

def test_guard_vectors_gives_no_run_candidates_a_floor_strictly_below_every_matched_score():
    baseline = {"q1": ["a", "b", "c"], "q2": ["d", "e"]}
    calibration = {("q1", "a"): (1, 0.5), ("q1", "b"): (2, 0.6), ("q1", "c"): (3, 0.7),
                   ("q2", "d"): (4, 0.8), ("q2", "e"): (5, 0.9)}
    primary, counts, sims = rrf_fuse.guard_vectors(
        baseline, ["q1", "q2"], {("q1", "b"): 2.0, ("q2", "e"): 3.0}, calibration)
    assert primary == [1.0, 2.0, 1.0, 1.0, 3.0]            # floor = min(2, 3) - 1 = 1 < 2
    assert counts == [1, 2, 3, 4, 5]
    assert sims == [0.5, 0.6, 0.7, 0.8, 0.9]


def test_guard_vectors_aborts_on_a_candidate_missing_from_calibration():
    with pytest.raises(SystemExit):
        rrf_fuse.guard_vectors({"q1": ["a", "b"]}, ["q1"], {("q1", "a"): 1.0}, {("q1", "a"): (1, 0.5)})


def test_guard_vectors_aborts_when_the_arm_matched_nothing():
    with pytest.raises(SystemExit):
        rrf_fuse.guard_vectors({"q1": ["a"]}, ["q1"], {}, {("q1", "a"): (1, 0.5)})


def test_rho_at_the_threshold_is_degenerate():
    # 20 points with one adjacent swap: rho = 1 - 6*2/(20*399) = 0.99849...; a count-in-disguise.
    counts = list(range(20))
    primary = list(range(20))
    primary[0], primary[1] = primary[1], primary[0]
    sims = [(i * 7) % 20 for i in range(20)]          # a permutation unrelated to primary
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert result["rho_chunk_count"] >= 0.95
    assert result["degenerate"] is True


def test_rho_just_above_the_threshold_is_degenerate():
    # n = 20, sum(d^2) = 64: rho = 1 - 384/7980 = 0.95188. Swaps 0<->4 (32), 5<->7 (8), 10<->12 (8),
    # 14<->16 (8), 17<->19 (8): total 64. (An exact 0.95 is not reachable: scipy returns
    # 0.9500000000000001 for the n = 9 construction, so the bracket 0.94887 / 0.95188 pins the rule.)
    counts = list(range(20))
    primary = list(range(20))
    for a, b in ((0, 4), (5, 7), (10, 12), (14, 16), (17, 19)):
        primary[a], primary[b] = primary[b], primary[a]
    result = rrf_fuse.degeneracy(primary, counts, [(i * 7) % 20 for i in range(20)])
    assert result["rho_chunk_count"] == pytest.approx(1 - 384 / 7980)
    assert result["degenerate"] is True


def test_rho_just_below_the_threshold_is_not_degenerate():
    # 1 - 6*sum(d^2)/(n(n^2-1)) with n = 20: sum(d^2) = 64 gives 1 - 384/7980 = 0.95188 (>= 0.95),
    # sum(d^2) = 68 gives 1 - 408/7980 = 0.94887 (< 0.95). Build a permutation with sum(d^2) = 68.
    counts = list(range(20))
    primary = list(range(20))
    # swap 0<->4 (d^2 16+16=32), 5<->8 (9+9=18), 10<->13 (9+9=18): total 68
    for a, b in ((0, 4), (5, 8), (10, 13)):
        primary[a], primary[b] = primary[b], primary[a]
    sims = [(i * 7) % 20 for i in range(20)]
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert result["rho_chunk_count"] == pytest.approx(1 - 408 / 7980)
    assert result["rho_chunk_count"] < 0.95
    assert abs(result["rho_max_sim"]) < 0.95
    assert result["degenerate"] is False


def test_a_negative_rho_beyond_the_threshold_is_degenerate_too():
    counts = list(range(20))
    primary = list(reversed(range(20)))
    sims = [(i * 7) % 20 for i in range(20)]
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert result["rho_chunk_count"] == pytest.approx(-1.0)
    assert result["degenerate"] is True


def test_the_max_sim_input_alone_can_mark_an_arm_degenerate():
    primary = list(range(20))
    counts = [(i * 7) % 20 for i in range(20)]
    sims = [i / 20 for i in range(20)]
    result = rrf_fuse.degeneracy(primary, counts, sims)
    assert abs(result["rho_chunk_count"]) < 0.95
    assert result["rho_max_sim"] == pytest.approx(1.0)
    assert result["degenerate"] is True


def test_a_constant_input_aborts_rather_than_reading_as_not_degenerate():
    with pytest.warns(Warning):
        with pytest.raises(SystemExit):
            rrf_fuse.degeneracy([1.0] * 5, [1, 2, 3, 4, 5], [0.1, 0.2, 0.3, 0.4, 0.5])
