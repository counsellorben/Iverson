#!/usr/bin/env python3
"""Filter the five FreshStack topics' subtopic qrels (qid \\t nugget \\t corpus_id \\t rel) to one
harness slice, writing TREC 4-column qrels with the nugget id in the iteration column -- the
input `report.py --nugget-qrels` needs for alpha_nDCG@10.

    python3 freshstack_nugget_qrels.py --slice <run>/beir --topics-root .../freshstack-5topic --out <run>/qrels.nugget.trec

Keeps a row iff its query id is in the slice's queries.jsonl AND its corpus id is in the slice's
corpus.jsonl; keeps rel as-is (0 rows included -- pyndeval treats rel>0 as relevant). Prints the
kept/dropped counts per topic and exits non-zero if any slice query ends up with no row.

Nothing is written to --out until every topic file has been read and every slice query is
confirmed covered (sample_corpus.py's convention: a converter that exits non-zero having
already left a partial file behind is worse than one that writes nothing).
"""

import argparse
import glob
import json
import os
import sys


def filter_rows(rows, query_ids, corpus_ids):
    """Yield only rows whose query id is in query_ids AND whose corpus id is in corpus_ids.
    rel is passed through unchanged, including "0"/0 rows -- pyndeval treats rel > 0 as
    relevant, so a non-relevant judgment is still meaningful input, not noise to drop."""
    for qid, nugget, docid, rel in rows:
        if qid in query_ids and docid in corpus_ids:
            yield (qid, nugget, docid, rel)


def load_ids(path, id_field="_id"):
    """The set of id_field values from a JSONL file (sample_corpus.py's load_jsonl, trimmed
    to just the ids this script needs)."""
    ids = set()
    try:
        with open(path, "r", encoding="utf-8") as f:
            for lineno, line in enumerate(f, start=1):
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as e:
                    sys.exit(f"{path}:{lineno}: invalid JSON: {e}")
                rid = row.get(id_field)
                if not rid:
                    sys.exit(f"{path}:{lineno}: missing or empty \"{id_field}\"")
                ids.add(rid)
    except OSError as e:
        sys.exit(f"cannot read {path}: {e}")
    return ids


def read_qrels_tsv(path):
    """One FreshStack topic's subtopic qrels.tsv (qid \\t nugget \\t corpus_id \\t rel, no
    header) as a list of 4-tuples."""
    rows = []
    try:
        with open(path, "r", encoding="utf-8", newline="") as f:
            for lineno, line in enumerate(f, start=1):
                line = line.rstrip("\r\n")
                if not line.strip():
                    continue
                fields = line.split("\t")
                if len(fields) != 4:
                    sys.exit(f"{path}:{lineno}: expected 4 tab-separated fields, got {len(fields)}: {line!r}")
                rows.append(tuple(fields))
    except OSError as e:
        sys.exit(f"cannot read {path}: {e}")
    return rows


def main():
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument(
        "--slice", required=True,
        help="A harness slice directory holding queries.jsonl and corpus.jsonl (e.g. <run>/beir).",
    )
    ap.add_argument(
        "--topics-root", required=True,
        help="Directory holding the FreshStack topics, each <topic>/freshstack/qrels.tsv.",
    )
    ap.add_argument("--out", required=True, help="Output path for the TREC 4-column nugget qrels.")
    args = ap.parse_args()

    query_ids = load_ids(os.path.join(args.slice, "queries.jsonl"))
    corpus_ids = load_ids(os.path.join(args.slice, "corpus.jsonl"))

    topic_files = sorted(glob.glob(os.path.join(args.topics_root, "*", "freshstack", "qrels.tsv")))
    if not topic_files:
        sys.exit(f"--topics-root {args.topics_root}: no */freshstack/qrels.tsv files found")

    kept_rows = []
    covered_qids = set()
    for topic_path in topic_files:
        topic = os.path.basename(os.path.dirname(os.path.dirname(topic_path)))
        rows = read_qrels_tsv(topic_path)
        topic_kept = list(filter_rows(rows, query_ids, corpus_ids))
        kept_rows.extend(topic_kept)
        covered_qids.update(qid for qid, _nugget, _docid, _rel in topic_kept)
        print(
            f"[{topic}] kept {len(topic_kept):,} / {len(rows):,} rows "
            f"({len(rows) - len(topic_kept):,} dropped)"
        )

    uncovered = sorted(query_ids - covered_qids)
    if uncovered:
        sys.exit(
            f"{len(uncovered)} slice quer{'y' if len(uncovered) == 1 else 'ies'} ended up with "
            f"no kept nugget-qrels row: {uncovered[:10]}" + (" ..." if len(uncovered) > 10 else "")
        )

    with open(args.out, "w", encoding="utf-8") as f:
        for qid, nugget, docid, rel in kept_rows:
            f.write(f"{qid} {nugget} {docid} {rel}\n")

    print(
        f"wrote {len(kept_rows):,} rows for {len(covered_qids):,} / {len(query_ids):,} "
        f"slice queries -> {args.out}"
    )


if __name__ == "__main__":
    main()
