"""pytest suite for report.py's paired-statistics section. Run with:

    PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs \
        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_report.py -q

report.py imports ir_measures lazily inside functions, so importing it here needs no PYTHONPATH;
running the paired section does."""
import os
import re
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import report  # noqa: E402


def write_qrels(path, rows):
    """rows: [(qid, docid, rel)]"""
    with open(path, "w", encoding="utf-8") as f:
        for qid, docid, rel in rows:
            f.write(f"{qid} 0 {docid} {rel}\n")


def write_run(path, rows, tag="t"):
    """rows: [(qid, [docid, ...])] -- ranks 1..n, descending scores n..1 like TrecRunWriter."""
    with open(path, "w", encoding="utf-8") as f:
        for qid, docids in rows:
            for i, docid in enumerate(docids):
                f.write(f"{qid} Q0 {docid} {i + 1} {float(len(docids) - i):.6f} {tag}\n")


QRELS = [("q1", "d1", 1), ("q2", "d2", 1), ("q3", "d3", 1), ("q4", "d4", 1)]
BASE = [("q1", ["d1", "d2", "d3"]), ("q2", ["d1", "d2", "d3"]),
        ("q3", ["d3", "d1", "d2"]), ("q4", ["d2", "d4", "d1"])]


@pytest.fixture
def qrels_path(tmp_path):
    p = tmp_path / "qrels.trec"
    write_qrels(p, QRELS)
    return str(p)


def load_qrels(path):
    import ir_measures
    return list(ir_measures.read_trec_qrels(path))


def test_self_comparison_is_zero_delta_on_every_measure(tmp_path, qrels_path, capsys):
    """A run compared to a byte-identical copy must report delta +0.0000 for nDCG@10, R@50 AND AP.
    Against the unfixed script only the first measure does: the baseline generator is consumed by
    nDCG@10, and R@50 / AP are then paired against an all-zero baseline (spec A34)."""
    from ir_measures import AP, R, nDCG
    base = tmp_path / "base.chunks.trec"
    copy = tmp_path / "copy.chunks.trec"
    write_run(base, BASE, tag="base")
    write_run(copy, BASE, tag="copy")

    report.run_paired_statistics(load_qrels(qrels_path), [str(base), str(copy)], str(base),
                                 [nDCG @ 10, R @ 50, AP])

    out = capsys.readouterr().out
    assert len(re.findall(r"delta\s+\+0\.0000", out)) == 3, out


def _three_arm_family(tmp_path):
    """A0 (control), A1 and A2 differ from A0 in ORDER on every query (same sets); A0' is a
    second control with a different doc SET on q4; A3 reorders A0'. Returns paths."""
    a0 = tmp_path / "a0.chunks.trec"
    a1 = tmp_path / "a1.chunks.trec"
    a2 = tmp_path / "a2.chunks.trec"
    a0p = tmp_path / "a0prime.chunks.trec"
    a3 = tmp_path / "a3.chunks.trec"
    reorder = lambda rows: [(q, list(reversed(d))) for q, d in rows]
    write_run(a0, BASE, "a0")
    write_run(a1, reorder(BASE), "a1")
    write_run(a2, [(q, d[1:] + d[:1]) for q, d in BASE], "a2")
    base_prime = BASE[:3] + [("q4", ["d2", "d4", "d5"])]
    write_run(a0p, base_prime, "a0p")
    write_run(a3, reorder(base_prime), "a3")
    return a0, a1, a2, a0p, a3


def test_pair_family_is_exactly_the_declared_pairs(tmp_path, qrels_path, capsys):
    from ir_measures import AP, R, nDCG
    a0, a1, a2, a0p, a3 = _three_arm_family(tmp_path)
    pairs = report.parse_pairs([f"{a1}={a0}", f"{a2}={a0}", f"{a3}={a0p}"])

    report.run_pair_statistics(load_qrels(qrels_path), pairs, [nDCG @ 10, R @ 50, AP])

    out = capsys.readouterr().out
    assert out.count("Holm (3 tests)") == 9, out          # 3 pairs x 3 measures
    assert "a3.chunks.trec  vs  a0prime.chunks.trec" in out
    assert "a3.chunks.trec  vs  a0.chunks.trec" not in out


