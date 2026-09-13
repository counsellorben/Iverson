# On-the-fly Popularity Time Decay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-13-popularity-time-decay-design.md` (commit SHA: `18f87a0`)

**Goal:** Add a recency term to the relation-popularity signal, computed at query time against the current clock, so stored data ages without being rewritten — while remaining bit-identical to today's ranking at the default `RecencyBoost = 0`.

**Architecture:** The consumer writes a monthly bucket series alongside the existing lifetime count, as a self-encoded string on the parent's Qdrant point. At query time the two popularity helpers sum a decayed count `D` from that series and fuse it as `(N + β·D) / (N + β·D + SaturationPoint)`. A new `[IversonPopularitySignal]` attribute marks which UTC `DateTime` column on the child entity is the interaction timestamp.

**Tech stack:** .NET 10 / C#, StarRocks (`DateHistogram` aggregation), Qdrant (payload merge via `SetPayloadAsync`), protobuf across five client languages (.NET, TypeScript, Python, Java, Go), xUnit + NSubstitute + FluentAssertions, Testcontainers for live-Qdrant tests.

---

## Global Constraints

Project-wide values every task must use verbatim, from the spec:

- `RecencyBoost` default **0.0**; validation finite and **≥ 0**.
- `RecencyHalfLifeDays` default **180.0**; validation finite, **> 0**, and **≤ 300**.
- Bucket retention: **exactly 60** monthly buckets, a fixed constant — never derived from the half-life.
- Bucket interval is **month**, fixed — not configurable.
- Bucket age is measured from the bucket's **start**, not its midpoint.
- Buckets carry **counts only**. No weights, no interaction types.
- The interaction timestamp is interpreted as **UTC**.
- Both payload keys are written on **every** update; the series is `""` when absent, never omitted.
- Commit messages: lowercase imperative, no prefix (matching `git log`: "add design for…", "wire Popularity into…"). Do **not** introduce a Conventional-Commits prefix.

## File Structure

**Create:**
- `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonPopularitySignalAttribute.cs` — the new marker attribute.

**Modify:**
- `Iverson.Clients/Common/Proto/object_mapping.proto` — `bool is_popularity_signal = 23` on `PropertyDescriptor`.
- `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` — `string? PopularitySignalColumn`.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — map the flag; reject two marked properties.
- `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs` — set the flag from the attribute.
- `Iverson.Clients/TypeScript/src/core.ts` — **two** `PropertyDescriptor` literal sites.
- `Iverson.Clients/Python/iverson_client/core.py` — set the flag.
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java` — set the flag.
- `Iverson.Clients/Go/iverson/registrar.go` — struct-tag metadata plus the descriptor projection.
- `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs` — two new options and their validation.
- `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` — second aggregation, encode, both keys.
- `Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs` — the shared `ComputeRecencySum` helper.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — both popularity helpers; hoist `now`.

**Test:**
- `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs` — SP9 empty-string round trip.
- `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` — flag mapping and duplicate rejection.
- `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs` — .NET attribute wiring.
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`, `Iverson.Clients/Python/tests/test_schema_registrar.py`, `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`, `Iverson.Clients/Go/iverson_test/registrar_test.go`.
- `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs` — the two validators.
- `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs` — series write; **fix the `p.Count == 1` assertion**.
- `Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs` — `ComputeRecencySum`.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — fused popularity on both paths.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here. Trusted as ground truth: **A1–A7, A10–A12, A15–A18, A21–A23, A25, A26, A28**, the `max()`-degeneracy and no-feedback-path items, and **SP1–SP8**. Full statements and evidence are in the spec's `Verified assumptions` table.

Two inherited items carry forward as live constraints rather than background facts:

