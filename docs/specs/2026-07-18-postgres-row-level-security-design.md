# Postgres Row-Level Security (Part B1) — Design Spec

**Status:** Draft, pending user review
**Date:** 2026-07-18
**Part of:** Tenant Data Isolation & Compliance Readiness initiative, Part B (DB-level defense-in-depth), sub-part B1 of 3 — see [Scope and decomposition](#scope-and-decomposition)

## Motivation

Part A (`2026-07-17-mandatory-tenant-boundary-design.md`) made every registered schema's tenant boundary mandatory and enforced it at the application layer — in the gRPC service handlers, the row/field evaluator, and each store's query-construction code. That enforcement is real, but it is **entirely a property of correctly-written application code**: a future bug in a query builder, a missed call site in a new code path, or a regression in the evaluator could silently reintroduce a cross-tenant leak, with nothing beneath the application layer to catch it.

This spec adds a second, structurally independent enforcement layer for Postgres specifically: Row-Level Security (RLS) policies that Postgres itself evaluates on every query, regardless of what SQL the application generates. If application-layer enforcement has a bug, the database still refuses to return or modify another tenant's rows.

This does not replace or duplicate Part A's checks — those are unchanged. It is a backstop, not a substitute.

## Scope and decomposition

Part B ("DB-level defense-in-depth") was originally conceived as one part covering all three data stores (Postgres, StarRocks, Qdrant). During brainstorming it became clear the three stores have fundamentally different native capabilities for this:

- **Postgres** has native Row-Level Security — a well-understood, standard mechanism.
- **Qdrant** (v1.9+) has native JWT-based RBAC where a token's `access` claim can carry a payload filter that Qdrant itself enforces, independent of the app's own query.
- **StarRocks** (the v4.1 line this deployment runs) has **no native row-level security**. The only documented path is Apache Ranger integration — a separate policy-management service with zero footprint in this codebase today. Worse, Ranger's row-filter policies key off the *connecting database identity*, and Iverson's app currently authenticates to StarRocks with one shared service credential for every request — making a real Ranger integration require a foundational redesign of how the app authenticates to StarRocks (per-tenant DB principals or impersonation), not just standing up Ranger.

Given that asymmetry, Part B was split into three independently-specced, independently-implementable sub-parts:

- **B1 (this spec)** — Postgres RLS.
- **B2 (future)** — Qdrant JWT-based payload-filter RBAC.
- **B3 (future)** — StarRocks: Apache Ranger integration plus the per-tenant StarRocks authentication redesign it depends on. Substantially larger and differently-shaped than B1/B2; treated as its own initiative-level project.

Explicitly out of scope for **this** spec: B2, B3; backfilling tenant *values* onto existing rows (Part A's job); any change to Part A's application-level enforcement, which is unchanged by this design; StarRocks and Qdrant defense-in-depth.

## Design

### The core mechanism

Postgres RLS policies are attached per table and evaluated by Postgres itself on every `SELECT`/`INSERT`/`UPDATE`/`DELETE`, independent of the application's own `WHERE` clauses. A policy's `USING` expression compares the row's tenant column against a session-scoped setting the application sets before running its query:

```sql
CREATE POLICY <table>_tenant_isolation ON "<table>"
USING ("<TenantColumn>" = current_setting('app.tenant_id', true));
```

`current_setting(name, true)` returns `NULL` when the setting was never set (verified against Postgres docs, not assumed) — and a `NULL` result in a `USING` clause excludes the row rather than including it (Postgres's row-security implementation treats `NULL` as `FALSE`, an explicitly documented "deny by default" behavior). So if application code ever forgets to set the session tenant value, the database returns **zero rows** rather than an unfiltered set — matching Part A's own deny-by-default posture.

### Why a new database role is required

Postgres superusers and roles carrying `BYPASSRLS` **unconditionally bypass RLS** — no policy, and no `ALTER TABLE ... FORCE ROW LEVEL SECURITY`, can ever override this (confirmed against official Postgres docs; `FORCE ROW LEVEL SECURITY` only binds table *owners*, and has no effect on an actual superuser).

Iverson's Postgres role today, `iverson`, is created via the official Postgres image's `POSTGRES_USER` environment variable — which grants full superuser privileges (confirmed: this is documented image behavior, not configurable down). That same role both owns every table (via `PostgresSchemaManager`'s DDL) and runs every runtime query (via `PostgresRepository`). Since a superuser can never be bound by RLS, genuine defense-in-depth requires a **second, non-superuser role** used specifically for the tenant-scoped runtime-query path.

- `iverson` continues to own tables and run all DDL (`PostgresSchemaManager.ApplySchemaAsync`, invoked from `RegisterSchema` and at startup) — RLS bypass for schema administration is expected and is not a security concern the same way runtime-query bypass would be.
- A new role, `iverson_runtime` — not a superuser, not a table owner — is used only for the interactive, acting-user-driven request path: reads and writes on user-registered entity tables.

**Role creation is app-managed.** `iverson_runtime` doesn't exist by default — something must create it before any `GRANT`/`SET ROLE` referencing it can succeed. Since Postgres has no `CREATE ROLE IF NOT EXISTS`, this follows the same idempotent, app-managed DDL pattern already used for tables (`PostgresSchemaManager`'s `CREATE TABLE IF NOT EXISTS`): a guarded `CREATE ROLE iverson_runtime NOLOGIN` (check `pg_roles` first, or catch the "already exists" error) runs once at server startup, before any other Postgres DDL — including the two fixed internal tables and the rollout self-heal loop below — so the role is guaranteed to exist for the lifetime of the process before any `RegisterSchema` call or startup DDL can reference it.

**No second connection string or credential is needed.** Postgres supports `SET LOCAL ROLE <role>` — a transaction-scoped role switch (confirmed valid syntax, confirmed to auto-revert at transaction end exactly like `SET LOCAL` for ordinary settings) — and a superuser can `SET ROLE` to any role in the database without needing explicit membership (confirmed against official docs: *"If the session user is a superuser, any role can be selected"*). After `SET ROLE`, Postgres evaluates all subsequent permission checks — including RLS — against the new role, not the original login role (confirmed: *"permissions checking for SQL commands is carried out as though the named role were the one that had logged in originally"*). So the existing `iverson` connection, mid-transaction, can `SET LOCAL ROLE iverson_runtime` to become genuinely RLS-bound for the rest of that transaction, then automatically revert at commit/rollback — no new secret to provision in Helm or docker-compose.

### Per-table privilege and policy model

Every Postgres-backed table for a user-registered type with a non-null `TenantColumn` (i.e., every type registered under Part A's mandatory-`tenant_field` model) gets, at DDL time:

1. `CREATE POLICY <table>_tenant_isolation ON "<table>" USING ("<TenantColumn>" = current_setting('app.tenant_id', true))`
2. `ALTER TABLE "<table>" ENABLE ROW LEVEL SECURITY` (no `FORCE` needed — `iverson_runtime` is neither the table's owner nor a superuser, so plain `ENABLE` already binds it)
3. `GRANT SELECT, INSERT, UPDATE, DELETE ON "<table>" TO iverson_runtime`

Legacy tables with no `TenantColumn` (pre-Part-A-cutover, already denied at the application layer) get **no grant at all** to `iverson_runtime` — so even a hypothetical application-layer bug for a legacy schema would hit a hard Postgres permission error rather than silently returning unfiltered rows.

Internal plumbing tables — the transactional outbox (`IversonReconciliationQueue`), the DLQ (`IversonDlqMessages`), and the schema registry (`_iverson_schema`) — are not tenant-partitioned (confirmed by reading their table definitions directly: none has a tenant column) and never went through Part A's per-tenant enforcement either. They get no RLS policy. They also don't need any grant to `iverson_runtime`, because (per "Query execution" below) the code paths that touch them run entirely under the unswitched `iverson` role.

### Schema-registration wiring

`PostgresSchemaManager.ApplySchemaAsync` is invoked once per registered type in a loop over `[RootType].Concat(Dependents)` (confirmed by reading `SchemaRegistrationOrchestrator.RegisterAsync` directly — this already covers root and joined/dependent types, not just the root). The three DDL statements above are added to this same method, gated on the table's `TenantColumn` being non-null.

**A concrete, necessary addition surfaced during verification:** the `TableSchema` record `ApplySchemaAsync` receives has no tenant-column field today (confirmed by reading `IRecordStoreRoles.cs` and `SchemaBuilder.ToTableSchema` directly — the record carries only `TableName`, `KeyColumn`, and `Columns`). `TableSchema` gains a `TenantColumn` field, populated from `SchemaDescriptor.TenantColumn` in `ToTableSchema`, so `ApplySchemaAsync` has what it needs.

**Idempotency:** Postgres has no `CREATE POLICY IF NOT EXISTS` (confirmed — not supported). Re-registration of an already-registered type is already a normal, supported path today, handled entirely through `ApplySchemaAsync`'s existing `CREATE TABLE IF NOT EXISTS`/`ADD COLUMN IF NOT EXISTS` idioms (confirmed by reading `PostgresSchemaManager.cs` — there is no separate "first registration only" branch anywhere in the registration path). The new DDL follows the same idempotent spirit: query `pg_policies` (or `DROP POLICY IF EXISTS` + `CREATE POLICY`) before creating, so re-registering a type doesn't error.

### Query execution: which call sites switch roles

The dividing line is **caller context, not repository class** — the interactive, acting-user-driven request path switches to `iverson_runtime`; background/system processes that legitimately need cross-tenant visibility must not be tenant-restricted, regardless of which repository interface they happen to call through. Grepping every consumer of `IEntityRepository` directly (not just the ones an initial pass names) finds 7, not 4:

- **Switches to `iverson_runtime`:** reads in `ObjectRetrievalGrpcService` (`Get`/`GetMany`), `ObjectMappingGrpcService`'s `Get`, `EntityRelationResolver`'s relation-traversal reads, and the actual write/delete statements — `ObjectMappingGrpcService.Delete`'s `DeleteAsync`, `ObjectPersistenceGrpcService`'s and `ObjectMappingGrpcService`'s entity-table statement inside `OutboxWriter.UpsertAndEnqueueOutboxAsync` — all of which run after authorization has already succeeded.
- **Stays on plain `iverson`:** `DlqRepository`, `ReconciliationQueueRepository`, `SchemaRegistryRepository`, `OutboxWriter`'s own outbox-table statement, and three background `IEntityRepository` consumers with no acting-user/tenant context at all — `ReconciliationService.FetchAllAsync` (full-type reconciliation sweeps across every tenant) and `IntelligenceStoreConsumer`/`EngagementStoreConsumer` (Kafka event consumers rebuilding projections from events across all tenants). All are cross-tenant system/background operations Part A never touched either; switching any of them to `iverson_runtime` would give them no tenant value to set, and RLS's fail-closed behavior would silently reduce every one of their operations to zero rows.
- **Stays on plain `iverson`, as a specific carve-out:** the pre-existing-row fetch in `Update` (both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService`) — it must see the true row regardless of tenant. `AuthorizationFieldMasking.EnforceWriteAuthorization` (`AuthorizationFieldMasking.cs:41-52`) treats a null `existingRowJson` as "no pre-existing row," force-stamping the caller's own tenant and proceeding to an upsert. If this fetch were tenant-scoped, a row that exists under a *different* tenant would become invisible — reclassifying "wrong tenant" as "doesn't exist yet" and turning today's clean `PermissionDenied` into a likely unhandled duplicate-key exception when the upsert collides with the physically-existing row. `Delete`'s equivalent pre-check does **not** need this carve-out: a tenant-mismatched row silently becoming "not found" is already `Delete`'s and the pure-read paths' exact existing behavior, so RLS-filtering them changes nothing observable.

**Tenant-value sourcing.** At most of these call sites, the fetch runs *before* the row/field authorization decision is computed (checked directly: `ObjectRetrievalGrpcService.Get`, `ObjectMappingGrpcService.Get`/`Update`/`Delete`'s initial check, `ObjectPersistenceGrpcService.Update`, and all 3 `EntityRelationResolver` methods fetch first; only `ObjectRetrievalGrpcService.GetMany` computes the decision first). So the tenant value passed into the session-scoping mechanism below cannot come from `AuthorizationDecision.TenantValue` at those sites — it isn't in scope yet. Instead, it's sourced directly from `schema.TenantColumn` (already known — a property of the already-loaded `SchemaDescriptor`) and the caller's raw `tenant_id` claim read directly off `actingUser` (available before `Evaluate()` runs) — both are available at every call site regardless of ordering. `decision.TenantValue` remains the source only where a decision has genuinely already been computed: `GetMany`, and the actual write/delete statements that run after authorization has already succeeded.

`EntityRepository`'s five methods (`FetchByKeyAsync`, `FetchManyByKeysAsync`, `FetchByColumnAsync`, `FetchAllAsync`, `DeleteAsync`) take no tenant parameter today (confirmed by reading `EntityRepository.cs` directly) — each gains one, sourced as described above.

**Session-variable safety under connection pooling.** `PostgresRepository` pools connections via the Npgsql default (confirmed: plain `new NpgsqlConnection(connectionString)`, no pooling override). Only 1 of its 4 connection-acquisition points (`ExecuteInTransactionAsync`) currently runs inside an explicit transaction — the other 3 (`QueryAsync`, `ExecuteAsync`, `QuerySingleOrDefaultAsync`), covering all reads plus several writes, are single ad-hoc statements. Setting the role/tenant context safely on a pooled connection requires an explicit transaction, since only `SET LOCAL`-scoped state (including a role set via `SET LOCAL ROLE`) is guaranteed by Postgres itself to auto-revert at transaction end regardless of success or failure — so `QueryAsync`/`ExecuteAsync`/`QuerySingleOrDefaultAsync` gain explicit `BEGIN`/`COMMIT` wrapping around the tenant-scoped path specifically (not the unrestricted plumbing path, which is unaffected).

**Setting the tenant value safely.** Postgres's `SET`/`SET LOCAL` statements take a literal in the SQL text — unlike ordinary `SELECT`/`INSERT`/`UPDATE`/`DELETE`, they do not accept a bound parameter the way every other statement in this codebase does via Dapper. Embedding the tenant claim value as a string literal directly into SQL text would be inconsistent with, and undermine, the parameterized-query discipline the rest of the codebase follows. Instead, the tenant value is set via Postgres's `set_config` function, called as an ordinary parameterized statement exactly like every other Dapper call in this codebase (the role switch itself, `SET LOCAL ROLE iverson_runtime`, uses no interpolated value — it's a fixed literal — so it's unaffected).

Each tenant-scoped query now runs as:

```sql
BEGIN;
SET LOCAL ROLE iverson_runtime;
SELECT set_config('app.tenant_id', @value, true);
<original statement>;
COMMIT;
```

**A real wrinkle this solves cleanly:** `OutboxWriter.UpsertAndEnqueueOutboxAsync` writes to both the tenant-scoped entity table and the cross-tenant outbox table in one transaction (confirmed by reading `OutboxWriter.cs` directly — both statements run against the same `IDbTransactionContext`). `ObjectMappingGrpcService.Delete` does the same for its entity-delete + outbox-delete-enqueue pair (confirmed by reading `ObjectMappingGrpcService.cs` directly). Because `SET (LOCAL) ROLE` can be issued more than once within a transaction, each of these transactions does `SET LOCAL ROLE iverson_runtime` + the entity-table statement, then `RESET ROLE` before the outbox-table statement — both statements land in one transaction, each under the correct privilege level.

### Rollout for already-registered tables

Unlike Part A's checks (evaluated per-request, so they apply immediately regardless of when a type was registered), RLS is DDL — it only exists on a table if the `CREATE POLICY`/`GRANT` statements actually ran against it. A type registered before this feature ships has a `TenantColumn` but no RLS policy on its physical table.

The server already loads every registered type's descriptor into an in-memory cache at startup via `SchemaRegistry.LoadAsync()` (confirmed: `Program.cs:332`, immediately followed by two hard-coded `ApplySchemaAsync` calls for the two fixed internal tables only — confirmed there is **no existing loop that reapplies schema DDL for user-registered types** at boot today, correcting an earlier, looser assumption). This design adds a new loop, immediately after `LoadAsync()` populates the cache, that calls the now-RLS-aware `ApplySchemaAsync` for every loaded descriptor — self-healing every existing table's RLS state on every server start, without a manual per-type re-registration step.

### Out of scope, but worth documenting explicitly

`Iverson.LoadTest` has several call sites (`DirectSeeder`, `ReadPathScenario`, `WritePathRunner`) that construct their own `NpgsqlConnection` directly against the same connection string, bypassing the application's gRPC API, `PostgresRepository`, and this design's role-switching entirely — for raw Postgres throughput benchmarking, a deliberate and separate LoadTest mode, not a simulated production caller. These are unaffected by, and irrelevant to, this design's actual defense-in-depth guarantee for the production request path; noted here so it isn't mistaken for a gap.

## Verified assumptions

| Assumption | Verification | Result |
|---|---|---|
| Superusers/`BYPASSRLS` roles unconditionally bypass RLS; `FORCE ROW LEVEL SECURITY` cannot override this for a true superuser | WebFetch + WebSearch against postgresql.org docs | Confirmed. `FORCE` only binds table owners without `BYPASSRLS`. |
| `current_setting(name, true)` returns `NULL` when the setting doesn't exist, instead of erroring | WebFetch postgresql.org `functions-admin.html` | Confirmed. |
| A `NULL` `USING`-clause result excludes the row (fail-closed), not includes it | WebSearch, multiple sources describing Postgres RLS's three-valued-logic handling | Confirmed — explicitly described as the deny-by-default behavior. |
| A non-owner, non-superuser role is bound by plain `ENABLE ROW LEVEL SECURITY` without needing `FORCE` | Derived from the superuser/owner-only scope of `FORCE`, confirmed above | Confirmed. |
| `SET LOCAL ROLE` is valid syntax and auto-reverts at transaction end like `SET LOCAL` for GUCs | WebFetch postgresql.org `sql-set-role.html` | Confirmed: *"The SESSION and LOCAL modifiers act the same as for the regular SET command."* |
| A superuser can `SET ROLE` to any role without explicit membership | WebFetch postgresql.org `sql-set-role.html` | Confirmed: *"If the session user is a superuser, any role can be selected."* |
| Permission checks (incl. RLS) after `SET ROLE` evaluate against the new role, not the original login role | WebFetch postgresql.org `sql-set-role.html` | Confirmed: *"permissions checking...carried out as though the named role were the one that had logged in originally."* |
| `iverson`'s connection-string role is a genuine Postgres superuser | WebSearch on official `postgres` Docker image `POSTGRES_USER` behavior + read `docker-compose.yml:34-36` | Confirmed — `POSTGRES_USER` grants full superuser privileges by documented image behavior. |
| Plumbing tables (outbox, DLQ, schema registry) have no tenant column | Read `ReconciliationSchema.cs`, `DlqSchema.cs`, `SchemaRegistryRepository.cs` directly | Confirmed — none defines a tenant-related column; explicitly documented in-code as internal infrastructure, not user entities. |
| `ObjectMappingGrpcService.Delete` and `OutboxWriter.UpsertAndEnqueueOutboxAsync` each run an entity-table statement and a plumbing-table statement in one transaction | Read `ObjectMappingGrpcService.cs:177-229`, `OutboxWriter.cs:15-53` directly | Confirmed. |
| `PostgresSchemaManager.ApplySchemaAsync` covers root and joined/dependent types, not just the root | Read `SchemaRegistrationOrchestrator.cs:33-59` directly | Confirmed — single call site, inside `foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))`. |
| Postgres has no `CREATE POLICY IF NOT EXISTS`; `pg_policies` is queryable for an existence guard | WebSearch against postgresql.org docs | Confirmed. |
| Re-registration of an already-registered type is already supported today | Read `SchemaRegistrationOrchestrator.cs` (no "already exists" branch) + `PostgresSchemaManager.cs` (idempotent `IF NOT EXISTS` DDL throughout) | Confirmed — no special-case code exists or is needed; the existing idempotent DDL style already handles it. |
| `EntityRepository`'s methods take no tenant parameter today | Read `EntityRepository.cs`, `IRecordStoreRoles.cs` directly | Confirmed — `FetchByKeyAsync`/`FetchManyByKeysAsync`/`FetchByColumnAsync`/`FetchAllAsync`/`DeleteAsync` all lack one; each needs the new parameter. |
| Exact set of gRPC-layer call sites that consume `EntityRepository` directly | Grepped every `entities.*`/`_entities.*` invocation across all 4 gRPC services + `EntityRelationResolver` | Confirmed 4 direct consumers: `ObjectRetrievalGrpcService`, `ObjectMappingGrpcService`, `ObjectPersistenceGrpcService`, `EntityRelationResolver`. **Corrected an imprecision in an earlier draft of this spec** — `AuthorizationFieldMasking` itself never calls `EntityRepository`; the gRPC service fetches the row first and passes the result to `AuthorizationFieldMasking.EnforceWriteAuthorization` for comparison. `ObjectPersistenceGrpcService` was initially omitted from the consumer list and has been added. |
| `TableSchema` carries tenant-column information already | Read `IRecordStoreRoles.cs`, `SchemaBuilder.ToTableSchema` directly | **Wrong** — `TableSchema` has no tenant field at all. Corrected: added as a concrete, necessary change in this spec. |
| An existing generic startup mechanism already reapplies schema DDL for all registered user types | Read `Program.cs:332-334` directly | **Overstated** — only the 2 fixed internal tables get reapplied at boot; `SchemaRegistry.LoadAsync()` loads all user-type descriptors into memory but does not currently call `ApplySchemaAsync` for them. Corrected: this design adds a new loop after `LoadAsync()`, not an extension of existing reapplication behavior. |
| Only `PostgresRepository`/`PostgresSchemaManager` construct `NpgsqlConnection` directly anywhere in the repo | Repo-wide `grep` for `new NpgsqlConnection` | **Wrong** — `Iverson.LoadTest` has several additional direct-connection call sites for its own throughput-benchmarking mode. Documented explicitly as out of scope above; does not affect the production defense-in-depth guarantee. |
| A real Postgres integration-test fixture already exists and can exercise multi-role/RLS behavior | Read `Iverson.Sql.Tests/PostgresIntegrationTests.cs` directly | Confirmed — `PostgresContainerFixture` spins up a real `postgres:16-alpine` Testcontainers instance; directly reusable. |
| Npgsql/Dapper can execute arbitrary raw SQL through `conn.ExecuteAsync(sql)` with no special restriction | Read `PostgresSchemaManager.cs` directly — it already executes raw multi-keyword DDL (`CREATE TABLE`, `ALTER TABLE`) through this exact path | Confirmed as a general fact, but **does not extend** to "`SET`/`SET LOCAL` accepts a bound parameter the way DML does" — that's a distinct, false claim this spec's first draft implicitly leaned on (critical-design-review round 1, finding 2.4). Postgres's `SET`/`SET LOCAL` require a literal in the SQL text; the design now uses the parameterizable `set_config()` function instead for the tenant value, avoiding string interpolation. |

## Testing approach

- Integration tests (Testcontainers, extending the existing `PostgresContainerFixture` pattern) proving, against a real Postgres instance: a raw query as `iverson_runtime` for tenant A cannot see tenant B's rows, independent of any application-level `WHERE` clause — this proves the database backstop itself, not application logic.
- An unset `app.tenant_id` session variable returns zero rows (fail-closed), confirming the deny-by-default property.
- A legacy (no-`TenantColumn`) table has no grant to `iverson_runtime` at all — a raw connection as that role gets a Postgres permission error, not an empty result.
- `OutboxWriter`'s combined transaction (entity write under `iverson_runtime`, `RESET ROLE`, outbox insert under `iverson`) completes correctly and each statement is subject to the privilege level it should be.
- Re-registering an already-registered type does not error on the new `CREATE POLICY`/`GRANT` DDL (idempotency).
- The startup self-heal loop applies RLS to a table that was registered under a pre-B1 build (simulating an upgrade).
