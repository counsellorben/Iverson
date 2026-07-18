# Task 3 Report: Postgres read + relation-traversal + write + delete enforcement

## Status: DONE

## What I did

Wired `AuthorizationDecision.TenantColumn`/`TenantValue` (populated by Task 2's evaluator)
into the 5 enforcement points named in the brief, adding a tenant clause next to each
existing owner-comparison clause, using the exact same pattern (same accessor, same denial
shape) and the exact code given in the brief, verbatim. Every tenant check is unconditional —
never gated behind `decision.OwnershipRequired` or any role/bypass check.

## Per-file changes

### `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`
- `Get`: added `(decision.TenantColumn is not null && StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue)` as a third OR-branch alongside `Denied` and the owner-mismatch check → returns `Found = false`.
- `GetMany`: added the identical tenant clause OR'd with the existing per-row ownership check inside the streaming loop.

### `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- `Get`: same 3-way OR clause as Retrieval.Get, using `entityStruct`.
- `Delete`: same 3-way OR clause, parsing `rowJson` into a `Struct` for the tenant comparison (mirrors the existing owner-mismatch parse), returning the same not-found response.

### `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs`
- `TryAuthorizeAndMask`: added the tenant clause to the private helper shared by all three relation-kind resolvers (ManyToOne/OneToOne, ManyToMany, OneToMany), so `Get` with `depth>0` cannot surface a cross-tenant related row through any relation kind.

### `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` (`EnforceWriteAuthorization`)
- Create branch (`existingRowJson is null`): unconditionally force-sets `payload.Fields[decision.TenantColumn] = decision.TenantValue` whenever `decision.TenantColumn is not null` — runs regardless of `OwnershipRequired`, so bypass (`CanWriteAll`) callers still get stamped.
- Update branch: before the existing owner-mismatch check, added an unconditional tenant-mismatch check (existing row's tenant must equal `decision.TenantValue`, else `PermissionDenied` with the caller-supplied `deniedMessage`) followed by an unconditional tenant-immutability check (a payload that supplies a different tenant value throws `PermissionDenied` with `"Tenant field is immutable."`). Both apply to bypass callers.
- `RejectDisallowedFields(...)` call was **not** touched — kept exactly as the brief specified (see Concern below).

## Tests added

- `ObjectRetrievalGrpcServiceTests.cs`: `Get_WithNonMatchingTenant_ReturnsNotFound`, `Get_WithBypassRoleAndNonMatchingTenant_ReturnsNotFound`, `GetMany_WithNonMatchingTenant_StreamsNotFound`, `GetMany_WithBypassRoleAndNonMatchingTenant_StreamsNotFound`.
- `ObjectMappingGrpcServiceTests.cs`: `Get_WithNonMatchingTenant_ReturnsNotFound`, `Get_WithBypassRoleAndNonMatchingTenant_ReturnsNotFound`, `Get_WithRelatedEntities_OmitsCrossTenantRelatedEntity_KeepsSameTenantOne` (depth=1, ManyToMany relation, bypass-authorized schema — proves tenant wins over row-level bypass in relation traversal), `Post_ForOrdinaryCaller_StampsTenantOntoPayload`, `Post_WithBypassRole_StillStampsTenantOntoPayload`, `Update_WithNonMatchingTenant_ThrowsPermissionDenied`, `Update_WithBypassRoleAndNonMatchingTenant_ThrowsPermissionDenied`, `Update_AttemptingToChangeTenantField_ThrowsPermissionDenied`, `Update_WithBypassRoleCaller_AttemptingToChangeTenantField_ThrowsPermissionDenied`, `Delete_WithNonMatchingTenant_ReturnsNotFound`, `Delete_WithBypassRoleAndNonMatchingTenant_ReturnsNotFound`.
- `ObjectPersistenceGrpcServiceTests.cs`: `Post_ForOrdinaryCaller_StampsTenantOntoPayload`, `Post_WithBypassRole_StillStampsTenantOntoPayload` (this file shares the same `EnforceWriteAuthorization` gate as Mapping, so create-stamp coverage here confirms the fix isn't Mapping-specific).

All new tests confirmed **failing** against the pre-implementation code (17 failures, exactly matching the new test count, zero regressions among the 373 pre-existing tests) before I made the 5 code changes — a clean TDD red state.

## Fixture fallout (expected, per the Task 2 handoff note) and what I fixed

Task 3 is the *first* code to actually compare `decision.TenantColumn`'s row value against
`decision.TenantValue` — Task 2 only computed and returned these fields. That meant every
existing row-JSON literal across these 4 test files (e.g. the shared `AuthorJson`/`ArticleJson`
constants, and the many per-test `ownedJson`/`allowedTagJson`/`postJson` locals) needed a
matching `"TenantId":"test-tenant"` added, or the new read/relation/delete tenant clause would
flip every currently-passing "found"/"success" test to "not found" (missing field → `null` →
mismatch against the caller's `test-tenant` claim). I added this to every literal used on a
success path across `ObjectRetrievalGrpcServiceTests.cs`, `ObjectMappingGrpcServiceTests.cs`,
`ObjectPersistenceGrpcServiceTests.cs`, and `EntityRelationResolverTests.cs` (including the
ManyToMany/OneToMany relation fixtures, which have full row-level bypass authorization but
still need a matching tenant since tenant is orthogonal to ownership).

This is explicitly **not** a repeat of Task 2's SchemaFixtures/ActingUserFixtures cutover
(schema/principal declarations) — it's row-level *data* literals, which had no reason to carry
tenant data before this task started consuming it. I verified via a red/green diff (17 failures
before the 5 code changes, 0 unrelated regressions) that this was the correct, minimal set of
literals to touch.

One additional, distinct fixture gap surfaced only after implementing Step 3 (unconditional
tenant stamp): the local `OwnedAuthorSchema()` helper duplicated across all 3 Grpc test files
declares `TenantColumn = "TenantId"` but never listed `"TenantId"` in `ScalarColumns`. Real
schemas built via `SchemaBuilder.BuildDescriptor` always include the tenant field's underlying
property in `ScalarColumns` (it's an ordinary non-key property), so this test fixture didn't
match production shape. The mismatch only bites when a `FieldPermission` on some *other* field
is active (`decision.AllowedFields` becomes non-null) — in that mode, `AllowedFields` is built
from `ScalarColumns` etc., so a field absent from `ScalarColumns` can never be in
`AllowedFields`, and the just-stamped `"TenantId"` gets rejected by `RejectDisallowedFields` as
"not permitted." Two pre-existing tests (`Post_ForOrdinaryCaller_WithFieldPermissionRestrictingOwnerColumn_StillForceSetsOwnerField`
in both Mapping and Persistence test files) hit exactly this. I fixed it by adding
`new ColumnDescriptor("TenantId", "text", false)` to `OwnedAuthorSchema()`'s `ScalarColumns` in
all 3 files, matching real `SchemaBuilder` output — not by touching
`AuthorizationFieldMasking.RejectDisallowedFields`, which the brief did not ask me to change.

I also found one inline row-JSON literal in `ObjectPersistenceGrpcServiceTests.cs`
(`Update_WithRestrictedFieldInWritePayload_ThrowsInvalidArgument`, line ~615) that didn't follow
the `ownedJson`/`AuthorJson` naming convention my grep sweep first targeted; added the missing
`"TenantId":"test-tenant"` there too.

## Test results

`dotnet test Iverson.Server/Iverson.Api.Tests --filter "Category!=Integration"` → **390 passed,
0 failed, 0 skipped** (up from 373 before this task's new tests; +17 new tests, all passing;
zero regressions).

## Concerns (not fixed — flagging per instructions, not silently deviating)

1. **`RejectDisallowedFields` has no `TenantColumn` exemption analogous to `OwnerFieldName`'s
   `exemptField` parameter.** In production, if a schema author defines a `FieldPermission`
   entry that specifically names the tenant column and restricts `WritableRoles` to a role the
   caller lacks, `decision.AllowedFields` would exclude the tenant column — and the just-forced
   tenant stamp (Step 3) would then be rejected by `RejectDisallowedFields` as "field not
   permitted," breaking every write for that schema. This is a narrow, deliberate
   misconfiguration scenario (a schema author would have to explicitly restrict the tenant
   field itself, which seems like an unlikely but possible mistake), and the brief's Step 3/4
   code didn't ask me to add such an exemption, so I left `RejectDisallowedFields`'s call site
   untouched exactly as specified. Worth a follow-up ticket if this is judged worth closing —
   the fix would mirror `exemptField: decision.OwnerFieldName` by also exempting
   `decision.TenantColumn`.
2. The `OwnedAuthorSchema()` test-fixture fix (adding `TenantId` to `ScalarColumns`) is
   duplicated identically across 3 test files (Retrieval/Mapping/Persistence) — this
   pre-existing duplication (not introduced by me) makes this kind of fixture-shape drift easy
   to reintroduce. Not fixing it now since deduplicating test helpers across files is out of
   scope for this task, but noting it in case a future task wants to extract a shared test
   helper.

## Self-review checklist (all confirmed by re-reading the diff)

- All 3 read sites (Retrieval.Get, Retrieval.GetMany, Mapping.Get): tenant check is a bare
  `decision.TenantColumn is not null && mismatch` OR-branch — not gated behind
  `OwnershipRequired` or any role check.
- Relation-traversal helper (`TryAuthorizeAndMask`): same unconditional OR-branch, shared by
  all three relation-kind resolvers.
- Write-create (`EnforceWriteAuthorization`, `existingRowJson is null` branch): tenant stamp is
  a bare `if (decision.TenantColumn is not null)` — separate from and not nested inside the
  `if (decision.OwnershipRequired)` block.
- Write-update (existing-row branch): tenant match + immutability is its own unconditional
  `if (decision.TenantColumn is not null)` block, placed before (and independent of) the
  ownership-mismatch check.
- Delete (`ObjectMappingGrpcService.Delete`): same unconditional 3-way OR pattern as the read
  sites, verified with two new tests that specifically use `CanDeleteAll` bypass + a
  cross-tenant row and confirm the delete is still blocked (not-found) and
  `_entities.DeleteAsync` is never called.
