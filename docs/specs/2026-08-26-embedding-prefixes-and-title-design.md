# Embedding Task Prefixes and Title Composition

**Supersedes `2026-08-26-ingest-contract-generation-design.md`**, which was never planned. That
design's content is carried forward here in full; prefixes and title composition change it rather
than merely adding to it, so one document replaces two.

## Context

The first full-corpus scored run of the retrieval harness (2026-08-26, standard BEIR SciFact test
split: 5,183 documents, 300 queries, 339 judgments) produced `SearchChunks` nDCG@10 **0.6820** and
`SearchSimilar` **0.6638**. Two defects in how documents are embedded account for a plausible share
of the gap to published numbers, and neither is a harness artefact — both affect every vector
Iverson writes.

**Iverson never sends nomic-embed-text's task prefixes.** The Nomic technical report
(arXiv 2402.01613) states the requirement directly: "we use the `search_query` and `search_document`
prefixes for the query and document respectively." No such string exists anywhere in this repo
(A14). The model's own published SciFact score is **0.705**, measured with the prefixes.

**The title is never embedded.** `BenchmarkDocument.Title` is stored in the Qdrant payload and
indexed as a keyword, but only `Body` carries `[IversonEmbedding]`/`[IversonChunk]`, and both
writers set body from the corpus `text` field alone. In SciFact every document is a paper title
plus abstract, and published SciFact numbers embed `title + " " + text`. The most information-dense
field in the corpus contributes nothing to retrieval.

Both are ingest-side, so both are paid for by one re-ingest.

## Goal

Prefixed, title-bearing vectors written identically by the C# and Python paths, with the C#-owned
constants generated into a contract that `ingest.py` reads rather than repeats — then one re-measured
BEIR SciFact run reported against the 0.6820 / 0.6638 baseline.

Explicitly **not** goals: the ablation sweep; measuring λ; changing any RPC signature or client;
re-embedding existing tenant collections; a model swap.

## Base

`main` at or after `4558491`, where `direct-qdrant-ingestion` merged. (The superseded spec said
`ingest.py` "exists only on the unmerged `direct-qdrant-ingestion` branch" — that is now stale.)

## Design

### 1. The C# prefix contract

`Iverson.Embeddings` gains two public constants:

```csharp
public const string DocumentPrefix = "search_document: ";
public const string QueryPrefix    = "search_query: ";
```

`IEmbeddingService` replaces `EmbedAsync` with two intent-named methods:

```csharp
Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default);
Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);
```

**`EmbedAsync` is removed from the interface, not kept alongside.** That is the point of the shape:
every call site and every test stub fails to compile until consciously visited. Leaving a neutral
method in place would let a stub silently attach to it and keep passing while covering nothing —
the failure mode this repo has already recorded (see Testing).

`EmbeddingService` keeps `EmbedAsync` as a **private** method that both public methods call after
prepending. `EnsureInitializedAsync`'s dimension probe calls it directly and **unprefixed**: the
dimension is prefix-independent, and probing with a prefix would only mislead a reader into thinking
the probe is representative.

The four production call sites map unambiguously (A3, A14):

| site | becomes |
|---|---|
| `IntelligenceStoreConsumer.cs:140` — object vector | `EmbedDocumentAsync` |
| `IntelligenceStoreConsumer.cs:244` — chunk vector | `EmbedDocumentAsync` |
| `ObjectSearchGrpcService.cs:197` — `SearchSimilar` | `EmbedQueryAsync` |
| `ObjectSearchGrpcService.cs:368` — `SearchChunks` | `EmbedQueryAsync` |

**Ordering rule: the task prefix is outermost.** When `PrefixWithContextAsync` composes contextual
chunk text, the embedded string is `search_document: {context}\n{chunk}`. nomic's prefix is only
meaningful at position zero, so it must be applied *after* context composition. `EmbedDocumentAsync`
receiving already-composed text and prepending gives this for free; the rule is stated so nobody
later "optimises" by prefixing the chunk first. This path is **not** exercised by the benchmark
(A6) but is live for any entity that enables contextual chunking.

