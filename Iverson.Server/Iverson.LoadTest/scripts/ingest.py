#!/usr/bin/env python3
"""Write a BEIR-shaped corpus straight into Qdrant, reproducing the exact point contract
`IntelligenceStoreConsumer` writes for `Iverson.LoadTest.Entities.BenchmarkDocument` — so the
existing gRPC search endpoints (SearchSimilar, SearchChunks) keep working against it unchanged.

This bypasses the gRPC/Kafka write path entirely (no server, no Postgres row, no consumer
drain to wait on): it embeds locally via Ollama and upserts to Qdrant over its REST API. That
is the whole point — the normal path costs ~34s/document; this is the fast path a benchmark
sweep needs.

The point contract (fixed; a divergence from any of this makes results incomparable with a
C#-written corpus, and the search endpoints would silently return nothing for a wrong id):

    object collection   {object-collection}   default benchmark_documents_tenant_bypass
      vectors: body_vector, body_centroid -- both 768-dim, Cosine
      payload: key, docId, title, body, ownerId, __TenantId

    chunks collection   {chunks-collection}   default benchmark_documents_chunks_tenant_bypass
      vectors: body_vector -- 768-dim, Cosine
      payload: text, parent_id, field ("Body"), chunk_index (a STRING), ownerId

Point ids are derived the same way IntelligenceStoreConsumer.KeyToUlong /
ComputeChunkPointId derive them, verified byte-for-byte against real C#-written points
(11359556208391300 for object key 01a03beb-3e97-7918-8474-9bc8745b2800; 29047809656757676 for
the first Body chunk of parent 01a0364f-d6a5-7125-a74e-799fa38cf7cf) — see the module
docstring's sibling task report for the check. Document keys are NOT server-assigned UUIDv7s
here (there is no server in this path); they are `uuid5(NAMESPACE, "{corpus}:{docId}")`,
which makes every point id a pure function of --corpus's directory name and the corpus's own
document id. Re-running is therefore an idempotent upsert, and --resume is simply "skip the
docIds already recorded in the progress file".

CRITICAL: benchmark_documents_tenant_bypass (450 points) and benchmark_documents_chunks_
tenant_bypass (554 points) hold the ONLY C#-written reference points in existence, each
costing ~34s/document to reproduce. --drop deletes and recreates a collection empty. NEVER
run --drop against those two collection names outside of the plan's Task 5 (which does so
deliberately, after the reference points have already been used to verify this script).
Point --object-collection/--chunks-collection at throwaway names to exercise --drop.

Usage:
    python3 Iverson.Server/Iverson.LoadTest/scripts/ingest.py \\
        --corpus /path/to/corpus-dir/beir/corpus.jsonl \\
        --key-map-path /path/to/keymap.json

    # Resume after an interrupted run (skips docIds already in <key-map-path>.progress):
    python3 Iverson.Server/Iverson.LoadTest/scripts/ingest.py \\
        --corpus /path/to/corpus-dir/beir/corpus.jsonl \\
        --key-map-path /path/to/keymap.json --resume

    # Small slice, for trying the script out without paying for a full corpus:
    python3 Iverson.Server/Iverson.LoadTest/scripts/ingest.py \\
        --corpus /path/to/corpus.jsonl --key-map-path /tmp/keymap.json --limit 20

Writes, alongside --key-map-path:
    <key-map-path>            flat {parentKey: docId} JSON -- what KeyMap.LoadAsync reads
    <key-map-path>.progress   one completed docId per line, appended+flushed per document
    <key-map-path>.stats.json documents/chunks/embed_calls/embeds_saved/elapsed_seconds +
                               started_at/finished_at -- ACCUMULATES across invocations
                               against the same --key-map-path (counters and elapsed_seconds
                               increment, started_at is preserved from the first run), so a
                               --resume'd run reports the same totals a single unbroken run
                               would have. elapsed_seconds is the SUM of each invocation's own
                               (finish - start), i.e. working time only -- started_at/
                               finished_at instead span first-start to last-finish, so after a
                               --resume across a gap (crash, or just stopping overnight) that
                               span includes the idle time between runs and elapsed_seconds
                               does not.

Requires Qdrant at http://localhost:6333 and Ollama (nomic-embed-text, already pulled) at
http://localhost:11434 -- see scripts/stack.py's `ingest` tier.
"""

