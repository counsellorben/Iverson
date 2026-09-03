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
