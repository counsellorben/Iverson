#!/usr/bin/env python3
"""Reorders an already-captured retrieval run (B) into three oracle rankings -- R
(relevance only), A (per-document aspect count), G (alpha-discounted greedy over aspects)
-- so report.py's existing scorer can measure how much headroom a hypothetical
aspect-coverage ranking term would have, without re-retrieving anything. Nothing here
computes a metric; report.py does that. See
docs/specs/2026-09-13-aspect-coverage-oracle-design.md.

Run with:

    python3 Iverson.Server/Iverson.LoadTest/scripts/aspect_oracle.py \\
        --run <path/to/run.trec> \\
        --qrels <path/to/qrels.trec> \\
        --nugget-qrels <path/to/qrels.nugget.trec> \\
        --out-dir <path/to/out-dir>

Writes, into --out-dir:
    oracle-R.trec, oracle-A.trec, oracle-G.trec  -- TREC run files, each a permutation of
        B's own per-query document list, tagged oracle-R / oracle-A / oracle-G.
    qrels.610.trec, qrels.nugget.610.trec        -- both qrels files filtered to the query
        ids with >= 1 relevant document among the 50 the run actually retrieved for that
        query (select_610), the population report.py's sensitivity re-run scores against.
    aspect-summary.tsv                            -- one row per query: relevant count in
        50, distinct nuggets, and nuggets reachable in the top 10 under B, R, A and G.
"""
import argparse
import collections
import os

ALPHA = 0.5   # spec §2.4: pyndeval's default, pre-registered so the greedy's objective
              # and the metric's objective are the same constant by construction.
              # Deliberately NOT a CLI flag.


def load_run(path):
    """TREC run file (6 columns: qid Q0 docid rank score tag) -> OrderedDict[qid, [docid, ...]]
    in file order."""
    run = collections.OrderedDict()
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            qid, _q0, docid, _rank, _score, _tag = line.split()
            run.setdefault(qid, []).append(docid)
    return run


def load_rel(path):
    """TREC qrels file (4 columns: qid iteration docid rel) -> {qid: set(docid)} over
    rel > 0 rows."""
    rel = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            qid, _iteration, docid, rel_val = line.split()
            if int(rel_val) > 0:
                rel.setdefault(qid, set()).add(docid)
    return rel


def load_nuggets(path):
    """Nugget qrels file (4 columns: qid nugget_id docid rel) -> {qid: {docid: set(nugget_id)}}
    over rel > 0 rows."""
    nug = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            qid, nugget_id, docid, rel_val = line.split()
            if int(rel_val) > 0:
                nug.setdefault(qid, {}).setdefault(docid, set()).add(nugget_id)
    return nug


def b_index(docs):
    """Position in B, the tie-break every ranking uses for "in B's order"."""
    return {d: i for i, d in enumerate(docs)}


def rank_R(docs, rel, nug):
    """Judged-relevant documents first, in B's order; the rest after, in B's order."""
    return [d for d in docs if d in rel] + [d for d in docs if d not in rel]


def rank_A(docs, rel, nug):
    """Judged-relevant documents ordered by descending count of distinct nuggets the
    document covers for this query, ties broken by B's order; the rest after, in B's
    order."""
    order = b_index(docs)
    relevant = [d for d in docs if d in rel]
    relevant.sort(key=lambda d: (-len(nug.get(d, ())), order[d]))
    return relevant + [d for d in docs if d not in rel]


def rank_G(docs, rel, nug):
    """Spec §2.2. Gain is the alpha-DISCOUNTED marginal gain, NOT the count of
    not-yet-covered nuggets: a nugget already covered by k appended documents is
    still worth (1 - ALPHA) ** k, and the two objectives coincide only at alpha = 1.
    Runs until the relevant prefix is exhausted -- there is no termination clause."""
    order = b_index(docs)
    remaining = [d for d in docs if d in rel]
    seen = collections.Counter()
    out = []
    while remaining:
        best = max(remaining, key=lambda d: (
            sum((1 - ALPHA) ** seen[n] for n in nug.get(d, ())),
            -order[d],
        ))
        out.append(best)
        for n in nug.get(best, ()):
            seen[n] += 1
        remaining.remove(best)
    return out + [d for d in docs if d not in rel]


def write_trec(path, ranking, tag):
    """Score is strictly decreasing in the NEW rank order (spec §2.8 / A19): the scorer
    re-sorts by score and ignores the rank column, so a file carrying B's original scores
    would be silently re-sorted back into B. Same shape as test_report.py:30."""
    with open(path, "w", encoding="utf-8") as f:
        for qid, docs in ranking.items():
            for i, docid in enumerate(docs):
                f.write(f"{qid} Q0 {docid} {i + 1} {float(len(docs) - i):.6f} {tag}\n")