import argparse
import json
import math
import os
import sys
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone

QDRANT_URL = "http://localhost:6333"
QDRANT_API_KEY = "dev-only-not-for-production-qdrant-key-0123456789"
OLLAMA_URL = "http://localhost:11434"

DEFAULT_OBJECT_COLLECTION = "benchmark_documents_tenant_bypass"
DEFAULT_CHUNKS_COLLECTION = "benchmark_documents_chunks_tenant_bypass"

# Read 2026-08-26 from the live collections' own payload_schema (GET /collections/{name}) --
# what IntelligenceStoreConsumer's collection-creation path indexes today. All "keyword".
# ensure_collection recreates these when it creates a collection from scratch, so a --drop
# does not leave the read side (tenant/ownership filtering) running an unindexed scan.
OBJECT_PAYLOAD_INDEXES = ["__TenantId", "body", "docId", "ownerId", "title"]
CHUNKS_PAYLOAD_INDEXES = ["field", "ownerId", "parent_id"]

# Read 2026-08-26 from a live point written by IntelligenceStoreConsumer (via
# BenchmarkIngestScenario's bypass identity) in benchmark_documents_tenant_bypass. Hard-coded
# rather than read from Qdrant at runtime: Task 5 of this plan drops both live collections
# before the measured sweep runs, so by then there is no C#-written point left to read these
# from, and every point this script wrote would carry null where the read path filters on
# ownership and routes by tenant.
OWNER_ID = "8f5c3da2e5ecbad46e1dab4890c109a4826919be420f5d7a3d0029a9fbff273e"
TENANT_ID = "tenant_bypass"

# Any fixed UUID works as the uuid5 namespace -- the only requirement is that it never changes
# between runs, since key = uuid5(NAMESPACE, "{corpus}:{docId}") must be reproducible across
# processes and machines. uuid.NAMESPACE_URL is a stdlib constant, so nothing else needs to
# agree on a value out-of-band.
NAMESPACE = uuid.NAMESPACE_URL

# Mirrors IntelligenceStoreConsumer.SplitIntoChunks for BenchmarkDocument.Body specifically
# (MaxTokens/Overlap resolved to chars via the consumer's 1 token ~= 4 chars approximation).
MAX_CHARS = 2048
STEP = 1792

M = (1 << 64) - 1


# ── Point-id derivation (verified against real C#-written points; see module docstring) ──

def fnv1a(s: str) -> int:
    h = 14695981039346656037
    for b in s.encode():
        h = ((h ^ b) * 1099511628211) & M
    return h


def key_to_ulong(key: str) -> int:
    return int.from_bytes(uuid.UUID(key).bytes[8:16], "little")


def chunk_point_id(parent_id: int, field: str, idx: int) -> int:
    h = (fnv1a(field) * 1000003 + idx) & M  # group before multiplying -- & binds looser than *
    return (parent_id ^ ((h * 0x9E3779B97F4A7C15) & M)) & M


# ── Chunking (mirrors SplitIntoChunks) ─────────────────────────────────────────────────

def split_into_chunks(text, max_chars=MAX_CHARS, step=STEP):
    start, idx = 0, 0
    while start < len(text):
        end = min(start + max_chars, len(text))
        if end < len(text) and not text[end].isspace():
            # C#'s text.LastIndexOf(' ', end, Math.Min(end - start, 50)) examines the range
            # [end - count + 1, end] inclusive (count positions ending at `end`). rfind's stop
            # is exclusive, so the upper bound stays `end` (position `end` itself is unreachable
            # -- the isspace guard above proves text[end] isn't a space) and the lower bound must
            # be end - count + 1, not end - 50: a space at exactly end - 50 with no other space
            # in [end - 49, end - 1] is found by C# but was missed here before this fix.
            count = min(end - start, 50)
            ws = text.rfind(" ", end - count + 1, end)
            if ws > start:
                end = ws
        yield text[start:end].strip(), idx
        idx += 1
        start += step