`NoOpEmbeddingService` (`StartupNoOpFakes.cs:17`) gains the two methods returning `new float[4]`,
matching what it does today. It and `EmbeddingService` are the only implementors (A1).

**Both halves must ship together.** Prefixed documents queried without `search_query: ` place the
query vector in a different region than the corpus vectors, which is plausibly *worse* than
prefixing neither. They are one change here, so this is not a sequencing hazard — it is recorded so
no future reader treats either half as independently shippable.

### 2. The generated ingest contract

The superseded design's mechanism carries forward unchanged: `Iverson.Api.Tests` owns a test that
both writes `ingest-contract.json` (under `IVERSON_REGENERATE_INGEST_CONTRACT=1`) and, by default,
asserts a fresh emit equals the committed copy, failing with a diff. Generator and drift gate are
the same artefact, which is what keeps "generated" from decaying into "stale".

**One new contract field**, under a new `embedding` object:

```json
"embedding": { "documentPrefix": "search_document: " }
```

This belongs in the contract by the superseded design's own §2 rule — C# owns it, so it is emitted;
`modelId` and `dimension` stay out because configuration and a startup probe own those. The emit
reads the constant directly rather than by reflection: it is `public const` in `Iverson.Embeddings`,
which `Iverson.Api.Tests` already references (A5).

**`queryPrefix` is deliberately not emitted.** Nothing Python-side would read it — queries are
embedded inside `Iverson.Api`, which is C# and is itself the constant's source (A21). A contract
field with no consumer is dead surface.

The source-of-truth table gains one row: *document prefix ← `Iverson.Embeddings` constant*.

**One new golden case**, alongside the five chunking cases: the composed document string for a plain
chunk. The context-composed form is not goldened, because the benchmark entity does not enable
contextual chunking (A6).

**A limitation stated rather than buried:** the contract pins C#/Python *agreement*, not
model-*appropriateness*. Change `Embeddings__ModelId` to a model with different conventions and both
sides will still agree — on the wrong prefix. The contract cannot detect that, because the model
side lives in configuration.

### 3. `ingest.py`

Carried forward from the superseded design: delete the module-level constants (`MAX_CHARS`, `STEP`,
the collection names, the payload-index lists) and the hardcoded `768`/`"Cosine"`, loading them from
the contract instead; keep the Python implementations of `split_into_chunks`, `compute_centroid`,
`key_to_ulong` and `chunk_point_id`, since the contract pins their behaviour rather than their code;
add `--model` and probe Ollama for the dimension; run `verify_contract()` immediately after
`parse_args()` and **before `--drop` acts** (A12) — dropping against a drifted contract is as
damaging as ingesting against one.

**The prefix is applied in one place: inside `embed()` (`ingest.py:307`), the single function
wrapping `/api/embed` (A7).** This matters for the reuse gate, which decides "this document's single
chunk equals its trimmed body, so embed once" by comparing **raw** text —
`reuse = body == body.strip() and len(body) <= STEP` (`ingest.py:369`). Applying the prefix at the
embed boundary leaves that comparison untouched and the gate valid (A8); prefixing earlier would
force the gate to reason about prefixed strings for no benefit.

`verify_contract()`'s golden replay grows the prefix-composition case.

### 4. Title composition

**The title is concatenated into the embedded text at corpus-build time, in `sample_corpus.py`.**

```
text = f"{title}\n\n{text}"   when a title is present; unchanged when it is not
```

`sample_corpus.py:225` is the sole writer of `corpus.jsonl` (A9), and both writers read `text` from
it — the C# path via `JsonlCorpusParser` into `Body = corpusDoc.Text` (A10), the Python path
directly. Composing upstream therefore reaches both writers with **no change to either**, keeping
them byte-identical. Composing at write time instead would put the same rule in two languages,
creating a fresh instance of exactly the divergence the ingest contract exists to eliminate.

