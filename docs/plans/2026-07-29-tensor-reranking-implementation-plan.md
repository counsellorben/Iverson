# Tensor Re-Ranking / Fusion Scoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-29-tensor-reranking-design.md` (commit SHA: `ee2ed68`)

**Goal:** Re-rank `SearchChunks` and `SearchSimilar` results server-side by fusing Qdrant's query-to-vector cosine with document-level centroid similarity and a recency decay, so the top `top_k` results a RAG consumer reads are better ones.

**Architecture:** A pure, I/O-free `IResultReranker` in `Iverson.Vector` fuses three signals as a weighted mean over the signals actually present. `ObjectSearchGrpcService` over-fetches `4 × top_k` from Qdrant, batch-fetches parent centroids from the object collection through a new retrieve-by-ids role method, resolves each candidate's decay value, calls the re-ranker, and trims to `top_k`.

**Tech stack:** .NET 10, Qdrant.Client 1.18.1 (`RetrieveAsync` with `WithVectorsSelector`), `System.Numerics.Tensors` 10.0.10 (`TensorPrimitives.CosineSimilarity`), xUnit + NSubstitute + FluentAssertions, Testcontainers for the real-Qdrant fixture.

---

## Global Constraints

Copied from the spec; every task must hold to these.

- **Fixed server-side constants.** Weights are `base` 0.60, `centroid` 0.30, `decay` 0.10; half-life is 180 days; over-fetch is `4 × top_k`. No caller-supplied weights, no per-request opt-out, no kill switch, no configuration knob.
- **Renormalize over present signals**, never substitute `0` or `1.0` for an absent one: `fused = Σ(wᵢ·sᵢ) / Σ(wᵢ)` over present signals only. With no centroid and no decay the result must be exactly `base`, preserving today's ordering bit-for-bit.
- **Re-ranking is unconditional** on both RPCs; the returned `score` becomes the fused value.
- **No cap on the over-fetched candidate count.** `top_k = 1000` legitimately fetches 4000.
- **Failures degrade, never throw:** a failed centroid retrieve falls back to raw-cosine ordering; a dimension mismatch or unparseable decay value makes that signal absent for that candidate.
- **One canonicalization rule for timestamps**, applied on both the write side and the read side.
- **Scope is `SearchChunks` and `SearchSimilar` only** — not the DSL `Search` path, not `VECTOR_SIMILAR` clauses.
- **Existing data is wiped before part 3 ships** (spec §6), so every point carries the canonical timestamp format and there is no mixed-format corpus. Task 3's write-side change assumes this; the wipe itself is an operational step outside these tasks.

## File Structure

**Create**
- `Iverson.Server/Iverson.Vector/IResultReranker.cs` — the fusion contract and its candidate record.
- `Iverson.Server/Iverson.Vector/ResultReranker.cs` — implementation over `TensorPrimitives`.
- `Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs` — formula tests.
- `Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs` — decay-field convention and decay-value computation.
- `Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs`

**Modify**
- `Iverson.Server/Iverson.Vector/IVectorRoles.cs` — `IVectorQueryService` gains the retrieve method.
- `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs` — implement it over `RetrieveAsync`.
- `Iverson.Server/Iverson.Vector/IntelligenceFilterBuilder.cs` — timestamp operand canonicalization.
- `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj` — `System.Numerics.Tensors` package reference.
- `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs` — register the re-ranker.
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — `ExtractTypedValue` timestamp case; `KeyToUlong` visibility.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — thread timestamp columns into both filter entry points; over-fetch, centroid fetch, re-rank, trim on both RPCs.

