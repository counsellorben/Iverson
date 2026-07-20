# Tenant Admin APIs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-20-tenant-admin-apis-design.md` (commit SHA: `106454ea6a53addaa288de70309e18110381d3ea`)

**Goal:** Add two gRPC admin surfaces — a system-wide tenant lifecycle API (operator-facing) and a delegated per-tenant admin API — closing the gap where tenant/user provisioning is entirely manual today.

**Architecture:** A new Postgres-backed `IversonTenants` registry tracks tenant existence/status. A new `IAuthentikAdminClient` orchestrates Authentik's REST API for actual user/group provisioning (Authentik remains the only identity store). Two new gRPC services expose lifecycle operations (`TenantLifecycleGrpcService`, Operator-gated) and delegated user management (`TenantAdminGrpcService`, new `TenantAdmin`-policy-gated, scoped to the caller's own tenant). A tenant-status check is added to the existing tenant-enforcement path (`ActingUserInterceptor`) plus inline in `TenantAdminGrpcService` itself (that service's caller doesn't go through the acting-user-impersonation mechanism the interceptor's check is otherwise gated on).

**Tech stack:** .NET 10 / ASP.NET Core, gRPC (`Grpc.AspNetCore` 2.80.0 + new `Grpc.AspNetCore.Web`), Dapper via `IRecordStoreQueryExecutor`, Postgres, Authentik REST API, xUnit/NSubstitute/FluentAssertions.

---

## Global Constraints

- `tenant_id`/tenant registry `Id` format: `TenantIdentifier.IsValid` (regex `^(?!.*--)([A-Za-z0-9_-]{1,52})$`).
- Tenant `Status` closed set: `"active"` | `"suspended"` | `"deleted"` — no other value is ever written or checked.
- All new services/repositories/caches are registered `AddSingleton` in `Program.cs`, matching every existing sibling (`AuditLog`, `IDlqRepository`, etc.).
- The Authentik automation user backing `IAuthentikAdminClient`'s bearer token is a **superuser** (explicit decision — avoids Authentik's non-superuser RBAC permission-codename uncertainty).
- No cascading data deletion across Postgres/StarRocks/Qdrant, no email/invite flow, no CORS configuration, no web UI — all explicitly out of scope per the spec.
- `TenantAdminGrpcService` never accepts a caller-suppliable `tenant_id` — it always derives the caller's own tenant from `context.GetHttpContext().User`, never from `IActingUserAccessor.ActingUser`.
- `RemoveUser`/`SetTenantAdmin` must verify the request's `user_id` belongs to the caller's own tenant (via `ListUsersByTenantAsync`) before acting, rejecting with `PermissionDenied` on mismatch.

## File Structure

**Create:**
- `Iverson.Server/Iverson.Api/Tenancy/TenantSchema.cs`
- `Iverson.Server/Iverson.Api/Tenancy/ITenantStatusCache.cs`, `TenantStatusCache.cs`
- `Iverson.Server/Iverson.Api/Tenancy/IAuthentikAdminClient.cs`, `AuthentikAdminClient.cs`
- `Iverson.Server/Iverson.Api/TenantAdminAuthorizationPolicy.cs`
- `Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs`
- `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs`
- `Iverson.Server/Iverson.Sql/TenantRepository.cs`
- `Iverson.Clients/Common/Proto/tenant_lifecycle.proto`, `tenant_admin.proto`
- Corresponding test files (see per-task lists below)

