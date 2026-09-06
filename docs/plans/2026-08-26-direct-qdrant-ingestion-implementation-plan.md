# Direct-to-Qdrant Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-26-direct-qdrant-ingestion-design.md` (commit SHA: `550e953`)

**Goal:** Four Python scripts that manage a tiered container stack, write a BEIR-shaped corpus directly into Qdrant using the same Ollama model the API queries with, and score the resulting TREC runs — then one run of the full SciFact corpus through them, measured.

**Architecture:** Ingest bypasses the gRPC → Postgres → Kafka → consumer write path entirely, so the multi-hour embed phase runs on two containers (`qdrant`, `ollama`) instead of thirteen. Python reproduces the C# consumer's point-id derivation, payload shape and chunking exactly, so `SearchSimilar`/`SearchChunks` continue to work through `Iverson.Api` unchanged. Scoring is external, via `ir_measures`.

**Tech stack:** Python 3.14 (stdlib only, plus `ir_measures` for scoring), Qdrant REST on `localhost:6333`, Ollama `/api/embed` on `localhost:11434`, Docker Compose, and the existing `Iverson.LoadTest` `benchmark-query` command.

---

## Global Constraints

Project-wide rules every task must hold to. Values are copied verbatim from the spec; do not re-derive them.

- **Scripts live in `Iverson.Server/Iverson.LoadTest/scripts/`**, beside `freshstack_to_jsonl.py`, and follow its conventions: a module docstring documenting invocation, `argparse`, `sys.exit` with a diagnostic on failure.
- **Stdlib only**, except `ir_measures` in `report.py`. It is reached through `PYTHONPATH`, never a site-packages install — this box is PEP 668 externally-managed with no working `venv`.
- **Chunking mirrors `IntelligenceStoreConsumer.SplitIntoChunks` exactly:** 2048-char window, 1792 step, extend to a word boundary within 50 chars, `.Trim()` each chunk. Divergence makes every number incomparable with the C# pipeline.
- **The point contract is fixed:**

  | | object collection | chunks collection |
  |---|---|---|
  | name | `benchmark_documents_tenant_bypass` | `benchmark_documents_chunks_tenant_bypass` |
  | vectors | `body_vector`, `body_centroid` — 768, Cosine | `body_vector` — 768, Cosine |
  | payload | `key`, `docId`, `title`, `body`, `ownerId`, `__TenantId` | `text`, `parent_id`, `field`, `chunk_index`, `ownerId` |

  `chunk_index` is a **string**. `field` is `"Body"`.
