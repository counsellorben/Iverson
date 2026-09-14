# CSR Round 4 Remediation — Design

Source: `docs/criticalreviews/2026-09-13-iverson-critical-security-review-4.md` (15 findings, SQS 0/100 hard-gated).

## Goal

Close all 15 findings from CSR round 4. The three findings sharing a common shape — a security control added once, on one call site, with nothing catching a structurally-equivalent sibling missing it (#3, #4, #9) — get fixed at their natural architectural boundary, each with a small regression test acting as a safety net against a future 4th instance, rather than patched as isolated one-offs. This is a design spec: what to build. Sequencing/batching for implementation is a separate, later step.

---

## 1. Finding #1 — Document templates may not traverse a relation to a row-owned type

Reject a document template that traverses a relation to a type declaring `Authorization.OwnerField` or non-empty `RowPermissions`. The check must run on the relation-target descriptor only, not inside the shared `SchemaRegistrationOrchestrator.RequireScalarProperty` helper (`Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:453-478`) — that helper is also called with the *declaring* type for a plain `{Prop}` segment (`:412`), where no relation is traversed at all, so a rejection placed inside it would refuse a type's own document template for declaring row-level authorization on itself, with an error message describing a traversal that never happened.

Instead, add the check in `RegisterEntitySegment`'s two relation-target branches, immediately after `RequireTargetDescriptor` returns `target` and before the existing `RequireScalarProperty(target, ...)` call — once in the one-hop branch (`:426-427`), once in the block branch (`:442-445`):

```csharp
if (target.Authorization?.OwnerField is not null || target.Authorization?.RowPermissions.Count > 0)
{
    throw new RpcException(new Status(statusCode,
        $"Document template on '{declaring.TypeName}' traverses relation '{segment.RelationName}' to " +
        $"'{target.TypeName}', which declares row-level authorization (OwnerField/RowPermissions); a " +
        "document template cannot traverse a relation to a row-owned type."));
}
```

This is a registration-time `InvalidArgument`/`FailedPrecondition` (matching the existing `statusCode` parameter's two call sites), not a runtime check. No existing registered schema or test fixture combines a document template with a relation to a row-owned type (verified), so this adds no breaking change to anything currently registered — it only forecloses the combination going forward. A type's own top-level `{Prop}` segments on its own row-owned fields are unaffected, since no relation is traversed there.

---

## 2. Finding #2 — SDK token-endpoint scheme validation, all 5 SDKs

Each SDK gains a check: if the token endpoint's scheme is not `https`, require the same insecure-opt-in flag the existing gRPC-channel guard already uses — reusing the existing concept, not adding a second flag. For Python, .NET, and Go, the token endpoint and the opt-in flag are already co-located on the same object/method; for TypeScript and Java, the endpoint lives behind an opaque `CallCredentials` type that `IversonClient`/`core.ts` never unwraps, so the check has to live one level lower, in the factory/constructor that actually holds the endpoint.

| SDK | Existing flag (reused) | New check location |
|---|---|---|
| Python | `allow_insecure_credentials` | `iverson_client/core.py`'s `IversonClient.__init__` (same method already checking `use_tls`/`credentials`, per `core.py:836-861`) |
| .NET | `allowInsecureChannelCallCredentials` | `ServiceCollectionExtensions.AddIversonClient` |
| Go | the existing `RequireTransportSecurity()`-backed opt-in | `iverson/auth.go` |
| TypeScript | a new `allowInsecureCredentials?: boolean` parameter | `createOAuth2ClientCredentials(clientId, clientSecret, tokenEndpoint, scope?)` in `src/auth.ts:16-20` — not `core.ts`'s constructor, which only ever receives the opaque `grpc.CallCredentials` this factory returns, never the endpoint itself |
| Java | a new `boolean allowInsecureCredentials` parameter | `OAuth2ClientCredentials`'s two constructors in `OAuth2ClientCredentials.java` — not `IversonClient.java`, which only ever receives the base `io.grpc.CallCredentials` type, never the concrete class whose `tokenEndpoint` field is private with no accessor |

Since `OAuth2ClientCredentials`/`createOAuth2ClientCredentials` are constructed independently of `IversonClient`/`core.ts` (confirmed: both are caller-facing factories, not SDK-internal — the only production construction sites are application code, e.g. `Iverson.Clients/Java/sample/.../Main.java`), a caller using a plaintext token endpoint now supplies the opt-in flag at *both* construction points — once to the credentials factory, once to the client's own `plaintext(...)`/insecure-channel call. This is a disclosed consequence of the two objects being independently constructed, not a hidden one.

Go and TypeScript additionally set an explicit no-follow (or same-origin-only) redirect policy on the token-request HTTP client (`http.Client{CheckRedirect: ...}` in Go; `fetch(..., {redirect: 'error'})` or equivalent in TypeScript), closing the redirect-replay amplifier the CSR report flagged as inferred-from-documented-defaults.

---

## 3. Findings #3 / #4 / #9 — the recurring-pattern fixes

### 3.1 Finding #3 — `ObjectMapping.Post`/`Update` get the payload-size guard `ObjectPersistence` already has

`AuthorizationFieldMasking.EnforceWriteAuthorization` (`Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:28+`) gains an `IPayloadSizeValidator` parameter and calls `ValidateTextColumnSizes(payload, schema)` internally, immediately after the existing `if (decision.Denied) { ...; throw ...; }` block (`:68-73`) — preserving today's authorization-first precedence exactly, so a denied caller sending an oversized payload still gets `PermissionDenied`, not `InvalidArgument`.

`ObjectMappingGrpcService`'s primary constructor gains `IPayloadSizeValidator payloadSizeValidator` as a new dependency (already registered in DI at `Program.cs:233`, so no new registration is needed) — its two write RPCs (`:303`, `:363`) need this to satisfy the widened `EnforceWriteAuthorization` signature, the same way `ObjectPersistenceGrpcService`'s constructor already does. Both services' four write-RPC methods already call this one shared function to be a valid write path at all — folding the check in here means a third future write RPC gets it automatically, by the same requirement that makes it a valid write path, not by remembering a second, separate injection.

`ObjectPersistenceGrpcService`'s now-redundant direct calls to `ValidateTextColumnSizes` (`:46`, `:122`) are removed — the shared helper is the single call site.

**Verified, accepted trade-off:** this moves the size check earlier in `ObjectPersistenceGrpcService.Post`/`Update`'s sequence — currently `EnforceWriteAuthorization` (denial check included) → `ValidateAndNormalizeRelations` → `ValidateTextColumnSizes`; after this change, the size check runs as part of `EnforceWriteAuthorization`, immediately after its denial check and before relation validation. A request that is both malformed in its relations and oversized in a text column now gets an oversized-column error rather than a relation error. A denied caller's precedence is unchanged — the denial check still runs first. Accepted and documented here rather than blocking, matching this session's own precedent for an analogous disclosed reordering (CSR round 3's `MaxRelationDepth`-before-`RequireSchema` change in `ObjectMappingGrpcService.Get`).

### 3.2 Finding #4 — DLQ admin surface gets tenant-scoped

- `DlqSchema.Table` (`Iverson.Server/Iverson.Api/Reconciliation/DlqSchema.cs`) gains a nullable `TenantId` column (`new("TenantId", "text", true)`), applied automatically on next restart via `ApplySchemaAsync`'s existing idempotent `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` mechanism — no new infrastructure, no manual migration step. `Iverson.Sql.Tests/DlqRepositoryPostgresIntegrationTests.cs`'s mirrored `TableSchema` literal (which the test uses to `DROP`/recreate the table before driving a real `DlqRepository.InsertAsync`) gains the same column, or its `InsertAsync` call would fail against a stale schema.
- `DlqRow`/`DlqMessage` (`Iverson.Server/Iverson.Sql/DlqRepository.cs`) gain the corresponding `TenantId` property; `DlqRepository`'s `SELECT`/`INSERT` column lists are updated to match. `DlqReplayRow` and `GetUnreplayedByIdAsync`'s `SELECT` list gain the same property/column — without it, the replay endpoint (the more consequential of the two, since it re-injects a message into Kafka) has nothing to compare against the caller's tenant claim and stays unscoped even after this fix.
- `DlqMonitorConsumer` (`Iverson.Server/Iverson.Api/Reconciliation/DlqMonitorConsumer.cs`) gains `SchemaRegistry registry` as a new constructor dependency (already a DI singleton at `Program.cs:230`, so no new registration is needed; the 3 existing test constructions in `DlqMonitorConsumerTests.cs` need the new argument). `HandleAsync` derives `TenantId` at insert time via two independent mechanisms, not one shared exception path: (1) deserializing `value` as `EntityEvent` and parsing `event.PayloadJson` via `JsonDocument.Parse` run inside a `try` whose `catch (JsonException)` yields `TenantId = null` on genuinely malformed JSON; (2) separately, `registry.Get(event.TypeName)?.TenantColumn` is checked for `null` with an ordinary null check — since `SchemaRegistry.Get` returns `SchemaDescriptor?` and resolves to `null` (not a throw) for an unregistered type, an unresolvable schema or tenant column skips the `ExtractString` call entirely and sets `TenantId = null` directly, without relying on any exception path. Both paths converge on "the row is still recorded, just without a tenant to scope by." `TenantId` is extracted from the parsed payload via a small private `ExtractString` helper — mirroring the exact pattern `EnrichmentConsumer.cs:374` and `PopularitySignalConsumer.cs:279` already each carry their own private copy of (this consumer gets a third copy, matching the established convention rather than introducing a new shared utility); `ExtractString`'s established shape takes a non-nullable `propertyName`, which is exactly why the schema/tenant-column lookup must be null-checked before the call rather than folded into the same `catch`. This guard is load-bearing, not defense-in-depth — the DLQ's raw value is exactly the class of message every other consumer's `PoisonMessageException` handling exists to dead-letter, so an unguarded parse here would throw on precisely the messages the DLQ exists to capture, and (since `ConsumeRawAsync` commits the offset only after a successful handle) hot-loop on that message every 10 seconds without ever recording it.
- `/admin/dlq` and `/admin/dlq/{id}/replay` (`Iverson.Server/Iverson.Api/Program.cs:399-430`) require an acting-user token in addition to the existing `Operator` policy. The endpoint delegates call `httpContext.AuthenticateAsync("ActingUser")` directly — the same general ASP.NET Core mechanism `ActingUserInterceptor.ValidateActingUserAsync` (`Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs`) already uses for gRPC calls, confirmed to work identically for a plain HTTP request since gRPC-over-HTTP/2 and Minimal API routes share the same underlying `HttpContext`. `IActingUserAccessor` is NOT used here — it's populated only by the gRPC-specific interceptor pipeline, which doesn't run for Minimal API routes. Results are filtered to the caller's own `tenant_id` claim. For a row with a null `TenantId`: the route's own `Operator` policy check reads the *service* principal and is already required to reach the endpoint at all, so it cannot itself gate anything further — instead, visibility requires the **acting-user** principal's own `groups`/`scope` to independently satisfy `OperatorAuthorizationPolicy.IsSatisfiedBy` (a second, explicit call against the acting-user claims, distinct from the route-level check), preserving cross-tenant visibility for the genuinely-untenanted plumbing case without making it the default for every row.
- `ExceptionMessage`/`ExceptionType` are left as-is on this surface (raw) — Finding #7's fix closes the adjacent info-leak vector separately; this surface inherits that fix rather than duplicating it.

### 3.3 Finding #9 — LLM output write-back gets the same size check, at the layer that actually has the schema

`EnrichmentConsumer` (`Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs`) takes `IPayloadSizeValidator` as a new dependency. Immediately after `GenerateAsync` returns `columns` (and after the existing zero-columns branch, `:167-178`), it builds a minimal `Struct` from that dictionary and calls the existing `ValidateTextColumnSizes(Struct, SchemaDescriptor)` — reusing the exact validator `ObjectPersistenceGrpcService`/`ObjectMappingGrpcService` call, not a new method or duplicated size-limit logic. This is the correct layer for this check: `EntityRepository.UpdateColumnsAsync` (`Iverson.Sql`) was originally considered but does not have — and structurally should not need — `SchemaDescriptor`/`LargeFieldColumns`/`StoreTargeting` information; `ValidateTextColumnSizes` itself skips validation entirely for types not projected to StarRocks at all, a decision only `Iverson.Api`-layer code (where `EnrichmentConsumer` lives) has the context to make.

**The check's own failure is caught locally, not by the enclosing best-effort `try`.** The call site sits inside `EnrichmentConsumer`'s outer `try` (`:157+`), whose `catch (Exception ex)` (`:227-233`) is explicitly documented as best-effort ("nothing below throws `PoisonMessageException`") and — critically — records no state row, so the next event for the same object would retry. Since generation runs at `temperature = 0` (`EnrichmentService.cs:55`), an oversized value would regenerate identically forever. Instead, the size check's `RpcException` is caught at the check site and handled the same way the zero-columns branch two lines above already handles "nothing to write back": record a state row via `state.UpsertAsync(tx, tenantValue, schema.TypeName, ev.Key, hash, DateTimeOffset.UtcNow)` (`:176-178`'s existing pattern) so the same source text and specification are not retried, log it, and return without calling `UpdateColumnsAsync`.