**Modify:**
- `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (add `ITenantRepository`/`TenantRow`)
- `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs` (class + `IsValid` → `public`)
- `Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs` (suspension check)
- `Iverson.Server/Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs` (add `"TenantAdmin"`)
- `Iverson.Server/Iverson.Api/Iverson.Api.csproj` (add `Grpc.AspNetCore.Web`)
- `Iverson.Server/Iverson.Api/Program.cs` (DI registrations, startup wiring, gRPC mapping, policies — across Tasks 1, 2, 4, 5)
- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- `docs/user-management-and-security.md`

## Inherited from spec

Trusted as ground truth (all 16 rows of the spec's Verified Assumptions section — not re-verified here), most relevantly:
- `IDlqRepository`/`DlqRepository` is the closest existing analog for a flat-CRUD registry table; `DlqSchema.Table` is applied via `PostgresSchemaManager.ApplySchemaAsync`, not raw SQL.
- Every `tenant_id` claim read for authorization purposes goes through `IActingUserAccessor.ActingUser`, populated only by `ActingUserInterceptor.ValidateActingUserAsync` when the acting-user-impersonation header is present.
- `app.UseAuthorization()` applies uniformly to HTTP and gRPC endpoints; `AuditingAuthorizationMiddlewareResultHandler`'s rejection-audit logic already covers any `RequireAuthorization(...)`-gated gRPC service.
- `ServerCallContext.GetHttpContext().User` is a valid, populated principal for any ASP.NET-Core-hosted gRPC service in production.
- The 5 pre-existing `tenant_id` values needing registry backfill: `tenant-loadtest`, `tenant-webtest`, `tenant-admin`, `tenant-smoke-test`, `tenant-bypass`.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `IRecordStoreRoles.cs` exists at `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, holds `IDlqRepository`/`TableSchema`/`ColumnSchema` | Read of file, lines 1-91 |
| 2 | File path | `DlqRepository.cs`, `DlqSchema.cs`, `PostgresSchemaManager.cs` exist at their expected `Iverson.Sql`/`Iverson.Api/Reconciliation` paths | Read of all three files in full |
| 3 | File path | `TenantIdentifier.cs`, `ActingUserInterceptor.cs`, `AuditingAuthorizationMiddlewareResultHandler.cs`, `Program.cs`, `Iverson.Api.csproj` all exist at their expected paths | Read of all five files this session |
| 4 | File path | `Iverson.Clients/Common/Proto/` holds all 4 existing `.proto` files; `Iverson.Client.Contracts.csproj:17` includes them via `Protobuf Include="../../Common/Proto/*.proto"` (a wildcard — a new file needs no csproj edit) | Read of directory listing + csproj |
| 5 | File path | `docs/user-management-and-security.md` and both blueprint files (`compose-only/service-clients.yaml`, `blueprints-configmap-service-clients.yaml`) exist at their expected paths | Read of all three this/prior session |
| 6 | Function signature | `TableSchema(string TableName, ColumnSchema KeyColumn, IReadOnlyList<ColumnSchema> Columns, string? TenantColumn = null)`; `ColumnSchema(string Name, string SqlType, bool IsNullable)` | `IRecordStoreRoles.cs:84-90` |
| 7 | Function signature | `IRecordStoreSchemaManager.ApplySchemaAsync(TableSchema)` creates the table via `CREATE TABLE IF NOT EXISTS` when `existingColumns.Count == 0`, applies RLS only `if (schema.TenantColumn is not null)` — our table passes `null`, so no RLS policy is applied | `PostgresSchemaManager.cs:14-123` (full read) |
| 8 | Function signature | `AuditLog.AdminOperation(ClaimsPrincipal actor, string operation, string? detail)`; `OperatorAuthorizationPolicy.IsSatisfiedBy(IEnumerable<string> groupClaims, string? scopeClaim)` | `AuditLog.cs`, `OperatorAuthorizationPolicy.cs` (both re-read this session) |
| 9 | Function signature | `ActingUserInterceptor`'s current constructor is `(ILogger<ActingUserInterceptor> logger)`; `ValidateActingUserAsync` returns immediately if the `x-acting-user-authorization` header is absent, otherwise authenticates via the `"ActingUser"` scheme and sets `IActingUserAccessor.ActingUser` | `ActingUserInterceptor.cs:7-49` (full read) |
| 10 | Function signature | `AuditingAuthorizationMiddlewareResultHandler.AuditedPolicies` is `private static readonly HashSet<string> AuditedPolicies = ["SchemaAdmin", "Operator"];` | `AuditingAuthorizationMiddlewareResultHandler.cs` (full read) |
| 11 | Function signature | `ActingUserFixtures.Principal(sub, groups...)` defaults `tenant_id` to `"test-tenant"`; `PrincipalWithTenant(sub, tenantId, groups...)` for explicit control; `TestJwtFactory.CreateToken(audience, subject, expires?, extraClaims?)`; `AuthTestWebApplicationFactory` swaps `IEmbeddingService`/`ISchemaRegistryRepository`/`IRecordStoreSchemaManager` for no-ops and configures both JWT schemes with a test signing key | `ActingUserFixtures.cs`, `TestJwtFactory.cs`, `AuthTestWebApplicationFactory.cs` (all read in full this session) |
| 12 | Command | `dotnet test <ProjectName>` / `dotnet build` run from `Iverson.Server/` is the established invocation (no `.sln` file at the repo or `Iverson.Server` root; every prior task in this initiative invoked per-project) | `find` for `*.sln` returned nothing; matches this session's own prior SDD dispatches |
| 13 | Task ordering | Task 2 (`ITenantStatusCache`) depends only on Task 1's `ITenantRepository`; Task 5 depends on Task 2's `ITenantStatusCache`, Task 3's `IAuthentikAdminClient`, and Task 4's grpc-web middleware setup — no task references a symbol a *later* task introduces | Cross-checked each task's planned imports against every other task's produced symbols |
| 14 | Code-in-plan validity | `Grpc.AspNetCore.Web` is released in lockstep with `Grpc.AspNetCore` by the grpc-dotnet project; pinning it to the same `2.80.0` already referenced is the established compatibility convention for this package family | `Iverson.Api.csproj:19` (`Grpc.AspNetCore` version); grpc-dotnet's own versioning convention |
| 15 | Consumer impact | No existing test sends the `x-acting-user-authorization` header, so `ActingUserInterceptor`'s new suspension check (which only runs inside that header-present branch) has zero impact on any existing test | `grep -rln "x-acting-user-authorization\|AddHeader.*[Aa]cting" Iverson.Server/Iverson.Api.Tests/` returned nothing |
| 16 | Consumer impact | No existing test asserts `AuditingAuthorizationMiddlewareResultHandler.AuditedPolicies`' exact membership (only specific-policy-rejection behavior), so adding `"TenantAdmin"` doesn't break `AuditingAuthorizationMiddlewareResultHandlerTests.cs`'s 2 existing tests | `grep -n "AuditedPolicies"` against that test file returned nothing |
| 17 | Consumer impact | `TenantIdentifier` is used within `Iverson.StarRocks` itself (`StarRocksPipelineBuilder.cs`, `StarRocksQueryBuilder.cs`, `StarRocksRepository.cs`) and tested in `Iverson.StarRocks.Tests` — widening `IsValid` from `internal` to `public` doesn't affect any of these internal callers (a public method remains freely callable from the same assembly) | `grep -rln "TenantIdentifier" Iverson.Server --include="*.cs"` — 6 hits, all either the declaration or same-assembly internal callers |
| 18 | Code-in-plan validity | This codebase's established convention for mocking an outbound `HttpClient` in a unit test is a custom `HttpMessageHandler` subclass overriding `SendAsync`, wrapped in `new HttpClient(handler)`, returned by a mocked `IHttpClientFactory` — not a bare injected `HttpClient` | `Iverson.Embeddings.Tests/EmbeddingServiceTests.cs:14-40` (full pattern read); **this changed Task 3's constructor shape from a bare `HttpClient` to `IHttpClientFactory` — a mechanical fix, applied silently, noted here** |

