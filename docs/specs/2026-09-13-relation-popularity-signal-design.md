# Relation popularity signal for SearchSimilar / SearchChunks

## Motivation

`SearchSimilar` and `SearchChunks` answer entirely out of the Qdrant payload — deliberately, by
design ([[project-retrieval-quality-benchmark]]'s "no Postgres join" bet). Everything a registered
type's own scalar/FK/metadata columns can offer is already denormalized into that payload at
ingest time, and everything a *related* type's text fields can offer is already composable into
the embedded/chunked text via the templated-document-chunking feature (`DocumentRenderer`, one-hop
and block placeholders reading `IEntityRepository`).

What neither mechanism can produce is an **aggregate signal over a one-to-many relation** — e.g.
"how many `UserArticle` rows point at this `Article`" — because that number lives only in
StarRocks/Postgres and changes on every related-row write, independent of the parent entity's own
text. This is a genuine, currently-unbuildable capability gap, verified against the shipped
`Article`/`UserArticle` sample domain (`Iverson.Clients/DotNet/Iverson.Client.Sample/Models/`),
not a hypothetical one.

This design adds that one signal: a configurable per-type relation count ("popularity"), computed
asynchronously from StarRocks and fused into vector search ranking exactly the way the existing
`Decay` signal already is.

## Explored and deliberately not pursued

- **Live query-time join** (StarRocks/Postgres queried inside `SearchSimilar`/`SearchChunks` at
  request time): rejected. `SearchSimilar`/`SearchChunks` have zero dependency on StarRocks/Postgres
  today; a live join would add new latency, a new failure dependency, and a new per-request
  authorization surface (the exact "N stores = N authorization checks" failure pattern recorded
  against similar ideas in [[project-multi-property-vector-search]]).
- **Hybrid lexical recall** (merging StarRocks `CONTAINS` hits into the vector candidate pool):
  rejected for this design. StarRocks's `CONTAINS` compiles to a plain SQL `LIKE '%...%'`
  (`StarRocksQueryBuilder.cs:897`) with no relevance gradient, and the one real benchmark run to
  date shows recall is not the bottleneck (SciFact R@50 ≈ 0.92–0.93,
  [[project-retrieval-quality-benchmark]]). A separate design if a concrete recall gap ever
  surfaces.
- **New declarative attribute across all 5 client languages**: rejected for this design. The
  relation graph needed already exists server-side (`SchemaDescriptor.Relations`); a new
  cross-client attribute would be a much larger, separate initiative
  ([[project-multi-property-vector-search]] costs this class of change at "a full spec->plan->SDD
  initiative" per proto field). This design is server-config-only.

## Design

### 1. Configuration

New `PopularitySignalOptions` (section `PopularitySignal`), empty by default:

```yaml
PopularitySignal:
  Signals:
    - ParentType: Article
      Relation: UserArticles     # must name a OneToMany relation on ParentType
  SaturationPoint: 50            # count at which the normalized signal reaches 0.5
```

At startup, each configured `Relation` name is resolved against `SchemaDescriptor.Relations` for
`ParentType` and validated to have `RelationKind.OneToMany` — a `ManyToOne`/`ManyToMany`/`OneToOne`
relation name is rejected with a clear error (a "count" is only meaningful for the one-to-many
direction; the other three kinds either always count 0/1 or need a different query shape entirely).
Resolving the relation also yields the child type name (`RelatedTypeName`) and the FK column that
lives on the child (`ForeignKey`) — both needed by the consumer below.

The same startup validation also rejects a configured relation whose child type (`RelatedTypeName`)
is not itself `StoreTarget.Engagement`-eligible (`StoreTargeting.IsEngagementEligible`) — a child
type that itself declares any `OneToMany` relation is never written to StarRocks at all
(`EngagementStoreConsumer` early-returns on `!TargetStores.HasFlag(StoreTarget.Engagement)`), so
every count for it would fail permanently with no signal otherwise. It fails fast if any
`PopularitySignal.Signals` entry is configured while `Engagement__Enabled=false` — this feature has
no runtime degrade path for a disabled engagement store (`DisabledEngagementStoreSearchService`
throws unconditionally on every call), so the combination is rejected at startup rather than
crash-looping the consumer. And `SaturationPoint` itself is validated to be finite and > 0 (see
§3's Normalization) — the same finiteness-first idiom `VectorRankingOptions`'s weights already use.

Shipping this with an empty `Signals` list changes no existing behavior.

### 2. Trigger and computation — new background consumer

A new `PopularitySignalConsumer : BackgroundService` (sibling to `EnrichmentConsumer` /
`DocumentRerenderConsumer`, subscribing to the same `EntityTopics.Events` stream on its own
consumer group) — structured on the same pattern `DocumentRerenderConsumer` already uses for
one-to-many dependents, but keyed off the static config map above rather than
`SchemaRegistry.GetDependents` (which is scoped specifically to document-template references and
would silently miss a relation not mentioned in any template — see Verified Assumptions).

On each event:
1. Filter to events whose `TypeName` matches a configured signal's child type (e.g. `UserArticle`).
2. Extract the FK value(s) from the event payload the same way `DocumentRerenderConsumer` already
   does for `RelationKind.OneToMany`: the current payload's FK value (`ev.PayloadJson`, which is
   also what a `Deleted` event's payload holds — the pre-delete snapshot); additionally, on an
   `Updated` event, also extract the FK value from `PriorPayloadJson` in case the row was
   reassigned to a different parent, so both the old and new parent get recomputed.
3. For each affected parent id, run a fresh `COUNT(*)` via the *existing*
   `IEngagementStoreSearchService.AggregateAsync` (`AggregationType.Count`, filtered
   `<ForeignKey> = <parentId>`) — no new StarRocks query-building code. Always a full recompute,
   never an increment/decrement, so redelivery and out-of-order events are naturally idempotent.
4. Patch the raw count onto the parent's Qdrant object-collection point via a new
   `IVectorWriteService.SetPayloadAsync(collection, id, payload)`, wrapping the Qdrant client's
   native `SetPayloadAsync` RPC (payload-only — does not touch vectors, unlike `UpsertAsync`, which
   would null unspecified named vectors per [[project-derived-vector-signals]]'s prior finding).
   Field name: `<relation>Count` camelCased (e.g. `userArticlesCount`).

Tenant scoping and the collection/point-id lookup reuse `IntelligenceTenantScope` exactly as
`ObjectSearchGrpcService` already does.

**Failure handling — degrade, don't retry.** If the parent's Qdrant point doesn't exist yet, the
consumer expects `SetPayloadAsync` to raise `NotFound`, in which case it logs and drops the update;
the signal is then simply absent for that candidate (treated as "no signal," not zero) until the
next event on that parent refreshes it. No DLQ, no retry queue — mirrors
`RetrieveVectorsOrDegradeAsync`'s existing degrade-not-fail convention. **Unverified as of this
design:** whether `SetPayloadAsync` actually raises `NotFound` for a missing point id within an
*already-created* collection (as opposed to a missing collection, which is an established pattern
elsewhere) could not be confirmed without a live Qdrant instance — treat this failure-handling text
as best-effort documentation to be confirmed at implementation time, not a verified behavioral
claim.

**Tenant resolution and authorization.** This consumer runs as an internal backend service, the
same trust level as `EnrichmentConsumer`/`IntelligenceStoreConsumer`, not on behalf of a caller —
but `AggregateAsync` still needs a real, tenant-scoped `authz` argument, not `null`. A bare
`authz: null` is a **test-only** escape hatch for raw-SQL-generation tests
(`EngagementRepository.cs:336-339`: "...e.g. a unit test exercising raw SQL generation... Production
always passes a real authz dict and never reaches here") — it resolves against an *unqualified*
table name (the wrong database, not "every tenant") with no missing-table/database degrade
handling, so it would throw and crash-restart the consumer on every real deployment. Instead, the
consumer resolves the affected row's tenant id the same way
`DocumentRerenderConsumer.ResolveTenantIdAsync` already does (the authoritative Postgres row for
`Created`/`Updated`, the pre-delete payload snapshot for `Deleted`), then calls `AggregateAsync`
with `AuthorizationConstraint(AllowedFields: null, OwnerColumn: null, OwnerValue: null,
TenantColumn: childSchema.TenantColumn, TenantValue: tenantId)` — `AllowedFields: null` gives the
"no field-level restriction" semantics the consumer needs without losing tenant-database
qualification. This routes through the already-correct, tenant-scoped branch, including its
existing graceful degrade for an unprovisioned tenant/table.

**Periodic reconciliation — backstop against the cross-consumer race.**
`PopularitySignalConsumer` and the existing `EngagementStoreConsumer` are independent Kafka
consumer groups on the same `EntityTopics.Events` topic, with no ordering guarantee between them —
if this consumer's count-read for a parent's *last-ever* child-touching event races ahead of
`EngagementStoreConsumer`'s corresponding StarRocks write, that parent's count is undercounted and,
since no further child event will ever arrive to trigger a recompute, stays wrong permanently. A new
periodic sweep, on its own interval independent of the event-driven trigger, recomputes every
configured signal's count for every known parent as a backstop, self-correcting any count left
stale by the race. This is the one mechanism in this design with no existing precedent in the
codebase.

### 3. Consumption — a fourth fusion signal

**Normalization.** The stored value is a raw count (unbounded); the existing fusion blends signals
that are all roughly bounded to [-1,1]/[0,1] (cosine similarity, decay). `ObjectSearchGrpcService`
converts the raw count at read time with a saturating curve, the same shape as BM25 term-frequency
saturation:

```
popularity = count / (count + SaturationPoint)
```

Bounded to [0, 1) once `SaturationPoint` is finite and > 0 (validated at startup, see §1) —
monotonic, diminishing-return; an unvalidated non-positive `SaturationPoint` could otherwise produce
`NaN`/`∞` in the fused score for a legitimately-computed zero count. `SaturationPoint` is chosen,
not measured — same starting position `HalfLifeDays` was in before
[[project-decay-weight-sensitivity]] — expect it to need real-traffic calibration later.

**Fusion.** `RerankCandidate` gains an optional `Popularity` field, populated the same way `Decay`
already is: read the raw count from the payload if present, apply the saturation formula; absent →
`null` ("no signal," never zero). `ResultReranker` treats it exactly like `Centroid`/`Decay`: a
`hasPopularity` check joins the existing checks in both the identity short-circuit (all three
absent → bit-exact `BaseScore`, so today's ranking is unchanged wherever no signal is configured)
and the weighted-mean branch (`weightedSum += WPopularity * popularity; weightTotal += WPopularity`).

**Config.** `VectorRankingOptions` gains `WPopularity` (default **0.0**) alongside `WBase`/
`WCentroid`/`WDecay`, validated with the same finiteness-first rule as the other three. Defaulting
to 0 means this ships inert until an operator both configures a `PopularitySignal` entry and sets a
nonzero weight.

## Out of scope

- No proto or client-library changes in any of the 5 languages — entirely server-side/config-driven
  (explicit user decision).
- No live query-time dependency on StarRocks/Postgres from `SearchSimilar`/`SearchChunks` — they
  remain Qdrant-only at request time.
- No BEIR/FreshStack benchmark measurement of `WPopularity`. Neither corpus has a real engagement
  relation, so this ships chosen-not-measured, in the same position `WDecay` is already in
  (`WDecay` is inert on `BenchmarkDocument`, per [[project-retrieval-quality-benchmark]]).
  Calibration needs real production traffic or a synthetic engagement dataset, neither produced
  here.
- No batching/backpressure on the new consumer beyond `ConsumerResilience`'s existing restart
  wrapper — one StarRocks round-trip + one Qdrant payload patch per relevant event. Add batching
  later only if a real load problem appears.
- No recency weighting inside the popularity signal itself — recency is already `WDecay`'s job;
  this signal is pure volume.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | The trigger cannot use `SchemaRegistry.GetDependents` — it's scoped to document-template references, not general relation tracking | `SchemaRegistry.cs:74-80` docstring: "whose **document template references** typeName." Corrected: resolve directly from `SchemaDescriptor.Relations`, filtered on `RelationKind.OneToMany`. |
| 2 | For a `OneToMany` relation, `RelationDescriptor.ForeignKey` names the FK column on the *child* type, not the parent | `ObjectMappingGrpcService.cs:168`: "`SchemaRelationKind.OneToMany => true, // FK belongs to the related type, not this one.`" |
| 3 | A `Deleted` event's payload still carries the FK value (the pre-delete snapshot), not an empty/nulled payload | `DocumentRerenderConsumer.cs` `ResolveTenantIdAsync`: "The row is already gone from Postgres by the time a delete event is consumed — read the tenant from the pre-delete snapshot **in the payload** instead." |
| 4 | `IEngagementStoreSearchService.AggregateAsync`'s `authz: null` path is **test-only**, not production-safe — it resolves against an unqualified (wrong) StarRocks database with no missing-table degrade handling. The consumer must instead resolve a real tenant id (the same way `DocumentRerenderConsumer.ResolveTenantIdAsync` does) and call `AggregateAsync` with a tenant-scoped `AuthorizationConstraint(AllowedFields: null, TenantColumn:, TenantValue:)` | `EngagementRepository.cs:336-339` in full: "...e.g. a unit test exercising raw SQL generation... Production (ObjectSearchGrpcService) always passes a real authz dict and never reaches here"; `TenantIdentifier.cs:116-117` (`Qualify` returns the unqualified name for `null`); `AuthorizationConstraint.cs:3-8` (field shape). Corrected 2026-09-13 per critical-design-review round 1, finding 2.1 — the original citation quoted only the comment's first clause. |
| 5 | `IEngagementStoreSearchService`, `IEntityRepository`, `IVectorWriteService`, `IntelligenceTenantScope` are all DI-Singleton and safe to inject into a new `BackgroundService` | `Iverson.StarRocks/ServiceCollectionExtensions.cs:23`, `Iverson.Sql/ServiceCollectionExtensions.cs:25`, `Iverson.Vector/ServiceCollectionExtensions.cs:44,50` — all `AddSingleton`. |
| 6 | Qdrant.Client 1.18.1 (already referenced) exposes a native payload-only write | `strings` on `qdrant.client/1.18.1/lib/net6.0/Qdrant.Client.dll` shows `QdrantClient.SetPayloadAsync` (plus `OverwritePayloadAsync`/`ClearPayloadAsync`) already compiled into the referenced package. |
| 7 | `RerankCandidate` has no production construction sites beyond `ObjectSearchGrpcService.cs:278,652` (recurrence check — adding `Popularity` is additive-only) | Repo-wide grep for `new RerankCandidate` — only those two production sites plus test files. |
| 8 | `RelationKind` has exactly `{OneToOne, OneToMany, ManyToOne, ManyToMany}`, so a config-time check can reject a misconfigured non-`OneToMany` relation | `SchemaDescriptor.cs:132`. |
| 9 | The entity-event envelope (`TypeName`, `PayloadJson`, `PriorPayloadJson`, `EventType`) is a shared type, not specific to `DocumentRerenderConsumer` | Same `EntityEvent` shape consumed identically by `DocumentRerenderConsumer`, `IntelligenceStoreConsumer`, `EnrichmentConsumer`. |
| 10 | The child relation type must itself be `StoreTarget.Engagement`-eligible (`StoreTargeting.IsEngagementEligible`) for its count to ever be producible — a child type with any `OneToMany` relation of its own is disqualified and never written to StarRocks | `Iverson.Api/Schema/StoreTargeting.cs:27-40` (eligibility predicate); `EngagementStoreConsumer.cs:46,100` (both handlers early-return on `!TargetStores.HasFlag(StoreTarget.Engagement)`). Added 2026-09-13 per critical-design-review round 1, finding 2.2. |
