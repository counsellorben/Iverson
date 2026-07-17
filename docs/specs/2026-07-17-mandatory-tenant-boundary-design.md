# Mandatory Tenant Boundary — Design Spec

**Status:** Draft, pending user review
**Date:** 2026-07-17
**Part of:** Tenant Data Isolation & Compliance Readiness initiative (Part A of 4 — see [Scope and decomposition](#scope-and-decomposition))

## Motivation

Iverson's existing row/field authorization system (built across Parts 5a-5d of the prior identity-management initiative) is role-based access control (RBAC), not tenant isolation. A schema's `AuthorizationRules` grant row/field permissions per role (an arbitrary Authentik group name), and a role with `CanReadAll = true` can read **every row in the table**, regardless of which organization/customer that row logically belongs to. There is no concept anywhere in the codebase of a hard, structural boundary between one tenant's data and another's.

This was raised proactively (no specific customer or compliance deadline yet) in service of a future goal: if Iverson is used as a genuine multi-tenant platform, tenants may have their own compliance obligations (SOC2, HIPAA), which typically expect data isolation to be a platform-enforced guarantee, not something that depends on every schema owner correctly configuring RBAC rules.

A closely related, already-closed gap informed this work without being its cause: a 2026-07-16 critical security review flagged a *latent* risk in StarRocks's `Expression` field validation that could have allowed a future query-builder refactor to silently reintroduce a cross-tenant read. That specific finding (and two related ones) were fully remediated and pushed to `origin/main@8e211e6` before this spec was written — confirmed by reading the current code, not assumed from the review document. It is not the driver for this spec, but it's a concrete illustration of the general problem this initiative addresses: today's isolation is entirely a property of correctly-written application code, with no structural backstop.

## Scope and decomposition

This is the first of four sequenced parts of a larger initiative:

- **Part A (this spec)** — Mandatory tenant boundary: every registered schema has a platform-enforced tenant partition, independent of whatever RBAC rules it configures.
- **Part B (future)** — Defense-in-depth: a second, independent enforcement layer beneath application code (e.g. Postgres Row-Level Security), so a future query-builder bug can't silently reintroduce a cross-tenant leak.
- **Part C (future)** — Audit logging of data access, especially denied/attempted cross-tenant access and admin-level operations — the kind of evidence SOC2/HIPAA auditors expect.
- **Part D (future)** — The two admin APIs originally requested (system-wide tenant/group lifecycle + delegated per-tenant admin), built on top of the hardened model from A-C.

Explicitly out of scope for **this** spec: B, C, and D above; backfilling tenant values onto existing rows of already-registered types (an implementation-plan task); database-level defense-in-depth; audit logging; tenant lifecycle management; and the exact Authentik provisioning mechanics for automation-client claims (flagged below as an unresolved risk, to be settled at plan time).

## Design

### Identity model

- A new dedicated Authentik claim, `tenant_id` (single string value), distinct from the existing multi-valued `groups` claim used for RBAC roles.
- **One tenant per user, strictly.** No user or service credential carries more than one tenant at a time.
- **No operator bypass.** The existing `operators` group continues to have its current rights for non-data operations (`/admin/*` DLQ/reconcile, `SchemaAdmin`-gated schema registration) — those aren't tenant-scoped operations. For the 4 data-plane gRPC services (`ObjectMapping`, `ObjectPersistence`, `ObjectRetrieval`, `ObjectSearch`), operators are tenant-scoped exactly like any other caller. A compromised or misused operator credential cannot read another tenant's data through the data-plane APIs.
- Service/automation callers (`loadtest-client`, `webtest-client`, `admin-automation-client`) each carry their own `tenant_id` claim, provisioned per-client, so their data-plane calls are tenant-scoped the same way a human user's are — no special-casing in enforcement logic.
- If `tenant_id` is absent from an otherwise-valid token calling one of the 4 data-plane gRPC services, the call is rejected (`PermissionDenied`). There is no "no tenant = unrestricted" fallback.

### Schema registration

- `TypeDescriptor` (the proto message in `object_mapping.proto`, sibling to `type_name`/`properties`/`relations`/`authorization`) gains a new required field, `tenant_field` (string, a property name on the type). This is deliberately **not** nested inside `AuthorizationRules` — that message is itself optional on `TypeDescriptor`, and a mandatory tenant boundary must exist even for schemas that configure no `AuthorizationRules` at all.
- `RegisterSchema` rejects any request missing `tenant_field` with `InvalidArgument`, following the same validation pattern as the existing `root_type is required` check.
- `SchemaBuilder.BuildDescriptor` reads `tenant_field` into a new `SchemaDescriptor.TenantColumn`, mirroring how `owner_field` is read into `OwnerColumn` today.
- **Hard cutover, no grace period.** Every currently-registered type (including LoadTest's `BenchmarkArticle`) must re-register with a `tenant_field` before further use. `SchemaRegistry.RegisterAsync` already performs an unconditional replace with no merge-preservation logic, so re-registration with a newly-added `tenant_field` requires no additional mechanism — it works exactly like registering a brand-new schema. Backfilling a tenant value onto that type's *existing rows* is a separate, necessary step before production traffic resumes for it; that backfill is an implementation-plan task, not a design decision made here.

### Authorization evaluation

`RowFieldAuthorizationEvaluator.Evaluate` reads `actingUser.FindFirst("tenant_id")?.Value` the same way it already reads `"sub"` and `"groups"` — no new accessor abstraction is introduced. `AuthorizationDecision` gains two new fields, `TenantColumn`/`TenantValue`, populated whenever the schema has a `TenantColumn` (always, since it's now mandatory) and the caller has a `tenant_id` claim (always, since it's now mandatory for callers too).

**This is a decision, confirmed with the user, not a default I picked silently:** the tenant check is **strictly additive** to the existing role-based logic, which is otherwise **unchanged**. A schema with no `AuthorizationRules` configured continues to deny everything outright, exactly as today (`RowFieldAuthorizationEvaluator.cs:11-12`) — the mandatory tenant boundary does not grant baseline access on its own. Tenant enforcement only has an observable effect on schemas that **do** configure `RowPermissions` with `CanReadAll`/`CanWriteAll`/`CanDeleteAll` — today, such a role can read/write every row in the table; going forward, "all" is scoped to "all rows in the caller's own tenant." This is the specific, narrow gap this design closes.

### Per-store enforcement

Each store's existing enforcement point (built in Parts 5b/5c/5d) gets one additional predicate, ANDed alongside whatever the existing role/ownership decision produces:

- **Postgres** (`ObjectMappingGrpcService`/`ObjectPersistenceGrpcService`/`ObjectRetrievalGrpcService`): tenant predicate wraps the outer `WHERE`, the same way the existing ownership predicate does.
- **StarRocks** (`ObjectSearchGrpcService` Search/Aggregate/GroupBy/Pipeline): `AuthorizationConstraint` (currently `AllowedFields`/`OwnerColumn`/`OwnerValue`) gains `TenantColumn`/`TenantValue`. The tenant predicate follows the same primary-type-`WHERE`-vs-joined-type-`ON` split already established for ownership, to avoid the join-collapse bug that pattern was specifically designed to prevent. The existing case-insensitive (`StringComparer.OrdinalIgnoreCase`) keying of the per-join-type constraints dictionary — added as a whole-branch-review fix for a real cross-tenant bypass in 5c — must be preserved for the new tenant fields; this is the same class of bug and the same fix applies.
- **Qdrant** (`SearchSimilar`/`SearchChunks`): a new `QdrantFilterBuilder.ApplyTenant` method, mirroring the existing `ApplyOwnership` method's shape exactly (`bool tenantRequired, string? tenantFieldCamelCase, string? tenantValue`).

### Known unresolved risk (flagged, not resolved by this spec)

The Authentik scope mappings backing `loadtest-client`/`webtest-client`/`admin-automation-client` today only return static presence booleans (`return {}` for `admin`/`schema_admin`) — there is no existing precedent in this codebase for a per-client **value** claim. The `groups` claim mechanism (`return [group.name for group in user.ak_groups.all()]`) works because human logins are tied to a real Authentik User object; whether a client-credentials-only provider has enough context in its scope-mapping expression to source a comparable per-client `tenant_id` value (without a bound user) is **not confirmed**. Resolving this — likely by binding a dedicated service-account User to each automation client, if Authentik's model supports it — is deferred to the implementation-planning phase rather than asserted here.

## Verified assumptions

| Assumption | Verification | Result |
|---|---|---|
| `owner_field` sits on `TypeDescriptor` directly | Read `object_mapping.proto:62-98` | **Wrong** — it's nested inside the optional `AuthorizationRules` message. Corrected: `tenant_field` is its own top-level `TypeDescriptor` field. |
| `SchemaBuilder.BuildDescriptor` reads `owner_field` into `OwnerColumn` | `grep` + read `SchemaBuilder.cs:92,142` | Confirmed. |
| `RegisterSchema` has a `root_type is required` validation precedent | `grep` `ObjectMappingGrpcService.cs:45` | Confirmed. |
| `SchemaRegistry.RegisterAsync` re-registration semantics | Read `SchemaRegistry.cs:47-56` | Confirmed unconditional blind replace, no merge/preserve logic — re-registration with a new mandatory field needs no extra mechanism. |
| `IActingUserAccessor` exposes parsed `Sub`/`Groups`/could expose `TenantId` | Read `IActingUserAccessor.cs`, `RowFieldAuthorizationEvaluator.cs:17,35` | **Wrong** — accessor is a bare `ClaimsPrincipal` holder; claim parsing happens in the evaluator directly. Corrected in design above. |
| `AuthorizationDecision`'s fields and consumer count | Read `IRowFieldAuthorizationEvaluator.cs:16-27`; `grep` construction sites | Confirmed — matches memory, exactly 5 construction sites, all in `RowFieldAuthorizationEvaluator.cs`. |
| "No `AuthorizationRules` → deny all" location and behavior | Read `RowFieldAuthorizationEvaluator.cs:10-15` | Confirmed current behavior; surfaced an internal inconsistency in the first-presented design (resolved with user: kept unchanged, see Design section). |
| StarRocks `AuthorizationConstraint` shape | Read `AuthorizationConstraint.cs:3-6` | Confirmed matches memory (`AllowedFields`/`OwnerColumn`/`OwnerValue`). |
| StarRocks case-insensitive constraint-dictionary fix still in place | `grep` `ObjectSearchGrpcService.cs:443` | Confirmed `StringComparer.OrdinalIgnoreCase` present. |
| Qdrant ownership filter mechanism | Read `QdrantFilterBuilder.cs:54-58` | Confirmed `ApplyOwnership` method exists with the expected shape; `ApplyTenant` can mirror it directly. |
| The 4 JWT audiences are current | `grep` `docker-compose.yml:269-274` | Confirmed: human-oidc, loadtest, webtest, admin-automation, plus the separate ActingUser (human) audience. |
| Authentik `groups` claim mechanism for human users | Read `service-clients.yaml:37-43` | Confirmed: `return [group.name for group in user.ak_groups.all()]`, tied to a real User object. |
| Authentik scope-mapping mechanism for service/automation clients | Read `service-clients.yaml:23-36` | `admin`/`schema_admin` mappings return static `{}` — no precedent for a per-client value claim. **Unresolved**, flagged above, deferred to plan-level verification. |

## Testing approach

- Unit tests on the evaluator: tenant mismatch denies access regardless of role permissions (including `CanReadAll`/`CanWriteAll`/`CanDeleteAll`).
- Per-store integration tests proving cross-tenant rows are invisible even to a caller whose role grants `CanReadAll` within a different tenant.
- A regression test mirroring the 5c precedent: an adversarial join or expression cannot be used to strip or bypass the tenant predicate.
- A test confirming a schema with no `AuthorizationRules` configured remains fully denied post-change (locking in the "no behavior change" decision above).