# ── Centroid (mirrors ComputeCentroid) ─────────────────────────────────────────────────

def is_zero_magnitude(vector):
    return sum(c * c for c in vector) == 0


def compute_centroid(vectors):
    dims = len(vectors[0])
    total = [0.0] * dims
    for v in vectors:
        magnitude = math.sqrt(sum(c * c for c in v))
        for i in range(dims):
            total[i] += v[i] / magnitude
    return [t / len(vectors) for t in total]


# ── Qdrant REST ─────────────────────────────────────────────────────────────────────────

def qdrant_request(method, path, body=None):
    """Returns (status, parsed_json_or_None). Never raises on an HTTP error status -- callers
    decide what status is expected."""
    url = f"{QDRANT_URL}{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("api-key", QDRANT_API_KEY)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req) as resp:
            raw = resp.read()
            return resp.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            parsed = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            parsed = None
        return e.code, parsed
    except urllib.error.URLError as e:
        sys.exit(f"could not reach Qdrant at {url}: {e}")


def collection_exists(name):
    status, _ = qdrant_request("GET", f"/collections/{name}")
    return status == 200


def create_payload_index(collection, field_name, field_schema="keyword"):
    status, body = qdrant_request(
        "PUT",
        f"/collections/{collection}/index",
        {"field_name": field_name, "field_schema": field_schema},
    )
    if status != 200:
        sys.exit(
            f"failed to create payload index '{field_name}' on '{collection}': HTTP {status} {body}"
        )


def ensure_collection(name, vectors_config, payload_indexes=()):
    """Create-if-missing. Leaves an existing collection (and its indexes) untouched -- callers
    that want a clean slate use --drop first. payload_indexes is a list of field names to index
    as "keyword", created only on the create path -- these are read straight off the live
    collections (GET /collections/{name}'s payload_schema) and are exactly what
    IntelligenceStoreConsumer's own collection-creation path (IVectorSchemaManager) creates, so
    a --drop-then-recreate must not leave the read side without them: SearchSimilar/SearchChunks
    filter on ownerId/__TenantId/parent_id/field for tenant and row-authorization scoping, and
    those filters degrade from an indexed lookup to a full collection scan without this.
    on_disk_payload is set false at creation to match the live collections (Qdrant's own current
    default is true)."""
    if collection_exists(name):
        return
    status, body = qdrant_request(
        "PUT",
        f"/collections/{name}",
        {"vectors": vectors_config, "on_disk_payload": False},
    )
    if status != 200:
        sys.exit(f"failed to create collection '{name}': HTTP {status} {body}")
    print(f"[ingest] created collection '{name}'")
    for field_name in payload_indexes:
        create_payload_index(name, field_name)
    if payload_indexes:
        print(f"[ingest] created {len(payload_indexes)} payload index(es) on '{name}': {', '.join(payload_indexes)}")


def drop_collection(name):
    status, body = qdrant_request("DELETE", f"/collections/{name}")
    if status != 200:
        sys.exit(f"failed to drop collection '{name}': HTTP {status} {body}")
    print(f"[ingest] dropped collection '{name}'")


def upsert_points(collection, points):
    if not points:
        return
    status, body = qdrant_request(
        "PUT", f"/collections/{collection}/points?wait=true", {"points": points}
    )
    if status != 200:
        sys.exit(f"failed to upsert {len(points)} point(s) into '{collection}': HTTP {status} {body}")


# ── Ollama ──────────────────────────────────────────────────────────────────────────────

def embed(text):
    url = f"{OLLAMA_URL}/api/embed"
    data = json.dumps({"model": "nomic-embed-text", "input": text}).encode()
    req = urllib.request.Request(url, data=data, method="POST")
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req) as resp:
            parsed = json.loads(resp.read())
    except urllib.error.URLError as e:
        sys.exit(f"embed request to {url} failed: {e}")
    embeddings = parsed.get("embeddings")
    if not embeddings:
        sys.exit(f"ollama /api/embed returned no embeddings (input length {len(text)}): {parsed}")
    return embeddings[0]


# ── Corpus ──────────────────────────────────────────────────────────────────────────────

