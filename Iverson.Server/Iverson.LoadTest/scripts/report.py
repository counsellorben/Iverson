#!/usr/bin/env python3
"""Structural checks and ir_measures scoring for TREC run files, plus an ingest-throughput
report read from ingest.py's stats sidecar.

Three things, in order, per invocation:

    1. Structural checks on each run file -- rows, distinct queries, count of non-zero
       scores, and duplicate doc ids within a query. These are exactly the malformations a
       TREC scorer either rejects outright or silently collapses, so they are surfaced
       before any score is trusted.

    2. ir_measures scoring -- nDCG@10, R@50, AP against a qrels file, one line per run.

    3. Ingest throughput, read from --stats-path (ingest.py's <key-map-path>.stats.json
       sidecar, if given and present): documents, chunks, embed calls, embeds saved by the
       reuse gate, wall time, docs/hour, seconds/embed, and the headline -- measured
       seconds/document against the ~34s/document of the full gRPC/Kafka pipeline (see
       ingest.py's module docstring).

Run-file discovery: --run may be passed more than once, and each value is either a run file
directly or a directory, in which case every *.trec file inside it (sorted) is scored. This
reads more naturally than a separate --run-dir flag: "score these paths" stays one concept
whether a path names a file or a directory of files, and multiple --run flags can still mix
individual files with directories in one invocation.

Not stdlib-only, unlike this directory's other scripts: ir_measures is the one third-party
import this project permits (P3 of the parent plan). It is reached through PYTHONPATH, never
a site-packages install -- this box is PEP 668 externally-managed with no working venv:

    python3 -m pip install --target /path/to/libs ir_measures

    PYTHONPATH=/path/to/libs python3 \\
        Iverson.Server/Iverson.LoadTest/scripts/report.py \\
        --run /path/to/runs-dir --qrels /path/to/qrels.trec

    # Individual files instead of a directory, and the ingest stats sidecar:
    PYTHONPATH=/path/to/libs python3 \\
        Iverson.Server/Iverson.LoadTest/scripts/report.py \\
        --run /path/to/baseline.chunks.trec --run /path/to/baseline.similar.trec \\
        --qrels /path/to/qrels.trec --stats-path /path/to/keymap.json.stats.json

CRITICAL: score with measure OBJECTS, never ir_measures.parse_measure's string form
("nDCG@10"). parse_measure calls ast.Num, removed in Python 3.12, and raises AttributeError
on this box's 3.14. Import the measures and build the list directly:

    from ir_measures import nDCG, R, AP
    ir_measures.calc_aggregate([nDCG@10, R@50, AP], qrels, run)
"""

import argparse
import glob
import json
import os
import sys
from datetime import datetime

FULL_PIPELINE_SECONDS_PER_DOCUMENT = 34.0


# ── Run-file discovery ─────────────────────────────────────────────────────────────────

def resolve_run_paths(run_args, qrels_path):
    """Expand each --run value into a sorted list of *.trec files if it names a directory,
    or keep it as-is if it names a file. Order: directory expansions are sorted so output is
    reproducible across a machine's directory-listing order; the top-level --run values keep
    the order given on the command line.

    A directory of run files routinely holds its own qrels file alongside them (this
    script's own verification directory does: qrels-small.trec sits next to
    baseline.chunks.trec) -- and qrels use the same *.trec extension while carrying a
    different column count (query_id, iteration, doc_id, relevance -- 4 fields, not the run
    format's 6). A naive glob would hand that file to ir_measures as a run and it would
    raise an unhandled ValueError unpacking the columns. The qrels path is therefore always
    excluded from run discovery, whether it was swept in from a directory or (a user error,
    but still handled) passed directly via --run."""
    qrels_abspath = os.path.abspath(qrels_path)
    paths = []
    excluded_qrels = False
    for value in run_args:
        if os.path.isdir(value):
            found = sorted(glob.glob(os.path.join(value, "*.trec")))
            if not found:
                sys.exit(f"--run {value}: directory contains no *.trec files")
            for path in found:
                if os.path.abspath(path) == qrels_abspath:
                    excluded_qrels = True
                    continue
                paths.append(path)
        elif os.path.isfile(value):
            if os.path.abspath(value) == qrels_abspath:
                excluded_qrels = True
                continue
            paths.append(value)
        else:
            sys.exit(f"--run {value}: not a file or directory")
    if excluded_qrels:
        print(f"[report] excluded {qrels_path} from run discovery (it is the --qrels file)")
    return paths


# ── Step 1: structural checks ──────────────────────────────────────────────────────────

def structural_check(path):
    """Rows, distinct queries, non-zero scores, and duplicate (query_id, doc_id) pairs --
    parsed from the raw whitespace-delimited columns rather than through
    ir_measures.read_trec_run, which returns whatever list of ScoredDoc rows it read without
    flagging a doc id repeated under one query. A repeated doc id is exactly the malformation
    a TREC scorer either rejects (pytrec_eval raises) or silently collapses (a dict-keyed
    reader keeps only the last occurrence) -- either way, the run file does not mean what it
    looks like it means, and that has to be visible before its score is trusted.

    Standard TREC run columns: query_id, iter, doc_id, rank, score, tag. Malformed lines
    (fewer than 6 whitespace-separated fields) are counted and reported rather than raising,
    since a partially-malformed file is still worth reporting the rest of."""
    rows = 0
    malformed = 0
    query_ids = set()
    nonzero_scores = 0
    seen = {}  # (query_id, doc_id) -> count
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            fields = line.split()
            if len(fields) < 6:
                malformed += 1
                continue
            query_id, _iter, doc_id, _rank, score, _tag = fields[:6]
            rows += 1
            query_ids.add(query_id)
            key = (query_id, doc_id)
            seen[key] = seen.get(key, 0) + 1
            try:
                if float(score) != 0.0:
                    nonzero_scores += 1
            except ValueError:
                sys.exit(f"{path}: non-numeric score {score!r} on a row for query {query_id}")

    duplicates = {k: c for k, c in seen.items() if c > 1}
    duplicate_rows = sum(c - 1 for c in duplicates.values())

    return {
        "rows": rows,
        "malformed_lines": malformed,
        "distinct_queries": len(query_ids),
        "nonzero_scores": nonzero_scores,
        "duplicate_pairs": len(duplicates),
        "duplicate_rows": duplicate_rows,
        "duplicate_examples": sorted(duplicates)[:5],
    }


