# Retrieval-Quality Benchmark — Public-Corpus Ablation Harness

Date: 2026-07-31
Status: Approved design, not yet planned or implemented

## Context

Parts 3 and 4b of the metadata / tensor-search initiative added two scoring mechanisms whose values
were never calibrated against evidence:

- **Fusion (part 3):** the returned score is a weighted mean of a Qdrant cosine (0.60), a document
  centroid cosine (0.30) and a recency decay (0.10). The weights were chosen, not measured.
- **Diversification (part 4b):** greedy MMR at λ = 0.70. Its own spec says plainly that topical
  collapse "has not been observed in a real corpus, and this is anticipation rather than a measured
  failure", and that λ "is a compile-time constant precisely so it can be revised centrally once
  there is evidence to revise it against."

There is no such evidence today, and there cannot be from Iverson's own corpora: measuring retrieval
quality needs relevance judgments, which a private corpus does not have. Public IR benchmarks do have
them. This spec builds the harness that runs Iverson against those benchmarks so the two open
questions become measurable.

**Base branch:** `main`, at `d3c8b3c`.

## Goal

Produce TREC-format run files from Iverson's two vector RPCs against public benchmark corpora, across
a sweep of fusion-weight and λ configurations, so that standard IR tooling can answer two questions:
does the centroid signal improve retrieval, and does MMR diversification help.

## Design

### 1. The two ablations, and why each is a single-constant edit

Both axes have exact "off" settings reachable by editing one compile-time constant. This is not an
approximation in either case.

- **Centroid off — `WCentroid = 0.00`.** `ResultReranker` accumulates `weightTotal += WCentroid`
  only inside `if (hasCentroid)`, so a zero weight adds nothing to either the numerator or the
  denominator and `fused` becomes exactly `base` (A14).
- **MMR off — `Lambda = 1.00`.** Then `mmr(c) = 1·fused − 0·maxSim = fused`, and with ties breaking
  toward the earlier candidate the selected set is bit-for-bit `Take(topK)` — the same degradation
  guarantee part 4b already proves and tests (A15).

The sweep is `WCentroid ∈ {0.30, 0.00}` × `Lambda ∈ {1.00, 0.70, 0.50, 0.30}`.

**No configuration seam is added.** Parts 3 and 4b both state "no caller-supplied weights, no
per-request opt-out, no kill switch, no configuration knob", and that rule stands. Each configuration
is a source edit plus a server rebuild. The alternative — widening the constants to `internal` for a
test-only seam — was considered and rejected: it puts a mutable knob into the exact components the
specs said must not have one, in a production assembly, for measurement, and seams outlive
experiments.

**One ingest serves the entire sweep.** The constants appear only in `ResultReranker` and
`ResultDiversifier`, both query-time components; nothing on the ingest path references them (A16).
Chunking, embedding and centroid computation therefore happen once, and each configuration is a
rebuild plus a re-query. This is what makes an eight-configuration sweep affordable at all.

### 2. Corpora

Two corpora, because the two questions need different judgment structures — and this is the single
most important constraint in the design:

- **Fusion → BEIR (SciFact + NFCorpus).** BEIR supplies `(query, document, relevance)` judgments,
  which is exactly what nDCG@10 and Recall@k need. ~9K documents and ~620 test queries combined,
  the small end of BEIR.
- **Diversification → FreshStack (two topics).** α-nDCG scores a result set by how much of a query's
  *facets* it covers, so it needs subtopic- or nugget-level judgments. **BEIR does not have these**,
  and no amount of nDCG on BEIR will answer the λ question. FreshStack ships 1–7 GPT-4o-generated
  nuggets per question and reports α-nDCG@10, Coverage@20 and Recall@50 — the exact metric family
  MMR exists to move. Two topics give ~50K documents and ~150 queries (A1).

### 3. Corpus → Iverson mapping

A new `BenchmarkDocument` entity in `Iverson.LoadTest`:

- `[IversonKey] Guid Id` — **server-assigned**, not the corpus id. Iverson generates a UUIDv7 when
  the supplied key is empty, and non-GUID keys are documented as unreachable (A5).
- `DocId` (string) — the corpus document id, carried as an ordinary property.
- `Title` (string) — the corpus title.
- `Body` — annotated **both** `[IversonEmbedding]` and `[IversonChunk]`. The dual annotation is what
  makes `centroidPossible` true and the centroid signal live; an embedding-only property would make
  the fusion a mathematical identity and the ablation meaningless (A4).
- `[IversonTenant]` on a tenant property, per the declarative marker (A7).

**Authorization must be declared, or every query returns nothing.** `SchemaRegistrar.RegisterAllAsync`
attaches authorization only to types present in its dictionary, and a type registered without rules is
denied on read — both vector RPCs return an empty stream rather than an error, so the failure looks
like "retrieval found nothing" (A21). `BenchmarkDocument` therefore needs an entry in
`authorizationByTypeName` granting `CanReadAll` to `iverson-loadtest-bypass`, and the scenario queries
as the already-provisioned `iverson-loadtest-bypass-user`. Schema registration validates `OwnerField`
against the type's declared scalars regardless of role (`SchemaRegistrationOrchestrator.cs:82-84`, via
`ValidateFieldReference`) (A21a), so `BenchmarkDocument` requires an `OwnerId` property — which the
shipped code has. It must also carry a `tenant_id` claim,
which the load test's existing tenant provisioning supplies.

Ingestion goes through `EntityCoordinator`, **not** `DirectSeeder`. `DirectSeeder` writes straight to
Postgres/StarRocks/Kafka for bulk speed and would bypass the chunk/embed/centroid pipeline the
benchmark exists to measure.

### 4. Query execution and run files

Both RPCs are exercised per query. They are cheap once ingestion is done, and they resolve document
identity differently:

- **`SearchSimilar`** returns the deserialized entity, so `DocId` comes back directly — no mapping.
- **`SearchChunks`** returns only `ParentKey` and `Score`, so the harness keeps an ingest-time
  `ParentKey → DocId` map and translates on the way out. Passages are aggregated to documents by
  **max chunk score per parent** (standard max-passage), which is also the RAG-realistic path.

Output is standard TREC run format — `qid Q0 docid rank score runtag` — with the run tag encoding the
configuration. There is no upper clamp on `top_k` (A13), and the 4× over-fetch is bounded accordingly.

**The two RPCs need different budgets, because `top_k` counts different units (A22).** For
`SearchSimilar` it counts entities, so `topK = 50` serves Recall@50 directly. For `SearchChunks` it
counts *chunks*, and several chunks of one document each consume budget before max-passage
aggregation collapses them — so a 50-chunk request yields well under 50 distinct documents. Request a
fixed multiple of 50 chunks there, then truncate to exactly the top 50 documents after aggregation.
The multiplier is chosen once and held constant across all eight configurations; varying it would
compare run files built from different candidate-pool sizes.

### 5. Scoring is external

The harness computes no metrics. It writes run files; scoring targets `ir_measures` — which supports
`alpha_nDCG`, reading subtopic ids from the qrels iteration field per TREC convention (A3) — fed by a
converter-derived TREC qrels file, rather than FreshStack's own evaluation package. This is a recorded
decision, not a silent default: FreshStack's package takes three objects (`qrels_nuggets`,
`qrels_query`, `query_to_nuggets`) that the harness does not produce, so building a TREC qrels file for
`ir_measures` is the route taken instead. Hand-rolling α-nDCG was rejected: the α redundancy discount
and the ideal-ranking denominator are easy to get subtly wrong, and a wrong metric invalidates every
conclusion silently.

### 6. Harness location

