#!/usr/bin/env python3
"""Normalise one FreshStack topic into the BEIR-shaped JSONL the benchmark harness parses.

FreshStack ships as HuggingFace parquet and ships NO qrels file — its judgments are nested
inside the query rows. This script flattens both into <out-dir>/freshstack/:

    corpus.jsonl   {"_id", "text"}
    queries.jsonl  {"_id", "text"}
    qrels.tsv      query_id \t nugget_id \t corpus_id \t relevance

Unlike deploy/scripts/mint_acting_user_token.py this is NOT stdlib-only: it needs the
HuggingFace datasets library. That script must run on any machine; this one is a dev tool
run deliberately before a sweep. A bare `pip install datasets` fails on this machine (system
Python is PEP 668 externally-managed and `python3 -m venv` is unavailable). Install with:

    python3 -m pip install --target "$HOME/.freshstack-libs" datasets

    PYTHONPATH="$HOME/.freshstack-libs" python3 \
        Iverson.Server/Iverson.LoadTest/scripts/freshstack_to_jsonl.py \
        --topic godot --out-dir /path/to/corpora
"""

import argparse
import json
import os
import sys

from datasets import get_dataset_config_names, get_dataset_split_names, load_dataset

CORPUS_REPO = "freshstack/corpus-oct-2024"
QUERIES_REPO = "freshstack/queries-oct-2024"
TOPICS = ["angular", "godot", "langchain", "laravel", "yolo"]


def load_config(repo, topic, split):
    """Load one topic. The config is the SECOND POSITIONAL argument to load_dataset --
    there is no `subset=` parameter (FreshStack's own README says otherwise, and an unknown
    keyword would fall through to **config_kwargs without selecting the topic), so the
    loaded config is asserted against the request rather than trusted.

    The split is a parameter because the two repos DIFFER: the corpus repo publishes its
    rows under `train`, the queries repo under `test`. Asserting it before loading turns an
    upstream rename into a named cause rather than a bare ValueError."""
    available = get_dataset_config_names(repo)
    if topic not in available:
        sys.exit(f"{repo}: topic '{topic}' not among configs {sorted(available)}")

    splits = get_dataset_split_names(repo, topic)
    if split not in splits:
        sys.exit(f"{repo} ({topic}): split '{split}' not among {sorted(splits)}")

    ds = load_dataset(repo, topic, split=split)

    loaded = getattr(ds.info, "config_name", None)
    if loaded != topic:
        sys.exit(f"{repo}: asked for config '{topic}' but loaded '{loaded}'")
    return ds


def whitespace_ids(values):
    """Ids that cannot survive a whitespace-delimited file.

    qrels.tsv carries three id columns and TREC qrels readers split on whitespace rather
    than strictly on tabs; TrecRunWriter emits the run file space-separated. One id with a
    space shifts every later column in its row, and the reader takes the tail of that id as
    the next field -- wrong scores, no error anywhere."""
    return {v for v in values if any(c.isspace() for c in v)}


