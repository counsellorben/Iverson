# Retrieval-Quality Benchmark Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-31-retrieval-quality-benchmark-design.md` (commit SHA: `ac0f27f`)

**Goal:** Produce TREC-format run files from Iverson's two vector RPCs against BEIR and FreshStack, across the eight-configuration ablation sweep, so external IR tooling can answer whether the centroid signal improves retrieval and whether MMR diversification helps.

**Architecture:** `Iverson.LoadTest` gains a `BenchmarkDocument` entity, corpus parsers, an ingest scenario and a query scenario, plus two `Program.cs` commands. Ingest runs once through the client write path and persists a `ParentKey → DocId` map; each of the eight configurations is a server rebuild followed by a query run that reads that map and writes run files. A new `Iverson.LoadTest.Tests` project holds unit tests for the two components that can be silently wrong.

**Tech stack:** .NET 10, xunit 2.9.3 + FluentAssertions 7.0.0 (existing test convention), `Iverson.Client.Core` / `Iverson.Client.Search` via project reference, Grpc.Core `Metadata` for acting-user propagation.

---

## Global Constraints

Copied verbatim from the spec; every task must hold to these.

- **No configuration seam is added.** "no caller-supplied weights, no per-request opt-out, no kill switch, no configuration knob". Each configuration is a source edit plus a server rebuild. Do not widen `WCentroid` or `Lambda` to `internal`, and do not add any accessor, env var, or request field that reaches them.
- **The harness computes no metrics.** It writes run files only. Scoring is external (`ir_measures` / FreshStack's own package). Do not implement α-nDCG, nDCG, or Recall.
- **One ingest serves the entire sweep.** Nothing in the query path may require re-ingestion.
- **The chunk-budget multiplier is chosen once and held constant across all eight configurations.** Varying it would compare run files built from different candidate-pool sizes.
- **Do not commit an edited `WCentroid` or `Lambda` to `main`.** Ablation builds live on a throwaway scratch branch.

## File Structure

**Create**
- `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkDocument.cs` — the corpus entity
- `Iverson.Server/Iverson.LoadTest/Corpus/CorpusModels.cs` — `CorpusDocument`, `CorpusQuery`, `Qrel` records
- `Iverson.Server/Iverson.LoadTest/Corpus/BeirCorpusParser.cs` — BEIR `corpus.jsonl` / `queries.jsonl` / qrels TSV
- `Iverson.Server/Iverson.LoadTest/Corpus/FreshStackCorpusParser.cs` — FreshStack corpus, questions, nugget qrels
- `Iverson.Server/Iverson.LoadTest/Benchmark/MaxPassageAggregator.cs` — chunk rows → document rows
- `Iverson.Server/Iverson.LoadTest/Benchmark/TrecRunWriter.cs` — `qid Q0 docid rank score runtag`
- `Iverson.Server/Iverson.LoadTest/Benchmark/KeyMap.cs` — `ParentKey → DocId`, JSON load/save
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs` — corpus → `EntityCoordinator`, writes the key map
- `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` — both RPCs → run files
- `Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj` — new test project
- `Iverson.Server/Iverson.LoadTest.Tests/Corpus/BeirCorpusParserTests.cs`
- `Iverson.Server/Iverson.LoadTest.Tests/Corpus/FreshStackCorpusParserTests.cs`
- `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/MaxPassageAggregatorTests.cs`

**Modify**
- `Iverson.Server/Iverson.LoadTest/Program.cs` — auth-dictionary entry, DI registrations, two switch cases
- `Iverson.Server/Iverson.Server.slnx` — one `<Project>` line for the test project

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and by two `critical-design-review` rounds. **Not re-verified here**; trusted as ground truth. A1–A22 in the spec, of which the load-bearing ones for this plan are:

- **A4** Dual `[IversonEmbedding]` + `[IversonChunk]` on one property is what makes the centroid signal live (`ObjectSearchGrpcService.cs:204`).
- **A5** The corpus id cannot be the entity key; keys are server-assigned UUIDv7.
- **A11 / A12** `SearchSimilar` returns entity data + score; `SearchChunks` returns `ParentKey` + `Score`.
- **A13** `top_k` has no upper clamp.
- **A14 / A15** `WCentroid = 0.00` makes `fused` exactly `base`; `Lambda = 1.00` makes selection identical to `Take(topK)`.
- **A16** The constants are query-time only, so one ingest serves the whole sweep.
- **A17 / A18** `Iverson.LoadTest` can host the entity, scenario and command; adding an entity type breaks no existing scenario.
- **A19** Every ablation build has a failing test suite by construction.
- **A21** A type registered without authorization rules is denied on read, and both vector RPCs return an empty stream rather than an error.
- **A22** `top_k` counts entities on `SearchSimilar` and chunks on `SearchChunks`; the chunk path does not dedup by parent.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `ac0f27f`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `Corpus/`, `Benchmark/` and `Iverson.LoadTest.Tests/` do not exist yet | `ls` of all three returned "No such file or directory" |
| P2 | File path | `Entities/`, `Scenarios/`, `Reporting/` exist; no `BenchmarkDocument.cs` yet | `ls Entities/` → `BenchmarkArticle.cs`, `BenchmarkAuthor.cs`, `BenchmarkTag.cs`; `ls Entities/BenchmarkDocument.cs` → not found |
| P3 | File path | The solution is `Iverson.Server/Iverson.Server.slnx`, listing 14 projects, none of them `Iverson.LoadTest.Tests` | Read of the file |
| P4 | Signature | `PersistAsync(T, Metadata?, CancellationToken)` returns `Task<string?>` carrying the server-assigned key | `EntityCoordinator.cs:98-111` — `return response.Key`; `object_persistence.proto:21` — "the assigned or existing entity key" |
| P5 | **Signature — shaped this plan** | `SearchSimilarAsync` and `SearchChunksAsync` take `(builder, CancellationToken)` only — **no `Metadata`**, so they cannot carry an acting user | `EntityCoordinator.cs:204-206` and `:222-224`; both call `search.SearchSimilar(request, cancellationToken: ct)` with no metadata |
| P6 | Signature | The acting-user pattern is `new Metadata().WithActingUser(await identity.GetTokenAsync(ct))`, passed to a raw gRPC client call | `ReadPathScenario.cs:230,235` — `search.Search(req, headers)`; `WritePathRunner.cs:75` |
| P7 | Signature | `Query.Similar<T>(expr)` and `Query.Chunks<T>(expr)` are public factories; the builder constructors are `internal` | `Query.cs:32` and `:38`; `QuerySimilarBuilder.cs:21` is `internal` |
| P8 | Signature | `.Text(string)`, `.TopK(uint)` and `.Build(string? traceId)` are public on both builders | `QuerySimilarBuilder.cs:27,28,50`; `QueryChunksBuilder.cs:29,30,49` |
| P9 | Signature | `SearchResponse.Data` is a `Struct` of **string** values keyed by **camelCase** property names — not a deserialized entity | `ObjectSearchGrpcService.cs:271-273` — `protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value)` over the Qdrant payload; `IntelligenceStoreConsumer.cs:384` — `pointPayload[col.Name.ToCamelCase()] = val` |
| P10 | Signature | `StructConverter` is not usable from `Iverson.LoadTest` | `Iverson.Client.Core.csproj` — `<InternalsVisibleTo Include="Iverson.Client.Core.Tests" />` only |
| P11 | Signature | `ChunkSearchResponse` carries `ParentKey` and `Score`; `parent_id` is the entity key | `ObjectSearchGrpcService.cs:442-450`; `IntelligenceStoreConsumer.cs:253` — `["parent_id"] = ev.Key` |
| P12 | **Signature — shaped this plan** | `BenchmarkReport` is the HdrHistogram **latency** reporter, which the spec puts out of scope | `Reporting/BenchmarkReport.cs:7-10` — `LongHistogram`, `Record(long microseconds)` |
| P13 | Code validity | `Iverson.LoadTest` reaches the builders transitively | `Iverson.LoadTest.csproj` references `Iverson.Client.Core`; `Iverson.Client.Core.csproj` references `Iverson.Client.Search` |
| P14 | Code validity | The test-project convention is xunit 2.9.3, FluentAssertions 7.0.0, `Microsoft.NET.Test.Sdk` 17.12.0, `net10.0`, `<IsTestProject>true</IsTestProject>` | `Iverson.Vector.Tests/Iverson.Vector.Tests.csproj` |
| P15 | Code validity | An entity is a `sealed class` marked `[IversonEntity]`, with `[IversonKey] Guid Id` and `[IversonTenant]` on a string property | `Entities/BenchmarkArticle.cs:5` (`[IversonEntity]`), `:8` (`[IversonKey] Guid Id`), `:16` (`[IversonTenant] public string TenantId`) |
| P16 | Command | Scenarios are resolved as `services.GetRequiredService<X>().RunAsync(flags)` from a `switch (command)` | `Program.cs:168-190` |
| P17 | Command | Commit convention is plain imperative for additions (`add get_schema to the python client`), `type(scope):` for fixes | `git log --oneline -12` |
| P18 | Ordering | T1 is independent of T2–T4; T3 needs T2's entity; T4 needs T3's key map and T1's models | T1 touches only `Corpus/` and the test project; `BenchmarkQueryScenario` reads the map file T3 writes |
| P19 | Consumer impact | Adding a `switch` case and an auth-dictionary entry is additive | `Program.cs:147-152` is a dictionary literal; `:168-190` is a `switch` over string commands — new entries touch no existing branch |
| P20 | Consumer impact | Adding a project line to `Iverson.Server.slnx` does not disturb the existing 14 | The file is a flat `<Solution>` list of `<Project Path=…/>` elements with no ordering or grouping semantics |
| P21 | **Not grounded in repo evidence** | A test project may reference an `OutputType=Exe` project | No existing test project references an Exe, so there is no local precedent. This rests on standard .NET SDK behaviour (a reference assembly is produced regardless of `OutputType`). If it fails at T1, fall back to extracting `Corpus/` and `Benchmark/` into a small class library referenced by both |

## Tasks

### Task 1: Test project, corpus models and parsers

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj`
- Create: `Iverson.Server/Iverson.LoadTest/Corpus/CorpusModels.cs`
- Create: `Iverson.Server/Iverson.LoadTest/Corpus/BeirCorpusParser.cs`
- Create: `Iverson.Server/Iverson.LoadTest/Corpus/FreshStackCorpusParser.cs`
- Modify: `Iverson.Server/Iverson.Server.slnx`
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Corpus/BeirCorpusParserTests.cs`, `.../FreshStackCorpusParserTests.cs`

**Interfaces:**
- Produces: `CorpusDocument`, `CorpusQuery`, `Qrel`, and both parsers — consumed by Tasks 3 and 4.

- [ ] **Step 1: Create the test project on the existing convention.**
Copy the package set from `Iverson.Vector.Tests/Iverson.Vector.Tests.csproj` — `Microsoft.NET.Test.Sdk` 17.12.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 2.8.2, `FluentAssertions` 7.0.0, `coverlet.collector` 6.0.2 — with `net10.0`, `<IsTestProject>true</IsTestProject>`, `Nullable` and `ImplicitUsings` enabled. Omit NSubstitute and Testcontainers: nothing here is mocked or containerised. Add a single `ProjectReference` to `../Iverson.LoadTest/Iverson.LoadTest.csproj`.

If the reference to the Exe fails to build (P21 is the one assumption without local precedent), stop and report — the fallback is a class library, which is a plan-shape change.

- [ ] **Step 2: Add the project to the solution.**
One line in `Iverson.Server/Iverson.Server.slnx`:
```xml
  <Project Path="Iverson.LoadTest.Tests/Iverson.LoadTest.Tests.csproj" />
