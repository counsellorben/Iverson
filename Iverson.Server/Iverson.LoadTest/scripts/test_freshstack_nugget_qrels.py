"""pytest suite for freshstack_nugget_qrels.py's pure functions and CLI. Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_freshstack_nugget_qrels.py -q

Stdlib only -- no PYTHONPATH needed, unlike report.py's suite."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import freshstack_nugget_qrels  # noqa: E402


def test_filter_keeps_only_rows_inside_the_slice():
    rows = [("q1", "q1_0", "dA", 1), ("q1", "q1_1", "dZ", 1), ("q9", "q9_0", "dA", 1), ("q1", "q1_0", "dB", 0)]
    kept = list(freshstack_nugget_qrels.filter_rows(rows, {"q1"}, {"dA", "dB"}))
    assert kept == [("q1", "q1_0", "dA", 1), ("q1", "q1_0", "dB", 0)]   # rel 0 kept; dZ and q9 dropped


def test_main_exits_when_a_slice_query_has_no_judged_doc(tmp_path, monkeypatch):
    (tmp_path / "beir").mkdir(); (tmp_path / "t" / "freshstack").mkdir(parents=True)
    (tmp_path / "beir" / "queries.jsonl").write_text('{"_id": "q1", "text": "x"}\n{"_id": "q2", "text": "y"}\n')
    (tmp_path / "beir" / "corpus.jsonl").write_text('{"_id": "dA", "text": "a"}\n')
    (tmp_path / "t" / "freshstack" / "qrels.tsv").write_text("q1\tq1_0\tdA\t1\n")
    monkeypatch.setattr(sys, "argv", ["x", "--slice", str(tmp_path / "beir"), "--topics-root", str(tmp_path), "--out", str(tmp_path / "out.trec")])
    with pytest.raises(SystemExit) as e:
        freshstack_nugget_qrels.main()
    assert "q2" in str(e.value)


def test_main_writes_trec_rows_with_nugget_id_in_iteration_column(tmp_path, monkeypatch):
    (tmp_path / "beir").mkdir(); (tmp_path / "t" / "freshstack").mkdir(parents=True)
    (tmp_path / "beir" / "queries.jsonl").write_text('{"_id": "q1", "text": "x"}\n')
    (tmp_path / "beir" / "corpus.jsonl").write_text('{"_id": "dA", "text": "a"}\n{"_id": "dB", "text": "b"}\n')
    (tmp_path / "t" / "freshstack" / "qrels.tsv").write_text(
        "q1\tq1_0\tdA\t1\nq1\tq1_0\tdB\t0\nq1\tq1_1\tdZ\t1\n"
    )
    out = tmp_path / "out.trec"
    monkeypatch.setattr(sys, "argv", ["x", "--slice", str(tmp_path / "beir"), "--topics-root", str(tmp_path), "--out", str(out)])
    freshstack_nugget_qrels.main()
    assert out.read_text().splitlines() == ["q1 q1_0 dA 1", "q1 q1_0 dB 0"]