- **SP8** — `SetPayload` merges; omitting a key preserves its previous value. This is why Task 6 writes both keys unconditionally.
- **SP9** — the empty-string payload round trip is **UNVERIFIED** in the spec. Task 1 discharges it, and is a gate: if it fails, the design needs revision before Task 6.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Consumer impact | Adding `PopularitySignalColumn` breaks no construction site | 19 `new SchemaDescriptor` sites, all in tests; the member is a defaulted nullable with no `required`, so object initialisers are unaffected |
| 2 | Consumer impact | Adding a descriptor member does **not** trip schema-drift detection | `SchemaRegistrationOrchestrator.cs:300-301` compares **only** `DocumentTemplateSource` via `string.Equals(..., Ordinal)`; `:288-292` states comparing parsed models "would look changed", which is why a single raw string is used |
| 3 | Consumer impact | `PopularityFor` has exactly one call site | `ObjectSearchGrpcService.cs:288` (definition `:1009`); `private static`, so the signature change is file-local |
| 4 | Consumer impact | `RetrievePopularityOrDegradeAsync` has exactly one call site | `ObjectSearchGrpcService.cs:651` (definition `:954`); `private`, file-local |
| 5 | Consumer impact | **TypeScript has two `PropertyDescriptor` literal sites**, not one | `core.ts:376` (`isMetadata: metadataFields.has(fieldName)`) and `core.ts:404` (`isMetadata: false`, the all-defaults path). Task 4 must edit **both** |
| 6 | Signature | Each client sets markers in a different shape | .NET `descriptor.IsMetadata = true` (`SchemaRegistrar.cs:199`); TS object literals (above); Python `is_metadata=(...)` kwarg (`core.py:280`); Java `b.setIsMetadata(true)` (`SchemaRegistrar.java:220`); Go `IsMetadata: fm.Metadata` (`registrar.go:142`) |
| 7 | Signature | `AggregationDescriptor` takes `CalendarInterval` as a named argument | `Aggregation.cs:15-24` — `(string Name, AggregationKind Kind, string Field, int Size = 10, string? CalendarInterval = null, …)` |
| 8 | Signature | Self-contained option validation belongs in `AddPopularitySignalOptions` | `PopularitySignalOptions.cs:24-37` validates `SaturationPoint` there; `ValidateAtStartup` handles schema-dependent checks only |
| 9 | File path | `SchemaBuilderTests.cs` exists | `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` |
| 10 | File path | All five client test files exist | TS `tests/schema-registrar.test.ts`; Python `tests/test_schema_registrar.py`; Java `client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`; Go `iverson_test/registrar_test.go`; .NET `Iverson.Client.Core.Tests/SchemaRegistrarTests.cs` |
| 11 | File path | The live-Qdrant fixture exists and is reusable | `QdrantVectorServiceTests.cs:9-10` — `[Collection(ContainerCollection.Name)]` on a class taking `IClassFixture<QdrantContainerFixture>` |
| 12 | Command | Solution build is `dotnet build Iverson.slnx` | `Iverson.slnx` at repo root |
| 13 | Command | TypeScript: `npm test` runs `npm run typecheck && vitest run`; stubs via `npm run generate` | `package.json` scripts |
| 14 | Command | Python pytest; Java Maven; Go `go test` | `pyproject.toml` `[tool.pytest.ini_options]`; `Iverson.Clients/Java/pom.xml`; `Iverson.Clients/Go/go.mod` |
| 15 | Command | Commit convention is lowercase imperative, no prefix | `git log --oneline -12` — "add design for…", "wire Popularity into…", "apply 5 fixes from…" |
| 16 | Ordering | No task imports a symbol a later task creates | Tasks 3–4 consume Task 2's proto field; Task 6 consumes Task 2's descriptor member; Task 7 consumes Task 5's options. All dependencies point backwards |
| 17 | Code validity | Existing flags are already covered by tests the new flag joins | `is_metadata`/`IsMetadata` appears in `ObjectMappingGrpcServiceTests.cs` and `SchemaBuilderTests.cs` — additive, so no existing assertion breaks |
| 18 | Code validity | `TakeLast(60)` selects the most recent buckets | SQL emits `ORDER BY bucket_key` ascending (`StarRocksQueryBuilder.cs:289`), and zero-padded `%Y-%m` sorts chronologically (spec A18), so the tail is the most recent |

