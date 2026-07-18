# Postgres Row-Level Security (Part B1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-18-postgres-row-level-security-design.md` (commit SHA: `eec0867`)

**Goal:** Add Postgres Row-Level Security as a second, structurally independent tenant-isolation layer beneath Part A's application-level enforcement, so a future application-code bug cannot silently reintroduce a cross-tenant leak.

**Architecture:** A new non-superuser `iverson_runtime` role (created idempotently by the app at startup) gets an RLS policy + grant on every tenant-scoped table. The interactive, acting-user-driven request path switches to this role via a transaction-scoped `SET LOCAL ROLE` + `set_config`; background/system processes and `Update`'s pre-write existence check stay on the existing `iverson` role.

**Tech stack:** Postgres 16 (RLS, `set_config`/`current_setting`, `SET LOCAL ROLE`), Npgsql/Dapper, the existing `IRecordStoreQueryExecutor`/`IEntityRepository`/`IOutboxWriter` abstractions in `Iverson.Sql`.

---

## Global Constraints

- **Additive only.** This does not change Part A's application-level enforcement, which stays exactly as-is. RLS is a backstop, evaluated by Postgres independently of the app's own checks.
- **No second connection string or credential.** All tenant-scoping uses `SET LOCAL ROLE`/`set_config` on the existing `iverson` connection — never a new secret in Helm or docker-compose.
- **Fail-closed.** An unset `app.tenant_id` session value must exclude all rows (`current_setting(name, true)` → `NULL` → row excluded), never fall through to an unfiltered result.
- **Non-breaking to existing callers.** Every interface signature change in this plan (`IRecordStoreQueryExecutor`, `IEntityRepository`, `IOutboxWriter`) is an added parameter with a `null`/default value — existing callers that don't know about tenant scoping (`DlqRepository`, `ReconciliationQueueRepository`, `SchemaRegistryRepository`, the `/health` and `/probe/sql` endpoints) require zero code changes.
- **Commit convention:** matches this repo's existing plan/commit style (lowercase imperative summary, no Conventional-Commits prefix), consistent with prior plans in `docs/plans/`.

## File Structure

