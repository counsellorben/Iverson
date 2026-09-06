#!/usr/bin/env python3
"""Chunk-level multivector experiment (spec docs/specs/2026-09-04-multivector-experiment-design.md).

    build  -- regroup the per-chunk collection's points into one Qdrant multivector point per
              document (Cosine, MaxSim). Zero embedding: the rows ARE the chunk vectors, so the two
              layouts differ only in storage and scoring.
    query  -- embed <run>/beir/queries.jsonl through TEI and write two raw TREC runs: a per-chunk
              named-vector search (250 chunks collapsed to 50 docs, the benchmark-query budget) and
              a multivector MaxSim query (50 docs), interleaved per query with latency captured.
    stats  -- points / indexed vectors / segments / on-disk size for both collections.

Reuses ingest.py's Qdrant and TEI helpers; never touches the object or chunk collections except
to read them. Every count that can be checked is checked and a mismatch exits non-zero.

    python3 multivector.py build [--drop]
    python3 multivector.py query --run-dir <run> --model Alibaba-NLP/gte-modernbert-base --embed-url http://localhost:8091
    python3 multivector.py stats --run-dir <run>
"""
import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ingest  # noqa: E402

DEFAULT_MULTIVECTOR_COLLECTION = "benchmark_documents_multivector_tenant_bypass"
CHUNK_VECTOR_NAME = "body_vector"
# BenchmarkQueryScenario.DocumentBudget / ChunkBudgetMultiplier -- fixed for the whole sweep.
DOCUMENT_BUDGET = 50
CHUNK_BUDGET_MULTIPLIER = 5
CHUNK_TOP_K = DOCUMENT_BUDGET * CHUNK_BUDGET_MULTIPLIER
CHUNKS_RUN_LABEL = "gte-chunks-raw"
MULTIVECTOR_RUN_LABEL = "gte-multivector-raw"
SCROLL_PAGE = 500
UPSERT_BATCH = 100
QDRANT_CONTAINER = "iverson-qdrant"
QDRANT_COLLECTIONS_DIR = "/qdrant/storage/collections"


# ── Pure functions (tested) ─────────────────────────────────────────────────────────────

def group_rows(points):
    """{parent_key: [row, ...]} with rows ordered by int(chunk_index). The payload stores
    chunk_index as a STRING (spec A10); a string sort would put "10" before "2"."""
    groups = {}
    for point in points:
        payload = point["payload"]
        groups.setdefault(payload["parent_id"], []).append(
            (int(payload["chunk_index"]), point["vector"][CHUNK_VECTOR_NAME]))
    return {key: [row for _, row in sorted(rows, key=lambda r: r[0])] for key, rows in groups.items()}


def resolve_parents(groups, objects):
    """One multivector point per parent, id = the parent's object point id. objects is
    {point_id: {"key", "docId"}}. Parents without an object point are returned, not skipped."""
    points, missing = [], []
    for parent_key, rows in groups.items():
        object_id = ingest.key_to_ulong(parent_key)
        obj = objects.get(object_id)
        if obj is None:
            missing.append(parent_key)
            continue
        points.append({
            "id": object_id,
            "vector": rows,
            "payload": {"key": obj["key"], "docId": obj["docId"], "chunk_count": len(rows)},
        })
    return points, missing


def collapse_by_doc(scored, limit):
    """DocumentRanking.CollapseByDocId: max per doc (first-seen wins an exact tie), descending,
    truncate AFTER the collapse."""
    best = {}
    for doc_id, score in scored:
        if doc_id not in best or score > best[doc_id]:
            best[doc_id] = score
    return sorted(best.items(), key=lambda kv: kv[1], reverse=True)[:limit]


def trec_lines(query_id, ranked, run_tag):
    """TrecRunWriter's format: `qid Q0 docid rank score runtag`, rank from 1, score F6."""
    return [f"{query_id} Q0 {doc_id} {rank} {score:.6f} {run_tag}"
            for rank, (doc_id, score) in enumerate(ranked, start=1)]


def summarize_latency(samples_ms):
    if not samples_ms:
        raise ValueError("no latency samples")
    ordered = sorted(samples_ms)
    n = len(ordered)

    def pct(p):
        return ordered[min(n - 1, int(round(p * (n - 1))))]

    return {"n": n, "p50_ms": pct(0.5), "p95_ms": pct(0.95), "mean_ms": sum(ordered) / n}


# ── Qdrant helpers (thin wrappers over ingest.qdrant_request) ────────────────────────────

def collection_info(name):
    """The collection's `result` object, or None when it does not exist (404)."""
    status, body = ingest.qdrant_request("GET", f"/collections/{name}")
    if status == 404:
        return None
    if status != 200:
        sys.exit(f"GET /collections/{name}: HTTP {status} {body}")
    return body["result"]


def scroll(name, with_vector, payload_fields):
    offset = None
    while True:
        body = {"limit": SCROLL_PAGE, "with_vector": with_vector, "with_payload": payload_fields}
        if offset is not None:
            body["offset"] = offset
        status, resp = ingest.qdrant_request("POST", f"/collections/{name}/points/scroll", body)
        if status != 200:
            sys.exit(f"scroll {name}: HTTP {status} {resp}")
        yield from resp["result"]["points"]
        offset = resp["result"].get("next_page_offset")
        if offset is None:
            return


def load_objects(object_collection):
    """{object point id: {"key", "docId"}} -- one scroll, payload only."""
    return {p["id"]: {"key": p["payload"]["key"], "docId": p["payload"]["docId"]}
            for p in scroll(object_collection, False, ["key", "docId"])}


def require_collection(name):
    info = collection_info(name)
    if info is None:
        sys.exit(f"collection '{name}' does not exist")
    return info