## Tasks

### Task 1: Discharge SP9 — the empty-string payload round trip

**Files:**
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs`

**Interfaces:**
- Produces: a verified answer to whether `""` replaces a prior payload value. Task 6's correctness depends on it.

- [ ] **Step 1: Add a live-container test asserting an empty string replaces a prior value**

Place it beside the existing `SetPayloadAsync_AddsFieldWithoutTouchingVectorOrExistingPayload`, using the same fixture.

```csharp
[Fact]
public async Task SetPayloadAsync_EmptyStringReplacesPriorValue()
{
    var svc    = fixture.Service;
    var mgr    = fixture.CollectionManager;
    var name   = "col_" + Guid.NewGuid().ToString("N")[..8];
    var vector = new float[] { 1f, 0f, 0f, 0f };

    await mgr.EnsureCollectionAsync(name, vectorSize: 4);
    await svc.UpsertAsync(name, 1UL, vector, new Dictionary<string, object> { ["series"] = "2026-01:5" });

    await svc.SetPayloadAsync(name, 1UL, new Dictionary<string, object> { ["series"] = "" });

    var payload = await svc.RetrievePayloadAsync(name, [1UL]);
    // The whole point: a stale series must not survive an empty write. Either the key reads
    // back as "" or it is gone — both yield D = 0. What must NOT happen is "2026-01:5".
    var survived = payload[1UL].TryGetValue("series", out var s) ? s : "";
    survived.Should().BeEmpty();
}
```

- [ ] **Step 2: Run it**
```bash
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj --filter SetPayloadAsync
```

- [ ] **Step 3: Gate — stop if it fails**

If `survived` comes back as `"2026-01:5"`, Qdrant ignores empty payload values and the spec's §2.1 fix does **not** overwrite a stale series. **Do not proceed to Task 6.** Report the result; the design needs a non-empty sentinel or a `ClearPayload` interface member, which is a spec change, not an implementation choice.

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs
git commit -m "verify an empty payload string replaces a prior Qdrant value"
```

---

### Task 2: Server schema plumbing for the marker

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`

**Interfaces:**
- Produces: proto field 23; `SchemaDescriptor.PopularitySignalColumn`. Consumed by Tasks 3, 4, 6.

- [ ] **Step 1: Add the proto field**

On `PropertyDescriptor`, after `chunk_contextual = 22`:
```proto
    bool   is_popularity_signal = 23;  // [IversonPopularitySignal] present — the interaction timestamp
```

- [ ] **Step 2: Add the descriptor member**

In `SchemaDescriptor`, beside the other defaulted members. Use a plain nullable string — **not** a collection — so the `MetadataColumns` case-comparer trap documented at `SchemaDescriptor.cs:72-76` cannot apply:
```csharp
    // Defaulted, not required: legacy _iverson_schema rows predate this marker and carry no such
    // key. Absent is benign — it means "this type has no interaction timestamp", which the write
    // path handles by writing an empty series.
    public string? PopularitySignalColumn { get; init; }
```

- [ ] **Step 3: Map the flag and reject duplicates in `SchemaBuilder`**

In the property loop that reads `prop.IsMetadata` (around `:96`), collect marked columns; after the loop, resolve to a single value and throw on more than one — matching the existing reserved-key throw style at `:134`:
```csharp
if (popularitySignalColumns.Count > 1)
    throw new ArgumentException(
        $"Properties {string.Join(", ", popularitySignalColumns.Select(n => $"'{n}'"))} all carry " +
        "[IversonPopularitySignal]. Exactly one property may mark the interaction timestamp.");
```
Set `PopularitySignalColumn = popularitySignalColumns.SingleOrDefault()` in the descriptor construction near `:226`.

- [ ] **Step 4: Tests**

In `SchemaBuilderTests.cs`: one marked property maps to `PopularitySignalColumn`; zero marked properties leaves it `null`; two marked properties throw with both names in the message.

- [ ] **Step 5: Build and test**
```bash
dotnet build Iverson.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter SchemaBuilder
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs
git commit -m "add is_popularity_signal to the schema descriptor and reject duplicate markers"
```

---

### Task 3: .NET attribute and registrar

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonPopularitySignalAttribute.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`

