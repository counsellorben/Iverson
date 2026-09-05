# Multivector Experiment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-04-multivector-experiment-design.md` (commit SHA: `5563454`; CDR round 1 approved as-is at `f2b67bd`)

**Goal:** Measure whether one Qdrant MaxSim multivector point per document matches Iverson's per-chunk collection on SciFact ranking quality, and what it costs in points, storage and query latency — producing a go/no-go verdict on adoption.

**Architecture:** One gte-modernbert-base ingest through `ingest.py` into the per-chunk collection pair; a new `multivector.py` that regroups those chunk points into a Cosine/MaxSim multivector collection (byte-identical vectors), queries both layouts directly from Python with interleaved latency capture, and reports storage; `benchmark-query` for the API control run; `report.py --baseline` for scoring. One templated `--max-batch-tokens` argument on the compose `tei-embed` service so gte can start on this box.

**Tech stack:** Python 3.14 stdlib (`multivector.py`, importing `ingest.py`), pytest 9.1.1, Qdrant 1.18.2 REST, TEI cpu-1.8 (ONNX backend), docker compose 2.40, the existing C# benchmark harness (`Iverson.LoadTest`), `report.py` with `ir_measures` from `~/repositories/iverson-benchmark-corpora/python-libs`.

---

## Global Constraints