- **Centroid** is the mean of **individually L2-normalised** chunk vectors, **not re-normalised afterwards**; zero-magnitude vectors are excluded from the input.
- **The embed-reuse gate is `text == text.strip() and len(text) <= 1792`** — an identity test, never a length test alone.
- **`report.py` uses `ir_measures` measure objects, never `parse_measure`** — the string parser calls the `ast.Num` removed in Python 3.12 and raises on this box's 3.14.
- **Commit messages:** plain imperative subject. Do not introduce a `plan:`/`feat:` prefix; the repo's usage is inconsistent and this plan adds no convention.

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/scripts/stack.py` — bring up a named container tier, stop out-of-tier `iverson-*`
- `Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py` — qrels conversion, query selection, corpus layout
- `Iverson.Server/Iverson.LoadTest/scripts/ingest.py` — the Qdrant write contract
- `Iverson.Server/Iverson.LoadTest/scripts/report.py` — structural checks and scoring

**Modify** — none. This plan is create-only.

**Test** — none. `scripts/` has no test harness and `freshstack_to_jsonl.py` establishes the convention: dev tooling verified by running against real data. Standing up pytest at this location would invent infrastructure the spec did not ask for. Each task's verification step runs the script against the real corpora instead.

## Inherited from spec

The spec's `Verified assumptions` section (V1–V31) was verified at spec-write time and is **not** re-verified here. Claims are compressed to one line each; the evidence for every one is in the spec, which is the authority. Trusted as ground truth:

- **V1, V2** — `--no-deps` starts a service without its `depends_on`; the API serves searches with StarRocks, Kafka, Zookeeper and Jaeger absent
- **V3** — *not isolated:* postgres/authentik/redis assumed required, never removed independently
- **V4** — Qdrant `/readyz` and Ollama `/api/tags` answer HTTP; the API does not, and compose probes it with a raw TCP connect to 8080
- **V5** — compose *service* names differ from *container* names
- **V6** — `docker stop` preserves state for restart
- **V7, V8** — `KeyToUlong` and `ComputeChunkPointId` reproduce in Python (3/3 and 4/4 against real points)
- **V9, V10, V11, V12** — collection names; named vectors 768/Cosine; object and chunk payload key sets; `chunk_index` is a string
- **V13, V14** — chunking is 2048/1792 + word boundary; centroid is mean-of-normalised, not re-normalised
- **V15** — Ollama `/api/embed` takes `{model, input}`, returns `embeddings[0]`
- **V16** — the root api-key suffices for collection create/upsert/delete
- **V17, V18** — `SearchChunks.ParentKey` comes from the chunk payload's `parent_id`; the read path builds its response from the Qdrant payload alone and never joins back to Postgres
- **V19, V20** — `benchmark-query` needs only queries + key map + a live API; schema registration persists across restarts
- **V21** — `scifact-full` is 5,183 docs, BEIR-shaped
- **V22** — the rescued godot corpus (retained as fact; FreshStack is out of scope)
- **V23** — `benchmark-query` reads `<path>/{beir,freshstack}/queries.jsonl`
- **V24** — *failed:* `alpha_nDCG` is not computable here; `parse_measure` raises on 3.14
- **V25, V26, V27** — nothing else shares these collections; `Body` is the only embedded/chunked field; centroids are fetched by `KeyToUlong(parentKey)`
- **V28** — `nomic-embed-text` is already in the ollama volume, so skipping `ollama-init` is safe (machine state, not code state)
- **V29, V30** — `nDCG@10`/`R@50`/`AP` compute here; `ir_measures` resolves repeated `(qid, docid)` qrels rows last-wins by file order
- **V31** — the api role serves searches with `iverson-worker` stopped

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `scripts/` holds only `freshstack_to_jsonl.py`; all four new names are free | `ls` — one file; each of `stack.py`, `ingest.py`, `sample_corpus.py`, `report.py` absent |
| P2 | File path | `scifact-full/` holds `corpus.jsonl`, `queries.jsonl`, `qrels/test.tsv` | `ls` of both directories; `qrels/` also holds `train.tsv` |
| P3 | File path | `iverson-benchmark-corpora/runs-2026-08-26/` holds the run files Task 4 verifies against, and their scores are the ones Task 4 asserts | `ls` — `baseline.chunks.trec`, `baseline.similar.trec`, `qrels-small.trec`, `keymap-merged.json`. Scored at plan-write time against `qrels-small.trec`: chunks `AP=0.8662 R@50=1.0000 nDCG@10=0.8948`; similar `AP=0.8657 R@50=0.9700 nDCG@10=0.8880` |
| P4 | File path | The compose file is `Iverson.Server/docker-compose.yml` | `ls -l` |
| P5 | Command | Ollama is reachable from the host at `http://localhost:11434` | `GET /api/tags` → 200 |
| P6 | Command | Qdrant is reachable from the host at `http://localhost:6333` with the root `api-key` | `GET /collections` → 200 |
| P7 | Signature | `ir_measures` exposes `read_trec_qrels`, `read_trec_run`, `calc_aggregate`, and objects `nDCG`, `R`, `AP` | All three callable; `nDCG@10`, `R@50`, `AP` construct |
| P8 | Signature | `benchmark-query`'s flags are `--corpus-path`, `--output-dir`, `--key-map-path`, `--config-label` | `Program.cs:399-402` |
| P9 | Signature | `read_trec_run` parses `TrecRunWriter` output unchanged | Parsed 2,000 rows of `baseline.chunks.trec`; first `ScoredDoc(query_id='1', doc_id='169264', score=0.603609)` |
| P10 | Command | `docker compose up -d --no-deps <service>` runs from `Iverson.Server/` | Used from that directory throughout spec verification (V1) |
| P11 | Command | `dotnet run -- benchmark-query …` runs from `Iverson.Server/Iverson.LoadTest/` | The project directory holding `Iverson.LoadTest.csproj` |
| P12 | Command | `bench-env.sh` supplies the env Task 5 needs | Sets `IVERSON_GRPC_URL`, `IVERSON_CLIENT_ID/SECRET`, `IVERSON_TOKEN_ENDPOINT`, `IVERSON_CLIENT_SCOPE` (admin-automation client) |
| P13 | Command | `ir_measures` is reached via `PYTHONPATH`, not site-packages | Imported successfully with `PYTHONPATH=<libs>`; the box is PEP 668 with no working `venv` |
| P14 | Command | Commit convention is a plain imperative subject; no mandatory prefix | `git log --oneline -12` — `fix:`/`spec:` appear but are not universal |
| P15 | Ordering | No task imports a symbol another task creates; Task 5's dependency on 1–4 is runtime-only | Each script is standalone with its own `main()`; nothing is imported across them |
| P16 | Code validity | Python 3.14.4; the four scripts need only stdlib plus `ir_measures` | `python3 -VV` |
| P17 | Code validity | `uuid.uuid5` is deterministic and `UUID(...).bytes[8:16]` little-endian gives the point id | `uuid5(NAMESPACE_URL,'scifact:4983')` stable across calls; `bytes[8:16]` LE → `2381698666080542355` |
| P18 | Code validity | **Qdrant round-trips u64 point ids above 2⁶³ exactly** | 50% of `uuid5` keys are ≥ 2⁶³ (1006/2000 sampled), so this is exercised on every run. Upserted `2**64-1`, `2**63+7` and a real `uuid5` id; all three scrolled back byte-exact |
| P19 | Sibling sweep | Every container name the `query` tier states matches compose | `container_name:` lines — `iverson-postgres`, `iverson-qdrant`, `iverson-ollama`, `iverson-redis`, `iverson-authentik-server`, `iverson-api` |
| P20 | Code validity | The `ownerId` and `__TenantId` values Python must write are `8f5c3da2e5ecbad46e1dab4890c109a4826919be420f5d7a3d0029a9fbff273e` and `tenant_bypass` | Read at plan-review time from a live C#-written object point in `benchmark_documents_tenant_bypass`. **These are the only C#-written points in existence** and Task 5 step 3 destroys them; the values must be captured before that, not read at runtime |
| P21 | Signature | The key map's on-disk shape is a flat JSON object of `{parentKey: docId}` | `KeyMap.cs:20,26,30` — `SerializeAsync`/`DeserializeAsync<Dictionary<string,string>>` with `WriteIndented = true` and no naming policy, so keys are written as-is. A flat object from `json.dump` is byte-compatible |
| P22 | Code validity | SciFact's 5,183 documents carry 5,183 distinct `_id`s, and no two collide under `uuid5`, so Task 5 step 4's point-count checkpoint is exact | Measured over `scifact-full/corpus.jsonl`: 5,183 lines, 5,183 distinct `_id`, 0 duplicates; 0 `uuid5(NAMESPACE_URL, "scifact:{id}")` collisions across all 5,183. Deterministic keys collapse duplicates into one point, so a duplicate would make a correct run fail the checkpoint |
| P23 | Code validity | `qrels/test.tsv` carries exactly 300 distinct queries, all present in `queries.jsonl`, and every docid it judges exists in `corpus.jsonl` | Measured: 300 distinct `query-id`, 0 absent from `queries.jsonl`; 0 relevant `corpus-id` and 0 judged `corpus-id` of any grade absent from `corpus.jsonl`. This is what makes Task 2 step 5's assertions pass on a correct run, and what keeps every sampled query's answer reachable |

