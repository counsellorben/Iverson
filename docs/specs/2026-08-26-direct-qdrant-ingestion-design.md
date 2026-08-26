# Direct-to-Qdrant Ingestion — Python Scripts, Tiered Stack, and a Two-Corpus Test

## Context

The retrieval-quality harness ingests through the full write path: gRPC `PersistAsync` → Postgres →
Kafka → `IntelligenceStoreConsumer` → chunk → embed → Qdrant. Measured on the live compose stack on
2026-08-26 that costs **~34 s/document**, which puts BEIR SciFact at ~3.5 days. That
corpus is not ingestible on this hardware by that route, so the eight-configuration ablation sweep —
the reason the harness exists — remains unrun.

Most of that cost is embedding, and embedding is unavoidable. But the *rate* is not fixed: per-embed
latency measured 5–14 s while 13 containers competed for two physical cores, against a 1.7–3 s idle
median. The plumbing between the entity and Qdrant contributes little time and a great deal of
required infrastructure.

This design writes to Qdrant directly from Python, which removes Postgres, Kafka, StarRocks and the
C# consumer from the ingest path entirely and lets the long phase run on **two containers**.

## Goal

Three Python scripts that manage a tiered container stack, ingest a BEIR-shaped corpus directly into
Qdrant using the same Ollama model the API queries with, and score the resulting TREC runs — such that
**queries continue to run unchanged through `Iverson.Api`**.

Explicitly **not** goals: changing the C# harness; running the ablation sweep; measuring λ (see
Known issues).

## Design

All scripts live in `Iverson.Server/Iverson.LoadTest/scripts/`, beside the existing
`freshstack_to_jsonl.py`, and follow its conventions.

### 1. `stack.py` — two container tiers

| tier | containers | purpose |
|---|---|---|
| `ingest` | `iverson-qdrant`, `iverson-ollama` | the multi-hour embed-and-upsert phase |
| `query` | ingest + `iverson-postgres`, `iverson-redis`, `iverson-authentik-server`, `iverson-api` | serving `SearchSimilar`/`SearchChunks` |

`stack.py <ingest|query|down>` starts the tier with `docker compose up -d --no-deps <services>`, then
stops any *other* running container whose name starts with `iverson-`.

`--no-deps` is load-bearing: `iverson-api` declares `starrocks`, `starrocks-init`, `kafka`, `jaeger`
and `ollama-init` as `service_healthy` dependencies, and the tier must skip all of them (V1, V2).

**Compose service names differ from container names** — service `qdrant` runs as container
`iverson-qdrant` (V5). The script uses service names for `up` and container names for `stop`.

**The stop set is a name-prefix allowlist**, never "everything not in the tier". Testcontainers
spawned by `Iverson.Api.Tests` in another worktree carry random names and an `org.testcontainers=true`
label; stopping them would kill a concurrent test run and be misdiagnosed as flakiness. The script
prints what it stopped and leaves everything else alone.

**Readiness probes** — Qdrant `GET /readyz`, Ollama `GET /api/tags`, and for the API a **raw TCP
connect to 127.0.0.1:8080**. The API has no HTTP health endpoint: 8080 is h2c/gRPC-only and refuses
HTTP/1.1. Compose's own healthcheck is `exec 3<>/dev/tcp/127.0.0.1/8080`, and this mirrors it (V4).

### 2. `ingest.py` — the Qdrant write contract

Corpus-agnostic: reads any BEIR-shaped `corpus.jsonl`.

**Chunking mirrors `IntelligenceStoreConsumer.SplitIntoChunks` exactly** — 2048-char window
(`maxTokens 512 × 4`), 1792 step (`2048 − 64×4`), extend to a word boundary within 50 chars (V13).
Divergence here makes every number incomparable with the C# pipeline.

**The point contract:**

| | object collection | chunks collection |
|---|---|---|
| name | `benchmark_documents_tenant_bypass` | `benchmark_documents_chunks_tenant_bypass` |
| point id | `KeyToUlong(key)` | `ComputeChunkPointId(parentId, "Body", i)` |
| vectors | `body_vector`, `body_centroid` — 768, Cosine | `body_vector` — 768, Cosine |
| payload | `key`, `docId`, `title`, `body`, `ownerId`, `__TenantId` | `text`, `parent_id`, `field`, `chunk_index`, `ownerId` |

`chunk_index` is a **string**, not an integer (V12).

**Id derivation, both verified against real C#-written points (V7, V8):**