```

- [ ] **Step 3: Define the corpus models.**
`CorpusModels.cs` — three records, holding only what the harness reads:
```csharp
namespace Iverson.LoadTest.Corpus;

public sealed record CorpusDocument(string DocId, string Title, string Text);
public sealed record CorpusQuery(string QueryId, string Text);
/// <param name="Subtopic">TREC qrels iteration field. BEIR writes "0"; FreshStack carries the nugget id here.</param>
public sealed record Qrel(string QueryId, string Subtopic, string DocId, int Relevance);
```

- [ ] **Step 4: Write the BEIR parser.**
Three static methods over `TextReader`, so tests pass a `StringReader` and the scenario passes a `StreamReader`:
- `ParseCorpus` — one JSON object per line with `_id`, `title`, `text`. Missing `title` is legal and becomes `""`; missing or empty `_id` is an error naming the line number.
- `ParseQueries` — one JSON object per line with `_id`, `text`.
- `ParseQrels` — TSV, `query-id <TAB> corpus-id <TAB> score`, with a header line to skip. Emits `Subtopic = "0"`.

Use `System.Text.Json` (already available via the framework; no package needed).

- [ ] **Step 5: Write the FreshStack parser.**
Same three-method shape. FreshStack's on-disk layout is not verified in-repo — read it from the downloaded dataset when implementing, and keep the parser's public shape identical to BEIR's so Tasks 3 and 4 treat the two corpora uniformly. The nugget id goes in `Qrel.Subtopic`, which is what makes α-nDCG computable downstream (spec A3).

- [ ] **Step 6: Unit-test both parsers.**
Inline fixture strings, no files on disk. Cover per parser: a well-formed multi-line corpus; a document with no `title`; a qrels file whose header is skipped; and — for FreshStack — that the nugget id lands in `Subtopic` rather than being dropped. These tests exist because a silently wrong parser invalidates every downstream number (spec Testing).

- [ ] **Step 7: Run the tests.**
```bash
dotnet test Iverson.Server/Iverson.LoadTest.Tests
```

- [ ] **Step 8: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest.Tests Iverson.Server/Iverson.LoadTest/Corpus Iverson.Server/Iverson.Server.slnx
git commit -m "add benchmark corpus parsers and a load-test test project"
```

