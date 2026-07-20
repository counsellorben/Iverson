# Tenant Audit Logging — Design Spec

**Status:** Draft, pending user review
**Date:** 2026-07-20
**Part of:** Tenant Data Isolation & Compliance Readiness initiative (Part C of 4 — see `docs/specs/2026-07-17-mandatory-tenant-boundary-design.md`)

## Motivation

Parts A and B of this initiative built a platform-enforced tenant boundary (application-level in Part A; Postgres RLS, Qdrant JWT-scoped collections, and StarRocks per-tenant databases as defense-in-depth in Part B). None of that enforcement is currently observable: a denied cross-tenant read, a denied cross-owner write, or an attempted admin operation by an unauthorized caller today either throws an exception that's never logged, or is silently masked as an ordinary "not found" response. There is no audit trail anywhere in the codebase (`grep -ri audit` across `Iverson.Server` returns nothing).

Part C closes this gap: every denied data-plane access attempt and every admin-level operation gets a structured, filterable log entry — the kind of evidence SOC2/HIPAA auditors expect to see. This spec does not add new storage; it uses the existing structured-logging pipeline (`ILogger`), under a dedicated category so the entries can be filtered/shipped independently of routine application logs.

## Scope and decomposition

This is Part C of the four-part initiative described in `docs/specs/2026-07-17-mandatory-tenant-boundary-design.md`:

- Part A — Mandatory tenant boundary (complete).
- Part B — Defense-in-depth: Postgres RLS, Qdrant tenant-scoped collections, StarRocks per-tenant databases (complete).
- **Part C (this spec)** — Audit logging of denied data-plane access and admin-level operations.
- Part D (future) — Admin APIs for tenant/group lifecycle management.

Explicitly out of scope for this spec:
- New storage or a query API for audit records (structured logs only, per user decision).
- Logging successful data-plane reads/writes (only denials + admin ops, per user decision).
- Logging generic authentication failures (missing/invalid token) on ordinary data-plane calls — only rejections of the two admin-scoped authorization policies (`SchemaAdmin`, `Operator`) are covered. See "Known limitation" below for the boundary this draws.

## Design

### Component 1: `AuditLog` helper

A new class, `AuditLog(ILogger<AuditLog> logger)`, registered in DI and injected wherever a denial or admin operation needs to be recorded — into gRPC service constructors (matching how `IEntityRepository`/`IActingUserAccessor`/etc. are already injected) and into the `/admin/*` minimal-API endpoint lambdas as an added parameter (matching how `IEventProducer` is already injected into those lambdas).

Two methods:

- `Denied(ClaimsPrincipal? actor, string action, string resourceType, string? resourceKey, string reason)` — one structured `LogWarning` per denied attempt.
  - `action`: `"Read"` / `"Create"` / `"Update"` / `"Delete"`.
  - `reason`: `"AccessDenied"` (the general `AuthorizationDecision.Denied` bucket — covers no-`AuthorizationRules`, missing acting-user, missing `tenant_id`/`sub` claim, and genuine role-mismatch alike, since `RowFieldAuthorizationEvaluator.Evaluate` collapses all of these into the same `Denied` flag and the call sites have no way to distinguish them further) / `"TenantMismatch"` / `"TenantImmutable"` / `"OwnerMismatch"` / `"OwnerImmutable"`.
  - Logs `actor`'s `sub` and `tenant_id` claims (when `actor` is non-null), `action`, `resourceType`, `resourceKey` (via `SanitizeForLog()`, matching the existing log-injection-safe convention used elsewhere in this codebase), and `reason`, all as structured fields (not string-interpolated).
- `AdminOperation(ClaimsPrincipal actor, string operation, string? detail)` — one structured `LogInformation` per successful admin action, logging the actor's `sub`, `operation`, and `detail`.

Both log under the single `ILogger<AuditLog>` category regardless of which service triggered them.

### Component 1 call sites

