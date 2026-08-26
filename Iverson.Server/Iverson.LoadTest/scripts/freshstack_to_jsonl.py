#!/usr/bin/env python3
# Raw docstring: --help renders it verbatim, so the \t column separators and the backslash
# line-continuations in the install command below must survive as written.
r"""Normalise one FreshStack topic into the BEIR-shaped JSONL the benchmark harness parses.

FreshStack ships as HuggingFace parquet and ships NO qrels file — its judgments are nested
inside the query rows. This script flattens both into <out-dir>/freshstack/:

    corpus.jsonl   {"_id", "text"}
    queries.jsonl  {"_id", "text"}
    qrels.tsv      query_id \t nugget_id \t corpus_id \t relevance

qrels.tsv is subtopic-scoped only: column 2 carries the nugget id, which is exactly what
makes it valid for alpha-nDCG and Coverage. It is not valid for query-level metrics
(Recall@k, nDCG) without a per-query collapse first -- measured on real godot output, 464
of 585 relevant (qid, docid) pairs carry both a 1 and a 0 under different nuggets, so a
naive collapse keyed on (qid, docid) alone would lose 43-55% of relevant judgments.

Writes into a fixed <out-dir>/freshstack/, so --topic is one-per-directory: running this
twice into the same --out-dir silently overwrites the first run's output. To score two
topics, convert each into its own --out-dir and concatenate the three files afterward.

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
    # Imported here, not at module scope: `datasets` is absent until the install below the
    # docstring has been run, and a module-level import would make `--help` -- which prints that
    # very install command -- fail with ModuleNotFoundError on exactly the machine that needs it.
    from datasets import get_dataset_config_names, get_dataset_split_names, load_dataset

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


def check_no_blank_text(kind, ids):
    """Hard-fail on empty text, matching JsonlCorpusParser, which rejects blank "text" on both
    the corpus and the query path. Catching it here keeps the failure in the converter -- where
    the cause is a renamed upstream field and the fix is one line -- rather than at the front of
    a benchmark-ingest run against a corpus that is already on disk. Measured clean on godot."""
    if ids:
        sys.exit(
            f"{len(ids)} {kind} row(s) have empty text, which JsonlCorpusParser rejects outright. "
            f"First few: {sorted(ids)[:5]}"
        )


def compose_query(row):
    """Title + body. This composition is a DECISION, not a verified fact: FreshStack does not
    document how it composes retrieval query text (spec section 1.1). Defined once so the string
    validated by check_no_blank_text is the same string queries.jsonl receives."""
    return f"{row['query_title']}\n\n{row['query_text']}"


def main():
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
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
    all_corpus_ids, blank_corpus_ids = set(), []
    for row in corpus:
        all_corpus_ids.add(row["_id"])
        if not (row["text"] or "").strip():
            blank_corpus_ids.append(row["_id"])
    check_no_blank_text("corpus", blank_corpus_ids)
    # Corpus ids are GitHub paths, and some contain spaces -- 24 of godot's 25,482 -- which
    # no whitespace-delimited file can carry. Those documents are excluded and reported
    # rather than aborting the run; judgments referencing them are dropped alongside them.
    excluded_ids = whitespace_ids(all_corpus_ids)
    corpus_ids = all_corpus_ids - excluded_ids

    query_ids, blank_query_ids = set(), []
    for row in queries:
        query_ids.add(row["query_id"])
        if not compose_query(row).strip():
            blank_query_ids.append(row["query_id"])
    check_no_whitespace("query_id", query_ids)
    check_no_blank_text("query", blank_query_ids)

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
                    continue
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
    written, duplicate_rows = 0, 0
    seen_ids = set()
    with open(os.path.join(out, "corpus.jsonl"), "w", encoding="utf-8") as f:
        for row in corpus:
            cid = row["_id"]
            if cid in excluded_ids:
                continue
            # Five of godot's 25,482 rows share an _id with another. Emitting both files two
            # distinct documents under one judgment key and lets one run file list the same doc
            # id at two ranks, which TREC scorers treat as malformed or silently collapse. First
            # occurrence wins, so the written corpus and the qrels id space describe one corpus.
            if cid in seen_ids:
                duplicate_rows += 1
                continue
            seen_ids.add(cid)
            f.write(json.dumps({"_id": cid, "text": row["text"]}) + "\n")
            written += 1

    with open(os.path.join(out, "queries.jsonl"), "w", encoding="utf-8") as f:
        for row in queries:
            f.write(json.dumps({"_id": row["query_id"], "text": compose_query(row)}) + "\n")

    with open(os.path.join(out, "qrels.tsv"), "w", encoding="utf-8") as f:
        for qid, nid, cid, rel in rows:
            f.write(f"{qid}\t{nid}\t{cid}\t{rel}\n")

    print(f"corpus.jsonl  {written:,} documents written")
    if excluded_ids:
        print(f"              {len(excluded_ids):,} excluded (whitespace in _id); "
              f"{dropped_excluded:,} judgment(s) dropped with them")
    if duplicate_rows:
        print(f"              {duplicate_rows:,} row(s) skipped (duplicate _id)")
    print(f"queries.jsonl {len(query_ids):,} queries")
    print(f"qrels.tsv     {len(rows):,} judgments ({len(nugget_ids):,} nuggets)")
    if dropped_non_relevant:
        print(f"dropped {dropped_non_relevant:,} unresolvable non-relevant judgment(s)")


if __name__ == "__main__":
    main()