### Task 2: `BenchmarkDocument` entity and its authorization

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Entities/BenchmarkDocument.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs` (authorization dictionary, ~line 147-152)

**Interfaces:**
- Produces: the registered `BenchmarkDocument` type — consumed by Tasks 3 and 4.

- [ ] **Step 1: Define the entity.**
```csharp
using Iverson.Client.Attributes;

namespace Iverson.LoadTest.Entities;

[IversonEntity]
public sealed class BenchmarkDocument
{
    [IversonKey] public Guid Id { get; set; }

    public string DocId { get; set; } = "";
    public string Title { get; set; } = "";

    // Both annotations, deliberately: the chunk field and the vector field sharing one
    // property name is what makes centroidPossible true on the server, and therefore what
    // makes the centroid term in the fusion non-degenerate (spec A4).
    [IversonEmbedding]
    [IversonChunk]
    public string Body { get; set; } = "";

    [IversonTenant] public string TenantId { get; set; } = "";
}
```
No `OwnerId`: the bypass role sets `ownershipRequired` false (spec §3), so no ownership column is needed.

- [ ] **Step 2: Grant read access.**
Add to the `authorizationByTypeName` dictionary in `Program.cs`:
```csharp
["BenchmarkDocument"] = BuildAuthorizationRules("Body"),
```
`BuildAuthorizationRules` already grants `CanReadAll` to `iverson-loadtest-bypass` and restricts the named field to that same role, which is why the queries in Task 4 must run as the bypass identity.

- [ ] **Step 3: Prove the type registers and is readable.**
Registration is assembly-scanned, so the new type is picked up with no further wiring (spec A17/A18). Run against a live stack:
```bash
dotnet run --project Iverson.Server/Iverson.LoadTest -- seed
```
and confirm the console reports `Schemas registered.` without error.

This step is not ceremony. Spec A21 records that a type registered without authorization rules is denied on read and both vector RPCs return an **empty stream rather than an error** — so a mistake here surfaces as "retrieval found nothing" eight configurations later. Verify now, not then.

- [ ] **Step 4: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/Entities/BenchmarkDocument.cs Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "add BenchmarkDocument entity for the retrieval-quality benchmark"
```