**Read-path masked denials** (currently silent `Found = false` / `Success = false` — no exception thrown today), 4 functions:
1. `ObjectRetrievalGrpcService.cs` `Get` (its auth-denial masked return, `:49`) — action `"Read"`.
2. `ObjectRetrievalGrpcService.cs` `GetMany` — two sites: the `decision.Denied` early-return (`:83-88`, one call per rejected batch, reason `AccessDenied`) and the per-row tenant/owner mismatch check (`:105-109`, same reasons as `Get`'s tenant/owner check) — action `"Read"`.
3. `ObjectMappingGrpcService.cs` `Get` (analogous masked return) — action `"Read"`.
4. `ObjectMappingGrpcService.cs` `Delete` (`:204-216`) — action `"Delete"`.

**Write-path thrown `PermissionDenied`**, 1 shared function (`AuthorizationFieldMasking.EnforceWriteAuthorization`, used by both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService`'s Post/Update — one set of `AuditLog.Denied` calls here covers all 4 of those call sites, not 4 separate additions) with 5 distinct checks inside it. `EnforceWriteAuthorization` is a static method taking every dependency as an explicit parameter (no constructor for DI to inject into) — `AuditLog auditLog` must be added to its signature the same way `authEvaluator` already is, and threaded through by its 4 callers (`ObjectMappingGrpcService.cs:112,159`; `ObjectPersistenceGrpcService.cs:34,85`):
5. `:38-39` — `decision.Denied` → reason `AccessDenied`, action `"Create"` or `"Update"` (passed in by the caller, since this function serves both).
6. `:61-62` — tenant mismatch on existing row → reason `TenantMismatch`, action `"Update"`.
7. `:64-65` — attempted tenant change → reason `TenantImmutable`, action `"Update"`.
8. `:69-70` — owner mismatch on existing row → reason `OwnerMismatch`, action `"Update"`.
9. `:81-82` — attempted owner change → reason `OwnerImmutable`, action `"Update"`.

**Admin operations** (`AuditLog.AdminOperation`, using the service credential — `httpContext.User`, not `ActingUser` — since these aren't tenant-scoped operations), 4 endpoints:
10. `ObjectMappingGrpcService.cs` `RegisterSchema` (`[Authorize(Policy = "SchemaAdmin")]`).
11. `Program.cs` `POST /admin/reconcile/{typeName}`.
12. `Program.cs` `GET /admin/dlq`.
13. `Program.cs` `POST /admin/dlq/{id}/replay`.

In total: 4 read-path functions (one, `GetMany`, contributing two distinct denial branches) + 1 shared write-path function (covering 5 distinct denial checks and 4 upstream call sites) + 4 admin endpoints.

### Component 2: authorization-rejection audit hook

A new class implementing `IAuthorizationMiddlewareResultHandler`, wrapping a private `AuthorizationMiddlewareResultHandler` instance (the built-in default implementation — confirmed present and public in `Microsoft.AspNetCore.Authorization.Policy`), registered as the DI implementation of `IAuthorizationMiddlewareResultHandler` in `Program.cs` after `AddAuthorization(...)`.

```csharp
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

Because this is a single middleware choke point (not per-call-site), it covers `RegisterSchema` + all 3 `/admin/*` endpoints with no changes to those 4 handlers beyond what Component 1 already adds for their success path.

`AuditedPolicies` is deliberately a closed, explicit set — not "every named policy" — so that adding a future named policy for an ordinary data-plane concern doesn't silently start auditing it. Only the two policies that gate admin-level operations are in scope for this spec.

### Known limitation

The bare `RequireAuthenticatedUser()` fallback policy — which guards every data-plane gRPC call that has no explicit `[Authorize(Policy = ...)]` — is deliberately excluded from `AuditedPolicies`. A caller with a missing or invalid token hitting an ordinary data-plane endpoint is not logged by this component. This was an explicit scope decision (see "Scope and decomposition" above): auditing every malformed/unauthenticated request across all data-plane traffic would be high-volume and wasn't part of what this spec was asked to cover. If that need arises later, it's a one-line change to `AuditedPolicies` handling (or a separate, explicitly-scoped follow-up).

Postgres Row-Level Security (added in Part B1) filters cross-tenant rows out of the query result before `ObjectRetrievalGrpcService.Get`/`GetMany` and `ObjectMappingGrpcService.Get`/`Delete` ever reach their in-application tenant-mismatch comparison — the tenant-scoped fetch simply returns no row, and execution takes the same masked "not found" path used for a genuinely nonexistent key. Consequence: a denied cross-tenant read/delete attempt at these four call sites will not produce a distinguishable `TenantMismatch` audit entry; it surfaces (if at all) only as an unremarkable "not found" outcome. Owner-mismatch denials at the same sites are unaffected (RLS scopes by tenant only), and the write path (`Update`, on both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService`) is unaffected — its existing-row fetch deliberately omits tenant scoping, so its `TenantMismatch` throw remains reliably audited. This is accepted as a known limitation rather than resolved by adding a second, unscoped existence probe, which would reintroduce the cross-tenant existence signal RLS's fail-closed design was built to suppress.

## Verified assumptions

| Assumption | Verification | Result |
|---|---|---|
| Read-path denials in `ObjectRetrievalGrpcService.Get` and `ObjectMappingGrpcService.Get` are masked as `Found = false`, not thrown | Read `ObjectRetrievalGrpcService.cs:23-54` | Confirmed — two return sites at `:37` (schema/row not found) is a different case; the auth-denial masked return is at `:49`. Both share the same shape. |
| `ObjectMappingGrpcService.Delete` masks denial as `Success = false`, not thrown | Read `ObjectMappingGrpcService.cs:185-216` | Confirmed — identical masking pattern to the read paths. |
| `AuthorizationFieldMasking.EnforceWriteAuthorization` has exactly 5 `PermissionDenied` throw sites, shared across Post/Update on both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService` | Read `AuthorizationFieldMasking.cs` in full | Confirmed — lines 38-39, 61-62, 64-65, 69-70, 81-82; doc comment explicitly states it's shared by both services' Post/Update. |
| `AuthorizationDecision.Denied` maps cleanly to "wrong role" | Read `RowFieldAuthorizationEvaluator.cs:8-53` | **Wrong** — `Denied` is a general bucket covering: no `AuthorizationRules` configured, null `actingUser`, missing/empty `schema.TenantColumn`, missing `tenant_id` claim, missing `sub` claim (ownership case), and genuine role/rule mismatch. Corrected: reason value renamed from the originally-discussed `RoleDenied` to the more accurate `AccessDenied`. |
| `IActingUserAccessor.ActingUser` exposes `ClaimsPrincipal?` with `sub`/`tenant_id`/`groups` claims via `.FindFirst`/`.FindAll` | Read `IActingUserAccessor.cs`, `ActingUserInterceptor.cs:41-47`, `RowFieldAuthorizationEvaluator.cs:20,24,42` | Confirmed. |
| Admin endpoints (`RegisterSchema`, `/admin/reconcile`, `/admin/dlq`, `/admin/dlq/{id}/replay`) use distinct authorization policies (`SchemaAdmin`, `Operator`) separate from the fallback policy | Read `Program.cs:134-149,304-329`, `ObjectMappingGrpcService.cs:36` | Confirmed — `FallbackPolicy = RequireAuthenticatedUser()` at `:136-137`; the 4 admin endpoints/method each declare an explicit named policy. |
| `/admin/dlq` (GET) and `/admin/dlq/{id}/replay` (POST) minimal-API lambdas can accept an added DI/ambient-bound parameter (`AuditLog`, `ClaimsPrincipal`) without breaking existing tests | Read `Iverson.Api.Tests/AuthenticationPipelineTests.cs:13-53` | Confirmed — these routes are exercised only through a real `WebApplicationFactory<Program>` HTTP pipeline, never invoked via direct reflection on the lambda; ASP.NET's minimal-API parameter binding handles the addition transparently. |
| `IAuthorizationMiddlewareResultHandler.HandleAsync` signature, and whether the built-in default handler (`AuthorizationMiddlewareResultHandler`) is public and wrappable | Read XML docs in `Microsoft.AspNetCore.App.Ref/9.0.17`'s `Microsoft.AspNetCore.Authorization.Policy.xml`/`.Authorization.xml` (local SDK reference assembly, not assumed from memory) | Confirmed — `HandleAsync(RequestDelegate, HttpContext, AuthorizationPolicy, PolicyAuthorizationResult)`; `AuthorizationMiddlewareResultHandler` is documented as "Default implementation for `IAuthorizationMiddlewareResultHandler`" in the public `Microsoft.AspNetCore.Authorization.Policy` namespace. |
| The merged `AuthorizationPolicy` object passed into `HandleAsync` retains the original policy name (`"SchemaAdmin"`/`"Operator"`) for inspection | Read the same XML docs — `AuthorizationPolicy` is documented as the combined/merged policy, not a named-policy passthrough | **Wrong assumption avoided before it was made** — corrected the design to read policy names from `context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>()` instead, which does retain the original `[Authorize(Policy = "...")]`/`.RequireAuthorization("...")` name strings. |
| `SanitizeForLog()` exists and is accessible from a new `AuditLog` class inside `Iverson.Api` | Read `Iverson.Api/LoggingExtensions.cs:5` | Confirmed — `internal static string SanitizeForLog(this string value)`, accessible anywhere within the `Iverson.Api` project. |
| Sibling-set sweep: all 12 individual checks route through only the two `AuditLog` methods with a consistent signature (no site needs a fundamentally different shape) | Re-read all 12 sites collectively (Component 1 section above) | Confirmed — every denial site has a `ClaimsPrincipal?` (via `ActingUser`, possibly null), an action, a resource, and a reason; every admin-op site has a `ClaimsPrincipal` (via `httpContext.User`, always authenticated by definition since it reached the handler) and an operation name. No site required a third method shape. |

## Testing approach

- Unit tests for `AuditLog.Denied`/`AuditLog.AdminOperation` verifying the structured log entry's fields (using a test `ILogger` capture, matching existing patterns in this codebase for asserting on structured log calls).
- Unit/integration tests at each of the call sites enumerated above verifying an `AuditLog` call fires with the correct `action`/`reason` for each distinct denial path (tenant mismatch, owner mismatch, immutability, generic access-denied) — extending the existing authorization test suites for these services rather than introducing new test files where an existing one already covers the call site.
- Integration test for `AuditingAuthorizationMiddlewareResultHandler` (via `AuthTestWebApplicationFactory`, the same fixture `AuthenticationPipelineTests` already uses): a caller without the `Operator`/`SchemaAdmin` role hitting `/admin/dlq` or `RegisterSchema` produces both the expected 403 *and* an audit log entry; a caller without any valid token at all (fallback-policy rejection) produces the 401 but *no* audit log entry, proving the policy-name filter works in both directions.
