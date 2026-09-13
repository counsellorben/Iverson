# Batch B4 report — CSR round 3 finding #5 (Postgres RLS not FORCEd; app connects as table owner)

**Status:** DONE_WITH_CONCERNS (concerns are residuals/follow-ups, not defects in what shipped)
**Commit:** `f06bf8c` on `worktree-csr-remediation` (parent `03d3f3e`)
**Tests:** `Iverson.Api.Tests` 935 passed / 0 failed; `Iverson.Sql.Tests` 107 passed / 0 failed (Postgres
Testcontainers fixture included). Acceptance tests verified falsifiable by mutation.

---

## 1. What the problem actually was (one correction to the brief)

The brief's three composing facts are right, but understate the exposure in one direction and
overstate it in another.

**Understated — FORCE alone does not close this.** The brief frames the fix as "add FORCE, and the
owner stops being exempt." True in kubernetes, where CNPG's `enableSuperuserAccess: false` means
`iverson` is the tables' owner but *not* a superuser. But under docker-compose (`POSTGRES_USER:
iverson`) and in every Testcontainers fixture, the connecting role **is a Postgres superuser**, and
PostgreSQL exempts superusers from RLS *unconditionally* — FORCE or not. So FORCE by itself would
have left dev, compose and the entire test suite exactly as exposed as before, and would have made
the guardrail environment-dependent: loud in production, silent everywhere it gets exercised.

The environment-independent guardrail is **routing every entity-table access through an explicit
non-ambient role**. `SET LOCAL ROLE iverson_runtime` makes `current_user` a role that owns nothing
and holds no `BYPASSRLS`, so the policy bites regardless of what the connection is. That is what
part 3 below does; FORCE is the belt-and-braces for the kubernetes owner case.

**Overstated — `EnrichmentConsumer.UpdateColumnsAsync` is not an unscoped path.** The brief lists it
as one of the three deliberately-cross-tenant sites to migrate. It is already tenant-scoped:
`EnrichmentConsumer.cs:186-188` wraps it in `EnterTenantScopeAsync(tenantValue)` /
`ExitRoleScopeAsync()`. The genuinely unscoped reads in that class are the two `FetchByKeyAsync`
calls (lines ~102 and ~202), not the writeback.

**And the count was wrong.** The report says "currently four" unscoped call sites. There were
**fourteen**, across nine classes. Every one of them would have started returning zero rows the
moment FORCE landed in kubernetes, so all fourteen had to be classified — this is the real reason
the batch is a "Significant Refactor", and it is the part a FORCE-only fix would have shipped as a
production outage rather than a security improvement.

Full inventory, with the disposition each one got:

| Call site | Disposition |
|---|---|
| `ObjectRetrievalGrpcService.Get` | `ForTenant(claim)` (was already scoped) |
| `ObjectRetrievalGrpcService.GetMany` | `ForTenant(decision.TenantValue)` / `CrossTenantMaintenance` when the type declares no tenant column |
| `ObjectMappingGrpcService.Get` | `ForTenant(claim)` (was already scoped) |
| `ObjectMappingGrpcService.Delete` (read) | `ForTenant(claim)` (was already scoped) |
| `ObjectMappingGrpcService.Delete` (the delete itself) | `ForTenant` / `CrossTenantMaintenance`, same conditional as above |
| `EntityRelationResolver` ×3 | `ForTenant(claim)` (was already scoped) |
| `DocumentRenderer` ×3 | `ForTenant(tenantId)` (was already scoped) |
| `DocumentRerenderConsumer.EnqueueByColumn/ByArrayContains` | `ForTenant(tenantId)` (was already scoped) |
| `ObjectMappingGrpcService.Update` (existing-row read) | **`CrossTenantMaintenance`** — see §5 |
| `ObjectPersistenceGrpcService.Update` (existing-row read) | **`CrossTenantMaintenance`** — see §5 |
| `EnrichmentConsumer` tenant-derivation read | `CrossTenantMaintenance` (chicken-and-egg: this read *is* what derives the tenant) |
| `EnrichmentConsumer` post-commit republish read | `ForTenant(tenantValue)` — see §6 |
| `EngagementStoreConsumer.FetchAuthoritativeOwnerValueAsync` | `CrossTenantMaintenance` |
| `IntelligenceStoreConsumer.FetchAuthoritativeOwnerValueAsync` | `CrossTenantMaintenance` |
| `IntelligenceStoreConsumer.FetchSummaryAsync` | `CrossTenantMaintenance` |
| `DocumentRerenderConsumer.ResolveTenantIdAsync` | `CrossTenantMaintenance` (same chicken-and-egg) |
| `ReconciliationService.ReconcileTypeAsync` | `CrossTenantMaintenance` |
| `ReconciliationService.ProcessOneAsync` | `CrossTenantMaintenance` |
| `DocumentRerenderQueueWorker.ProcessEntityRowAsync` | `CrossTenantMaintenance` |
| `DocumentRerenderQueueWorker.FetchKeysAndTenantsPagedAsync` | `CrossTenantMaintenance` |

Guiding rule: **every site that was cross-tenant before is still cross-tenant** (the brief's
"do not change behaviour" constraint), but it now says so in source instead of getting there by
omission. The one exception is the EnrichmentConsumer republish read, justified in §6.

---

## 2. What shipped

### Part 1 — `FORCE ROW LEVEL SECURITY`
`Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs`, immediately after the existing `ENABLE`.
`FORCE` is idempotent in Postgres, which matters because `Program.cs` re-runs `ApplySchemaAsync`
for every registered descriptor on every startup (the "self-heal RLS state" loop at `Program.cs:470`)
— verified, as the brief asked. It sits inside the existing session advisory lock, so it joins the
`ENABLE`/`GRANT` statements already serialised against the `XX000 tuple concurrently updated` race;
the lock's comment was updated to name it.

### Part 2 — the `iverson_maintenance` role
`EnsureRuntimeRoleAsync` → **`EnsureRolesAsync`** (renamed on `IRecordStoreSchemaManager`; it now
ensures two roles, and a method called "runtime role" that creates a maintenance role is the kind
of thing that rots). It:

- creates `iverson_runtime NOLOGIN` and `iverson_maintenance NOLOGIN BYPASSRLS` via a shared
  check-then-create helper, matching the existing idiom exactly (existence check, `CREATE`, swallow
  `42710` for the concurrent-replica race);
- **verifies `rolbypassrls` on the maintenance role and repairs it** if a pre-existing role lacks
  it. This is the one place I went beyond the brief, deliberately: a maintenance role without
  `BYPASSRLS` does not error — it silently returns zero rows to every reconciliation replay, i.e.
  reconciliation reprojecting nothing at all, forever. Verify, don't assume;
- turns the kubernetes `42501` (app user cannot `CREATE ROLE` / grant `BYPASSRLS`) into an
  `InvalidOperationException` naming the exact SQL to run, instead of a bare Postgres error. Still
  fails startup — loudly, and now actionably.

`ApplySchemaAsync` GRANTs `SELECT, INSERT, UPDATE, DELETE` to `iverson_maintenance` on **every**
table it manages, unconditionally — unlike the `iverson_runtime` grant, which stays inside the
`TenantColumn is not null` branch. Rationale: a reconciliation replay of a type that declares no
tenant field is still a maintenance read, and granting only the tenant-scoped subset turns those
into `42501` at runtime. Covered by a dedicated test.

`charts/postgres/templates/cluster.yaml` gains the two parallel `postInitApplicationSQL` statements
(`CREATE ROLE iverson_maintenance NOLOGIN BYPASSRLS`, `GRANT iverson_maintenance TO iverson`) and
its comment now explains both roles and the already-initialised-cluster manual step.

### Part 3 — the architectural change (option (a), with a shape adjustment)

**I chose (a), the non-defaulted required argument — not (b), the interface split.** Reasoning:

- **The codebase has no precedent for splitting a repository across interfaces.** Every repository
  in `IRecordStoreRoles.cs` (`IEnrichmentStateRepository`, `ISchemaRegistryRepository`,
  `IReconciliationQueueRepository`, `IDocumentRerenderQueueRepository`, `IDlqRepository`,
  `ITenantRepository`) is exactly one interface with one implementation and one
  `AddSingleton` registration. (b) would have invented a new convention.
- **(b) needs two implementations of identical SQL.** The tenant and maintenance interfaces would
  have byte-identical method bodies differing only in a role constant, either duplicated or hidden
  behind a shared base — and two implementations that must stay in lockstep is exactly the drift
  hazard this finding is about.
- **(b) doesn't segregate cleanly here.** `ObjectMappingGrpcService` genuinely needs both (its
  `Get`/`Delete` reads are tenant-scoped, its `Update` existing-row read is not), so it would take
  both interfaces and the "visible at a glance in the constructor" benefit largely evaporates.
- **(a) is a compile-time guarantee at every call site, existing and future**, which is the stated
  point of the finding.

The shape adjustment: rather than keeping two parameters and dropping the defaults
(`bool tenantScoped, string? tenantId`), I collapsed them into one required value:

```csharp
public readonly record struct EntityAccess
{
    public bool CrossTenant { get; }
    public string? TenantId { get; }
    public static EntityAccess ForTenant(string? tenantId) => new(false, tenantId);
    public static EntityAccess CrossTenantMaintenance { get; } = new(true, null);
}
```

Three properties this buys over a bare non-defaulted bool:

1. **A stale `tenantScoped: false` cannot survive the migration.** If I had only dropped the
   default, an existing explicit `false` would still compile — and would now mean "maintenance
   role", a *different* thing from what its author wrote. Changing the type forces every site
   through review. (This is not hypothetical: `ObjectRetrievalGrpcService.GetMany` and
   `ObjectMappingGrpcService.Delete` both passed a computed `tenantScoped: decision.TenantColumn is
   not null`, whose `false` arm needed a genuine decision, not a mechanical port.)
2. **"Tenant-scoped" and "which tenant" can no longer disagree.** They are one value.
3. **`default(EntityAccess)` is `ForTenant(null)`** — the fail-closed value (zero rows), not the
   cross-tenant one. Pinned by a test, because a struct's default is reachable from `default` in
   test code and from any future `new T()`.

Underneath, `IRecordStoreQueryExecutor`'s `bool tenantScoped = false` became the three-valued
`RecordStoreRole { Connection = 0, TenantRuntime, Maintenance }`, and
`PostgresRepository.RunTenantScopedAsync` → `RunAsRoleAsync`. `Connection` keeps a default there,
**deliberately**: that executor is an internal SQL primitive used by the plumbing repositories
(outbox, DLQ, tenant registry, schema registry, enrichment state, reconciliation queue) whose tables
carry no policy and no grant for either entity role, and for which the connection's own role is the
correct answer. It is not reachable from any gRPC service. The doc comment on the enum says so.

`TenantScopeTransactionExtensions` gained `EnterMaintenanceScopeAsync`, and
`ExitTenantScopeAsync` was renamed **`ExitRoleScopeAsync`** — it now unwinds either role, and
keeping a name that says "tenant" would have made `EntityRepository.DeleteAsync`'s maintenance arm
read as a mismatched pair.

---

## 3. The acceptance-criterion test

`Iverson.Sql.Tests/TenantScopedAccessIntegrationTests.TableOwner_ReadingWithNoTenantScope_SeesZeroRows_UnderForcedRowLevelSecurity`
— reusing the existing `PostgresContainerFixture`, as instructed (no new fixture).

The brief's sketch ("connect as `iverson_runtime` with `tenantScoped` omitted") is not constructible
— omitting the scope is precisely what *doesn't* connect as `iverson_runtime`. And the fixture's own
connection is a **superuser**, which bypasses RLS regardless of FORCE, so the vulnerable condition
cannot be observed on it at all. The test therefore reproduces the real kubernetes topology:

1. seed a two-tenant table (setup step, on the superuser connection);
2. `CREATE ROLE <tmp> NOLOGIN`, `ALTER TABLE <t> OWNER TO <tmp>` — a **non-superuser owner**,
   exactly what CNPG's `bootstrap.initdb.owner: iverson` + `enableSuperuserAccess: false` produces;
3. inside one transaction, `SET LOCAL ROLE <tmp>` and `SELECT COUNT(*)` with no `app.tenant_id` set;
4. assert **0**;
5. restore ownership and drop the role in a `finally`.

Plus `ApplySchemaAsync_TenantScopedTable_SetsForceRowLevelSecurity`, the catalogue-level assertion
on `pg_class.relforcerowsecurity` (not just `relrowsecurity`).

**Falsifiability verified by mutation, not asserted.** With the `FORCE` statement commented out and
everything else unchanged:

```
Failed ...ApplySchemaAsync_TenantScopedTable_SetsForceRowLevelSecurity
  Expected forced to be True, but found False.