**Interfaces:**
- Consumes: Task 2's proto field.
- Produces: the reference implementation Task 4's four clients mirror.

- [ ] **Step 1: Create the attribute**

Match the file-per-attribute convention of the 16 existing files:
```csharp
namespace Iverson.Client.Attributes;

/// <summary>
/// Marks the UTC <see cref="DateTime"/> property recording when an interaction happened.
/// Exactly one property per entity may carry this; two fail schema registration.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class IversonPopularitySignalAttribute : Attribute;
```

- [ ] **Step 2: Set the flag in the registrar**

Beside the `descriptor.IsMetadata = true;` assignment at `SchemaRegistrar.cs:199`, following the same shape.

- [ ] **Step 3: Test** — an entity with the attribute sets `IsPopularitySignal` on the right property and leaves it false elsewhere.

- [ ] **Step 4: Build and test**
```bash
dotnet build Iverson.slnx
dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj --filter SchemaRegistrar
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/DotNet/
git commit -m "add IversonPopularitySignal attribute to the dotnet client"
```

---

### Task 4: The four remaining clients

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/core.ts` (**two** literal sites: `:376` and `:404`)
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java`
- Modify: `Iverson.Clients/Go/iverson/registrar.go`
- Test: the four corresponding test files

**Interfaces:**
- Consumes: Task 2's proto field, and Task 3's semantics as the reference.

- [ ] **Step 1: Regenerate stubs for the three scripted clients**
```bash
bash Iverson.Clients/TypeScript/scripts/generate_protos.sh
bash Iverson.Clients/Python/scripts/generate_protos.sh
bash Iverson.Clients/Go/scripts/generate_protos.sh
```
Java regenerates through Maven on build.

- [ ] **Step 2: Set the flag in each client, matching each one's existing shape**

Each client expresses markers differently — do not assume a uniform edit:
- **TypeScript** — `isPopularitySignal` in **both** `PropertyDescriptor` literals: the property path at `:376` (a real per-field value) and the all-defaults path at `:404` (`false`). Missing the second leaves a silently wrong descriptor on that path.
- **Python** — an `is_popularity_signal=(...)` keyword argument beside `is_metadata=` at `:280`.
- **Java** — `b.setIsPopularitySignal(true)` beside `:220`.
- **Go** — a new `fieldMeta` field plus its struct-tag parse, projected as `IsPopularitySignal: fm.PopularitySignal` beside `:142`.

- [ ] **Step 3: Tests** — one per client, mirroring Task 3's assertion.

- [ ] **Step 4: Run each suite**
```bash
cd Iverson.Clients/TypeScript && npm test && cd -
cd Iverson.Clients/Python && python3 -m pytest tests/ && cd -
cd Iverson.Clients/Java && mvn -q test && cd -
cd Iverson.Clients/Go && go test ./... && cd -
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/TypeScript/ Iverson.Clients/Python/ Iverson.Clients/Java/ Iverson.Clients/Go/
git commit -m "add the popularity-signal marker to the typescript, python, java and go clients"
```

---

### Task 5: The two new options

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs`

**Interfaces:**
- Produces: `RecencyBoost`, `RecencyHalfLifeDays`. Consumed by Task 7.

- [ ] **Step 1: Add the properties**
```csharp
    public double RecencyBoost        { get; set; } = 0.0;
    public double RecencyHalfLifeDays { get; set; } = 180.0;
```

- [ ] **Step 2: Validate in `AddPopularitySignalOptions`**, beside the existing `SaturationPoint` check:
```csharp
if (!double.IsFinite(opts.RecencyBoost) || opts.RecencyBoost < 0)
    throw new InvalidOperationException(
        $"{PopularitySignalOptions.Section}:RecencyBoost must be finite and non-negative " +
        $"(was {opts.RecencyBoost}).");

