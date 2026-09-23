# One projection tenant-resolution rule for the entity-event consumers

## Motivation

IDesign review round 2, Finding 3 (`docs/reviews/2026-09-15-Iverson-idesign-review-1.md`).

Five `entity.events` consumers decide, each in its own copy of the same code, which tenant (and, for two of them, which owner) an event may be projected under. The rule is the projection side of CSR #7:

- **Created/Updated:** re-derive from the authoritative Postgres row, because the event payload is unsigned.
- **Deleted:** read the pre-delete snapshot, because the row is already gone.

The five copies:

| Consumer | Created/Updated | Deleted |
|---|---|---|
| `IntelligenceStoreConsumer` | `FetchAuthoritativeOwnerValueAsync` (`:576-593`), called twice (`:116-119` owner, `:126-127` tenant) | `ExtractString(payload, TenantColumn)` (`:546`) |
| `EngagementStoreConsumer` | `FetchAuthoritativeOwnerValueAsync` (`:140-160`), called twice (`:58-59` tenant, `:81` owner) | inline `TryGetProperty` (`:109-127`) |
| `EnrichmentConsumer` | inline (`:107-143`) | inline (`:280-301`) |
| `DocumentRerenderConsumer` | `ResolveTenantIdAsync` (`:131-149`) | same method |
| `PopularitySignalConsumer` | `ResolveTenantIdAsync` (`:258-282`) | same method |

The copies have drifted:

- **Unresolvable tenant on Created/Updated.** Four consumers drop the event with a warning. `IntelligenceStoreConsumer` carries on with a null tenant. It resolves the Qdrant sentinel collection `{base}[_chunks]___no_tenant_claim___<fingerprint>` and skips lazy-create for it (`:156`, `:174`), then writes to it anyway (`:161`, `:228`, `:336`). Qdrant answers NotFound, so the event takes `MessageDispatcher`'s retry ×3 → DLQ path.
- **A JSON-`null` tenant in a snapshot.** `EngagementStoreConsumer` and `IntelligenceStoreConsumer` turn it into `""` via `JsonElement.ToString()`, so their null guards do not fire. The other three turn it into `null` and drop.
- **Casing.** `EngagementStoreConsumer` has no camelCase fallback; the other four do.
- **Malformed input.** `EnrichmentConsumer` logs and skips a malformed row or snapshot. `DocumentRerenderConsumer` and `PopularitySignalConsumer` let a raw `JsonException` escape, which is then retried 3 times before it dead-letters. `IntelligenceStoreConsumer` and `EngagementStoreConsumer` throw `PoisonMessageException` for a malformed snapshot.

## Decisions (made during brainstorming, 2026-09-15)

1. **Outcome policy when no tenant resolves: split by cause.**
   - **(i)** Created/Updated and the authoritative row is **gone**: **drop** with a warning. Events are keyed by entity key (`OutboxPublisher.cs:44-46`), so this entity's Deleted event always follows and performs the projection cleanup. A dead-lettered case-(i) event could never be replayed successfully. This matches `ReconciliationService.ProcessOneAsync` (`:108-114`), which already treats a gone row as "nothing to reconcile".
   - **(ii)** The row (or delete snapshot) is **present but carries no tenant value**, or is malformed: **dead-letter immediately** (`PoisonMessageException`). Every write path stamps the tenant column, so this is an invariant violation an operator must see. Retrying cannot heal it.
2. **Packaging: a static helper** taking `IEntityRepository` as a parameter, following the precedent of `AuthorizationFieldMasking.EnforceWriteAuthorization`, which takes its collaborators as parameters. There is no DI registration and no change to any consumer constructor.
3. **Sentinel tenant id: fold in.** `CreateTenant` rejects a tenant id equal to the Qdrant no-tenant sentinel (§3).

## Explored and deliberately not pursued

- **Injected singleton** (`ProjectionTenantResolver`, `DocumentRenderer` precedent): it behaves the same as the static helper, but costs a DI registration, a constructor parameter on five consumers, and changes to about 11 test construction sites.
- **Abstract `ProjectionConsumer : BackgroundService` base class** absorbing `Deserialize` as well: broader than Finding 3, and it changes the inheritance of five hosted services. The popularity-signal plan also ruled against a shared base class (`PopularitySignalConsumer.cs:255-257`).
- **Drop everywhere:** this would hide case (ii), an invariant violation, behind a log line.
- **Dead-letter everywhere:** every delete race would produce DLQ rows in up to five consumer groups that can never be replayed.