`Iverson.LoadTest` gains the entity, a scenario, and a `Program.cs` command. It already has the client
wiring, Authentik acting-user auth, tenant and schema setup, and a reporting shape; entity
registration is by assembly scan, so a new type in the same assembly is picked up with no server
change (A17, A18). A separate project would duplicate all of that for one scenario.

### 7. Running the sweep

Because the tests hand-compute expected values at the current constants, **every ablation build has a
failing test suite by construction** (A19). The sweep therefore runs on a scratch branch: edit the
constant, build, deploy, run the harness, keep the run file, move to the next configuration, and
discard the branch at the end. Check the first configuration's run file is non-empty before spending
the remaining seven cycles. Do not run the suite against an ablation build expecting green, and do
not commit an edited constant to `main`.

## Testing

Two parts of the harness can be silently wrong in ways that would invalidate every result, and they
get unit tests:

- the corpus parsers (BEIR `corpus.jsonl` / `queries.jsonl` / `qrels` TSV, and FreshStack's format);
- the max-passage aggregation — that several chunks of one parent collapse to a single document entry
  carrying the maximum chunk score, and that ordering follows the aggregated score.

The rest of the harness is I/O against a live stack and is not usefully unit-testable.

## Verified assumptions

Verified against the codebase at `main@d3c8b3c` before this spec was written.

| # | Assumption | Evidence |
|---|---|---|
| A1 | FreshStack is publicly available with nugget-level judgments, but its smallest topic (godot) has ~25K documents and 99 questions — not the ~50 questions originally assumed | Measured from the published HuggingFace dataset: angular 117,288 docs / 129 queries, laravel 52,351 docs / 184 queries, langchain 49,514 docs / 203 queries, yolo 27,207 docs / 57 queries, godot 25,482 docs / 99 queries; reports α@10, C@20, R@50. Nuggets are 1–7 per question, not a flat 3–4. FreshStack ships no qrels file — judgments are nested inside each query's row, not a separate TREC-style qrels file. The corpus carries no `title` field |
| A2 | BEIR SciFact and NFCorpus are small enough for the fusion half | ~5.2K and ~3.6K documents, ~300 and ~323 test queries — the small end of BEIR's 18 datasets, which are now a subset of MTEB. **Stated from published dataset documentation, not fetched and counted in this session**; confirm the exact figures when downloading, since the scale decision rests on them |
| A3 | `ir_measures` computes α-nDCG from TREC-format inputs | `ir_measures` supports `alpha_nDCG`, taking subtopic ids from the qrels *iteration* field per TREC convention; `trec_eval`/`ndeval` are the underlying references |
| A4 | Dual `[IversonEmbedding]` + `[IversonChunk]` annotation is what makes the centroid signal live | `ObjectSearchGrpcService.cs:204` — `centroidPossible = schema.ChunkFields.Any(c => …PropertyName == vectorDesc.PropertyName)` |
| A5 | **Shape-changing.** The corpus id cannot be the entity key | `ObjectMappingGrpcService.cs:127-132` assigns `Guid.CreateVersion7()` when the supplied key is empty; `IntelligenceStoreConsumer.cs:576` states "Non-GUID keys are unreachable today (keys are server-generated UUIDv7)". The sample entity uses `[IversonKey] Guid Id`. A string key *might* work — `SchemaRegistrar.BuildKeyDescriptor` goes through `DetectType` rather than hardcoding Guid — but the design does not depend on it |
| A6 | Registering a new entity type needs no server-side code change | Registration is client-driven by assembly scan (A17's evidence); authorization rules are a client-side per-type dictionary |
| A7 | The entity can declare its tenant field declaratively | `Iverson.Client.Attributes/IversonTenantAttribute.cs:9` |
| A8 | Ingestion must use the client write path, not `DirectSeeder` | `Seeding/DirectSeeder.cs` opens Npgsql, MySqlConnector and a Kafka producer directly and targets 400K articles — a bulk path that bypasses the API |
| A9 | `Iverson.LoadTest` can register a schema and write as an authorized acting user | `Program.cs:118-126` wires `AddIversonClient` with a tenant-admin token provider; `Auth/ActingUserTokenProvider.cs` and `Auth/AuthentikFlowExecutorClient.cs` exist |
| A10 | **NOT VERIFIED — still open, now quantified.** Whether ~59K documents can be ingested on the laptop deployment in tolerable time | Cannot be established by reading code; it depends on CPU Ollama embedding throughput under real chunking. The 2026-08-24 live run ingested 400 documents and drove a 4-core box to a load average of ~20, hard enough that Kafka's `FIND_COORDINATOR` lookup began timing out — reproduced at the broker, not a client artifact (`8db2bf4`, `docs/runbooks/integration-test-flake-signatures.md`). The smallest viable FreshStack topic, godot, is 25,482 documents / 99 queries — roughly 64× that volume. Listed here rather than silently omitted, and carried as the first entry under "Known issues" |
| A11 | `SearchSimilarAsync` returns the deserialized entity plus score | `EntityCoordinator.cs:204-220` — yields `new SearchResult<T>(entity, response.Score)` |
| A12 | `SearchChunksAsync` returns `ParentKey` and `Score` | `EntityCoordinator.cs:222-234` yields the raw `ChunkSearchResponse`, whose fields are `parent_key`, `chunk_text`, `score`, `trace_id` |
| A13 | `top_k` has no upper clamp | `ObjectSearchGrpcService.cs:198` and `:347` — `(ulong)Math.Max(1, (int)request.TopK)`, lower bound only |
| A14 | `WCentroid = 0.00` makes `fused` exactly `base` | `ResultReranker.cs:35-42` — `weightedSum += WCentroid * sim` and `weightTotal += WCentroid` both inside `if (hasCentroid)`, so a zero weight contributes to neither |
| A15 | `Lambda = 1.00` makes selection identical to `Take(topK)` | `ResultDiversifier.cs:76-77` — `Lambda * Score - (1 - Lambda) * maxSim` collapses to `Score`; part 4b's `Diversify_AllVectorsAbsent_…` test already asserts the fused-order equivalence this reduces to |
| A16 | The constants are query-time only, so one ingest serves the whole sweep | `grep` for `WBase\|WCentroid\|WDecay\|Lambda` across non-test server sources returns hits only in `ResultReranker.cs` and `ResultDiversifier.cs`; nothing in `IntelligenceStoreConsumer` references them |
| A17 | `Iverson.LoadTest` can host the entity, scenario and command | `Program.cs:122` registers entities via `entityAssemblies: [typeof(BenchmarkArticle).Assembly]`; `Program.cs:168-190` is a `switch` over commands; `Scenarios/`, `Reporting/` and `Auth/` already exist |
| A18 | Adding an entity type breaks no existing scenario | Entity discovery is assembly-scan; authorization is a per-type dictionary keyed by name (`Program.cs:147-151`), so a new entry is additive |
| A19 | **Operational.** Every ablation build has a failing test suite | `ResultRerankerTests.cs:28-29` asserts `(0.6*0.9 + 0.3*0.5 + 0.1*0.8)/1.0 = 0.77`, with more at `:44-46` and `:62`; `ResultDiversifierTests.cs` hand-computes at λ = 0.70. Editing a constant falsifies them by construction |
| A20 | *(Recurrence)* Every configuration in the sweep is a pure constant edit — no member of the matrix needs a code-shape change | Members enumerated: `WCentroid ∈ {0.30, 0.00}` and `Lambda ∈ {1.00, 0.70, 0.50, 0.30}`. Both symbols are `private const double` (`ResultReranker.cs:12`, `ResultDiversifier.cs:12`) read at a single expression site each, and A16's sweep confirms no other code path branches on their values |
| A21 | A type registered without authorization rules is denied on read, and both vector RPCs return an empty stream rather than an error | `SchemaRegistrar.cs:26-30` attaches `Authorization` only for dictionary-present types; `RowFieldAuthorizationEvaluator.cs:11-12` returns `Denied` when rules are null; `ObjectSearchGrpcService.cs:126-127` and `:298-299` — `if (decision.Denied) return;` |
| A21a | *(Scope)* A21 governs authorization **evaluation** only — it says nothing about schema **registration**, which validates independently of any role's authorization state | `SchemaRegistrationOrchestrator.cs:82-84` — `if (!string.IsNullOrEmpty(ownerField)) ValidateFieldReference(descriptor, ownerField, "owner_field");` — calls `ValidateFieldReference` on `owner_field` whenever `OwnerField` is declared, regardless of role, before any authorization rule is consulted. Conflating the two — reading A21's bypass-role behavior as also excusing registration-time validation — is what produced the false §3 claim that `BenchmarkDocument` needs no `OwnerId` |
| A22 | `top_k` counts entities on `SearchSimilar` and chunks on `SearchChunks`; the chunk path does not dedup by parent | `ObjectSearchGrpcService.cs:437` bounds `Diversify` over chunk points and `:442-450` writes one response per chunk carrying `ParentKey`, with no dedup; `:267` bounds the same call over entity points |

## Known issues / accepted as out of scope

**Laptop ingest feasibility is unverified.** The combined corpora are ~59K documents, which chunk into
substantially more embedding calls, all through CPU Ollama. Whether this completes in a tolerable time
on the laptop deployment cannot be established without running it — and a previous kind run on this
machine hit a local `pids.max=307` ceiling. This is the largest open risk in the design. If ingestion
proves intractable, the fallback is BEIR alone, which is ~9K documents and answers the fusion question
without the diversity half.

The 2026-08-24 live run is evidence, not resolution: ingesting 400 documents drove a 4-core box to a
load average of ~20, hard enough that Kafka's `FIND_COORDINATOR` lookup began timing out — reproduced
at the broker, not a client artifact (`8db2bf4`, `docs/runbooks/integration-test-flake-signatures.md`).
The smallest viable FreshStack topic, godot, is 25,482 documents with 99 queries — roughly 64× the
volume that produced that load, and one topic alone matches the ~100-question statistical power the
prior spec assumed from two.

**~150 queries is modest statistical power.** Two FreshStack topics give roughly 150 questions — more
than the ~100 originally estimated, but still modest. That is enough to detect a large diversification
effect and not enough to resolve a subtle one, so a null result on the λ sweep should be read as "no
large effect detected", never as "λ = 0.70 is optimal". Ben chose two topics over one for exactly this
reason; more topics would cost proportionally more ingest.

**α-nDCG depends on expressing FreshStack's nuggets in the scoring tool's expected shape.** `ir_measures`
reads subtopic ids from the qrels iteration field. FreshStack's own evaluation package would have been
the lower-risk route, but its API takes three objects (`qrels_nuggets`, `qrels_query`,
`query_to_nuggets`) the harness does not produce; §5 records `ir_measures` against a converter-derived
TREC qrels file as the route taken instead. This was not verified end-to-end.

**Ablation builds are knowingly red.** See A19 and §7. Accepted as the cost of not adding a
configuration seam.

## Not in this spec

- **Automating the sweep.** A shell loop over configurations that edits, builds, deploys and runs is
  fine; building a sweep runner is not part of this.
- **CI integration.** This is a calibration exercise, not a regression gate. If a run file ever becomes
  a baseline worth defending, that is a separate decision.
- **Latency measurement.** `Iverson.LoadTest`'s existing scenarios already own that, with HdrHistogram.
- **Acting on the results.** Changing `WCentroid` or λ on the strength of what this measures is a
  separate spec, and should be — the numbers come first.
- **Any change to the scoring components themselves.** `ResultReranker` and `ResultDiversifier` are
  read-only here apart from the throwaway constant edits on a scratch branch.