### Task 3: Ingest scenario

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/KeyMap.cs`
- Create: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs` (DI registration, `switch` case)

**Interfaces:**
- Consumes: Task 1's `CorpusDocument` and parsers; Task 2's `BenchmarkDocument`.
- Produces: the persisted `ParentKey → DocId` map file — consumed by Task 4.

- [ ] **Step 1: Define the key map.**
`KeyMap.cs` — a `Dictionary<string, string>` (server key → corpus doc id) with `SaveAsync(path)` / `LoadAsync(path)` over `System.Text.Json`.

It must be a file, not an in-memory field. The spec's §1 has one ingest serving all eight configurations, and each configuration is a separate process run after a server rebuild — so the map has to outlive the ingest process for Task 4 to translate `ParentKey` at all.

- [ ] **Step 2: Write the ingest scenario.**
Constructor-inject `EntityCoordinator<BenchmarkDocument>`, `ActingUserIdentities`, and `ILogger<BenchmarkIngestScenario>`, following `WritePathRunner`'s shape.

For each parsed `CorpusDocument`: build a `BenchmarkDocument` with `DocId`, `Title`, `Body`, and the tenant id the other scenarios use; leave `Id` unset so the server assigns the UUIDv7 (spec A5); call
```csharp
var headers = new Grpc.Core.Metadata().WithActingUser(await identity.GetTokenAsync(ct));
var key     = await documents.PersistAsync(doc, headers, ct);
```
and record `key → doc.DocId`. A null return means the write failed — count it, log it, and keep going, but fail the run at the end if any document failed, because a partial corpus silently changes every metric.