*Category 6 (consumer impact) is not required: this plan is create-only — no task has a `Modify:` entry, so no existing caller is touched.*

## Tasks

### Task 1: `stack.py` — container tiers

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/stack.py`

- [ ] **Step 1: Define the two tiers as service/container name pairs.** Compose `up` takes *service* names, `docker stop` takes *container* names, and they differ (V5, P19).

```python
TIERS = {
    "ingest": ["qdrant", "ollama"],
    "query":  ["qdrant", "ollama", "postgres", "redis", "authentik-server", "iverson-api"],
}
CONTAINER = {                      # service -> container_name, from docker-compose.yml
    "qdrant": "iverson-qdrant", "ollama": "iverson-ollama", "postgres": "iverson-postgres",
    "redis": "iverson-redis", "authentik-server": "iverson-authentik-server",
    "iverson-api": "iverson-api",
}
```

- [ ] **Step 2: Start the tier with `--no-deps`.** `subprocess.run(["docker","compose","up","-d","--no-deps",*services], cwd=COMPOSE_DIR)`, where `COMPOSE_DIR` is the directory holding `docker-compose.yml` (P4). `--no-deps` is what lets the query tier skip `starrocks`, `starrocks-init`, `kafka`, `jaeger` and `ollama-init` despite `iverson-api` declaring them `service_healthy` (V1, V2).

- [ ] **Step 3: Stop out-of-tier containers by name prefix.** List running containers, keep those whose name starts with `iverson-` and is not in the tier's container set, stop those. **Never stop anything without the prefix** — Testcontainers from `Iverson.Api.Tests` carry random names, and stopping them would kill a concurrent test run in another worktree. Print both lists: what was stopped, and what was left alone.

- [ ] **Step 4: Wait for readiness.** Qdrant `GET /readyz`, Ollama `GET /api/tags`, and for `iverson-api` a **raw TCP connect** to `127.0.0.1:8080` — it has no HTTP health endpoint, since 8080 is h2c/gRPC-only (V4). Poll with a timeout and exit non-zero on failure, so a caller can chain `stack.py query && dotnet run …` without sleeping.

- [ ] **Step 5: Implement `down`** — stop every running `iverson-*` container.

- [ ] **Step 6: Verify against the live daemon.** Run `stack.py ingest`, confirm exactly `iverson-qdrant` and `iverson-ollama` remain. Run `stack.py query`, confirm the six tier containers are up and `iverson-api` reaches healthy. Confirm any non-`iverson-` container present was reported as left alone.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/stack.py
git commit -m "add stack.py: ingest and query container tiers for direct-Qdrant benchmarking"
```