def read_corpus(path):
    """Same validation JsonlCorpusParser applies: missing/empty "_id" or blank "text" fails
    the whole run rather than silently producing a document with nothing indexed."""
    docs = []
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            doc_id = row.get("_id")
            if not doc_id:
                sys.exit(f"{path}:{lineno}: missing or empty \"_id\"")
            title = row.get("title") or ""
            text = row.get("text") or ""
            if not text.strip():
                sys.exit(f"{path}:{lineno} (_id \"{doc_id}\"): missing or empty \"text\"")
            docs.append((doc_id, title, text))
    return docs


# ── Ingest one document ─────────────────────────────────────────────────────────────────

def ingest_document(key, doc_id, title, body, object_collection, chunks_collection, stats):
    parent_id = key_to_ulong(key)
    chunks = list(split_into_chunks(body))

    # Reuse gate: an identity check, never a length check alone. When body carries no
    # surrounding whitespace AND is short enough that split_into_chunks yields exactly one
    # chunk equal to body itself, one embed call fills both body_vector and that chunk's
    # vector -- otherwise the chunk text (stripped, windowed) is a different string from body
    # and needs its own embedding.
    reuse = body == body.strip() and len(body) <= STEP
    body_vector = embed(body)
    stats["embed_calls"] += 1

    if reuse:
        assert len(chunks) == 1 and chunks[0][0] == body, (
            "reuse gate fired but split_into_chunks did not yield a single chunk equal to body"
        )
        chunk_vectors = [body_vector]
        stats["embeds_saved"] += 1
    else:
        chunk_vectors = []
        for chunk_text, _ in chunks:
            chunk_vectors.append(embed(chunk_text))
            stats["embed_calls"] += 1

    centroid_input = [v for v in chunk_vectors if not is_zero_magnitude(v)]
    centroid = compute_centroid(centroid_input) if centroid_input else None

    object_vectors = {"body_vector": body_vector}
    if centroid is not None:
        object_vectors["body_centroid"] = centroid

    object_payload = {
        "key": key,
        "docId": doc_id,
        "title": title,
        "body": body,
        "ownerId": OWNER_ID,
        "__TenantId": TENANT_ID,
    }
    upsert_points(
        object_collection,
        [{"id": parent_id, "vector": object_vectors, "payload": object_payload}],
    )

    chunk_points = []
    for (chunk_text, idx), vec in zip(chunks, chunk_vectors):
        chunk_points.append(
            {
                "id": chunk_point_id(parent_id, "Body", idx),
                "vector": {"body_vector": vec},
                "payload": {
                    "text": chunk_text,
                    "parent_id": key,
                    "field": "Body",
                    "chunk_index": str(idx),
                    "ownerId": OWNER_ID,
                },
            }
        )
    upsert_points(chunks_collection, chunk_points)

    stats["documents"] += 1
    stats["chunks"] += len(chunk_points)


# ── Stats sidecar ───────────────────────────────────────────────────────────────────────