- **Precondition for Tasks 4–5:** the `embedding-migration` branch has merged to local main (this plan reuses its `ingest.py` flags, `tei-embed` service and per-arm procedure) and the box is on the SciFact nomic baseline (5,183 object / 19,967 chunk points, 768 dims, API defaults). Tasks 1–3 may run on a worktree based on the merged main at any time.
- **Box discipline (spec §4, §6):** nothing else runs on the box during the ingest; every `docker compose` call is a single-service `--no-deps` action from `Iverson.Server/`; **never call `stack.py`** after TEI is up — its out-of-tier stop halts `iverson-tei-embed`; never a tier-wide `up` (it recreates postgres/authentik).
- **Window and counts are fixed:** `--chunk-max-chars 512 --chunk-step 448`; the ingest sidecar must report exactly `documents 5183, chunks 19967`; any other number invalidates the arm.
- **Model id everywhere:** `Alibaba-NLP/gte-modernbert-base` — on the TEI recreate (`EMBED_MODEL_ID`), the ingest (`--model`), the API recreate (`BENCH_EMBED_MODEL` and `EMBED_MODEL_ID`) and the query script (`--model`). No prefix on either side.
- **TEI batch tokens:** `TEI_MAX_BATCH_TOKENS=4096` on every `tei-embed` recreate for this model (compose default 16384 OOM-kills warm-up on this box, spec §10 row 7).
- **Run labels are fixed:** `gte-chunks-api`, `gte-chunks-raw`, `gte-multivector-raw`; run directory `~/repositories/iverson-benchmark-corpora/scifact-gte-<date>/`; snapshots in `scifact-gte-qdrant-snapshots/`. Existing run directories are read-only evidence.
- **Scoring uses `--baseline`, never `--pair`** (spec A13: `--pair` enforces pool invariance and would call the multivector arm invalid).
- `docs/plans/` is gitignored: commit plan artifacts with `git add -f`.

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/multivector.py` — `build` (regroup chunk points into the multivector collection), `query` (raw Qdrant runs for both layouts + latency sidecar), `stats` (points/segments/disk table). Pure functions at the top, I/O below.
- `Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py` — pytest for the pure functions.
- `docs/plans/2026-09-GATE-multivector.md` — the verdict (Task 5).
- (outside the repo, untracked) `~/repositories/iverson-benchmark-corpora/scifact-gte-<date>/` and `scifact-gte-qdrant-snapshots/`.

**Modify**
- `Iverson.Server/docker-compose.yml:175` — `tei-embed` command gains `"--max-batch-tokens", "${TEI_MAX_BATCH_TOKENS:-16384}"`.

## Inherited from spec

The following were verified by `thorough-brainstorming` (spec §10–§11) and are NOT re-verified here:

- A1/A20/A21 — TEI cpu-1.8 (1.8.3) serves gte-modernbert-base on CPU: 768 unit-length CLS dims; compose-default warm-up OOM-kills (6.9 GB RSS); `--max-batch-tokens 4096` → ready ≈ 60 s at 3.5 GB, `max_input_length` 4096; `--max-input-length` is not a flag; 1.16 s per 512-char chunk, ≈ 3 s per whole body under load (§10 rows 6–9).
- A2 — `tei-embed` service: image cpu-1.8, `--model-id ${EMBED_MODEL_ID:-…} --auto-truncate`, port 8091, `tei_models` volume (`docker-compose.yml:171-179` on the migration branch).
- A3/A4 — `ingest.py` flags `--model/--embed-url/--chunk-max-chars/--chunk-step`; `embed(text, model, document_prefix, embed_url)`; `qdrant_request(method, path, body) -> (status, parsed)` with the api-key header (`ingest.py:145-146, 276-300, 530`).
- A5/A6/A23 — gte's family is absent from both prefix tables → empty document and query prefix on the Python and C# sides; the `/info` guard is an ordinal `model_id` compare; both embed via `/v1/embeddings`.
- A7/A8/A9/A17/A24 — Qdrant 1.18.2 REST: multivector create (`Cosine` + `max_sim`), upsert `vector: [[…],[…]]`, scroll shapes (named vectors as `{"body_vector": […]}`, `next_page_offset`), named search, `points/query` with a one-row multivector = max-over-rows, wrong dimension → 400, info, delete (§10 rows 3–5).
- A10 — chunk payload `text, parent_id (GUID key), field, chunk_index (string), ownerId`; object payload `key, docId, title, body, ownerId, __TenantId`; `key_to_ulong(key) == object point id` (§10 row 10).
- A11 — `DocumentBudget` 50 × `ChunkBudgetMultiplier` 5 = 250 chunks; `CollapseByDocId` = max per doc, descending, truncate after (`BenchmarkQueryScenario.cs:37-38`, `DocumentRanking.cs:29-46`).
- A12 — run-dir corpus at 512/448 → 5,183 / 19,967, budget reach 64.9 ≥ 50.
- A13 — `report.py --pair` enforces pool invariance; `--baseline` is the cross-pool mode; a run with no `.meta.json` scores as `build: unknown`.
- A14 — `<run>/beir/queries.jsonl` has `_id` + `text`, 300 rows; `qrels.trec` covers 300 queries.
- A15 — migration plan Task 6 steps 4–6 and Task 7 step 5 (TEI recreate, snapshot loop, schema delete, API recreate, restore loop) are the per-arm procedure.
- A16 — `GET /collections/{name}` → `points_count/indexed_vectors_count/segments_count`; collection dirs at `/qdrant/storage/collections/<name>` inside `iverson-qdrant` (§10 row 11).
- A18/A19 — `stack.py` stops out-of-tier containers incl. `iverson-tei-embed`; `ingest.py --drop` touches only its two named collections; the API's `ApplyCollectionAsync` touches only its schema's collection.
- A22 — pytest runner: `python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/<file>.py -q`.

## Verified plan-level assumptions

Verified 2026-09-04/05 against the `embedding-migration` worktree at `f2706a4` (the code this plan runs on after the merge) and the live stack, read-only:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `scripts/multivector.py` and `scripts/test_multivector.py` do not exist on main or the migration branch | `ls` → no such file (both) |
| P2/P27 | Command | `docker-compose.yml:175` is exactly `command: ["--model-id", "${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}", "--auto-truncate"]`; `docker compose --profile tei config` renders the list with the env default interpolated | `sed -n 175p`; `config` output shows `- --model-id` / `- BAAI/bge-base-en-v1.5` / `- --auto-truncate` |
| P3 | Signature | `import ingest` from the scripts dir performs no network I/O (socket guard) and exports `qdrant_request, key_to_ulong, embed, DEFAULT_OBJECT_COLLECTION, DEFAULT_CHUNKS_COLLECTION, QDRANT_URL, HTTP_TIMEOUT_SECONDS, EMBED_RETRIES`; defaults `benchmark_documents_tenant_bypass` / `benchmark_documents_chunks_tenant_bypass`; `HTTP_TIMEOUT_SECONDS` 300 | ran the import under a socket guard |
| P4 | Code validity | `test_report.py` imports its module via `sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))` then `import report  # noqa: E402` | `test_report.py:8-15` |
| P5 | Signature | `ingest.key_to_ulong(key: str) -> int`; `ingest.embed(text, model, document_prefix, embed_url)` posts `{model, input}` to `<embed_url>/v1/embeddings` (served by Ollama and TEI alike) and returns `data[0]["embedding"]` (list of floats) | `inspect.signature`; `ingest.py:527-557` (section header "Embedding backend (Ollama or TEI, /v1/embeddings)") |
| P6 | Code validity | `TrecRunWriter` writes `{queryId} Q0 {docId} {rank} {score:F6} {runTag}`, space-separated, rank from 1, runTag = `--config-label` | `TrecRunWriter.cs:6-7, 25-31`; `BenchmarkQueryScenario.cs:287-288` |
| P7 | Command | `report.py --run <dir>` expands to sorted `*.trec` (qrels files excluded); `--baseline` is excluded from the comparison set by absolute path; a mix of `--run` files and dirs is accepted | `report.py:117-141, 533-552` |
| P8 | Command | `report.py --stats-path` reads `documents, chunks, embed_calls, embeds_saved, elapsed_seconds` from the ingest sidecar | `report.py:320-333` |
| P9 | Code validity | Qdrant scroll accepts `with_payload` as a field list and `offset` = `next_page_offset` for paging; object scroll with `["key","docId"]` returns exactly those | live scroll on the chunks/object collections → 200, payloads `{parent_id, chunk_index}` / `{key, docId}`, second page ids follow the offset |
| P10 | Code validity | `GET /collections/benchmark_documents_multivector_tenant_bypass` → 404 `doesn't exist` while absent (existence check) | live GET → 404 |
| P12 | Command | `docker exec iverson-qdrant du -sh <dir>` prints `<size>\t<path>` | spec §10 row 11 (`28M\t/qdrant/…`) |
| P13 | Command | `benchmark-query` invocation: `cd Iverson.Server/Iverson.LoadTest && dotnet run -c Release -- benchmark-query --corpus-path $X --key-map-path $X/keymap.json --output-dir $X/runs --config-label <label>` after `source /home/ben/iverson-benchmark-data/bench-env.sh`; outputs `<label>.chunks.trec`, `<label>.similar.trec`, `<label>.meta.json` | migration plan `:808-812, 859-860`; `BenchmarkQueryScenario.cs:220, 285` |
| P14 | Command | The migration plan's exact commands for TEI recreate (`:821-823`), run-dir copy (`:819`), ingest (`:826`), snapshot loop (`:839-846`), schema delete + API recreate + identity checks (`:851-857`), restore (`:905-921`, `RESTORE.md`) | read; copied verbatim below with the model/paths substituted |
| P15 | Command | compose runs from `Iverson.Server/` (project `iversonserver`, network `iversonserver_default`) | migration plan P16 + its Task 6 commands |
| P17 | Ordering | Tasks 2 and 3 import only `ingest.py` (pre-existing) and each other's pure functions within one file; Task 4 needs Task 1 (compose var) + the merge; Task 5 needs Tasks 2–4 | by construction; no later-task symbol is used earlier |
| P18 | Command | pytest 9.1.1 is installed for `python3` | `python3 -m pytest --version` |
| P19 | File path | run-dir layout `<run>/beir/queries.jsonl`, `<run>/qrels.trec`, `<run>/runs/`, created by `mkdir -p $X/runs && cp -r $SCI/beir $X/ && cp $SCI/qrels.trec $X/` | migration plan `:819`; `scifact-run-2026-08-26/` listing |
| P20 | Consumer impact | Nothing in tests or scripts asserts the `tei-embed` command text; the only `tei-embed` references outside compose are URL strings in `EmbeddingServiceTests.cs` / `EmbeddingServiceResolverTests.cs` | `grep -rln "auto-truncate\|tei-embed"` over `*.cs *.py *.json *.sh` |
| P22 | Code validity | `ir_measures.read_trec_run` parses `1 Q0 15830352 1 0.812345 gte-multivector-raw` → `ScoredDoc('1','15830352',0.812345)` | ran with `PYTHONPATH=…/python-libs` |
| P23 | File path | `docs/plans/2026-09-GATE-multivector.md` does not exist; `**/docs/plans/` is gitignored (`.gitignore:49`) while 45 files under it are tracked → `git add -f` | `ls`; `git check-ignore -v`; `git ls-files` |
| P24 | Command | Commit convention: lowercase imperative subject, optional `area:` prefix only for compose, no Conventional-Commits type | `git log --oneline -8` |
| P26 | Sibling sweep | Every Qdrant response field the script reads: scroll `result.points[].{id, vector, payload}` + `result.next_page_offset`; search `result[].{id, score, payload}`; query `result.points[].{id, score, payload}`; info `result.{points_count, indexed_vectors_count, segments_count, config.params.vectors.body_vector.size}` | spec §10 rows 3–5, 10 + P9 |

