# CSR Round 10 — Server Authorization Remediation Design

**Source review:** `docs/criticalreviews/2026-10-03-iverson-critical-security-review-10.md` (reviewed at `a8c48db4`)

**Goal:** Sub-project A of the round-10 remediation, covering server-side authorization:

- **Fixed:** Findings #1 (join fields), #8 (full-row replace clears restricted fields, plus the coupled `__TenantId` defect), #10 (Update creates rows; cross-tenant oracle), #11 (no token revocation), #13 (`/v1/traces` open to any principal) and #14 (console readers skip the tenant-status check).
- **#7:** gets its interim patch only.
- **#2:** refuted, with no code change (§8).

The other round-10 findings belong to other sub-projects and are out of scope here.

**Global constraints**
- One branch, one merge. No proto change and no SDK change.
- The conformance change is server-side only: one requirement retired, one added, and the judge and its tests re-pinned.
- Security write-ups (comments, test names, the standard) stay at the level of conditions and behaviour, never step-by-step exploitation.
- `docs/` is gitignored: commit with `git add -f`.

---

## 1. Update is strictly an update (closes Finding #10)

Applies identically to `ObjectPersistenceGrpcService.Update` (`Grpc/ObjectPersistenceGrpcService.cs:94-170`) and `ObjectMappingGrpcService.Update` (`Grpc/ObjectMappingGrpcService.cs:191-260`).

**Behaviour:** Update never creates a row. A key with no row in the caller's tenant gets gRPC `NotFound` with the message `'{TypeName}:{key}' not found.` That is the same text Mapping `Get` and `Delete` already use, in-band, at `ObjectMappingGrpcService.cs:102, 120, 290, 315`. A key that exists nowhere and a key owned by another tenant are indistinguishable: the existing-row fetch is already tenant-scoped (`EntityAccess.ForTenant`), so both come back null.

**Ordering — load-bearing.** The NotFound check lives inside `AuthorizationFieldMasking.EnforceWriteAuthorization`, behind a new `bool requireExistingRow` parameter. Both Update call sites pass `true`; `Post` and Mapping `Create` pass `false`. Order of operations:

1. The smuggled-tenant-column check → InvalidArgument (unchanged, `AuthorizationFieldMasking.cs:73-80`).
2. `authEvaluator.Evaluate`; `decision.Denied` → audit `AccessDenied` + PermissionDenied (unchanged).
3. **New:** `requireExistingRow && existingRowJson is null` → `RpcException(NotFound, "'{TypeName}:{key}' not found.")`. It writes no audit entry; Mapping Get doesn't audit not-found either.

The audit action label follows the RPC, not the row: `requireExistingRow || existingRowJson is not null ? "Update" : "Create"`. Today it is `existingRowJson is null ? "Create" : "Update"`, which audits a denied Update of a missing key as "Create".
4. The existing-row branch (tenant match and immutability, force-set the tenant column, owner checks) and then §2.

Why the order matters: a caller who is denied anyway must get PermissionDenied whether or not the key exists. If NotFound came first, such a caller would get NotFound for a missing key and PermissionDenied for an existing one, which reveals existence. Probe evidence: verified assumption 13.

`EnforceWriteAuthorization` needs the type name and the key for the message. `resourceKey` is already a parameter (used for audit), and `schema.TypeName` is available.

**Removed:**
- **The `42501` catch.** Delete the `catch (PostgresException ex) when (ex.SqlState == "42501" && …)` blocks in both Update RPCs, together with their "swallowed as success" log line and `BlockedCrossTenantWrite` audit. With no create path the collision cannot occur. If it ever does, it must fail visibly, not report a fake success.
- **The create comment.** The create-branch comment in `EnforceWriteAuthorization` ("or an Update whose key doesn't exist yet and will be created by the upsert") becomes create-only.
- **The stale round-9 comments.** The comments at both fetch sites (`ObjectPersistenceGrpcService.cs:106-112`, `ObjectMappingGrpcService.cs:205-207`) that describe the fall-through to the create path and the swallowed RLS collision are rewritten to describe NotFound.

**Accepted, unchanged:**
- **Re-insert race.** A row deleted between the fetch and the upsert is re-inserted by the upsert. It is the same tenant, the same key, and a caller authorized to write it.
- **Owner mismatch.** An existing in-tenant row that the caller doesn't own still answers PermissionDenied (`OwnerMismatch`), not NotFound. That signal predates this change, and keys are server-assigned UUIDv7s.

## 2. Restricted fields are carried forward; the forced tenant column is exempt (closes Finding #8)

All changes are in `Grpc/AuthorizationFieldMasking.cs`.