def update_stats_sidecar(path, run_stats, started_at_iso, finished_at_iso, elapsed_seconds):
    """elapsed_seconds is THIS invocation's own (finish - start), summed into whatever the
    sidecar already holds -- working time only. started_at/finished_at, by contrast, span
    first-start to last-finish and so include any idle gap between a crash and a later
    --resume; they are kept for display but must not be used to derive throughput (see
    module docstring)."""
    existing = {}
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            existing = json.load(f)

    merged = {
        "documents": existing.get("documents", 0) + run_stats["documents"],
        "chunks": existing.get("chunks", 0) + run_stats["chunks"],
        "embed_calls": existing.get("embed_calls", 0) + run_stats["embed_calls"],
        "embeds_saved": existing.get("embeds_saved", 0) + run_stats["embeds_saved"],
        "elapsed_seconds": existing.get("elapsed_seconds", 0) + elapsed_seconds,
        "started_at": existing.get("started_at", started_at_iso),
        "finished_at": finished_at_iso,
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(merged, f, indent=2)
    return merged


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--corpus", required=True, help="path to a BEIR-shaped corpus.jsonl")
    ap.add_argument("--key-map-path", required=True)
    ap.add_argument(
        "--limit", type=int, default=None, help="ingest only the first N documents (for trying the script out)"
    )
    ap.add_argument(
        "--resume", action="store_true",
        help="skip docIds already recorded in <key-map-path>.progress",
    )
    ap.add_argument(
        "--drop", action="store_true",
        help="delete and recreate both collections, and delete the progress file and stats "
             "sidecar, before ingesting. NEVER pass this with the live benchmark_documents_* "
             "collection names outside of the plan's Task 5 -- see module docstring.",
    )
    ap.add_argument("--object-collection", default=DEFAULT_OBJECT_COLLECTION)
    ap.add_argument("--chunks-collection", default=DEFAULT_CHUNKS_COLLECTION)
    args = ap.parse_args()

    progress_path = f"{args.key_map_path}.progress"
    stats_path = f"{args.key_map_path}.stats.json"

    if args.drop:
        drop_collection(args.object_collection)
        drop_collection(args.chunks_collection)
        for p in (progress_path, stats_path):
            if os.path.exists(p):
                os.remove(p)
                print(f"[ingest] removed {p}")

    ensure_collection(
        args.object_collection,
        {"body_vector": {"size": 768, "distance": "Cosine"}, "body_centroid": {"size": 768, "distance": "Cosine"}},
        payload_indexes=OBJECT_PAYLOAD_INDEXES,
    )
    ensure_collection(
        args.chunks_collection,
        {"body_vector": {"size": 768, "distance": "Cosine"}},
        payload_indexes=CHUNKS_PAYLOAD_INDEXES,
    )

    corpus_name = os.path.basename(os.path.dirname(os.path.abspath(args.corpus))) or "corpus"
    docs = read_corpus(args.corpus)
    if args.limit is not None:
        docs = docs[: args.limit]
    print(f"[ingest] {len(docs):,} document(s) from {args.corpus} (corpus name '{corpus_name}')")

    already_done = set()
    if args.resume and os.path.exists(progress_path):
        with open(progress_path, encoding="utf-8") as f:
            already_done = {line.strip() for line in f if line.strip()}
        print(f"[ingest] --resume: {len(already_done):,} docId(s) already recorded in {progress_path}")

    key_map = {}
    run_stats = {"documents": 0, "chunks": 0, "embed_calls": 0, "embeds_saved": 0}
    started_at = datetime.now(timezone.utc)
    skipped = 0

    with open(progress_path, "a", encoding="utf-8") as progress_f:
        for i, (doc_id, title, body) in enumerate(docs, start=1):
            key = str(uuid.uuid5(NAMESPACE, f"{corpus_name}:{doc_id}"))
            key_map[key] = doc_id

            if args.resume and doc_id in already_done:
                skipped += 1
                continue

            ingest_document(key, doc_id, title, body, args.object_collection, args.chunks_collection, run_stats)
            progress_f.write(doc_id + "\n")
            progress_f.flush()

            if i % 100 == 0 or i == len(docs):
                print(f"[ingest] {i:,}/{len(docs):,} processed ({skipped:,} skipped via --resume)")

    with open(args.key_map_path, "w", encoding="utf-8") as f:
        json.dump(key_map, f, indent=2)
    print(f"[ingest] key map ({len(key_map):,} entries) written to {args.key_map_path}")

    finished_at = datetime.now(timezone.utc)
    elapsed_seconds = (finished_at - started_at).total_seconds()
    merged = update_stats_sidecar(
        stats_path, run_stats, started_at.isoformat(), finished_at.isoformat(), elapsed_seconds
    )
    print(
        f"[ingest] this run: {run_stats['documents']:,} documents, {run_stats['chunks']:,} chunks, "
        f"{run_stats['embed_calls']:,} embed calls ({run_stats['embeds_saved']:,} saved)"
    )
    print(
        f"[ingest] cumulative ({stats_path}): {merged['documents']:,} documents, "
        f"{merged['chunks']:,} chunks, {merged['embed_calls']:,} embed calls "
        f"({merged['embeds_saved']:,} saved), {merged['started_at']} -> {merged['finished_at']}"
    )


if __name__ == "__main__":
    main()