Report progress with plain `Console.WriteLine` every N documents, matching `ReadPathScenario`. Do **not** use `BenchmarkReport`: it is the HdrHistogram latency reporter (P12), and latency measurement is out of scope per the spec.

- [ ] **Step 3: Ingest BEIR before FreshStack.**
Take the corpus name and paths from `CommandFlags`, and run BEIR first when both are requested. BEIR is ~9K documents against FreshStack's ~50K, and it alone answers the fusion question — so ordering it first means the spec's largest open risk (A10, laptop ingest feasibility, explicitly unverified) is hit early and its stated fallback stays available.

- [ ] **Step 4: Save the map and wire the command.**
Write the map next to the run-file output directory. Register the scenario in DI alongside the existing ones and add:
```csharp
case "benchmark-ingest":
    await services.GetRequiredService<BenchmarkIngestScenario>().RunAsync(flags);
    break;
```

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/Benchmark/KeyMap.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkIngestScenario.cs Iverson.Server/Iverson.LoadTest/Program.cs
git commit -m "add benchmark corpus ingest scenario"
```

### Task 4: Query scenario, max-passage aggregation and run files

**Files:**
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/MaxPassageAggregator.cs`
- Create: `Iverson.Server/Iverson.LoadTest/Benchmark/TrecRunWriter.cs`
- Create: `Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs`
- Modify: `Iverson.Server/Iverson.LoadTest/Program.cs` (DI registration, `switch` case)
- Test: `Iverson.Server/Iverson.LoadTest.Tests/Benchmark/MaxPassageAggregatorTests.cs`

**Interfaces:**
- Consumes: Task 1's `CorpusQuery`; Task 3's persisted key map; Task 2's registered type.

- [ ] **Step 1: Write the max-passage aggregator.**
A pure static function — `IReadOnlyList<(string DocId, double Score)> Aggregate(IEnumerable<(string ParentKey, double Score)> chunks, IReadOnlyDictionary<string,string> keyMap, int limit)`:
group by parent, take the **maximum** chunk score per parent, order by that score descending, then take `limit`. A parent missing from the key map is an error naming the key — it means ingest and query disagree about the corpus.

- [ ] **Step 2: Unit-test the aggregator.**
Three cases, all from the spec's Testing section plus the budget rule: several chunks of one parent collapse to a single entry carrying the **maximum** (not the first, not the sum); ordering follows the aggregated score rather than the input order; and a chunk list spanning more than `limit` parents truncates to exactly `limit`.

- [ ] **Step 3: Write the TREC run writer.**
`qid Q0 docid rank score runtag`, space-separated, rank starting at 1. The run tag encodes the configuration, e.g. `wc0.30-l0.70`. One file per RPC per configuration.