```
KeyToUlong(guid)            = int.from_bytes(uuid.UUID(key).bytes[8:16], "little")
ComputeChunkPointId(p,f,i)  = p ^ (((fnv1a(f)*1000003 + i) & M) * 0x9E3779B97F4A7C15 & M)   M = 2**64-1
```

Python's `&` binds looser than `*`; the mask must be grouped explicitly. Getting this wrong produces
chunk points at unreachable ids and a `SearchChunks` that silently returns nothing.

**Keys are deterministic** — `uuid5(namespace, f"{corpus}:{docId}")`, not random. Point ids therefore
become stable upserts: re-running is idempotent, resume-after-interrupt is "skip what is already
present", and the key map is reproducible. Nothing requires UUIDv7; `KeyToUlong` parses any GUID.

**Centroid** is the mean of **individually L2-normalised** chunk vectors, **not re-normalised
afterwards** — each vector is divided by its own magnitude, summed, then divided by the count (V14).
Zero-magnitude vectors are excluded from the centroid input, as the consumer does; a document whose
every chunk vector is degenerate gets no centroid at all.

**The single-chunk saving.** `SplitIntoChunks` yields `text[start..end].Trim()`, so the one chunk a
short document produces equals the body only when the body is already trimmed. The optimisation is
therefore gated on the identity holding, not on length: reuse one embed for both `body_vector` and the
chunk's vector when `text == text.strip() and len(text) <= 1792`, and embed twice otherwise.

SciFact is measured clean — **0** documents where `text != text.strip()` — so the gate never fires on
it. The condition is stated rather than folded into the length test because it is the identity the
saving depends on: a corpus whose bodies carry surrounding whitespace would get
`body_vector = embed(trimmed)` where the C# consumer writes `embed(untrimmed)`, diverging silently.

**`body_vector` is still required.** `SearchSimilar` searches `<property>_vector` on the object
collection; omitting it breaks that RPC entirely.

**Resumability is required, not optional.** A progress file records completed docIds; `--resume`
skips them. A six-hour run that cannot resume is a six-hour run that restarts from zero.

**`--drop` recreates both collections empty, and deletes the progress file with them.** Destructive,
therefore never the default. The two must move together: a `--drop` that left the progress file behind
would make the next `--resume` skip every document, reporting success over an empty collection and
silently invalidating every downstream metric.

**`ownerId` and `__TenantId` are copied from a live C#-written reference point**, not guessed.

### 3. `sample_corpus.py` and `report.py`

**`sample_corpus.py`** builds a coherent subset the way `corpus-small` was built: N queries, every
document judged relevant to them, plus distractors to a target size — so sampled queries retain
reachable answers. It emits the `<corpus-path>/beir/` layout `benchmark-query` reads
(V23), **and alongside it a qrels restricted to the queries it kept** — which is what `report.py`
scores against. `ir_measures.calc_aggregate` aggregates over the queries in the qrels, so a query
present there but absent from the run contributes zero and drags every aggregate down; scoring a
sampled run against the corpus's full qrels reports a wrong number rather than a missing one.
`corpus-small` already ships its qrels this way — 40 queries, 40 covered, against 300 in the full set.

**SciFact goes through it**, because that layout is the only one `benchmark-query` can read and
`scifact-full/` does not have it — its `corpus.jsonl`, `queries.jsonl` and `qrels/` sit at the top
level, so `LoadQueries` finds nothing and throws. A target size at or above the corpus size means
"re-lay out, sample nothing", which is how this run keeps all 5,183 documents.

**It also selects the query set**, which BEIR forces: `queries.jsonl` carries all 1,109 train, dev and
test queries in one file and `LoadQueries` applies no filter. It emits only the queries carrying
judgments in `qrels/test.tsv` — ~300 — so every run-file row is scoreable. The file matters: `qrels/`
also holds `train.tsv`, whose 809 queries are disjoint from test's 300 and together exhaust
`queries.jsonl`, so filtering on the directory would filter nothing. It reads those qrels in
TREC form: the conversion runs before sampling (below), because BEIR's column 2 is `corpus-id` and
column 3 is `score` where TREC's are the iteration field and the doc id — reading one as the other
takes the score as a doc id and drops every relevant document the sample exists to include.

**The collection must hold this corpus alone.** The collection name is `benchmark_documents_{tenant}`
— entity type and tenant, nothing corpus-specific — so points from any earlier ingest survive into
this one, competing for the top-50 and depressing Recall; and any parent absent from this run's key
map fails `benchmark-query` outright. `--drop` is what guarantees that.

**Procedure:**

1. `sample_corpus.py` — convert the qrels to TREC, then write the corpus directory steps 4 and 6 both
   read. Needs no containers