## Tasks

### Task 1: Templated `--max-batch-tokens` on the compose `tei-embed` service

**Files:**
- Modify: `Iverson.Server/docker-compose.yml:175`

- [ ] **Step 1: Edit the command.** Replace line 175

```yaml
    command: ["--model-id", "${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}", "--auto-truncate"]
```
with
```yaml
    # TEI's default max_batch_tokens (16384) OOM-kills warm-up for gte-modernbert-base on the 9 GB box
    # (spec 2026-09-04-multivector-experiment-design §10 row 7); that arm passes TEI_MAX_BATCH_TOKENS=4096.
    # The default here is TEI's own, so every bge arm is unchanged.
    command: ["--model-id", "${EMBED_MODEL_ID:-BAAI/bge-base-en-v1.5}", "--auto-truncate", "--max-batch-tokens", "${TEI_MAX_BATCH_TOKENS:-16384}"]
```

- [ ] **Step 2: Verify the render, default and override.**
```bash
cd Iverson.Server
docker compose --profile tei config | grep -A 6 '^  tei-embed:' | grep -A 5 'command:'
# expect the five items ending: - --max-batch-tokens / - "16384"
TEI_MAX_BATCH_TOKENS=4096 docker compose --profile tei config | grep -A 6 '^  tei-embed:' | grep -c '"4096"\|^      - 4096$'   # 1
docker compose config --services | grep -c '^tei-embed$'   # 0 — still profile-gated
```

- [ ] **Step 3: Commit.**
```bash
git add Iverson.Server/docker-compose.yml
git commit -m "compose: template tei-embed's --max-batch-tokens so gte-modernbert-base can start on the laptop"
```

### Task 2: `multivector.py` — pure functions, `build`, `stats`

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/multivector.py`
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py`

**Interfaces**
- Consumes: `ingest.qdrant_request`, `ingest.key_to_ulong`, `ingest.DEFAULT_OBJECT_COLLECTION`, `ingest.DEFAULT_CHUNKS_COLLECTION`.
- Produces: `group_rows`, `resolve_parents`, `scroll`, `collection_info`, `load_objects`, `add_common_args`, the `main()` dispatcher — Task 3 adds `query` to the same file.

- [ ] **Step 1: Write the failing tests.** Create `test_multivector.py`:

```python
"""pytest suite for multivector.py's pure functions. Run with:

        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q

No Qdrant, no TEI: the request bodies are pinned by the spec's live probes (spec §10 rows 3-5),
so these tests cover the logic between them -- regrouping, parent resolution, collapse, TREC
formatting and the latency summary."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import multivector  # noqa: E402

# Live object point verified in the spec (§10 row 10): key -> point id.
KEY = "29beccc1-3d1c-577b-a9a2-114337e70200"
OID = 817174487868073


def chunk(parent, index, row):
    return {"id": 1, "vector": {"body_vector": row}, "payload": {"parent_id": parent, "chunk_index": str(index)}}


def test_group_rows_orders_by_integer_index_not_string():
    groups = multivector.group_rows([chunk(KEY, 10, [10.0]), chunk(KEY, 2, [2.0]), chunk(KEY, 9, [9.0])])
    assert groups == {KEY: [[2.0], [9.0], [10.0]]}


def test_group_rows_keeps_parents_apart():
    groups = multivector.group_rows([chunk("a", 0, [1.0]), chunk("b", 0, [2.0]), chunk("a", 1, [3.0])])
    assert groups == {"a": [[1.0], [3.0]], "b": [[2.0]]}


def test_resolve_parents_builds_points_from_the_object_point():
    points, missing = multivector.resolve_parents({KEY: [[1.0], [2.0]]}, {OID: {"key": KEY, "docId": "15830352"}})
    assert missing == []
    assert points == [{"id": OID, "vector": [[1.0], [2.0]],
                       "payload": {"key": KEY, "docId": "15830352", "chunk_count": 2}}]


def test_resolve_parents_reports_missing_object_point():
    points, missing = multivector.resolve_parents({KEY: [[1.0]]}, {})
    assert points == [] and missing == [KEY]


def test_collapse_keeps_max_not_first_seen():
    assert multivector.collapse_by_doc([("d1", 0.3), ("d1", 0.9), ("d2", 0.5)], 10) == [("d1", 0.9), ("d2", 0.5)]


def test_collapse_truncates_after_collapse_not_before():
    # Three rows, two docs: a truncate-before-collapse would keep only d1.
    assert multivector.collapse_by_doc([("d1", 0.9), ("d1", 0.8), ("d2", 0.7)], 2) == [("d1", 0.9), ("d2", 0.7)]


def test_collapse_sorts_descending_and_first_seen_wins_ties():
    assert multivector.collapse_by_doc([("d2", 0.5), ("d1", 0.5), ("d3", 0.6)], 10) == [("d3", 0.6), ("d2", 0.5), ("d1", 0.5)]


def test_trec_lines_match_trecrunwriter_format():
    assert multivector.trec_lines("1", [("15830352", 0.8123456), ("4983", 0.7)], "gte-multivector-raw") == [
        "1 Q0 15830352 1 0.812346 gte-multivector-raw",
        "1 Q0 4983 2 0.700000 gte-multivector-raw",
    ]


def test_summarize_latency():
    s = multivector.summarize_latency([5.0, 1.0, 3.0, 2.0, 4.0])
    assert s == {"n": 5, "p50_ms": 3.0, "p95_ms": 5.0, "mean_ms": 3.0}


def test_summarize_latency_rejects_empty():
    with pytest.raises(ValueError):
        multivector.summarize_latency([])
```

- [ ] **Step 2: Run the tests; expect `ModuleNotFoundError: multivector`.**
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q 2>&1 | tail -3
```

- [ ] **Step 3: Create `multivector.py`** with the pure functions, the Qdrant helpers, `build`, `stats` and the dispatcher. (`query` is Task 3; leave its `cmd_query` out and its subparser unregistered until then.)

```python
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
```

- [ ] **Step 4: Run the tests — all 10 pass** (the `collapse_*`, `trec_lines` and `summarize_latency` tests belong to `query`'s logic but the functions live in this step's file, so they go green here):
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q 2>&1 | tail -3   # 10 passed
```

- [ ] **Step 5: Smoke `build` and `stats` against scratch collections on the live Qdrant** (read-only on the real ones; the scratch names are throwaway). Requires the live chunks/object collections to exist with any model, and **no ingest writing them** (a half-written corpus has chunks whose parent is not yet upserted, which `build` correctly refuses) — the smoke checks mechanics, not numbers.
```bash
cd Iverson.Server/Iverson.LoadTest/scripts
python3 multivector.py build --multivector-collection scratch_multivector_smoke
# expect: created … / N points, R rows == R chunk points   (N = objects, R = chunk points of whatever is live)
python3 multivector.py build --multivector-collection scratch_multivector_smoke; echo "exit $?"   # refuses: already exists; exit 1
python3 multivector.py build --multivector-collection scratch_multivector_smoke --drop           # dropped + rebuilt
python3 multivector.py stats --multivector-collection scratch_multivector_smoke --run-dir /tmp/mv-smoke && cat /tmp/mv-smoke/runs/storage.json
curl -s -X DELETE -H "api-key: dev-only-not-for-production-qdrant-key-0123456789" 127.0.0.1:6333/collections/scratch_multivector_smoke   # cleanup
rm -rf /tmp/mv-smoke
```

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/multivector.py Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py
git commit -m "add multivector.py build and stats: regroup chunk points into a MaxSim multivector collection"
```

### Task 3: `multivector.py query` — raw runs for both layouts

**Files:**
- Modify: `Iverson.Server/Iverson.LoadTest/scripts/multivector.py` (add `cmd_query` + subparser)
- Test: `Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py` (already covers `collapse_by_doc`, `trec_lines`, `summarize_latency`; add the two below)

**Interfaces**
- Consumes: Task 2's `scroll`, `require_collection`, `collapse_by_doc`, `trec_lines`, `summarize_latency`, `add_common_args`; `ingest.embed`.
- Produces: `runs/gte-chunks-raw.chunks.trec`, `runs/gte-multivector-raw.chunks.trec`, `runs/raw-latency.json` (Task 5).

- [ ] **Step 1: Add two tests** for the per-query assembly, to `test_multivector.py`:

```python
def test_rank_chunk_hits_collapses_through_the_parent_map():
    hits = [{"id": 1, "score": 0.9, "payload": {"parent_id": "ka"}},
            {"id": 2, "score": 0.8, "payload": {"parent_id": "kb"}},
            {"id": 3, "score": 0.95, "payload": {"parent_id": "ka"}}]
    assert multivector.rank_chunk_hits(hits, {"ka": "A", "kb": "B"}, 50) == [("A", 0.95), ("B", 0.8)]


