# StarRocks Tenant Database Isolation (Part B3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-19-starrocks-tenant-database-isolation-design.md` (commit SHA: `bfe28265d682e0fc98c0fd1b96342fa0edd0070c`)

**Goal:** Give every tenant a physically isolated StarRocks database, enforced by StarRocks' own native RBAC (`SET ROLE`), as a database-level backstop beneath the existing application-layer `WHERE`-clause tenant filtering.

**Architecture:** One StarRocks database per tenant (`iverson_tenant_{tenantId}`) and one role per tenant (`role_tenant_{tenantId}`), granted SELECT/INSERT/UPDATE/DELETE + CREATE TABLE on that database. A single shared `iverson_app` connection activates exactly one tenant role via `SET ROLE` for the duration of each tenant-scoped call, then deactivates it — mirroring `Iverson.Sql/PostgresRepository.cs`'s existing `SET LOCAL ROLE` pattern for Postgres. Because `SET ROLE` changes privileges but not the connection's current database, every generated SQL statement is fully qualified with the tenant database name (`` `iverson_tenant_{tenantId}`.`{tableName}` ``) rather than relying on `USE`.

**Tech stack:** C# / .NET 10, Dapper, MySqlConnector (StarRocks' MySQL-wire-protocol driver) — all per the spec's Verified Assumptions; no new dependencies.

---

## Global Constraints

- Tenant IDs must pass the allowlist `^[A-Za-z0-9_-]{1,52}$` before being spliced into any database name, role name, or `SET ROLE`/`GRANT` statement fragment. A tenant ID that fails this check is treated as absent (fail-closed).
- Database name: `iverson_tenant_{tenantId}`. Role name: `role_tenant_{tenantId}`.
- `SET ROLE`/`SET ROLE NONE` must run on the same physical connection as the operation it scopes, and that connection must not have `USE`'d a tenant database — every physical-table SQL reference must be fully qualified with the tenant database name instead.
- Provisioning uses `user_admin` only (never `db_admin` — verified over-broad during spec brainstorming).
- Read-path gating (`SearchAsync`/`AggregateAsync`/`GroupByAsync`/`PipelineAsync`): `authz == null` means the caller isn't going through Part A's authorization evaluation at all (verified: no production caller ever passes null) — falls back to today's unscoped query. `authz != null` but the primary type's `TenantValue` is null/invalid fails closed (no query executes). Never conflate these two cases.
- Every new/modified commit message follows this repo's existing `type(scope): summary` convention (see `git log --oneline`), e.g. `feat(starrocks): ...`, `fix(api): ...`.

## File Structure