## Design

### 1. The helper

New file `Iverson.Server/Iverson.Api/Consumers/ProjectionTenantResolution.cs`:

```csharp
internal static class ProjectionTenantResolution
{
    internal static async Task<AuthoritativeRow?> FetchAuthoritativeRowAsync(
        IEntityRepository entities, SchemaDescriptor schema, string key, string consumer);

    internal static string TenantFromSnapshot(
        string payloadJson, SchemaDescriptor schema, string key, string consumer);

    internal static string? ReadString(JsonElement element, string propertyName);
}

internal sealed record AuthoritativeRow(string TenantId, JsonElement Row)
{
    internal string? ReadString(string propertyName) => ProjectionTenantResolution.ReadString(Row, propertyName);
}
```

**`FetchAuthoritativeRowAsync`**
- Makes one call: `entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key, EntityAccess.CrossTenantMaintenance)`.
- Outcomes:
  - `null` from the repository → returns `null` (case i). The helper does not log; each caller logs its own drop warning.
  - JSON that does not parse → `throw new PoisonMessageException($"{consumer} Malformed authoritative row JSON type={schema.TypeName} key={key}", ex)`.
  - `ReadString(row, schema.TenantColumn)` is `null` → `throw new PoisonMessageException($"{consumer} No tenant value on authoritative row type={schema.TypeName} key={key}")` (case ii).
  - Otherwise → `new AuthoritativeRow(tenant, root.Clone())`. The clone keeps `Row` valid after the `JsonDocument` is disposed.
- An exception from `FetchByKeyAsync` itself propagates **unwrapped**, so a transient Postgres fault keeps its ordinary retry path.

**`TenantFromSnapshot`**
- The snapshot is `ev.PayloadJson`, which is the raw `row_to_json` row captured before the delete.
- Outcomes:
  - JSON that does not parse → `PoisonMessageException` (`"{consumer} Malformed delete snapshot JSON …"`).
  - Tenant `null` → `PoisonMessageException` (`"{consumer} No tenant value in delete snapshot …"`).
  - Otherwise → returns the tenant.

**`ReadString`**
- Tries the exact property name, then the camelCase fallback (first character lower-cased).
- JSON `null` → `null`; JSON string → its value; any other kind → `JsonElement.ToString()`.
- This is the body the `EnrichmentConsumer`, `DocumentRerenderConsumer` and `PopularitySignalConsumer` copies already have, so their JSON-null behaviour is the one that wins.

`consumer` is the existing log prefix (`"[Intelligence]"`, `"[Engagement]"`, `"[Enrichment]"`, `"[DocumentRerender]"`, `"[PopularitySignal]"`), so dead-letter records stay attributable. The prefix each consumer already uses in its own logs is authoritative; the implementation must match it.

### 2. Consumer wiring and behaviour changes

**`IntelligenceStoreConsumer.HandleAsync`**
- **Fetch:** the two `FetchAuthoritativeOwnerValueAsync` calls (`:116-119`, `:126-127`) become `var row = await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, schema, ev.Key, "[Intelligence]");`, at the position of the current tenant fetch. The schema lookup, the `CollectionName is null` return and the payload parse stay ahead of it.
- **On `null`:** `logger.LogWarning("[Intelligence] Dropped event — no authoritative row for type={Type} key={Key}", …)` and return, before any embedding or Qdrant call.
- **Values:** `authoritativeTenantValue = row.TenantId` (non-null `string`). `authoritativeOwnerValue = ownerField is not null ? row.ReadString(ownerField) : null`.
- **Dead code removed.** A null tenant can no longer reach this code, so these branches go. Keeping them, with comments asserting reachability, would mislead:
  - the `if (authoritativeTenantValue is not null)` gates before `EnsureCollectionAsync` (`:156`, `:174`); lazy-create now always runs;
  - the "rendering document with no authoritative tenant value" warning (`:199-212`);
  - `?? string.Empty` (`:215`);
  - `&& authoritativeTenantValue is not null` on the centroid write (`:365`), with its "inherited asymmetry" comment (`:361-364`).
