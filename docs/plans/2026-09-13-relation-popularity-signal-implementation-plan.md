# Relation Popularity Signal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-13-relation-popularity-signal-design.md` (commit SHA: `23fddc07e3fb3b8f4023c08d64c1dde5f0e62bb5`)

**Goal:** Add a configurable, asynchronously-computed "popularity" signal (a relation row count, e.g. `UserArticle` rows per `Article`) that fuses into `SearchSimilar`/`SearchChunks` ranking exactly the way the existing `Decay` signal does.

**Architecture:** A new background consumer (`PopularitySignalConsumer`) recomputes a StarRocks `COUNT(*)` whenever a configured child relation type changes, and patches the raw count onto the parent's Qdrant object point via a new payload-only write. A periodic reconciliation worker provides a backstop against the inherent race between this consumer and the existing `EngagementStoreConsumer`. `ResultReranker` gains a fourth weighted signal, and `ObjectSearchGrpcService` wires it into both the object-vector and chunk search paths.

**Tech stack:** C# / .NET, Qdrant.Client 1.18.1, StarRocks via `IEngagementStoreSearchService`, xUnit + NSubstitute + FluentAssertions (inherited from spec's verified assumptions and this codebase's existing test conventions).

---

## File Structure

- **Create** `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs` — options binding + schema-dependent startup validator
- **Modify** `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` — add `WPopularity`
- **Modify** `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs` — validate `WPopularity`
- **Modify** `Iverson.Server/Iverson.Vector/IVectorRoles.cs` — add `SetPayloadAsync` to `IVectorWriteService`
- **Modify** `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs` — implement `SetPayloadAsync`
- **Modify** `Iverson.Server/Iverson.Vector/IResultReranker.cs` — add `Popularity` to `RerankCandidate`
- **Modify** `Iverson.Server/Iverson.Vector/ResultReranker.cs` — fuse the new signal
- **Create** `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` — event trigger + shared update logic (`PopularitySignalUpdater`)
- **Create** `Iverson.Server/Iverson.Api/Reconciliation/PopularitySignalReconciliationWorker.cs` — periodic sweep
- **Modify** `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — wire `Popularity` into both search paths
- **Modify** `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — thread the new constructor parameter through 7 existing call sites
- **Modify** `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs` — thread the new constructor parameter through 1 existing call site
- **Modify** `Iverson.Server/Iverson.Api/Program.cs` — registration + post-`LoadAsync` validation call
- **Test:** `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs`, `ResultRerankerTests.cs`, `QdrantVectorServiceTests.cs` (extended)
- **Test:** `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs` (new)
- **Test:** `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs` (new)
- **Test:** `Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs` (new)
- **Test:** `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs` (extended)

## Inherited from spec

