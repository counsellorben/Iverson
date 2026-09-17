# CSR Round 9 Remediation Design

**Source review:** `docs/criticalreviews/2026-09-16-iverson-critical-security-review-9.md` (commit SHA: `f1382e1330ce734440f4d65dec4752f1a058c699`)

**Goal:** Fix Findings #1, #2, #4, #5 from CSR round 9, and the "quick win" (Postgres) half of Finding #3. Findings #6 and #7 get no code change — both are documented as accepted-open/monitor. The Authentik/OIDC/service-mesh half of Finding #3, and the StarRocks-TLS half of the same finding, are deferred as separate future infrastructure work.

---

## 1. Schema `OwnerTenantId` write-side ownership check (closes Finding #1)

**Problem:** `RegisterSchema` stamps `OwnerTenantId` from the caller's acting-user claim and unconditionally overwrites whatever descriptor already exists, with no comparison to the incumbent owner. Any holder of the `schema_admin` scope — which the platform's own service-client blueprint issues to tenant-bound clients, not just operators — can seize another tenant's type ownership (denying that tenant's own `GetSchema` visibility) or reset ownership to `null` by omitting the acting-user header (reopening the cross-tenant metadata leak Finding #1 of round 8 closed).

**Fix:** in `SchemaRegistrationOrchestrator.RegisterAsync`'s phase-1 loop (`Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`), reuse the `priorDescriptor` lookup already computed there (for the embedding-model-change check, immediately preceding it) and add:

```csharp
if (priorDescriptor is not null && priorDescriptor.OwnerTenantId != ownerTenantId)
    throw new RpcException(new Status(StatusCode.PermissionDenied,
        $"Type '{typeDesc.TypeName}' is registered to another tenant and cannot be re-registered here."));
```

placed immediately after the `priorDescriptor` assignment, before the `priorModel`/embedding-check block, so a rejected registration applies no DDL and writes nothing to the registry — consistent with the file's existing phase-1/phase-3 split.

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

**Fix:**
1. Add a new volume + volumeMount to `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`, mirroring the existing `kafka-ca`/`qdrant-tls` pattern:
   ```yaml
   volumeMounts:
     - name: postgres-tls
       mountPath: /etc/postgres-tls
       readOnly: true
   volumes:
     - name: postgres-tls
       secret:
         secretName: {{ .Release.Name }}-postgres-ca
   ```
   (CNPG auto-generates this CA secret, named `<cluster-name>-ca`, by default — this chart's `cluster.yaml` already states "CNPG always enables and manages TLS ... unconditionally," and no custom `certificates:` override changes the default secret naming.)
2. Change the connection string:
   ```
   Host={{ .Release.Name }}-postgres-rw;Port=5432;Database=iverson;Username=iverson;Password=$(POSTGRES_APP_PASSWORD);SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt
   ```

**Out of scope (deferred, per your earlier answer):** StarRocks gets no TLS change — verified the chart has no TLS infrastructure on that store at all today (no cert issuance, no `ssl`/`tls` config anywhere in `charts/starrocks/`), so `SslMode=VerifyCA` would simply fail to connect; standing up TLS there is new infrastructure work, not a quick win. The Authentik admin-token hop, OIDC discovery, Jaeger, and the ingress→api h2c leg are likewise deferred (need real architectural work: TLS termination point decisions or a service mesh).

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

## 5. Make cross-tenant `Update` collisions structurally impossible (closes Finding #5)

**Problem:** Both `Update` paths (`ObjectPersistenceGrpcService.cs`, `ObjectMappingGrpcService.cs`) deliberately read the existing row via `EntityAccess.CrossTenantMaintenance` so `EnforceWriteAuthorization` can compare tenant values and deny on mismatch. This makes the two outcomes ("key exists, another tenant's" vs. "key doesn't exist anywhere") observably different — an authenticated caller can probe whether an arbitrary key exists in another tenant's rows.

**Fix (verified against a real Postgres+RLS container, not just read):** change the read from `EntityAccess.CrossTenantMaintenance` to `EntityAccess.ForTenant(...)`, matching how `Get` and `Delete` already work — this makes another tenant's row genuinely invisible, not just denied on comparison. Verified empirically that this is sufficient: reproducing this codebase's exact schema (`FORCE ROW LEVEL SECURITY`, a `USING`-only tenant policy) and exact upsert SQL (`INSERT ... ON CONFLICT ("Key") DO UPDATE SET ...`) against a real Postgres 16 container, an attempt to "update" a key that exists under a different tenant fails cleanly with `SQLSTATE 42501: new row violates row-level security policy` — not a raw duplicate-key violation, not a silent no-op — and the other tenant's row is left provably unmodified.

Wrap the upsert call (`outboxWriter.UpsertAndEnqueueOutboxAsync`, both call sites) in a catch for this specific condition:

```csharp
try
{
    outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(...);
}
catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText.Contains("row-level security policy"))
{
    auditLog.Denied(actingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
    return new PersistResponse { Success = true, Key = key, TraceId = request.TraceId };
}
```

The `MessageText.Contains(...)` check is deliberate, not decorative: SQLSTATE `42501` (`insufficient_privilege`) is also raised for genuine permission/grant misconfigurations, which must **not** be silently reported as a fake success — only the RLS-specific message is safe to swallow this way.

**Change from the read-side `EnforceWriteAuthorization` check:** the `TenantMismatch` branch inside `AuthorizationFieldMasking.EnforceWriteAuthorization` becomes unreachable for these two call sites (the row is never visible cross-tenant, so `existingRowJson` is always null for another tenant's key — the method takes the create branch, which is exactly what then fails at the database layer and gets caught above). No change needed inside `AuthorizationFieldMasking.cs` itself; the two call sites' `existingRowJson` source is what changes.

---

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | `priorDescriptor` is computed in `SchemaRegistrationOrchestrator.RegisterAsync`'s phase-1 loop before the point the new ownership check is inserted | Read `SchemaRegistrationOrchestrator.cs:100-103` directly |
| 2 | No existing test in `SchemaRegistrationOrchestratorTests.cs`/`DocumentTemplateValidationTests.cs` registers the same type under two different non-null owners expecting success | `grep` for any non-`null` `ownerTenantId` literal in either file — zero hits; every call uses `null` |
| 3 | The conformance-driver matrix and loadtest both operate under a single, consistent tenant identity throughout a run, so the new ownership check doesn't break their normal `RegisterAllAsync` flows | `docs/runbooks/client-conformance-matrix.md`: one `IVERSON_CLIENT_SCOPE`/`IVERSON_CLIENT_ID` pair covers the whole matrix run across all 5 language drivers; `Iverson.Client.Conformance.Driver/Program.cs:103` reads `--acting-token` once per process, reused across all 9 `RegisterAllAsync()` call sites |
| 4 | The one gRPC-level test scenario that uses a second, different tenant's token (`--wrong-acting-token`) exercises an `Update` denial, not `RegisterSchema` | Read `Program.cs:773-800` directly — the wrong-token invoker is used only for the negative-leg `Update` test |
| 5 | `RpcException(PermissionDenied)` thrown from `RegisterAsync`'s phase 1 isn't caught/retried anywhere before reaching the gRPC caller | `grep` for `try`/`catch` around `_schemaRegistration.RegisterAsync` in `ObjectMappingGrpcService.cs` — none found |
| 6 | `app.UseRateLimiter(RateLimiterOptions)` (explicit-instance overload) and the DI-configured `app.UseRateLimiter()` can both run in the same pipeline without conflict | Built and ran a real minimal Kestrel app with both calls, one before and one after `UseAuthentication()`/`UseAuthorization()`: request succeeds end to end (`ok user=probe-user`, HTTP 200) |
| 7 | `context.GetEndpoint()` resolves correctly even before `UseAuthentication()` runs | Same probe: pre-auth limiter callback printed `endpoint=HTTP: GET /probe` (not null) |
| 8 | `ctx.Connection.RemoteIpAddress` is safely readable pre-auth | Same probe: printed `ip=127.0.0.1`, no exception |
| 9 | Npgsql 10.0.3 accepts `SSL Mode=VerifyFull;Root Certificate=<path>` as valid connection-string keys | Built a real throwaway project referencing `Npgsql 10.0.3`; `NpgsqlConnectionStringBuilder` parsed the string and reported `SslMode=VerifyFull RootCertificate=/etc/postgres-tls/ca.crt` |
| 10 | CNPG auto-generates a `<cluster-name>-ca` secret by default, with no override in this chart | Read `charts/postgres/templates/cluster.yaml` in full — no `certificates:` stanza; explicit comment states CNPG "always enables and manages TLS ... unconditionally" |
| 11 | No existing volume/volumeMount collision at `/etc/postgres-tls` in the api deployment template | Read `charts/api/templates/deployment.yaml`'s `volumeMounts`/`volumes` sections directly — only `kafka-ca`, `qdrant-tls`, `tmp` exist |
| 12 | `:8081` cannot reach the 4 unmarked gRPC services today regardless of the new markers (no behavior change) | Read `appsettings.json:13,17` directly — `:8080`=`Http2`, `:8081`=`Http1`; gRPC requires HTTP/2 |
| 13 | No existing test asserts the 4 core gRPC services are unmarked | `grep` for `ObjectMapping\|ObjectPersistence\|ObjectRetrieval\|ObjectSearch` in `AuthenticationPipelineTests.cs` — zero hits |
| 14 | `EnforceWriteAuthorization`'s `TenantMismatch` branch is exercised by exactly 2 real call sites (the Update paths in `ObjectPersistenceGrpcService`/`ObjectMappingGrpcService`), not more | Read all 4 call sites directly: the other 2 (`ObjectMappingGrpcService.cs:310`, and `ObjectPersistenceGrpcService.cs:35`'s `Post`/Create paths) pass `existingRowJson: null` unconditionally, making the `TenantMismatch` branch unreachable there |
| 15 | Existing `Update_TenantMismatch_LogsAuditDeniedWithTenantMismatch` tests don't assert the specific `PermissionDenied` status code (so changing the outcome doesn't break them, though the fix supersedes needing to check this — the test's mocked `_entities.FetchByKeyAsync` return would need updating regardless once the access level changes) | Read both tests directly (`ObjectMappingGrpcServiceTests.cs:2125`, `ObjectPersistenceGrpcServiceTests.cs:862`) — both assert only `ThrowAsync<RpcException>()`, no status-code check |
| 16 | `Get` and `Delete` do not share Finding #5's oracle — neither needs the same fix | Read both methods directly: both already use `EntityAccess.ForTenant(...)`, never `CrossTenantMaintenance`, so both already return an identical "not found" response for a cross-tenant key and a genuinely-missing one |
| 17 | The entity tables' key column is a plain (non-composite) `PRIMARY KEY`, globally unique across tenants sharing the table | Read `PostgresSchemaManager.cs:68` directly — `"{KeyColumn}" {SqlType} PRIMARY KEY`, no tenant column in the constraint |
| 18 | The exact upsert SQL is `INSERT ... SELECT * FROM json_populate_record(...) ON CONFLICT ("Key") DO UPDATE SET ...` | Read `OutboxWriter.cs:26-31` directly |
| 19 | Narrowing the read to `EntityAccess.ForTenant(...)` and then attempting the real upsert against a cross-tenant key produces a clean, catchable, specific error — not a silent no-op or an unrelated duplicate-key violation | Reproduced the exact schema (`FORCE ROW LEVEL SECURITY`, `USING`-only tenant policy) and exact upsert SQL against a real Postgres 16 container: the conflicting write failed with `SQLSTATE 42501: new row violates row-level security policy`; the other tenant's row was confirmed unmodified afterward |
| 20 | SQLSTATE `42501` is also used for genuine permission/grant misconfigurations, so catching on SQLSTATE alone would be too broad | Postgres documentation: `42501` is the `insufficient_privilege` error class, shared by RLS violations and plain `GRANT`-missing errors — the fix's catch also checks the message text for "row-level security policy" specifically |

## Known issues / accepted as out of scope

- **Finding #1's null-owner and cross-concrete-tenant consequences** (per your explicit choice, after being shown the concrete effect) — the write-side ownership check has no special case for a `null` incumbent owner. Every type registered before this fix (or by any header-less call) becomes permanently unclaimable by any acting-user-bearing caller going forward; and once a type has any concrete owner, a different tenant's routine, otherwise-legitimate re-registration of that same shared type name is now rejected, reversing round 8's own deliberate tolerance for that pattern. If this needs revisiting later, the `IsShared` opt-in flag (referenced in Finding #6's own remediation) is the identified path to reconciling ownership enforcement with legitimate type-name sharing.
- **StarRocks TLS, the Authentik admin-token hop, OIDC discovery over TLS, and a service mesh** (per your choice) — all deferred; StarRocks has no TLS infrastructure today (would need new cert issuance and chart changes, not a quick win), and the Authentik/OIDC/mesh work needs real infrastructure decisions this spec doesn't make.
- **Finding #4's startup assertion** (architectural improvement, not required for the primary fix) — deferred; would need to correctly enumerate every legitimate unmarked endpoint to avoid false positives.
- **Finding #2's `UseForwardedHeaders`/trusted-proxy configuration** — deferred; needs the cluster's actual trusted-proxy CIDR. Without it, the new pre-auth limiter degrades to one shared bucket per ingress pod for external traffic, rather than true per-client throttling.
- **Findings #6 and #7** (per your choice) — no code change. Finding #6 (type-name existence oracle) becomes safe to close later once ownership can no longer be silently reassigned by this round's Finding #1 fix, but is not closed by it alone — the `IsShared` flag is still the identified path if it's ever revisited. Finding #7 (residual prompt-injection surface in the reasoning agent) has mitigations already proportionate to its retrieval-only, tenant-bounded scope; re-assess only if the agent's tool surface grows beyond retrieval.