def filter_qrels(src, dst, keep):
    """Line-level copy of a TREC qrels file, keeping only rows whose first field (the
    query id) is in `keep`."""
    with open(src, encoding="utf-8") as fin, open(dst, "w", encoding="utf-8") as fout:
        for line in fin:
            stripped = line.strip()
            if not stripped:
                continue
            qid = stripped.split()[0]
            if qid in keep:
                fout.write(stripped + "\n")


def select_610(run, rel):
    """Query ids with >= 1 relevant document among the 50 documents that query's run list
    actually contains -- not merely somewhere in the qrels."""
    keep = set()
    for qid, docs in run.items():
        q_rel = rel.get(qid, set())
        if any(d in q_rel for d in docs):
            keep.add(qid)
    return keep


def summary_rows(run, rel, nug, rankings):
    """One row per query: relevant count in 50, distinct nuggets covered by relevant
    documents, and nuggets reachable in the top 10 under each named ranking.

    `rankings` is an OrderedDict/dict of label -> {qid: [docid, ...]}, e.g.
    {"B": ..., "R": ..., "A": ..., "G": ...}, each value a permutation of run[qid]."""
    labels = list(rankings.keys())
    rows = []
    for qid, docs in run.items():
        q_rel = rel.get(qid, set())
        q_nug = nug.get(qid, {})
        relevant_count = sum(1 for d in docs if d in q_rel)
        distinct_nuggets = len({n for d in docs if d in q_nug for n in q_nug[d]})
        row = [qid, relevant_count, distinct_nuggets]
        for label in labels:
            top10 = rankings[label][qid][:10]
            row.append(len({n for d in top10 for n in q_nug.get(d, ())}))
        rows.append(row)
    return rows, labels


def main():
    parser = argparse.ArgumentParser(
        description="Reorder a captured TREC run into three aspect-coverage oracle "
                     "rankings (R, A, G) plus a 610-query qrels sensitivity population.")
    parser.add_argument("--run", required=True, help="TREC run file (the shipped ranking, B)")
    parser.add_argument("--qrels", required=True, help="TREC document qrels file")
    parser.add_argument("--nugget-qrels", required=True, help="TREC nugget (aspect) qrels file")
    parser.add_argument("--out-dir", required=True, help="Directory to write outputs into")
    args = parser.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)

    run = load_run(args.run)
    rel = load_rel(args.qrels)
    nug = load_nuggets(args.nugget_qrels)

    ranking_B = collections.OrderedDict()
    ranking_R = collections.OrderedDict()
    ranking_A = collections.OrderedDict()
    ranking_G = collections.OrderedDict()
    for qid, docs in run.items():
        q_rel = rel.get(qid, set())
        q_nug = nug.get(qid, {})
        ranking_B[qid] = docs
        ranking_R[qid] = rank_R(docs, q_rel, q_nug)
        ranking_A[qid] = rank_A(docs, q_rel, q_nug)
        ranking_G[qid] = rank_G(docs, q_rel, q_nug)

    write_trec(os.path.join(args.out_dir, "oracle-R.trec"), ranking_R, "oracle-R")
    write_trec(os.path.join(args.out_dir, "oracle-A.trec"), ranking_A, "oracle-A")
    write_trec(os.path.join(args.out_dir, "oracle-G.trec"), ranking_G, "oracle-G")

    keep = select_610(run, rel)
    filter_qrels(args.qrels, os.path.join(args.out_dir, "qrels.610.trec"), keep)
    filter_qrels(args.nugget_qrels, os.path.join(args.out_dir, "qrels.nugget.610.trec"), keep)

    rows, labels = summary_rows(
        run, rel, nug,
        collections.OrderedDict([("B", ranking_B), ("R", ranking_R), ("A", ranking_A), ("G", ranking_G)]),
    )
    summary_path = os.path.join(args.out_dir, "aspect-summary.tsv")
    with open(summary_path, "w", encoding="utf-8") as f:
        header = ["qid", "relevant_in_50", "distinct_nuggets"] + [f"nuggets_top10_{label}" for label in labels]
        f.write("\t".join(header) + "\n")
        for row in rows:
            f.write("\t".join(str(x) for x in row) + "\n")

    print(f"wrote {len(run)} queries to {args.out_dir}: "
          f"oracle-R.trec, oracle-A.trec, oracle-G.trec, "
          f"qrels.610.trec ({len(keep)} queries), qrels.nugget.610.trec ({len(keep)} queries), "
          f"aspect-summary.tsv ({len(rows)} rows)")


if __name__ == "__main__":
    main()