2. `stack.py ingest` — down to two containers
3. `ingest.py --drop` — recreate both collections empty
4. `ingest.py` — embed, upsert, write the key map
5. `stack.py query` — bring up the query tier
6. `dotnet run -- benchmark-query` — the existing C# command, unchanged
7. `report.py` — score and summarise

**The BEIR qrels converter is the repo's missing piece**, and it runs first. BEIR ships 3-column qrels
(`query-id`/`corpus-id`/`score`) with a header and sometimes CRLF; `ir_measures` needs 4-column TREC
`qid iteration docid rel`. Nothing in the repo does this today. It runs before sampling, so the
sampler and `report.py` both read TREC, and it is applied only to 3-column input — a file already in
4-column TREC passes through unchanged, since its column 2 is meaningful and must survive to disk.

It **must use `ir_measures` measure objects, never `parse_measure`** — the string parser calls the
`ast.Num` removed in Python 3.12 and raises `AttributeError` on this box's 3.14 (V24).

**Reported:**

- *Ingest* — documents, chunks, embed calls, embeds saved by the single-chunk path, wall time,
  docs/hour, seconds/embed
- *Query* — rows, queries, non-zero scores, duplicate-docid count
- *Scoring* — `nDCG@10`, `R@50` and `AP`
- *Headline* — measured seconds/document against the 34 s/document of the full pipeline

## Scope

BEIR SciFact **full** — 5,183 documents, ~300 test-judged queries. One corpus, measured completely.

## Verified assumptions

Verified 2026-08-26 against the running compose stack and the code at `bump-ollama-0.12.11`.

