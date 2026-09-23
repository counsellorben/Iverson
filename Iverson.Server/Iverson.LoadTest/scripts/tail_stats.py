#!/usr/bin/env python3
"""Phase 1 measurement for the chunk-coverage signal (spec 2026-09-08-chunk-coverage-signal-design.md
§2, §3 Phase 1).

Reads a raw chunk-hit dump (<label>.chunks.hits.tsv, written by benchmark-query -- Task 2), the
beta=0 collapsed document ranking (<label>.chunks.trec) and keymap.json, and reports the three
things Phase 1 owes Phase 2:

    1. the in-pool tail-depth histogram -- how many top-50 documents have 2, 3, 4-or-more pooled
       chunks;
    2. s -- the mean score of the 2nd-4th pooled chunks of the documents that reach the top 50 of
       the beta=0 ranking. A document contributing only one pooled chunk has no tail and
       contributes nothing to s;
    3. the pool-wide equivalent of s, over every parent in the dump (unscoped by the run file),
       printed beside it -- this is strictly lower, and both numbers go on the record (spec §2).

Then, from the same run file, the two derived ladder endpoints Phase 2 needs so it does not have
to recompute them by hand:

    tie-break beta = median_top10_adjacent_gap / s
    parity    beta = span_rank1_to_rank50 / (3 * s)

"s" in both formulas is the top-50-scoped figure (2), not the pool-wide one (3) -- the ladder is
set from the ranking's own decision scale, and the decision scale and s must come from the same
population (spec §2) or the ratio is not a ratio of comparable quantities.

Stdlib only.

    python3 tail_stats.py --hits path/to/label.chunks.hits.tsv --run path/to/label.chunks.trec \\
        --keymap path/to/keymap.json
"""
import argparse
import json
import statistics
import sys
from collections import defaultdict

TAIL_CAP = 3          # DocumentRanking.TailCap: skip the max, sum up to 3 more.
DOCUMENT_BUDGET = 50  # DocumentRanking.CollapseByDocIdWithTail's `limit` on this arm.


def load_keymap(path):
    """keymap.json: a flat {parentKey: docId} JSON object (ingest.py's KeyMap.LoadAsync format,
    ingest.py:88)."""
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def load_hits(path):
    """Parses <label>.chunks.hits.tsv (Task 2, ChunkHitDumpWriter): header
    `queryId\\tparentKey\\trank\\tscore`, tab-separated, one row per hit in per-query rank order.
    Returns a list of (queryId, parentKey, score) in file order.

    `rank` is CHECKED, not discarded: every figure below is derived from file order, so a dump that
    had been sorted, filtered or concatenated would otherwise read as valid and produce plausible
    but wrong statistics. ChunkHitDumpWriter writes rank 1-based per query in supply order, so rank
    must equal the count of rows seen so far for that query."""
    rows = []
    rows_per_query = {}
    with open(path, encoding="utf-8") as f:
        header = f.readline()
        if not header:
            sys.exit(f"{path}: empty file, expected a header row")
        for lineno, line in enumerate(f, start=2):
            line = line.rstrip("\n")
            if not line:
                continue
            fields = line.split("\t")
            if len(fields) != 4:
                sys.exit(f"{path}:{lineno}: expected 4 tab-separated fields, got {len(fields)}: {line!r}")
            query_id, parent_key, rank, score = fields
            try:
                rank = int(rank)
            except ValueError:
                sys.exit(f"{path}:{lineno}: non-integer rank {rank!r}")
            expected_rank = rows_per_query.get(query_id, 0) + 1
            rows_per_query[query_id] = expected_rank
            if rank != expected_rank:
                sys.exit(
                    f"{path}:{lineno}: rank {rank} disagrees with file order (this is row "
                    f"{expected_rank} of query {query_id!r}) -- the dump has been reordered or "
                    f"edited, and every figure here is derived from file order")
            try:
                score = float(score)
            except ValueError:
                sys.exit(f"{path}:{lineno}: non-numeric score {score!r}")
            rows.append((query_id, parent_key, score))
    return rows


