# CSR Round 9 Remediation Design

**Source review:** `docs/criticalreviews/2026-09-16-iverson-critical-security-review-9.md` (commit SHA: `f1382e1330ce734440f4d65dec4752f1a058c699`)

**Goal:** Fix Findings #1, #2, #4 from CSR round 9, and the "quick win" (Postgres) half of Finding #3. Mitigate Finding #5 — closes its 1-RPC oracle; a 2-RPC oracle remains as an accepted residual (forced decision, resolved — see section 5). Findings #6 and #7 get no code change — both are documented as accepted-open/monitor. The Authentik/OIDC/service-mesh half of Finding #3, and the StarRocks-TLS half of the same finding, are deferred as separate future infrastructure work.

---

## 1. Schema `OwnerTenantId` write-side ownership check (closes Finding #1)

**Problem:** `RegisterSchema` stamps `OwnerTenantId` from the caller's acting-user claim and unconditionally overwrites whatever descriptor already exists, with no comparison to the incumbent owner. Any holder of the `schema_admin` scope — which the platform's own service-client blueprint issues to tenant-bound clients, not just operators — can seize another tenant's type ownership (denying that tenant's own `GetSchema` visibility) or reset ownership to `null` by omitting the acting-user header (reopening the cross-tenant metadata leak Finding #1 of round 8 closed).

**Fix:** in `SchemaRegistrationOrchestrator.RegisterAsync`'s phase-1 loop (`Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`), source the ownership comparison from the registry directly — not from the `priorDescriptor` lookup used a few lines later for the embedding-model-change check, which is `batchDescriptors`-first and always carries a `null` `OwnerTenantId` for a descriptor built earlier in this same request (stamped only in phase 3). Reusing that lookup would reject a caller's own same-request duplicate type name as "registered to another tenant." Add:

```csharp
var priorRegistered = registry.Get(typeDesc.TypeName);
if (priorRegistered is not null && priorRegistered.OwnerTenantId != ownerTenantId)
    throw new RpcException(new Status(StatusCode.PermissionDenied,
        $"Type '{typeDesc.TypeName}' is registered to another tenant and cannot be re-registered here."));
```

placed before the `priorDescriptor`/embedding-check block (which is unaffected and keeps its own separate lookup), so a rejected registration applies no DDL and writes nothing to the registry — consistent with the file's existing phase-1/phase-3 split.

**Accepted, deliberate consequence (per your explicit choice):** this condition does not special-case `priorDescriptor.OwnerTenantId == null`. Round 8's own design spec states that pre-round-8 `_iverson_schema` rows "predate this marker and carry no such key — deserializes to null." Under this fix, **any type that currently has a null `OwnerTenantId` becomes permanently unclaimable by any acting-user-bearing caller** — the very next legitimate tenant-scoped re-registration of such a type is rejected (`priorDescriptor is not null` is true; `null != "tenant-A"` is true). Only a header-less call (itself only reachable by omitting the acting-user token, i.e. `ownerTenantId == null`) can still "re-register" a null-owner type. This also means round 8's own documented "different tenants routinely share a type name across restarts" pattern is no longer tolerated for any type once one tenant has claimed it — a second tenant's legitimate re-registration of that same shared type name is now rejected. Both consequences were explicitly presented and accepted; see Known Issues below.

---

## 2. Pre-authentication rate limiting (closes Finding #2)

**Problem:** The HTTP pipeline runs `UseAuthentication()` → `UseAuthorization()` → `UseRateLimiter()`. A request carrying a garbage or expired bearer token is rejected by `UseAuthorization()` and never reaches the rate limiter at all — such requests are not merely rate-limited late, they are never rate-limited. This applies to gRPC calls too: ASP.NET Core's `UseAuthentication`/`UseAuthorization` middleware runs ahead of the gRPC-specific interceptor chain (`RateLimitInterceptor`, which only reorders relative to the second, acting-user-token interceptor).

**Fix:** add a second, separate `RateLimiterOptions` instance, applied via `app.UseRateLimiter(preAuthOptions)` placed *before* `app.UseAuthentication()`:

```csharp
var preAuthOptions = new RateLimiterOptions
{
    RejectionStatusCode = StatusCodes.Status429TooManyRequests
};
preAuthOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
{
    var isHealthListenerEndpoint = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()?.Port == 8081;
    if (isHealthListenerEndpoint)
        return RateLimitPartition.GetNoLimiter("unlimited");

    return RateLimitPartition.GetSlidingWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 6_000,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        });
});
// The existing "traces" named policy (registered only on the DI-configured post-auth options
// below) must also exist on this instance — RateLimiterOptions' policy map is per-instance, and
// /v1/traces carries .RequireRateLimiting("traces"); without this, every request to that endpoint
// throws InvalidOperationException before ever reaching the endpoint. No-op here (not a clone of
// the real per-sub policy, which has no "sub" claim to key on this early): the real 60/min budget
// stays enforced by the unchanged post-auth limiter below.
preAuthOptions.AddPolicy("traces", _ => RateLimitPartition.GetNoLimiter<string>("unlimited"));

app.Use(ListenerPortGateAsync);
app.UseRateLimiter(preAuthOptions);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();   // existing DI-configured GlobalLimiter, unchanged
app.UseGrpcWeb();
```

Unlike the existing post-auth `GlobalLimiter`, this new limiter does **not** exclude gRPC-content-typed requests — gRPC calls pay the same pre-auth JWT cost as HTTP and have no pre-auth protection of their own today, so they need this same coverage. The existing per-subject `GlobalLimiter` stays exactly where it is, unmodified.

**Out of scope (deferred):** `UseForwardedHeaders`/trusted-proxy configuration for real client-IP visibility behind ingress-nginx. Without it, `ctx.Connection.RemoteIpAddress` resolves to the ingress controller's pod IP for all external traffic, so this new limiter degrades to one shared 6,000/min bucket for the whole platform's external traffic — still strictly better than no limiter, but not truly per-client. Needs the cluster's actual trusted-proxy CIDR, which requires an infrastructure decision this spec doesn't make.

---

## 3. Postgres connection TLS certificate verification (closes the Postgres half of Finding #3)

**Problem:** The Postgres connection string sets no `SSL Mode` or `Root Certificate`; Npgsql defaults to opportunistic (`Prefer`) TLS negotiation with no certificate verification — protected against a passive sniffer, not against an active on-path attacker.

**Fix — apply identically to BOTH `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` and `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml`:** the worker Deployment carries a byte-identical `ConnectionStrings__Postgres`, connecting as the same `iverson` role — under the `BYPASSRLS` `iverson_maintenance` role via `EntityAccess.CrossTenantMaintenance`, since the worker's reconciliation/consumer paths read and write every tenant's rows. Leaving it out would leave the fix's own closure claim false.
1. Add a new volume + volumeMount to each deployment template, mirroring the existing `kafka-ca`/`qdrant-tls` pattern (already present in both charts in the same shape):
   ```yaml
   volumeMounts:
     - name: postgres-tls
       mountPath: /etc/postgres-tls
       readOnly: true
   volumes:
     - name: postgres-tls
       secret:
         secretName: {{ .Release.Name }}-postgres-ca
         items:
           - key: ca.crt
             path: ca.crt
   ```
   The `items:` projection is required, not optional — without it every key in the secret is mounted, including the CA's private key (`ca.key`). This exposes only the public `ca.crt`, matching the projection the existing `qdrant-tls` volume already uses for the same reason (`deployment.yaml:213-218` in the api chart; the worker chart's own `qdrant-tls` volume uses the identical shape) — `kafka-ca`'s own lack of a projection is not a counterexample, since that secret is Strimzi's public cert secret, not a CA-key secret. The CA secret name is release-scoped, not workload-scoped (`cluster.yaml:4`), so the same `{{ .Release.Name }}-postgres-ca` reference is correct in both charts.
   (CNPG auto-generates this CA secret, named `<cluster-name>-ca`, by default — this chart's `cluster.yaml` already states "CNPG always enables and manages TLS ... unconditionally," and no custom `certificates:` override changes the default secret naming.)
2. Change the connection string in both templates:
   ```
   Host={{ .Release.Name }}-postgres-rw;Port=5432;Database=iverson;Username=iverson;Password=$(POSTGRES_APP_PASSWORD);SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt
   ```

**Pre-merge verification gate (forced decision, resolved — verify before merging):** this fix must not merge until confirmed against a real cluster, and blocks BOTH the api and worker Deployments coming ready, not just one. `SSL Mode=VerifyFull` fails the whole connection, not partially, if any of the following don't hold: the secret `{{ .Release.Name }}-postgres-ca` exists, it contains a key named `ca.crt`, that CA is the one that actually signed the server certificate presented by `{{ .Release.Name }}-postgres-rw`, **and that certificate's SAN list contains the bare service name `{{ .Release.Name }}-postgres-rw` the connection string uses** — this fourth condition is not optional: it's the entire difference between `VerifyFull` (chosen here) and the cheaper `VerifyCA` (chain-only, no hostname check), per Npgsql's own documentation. A chain-valid certificate with the wrong SAN passes a plain `openssl verify` and still fails `VerifyFull` at connect time. Before merging, on a real cluster:
```bash
kubectl get secret <release>-postgres-ca -o jsonpath='{.data}'                              # confirm the key set includes ca.crt
kubectl get secret <release>-postgres-server -o jsonpath='{.data.tls\.crt}' | base64 -d > server.crt
openssl x509 -in server.crt -noout -ext subjectAltName                                      # must list <release>-postgres-rw
openssl verify -CAfile ca.crt -verify_hostname <release>-postgres-rw server.crt              # confirm both the signing chain AND the hostname match
```
The `items:` projection above converts a wrong-key-name mistake into a deploy-time pod-startup failure with a clear error, rather than a silent runtime connection failure — but it does not by itself confirm the signing chain or the hostname match, which is why the live checks above are still required.

**Out of scope (deferred, per your earlier answer):** StarRocks gets no TLS change — verified the chart has no TLS infrastructure on that store at all today (no cert issuance, no `ssl`/`tls` config anywhere in `charts/starrocks/`), so `SslMode=VerifyCA` would simply fail to connect; standing up TLS there is new infrastructure work, not a quick win. The Authentik admin-token hop, OIDC discovery, Jaeger, and the ingress→api h2c leg are likewise deferred (need real architectural work: TLS termination point decisions or a service mesh). The `job-revoke-cross-db` bootstrap Job's `psql` step (per your explicit choice) also uses the same `iverson` credential with no `PGSSLMODE` set, but is deferred rather than fixed in this round — its exposure window is one Job run at install time rather than continuous Deployment traffic, and its entire SQL surface is two `REVOKE`/`GRANT` statements, never a read of tenant data; see Known Issues.

---

## 4. Complete the listener partition (closes Finding #4)

**Fix:** append `.WithMetadata(new RequireListenerPort(8080))` to the four currently-unmarked `MapGrpcService` calls in `Iverson.Server/Iverson.Api/Program.cs:607-610`:

```csharp
app.MapGrpcService<ObjectMappingGrpcService>().WithMetadata(new RequireListenerPort(8080));
app.MapGrpcService<ObjectPersistenceGrpcService>().WithMetadata(new RequireListenerPort(8080));
app.MapGrpcService<ObjectRetrievalGrpcService>().WithMetadata(new RequireListenerPort(8080));
app.MapGrpcService<ObjectSearchGrpcService>().WithMetadata(new RequireListenerPort(8080));
```

No behavior change today (verified: `:8081` is configured `Protocols: Http1` in `appsettings.json`, and gRPC requires HTTP/2, so these four services are unreachable on `:8081` regardless) — this closes the gap where a future, unrelated config change (`:8081`'s protocol, or a new `.EnableGrpcWeb()` call) would silently expose the full data plane to `:8081`'s any-source NetworkPolicy rule with nothing in that future diff to show it.

**Out of scope (deferred):** the startup assertion that would make the marker non-optional (throw if any endpoint lacks a `RequireListenerPort` marker) — a bigger change needing to correctly enumerate every legitimate unmarked endpoint (e.g. `/health`'s own internals), not needed for this fix to close Finding #4 itself.

---

## 5. Narrow the cross-tenant `Update` collision to a 2-RPC oracle (mitigates Finding #5)

**Problem:** Both `Update` paths (`ObjectPersistenceGrpcService.cs`, `ObjectMappingGrpcService.cs`) deliberately read the existing row via `EntityAccess.CrossTenantMaintenance` so `EnforceWriteAuthorization` can compare tenant values and deny on mismatch. This makes the two outcomes ("key exists, another tenant's" vs. "key doesn't exist anywhere") observably different — an authenticated caller can probe whether an arbitrary key exists in another tenant's rows.

**Fix (verified against a real Postgres+RLS container, not just read):** change the read from `EntityAccess.CrossTenantMaintenance` to `EntityAccess.ForTenant(...)`, matching how `Get` and `Delete` already work — this makes another tenant's row genuinely invisible, not just denied on comparison. Verified empirically that this is sufficient: reproducing this codebase's exact schema (`FORCE ROW LEVEL SECURITY`, a `USING`-only tenant policy) and exact upsert SQL (`INSERT ... ON CONFLICT ("Key") DO UPDATE SET ...`) against a real Postgres 16 container, an attempt to "update" a key that exists under a different tenant fails cleanly with `SQLSTATE 42501: new row violates row-level security policy` — not a raw duplicate-key violation, not a silent no-op — and the other tenant's row is left provably unmodified.

Wrap the upsert call (`outboxWriter.UpsertAndEnqueueOutboxAsync`) in a catch for this specific condition, at both call sites — the catch body differs per site because the two `Update` methods return different response types:

`ObjectPersistenceGrpcService.Update` (returns `PersistResponse`, which has a `Key` field; this class's constructor parameters carry no leading underscore, and the acting user is reached via `actingUserAccessor.ActingUser`; the upsert call's result must be hoisted above the `try` since it's consumed after the catch on the non-exceptional path):
```csharp
Guid outboxRowId;
try
{
    outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(...);
}
catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText.Contains("row-level security policy"))
{
    auditLog.Denied(actingUserAccessor.ActingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
    return new PersistResponse { Success = true, Key = key, TraceId = request.TraceId };
}
```

`ObjectMappingGrpcService.Update` (returns `MappingResponse`, which has no `Key` field but does have `Data` — a genuine success on this path returns `Data = request.Payload` after stripping the server-owned tenant column, so the swallowed path must match that shape exactly, not just the status; this class's constructor parameters are all leading-underscore-prefixed, unlike `ObjectPersistenceGrpcService`'s):
```csharp
Guid outboxRowId;
try
{
    outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(...);
}
catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText.Contains("row-level security policy"))
{
    _auditLog.Denied(_actingUserAccessor.ActingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
    AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);
    return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
}
```
The `RemoveTenantColumn` call is load-bearing, not cosmetic: on this path `EnforceWriteAuthorization` took the create branch, which force-sets the tenant column into `request.Payload` itself; omitting the strip would return that server-owned column to the caller only on this swallowed path — a second, subtler oracle.

The `MessageText.Contains(...)` check is deliberate, not decorative: SQLSTATE `42501` (`insufficient_privilege`) is also raised for genuine permission/grant misconfigurations, which must **not** be silently reported as a fake success — only the RLS-specific message is safe to swallow this way.

**Change from the read-side `EnforceWriteAuthorization` check:** the `TenantMismatch` branch inside `AuthorizationFieldMasking.EnforceWriteAuthorization` becomes unreachable for these two call sites (the row is never visible cross-tenant, so `existingRowJson` is always null for another tenant's key — the method takes the create branch, which is exactly what then fails at the database layer and gets caught above). No change needed inside `AuthorizationFieldMasking.cs` itself; the two call sites' `existingRowJson` source is what changes.

**Residual (forced decision, resolved — accept a 2-RPC oracle):** this fix closes the single-RPC oracle — the `Update` response itself is now identical for both cases — but does not make the underlying visibility identical end-to-end. `Update` upserts: a genuinely-free key gets a real row created; an RLS-hidden foreign-tenant key does not (the insert collides and is caught). A follow-up `Get(key)` on the same key still distinguishes the two cases, at the cost of one extra RPC instead of zero. Making `Update` non-creating would close this fully, but was evaluated and rejected: `Update`'s upsert semantics are a deliberately designed, tested product feature (`ObjectPersistenceGrpcServiceTests.cs`'s `Update_ExecutesSqlUpsert_WithPayloadJson` and `Update_ForOrdinaryCaller_WhenRowDoesNotExistYet_ForceSetsOwnerFieldToActingUserSub`; `ObjectMappingGrpcServiceTests.cs`'s `Update_ExecutesUpsertSql_DirectlyToPostgres`, `Update_InsertsReconciliationQueueRowInSameTransactionAsUpsert`, and `Update_WithBypassRole_WhenRowDoesNotExistYet_LeavesOwnerFieldUntouched` all confirm and exercise it), not an accident — removing it is a platform-wide wire-contract change out of proportion to this finding. Finding #5 is recorded as **mitigated, not fully closed**: the 1-RPC oracle this fix targeted is closed; a 2-RPC oracle remains as an accepted residual.

**Conformance matrix consequence:** the cross-language conformance matrix's `IdentityScenario` scenario (`Iverson.ClientConformance/Scenarios/IdentityScenario.cs`) asserts `DeniedStatusCode = 7` (`PERMISSION_DENIED`) for exactly this cross-tenant `Update` attempt, and the requirement it discharges (`IVC-IDN-003`) is Active and normative in the published client standard. This fix changes the server outcome to a swallowed success, which makes IVC-IDN-003's enforcement clause false — nothing on the wire distinguishes "denied" from "accepted" anymore. **No driver-side change is required in any of the five languages:** every driver already reports the acceptance path as inert data (`{statusCode: null, status: "succeeded"}`) with an explicit in-source comment that the outcome is "reported as a missing status code rather than judged here" — the drivers already emit exactly what this fix's outcome needs. Four artifacts need updating at implementation time, as one required co-change documenting a single behavioral change, not four independent or optional edits:

1. `Iverson.ClientConformance/Scenarios/IdentityScenario.cs` — `DeniedStatusCode`'s constant and doc comment replaced with a success-shaped expectation, the assertion inverted, and the failure-detail strings (which currently name acceptance as the failure mode) rewritten.
2. `Iverson.ClientConformance.Tests/IdentityScenarioTests.cs` — invert `Judge_WrongActingUsersUpdateWasDenied_Idn003EnforcementPasses` and `Judge_WrongActingUsersUpdateSucceeded_Idn003EnforcementFails`, repoint the shared `JudgeHappy` fixture's `denied` default at the acceptance shape, and rewrite `Judge_Idn003EnforcementDetail_…` (whose asserted detail strings are currently a denial's). Leave `Judge_WrongActingUsersUpdateFailedWithSomeOtherCode_…` unchanged — a code other than "none" still means something other than the swallow fired.
3. `Iverson.ClientConformance/Requirements.cs` (the enforcement bullet and the "what the enforcement half cannot distinguish" paragraph) — updated to match.
4. `docs/standards/iverson-client-standard.md` — move IVC-IDN-003's enforcement clause to the standard's existing Deferred-rows ledger (per your explicit choice): reword the Statement to a derivation-only claim, add a Deferred row with the reason that the server no longer signals the refusal on the wire at all, by design, so no client-observable assertion can discharge enforcement — the only remaining evidence is the server's own audit entry (`reason=BlockedCrossTenantWrite`), which no conformance run reads. Update the Coverage row and Backstop section to match. This mirrors an existing sibling entry in the same Deferred ledger, whose stated reason is identically "no client can read the audit log."

---

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | The ownership comparison must source from `registry.Get(typeDesc.TypeName)` directly, not from the `priorDescriptor` lookup used later for the embedding-model-change check — that lookup is `batchDescriptors`-first, and an in-batch descriptor's `OwnerTenantId` is always `null` (stamped only in phase 3), which would falsely reject a same-request duplicate type name as belonging to another tenant | `SchemaRegistrationOrchestrator.cs:258` populates `batchDescriptors[descriptor.TypeName] = descriptor` from the freshly-built descriptor, before any `OwnerTenantId` stamp; `SchemaRegistry.cs:24-25` — `registry.Get(string)` returns the persisted descriptor from `_schemas`, the correct operand |
| 2 | No existing test in `SchemaRegistrationOrchestratorTests.cs`/`DocumentTemplateValidationTests.cs` registers the same type under two different non-null owners expecting success | `grep` for any non-`null` `ownerTenantId` literal in either file — zero hits; every call uses `null` |
| 3 | The conformance-driver matrix and loadtest both operate under a single, consistent tenant identity throughout a run, so the new ownership check doesn't break their normal `RegisterAllAsync` flows | `docs/runbooks/client-conformance-matrix.md`: one `IVERSON_CLIENT_SCOPE`/`IVERSON_CLIENT_ID` pair covers the whole matrix run across all 5 language drivers; `Iverson.Client.Conformance.Driver/Program.cs:103` reads `--acting-token` once per process, reused across all 9 `RegisterAllAsync()` call sites |
| 4 | The one gRPC-level test scenario that uses a second, different tenant's token (`--wrong-acting-token`) exercises an `Update` denial, not `RegisterSchema` | Read `Program.cs:773-800` directly — the wrong-token invoker is used only for the negative-leg `Update` test |
| 5 | `RpcException(PermissionDenied)` thrown from `RegisterAsync`'s phase 1 isn't caught/retried anywhere before reaching the gRPC caller | `grep` for `try`/`catch` around `_schemaRegistration.RegisterAsync` in `ObjectMappingGrpcService.cs` — none found |
| 6 | `app.UseRateLimiter(RateLimiterOptions)` (explicit-instance overload) and the DI-configured `app.UseRateLimiter()` can both run in the same pipeline without conflict | Built and ran a real minimal Kestrel app with both calls, one before and one after `UseAuthentication()`/`UseAuthorization()`: request succeeds end to end (`ok user=probe-user`, HTTP 200) |
| 7 | `context.GetEndpoint()` resolves correctly even before `UseAuthentication()` runs | Same probe: pre-auth limiter callback printed `endpoint=HTTP: GET /probe` (not null) |
| 8 | `ctx.Connection.RemoteIpAddress` is safely readable pre-auth | Same probe: printed `ip=127.0.0.1`, no exception |
| 9 | Npgsql 10.0.3 accepts `SSL Mode=VerifyFull;Root Certificate=<path>` as valid connection-string keys | Built a real throwaway project referencing `Npgsql 10.0.3`; `NpgsqlConnectionStringBuilder` parsed the string and reported `SslMode=VerifyFull RootCertificate=/etc/postgres-tls/ca.crt` |
| 10 | This chart sets no `certificates:` override, so CNPG's default CA-secret naming (`<cluster-name>-ca`) applies — this covers only that no override exists, not that the secret's actual key set or its signing chain match what the fix assumes; that half is unverified and is why section 3 gates the fix's merge on a live check | Read `charts/postgres/templates/cluster.yaml` in full — no `certificates:` stanza; explicit comment states CNPG "always enables and manages TLS ... unconditionally" |
| 11 | No existing volume/volumeMount collision at `/etc/postgres-tls` in either the api or worker deployment template | Read both `charts/api/templates/deployment.yaml`'s and `charts/worker/templates/deployment.yaml`'s `volumeMounts`/`volumes` sections directly — only `kafka-ca`, `qdrant-tls`, `tmp` exist in each |
| 12 | `:8081` cannot reach the 4 unmarked gRPC services today regardless of the new markers (no behavior change) — the "gRPC needs HTTP/2" reasoning alone is insufficient, since `app.UseGrpcWeb()` is global and gRPC-Web runs over HTTP/1.1; the conclusion holds only because `GrpcWebOptions.DefaultEnabled` is never set (default `false`) and none of the 4 services calls `.EnableGrpcWeb()` | Read `appsettings.json:13,17` directly — `:8080`=`Http2`, `:8081`=`Http1`; `command grep -rn "AddGrpcWeb\|GrpcWebOptions\|DefaultEnabled"` across non-worktree `.cs` — zero hits |
| 13 | No existing test asserts the 4 core gRPC services are unmarked | `grep` for `ObjectMapping\|ObjectPersistence\|ObjectRetrieval\|ObjectSearch` in `AuthenticationPipelineTests.cs` — zero hits |
| 14 | `EnforceWriteAuthorization`'s `TenantMismatch` branch is exercised by exactly 2 real call sites (the Update paths in `ObjectPersistenceGrpcService`/`ObjectMappingGrpcService`), not more | Read all 4 call sites directly: the other 2 (`ObjectMappingGrpcService.cs:310`, and `ObjectPersistenceGrpcService.cs:35`'s `Post`/Create paths) pass `existingRowJson: null` unconditionally, making the `TenantMismatch` branch unreachable there |
| 15 | Existing `Update_TenantMismatch_LogsAuditDeniedWithTenantMismatch` tests don't assert the specific `PermissionDenied` status code (so changing the outcome doesn't break them, though the fix supersedes needing to check this — the test's mocked `_entities.FetchByKeyAsync` return would need updating regardless once the access level changes) | Read both tests directly (`ObjectMappingGrpcServiceTests.cs:2125`, `ObjectPersistenceGrpcServiceTests.cs:862`) — both assert only `ThrowAsync<RpcException>()`, no status-code check |
| 16 | `Get` and `Delete` do not share Finding #5's oracle — neither needs the same fix. (Correction: both actually use a guarded ternary with a `CrossTenantMaintenance` arm — `decision.TenantColumn is not null ? ForTenant(decision.TenantValue) : CrossTenantMaintenance` — not unconditional `ForTenant` as originally stated here. The no-oracle conclusion still holds: `decision.TenantColumn is null` co-occurs with `Denied = true`, and both sites deny before the cross-tenant arm's result would ever reach the caller.) | `ObjectRetrievalGrpcService.cs:115-117`, `ObjectMappingGrpcService.cs:481-483`; `RowFieldAuthorizationEvaluator.cs:29-30` for the `Denied`/`TenantColumn` co-occurrence |
| 17 | The entity tables' key column is a plain (non-composite) `PRIMARY KEY`, globally unique across tenants sharing the table | Read `PostgresSchemaManager.cs:68` directly — `"{KeyColumn}" {SqlType} PRIMARY KEY`, no tenant column in the constraint |
| 18 | The exact upsert SQL is `INSERT ... SELECT * FROM json_populate_record(...) ON CONFLICT ("Key") DO UPDATE SET ...` | Read `OutboxWriter.cs:26-31` directly |
| 19 | Narrowing the read to `EntityAccess.ForTenant(...)` and then attempting the real upsert against a cross-tenant key produces a clean, catchable, specific error — not a silent no-op or an unrelated duplicate-key violation | Reproduced the exact schema (`FORCE ROW LEVEL SECURITY`, `USING`-only tenant policy) and exact upsert SQL against a real Postgres 16 container: the conflicting write failed with `SQLSTATE 42501: new row violates row-level security policy`; the other tenant's row was confirmed unmodified afterward |
| 20 | SQLSTATE `42501` is also used for genuine permission/grant misconfigurations, so catching on SQLSTATE alone would be too broad | Postgres documentation: `42501` is the `insufficient_privilege` error class, shared by RLS violations and plain `GRANT`-missing errors — the fix's catch also checks the message text for "row-level security policy" specifically |

## Known issues / accepted as out of scope

- **Finding #1's null-owner and cross-concrete-tenant consequences** (per your explicit choice, after being shown the concrete effect) — the write-side ownership check has no special case for a `null` incumbent owner. Every type registered before this fix (or by any header-less call) becomes permanently unclaimable by any acting-user-bearing caller going forward; and once a type has any concrete owner, a different tenant's routine, otherwise-legitimate re-registration of that same shared type name is now rejected, reversing round 8's own deliberate tolerance for that pattern. If this needs revisiting later, the `IsShared` opt-in flag (referenced in Finding #6's own remediation) is the identified path to reconciling ownership enforcement with legitimate type-name sharing.
- **StarRocks TLS, the Authentik admin-token hop, OIDC discovery over TLS, and a service mesh** (per your choice) — all deferred; StarRocks has no TLS infrastructure today (would need new cert issuance and chart changes, not a quick win), and the Authentik/OIDC/mesh work needs real infrastructure decisions this spec doesn't make.
- **The `job-revoke-cross-db` bootstrap Job's Postgres connection** (per your explicit choice) — this one-shot Helm Job connects with the same `iverson` credential as the api/worker Deployments, with no `PGSSLMODE` set, and so shares the same unverified-TLS condition Finding #3 targets. Deferred rather than fixed in this round: its exposure window is one Job run per `helm upgrade` rather than continuous traffic, and its entire SQL surface is two `REVOKE`/`GRANT` statements — it never reads or writes tenant data.
- **Finding #4's startup assertion** (architectural improvement, not required for the primary fix) — deferred; would need to correctly enumerate every legitimate unmarked endpoint to avoid false positives.
- **Finding #2's `UseForwardedHeaders`/trusted-proxy configuration** — deferred; needs the cluster's actual trusted-proxy CIDR. Without it, the new pre-auth limiter degrades to one shared bucket per ingress pod for external traffic, rather than true per-client throttling.
- **Findings #6 and #7** (per your choice) — no code change. Finding #6 (type-name existence oracle) becomes safe to close later once ownership can no longer be silently reassigned by this round's Finding #1 fix, but is not closed by it alone — the `IsShared` flag is still the identified path if it's ever revisited. Finding #7 (residual prompt-injection surface in the reasoning agent) has mitigations already proportionate to its retrieval-only, tenant-bounded scope; re-assess only if the agent's tool surface grows beyond retrieval.