Trusted as ground truth (thorough-brainstorming's 12 verified assumptions, unmodified by this plan):

- `SchemaRegistry.GetDependents` is scoped to document-template references — the config-driven relation resolution goes through `SchemaDescriptor.Relations` directly instead.
- `RelationDescriptor.ForeignKey` for a `OneToMany` relation names the FK column on the *child* type.
- A `Deleted` event's payload carries the FK value (pre-delete snapshot).
- `AggregateAsync`'s `authz: null` path is test-only; a real tenant-scoped `AuthorizationConstraint` is required.
- `IEngagementStoreSearchService`, `IEntityRepository`, `IVectorWriteService`, `IntelligenceTenantScope` are DI-Singleton.
- Qdrant.Client 1.18.1 exposes a native payload-only write (`SetPayloadAsync`).
- `RerankCandidate` has exactly 2 production construction sites (additive-only).
- `RelationKind` has exactly `{OneToOne, OneToMany, ManyToOne, ManyToMany}`.
- The `EntityEvent` envelope is shared across consumers.
- The child relation type must be `StoreTarget.Engagement`-eligible.
- The parent type must be `StoreTarget.Intelligence`-eligible.
- `IVectorQueryService.RetrievePayloadAsync(collectionName, ids)` exists as a batched payload-by-id fetch.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Ordering | `SchemaRegistry` is not populated until `schemaRegistry.LoadAsync()` runs, which happens *after* `builder.Build()` — so any validation needing live schema data cannot use the same pre-`Build()` `services.AddX(cfg)` pattern `DecayOptions`/`VectorRankingOptions` use. | `Program.cs:277` (`var app = builder.Build();`), `:417-418` (`var schemaRegistry = app.Services.GetRequiredService<SchemaRegistry>(); await schemaRegistry.LoadAsync();`) — both after `Build()`, under an unconditional (not role-gated) `// ── Schema hydration ──` section. |
| 2 | File path / pattern | The periodic reconciliation sweep has a direct precedent to mirror, contrary to the spec's narrative text ("no existing precedent") — that text is not one of the spec's 12 formally verified assumptions. | `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationQueueWorker.cs`: a `BackgroundService` running `while (!ct.IsCancellationRequested) { ...; await Task.Delay(PollInterval, ct); }` inside `ConsumerResilience.RunWithRestartAsync`. |
| 3 | Ordering | Background consumers/workers (`IntelligenceStoreConsumer`, `DocumentRerenderConsumer`, `ReconciliationQueueWorker`, etc.) are only registered as hosted services when `workloadRole == "worker"`; the new consumer and reconciliation worker must register in that same conditional block, not unconditionally. | `Program.cs:257-268`. |
| 4 | Signature | `RerankCandidate(ulong Id, double BaseScore, float[]? Centroid, double? Decay)` is a positional record with no default values on its 4 existing parameters; `ObjectSearchGrpcService.cs:652` constructs it positionally. Adding `Popularity` as a 5th parameter is safe only if it is trailing **and carries a default value** (`double? Popularity = null`). | `Iverson.Server/Iverson.Vector/IResultReranker.cs:3-7`; re-confirmed `ObjectSearchGrpcService.cs:652-654`'s positional 4-argument call matches the current record order exactly. |
| 5 | Signature | `IVectorWriteService` has no `SetPayloadAsync` today; `IntelligenceVectorService.UpsertAsync`/`UpsertNamedAsync` share a common shape (`Telemetry.Source.StartActivity` → build the Qdrant call → `activity?.SetStatus(Ok)`) and both reuse a private `ToQdrantValue(object)` helper for payload conversion — the new method mirrors this shape exactly. | `Iverson.Server/Iverson.Vector/IVectorRoles.cs:39-57`; `IntelligenceVectorService.cs:11-29` (`UpsertAsync` body, confirms `ToQdrantValue` usage). |
| 6 | Signature | The single-id overload `QdrantClient.SetPayloadAsync(string, IReadOnlyDictionary<string, Value>, ulong, ...)` exists (remaining params defaulted), taking Qdrant's own `Value` type, not raw `object` — the wrapper must convert via the existing `ToQdrantValue` helper. | `~/.nuget/packages/qdrant.client/1.18.1/lib/net6.0/Qdrant.Client.xml`, member `M:Qdrant.Client.QdrantClient.SetPayloadAsync(System.String,...,System.UInt64,...)`. |
| 7 | Validation pattern | `AddVectorRanking`'s existing validation for `WBase`/`WCentroid`/`WDecay` checks finiteness, non-negativity, and that the sum is `> 0` (guards `ResultReranker`'s weighted-mean division by zero) — `WPopularity` must be folded into all three checks, not validated separately, since `weightTotal` is their sum. | `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs:67-89` (read in full). |
| 8 | Signature | `AuthorizationConstraint(IReadOnlySet<string>? AllowedFields, string? OwnerColumn, string? OwnerValue, string? TenantColumn = null, string? TenantValue = null)` — field names/order match the spec's own usage exactly; no correction needed. | `Iverson.Server/Iverson.StarRocks/AuthorizationConstraint.cs:3-8`. |
| 9 | Signature | `SchemaDescriptor.TenantColumn` is `public required string TenantColumn { get; init; }` (non-nullable, always present). | `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs:67`. |
| 10 | Signature | `EntityEvent(EntityEventType, string TypeName, string Key, string PayloadJson, string TraceId, string SchemaVersion, DateTimeOffset OccurredAt, StoreTarget TargetStores = All, string? PriorPayloadJson = null, bool SuppressRerenderCascade = false)`. | `Iverson.Server/Iverson.Events/EntityEvent.cs:20-30`. |
| 11 | Signature | `IEventConsumer.ConsumeAsync(string topic, string groupId, Func<string,string,CancellationToken,Task> handler, CancellationToken ct)` — the dispatch handler shape to implement. | `Iverson.Server/Iverson.Events/IEventConsumer.cs:7-11`. |
| 12 | Signature | `ConsumerResilience.RunWithRestartAsync(Func<Task> runConsumers, ILogger logger, string label, CancellationToken ct, TimeSpan? restartDelay = null)`. | `Iverson.Server/Iverson.Api/Consumers/ConsumerResilience.cs:12-18`. |
| 13 | Signature | `IEntityRepository.FetchKeysAndTenantsPagedAsync(TableSchema schema, string? afterKey, int pageSize)` returns `Task<IEnumerable<KeyedTenantRow>>`, `KeyedTenantRow(string Key, string? TenantId)` — the mechanism for the reconciliation worker to enumerate every known parent, paged, across tenants. | `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:67`; `KeyedRow.cs:5`. |
| 14 | Signature | `AggregationDescriptor(string Name, AggregationKind Kind, string Field, int Size = 10, ...)`, `AggregationKind.Count` exists, and `AggregationResult(string Name, AggregationKind Kind, IReadOnlyList<AggregationBucket>? Buckets = null, double? MetricValue = null)`. | `Iverson.Server/Iverson.StarRocks/Aggregation.cs:15-32`. |
| 15 | Code validity | `COUNT(*)` requires `Field` to be an **empty string**, not `"*"` — `StarRocksQueryBuilder` treats `Field: ""` + `Expression: null` as the count-all case; a non-empty `Field` would instead COUNT that column. | `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs:257-259` (`isCountAll = spec.Kind == AggregationKind.Count && string.IsNullOrEmpty(spec.Field) && string.IsNullOrEmpty(spec.Expression)`). |
| 16 | Code validity | `SearchQuery`/`SearchClause`/`SearchValue` (the same proto types `AggregateAsync`'s `query` parameter takes) are constructible directly via C# object initializers in-process, with no RPC round-trip. | `Iverson.Server/Iverson.ClientConformance/Scenarios/QueryScenario.cs:347-361` (existing non-generated construction site). |
| 17 | Code validity | `relation.ForeignKey` is stored in **raw SQL column-name casing** (e.g. `"ArticleId"`, not camelCase `"articleId"`) — confirmed by its two existing consumers: `DocumentRerenderConsumer.EnqueueByColumnAsync` passes it directly as a Postgres column name, and `EnqueueOneToManyParentsAsync`'s `ExtractString(payload, relation.ForeignKey)` reads it directly as a JSON property name from `ev.PayloadJson` (the *authoritative Postgres row* shape, not the camelCased Qdrant-payload shape `IntelligenceStoreConsumer` produces separately). The new consumer must use `relation.ForeignKey` verbatim in both the StarRocks filter clause and the JSON payload extraction — never `.ToCamelCase()` it. | `Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs:151-163` (`EnqueueByColumnAsync`, `foreignKey` passed straight to `FetchByColumnAsync`), `:179-193` (`EnqueueOneToManyParentsAsync`, `ExtractString(payload, relation.ForeignKey)`). |
| 18 | Consumer impact (Cat. 6 — this plan modifies existing files) | `SchemaBuilder.ToEngagementQuerySchema` is `internal static` in `Iverson.Api.Schema` — reachable from `Iverson.Api.Consumers` (same assembly) with no visibility change. | `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:299`. |
| 19 | Consumer impact (Cat. 6) | `EngagementNotReadyException`/`EngagementStoreDisabledException` are `public` in `Iverson.StarRocks`, reachable from `Iverson.Api`. Since startup validation (Task 1) already guarantees `Engagement__Enabled=true` and the child type is Engagement-eligible before this consumer ever runs, `EngagementStoreDisabledException` is not expected at runtime; `EngagementNotReadyException` (a transient StarRocks-readiness state) *can* still occur and is handled by the broad per-parent-id isolation catch (Task 4), mirroring `DocumentRerenderConsumer`'s existing "isolate per-dependent, log, and keep going" pattern rather than a narrow dedicated catch. | `Iverson.Server/Iverson.StarRocks/EngagementNotReadyException.cs:3`, `EngagementStoreDisabledException.cs:3`; `DocumentRerenderConsumer.cs:80-127` (the per-dependent `try`/`catch (Exception ex)` isolation this plan mirrors). |
| 20 | Test convention | Consumer/service tests use xUnit + NSubstitute + FluentAssertions, with dependencies injected via primary constructors and mocked with `Substitute.For<T>()`. | `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs:1-30`. |
| 21 | Sibling-set sweep (options binding vs. schema-dependent validation) | Two existing options-registration idioms coexist in this codebase: (a) validate-then-`Options.Create()` singleton (`DecayOptions`, `VectorRankingOptions` — self-contained, no external state needed) and (b) plain `services.Configure<T>(section)` with no validation (`DocumentRerenderOptions`, `EngagementStoreOptions`). Neither fits `PopularitySignalOptions` alone: its numeric field (`SaturationPoint`) fits (a); its relation fields need `SchemaRegistry`, which isn't available until after (a)'s registration point. Resolved by splitting: bind + validate `SaturationPoint` via pattern (a) at the normal pre-`Build()` point; resolve/validate the relation entries via a separate static method called explicitly after `LoadAsync()` (see assumption #1). No new third options-pattern is introduced. | Re-read of `DecayOptions.cs` and `Program.cs:239,265-266` (the `Configure<T>` sites) confirms both existing idioms; this plan does not add a third. |
| 22 | Consumer impact (Cat. 6) | `ObjectSearchGrpcService`'s own constructor — distinct from `RerankCandidate` (assumption #4) — has 8 existing direct-construction call sites across 2 test files, none in Task 6's original file list; every one already threads `Options.Create(new DecayOptions())` as its trailing (12th) positional argument, so appending a 13th (`Options.Create(new PopularitySignalOptions())`) is a mechanical, already-precedented edit at each site. Added 2026-09-13 per critical-implementation-review round 1, finding 2.3. | `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs:74,2613,2686,2740,2759,3956,4152`; `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs:332`; `ObjectSearchGrpcServiceTests.cs:74-80` read in full, confirming the `Options.Create(new DecayOptions())`-trailing shape. |

## Tasks

### Task 1: Configuration — `PopularitySignalOptions`, `WPopularity`, and startup validation

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs`
- Modify: `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs`
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs`

**Interfaces:**
- Produces: `PopularitySignalOptions` (bound), `PopularitySignalEntry(string ParentType, string Relation)`, `PopularitySignalValidator.ValidateAtStartup(...)`, `VectorRankingOptions.WPopularity` — consumed by Tasks 3, 4, 5, 6.

- [ ] **Step 1: Add `PopularitySignalOptions.cs`**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Iverson.Api.Schema;
using Iverson.StarRocks;

namespace Iverson.Api.Grpc;

public sealed record PopularitySignalEntry(string ParentType, string Relation);

public sealed class PopularitySignalOptions
{
    public const string Section = "PopularitySignal";
    public List<PopularitySignalEntry> Signals { get; set; } = [];
    public double SaturationPoint { get; set; } = 50;
}

public static class PopularitySignalOptionsExtensions
{
    // Binds and validates only the self-contained field (SaturationPoint). The relation entries
    // need SchemaRegistry, which is not populated until after builder.Build() + LoadAsync() —
    // see ValidateAtStartup below, called separately from Program.cs.
    public static IServiceCollection AddPopularitySignalOptions(
        this IServiceCollection services, IConfiguration config)
    {
        var opts = new PopularitySignalOptions();
        config.GetSection(PopularitySignalOptions.Section).Bind(opts);

        if (!double.IsFinite(opts.SaturationPoint) || opts.SaturationPoint <= 0)
            throw new InvalidOperationException(
                $"{PopularitySignalOptions.Section}:SaturationPoint must be finite and greater than " +
                $"zero (was {opts.SaturationPoint}).");

        services.AddSingleton(Options.Create(opts));
        return services;
    }
}

internal static class PopularitySignalValidator
{
    /// <summary>
    /// Called once at startup, after SchemaRegistry.LoadAsync() — the four checks the design
    /// mandates, all fail-fast. A misconfigured entry throws InvalidOperationException.
    /// </summary>
    internal static void ValidateAtStartup(
        PopularitySignalOptions options, SchemaRegistry registry, bool engagementEnabled)
    {
        foreach (var signal in options.Signals)
        {
            if (!engagementEnabled)
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: Signals is non-empty but " +
                    $"{EngagementStoreOptions.Section}:Enabled is false — this feature has no " +
                    "runtime degrade path for a disabled engagement store.");

            var parentSchema = registry.Get(signal.ParentType)
                ?? throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' is not a " +
                    "registered schema.");

            if (!StoreTargeting.HasVectorOrChunkFields(parentSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' has no " +
                    "vector or chunk fields, so it has no Qdrant point to ever patch.");

            var relation = parentSchema.Relations.FirstOrDefault(r =>
                string.Equals(r.PropertyName, signal.Relation, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: '{signal.Relation}' is not a relation on " +
                    $"'{signal.ParentType}'.");

            if (relation.Kind != RelationKind.OneToMany)
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: relation '{signal.Relation}' on " +
                    $"'{signal.ParentType}' is {relation.Kind}, not OneToMany — a count is only " +
                    "meaningful for a one-to-many relation.");

            var childSchema = registry.Get(relation.RelatedTypeName)
                ?? throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: related type '{relation.RelatedTypeName}' " +
                    $"(from relation '{signal.Relation}') is not a registered schema.");

            if (!StoreTargeting.IsEngagementEligible(childSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: child type '{relation.RelatedTypeName}' is " +
                    "not StarRocks-eligible (it declares a OneToMany relation of its own), so its " +
                    "count can never be computed.");
        }
    }
}
```

- [ ] **Step 2: Add `WPopularity` to `VectorRankingOptions`**

In `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs`, add alongside `WDecay`:
```csharp
public double WPopularity { get; set; } = 0.0;
```

- [ ] **Step 3: Fold `WPopularity` into `AddVectorRanking`'s validation**

In `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`, extend the three existing checks (finiteness, non-negativity, sum `> 0`) to include `WPopularity`:
```csharp
if (!double.IsFinite(opts.WBase) || !double.IsFinite(opts.WCentroid) ||
    !double.IsFinite(opts.WDecay) || !double.IsFinite(opts.WPopularity) ||
    !double.IsFinite(opts.LambdaSimilar) || !double.IsFinite(opts.LambdaChunks))
    throw new InvalidOperationException(
        $"{VectorRankingOptions.Section}: every value must be finite " +
        $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, " +
        $"WPopularity={opts.WPopularity}, LambdaSimilar={opts.LambdaSimilar}, " +
        $"LambdaChunks={opts.LambdaChunks}).");

if (opts.WBase < 0 || opts.WCentroid < 0 || opts.WDecay < 0 || opts.WPopularity < 0)
    throw new InvalidOperationException(
        $"{VectorRankingOptions.Section}: weights must be non-negative " +
        $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, " +
        $"WPopularity={opts.WPopularity}).");

if (opts.WBase + opts.WCentroid + opts.WDecay + opts.WPopularity <= 0)
    throw new InvalidOperationException(
        $"{VectorRankingOptions.Section}: at least one weight must be greater than zero; " +
        "all-zero weights make every fused score NaN.");
```

- [ ] **Step 4: Wire into `Program.cs`**

Near the existing `AddDecayOptions` call (`:189`):
```csharp
builder.Services.AddPopularitySignalOptions(cfg);
```

Immediately after `await schemaRegistry.LoadAsync();` (`:418`):
```csharp
PopularitySignalValidator.ValidateAtStartup(
    app.Services.GetRequiredService<IOptions<PopularitySignalOptions>>().Value,
    schemaRegistry,
    cfg.GetValue($"{EngagementStoreOptions.Section}:Enabled", true));
```

- [ ] **Step 5: Tests**

`VectorRankingOptionsTests.cs`: extend existing validation tests with `WPopularity` cases (negative, non-finite, all-four-zero) matching the existing `WBase`/`WCentroid`/`WDecay` test shapes.

`PopularitySignalOptionsTests.cs` (new): one test per `ValidateAtStartup` fail-fast branch (unregistered `ParentType`, non-vector/chunk `ParentType`, unknown relation, non-`OneToMany` relation, unregistered `RelatedTypeName`, non-Engagement-eligible child, `Signals` non-empty with `engagementEnabled: false`), plus one passing case using a `SchemaRegistry` populated with `Article`/`UserArticle`-shaped fixtures (mirror `SchemaFixtures.cs`'s existing convention).

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs \
        Iverson.Server/Iverson.Vector/VectorRankingOptions.cs \
        Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs \
        Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs
git commit -m "add PopularitySignalOptions and WPopularity with startup validation"
```

---

### Task 2: `IVectorWriteService.SetPayloadAsync`

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IVectorRoles.cs`
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs`

**Interfaces:**
- Produces: `IVectorWriteService.SetPayloadAsync` — consumed by Task 4.

- [ ] **Step 1: Add to `IVectorWriteService`**

In `IVectorRoles.cs`, alongside `UpdateNamedVectorsAsync`:
```csharp
Task SetPayloadAsync(string collectionName, ulong id, IReadOnlyDictionary<string, object> payload);
```

- [ ] **Step 2: Implement in `IntelligenceVectorService`**

Mirrors `UpsertAsync`'s shape exactly (telemetry activity, existing `ToQdrantValue` helper), but calls the native payload-only RPC instead of an upsert:
```csharp
public async Task SetPayloadAsync(
    string collectionName, ulong id, IReadOnlyDictionary<string, object> payload)
{
    using var activity = Telemetry.Source.StartActivity("qdrant.set_payload", ActivityKind.Client);
    activity?.SetTag("db.system", "qdrant");
    activity?.SetTag("qdrant.collection", collectionName);
    activity?.SetTag("qdrant.point_id", id);

    var qdrantPayload = payload.ToDictionary(kv => kv.Key, kv => ToQdrantValue(kv.Value));
    await client.SetPayloadAsync(collectionName, qdrantPayload, id);
    activity?.SetStatus(ActivityStatusCode.Ok);
}
```

- [ ] **Step 3: Tests**

`QdrantVectorServiceTests.cs`: extend with a real-container test (matching this file's existing Testcontainer convention, no skip attributes — per [[project-derived-vector-signals]]'s precedent that a mocked write-service test is not evidence of Qdrant's real behavior): upsert a point, call `SetPayloadAsync` with a new field, then read the payload back via `RetrievePayloadAsync` and assert the new field is present *and* the point's existing vector/payload fields are untouched (the property `UpsertAsync` does NOT have — this is the whole reason for the new method).

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Vector/IVectorRoles.cs \
        Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs \
        Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs
git commit -m "add IVectorWriteService.SetPayloadAsync"
```

---

### Task 3: Fusion — `RerankCandidate.Popularity` and `ResultReranker`

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IResultReranker.cs`
- Modify: `Iverson.Server/Iverson.Vector/ResultReranker.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs`

**Interfaces:**
- Consumes: `VectorRankingOptions.WPopularity` (Task 1).
- Produces: `RerankCandidate.Popularity` — consumed by Task 6.

- [ ] **Step 1: Add `Popularity` to `RerankCandidate`**

Trailing, defaulted — per Verified plan-level assumption #4:
```csharp
public sealed record RerankCandidate(
    ulong    Id,
    double   BaseScore,
    float[]? Centroid,
    double?  Decay,
    double?  Popularity = null);
```

- [ ] **Step 2: Fuse in `ResultReranker.Rerank`**

Extend the identity short-circuit and the weighted-mean branch symmetrically with the existing `hasCentroid`/`hasDecay` pattern:
```csharp
var hasCentroid   = candidate.Centroid is not null && candidate.Centroid.Length == queryVector.Length;
var hasDecay      = candidate.Decay is not null;
var hasPopularity = candidate.Popularity is not null;

double fusedScore;
if (!hasCentroid && !hasDecay && !hasPopularity)
{
    fusedScore = candidate.BaseScore;
}
else
{
    var weightedSum = _o.WBase * candidate.BaseScore;
    var weightTotal = _o.WBase;

    if (hasCentroid)
    {
        var centroidSimilarity = TensorPrimitives.CosineSimilarity(queryVector, candidate.Centroid!);
        weightedSum += _o.WCentroid * centroidSimilarity;
        weightTotal += _o.WCentroid;
    }

    if (hasDecay)
    {
        weightedSum += _o.WDecay * candidate.Decay!.Value;
        weightTotal += _o.WDecay;
    }

    if (hasPopularity)
    {
        weightedSum += _o.WPopularity * candidate.Popularity!.Value;
        weightTotal += _o.WPopularity;
    }

    fusedScore = weightedSum / weightTotal;
}
```

- [ ] **Step 3: Tests**

`ResultRerankerTests.cs`: extend with `Popularity`-only cases mirroring the existing `Decay`-only tests exactly (identity short-circuit still fires when all three are absent; weighted-mean includes `WPopularity * popularity` when present; all-three-present case). Existing tests must stay green unmodified — `Popularity` defaults to `null` everywhere they don't specify it.

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Vector/IResultReranker.cs \
        Iverson.Server/Iverson.Vector/ResultReranker.cs \
        Iverson.Server/Iverson.Vector.Tests/ResultRerankerTests.cs
git commit -m "fuse Popularity as a fourth ResultReranker signal"
```

---

### Task 4: `PopularitySignalConsumer` and the shared update logic

**Files:**
- Create: `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs`

**Interfaces:**
- Consumes: `PopularitySignalOptions` (Task 1), `IVectorWriteService.SetPayloadAsync` (Task 2).
- Produces: `PopularitySignalUpdater.UpdateAsync` — consumed by Task 5.

- [ ] **Step 1: `PopularitySignalUpdater` — the shared recompute-and-write logic**

One method both this consumer and Task 5's reconciliation worker call. Field naming (`<relation>Count` camelCased) and the two documented degrade cases (null `AggregateAsync` result; Qdrant `NotFound`) per the spec:

```csharp
internal sealed class PopularitySignalUpdater(
    IEngagementStoreSearchService search,
    IVectorWriteService vector,
    IntelligenceTenantScope tenantScope,
    ILogger<PopularitySignalUpdater> logger)
{
    internal async Task UpdateAsync(
        SchemaDescriptor parentSchema, PopularitySignalEntry signal, SchemaDescriptor childSchema,
        RelationDescriptor relation, string parentKey, string? tenantId)
    {
        var query = new SearchQuery
        {
            Clauses =
            {
                new SearchClause
                {
                    Property   = relation.ForeignKey, // raw column-name casing — see plan assumption #17
                    Operator   = SearchOperator.Equals,
                    ClauseType = SearchClauseType.Filter,
                    Value      = new SearchValue { StringVal = parentKey }
                }
            }
        };
        var spec = new AggregationDescriptor("count", AggregationKind.Count, Field: "");

        AggregationResult? result;
        try
        {
            result = await search.AggregateAsync(
                SchemaBuilder.ToEngagementQuerySchema(childSchema),
                query,
                spec,
                authz: new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
                {
                    [childSchema.TypeName] = new AuthorizationConstraint(
                        AllowedFields: null, OwnerColumn: null, OwnerValue: null,
                        TenantColumn: childSchema.TenantColumn, TenantValue: tenantId)
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "[PopularitySignal] AggregateAsync failed for parent={Parent} type={Type} relation={Relation}; skipping.",
                parentKey.SanitizeForLog(), parentSchema.TypeName.SanitizeForLog(), signal.Relation.SanitizeForLog());
            return;
        }

        if (result is null)
        {
            // AggregateAsync's tenant-scoped branch's own graceful outcome for an unprovisioned
            // tenant/table — exactly what the cross-consumer race can produce. Skip; a later event
            // or the reconciliation sweep will retry. See CDR round 2, finding 2.1.
            logger.LogInformation(
                "[PopularitySignal] AggregateAsync returned null for parent={Parent} type={Type}; skipping this update.",
                parentKey.SanitizeForLog(), parentSchema.TypeName.SanitizeForLog());
            return;
        }

        var count      = (long)(result.MetricValue ?? 0.0);
        var collection = tenantScope.ResolveCollectionName(parentSchema.CollectionName!, tenantId, isChunks: false);
        var pointId    = IntelligenceStoreConsumer.KeyToUlong(parentKey);
        var fieldName  = signal.Relation.ToCamelCase() + "Count";

        try
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: false)))
                await vector.SetPayloadAsync(collection, pointId,
                    new Dictionary<string, object> { [fieldName] = count });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // The parent's Qdrant point doesn't exist yet — degrade, don't retry (spec §2).
            logger.LogInformation(
                "[PopularitySignal] SetPayloadAsync NotFound for parent={Parent} collection={Collection}; skipping.",
                parentKey.SanitizeForLog(), collection.SanitizeForLog());
        }
    }
}
```

- [ ] **Step 2: `PopularitySignalConsumer` — event parsing and trigger**

Structured on `DocumentRerenderConsumer`'s exact pattern for `RelationKind.OneToMany` FK extraction (current + prior payload, old/new parent on reassignment) and its Created/Updated-vs-Deleted tenant resolution split — but keyed off `PopularitySignalOptions.Signals` (via `SchemaDescriptor.Relations`, per Verified Assumption #1) instead of `SchemaRegistry.GetDependents`:

```csharp
public sealed class PopularitySignalConsumer(
    IEventConsumer consumer,
    SchemaRegistry registry,
    IEntityRepository entities,
    IOptions<PopularitySignalOptions> options,
    PopularitySignalUpdater updater,
    ILogger<PopularitySignalConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.popularity-signal";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => consumer.ConsumeAsync(EntityTopics.Events, GroupId, DispatchAsync, ct),
            logger, "PopularitySignal", ct);

    internal async Task DispatchAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);

        // Which configured signal(s) does this event's type feed, as the CHILD side?
        var matches = options.Value.Signals
            .Select(s => (Signal: s, ParentSchema: registry.Get(s.ParentType)))
            .Where(m => m.ParentSchema is not null)
            .Select(m => (m.Signal, ParentSchema: m.ParentSchema!,
                Relation: m.ParentSchema!.Relations.FirstOrDefault(r =>
                    string.Equals(r.PropertyName, m.Signal.Relation, StringComparison.OrdinalIgnoreCase))))
            .Where(m => m.Relation is not null &&
                string.Equals(m.Relation!.RelatedTypeName, ev.TypeName, StringComparison.OrdinalIgnoreCase))
            .Select(m => (m.Signal, m.ParentSchema, Relation: m.Relation!)) // null-forgive once, post-filter, so `relation` below is non-nullable
            .ToList();

        if (matches.Count == 0) return;

        var childSchema = registry.Get(ev.TypeName);
        if (childSchema is null) return;

        var tenantId = await ResolveTenantIdAsync(ev, childSchema, ct);
        if (tenantId is null) return; // mirrors DocumentRerenderConsumer.cs:68 — an unresolvable
                                       // tenant must never reach a StarRocks-bound call downstream

        using var payloadDoc = JsonDocument.Parse(ev.PayloadJson);
        var payload = payloadDoc.RootElement;

        JsonElement? priorPayload = null;
        if (ev.PriorPayloadJson is not null)
        {
            using var priorDoc = JsonDocument.Parse(ev.PriorPayloadJson);
            priorPayload = priorDoc.RootElement.Clone();
        }

        foreach (var (signal, parentSchema, relation) in matches)
        {
            try
            {
                var newParentKey = ExtractString(payload, relation.ForeignKey);
                if (newParentKey is not null)
                    await updater.UpdateAsync(parentSchema, signal, childSchema, relation, newParentKey, tenantId);

                if (priorPayload is not null)
                {
                    var oldParentKey = ExtractString(priorPayload.Value, relation.ForeignKey);
                    if (oldParentKey is not null && oldParentKey != newParentKey)
                        await updater.UpdateAsync(parentSchema, signal, childSchema, relation, oldParentKey, tenantId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[PopularitySignal] Failed to update parent for signal={Signal} type={Type} key={Key} — skipping.",
                    signal.Relation.SanitizeForLog(), ev.TypeName.SanitizeForLog(), ev.Key.SanitizeForLog());
            }
        }
    }

    // ResolveTenantIdAsync / Deserialize / ExtractString: identical shape to
    // DocumentRerenderConsumer's private helpers of the same name (Created/Updated resolve from
    // the authoritative Postgres row; Deleted resolves from the pre-delete payload snapshot).
}
```

**Note for the implementing task:** `ResolveTenantIdAsync(EntityEvent ev, SchemaDescriptor changedSchema, CancellationToken ct)`, `Deserialize(string key, string value)`, and `ExtractString(JsonElement payload, string propertyName)` are private helpers in `DocumentRerenderConsumer` (`DocumentRerenderConsumer.cs:131-149,195,213`; not currently shared/extracted). Per this codebase's existing convention (each consumer owns its private copies — `IntelligenceStoreConsumer` and `DocumentRerenderConsumer` do not share these either), duplicate the same three private methods here, with identical signatures and bodies, rather than extracting a shared base class; introducing a new shared abstraction across consumer classes is out of scope for this plan (YAGNI — no third caller exists yet).

- [ ] **Step 3: DI registration in `Program.cs`**

Inside the existing `if (workloadRole == "worker")` block (`:257-268`), alongside `DocumentRerenderConsumer`:
```csharp
builder.Services.AddSingleton<Iverson.Api.Consumers.PopularitySignalUpdater>();
builder.Services.AddHostedService<Iverson.Api.Consumers.PopularitySignalConsumer>();
```

- [ ] **Step 4: Tests**

`PopularitySignalConsumerTests.cs`, mirroring `EnrichmentConsumerTests.cs`'s/`DocumentRerenderConsumerTests.cs`'s NSubstitute-based conventions: a `Created`/`Updated`/`Deleted` event on a configured child type triggers `IEngagementStoreSearchService.AggregateAsync` with a tenant-scoped `AuthorizationConstraint` (not `null`) and the correct `ForeignKey`-filtered query; an `Updated` event with a changed FK triggers two updates (old + new parent); a `null` `AggregateAsync` result and a Qdrant `NotFound` each skip without throwing; an unresolvable tenant (mirroring `DocumentRerenderConsumerTests.cs`'s existing null-tenant test cases) skips without calling `UpdateAsync`; an unrelated event type is a no-op.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs \
        Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs
git commit -m "add PopularitySignalConsumer"
```

