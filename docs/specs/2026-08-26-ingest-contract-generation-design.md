# Generated Ingest Contract — One Source of Truth for the Python Write Path

## Context

`2026-08-26-direct-qdrant-ingestion-design.md` moved benchmark ingest off the full write path and
into `ingest.py`, which writes to Qdrant directly. That bought the two-container ingest tier the
retrieval harness needs, at a stated cost: `ingest.py` **reimplements** the C# write contract.

> "Chunking mirrors `IntelligenceStoreConsumer.SplitIntoChunks` exactly — 2048-char window, 1792
> step, extend to a word boundary within 50 chars (V13). Divergence here makes every number
> incomparable with the C# pipeline."

Two implementations, held in sync by hand. The hazard is not theoretical: commit `4771286`
("fix chunker word-boundary off-by-one and recreate payload indexes on collection create") is a
divergence that already occurred and had to be found by inspection.

This design removes the *constant* half of that duplication by generating it from C#, and pins the
*algorithmic* half with golden cases the C# implementation produces.

## Goal

A committed `ingest-contract.json`, generated from the real C# code paths by a test that also fails
when the committed copy drifts, and consumed by `ingest.py` at startup.

Explicitly **not** goals: rewriting `ingest.py` in C#; extracting a shared runtime service; changing
the C# ingest behaviour; running the ablation sweep.

## Base

