# Tenant Admin APIs — Design Spec

**Status:** Draft, pending user review
**Date:** 2026-07-20
**Part of:** Tenant Data Isolation & Compliance Readiness initiative (Part D of 4 — see `docs/specs/2026-07-17-mandatory-tenant-boundary-design.md`)

## Motivation

Parts A-C of this initiative built and made observable a platform-enforced tenant boundary. But provisioning a tenant in the first place is still entirely manual: an operator logs into Authentik's admin UI, hand-creates a user, and hand-sets its `attributes.tenant_id` (per `docs/user-management-and-security.md`'s "Creating a human user" procedure) — the same manual path documented there for the `operators` group. There is no API for creating a tenant, listing tenants, suspending one, or letting a tenant manage its own users. Part D closes this gap with two admin surfaces: one operator-facing (system-wide tenant lifecycle) and one delegated to each tenant's own admin (managing users within that tenant only).

## Scope and decomposition

This is Part D of the four-part initiative described in `docs/specs/2026-07-17-mandatory-tenant-boundary-design.md`:

- Part A — Mandatory tenant boundary (complete).
- Part B — Defense-in-depth: Postgres RLS, Qdrant tenant-scoped collections, StarRocks per-tenant databases (complete).
- Part C — Audit logging of denied data-plane access and admin-level operations (complete).
- **Part D (this spec)** — The two admin APIs: system-wide tenant lifecycle + delegated per-tenant admin.

Explicitly out of scope for this spec:
- Cascading data deletion across Postgres/StarRocks/Qdrant when a tenant is deleted. `DeleteTenant` deprovisions identity only; removing a deleted tenant's actual data from the three stores is a separate, future concern.
- Email/invitation-based user onboarding. No SMTP infrastructure exists in this deployment; new users get an initial password set directly by the caller.
- A web UI. This spec defines the gRPC service contract (plus grpc-web wiring so a browser *can* call it); building the actual admin frontend is separate work.

## Design

### Data model: `tenants` registry

Nothing in the codebase today tracks "the list of tenants" — `tenant_id` is a free-form string stamped on users (via an Authentik user attribute) and on rows (via each schema's mandatory `tenant_field`), and StarRocks/Postgres/Qdrant all provision tenant-scoped resources lazily, on first write, with no central registry. This spec adds one: a new `IversonTenants` table (mirroring `IversonDlqMessages`'s exact shape — a flat `TableSchema` applied via `PostgresSchemaManager.ApplySchemaAsync` at startup, alongside the existing `DlqSchema.Table` and `ReconciliationSchema.Table` calls — not the JSONB-registry pattern `_iverson_schema` uses, since this table needs no upsert-with-merge semantics):

```csharp
internal static class TenantSchema
{
    public const string TableName = "IversonTenants";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("Id", "text", false),
        new List<ColumnSchema>
        {
            new("DisplayName", "text", false),
            new("Status",      "text", false),   // "active" | "suspended" | "deleted"
            new("CreatedAt",   "timestamptz", false),
        });
}
```