def test_holm_is_keyed_by_pair_not_run_alone(tmp_path, qrels_path, capsys):
    """Spec M2/R12: a1 appears in two pairs, against two DIFFERENT baselines. Keyed by run path
    alone, holm_by_run collapses to one dict entry per run path, so the LAST pair processed for that
    run silently overwrites the earlier one's adjusted p -- both blocks then print the SAME value,
    rather than each pair's own Holm-adjusted p.

    This is NOT built from _three_arm_family: its a0/a1/a0prime differ only by whole-list reversal
    or rotation, which (empirically) still yields the SAME sign-flip permutation p for both pairs,
    so the bug is invisible in the printed output even though the dict has collapsed. Instead, each
    query's target-relevant doc (qN's relevant doc is dN, per QRELS) is placed at a distinct rank
    per run: last-of-3 in a1 for every query, first-of-3 in a0 for every query (so a1 is uniformly
    worse -- perm_p = 0.1250, an extreme, all-same-sign pattern), but alternating first/last in
    a0prime (so a1 is worse on only 2 of 4 queries -- perm_p = 0.5000, a middling pattern). Doc SETS
    are identical across all three files per query (only rank order changes), so both pairs pass
    check_pool. Verified empirically against both the unfixed and the fixed keying (see the
    implementation report) before landing these two exact expected values."""
    from ir_measures import AP, R, nDCG

    a1_rows = [("q1", ["x", "y", "d1"]), ("q2", ["x", "y", "d2"]),
               ("q3", ["x", "y", "d3"]), ("q4", ["x", "y", "d4"])]
    a0_rows = [("q1", ["d1", "x", "y"]), ("q2", ["d2", "x", "y"]),
               ("q3", ["d3", "x", "y"]), ("q4", ["d4", "x", "y"])]
    a0prime_rows = [("q1", ["y", "x", "d1"]), ("q2", ["d2", "y", "x"]),
                     ("q3", ["y", "x", "d3"]), ("q4", ["d4", "y", "x"])]

    a1 = tmp_path / "a1.chunks.trec"
    a0 = tmp_path / "a0.chunks.trec"
    a0prime = tmp_path / "a0prime.chunks.trec"
    write_run(a1, a1_rows, "a1")
    write_run(a0, a0_rows, "a0")
    write_run(a0prime, a0prime_rows, "a0prime")

    pairs = report.parse_pairs([f"{a1}={a0}", f"{a1}={a0prime}"])
    report.run_pair_statistics(load_qrels(qrels_path), pairs, [nDCG @ 10, R @ 50, AP])

    out = capsys.readouterr().out
    assert out.count("Holm (2 tests)") == 6, out            # 2 pairs x 3 measures
    assert "a1.chunks.trec  vs  a0.chunks.trec" in out
    assert "a1.chunks.trec  vs  a0prime.chunks.trec" in out

    # The regression-catching assertion: each pair's OWN Holm-adjusted p, not the other pair's.
    # Against the unfixed (run-path-only) keying both blocks print 0.5000 (a0prime's, processed
    # last, silently overwrites a0's own 0.2500).
    ndcg_block = re.search(
        r"a1\.chunks\.trec  vs  a0\.chunks\.trec\s+\(nDCG@10\).*?Holm \(2 tests\)\s+p_adj = ([\d.]+)",
        out, re.S)
    assert ndcg_block and ndcg_block.group(1) == "0.2500", out


def test_pool_check_rejects_a_changed_document_set(tmp_path, qrels_path):
    from ir_measures import AP, R, nDCG
    a0, a1, a2, a0p, a3 = _three_arm_family(tmp_path)
    # a3 vs a0 (NOT its own control): q4 holds d5 instead of d1 -- the pool moved.
    with pytest.raises(SystemExit) as e:
        report.run_pair_statistics(load_qrels(qrels_path),
                                   report.parse_pairs([f"{a3}={a0}"]), [nDCG @ 10, R @ 50, AP])
    assert "pool changed" in str(e.value) and "1 of 4" in str(e.value)


def test_pool_check_rejects_too_few_reordered_queries(tmp_path, qrels_path):
    from ir_measures import AP, R, nDCG
    a0 = tmp_path / "a0.chunks.trec"
    a1 = tmp_path / "a1.chunks.trec"
    write_run(a0, BASE, "a0")
    # Identical to a0 on 3 of 4 queries; 25% differ -- exactly at the threshold, so it PASSES...
    write_run(a1, BASE[:3] + [("q4", list(reversed(BASE[3][1])))], "a1")
    report.check_pool(str(a1), str(a0))
    # ...and 0 of 4 differ fails.
    write_run(a1, BASE, "a1")
    with pytest.raises(SystemExit) as e:
        report.check_pool(str(a1), str(a0))
    assert "0 of 4" in str(e.value) and "25" in str(e.value)