def test_rank_chunk_hits_fails_loud_on_unknown_parent():
    with pytest.raises(SystemExit):
        multivector.rank_chunk_hits([{"id": 1, "score": 0.9, "payload": {"parent_id": "zz"}}], {}, 50)
```

- [ ] **Step 2: Run; expect `AttributeError: rank_chunk_hits`.**

- [ ] **Step 3: Add `rank_chunk_hits` (after `collapse_by_doc`) and `cmd_query` (before the CLI block), and register the subparser.**

```python
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
```

```python
# ── query ───────────────────────────────────────────────────────────────────────────────

def cmd_query(args):
    require_collection(args.chunks_collection)
    require_collection(args.multivector_collection)
    queries_path = os.path.join(args.run_dir, "beir", "queries.jsonl")
    with open(queries_path, encoding="utf-8") as f:
        queries = [json.loads(line) for line in f if line.strip()]
    if not queries:
        sys.exit(f"no queries in {queries_path}")
    key_to_doc = {p["payload"]["key"]: p["payload"]["docId"]
                  for p in scroll(args.object_collection, False, ["key", "docId"])}

    runs_dir = os.path.join(args.run_dir, "runs")
    os.makedirs(runs_dir, exist_ok=True)
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
            "queries": len(queries),
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

            t0 = time.perf_counter()
            status, resp = ingest.qdrant_request("POST", f"/collections/{args.multivector_collection}/points/query", {
                "query": [vec], "limit": DOCUMENT_BUDGET, "with_payload": ["docId"],
            })
            latency["multivector"].append((time.perf_counter() - t0) * 1000)
            if status != 200:
                sys.exit(f"query {qid}: multivector query HTTP {status} {resp}")
            mv_ranked = [(p["payload"]["docId"], p["score"]) for p in resp["result"]["points"]]

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
```

In `main()`, between the `build` and `stats` parsers:
```python
    q = sub.add_parser("query", help="raw Qdrant runs for both layouts + latency sidecar")
    add_common_args(q)
    q.add_argument("--run-dir", required=True, help="run directory holding beir/queries.jsonl; writes runs/")
    q.add_argument("--model", required=True, help="embedding model id, sent as the request's model field")
    q.add_argument("--embed-url", required=True, help="TEI base URL, e.g. http://localhost:8091")
    q.set_defaults(func=cmd_query)