def check_no_whitespace(kind, values):
    """Hard-fail for the id columns that cannot simply be dropped. Excluding a query or a
    nugget would silently change what is being measured; excluding a document does not, so
    the corpus column is handled by exclusion in main() instead."""
    bad = sorted(whitespace_ids(values))
    if bad:
        sys.exit(
            f"{len(bad)} {kind} value(s) contain whitespace, which would shift columns in "
            f"qrels.tsv and the TREC run file. First few: {bad[:5]}"
        )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--topic", required=True, choices=TOPICS)
    ap.add_argument("--out-dir", required=True)
    args = ap.parse_args()

    corpus = load_config(CORPUS_REPO, args.topic, "train")
    queries = load_config(QUERIES_REPO, args.topic, "test")

    # Everything is validated BEFORE anything is written. A converter that exits non-zero
    # having already left corpus.jsonl on disk is worse than one that writes nothing:
    # BenchmarkIngestScenario gates on File.Exists alone, so the next benchmark-ingest run
    # would ingest a corpus whose judgments were never produced and score zero throughout.
    # Re-iterating a Dataset is cheap -- it is memory-mapped Arrow, not held in RAM.
    all_corpus_ids = {row["_id"] for row in corpus}
    # Corpus ids are GitHub paths, and some contain spaces -- 24 of godot's 25,482 -- which
    # no whitespace-delimited file can carry. Those documents are excluded and reported
    # rather than aborting the run; judgments referencing them are dropped alongside them.
    excluded_ids = whitespace_ids(all_corpus_ids)
    corpus_ids = all_corpus_ids - excluded_ids

    query_ids = {row["query_id"] for row in queries}
    check_no_whitespace("query_id", query_ids)

    # qrels -- the nugget id lands in column 2, the TREC iteration field, which is what
    # makes alpha-nDCG computable: ir_measures reads subtopic ids from exactly that column.
    nugget_ids = set()
    rows, missing_relevant = [], []
    dropped_non_relevant, dropped_excluded = 0, 0
    for row in queries:
        for nugget in row["nuggets"]:
            nugget_ids.add(nugget["_id"])
            for cid in nugget["relevant_corpus_ids"]:
                # An excluded document is a known omission, not a broken join -- test it
                # first, or excluding those 24 documents would trip the hard-fail below.
                if cid in excluded_ids:
                    dropped_excluded += 1
                    continue
                # Hard-fail: nuggets are generated FROM the corpus, so every relevant id
                # should resolve by construction. If one does not, the join encoded here is
                # wrong and the right response is to stop.
                if cid not in corpus_ids:
                    missing_relevant.append(cid)
                rows.append((row["query_id"], nugget["_id"], cid, 1))
            for cid in nugget["non_relevant_corpus_ids"]:
                if cid in excluded_ids:
                    dropped_excluded += 1
                    continue
                # Drop and report: these come from a retrieval judgment pool, not from
                # nugget generation, so the by-construction argument does not transfer.
                if cid not in corpus_ids:
                    dropped_non_relevant += 1
                    continue
                rows.append((row["query_id"], nugget["_id"], cid, 0))

    check_no_whitespace("nugget _id", nugget_ids)

    if missing_relevant:
        sys.exit(
            f"{len(missing_relevant)} relevant_corpus_id(s) are absent from the '{args.topic}' "
            f"corpus. First few: {sorted(set(missing_relevant))[:5]}"
        )

    # Every assertion has passed; only now does anything reach disk.
    out = os.path.join(args.out_dir, "freshstack")
    os.makedirs(out, exist_ok=True)

    # corpus.jsonl -- `metadata` dropped; `title` omitted entirely rather than emitted
    # empty, since FreshStack has no such field and the parser defaults a missing one to "".
    written = 0
    with open(os.path.join(out, "corpus.jsonl"), "w", encoding="utf-8") as f:
        for row in corpus:
            if row["_id"] in excluded_ids:
                continue
            f.write(json.dumps({"_id": row["_id"], "text": row["text"]}) + "\n")
            written += 1

    # queries.jsonl -- title + body. This composition is a DECISION, not a verified fact:
    # FreshStack does not document how it composes retrieval query text (spec section 1.1).
    with open(os.path.join(out, "queries.jsonl"), "w", encoding="utf-8") as f:
        for row in queries:
            text = f"{row['query_title']}\n\n{row['query_text']}"
            f.write(json.dumps({"_id": row["query_id"], "text": text}) + "\n")

    with open(os.path.join(out, "qrels.tsv"), "w", encoding="utf-8") as f:
        for qid, nid, cid, rel in rows:
            f.write(f"{qid}\t{nid}\t{cid}\t{rel}\n")

    print(f"corpus.jsonl  {written:,} documents written")
    if excluded_ids:
        print(f"              {len(excluded_ids):,} excluded (whitespace in _id); "
              f"{dropped_excluded:,} judgment(s) dropped with them")
    if written != len(corpus_ids):
        print(f"              {written - len(corpus_ids):,} row(s) share an _id with another")
    print(f"queries.jsonl {len(query_ids):,} queries")
    print(f"qrels.tsv     {len(rows):,} judgments ({len(nugget_ids):,} nuggets)")
    if dropped_non_relevant:
        print(f"dropped {dropped_non_relevant:,} unresolvable non-relevant judgment(s)")


if __name__ == "__main__":
    main()