**Modify:**
- `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` — `TableSchema` gains `TenantColumn`; `IEntityRepository`'s 5 methods and `IRecordStoreQueryExecutor`'s 3 ad-hoc methods gain a `bool tenantScoped` parameter (whether the call switches roles at all) alongside a nullable `string? tenantId` value (what to set the session to); `IOutboxWriter.UpsertAndEnqueueOutboxAsync` gains only a nullable `tenantId` parameter (its callers are always post-authorization, where the ambiguity the `tenantScoped` split resolves doesn't arise).
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — `ToTableSchema` populates the new field.
- `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` — idempotent `iverson_runtime` role bootstrap; `CREATE POLICY`/`ENABLE ROW LEVEL SECURITY`/`GRANT` added to `ApplySchemaAsync`.
- `Iverson.Server/Iverson.Sql/PostgresRepository.cs` — `QueryAsync`/`ExecuteAsync`/`QuerySingleOrDefaultAsync` wrap in `BEGIN`/`SET LOCAL ROLE`/`set_config`/`COMMIT` when a tenant value is supplied.
- `Iverson.Server/Iverson.Sql/EntityRepository.cs` — 5 methods thread the new parameter through.
- `Iverson.Server/Iverson.Sql/OutboxWriter.cs` — `UpsertAndEnqueueOutboxAsync` issues the role/GUC sequence around its two statements.
- `Iverson.Server/Iverson.Api/Program.cs` — role-bootstrap call before existing startup DDL; new self-heal loop after `SchemaRegistry.LoadAsync()`.
- `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, `ObjectMappingGrpcService.cs`, `ObjectPersistenceGrpcService.cs`, `EntityRelationResolver.cs` — thread the correct tenant value at each call site.
- `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs`, `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`, `EngagementStoreConsumer.cs` — pass `null` explicitly.

**Test:**
- `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs` (or a new sibling file in the same project) — extends the existing `PostgresContainerFixture` (`postgres:16-alpine`, matching production's `postgres:16`).

## Inherited from spec

The following were verified by `thorough-brainstorming` (across two `critical-design-review` rounds) and are NOT re-verified here:

- Postgres superusers/`BYPASSRLS` roles unconditionally bypass RLS; `FORCE ROW LEVEL SECURITY` cannot override this for a true superuser.
- `current_setting(name, true)` returns `NULL` when unset; a `NULL` `USING`-clause result excludes the row (fail-closed).
- `SET LOCAL ROLE` is valid syntax, auto-reverts at transaction end; a superuser can `SET ROLE` to any role without membership; permission checks (incl. RLS) after `SET ROLE` evaluate against the new role.
- `iverson`'s connection-string role is a genuine Postgres superuser (`POSTGRES_USER` on the official image).
- Plumbing tables (`IversonReconciliationQueue`, `IversonDlqMessages`, `_iverson_schema`) have no tenant column and are outside Part A's tenant model.
- `PostgresSchemaManager.ApplySchemaAsync` covers root and joined/dependent types via a loop in `SchemaRegistrationOrchestrator.RegisterAsync`.
- Postgres has no `CREATE POLICY IF NOT EXISTS`; `pg_policies` is queryable for an existence guard.
- Re-registration of an already-registered type is already idempotent via `CREATE TABLE IF NOT EXISTS`/`ADD COLUMN IF NOT EXISTS`.
- The exact 7 consumers of `IEntityRepository` (4 interactive + 3 background), and the fetch-vs-decision ordering at each of the 9 read/pre-write call sites (8 fetch-before-decision, 1 — `GetMany` — decision-before-fetch).
- `EnforceWriteAuthorization`'s null-`existingRowJson` semantics (`AuthorizationFieldMasking.cs:41-52`) and why `Update`'s pre-check must stay on plain `iverson`.
- `set_config('app.tenant_id', @value, true)` is the correct parameterizable mechanism (not `SET LOCAL app.tenant_id = '<value>'`, which cannot be parameterized).
- `OutboxWriter.UpsertAndEnqueueOutboxAsync` and `ObjectMappingGrpcService.Delete` each run an entity-table statement and a plumbing-table statement in one `ExecuteInTransactionAsync` transaction, requiring `SET LOCAL ROLE`/`RESET ROLE` mid-transaction via `IDbTransactionContext.ExecuteAsync`.
- No existing generic startup loop reapplies schema DDL for user-registered types; `SchemaRegistry.LoadAsync()` loads all descriptors into memory but doesn't call `ApplySchemaAsync` for them today.
- `Iverson.LoadTest`'s direct-`NpgsqlConnection` call sites (`DirectSeeder`, `ReadPathScenario`, `WritePathRunner`) are a separate benchmarking mode, out of scope.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | All 15 files this plan touches exist at the stated paths | `ls`/existence check on each; all present |
| 2 | Function signature | `SchemaBuilder.ToTableSchema` (`SchemaBuilder.cs:116-119`) maps only `TableName`/`KeyColumn`/`ScalarColumns` today | Read directly |
| 3 | Function signature | `IDbTransactionContext.ExecuteAsync(string sql, object? param = null)` (`IRecordStoreRoles.cs:23`) accepts arbitrary parameterized SQL — the exact primitive Task 2 needs for issuing `SET LOCAL ROLE`/`set_config`/`RESET ROLE` inline | Read directly |
| 4 | Convention match | The established pattern for reading a raw claim off `actingUser` is `actingUser.FindFirst("tenant_id")?.Value`, already used in `RowFieldAuthorizationEvaluator.cs:20` | Read directly — Task 3's pre-decision call sites mirror this exact expression |
| 5 | Ordering (fetch vs. decision) | `ObjectRetrievalGrpcService.Get` (fetch:32, decision:39), `.GetMany` (decision:77, fetch:87), `ObjectMappingGrpcService.Get` (fetch:68, decision:79), `.Update` (fetch:153, `EnforceWriteAuthorization`:154), `.Delete` (fetch:184, decision:193, transaction:211), `ObjectPersistenceGrpcService.Update` (fetch:80, `EnforceWriteAuthorization`:81), `EntityRelationResolver`'s 3 methods (fetch before decision in all 3) | Re-read every file fresh at plan-write time; matches spec exactly, confirmed unchanged via `git log` (no commits touched any of these files since `8a3633c`, well before spec-writing) |
| 6 | Exact call sites | `ReconciliationService.FetchAllAsync` at `ReconciliationService.cs:31`; `IntelligenceStoreConsumer`/`EngagementStoreConsumer`'s `FetchByKeyAsync` at `IntelligenceStoreConsumer.cs:236`/`EngagementStoreConsumer.cs:103` | Read directly |
| 7 | Test command | `dotnet test Iverson.Server/Iverson.Sql.Tests` is a valid, working invocation | Ran `--list-tests`; succeeded, listed existing tests |
| 8 | Library/version | Production docker-compose (`Iverson.Server/docker-compose.yml:30`) runs `postgres:16`, matching the test fixture's `postgres:16-alpine` — same RLS/`set_config`/`SET ROLE` semantics apply in both | Read directly |
| 9 | Code validity | `Iverson.Sql.csproj` has `<Nullable>enable</Nullable>`; `string?` is already used in this exact interface file (`IRecordStoreRoles.cs:29`) | Read directly |
| 10 | Consumer impact (Cat 6, sibling sweep — `IRecordStoreQueryExecutor`) | Full consumer list of `QueryAsync`/`ExecuteAsync`/`QuerySingleOrDefaultAsync`: `DlqRepository`, `ReconciliationQueueRepository`, `SchemaRegistryRepository` (unaffected — pass 2 args, new 3rd param defaults to `null`), `EntityRepository`/`OutboxWriter` (Task 2's actual new usage), plus two direct calls in `Program.cs` (`/health`:251, `/probe/sql`:278) both calling `QuerySingleOrDefaultAsync<int>("SELECT 1")` with 1 arg — confirmed unaffected by the new optional 3rd parameter | Grepped every consumer of the interface repo-wide; read each call site directly |
| 11 | Consumer impact (Cat 6, sibling sweep — `IEntityRepository`) | Exactly 7 functional consumers, matching the spec exactly; no 8th consumer introduced since spec-write time | Repo-wide grep, cross-checked against spec's list |
| 12 | Consumer impact (Cat 6, sibling sweep — `IOutboxWriter`) | `OutboxPublisher.cs:39` calls `DeleteOutboxRowIfPresentAsync` (a different method, unaffected); `ReconciliationService.cs`/`ReconciliationSchema.cs` hits are doc-comment `<see cref>` references only, not code; only `ObjectMappingGrpcService`'s and `ObjectPersistenceGrpcService`'s `Post`/`Update` call `UpsertAndEnqueueOutboxAsync` | Grepped and read every hit directly |
| 13 | Consumer impact (Cat 6, implementer check) | No hand-written class implements `IRecordStoreQueryExecutor`, `IEntityRepository`, or `IOutboxWriter` other than `PostgresRepository`/`EntityRepository`/`OutboxWriter` themselves — no test fakes bypass NSubstitute's auto-adapting proxies | Grepped for `: I<Interface>` repo-wide; only the 3 known production classes matched |
| 14 | Postgres semantics | `NOLOGIN` on a role does not block `SET ROLE`/`SET LOCAL ROLE` from switching to it — it only blocks direct client authentication; a superuser can still switch to it | WebSearch against Postgres docs/community sources: *"NOLOGIN role... can be switched to via SET ROLE if the appropriate permissions have been granted"* |
| 15 | Code validity | `PostgresRepository.ExecuteInTransactionAsync`'s existing transaction structure (`CreateConnection` → `OpenAsync` → `BeginTransactionAsync` → try/commit, catch/rollback) and its positional (not named) Dapper argument convention (`conn.ExecuteAsync(sql, param, tx)`, `PostgresRepository.cs:86`) — Task 2's new tenant-scoped wrapping in `QueryAsync`/`ExecuteAsync`/`QuerySingleOrDefaultAsync` mirrors this exact shape | Read `PostgresRepository.cs:85-131` directly |

## Tasks

### Task 1: Schema/DDL foundation, role bootstrap, rollout

**Files:**
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Modify: `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs` (or sibling)

**Interfaces:**
- Produces: `TableSchema.TenantColumn` (consumed by Task 2's `ApplySchemaAsync` DDL logic — already in this task — and read nowhere else outside `Iverson.Sql`).

- [ ] **Step 1: Add `TenantColumn` to `TableSchema`.** In `IRecordStoreRoles.cs`, add a nullable `string? TenantColumn` member to the `TableSchema` record (alongside `TableName`, `KeyColumn`, `Columns`).

- [ ] **Step 2: Populate it in `SchemaBuilder.ToTableSchema`.** At `SchemaBuilder.cs:116-119`, add `d.TenantColumn` as a new argument to the `TableSchema` constructor call.

- [ ] **Step 3: Add an idempotent role-bootstrap method to `PostgresSchemaManager`.**
```csharp
public async Task EnsureRuntimeRoleAsync()
{
    await using var conn = CreateConnection();
    await conn.OpenAsync();

    var exists = await conn.QuerySingleOrDefaultAsync<bool>(
        "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'iverson_runtime')");
    if (!exists)
    {
        try
        {
            await conn.ExecuteAsync("CREATE ROLE iverson_runtime NOLOGIN");
        }
        catch (PostgresException ex) when (ex.SqlState == "42710")
        {
            // Another replica created it concurrently between our check and this CREATE
            // (this deployment runs multiple API replicas) — fine, it exists now either way.
        }
    }
}
```
Add this to `IRecordStoreSchemaManager` (`IRecordStoreRoles.cs`) so it's callable via DI, matching how `ApplySchemaAsync` is already exposed.

- [ ] **Step 4: Add the tenant DDL to `ApplySchemaAsync`.** After the existing column DDL in `PostgresSchemaManager.ApplySchemaAsync`, when `schema.TenantColumn is not null`:
```csharp
if (schema.TenantColumn is not null)
{
    var policyName = $"{schema.TableName}_tenant_isolation";
    var policyExists = await conn.QuerySingleOrDefaultAsync<bool>(
        "SELECT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = @Table AND policyname = @Policy)",
        new { Table = schema.TableName, Policy = policyName });

    if (!policyExists)
    {
        try
        {
            await conn.ExecuteAsync($"""
                CREATE POLICY "{policyName}" ON "{schema.TableName}"
                USING ("{schema.TenantColumn}" = current_setting('app.tenant_id', true))
                """);
        }
        catch (PostgresException ex) when (ex.SqlState == "42710")
        {
            // Another replica created it concurrently between our check and this CREATE — fine.
        }
    }

    await conn.ExecuteAsync($"""ALTER TABLE "{schema.TableName}" ENABLE ROW LEVEL SECURITY""");
    await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON "{schema.TableName}" TO iverson_runtime""");
}
```
(Legacy tables with `TenantColumn is null` get none of this — no policy, no grant — per the spec's hard-cutover posture.)

- [ ] **Step 5: Wire role bootstrap and the rollout self-heal loop into `Program.cs`.** Before the two existing fixed-table `ApplySchemaAsync` calls (`Program.cs:333-334`), call `EnsureRuntimeRoleAsync()`. Immediately after `SchemaRegistry.LoadAsync()` (`Program.cs:332`), add a loop over every loaded descriptor calling `ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor))` — self-healing RLS state for tables registered before this change shipped.

- [ ] **Step 6: Tests.** Using `PostgresContainerFixture`: role bootstrap is idempotent (calling `EnsureRuntimeRoleAsync` twice doesn't error); a table registered with a `TenantColumn` gets the policy, `ENABLE ROW LEVEL SECURITY`, and the grant; a table with no `TenantColumn` gets neither; re-registering an already-registered tenant-scoped type doesn't error on the new DDL; the self-heal loop applies RLS to a table simulating pre-B1 registration — construct the pre-B1 state via a raw `CREATE TABLE` (bypassing `ApplySchemaAsync`'s tenant-DDL branch entirely, so no policy/RLS/grant exist yet — this simulates a table whose descriptor already had `TenantColumn` set under Part A but whose physical DDL predates this change), then call `ApplySchemaAsync` with the real, tenant-scoped `TableSchema` and assert the policy, `ENABLE ROW LEVEL SECURITY`, and grant now exist where they didn't before.

- [ ] **Step 7: Run tests and commit.**
```bash
dotnet test Iverson.Server/Iverson.Sql.Tests
git add Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Sql.Tests
git commit -m "feat(sql): add RLS policy/role DDL and startup bootstrap for tenant-scoped tables"
```

### Task 2: Connection/transaction plumbing

**Files:**
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (continuing Task 1's file)
- Modify: `Iverson.Server/Iverson.Sql/PostgresRepository.cs`
- Modify: `Iverson.Server/Iverson.Sql/EntityRepository.cs`
- Modify: `Iverson.Server/Iverson.Sql/OutboxWriter.cs`
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs` (or sibling)

**Interfaces:**
- Consumes: `TableSchema.TenantColumn` (Task 1).
- Produces: a `bool tenantScoped` parameter (whether this call switches roles at all) alongside the nullable `string? tenantId` value (what to set the session to) on `IEntityRepository`'s 5 methods and `IRecordStoreQueryExecutor`'s 3 ad-hoc methods — separating "should switch" from "what value," so a tenant-scoped call with a missing claim still switches to `iverson_runtime` with an unset session value (RLS's fail-closed exclusion) rather than skipping the switch entirely (an unfiltered read on `iverson`). The nullable-only `tenantId` parameter on `IOutboxWriter.UpsertAndEnqueueOutboxAsync` is unaffected — its callers are always post-authorization, where `decision.TenantValue` is guaranteed non-null whenever the schema is tenant-scoped, so this ambiguity doesn't arise there. Consumed by Task 3's call sites.

- [ ] **Step 1: Add an optional `tenantScoped`/`tenantId` parameter pair to `IRecordStoreQueryExecutor`'s 3 ad-hoc methods**, defaulting to `false`/`null`. The two are separate on purpose: `tenantScoped` decides whether this call switches roles at all (`false` for background/system callers, who never should); `tenantId` is the value to set when it does switch — which can itself be `null` (an interactive, tenant-scoped caller whose claim happens to be missing still switches to `iverson_runtime`, just with an unset session value, so RLS's fail-closed exclusion applies instead of the call silently running unfiltered on `iverson`):
```csharp
Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null);
Task<int> ExecuteAsync(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null);
Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null);
```
In `PostgresRepository`'s implementations: when `tenantScoped` is `false` (the default), behavior is unchanged (today's single ad-hoc statement on a plain-opened connection, no transaction). When `tenantScoped` is `true` — regardless of whether `tenantId` itself is `null` or a real value — wrap the statement in a transaction, mirroring `ExecuteInTransactionAsync`'s existing `BeginTransactionAsync`/commit/rollback structure (`PostgresRepository.cs:102-131`) and its positional-argument Dapper convention (`conn.ExecuteAsync(sql, param, tx)`, `PostgresRepository.cs:86`):
```csharp
await using var conn = CreateConnection();
await conn.OpenAsync();
await using var tx = await conn.BeginTransactionAsync();
try
{
    await conn.ExecuteAsync("SET LOCAL ROLE iverson_runtime", null, tx);
    await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId }, tx);
    var result = await conn.QueryAsync<T>(sql, param, tx); // or ExecuteAsync/QuerySingleOrDefaultAsync as appropriate
    await tx.CommitAsync();
    return result;
}
catch
{
    await tx.RollbackAsync();
    throw;
}
```

- [ ] **Step 2: Add `tenantScoped`/`tenantId` parameters to `IEntityRepository`'s 5 methods** in `IRecordStoreRoles.cs`, and thread both into `EntityRepository`'s corresponding `sql.*Async(...)` calls (Step 1's new parameters):
```csharp
public Task<string?> FetchByKeyAsync(TableSchema schema, string key, bool tenantScoped = false, string? tenantId = null) =>
    sql.QuerySingleOrDefaultAsync<string>(
        $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
        new { Key = key }, tenantScoped, tenantId);
```
(Mirror this pattern for `FetchManyByKeysAsync`, `FetchByColumnAsync`, `FetchAllAsync`.) For `DeleteAsync` (operates on an already-open `IDbTransactionContext tx`, not `IRecordStoreQueryExecutor`): when `tenantScoped` is `true` (regardless of whether `tenantId` is null), issue `SET LOCAL ROLE`/`set_config` via `tx.ExecuteAsync(...)` immediately before the `DELETE` statement:
```csharp
public async Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key, bool tenantScoped = false, string? tenantId = null)
{
    if (tenantScoped)
    {
        await tx.ExecuteAsync("SET LOCAL ROLE iverson_runtime");
        await tx.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId });
    }
    await tx.ExecuteAsync(
        $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
        new { Key = key });
}
```

- [ ] **Step 3: Add a nullable `tenantId` parameter to `IOutboxWriter.UpsertAndEnqueueOutboxAsync`.** Inside its transaction callback, when `tenantId is not null`, issue `SET LOCAL ROLE`/`set_config` via `tx.ExecuteAsync(...)` before the entity-table upsert statement, then `RESET ROLE` via `tx.ExecuteAsync("RESET ROLE")` before the outbox-table insert statement:
```csharp
public async Task<Guid> UpsertAndEnqueueOutboxAsync(
    TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null)
{
    // ...existing upsertSql/outboxSql construction unchanged...
    var outboxRowId = Guid.CreateVersion7();

    await txRunner.ExecuteInTransactionAsync(async tx =>
    {
        if (tenantId is not null)
        {
            await tx.ExecuteAsync("SET LOCAL ROLE iverson_runtime");
            await tx.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId });
        }
        await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
        if (tenantId is not null)
            await tx.ExecuteAsync("RESET ROLE");
        await tx.ExecuteAsync(outboxSql, new { Id = outboxRowId, TypeName = typeName, EntityKey = key, EnqueuedAt = DateTimeOffset.UtcNow });
    });

    return outboxRowId;
}
```

- [ ] **Step 4: Tests.** Using `PostgresContainerFixture`: a raw query as `iverson_runtime` for tenant A cannot see tenant B's rows in a tenant-scoped table (proves the DB backstop independent of any app `WHERE` clause); a `null`/unset tenant value returns zero rows for a tenant-scoped table (fail-closed); a legacy no-`TenantColumn` table raises a Postgres permission error under `iverson_runtime` (no grant exists), not an empty result; `OutboxWriter.UpsertAndEnqueueOutboxAsync` with a tenant value completes correctly, with the entity write visible as `iverson_runtime` for the right tenant and the outbox row present regardless (correct privilege level per statement); existing `DlqRepositoryTests`/`ReconciliationQueueRepositoryTests`/`SchemaRegistryRepositoryTests` still pass unchanged (confirms the optional-parameter default is non-breaking). **Explicitly**: calling the layer with `tenantScoped: true, tenantId: null` against a tenant-scoped table returns zero rows (proves it took the `iverson_runtime` transactional path and hit RLS's fail-closed exclusion, distinct from `tenantScoped: false` against the same table, which returns the unfiltered result) — this is the regression test for the `tenantScoped`/`tenantId` split itself.

- [ ] **Step 5: Run tests and commit.**
```bash
dotnet test Iverson.Server/Iverson.Sql.Tests
git add Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Sql/PostgresRepository.cs Iverson.Server/Iverson.Sql/EntityRepository.cs Iverson.Server/Iverson.Sql/OutboxWriter.cs Iverson.Server/Iverson.Sql.Tests
git commit -m "feat(sql): switch to iverson_runtime role for tenant-scoped queries and writes"
```

### Task 3: gRPC/consumer call-site wiring

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs`
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs`

**Test:**
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`, `ObjectMappingGrpcServiceTests.cs`, `ObjectPersistenceGrpcServiceTests.cs`, `EntityRelationResolverTests.cs` — updated by Step 11 to account for the new parameters.

**Interfaces:**
- Consumes: the `tenantScoped`/`tenantId` parameters from Task 2.

- [ ] **Step 1: `ObjectRetrievalGrpcService.Get`** (`:32`, fetch precedes decision). Pass `tenantScoped: schema.TenantColumn is not null` and `tenantId: actingUserAccessor.ActingUser.FindFirst("tenant_id")?.Value` as `FetchByKeyAsync`'s new arguments — `tenantScoped` switches roles whenever the schema requires it, independent of whether the claim itself is present, so a claim-less caller on a tenant-scoped schema still gets RLS's fail-closed exclusion rather than an unfiltered read on `iverson`.

- [ ] **Step 2: `ObjectRetrievalGrpcService.GetMany`** (`:77` decision, `:87` fetch — decision precedes fetch here). Pass `tenantScoped: decision.TenantColumn is not null` and `tenantId: decision.TenantValue` to `FetchManyByKeysAsync`.

- [ ] **Step 3: `ObjectMappingGrpcService.Get`** (`:68` fetch, `:79` decision). Same raw-claim sourcing as Step 1.

- [ ] **Step 4: `ObjectMappingGrpcService.Update`'s pre-check fetch** (`:153`). Pass no tenant arguments (defaults: `tenantScoped: false`) — the carve-out; must see the true row regardless of tenant.

- [ ] **Step 5: `ObjectMappingGrpcService.Delete`'s pre-check fetch** (`:184`, fetch precedes decision at `:193`). Same sourcing as Step 1 — unlike `Update`, "not found" is the correct outcome here either way.

- [ ] **Step 6: `ObjectMappingGrpcService.Delete`'s `DeleteAsync` inside the transaction** (`:213`, decision already computed at `:193`). Pass `tenantScoped: decision.TenantColumn is not null` and `tenantId: decision.TenantValue`.

- [ ] **Step 7: `ObjectPersistenceGrpcService.Update`'s pre-check fetch** (`:80`). Pass no tenant arguments (same carve-out as Step 4).

- [ ] **Step 8: All 4 `Post`/`Update` calls to `OutboxWriter.UpsertAndEnqueueOutboxAsync`** (`ObjectMappingGrpcService.cs:125,162`; `ObjectPersistenceGrpcService.cs:58,94`). Pass `decision.TenantValue` — authorization has already succeeded by this point in every case.

- [ ] **Step 9: `EntityRelationResolver`'s 3 methods** (`ResolveSingleRelationAsync:67`, `ResolveManyToManyAsync:92`, `ResolveOneToManyAsync:123` — all fetch before decision). Pass `tenantScoped: relatedSchema.TenantColumn is not null` and `tenantId: actingUser?.FindFirst("tenant_id")?.Value` to each fetch call.

- [ ] **Step 10: `ReconciliationService.FetchAllAsync`** (`:31`), **`IntelligenceStoreConsumer`'s `FetchByKeyAsync`** (`:236`), **`EngagementStoreConsumer`'s `FetchByKeyAsync`** (`:103`). Pass no tenant arguments at each (defaults: `tenantScoped: false`) — no acting-user context; these compile with no other changes needed since Task 2's parameters are optional.

- [ ] **Step 11: Update existing `IEntityRepository` NSubstitute setups for the new parameters.** Adding `tenantScoped`/`tenantId` to `IEntityRepository`'s methods means every existing mock setup/verification of the form `Arg.Any<TableSchema>(), Arg.Any<string>())` — compiled today against the 2-parameter interface — implicitly records the omitted new parameters as concrete `false`/`null`, and will silently stop matching once production code passes a real `tenantScoped: true` (which it now does at every call site above, since the shared `ActingUserFixtures.Principal` test fixture carries a real `tenant_id` claim). Update every such setup/verification in `ObjectRetrievalGrpcServiceTests.cs`, `ObjectMappingGrpcServiceTests.cs`, `ObjectPersistenceGrpcServiceTests.cs`, and `EntityRelationResolverTests.cs` to add `Arg.Any<bool>(), Arg.Any<string?>()` (or the appropriate concrete/matcher values where a test specifically asserts on them) so they continue to match regardless of what tenant arguments flow through.

- [ ] **Step 12: Run the full existing test suite to confirm no behavioral regression** (RLS is a backstop, not a behavior change — every existing test for these 7 files, plus the 4 test files updated in Step 11, should pass).

- [ ] **Step 13: Run tests and commit.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests --filter "Category!=Integration"
git add Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs Iverson.Server/Iverson.Api/Reconciliation/ReconciliationService.cs Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/EntityRelationResolverTests.cs
git commit -m "feat(api): thread tenant value into RLS-scoped calls at every read/write call site"
```