if (!double.IsFinite(opts.RecencyHalfLifeDays) || opts.RecencyHalfLifeDays <= 0 ||
    opts.RecencyHalfLifeDays > 300)
    throw new InvalidOperationException(
        $"{PopularitySignalOptions.Section}:RecencyHalfLifeDays must be finite and in (0, 300] " +
        $"(was {opts.RecencyHalfLifeDays}). The consumer stores 60 monthly buckets; a longer " +
        "half-life would silently lose tail contribution.");
```

- [ ] **Step 3: Tests** — defaults are `0.0` and `180.0`; negative boost, NaN, zero half-life, and `300.1` each throw; `300.0` is accepted.

- [ ] **Step 4: Build and test**
```bash
dotnet build Iverson.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter PopularitySignalOptions
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs
git commit -m "add RecencyBoost and RecencyHalfLifeDays options with validation"
```

---

### Task 6: Write path — the bucket series

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs`

**Interfaces:**
- Consumes: Task 2's `PopularitySignalColumn`; Task 1's verified answer.
- Produces: the `<relation>CountBuckets` payload key that Task 7 reads.

- [ ] **Step 1: Issue the histogram in its own `try`**

After the existing Count block and its `return`s, before the write. The separate `try` is load-bearing: the Count `catch` returns at `:81`, so sharing it would abort the whole update and leave the count stale.

```csharp
IReadOnlyList<AggregationBucket>? buckets = null;
if (childSchema.PopularitySignalColumn is { } tsColumn)
{
    try
    {
        var histSpec = new AggregationDescriptor(
            "buckets", AggregationKind.DateHistogram, tsColumn, CalendarInterval: "month");
        var hist = await search.AggregateAsync(
            SchemaBuilder.ToEngagementQuerySchema(childSchema), query, histSpec, authz: authzConstraints);
        buckets = hist?.Buckets;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex,
            "[PopularitySignal] histogram failed for parent={Parent} type={Type}; writing an empty series.",
            parentKey.SanitizeForLog(), parentSchema.TypeName.SanitizeForLog());
    }
}
```

Hoist the `authz` dictionary the Count call builds inline into a local so both aggregations share it.

- [ ] **Step 2: Encode, truncating to the most recent 60**

Buckets arrive ascending by key, so the tail is the most recent:
```csharp
var series = buckets is null
    ? ""
    : string.Join(";", buckets.TakeLast(60).Select(b => $"{b.Key}:{b.DocCount}"));
```

- [ ] **Step 3: Write both keys unconditionally**
```csharp
await vector.SetPayloadAsync(collection, pointId, new Dictionary<string, object>
{
    [fieldName]             = count,
    [fieldName + "Buckets"] = series
});
```
Never omit the series key: `SetPayload` merges, so an omission would leave a stale series beside a fresh count (spec SP8).

- [ ] **Step 4: Tests**

- **Fix the existing assertion** at `PopularitySignalConsumerTests.cs:163` — `p.Count == 1` becomes `p.Count == 2` with both keys asserted. It now fails on every path, not only the bucketed one.
- A child with a marked column writes the encoded series.
- A child with no marked column writes `""`.
- A histogram failure still writes the count, with `""` for the series.
- A 70-bucket result truncates to the 60 most recent, keeping the newest.

- [ ] **Step 5: Build and test**
```bash
dotnet build Iverson.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter PopularitySignal
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
git commit -m "write a monthly bucket series alongside the popularity count"
```

---

### Task 7: Read path — fuse the recency term

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/DecayFieldResolverTests.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 5's options; Task 6's payload key.

- [ ] **Step 1: Add the shared parse helper beside `ComputeDecay`**

