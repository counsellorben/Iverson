# Dafny Plan Verification: 2026-10-21-csr-round12-platform-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md
**Source spec:** docs/specs/2026-10-19-csr-round12-platform-design.md
**Plan SHA:** 7c4d86687b557e7e33a52c30cc07e6728dfb4622 | **Replay base:** 7c4d86687b557e7e33a52c30cc07e6728dfb4622 | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-21-csr-round12-platform-implementation-plan/

## 0. Coverage enumeration

### Replay

- The skill's `replay.py apply` cannot parse 227 of the plan's edits (the "**1b.** In the same file, replace:" sub-step form and its `with:` lines, in Tasks 1–6, 8, 9, 18–22 and 27), so run as written it would stop at Task 1 Step 1 with "unrecognised edit form". The user chose to build the worktree from the CIR rounds' replay instead: Tasks 1–25 applied as text from the plan at `7c4d8668` (709/709 edits, the seven `git rm` deletions included), synced over a detached worktree at the replay base `7c4d8668` (code identical to `2f6c6404`: every commit since touches only `docs/`). `git diff --stat` against the base: 189 files changed, the same as the CIR-9 review tree. This is a deviation from Step 7, recorded here; it is not a plan defect, because the plan's edits apply.
- `kind-checks.alive-wait` is not a repository file: it is the script Task 27 Step 14 writes from its `T27KIND` heredoc, extracted from the plan text and transliterated from that.

### Scan (every block a task creates or changes)

Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md @ 7c4d8668.
Post-plan code read from /home/ben/.cache/csr12p-cir9/tree (Tasks 1–25 applied); line numbers below are post-plan file lines.
One line per (task, step, file); `(n)` is the number of edit blocks the step makes in that file (sub-steps `Na`, `Nb`, … merged).
Classes: logic-bearing → candidate; glue; I/O-bound; config/declarative; tests. A block only partly logic-bearing names its logic-bearing part.
Embedded code is classed by its own language (Python expressions in blueprint YAML, CEL in the admission template, sh/bash/Python
inside Helm and scripts); the YAML/Helm around it is config.

#### Task 0
T0 S1 scratch `env.sh` (worktree and scratch setup) — config (setup; no repo code)

#### Tasks 1–25


##### Task 1