def test_pair_and_baseline_are_mutually_exclusive(tmp_path, qrels_path, monkeypatch):
    a0 = tmp_path / "a0.chunks.trec"
    write_run(a0, BASE, "a0")
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(a0), "--qrels", qrels_path,
                                      "--baseline", str(a0), "--pair", f"{a0}={a0}"])
    with pytest.raises(SystemExit) as e:
        report.main()
    assert "--pair" in str(e.value) and "--baseline" in str(e.value)


def test_score_block_is_complete_without_nugget_qrels(tmp_path, qrels_path, capsys, monkeypatch):
    """A run with only q1 covered, scored against the 4-query qrels fixture, must print all
    three base measures present and non-zero -- catching a naive per-measure scoring loop
    (one calc_aggregate call per measure, each re-reading or re-consuming the run) that
    zeroes out every measure after the first. Needs no pyndeval: no --nugget-qrels is passed.

    This does NOT exercise score_run's single-materialisation guarantee end to end: with no
    --nugget-qrels, `nugget_measures` is empty and score_run makes exactly one
    calc_aggregate call regardless of whether `run` was materialised first -- removing the
    `list(...)` in score_run does not fail this test (verified directly: the whole
    non-pyndeval suite in this file still passes with it removed). The two-qrels-group merge
    that materialisation
    actually protects -- base measures against qrels, alpha_nDCG@10 against nugget_qrels,
    from the SAME read of the run -- is exercised only by
    test_alpha_ndcg_appears_only_with_nugget_qrels below, which is pyndeval-gated and skips
    today. That path is unverified until pyndeval is installed."""
    run = tmp_path / "x.similar.trec"
    write_run(run, [("q1", ["d1", "d2"])])
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path])
    report.main()
    out = capsys.readouterr().out
    for line in ("nDCG@10    0.2500", "R@50       0.2500", "AP         0.2500"):
        assert line in out, out


def test_alpha_ndcg_appears_only_with_nugget_qrels(tmp_path, qrels_path, capsys, monkeypatch):
    pytest.importorskip("pyndeval")
    run = tmp_path / "x.similar.trec"
    write_run(run, [("q1", ["d1", "d2"])])
    nugget = tmp_path / "qrels.nugget.trec"
    nugget.write_text("q1 s1 d1 1\nq1 s2 d2 1\n")
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path])
    report.main()
    assert "alpha_nDCG@10" not in capsys.readouterr().out
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path,
                                      "--nugget-qrels", str(nugget)])
    report.main()
    out = capsys.readouterr().out
    # The whole block, so a consumed-generator bug that zeroes any later measure is caught in
    # either call order: nDCG/R/AP score 0.25 on the 4-query fixture, α-nDCG 1.0 (both subtopics
    # covered in the top 2).
    for line in ("nDCG@10    0.2500", "R@50       0.2500", "AP         0.2500", "alpha_nDCG@10 1.0000"):
        assert line in out, line


def test_diversity_means_print_from_sidecar(tmp_path, qrels_path, capsys, monkeypatch):
    run = tmp_path / "y.chunks.trec"
    write_run(run, [("q1", ["d1"])])
    (tmp_path / "y.chunks.diversity.json").write_text(
        '{"label":"y","queries":1,"meanDistinctParentsAt10":7.5,"meanDistinctParentsAt50":31.0,"perQuery":{}}')
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path])
    report.main()
    out = capsys.readouterr().out
    assert "distinct parents @10  7.50" in out and "@50  31.00" in out