### Task 2: `sample_corpus.py` — qrels conversion and corpus layout

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py`

- [ ] **Step 1: Convert BEIR qrels to TREC.** Input is 3-column `query-id`/`corpus-id`/`score` with a header row and sometimes CRLF; output is 4-column `qid iteration docid rel` with `iteration = 0`. Strip the header, strip `\r`. **Apply the conversion only to 3-column input** — a file already in 4-column TREC passes through unchanged, since its column 2 is meaningful.

- [ ] **Step 2: Select the query set from `qrels/test.tsv` specifically.** `queries.jsonl` carries all 1,109 train, dev and test queries in one file and `LoadQueries` applies no filter. Keep only queries with a judgment in `qrels/test.tsv` — ~300. **The file matters:** `qrels/` also holds `train.tsv`, whose 809 queries are disjoint from test's 300 and together exhaust `queries.jsonl` (P2), so filtering on the directory would filter nothing.

- [ ] **Step 3: Choose documents.** Every document judged relevant to a kept query, plus distractors up to `--target-size`. A target at or above the corpus size means "re-lay out, sample nothing", which is how the SciFact run keeps all 5,183 documents.

- [ ] **Step 4: Emit the layout `benchmark-query` reads** (V23) plus a filtered qrels:
  - `<out>/beir/corpus.jsonl` — `{"_id","title","text"}`
  - `<out>/beir/queries.jsonl` — `{"_id","text"}`
  - `<out>/qrels.trec` — TREC 4-column, **restricted to the kept queries**. `calc_aggregate` aggregates over the queries in the qrels, so a query present there but absent from the run contributes zero and drags every aggregate down.

- [ ] **Step 5: Verify against the real corpus.** Run against `/home/ben/iverson-benchmark-data/scifact-full` with a target ≥ 5,183. Assert: `corpus.jsonl` is 5,183 lines; `queries.jsonl` is ~300; every qid in `qrels.trec` appears in `queries.jsonl`; every relevant docid in `qrels.trec` appears in `corpus.jsonl`. Print the counts.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/sample_corpus.py
git commit -m "add sample_corpus.py: BEIR qrels conversion, test-query selection, corpus layout"
```

### Task 3: `ingest.py` — the Qdrant write contract

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/ingest.py`

- [ ] **Step 1: Reproduce the point-id derivation.** Both forms are verified against real C#-written points (V7, V8). The mask grouping is explicit because Python's `&` binds looser than `*` — getting it wrong puts every chunk at an unreachable id and `SearchChunks` silently returns nothing.

```python
M = (1 << 64) - 1

def fnv1a(s: str) -> int:
    h = 14695981039346656037
    for b in s.encode(): h = ((h ^ b) * 1099511628211) & M
    return h

def key_to_ulong(key: str) -> int:
    return int.from_bytes(uuid.UUID(key).bytes[8:16], "little")

def chunk_point_id(parent_id: int, field: str, idx: int) -> int:
    h = (fnv1a(field) * 1000003 + idx) & M          # group before multiplying
    return (parent_id ^ ((h * 0x9E3779B97F4A7C15) & M)) & M