---

### Task 5: `PopularitySignalReconciliationWorker`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Reconciliation/PopularitySignalReconciliationWorker.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs`

**Interfaces:**
- Consumes: `PopularitySignalOptions` (Task 1), `PopularitySignalUpdater` (Task 4).

- [ ] **Step 1: The worker**

Mirrors `ReconciliationQueueWorker`'s poll-loop shape exactly (Verified plan-level assumption #2); enumerates every known parent for each configured signal via `IEntityRepository.FetchKeysAndTenantsPagedAsync` (paged, tenant-aware):

```csharp
internal sealed class PopularitySignalReconciliationWorker(
    IOptions<PopularitySignalOptions> options,
    SchemaRegistry registry,
    IEntityRepository entities,
    PopularitySignalUpdater updater,
    ILogger<PopularitySignalReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private const int PageSize = 500;

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(() => SweepLoopAsync(ct), logger, "PopularitySignalReconciliation", ct);

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var signal in options.Value.Signals)
                await SweepSignalAsync(signal, ct);

            await Task.Delay(SweepInterval, ct);
        }
    }

    private async Task SweepSignalAsync(PopularitySignalEntry signal, CancellationToken ct)
    {
        var parentSchema = registry.Get(signal.ParentType);
        var relation = parentSchema?.Relations.FirstOrDefault(r =>
            string.Equals(r.PropertyName, signal.Relation, StringComparison.OrdinalIgnoreCase));
        var childSchema = relation is not null ? registry.Get(relation.RelatedTypeName) : null;
        if (parentSchema is null || relation is null || childSchema is null) return; // schema unregistered mid-run; skip this sweep

        string? afterKey = null;
        while (!ct.IsCancellationRequested)
        {
            var page = (await entities.FetchKeysAndTenantsPagedAsync(
                SchemaBuilder.ToTableSchema(parentSchema), afterKey, PageSize)).ToList();
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                if (row.TenantId is null) continue; // same principle as PopularitySignalConsumer's
                                                      // tenant guard — never reach a StarRocks-bound
                                                      // call with an unresolvable tenant
                try
                {
                    await updater.UpdateAsync(parentSchema, signal, childSchema, relation, row.Key, row.TenantId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "[PopularitySignalReconciliation] Failed to update parent={Parent} for signal={Signal} — skipping.",
                        row.Key.SanitizeForLog(), signal.Relation.SanitizeForLog());
                }
            }

            afterKey = page[^1].Key;
        }
    }
}
```