- [ ] **Step 4: Write the query scenario.**
Constructor-inject `ObjectSearchService.ObjectSearchServiceClient`, `ActingUserIdentities`, and the logger — **not** `EntityCoordinator`. Its search methods take no `Metadata` (P5), so they cannot carry an acting user, and an unauthenticated query is denied into an empty stream (spec A21).

Build each request with the public builder and send it through the raw client with acting-user headers:
```csharp
var headers = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

var similarReq = Query.Similar<BenchmarkDocument>(d => d.Body).Text(q.Text).TopK(50).Build();
using var similar = search.SearchSimilar(similarReq, headers, cancellationToken: ct);
await foreach (var r in similar.ResponseStream.ReadAllAsync(ct))
{
    // Data is a Struct of camelCase STRING fields taken from the Qdrant payload (P9);
    // it is not a deserialized entity, and StructConverter is internal to Core (P10).
    var docId = r.Data.Fields["docId"].StringValue;
    …
}
```

- [ ] **Step 5: Use the two budgets.**
`SearchSimilar` takes `TopK(50)` — there `top_k` counts entities, so 50 results are 50 documents.

`SearchChunks` takes `TopK(50 * ChunkBudgetMultiplier)`, then feeds the results through `MaxPassageAggregator.Aggregate(..., limit: 50)`. There `top_k` counts **chunks** and the server does not dedup by parent (spec A22), so a 50-chunk request would yield well under 50 distinct documents and understate Recall@50.

Declare the multiplier as one `private const int ChunkBudgetMultiplier` in this scenario, and do not vary it between configurations — that is a Global Constraint.

- [ ] **Step 6: Wire the command.**
```csharp
case "benchmark-query":
    await services.GetRequiredService<BenchmarkQueryScenario>().RunAsync(flags);
    break;
```
Take the configuration label (for the run tag), the key-map path and the output directory from `CommandFlags`.

- [ ] **Step 7: Run the tests.**
```bash
dotnet test Iverson.Server/Iverson.LoadTest.Tests
```

- [ ] **Step 8: Commit.**
```bash
git add Iverson.Server/Iverson.LoadTest/Benchmark Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest.Tests/Benchmark
git commit -m "add benchmark query scenario and TREC run output"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's "Not in this spec". A new spec → new plan cycle is required to add any of these.

- **Automating the sweep.** A shell loop over configurations that edits, builds, deploys and runs is fine; building a sweep runner is not part of this.
- **CI integration.** This is a calibration exercise, not a regression gate. If a run file ever becomes a baseline worth defending, that is a separate decision.
- **Latency measurement.** `Iverson.LoadTest`'s existing scenarios already own that, with HdrHistogram.
- **Acting on the results.** Changing `WCentroid` or λ on the strength of what this measures is a separate spec, and should be — the numbers come first.
- **Any change to the scoring components themselves.** `ResultReranker` and `ResultDiversifier` are read-only here apart from the throwaway constant edits on a scratch branch.

## Known issues inherited from spec

These exist in the implementation by design — accepted by the user during brainstorming.

**Laptop ingest feasibility is unverified.** The combined corpora are ~59K documents, which chunk into substantially more embedding calls, all through CPU Ollama. Whether this completes in a tolerable time on the laptop deployment cannot be established without running it — and a previous kind run on this machine hit a local `pids.max=307` ceiling. This is the largest open risk in the design. If ingestion proves intractable, the fallback is BEIR alone, which is ~9K documents and answers the fusion question without the diversity half.

**~100 queries is modest statistical power.** Two FreshStack topics give roughly 100 questions. That is enough to detect a large diversification effect and not enough to resolve a subtle one, so a null result on the λ sweep should be read as "no large effect detected", never as "λ = 0.70 is optimal". Ben chose two topics over one for exactly this reason; more topics would cost proportionally more ingest.

**α-nDCG depends on expressing FreshStack's nuggets in the scoring tool's expected shape.** `ir_measures` reads subtopic ids from the qrels iteration field; FreshStack's own evaluation package is the lower-risk route and should be preferred if its input format accepts a standard run file. This was not verified end-to-end.

**Ablation builds are knowingly red.** See A19 and §7. Accepted as the cost of not adding a configuration seam.