- **Deleted:** `FetchAuthoritativeOwnerValueAsync` (`:576-593`).
- **Spec consistency:** the Qdrant tenant-isolation spec (`2026-07-18-qdrant-tenant-collection-isolation-design.md:51`, `:70`) requires lazy-create to be skipped whenever the write-path tenant is null. That still holds, because a null tenant now never reaches the write path: the helper returns `null` (drop) or throws before it.

**`IntelligenceStoreConsumer.HandleDeleteAsync`**
- The payload parse (`:535-544`) and `:546` become `var tenantValue = ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, schema, ev.Key, "[Intelligence]");`.

**`EngagementStoreConsumer.HandleUpsertAsync`**
- **Fetch:** `:58-64` becomes the helper call. On `null`, keep today's warning text ("Dropped upsert — no authoritative …") and return.
- **Owner:** `:81` becomes `row.ReadString(ownerField)`. `WithOwnerValue` is unchanged.
- **Deleted:** `FetchAuthoritativeOwnerValueAsync` (`:140-160`).

**`EngagementStoreConsumer.HandleDeleteAsync`**
- `:109-127` becomes `TenantFromSnapshot(..., "[Engagement]")`.

**`EnrichmentConsumer.HandleAsync`**
- `:107-143` becomes the helper call. On `null`, keep today's "No authoritative row … skipping" warning and return.
- `BuildSourceText(schema, row)` (`:145`) reads `row.Row`, and the tenant comes from `row.TenantId`. The raw row string `rowJson` (declared at `:107`) has one other reader, the outbox enqueue at `:223`. It becomes `row.Row.GetRawText()`. Every `tenantValue` reader outside the replaced range (`:149`, `:179`, `:204`, `:218`, `:221`, `:233`) reads `row.TenantId`.

**`EnrichmentConsumer.HandleDeleteAsync`**
- `:280-301` becomes `TenantFromSnapshot(..., "[Enrichment]")`. The early `schema is null || EnrichmentTargets.Count == 0` return (`:272-273`) stays ahead of it.

**`DocumentRerenderConsumer` and `PopularitySignalConsumer`**
- Each `ResolveTenantIdAsync` body becomes:
  ```csharp
  ev.EventType == EntityEventType.Deleted
      ? ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, changedSchema, ev.Key, "<prefix>")
      : (await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, changedSchema, ev.Key, "<prefix>"))?.TenantId
  ```
- The callers' `if (tenantId is null) return;` stays (`DocumentRerenderConsumer.cs:68`, `PopularitySignalConsumer.cs:198`) and now fires only for case (i).
- `PopularitySignalConsumer`'s duplication-justifying comment (`:250-257`) is deleted.

**Resulting behaviour changes**

| Condition | Intelligence | Engagement | Enrichment | DocumentRerender | PopularitySignal |
|---|---|---|---|---|---|
| C/U, row gone | retry×3 → DLQ ⇒ **drop** | drop (same) | drop (same) | drop (same) | drop (same) |
| C/U, row present, tenant missing/null | retry×3 → DLQ ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** |
| C/U, row JSON malformed | raw exception, retry×3 → DLQ ⇒ **Poison** | same ⇒ **Poison** | log + skip ⇒ **Poison** | retry×3 → DLQ ⇒ **Poison** | retry×3 → DLQ ⇒ **Poison** |
| Deleted, snapshot tenant missing | delete on sentinel, retry×3 → DLQ ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** |
| Deleted, snapshot tenant JSON-null | delete under `""` tenant ⇒ **Poison** | delete under `""` tenant ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** | drop ⇒ **Poison** |
| Deleted, snapshot malformed | Poison (same) | Poison (same) | log + skip ⇒ **Poison** | retry×3 → DLQ ⇒ **Poison** | Poison (same) |
| Owner column JSON-null on the row | owner `""` in point payload ⇒ **key omitted** | owner `""` spliced ⇒ **key removed** | n/a | n/a | n/a |
| Authoritative-row reads per C/U event with an owner field | 2 ⇒ **1** (plus the unchanged summary read) | 2 ⇒ **1** | n/a | n/a | n/a |