A new `ITenantRepository`/`TenantRepository` pair, in `Iverson.Sql`, mirrors `IDlqRepository`/`DlqRepository` exactly: constructed with `(string tableName, IRecordStoreQueryExecutor sql)`, plain (non-tenant-scoped) queries — `InsertAsync`, `GetAsync(id)`, `ListAsync()`, `UpdateStatusAsync(id, status)`. Queries never pass `tenantScoped: true` to `IRecordStoreQueryExecutor`, so this table is never subject to the Postgres RLS policies Part B1 established (those apply only to tables that opt in, exactly like `IversonDlqMessages` already doesn't).

`tenant_id`/`Id` values are validated against the exact format `TenantIdentifier.IsValid` already enforces in `Iverson.StarRocks` (regex `^(?!.*--)([A-Za-z0-9_-]{1,52})$`) — that class changes from `internal` to `public` so `Iverson.Api` (which already has a `ProjectReference` to `Iverson.StarRocks`) can call it directly, rather than duplicating the regex.

### Authentik orchestration client

A new `IAuthentikAdminClient`, in `Iverson.Api`, wraps Authentik's REST management API (`/api/v3/core/users/`, `/api/v3/core/groups/`) — the only thing in the codebase that talks to that API. It exposes exactly the operations the two new gRPC services need:

- `CreateUserAsync(username, email, password, tenantId, groups)` — creates an Authentik user with the given `attributes.tenant_id` and group memberships.
- `ListUsersByTenantAsync(tenantId)` — lists users whose `tenant_id` attribute matches.
- `DeactivateUserAsync(userId)` / `DeactivateAllUsersInTenantAsync(tenantId)`.
- `AddGroupAsync(userId, groupName)` / `RemoveGroupAsync(userId, groupName)` — used for `SetTenantAdmin`'s promote/demote.

Authenticated via a static bearer token (an Authentik `authentik_core.token` object), added to both blueprint files (`compose-only/service-clients.yaml` and the kind `blueprints-configmap-service-clients.yaml` template) bound to a new, dedicated non-superuser automation user — provisioned the same way existing service-client secrets already are (compose: hardcoded dev value; kind: `randAlphaNum` + lookup-guarded Kubernetes Secret). **The exact blueprint YAML shape for `authentik_core.token` is not yet verified against Authentik's own schema/docs** (no existing blueprint in this repo uses that model) — flagged for verification during implementation planning, not assumed here.

### gRPC service 1: `TenantLifecycleGrpcService` (operator-facing)

Gated by the existing `Operator` policy (`groups` contains `operators`, or `scope` contains `admin`) — the same policy already governing `/admin/reconcile`, `/admin/dlq`, `/admin/dlq/{id}/replay`.

```proto
service TenantLifecycleGrpcService {
  rpc CreateTenant(CreateTenantRequest) returns (Tenant);
  rpc ListTenants(ListTenantsRequest) returns (ListTenantsResponse);
  rpc SuspendTenant(SuspendTenantRequest) returns (Tenant);
  rpc ReactivateTenant(ReactivateTenantRequest) returns (Tenant);
  rpc DeleteTenant(DeleteTenantRequest) returns (google.protobuf.Empty);
}

message CreateTenantRequest {
  string tenant_id = 1;              // validated via TenantIdentifier.IsValid
  string display_name = 2;
  string admin_username = 3;
  string admin_email = 4;
  string admin_initial_password = 5;
}
```

- **`CreateTenant`**: validates `tenant_id`, inserts the `IversonTenants` row (`status='active'`), then calls `IAuthentikAdminClient.CreateUserAsync` with the `tenant-admins` group and the given `tenant_id` attribute. Not a true transaction (Authentik isn't transactional with Postgres) — if the Authentik call fails after the row insert succeeds, the row is deleted as a compensating action and the RPC returns an error.
- **`SuspendTenant`/`ReactivateTenant`**: flip `Status` between `'active'`/`'suspended'`.
- **`DeleteTenant`**: sets `Status='deleted'` (never a hard row delete — preserves the audit trail and permanently reserves the id so it can't be recreated and collide with old data) and calls `DeactivateAllUsersInTenantAsync`. No cascading data deletion (see "Out of scope").

### gRPC service 2: `TenantAdminGrpcService` (delegated per-tenant admin)

Gated by a new `TenantAdmin` policy — `groups` contains `tenant-admins` (mirrors `OperatorAuthorizationPolicy.IsSatisfiedBy`'s shape, group-check only, no scope-based fallback). Every RPC additionally scopes to the **caller's own** `tenant_id` claim, read from the **primary** authenticated principal (`context.GetHttpContext().User` — the same source `RegisterSchema` and the 3 `/admin/*` endpoints already use for `AuditLog`, and the same source the `Operator`/`TenantAdmin` policies read `groups` from), **not** `IActingUserAccessor.ActingUser` — that accessor is only populated when an acting-user-impersonation header is present (Part 4's service-acting-on-behalf-of-an-end-user pattern), which a tenant-admin calling directly via grpc-web with their own JWT has no reason to ever send. `AuditLog.AdminOperation`'s `actor` parameter for this service's RPCs is sourced the same way. No request message carries a caller-suppliable `tenant_id` field, so a tenant-admin has no way to even name a different tenant.

```proto
service TenantAdminGrpcService {
  rpc InviteUser(InviteUserRequest) returns (TenantUser);
  rpc ListUsers(ListUsersRequest) returns (ListUsersResponse);
  rpc RemoveUser(RemoveUserRequest) returns (google.protobuf.Empty);
  rpc SetTenantAdmin(SetTenantAdminRequest) returns (TenantUser);
}
```

`InviteUser` creates a user stamped with the caller's own `tenant_id` (never a request-supplied one) and an initial password supplied by the caller (no SMTP). `ListUsers` returns `IAuthentikAdminClient.ListUsersByTenantAsync(callerTenantId)`'s result set directly. `RemoveUser` and `SetTenantAdmin` each take a request-supplied `user_id` — before acting, both must verify that user's `tenant_id` attribute matches the caller's own tenant (e.g. checking membership in that same `ListUsersByTenantAsync(callerTenantId)` result, or an equivalent single-user lookup) and reject with `RpcException(PermissionDenied)` on a mismatch. Without this check, a request-supplied `user_id` — unlike `tenant_id`, never withheld from the caller — could name a user in a different tenant.

### Suspension enforcement

Every `tenant_id` read for authorization purposes in the entire codebase (`RowFieldAuthorizationEvaluator`, `EntityRelationResolver`, `ObjectRetrievalGrpcService`, `ObjectMappingGrpcService`, `AuditLog` — confirmed by a repo-wide grep) goes through `IActingUserAccessor.ActingUser`, which is populated in exactly one place: `ActingUserInterceptor.ValidateActingUserAsync`. That one method — right after `result.Principal` is validated — gains a tenant-status check: a new `ITenantStatusCache` (`IMemoryCache`-backed, ~30s TTL; first use of `IMemoryCache` in this codebase, a standard built-in service requiring `builder.Services.AddMemoryCache()`) wraps `ITenantRepository.GetAsync`. A `suspended` or `deleted` status throws `RpcException(PermissionDenied)`. This single insertion point covers every downstream tenant check with no per-call-site changes.

**Backfill requirement.** The registry starts empty, but 5 `tenant_id` values already exist in both blueprint files today (`tenant-loadtest`, `tenant-webtest`, `tenant-admin`, `tenant-smoke-test`, `tenant-bypass`) and were never created through `CreateTenant`. Since an unregistered `tenant_id` is denied (fail-closed, per explicit decision — see "Known limitation"), the same startup code that calls `ApplySchemaAsync(TenantSchema.Table)` must also seed these 5 rows as `Status='active'` (`INSERT ... ON CONFLICT (Id) DO NOTHING`), or every existing dev/test/loadtest flow breaks on first deploy.

**Manual tenant-user creation is removed.** `docs/user-management-and-security.md`'s "Creating a human user and granting operator access" procedure currently documents creating a human user (steps 1-2) as a precursor to granting operator access (steps 3-5). Steps 1-2 are removed from that doc as part of this work — `CreateTenant`/`InviteUser` become the only way to create a new tenant user going forward, since any user created outside them has no `IversonTenants` registry row and is permanently denied by the fail-closed suspension check. Steps 3-5 (granting operator access to an existing user) are unaffected and remain a manual step, since Part D provides no alternative for operator provisioning.

### Audit logging integration

Every RPC in both new services calls `AuditLog.AdminOperation` on success, matching Part C's existing convention for `/admin/*` and `RegisterSchema`. Rejections are covered by Part C's existing `AuditingAuthorizationMiddlewareResultHandler` — gRPC's `RequireAuthorization(...)` runs through the same ASP.NET Core authorization middleware pipeline as the HTTP `/admin/*` endpoints (confirmed: `app.UseAuthorization()` applies uniformly, and neither existing gRPC service chains its own per-method authorization — service-level `.RequireAuthorization("Operator")`/`.RequireAuthorization("TenantAdmin")` on `MapGrpcService<T>()` is the mechanism this spec uses), so the handler's rejection-audit logic already applies automatically. The only change needed is adding `"TenantAdmin"` to its `AuditedPolicies` hash-set literal, alongside the existing `"SchemaAdmin"`/`"Operator"`.

### grpc-web

`Grpc.AspNetCore.Web` (not currently referenced; `Grpc.AspNetCore` 2.80.0 is) is added as a package reference. `app.UseGrpcWeb()` joins the middleware pipeline; both new services' `MapGrpcService<T>()` calls chain `.EnableGrpcWeb()`, so a browser-based admin UI can call them directly without a separate proxy.

## Known limitation

**Fail-closed on unregistered `tenant_id` values, explicitly chosen over fail-open.** Any `tenant_id` claim without a corresponding `IversonTenants` row is denied by the suspension check — including every tenant_id that predates this feature. This is why the 5 existing blueprint tenant_ids must be backfilled as part of this work (see "Backfill requirement" above); any *future* ad-hoc tenant_id introduced outside the `CreateTenant` RPC (e.g., a new integration-test fixture) will also be denied until a corresponding registry row exists. This is accepted as the correct, stricter posture, matching Part A's hard-cutover precedent for legacy schemas.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `TenantIdentifier.IsValid` exists in `Iverson.StarRocks` but is `internal`, with `InternalsVisibleTo` only granted to `Iverson.StarRocks.Tests` | `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs:5-9`; `Iverson.StarRocks.csproj:8-10` |
| 2 | `Iverson.Api` already has a `ProjectReference` to `Iverson.StarRocks` | `Iverson.Api.csproj:33` |
| 3 | `IDlqRepository`/`DlqRepository` (Iverson.Sql) is the closest existing analog for a new flat-CRUD registry table | `Iverson.Server/Iverson.Sql/DlqRepository.cs:1-59` |
| 4 | `DlqSchema.Table` is applied via `PostgresSchemaManager.ApplySchemaAsync` at startup, not raw SQL | `Iverson.Server/Iverson.Api/Reconciliation/DlqSchema.cs:1-30`; `Program.cs:350` |
| 5 | `_iverson_schema`'s raw-SQL/JSONB-upsert pattern is a *different*, less-applicable precedent (used for the schema registry specifically) | `Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs:1-30` |
| 6 | `AuditLog.AdminOperation(ClaimsPrincipal actor, string operation, string? detail)` signature | `Iverson.Server/Iverson.Api/Grpc/AuditLog.cs` (Part C) |
| 7 | `OperatorAuthorizationPolicy.IsSatisfiedBy(IEnumerable<string> groupClaims, string? scopeClaim)` is the pattern to mirror for a new `TenantAdminAuthorizationPolicy` | `Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs` |
| 8 | `AuditingAuthorizationMiddlewareResultHandler.AuditedPolicies` is a simple `HashSet<string>` literal, trivially extensible | `Iverson.Server/Iverson.Api/Grpc/AuditingAuthorizationMiddlewareResultHandler.cs` (Part C) |
| 9 | `app.UseAuthorization()` applies uniformly to HTTP and gRPC endpoints; none of the 4 existing gRPC services chain per-method `.RequireAuthorization(...)` today (they rely on the fallback policy) | `Program.cs:232-233, 360-363` |
| 10 | `Grpc.AspNetCore.Web` is not currently referenced; `Grpc.AspNetCore` 2.80.0 is | `Iverson.Api.csproj:19` |
| 11 | `IMemoryCache`/`AddMemoryCache()` is not currently registered anywhere in `Program.cs` | grep of `Program.cs` |
| 12 | Every `tenant_id` claim read for authorization purposes in the codebase goes through `IActingUserAccessor.ActingUser`, populated only by `ActingUserInterceptor.ValidateActingUserAsync` | repo-wide grep for `FindFirst("tenant_id")`: `ObjectRetrievalGrpcService.cs:36`, `ObjectMappingGrpcService.cs:73,199`, `AuditLog.cs:11`, `EntityRelationResolver.cs:70,98,132`, `RowFieldAuthorizationEvaluator.cs:20`; single writer at `ActingUserInterceptor.cs:41` |
| 13 | The 5 pre-existing `tenant_id` values needing registry backfill | `service-clients.yaml` (compose) + `blueprints-configmap-service-clients.yaml` (kind), both containing `tenant-loadtest`, `tenant-webtest`, `tenant-admin`, `tenant-smoke-test`, `tenant-bypass` |
| 14 | Postgres RLS (Part B1) only applies to tables that pass `tenantScoped: true` to `IRecordStoreQueryExecutor`; `IversonDlqMessages` doesn't, confirming plain tables are unaffected | `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:5-7`; `DlqRepository.cs` (no `tenantScoped` arguments passed) |
| 15 | All 4 existing `.proto` files live in `Iverson.Clients/Common/Proto/`, compiled with `GrpcServices="Both"` in `Iverson.Client.Contracts.csproj`, which `Iverson.Api` references — confirming new admin protos need no other language client's build pipeline touched | `Iverson.Client.Contracts.csproj:16-17`; `Iverson.Api.csproj:36` |

**Not fully verified — flagged for the implementation plan:** the exact Authentik blueprint YAML shape for an `authentik_core.token` model (no existing blueprint in this repo uses that model type; needs checking against Authentik's own blueprint schema/docs before the plan specifies literal YAML).

## Testing approach

Follows this initiative's established conventions: `NullLogger<T>.Instance`/`Substitute.For<ILogger<T>>()` for unit tests, `AuthTestWebApplicationFactory` for pipeline-level integration tests (suspension enforcement via a real request through `ActingUserInterceptor`, the new `TenantAdmin` policy's rejection/acceptance, grpc-web reachability), unit tests for `IAuthentikAdminClient` (mocked `HttpClient`) and `ITenantRepository` (mirroring `DlqRepository`'s own test shape).