Currently bounded by `EnrichmentService.MaxGeneratedTokens = 256`, which cannot approach StarRocks' 65,533-byte column ceiling — this fix closes the gap structurally rather than leaving it dependent on that configuration value never changing.

### 3.4 Three regression tests (the safety net)

1. A test asserting `EnforceWriteAuthorization` rejects an oversized payload, reachable from both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService`'s write paths via the one shared call site.
2. A test asserting `/admin/dlq` and `/admin/dlq/{id}/replay` return `Unauthenticated`/`PermissionDenied` for a service-only token (no acting-user), and correctly filter by tenant for one that has it.
3. A test asserting `EnrichmentConsumer`'s write-back path, on an oversized generated value, suppresses the write-back, records a state row, and never calls `UpdateColumnsAsync` — not "the same way the gRPC write path does," since that path surfaces `InvalidArgument` to the caller while this one is a silent, best-effort skip by design.

---

## 4. Finding #5 — Delimit the untrusted slot in every enrichment prompt template

`EnrichmentPrompts.cs`'s four templates (`Summary`, `Keywords`, `Extraction`, `ChunkContext`) each wrap their untrusted `{0}`/`{1}` slot(s) in explicit markers (e.g. `<<<BEGIN_SOURCE_TEXT>>> {0} <<<END_SOURCE_TEXT>>>`), with instruction text updated to tell the model everything between the markers is data, never instructions. The `Extraction` template's per-target hint (`EnrichmentConsumer.cs:308-310`) moves *before* the untrusted text rather than being appended after it. Verified: `EnrichmentService.GenerateInternalAsync` (`Iverson.Server/Iverson.Embeddings/EnrichmentService.cs:36-56`) passes the prompt through verbatim as a single `user`-role message with no wrapping that would interact with the new markers — the backend's chat-completions API does support a separate `system` role, but the current code doesn't use one; introducing that split is a larger change than this finding requires and is not part of this design.

---

## 5. Finding #6 — Dependency-vulnerability scanning, all 6 ecosystems

One new required CI check per ecosystem, each failing the build on a new High/Critical finding:

| Ecosystem | Check |
|---|---|
| NuGet | `dotnet list package --vulnerable` across the solution |
| npm (AdminUI) | `npm audit --audit-level=high` |
| npm (TypeScript SDK) | `npm audit --audit-level=high` |
| Go | `govulncheck ./...` |
| Java | an OWASP dependency-check Maven plugin bound to the `verify` phase |
| Python (SDK) | `pip-audit` |
| Python (Agents) | `pip-audit` |

All seven jobs are added to **both** `.github/workflows/` and `.gitlab-ci.yml`, in parity with the repo's existing four deploy-validation jobs, which already exist in both systems with matching tool versions and SHA-256 pins — no shared-definition mechanism exists between the two (no cross-file `include:`/`extends:`, no local composite actions), so parity means fourteen job definitions, not seven, matching the established convention rather than introducing the pipelines' first asymmetry.

Unlike the existing deploy-validation jobs — which are path-filtered to `Iverson.Server/deploy/**` and would never fire on a dependency-manifest change (a `.csproj` bump, a `package-lock.json`, `go.mod`, `pom.xml`, `requirements.txt` touches none of that path) — these seven run unfiltered by path on every pull/merge request, since a new advisory against an already-pinned, unchanged version is the common case a periodic re-scan catches and a PR-only trigger cannot. The periodic re-scan itself is **not symmetric across the two CI systems**: the GitHub-side jobs gain a native, self-sufficient `schedule: cron: '30 3 * * 0'` trigger in their workflow file, mirroring `.github/workflows/codeql.yml:7-9`'s existing unfiltered-push/PR-plus-cron shape — a construct that lives entirely inside the tracked YAML. GitLab CI has no file-only equivalent (confirmed: `.gitlab-ci.yml` contains no `schedule`/`CI_PIPELINE_SOURCE == "schedule"` content today); a periodic GitLab pipeline requires a project-level Pipeline Schedule resource, created out-of-band via the GitLab UI (Project → CI/CD → Pipeline Schedules), the Pipeline Schedules API, or an IaC resource such as `gitlab_pipeline_schedule` if Terraform-managed GitLab configuration is otherwise in scope for this repo — paired with a `rules: - if: '$CI_PIPELINE_SOURCE == "schedule"'` clause added to the seven GitLab jobs. Provisioning that Pipeline Schedule is a follow-up action outside this design's file-only scope; the seven GitLab jobs, as specified here, run on every merge request but do not gain the periodic re-scan until that follow-up is done. Per-ecosystem path filters (e.g. gating the Go scan on `Iverson.Clients/Go/**` + `go.sum`) are a legitimate refinement left to implementation.

A `.github/dependabot.yml` is added alongside these for automated update PRs going forward.

---

## 6. Findings #7, #10, #11, #12, #13, #14, #15 — the remaining Low findings

**#7 — Fixed error messages instead of `ex.Message` in `RpcException`s.** Every flagged `catch` block in `ObjectSearchGrpcService.cs` (`:240-244`, `:564-568`) and `SchemaRegistrationOrchestrator.cs:143` logs the real exception server-side and returns a fixed, generic message to the caller (e.g., `"Embedding service unavailable."`), correlatable via the existing `X-Trace-Id` response header.

**#10 — `DocumentRerenderConsumer`'s `OneToMany` arm gets tenant-scoped.** Unlike its two siblings, this arm's foreign key plays an inverted role: the FK column lives on the *changed* (child) row and its value *is* the parent key to enqueue, not a column to search declaring rows by — so mirroring the siblings' `FetchByColumnAsync` pattern doesn't apply here. Instead, `EnqueueOneToManyParentsAsync` (`:179-194`) changes its `declaringTypeName: string` parameter to `declaringSchema: SchemaDescriptor` (already in scope at the call site, `:88`), and before each `queue.EnqueueEntityAsync` call (both the new-parent and old-parent branches — the arm enqueues both on FK reassignment), verifies the parent row exists under the acting tenant via `entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(declaringSchema), parentKey, EntityAccess.ForTenant(tenantId))` (the same call `EnrichmentConsumer.cs:200` already makes) — a null result skips that enqueue, so a cross-tenant foreign key now fails closed instead of silently enqueueing.

**#11 — Sanitize `AuditLog`'s actor claims.** `.SanitizeForLog()` on the `sub`/`tenant_id` reads in `Denied` (`AuditLog.cs:13-17`), and on `sub` in `AdminOperation` (`:19-25`) — matching the treatment already given to the resource arguments beside them.

**#12 — Digest-pin the remaining 4 Dockerfiles** (`Iverson.Server/Iverson.Events/Dockerfile`, `Iverson.Server/Iverson.Vector/Dockerfile`, `Iverson.Server/Iverson.Sql/Dockerfile`, `Iverson.Server/Iverson.Launcher/Dockerfile`), same `@sha256:...` treatment already applied to `Iverson.Api`/`Iverson.AdminUI`'s Dockerfiles.

**#13 — `SchemaRegistry.LoadAsync` re-applies identifier/key-type validation on rehydration.** Skipping (not crashing on) a non-conforming row the same way a malformed-JSON row is already skipped (`SchemaRegistry.cs:118-151`).

**#14 — `PopularitySignalConsumer` gets the same poison-message guard its siblings use.** Wrap its 3 unguarded `JsonDocument.Parse` calls (`:201`, `:207`, `:247`) in the same `try/catch → PoisonMessageException` pattern `IntelligenceStoreConsumer.cs:100-109,535-544` and `EngagementStoreConsumer.cs:109-118` already use.

**#15 — Typed `InvalidArgument` instead of unhandled exceptions.** `ObjectRetrievalGrpcService.GetMany` validates `request.Keys` are well-formed GUIDs before calling `EntityRepository.cs:16`'s `keys.Select(Guid.Parse)`. `StructSerializer`'s (`ProtoPayloadHelper.cs`) case-folding step (`ToDictionary(kv => UpperFirst(kv.Key), ...)`) appears twice — once for the top-level payload (`:10`), once for nested `StructValue` fields (`:22-23`) — and both are extracted into one shared fold-with-collision-check helper, so either a top-level or a nested key collision raises a typed `RpcException` naming the colliding keys, instead of letting `ToDictionary` throw `ArgumentException`.

All seven are small, independent, single-file-or-two changes with no forks.

---

## 7. Finding #8 — the hard-gate closure

**Generate the 7 credentials, don't hardcode them.** A helper script generates a `.env` file, written to `Iverson.Server/` (the Docker Compose project directory — the directory holding `docker-compose.yml` — so Compose auto-loads it), on first run with random values (`openssl rand -hex 32` or equivalent) for the three OAuth2 `client_secret`s, three passwords, and the orchestrator API token currently hardcoded in `compose-only/service-clients.yaml` (lines 85, 107, 128, 276, 291, 358, 366). `.env` is added to `.gitignore`'s existing "Secrets / local config" block (`:53-59`) — it is not currently ignored by any existing rule, so without this addition the generated file would sit in the repo as an untracked-but-visible file full of live credentials, the exact failure mode this fix exists to remove.

**Blueprint switches to Authentik's `!Env` tag.** The seven literal values become `!Env VAR_NAME` references. `!Env` has no default-value form — an unset variable is a hard failure at blueprint-import time — so `docker-compose.yml` must pass all seven as environment variables to the Authentik server/worker containers that load this blueprint, sourced from the generated `.env`.

**`AuthentikContainerFixture` gets matching env vars.** `BuildAuthentikContainer` (`Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikContainerFixture.cs:214+`) currently sets a small, fixed env-var list with no substitution layer (bind-mounts the raw blueprints directory directly) — confirmed still the current shape. It needs the same seven environment variables set, generated fresh per fixture instance rather than reading the compose `.env` (the fixture and the compose stack are independent environments). The `OrchestratorToken` constant (`:108`, still present as a hardcoded literal, used at `:340`) is replaced with a value read from the fixture's own generated env var.

This closes the hard gate outright — no static credential remains in source after this change.

---

## Verified assumptions

The following were verified against the current codebase (not taken on faith) before this design was finalized:

| Assumption | Evidence |
|---|---|
| `RegisterEntitySegment`'s one-hop and block-inner branches have `target.Authorization` (with `OwnerField`/`RowPermissions`) in scope immediately after `RequireTargetDescriptor` returns, before the existing `RequireScalarProperty(target, ...)` call | `SchemaRegistrationOrchestrator.cs:426-427,442-445`; `AuthorizationRules.OwnerField`/`RowPermissions` confirmed in `SchemaDescriptor.cs:139-141` |
| No registered/test schema combines a document template with a row-owned related type | grep across `SchemaBuilderTests.cs` and `DocumentTemplateValidationTests.cs` — the two concepts never co-occur on the same descriptor |
| Python/.NET/Go have the insecure-opt-in flag and token endpoint co-located in the same constructor/method scope; TypeScript and Java do not — the endpoint is reachable only inside `createOAuth2ClientCredentials`/`OAuth2ClientCredentials`'s own constructors | Python: `core.py:836-861`. .NET: `ServiceCollectionExtensions.cs:41,44` + `IversonClientCredentials.cs:16`. Go: `auth.go:33,49`. TypeScript: `auth.ts:16-20,40`, confirmed absent from `core.ts`'s constructor. Java: `OAuth2ClientCredentials.java` — `tokenEndpoint` is `private final` with no accessor; `IversonClient.java` only ever takes the base `io.grpc.CallCredentials` type |
| `EnforceWriteAuthorization` is called by all 4 write-RPC methods | `ObjectMappingGrpcService.cs:303,363`; `ObjectPersistenceGrpcService.cs:35,111` |
| `ValidateTextColumnSizes(Struct, SchemaDescriptor)`'s params are already in scope inside `EnforceWriteAuthorization` | `AuthorizationFieldMasking.cs:28-35` signature already takes both |
| `ObjectMappingGrpcService`'s primary constructor can accept a new `IPayloadSizeValidator` parameter, satisfied by DI without new registration | `Program.cs:233` already registers `AddSingleton<IPayloadSizeValidator, PayloadSizeValidator>()`; 11 existing `new ObjectMappingGrpcService(...)` test sites need the new argument |
| Removing `ObjectPersistenceGrpcService`'s direct calls shifts validation order relative to relation validation (not just removes duplication), but NOT relative to the authorization denial — inserting the check immediately after `if (decision.Denied)` (`AuthorizationFieldMasking.cs:68-73`) preserves today's denial-first precedence exactly | `ObjectPersistenceGrpcService.cs:30-46` — current order is authz (denial included) → relation-validate → size-check; the corrected insertion point sits inside the authz step, after its own denial check |
| The acting-user mechanism is reachable from an HTTP Minimal API route, not just gRPC | `ActingUserInterceptor.cs`'s `ValidateActingUserAsync` calls `httpContext.AuthenticateAsync("ActingUser")`, a general ASP.NET Core `HttpContext` extension — confirmed this doesn't depend on the gRPC interceptor pipeline |
| `OperatorAuthorizationPolicy.IsSatisfiedBy` takes plain claim collections, not a `ClaimsPrincipal`, so it can be evaluated a second time against the acting-user principal's own claims (distinct from the route-level check against the service principal) | `OperatorAuthorizationPolicy.IsSatisfiedBy(IEnumerable<string>, string?)` signature; `ActingUserInterceptor.cs:39-43` confirms the acting-user principal is a separate `ClaimsPrincipal` from `httpContext.User` |
| The DLQ table's schema supports adding a nullable column without new migration infrastructure | `PostgresSchemaManager.cs:118` runs `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` for every declared column on every startup |
| `DlqMonitorConsumer` can accept a new `SchemaRegistry` dependency, satisfied by DI without new registration | `Program.cs:230` already registers `AddSingleton<SchemaRegistry>()`; 3 existing test constructions in `DlqMonitorConsumerTests.cs` need the new argument |
| `DlqReplayRow`/`GetUnreplayedByIdAsync` and the `Iverson.Sql.Tests` mirrored `TableSchema` literal are the two additional sites the `TenantId` column must reach, beyond `DlqRow`/`DlqMessage`/the list-endpoint SELECT | `DlqRow.cs:6`; `DlqRepository.cs:42-49`; `DlqRepositoryPostgresIntegrationTests.cs:62-77` |
| A reusable tenant-extraction helper exists for `DlqMonitorConsumer` to call | refuted — `EnrichmentConsumer.cs:374`/`PopularitySignalConsumer.cs:279` each carry their own private copy; `DlqMonitorConsumer` gets a third, matching the established convention |
| `EntityRepository.UpdateColumnsAsync` has enough schema info to run the size check itself | refuted — it takes `TableSchema`, not `SchemaDescriptor`; `ValidateTextColumnSizes` needs `LargeFieldColumns` and `StoreTargeting`, only available where `EnrichmentConsumer` already holds the `SchemaDescriptor` |
| `EnrichmentConsumer`'s zero-columns branch already records a state row via `state.UpsertAsync(...)` then returns, without writing back — the exact pattern the size-check failure path reuses | `EnrichmentConsumer.cs:176-178` |
| `EnrichmentService` passes the prompt through verbatim, no wrapping that would interact with new delimiters | `EnrichmentService.cs:36-56` — single `user`-role message, prompt interpolated as-is |
| `EnqueueOneToManyParentsAsync` can take `declaringSchema: SchemaDescriptor` instead of `declaringTypeName: string`, since the schema is already resolved in scope at its call site, and `FetchByKeyAsync` + `EntityAccess.ForTenant` is a live, already-used pattern | `DocumentRerenderConsumer.cs:88` (`declaringSchema` resolved); `EntityRepository.cs:7` (`FetchByKeyAsync` signature); `EnrichmentConsumer.cs:200` (live tenant-scoped caller) |
| `StructSerializer`'s (`ProtoPayloadHelper.cs`) two `UpperFirst`-based folds (top-level and nested `StructValue`) are the complete population needing the collision check | file read in full (26 lines); exactly two `ToDictionary(kv => UpperFirst(kv.Key), ...)` occurrences |
| Both existing CI pipelines are path-filtered to `Iverson.Server/deploy/**`; `codeql.yml` is the repo's only unfiltered, scheduled precedent | `.github/workflows/deploy-validate.yml:3-6`; `.gitlab-ci.yml:4-9`; `.github/workflows/codeql.yml:3-9` |
| `.env` is not currently ignored by any `.gitignore` rule; `Iverson.Server/` is the Docker Compose project directory | `git check-ignore -v .env Iverson.Server/.env` → exit 1, no match; `git ls-files \| grep -i docker-compose` → exactly `Iverson.Server/docker-compose.yml` |
| `AuthentikContainerFixture`'s env-var list and `OrchestratorToken` constant are still shaped as characterized | `AuthentikContainerFixture.cs:108,340` — confirmed unchanged |

## Out of scope

- Batching/sequencing this design into an implementation plan (a separate step).
- A "system"-role split for the enrichment prompts (available on the backend but not adopted — larger change than Finding #5 requires).
- Extending the audit log to a tamper-evident store, or any other item the CSR report itself marked as an accepted residual, recorded residual, or not-a-confirmed-gap rather than a numbered finding.