**Test**
- `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs` — real-Qdrant coverage of the retrieve.
- `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs` — canonicalization cases.
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — write-side normalization.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — service-level wiring.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here (spec's A1–A21). The load-bearing ones:

- **A1–A2:** `VectorSearchResult(ulong Id, double Score, IReadOnlyDictionary<string,string> Payload)`; `SearchNamedAsync` returns the full payload, stringified.
- **A4:** chunk payload carries `parent_id = ev.Key`; the object point id is `KeyToUlong(key)`.
- **A5–A7:** metadata reaches both chunk and object payloads under camelCase keys; `MetadataColumns` joined to `ScalarColumns[].SqlType` identifies timestamp columns (`ClrDatetime → TIMESTAMPTZ/DATETIME`).
- **A8:** `ExtractTypedValue` has no timestamp case today, so the stored format is client-determined — the defect §8 fixes.
- **A10–A14:** `top_k` is unclamped; both RPCs can resolve the object collection and mint a second read-only key; Qdrant returns cosine similarity; `_centroid` names come from `ToSnakeCase()`.
- **A15:** exactly one test asserts a score value; nothing asserts ordering; no client interprets `score`.
- **A16:** `SearchSimilar` gets a centroid only when its property carries both `[IversonEmbedding]` and `[IversonChunk]`.
- **A17–A19:** the Qdrant client supports batched retrieve with named-vector selection; `TensorPrimitives.CosineSimilarity` exists; both response messages carry `float score`.
- **A20–A21:** `ExtractTypedValue` feeds only Qdrant payloads; exactly `EQUALS`, `NOT_EQUALS`, `IN` reach a payload string comparison.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `Iverson.Vector/` is flat and holds no `IResultReranker.cs`/`ResultReranker.cs` | `ls Iverson.Vector/` — 9 `.cs` files, no subdirectories |
| P2 | File path | **Corrected from draft.** There is no `Search/` folder in `Iverson.Api`; `Grpc/` already holds non-service helpers, so `DecayFieldResolver` belongs there | `ls -d Iverson.Api/*/` → Authorization, Consumers, Grpc, Properties, Reconciliation, Schema, Tenancy. `Grpc/` contains `EntityKeyAccessor.cs`, `ProtoPayloadHelper.cs`, `StructFieldAccess.cs`, `RelationValidator.cs` |
| P3 | Signature | `TensorPrimitives.CosineSimilarity(ReadOnlySpan<float>, ReadOnlySpan<float>)` exists | `System.Numerics.Tensors.xml` (10.0.10) |
| P4 | Signature | Retrieve accessor chain: `RetrievedPoint.Id.Num` (`ulong`), `.Vectors` → `VectorsOutput.Vectors` → `NamedVectorsOutput.Vectors` (`MapField<string, VectorOutput>`) → `VectorOutput.Dense` (`DenseVector`) | Reflection dump over `Qdrant.Client` 1.18.1, including `DenseVector.Data : RepeatedField<float>`. `VectorOutput` exposes both `.Data` and `.Dense`; this plan uses `.Dense`, matching the write side's `Vector.Dense` convention |
| P5 | Signature | `PointId` converts implicitly from `ulong` | Reflection: `PointId.op_Implicit(UInt64)` |
| P6 | Signature | `Build(IReadOnlyList<SearchClause> clauses, SearchLogic logic, string rpcName)` returns `Filter` (not `Filter?`) | `IntelligenceFilterBuilder.cs:16` |
| P7 | Signature | `ColumnDescriptor(string Name, string SqlType, bool IsNullable)`; `SchemaDescriptor` exposes `KeyColumn` and `ScalarColumns` | `SchemaDescriptor.cs:51`, `:9-10` |
| P8 | Signature | `ObjectSearchGrpcService`'s primary constructor already takes `IVectorQueryService` and `ILogger<ObjectSearchGrpcService>` | `ObjectSearchGrpcService.cs:30-36` |
| P9 | Command | `Iverson.Server.slnx` and both test `.csproj` paths exist as written | `ls` on all three |
| P10 | Command | `feat(vector)`, `feat(consumer)`, `feat(search)` are established Conventional-Commit scopes | `git log --all` grep: `feat(vector)` ×2, `feat(consumer)` ×2, `feat(search)` ×1, plus `fix`/`test` variants |
| P11 | Ordering | Tasks 1–4 touch disjoint symbols; only Task 5 consumes their output | T1: `IVectorRoles`/`IntelligenceVectorService`. T2: two new files + csproj + DI. T3: `IntelligenceFilterBuilder` + `ExtractTypedValue`. T4: one new file. No overlap |
| P12 | Ordering | Tasks 3 and 5 both edit `ObjectSearchGrpcService` but in different regions | T3 edits the filter-construction sites (`:161`, `:632`); T5 edits the post-search sites (`:202`, `:303`) and the constructor. T3 runs first |
| P13 | Code validity | `Iverson.Vector.csproj` takes a plain `PackageReference`; no central package management; `InternalsVisibleTo Iverson.Vector.Tests` already declared | Read of the csproj — `net10.0`, four `PackageReference` entries, `AssemblyAttribute` IVT block |
| P14 | Code validity | `RetrieveAsync(string, IReadOnlyList<PointId>, WithPayloadSelector, WithVectorsSelector, …)` and `WithVectorsSelector.op_Implicit(String[])` both exist | `Qdrant.Client.xml` (1.18.1) |
| P15 | Consumer impact | `Build` has **one** production caller but **14** call sites in `QdrantFilterBuilderTests` — the added parameter must be defaulted | `ObjectSearchGrpcService.cs:161`; `grep -c` on `QdrantFilterBuilderTests.cs` → 14 |
| P16 | Consumer impact | Adding a method to `IVectorQueryService` breaks no implementer: one production class, and every test double is `Substitute.For<>` | `typeof(IntelligenceVectorService).Should().Implement<IVectorQueryService>()` at `QdrantVectorServiceTests.cs:215`; all other hits are `Substitute.For<IVectorQueryService>()` |
| P17 | Consumer impact | `ExtractTypedValue` already returns `object?` and all three callers assign into `Dictionary<string, object>`, so returning a `DateTimeOffset` needs no signature or call-site change | `IntelligenceStoreConsumer.cs:612` (return type), callers at `:233`, `:346`, `:351`; `ToQdrantValue` handles `DateTimeOffset` at `IntelligenceVectorService.cs:185` |
| P18 | Consumer impact | **Corrected from spec §8.** Two tests bind `KeyToUlong` by reflection on `typeof(IntelligenceStoreConsumer)` with `BindingFlags.NonPublic`. Widening `private` → `internal` **in place** satisfies the search service's need and keeps both tests passing; *moving* the method to another type would break them | `IntelligenceStoreConsumerTests.cs:746`, `:764`; `internal` remains `NonPublic` to reflection |
| P19 | Consumer impact | The vector roles are registered in `Iverson.Vector/ServiceCollectionExtensions.cs`; the re-ranker registration belongs beside them | `:40-46` — `AddSingleton<IntelligenceVectorService>()` then role-forwarding singletons |
| P20 | Sibling sweep | **Corrected from spec §8.** The two RPCs reach the filter builder through *different* public entry points: `SearchSimilar` → `Build` (`:161`), `SearchChunks` → `MatchEquality` (`:632`). Both funnel to the private `BuildEqualityCondition`, so canonicalization must live there and **both** entry points must thread the timestamp columns. Property names arrive camelCased at both (`:152`, `:632` `canonicalName.ToCamelCase()`) | `IntelligenceFilterBuilder.cs:16`, `:59`, `:73-74`, `:105`; `ObjectSearchGrpcService.cs:152`, `:161`, `:632` |

## Tasks

### Task 1: Retrieve named vectors by point id

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IVectorRoles.cs`
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs`

**Interfaces:**
- Produces: `RetrieveNamedVectorAsync`, which Task 5 calls to fetch parent centroids. Independent of Tasks 2–4.

- [ ] **Step 1: Add the interface method**

On `IVectorQueryService`, beside `SearchNamedAsync`:

```csharp
Task<IReadOnlyDictionary<ulong, float[]>> RetrieveNamedVectorAsync(
    string collectionName,
    IReadOnlyList<ulong> ids,
    string vectorName);
```

One named vector is all any caller needs; returning a map keyed by point id is what Task 5 consumes directly. Adding the method breaks no implementer (P16).

- [ ] **Step 2: Implement it**

On `IntelligenceVectorService`, mirroring `SearchNamedAsync`'s telemetry shape (`:117-122`): open `Telemetry.Source.StartActivity("qdrant.retrieve_named_vector", ActivityKind.Client)` and set `db.system`, `qdrant.collection`, `qdrant.vector_name`, plus the id count. Then:

```csharp
var points = await client.RetrieveAsync(
    collectionName,
    ids.Select(id => (PointId)id).ToList(),
    withPayload: false,
    withVectors: new[] { vectorName });

var result = new Dictionary<ulong, float[]>();
foreach (var p in points)
{
    if (p.Vectors?.Vectors?.Vectors.TryGetValue(vectorName, out var v) == true && v.Dense is not null)
        result[p.Id.Num] = v.Dense.Data.ToArray();
}
return result;
```

Points lacking the named vector are simply absent from the map — Task 5 treats absence as "no centroid signal" (Global Constraints). `PointId` converts implicitly from `ulong` (P5); `withVectors` takes a `string[]` (P14); the accessor chain is P4.

- [ ] **Step 3: Integration test against real Qdrant**

In `QdrantIntegrationTests`, following the file's existing `QdrantContainerFixture` convention: upsert a point carrying two named vectors, retrieve only one of them by id, and assert the returned map contains that point's id with the expected values and that the other vector is not returned. Add a second case asserting that ids with no such point are absent from the map rather than throwing.

A mock cannot confirm the named-vector shape Qdrant actually returns — this is the class of gap part 4a's final review caught.

- [ ] **Step 4: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Vector/IVectorRoles.cs Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs
git commit -m "feat(vector): retrieve a named vector by point id"
```

---

### Task 2: The re-ranker

**Files:**
- Create: `Iverson.Server/Iverson.Vector/IResultReranker.cs`
- Create: `Iverson.Server/Iverson.Vector/ResultReranker.cs`
- Modify: `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj`
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs`

**Interfaces:**
- Produces: `IResultReranker`, which Task 5 calls. Independent of Tasks 1, 3, 4.
- Consumes: the decay **value** (already in `[0,1]`), not a timestamp — Task 4 owns the half-life curve. The re-ranker performs no I/O and reads no clock.

- [ ] **Step 1: Add the package reference**

In `Iverson.Vector.csproj`, alongside the existing entries (P13):

```xml
<PackageReference Include="System.Numerics.Tensors" Version="10.0.10" />
```

- [ ] **Step 2: Define the contract**

```csharp
public sealed record RerankCandidate(
    ulong    Id,
    double   BaseScore,
    float[]? Centroid,
    double?  Decay);

public sealed record RerankedResult(ulong Id, double FusedScore);

public interface IResultReranker
{
    IReadOnlyList<RerankedResult> Rerank(float[] queryVector, IReadOnlyList<RerankCandidate> candidates);
}
```

- [ ] **Step 3: Implement the fused score**

`ResultReranker.Rerank` scores each candidate as the weighted mean over the signals actually present, then returns the candidates sorted by fused score descending:

```csharp
private const double WBase = 0.60, WCentroid = 0.30, WDecay = 0.10;
```

- `base` is always present, contributing `WBase * candidate.BaseScore`.
- `centroid` is present when `Centroid is not null && Centroid.Length == queryVector.Length`; its similarity is `TensorPrimitives.CosineSimilarity(queryVector, candidate.Centroid)` (P3). A length mismatch makes the signal **absent**, not zero (Global Constraints; spec §7).
- `decay` is present when `Decay is not null`.

Divide the weighted sum by the sum of the weights of the present signals. With neither centroid nor decay present the result must equal `BaseScore` exactly.

Do **not** add a zero-magnitude guard, a clock, a configuration knob, or a per-request weight override.

- [ ] **Step 4: Register it**

In `Iverson.Vector/ServiceCollectionExtensions.cs`, beside the existing role registrations (P19):

```csharp
services.AddSingleton<IResultReranker, ResultReranker>();
```

- [ ] **Step 5: Test the formula**

In `ResultRerankerTests`, with known vectors so every expected value is computed by hand:

- all three signals present — asserted fused value;
- centroid promotes a candidate above one with a higher `BaseScore`;
- decay breaks a tie between two equal-`BaseScore`, equal-centroid candidates;
- centroid absent → `0.857·base + 0.143·decay`;
- decay absent → `0.667·base + 0.333·centroid`;
- both absent → fused equals `BaseScore` exactly, and the returned order matches the input order for already-descending input (the "today's behavior preserved" case);
- centroid present but of the wrong length → treated as absent, not as zero.

The types are `internal`-visible to this test project already (P13), so no reflection is needed.

- [ ] **Step 6: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Vector/IResultReranker.cs Iverson.Server/Iverson.Vector/ResultReranker.cs Iverson.Server/Iverson.Vector/Iverson.Vector.csproj Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs
git commit -m "feat(vector): fused-score re-ranker over base, centroid and decay signals"
```

---

### Task 3: Timestamp canonicalization, write side and read side

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:612-628`
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceFilterBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:161`, `:632`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs`

**Interfaces:**
- Produces: canonical `"o"`-format timestamps in Qdrant payloads and the matching read-side rule. Independent of Tasks 1, 2, 4; must land **before** Task 5 because both touch `ObjectSearchGrpcService` (P12).

- [ ] **Step 1: Canonicalize on write**

In `ExtractTypedValue`'s type switch (`:621-627`), add a case before the default:

```csharp
"TIMESTAMPTZ" or "DATETIME" =>
    v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
        v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
        ? dto
        : null,
```

`ToQdrantValue`'s existing `DateTimeOffset` branch (`IntelligenceVectorService.cs:185`) then writes `"o"` form. The method already returns `object?` and all three callers assign into `Dictionary<string, object>`, so nothing else changes (P17). A value that will not parse yields `null`, which the callers already skip — the column is simply absent from the payload rather than stored in a format nothing can read.

- [ ] **Step 2: Canonicalize on read, in the shared helper**

The two RPCs enter the builder through different public methods (P20), so the rule goes in the private `BuildEqualityCondition` (`:103-105`) that both funnel through, and each entry point threads the timestamp columns in with a **defaulted** parameter — `Build` has 14 test call sites that must keep compiling (P15):

```csharp
public static Filter Build(
    IReadOnlyList<SearchClause> clauses,
    SearchLogic logic,
    string rpcName,
    IReadOnlySet<string>? timestampColumns = null)

public static Condition MatchEquality(
    string property,
    SearchValue value,
    IReadOnlySet<string>? timestampColumns = null)
```

`BuildEqualityCondition` canonicalizes a `StringVal` operand — parse with `DateTimeOffset.TryParse` and re-emit `"o"` — when `timestampColumns` contains the property, leaving it untouched otherwise. Apply it for `EQUALS`, `NOT_EQUALS` and `IN`; those are the only three operators that reach a payload string comparison (spec A21), and `IN` canonicalizes each element of its list. An operand that will not parse passes through unchanged rather than throwing — the caller sent a value that was never going to match.

**Casing contract:** property names arrive camelCased at both entry points (`ObjectSearchGrpcService.cs:152` and `:632`'s `canonicalName.ToCamelCase()`), so the set must hold camelCase names, or the comparison must be `OrdinalIgnoreCase`. A mismatch here silently disables the canonicalization and reinstates the bug — assert it in the tests.

- [ ] **Step 3: Supply the set from both call sites**

In `ObjectSearchGrpcService`, derive the type's timestamp columns once per request — the `ScalarColumns` entries whose `SqlType` is `TIMESTAMPTZ` or `DATETIME`, camelCased (P7) — and pass them to `Build` at `:161` and to `MatchEquality` at `:632`.

- [ ] **Step 4: Test both sides**

In `IntelligenceStoreConsumerTests`: an entity with a declared timestamp metadata column whose client-sent value is a non-canonical but parseable string (e.g. `2026-07-29T00:00:00Z`) is stored in `"o"` form; an unparseable value results in the column being absent from the payload.

In `QdrantFilterBuilderTests`, following the file's existing convention: `EQUALS` on a timestamp column canonicalizes the operand; `NOT_EQUALS` and `IN` do the same; a non-timestamp column's operand is untouched; a caller value that will not parse passes through unchanged; and a call with the default (omitted) `timestampColumns` behaves exactly as today.

- [ ] **Step 5: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Vector/IntelligenceFilterBuilder.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs
git commit -m "feat(search): canonicalize timestamp payload values on write and in filter operands"
```

---

### Task 4: The decay-field convention

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs`

**Interfaces:**
- Produces: the decay field's payload key and a decay value in `[0,1]`, both consumed by Task 5. Independent of Tasks 1–3.

- [ ] **Step 1: Resolve the field**

An `internal static` resolver that takes a `SchemaDescriptor` and returns the camelCase payload key of the decay column, or `null`:

- consider the `MetadataColumns` entries whose matching `ScalarColumns` entry has `SqlType` `TIMESTAMPTZ` or `DATETIME` (P7; spec A7);
- exactly one → return its camelCase name;
- none → `null`;
- two or more → `null`, and log once per type.

The two-or-more case refuses to guess deliberately (spec §6). "Once per type" means the resolver caches its per-type answer; the cache is also what keeps the join off the hot path.

- [ ] **Step 2: Compute the decay value**

A second `internal static` member that turns a payload string into a decay value:

```csharp
internal static double? ComputeDecay(string? storedValue, DateTimeOffset now)
```

`0.5 ^ (age / halfLife)` with `halfLife` = 180 days, parsing with `DateTimeOffset.TryParse` under `DateTimeStyles.RoundtripKind`. Returns `null` — signal absent, never a neutral `1.0` — when the value is null, empty, or unparseable (Global Constraints; spec §7). `now` is a parameter rather than a `DateTimeOffset.UtcNow` call so the tests can pin exact values without a clock abstraction.

- [ ] **Step 3: Test the convention and the curve**

In `DecayFieldResolverTests`: zero, one, and two timestamp metadata columns; a timestamp column that is *not* declared metadata is not selected; the camelCase key matches the payload key the consumer writes. For the curve: age 0 → `1.0`; age exactly 180 days → `0.5`; age 360 days → `0.25`; null, empty and unparseable values → `null`.

- [ ] **Step 4: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs
git commit -m "feat(search): decay-field convention and half-life curve"
```

---

### Task 5: Wire the re-rank into both RPCs

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:564`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — constructor, `SearchSimilar` (`:202`), `SearchChunks` (`:303`)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's `RetrieveNamedVectorAsync`, Task 2's `IResultReranker`, Task 4's resolver. Runs after Task 3 (P12).

- [ ] **Step 1: Widen `KeyToUlong`**

Change `private static ulong KeyToUlong` to `internal static` in place (`IntelligenceStoreConsumer.cs:564`). Do **not** move it to another type: two tests bind it by reflection on `typeof(IntelligenceStoreConsumer)` with `BindingFlags.NonPublic`, and `internal` remains `NonPublic`, so widening in place keeps them passing while giving the search service the same key→point-id function (P18). Both sides must derive identical ids.

- [ ] **Step 2: Inject the re-ranker**

Add `IResultReranker reranker` to `ObjectSearchGrpcService`'s primary constructor (P8).

- [ ] **Step 3: Over-fetch**

In both RPCs, request `4 × topK` from `SearchNamedAsync` (`:202`, `:303`) instead of `topK`. `top_k` is unclamped (spec A10) and `request.TopK` is `uint32` widened to `ulong`, so the multiply cannot overflow. Do not cap the result.

- [ ] **Step 4: Fetch the centroids**

After the search returns and before scoring, in both RPCs:

- **`SearchSimilar`** — the centroid vector name is `<property>_centroid` and is used **only when the searched property carries both `[IversonEmbedding]` and `[IversonChunk]`** (spec §5, A16); otherwise skip the fetch entirely and let every candidate's centroid be absent. The ids are the candidates' own `Id` values.
- **`SearchChunks`** — the vector name is `<property>_centroid` (the property is a chunk field by definition), and the ids are the distinct `KeyToUlong(parent_id)` values read from each candidate's payload (spec §4; A4).

Then resolve the object collection with `ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: false)` (spec A11) and call `RetrieveNamedVectorAsync` inside its own `using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(objectCollection, readOnly: true)))` scope (A12) — a second sequential scope, matching the pattern already used at `:198` and `:300`.

If the retrieve throws, log and continue with every centroid absent — a degraded ranking beats a failed search (Global Constraints; spec §7).

- [ ] **Step 5: Score and trim**

Build one `RerankCandidate` per search result: `BaseScore` from the result's `Score`; `Centroid` from the map (absent when the id is missing); `Decay` from `DecayFieldResolver` — resolve the field once per request, read that key from the candidate's payload, and compute the value. Call `reranker.Rerank(queryVector, candidates)`, take the first `topK`, and stream them in the returned order with the fused score in the response's `score` field.

`SearchChunks` still returns each chunk's own `parent_key` and `chunk_text`; only the ordering and the score change.

- [ ] **Step 6: Service-level tests**

In `ObjectSearchGrpcServiceTests`, following the file's existing `Substitute.For<IVectorQueryService>` convention:

- `4 × top_k` is the limit actually passed to `SearchNamedAsync`, and exactly `top_k` results are streamed;
- `SearchChunks` batches the centroid retrieve to the **distinct** parent ids — three chunks sharing one parent produce a single id in the call;
- the retrieve throwing leaves the results in raw-cosine order rather than failing the call;
- `SearchSimilar` on a dual-annotated property fetches centroids; on an embedding-only property it does not call the retrieve at all;
- the fused score reaches the response `score` field.

- [ ] **Step 7: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(search): re-rank SearchChunks and SearchSimilar results by fused score"
```

## Tasks NOT in this plan

Inherited from the spec's Out of scope. A new spec → plan cycle is required to add any of these.

- **Keyword and summary signals.** Considered and excluded; the fused score uses base cosine, centroid and decay only.
- **Caller-supplied weights or a per-request opt-out.** The weights are fixed server-side constants by decision.
- **The DSL `Search` path and `VECTOR_SIMILAR` clauses.** Re-ranking applies to `SearchChunks` and `SearchSimilar` only.
- **Part 4b cluster centroids** and **part 5's agent-facing surface.** Their own specs.
- **Backfilling centroids onto pre-4a documents.** They re-rank without the centroid signal until republished; §2's renormalization is what makes that safe.

## Known issues inherited from spec

**Re-ranking is unconditional and changes `score` semantics for every existing caller** of the two RPCs. Accepted by Ben as a consequence of choosing fixed server-side constants over per-request knobs; §2 records the reasoning.

**A `top_k = N` request costs a `4N` Qdrant fetch** with no ceiling. Accepted deliberately — see §3.