## Tasks

### Task 1: Tenant registry data model

**Files:**
- Create: `Iverson.Server/Iverson.Api/Tenancy/TenantSchema.cs`
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`
- Create: `Iverson.Server/Iverson.Sql/TenantRepository.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Sql.Tests/TenantRepositoryTests.cs`

**Interfaces:**
- Produces: `ITenantRepository`, `TenantRow`, `TenantSchema.Table`/`TenantSchema.TableName` — consumed by Tasks 2, 4, 5.

- [ ] **Step 1: Widen `TenantIdentifier.IsValid` to public**

In `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`, change only the class and `IsValid` modifiers (leave `DatabaseName`/`RoleName`/`Qualify`/`AllowedPattern` as-is):

```csharp
using System.Text.RegularExpressions;

namespace Iverson.StarRocks;

public static class TenantIdentifier
{
    private static readonly Regex AllowedPattern = new("^(?!.*--)([A-Za-z0-9_-]{1,52})$", RegexOptions.Compiled);

    public static bool IsValid(string tenantId) => AllowedPattern.IsMatch(tenantId);

    internal static string DatabaseName(string tenantId) => $"iverson_tenant_{tenantId}";
    internal static string RoleName(string tenantId) => $"role_tenant_{tenantId}";
    internal static string Qualify(string? tenantDatabase, string tableName) =>
        tenantDatabase is null ? $"`{tableName}`" : $"`{tenantDatabase}`.`{tableName}`";
}
```

- [ ] **Step 2: Add `ITenantRepository`/`TenantRow` to `IRecordStoreRoles.cs`**

Add near `IDlqRepository` (after its closing brace, before the `TableSchema`/`ColumnSchema` records):

```csharp
public interface ITenantRepository
{
    Task InsertAsync(string id, string displayName, string status);
    Task SeedIfMissingAsync(string id, string displayName, string status);
    Task<TenantRow?> GetAsync(string id);
    Task<IEnumerable<TenantRow>> ListAsync();
    Task UpdateStatusAsync(string id, string status);
    Task DeleteAsync(string id);
}

public sealed record TenantRow(string Id, string DisplayName, string Status, DateTimeOffset CreatedAt);
```

- [ ] **Step 3: Create `TenantRepository.cs`** (mirrors `DlqRepository.cs` exactly)

```csharp
namespace Iverson.Sql;

public sealed class TenantRepository(
    string tableName,
    IRecordStoreQueryExecutor sql) : ITenantRepository
{
    public Task InsertAsync(string id, string displayName, string status) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "DisplayName", "Status", "CreatedAt")
            VALUES (@Id, @DisplayName, @Status, @CreatedAt)
            """,
            new { Id = id, DisplayName = displayName, Status = status, CreatedAt = DateTimeOffset.UtcNow });

    public Task SeedIfMissingAsync(string id, string displayName, string status) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "DisplayName", "Status", "CreatedAt")
            VALUES (@Id, @DisplayName, @Status, @CreatedAt)
            ON CONFLICT ("Id") DO NOTHING
            """,
            new { Id = id, DisplayName = displayName, Status = status, CreatedAt = DateTimeOffset.UtcNow });

    public Task<TenantRow?> GetAsync(string id) =>
        sql.QuerySingleOrDefaultAsync<TenantRow>(
            $"""SELECT "Id", "DisplayName", "Status", "CreatedAt" FROM "{tableName}" WHERE "Id" = @Id""",
            new { Id = id });

    public Task<IEnumerable<TenantRow>> ListAsync() =>
        sql.QueryAsync<TenantRow>(
            $"""SELECT "Id", "DisplayName", "Status", "CreatedAt" FROM "{tableName}" ORDER BY "CreatedAt" """);

    public Task UpdateStatusAsync(string id, string status) =>
        sql.ExecuteAsync(
            $"""UPDATE "{tableName}" SET "Status" = @Status WHERE "Id" = @Id""",
            new { Id = id, Status = status });

    public Task DeleteAsync(string id) =>
        sql.ExecuteAsync(
            $"""DELETE FROM "{tableName}" WHERE "Id" = @Id""",
            new { Id = id });
}
```

- [ ] **Step 4: Create `TenantSchema.cs`** (mirrors `DlqSchema.cs`)

```csharp
using Iverson.Sql;