def test_pair_statistics_routes_alpha_ndcg_to_nugget_qrels(tmp_path, qrels_path, monkeypatch):
    """--pair + --nugget-qrels must score alpha_nDCG@10 against the nugget qrels, not the
    query-level qrels -- run_pair_statistics is the fourth consumer Task 3 missed (score_run,
    per_query_values and run_paired_statistics were already fixed to route through qrels_for;
    this function's own baseline_values computation, and its call into paired_comparison,
    both used to pass the plain query-level qrels for every measure).

    No pyndeval needed: actually computing alpha_nDCG requires it (not installed on this
    box), so ir_measures.iter_calc is monkeypatched to intercept just the alpha_nDCG@10 call
    and record which qrels OBJECT it was given, by identity, rather than really scoring it.
    Against the unfixed run_pair_statistics (no nugget_qrels parameter, `qrels` used
    unconditionally) this test fails: every recorded qrels object is the query-level `qrels`
    fixture, never `nugget_qrels`."""
    from ir_measures import alpha_nDCG, nDCG
    import ir_measures

    a0, a1, a2, a0p, a3 = _three_arm_family(tmp_path)

    nugget_path = tmp_path / "qrels.nugget.trec"
    nugget_path.write_text(
        "q1 s1 d1 1\nq1 s2 d2 1\nq2 s1 d2 1\nq2 s2 d1 1\n"
        "q3 s1 d3 1\nq3 s2 d1 1\nq4 s1 d4 1\nq4 s2 d2 1\n"
    )
    qrels = load_qrels(qrels_path)
    nugget_qrels = load_qrels(str(nugget_path))

    real_iter_calc = ir_measures.iter_calc
    seen_qrels = []

    def fake_iter_calc(measures, used_qrels, run):
        if len(measures) == 1 and str(measures[0]).startswith("alpha_nDCG"):
            seen_qrels.append(used_qrels)
            run_list = list(run)
            query_ids = sorted({row.query_id for row in run_list})
            return [ir_measures.Metric(query_id=qid, measure=measures[0], value=1.0) for qid in query_ids]
        return real_iter_calc(measures, used_qrels, run)

    monkeypatch.setattr(ir_measures, "iter_calc", fake_iter_calc)

    pairs = report.parse_pairs([f"{a1}={a0}"])
    report.run_pair_statistics(qrels, pairs, [nDCG @ 10, alpha_nDCG @ 10], nugget_qrels)

    assert seen_qrels, "alpha_nDCG@10 was never scored -- fake_iter_calc was never hit"
    assert all(used is nugget_qrels for used in seen_qrels), (
        "alpha_nDCG@10 must be routed to nugget_qrels, not the query-level qrels; "
        f"saw {sum(1 for u in seen_qrels if u is qrels)} of {len(seen_qrels)} calls routed "
        "to the wrong (query-level) qrels object"
    )


def test_nugget_qrels_with_no_subtopic_structure_is_refused(tmp_path, qrels_path, monkeypatch):
    """A --nugget-qrels file where every row shares one iteration value looks exactly like a
    query-level qrels file -- the operator's likeliest mistake is swapping --qrels and
    --nugget-qrels, one path segment apart in a typical run directory. Refused before any
    scoring, naming both flags."""
    run = tmp_path / "x.similar.trec"
    write_run(run, [("q1", ["d1", "d2"])])
    bad_nugget = tmp_path / "qrels.nugget.trec"
    write_qrels(bad_nugget, QRELS)  # iteration column is "0" on every row -- query-level, not nugget
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path,
                                      "--nugget-qrels", str(bad_nugget)])
    with pytest.raises(SystemExit) as e:
        report.main()
    message = str(e.value)
    assert "--nugget-qrels" in message and "--qrels" in message


def test_nugget_qrels_with_subtopic_structure_is_accepted(tmp_path, qrels_path, monkeypatch):
    """The mirror image of the refusal test: a genuine nugget file (more than one iteration
    value across its rows) must not trip the structural check. Guarded on pyndeval since this
    exercises the real scoring path through main(), not just the check itself."""
    pytest.importorskip("pyndeval")
    run = tmp_path / "x.similar.trec"
    write_run(run, [("q1", ["d1", "d2"])])
    good_nugget = tmp_path / "qrels.nugget.trec"
    good_nugget.write_text("q1 s1 d1 1\nq1 s2 d2 1\n")
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path,
                                      "--nugget-qrels", str(good_nugget)])
    report.main()  # must not raise


def test_diversity_means_omitted_without_crashing_when_sidecar_missing_a_key(tmp_path, qrels_path, capsys, monkeypatch):
    """A sidecar that parses as JSON but is missing one of the two mean keys must not crash
    the whole report.py run -- print_scores must not destructure and format a None with
    :.2f. Unreachable via Task 2's writer today (it always emits both keys), but
    load_diversity_means's own docstring promises the same tolerance load_build_composite
    gives a corrupt build sidecar, so this pins the chosen behaviour: omit both lines rather
    than print a partial pair."""
    run = tmp_path / "y.chunks.trec"
    write_run(run, [("q1", ["d1"])])
    (tmp_path / "y.chunks.diversity.json").write_text(
        '{"label":"y","queries":1,"meanDistinctParentsAt10":7.5,"perQuery":{}}')
    monkeypatch.setattr(sys, "argv", ["report.py", "--run", str(run), "--qrels", qrels_path])
    report.main()
    out = capsys.readouterr().out
    assert "distinct parents" not in out