- Create: `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs` — validation, naming, qualification helper.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs` — tenant-scoped connection wrapping; all 6 wrap points.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs` — thread `tenantDatabase` through `BuildSearch`/`BuildAggregate`/`BuildGroupBy`/`BuildFromWithJoins`.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs` — thread `tenantDatabase` through `Build`.
- Modify: `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs` — `IEngagementStoreEntityStore` signature changes; delete `IEngagementStoreSchemaManager`.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksSchemaManager.cs` — collapse to just `BuildCreateTableDdl` (qualified); delete `ApplyTableAsync`/`EnsureDatabaseAsync`/readiness machinery.
- Modify: `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs` — remove `StarRocksSchemaManager`/`IEngagementStoreSchemaManager` DI registration.
- Modify: `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs` — provisioning cache + tenant re-derivation on upsert/delete.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — remove eager `ApplyTableAsync` call and its constructor dependency.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml` — grant `user_admin`.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs` — new, unit.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs` — migrate off `ApplyTableAsync`.
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs` — signature + stub updates, new tenant tests.
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerKafkaOrderingTests.cs` — stub + payload updates.
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — remove/rewrite the eager-creation test.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs` — new, integration (privilege boundary).

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are trusted as ground truth here (see the spec's own `## Verified assumptions` table for full evidence citations):

- StarRocks 4.1.1 (this repo), no native row-level security, Ranger rejected.
- `SET ROLE`/`SET ROLE NONE` fully reversible mid-session, doesn't change `CURRENT_USER()`.
- A connection with no role active has zero privilege on any tenant database (fail-closed default).
- A database-level wildcard grant issued once covers tables created later in that database.
- `user_admin` alone (not `db_admin`) is the correct, minimal provisioning role.
- `StarRocksRepository`'s 4 read methods + `UpsertAsync`/`DeleteAsync` are the only production callers of `QueryAsync`/`ExecuteAsync`.
- `AuthorizationConstraint.TenantValue` is already available at all 4 read call sites via the existing `authz` dictionary — no new plumbing needed to reach `StarRocksRepository`.
- `EngagementStoreConsumer`'s `FetchAuthoritativeOwnerValueAsync` is generic over field name — reusable for tenant re-derivation with no new helper.
- `ev.PayloadJson` is guaranteed non-null on every delete event `EngagementStoreConsumer` receives.
- Only `EngagementStoreConsumer` calls `StarRocksRepository.UpsertAsync`/`DeleteAsync` — exactly one call site each to update.
- `job-create-user.yaml`'s idempotent-hook pattern safely accommodates one more `GRANT`.
- `StarRocksHealthChecker` never touches a tenant-specific database/table — out of scope, untouched.
- New `iverson_tenant_{id}`/`role_tenant_{id}` names don't collide with the existing bare `iverson` database.
- Tenant-ID allowlist bound is `{1,52}` (not `{1,64}`) — leaves room for the 12-char `role_tenant_` prefix under StarRocks' 64-char identifier cap.
- An active tenant role is denied on every *other* tenant's database, not just when no role is active.
- Fully-qualified `` `db`.`table` `` references with no `USE` statement correctly route privilege checks — independently verified live in CDR round 2.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against the current codebase (all citations re-confirmed against `main` at commit `bfe2826`):

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs` does not exist yet | `ls Iverson.Server/Iverson.StarRocks/*.cs` — 18 existing files, no `TenantIdentifier.cs` |
| 2 | Consumer impact | `IEngagementStoreQueryExecutor` (unaffected by this plan — `QueryAsync`/`ExecuteAsync` keep their public signature) is resolved via DI only in test files, never injected by production code | `grep -rn "IEngagementStoreQueryExecutor\b"` — only `ServiceCollectionExtensions.cs` (registration) and 2 test files |
| 3 | Signature | `StarRocksRepository.RunAsync<T>(Func<Task<T>> operation)` exists as a private instance method suitable for wrapping a new tenant-scoped connection helper | `StarRocksRepository.cs:34-49` |
| 4 | Code pattern | `Iverson.Sql/PostgresRepository.cs`'s `RunTenantScopedAsync` (open connection → activate role → run operation → the connection is disposed) is the established structural precedent this design explicitly says to mirror | `PostgresRepository.cs:116-134` |
| 5 | Consumer impact (Cat 6, critical) | `StarRocksQueryBuilder.BuildSearch`/`BuildAggregate`/`BuildGroupBy` and `StarRocksPipelineBuilder.Build` are called with a trailing optional `authz` parameter by 118+31 existing unit-test call sites in `StarRocksQueryBuilderTests.cs`/`StarRocksPipelineBuilderTests.cs`, none of which reach past the 5 leading positional parameters — adding one more trailing optional parameter (`tenantDatabase = null`) after `authz` is backward compatible and requires zero test changes | `grep -n "BuildSearch("` sample: `StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 10)` — stops at 5 positional args |
| 6 | Consumer impact (Cat 6, critical) | 15 existing tests in `StarRocksIntegrationTests.cs` and 5 in `PipelineIntegrationTests.cs` call `SearchAsync`/`AggregateAsync`/`GroupByAsync`/`PipelineAsync` with `authz` left at its default `null`, to test raw SQL generation directly, bypassing gRPC-layer authorization | `grep -n "authz:" StarRocksIntegrationTests.cs` → 0 hits against 15 call sites; `PipelineIntegrationTests.cs` line 66 etc. |
| 7 | Consumer impact (Cat 6, critical — resolves #6) | Production (`ObjectSearchGrpcService`) never calls these 4 methods with `authz` null: `EvaluateAuthorization` always constructs a real `Dictionary` (never null) and `Search`/`Aggregate`/`GroupBy`/`Pipeline` all `return`/deny before the call whenever the primary type's authorization is denied (which is the only path that could leave `TenantValue` null) | `ObjectSearchGrpcService.cs:51-53` (`if (auth.PrimaryDenied) return;`), `:469-472` (`Constraints` is non-nullable `IReadOnlyDictionary`), `RowFieldAuthorizationEvaluator.cs:18-19` (denies before `TenantColumn`/`tenantId` ever reach `AuthorizationDecision`) |
| 8 | Consumer impact (Cat 6, critical) | `SchemaFixtures.AuthorSchema()` (used by nearly every `EngagementStoreConsumerTests.cs` test) declares `TenantColumn = "TenantId"` unconditionally — unlike `Authorization.OwnerField`, tenant re-derivation is NOT gated behind an opt-in config, so it fires on every existing upsert/delete test | `SchemaFixtures.cs:26,42,61,80,96,111,135` |
| 9 | Consumer impact (Cat 6, critical) | `EngagementStoreConsumerTests.cs`'s constructor stubs `_entities.FetchByKeyAsync(...)` to return `"""{"Name":"Alice"}"""` (no `TenantId` key) and `HandleDelete_WithEngagementFlag_CallsDeleteAsync`'s event payload is `"{}"` — both currently lack a tenant value, so both must be updated or every existing upsert/delete test in this file (and the ordering test) will silently stop calling `UpsertAsync`/`DeleteAsync` once tenant re-derivation is fail-closed by default | `EngagementStoreConsumerTests.cs:45-46,88`; `EngagementStoreConsumerKafkaOrderingTests.cs:68,104` (no stub configured on `entities` at all) |
| 10 | Consumer impact (Cat 6, critical) | `UpsertAsync`/`DeleteAsync` signature changes (adding `tenantId`) require updating every `Received(1).UpsertAsync(...)`/`.DeleteAsync(...)` and setup-stub call across both consumer test files | `grep -c "UpsertAsync\|DeleteAsync" EngagementStoreConsumerTests.cs` → 24; `EngagementStoreConsumerKafkaOrderingTests.cs` → 4 |
| 11 | Consumer impact (Cat 6, critical) | `IEngagementStoreSchemaManager.ApplyTableAsync` has no production callers left after Task 5's orchestrator change, but is used as the table-setup helper at 4 call sites across ~30 tests in `StarRocksIntegrationTests.cs` via `StarRocksContainerFixture.SchemaManager` | `grep -rn "ApplyTableAsync"` excluding `.Tests` → only `SchemaRegistrationOrchestrator.cs:61`; including tests → `StarRocksIntegrationTests.cs:150,283,320,533` + fixture at `:45` |
| 12 | Type/consumer impact | `StarRocksSchemaManager.BuildCreateTableDdl` is `internal static` and stateless (no instance fields used) — callable directly from `StarRocksRepository` (same assembly) with no DI/instance needed once the surrounding instance machinery (`ApplyTableAsync`, `EnsureDatabaseAsync`, readiness gate, `_dbName`) is deleted | `StarRocksSchemaManager.cs:45-64` |
| 13 | Signature | `EngagementStoreConsumer`'s existing `WithOwnerValue(payloadJson, ownerField, authoritativeOwnerValue)` helper is field-name-generic despite its name — reusable verbatim for tenant-value splicing | `EngagementStoreConsumer.cs:124-133` |
| 14 | File path / test convention | `StarRocksContainerFixture` (Testcontainers-based, connects as `root`) already exists and exposes a public `ConnectionString`/`Repository`; a new test class can reuse it unmodified, creating its own `iverson_app` user + grants via `fixture.Repository.ExecuteAsync(...)` (which runs as root) rather than duplicating the container-readiness-wait logic in a second fixture | `StarRocksIntegrationTests.cs:11-120` |
| 15 | Command | Tests run via `dotnet test Iverson.Server/Iverson.StarRocks.Tests` and `dotnet test Iverson.Server/Iverson.Api.Tests`, matching this repo's established convention | `docs/plans/2026-07-17-mandatory-tenant-boundary-implementation-plan.md:131,180,264,291` |
| 16 | Task ordering | Task 3 (write path) depends on Task 1 (`TenantIdentifier`); Task 4 (provisioning) depends on Task 3's `IEngagementStoreEntityStore` signature; Task 5 (orchestrator cleanup) is independent of Tasks 1-4 and can run any time after Task 4 lands (so `EnsureTenantProvisionedAsync` exists as the new table-creation path before the old eager one is removed); Task 7 depends on Tasks 1-4 being complete | Derived from each task's own file dependencies, listed per-task below |
| 17 | Commit convention | Existing commits use `type(scope): summary` (e.g. `feat(starrocks): ...`, `fix(api): ...`) | `git log --oneline -15` |

## Tasks

### Task 1: Tenant identifier validation, naming, and qualification helper

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`
- Test: `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs`

**Interfaces:**
- Produces: `TenantIdentifier.IsValid`, `TenantIdentifier.DatabaseName`, `TenantIdentifier.RoleName`, `TenantIdentifier.Qualify` — consumed by Tasks 2, 3, 4.

- [ ] **Step 1: Create `TenantIdentifier.cs`**

```csharp
using System.Text.RegularExpressions;

namespace Iverson.StarRocks;

internal static class TenantIdentifier
{
    private static readonly Regex AllowedPattern = new("^[A-Za-z0-9_-]{1,52}$", RegexOptions.Compiled);

    internal static bool IsValid(string tenantId) => AllowedPattern.IsMatch(tenantId);

    internal static string DatabaseName(string tenantId) => $"iverson_tenant_{tenantId}";

    internal static string RoleName(string tenantId) => $"role_tenant_{tenantId}";

    internal static string Qualify(string? tenantDatabase, string tableName) =>
        tenantDatabase is null ? $"`{tableName}`" : $"`{tenantDatabase}`.`{tableName}`";
}
```

- [ ] **Step 2: Write unit tests** covering: valid IDs (alphanumeric, underscore, hyphen, boundary lengths 1 and 52); invalid IDs (53 chars, empty, backtick, semicolon, space, SQL comment sequence `--`); `DatabaseName`/`RoleName` produce the exact `iverson_tenant_`/`role_tenant_` prefixes; `Qualify` returns bare backtick-quoted name when `tenantDatabase` is null, and `` `db`.`table` `` when not.

- [ ] **Step 3: Run tests and commit**
```bash
dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter TenantIdentifierTests
git add Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs
git commit -m "feat(starrocks): add TenantIdentifier validation/naming helper for tenant database isolation"
```

### Task 2: Tenant-scoped connection wrapping and the read path

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`

**Interfaces:**
- Consumes: `TenantIdentifier` (Task 1).
- Produces: `StarRocksRepository.RunTenantScopedAsync` — consumed by Task 3.

- [ ] **Step 1: Add `RunTenantScopedAsync` to `StarRocksRepository`**

Add after the existing `ExecuteAsync` method (`StarRocksRepository.cs:98`):

```csharp
private async Task<T> RunTenantScopedAsync<T>(
    string activityName, string tenantId, string sql, Func<MySqlConnection, Task<T>> operation)
{
    using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
    activity?.SetTag("db.system", "starrocks");
    activity?.SetTag("db.statement", sql);

    try
    {
        var result = await RunAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync($"SET ROLE `{TenantIdentifier.RoleName(tenantId)}`");
            try
            {
                return await operation(conn);
            }
            finally
            {
                try
                {
                    await conn.ExecuteAsync("SET ROLE NONE");
                }
                catch (Exception ex)
                {
                    // The connection is being disposed either way; a broken connection here must
                    // never replace the operation's own result or exception — same discipline as
                    // PostgresRepository.RunTenantScopedAsync's rollback-failure handling.
                    logger.LogWarning(ex, "SET ROLE NONE failed while releasing a tenant-scoped StarRocks connection");
                }
            }
        });
        activity?.SetStatus(ActivityStatusCode.Ok);
        return result;
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.RecordException(ex);
        throw;
    }
}
```

- [ ] **Step 2: Thread `tenantDatabase` through `StarRocksQueryBuilder`**

In `BuildSearch` (`:21-30`), `BuildAggregate` (`:134-142`), `BuildGroupBy` (`:303-308`), add a new trailing parameter `string? tenantDatabase = null` after `authz`.

Replace the two standalone bare-table sites:
- `:81`: `from = $"FROM `{tableName}`";` → `from = $"FROM {TenantIdentifier.Qualify(tenantDatabase, tableName)}";`
- `:157`: same replacement in `BuildAggregate`'s no-joins branch.

In `BuildFromWithJoins` (`:705-711`), add the same trailing `string? tenantDatabase = null` parameter, and update its 3 callers (`BuildSearch:43`, `BuildAggregate:152`, `BuildGroupBy:313`) to pass `tenantDatabase` through. Replace:
- `:714`: `var sb = new StringBuilder($"FROM `{primarySchema.TableName}`");` → `var sb = new StringBuilder($"FROM {TenantIdentifier.Qualify(tenantDatabase, primarySchema.TableName)}");`
- `:778`: `$" {kind} JOIN `{rightCtx.TableName}` ON "` → `$" {kind} JOIN {TenantIdentifier.Qualify(tenantDatabase, rightCtx.TableName)} ON "`

- [ ] **Step 3: Thread `tenantDatabase` through `StarRocksPipelineBuilder.Build`**

Add a trailing `string? tenantDatabase = null` parameter to `Build` (`:376-380`).

Replace:
- `:411`: `sb.Append($"WITH `{BaseStepName}` AS (SELECT * FROM `{schema.TableName}`");` → `sb.Append($"WITH `{BaseStepName}` AS (SELECT * FROM {TenantIdentifier.Qualify(tenantDatabase, schema.TableName)}");`
- `:530`: `? $"`{joinedSchema!.TableName}` AS `{src.Name}`"` → `? $"{TenantIdentifier.Qualify(tenantDatabase, joinedSchema!.TableName)} AS `{src.Name}`"`

**Do NOT modify** `:428`, `:480`, `:512` (`` FROM `{prev}` ``/`` FROM `{input.Name}` ``) — these reference in-query CTE step aliases defined earlier in the same `WITH ... SELECT` statement, not physical tables (see spec's Call-site wiring section for the reasoning). Add a one-line comment at the first of these three sites noting they must stay bare, to prevent a future qualification sweep from touching them by mistake.

- [ ] **Step 4: Wire the 4 read methods in `StarRocksRepository`**

Replace `SearchAsync` (`:147-160`):

```csharp
public async Task<IEnumerable<dynamic>> SearchAsync(
    StarRocksQuerySchema schema,
    SearchQuery? query,
    int page,
    int pageSize,
    IReadOnlyList<string>? fields = null,
    IReadOnlyList<JoinSpec>? joins = null,
    Func<string, StarRocksQuerySchema?>? registry = null,
    IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
{
    // authz == null means the caller isn't going through Part A's authorization evaluation at
    // all (e.g. a unit test exercising raw SQL generation) — preserve today's unscoped behavior.
    // Production (ObjectSearchGrpcService) always passes a real authz dict and never reaches
    // here with a missing tenant value (it denies upstream first) — verified at plan-write time.
    if (authz is null)
    {
        var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildSearch(
            schema.TableName, schema, query, page, pageSize, fields, joins, registry, authz);
        return await QueryAsync<dynamic>(unscopedSql, unscopedParam);
    }

    var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
    if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
        return [];

    var (sql, param) = StarRocksQueryBuilder.BuildSearch(
        schema.TableName, schema, query, page, pageSize, fields, joins, registry, authz,
        TenantIdentifier.DatabaseName(tenantId));

    return await RunTenantScopedAsync("sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param));
}
```

Apply the identical `authz is null` / `tenantId is null || !IsValid` / qualified-and-scoped pattern to `GroupByAsync` (`:207-215`) and `PipelineAsync` (`:217-226`, passing `tenantDatabase` to `StarRocksPipelineBuilder.Build` the same way) — both return `Task<IEnumerable<dynamic>>` like `SearchAsync`, so the fail-closed branch is `return [];` in both, identical in shape to the `SearchAsync` code above.

`AggregateAsync` (`:162-205`) returns `Task<AggregationResult?>`, not `Task<IEnumerable<dynamic>>` — its fail-closed branch must return `null`, not `[]`. Only the query-building and execution portion (`:175-178`) changes; the aggregation-kind switch below (`:180-204`) stays unchanged, now operating on `rows` sourced from the tenant-scoped path:

```csharp
public async Task<AggregationResult?> AggregateAsync(
    StarRocksQuerySchema schema,
    SearchQuery? query,
    AggregationDescriptor spec,
    SearchQuery? having = null,
    IReadOnlyList<JoinSpec>? joins = null,
    Func<string, StarRocksQuerySchema?>? registry = null,
    IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
{
    if (spec.GroupByFields is { Count: > 1 })
        throw new StarRocksQueryTranslationException(
            "Multi-key GROUP BY (group_by_fields with more than one entry) is not yet supported via the Aggregate RPC's result decoding; use a single field or wait for the GroupByRequest RPC.");

    List<dynamic> rows;
    if (authz is null)
    {
        var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildAggregate(
            schema.TableName, schema, query, spec, having, joins, registry, authz);
        rows = (await QueryAsync<dynamic>(unscopedSql, unscopedParam)).ToList();
    }
    else
    {
        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return null;

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
            schema.TableName, schema, query, spec, having, joins, registry, authz,
            TenantIdentifier.DatabaseName(tenantId));
        rows = (await RunTenantScopedAsync("sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param))).ToList();
    }

    switch (spec.Kind)
    {
        case AggregationKind.Terms:
        case AggregationKind.DateHistogram:
        case AggregationKind.Range:
        {
            var buckets = rows
                .Select(r => (IDictionary<string, object>)r)
                .Where(r => r.TryGetValue("bucket_key", out var k) && k is not null)
                .Select(r => new AggregationBucket(
                    r["bucket_key"]?.ToString() ?? string.Empty,
                    Convert.ToInt64(r["doc_count"])))
                .ToList();
            return new AggregationResult(spec.Name, spec.Kind, Buckets: buckets);
        }

        default:
        {
            if (rows.Count == 0) return new AggregationResult(spec.Name, spec.Kind, MetricValue: null);
            var row0 = (IDictionary<string, object>)rows[0];
            row0.TryGetValue("metric_val", out var val);
            return new AggregationResult(spec.Name, spec.Kind,
                MetricValue: val is null ? null : Convert.ToDouble(val));
        }
    }
}
```

- [ ] **Step 5: Build and run existing tests to confirm no regression**
```bash
dotnet build Iverson.Server/Iverson.StarRocks
dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "FullyQualifiedName~StarRocksQueryBuilderTests|FullyQualifiedName~StarRocksPipelineBuilderTests"
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs
git commit -m "feat(starrocks): tenant-scope the read path (SearchAsync/AggregateAsync/GroupByAsync/PipelineAsync)"
```

### Task 3: Write path (UpsertAsync / DeleteAsync)

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs`

**Interfaces:**
- Consumes: `TenantIdentifier` (Task 1), `RunTenantScopedAsync` (Task 2).
- Produces: new `IEngagementStoreEntityStore.UpsertAsync`/`DeleteAsync` signatures — consumed by Task 4.

- [ ] **Step 1: Update `IEngagementStoreEntityStore`** (`IEngagementStoreRoles.cs:22-26`)

```csharp
public interface IEngagementStoreEntityStore
{
    Task UpsertAsync(StarRocksTableSchema schema, string payloadJson, string tenantId);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId);
}
```

- [ ] **Step 2: Update `UpsertAsync`** (`StarRocksRepository.cs:100-140`) — add a `tenantId` parameter, validate it, qualify the table reference, and route through `RunTenantScopedAsync` instead of the public `ExecuteAsync`:

```csharp
public async Task UpsertAsync(StarRocksTableSchema schema, string payloadJson, string tenantId)
{
    using var activity = Telemetry.Source.StartActivity("sr.upsert", ActivityKind.Client);
    activity?.SetTag("db.system", "starrocks");
    activity?.SetTag("db.table", schema.TableName);

    if (!TenantIdentifier.IsValid(tenantId)) return;

    var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
        ?? new Dictionary<string, JsonElement>();

    var knownCols = schema.Columns
        .Select(c => c.Name)
        .Append(schema.KeyColumn.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var entries = row
        .Where(kv => knownCols.Contains(kv.Key)
                  && kv.Value.ValueKind != JsonValueKind.Object
                  && kv.Value.ValueKind != JsonValueKind.Undefined)
        .ToList();

    if (entries.Count == 0) return;

    var colList   = string.Join(", ", entries.Select(e => $"`{e.Key}`"));
    var paramList = string.Join(", ", entries.Select((_, i) => $"@p{i}"));
    var qualifiedTable = TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenantId), schema.TableName);

    // StarRocks Primary Key model treats INSERT of an existing key as a FULL-ROW REPLACE:
    // any column absent from the INSERT list is reset to its default/null. (unchanged rationale
    // from the pre-existing comment here — see git history.)
    var sql = $"INSERT INTO {qualifiedTable} ({colList}) VALUES ({paramList})";

    var param = new DynamicParameters();
    for (var i = 0; i < entries.Count; i++)
        param.Add($"p{i}", JsonElementToObject(entries[i].Value));

    await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, param));
    activity?.SetStatus(ActivityStatusCode.Ok);
}
```

- [ ] **Step 3: Update `DeleteAsync`** (`StarRocksRepository.cs:142-145`):

```csharp
public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
{
    if (!TenantIdentifier.IsValid(tenantId)) return;

    var qualifiedTable = TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenantId), tableName);
    var sql = $"DELETE FROM {qualifiedTable} WHERE `{keyColumn}` = @key";
    await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, new { key = keyValue }));
}
```

- [ ] **Step 4: Build** (this will show compile errors in `EngagementStoreConsumer.cs` and both consumer test files — expected; Task 4 fixes the consumer, this task only needs `Iverson.StarRocks` itself to build clean)
```bash
dotnet build Iverson.Server/Iverson.StarRocks
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs
git commit -m "feat(starrocks): tenant-scope the write path (UpsertAsync/DeleteAsync)"
```

### Task 4: Lazy provisioning and `EngagementStoreConsumer` wiring

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerKafkaOrderingTests.cs`

**Interfaces:**
- Consumes: `TenantIdentifier` (Task 1), the Task 3 `UpsertAsync`/`DeleteAsync` signatures, `StarRocksSchemaManager.BuildCreateTableDdl` (unchanged location, qualified in this task — Task 5 later removes everything else from that class).

- [ ] **Step 1: Add `EnsureTenantProvisionedAsync` to `IEngagementStoreEntityStore`** (`IEngagementStoreRoles.cs`):

```csharp
public interface IEngagementStoreEntityStore
{
    Task UpsertAsync(StarRocksTableSchema schema, string payloadJson, string tenantId);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId);
    Task EnsureTenantProvisionedAsync(string tenantId, StarRocksTableSchema schema);
}
```

- [ ] **Step 2: Implement `EnsureTenantProvisionedAsync` in `StarRocksRepository`**, using a plain `RunAsync`-wrapped connection (no tenant role needed — this runs as `user_admin`):

```csharp
public async Task EnsureTenantProvisionedAsync(string tenantId, StarRocksTableSchema schema)
{
    if (!TenantIdentifier.IsValid(tenantId)) return;

    var dbName   = TenantIdentifier.DatabaseName(tenantId);
    var roleName = TenantIdentifier.RoleName(tenantId);
    var qualifiedTable = TenantIdentifier.Qualify(dbName, schema.TableName);
    var createTableDdl = StarRocksSchemaManager.BuildCreateTableDdl(schema, qualifiedTable);

    using var activity = Telemetry.Source.StartActivity("sr.provision_tenant", ActivityKind.Client);
    activity?.SetTag("db.system", "starrocks");
    activity?.SetTag("db.table", schema.TableName);

    await RunAsync(async () =>
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync("SET ROLE user_admin");
        try
        {
            await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{dbName}`");
            await conn.ExecuteAsync($"CREATE ROLE IF NOT EXISTS `{roleName}`");
            await conn.ExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON `{dbName}`.* TO ROLE `{roleName}`");
            await conn.ExecuteAsync($"GRANT CREATE TABLE ON DATABASE `{dbName}` TO ROLE `{roleName}`");
            await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_app'@'%'");
            await conn.ExecuteAsync(createTableDdl);
            return true;
        }
        finally
        {
            try
            {
                await conn.ExecuteAsync("SET ROLE NONE");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SET ROLE NONE failed while releasing a tenant-provisioning StarRocks connection");
            }
        }
    });
    activity?.SetStatus(ActivityStatusCode.Ok);
}
```

Note: `BuildCreateTableDdl` gains a second parameter here (`qualifiedTable`, already-qualified) rather than a bare `tenantDatabase` — see Task 5 Step 1 for the exact signature change, since that method lives in `StarRocksSchemaManager` and this task only calls it.

- [ ] **Step 3: Wire `EngagementStoreConsumer`** (`EngagementStoreConsumer.cs`):

Add a per-process idempotency cache field, mirroring `IntelligenceStoreConsumer._ensuredCollections`:

```csharp
private readonly HashSet<(string TenantId, string TableName)> _provisioned = [];
```

Rewrite `HandleUpsertAsync` (`:41-75`) to also re-derive and provision the tenant, reusing the existing owner-re-derivation helper for tenant sourcing:

```csharp
internal async Task HandleUpsertAsync(string key, string value, CancellationToken ct)
{
    var ev = Deserialize(key, value);
    if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

    var schema = registry.Get(ev.TypeName);
    if (schema is null)
    {
        logger.LogError("[Engagement] Dropped upsert — no schema for type={Type} key={Key}", ev.TypeName.SanitizeForLog(), key);
        return;
    }

    // tenant_field is mandatory on every registered schema (Part A) — schema.TenantColumn is
    // never null here for a schema that reached this point (SchemaRegistrationOrchestrator
    // rejects registration otherwise).
    var authoritativeTenantValue = await FetchAuthoritativeOwnerValueAsync(schema, schema.TenantColumn!, ev.Key, ct);
    if (authoritativeTenantValue is null)
    {
        logger.LogWarning("[Engagement] Dropped upsert — no authoritative tenant value for type={Type} key={Key}", ev.TypeName.SanitizeForLog(), key);
        return;
    }

    var srSchema = SchemaBuilder.ToStarRocksTableSchema(schema);

    if (_provisioned.Add((authoritativeTenantValue, srSchema.TableName)))
        await sr.EnsureTenantProvisionedAsync(authoritativeTenantValue, srSchema);

    // Re-derive the ownership value from the authoritative Postgres row rather than trusting
    // the event payload's own value for it — the payload is unsigned JSON and this value
    // feeds StarRocks's read-time row authorization filtering (CSR #7, StarRocks sibling).
    var ownerField = schema.Authorization?.OwnerField;
    var payloadJson = ev.PayloadJson;
    if (ownerField is not null)
    {
        var authoritativeOwnerValue = await FetchAuthoritativeOwnerValueAsync(schema, ownerField, ev.Key, ct);
        try
        {
            payloadJson = WithOwnerValue(ev.PayloadJson, ownerField, authoritativeOwnerValue);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException(
                $"[Engagement] Malformed payload JSON type={ev.TypeName} key={key}", ex);
        }
    }

    await sr.UpsertAsync(srSchema, payloadJson, authoritativeTenantValue);
    logger.LogInformation("[Engagement] Upserted {Type}:{Key}", ev.TypeName.SanitizeForLog(), key);
}
```

Rewrite `HandleDeleteAsync` (`:77-91`) to extract the tenant value from `ev.PayloadJson` (the pre-delete row snapshot — the row is already gone from Postgres by delete time, so this can't re-fetch):

```csharp
internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
{
    var ev = Deserialize(key, value);
    if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

    var schema = registry.Get(ev.TypeName);
    if (schema is null)
    {
        logger.LogError("[Engagement] Dropped delete — no schema for type={Type} key={Key}", ev.TypeName.SanitizeForLog(), key);
        return;
    }

    using var doc = JsonDocument.Parse(ev.PayloadJson);
    var tenantValue = doc.RootElement.TryGetProperty(schema.TenantColumn!, out var v)
        ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
        : null;
    if (tenantValue is null)
    {
        logger.LogWarning("[Engagement] Dropped delete — no tenant value in payload for type={Type} key={Key}", ev.TypeName.SanitizeForLog(), key);
        return;
    }

    await sr.DeleteAsync(schema.TableName, schema.KeyColumn.Name, ev.Key, tenantValue);
    logger.LogInformation("[Engagement] Deleted {Type}:{Key}", ev.TypeName.SanitizeForLog(), key);
}
```

- [ ] **Step 4: Update `EngagementStoreConsumerTests.cs`**

In the constructor (`:29-49`):
- Update the two `_sr.UpsertAsync(...)`/`_sr.DeleteAsync(...)` default stubs to 3-arg/4-arg form (append `Arg.Any<string>()`).
- Update the `_entities.FetchByKeyAsync(...)` default stub to include a tenant value: `.Returns("""{"Name":"Alice","TenantId":"tenant-a"}""")`.

Across the file (24 references per the verified-assumptions table): update every `_sr.Received(...).UpsertAsync(...)`/`.DeleteAsync(...)` and every re-stub of `_sr.UpsertAsync(...)`/`_sr.DeleteAsync(...)` to the new 3-arg/4-arg signatures, appending `Arg.Any<string>()` (or a specific expected tenant value where the test's intent calls for asserting it — e.g. `HandleUpsert_WithEngagementFlag_CallsUpsertAsync` should assert `tenantId == "tenant-a"` to lock in the new behavior, not just `Arg.Any<string>()`).

For every test event whose `PayloadJson`/mocked authoritative row doesn't already carry a `TenantId`, add one (e.g. `{"Name":"Alice","TenantId":"tenant-a"}"`), matching the constructor default. `HandleDelete_WithEngagementFlag_CallsDeleteAsync`'s event `PayloadJson: "{}"` (`:88`) must become `PayloadJson: """{"TenantId":"tenant-a"}"""`.

Add two new tests mirroring the existing owner-value adversarial pair (`HandleUpsert_WithForgedOwnerValueInPayload_...`, `HandleUpsert_WithOwnerFieldAndNoAuthoritativeRow_...`), for the tenant case:
- `HandleUpsert_WithNoAuthoritativeTenantValue_SkipsProvisioningAndUpsert` — `_entities.FetchByKeyAsync` returns a row with no `TenantId` key; assert `_sr.DidNotReceive().EnsureTenantProvisionedAsync(...)` and `_sr.DidNotReceive().UpsertAsync(...)`.
- `HandleUpsert_ForNewTenant_CallsEnsureTenantProvisionedAsyncOnce_ThenSkipsOnSecondEvent` — two upsert events for the same (tenant, type) pair; assert `_sr.Received(1).EnsureTenantProvisionedAsync(...)` (idempotency-cache behavior).

- [ ] **Step 5: Update `EngagementStoreConsumerKafkaOrderingTests.cs`**

Add a stub for `entities.FetchByKeyAsync(...)` returning a JSON row with a `TenantId` (currently unconfigured, per the verified-assumptions table). Update the `createdEvent`/`deletedEvent` payloads (`:68`) to include a `TenantId` field. Update the 2-arg/3-arg `sr.UpsertAsync`/`sr.DeleteAsync` stub/assertion calls (`:85-86,123-124`) to 3-arg/4-arg form.

- [ ] **Step 6: Build and run**
```bash
dotnet build Iverson.Server/Iverson.Api
dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~EngagementStoreConsumer"
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs Iverson.Server/Iverson.StarRocks/StarRocksRepository.cs Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerKafkaOrderingTests.cs
git commit -m "feat(api): lazily provision per-tenant StarRocks databases on first write"
```

### Task 5: Remove eager schema application and its now-dead machinery

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksSchemaManager.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 4's `EnsureTenantProvisionedAsync` (the replacement table-creation path).

- [ ] **Step 1: Collapse `StarRocksSchemaManager` to just `BuildCreateTableDdl`**, qualified:

```csharp
namespace Iverson.StarRocks;

internal static class StarRocksSchemaManager
{
    internal static string BuildCreateTableDdl(StarRocksTableSchema schema, string qualifiedTableName)
    {
        var keySql  = $"`{schema.KeyColumn.Name}` {schema.KeyColumn.SrType} NOT NULL";
        var colsSql = schema.Columns.Select(c =>
            $"`{c.Name}` {c.SrType}{(c.IsNullable ? "" : " NOT NULL")}");

        var orderBy = schema.SortKey.Count > 0
            ? $"\nORDER BY ({string.Join(", ", schema.SortKey.Select(k => $"`{k}`"))})"
            : "";

        return $"""
            CREATE TABLE IF NOT EXISTS {qualifiedTableName} (
                {keySql},
                {string.Join(",\n    ", colsSql)}
            ) ENGINE=OLAP
            PRIMARY KEY(`{schema.KeyColumn.Name}`)
            DISTRIBUTED BY HASH(`{schema.KeyColumn.Name}`) BUCKETS 4{orderBy}
            PROPERTIES ("replication_num" = "1")
            """;
    }
}
```

Delete `ApplyTableAsync`, `EnsureDatabaseAsync`, `CheckBackendAliveAsync`, the constructor, `_dbName`, `_readinessGate`, `_pipeline`, `CreateConnection`, and the `using` statements they alone required (`Dapper`'s `ExecuteAsync`/`QuerySingleOrDefaultAsync`/`QueryAsync` extension usages, `Microsoft.Extensions.Logging`, `MySqlConnector`, `Polly`, `Polly.CircuitBreaker` — keep only what `BuildCreateTableDdl` itself needs, i.e. no `using` beyond the default namespace).

Note the signature change from Task 4 Step 2's call: `BuildCreateTableDdl(schema, qualifiedTable)` where `qualifiedTable` is already the fully-qualified `` `db`.`table` `` string (built via `TenantIdentifier.Qualify` in `StarRocksRepository.EnsureTenantProvisionedAsync`) — this keeps `StarRocksSchemaManager` itself free of any `TenantIdentifier`/tenant-database dependency, matching its now-minimal, single-purpose shape.

- [ ] **Step 2: Delete `IEngagementStoreSchemaManager`** from `IEngagementStoreRoles.cs` (`:11-14`).

- [ ] **Step 3: Update `SchemaRegistrationOrchestrator`** (`SchemaRegistrationOrchestrator.cs`) — remove the `IEngagementStoreSchemaManager starRocks` constructor parameter (`:18`) and the eager call block (`:59-67`):

```csharp
public sealed class SchemaRegistrationOrchestrator(
    IRecordStoreSchemaManager schemaManager,
    IEmbeddingService embedding,
    SchemaRegistry registry)
    : ISchemaRegistrationOrchestrator
```

Remove the `try { await starRocks.ApplyTableAsync(...) } catch (StarRocksNotReadyException ex) { ... }` block entirely (`:59-67`) — no replacement call; provisioning is now lazy, on first write, per Task 4.

- [ ] **Step 4: Update `ServiceCollectionExtensions.cs`** — remove the `StarRocksSchemaManager`/`IEngagementStoreSchemaManager` registration block (`:22-27`).

- [ ] **Step 5: Update `SchemaRegistrationOrchestratorTests.cs`** — remove `RegisterAsync_CallsApplyTableAsync_WithMatchingTableName` (`:138`) and any `IEngagementStoreSchemaManager` substitute setup that only existed to support it. Confirm no other test in this file depends on `IEngagementStoreSchemaManager`.

- [ ] **Step 6: Migrate `StarRocksIntegrationTests.cs` off `ApplyTableAsync`**

In `StarRocksContainerFixture` (`:11-120`): remove the `SchemaManager` property and its construction (`:23,45`) — no longer needed.

In `StarRocksIntegrationTests.cs`, remove the `_schemaManager` field (`:126`). Replace `CreateAndSeedAuthorsAsync`'s `await _schemaManager.ApplyTableAsync(schema);` (`:150`) with direct DDL execution via the repository, reusing `StarRocksSchemaManager.BuildCreateTableDdl` (now `internal static`, callable directly since this test project already references `Iverson.StarRocks`):

```csharp
await repo.ExecuteAsync(StarRocksSchemaManager.BuildCreateTableDdl(schema, $"`{tableName}`"));
```

(Unqualified — these tests exercise the base `iverson_test` database, not tenant isolation; Task 7 adds the tenant-qualified integration tests separately.)

Apply the same replacement at the other 3 `_schemaManager.ApplyTableAsync(...)` call sites (`:283,320,533`) — read each site's surrounding context first, since they construct different `StarRocksTableSchema` instances inline; substitute each with `await _repo.ExecuteAsync(StarRocksSchemaManager.BuildCreateTableDdl(<that site's schema variable>, $"`{<that site's table variable>}`"));` using the same variable already in scope at each site.

- [ ] **Step 7: Build and run**
```bash
dotnet build Iverson.Server/Iverson.Api
dotnet build Iverson.Server/Iverson.StarRocks
dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~SchemaRegistrationOrchestrator"
dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "Category=Integration"
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs Iverson.Server/Iverson.StarRocks/StarRocksSchemaManager.cs Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs
git commit -m "fix(starrocks): remove eager ApplyTableAsync now that tenant provisioning is lazy"
```

### Task 6: Deployment — grant `user_admin`

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml`

- [ ] **Step 1: Add the grant** to the existing `mysql -e` block (`:68-76`), after the existing `GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';` line:

```sql
GRANT user_admin TO 'iverson_app'@'%';
```

- [ ] **Step 2: Commit**
```bash
git add "Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml"
git commit -m "feat(starrocks): grant user_admin to iverson_app for tenant provisioning"
```

### Task 7: Integration tests for the privilege boundary

**Files:**
- Test: `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs`

**Interfaces:**
- Consumes: `StarRocksContainerFixture` (existing, unmodified — Task 5 only removed its `SchemaManager` property), Tasks 1-4's tenant-scoping mechanism.

- [ ] **Step 1: Create the test class**, reusing `StarRocksContainerFixture` (connects as `root`) to bootstrap `iverson_app` + `user_admin` once, then building a second `StarRocksRepository` pointed at the same container but authenticating as `iverson_app`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

[Trait("Category", "Integration")]
public sealed class TenantIsolationIntegrationTests : IClassFixture<StarRocksContainerFixture>
{
    private readonly StarRocksRepository _appRepo;

    public TenantIsolationIntegrationTests(StarRocksContainerFixture fx)
    {
        // fx.Repository connects as root — reuse it to create iverson_app + user_admin, mirroring
        // job-create-user.yaml. Idempotent (IF NOT EXISTS / re-GRANT), safe if xUnit re-runs this
        // constructor per test in the class.
        fx.Repository.ExecuteAsync("""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'test_pw';
            GRANT user_admin TO 'iverson_app'@'%';
            """).GetAwaiter().GetResult();

        var appConnectionString = new MySqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = "iverson_app",
            Password = "test_pw"
        }.ToString();

        _appRepo = new StarRocksRepository(appConnectionString, NullLogger<StarRocksRepository>.Instance);
    }

    private static StarRocksTableSchema ProbeSchema(string tableName) =>
        new(tableName, new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Name", "STRING", true)]);
}
```

- [ ] **Step 2: Write the privilege-boundary tests**, covering (per the spec's Testing Approach section):

- `EnsureTenantProvisionedAsync_ThenUpsert_SucceedsForOwnTenant` — provision tenant `a`, upsert a row, confirm it's readable via `SearchAsync` with `authz` carrying tenant `a`'s constraint.
- `SearchAsync_ForOtherTenant_ReturnsNoRows` — with tenant `a` provisioned and seeded, `SearchAsync` scoped to tenant `b` (never provisioned) returns an empty result — proving the privilege boundary itself, not application-level `WHERE`-filtering (tenant `b`'s constraint's `TenantValue` differs, so this exercises `SET ROLE role_tenant_b`, which has no grant on `iverson_tenant_a`).
- `EnsureTenantProvisionedAsync_CalledTwice_IsIdempotent` — provisioning the same tenant twice does not throw.
- `EnsureTenantProvisionedAsync_ThenSecondType_WildcardGrantCoversNewTable` — provision tenant `a` for one table, then call `EnsureTenantProvisionedAsync` for a second table under the same tenant; confirm both are usable without an intervening extra grant (locks in the spec's wildcard-grant-covers-future-table verified assumption at the application-code level, not just raw SQL).
- `UpsertAsync_WithInvalidTenantId_IsNoOp` — a tenant ID failing `TenantIdentifier.IsValid` (e.g. containing a backtick) results in no exception and no row written — confirms fail-closed at the application layer, not just the identifier-allowlist unit test from Task 1.
- `SearchAsync_WithAuthzNull_UsesUnscopedSharedDatabase` — locks in the Task 2 Step 4 resolution: calling `SearchAsync` with `authz: null` still executes (against the shared/default database), confirming the plan's `authz is null → unscoped` behavior rather than silently returning empty.

- [ ] **Step 3: Run**
```bash
dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "FullyQualifiedName~TenantIsolationIntegrationTests"
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs
git commit -m "test(starrocks): cover the per-tenant database privilege boundary end-to-end"
```

## Tasks NOT in this plan

Explicitly out of scope for this spec: any change to Part A's application-level `WHERE`-clause enforcement (unchanged, this is an additive backstop only); backfilling tenant values or re-homing rows for pre-existing data in the current shared `iverson` StarRocks database; StarRocks transport TLS (a real pre-existing gap, structurally identical to the one B2 closed for Qdrant, but not requested as part of this design — see Known limitations).

## Known issues inherited from spec

- **Database-count growth.** Every tenant now costs one physical StarRocks database (holding all of that tenant's type tables) — a real per-tenant resource-count cost, the same category of tradeoff B2 accepted for Qdrant's collection-per-tenant growth. No live tenants exist and tenant-count expectations are not yet known, so no mitigation (e.g. capping, sharding) is designed here — an accepted operational risk to revisit once real scale expectations exist.
- **The pre-existing shared `iverson` database's fate is not addressed.** Rows in the current single shared database predate this design; no backfill or migration path into per-tenant databases is designed. Whether the old shared database is kept as a dead relic or dropped is a deploy-time decision outside this spec's scope.
- **StarRocks transport TLS.** The MySQL wire protocol connection between the app and StarRocks is not TLS'd today — the same category of gap B2 found and closed for Qdrant, but not requested as part of this design. `SET ROLE` statements and query results cross the wire in plaintext, unchanged by this design. Flagged for awareness, not solved here.
- **Pre-Part-A legacy rows with a null tenant value.** Mirrors B1/B2's identical carve-out: such a row's upsert-path tenant re-derivation finds null, so the write is skipped via the fail-closed no-role-active default rather than reaching an unfiltered/unscoped table. Moot in practice today — no live tenant data exists yet.
- **Schema evolution for already-provisioned tenants is not propagated.** Today, `StarRocksSchemaManager.ApplyTableAsync` diffs an existing table's columns against the current schema and runs `ALTER TABLE ADD COLUMN` for anything missing, invoked eagerly on every `RegisterSchema` call — so a schema change (e.g. a newly-added field) propagates to the single shared table automatically. This design's lazy `CREATE TABLE IF NOT EXISTS` runs once, on a tenant's first write for a type, and nothing re-diffs or re-applies a later schema change to a tenant whose table already exists. A tenant provisioned before a schema change will not receive that change's new columns; this is explicitly out of scope for this design and would require a separate, future mechanism (or manual DDL) to address.