**2a. Exemption (the coupled defect).**
- `RejectDisallowedFields(payload, allowedFields, exemptField)` becomes `RejectDisallowedFields(payload, allowedFields, IReadOnlyCollection<string> exemptFields)`.
- `EnforceWriteAuthorization` passes `{ decision.OwnerFieldName, decision.TenantColumn }`, minus nulls, on both the create and the update branch. This is safe because both values have already been force-set or verified by the server at that point.
- The defect being fixed: `RowFieldAuthorizationEvaluator.cs:84-91` excludes the tenant column from `AllowedFields`, so today every write by a field-restricted caller on a reserved-`__TenantId` schema fails with InvalidArgument at `AuthorizationFieldMasking.cs:160`.
- `EnforceWriteAuthorization` (`:160`) is the only production caller of `RejectDisallowedFields`. Its tests are updated to the new signature.

**2b. Carry-forward.**
- Add `CarryForwardRestrictedFields(Struct payload, Struct existingRow, IReadOnlySet<string>? allowedFields)`, called on the update branch **after** `RejectDisallowedFields` and **before** `payloadSizeValidator.ValidateTextColumnSizes`, and returns the canonical keys it inserted (empty when it is a no-op).
- `allowedFields == null` (the caller has no field restriction) → no-op.
- Otherwise, for every field of `existingRow` whose canonical name (`StructSerializer.UpperFirst`) is not in `allowedFields`, and which has no case-insensitive match among the payload's keys: copy the stored value into the payload under the stored (canonical) key.
- The stored row's keys are canonical column names: it is the `Data` JSON of `FetchByKeyAsync`, which holds every column including restricted ones.
- The tenant column is already in the payload (force-set), so it is never copied again.
- A restricted field that **is** present in the payload is still rejected by 2a; that behaviour is unchanged.
- **The owner column is carried forward too.** On the update branch, when the schema declares an `OwnerField` (`schema.Authorization.OwnerField`, so bypass callers are covered as well) and the payload has no case-insensitive match for it, the stored owner value is copied in under its canonical name, and the key joins the returned inserted-key collection (so §2c strips it from the response). Without this, an Update that omits the owner column writes the full row with a NULL owner, orphaning it from its owner. The owner column is writable, so the restricted-field rule above never reaches it. The existing `OwnerImmutable` check only compares a value that is present.
- **Effect:** the serialized payload sent to Postgres (`OutboxWriter` full-row upsert) and Kafka (the StarRocks full-row replace) carries the stored values of fields the caller may not write, so omitting them no longer clears them.

**2c. Mapping Update response masking.**
- Today `ObjectMappingGrpcService.Update` returns `Data = request.Payload` with only the tenant column stripped. After 2b that payload contains carried-forward restricted values the caller may not be allowed to read.
- After `SerializePayload` (so the Kafka payload stays complete) and before returning, mask the response the way Mapping Get does: evaluate `AuthorizationAction.Read` for the acting user and call `AuthorizationFieldMasking.MaskDisallowedFields(request.Payload, readDecision.AllowedFields)`, the same call Get makes at `ObjectMappingGrpcService.cs:126`. `MaskDisallowedFields` already removes the tenant column first, so it replaces the existing `RemoveTenantColumn` strip.
- Then remove from `request.Payload` exactly the keys carry-forward inserted. `EnforceWriteAuthorization` returns `CarryForwardRestrictedFields`' inserted-key collection (empty for create and for callers with no field restriction). Read masking alone is not enough: a caller whose read decision is Denied (a `CanWriteAll`-without-`CanReadAll` role on a type with no `OwnerField`), or ownership-required on a row it does not own, gets a null read `AllowedFields`. `MaskDisallowedFields` would then return the carried values from a row Mapping Get would refuse them (`ObjectMappingGrpcService.cs:109-124`).
- `PersistResponse` returns only the key, so Persistence Update needs no masking.

## 3. Join fields are field-authorized (closes Finding #1)

In `Iverson.StarRocks/StarRocksQueryBuilder.cs`, after `leftCol` and `rightCol` are resolved (`:881-886`):

- Each one is checked against its own type's constraint: `authz` keyed by `join.LeftType` and `join.RightType` respectively. The rule matches `IsFieldAllowed` (`:594-608`): no `authz`, no entry for the type, or `AllowedFields == null` → allowed; otherwise `AllowedFields.Contains(resolvedColumn)`.
- An unauthorized field throws `EngagementQueryTranslationException($"Field '{field}' on '{type}' referenced in join is not authorized for this caller.")`. Every search RPC already maps that exception to InvalidArgument (`ObjectSearchGrpcService.cs:110-113` and siblings).
- This covers Search, Aggregate and GroupBy, all of which build joins here.
- `StarRocksPipelineBuilder` already gates join columns through `ColumnsFor`/`RequireColumn` (`:175-186`) and is unchanged.

## 4. Expression validator interim patch (Finding #7, interim only)

