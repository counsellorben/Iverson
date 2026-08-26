#!/usr/bin/env python3
"""Build the BEIR-shaped subset `benchmark-ingest`/`benchmark-query` read, from a flat BEIR
corpus directory that does not have that layout on disk.

`scifact-full/` (and any other flat BEIR download) ships corpus.jsonl, queries.jsonl and a
qrels/ directory at the top level. JsonlCorpusParser and BenchmarkQueryScenario.LoadQueries
instead expect <corpus-path>/beir/{corpus,queries}.jsonl, so nothing in the flat shape is
directly usable by benchmark-ingest/benchmark-query without a conversion step.

This script does three things, always in this order, into <out-dir>/:

    1. Converts the qrels to TREC. BEIR ships 3-column `query-id`/`corpus-id`/`score` with a
       header row and sometimes CRLF line endings; ir_measures needs 4-column TREC
       `qid iteration docid rel`. A qrels file already in 4-column TREC form passes through
       unchanged -- its column 2 is meaningful there (an iteration/subtopic id) and reading it
       as `corpus-id` would take the score as a doc id and drop every relevant document.

    2. Selects the query set from that qrels file. queries.jsonl carries every train, dev and
       test query in one file with no split marker, and LoadQueries applies no filter, so
       without this step a run would search all 1,109 queries -- most with no judgment
       anywhere -- and report a meaningless aggregate. The qrels path matters: qrels/test.tsv
       and qrels/train.tsv are disjoint splits of the same 1,109 queries, so pointing this
       script at the wrong file silently changes which ~300 (or ~800) queries get kept.

    3. Chooses documents: every document judged relevant to a kept query, always, plus
       distractors up to --target-size. Omit --target-size (or pass one at or above the
       corpus size) to keep the entire corpus -- "re-lay out, sample nothing" -- which is how
       the SciFact run reaches the <path>/beir/ layout benchmark-ingest/benchmark-query
       require while keeping all 5,183 documents.

Writes:

    <out-dir>/beir/corpus.jsonl   {"_id", "title", "text"} -- chosen documents only
    <out-dir>/beir/queries.jsonl  {"_id", "text"} -- kept queries only
    <out-dir>/qrels.trec          TREC 4-column, restricted to kept queries

The qrels restriction matters downstream: ir_measures.calc_aggregate aggregates over the
queries present in the qrels file, so a query present there but absent from the run drags
every aggregate down toward zero rather than being reported as missing -- scoring a sampled
run against an unfiltered qrels reports a wrong number, not a missing one.

Usage:
    python3 Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py \\
        --corpus-dir /path/to/scifact-full \\
        --out-dir /path/to/output \\
        --target-size 5183
"""

import argparse
import json
import os
import random
import sys


def convert_qrels_to_trec(path):
    """Read a qrels file and return TREC 4-column rows (qid, iteration, docid, rel) as strings.

    BEIR's on-disk shape is 3 columns (`query-id`, `corpus-id`, `score`) with a header row and
    sometimes CRLF; TREC's is 4 (`qid`, `iteration`, `docid`, `rel`) with no header. Column
    count on the first line decides which shape this file is in -- applying the BEIR
    conversion to an already-4-column file would read its (meaningful) iteration column as a
    corpus id and silently corrupt every row.
    """
    try:
        with open(path, "r", encoding="utf-8", newline="") as f:
            # newline="" plus an explicit \r strip below: BEIR qrels sometimes ship CRLF, and
            # leaving \r on a docid would make it fail every id comparison silently rather
            # than loudly (a corpus id ending "\r" cannot match one that doesn't).
            lines = [line.rstrip("\r\n") for line in f if line.strip()]
    except OSError as e:
        sys.exit(f"cannot read qrels file {path}: {e}")

    if not lines:
        sys.exit(f"{path}: empty qrels file")

    first_fields = lines[0].split("\t")

    if len(first_fields) == 4:
        # Already TREC: no header row, column 2 is the iteration/subtopic id and must survive
        # to the emitted qrels.trec unchanged.
        rows = []
        for lineno, line in enumerate(lines, start=1):
            fields = line.split("\t")
            if len(fields) != 4:
                sys.exit(f"{path}:{lineno}: expected 4 tab-separated fields, got {len(fields)}: {line!r}")
            rows.append(tuple(fields))
        return rows

    if len(first_fields) == 3:
        # BEIR shape: header row, then query-id/corpus-id/score. Strip the header, remap each
        # remaining row to TREC's qid/iteration/docid/rel with a fixed iteration of "0".
        rows = []
        for lineno, line in enumerate(lines[1:], start=2):
            fields = line.split("\t")
            if len(fields) != 3:
                sys.exit(f"{path}:{lineno}: expected 3 tab-separated fields, got {len(fields)}: {line!r}")
            qid, corpus_id, score = fields
            rows.append((qid, "0", corpus_id, score))
        return rows

    sys.exit(
        f"{path}: first line has {len(first_fields)} tab-separated field(s); expected 3 "
        "(BEIR, with header) or 4 (TREC, no header)."
    )


def load_jsonl(path, id_field="_id"):
    """Load a JSONL file into a dict keyed by id_field, preserving each row's other fields."""
    rows = {}
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
                if rid in rows:
                    sys.exit(f"{path}:{lineno}: duplicate \"{id_field}\" {rid!r}")
                rows[rid] = row
    except OSError as e:
        sys.exit(f"cannot read {path}: {e}")
    return rows


