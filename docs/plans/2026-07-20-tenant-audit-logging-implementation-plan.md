# Tenant Audit Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-20-tenant-audit-logging-design.md` (commit SHA: `74a0dbfcfe09d566778679965f38722f24383fd4`)

**Goal:** Give every denied data-plane access attempt and every admin-level operation a structured, filterable log entry, closing the audit-trail gap identified in the tenant isolation initiative's Part C.

**Architecture:** A new `AuditLog` helper class (`Denied`/`AdminOperation` methods over a dedicated `ILogger<AuditLog>` category) is wired into 4 read-path masked-denial sites, 1 shared write-path function (covering 4 upstream callers), and 4 admin-op endpoints. A second component, `AuditingAuthorizationMiddlewareResultHandler`, wraps the default ASP.NET Core authorization result handler to catch rejections of the two admin-scoped policies (`SchemaAdmin`/`Operator`) at a single middleware choke point.

**Tech stack:** ASP.NET Core 10 minimal APIs + gRPC (`Grpc.AspNetCore`), `Microsoft.Extensions.Logging` structured logging, xUnit + NSubstitute + FluentAssertions for tests.

---

## Global Constraints

- `AuditLog.Denied`'s `action` parameter must be one of: `"Read"` / `"Create"` / `"Update"` / `"Delete"` / `"Unauthorized"`.
- `AuditLog.Denied`'s `reason` parameter must be one of: `"AccessDenied"` / `"TenantMismatch"` / `"TenantImmutable"` / `"OwnerMismatch"` / `"OwnerImmutable"` / `"PolicyRejected"`.
- Both `resourceType` and `resourceKey` passed into `AuditLog.Denied` must be sanitized via the existing `SanitizeForLog()` extension (`Iverson.Api/LoggingExtensions.cs`) before being interpolated into the log template — matching the established convention already used at every `TypeName`-logging call site in this codebase.
- `AuditLog` and `AuditingAuthorizationMiddlewareResultHandler` are both registered as DI singletons (`AddSingleton`), matching the existing convention for stateless helper services in `Program.cs` (e.g. `IRowFieldAuthorizationEvaluator`, `IEntityKeyAccessor`).
- New tests asserting on structured log content must follow the established pattern in `Iverson.Api.Tests/Grpc/OutboxPublisherTests.cs`: a shared `Substitute.For<ILogger<T>>()` field, asserted via `.Received(1).Log(LogLevel.X, Arg.Any<EventId>(), Arg.Is<object>(v => v.ToString()!.Contains(expectedSubstring)), Arg.Any<Exception>(), Arg.Any<Func<object, Exception?, string>>())`. Do not substitute `AuditLog` itself (it is a `sealed` class, not an interface — NSubstitute cannot proxy it); construct a real `AuditLog` around a substituted `ILogger<AuditLog>` instead, or `new AuditLog(NullLogger<AuditLog>.Instance)` where a test doesn't care about audit assertions.

## File Structure

- **Create:** `Iverson.Api/Grpc/AuditLog.cs` — the audit-logging helper (Task 1)
- **Create:** `Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs` — the authorization-rejection audit hook (Task 5)
- **Create:** `Iverson.Api.Tests/Grpc/AuditLogTests.cs` — unit tests for the helper (Task 1)
- **Create:** `Iverson.Api.Tests/Grpc/AuditingAuthorizationMiddlewareResultHandlerTests.cs` — integration tests for the hook (Task 5)
- **Modify:** `Iverson.Api/Program.cs` — DI registrations (Tasks 1, 5); `AuditLog`/`HttpContext` params on 3 admin endpoints (Task 4)
- **Modify:** `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` — constructor + `Get`/`GetMany` wiring (Task 2)
- **Modify:** `Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — constructor + `Get`/`Delete` wiring (Task 2); `Post`/`Update` call-site wiring (Task 3); `RegisterSchema` wiring (Task 4)
- **Modify:** `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` — constructor + `Post`/`Update` call-site wiring (Task 3)
- **Modify:** `Iverson.Api/Grpc/AuthorizationFieldMasking.cs` — `EnforceWriteAuthorization` signature + 5 throw sites (Task 3)
- **Modify:** `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs` — audit-logger field + new denial-audit tests (Task 2)
- **Modify:** `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — audit-logger field + new denial-audit tests (Tasks 2, 3, 4)
- **Modify:** `Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` — audit-logger field + new denial-audit tests (Task 3)
- **Modify:** `Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs` — pass a no-op `AuditLog` into its direct `ObjectMappingGrpcService` construction (Task 4)

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are NOT re-verified here:

- Read-path denials in `ObjectRetrievalGrpcService.Get`/`GetMany` and `ObjectMappingGrpcService.Get`/`Delete` are masked as `Found = false`/`Success = false`, never thrown (`ObjectRetrievalGrpcService.cs:23-117`, `ObjectMappingGrpcService.cs:185-216`).
- `AuthorizationFieldMasking.EnforceWriteAuthorization` has exactly 5 `PermissionDenied` throw sites, shared across Post/Update on both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService` (`AuthorizationFieldMasking.cs:38-82`).
- `AuthorizationDecision.Denied` is a general bucket (not "wrong role" specifically) — reason value is `AccessDenied`.
- `IActingUserAccessor.ActingUser` exposes `ClaimsPrincipal?` with `sub`/`tenant_id`/`groups` claims.
- Admin endpoints (`RegisterSchema`, `/admin/reconcile`, `/admin/dlq`, `/admin/dlq/{id}/replay`) use distinct named authorization policies (`SchemaAdmin`, `Operator`) separate from the `RequireAuthenticatedUser()` fallback policy.
- The `/admin/dlq`/`/admin/dlq/{id}/replay` minimal-API lambdas can accept added parameters without breaking existing tests (they're only exercised through the real HTTP pipeline).
- `IAuthorizationMiddlewareResultHandler.HandleAsync(RequestDelegate, HttpContext, AuthorizationPolicy, PolicyAuthorizationResult)` is the interface to implement; the built-in `AuthorizationMiddlewareResultHandler` (public, in `Microsoft.AspNetCore.Authorization.Policy`) is the default implementation to wrap.
- Policy names for endpoint-rejection filtering must be read from `context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>()`, not from the merged `AuthorizationPolicy` object (which doesn't retain the original name).
- `SanitizeForLog()` (`Iverson.Api/LoggingExtensions.cs:5`) is `internal static string SanitizeForLog(this string value)`, accessible anywhere in `Iverson.Api`.
- Postgres RLS masks cross-tenant reads/deletes as "not found" before the app-level tenant check runs on 4 specific call sites (`Get`/`GetMany`/`Get`/`Delete`) — accepted as a known limitation; these sites still correctly audit `AccessDenied`/`OwnerMismatch`, just not `TenantMismatch` for genuine cross-tenant attempts.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Api/Grpc/AuditLog.cs` does not already exist | `ls` returned "No such file or directory" |
| 2 | File path | `Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs` does not already exist | Same `ls` check |
| 3 | File path | `Iverson.Api.Tests/Grpc/AuditLogTests.cs` does not already exist | Same `ls` check |
| 4 | File path | `Iverson.Api.Tests/Grpc/AuditingAuthorizationMiddlewareResultHandlerTests.cs` does not already exist | Same `ls` check |
| 5 | Function signature | `ColumnDescriptor` (the type of `SchemaDescriptor.KeyColumn`) has a `Name` property of type `string` | Read `Iverson.Api/Schema/SchemaDescriptor.cs:28` — `public sealed record ColumnDescriptor(string Name, string SqlType, bool IsNullable)` |
| 6 | Code validity | `AuthorizationDecision`'s `Denied = true` construction always pairs with `OwnershipRequired = false` and `TenantColumn = null` — needed so the `decision.Denied ? "AccessDenied" : ownerMismatch ? "OwnerMismatch" : "TenantMismatch"` cascade never has to arbitrate between `Denied` and a mismatch simultaneously | Read `Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs:8-53` — all 5 early-return sites construct `new AuthorizationDecision(true, false, null, null, null, null, null)` |
| 7 | Sibling-set sweep | All 4 read-path masked-denial sites (`ObjectRetrievalGrpcService.Get:43-50`, `.GetMany:105-109`, `ObjectMappingGrpcService.Get:82-94`, `.Delete:204-216`) share the identical 3-part condition shape (`decision.Denied \|\| ownerMismatch \|\| tenantMismatch`), so the same restructuring code applies uniformly at all 4 | Direct read of each site this session |
| 8 | Consumer impact | `EnforceWriteAuthorization` has exactly 4 callers, all accounted for in the plan | `grep -rn "EnforceWriteAuthorization" --include="*.cs" .` returned exactly `ObjectMappingGrpcService.cs:112,159`, `ObjectPersistenceGrpcService.cs:34,85`, plus the declaration |
| 9 | Consumer impact | All direct constructors of `ObjectRetrievalGrpcService`/`ObjectMappingGrpcService`/`ObjectPersistenceGrpcService` across the whole test suite are accounted for | `grep -rln "new ObjectRetrievalGrpcService(\|new ObjectMappingGrpcService(\|new ObjectPersistenceGrpcService(" --include="*.cs" .` found 4 files — the 3 expected per-service test files, plus a 4th, previously-unaccounted-for direct construction in `RegisterSchemaAuthorizationIntegrationTests.cs:216-228` — added to Task 4's file list |
| 10 | Test command | `Iverson.Api.Tests.csproj` exists at the expected path | `find . -iname "Iverson.Api.Tests.csproj"` → `./Iverson.Api.Tests/Iverson.Api.Tests.csproj` |
| 11 | Task ordering | Tasks 2, 3, and 4 all touch `ObjectMappingGrpcService.cs`'s constructor (to add the same `AuditLog` dependency). Verified the plan assigns the constructor addition to Task 2 only (first task to touch that file); Tasks 3 and 4 consume the already-added field rather than re-adding it. Task 5 is independent of Tasks 2-4 (touches only its own new file + a disjoint `Program.cs` DI line) | Direct inspection of each task's file/line targets during drafting; see each task's "Interfaces: Consumes" note below |
| 12 | Code validity | `WebApplicationFactory<T>.WithWebHostBuilder(Action<IWebHostBuilder>)` exists in the referenced package version | `grep` of `Microsoft.AspNetCore.Mvc.Testing.xml` (v10.0.9, matching this project's own `bin/Debug/net10.0` output) confirms the member |
| 13 | Function signature | `TestJwtFactory.CreateToken(string audience, string subject, DateTime? expires = null, IEnumerable<Claim>? extraClaims = null)` | Read `Iverson.Api.Tests/Helpers/TestJwtFactory.cs:13-14` |
| 14 | Code validity | `OperatorAuthorizationPolicy.IsSatisfiedBy` returns `false` for a token with no `groups`/`scope` claims (needed for the "authenticated but wrong role → 403" test scenario) | Read `Iverson.Api/OperatorAuthorizationPolicy.cs:9-15` |
| 15 | Pattern reuse | The only existing precedent for asserting on structured log content in this codebase is `Substitute.For<ILogger<T>>()` + `.Received(1).Log(...)` | Read `Iverson.Api.Tests/Grpc/OutboxPublisherTests.cs:16,24-30` |
| 16 | Function signature | `ServerCallContext.GetHttpContext()` is a valid, already-used extension for obtaining the service credential from a gRPC method (needed for `RegisterSchema`'s `AdminOperation` call) | Already used at `Iverson.Api/Grpc/ActingUserInterceptor.cs:36` |
| 17 | Drift check | Spec's last-modifying commit equals current HEAD (no drift since spec-write time) | `git log -1 --format=%H -- docs/specs/2026-07-20-tenant-audit-logging-design.md` and `git log -1 --format=%H` both return `74a0dbf...` |
| 18 | Code validity | `WebApplicationFactory<T>.WithWebHostBuilder(...)`'s derived factory composes with (rather than replaces) the base factory's own `ConfigureWebHost` override — load-bearing for Task 5's test, since `AuthTestWebApplicationFactory`'s JWT signing-key setup must still apply for `TestJwtFactory`-issued tokens to validate | `Microsoft.AspNetCore.Mvc.Testing.xml`'s doc text for `WithWebHostBuilder`: creates a factory "with an `IWebHostBuilder` that is further customized" — confirming composition, not replacement |

## Tasks

### Task 1: `AuditLog` helper

**Files:**
- Create: `Iverson.Api/Grpc/AuditLog.cs`
- Create: `Iverson.Api.Tests/Grpc/AuditLogTests.cs`
- Modify: `Iverson.Api/Program.cs`

**Interfaces:**
- Produces: the `AuditLog` class (DI singleton) that every later task injects.

- [ ] **Step 1: Create `AuditLog`**
```csharp
using System.Security.Claims;

namespace Iverson.Api.Grpc;

public sealed class AuditLog(ILogger<AuditLog> logger)
{
    public void Denied(ClaimsPrincipal? actor, string action, string resourceType, string? resourceKey, string reason) =>
        logger.LogWarning(
            "[Audit.Denied] actor={Actor} tenant={Tenant} action={Action} resourceType={ResourceType} resourceKey={ResourceKey} reason={Reason}",
            actor?.FindFirst("sub")?.Value ?? "unknown",
            actor?.FindFirst("tenant_id")?.Value ?? "unknown",
            action, resourceType.SanitizeForLog(), resourceKey?.SanitizeForLog(), reason);

    public void AdminOperation(ClaimsPrincipal actor, string operation, string? detail) =>
        logger.LogInformation(
            "[Audit.AdminOperation] actor={Actor} operation={Operation} detail={Detail}",
            actor.FindFirst("sub")?.Value ?? "unknown", operation, detail?.SanitizeForLog());
}
```

- [ ] **Step 2: Register in DI**
In `Program.cs`, near the other stateless-singleton registrations (alongside `IRowFieldAuthorizationEvaluator`/`IEntityKeyAccessor`):
```csharp
builder.Services.AddSingleton<AuditLog>();
```

- [ ] **Step 3: Unit tests**
```csharp
using System.Security.Claims;
using Iverson.Api.Grpc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class AuditLogTests
{
    private readonly ILogger<AuditLog> _logger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _sut;

    public AuditLogTests() => _sut = new AuditLog(_logger);

    private void AssertLogged(LogLevel level, string expectedSubstring) =>
        _logger.Received(1).Log(
            level,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains(expectedSubstring)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public void Denied_WithActor_LogsWarningWithReason()
    {
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-1"), new Claim("tenant_id", "tenant-a")]));

        _sut.Denied(actor, "Read", "Article", "key-1", "TenantMismatch");

        AssertLogged(LogLevel.Warning, "TenantMismatch");
    }

    [Fact]
    public void Denied_NullActor_LogsWarningWithUnknownActor()
    {
        _sut.Denied(null, "Read", "Article", "key-1", "AccessDenied");

        AssertLogged(LogLevel.Warning, "unknown");
    }

    [Fact]
    public void AdminOperation_LogsInformationWithOperation()
    {
        var actor = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "svc-1")]));

        _sut.AdminOperation(actor, "RegisterSchema", "Article");

        AssertLogged(LogLevel.Information, "RegisterSchema");
    }
}
```

- [ ] **Step 4: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~AuditLogTests"
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/AuditLog.cs Iverson.Server/Iverson.Api.Tests/Grpc/AuditLogTests.cs Iverson.Server/Iverson.Api/Program.cs
git commit -m "feat(api): add AuditLog helper for tenant audit logging"
```

---

### Task 2: Read-path call sites

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's `AuditLog`.
- Produces: `_auditLog` field on `ObjectMappingGrpcService` — Tasks 3 and 4 add new call sites in this same class and must reuse this field, not re-add the constructor parameter.

- [ ] **Step 1: Wire `ObjectRetrievalGrpcService`**
Add `AuditLog auditLog` as a trailing constructor parameter:
```csharp
public sealed class ObjectRetrievalGrpcService(
    IEntityRepository _entities,
    SchemaRegistry registry,
    ILogger<ObjectRetrievalGrpcService> logger,
    IActingUserAccessor actingUserAccessor,
    IRowFieldAuthorizationEvaluator authEvaluator,
    AuditLog auditLog)
    : ObjectRetrievalService.ObjectRetrievalServiceBase
```

In `Get`, replace the combined-condition masked return (`:43-50`) with:
```csharp
var ownerMismatch  = decision.OwnershipRequired &&
    StructFieldAccess.GetFieldString(data, decision.OwnerFieldName!) != decision.OwnerValue;
var tenantMismatch = decision.TenantColumn is not null &&
    StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue;
if (decision.Denied || ownerMismatch || tenantMismatch)
{
    auditLog.Denied(actingUserAccessor.ActingUser, "Read", request.TypeName, request.Key,
        decision.Denied ? "AccessDenied" : ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
    return new RetrievalResponse { Found = false, TraceId = request.TraceId };
}
```

In `GetMany`, the `decision.Denied` early-return (`:83-88`) becomes:
```csharp
if (decision.Denied)
{
    auditLog.Denied(actingUserAccessor.ActingUser, "Read", request.TypeName, null, "AccessDenied");
    foreach (var _ in keys)
        await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
    return;
}
```
and the per-row check (`:105-109`) becomes:
```csharp
var ownerMismatch  = decision.OwnershipRequired &&
    StructFieldAccess.GetFieldString(data, decision.OwnerFieldName!) != decision.OwnerValue;
var tenantMismatch = decision.TenantColumn is not null &&
    StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue;
if (ownerMismatch || tenantMismatch)
{
    auditLog.Denied(actingUserAccessor.ActingUser, "Read", request.TypeName, key,
        ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
    await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
    continue;
}
```

- [ ] **Step 2: Wire `ObjectMappingGrpcService`**
Add `AuditLog _auditLog` as a trailing primary-constructor parameter (matching this file's underscore-prefixed convention):
```csharp
public sealed class ObjectMappingGrpcService(
    IEntityRepository _entities,
    IRecordStoreTransactionRunner _txRunner,
    IOutboxPublisher _outboxPublisher,
    SchemaRegistry _registry,
    IRelationValidator _relationValidator,
    IEntityKeyAccessor _keyAccessor,
    IOutboxWriter _outboxWriter,
    ILogger<ObjectMappingGrpcService> _logger,
    IActingUserAccessor _actingUserAccessor,
    IRowFieldAuthorizationEvaluator _authEvaluator,
    IEntityRelationResolver _relationResolver,
    ISchemaRegistrationOrchestrator _schemaRegistration,
    AuditLog _auditLog)
    : ObjectMappingService.ObjectMappingServiceBase
```

In `Get`, replace `:82-94` with (note the existing local is named `entityStruct`, not `data`):
```csharp
var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
var ownerMismatch  = decision.OwnershipRequired &&
    StructFieldAccess.GetFieldString(entityStruct, decision.OwnerFieldName!) != decision.OwnerValue;
var tenantMismatch = decision.TenantColumn is not null &&
    StructFieldAccess.GetFieldString(entityStruct, decision.TenantColumn) != decision.TenantValue;
if (decision.Denied || ownerMismatch || tenantMismatch)
{
    _auditLog.Denied(_actingUserAccessor.ActingUser, "Read", request.TypeName, request.Key,
        decision.Denied ? "AccessDenied" : ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
    return new MappingResponse
    {
        Success = false,
        Error   = $"'{request.TypeName}:{request.Key}' not found.",
        TraceId = request.TraceId
    };
}
```

In `Delete`, replace `:203-216` with (note this method has no named local for the parsed row today — it parses `rowJson` inline, twice; introduce `rowStruct` so it can be reused):
```csharp
var decision  = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Delete);
var rowStruct = JsonParser.Default.Parse<Struct>(rowJson);
var ownerMismatch  = decision.OwnershipRequired &&
    StructFieldAccess.GetFieldString(rowStruct, decision.OwnerFieldName!) != decision.OwnerValue;
var tenantMismatch = decision.TenantColumn is not null &&
    StructFieldAccess.GetFieldString(rowStruct, decision.TenantColumn) != decision.TenantValue;
if (decision.Denied || ownerMismatch || tenantMismatch)
{
    _auditLog.Denied(_actingUserAccessor.ActingUser, "Delete", request.TypeName, request.Key,
        decision.Denied ? "AccessDenied" : ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
    return new MappingDeleteResponse
    {
        Success = false,
        Error   = $"'{request.TypeName}:{request.Key}' not found.",
        TraceId = request.TraceId
    };
}
```

- [ ] **Step 3: Update existing tests' constructors**
In both `ObjectRetrievalGrpcServiceTests.cs` and `ObjectMappingGrpcServiceTests.cs`, add:
```csharp
private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
private readonly AuditLog _auditLog;
```
constructed as `_auditLog = new AuditLog(_auditLogger);` in the constructor body, and pass `_auditLog` as the new trailing argument to `_sut`'s construction.

- [ ] **Step 4: Add denial-audit tests**
Add one `[Fact]` per reachable `reason` value at each of the 4 call sites (e.g. `Get_TenantMismatch_LogsAuditDeniedWithTenantMismatch`, `Get_OwnerMismatch_LogsAuditDeniedWithOwnerMismatch`, `GetMany_AccessDenied_LogsAuditDeniedOnce`, etc.), asserting via the `OutboxPublisherTests`-style `_auditLogger.Received(1).Log(LogLevel.Warning, ..., Arg.Is<object>(v => v.ToString()!.Contains("<reason>")), ...)` pattern, reusing each test file's existing denial-scenario setup (schema/fixture construction already present for the existing "returns not found on mismatch" tests).

- [ ] **Step 5: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~ObjectRetrievalGrpcServiceTests|FullyQualifiedName~ObjectMappingGrpcServiceTests"
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectRetrievalGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "feat(api): audit-log denied read-path access attempts"
```

---

### Task 3: Write-path (`EnforceWriteAuthorization`)

**Files:**
- Modify: `Iverson.Api/Grpc/AuthorizationFieldMasking.cs`
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 1's `AuditLog`; Task 2's `_auditLog` field on `ObjectMappingGrpcService` (do not re-add the constructor parameter there — only add the two new call-site arguments).
- Produces: `AuditLog auditLog` field on `ObjectPersistenceGrpcService` (first task to touch that file's constructor).

- [ ] **Step 1: Add `auditLog` to `EnforceWriteAuthorization` and audit each throw site**
```csharp
public static void EnforceWriteAuthorization(
    IRowFieldAuthorizationEvaluator authEvaluator,
    ClaimsPrincipal? actingUser,
    SchemaDescriptor schema,
    Struct payload,
    AuthorizationAction action,
    string deniedMessage,
    string? existingRowJson,
    AuditLog auditLog)
{
    var auditAction = existingRowJson is null ? "Create" : "Update";
    var resourceKey = StructFieldAccess.GetFieldString(payload, schema.KeyColumn.Name);

    var decision = authEvaluator.Evaluate(schema, actingUser, action);
    if (decision.Denied)
    {
        auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "AccessDenied");
        throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
    }

    if (existingRowJson is null)
    {
        if (decision.TenantColumn is not null)
            payload.Fields[decision.TenantColumn] = Value.ForString(decision.TenantValue!);
        if (decision.OwnershipRequired)
            payload.Fields[decision.OwnerFieldName!] = Value.ForString(decision.OwnerValue!);
    }
    else
    {
        var existingStruct = JsonParser.Default.Parse<Struct>(existingRowJson);

        if (decision.TenantColumn is not null)
        {
            if (StructFieldAccess.GetFieldString(existingStruct, decision.TenantColumn) != decision.TenantValue)
            {
                auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "TenantMismatch");
                throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
            }
            var attemptedTenant = StructFieldAccess.GetFieldString(payload, decision.TenantColumn);
            if (attemptedTenant is not null && attemptedTenant != decision.TenantValue)
            {
                auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "TenantImmutable");
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant field is immutable."));
            }
        }

        if (decision.OwnershipRequired &&
            StructFieldAccess.GetFieldString(existingStruct, decision.OwnerFieldName!) != decision.OwnerValue)
        {
            auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "OwnerMismatch");
            throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
        }

        var ownerFieldName = schema.Authorization?.OwnerField;
        if (!string.IsNullOrEmpty(ownerFieldName))
        {
            var attemptedOwnerValue = StructFieldAccess.GetFieldString(payload, ownerFieldName);
            if (attemptedOwnerValue is not null &&
                attemptedOwnerValue != StructFieldAccess.GetFieldString(existingStruct, ownerFieldName))
            {
                auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "OwnerImmutable");
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Owner field is immutable after creation."));
            }
        }
    }

    RejectDisallowedFields(payload, decision.AllowedFields, exemptField: decision.OwnerFieldName);
}
```

- [ ] **Step 2: Thread `_auditLog`/`auditLog` through the 4 callers**
`ObjectMappingGrpcService.cs:112` (Post) and `:159` (Update): add `, _auditLog` as the trailing argument (reusing Task 2's field — do not add a new constructor parameter here).

`ObjectPersistenceGrpcService.cs`: add `AuditLog auditLog` as a trailing constructor parameter (this class's constructor has not been touched by any earlier task):
```csharp
public sealed class ObjectPersistenceGrpcService(
    IOutboxPublisher outboxPublisher,
    SchemaRegistry registry,
    IRelationValidator relationValidator,
    IEntityKeyAccessor keyAccessor,
    IOutboxWriter outboxWriter,
    ILogger<ObjectPersistenceGrpcService> logger,
    IEntityRepository entities,
    IActingUserAccessor actingUserAccessor,
    IRowFieldAuthorizationEvaluator authEvaluator,
    AuditLog auditLog)
    : ObjectPersistenceService.ObjectPersistenceServiceBase
```
then add `, auditLog` as the trailing argument at `:34` (Post) and `:85` (Update).

- [ ] **Step 3: Update existing tests' constructors**
In `ObjectMappingGrpcServiceTests.cs`, reuse the `_auditLog`/`_auditLogger` fields Task 2 already added — no new field needed, just pass `_auditLog` unchanged (already wired at Task 2). In `ObjectPersistenceGrpcServiceTests.cs`, add the same `_auditLogger`/`_auditLog` field pair Task 2 added to the other two test files (this is this file's first exposure to `AuditLog`), and pass `_auditLog` as `ObjectPersistenceGrpcService`'s new trailing constructor argument.

- [ ] **Step 4: Add denial-audit tests**
Add `[Fact]`s covering each of the 5 reasons (`AccessDenied`, `TenantMismatch`, `TenantImmutable`, `OwnerMismatch`, `OwnerImmutable`) reachable through `Post`/`Update` on both `ObjectMappingGrpcServiceTests.cs` and `ObjectPersistenceGrpcServiceTests.cs`, reusing each file's existing denial-scenario fixtures, asserted the same way as Task 2's Step 4.

- [ ] **Step 5: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~ObjectPersistenceGrpcServiceTests"
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs
git commit -m "feat(api): audit-log denied write-path access attempts"
```

---

### Task 4: Admin operations

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Api/Program.cs`
- Modify: `Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1's `AuditLog`; Task 2's `_auditLog` field on `ObjectMappingGrpcService`.

- [ ] **Step 1: Wire `RegisterSchema`**
```csharp
var registered = await _schemaRegistration.RegisterAsync(request, context.CancellationToken);

_auditLog.AdminOperation(context.GetHttpContext().User, "RegisterSchema", request.RootType.TypeName);

return new SchemaResponse
{
    Success    = true,
    TraceId    = request.TraceId,
    Registered = { registered }
};
```

- [ ] **Step 2: Wire the 3 `/admin/*` endpoints in `Program.cs`**
```csharp
app.MapPost("/admin/reconcile/{typeName}", async (
    string typeName,
    Iverson.Api.Reconciliation.ReconciliationService reconciliation,
    AuditLog audit,
    HttpContext httpContext) =>
{
    var count = await reconciliation.ReconcileTypeAsync(typeName);
    if (count is null)
        return Results.NotFound(new { error = $"No schema registered for '{typeName}'" });

    audit.AdminOperation(httpContext.User, "Reconcile", typeName);
    return Results.Ok(new { reconciledCount = count, typeName });
}).WithName("Reconcile").RequireAuthorization("Operator");

app.MapGet("/admin/dlq", async (IDlqRepository dlq, AuditLog audit, HttpContext httpContext) =>
{
    var rows = await dlq.ListUnreplayedAsync(200);
    audit.AdminOperation(httpContext.User, "ListDlq", null);
    return Results.Ok(rows);
}).WithName("ListDlq").RequireAuthorization("Operator");

app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, IEventProducer events, AuditLog audit, HttpContext httpContext) =>
{
    var row = await dlq.GetUnreplayedByIdAsync(id);
    if (row is null) return Results.NotFound(new { error = $"No unreplayed DLQ row with id '{id}'" });

    await events.ProduceAsync(row.SourceTopic, row.MessageKey, row.MessageValue);
    await dlq.MarkReplayedAsync(id);
    audit.AdminOperation(httpContext.User, "ReplayDlq", id.ToString());

    return Results.Ok(new { replayed = true, id, topic = row.SourceTopic });
}).WithName("ReplayDlq").RequireAuthorization("Operator");
```
(`AdminOperation` is only called on the success path in `Reconcile`/`ReplayDlq` — matching `AuditLog.AdminOperation`'s "one entry per **successful** admin action" contract; `ListDlq` has no failure branch, so it logs unconditionally.)

- [ ] **Step 3: Fix `RegisterSchemaAuthorizationIntegrationTests.cs`'s direct construction**
At `:216-228`, add a no-op `AuditLog` as the new trailing constructor argument:
```csharp
new AuditLog(NullLogger<AuditLog>.Instance)
```

- [ ] **Step 4: Add admin-operation audit tests**
Extend `ObjectMappingGrpcServiceTests.cs` with a `RegisterSchema_Succeeds_LogsAdminOperation` test (reusing Task 2's `_auditLogger` field, asserting `LogLevel.Information` with `"RegisterSchema"`). `/admin/*` endpoint coverage for the success path is deferred to Task 5's integration test file, which already boots the real pipeline — no separate unit-level coverage is needed for 3 one-line minimal-API lambdas whose only new logic is the `AdminOperation` call itself.

- [ ] **Step 5: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~RegisterSchemaAuthorizationIntegrationTests"
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "feat(api): audit-log successful admin operations"
```

---

### Task 5: Authorization-rejection audit hook

**Files:**
- Create: `Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs`
- Create: `Iverson.Api.Tests/Grpc/AuditingAuthorizationMiddlewareResultHandlerTests.cs`
- Modify: `Iverson.Api/Program.cs`

**Interfaces:**
- Consumes: Task 1's `AuditLog`.

- [ ] **Step 1: Create `AuditingAuthorizationMiddlewareResultHandler`**
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Iverson.Api.Grpc;

public sealed class AuditingAuthorizationMiddlewareResultHandler(AuditLog auditLog) : IAuthorizationMiddlewareResultHandler
{
    private static readonly HashSet<string> AuditedPolicies = ["SchemaAdmin", "Operator"];
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded)
        {
            var policyNames = context.GetEndpoint()?.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .Where(p => p is not null) ?? [];
            if (policyNames.Any(p => AuditedPolicies.Contains(p!)))
                auditLog.Denied(context.User, "Unauthorized", context.GetEndpoint()?.DisplayName ?? "unknown", null, "PolicyRejected");
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
```

- [ ] **Step 2: Register in DI**
In `Program.cs`, after the `AddAuthorization(...)` block:
```csharp
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationMiddlewareResultHandler>();
```

- [ ] **Step 3: Integration tests**
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Api.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// Boots the real Program.cs pipeline (the same base fixture AuthenticationPipelineTests and
// RegisterSchemaAuthorizationPipelineTests use), layering a per-test ILogger<AuditLog> substitute
// via WithWebHostBuilder so the audit signal from AuditingAuthorizationMiddlewareResultHandler
// can be asserted alongside the real HTTP status code.
public class AuditingAuthorizationMiddlewareResultHandlerTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;

    public AuditingAuthorizationMiddlewareResultHandlerTests(AuthTestWebApplicationFactory factory) =>
        _baseFactory = factory;

    private (HttpClient client, ILogger<AuditLog> loggerSpy) CreateClientWithLoggerSpy()
    {
        var loggerSpy = Substitute.For<ILogger<AuditLog>>();
        var factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILogger<AuditLog>>();
                services.AddSingleton(loggerSpy);
            }));
        return (factory.CreateClient(), loggerSpy);
    }

    private static void AssertWarningLogged(ILogger<AuditLog> loggerSpy, string expectedSubstring) =>
        loggerSpy.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains(expectedSubstring)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public async Task AuthenticatedWrongRole_AdminDlq_Returns403AndLogsAuditEntry()
    {
        var (client, loggerSpy) = CreateClientWithLoggerSpy();
        var token = TestJwtFactory.CreateToken("test-service-audience", "ak-some-other-service");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/admin/dlq");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        AssertWarningLogged(loggerSpy, "PolicyRejected");
    }

    [Fact]
    public async Task NoToken_AdminDlq_Returns401AndDoesNotLogAuditEntry()
    {
        var (client, loggerSpy) = CreateClientWithLoggerSpy();

        var response = await client.GetAsync("/admin/dlq");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        loggerSpy.ReceivedCalls().Should().BeEmpty();
    }
}
```

- [ ] **Step 4: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~AuditingAuthorizationMiddlewareResultHandlerTests"
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs Iverson.Server/Iverson.Api.Tests/Grpc/AuditingAuthorizationMiddlewareResultHandlerTests.cs Iverson.Server/Iverson.Api/Program.cs
git commit -m "feat(api): audit-log rejected admin-policy authorization attempts"
```

## Known issues inherited from spec

The bare `RequireAuthenticatedUser()` fallback policy is deliberately excluded from `AuditedPolicies` — a caller with a missing/invalid token hitting an ordinary data-plane endpoint is not audit-logged by this plan. Postgres RLS also masks cross-tenant reads/deletes at 4 call sites as plain "not found" before the app-level `TenantMismatch` check runs, so those specific attempts won't produce a `TenantMismatch` entry (they still correctly produce `AccessDenied`/`OwnerMismatch` where applicable). Both are accepted, documented limitations from the source spec, not gaps introduced by this plan.
