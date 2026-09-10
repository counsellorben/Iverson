#!/usr/bin/env python3
"""Chunk-level multivector experiment (spec docs/specs/2026-09-04-multivector-experiment-design.md).

    build  -- regroup the per-chunk collection's points into one Qdrant multivector point per
              document (Cosine, MaxSim). Zero embedding: the rows ARE the chunk vectors, so the two
              layouts differ only in storage and scoring.
    query  -- embed <run>/beir/queries.jsonl through TEI and write two raw TREC runs: a per-chunk
              named-vector search (250 chunks collapsed to 50 docs, the benchmark-query budget) and
              a multivector MaxSim query (50 docs), interleaved per query with latency captured.
    stats  -- points / indexed vectors / segments / on-disk size for both collections.
    probe  -- exactness probe (spec §3.5): is either arm's retrieval approximate at its operating
              beam? Sweeps each collection's own hnsw_ef constant over a bulk-plus-tail sample of
              queries and writes runs/probe.json. Diagnostic only, runnable before or after §3.3.

Reuses ingest.py's Qdrant and TEI helpers; never touches the object or chunk collections except
to read them. Every count that can be checked is checked and a mismatch exits non-zero.

    python3 multivector.py build [--drop]
    python3 multivector.py query --run-dir <run> --model Alibaba-NLP/gte-modernbert-base --embed-url http://localhost:8091
    python3 multivector.py stats --run-dir <run>
    python3 multivector.py probe --run-dir <run> --model Alibaba-NLP/gte-modernbert-base --embed-url http://localhost:8091 \\
        --tail-qrels <orig-run>/qrels.trec --tail-control-run <orig-run>/runs/gte-chunks-raw.chunks.trec \\
        --tail-arm-run <orig-run>/runs/gte-multivector-raw.chunks.trec
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

# Every probe point must exceed its collection's own `limit`: Qdrant clamps params.hnsw_ef up
# to `limit`, so a smaller value is inert and would score as a spurious agreement.
CONTROL_HNSW_EF_SWEEP = (250, 500, 1000, 4000)
ARM_HNSW_EF_SWEEP = (65, 100, 250, 1000)
PROBE_BULK_QUERIES = 30


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


def rank_chunk_hits(hits, key_to_doc, limit):
    """Per-chunk search hits -> document ranking via the parent map (MaxPassageAggregator +
    CollapseByDocId). An unresolved parent means the index holds a document this run cannot
    name; that ranking must not be scored (spec §3.3)."""
    scored = []
    for hit in hits:
        parent = hit["payload"]["parent_id"]
        if parent not in key_to_doc:
            sys.exit(f"chunk {hit['id']}: parent {parent} has no object point")
        scored.append((key_to_doc[parent], hit["score"]))
    return collapse_by_doc(scored, limit)


def rank_multivector_points(points, limit):
    """MaxSim points -> document ranking. Qdrant returns one point per document here, but the
    arm must not depend on that: two points sharing a docId would produce a malformed run, and
    the chunk arm already collapses. Same helper, same order -- collapse, then truncate."""
    return collapse_by_doc([(p["payload"]["docId"], p["score"]) for p in points], limit)


def existing_run_files(runs_dir):
    """The run files a `query` into this directory would overwrite. The gate's own
    `gte-chunks-raw` / `gte-multivector-raw` files are unreproducible evidence, so cmd_query
    refuses rather than clobbering them."""
    names = (f"{CHUNKS_RUN_LABEL}.chunks.trec", f"{MULTIVECTOR_RUN_LABEL}.chunks.trec")
    paths = [os.path.join(runs_dir, n) for n in names]
    return [p for p in paths if os.path.exists(p)]


def tail_query_ids(control_values, arm_values):
    """Query ids whose per-query measure value differs between the two runs -- per the design
    these are the only queries whose exactness bears on the gate, because recall failures live
    in a small tail and a bulk sample lands in the ~85% where nothing moves. A query present on
    only one side counts as differing."""
    keys = set(control_values) | set(arm_values)
    return sorted(k for k in keys if control_values.get(k) != arm_values.get(k))


def probe_set(query_ids, tail_ids, bulk_n):
    """The probe set: the first bulk_n corpus queries plus every tail id, de-duplicated and
    returned in corpus order. A tail id absent from the corpus is an error rather than a silent
    drop -- it would mean the tail was derived against a different corpus."""
    missing = [t for t in tail_ids if t not in set(query_ids)]
    if missing:
        raise ValueError(f"tail query ids not present in the corpus: {missing}")
    chosen = set(query_ids[:bulk_n]) | set(tail_ids)
    return [q for q in query_ids if q in chosen]


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


# ── query ───────────────────────────────────────────────────────────────────────────────

def cmd_query(args):
    # Both layouts must be HNSW-indexed and idle before a latency is taken (CIR-1 §2.2); the
    # state measured under is written into the sidecar so the gate document reports it.
    index_state = {}
    for name in (args.chunks_collection, args.multivector_collection):
        info = require_collection(name)
        if info["status"] != "green":
            sys.exit(f"'{name}' is {info['status']}: wait for indexing to finish before measuring")
        if info["indexed_vectors_count"] != info["points_count"]:
            sys.exit(
                f"'{name}' has {info['indexed_vectors_count']:,} of {info['points_count']:,} vectors "
                f"HNSW-indexed: a segment below indexing_threshold is searched exactly, which is the "
                f"index-state asymmetry this re-run exists to remove. Raise indexing_threshold and "
                f"re-check before measuring.")
        index_state[name] = {k: info[k] for k in ("status", "points_count", "indexed_vectors_count", "segments_count")}
    queries_path = os.path.join(args.run_dir, "beir", "queries.jsonl")
    with open(queries_path, encoding="utf-8") as f:
        queries = [json.loads(line) for line in f if line.strip()]
    if not queries:
        sys.exit(f"no queries in {queries_path}")
    key_to_doc = {p["payload"]["key"]: p["payload"]["docId"]
                  for p in scroll(args.object_collection, False, ["key", "docId"])}

    runs_dir = os.path.join(args.run_dir, "runs")
    os.makedirs(runs_dir, exist_ok=True)
    clash = existing_run_files(runs_dir)
    if clash:
        sys.exit(f"refusing to overwrite {len(clash)} existing run file(s) in {runs_dir}: "
                 f"{', '.join(os.path.basename(p) for p in clash)} -- point --run-dir at a fresh directory")
    chunk_lines, mv_lines, short = [], [], []
    latency = {"chunks": [], "multivector": []}

    def write_outputs():
        # Called on every exit path (try/finally): a failed run still leaves its partial
        # evidence on disk, and the latency sidecar carries whatever was measured.
        with open(os.path.join(runs_dir, f"{CHUNKS_RUN_LABEL}.chunks.trec"), "w", encoding="utf-8") as f:
            f.write("\n".join(chunk_lines) + ("\n" if chunk_lines else ""))
        with open(os.path.join(runs_dir, f"{MULTIVECTOR_RUN_LABEL}.chunks.trec"), "w", encoding="utf-8") as f:
            f.write("\n".join(mv_lines) + ("\n" if mv_lines else ""))
        sidecar = {
            "model": args.model, "embed_url": args.embed_url,
            "chunks_collection": args.chunks_collection,
            "multivector_collection": args.multivector_collection,
            "chunk_top_k": CHUNK_TOP_K, "document_budget": DOCUMENT_BUDGET,
            "mv_hnsw_ef": args.mv_hnsw_ef,
            "queries": len(queries),
            "index_state": index_state,
        }
        for mode, samples in latency.items():
            sidecar[mode] = summarize_latency(samples) if samples else None
        with open(os.path.join(runs_dir, "raw-latency.json"), "w", encoding="utf-8") as f:
            json.dump(sidecar, f, indent=2)

    try:
        for i, q in enumerate(queries, start=1):
            qid, text = q["_id"], q["text"]
            # Same route and empty prefix as the API for this model (spec A6, A23).
            vec = ingest.embed(text, args.model, "", args.embed_url)

            # Interleaved per query so the two latencies see the same box state.
            t0 = time.perf_counter()
            status, resp = ingest.qdrant_request("POST", f"/collections/{args.chunks_collection}/points/search", {
                "vector": {"name": CHUNK_VECTOR_NAME, "vector": vec},
                "limit": CHUNK_TOP_K, "with_payload": ["parent_id"],
            })
            latency["chunks"].append((time.perf_counter() - t0) * 1000)
            if status != 200:
                sys.exit(f"query {qid}: chunk search HTTP {status} {resp}")
            ranked = rank_chunk_hits(resp["result"], key_to_doc, DOCUMENT_BUDGET)

            mv_body = {"query": [vec], "limit": DOCUMENT_BUDGET, "with_payload": ["docId"],
                       "params": {"hnsw_ef": args.mv_hnsw_ef}}
            t0 = time.perf_counter()
            status, resp = ingest.qdrant_request(
                "POST", f"/collections/{args.multivector_collection}/points/query", mv_body)
            latency["multivector"].append((time.perf_counter() - t0) * 1000)
            if status != 200:
                sys.exit(f"query {qid}: multivector query HTTP {status} {resp}")
            mv_ranked = rank_multivector_points(resp["result"]["points"], DOCUMENT_BUDGET)

            if len(ranked) < DOCUMENT_BUDGET or len(mv_ranked) < DOCUMENT_BUDGET:
                short.append((qid, len(ranked), len(mv_ranked)))
            chunk_lines.extend(trec_lines(qid, ranked, CHUNKS_RUN_LABEL))
            mv_lines.extend(trec_lines(qid, mv_ranked, MULTIVECTOR_RUN_LABEL))
            if i % 50 == 0 or i == len(queries):
                print(f"[multivector] {i}/{len(queries)} queries")
    finally:
        write_outputs()

    if short:
        sys.exit(f"{len(short)} query(ies) filled fewer than {DOCUMENT_BUDGET} documents "
                 f"(qid, chunks-docs, multivector-docs): {short[:5]} -- R@50 would be understated; not scored")
    for mode, samples in latency.items():
        s = summarize_latency(samples)
        print(f"[multivector] {mode:12} n={s['n']} p50={s['p50_ms']:.1f} ms p95={s['p95_ms']:.1f} ms mean={s['mean_ms']:.1f} ms")
    print(f"[multivector] wrote {CHUNKS_RUN_LABEL}.chunks.trec, {MULTIVECTOR_RUN_LABEL}.chunks.trec, raw-latency.json in {runs_dir}")


# ── probe ───────────────────────────────────────────────────────────────────────────────

def cmd_probe(args):
    """Exactness probe of spec §3.5: is either arm's retrieval approximate at its operating
    beam? Diagnostic only -- no index-state assertion (unlike cmd_query), since this must be
    runnable before §3.3 as well as after; the state it ran under is recorded in the sidecar
    instead so a probe taken against an unindexed collection is diagnosable, not misread as an
    engine verdict."""
    collections = {}
    for name in (args.chunks_collection, args.multivector_collection, args.object_collection):
        collections[name] = require_collection(name)

    # Function-local: report.py's own convention (:368, :495, ...), and it keeps build/query/
    # stats free of any ir_measures dependency -- probe is the only subcommand that needs it.
    import report
    import ir_measures
    from ir_measures import R

    # Materialised, not the bare generator: per_query_values iterates it, and a second call
    # against an exhausted generator silently returns {} rather than raising.
    qrels = list(ir_measures.read_trec_qrels(args.tail_qrels))
    control_values = report.per_query_values(qrels, args.tail_control_run, R @ 50)
    arm_values = report.per_query_values(qrels, args.tail_arm_run, R @ 50)
    tail = tail_query_ids(control_values, arm_values)
    corpus_size = len(set(control_values) | set(arm_values))
    print(f"[multivector] tail: {len(tail)} of {corpus_size}")

    queries_path = os.path.join(args.run_dir, "beir", "queries.jsonl")
    with open(queries_path, encoding="utf-8") as f:
        queries = [json.loads(line) for line in f if line.strip()]
    if not queries:
        sys.exit(f"no queries in {queries_path}")
    ids = [q["_id"] for q in queries]
    texts = {q["_id"]: q["text"] for q in queries}
    probe_ids = probe_set(ids, tail, PROBE_BULK_QUERIES)
    print(f"[multivector] probe set: {len(probe_ids)} queries ({len(tail)} tail + up to "
          f"{PROBE_BULK_QUERIES} bulk)")

    # Spec §4: a fresh run directory holds only beir/ and qrels.trec at probe time, so runs/
    # does not exist yet -- created before the embedding loop so an unwritable path fails in
    # the first second, not after every measurement below.
    runs_dir = os.path.join(args.run_dir, "runs")
    os.makedirs(runs_dir, exist_ok=True)

    vectors = {}
    for i, qid in enumerate(probe_ids, start=1):
        # Same route and empty prefix as cmd_query (spec A6, A23): document_prefix is
        # positional with no default.
        vectors[qid] = ingest.embed(texts[qid], args.model, "", args.embed_url)
        if i % 10 == 0 or i == len(probe_ids):
            print(f"[multivector] embedded {i}/{len(probe_ids)} probe queries")

    # Step 0: is the arm's beam settable through params.hnsw_ef at all? Three configurations
    # at the arm's own limit (DOCUMENT_BUDGET): no params (Qdrant's default beam), hnsw_ef 65
    # (the operating point), hnsw_ef 1000 (a positive control -- if even this doesn't move the
    # ranking, the collection isn't approximating over this probe set at all).
    print("[multivector] step 0: is the arm's beam settable via params.hnsw_ef?")
    mv_configs = {"default": None, 65: {"hnsw_ef": 65}, 1000: {"hnsw_ef": 1000}}
    mv_rankings = {label: {} for label in mv_configs}
    for qid in probe_ids:
        vec = vectors[qid]
        for label, params in mv_configs.items():
            body = {"query": [vec], "limit": DOCUMENT_BUDGET, "with_payload": ["docId"]}
            if params is not None:
                body["params"] = params
            status, resp = ingest.qdrant_request(
                "POST", f"/collections/{args.multivector_collection}/points/query", body)
            if status != 200:
                sys.exit(f"probe step0 query {qid} ({label}): HTTP {status} {resp}")
            ranked = rank_multivector_points(resp["result"]["points"], DOCUMENT_BUDGET)
            mv_rankings[label][qid] = [doc_id for doc_id, _ in ranked]

    settable_n = sum(1 for qid in probe_ids if mv_rankings["default"][qid] != mv_rankings[65][qid])
    positive_control_n = sum(1 for qid in probe_ids if mv_rankings[65][qid] != mv_rankings[1000][qid])
    print(f"[multivector] settable_n={settable_n} positive_control_n={positive_control_n} "
          f"(of {len(probe_ids)} probe queries)")

    # Step 1: sweep each collection's own beam constant and, per sweep point, count how many
    # queries' top-50 document ranking differs from that collection's operating point.
    print("[multivector] step 1: sweeping each collection's beam constant")
    key_to_doc = {p["payload"]["key"]: p["payload"]["docId"]
                  for p in scroll(args.object_collection, False, ["key", "docId"])}

    control_operating_ef = 250
    control_rankings = {}
    for ef in CONTROL_HNSW_EF_SWEEP:
        per_query = {}
        for qid in probe_ids:
            body = {"vector": {"name": CHUNK_VECTOR_NAME, "vector": vectors[qid]},
                     "limit": CHUNK_TOP_K, "with_payload": ["parent_id"], "params": {"hnsw_ef": ef}}
            status, resp = ingest.qdrant_request(
                "POST", f"/collections/{args.chunks_collection}/points/search", body)
            if status != 200:
                sys.exit(f"probe step1 control query {qid} (hnsw_ef={ef}): HTTP {status} {resp}")
            ranked = rank_chunk_hits(resp["result"], key_to_doc, DOCUMENT_BUDGET)
            per_query[qid] = [doc_id for doc_id, _ in ranked]
        control_rankings[ef] = per_query
        print(f"[multivector] control hnsw_ef={ef} done")
    control_agreement = {
        ef: sum(1 for qid in probe_ids
                if control_rankings[ef][qid] != control_rankings[control_operating_ef][qid])
        for ef in CONTROL_HNSW_EF_SWEEP
    }

    arm_operating_ef = 65
    arm_rankings = {}
    for ef in ARM_HNSW_EF_SWEEP:
        per_query = {}
        for qid in probe_ids:
            body = {"query": [vectors[qid]], "limit": DOCUMENT_BUDGET, "with_payload": ["docId"],
                     "params": {"hnsw_ef": ef}}
            status, resp = ingest.qdrant_request(
                "POST", f"/collections/{args.multivector_collection}/points/query", body)
            if status != 200:
                sys.exit(f"probe step1 arm query {qid} (hnsw_ef={ef}): HTTP {status} {resp}")
            ranked = rank_multivector_points(resp["result"]["points"], DOCUMENT_BUDGET)
            per_query[qid] = [doc_id for doc_id, _ in ranked]
        arm_rankings[ef] = per_query
        print(f"[multivector] arm hnsw_ef={ef} done")
    arm_agreement = {
        ef: sum(1 for qid in probe_ids if arm_rankings[ef][qid] != arm_rankings[arm_operating_ef][qid])
        for ef in ARM_HNSW_EF_SWEEP
    }
    print(f"[multivector] control differs-from-operating-point (ef={control_operating_ef}): {control_agreement}")
    print(f"[multivector] arm differs-from-operating-point (ef={arm_operating_ef}): {arm_agreement}")

    probe_path = os.path.join(runs_dir, "probe.json")
    result = {
        "tail_query_ids": tail,
        "corpus_size": corpus_size,
        "probe_query_ids": probe_ids,
        "settable_n": settable_n,
        "positive_control_n": positive_control_n,
        "control_hnsw_ef_sweep": list(CONTROL_HNSW_EF_SWEEP),
        "arm_hnsw_ef_sweep": list(ARM_HNSW_EF_SWEEP),
        "probe_bulk_queries": PROBE_BULK_QUERIES,
        "control_operating_ef": control_operating_ef,
        "arm_operating_ef": arm_operating_ef,
        "control_agreement": {str(ef): n for ef, n in control_agreement.items()},
        "arm_agreement": {str(ef): n for ef, n in arm_agreement.items()},
        "collections": {
            name: {
                "name": name,
                "status": info["status"],
                "points_count": info["points_count"],
                "indexed_vectors_count": info["indexed_vectors_count"],
                "segments_count": info["segments_count"],
            }
            for name, info in collections.items()
        },
    }
    with open(probe_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
    print(f"[multivector] wrote {probe_path}")

    if settable_n == 0 and positive_control_n == 0:
        mv_info = collections[args.multivector_collection]
        sys.exit(
            f"arm beam is not settable through params.hnsw_ef across {len(probe_ids)} probe "
            f"queries (settable_n=0, positive_control_n=0): the design's correction does not "
            f"work as specified. Stop -- this run needs re-approval before any gated "
            f"measurement. '{args.multivector_collection}' indexed_vectors_count="
            f"{mv_info['indexed_vectors_count']:,} of points_count={mv_info['points_count']:,} "
            f"(an unindexed collection searches exactly, which would make all three step-0 "
            f"configurations agree by construction -- check that first)."
        )


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

    q = sub.add_parser("query", help="raw Qdrant runs for both layouts + latency sidecar")
    add_common_args(q)
    q.add_argument("--run-dir", required=True, help="run directory holding beir/queries.jsonl; writes runs/")
    q.add_argument("--model", required=True, help="embedding model id, sent as the request's model field")
    q.add_argument("--embed-url", required=True, help="TEI base URL, e.g. http://localhost:8091")
    q.add_argument("--mv-hnsw-ef", type=int, default=65,
                   help="params.hnsw_ef for the multivector arm (default 65: the corpus-fraction "
                        "match to the control's 250-node beam). Values below the query's own limit "
                        "are clamped up to it by Qdrant and are therefore inert.")
    q.set_defaults(func=cmd_query)

    s = sub.add_parser("stats", help="points / indexed / segments / disk for both collections")
    add_common_args(s)
    s.add_argument("--run-dir", required=True, help="run directory; writes runs/storage.json")
    s.set_defaults(func=cmd_stats)

    p = sub.add_parser("probe", help="exactness probe: is either arm's retrieval approximate?")
    add_common_args(p)
    p.add_argument("--run-dir", required=True, help="run directory holding beir/queries.jsonl; writes runs/probe.json")
    p.add_argument("--model", required=True)
    p.add_argument("--embed-url", required=True)
    p.add_argument("--tail-qrels", required=True, help="qrels.trec of the ORIGINAL run (its run-dir root, not runs/)")
    p.add_argument("--tail-control-run", required=True, help="original gte-chunks-raw.chunks.trec")
    p.add_argument("--tail-arm-run", required=True, help="original gte-multivector-raw.chunks.trec")
    p.set_defaults(func=cmd_probe)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