```

- [ ] **Step 2: Derive keys deterministically.** `key = str(uuid.uuid5(NAMESPACE, f"{corpus}:{doc_id}"))`. Point ids become stable upserts, so re-running is idempotent, resume is "skip what is present", and the key map is reproducible. Roughly half of these ids exceed 2⁶³; Qdrant round-trips them exactly (P18).

- [ ] **Step 3: Mirror `SplitIntoChunks`.**

```python
def split_into_chunks(text, max_chars=2048, step=1792):
    start, idx = 0, 0
    while start < len(text):
        end = min(start + max_chars, len(text))
        if end < len(text) and not text[end].isspace():
            ws = text.rfind(" ", max(start, end - 50), end)
            if ws > start: end = ws
        yield text[start:end].strip(), idx
        idx += 1
        start += step
```

- [ ] **Step 4: Create or drop collections.** `--drop` deletes and recreates both collections empty **and deletes the progress file and the stats sidecar with them** — a `--drop` that left the progress file behind would make the next `--resume` skip every document and report success over an empty collection, and one that left the sidecar behind would accumulate the next run's counters onto the previous run's, so the headline would span both. Object collection carries `body_vector` and `body_centroid`; chunks carries `body_vector`; all 768, Cosine. **Do not exercise `--drop` against the live `benchmark_documents_*` collections while implementing this step.** They hold the only C#-written points in existence — 450 object, 554 chunk — and step 9's verification compares against them. Test `--drop` against a throwaway collection name instead; it costs nothing and preserves the reference until step 9 has run.

- [ ] **Step 5: Embed, with the reuse gate.** POST `{"model": "nomic-embed-text", "input": text}` to `http://localhost:11434/api/embed`, read `embeddings[0]` (V15, P5). When `text == text.strip() and len(text) <= 1792` the single chunk equals the body, so one embed fills both `body_vector` and that chunk's vector. **Gate on the identity, never on length alone.**

- [ ] **Step 6: Compute the centroid.** Divide each chunk vector by its own magnitude, sum, divide by count — **do not re-normalise** (V14). Exclude zero-magnitude vectors from the input; a document whose every chunk vector is degenerate gets no centroid at all.

- [ ] **Step 7: Build payloads and upsert.** Object: `key`, `docId`, `title`, `body`, `ownerId`, `__TenantId`. Chunk: `text`, `parent_id`, `field="Body"`, `chunk_index` **as a string**, `ownerId`. Take `ownerId` and `__TenantId` from P20 as **module-level constants**, with a comment recording that they were read from a live C#-written point on 2026-08-26. Do **not** read them from Qdrant at runtime: Task 5 step 3 drops the collections, so by the time the measured run executes there is no C#-written point left to read, and every point would carry `null` where the read path filters on ownership and routes by tenant.

- [ ] **Step 8: Write the key map, progress file and stats sidecar.** Key map is a flat `{parentKey: docId}` JSON object — what `KeyMap.LoadAsync` deserialises. Progress file records completed docIds, **appended and flushed as each document's points are upserted** — not accumulated in memory and written at exit, which would provide no resumability for the case resume exists for. `--resume` skips them. Stats sidecar at `<key-map-path>.stats.json` records `documents`, `chunks`, `embed_calls`, `embeds_saved` and wall-clock `started_at`/`finished_at` — Task 4 reads these and none of them is recoverable afterwards from Qdrant or the key map. **The sidecar accumulates across invocations** for a given key-map path: `started_at` is preserved from the first invocation, `finished_at` is updated, and the counters are incremented rather than replaced, so wall time and per-document figures describe the corpus rather than the last segment. A resumed run therefore yields the same headline a single-pass run would.

- [ ] **Step 9: Verify against a live reference point.** Ingest a ~20-document slice, then **scroll one Python-written object point and one chunk point and compare against a C#-written point in the same collection**: point id recomputes from the payload `key`, payload key sets match exactly, vector dimensions are 768, and `chunk_index` is a string. This is the check that catches a contract drift the scores would only show much later.

- [ ] **Step 10: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/ingest.py
git commit -m "add ingest.py: direct Qdrant write path reproducing the consumer's point contract"
```

### Task 4: `report.py` — structural checks and scoring

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/scripts/report.py`

- [ ] **Step 1: Structural checks on each run file.** Rows, distinct queries, count of non-zero scores, and **duplicate doc ids within a query** — malformed TREC that scorers either reject or silently collapse. Report per file.

- [ ] **Step 2: Score with `ir_measures`.** Use measure **objects** — `from ir_measures import nDCG, R, AP` then `calc_aggregate([nDCG@10, R@50, AP], qrels, run)`. **Never `parse_measure`**: it calls the `ast.Num` removed in Python 3.12 and raises on this box (V24). Read qrels with `read_trec_qrels`, runs with `read_trec_run` (P7, P9).