Mirrors `ComputeDecay`'s curve and its future-clamp, and returns `0.0` — never a neutral value — for anything unparseable:
```csharp
/// <summary>
/// Sums a stored "yyyy-MM:count;…" series into a decayed count against <paramref name="now"/>,
/// using the same 0.5^(age/halfLife) curve as ComputeDecay. Bucket age is measured from the
/// bucket's START; the constant offset that introduces is absorbed by RecencyBoost and does not
/// affect ranking. Any malformed entry abandons the WHOLE series (returns 0.0) rather than
/// yielding a confidently wrong partial sum.
/// </summary>
internal static double ComputeRecencySum(string? series, DateTimeOffset now, double halfLifeDays)
{
    if (string.IsNullOrEmpty(series)) return 0.0;

    var sum = 0.0;
    foreach (var entry in series.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var sep = entry.IndexOf(':');
        if (sep <= 0) return 0.0;

        if (!DateTime.TryParseExact(
                entry[..sep], "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var bucketStart))
            return 0.0;

        if (!long.TryParse(entry[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            return 0.0;

        var ageDays = (now - new DateTimeOffset(bucketStart, TimeSpan.Zero)).TotalDays;
        sum += count * Math.Min(1.0, Math.Pow(0.5, ageDays / halfLifeDays));
    }
    return sum;
}
```

- [ ] **Step 2: Hoist `now` above the popularity call on the chunk path**

`ObjectSearchGrpcService.cs` computes `var now = DateTimeOffset.UtcNow;` at `:656`, after `RetrievePopularityOrDegradeAsync` is called at `:651`. Move the `now` declaration above that call.

- [ ] **Step 3: Fuse in both helpers**

Both take `now` and read the series key. `PopularityFor`:
```csharp
var series    = result.Payload.TryGetValue(fieldName + "Buckets", out var s) ? s : null;
var d         = DecayFieldResolver.ComputeRecencySum(series, now, options.RecencyHalfLifeDays);
var effective = count + options.RecencyBoost * d;
return effective / (effective + options.SaturationPoint);
```
`RetrievePopularityOrDegradeAsync` applies the same arithmetic per parent id. Both keep returning **null** when the count key is absent or unparseable — the floor needs `N`.

- [ ] **Step 4: Tests**

- `ComputeRecencySum`: empty/null → 0.0; a single current bucket → its count; a bucket one half-life old → half its count; a future bucket clamps to 1.0; malformed key, malformed count, and a missing colon each → 0.0 for the whole series.
- Fusion: `β = 0` yields exactly `N/(N+S)` for a non-empty series — the bit-exactness guarantee.
- Fusion: `β > 0` with a recent series ranks a document above an equal-`N` document with an ancient series.
- Both RPCs: a missing series key leaves ranking unchanged from today.

- [ ] **Step 5: Build and test**
```bash
dotnet build Iverson.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "DecayFieldResolver|ObjectSearch"
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/DecayFieldResolver.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/
git commit -m "fuse the decayed recency count into the popularity signal"
```

## Tasks NOT in this plan

Inherited verbatim from the spec's `Out of scope`. A new spec → plan cycle is required to add any of these.

- **A dedicated engagement ingestion endpoint.** Decided: its own spec, started next, independent of what the measurement gate returns. Nothing is blocked meanwhile — a deployment populates popularity today by `Post`-ing a child entity carrying `[IversonPopularitySignal]`. Note the throughput argument for such an endpoint is currently **unmeasured in both directions**: the only write-path data on disk is `count=32, concurrency=16` runs whose p95 is cold-start dominated (even `Tag`, with no embedding field, reports 1 ops/sec at p95 7.1s).
- **Weighted or typed interactions.** Buckets carry **counts**; a view weighs the same as a save. `DateHistogram` hardcodes `COUNT(*)` (`StarRocksQueryBuilder.cs:286-287`), so a bucket can never carry a weighted sum. Adding weights later needs a new aggregation kind or a `GroupBy`+`Sum` path in the StarRocks layer — a door this design closes, knowingly.
- **Calibrating β or the half-life.** Both ship chosen-not-measured, like `WPopularity` and `WDecay` before them. The measurement spec is the instrument that could calibrate them.