- [ ] **Step 2: DI registration in `Program.cs`**

Same worker-role block as Task 4:
```csharp
builder.Services.AddHostedService<Iverson.Api.Reconciliation.PopularitySignalReconciliationWorker>();
```

- [ ] **Step 3: Tests**

`PopularitySignalReconciliationWorkerTests.cs`: given a configured signal and a mocked `IEntityRepository` returning two pages of parents, asserts `PopularitySignalUpdater.UpdateAsync` is called once per parent across both pages; asserts a schema-unregistered signal is skipped without throwing; asserts a row with a `null` `TenantId` is skipped without calling `UpdateAsync`; asserts one parent's `UpdateAsync` throwing does not stop the sweep for the remaining parents (mirrors Task 4's per-item isolation).

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Api/Reconciliation/PopularitySignalReconciliationWorker.cs \
        Iverson.Server/Iverson.Api/Program.cs \
        Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs
git commit -m "add PopularitySignalReconciliationWorker"
```

---

### Task 6: `ObjectSearchGrpcService` wiring

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`

**Interfaces:**
- Consumes: `PopularitySignalOptions` (Task 1), `RerankCandidate.Popularity` (Task 3).

- [ ] **Step 1: Inject `IOptions<PopularitySignalOptions>`**

Add to `ObjectSearchGrpcService`'s primary constructor parameter list, stored as `_popularitySignal = popularitySignalOptions.Value` (matches the existing `_decayOptions`/`_ranking` field pattern). This is a new *required* parameter with no default, so it breaks every existing direct `new ObjectSearchGrpcService(...)` call — update each of the 8 sites below by appending `Options.Create(new PopularitySignalOptions())` as a 13th positional argument, mirroring how `Options.Create(new DecayOptions())` is already the trailing argument at every one of them:
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs:74,2613,2686,2740,2759,3956,4152`
- `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs:332`

- [ ] **Step 2: Object-vector path (`SearchSimilar`) — sourcing + fetch-limit gate**

In the `SearchSimilar` object-vector path, alongside the existing `centroidPossible`/`decayField` computation (`:234-244`):
```csharp
var popularityPossible = _popularitySignal.Signals.Any(s =>
    string.Equals(s.ParentType, schema.TypeName, StringComparison.OrdinalIgnoreCase));