namespace Iverson.Api.Tenancy;

internal static class TenantSchema
{
    public const string TableName = "IversonTenants";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("Id", "text", false),
        new List<ColumnSchema>
        {
            new("DisplayName", "text", false),
            new("Status",      "text", false),
            new("CreatedAt",   "timestamptz", false),
        });
}
```

- [ ] **Step 5: Wire into `Program.cs`**

Add to the `AddSingleton` cluster (near the existing `IDlqRepository` registration, ~line 196):

```csharp
builder.Services.AddSingleton<ITenantRepository>(sp => new TenantRepository(
    Iverson.Api.Tenancy.TenantSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
```

Add to the schema-hydration block (right after the existing `DlqSchema.Table` call, ~line 350) and seed the 5 legacy tenants:

```csharp
await schemaManager.ApplySchemaAsync(Iverson.Api.Tenancy.TenantSchema.Table);

var tenantRepository = app.Services.GetRequiredService<ITenantRepository>();
foreach (var legacyTenantId in new[] { "tenant-loadtest", "tenant-webtest", "tenant-admin", "tenant-smoke-test", "tenant-bypass" })
    await tenantRepository.SeedIfMissingAsync(legacyTenantId, legacyTenantId, "active");
```

- [ ] **Step 6: Tests** — `Iverson.Sql.Tests/TenantRepositoryTests.cs`, mirroring `DlqRepositoryTests.cs`'s exact shape (mocked `IRecordStoreQueryExecutor` via `Substitute.For`, asserting the generated SQL text and params for each of the 6 methods).

- [ ] **Step 7: Build and test**
```bash
cd Iverson.Server
dotnet build
dotnet test Iverson.Sql.Tests
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Api/Tenancy/TenantSchema.cs Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Sql/TenantRepository.cs Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Sql.Tests/TenantRepositoryTests.cs
git commit -m "feat(api,sql): add tenant registry data model"
```

---

### Task 2: Suspension enforcement

**Files:**
- Create: `Iverson.Server/Iverson.Api/Tenancy/ITenantStatusCache.cs`, `TenantStatusCache.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/TenantStatusCacheTests.cs`, `Iverson.Server/Iverson.Api.Tests/Grpc/ActingUserInterceptorSuspensionTests.cs`

**Interfaces:**
- Consumes: Task 1's `ITenantRepository`.
- Produces: `ITenantStatusCache` — consumed by Task 5.

- [ ] **Step 1: Create `ITenantStatusCache.cs`**

```csharp
namespace Iverson.Api.Tenancy;

public interface ITenantStatusCache
{
    Task<string?> GetStatusAsync(string tenantId);
}
```

- [ ] **Step 2: Create `TenantStatusCache.cs`**

```csharp
using Iverson.Sql;
using Microsoft.Extensions.Caching.Memory;

namespace Iverson.Api.Tenancy;

public sealed class TenantStatusCache(
    ITenantRepository tenantRepository,
    IMemoryCache cache) : ITenantStatusCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<string?> GetStatusAsync(string tenantId)
    {
        if (cache.TryGetValue(tenantId, out string? cachedStatus))
            return cachedStatus;

        var tenant = await tenantRepository.GetAsync(tenantId);
        var status = tenant?.Status;
        cache.Set(tenantId, status, Ttl);
        return status;
    }
}
```

- [ ] **Step 3: Modify `ActingUserInterceptor.cs`**

```csharp
using Grpc.Core;
using Grpc.Core.Interceptors;
using Iverson.Api.Tenancy;
using Microsoft.AspNetCore.Authentication;

namespace Iverson.Api.Grpc;

public sealed class ActingUserInterceptor(
    ILogger<ActingUserInterceptor> logger,
    ITenantStatusCache tenantStatusCache) : Interceptor
{
    public const string MetadataKey = "x-acting-user-authorization";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await ValidateActingUserAsync(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await ValidateActingUserAsync(context);
        await continuation(request, responseStream, context);
    }

    private async Task ValidateActingUserAsync(ServerCallContext context)
    {
        var header = context.RequestHeaders.Get(MetadataKey)?.Value;
        if (string.IsNullOrEmpty(header))
            return;

        var httpContext = context.GetHttpContext();
        var result = await httpContext.AuthenticateAsync("ActingUser");
        if (!result.Succeeded || result.Principal is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Acting-user token is invalid."));

        var tenantId = result.Principal.FindFirst("tenant_id")?.Value;
        if (tenantId is not null)
        {
            var status = await tenantStatusCache.GetStatusAsync(tenantId);
            if (status is null or "suspended" or "deleted")
                throw new RpcException(new Status(StatusCode.PermissionDenied, $"Tenant '{tenantId}' is not active."));
        }

        httpContext.RequestServices.GetRequiredService<IActingUserAccessor>().ActingUser = result.Principal;

        var serviceSubject    = httpContext.User.FindFirst("sub")?.Value ?? "unknown";
        var actingUserSubject = result.Principal.FindFirst("sub")?.Value ?? "unknown";
        logger.LogInformation(
            "service {ServiceAccountSubject} acting as user {ActingUserSub} called {Method}",
            serviceSubject, actingUserSubject, context.Method);
    }
}
```

- [ ] **Step 4: Wire into `Program.cs`**

```csharp
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ITenantStatusCache, TenantStatusCache>();
```

- [ ] **Step 5: Tests**
  - `TenantStatusCacheTests.cs`: unit tests with `Substitute.For<ITenantRepository>()` + a real `MemoryCache` (or mocked `IMemoryCache`), covering cache-hit/cache-miss and null/active/suspended status.
  - `ActingUserInterceptorSuspensionTests.cs`: an `AuthTestWebApplicationFactory`-based pipeline test substituting `ITenantStatusCache` via `WithWebHostBuilder(builder => builder.ConfigureServices(services => { services.RemoveAll<ITenantStatusCache>(); services.AddSingleton(fakeCache); }))` (mirrors Part C's `AuditingAuthorizationMiddlewareResultHandlerTests.cs` substitution pattern) — send a request with the acting-user header carrying a `tenant_id` the fake cache reports as `"suspended"`, assert `PermissionDenied`.

- [ ] **Step 6: Build and test**
```bash
cd Iverson.Server
dotnet build
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~TenantStatusCache|FullyQualifiedName~ActingUserInterceptorSuspension"
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Api/Tenancy/ITenantStatusCache.cs Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Tenancy/TenantStatusCacheTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ActingUserInterceptorSuspensionTests.cs
git commit -m "feat(api): enforce tenant suspension in ActingUserInterceptor"
```

---

### Task 3: Authentik orchestration client

**Files:**
- Create: `Iverson.Server/Iverson.Api/Tenancy/IAuthentikAdminClient.cs`, `AuthentikAdminClient.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Test: `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs`

**Interfaces:**
- Produces: `IAuthentikAdminClient` — consumed by Tasks 4, 5.

**Not fully verified** (mirrors the spec's own flagged caveat): Authentik's exact Core-API JSON field names for user creation/`attributes`/`groups`/`set_password`, and the blueprint's exact field name for granting superuser status (shown below as `is_superuser`, unverified against any existing blueprint in this repo), are grounded in Authentik's documented DRF conventions and a confirmed separate `set_password` endpoint, but not verified against a live instance — double-check against a running Authentik (or its OpenAPI schema at `/api/v3/schema/`) while implementing this task, and adjust field names if they differ.

- [ ] **Step 1: Create `IAuthentikAdminClient.cs`**

```csharp
namespace Iverson.Api.Tenancy;

public sealed record AuthentikUser(string Id, string Username, string Email);

public interface IAuthentikAdminClient
{
    Task<string> CreateUserAsync(string username, string email, string password, string tenantId, IReadOnlyList<string> groups);
    Task<IEnumerable<AuthentikUser>> ListUsersByTenantAsync(string tenantId);
    Task DeactivateUserAsync(string userId);
    Task DeactivateAllUsersInTenantAsync(string tenantId);
    Task AddGroupAsync(string userId, string groupName);
    Task RemoveGroupAsync(string userId, string groupName);
}
```

- [ ] **Step 2: Create `AuthentikAdminClient.cs`**, following the `IHttpClientFactory` + `HttpMessageHandler`-testable convention `EmbeddingService` already establishes (constructor takes `IHttpClientFactory`, resolves a named client, calls Authentik's Core API). Resolve group names to PKs via `GET /api/v3/core/groups/?name={name}` before referencing them; create users via `POST /api/v3/core/users/`, set the password via `POST /api/v3/core/users/{id}/set_password/`; `ListUsersByTenantAsync` fetches all pages of `GET /api/v3/core/users/` and filters client-side on `attributes.tenant_id` (the accepted fallback per the spec's uncovered span-check item).

- [ ] **Step 3: Wire into `Program.cs`** — register a named `HttpClient` (base address + bearer token from config) and `AddSingleton<IAuthentikAdminClient, AuthentikAdminClient>()`.

- [ ] **Step 4: Blueprint changes** — mirroring the existing `iverson-loadtest-bypass` group + user pattern exactly. The `authentik_core.group` and `authentik_core.user` entries are identical in both files; the `authentik_core.token` entry's `key` differs between compose (literal hardcoded value) and kind (Helm-templated), matching how every other secret-backed value in these two files already differs.

**Compose** (`service-clients.yaml`):
```yaml
  - model: authentik_core.group
    identifiers:
      name: tenant-admins
    attrs: {}
  - model: authentik_core.user
    identifiers:
      username: iverson-admin-orchestrator
    attrs:
      username: iverson-admin-orchestrator
      name: "Iverson Admin Orchestrator"
      email: iverson-admin-orchestrator@example.invalid
      password: "dev-only-not-for-production-admin-orchestrator-password"
      is_active: true
      is_superuser: true
  - model: authentik_core.token
    identifiers:
      identifier: iverson-admin-orchestrator-token
    attrs:
      key: "dev-only-not-for-production-admin-orchestrator-token"
      user: !Find [authentik_core.user, [username, iverson-admin-orchestrator]]
      intent: api
```

**Kind** (`blueprints-configmap-service-clients.yaml`): the same `authentik_core.group` entry, plus:
```yaml
  - model: authentik_core.user
    identifiers:
      username: iverson-admin-orchestrator
    attrs:
      username: iverson-admin-orchestrator
      name: "Iverson Admin Orchestrator"
      email: iverson-admin-orchestrator@example.invalid
      password: {{ if $adminOrchestratorUser }}{{ index $adminOrchestratorUser.data "password" | b64dec }}{{ else }}{{ randAlphaNum 32 }}{{ end }}
      is_active: true
      is_superuser: true
  - model: authentik_core.token
    identifiers:
      identifier: iverson-admin-orchestrator-token
    attrs:
      key: {{ if $adminOrchestratorToken }}{{ index $adminOrchestratorToken.data "token-key" | b64dec }}{{ else }}{{ randAlphaNum 50 }}{{ end }}
      user: !Find [authentik_core.user, [username, iverson-admin-orchestrator]]
      intent: api
```

Add paired `Secret` blocks to `secret-service-clients.yaml` (mirroring the other 6 exactly — one storing a `password` data field for the user, one storing a `token-key` data field for the token) and their `$adminOrchestratorUser := lookup ...`/`$adminOrchestratorToken := lookup ...` declarations to the ConfigMap template, alongside the existing `$loadtest`/`$webtest`/etc. lookups.

- [ ] **Step 5: Tests** — `AuthentikAdminClientTests.cs` using the `FakeHttpMessageHandler : HttpMessageHandler` + mocked `IHttpClientFactory` pattern from `EmbeddingServiceTests.cs`, covering each of the 6 client methods against a fixed fake response.

- [ ] **Step 6: Build and test**
```bash
cd Iverson.Server
dotnet build
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~AuthentikAdminClient"
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Api/Tenancy/IAuthentikAdminClient.cs Iverson.Server/Iverson.Api/Tenancy/AuthentikAdminClient.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs
git commit -m "feat(api,deploy): add Authentik admin orchestration client"
```

---

### Task 4: `TenantLifecycleGrpcService`

**Files:**
- Create: `Iverson.Clients/Common/Proto/tenant_lifecycle.proto`
- Create: `Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api/Iverson.Api.csproj`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's `ITenantRepository`, Task 3's `IAuthentikAdminClient`.
- Produces: the one-time `Grpc.AspNetCore.Web` package reference + `app.UseGrpcWeb()` middleware — consumed by Task 5.

- [ ] **Step 1: Create `tenant_lifecycle.proto`**

```proto
syntax = "proto3";
option csharp_namespace = "Iverson.Client.Contracts";
package iverson;

import "google/protobuf/empty.proto";

service TenantLifecycleGrpcService {
    rpc CreateTenant     (CreateTenantRequest)    returns (Tenant);
    rpc ListTenants      (ListTenantsRequest)     returns (ListTenantsResponse);
    rpc SuspendTenant    (SuspendTenantRequest)    returns (Tenant);
    rpc ReactivateTenant (ReactivateTenantRequest) returns (Tenant);
    rpc DeleteTenant     (DeleteTenantRequest)     returns (google.protobuf.Empty);
}

message CreateTenantRequest {
    string tenant_id              = 1;
    string display_name           = 2;
    string admin_username         = 3;
    string admin_email            = 4;
    string admin_initial_password = 5;
}

message ListTenantsRequest {
}

message ListTenantsResponse {
    repeated Tenant tenants = 1;
}

message SuspendTenantRequest {
    string tenant_id = 1;
}

message ReactivateTenantRequest {
    string tenant_id = 1;
}

message DeleteTenantRequest {
    string tenant_id = 1;
}

message Tenant {
    string tenant_id    = 1;
    string display_name = 2;
    string status       = 3;
}
```

- [ ] **Step 2: Create `TenantLifecycleGrpcService.cs`**

```csharp
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Tenancy;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;

namespace Iverson.Api.Grpc;

public sealed class TenantLifecycleGrpcService(
    ITenantRepository tenantRepository,
    IAuthentikAdminClient authentikAdminClient,
    AuditLog auditLog) : Iverson.Client.Contracts.TenantLifecycleGrpcService.TenantLifecycleGrpcServiceBase
{
    public override async Task<Tenant> CreateTenant(CreateTenantRequest request, ServerCallContext context)
    {
        if (!TenantIdentifier.IsValid(request.TenantId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.TenantId}' is not a valid tenant id."));

        await tenantRepository.InsertAsync(request.TenantId, request.DisplayName, "active");

        try
        {
            await authentikAdminClient.CreateUserAsync(
                request.AdminUsername, request.AdminEmail, request.AdminInitialPassword,
                request.TenantId, ["tenant-admins"]);
        }
        catch
        {
            await tenantRepository.DeleteAsync(request.TenantId);
            throw;
        }

        auditLog.AdminOperation(context.GetHttpContext().User, "CreateTenant", request.TenantId);
        return new Tenant { TenantId = request.TenantId, DisplayName = request.DisplayName, Status = "active" };
    }

    public override async Task<ListTenantsResponse> ListTenants(ListTenantsRequest request, ServerCallContext context)
    {
        var tenants = await tenantRepository.ListAsync();
        var response = new ListTenantsResponse();
        response.Tenants.AddRange(tenants.Select(ToProto));
        return response;
    }

    public override Task<Tenant> SuspendTenant(SuspendTenantRequest request, ServerCallContext context) =>
        SetStatusAsync(request.TenantId, "suspended", "SuspendTenant", context);

    public override Task<Tenant> ReactivateTenant(ReactivateTenantRequest request, ServerCallContext context) =>
        SetStatusAsync(request.TenantId, "active", "ReactivateTenant", context);

    public override async Task<Empty> DeleteTenant(DeleteTenantRequest request, ServerCallContext context)
    {
        var tenant = await tenantRepository.GetAsync(request.TenantId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Tenant '{request.TenantId}' not found."));
        await tenantRepository.UpdateStatusAsync(request.TenantId, "deleted");
        await authentikAdminClient.DeactivateAllUsersInTenantAsync(request.TenantId);
        auditLog.AdminOperation(context.GetHttpContext().User, "DeleteTenant", request.TenantId);
        return new Empty();
    }

    private async Task<Tenant> SetStatusAsync(string tenantId, string status, string operation, ServerCallContext context)
    {
        var existing = await tenantRepository.GetAsync(tenantId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Tenant '{tenantId}' not found."));
        await tenantRepository.UpdateStatusAsync(tenantId, status);
        auditLog.AdminOperation(context.GetHttpContext().User, operation, tenantId);
        return ToProto(existing with { Status = status });
    }

    private static Tenant ToProto(TenantRow row) =>
        new() { TenantId = row.Id, DisplayName = row.DisplayName, Status = row.Status };
}
```

- [ ] **Step 3: Add `Grpc.AspNetCore.Web` to `Iverson.Api.csproj`**

```xml
<PackageReference Include="Grpc.AspNetCore.Web" Version="2.80.0" />
```

- [ ] **Step 4: Wire into `Program.cs`**

Add `app.UseGrpcWeb();` right after `app.UseAuthorization();` (~line 233). Add to the `workloadRole == "api"` block (~line 360):

```csharp
app.MapGrpcService<TenantLifecycleGrpcService>().RequireAuthorization("Operator").EnableGrpcWeb();
```

- [ ] **Step 5: Tests** — unit tests with `Substitute.For<ITenantRepository>()`/`Substitute.For<IAuthentikAdminClient>()` covering each RPC (including the `CreateTenant` Authentik-failure compensating-delete path and the invalid-`tenant_id` rejection), plus one `AuthTestWebApplicationFactory` pipeline test confirming a non-operator caller gets rejected by the `Operator` policy.

- [ ] **Step 6: Build and test**
```bash
cd Iverson.Server
dotnet build
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~TenantLifecycleGrpcService"
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Clients/Common/Proto/tenant_lifecycle.proto Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs Iverson.Server/Iverson.Api/Iverson.Api.csproj Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs
git commit -m "feat(api): add TenantLifecycleGrpcService for system-wide tenant lifecycle"
```

---

### Task 5: `TenantAdminGrpcService`

**Files:**
- Create: `Iverson.Clients/Common/Proto/tenant_admin.proto`
- Create: `Iverson.Server/Iverson.Api/TenantAdminAuthorizationPolicy.cs`
- Create: `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 2's `ITenantStatusCache`, Task 3's `IAuthentikAdminClient`, Task 4's grpc-web middleware setup.

- [ ] **Step 1: Create `tenant_admin.proto`**

```proto
syntax = "proto3";
option csharp_namespace = "Iverson.Client.Contracts";
package iverson;

import "google/protobuf/empty.proto";

service TenantAdminGrpcService {
    rpc InviteUser     (InviteUserRequest)     returns (TenantUser);
    rpc ListUsers      (ListUsersRequest)      returns (ListUsersResponse);
    rpc RemoveUser     (RemoveUserRequest)     returns (google.protobuf.Empty);
    rpc SetTenantAdmin (SetTenantAdminRequest) returns (TenantUser);
}

message InviteUserRequest {
    string username         = 1;
    string email            = 2;
    string initial_password = 3;
}

message ListUsersRequest {
}

message ListUsersResponse {
    repeated TenantUser users = 1;
}

message RemoveUserRequest {
    string user_id = 1;
}

message SetTenantAdminRequest {
    string user_id = 1;
    bool   grant   = 2;
}

message TenantUser {
    string user_id  = 1;
    string username = 2;
    string email    = 3;
}
```

- [ ] **Step 2: Create `TenantAdminAuthorizationPolicy.cs`** (mirrors `OperatorAuthorizationPolicy.cs`)

```csharp
namespace Iverson.Api;

public static class TenantAdminAuthorizationPolicy
{
    public static bool IsSatisfiedBy(IEnumerable<string> groupClaims) => groupClaims.Contains("tenant-admins");
}
```

- [ ] **Step 3: Create `TenantAdminGrpcService.cs`**

```csharp
using Grpc.Core;
using Iverson.Api.Tenancy;
using Iverson.Client.Contracts;

namespace Iverson.Api.Grpc;

public sealed class TenantAdminGrpcService(
    IAuthentikAdminClient authentikAdminClient,
    ITenantStatusCache tenantStatusCache,
    AuditLog auditLog) : Iverson.Client.Contracts.TenantAdminGrpcService.TenantAdminGrpcServiceBase
{
    public override async Task<TenantUser> InviteUser(InviteUserRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        await authentikAdminClient.CreateUserAsync(request.Username, request.Email, request.InitialPassword, tenantId, []);
        auditLog.AdminOperation(context.GetHttpContext().User, "InviteUser", request.Username);
        return new TenantUser { Username = request.Username, Email = request.Email };
    }

    public override async Task<ListUsersResponse> ListUsers(ListUsersRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        var users = await authentikAdminClient.ListUsersByTenantAsync(tenantId);
        var response = new ListUsersResponse();
        response.Users.AddRange(users.Select(u => new TenantUser { UserId = u.Id, Username = u.Username, Email = u.Email }));
        return response;
    }

    public override async Task<Empty> RemoveUser(RemoveUserRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        await RequireUserInTenantAsync(request.UserId, tenantId);
        await authentikAdminClient.DeactivateUserAsync(request.UserId);
        auditLog.AdminOperation(context.GetHttpContext().User, "RemoveUser", request.UserId);
        return new Empty();
    }

    public override async Task<TenantUser> SetTenantAdmin(SetTenantAdminRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        var user = await RequireUserInTenantAsync(request.UserId, tenantId);
        if (request.Grant)
            await authentikAdminClient.AddGroupAsync(request.UserId, "tenant-admins");
        else
            await authentikAdminClient.RemoveGroupAsync(request.UserId, "tenant-admins");
        auditLog.AdminOperation(context.GetHttpContext().User, "SetTenantAdmin", request.UserId);
        return new TenantUser { UserId = user.Id, Username = user.Username, Email = user.Email };
    }

    private async Task<string> RequireActiveTenantAsync(ServerCallContext context)
    {
        var tenantId = context.GetHttpContext().User.FindFirst("tenant_id")?.Value
            ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "No tenant_id claim."));
        var status = await tenantStatusCache.GetStatusAsync(tenantId);
        if (status is null or "suspended" or "deleted")
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"Tenant '{tenantId}' is not active."));
        return tenantId;
    }

    private async Task<AuthentikUser> RequireUserInTenantAsync(string userId, string tenantId)
    {
        var users = await authentikAdminClient.ListUsersByTenantAsync(tenantId);
        return users.FirstOrDefault(u => u.Id == userId)
            ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "User does not belong to your tenant."));
    }
}
```

- [ ] **Step 4: Wire into `Program.cs`**

Add to the `AddAuthorization` block, alongside the existing `Operator`/`SchemaAdmin` policies:

```csharp
options.AddPolicy("TenantAdmin", policy => policy.RequireAssertion(context =>
    TenantAdminAuthorizationPolicy.IsSatisfiedBy(
        context.User.FindAll("groups").Select(c => c.Value))));