INDEX_WAIT_SECONDS = 600


def wait_for_index(name):
    """Qdrant's wait=true covers the write, not the optimizer: HNSW is built asynchronously once a
    segment passes indexing_threshold, with status yellow meanwhile. A latency measured before that
    is exact search under an index build, not the layout under test (CIR-1 §2.2). Green with zero
    indexed vectors past the deadline means the collection was never indexed -- refuse."""
    deadline = time.monotonic() + INDEX_WAIT_SECONDS
    while True:
        info = require_collection(name)
        status, indexed = info["status"], info["indexed_vectors_count"]
        if status == "green" and indexed > 0:
            print(f"[multivector] '{name}' green, indexed_vectors_count {indexed:,}")
            return info
        if time.monotonic() > deadline:
            sys.exit(f"'{name}' is {status} with indexed_vectors_count {indexed} after {INDEX_WAIT_SECONDS}s "
                     "-- not HNSW-indexed; the arm would be brute force")
        print(f"[multivector] '{name}' {status}, indexed {indexed:,} -- waiting for the index")
        time.sleep(5)


# ── build ───────────────────────────────────────────────────────────────────────────────

def cmd_build(args):
    chunks_info = require_collection(args.chunks_collection)
    dim = chunks_info["config"]["params"]["vectors"][CHUNK_VECTOR_NAME]["size"]
    expected_rows = chunks_info["points_count"]
    target = args.multivector_collection

    if collection_info(target) is not None:
        if not args.drop:
            sys.exit(f"collection '{target}' already exists; pass --drop to recreate it")
        status, body = ingest.qdrant_request("DELETE", f"/collections/{target}")
        if status != 200:
            sys.exit(f"DELETE /collections/{target}: HTTP {status} {body}")
        print(f"[multivector] dropped '{target}'")

    # Cosine matches IntelligenceCollectionManager.Metric; no quantization (spec §3.3).
    status, body = ingest.qdrant_request("PUT", f"/collections/{target}", {
        "vectors": {"size": dim, "distance": "Cosine", "multivector_config": {"comparator": "max_sim"}},
    })
    if status != 200:
        sys.exit(f"PUT /collections/{target}: HTTP {status} {body}")
    print(f"[multivector] created '{target}' (dim {dim}, Cosine, max_sim)")

    objects = load_objects(args.object_collection)
    # Scroll order is by point id, not by parent, so grouping needs every row in memory:
    # 19,967 x 768 floats is ~0.5 GB of Python objects, fine on this box.
    groups = group_rows(scroll(args.chunks_collection, True, ["parent_id", "chunk_index"]))
    points, missing = resolve_parents(groups, objects)
    if missing:
        sys.exit(f"{len(missing)} parent(s) have no object point in '{args.object_collection}', "
                 f"e.g. {missing[:3]}")

    written = 0
    for start in range(0, len(points), UPSERT_BATCH):
        batch = points[start:start + UPSERT_BATCH]
        last = start + UPSERT_BATCH >= len(points)
        status, body = ingest.qdrant_request(
            "PUT", f"/collections/{target}/points?wait={'true' if last else 'false'}", {"points": batch})
        if status != 200:
            sys.exit(f"upsert into '{target}' failed at point {start}: HTTP {status} {body}")
        written += sum(len(p["vector"]) for p in batch)

    if written != expected_rows:
        sys.exit(f"rows written {written} != chunk points {expected_rows} in '{args.chunks_collection}'")
    target_points = require_collection(target)["points_count"]
    if target_points != len(points):
        sys.exit(f"'{target}' reports {target_points} points, expected {len(points)}")
    wait_for_index(target)
    print(f"[multivector] {len(points)} points, {written} rows == {expected_rows} chunk points")


# ── stats ───────────────────────────────────────────────────────────────────────────────

def disk_size(name):
    out = subprocess.run(
        ["docker", "exec", QDRANT_CONTAINER, "du", "-sh", f"{QDRANT_COLLECTIONS_DIR}/{name}"],
        capture_output=True, text=True, check=True).stdout
    return out.split()[0]


def cmd_stats(args):
    rows = []
    for name in (args.chunks_collection, args.multivector_collection):
        info = require_collection(name)
        rows.append({
            "collection": name,
            "points_count": info["points_count"],
            "indexed_vectors_count": info["indexed_vectors_count"],
            "segments_count": info["segments_count"],
            "disk": disk_size(name),
        })
    print(f"{'collection':55} {'points':>8} {'indexed':>8} {'segments':>8} {'disk':>7}")
    for r in rows:
        print(f"{r['collection']:55} {r['points_count']:>8,} {r['indexed_vectors_count']:>8,} "
              f"{r['segments_count']:>8} {r['disk']:>7}")
    path = os.path.join(args.run_dir, "runs", "storage.json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=2)
    print(f"[multivector] wrote {path}")


# ── CLI ─────────────────────────────────────────────────────────────────────────────────

def add_common_args(p):
    p.add_argument("--object-collection", default=ingest.DEFAULT_OBJECT_COLLECTION)
    p.add_argument("--chunks-collection", default=ingest.DEFAULT_CHUNKS_COLLECTION)
    p.add_argument("--multivector-collection", default=DEFAULT_MULTIVECTOR_COLLECTION)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)

    b = sub.add_parser("build", help="regroup chunk points into the multivector collection")
    add_common_args(b)
    b.add_argument("--drop", action="store_true", help="delete and recreate the multivector collection")
    b.set_defaults(func=cmd_build)

    s = sub.add_parser("stats", help="points / indexed / segments / disk for both collections")
    add_common_args(s)
    s.add_argument("--run-dir", required=True, help="run directory; writes runs/storage.json")
    s.set_defaults(func=cmd_stats)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