var rerankIsIdentity = !centroidPossible && decayField is null && !popularityPossible;
```

In the `RerankCandidate` construction (`:278-282`), read the raw count from the object point's own payload the same way `Decay` already is:
```csharp
var candidates = results.Select(r => new RerankCandidate(
    Id:         r.Id,
    BaseScore:  r.Score,
    Centroid:   centroids.TryGetValue(r.Id, out var centroid) ? centroid : null,
    Decay:      DecayFor(r, decayField, now, _decayOptions.HalfLifeDays),
    Popularity: PopularityFor(schema, r, _popularitySignal))).ToList();
```
with a small static helper mirroring `DecayFor`'s shape:
```csharp
private static double? PopularityFor(SchemaDescriptor schema, VectorSearchResult result, PopularitySignalOptions options)
{
    var signal = options.Signals.FirstOrDefault(s =>
        string.Equals(s.ParentType, schema.TypeName, StringComparison.OrdinalIgnoreCase));
    if (signal is null) return null;

    var fieldName = signal.Relation.ToCamelCase() + "Count";
    if (!result.Payload.TryGetValue(fieldName, out var stored) || !long.TryParse(stored, out var count))
        return null;

    return count / (count + options.SaturationPoint);
}
```

- [ ] **Step 3: Chunk path (`SearchChunksFusedAsync`) — batched lookup**

Mirrors the existing Centroid batched-by-distinct-parent-id lookup (`:629-641`). After computing `parentIds` (already collected for the Centroid fetch), add a batched popularity fetch against the object collection using `IVectorQueryService.RetrievePayloadAsync`:
```csharp
var popularities = await RetrievePopularityOrDegradeAsync(
    tenantScope.ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks: false),
    parentIds, schema, _popularitySignal, rpcName);
