"""pytest suite for summarize_teacher_attempts.py (spec
docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md). Run with:

    python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_summarize_teacher_attempts.py -q

No non-stdlib imports beyond pytest -- nothing needs PYTHONPATH."""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import summarize_teacher_attempts as sta  # noqa: E402


def write_ledger(path, records):
    with open(path, "w", encoding="utf-8") as f:
        for record in records:
            f.write(json.dumps(record) + "\n")
    return str(path)


def record(query_id, status, reason=None, pass_id="p1"):
    return {
        "query_id": query_id,
        "status": status,
        "content": "...",
        "order": None,
        "reason": reason,
        "pass": pass_id,
    }


def test_query_accepted_on_first_entry(tmp_path):
    path = write_ledger(tmp_path / "a.jsonl", [record("1", "accepted")])
    rows = sta.summarize_ledger(path)
    assert rows == [("1", 1, "accepted", [])]


def test_query_exhausted_after_four_rejected(tmp_path):
    records = [record("1", "rejected", reason=f"reason {i}") for i in range(4)]
    path = write_ledger(tmp_path / "a.jsonl", records)
    rows = sta.summarize_ledger(path)
    assert rows == [("1", 4, "exhausted", ["reason 0", "reason 1", "reason 2", "reason 3"])]


def test_mixed_failure_reasons(tmp_path):
    records = [
        record("1", "rejected", reason="finish_reason=length"),
        record("1", "rejected", reason="finish_reason=length"),
        record("1", "rejected", reason="not a permutation: missing ids"),
        record("1", "rejected", reason="not a permutation: missing ids"),
    ]
    path = write_ledger(tmp_path / "a.jsonl", records)
    rows = sta.summarize_ledger(path)
    assert rows[0][2] == "exhausted"
    assert rows[0][3] == [
        "finish_reason=length",
        "finish_reason=length",
        "not a permutation: missing ids",
        "not a permutation: missing ids",
    ]


def test_multiple_files_produce_one_row_per_file_per_query(tmp_path):
    path_a = write_ledger(tmp_path / "a.jsonl", [record("1", "accepted")])
    path_b = write_ledger(tmp_path / "b.jsonl", [record("2", "rejected", reason="x")])
    table = sta.format_table(
        [(path_a, sta.summarize_ledger(path_a)), (path_b, sta.summarize_ledger(path_b))]
    )
    assert "a.jsonl" in table and "1" in table
    assert "b.jsonl" in table and "2" in table
