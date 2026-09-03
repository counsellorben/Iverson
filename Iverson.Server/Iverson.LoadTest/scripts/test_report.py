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