T1 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikAdminClientTests.cs` (6 edit(s)) — tests (citation source)
T1 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs` (1 edit(s)) — tests (citation source)
T1 S3 `Iverson.Server/Iverson.Api/Tenancy/IIdpAdminClient.cs` (1 edit(s)) — interface + record `IdpUserRecord` — config/declarative (types only)
T1 S3 `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs` (1 edit(s)) — whole file: `ListUsersByTenantAsync` (148–206), `GetUserAsync` (214–238), `ReadPk`/`TenantIdOf` (376–390) — logic-bearing → candidate `IdpAdminClient.TenantReads`; `AddGroupAsync`/`RemoveGroupAsync`/`PostGroupMembershipAsync` (266–292), `DeactivateUserAsync`, `ResolveGroupPkAsync`, `PublicLink`, `CreateUserAsync` (unchanged logic) — I/O-bound (HTTP calls whose behaviour lives in Authentik); class remarks — prose
T1 S3 `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs` (5 edit(s)) — 3c usings — glue; 3d–3f `user.Id` at the three IdP writes in `RemoveUser`/`SetTenantAdmin` — glue (pass-through of the checked record's pk); 3g `RequireUserInTenantAsync` + `UserNotInTenant` + `IsCanonicalUserPk` (123–141) — logic-bearing → candidate `TenantAdminGrpcService.RequireUserInTenantAsync`
T1 S6 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs` (1 edit(s)) — tests (citation source)

##### Task 2

T2 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/RateLimitInterceptorTests.cs` (1 edit(s)) — tests (citation source)
T2 S1 `Iverson.Server/Iverson.Api.Tests/RateLimiting/AdminRateLimitPipelineTests.cs` (1 edit(s)) — tests (citation source)
T2 S1 `Iverson.Server/Iverson.Api.Tests/RateLimiting/SharedCeilingRateLimiterTests.cs` (1 edit(s)) — tests (citation source)
T2 S1 `Iverson.Server/Iverson.Api.Tests/TracesRelayEndpointTests.cs` (2 edit(s)) — tests (citation source)
T2 S3 `Iverson.Server/Iverson.Api/RateLimiting/SharedCeilingRateLimiter.cs` (1 edit(s)) — `SharedCeilingRateLimiter` — glue (delegates every acquire to `RateLimiter.CreateChained(perCaller, sharedCeiling)`; disposal plumbing); the per-caller-then-ceiling behaviour lives in System.Threading.RateLimiting
T2 S4 `Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs` (1 edit(s)) — whole file: `AdminServices`, `_limiter`, `_adminLimiter` (13–37) and `Enforce` (58–74) — logic-bearing → candidate `RateLimitInterceptor.Enforce`; `UnaryServerHandler`/`ServerStreamingServerHandler` — glue
T2 S4 `Iverson.Server/Iverson.Api/Program.cs` (6 edit(s)) — 4b–4g: the `"traces"` chained policy (600/min shared ceiling), the `"admin"` 30/min per-`sub` policy and its pre-auth no-op twin, `RequireRateLimiting("admin")` on the three admin routes — glue/config (ASP.NET rate-limiter registration; behaviour lives in the library)
T2 S4 `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs` (4 edit(s)) — 4h–4k `RequireRateLimiting("admin")` on the console routes — config (endpoint metadata)
T2 S4 `Iverson.Server/Iverson.Api/Console/AdminConsoleMetricsEndpoint.cs` (1 edit(s)) — 4l `RequireRateLimiting("admin")` — config (endpoint metadata)

##### Task 3

T3 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs` (4 edit(s)) — tests (citation source)
T3 S1 `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs` (5 edit(s)) — tests (citation source)
T3 S3 `Iverson.Server/Iverson.Sql/InviteQuotaRepository.cs` (1 edit(s)) — `EnsureTableAsync` (15–24) — config/declarative (DDL); `TryReserveAsync` (27–37, the upsert SQL) — logic-bearing (SQL `ON CONFLICT … DO UPDATE … WHERE`) → candidate `InviteQuotaRepository.TryReserveAsync`
T3 S3 `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (1 edit(s)) — new interface `IInviteQuotaRepository` — config/declarative (types only)
T3 S3 `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs` (2 edit(s)) — 3c–3d parameter and `EnsureTableAsync` call — glue
T3 S3 `Iverson.Server/Iverson.Api/ApiLoginGrants.cs` (3 edit(s)) — 3e–3g the grant statement, the table list and `ReadinessProbeSql` constants — config/declarative (SQL text constants)
T3 S3 `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs` (2 edit(s)) — 3h constructor parameter — glue; 3i `InviteUser` reservation before the IdP call (29–53) — logic-bearing → candidate `TenantAdminGrpcService.InviteUser`
T3 S3 `Iverson.Server/Iverson.Api/Program.cs` (2 edit(s)) — 3j–3k DI registration and the bootstrap argument — glue

##### Task 4

T4 S1 `Iverson.Server/Iverson.Api.Tests/OperatorAuthorizationPolicyTests.cs` (1 edit(s)) — tests (citation source)
T4 S1 `Iverson.Server/Iverson.Api.Tests/SchemaAdminAuthorizationPolicyTests.cs` (1 edit(s)) — tests (citation source)
T4 S1 `Iverson.Server/Iverson.Api.Tests/ConsoleAdminAuthorizationPolicyTests.cs` (1 edit(s)) — tests (citation source)
T4 S3 `Iverson.Server/Iverson.Api/OperatorSubjects.cs` (1 edit(s)) — `OperatorSubjects` record + `FromConfiguration` (21–25, the empty-fallback-is-unset branch) — logic-bearing → part of candidate `OperatorAuthorizationPolicy.IsSatisfiedBy`
T4 S3 `Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs` (1 edit(s)) — `IsSatisfiedBy` (14–25) — logic-bearing → candidate `OperatorAuthorizationPolicy.IsSatisfiedBy`
T4 S3 `Iverson.Server/Iverson.Api/SchemaAdminAuthorizationPolicy.cs` (1 edit(s)) — `IsSatisfiedBy` (11–18) — logic-bearing → part of candidate `OperatorAuthorizationPolicy.IsSatisfiedBy`
T4 S3 `Iverson.Server/Iverson.Api/ConsoleAdminAuthorizationPolicy.cs` (1 edit(s)) — `IsSatisfiedBy` (10–12) — logic-bearing → part of candidate `OperatorAuthorizationPolicy.IsSatisfiedBy`
T4 S3 `Iverson.Server/Iverson.Api/Program.cs` (2 edit(s)) — 3e `Operator`/`SchemaAdmin` policy registrations, 3f the three acting-user `isOperator` call sites — glue (call the candidate functions)
T4 S5 `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs` (2 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs` (5 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/PrivilegedAdminEndpointTests.cs` (1 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs` (2 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/AdminConsoleActiveTenantPipelineTests.cs` (2 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/AdminConsoleEndpointsPipelineTests.cs` (4 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/AdminConsoleMetricsPipelineTests.cs` (1 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceAuthorizationPipelineTests.cs` (1 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationPipelineTests.cs` (4 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/TracesRelayEndpointTests.cs` (2 edit(s)) — tests (citation source)
T4 S5 `Iverson.Server/Iverson.Api.Tests/AdminConsoleRolesEndpointTests.cs` (1 edit(s)) — tests (citation source)
T4 S7 `Iverson.Server/Iverson.Api/Program.cs` (6 edit(s)) — 7a–7e `ConsoleAdmin` policy, `/v1/traces` `RequireAuthorization("ConsoleClient", "ConsoleAdmin")`, the Authentik client's 10 s `Timeout`, the fallback warning, comments — glue/config
T4 S7 `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs` (7 edit(s)) — 7f–7l `ConsoleAdmin` on `/schema` and `/data-volume`, the `/roles` route, `GetRoles` (two calls to the candidate functions), `ConsoleRolesResponse` — glue/config
T4 S9 `Iverson.Server/deploy/helm/iverson/values.yaml` (1 edit(s)) — config/declarative (Helm)
T4 S9 `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` (2 edit(s)) — config/declarative (Helm)
T4 S9 `Iverson.Server/deploy/helm/iverson/values-aws.ci-override.yaml` (1 edit(s)) — config/declarative (Helm)
T4 S9 `Iverson.Server/docker-compose.yml` (1 edit(s)) — config/declarative (compose)
T4 S9 scratch `$SCR` scripts: check_t4.py, t4_render.sh (render checks) — tests (citation source; scratch, never committed)
T4 S10 `Iverson.Server/Iverson.LoadTest/scripts/test_dialogue_patterns.py` (1 edit(s)) — tests (citation source)
T4 S11 scratch `$SCR` scripts: a2_mutate.py, t4_dotnet.sh (mutation runner) — tests (citation source; scratch, never committed)

##### Task 5

T5 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/TokenRevocationCacheTests.cs` (1 edit(s)) — tests (citation source)
T5 S3 `Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs` (3 edit(s)) — 3a class comment — prose; 3b `Skew` constant — config; 3c `IsRevokedAsync`'s predicate (35–42) — logic-bearing → candidate `TokenRevocationCache.IsRevokedAsync`
T5 S3 `Iverson.Server/Iverson.Api/Tenancy/ITokenRevocationCache.cs` (2 edit(s)) — doc comments — prose
T5 S5 `Iverson.Server/Iverson.Api.Tests/Tenancy/TenantStatusCacheTests.cs` (1 edit(s)) — tests (citation source)
T5 S7 `Iverson.Server/Iverson.Api/Tenancy/ITenantStatusCache.cs` (1 edit(s)) — new interface member `InvalidateAll` — config/declarative
T5 S7 `Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs` (1 edit(s)) — whole file: generation-keyed `KeyFor`, `GetStatusAsync`, `Invalidate`, `InvalidateAll` (36–52) — logic-bearing → candidate `TenantStatusCache.InvalidateAll`
T5 S7 `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs` (1 edit(s)) — tests (citation source)
T5 S9 `Iverson.Server/Iverson.Sql.Tests/TenantRepositoryTests.cs` (1 edit(s)) — tests (citation source)
T5 S9 `Iverson.Server/Iverson.Sql.Tests/TokenRevocationRepositoryTests.cs` (1 edit(s)) — tests (citation source)
T5 S11 `Iverson.Server/Iverson.Sql/AuthInvalidation.cs` (1 edit(s)) — channel and `RETURNING pg_notify(...)` clause constants — config/declarative (SQL text)
T5 S11 `Iverson.Server/Iverson.Sql/TokenRevocationRepository.cs` (1 edit(s)) — `RevokeAsync` appends the notify clause — I/O-bound (the notification is Postgres's; no branch in the code)
T5 S11 `Iverson.Server/Iverson.Sql/TenantRepository.cs` (2 edit(s)) — the five writes append the notify clause — I/O-bound (as above)
T5 S13 `Iverson.Server/Iverson.Api.Tests/Helpers/AuthTestWebApplicationFactory.cs` (3 edit(s)) — tests (citation source)
T5 S13 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthInvalidationListenerTests.cs` (1 edit(s)) — tests (citation source)
T5 S13 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthInvalidationListenerPostgresIntegrationTests.cs` (1 edit(s)) — tests (citation source)
T5 S15 `Iverson.Server/Iverson.Api/Tenancy/AuthInvalidationListener.cs` (1 edit(s)) — `ExecuteAsync` reconnect/back-off loop + `InvalidateCaches` (43–97) — logic-bearing → candidate `AuthInvalidationListener.ExecuteAsync`; `ListenerConnectionString` (90–91) — glue (Npgsql builder); meter/counter — config
T5 S15 `Iverson.Server/Iverson.Api/Program.cs` (2 edit(s)) — 15b–15c meter name and the api-role `AddHostedService` — glue
T5 S17 `Iverson.Server/Iverson.Api.Tests/Grpc/StreamRevalidationInterceptorTests.cs` (1 edit(s)) — tests (citation source)
T5 S19 `Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCheck.cs` (1 edit(s)) — `IsRevokedAsync` (13–24: no `sub` → not revoked, `iat` parse) — logic-bearing → modelled inside candidate `StreamRevalidationInterceptor.RecheckAsync`
T5 S19 `Iverson.Server/Iverson.Api/Grpc/StreamRevalidationInterceptor.cs` (1 edit(s)) — `ServerStreamingServerHandler` (28–43) — glue; `RecheckAsync` (45–55) and `RevalidatingServerStreamWriter.WriteAsync` ×2 (75–85) — logic-bearing → candidate `StreamRevalidationInterceptor.RecheckAsync`; `WriteOptions` pass-through — glue
T5 S19 `Iverson.Server/Iverson.Api/Program.cs` (3 edit(s)) — 19c–19d interceptor registration; `RejectRevokedTokenAsync` now delegates to `TokenRevocationCheck` — glue
T5 S21 scratch `$SCR` scripts: t5_dotnet.sh (mutation runner) — tests (citation source; scratch, never committed)

##### Task 6

T6 S2 `Iverson.AdminUI/src/auth/roles.test.tsx` (1 edit(s)) — tests (citation source)
T6 S2 `Iverson.AdminUI/src/layout/Sidebar.test.tsx` (1 edit(s)) — tests (citation source)
T6 S2 `Iverson.AdminUI/src/router.test.tsx` (1 edit(s)) — tests (citation source)
T6 S2 `Iverson.AdminUI/src/pages/LandingPage.test.tsx` (1 edit(s)) — tests (citation source)
T6 S2 `Iverson.AdminUI/src/auth/RequireGroup.test.tsx` (1 edit(s)) — tests (deleted)
T6 S4 `Iverson.AdminUI/src/api/types.ts` (3 edit(s)) — `ConsoleRolesResponse` and comments — config/declarative (types)
T6 S4 `Iverson.AdminUI/src/api/console.ts` (7 edit(s)) — `fetchRoles` and comments — glue (one `getJson` call)
T6 S4 `Iverson.AdminUI/src/auth/roles.tsx` (1 edit(s)) — `RolesProvider` state mapping (40–52), `hasRole` (55–61), `RequireRole` (74–112) — logic-bearing → candidate `roles.RequireRole`; context/hook plumbing — glue
T6 S4 `Iverson.AdminUI/src/router.tsx` (1 edit(s)) — route table — config/declarative (each guard is `RequireRole`)
T6 S4 `Iverson.AdminUI/src/layout/Sidebar.tsx` (1 edit(s)) — renders two links on `hasRole` — glue (the decision is the candidate's `hasRole`)
T6 S4 `Iverson.AdminUI/src/pages/LandingPage.tsx` (1 edit(s)) — cards shown on `hasRole(..., "operator")` — glue (the decision is the candidate's `hasRole`)
T6 S4 `Iverson.AdminUI/src/auth/RequireGroup.tsx` (1 edit(s)) — deleted file — no code produced (excluded)
T6 S4 `Iverson.AdminUI/src/auth/groups.ts` (1 edit(s)) — deleted file — no code produced (excluded)
T6 S4 `Iverson.AdminUI/src/pages/PerformancePage.tsx` (1 edit(s)) — deleted file — no code produced (excluded)
T6 S4 `Iverson.AdminUI/src/pages/StoragePage.tsx` (1 edit(s)) — deleted file — no code produced (excluded)
T6 S5 `Iverson.AdminUI/src/widgets/TenantRosterWidget.test.tsx` (1 edit(s)) — tests (citation source)
T6 S5 `Iverson.AdminUI/src/widgets/QdrantCollectionsWidget.test.tsx` (1 edit(s)) — tests (citation source)
T6 S5 `Iverson.AdminUI/src/widgets/FanOutBacklogWidget.test.tsx` (1 edit(s)) — tests (citation source)
T6 S5 `Iverson.AdminUI/src/widgets/metricsFixture.ts` (1 edit(s)) — tests (citation source)

##### Task 7

T7 S1 scratch `$SCR` scripts: check_t7.py, a3_render.sh, a3_ci.sh, a3_mut.sh, t7_wait_probe.sh (render checks; the wait-for-migration probe against a throwaway Postgres) — tests (citation source; scratch, never committed)
T7 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml` (4 edit(s)) — replicas/anti-affinity templating — config (Helm); the `wait-for-migration` init container's embedded Python `migrated()` and wait loop (~61–103) — logic-bearing (Python) → candidate `deployment-server.wait-for-migration`
T7 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/pdb.yaml` (1 edit(s)) — config/declarative (Helm)
T7 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-worker.yaml` (4 edit(s)) — `strategy: Recreate`, the `migrate` init container (`python -m lifecycle.migrate`, no logic of ours) — config (Helm)
T7 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml` (2 edit(s)) — config/declarative (Helm)
T7 S3 `Iverson.Server/deploy/helm/iverson/values-aws.yaml` (1 edit(s)) — config/declarative (Helm)
T7 S3 `Iverson.Server/deploy/helm/iverson/values-azure.yaml` (1 edit(s)) — config/declarative (Helm)
T7 S3 `Iverson.Server/deploy/helm/iverson/values-gcp.yaml` (1 edit(s)) — config/declarative (Helm)
T7 S3 `Iverson.Server/deploy/helm/iverson/charts/jaeger/templates/deployment.yaml` (1 edit(s)) — config/declarative (Helm)
T7 S3 `Iverson.Server/deploy/helm/iverson/charts/jaeger/values.yaml` (1 edit(s)) — config/declarative (Helm)

##### Task 8

T8 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorFederationBlueprintTests.cs` (5 edit(s)) — tests (citation source)
T8 S1 scratch `$SCR` scripts: check_t8.py, check_t8_files.py, t8_nginx.sh (render/file checks; the :9080 route table check) — tests (citation source; scratch, never committed)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/_helpers.tpl` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-local.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-laptop.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-aws.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-azure.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-gcp.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-aws.ci-override.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-azure.ci-override.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/values-gcp.ci-override.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml` (3 edit(s)) — `$issuer` include and Helm text — config; the `orchestrator-workload` expression's new `iss` conjunct (Python, ~101–118) — logic-bearing → candidate `blueprints-configmap.orchestrator-workload`; the `orchestrator-tokenreview` expression (Python, 141–168) — logic-bearing → candidate `blueprints-configmap.orchestrator-tokenreview`; its binding at order 2 — config
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml` (3 edit(s)) — `$issuer` include — config; the `admin-automation-workload` expression's new `iss` conjunct (Python, 775–787) — logic-bearing → candidate `secret-service-clients.admin-automation-workload`; comments — prose
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml` (2 edit(s)) — the `tokenreview-relay` native sidecar (`kubectl proxy` flags) and its projected-token volume — config (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/clusterrole-tokenreview.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-worker.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap.yaml` (1 edit(s)) — config/declarative (Helm)
T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml` (4 edit(s)) — `proxy_bind 127.0.0.2` on both server blocks, HSTS `add_header` — config (nginx); the `:9080` server block's location set (72–115) — logic-bearing (nginx location selection) → candidate `configmap-tls-proxy.public-locations`; the pre-existing geo/map X-Forwarded-For selection is unchanged
T8 S3 `Iverson.Server/deploy/terraform/modules/cluster-aws/outputs.tf` (1 edit(s)) — config/declarative (Terraform)
T8 S3 `Iverson.Server/deploy/terraform/modules/cluster-gcp/outputs.tf` (1 edit(s)) — config/declarative (Terraform)
T8 S3 `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf` (1 edit(s)) — config/declarative (Terraform)
T8 S3 `Iverson.Server/deploy/terraform/modules/cluster-azure/outputs.tf` (1 edit(s)) — config/declarative (Terraform)
T8 S3 `Iverson.Server/deploy/terraform/aws/main.tf` (1 edit(s)) — config/declarative (Terraform)
T8 S3 `Iverson.Server/deploy/terraform/azure/main.tf` (1 edit(s)) — config/declarative (Terraform)
T8 S3 `Iverson.Server/deploy/terraform/gcp/main.tf` (1 edit(s)) — config/declarative (Terraform)
T8 S3 `Iverson.Server/docker-compose.yml` (1 edit(s)) — config/declarative (compose)
T8 S3 `Iverson.Server/deploy/compose-tls-proxy.conf` (1 edit(s)) — `proxy_bind 127.0.0.2` and `proxy_set_header X-Forwarded-For $remote_addr` on compose's only location — config (nginx, no location choice)

##### Task 9

T9 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikContainerFixture.cs` (5 edit(s)) — tests (citation source)
T9 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikRecoveryFlowIntegrationTests.cs` (7 edit(s)) — tests (citation source)
T9 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikOrchestratorRoleBlueprintTests.cs` (1 edit(s)) — tests (citation source)
T9 S1 scratch `$SCR` scripts: check_t9.py, check_t9_files.py (blueprint render checks) — tests (citation source; scratch, never committed)
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/reputation.yaml` (1 edit(s)) — 3a whole file: the round-11 address-only expression and its binding marked `state: absent`, the order-12 deny-stage binding to `default-authentication-flow` — config (blueprint YAML; the throttle expression itself is in recovery-flow.yaml)
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/recovery-flow.yaml` (2 edit(s)) — the `iverson-login-throttle` expression (Python, 71–79) — logic-bearing → candidate `recovery-flow.login-throttle`; the deny stage, its order-0 binding and comments — config (blueprint YAML)
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap.yaml` (1 edit(s)) — `AUTHENTIK_REPUTATION__EXPIRY` — config (Helm)
T9 S3 `Iverson.Server/docker-compose.yml` (2 edit(s)) — config/declarative (compose)
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml` (3 edit(s)) — 3x the `mfa-enforcement.yaml` `deny` rewrite (Helm `replace`), the `operators` group `state: absent` entry, the `initial-setup.yaml` document (its policy is the constant `return False`) — config (Helm/blueprint)
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/mfa-enforcement.yaml` (1 edit(s)) — comment only — prose
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/operators-group.yaml` (1 edit(s)) — deleted file — no code produced (excluded)
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml` (8 edit(s)) — the `console-admins` expression (Python with a Helm-rendered list, 352–358) — logic-bearing → candidate `secret-service-clients.console-admins`; its binding, `grant_types`, the `tenant-admins` entry with `permissions:`, the moved entries and comments — config (blueprint YAML)
T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` (4 edit(s)) — the compose `console-admins` expression (Python, 194–199) — logic-bearing → candidate `compose-service-clients.console-admins`; binding, `tenant-admins` permissions entry, comments — config

##### Task 10

T10 S1 `docs/runbooks/operator-access-onboarding.md` (3 edit(s)) — config/declarative (Markdown prose)
T10 S1 `docs/runbooks/csr11-identity-plane.md` (4 edit(s)) — config/declarative (Markdown prose)
T10 S1 `docs/runbooks/admin-console-landing-page-usage.md` (8 edit(s)) — config/declarative (Markdown prose)
T10 S1 `docs/runbooks/grpc-admin-auth-cutover.md` (2 edit(s)) — config/declarative (Markdown prose)
T10 S1 `docs/user-management-and-security.md` (5 edit(s)) — config/declarative (Markdown prose)
T10 S1 `Iverson.AdminUI/README.md` (3 edit(s)) — config/declarative (Markdown prose)
T10 S1 `docs/authentik-migration-startup-race.md` (1 edit(s)) — config/declarative (Markdown prose)

##### Task 11

T11 S1 scratch `$SCR` scripts: b_render.sh, b_ci.sh, b_tf.sh, check_t11.py, t11_guards.sh, t11_mutate.sh, vapsim/go.mod, vapsim/main.go (render checks; vapsim evaluates the rendered CEL with cel-go on 34 requests) — tests (citation source; scratch, never committed)
T11 S3 `Iverson.Server/deploy/helm/iverson/templates/admission-policy.yaml` (1 edit(s)) — the render guards on `admission.operatorGroups` (18–26) — config (Helm `fail`); the VAP's CEL `variables` and six `validations` (43–126) — logic-bearing (CEL) → candidate `admission-policy.validations`; `matchConstraints`, `messageExpression`s, the binding — config
T11 S3 `Iverson.Server/deploy/helm/iverson/values.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/helm/iverson/values-local.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/helm/iverson/values-laptop.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/helm/iverson/values-aws.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/helm/iverson/values-azure.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/helm/iverson/values-gcp.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/helm/iverson/values-azure.ci-override.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/helm/iverson/values-gcp.ci-override.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S3 `Iverson.Server/deploy/kind/setup.sh` (1 edit(s)) — the printed `helm upgrade … --kube-as-*` hint — config (prose string)
T11 S3 `Iverson.Server/deploy/kind/setup.ps1` (1 edit(s)) — the printed `helm upgrade … --kube-as-*` hint — config (prose string)
T11 S6 `Iverson.Server/deploy/scripts/test_mint_acting_user_token.py` (2 edit(s)) — tests (citation source)
T11 S6 `Iverson.Server/deploy/scripts/mint_acting_user_token.py` (3 edit(s)) — `KIND_OPERATOR_IMPERSONATION`, `PortForward.argv` and its `impersonation` parameter — glue (argv assembly)
T11 S7 `Iverson.Server/Iverson.LoadTest.Tests/Auth/AdminAutomationCredentialsTests.cs` (2 edit(s)) — tests (citation source)
T11 S7 `Iverson.Server/Iverson.LoadTest/Auth/AdminAutomationCredentials.cs` (2 edit(s)) — two error-message strings — prose
T11 S8 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/serviceaccount-admin-automation.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S8 `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml` (1 edit(s)) — config/declarative (Helm)
T11 S8 `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` (1 edit(s)) — three error-message hints — prose
T11 S9 `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf` (1 edit(s)) — config/declarative (Terraform)
T11 S9 `Iverson.Server/deploy/terraform/modules/cluster-aws/variables.tf` (1 edit(s)) — config/declarative (Terraform)
T11 S9 `Iverson.Server/deploy/terraform/aws/main.tf` (2 edit(s)) — config/declarative (Terraform)
T11 S9 `Iverson.Server/deploy/terraform/azure/main.tf` (1 edit(s)) — config/declarative (Terraform)
T11 S9 `Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf` (1 edit(s)) — config/declarative (Terraform)
T11 S9 `Iverson.Server/deploy/terraform/modules/cluster-gcp/variables.tf` (1 edit(s)) — config/declarative (Terraform)
T11 S9 `Iverson.Server/deploy/terraform/gcp/main.tf` (2 edit(s)) — config/declarative (Terraform)
T11 S10 `docs/runbooks/operators-group.md` (1 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/csr11-identity-plane.md` (4 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/csr10-infrastructure-cutover.md` (5 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/grpc-admin-auth-cutover.md` (4 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/kind-cluster-troubleshooting.md` (2 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/csr12-server.md` (3 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/rls-force-maintenance-role-cutover.md` (2 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/chunk-collection-cleanup-2026-07.md` (2 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/csr11-transport-network.md` (2 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/user-management-and-security.md` (2 edit(s)) — config/declarative (Markdown prose)
T11 S10 `docs/runbooks/operator-access-onboarding.md` (5 edit(s)) — config/declarative (Markdown prose)

##### Task 12

T12 S1 scratch `$SCR` scripts: check_t12.py, chart_mutate.sh, t12_mutate.sh — tests (citation source; scratch, never committed)
T12 S3 `Iverson.Server/deploy/helm/iverson/charts/kafka/templates/kafka.yaml` (1 edit(s)) — config/declarative (Helm)
T12 S3 `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml` (1 edit(s)) — config/declarative (Helm)

##### Task 13

T13 S1 `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs` (3 edit(s)) — tests (citation source)
T13 S1 scratch `$SCR` scripts: check_t13.py, t13_mutate.sh — tests (citation source; scratch, never committed)
T13 S3 `Iverson.Server/Iverson.Api/Program.cs` (4 edit(s)) — `RequireListenerPort(8082)` on `/metrics` and `/build` — config (endpoint metadata); the two global-limiter exemptions `Port is 8081 or 8082` — trivially logic-bearing one-line constant pattern; excluded as glue
T13 S3 `Iverson.Server/Iverson.Api/appsettings.json` (1 edit(s)) — config/declarative
T13 S3 `Iverson.Server/Iverson.Api/Dockerfile` (1 edit(s)) — config/declarative
T13 S3 `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` (2 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml` (1 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/helm/iverson/charts/api/templates/headless-service.yaml` (2 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/helm/iverson/charts/worker/templates/headless-service.yaml` (1 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/helm/iverson/charts/api/templates/service.yaml` (1 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/helm/iverson/charts/worker/templates/service.yaml` (1 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/helm/iverson/templates/prometheus-config.yaml` (1 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml` (5 edit(s)) — config/declarative (Helm)
T13 S3 `Iverson.Server/deploy/prometheus/prometheus.local.yml` (2 edit(s)) — config/declarative (YAML)
T13 S3 `Iverson.Server/docker-compose.yml` (2 edit(s)) — config/declarative (compose)
T13 S3 `Iverson.Server/Iverson.LoadTest/Program.cs` (1 edit(s)) — `IVERSON_HTTP_URL` default port 8082 — config (default constant)
T13 S3 `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py` (2 edit(s)) — `--build-url` default port and docstring — config (default constant)
T13 S3 `Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py` (1 edit(s)) — `--build-url` default port — config (default constant)
T13 S3 `Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py` (1 edit(s)) — `--build-url` default port — config (default constant)
T13 S3 `docs/runbooks/csr11-transport-network.md` (1 edit(s)) — config/declarative (Markdown prose)
T13 S3 `Iverson.Server/Iverson.Api.Tests/Tenancy/AuthentikTrustTests.cs` (1 edit(s)) — tests (citation source)

##### Task 14

T14 S1 scratch `$SCR` scripts: check_t14.py, t14_fallback.sh, t14_mutate.sh — tests (citation source; scratch, never committed)
T14 S3 `Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/Chart.yaml` (1 edit(s)) — config/declarative (Helm chart under Terraform)
T14 S3 `Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values.yaml` (1 edit(s)) — config/declarative (Helm chart under Terraform)
T14 S3 `Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/values-kind.yaml` (1 edit(s)) — config/declarative (Helm chart under Terraform)
T14 S3 `Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/_helpers.tpl` (1 edit(s)) — config/declarative (Helm chart under Terraform)
T14 S3 `Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/networkpolicies.yaml` (1 edit(s)) — config/declarative (Helm chart under Terraform)
T14 S4 `Iverson.Server/deploy/terraform/modules/operators/main.tf` (2 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/modules/operators/variables.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/modules/cluster-aws/outputs.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/modules/cluster-azure/outputs.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/modules/cluster-gcp/outputs.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/aws/main.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/azure/main.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/gcp/main.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf` (1 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/terraform/modules/cluster-azure/main.tf` (3 edit(s)) — config/declarative (Terraform)
T14 S4 `Iverson.Server/deploy/kind/setup.sh` (2 edit(s)) — 4d the `--only <phase>` argument parser (44–52) and `phase_operator_namespaces` (installs) — parser logic-bearing (bash) → candidate `setup.sh.guards`; the phase body — I/O-bound (kubectl/helm)
T14 S4 `Iverson.Server/deploy/kind/setup.ps1` (2 edit(s)) — 4d the `--only` argument parser (43–51) and `Install-OperatorNamespacePolicies` — parser logic-bearing (PowerShell) → candidate `setup.ps1.guards`; the phase body — I/O-bound
T14 S4 `.github/workflows/deploy-validate.yml` (2 edit(s)) — config/declarative (CI)
T14 S4 `.gitlab-ci.yml` (2 edit(s)) — config/declarative (CI)
T14 S5 `Iverson.Server/deploy/helm/iverson/values.yaml` (1 edit(s)) — config/declarative (Helm)
T14 S5 `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml` (5 edit(s)) — config/declarative (Helm)
T14 S5 `docs/runbooks/csr10-infrastructure-cutover.md` (1 edit(s)) — config/declarative (Markdown prose)

##### Task 15

T15 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs` (4 edit(s)) — tests (citation source)
T15 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs` (1 edit(s)) — tests (citation source)
T15 S1 `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksReadinessIntegrationTests.cs` (1 edit(s)) — tests (citation source)
T15 S3 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksHealthCheckerTests.cs` (1 edit(s)) — tests (citation source)
T15 S4 `Iverson.Server/Iverson.StarRocks/EngagementHealthChecker.cs` (1 edit(s)) — `BackendProbeSql`/`BackendProbeTimeoutSeconds`/`CreateBackendProbe` — config; `AnyBackendReadyAsync` (73–83, two rows) with the unchanged `CheckHealthAsync`/`ClassifyConnectionException` it feeds — logic-bearing → candidate `EngagementHealthChecker.CheckHealthAsync`
T15 S4 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksContainerReadinessTests.cs` (1 edit(s)) — tests (citation source)
T15 S4 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml` (3 edit(s)) — 4c the dropped `GRANT OPERATE` and the added `REVOKE OPERATE ON SYSTEM FROM USER` statements inside the job's embedded `sh` batch — logic-bearing (statement order of the sh script) → candidate `job-create-user.create-user`
T15 S4 `Iverson.Server/docker-compose.yml` (3 edit(s)) — `starrocks-init`'s embedded SQL: `GRANT OPERATE` → `REVOKE OPERATE` — config (a fixed statement list, no branch)
T15 S5 inline Python check of the job and compose statements — tests

##### Task 16

T16 S1 scratch `$SCR` scripts: check_t16.py, t16_kind.sh (stub-kind version gate check), t16_mutate.sh — tests (citation source; scratch, never committed)
T16 S3 `Iverson.Server/deploy/terraform/modules/cluster-aws/variables.tf` (1 edit(s)) — config/declarative (Terraform)
T16 S3 `Iverson.Server/deploy/terraform/modules/cluster-azure/variables.tf` (1 edit(s)) — config/declarative (Terraform)
T16 S3 `Iverson.Server/deploy/terraform/modules/cluster-gcp/variables.tf` (1 edit(s)) — config/declarative (Terraform)
T16 S3 `Iverson.Server/deploy/terraform/modules/cluster-aws/main.tf` (4 edit(s)) — config/declarative (Terraform)
T16 S3 `Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml` (1 edit(s)) — config/declarative (Helm chart under Terraform)
T16 S3 `.github/workflows/deploy-validate.yml` (1 edit(s)) — config/declarative (CI)
T16 S3 `.gitlab-ci.yml` (1 edit(s)) — config/declarative (CI)
T16 S3 `Iverson.Server/deploy/kind/kind-config.yaml` (1 edit(s)) — config/declarative (YAML)
T16 S3 `Iverson.Server/deploy/kind/setup.sh` (1 edit(s)) — 3c the kind version gate (4–11) — logic-bearing (bash) → candidate `setup.sh.guards`
T16 S3 `Iverson.Server/deploy/kind/setup.ps1` (1 edit(s)) — 3c the kind version gate (3–9) — logic-bearing (PowerShell) → candidate `setup.ps1.guards`

##### Task 17

T17 S1 scratch `$SCR` scripts: t17/check_t17.py, t17/t17_enroll.sh (stub-kubectl `ambient_enroll` cases), stub kubectl, t17/t17_mutate.sh — tests (citation source; scratch, never committed)
T17 S3 `Iverson.Server/deploy/terraform/modules/operators/charts/istio/vendor-istio-charts.sh` (1 edit(s)) — download, `sha256sum -c`, extract, copy — I/O-bound (bash; the one check is `sha256sum`'s)
T17 S3 runs `vendor-istio-charts.sh`, which writes `Iverson.Server/deploy/terraform/modules/operators/charts/istio/{base,istio-discovery,istio-cni,ztunnel}/` (Istio 1.31.2's charts, copied unmodified from the pinned tarball) — config/declarative (third-party Helm charts)
T17 S4 `Iverson.Server/deploy/kind/istio/istiod.yaml` (1 edit(s)) — config/declarative (YAML)
T17 S4 `Iverson.Server/deploy/kind/istio/istio-cni.yaml` (1 edit(s)) — config/declarative (YAML)
T17 S4 `Iverson.Server/deploy/kind/istio/ztunnel.yaml` (1 edit(s)) — config/declarative (YAML)
T17 S5 `Iverson.Server/deploy/kind/setup.sh` (4 edit(s)) — 5a–5d `PHASES`, `phase_istio` (I/O-bound helm installs), `ambient_enroll` (106–115) — logic-bearing (bash) → candidate `setup.sh.guards`; `phase_ambient_labels` and the fresh-install calls — glue
T17 S5 `Iverson.Server/deploy/kind/setup.ps1` (4 edit(s)) — 5e–5h `$Phases`, `Install-Istio` (I/O-bound), `Add-AmbientLabels` (96–110) — logic-bearing (PowerShell) → candidate `setup.ps1.guards`; dispatch `switch` and fresh-install calls — glue
T17 S6 `Iverson.Server/deploy/terraform/modules/operators/main.tf` (5 edit(s)) — config/declarative (Terraform)
T17 S7 `Iverson.Server/deploy/helm/iverson/values-laptop.yaml` (1 edit(s)) — config/declarative (Helm)

##### Task 18

T18 S1 scratch `$SCR` scripts: t18/t18_render.sh, t18_schemas.sh, t18_ci.sh, add15008.py, check_t18.py, t18_mutate.sh (add15008.py is also run by Step 5 to edit networkpolicies.yaml: a one-off rewriting tool, see T18 S5) — tests (citation source; scratch, never committed)
T18 S3 `Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl` (1 edit(s)) — config/declarative (Helm)
T18 S3 `Iverson.Server/deploy/helm/iverson/values.yaml` (1 edit(s)) — config/declarative (Helm)
T18 S3 `Iverson.Server/deploy/helm/iverson/values-local.yaml` (2 edit(s)) — config/declarative (Helm)
T18 S3 `Iverson.Server/deploy/helm/iverson/values-laptop.yaml` (2 edit(s)) — config/declarative (Helm)
T18 S4 `Iverson.Server/deploy/helm/iverson/templates/networkpolicies.yaml` (19 edit(s)) — config/declarative (Helm)
T18 S5 runs `add15008.py` over `templates/networkpolicies.yaml` and `templates/_helpers.tpl` (adds `{ protocol: TCP, port: 15008 }` to every port list that lacks it) — config/declarative (the produced output is NetworkPolicy YAML; the rewriting tool is scratch, Step 1's)
T18 S6 `Iverson.Server/deploy/helm/iverson/templates/mesh-policies.yaml` (1 edit(s)) — config/declarative (Helm)
T18 S7 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/serviceaccounts.yaml` (1 edit(s)) — config/declarative (Helm)
T18 S7 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/starrockscluster.yaml` (2 edit(s)) — config/declarative (Helm)
T18 S7 `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml` (1 edit(s)) — config/declarative (Helm)
T18 S8 `Iverson.Server/deploy/terraform/modules/operators/charts/operator-namespace-policies/templates/peerauthentication-cnpg.yaml` (1 edit(s)) — config/declarative (Helm chart under Terraform)
T18 S8 `Iverson.Server/deploy/terraform/modules/operators/main.tf` (2 edit(s)) — config/declarative (Terraform)
T18 S8 `.github/workflows/deploy-validate.yml` (1 edit(s)) — config/declarative (CI)
T18 S8 `.gitlab-ci.yml` (1 edit(s)) — config/declarative (CI)

##### Task 19

T19 S1 `Iverson.Server/Iverson.Api.Tests/Helpers/HstsTestWebApplicationFactories.cs` (1 edit(s)) — tests (citation source)
T19 S1 `Iverson.Server/Iverson.Api.Tests/HstsPipelineTests.cs` (1 edit(s)) — tests (citation source)
T19 S1 scratch `$SCR` scripts: t19/check_t19.py, t19/t19_job.sh (stub-mysql run of the create-user Job), stub mysql, t19/t19_mutate.sh — tests (citation source; scratch, never committed)
T19 S3 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/starrockscluster.yaml` (2 edit(s)) — `MYSQL_PWD` env on FE/BE and the TLS settings — config (Helm/CR)
T19 S3 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml` (3 edit(s)) — 3c–3e `FE_REPLICAS`, `SET PASSWORD` moved before the BE/FE wait, the wait on both counts, the batch without `SET PASSWORD` (embedded `sh`, 62–111) — logic-bearing → candidate `job-create-user.create-user`
T19 S3 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/configmap-fe.yaml` (1 edit(s)) — `ssl_force_secure_transport = true` and the header comment — config (fe.conf)
T19 S4 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/job-revoke-cross-db.yaml` (2 edit(s)) — `PGSSLMODE`/`PGSSLROOTCERT` env and the CA volume — config (Helm)
T19 S5 `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` (1 edit(s)) — config/declarative (Helm)
T19 S5 `Iverson.Server/Iverson.Api/Program.cs` (2 edit(s)) — `Hsts:Enabled` read and `AddHsts`/`UseHsts` under it — glue/config (ASP.NET registration)

##### Task 20

T20 S1 scratch `$SCR` scripts: t20/check_t20.py, check_t20_kind.py, t20_guards.sh, t20_mutate.sh — tests (citation source; scratch, never committed)
T20 S3 `Iverson.Server/deploy/kind/edge-gateway.yaml` (1 edit(s)) — config/declarative (YAML)
T20 S3 `Iverson.Server/deploy/kind/setup.sh` (4 edit(s)) — 3b removes the ingress-nginx install; 3d–3e `PHASES` gains `gateway`, `phase_gateway` (apply + a 30×2 s existence wait + rollout status) — I/O-bound; the `--only` dispatch (136–140) — part of candidate `setup.sh.guards`
T20 S3 `Iverson.Server/deploy/kind/setup.ps1` (6 edit(s)) — 3f removes ingress-nginx; 3h–3i `New-EdgeGateway` (the certificate-if-missing branch, apply, existence wait) — I/O-bound; `$Phases` gains `gateway` — part of candidate `setup.ps1.guards`
T20 S4 `Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl` (5 edit(s)) — config/declarative (Helm)
T20 S4 `Iverson.Server/deploy/helm/iverson/charts/api/templates/_helpers.tpl` (1 edit(s)) — config/declarative (Helm)
T20 S4 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/_helpers.tpl` (1 edit(s)) — config/declarative (Helm)
T20 S4 `Iverson.Server/deploy/helm/iverson/templates/prometheus-config.yaml` (1 edit(s)) — config/declarative (Helm)
T20 S5 `Iverson.Server/deploy/helm/iverson/charts/api/templates/ingress.yaml` (1 edit(s)) — config/declarative (Helm)
T20 S5 `Iverson.Server/deploy/helm/iverson/charts/admin-ui/templates/ingress.yaml` (1 edit(s)) — config/declarative (Helm)
T20 S5 `Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml` (1 edit(s)) — config/declarative (Helm)
T20 S5 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/ingress.yaml` (3 edit(s)) — config/declarative (Helm)
T20 S6 `Iverson.Server/deploy/helm/iverson/charts/api/templates/secret-internal-tls.yaml` (2 edit(s)) — config/declarative (Helm)
T20 S6 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-internal-tls.yaml` (2 edit(s)) — config/declarative (Helm)
T20 S6 `Iverson.Server/deploy/helm/iverson/templates/gateway-istio.yaml` (1 edit(s)) — config/declarative (Helm)
T20 S6 `Iverson.Server/deploy/helm/iverson/templates/_validate.tpl` (1 edit(s)) — config/declarative (Helm)
T20 S7 `Iverson.Server/deploy/helm/iverson/values-local.yaml` (2 edit(s)) — config/declarative (Helm)
T20 S7 `Iverson.Server/deploy/helm/iverson/values-laptop.yaml` (1 edit(s)) — config/declarative (Helm)
T20 S7 `Iverson.Server/deploy/helm/iverson/charts/api/values.yaml` (1 edit(s)) — config/declarative (Helm)

##### Task 21

T21 S1 scratch `$SCR` scripts: t21/check_t21.py, check_t21_tf.py, t21_guards.sh, t21_mutate.sh — tests (citation source; scratch, never committed)
T21 S3 `Iverson.Server/deploy/terraform/modules/cluster-gcp/main.tf` (1 edit(s)) — config/declarative (Terraform)
T21 S3 `Iverson.Server/deploy/terraform/gcp/main.tf` (1 edit(s)) — config/declarative (Terraform)
T21 S3 `Iverson.Server/deploy/terraform/gcp/waf.tf` (1 edit(s)) — config/declarative (Terraform)
T21 S4 `Iverson.Server/deploy/helm/iverson/templates/gateway-gke.yaml` (1 edit(s)) — config/declarative (Helm)
T21 S5 `Iverson.Server/deploy/helm/iverson/charts/api/templates/service.yaml` (3 edit(s)) — config/declarative (Helm)
T21 S5 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/service.yaml` (2 edit(s)) — config/declarative (Helm)
T21 S5 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/backendconfig.yaml` (1 edit(s)) — deleted file — no code produced (excluded)
T21 S5 `Iverson.Server/deploy/helm/iverson/templates/_helpers.tpl` (5 edit(s)) — config/declarative (Helm)
T21 S5 `Iverson.Server/deploy/helm/iverson/templates/_validate.tpl` (2 edit(s)) — config/declarative (Helm)
T21 S5 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/ingress.yaml` (1 edit(s)) — config/declarative (Helm)
T21 S5 `Iverson.Server/deploy/helm/iverson/charts/authentik/values.yaml` (1 edit(s)) — config/declarative (Helm)
T21 S5 `Iverson.Server/deploy/helm/iverson/charts/api/templates/admin-api-ingress.yaml` (1 edit(s)) — config/declarative (Helm)
T21 S6 `Iverson.Server/deploy/helm/iverson/values.yaml` (1 edit(s)) — config/declarative (Helm)
T21 S6 `Iverson.Server/deploy/helm/iverson/values-gcp.yaml` (5 edit(s)) — config/declarative (Helm)
T21 S6 `Iverson.Server/deploy/helm/iverson/values-gcp.ci-override.yaml` (1 edit(s)) — config/declarative (Helm)

##### Task 22

T22 S1 scratch `$SCR` scripts: check_t22_compose.py, t22_gen.sh (generate-compose-secrets checks), t22_fe.sh (fe-tls-entrypoint checks), t22_mutate.sh — tests (citation source; scratch, never committed)
T22 S3 `scripts/generate-compose-secrets.sh` (1 edit(s)) — the `LEAVES` table, the `renew` decision loop and the regenerate-everything branch (53–89) — logic-bearing (bash) → candidate `generate-compose-secrets.renew`; the openssl calls — I/O-bound
T22 S4 `Iverson.Server/deploy/postgres/tls-entrypoint.sh` (1) — glue (three `install` copies, then `exec docker-entrypoint.sh … -c ssl=on …`; no branch). Note: its fence is ```sh after a lead `Create `…` (Verified assumption 74's wrapper, …):`, which the skill's replay.py does not read as a create: it classes the block as a run block
T22 S4 `Iverson.Server/deploy/starrocks/fe-tls-entrypoint.sh` (1 edit(s)) — keystore export — I/O-bound; the append-each-setting-once loop (17–24) — logic-bearing (bash) → candidate `fe-tls-entrypoint.settings`
T22 S5 `Iverson.Server/docker-compose.yml` (21 edit(s)) — 5a–5u TLS mounts, entrypoints, connection strings, Kestrel endpoints, the worker's `CMD-SHELL` healthcheck pipeline (`openssl s_client … | grep -q " 200 "`, a fixed pipeline with no branch) — config (compose YAML)

##### Task 23

T23 S1 `Iverson.Server/Iverson.LoadTest.Tests/Auth/CredentialEndpointsTests.cs` (1 edit(s)) — tests (citation source)
T23 S1 `Iverson.Server/Iverson.ClientConformance.Tests/HarnessEndpointCheckTests.cs` (1 edit(s)) — tests (citation source)
T23 S1 `Iverson.Server/Iverson.ClientConformance.Tests/HarnessChannelTests.cs` (1 edit(s)) — tests (citation source)
T23 S1 `Iverson.Clients/Go/conformance/main_test.go` (1 edit(s)) — tests (citation source)
T23 S1 `Iverson.Clients/Python/tests/test_conformance_driver.py` (2 edit(s)) — tests (citation source)
T23 S1 `Iverson.Server/deploy/scripts/test_mint_acting_user_token.py` (2 edit(s)) — tests (citation source)
T23 S1 `Iverson.Server/Iverson.LoadTest/scripts/test_pattern_leg.py` (1 edit(s)) — tests (citation source)
T23 S1 `Iverson.Agents/Python/tests/test_main.py` (1 edit(s)) — tests (citation source)
T23 S3 `Iverson.Server/Iverson.ClientConformance/HarnessChannel.cs` (1 edit(s)) — `CheckEndpoints` (20–21, delegation to `CredentialEndpoints.Check`) and `Create` (28–37) — logic-bearing → candidate `HarnessChannel.Create`
T23 S3 `Iverson.Server/Iverson.ClientConformance/Program.cs` (4 edit(s)) — default URL, the check and the channel through `HarnessChannel` — glue/config
T23 S3 `Iverson.Server/Iverson.ClientConformance/TokenBroker.cs` (2 edit(s)) — Host-header default removed — config
T23 S3 `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs` (1 edit(s)) — `AuthentikBaseUrl` default `https://localhost:9443` — config (a default constant)
T23 S3 `Iverson.Server/Iverson.LoadTest/Program.cs` (5 edit(s)) — URL/connection-string defaults, Host default removed, comment — config
T23 S4 `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs` (3 edit(s)) — `BuildInvoker`'s scheme test and transport choice (24–74) — logic-bearing → candidate `Auth.BuildInvoker`
T23 S4 `Iverson.Clients/Go/conformance/main.go` (7 edit(s)) — imports, comments — glue; `transportOption`/`grpcUsesTLS` (248–259) — logic-bearing → candidate `conformance.grpcUsesTLS`; the two dial sites — glue
T23 S4 `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java` (2 edit(s)) — the scheme test choosing `useTransportSecurity`/`usePlaintext` (180–186) — logic-bearing → candidate `Driver.channelTransport`; comment — prose
T23 S4 `Iverson.Clients/Python/conformance/driver.py` (5 edit(s)) — `parse_grpc_address` (342–346) and `build_driver_channel`'s `use_tls` branches (349–391) — logic-bearing → candidate `driver.parse_grpc_address`; the three call sites — glue
T23 S4 `Iverson.Clients/TypeScript/conformance/driver.ts` (6 edit(s)) — `parseGrpcAddress` (240–252) — logic-bearing → candidate `driver.parseGrpcAddress`; the `useTls` call sites (364, 394, 403, 743) — glue
T23 S5 `Iverson.Server/deploy/scripts/mint_acting_user_token.py` (3 edit(s)) — docstring, `DEFAULT_COMPOSE_BASE_URL`, compose Host default removed — config
T23 S5 `Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py` (7 edit(s)) — docstring, defaults, `mint_service_token` sends `Host` only when set (one `if`) — config/glue (trivial conditional header)
T23 S5 `Iverson.Server/Iverson.LoadTest/scripts/dialogue_patterns.py` (1 edit(s)) — `--build-url` default `https://` — config (default constant)
T23 S5 `Iverson.Server/Iverson.LoadTest/scripts/dialogue_pool.py` (1 edit(s)) — `--build-url` default `https://` — config (default constant)
T23 S5 `Iverson.Server/Iverson.LoadTest/scripts/stack.py` (2 edit(s)) — Qdrant readiness URL and docstring — config (default constant)
T23 S5 `Iverson.Server/Iverson.LoadTest/scripts/ingest.py` (2 edit(s)) — `QDRANT_URL` constant and docstring — config (default constant)
T23 S5 `Iverson.Agents/Python/iverson_agent/__main__.py` (2 edit(s)) — `IVERSON_GRPC_URL` default and docstring — config (the scheme logic is unchanged)
T23 S5 `Iverson.Server/deploy/prometheus/prometheus.local.yml` (1 edit(s)) — config/declarative (YAML)
T23 S5 `Iverson.Server/docker-compose.yml` (1 edit(s)) — config/declarative (compose)
T23 S5 `Iverson.AdminUI/.env.development` (2 edit(s)) — config/declarative

##### Task 24

T24 S1 `docs/runbooks/csr12-platform.md` (1 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/specs/2026-09-24-api-ingress-nginx-grpc-design.md` (1 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/runbooks/csr11-transport-network.md` (1 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/runbooks/kind-cluster-troubleshooting.md` (3 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/runbooks/admin-console-landing-page-usage.md` (3 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/runbooks/operator-access-onboarding.md` (1 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/user-management-and-security.md` (12 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/runbooks/client-conformance-matrix.md` (1 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/runbooks/csr12-server.md` (3 edit(s)) — config/declarative (Markdown prose)
T24 S2 `Iverson.Server/Iverson.LoadTest/README.md` (4 edit(s)) — config/declarative (Markdown prose)
T24 S2 `docs/runbooks/integration-test-flake-signatures.md` (1 edit(s)) — config/declarative (Markdown prose)
T24 S2 `Iverson.AdminUI/README.md` (2 edit(s)) — config/declarative (Markdown prose)
T24 S3 inline Python link check — tests

##### Task 25

T25 S1 scratch `$SCR` scripts: t25/test_probes.sh (offline checks of the probe helpers), t25/mutate.sh — tests (citation source; scratch, never committed)
T25 S3 `Iverson.Server/deploy/scripts/authentik-probes.sh` (1 edit(s)) — helpers `edge_client_from_xff`, `ip_is`, `is_loopback`, `is_console`, `setup_shows_no_prompt` (60–92) and the option parser (216–227) — logic-bearing (bash) → candidate `authentik-probes.helpers`; `xff_for_marker` (grep|tail|sed pipeline), `FLOW_PY`/`REPUTATION_PY` drivers, `ak_shell`, port-forward, cleanup and the A1–A9 main body — I/O-bound (live probes)
T25 S3 `Iverson.Server/deploy/scripts/mesh-check.sh` (1 edit(s)) — `mtls_seen` (70–76) and the option parser (107–118) — logic-bearing (bash) → candidate `mesh-check.mtls_seen`; `UNENROLLED_PY` (59–66) — logic-bearing (embedded Python) → candidate `mesh-check.UNENROLLED_PY`; `hop_bytes`, `port_forward`, cleanup and M1–M5 — I/O-bound
T25 S3 `Iverson.Server/deploy/scripts/admission-check.sh` (1 edit(s)) — `classify` (52–60) and the option parser (186–197) — logic-bearing (bash) → candidate `admission-check.classify`; `expect`, `port_forward_outcome`, `exchange`, `legacy_token`, cleanup and N/O/L checks — I/O-bound (live kubectl/Authentik calls)


#### Tasks 26–27

T26 S1 scratch `check_t26.py`, `check_t26_opns.py`, `check_t26_tf.py`, `t26_mut.py`, `t26_render.sh`, `t26_ci.sh`, `t26_neg.sh`, `t26_opns.sh`, `t26_tf.sh`, `t26_mutate.sh` — tests (static checks; no repo file, no commit)
T26 S2–S5 runs of those checks against `main` and the branch, Terraform baseline, mutation proof — tests
T27 S1–S4 full suites, tool install (kind v0.33.0), state record, compose bring-up — tests (live runs)
T27 S7–S9, S12, S15, S18–S19 conformance harness, teardown, kind cluster creation, convergence report, probe-script runs, upgrade probes, state diff — tests (live runs; no new code)
T27 S5 scratch `compose-checks.sh` — tests (live compose checks)
T27 S6 scratch `probe.csproj`, `Program.cs`, `tokens.sh` — tests (live token probe)
T27 S10 scratch `converge-poll.sh`, `converge-report.py`, `apply-blueprints.sh`, `live-values.yaml` — tests (fresh-install convergence poller)
T27 S11 scratch `migration-wait.py` — tests (live migration-wait observation)
T27 S13 scratch `capacity.py` — tests/check tooling (computes the measured capacity numbers); its one repo output, the `# Capacity` comment in `Iverson.Server/deploy/helm/iverson/values-laptop.yaml:9-11` — config/declarative (YAML comment prose)
T27 S14 scratch `local-live.yaml` — config (live values); scratch `kind-checks.sh` — live check harness: its `alive()` parser (embedded Python) and the at-most-60×5 s wait loop are logic-bearing → candidate `kind-checks.alive-wait` **at the caller's request** (by the skill's table it is test/check code; see the approval list's note — reject at the gate if it should stay a citation source)
T27 S16 scratch `exchange.py`, `createtenant.csproj`, `Program.cs`, `createtenant.sh` — tests (live TokenReview-pin and CreateTenant probes)
T27 S17 scratch `upgrade-poll.sh` — tests (upgrade poller)

### Approved trusted base (axioms)

- `TenantAdminGrpcService.RequireUserInTenantAsync`: 
  ```dafny
  function IntTryParseNone(s: CsString): Option<int32>
    ensures (|s| > 0 && (forall i :: 0 <= i < |s| ==> '0' <= s[i] <= '9') && DecimalValue(s) <= 0x7FFF_FFFF) ==> IntTryParseNone(s) == Some(DecimalValue(s) as int32)
  
  type Directory
  
  function GetUserAsync(idp: Directory, userId: CsString): GetUserResult
    ensures GetUserAsync(idp, userId).Found? ==> GetUserAsync(idp, userId).record.Id == userId
  ```
- `IdpAdminClient.TenantReads`: 
  ```dafny
  type Server
  
  function Get(server: Server, path: seq<char>): HttpAnswer
  
  function TenantFilter(tenantId: CsString): CsString
  
  function JsonGetInt32(raw: CsString): Option<int32>
  ```
- `TenantAdminGrpcService.InviteUser`: 
  ```dafny
  type Env
  
  type Date(==)
  
  function TenantStatus(env: Env, tenantId: CsString): Option<CsString>
  
  function TryReserve(env: Env, tenantId: CsString, day: Date): bool
  
  function UtcToday(env: Env): Date
  
  function CreateUserThrows(env: Env): bool
  ```
- `InviteQuotaRepository.TryReserveAsync`: none
- `RateLimitInterceptor.Enforce`: 
  ```dafny
  function SlidingWindowGrants<K(==)>(granted: seq<K>, key: K, permitLimit: nat): bool
    ensures SlidingWindowGrants(granted, key, permitLimit) <==> multiset(granted)[key] < permitLimit
  ```
- `OperatorAuthorizationPolicy.IsSatisfiedBy`: 
  ```dafny
  type Config
  
  function ConfigValue(cfg: Config, key: CsString): Option<CsString>
  
  function ConfigStringArray(cfg: Config, section: CsString): Option<seq<CsString>>
  ```
- `TokenRevocationCache.IsRevokedAsync`: none
- `StreamRevalidationInterceptor.RecheckAsync`: 
  ```dafny
  type CacheEnv
  
  type Msg
  
  function CacheRevoked(env: CacheEnv, sub: CsString, iat: Option<Ticks>): bool
  
  function TenantStatus(env: CacheEnv, tenantId: CsString): Option<CsString>
  
  function LongTryParse(s: Option<CsString>): Option<int>
    ensures s.None? ==> LongTryParse(s).None?
    ensures LongTryParse(s).Some? ==> -0x8000_0000_0000_0000 <= LongTryParse(s).value < 0x8000_0000_0000_0000
  ```
- `TenantStatusCache.InvalidateAll`: none
- `AuthInvalidationListener.ExecuteAsync`: none
- `EngagementHealthChecker.CheckHealthAsync`: 
  ```dafny
  function QueryOutcome(backendAlive: bool): ProbeResult
    ensures QueryOutcome(backendAlive) == (if backendAlive then Rows(2) else ProbeFails(OtherError(1064)))
  ```
- `HarnessChannel.Create`: 
  ```dafny
  function StartsWithOrdinalIgnoreCase(s: CsString, prefix: CsString): bool
    ensures IsAscii(s) && IsAscii(prefix) ==> (StartsWithOrdinalIgnoreCase(s, prefix) <==> |s| >= |prefix| && AsciiLower(s[..|prefix|]) == AsciiLower(prefix))
  
  function UriParse(s: CsString): Option<UriInfo>
  ```
- `Auth.BuildInvoker`: 
  ```dafny
  function StartsWithOrdinalIgnoreCase(s: CsString, prefix: CsString): bool
    ensures IsAscii(s) && IsAscii(prefix) ==> (StartsWithOrdinalIgnoreCase(s, prefix) <==> |s| >= |prefix| && AsciiLower(s[..|prefix|]) == AsciiLower(prefix))
  ```
- `conformance.grpcUsesTLS`: 
  ```dafny
  function StringsToLower(s: seq<Byte>): seq<Byte>
    ensures IsAscii(s) ==> StringsToLower(s) == AsciiLower(s)
  ```
- `driver.parse_grpc_address`: 
  ```dafny
  function UrlSplit(s: seq<CodePoint>): SplitResult
    ensures |s| >= 2 && s[..2] == DoubleSlash ==> UrlSplit(s).scheme == []
  ```
- `driver.parseGrpcAddress`: 
  ```dafny
  function UrlParse(s: CsString): Option<UrlInfo>
    ensures |s| >= 7 && s[..7] == "http://" && UrlParse(s).Some? ==> UrlParse(s).value.protocol == "http:"
  
  type Number
  
  function NumberFromString(s: CsString): Number
  
  predicate NumberIsPositiveInteger(n: Number)
  ```
- `Driver.channelTransport`: 
  ```dafny
  function EqualsIgnoreCase(a: CsString, b: CsString): bool
    ensures IsAscii(a) && IsAscii(b) ==> (EqualsIgnoreCase(a, b) <==> AsciiLower(a) == AsciiLower(b))
  ```
- `roles.RequireRole`: 
  ```dafny
  type Failure(==)
  ```
- `blueprints-configmap.orchestrator-tokenreview`: none
- `blueprints-configmap.orchestrator-workload`: 
  ```dafny
  type AkEnv
  
  type AkUser
  
  function AkUserBy(env: AkEnv, username: seq<CodePoint>): Option<AkUser>
  
  function AkIsGroupMember(env: AkEnv, user: AkUser, group: seq<CodePoint>): bool
  ```
- `secret-service-clients.admin-automation-workload`: none
- `secret-service-clients.console-admins`: 
  ```dafny
  type AkEnv
  
  type AkUser
  
  function AkIsGroupMember(env: AkEnv, user: AkUser, group: seq<CodePoint>): bool
  
  function UserUid(user: AkUser): seq<CodePoint>
  ```
- `compose-service-clients.console-admins`: 
  ```dafny
  type AkEnv
  
  type AkUser
  
  function AkIsGroupMember(env: AkEnv, user: AkUser, group: seq<CodePoint>): bool
  ```
- `recovery-flow.login-throttle`: 
  ```dafny
  function ClampedAdd(score: int, delta: int): int
    ensures ClampedAdd(score, delta) == if score + delta < -5 then -5 else if score + delta > 5 then 5 else score + delta
  ```
- `deployment-server.wait-for-migration`: 
  ```dafny
  type Version(==)
  
  type Build(==)
  
  function DbDuring(before: DbState, p: MigratorPhase, v: Version, b: Build): DbState
    ensures DbDuring(before, p, v, b).tableExists == (before.tableExists || p == RowWritten || p == Migrating || p == PassEnded || p == DiedAfterRow)
    ensures ((v, b) in DbDuring(before, p, v, b).rows) <==> (p == RowWritten || p == Migrating || p == PassEnded || p == DiedAfterRow)
    ensures DbDuring(before, p, v, b).lock1000 <==> (p == LockTaken || p == RowWritten || p == Migrating)
  ```
- `admission-policy.validations`: none
- `configmap-tls-proxy.public-locations`: 
  ```dafny
  predicate FlowRegexMatches(uri: seq<Byte>)
    ensures FlowRegexMatches(uri) <==> exists a, s :: (a == IfFlow || a == Executor) && s in AllowedSlugs() && (uri == a + s + Slash || uri == a + s + Slash + Newline)
  ```
- `job-create-user.create-user`: 
  ```dafny
  datatype MysqlResult = MysqlResult(exit: int, out: seq<seq<Byte>>)
  
  function MysqlRun(v: ClusterView, pw: Option<seq<Byte>>, stmt: Stmt): MysqlResult
    ensures pw != v.rootPassword ==> MysqlRun(v, pw, stmt).exit != 0
    ensures pw == v.rootPassword && stmt == ShowBackends ==> MysqlRun(v, pw, stmt) == MysqlResult(0, v.backendLines)
    ensures pw == v.rootPassword && stmt == ShowFrontends ==> MysqlRun(v, pw, stmt) == MysqlResult(0, v.frontendLines)
  ```
- `setup.sh.guards`: 
  ```dafny
  function SortVFirst(x: seq<Byte>, y: seq<Byte>): seq<Byte>
    ensures IsVTriple(x) && IsVTriple(y) ==> SortVFirst(x, y) == (if TripleLe(VTriple(x), VTriple(y)) then x else y)
  ```
- `setup.ps1.guards`: 
  ```dafny
  function CaseFold(s: CsString): CsString
    ensures IsAscii(s) ==> CaseFold(s) == AsciiLower(s)
  
  function VersionParse(s: CsString): Option<(int, int, int)>
    ensures IsDottedTriple(s) ==> VersionParse(s) == Some(TripleOf(s))
  ```
- `generate-compose-secrets.renew`: none
- `fe-tls-entrypoint.settings`: none
- `authentik-probes.helpers`: none
- `admission-check.classify`: 
  ```dafny
  function GrepQF(text: seq<Byte>, pat: seq<Byte>): bool
    ensures pat != [] && !Contains(pat, Newline) ==> (GrepQF(text, pat) <==> Contains(text, pat))
  ```
- `mesh-check.mtls_seen`: 
  ```dafny
  function GrepQF(text: seq<Byte>, pat: seq<Byte>): bool
    ensures pat != [] && !Contains(pat, Newline) ==> (GrepQF(text, pat) <==> Contains(text, pat))
  ```
- `mesh-check.UNENROLLED_PY`: none
- `kind-checks.alive-wait`: none

### Gate amendments (user-approved, 2026-10-23, after the first proving round)

- `admission-policy.validations` P3: the frozen clause `requires r.object.Some? && r.oldObject.Some? && SameImages(r.object.value, r.oldObject.value)` could not parse (`object` is a Dafny 4.11 reserved word). Re-frozen with the accessor `ObjectOf(r)` in place of `r.object` (same meaning); the user approved.
- `EngagementHealthChecker.CheckHealthAsync`: added approved axiom (the probe is `SELECT * FROM TABLE(generate_series(1,2))`, which yields at most two rows; spec §3.5 line 316, plan Task 15 Step 4 `BackendProbeSql`):
  ```dafny
  lemma ProbeYieldsAtMostTwoRows(env: HealthEnv)
    ensures env.probe.Rows? ==> env.probe.n <= 2
  ```
- WITHDRAWN (same day): the `ProbeYieldsAtMostTwoRows` axiom above was unsound: it is stated for every `HealthEnv`, while `Rows(n: nat)` admits n > 2, so `ProbeYieldsAtMostTwoRows(HealthEnv(StepOk, StepOk, Rows(3)))` proves `false`. The user chose to revert: the axiom is removed, the model is the one the first proving round gated, and P3 stays unproved.

### Correspondence tables

#### `TenantAdminGrpcService.RequireUserInTenantAsync`: T1 S3 `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs` `RequireUserInTenantAsync, UserNotInTenant, IsCanonicalUserPk (123–141)` (C#)

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| `Tenancy/IIdpAdminClient.cs:16–23` `IdpUserRecord` | 17 | `IReadOnlyList<string>` becomes `seq<CsString>`; `string? TenantId` becomes `Option<CsString>` |
| `IIdpAdminClient.cs:43` `Task<IdpUserRecord?> GetUserAsync` | 20, 60, 63–64 | Approved axiom. The result is `Found`, `Null` (404/null) or `Threw` (exception is an outcome) |
| TenantAdminGrpcService.cs:123 signature | 122 | `string userId, string tenantId` are non-nullable, so `CsString`. The IdP is an explicit `Directory` parameter (it stands in for the injected `authentikAdminClient`) |
| :125–126 `if (!IsCanonicalUserPk(userId)) throw UserNotInTenant();` | 124–125 | The throw becomes `Refused(NoCall)` |
| :127 `await authentikAdminClient.GetUserAsync(userId)` | 127–128 | A throw from GetUserAsync propagates and becomes `GuardThrew` (line 128–129). This is a separate branch made explicit because of the `Threw` variant; the C# has no catch |
| :128–129 `if (user is null \|\| user.TenantId != tenantId) throw UserNotInTenant();` | 130–131 | Ordinal `string? != string` becomes `Option` inequality with `Some(tenantId)`, so null TenantId compares unequal, as in C#. The throw becomes `Refused(OneGetUserCall)` |
| :130 `return user;` | 133 | `Accepted(user.record)` |
| :133–134 `UserNotInTenant()` | 26–27 (`Refused` variant) | PermissionDenied RpcException modelled as `Refused(calls)`, per the library boundary |
| :138–141 `IsCanonicalUserPk` | 113–118 | `int.TryParse(…NumberStyles.None, Invariant…)` becomes approved axiom `IntTryParseNone` (56–57). `pk > 0` is on `int32`. `pk.ToString(InvariantCulture) == userId` becomes `Int32ToString` (98–105) compared ordinally |
| `int.ToString(CultureInfo.InvariantCulture)` (library) | 68–105 | Modelled as `-` followed by the magnitude's decimal digits for negatives, and decimal digits with no leading zero otherwise. The length bound needed for `CsString` is proved via `NatToStringLength`/`Pow10` |

#### `IdpAdminClient.TenantReads`: T1 S3 `Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs` `ListUsersByTenantAsync (148–206), GetUserAsync (214–238), ReadPk (376–382), TenantIdOf (385–390)` (C#)

| Worktree lines | Dafny lines | Note |
|---|---|---|
| 148–206 `ListUsersByTenantAsync` | 276–281 (`ListUsersByTenant`) | `fuel` bounds the page count; when it runs out the result is `Unfinished(paths)`. |
| 152–153 `filter = Uri.EscapeDataString(JsonSerializer.Serialize(...))` | 278 | This is the approved axiom `TenantFilter`, which is total. **Deviation:** it ignores a possible serializer or escaper throw on invalid UTF-16. Such a throw would happen before any request, so `paths` would be empty and no property is affected. |
| 154–155 `matches`, first `path` | 279–280 | `"/api/v3/core/users/?attributes=" + filter` is the same as `FirstPage(t)` (lines 155–158). |
| 157–203 `while (path is not null)` | 233–273 (`Loop`, recursive over fuel) | `path` is `Option<seq<char>>`, and `None` stands for null. Each page appends its path to `sent` before the request. |
| 159, 162–163 `GetAsync`, `ParseAsync` | 241–244 | Approved axiom `Get`. `Transport` (line 242) and `body None` (line 244) both give `ListThrew`. |
| 160 `EnsureSuccessStatusCode` | 243 | Anything outside 2xx gives `ListThrew`. |
| 164–166 `root.GetProperty("results").EnumerateArray()` | 246–252 | If `root` is not an object or `results` is missing, the result is `ListThrew`. The same holds if `results` is not an array. |
| 166–176 foreach / tenant check / `new IdpUser(...)` | 204–230 (`Collect`) | Branch order is kept. A throwing `TenantIdOf` comes first (211), then `!= tenantId` → continue (212–213), then `ReadPk`, username, email, uid (215–228). `sources` is the ghost list of source elements. |
| 186–189 missing `pagination` | 255–257 | A miss and a throw from a non-object `root` both give `ListThrew`. |
| 191–194 `next` missing or not a Number | 259–263 | `TryGetProperty` on a non-object `pagination` also gives `ListThrew`. |
| 196 `next.GetInt32()` | 264–265 | Approved axiom `JsonGetInt32`; `None` means it threw. |
| 197–200 `nextPage < 0` | 267 | `ListThrew`. |
| 202 next path | 269–272 | `Int32ToString` renders decimal and is modelled at lines 134–149. The value is only rendered when `nextPage > 0`, so the culture's negative sign never matters. |
| 205 `return matches` | 237 | `Users(matches, sources, paths)`. |
| 214–238 `GetUserAsync` | 284–319 (`GetUser`) | |
| 217 `GetAsync($"/api/v3/core/users/{userId}/")` | 286 | Approved axiom `Get`; `Transport` gives `Threw` (287). |
| 218–219 404 → null | 288 | |
| 220 `EnsureSuccessWithBodyAsync` | 289 | Non-2xx gives `Threw`. |
| 222–224 parse, `user = root` | 290–292 | Non-JSON gives `Threw`. |
| 226–228 `ReadPk`, `pk != userId` → null | 293–296 | A null pk compares as `None != Some(userId)` and returns `Null`, as C#'s `null != userId` does. |
| 230–237 record construction | 298–318 | Arguments are evaluated left to right, and any throw gives `Threw`. `GetBoolean` is modelled at 111–116. Groups `EnumerateArray().Select(GetString()!)` is modelled at 121–131. `TenantIdOf` is checked for a throw at 317. |
| 376–382 `ReadPk` | 164–176 | A Number gives its `GetRawText` (raw text). Otherwise `GetString()!` applies: a string or null passes through, and any other kind throws. A missing `pk` or non-object element also throws. |
| 385–390 `TenantIdOf` | 179–185 (`TenantIdOfThrows`), 188–199 (`TenantIdOf`) | **Deviation:** the method is split into a throw predicate and an `Option` value, because the frozen P2 compares `TenantIdOf(...)` with `Some(tenantId)`. `TryGetProperty` on a non-object `user` or `attributes` throws. Callers test the throw first. |
| Library: `GetProperty`, `TryGetProperty`, `GetString` | 58–106 | Property lookup is ordinal and case-sensitive, and the last duplicate name wins, as `JsonElement` does. |

#### `TenantAdminGrpcService.InviteUser`: T3 S3 `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs` `InviteUser (29–53), RequireActiveTenantAsync (106–114)` (C#)

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| 29 (signature: `request`, `context`) | 24 `InviteCall`, 49 | **Deviation:** `context` is reduced to the `tenant_id` claim as `Option<CsString>`. `request.Username`/`Email` are dropped because no property reads them. The four ways the call can end become `InviteStatus` (26) and `InviteResult` (27). |
| 31 `await RequireActiveTenantAsync(context)` | 51–52 | If the helper throws, the exception propagates as `InviteResult(PermissionDenied, [])`, since exceptions are outcomes. |
| 106 `RequireActiveTenantAsync` | 37–38 | Returns `None` when it would throw `PermissionDenied`, and `Some(tenantId)` when it returns. |
| 108–109 `FindFirst("tenant_id")?.Value ?? throw PermissionDenied` | 40 | A missing claim is `tenantClaim.None?`. A present claim's `Value` is a non-null string. |
| 110 `tenantStatusCache.GetStatusAsync(tenantId)` | 43 | Approved axiom `TenantStatus`. A null status is `None`. |
| 111–112 `status is null or "suspended" or "deleted"` → throw | 44, 29–30 | The branch order is kept. The `Suspended` and `Deleted` constants are the literal values. `IsActive` (32–35) is its exact negation, as the model defines it. |
| 113 `return tenantId` | 45 | |
| 34 `DateOnly.FromDateTime(DateTime.UtcNow)` | 55 | Approved axiom `UtcToday`. |
| 34 `inviteQuota.TryReserveAsync(tenantId, day)` | 56–57 | Approved axiom `TryReserve`. The reservation is always recorded as the effect `Reserve(tenantId, today)` because the call is made whatever it returns. |
| 35 throw `ResourceExhausted` | 58 | Effects are `[Reserve]` only. |
| 40–44 `authentikAdminClient.CreateUserAsync(...)` | 59–60 | Approved axiom `CreateUserThrows`. When it throws, the result is `InviteThrew` and the effects are `[Reserve, CreateUser]`, because the IdP was called. |
| 45 `auditLog.AdminOperation` | (none) | **Deviation:** ignored, as the approved boundary allows; it has no effect on the outcome. |
| 46–52 build and return `TenantUser` | 62 | **Deviation:** `Ok` with `[Reserve, CreateUser]`. The response fields (`UserId`, `RecoveryLink ?? ""`) are not modelled because no property reads them. |

#### `InviteQuotaRepository.TryReserveAsync`: T3 S3 `Iverson.Server/Iverson.Sql/InviteQuotaRepository.cs` `TryReserveAsync (27–37)` (C# with PostgreSQL SQL)

| Worktree lines | Dafny lines | Note |
|---|---|---|
| 18–23 (DDL, `"Count" int`, PK `("TenantId","Day")`) | 10–11 (`int32`), 15–16 (`QuotaRow`) | One row for one key, as approved. **Deviation:** the column can hold NULL, but it is modelled as `int32`. This statement only ever writes 1 or Count+1, so NULL cannot arise from this path. |
| 27 (`TryReserveAsync(string tenantId, DateOnly day)`) | 61–66 (`Reserve(s: QuotaRow)`) | **Deviation:** the model covers one key. `tenantId` and `day` only pick which row `s` is. |
| 30–31 (`INSERT … VALUES (…, 1)`) | 37–39 | No conflict: the row is inserted as `Row(1)` and `Some(1)` is returned. |
| 32–34 (`ON CONFLICT … DO UPDATE SET "Count" = "Count" + 1 WHERE "Count" < 100`) | 40–45, 46–48 | When the WHERE holds, the count goes up by 1. Integer overflow raises an error, modelled as `Raised` (44). When the WHERE fails, the row is neither updated nor returned (48). |
| 35 (`RETURNING "Count"`) | 39, 45, 48 (the `returning` field) | |
| 28 (`QuerySingleOrDefaultAsync<int?>`) | 53–57 | No row gives null (`None`); one row gives its value. |
| 37 (`… is not null`) | 70 (`.Some?`) | |
| (throw path of line 28) | 22–24, 69 | **Deviation:** the approved `ReserveResult(returned, next)` has no variant for a throw, so `Raised` is folded into `returned == false` with the row unchanged. The lemma `StatementNeverRaises` (102–105) proves this path can never be reached from any row state, because `c < 100` keeps `c+1` at or below 100. The fold therefore hides no outcome. |
| (serial order across api instances, §6 line 490) | 75–87 (`CountAfter`, `ReservedCount`), 89–94 (`StoredCount`) | Harness, per the approved library boundary: the row lock taken by `ON CONFLICT DO UPDATE` puts concurrent statements in one serial order. |
| (none) | 96, 107–125 | Helpers. `Min` is used by the lemmas. `CountAfterShape` proves that n reservations leave `Row(min(n,100))`, or `NoRow` when n is 0. `ReservedCountIsMin` proves that n attempts reserve min(n,100) slots. Both are proved by induction. |
| (none) | 129–151 | The three property lemmas, copied exactly from the frozen text. |

#### `RateLimitInterceptor.Enforce`: T2 S4 `Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs` `AdminServices, _limiter, _adminLimiter (13–37), Enforce (58–74)` (C#)

| Source lines | Dafny lines | Note |
|---|---|---|
| 13–17 `AdminServices` | 24–25, 41 | The two `Descriptor.FullName`s become the constants `"iverson.TenantAdminGrpcService"` and `"iverson.TenantLifecycleGrpcService"`. I checked the package `iverson` in `tenant_admin.proto:3` and `tenant_lifecycle.proto:3`. The array order is kept. |
| 19–27 `_limiter` (PermitLimit 50_000) | 44, 58–63 (`AcquireGeneral`) | Library boundary: the sliding window is the approved axiom `SlidingWindowGrants` (11–12). The window, segment and queue options are not modelled; segment expiry is out of the model, as approved. A grant appends the subject to `general`; a refusal leaves the windows unchanged. |
| 29–37 `_adminLimiter` (PermitLimit 30) | 45, 66–71 (`AcquireAdmin`) | Same as the row above, keyed by `(service, subject)` on `admin`. |
| 58 `Enforce(ServerCallContext)` | 74 | `ServerCallContext` is reduced to `Call(sub, routed)` (17). The model takes `Windows` and returns `EnforceResult(acquired, next)`, which makes the limiter state explicit. |
| 60 subject `?.Value ?? "unknown"` | 26, 77 | A `None` `sub` maps to `UnknownSubject`. |
| 63–64 matched ServiceName via endpoint metadata | 79 | Library boundary: routing is the input `routed`, with `None` for any null in the `?.` chain. |
| 65 `FirstOrDefault(service => service == matched)` | 49–55, 81 | Searches the list in order with ordinal equality. A null `matched` matches no element. |
| 66–68 `adminService is null ? _limiter… : _adminLimiter…` | 83–85 | Same branch order as the source. `using`/dispose of the lease is not modelled because it doesn't affect the result. |
| 69–73 `!IsAcquired` → LogWarning + throw `RpcException(ResourceExhausted)` | 86–88 | The throw is the outcome `acquired == false`, carrying the unchanged `next`. `LogWarning` and `SanitizeForLog` are logging calls with no effect on the result, so they are left out. |
| — | 28–36 | `GeneralCount` and `AdminCount`, as specified in the approved model. |
| — | 93–123 | Helper lemmas: the two service names are distinct, and `FirstOrDefault` is evaluated on the admin, lifecycle and other inputs. All are proved. |

#### `OperatorAuthorizationPolicy.IsSatisfiedBy`: T4 S3 `Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs` `IsSatisfiedBy (14–25); with SchemaAdminAuthorizationPolicy.cs IsSatisfiedBy (11–18), ConsoleAdminAuthorizationPolicy.cs IsSatisfiedBy (10–12), OperatorSubjects.cs FromConfiguration (21–25), and the unchanged TenantAdminAuthorizationPolicy.IsSatisfiedBy` (C#)

| Worktree file:line | Task/step | Dafny lines | Notes / deviations |
|---|---|---|---|
| OperatorAuthorizationPolicy.cs:14 (signature) | T4 S3 | 96 | `ClaimsPrincipal` is modelled as `seq<Claim>`, the claims of all its identities in order, and `OperatorSubjects` as a datatype. Both C# types are non-nullable, so no `requires` was added. |
| OperatorAuthorizationPolicy.cs:16–17 (`sub` on the list) | T4 S3 | 98–100 | Membership in a `HashSet` with `StringComparer.Ordinal` is modelled as `in set<CsString>`. |
| OperatorAuthorizationPolicy.cs:19–21 (`admin` scope token) | T4 S3 | 102–104 | `Split(' ', RemoveEmptyEntries)` is modelled as `ScopeTokens` (72–79, using `IndexOfSpace` 60–69). `Enumerable.Contains` uses default ordinal equality and is modelled as `in seq`. |
| OperatorAuthorizationPolicy.cs:23–24 (compose fallback) | T4 S3 | 105–107 | The `string?` fallback is `Option<CsString>`. `FindAll(...).Any(c => c.Value == g)` is `AnyValueEquals(FindAll(p,"groups"), g)` (34–39, 48–51). |
| SchemaAdminAuthorizationPolicy.cs:11–18 | T4 S3 | 111–118 | Same branch order: the Operator early return comes first, then the `schema_admin` scope token. |
| TenantAdminAuthorizationPolicy.cs:5 (unchanged) | (pre-existing) | 121–124 | `groupClaims.Contains("tenant-admins")` is modelled as `in seq`. |
| ConsoleAdminAuthorizationPolicy.cs:10–12 | T4 S3 | 127–131 | `FindAll("groups").Select(c => c.Value)` is modelled as `SelectValues(FindAll(...))` (54–57). The short-circuit `\|\|` is kept. |
| OperatorSubjects.cs:21–25 FromConfiguration | T4 S3 | 134–142 | `GetSection(...).Get<string[]>() ?? Array.Empty` is modelled as `ConfigStringArray` with `None` mapped to `[]`; the HashSet becomes `set x \| x in items`. `configuration[key] is { Length: > 0 } g ? g : null` is modelled as `ConfigValue` with the length check. |
| OperatorSubjects.cs:19 (record) | T4 S3 | 82 | Matches the approved model. |
| Library: `FindFirst` / `FindAll` / `Claim` | n/a | 20–39 | **Deviation:** .NET `ClaimsIdentity.FindFirst/FindAll(string)` compare claim types with `OrdinalIgnoreCase`; the model compares ordinally, as the approved model says ("the first claim of that type"). Every property states its premises through the same `FindFirst`/`HasClaim`, and the proofs never rely on how types are matched, so the results stand under either rule. |
| Library: `HasClaim` (property vocabulary) | n/a | 42–45 | `exists c :: c in p && c.ctype == t && c.value == v`. It is tied to the transliteration's `FindAll` by the proved helpers `FindAllMembership` (148–157), `GroupsAnyIffHasClaim` (159–174) and `GroupsSelectIffHasClaim` (176–192). |
| Vocabulary: `Option` | n/a | 10 | **Deviation:** it is declared `Option<+T>` (covariant) rather than `Option<T>`. Without this, the frozen P8 clause `ConfigValue(...) == Some("")` fails to resolve with "Option<CsString> vs Option<string> not comparable". The semantics are unchanged. |
| Properties P1–P8 | n/a | 199–252 | The headers and clauses are frozen text. P5 calls `GroupsAnyIffHasClaim`, P7 calls `GroupsSelectIffHasClaim`, and the other six lemmas have empty bodies. |

#### `TokenRevocationCache.IsRevokedAsync`: T5 S3 `Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs` `IsRevokedAsync (35–42)` (C#)

| Worktree `file:line` (task/step) | Dafny lines | Note |
|---|---|---|
| `TokenRevocationCache.cs:26` `Skew = TimeSpan.FromSeconds(5)` (context, unchanged) | 13, 23–24 | `SkewTicks := 5 * SecondTicks`, using 1 s = 10,000,000 ticks. |
| `TokenRevocationCache.cs:33` `record Snapshot(RevokedAt, LoadedAt)` (context, unchanged) | 26–28 | **Deviation:** `LoadedAt` is left out. `IsRevokedAsync` never reads it; only the staleness check uses it. `DateTimeOffset` values are modelled as `Ticks` (UTC ticks). |
| `TokenRevocationCache.cs:35` signature (T5 S3) | 33 | `string sub` becomes `CsString`. `DateTimeOffset? issuedAt` becomes `Option<Ticks>`. **Deviation:** the `CancellationToken` parameter and the async wrapper are dropped. The result is `RevokedResult`, where `Revoked(b)` is the returned bool and `RevokedThrew` is the exception outcome. |
| `TokenRevocationCache.cs:37` `await CurrentSnapshotAsync(...)` (T5 S3) | 32–33 (parameter `s`) | As the approved boundary specifies, the snapshot is an input. The load and reload paths are not modelled. |
| `TokenRevocationCache.cs:40` `RevokedAt.TryGetValue(sub, out revokedAt) && …` (T5 S3) | 35–38 | A miss returns false through the `&&` short-circuit. A hit binds `revokedAt`. Lookup is ordinal, so keys are exact `seq<char>` values. |
| `TokenRevocationCache.cs:41` `issuedAt is null \|\| …` (T5 S3) | 39–40 | A null `iat` means revoked, and the `\|\|` short-circuit skips the addition. |
| `TokenRevocationCache.cs:41` `issuedAt <= revokedAt + Skew` (T5 S3) | 15–21, 41–45 | `AddTimeSpan` models `DateTimeOffset + TimeSpan`. A sum above `MaxTicks` gives `AddThrew`, which becomes `RevokedThrew` (the `ArgumentOutOfRangeException` outcome). Once `issuedAt` is non-null, the lifted `<=` is a plain tick comparison. **Deviation, as the approved boundary specifies:** overflow is checked on UTC ticks. The real `DateTimeOffset.Add` checks the clock time (UTC plus offset) first. The two agree when the offset is 0, which is how Npgsql returns `timestamptz` values. |
| `TokenRevocationCache.cs:42` `}` | 46 | — |

#### `StreamRevalidationInterceptor.RecheckAsync`: T5 S19 `Iverson.Server/Iverson.Api/Grpc/StreamRevalidationInterceptor.cs` `RecheckAsync (45–55), RevalidatingServerStreamWriter.WriteAsync ×2 (75–85); with Tenancy/TokenRevocationCheck.cs IsRevokedAsync (13–24)` (C#)

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| (vocabulary) `ClaimsPrincipal.FindFirst(t)?.Value` | 21–28 (`Claim`, `FindFirst`) | Deviation: the claim type is compared ordinally, as the approved model says ("the first claim of that type"). The real .NET `ClaimsIdentity.FindFirst` compares types `OrdinalIgnoreCase`. The properties are written in terms of the same `FindFirst`, so the proofs are not affected. |
| (library) `DateTimeOffset.FromUnixTimeSeconds` | 53–64 | Modelled: outside [−62 135 596 800, 253 402 300 799], the throw becomes `DtoThrew`; otherwise ticks = s·10⁷ + 621 355 968 000 000 000. The `requires` is the range of the `long` parameter. |
| Approved axioms | 35–45 | Copied verbatim, at top level. |
| TokenRevocationCheck.cs:16 | 74 | `sub := FindFirst(principal, "sub")` |
| TokenRevocationCheck.cs:17–18 | 75 | null `sub` gives `Checked(false)` |
| TokenRevocationCheck.cs:20–22 | 77–83 | `long.TryParse` is mapped to `LongTryParse` (a null value gives None per the axiom). When the parse succeeds, `FromUnixTimeSeconds` runs; its throw becomes `CheckThrew`. |
| TokenRevocationCheck.cs:23 | 81, 83 | `CacheRevoked(env, sub, Some(ticks) / None)`. Deviation: the `CancellationToken` is dropped, because the approved `CacheRevoked` axiom is a pure bool. A cancellation exception from the cache is outside the model. |
| StreamRevalidationInterceptor.cs:45 | 90, 92, 94 | `StreamCall(primary, acting: Option)`. Deviation: the approved name `datatype Recheck` clashes with the function `Recheck` that the frozen lemmas use, so the datatype is named `RecheckOutcome` with the approved constructors. |
| :47 | 97–99 | Primary check first; a throw gives `RecheckThrew`; revoked gives `EndUnauthenticated`. |
| :48 | 102–104 | `\|\|` short-circuits: the acting user is checked only when the primary is not revoked; null acting means false. |
| :49 | 99, 104 | `RpcException(Unauthenticated)` becomes `EndUnauthenticated` (the message text is not modelled; no property cites it). |
| :51 | 107 | `actingUser?.FindFirst("tenant_id")?.Value` |
| :52–53 | 109–111 | Pattern `null or "suspended" or "deleted"` transliterated literally. It is not routed through `IsActiveStatus`; the two agree by definition. |
| :54 | 112 | `EndPermissionDenied("Tenant '" + t + "' is not active.")`. Deviation: `detail` is `seq<char>`, as approved, so a .NET string-length overflow (OutOfMemoryException) on a huge `tenant_id` is not modelled. |
| :55 | 113 | Normal completion gives `Write`. |
| :41 (the delegate) and :75–79 | 129–133 | One-argument form: recheck, then the inner `WriteAsync` appends `m`; a non-Write outcome propagates and nothing is written. The token (`callCancellation`) is ignored, as for line 23. |
| :81–85 | 124–128 | Two-argument form: same body, with `cancellationToken`; the token is ignored likewise. The inner writer's own failures are outside the model. |
| Property vocabulary | 141–165 | `IatReadable`, `ReadIat`, `PrincipalRevoked`, `IsActiveStatus`, per the approved model notes. `ReadIat` returns None when `FromUnixTimeSeconds` would throw; it only matters under `IatReadable`. |
| Helper lemma | 168–172 | `CheckAgreesWithRevoked`: a check that doesn't throw answers exactly `PrincipalRevoked`, and under `IatReadable` the check never throws. Proved with an empty body. |
| Frozen lemmas | 179, 193, 205, 216 | Header and clauses copied line for line; each body `{` opens on the line after the last clause. |

#### `TenantStatusCache.InvalidateAll`: T5 S7 `Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs` `KeyFor, GetStatusAsync, Invalidate, InvalidateAll (36–52)` (C#)

| Worktree file:line | Dafny lines | Notes / deviations |
|---|---|---|
| :28 `Ttl` (30 s) | 139 (`Evict` case in `Step`), 123–128 (`Evict` event) | **Deviation:** TTL expiry and memory-pressure removal are both modelled as an arbitrary `Evict(key)` event. This is a superset of the real removals. |
| :30 `_generation` (`long`) | 51 (`State.generation: int`), 53 (`Init`, starts at 0) | **Deviation:** `long` is modelled as `int`, so it never wraps (2^63 increments are unreachable), as approved. |
| :36 `KeyFor` | 59 (`KeyPrefix`), 62 (`KeyResult`), 64–70; `long.ToString()` at 21–41 (`DigitChar`, `NatToString`, `LongToString`) | Interpolation is `"tenant-status:" + LongToString(g) + ":" + tenantId`. **Deviation:** a result longer than the CLR allows throws `OutOfMemoryException`. That is modelled as the outcome `TooLong`, at the `CsString` bound of 2^31. The real CLR limit is about 2^30, so the model is slightly looser. **Deviation:** `long.ToString()` uses ASCII digits, and the sign for negatives is fixed as `-`. In C# the sign comes from the current culture, but this is unreachable because the generation is never negative. |
| :38–40 `GetStatusAsync`: `Interlocked.Read` + `KeyFor` | 72–82 (`GetStatusBegin`), 132–136 (`Begin` case of `Step`) | A key that is too long gives `Threw`: the read throws and touches no state. **Deviation, as the approved model specifies:** `Interlocked.Read` (line 40) and `TryGetValue` (line 41) are fused into one atomic `Begin`. In the real code they are two steps. A read that has fetched generation g but not yet looked up its key can see entries that were Set or removed in between. If an `InvalidateAll` runs in that gap, the read behaves like one that began before it, because it uses the old generation. P1 is unaffected: every entry stored under a later generation's key comes from a read whose generation-read follows the `InvalidateAll`. P2 holds only with `Begin` placed at the lookup point. |
| :41–42 `TryGetValue` hit, `return cachedStatus` | 84–87 (`Hit`) | A cached `null` counts as a hit and returns `None`, as `TryGetValue<string?>` does. |
| :44 `await tenantRepository.GetAsync` | 75 (`Miss`), 135 (the read is recorded in `pending` with the key built at line 40) | `GetAsync` is modelled as the `Finish` event's `status` argument. **Deviation:** if `GetAsync` throws, or never completes, the trace has no `Finish` and the entry stays pending forever. That has no effect on the cache, which matches the code: when line 44 throws, line 46 never runs. |
| :45–47 `tenant?.Status`; `cache.Set(key, status, Ttl)`; return | 92–101 (`GetStatusFinish`), 137–138 (`Finish` case) | The Set uses the key built at `Begin` (line 40), not a fresh one. `Entry.writer` is ghost provenance and never affects behaviour. |
| :50 `Invalidate` | 103–110 (`CacheInvalidate`), 141 | A key that is too long gives no removal (`KeyFor` threw). The same fusing deviation as line 40 applies: the generation read and `Remove` are one step. |
| :52 `InvalidateAll` | 112–117 (`CacheInvalidateAll`), 140 | `Interlocked.Increment` is atomic, modelled as `generation + 1`. |
| Trace model (not code) | 123–199 | `Event`, `Step`, `Run`, `GenAt`, `ValidTrace` (`UniqueBegins` + `FinishesFollowMisses`; the latter means "every `Finish` follows a missing `Begin`"), `ServedFrom`, `FindBegin`/`BeganAt`, `ReadTenant`. If a read id has no `Begin`, `BeganAt` returns `\|t\|`. |
| Helper proofs | 207–462 | Generation is monotone, and goes up by exactly 1 at `InvalidateAll`. `KeyFor` is injective, because the digits contain no `:` and decimal printing is injective. `BeganAt` is unique when `Begin` ids are unique. A provenance invariant (`Inv`, `InvStep`, `InvAll`) gives every cache entry a writer that began at index b, with the key built from generation `GenAt(b)`, and finished at some m with b < m. |
| Properties | 466–480 (P1), 482–498 (P2) | Copied verbatim from the frozen text. |

#### `AuthInvalidationListener.ExecuteAsync`: T5 S15 `Iverson.Server/Iverson.Api/Tenancy/AuthInvalidationListener.cs` `ExecuteAsync, InvalidateCaches (43–97)` (C#)

| Worktree lines | Dafny lines | Note / deviation |
|---|---|---|
| 40–41 `FirstRetryDelay`, `MaxRetryDelay` | 46–47 | A `TimeSpan` is modelled as its `Ticks` in a `newtype Int64` (line 23): 10_000_000 and 300_000_000. |
| 43–46 entry: `connectionString`, `retryDelay = FirstRetryDelay` | 115–119 `Run` | The connection string (45) and `ListenerConnectionString` (90–91) are library and are not modelled. |
| 47 `while (!stoppingToken.IsCancellationRequested)` | 102–113 `Loop` (105, 108) | Cancellation seen at the loop head is folded into the next attempt being `Stopped`, which gives `[Return]`. When the script runs out, the observed trace ends (`[]`). |
| 49–62 try body: Notification handler (52), `OpenAsync` (53), LISTEN (54–55), `InvalidateCaches()` (58), `retryDelay = FirstRetryDelay` (59), `while(true) WaitAsync` (61–62) | 72–92 `TryBlock`, `Listens` arm (79–91) | The environment is the `Attempt` script (line 16), as approved. Notifications are modelled as arriving only during `WaitAsync`, after LISTEN and the explicit invalidation, per the approved model. A notification that Npgsql delivers while the LISTEN command itself runs is not modelled. `Listen` is emitted only when LISTEN succeeds. |
| 64–66 `catch (OperationCanceledException) when cancelled → return` | 76 (`Stopped`), 90–91 (`Listens(_, false)`), 108 (`+ [Return]`) | An OCE thrown while the token is not cancelled is an ordinary failure, so it becomes `OpenOrListenFails` or `Listens(_, true)`. |
| 68–75 `catch (Exception)`: counter and log | 77–78 (`OpenOrListenFails`), 87–88 (`Listens(_, true)`) | The counter and logger are ignored, as approved. |
| 77–84 `Task.Delay(retryDelay)` and its OCE → return | 110–112 (`Delay(t.retryDelay as int)`) | A cancelled delay is the next attempt's `Stopped`, so the `Delay` action is still emitted and `Return` follows it. The range checks that `Task.Delay` makes on its argument are not modelled; they cannot trigger, because the value stays between 1 s and 30 s. |
| 85 `retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks))` | 94–98 `NextRetryDelay`; 26–40 `Wrap64` / `WrapMul64` / `Int64Min` | Unchecked `long` multiply is modelled as wrapping. Lemma `NoOverflow` (345) proves no wrap occurs for values up to 30 s. |
| 93–97 `InvalidateCaches` | the `InvalidateBoth` action (20) | Both cache calls together count as one action. Their own failure modes are out of scope. |

#### `EngagementHealthChecker.CheckHealthAsync`: T15 S4 `Iverson.Server/Iverson.StarRocks/EngagementHealthChecker.cs` `CheckHealthAsync (12–30), ClassifyConnectionException (42–45), CreateBackendProbe (60–61), AnyBackendReadyAsync (73–83)` (C#)

| Worktree file:line | Dafny lines | Notes / deviations |
|---|---|---|
| 12–13 `CheckHealthAsync` signature | 79–80 `CheckHealth(env)` | Async becomes a pure function. The environment `HealthEnv` stands in for every MySqlConnector call. |
| 16 `CreateConnection()` | 81 (folded into `env.open`) | A constructor throw, such as a bad connection string, is `StepFails(NotMySql)` on `open`. |
| 17 `conn.OpenAsync()` | 81 | `env.open`. A failure goes to `ClassifyConnectionException`, as the catch at 26–28 does. |
| 19–20 `SELECT 1` `ExecuteScalarAsync` | 82 | `env.selectOne`. The scalar result is ignored, as in the code. |
| 22 `AnyBackendReadyAsync(conn)` | 84–85 | A throw is `Throws(e)`, then classified (26–28). |
| 23–24 ternary Healthy/Unhealthy | 86–87 | Same order as the code. |
| 26–28 `catch (Exception ex)` | 81, 82, 85 | **Deviation:** `DisposeAsync` throws from the `await using` blocks (16, 19, 75, 76) are not modelled. |
| 42–45 `ClassifyConnectionException` | 46–52 | `MySqlException{ErrorCode: AccessDenied}` becomes `AccessDenied`. Any other exception becomes another variant: `OtherError`, `CommandTimeout` or `NotMySql`. |
| 56, 58 constants | 56–57 | No logic. |
| 60–61 `CreateBackendProbe` | 54–57 | Builds the command and has no branches. The effect of `CommandTimeout = 5` is the environment outcome `ProbeFails(CommandTimeout)`. |
| 73 `AnyBackendReadyAsync` signature | 68 | `ct` is always `default` from this caller, so cancellation is not modelled. |
| 75–76 `CreateBackendProbe`, `ExecuteReaderAsync` | 70–71 | A throw is `ProbeFails(e)`, which becomes `Throws(e)`. |
| 78 `var rows = 0` | 73 | `rows` is `Int32`, a bounded newtype (line 32). |
| 79–80 `while (ReadAsync) rows++` | 59–65 `CountLoop`, 74 | `Rows(n)` means n reads return true, then one returns false. A `ReadAsync` throw partway through is folded into `ProbeFails`. `rows++` wraps (`Inc32`, lines 34–37): the project has no `CheckForOverflowUnderflow`, so C# runs unchecked. |
| 82 `return rows == 2` | 75 | |

#### `HarnessChannel.Create`: T23 S3 `Iverson.Server/Iverson.ClientConformance/HarnessChannel.cs` `CheckEndpoints (20–21), Create (28–37); with the unchanged Iverson.LoadTest/Auth/CredentialEndpoints.cs Check (24–53)` (C#)

| Worktree file:line (task/step) | Dafny lines | Notes / deviations |
|---|---|---|
| `Iverson.Server/Iverson.ClientConformance/HarnessChannel.cs:20–21` CheckEndpoints (T23 S3) | 147–150 | Direct call `Check(grpcUrl, tokenEndpoint, true)`. The `string?` token endpoint becomes `Option<CsString>`. |
| `HarnessChannel.cs:28–37` Create (T23 S3) | 152–161 | The `callCredentials` parameter is dropped: it is passed through unchanged, and the frozen property calls `Create(grpcUrl)`. `GrpcChannel.ForAddress` / `ChannelCredentials.Create` record a `ChannelPlan(credentials, unsafeInsecureCallCredentials)`, as the approved boundary says. Anything `ForAddress` might throw (URI parse failures, or a mismatch between credentials and scheme inside Grpc.Net.Client) is not modelled, per that boundary. |
| `HarnessChannel.cs:30` `StartsWith("https://", OrdinalIgnoreCase)` | 157 (`HttpsPrefix` const 152) | Uses the approved axiom `StartsWithOrdinalIgnoreCase` (37–39). |
| `HarnessChannel.cs:33–35` options | 158–160 | Field order in the constructor call differs; this has no effect. |
| `Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs:22–52` Check (unchanged; brought in by T23 S3) | 114–141 | **Path/line deviation:** the brief gives `Iverson.LoadTest/Auth/CredentialEndpoints.cs` (24–53). In the worktree the file is at `Iverson.Server/Iverson.LoadTest/...`, the signature is on line 22 and the body runs 23–52. |
| CredentialEndpoints.cs:24–28 (token endpoint shape) | 115–119 | `Uri.TryCreate(…, Absolute, out _)` → `UriParse(..).Some?` (approved axiom, 45). `EndsWith(Ordinal)` is modelled (51–54). The interpolated message is folded into one literal constant (96). |
| :30–34 (gRPC URL loopback/https) | 121–123 | `grpc.Scheme == Uri.UriSchemeHttps` → `UriParse(grpcUrl).value.scheme == UriSchemeHttps` (const "https", 48). |
| :36–37 | 125–126 | `IsHttpsOrLoopback` (66–67) → 109–112. |
| :42–45 (non-loopback https gRPC requires https token) | 128–130 | `grpc` is non-null here because the branch at 121 was passed, so `.value` is safe. |
| :47–49 + AuthentikBaseUrl :59–61 | 132–138; 103–107 | `tokenEndpoint[..IndexOf(…)]` is modelled with the throw as an outcome: `RangeTo` returns `Outcome<CsString>` = `Returns \| Throws` (74–81), and `IndexOf(Ordinal)` is modelled (58–71, `Int32` result). Check calls the proved lemma `AuthentikBaseUrlReturnsAfterShapeCheck` (186–196), which shows `Throws` cannot happen on this path. It is a verified lemma call, not an assume. |
| :51 `return null` | 140 | `None`. |
| Messages :27–49 | 96–101 | Each literal is wrapped as `var m: string := "…"; assert \|m\| < 0x8000_0000; m`, because Z3 could not discharge the `CsString` subset check for long literals directly. The text is unchanged. |

#### `Auth.BuildInvoker`: T23 S4 `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs` `BuildInvoker (24–74)` (C#)

| Worktree `Auth.cs` lines | Dafny lines | Note |
|---|---|---|
| 24–31 (signature) | 56 | `string?` parameters become `Option<CsString>`; `grpcUrl` is non-nullable, so it is plain `CsString`. **Deviation:** `actingToken` (line 29) is dropped. It only feeds `ActingUserInterceptor`, which the approved boundary ignores, and the frozen lemma signature has no such parameter. The method's return value (`CallInvoker`) becomes the `InvokerPlan` record from the approval list. |
| 35 `grpcUrl.StartsWith("https://", OrdinalIgnoreCase)` | 58 (with 27–28 axiom, 39 `HttpsPrefix`, 42–45 `HasHttpsScheme`) | This is the approved axiom. `"https://"` is the named constant `HttpsPrefix`. `HasHttpsScheme` is the same call, as in `HarnessChannel.Create`. |
| 36 `transport` | 59 (`Transport` datatype at 36) | `ChannelCredentials.SecureSsl` / `Insecure` become the enum `SecureSsl` / `Insecure`. |
| 37–42 `GrpcChannelOptions { UnsafeUseInsecureChannelCallCredentials = !tls }` | 60 | Only the modelled field is kept. |
| 50 `!string.IsNullOrEmpty(serviceToken)` | 61 (`IsNullOrEmpty` at 31–34) | `string.IsNullOrEmpty` is modelled as `None` or length 0. |
| 52–57 static `CallCredentials` + `ChannelCredentials.Create(transport, …)` | 62 | The interceptor body is ignored. Setting `options.Credentials` becomes `credentials := Some(transport)`. |
| 59–61 trio non-empty check | 63–65 | Same branch order and same short-circuit `&&`. |
| 63–70 `ServiceTokenProvider` + `CallCredentials` + `ChannelCredentials.Create` | 66 | The token provider and interceptor are ignored. This also becomes `credentials := Some(transport)`. |
| (implicit else: `options.Credentials` never set) | 67–68 | `credentials := None`. |
| 73–74 `GrpcChannel.ForAddress` + `Intercept(ActingUserInterceptor)` | 68 | Channel construction and the interceptor are ignored. The plan carries the options already chosen. |
| Approved vocabulary | 8–24 | `Option`, `CsString`, `IsAscii`, `AsciiLower` (with the helper `AsciiLowerChar`). |
| Approved axiom | 27–28 | Copied word for word. |
| Frozen lemma P1 | 72–79 | Header and clauses copied word for word. The body is empty. |

#### `conformance.grpcUsesTLS`: T23 S4 `Iverson.Clients/Go/conformance/main.go` `transportOption, grpcUsesTLS (248–259)` (Go)

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| `Iverson.Clients/Go/conformance/main.go:248` `func transportOption(addr string) grpc.DialOption` | 44 | The return type `grpc.DialOption` is modelled as `datatype DialTransport = TlsCreds \| InsecureCreds` (line 41), as the library boundary approves. |
| main.go:249 `if grpcUsesTLS(addr) {` | 46 | Same branch order. |
| main.go:250 `return grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{}))` | 47 | Modelled as `TlsCreds`. |
| main.go:252 `return grpc.WithInsecure()` | 49 | Modelled as `InsecureCreds`. |
| main.go:256 `func grpcUsesTLS(addr string) bool` | 52 | `string` becomes `seq<Byte>`. |
| main.go:257 `strings.HasPrefix(strings.ToLower(addr), "https://")` | 54 | `strings.ToLower` uses the approved axiom `StringsToLower` (lines 31–32). `strings.HasPrefix` is modelled as `StringsHasPrefix` (lines 35–38): `\|s\| >= \|p\| && s[..\|p\|] == p`. `"https://"` is the constant `HttpsBytes` (line 27). |
| (vocabulary) | 8–24 | `Byte`, `IsAscii` and `AsciiLower` (built from per-byte `AsciiLowerByte`) are defined as the shared vocabulary says. |
| (helper) | 58–62 | Lemma `AsciiLowerPrefix` shows that lowercasing commutes with taking a prefix. It proves with an empty body. |

#### `driver.parse_grpc_address`: T23 S4 `Iverson.Clients/Python/conformance/driver.py` `parse_grpc_address (342–346), build_driver_channel (349–391)` (Python)

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| driver.py:342 (signature, `grpc_addr: str`) | 44 `ParseGrpcAddress(grpcAddr: seq<CodePoint>): Parsed` | Python `str` becomes `seq<CodePoint>`. The return tuple becomes `Parsed`, and a raise becomes the `ParseRaised` variant. |
| driver.py:345 `"//" in grpc_addr` | 23–31 `OccursAt`/`Contains`, 47 | `in` is modelled as substring containment. |
| driver.py:345 `urlsplit(... else f"//{grpc_addr}")` | 39–41 (approved axiom), 47 | **Deviation:** `urlsplit`'s own `ValueError` (for example, unbalanced IPv6 brackets) is not modelled; the approved `SplitResult` has no field for it. This makes the obligations stronger, not weaker: those inputs would only add `ParseRaised` cases, where the lemmas hold vacuously. |
| driver.py:346 `parsed.hostname or "localhost"` | 49 | `None` and `""` are falsy, so either one gives `Localhost` (line 20). |
| driver.py:346 `parsed.port` (raises) | 51 | Modelled through `portRaises` and mapped to `ParseRaised`. The host is computed first, matching Python's left-to-right tuple evaluation; `.hostname` never raises. |
| driver.py:346 `parsed.port or 5000` | 54 | `None` and `0` are falsy, so either one gives 5000. |
| driver.py:346 `parsed.scheme == "https"` | 56 | Uses `HttpsScheme` (line 18). |
| driver.py:349–358 (signature) | 61–63, 83–85 | `Optional[str]` becomes `Option<seq<CodePoint>>`. **Deviation:** `host` and `port` are not modelled, because they only feed `address` and do not affect `ChannelKind`. |
| driver.py:368 `address = f"{host}:{port}"` | none | Not modelled, since it does not affect the transport kind. |
| driver.py:369–379 `service_token` / elif `client_id and client_secret and token_endpoint` | 65–68 (`Truthy`, 34–37) | `call_creds` is abstracted to its length. Python truthiness is modelled as `Some` and non-empty. The plugin objects are not modelled. |
| driver.py:380–381 `acting_token` | 69 | |
| driver.py:386–387 `if not call_creds: ssl \| insecure` | 75–76 | `grpc.secure_channel(…, ssl_channel_credentials())` becomes `SslChannel`, and `insecure_channel` becomes `InsecureChannel`. |
| driver.py:389–391 base creds, composite, `secure_channel` | 78–79 | ssl base becomes `SslCompositeChannel`; `local_channel_credentials` becomes `LocalCompositeChannel`. The `grpc` constructors are assumed not to raise, per the library boundary. |
| (whole function) | 83–88 `BuildDriverChannelFull` | Composes the call-credential count with the transport choice. |
| (helper) | 91–94 `PrefixDoubleSlash` | Helper lemma, proved. |

#### `driver.parseGrpcAddress`: T23 S4 `Iverson.Clients/TypeScript/conformance/driver.ts` `parseGrpcAddress (240–252)` (TypeScript)

| Worktree lines | Dafny lines | Note |
|---|---|---|
| 240 (signature) | 41, 43 | The return object `{host, port, useTls}` becomes `ParsedAddr(host, port, useTls)`. Every thrown `Error` becomes the `Throws` variant. |
| 241 `addr.includes('://') ? addr : \`http://${addr}\`` | 30–37 (`Contains`), 45–49 | `includes` is modelled as a recursive ordinal substring search over UTF-16 units. Template concatenation is `HttpSchemePrefix + addr`. **Deviation:** if the concatenated string would reach the `CsString` bound (2^31 units), the model returns `Throws`, standing for JS's RangeError on an over-long string. V8's real limit is lower (about 2^29), so this cutoff is an approximation. It is unreachable under P1's precondition. |
| 242–247 try `new URL(withScheme)` / catch, throw | 50–52 | Uses the approved axiom `UrlParse`; `None` means `new URL` threw, which maps to `Throws`. |
| 248 `if (!url.hostname) throw` | 55–56 | A falsy string means an empty one, so the check is `\|url.hostname\| == 0`. |
| 249 `url.port ? Number(url.port) : url.protocol === 'https:' ? 443 : 80` | 58–62 | `Number(url.port)` becomes `NumberFromString`. **Deviation:** the literals `443` and `80` are written as `NumberFromString("443")` and `NumberFromString("80")`. No literal constant of type `Number` was approved, and in JS `Number("443") === 443`. |
| 250 `!Number.isInteger(port) \|\| port <= 0` throw | 63–64 | The whole condition maps to `!NumberIsPositiveInteger(port)` (approved axiom). This includes NaN, since `Number.isInteger(NaN)` is false. |
| 251 return `{host, port, useTls: url.protocol === 'https:'}` | 66–67 | `HttpsProtocol` = `"https:"`. |
| — | 72–80 | P1 lemma. Its body adds a single helper `assert` (`("http://"+addr)[..7] == "http://"`) so the `UrlParse` axiom fires on the bare `host:port` path. |

#### `Driver.channelTransport`: T23 S4 `Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java` `the scheme test and channel builder (180–186)` (Java)

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| Driver.java:180 `ManagedChannelBuilder.forAddress(grpcUri.getHost(), grpcUri.getPort())` | 43 (the input `scheme`) | Deviation: host and port are not modelled because they do not affect the transport. `grpcUri.getScheme()` is the input `scheme: Option<CsString>`, and `None` stands for Java `null`. |
| Driver.java:181 `if ("https".equalsIgnoreCase(grpcUri.getScheme()))` | 39 (`Https` constant), 45–50 | The receiver and argument keep their order: `EqualsIgnoreCase(Https, s)`. Deviation: when the scheme is `null`, `String.equalsIgnoreCase(null)` returns `false` under the documented java.lang.String contract. That case is modelled as `case None => false` (line 48), outside the approved axiom, because the axiom only takes non-null `CsString`. |
| Driver.java:182 `channelBuilder.useTransportSecurity();` | 51–52 | Modelled as `TransportSecurity` (library boundary). |
| Driver.java:183–184 `else { channelBuilder.usePlaintext(); }` | 53–55 | Modelled as `Plaintext` (library boundary). |
| Driver.java:185 `}` | 56 | Same structure: the branch order is kept. |
| Driver.java:186 `ManagedChannel channel = channelBuilder` (continues with `.intercept(capture).build()` on 187–188) | none | Deviation: not modelled, because intercepting and building do not change the transport chosen at 181–184. |
| Library boundary | 32 (`JavaTransport`), 35–36 (approved axiom, copied exactly) | Shared vocabulary is at lines 8–26 (`Option`, `CsString`, `IsAscii`, `AsciiLower`). The helper `HttpsFacts` (ensures `IsAscii(Https)` and `AsciiLower(Https) == "https"`) is proved and has no trusted parts. |

#### `roles.RequireRole`: T6 S4 `Iverson.AdminUI/src/auth/roles.tsx` `RolesProvider state (40–52), hasRole (55–61), RequireRole (74–112)` (TypeScript/TSX)

| Worktree file:line | Dafny lines | Note on deviation |
|---|---|---|
| `api/types.ts:160–163` (`ConsoleRolesResponse`) | 21 | `datatype RolesResponse(operator: bool, tenantAdmin: bool)` |
| `roles.tsx:4` (`ApiFailure`) | 16 | Approved axiom `type Failure(==)`, opaque and only carried through |
| `roles.tsx:22` (`ConsoleRole`), plus `"either"` at 55 and 78 | 27 | String-literal union becomes `datatype Role = OperatorRole \| TenantAdminRole \| Either` |
| `roles.tsx:24–27` (`ConsoleRoleState`) | 24 | Discriminated union on `status` becomes `Checking \| Ready(roles) \| Unavailable(failure)` |
| `roles.tsx:40–42` (`useAuth`, `usePolledResource`) | 34–36 | Library boundary: `roles.data: T \| null` and `roles.failure: ApiFailure \| null` become `Option` inputs |
| `roles.tsx:44–49` (state ternary) | 36–41 | Same branch order: `data !== null` gives Ready, else `failure !== null` gives Unavailable, else Checking |
| `roles.tsx:51` (Provider JSX) | — | Publishing through context is library boundary; `ProviderState`'s result is the published value |
| `roles.tsx:55–58` (`hasRole` early return) | 44–47 | `status !== "ready"` gives `false` |
| `roles.tsx:60` (`hasRole` ternary) | 48–52 | `"either"` gives `operator \|\| tenantAdmin`; otherwise the indexed field `state.roles[role]` becomes a `match` on the role |
| `roles.tsx:63–67` (`DENIED`) | 55–84 | Each literal is a nullary function returning a `CsString`, with an `assert` on its length (53, 57 and 63 UTF-16 units, checked with `wc -c`). This is needed because the verifier cannot discharge the `CsString` bound on a raw `const` literal. `DeniedMessage` is the record lookup. |
| `roles.tsx:74–81` (signature, `useConsoleRoles()`) | 86–87 | The context read is library boundary; the state arrives as a parameter |
| `roles.tsx:83–89` (checking) | 89–90 | `Progress` stands for the `CircularProgress` JSX |
| `roles.tsx:91–99` (unavailable) | 91–92 | `ReadError` stands for the error `Alert` |
| `roles.tsx:101–109` (denied) | 93–94 | `Denied(DENIED[role])` stands for the warning `Alert` |
| `roles.tsx:111` (children) | 95–96 | `Children` |

#### `blueprints-configmap.orchestrator-tokenreview`: T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml` `the iverson-admin-orchestrator-tokenreview expression (141–168)` (Python, inside a Helm-rendered blueprint)

| Worktree lines | Dafny lines | Note |
|---|---|---|
| 141–146 (blueprint `id`, `model`, `identifiers`, `attrs`, `expression: \|`) | — | YAML metadata with no logic, so nothing to transliterate. |
| 147 `try:` / 167–168 `except Exception: return False` | 58 (`Ex<T>`), 109–116 (`Pin`) | The try body is `TryBody`, which returns `Ex<bool>`. `Exc` becomes `false`, so an exception is an outcome. |
| 148–149 `if not request.context.get("oauth_jwt"): return False` | 80–82 | A missing key is treated as `PyNone` (what `dict.get` returns), then checked with `Truthy`. |
| 150 `request.http_request.POST["client_assertion"]` | 83–84 | `formAssertion.None?` means a KeyError, so `Exc`. The assertion value only goes into the request, which the model replaces with the input `PostResult`, so the value itself is unused. |
| 151–159 `requests.post(…, timeout=2)` | 85–86, 30 | This is the input `PostResult`, as the library boundary says. `PostRaised` gives `Exc`. |
| 160–161 `if not 200 <= status_code < 300: return False` | 87–88 | Exact. |
| 162 `response.json().get("status") or {}` | 89–94, 62–71 | `json.None?` means `.json()` raised, so `Exc`. `DictGet` on a value that isn't a dict is an AttributeError, so `Exc`. `OrEmpty` models `or {}`. |
| 164 `status.get("authenticated") is True` | 95–99 | `is True` is modelled as `== PyBool(true)`: a JSON `true` is the `True` singleton, and `1 is True` is False. A failing `and` short-circuits to `Ok(false)`. |
| 165 `(status.get("user") or {}).get("username") == "system:serviceaccount:…-api"` | 100–106 | Python `==` between a str and any value is modelled as `n == PyStr(expected)`. The Helm-rendered literal is the input `expected`. |
| 163–166 `return (… and …)` | 96–106 | Both operands are bool, so the return value is always a bool. |
| (vocabulary) | 10–56, 119–145 | `Truthy` deviation: `PyOther` counts as truthy (Python's default for an object). No property depends on it except through `Truthy` itself. |

#### `blueprints-configmap.orchestrator-workload`: T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml` `the iverson-admin-orchestrator-workload expression (100–118)` (Python, inside a Helm-rendered blueprint)

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| 14 `$issuer := include "authentik.serviceAccountIssuer"` (T8 S3) | parameter `issuer` (134, 176) | `issuer` is the value of the rendered `"{{ $issuer }}"` Python literal. Helm puts the value in without escaping, so a value containing `"`, `\` or a newline would make the literal differ from the raw value. That case is outside the model. Every issuer in the values files is a plain URL. |
| 102 `jwt = request.context.get("oauth_jwt") or {}` | 138 (`OrEmptyDict`, 46–49; `Truthy`, 34–44) | `Option<PyVal>` None means the key is absent. A falsy value becomes `{}`. |
| 103 `aud = jwt.get("aud")` | 140–142 | `.get` on a value that is not a dict raises AttributeError, modelled as `Raised` at 140. `DictGet` (52–55) returns `PyNone` when the key is absent. |
| 104 `auds = …` | 144 | — |
| 105–106 `iat`, `exp` | 146–147 | — |
| 107 `k8s = jwt.get("kubernetes.io") or {}` | 149 | — |
| 108 `user = ak_user_by(...)` | 151 | Approved axiom `AkUserBy`. Its call position is kept; it is total, so it has no effect on the outcome. |
| 110 `jwt.get("iss") == "{{ $issuer }}"` (T8 S3) | 153 | `StrEq` (73–77): `str == v` holds only for an equal `str`. |
| 111 `sub` pin | 154 (`ExpectedSub`, 118–121) | Deviation: the approved signature `OrchestratorWorkload(jwt, issuer, env)` has no release. The 3-arg predicate (176–179) is therefore "admitted for **some** namespace/release". Proving it false proves refusal under every rendering, which is the stronger claim. |
| 112 `"iverson-authentik-orchestrator" in auds` | 155 (`ListHasStr`, 80–83) | — |
| 113 `isinstance(iat, int) and isinstance(exp, int)` | 156–157 (`IsInt`, 58–62) | `bool` counts as `int`, as Python does. |
| 114 `exp - iat <= 600` | 158 (`IntVal`, 64–70) | Python ints are unbounded; True and False count as 1 and 0. |
| 115 `"pod" in k8s` | 160–162 (`PyContainsStr`, 96–103) | dict: key test; list: membership; str: substring (`SubAt`/`IsSubstring`, 85–93). Anything else raises TypeError → `Raised`. Deviation: `PyOther` (a float or other object) is taken to raise. |
| 116 `user is not None` | 163 | — |
| 117 `ak_is_group_member(user, name=...)` | 164 | Approved axiom `AkIsGroupMember`. |
| `and` chain, 109–118 | 153–164 | The branch order follows the short-circuit order. Every operand is a `bool`, so `and` returns a `bool`. |
| expression → policy result | 168–173 (`PolicyResult`) | An expression that raises is a failed policy (`False`). |
| — | 34–44 | Deviation: `Truthy(PyOther)` is taken as true. Both choices refuse at 102 and 107: a truthy `PyOther` raises at `.get`, and a falsy one becomes `{}`, which has no `iss` and no `pod`. |
| `JwtGet` (approved model) | 125–129 | When the value is not a dict, `JwtGet` returns None; the real code raises there and is refused. |
| helper | 182–191 | `IssuerMismatchRefusesEveryRelease` is proved. |
| P1 | 194–204 | The frozen header and clauses are verbatim. The body is a `forall` over namespace and release that calls the helper. |

#### `secret-service-clients.admin-automation-workload`: T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml` `the iverson-admin-automation-workload expression (775–787)` (Python, inside a Helm-rendered blueprint)

| Worktree lines | Dafny lines | Note |
|---|---|---|
| 29 (`$issuer := include "authentik.serviceAccountIssuer"`), 27–28 (`$ns`, `$rel`) | 102–105 (parameters `issuer`, `ns`, `rel`) | **Deviation:** `issuer` is the value of the rendered `"{{ $issuer }}"` Python literal, not the raw Helm value. This differs only if the value contains `"` or `\`. Namespace and release become parameters, and the predicate `AdminAutomationWorkload` (lines 134–138) is "true for some `ns` and `rel`". This is needed because the frozen lemma signature has neither. |
| 775 `expression: \|` | 104–125 `AdminAutomationExpr` | The policy result is `PolicyResult` (127–132), where a raised exception means `False`. |
| 776 `jwt = request.context.get("oauth_jwt") or {}` | 108 | `Option<PyVal>` stands for absent or present. `OrEmptyDict` uses Python truthiness (28–44). **Deviation:** `PyOther` (a float or other object) counts as truthy. Any truthy non-dict raises at 777, so the policy refuses either way. |
| 777 `aud = jwt.get("aud")` | 110, 112 | `.get` on a non-dict raises (`Raised`). This is the only path that raises. |
| 778 `auds = aud if isinstance(aud, list) else [aud]` | 114 | — |
| 779–780 `iat`, `exp` | 116–117 | — |
| 781–787 `return ( … and … )` | 119–124 | The short-circuit `and` chain becomes `if/else` steps in source order. Every operand is a `bool`, so the chain's value is a `bool`. |
| 782 `jwt.get("iss") == "{{ $issuer }}"` | 119 | `StrEq`: `==` against a str is true only for an equal str and never raises. |
| 783 `jwt.get("sub") == "system:serviceaccount:…-admin-automation"` | 120, `ExpectedSub` 86–89 | — |
| 784 `"iverson-authentik-admin-automation" in auds` | 121, `ListHasStr` 73–76 | `auds` is always a list, so `in` compares elements with `==` and never raises. |
| 785 `isinstance(iat, int) and isinstance(exp, int)` | 122–123, `IsInt` 53–56 | `bool` counts as `int`, as in Python. `IntVal` maps `True`/`False` to 1/0. |
| 786 `exp - iat <= 600` | 124 | Python `int` is unbounded, so this uses Dafny `int`. |

#### `secret-service-clients.console-admins`: T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml` `the iverson-console-admins expression (352–358)` (Python with a Helm-rendered list)

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml:352–356` (T9 S3): the `id: console-admins`, model, `identifiers.name: iverson-console-admins` and `attrs.expression` keys | 30 (function name `ConsoleAdmins`) | This is blueprint metadata with no logic. Only the policy's name and identity carry over, through the function. |
| `…secret-service-clients.yaml:357` (T9 S3): `name="tenant-admins"` | 20–21 `const TenantAdminsGroup` | The Python string literal is written as its 13 code points. |
| `…secret-service-clients.yaml:357` (T9 S3): `ak_is_group_member(request.user, name="tenant-admins")` | 32, first operand; axiom at 15 | This is the approved axiom `AkIsGroupMember`. `env` stands for Authentik's group state, which the real call reads implicitly. |
| `…secret-service-clients.yaml:357` (T9 S3): `request.user.uid` | 32; axiom at 17 | This is the approved axiom `UserUid`. |
| `…secret-service-clients.yaml:357` (T9 S3): `{{ .Values.global.operatorSubjects \| default (list) \| toJson }}` | input `subjects` (30) | Library boundary as approved. The Helm `toJson` output, Python list literal of the same hex-digest strings, becomes the input `subjects`. An unset value renders `[]`, which is the empty sequence. |
| `…secret-service-clients.yaml:357` (T9 S3): `return A or B` | 32 `A \|\| B` | Both operands are bool and Python `or` short-circuits, so the returned value is exactly Dafny's `\|\|`. List `in` is membership by string equality, which is Dafny sequence `in`. |
| `…secret-service-clients.yaml:358` (T9 S3): the following `policybinding` entry | none | This entry is out of the candidate's logic and is not modelled. |

#### `compose-service-clients.console-admins`: T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` `the iverson-console-admins expression (194–199)` (Python)

| Worktree `file:line` (task/step) | Dafny lines | Note |
|---|---|---|
| `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml:193–198` (T9 S3): the `expressionpolicy` entry with id `console-admins`, name `iverson-console-admins` and the `expression: \|` key | 24 (name of `ComposeConsoleAdmins`) | This is blueprint YAML with no logic. It only names and wraps the policy. |
| `…/compose-only/service-clients.yaml:199` (T9 S3): `return ak_is_group_member(request.user, name="tenant-admins") or ak_is_group_member(request.user, name="operators")` | 24–27 (`ComposeConsoleAdmins`), 17–18 (constants) | `request.user` becomes the `user` parameter. Authentik's evaluation context becomes the opaque `env: AkEnv`. `ak_is_group_member(..., name=X)` becomes the approved axiom `AkIsGroupMember(env, user, X)`. Python's `or` over two bools short-circuits and returns a bool, so it maps directly to Dafny's `\|\|`. The literals `"tenant-admins"` and `"operators"` become the `seq<CodePoint>` constants `TenantAdminsGroup` and `OperatorsGroup`. There is no exception path: the axiom is total, as approved. |
| (vocabulary) | 7 | `newtype CodePoint = x: int \| 0 <= x < 0x110000`, the shared vocabulary for Python strings. |
| (approved axioms) | 10, 12, 14 | `type AkEnv`, `type AkUser` and `function AkIsGroupMember`, copied exactly. |
| (frozen property) | 29–33 | P1's header and clause are copied exactly, and the body `{` opens on line 32. |
| (helper sanity lemma) | 36–40 | `ConstantsSpellTheLiterals` checks the constants' lengths (13 and 9) and that they differ. It is not an obligation. |

#### `recovery-flow.login-throttle`: T9 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/recovery-flow.yaml` `the iverson-login-throttle expression (71–79)` (Python)

| Worktree lines | Python | Dafny lines | Notes on deviations |
|---|---|---|---|
| 71–73 | `expression: \|`, the imports of `Sum` and `Reputation` | 26–31 (`RepRow`, `PendingUser`), 37–74 (ORM model) | The library boundary is modelled over a row list. |
| 74 | `ip = str(ak_client_ip)` | 94 (parameter `clientIp`), 96 | `str(ak_client_ip)` is an input, as the boundary says. |
| 75 | `user = request.user` | 94 (parameter `user: PendingUser`) | The pending user is an input. `getattr(…, None)` becomes `Option`. |
| 76 | `identifiers = [i for i in (username, email) if i]` | 76–79 (`Truthy`), 86–90 (`Identifiers`), 97 | Python truthiness: `None` and `""` are dropped. Username comes first, then email. |
| 77 | `pair = filter(ip=ip, identifier__in=identifiers).aggregate(total=Sum("score"))["total"] or 0` | 37–45 (`FilterPair`), 57–61 (`Total`), 64–67 (`SqlSum`), 70–73 (`OrZero`), 98 | `identifier__in` is a membership test, so a row counts once even if its identifier is listed twice. The filter recurses from the last row; order does not affect Sum. SQL SUM over no rows gives `None`. `x or 0` maps both `None` and `0` to 0. |
| 78 | `address = filter(ip=ip, score__lt=0).aggregate(...)["total"] or 0` | 47–55 (`FilterNeg`), 99; 104–107 (`AddressAggregate`) | `AddressAggregate` is the same `address` value, as specified by the frozen properties. |
| 79 | `return pair <= -5 or address <= -50` | 100 | `or` short-circuits in Python, but both sides are pure, so this is equivalent. |
| (environment) | Authentik's scoring and the deny stage at order 12 | 118–180 | `Step`, `PairAfter`, `Repeat` and `CountOf` are copied from the design's `LoginThrottle.dfy`, over the approved `ClampedAdd` (lines 20–21). `AdmissibleFor` (149–155) evaluates `!Throttle(OneRow(ip, username, s0), ip, u)` once per plan, so it requires `u.username.Some?`. **Deviation:** the design's `UnknownPlanStep` used its own `RefusesNewPlan` for the in-plan refusal. Here it uses the transliterated `Throttle` on a fixed one-row table (`EnvIp = []`, `EnvId = [0]`, lines 161–169), because the frozen `UnknownPairAtOrder12(k)` takes no address or identifier. The helper `OneRowThrottle` (186–201) proves the decision is `p <= -5` for every address, every identifier in `Identifiers(u)` and every email, so this choice changes nothing. |
| (helpers) | (none) | 186–385 | Each contract is proved: `OneRowThrottle`, `TotalAppend`, `NegTotalBound`, `FilterPairSubset`, `TotalNonPos`, `PairTotalAtMost`, `NoRowsAtIp`, plus `PairAfterConcat`, `RepeatWrongPassword`, `RepeatWrongCode`, `RepeatSuccess`, `Interleaved`/`InterleavedFacts` and `UnknownAfterPlansExact`, adapted from the design. |
| (properties) | (none) | 389–464 | The frozen headers and clauses are copied exactly. Each body `{` opens on the line after the last clause. |

#### `deployment-server.wait-for-migration`: T7 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml` `the wait-for-migration init container's python -c program (64–102)` (Python, inside Helm)

| Worktree lines | Dafny lines | Note |
|---|---|---|
| 64–67 (imports: `time`, `connect`, `authentik_version`/`authentik_build_hash`, `CONFIG`) | 11, 14 (`Version`, `Build`); 23, 27 (`DbState`, `Probe`) | Library boundary. `authentik_version()`/`authentik_build_hash()` become the inputs `v`, `b`. `connect` and `CONFIG` fold into the probe outcome. |
| 69–75 (`LOCK_1000` SQL) | 23 (`lock1000` field) | Axiomatised as `DbState.lock1000`: an advisory key-1000 row in this database, held or awaited. |
| 77–79 (`def migrated()`, `with connect(...)`, `cursor`) | 44 | Any exception from connect, execute, fetchone or close becomes `Probe.Raised` (27). |
| 80 (`SET search_path`) | none | Folded into the probe. A failure there is `Raised`. |
| 81–83 (`to_regclass` check, `return False`) | 48 | Same branch order. |
| 84–90 (version/build `EXISTS` check, `return False`) | 51 | Same branch order. |
| 91–92 (`LOCK_1000`, `return not ...`) | 53 | Same. |
| 94 (`while True:`) | 64–74 (`WaitFrom`), 77–80 (`Wait`) | The endless loop is modelled over a finite sequence of probes. `StillWaiting` means it has not broken within those probes. `Finished(i)` gives the iteration at which it breaks. |
| 95–98 (`try: if migrated(): print; break`) | 70–71 | `print` ignored per the library boundary. |
| 99 (waiting `print`) | 72 | `print` ignored; falls through to the next probe. |
| 100–101 (`except Exception: print`) | 73–74 | The raising path is an outcome: continue with the next probe. |
| 102 (`time.sleep(5)`) | the `i + 1` step | Timing is not modelled. |

#### `admission-policy.validations`: T11 S3 `Iverson.Server/deploy/helm/iverson/templates/admission-policy.yaml` `variables and validations (43–126), with matchConstraints (35–42) and the binding (128–138)` (CEL, inside Helm)

| Worktree file:line | Dafny lines | Notes on deviations |
|---|---|---|
| :34 `failurePolicy: Fail`, :134 `validationActions: [Deny]` | 376–381 `Admits` | `!MatchesPolicy ∨ ∀i<6. Validation(i) == CTrue`, so an error or a false validation denies. |
| :35–42 `matchConstraints.resourceRules` | 138–155 `MatchesRule` | `apiVersions: ["v1"]` is not modelled because `AReq` carries no version. A rule matches only the resource or subresource it names, so `deployments/scale` and `*/status` are not matched. |
| :128–138 binding, `namespaceSelector` | 157–161 `MatchesPolicy` | The `kubernetes.io/metadata.name` label is taken as the namespace name, so this is `r.ns == c.releaseNs`. `policyName` is just the link to the policy. |
| :44–45 `protected` | 167–171 `VProtected` | Helm's `toJson` list becomes a Dafny sequence literal. |
| :46–47 `operator` | 173–178 `VOperator` | `has(groups) && groups.exists(g, g in OPS)`. When groups are absent the right side is `CError`, which `And(CFalse, ·)` absorbs. |
| :48–49 `node` | 180–185 `VNode` | Same shape as `operator`. |
| :50–51 `res` | 187–188 `VRes` | |
| :52–53 `sub` | 190–191 `VSub` | `AReq.sub` already holds `has(request.subResource) ? request.subResource : ''`, as the model note specifies. |
| :54–56 `connectPod` | 193–204 `ConnectPrefixes`, `VConnectPod` | |
| :57–62 `podSa` | 206–218 `PodSaField`, `VPodSa` | A null object is `SErr` inside the branch. The ternary's condition is evaluated with three-valued `&&`. |
| :63–70 `newTplSa` | 220–244 `WorkloadFive`, `TplSaField`, `CronTplSaField`, `VNewTplSa` | `has(object.spec.template.spec.serviceAccountName)` becomes `Obj.tplSa`; the CronJob path becomes `Obj.cronTplSa`. Schema validation is assumed to supply the intermediate `spec`/`template` fields. |
| :71–77 `oldTplSa` | 246–254 `VOldTplSa` | Same as `newTplSa`, on `oldObject`. |
| :78–83 `imagesChanged` | 256–283 `MapStrs`, `CMap`, `InitMap`, `VImagesChanged` | `map(c, c.name + '=' + c.image)` and list `!=`. A null object gives `LErr`. |
| :87–91 rule (a) | 291–300 `GuardA`, `ValA` | `messageExpression` is not modelled because it has no effect on admission. |
| :92–96 rule (b) | 302–311 `GuardB`, `ValB` | |
| :97–106 rule (c) | 288, 313–331 `PodControllers`, `GuardC`, `ObjNameStartsWith`, `ValC` | `object.metadata.name` on a null object or an absent name is `CError`. |
| :107–117 rule (d) | 289, 333–342 `WorkloadControllers`, `GuardD`, `ValD` | |
| :118–122 rule (e) | 344–353 `GuardE`, `ValE` | |
| :123–126 rule (f) | 355–364 `GuardF`, `ValF` | |
| (all validations) | 366–374 `Validation(i)` | Indexes 0 to 5 map to (a) to (f). Any index of 5 or more maps to (f), so no `requires` is needed. |
| (request shape) | 10–32, 23–28 `ObjectOf` | **Deviation:** `ObjectOf(r)` is a positional-match accessor standing in for `r.object`, which Dafny cannot parse. The transliteration uses it everywhere `object` is read. Semantics are unchanged. |
| (constants) | 38–76 | CEL string literals written as `seq<CodePoint>` literals, each with the source string in a comment. `ContainerName` = `"api"` names the single container in `PodCreateBy`; it does not affect any property. |
| (spec vocabulary) | 384–470 | Written as in the approval list. `PodSaOf`, `NewTplSa` and `OldTplSa` are the CEL variables' values, with `SErr` mapped to `""`. Helper lemmas at 474–536 show that `SErr` never arises. |

#### `configmap-tls-proxy.public-locations`: T8 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml` `the :9080 server block's locations (72–115)` (nginx configuration)

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| yaml:72–88 (`server {`, listen/ssl, `proxy_bind`, HSTS, `proxy_set_header` ×4) | not modelled | Server-level directives that play no part in choosing a location; the properties are only about routing outcomes. |
| yaml:89 regex (the five slugs) | 78–82 `AllowedSlugs()`, 66–75 slug constants | `SlugImplicitConsent` (dfy:71) is written as `[…"default-"] + […rest]`. This is only so the solver can index the 47-byte literal; the value is the same. |
| yaml:89 regex match | 90–91 `FlowRegexMatches` (approved axiom), used at 135 | Library boundary, with PCRE's default `$`. |
| yaml:90–92 `= /flows/-/…` | 118–120 | Exact → `Forward`. |
| yaml:93 `/static/`, yaml:94 `/media/` | 107–113 `LongestPrefixOutcome` (109, 110) | Prefix → `Forward`. |
| yaml:95–104 `= /ws/client/` | 121 | Exact → `Forward`. The header directives aren't modelled. |
| yaml:105–111 `= /application/o/…` | 122–128 | Exact → `Forward`. |
| yaml:112 `= /-/health/live/` | 129 | Exact → `Forward`. |
| yaml:113 `= / { return 302 … }` | 130 | Exact → `Redirect`. |
| yaml:114 `location / { return 404; }` | 111 | Prefix → `NotFound`. |
| nginx location-selection algorithm | 115–138 `Route` | Deviation: branches follow nginx's evaluation order (exact first, then the longest prefix is remembered, then the regex, then the remembered prefix), not the file's textual order, where the regex at line 89 comes first. Within each class, file order is kept. |
| (no location matches) | 112 | Deviation: modelled as `NotFound`. It can't be reached, because a normalised URI always starts with `/`, and `Outcome` is frozen with three variants. |
| URI normalisation | none | Taken as already done before `Route`, as the library boundary says. |

#### `job-create-user.create-user`: T15 S4 (4c), T19 S3 (3c–3e) `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml` `the create-user container's sh script (62–111)` (POSIX sh, inside Helm)

| Worktree `job-create-user.yaml` lines | Introduced by | Dafny lines | Notes / deviations |
|---|---|---|---|
| 62 `set -e`, 63 `FE_HOST`, 65 `BE_REPLICAS` | pre-existing | 110–114 (`JobCalls`), parameter `beReplicas: nat` | `FE_HOST` is not modelled because it doesn't affect any property. `set -e` is modelled as the trace ending after a failed `SET PASSWORD` (140–141). |
| 64 `FE_REPLICAS` | T19 S3 (3c) | parameter `feReplicas: nat` | Helm values are modelled as `nat`. A non-integer value would make `[ -ge ]` error, so the loop would never exit; that case is not modelled. |
| 71 `TLS=…` | pre-existing (round 11) | — | TLS flags are ignored. A failed TLS connection is just a non-zero `MysqlRun` exit. |
| 73–76 `fe()` | pre-existing | 99–102 (`Fe`) | `-p"$ROOT_PW"` is modelled as `Some(rootPw)` even when `ROOT_PW` is empty, where the real client would prompt. |
| 77–87 FE-wait `until` (78 probe with password, 79 `ROOT_HAS_PW=1`, 80 probe without, 81 `ROOT_HAS_PW=0`, 83 `false`, 86 `sleep`) | pre-existing | 116–133 (`ProbeLoop`) | Same branch order. The view at call n is `world(n)`. `echo` and `sleep` are ignored. |
| 93 `fe -e "SET PASSWORD …"`, 94 `ROOT_HAS_PW=1` | T19 S3 (3d) | 135–144 (`SetPasswordStep`), 104–108 (`ViewAfterSet`) | A non-zero exit ends the trace (`set -e`). Once the step succeeds, every later call sees `world(m)` with `rootPassword := Some(rootPw)`, as in the approved model note. |
| 95–98 BE/FE wait `until … && …` | T19 S3 (3d) | 146–160 (`WaitLoop`) | `&&` short-circuits as in sh. `fe … 2>/dev/null \| wc -l` is modelled as `\|MysqlRun(...).out\|`, whatever the exit code; this assumes one newline-terminated line per element. |
| 99–111 batch `fe -e "…"` (100–108 and 110 pre-existing, quoted by T19 3d) | T19 S3 (3d) moved its start | 162–166 (`BatchStep`), 64–92 (`BatchLines` and its line constants) | The model ends at 111 whatever the batch's exit. `lines` has one element per statement, with the YAML block's 2-space indentation and the blank first and last lines dropped. `$APP_PW` and `$PROVISIONER_PW` are expanded as uninterpreted consts `AppPw` and `ProvisionerPw` (56–57); a password containing a newline would be one element here but split into several real lines. Long literals are written as `"X " + "rest"` because Dafny 4.11 cannot index string literals of about 50 or more characters; the bytes are identical. `Bytes` maps `char → int % 256`, which is the identity for these ASCII literals. |
| 109 `REVOKE OPERATE ON SYSTEM FROM USER 'iverson_app'@'%';` (and the removed `GRANT OPERATE`) | T15 S4 (4c) | 75, 90 | — |
| (deletion of the old `ROOT_HAS_PW=1` block) | T19 S3 (3e) | — | Nothing left to transliterate. |
| unbounded `until` loops | — | 58–61 `const Fuel: nat` (uninterpreted) | An infinite run is truncated to its first `Fuel` calls. Because `Fuel` is arbitrary, the lemmas hold for every prefix length. |
| property vocabulary | — | 168–189 | `FeAnswered`: some `SELECT 1` call is followed by a call that is not a probe. `AfterProbe`: the trace with its leading `SelectOne` run dropped. `IsPrefix` is at 50–53. |
| helper lemmas | — | 191–517 | All helper lemmas are proved. They use no `assume`, `{:axiom}`, `{:induction}` or `{:verify false}`. |

#### `setup.sh.guards`: T14 S4 (4d), T16 S3 (3c), T17 S5 (5a–5d), T20 S3 (3d–3e) `Iverson.Server/deploy/kind/setup.sh` `the kind version gate (4–11), the --only parser (44–52), ambient_enroll (106–115), the --only dispatch (136–140)` (bash)

| setup.sh lines (task/step) | Dafny lines | Deviation notes |
|---|---|---|
| 4–6 `KIND_MIN=v0.33.0` (T16 S3 3c) | 196 | none |
| 7 `KIND_VERSION="$(kind version \| awk '{ print $2 }')"` (T16 S3 3c) | 200–201, with helpers `AwkPrint2`/`AwkField2`/`SkipBlanks`/`TakeField` 99–135 and `StripTrailingNL` 92–97 | The input is the `kind version` stdout. A failing `kind` (pipefail plus set -e exit) is not modelled. awk fields are split on runs of space or tab, records end at `\n`, and `$(…)` strips trailing newlines. |
| 8 `${KIND_VERSION%%-*}` piped to `sort -V \| head -n 1` (T16 S3 3c) | 202–203, `CutAtFirst` 86–90, approved axiom `SortVFirst` 188–189 | `printf '%s\n%s\n' … \| sort -V \| head -n 1` inside `$(…)` is modelled as `SortVFirst(KIND_MIN, v)`. This is exact only when `v` has no newline, which holds for every `KindVersionLine` input. |
| 9–10 echo, then `exit 1` | 204 `Refused` | none |
| 11 `fi` (falls through) | 206 `Passed` | Lines 13–43, the operator pins and `helm dependency list`, are outside the candidate. `ParseAndDispatch` assumes they succeeded. |
| 44 `PHASES=…` (T14 S4 4d) | 229–247 (literals), 236 | none |
| 45–46 `ONLY=""`, `[ "$#" -gt 0 ]` | 262–264, 271 | none |
| 47 `[ "$#" -ne 2 ] \|\| [ "$1" != "--only" ] \|\| [[ " $PHASES " != *" $2 "* ]]` | 265–266, with `Occurs`/`Pad` 47–61 | The quoted `$2` inside the glob is literal, so the test is a substring check. The `\|\|` short-circuit order is kept. |
| 48–49 usage message, then `exit 2` | 267 `UsageExit2` | none |
| 51 `ONLY="$2"` | 269 | none |
| 106–109 `ambient_enroll`, `policies=$(…)`, `ports=" $(…) "` (T17 S5 5a–5d) | 291–294 | The inputs are the `$(…)` values: `policyNames` for the `-o name` output and `ports` for the `jsonpath '{..port}'` output. A raw `-o name` stdout made only of newlines (which kubectl never produces) would become empty. |
| 110 `[ -n "$policies" ] && [ "${ports#* 15008 }" = "$ports" ]` | 295–296, `StripShortestStarLit`/`StripScan` 65–78 | `${x#*lit}` removes the shortest prefix that ends in `lit`. |
| 111–112 echo, then `return 0` | 297 `false` | none |
| 114 `kubectl label namespace …` | 299 `true` | A failing `kubectl label` (set -e) still counts as "labelled" (attempted). |
| 137 `[ -n "$ONLY" ]` (T20 S3 3d–3e) | 276–277 | none |
| 138–139 `"phase_${ONLY//-/_}"`, then `exit 0` | 278, with `ReplaceAll` 80–84 and `CallFunction` 251–258 | The four defined `phase_*` functions (54, 83, 117, 122) map to `RunPhase(p)`. Any other name gives command not found, exit 127 under set -e, which is `OtherExit(127)`. A phase that fails mid-run is still `RunPhase`. |
| 140 `fi`, then 142– fresh install | 280 `FullInstall` | none |

#### `setup.ps1.guards`: T14 S4 (4d), T16 S3 (3c), T17 S5 (5e–5h), T20 S3 (3f–3i) `Iverson.Server/deploy/kind/setup.ps1` `the kind version gate (3–9), the --only parser (43–51), Add-AmbientLabels (96–110), the dispatch (144–152)` (PowerShell)

| Worktree lines (task/step) | Dafny lines | Notes / deviations |
|---|---|---|
| 3–4 comment, 5 `$KindMin = [version]"0.33.0"` (T16 S3 3c) | 202–204 | The cast is `VersionParse` (approved axiom). If it returns None, the outcome is `VersionCastThrown`. |
| 6 `("$(kind version)" -split '\s+')[1]` (T16 S3 3c) | 206–208, with `SplitWs` 61–70 and `IsWs` 34–39 | `-split '\s+'` is modelled as Regex.Split over the .NET `\s` class (`\f\n\r\t\v\x85` plus `\p{Z}`), keeping empty fields. An index past the end gives `$null`, and line 7 then throws: modelled as the added outcome `KindVersionNullThrown`. |
| 7 `.TrimStart('v') -split '-'` `[0]`, `[version]` cast, `-lt` (T16 S3 3c) | 209–216, `TrimStartChar` 96–100, `SplitOnChar` 87–93, `VersionLt` 194–197 | `-lt` on `System.Version` is a lexicographic compare of three fields (Revision is -1 on both sides). A failed cast gives `VersionCastThrown`. |
| 8 `throw "kind … older …"` (T16 S3 3c) | 215 (`Refused`) | `GateOutcome` gained two outcomes for the throwing paths: `KindVersionNullThrown` and `VersionCastThrown` (line 191). |
| 43 `$Phases` (T14 S4 4d) | 245–246, `PhasesPs` 183–186 | |
| 44–45 `$Only = ""`, `if ($args.Count -gt 0)` (T14 S4 4d) | 247–249, 255–256 | |
| 46 `-ne 2 -or -notmatch '^--?only$' -or -notcontains` (T14 S4 4d) | 250, `MatchesOnlyRegex`/`OnlyFlagCore`/`OnlyWordCI` 225–240, `ContainsPs` 103–107 | No axiom was approved for `-notmatch`, so the regex is modelled by .NET semantics: IgnoreCase, and `$` also matches before a final `\n`. `-notcontains` uses the `CaseFold` equality. |
| 47 `throw "usage: …"` (T14 S4 4d) | 252 (`UsageThrown`) | |
| 49 `$Only = $args[1]` (T14 S4 4d) | 254 | |
| 96–103 comments, 104 `$Policies = kubectl …` (T17 S5 5e–5h) | 274–284 | The `kubectl` output is the input `Option<CsString>`: None means no output, and several lines count as truthy (`TruthyPs` 277–280). |
| 105 `$Ports = "$(…)" -split '\s+'` (T17 S5 5e–5h) | 285–286 | |
| 106 `if ($Policies -and -not ($Ports -contains '15008'))` (T17 S5 5e–5h) | 287–288 | |
| 107 not enrolled / 109 `kubectl label …` (T17 S5 5e–5h) | 289 / 291 | `EnrollPs` returns true if and only if line 109 runs. |
| 144 `if ($Only)` (T20 S3 3f–3i) | 262–263 | An empty string is false. |
| 145–150 `switch ($Only)` (T20 S3 3f–3i) | 264–269 | Each arm compares with `CaseFold` equality. `RunPhasePs(only)` means "the arm whose label `-eq`s `only` ran". |
| 151 `exit 0` (T20 S3 3f–3i) | 269 | I added the variant `NoArmMatchedExit0` to `DispatchPs`, because the switch has no default arm. |
| 154–244 full install | 271 (`FullInstallPs`) | |

#### `generate-compose-secrets.renew`: T22 S3 `scripts/generate-compose-secrets.sh` `LEAVES, the renew decision and the regenerate branch (53–89)` (bash)

| Worktree lines | Dafny lines | Notes |
|---|---|---|
| 51 `TLS_DIR=…` | 15 `Fs`, 18 `Loc.TlsDir` | Deviation: paths are modelled as basenames relative to `TLS_DIR`. `Loc.WorkDir` stands for the `mktemp -d` directory, which the approved boundary puts outside `TLS_DIR`. |
| 52–59 `LEAVES=(…)` | 47–65 stem/rest byte constants, 68–74 `LEAVES` | Each element is the exact literal, written as `<stem> + [Space] + <rest>`. The bytes were generated mechanically from the source strings, and each constant has a comment giving its ASCII text. |
| 63, 79 `read -r stem … <<< "$leaf"` | 80–94 `IsIfs`/`DropIfs`/`TakeField`/`ReadStem` | Default IFS is modelled: leading whitespace is skipped and the first field is taken. Deviation: at line 79, `cn` and `san` are not modelled because they only feed openssl arguments, never file names. |
| loop stems | 97–103 `StemsOf`, `Leaves()` | `Leaves()` is derived from `LEAVES` through `ReadStem`, not hard-coded. `LeavesValue` (264) proves that it equals the five stems. |
| 60 `renew=no` | 126 | |
| 61 `[ -f ca.crt ] \|\| renew=yes` | 127 | `[ -f ]` is modelled as membership in `fs.files`. |
| 62–68 the decision loop | 109–121 `RenewLoop` | Array order and the short-circuit `\|\|` are kept. `openssl x509 -checkend` is modelled as membership in `fs.expiring`, as the boundary approves. |
| 69 `[ "$renew" = yes ]`, 89 `fi` | 124–129 `Renew`, 158–162 `Writes` | The no-renew path writes nothing. |
| 70–73 mkdir/chmod/mktemp/trap | 150–156 (comment) | These write no file. |
| 75 `quiet` | 133–135 (comment), `WorkDir(OpensslErr)` entries | Each call writes `$work/openssl.err`. Deviation: an openssl failure (exit 1) is not modelled, because the approved boundary places it outside the properties. |
| 76–77 CA `openssl req` | 153 `[WorkDir(OpensslErr), WorkDir(CaKey), TlsDir(CaCrt)]` | `-keyout "$work/ca.key"` is a work-directory write and `-out "$TLS_DIR/ca.crt"` is a `TLS_DIR` write. |
| 78–86 leaf loop | 137–148 `LeafWrites` | Per stem, in order: `openssl.err`, `TLS_DIR/stem.key`, `work/stem.csr` (80–81); `work/stem.cnf` (82); `openssl.err`, `TLS_DIR/stem.crt` (83–84). The `$(openssl rand -hex 8)` serial and the chmod at line 85 write no file. |
| 87–88 chmod/echo | 155 (comment) | These write no file. |
| the files under `TLS_DIR` that get written | 164–169 `TlsNames`, `Written` | |

#### `fe-tls-entrypoint.settings`: T22 S4 `Iverson.Server/deploy/starrocks/fe-tls-entrypoint.sh` `the append-each-setting-once loop (17–24)` (bash)

| Source lines | Dafny lines | Notes / deviations |
|---|---|---|
| 12, 14 (`keystore=`, `password=`), context outside the member | 21–24 (`KEYSTORE`, `PASSWORD`) | Written as byte displays with an ASCII comment. Dafny cannot prove facts about the bytes of a string literal, so the constants could not be written as strings. |
| 17 `conf=…/fe.conf` | `Step`/`Loop` parameter `conf` (154, 164) | The file is modelled as its content, `seq<Byte>`. A missing or unreadable file (grep exit 2, which reads as "not found") and write failures under `set -e` are not modelled. |
| 18 the `for setting in "…" "…" "…"` list | 25–30 (literals), 141–145 `Settings()` (opaque) | The word list is kept as written: literal + `$keystore`/`$password`. `opaque` only controls proof visibility. `S0/S1/S2` (203–205) equal its elements; `SettingsAre` proves this. |
| 19 `key="${setting%% =*}"` | 87–93 `StripLongestSuffixSpEqStar`, used at 156 | Removes everything from the first `" ="` on (longest suffix matching the glob ` =*`). `SettingsFacts` proves it yields the three keys. |
| 20 `grep -Eq "^[[:space:]]*${key}[[:space:]]*=" "$conf"` | 63–73 `MatchAt`/`EreMatch`, 98–111 `ZapNuls`/`GrepLines`/`GrepEq`, used at 157 | Per-line ERE match with `[[:space:]]` = the 6 approved bytes, and the key matched literally (it holds no metacharacters). **Deviation from plain per-line splitting:** a file holding a NUL is binary to GNU grep, which then treats each NUL as a line terminator (grep.c `nul_zapper`). I confirmed this on GNU grep 3.12: the second run did not re-append. It does not affect the witness, which fails under either line model. |
| 21 `[ -s ] && [ -n "$(tail -c 1 …)" ] && echo >>` | 114–135 (`TestS`, `TailC1`, `DropNuls`, `StripTrailingNLs`, `CommandSubst`, `TestN`), 147–151 `Line21` | **Deviation beyond the approved note:** command substitution also drops NUL bytes. Bash 5.3.9 prints "ignored null byte in input" and does this. This is the behaviour that causes the failure. |
| 22 `echo "$setting" >> "$conf"` | 159 `c1 + setting + [NL]` | — |
| 23 `fi` (grep matched: no change) | 160 `else conf` | — |
| 18/24 loop iteration | 164–168 `Loop`, 170 `Append` | Branch order is kept. The loop body is the opaque `Step` (154–161). |
| Property vocabulary | 40 `Keys`, 44–60 `FirstNL`/`Lines` (opaque), 76–79 `HasSetting` | `Lines`: split at `\n`; a final `\n` ends the last line; a trailing unterminated run is its own line. |

#### `authentik-probes.helpers`: T25 S3 `Iverson.Server/deploy/scripts/authentik-probes.sh` `edge_client_from_xff, ip_is, is_loopback, is_console, setup_shows_no_prompt (60–92), the option parser (216–227)` (bash)

| Worktree file:line | Dafny lines | Notes / deviations |
|---|---|---|
| 42–43 `RELEASE=iverson`, `NS=iverson` | 34 `IversonDefault`, 199–202 `Options` | Faithful. |
| 60 `edge_client_from_xff() { # <xff> <hops>` | 126 `EdgeClient(header, hops: nat)` | Bash receives `$2` as a decimal string. The model takes its arithmetic value as a `nat` and converts it to intmax_t with `Wrap64` (line 133). |
| 61–62 `local IFS=, …; read -r -a entries <<< "$1"` | 128–129, using `ReadLine` (102–110), `ReadArrayComma` (113–119), `SplitAt` (72–81) | The here-string adds a newline and `read` takes bytes up to the first one. `IFS=,` has no whitespace, so spaces are kept. An empty line gives no fields, and a line ending in `,` drops one trailing empty field. I checked these against real bash: `"a,"`→[a], `","`→[""], `"a,,"`→[a,""], `" a , b "`→[" a "," b "]. NUL bytes can't occur in bash strings and are not modelled specially. |
| 63 `i=$(( ${#entries[@]} - $2 ))` | 130–133 | intmax_t wrapping is modelled as `Wrap64` (96–99) on `$2` and on the difference. **Deviation:** an array of 2^63 or more elements can't exist in bash, so for that case only the model uses the plain difference to keep the function total. I confirmed the wrap in real bash: hops 9223372036854775809 on `"a,b"` gives rc=1, matching the model. |
| 64 `[ n -gt 0 ] && [ "$i" -ge 0 ] \|\| return 1` | 134–135 | `return 1` maps to `None`. |
| 65 `printf '%s\n' "${entries[$i]// /}"` | 136–137, `StripSpaces` (57–63) | `Some(v)` is the printed text minus its trailing newline, which is what the caller's `$(...)` sees. An unset index (reachable only through a hops wrap) expands to `""`. |
| 76 `ip_is` | 140–144 | `[ a = b ]` is a 3-argument binary string comparison. |
| 78–83 `is_loopback` `case` | 146–157 (`GlobPrefixStar`, `IsLoopback`) | Glob `p*` is modelled as a byte prefix; glob `::1` as exact equality. **Library boundary:** glob matching is assumed byte-wise; locale and multibyte matching are not modelled. |
| 86 `is_console` | 159–163, `StripOneSlash` (66–70) | `CONSOLE_URL` (a global) becomes the parameter `consoleUrl`. `${x%/}` removes exactly one trailing `/`. |
| 90–92 `setup_shows_no_prompt` | 165–175 (`GlobStarWordStar`, `SetupShowsNoPrompt`) | `[[ $2 != *initial-setup* ]]` is modelled as substring absence (byte-wise glob, as above). |
| 216–218 sourcing guard (`return 0` when sourced) | 178 (comment only) | **Deviation:** not modelled. `Options` models the script when it is executed directly. |
| 220–227 `while`/`case`/`shift` parser | 179 `OptionsOutcome`, 181–196 `OptionsLoop`, 199–202 `Options` | Branch order is kept: `--release`, `--namespace`, `-h\|--help`, `*`. `usage` output is ignored. `exit N` maps to `Exit(N)`; falling out of the loop maps to `Parsed(release, ns)`. |

#### `admission-check.classify`: T25 S3 `Iverson.Server/deploy/scripts/admission-check.sh` `classify (52–60), the option parser (186–197)` (bash)

| Worktree file:line | Task/step | Dafny lines | Note |
|---|---|---|---|
| 38–39 `RELEASE=iverson`, `NS=iverson` | T25 S3 | 32–33 `DefaultRelease`, `DefaultNs` | Context only; these are the defaults the parser starts from. |
| 52 `classify() { # <exit code> <output> <policy name>` | T25 S3 | 47 `Classify(rc: int, out, policy): seq<Byte>` | `rc` is a Dafny `int`, not limited to bash's 0–255 range for `$?`. Nothing is added as a precondition. The result is what `$(classify …)` captures: the trailing newline from `echo` is stripped, so the result is `"admitted"`, not `"admitted\n"`. |
| 53 `if [ "$1" -eq 0 ]; then` | T25 S3 | 49 | The callers always pass `$?`, so the argument is an integer. A non-numeric argument (where `[` returns 2 and the elif runs) is not modelled. |
| 54 `echo admitted` | T25 S3 | 50 | `Admitted` (line 24) |
| 55 `elif printf '%s' "$2" \| command grep -qF -- "$3"; then` | T25 S3 | 51; axiom at 39–40 | The whole pipeline is the approved library-boundary call `GrepQF(out, policy)`. |
| 56 `echo refused` | T25 S3 | 52 | `RefusedWord` (line 25) |
| 57–58 `else echo error` | T25 S3 | 53–54 | `ErrorWord` (line 26) |
| 186–188 `if [[ "${BASH_SOURCE[0]}" != "$0" ]]; then return 0; fi` | T25 S3 | not modelled | Deviation: `Options` models the script run directly. When the script is sourced (as the tests do), the parser never runs. |
| 190 `while [ $# -gt 0 ]; do` | T25 S3 | 69–73 (`OptionsLoop`, the `\|args\| == 0` → `Parsed` case); 64–67 `Options` is the entry point with the defaults | The loop is turned into a recursive function, with `decreases \|args\|`. |
| 192 `--release) [ $# -ge 2 ] \|\| { usage; exit 2; }; RELEASE=$2; shift 2 ;;` | T25 S3 | 74–78 | `usage` writes to stderr only, so it is not modelled. |
| 193 `--namespace) … NS=$2; shift 2 ;;` | T25 S3 | 79–83 | Same as above. |
| 194 `-h\|--help) usage; exit 0 ;;` | T25 S3 | 84–85 | |
| 195 `*) usage; exit 2 ;;` | T25 S3 | 86–87 | |
| 196–197 `esac` / `done` | T25 S3 | 76, 81 (recursive calls) | |
| (vocabulary) | — | 10–33 | `Byte`, `OccursAt`/`Contains` (contiguous substring), `Newline` = `"\n"`, and the flag constants `--release`, `--namespace`, `-h`, `--help` as bytes. |
| (proof helper) | — | 94–103 `OptionsLoopExitCodes` | A manual induction showing every `Exit` code is 0 or 2; `P2_Options` calls it. |
| (frozen properties) | — | 110–114 `P1_Classify`, 117–123 `P2_Options` | Copied exactly as given. Each body opens on the line after its last clause. |

#### `mesh-check.mtls_seen`: T25 S3 `Iverson.Server/deploy/scripts/mesh-check.sh` `mtls_seen (70–76), the option parser (107–118)` (bash)

| Worktree lines | Dafny lines | Note |
|---|---|---|
| 36–37 (`RELEASE=iverson`, `NS=iverson`) | 54 (`DefaultName`), 119–122 (`Options`) | Faithful. |
| 70 (`mtls_seen` header) | 89 (`MtlsSeen(lines, src, dst)`) | **Deviation:** the metrics file `$1` is given as its list of lines. The case where grep cannot read the file is not modelled; the caller creates it at line 178. |
| 71 (`grep -E '^istio_tcp_connections_opened_total\{' "$1"`) | 91, plus `Opened` (67–70), `Filter` (73–77), `OpenedPrefix` (37) | **Deviation:** in ERE `\{` is a literal `{` and the pattern has no other metacharacters, so it is modelled as a literal line-prefix test. |
| 72 (`grep -F 'reporter="destination"'`) | 92, `ReporterDest` (38) | Modelled line by line as the library boundary says: the stage keeps lines that contain the string. |
| 73 (`grep -F "source_workload=\"$2\""`) | 93, `SourceLabel` (44), 41, 43 | **Deviation:** a newline in `$2` would make grep `-F` treat it as several patterns. The model uses plain `Contains`; P1's `requires` excludes newlines. |
| 74 (`grep -F "destination_workload=\"$3\""`) | 94, `DestLabel` (45), 42, 43 | Same note as line 73, for `$3`. |
| 75 (`grep -qF 'connection_security_policy="mutual_tls"'`) | 95, `Join` (80–83), `MutualTls` (39) | The approved `GrepQF` axiom is applied to the text of the pipe: each kept line followed by a newline. |
| 76 (`}`) | 96 | Faithful. |
| 107–109 (`BASH_SOURCE` guard: `return 0` when sourced) | not modelled; comment at 117–118 | **Deviation:** the guard does not depend on the arguments, and a script run as a command always reaches line 111. |
| 111 (`while [ $# -gt 0 ]`) | 108 | Recursion over `args`; an empty list gives `Parsed(release, ns)`. |
| 113 (`--release`, `[ $# -ge 2 ] \|\| { usage; exit 2; }`, `shift 2`) | 109–110 | `usage`'s stderr output is not modelled; `exit` becomes the `Exit` outcome. |
| 114 (`--namespace`) | 111–112 | Same as line 113. |
| 115 (`-h\|--help) usage; exit 0`) | 113 | Faithful. |
| 116 (`*) usage; exit 2`) | 114 | Faithful. |
| 117–118 (`esac` / `done`) | 104–115 | The loop is written as tail recursion; branch order is kept. |

#### `mesh-check.UNENROLLED_PY`: T25 S3 `Iverson.Server/deploy/scripts/mesh-check.sh` `UNENROLLED_PY (59–66)` (Python, inside bash)

| Worktree lines (task/step) | Dafny lines | Note |
|---|---|---|
| 59–60 `UNENROLLED_PY='`, `import json, sys` (T25 S3) | 10, 12, 19 (`Option`, `CodePoint`, `Pod`) | Per the approved library boundary, `json.load` gives the pod list. Each `Pod` holds `name = p["metadata"]["name"]`, `phase = p["status"].get("phase")` and `redirection = (p["metadata"].get("annotations") or {}).get("ambient.istio.io/redirection")`. The `or {}` and `.get` fallbacks show up as `None`. **Deviation:** a missing `items`, `status`, `metadata` or `name`, and JSON values that are not strings, raise in Python or print a repr. Both are outside P1's input shape, as the approved boundary says, so the model has no exception variant. |
| 61 `pods = [p for p in … if p["status"].get("phase") == "Running"]` (T25 S3) | 22 (`Running`), 44–54 (`Comprehension`), 67 | The comprehension is modelled as a left-to-right accumulator, the same as Python's iteration order. |
| 62 `print(len(pods))` (T25 S3) | 27–37 (`Digit`, `Decimal` = `str(n)` for n ≥ 0), 68 | Output is modelled as the sequence of printed lines, each without its trailing `\n`. |
| 63–65 `for p in pods: if … != "enabled": print(p["metadata"]["name"])` (T25 S3) | 24 (`Enabled`), 56–63 (`ForLoop`), 69 | Same branch order. `!=` against `Some(Enabled)` covers both an absent annotation and any other value. |
| 66 `'` | none | Closes the bash string. |
| none (spec helpers) | 76–86 `RunningPods`, `UnenrolledNames` | These are defined independently of the transliteration, as structural recursion on `pods[0]` and `pods[1..]`. |
| none (proof) | 92–116 `ComprehensionIsFilter`, `ForLoopIsFilter`; 123–132 `P1_RunningCountThenUnenrolled` | Proved helper lemmas: each accumulator loop equals `acc + filter(suffix)`. The P1 header is copied verbatim from the frozen text, and its body `{` opens on the line after its `ensures`. |

#### `kind-checks.alive-wait`: T27 S14 `$LIVE/kind-checks.sh` (scratch, under `$SCR/live`; plan lines 28563–28592) `alive() (its embedded Python) and the at-most-60 wait loop` (bash with embedded Python)

| kind-checks.sh lines (T27 S14) | Dafny lines | Note |
|---|---|---|
| 6–7 `alive()`, mysql pipe into `python3 -c` | 152–176 `AliveProgram`, `Alive`; input `out` | The `mysql --batch` output is an input: `out` is the lines Python's `sys.stdin` yields after decoding and universal-newline translation. If mysql fails (`2>/dev/null`), the input is empty. |
| 8 `rows = [l.rstrip("\n").split("\t") for l in sys.stdin if l.strip()]` | 129–134 `RowsOf`; 74–77 `Fields`; 55–59 `RStripNL`; 62–71 `Split`; 41–52 `IsPySpace`/`IsBlank` | `strip()` is modelled on the full `_PyUnicode_IsWhitespace` set. `split("\t")` keeps empty fields. `rstrip("\n")` removes every trailing `\n`. |
| 9 `i = rows[0].index("Alive") if rows else -1` | 154–155 (`iR`); 92–97 `ListIndex`; 80–89 `FirstIndex` | A missing `Alive` gives the `ValueError` outcome, so nothing is printed (157–158, 173–174). |
| 10 `",".join(r[i] for r in rows[1:]) if i >= 0 else "none"`; `print` | 160–166; 137–149 `GenIndex`; 100–105 `PyIndex` (negative indices included); 108–114 `JoinComma` | The generator's `IndexError` propagates through `join`, so nothing is printed. `Alive` returns the printed text without `print`'s trailing `\n`, since `$(…)` strips it. **Deviation:** `$(…)` also strips any further trailing newlines. That cannot matter here, because a field never ends in `\n` for lines read from stdin. |
| 15 `if kubectl … get pod iverson-starrocks-fe-0` | 241–244 `WaitLoop` | The command's success is the input `fePodExists`. |
| 16 `for _ in $(seq 1 60)` | 223–238 `ForLoop` (`k` = iterations completed; `k == 60` means `seq` is exhausted) | |
| 17 `case ",$(alive FE),$(alive BE)," in` | 216–219 `Subject`; 226–227 | The two `alive` strings for iteration `k` are inputs, `views[k].0/.1`. **Deviation:** the strings are code points, not bash bytes. The patterns are ASCII, so substring containment is the same under UTF-8. |
| 18 `*,false,*\|*,none,*\|*,,*) ;;` | 228–230; 117–121 `Contains` | The `*P*` glob, with no metacharacters in P, is modelled as substring containment. |
| 19–20 `kubectl get --raw …/health \| grep -qF '"starrocks":true' && break` | 231–232 (returns `k + 1`) | The grep result is the input `views[k].2`. |
| 22 `sleep 5` | 230, 234 (moves to `ForLoop(views, k + 1)`) | Time is modelled only through the per-iteration views. |
| (spec) | 183–201 `AliveIndex`, `WellFormedBatch`, `Column`; 247–252 `Done` | These follow the approved model notes. |
| (environment) | 210–213 `ViewAt` | **Deviation:** to avoid adding a precondition, a view past the end of `views` reads as `([], [], false)`, which counts as not done. P2's `\|views\| >= 60` makes this unreachable. |
| helpers | 258–306 `RowsOfNoBlank`, `GenIndexColumn`, `ForLoopFacts` | Proved by explicit induction. |

### Obligations

| Candidate | Obligation | Citation | Status |
|---|---|---|---|
| `TenantAdminGrpcService.RequireUserInTenantAsync` | P1_NonCanonicalRefusedWithoutCall | design TenantUserGuard.P1_NonCanonicalRefusedWithoutCall: "`RequireUserInTenantAsync` first requires `user_id` to be a canonical positive decimal integer; any other value is "not in this tenant" (`PermissionDenied`) with no IdP call." (§2.1 line 42) | proved |
|  | P2_MismatchRefused | design TenantUserGuard.P2_MismatchRefused: "`GetUserAsync` also requires the returned `pk` to equal the requested pk." (line 42); "uses `GetUserAsync` and compares `tenant_id`" (line 41); "404 maps to "not in this tenant"" (line 40) | proved |
|  | P3_MutationsUseCheckedPk | design TenantUserGuard.P3_MutationsUseCheckedPk: "RemoveUser and SetTenantAdmin pass the checked record's pk, not the raw request string, to `DeactivateUserAsync`, `AddGroupAsync` and `RemoveGroupAsync`." (line 42) | proved |
|  | P4_CanonicalPkReachesTheIdp | "`RequireUserInTenantAsync` (RemoveUser, SetTenantAdmin) uses `GetUserAsync` and compares `tenant_id`" (§2.1 line 41); test `RemoveUser_ReadsTheTargetByPk_AndNeverListsTheDirectory` (T1 S1 1f), generalised | proved |
| `IdpAdminClient.TenantReads` | P1_EveryPageCarriesTheFilter | "`ListUsersByTenantAsync` requests `GET /api/v3/core/users/?attributes=<JSON {"tenant_id": t}>` on **every** page." (§2.1 line 38); test `ListUsersByTenantAsync_AsksAuthentikForTheTenantsUsersOnly` (T1 S1 1b), generalised | proved |
|  | P2_OnlyTheTenantsUsersReturned | "The client keeps its own check that each returned user's `attributes.tenant_id` equals the tenant, because Authentik returns the unfiltered list when filtering raises a `ValueError`" (§2.1 line 39); test `ListUsersByTenantAsync_DropsAUserOfAnotherTenant_EvenWhenAuthentikReturnsIt` | proved |
|  | P3_NotFoundIsNull | "404 maps to "not in this tenant"." (§2.1 line 40); test `GetUserAsync_NotFound_ReturnsNull` | proved |
|  | P4_ReturnedRecordHasTheRequestedPk | "`GetUserAsync` also requires the returned `pk` to equal the requested pk." (§2.1 line 42); test `GetUserAsync_ReturnedPkDiffersFromTheRequestedOne_ReturnsNull` | proved |
|  | P5_OtherFailureThrows | test `GetUserAsync_OtherFailure_ThrowsHttpRequestException` (T1 S1 1e), generalised | proved |
| `TenantAdminGrpcService.InviteUser` | P1_LimitReachedRefusesWithoutCallingTheIdp | "No row returned means over quota: `ResourceExhausted` ("the tenant's daily invitation limit is reached")." (§2.1 line 51); test `InviteUser_DailyLimitReached_ThrowsResourceExhaustedAndDoesNotCreateUser` | proved |
|  | P2_ReservesTheCallersTenantTodayFirst | "The api reserves a slot before calling Authentik" (§2.1 line 51); test `InviteUser_ReservesTodaysSlotForTheCallersTenant_BeforeCallingTheIdp` | proved |
|  | P3_InactiveTenantReservesAndCreatesNothing | tests `InviteUser_SuspendedTenant_ThrowsPermissionDeniedAndDoesNotCreateUser` and `InviteUser_NoTenantIdClaim_ThrowsPermissionDeniedAndDoesNotCreateUser`, generalised | proved |
| `InviteQuotaRepository.TryReserveAsync` | P1_AtMostHundredReserved | design InviteQuota.P1_AtMostHundredReserved: "`InviteUser` is limited to **100 per tenant per UTC day**." (§2.1 line 50) | proved |
|  | P2_HundredFirstRefused | design InviteQuota.P2_HundredFirstRefused: "the 101st invite of a tenant's day is refused across two api instances sharing Postgres" (§6 line 490) | proved |
|  | P3_CountMonotone | design InviteQuota.P3_CountMonotone: "A reserved slot is not returned if the IdP call then fails." (§2.1 line 51) | proved |
| `RateLimitInterceptor.Enforce` | P1_ThirtyFirstRefusedOthersPass | design RateLimitPartition.P1_ThirtyFirstRefusedOthersPass: "the 31st TenantAdmin call in a minute is refused while other RPCs pass" (§6 line 490) | proved |
|  | P2_SeparateBuckets | design RateLimitPartition.P2_SeparateBuckets: "partitioned by `(service, sub)`, so each of the two services has its own 30/min bucket per caller" (§2.1 line 45) | proved |
|  | P3_AnotherCallersBucketUntouched | test `One_callers_spent_TenantAdmin_budget_leaves_another_callers_untouched` (T2 S1 1a), generalised | proved |
|  | P4_OtherRpcsNeverSpendTheAdminBucket | "`RateLimitInterceptor` keeps its 50,000/min per-`sub` limiter for every RPC except the `TenantAdminGrpcService` and `TenantLifecycleGrpcService` methods. Those go to a second limiter" (§2.1 line 45) | proved |
| `OperatorAuthorizationPolicy.IsSatisfiedBy` | P1_OperatorImpliesSchemaAdmin | design RoleFunctions.P1_OperatorImpliesSchemaAdmin: "`SchemaAdminAuthorizationPolicy`: `OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` (the list, the `admin` scope, or the compose fallback), or scope `schema_admin`, which keeps "Operator implies SchemaAdmin"" (§2.4 line 111) | proved |
|  | P2_GroupsIrrelevantWithoutFallback | design RoleFunctions.P2_GroupsIrrelevantWithoutFallback: "The `groups` claim no longer yields Operator anywhere on Helm." (§2.4 line 114) with "API setting `Authorization:OperatorGroupFallback` (unset by default)" (line 119) | proved |
|  | P3_AdminScopeIsOperator | design RoleFunctions.P3_AdminScopeIsOperator: "Admin-automation keeps Operator through its `admin` scope." (§2.4 line 106) | proved |
|  | P4_OperatorImpliesConsoleAdmin | design RoleFunctions.P4_OperatorImpliesConsoleAdmin: "A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin." (§2.2 line 69) | proved |
|  | P5_FallbackGroupIsSchemaAdmin | design RoleFunctions.P5_FallbackGroupIsSchemaAdmin: "(the list, the `admin` scope, or the compose fallback)" (§2.4 line 111) | proved |
|  | P6_ListedSubjectIsOperator | "`OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` is true when the principal's `sub` is on the list or its `scope` contains `admin`." (§2.4 line 108); test `IsSatisfiedBy_SubjectOnTheList_ReturnsTrue` | proved |
|  | P7_ConsoleAdminIsOperatorOrTenantAdmin | "A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin." (§2.2 line 69); test `IsSatisfiedBy_SignedInUserWithNeitherRole_ReturnsFalse` | proved |
|  | P8_EmptyFallbackIsUnset | "API setting `Authorization:OperatorGroupFallback` (unset by default)" (§2.4 line 119); test `FromConfiguration_NothingSet_IsAnEmptyListAndNoFallback` (`null`, `""`) | proved |
| `TokenRevocationCache.IsRevokedAsync` | P1_WithinFiveSecondsRevoked | design StreamRecheck.P1_WithinFiveSecondsRevoked: "A token is revoked when `iat <= revoked_at + 5 s`. … A fresh login within 5 s of a revocation is therefore refused" (§2.3 line 90) | proved |
|  | P2_Boundary | design StreamRecheck.P2_Boundary: "`TokenRevocationCacheTests.cs:50-52` moves from +1 s to +6 s, and a +5 s boundary test is added" (§2.3 line 90) | proved |
|  | P3_NoRowNotRevoked | "A token is revoked when `iat <= revoked_at + 5 s`." (§2.3 line 90) | proved |
| `StreamRevalidationInterceptor.RecheckAsync` | P1_NoWriteAfterRevocationOrSuspension | design StreamRecheck.P3_NoWriteAfterRevocationOrSuspension: "Before each message it re-checks … the revocation of the primary principal's `sub` and of the acting user's `sub`; the status of the effective tenant. A revoked principal ends the stream with `Unauthenticated`. A suspended or deleted tenant ends it with `PermissionDenied`" (§2.3 lines 93–96) | proved |
|  | P2_RevokedEndsUnauthenticated | "A revoked principal ends the stream with `Unauthenticated`." (§2.3 line 96); test `ARevokedPrincipal_EndsTheStreamUnauthenticated_AtItsNextMessage` | proved |
|  | P3_InactiveTenantEndsPermissionDenied | "A suspended or deleted tenant ends it with `PermissionDenied` "Tenant '…' is not active.", as the unary paths do." (§2.3 line 96); test `ATenantNoLongerActive_EndsTheStreamPermissionDenied_AtItsNextMessage` | proved |
|  | P4_BothWriteFormsRecheckFirst | "The wrapper implements both `WriteAsync(T)` and `WriteAsync(T, CancellationToken)` and passes `WriteOptions` through" (§2.3 line 97); tests `ARevokedPrincipal_EndsTheStreamUnauthenticated_AtItsNextMessage` (both forms) and `NothingChanged_BothWriteFormsWriteThrough_PassingTheTokenAndTheWriteOptions` | proved |
| `TenantStatusCache.InvalidateAll` | P1_ReadBeforeInvalidateAllNeverServedAfter | test `InvalidateAll_DuringARepositoryRead_TheReadsResultIsNotServedAfterwards` (T5 S5), generalised; "On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`." (§2.3 line 85) | proved |
|  | P2_FirstReadAfterInvalidateAllGoesToTheRepository | test `InvalidateAll_EveryTenantReadsTheRepositoryAgain` (T5 S5), generalised | proved |
| `AuthInvalidationListener.ExecuteAsync` | P1_ListeningWriteInvalidates | design CacheInvalidation.P1_ListeningWriteInvalidates: "Every revocation write and every tenant-status write issues `pg_notify` in the same transaction as the write … On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`." (§2.3 lines 84–85) | proved |
|  | P2_NoWriteMissedAcrossReconnect | design CacheInvalidation.P2_NoWriteMissedAcrossReconnect: "On every connect and reconnect it issues `LISTEN` first, then invalidates both caches. A notification sent while it was disconnected is not replayed." (§2.3 line 86) | proved |
|  | P3_BackoffFromOneToThirtySeconds | "Reconnects back off from 1 s to 30 s." (§2.3 line 86) | proved |
| `EngagementHealthChecker.CheckHealthAsync` | P1_AuthPendingOnlyFromRefusedLogin | design ReadinessMapping.P1_AuthPendingOnlyFromRefusedLogin: "With no grant needed, `AuthPending` now arises only from a refused login." (§3.5 line 317) (the precondition is VA45's fact that the probe needs no grant, so after a login no statement fails with the login error) | proved |
|  | P2_RowsMeanHealthy | design ReadinessMapping.P2_RowsMeanHealthy: "Two rows mean Healthy. The query needs no grant, returns rows with a backend alive, and fails in about 0.4 s ("Backend node not found") with none (Verified assumption 45)." (§3.5 line 316) | proved |
|  | P3_HealthyIsExactlyTwoRows | "Two rows mean Healthy." (§3.5 line 316) | unproved |
| `HarnessChannel.Create` | P1_TheSchemePicksTheTransport | "The harness (`ClientConformance/Program.cs:111-114`) and the five drivers (…) hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446); test `Create_BuildsAChannelWhoseTransportMatchesTheScheme` (`https://`, `HTTPS://`, `http://`) | proved |
|  | P2_CredentialsGoOnlyToHttpsOrLoopback | test `CheckEndpoints_RefusesAPlaintextNetworkGrpcUrlOrTokenEndpoint` ("IVERSON_GRPC_URL must be https:// or a loopback address"), generalised | proved |
| `Auth.BuildInvoker` | P1_TheSchemePicksTheTransport | "… the five drivers (`Auth.cs:52, 65`; …) hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved |
| `conformance.grpcUsesTLS` | P1_TlsExactlyForAnHttpsScheme | test `TestGrpcUsesTLSFollowsTheScheme` (T23 S1 1d: `https://`, `HTTPS://` true; `http://`, bare `host:port` false), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved |
|  | P2_DialOptionFollowsTheScheme | as P1 | proved |
| `driver.parse_grpc_address` | P1_BareHostPortIsPlaintext | test `test_parse_grpc_address_takes_tls_from_the_scheme` (`localhost:8080` → `False`), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved |
|  | P2_HttpsSchemeIsTls | test `test_parse_grpc_address_takes_tls_from_the_scheme` (`https://…` → `True`, `http://…` → `False`), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved |
|  | P3_ChannelTransportFollowsUseTls | test `test_build_driver_channel_takes_its_transport_from_use_tls` (`(True, acting) → ssl`, `(False, acting) → local`, `(True, None) → ssl`, `(False, None) → []`), generalised | proved |
| `driver.parseGrpcAddress` | P1_TlsExactlyForAnHttpsUrl | "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved |
| `Driver.channelTransport` | P1_TlsExactlyForAnHttpsScheme | "… `Driver.java:177` … hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446) | proved |
| `roles.RequireRole` | P1_NoRoleBeforeTheAnswer | tests "shows progress, not the page and not a refusal, while the roles are being read" and "says the roles could not be read when the request failed, rather than refusing" (T6 S2 2a), generalised; "replaced by a role guard over that response" (§2.2 line 78) | proved |
|  | P2_RolesAreTheServersAnswer | tests "refuses an operator a tenant-admin page: the two roles answer different questions" and "admits an operator and a tenant admin alike to a page for either role"; "The landing page requires either role" (§2.2 line 79) | proved |
|  | P3_GuardRendersByState | the `RequireRole` tests (T6 S2 2a), generalised | proved |
|  | P4_ProviderState | tests "reads the roles once with the session's token and publishes them", "publishes 'checking' and reads nothing before the session has a token", "publishes 'unavailable' when the roles request fails"; "the last good answer is kept while a retry runs" is the code's own comment and not cited | proved |
| `blueprints-configmap.orchestrator-tokenreview` | P1_SuccessAdmitted | design TokenReviewPin.P1_SuccessAdmitted: "It refuses (`invalid_grant`) on any exception, on any response status outside 2xx (a successful review answers 201)" (§2.6 line 175) | proved |
|  | P2_ErrorsRefused | design TokenReviewPin.P2_ErrorsRefused: same sentence (§2.6 line 175) | proved |
|  | P3_WrongIdentityRefused | design TokenReviewPin.P3_WrongIdentityRefused: "It requires `status.authenticated` true and `status.user.username` = `system:serviceaccount:<ns>:<rel>-api`." (§2.6 line 175) | proved |
|  | P4_NoFederatedJwtRefused | "It keys on `context["oauth_jwt"]` and the request, not `request.user`, which is anonymous on a first exchange." (§2.6 line 176); test `TokenReviewExpression_ReviewsTheRawAssertion_AndRefusesEveryFailure` (`if not request.context.get("oauth_jwt"):`) | proved |
|  | P5_MissingAssertionRefused | "The raw assertion is read from the token request's form body." (§2.6 line 177) with "It refuses (`invalid_grant`) on any exception" (line 175); test `TokenReviewExpression_…` ("every step, the form read included, is inside the try") | proved |
| `blueprints-configmap.orchestrator-workload` | P1_AnotherIssuerRefused | "Both pins (orchestrator and admin-automation) compare `iss` exactly with a new required value `authentik.serviceAccountIssuer`." (§2.6 line 181) | proved |
| `secret-service-clients.admin-automation-workload` | P1_AnotherIssuerRefused | "Both pins (orchestrator and admin-automation) compare `iss` exactly with a new required value `authentik.serviceAccountIssuer`." (§2.6 line 181); test `AdminAutomationPin_ComparesTheIssuer` (T8 S1 1a) | proved |
| `secret-service-clients.console-admins` | P1_TenantAdminAdmitted | "It admits members of `tenant-admins` and subjects on the operator list (§2.4); everyone else gets Authentik's "Permission denied" at authorize." (§2.2 line 66) | proved |
|  | P2_ListedOperatorAdmitted | "The expression is `ak_is_group_member(request.user, name="tenant-admins") or request.user.uid in [<list>]`." (§2.4 line 116) | proved |
|  | P3_EveryoneElseRefused | "everyone else gets Authentik's "Permission denied" at authorize" (§2.2 line 66) | proved |
| `compose-service-clients.console-admins` | P1_ComposeAdmitsTheTwoGroups | "The `operators` group and the bypass user's membership stay in `compose-only/service-clients.yaml`, together with a compose copy of the console sign-in policy that reads that group." (§2.4 line 120) | proved |
| `recovery-flow.login-throttle` | P1_KnownWrongPasswordsFromZero | design LoginThrottle.P1_KnownWrongPasswordsFromZero: "after 5 failed attempts for a known username whose pair starts at 0 (wrong passwords;" (§2.5 line 158) | proved |
|  | P2_ThreeWrongCodesReachMinusFive | design LoginThrottle.P2_ThreeWrongCodesReachMinusFive: "a wrong MFA code after a correct password lowers the row by 2, since the validation stage and its login-failure signal each score −1, so three wrong codes reach −5" (§2.5 line 158) | proved |
|  | P3_SuccessesFirstOffset | design LoginThrottle.P3_SuccessesFirstOffset: "after 5 wrong passwords plus one more for each successful login from that address in the row's hour that found the row below +5: up to 10 when those logins come before the failures" (§2.5 line 158) | proved |
|  | P4_InterleavedNoFixedBound | design LoginThrottle.P4_InterleavedNoFixedBound: "and more when the owner signs in between them" (§2.5 line 158); "and with no fixed bound when the owner keeps signing in between them" (§7 line 538) | proved |
|  | P5_UnknownThirdPlanRefused | design LoginThrottle.P5_UnknownThirdPlanRefused: "An unknown one scores −2 per attempt (identification and login both fail), and its identification's −1 is written before order 12 runs, so its 3rd plan is refused after 2 failed attempts" (§2.5 line 158) | proved |
|  | P6_AddressCapNeedsTenIdentifiers | design LoginThrottle.P9_AddressCapNeedsTenIdentifiers: "The address cap is a sum over rows, each floored at −5, so it is reached only once failures span at least 10 identifiers." (§2.5 line 159) | proved |
|  | P7_EmailRowsCount | "**pair:** the rows for the client address whose identifier is the pending user's username or email have a summed score ≤ −5" (§2.5 line 148) | proved |
|  | P8_OtherAddressesNeverCount | "the rows for the client address …" (§2.5 line 148); "the sum of all negative row scores for the client address" (line 149) | proved |
| `deployment-server.wait-for-migration` | P1_MigratedExactly | plan check `t7_wait_probe.sh` (T7 S1): "It must keep waiting with no version table, with only an older version's row, and with this image's row while lock 1000 is held, and finish once the lock is released", generalised | proved |
|  | P2_WaitEndsAtTheFirstMigratedAnswer | as P1; "Servers wait for the migration and never migrate." (§2.1 line 59) | proved |
|  | P3_WaitObservesTheEndOfThePass | "Neither the migrator's advisory lock nor the `authentik_version_history` marker shows completion on its own … The plan picks a wait that observes the end of the pass." (§2.1 line 59) | unconfirmed |
| `admission-policy.validations` | P1_OperatorsAdmitted | design AdmissionPolicy.P1_OperatorsAdmitted: "Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else; rule (c)'s name clause binds the operators group too." (§3.1 line 233) | proved |
|  | P2_ClusterAdminDenied | design AdmissionPolicy.P2_ClusterAdminDenied: "Cluster-admin is denied too, by design (Verified assumption 37)" (§3.1 line 233) | proved |
|  | P3_MetadataOnlyPodUpdateAdmitted | design AdmissionPolicy.P3_MetadataOnlyPodUpdateAdmitted: "Metadata-only pod updates are not matched (Verified assumption 77)." (§3.1 line 239) | proved |
|  | P4_KubeletMintsTokens | design AdmissionPolicy.P4_KubeletMintsTokens: "The kubelet (`system:nodes`) is also admitted, so projected tokens keep mounting." (§3.1 line 234) | proved |
|  | P5_ScaleAdmitted | design AdmissionPolicy.P5_ScaleAdmitted: "creates or updates (other than through the `scale` subresource) a workload whose pod template uses either account" (§3.1 line 243) | proved |
|  | P6_NoProtectedPodOrTemplateForNonOperators | design AdmissionPolicy.P6_NoProtectedPodOrTemplateForNonOperators: "Without (d), anyone who can create a Job could run a pod as `<rel>-admin-automation` through the job controller, which (c) admits." (§3.1 line 237) | proved |
|  | P7_MisnamedProtectedPodRefusedForAll | design AdmissionPolicy.P7_MisnamedProtectedPodRefusedForAll: "Whoever creates it, the operators group and those controllers included, a pod with either account is admitted only when its name starts with `<rel>-api-`" (§3.1 line 236) | proved |
|  | P8_AdmittedProtectedPodIsGated | design AdmissionPolicy.P8_AdmittedProtectedPodIsGated: "so (b), (e) and (f) match every pod that holds a protected token" (§3.1 line 236) | proved |
|  | P9_ApiReplicaSetPodsAdmitted | design AdmissionPolicy.P9_ApiReplicaSetPodsAdmitted: "the api's ReplicaSet pods (`<rel>-api-<hash>-<suffix>`) already do (Verified assumption 96)" (§3.1 line 236) | proved |
| `configmap-tls-proxy.public-locations` | P1_RootRedirects | design AllowList9080.P1_RootRedirects: "**`= /` redirects (302) to the console URL** instead of forwarding to Authentik's root." (§2.7 line 216) | proved |
|  | P2_SetupNotForwarded | design AllowList9080.P2_SetupNotForwarded: "`:9080` forwards neither `/setup` nor the flow's slug" (§2.6 line 192); "**Refused:** `initial-setup`" (§2.7 line 217) | proved |
|  | P3_InterfacesRefused | design AllowList9080.P3_InterfacesRefused: "**Refused:** … `/if/user/`, `/if/admin/` and the rest of `/api/`." (§2.7 line 217) | proved |
|  | P4_AllowedFlowsForwarded | design AllowList9080.P4_AllowedFlowsForwarded: "`/if/flow/<slug>/` and `/api/v3/flows/executor/<slug>/` for exactly: `default-authentication-flow`, `iverson-recovery`; `default-provider-authorization-implicit-consent` …; `default-invalidation-flow` …; `default-provider-invalidation-flow`" (§2.7 lines 204–208) | proved |
|  | P5_OtherApiRefused | design AllowList9080.P5_OtherApiRefused: "the rest of `/api/`" refused (§2.7 line 217) | unconfirmed |
|  | P6_WebSocketAndLiveForwarded | "`/ws/client/`, with WebSocket upgrade headers." (§2.7 line 211); "`= /-/health/live/`, for the GCP health check." (line 213); plan check `check_t8.py` (T8 S1: `/ws/client/` and `/-/health/live/` in `allowed`, `/-/health/ready/` in `refused`) | proved |
| `job-create-user.create-user` | P1_JobSetsPasswordOnFe0 | design StarRocksBootstrap.P2_JobSetsPasswordOnFe0: "the create-user Job sets root's password as soon as FE-0 answers, no longer waiting for the BE count first" (§4.3 line 375) | proved |
|  | P2_BatchOnlyAfterEveryNodeListed | "It then waits for `BE_REPLICAS` backends and every FE before the rest of the batch." (§4.3 line 375); plan check `t19_job.sh` (T19 S1: `show-backends` and `show-frontends` before `create-user`), generalised | proved |
|  | P3_RevokeOperateAfterCreateUser | "`GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%'` is dropped … the job and compose's `starrocks-init` also run `REVOKE OPERATE ON SYSTEM FROM USER 'iverson_app'@'%'` beside the existing role revoke, after `CREATE USER IF NOT EXISTS`" (§3.5 line 319); plan check (T15 S5: `"GRANT OPERATE" not in text`) | proved |
|  | P4_PasswordAfterSetPassword | "Because the FE and BE then log in with root's password from their first start, the create-user Job sets root's password as soon as FE-0 answers" (§4.3 line 375); plan check `t19_job.sh` (`[ "$(command grep -c "^pw" $D/log)" -ge 3 ]`, state `r` after both runs) | proved |
| `setup.sh.guards` | P1_KindGate | "`setup.sh`/`setup.ps1` refuse a kind older than v0.33.0" (§3.6 line 330); plan check `t16_kind.sh` (T16 S1: "kind v0.24.0 must be refused before any helm or kubectl call; kind v0.33.0 must get past the check"), generalised | proved |
|  | P2_OnlyRunsOnePhase | **Expected:** (T14 S4) "`usage: Iverson.Server/deploy/kind/setup.sh [--only <phase>]   (phases: …)` then `exit=2`" for `--only no-such-phase`, generalised; Task 27 Interfaces "runs a single phase with `--only <phase>`" | unconfirmed |
|  | P3_EnrolOnlyWith15008 | "On kind they are labelled only after the chart's 15008 rules exist (§5)" (§4.1 line 344); plan check `t17_enroll.sh` (T17 S1: fresh → label; `8080 15008 8081 15008` → label; `8080 8081 5432` → none; `150080 8081` → none), generalised | proved |
| `setup.ps1.guards` | P1_KindGate | "`setup.sh`/`setup.ps1` refuse a kind older than v0.33.0" (§3.6 line 330) | proved |
|  | P2_OnlyRunsOnePhase | **Expected:** (T14 S4) "… then `exit=2` … `setup.ps1` … mirrors the bash block line for line." | unconfirmed |
|  | P3_EnrolOnlyWith15008 | "On kind they are labelled only after the chart's 15008 rules exist (§5)" (§4.1 line 344); **Expected:** (T14 S4) "mirrors the bash block line for line" | proved |
| `generate-compose-secrets.renew` | P1_RenewWhenAnyMissingOrExpiring | "All are rewritten together whenever any is missing or near expiry" (§4.6 line 438); plan check `t22_gen.sh` (T22 S1: "a re-run keeps the files", "a missing leaf key rewrites every file", "a leaf near expiry rewrites every file"), generalised | proved |
|  | P2_RenewRewritesEverythingButTheCaKey | "All are rewritten together …, and the CA key is still deleted afterwards" (§4.6 line 438); plan check `t22_gen.sh` ("still no CA key") | proved |
| `fe-tls-entrypoint.settings` | P1_EachSettingOnce | plan check `t22_fe.sh` (T22 S1: two runs, then "fe.conf has '<line>' once" for each setting and "fe.conf has four lines"), generalised | unconfirmed |
|  | P2_ExistingLinesKept | plan check `t22_fe.sh` ("the existing line is kept whole", from a file with no trailing newline "as an edited file may have") | unconfirmed |
| `authentik-probes.helpers` | P1_EdgeClient | tests (T25 S1 `test_probes.sh`): "edge client, hops 1, is the right-most entry", "hops 2, is the second from the right", "of an empty header is none", "beyond the header is none", generalised | proved |
|  | P2_IpIs | tests "a plain address matches", "an IPv4-mapped address matches", "another address does not match" | proved |
|  | P3_IsLoopback | tests "the tls-proxy bind address is loopback", "the port-forward address is loopback", "a pod address is not loopback" | proved |
|  | P4_IsConsole | tests "the console URL without its slash is the console", "the login page is not the console", "an empty Location is not the console"; "`= /` redirects (302) to the console URL" (§2.7 line 216) | proved |
|  | P5_SetupShowsNoPrompt | tests "/setup failing shows no prompt", "redirecting to / shows no prompt", "redirecting to the setup flow shows a prompt", "answering 200 shows a prompt"; "on the in-cluster route, `/setup` and the `initial-setup` executor show no prompt" (§6 line 517) | proved |
|  | P6_Options | tests "<script>: --help exits 0", "<script>: an unknown option exits 2" (T25 S1) | proved |
| `admission-check.classify` | P1_Classify | tests (T25 S1): "success is admitted", "the policy named in the error is refused", "an RBAC refusal is an error", "another policy is an error", generalised; "a non-operator refused … the operators group admitted" (§6 line 519) | proved |
|  | P2_Options | tests "admission-check: --help exits 0", "admission-check: an unknown option exits 2" | proved |
| `mesh-check.mtls_seen` | P1_MtlsSeen | tests "FE -> BE mutual TLS reported by the destination", "a source-side report does not count", "a plaintext connection does not count" (T25 S1), generalised; "ztunnel reporting mTLS for FE ↔ BE" (§6 line 518) | proved |
|  | P2_Options | tests "mesh-check: --help exits 0", "mesh-check: an unknown option exits 2" | proved |
| `mesh-check.UNENROLLED_PY` | P1_RunningCountThenUnenrolled | test "running pods counted and the unenrolled one named" (T25 S1), generalised; "every release pod enrolled" (§6 line 518) | proved |
| `kind-checks.alive-wait` | P1_AliveColumn | **Expected:** (T27 S14) "`local K2 frontends alive: true backends alive: true` (one FE and one BE on the local profile; every row `true`)" | proved |
|  | P2_WaitBounded | **Expected:** (T27 S14) "printed after the script's wait of at most 5 minutes (the hook releases Helm once the BE is listed, which is before its process starts); a `false` after the wait is a failure" | proved |

Totals: 113 proved, 6 unconfirmed, 1 unproved.

### Gates

- G0, G1/G3 and G2 pass for every candidate (run as the skill's Step 9 blocks; `admission-policy.validations` failed G0 until the gate re-froze P3, then passed). G4: every `requires` outside a lemma is signature-forced (`FromUnixTimeSeconds`' `long` range) or an internal bound on a recursion or library-model helper (index and loop counters, `DigitChar`, `AddTimeSpan`'s non-negative span, the `kind version` line model, the `Finish`-follows-a-miss trace model); no transliterated entry point gains a precondition, and each correspondence-table deviation was checked against the worktree file.
- G5: every proved obligation is falsifiable. Each was killed by a single-line mutation of the transliterated code (never a lemma or an axiom), each kept as its own file `gates/g5/<L>.<k>.dfy` with its csv; the table names the mutation and the failed row.

| Candidate | Obligation | Mutation (line: old → new) | Failed row |
|---|---|---|---|
| `TenantAdminGrpcService.RequireUserInTenantAsync` | P1_NonCanonicalRefusedWithoutCall | 39: `\|s\| > 0 && AllDigits(s) && s[0] != '0'` → `\|s\| > 0 && AllDigits(s) && s[0] == '0'` | NatToStringCanonical (a helper P1_NonCanonicalRefusedWithoutCall's proof calls) |
|  | P2_MismatchRefused | 130: `else if user.Null? \|\| user.record.TenantId != Some(tenantId) then` → `else if user.Null? \|\| user.record.TenantId == Some(tenantId) then` | P2_MismatchRefused |
|  | P3_MutationsUseCheckedPk | 130: `else if user.Null? \|\| user.record.TenantId != Some(tenantId) then` → `else if user.Null? \|\| user.record.TenantId == Some(tenantId) then` | P3_MutationsUseCheckedPk |
|  | P4_CanonicalPkReachesTheIdp | 39: `\|s\| > 0 && AllDigits(s) && s[0] != '0'` → `\|s\| > 0 && AllDigits(s) && s[0] == '0'` | CanonicalValuePositive (a helper P4_CanonicalPkReachesTheIdp's proof calls) |
| `IdpAdminClient.TenantReads` | P1_EveryPageCarriesTheFilter | 212: `else if TenantIdOf(user) != Some(tenantId) then                     // 168 (null != tenantId)` → `else if TenantIdOf(user) == Some(tenantId) then                     // 168 (null != tenantId)` | CollectKeepsTenant (a helper P1_EveryPageCarriesTheFilter's proof calls) |
|  | P2_OnlyTheTenantsUsersReturned | 212: `else if TenantIdOf(user) != Some(tenantId) then                     // 168 (null != tenantId)` → `else if TenantIdOf(user) == Some(tenantId) then                     // 168 (null != tenantId)` | CollectKeepsTenant (a helper P2_OnlyTheTenantsUsersReturned's proof calls) |
|  | P3_NotFoundIsNull | 288: `else if ans.status == 404 then Null                                   // 218-219` → `else if ans.status != 404 then Null                                   // 218-219` | P3_NotFoundIsNull |
|  | P4_ReturnedRecordHasTheRequestedPk | 296: `if pk != Some(userId) then Null                                   // 227-228` → `if pk == Some(userId) then Null                                   // 227-228` | P4_ReturnedRecordHasTheRequestedPk |
|  | P5_OtherFailureThrows | 288: `else if ans.status == 404 then Null                                   // 218-219` → `else if ans.status != 404 then Null                                   // 218-219` | P5_OtherFailureThrows |
| `TenantAdminGrpcService.InviteUser` | P1_LimitReachedRefusesWithoutCallingTheIdp | 34: `s.Some? && s.value != Suspended && s.value != Deleted` → `s.Some? && s.value == Suspended && s.value != Deleted` | P1_LimitReachedRefusesWithoutCallingTheIdp |
|  | P2_ReservesTheCallersTenantTodayFirst | 62: `InviteResult(Ok, [reserve, CreateUser(tenantId)])                  // 40–52 (audit ignored)` → `InviteResult(Ok, [CreateUser(tenantId), reserve])                  // 40–52 (audit ignored)` (manual) | P2_ReservesTheCallersTenantTodayFirst |
|  | P3_InactiveTenantReservesAndCreatesNothing | 34: `s.Some? && s.value != Suspended && s.value != Deleted` → `s.Some? && s.value == Suspended && s.value != Deleted` | P3_InactiveTenantReservesAndCreatesNothing |
| `InviteQuotaRepository.TryReserveAsync` | P1_AtMostHundredReserved | 42: `if c < 100 then` → `if c <= 100 then` | CountAfterShape (a helper P1_AtMostHundredReserved's proof calls) |
|  | P2_HundredFirstRefused | 42: `if c < 100 then` → `if c <= 100 then` | P2_HundredFirstRefused |
|  | P3_CountMonotone | 42: `if c < 100 then` → `if c <= 100 then` | CountAfterShape (a helper P3_CountMonotone's proof calls) |
| `RateLimitInterceptor.Enforce` | P1_ThirtyFirstRefusedOthersPass | 52: `if \|services\| == 0 then None` → `if \|services\| != 0 then None` | FirstOrDefaultAdmin (a helper P1_ThirtyFirstRefusedOthersPass's proof calls) |
|  | P2_SeparateBuckets | 52: `if \|services\| == 0 then None` → `if \|services\| != 0 then None` | FirstOrDefaultLifecycle (a helper P2_SeparateBuckets's proof calls) |
|  | P3_AnotherCallersBucketUntouched | 52: `if \|services\| == 0 then None` → `if \|services\| != 0 then None` | FirstOrDefaultAdmin (a helper P3_AnotherCallersBucketUntouched's proof calls) |
|  | P4_OtherRpcsNeverSpendTheAdminBucket | 52: `if \|services\| == 0 then None` → `if \|services\| != 0 then None` | FirstOrDefaultOther (a helper P4_OtherRpcsNeverSpendTheAdminBucket's proof calls) |
| `OperatorAuthorizationPolicy.IsSatisfiedBy` | P1_OperatorImpliesSchemaAdmin | 114: `true` → `false` | P1_OperatorImpliesSchemaAdmin |
|  | P2_GroupsIrrelevantWithoutFallback | 107: `&& AnyValueEquals(FindAll(p, "groups"), os.GroupFallback.value)` → `\|\| AnyValueEquals(FindAll(p, "groups"), os.GroupFallback.value)` | P2_GroupsIrrelevantWithoutFallback |
|  | P3_AdminScopeIsOperator | 100: `true` → `false` | P3_AdminScopeIsOperator |
|  | P4_OperatorImpliesConsoleAdmin | 130: `\|\| IsTenantAdmin(SelectValues(FindAll(p, "groups")))` → `&& IsTenantAdmin(SelectValues(FindAll(p, "groups")))` | P4_OperatorImpliesConsoleAdmin |
|  | P5_FallbackGroupIsSchemaAdmin | 37: `if \|p\| == 0 then []` → `if \|p\| != 0 then []` | FindAllMembership (a helper P5_FallbackGroupIsSchemaAdmin's proof calls) |
|  | P6_ListedSubjectIsOperator | 100: `true` → `false` | P6_ListedSubjectIsOperator |
|  | P7_ConsoleAdminIsOperatorOrTenantAdmin | 37: `if \|p\| == 0 then []` → `if \|p\| != 0 then []` | FindAllMembership (a helper P7_ConsoleAdminIsOperatorOrTenantAdmin's proof calls) |
|  | P8_EmptyFallbackIsUnset | 141: `if fb.Some? && \|fb.value\| > 0 then Some(fb.value) else None)` → `if fb.Some? && \|fb.value\| >= 0 then Some(fb.value) else None)` | P8_EmptyFallbackIsUnset |
| `TokenRevocationCache.IsRevokedAsync` | P1_WithinFiveSecondsRevoked | 20: `if d as int + spanTicks <= MaxTicks then AddOk((d as int + spanTicks) as Ticks) else AddThrew` → `if d as int + spanTicks < MaxTicks then AddOk((d as int + spanTicks) as Ticks) else AddThrew` | P1_WithinFiveSecondsRevoked |
|  | P2_Boundary | 36: `if sub !in s.RevokedAt then Revoked(false)` → `if sub in s.RevokedAt then Revoked(false)` | P2_Boundary |
|  | P3_NoRowNotRevoked | 36: `if sub !in s.RevokedAt then Revoked(false)` → `if sub !in s.RevokedAt then Revoked(true)` | P3_NoRowNotRevoked |
| `StreamRevalidationInterceptor.RecheckAsync` | P1_NoWriteAfterRevocationOrSuspension | 62: `if seconds < UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew` → `if seconds <= UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew` | CheckAgreesWithRevoked (a helper P1_NoWriteAfterRevocationOrSuspension's proof calls) |
|  | P2_RevokedEndsUnauthenticated | 62: `if seconds < UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew` → `if seconds <= UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew` | CheckAgreesWithRevoked (a helper P2_RevokedEndsUnauthenticated's proof calls) |
|  | P3_InactiveTenantEndsPermissionDenied | 62: `if seconds < UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew` → `if seconds <= UnixMinSeconds \|\| seconds > UnixMaxSeconds then DtoThrew` | CheckAgreesWithRevoked (a helper P3_InactiveTenantEndsPermissionDenied's proof calls) |
|  | P4_BothWriteFormsRecheckFirst | 127: `if !r.Write? then (r, written)` → `if r.Write? then (r, written)` | P4_BothWriteFormsRecheckFirst |
| `TenantStatusCache.InvalidateAll` | P1_ReadBeforeInvalidateAllNeverServedAfter | 34: `if n < 10 then [DigitChar(n)] else NatToString(n / 10) + [DigitChar(n % 10)]` → `if n <= 10 then [DigitChar(n)] else NatToString(n / 10) + [DigitChar(n % 10)]` | NatToStringShape (a helper P1_ReadBeforeInvalidateAllNeverServedAfter's proof calls) |
|  | P2_FirstReadAfterInvalidateAllGoesToTheRepository | 34: `if n < 10 then [DigitChar(n)] else NatToString(n / 10) + [DigitChar(n % 10)]` → `if n <= 10 then [DigitChar(n)] else NatToString(n / 10) + [DigitChar(n % 10)]` | NatToStringShape (a helper P2_FirstReadAfterInvalidateAllGoesToTheRepository's proof calls) |
| `AuthInvalidationListener.ExecuteAsync` | P1_ListeningWriteInvalidates | 51: `if n == 0 then [] else [a] + RepeatAction(a, n - 1)` → `if n != 0 then [] else [a] + RepeatAction(a, n - 1)` | RepeatNoDelay (a helper P1_ListeningWriteInvalidates's proof calls) |
|  | P2_NoWriteMissedAcrossReconnect | 51: `if n == 0 then [] else [a] + RepeatAction(a, n - 1)` → `if n != 0 then [] else [a] + RepeatAction(a, n - 1)` | RepeatNoDelay (a helper P2_NoWriteMissedAcrossReconnect's proof calls) |
|  | P3_BackoffFromOneToThirtySeconds | 51: `if n == 0 then [] else [a] + RepeatAction(a, n - 1)` → `if n != 0 then [] else [a] + RepeatAction(a, n - 1)` | RepeatNoDelay (a helper P3_BackoffFromOneToThirtySeconds's proof calls) |
| `EngagementHealthChecker.CheckHealthAsync` | P1_AuthPendingOnlyFromRefusedLogin | 24: `env.selectOne == StepFails(AccessDenied) \|\| env.probe == ProbeFails(AccessDenied)` → `env.selectOne != StepFails(AccessDenied) \|\| env.probe == ProbeFails(AccessDenied)` | P1_AuthPendingOnlyFromRefusedLogin |
|  | P2_RowsMeanHealthy | 36: `if x == 0x7fff_ffff then -0x8000_0000 else x + 1` → `if x != 0x7fff_ffff then -0x8000_0000 else x + 1` | P2_RowsMeanHealthy |
| `HarnessChannel.Create` | P1_TheSchemePicksTheTransport | 20: `forall i :: 0 <= i < \|s\| ==> (s[i] as int) < 128` → `forall i :: 0 <= i <= \|s\| ==> (s[i] as int) < 128` | HttpsPrefixIsAsciiAndLower (a helper P1_TheSchemePicksTheTransport's proof calls) |
|  | P2_CredentialsGoOnlyToHttpsOrLoopback | 122: `\|\| !(UriParse(grpcUrl).value.isLoopback \|\| (grpcMayUseTls && UriParse(grpcUrl).value.scheme == UriSchemeHttps))` → `\|\| !(UriParse(grpcUrl).value.isLoopback \|\| (grpcMayUseTls && UriParse(grpcUrl).value.scheme != UriSchemeHttps))` | P2_CredentialsGoOnlyToHttpsOrLoopback |
| `Auth.BuildInvoker` | P1_TheSchemePicksTheTransport | 60: `var unsafeInsecure := !tls;                                                   // :37–42` → `var unsafeInsecure := tls;                                                   // :37–42` | P1_TheSchemePicksTheTransport |
| `conformance.grpcUsesTLS` | P1_TlsExactlyForAnHttpsScheme | 37: `\|s\| >= \|prefix\| && s[..\|prefix\|] == prefix` → `\|s\| >= \|prefix\| && s[..\|prefix\|] != prefix` | P1_TlsExactlyForAnHttpsScheme |
|  | P2_DialOptionFollowsTheScheme | 47: `TlsCreds` → `InsecureCreds` (manual) | P2_DialOptionFollowsTheScheme |
| `driver.parse_grpc_address` | P1_BareHostPortIsPlaintext | 56: `Parsed(host, port, parsed.scheme == HttpsScheme)` → `Parsed(host, port, parsed.scheme != HttpsScheme)` | P1_BareHostPortIsPlaintext |
|  | P2_HttpsSchemeIsTls | 56: `Parsed(host, port, parsed.scheme == HttpsScheme)` → `Parsed(host, port, parsed.scheme != HttpsScheme)` | P2_HttpsSchemeIsTls |
|  | P3_ChannelTransportFollowsUseTls | 75: `if callCreds == 0 then                                   // 386` → `if callCreds != 0 then                                   // 386` | P3_ChannelTransportFollowsUseTls |
| `driver.parseGrpcAddress` | P1_TlsExactlyForAnHttpsUrl | 47: `if !Contains(addr, SchemeSeparator) && \|HttpSchemePrefix + addr\| >= 0x8000_0000 then Throws` → `if !Contains(addr, SchemeSeparator) && \|HttpSchemePrefix + addr\| > 0x8000_0000 then Throws` | P1_TlsExactlyForAnHttpsUrl |
| `Driver.channelTransport` | P1_TlsExactlyForAnHttpsScheme | 15: `forall i :: 0 <= i < \|s\| ==> (s[i] as int) < 128` → `forall i :: 0 <= i <= \|s\| ==> (s[i] as int) < 128` | HttpsFacts (a helper P1_TlsExactlyForAnHttpsScheme's proof calls) |
| `roles.RequireRole` | P1_NoRoleBeforeTheAnswer | 46: `if !state.Ready? then` → `if state.Ready? then` | P1_NoRoleBeforeTheAnswer |
|  | P2_RolesAreTheServersAnswer | 46: `if !state.Ready? then` → `if state.Ready? then` | P2_RolesAreTheServersAnswer |
|  | P3_GuardRendersByState | 93: `else if !HasRole(state, role) then` → `else if HasRole(state, role) then` | P3_GuardRendersByState |
|  | P4_ProviderState | 38: `if data.Some? then Ready(data.value)                 // roles.data !== null` → `if data.Some? && failure.None? then Ready(data.value)                 // roles.data !== null` (manual) | P4_ProviderState |
| `blueprints-configmap.orchestrator-tokenreview` | P1_SuccessAdmitted | 48: `case PyDict(m) => \|m\| > 0` → `case PyDict(m) => \|m\| > 1` | P1_SuccessAdmitted |
|  | P2_ErrorsRefused | 82: `if !Truthy(jwt) then Ok(false)` → `if !Truthy(jwt) then Ok(true)` | P2_ErrorsRefused |
|  | P3_WrongIdentityRefused | 82: `if !Truthy(jwt) then Ok(false)` → `if !Truthy(jwt) then Ok(true)` | PinReadsIdentity (a helper P3_WrongIdentityRefused's proof calls) |
|  | P4_NoFederatedJwtRefused | 44: `case PyNone => false` → `case PyNone => true` | P4_NoFederatedJwtRefused |
|  | P5_MissingAssertionRefused | 82: `if !Truthy(jwt) then Ok(false)` → `if !Truthy(jwt) then Ok(true)` | P5_MissingAssertionRefused |
| `blueprints-configmap.orchestrator-workload` | P1_AnotherIssuerRefused | 76: `v == PyStr(s)` → `v != PyStr(s)` | IssuerMismatchRefusesEveryRelease (a helper P1_AnotherIssuerRefused's proof calls) |
| `secret-service-clients.admin-automation-workload` | P1_AnotherIssuerRefused | 70: `v == PyStr(s)` → `v != PyStr(s)` | IssuerMismatchRefusesEveryRelease (a helper P1_AnotherIssuerRefused's proof calls) |
| `secret-service-clients.console-admins` | P1_TenantAdminAdmitted | 32: `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| UserUid(user) in subjects` → `AkIsGroupMember(env, user, TenantAdminsGroup) && UserUid(user) in subjects` | P1_TenantAdminAdmitted |
|  | P2_ListedOperatorAdmitted | 32: `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| UserUid(user) in subjects` → `AkIsGroupMember(env, user, TenantAdminsGroup) && UserUid(user) in subjects` | P2_ListedOperatorAdmitted |
|  | P3_EveryoneElseRefused | 32: `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| UserUid(user) in subjects` → `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| UserUid(user) !in subjects` (manual) | P3_EveryoneElseRefused |
| `compose-service-clients.console-admins` | P1_ComposeAdmitsTheTwoGroups | 26: `AkIsGroupMember(env, user, TenantAdminsGroup) \|\| AkIsGroupMember(env, user, OperatorsGroup)` → `AkIsGroupMember(env, user, TenantAdminsGroup) && AkIsGroupMember(env, user, OperatorsGroup)` | P1_ComposeAdmitsTheTwoGroups |
| `recovery-flow.login-throttle` | P1_KnownWrongPasswordsFromZero | 40: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | OneRowThrottle (a helper P1_KnownWrongPasswordsFromZero's proof calls) |
|  | P2_ThreeWrongCodesReachMinusFive | 40: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | OneRowThrottle (a helper P2_ThreeWrongCodesReachMinusFive's proof calls) |
|  | P3_SuccessesFirstOffset | 40: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | OneRowThrottle (a helper P3_SuccessesFirstOffset's proof calls) |
|  | P4_InterleavedNoFixedBound | 40: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | OneRowThrottle (a helper P4_InterleavedNoFixedBound's proof calls) |
|  | P5_UnknownThirdPlanRefused | 40: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | OneRowThrottle (a helper P5_UnknownThirdPlanRefused's proof calls) |
|  | P6_AddressCapNeedsTenIdentifiers | 50: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | NegTotalBound (a helper P6_AddressCapNeedsTenIdentifiers's proof calls) |
|  | P7_EmailRowsCount | 40: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | FilterPairSubset (a helper P7_EmailRowsCount's proof calls) |
|  | P8_OtherAddressesNeverCount | 40: `if \|rows\| == 0 then []` → `if \|rows\| != 0 then []` | NoRowsAtIp (a helper P8_OtherAddressesNeverCount's proof calls) |
| `deployment-server.wait-for-migration` | P1_MigratedExactly | 48: `if !db.tableExists then false` → `if !db.tableExists then true` | P1_MigratedExactly |
|  | P2_WaitEndsAtTheFirstMigratedAnswer | 67: `if i >= \|probes\| then StillWaiting` → `if i > \|probes\| then StillWaiting` | WaitFromFinished (a helper P2_WaitEndsAtTheFirstMigratedAnswer's proof calls) |
| `admission-policy.validations` | P1_OperatorsAdmitted | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | OperatorIff (a helper P1_OperatorsAdmitted's proof calls) |
|  | P2_ClusterAdminDenied | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | ImagesChangedNoError (a helper P2_ClusterAdminDenied's proof calls) |
|  | P3_MetadataOnlyPodUpdateAdmitted | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | P3_MetadataOnlyPodUpdateAdmitted |
|  | P4_KubeletMintsTokens | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | P4_KubeletMintsTokens |
|  | P5_ScaleAdmitted | 151: `&& r.resource in [DeploymentsResource, ReplicaSetsResource, StatefulSetsResource, DaemonSetsResource] && r.sub == EmptyStr)` → `&& r.resource in [DeploymentsResource, ReplicaSetsResource, StatefulSetsResource, DaemonSetsResource] && r.sub != EmptyStr)` | P5_ScaleAdmitted |
|  | P6_NoProtectedPodOrTemplateForNonOperators | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | OperatorIff (a helper P6_NoProtectedPodOrTemplateForNonOperators's proof calls) |
|  | P7_MisnamedProtectedPodRefusedForAll | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | PodSaNoError (a helper P7_MisnamedProtectedPodRefusedForAll's proof calls) |
|  | P8_AdmittedProtectedPodIsGated | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | PodSaNoError (a helper P8_AdmittedProtectedPodIsGated's proof calls) |
|  | P9_ApiReplicaSetPodsAdmitted | 86: `if a == CFalse \|\| b == CFalse then CFalse` → `if a != CFalse \|\| b == CFalse then CFalse` | P9_ApiReplicaSetPodsAdmitted |
| `configmap-tls-proxy.public-locations` | P1_RootRedirects | 118: `if uri == FlowsDefaultAuthentication then Forward          // yaml:90` → `if uri != FlowsDefaultAuthentication then Forward          // yaml:90` | P1_RootRedirects |
|  | P2_SetupNotForwarded | 102: `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p` | P2_SetupNotForwarded |
|  | P3_InterfacesRefused | 102: `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p` | P3_InterfacesRefused |
|  | P4_AllowedFlowsForwarded | 130: `else if uri == Root then Redirect                          // yaml:113  return 302 <console>` → `else if uri != Root then Redirect                          // yaml:113  return 302 <console>` | P4_AllowedFlowsForwarded |
|  | P6_WebSocketAndLiveForwarded | 102: `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p` | P6_WebSocketAndLiveForwarded |
| `job-create-user.create-user` | P1_JobSetsPasswordOnFe0 | 113: `ProbeLoop(world, rootPw, beReplicas, feReplicas, 0, Fuel)` → `ProbeLoop(world, rootPw, beReplicas, feReplicas, 1, Fuel)` | P1_JobSetsPasswordOnFe0 |
|  | P2_BatchOnlyAfterEveryNodeListed | 113: `ProbeLoop(world, rootPw, beReplicas, feReplicas, 0, Fuel)` → `ProbeLoop(world, rootPw, beReplicas, feReplicas, 1, Fuel)` | P2_BatchOnlyAfterEveryNodeListed |
|  | P3_RevokeOperateAfterCreateUser | 52: `\|p\| <= \|s\| && s[..\|p\|] == p` → `\|p\| <= \|s\| && s[..\|p\|] != p` | NotPrefixAt (a helper P3_RevokeOperateAfterCreateUser's proof calls) |
|  | P4_PasswordAfterSetPassword | 113: `ProbeLoop(world, rootPw, beReplicas, feReplicas, 0, Fuel)` → `ProbeLoop(world, rootPw, beReplicas, feReplicas, 1, Fuel)` | P4_PasswordAfterSetPassword |
| `setup.sh.guards` | P1_KindGate | 38: `if i == \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)` → `if i != \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)` | SplitOnCons (a helper P1_KindGate's proof calls) |
|  | P3_EnrolOnlyWith15008 | 38: `if i == \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)` → `if i != \|s\| then [s] else [s[..i]] + SplitOn(s[i + 1..], b)` | PadOccursIffToken (a helper P3_EnrolOnlyWith15008's proof calls) |
| `setup.ps1.guards` | P1_KindGate | 36: `c == '\t' \|\| c == '\n' \|\| c == '\u000B' \|\| c == '\u000C' \|\| c == '\r' \|\| c == ' '` → `c != '\t' \|\| c == '\n' \|\| c == '\u000B' \|\| c == '\u000C' \|\| c == '\r' \|\| c == ' '` | P1_KindGate |
|  | P3_EnrolOnlyWith15008 | 15: `forall i :: 0 <= i < \|s\| ==> (s[i] as int) < 128` → `forall i :: 0 <= i <= \|s\| ==> (s[i] as int) < 128` | CaseFoldPlain (a helper P3_EnrolOnlyWith15008's proof calls) |
| `generate-compose-secrets.renew` | P1_RenewWhenAnyMissingOrExpiring | 100: `if leaves == [] then [] else [ReadStem(leaves[0])] + StemsOf(leaves[1..])` → `if leaves != [] then [] else [ReadStem(leaves[0])] + StemsOf(leaves[1..])` | RenewLoopSpec (a helper P1_RenewWhenAnyMissingOrExpiring's proof calls) |
|  | P2_RenewRewritesEverythingButTheCaKey | 85: `if \|s\| > 0 && IsIfs(s[0]) then DropIfs(s[1..]) else s` → `if \|s\| > 0 \|\| IsIfs(s[0]) then DropIfs(s[1..]) else s` | ReadStemSplit (a helper P2_RenewRewritesEverythingButTheCaKey's proof calls) |
| `authentik-probes.helpers` | P1_EdgeClient | 48: `0 <= i && i + \|sub\| <= \|s\| && s[i..i + \|sub\|] == sub` → `0 <= i && i + \|sub\| <= \|s\| && s[i..i + \|sub\|] != sub` | NewlineByteContained (a helper P1_EdgeClient's proof calls) |
|  | P2_IpIs | 143: `a == b \|\| a == MappedPrefix + b` → `a != b \|\| a == MappedPrefix + b` | P2_IpIs |
|  | P3_IsLoopback | 42: `\|p\| <= \|x\| && x[..\|p\|] == p` → `\|p\| <= \|x\| && x[..\|p\|] != p` | P3_IsLoopback |
|  | P4_IsConsole | 162: `x != [] && StripOneSlash(x) == StripOneSlash(consoleUrl)` → `x != [] && StripOneSlash(x) != StripOneSlash(consoleUrl)` | P4_IsConsole |
|  | P5_SetupShowsNoPrompt | 53: `exists i {:trigger OccursAt(s, sub, i)} :: 0 <= i <= \|s\| && OccursAt(s, sub, i)` → `exists i {:trigger OccursAt(s, sub, i)} :: 0 < i <= \|s\| && OccursAt(s, sub, i)` | GlobIsContains (a helper P5_SetupShowsNoPrompt's proof calls) |
|  | P6_Options | 185: `if \|args\| == 0 then Parsed(release, ns)` → `if \|args\| != 0 then Parsed(release, ns)` | P6_Options |
| `admission-check.classify` | P1_Classify | 49: `if rc == 0 then                      // 53: if [ "$1" -eq 0 ]; then` → `if rc != 0 then                      // 53: if [ "$1" -eq 0 ]; then` | P1_Classify |
|  | P2_Options | 72: `if \|args\| == 0 then                                   // 190: while [ $# -gt 0 ]; do` → `if \|args\| != 0 then                                   // 190: while [ $# -gt 0 ]; do` | P2_Options |
| `mesh-check.mtls_seen` | P1_MtlsSeen | 14: `forall i :: 0 <= i < \|s\| ==> s[i] as int < 256` → `forall i :: 0 <= i <= \|s\| ==> s[i] as int < 256` | MutualTlsShape (a helper P1_MtlsSeen's proof calls) |
|  | P2_Options | 108: `if \|args\| == 0 then Parsed(release, ns)                                     // line 111: while [ $# -gt 0 ]` → `if \|args\| != 0 then Parsed(release, ns)                                     // line 111: while [ $# -gt 0 ]` | P2_Options |
| `mesh-check.UNENROLLED_PY` | P1_RunningCountThenUnenrolled | 50: `if i == \|items\| then acc` → `if i != \|items\| then acc` | ComprehensionIsFilter (a helper P1_RunningCountThenUnenrolled's proof calls) |
| `kind-checks.alive-wait` | P1_AliveColumn | 102: `if 0 <= i < \|r\| then Ok(r[i])` → `if 0 < i < \|r\| then Ok(r[i])` | GenIndexColumn (a helper P1_AliveColumn's proof calls) |
|  | P2_WaitBounded | 227: `if k == 60 then 60` → `if k == 60 then 61` (manual) | ForLoopFacts (a helper P2_WaitBounded's proof calls) |

### Unconfirmed obligations (failed in the model; no confirmed runner)

- `deployment-server.wait-for-migration` P3_WaitObservesTheEndOfThePass: unconfirmed — Python inside a Helm `python -c` block: no runner in the confirmed-forms table.
  - Input: a fresh install whose migrator took lock 1000, wrote this image's `(version, build)` row to `authentik_version_history` and was killed before `migrate_schemas` finished (Postgres releases the lock).
  - Required: a migrated answer only after the pass ended.
  - Predicted: `migrated()` answers True (table, row, lock 1000 free); the wait ends and the server starts on a partly migrated schema. Proved in the model as `P3_CounterexampleDiedAfterRow`.
  - Supplementary run outside the runner table: none (not run: needs Postgres).
- `configmap-tls-proxy.public-locations` P5_OtherApiRefused: unconfirmed — nginx configuration: no runner in the confirmed-forms table.
  - Input: `GET /api/v3/flows/executor/default-authentication-flow/%0A` on `:9080` (normalised `$uri` ends in a newline).
  - Required: nothing else under `/api/` is forwarded.
  - Predicted: the regex location at yaml:89 matches, because PCRE's `$` also matches before a final newline, and the request is forwarded to Authentik instead of the fallback 404. Proved in the model as `P5_Counterexample`.
  - Supplementary run outside the runner table: none.
- `setup.sh.guards` P2_OnlyRunsOnePhase: unconfirmed — bash: no runner in the confirmed-forms table.
  - Input: `setup.sh --only "ambient-labels gateway"`.
  - Required: usage and exit 2 for an argument that is not one phase name.
  - Predicted: the line-47 glob accepts any run of adjacent phase names; line 138 runs `phase_ambient_labels gateway`, command not found, exit 127. Proved in the model as `P2_Witness`.
  - Supplementary run outside the runner table: the prover ran the real worktree script with stub `kind`/`helm`/`kubectl`: `line 138: phase_ambient_labels gateway: command not found`, exit 127; `--only no-such-phase` gave usage and exit 2.
- `setup.ps1.guards` P2_OnlyRunsOnePhase: unconfirmed — PowerShell: no runner in the confirmed-forms table (the repo has no Pester tests).
  - Input: `setup.ps1 "--only`n" istio` (the flag ends in a newline).
  - Required: a usage throw for any flag other than `--only` / `-only`.
  - Predicted: .NET's `$` matches before a final `\n`, so `-notmatch '^--?only$'` is false and the `istio` phase runs. Proved in the model as `P2_Witness_TrailingNewline`; `P2_HoldsWithoutTrailingNewline` proves P2 for every ASCII flag without a trailing newline.
  - Supplementary run outside the runner table: none.
- `fe-tls-entrypoint.settings` P1_EachSettingOnce: unconfirmed — bash: no runner in the confirmed-forms table.
  - Input: `fe.conf` holding the single byte `0x00` (or any content whose last byte is NUL).
  - Required: after a run each setting is on a line of its own.
  - Predicted: `$(tail -c 1 …)` drops the NUL, so no separating newline is written and `ssl_keystore_location = …` is glued onto the NUL line. Proved in the model (`Witness_P1`); `P1_HoldsWithoutNul` proves P1 for every NUL-free `fe.conf`.
  - Supplementary run outside the runner table: the prover ran the real lines 17–24 under bash 5.3.9 and GNU grep 3.12: output bytes as predicted, with bash's "command substitution: ignored null byte in input".
- `fe-tls-entrypoint.settings` P2_ExistingLinesKept: unconfirmed — bash: no runner in the confirmed-forms table.
  - Input: as P1: `fe.conf` = `0x00`.
  - Required: every existing line survives whole.
  - Predicted: the existing line `"\0"` becomes `"\0ssl_keystore_location = …"`. Proved in the model (`Witness_P2`); `P2_HoldsUnlessLastByteNul` proves P2 whenever the last byte is not NUL.
  - Supplementary run outside the runner table: as P1.

### Unproved obligations

- `EngagementHealthChecker.CheckHealthAsync` P3_HealthyIsExactlyTwoRows: unproved — the C# `int` row counter wraps (unchecked), so 2^32 + 2 rows also reads as Healthy (`P3_Counterexample`, proved). The fixed probe query cannot return that many rows, but no sound approved axiom says so: the axiom first approved at the gate stated the bound for every environment and proved `false`, and the user withdrew it (see the gate amendments).

### Design-only properties

- **Design-only: `LoginThrottle.P6_PlanFromZeroCancelledAtFifth`:** the in-plan cancel is the stock password stage's `failed_attempts_before_cancel` inside Authentik; no plan code block implements it (the plan's throttle expression decides only new plans at order 12 and order 0).
- **Design-only: `LoginThrottle.P7_PlanFromNegativeNeverCancelled`:** as P6: Authentik's password stage, not plan code.
- **Design-only: `LoginThrottle.P8_UnknownNeverCancelled`:** as P6: Authentik's password stage, not plan code.
- **Design-only: `StarRocksBootstrap.P1_StartAfterPasswordJoins`:** a BE or FE follower joining on a start after the password is the StarRocks image entrypoints' behaviour with `MYSQL_PWD` from `starrockscluster.yaml` (config); no plan code block implements it.
- **Design-only: `StarRocksBootstrap.P3_OldOrderDeadlocks`:** a property of the base's job order, which the plan deletes; no plan code block has that order.

### Ambiguities

- `StreamRevalidationInterceptor.RecheckAsync`: the spec's "effective tenant" (§2.3) is read as the acting user's `tenant_id` claim, which is what the code checks; the approval list states this reading.
- `admission-policy.validations` P3 and the CEL `object` variable: Dafny reserves `object`, so the model reads it through the accessor `ObjectOf(r)` (gate amendment).

## 2. Literal-wrongness findings

No literal-wrongness findings. (Six obligations failed in the model, but none has a confirmed runner, so each is recorded above as unconfirmed rather than here.)

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ Approve with literal-wrongness fixes: §2 and §3 are empty, but six obligations are unconfirmed (failed in the model, with no runner that can confirm them: `deployment-server.wait-for-migration` P3, `configmap-tls-proxy.public-locations` P5, `setup.sh.guards` P2, `setup.ps1.guards` P2, `fe-tls-entrypoint.settings` P1 and P2) and one is unproved (`EngagementHealthChecker.CheckHealthAsync` P3).