def load_run_doc_ids(path):
    """Parses the beta=0 <label>.chunks.trec: standard 6-column TREC (`queryId Q0 docId rank score
    runTag`), space-separated, one row per document, already truncated to the top 50 per query.
    Parsed with the same tolerant whitespace-split report.py's structural_check uses (report.py's
    own comment: standard TREC columns are query_id, iter, doc_id, rank, score, tag). Returns
    {queryId: [(docId, score), ...]} in file order, which TrecRunWriter writes in descending-score
    rank order (TrecRunWriter.cs)."""
    by_query = defaultdict(list)
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            fields = line.split()
            if len(fields) < 6:
                sys.exit(f"{path}:{lineno}: expected 6 whitespace-separated fields, got {len(fields)}: {line!r}")
            query_id, _iter, doc_id, _rank, score, _tag = fields[:6]
            try:
                score = float(score)
            except ValueError:
                sys.exit(f"{path}:{lineno}: non-numeric score {score!r}")
            by_query[query_id].append((doc_id, score))
    return dict(by_query)


def resolve_by_doc(hits, keymap):
    """Groups dump rows by (queryId, docId), resolving parentKey -> docId through keymap. A
    parentKey absent from keymap is excluded from every downstream figure, mirroring
    MaxPassageAggregator.Aggregate's own unresolved-parent handling (MaxPassageAggregator.cs) --
    an unresolved parent is not part of any ranking and must not be part of its statistics either.
    Returns ({(queryId, docId): [score, ...]} in dump order, count of excluded rows)."""
    by_doc = defaultdict(list)
    unresolved = 0
    for query_id, parent_key, score in hits:
        doc_id = keymap.get(parent_key)
        if doc_id is None:
            unresolved += 1
            continue
        by_doc[(query_id, doc_id)].append(score)
    return dict(by_doc), unresolved


def tail_scores(scores):
    """One document's 2nd-4th pooled chunk scores, highest first -- exactly the slice
    DocumentRanking.CollapseByDocIdWithTail sums (`descending.Skip(1).Take(TailCap)`). Empty for a
    document with a single pooled chunk."""
    descending = sorted(scores, reverse=True)
    return descending[1:1 + TAIL_CAP]


def tail_depth_histogram(by_doc):
    """{2: n, 3: n, "4+": n} -- counts of (query, doc) pairs in `by_doc` whose pooled-chunk COUNT
    is exactly 2, exactly 3, or 4-or-more. A document contributing exactly one pooled chunk falls
    into none of these buckets, matching the spec's histogram (§2, §3 Phase 1)."""
    hist = {2: 0, 3: 0, "4+": 0}
    for scores in by_doc.values():
        n = len(scores)
        if n == 2:
            hist[2] += 1
        elif n == 3:
            hist[3] += 1
        elif n >= 4:
            hist["4+"] += 1
    return hist


def mean_tail_score(by_doc):
    """The mean score of the 2nd-4th pooled chunks, flattened across every document in `by_doc`
    that has one -- a single overall mean over every individual tail-chunk score value, not a mean
    of per-document means. A document contributing one chunk supplies zero values and so
    contributes nothing to the mean, per spec §2's own wording. Returns (mean, n_values); mean is
    None when nothing in `by_doc` has a tail."""
    values = []
    for scores in by_doc.values():
        values.extend(tail_scores(scores))
    if not values:
        return None, 0
    return statistics.mean(values), len(values)


def scoped_to_run(by_doc, run_by_query):
    """Restricts `by_doc` to the (queryId, docId) pairs present in the beta=0 run file -- the
    documents that reached the top 50 of that query's ranking (spec §2's scoping rule: both
    numerators are properties of the top-50 ranking, so the denominator must come from the same
    population). Scoping is on the RUN FILE's document set, not the dump's -- a document with many
    pooled chunks that never reached the top 50 is excluded here even though it is still counted in
    the pool-wide figure."""
    keep = {(query_id, doc_id) for query_id, rows in run_by_query.items() for doc_id, _score in rows}
    return {key: scores for key, scores in by_doc.items() if key in keep}


def check_scope_covers_run(scoped, run_by_query, hits_path, run_path, keymap_path):
    """Every row of the run file is a (query, doc) pair the run's own aggregation produced from this
    dump through this key map, so scoping the dump to the run file must recover EVERY run row --
    scoped size == run file row count, exactly.

    Without this the script cannot tell a matched triple from a mismatched one. `--keymap` is a
    separate argument from `--run`, and every corpus directory holds a file called exactly
    `keymap.json`, so pointing at the wrong corpus's key map still resolves: `unresolved` stays 0
    (its parent keys are all present, just mapped to other documents) and `scoped_to_run` quietly
    intersects down to whatever the two populations happen to share. s was then computed over an
    accidental intersection, and the ladder endpoints derived from it were wrong by an unknown
    amount with nothing on screen to say so."""
    run_rows = sum(len(rows) for rows in run_by_query.values())
    if len(scoped) != run_rows:
        sys.exit(
            f"[tail_stats] scoping the dump to the run file recovered {len(scoped)} of the run "
            f"file's {run_rows} rows -- they must be equal.\n"
            f"  hits   {hits_path}\n"
            f"  run    {run_path}\n"
            f"  keymap {keymap_path}\n"
            f"This is a run/keymap/dump mismatch: the run file was not produced from this dump "
            f"through this key map. Check that all three come from the same benchmark-query run "
            f"and the same corpus.")