| # | Claim | Evidence |
|---|---|---|
| V1 | `docker compose up -d --no-deps <svc>` starts a service without its `depends_on` set | `iverson-api` cold-started to healthy in ~1 s with starrocks/kafka/zookeeper/jaeger stopped |
| V2 | The API serves searches with StarRocks, Kafka, Zookeeper, Jaeger absent | `benchmark-query` ran 40/40 queries and wrote both run files, exit 0, with all five stopped |
| V3 | **NOT ISOLATED.** postgres + authentik-server + redis required at runtime | They were up throughout; never independently removed. No design impact — the query tier includes them |
| V4 | Readiness probes | Qdrant `/readyz` → 200; Ollama `/api/tags` → 200; API 8080/8081 refuse HTTP/1.1, and compose probes with `exec 3<>/dev/tcp/127.0.0.1/8080` |
| V5 | Service names ≠ container names | `docker compose config --services` → `qdrant`, `ollama`; `docker ps` → `iverson-qdrant`, `iverson-ollama` |
| V6 | `docker stop` preserves state for restart | Containers restarted repeatedly across the session with volumes intact |
| V7 | `KeyToUlong(guid)` = LE uint64 of `bytes[8:16]` | Reproduced **3/3** real object point ids from their `key` payloads |
| V8 | `ComputeChunkPointId` reproduces in Python | Reproduced **4/4** real chunk point ids, after correcting `&`/`*` precedence |
| V9 | Collection names | Live: `benchmark_documents_tenant_bypass`, `benchmark_documents_chunks_tenant_bypass` |
| V10 | Named vectors and distance | Live config: objects `body_centroid`+`body_vector` (768, Cosine); chunks `body_vector` (768, Cosine) |
| V11 | Object payload keys | Live: `['__TenantId','body','docId','key','ownerId','title']` |
| V12 | Chunk payload keys; `chunk_index` is a string | Live: `['chunk_index','field','ownerId','parent_id','text']`, value `'0'` |
| V13 | Chunking is 2048/1792 + word-boundary | `IntelligenceStoreConsumer.SplitIntoChunks` — `maxChars = 512*4`, `step = max(2048-256, 1024)` |
| V14 | Centroid = mean of individually-normalised vectors, not re-normalised | `ComputeCentroid` divides each vector by its magnitude, sums, divides by count |
| V15 | Ollama `/api/embed` contract | `EmbeddingService.cs:59,71` — posts `{model, input}`, reads `embeddings[0]` |
| V16 | Root api-key suffices for writes | Created a probe collection, upserted, read back 1 point, deleted — all `ok` |
| V17 | `SearchChunks.ParentKey` comes from the chunk payload | `ObjectSearchGrpcService.cs:473` — `ParentKey = parentId` from `r.Payload["parent_id"]` |
| V18 | The read path never joins back to Postgres per result | `ObjectSearchGrpcService` builds response `Data` from `r.Payload` alone — Python-written points need no Postgres row |
| V19 | `benchmark-query` needs only queries + key map + a live API | Ran successfully against a Qdrant populated by a prior process |
| V20 | Schema registration persists across restarts | "Schemas registered" succeeded repeatedly after container restarts |
| V21 | `scifact-full` is 5,183 docs, BEIR-shaped | `wc -l` = 5183 corpus / 1109 queries; first row has `_id`/`title`/`text` |
| V22 | The rescued godot corpus is BEIR-shaped with TREC nugget qrels | 25,458 corpus / 99 queries; qrels row `76111264 76111264_0 <doc> 1` |
| V23 | `benchmark-query` reads `<path>/{beir,freshstack}/queries.jsonl` | `BenchmarkQueryScenario.LoadQueries` |
| V24 | **FAILED.** `ir_measures` cannot compute `alpha_nDCG` here | `ValueError: Unsupported measures {alpha_nDCG@10} … pyndeval`; `pyndeval` has no 3.14 wheel and the box has no gcc/make/headers. Separately `parse_measure` raises on the removed `ast.Num` |
| V25 | Nothing else shares these collections | Live collections: `benchmark_documents_*` (this work), `vector_docs_*` (entity-binding), `iverson-probe` |
| V26 | `Body` is the only `[IversonEmbedding]`/`[IversonChunk]` field | `BenchmarkDocument.cs:16-18` — the set has exactly one member |
| V27 | Centroids are fetched by `KeyToUlong(parentKey)` | `ObjectSearchGrpcService.cs:408,445` |
| V28 | `nomic-embed-text` is already present in the ollama volume, so skipping `ollama-init` is safe | `GET /api/tags` → `['nomic-embed-text:latest', 'qwen2.5:3b']`. **Machine state, not code state** — it holds only while `iversonserver_ollama_data` survives; a fresh volume needs `ollama-init` run once before the `ingest` tier is usable |
| V29 | `ir_measures` computes `nDCG@10`, `R@50` and `AP` on this box | `calc_aggregate([nDCG@10, R@50, AP], …)` over real `qrels-small.trec` and `fix2.chunks.trec` → `AP=0.8662`, `R@50=1.0000`, `nDCG@10=0.8948`. Stated separately from V24 because that item records only that `alpha_nDCG` fails — the working measures cannot be inferred from a neighbouring negative, especially with `parse_measure` already broken here |
| V30 | `ir_measures` resolves repeated `(qid, docid)` qrels rows **last-wins, by file order** | Identical run and judgments, row order swapped: `q1 0 dA 1` / `q1 0 dA 0` → `AP=0.0000`; reversed → `AP=1.0000`. This is why BEIR's one-row-per-pair qrels are safe to score against, and why FreshStack's subtopic qrels are not |
| V31 | The api role serves `SearchSimilar`/`SearchChunks` with `iverson-worker` stopped | `Iverson.Api/Program.cs:438` gates gRPC endpoint mapping on `workloadRole == "api"`, and `:443` maps `ObjectSearchGrpcService` inside that block; the worker role's exclusive work is the consumer registration at `:254`. Stated because **V2 does not cover it** — that experiment stopped five other containers with `iverson-worker` running throughout, so the `query` tier's omission of the worker rested on an experiment that never tested it |

## Known issues / accepted as out of scope

**FreshStack was dropped from this project — Ben's decision, 2026-08-26.** Its qrels are
subtopic-scoped: one `(qid, docid)` pair appears once per nugget, and `ir_measures` resolves repeats
last-wins by file order (V30), so a query-level reader silently reads 322 of 585 relevant pairs — 55%,
across 79 of 99 queries — as non-relevant. `nDCG@10`, `AP` and `R@50` are all invalid against those
qrels, and the subtopic-aware measure that would be valid, α-nDCG, cannot be computed here: `pyndeval`
has no Python 3.14 wheel and this box has no gcc, make or Python headers (V24). Re-admitting FreshStack
needs either a query-level qrels collapsed on `rel = max` over nuggets, or a C toolchain
(`sudo apt install build-essential python3.14-dev`, then `pip install pyndeval`). **This project
therefore does not advance the λ measurement**, which remains the harness's original purpose.

**V3 was never isolated.** postgres, authentik-server and redis are assumed required; they were never
removed independently. The query tier includes them, so the risk is a tier one container larger than
necessary, not a broken tier.

**Five containers were left stopped** by the V2 experiment — `iverson-starrocks`, `iverson-kafka`,
`iverson-zookeeper`, `iverson-jaeger`, `iverson-prometheus`. `docker compose up -d` restores them.
