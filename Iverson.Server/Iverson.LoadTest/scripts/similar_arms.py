#!/usr/bin/env python3
"""Raw head-vs-centroid .similar runs (spec 2026-09-06-tier1-retrieval-defaults-design §3.3).

For every query in <run>/beir/queries.jsonl: embed once as --query-prefix + text (the ingest
contract carries document prefixes only; bge-base's query instruction is
"Represent this sentence for searching relevant passages: "), then two raw named-vector
searches on the object collection — body_vector (the head-512 object vector) and body_centroid
— limit 50, and write runs/head-raw.similar.trec and runs/centroid-raw.similar.trec. Raw against
raw isolates the representation; the API's fused .similar run is reported beside them.

    python3 similar_arms.py --run-dir <run> --model BAAI/bge-base-en-v1.5 --embed-url http://localhost:8091 \\
        --query-prefix "Represent this sentence for searching relevant passages: "
"""
import argparse, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ingest        # noqa: E402
import multivector   # noqa: E402  (trec_lines, require_collection)

DOCUMENT_BUDGET = 50
ARMS = {"head-raw": "body_vector", "centroid-raw": "body_centroid"}


def compose_query(prefix, text):
    return prefix + text


def rank_hits(hits, query_id, limit):
    ranked = []
    for h in hits:
        doc_id = h.get("payload", {}).get("docId")
        if doc_id is None:
            sys.exit(f"query {query_id}: point {h.get('id')} has no docId payload")
        ranked.append((doc_id, h["score"]))
    if len(ranked) < limit:
        sys.exit(f"query {query_id}: {len(ranked)} hits < {limit} -- R@50 would be understated; not scored")
    return ranked


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run-dir", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--embed-url", required=True)
    ap.add_argument("--query-prefix", required=True, help="the model's QUERY instruction, verbatim, or '' for models that need none")
    ap.add_argument("--object-collection", default=ingest.DEFAULT_OBJECT_COLLECTION)
    args = ap.parse_args()

    multivector.require_collection(args.object_collection)
    with open(os.path.join(args.run_dir, "beir", "queries.jsonl"), encoding="utf-8") as f:
        queries = [json.loads(line) for line in f if line.strip()]
    runs_dir = os.path.join(args.run_dir, "runs"); os.makedirs(runs_dir, exist_ok=True)
    lines = {label: [] for label in ARMS}
    try:
        for i, q in enumerate(queries, start=1):
            vec = ingest.embed(compose_query(args.query_prefix, q["text"]), args.model, "", args.embed_url)
            for label, vector_name in ARMS.items():
                status, resp = ingest.qdrant_request("POST", f"/collections/{args.object_collection}/points/search", {
                    "vector": {"name": vector_name, "vector": vec}, "limit": DOCUMENT_BUDGET, "with_payload": ["docId"]})
                if status != 200:
                    sys.exit(f"query {q['_id']} ({label}): HTTP {status} {resp}")
                lines[label].extend(multivector.trec_lines(q["_id"], rank_hits(resp["result"], q["_id"], DOCUMENT_BUDGET), label))
            if i % 50 == 0 or i == len(queries):
                print(f"[similar_arms] {i}/{len(queries)} queries")
    finally:
        for label, rows in lines.items():
            with open(os.path.join(runs_dir, f"{label}.similar.trec"), "w", encoding="utf-8") as f:
                f.write("\n".join(rows) + ("\n" if rows else ""))
    print(f"[similar_arms] wrote {', '.join(f'{l}.similar.trec' for l in ARMS)} in {runs_dir}")


if __name__ == "__main__":
    main()