def span_rank1_to_rank50(run_by_query):
    """Mean, across queries with at least 2 ranked rows, of (rank-1 score - lowest-ranked score) --
    the per-query decision span the parity endpoint is built from (spec §2, spec B22's reported
    'mean' figure -- 0.0747 on the primary arm -- rather than its median). A query with fewer than
    2 ranked rows contributes no span. Returns None if no query qualifies."""
    spans = [rows[0][1] - rows[-1][1] for rows in run_by_query.values() if len(rows) >= 2]
    return statistics.mean(spans) if spans else None


def median_top10_adjacent_gap(run_by_query):
    """Median, over every adjacent-rank score gap within each query's own top 10 (rank i score
    minus rank i+1 score, for i = 1..min(10, n)-1), pooled across all queries into one list before
    taking the median -- matching spec B22's single reported figure (0.00236) rather than a median
    of per-query medians. Returns None if no query has 2 or more ranked rows."""
    gaps = []
    for rows in run_by_query.values():
        top10 = rows[:10]
        for i in range(len(top10) - 1):
            gaps.append(top10[i][1] - top10[i + 1][1])
    return statistics.median(gaps) if gaps else None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--hits", required=True, help="<label>.chunks.hits.tsv, written by benchmark-query (Task 2)")
    ap.add_argument("--run", required=True, help="the beta=0 <label>.chunks.trec run file")
    ap.add_argument("--keymap", required=True, help="keymap.json: flat {parentKey: docId}")
    args = ap.parse_args()

    keymap = load_keymap(args.keymap)
    hits = load_hits(args.hits)
    run_by_query = load_run_doc_ids(args.run)

    by_doc, unresolved = resolve_by_doc(hits, keymap)
    if unresolved:
        print(f"[tail_stats] {unresolved} dump row(s) had a parentKey absent from the key map -- excluded from every figure below")

    top50_by_doc = scoped_to_run(by_doc, run_by_query)
    check_scope_covers_run(top50_by_doc, run_by_query, args.hits, args.run, args.keymap)

    hist = tail_depth_histogram(top50_by_doc)
    print("[tail_stats] in-pool tail-depth histogram (top-50 documents):")
    print(f"  2 chunks    {hist[2]}")
    print(f"  3 chunks    {hist[3]}")
    print(f"  4+ chunks   {hist['4+']}")

    s_top50, n_top50 = mean_tail_score(top50_by_doc)
    s_pool, n_pool = mean_tail_score(by_doc)

    if s_top50 is None:
        sys.exit("[tail_stats] no top-50 document has a pooled tail -- s is undefined, cannot derive the ladder")

    print(f"[tail_stats] s (top-50 scoped)   {s_top50:.6f}  (n={n_top50} tail-chunk values)")
    if s_pool is None:
        print("[tail_stats] s (pool-wide)       undefined -- no document in the dump has a pooled tail")
    else:
        print(f"[tail_stats] s (pool-wide)       {s_pool:.6f}  (n={n_pool} tail-chunk values)")

    span = span_rank1_to_rank50(run_by_query)
    gap = median_top10_adjacent_gap(run_by_query)
    if span is None or gap is None:
        sys.exit("[tail_stats] the run file has too few ranked rows per query to derive span / median gap")

    tie_break = gap / s_top50
    parity = span / (3 * s_top50)

    print(f"[tail_stats] span_rank1_to_rank50               {span:.6f}")
    print(f"[tail_stats] median_top10_adjacent_gap          {gap:.6f}")
    print(f"[tail_stats] tie-break beta = median_top10_adjacent_gap / s   {tie_break:.6f}")
    print(f"[tail_stats] parity beta    = span_rank1_to_rank50 / (3*s)    {parity:.6f}")


if __name__ == "__main__":
    main()
