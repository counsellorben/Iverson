# Postgres Authorization Enforcement (Part 5b) — Design

## Context

This is Part 5b of the 5-part identity-management initiative. Part 5a is complete and merged:

- Schema registration carries optional `AuthorizationRules` per type (owner field + row-level role→permission rules + field-level role→permission rules).
- `IRowFieldAuthorizationEvaluator` (`Iverson.Server/Iverson.Api/Authorization/`) is a pure function: given a `SchemaDescriptor`, an acting-user `ClaimsPrincipal?`, and an `AuthorizationAction` (Read/Write/Delete), it returns an `AuthorizationDecision` (`Denied`; `OwnershipRequired` + `OwnerFieldName` + `OwnerValue`; `AllowedFields` — a nullable set, null = unrestricted).
- Nothing calls the evaluator yet. 5a wired zero enforcement into any RPC.

Part 5b wires that evaluator into the Postgres write and read paths — the last piece before enforcement is actually live. Later parts (5c StarRocks reads, 5d Qdrant reads) will do the equivalent for their stores.

## Goals

1. Enforce row-level and field-level authorization on every Postgres-backed write and read RPC: `ObjectMappingGrpcService` (`Post`/`Update`/`Delete`/`Get`, including recursive relation traversal) and `ObjectPersistenceGrpcService` (`Post`/`Update`), and `ObjectRetrievalGrpcService` (`Get`/`GetMany`).
2. On create, the owner field is server-controlled: force-set to the acting user's own `sub` for ordinary callers; left to the client for callers holding a write-bypass role (`CanWriteAll`).
3. Restricted write fields are rejected outright (not silently stripped); restricted read fields are masked from the response.
4. Ownership is immutable after creation — Update can never change a row's owner field, for any caller.

## Non-goals (explicitly out of scope for 5b)

- StarRocks/Qdrant enforcement (5c/5d — separate plans).
- Any UI/tooling for authoring `AuthorizationRules` — schema registration remains a raw proto call, unchanged from 5a.
- Adding a `Delete` RPC to `ObjectPersistenceGrpcService` — it doesn't have one today and this plan doesn't add one.
- Migrating or backfilling `AuthorizationRules` onto any existing registered schema.

## Design

### 1. Shared enforcement mechanics