- [ ] **Step 3: Report ingest statistics and the headline.** Documents, chunks, embed calls, embeds saved by the reuse gate, wall time, docs/hour, seconds/embed — read from `<key-map-path>.stats.json`, the sidecar Task 3 step 8 writes. Then the headline: measured seconds/document against the 34 s/document of the full pipeline.

- [ ] **Step 4: Verify against the existing run files.** Run against `iverson-benchmark-corpora/runs-2026-08-26/` with `qrels-small.trec`. Expect `baseline.chunks.trec` → `AP=0.8662`, `R@50=1.0000`, `nDCG@10=0.8948`, and `baseline.similar.trec` → `AP=0.8657`, `R@50=0.9700`, `nDCG@10=0.8880` (P3). Any deviation means the reader is wrong, not the data.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.LoadTest/scripts/report.py
git commit -m "add report.py: TREC run structural checks and ir_measures scoring"
```

### Task 5: Run SciFact end to end

**Interfaces:**
- Consumes: all four scripts from Tasks 1–4, at runtime only.
- Produces: run files, scores and throughput measurements. **No repo commit** — artifacts land in `/home/ben/repositories/iverson-benchmark-corpora/`, which is deliberately outside the repo.

- [ ] **Step 1: Build the corpus.** `sample_corpus.py` against `scifact-full` with a target ≥ 5,183. Confirm 5,183 documents and ~300 queries.

- [ ] **Step 2: Drop to the ingest tier.** `stack.py ingest` — confirm exactly two containers.

- [ ] **Step 3: Recreate the collections empty.** `ingest.py --drop`. The collection currently holds ~450 points from two earlier ingests; this is what removes them. **Irreversible for the reference data:** those points are the only C#-written ones in existence — they came from ingests through the full write path at ~34 s/document and will not be recreated. Anything that needs them (P20's values, Task 3 step 9's comparison) must have been captured before this step.

- [ ] **Step 4: Ingest.** `ingest.py`. Expect roughly 4–6 hours. **Checkpoint before proceeding:** the reported document count equals 5,183, and a Qdrant point count confirms it. If interrupted, resume with `--resume` rather than restarting.

- [ ] **Step 5: Bring up the query tier.** `stack.py query`.

- [ ] **Step 6: Query.** `source /home/ben/iverson-benchmark-data/bench-env.sh` (P12), then from `Iverson.Server/Iverson.LoadTest/` (P11) run `benchmark-query` with `--corpus-path`, `--key-map-path`, `--output-dir`, `--config-label` (P8). It must exit 0; a non-zero exit naming unresolved parent keys means the collection holds points this run cannot name, and the run files must not be scored.

- [ ] **Step 7: Score and report.** `report.py` over the run files. Report `nDCG@10`, `R@50`, `AP` for both RPCs, the structural checks, and measured seconds/document against 34 s/doc.

## Tasks NOT in this plan

Inherited from the spec's stated non-goals. A new spec → plan cycle is required to add any of these:

Changing the C# harness; running the ablation sweep; measuring λ.

## Known issues inherited from spec

Preserved from the spec's "Known issues / accepted as out of scope". These exist by design, accepted during brainstorming:

**FreshStack was dropped from this project — Ben's decision, 2026-08-26.** Its qrels are subtopic-scoped: one `(qid, docid)` pair appears once per nugget, and `ir_measures` resolves repeats last-wins by file order (V30), so a query-level reader silently reads 322 of 585 relevant pairs — 55%, across 79 of 99 queries — as non-relevant. `nDCG@10`, `AP` and `R@50` are all invalid against those qrels, and the subtopic-aware measure that would be valid, α-nDCG, cannot be computed here: `pyndeval` has no Python 3.14 wheel and this box has no gcc, make or Python headers (V24). Re-admitting FreshStack needs either a query-level qrels collapsed on `rel = max` over nuggets, or a C toolchain (`sudo apt install build-essential python3.14-dev`, then `pip install pyndeval`). **This project therefore does not advance the λ measurement**, which remains the harness's original purpose.

**V3 was never isolated.** postgres, authentik-server and redis are assumed required; they were never removed independently. The query tier includes them, so the risk is a tier one container larger than necessary, not a broken tier.

**Five containers were left stopped** by the V2 experiment — `iverson-starrocks`, `iverson-kafka`, `iverson-zookeeper`, `iverson-jaeger`, `iverson-prometheus`. `docker compose up -d` restores them.