In `Iverson.StarRocks/StarRocksPipelineBuilder.cs`:

**Whitelist split.** `DeriveWhitelist` (`:28-32`) is replaced by two sets, both `StringComparer.OrdinalIgnoreCase`:
- `DeriveFunctions`: `SUM, AVG, MIN, MAX, COUNT, COALESCE, NULLIF, ROUND, ABS`.
- `DeriveKeywords`: `OVER, PARTITION, BY, ORDER, ASC, DESC, AND, OR, NOT, NULL`.

**Token classification.** A new `internal static bool IsNonColumnToken(string expr, Match token)` returns true when:
- the token is in `DeriveKeywords`; or
- the token is in `DeriveFunctions` **and** the next non-whitespace character after it in `expr` is `(`.

Every other token falls through to the call site's existing column handling.

**Call sites.** All four token-scan sites replace `DeriveWhitelist.Contains(m.Value)` with `IsNonColumnToken(expr, m)`:
- `StarRocksQueryBuilder.cs:253` (Aggregate expression);
- `StarRocksQueryBuilder.cs:531` (GroupBy metric expression);
- `StarRocksPipelineBuilder.cs:223` (pipeline metric expression);
- `StarRocksPipelineBuilder.cs:396` (Derive).

**Forbidden characters.**
- `RejectForbiddenCharacters` (`:374-388`) also rejects `"` and `\`.
- Its message becomes: `"… contains a forbidden character (no semicolons, quotes (single or double), backticks, backslashes, or SQL comment sequences, including '#')."`.
- The XML doc comment is updated to match.

**Effect:** a restricted column whose name equals a function name (e.g. `Sum`), written without a following `(`, is now resolved and field-checked as a column instead of being skipped.

**Out of scope:** the expression parser rewrite (CSR #7's primary remediation) and splitting the StarRocks provisioning and query credentials (its architectural item). Both are deferred.

## 5. Token revocation (closes Finding #11)

**5a. Store** (`Iverson.Sql`)
- **Table**, raw DDL in the `EnrichmentStateRepository` style:
  ```sql
  CREATE TABLE IF NOT EXISTS iverson_token_revocations (
      sub        TEXT PRIMARY KEY,
      revoked_at TIMESTAMPTZ NOT NULL
  )
  ```
- **`ITokenRevocationRepository` / `TokenRevocationRepository(IRecordStoreQueryExecutor sql)`:**
  - `EnsureTableAsync()`;
  - `RevokeAsync(string sub)`: `INSERT … VALUES (@Sub, now()) ON CONFLICT (sub) DO UPDATE SET revoked_at = EXCLUDED.revoked_at`;
  - `ListAsync()`: returns every row as `(string Sub, DateTimeOffset RevokedAt)`.
- **Startup:** `Program.cs` calls `EnsureTableAsync()` next to the enrichment-state and re-render-queue bootstrap (`Program.cs:705-712`). It runs as the owner role through the plain executor, as those two do, so no grants are needed.
- **No pruning.** The table holds one row per removed or demoted user, and the design deliberately avoids depending on token lifetimes.

**5b. Cache** (`Iverson.Api/Tenancy`)
- `ITokenRevocationCache` / `TokenRevocationCache`, a singleton holding an immutable `sub → revoked_at` snapshot and the time it was loaded.
- `IsRevokedAsync(string sub, DateTimeOffset? issuedAt)`:
  - If the snapshot is older than 30 s (the `TenantStatusCache` TTL), reload it from `ListAsync()`. Only one reload runs at a time (`SemaphoreSlim`); concurrent callers wait for it.
  - A reload failure propagates and the request fails, matching how `TenantStatusCache` fails today.
  - Returns true when `sub` is in the snapshot and (`issuedAt` is null or `issuedAt <= revoked_at`).

**5c. Check**
- An `OnTokenValidated` handler on **both** JwtBearer schemes (`Program.cs:181-230`; the ActingUser scheme's `Events` already has `OnMessageReceived`).
- It reads `sub` and `iat` (Unix seconds) from `context.Principal`. If `IsRevokedAsync` returns true, it calls `context.Fail("Token has been revoked.")`.
- **Results:**
  - the default scheme answers 401, or gRPC Unauthenticated;
  - the ActingUser scheme is authenticated by `ActingUserInterceptor.ValidateActingUserAsync`, whose `!result.Succeeded` branch already answers Unauthenticated "Acting-user token is invalid.";
  - service-client `sub`s are never in the table and always pass.
- **Coverage:** gRPC callers, acting users, `/admin/console/*` and `/v1/traces`.
- **Why the default scheme matters:** checking only acting-user tokens would miss demotion, because the `TenantAdmin` policy reads groups from the caller's own token (`Program.cs:246-248`, `context.User`).

**5d. Writers** (`Grpc/TenantAdminGrpcService.cs`)
- **`IdpUser` gains `Uid`.** `IdpAdminClient.ListUsersByTenantAsync` (`Tenancy/IdpAdminClient.cs:135-170`) reads it from each user's `uid` property. Any other `IdpUser` construction sites are updated.
- **`RemoveUser`:** `var user = await RequireUserInTenantAsync(...)`, then `await tokenRevocations.RevokeAsync(user.Uid)`, **then** `DeactivateUserAsync`.
- **`SetTenantAdmin(grant: false)`:** revoke **before** `RemoveGroupAsync`. `grant: true` does not revoke.
- **Revoke first, deliberately.** If Authentik then fails, the user only has to log in again. The reverse order would deactivate or demote the user in Authentik while their tokens stay valid, which is the defect being fixed.
- **Tenant deactivation does not revoke.** The tenant-status checks already cover it: `ActingUserInterceptor`, `TenantAdminGrpcService.RequireActiveTenantAsync`, and §7's console filter.

**Accepted:** clock skew of a few seconds between Authentik (`iat`) and Postgres (`now()`) at the moment of revocation.

## 6. `/v1/traces` accepts console-issued tokens only (closes Finding #13)

- **Setting:** a new config value, `Authentication:ConsoleAudience`.
- **Policy:** a new `ConsoleClient` policy in `AddAuthorization` (`Program.cs:233-249`), satisfied when any `aud` claim on `context.User` equals that value (ordinal). When the value is null or empty, the policy is never satisfied (fail closed).
- **Route:** the `/v1/traces` mapping (`Program.cs:771-800`) uses `.RequireAuthorization("ConsoleClient")` instead of `.RequireAuthorization()`. The rate limiter and body limit are unchanged.
- **Wiring:**
  - compose: `Authentication__ConsoleAudience=dev-iverson-human-oidc-client-id` on the `iverson-api` service, beside `ValidAudiences__0` (`docker-compose.yml:483`). `:589` is `iverson-worker`, which never serves `/v1/traces`;
  - Helm (`charts/api/templates/deployment.yaml`): `Authentication__ConsoleAudience` from `secretKeyRef: { name: {{ .Release.Name }}-authentik-human-oidc-client, key: client-id }`, the same source as `ValidAudiences__0` (`:141-143`).
- **Effect:** service clients and acting-user tokens are refused, and every console user, administrators and ordinary tenant users alike, keeps browser tracing. Ordinary users are legitimate console users: the console requests `groups tenant_id` (`Iverson.AdminUI/src/auth/AuthProvider.tsx:56`), the landing page is deliberately unguarded (`router.tsx:24`), and `/schema` and `/data-volume` need only authentication.

## 7. Console endpoints check tenant status (closes Finding #14)

- **Filter:** `ActiveTenantEndpointFilter : IEndpointFilter` in `Iverson.Api/Console`, added to all four endpoints in `AdminConsoleEndpoints.MapAdminConsoleEndpoints` (`Console/AdminConsoleEndpoints.cs:49-78`).
- **Rule:** if `http.User` has a non-empty `tenant_id` claim and `ITenantStatusCache.GetStatusAsync(tenantId)` returns `null`, `"suspended"` or `"deleted"`, answer `403`. Otherwise call `next`.
- **Operators** have no `tenant_id` attribute (`AdminConsoleEndpoints.cs:91-93`). Their console token still carries `tenant_id: null`, because the console requests the `tenant_id` scope, and .NET reads that as an empty claim (CIR-1 of the plan, probes PK/PD). An absent or empty claim passes.
- The rule is the same one `ActingUserInterceptor.cs:42-48` applies.

## 8. Finding #2 — refuted, no change

The CSR harness called `SchemaRegistry.RegisterAsync` directly, bypassing `SchemaRegistrationOrchestrator`. That orchestrator is the only production registration path; `ObjectMappingGrpcService.cs:54` is the only non-test caller of `RegisterAsync`.

On that path:
- **Row-owned targets.** `RequireNotRowOwned` (`SchemaRegistrationOrchestrator.cs:491-500`) rejects any one-hop or block (collection) relation to a type with an `OwnerField` or `RowPermissions`; applied at `:436` and `:452`.
- **Restricted properties.** `RequireScalarProperty` (`:465-490`) rejects every referenced property that carries a FieldPermission, and the tenant column. This applies on the declaring type, the one-hop target and block-inner properties.
- **Re-checks.** Phase 2 (`:266-291`) re-validates every other registered type's template against the effective descriptors, with FailedPrecondition. A change to a target's authorization therefore cannot leave an invalid dependent template in place.
- **Renderer.** `DocumentRenderer` (`Consumers/DocumentRenderer.cs:60-90`) emits only the properties the template names.

**Scope.** `SchemaRegistry.LoadAsync` (`SchemaRegistry.cs:83-155`) reloads persisted descriptors at startup without re-running template validation, and the checks arrived after templates: templates from `e6302d16` (2026-08-20), template validation with the FieldPermission check from `d2c2cad4` (2026-08-21), the row-owned-target check only from `410cb83b` (2026-09-14). The refutation therefore also rests on no deployment holding a template descriptor persisted before `410cb83b`. The user confirmed on 2026-10-03 that no persisted descriptors exist.

The sub-project A record notes the refutation; no code changes.

## 9. Conformance (forced by §1)

All five drivers report a gRPC status code as data (`statusCode`, `Ok=true`) on the `denied_update_wrong_acting_user` step. NotFound is code 5, so IVC-IDN-007's assertion ("no gRPC error status") would fail.

- **Retire** `IVC-IDN-007` in `docs/standards/iverson-client-standard.md`:
  - set the table row's Status to `Retired`;
  - add a retirement note in the style of the existing IDN-003/004 notes: superseded because Update no longer answers a cross-tenant write with an acceptance-shaped response;
  - remove its const `IdnCrossTenantUpdateAnsweredWithoutError` from `Iverson.ClientConformance/Requirements.cs`.
- **Add** `IVC-IDN-008` | Active | Behaviour | *A mapped update attempted by an acting user of another tenant is answered with gRPC status NOT_FOUND (5), the status an update of a key that exists nowhere receives.*
  - const `IdnCrossTenantUpdateAnsweredNotFound = "IVC-IDN-008"`, with an XML doc in the style of its siblings recording that it supersedes IDN-007.
- **Judge** (`Scenarios/IdentityScenario.cs`, the enforcement assertion at ~`:470-490`):
  - the predicate becomes `deniedStep is { Ok: true } && !malformedCode && code == 5`;
  - the detail strings are updated;
  - the assertion cites `IVC-IDN-008`.
- **Tests:** `Iverson.ClientConformance.Tests/IdentityScenarioTests.cs` is re-pinned:
  - code 5 passes; null (accepted), 7 and 0 fail; a malformed code and a broken step still fail;
  - `JudgeHappy`'s default denied step (`:62`) becomes `DeniedStep(5)`;
  - the other `DeniedStep` sites are re-pinned: `:116, :146, :383, :450` (null), `:374, :642, :680` (7) and `:401` (16), along with the assertions named "answered without a gRPC error status".
- **Coverage ledger** (`iverson-client-standard.md`, IDN `#### Coverage ledger`):
  - repoint row `:567` to `| A cross-tenant update is answered with gRPC status NOT_FOUND | Covered | IVC-IDN-008 |`, the single Covered claimant of IDN-008;
  - delete row `:575` ("Cross-tenant write denial signaled on the wire"), or keep it Deferred with a rewritten reason. It must never be Covered, because a second Covered claimant fails the gate.
- **Stale statements** describing the swallowed cross-tenant write or IDN-007, all rewritten for NotFound and IDN-008:
  - the IDN prose describing the response-shape grading (`:411`, `:437-443`, `:466-472`);
  - Deferred row `:573` (a header-less caller now gets 7, a wrong-tenant caller 5, by design);
  - the negative-leg prose `:479-516`;
  - the backstop prose `:580-600` (with no seeded row the answer is now NOT_FOUND and IDN-008 passes);
  - ERR Deferred row `:1012`;
  - the `IdentityScenario.cs` class and helper docs at `:12`, `:93-145` (including the crefs at `:122`, `:141` to the removed const) and `:656`.
- **Driver comments.** The `denied_update_wrong_acting_user` comments in the five conformance drivers (.NET, Python, TypeScript, Go, Java) are rewritten to describe NOT_FOUND. These are comment-only edits.
- **No driver code or SDK change.** No SDK catches gRPC errors on mapped update; Go wraps with `%w`, and `status.FromError` in grpc-go 1.84 unwraps it (verified assumption 6).

## 10. Testing

**Mutation testing.** Task reviews must mutation-test the new guards (standing rule): each of the following must have a test that goes red when it is removed or inverted:
- the NotFound check and its position relative to the denial check;
- the tenant exemption;
- carry-forward, including the owner column;
- the audit action label on a denied Update;
- the join check;
- the `(` lookahead;
- the two new forbidden characters;
- the revocation comparison (`<=`, missing `iat`);
- the revoke-before-Authentik ordering;
- the policy;
- the filter.

**Write path** (`Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`, `ObjectMappingGrpcServiceTests.cs`, `AuthorizationFieldMasking` tests)

A scratch run with only the NotFound check (placed before authorization) failed 15 tests (19 cases). They divide into three groups:
- **Rewrite or delete** (they encode create-on-update or the swallow), per service:
  - `Update_ForOrdinaryCaller_WhenRowDoesNotExistYet_ForceSetsOwnerFieldToActingUserSub` (×2 cases);
  - `Update_WithBypassRole_WhenRowDoesNotExistYet_LeavesOwnerFieldUntouched` (×2 cases);
  - `Update_CrossTenantKeyCollidesOnUpsert_SwallowsAsSuccessAndLogsBlockedCrossTenantWrite`.

  Their owner force-set coverage moves to the create (`Post`/`Create`) tests if not already there.
- **Pass again under §1's ordering** (no edit expected; confirm), per service: `Update_WithNoActingUser_ThrowsPermissionDenied` and `Update_WithNoAuthorizationRulesConfigured_ThrowsPermissionDenied`.
- **Need an existing-row stub** (they relied on the fixture default of `FetchByKeyAsync` returning null):
  - Mapping: `Update_InsertsReconciliationQueueRowInSameTransactionAsUpsert`, `Update_WithValidKey_EmitsUpdatedEvent`, `Update_ExecutesUpsertSql_DirectlyToPostgres`;
  - Persistence: `Update_WithCarriageReturnLineFeedInKey_LogsSanitizedKeyWithoutRawNewline`, `Update_ExecutesSqlUpsert_WithPayloadJson`.

**New tests:**
- **NotFound and ordering** (both services):
  - NotFound for a missing key, with the not-found message;
  - a denied caller with no existing row gets PermissionDenied, not NotFound;
  - a smuggled tenant column with no existing row gets InvalidArgument, not NotFound;
  - no upsert or publish happens on NotFound.
- **Field masking**, using new reserved-`__TenantId` fixtures (existing fixtures in `Helpers/SchemaFixtures.cs` use only the legacy column):
  - a field-restricted caller's create and update succeed;
  - an omitted restricted field is carried forward with its stored value;
  - an Update that omits the owner column keeps the stored owner (bypass and ownership-scoped callers), and the response does not echo it;
  - a denied Update of a missing key is audited with action "Update";
  - a present restricted field is still rejected;
  - a caller with no field restriction is unchanged;
  - a camelCase payload key matches a canonical stored key, so a case variant is not carried over a present field.
- **Mapping response:** the Update response omits fields the caller may not read, while the published payload still contains them, including the two row-level cases above. For a `CanWriteAll`-without-`CanReadAll` writer on a type with no `OwnerField`, and for the same role updating a row it does not own on a type with an `OwnerField`, the response omits the carried field while the published payload carries it.
- **Postgres** (Testcontainers, `Iverson.Sql.Tests` or the existing Api integration fixture): a restricted value carried forward survives the `OutboxWriter` upsert.

**Query path** (`Iverson.StarRocks.Tests`):
- **Joins:** a restricted join field is rejected on the left and on the right; an allowed one passes; no `authz` passes.
- **Expressions**, at each of the four sites:
  - a restricted column named `Sum`, used bare, is rejected;
  - `SUM(x)`, `SUM (x)` and `SUM(x) OVER (PARTITION BY k ORDER BY x DESC)` pass;
  - `"` and `\` are rejected.

**Identity:**
- **`TokenRevocationCache`:**
  - `iat <= revoked_at` → revoked; `iat > revoked_at` → not revoked; missing `iat` with a matching `sub` → revoked; an unknown `sub` → not revoked;
  - a 30 s reload (with an injected clock or `TimeProvider`);
  - concurrent callers trigger one reload;
  - a reload failure propagates.
- **`TokenRevocationRepository`:** Testcontainers coverage of `EnsureTableAsync`, `RevokeAsync`'s upsert, and `ListAsync`.
- **`OnTokenValidated`:** a pipeline test minting a token with `iat` (the existing pipeline-test tokens carry none) whose `sub` is revoked → Unauthenticated, for both a primary-scheme call and an acting-user call.
- **`TenantAdminGrpcService`:**
  - `RemoveUser` and `SetTenantAdmin(false)` call `RevokeAsync(uid)` before the Authentik call, and nothing reaches Authentik when revocation throws;
  - `SetTenantAdmin(true)` doesn't revoke;
  - `IdpAdminClient` parses `uid`.
- **`/v1/traces`:** a non-console `aud` gets 403; the console `aud` passes; an unset `ConsoleAudience` gets 403.
- **`ActiveTenantEndpointFilter`:** null, suspended or deleted status → 403 on all four endpoints; active → pass; no `tenant_id` → pass.

**Test host.** `Helpers/AuthTestWebApplicationFactory.cs` gets `services.RemoveAll<ITokenRevocationRepository>(); services.AddSingleton<ITokenRevocationRepository, NoOpTokenRevocationRepository>();`, beside the existing `NoOpTenantRepository` (`:63-64`). `ConsoleAudience` is set in the factory configuration where pipeline tests need it.

**Live check** before merge, on an isolated compose project: a copy of `docker-compose.yml` with the `container_name:` lines stripped, `--project-directory Iverson.Server`, and the user's existing containers, volumes and networks untouched; tear down with `down -v`.
- A removed user's existing acting-user token is refused within 30 s, and a fresh login after re-activation, if any, is accepted.
- The identity conformance scenario passes IVC-IDN-008 for at least the .NET driver.
- A field-restricted caller's Update that omits a write-restricted `DATETIME` property and a write-restricted `BYTES` property leaves both stored values intact in Postgres, and the `DATETIME` value intact in the StarRocks row (read back through Search) and in the Qdrant point payload. `BYTES` is checked in Postgres only: an SDK client sends bytes as base64 text, the text projected to StarRocks differs between a fast-path write and a carried update, and Search cannot display BYTES. That pre-existing encoding split is recorded as a known issue (plan CIR-1 §3.1, user's pick C).

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | A token's `sub` equals the Authentik user's `uid`; tokens carry a numeric `iat` | Live: isolated compose Authentik, user created, password set via recovery, token minted with the real `AuthentikFlowExecutorClient` through `iverson-loadtest-human`. Decoded `sub` = `uid` (`SUB == UID: True`); `iat` present, JSON number. Claim names: `iss,sub,aud,exp,iat,auth_time,acr,amr,sid,jti,groups,tenant_id,azp,uid,scope` |
| 2 | Authentik's user **list** endpoint (the one `ListUsersByTenantAsync` pages through) returns `uid` | Same probe: `GET /api/v3/core/users/?username=…` result row carries `uid`, equal to the detail endpoint's |
| 3 | `RequireUserInTenantAsync` already holds the user record, so `Uid` costs no extra Authentik call | `TenantAdminGrpcService.cs:91-94` returns an `IdpUser` from `ListUsersByTenantAsync`; `IdpAdminClient.cs:163-166` builds it from the list JSON |
| 4 | Allow-listed keywords can't be referenced as unquoted columns in StarRocks; function names can | Live `starrocks/allin1-ubuntu:4.1.1`, a table with backticked columns named after every whitelist word. `avg(sum)`, `avg(abs)` and `avg(coalesce)` resolve to the columns (the gap). `over, partition, by, order, asc, desc, and, or, not` are syntax errors (1064). `avg(null)` returns NULL, a literal, not the column |
| 5 | StarRocks accepts a space between function name and `(`; `"…"` is a string literal | Same container: `select SUM (x)` → 2; `SUM(x) OVER (PARTITION BY id ORDER BY x DESC)` → 2; `coalesce(x, "a")` → 2 |
| 6 | Every SDK passes gRPC NotFound on mapped update through as a gRPC error its driver classifies (`Ok=true`, `statusCode`) | Read all five: .NET/Python/TS/Java raise the RpcError unwrapped (no `catch` of gRPC errors around update); Go wraps with `%w` (`coordinator.go:299`), and grpc-go is `v1.84.0` (`go.mod:6`), whose `status.FromError` unwraps. Each driver's denied step reports `statusCode` on a gRPC error (`driver.py`, `driver.ts`, `main.go`, `Driver.java`, `Program.cs`) |
| 7 | `AllowedFields` never contains the tenant column (the coupled defect is real), and is null when no field is excluded | `RowFieldAuthorizationEvaluator.cs:82-116`: `allFields` excludes `IsTenantColumn`; `allowedFields` assigned only when `excluded.Count > 0` |
| 8 | The stored row JSON holds every column, restricted ones included, under canonical names | `FetchByKeyAsync` returns the row's `Data` JSON; Mapping Get parses that same JSON and only **then** masks (`ObjectMappingGrpcService.cs:96-126`) |
| 9 | `OnTokenValidated` fires for `AuthenticateAsync("ActingUser")` called by `ActingUserInterceptor`, and `context.Fail` yields Unauthenticated | Scratch worktree: added an `OnTokenValidated` to the ActingUser scheme. `ActingUserInterceptorSuspensionTests` 4/4 pass with it passive; with it failing, all 4 get `StatusCode.Unauthenticated`. The hook logged `sub=test-human-sub iat=` (pipeline-test tokens carry no `iat`) |
| 10 | A raw-DDL platform table works under Helm's Postgres roles without grants | Role switching (`SET LOCAL ROLE`) happens only in `PostgresRepository.cs:144-148` and `EntityRepository` tenant paths; the plain `IRecordStoreQueryExecutor` runs as the owner `iverson`, which is how `iverson_enrichment_state` (`EnrichmentStateRepository.cs:5-16`) works on Helm today |
| 11 | Console tokens carry `aud` = the console client id, as a single string | Live probe token: `aud = "dev-iverson-loadtest-human-client-id"` (single string, = client id; `azp` likewise). Same Authentik OAuth2 provider implementation as `iverson-oidc-default`. The console client is `ValidAudiences__0` in compose (`:483, :589`) and Helm (`deployment.yaml:141-143`) |
| 12 | Nothing depends on Update creating a row | `Create` refuses client keys (`EntityKeyAccessor.cs:39-45`). The drivers' updates target rows written earlier in the same run (`Program.cs:943, 1213` and siblings). LoadTest and `Iverson.Agents` make no Update calls (grep) |
| 13 | The set of server tests affected by NotFound is known | Scratch worktree with NotFound-before-authorization in both RPCs: `Iverson.Api.Tests` 1299 pass / 19 fail (15 tests), listed in §10 |
| 14 | `DeriveWhitelist` is referenced only at the four scan sites | `command grep -rn DeriveWhitelist` over `Iverson.Server`: the two builders plus one comment in `StarRocksQueryBuilderTests.cs:1981` |
| 15 | No legitimate expression uses `"` or `\` | grep of expression literals across `Iverson.Server`, `Iverson.Clients` and `Iverson.Agents`: none; the agent builds no expressions |
| 16 | `EngagementQueryTranslationException` maps to InvalidArgument | `ObjectSearchGrpcService.cs:110-113` (and `:784, :832, :910`, `MatchPattern.cs:163`) |
| 17 | Mapping Get masks its response with the Read decision, so Update can do the same | `ObjectMappingGrpcService.cs:126` |
| 18 | Pipeline-test hosts can substitute the revocation repository | `AuthTestWebApplicationFactory.cs:48-66` substitutes `ITenantRepository`, `IEnrichmentStateRepository` and others with no-ops; every pipeline factory derives from it |
| 19 | Operators carry no `tenant_id`; tenant status values are `active/suspended/deleted`, null when unknown | `AdminConsoleEndpoints.cs:91-93` ("an operator, having no `tenant_id` claim"); `TenantStatusCache.cs`; `ActingUserInterceptor.cs:44-46`. Their console token carries `tenant_id: null`, read by .NET as an empty claim (plan CIR-1 PK/PD) |
| 20 | No deployment holds a template descriptor persisted before `410cb83b` (the date the row-owned-target template check arrived) | User confirmation, 2026-10-03: no persisted descriptors exist. `SchemaRegistry.LoadAsync` (`SchemaRegistry.cs:83-155`) does not re-validate templates, so §8's refutation depends on this |
| 21 | Carried-forward values survive the `row_to_json` → payload → `json_populate_record` upsert round trip for every column type | CDR-1's P-RT: PGlite (PostgreSQL 18.3) table with one column per `SchemaBuilder` SQL type, scalar and array (`:389-420`). `OutboxWriter`'s exact upsert, then a `row_to_json` dump, then a JSON hop standing in for Struct/`SerializePayload`, then the upsert again: byte-identical, all 18 columns. Caveats: not `postgres:16`, and the .NET hop was not run. §10's Testcontainers carry-forward test (real `OutboxWriter`, `postgres:16-alpine`) closes both |
| 22 | Every Authentik provider uses the default subject mode, so VA-1's `sub == uid` holds for the console and acting-user providers too | `grep -rn sub_mode` over yaml/yml/py/json/tpl/sh: 0 hits |
| 23 | Revocation reaches every caller of the ActingUser scheme, not only `ActingUserInterceptor` | `grep -rn '"ActingUser"'` (non-test): the scheme at `Program.cs:206`; callers `ActingUserInterceptor.cs:40` and `Program.cs:600, 620, 643` (`/admin/reconcile`, `/admin/dlq`, `/admin/dlq/{id}/replay`). All four map `!Succeeded` to Unauthenticated or 401 |

## Known issues / accepted as out of scope

- **Finding #7's primary and architectural remediations** (the expression parser, and splitting the StarRocks credential): deferred by scope choice; §4 is the interim patch only.
- **Update's re-insert race** (§1): a concurrent delete between the fetch and the upsert re-inserts the row in the same tenant.
- **Owner-mismatch PermissionDenied** (§1): it tells a caller a key exists in their own tenant. This predates the change; keys are UUIDv7.
- **No audit entry for cross-tenant Update attempts**: they are now indistinguishable from missing keys, by design.
- **Revocation clock skew** (§5): a few seconds between Authentik's `iat` and Postgres `now()`.
- **Console users can still write spans** (§6): any console user's browser can post spans. A collector that stamps the caller and overwrites `service.name` (CSR #13's architectural item) is deferred.
- **Other round-10 findings** (#3–#6, #9, #12, #15–#24): belong to the other sub-projects.