def print_structural_check(path, check):
    print(f"\n[structural] {path}")
    print(f"  rows                 {check['rows']:,}")
    if check["malformed_lines"]:
        print(f"  malformed lines      {check['malformed_lines']:,} (fewer than 6 fields; skipped)")
    print(f"  distinct queries     {check['distinct_queries']:,}")
    print(f"  non-zero scores      {check['nonzero_scores']:,} / {check['rows']:,}")
    if check["duplicate_pairs"]:
        print(
            f"  duplicate doc ids    {check['duplicate_pairs']:,} (query, doc) pair(s), "
            f"{check['duplicate_rows']:,} extra row(s) beyond the first -- "
            f"e.g. {check['duplicate_examples']}"
        )
    else:
        print("  duplicate doc ids    none")


# ── Step 2: scoring ─────────────────────────────────────────────────────────────────────

def score_run(qrels, run_path, measures):
    import ir_measures

    run = ir_measures.read_trec_run(run_path)
    return ir_measures.calc_aggregate(measures, qrels, run)


def print_scores(path, measures, results):
    print(f"\n[scores] {path}")
    for measure in measures:
        print(f"  {str(measure):10s} {results[measure]:.4f}")


# ── Step 3: ingest stats sidecar ───────────────────────────────────────────────────────

def load_stats(path):
    if not path:
        return None
    if not os.path.exists(path):
        print(f"\n[stats] {path} not found -- skipping ingest-throughput report")
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def print_stats(path, stats):
    print(f"\n[stats] {path}")
    documents = stats.get("documents", 0)
    chunks = stats.get("chunks", 0)
    embed_calls = stats.get("embed_calls", 0)
    embeds_saved = stats.get("embeds_saved", 0)
    print(f"  documents            {documents:,}")
    print(f"  chunks               {chunks:,}")
    print(f"  embed calls          {embed_calls:,} ({embeds_saved:,} saved by the reuse gate)")

    started_at = stats.get("started_at")
    finished_at = stats.get("finished_at")
    if not started_at or not finished_at:
        print("  wall time            unavailable (started_at/finished_at missing from sidecar)")
        return

    started = datetime.fromisoformat(started_at)
    finished = datetime.fromisoformat(finished_at)
    wall_seconds = (finished - started).total_seconds()
    print(f"  wall time            {wall_seconds:,.1f}s ({started_at} -> {finished_at})")

    if wall_seconds <= 0:
        print("  docs/hour            unavailable (non-positive wall time)")
        return
    if documents == 0:
        print("  docs/hour            unavailable (zero documents)")
        return

    docs_per_hour = documents / (wall_seconds / 3600.0)
    seconds_per_document = wall_seconds / documents
    print(f"  docs/hour            {docs_per_hour:,.1f}")
    print(f"  seconds/document     {seconds_per_document:.3f}")
    if embed_calls > 0:
        print(f"  seconds/embed        {wall_seconds / embed_calls:.3f}")

    speedup = FULL_PIPELINE_SECONDS_PER_DOCUMENT / seconds_per_document
    print(
        f"\n  headline: {seconds_per_document:.3f}s/document measured vs "
        f"{FULL_PIPELINE_SECONDS_PER_DOCUMENT:.0f}s/document for the full gRPC/Kafka pipeline "
        f"-- {speedup:.1f}x"
    )


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument(
        "--run", action="append", required=True,
        help="a TREC run file, or a directory of *.trec files; may be passed more than once",
    )
    ap.add_argument("--qrels", required=True, help="TREC qrels file")
    ap.add_argument(
        "--stats-path", default=None,
        help="ingest.py's <key-map-path>.stats.json sidecar; omit to skip the throughput report",
    )
    args = ap.parse_args()

    # Imported here rather than at module scope, matching freshstack_to_jsonl.py's pattern
    # for its own non-stdlib import: `--help` must work even when PYTHONPATH is not yet set
    # up, since printing the install instructions above is exactly what a user without
    # ir_measures installed needs from this script first.
    try:
        import ir_measures
        from ir_measures import AP, R, nDCG
    except ImportError as e:
        sys.exit(
            f"could not import ir_measures ({e}). It must be reached via PYTHONPATH, not "
            "site-packages -- see this script's module docstring for the install command."
        )

    measures = [nDCG @ 10, R @ 50, AP]

    if not os.path.exists(args.qrels):
        sys.exit(f"--qrels {args.qrels}: not found")

    run_paths = resolve_run_paths(args.run, args.qrels)
    if not run_paths:
        sys.exit("no run files resolved from --run (after excluding --qrels)")

    for path in run_paths:
        print_structural_check(path, structural_check(path))

    qrels = list(ir_measures.read_trec_qrels(args.qrels))

    for path in run_paths:
        results = score_run(qrels, path, measures)
        print_scores(path, measures, results)

    stats = load_stats(args.stats_path)
    if stats is not None:
        print_stats(args.stats_path, stats)


if __name__ == "__main__":
    main()