def main():
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument(
        "--corpus-dir", required=True,
        help="Flat BEIR directory holding corpus.jsonl, queries.jsonl and qrels/.",
    )
    ap.add_argument(
        "--out-dir", required=True,
        help="Directory to write <out-dir>/beir/{corpus,queries}.jsonl and "
             "<out-dir>/qrels.trec into.",
    )
    ap.add_argument(
        "--qrels",
        help="Qrels file that selects the query set and its judgments "
             "(default: <corpus-dir>/qrels/test.tsv). Use train.tsv or any other split "
             "deliberately -- see the module docstring on why the file matters.",
    )
    ap.add_argument(
        "--target-size", type=int, default=None,
        help="Total documents to keep (relevant documents plus distractors up to this "
             "count). Omit to keep every document in the corpus.",
    )
    ap.add_argument(
        "--seed", type=int, default=0,
        help="Random seed for distractor sampling, for a reproducible sample "
             "(default: %(default)s).",
    )
    args = ap.parse_args()

    if args.target_size is not None and args.target_size < 0:
        sys.exit(f"--target-size must be non-negative, got {args.target_size}")

    qrels_path = args.qrels or os.path.join(args.corpus_dir, "qrels", "test.tsv")
    trec_rows = convert_qrels_to_trec(qrels_path)

    kept_qids = {qid for qid, _iteration, _docid, _rel in trec_rows}

    relevant_docids = set()
    for qid, _iteration, docid, rel in trec_rows:
        try:
            rel_int = int(rel)
        except ValueError:
            sys.exit(f"{qrels_path}: non-integer rel value {rel!r} for query {qid!r}, doc {docid!r}")
        if rel_int > 0:
            relevant_docids.add(docid)

    queries_path = os.path.join(args.corpus_dir, "queries.jsonl")
    all_queries = load_jsonl(queries_path)

    # Everything is validated BEFORE anything reaches disk (freshstack_to_jsonl.py
    # convention): a converter that exits non-zero having already left a partial beir/
    # directory behind is worse than one that writes nothing, since BenchmarkIngestScenario
    # and BenchmarkQueryScenario gate on File.Exists alone.
    missing_qids = sorted(kept_qids - all_queries.keys())
    if missing_qids:
        sys.exit(
            f"{len(missing_qids)} query id(s) from {qrels_path} are absent from "
            f"{queries_path}. First few: {missing_qids[:5]}"
        )

    corpus_path = os.path.join(args.corpus_dir, "corpus.jsonl")
    all_corpus = load_jsonl(corpus_path)

    missing_docids = sorted(relevant_docids - all_corpus.keys())
    if missing_docids:
        sys.exit(
            f"{len(missing_docids)} relevant corpus id(s) from {qrels_path} are absent from "
            f"{corpus_path}. First few: {missing_docids[:5]}"
        )

    # Step 3: choose documents -- every relevant document, always, plus distractors up to
    # --target-size. Omitting --target-size (or passing one at or above the corpus size)
    # keeps everything: "re-lay out, sample nothing".
    if args.target_size is None or args.target_size >= len(all_corpus):
        chosen_docids = set(all_corpus.keys())
    else:
        if args.target_size < len(relevant_docids):
            sys.exit(
                f"--target-size {args.target_size} is smaller than the "
                f"{len(relevant_docids)} document(s) judged relevant to the kept queries; "
                "every relevant document must fit in the sample."
            )
        distractor_pool = sorted(all_corpus.keys() - relevant_docids)
        random.Random(args.seed).shuffle(distractor_pool)
        n_distractors = args.target_size - len(relevant_docids)
        chosen_docids = relevant_docids | set(distractor_pool[:n_distractors])

    out_beir = os.path.join(args.out_dir, "beir")
    os.makedirs(out_beir, exist_ok=True)

    with open(os.path.join(out_beir, "corpus.jsonl"), "w", encoding="utf-8") as f:
        for docid in sorted(chosen_docids):
            row = all_corpus[docid]
            f.write(json.dumps({
                "_id": docid,
                "title": row.get("title") or "",
                "text": row.get("text") or "",
            }) + "\n")

    with open(os.path.join(out_beir, "queries.jsonl"), "w", encoding="utf-8") as f:
        for qid in sorted(kept_qids):
            row = all_queries[qid]
            f.write(json.dumps({"_id": qid, "text": row.get("text") or ""}) + "\n")

    kept_rows = [row for row in trec_rows if row[0] in kept_qids]
    with open(os.path.join(args.out_dir, "qrels.trec"), "w", encoding="utf-8") as f:
        for qid, iteration, docid, rel in kept_rows:
            f.write(f"{qid}\t{iteration}\t{docid}\t{rel}\n")

    print(f"corpus.jsonl  {len(chosen_docids):,} / {len(all_corpus):,} document(s)")
    print(f"queries.jsonl {len(kept_qids):,} / {len(all_queries):,} quer{'y' if len(kept_qids) == 1 else 'ies'}")
    print(f"qrels.trec    {len(kept_rows):,} judgment row(s), {len(relevant_docids):,} distinct relevant document(s)")


if __name__ == "__main__":
    main()