The separate `title` field stays in `corpus.jsonl` and in the Qdrant payload for display and
filtering (A20).

**Rejected: adding `[IversonEmbedding]` to `Title`.** `SearchSimilarRequest` carries
`string property` — a search targets exactly one embedded property, and there is no cross-vector
fusion anywhere in the search path. A `title_vector` would never be consulted by a benchmark
querying `Body`; it would cost a schema change, a new contract field and a re-ingest, and deliver
nothing. Multi-property search is a real platform gap, and a separate project.

### 5. The measurement

One full re-ingest of the standard split, then `benchmark-query` over the same 300 queries and
`report.py` against the same `qrels.trec`. The existing run is a clean control: same corpus, same
queries, same judgments, same code but for this spec's two changes. Baseline figures are recorded
in `scifact-run-2026-08-26/report.txt` (A13).

| | baseline | this spec |
|---|---|---|
| `SearchChunks` nDCG@10 / R@50 / AP | 0.6820 / 0.9160 / 0.6377 | to be measured |
| `SearchSimilar` nDCG@10 / R@50 / AP | 0.6638 / 0.8695 / 0.6212 | to be measured |

**Expected ingest cost, corrected for the title (A18):** title concatenation raises mean body length
1,401 → 1,500 chars and pushes documents over the 1,792-char reuse threshold from 1,003 to 1,363,
so ~360 documents lose the reuse saving and chunk count rises ~3%. Scaling the measured baseline
(6,219 chunks / 7,222 embed calls): approximately **6,400 chunks and ~7,750 embed calls**, about
+7%, so **~5.1 hours** at the measured 3.315 s/document.

**The headline number is a combined delta and is reported as such.** Prefixes and title are both
ingest-side, so attributing them separately costs a second and third full ingest — roughly 10 extra
hours to explain a result that, if it lands well, needs no explanation. Attribution is spent only
if the combined result disappoints.

The re-ingest drops the existing collection. That is irreversible and is confirmed at execution
time, not assumed.

### 6. Testing

**Unit, C#.** `EmbeddingService` composes both prefixes correctly, and the dimension probe stays
unprefixed. The four production call sites use the correct method — asserted directly, because a
wrong choice here raises no error and produces only a worse number.

**The repointed `EmbedAsync` references** — 75 across 6 files — get a branch-coverage diff against a
padded base, not merely a green suite. The guarded failure is specific and has happened here: a
re-pointed test keeps passing while silently losing the branch it existed to cover.