This work applies to `ingest.py`, which exists only on the unmerged `direct-qdrant-ingestion` branch
(`main`'s `scripts/` holds just `freshstack_to_jsonl.py`). It must be based on that branch or land
after it merges.

## Design

### 1. The generator is a test, not a CLI

`Iverson.Api.Tests` owns the emit. One test class both writes `ingest-contract.json` (under
`IVERSON_REGENERATE_INGEST_CONTRACT=1`) and, by default, asserts a fresh emit equals the committed
copy — failing with a diff. Generator and drift gate are the same artefact, which is what keeps
"generated" from decaying into "stale".

This location is forced by access, and it is the cheapest one available:

- `Iverson.Api` grants `InternalsVisibleTo` to `Iverson.Api.Tests` (V1).
- `Iverson.Api.Tests` already references Api, Vector and Embeddings (V2).
- Reaching these five helpers by reflection is an **established convention** in this very project,
  not an invention: `IntelligenceStoreConsumerTests.cs:756-830` already binds `ComputeChunkPointId`
  and `KeyToUlong` with `BindingFlags.NonPublic | BindingFlags.Static` (V3).
- Building a descriptor without containers is likewise established: `SchemaBuilderTests.cs:28`
  calls `SchemaBuilder.BuildDescriptor(typeDesc, embedding)` with a stub (V10).

The rejected alternative was an emit command in `Iverson.LoadTest`. That project references only
`Iverson.Client.Core` and `Iverson.Events` (V4); reaching `SchemaBuilder` would force a
`Iverson.LoadTest → Iverson.Api` reference, dragging the ASP.NET host into the load-test tool.
`Iverson.LoadTest` never needs to know `Iverson.Api` exists.

### 2. Source of truth per field

| contract field | sourced from | verified |
|---|---|---|
| collection-naming rule | `IntelligenceTenantScope.ResolveCollectionName` | V5 |
| object vectors (`_vector`, `_centroid`), payload indexes | `SchemaBuilder.ToCollectionSchema` | V6 |
| chunk vectors, chunk payload indexes | `SchemaBuilder.ToChunkCollectionSchema` | V6 |
| chunk window (`maxChars`, `step`, `wordBoundaryLookback`) | the real derivation in `SplitIntoChunks` | V7 |
| distance metric | new `Iverson.Vector` constant (see §4) | V8 |
| golden chunk boundaries, point ids, centroid | reflective calls to the real helpers | V3 |

**Neither `modelId` nor `dimension` is generated.** `SchemaBuilder` takes both from
`IEmbeddingService` (`SchemaBuilder.cs:62,73-74,163-164`), and `EmbeddingService` resolves them from
configuration and a startup probe of Ollama — the live values come from `Embeddings__ModelId` in
compose, not from C# (V9). Emitting them would mean a test hardcoding `768` and
`"nomic-embed-text"`, which is exactly the drift being removed. They are properties the *model*
owns, so the model stays their source of truth: see §5.

### 3. The file

`Iverson.Server/Iverson.LoadTest/scripts/ingest-contract.json`, committed beside the scripts that
read it, with a header naming the owning test and the regeneration command.

```json
{
  "chunkWindow":      { "maxChars": 2048, "step": 1792, "wordBoundaryLookback": 50 },
  "collectionNaming": { "template": "{base}{suffix}_{tenant}",
                        "chunksSuffix": "_chunks",
                        "noTenantSentinel": "__no-tenant-claim__" },
  "distance":         "Cosine",
  "objectCollection": { "vectorNames": ["body_vector", "body_centroid"],
                        "payloadIndexes": [ { "field": "__TenantId", "kind": "Keyword" },
                                            { "field": "body",       "kind": "Keyword" },
                                            { "field": "docId",      "kind": "Keyword" },
                                            { "field": "ownerId",    "kind": "Keyword" },
                                            { "field": "title",      "kind": "Keyword" } ] },
  "chunksCollection": { "vectorNames": ["body_vector"],
                        "payloadIndexes": [ { "field": "parent_id", "kind": "Keyword" },
                                            { "field": "field",     "kind": "Keyword" },
                                            { "field": "ownerId",   "kind": "Keyword" } ] },
  "payloadKeys":      { "chunk":  ["text", "parent_id", "field", "chunk_index"],
                        "chunkIndexIsString": true },
  "golden":           { "chunking": [ /* the five cases of §6 */ ],
                        "pointIds": [ /* one GUID-key case */ ],
                        "centroid": { "inputs": [ /* fixed 4-dim vectors */ ], "output": [ /* ... */ ],
                                      "tolerance": 1e-6 } }
}
```

Object payload-index *kinds* come from `SchemaBuilder.SqlTypeToPayloadKind`, not from a list written
here; the five fields above are what that mapping yields for `BenchmarkDocument` today.

Naming is emitted as the **rule**, not as a baked-in `benchmark_documents_tenant_bypass`, so the
contract states something about `ResolveCollectionName` rather than about one benchmark's tenant.

The `__TenantId` index name is emitted verbatim because `ToCamelCase` leaves a leading underscore
unchanged — Python already matches, but by coincidence rather than by contract (V16).

The payload keys are load-bearing for **readers**, not only the writer: the query path reads
`parent_id`, `text` and `{prop}_centroid` (`ObjectSearchGrpcService.cs:406-468`, V11). A contract
that described only what the consumer writes would miss half the coupling.

### 4. Production changes — two, both small

**`ChunkWindow` extraction.** `maxChars = maxTokens * 4`, `step = Math.Max(maxChars - overlapChars,
maxChars / 2)` and the bare `50` are inline in `SplitIntoChunks` and unreachable from a test. Lift
them into `internal static (int MaxChars, int Step, int Lookback) ChunkWindow(int maxTokens, int
overlap)`, which `SplitIntoChunks` then calls. Safe: `SplitIntoChunks` has **exactly one call site**
(`IntelligenceStoreConsumer.cs:229`) and no test binds it by name (V7).

**Distance constant.** `Distance.Cosine` is a bare literal duplicated at
`IntelligenceCollectionManager.cs:35` and `:230` (V8). Introduce one constant in `Iverson.Vector`,
point both call sites at it, and emit it. This removes an existing duplication rather than adding
one, and makes the contract complete instead of complete-except-one-field.

Nothing else in `IntelligenceStoreConsumer` changes. Its behaviour is unchanged in both cases.

### 5. `ingest.py` changes

`ingest.py` is the only script that needs touching: the other four encode no C#-owned constant at
all (V15).

Delete the module-level constants — `MAX_CHARS`, `STEP`, `DEFAULT_OBJECT_COLLECTION`,
`DEFAULT_CHUNKS_COLLECTION`, `OBJECT_PAYLOAD_INDEXES`, `CHUNKS_PAYLOAD_INDEXES` — and the hardcoded
`768` / `"Cosine"` in collection creation (`ingest.py:114-154, 504-509`, V12). Load them from the
contract instead.

`split_into_chunks`, `compute_centroid`, `key_to_ulong` and `chunk_point_id` **keep their Python
implementations**. The contract pins their behaviour, not their code.

**Model and dimension.** Add a `--model` argument (default `nomic-embed-text`), documented as needing
to match the API's `Embeddings__ModelId`, and **probe Ollama for the dimension** the way
`EmbeddingService.EnsureInitializedAsync` does. Today `ingest.py` hardcodes `768` and does not probe
(V12), so this is new behaviour. It makes the Python and query paths agree by construction rather
than by two literals happening to match.

**`verify_contract()` runs automatically at the start of every invocation**, immediately after
`parse_args()` and *before* `--drop` acts (V13) — dropping collections against a drifted contract is
as damaging as ingesting against one. It replays the golden cases and exits non-zero on mismatch,
printing expected and actual. A three-hour run must not begin on a drifted contract.

### 6. Golden cases

Chosen to cover the failure that already happened, not to enumerate the space:

| case | why |
|---|---|
| text shorter than the window | single-chunk path; also the `text.Trim()` equality that makes dedup valid |
| text exactly at the window boundary | off-by-one at `end == text.Length` |
| multi-chunk text with overlap | `step` applied repeatedly |
| word-boundary extension **fires** | the `LastIndexOf(' ', end, …)` branch |
| word-boundary extension **must not fire** (no space in the last 50 chars) | precisely the class of `4771286` |

Plus point ids for a GUID key, and a centroid over fixed 4-dimensional synthetic vectors — the
formula is dimension-agnostic, so small vectors exercise it as well as 768 and keep the check
Ollama-free.

**No non-GUID point-id case.** `KeyToUlong`'s FNV branch is documented as *"unreachable today (keys
are server-generated UUIDv7)"* (`IntelligenceStoreConsumer.cs:684`, V14). Goldening it would pin dead code.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| V1 | `Iverson.Api` grants internals to `Iverson.Api.Tests` | `Iverson.Api.csproj:10-12` — `InternalsVisibleTo` → `Iverson.Api.Tests` |
| V2 | `Iverson.Api.Tests` references Api, Vector, Embeddings | `Iverson.Api.Tests.csproj:30-35` |
| V3 | Reflection on these private statics is established | `IntelligenceStoreConsumerTests.cs:756-830` binds `ComputeChunkPointId`, `KeyToUlong` via `BindingFlags.NonPublic \| BindingFlags.Static` |
| V4 | `Iverson.LoadTest` does **not** reference `Iverson.Api` | `Iverson.LoadTest.csproj:10-11` — only `Iverson.Client.Core`, `Iverson.Events` |
| V5 | Naming rule is `{base}{suffix}_{tenant}`, `_chunks`, sentinel `__no-tenant-claim__` | `IntelligenceTenantScope.cs:9-16` |
| V6 | `ToCollectionSchema` / `ToChunkCollectionSchema` are `internal static` and carry vectors + payload indexes | `SchemaBuilder.cs:315-340`; chunk indexes are `parent_id`, `field`, owner-field camelCased |
| V7 | `SplitIntoChunks` has exactly one caller and no test binds it by name | `IntelligenceStoreConsumer.cs:229` is the only call; `SchemaBuilder.cs:154` is a comment |
| V8 | Distance is **not** in `CollectionSchema`; `Distance.Cosine` is a literal duplicated twice | `CollectionSchema.cs` is `(CollectionName, Vectors, PayloadIndexes)`; `IntelligenceCollectionManager.cs:35,230` |
| V9 | `modelId` **and** `dimension` both come from `IEmbeddingService`, resolved from config + a startup probe | `SchemaBuilder.cs:62,73-74,163-164`; `EmbeddingService.EnsureInitializedAsync`; compose sets `Embeddings__ModelId` |
| V10 | `SchemaBuilder.BuildDescriptor` runs container-free with a stub | `SchemaBuilderTests.cs:28,51,75,127,148`; `Helpers/SchemaFixtures.cs` |
| V11 | The query path reads `parent_id`, `text`, `{prop}_centroid` | `ObjectSearchGrpcService.cs:406,415,444,467-468` |
| V12 | `ingest.py` hardcodes the constants **and** `768`; it does not probe | `ingest.py:114-154` (constants), `:504,509` (`"size": 768, "distance": "Cosine"`), `:309` (model) |
| V13 | `main()` has a startup point after `parse_args()` and before `--drop` acts | `ingest.py:456-500` |
| V14 | `KeyToUlong`'s FNV branch is documented unreachable | `IntelligenceStoreConsumer.cs:684-687` |
| V15 | **Recurrence:** only `ingest.py` encodes C#-owned constants | `stack.py`, `report.py`, `sample_corpus.py`, `freshstack_to_jsonl.py` match none of `768\|nomic\|_vector\|_centroid\|_chunks\|2048\|1792\|parent_id\|chunk_index\|benchmark_documents\|Cosine` |
| V16 | Python's `"__TenantId"` index name matches C# | `ToCamelCase` is `char.ToLowerInvariant(name[0]) + name[1..]`; a leading `_` round-trips unchanged |
| V17 | No repo-relative-path convention exists in `Iverson.Api.Tests` | No match for `AppContext.BaseDirectory`, `SolutionDir`, or parent-walking — this is a **new** pattern |

## Known issues, accepted

**Centroid numerics differ between the pipelines, independently of this work.** `ComputeCentroid`
sums into `float[]` using `MathF.Sqrt` (`IntelligenceStoreConsumer.cs:470-484`); `ingest.py` computes
in float64. The two therefore produce centroids differing at roughly 1e-7 **today**. The golden
centroid check states a tolerance rather than asserting exact equality, and the divergence is
documented rather than fixed: it is far below anything that reorders a result set, and forcing either
side to change numeric type is a larger change than the problem warrants. Accepted by Ben.

**Locating the contract file from a test is a new pattern** (V17). The test walks up from
`AppContext.BaseDirectory` to the `Iverson.slnx` marker. There is no precedent in this repo to
follow, so the mechanism is stated here rather than inherited.

**The contract pins settings and the goldened algorithms — not all behaviour.** A divergence in a
Python code path with no golden case remains undetectable. The five chunking cases, the point ids and
the centroid are chosen to cover the observed failure mode; they are not a proof of equivalence.
