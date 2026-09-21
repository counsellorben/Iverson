"""Summarizes one or more teacher_rerank.py --responses ledger files into a per-query
attempt-count and outcome table, to inform the follow-on retry/budget decision (spec
docs/specs/2026-09-21-teacher-ceiling-attempt-measurement-design.md). Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/summarize_teacher_attempts.py <ledger.jsonl> [<ledger.jsonl> ...]

No non-stdlib imports."""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import teacher_rerank as tr  # noqa: E402


def summarize_ledger(path):
    """Returns [(query_id, attempt_count, status, reasons), ...] for one ledger file, in
    `read_responses_ledger`'s dict insertion order. `reasons` is empty for an accepted query --
    only exhausted queries carry their rejected attempts' reasons."""
    ledger = tr.read_responses_ledger(path)
    rows = []
    for query_id, records in ledger.items():
        accepted = tr.accepted_entry(records)
        status = "accepted" if accepted is not None else "exhausted"
        reasons = [] if accepted is not None else [
            r.get("reason") for r in records if r.get("status") == "rejected"
        ]
        rows.append((query_id, len(records), status, reasons))
    return rows


def format_table(path_rows):
    """`path_rows` is [(path, [(query_id, attempt_count, status, reasons), ...]), ...]. One
    printed row per (file, query) -- a query_id repeated across files is never merged."""
    lines = [f"{'file':<24} {'query_id':<10} {'attempts':<9} {'status':<10} reasons"]
    for path, rows in path_rows:
        base = os.path.basename(path)
        for query_id, attempt_count, status, reasons in rows:
            reason_text = "; ".join(reasons) if reasons else "-"
            lines.append(f"{base:<24} {query_id:<10} {attempt_count:<9} {status:<10} {reason_text}")
    return "\n".join(lines)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("responses", nargs="+", help="one or more --responses ledger JSONL paths")
    args = ap.parse_args(argv)

    path_rows = [(path, summarize_ledger(path)) for path in args.responses]
    print(format_table(path_rows))


if __name__ == "__main__":
    main()