```
Update the module docstring's `query` usage line if it drifted.

- [ ] **Step 4: Run the tests — 12 passed.**
```bash
python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q 2>&1 | tail -3
```

- [ ] **Step 5: Smoke `query` against the scratch collection.** The box is on the SciFact nomic baseline (Global Constraints), so the query embeds come from Ollama: `ingest.embed` speaks `/v1/embeddings` to Ollama and TEI alike (P5). Run only while no ingest is writing the live collections. A 3-query slice keeps it to seconds:
```bash
cd Iverson.Server/Iverson.LoadTest/scripts
python3 multivector.py build --multivector-collection scratch_multivector_smoke --drop
mkdir -p /tmp/mv-smoke/beir && head -3 /home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26/beir/queries.jsonl > /tmp/mv-smoke/beir/queries.jsonl
python3 multivector.py query --run-dir /tmp/mv-smoke --multivector-collection scratch_multivector_smoke --model nomic-embed-text --embed-url http://localhost:11434
wc -l /tmp/mv-smoke/runs/*.trec          # 150 each (3 queries x 50)
head -2 /tmp/mv-smoke/runs/gte-multivector-raw.chunks.trec   # "<qid> Q0 <docid> 1 0.xxxxxx gte-multivector-raw"
cat /tmp/mv-smoke/runs/raw-latency.json
# Wrong-dimension path: point --embed-url at a model of another dimension if one is up (e.g. TEI bge-small on 8091 vs a 768 collection); expect "HTTP 400 … Vector dimension error" and both .trec files present but empty. Skip if no second model is serving.
curl -s -X DELETE -H "api-key: dev-only-not-for-production-qdrant-key-0123456789" 127.0.0.1:6333/collections/scratch_multivector_smoke; rm -rf /tmp/mv-smoke
```

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/multivector.py Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py
git commit -m "add multivector.py query: interleaved raw per-chunk and MaxSim runs with latency capture"
```

### Task 4: gte ingest and the API control run (`gte-chunks-api`)

**Files:**
- Create (outside the repo): `~/repositories/iverson-benchmark-corpora/scifact-gte-<date>/` (`beir/`, `qrels.trec`, `ingest-started-at.txt`, `ingest.log`, `keymap.json*`, `runs/gte-chunks-api.*`), `scifact-gte-qdrant-snapshots/` (two `.snapshot` files + `RESTORE.md`)

**Interfaces**
- Consumes: Task 1 (`TEI_MAX_BATCH_TOKENS`), the merged migration branch (`ingest.py` flags, `tei-embed`, API `Models__0__*` env), `$SCI` = `scifact-run-2026-08-26`.
- Produces: gte per-chunk collections at the default names, `$MV/runs/gte-chunks-api.{chunks,similar}.trec` + `.meta.json`, the gte snapshots.

Operational and long (≈ 11 h loaded, less idle). Run the ingest in the background and poll its log. Nothing else on the box.

- [ ] **Step 1: Preconditions.**
```bash
cd /home/ben/repositories/Iverson && git log --oneline -1 && grep -c 'TEI_MAX_BATCH_TOKENS' Iverson.Server/docker-compose.yml   # merged main; 1
cd Iverson.Server
docker compose --dry-run up -d --no-deps qdrant ollama postgres redis authentik-server iverson-api 2>&1 | grep -o "Container iverson-[a-z-]* *Recreate" | sort -u   # whatever prints, do NOT run that up
docker stop iverson-worker iverson-starrocks iverson-kafka iverson-zookeeper iverson-jaeger 2>/dev/null
docker ps --format '{{.Names}}' | grep -q '^iverson-worker$' && echo WORKER-RUNNING-STOP        # must print nothing
for c in iverson-qdrant iverson-ollama iverson-postgres iverson-redis iverson-authentik-server iverson-api; do docker inspect -f '{{.Name}} {{.State.Health.Status}}' $c; done   # six healthy
K=dev-only-not-for-production-qdrant-key-0123456789
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*\|"size":[0-9]*'   # 19967, 768
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'                # 5183
curl -s 127.0.0.1:8081/build | grep -o '"composite":"[^"]*"'   # record it: every run this plan produces must carry it
ls /home/ben/repositories/iverson-benchmark-corpora/scifact-512-qdrant-snapshots/*.snapshot | wc -l   # 2 — the nomic baseline restore set exists (spec §6 step 2: skip snapshotting it again)
```
Any other point count or size: restore the nomic baseline first (Task 5 step 6's loop) and stop.

- [ ] **Step 2: Run directory and TEI.**
```bash
export SCI=/home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
export D=$(date +%F)
export MV=/home/ben/repositories/iverson-benchmark-corpora/scifact-gte-$D
mkdir -p $MV/runs && cp -r $SCI/beir $MV/ && cp $SCI/qrels.trec $MV/
cd /home/ben/repositories/Iverson/Iverson.Server
TEI_MAX_BATCH_TOKENS=4096 EMBED_MODEL_ID=Alibaba-NLP/gte-modernbert-base docker compose --profile tei up -d --no-deps --force-recreate tei-embed
until curl -sf http://127.0.0.1:8091/info >/dev/null; do sleep 5; done     # first start downloads ~600 MB into tei_models
curl -s http://127.0.0.1:8091/info | grep -o '"model_id":"[^"]*"\|"max_input_length":[0-9]*\|"auto_truncate":[a-z]*\|"max_batch_tokens":[0-9]*' | tee $MV/tei-info.txt
# Alibaba-NLP/gte-modernbert-base, 4096, true, 4096 — anything else stops the arm
docker stats --no-stream --format '{{.Name}} {{.MemUsage}}' iverson-tei-embed   # ≈ 3.5 GB
```

- [ ] **Step 3: Ingest (background), then verify counts.**
```bash
cd Iverson.LoadTest/scripts
date -u +%FT%TZ > $MV/ingest-started-at.txt
PYTHONUNBUFFERED=1 nohup python3 ingest.py --corpus $MV/beir/corpus.jsonl --key-map-path $MV/keymap.json --drop \
  --model Alibaba-NLP/gte-modernbert-base --embed-url http://localhost:8091 --chunk-max-chars 512 --chunk-step 448 \
  > $MV/ingest.log 2>&1 &
# poll: tail -2 $MV/ingest.log
```
On completion:
```bash
tail -3 $MV/ingest.log                       # "this run: 5,183 documents, 19,967 chunks, …"
cat $MV/keymap.json.stats.json               # documents 5183, chunks 19967, model Alibaba-NLP/gte-modernbert-base, embed_url http://localhost:8091, chunk_max_chars 512, chunk_step 448, elapsed_seconds
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*\|"size":[0-9]*'   # 19967, 768
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'                # 5183
```
Exactly 19,967 or the arm is invalid (Global Constraints).

- [ ] **Step 4: Snapshot the gte per-chunk collections.**
```bash
SNAP=/home/ben/repositories/iverson-benchmark-corpora/scifact-gte-qdrant-snapshots; mkdir -p $SNAP
for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do
  name=$(curl -s -X POST -H "api-key: $K" "127.0.0.1:6333/collections/$c/snapshots?wait=true" | grep -o '"name":"[^"]*"' | cut -d'"' -f4)
  curl -s -H "api-key: $K" -o $SNAP/$name "127.0.0.1:6333/collections/$c/snapshots/$name"
  curl -s -X DELETE -H "api-key: $K" "127.0.0.1:6333/collections/$c/snapshots/$name" >/dev/null
done
ls -la $SNAP    # two .snapshot files
printf '# SciFact gte-modernbert-base (TEI) collections — Qdrant snapshots (%s)\n\n5,183 object / 19,967 chunk points, 768 dims, 512/448/50 window, model Alibaba-NLP/gte-modernbert-base, no prefixes.\nKey map: ../scifact-gte-%s/keymap.json. Restore with the loop in ../scifact-512-qdrant-snapshots/RESTORE.md.\nThe multivector collection snapshot is added by Task 5.\n' "$D" "$D" > $SNAP/RESTORE.md
```

- [ ] **Step 5: Register under gte and run the API control.**
```bash
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE type_name = 'BenchmarkDocument';"   # DELETE 1
cd /home/ben/repositories/Iverson/Iverson.Server
BENCH_EMBED_MODEL=Alibaba-NLP/gte-modernbert-base EMBED_MODEL_ID=Alibaba-NLP/gte-modernbert-base docker compose up -d --no-deps iverson-api
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
docker logs iverson-api 2>&1 | grep "EmbeddingService initialized\|not initialized\|serves model" | tee $MV/api-identity.txt
# exactly: initialized: model=Alibaba-NLP/gte-modernbert-base dimension=768 documentPrefix= queryPrefix=   (both prefixes empty); no warning line
docker inspect iverson-api | grep -o '"Embeddings__ModelId=[^"]*"'    # Alibaba-NLP/gte-modernbert-base
source /home/ben/iverson-benchmark-data/bench-env.sh
cd Iverson.LoadTest
dotnet run -c Release -- benchmark-query --help 2>&1 | grep -q "Schemas registered." && echo SCHEMA-OK
docker exec iverson-postgres psql -U iverson -d iverson -tAc "SELECT type_name, updated_at FROM _iverson_schema WHERE type_name = 'BenchmarkDocument'"   # one fresh row
dotnet run -c Release -- benchmark-query --corpus-path $MV --key-map-path $MV/keymap.json --output-dir $MV/runs --config-label gte-chunks-api 2>&1 | tee $MV/runs/gte-chunks-api.log
wc -l $MV/runs/gte-chunks-api.chunks.trec $MV/runs/gte-chunks-api.similar.trec   # 15000 each
grep -o '"composite":"[^"]*"' $MV/runs/gte-chunks-api.meta.json                 # equals step 1's composite
```

No commit: everything this task produces lives outside the repo.

### Task 5: Multivector arms, scoring, restore, gate document

**Files:**
- Create: `docs/plans/2026-09-GATE-multivector.md`; (outside the repo) `$MV/runs/gte-chunks-raw.chunks.trec`, `$MV/runs/gte-multivector-raw.chunks.trec`, `$MV/runs/raw-latency.json`, `$MV/runs/storage.json`, `$MV/report-*.txt`, the multivector snapshot in `scifact-gte-qdrant-snapshots/`

**Interfaces**
- Consumes: Tasks 2–4; `$SCI/runs/rerank-a0.chunks.trec` (the nomic baseline); `$MV/qrels.trec`.

- [ ] **Step 1: Build, query, stats** (TEI still up on gte; the API may stay up, it is not used).
```bash
cd /home/ben/repositories/Iverson/Iverson.Server/Iverson.LoadTest/scripts
python3 multivector.py build 2>&1 | tee $MV/multivector-build.log      # 5183 points, 19967 rows == 19967 chunk points
python3 multivector.py query --run-dir $MV --model Alibaba-NLP/gte-modernbert-base --embed-url http://localhost:8091 2>&1 | tee $MV/multivector-query.log   # ≈ 5 min (300 embeds)
wc -l $MV/runs/gte-chunks-raw.chunks.trec $MV/runs/gte-multivector-raw.chunks.trec   # 15000 each
cat $MV/runs/raw-latency.json
python3 multivector.py stats --run-dir $MV | tee $MV/multivector-stats.txt
```

- [ ] **Step 2: Snapshot the multivector collection** into the same directory.
```bash
K=dev-only-not-for-production-qdrant-key-0123456789; SNAP=/home/ben/repositories/iverson-benchmark-corpora/scifact-gte-qdrant-snapshots
c=benchmark_documents_multivector_tenant_bypass
name=$(curl -s -X POST -H "api-key: $K" "127.0.0.1:6333/collections/$c/snapshots?wait=true" | grep -o '"name":"[^"]*"' | cut -d'"' -f4)
curl -s -H "api-key: $K" -o $SNAP/$name "127.0.0.1:6333/collections/$c/snapshots/$name"
curl -s -X DELETE -H "api-key: $K" "127.0.0.1:6333/collections/$c/snapshots/$name" >/dev/null
ls -la $SNAP   # three .snapshot files
```

- [ ] **Step 3: Score — three invocations captured to files.**
```bash
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
export SCI=/home/ben/repositories/iverson-benchmark-corpora/scifact-run-2026-08-26
# (a) everything in the run dir, structural checks + throughput, no pairing
python3 report.py --run $MV/runs --qrels $MV/qrels.trec --stats-path $MV/keymap.json.stats.json 2>&1 | tee $MV/report-all.txt
# (b) the gated layout comparison, plus the API run against the same raw control (Holm family of 2)
python3 report.py --run $MV/runs/gte-multivector-raw.chunks.trec --run $MV/runs/gte-chunks-api.chunks.trec \
  --qrels $MV/qrels.trec --baseline $MV/runs/gte-chunks-raw.chunks.trec 2>&1 | tee $MV/report-layout.txt
# (c) the model observation
python3 report.py --run $MV/runs/gte-chunks-api.chunks.trec --qrels $MV/qrels.trec \
  --baseline $SCI/runs/rerank-a0.chunks.trec 2>&1 | tee $MV/report-model.txt
```
Expected in (a): every `.chunks.trec` 15,000 rows, 300 queries, 300/300 covered, no duplicate doc ids; the two raw runs report `build: unknown`; `gte-chunks-api` carries Task 4's composite. Expected in (b): a `FEW QUERIES CHANGED` banner on the layout pair is plausible (shared vectors) — read the permutation p, as the banner says. A `BUILD MISMATCH` line in (c) is expected (rerank-a0 predates the migration image) and benign per the migration's same-build control.

- [ ] **Step 4: Restore the nomic baseline and leave the box on it** (migration Task 7 step 5, verbatim, plus removal of the experiment's collection).
```bash
cd /home/ben/repositories/Iverson/Iverson.Server && docker compose --profile tei stop tei-embed
K=dev-only-not-for-production-qdrant-key-0123456789
curl -s -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_multivector_tenant_bypass; echo   # snapshotted in step 2; the box ends as found
for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do curl -s -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/$c; echo; done
cd /home/ben/repositories/iverson-benchmark-corpora/scifact-512-qdrant-snapshots
for f in *.snapshot; do c="${f%%-6802952876034638*}"; curl -s -X POST -H "api-key: $K" "http://localhost:6333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f"; echo; done
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*\|"size":[0-9]*'   # 19967, 768
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'                # 5183
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE type_name = 'BenchmarkDocument';"
cd /home/ben/repositories/Iverson/Iverson.Server && docker compose up -d --no-deps iverson-api      # defaults: nomic, Ollama
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
docker logs iverson-api 2>&1 | grep "EmbeddingService initialized"   # model=nomic-embed-text dimension=768
source /home/ben/iverson-benchmark-data/bench-env.sh
cd Iverson.LoadTest && dotnet run -c Release -- benchmark-query --help 2>&1 | grep -q "Schemas registered." && echo SCHEMA-OK
```

- [ ] **Step 5: Write `docs/plans/2026-09-GATE-multivector.md`** in the shape of `2026-09-GATE-embedding-migration.md` / `2026-09-GATE-reranker-phase1.md`: header (date, main HEAD, plan reference, where `$MV` and the snapshots live); Method (spec §6 protocol, the 512/448 ruling and the single-chunk probe that forced it, the derived-arm construction, `--baseline` not `--pair` and why); Arms table (`rerank-a0`, `gte-chunks-api`, `gte-chunks-raw`, `gte-multivector-raw`: model, layout, query path, rows, ingest `elapsed_seconds` / `embed_calls` for the gte ingest, query wall-clock); the scores from (a) for all runs incl. `.similar`; the `report.py` compare blocks from (b) and (c) quoted verbatim; the latency and storage table from `raw-latency.json` and `storage.json` (p50/p95/mean per mode, the p95 ratio, points / indexed / segments / disk per collection); a **Gate** section with the three §7 criteria each marked PASS/FAIL with the number that decided it — nDCG@10 CI lower bound, R@50 CI lower bound, p95 ratio ≤ 1.25 — and the one-word verdict **GO / NO-GO**, naming the failing criterion on NO-GO; the model observation stated with its statistics and no threshold; spec §8's adoption implications restated as the follow-up spec's inputs; close with the box state (nomic restored, 5,183 / 19,967, API defaults, TEI stopped).

- [ ] **Step 6: Commit.**
```bash
git add -f docs/plans/2026-09-GATE-multivector.md
git commit -m "record the multivector experiment gate verdict"
```

## Tasks NOT in this plan

Inherited from the spec's Out-of-scope statement (§2):

Any change to `Iverson.Api`, `Iverson.Vector` or the clients; the plan's C# reference program; the 2048/1792 window; NFCorpus and FreshStack; quantization; the migration's `--pair` scoring mode. Adoption, if the gate passes, is a later spec (spec §8).

## Known issues inherited from spec

- **The layout verdict is at 512/448, not the plan's long window.** Chosen deliberately (§2.1). If gte's 8k context is ever the question, it needs a long-document corpus (FreshStack at 2048/1792 is 73 % multi-chunk) and its own control.
- **HNSW approximation is inside the comparison.** Both raw modes use default search parameters, as the API does, so the delta includes any recall difference between per-chunk HNSW with a 250 budget and multivector HNSW at 50. That is the difference adoption would experience.
- **Latency is measured on this box, interleaved, embedding excluded.** Absolute numbers do not transfer; the ratio is the gate.
- **Timing was measured under load** (Task 6 running). The ETA is pessimistic; the per-text ratios are what the plan should trust.
- The whole-body embeds cost ≈ 4 h for a run that is not gated (Ben, 2026-09-04: keep them).