```
```csharp
private async Task<IReadOnlyDictionary<ulong, double>> RetrievePopularityOrDegradeAsync(
    string collection, IReadOnlyList<ulong> parentIds, SchemaDescriptor schema,
    PopularitySignalOptions options, string rpcName)
{
    var signal = options.Signals.FirstOrDefault(s =>
        string.Equals(s.ParentType, schema.TypeName, StringComparison.OrdinalIgnoreCase));
    if (signal is null || parentIds.Count == 0)
        return new Dictionary<ulong, double>();

    var fieldName = signal.Relation.ToCamelCase() + "Count";
    IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> payloads;
    try
    {
        using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: true)))
            payloads = await vector.RetrievePayloadAsync(collection, parentIds);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex,
            "[{Rpc}] popularity retrieve failed (collection={Collection}); re-ranking without it.",
            rpcName, collection.SanitizeForLog());
        return new Dictionary<ulong, double>();
    }

    var result = new Dictionary<ulong, double>();
    foreach (var (id, payload) in payloads)
        if (payload.TryGetValue(fieldName, out var stored) && long.TryParse(stored, out var count))
            result[id] = count / (count + options.SaturationPoint);
    return result;
}
```
In the chunk `RerankCandidate` construction (`:646-654`), populate `Popularity` from `popularities` keyed by the chunk's `parent_id`, the same way `centroid` already is.

- [ ] **Step 4: Tests**

`ObjectSearchVectorIntegrationTests.cs`: extend with a live-Qdrant case (matching this file's existing Testcontainer convention) covering: (a) a configured type with a patched `<relation>Count` field ranks a lower-cosine-but-more-popular candidate above a higher-cosine-but-unpopular one when `WPopularity` is raised; (b) `SearchChunks` on the same type also reflects the signal (closing finding 2.2); (c) an unconfigured type is unaffected (bit-identical ordering to today).

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
        Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs \
        Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs
git commit -m "wire Popularity into SearchSimilar and SearchChunks"
```

## Known issues inherited from spec

- Whether Qdrant's `SetPayloadAsync` actually raises `NotFound` for a point id that doesn't exist within an already-created collection was left explicitly unverified by the spec (no live Qdrant instance available at design time) — Task 2's integration test against a real Testcontainer resolves this empirically before the rest of the plan depends on the assumption.
- `WPopularity`/`SaturationPoint` ship chosen-not-measured — no BEIR/FreshStack benchmark task exists in this plan, matching the spec's explicit scope decision.