```

Add to the `workloadRole == "api"` block:

```csharp
app.MapGrpcService<TenantAdminGrpcService>().RequireAuthorization("TenantAdmin").EnableGrpcWeb();
```

- [ ] **Step 5: Modify `AuditingAuthorizationMiddlewareResultHandler.cs`**

```csharp
private static readonly HashSet<string> AuditedPolicies = ["SchemaAdmin", "Operator", "TenantAdmin"];
```

- [ ] **Step 6: Tests** — unit tests with mocked `IAuthentikAdminClient`/`ITenantStatusCache` covering: happy path for all 4 RPCs; suspended-tenant rejection; cross-tenant `RemoveUser`/`SetTenantAdmin` rejection (target `user_id` not in `ListUsersByTenantAsync`'s result); plus an `AuthTestWebApplicationFactory` pipeline test confirming the `TenantAdmin` policy gates correctly (non-tenant-admin caller rejected, audited via Part C's handler).

- [ ] **Step 7: Build and test**
```bash
cd Iverson.Server
dotnet build
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~TenantAdminGrpcService"
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Clients/Common/Proto/tenant_admin.proto Iverson.Server/Iverson.Api/TenantAdminAuthorizationPolicy.cs Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs
git commit -m "feat(api): add TenantAdminGrpcService for delegated per-tenant admin"
```

---

### Task 6: Doc update

**Files:**
- Modify: `docs/user-management-and-security.md`

- [ ] **Step 1: Add a note after the "Creating a human user and granting operator access" section's existing content** (do not alter its steps 1-5):

```markdown
> **Note:** As of the Tenant Admin APIs (Part D), manually editing a user's
> `attributes.tenant_id` in the Authentik admin UI, outside `CreateTenant`/
> `InviteUser`, is no longer a supported way to provision a tenant user — any
> `tenant_id` introduced that way has no registry row and is permanently
> denied by the platform's fail-closed tenant-suspension check. The steps
> above (creating an operator) are unaffected and remain the sanctioned path
> for operator onboarding specifically.
```

- [ ] **Step 2: Commit**
```bash
git add docs/user-management-and-security.md
git commit -m "docs: note that ad-hoc tenant_id assignment is unsupported"
```

## Known issues inherited from spec

**Fail-closed on unregistered `tenant_id` values, explicitly chosen over fail-open.** Any `tenant_id` claim without a corresponding `IversonTenants` row is denied by the suspension check — including every tenant_id that predates this feature. This is why the 5 existing blueprint tenant_ids are backfilled in Task 1 (see "Backfill requirement" in the spec); any *future* ad-hoc tenant_id introduced outside the `CreateTenant` RPC (e.g., a new integration-test fixture) will also be denied until a corresponding registry row exists. This is accepted as the correct, stricter posture, matching Part A's hard-cutover precedent for legacy schemas.