The owner JSON-null change makes the "omit the key when there is no authoritative owner value" rule, which already applies to a gone row, apply to a null owner value too. A `""` owner matched no ownership-filtered reader in either store, because an acting user with an empty `sub` is denied before any owner filter is built (verified assumption #29). No reader loses a row.

**Left unchanged (scope limits):**
- the local `ExtractString` copies used for non-tenant fields (FK keys, payload text, metadata values);
- `IntelligenceStoreConsumer.FetchSummaryAsync` and its best-effort row read;
- `DocumentRenderer`, `DlqMonitorConsumer`, `ReconciliationService`;
- the delivery contract in `MessageDispatcher`.

### 3. Reject the Qdrant no-tenant sentinel as a tenant id

The Qdrant write and read paths fail closed for a null tenant only while the sentinel collection never exists (Qdrant isolation spec `:51`, `:70`). `TenantIdentifier.IsValid` (`^(?!.*--)([A-Za-z0-9_-]{1,52})$`) accepts the literal `__no-tenant-claim__`. A tenant provisioned under that id would therefore own the sentinel collection. A tenant id reaches the system in only three ways:

- `TenantLifecycleGrpcService.CreateTenant`;
- the hard-coded legacy seeds (`Program.cs:511`);
- acting-user claims, which `ActingUserInterceptor` admits only for an existing active tenant row.

`CreateTenant` is therefore the single place to close this.

- `IntelligenceTenantScope.NoTenantSentinel` changes from `private const` to `public const` (`Iverson.Vector/IntelligenceTenantScope.cs:10`).
- `TenantLifecycleGrpcService.CreateTenant` (`:17`) rejects the id with the same `InvalidArgument` message it gives an invalid one when `!TenantIdentifier.IsValid(request.TenantId) || string.Equals(request.TenantId, IntelligenceTenantScope.NoTenantSentinel, StringComparison.Ordinal)`.
- The comparison is **ordinal**. `ResolveCollectionName` fingerprints the original, unsanitized id, and `Sanitize` preserves letter case. Only a byte-identical id yields the sentinel collection name, so a case variant is a distinct, harmless collection.
- The rule stays out of `TenantIdentifier.IsValid`. That function lives in `Iverson.StarRocks`, and the sentinel is a Qdrant concept. `Iverson.Vector` deliberately takes no reference on `Iverson.StarRocks` (`IntelligenceTenantScope.cs:65-68`), and the reverse coupling would be just as wrong.

### 4. Testing

Conventions: xUnit, NSubstitute (`Substitute.For<IEntityRepository>()`), FluentAssertions 8 `await act.Should().ThrowAsync<T>()`. Tests live in `Iverson.Api.Tests/Consumers/` and `Iverson.Api.Tests/Grpc/`.

**New `Iverson.Api.Tests/Consumers/ProjectionTenantResolutionTests.cs`:**
- **`FetchAuthoritativeRowAsync`:**
  - repository returns `null` → result `null`;
  - row with a tenant → `TenantId` matches, and `Row.ReadString` still works after the call returns;
  - tenant JSON-null → `PoisonMessageException`;
  - tenant key absent → `PoisonMessageException`;
  - camelCase-only tenant key → resolves;
  - malformed row JSON → `PoisonMessageException`;
  - repository throws `InvalidOperationException` → `InvalidOperationException` propagates (not Poison);
  - the repository call receives `EntityAccess.CrossTenantMaintenance`.
- **`TenantFromSnapshot`:** tenant present → value; key absent → Poison; JSON-null → Poison; malformed → Poison.
- **`ReadString`:** exact key preferred over camelCase; JSON null → `null`; a JSON number → its raw text.

**Existing tests rewritten to the new behaviour, renamed to match what they now assert:**

| Test | New assertion |
|---|---|
| `EngagementStoreConsumerTests.HandleUpsert_WithNoAuthoritativeTenantValue_SkipsProvisioningAndUpsert` (`:420`) | throws `PoisonMessageException`; no `EnsureTenantProvisionedAsync`, no `UpsertAsync` |
| `EnrichmentConsumerTests.HandleUpdated_WithNullTenantValueInRow_SkipsAndWritesNoStateRow` (`:374`) | throws Poison; no state row |
| `EnrichmentConsumerTests.HandleDelete_WithNoTenantInSnapshot_SkipsTheStateDelete` (`:404`) | throws Poison; no state delete |
| `DocumentRerenderConsumerTests.Dispatch_AuthoritativeRowHasNoTenantValue_EnqueuesNothing` (`:526`) | throws Poison; nothing enqueued |
| `PopularitySignalConsumerTests.Dispatch_AuthoritativeRowHasNoTenantValue_SkipsWithoutCallingAggregate` (`:458`) | throws Poison; no `AggregateAsync` |
| `IntelligenceStoreConsumerTests.HandleCreated_WithOwnerFieldAndNoAuthoritativeRow_OmitsOwnerKeyFromChunkPayload` (`:424`) | row gone → does not throw; no `UpsertNamedAsync`, `DeleteByFilterAsync` or `ApplyCollectionAsync` on any collection |
| `IntelligenceStoreConsumerTests.HandleCreated_AuthoritativeTenantValueMissing_RendersDocumentWithoutThrowingAndLogsWarning` (`:2323`) | **deleted**: the scenario it pins (rendering with a null tenant) is unreachable; the rewritten `:424` covers the row-gone path |

**Existing test whose fixture must change (assertion unchanged):** `IntelligenceStoreConsumerTests.HandleCreated_WithMultipleVectorFields_EmbedsAllFields` (`:740`). Its schema uses `TenantColumn = SchemaDescriptor.TenantColumnName` (`:761`) but relies on the constructor row stub (`:96-97`), which carries the tenant only under `"TenantId"`. Under the new rule that is case (ii) and throws. Add `_entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>()).Returns($$"""{"{{SchemaDescriptor.TenantColumnName}}":"test-tenant"}""");` before `HandleAsync`, mirroring `:707-708`. The embed assertions stay as they are.

**New consumer tests** for changed delete-snapshot paths that no existing test covers:
- `IntelligenceStoreConsumerTests`: Deleted with a snapshot lacking a tenant → throws Poison; no `DeleteAsync`.
- `EngagementStoreConsumerTests`: Deleted with snapshot `{"TenantId":null}` → throws Poison; no `DeleteAsync` (pins the removed `""`-tenant delete).

**New `TenantLifecycleGrpcServiceTests` case**, `CreateTenant_NoTenantSentinelId_ThrowsInvalidArgumentAndTouchesNoDependency`, mirroring `CreateTenant_InvalidTenantId_ThrowsInvalidArgumentAndTouchesNoDependency` (`:71`). It uses `TenantId = IntelligenceTenantScope.NoTenantSentinel` and asserts `InvalidArgument`, no `InsertAsync` and no `CreateUserAsync`.

**Unchanged tests that must stay green**, as evidence that the single fetch kept CSR #7:
- the owner-forgery tests (Engagement `:273`; Intelligence `:318`, `:365`);
- the owner-omission test with a row that has a tenant but no owner (Engagement `:349`);
- the `Received(1).FetchByKeyAsync` tests (Engagement `:345`, `:416`; Intelligence `:487`, `:527`).

**Commands**, run from `Iverson.Server/`:
- Quick loop: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~Consumers&FullyQualifiedName!~KafkaOrdering"`. The bare `~Consumers` filter also matches `EngagementStoreConsumerKafkaOrderingTests`, a Kafka container test. Confirm the matched set with `--list-tests` before relying on the count.
- Tenant test: `dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~TenantLifecycleGrpcServiceTests"`.
- Before completion: the full `dotnet test Iverson.Api.Tests`.

## Out of scope

- Round 1 Finding 1 and round 2 Findings 1 and 2 (orchestrator extraction, retrieval Engine, tenant-aware `Iverson.Vector`).
- Collapsing the non-tenant `ExtractString` copies and the five `Deserialize` copies.
- Reusing the authoritative row for `IntelligenceStoreConsumer`'s summary read.
- The `DlqMonitorConsumer` tenant-from-payload attribution used by `/admin/dlq` visibility.
- Existing tenants or seeds whose id already equals the sentinel. None exist: the legacy seed list is hard-coded in `Program.cs:511`, and no path besides `CreateTenant` inserts an arbitrary id.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `IEntityRepository.FetchByKeyAsync(TableSchema, string, EntityAccess)` returns `Task<string?>` with no `CancellationToken`; `EntityAccess.CrossTenantMaintenance` exists | `Iverson.Sql/EntityRepository.cs:7`; `Iverson.Sql/IRecordStoreRoles.cs:160` |
| 2 | `SchemaDescriptor.TenantColumn` is non-null for every registered schema | `Iverson.Api/Schema/SchemaDescriptor.cs:67` `public required string TenantColumn`; rehydrated rows without it are not admitted (`DocumentRerenderConsumerTests.cs:455` pins this) |
| 3 | `PoisonMessageException(string)` and `(string, Exception)` exist and are public | `Iverson.Events/PoisonMessageException.cs` |
| 4 | All five copies build the table via `SchemaBuilder.ToTableSchema(schema)` | `IntelligenceStoreConsumer.cs:582`, `EngagementStoreConsumer.cs:147`, `EnrichmentConsumer.cs:104`, `DocumentRerenderConsumer.cs:145`, `PopularitySignalConsumer.cs:277` |
| 5 | Row JSON and delete snapshots carry the tenant under the exact `TenantColumn` name | `EntityRepository.cs:8-10` uses `row_to_json(t)`, which keys by the quoted column name; the Delete snapshot is that same `FetchByKeyAsync` result (`ObjectMappingGrpcService.cs:428-429`, `:479-484`) |
| 6 | Each consumer's current tenant code and failure behaviour are as described in §2 and the Motivation table | Read at the cited ranges; the Rerender Deleted arm and Created row parse throw a raw `JsonException` (`DocumentRerenderConsumer.cs:137`, `:148`); the Enrichment Deleted malformed path logs and returns (`EnrichmentConsumer.cs:285-292`) |
| 7 | The listed Intelligence branches are the only code depending on a null tenant | Full read of `IntelligenceStoreConsumer.HandleAsync` (`:73-401`); the other uses of `authoritativeTenantValue` are `ResolveCollectionName` arguments, which accept any string |
| 8 | Every consumer's row-gone return precedes all side effects | Intelligence: the new return sits before the first embed or Qdrant call (`:131`); Engagement `:60-64` precedes provisioning `:68-72`; Enrichment `:108-114` precedes state and hash work; Rerender `:68` precedes enqueue; Popularity `:198` precedes `AggregateAsync` |
| 9 | No catch-all swallows the new `PoisonMessageException` | Enrichment's `try` starts after `BuildSourceText` (`EnrichmentConsumer.cs:161`); Popularity's per-signal `catch` (`:241`) is inside the loop after resolution; Intelligence, Engagement and Rerender have no `try` around resolution |
| 10 | All five consumers run handlers through `MessageDispatcher`, where Poison dead-letters immediately and other exceptions retry | `Iverson.Events/KafkaConsumer.cs:11`, `:65`; `MessageDispatcher.cs:51-59` (Poison → DLQ), `:65-78` (retry ×3 → DLQ) |
| 11 | No reflection binds the deleted private methods | `grep FetchAuthoritativeOwnerValueAsync\|ResolveTenantIdAsync` outside `Iverson.Api/Consumers/` finds only comments (`IntelligenceStoreConsumerTests.cs:2326`, `:2358`; `PopularitySignalConsumerTests.cs:549`) |
| 12 | Exactly the seven tests in §4 assert changed behaviour, plus one test (`:740`) whose fixture must change | Swept every `FetchByKeyAsync` stub and every `EntityEventType.Deleted` payload across the five consumer test files, `ObjectSearchVectorIntegrationTests.cs` and `Reconciliation/`: tenantless or `null` row stubs occur only at Intelligence `:439`, `:2333`, Engagement `:428`, Enrichment `:378`/`:409`, Rerender `:533` and Popularity `:465`. Rerender `:289`/`:513` and Popularity `:443` are row-gone (unchanged drop); Rerender `:474` returns earlier (type not registered); Engagement `:101` throws earlier (unknown type); Popularity `:550` is a malformed Deleted that is already Poison; and every schema whose `TenantColumn` differs from the key its row stub stores the tenant under: only `IntelligenceStoreConsumerTests.cs:761` (no stub) and `:701` (stubbed at `:707-708`) |
| 13 | Consumer fixtures do not rely on NSubstitute's default `null` row for happy paths | Constructor stubs return tenant-bearing rows under `"TenantId"`: `IntelligenceStoreConsumerTests.cs:96-97`, `EngagementStoreConsumerTests.cs:51-52`, `EnrichmentConsumerTests.cs:79`. The one test that switches `TenantColumn` to the reserved name without its own stub is `IntelligenceStoreConsumerTests.cs:740` (§4). Rerender and Popularity stub per test, and an unstubbed row already drops today |
| 14 | `ObjectSearchVectorIntegrationTests` does not use the null-tenant path | `ObjectSearchVectorIntegrationTests.cs:261-263` stubs a row with `TenantId` |
| 15 | `Iverson.Api.Tests` can see `internal` types in `Iverson.Api` | `Iverson.Api/Iverson.Api.csproj:10-12` `InternalsVisibleTo Iverson.Api.Tests` |
| 16 | The quick-loop filter's matched set | `dotnet test Iverson.Api.Tests --list-tests --filter "FullyQualifiedName~Consumers"` listed 166 cases in 8 classes, including `EngagementStoreConsumerKafkaOrderingTests` (a container test), hence the `!~KafkaOrdering` exclusion |
| 17 | The test stack | `Iverson.Api.Tests.csproj:11-17`: xunit 2.9.3, NSubstitute 5.3.0, FluentAssertions 8.11.0; `ThrowAsync<PoisonMessageException>()` is already used at `EngagementStoreConsumerTests.cs:166` |
| 18 | The Qdrant spec's lazy-create rule stays satisfied after removing the null gates | Qdrant spec `:51`, `:70` ties the rule to a null write-path tenant; the helper makes that state unreachable (drop or throw before the write path) |
| 19 | `EnrichmentConsumer` reads `row` only in `BuildSourceText` (`:145`) and `rowJson` only in the outbox enqueue (`:223`) | `grep -n rowJson EnrichmentConsumer.cs` → `107, 108, 119, 223` (only `:223` is outside `:107-143`); the only `row` reference is `:145`; the later republish uses its own fetch (`:232`) |
| 20 | Nothing outside the consumers depends on their private resolvers | Same grep as #11 |
| 21 | The Kafka ordering container test does not depend on a tenantless row | `EngagementStoreConsumerKafkaOrderingTests.cs:108-110` stubs a row with `TenantId` |
| 22 | **Probe:** a write to the missing sentinel collection fails in real Qdrant | Ran `qdrant/qdrant:v1.18.2` with `QDRANT__SERVICE__JWT_RBAC=true` and a scoped rw JWT for `articles___no_tenant_claim___74zc32639tuhty7qvmca0gtmn`: `PUT …/points` → HTTP 404 `"Collection … doesn't exist!"`; `POST …/points/delete` → HTTP 404; the admin collection list stayed `[]` |
| 23 | Case (i) is always followed by the entity's Deleted event | `OutboxPublisher.cs:44-46` produces with the entity key; `ObjectMappingGrpcService.Delete` publishes `Deleted` with `request.Key` (`:494-502`); the delete-outbox row is replayed by `ReconciliationService.ProcessDeleteRowAsync` if the publish fails |
| 24 | **Probe:** `JsonElement.ToString()` on JSON null yields `""` | Ran a .NET 10.0.112 single-file program: the Engagement shape gave `""`; the Popularity/Enrichment/Rerender shape gave `null` |
| 25 | A tenant id enters only via `CreateTenant`, the hard-coded seeds, or an admitted claim | `TenantLifecycleGrpcService.cs:17-23` (the only `InsertAsync` caller); `Program.cs:508-513` (`SeedIfMissingAsync`, fixed list); `ActingUserInterceptor.cs:44-50` rejects a claim whose tenant row is absent or not active |
| 26 | `TenantIdentifier.IsValid` accepts `__no-tenant-claim__` | Pattern `^(?!.*--)([A-Za-z0-9_-]{1,52})$` (`Iverson.StarRocks/TenantIdentifier.cs:9`): 19 characters from the allowed set, with no `--` |
| 27 | Only a byte-identical id collides with the sentinel collection name | `IntelligenceTenantScope.cs:41-46` fingerprints the original value; `Sanitize` (`:52-62`) keeps letter case |
| 28 | `Iverson.Api` references `Iverson.Vector`, and `TenantLifecycleGrpcServiceTests` has an invalid-id test to mirror | `Iverson.Api.csproj:45`; `TenantLifecycleGrpcServiceTests.cs:71-95` |
| 29 | No ownership-filtered read can carry an empty owner value | `RowFieldAuthorizationEvaluator.cs:51-59`: `ownerValue = sub`, and `string.IsNullOrEmpty(sub)` returns a denied decision first |
| 30 | The log prefixes used for `consumer` | `"[Intelligence]"`, `"[Engagement]"`, `"[Enrichment]"`, `"[PopularitySignal]"` appear in the respective consumers' existing messages (quoted in §2 citations); `"[DocumentRerender]"` at `DocumentRerenderConsumer.cs:123` |
| 31 | `row.Row.GetRawText()` supplies the `string payload` that `EnqueueUpdateOutboxRowAsync` requires, and an `Updated` outbox row's stored payload is never read on replay | `OutboxWriter.cs:10`: the signature takes `string payload`. A .NET 10 run that parsed a `row_to_json`-shaped row, cloned the root and disposed the document gave `GetRawText() == fetched` → `True`. `grep '\.Payload\b\|"Payload"'` over `Iverson.Api` and `Iverson.Sql`: the outbox `Payload` column is selected only at `ReconciliationQueueRepository.cs:10` and read only at `ReconciliationService.cs:168`, inside `ProcessDeleteRowAsync` |
| 32 | Decision 1(ii)'s premise: every current write path stamps a non-null tenant, and the column is `NOT NULL` | The evaluator returns `Denied` for no rules, no acting user, no `TenantColumn`, or an empty `tenant_id` claim (`RowFieldAuthorizationEvaluator.cs:11-15`, `:29-33`), and otherwise returns `TenantValue = tenantId` (`:120-121`). `EnforceWriteAuthorization` throws on `Denied` (`AuthorizationFieldMasking.cs:83`). `grep UpsertAndEnqueueOutboxAsync(` gives exactly four callers (`ObjectPersistenceGrpcService.cs:62`, `:133`; `ObjectMappingGrpcService.cs:316`, `:381`), each passing `tenantId: decision.TenantValue`, which `OutboxWriter.WithTenantColumn` writes into the row (`OutboxWriter.cs:47`). `Iverson.LoadTest/Seeding/DirectSeeder.cs:84`, `:154`, `:213-214` COPY `"__TenantId"` explicitly and publish no events. The column is `IsNullable false` (`SchemaBuilder.cs:229`), so `CREATE` emits `NOT NULL` (`PostgresSchemaManager.cs:69-70`). On a pre-existing table, `ADD COLUMN` backfills `NOT NULL DEFAULT ('')` (`:118`, `GetDefaultForType` `:339`) |
| 33 | Every producer of a `Deleted` entity event ships the full pre-delete row as `PayloadJson` | Three producers. (1) `ObjectMappingGrpcService.Delete` constructs `EntityEventType.Deleted` (`:495`) with `rowJson` from `FetchByKeyAsync` (`:428-429`). (2) `ReconciliationService.ProcessDeleteRowAsync` (`:165`) republishes `row.Payload`, which `ObjectMappingGrpcService.Delete` persisted as that same `rowJson` via `EnqueueDeleteOutboxRowAsync` (`:479-484`). (3) `/admin/dlq/{id}/replay` (`Program.cs:460`) re-produces a dead-lettered message's original bytes unchanged, so its payload is whichever of (1) or (2) produced it. `grep EntityEventType.Deleted` over all non-test sources finds constructions only at (1) and (2) |