Failed ...TableOwner_ReadingWithNoTenantScope_SeesZeroRows_UnderForcedRowLevelSecurity
  Expected visibleToOwner to be 0, but found 2 (difference of 2).
```

Two rows — every tenant's — which is the finding, reproduced. The mutant was then reverted and the
suite re-run green.

Other tests added:
- `QueryAsync_Maintenance_OnATenantTable_SeesAllTenantsRows` — the BYPASSRLS path works.
- `QueryAsync_Maintenance_OnANonTenantTable_Succeeds_NotPermissionDenied` — pins the unconditional
  maintenance GRANT; without it this is `42501`.
- `EnsureRolesAsync_CreatesMaintenanceRoleWithBypassRls` — asserts `rolbypassrls` true for
  maintenance **and false for `iverson_runtime`** (the role the policy is supposed to bite on).
- `EnsureRolesAsync_RepairsAPreExistingMaintenanceRoleThatLacksBypassRls` — the upgrade path.
- `DeleteAsync_CrossTenantMaintenance_SwitchesToTheBypassrlsRole_SetsNoTenantGuc_AndStillResets`.
- `EntityAccess_Default_IsForTenantWithNoTenantId_TheFailClosedValue`.
- `Get_/GetMany_ReadsUnderTheActingUsersTenantScope_NotCrossTenant` — pins the acting user's
  `tenant_id` claim at the externally reachable read path (this was **never** asserted before;
  the old `tenantScoped: true` was untested at this call site).
- `ReconcileTypeAsync_ReadsUnderTheCrossTenantMaintenanceRole` and the equivalent on the re-render
  worker's type-level page read — pins the exemption so a future "tidy-up" cannot silently narrow
  or widen it.

An existing test, `QueryAsync_TenantScopedFalse_OnTheSameTable_SeesAllTenantsRows`, asserted the
vulnerable behaviour as if it were a feature. It is kept (the ambient role really does see
everything, and `RecordStoreRole.Connection` still exists for plumbing) but renamed
`QueryAsync_OnTheConnectionsOwnRole_SeesAllTenantsRows_WhichIsWhyNoEntityReadMayUseIt` and
re-commented to say what it now documents.

---

## 4. Migration / idempotency

Verified as the brief required: there is no versioned-migration mechanism; `ApplySchemaAsync`
re-runs its DDL on every startup for every registered descriptor (`Program.cs:470-472`) plus the
three bootstrap tables. So:

- `FORCE ROW LEVEL SECURITY` — idempotent in Postgres; self-heals every existing table on next boot.
- `GRANT ... TO iverson_maintenance` — idempotent; same self-heal.
- `CREATE ROLE` — not idempotent, so it uses the same check-then-create + swallow-`42710` idiom
  `iverson_runtime` already used, per the brief's instruction to match the existing idiom.
- kubernetes, **already-initialised cluster**: `postInitApplicationSQL` does not re-run (the chart
  already documented this for `iverson_runtime`). The app cannot create the role itself there, so
  startup fails with an `InvalidOperationException` naming the four statements to run by hand. The
  chart comment was updated accordingly. **This is an operator action required at deploy time** —
  see Concerns.

Five test fixtures called `ApplySchemaAsync` without ever ensuring the roles (they only created
non-tenant tables, which previously needed no grant). The unconditional maintenance GRANT would have
broken them, so each now calls `EnsureRolesAsync()` in `InitializeAsync`, mirroring Program.cs:
`TenantRepositoryPostgresIntegrationTests`, `DlqRepositoryPostgresIntegrationTests`,
`ReconciliationQueuePostgresIntegrationTests`, `DocumentRerenderQueuePostgresIntegrationTests`,
`RegisterSchemaAuthorizationIntegrationTests` (the last already had it).

---

## 5. Deviations from the brief

1. **`EnsureRuntimeRoleAsync` renamed to `EnsureRolesAsync`** (interface change, ~6 call sites).
   Not requested; the method now ensures two roles and verifies an attribute.
2. **`ExitTenantScopeAsync` renamed to `ExitRoleScopeAsync`** (3 production call sites). Same reason.
3. **`BYPASSRLS` is verified and repaired, not merely created.** Beyond the brief, justified in §2.
4. **The unconditional maintenance GRANT** (rather than mirroring `iverson_runtime`'s conditional
   one). Required for correctness; justified in §2 and pinned by a test.
5. **Eleven more call sites migrated than the brief listed.** Not optional — FORCE would have broken
   every one of them in kubernetes.
6. **`ObjectMappingGrpcService.Update` and `ObjectPersistenceGrpcService.Update` were left
   cross-tenant.** These are externally reachable and were *not* in the brief's list of
   already-correct sites, so I had to make a judgment. Both read the existing row only to feed
   `AuthorizationFieldMasking.EnforceWriteAuthorization`, which itself compares the row's
   tenant/owner values against the acting user's and **denies on mismatch**. Narrowing the read to
   the acting tenant would convert an explicit, audit-logged authorization denial into a silent
   "no existing row" — a *less* auditable outcome, and a behaviour change on a currently-correct
   externally-reachable path, which the brief forbids. Preserved as-is and marked
   `CrossTenantMaintenance` with an in-code comment pointing here. **Flagged as a follow-up
   candidate** — the right change is probably tenant-scoping the read *and* keeping the denial
   explicit, which is a behaviour design question, not a mechanical one.
7. **`docs/security/tma.md` was not updated.** It is untracked in this worktree (created by another
   batch or the TMA run) and its line 104/106 mitigation text now understates what is in place
   (`FORCE`, plus the `iverson_maintenance` exemption). I did not touch or commit another batch's
   untracked artifact. **Recommend the orchestrator update it.**

---

## 6. Two judgment calls worth a reviewer's eye

**`EnrichmentConsumer`'s post-commit republish read was narrowed to `ForTenant(tenantValue)`.** This
is the only site where I tightened rather than preserved. It is provably equivalent: `tenantValue`
was extracted from that row's own tenant column earlier in the same method, and the `UPDATE` that
immediately precedes this read ran under `EnterTenantScopeAsync(tenantValue)` and matched — so the
RLS predicate is satisfied by construction. Leaving it cross-tenant would have been a pointless
BYPASSRLS on a read whose tenant is known and already proven. Covered by the existing
`EnrichmentConsumerTests` (green).

**`decision.TenantColumn is null` now maps to `CrossTenantMaintenance`, not to the ambient role.**
At `ObjectRetrievalGrpcService.GetMany` and `ObjectMappingGrpcService.Delete`, the old
`tenantScoped: decision.TenantColumn is not null` meant "no tenant column ⇒ no role switch". Those
tables carry no RLS, so the ambient role was harmless — but it is also the pattern the finding is
about, so I mapped it to the maintenance role instead. It is truthful (a read of a table with no
tenant column *is* cross-tenant), it is why the maintenance GRANT had to be unconditional, and the
in-transaction `DELETE` arm still resets the role before the outbox write in the same transaction —
covered by the existing regression test
`DeleteAsync_TenantScoped_ThenPlumbingTableInsert_InSameTransaction_Commits`, which is green.

---

## 7. Concerns / residuals

1. **Deploy-time operator action (kubernetes, existing clusters).** `postInitApplicationSQL` runs
   only at initdb. An already-provisioned cluster needs, as a superuser:
   ```sql
   CREATE ROLE iverson_maintenance NOLOGIN BYPASSRLS;
   GRANT iverson_maintenance TO iverson;
   ```
   Startup fails with an explicit message naming these if they are missing. The same limitation
   already existed for `iverson_runtime` and is documented in the chart, but this one is new and
   **will bite on the next deploy to an existing cluster.**

2. **The write path can still land on the connection's role.** `OutboxWriter
   .UpsertAndEnqueueOutboxAsync` enters tenant scope on `tenantId is not null`, not on
   `schema.TenantColumn is not null`. A tenant-carrying schema written with a null tenant id would
   run the `INSERT` on the ambient role: rejected by the FORCE'd policy's `WITH CHECK` in
   kubernetes, but still accepted under a superuser connection (compose/tests). Unreachable in
   practice — the mandatory tenant boundary denies a principal with no `tenant_id` claim before
   this point — and it is a write of an *untenanted* row, not a cross-tenant leak. Left alone
   because it is a write-path behaviour change outside this finding. **Follow-up candidate:** switch
   the condition to `schema.TenantColumn is not null` so it fails closed uniformly.

3. **`ObjectMappingGrpcService.Update` / `ObjectPersistenceGrpcService.Update`** — see §5.6. These
   are the only externally reachable reads that see across tenants. Deliberate and unchanged, but a
   reviewer should agree with the reasoning rather than assume it.

4. **Assertion strength is uneven.** The bulk test migration was mechanical, so many
   `Received(...)` assertions now pass `Arg.Any<EntityAccess>()` where they previously pinned the
   implicit `false`. I tightened the four highest-value ones (retrieval Get/GetMany, reconciliation,
   re-render worker) with new dedicated tests, but the consumer classes
   (`EngagementStoreConsumerTests`, `IntelligenceStoreConsumerTests`, `EntityRelationResolverTests`)
   still accept any access value. Nothing regressed relative to before — those sites were never
   asserted — but pinning them would be cheap and is worth a follow-up.

5. **Not run:** `AuthentikRecoveryFlowIntegrationTests` (9), `ObjectSearchVectorIntegrationTests`
   (4), `StarRocksReadinessIntegrationTests` (2) — Authentik / Qdrant / StarRocks containers,
   unrelated to this change's surface. They compile. Everything Postgres-backed was run.

---

## 8. Files changed

**Production**
- `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` — `RecordStoreRole`, `EntityAccess`,
  `IEntityRepository` signatures, `EnsureRolesAsync`, `EnterMaintenanceScopeAsync`/`ExitRoleScopeAsync`
- `Iverson.Server/Iverson.Sql/PostgresRepository.cs` — `RunAsRoleAsync`, `db.role` activity tag
- `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` — `FORCE`, maintenance GRANT, `EnsureRolesAsync`
- `Iverson.Server/Iverson.Sql/EntityRepository.cs`, `OutboxWriter.cs`
- `Iverson.Server/Iverson.Api/Grpc/` — `ObjectRetrievalGrpcService`, `ObjectMappingGrpcService`,
  `ObjectPersistenceGrpcService`, `EntityRelationResolver`
- `Iverson.Server/Iverson.Api/Consumers/` — `DocumentRenderer`, `DocumentRerenderConsumer`,
  `EngagementStoreConsumer`, `EnrichmentConsumer`, `IntelligenceStoreConsumer`
- `Iverson.Server/Iverson.Api/Reconciliation/` — `ReconciliationService`, `DocumentRerenderQueueWorker`
- `Iverson.Server/Iverson.Api/Program.cs` — startup-ordering comment
- `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml`

**Tests** — 17 files across `Iverson.Api.Tests` and `Iverson.Sql.Tests`; the substantive ones are
`Iverson.Sql.Tests/TenantScopedAccessIntegrationTests.cs`, `PostgresIntegrationTests.cs`,
`EntityRepositoryTests.cs`, and the three call-site pins in
`ObjectRetrievalGrpcServiceTests.cs` / `ReconciliationServiceTests.cs` /
`DocumentRerenderQueueWorkerTests.cs`.