**Contract drift gate.** The existing emit-and-compare test covers `documentPrefix` and the new
golden case; `verify_contract()` replays it in `ingest.py` before `--drop` acts.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | Exactly two implementors of `IEmbeddingService` | `EmbeddingService.cs:12`; `StartupNoOpFakes.cs:17` (`NoOpEmbeddingService`) — all other test usages are `Substitute.For<>` |
| A2 | Removing `EmbedAsync` from the interface breaks every caller at compile time | All four call sites resolve through the injected interface; no caller holds a concrete `EmbeddingService` |
| A3 | The four production call sites are the only places text is embedded | `IntelligenceStoreConsumer.cs:140,244`; `ObjectSearchGrpcService.cs:197,368` |
| A4 | The probe can use a private raw embed; dimension is prefix-independent | `EmbeddingService.cs:37` calls its own method; dimension is the model's output width |
| A5 | `Iverson.Embeddings` constants reachable from `Iverson.Api.Tests` without reflection | `Iverson.Api.Tests.csproj:35` — `ProjectReference` to `Iverson.Embeddings` |
| A6 | `BenchmarkDocument` does not use contextual chunking | No `Contextual` on the entity; `IntelligenceStoreConsumer.cs:233` also gates on `enrichmentOptions.Value.Enabled` |
| A7 | `ingest.py` has one function wrapping `/api/embed` | `ingest.py:307` — `def embed(text)` |
| A8 | The reuse gate compares raw text before embedding | `ingest.py:369` — `reuse = body == body.strip() and len(body) <= STEP` |
| A9 | `sample_corpus.py` is the sole writer of `corpus.jsonl` | `sample_corpus.py:225` |
| A10 | The C# path maps corpus `text`→`Body`, `title`→`Title` | `JsonlCorpusParser.cs:23-24,38`; `BenchmarkIngestScenario.cs:203-204` |
| A11 | Queries and qrels are unaffected by title composition | Title appears only in `corpus.jsonl`; `queries.jsonl` is `{_id, text}` (`sample_corpus.py:237`) |
| A12 | `--drop` has a drop-only path that exits before ingesting | `ingest.py:55-61` documents it; the script exits immediately after dropping |
| A13 | Baseline figures are recorded retrievably | `scifact-run-2026-08-26/report.txt`, 38 lines |
| A14 | Nothing else consumes `EmbedAsync`; no prefix string exists in the repo | Full non-test grep returns only the interface declaration, the probe, a log message, and the four call sites |
| A15 | The superseded design's V1–V19 still hold post-merge | Spot-checked: V1 `Iverson.Api.csproj:10-13` (`InternalsVisibleTo`); V4 `Iverson.LoadTest.csproj:10-11` (only `Client.Core`, `Events`) |
| A16 | `Iverson.Api.Tests` does not yet reference `Iverson.LoadTest`; adding it creates no cycle | `Iverson.Api.Tests.csproj:30-35`; `Iverson.LoadTest` never references `Iverson.Api` |
| A17 | Prefix + a full chunk stays well inside the model's context | 17 prefix chars ≈ 3 tokens; 2,048 chars ≈ 386 tokens; ~390 of a 2,048-token context |
| A18 | **Changed.** Title composition raises ingest cost ~7% | Measured over the real corpus: mean body 1,401→1,500 chars; docs over the 1,792 reuse threshold 1,003→1,363; ~6,400 chunks / ~7,750 embed calls; ~5.1 h |
| A19 | `report.py` needs no change | It reports absolute figures; the baseline comparison is spec-level |
| A20 | The `title` payload field stays populated | Sourced from the separate `title` field, which composition leaves in place |
| A21 | No proto or client change | No RPC signature changes; the query prefix never leaves C# |

## Known issues, accepted

**Pre-existing collections hold prefix-less, title-less vectors.** Ben's decision, 2026-08-26: this
spec re-ingests the benchmark collection only. Any other collection written before this change holds
vectors that are now stale in exactly the way a model change makes them stale, and must be
re-embedded to be comparable. No migration tooling is built, because this is a dev stack with no
production data to protect.

**The measured delta is not attributable between the two changes.** See §5.

**`corpus.jsonl` is no longer a verbatim BEIR corpus file** — its `text` carries the title too. This
is deliberate benchmark preparation and is stated in the file's header, not left to be discovered by
diffing against upstream.

**Centroid numerics differ between the pipelines, independently of this work.** Carried forward from
the superseded design: `ComputeCentroid` sums into `float[]` with `MathF.Sqrt`
(`IntelligenceStoreConsumer.cs:470-484`) while `ingest.py` computes in float64, so the two produce
centroids differing at roughly 1e-7. The golden centroid check states a tolerance rather than
asserting exact equality. Far below anything that reorders a result set. Accepted by Ben.

**Locating the contract file from a test is a new pattern.** Carried forward: the test walks up from
`AppContext.BaseDirectory` to the `Iverson.slnx` marker. No precedent exists in this repo.

**The contract pins settings and the goldened algorithms — not all behaviour.** Carried forward: a
divergence in a Python code path with no golden case remains undetectable.