**DI wiring**: `IActingUserAccessor` and `IRowFieldAuthorizationEvaluator` (both already registered from Part 4 / 5a) get added to the constructor of all 3 services. None currently inject either. `ObjectPersistenceGrpcService` additionally gains `IEntityRepository` (it has none today — needed for Update's new pre-fetch, §6).

**Two new static helpers**, since the same logic is needed across 5+ call sites. Payload field keys can arrive in either PascalCase or camelCase (client's choice), while `AllowedFields` (produced by the evaluator) always contains canonical schema-declared names — so both helpers canonicalize each payload key via the already-existing `StructSerializer.UpperFirst(key)` (the same canonicalization `SerializePayload` itself applies, just performed earlier, at the point these checks run) before testing membership, rather than comparing raw keys or using `StructFieldAccess.Candidates` (which resolves the opposite direction — canonical-name-to-struct-key, not struct-key-to-canonical-name):
- `MaskDisallowedFields(Struct payload, IReadOnlySet<string>? allowedFields)` — for reads: when `allowedFields` is non-null, removes every field from `payload.Fields` whose `UpperFirst`-canonicalized key is not in the set (a direct `Fields.Remove(...)` per non-matching key — `Struct.Fields` is already mutated in-place elsewhere in this codebase, e.g. `ObjectMappingGrpcService.cs:368`); no-op when null.
- `RejectDisallowedFields(Struct payload, IReadOnlySet<string>? allowedFields, string? exemptField = null)` — for writes: when `allowedFields` is non-null, throws `RpcException(InvalidArgument, ...)` naming any payload field whose `UpperFirst`-canonicalized key is neither in the set nor equal to `exemptField`; no-op when `allowedFields` is null. `exemptField` lets the owner-field write (§4/§5, which the server itself controls, not the client) bypass a `FieldPermission` entry that happens to target the owner field's own name — see §4 step 3.

**Evaluator call pattern**: `var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, action);`, then branch per RPC (below). Ownership checks read a row's current owner-field value via the already-existing `StructFieldAccess.GetFieldString(rowStruct, decision.OwnerFieldName)`.

**Status codes**: write-path denial is `RpcException(StatusCode.PermissionDenied, ...)` — distinct from the "not found" masking used for reads (a write is an active attempt; there's no existing-row-existence to protect). Read-path denial reuses each RPC's existing not-found response shape (`Success=false`/`Found=false` + the existing "not found" error text where applicable) — this doesn't reveal to the caller whether a row exists but is forbidden versus doesn't exist at all.

### 2. `ObjectMappingGrpcService.Get` (root + recursive relation enforcement)

After the existing fetch-by-key:
1. `Evaluate(schema, ActingUser, Read)`. If `Denied`, or `OwnershipRequired` and the fetched row's owner-field value doesn't match `OwnerValue` → return the existing not-found response.
2. Otherwise, `MaskDisallowedFields(entityStruct, decision.AllowedFields)`.
3. If `request.Depth > 0`, relation traversal proceeds as today, but each resolver (`ResolveSingleRelationAsync`, `ResolveManyToManyAsync`, `ResolveOneToManyAsync`) applies the same two steps — row-level check then field masking — to every related row it fetches, using that related type's own `SchemaDescriptor` (already looked up via `_registry.Get(relation.RelatedTypeName)`) and the same `ActingUser`, before splicing the row into the parent struct. A related entity/item that fails its row-level check is simply not spliced in (single-valued: field omitted from the parent struct; collection: item omitted from the list) — traversal continues for the rest.

This makes the row-level+field-level check one recursive unit applied uniformly at every level of traversal, not special-cased at the root.

### 3. `ObjectRetrievalGrpcService.Get` / `GetMany`

Same root-level pattern as §2, minus recursion (this service never traverses relations):
- `Get`: after fetch, `Evaluate(schema, ActingUser, Read)`; `Denied` or ownership-mismatch → `Found = false`. Otherwise mask fields, return `Found = true`.
- `GetMany`: the evaluator is called **once per call, not once per key** (schema — and therefore the decision's `Denied`/`OwnershipRequired`/`AllowedFields` — doesn't vary per row, only per caller/schema/action). Applied per-key in the existing streaming loop: a denied key streams `Found = false`, exactly like a missing key does today; an authorized key gets its struct masked before streaming. Ownership mismatch is still checked per-row (each row's own owner-field value differs).

### 4. `Post` (both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService`)

After schema resolution, before relation validation:
1. `Evaluate(schema, ActingUser, Write)`. `Denied` → `RpcException(PermissionDenied)`.
2. Owner-field handling: if `decision.OwnershipRequired == false` (a bypass role matched) → leave the payload's owner field exactly as the client sent it (including absent). Otherwise (`OwnershipRequired == true`) → force-set the payload's owner field to `decision.OwnerValue`, overwriting whatever the client sent.
3. `RejectDisallowedFields(payload, decision.AllowedFields, exemptField: decision.OwnerFieldName)` — the owner field is exempt from this check because, in the non-bypass path, the server (not the client) is the one that put that value in the payload (step 2); a `FieldPermission` entry that happens to target the owner field's own name must not be able to block ordinary-caller creation, which Goal 2 requires to always succeed regardless of field-level rules on that one field.
4. Continue with existing relation validation, key generation, serialization, write — unchanged.

A schema with `AuthorizationRules` configured and an `owner_field` set always resolves to either bypass or `OwnershipRequired = true` for an authenticated caller (never `Denied` — per the evaluator's logic, `Denied` on the Write path only occurs with no rules at all, no identity, or rules-configured-but-no-owner-field-and-no-bypass), so step 2 is reachable by essentially every authenticated create call on an owner-configured schema.

### 5. `Update` (both services)

Both services' `Update` currently blind-upserts (`INSERT ... ON CONFLICT DO UPDATE`) — a row that doesn't yet exist is silently created, not rejected. Ownership enforcement needs the row's *current* owner value, which requires a new pre-fetch. `ObjectPersistenceGrpcService` gains `IEntityRepository` for this (§1).

After schema resolution and key extraction (existing, unchanged position):
1. **New pre-fetch**: `var existingRowJson = await _entities.FetchByKeyAsync(schema, key);` (mirrors what `ObjectMappingGrpcService.Delete` already does at `ObjectMappingGrpcService.cs:253`).
2. `Evaluate(schema, ActingUser, Write)`. `Denied` → `PermissionDenied`.
3. **If `existingRowJson` is null** (row doesn't exist yet): treat exactly like `Post` (§4) — same owner-field bypass/force-set logic. This preserves the existing UPSERT-as-create-if-absent behavior unchanged; it is not a new decision.
4. **If `existingRowJson` is non-null** (row exists):
   - If `decision.OwnershipRequired` (no bypass) and the existing row's owner-field value doesn't match `decision.OwnerValue` → `PermissionDenied`.
   - Only when the payload *itself contains* the owner field (`StructFieldAccess.GetFieldString(payload, schema.Authorization?.OwnerField)` returns non-null — an absent field is not an attempted change and never triggers this check): if that value differs from the existing row's current owner-field value → `PermissionDenied` (ownership is immutable post-creation — this check applies **regardless of bypass role**, since a write-bypass grants broader read/write of other people's rows, not the ability to reassign ownership, which was never asked for). **The field name here comes from `schema.Authorization?.OwnerField`, not `decision.OwnerFieldName`** — the evaluator only populates `OwnerFieldName` in its non-bypass branch, so it is always `null` for bypass callers, exactly the caller class this check must also cover; immutability is a property of the schema, not of any one caller's bypass status.
5. `RejectDisallowedFields(payload, decision.AllowedFields, exemptField: decision.OwnerFieldName)` — same reasoning as §4 step 3 (the server, not the client, is responsible for the owner field's value in the non-bypass path). For bypass callers `decision.OwnerFieldName` is `null`, which is harmless here: bypass callers never have their owner field force-set (step 3/step 4 don't touch it for them), so there is nothing to exempt.
6. Continue with existing relation validation, serialization, write — unchanged.

### 6. `Delete` (`ObjectMappingGrpcService` only)

The existing pre-fetch (`ObjectMappingGrpcService.cs:253`, currently used only to snapshot the row for the outbox/event payload) is reused for the ownership check — no new read needed:
1. After the existing fetch-and-not-found-check, `Evaluate(schema, ActingUser, Delete)`.
2. `Denied`, or `OwnershipRequired` and the pre-fetched row's owner-field value doesn't match `OwnerValue` → return the same not-found response already used for a missing row (consistent with §2/§3's "don't leak existence" treatment).
3. Continue with the existing delete transaction — unchanged. (Delete has no field-level dimension — the evaluator always returns `AllowedFields = null` for the Delete action.)

`ObjectPersistenceGrpcService` has no `Delete` RPC — nothing to enforce there.

## Testing plan

Unit tests added to the 3 existing test files (`ObjectMappingGrpcServiceTests.cs`, `ObjectPersistenceGrpcServiceTests.cs`, `ObjectRetrievalGrpcServiceTests.cs`), using the **real** `RowFieldAuthorizationEvaluator` (already exhaustively unit-tested in 5a) against hand-built `SchemaDescriptor.Authorization` + `ClaimsPrincipal` fixtures — verifying each RPC correctly *wires up and consumes* the evaluator's decision, not the evaluator's internal logic again. Per RPC, cover: denied (no rules configured), denied (no identity), owner-match allowed, bypass-role allowed, non-owner denied, restricted-field-in-write-payload rejected, restricted-field-absent-from-read-response. `Update` additionally covers: row-doesn't-exist-yet (create-path owner logic applies), ownership-immutability rejection (with and without a bypass role). `Get` additionally covers: recursive traversal with one allowed and one denied related entity in the same call (denied one omitted, rest present).

## Known consequences, accepted by user

Two consequences flagged as accepted-in-principle during 5a's brainstorm now become live behavior:
- Every schema with no `AuthorizationRules` configured will reject every Post/Update/Delete/Get call once this lands — this affects every currently-registered schema in the system, since none has `AuthorizationRules` configured yet.
- Every call with no acting-user identity (`x-acting-user-authorization` header absent) to a rules-configured schema will also be rejected.

Neither is fixed by this plan; migrating/configuring existing schemas remains explicitly out of scope (see Non-goals).

## Verified assumptions

- `ObjectMappingGrpcService`'s constructor (`ObjectMappingGrpcService.cs:23-36`) has neither `IActingUserAccessor` nor `IRowFieldAuthorizationEvaluator`. `ObjectPersistenceGrpcService`'s constructor (`ObjectPersistenceGrpcService.cs:16-22`) has neither of those, nor `IEntityRepository`. `ObjectRetrievalGrpcService`'s constructor (`ObjectRetrievalGrpcService.cs:9-12`) has neither `IActingUserAccessor` nor `IRowFieldAuthorizationEvaluator`. All confirmed via direct read.
- `StructFieldAccess.GetFieldString(Struct, string)` (`StructFieldAccess.cs:25-31`) exists with that signature; all lookups in this file go through `Candidates(name)` (lines 10-15), matching a field name and its camelCase form — the new masking/rejection helpers must use the same matching, not naive equality, to behave consistently with the rest of the RPC surface.
- `Struct.Fields[key] = value` direct mutation is an already-established pattern in this codebase (confirmed at `ObjectMappingGrpcService.cs:368,397,422`, `EntityKeyAccessor.cs:26,29`, `ObjectSearchGrpcService.cs:146,448`) — `MapField<string,Value>` supports indexer set and, by the same `IDictionary` surface, `.Remove(key)`, for the new `MaskDisallowedFields` helper. No existing "remove field"/"mask field"/"strip field" helper exists anywhere in the repo (grepped for `RemoveField`/`MaskField`/`StripField` — zero hits) — this is genuinely new, not a duplicate.
- `ObjectMappingGrpcService.Delete`'s pre-fetch (`FetchByKeyAsync`, line 253) happens well before the delete transaction (line 265) — confirmed via full read of the method (lines 246-312) — leaving room to insert the ownership check using the already-fetched `rowJson`.
- All three relation resolvers (`ObjectMappingGrpcService.cs:352-423`) share an identical shape: fetch the related row(s), parse to `Struct`, optionally recurse, *then* splice into the parent (`entityStruct.Fields[relation.PropertyName] = ...` for single relations at lines 368; `items.Add(...)` before `Value.ForList(items.ToArray())` for collection relations at lines 394/397 and 419/422) — confirmed a clean "fetch, check, then splice/add" insertion point exists in all three, and each already has `relatedSchema` in scope from `_registry.Get(relation.RelatedTypeName)` (lines 358, 379, 406) for the recursive `Evaluate` call.
- `IRowFieldAuthorizationEvaluator` is registered `AddSingleton` at `Program.cs:173`, confirmed still present post-5a-merge.
- Both `Update` methods (`ObjectMappingGrpcService.cs:186-244`, `ObjectPersistenceGrpcService.cs:93-158`) extract `key` immediately after schema resolution, well before the write call — confirmed via full read of both methods — leaving a clean insertion point for the new pre-fetch right after key extraction.
- Response proto shapes confirmed via `.proto` read: `MappingResponse` (`object_mapping.proto:131-136`) and `MappingDeleteResponse` (`:138-142`) both have `success`+`error`; `PersistResponse` (`object_persistence.proto:19-24`) has `success`+`error` (not used for denial — `Post`/`Update` use `RpcException` instead); `RetrievalResponse` (`object_retrieval.proto:26-30`) has only `found`+`data`+`trace_id`, no `error` field — consistent with reusing its existing not-found shape (`Found=false`) for denial, which requires no new field.
- `RowFieldAuthorizationEvaluator`'s row-level bypass logic (`RowFieldAuthorizationEvaluator.cs`, read in full post-merge) is exactly as this design assumes: `Denied` iff no rules, no identity, or (no bypass role matched AND no `owner_field` configured); otherwise bypass (`OwnershipRequired=false`) or ownership (`OwnershipRequired=true`, `OwnerFieldName`/`OwnerValue` populated from `sub`). The post-5a-merge `AllowedFields` fix (adding FK/vector/chunk columns) touched only the field-level branch, not this row-level logic — confirmed by reading the full current file. **`OwnerFieldName`/`OwnerValue` are declared `null` and only reassigned inside the non-bypass (`else if`) branch — they are always `null` when `bypass=true`.** This is why §5's immutability check sources the field name from `schema.Authorization?.OwnerField` rather than `decision.OwnerFieldName`: the latter is unavailable for exactly the bypass-caller case that check is required to cover.
- `ObjectRetrievalGrpcService.GetMany` (`ObjectRetrievalGrpcService.cs:42-78`, read in full) resolves `schema` once before its streaming loop — confirmed the design's "evaluate once per call" is directly supported by the existing structure, with per-row-only ownership/masking work inside the loop.
- Consumer impact (Cat 6): exactly 4 files directly construct one of the 3 services with `new ObjectMappingGrpcService(...)` / `new ObjectPersistenceGrpcService(...)` / `new ObjectRetrievalGrpcService(...)` — `ObjectMappingGrpcServiceTests.cs`, `ObjectPersistenceGrpcServiceTests.cs`, `ObjectRetrievalGrpcServiceTests.cs`, and `RegisterSchemaAuthorizationIntegrationTests.cs` (from 5a's Task 3). All 4 will need their constructor-call sites updated when these services' constructor parameter lists grow — a plan-level task, not a design-level concern, but flagged here since it's a direct, unavoidable consequence of §1's DI wiring.
- `StructSerializer.UpperFirst(string)` (`ProtoPayloadHelper.cs:11-12`) is the existing canonicalization `SerializePayload` already applies to convert a client-supplied field key to its canonical (PascalCase) form. Confirmed via fresh read that `SerializePayload` runs *after* the point in every call site (`ObjectMappingGrpcService.cs:141,201`, `ObjectPersistenceGrpcService.cs:40,107`) where `RejectDisallowedFields`/`MaskDisallowedFields` are proposed to run — so payload keys are not yet canonicalized when these new checks execute, and the checks must canonicalize independently via `UpperFirst` rather than relying on prior serialization or on `StructFieldAccess.Candidates` (which resolves the opposite direction).
