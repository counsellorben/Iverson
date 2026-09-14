# CSR Round 4 Remediation — Design

Source: `docs/criticalreviews/2026-09-13-iverson-critical-security-review-4.md` (15 findings, SQS 0/100 hard-gated).

## Goal

Close all 15 findings from CSR round 4. The three findings sharing a common shape — a security control added once, on one call site, with nothing catching a structurally-equivalent sibling missing it (#3, #4, #9) — get fixed at their natural architectural boundary, each with a small regression test acting as a safety net against a future 4th instance, rather than patched as isolated one-offs. This is a design spec: what to build. Sequencing/batching for implementation is a separate, later step.

---

## 1. Finding #1 — Document templates may not traverse a relation to a row-owned type

Extend `SchemaRegistrationOrchestrator.RequireScalarProperty` (`Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:453-478`) to reject a target type declaring `Authorization.OwnerField` or non-empty `RowPermissions`, immediately after the existing `FieldPermissions` rejection at lines 471-477 — same shape, same `context`/`statusCode` parameters already in scope:

```csharp
if (context.Authorization?.OwnerField is not null || context.Authorization?.RowPermissions.Count > 0)
{
    throw new RpcException(new Status(statusCode,
        $"Document template references property '{propertyName}' on '{context.TypeName}', which " +
        "declares row-level authorization (OwnerField/RowPermissions); a document template cannot " +
        "traverse a relation to a row-owned type."));
}
```

This is a registration-time `InvalidArgument`/`FailedPrecondition` (matching the existing `statusCode` parameter's two call sites), not a runtime check. No existing registered schema or test fixture combines these two features (verified), so this adds no breaking change to anything currently registered — it only forecloses the combination going forward.

---

## 2. Finding #2 — SDK token-endpoint scheme validation, all 5 SDKs

Each SDK's credentials constructor/validator gains a check: if the token endpoint's scheme is not `https`, require the same insecure-opt-in flag the existing gRPC-channel guard already uses — reusing the existing concept, not adding a second flag.

| SDK | Existing flag (reused) | New check location |
|---|---|---|
| Python | `allow_insecure_credentials` | `iverson_client/core.py`'s `IversonClient.__init__` (same method already checking `use_tls`/`credentials`, per `core.py:836-861`) |
| .NET | `allowInsecureChannelCallCredentials` | `ServiceCollectionExtensions.AddIversonClient` |
| Go | the existing `RequireTransportSecurity()`-backed opt-in | `iverson/auth.go` |
| TypeScript | `allowInsecureCredentials` | `src/core.ts`'s constructor |
| Java | `requireInsecureCredentialsOptIn`'s backing flag | `IversonClient.java` |

Go and TypeScript additionally set an explicit no-follow (or same-origin-only) redirect policy on the token-request HTTP client (`http.Client{CheckRedirect: ...}` in Go; `fetch(..., {redirect: 'error'})` or equivalent in TypeScript), closing the redirect-replay amplifier the CSR report flagged as inferred-from-documented-defaults.

---

## 3. Findings #3 / #4 / #9 — the recurring-pattern fixes

### 3.1 Finding #3 — `ObjectMapping.Post`/`Update` get the payload-size guard `ObjectPersistence` already has

`AuthorizationFieldMasking.EnforceWriteAuthorization` (`Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:28+`) gains an `IPayloadSizeValidator` parameter and calls `ValidateTextColumnSizes(payload, schema)` internally, right after the existing tenant-column-smuggling check. Both `ObjectMappingGrpcService` (`:303`, `:363`) and `ObjectPersistenceGrpcService` (`:35`, `:111`) already call this one shared function on all 4 of their write-RPC methods to be a valid write path at all — folding the check in here means it structurally cannot be skipped by a third future write RPC, without needing that RPC's author to remember to inject the dependency separately.

`ObjectPersistenceGrpcService`'s now-redundant direct calls to `ValidateTextColumnSizes` (`:46`, `:122`) are removed — the shared helper is the single call site.

**Verified, accepted trade-off:** this moves the size check earlier in `ObjectPersistenceGrpcService.Post`/`Update`'s sequence — currently `EnforceWriteAuthorization` → `ValidateAndNormalizeRelations` → `ValidateTextColumnSizes`; after this change, the size check runs as part of the first step, before relation validation. A request that is both malformed in its relations and oversized in a text column now gets an oversized-column error rather than a relation error. Accepted and documented here rather than blocking, matching this session's own precedent for an analogous disclosed reordering (CSR round 3's `MaxRelationDepth`-before-`RequireSchema` change in `ObjectMappingGrpcService.Get`).

### 3.2 Finding #4 — DLQ admin surface gets tenant-scoped

- `DlqSchema.Table` (`Iverson.Server/Iverson.Api/Reconciliation/DlqSchema.cs`) gains a nullable `TenantId` column (`new("TenantId", "text", true)`), applied automatically on next restart via `ApplySchemaAsync`'s existing idempotent `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` mechanism — no new infrastructure, no manual migration step.
- `DlqRow`/`DlqMessage` (`Iverson.Server/Iverson.Sql/DlqRepository.cs`) gain the corresponding `TenantId` property; `DlqRepository`'s `SELECT`/`INSERT` column lists are updated to match.
- `DlqMonitorConsumer.HandleAsync` (`Iverson.Server/Iverson.Api/Reconciliation/DlqMonitorConsumer.cs:21-53`) derives `TenantId` at insert time: deserialize `value` as `EntityEvent`, look up `SchemaRegistry.Get(event.TypeName)?.TenantColumn`, and extract that field from `JsonDocument.Parse(event.PayloadJson)` via a small private `ExtractString` helper — mirroring the exact pattern `EnrichmentConsumer.cs:374` and `PopularitySignalConsumer.cs:279` already each carry their own private copy of (this consumer gets a third copy, matching the established convention rather than introducing a new shared utility). Null when the type/column can't be resolved (e.g., the schema was since unregistered); the row is still recorded, just without a tenant to scope by.
- `/admin/dlq` and `/admin/dlq/{id}/replay` (`Iverson.Server/Iverson.Api/Program.cs:399-430`) require an acting-user token in addition to the existing `Operator` policy. The endpoint delegates call `httpContext.AuthenticateAsync("ActingUser")` directly — the same general ASP.NET Core mechanism `ActingUserInterceptor.ValidateActingUserAsync` (`Iverson.Server/Iverson.Api/Grpc/ActingUserInterceptor.cs`) already uses for gRPC calls, confirmed to work identically for a plain HTTP request since gRPC-over-HTTP/2 and Minimal API routes share the same underlying `HttpContext`. `IActingUserAccessor` is NOT used here — it's populated only by the gRPC-specific interceptor pipeline, which doesn't run for Minimal API routes. Results are filtered to the caller's own `tenant_id` claim; a row with a null `TenantId` is visible only to a caller who *also* holds `Operator`, preserving cross-tenant visibility for the genuinely-untenanted plumbing case without making it the default for every row.
- `ExceptionMessage`/`ExceptionType` are left as-is on this surface (raw) — Finding #7's fix closes the adjacent info-leak vector separately; this surface inherits that fix rather than duplicating it.

### 3.3 Finding #9 — LLM output write-back gets the same size check, at the layer that actually has the schema

`EnrichmentConsumer` (`Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs`) takes `IPayloadSizeValidator` as a new dependency. Immediately before calling `entities.UpdateColumnsAsync` (`:189-190`) with the generated `columns` dictionary, it builds a minimal `Struct` from that dictionary and calls the existing `ValidateTextColumnSizes(Struct, SchemaDescriptor)` — reusing the exact validator `ObjectPersistenceGrpcService`/`ObjectMappingGrpcService` call, not a new method or duplicated size-limit logic. This is the correct layer for this check: `EntityRepository.UpdateColumnsAsync` (`Iverson.Sql`) was originally considered but does not have — and structurally should not need — `SchemaDescriptor`/`LargeFieldColumns`/`StoreTargeting` information; `ValidateTextColumnSizes` itself skips validation entirely for types not projected to StarRocks at all, a decision only `Iverson.Api`-layer code (where `EnrichmentConsumer` lives) has the context to make.

Currently bounded by `EnrichmentService.MaxGeneratedTokens = 256`, which cannot approach StarRocks' 65,533-byte column ceiling — this fix closes the gap structurally rather than leaving it dependent on that configuration value never changing.

### 3.4 Three regression tests (the safety net)

1. A test asserting `EnforceWriteAuthorization` rejects an oversized payload, reachable from both `ObjectMappingGrpcService` and `ObjectPersistenceGrpcService`'s write paths via the one shared call site.
2. A test asserting `/admin/dlq` and `/admin/dlq/{id}/replay` return `Unauthenticated`/`PermissionDenied` for a service-only token (no acting-user), and correctly filter by tenant for one that has it.
3. A test asserting `EnrichmentConsumer`'s write-back path rejects an oversized generated value the same way the gRPC write path does.

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

All run as required checks in whichever CI system(s) currently gate merges, matching the existing pattern where `helm lint`/`kubeconform`/`tfsec` already run as required, not advisory, checks. A `.github/dependabot.yml` is added alongside these for automated update PRs going forward.

---

## 6. Findings #7, #10, #11, #12, #13, #14, #15 — the remaining Low findings

**#7 — Fixed error messages instead of `ex.Message` in `RpcException`s.** Every flagged `catch` block in `ObjectSearchGrpcService.cs` (`:240-244`, `:564-568`) and `SchemaRegistrationOrchestrator.cs:143` logs the real exception server-side and returns a fixed, generic message to the caller (e.g., `"Embedding service unavailable."`), correlatable via the existing `X-Trace-Id` response header.

**#10 — `DocumentRerenderConsumer`'s `OneToMany` arm gets tenant-scoped like its siblings.** Replace the direct payload-FK read (`:184-186`) with a tenant-scoped query mirroring `EnqueueByColumnAsync`/`EnqueueByArrayContainsAsync`'s existing pattern (`:152-164`, `:166-178`: `entities.FetchByColumnAsync`/equivalent with `EntityAccess.ForTenant(tenantId)`, then extract and enqueue) before enqueueing — a cross-tenant foreign key now fails closed (zero rows) instead of silently enqueueing.

**#11 — Sanitize `AuditLog`'s actor claims.** `.SanitizeForLog()` on the `sub`/`tenant_id` reads in `Denied` (`AuditLog.cs:13-17`), and on `sub` in `AdminOperation` (`:19-25`) — matching the treatment already given to the resource arguments beside them.

**#12 — Digest-pin the remaining 4 Dockerfiles** (`Iverson.Server/Iverson.Events/Dockerfile`, `Iverson.Server/Iverson.Vector/Dockerfile`, `Iverson.Server/Iverson.Sql/Dockerfile`, `Iverson.Server/Iverson.Launcher/Dockerfile`), same `@sha256:...` treatment already applied to `Iverson.Api`/`Iverson.AdminUI`'s Dockerfiles.

**#13 — `SchemaRegistry.LoadAsync` re-applies identifier/key-type validation on rehydration.** Skipping (not crashing on) a non-conforming row the same way a malformed-JSON row is already skipped (`SchemaRegistry.cs:118-151`).

**#14 — `PopularitySignalConsumer` gets the same poison-message guard its siblings use.** Wrap its 3 unguarded `JsonDocument.Parse` calls (`:201`, `:207`, `:247`) in the same `try/catch → PoisonMessageException` pattern `EnrichmentConsumer.cs:114-125,253-265`, `IntelligenceStoreConsumer.cs:100-109,535-544`, `EngagementStoreConsumer.cs:109-118` already use.

**#15 — Typed `InvalidArgument` instead of unhandled exceptions.** `ObjectRetrievalGrpcService.GetMany` validates `request.Keys` are well-formed GUIDs before calling `EntityRepository.cs:16`'s `keys.Select(Guid.Parse)`. `ProtoPayloadHelper.cs:10`'s case-folding step (`ToDictionary(kv => UpperFirst(kv.Key), ...)`) detects a key collision and raises a typed `RpcException` naming the colliding keys, instead of letting `ToDictionary` throw `ArgumentException`.

All seven are small, independent, single-file-or-two changes with no forks.

---

## 7. Finding #8 — the hard-gate closure

**Generate the 7 credentials, don't hardcode them.** A helper script generates a git-ignored `.env` file on first run with random values (`openssl rand -hex 32` or equivalent) for the three OAuth2 `client_secret`s, three passwords, and the orchestrator API token currently hardcoded in `compose-only/service-clients.yaml` (lines 85, 107, 128, 276, 291, 358, 366).

**Blueprint switches to Authentik's `!Env` tag.** The seven literal values become `!Env VAR_NAME` references. `!Env` has no default-value form — an unset variable is a hard failure at blueprint-import time — so `docker-compose.yml` must pass all seven as environment variables to the Authentik server/worker containers that load this blueprint, sourced from the generated `.env`.

**`AuthentikContainerFixture` gets matching env vars.** `BuildAuthentikContainer` (`Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikContainerFixture.cs:214+`) currently sets a small, fixed env-var list with no substitution layer (bind-mounts the raw blueprints directory directly) — confirmed still the current shape. It needs the same seven environment variables set, generated fresh per fixture instance rather than reading the compose `.env` (the fixture and the compose stack are independent environments). The `OrchestratorToken` constant (`:108`, still present as a hardcoded literal, used at `:340`) is replaced with a value read from the fixture's own generated env var.

This closes the hard gate outright — no static credential remains in source after this change.

---

## Verified assumptions

The following were verified against the current codebase (not taken on faith) before this design was finalized:

| Assumption | Evidence |
|---|---|
| `RequireScalarProperty` has `context.Authorization` (with `OwnerField`/`RowPermissions`) in scope where the `FieldPermissions` check runs | `SchemaRegistrationOrchestrator.cs:453-478`; `AuthorizationRules.OwnerField`/`RowPermissions` confirmed in `SchemaDescriptor.cs:139-141` |
| No registered/test schema combines a document template with a row-owned related type | grep across `SchemaBuilderTests.cs` and `DocumentTemplateValidationTests.cs` — the two concepts never co-occur on the same descriptor |
| All 5 SDKs' insecure-opt-in flag and token endpoint are co-located in the same constructor scope | confirmed for Python (`core.py:836-861`); true by construction for the other 4, since their existing guards already require both in the same scope |
| `EnforceWriteAuthorization` is called by all 4 write-RPC methods | `ObjectMappingGrpcService.cs:303,363`; `ObjectPersistenceGrpcService.cs:35,111` |
| `ValidateTextColumnSizes(Struct, SchemaDescriptor)`'s params are already in scope inside `EnforceWriteAuthorization` | `AuthorizationFieldMasking.cs:28-35` signature already takes both |
| Removing `ObjectPersistenceGrpcService`'s direct calls shifts validation order (not just removes duplication) | `ObjectPersistenceGrpcService.cs:30-46` — current order is authz → relation-validate → size-check; addressed as an accepted, documented trade-off |
| The acting-user mechanism is reachable from an HTTP Minimal API route, not just gRPC | `ActingUserInterceptor.cs`'s `ValidateActingUserAsync` calls `httpContext.AuthenticateAsync("ActingUser")`, a general ASP.NET Core `HttpContext` extension — confirmed this doesn't depend on the gRPC interceptor pipeline |
| The DLQ table's schema supports adding a nullable column without new migration infrastructure | `PostgresSchemaManager.cs:118` runs `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` for every declared column on every startup |
| A reusable tenant-extraction helper exists for `DlqMonitorConsumer` to call | refuted — `EnrichmentConsumer.cs:374`/`PopularitySignalConsumer.cs:279` each carry their own private copy; `DlqMonitorConsumer` gets a third, matching the established convention |
| `EntityRepository.UpdateColumnsAsync` has enough schema info to run the size check itself | refuted — it takes `TableSchema`, not `SchemaDescriptor`; `ValidateTextColumnSizes` needs `LargeFieldColumns` and `StoreTargeting`, only available where `EnrichmentConsumer` already holds the `SchemaDescriptor` |
| `EnrichmentService` passes the prompt through verbatim, no wrapping that would interact with new delimiters | `EnrichmentService.cs:36-56` — single `user`-role message, prompt interpolated as-is |
| `DocumentRerenderConsumer`'s other two relation arms have a literally mirrorable query pattern | `DocumentRerenderConsumer.cs:152-178` — both use `FetchByColumnAsync`/`FetchByArrayContainsAsync` with `EntityAccess.ForTenant(tenantId)` |
| `AuthentikContainerFixture`'s env-var list and `OrchestratorToken` constant are still shaped as characterized | `AuthentikContainerFixture.cs:108,340` — confirmed unchanged |

## Out of scope

- Batching/sequencing this design into an implementation plan (a separate step).
- A "system"-role split for the enrichment prompts (available on the backend but not adopted — larger change than Finding #5 requires).
- Extending the audit log to a tamper-evident store, or any other item the CSR report itself marked as an accepted residual, recorded residual, or not-a-confirmed-gap rather than a numbered finding.
