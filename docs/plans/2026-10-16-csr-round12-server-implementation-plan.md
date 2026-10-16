# CSR Round 12 Sub-project 1: Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-16-csr-round12-server-design.md` (commit SHA: `dc375023`)

**Goal:** close round-12 #1, #2, #7, #8, #9, #13 (TMA F3) and #18 (TMA F8, the enrichment half) in the server, as the spec defines them.

**Architecture:**
- **Write path:** payload keys are canonicalised to their registered column at RPC entry; declared owner, field-permission and FK names are normalised to the column's spelling; registration refuses case-duplicate columns.
- **Read path:** popularity signals count only when the child is no more restricted than the parent, SearchSimilar answers from a column allow-list, and decay uses only readable fields; enrichment's target rule is re-checked on reload.
- **Privileged work moves to the worker:** RegisterSchema's DDL and registry write, `/admin/reconcile` and DLQ replay run as rows in `IversonPrivilegedRequests` that the worker executes; the api queues and waits.
- **Credentials:** the api connects to Postgres as `iverson_api` (runtime role only, no DDL, no BYPASSRLS); every StarRocks data connection uses a per-tenant login whose default role is that tenant's role; `iverson_app` keeps only `OPERATE ON SYSTEM`.

**Tech stack:**
- .NET 10: `Iverson.Api`, `Iverson.Sql`, `Iverson.StarRocks`, `Iverson.Events`, tested with xUnit 2.9, FluentAssertions and NSubstitute; Testcontainers Postgres (`postgres:18-alpine`) and StarRocks (`starrocks/allin1-ubuntu`);
- Npgsql and Dapper; MySqlConnector 2.6.2;
- Helm 3 umbrella chart at `Iverson.Server/deploy/helm/iverson`, CloudNativePG 1.30.0 (chart 0.29.0), docker compose on podman.

---

## Global Constraints

**From the spec:**
- **One branch, one merge.** No proto change and no SDK change.
- **No StarRocks tenant data from before round 12** exists on any stack (spec decision 5); there is no backfill.
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation.
- **Most of `docs/` is gitignored** (`docs/plans/`, `docs/specs/`, `docs/criticalreviews/`, `docs/reviews/`, `docs/performance/`, `docs/superpowers/`, `.gitignore:46-51`): commit files there with `git add -f`. `docs/runbooks/` is tracked normally.
- **The user's running compose stack is off-limits.** Never start, stop or modify the `iversonserver` project's containers, volumes, networks or images, or another session's `rfdt-*` containers and `tei_models` volume. Never run `docker compose up` against `Iverson.Server/docker-compose.yml` without the isolated project name Task 13 uses. Live checks use throwaway, uniquely named resources and delete them afterwards.
- **Memory.** Run one heavy process at a time: a WSL OOM kills the whole VM. Wrap builds and test runs in `systemd-run --user --scope -q -p MemoryMax=6G`, with `MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false` exported. Container-backed test classes (`[Trait("Category", "Integration")]`) count as heavy.

**Session rules:**
- **Location.** Work in the worktree `/home/ben/repositories/Iverson/.worktrees/csr12-server` on branch `csr12-server`. Use absolute paths, and never `cd` to the main checkout.
- **Line numbers.** Line numbers cited in a task are at `6b942e1c`. Earlier tasks shift them, so **match every edit on the quoted text**, not on the number.
- **Commands.**
  - Use `command grep` (plain `grep` is a gitignore-aware ugrep that skips `docs/`), or `git grep`.
  - Every repo-wide grep excludes `.env*`; nobody reads the main checkout's `Iverson.Server/.env`.
  - The docker CLI is podman.
  - Kill processes by PID, never `pkill -f`.
- **Helm.** Run `helm dependency build` in `Iverson.Server/deploy/helm/iverson` before every `helm template` or `helm lint`, because a stale `charts/*.tgz` renders old subchart content.
- **Commits.**
  - Add the task's files by explicit path. Never use `-A` or `.`, and never touch `.superpowers/` or `docs/security/tma.md`.
  - Messages are lowercase imperative with no prefix, and end with exactly `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Do not use your own model name in the trailer.
- **Scratch.** Scratch files go under `$SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd` (Task 0 creates it), never in the repo.
- **Shell state.** Each bash block runs in a fresh shell: no variable or working directory carries over. Blocks that use `$SCR`, `$WT` or `$LIVE` start with `source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh`. Blocks with repo-relative paths start with a `cd` to the worktree, or to its `Iverson.Server`.
- **Test filters.** `--filter "FullyQualifiedName~X"` is a substring match; this plan writes `~.ClassName.` with dots on both sides. A new class name must not be a substring of an existing one.

## File Structure

**Create:**
- `Iverson.Server/Iverson.Api.Tests/Helpers/InProcessSchemaRegistrationExecutor.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryPostgresRoundTripTests.cs` (Task 6)
- `Iverson.Server/Iverson.Api/ApiLoginGrants.cs` (Task 9)
- `Iverson.Server/Iverson.Api/Grpc/ISchemaRegistrationExecutor.cs` (Task 7)
- `Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs` (Task 2)
- `Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs` (Task 4)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestClient.cs` (Task 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs` (Task 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs` (Task 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestSchema.cs` (Task 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs` (Task 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs` (Task 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs` (Task 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs` (Task 7)
- `Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs` (Task 5)
- `Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs` (Task 10)
- `Iverson.Server/Iverson.Api/Tenancy/StarRocksTenantSchema.cs` (Task 10)
- `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs` (Task 9)
- `Iverson.Server/Iverson.Sql/ExistingTableInspector.cs` (Task 7)
- `Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs` (Task 7)
- `Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs` (Task 7)
- `Iverson.Server/Iverson.Sql/PrivilegedRequestRow.cs` (Task 7)
- `Iverson.Server/Iverson.StarRocks/IProvisionedTenantDirectory.cs` (Task 10)
- `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/secret-api.yaml` (Task 12)
- `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-tenant-key.yaml` (Task 12)
- `docs/runbooks/csr12-server.md` (Task 12)

**Modify:**
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` (Task 7, 9)
- `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api/ApiLoginGrants.cs` (Task 10)
- `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` (Task 9)
- `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs` (Task 3)
- `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs` (Task 5)
- `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` (Task 4)
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (Task 2, 9)
- `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (Task 2)
- `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` (Task 9)
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (Task 4, 11)
- `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs` (Task 4)
- `Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs` (Task 2)
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (Task 1, 5, 7)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs` (Task 8)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs` (Task 8)
- `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs` (Task 8)
- `Iverson.Server/Iverson.Api/Program.cs` (Task 7, 8, 9, 10)
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` (Task 1)
- `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` (Task 1, 5, 7, 9)
- `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs` (Task 10)
- `Iverson.Server/Iverson.Api/appsettings.Development.json` (Task 10, 12)
- `Iverson.Server/Iverson.Sql/DlqRepository.cs` (Task 8)
- `Iverson.Server/Iverson.Sql/DlqRow.cs` (Task 8)
- `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (Task 7, 8, 9)
- `Iverson.Server/Iverson.Sql/OutboxWriter.cs` (Task 9)
- `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` (Task 9)
- `Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs` (Task 7)
- `Iverson.Server/Iverson.Sql/ServiceCollectionExtensions.cs` (Task 7)
- `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (Task 3, 10)
- `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs` (Task 10)
- `Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs:19, 37, 39-40` (Task 11)
- `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs` (Task 10)
- `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` (Task 12)
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/job-revoke-cross-db.yaml` (Task 12)
- `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml` (Task 12)
- `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml` (Task 12)
- `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml` (Task 12)
- `Iverson.Server/docker-compose.yml` (Task 12)
- `scripts/generate-compose-secrets.sh` (Task 12)

**Test:**
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (Task 11)
- `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestTestData.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestWorkerTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/QueuedSchemaRegistrationExecutorTests.cs` (Task 7)
- `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/SchemaRegistrationApplierTests.cs` (Task 7)
- `Iverson.Server/Iverson.StarRocks.Tests/StarRocksResiliencePipelineFactoryTests.cs` (Task 11)
- `Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs` (Task 11)

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` (and reconfirmed by CDR rounds 1–2) at spec-write time and are NOT re-verified here. Trusted as ground truth:

Evidence was gathered on 2026-10-16 against `f7eeba79`. "Probed" means run against a live container. The full reports are in the session scratchpad (`csr12-server/verify-section*.md`, `store-credentials-map.md`).

1. **Every client write goes through the gate,** in the four write RPCs (`ObjectPersistenceGrpcService.cs:35,117`, `ObjectMappingGrpcService.cs:155,221`). Update's `ExtractKey` and row fetch run before it (`:103,114`; `:210,220`), hence §2.1's placement.
2. **.NET sends acronym-led names in a form the server can't map.** Probed: `JsonNamingPolicy.CamelCase` gives `URLPath`→`urlPath`, `ID`→`id` and `IPAddress`→`ipAddress`. The SDK registers `prop.Name` (`SchemaRegistrar.cs:146`), and the server only upper-cases the first letter (`ProtoPayloadHelper.cs:13-33`).
3. **Every legitimate payload key is a column, or is ignored.** Only .NET differs from the registered name, and only by case. Python, TypeScript, Java and Go send the registered name. Column names are `KeyColumn` plus `ScalarColumns`, which include FK, embedding, chunk, metadata and enrichment columns and `__TenantId` (`SchemaBuilder.cs:60-64,229`).
4. **Case-duplicate columns can be registered today,** and so can a navigation name that matches another column (`SchemaDescriptor.cs:139-140`). User decision 7.
5. **The declared owner, field-permission and FK names keep the caller's spelling** (`SchemaRegistrationOrchestrator.cs:588-597`, `RowFieldAuthorizationEvaluator.cs:117`), giving the spoofing in §2.2.
6. **`json_populate_record` matches keys case-sensitively, and StarRocks resolves column names case-insensitively.** Probed on StarRocks 4.1.1: an INSERT naming `OWNERID` fills `OwnerId`.
7. **The popularity signal is computed only in `PopularitySignalUpdater`,** for the consumer and the sweep, and read only in `ObjectSearchGrpcService`. Its keys are `{relation camelCase}Count` and `…CountBuckets`. No SDK, the console or the agent reads them.
8. **The evaluator's read semantics give §3.1's rule** (`RowFieldAuthorizationEvaluator.cs:67-186`). Owner-scoped readers need no role, so the parent must have no owner field.
9. **`decision.AllowedFields` is in scope at all three decay sites.**
10. **The enrichment rule can be restated on `SchemaDescriptor`, and disabling enrichment is clean.** `LoadAsync` is the single reload path (`SchemaRegistry.cs:67-212`); empty targets return early (`EnrichmentConsumer.cs:97,266`). `EscapeUntrustedText` rewrites only `<<<` and `>>>`.
11. **Registration's privileged effects are only the DDL, the registry write and the re-render enqueue** (`SchemaRegistrationOrchestrator.cs:324-345`). `RegisterAsync` also updates the caller's cache (`SchemaRegistry.cs:235`), hence the api reloads.
12. **The model-change recovery adopts the existing table** (`SchemaRegistrationOrchestrator.cs:130-140`).
13. **No plumbing table carries `__TenantId`,** and only `iverson_token_revocations` can collide by derived name. Plumbing DDL: `TokenRevocationRepository.cs:15`, `EnrichmentStateRepository.cs:8`, `SchemaRegistryRepository.cs:8`, `DocumentRerenderQueueRepository.cs:19`, `ReconciliationSchema.cs:29`, `DlqSchema.cs:12`, `TenantSchema.cs:7`.
14. **Owned tables can hold other tenants' rows written before `02abddb3`,** so the adoption check queries.
15. **The worker has every service the requests need** (schema manager, registry, rerender queue, `ReconciliationService`, DLQ repository, event producer). The worker's registry can be stale, hence §5.1's reload.
16. **Edge timeouts:** nothing overrides the nginx (60 s) or ALB (60 s idle) defaults. GCE and AGIC default to 30 s, though gRPC through them already fails today (`values-gcp.yaml:102`). Kestrel has no request timeout. `/admin` reconcile and DLQ routes are port-forward only (`Program.cs:621,657,696`; `admin-api-ingress.yaml:68-74`). Defaults are from vendor documentation and were not re-checked live.
17. **The claim and expiry race resolves cleanly.** Probed on Postgres 18: with the worker's claim holding the row, the api's `UPDATE … WHERE Status='pending'` waits, re-evaluates and affects 0 rows; the status stays `running`.
18. **CNPG 1.30.0 (chart 0.29.0) supports managed roles with `inRoles` and a basic-auth `passwordSecret`,** reconciled on existing clusters (CNPG docs). The repo already uses one (`cluster.yaml:48-54`).
19. **The api's complete Postgres access list is in §6.2:**
    - startup DDL;
    - the registry load and 30 s DDL;
    - revocation and tenant-status reads;
    - entity runtime DML;
    - outbox insert, delete by `Id`, and the Deleted/Updated rows;
    - tenant lifecycle, including the DELETE rollback (`TenantLifecycleGrpcService.cs:44-48`);
    - the console readers (`IversonTenants` SELECT only);
    - the DLQ routes;
    - `/health`.

    `ObjectSearchGrpcService` makes no Postgres access.
20. **A non-owner login works for the api's transactions, with the right denials.** Probed on Postgres 18:
    - an entity upsert under `iverson_runtime` with RLS, then `RESET ROLE`, an outbox insert and a delete by `Id`, succeed with `INSERT, DELETE, SELECT ("Id")`;
    - reading outbox payloads, `SET ROLE iverson_maintenance` and DDL are denied;
    - DLQ column grants allow the listed columns and deny `MessageValue` and `SELECT *`;
    - the revocation `ON CONFLICT` upsert works with table grants;
    - `UPDATE ("Status")` denies other columns.
21. **The dead branches are unreachable.** Loaded and built descriptors always have a tenant column, and the only non-denied decision carries a tenant (`SchemaRegistry.LoadAsync`, `SchemaBuilder.cs:229,247`, `RowFieldAuthorizationEvaluator.cs:29-33`).
22. **CONNECT ordering.** The Authentik hook waits only for the `authentik` role (`job-revoke-cross-db.yaml:65-67`). On an upgraded cluster PUBLIC CONNECT is already revoked, and hooks run after `--wait` (`ReadinessPolicy.cs:9-13`), hence the worker grants CONNECT.
23. **Startup DDL and ordering.**
    - `SchemaRegistry.LoadAsync` runs `EnsureTableAsync` every 30 s in the api (`SchemaRefreshWorker`, `Program.cs:485`).
    - The legacy-tenant seed runs in the api (`Program.cs:754-759`).
    - Startup runs before Kestrel listens.
    - Compose starts the worker after the api (`docker-compose.yml:683-684`), and the api's compose healthcheck is TCP only (`:550`).
24. **A per-tenant StarRocks login is confined to its tenant.** Probed on StarRocks 4.1.1 and 4.1.6:
    - a 35-character name and a 64-character password are accepted;
    - `CREATE USER IF NOT EXISTS … DEFAULT ROLE` is idempotent;
    - `ALTER USER IDENTIFIED BY` and `SET PROPERTIES` work from the provisioner with `user_admin`, and `SHOW PROPERTY` shows the caps;
    - the role is active at login with no `SET ROLE`;
    - another tenant's existing table is denied (5203), and its missing table is 5502;
    - `SET ROLE` to an ungranted role is refused, and `SET ROLE ALL` doesn't widen;
    - `SHOW BACKENDS` is denied.
25. **Backfill and revoke work as the provisioner.** Probed on StarRocks 4.1.6:
    - `SHOW DATABASES LIKE 'iverson_tenant_%'` lists every database with a direct `ALTER` grant, under any role;
    - it can list `iverson_app`'s grants and revoke tenant roles;
    - `iverson_app` keeps `SHOW BACKENDS`;
    - a revoked role gives "… is not granted to …", which the current read path treats as empty, hence the post-upgrade revoke.
26. **Pooled reuse and session state.** Probed with MySqlConnector 2.6.2 on StarRocks 4.1.6, and the reset rows on 4.1.1:
    - the pool reuses the connection;
    - the default role and user variables persist across reuse, with or without the reset;
    - with the default `ConnectionReset=true`, the reset puts system session variables back to their global defaults: after one reuse a capped login reads `query_timeout` 300 and `exec_mem_limit` 2147483648, not its 180 and 4294967296;
    - with `ConnectionReset=false` the caps hold, and a system variable set on one checkout persists to the next;
    - `SET ROLE NONE` before return leaves the next checkout with no role.
27. **MySqlConnector 2.6.2 pool options behave as documented:** `MaximumPoolSize` (default 100), `ConnectionIdleTimeout` (default 180 s, reaped every 30–60 s), `MinimumPoolSize` 0, and a full pool waits up to `ConnectionTimeout` (15 s) and then throws. That a distinct connection string gets a distinct pool is library behaviour that the repo's tests rely on (`MatchRowsIntegrationTests.cs:332-345`).
28. **StarRocks 4.1.6 FE limits.** Probed: `qe_max_connection` = 4096 (not mutable), and the per-user default is `max_user_connections` 1024.
29. **Every StarRocks connection site** is enumerated in `verify-section45.md` D15: data paths P1–P2, authz-null and executor P3, provisioner P4, readiness and liveness P5–P6, `/health` P7, dev appsettings P8. LoadTest and DirectSeeder use root against the legacy shared `iverson` database and are unaffected.
30. **The provisioner still needs its self-granted role and ALTER** (`EngagementRepository.cs:529-544`). `DEFAULT ROLE` requires the role to be granted first (StarRocks ALTER USER docs).
31. **The tenant key must be its own Secret.** Adding a key to an existing lookup Secret renders empty on upgrade (`secret-provisioner.yaml:17-21`).
32. **Only the api and worker Deployments, the Authentik hook and compose reference `<release>-postgres-app` or `ConnectionStrings__Postgres`.** ClientConformance and LoadTest have their own owner strings, outside the api pod.
33. **Every `EntityEvent` producer emits exact column names,** which the ordinal StarRocks upsert relies on (CDR-1 §0 R4 producer closure).
34. **`iverson_runtime` exists before CNPG reconciles `iverson_api`'s `inRoles`.** `postInitApplicationSQL` creates it at initdb (`charts/postgres/templates/cluster.yaml:43-47`), and existing clusters already have it (CDR-1 U8).
35. **The api needs no sequence grants.** Every plumbing key it inserts is uuid or text (`ReconciliationSchema.cs:31-44`, `TenantSchema.cs:9-16`, `TokenRevocationRepository.cs:15-18`, and the new uuid `Id`) (CDR-1 U9).
36. **PUBLIC keeps USAGE on schema `public`.** `git grep -i "on schema public\|REVOKE ALL" -- Iverson.Server/` finds nothing (CDR-1 U10).

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time (drafting reads; the in-order replay and per-task build/test runs are recorded at the end of this table).

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | 1 | The edited source files exist at the stated paths and lines | `SchemaBuilder.cs` (473 lines): `return new SchemaDescriptor` `:231`, `DocumentTemplateSource … };` `:254-256`. `SchemaRegistry.cs` (252): validation `LogError` `:204-207`, `_schemas[typeName] = descriptor;` `:210`, its only occurrence. `SchemaRegistrationOrchestrator.cs` (767): `:83-84`, `:266-268`, `:443-444`. `ObjectPersistenceGrpcService.cs` (177): `:33`, `:51-53`, `:103`, `:131-133`. `ObjectMappingGrpcService.cs`: `:153`, `:162-164`, `:210`, `:233-235`. `ProtoPayloadHelper.cs` (45). `EngagementRepository.cs`: `UpsertAsync` `:440-480`, `DeleteAsync` `:482`. `EngagementStoreConsumer.cs` (172): `WithOwnerValue` `:118-133` |
| 2 | 1 | The new files and symbols do not exist, and their names collide with nothing | `git grep -c` over `Iverson.Server` finds 0 hits for `DeclaredNameNormalizationTests`, `CanonicalPayloadKeyRpcTests`, `PayloadKeyCanonicalizer`, `EngagementUpsertEntriesTests`, `UpsertEntries`, `NormalizeDeclaredNames`, `RejectCaseDuplicateProperties` and `RegistrationCaseCollisionTests` |
| 3 | 1 | The test anchors exist once | `SchemaRegistrationOrchestratorTests.cs:1650` (`…Contain("WidgetId").And.Contain("identical to its foreign key");`) ends `RegisterAsync_WithOneToManyPropertyNameEqualsForeignKey_ThrowsInvalidArgument`, a phase-1-only test. `EngagementStoreConsumerTests.cs:512` `// ── Registration gate ──…`. Replay check: each occurs exactly once |
| 4 | 2 | `EnforceWriteAuthorization` signature and visibility | `AuthorizationFieldMasking.cs:11` `internal static class`. `:58-68` takes `(IRowFieldAuthorizationEvaluator, ClaimsPrincipal?, SchemaDescriptor, Struct, AuthorizationAction, string, string?, bool requireExistingRow, AuditLog, IPayloadSizeValidator)` and returns `IReadOnlyCollection<string>`. `Iverson.Api.csproj:10-12` has `InternalsVisibleTo Iverson.Api.Tests`. `AuthorizationFieldMaskingTests.cs:196-207` calls it the same way |
| 5 | 2 | `SchemaBuilder.BuildDescriptor(TypeDescriptor, IEmbeddingService)` is internal static; `SchemaBuilder` declares the alias `SchemaRelationKind` | `SchemaBuilder.cs:17, 33, 7` |
| 6 | 2 | Record shapes used by `NormalizeDeclaredNames` and the fixtures | `SchemaDescriptor.cs`: `RelationDescriptor(PropertyName, Kind, RelatedTypeName, ForeignKey)` `:127-131`; `AuthorizationRules(OwnerField, RowPermissions, FieldPermissions)` with an `init` OwnerField `:145-167`; `FieldPermission(FieldName, ReadableRoles, WritableRoles)` `:171-174`; `Relations`, `ScalarColumns` and `KeyColumn` are `required … { get; init; }` `:34-39`; `IsTenantColumn` `:20-21` |
| 7 | 2 | Proto contract fields used in the tests | `object_mapping.proto:70-94`: `RelationDescriptor{property_name, kind, related_type, foreign_key}`, `RowPermission{role, can_write_all}`, `FieldPermission{field_name, readable_roles}`, `AuthorizationRules{owner_field, row_permissions, field_permissions}`. Used in the same C# shape at `SchemaRegistrationOrchestratorTests.cs:58, 1229-1240, 1600-1606`. `[InlineData(Client.Contracts.RelationKind.X)]` is already used at `:297-298` |
| 8 | 2 | `SchemaRegistry` constructor and `LoadAllAsync`'s return type | `SchemaRegistry.cs:8-10` `(ISchemaRegistryRepository, ILogger<SchemaRegistry>)`. `IRecordStoreRoles.cs:194` `Task<IEnumerable<(string TypeName, string SchemaJson)>> LoadAllAsync()`. `SchemaRegistryTests.cs:288-296` stubs it with a `List<(string TypeName, string SchemaJson)>` |
| 9 | 2 | Service constructors used by `CanonicalPayloadKeyRpcTests` | `ObjectPersistenceGrpcService.cs:16-27` (11 parameters, in the order used). `ObjectMappingGrpcService.cs:21-37` (15 parameters, in the order used; `ObjectMappingGrpcServiceTests.cs:91-106` passes the same list). `IOutboxWriter.UpsertAndEnqueueOutboxAsync(TableSchema, string, string, string payloadJson, string? tenantId = null)` is at `OutboxWriter.cs:7`. `EngagementQueryLimitOptions.Default` is at `EngagementQueryLimitOptions.cs:92` |
| 10 | 2 | Test helpers | `ActingUserFixtures.Principal(sub, params groups)` gives tenant `test-tenant` (`ActingUserFixtures.cs:11-20`). `SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(bool withOwnerField, string ownerFieldName = "OwnerId")` (`SchemaFixtures.cs:275-276`) gives `test-bypass` CanWriteAll only, OwnerId TEXT and the reserved tenant column (`:278-306`). `TestServerCallContext.Create()` is in `Helpers/TestServerCallContext.cs` and is used throughout. `ActingUserAccessor` (`Iverson.Api/IActingUserAccessor.cs:10`) has a settable `ActingUser` |
| 11 | 2 | `EngagementTableSchema` and `EngagementColumnSchema` are public positional records; `Iverson.StarRocks` exposes internals to its tests | `EngagementTableSchema.cs:3-14`. `Iverson.StarRocks.csproj:8-10` `InternalsVisibleTo Iverson.StarRocks.Tests`. The precedent for an internal static seam is `EngagementRepository.OpenProvisionerConnectionAsync`, tested in `EngagementRepositoryProvisioningTests.cs:33` |
| 12 | 2 | Namespaces resolve without clashes | Types in `Iverson.Api.Schema` (enumerated): none is named `Value`, `Status`, `Struct` or `ObjectType`. The only clashes with `Iverson.Client.Contracts` are `AuthorizationRules`, `FieldPermission`, `RelationDescriptor`, `RelationKind` and `RowPermission`; the new test files reach these only through `using` aliases. `EntityAccess` and `TableSchema` are in `Iverson.Sql` (`IRecordStoreRoles.cs:132`). `IEntityRelationResolver` and `AuditLog` are in `Iverson.Api.Grpc`. `Iverson.Api.Tests.csproj:30` defines the global alias `Value` |
| 13 | 3 | The test commands are valid | `Iverson.Api.Tests` and `Iverson.StarRocks.Tests` target `net10.0` with xUnit 2.9.3. Container classes carry `[Trait("Category", "Integration")]` (`UpdateCarryForwardPostgresIntegrationTests.cs:25`, `TenantIsolationIntegrationTests.cs:15`), so `Category!=Integration` excludes them. The filters use `FullyQualifiedName~.Class.`, so `.EngagementStoreConsumerTests.` does not match `EngagementStoreConsumerKafkaOrderingTests` |
| 14 | 4 | Task 1 before Task 3; Task 2 before Task 3 | `WithOwnerValue` writes under `ownerField` (Task 3), which is the column spelling only after `NormalizeDeclaredNames` (Task 1). The ordinal upsert (Task 3) is lossless only once gate-path events carry exact column keys (Task 2), and enrichment and reconciliation republish `row_to_json` (spec VA 33). Task 2's tests do not depend on Task 1: their owner field `OwnerId` already equals its column |
| 15 | 4 | Shared hunks with later tasks | `SchemaRegistry.LoadAsync`: Task 1 inserts before `_schemas[typeName] = descriptor;` (`:210`). Task 5 also inserts its enrichment step there and Task 9 deletes `:69`; both quote the file as Task 1 leaves it. `SchemaRegistrationOrchestrator.cs`: Task 1's hunks are `:83-84`, `:266-268` and `:443`; Task 5 edits `ValidateEnrichmentTargets` (`:631-753`) and Task 7 edits phase 3 (`:324-350`), which are disjoint. `ObjectMappingGrpcService.cs`: Task 9 edits `:340-348`, disjoint from Task 2's `:153-235`. `EngagementRepository.cs`: Task 10 edits connection handling and provisioning; Task 3 touches `:448-462` and inserts above `:482` |
| 16 | 5 | C# and BCL features | `net10.0`, so C# 14: collection expressions, `with` on records and on `init` properties, `is not { } x ? … : …` definite assignment, and local functions. `field` is already used in `SchemaDescriptor.cs:165`. `Dictionary.TryGetValue`, LINQ `Prepend`/`Append`/`Distinct(comparer)`/`GroupBy(comparer)`, `Contains(value, comparer)` and `ToHashSet(comparer)` are all in the BCL. `HashSet<string>` implements `IReadOnlySet<string>` |
| 17 | 5 | Protobuf and System.Text.Json behaviour | `MapField<string, Value>`: `Remove`, indexer set and `Keys`. Keys are collected before mutation, and `StructFieldAccess.SetField` (`StructFieldAccess.cs:97-104`) mutates the same way. `JsonObject` keys are case-sensitive, and `node.Select(kv => kv.Key)` is the pattern of `OutboxWriter.WithTenantColumn` (`OutboxWriter.cs:93-97`). `Dictionary<string, JsonElement>` deserialization keeps every key ordinally |
| 18 | 5 | NSubstitute and FluentAssertions idioms | `Arg.Do<string>` mixed with `Arg.Any` is already used at `EngagementStoreConsumerTests.cs:305`. `act.Should().Throw<RpcException>().Which` on a `Func<T>` is used at `StructSerializerTests.cs:56`. `ContainSingle().Which` and `.Keys.Should().NotContain([…])` (`ObjectMappingGrpcServiceTests.cs:3142`). `BeEquivalentTo([…])` with a collection expression is the pattern of R11's `PipelineIntegrationTests` |
| 19 | 5 | Evaluator semantics behind the gate-level tests | `RowFieldAuthorizationEvaluator.cs:35-62`. A Write is bypass when a group matches a `RowPermission` with `CanWriteAll`. Otherwise, with an `OwnerField` and a `sub`, it is ownership-required with `OwnerFieldName = rules.OwnerField`. With no `FieldPermissions`, `AllowedFields` is null (`:66`). `OwnerImmutable` reads `schema.Authorization.OwnerField` through `StructFieldAccess.Candidates`, exact or lower-first (`AuthorizationFieldMasking.cs:191-201`; `StructFieldAccess.cs:55-60`) |
| 20 | 6 | `StructSerializer.SerializePayload` gains an optional `SchemaDescriptor? schema` | Callers (`command grep`): production `ObjectPersistenceGrpcService.cs:53, 133` and `ObjectMappingGrpcService.cs:164, 235`, all four updated. Tests: `StructSerializerTests.cs` (6 calls) and `AuthorizationFieldMaskingTests.cs:100, 123, 829` pass no schema, so their behaviour is unchanged. `UpperFirst` is unchanged; it is also used by `AuthorizationFieldMasking.cs:308, 322, 356` and `ObjectSearchGrpcService.cs:480` |
| 21 | 6 | Canonicalisation breaks no existing RPC test | `command grep` of `ObjectMappingGrpcServiceTests`, `ObjectPersistenceGrpcServiceTests`, `OwnerPrivateTypeVisibilityTests` and `UpdateCarryForwardPostgresIntegrationTests` for payload keys that start lower-case or are re-cased finds none. Lower-case keys appear only in component tests (`EntityKeyAccessorTests`, `RelationValidatorTests`), which do not go through the RPCs. The smuggle test `AuthorizationFieldMaskingTests.cs:429-436` calls the gate directly. The persistence smuggle test (`ObjectPersistenceGrpcServiceTests.cs:979-995`) sends the reserved spelling, which is excluded from the map |
| 22 | 6 | Mapping Post and Update now echo canonical keys | Both return `request.Payload` (`ObjectMappingGrpcService.cs:196, 281`). The spec (§2.1, VA 3) shows that only .NET sends non-canonical spellings, and its reader is case-insensitive. No RPC test asserts a camelCase response key (grep in #21) |
| 23 | 6 | `PayloadSizeValidator` now sees .NET camelCase columns | It matches the exact `column.Name` (`PayloadSizeValidator.cs:48-52`). After canonicalisation it applies to .NET payloads, which is the spec's intended behaviour change. It needs a release note (spec §2.1, §10); see forced decision F2 |
| 24 | 6 | `BuildDescriptor` now normalises declared names | One production caller, `SchemaRegistrationOrchestrator.cs:163`. 33 test call sites. The only tests that register a name spelled unlike its column are `SchemaRegistrationOrchestratorTests.cs:1249, 1358, 1394` (`summary`, `body`). They assert message substrings (`"cannot be enforced"`, `"a subset of them"`) that do not contain the name, so they stay green. Their case-insensitive comparisons in `ValidateEnrichmentTargets` now see the normalised name, which is harmless |
| 25 | 6 | Loaded and built descriptors now carry column spellings for `OwnerField`, `FieldName` and non-OneToMany `ForeignKey` | Readers of `OwnerField`: `EngagementStoreConsumer.cs:78`, `IntelligenceStoreConsumer.cs:126, 298`, `ObjectSearchGrpcService.cs:218, 387, 542`, `ObjectSearchGrpcService.MatchPattern.cs:90`, `AuthorizationFieldMasking.cs:191, 224`, `RowFieldAuthorizationEvaluator.cs:51, 58`, `SchemaBuilder.cs:359`. Every one compares case-insensitively or through `Candidates`, so a column spelling only makes more of them match (verify-section1 A4 #3, #12, #14, #18). Two outward-visible effects: (a) for a type whose owner field is spelled unlike its column, GetSchema (`SchemaCatalogReader.cs:121`) and the console (`AdminConsoleEndpoints.cs:138`) now report the column spelling; (b) a chunk point's owner key and the chunk search filter both use `OwnerField.ToCamelCase()` (`IntelligenceStoreConsumer.cs:298`, `ObjectSearchGrpcService.cs:218`), so for such a type the chunk points written before this change carry the old key and fail closed for owner-scoped SearchChunks until re-ingested. ForeignKey readers (`RelationValidator.cs:65-133`, `EntityRelationResolver.cs:150, 186`) use `Candidates`; OneToMany foreign keys are unchanged |
| 26 | 6 | No model the repo registers is refused by the new §2.4 checks | Navigation names in `Iverson.Api.Tests` are `Owner`, `Tags`, `Children`, `Author`, `Article`, `Tenant`, `WidgetId` and `AuthorId`. None matches another property ignoring case, apart from the two collision tests (`:1612-1651`, methods at `:1613` and `:1633`), which hit the collision check first. `SimpleType(...)` calls never repeat a name (grep). Conformance relations: `NamingRejectedScenario.cs:432, 471` (`Author`, `Tags`), `TenantRejectedScenario.cs:317` (`Owner`), `NavPropertyRejectedScenario.cs:41` (`Author`) |
| 27 | 6 | The StarRocks upsert now matches ordinally | Sole production caller: `EngagementStoreConsumer.cs:94`. Container tests send exact names (`TenantIsolationIntegrationTests.cs:37-47`, `StarRocksCredentialSplitIntegrationTests.cs:50-55`: `Id`/`Name` against `Id`/`Name`). Every `EntityEvent` producer emits exact column names (spec VA 33). A DLQ replay of an event from before this change loses its non-canonical columns in StarRocks, as Postgres already did (verify-section1 A5) |
| 28 | 6 | `WithOwnerValue`'s behaviour change | It is private, with one caller (`EngagementStoreConsumer.cs:85`). Existing tests `:273-389` stay green: forged-owner, the empty owner field, the missing authoritative owner (still omitted), and no owner field (payload byte-identical, because the branch isn't entered) |
| 29 | 6 | Constructors the new test files depend on | `CanonicalPayloadKeyRpcTests` constructs both write services, and `DeclaredNameNormalizationTests` constructs `SchemaRegistry`. A later task that changes these constructors must update these files with the existing classes (stated in Task 2's Interfaces) |
| 30 | 1 | `PopularitySignalEligibility.cs` and `PopularitySignalEligibilityTests.cs` do not exist, and no type or test class has a name containing `PopularitySignalEligibility` | `git grep -n "PopularitySignalEligibility"` returns nothing |
| 31 | 1 | The four production files and six test files Task 4 modifies exist at the cited lines | `ObjectSearchGrpcService.cs:264-274, 313, 459-481, 681-685, 985-998, 1046-1069`; `PopularitySignalConsumer.cs:25-29, 52-56`; `PopularitySignalOptions.cs:101-104, 123-127`; `ObjectSearchVectorIntegrationTests.cs:374, 423, 426`; `PopularitySignalReconciliationWorkerTests.cs:163`; test signatures at `ObjectSearchGrpcServiceTests.cs:2737, 2833, 2890, 2950, 3009, 3074, 4719, 4775` (`command grep -n`) |
| 32 | 2 | `PopularitySignalUpdater.UpdateAsync(SchemaDescriptor parentSchema, PopularitySignalEntry signal, SchemaDescriptor childSchema, SchemaRelationDescriptor relation, string parentKey, string? tenantId)` already receives the resolved child, so the gate needs no new dependency | `PopularitySignalConsumer.cs:52-54`; the alias `SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor` is at `:21` |
| 33 | 2 | `SchemaRegistry.Get(string)` returns `SchemaDescriptor?`, case-insensitive | `SchemaRegistry.cs:12, 16-17` |
| 34 | 2 | `AuthorizationRules(string? OwnerField, IReadOnlyList<RowPermission>, IReadOnlyList<FieldPermission>)`: the record normalises `""` to null; `RowPermission(Role, CanReadAll, CanWriteAll, CanDeleteAll)`; `FieldPermission(FieldName, ReadableRoles, WritableRoles)`; `RelationDescriptor(PropertyName, Kind, RelatedTypeName, ForeignKey)`; `SchemaDescriptor.PopularitySignalColumn` is `string?` | `SchemaDescriptor.cs:127-131, 145-174, 87` |
| 35 | 5 | The predicate restates the evaluator's read rules: `Authorization == null` → denied for all; bypass when any `groups` value equals a `RowPermission.Role` with `CanReadAll` (ordinal `HashSet`); otherwise an owner-scoped type admits any caller with `sub`; a field is excluded when a `FieldPermission` has a non-empty `ReadableRoles` disjoint from the groups, with names compared case-insensitively | `RowFieldAuthorizationEvaluator.cs:10-12, 36-43, 51-60, 66-80`; spec Verified assumption 8; verify-section1 A10 |
| 36 | 5 | The count is filtered on `relation.ForeignKey` and the histogram reads `childSchema.PopularitySignalColumn`, so these are the two child fields whose permissions matter | `PopularitySignalConsumer.cs:62, 111-116` |
| 37 | 5 | `ObjectSearchGrpcService` reaches `registry` (a primary-constructor parameter), `logger` and `_popularitySignal` from instance methods. `AuthorizationDecision` is in scope through `using Iverson.Api.Authorization` | `ObjectSearchGrpcService.cs:4, 32-53`; `registry` is already used in the instance method `RequireSchema` (`:1072`) |
| 38 | 5 | `decision` is in scope at both decay sites: SearchSimilar (`:161`), and `SearchChunksFusedAsync`'s parameter (`:637-639`), which serves SearchChunks (`:577`) and SearchSimilar-via-chunks (`:393`) | `ObjectSearchGrpcService.cs:161, 264, 393, 577, 637-639, 685` |
| 39 | 5 | `DecayFieldResolver.ResolveDecayField` returns `candidates[0].Name.ToCamelCase()`. `ToCamelCase` only lower-cases the first character, and `AllowedFields` is an `OrdinalIgnoreCase` set, so `AllowedFields.Contains(decayField)` matches the column | `DecayFieldResolver.cs:39-40, 64`; `NamingExtensions.cs:16-17`; `RowFieldAuthorizationEvaluator.cs:121` |
| 40 | 5 | With no centroid, no decay and no popularity, `ResultReranker` returns `BaseScore` exactly, so the decay tests' expected 0.80, 0.70 and 0.90 are exact | `Iverson.Vector/ResultReranker.cs:21-33` |
| 41 | 5 | With decay on, the decay tests would fail: the default `WDecay` is 0.10 and `WBase` 0.45 (`ObjectSearchGrpcServiceTests.cs:2821`). A one-year age gap therefore gives about 0.836 vs 0.699 (SearchSimilar), and the other paths diverge the same way | the arithmetic in `SearchSimilar_UsesConfiguredHalfLife_NotAHardCodedOne` (`:2820-2826`) |
| 42 | 5 | `EmbeddingOnlyWithDecaySchema` (collection `dated`), `DualAnnotatedWithDecaySchema` (`Doc`/`docs`), `StubNoCentroids`, `StubChunkDensity`, `ParentPayloads`, `RoutedSut`, `CapturedLimit`, `UnitVector`, `InvokeKeyToUlong` and `MakeStream` exist with the signatures the new tests use. The tenant-qualified collection names `dated_…`, `docs_…`, `docs_chunks_…` and `articles_…_test_tenant_dwsdnzpm8ad7tqdkswqfpfec1` are the ones existing tests stub | `ObjectSearchGrpcServiceTests.cs:2613-2629` (`EmbeddingOnlyWithDecaySchema`), `:2633` (`UnitVector`), `:2640` (`CapturedLimit`), `:3775` (`InvokeKeyToUlong`), `:3907-3958` (`RoutedSut`, `DualAnnotatedWithDecaySchema`, `StubChunkDensity`, `StubNoCentroids`), `:3963` (`ParentPayloads`) |
| 43 | 5 | In the test files, `RelationDescriptor`, `RelationKind`, `AuthorizationRules`, `RowPermission` and `FieldPermission` are ambiguous between `Iverson.Client.Contracts` and `Iverson.Api.Schema`, so the new code qualifies them as `Iverson.Api.Schema.*`. This applies to `ObjectSearchGrpcServiceTests`, `ObjectSearchVectorIntegrationTests`, `PopularitySignalConsumerTests` and `PopularitySignalReconciliationWorkerTests`. `PopularitySignalOptionsTests` and `PopularitySignalEligibilityTests` import only `Iverson.Api.Schema` | the proto message list (`git grep -h "^message \ | ^enum " -- '*.proto'`) includes `AuthorizationRules`, `FieldPermission`, `RelationDescriptor`, `RelationKind` and `RowPermission`; the existing tests already qualify them, e.g. `ObjectSearchGrpcServiceTests.cs:2605-2606`, `PopularitySignalConsumerTests.cs:20-21` |
| 44 | 5 | `SchemaRegistry.RegisterAsync` over a substituted repository only serialises the descriptor and caches it, with no validation, so the test helpers can register any shape | `SchemaRegistry.cs:228-237` |
| 45 | 5 | `ToCamelCase` (internal, namespace `Iverson.Api`) is in scope in `PopularitySignalOptions.cs` (namespace `Iverson.Api.Grpc`) | `NamingExtensions.cs:1-3`; the consumer uses it from `Iverson.Api.Consumers` (`PopularitySignalConsumer.cs:108`) |
| 46 | 5 | `StructSerializer.UpperFirst` stays resolvable, so mutation M6 compiles | it is used at `ObjectSearchGrpcService.cs:480` with the file's existing usings, which do not change |
| 47 | 3 | `--filter "FullyQualifiedName~.X."` with dots on both sides matches only class `X`; the new class name is unique | `git grep -n "PopularitySignalEligibilityTests"` is empty; the other filter classes already exist |
| 48 | 3 | The Qdrant suite has 7 facts | `ObjectSearchVectorIntegrationTests.cs` `[Fact]` at `:108, 149, 211, 324, 373, 422, 487` (`command grep -n`) |
| 49 | 4 | Tasks 1–3 do not edit any file Task 4 edits | brief file-ownership table: T1 SchemaBuilder/orchestrator/LoadAsync; T2 canonicaliser and write RPCs; T3 EngagementRepository and EngagementStoreConsumer |
| 50 | 4 | Tasks 10 and 11 edit `ObjectSearchGrpcService.cs` after this task, in other hunks (Unavailable mapping; Aggregate `Task.WhenAll` at `:752`) | brief table; Task 4 touches `:264-313`, `:459-481`, `:681-685` and `:985-1069` only |
| 51 | 6 | `UpdateAsync` now returns `Skipped` for an ineligible signal. Callers: `PopularitySignalConsumer.cs:249, 255` (outcome discarded) and `PopularitySignalReconciliationWorker.cs:75` (`Skipped` resets the failure streak, `:84-87`). The existing consumer and sweep fixtures carry no Authorization, so they stay eligible and unchanged | `PopularitySignalConsumerTests.cs:78-90, 101-112`; `PopularitySignalReconciliationWorkerTests.cs:42-66` |
| 52 | 6 | `ValidateAtStartup` can now throw (collision) and log (ineligible). Its only production caller is `Program.cs:726`. No shipped config sets `PopularitySignal` | `git grep -n "PopularitySignal" -- Iverson.Server/docker-compose.yml Iverson.Server/deploy` is empty. Existing `PopularitySignalOptionsTests` use `MinimalSchema` with `ScalarColumns = []` and no Authorization (`:20-37`), so neither change fires on them |
| 53 | 6 | `WriteSimilarResponsesAsync` drops non-column keys. Callers: `:335` (head path) and `:450` (via chunks). No SDK, console, agent or conformance code reads popularity or other non-column keys. Every key asserted after SearchSimilar in the server tests is a column | verify-section1 A14 (whole-repo search); keys asserted in `ObjectSearch*Tests` are `Body`, `Id`, `Name` and `Title` (`command grep` over `Data.Fields[` / `ContainKey`) |
| 54 | 6 | `PopularityFor` and `RetrievePopularityOrDegradeAsync` change signature, and each has one caller | `git grep`: `:313` and `:681` |
| 55 | 6 | Decay changes only for a caller whose `AllowedFields` excludes the decay column. The existing decay fixtures carry no FieldPermissions, so their tests are unchanged | `ObjectSearchGrpcServiceTests.cs:2626-2627` (`[]`), `:3940-3945` (built on `DualAnnotatedSchema`, `:2605-2606`, `[]`) |
| 56 | 6 | The 8 unit ranking tests configure `("Article", "Author")`, which is ineligible today: `ArticleSchema` declares `Author` ManyToOne and `Author` is not registered. They are re-fixtured, and their payload keys and expected scores are unchanged. The 2 Qdrant tests configure `("Article", "Comments")` with no such relation, and are re-fixtured too | `SchemaFixtures.cs:55-74`; verify-section1 A14; `ObjectSearchVectorIntegrationTests.cs:406, 473` |
| 57 | 6 | `SearchSimilar_UnconfiguredType_OrdersIdenticallyToBaseline` is unaffected: its signal list is empty | `ObjectSearchVectorIntegrationTests.cs:516-517` |
| 58 | 1 | `Iverson.Api/Schema/EnrichmentTargetRules.cs` does not exist and the name is unused | `git grep -n "EnrichmentTargetRules"` is empty |
| 59 | 2 | `ValidateEnrichmentTargets(TypeDescriptor, SchemaDescriptor)` is private, with one caller | `SchemaRegistrationOrchestrator.cs:176, 631` |
| 60 | 5 | Mapping `TypeDescriptor` → descriptor. `String` maps to `TEXT` and `String[]` to `TEXT[]`, and no other `ObjectType` does. Targets come from non-key properties only. `IsEmbedding`/`IsChunk` become `VectorFields`/`ChunkFields`. A template adds a chunk field named exactly `"Document"` | `SchemaBuilder.cs:60-93, 196-212, 392, 408` (type maps); verify-section1 A12 |
| 61 | 5 | The enrichment source text is `VectorFields ∪ ChunkFields` read from the row. The synthetic `Document` has no row value, so excluding it matches what is really read | `EnrichmentConsumer.cs:342-353` |
| 62 | 5 | `SchemaDescriptor.EnrichmentTargets` is `init`, so `descriptor with { EnrichmentTargets = [] }` compiles. `descriptor` is a local `SchemaDescriptor?`, known non-null inside `if (descriptor is not null)` | `SchemaDescriptor.cs:98`; `SchemaRegistry.cs:98, 138` |
| 63 | 5 | `EnrichmentConsumer` returns early for a type with no targets on both paths | `EnrichmentConsumer.cs:97` (HandleAsync) and `:266` (delete path) |
| 64 | 5 | `EscapeUntrustedText(string)` rewrites only `<<<`/`>>>`. `target.Hint` is `string?`, so `?? string.Empty` keeps a non-null argument, and `string.Format` already rendered null as empty. `ComputeHash` hashes `t.Hint` raw and is unchanged | `EnrichmentPrompts.cs:5-6, 19-23`; `SchemaDescriptor.cs:115`; `EnrichmentConsumer.cs:358-371` |
| 65 | 5 | `SanitizeForLog` (internal, namespace `Iverson.Api`) is in scope in `SchemaRegistry.cs` (namespace `Iverson.Api.Schema`) | `LoggingExtensions.cs:1-5`; used from `Iverson.Api.Schema` at `SchemaCatalogReader.cs:197` |
| 66 | 5 | The tests' tampered rows pass `LoadAsync`'s identifier/key-type check, so they reach the new block. `__TenantId` is exempt; `Summary`, `Tags` and `Missing` are plain identifiers; FieldPermissions and chunk names are not checked there | `SchemaRegistry.cs:173-202` |
| 67 | 5 | `DocumentTemplateParser.Parse` is public and round-trips through the registry's JSON | `DocumentTemplateParser.cs:23, 27`; `SchemaRegistryTests.cs:348-375` |
| 68 | 5 | `TypeDescriptor.DocumentTemplate`, `PropertyDescriptor.IsArray`, `IsKeywordsTarget` and `IsSummaryTarget` exist | `object_mapping.proto:49, 63-64, 107` |
| 69 | 5 | `EnrichmentConsumerTests.EnrichedArticle(string? extractHint)`, `CaptureHashAsync`, `BuildSut`, `Event` and `Key` exist as used | `EnrichmentConsumerTests.cs:87-115, 147-183` |
| 70 | 5 | `RecordingLogger` records the formatted message, so `Contains("'Article'")` sees the substituted type name | `SchemaRegistryTests.cs:774-786` |
| 71 | 3 | The Step 2 filter classes exist, and none is a substring-collision with a dot on each side | `SchemaRegistryTests`, `SchemaRegistrationOrchestratorTests` and `EnrichmentConsumerTests` (`git grep -n "class SchemaRegistryTests\ | class SchemaRegistrationOrchestratorTests\ | class EnrichmentConsumerTests"`) |
| 72 | 4 | Task 1 edits `SchemaRegistry.LoadAsync` (name normalisation) and the orchestrator (refusals) before this task. This task anchors on the identifier refusal block and on `ValidateEnrichmentTargets(typeDesc, descriptor);`/the method body, which Task 1 is not assigned to change | brief §2.2/§2.4 rows; spec §2.2 ("`descriptor with { … }`") |
| 73 | 4 | Task 7 later re-points orchestrator tests to the queued path. Validation stays in the api (§5.2), so the template-only test still throws before anything is queued | spec §5.2 "Validation and descriptor building stay in the api" |
| 74 | 6 | `ValidateEnrichmentTargets` keeps throwing `InvalidArgument` with the same messages, except the non-string rule's tail ("it is 'Int32'" → "its column type is 'INTEGER'"). No test or client matches that tail | `git grep -n "must be a string property"` finds only the orchestrator; `RegisterAsync_WithNonStringEnrichmentTarget_ThrowsInvalidArgument` asserts the status code only (`SchemaRegistrationOrchestratorTests.cs:1127-1140`). The FieldPermission tests assert `"cannot be enforced"`, `"a subset of them"` and `"Notes"`, all kept (`:1255, 1279-1384`) |
| 75 | 6 | `LoadAsync` may now load a descriptor with its targets removed. Callers: `Program.cs:724` (startup), `SchemaRefreshWorker.cs:28` (30 s poll) and `GetOrReloadAsync` (`SchemaRegistry.cs:56`). No existing test loads a target-bearing descriptor through `LoadAsync` | `git grep -n "EnrichmentTargets" -- '*.cs'` outside `Iverson.Api/`: only `EnrichmentConsumerTests`, `IntelligenceStoreConsumerTests` and `ObjectMappingGrpcServiceTests`, which all register through `RegisterAsync`. `LegacyArticleRowJson` has no tenant column and is refused first (`SchemaRegistry.cs:143-152`) |
| 76 | 6 | The extraction prompt changes only for hints containing `<<<` or `>>>`. The existing prompt test's hint has neither | `EnrichmentConsumerTests.cs:87, 311` |
| 77 | 6 | The error log repeats on every 30 s poll while a bad row stays stored, as the existing relation-collision log does | `SchemaRegistry.cs:160-171` (same per-load pattern) |
| 78 | 1 | `SchemaRegistryRepository` has `EnsureTableAsync`, `LoadAllAsync`, `UpsertAsync`, `DeleteAsync` and ends with `DeleteAsync`'s two lines and `}` | `Iverson.Sql/SchemaRegistryRepository.cs:5-35` |
| 79 | 1 | `ISchemaRegistryRepository` is the 4-member block quoted in Task 7 3e | `Iverson.Sql/IRecordStoreRoles.cs:191-197` |
| 80 | 1 | `IDlqRepository.GetUnreplayedByIdAsync` is one line, unique in the file | `IRecordStoreRoles.cs:226`; replay count 1 |
| 81 | 1 | `AddPostgres` ends with the `ISchemaRegistryRepository` registration then `return services;`, and has `connectionString` in scope | `Iverson.Sql/ServiceCollectionExtensions.cs:8,28-31` |
| 82 | 1 | `SchemaRegistry`'s JSON options are `private static readonly JsonSerializerOptions s_jsonOptions` at the end of the class; no member named `JsonOptions` exists | `Iverson.Api/Schema/SchemaRegistry.cs:247-251`; `git grep -n "JsonOptions" -- Iverson.Server/Iverson.Api/Schema/` finds only `s_jsonOptions` |
| 83 | 1 | The orchestrator's constructor, owner pre-check, phase-2 comment and phase 3 read exactly as quoted | `Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:14-20, 86-89, 281-282, 324-350`; LF line endings (`cat -A`) |
| 84 | 1 | `schemaManager` and `rerenderQueue` are used only in phase 3, so removing them from the constructor leaves no dangling reference | `command grep -n "schemaManager\ | rerenderQueue\ | SchemaDrift" SchemaRegistrationOrchestrator.cs` → `:15,:18,:331,:333,:348` only |
| 85 | 1 | `Program.cs` anchors: the orchestrator registration (`:377`), the worker block's last line `…DocumentRerenderQueueWorker>();` + `}` (`:478-479`), the `TenantSchema` apply (`:752`), the reconcile route's `ReconciliationService` parameter (`:601`) and body (`:615-620`), the replay route's signature (`:659`) and body (`:685-695`); each unique | Read `Program.cs:371-386, 466-480, 596-697, 746-756`; replay counts 1 |
| 86 | 1 | The four `new SchemaRegistrationOrchestrator(` sites are the only constructions | `git grep -n "new SchemaRegistrationOrchestrator" -- '*.cs'` → `ObjectMappingGrpcServiceTests.cs:91`, `RegisterSchemaAuthorizationIntegrationTests.cs:226`, `SchemaRegistrationOrchestratorTests.cs:36`, `DocumentTemplateValidationTests.cs:37` |
| 87 | 1 | `ISchemaRegistryRepository` has exactly three implementations; the two test fakes end with `UpsertAsync`/`DeleteAsync`/`}` | `git grep -n "ISchemaRegistryRepository" -- '*.cs'`: `SchemaRegistryRepository`, `NoOpSchemaRegistryRepository` (`StartupNoOpFakes.cs:27-36`), `AdminConsoleSchemaRegistryRepository` (`AdminConsoleTestWebApplicationFactory.cs:97`, ends at its `DeleteAsync`); the rest are `Substitute.For` |
| 88 | 1 | `IDlqRepository` has exactly two implementations | `git grep`: `DlqRepository`, `NoOpDlqRepository` (`StartupNoOpFakes.cs:95-102`); the rest are substitutes |
| 89 | 1 | None of the new type or file names exists | `git grep -n "PrivilegedRequest\ | SchemaRegistrationExecutor\ | SchemaRegistrationApplier\ | ExistingTable\ | AdvisoryLock\ | PrivilegedAdminEndpoint\ | SchemaRegistryPostgresRoundTrip" -- '*.cs'` finds nothing; `ls Iverson.Api`/`Iverson.Api.Tests` show no `PrivilegedRequests` folder |
| 90 | 1 | New test classes are not substrings of existing class names and their `~.Name.` filters match only themselves | Same grep as #12; filters are dot-delimited |
| 91 | 1 | `ContainerCollection.Name` exists in `Iverson.Api.Tests`, and container classes join it | `Iverson.Api.Tests/ContainerCollection.cs:23-27`; `DocumentRerenderQueuePostgresIntegrationTests.cs:44-46` |
| 92 | 1 | The Postgres-only Testcontainers fixture pattern (`postgres:18-alpine`, `EnsureRolesAsync`) | `DocumentRerenderQueuePostgresIntegrationTests.cs:17-41`; `RegisterSchemaAuthorizationIntegrationTests.cs:38-40,83` |
| 93 | 1 | `SchemaFixtures.AuthorSchema()` exists and loads (UUID key "uuid", tenant column `TenantId`) | `Iverson.Api.Tests/Helpers/SchemaFixtures.cs:39-52`; `SchemaRegistry.LoadAsync`'s checks `:143-208` accept it (it is already loaded through `LoadAsync` in `SchemaRegistryTests.cs:45-53`) |
| 94 | 1 | `ReconciliationService` is constructible in tests from substitutes | `Reconciliation/ReconciliationService.cs:15-20`; `ReconciliationServiceTests.cs:22-36` |
| 95 | 1 | The replay route's three DLQ calls in `InactiveTenantHttpEndpointTests` are single lines, each unique | `InactiveTenantHttpEndpointTests.cs:61,193,208`; replay counts 1 |
| 96 | 1 | The reconcile pipeline test's tail is the quoted block, unique | `AuthenticationPipelineTests.cs:144-158`; it contains the only `HttpStatusCode.NotFound` in the file (`command grep -c` → 1) |
| 97 | 1 | `DlqRepositoryTests` has `MarkReplayedAsync_SetsReplayedTrueById`, and tests DLQ SQL with a substituted executor | `Iverson.Sql.Tests/DlqRepositoryTests.cs:42-65` |
| 98 | 2 | `IRecordStoreQueryExecutor.QueryAsync/ExecuteAsync/QuerySingleOrDefaultAsync(sql, param, role, tenantId)`; `RecordStoreRole.Maintenance` runs the statement under `SET LOCAL ROLE iverson_maintenance` | `IRecordStoreRoles.cs:48-53`; `PostgresRepository.cs:135-160` |
| 99 | 2 | `IRecordStoreSchemaManager.ApplySchemaAsync(TableSchema, SchemaDriftPolicy)` throws `SchemaDriftException(table, column, actual, expected)` under `Throw`, whose message contains "Migrate the column by hand" | `IRecordStoreRoles.cs:63-68`; `PostgresSchemaManager.cs:113-118` |
| 100 | 2 | `IDocumentRerenderQueueRepository.EnqueueTypeAsync(string)` | `IRecordStoreRoles.cs:212` |
| 101 | 2 | `IEventProducer.ProduceAsync(string topic, string key, string message)` (non-generic overload chosen for string arguments) | `Iverson.Events/IEventProducer.cs:5-6`; today's route calls it with three strings (`Program.cs:691`) |
| 102 | 2 | `IDlqRepository.GetUnreplayedByIdAsync` returns `DlqReplayRow(SourceTopic, MessageKey, MessageValue, TenantId)`; `MarkReplayedAsync(Guid)` | `DlqRow.cs:7`; `DlqRepository.cs:43-55` |
| 103 | 2 | `ReconciliationService.ReconcileTypeAsync(string, CancellationToken)` returns null for an unregistered type, resolves through `registry.Get`, and on cancellation leaves its page loop and returns the partial count | `ReconciliationService.cs:25-72` (`:27-28`, `:41`, `:71`) |
| 104 | 2 | `SchemaRegistry.LoadAsync(CancellationToken)` and `RegisterAsync(SchemaDescriptor)`; `LoadAsync` never observes the token itself | `SchemaRegistry.cs:67-226, 228-237` |
| 105 | 2 | `SchemaBuilder.BuildDescriptor(TypeDescriptor, IEmbeddingService)`, `ToTableSchema`, `ToTableName` are internal static, visible to `Iverson.Api.Tests` | `SchemaBuilder.cs:17,30,32,271`; `Iverson.Api.csproj:9-11` (`InternalsVisibleTo Iverson.Api.Tests`) and the `DynamicProxyGenAssembly2` entry (`:12-14`) for substitutes of internal interfaces |
| 106 | 2 | The proto members used by Task 6 exist: `TypeDescriptor.Description/DocumentTemplate/DocumentMaxTokens/DocumentOverlap/DocumentContextual/Authorization`, `PropertyDescriptor.IsSearchKey/SearchKeyOrder/IsChunk/ChunkMaxTokens/ChunkOverlap/ChunkContextual/IsEmbedding/IsSummaryTarget/IsMetadata/IsArray/IsPopularitySignal/IsNullable/Description`, `FieldPermission.FieldName/ReadableRoles`, `RowPermission.Role/CanReadAll/CanWriteAll`, `ObjectType.Datetime` | `Iverson.Clients/Common/Proto/object_mapping.proto:32-111`; `SchemaBuilder.cs:110,397` |
| 107 | 2 | `ConsumerResilience.RunWithRestartAsync(Func<Task>, ILogger, string, CancellationToken)` swallows the shutdown `OperationCanceledException` and returns | `Consumers/ConsumerResilience.cs:12-47` |
| 108 | 2 | `ObjectMappingGrpcService.RegisterSchema` reads the owner from the acting user and calls `ISchemaRegistrationOrchestrator.RegisterAsync(request, ownerTenantId, ct)`, whose signature is unchanged | `ObjectMappingGrpcService.cs:52-53`; `SchemaRegistrationOrchestrator.cs:9-12` |
| 109 | 2 | `AuditLog.AdminOperation(ClaimsPrincipal, string, string?)` | `Grpc/AuditLog.cs:19-22` |
| 110 | 2 | `PostgresIdentifier.Quote(string)` refuses `"` and NUL | `Iverson.Sql/PostgresIdentifier.cs:31-38` |
| 111 | 2 | `KeyedTenantRow(Key, TenantId)` and `EntityAccess` are the types of `FetchKeysAndTenantsPagedAsync` | `KeyedRow.cs:5`; `IRecordStoreRoles.cs:171` |
| 112 | 3 | Every bash block either `cd`s to the worktree or its `Iverson.Server`; heavy runs are under `systemd-run --user --scope -q -p MemoryMax=6G` with `MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false` exported (a `--scope` run inherits the caller's environment) | Brief "Session conventions"; template plan `:954,968` |
| 113 | 3 | `dotnet test … --filter "Category!=Integration"` excludes the container classes, which carry `[Trait("Category", "Integration")]` | Template plan `:1074`; every container class in `Iverson.Api.Tests` carries the trait (e.g. `RegisterSchemaAuthorizationIntegrationTests.cs:198`) |
| 114 | 3 | The expected counts: Task 6 1; Task 7 unit 26 (11 + 6 + 5 + 4), container 20, plus 2 for the round-trip and authorization integration classes; Task 8 executor 10, endpoints 7 | Counted `[Fact]`s in `D3.md` per class |
| 115 | 4 | Task 6 runs before Task 7, which reuses the registry's JSON options for its payload (spec §9 "before the worker work") | Spec `:277` |
| 116 | 4 | Tasks 1 and 5 do not change Task 6's fixture: its declared names equal their columns and its one enrichment target is `TEXT` (Task 5's predicate) | Task 6 fixture type; spec §2.2, §4 |
| 117 | 4 | Task 7 edits orchestrator regions Tasks 1 and 5 do not: the constructor, the 4-line owner pre-check, two phase-2 comment lines and phase 3 | Brief's file table (T1: refusals, T5: `ValidateEnrichmentTargets`); spec §2.2, §2.4, §4 |
| 118 | 4 | Task 8 consumes Task 7's types and files and quotes them as Task 7 leaves them | Scratch replay applied Task 8's 20 edits after Task 7's: all matched once |
| 119 | 4 | Task 9 gates the startup block (which then holds Task 7's new `ApplySchemaAsync` line) to the worker, and grants the api `INSERT, SELECT, UPDATE ("Status")` on `IversonPrivilegedRequests`; the api's statements need no more | `PrivilegedRequestRepository`: api calls `InsertAsync`, `GetAsync`, `ExpireIfPendingAsync` (UPDATE of `"Status"` only, with `SELECT` columns in `WHERE`); spec §6.2 row `IversonPrivilegedRequests` |
| 120 | 4 | Task 10 inserts its own plumbing table and worker services after Task 7 and must quote `Program.cs` as Task 7 leaves it | Task 7 **Interfaces** |
| 121 | 4 | The test files Task 6 and Task 7 create call `EnsureTableAsync` themselves, so they stay green after Task 9 takes it out of `SchemaRegistry.LoadAsync` | Spec §6.3; `SchemaRegistryPostgresRoundTripTests` and `PrivilegedRequestPostgresFixture` |
| 122 | 5 | .NET 10 APIs used: `Guid.CreateVersion7()`, `Stopwatch.GetTimestamp/GetElapsedTime`, `Task.WaitAsync(TimeSpan)`, `CancellationTokenSource.CancelAsync()`, primary constructors, collection expressions, `KeyValuePair` deconstruction in `foreach` | `TargetFramework net10.0` (`Iverson.Api.csproj:4`, `Iverson.Sql.csproj:4`); `Guid.CreateVersion7` already used at `DlqRepository.cs:19`; `CancelAsync` at `ProjectionWaiterTests.cs:122`; `WaitAsync(TimeSpan)` at `MatchPatternGrpcServiceTests.cs:532` |
| 123 | 5 | Dapper 2.1.89 materialises a positional record when the selected columns match its constructor parameters in order, by name (case-insensitive) and type; every new `SELECT` names its columns in record order | `Iverson.Sql.csproj:10`; existing `DlqRow`/`DlqReplayRow`/`TenantRow` reads (`DlqRepository.cs:31-50`, `TenantRepository.cs:24-31`) rely on the same; `PrivilegedRequestRow` (Id, Kind, Payload, Status, ResultJson), `ExistingTable` (IsOrdinaryTable, HasTenantColumn), `DlqReplayCheckRow` (SourceTopic, TenantId) |
| 124 | 5 | Npgsql 10.0.3 reads `jsonb` as `string` and accepts `@p::jsonb` from a string parameter | `Iverson.Sql.csproj:11`; `SchemaRegistryRepository.cs:17-18,26` already does both |
| 125 | 5 | Postgres resolves `now() + @d * interval '1 second'` for a `float8` parameter (`float8 * interval`), and a NULL parameter yields NULL | Postgres operator `*(double precision, interval)`; NULL propagation; [PG behaviour, not repo-verified]. The container tests `APendingRequestPastItsExpiry…` and `DeleteFinishedOlderThan…` exercise both arms |
| 126 | 5 | `pg_advisory_lock(bigint)`/`pg_advisory_unlock(bigint)` session locks; a bigint key shows its high half in `pg_locks.classid` with `objsubid = 1`; `hashtext(..)::bigint` keys are sign-extended `int4` | `PostgresSchemaManager.cs:59-60,220-221` (the existing session-lock pattern); `StartupDdl.cs:20-21`; pg_locks layout is [PG behaviour, not repo-verified] and is what `TheLockKeys_LieOutsideTheRangeOfTheHashtextTableLocks` checks |
| 127 | 5 | `INSERT … ON CONFLICT (type_name) DO UPDATE … WHERE <cond>` affects 0 rows when the condition is false, and `->>` yields NULL for an absent or JSON-null key | verify-section23.md B4; container tests `UpsertIfOwner_*` |
| 128 | 5 | The claim/expiry race resolves with exactly one winner | Spec Verified assumption 17 (probed on Postgres 18) |
| 129 | 5 | NSubstitute 6.2.0: `Arg.Is(value)`, `Arg.Is<T>(expr)`, `ReceivedCalls()` + `GetMethodInfo()/GetArguments()`, `Received.InOrder` (checks only calls matching its specs), `Returns(a, b)` sequences, `Returns(Func<CallInfo,T>)`, `.AndDoes(...)`, `ThrowsAsync` | `Iverson.Api.Tests.csproj:17`; repo usage: `ThrowsAsync` `TenantAdminGrpcServiceTests.cs:224`, `Received.InOrder` and `ReceivedCalls().…GetMethodInfo()` `EngagementStoreConsumerKafkaOrderingTests.cs:118-128`; `AndDoes` is NSubstitute's `ConfiguredCall.AndDoes` (not used in the repo yet). `Arg.Is((string?)null)` is cast so it does not bind ambiguously to the expression overload |
| 130 | 5 | FluentAssertions 8.11: `BeEquivalentTo` compares records member-wise (not by `Equals`), `WithStrictOrdering`, `OnlyContain`, `Equal(params)`, `ThrowAsync<T>()` → `.Which`/`.Where` | `Iverson.Api.Tests.csproj:18`; member-wise record comparison relied on at `RegisterSchemaAuthorizationIntegrationTests.cs:282-287`; `WithStrictOrdering` `PatternPartitionBatcherTests.cs:64`; `OnlyContain` `AdminConsoleDataVolumeEndpointTests.cs:104` |
| 131 | 5 | System.Text.Json deserialises the positional records `RegisterSchemaPayload`, `PrivilegedRequestResult`, `ReconcileTypePayload`, `ReplayDlqPayload` with the camelCase options, and serialises `Grpc.Core.StatusCode` as a number | Same options already round-trip the positional `SchemaDescriptor` members (`DocumentSegment`, `RowPermission`, …) per verify-section23.md B2; enums default to numbers |
| 132 | 5 | `Results.Json(data, statusCode:)` and `StatusCodes.Status202Accepted`/`Status500InternalServerError` | `Program.cs:590` uses `Results.Json(…, statusCode: 503)`; `StatusCodes.*` used at `Program.cs:113,501` |
| 133 | 5 | In `Program.cs` (global namespace) `global::Grpc.Core.StatusCode` is needed because `using Iverson.Api.Grpc;` is in scope; in test namespaces under `Iverson.Api.Tests` a `Grpc` prefix would resolve to `Iverson.Api.Tests.Grpc`, so tests use `using Grpc.Core;` at file level, an alias, or `global::` | `Program.cs:11`; `Iverson.Api.Tests/Grpc/*` namespace `Iverson.Api.Tests.Grpc` |
| 134 | 5 | No top-level local in `Program.cs` is named `requestId`, `reconcileRequest`, `reconcileResult`, `replayRequest`, `replayResult`, `privileged` or `status` | `command grep -nE "^(var) " Program.cs` (after replay) lists none of them |
| 135 | 5 | No project sets `TreatWarningsAsErrors` or `GenerateDocumentationFile`, so an unused `using` left in `Program.cs` (`Iverson.Events`) is harmless | `command grep` over `Iverson.Server/*/*.csproj` and `Directory.Build.*`: none |
| 136 | 6 | `SchemaRegistrationOrchestrator`'s constructor changes; its callers are DI (`Program.cs:377`) and the four test sites of #9, all updated | #9 |
| 137 | 6 | `ISchemaRegistryRepository` gains two members; the three implementations of #10 are updated, every other use is `Substitute.For`, which needs no change | #10 |
| 138 | 6 | `IDlqRepository` gains one member; both implementations (#11) are updated; `InactiveTenantHttpEndpointTests` re-points its three replay-lookup lines to the new member, otherwise its `DidNotReceive` would pass vacuously | #11, #18 |
| 139 | 6 | `SchemaRegistry.RegisterAsync` loses its only production caller (the orchestrator's phase 3); it stays, used by tests and by the in-process test executor | `git grep -n "RegisterAsync(" -- 'Iverson.Server/*.cs' ':!*Tests*'` → orchestrator `:339` only |
| 140 | 6 | `RegisterSchema` can now answer `Unavailable` (worker never claimed) and `DeadlineExceeded` (claimed, unfinished after 10 s). No SDK sets a deadline or special-cases these; every registrar surfaces the RPC error | verify-section23.md B10 (`SchemaRegistrar.cs:33-39`, `registrar.go:68`, `SchemaRegistrar.java:73`, `core.py:187,201`, `core.ts:206-221`) |
| 141 | 6 | A write right after registration on the same api replica still sees the type (the api reloads before answering); other replicas catch up on their 30 s poll, as today | `QueuedSchemaRegistrationExecutor`; `SchemaRefreshWorker.cs:14,27-28` |
| 142 | 6 | `/admin/reconcile` can now answer 202 and no longer stops when the client disconnects; `/admin/dlq/{id}/replay` can answer 202. No client in the repo calls either route programmatically | `git grep -n "admin/reconcile\ | dlq/.*replay"` over `*.ts *.tsx *.js *.py *.go *.java *.sh`: none; the runbook calls reconcile by hand with `curl` (`docs/runbooks/chunk-collection-cleanup-2026-07.md:29`); round-8 plan `:39` "No client anywhere in the repo depends on `/admin/reconcile`'s synchronous response shape" |
| 143 | 6 | `AuthenticationPipelineTests.ServiceTokenAndActingUserToken_PostAdminReconcile_Succeeds` expected 404 from the in-process reconcile; with the worker answering, its host needs a faked request queue to stay a gate test | `AuthenticationPipelineTests.cs:136-158`; `AuthTestWebApplicationFactory` registers no worker and no request-queue fake |
| 144 | 6 | `RegisterSchemaAuthorizationIntegrationTests` relied on `SchemaRegistry.RegisterAsync` creating `_iverson_schema`; with the applier writing through the repository, the test creates the table first | `SchemaRegistry.cs:230`; Task 7 1h |
| 145 | 6 | `AddPostgres` now also registers `IExistingTableInspector` and `IPrivilegedRequestLocks` in both roles; only the worker resolves them | `ServiceCollectionExtensions.cs`; the applier and executor are registered worker-only (Task 7 3s) |
| 146 | 6 | The api's request-path behaviour for other routes is unchanged: the new services are resolved only by RegisterSchema and the two admin routes | Task 7 3s, Task 8 3e–3f |
| 147 | 1 | The ten modified files exist at the stated paths | `ls` / `cat -n` of each. `Program.cs` has 950 lines, and every file is UTF-8 with LF line endings (`file`). |
| 148 | 1 | The six created files and their type names do not exist yet | `git grep -n "ApiStartupReadiness\ | ApiLoginGrants\ | TenantlessDecision\ | SchemaRegistryStartup\ | WorkerStoreBootstrap\ | EnsureApiLogin\ | EnsureCanEnterRuntimeRole\ | HasLoaded\ | ApiLoginName"` gives no hits. |
| 149 | 3 | The new test class names collide with no filter | Each is filtered as `FullyQualifiedName~.<Class>.`. None of the four new class names is a substring of an existing class name, nor contains one: `SchemaRegistryStartupTests` does not contain `.SchemaRegistryTests.`, and `ApiStartupReadinessPipelineTests` does not contain `ReadinessPolicyTests`. |
| 150 | 2 | `IRecordStoreSchemaManager` has exactly two members today, and its implementers are `PostgresSchemaManager` and `NoOpRecordStoreSchemaManager`; everything else substitutes it | `IRecordStoreRoles.cs:66-77`. `git grep IRecordStoreSchemaManager`: `PostgresSchemaManager.cs:10`, `StartupNoOpFakes.cs:63`. `ObjectMappingGrpcServiceTests.cs:70`, `SchemaRegistrationOrchestratorTests.cs:18`, `DocumentTemplateValidationTests.cs:22` and `PostgresSchemaManagerTests.cs:13` use `Substitute.For`, which implements new members automatically. |
| 151 | 2 | `EnsureCanEnterRoleAsync(NpgsqlConnection, string)` is a private static helper whose 42501 message reads `GRANT {roleName} TO {conn.UserName};` | `PostgresSchemaManager.cs:283-306`. The existing test pins that format: `PostgresIntegrationTests.cs:491`. |
| 152 | 2 | `EnsureRoleAsync` runs its existence query as `QuerySingleOrDefaultAsync<bool>` without a transaction, so `RoleExistsAsync` is behaviour-preserving | `PostgresSchemaManager.cs:308-312`. |
| 153 | 5 | The Dapper calls compile with a transaction argument in third position | Existing calls use the same shape: `conn.ExecuteAsync($"SET LOCAL ROLE {roleName}", null, tx)` (`PostgresSchemaManager.cs:289`) and `conn.QuerySingleOrDefaultAsync<T>(sql, param, tx)` (`PostgresRepository.cs:93`). Dapper is 2.1.89 (`Iverson.Sql.csproj`). |
| 154 | 5 | `ArgumentNullException.ThrowIfNull` exists and makes `tenantId` non-null by flow | .NET 6+ BCL with `[NotNull]`. Projects target `net10.0` (`Iverson.Sql.csproj`, `Iverson.Api.csproj`). |
| 155 | 2 | `SchemaRegistry` has `IsRegistered`, `LoadAsync`'s first statement is `await repository.EnsureTableAsync();`, and its last statement is the `logger.LogInformation("SchemaRegistry loaded …")` line | `SchemaRegistry.cs:19`, `:67-71`, `:225-226`. |
| 156 | 6 | Removing `EnsureTableAsync` from `LoadAsync` breaks no caller | Callers of `LoadAsync`: `Program.cs:724`, `SchemaRefreshWorker.cs:28`, `SchemaRegistry.cs:56` (`GetOrReloadAsync`). Every real-Postgres test creates `_iverson_schema` through `RegisterAsync` before any load: `RegisterSchemaAuthorizationIntegrationTests.cs:257,277`, `UpdateCarryForwardPostgresIntegrationTests.cs:54`, `ReconciliationQueuePostgresIntegrationTests.cs:83`. `RegisterAsync` keeps its own `EnsureTableAsync` (`SchemaRegistry.cs:230`). No test asserts that `LoadAsync` calls `EnsureTableAsync` (`git grep EnsureTableAsync -- '*Tests*'`). |
| 157 | 6 | Adding `HasLoaded` changes no existing behaviour | It is a new read-only property set at the end of a successful `LoadAsync`. Its only reader is `/health` (this task). |
| 158 | 6 | `OutboxWriter.UpsertAndEnqueueOutboxAsync` now refuses a null tenant, and every production caller passes a non-null one | The callers are `ObjectMappingGrpcService.cs:167,238` and `ObjectPersistenceGrpcService.cs:63,140`, each passing `decision.TenantValue` from a non-denied decision. The evaluator's only non-denied return carries a non-empty tenant (`RowFieldAuthorizationEvaluator.cs:29-33,125-126`; spec Verified assumption 21). Test callers: `OutboxWriterTests.cs:32,48,83,196` (re-pointed in Step 1) and `TenantScopedAccessIntegrationTests.cs:272` (passes `tenantId: "tenant-a"`). `EnrichmentConsumerTests.cs:244` is a `DidNotReceiveWithAnyArgs` on a substitute. |
| 159 | 6 | The unconditional `EnterTenantScopeAsync` and `ExitRoleScopeAsync` keep the tenant path's statement order | `OutboxWriterTests.cs:55-73` pins 5 calls in order for the tenant path. That path is unchanged, since the conditionals were true there. |
| 160 | 6 | The GetMany and Delete refusals are reachable only through a stub evaluator; the real paths still read and delete under `ForTenant(decision.TenantValue)` | `ObjectRetrievalGrpcService.cs:98-104` (Denied return before `:112`); `ObjectMappingGrpcService.cs:315-333` (Denied return before `:338`). `ObjectRetrievalGrpcServiceTests.cs:135-150` asserts `EntityAccess.ForTenant("test-tenant")` for GetMany and still holds. |
| 161 | 2 | `AuthorizationDecision` is a 7-argument positional record; `IRowFieldAuthorizationEvaluator.Evaluate(SchemaDescriptor, ClaimsPrincipal?, AuthorizationAction)` | `IRowFieldAuthorizationEvaluator.cs:8-14,16-59`. |
| 162 | 2 | The `ObjectMappingGrpcService` constructor order and types | `ObjectMappingGrpcService.cs:21-36`. The all-substitute construction in `TenantlessDecisionRefusalTests` copies `RegisterSchemaAuthorizationIntegrationTests.cs:233-248`, which compiles today. |
| 163 | 2 | The `ObjectRetrievalGrpcService` constructor | `ObjectRetrievalGrpcService.cs:15-21`; constructed the same way at `ObjectRetrievalGrpcServiceTests.cs:42-48`. |
| 164 | 2 | `MappingDeleteRequest{TypeName,Key}`, `RetrievalManyRequest{TypeName,Keys}`, `IServerStreamWriter<RetrievalResponse>` | `ObjectMappingGrpcService.cs:284-295`; `ObjectRetrievalGrpcService.cs:75-81`; `ObjectRetrievalGrpcServiceTests.cs:143-145`. |
| 165 | 2 | `SchemaFixtures.AuthorSchema()`, `ActingUserFixtures.Principal`, `TestServerCallContext.Create()` | `SchemaFixtures.cs:39`; `ActingUserFixtures.cs:11-12` (tenant `test-tenant`); `TestServerCallContext.cs:26`. |
| 166 | 2 | `IActingUserAccessor`, `ActingUserAccessor` (namespace `Iverson.Api`), `AuditLog(ILogger<AuditLog>)`, `IOutboxPublisher`, `IRelationValidator`, `IPayloadSizeValidator`, `IEntityKeyAccessor`, `IEntityRelationResolver`, `ISchemaRegistrationOrchestrator` (namespace `Iverson.Api.Grpc`), `EngagementQueryLimitOptions.Default` (`Iverson.StarRocks`) | `git grep` of each declaration: `IActingUserAccessor.cs:5,10`; `AuditLog.cs:5`; `OutboxPublisher.cs:7`; `RelationValidator.cs:7`; `PayloadSizeValidator.cs:10`; `EntityKeyAccessor.cs:6`; `EntityRelationResolver.cs:12`; `SchemaRegistrationOrchestrator.cs:9`. |
| 167 | 2 | `ReconciliationSchema`, `DlqSchema` and `TenantSchema` are `internal static` with `const TableName` and a `Table`; Iverson.Api grants `InternalsVisibleTo` to `Iverson.Api.Tests` | `ReconciliationSchema.cs:29-45`, `DlqSchema.cs:10-29`, `TenantSchema.cs:5-17`; `Iverson.Api.csproj:10-12`. |
| 168 | 5 | The DLQ grant names every `DlqSchema` column except `MessageValue` | `DlqSchema.cs:15-28` lists `Id` and `SourceTopic, ConsumerGroup, MessageKey, MessageValue, ExceptionType, ExceptionMessage, Attempts, FailedAt, Replayed, TenantId`. The grant has those 10 minus `MessageValue`. The api's list query reads exactly those 10 (`DlqRepository.cs:31-41`). |
| 169 | 5 | The outbox grant covers every api outbox statement | Insert: `OutboxWriter.cs:36-42` (7 columns) and `:115-119` (9 columns with `EventType`, `Payload`). Table-level `INSERT` covers both. Delete by `Id`: `:104-110`, which needs `DELETE` and `SELECT ("Id")`. |
| 170 | 5 | The tenant grant covers every api tenant statement | `TenantRepository.cs:7-46`: INSERT, SELECT, UPDATE `Status` WHERE `Id`/`Status`, DELETE WHERE `Id`. All are table-level. `SeedIfMissingAsync` is now worker-only (Step 5h). |
| 171 | 5 | The revocation grant covers the upsert and the list | `TokenRevocationRepository.cs:21-38`: `INSERT … ON CONFLICT (sub) DO UPDATE SET revoked_at = EXCLUDED…` and `SELECT sub, revoked_at`. Spec Verified assumption 20 probed the upsert with table grants. |
| 172 | 5 | The registry grant covers the load | `SchemaRegistryRepository.cs:15-20` (`SELECT type_name, schema_json`). Upsert and Delete are not granted (registration goes through the worker, spec §5). |
| 173 | 5 | `CONNECT` via `DO … EXECUTE format('… %I …', current_database())` works for the database owner | Postgres behaviour: plpgsql `DO` is available to every user. Only the owner, or a superuser, may grant `CONNECT`. `iverson` is the initdb owner on CNPG (`store-credentials-map.md` §1, `cluster.yaml:23-26`) and the superuser on compose (`docker-compose.yml:38-41`). **Not run here**; the integration test's `connect` assertion exercises it (Step 6). |
| 174 | 5 | A login with no `CREATE` on `public` is refused `CREATE TABLE`, and a non-owner is refused `ALTER TABLE`, both with 42501 | Postgres ≥ 15 revokes `CREATE` on `public` from PUBLIC. The test image is `postgres:18-alpine` (as in `PostgresIntegrationTests.cs:12`), and compose uses `postgres:18.6` (`docker-compose.yml:35`). CNPG's default image for 1.30 is not pinned in `cluster.yaml` (**not repo-verified**). `ALTER TABLE` needs ownership. Exercised by `Ddl_IsDenied`. |
| 175 | 5 | `has_table_privilege(text, text)` checks `current_user`, and raises 42P01 for a missing table | Postgres function semantics (**not repo-verified**). Exercised by `ReadinessProbe_IsFalseForALoginWithoutTheGrants_AndTrueForTheApiLogin` (false and true). `/health` maps a faulted task to false through the existing `ContinueWith(t => t.IsCompletedSuccessfully && …)` shape (`Program.cs:568`). |
| 176 | 5 | `pg_has_role(name, name, 'MEMBER')` is portable to every supported version | Postgres docs. The existing comment at `PostgresSchemaManager.cs:276-280` explains why the runtime probe uses `SET LOCAL ROLE` rather than this lookup. Here it only decides whether to issue `GRANT`; the api's own probe still uses `SET LOCAL ROLE`. |
| 177 | 5 | `format('CREATE ROLE iverson_api LOGIN PASSWORD %L', @Password::text)` yields a correctly quoted statement | `format`'s `%L` is `quote_literal`. Exercised by the fixture (which creates the login with a password and logs in with it) and by the restore in test 14. |
| 178 | 5 | Concurrent GRANTs on one table can fail with XX000, and a transaction-scoped advisory lock serialises them | `PostgresSchemaManager.cs:46-49` documents the XX000 race on GRANT. `StartupDdl.cs:12-21` uses `pg_advisory_xact_lock(hashtext(…)::bigint)` for the same purpose. The key `iverson_api_login` is a new constant. |
| 179 | 4 | CNPG creates managed roles asynchronously, so the worker can start before `iverson_api` exists | `job-revoke-cross-db.yaml:60-67` polls for `authentik` for that reason. Spec Verified assumption 34 covers `iverson_runtime` existing first. |
| 180 | 4 | The api can start before the worker on Helm, which has no ordering between Deployments | `verify-section23.md` C6.4: Helm has no ordering between the two Deployments. Spec §8 only reverses compose. |
| 181 | 2 | `WebApplicationFactory` settings from `UseSetting` are visible to Program.cs's configuration reads, both before and after `Build()` | `PreAuthPartitionPipelineTests.cs:36` sets `RateLimiting:TrustedProxies:Cidrs:0`, which Program.cs reads before `Build()` (`Program.cs:42`). `AuthTestWebApplicationFactory.cs:54` sets `Authentication:ConsoleAudience`, read after `Build()` (`Program.cs:247`). So `Tenancy:SeedLegacyTenants` (read after `Build()`) is visible to the startup test. |
| 182 | 2 | The test host runs Program.cs in the api role | `Program.cs:37` defaults `workloadRole` to `"api"`, and no test sets `WORKLOAD_ROLE` (`git grep WORKLOAD_ROLE -- '*Tests*'` gives no hits). |
| 183 | 6 | Every `WebApplicationFactory<Program>` host still boots, because the api branch calls only `EnsureCanEnterRuntimeRoleAsync` and `LoadAsync` | `AuthTestWebApplicationFactory.cs:58-80` swaps in the no-op schema manager and registry repository. The no-op gains the two members (Step 5d). `AdminConsoleTestWebApplicationFactory.cs:77-78` swaps its own registry repository, whose `LoadAllAsync` returns a completed task, so the api's synchronous first load still populates the registry before the first request. |
| 184 | 6 | `/health`'s new `QuerySingleOrDefaultAsync<bool>` call would make `GetHealth_TwoRequestsWithinCacheWindow_OnlyInvokesDependenciesOnce` answer 503 unless stubbed | NSubstitute's auto-value for `Task<bool>` is `false`. `AuthenticationPipelineTests.cs:362-381` asserts 200 twice. Step 3 stubs it to `true` and counts it once. `GetHealth_StarRocksAuthPending_IsReportedAsFalse` (`:384-406`) asserts only the `starrocks` field, so it needs no change. No other test calls `/health` (`git grep '"/health"' -- '*Tests*'` finds only that file). |
| 185 | 6 | The `/health` body gains `apiGrants` and `registry`. This is additive, and the response keeps its existing keys | `Program.cs:577-583`. `git grep -n "checks" -- '*AdminUI*'` finds no reader of the Postgres field names. |
| 186 | 6 | `PopularitySignalValidator.ValidateAtStartup` tolerates an empty registry in the api | `PopularitySignalOptions.cs:59-69` and `:89-97`: an unregistered `ParentType` logs a warning and is skipped. The configuration-only checks (`:76-87`) still throw. |
| 187 | 6 | Moving the legacy-tenant seed to the worker keeps compose seeding | `docker-compose.yml:640` already sets `Tenancy__SeedLegacyTenants=true` on `iverson-worker`, as well as on the api (`:527`). On Helm it is set only on the api Deployment (`charts/api/templates/deployment.yaml:106-107`); Task 12 moves it to the worker (spec §8). |
| 188 | 6 | Removing the api's DDL leaves no other DDL in the api process | The api-role DDL sites are `Program.cs:724` (via `LoadAsync`), `:734`, `:739`, `:743`, `:749-752`, `:763-764` (`store-credentials-map.md` §3), and `SchemaRegistrationOrchestrator.cs:331` (RegisterSchema), which Task 7 moves to the worker. `SchemaRefreshWorker` (`Program.cs:485`) now loads only. |
| 189 | 3 | The commands use the worktree path, the memory cap and the MSBuild env | Brief "Session conventions". `dotnet test --filter "Category!=Integration"` is the established non-container filter (template plan, Task 1 Step 4). |
| 190 | 5 | FluentAssertions 8 and NSubstitute 6 APIs used | Both test projects reference FA 8.11.0 and NSubstitute 6.2.0 (`Iverson.Api.Tests.csproj:16-17`, `Iverson.Sql.Tests.csproj:15-16`). The APIs are already in use: `ThrowsAsync` from `NSubstitute.ExceptionExtensions` (`TenantAdminGrpcServiceTests.cs:224`); `.Should().ThrowAsync<T>()` then `.Which` (`PostgresIntegrationTests.cs:485-493`); `DidNotReceiveWithAnyArgs` (`EnrichmentConsumerTests.cs:243-244`); `Contain(predicate).Which` (FA 8 `AndWhichConstraint`). |
| 191 | 5 | Dapper maps a value tuple positionally, and `long` from `count(*)` | `SchemaRegistryRepository.cs:17` maps `(string type_name, string schema_json)`; `TokenRevocationRepository.cs:35` maps `(string sub, DateTime revoked_at)`. |
| 192 | 4 | Task 7's `IversonPrivilegedRequests` table exists in the worker's startup before Task 9's grants run | **Contract, not verified** (Task 7 is drafted in parallel). Spec §5.1 says "The worker creates it through `ApplySchemaAsync`". See the rebase rule in Interfaces. If the table is missing, the fixture's grant step fails with 42P01, and every case of `ApiLoginGrantsPostgresIntegrationTests` fails at fixture init. |
| 193 | 4 | Task 10 runs after Task 9 and appends its grant and its expected privilege | Brief task order 1→13. The anchors are given in Interfaces. |
| 194 | 6 | `IRowFieldAuthorizationEvaluator`'s doc paragraph (`:49-56`) describes the two ternaries this task removes | `IRowFieldAuthorizationEvaluator.cs:49-56`. The `TenantColumn is not null` sites are listed by `git grep` (`ObjectRetrievalGrpcService.cs:48,118,134`, `ObjectMappingGrpcService.cs:120,313,346`, `EntityRelationResolver.cs:135`, `AuthorizationFieldMasking.cs:127,148,167`). |
| 195 | 1 | The files Task 10 and Task 11 modify exist at the stated paths | `ls`: `Iverson.StarRocks/{EngagementRepository,TenantIdentifier,ServiceCollectionExtensions,StarRocksResiliencePipelineFactory}.cs`, `Iverson.Api/{Program.cs,appsettings.Development.json,Grpc/ObjectSearchGrpcService.cs}`, and the eight test files named in the Files blocks |
| 196 | 1 | The 8 created files do not exist, and no type is already named `IProvisionedTenantDirectory`, `InMemoryProvisionedTenantDirectory`, `StarRocksTenantSchema`, `PostgresProvisionedTenantDirectory` or `TenantPoolSize`, and no table `IversonStarRocksTenants` exists | `git grep -n "TenantLogin\ | ProvisionedTenant\ | StarRocksTenantSchema\ | IversonStarRocksTenants\ | TenantPoolSize"` finds only an unrelated local and a test name (`MatchRowsIntegrationTests.cs:362,364`, `TenantIsolationIntegrationTests.cs:150`) |
| 197 | 1 | The new test class names collide with no existing class under the `~.Name.` filter | The class list of `Iverson.StarRocks.Tests` (grep `class …Tests`) has `EngagementRepositoryTests` and `StarRocksIntegrationTests`, and neither equals `.EngagementRepositoryTenantLoginTests.` or `.StarRocksTenantLoginIntegrationTests.` with dots on both sides. `git grep` for `PostgresProvisionedTenantDirectoryTests` and `ProvisionedTenantDirectoryPostgresIntegrationTests` finds nothing |
| 198 | 1 | `Iverson.Api.Tests/Tenancy/` exists, uses namespace `Iverson.Api.Tests.Tenancy`, and imports `Iverson.Api.Tenancy` | `TenantStatusCacheTests.cs:1-6`; `AuthentikContainerFixture.cs:12` |
| 199 | 2 | `EngagementRepository` is a primary-constructor class whose last parameter is `string? provisionerConnectionString = null`, so two more optional trailing parameters keep every caller compiling | `EngagementRepository.cs:15-21`. The callers (`git grep "new EngagementRepository("`) are `Api.Tests/StarRocks/StarRocksReadinessIntegrationTests.cs:67`, `EngagementRepositoryProvisioningTests.cs:81`, `MatchRowsIntegrationTests.cs:30,336,361`, `StarRocksCredentialSplitIntegrationTests.cs:43,160`, `StarRocksIntegrationTests.cs:81`, `StarRocksRepositorySearchTests.cs:12`, `StarRocksRepositoryTests.cs:125`, `TenantIsolationIntegrationTests.cs:33` and `ServiceCollectionExtensions.cs:28`. All are positional with at most 4 arguments, or named |
| 200 | 2 | A static method parameter may share a name with a primary-constructor parameter (`TenantConnectionString(…, string tenantLoginKey)`) | Existing precedent: `CheckBackendAliveAsync(string connectionString, CancellationToken ct)` (`EngagementRepository.cs:763`) shadows primary parameter `connectionString` |
| 201 | 2 | `TenantIdentifier.Fingerprint` is a private static in the same class, so `LoginName` can call it; `System.Security.Cryptography` and `System.Text` are already imported | `TenantIdentifier.cs:1-2, 95-114` |
| 202 | 2 | `MySqlConnectionStringBuilder` has `UserID`, `Password`, `Database` (string), and `MaximumPoolSize`, `ConnectionIdleTimeout` and `ConnectionTimeout` (`uint`) | Decompiled `MySqlConnectionStringBuilder` (2.6.2): `public string UserID` (:67), `Password` (:87), `Database` (:107), `public uint ConnectionIdleTimeout` (:517), `MaximumPoolSize` (:555), `ConnectionTimeout` (:747) |
| 203 | 2 | `OpenProvisionerConnectionAsync(Func<Task>)` keeps its signature, so its 5 references compile | `EngagementRepository.cs:125`. `git grep -n OpenProvisionerConnectionAsync` gives 5 hits: the definition, the call at `:515`, and three in `EngagementRepositoryProvisioningTests.cs` |
| 204 | 2 | `AddStarRocks` ends with `string? provisionerConnectionString = null`, and its factory lambda has `sp` in scope for `GetService` | `ServiceCollectionExtensions.cs:8-14, 25-30` |
| 205 | 2 | `IRecordStoreQueryExecutor.QuerySingleOrDefaultAsync<T>(sql, param, role = Connection, tenantId = null)` and `ExecuteAsync(…)` exist, and `PostgresRepository(string, ILogger<PostgresRepository>)` implements them | `Iverson.Sql/IRecordStoreRoles.cs:48-53`; `PostgresRepository.cs:8-10` |
| 206 | 2 | `TableSchema(TableName, KeyColumn, Columns, TenantColumn = null)` and `ColumnSchema(Name, SqlType, IsNullable)`. `ApplySchemaAsync` creates the key column as `PRIMARY KEY` and grants only `iverson_maintenance` when `TenantColumn` is null | `IRecordStoreRoles.cs:258-264`; `PostgresSchemaManager.cs:81, 172, 174` |
| 207 | 2 | `PostgresSchemaManager(string, ILogger<PostgresSchemaManager>)` is public | `PostgresSchemaManager.cs:8-10` |
| 208 | 2 | `ContainerCollection.Name` is `"containers"` in namespace `Iverson.Api.Tests`; Postgres fixtures use `PostgreSqlBuilder().WithImage("postgres:18-alpine")`, and `Testcontainers.PostgreSql` 4.15.0 is referenced | `Api.Tests/ContainerCollection.cs`; `Sql.Tests/TokenRevocationRepositoryPostgresIntegrationTests.cs:17-19`; `Iverson.Api.Tests.csproj:23` |
| 209 | 2 | `StarRocksCollection.Name`, `StarRocksContainerFixture.ConnectionString` / `.Repository`, and `StarRocksSplitLogins.EnsureAsync`, which returns `(App, Provisioner)` with `Database = ""` | `StarRocksIntegrationTests.cs:60-81, 209-210`; `StarRocksSplitLogins.cs:33-58` |
| 210 | 2 | `MatchRowsRequest` takes 9 positional arguments in the order used; `AggregationDescriptor(Name, Kind, Field, …)`; `AuthorizationConstraint(…, TenantValue:)` | `MatchRowsRequest.cs:11-20`; `Aggregation.cs:15-24`; `TenantIsolationIntegrationTests.cs:52-53` |
| 211 | 2 | `ObjectSearchGrpcServiceTests` has `_registry`, `_search`, `_sut`, `MakeStream<T>()`, `OwnedSchema(string, string?, …)`, `SchemaFixtures.AuthorSchema()`, the `SrAggResult` alias, and `TestServerCallContext.Create()` | `ObjectSearchGrpcServiceTests.cs:19, 23-80, 82-89, 190-207`; `Api.Tests/Helpers/TestServerCallContext.cs:5-7` |
| 212 | 2 | `ObjectSearchGrpcService.Aggregate` builds `aggTasks` with an unbounded `Select(... RunAggregationAsync(... ProtoToEngagementSpec(spec) ...))` and then `Task.WhenAll`. `ProtoToEngagementSpec` can throw (`ArgumentOutOfRangeException` from `ProtoKindToSr`), hence the up-front conversion | `ObjectSearchGrpcService.cs:741-752, 1204-1229` |
| 213 | 2 | `RunAggregationAsync(SchemaDescriptor, SearchQuery?, EngagementAggSpec, SearchQuery?, IReadOnlyList<JoinSpec>?, IReadOnlyDictionary<…>?)` returns `Task<EngagementAggResult?>` | `ObjectSearchGrpcService.cs:760-777` |
| 214 | 2 | Both resilience strategies use the same predicate, `Handle<MySqlException>(ex => ex.IsTransient)`. The breaker is outer and the retry inner (builder order) | `StarRocksResiliencePipelineFactory.cs:13-40` (`:19` breaker, `:37` retry) |
| 215 | 5 | MySqlConnector 2.6.2 raises a full pool as `new MySqlException(MySqlErrorCode.UnableToConnectToHost, "Connect Timeout expired." + " All pooled connections are in use.")`, and `UnableToConnectToHost` is transient | Decompiled `MySqlConnection.CreateSessionAsync`: `string text = ((pool?.IsEmpty ?? false) ? " All pooled connections are in use." : ""); throw new MySqlException(MySqlErrorCode.UnableToConnectToHost, "Connect Timeout expired." + text);`. The pooled `pool.GetSessionAsync` call is inside the same `try`. Decompiled `MySqlException.IsErrorTransient`: `ConnectionCountError`, `UnableToConnectToHost`, `LockWaitTimeout`, `LockDeadlock`, `XARBDeadlock` |
| 216 | 5 | `MySqlException`'s 4-argument internal constructor `(MySqlErrorCode, string?, string, Exception?)` exists, for the reflection helper | Decompiled `MySqlException` :102; same helper at `StarRocksResiliencePipelineFactoryTests.cs:24-31` |
| 217 | 5 | `Convert.ToHexStringLower` (.NET 9+) and `HMACSHA256.HashData(byte[], byte[])` (.NET 6+) are available | Every project targets `net10.0` (`Iverson.StarRocks.csproj`, `Iverson.StarRocks.Tests.csproj`) |
| 218 | 5 | The pinned vectors are right: `LoginName("acme") = iverson_t_7pfddyte668561tz83en42m5m` (35 characters), and `LoginPassword("acme", "test-tenant-login-key") = 401cfbf7…c30c1aa3` | Computed in Python with `TenantIdentifier.Fingerprint`'s algorithm (SHA-256, first 16 bytes big-endian, 25 base-36 digits from the low end; `TenantIdentifier.cs:95-114`) and `hmac.new(b'test-tenant-login-key', b'starrocks-tenant:acme', sha256)`. The test also asserts `RoleName("acme")` ends with the same digits, which pins the Python algorithm against the C# one |
| 219 | 5 | StarRocks accepts `CREATE USER IF NOT EXISTS '<35 chars>'@'%' IDENTIFIED BY '<64 hex>' DEFAULT ROLE '<role>'` and `ALTER USER … SET PROPERTIES (…)` from `iverson_provisioner` with `user_admin` active, and the role is active at login | Spec Verified assumption 24 (probed on 4.1.1 and 4.1.6). The test image is `starrocks/allin1-ubuntu:4.1.1` (`StarRocksIntegrationTests.cs:23`) |
| 220 | 5 | `'iverson_t_…'@'%'` in a provisioning statement trips neither MySqlConnector's parameter parsing nor `StarRocksParameterGuard` | The existing `GRANT … TO USER 'iverson_app'@'%'` runs through `conn.ExecuteAsync` (`EngagementRepository.cs:523, 531`); the guard's regex `(?<!@)@(?!@)(\w+)` (`StarRocksParameterGuard.cs:24-25`) excludes `@'` by design (comment `:21-23`) |
| 221 | 5 | `SELECT @@query_timeout` / `@@exec_mem_limit` read the user-level session properties at login; with `ConnectionReset=false`, they and `SET query_timeout = 7` persist across a pooled checkout | Spec Verified assumptions 24 and 26. Probed at plan time on `starrocks/allin1-ubuntu:4.1.1` with MySqlConnector 2.6.2 (`csr12-server/resetprobe/probe3.cs`): a fresh login reads 180 / 4294967296; with `ConnectionReset=true` the same physical connection (one `CONNECTION_ID()`) reads 300 / 2147483648 on its next checkout; with `ConnectionReset=false` it reads 7 / 4294967296 after `SET query_timeout = 7`. The first replay of this plan, without `ConnectionReset=false`, failed both tests that read the caps |
| 222 | 5 | Dapper's `ExecuteReaderAsync` on a `SET` statement yields a reader whose first `ReadAsync` is false | The MySqlConnector/ADO.NET contract for a statement with no result set; existing `StreamTenantScopedAsync` reads with `ReadAsync` until false (`EngagementRepository.cs:376`) |
| 223 | 5 | NSubstitute's `Returns(T, params T[])` works on a `Task<T>` member; `CallInfo.ArgAt<T>(int)`; `DynamicProxyGenAssembly2` can see internals of `Iverson.Api` | Existing `_sql.ExecuteAsync(…).Returns(0)` (`ObjectSearchGrpcServiceTests.cs:38`); NSubstitute 6.2.0 (`Iverson.StarRocks.Tests.csproj`); `Iverson.Api.csproj` grants `InternalsVisibleTo` to `Iverson.Api.Tests` and `DynamicProxyGenAssembly2` |
| 224 | 3 | `--filter "FullyQualifiedName~.X."` selects exactly the named class; `FullyQualifiedName!~IntegrationTests` excludes every StarRocks container class; `Category!=Integration` excludes the Api container classes | Plan header convention; template plan Task 3 Step 4 uses `!~IntegrationTests` for the StarRocks project; the new container classes carry `[Trait("Category", "Integration")]` |
| 225 | 3 | `systemd-run --user --scope` passes the exported environment (`MSBUILDDISABLENODEREUSE`, `UseSharedCompilation`) to `dotnet` | A transient scope runs the command as a child of the caller (`--scope` semantics), the pattern the plan header prescribes |
| 226 | 4 | Task 10 runs after Task 3 (which edits only `UpsertAsync` in `EngagementRepository.cs`) and Task 9 (role step and startup gating); Task 11 runs after Task 10 | Brief's task table; no hunk of Task 10 overlaps `UpsertAsync` (`:440-480`) |
| 227 | 4 | The table exists before the api's grant names it, and `iverson_api` exists before that grant runs | D4's `WorkerStoreBootstrap.RunAsync` (D4.md Step 5b) runs the `ApplySchemaAsync` lines (this task's insert follows `TenantSchema`) and only then `EnsureApiLoginAsync(…, ApiLoginGrants.Statements, …)`, which ensures the login (waiting for CNPG, or creating it on compose) before granting. D4 **Interfaces**, "Produces for Task 10", names the three anchors this task uses |
| 228 | 4 | Every `WebApplicationFactory` host in `Iverson.Api.Tests` boots in Development and reads `appsettings.Development.json`, so the new key guard needs the Development literal | `Iverson.Api.csproj` comment ("committed … so Iverson.Api.Tests can load it under Mvc.Testing's Development environment"); `git grep "UseEnvironment"` in Api.Tests finds nothing; `git grep "ConnectionStrings:StarRocks"` in Api.Tests finds nothing (the hosts take the StarRocks string from the Development file) |
| 229 | 6 | `EngagementRepository` constructor gains two optional trailing parameters | The 12 call sites in row 5 compile unchanged. Five construct repositories that reach tenant-scoped paths, and Task 10 passes them the key and directory: `TenantIsolationIntegrationTests.cs:33`, `MatchRowsIntegrationTests.cs:30` (its `:336,361` tests are replaced), `StarRocksCredentialSplitIntegrationTests.cs:43`, `StarRocksRepositoryTests.cs:125`, `EngagementRepositoryProvisioningTests.cs:81`. The last refuses on the provisioner check first, which is unchanged |
| 230 | 6 | `AddStarRocks` gains `tenantLoginKey` and resolves `IProvisionedTenantDirectory` | Callers: `Program.cs:283` (passes the key; registers the directory) and `ServiceCollectionExtensionsTests.cs:19,36,62,81`. `:81`'s test provisions and is updated; the other three only resolve services. `GetService` returns null when none is registered |
| 231 | 6 | The readers (`SearchAsync`, `AggregateAsync`, `GroupByAsync`, `PipelineAsync`, `MatchRowsAsync`) now answer an unrecorded tenant as empty or null without connecting, which is the same shape callers already handle for an unprovisioned tenant | Production callers: `ObjectSearchGrpcService.cs:98-108, 742-751, 825-831, 906-912`; `ObjectSearchGrpcService.MatchPattern.cs:408`; `AggregateReader.cs:108` (null → 0, `:82-89`); `PopularitySignalConsumer.cs:80,117` (null → `Skipped`, pinned "never Failed", `PopularitySignalConsumerTests.cs:628-629`). The same null and empty values as today's role arm (`verify-section45.md` D11) |
| 232 | 6 | A refused tenant login now surfaces as `EngagementNotReadyException` | gRPC: mapped to `Unavailable` at `ObjectSearchGrpcService.cs:114-116, 789-791, 837-839, 918-920` and `.MatchPattern.cs:171-173`. Consumers: `TransientFailures.cs:48` treats it as transient (halt and redeliver). Console data volume: `AdminConsoleDataVolumeEndpointTests.cs:70` already pins its handling of the exception |
| 233 | 6 | `DeleteAsync` returns early for an unrecorded tenant | Sole production caller: `EngagementStoreConsumer.cs:112` (tenant from the snapshot). Before, the same event failed `SET ROLE` with ParseError, which is non-transient (`TransientFailures.cs:39-42`), and dead-lettered. Now it is a no-op. Api.Tests substitute `IEngagementStoreEntityStore` |
| 234 | 6 | `EnsureTenantProvisionedAsync` also creates the tenant login, stops granting the role to `iverson_app`, records the tenant, and refuses without a key or directory | Sole production caller: `EngagementStoreConsumer.cs:71` (worker; `Program.cs` hands the worker both through `AddStarRocks`). Test callers: the three integration classes (updated) and `ServiceCollectionExtensionsTests.cs:90` (updated) |
| 235 | 6 | `IsExpectedMissingResourceError` loses its role arm | 13 references, all in `EngagementRepository.cs` (`git grep`): the five readers' `when` clauses and telemetry arguments, `MatchRowsAsync`, and `RewrapIfBackendUnavailableAsync` (`:90`). Tests that pinned the arm: `EngagementRepositoryLivenessRewrapTests.cs:166-167` (re-pointed); `TenantIsolationIntegrationTests.cs:95-140` and `MatchRowsIntegrationTests.cs:229` (now empty through the directory instead); `TransientFailuresTests.cs:82-83` (classifies `TransientFailures`, unchanged) |
| 236 | 6 | `RunTenantScopedAsync`, `StreamTenantScopedAsync` and `ReleaseAsync` no longer run `SET ROLE` or `SET ROLE NONE` | Private and internal; callers are the readers, `UpsertAsync`, `DeleteAsync`, and the tests at `MatchRowsIntegrationTests.cs:239, 292, 320-370` and `StarRocksRepositoryTests.cs:131-132`. The two tests whose premise was `SET ROLE` are re-pointed |
| 237 | 6 | The Aggregate RPC runs at most 4 specs at once; results stay in request order; a bad spec still fails before any runs | Clients see the same response. Tests: `ObjectSearchGrpcServiceTests.cs:590-990` (Aggregate family) and `Grpc/OwnerPrivateTypeVisibilityTests.cs` (Aggregate via `_sut`); neither depends on concurrency except `Aggregate_WithMultipleSpecs_QueriesAllConcurrently`, which only counts calls (3) |
| 238 | 6 | The breaker and the retry stop counting and retrying a full pool | Sole caller: `EngagementRepository.cs:30-32`. A full pool still reaches `RunAsync`'s catch as a transient `MySqlException` (no liveness rewrap, `:90`) and is rethrown, as today. `TransientFailures` (consumers) still says transient. Tests: `StarRocksResiliencePipelineFactoryTests.cs` (5 existing, unchanged) |
| 239 | 6 | `Program.cs` startup now also requires `StarRocks:TenantKey` when engagement is on, and the worker bootstrap creates and grants the new table | Both roles run the same `Program.cs` (`Program.cs:37` `workloadRole`). Compose and Helm do not set the key until Task 12 (D6 renders `StarRocks__TenantKey`); between Task 10 and Task 12 on the branch they would fail to start, as D4 already accepts for the worker. No live run happens before Task 13. `ApiStartupTestFactory` (D4) asserts that the api role never calls `ApplySchemaAsync`; the new line is inside `WorkerStoreBootstrap.RunAsync`, which only the worker runs |
| 240 | 6 | Task 9's `ApiLogin_HoldsExactlyTheListedPrivileges` asserts the api login's complete table-privilege list | D4.md, `ApiLoginGrantsPostgresIntegrationTests`: `tablePrivileges.Should().BeEquivalentTo(...)`. Task 10 adds `"IversonStarRocksTenants:SELECT"` at D4's published anchor, so the exact-list test keeps passing and now proves the grant |
| 241 | 1 | The seven modified chart, compose and script files exist at the stated paths, and the two new chart templates do not | `ls charts/*/templates` lists `postgres/templates/cluster.yaml` only (no `secret-api.yaml`), and `starrocks/templates/` has no `secret-tenant-key.yaml`; `cat -n` read every quoted hunk (cluster.yaml 60 lines, job-create-user.yaml 138, api deployment.yaml 354, worker deployment.yaml 202, job-revoke-cross-db.yaml 93, docker-compose.yml 697, generate-compose-secrets.sh 64) |
| 242 | 1 | `docs/runbooks/csr12-server.md` does not exist, and `docs/runbooks/` is **tracked, not gitignored**, so it is staged without `-f` (the brief's "docs need `git add -f`" holds only for the six ignored subdirectories) | `ls docs/runbooks` has no csr12 file; `git check-ignore -v docs/runbooks/csr12-server.md` exits 1; `.gitignore:46-51` ignores only `docs/{superpowers,criticalreviews,performance,plans,reviews,specs}`; `git ls-files docs/runbooks` lists the existing runbooks |
| 243 | 1 | `charts/*.tgz` written by `helm dependency build` are gitignored | `.gitignore:76` `Iverson.Server/deploy/helm/**/charts/*.tgz` |
| 244 | 1 | Only the api and worker Deployments, the Authentik hook and compose reference `<release>-postgres-app` / `POSTGRES_APP_PASSWORD` / `ConnectionStrings__Postgres` | `git grep -n "postgres-app\ | ConnectionStrings__Postgres"` and `git grep -n POSTGRES_APP_PASSWORD` (non-md): `charts/api/templates/deployment.yaml:84-94`, `charts/worker/templates/deployment.yaml:67-77`, `charts/authentik/templates/job-revoke-cross-db.yaml:42,82`, `docker-compose.yml:480,608` |
| 245 | 1 | Under `-p csr12`, with `container_name:` stripped, the containers are `csr12-postgres-1`, `csr12-starrocks-1`, `csr12-starrocks-init-1`, `csr12-iverson-api-1` and `csr12-iverson-worker-1`, and the `image: iverson-api` line occurs twice | Compose service keys at `docker-compose.yml:34,56,94,467,590`; `image: iverson-api` at the api (`:471`) and the worker (`:594`); the round-11 precedent used the same sed (`2026-10-08-…-server-authorization-implementation-plan.md` Task 11 Step 5) |
| 246 | 1 | The api runtime image is Ubuntu, with `bash`, `head` and `grep`, so the worker healthcheck runs | `Iverson.Api/Dockerfile:37` `FROM mcr.microsoft.com/dotnet/aspnet:10.0…`, `:50` `USER ubuntu`; the existing api healthcheck already runs `bash` with `/dev/tcp` (`docker-compose.yml:550`) |
| 247 | 1 | The job images (`mysql:26.7.0` on the chart, `mysql:8.0.46` on compose) have `sh`, `sed` and `tr` | `charts/starrocks/values.yaml:21`; `podman history mysql:26.7.0` shows the image's own build running `sed`, `grep`, `mktemp` and `uname` (coreutils and sed present); the current job already runs `wc -l` in it (`job-create-user.yaml:93`); compose's `starrocks-init` (`:96`) is the same image family |
| 248 | 1 | The Task 13 test projects | `ls -d Iverson.Server/*.Tests Iverson.Clients/DotNet/*.Tests`: Api, ClientConformance, Embeddings, Events, LoadTest, Patterns, Sql, StarRocks, Vector; Client.Core, Client.Search |
| 249 | 1 | The kind scripts build from the tree their own copy sits in, so a `git archive` of main in `$LIVE/main-src` builds main | `deploy/kind/build-and-load-image.sh:63` `REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"` |
| 250 | 1 | The bypass user and group names, and its tenant | `charts/authentik/blueprints/compose-only/service-clients.yaml:295` (group `iverson-loadtest-bypass`), `:304-312` (user `iverson-loadtest-bypass-user`, `tenant_id: "tenant_bypass"`); smoke-test user `:284-292` (`tenant_smoke_test`) |
| 251 | 1 | The live cluster and stack start clean | `KIND_EXPERIMENTAL_PROVIDER=podman kind get clusters` → `No kind clusters found` at plan time; Task 13 Step 3 re-checks and stops if anything runs |
| 252 | 2 | The job's `fe()` passes its arguments through to `mysql` with root's login, so `fe --batch --skip-column-names -e …` works | `job-create-user.yaml:72-75` |
| 253 | 2 | CNPG managed-role fields: `name`, `ensure`, `login`, `passwordSecret.name` (already used), plus `inRoles` | `cluster.yaml:48-54`; spec VA18 (CNPG 1.30.0 supports `inRoles` and a basic-auth `passwordSecret`); a basic-auth Secret's `username` must equal the role name, as `secret-postgres.yaml:14` does for `authentik` |
| 254 | 2 | `iverson_runtime` exists before CNPG reconciles `inRoles` | `cluster.yaml:43-47` `postInitApplicationSQL`; spec VA34 |
| 255 | 2 | The worker serves `GET /health` over plain HTTP/1.1 on 8081 in compose, returning 200 when ready and 503 otherwise | `appsettings.json` `Kestrel:Endpoints:Http` `http://*:8081`, `Http1`; compose sets no Kestrel override; `Program.cs:552-583` (`AllowAnonymous`, `RequireListenerPort(8081)`, 503 when `!readiness.Ready`) |
| 256 | 2 | The seed key is `Tenancy:SeedLegacyTenants`; the StarRocks string is required only when `Engagement:Enabled` | `Program.cs:754`; `Program.cs:283-288` |
| 257 | 2 | Plumbing names used in the live SQL | `ReconciliationSchema.cs:29,43` (`IversonReconciliationQueue`, `Payload`); `DlqSchema.cs:12,22` (`IversonDlqMessages`, `MessageValue`); `TenantSchema.cs:7,11` (`IversonTenants`, key `Id`); `SchemaRegistryRepository.cs:8` (`_iverson_schema`). `IversonStarRocksTenants`/`TenantId` are Task 10's (spec §7.3), so the assembler must confirm D5's quoting |
| 258 | 2 | Hashed role names are `role_tenant_<readable>_<25 base-36>`, and legacy ones are `role_tenant_<tenantId>`. Both are within `[A-Za-z0-9_]`, because StarRocks refuses a hyphen in a role name | `TenantIdentifier.cs:23,63-70`, and the doc comment `:41-46,56-61` |
| 259 | 2 | The probe console's contract types | `object_mapping.proto:9-15` (service), `:44` PropertyDescriptor, `:77-94` RowPermission/AuthorizationRules (`role`, `can_read_all`, `owner_field`), `:96` TypeDescriptor, `:113` SchemaRequest, `:174-197` MappingGet/Write/Response; `object_search.proto:9-26` (`Search`, `SearchRequest.type_name`, `page_size`); `tenant_lifecycle.proto:7-25,56`; `ActingUserInterceptor.cs:12` header `x-acting-user-authorization` |
| 260 | 2 | The acting-user mint script reads the password from `IVERSON_ACTING_USER_PASSWORD`, not argv, and caches TOTP under `~/.cache/iverson` | `deploy/scripts/mint_acting_user_token.py:43-56,87-89`, `--username` at `:454`. The round-11 precedent's `--password` flag no longer exists, so Task 13 sets `HOME=$LIVE/home` |
| 261 | 2 | The conformance harness environment | `Iverson.ClientConformance/Program.cs:313` (gRPC URL default), `:336-347` (the five variables, and the loopback rule) |
| 262 | 3 | Local tool versions | `helm version` v3.16.4, `kube-score version` 1.19.0, `kubeconform -v` v0.8.0, `tfsec --version` v1.28.6; `tfsec --help` lists `--no-colour` |
| 263 | 3 | CI's chart commands, which `t12_ci.sh` mirrors | `.github/workflows/deploy-validate.yml:40` (helm lint per overlay, with `ci-override`), `:76-77` (template, kubeconform 1.30.0 `-ignore-missing-schemas`), `:121-125` (kube-score with three `--ignore-test` flags), `:143` (tfsec) |
| 264 | 3 | Task 12 Steps 2, 4 and 5 print what their Expected blocks say | Plan-time replay. **Step 2:** five `FAIL`s with the listed messages; `criticals: 0` everywhere; Valid 92/68/99/99/99; `[job:*] FAIL exit=0 revokes=[]`; `[compose] FAIL` with all but the two holding checks. **Step 4:** five `PASS`es; Valid 94/69/101/101/101; `criticals changed: 0`; `[job:roles] PASS` (two revokes), `[job:none] PASS`, `[job:badname] PASS exit=1`; `[compose] PASS`. **Step 5:** M1–M10 each print the named failure |
| 265 | 3 | No Task 12 or Task 13 bash block executes during the dafny-plan-verification replay | `replay.classify` on every block. Task 12 Steps 1 and 5 are `local-env` (writes only under `$SCR`); Steps 2 and 4 are `check`; Step 6 is `commit`. Task 13's blocks are `live`, `check` or `local-env`. The mutation proof edits copies under `$SCR/t12-mut`, never the worktree |
| 266 | 3 | The rendered job script, compose's `starrocks-init` command (with `$$` → `$`) and the worker healthcheck string are valid shell, and compose's command has no unescaped `$` | `sh -n` on both scripts, and `bash -n -c` on the healthcheck: OK; a regex scan of the parsed command found no single `$`, and the healthcheck has none |
| 267 | 3 | `docker compose -p csr12 … config --quiet` parses the file | Not run at plan time: docker is a live command and the replay forbids it outside Task 13. PyYAML loads it (`check_t12_compose.py`), and Task 13 Step 5 runs `config` |
| 268 | 3 | `kubectl rollout status --timeout` exits non-zero on timeout. `\drg` exists in the PG 18 psql that `kubectl cnpg psql` runs | kubectl documentation (not re-run). `\drg` exists in psql since 16, and the cluster image is postgres 18.6 (`charts/authentik/values.yaml:8`, the same image family) |
| 269 | 4 | Task 12 runs after Tasks 9 and 10. The compose order and the api login need Task 9's worker grants and login creation; the tenant key and the iverson_app revoke need Task 10 to have stopped granting roles to `iverson_app` and to read the key | brief task table; spec §6.2, §7.1, §7.5 |
| 270 | 4 | Inside the job, the revoke runs after the login batch, so `iverson_app` exists before `SHOW GRANTS` names it | The checker pins the order: `s.index("SHOW GRANTS") > s.index("ALTER USER 'iverson_app' SET PROPERTIES")` |
| 271 | 4 | Helm hooks run only after `--wait`, so the api's CONNECT must come from the worker | spec VA22; `ReadinessPolicy.cs:9-13` |
| 272 | 4 | A worker that starts before CNPG has created `iverson_api` fails its grant and is restarted | CDR-2 D1. Task 13 records the restart count instead of asserting zero |
| 273 | 4 | **The Development key must exist by the time Task 10's code requires it.** `WebApplicationFactory<Program>` hosts boot in the Development environment, so they load `appsettings.Development.json` | `Helpers/AuthTestWebApplicationFactory.cs:18` (no `UseEnvironment` override, so the default is Development); about 10 test classes use these factories (`git grep -l WebApplicationFactory`). **If Task 10 makes `StarRocks:TenantKey` required at startup, hunk (j) must move into Task 10, or Task 10's `Iverson.Api.Tests` fail until Task 12.** The assembler must decide with D5 |
| 274 | 4 | Task 13 runs last | by construction |
| 275 | 5 | `lookup` returns empty under `helm template`, so the generate branch renders, with `randAlphaNum` and `b64enc` from sprig | The replayed renders contain both new Secrets with non-empty data on every overlay where they render (checker PASS) |
| 276 | 5 | The revoke parser handles hashed, legacy and `public` roles, ignores privilege rows, and refuses a non-identifier name before splicing it | `t12_job.sh` against the rendered script under dash with a stub `mysql`: `roles`, `none` and `badname` all PASS; mutations M1–M3 each fail one case |
| 277 | 5 | StarRocks accepts `REVOKE <role> FROM '<user>'@'%'` | `job-create-user.yaml:107` already runs `REVOKE user_admin FROM 'iverson_app'@'%'` against 4.1.x (round 11, live) |
| 278 | 5 | **The `SHOW GRANTS` row format.** A role grant is one row, `GRANT 'r1', 'r2' TO 'iverson_app'@'%'`, with no ` ON `. Every privilege row contains ` ON ` | **Not verified live at plan time.** The `Grants` format comes from the StarRocks documentation, and a repo-wide grep finds no recorded probe output. The privilege form (`GRANT <priv> ON <object> TO …`) is the grammar the job itself uses (`:101-103`). Task 13 Step 4 checks the role row first, on a throwaway 4.1.6, and stops if its shape differs |
| 279 | 5 | **`SHOW USERS`** lists `'iverson_t_…'@'%'` rows on StarRocks 4.1.6, and `SHOW PROPERTY FOR '<name>' LIKE 'session.%'` shows the caps | **`SHOW USERS` is not verified** (Task 13 Step 7 runs it first). `SHOW PROPERTY` is spec VA24 (probed) |
| 280 | 5 | Compose YAML: `$$` escapes `$` in `command:`, and the healthcheck's `\\r\\n` in a double-quoted flow string reaches bash as `\r\n` for `printf` | The parsed healthcheck string printed `printf 'GET /health HTTP/1.1\r\nHost: localhost\r\n…'`; `bash -n` OK |
| 281 | 5 | JSON and the secrets script stay valid | Task 12 Step 4's `json.load` and `bash -n` both pass on the replayed files |
| 282 | 6 | Dropping `POSTGRES_APP_PASSWORD` and `<release>-postgres-app` from the api breaks no other reader | Row 4. The worker and the Authentik hook keep both, and CNPG owns the Secret |
| 283 | 6 | Moving `Tenancy__SeedLegacyTenants` to the worker: the only reader is the startup block, which Task 9 gates to the worker | `Program.cs:754`, and no other `git grep` hit in code. `values.yaml:106-110`'s comment ("AND Tenancy__SeedLegacyTenants") stays true. Compose drops the api's copy too, which CDR-2 F1 called inert |
| 284 | 6 | Revoking every role from `iverson_app` breaks no reader once Task 10 is in. Data connections use per-tenant logins, and `/health` and readiness use `SHOW BACKENDS`, which needs only `OPERATE ON SYSTEM` | spec §7.4, VA24, VA25 (`iverson_app` keeps `SHOW BACKENDS`); Task 13 Step 4 checks `SHOW BACKENDS` as `iverson_app` after the revoke |
| 285 | 6 | The Authentik hook's added grant never strips another login's CONNECT | `job-revoke-cross-db.yaml:89-90` revokes from PUBLIC only; spec VA22, CDR-2 U4 |
| 286 | 6 | **Compose reorder vs `stack.py`'s `query` tier** | `Iverson.LoadTest/scripts/stack.py:86,102`: `docker compose up -d --no-deps qdrant tei-embed postgres authentik-server iverson-api`. `--no-deps` does not start iverson-worker, so the api's new `depends_on` is not enforced there. **On a fresh Postgres volume, `iverson_api` does not exist until the worker has run once, so the query tier's api never becomes usable.** On a volume where the full stack has run once, it works. Its docstring (`:44-58`) still describes the api's DDL gating Kestrel. **Resolved (user, spec §10 amended in `a8d18301`):** `stack.py` is unchanged, and the runbook's §1 says that the tier needs a volume where the full stack has run once |
| 287 | 6 | The api healthcheck (TCP 8080) and its consumers are unchanged; only its comment changes | `docker-compose.yml:547-553`; after this task no service `depends_on` `iverson-api` (its only dependent was the worker, `:683`) |
| 288 | 6 | An existing `.env` lacks `IVERSON_STARROCKS_TENANT_KEY`, and compose's `:?` then refuses to start | `generate-compose-secrets.sh:33-37` appends only missing keys. The runbook §1 says to re-run it, and Task 13 Step 5 runs it |
| 289 | 6 | `appsettings.Development.json` is read by local Development runs and by every `WebApplicationFactory` test host. Adding `StarRocks:TenantKey` and dropping `Database=iverson` from a string that no test asserts on is inert for those hosts | `Iverson.Api.csproj:19-25` (committed for Mvc.Testing); `git grep "Database=iverson;User Id=root"` in test projects finds nothing; the factories leave StarRocks clients inert (`AuthTestWebApplicationFactory.cs:11-17`). See row 33 for the ordering |
| 290 | 6 | `job-create-user.yaml`'s new behaviour reaches every StarRocks profile on its next upgrade | The hook runs on post-install and post-upgrade (`:5-17`). The revoke is idempotent: Task 13 Step 4's second rerun revokes 0 |
| 291 | 1 | `IversonPrivilegedRequests` gains a nullable `ClaimedAt timestamptz`, set only by the claim; `RequestedAt` cannot stand in, because a request can wait (reconcile and replay never expire) before it is claimed | Task 7 3k and 3b. The table is new in this plan, so nothing migrates; on a table created before the column existed `ApplySchemaAsync` adds a missing nullable column (`PostgresSchemaManager.cs:127-136`) |
| 292 | 5 | `FailAbandonedAsync` never matches a row whose `ClaimedAt` is NULL (never claimed), because `NULL < x` is not true | Postgres three-valued comparison [PG behaviour]; the container test `FailAbandoned_FailsOnlyRequestsClaimedLongerAgoThanTheCutoff` holds a never-claimed, aged row at `pending` |
| 293 | 6 | Failing a claim older than an hour misreports only a request that is in fact still running then (a very large reconcile). Its own `CompleteAsync`, which matches on `"Id"` alone, later records the real outcome over `failed`. No api caller waits longer than 60 s, so none observes the interim `failed` during its wait | Task 7 3b `CompleteAsync` (`WHERE "Id" = @Id`); `PrivilegedRequestWaits` (15 s, 10 s; Task 8 adds 60 s and 30 s) |
| 294 | 2 | The worker records an abandoned claim with the same status code the executor uses when the worker stops mid-request, `Unavailable` | Task 7 3q (`catch (OperationCanceledException) when (ct.IsCancellationRequested)` → `StatusCode.Unavailable`) and 3r (`Abandoned`) |
| 295 | 6 | `/health`'s `iverson_runtime` probe (Task 9) is passive and cached: it writes nothing, and it runs at most once per 2 s | `PostgresSchemaManager.cs:283-306`: `EnsureCanEnterRoleAsync` begins a transaction, runs `SET LOCAL ROLE`, and calls `RollbackAsync`. Task 9 Step 5d's `EnsureCanEnterRuntimeRoleAsync` only opens a connection around it. The `health-composite` cache (`Program.cs:560-561,592`) returns before any check runs |
| 296 | 6 | The new readiness condition holds for the worker and for every test host, so it constrains only the api's own login | Worker: `iverson` is a member of `iverson_runtime` (`cluster.yaml:43-47` postInitApplicationSQL; on compose it is the superuser, `docker-compose.yml:38-41`), and the worker's `EnsureRolesAsync` already fails startup otherwise (`PostgresSchemaManager.cs:268`). Test hosts: `NoOpRecordStoreSchemaManager` returns `Task.CompletedTask` (Task 9 Step 5d), and NSubstitute's auto-value for a `Task` member is a completed task, so the other `/health` tests keep their status codes |
| 297 | 2 | Re-configuring a substitute with `ThrowsAsync` after the host has booted changes only later calls: the startup probe has already succeeded and the registry has loaded | Program.cs's top-level startup awaits finish inside `CreateClient()`, before `app.Run` serves a request. NSubstitute's last configuration wins, which Task 9's own `GetHealth_WhenTheLoginLacksTheOutboxInsertGrant_IsNotReady` already relies on (`Returns(false)` after the constructor's `Returns(true)`) |
| 298 | 5 | The `/health` lambda parameter `storeRoles` shadows no top-level local. `schemaManager`, which Task 9 Step 5h declares at top level, would | `command grep -n storeRoles` over the repo and the plan finds only Task 9's two uses |
| 299 | 5 | The runbook's re-key snippet derives the same login name and password as `TenantIdentifier.LoginName` and `LoginPassword` | Run at plan time with `TENANT_KEY=test-tenant-login-key` on stdin `acme`: it printed `ALTER USER 'iverson_t_7pfddyte668561tz83en42m5m'@'%' IDENTIFIED BY '401cfbf7dd97a41c7b4d514d4c625ddb59f3789a8c91cec743ac4451c30c1aa3';`, which is row 218's pinned vector (Task 10's `LoginName_IsThePrefixPlusTheRoleNamesFingerprint` and its password test) |
| 300 | 5 | `ALTER USER '<login>'@'%' IDENTIFIED BY '<pw>'` re-keys a tenant login when run as root or as `iverson_provisioner` (which holds `user_admin`) | spec VA24: `ALTER USER IDENTIFIED BY` works from the provisioner with `user_admin` (probed on 4.1.1 and 4.1.6); root holds every right |
| 301 | 4 | At Task 12 Step 2, `appsettings.Development.json` already holds `StarRocks:TenantKey`, so that compose check passes before Task 12's edits | Task 10's Development hunk adds `"StarRocks": { "TenantKey": "dev-only-not-for-production-starrocks-tenant-key-0123456789" }`, and Task 12 (j) was rebased onto it to drop only `Database=iverson;` |
| 302 | 3 | Every task's edits apply in order on `6b942e1c`, and after each of Tasks 1–12 the solution builds and its unit tests pass | Plan-time replay of this text (`replay.py apply --through-task N`, N = 1…12) on a scratch worktree, then `dotnet build Iverson.slnx` and `dotnet test --filter "Category!=Integration"` after each: replay exit 0, 0 build errors and 0 failed tests at every N, across 11 test projects; 3775 passed after Task 1, 3916 after Task 12 |
| 303 | 3 | The integration suites pass on the state Task 12 leaves | Same replay through Task 12, `dotnet test --filter "Category=Integration"` per project: `Iverson.Sql.Tests` 63/63, `Iverson.StarRocks.Tests` 76/76 (including both tests that read the tenant login's caps, after the `ConnectionReset=false` fix in row 221), `Iverson.Api.Tests` 85/85 |

## Tasks

### Task 0: Branch

- [ ] **Step 1: Create the worktree and the scratch environment.**

```bash
cd /home/ben/repositories/Iverson
git worktree add -b csr12-server .worktrees/csr12-server main
mkdir -p /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd
cat > /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh <<'X'
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd
WT=/home/ben/repositories/Iverson/.worktrees/csr12-server
LIVE=$SCR/live
X
git -C .worktrees/csr12-server log --oneline -1
```

Expected: the worktree is on `csr12-server`, and HEAD equals `main` (`6b942e1c` or later).

---

### Task 1: Registration hygiene: declared names take the column's spelling, and case collisions are refused

**Spec:** §2.2 (side fix: owner spoofing through an owner field spelled unlike its column), §2.4 (registration refusals), §9 (the §2 tests for normalised names and refusals). User decision 7.

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
  - `:231` and `:254-256` (`BuildDescriptor`'s `return new SchemaDescriptor { … };`): the built descriptor is returned through `NormalizeDeclaredNames`.
  - After `:256`: new `internal static SchemaDescriptor NormalizeDeclaredNames(SchemaDescriptor descriptor)`. It rewrites `Authorization.OwnerField`, every `Authorization.FieldPermissions[].FieldName` and the `ForeignKey` of every non-OneToMany relation to the spelling of the one column each matches ignoring case. A name matching no column, or more than one, is left alone. The tenant column is never a target.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:204-210` (`LoadAsync`): each admitted stored descriptor goes through `SchemaBuilder.NormalizeDeclaredNames` before it is cached.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
  - `:83-84`: phase 1 calls the new `RejectCaseDuplicateProperties(typeDesc)` after the relation identifier checks.
  - `:266-268`: after the navigation/foreign-key collision loop, a new loop refuses a navigation property whose name matches any column ignoring case.
  - Before `:443` (the doc comment of `RejectDeclaredTenantField`): the new private static `RejectCaseDuplicateProperties`.
- Test (create): `Iverson.Server/Iverson.Api.Tests/Schema/DeclaredNameNormalizationTests.cs`. Covers `BuildDescriptor` and `LoadAsync` normalisation, an ambiguous stored name, and, through `AuthorizationFieldMasking.EnforceWriteAuthorization` with a `BuildDescriptor`-built schema, the create-time owner spoof and the skipped `OwnerImmutable`.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`. Three tests (one a four-case theory) inserted after `RegisterAsync_WithOneToManyPropertyNameEqualsForeignKey_ThrowsInvalidArgument`, which ends at `:1651`.

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces:
  - `SchemaBuilder.NormalizeDeclaredNames(SchemaDescriptor)`. Task 3's `EngagementStoreConsumer.WithOwnerValue` relies on it: after Task 1, `Authorization.OwnerField` of every built or loaded descriptor is the owner column's own spelling.
  - In `SchemaRegistry.LoadAsync`, the statement `descriptor = SchemaBuilder.NormalizeDeclaredNames(descriptor);` plus its comment, inserted directly before `_schemas[typeName] = descriptor;`. **Task 5** also inserts its enrichment-predicate step before `_schemas[typeName] = descriptor;`. It must quote the text as Task 1 leaves it and keep the normalisation ahead of its own step. **Task 9** deletes `await repository.EnsureTableAsync();` at the top of `LoadAsync` (`:69`); that hunk is disjoint from this one.
  - In `SchemaRegistrationOrchestrator.RegisterAsync` phase 1: `RejectCaseDuplicateProperties(typeDesc);` after the relation `ValidateIdentifier` loop, and the navigation/column loop after the collision loop. **Task 5** (`ValidateEnrichmentTargets`) and **Task 7** (phase 3, the queued path) edit other parts of this file and quote the text as Task 1 leaves it. Both refusals are phase-1 validation, which the spec keeps in the api (§5.2), so Task 7's move of phase 3 to the worker must leave them in place.
  - The orchestrator tests are inserted mid-file, after a phase-1-only test, not at the end, so Task 7's re-pointing of phase-3 tests does not collide with them.
- Line numbers are at `6b942e1c`.

- [ ] **Step 1: Write the failing tests**

**1a.** Create `Iverson.Server/Iverson.Api.Tests/Schema/DeclaredNameNormalizationTests.cs`:
```csharp
using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ContractsAuthorizationRules  = Iverson.Client.Contracts.AuthorizationRules;
using ContractsFieldPermission     = Iverson.Client.Contracts.FieldPermission;
using ContractsRelationDescriptor  = Iverson.Client.Contracts.RelationDescriptor;
using ContractsRelationKind        = Iverson.Client.Contracts.RelationKind;
using ContractsRowPermission       = Iverson.Client.Contracts.RowPermission;
using SchemaAuthorizationRules     = Iverson.Api.Schema.AuthorizationRules;
using SchemaFieldPermission        = Iverson.Api.Schema.FieldPermission;
using SchemaRelationDescriptor     = Iverson.Api.Schema.RelationDescriptor;
using SchemaRelationKind           = Iverson.Api.Schema.RelationKind;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// CSR round 12 §2.2. Registration matches <c>owner_field</c>, <c>field_permissions[].field_name</c>
/// and a relation's <c>foreign_key</c> against the type's columns ignoring case, but used to store
/// the caller's spelling. The write gate looks those names up exactly or lower-first, so an owner
/// field spelled unlike its column beyond the first letter let an owner-scoped writer choose the
/// owner on create and let a bypass writer change it on update. Built and stored descriptors now
/// carry the column's own spelling.
/// </summary>
public class DeclaredNameNormalizationTests
{
    private const string NoteKey = "44444444-0000-0000-0000-000000000004";

    private static IEmbeddingService Embedding()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");
        return embedding;
    }

    // Columns Id (key), Title, OwnerId and AuthorId, plus the server-owned tenant column
    // BuildDescriptor appends.
    private static TypeDescriptor NoteType(string ownerField, params ContractsRowPermission[] rowPermissions)
    {
        var td = new TypeDescriptor { TypeName = "Note" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id",       ObjectType = ObjectType.Guid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Title",    ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId",  ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor { Name = "AuthorId", ObjectType = ObjectType.Guid });
        td.Authorization = new ContractsAuthorizationRules { OwnerField = ownerField };
        foreach (var rowPermission in rowPermissions)
            td.Authorization.RowPermissions.Add(rowPermission);
        return td;
    }

    // A descriptor as a build without the rewrite stored it: every declared name keeps the
    // caller's spelling. The OneToMany foreign key happens to match this type's AuthorId column
    // ignoring case, but it names a column of the related type.
    private static SchemaDescriptor StoredNote() => new()
    {
        TypeName      = "Note",
        TableName     = "notes",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Title",    "TEXT", true),
            new ColumnDescriptor("OwnerId",  "TEXT", true),
            new ColumnDescriptor("AuthorId", "UUID", true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ],
        FkColumns     = [new ForeignKeyDescriptor("AuthorId", "Author")],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     =
        [
            new SchemaRelationDescriptor("Author",  SchemaRelationKind.ManyToOne, "Author", "authorid"),
            new SchemaRelationDescriptor("Replies", SchemaRelationKind.OneToMany, "Reply",  "authorid"),
        ],
        Authorization = new SchemaAuthorizationRules(
            "ownerid",
            [],
            [new SchemaFieldPermission("title", ["editors"], [])]),
        TenantColumn  = SchemaDescriptor.TenantColumnName
    };

    // Serializes exactly as SchemaRegistry.RegisterAsync does.
    private static string SerializeAsRegistryWould(SchemaDescriptor descriptor) =>
        System.Text.Json.JsonSerializer.Serialize(
            descriptor,
            new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static async Task<SchemaRegistry> LoadedRegistry(SchemaDescriptor stored)
    {
        var repository = Substitute.For<ISchemaRegistryRepository>();
        repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            (stored.TypeName, SerializeAsRegistryWould(stored))
        });
        var registry = new SchemaRegistry(repository, NullLogger<SchemaRegistry>.Instance);
        await registry.LoadAsync();
        return registry;
    }

    private static IReadOnlyCollection<string> Enforce(
        SchemaDescriptor schema, ClaimsPrincipal writer, Struct payload, string? existingRowJson) =>
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            new RowFieldAuthorizationEvaluator(),
            writer,
            schema,
            payload,
            AuthorizationAction.Write,
            "Not authorized to write this entity.",
            existingRowJson,
            requireExistingRow: existingRowJson is not null,
            new AuditLog(NullLogger<AuditLog>.Instance),
            new PayloadSizeValidator());

    [Fact]
    public void BuildDescriptor_RewritesDeclaredNamesToTheMatchedColumnsSpelling()
    {
        var td = NoteType("ownerid");
        td.Authorization.FieldPermissions.Add(
            new ContractsFieldPermission { FieldName = "title", ReadableRoles = { "editors" } });
        td.Relations.Add(new ContractsRelationDescriptor
            { PropertyName = "Author", Kind = ContractsRelationKind.ManyToOne, RelatedType = "Author", ForeignKey = "authorid" });
        td.Relations.Add(new ContractsRelationDescriptor
            { PropertyName = "Replies", Kind = ContractsRelationKind.OneToMany, RelatedType = "Reply", ForeignKey = "authorid" });

        var descriptor = SchemaBuilder.BuildDescriptor(td, Embedding());

        descriptor.Authorization!.OwnerField.Should().Be("OwnerId");
        descriptor.Authorization.FieldPermissions.Single().FieldName.Should().Be("Title");
        descriptor.Relations.Single(r => r.PropertyName == "Author").ForeignKey.Should().Be("AuthorId");
        descriptor.Relations.Single(r => r.PropertyName == "Replies").ForeignKey.Should().Be("authorid",
            "a OneToMany foreign key names a column of the related type, not of this one");
    }

    [Fact]
    public async Task LoadAsync_RewritesAStoredDescriptorsDeclaredNamesToTheMatchedColumnsSpelling()
    {
        var registry = await LoadedRegistry(StoredNote());

        var loaded = registry.Get("Note")!;
        loaded.Authorization!.OwnerField.Should().Be("OwnerId");
        loaded.Authorization.FieldPermissions.Single().FieldName.Should().Be("Title");
        loaded.Relations.Single(r => r.PropertyName == "Author").ForeignKey.Should().Be("AuthorId");
        loaded.Relations.Single(r => r.PropertyName == "Replies").ForeignKey.Should().Be("authorid");
    }

    [Fact]
    public async Task LoadAsync_ANameMatchingCaseDuplicateColumns_IsLeftAsDeclared_AndTheTypeStillLoads()
    {
        // User decision 7: a stored type with case-duplicate columns stays loaded. Its owner field
        // matches two columns, so there is no single spelling to take.
        var stored = StoredNote() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Title",    "TEXT", true),
                new ColumnDescriptor("OwnerId",  "TEXT", true),
                new ColumnDescriptor("OWNERID",  "TEXT", true),
                new ColumnDescriptor("AuthorId", "UUID", true),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
            ]
        };

        var registry = await LoadedRegistry(stored);

        registry.IsRegistered("Note").Should().BeTrue();
        registry.Get("Note")!.Authorization!.OwnerField.Should().Be("ownerid");
    }

    [Fact]
    public void Create_ByAnOwnerScopedWriter_StampsTheWriterUnderTheOwnerColumnOnly()
    {
        // No group, so no RowPermission applies: the writer is ownership-scoped and the server
        // must set the owner. The client's own OwnerId must not survive beside it.
        var schema = SchemaBuilder.BuildDescriptor(NoteType("ownerid"), Embedding());
        var payload = new Struct
        {
            Fields = { ["Title"] = Value.ForString("t"), ["OwnerId"] = Value.ForString("someone-else") }
        };

        Enforce(schema, ActingUserFixtures.Principal("writer-1"), payload, existingRowJson: null);

        payload.Fields.Keys.Where(k => string.Equals(k, "OwnerId", StringComparison.OrdinalIgnoreCase))
            .Should().Equal("OwnerId");
        payload.Fields["OwnerId"].StringValue.Should().Be("writer-1");
    }

    [Fact]
    public void Update_ByABypassWriter_ChangingTheOwner_IsRefusedAsImmutable()
    {
        var schema = SchemaBuilder.BuildDescriptor(
            NoteType("ownerid", new ContractsRowPermission { Role = "editors", CanWriteAll = true }), Embedding());
        var payload = new Struct
        {
            Fields =
            {
                ["Id"]      = Value.ForString(NoteKey),
                ["Title"]   = Value.ForString("new"),
                ["OwnerId"] = Value.ForString("writer-1"),
            }
        };

        var act = () => Enforce(schema, ActingUserFixtures.Principal("writer-1", "editors"), payload,
            existingRowJson: $$"""{"Id":"{{NoteKey}}","Title":"old","OwnerId":"someone-else","__TenantId":"test-tenant"}""");

        var ex = act.Should().Throw<RpcException>().Which;
        ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Status.Detail.Should().Contain("immutable");
    }
}
```

**1b.** In `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, replace:
```csharp
        ex.Which.Status.Detail.Should().Contain("WidgetId").And.Contain("identical to its foreign key");
    }
```
with:
```csharp
        ex.Which.Status.Detail.Should().Contain("WidgetId").And.Contain("identical to its foreign key");
    }

    // ── CSR round 12 §2.4: names a write payload could not tell apart ─────────
    // The write RPCs map each payload key to the column it names ignoring case, so two columns
    // that differ only by case, or a navigation property named like a column, cannot be told apart.

    [Fact]
    public async Task RegisterAsync_TwoPropertiesWhoseNamesDifferOnlyByCase_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Url", "URL");

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'Url'").And.Contain("'URL'");
        _registry.Get("Widget").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_TheKeyAndAPropertyWhoseNamesDifferOnlyByCase_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "ID");

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'Id'").And.Contain("'ID'");
        _registry.Get("Widget").Should().BeNull();
    }

    [Theory]
    [InlineData(Client.Contracts.RelationKind.ManyToOne, "Name")]
    [InlineData(Client.Contracts.RelationKind.ManyToOne, "name")]
    [InlineData(Client.Contracts.RelationKind.ManyToOne, "id")]
    [InlineData(Client.Contracts.RelationKind.OneToMany, "NAME")]
    public async Task RegisterAsync_ANavigationPropertyNamedLikeAColumnInAnyCase_ThrowsInvalidArgument(
        Client.Contracts.RelationKind kind, string navigationProperty)
    {
        var td = SimpleType("Widget", "Name");
        td.Properties.Add(new PropertyDescriptor { Name = "AuthorId", ObjectType = ObjectType.Guid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = navigationProperty,
            Kind         = kind,
            RelatedType  = "Author",
            ForeignKey   = kind == Client.Contracts.RelationKind.OneToMany ? "WidgetId" : "AuthorId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain($"'{navigationProperty}'").And.Contain("matches column");
        _registry.Get("Widget").Should().BeNull();
    }
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.DeclaredNameNormalizationTests.|FullyQualifiedName~.SchemaRegistrationOrchestratorTests."
```
Expected: 10 failures. Every test already in `SchemaRegistrationOrchestratorTests` passes.
- `BuildDescriptor_RewritesDeclaredNamesToTheMatchedColumnsSpelling`: `OwnerField` is `"ownerid"`.
- `LoadAsync_RewritesAStoredDescriptorsDeclaredNamesToTheMatchedColumnsSpelling`: `OwnerField` is `"ownerid"`.
- `Create_ByAnOwnerScopedWriter_StampsTheWriterUnderTheOwnerColumnOnly`: the keys are `OwnerId` and `ownerid`. The client's `OwnerId` = `someone-else` survives beside the server's `ownerid`.
- `Update_ByABypassWriter_ChangingTheOwner_IsRefusedAsImmutable`: no exception, because `OwnerImmutable` looked up `ownerid` and found nothing.
- `RegisterAsync_TwoPropertiesWhoseNamesDifferOnlyByCase_ThrowsInvalidArgument` and `RegisterAsync_TheKeyAndAPropertyWhoseNamesDifferOnlyByCase_ThrowsInvalidArgument`: no exception.
- `RegisterAsync_ANavigationPropertyNamedLikeAColumnInAnyCase_ThrowsInvalidArgument` (4 cases): no exception.

`LoadAsync_ANameMatchingCaseDuplicateColumns_IsLeftAsDeclared_AndTheTypeStillLoads` **passes**. It pins behaviour that must survive the change (user decision 7). Record that it passed.

- [ ] **Step 3: Implement**

**3a.** In `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`, replace:
```csharp
        return new SchemaDescriptor
        {
            TypeName          = typeDesc.TypeName,
```
with:
```csharp
        return NormalizeDeclaredNames(new SchemaDescriptor
        {
            TypeName          = typeDesc.TypeName,
```

**3b.** In `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`, replace:
```csharp
            DocumentTemplateSource = string.IsNullOrEmpty(typeDesc.DocumentTemplate) ? null : typeDesc.DocumentTemplate
        };
    }
```
with:
```csharp
            DocumentTemplateSource = string.IsNullOrEmpty(typeDesc.DocumentTemplate) ? null : typeDesc.DocumentTemplate
        });
    }

    /// <summary>
    /// CSR round 12 §2.2. Rewrites the names a type declares by reference to the spelling of the one
    /// column each names, ignoring case. These are <c>Authorization.OwnerField</c>, every
    /// <c>Authorization.FieldPermissions[].FieldName</c>, and the <c>ForeignKey</c> of every relation
    /// that is not OneToMany.
    /// <para>
    /// Registration matches these names case-insensitively, but it used to store the caller's
    /// spelling. The write gate looks a name up exactly or lower-first
    /// (<c>StructFieldAccess.Candidates</c>). So with an owner field spelled unlike its column beyond
    /// the first letter, an owner-scoped writer could set the owner on create, because the
    /// authoritative value went in under a second key, and a bypass writer skipped
    /// <c>OwnerImmutable</c>.
    /// </para>
    /// <para>
    /// A name that matches no column is left as declared. So is a name that matches more than one,
    /// on a type stored before case-duplicate columns were refused. The server-owned tenant column
    /// is never a target. A OneToMany foreign key names a column of the related type, so this
    /// type's columns cannot spell it. Called by <see cref="BuildDescriptor"/>, and by
    /// <c>SchemaRegistry.LoadAsync</c> for stored descriptors.
    /// </para>
    /// </summary>
    internal static SchemaDescriptor NormalizeDeclaredNames(SchemaDescriptor descriptor)
    {
        var columnSpelling = descriptor.ScalarColumns
            .Select(c => c.Name)
            .Prepend(descriptor.KeyColumn.Name)
            .Where(name => !SchemaDescriptor.IsTenantColumn(name))
            .Distinct(StringComparer.Ordinal)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(spellings => spellings.Count() == 1)
            .ToDictionary(spellings => spellings.Key, spellings => spellings.Single(), StringComparer.OrdinalIgnoreCase);

        string ColumnSpelling(string name) =>
            columnSpelling.TryGetValue(name, out var column) ? column : name;

        return descriptor with
        {
            Relations = descriptor.Relations
                .Select(r => r.Kind == SchemaRelationKind.OneToMany ? r : r with { ForeignKey = ColumnSpelling(r.ForeignKey) })
                .ToList(),
            Authorization = descriptor.Authorization is not { } authorization ? null : authorization with
            {
                OwnerField       = authorization.OwnerField is null ? null : ColumnSpelling(authorization.OwnerField),
                FieldPermissions = authorization.FieldPermissions
                    .Select(fp => fp with { FieldName = ColumnSpelling(fp.FieldName) })
                    .ToList()
            }
        };
    }
```

**3c.** In `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`, replace:
```csharp
                    logger.LogError(
                        "Schema '{TypeName}' failed identifier/key-type validation on rehydration and was NOT loaded.",
                        typeName);
                    continue;
                }

                _schemas[typeName] = descriptor;
```
with:
```csharp
                    logger.LogError(
                        "Schema '{TypeName}' failed identifier/key-type validation on rehydration and was NOT loaded.",
                        typeName);
                    continue;
                }

                // CSR round 12 §2.2: the rewrite registration applies. A descriptor stored before it
                // reaches the write gate with its owner field, field permissions and foreign keys
                // spelled as its columns are.
                descriptor = SchemaBuilder.NormalizeDeclaredNames(descriptor);

                _schemas[typeName] = descriptor;
```

**3d.** In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
                ValidateIdentifier(relation.RelatedType, $"relation related type on type '{typeDesc.TypeName}'");
            }
```
with:
```csharp
                ValidateIdentifier(relation.RelatedType, $"relation related type on type '{typeDesc.TypeName}'");
            }

            RejectCaseDuplicateProperties(typeDesc);
```

**3e.** In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
                        $"The navigation-property name must be distinct from the foreign key."));
                }
            }
```
with:
```csharp
                        $"The navigation-property name must be distinct from the foreign key."));
                }
            }

            // CSR round 12 §2.4. A navigation property is a payload key, never a column. The write
            // RPCs rename every payload key to the column it names ignoring case, so a navigation
            // name that matches a column would be renamed onto that column. This runs after the
            // collision check above, so a navigation property named exactly like its own foreign
            // key keeps that message.
            foreach (var relation in descriptor.Relations)
            {
                var column = descriptor.ScalarColumns
                    .Select(c => c.Name)
                    .Prepend(descriptor.KeyColumn.Name)
                    .FirstOrDefault(name => string.Equals(name, relation.PropertyName, StringComparison.OrdinalIgnoreCase));
                if (column is not null)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' has a " +
                        $"navigation-property name that matches column '{column}' ignoring case. A navigation " +
                        "property must not share its name with any property, in any letter case."));
                }
            }
```

**3f.** In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
    /// <summary>
    /// The server owns the tenant boundary outright: SchemaBuilder injects
```
with:
```csharp
    // CSR round 12 §2.4 (user decision 7). This refuses two properties, or the key and a property,
    // whose names are equal ignoring case. Postgres would create both quoted columns, but StarRocks
    // resolves column names case-insensitively and cannot hold both. A write RPC also could not
    // tell which column a payload key names. Runs on the inbound typeDesc, which holds the key and
    // every property.
    private static void RejectCaseDuplicateProperties(TypeDescriptor typeDesc)
    {
        var duplicate = typeDesc.Properties
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is null) return;

        throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"'{typeDesc.TypeName}' declares properties {string.Join(", ", duplicate.Select(p => $"'{p.Name}'"))} " +
            "whose names are equal ignoring letter case. Property names must be distinct ignoring case; " +
            "rename one of them."));
    }

    /// <summary>
    /// The server owns the tenant boundary outright: SchemaBuilder injects
```

- [ ] **Step 4: Run the tests and see them pass**

Same command as Step 2. Expected: 0 failures, including every test already in `SchemaRegistrationOrchestratorTests`. The 11 new cases pass: 5 in `DeclaredNameNormalizationTests`, plus 2 Facts and 4 theory cases in the orchestrator class. In particular, `RegisterAsync_WithManyToOnePropertyNameEqualsForeignKey_ThrowsInvalidArgument` still gets "identical to its foreign key", because the collision loop runs before the new navigation/column loop.

Then run the whole non-container Api suite, which also covers the other `BuildDescriptor` callers in `SchemaBuilderTests`, `DocumentTemplateValidationTests` and `ObjectMappingGrpcServiceTests` (RegisterSchema), and the `SchemaRegistryTests` load paths:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures.

- [ ] **Step 5: Mutation proof** (Step 2's command; make each mutation by hand, run, record the failing tests, and undo it before the next)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | Owner field normalised | In `NormalizeDeclaredNames`, change `OwnerField       = authorization.OwnerField is null ? null : ColumnSpelling(authorization.OwnerField),` to `OwnerField       = authorization.OwnerField,` | `BuildDescriptor_RewritesDeclaredNames…`, `LoadAsync_RewritesAStoredDescriptors…`, `Create_ByAnOwnerScopedWriter_…`, `Update_ByABypassWriter_ChangingTheOwner_…` |
| M2 | Stored descriptors normalised | In `SchemaRegistry.LoadAsync`, delete `descriptor = SchemaBuilder.NormalizeDeclaredNames(descriptor);` | `LoadAsync_RewritesAStoredDescriptorsDeclaredNamesToTheMatchedColumnsSpelling` |
| M3 | Ambiguous names left alone | In `NormalizeDeclaredNames`, delete `.Where(spellings => spellings.Count() == 1)` | `LoadAsync_ANameMatchingCaseDuplicateColumns_…` (the `OwnerId`/`OWNERID` group reaches `spellings.Single()`, which throws, so `LoadAsync` throws) |
| M4 | Navigation names compared ignoring case | In the new navigation/column loop, change `StringComparison.OrdinalIgnoreCase` to `StringComparison.Ordinal` | `RegisterAsync_ANavigationPropertyNamedLikeAColumnInAnyCase_…` (cases `name`, `id`, `NAME`) |

- [ ] **Step 6: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Schema/DeclaredNameNormalizationTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
git commit -m "spell declared names as their columns and refuse case-colliding names at registration

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Canonical payload keys at the write RPCs

**Spec:** §2.1 (closes #2), the `FoldKeys` part of §2.3, §9 (the §2 RPC tests).

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs`. Its single method, `internal static void Canonicalize(SchemaDescriptor schema, Struct payload)`, renames each top-level key that names exactly one column ignoring case to that column's spelling. It refuses with `InvalidArgument` two keys naming one column, and a key naming more than one column. Keys naming no column are left alone. The reserved tenant column is never in the map.
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`
  - `:33-36` (Post) and `:103` (Update): call `PayloadKeyCanonicalizer.Canonicalize` first. On Update that is before `ExtractKey`, the tenant-scoped fetch and `EnforceWriteAuthorization`.
  - `:53` and `:133`: `SerializePayload(request.Payload, schema)`.
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
  - `:153-156` (Post) and `:210` (Update): the same call.
  - `:164` and `:235`: `SerializePayload(request.Payload, schema)`.
- Modify: `Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs` (whole file, 45 lines; no other task edits it). `SerializePayload` gains an optional `SchemaDescriptor? schema`. `FoldKeys` keeps a top-level key that is exactly a column name and upper-firsts every other key; nested keys are always upper-firsted.
- Test (create): `Iverson.Server/Iverson.Api.Tests/Grpc/CanonicalPayloadKeyRpcTests.cs`. These are RPC-level tests through both services, with a substituted `IOutboxWriter` that captures the upsert JSON.

**Interfaces:**
- Consumes: nothing from Task 1. The owner tests here use an owner field already spelled like its column.
- Produces:
  - `PayloadKeyCanonicalizer.Canonicalize`.
  - `StructSerializer.SerializePayload(Struct, SchemaDescriptor? schema = null)`. Callers that pass no schema (tests only) keep today's behaviour.
  - After this task, every gate-path `EntityEvent` carries each column key in its exact registered spelling. Task 3's ordinal StarRocks upsert relies on that.
- **Task 9** edits `ObjectMappingGrpcService.cs` at `:340-348` (Delete's maintenance branch). Its quotes are disjoint from this task's Post and Update hunks.
- **Shared constructor sites.** The new test file constructs `ObjectPersistenceGrpcService` and `ObjectMappingGrpcService`. A later task that changes either constructor (Task 7 or Task 9, if they do) must update this file with the existing test classes.
- **Release note (spec §2.1, §10).** `PayloadSizeValidator` now applies to .NET camelCase writes, so an oversized .NET write fails up front with `InvalidArgument`. The repo has no release-notes file. The assembler should carry this line into Task 12's runbook upgrade note, or wherever the plan keeps release notes.
- Line numbers are at `6b942e1c`.

- [ ] **Step 1: Write the failing tests**

Create `Iverson.Server/Iverson.Api.Tests/Grpc/CanonicalPayloadKeyRpcTests.cs`:
```csharp
using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using SchemaAuthorizationRules = Iverson.Api.Schema.AuthorizationRules;
using SchemaRowPermission      = Iverson.Api.Schema.RowPermission;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR round 12 #2. The four client write RPCs rename each payload key to the spelling of the
/// column it names, ignoring case, before anything reads a key by name.
/// <para>
/// Before this, a re-cased owner key slipped past the owner checks, which look a name up exactly
/// or lower-first, and StarRocks then wrote it into the owner column. And the .NET client's
/// camelCase policy turns <c>URLPath</c> into <c>urlPath</c>, which the server upper-firsted to
/// <c>UrlPath</c>, so Postgres' case-sensitive <c>json_populate_record</c> stored NULL. Each test
/// reads the JSON the RPC hands to the Postgres upsert.
/// </para>
/// </summary>
public class CanonicalPayloadKeyRpcTests
{
    private const string LinkKey    = "55555555-0000-0000-0000-000000000005";
    private const string DossierKey = "66666666-0000-0000-0000-000000000006";

    private readonly SchemaRegistry _registry =
        new(Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance);
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IOutboxWriter _outboxWriter = Substitute.For<IOutboxWriter>();
    private readonly List<string> _written = [];
    private readonly ObjectPersistenceGrpcService _persistence;
    private readonly ObjectMappingGrpcService _mapping;

    public CanonicalPayloadKeyRpcTests()
    {
        var actingUser = new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
        var authEvaluator = new RowFieldAuthorizationEvaluator();
        var outboxPublisher = Substitute.For<IOutboxPublisher>();
        var auditLog = new AuditLog(NullLogger<AuditLog>.Instance);

        // NSubstitute's auto-value for Task<string?> is "", not null. Update answers NotFound when
        // the tenant-scoped pre-fetch finds nothing, so update tests stub the stored row.
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns((string?)null);

        // The JSON each write hands to the Postgres upsert (json_populate_record), which the outbox
        // event also carries to the projections.
        _outboxWriter
            .UpsertAndEnqueueOutboxAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<string>(json => _written.Add(json)), Arg.Any<string?>())
            .Returns(Guid.NewGuid());

        _persistence = new ObjectPersistenceGrpcService(
            outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectPersistenceGrpcService>.Instance,
            _entities,
            actingUser,
            authEvaluator,
            auditLog);
        _mapping = new ObjectMappingGrpcService(
            _entities,
            Substitute.For<IRecordStoreTransactionRunner>(),
            outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectMappingGrpcService>.Instance,
            actingUser,
            authEvaluator,
            Substitute.For<IEntityRelationResolver>(),
            Substitute.For<ISchemaRegistrationOrchestrator>(),
            auditLog,
            EngagementQueryLimitOptions.Default);
    }

    // ID and URLPath start with an acronym, which the .NET client's camelCase policy folds: it
    // sends "id" and "urlPath". url starts lower-case, which a property name may.
    private static SchemaDescriptor LinkSchema() => new()
    {
        TypeName      = "Link",
        TableName     = "links",
        KeyColumn     = new ColumnDescriptor("ID", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("URLPath", "TEXT", true),
            new ColumnDescriptor("url",     "TEXT", true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        Authorization = new SchemaAuthorizationRules(
            null, [new SchemaRowPermission("test-bypass", true, true, true)], []),
        TenantColumn  = SchemaDescriptor.TenantColumnName
    };

    // A type stored before case-duplicate columns were refused at registration (user decision 7).
    private static SchemaDescriptor CaseDuplicateSchema() => LinkSchema() with
    {
        TypeName      = "LegacyLink",
        TableName     = "legacy_links",
        ScalarColumns =
        [
            new ColumnDescriptor("Url", "TEXT", true),
            new ColumnDescriptor("URL", "TEXT", true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ]
    };

    private void StubStoredLink() =>
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"ID":"{{LinkKey}}","URLPath":"/old","url":"old","__TenantId":"test-tenant"}""");

    private void StubStoredDossier() =>
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{DossierKey}}","Title":"old title","OwnerId":"someone-else","__TenantId":"test-tenant"}""");

    private static Struct MakePayload(Dictionary<string, Value> fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = v;
        return s;
    }

    private JsonElement WrittenRow() =>
        JsonDocument.Parse(_written.Should().ContainSingle().Which).RootElement.Clone();

    // ── Acronym-led and lower-case-first columns, at each of the four RPCs ────

    [Fact]
    public async Task PersistencePost_AcronymAndLowerCaseFirstKeys_ReachTheUpsertUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());

        await _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["urlPath"] = Value.ForString("/a"), ["url"] = Value.ForString("a") })
        }, TestServerCallContext.Create());

        var row = WrittenRow();
        row.GetProperty("URLPath").GetString().Should().Be("/a");
        row.GetProperty("url").GetString().Should().Be("a");
    }

    [Fact]
    public async Task PersistenceUpdate_AcronymKeyAndColumns_ReachTheUpsertUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());
        StubStoredLink();

        var response = await _persistence.Update(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new()
            {
                ["id"]      = Value.ForString(LinkKey),
                ["urlPath"] = Value.ForString("/b"),
                ["url"]     = Value.ForString("b"),
            })
        }, TestServerCallContext.Create());

        response.Key.Should().Be(LinkKey);
        var row = WrittenRow();
        row.GetProperty("ID").GetString().Should().Be(LinkKey);
        row.GetProperty("URLPath").GetString().Should().Be("/b");
        row.GetProperty("url").GetString().Should().Be("b");
    }

    [Fact]
    public async Task MappingPost_AcronymAndLowerCaseFirstKeys_ReachTheUpsertAndTheResponseUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());

        var response = await _mapping.Post(new MappingWriteRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["urlPath"] = Value.ForString("/a"), ["url"] = Value.ForString("a") })
        }, TestServerCallContext.Create());

        var row = WrittenRow();
        row.GetProperty("URLPath").GetString().Should().Be("/a");
        row.GetProperty("url").GetString().Should().Be("a");
        // Post echoes the request payload, so the caller now gets the registered spelling back.
        response.Data.Fields.Keys.Should().Contain(["ID", "URLPath", "url"]);
    }

    [Fact]
    public async Task MappingUpdate_AcronymKeyAndColumns_ReachTheUpsertUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());
        StubStoredLink();

        var response = await _mapping.Update(new MappingWriteRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new()
            {
                ["id"]      = Value.ForString(LinkKey),
                ["urlPath"] = Value.ForString("/b"),
                ["url"]     = Value.ForString("b"),
            })
        }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        var row = WrittenRow();
        row.GetProperty("ID").GetString().Should().Be(LinkKey);
        row.GetProperty("URLPath").GetString().Should().Be("/b");
        row.GetProperty("url").GetString().Should().Be("b");
    }

    // ── A re-cased owner key is an owner change ───────────────────────────────
    // "test-bypass" may write every Dossier row (CanWriteAll), so only OwnerImmutable stands between
    // it and reassigning a row it does not own.

    [Fact]
    public async Task PersistenceUpdate_AReCasedOwnerKey_IsRefusedAsAnOwnerChange()
    {
        await _registry.RegisterAsync(SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(withOwnerField: true));
        StubStoredDossier();

        var act = () => _persistence.Update(new PersistRequest
        {
            TypeName = "Dossier",
            Payload  = MakePayload(new()
            {
                ["Id"]      = Value.ForString(DossierKey),
                ["Title"]   = Value.ForString("new title"),
                ["OWNERID"] = Value.ForString("test-user"),
            })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task MappingUpdate_AReCasedOwnerKey_IsRefusedAsAnOwnerChange()
    {
        await _registry.RegisterAsync(SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(withOwnerField: true));
        StubStoredDossier();

        var act = () => _mapping.Update(new MappingWriteRequest
        {
            TypeName = "Dossier",
            Payload  = MakePayload(new()
            {
                ["Id"]      = Value.ForString(DossierKey),
                ["Title"]   = Value.ForString("new title"),
                ["OWNERID"] = Value.ForString("test-user"),
            })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
        _written.Should().BeEmpty();
    }

    // ── Keys that cannot be mapped to one column ──────────────────────────────

    [Fact]
    public async Task Post_TwoKeysNamingOneColumn_IsInvalidArgument()
    {
        await _registry.RegisterAsync(LinkSchema());

        var act = () => _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["URLPath"] = Value.ForString("/a"), ["urlPath"] = Value.ForString("/b") })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'URLPath'").And.Contain("'urlPath'");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task Post_AKeyMatchingCaseDuplicateColumns_IsInvalidArgument()
    {
        await _registry.RegisterAsync(CaseDuplicateSchema());

        var act = () => _persistence.Post(new PersistRequest
        {
            TypeName = "LegacyLink",
            Payload  = MakePayload(new() { ["url"] = Value.ForString("a") })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'Url'").And.Contain("'URL'");
        _written.Should().BeEmpty();
    }

    // ── What canonicalisation leaves alone ────────────────────────────────────

    [Fact]
    public async Task Post_AReCasedTenantColumn_IsRefusedUnderTheCallersOwnSpelling()
    {
        // The reserved tenant column is not a rename target, so the smuggling check still names
        // what the caller sent.
        await _registry.RegisterAsync(LinkSchema());

        var act = () => _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["__tenantid"] = Value.ForString("other-tenant") })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'__tenantid'");
    }

    [Fact]
    public async Task Post_KeysThatAreNotColumns_AreStillUpperFirstedAtEveryLevel()
    {
        // Only a top-level key that is exactly a column keeps its spelling. A nested key never
        // names a column, even when it is spelled like one.
        await _registry.RegisterAsync(LinkSchema());
        var meta = new Struct { Fields = { ["url"] = Value.ForString("nested") } };

        await _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["extra"] = Value.ForString("x"), ["meta"] = Value.ForStruct(meta) })
        }, TestServerCallContext.Create());

        var row = WrittenRow();
        row.GetProperty("Extra").GetString().Should().Be("x");
        row.GetProperty("Meta").GetProperty("Url").GetString().Should().Be("nested");
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.CanonicalPayloadKeyRpcTests."
```
Expected: 8 failed, 2 passed.
- The four `…_AcronymAndLowerCaseFirstKeys…` and `…_AcronymKeyAndColumns…` tests fail.
  - On Post, `GetProperty("URLPath")` throws `KeyNotFoundException`, because the upsert JSON carries `UrlPath` and `Url`.
  - On Update, the RPC throws `InvalidArgument` "Update requires a non-empty 'ID' in the payload.", because `ExtractKey` looks for `ID` or `iD` and the payload has `id`.
- `PersistenceUpdate_AReCasedOwnerKey_…` and `MappingUpdate_AReCasedOwnerKey_…`: no exception. The write goes through with `OWNERID`.
- `Post_TwoKeysNamingOneColumn_IsInvalidArgument`: no exception. `URLPath` and `UrlPath` do not collide in `FoldKeys`.
- `Post_AKeyMatchingCaseDuplicateColumns_IsInvalidArgument`: no exception. `url` is written as `Url`.
- `Post_AReCasedTenantColumn_IsRefusedUnderTheCallersOwnSpelling` and `Post_KeysThatAreNotColumns_AreStillUpperFirstedAtEveryLevel` **pass**. They pin behaviour that must survive the change. Record that they passed.

- [ ] **Step 3: Implement**

**3a.** Create `Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs`:
```csharp
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

/// <summary>
/// CSR round 12 #2. Renames each top-level payload key that names a column, ignoring case, to the
/// column's registered spelling. The four client write RPCs run it first: before the key is read
/// and before <see cref="AuthorizationFieldMasking.EnforceWriteAuthorization"/>. Every later step
/// that finds a column by name then sees one spelling: the key read, the owner checks, the size
/// guard, the serializer, and Postgres' case-sensitive <c>json_populate_record</c>.
/// <para>
/// Two things went wrong without it. A re-cased owner key (<c>OWNERID</c>) passed the owner checks,
/// which look a name up exactly or lower-first, and StarRocks, which resolves column names
/// case-insensitively, then wrote it into the owner column. And the .NET client's camelCase policy
/// folds a leading acronym: <c>URLPath</c> is sent as <c>urlPath</c>, which reached Postgres as
/// <c>UrlPath</c> and was stored as NULL.
/// </para>
/// <para>
/// Two cases are refused with InvalidArgument: two keys that name the same column, and a key that
/// names more than one column (on a type stored before case-duplicate columns were refused at
/// registration). Keys that name no column, navigation properties among them, are left alone. The
/// server-owned tenant column is never a target, so the smuggling check refuses a re-cased copy of
/// it under the caller's own spelling.
/// </para>
/// </summary>
internal static class PayloadKeyCanonicalizer
{
    internal static void Canonicalize(SchemaDescriptor schema, Struct payload)
    {
        var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in schema.ScalarColumns.Select(c => c.Name).Prepend(schema.KeyColumn.Name))
        {
            if (SchemaDescriptor.IsTenantColumn(name)) continue;
            if (!columns.TryGetValue(name, out var spellings))
                columns[name] = spellings = [];
            if (!spellings.Contains(name, StringComparer.Ordinal))
                spellings.Add(name);
        }

        var keyFor = new Dictionary<string, string>(StringComparer.Ordinal);
        var renames = new List<(string From, string To)>();
        foreach (var key in payload.Fields.Keys)
        {
            if (!columns.TryGetValue(key, out var spellings)) continue;

            if (spellings.Count > 1)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Payload field '{key}' on '{schema.TypeName}' matches more than one column " +
                    $"({string.Join(", ", spellings.Select(s => $"'{s}'"))}), whose names differ only by " +
                    "letter case. Re-register the type with property names that are distinct ignoring case."));
            }

            var column = spellings[0];
            if (keyFor.TryGetValue(column, out var other))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Payload fields '{other}' and '{key}' on '{schema.TypeName}' both name column '{column}'; " +
                    "send it once."));
            }

            keyFor[column] = key;
            if (!string.Equals(key, column, StringComparison.Ordinal))
                renames.Add((key, column));
        }

        foreach (var (from, to) in renames)
        {
            var value = payload.Fields[from];
            payload.Fields.Remove(from);
            payload.Fields[to] = value;
        }
    }
}
```

**3b.** Replace the whole of `Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs` with:
```csharp
using System.Text.Json;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

internal static class StructSerializer
{
    // A nested object's keys never name a column.
    private static readonly HashSet<string> s_noColumns = [];

    /// <summary>
    /// Serializes a write payload. A top-level key that is exactly one of <paramref name="schema"/>'s
    /// column names is written as it is. Every other key, nested keys included, is upper-firsted.
    /// The write RPCs pass the schema after <see cref="PayloadKeyCanonicalizer"/> has given every
    /// column key its registered spelling. So a column whose name starts lower-case (<c>url</c>)
    /// reaches Postgres' case-sensitive <c>json_populate_record</c> under its own name, not as
    /// <c>Url</c> (CSR round 12 §2.3).
    /// </summary>
    internal static string SerializePayload(Struct payload, SchemaDescriptor? schema = null) =>
        JsonSerializer.Serialize(FoldKeys(payload.Fields, schema is null
            ? s_noColumns
            : schema.ScalarColumns.Select(c => c.Name).Append(schema.KeyColumn.Name).ToHashSet(StringComparer.Ordinal)));

    internal static string UpperFirst(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static Dictionary<string, object?> FoldKeys(MapField<string, Value> fields, IReadOnlySet<string> columnNames)
    {
        var originalNameOf = new Dictionary<string, string>();
        var folded = new Dictionary<string, object?>();
        foreach (var (key, value) in fields)
        {
            var upper = columnNames.Contains(key) ? key : UpperFirst(key);
            if (originalNameOf.TryGetValue(upper, out var collidingOriginal))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Payload fields '{collidingOriginal}' and '{key}' both fold to '{upper}' after " +
                    "case-normalization; rename one of them."));
            }
            originalNameOf[upper] = key;
            folded[upper] = ProtoValueToObject(value);
        }
        return folded;
    }

    private static object? ProtoValueToObject(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.StringValue  => v.StringValue,
        Value.KindOneofCase.NumberValue  => v.NumberValue,
        Value.KindOneofCase.BoolValue    => v.BoolValue,
        Value.KindOneofCase.NullValue    => null,
        Value.KindOneofCase.ListValue    => v.ListValue.Values.Select(ProtoValueToObject).ToList(),
        Value.KindOneofCase.StructValue  => FoldKeys(v.StructValue.Fields, s_noColumns),
        _                                => null
    };
}
```

**3c.** In `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`, replace:
```csharp
        var schema = RequireSchema(request.TypeName);

        AuthorizationFieldMasking.EnforceWriteAuthorization(
```
with:
```csharp
        var schema = RequireSchema(request.TypeName);

        // CSR round 12 #2: ahead of every step that reads a payload key by name.
        PayloadKeyCanonicalizer.Canonicalize(schema, request.Payload);

        AuthorizationFieldMasking.EnforceWriteAuthorization(
```

**3d.** In `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`, replace:
```csharp
        var key = keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);
```
with:
```csharp
        var key = keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);

        var payloadJson = StructSerializer.SerializePayload(request.Payload, schema);
```

**3e.** In `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`, replace:
```csharp
        var key = keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
```
with:
```csharp
        // CSR round 12 #2: before the key read, so a key column such as ID, which the .NET client
        // sends as "id", is found.
        PayloadKeyCanonicalizer.Canonicalize(schema, request.Payload);

        var key = keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
```

**3f.** In `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`, replace:
```csharp
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);
```
with:
```csharp
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload, schema);
```

**3g.** In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, replace:
```csharp
        var schema = RequireSchema(request.TypeName);

        AuthorizationFieldMasking.EnforceWriteAuthorization(
```
with:
```csharp
        var schema = RequireSchema(request.TypeName);

        // CSR round 12 #2: ahead of every step that reads a payload key by name.
        PayloadKeyCanonicalizer.Canonicalize(schema, request.Payload);

        AuthorizationFieldMasking.EnforceWriteAuthorization(
```

**3h.** In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, replace:
```csharp
        var key = _keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);
```
with:
```csharp
        var key = _keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);

        var payloadJson = StructSerializer.SerializePayload(request.Payload, schema);
```

**3i.** In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, replace:
```csharp
        var key = _keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
```
with:
```csharp
        // CSR round 12 #2: before the key read, so a key column such as ID, which the .NET client
        // sends as "id", is found.
        PayloadKeyCanonicalizer.Canonicalize(schema, request.Payload);

        var key = _keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
```

**3j.** In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, replace:
```csharp
        _relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);
```
with:
```csharp
        _relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload, schema);
```

- [ ] **Step 4: Run the tests and see them pass**

Same command as Step 2. Expected: 10 passed, 0 failed.

Then run the whole non-container Api suite. It covers the existing RPC classes, `StructSerializerTests` (which call `SerializePayload` with no schema, so behaviour is unchanged), `AuthorizationFieldMaskingTests` and the consumer tests:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures.

Then the real-Postgres carry-forward class, which sends a Persistence Update through `SerializePayload` and `json_populate_record`. **Heavy: it starts a Postgres Testcontainers container. Nothing else may run at the same time.**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.UpdateCarryForwardPostgresIntegrationTests."
```
Expected: 0 failures.

- [ ] **Step 5: Mutation proof** (Step 2's command; make each mutation by hand, run, record the failing tests, and undo it before the next)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | Rename to the column's spelling | In `Canonicalize`, change `payload.Fields[to] = value;` to `payload.Fields[from] = value;` | the four `…Acronym…` tests, `PersistenceUpdate_AReCasedOwnerKey_…`, `MappingUpdate_AReCasedOwnerKey_…` |
| M2 | A key naming several columns is refused | In `Canonicalize`, change `if (spellings.Count > 1)` to `if (spellings.Count > 2)` | `Post_AKeyMatchingCaseDuplicateColumns_IsInvalidArgument` |
| M3 | Two keys naming one column are refused | In `Canonicalize`, change `if (keyFor.TryGetValue(column, out var other))` to `if (keyFor.TryGetValue(column, out var other) && false)` | `Post_TwoKeysNamingOneColumn_IsInvalidArgument` |
| M4 | The serializer keeps exact column keys | In `FoldKeys`, change `var upper = columnNames.Contains(key) ? key : UpperFirst(key);` to `var upper = UpperFirst(key);` | the four `…Acronym…` tests (on `url`) |

- [ ] **Step 6: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/CanonicalPayloadKeyRpcTests.cs
git commit -m "give payload keys their column's spelling at the write rpcs

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: The StarRocks sink matches column names exactly, and the owner override drops every re-cased copy

**Spec:** §2.3 (the `EngagementRepository.UpsertAsync` and `EngagementStoreConsumer.WithOwnerValue` parts), §9 ("StarRocks upsert exact; `WithOwnerValue` drops variants").

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`
  - `:448-462` (`UpsertAsync`): payload entry selection moves into a new `internal static List<KeyValuePair<string, JsonElement>> UpsertEntries(EngagementTableSchema schema, string payloadJson)`, which matches column names ordinally. It used `StringComparer.OrdinalIgnoreCase`. The rest of `UpsertAsync` is unchanged.
  - Before `:482` (`DeleteAsync`): the new `UpsertEntries`. It is a seam so the selection can be unit-tested without a server, the same way `OpenProvisionerConnectionAsync` is.
- Modify: `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs:118-133`. `WithOwnerValue` removes every key equal to the owner field ignoring case, then sets the authoritative value under `ownerField`, or sets nothing when the value is null.
- Test (create): `Iverson.Server/Iverson.StarRocks.Tests/EngagementUpsertEntriesTests.cs`. Unit tests; no container.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs`. Two tests inserted before `// ── Registration gate` (`:512`).

**Interfaces:**
- Consumes:
  - Task 1: `Authorization.OwnerField` of every built or loaded descriptor is the owner column's own spelling (`SchemaBuilder.NormalizeDeclaredNames`). `WithOwnerValue` therefore writes under `ownerField`, and `ProjectionTenantResolution.ReadString(ownerField)` finds the column in `row_to_json`.
  - Task 2: gate-path events carry exact column spellings. Enrichment and reconciliation republish `row_to_json`, which is exact too (spec Verified assumption 33). The ordinal selection drops only keys that are not columns.
- Produces: `EngagementRepository.UpsertEntries`. **Task 10** rewrites `EngagementRepository`'s connection handling (`RunTenantScopedAsync` and the per-tenant connection strings) and the provisioning code. This task's two hunks are `UpsertAsync`'s entry selection and an insertion directly above `DeleteAsync`'s signature line. Task 10 quotes the file as Task 3 leaves it. If it rewrites `DeleteAsync`, it keeps `UpsertEntries` above it.
- Line numbers are at `6b942e1c`.

- [ ] **Step 1: Write the failing tests**

**1a.** Create `Iverson.Server/Iverson.StarRocks.Tests/EngagementUpsertEntriesTests.cs`:
```csharp
using FluentAssertions;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §2.3. The StarRocks upsert writes a payload entry only when its key is exactly a
/// column name. StarRocks resolves column names case-insensitively, so a re-cased key such as
/// <c>OWNERID</c> would otherwise be written into the <c>OwnerId</c> column. No server is needed.
/// </summary>
public class EngagementUpsertEntriesTests
{
    private static readonly EngagementTableSchema Schema = new(
        "notes",
        new EngagementColumnSchema("Id", "VARCHAR(36)", false),
        [
            new EngagementColumnSchema("Title",   "STRING", true),
            new EngagementColumnSchema("OwnerId", "STRING", true),
            new EngagementColumnSchema("url",     "STRING", true),
        ]);

    [Fact]
    public void KeysThatAreExactlyColumnNames_AreSelected()
    {
        var entries = EngagementRepository.UpsertEntries(Schema,
            """{"Id":"k1","Title":"t","OwnerId":"o","url":"/a"}""");

        entries.Select(e => e.Key).Should().BeEquivalentTo(["Id", "Title", "OwnerId", "url"]);
    }

    [Fact]
    public void AKeyThatMatchesAColumnOnlyIgnoringCase_IsNotSelected()
    {
        var entries = EngagementRepository.UpsertEntries(Schema,
            """{"Id":"k1","OWNERID":"victim","id":"k2","Url":"/b"}""");

        entries.Select(e => e.Key).Should().Equal("Id");
    }

    [Fact]
    public void AnObjectValuedEntry_IsNotSelected()
    {
        var entries = EngagementRepository.UpsertEntries(Schema,
            """{"Id":"k1","Title":{"nested":1}}""");

        entries.Select(e => e.Key).Should().Equal("Id");
    }
}
```

**1b.** In `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs`, replace:
```csharp
    // ── Registration gate ─────────────────────────────────────────────────────
```
with:
```csharp
    // ── Re-cased owner keys (CSR round 12 §2.3) ───────────────────────────────
    // StarRocks resolves column names case-insensitively, so every key naming the owner column in
    // any letter case is the owner column. An event replayed from before payload keys were
    // canonical can carry one.

    [Fact]
    public async Task HandleUpsert_WithReCasedOwnerKeysInPayload_UpsertsOnlyTheAuthoritativeOwner()
    {
        var schema = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new AuthorizationRules(
                "Name",
                new List<RowPermission> { new("test-bypass", true, true, true) },
                new List<FieldPermission>())
        };
        await _registry.RegisterAsync(schema);
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Returns("""{"Name":"RealOwner","TenantId":"tenant-a"}""");

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Forged","NAME":"AlsoForged","name":"Forged too"}""",
            TraceId:       "trace-recased-owner",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        string? capturedJson = null;
        _sr.UpsertAsync(Arg.Any<EngagementTableSchema>(), Arg.Do<string>(j => capturedJson = j), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedJson!);
        var ownerKeys = doc.RootElement.EnumerateObject()
            .Where(p => string.Equals(p.Name, "Name", StringComparison.OrdinalIgnoreCase))
            .ToList();
        ownerKeys.Select(p => p.Name).Should().Equal("Name");
        ownerKeys.Single().Value.GetString().Should().Be("RealOwner");
    }

    [Fact]
    public async Task HandleUpsert_WithReCasedOwnerKeysAndNoAuthoritativeOwner_UpsertsNoOwnerKeyAtAll()
    {
        // Fail closed: with no authoritative owner, no spelling of the owner key may reach StarRocks.
        var schema = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new AuthorizationRules(
                "Name",
                new List<RowPermission> { new("test-bypass", true, true, true) },
                new List<FieldPermission>())
        };
        await _registry.RegisterAsync(schema);
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
                 .Returns("""{"TenantId":"tenant-a"}""");

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"NAME":"victim","name":"victim"}""",
            TraceId:       "trace-recased-owner-null",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        string? capturedJson = null;
        _sr.UpsertAsync(Arg.Any<EngagementTableSchema>(), Arg.Do<string>(j => capturedJson = j), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedJson!);
        doc.RootElement.EnumerateObject()
            .Where(p => string.Equals(p.Name, "Name", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    // ── Registration gate ─────────────────────────────────────────────────────
```

- [ ] **Step 2: Run them and see them fail**

StarRocks unit tests (no containers):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.EngagementUpsertEntriesTests."
```
Expected: the build fails with `CS0117: 'EngagementRepository' does not contain a definition for 'UpsertEntries'`, three times, in `EngagementUpsertEntriesTests.cs`.

Consumer tests:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.EngagementStoreConsumerTests."
```
Expected: 2 failures, and every existing test in the class passes.
- `HandleUpsert_WithReCasedOwnerKeysInPayload_UpsertsOnlyTheAuthoritativeOwner`: the owner keys are `Name`, `NAME` and `name`.
- `HandleUpsert_WithReCasedOwnerKeysAndNoAuthoritativeOwner_UpsertsNoOwnerKeyAtAll`: `NAME` and `name` survive.

- [ ] **Step 3: Implement**

**3a.** In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:
```csharp
        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
            ?? new Dictionary<string, JsonElement>();

        var knownCols = schema.Columns
            .Select(c => c.Name)
            .Append(schema.KeyColumn.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = row
            .Where(kv => knownCols.Contains(kv.Key)
                      && kv.Value.ValueKind != JsonValueKind.Object
                      && kv.Value.ValueKind != JsonValueKind.Undefined)
            .ToList();
```
with:
```csharp
        var entries = UpsertEntries(schema, payloadJson);
```

**3b.** In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:
```csharp
    public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
```
with:
```csharp
    /// <summary>
    /// The payload entries <see cref="UpsertAsync"/> writes. An entry is written when its key is
    /// exactly a column name, compared ordinally, and its value is not a JSON object.
    /// <para>
    /// Ordinal, because StarRocks resolves column names case-insensitively. A re-cased key such as
    /// <c>OWNERID</c> would otherwise go into the <c>OwnerId</c> column, past the owner value the
    /// consumer re-derived from Postgres (CSR round 12 §2.3). Every producer emits exact column
    /// names: the write RPCs canonicalise keys at entry, and enrichment and reconciliation
    /// republish <c>row_to_json</c>. A key that is not exact therefore names no column.
    /// </para>
    /// </summary>
    internal static List<KeyValuePair<string, JsonElement>> UpsertEntries(EngagementTableSchema schema, string payloadJson)
    {
        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
            ?? new Dictionary<string, JsonElement>();

        var knownCols = schema.Columns
            .Select(c => c.Name)
            .Append(schema.KeyColumn.Name)
            .ToHashSet(StringComparer.Ordinal);

        return row
            .Where(kv => knownCols.Contains(kv.Key)
                      && kv.Value.ValueKind != JsonValueKind.Object
                      && kv.Value.ValueKind != JsonValueKind.Undefined)
            .ToList();
    }

    public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
```

**3c.** In `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs`, replace:
```csharp
    // Overrides the owner-column key in a raw event payload JSON document with the authoritative
    // value, or removes the key entirely when the authoritative value is null (fail closed —
    // never fall back to the payload's own, unvalidated value). StarRocks's UpsertAsync takes a
    // whole JSON document rather than per-field values, so unlike the Qdrant/Intelligence sibling
    // fix, the corrected value has to be spliced back into the document rather than assembled
    // into a fresh payload dictionary.
    private static string WithOwnerValue(string payloadJson, string ownerField, string? authoritativeOwnerValue)
    {
        var node = JsonNode.Parse(payloadJson)!.AsObject();
        if (authoritativeOwnerValue is null)
            node.Remove(ownerField);
        else
            node[ownerField] = JsonValue.Create(authoritativeOwnerValue);

        return node.ToJsonString();
    }
```
with:
```csharp
    // Overrides the owner column in a raw event payload JSON document with the authoritative value,
    // or leaves it out entirely when the authoritative value is null (fail closed — never fall back
    // to the payload's own, unvalidated value). StarRocks's UpsertAsync takes a whole JSON document
    // rather than per-field values, so unlike the Qdrant/Intelligence sibling fix, the corrected
    // value has to be spliced back into the document rather than assembled into a fresh payload
    // dictionary.
    //
    // Every key naming the owner column in any letter case is removed first, as
    // OutboxWriter.WithTenantColumn does for the tenant column. StarRocks resolves column names
    // case-insensitively, so a surviving re-cased key would still be written into the owner column
    // (CSR round 12 §2.3). The value is set under ownerField, which SchemaBuilder.NormalizeDeclaredNames
    // makes the column's own spelling.
    private static string WithOwnerValue(string payloadJson, string ownerField, string? authoritativeOwnerValue)
    {
        var node = JsonNode.Parse(payloadJson)!.AsObject();
        foreach (var key in node
                     .Select(kv => kv.Key)
                     .Where(k => string.Equals(k, ownerField, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            node.Remove(key);

        if (authoritativeOwnerValue is not null)
            node[ownerField] = JsonValue.Create(authoritativeOwnerValue);

        return node.ToJsonString();
    }
```

- [ ] **Step 4: Run the tests and see them pass**

Both Step 2 commands. Expected: `EngagementUpsertEntriesTests` 3 passed, and `EngagementStoreConsumerTests` 0 failures.

Then the two non-container suites:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "Category!=Integration"
```
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures in each.

Then the live upsert path. `TenantIsolationIntegrationTests` upserts payloads whose keys are the exact column names (`ProbePayload`: `Id`, `Name`, against `ProbeSchema`'s `Id` and `Name`) and reads them back. **Heavy: it starts a StarRocks Testcontainers container. Nothing else may run at the same time.**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.TenantIsolationIntegrationTests."
```
Expected: 0 failures.

- [ ] **Step 5: Mutation proof** (the Step 2 commands; make each mutation by hand, run, record the failing tests, and undo it before the next)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | Exact column match in the StarRocks upsert | In `UpsertEntries`, change `.ToHashSet(StringComparer.Ordinal);` to `.ToHashSet(StringComparer.OrdinalIgnoreCase);` | `AKeyThatMatchesAColumnOnlyIgnoringCase_IsNotSelected` |
| M2 | Every case variant of the owner key dropped | In `WithOwnerValue`, change `StringComparison.OrdinalIgnoreCase` to `StringComparison.Ordinal` | `HandleUpsert_WithReCasedOwnerKeysInPayload_…`, `HandleUpsert_WithReCasedOwnerKeysAndNoAuthoritativeOwner_…` |
| M3 | A null owner writes nothing | In `WithOwnerValue`, change `if (authoritativeOwnerValue is not null)` to `if (true)` | `HandleUpsert_WithReCasedOwnerKeysAndNoAuthoritativeOwner_…`, and the existing `HandleUpsert_WithOwnerFieldAndNoAuthoritativeRow_OmitsOwnerKeyFromPayload` |

- [ ] **Step 6: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add Iverson.Server/Iverson.StarRocks/EngagementRepository.cs Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs Iverson.Server/Iverson.StarRocks.Tests/EngagementUpsertEntriesTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs
git commit -m "match starrocks upsert columns exactly and drop re-cased owner keys

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Read-path leaks — popularity eligibility, the SearchSimilar allow-list, decay from readable fields

**Spec:** §3.1, §3.2, §9 (§3 rows) (closes #8, #9)

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs` — the one predicate, `IsEligible(parent, relation, registry)`, plus an overload taking the already-resolved child, which the updater uses.
- Modify: `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs`:
  - `:26-29`: the `PopularityUpdateOutcome` doc names the new skip;
  - `:52-56`: `PopularitySignalUpdater.UpdateAsync` returns `Skipped` for an ineligible signal before any StarRocks or Qdrant call. This one gate covers both callers, the consumer (`:249`, `:255`) and the sweep (`PopularitySignalReconciliationWorker.cs:75`). The sweep file does not change.
- Modify: `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs`, `ValidateAtStartup`:
  - after `:101-104`: refuse a signal whose `{relation camelCase}Count` or `…CountBuckets` key case-insensitively equals a parent column;
  - after `:123-127`: log each ineligible signal once (Warning).
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`:
  - `:264-274` (SearchSimilar): `ReadableDecayField` and `EligiblePopularitySignal` replace the raw decay field and `popularityPossible`;
  - `:313`: `PopularityFor` takes the eligible signal;
  - `:459-481` (`WriteSimilarResponsesAsync`): the column lookup becomes the response's allow-list; unknown keys are dropped instead of passed through as `UpperFirst(key)`;
  - `:681-685` (`SearchChunksFusedAsync`, shared by SearchChunks and SearchSimilar-via-chunks): the same two helpers;
  - `:985-998` (`RetrievePopularityOrDegradeAsync`): takes the eligible signal instead of the schema;
  - `:1046-1069` (`PopularityFor`): takes the eligible signal; the two new private helpers follow it, before `// ── Helpers`.
- Test (create): `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalEligibilityTests.cs` — the predicate's cases.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`:
  - re-fixture the 8 popularity ranking tests (`:2732-2739`, `:2833-2835`, `:2890-2892`, `:2950-2952`, `:3009-3011`, `:3074-3076`, `:4719-4732`, `:4775-4788`) through a new helper that registers Article with an eligible `Author` signal;
  - append the read-side gate, allow-list and decay tests at the end of the class.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs` (container suite) — a `RegisterArticleWithCommentsAsync` helper before `MakeStream`, used by `SearchSimilar_PromotesLowerCosineButMorePopularCandidate_WhenWPopularityRaised` (`:374-378`) and `SearchChunks_ReflectsPopularitySignal_ForConfiguredType` (`:423-428`).
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs` — append the collision theory and the ineligible-signal warning.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs` — append an updater test and a dispatch test.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs` — one sweep test, inserted before the per-row isolation test (`:163`).

**Interfaces:**
- Consumes: nothing from Tasks 1–3. None of them edits the files above. (Task 1 changes `SchemaRegistry.LoadAsync`, which these tests do not go through; they register with `RegisterAsync`.)
- Produces:
  - `internal static class PopularitySignalEligibility` (namespace `Iverson.Api.Grpc`) with `IsEligible(SchemaDescriptor parent, RelationDescriptor relation, SchemaRegistry registry)` and `IsEligible(SchemaDescriptor parent, RelationDescriptor relation, SchemaDescriptor? child)`.
  - In `ObjectSearchGrpcService.cs`: new private members `EligiblePopularitySignal(SchemaDescriptor)` and `ReadableDecayField(SchemaDescriptor, AuthorizationDecision)`; changed signatures `PopularityFor(PopularitySignalEntry?, …)` and `RetrievePopularityOrDegradeAsync(string, IReadOnlyList<ulong>, PopularitySignalEntry?, …)`. Tasks 10 and 11 edit other hunks of this file (the gRPC `Unavailable` mapping and Aggregate's `Task.WhenAll`), and their quotes must match the file as this task leaves it. This task touches only SearchSimilar, SearchChunks, `SearchChunksFusedAsync`, `WriteSimilarResponsesAsync` and the popularity helpers.
  - `ObjectSearchGrpcServiceTests.cs` gains helpers at the end of the class (`RegisterArticleCountingAuthorsAsync`, `AuthorReadableOnlyByEditors`, `DecayColumnReadableOnlyByEditors`, `DatedChunk`). A later task that appends to this class must anchor on the class's last lines as this task leaves them.
- Line numbers are at `6b942e1c`.

- [ ] **Step 1: Write the failing tests for the gates, the allow-list and decay** (everything except the predicate's own class, which cannot compile until Step 5)

**1a. Re-fixture the eight popularity ranking tests** (`ObjectSearchGrpcServiceTests.cs`). Each configures `PopularitySignalEntry("Article", "Author")` and plants `authorCount`/`authorCountBuckets` payload keys. `SchemaFixtures.ArticleSchema()` declares `Author` as ManyToOne and registers no `Author` child, so under the new predicate the signal would be ineligible and every one of these tests would lose its popularity term. Each now registers through `RegisterArticleCountingAuthorsAsync()` (added in 1b). It re-declares `Author` as OneToMany to a registered `Author` child carrying the same bypass role, which keeps every payload key and expected score unchanged.

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    // FIX 3 regression: neither centroid nor decay can be present (ArticleSchema, Title is
    // embedding-only and the schema has no timestamp metadata column) but a PopularitySignalEntry
    // IS configured for the type — popularityPossible must alone keep rerankIsIdentity false and
    // preserve the 4x over-fetch, exactly like the decay-only case above does for decayField.
    [Fact]
    public async Task SearchSimilar_NoCentroidOrDecayButPopularityConfigured_StillOverFetchesFourTimesTopK()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    // FIX 3 regression: neither centroid nor decay can be present (ArticleSchema, Title is
    // embedding-only and the schema has no timestamp metadata column) but an eligible
    // PopularitySignalEntry IS configured for the type — the popularity signal must alone keep
    // rerankIsIdentity false and preserve the 4x over-fetch, exactly like the decay-only case above
    // does for decayField.
    [Fact]
    public async Task SearchSimilar_NoCentroidOrDecayButPopularityConfigured_StillOverFetchesFourTimesTopK()
    {
        await RegisterArticleCountingAuthorsAsync();
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    public async Task SearchSimilar_PopularityRecencyBoostZero_IgnoresBucketSeriesEntirely()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    public async Task SearchSimilar_PopularityRecencyBoostZero_IgnoresBucketSeriesEntirely()
    {
        await RegisterArticleCountingAuthorsAsync();
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    public async Task SearchSimilar_PopularityRecencyBoostPositive_RanksRecentSeriesAboveEqualCountAncientSeries()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    public async Task SearchSimilar_PopularityRecencyBoostPositive_RanksRecentSeriesAboveEqualCountAncientSeries()
    {
        await RegisterArticleCountingAuthorsAsync();
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    public async Task SearchSimilar_PopularityMissingBucketsKeyWithRecencyBoostEnabled_UsesPlainCountFormula()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    public async Task SearchSimilar_PopularityMissingBucketsKeyWithRecencyBoostEnabled_UsesPlainCountFormula()
    {
        await RegisterArticleCountingAuthorsAsync();
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    public async Task SearchChunks_PopularityRecencyBoostZero_IgnoresBucketSeriesEntirely()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    public async Task SearchChunks_PopularityRecencyBoostZero_IgnoresBucketSeriesEntirely()
    {
        await RegisterArticleCountingAuthorsAsync();
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    public async Task SearchChunks_PopularityMissingBucketsKeyWithRecencyBoostEnabled_UsesPlainCountFormula()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    public async Task SearchChunks_PopularityMissingBucketsKeyWithRecencyBoostEnabled_UsesPlainCountFormula()
    {
        await RegisterArticleCountingAuthorsAsync();
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    public async Task SearchSimilar_RecencyBoost_IsRejectedAtStartupOrEmitsOnlyFiniteScores(
        double recencyBoost, bool expectRejected)
    {
        var popularity = BindRecencyBoostOrRejected(recencyBoost);
        if (expectRejected)
        {
            popularity.Should().BeNull("a RecencyBoost past the bound must fail loudly at startup");
            return;
        }
        popularity.Should().NotBeNull();

        popularity!.Signals = [new PopularitySignalEntry("Article", "Author")];
        popularity.SaturationPoint = 100.0;
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    public async Task SearchSimilar_RecencyBoost_IsRejectedAtStartupOrEmitsOnlyFiniteScores(
        double recencyBoost, bool expectRejected)
    {
        var popularity = BindRecencyBoostOrRejected(recencyBoost);
        if (expectRejected)
        {
            popularity.Should().BeNull("a RecencyBoost past the bound must fail loudly at startup");
            return;
        }
        popularity.Should().NotBeNull();

        popularity!.Signals = [new PopularitySignalEntry("Article", "Author")];
        popularity.SaturationPoint = 100.0;
        await RegisterArticleCountingAuthorsAsync();
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
    public async Task SearchChunks_RecencyBoost_IsRejectedAtStartupOrEmitsOnlyFiniteScores(
        double recencyBoost, bool expectRejected)
    {
        var popularity = BindRecencyBoostOrRejected(recencyBoost);
        if (expectRejected)
        {
            popularity.Should().BeNull("a RecencyBoost past the bound must fail loudly at startup");
            return;
        }
        popularity.Should().NotBeNull();

        popularity!.Signals = [new PopularitySignalEntry("Article", "Author")];
        popularity.SaturationPoint = 100.0;
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
```
with:
```csharp
    public async Task SearchChunks_RecencyBoost_IsRejectedAtStartupOrEmitsOnlyFiniteScores(
        double recencyBoost, bool expectRejected)
    {
        var popularity = BindRecencyBoostOrRejected(recencyBoost);
        if (expectRejected)
        {
            popularity.Should().BeNull("a RecencyBoost past the bound must fail loudly at startup");
            return;
        }
        popularity.Should().NotBeNull();

        popularity!.Signals = [new PopularitySignalEntry("Article", "Author")];
        popularity.SaturationPoint = 100.0;
        await RegisterArticleCountingAuthorsAsync();
```

**1b. Append the helpers and the new search tests** at the end of `ObjectSearchGrpcServiceTests`.

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:
```csharp
        written[0].Score.Should().BeGreaterThan(0.9f);
    }
}
```
with:
```csharp
        written[0].Score.Should().BeGreaterThan(0.9f);
    }

    // ── CSR round 12 #8: popularity is ranked on only when eligible ──────────────

    // SchemaFixtures.ArticleSchema declares "Author" as ManyToOne, and a signal over a relation that
    // is not OneToMany, or whose child is not registered, is never eligible. The popularity tests in
    // this class configure ("Article", "Author") and plant "authorCount"/"authorCountBuckets" payload
    // keys, so this re-declares that relation as OneToMany and registers its Author child. The
    // default child carries the same bypass role as Article, so every Article reader can read every
    // counted row and the signal is eligible. Pass a child with other read rules to make it
    // ineligible.
    private async Task RegisterArticleCountingAuthorsAsync(SchemaDescriptor? author = null)
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with
        {
            Relations =
            [
                new Iverson.Api.Schema.RelationDescriptor(
                    "Author", Iverson.Api.Schema.RelationKind.OneToMany, "Author", "ArticleId")
            ]
        });
        await _registry.RegisterAsync(author ?? SchemaFixtures.AuthorSchema());
    }

    // Every Article reader holds "test-bypass"; this child's rows are readable only by "editors".
    private static SchemaDescriptor AuthorReadableOnlyByEditors() => SchemaFixtures.AuthorSchema() with
    {
        Authorization = new Iverson.Api.Schema.AuthorizationRules(
            null, new List<Iverson.Api.Schema.RowPermission> { new("editors", true, true, true) }, [])
    };

    // The signal is configured but not eligible: a caller who can read an Article could not read
    // every Author row it counts. It is not ranked on (the score is the base score), and it does
    // not keep the over-fetch alive either (exactly top_k is fetched).
    [Fact]
    public async Task SearchSimilar_PopularitySignalNotEligible_IsNotRankedOn()
    {
        await RegisterArticleCountingAuthorsAsync(AuthorReadableOnlyByEditors());

        var sut = new ObjectSearchGrpcService(
            _registry, _search, _vector, _resolver,
            NullLogger<ObjectSearchGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator, new IntelligenceTenantScope("test-signing-key-0123456789abcdef"),
            new ResultReranker(Options.Create(new VectorRankingOptions { WBase = 0.45, WPopularity = 5.0 })),
            new ResultDiversifier(),
            Options.Create(new VectorRankingOptions { LambdaSimilar = 0.70, LambdaChunks = 0.70 }),
            Options.Create(new DecayOptions()),
            Options.Create(new PopularitySignalOptions
            {
                Signals         = [new PopularitySignalEntry("Article", "Author")],
                SaturationPoint = 100.0
            }),
            EngagementQueryLimitOptions.Default);

        var fakeVector = UnitVector();
        _embedding.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);
        var results = new List<VectorSearchResult>
        {
            new(1, 0.80, new Dictionary<string, string> { ["title"] = "a1", ["authorCount"] = "7" })
        };
        _vector.SearchNamedAsync("articles_test_tenant_dwsdnzpm8ad7tqdkswqfpfec1", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 1 },
            writer, TestServerCallContext.Create());

        CapturedLimit(_vector).Should().Be(1);
        written.Should().HaveCount(1);
        written[0].Score.Should().Be(0.80f);
    }

    // The chunk path's batched popularity lookup follows the same predicate: an ineligible signal
    // never even reads the parents' payloads.
    [Fact]
    public async Task SearchChunks_PopularitySignalNotEligible_IsNotRankedOn()
    {
        await RegisterArticleCountingAuthorsAsync(AuthorReadableOnlyByEditors());

        var sut = new ObjectSearchGrpcService(
            _registry, _search, _vector, _resolver,
            NullLogger<ObjectSearchGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator, new IntelligenceTenantScope("test-signing-key-0123456789abcdef"),
            new ResultReranker(Options.Create(new VectorRankingOptions { WBase = 0.45, WPopularity = 5.0 })),
            new ResultDiversifier(),
            Options.Create(new VectorRankingOptions { LambdaSimilar = 0.70, LambdaChunks = 0.70 }),
            Options.Create(new DecayOptions()),
            Options.Create(new PopularitySignalOptions
            {
                Signals         = [new PopularitySignalEntry("Article", "Author")],
                SaturationPoint = 100.0
            }),
            EngagementQueryLimitOptions.Default);

        var queryVector = UnitVector();
        _embedding.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(queryVector);
        const string parent = "parent-1";
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>
               {
                   new(1, 0.70, new Dictionary<string, string> { ["text"] = "c1", ["parent_id"] = parent })
               }.AsReadOnly());
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>());
        _vector.RetrievePayloadAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>())
               .Returns((IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>>)
                   new Dictionary<ulong, IReadOnlyDictionary<string, string>>
                   {
                       [InvokeKeyToUlong(parent)] = new Dictionary<string, string> { ["authorCount"] = "9" }
                   });

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 1 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Score.Should().Be(0.70f);
        await _vector.DidNotReceiveWithAnyArgs().RetrievePayloadAsync(default!, default!);
    }

    // SearchSimilar builds each response object from an allow-list: the descriptor's columns plus the
    // identity key. A count the worker wrote onto the point, its bucket series, and a key left over
    // from a since-removed column or signal are all dropped, never passed through.
    [Fact]
    public async Task SearchSimilar_StreamsOnlyDescriptorColumnsAndTheKey()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);
        var results = new List<VectorSearchResult>
        {
            new(1, 0.80, new Dictionary<string, string>
            {
                ["key"]                  = "article-1",
                ["title"]                = "a1",
                ["authorCount"]          = "7",
                ["authorCountBuckets"]   = "2026-01:7",
                ["retiredColumn"]        = "left over"
            })
        };
        _vector.SearchNamedAsync("articles_test_tenant_dwsdnzpm8ad7tqdkswqfpfec1", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 1 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Data.Fields.Keys.Should().BeEquivalentTo(["Id", "Title"]);
    }

    // ── CSR round 12 #9: decay only from a readable decay column ─────────────────

    // Rows of every caller in this class are readable through "test-bypass", but PublishedAt is
    // readable only by "editors", so the caller's AllowedFields excludes the decay column.
    private static Iverson.Api.Schema.AuthorizationRules DecayColumnReadableOnlyByEditors() => new(
        null,
        new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
        new List<Iverson.Api.Schema.FieldPermission> { new("PublishedAt", ["editors"], []) });

    private static VectorSearchResult DatedChunk(ulong id, double score, string parentKey, DateTimeOffset publishedAt) =>
        new(id, score, new Dictionary<string, string>
        {
            ["text"]        = $"chunk-of-{parentKey}",
            ["parent_id"]   = parentKey,
            ["publishedAt"] = publishedAt.ToString("O")
        });

    // Two candidates with the same base score whose timestamps are a year apart. Decay would separate
    // them and so reveal the order of a column the caller cannot read. With decay disabled, and no
    // centroid or popularity signal, the fused score is exactly the base score (ResultReranker's
    // no-other-signal short circuit), so both score 0.80.
    [Fact]
    public async Task SearchSimilar_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled()
    {
        await _registry.RegisterAsync(EmbeddingOnlyWithDecaySchema() with { Authorization = DecayColumnReadableOnlyByEditors() });

        var fakeVector = UnitVector();
        _embedding.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);
        var now = DateTimeOffset.UtcNow;
        var results = new List<VectorSearchResult>
        {
            new(1, 0.80, new Dictionary<string, string> { ["title"] = "fresh", ["publishedAt"] = now.ToString("O") }),
            new(2, 0.80, new Dictionary<string, string> { ["title"] = "old", ["publishedAt"] = now.AddDays(-365).ToString("O") })
        };
        _vector.SearchNamedAsync("dated_test_tenant_dwsdnzpm8ad7tqdkswqfpfec1", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Dated", Property = "Title", Query = "q", TopK = 2 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written.Select(w => w.Score).Should().Equal(0.80f, 0.80f);
    }

    [Fact]
    public async Task SearchChunks_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled()
    {
        await _registry.RegisterAsync(DualAnnotatedWithDecaySchema() with { Authorization = DecayColumnReadableOnlyByEditors() });

        var fakeVector = UnitVector();
        _embedding.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);
        StubNoCentroids();
        var now = DateTimeOffset.UtcNow;
        _vector.SearchNamedAsync("docs_chunks_test_tenant_dwsdnzpm8ad7tqdkswqfpfec1", "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>
               {
                   DatedChunk(1, 0.70, "parent-fresh", now),
                   DatedChunk(2, 0.70, "parent-old", now.AddDays(-365))
               }.AsReadOnly());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Doc", Property = "Body", Query = "q", TopK = 2 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written.Select(w => w.Score).Should().Equal(0.70f, 0.70f);
    }

    // SearchSimilar routed via chunks shares SearchChunksFusedAsync with SearchChunks; pinned through
    // its own entry point all the same.
    [Fact]
    public async Task SearchSimilar_RoutedViaChunks_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled()
    {
        await _registry.RegisterAsync(DualAnnotatedWithDecaySchema() with { Authorization = DecayColumnReadableOnlyByEditors() });

        var fakeVector = UnitVector();
        _embedding.EmbedQueryAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);
        StubChunkDensity(objectCount: 10, chunkCount: 13);
        StubNoCentroids();
        var now = DateTimeOffset.UtcNow;
        _vector.SearchNamedAsync("docs_chunks_test_tenant_dwsdnzpm8ad7tqdkswqfpfec1", "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>
               {
                   DatedChunk(1, 0.90, "parent-fresh", now),
                   DatedChunk(2, 0.90, "parent-old", now.AddDays(-365))
               }.AsReadOnly());
        _vector.RetrievePayloadAsync("docs_test_tenant_dwsdnzpm8ad7tqdkswqfpfec1", Arg.Any<IReadOnlyList<ulong>>())
               .Returns((IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>>)
                   ParentPayloads(["parent-fresh", "parent-old"]));

        var (writer, written) = MakeStream<SearchResponse>();
        await RoutedSut().SearchSimilar(
            new SearchSimilarRequest { TypeName = "Doc", Property = "Body", Query = "q", TopK = 2 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written.Select(w => w.Score).Should().Equal(0.90f, 0.90f);
    }
}
```

**1c. The integration fixtures** (`ObjectSearchVectorIntegrationTests.cs`, container suite). Both popularity tests configure `("Article", "Comments")`, but `ArticleSchema` has no `Comments` relation, so the signal would be ineligible after Step 6. They pass today with or without this change; they are run only in Step 7.

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`, replace:
```csharp
    private static (IServerStreamWriter<T> writer, List<T> written) MakeStream<T>()
```
with:
```csharp
    // A popularity signal is ranked on only when it counts a registered OneToMany child that every
    // reader of the parent can read (PopularitySignalEligibility). ArticleSchema has no such
    // relation, so the popularity tests register Article with a "Comments" OneToMany relation, and
    // a Comment child carrying Article's own bypass role.
    private async Task RegisterArticleWithCommentsAsync(string collection)
    {
        var article = SchemaFixtures.ArticleSchema();
        await _registry.RegisterAsync(article with
        {
            CollectionName = collection,
            Relations =
            [
                .. article.Relations,
                new Iverson.Api.Schema.RelationDescriptor(
                    "Comments", Iverson.Api.Schema.RelationKind.OneToMany, "Comment", "ArticleId")
            ]
        });
        await _registry.RegisterAsync(new SchemaDescriptor
        {
            TypeName       = "Comment",
            TableName      = "comments",
            CollectionName = null,
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Body", "text", false), new ColumnDescriptor("ArticleId", "uuid", false)],
            FkColumns      = [new ForeignKeyDescriptor("ArticleId", "Article")],
            VectorFields   = [],
            ChunkFields    = [],
            Relations      =
            [
                new Iverson.Api.Schema.RelationDescriptor(
                    "Article", Iverson.Api.Schema.RelationKind.ManyToOne, "Article", "ArticleId")
            ],
            Authorization  = article.Authorization,
            TenantColumn   = "TenantId"
        });
    }

    private static (IServerStreamWriter<T> writer, List<T> written) MakeStream<T>()
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`, replace:
```csharp
    public async Task SearchSimilar_PromotesLowerCosineButMorePopularCandidate_WhenWPopularityRaised()
    {
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);
```
with:
```csharp
    public async Task SearchSimilar_PromotesLowerCosineButMorePopularCandidate_WhenWPopularityRaised()
    {
        var collection = UniqueName();
        await RegisterArticleWithCommentsAsync(collection);
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`, replace:
```csharp
        // lookup SearchSimilar's object-vector path applies directly.
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);
```
with:
```csharp
        // lookup SearchSimilar's object-vector path applies directly.
        var collection = UniqueName();
        await RegisterArticleWithCommentsAsync(collection);
```

**1d. Startup validation** (`PopularitySignalOptionsTests.cs`). `MinimalSchema` carries no Authorization, so every existing test's signal is eligible and none of them starts warning. `ValidateAtStartup_RecencyBoostPositive_ChildHasMarker_DoesNotWarn` keeps asserting no warning at all, which makes it the negative control for the new log.

In `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs`, replace:
```csharp
        var act = () => services.AddPopularitySignalOptions(config);

        act.Should().NotThrow();
    }
}
```
with:
```csharp
        var act = () => services.AddPopularitySignalOptions(config);

        act.Should().NotThrow();
    }

    // CSR round 12 #8: the worker writes the count onto the parent's own point under
    // "{relation camelCase}Count" and "…CountBuckets", and SearchSimilar maps a payload key onto
    // the column whose camelCase name it equals. A parent column with either name would be
    // overwritten by the count, and would return it to every reader of that column. Matched
    // case-insensitively.
    [Theory]
    [InlineData("CommentsCount")]
    [InlineData("commentscountbuckets")]
    public async Task ValidateAtStartup_SignalKeyMatchingAParentColumn_Throws(string columnName)
    {
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("Comments", RelationKind.OneToMany, "Comment", "ArticleId")])
            with { ScalarColumns = [new ColumnDescriptor(columnName, "BIGINT", true)] };
        var comment = MinimalSchema(
            "Comment",
            relations: [new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")]);
        var registry = await RegistryWith(article, comment);
        var options = OptionsWith("Article", "Comments");

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*'{columnName}'*'Comments'*");
    }

    // An ineligible signal (here an owner-scoped parent, read by any caller in the tenant) is not a
    // misconfiguration that stops the process: it is skipped at runtime, and said so once here.
    [Fact]
    public async Task ValidateAtStartup_SignalNotEligible_WarnsAndDoesNotThrow()
    {
        var bypass = new AuthorizationRules(null, [new RowPermission("readers", true, false, false)], []);
        var article = MinimalSchema(
            "Article",
            vectorFields: [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            relations: [new RelationDescriptor("Comments", RelationKind.OneToMany, "Comment", "ArticleId")])
            with { Authorization = bypass with { OwnerField = "AuthorId" } };
        var comment = MinimalSchema(
            "Comment",
            relations: [new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")])
            with { Authorization = bypass };
        var registry = await RegistryWith(article, comment);
        var options = OptionsWith("Article", "Comments");
        var logger = Substitute.For<ILogger>();

        var act = () => PopularitySignalValidator.ValidateAtStartup(
            options, registry, engagementEnabled: true, logger);

        act.Should().NotThrow();
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("'Comments'") && o.ToString()!.Contains("not eligible")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
```

**1e. The consumer and the sweep.** Their fixtures carry no Authorization, so the existing tests' signals stay eligible (the parent's Authorization is null).

In `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs`, replace:
```csharp
        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unavailable);
    }
}
```
with:
```csharp
        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unavailable);
    }

    // ── CSR round 12 #8: an ineligible signal is never computed ───────────────

    private static Iverson.Api.Schema.AuthorizationRules ReadableBy(string role) =>
        new(null, [new Iverson.Api.Schema.RowPermission(role, true, false, false)], []);

    // The child carries Authorization; the parent's rows are readable by a role the child's are
    // not, so a parent reader would learn a count of rows they cannot read. Skipped before any
    // StarRocks or Qdrant call.
    [Fact]
    public async Task UpdateAsync_IneligibleSignal_ReturnsSkippedWithoutCountingOrWriting()
    {
        StubCount(3);
        var article = ArticleSchema() with { Authorization = ReadableBy("staff") };

        var outcome = await BuildUpdater().UpdateAsync(
            article, new PopularitySignalEntry("Article", "Comments"),
            CommentSchema() with { Authorization = ReadableBy("editors") },
            article.Relations[0], ArticleId, TenantA);

        outcome.Should().Be(PopularityUpdateOutcome.Skipped);
        await _search.DidNotReceiveWithAnyArgs().AggregateAsync(
            default!, default, default!, default, default, default, default);
        await _vector.DidNotReceiveWithAnyArgs().SetPayloadAsync(
            default!, default, Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    // An owner-scoped parent is read by every caller in the tenant (each sees their own rows), so a
    // count of all of its children is never eligible, whatever roles the child grants.
    [Fact]
    public async Task Dispatch_OwnerScopedParent_SkipsTheSignalWithoutCountingOrWriting()
    {
        await _registry.RegisterAsync(ArticleSchema() with
        {
            Authorization = ReadableBy("readers") with { OwnerField = "OwnerId" }
        });
        await _registry.RegisterAsync(CommentSchema() with { Authorization = ReadableBy("readers") });
        StubCount(3);

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);
        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);

        await BuildSut(OptionsWith("Article", "Comments")).DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _search.DidNotReceiveWithAnyArgs().AggregateAsync(
            default!, default, default!, default, default, default, default);
        await _vector.DidNotReceiveWithAnyArgs().SetPayloadAsync(
            default!, default, Arg.Any<IReadOnlyDictionary<string, object>>());
    }
}
```

In `Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs`, replace:
```csharp
    // ── Per-row isolation: one parent's UpdateAsync throwing does not stop the sweep for the
```
with:
```csharp
    // ── CSR round 12 #8: the sweep goes through the same UpdateAsync, which skips an ineligible
    //    signal. Here the parent carries Authorization and the child none, so nobody could read the
    //    counted rows: no parent's count is computed or written. ────────────────────────────
    [Fact]
    public async Task SweepSignalAsync_IneligibleSignal_ComputesAndWritesNothing()
    {
        await _registry.RegisterAsync(ArticleSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null, [new Iverson.Api.Schema.RowPermission("readers", true, false, false)], [])
        });
        await _registry.RegisterAsync(CommentSchema());
        StubAggregate(1);

        var page = new[] { new KeyedTenantRow("article-1", TenantA) };
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-1", 500, Arg.Any<EntityAccess>()).Returns([]);

        await BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);

        await _search.DidNotReceiveWithAnyArgs().AggregateAsync(
            default!, default, default!, default, default, default, default);
        await _vector.DidNotReceiveWithAnyArgs().SetPayloadAsync(
            default!, default, Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    // ── Per-row isolation: one parent's UpdateAsync throwing does not stop the sweep for the
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.ObjectSearchGrpcServiceTests.|FullyQualifiedName~.PopularitySignalOptionsTests.|FullyQualifiedName~.PopularitySignalConsumerTests.|FullyQualifiedName~.PopularitySignalReconciliationWorkerTests."
```
Expected: 12 failures, and every other test passes, including the 8 re-fixtured ranking tests (10 cases):
- `ObjectSearchGrpcServiceTests`:
  - `SearchSimilar_PopularitySignalNotEligible_IsNotRankedOn`: limit 4, and the score has the popularity term;
  - `SearchChunks_PopularitySignalNotEligible_IsNotRankedOn`: the score has the popularity term, and `RetrievePayloadAsync` was called;
  - `SearchSimilar_StreamsOnlyDescriptorColumnsAndTheKey`: the keys also include `AuthorCount`, `AuthorCountBuckets` and `RetiredColumn`;
  - `SearchSimilar_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled`, `SearchChunks_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled` and `SearchSimilar_RoutedViaChunks_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled`: the two scores differ, because decay ran.
- `PopularitySignalOptionsTests`: both cases of `ValidateAtStartup_SignalKeyMatchingAParentColumn_Throws` (no throw), and `ValidateAtStartup_SignalNotEligible_WarnsAndDoesNotThrow` (no warning).
- `PopularitySignalConsumerTests`: `UpdateAsync_IneligibleSignal_ReturnsSkippedWithoutCountingOrWriting` (`Updated`) and `Dispatch_OwnerScopedParent_SkipsTheSignalWithoutCountingOrWriting` (aggregate called).
- `PopularitySignalReconciliationWorkerTests`: `SweepSignalAsync_IneligibleSignal_ComputesAndWritesNothing` (aggregate called).

- [ ] **Step 3: Write the predicate's tests**

Create `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalEligibilityTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

// CSR round 12 #8. A popularity signal counts CHILD rows onto the PARENT's Qdrant point, so it may be
// computed and ranked on only when every caller who can read the parent could read each counted
// child row. The cases follow RowFieldAuthorizationEvaluator's read semantics:
//  * a role reads every row in the tenant when a RowPermission grants it CanReadAll;
//  * an owner-scoped type is read by any caller, who sees their own rows and needs no role;
//  * a FieldPermission with a non-empty ReadableRoles hides that field from every other caller.
public class PopularitySignalEligibilityTests
{
    private static readonly RelationDescriptor Comments =
        new("Comments", RelationKind.OneToMany, "Comment", "ArticleId");

    private static AuthorizationRules ReadableBy(params string[] roles) =>
        new(null, roles.Select(role => new RowPermission(role, true, false, false)).ToList(), []);

    private static SchemaDescriptor Article(AuthorizationRules? authorization) => new()
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = "articles",
        KeyColumn      = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns  = [new ColumnDescriptor("Title", "TEXT", false)],
        FkColumns      = [],
        VectorFields   = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
        ChunkFields    = [],
        Relations      = [Comments],
        Authorization  = authorization,
        TenantColumn   = SchemaDescriptor.TenantColumnName
    };

    // PostedAt is the child's [IversonPopularitySignal] column, which the recency series reads.
    private static SchemaDescriptor Comment(AuthorizationRules? authorization) => new()
    {
        TypeName      = "Comment",
        TableName     = "comments",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Body", "TEXT", false),
            new ColumnDescriptor("ArticleId", "UUID", true),
            new ColumnDescriptor("PostedAt", "TIMESTAMPTZ", false)
        ],
        FkColumns              = [],
        VectorFields           = [],
        ChunkFields            = [],
        Relations              = [],
        Authorization          = authorization,
        TenantColumn           = SchemaDescriptor.TenantColumnName,
        PopularitySignalColumn = "PostedAt"
    };

    private static async Task<SchemaRegistry> RegistryWith(params SchemaDescriptor[] schemas)
    {
        var registry = new SchemaRegistry(Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance);
        foreach (var schema in schemas)
            await registry.RegisterAsync(schema);
        return registry;
    }

    // Nobody can read a parent with no Authorization, so its ranking reveals nothing to anyone.
    [Fact]
    public void IsEligible_ParentWithNoAuthorization_ReturnsTrue() =>
        PopularitySignalEligibility.IsEligible(Article(null), Comments, Comment(null)).Should().BeTrue();

    [Fact]
    public void IsEligible_OwnerScopedParent_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers") with { OwnerField = "AuthorId" }), Comments, Comment(ReadableBy("readers")))
            .Should().BeFalse();

    [Fact]
    public void IsEligible_ChildWithNoAuthorization_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(null))
            .Should().BeFalse();

    [Fact]
    public void IsEligible_EveryParentReaderReadsEveryChildRow_ReturnsTrue() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers")), Comments, Comment(ReadableBy("readers", "staff")))
            .Should().BeTrue();

    [Fact]
    public void IsEligible_AParentReaderThatCannotReadEveryChildRow_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers", "staff")), Comments, Comment(ReadableBy("readers")))
            .Should().BeFalse();

    // Roles compare ordinally, as the evaluator matches them to the caller's `groups` claim.
    [Fact]
    public void IsEligible_ChildRoleDifferingOnlyByCase_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(ReadableBy("Readers")))
            .Should().BeFalse();

    // A role without CanReadAll on the parent does not read it, so the child need not cover it.
    [Fact]
    public void IsEligible_ParentRoleWithoutCanReadAll_IsNotAParentReader_ReturnsTrue()
    {
        var parentRules = new AuthorizationRules(
            null, [new RowPermission("readers", true, false, false), new RowPermission("writers", false, true, true)], []);

        PopularitySignalEligibility.IsEligible(Article(parentRules), Comments, Comment(ReadableBy("readers")))
            .Should().BeTrue();
    }

    // Nor does a child role without CanReadAll read the child's rows.
    [Fact]
    public void IsEligible_ChildRoleWithoutCanReadAll_DoesNotCoverAParentReader_ReturnsFalse()
    {
        var childRules = new AuthorizationRules(null, [new RowPermission("readers", false, true, true)], []);

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(childRules))
            .Should().BeFalse();
    }

    // The count is keyed on the foreign key and the recency series on the signal column, so each
    // must be readable to every parent reader. Field names compare case-insensitively; an empty
    // ReadableRoles restricts nothing; a permission on any other field is irrelevant.
    [Theory]
    [InlineData("ArticleId", "editors", false)]
    [InlineData("articleid", "editors", false)]
    [InlineData("PostedAt", "editors", false)]
    [InlineData("ArticleId", "readers,editors", true)]
    [InlineData("ArticleId", "", true)]
    [InlineData("Body", "editors", true)]
    public void IsEligible_ChildFieldPermission_DecidesByTheForeignKeyAndTheSignalColumn(
        string fieldName, string readableRoles, bool expected)
    {
        var roles = readableRoles.Length == 0 ? [] : readableRoles.Split(',').ToList();
        var childRules = new AuthorizationRules(
            null,
            [new RowPermission("readers", true, false, false)],
            [new FieldPermission(fieldName, roles, ["editors"])]);

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, Comment(childRules))
            .Should().Be(expected);
    }

    // Only a OneToMany relation has child rows to count.
    [Fact]
    public void IsEligible_RelationNotOneToMany_ReturnsFalse() =>
        PopularitySignalEligibility.IsEligible(
                Article(ReadableBy("readers")), Comments with { Kind = RelationKind.ManyToOne }, Comment(ReadableBy("readers")))
            .Should().BeFalse();

    [Fact]
    public async Task IsEligible_ChildNotRegistered_ReturnsFalse()
    {
        var registry = await RegistryWith(Article(ReadableBy("readers")));

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, registry).Should().BeFalse();
    }

    [Fact]
    public async Task IsEligible_ChildResolvedThroughTheRegistry_ReturnsTrue()
    {
        var registry = await RegistryWith(Article(ReadableBy("readers")), Comment(ReadableBy("readers")));

        PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, registry).Should().BeTrue();
    }

    // Spec §3.1: "A missing child or a relation that isn't OneToMany makes the signal ineligible"
    // holds even for a parent with no Authorization.
    [Fact]
    public async Task IsEligible_ParentWithNoAuthorizationButNoRegisteredChild_ReturnsFalse()
    {
        var registry = await RegistryWith(Article(null));

        PopularitySignalEligibility.IsEligible(Article(null), Comments, registry).Should().BeFalse();
    }
}
```

- [ ] **Step 4: Run the predicate's tests and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.PopularitySignalEligibilityTests."
```
Expected: the build fails with `CS0103: The name 'PopularitySignalEligibility' does not exist in the current context`, and no test runs.

- [ ] **Step 5: Implement the predicate and its write-side gate**

Create `Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs`:
```csharp
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

/// <summary>
/// Whether a configured popularity signal may be computed and ranked on (CSR round 12 #8). The
/// signal counts CHILD rows and writes the count onto the PARENT's Qdrant point. Every caller who
/// can read the parent sees the ranking move with it, so the signal is eligible only when every
/// such caller could read each counted child row, including the two columns the count is built
/// from.
///
/// Stated in RowFieldAuthorizationEvaluator's read semantics, and statically: access only grows
/// with a caller's groups, so it is enough to check each role that reads the parent. Rows are
/// tenant-scoped on both sides, and so is the count, so tenancy adds nothing.
///
/// Called by PopularitySignalUpdater (the consumer and the sweep), by ObjectSearchGrpcService when
/// ranking, and by PopularitySignalValidator.ValidateAtStartup, which logs each ineligible signal.
/// </summary>
internal static class PopularitySignalEligibility
{
    internal static bool IsEligible(SchemaDescriptor parent, RelationDescriptor relation, SchemaRegistry registry) =>
        IsEligible(parent, relation, registry.Get(relation.RelatedTypeName));

    internal static bool IsEligible(SchemaDescriptor parent, RelationDescriptor relation, SchemaDescriptor? child)
    {
        // Only a OneToMany relation has child rows to count, and an unregistered child has no read
        // rules to judge them by.
        if (relation.Kind != RelationKind.OneToMany || child is null)
            return false;

        // Nobody can read a parent with no Authorization, so its ranking reveals nothing to anyone.
        if (parent.Authorization is not { } parentRules)
            return true;

        // An owner-scoped parent is read by any caller with a `sub` (each sees their own rows, with no
        // role needed), while the count covers every caller's child rows.
        if (parentRules.OwnerField is not null)
            return false;

        // Nobody can read a child with no Authorization.
        if (child.Authorization is not { } childRules)
            return false;

        // So every parent reader holds a CanReadAll role on the parent, and each such role must read
        // every child row too. Roles compare ordinally, as the evaluator matches the `groups` claim.
        var parentReaders = Readers(parentRules);
        if (!parentReaders.IsSubsetOf(Readers(childRules)))
            return false;

        // The count filters on the foreign key and the recency series reads PopularitySignalColumn,
        // so a FieldPermission that hides either must still admit every parent reader. Field names
        // compare case-insensitively, and an empty ReadableRoles restricts nothing.
        return childRules.FieldPermissions
            .Where(fp => fp.ReadableRoles.Count > 0 &&
                         (string.Equals(fp.FieldName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(fp.FieldName, child.PopularitySignalColumn, StringComparison.OrdinalIgnoreCase)))
            .All(fp => parentReaders.IsSubsetOf(fp.ReadableRoles));
    }

    private static HashSet<string> Readers(AuthorizationRules rules) =>
        rules.RowPermissions.Where(rp => rp.CanReadAll).Select(rp => rp.Role).ToHashSet(StringComparer.Ordinal);
}
```

In `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs`, replace:
```csharp
/// distinguish a real engagement-store failure from a designed no-op. <c>Skipped</c> covers the
/// documented degrade cases (unprovisioned tenant, Qdrant point not yet created) — folding them
/// into <c>Failed</c> would make the sweep abandon itself during normal operation.
```
with:
```csharp
/// distinguish a real engagement-store failure from a designed no-op. <c>Skipped</c> covers the
/// documented degrade cases (unprovisioned tenant, Qdrant point not yet created) and an ineligible
/// signal — folding them into <c>Failed</c> would make the sweep abandon itself during normal
/// operation.
```

In `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs`, replace:
```csharp
        SchemaRelationDescriptor relation, string parentKey, string? tenantId)
    {
        var query = new SearchQuery
```
with:
```csharp
        SchemaRelationDescriptor relation, string parentKey, string? tenantId)
    {
        // CSR round 12 #8: a count that some reader of the parent could not see row by row is never
        // computed. Silent here, because this runs per event and per swept parent;
        // PopularitySignalValidator.ValidateAtStartup logs each such signal once.
        if (!PopularitySignalEligibility.IsEligible(parentSchema, relation, childSchema))
            return PopularityUpdateOutcome.Skipped;

        var query = new SearchQuery
```

- [ ] **Step 6: Implement the read side, the allow-list, decay and the startup checks**

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:
```csharp
        var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);

        var popularityPossible = _popularitySignal.Signals.Any(s =>
            string.Equals(s.ParentType, schema.TypeName, StringComparison.OrdinalIgnoreCase));

        // When NEITHER signal can be present, the fused score provably equals the base score for
        // every candidate and the re-rank is a mathematical identity — Qdrant's own ordering is
        // already final. Over-fetching 4x then discarding 3/4 of the payloads (which carry the
        // full source text of every vector field) buys nothing, so fetch exactly topK. Whenever
        // either signal CAN be present the over-fetch stays exactly 4x with no ceiling.
        var rerankIsIdentity = !centroidPossible && decayField is null && !popularityPossible;
```
with:
```csharp
        var decayField = ReadableDecayField(schema, decision);

        var popularitySignal = EligiblePopularitySignal(schema);

        // When NEITHER signal can be present, the fused score provably equals the base score for
        // every candidate and the re-rank is a mathematical identity — Qdrant's own ordering is
        // already final. Over-fetching 4x then discarding 3/4 of the payloads (which carry the
        // full source text of every vector field) buys nothing, so fetch exactly topK. Whenever
        // either signal CAN be present the over-fetch stays exactly 4x with no ceiling.
        var rerankIsIdentity = !centroidPossible && decayField is null && popularitySignal is null;
```

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:
```csharp
            Popularity: PopularityFor(schema, r, _popularitySignal, now))).ToList();
```
with:
```csharp
            Popularity: PopularityFor(popularitySignal, r, _popularitySignal, now))).ToList();
```

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:
```csharp
        // Camel-cased descriptor name → descriptor, built once per request rather than per row.
        // Covers ScalarColumns and KeyColumn, plus the explicit "key" special case:
        // IntelligenceStoreConsumer.cs:417 writes the identity value under the literal payload key
        // "key", which ToCamelCase("Id") would never produce, so it cannot be derived from the
        // lookup and must be seeded directly. StructSerializer.UpperFirst below only fires for a
        // payload key with no matching descriptor column at all — e.g. a stale key left behind by
        // a since-removed column.
        var columnLookup = new Dictionary<string, ColumnDescriptor>(StringComparer.Ordinal);
        foreach (var col in schema.ScalarColumns)
            columnLookup[col.Name.ToCamelCase()] = col;
        columnLookup[schema.KeyColumn.Name.ToCamelCase()] = schema.KeyColumn;
        columnLookup["key"] = schema.KeyColumn;

        foreach (var (payload, score) in rows)
        {
            var protoStruct = new Struct();
            foreach (var kvp in payload)
            {
                if (columnLookup.TryGetValue(kvp.Key, out var col))
                    protoStruct.Fields[col.Name] = ConvertPayloadValue(kvp.Value, col.SqlType);
                else
                    protoStruct.Fields[StructSerializer.UpperFirst(kvp.Key)] = Value.ForString(kvp.Value);
            }
```
with:
```csharp
        // Camel-cased descriptor name → descriptor, built once per request rather than per row.
        // Covers ScalarColumns and KeyColumn, plus the explicit "key" special case:
        // IntelligenceStoreConsumer.cs:417 writes the identity value under the literal payload key
        // "key", which ToCamelCase("Id") would never produce, so it cannot be derived from the
        // lookup and must be seeded directly.
        //
        // The lookup is also the response's allow-list (CSR round 12 #8). Every other payload key is
        // dropped: the popularity counts the worker writes onto the point, keys left over from a
        // removed signal or a since-removed column, and any future server-side extra. No client
        // reads them.
        var columnLookup = new Dictionary<string, ColumnDescriptor>(StringComparer.Ordinal);
        foreach (var col in schema.ScalarColumns)
            columnLookup[col.Name.ToCamelCase()] = col;
        columnLookup[schema.KeyColumn.Name.ToCamelCase()] = schema.KeyColumn;
        columnLookup["key"] = schema.KeyColumn;

        foreach (var (payload, score) in rows)
        {
            var protoStruct = new Struct();
            foreach (var kvp in payload)
            {
                if (columnLookup.TryGetValue(kvp.Key, out var col))
                    protoStruct.Fields[col.Name] = ConvertPayloadValue(kvp.Value, col.SqlType);
            }
```

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:
```csharp
        var popularities = await RetrievePopularityOrDegradeAsync(
            tenantScope.ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks: false),
            parentIds, schema, _popularitySignal, now, rpcName);

        var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);
```
with:
```csharp
        var popularities = await RetrievePopularityOrDegradeAsync(
            tenantScope.ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks: false),
            parentIds, EligiblePopularitySignal(schema), _popularitySignal, now, rpcName);

        var decayField = ReadableDecayField(schema, decision);
```

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:
```csharp
    /// <summary>
    /// Batched popularity lookup for a chunk search's distinct parent ids, against the object
    /// collection's own payload (the same "<relation>Count" field the object-vector path reads
    /// via PopularityFor). A failure here degrades the ranking rather than failing the search:
    /// every popularity becomes ABSENT (never a substituted neutral value), same contract as
    /// RetrieveVectorsOrDegradeAsync.
    /// </summary>
    private async Task<IReadOnlyDictionary<ulong, double>> RetrievePopularityOrDegradeAsync(
        string collection, IReadOnlyList<ulong> parentIds, SchemaDescriptor schema,
        PopularitySignalOptions options, DateTimeOffset now, string rpcName)
    {
        var signal = options.Signals.FirstOrDefault(s =>
            string.Equals(s.ParentType, schema.TypeName, StringComparison.OrdinalIgnoreCase));
        if (signal is null || parentIds.Count == 0)
```
with:
```csharp
    /// <summary>
    /// Batched popularity lookup for a chunk search's distinct parent ids, against the object
    /// collection's own payload (the same "<relation>Count" field the object-vector path reads
    /// via PopularityFor). <paramref name="signal"/> is the type's eligible signal
    /// (EligiblePopularitySignal), or null, which reads nothing. A failure here degrades the
    /// ranking rather than failing the search: every popularity becomes ABSENT (never a
    /// substituted neutral value), same contract as RetrieveVectorsOrDegradeAsync.
    /// </summary>
    private async Task<IReadOnlyDictionary<ulong, double>> RetrievePopularityOrDegradeAsync(
        string collection, IReadOnlyList<ulong> parentIds, PopularitySignalEntry? signal,
        PopularitySignalOptions options, DateTimeOffset now, string rpcName)
    {
        if (signal is null || parentIds.Count == 0)
```

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:
```csharp
    /// <summary>
    /// Reads the raw relation count from the object point's own payload — written by
    /// PopularitySignalConsumer/PopularitySignalReconciliationWorker under "<relation>Count" —
    /// and squashes it into [0,1) via the standard saturating-count curve. Null when the type
    /// has no configured signal or the field is absent/unparseable (degrade, never substitute).
    /// </summary>
    private static double? PopularityFor(
        SchemaDescriptor schema, VectorSearchResult result, PopularitySignalOptions options, DateTimeOffset now)
    {
        var signal = options.Signals.FirstOrDefault(s =>
            string.Equals(s.ParentType, schema.TypeName, StringComparison.OrdinalIgnoreCase));
        if (signal is null) return null;
```
with:
```csharp
    /// <summary>
    /// Reads the raw relation count from the object point's own payload — written by
    /// PopularitySignalConsumer/PopularitySignalReconciliationWorker under "<relation>Count" —
    /// and squashes it into [0,1) via the standard saturating-count curve. Null when the type
    /// has no eligible signal (<paramref name="signal"/> is null) or the field is
    /// absent/unparseable (degrade, never substitute).
    /// </summary>
    private static double? PopularityFor(
        PopularitySignalEntry? signal, VectorSearchResult result, PopularitySignalOptions options, DateTimeOffset now)
    {
        if (signal is null) return null;
```

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:
```csharp
        return effective / (effective + options.SaturationPoint);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
```
with:
```csharp
        return effective / (effective + options.SaturationPoint);
    }

    /// <summary>
    /// The type's configured popularity signal when it is eligible (CSR round 12 #8), else null: a
    /// count is ranked on only when every caller who can read this type could read the child rows
    /// it counts (PopularitySignalEligibility). Checked here as well as where the count is written,
    /// because counts written before a signal became ineligible stay on the Qdrant points until
    /// each point is next re-upserted.
    /// </summary>
    private PopularitySignalEntry? EligiblePopularitySignal(SchemaDescriptor schema)
    {
        var signal = _popularitySignal.Signals.FirstOrDefault(s =>
            string.Equals(s.ParentType, schema.TypeName, StringComparison.OrdinalIgnoreCase));
        if (signal is null) return null;

        var relation = schema.Relations.FirstOrDefault(r =>
            string.Equals(r.PropertyName, signal.Relation, StringComparison.OrdinalIgnoreCase));
        return relation is not null && PopularitySignalEligibility.IsEligible(schema, relation, registry)
            ? signal
            : null;
    }

    /// <summary>
    /// The decay field, or null when the caller may not read it (CSR round 12 #9). Decay re-ranks
    /// on the column's value, so a caller whose AllowedFields excludes that column could otherwise
    /// read its order from the scores. AllowedFields compares case-insensitively, so the camelCase
    /// payload key DecayFieldResolver returns matches the column name as it is. Decided per
    /// caller, outside DecayFieldResolver's per-schema cache.
    /// </summary>
    private string? ReadableDecayField(SchemaDescriptor schema, AuthorizationDecision decision)
    {
        var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);
        return decayField is not null && decision.AllowedFields is not null && !decision.AllowedFields.Contains(decayField)
            ? null
            : decayField;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
```

In `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs`, replace:
```csharp
            if (!StoreTargeting.HasVectorOrChunkFields(parentSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' has no " +
                    "vector or chunk fields, so it has no Qdrant point to ever patch.");
```
with:
```csharp
            if (!StoreTargeting.HasVectorOrChunkFields(parentSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' has no " +
                    "vector or chunk fields, so it has no Qdrant point to ever patch.");

            // CSR round 12 #8: the count is written onto the parent's own point under these two keys,
            // and SearchSimilar maps a payload key onto the column whose camelCase name it equals. A
            // parent column with either name would be overwritten by the count and would return it
            // to every reader of that column.
            var countKey = signal.Relation.ToCamelCase() + "Count";
            var collidingColumn = parentSchema.ScalarColumns.Append(parentSchema.KeyColumn).FirstOrDefault(c =>
                string.Equals(c.Name, countKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, countKey + "Buckets", StringComparison.OrdinalIgnoreCase));
            if (collidingColumn is not null)
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: column '{collidingColumn.Name}' on " +
                    $"'{signal.ParentType}' has the name of a payload key the signal for relation " +
                    $"'{signal.Relation}' writes ('{countKey}' or '{countKey}Buckets'). Rename the " +
                    "column or remove the signal.");
```

In `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs`, replace:
```csharp
            if (!StoreTargeting.IsEngagementEligible(childSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: child type '{relation.RelatedTypeName}' is " +
                    "not StarRocks-eligible (it declares a OneToMany relation of its own), so its " +
                    "count can never be computed.");
```
with:
```csharp
            if (!StoreTargeting.IsEngagementEligible(childSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: child type '{relation.RelatedTypeName}' is " +
                    "not StarRocks-eligible (it declares a OneToMany relation of its own), so its " +
                    "count can never be computed.");

            // CSR round 12 #8. Not fatal: an ineligible signal is skipped at runtime by the writer and
            // the ranker alike, and re-registering either type can make it eligible.
            if (!PopularitySignalEligibility.IsEligible(parentSchema, relation, childSchema))
                logger.LogWarning(
                    "{Section}: the signal for relation '{Relation}' on '{ParentType}' is not eligible, " +
                    "so it is neither computed nor ranked on. A caller who can read '{Parent}' could not " +
                    "read every '{ChildType}' row it counts: the parent is owner-scoped, or one of its " +
                    "reading roles cannot read the child's rows, foreign key or [IversonPopularitySignal] " +
                    "column.",
                    PopularitySignalOptions.Section, signal.Relation, signal.ParentType, signal.ParentType,
                    relation.RelatedTypeName);
```

- [ ] **Step 7: Run the tests and see them pass**

The Step 2 command, plus the predicate's class:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.PopularitySignalEligibilityTests.|FullyQualifiedName~.ObjectSearchGrpcServiceTests.|FullyQualifiedName~.PopularitySignalOptionsTests.|FullyQualifiedName~.PopularitySignalConsumerTests.|FullyQualifiedName~.PopularitySignalReconciliationWorkerTests."
```
Expected: 0 failures. `PopularitySignalEligibilityTests` runs 18 cases (12 facts and a 6-case theory).

Then the container-free regression for the project:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures.

Then the Qdrant container suite (**heavy: starts a Qdrant container. Nothing else may run at the same time.**):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.ObjectSearchVectorIntegrationTests."
```
Expected: 7 passed. These include `SearchSimilar_PromotesLowerCosineButMorePopularCandidate_WhenWPopularityRaised` and `SearchChunks_ReflectsPopularitySignal_ForConfiguredType`, which are eligible through the Step 1c fixture. `SearchSimilar_ReturnsCanonicalDescriptorNamesAndTypedValues` and `SearchSimilar_EmitsTheKeyTheConsumerActuallyWrote_ForAMultiWordColumn` still find every asserted column under the allow-list.

- [ ] **Step 8: Mutation proof** (unit suites only; apply each mutation alone, run the Step 7 filtered command, record the failing tests, and undo the mutation by hand before the next)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | Owner-scoped parent | In `PopularitySignalEligibility.IsEligible`, `if (parentRules.OwnerField is not null)` → `if (false)` | `IsEligible_OwnerScopedParent_ReturnsFalse`, `Dispatch_OwnerScopedParent_SkipsTheSignalWithoutCountingOrWriting`, `ValidateAtStartup_SignalNotEligible_WarnsAndDoesNotThrow` |
| M2 | Parent readers ⊆ child readers | `if (!parentReaders.IsSubsetOf(Readers(childRules)))` → `if (false)` | `IsEligible_AParentReaderThatCannotReadEveryChildRow_ReturnsFalse`, `IsEligible_ChildRoleDifferingOnlyByCase_ReturnsFalse`, `IsEligible_ChildRoleWithoutCanReadAll_DoesNotCoverAParentReader_ReturnsFalse`, `UpdateAsync_IneligibleSignal_ReturnsSkippedWithoutCountingOrWriting`, `SearchSimilar_PopularitySignalNotEligible_IsNotRankedOn`, `SearchChunks_PopularitySignalNotEligible_IsNotRankedOn` |
| M3 | FK and signal-column permissions | `.All(fp => parentReaders.IsSubsetOf(fp.ReadableRoles));` → `.All(fp => true);` | `IsEligible_ChildFieldPermission_DecidesByTheForeignKeyAndTheSignalColumn` (cases `ArticleId,editors`, `articleid,editors`, `PostedAt,editors`) |
| M4 | Write-side gate | In `UpdateAsync`, `if (!PopularitySignalEligibility.IsEligible(parentSchema, relation, childSchema))` → `if (false)` | `UpdateAsync_IneligibleSignal_ReturnsSkippedWithoutCountingOrWriting`, `Dispatch_OwnerScopedParent_SkipsTheSignalWithoutCountingOrWriting`, `SweepSignalAsync_IneligibleSignal_ComputesAndWritesNothing` |
| M5 | Read-side gate | In `EligiblePopularitySignal`, `relation is not null && PopularitySignalEligibility.IsEligible(schema, relation, registry)` → `relation is not null` | `SearchSimilar_PopularitySignalNotEligible_IsNotRankedOn`, `SearchChunks_PopularitySignalNotEligible_IsNotRankedOn` |
| M6 | Allow-list | After the `if (columnLookup.TryGetValue(…))` assignment, add back `else protoStruct.Fields[StructSerializer.UpperFirst(kvp.Key)] = Value.ForString(kvp.Value);` | `SearchSimilar_StreamsOnlyDescriptorColumnsAndTheKey` |
| M7 | Decay from readable fields | In `ReadableDecayField`, `!decision.AllowedFields.Contains(decayField)` → `false` | `SearchSimilar_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled`, `SearchChunks_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled`, `SearchSimilar_RoutedViaChunks_CallerDeniedTheDecayColumn_ScoresAsWithDecayDisabled` |
| M8 | Startup collision | `if (collidingColumn is not null)` → `if (false)` | `ValidateAtStartup_SignalKeyMatchingAParentColumn_Throws` (both cases) |

The guards are independent, so each one gets a row; eight is past the usual four. Do not re-run the container suite under mutation.

- [ ] **Step 9: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add \
  Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs \
  Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs \
  Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs \
  Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalEligibilityTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs \
  Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs \
  Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs
git commit -m "rank on popularity only when every parent reader can read the counted rows, and decay only from readable columns

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Enrichment rules re-checked on reload, and the hint escaped

**Spec:** §4, §9 (§4 row) (closes the enrichment half of #18)

**Files:**
- Create: `Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs` — `Violation(SchemaDescriptor)`. It is the whole of `ValidateEnrichmentTargets`'s rule set, stated over the descriptor alone, and returns the first broken rule's message or null.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`:
  - `:176`: the call drops `typeDesc`;
  - `:625-752`: `ValidateEnrichmentTargets` throws `InvalidArgument` with the predicate's message, and its body moves to `EnrichmentTargetRules`.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`, `LoadAsync`. After the identifier/key-type refusal block (`:203-208`), a descriptor that breaks a rule is loaded with `EnrichmentTargets = []`, and an Error names the type.
- Modify: `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs:303-310`: the extraction prompt passes `target.Hint` through `EnrichmentPrompts.EscapeUntrustedText`. `ComputeHash` (`:358-371`) is unchanged and keeps hashing the raw hint.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — two theories before `SerializeAsRegistryWould` (`:763`).
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — two registration tests after `RegisterAsync_WithEmptyExtractHint_ThrowsInvalidArgument` (`:1210-1224`).
- Test (modify): `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs` — two tests after `HandleUpdated_EscapesForgedDelimiterInSourceText` (`:317-334`).

**Interfaces:**
- Consumes:
  - `SchemaRegistry.LoadAsync` as Task 1 leaves it. The anchor is the identifier/key-type refusal block that ends `"…failed identifier/key-type validation on rehydration and was NOT loaded.", typeName); continue; }`. This task inserts its block between that `}` and `_schemas[typeName] = descriptor;`. If Task 1 inserts its §2.2 name normalisation at the same point, put Task 1's block first and this one directly after it. Every comparison here is case-insensitive, so the order does not change any outcome.
  - `SchemaRegistrationOrchestrator.cs` as Task 1 leaves it. Task 1 adds refusals, but must not edit `ValidateEnrichmentTargets` or the line `ValidateEnrichmentTargets(typeDesc, descriptor);`.
- Produces:
  - `internal static class EnrichmentTargetRules` (namespace `Iverson.Api.Schema`) with `internal static string? Violation(SchemaDescriptor descriptor)`.
  - `SchemaRegistrationOrchestrator.ValidateEnrichmentTargets(SchemaDescriptor)` (private; was `(TypeDescriptor, SchemaDescriptor)`).
  - The `LoadAsync` block. Task 9 removes `EnsureTableAsync` from the top of `LoadAsync`, and Task 7 calls `LoadAsync` from the worker and the api; both match the file as this task leaves it.
  - Two new orchestrator tests that call `_sut.RegisterAsync` and read `_registry.Get`. Task 7 re-points this file's tests to the queued path, and must re-point these two as well. Validation stays in the api (spec §5.2), so `RegisterAsync_WithEnrichmentTargetWhoseOnlySourceIsTheDocumentTemplate_ThrowsInvalidArgument` still throws before anything is queued.
- Line numbers are at `6b942e1c`.

- [ ] **Step 1: Write the failing tests**

**1a. Reload** (`SchemaRegistryTests.cs`). Every case is a stored row that `SchemaFixtures.ArticleSchema()` turns into a rule-breaker. `Title` is its embedding source and `Body` its chunk source.

In `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs`, replace:
```csharp
    // Serializes exactly as SchemaRegistry.RegisterAsync does, so the fixtures above are real
```
with:
```csharp
    // ── CSR round 12 #18: the enrichment-target rules re-applied on rehydration ────────────────
    // Registration refuses a type that breaks one of the rules, but LoadAsync never passes through
    // registration. A stored row that breaks one (written before the rule existed, or altered in
    // storage) must not drive the enrichment pipeline. The type still loads, so every other
    // read, write and projection keeps working, but with no targets; EnrichmentConsumer returns
    // early for a type with none.

    // ArticleSchema plus a TEXT "Summary" column. Title (embedding) and Body (chunk) are its sources.
    private static SchemaDescriptor EnrichedArticle(params EnrichmentTarget[] targets)
    {
        var article = SchemaFixtures.ArticleSchema();
        return article with
        {
            ScalarColumns     = [.. article.ScalarColumns, new ColumnDescriptor("Summary", "text", true)],
            EnrichmentTargets = targets.Length > 0 ? targets : [new EnrichmentTarget("Summary", EnrichmentKind.Summary, null)]
        };
    }

    private static SchemaDescriptor EnrichedArticleBreaking(string rule)
    {
        var article = EnrichedArticle();
        var bypass  = article.Authorization!;
        return rule switch
        {
            "target-not-a-column" => EnrichedArticle(new EnrichmentTarget("Missing", EnrichmentKind.Summary, null)),
            "target-not-text"     => EnrichedArticle(new EnrichmentTarget("AuthorId", EnrichmentKind.Summary, null)),
            "target-is-the-key"   => EnrichedArticle(new EnrichmentTarget("Id", EnrichmentKind.Summary, null)),
            "target-is-a-source"  => EnrichedArticle(new EnrichmentTarget("Body", EnrichmentKind.Summary, null)),
            "extracted-no-hint"   => EnrichedArticle(new EnrichmentTarget("Summary", EnrichmentKind.Extracted, " ")),
            "target-is-the-tenant-column" => article with
            {
                ScalarColumns     = [.. article.ScalarColumns, new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false)],
                TenantColumn      = SchemaDescriptor.TenantColumnName,
                EnrichmentTargets = [new EnrichmentTarget(SchemaDescriptor.TenantColumnName, EnrichmentKind.Summary, null)]
            },
            // A document template adds a synthetic "Document" chunk field. Its text is rendered from
            // the template, not read as source text, so on its own it is no source.
            "only-source-is-the-synthetic-document" => article with
            {
                VectorFields           = [],
                ChunkFields            = [new ChunkDescriptor("Document", 512, 64, "nomic-embed-text", 768)],
                DocumentTemplate       = DocumentTemplateParser.Parse("{Title}"),
                DocumentTemplateSource = "{Title}"
            },
            "target-write-restricted" => article with
            {
                Authorization = bypass with { FieldPermissions = [new FieldPermission("Summary", [], ["editors"])] }
            },
            "target-more-readable-than-its-source" => article with
            {
                Authorization = bypass with { FieldPermissions = [new FieldPermission("Body", ["editors"], [])] }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
        };
    }

    [Theory]
    [InlineData("target-not-a-column")]
    [InlineData("target-not-text")]
    [InlineData("target-is-the-key")]
    [InlineData("target-is-a-source")]
    [InlineData("extracted-no-hint")]
    [InlineData("target-is-the-tenant-column")]
    [InlineData("only-source-is-the-synthetic-document")]
    [InlineData("target-write-restricted")]
    [InlineData("target-more-readable-than-its-source")]
    public async Task LoadAsync_DescriptorBreakingAnEnrichmentRule_LoadsWithNoTargetsAndLogsError(string rule)
    {
        var stored = EnrichedArticleBreaking(rule);
        _repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", SerializeAsRegistryWould(stored))
        });

        await _sut.LoadAsync();

        _sut.IsRegistered("Article").Should().BeTrue("breaking an enrichment rule disables enrichment, not the type");
        var loaded = _sut.Get("Article")!;
        loaded.EnrichmentTargets.Should().BeEmpty();
        loaded.ScalarColumns.Should().HaveCount(stored.ScalarColumns.Count);
        _logs.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("'Article'") &&
            e.Message.Contains("enrichment disabled"));
    }

    // The positive controls: without them, a LoadAsync that stripped every type's targets would
    // satisfy the theory above. TEXT[] is a string array, which the rules have always admitted, and
    // a template does not count against a type that has a real source as well.
    [Theory]
    [InlineData("text")]
    [InlineData("text-array")]
    [InlineData("template-beside-a-real-source")]
    public async Task LoadAsync_DescriptorKeepingTheEnrichmentRules_KeepsItsTargets(string shape)
    {
        var article = EnrichedArticle();
        var stored = shape switch
        {
            "text"       => article,
            "text-array" => article with
            {
                ScalarColumns     = [.. article.ScalarColumns, new ColumnDescriptor("Tags", "TEXT[]", true)],
                EnrichmentTargets = [new EnrichmentTarget("Tags", EnrichmentKind.Keywords, null)]
            },
            "template-beside-a-real-source" => article with
            {
                ChunkFields            = [.. article.ChunkFields, new ChunkDescriptor("Document", 512, 64, "nomic-embed-text", 768)],
                DocumentTemplate       = DocumentTemplateParser.Parse("{Title}"),
                DocumentTemplateSource = "{Title}"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
        _repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", SerializeAsRegistryWould(stored))
        });

        await _sut.LoadAsync();

        _sut.Get("Article")!.EnrichmentTargets.Should().BeEquivalentTo(stored.EnrichmentTargets);
        _logs.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    // Serializes exactly as SchemaRegistry.RegisterAsync does, so the fixtures above are real
```

**1b. Registration** (`SchemaRegistrationOrchestratorTests.cs`). Both tests pass today. They pin two places where the descriptor-only restatement could drift from today's `TypeDescriptor` checks: the synthetic `Document` chunk field, and string arrays.

In `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, replace:
```csharp
            { Name = "Extracted", ObjectType = ObjectType.String, ExtractHint = "   " });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }
```
with:
```csharp
            { Name = "Extracted", ObjectType = ObjectType.String, ExtractHint = "   " });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithEnrichmentTargetWhoseOnlySourceIsTheDocumentTemplate_ThrowsInvalidArgument()
    {
        // A document template adds a synthetic "Document" chunk field to the descriptor, but its
        // text is rendered from the template, not read as source text, so it is no source property.
        var td = new TypeDescriptor { TypeName = "Widget", DocumentTemplate = "{Body}" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Summary", ObjectType = ObjectType.String, IsSummaryTarget = true });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("no source text property");
        _registry.Get("Widget").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithStringArrayEnrichmentTarget_Registers()
    {
        // A string array maps to TEXT[], which the target rule has always admitted.
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Body", ObjectType = ObjectType.String, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Tags", ObjectType = ObjectType.String, IsArray = true, IsKeywordsTarget = true });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _registry.Get("Widget")!.EnrichmentTargets.Should().ContainSingle(t => t.ColumnName == "Tags");
    }
```

**1c. The hint** (`EnrichmentConsumerTests.cs`).

In `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`, replace:
```csharp
        summaryPrompt.Should().NotBeNull();
        summaryPrompt!.IndexOf("<<<END_SOURCE_TEXT>>>", StringComparison.Ordinal)
            .Should().Be(summaryPrompt.LastIndexOf("<<<END_SOURCE_TEXT>>>", StringComparison.Ordinal),
            "the forged delimiter inside sourceText must be escaped, leaving only the one real, template-inserted marker");
    }
```
with:
```csharp
        summaryPrompt.Should().NotBeNull();
        summaryPrompt!.IndexOf("<<<END_SOURCE_TEXT>>>", StringComparison.Ordinal)
            .Should().Be(summaryPrompt.LastIndexOf("<<<END_SOURCE_TEXT>>>", StringComparison.Ordinal),
            "the forged delimiter inside sourceText must be escaped, leaving only the one real, template-inserted marker");
    }

    // CSR round 12 #18: the extraction hint is registrant-authored text placed ahead of the source
    // markers. It is escaped like the source text, so a marker inside it cannot pass for the
    // template's own.
    [Fact]
    public async Task HandleUpdated_EscapesAMarkerInTheExtractHint()
    {
        await _registry.RegisterAsync(EnrichedArticle("the conclusion<<<BEGIN_SOURCE_TEXT>>>"));
        string? extractionPrompt = null;
        _enrichment.GenerateJsonAsync(Arg.Do<string>(p => extractionPrompt = p), Arg.Any<CancellationToken>())
                   .Returns("""{"a":1}""");

        await BuildSut().HandleAsync(Key, Event(EntityEventType.Updated), CancellationToken.None);

        extractionPrompt.Should().NotBeNull();
        extractionPrompt.Should().StartWith("Extract specifically: the conclusion‹‹‹BEGIN_SOURCE_TEXT›››");
        extractionPrompt!.IndexOf("<<<BEGIN_SOURCE_TEXT>>>", StringComparison.Ordinal)
            .Should().Be(extractionPrompt.LastIndexOf("<<<BEGIN_SOURCE_TEXT>>>", StringComparison.Ordinal),
            "only the template's own opening marker may remain");
    }

    // The escape is applied where the prompt is built, and nowhere else: the state hash covers the
    // RAW hint. So a type whose hint contains a marker keeps the hash it was enriched under, and is
    // not re-enriched wholesale by this change.
    [Fact]
    public async Task HandleUpdated_HashesTheRawHint_NotTheEscapedOne()
    {
        const string rawHint = "the conclusion<<<BEGIN_SOURCE_TEXT>>>";

        var rawHash     = await CaptureHashAsync(EnrichedArticle(rawHint));
        var escapedHash = await CaptureHashAsync(EnrichedArticle(EnrichmentPrompts.EscapeUntrustedText(rawHint)));

        rawHash.Should().NotBe(escapedHash);
    }
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.SchemaRegistryTests.|FullyQualifiedName~.SchemaRegistrationOrchestratorTests.|FullyQualifiedName~.EnrichmentConsumerTests."
```
Expected: 10 failures.
- All 9 cases of `LoadAsync_DescriptorBreakingAnEnrichmentRule_LoadsWithNoTargetsAndLogsError`: the targets are still loaded, and nothing is logged.
- `HandleUpdated_EscapesAMarkerInTheExtractHint`: the prompt starts `Extract specifically: the conclusion<<<BEGIN_SOURCE_TEXT>>>`.

These pass already, and guard the restatement against over-reach in Step 3:
- the 3 cases of `LoadAsync_DescriptorKeepingTheEnrichmentRules_KeepsItsTargets`;
- `RegisterAsync_WithEnrichmentTargetWhoseOnlySourceIsTheDocumentTemplate_ThrowsInvalidArgument`;
- `RegisterAsync_WithStringArrayEnrichmentTarget_Registers`;
- `HandleUpdated_HashesTheRawHint_NotTheEscapedOne`.

- [ ] **Step 3: Implement**

Create `Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs`:
```csharp
namespace Iverson.Api.Schema;

/// <summary>
/// The enrichment-target rules, stated over a <see cref="SchemaDescriptor"/> alone (CSR round 12
/// #18), so that both admission paths apply the same ones:
///  * SchemaRegistrationOrchestrator.ValidateEnrichmentTargets refuses a registration that breaks
///    one, with the message returned here;
///  * SchemaRegistry.LoadAsync rehydrates stored rows without passing through registration, and
///    loads a stored descriptor that breaks one with no enrichment targets.
/// Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
/// are columns the enrichment pipeline writes to, not reads from; the rules keep that sound, and
/// keep a target's FieldPermissions consistent with the pipeline being its only writer and its
/// text being derived from the source properties.
/// </summary>
internal static class EnrichmentTargetRules
{
    // SchemaBuilder adds a chunk field of this name to a type with a document template. Its text is
    // rendered from the template, not read from a property, so it is not a source.
    private const string SyntheticDocumentField = "Document";

    /// <summary>
    /// The first rule <paramref name="descriptor"/> breaks, as a caller-facing message, or null when
    /// it breaks none. Pure: it reads nothing but the descriptor.
    /// </summary>
    internal static string? Violation(SchemaDescriptor descriptor)
    {
        if (descriptor.EnrichmentTargets.Count == 0)
            return null;

        var ownerField = descriptor.Authorization?.OwnerField;

        // Source text is the concatenation of the type's [IversonEmbedding]/[IversonChunk] properties
        // (EnrichmentConsumer.BuildSourceText) — nothing else counts. The descriptor records them as
        // its vector and chunk fields.
        var sourceNames = descriptor.VectorFields.Select(v => v.PropertyName)
            .Concat(descriptor.ChunkFields
                .Select(c => c.PropertyName)
                .Where(name => descriptor.DocumentTemplate is null ||
                               !string.Equals(name, SyntheticDocumentField, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var target in descriptor.EnrichmentTargets)
        {
            // The key is looked up too, so that a target naming it reaches the key rule below.
            // SchemaBuilder builds targets from non-key properties only, so at registration this
            // always finds a column; a stored row is arbitrary JSON.
            var column = descriptor.ScalarColumns.FirstOrDefault(c =>
                             string.Equals(c.Name, target.ColumnName, StringComparison.OrdinalIgnoreCase))
                         ?? (string.Equals(descriptor.KeyColumn.Name, target.ColumnName, StringComparison.OrdinalIgnoreCase)
                             ? descriptor.KeyColumn
                             : null);
            if (column is null)
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' is not a column " +
                       "of the type.";
            }

            // A string property maps to TEXT and a string array to TEXT[] (SchemaBuilder's type map);
            // no other property type does. Case-insensitive, like LoadAsync's UUID key check, because
            // hand-built descriptors spell SQL types in lower case.
            if (!string.Equals(column.SqlType, "TEXT", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(column.SqlType, "TEXT[]", StringComparison.OrdinalIgnoreCase))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' must be a string " +
                       $"property; its column type is '{column.SqlType}'.";
            }

            // The tenant clause cannot fire at registration, which rejects a declared property named
            // after the reserved tenant column before BuildDescriptor runs, but it can on a stored row.
            // The message names only what a caller can still collide with: tenant_field is no longer
            // accepted at all (RejectDeclaredTenantField).
            if (string.Equals(target.ColumnName, descriptor.KeyColumn.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.ColumnName, descriptor.TenantColumn, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(ownerField) &&
                 string.Equals(target.ColumnName, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot be the key " +
                       "or owner_field.";
            }

            if (sourceNames.Contains(target.ColumnName))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot also carry " +
                       "[IversonEmbedding]/[IversonChunk]: if this property were also a source property, the " +
                       "enrichment writeback would mutate the hashed text, causing the enricher to re-enrich its " +
                       "own republished event without bound.";
            }

            if (target.Kind == EnrichmentKind.Extracted && string.IsNullOrWhiteSpace(target.Hint))
            {
                return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' has [IversonExtracted] " +
                       "with an empty hint.";
            }
        }

        // A type with an enrichment target but no source property would hash an empty source text
        // and call the enrichment model with an empty prompt.
        if (sourceNames.Count == 0)
        {
            return $"'{descriptor.TypeName}' declares enrichment targets but has no source text property " +
                   "for the enrichment pipeline to read from.";
        }

        var targetNames = descriptor.EnrichmentTargets
            .Select(t => t.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The two rules below use RowFieldAuthorizationEvaluator's own comparisons: a
        // FieldPermission names a property case-insensitively, role names compare ordinally
        // (the way the caller's `groups` claim is matched), and an empty role list means
        // unrestricted, so only non-empty lists are acted on.
        var fieldPermissions = descriptor.Authorization?.FieldPermissions ?? [];

        // Write: the enrichment pipeline is a target's only writer, so a write restriction on
        // a target could never be enforced against anyone.
        var writeRestricted = fieldPermissions.FirstOrDefault(fp =>
            fp.WritableRoles.Count > 0 && targetNames.Contains(fp.FieldName));
        if (writeRestricted is not null)
        {
            return $"Enrichment target '{writeRestricted.FieldName}' on '{descriptor.TypeName}' carries a " +
                   "FieldPermission with writable_roles. The enrichment pipeline is the target's only writer, so " +
                   "a write restriction on it cannot be enforced; remove writable_roles from that FieldPermission.";
        }

        // Read: a target's text is generated from the source text, so every caller who can read
        // a target must be able to read every source. The evaluator excludes a field when ANY
        // FieldPermission naming it excludes the caller, so each restricted source permission
        // needs one target permission whose roles are a subset of its roles.
        foreach (var source in fieldPermissions.Where(fp =>
                     fp.ReadableRoles.Count > 0 && sourceNames.Contains(fp.FieldName)))
        {
            foreach (var target in descriptor.EnrichmentTargets)
            {
                var covered = fieldPermissions.Any(fp =>
                    fp.ReadableRoles.Count > 0 &&
                    string.Equals(fp.FieldName, target.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                    fp.ReadableRoles.All(role => source.ReadableRoles.Contains(role, StringComparer.Ordinal)));

                if (!covered)
                {
                    return $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' is generated from " +
                           $"source property '{source.FieldName}', whose readable_roles are restricted, but the " +
                           "target carries no FieldPermission whose readable_roles are a subset of them. Every " +
                           "caller who can read an enrichment target must be able to read every source it is " +
                           "generated from.";
                }
            }
        }

        return null;
    }
}
```

In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
            ValidateEnrichmentTargets(typeDesc, descriptor);
```
with:
```csharp
            ValidateEnrichmentTargets(descriptor);
```

In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
    // Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
    // are columns the enrichment pipeline writes to, not reads from. Five rules keep that
    // invariant sound at registration time rather than failing obscurely at ingest, and two more
    // keep a target's FieldPermissions consistent with that: the pipeline is its only writer, and
    // its text is derived from the source properties.
    private static void ValidateEnrichmentTargets(TypeDescriptor typeDesc, SchemaDescriptor descriptor)
    {
        var ownerField = descriptor.Authorization?.OwnerField;

        foreach (var target in descriptor.EnrichmentTargets)
        {
            var property = typeDesc.Properties.First(p =>
                string.Equals(p.Name, target.ColumnName, StringComparison.OrdinalIgnoreCase));

            if (property.ObjectType != ObjectType.String)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' must be a string " +
                    $"property; it is '{property.ObjectType}'."));
            }

            // The TenantColumn clause is UNREACHABLE BY CONSTRUCTION, and is kept for the same
            // reason as the __TenantId exclusion on the FK lookup above rather than deleted:
            // target.ColumnName is always a DECLARED property name (the First() lookup directly
            // above would throw otherwise), and RejectReservedTenantName rejects a declared
            // property named __TenantId before BuildDescriptor ever runs. It is the standing
            // statement that an enrichment target may never be the tenant column — the property
            // that keeps this check correct if the reserved-name guard is ever reordered or the
            // tenant column ever becomes client-addressable again. The user-facing message names
            // only the two things a caller can still actually collide with: tenant_field is no
            // longer accepted at all (RejectDeclaredTenantField), so naming it in the remedy would
            // point the caller at a field that no longer exists.
            if (property.IsKey ||
                string.Equals(target.ColumnName, descriptor.TenantColumn, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(ownerField) &&
                 string.Equals(target.ColumnName, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot be the key " +
                    "or owner_field."));
            }

            if (property.IsEmbedding || property.IsChunk)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot also carry " +
                    "[IversonEmbedding]/[IversonChunk]: if this property were also a source property, the " +
                    "enrichment writeback would mutate the hashed text, causing the enricher to re-enrich its " +
                    "own republished event without bound."));
            }

            if (target.Kind == EnrichmentKind.Extracted && string.IsNullOrWhiteSpace(target.Hint))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' has [IversonExtracted] " +
                    "with an empty hint."));
            }
        }

        if (descriptor.EnrichmentTargets.Count > 0)
        {
            var targetNames = descriptor.EnrichmentTargets
                .Select(t => t.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Source text is defined as the concatenation of the type's [IversonEmbedding]/
            // [IversonChunk] properties — nothing else counts. A type with an enrichment target
            // but no embedding/chunk property would hash an empty source text and call the
            // enrichment model with an empty prompt.
            var hasSourceProperty = typeDesc.Properties.Any(p => p.IsEmbedding || p.IsChunk);

            if (!hasSourceProperty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"'{descriptor.TypeName}' declares enrichment targets but has no source text property " +
                    "for the enrichment pipeline to read from."));
            }

            // The two rules below use RowFieldAuthorizationEvaluator's own comparisons: a
            // FieldPermission names a property case-insensitively, role names compare ordinally
            // (the way the caller's `groups` claim is matched), and an empty role list means
            // unrestricted, so only non-empty lists are acted on.
            var fieldPermissions = descriptor.Authorization?.FieldPermissions ?? [];

            // Write: the enrichment pipeline is a target's only writer, so a write restriction on
            // a target could never be enforced against anyone.
            var writeRestricted = fieldPermissions.FirstOrDefault(fp =>
                fp.WritableRoles.Count > 0 && targetNames.Contains(fp.FieldName));
            if (writeRestricted is not null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{writeRestricted.FieldName}' on '{descriptor.TypeName}' carries a " +
                    "FieldPermission with writable_roles. The enrichment pipeline is the target's only writer, so " +
                    "a write restriction on it cannot be enforced; remove writable_roles from that FieldPermission."));
            }

            // Read: a target's text is generated from the source text, so every caller who can read
            // a target must be able to read every source. The evaluator excludes a field when ANY
            // FieldPermission naming it excludes the caller, so each restricted source permission
            // needs one target permission whose roles are a subset of its roles.
            var sourceNames = typeDesc.Properties
                .Where(p => p.IsEmbedding || p.IsChunk)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var source in fieldPermissions.Where(fp =>
                         fp.ReadableRoles.Count > 0 && sourceNames.Contains(fp.FieldName)))
            {
                foreach (var target in descriptor.EnrichmentTargets)
                {
                    var covered = fieldPermissions.Any(fp =>
                        fp.ReadableRoles.Count > 0 &&
                        string.Equals(fp.FieldName, target.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                        fp.ReadableRoles.All(role => source.ReadableRoles.Contains(role, StringComparer.Ordinal)));

                    if (!covered)
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' is generated from " +
                            $"source property '{source.FieldName}', whose readable_roles are restricted, but the " +
                            "target carries no FieldPermission whose readable_roles are a subset of them. Every " +
                            "caller who can read an enrichment target must be able to read every source it is " +
                            "generated from."));
                    }
                }
            }
        }
    }
```
with:
```csharp
    // Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
    // are columns the enrichment pipeline writes to, not reads from. The rules that keep that sound
    // live in EnrichmentTargetRules, stated over the descriptor alone so that SchemaRegistry.LoadAsync
    // applies the same ones to stored rows (CSR round 12 #18). Registration refuses the first one
    // broken, rather than failing obscurely at ingest.
    private static void ValidateEnrichmentTargets(SchemaDescriptor descriptor)
    {
        if (EnrichmentTargetRules.Violation(descriptor) is { } violation)
            throw new RpcException(new Status(StatusCode.InvalidArgument, violation));
    }
```

In `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`, replace:
```csharp
                    logger.LogError(
                        "Schema '{TypeName}' failed identifier/key-type validation on rehydration and was NOT loaded.",
                        typeName);
                    continue;
                }
```
with:
```csharp
                    logger.LogError(
                        "Schema '{TypeName}' failed identifier/key-type validation on rehydration and was NOT loaded.",
                        typeName);
                    continue;
                }

                // The enrichment-target rules (CSR round 12 #18). Registration refuses a type that
                // breaks one, but rehydration never passes through registration, so a stored row that
                // breaks one (written before the rule existed, or altered in storage) would otherwise
                // drive the enrichment pipeline unchecked. The type still loads, so every other read,
                // write and projection keeps working, but with no targets: EnrichmentConsumer returns
                // early for a type with none.
                if (EnrichmentTargetRules.Violation(descriptor) is { } enrichmentViolation)
                {
                    logger.LogError(
                        "Schema '{TypeName}' loaded from storage breaks an enrichment-target rule and was " +
                        "loaded with enrichment disabled: {Violation} Re-register the type to restore enrichment.",
                        typeName, enrichmentViolation.SanitizeForLog());
                    descriptor = descriptor with { EnrichmentTargets = [] };
                }
```

In `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs`, replace:
```csharp
                    // EnrichmentPrompts.Extraction carries two slots: {0} is the per-target hint
                    // (mandatory for [IversonExtracted], enforced at registration), and {1} is the
                    // source text. The hint is positioned ahead of the text so the model knows what
                    // to pull out before reading the untrusted content.
                    try
                    {
                        generated = await enrichment.GenerateJsonAsync(
                            string.Format(EnrichmentPrompts.Extraction, target.Hint, EnrichmentPrompts.EscapeUntrustedText(sourceText)), ct);
```
with:
```csharp
                    // EnrichmentPrompts.Extraction carries two slots: {0} is the per-target hint
                    // (mandatory for [IversonExtracted], enforced at registration and on reload), and
                    // {1} is the source text. The hint is positioned ahead of the text so the model
                    // knows what to pull out before reading the untrusted content. The hint is
                    // registrant-authored, so it is escaped like the text (CSR round 12 #18) and
                    // cannot pass for one of the markers. Only here, where the prompt is built:
                    // ComputeHash keeps hashing the raw hint, so stored hashes stay valid.
                    try
                    {
                        generated = await enrichment.GenerateJsonAsync(
                            string.Format(
                                EnrichmentPrompts.Extraction,
                                EnrichmentPrompts.EscapeUntrustedText(target.Hint ?? string.Empty),
                                EnrichmentPrompts.EscapeUntrustedText(sourceText)),
                            ct);
```

- [ ] **Step 4: Run the tests and see them pass**

The Step 2 command. Expected: 0 failures, including every existing enrichment registration test in `SchemaRegistrationOrchestratorTests` (their assertions are on the status code or on message fragments the restatement keeps: "cannot be enforced", "a subset of them", "Notes"). Then the container-free regression:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures. No other test loads a stored descriptor with enrichment targets through `LoadAsync`. `EnrichmentConsumerTests.LegacyArticleRowJson` lacks a tenant column and is refused before this check.

- [ ] **Step 5: Mutation proof**

Before the first mutation, run Step 6's `git add …` command now (not the `git commit`), so that `git checkout -- <file>` restores this task's staged version. Apply each mutation alone, run the Step 2 command, record the failing tests, and revert with `git -C /home/ben/repositories/Iverson/.worktrees/csr12-server checkout -- <file>`.

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | Reload disables the targets | In `SchemaRegistry.LoadAsync`, delete `descriptor = descriptor with { EnrichmentTargets = [] };` | `LoadAsync_DescriptorBreakingAnEnrichmentRule_LoadsWithNoTargetsAndLogsError` (all 9 cases) |
| M2 | Synthetic `Document` is no source | In `EnrichmentTargetRules.Violation`, `.Where(name => descriptor.DocumentTemplate is null \|\|` → `.Where(name => true \|\|` | `LoadAsync_DescriptorBreakingAnEnrichmentRule_LoadsWithNoTargetsAndLogsError(rule: "only-source-is-the-synthetic-document")`, `RegisterAsync_WithEnrichmentTargetWhoseOnlySourceIsTheDocumentTemplate_ThrowsInvalidArgument` |
| M3 | TEXT[] admitted | `!string.Equals(column.SqlType, "TEXT[]", StringComparison.OrdinalIgnoreCase)` → `true` | `LoadAsync_DescriptorKeepingTheEnrichmentRules_KeepsItsTargets(shape: "text-array")`, `RegisterAsync_WithStringArrayEnrichmentTarget_Registers` |
| M4 | Hint escaped | In `EnrichmentConsumer`, `EnrichmentPrompts.EscapeUntrustedText(target.Hint ?? string.Empty),` → `target.Hint,` | `HandleUpdated_EscapesAMarkerInTheExtractHint` |

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add \
  Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs \
  Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
  Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs \
  Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs \
  Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
git commit -m "re-check enrichment target rules on schema reload and escape the extraction hint

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: The registry round trip through real Postgres

**Spec:** §9 "§5, first" (CDR-1 §3.2 A). Runs before the worker work: Task 7's payload carries descriptors serialised with the registry's JSON options, so the round trip must be pinned first.

**Files:**
- Create: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryPostgresRoundTripTests.cs`. A Postgres-only Testcontainers fixture (`postgres:18-alpine`, the pattern of `RegisterSchemaAuthorizationIntegrationTests.cs:38-40` and `DocumentRerenderQueuePostgresIntegrationTests.cs:19-41`) and one test: an owner-stamped descriptor built by `SchemaBuilder.BuildDescriptor` from a type that uses every descriptor member is written through `SchemaRegistry.RegisterAsync` → `SchemaRegistryRepository.UpsertAsync`, read back by a fresh `SchemaRegistry` over a fresh `PostgresRepository`, and compared whole (`OwnerTenantId` included), with an equal `SchemaBuilder.ToTableSchema` output.

**Interfaces:**
- Consumes: nothing from other tasks. Task 1 (name normalisation in `LoadAsync`) and Task 5 (the enrichment predicate in `LoadAsync`) run first; the fixture type spells every declared name exactly as its column and its one enrichment target is a `TEXT` column, so neither changes the loaded descriptor.
- Produces: nothing other tasks quote. The test calls `SchemaRegistryRepository.EnsureTableAsync()` itself, so it stays green after Task 9 moves that call out of `SchemaRegistry.LoadAsync`.
- New class name `SchemaRegistryPostgresRoundTripTests` is no substring of an existing class (`git grep -n "SchemaRegistryPostgresRoundTrip" -- '*.cs'` finds nothing at `6b942e1c`), and its filter `~.SchemaRegistryPostgresRoundTripTests.` matches no other class.

- [ ] **Step 1: Write the test**

Create `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryPostgresRoundTripTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public sealed class SchemaRegistryRoundTripPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// CSR round 12 §5 sends owner-stamped descriptors from the api to the worker serialised with the
/// registry's JSON options, and the worker writes them to <c>_iverson_schema</c>. That is only
/// sound if a descriptor survives the trip through Postgres whole, so this pins it: every member,
/// <see cref="SchemaDescriptor.OwnerTenantId"/> included, and the table the descriptor projects to.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class SchemaRegistryPostgresRoundTripTests(SchemaRegistryRoundTripPostgresFixture fixture)
    : IClassFixture<SchemaRegistryRoundTripPostgresFixture>
{
    private SchemaRegistryRepository FreshRepository() =>
        new(new PostgresRepository(fixture.ConnectionString, NullLogger<PostgresRepository>.Instance));

    // One type that reaches every SchemaDescriptor member SchemaBuilder fills: key and field
    // descriptions, search keys, an embedding (vector field + large field), a chunk (chunk field
    // with contextual set), a summary target, a metadata column, a popularity signal, a text array,
    // two foreign-key columns, a relation, row and field permissions, an owner field, a document
    // template (a synthetic chunk field) and a type description.
    private static SchemaDescriptor OwnerStampedDescriptor()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var td = new TypeDescriptor
        {
            TypeName           = "RoundTripArticle",
            Description        = "An article that exercises every descriptor member",
            DocumentTemplate   = "{Title}: {Body}",
            DocumentMaxTokens  = 384,
            DocumentOverlap    = 48,
            DocumentContextual = true,
            Authorization = new Client.Contracts.AuthorizationRules
            {
                OwnerField     = "OwnerId",
                RowPermissions = { new Client.Contracts.RowPermission { Role = "editors", CanReadAll = true, CanWriteAll = true } },
                FieldPermissions = { new Client.Contracts.FieldPermission { FieldName = "Summary", ReadableRoles = { "editors" } } }
            }
        };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true, Description = "The key" });
        td.Properties.Add(new PropertyDescriptor { Name = "Title", ObjectType = ObjectType.String, IsSearchKey = true, SearchKeyOrder = 1, Description = "Headline" });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ObjectType = ObjectType.String, IsChunk = true, ChunkMaxTokens = 256, ChunkOverlap = 32, ChunkContextual = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Abstract", ObjectType = ObjectType.String, IsEmbedding = true, IsNullable = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Summary", ObjectType = ObjectType.String, IsSummaryTarget = true, IsNullable = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Category", ObjectType = ObjectType.String, IsMetadata = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Tags", ObjectType = ObjectType.String, IsArray = true });
        td.Properties.Add(new PropertyDescriptor { Name = "ViewedAt", ObjectType = ObjectType.Datetime, IsPopularitySignal = true, IsNullable = true });
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor { Name = "RoundTripAuthorId", ObjectType = ObjectType.Guid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Author",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "RoundTripAuthor",
            ForeignKey   = "RoundTripAuthorId"
        });

        return SchemaBuilder.BuildDescriptor(td, embedding) with { OwnerTenantId = "tenant-roundtrip" };
    }

    [Fact]
    public async Task OwnerStampedDescriptor_RoundTripsThroughPostgres_Whole()
    {
        var original = OwnerStampedDescriptor();

        // The worker creates the table at startup; this test owns its own container.
        await FreshRepository().EnsureTableAsync();
        await new SchemaRegistry(FreshRepository(), NullLogger<SchemaRegistry>.Instance).RegisterAsync(original);

        // A second registry over a second repository and connection: what it holds came from the
        // JSON in Postgres, not from the first registry's memory.
        var reader = new SchemaRegistry(FreshRepository(), NullLogger<SchemaRegistry>.Instance);
        await reader.LoadAsync();
        var loaded = reader.Get("RoundTripArticle");

        loaded.Should().NotBeNull();
        loaded!.OwnerTenantId.Should().Be("tenant-roundtrip");
        loaded.Should().BeEquivalentTo(original);
        SchemaBuilder.ToTableSchema(loaded).Should().BeEquivalentTo(
            SchemaBuilder.ToTableSchema(original), options => options.WithStrictOrdering());
    }
}
```

- [ ] **Step 2: Run it**

This test pins behaviour that already holds (B2 in `verify-section23.md`: descriptors already round-trip through `_iverson_schema`), so it passes on the unmodified code. Its red counterfactual is the mutation table in Step 4.

Heavy: starts one Postgres container. Nothing else may run at the same time.
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.SchemaRegistryPostgresRoundTripTests."
```
Expected: 1 passed, 0 failed. If it fails, stop: a descriptor member does not survive the trip, and Task 7's payload would lose it too. Record the FluentAssertions difference report and ask.

- [ ] **Step 3: No implementation**

Nothing changes in production code for this task.

- [ ] **Step 4: Mutation proof** (heavy, one at a time; make each mutation by hand and undo it by hand)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | The owner travels in the JSON | In `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`, put `[System.Text.Json.Serialization.JsonIgnore]` on the line above `public string? OwnerTenantId { get; init; }` | `OwnerStampedDescriptor_RoundTripsThroughPostgres_Whole` |
| M2 | The serialised descriptor is the one registered | In `SchemaRegistry.RegisterAsync`, change `JsonSerializer.Serialize(descriptor, s_jsonOptions)` to `JsonSerializer.Serialize(descriptor with { SearchKeyColumns = [] }, s_jsonOptions)` | `OwnerStampedDescriptor_RoundTripsThroughPostgres_Whole` |

Run the Step 2 command under each mutation and record the failure message. Revert, and run Step 2 once more: 1 passed.

- [ ] **Step 5: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryPostgresRoundTripTests.cs
git commit -m "pin the descriptor round trip through real postgres

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Privileged requests through the worker, and RegisterSchema on them

**Spec:** §5.1, §5.2 (RegisterSchema), §5.3, §9 (§5 bullets). Closes #1 (table adoption) and #7 (the schema-owner race); moves RegisterSchema's DDL and registry write out of the api (part of #13).

**Files:**
- Create: `Iverson.Server/Iverson.Sql/PrivilegedRequestRow.cs`. The row record and the five status constants.
- Create: `Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs`. Insert, read, conditional expire, list claimable, conditional claim (which stamps `ClaimedAt`), complete, failing abandoned claims, cleanup.
- Create: `Iverson.Server/Iverson.Sql/ExistingTableInspector.cs`. What sits at a derived table name (relkind, tenant column), and the foreign-row `EXISTS` under `iverson_maintenance`.
- Create: `Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs`. Session advisory locks on dedicated pooled connections, keys outside the `int4` `hashtext` range.
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:191-197`. `ISchemaRegistryRepository` gains `GetOwnersAsync` and `UpsertIfOwnerAsync`; `IExistingTableInspector`, `IPrivilegedRequestRepository` and `IPrivilegedRequestLocks` follow it.
- Modify: `Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs:33-35`. The owner read (`lower(type_name) = lower(@TypeName)`) and the conditional upsert, appended after `DeleteAsync`.
- Modify: `Iverson.Server/Iverson.Sql/ServiceCollectionExtensions.cs:28-31`. `AddPostgres` registers the inspector and the locks.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:247`. An `internal` accessor for the JSON options, which the payload reuses (B2's "would have to be exposed").
- Create: `Iverson.Server/Iverson.Api/Grpc/ISchemaRegistrationExecutor.cs`. The seam between RegisterSchema's validation (api) and its privileged effects.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`: the constructor (`:14-20`), the owner pre-check's exception (`:86-89`), the phase-2 comment (`:281-282`), and phase 3 (`:324-350`), which now hands the owner-stamped descriptors to the executor.
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestSchema.cs`. The `IversonPrivilegedRequests` `TableSchema`: spec §5.1's columns plus `ClaimedAt`, which the claim sets, because `RequestedAt` is not the claim time.
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs`. The kind constants, `RegisterSchemaPayload`, `PrivilegedRequestResult`.
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs`. The api's waits (15 s, 10 s, 250 ms).
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestClient.cs`. The api's submit and poll.
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs`. The api's executor: queue, wait, reload.
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs`. The worker's executor, §5.3 steps 1–5.
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs`. Runs one claimed request: reload, lock, dispatch, error mapping.
- Create: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs`. The hosted poll, claim and cleanup loop. Cleanup also fails `running` requests claimed over an hour ago (a worker that stopped mid-request) as `Unavailable`.
- Modify: `Iverson.Server/Iverson.Api/Program.cs`: `:377` (DI for both roles), `:478` (the worker-only services), `:752` (the table's startup DDL).
- Create: `Iverson.Server/Iverson.Api.Tests/Helpers/InProcessSchemaRegistrationExecutor.cs`. The orchestrator tests' in-process executor.
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:34-36`. `NoOpSchemaRegistryRepository` gains the two members.
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs`. `AdminConsoleSchemaRegistryRepository` gains the two members, after its `DeleteAsync`.
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs:36-41`. The construction, plus two tests before `RegisterAsync_TypeAlreadyOwnedByAnotherTenant_ThrowsPermissionDenied`.
- Modify: `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs:37-42`. The construction.
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs:91-93`. The construction.
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs:226-231`. The construction, with the real applier.
- Test: `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestTestData.cs` (new). Shared descriptors and request rows.
- Test: `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/SchemaRegistrationApplierTests.cs` (new). §5.3 with substitutes.
- Test: `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/QueuedSchemaRegistrationExecutorTests.cs` (new). The api's waits.
- Test: `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs` (new). Reload, lock, error mapping.
- Test: `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestWorkerTests.cs` (new). Claiming, concurrency, recording, cleanup.
- Test: `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs` (new, container). The table, the registry SQL, adoption, drift, the locks, and one registration end to end.

**Interfaces:**
- Consumes: Task 6's green round trip. Task 1's orchestrator refusals and Task 5's `ValidateEnrichmentTargets` change: this task's orchestrator hunks are the constructor, the 4-line owner pre-check, two comment lines in phase 2 and phase 3, none of which Tasks 1 or 5 edit. Its `SchemaRegistry.cs` hunk is the JSON-options declaration at the end of the class, which Tasks 1, 5 and 9 do not edit.
- Produces, for Task 8: `PrivilegedRequestClient` (`SubmitAsync`, `ReadAsync`, `WaitWhileAsync`, `Waits`), `PrivilegedRequestResult` (`Ok`, `ToJson`, `From`), `PrivilegedRequestKinds`, `PrivilegedRequestWaits` (init-only properties), `PrivilegedRequestExecutor` (its constructor and `RunAsync` switch, which Task 8 extends), `IPrivilegedRequestLocks.AcquireTypeAsync`, and the test files' `_sut`/`NewExecutor` constructions, which Task 8 rewrites.
- Produces, for Task 9: the table `IversonPrivilegedRequests`, created by the new line `await schemaManager.ApplySchemaAsync(Iverson.Api.PrivilegedRequests.PrivilegedRequestSchema.Table);` directly after the `TenantSchema` apply in `Program.cs`'s startup block. Task 9 gates that block to the worker and grants `iverson_api` `INSERT, SELECT, UPDATE ("Status")` on it. The api's statements against the table are exactly: `InsertAsync` (INSERT), `GetAsync` (SELECT), `ExpireIfPendingAsync` (`UPDATE … SET "Status"` only, with a `WHERE` on `"Id"` and `"Status"`). The api also calls `SchemaRegistry.LoadAsync` after a successful registration (SELECT on `_iverson_schema`). Nothing in the api calls `SchemaRegistry.RegisterAsync` any more; its only remaining callers are tests. `"ClaimedAt"` is written only by the worker (the claim, and `FailAbandonedAsync`), so the api's `UPDATE ("Status")` grant stays sufficient.
- Produces, for Task 10: Task 10 also adds a plumbing table to `Program.cs`'s startup block and services to the worker-only block. Its quotes must match those blocks as this task leaves them: one new line after the `TenantSchema` apply, and three new lines plus a comment before the worker block's closing `}`.
- `Program.cs` DI: the new services are added for both roles next to `ISchemaRegistrationOrchestrator` (`:377`), except the applier, the executor and the hosted worker, which are worker-only.
- New class names checked against existing ones with `git grep -n "class <Name>" -- '*.cs'` at `6b942e1c`: none of `PrivilegedRequest*`, `SchemaRegistrationApplier*`, `QueuedSchemaRegistrationExecutor*`, `InProcessSchemaRegistrationExecutor`, `ExistingTableInspector`, `PostgresPrivilegedRequestLocks` exists, and no existing class name contains one of the new test class names.

- [ ] **Step 1: Write the failing tests**

**1a. Shared test data.** Create `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestTestData.cs`:
```csharp
using System.Text.Json;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.Tests.PrivilegedRequests;

internal static class PrivilegedRequestTestData
{
    /// <summary>
    /// A minimal registrable descriptor: a UUID key, one nullable column and the server-owned
    /// tenant column, named as SchemaBuilder names them.
    /// </summary>
    public static SchemaDescriptor Descriptor(string typeName, string? owner, string titleSqlType = "TEXT") => new()
    {
        TypeName      = typeName,
        TableName     = SchemaBuilder.ToTableName(typeName),
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Title", titleSqlType, true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false)
        ],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = SchemaDescriptor.TenantColumnName,
        OwnerTenantId = owner
    };

    /// <summary>A claimed RegisterSchema request carrying <paramref name="descriptors"/>.</summary>
    public static PrivilegedRequestRow RegisterRow(params SchemaDescriptor[] descriptors) => new(
        Guid.NewGuid(),
        PrivilegedRequestKinds.RegisterSchema,
        JsonSerializer.Serialize(new RegisterSchemaPayload(descriptors, []), SchemaRegistry.JsonOptions),
        PrivilegedRequestStatus.Running,
        null);

    /// <summary>A request row as the api reads it back while waiting.</summary>
    public static PrivilegedRequestRow StatusRow(string status, PrivilegedRequestResult? result = null) => new(
        Guid.Empty, PrivilegedRequestKinds.RegisterSchema, "{}", status, result?.ToJson());
}
```

**1b. The worker's RegisterSchema, unit level.** Create `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/SchemaRegistrationApplierTests.cs`:
```csharp
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.3, per descriptor in batch order: the stored owner, the adoption check, the DDL
/// and the conditional registry write; then the re-render enqueue.
/// </summary>
public class SchemaRegistrationApplierTests
{
    private readonly ISchemaRegistryRepository _registry = Substitute.For<ISchemaRegistryRepository>();
    private readonly IExistingTableInspector _tables = Substitute.For<IExistingTableInspector>();
    private readonly IRecordStoreSchemaManager _schemaManager = Substitute.For<IRecordStoreSchemaManager>();
    private readonly IDocumentRerenderQueueRepository _rerenderQueue = Substitute.For<IDocumentRerenderQueueRepository>();
    private readonly SchemaRegistrationApplier _sut;

    public SchemaRegistrationApplierTests()
    {
        _registry.GetOwnersAsync(Arg.Any<string>()).Returns(new List<string?>());
        _registry.UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _tables.DescribeAsync(Arg.Any<string>(), Arg.Any<string>()).Returns((ExistingTable?)null);
        _sut = new SchemaRegistrationApplier(_registry, _tables, _schemaManager, _rerenderQueue);
    }

    private Task Apply(params SchemaDescriptor[] descriptors) =>
        _sut.ExecuteAsync(descriptors, [], CancellationToken.None);

    private async Task AssertNothingAppliedAsync()
    {
        await _schemaManager.DidNotReceive().ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>());
        await _registry.DidNotReceive().UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AnotherTenantOwnsTheStoredRow_IsPermissionDenied_AndNothingIsApplied()
    {
        _registry.GetOwnersAsync("Probe").Returns(new List<string?> { "tenant-b" });

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndNoTable_AppliesTheDdlThenWritesTheOwnerStampedRow()
    {
        await Apply(Descriptor("Probe", "tenant-a"));

        Received.InOrder(() =>
        {
            _schemaManager.ApplySchemaAsync(Arg.Is<TableSchema>(t => t.TableName == "probes"), Arg.Is(SchemaDriftPolicy.Throw));
            _registry.UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is<string?>("tenant-a"));
        });
        var written = (string)_registry.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ISchemaRegistryRepository.UpsertIfOwnerAsync))
            .GetArguments()[1]!;
        JsonSerializer.Deserialize<SchemaDescriptor>(written, SchemaRegistry.JsonOptions)!
            .OwnerTenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingRelationThatIsNotATable_IsAlreadyExists()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(false, true));

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
        ex.Which.Status.Detail.Should().Contain("'probes'").And.Contain("is not an ordinary table");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingTableWithoutTheTenantColumn_IsAlreadyExists()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, false));

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
        ex.Which.Status.Detail.Should().Contain($"has no {SchemaDescriptor.TenantColumnName} column");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingTableHoldingAnotherTenantsRows_IsAlreadyExists()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, true));
        _tables.HasRowsOfAnotherTenantAsync("probes", SchemaDescriptor.TenantColumnName, "tenant-a").Returns(true);

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.AlreadyExists);
        ex.Which.Status.Detail.Should().Contain("holds rows of another tenant");
        await AssertNothingAppliedAsync();
    }

    [Fact]
    public async Task NoStoredRowAndAnExistingTableHoldingOnlyTheOwnersRows_IsAdopted()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, true));
        _tables.HasRowsOfAnotherTenantAsync("probes", SchemaDescriptor.TenantColumnName, "tenant-a").Returns(false);

        await Apply(Descriptor("Probe", "tenant-a"));

        await _schemaManager.Received(1).ApplySchemaAsync(Arg.Any<TableSchema>(), SchemaDriftPolicy.Throw);
        await _registry.Received(1).UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is<string?>("tenant-a"));
    }

    [Fact]
    public async Task AnUnownedType_AdoptsAnExistingTenantScopedTable_WithoutTheForeignRowQuery()
    {
        _tables.DescribeAsync("probes", SchemaDescriptor.TenantColumnName).Returns(new ExistingTable(true, true));

        await Apply(Descriptor("Probe", owner: null));

        await _tables.DidNotReceive().HasRowsOfAnotherTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _registry.Received(1).UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is((string?)null));
    }

    [Fact]
    public async Task AStoredRowOfTheSameOwner_IsReRegisteredWithoutTheAdoptionCheck()
    {
        _registry.GetOwnersAsync("Probe").Returns(new List<string?> { "tenant-a" });

        await Apply(Descriptor("Probe", "tenant-a"));

        await _tables.DidNotReceive().DescribeAsync(Arg.Any<string>(), Arg.Any<string>());
        await _registry.Received(1).UpsertIfOwnerAsync(Arg.Is("Probe"), Arg.Any<string>(), Arg.Is<string?>("tenant-a"));
    }

    [Fact]
    public async Task Drift_IsFailedPrecondition_AndTheRegistryIsNotWritten()
    {
        _schemaManager.ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>())
            .ThrowsAsync(new SchemaDriftException("probes", "Title", "integer", "TEXT"));

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ex.Which.Status.Detail.Should().Contain("Migrate the column by hand");
        await _registry.DidNotReceive().UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task TheConditionalWriteMatchingNoRow_IsPermissionDenied()
    {
        _registry.UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(false);

        var act = () => Apply(Descriptor("Probe", "tenant-a"));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("registered to another tenant");
    }

    [Fact]
    public async Task ChangedTemplates_AreEnqueuedAfterEveryDescriptorIsWritten()
    {
        await _sut.ExecuteAsync(
            [Descriptor("First", "tenant-a"), Descriptor("Second", "tenant-a")], ["First"], CancellationToken.None);

        Received.InOrder(() =>
        {
            _registry.UpsertIfOwnerAsync(Arg.Is("First"), Arg.Any<string>(), Arg.Any<string?>());
            _registry.UpsertIfOwnerAsync(Arg.Is("Second"), Arg.Any<string>(), Arg.Any<string?>());
            _rerenderQueue.EnqueueTypeAsync("First");
        });
        await _rerenderQueue.Received(1).EnqueueTypeAsync(Arg.Any<string>());
    }
}
```

**1c. The api's wait, unit level.** Create `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/QueuedSchemaRegistrationExecutorTests.cs`:
```csharp
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.2: the api queues the registration, waits for the claim, then for the result,
/// and reloads its registry on success. The waits are shortened; their production values are
/// <see cref="PrivilegedRequestWaits"/>' defaults.
/// </summary>
public class QueuedSchemaRegistrationExecutorTests
{
    private static readonly PrivilegedRequestWaits ShortWaits = new()
    {
        RegisterClaim = TimeSpan.FromMilliseconds(300),
        RegisterRun   = TimeSpan.FromMilliseconds(300),
        PollInterval  = TimeSpan.FromMilliseconds(10)
    };

    private readonly IPrivilegedRequestRepository _requests = Substitute.For<IPrivilegedRequestRepository>();
    private readonly ISchemaRegistryRepository _registryRepository = Substitute.For<ISchemaRegistryRepository>();
    private readonly QueuedSchemaRegistrationExecutor _sut;

    public QueuedSchemaRegistrationExecutorTests()
    {
        _registryRepository.LoadAllAsync().Returns(Enumerable.Empty<(string, string)>());
        _sut = new QueuedSchemaRegistrationExecutor(
            new PrivilegedRequestClient(_requests, ShortWaits),
            new SchemaRegistry(_registryRepository, NullLogger<SchemaRegistry>.Instance));
    }

    private Task Execute() =>
        _sut.ExecuteAsync([Descriptor("Probe", "tenant-a")], ["Probe"], CancellationToken.None);

    [Fact]
    public async Task Succeeded_ReloadsTheRegistryBeforeAnswering()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(
            StatusRow(PrivilegedRequestStatus.Running), StatusRow(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok()));

        await Execute();

        await _registryRepository.Received(1).LoadAllAsync();
        await _requests.Received(1).InsertAsync(
            Arg.Any<Guid>(),
            Arg.Is(PrivilegedRequestKinds.RegisterSchema),
            Arg.Any<string>(),
            Arg.Is<string?>("tenant-a"),
            Arg.Is<TimeSpan?>(ShortWaits.RegisterClaim));
    }

    [Fact]
    public async Task ThePayload_IsTheOwnerStampedDescriptorsAndTheChangedTemplates()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok()));

        await Execute();

        var payloadJson = (string)_requests.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPrivilegedRequestRepository.InsertAsync))
            .GetArguments()[2]!;
        var payload = JsonSerializer.Deserialize<RegisterSchemaPayload>(payloadJson, SchemaRegistry.JsonOptions)!;
        payload.Descriptors.Should().BeEquivalentTo(new[] { Descriptor("Probe", "tenant-a") });
        payload.ChangedTemplateTypes.Should().Equal("Probe");
    }

    [Fact]
    public async Task Failed_AnswersWithTheStoredStatusAndMessage()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(StatusCode.PermissionDenied, "Type 'Probe' is registered to another tenant and cannot be re-registered here.")));

        var act = Execute;

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");
        await _registryRepository.DidNotReceive().LoadAllAsync();
    }

    [Fact]
    public async Task NeverClaimed_ExpiresTheRequest_AndAnswersUnavailable()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Pending));
        _requests.ExpireIfPendingAsync(Arg.Any<Guid>()).Returns(true);

        var act = Execute;

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unavailable);
        ex.Which.Status.Detail.Should().Contain("did not pick the registration up");
        await _requests.Received(1).ExpireIfPendingAsync(Arg.Any<Guid>());
        await _registryRepository.DidNotReceive().LoadAllAsync();
    }

    [Fact]
    public async Task ClaimedJustAsTheApiGaveUp_IsWaitedOnRatherThanExpired()
    {
        // The expiry is conditional on 'pending': when the worker's claim got there first, it
        // matches no row, and the api waits on the claimed request instead.
        var claimed = false;
        _requests.ExpireIfPendingAsync(Arg.Any<Guid>()).Returns(false).AndDoes(_ => claimed = true);
        _requests.GetAsync(Arg.Any<Guid>()).Returns(_ => claimed
            ? StatusRow(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok())
            : StatusRow(PrivilegedRequestStatus.Pending));

        await Execute();

        await _registryRepository.Received(1).LoadAllAsync();
    }

    [Fact]
    public async Task ClaimedButUnfinishedAfterTheRunWait_AnswersDeadlineExceeded()
    {
        _requests.GetAsync(Arg.Any<Guid>()).Returns(StatusRow(PrivilegedRequestStatus.Running));

        var act = Execute;

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.DeadlineExceeded);
        ex.Which.Status.Detail.Should().Contain("registration is still being applied");
        await _requests.DidNotReceive().ExpireIfPendingAsync(Arg.Any<Guid>());
    }
}
```

**1d. Running one claimed request, unit level.** Create `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs`:
```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.1: the worker reloads its registry, takes the request kind's lock, runs it, and
/// turns every outcome into a recorded result: a refusal keeps its status and message, an
/// unexpected exception becomes a generic Internal with the details in the log.
/// </summary>
public class PrivilegedRequestExecutorTests
{
    private readonly ISchemaRegistryRepository _registryRepository = Substitute.For<ISchemaRegistryRepository>();
    private readonly IExistingTableInspector _tables = Substitute.For<IExistingTableInspector>();
    private readonly IRecordStoreSchemaManager _schemaManager = Substitute.For<IRecordStoreSchemaManager>();
    private readonly IPrivilegedRequestLocks _locks = Substitute.For<IPrivilegedRequestLocks>();
    private readonly ILogger<PrivilegedRequestExecutor> _logger = Substitute.For<ILogger<PrivilegedRequestExecutor>>();
    private readonly SchemaRegistry _registry;
    private readonly PrivilegedRequestExecutor _sut;

    public PrivilegedRequestExecutorTests()
    {
        _registryRepository.LoadAllAsync().Returns(Enumerable.Empty<(string, string)>());
        _registryRepository.GetOwnersAsync(Arg.Any<string>()).Returns(new List<string?>());
        _registryRepository.UpsertIfOwnerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);
        _tables.DescribeAsync(Arg.Any<string>(), Arg.Any<string>()).Returns((ExistingTable?)null);
        _locks.AcquireRegistrationAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        _locks.AcquireTypeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        _registry = new SchemaRegistry(_registryRepository, NullLogger<SchemaRegistry>.Instance);
        _sut = new PrivilegedRequestExecutor(
            _registry,
            new SchemaRegistrationApplier(_registryRepository, _tables, _schemaManager, Substitute.For<IDocumentRerenderQueueRepository>()),
            _locks,
            _logger);
    }

    [Fact]
    public async Task RegisterSchema_ReloadsTheRegistry_TakesTheRegistrationLock_AndApplies()
    {
        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), CancellationToken.None);

        result.Should().Be(PrivilegedRequestResult.Ok());
        await _registryRepository.Received(1).LoadAllAsync();
        await _locks.Received(1).AcquireRegistrationAsync(Arg.Any<CancellationToken>());
        await _schemaManager.Received(1).ApplySchemaAsync(Arg.Is<TableSchema>(t => t.TableName == "probes"), SchemaDriftPolicy.Throw);
    }

    [Fact]
    public async Task ARefusal_IsRecordedWithItsStatusAndMessage()
    {
        _registryRepository.GetOwnersAsync("Probe").Returns(new List<string?> { "tenant-b" });

        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), CancellationToken.None);

        result.Code.Should().Be(StatusCode.PermissionDenied);
        result.Message.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");
    }

    [Fact]
    public async Task AnUnexpectedException_IsRecordedAsInternal_WithAGenericMessage_AndLogged()
    {
        _schemaManager.ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>())
            .ThrowsAsync(new InvalidOperationException("connection refused: secret detail"));

        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), CancellationToken.None);

        result.Code.Should().Be(StatusCode.Internal);
        result.Message.Should().NotContain("secret detail");
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(e => e.Message == "connection refused: secret detail"),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task AnUnknownKind_IsRecordedAsInternal()
    {
        var request = new PrivilegedRequestRow(Guid.NewGuid(), "Unheard", "{}", PrivilegedRequestStatus.Running, null);

        var result = await _sut.RunAsync(request, CancellationToken.None);

        result.Code.Should().Be(StatusCode.Internal);
    }

    [Fact]
    public async Task StoppedByWorkerShutdown_IsRecordedAsUnavailable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _locks.AcquireRegistrationAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cts.Token));

        var result = await _sut.RunAsync(RegisterRow(Descriptor("Probe", "tenant-a")), cts.Token);

        result.Code.Should().Be(StatusCode.Unavailable);
        await _schemaManager.DidNotReceive().ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>());
    }
}
```

**1e. The poll loop, unit level.** Create `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestWorkerTests.cs`:
```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.1: the worker claims each pending request with a conditional UPDATE, runs every
/// claimed request on its own task while it keeps claiming, records the outcome, and deletes
/// finished requests older than a day.
/// </summary>
public class PrivilegedRequestWorkerTests
{
    private readonly IPrivilegedRequestRepository _requests = Substitute.For<IPrivilegedRequestRepository>();
    private readonly IPrivilegedRequestExecutor _executor = Substitute.For<IPrivilegedRequestExecutor>();
    private readonly ConcurrentDictionary<Guid, (string Status, string ResultJson)> _completed = new();
    private readonly PrivilegedRequestWorker _sut;

    public PrivilegedRequestWorkerTests()
    {
        _requests.TryClaimAsync(Arg.Any<Guid>()).Returns(true);
        _requests.CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _completed[(Guid)ci[0]] = ((string)ci[1], (string)ci[2]));
        _executor.RunAsync(Arg.Any<PrivilegedRequestRow>(), Arg.Any<CancellationToken>()).Returns(PrivilegedRequestResult.Ok());
        _sut = new PrivilegedRequestWorker(_requests, _executor, NullLogger<PrivilegedRequestWorker>.Instance);
    }

    private static PrivilegedRequestRow Pending() =>
        new(Guid.NewGuid(), PrivilegedRequestKinds.RegisterSchema, "{}", PrivilegedRequestStatus.Pending, null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold within 5 s.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task PollOnce_RunsOnlyTheRequestsItClaims()
    {
        var won = Pending();
        var lost = Pending();
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns([won, lost]);
        _requests.TryClaimAsync(lost.Id).Returns(false);

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.WhenIdleAsync();

        await _executor.Received(1).RunAsync(Arg.Is(won), Arg.Any<CancellationToken>());
        await _executor.DidNotReceive().RunAsync(Arg.Is(lost), Arg.Any<CancellationToken>());
        _completed[won.Id].Status.Should().Be(PrivilegedRequestStatus.Succeeded);
        _completed.ContainsKey(lost.Id).Should().BeFalse();
    }

    [Fact]
    public async Task PollOnce_DoesNotWaitForEarlierRequestsToFinish()
    {
        var slow = Pending();
        var fast = Pending();
        var gate = new TaskCompletionSource<PrivilegedRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns([slow, fast]);
        _executor.RunAsync(Arg.Is(slow), Arg.Any<CancellationToken>()).Returns(gate.Task);

        await _sut.PollOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => _completed.ContainsKey(fast.Id));

        _completed.ContainsKey(slow.Id).Should().BeFalse("the slow request is still running");
        gate.SetResult(PrivilegedRequestResult.Ok());
        await _sut.WhenIdleAsync();
        _completed.ContainsKey(slow.Id).Should().BeTrue();
    }

    [Fact]
    public async Task AFailedOutcome_IsRecordedAsFailed_WithItsResult()
    {
        var request = Pending();
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns([request]);
        _executor.RunAsync(Arg.Is(request), Arg.Any<CancellationToken>())
            .Returns(new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied"));

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.WhenIdleAsync();

        var (status, resultJson) = _completed[request.Id];
        status.Should().Be(PrivilegedRequestStatus.Failed);
        JsonSerializer.Deserialize<PrivilegedRequestResult>(resultJson, SchemaRegistry.JsonOptions)
            .Should().Be(new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied"));
    }

    [Fact]
    public async Task PollOnce_DeletesFinishedRequestsOlderThanADay_AtMostOncePerInterval()
    {
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns(Enumerable.Empty<PrivilegedRequestRow>());

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.PollOnceAsync(CancellationToken.None);

        await _requests.Received(1).DeleteFinishedOlderThanAsync(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task PollOnce_FailsRequestsClaimedOverAnHourAgo_AsUnavailable_AtMostOncePerInterval()
    {
        // A worker that stops mid-request leaves its row 'running'. Reconcile and replay requests
        // never expire, so this is the only thing that ends them.
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns(Enumerable.Empty<PrivilegedRequestRow>());

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.PollOnceAsync(CancellationToken.None);

        await _requests.Received(1).FailAbandonedAsync(TimeSpan.FromHours(1), Arg.Any<string>());
        var resultJson = (string)_requests.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPrivilegedRequestRepository.FailAbandonedAsync))
            .GetArguments()[1]!;
        JsonSerializer.Deserialize<PrivilegedRequestResult>(resultJson, SchemaRegistry.JsonOptions)
            .Should().Be(new PrivilegedRequestResult(StatusCode.Unavailable, "The worker stopped before finishing this request."));
    }
}
```

**1f. Against real Postgres.** Create `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs`:
```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

public sealed class PrivilegedRequestPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    public string ConnectionString { get; private set; } = null!;
    public PostgresRepository Repository { get; private set; } = null!;
    public PostgresSchemaManager SchemaManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Repository = new PostgresRepository(ConnectionString, NullLogger<PostgresRepository>.Instance);
        SchemaManager = new PostgresSchemaManager(ConnectionString, NullLogger<PostgresSchemaManager>.Instance);

        // The worker's startup order: roles, the registry table, then the plumbing tables.
        await SchemaManager.EnsureRolesAsync();
        await new SchemaRegistryRepository(Repository).EnsureTableAsync();
        await SchemaManager.ApplySchemaAsync(PrivilegedRequestSchema.Table);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// CSR round 12 §5 against real Postgres: the request table's claim and expiry, the registry's
/// owner read and conditional write, adoption, drift, the advisory locks, and one registration
/// end to end through the queue and a running worker.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class PrivilegedRequestPostgresIntegrationTests(PrivilegedRequestPostgresFixture fixture)
    : IClassFixture<PrivilegedRequestPostgresFixture>
{
    private PrivilegedRequestRepository Requests => new(PrivilegedRequestSchema.TableName, fixture.Repository);
    private SchemaRegistryRepository Registry => new(fixture.Repository);

    private SchemaRegistry NewRegistry() => new(Registry, NullLogger<SchemaRegistry>.Instance);

    private SchemaRegistrationApplier Applier() => new(
        Registry,
        new ExistingTableInspector(fixture.Repository),
        fixture.SchemaManager,
        Substitute.For<IDocumentRerenderQueueRepository>());

    private PrivilegedRequestExecutor NewExecutor() => new(
        NewRegistry(),
        Applier(),
        new PostgresPrivilegedRequestLocks(fixture.ConnectionString),
        NullLogger<PrivilegedRequestExecutor>.Instance);

    private Task<string?> StatusAsync(Guid id) =>
        fixture.Repository.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "Status" FROM "{PrivilegedRequestSchema.TableName}" WHERE "Id" = @Id""", new { Id = id });

    private async Task<Guid> InsertAsync(TimeSpan? expiresAfter)
    {
        var id = Guid.NewGuid();
        await Requests.InsertAsync(id, PrivilegedRequestKinds.RegisterSchema, "{}", "tenant-a", expiresAfter);
        return id;
    }

    private static async Task AssertRefusedAsync(Func<Task> act, StatusCode code, string detail) =>
        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.StatusCode == code && e.Status.Detail.Contains(detail));

    // ── The request table ────────────────────────────────────────────────────

    [Fact]
    public async Task TryClaim_TheSameRequestTwice_SucceedsOnce()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));

        (await Requests.TryClaimAsync(id)).Should().BeTrue();
        (await Requests.TryClaimAsync(id)).Should().BeFalse();
        (await StatusAsync(id)).Should().Be(PrivilegedRequestStatus.Running);
    }

    [Fact]
    public async Task ARequestTheApiExpired_CannotBeClaimed()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));

        (await Requests.ExpireIfPendingAsync(id)).Should().BeTrue();

        (await Requests.TryClaimAsync(id)).Should().BeFalse();
        (await StatusAsync(id)).Should().Be(PrivilegedRequestStatus.Expired);
    }

    [Fact]
    public async Task ARequestTheWorkerClaimed_CannotBeExpired()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));

        (await Requests.TryClaimAsync(id)).Should().BeTrue();

        (await Requests.ExpireIfPendingAsync(id)).Should().BeFalse();
        (await StatusAsync(id)).Should().Be(PrivilegedRequestStatus.Running);
    }

    [Fact]
    public async Task APendingRequestPastItsExpiry_IsNeitherListedNorClaimable_WhileOneWithNoExpiryIs()
    {
        var expired = await InsertAsync(TimeSpan.Zero);
        var neverExpires = await InsertAsync(expiresAfter: null);

        var listed = (await Requests.ListClaimableAsync(1000)).Select(r => r.Id).ToList();

        listed.Should().NotContain(expired).And.Contain(neverExpires);
        (await Requests.TryClaimAsync(expired)).Should().BeFalse();
        (await Requests.TryClaimAsync(neverExpires)).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_RecordsTheStatusAndTheResult()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));
        await Requests.TryClaimAsync(id);

        await Requests.CompleteAsync(id, PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied").ToJson());

        var row = await Requests.GetAsync(id);
        row!.Status.Should().Be(PrivilegedRequestStatus.Failed);
        PrivilegedRequestResult.From(row).Should().Be(new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied"));
    }

    [Fact]
    public async Task DeleteFinishedOlderThan_RemovesOnlyOldFinishedRequests()
    {
        var oldSucceeded = await InsertAsync(expiresAfter: null);
        await Requests.CompleteAsync(oldSucceeded, PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok().ToJson());
        var oldPendingPastExpiry = await InsertAsync(TimeSpan.Zero);
        var oldRunning = await InsertAsync(expiresAfter: null);
        await Requests.TryClaimAsync(oldRunning);
        var oldPendingNoExpiry = await InsertAsync(expiresAfter: null);
        var recentFailed = await InsertAsync(expiresAfter: null);
        await Requests.CompleteAsync(recentFailed, PrivilegedRequestStatus.Failed, PrivilegedRequestResult.Ok().ToJson());
        await fixture.Repository.ExecuteAsync(
            $"""UPDATE "{PrivilegedRequestSchema.TableName}" SET "RequestedAt" = now() - interval '2 days' WHERE "Id" = ANY(@Ids)""",
            new { Ids = new[] { oldSucceeded, oldPendingPastExpiry, oldRunning, oldPendingNoExpiry } });

        var deleted = await Requests.DeleteFinishedOlderThanAsync(TimeSpan.FromDays(1));

        deleted.Should().Be(2);
        (await Requests.GetAsync(oldSucceeded)).Should().BeNull();
        (await Requests.GetAsync(oldPendingPastExpiry)).Should().BeNull();
        (await Requests.GetAsync(oldRunning)).Should().NotBeNull();
        (await Requests.GetAsync(oldPendingNoExpiry)).Should().NotBeNull();
        (await Requests.GetAsync(recentFailed)).Should().NotBeNull();
    }

    [Fact]
    public async Task FailAbandoned_FailsOnlyRequestsClaimedLongerAgoThanTheCutoff()
    {
        var abandoned = await InsertAsync(expiresAfter: null);
        await Requests.TryClaimAsync(abandoned);
        var recentlyClaimed = await InsertAsync(expiresAfter: null);
        await Requests.TryClaimAsync(recentlyClaimed);
        var neverClaimed = await InsertAsync(expiresAfter: null);
        // Ages the claim time the claim itself recorded, so a claim that recorded none stays NULL
        // and matches nothing. The never-claimed row is aged too: a pending row is never failed.
        await fixture.Repository.ExecuteAsync(
            $"""UPDATE "{PrivilegedRequestSchema.TableName}" SET "ClaimedAt" = "ClaimedAt" - interval '2 hours', "RequestedAt" = "RequestedAt" - interval '2 hours' WHERE "Id" = ANY(@Ids)""",
            new { Ids = new[] { abandoned, neverClaimed } });
        var result = new PrivilegedRequestResult(StatusCode.Unavailable, "The worker stopped before finishing this request.");

        var failed = await Requests.FailAbandonedAsync(TimeSpan.FromHours(1), result.ToJson());

        failed.Should().Be(1);
        var row = await Requests.GetAsync(abandoned);
        row!.Status.Should().Be(PrivilegedRequestStatus.Failed);
        PrivilegedRequestResult.From(row).Should().Be(result);
        (await StatusAsync(recentlyClaimed)).Should().Be(PrivilegedRequestStatus.Running);
        (await StatusAsync(neverClaimed)).Should().Be(PrivilegedRequestStatus.Pending);
    }

    // ── The registry's owner read and conditional write ──────────────────────

    [Fact]
    public async Task GetOwners_MatchesTheTypeNameIgnoringCase()
    {
        (await Registry.UpsertIfOwnerAsync("OwnerProbe", """{"ownerTenantId":"tenant-a"}""", "tenant-a")).Should().BeTrue();

        (await Registry.GetOwnersAsync("ownerprobe")).Should().Equal("tenant-a");
        (await Registry.GetOwnersAsync("NoSuchProbe")).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertIfOwner_ARowOfAnotherOwner_IsLeftUnchanged()
    {
        await Registry.UpsertIfOwnerAsync("ContestProbe", """{"ownerTenantId":"tenant-a","marker":1}""", "tenant-a");

        (await Registry.UpsertIfOwnerAsync("ContestProbe", """{"ownerTenantId":"tenant-b","marker":2}""", "tenant-b")).Should().BeFalse();
        (await Registry.GetOwnersAsync("ContestProbe")).Should().Equal("tenant-a");

        (await Registry.UpsertIfOwnerAsync("ContestProbe", """{"ownerTenantId":"tenant-a","marker":3}""", "tenant-a")).Should().BeTrue();
        (await fixture.Repository.QuerySingleOrDefaultAsync<string>(
            "SELECT schema_json->>'marker' FROM _iverson_schema WHERE type_name = 'ContestProbe'")).Should().Be("3");
    }

    [Fact]
    public async Task UpsertIfOwner_ALegacyRowWithNoOwnerKey_CountsAsOwnerless()
    {
        await Registry.UpsertAsync("LegacyProbe", """{"typeName":"LegacyProbe"}""");

        (await Registry.GetOwnersAsync("LegacyProbe")).Should().Equal((string?)null);
        (await Registry.UpsertIfOwnerAsync("LegacyProbe", """{"ownerTenantId":"tenant-a"}""", "tenant-a")).Should().BeFalse();
        (await Registry.UpsertIfOwnerAsync("LegacyProbe", """{"ownerTenantId":null}""", null)).Should().BeTrue();
    }

    // ── The worker's RegisterSchema ──────────────────────────────────────────

    [Fact]
    public async Task TwoOwnersRacingForANewName_ExactlyOneWins()
    {
        var executor = NewExecutor();

        var results = await Task.WhenAll(
            executor.RunAsync(RegisterRow(Descriptor("RaceProbe", "tenant-a")), CancellationToken.None),
            executor.RunAsync(RegisterRow(Descriptor("RaceProbe", "tenant-b")), CancellationToken.None));

        results.Select(r => r.Code).Should().BeEquivalentTo(new[] { StatusCode.OK, StatusCode.PermissionDenied });
        var winner = results[0].Code == StatusCode.OK ? "tenant-a" : "tenant-b";
        (await Registry.GetOwnersAsync("RaceProbe")).Should().Equal(winner);
    }

    [Fact]
    public async Task ACaseVariantOfAnotherTenantsType_IsPermissionDenied()
    {
        await Applier().ExecuteAsync([Descriptor("CaseProbe", "tenant-a")], [], CancellationToken.None);

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("caseprobe", "tenant-b")], [], CancellationToken.None),
            StatusCode.PermissionDenied, "registered to another tenant");

        (await Registry.GetOwnersAsync("CASEPROBE")).Should().Equal("tenant-a");
    }

    [Fact]
    public async Task ANameWhoseTableIsAPlumbingTable_IsAlreadyExists()
    {
        // ToTableName("IversonTokenRevocation") is the revocation table's own name.
        await new TokenRevocationRepository(fixture.Repository).EnsureTableAsync();

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("IversonTokenRevocation", "tenant-a")], [], CancellationToken.None),
            StatusCode.AlreadyExists, $"has no {SchemaDescriptor.TenantColumnName} column");

        (await Registry.GetOwnersAsync("IversonTokenRevocation")).Should().BeEmpty();
    }

    [Fact]
    public async Task ANameWhoseRelationIsAView_IsAlreadyExists()
    {
        await fixture.Repository.ExecuteAsync("""CREATE VIEW view_probes AS SELECT gen_random_uuid() AS "Id" """);

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("ViewProbe", "tenant-a")], [], CancellationToken.None),
            StatusCode.AlreadyExists, "is not an ordinary table");
    }

    [Fact]
    public async Task AnOrphanedTableHoldingAnotherTenantsRows_IsAlreadyExists()
    {
        await Applier().ExecuteAsync([Descriptor("OrphanProbe", "tenant-a")], [], CancellationToken.None);
        await fixture.Repository.ExecuteAsync(
            """INSERT INTO orphan_probes ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'theirs', 'tenant-b')""");
        await Registry.DeleteAsync("OrphanProbe");

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("OrphanProbe", "tenant-a")], [], CancellationToken.None),
            StatusCode.AlreadyExists, "holds rows of another tenant");

        (await Registry.GetOwnersAsync("OrphanProbe")).Should().BeEmpty();
    }

    [Fact]
    public async Task TheModelChangeRecovery_AdoptsATableHoldingOnlyTheOwnersRows()
    {
        // The documented recovery: delete the registry row, then register again.
        await Applier().ExecuteAsync([Descriptor("RecoveryProbe", "tenant-a")], [], CancellationToken.None);
        await fixture.Repository.ExecuteAsync(
            """INSERT INTO recovery_probes ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'mine', 'tenant-a')""");
        await Registry.DeleteAsync("RecoveryProbe");

        await Applier().ExecuteAsync([Descriptor("RecoveryProbe", "tenant-a")], [], CancellationToken.None);

        (await Registry.GetOwnersAsync("RecoveryProbe")).Should().Equal("tenant-a");
    }

    [Fact]
    public async Task AColumnTypeChange_IsFailedPrecondition()
    {
        await Applier().ExecuteAsync([Descriptor("DriftProbe", "tenant-a")], [], CancellationToken.None);

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("DriftProbe", "tenant-a", titleSqlType: "INTEGER")], [], CancellationToken.None),
            StatusCode.FailedPrecondition, "Migrate the column by hand");
    }

    // ── The advisory locks ───────────────────────────────────────────────────

    [Fact]
    public async Task TheRegistrationLock_IsHeldByOneHolderAtATime()
    {
        var locks = new PostgresPrivilegedRequestLocks(fixture.ConnectionString);

        var first = await locks.AcquireRegistrationAsync(CancellationToken.None);
        var second = locks.AcquireRegistrationAsync(CancellationToken.None);
        await Task.Delay(500);
        second.IsCompleted.Should().BeFalse("the first holder still has the lock");

        await first.DisposeAsync();
        await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ATypeLock_SerialisesOneTypeIgnoringCase_AndBlocksNeitherAnotherTypeNorRegistration()
    {
        var locks = new PostgresPrivilegedRequestLocks(fixture.ConnectionString);

        var article = await locks.AcquireTypeAsync("LockArticle", CancellationToken.None);
        await using (var other = await locks.AcquireTypeAsync("LockOther", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))) { }
        await using (var registration = await locks.AcquireRegistrationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))) { }

        var sameTypeOtherCase = locks.AcquireTypeAsync("lockarticle", CancellationToken.None);
        await Task.Delay(500);
        sameTypeOtherCase.IsCompleted.Should().BeFalse("a type lock folds the name's case");

        await article.DisposeAsync();
        await using var sameTypeLease = await sameTypeOtherCase.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheLockKeys_LieOutsideTheRangeOfTheHashtextTableLocks()
    {
        var locks = new PostgresPrivilegedRequestLocks(fixture.ConnectionString);
        await using var registration = await locks.AcquireRegistrationAsync(CancellationToken.None);
        await using var type = await locks.AcquireTypeAsync("LockKeyProbe", CancellationToken.None);

        // pg_locks shows a bigint advisory key's high half in classid (objsubid 1). The
        // hashtext(...)::bigint keys that ApplySchemaAsync and StartupDdl take are sign-extended
        // int4 values, so their high half is 0 or 2^32 - 1.
        var highHalves = (await fixture.Repository.QueryAsync<long>(
            "SELECT classid::bigint FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1")).ToList();

        highHalves.Should().HaveCount(2);
        highHalves.Should().OnlyContain(h => h != 0L && h != 4294967295L);
    }

    // ── End to end ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ARegistration_ThroughTheQueue_IsAppliedByTheWorker_AndReloadedByTheApi()
    {
        await fixture.Repository.ExecuteAsync($"""DELETE FROM "{PrivilegedRequestSchema.TableName}" """);

        var apiRegistry = NewRegistry();
        var resolver = Substitute.For<IEmbeddingServiceResolver>();
        resolver.Get(Arg.Any<string?>()).Returns(Substitute.For<IEmbeddingService>());
        var orchestrator = new SchemaRegistrationOrchestrator(
            resolver,
            apiRegistry,
            new QueuedSchemaRegistrationExecutor(new PrivilegedRequestClient(Requests, new PrivilegedRequestWaits()), apiRegistry),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
        var worker = new PrivilegedRequestWorker(Requests, NewExecutor(), NullLogger<PrivilegedRequestWorker>.Instance);

        var type = new TypeDescriptor { TypeName = "QueuedProbe" };
        type.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        type.Properties.Add(new PropertyDescriptor { Name = "Title", ObjectType = ObjectType.String });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var registered = await orchestrator.RegisterAsync(new SchemaRequest { RootType = type }, "tenant-e2e", CancellationToken.None);

            registered.Should().Equal("QueuedProbe");
            apiRegistry.Get("QueuedProbe")!.OwnerTenantId.Should().Be("tenant-e2e");
            (await Registry.GetOwnersAsync("QueuedProbe")).Should().Equal("tenant-e2e");
            (await fixture.Repository.QuerySingleOrDefaultAsync<string>(
                $"""SELECT "Status" FROM "{PrivilegedRequestSchema.TableName}" """)).Should().Be(PrivilegedRequestStatus.Succeeded);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
```

**1g. The orchestrator hands its descriptors to the executor.** In `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, replace:
```csharp
    [Fact]
    public async Task RegisterAsync_TypeAlreadyOwnedByAnotherTenant_ThrowsPermissionDenied()
```
with:
```csharp
    [Fact]
    public async Task RegisterAsync_HandsTheOwnerStampedDescriptorsToTheExecutor_AndDoesNotWriteItself()
    {
        var executor = Substitute.For<ISchemaRegistrationExecutor>();
        var sut = new SchemaRegistrationOrchestrator(
            _resolver, _registry, executor, NullLogger<SchemaRegistrationOrchestrator>.Instance);
        var request = new SchemaRequest
        {
            RootType   = SimpleType("Widget", "Name"),
            Dependents = { SimpleType("Gadget", "Name") }
        };

        var registered = await sut.RegisterAsync(request, "tenant-a", CancellationToken.None);

        registered.Should().Equal("Widget", "Gadget");
        await executor.Received(1).ExecuteAsync(
            Arg.Is<IReadOnlyList<SchemaDescriptor>>(d =>
                d.Select(x => x.TypeName).SequenceEqual(new[] { "Widget", "Gadget" }) &&
                d.All(x => x.OwnerTenantId == "tenant-a")),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
        // Spec §5.2: the api's registry changes only when the queued request succeeds and the api
        // reloads it, which the executor does.
        _registry.Get("Widget").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_TheExecutorsRefusal_ReachesTheCallerUnchanged()
    {
        var executor = Substitute.For<ISchemaRegistrationExecutor>();
        executor.ExecuteAsync(Arg.Any<IReadOnlyList<SchemaDescriptor>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "The worker did not pick the registration up; retry.")));
        var sut = new SchemaRegistrationOrchestrator(
            _resolver, _registry, executor, NullLogger<SchemaRegistrationOrchestrator>.Instance);

        var act = () => sut.RegisterAsync(new SchemaRequest { RootType = SimpleType("Widget", "Name") }, "tenant-a", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unavailable);
    }

    [Fact]
    public async Task RegisterAsync_TypeAlreadyOwnedByAnotherTenant_ThrowsPermissionDenied()
```

**1h. Re-point the four constructions of the orchestrator.** Create `Iverson.Server/Iverson.Api.Tests/Helpers/InProcessSchemaRegistrationExecutor.cs`:
```csharp
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.Tests.Helpers;

/// <summary>
/// For the orchestrator's own tests, which are about validation, not about the queue: applies
/// RegisterSchema's effects in-process, in the worker's order, and writes through
/// <see cref="SchemaRegistry.RegisterAsync"/> so the test's registry holds the type at once, as the
/// api's registry does after its reload in production.
/// </summary>
internal sealed class InProcessSchemaRegistrationExecutor(
    IRecordStoreSchemaManager schemaManager,
    SchemaRegistry registry,
    IDocumentRerenderQueueRepository rerenderQueue) : ISchemaRegistrationExecutor
{
    public async Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct)
    {
        foreach (var descriptor in descriptors)
        {
            await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
            await registry.RegisterAsync(descriptor);
        }

        foreach (var typeName in changedTemplateTypes)
            await rerenderQueue.EnqueueTypeAsync(typeName);
    }
}
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, replace:
```csharp
        _sut = new SchemaRegistrationOrchestrator(
            _schemaManager,
            _resolver,
            _registry,
            Substitute.For<IDocumentRerenderQueueRepository>(),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```
with:
```csharp
        _sut = new SchemaRegistrationOrchestrator(
            _resolver,
            _registry,
            new Helpers.InProcessSchemaRegistrationExecutor(
                _schemaManager, _registry, Substitute.For<IDocumentRerenderQueueRepository>()),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```

In `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`, replace:
```csharp
        _sut = new SchemaRegistrationOrchestrator(
            _schemaManager,
            _resolver,
            _registry,
            _rerenderQueue,
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```
with:
```csharp
        _sut = new SchemaRegistrationOrchestrator(
            _resolver,
            _registry,
            new InProcessSchemaRegistrationExecutor(_schemaManager, _registry, _rerenderQueue),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`, replace:
```csharp
        _schemaRegistration = new SchemaRegistrationOrchestrator(
            _schemaManager, _resolver, _registry, Substitute.For<IDocumentRerenderQueueRepository>(),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```
with:
```csharp
        _schemaRegistration = new SchemaRegistrationOrchestrator(
            _resolver, _registry,
            new InProcessSchemaRegistrationExecutor(_schemaManager, _registry, Substitute.For<IDocumentRerenderQueueRepository>()),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs`, replace:
```csharp
        var schemaRegistration = new SchemaRegistrationOrchestrator(
            fixture.PostgresSchemaManager,
            embeddingResolver,
            registry,
            Substitute.For<IDocumentRerenderQueueRepository>(),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```
with:
```csharp
        // The worker's half of the registration runs in-process here, against the same Postgres;
        // the queue between api and worker is PrivilegedRequestPostgresIntegrationTests' subject.
        // The registry table is the worker's startup DDL, so it is created here.
        var registryRepository = new SchemaRegistryRepository(fixture.PostgresRepository);
        await registryRepository.EnsureTableAsync();
        var schemaRegistration = new SchemaRegistrationOrchestrator(
            embeddingResolver,
            registry,
            new Iverson.Api.PrivilegedRequests.SchemaRegistrationApplier(
                registryRepository,
                new ExistingTableInspector(fixture.PostgresRepository),
                fixture.PostgresSchemaManager,
                Substitute.For<IDocumentRerenderQueueRepository>()),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
```

**1i. The two hand-written registry fakes implement the new members.** In `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs`, replace:
```csharp
    public Task UpsertAsync(string typeName, string schemaJson) => Task.CompletedTask;
    public Task DeleteAsync(string typeName) => Task.CompletedTask;
}
```
with:
```csharp
    public Task UpsertAsync(string typeName, string schemaJson) => Task.CompletedTask;
    public Task DeleteAsync(string typeName) => Task.CompletedTask;

    public Task<IReadOnlyList<string?>> GetOwnersAsync(string typeName) =>
        Task.FromResult<IReadOnlyList<string?>>([]);

    public Task<bool> UpsertIfOwnerAsync(string typeName, string schemaJson, string? ownerTenantId) =>
        Task.FromResult(true);
}
```

In `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs`, replace:
```csharp
    public Task UpsertAsync(string typeName, string schemaJson) => Task.CompletedTask;
    public Task DeleteAsync(string typeName) => Task.CompletedTask;
}
```
with:
```csharp
    public Task UpsertAsync(string typeName, string schemaJson) => Task.CompletedTask;
    public Task DeleteAsync(string typeName) => Task.CompletedTask;

    public Task<IReadOnlyList<string?>> GetOwnersAsync(string typeName) =>
        Task.FromResult<IReadOnlyList<string?>>([]);

    public Task<bool> UpsertIfOwnerAsync(string typeName, string schemaJson, string? ownerTenantId) =>
        Task.FromResult(true);
}
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | command grep -E "error CS" | sort -u | head -40
```
Expected: the test project does not compile. The errors are `CS0246`/`CS0234` for the types this task creates (`Iverson.Api.PrivilegedRequests`, `PrivilegedRequestRow`, `PrivilegedRequestStatus`, `IPrivilegedRequestRepository`, `IExistingTableInspector`, `ExistingTable`, `IPrivilegedRequestLocks`, `PostgresPrivilegedRequestLocks`, `ExistingTableInspector`, `ISchemaRegistrationExecutor`), `CS0117`/`CS1061` for `SchemaRegistry.JsonOptions`, `GetOwnersAsync` and `UpsertIfOwnerAsync`, and `CS1503`/`CS7036` at the four orchestrator constructions. Record the distinct error codes. There is no runtime red for this task: every new test depends on types that do not exist yet.

- [ ] **Step 3: Implement**

**3a. `Iverson.Sql`: the row and its statuses.** Create `Iverson.Server/Iverson.Sql/PrivilegedRequestRow.cs`:
```csharp
namespace Iverson.Sql;

/// <summary>One row of the privileged-request queue (CSR round 12 §5.1).</summary>
/// <param name="Payload">The kind's payload, JSON.</param>
/// <param name="ResultJson">The worker's recorded outcome, JSON; null until the request finishes.</param>
public sealed record PrivilegedRequestRow(Guid Id, string Kind, string Payload, string Status, string? ResultJson);

public static class PrivilegedRequestStatus
{
    public const string Pending   = "pending";
    public const string Running   = "running";
    public const string Succeeded = "succeeded";
    public const string Failed    = "failed";
    public const string Expired   = "expired";
}
```

**3b. The repository.** Create `Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs`:
```csharp
namespace Iverson.Sql;

/// <summary>
/// The queue through which the api hands privileged work to the worker (CSR round 12 §5):
/// RegisterSchema's DDL and registry write, <c>/admin/reconcile/{type}</c> and DLQ replay. The api
/// inserts a row and polls its status; the worker claims it, runs it and records the outcome.
/// A plumbing table with no tenant column, so no RLS policy.
/// </summary>
public sealed class PrivilegedRequestRepository(
    string tableName,
    IRecordStoreQueryExecutor sql) : IPrivilegedRequestRepository
{
    // RequestedAt and ExpiresAt come from the database clock, the clock the claim's expiry test
    // reads, so the api's and the worker's clocks never have to agree. A null expiry leaves
    // ExpiresAt NULL (NULL * interval is NULL): the request never expires.
    public Task InsertAsync(Guid id, string kind, string payloadJson, string? requestedBy, TimeSpan? expiresAfter) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "Kind", "Payload", "RequestedBy", "Status", "RequestedAt", "ExpiresAt")
            VALUES (@Id, @Kind, @Payload::jsonb, @RequestedBy, '{PrivilegedRequestStatus.Pending}', now(),
                    now() + @ExpiresAfterSeconds * interval '1 second')
            """,
            new
            {
                Id = id,
                Kind = kind,
                Payload = payloadJson,
                RequestedBy = requestedBy,
                ExpiresAfterSeconds = expiresAfter?.TotalSeconds
            });

    public Task<PrivilegedRequestRow?> GetAsync(Guid id) =>
        sql.QuerySingleOrDefaultAsync<PrivilegedRequestRow>(
            $"""
            SELECT "Id", "Kind", "Payload", "Status", "ResultJson"
            FROM "{tableName}"
            WHERE "Id" = @Id
            """,
            new { Id = id });

    // Sets only Status, which is all the api's column grant allows (§6.2). A request the worker
    // has already claimed is 'running', so this matches no row and the claim stands.
    public async Task<bool> ExpireIfPendingAsync(Guid id) =>
        await sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}" SET "Status" = '{PrivilegedRequestStatus.Expired}'
            WHERE "Id" = @Id AND "Status" = '{PrivilegedRequestStatus.Pending}'
            """,
            new { Id = id }) == 1;

    public Task<IEnumerable<PrivilegedRequestRow>> ListClaimableAsync(int limit) =>
        sql.QueryAsync<PrivilegedRequestRow>(
            $"""
            SELECT "Id", "Kind", "Payload", "Status", "ResultJson"
            FROM "{tableName}"
            WHERE "Status" = '{PrivilegedRequestStatus.Pending}'
              AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())
            ORDER BY "RequestedAt"
            LIMIT @Limit
            """,
            new { Limit = limit });

    // False when another worker replica claimed it first, the api expired it, or its expiry passed.
    public async Task<bool> TryClaimAsync(Guid id) =>
        await sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}" SET "Status" = '{PrivilegedRequestStatus.Running}', "ClaimedAt" = now()
            WHERE "Id" = @Id AND "Status" = '{PrivilegedRequestStatus.Pending}'
              AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())
            """,
            new { Id = id }) == 1;

    public Task CompleteAsync(Guid id, string status, string resultJson) =>
        sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}"
            SET "Status" = @Status, "ResultJson" = @ResultJson::jsonb, "CompletedAt" = now()
            WHERE "Id" = @Id
            """,
            new { Id = id, Status = status, ResultJson = resultJson });

    // A request claimed before the cutoff and still 'running' belongs to a worker that stopped
    // without recording an outcome. A NULL ClaimedAt (never claimed) matches nothing. A request
    // that is in fact still running past the cutoff is marked too; its own CompleteAsync, which
    // matches on "Id" alone, then records the real outcome over this one.
    public Task<int> FailAbandonedAsync(TimeSpan claimedBefore, string resultJson) =>
        sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}"
            SET "Status" = '{PrivilegedRequestStatus.Failed}', "ResultJson" = @ResultJson::jsonb, "CompletedAt" = now()
            WHERE "Status" = '{PrivilegedRequestStatus.Running}'
              AND "ClaimedAt" < now() - @AgeSeconds * interval '1 second'
            """,
            new { ResultJson = resultJson, AgeSeconds = claimedBefore.TotalSeconds });

    // Finished means succeeded, failed or expired, and also a pending request whose expiry has
    // passed: nothing can claim it any more, because its api gave up without marking it.
    public Task<int> DeleteFinishedOlderThanAsync(TimeSpan age) =>
        sql.ExecuteAsync(
            $"""
            DELETE FROM "{tableName}"
            WHERE "RequestedAt" < now() - @AgeSeconds * interval '1 second'
              AND ("Status" IN ('{PrivilegedRequestStatus.Succeeded}', '{PrivilegedRequestStatus.Failed}', '{PrivilegedRequestStatus.Expired}')
                   OR ("Status" = '{PrivilegedRequestStatus.Pending}' AND "ExpiresAt" < now()))
            """,
            new { AgeSeconds = age.TotalSeconds });
}
```

**3c. The adoption queries.** Create `Iverson.Server/Iverson.Sql/ExistingTableInspector.cs`:
```csharp
namespace Iverson.Sql;

/// <summary>What sits in <c>public</c> under a type's derived table name.</summary>
/// <param name="IsOrdinaryTable"><c>relkind = 'r'</c>: not a view, sequence, index or partitioned table.</param>
public sealed record ExistingTable(bool IsOrdinaryTable, bool HasTenantColumn);

/// <summary>
/// RegisterSchema's adoption check (CSR round 12 §5.3 step 2), for a type with no registry row
/// whose derived table already exists.
/// </summary>
public sealed class ExistingTableInspector(IRecordStoreQueryExecutor sql) : IExistingTableInspector
{
    public Task<ExistingTable?> DescribeAsync(string tableName, string tenantColumn) =>
        sql.QuerySingleOrDefaultAsync<ExistingTable>(
            """
            SELECT c.relkind = 'r' AS "IsOrdinaryTable",
                   EXISTS (SELECT 1 FROM pg_attribute a
                           WHERE a.attrelid = c.oid AND a.attname = @TenantColumn
                             AND a.attnum > 0 AND NOT a.attisdropped) AS "HasTenantColumn"
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = @TableName
            """,
            new { TableName = tableName, TenantColumn = tenantColumn });

    // Under iverson_maintenance (BYPASSRLS): the check must see every tenant's rows. A NULL tenant
    // value counts as another tenant's.
    public Task<bool> HasRowsOfAnotherTenantAsync(string tableName, string tenantColumn, string ownerTenantId) =>
        sql.QuerySingleOrDefaultAsync<bool>(
            $"""
            SELECT EXISTS (SELECT 1 FROM {PostgresIdentifier.Quote(tableName)}
                           WHERE {PostgresIdentifier.Quote(tenantColumn)} IS DISTINCT FROM @Owner)
            """,
            new { Owner = ownerTenantId },
            RecordStoreRole.Maintenance);
}
```

**3d. The locks.** Create `Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs`:
```csharp
using Dapper;
using Npgsql;

namespace Iverson.Sql;

/// <summary>
/// The worker's locks for privileged requests (CSR round 12 §5.1): session advisory locks, each on
/// its own pooled connection for as long as it is held, released in <see cref="IAsyncDisposable.DisposeAsync"/>.
/// <para>
/// The per-table keys elsewhere are <c>hashtext(name)::bigint</c> (<see cref="PostgresSchemaManager"/>,
/// <see cref="StartupDdl"/>): sign-extended <c>int4</c> values in [-2^31, 2^31). These keys sit above
/// that range, so they can never collide with a table's: the registration lock is 2^41, and a type
/// lock is 2^40 + hashtext(lower(type)), in [2^40 - 2^31, 2^40 + 2^31).
/// </para>
/// </summary>
public sealed class PostgresPrivilegedRequestLocks(string connectionString) : IPrivilegedRequestLocks
{
    internal const long RegistrationKey = 1L << 41;
    internal const long TypeKeyBase     = 1L << 40;

    public Task<IAsyncDisposable> AcquireRegistrationAsync(CancellationToken ct) =>
        AcquireAsync("@Key", new { Key = RegistrationKey }, ct);

    // lower(): the registry resolves type names case-insensitively, so two spellings of one
    // type must share a lock.
    public Task<IAsyncDisposable> AcquireTypeAsync(string typeName, CancellationToken ct) =>
        AcquireAsync("@Base + hashtext(lower(@TypeName))", new { Base = TypeKeyBase, TypeName = typeName }, ct);

    private async Task<IAsyncDisposable> AcquireAsync(string keySql, object param, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition($"SELECT pg_advisory_lock({keySql})", param, cancellationToken: ct));
            return new Lease(conn, keySql, param);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(NpgsqlConnection conn, string keySql, object param) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Unlock before the connection goes back to the pool: a pooled connection keeps any
            // session lock it still holds.
            try
            {
                await conn.ExecuteAsync($"SELECT pg_advisory_unlock({keySql})", param);
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}
```

**3e. The interfaces.** In `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, replace:
```csharp
public interface ISchemaRegistryRepository
{
    Task EnsureTableAsync();
    Task<IEnumerable<(string TypeName, string SchemaJson)>> LoadAllAsync();
    Task UpsertAsync(string typeName, string schemaJson);
    Task DeleteAsync(string typeName);
}
```
with:
```csharp
public interface ISchemaRegistryRepository
{
    Task EnsureTableAsync();
    Task<IEnumerable<(string TypeName, string SchemaJson)>> LoadAllAsync();
    Task UpsertAsync(string typeName, string schemaJson);
    Task DeleteAsync(string typeName);

    /// <summary>
    /// The stored owner of every row whose type name equals <paramref name="typeName"/> ignoring
    /// case: null for an ownerless row or a legacy row with no owner key. Empty when the type has
    /// no row.
    /// </summary>
    Task<IReadOnlyList<string?>> GetOwnersAsync(string typeName);

    /// <summary>
    /// Inserts the row, or replaces it only when its stored owner is
    /// <paramref name="ownerTenantId"/> (null matching null). False when a row of another owner
    /// kept its place.
    /// </summary>
    Task<bool> UpsertIfOwnerAsync(string typeName, string schemaJson, string? ownerTenantId);
}

public interface IExistingTableInspector
{
    /// <summary>The relation named <paramref name="tableName"/> in <c>public</c>, or null when there is none.</summary>
    Task<ExistingTable?> DescribeAsync(string tableName, string tenantColumn);

    /// <summary>
    /// Whether any row of the table has a tenant other than <paramref name="ownerTenantId"/>, read
    /// across every tenant under <c>iverson_maintenance</c>.
    /// </summary>
    Task<bool> HasRowsOfAnotherTenantAsync(string tableName, string tenantColumn, string ownerTenantId);
}

public interface IPrivilegedRequestRepository
{
    /// <summary>A null <paramref name="expiresAfter"/> means the request never expires.</summary>
    Task InsertAsync(Guid id, string kind, string payloadJson, string? requestedBy, TimeSpan? expiresAfter);
    Task<PrivilegedRequestRow?> GetAsync(Guid id);

    /// <summary>pending → expired. False when the request was no longer pending.</summary>
    Task<bool> ExpireIfPendingAsync(Guid id);

    /// <summary>Pending, unexpired requests, oldest first.</summary>
    Task<IEnumerable<PrivilegedRequestRow>> ListClaimableAsync(int limit);

    /// <summary>pending and unexpired → running. False when another claim or the expiry won.</summary>
    Task<bool> TryClaimAsync(Guid id);

    Task CompleteAsync(Guid id, string status, string resultJson);

    /// <summary>
    /// running → failed, recording <paramref name="resultJson"/>, for every request claimed longer
    /// ago than <paramref name="claimedBefore"/>: its worker stopped without recording an outcome.
    /// </summary>
    Task<int> FailAbandonedAsync(TimeSpan claimedBefore, string resultJson);

    Task<int> DeleteFinishedOlderThanAsync(TimeSpan age);
}

public interface IPrivilegedRequestLocks
{
    /// <summary>The one registration lock, held while a RegisterSchema request runs.</summary>
    Task<IAsyncDisposable> AcquireRegistrationAsync(CancellationToken ct);

    /// <summary>A lock per type name, case-insensitive, held while a reconcile of that type runs.</summary>
    Task<IAsyncDisposable> AcquireTypeAsync(string typeName, CancellationToken ct);
}
```

**3f. The owner read and the conditional write.** In `Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs`, replace:
```csharp
    public Task DeleteAsync(string typeName) =>
        sql.ExecuteAsync("DELETE FROM _iverson_schema WHERE type_name = @TypeName", new { TypeName = typeName });
}
```
with:
```csharp
    public Task DeleteAsync(string typeName) =>
        sql.ExecuteAsync("DELETE FROM _iverson_schema WHERE type_name = @TypeName", new { TypeName = typeName });

    // SchemaRegistry resolves type names ignoring case, but type_name is a case-sensitive primary
    // key, so a case variant of a stored name is a separate row here. lower() finds every row the
    // registry would fold together. The owner is the ownerTenantId key inside schema_json; ->>
    // gives NULL both for an explicit null and for a legacy row without the key.
    public async Task<IReadOnlyList<string?>> GetOwnersAsync(string typeName) =>
        (await sql.QueryAsync<string?>(
            "SELECT schema_json->>'ownerTenantId' FROM _iverson_schema WHERE lower(type_name) = lower(@TypeName)",
            new { TypeName = typeName })).ToList();

    // The conflict arm only fires for the stored owner: a row of another owner makes this affect
    // zero rows and leaves that row as it was.
    public async Task<bool> UpsertIfOwnerAsync(string typeName, string schemaJson, string? ownerTenantId) =>
        await sql.ExecuteAsync(
            """
            INSERT INTO _iverson_schema (type_name, schema_json, updated_at)
            VALUES (@TypeName, @Json::jsonb, now())
            ON CONFLICT (type_name) DO UPDATE
                SET schema_json = EXCLUDED.schema_json,
                    updated_at  = EXCLUDED.updated_at
                WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner
            """,
            new { TypeName = typeName, Json = schemaJson, Owner = ownerTenantId }) > 0;
}
```

**3g. DI in `Iverson.Sql`.** In `Iverson.Server/Iverson.Sql/ServiceCollectionExtensions.cs`, replace:
```csharp
        services.AddSingleton<ISchemaRegistryRepository>(sp =>
            new SchemaRegistryRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));

        return services;
```
with:
```csharp
        services.AddSingleton<ISchemaRegistryRepository>(sp =>
            new SchemaRegistryRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));

        // CSR round 12 §5.3: the worker's RegisterSchema adoption check and its request locks.
        services.AddSingleton<IExistingTableInspector>(sp =>
            new ExistingTableInspector(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
        services.AddSingleton<IPrivilegedRequestLocks>(_ => new PostgresPrivilegedRequestLocks(connectionString));

        return services;
```

**3h. The registry's JSON options.** In `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`, replace:
```csharp
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
```
with:
```csharp
    /// <summary>
    /// The options every <c>_iverson_schema</c> row is written and read with. The privileged-request
    /// payloads (CSR round 12 §5.2) carry descriptors with these same options, so the worker reads
    /// exactly what <see cref="LoadAsync"/> would.
    /// </summary>
    internal static JsonSerializerOptions JsonOptions => s_jsonOptions;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
```

**3i. The seam.** Create `Iverson.Server/Iverson.Api/Grpc/ISchemaRegistrationExecutor.cs`:
```csharp
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

/// <summary>
/// RegisterSchema's privileged effects (CSR round 12 §5): per descriptor in order, the DDL and the
/// registry write, then the re-render enqueue for each changed template. The api's implementation
/// (<c>QueuedSchemaRegistrationExecutor</c>) queues them for the worker and waits; the worker's
/// (<c>SchemaRegistrationApplier</c>) applies them. Failures are <c>RpcException</c>s carrying the
/// status the caller receives.
/// </summary>
public interface ISchemaRegistrationExecutor
{
    Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct);
}
```

**3j. The orchestrator.** In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
public sealed class SchemaRegistrationOrchestrator(
    IRecordStoreSchemaManager schemaManager,
    IEmbeddingServiceResolver resolver,
    SchemaRegistry registry,
    IDocumentRerenderQueueRepository rerenderQueue,
    ILogger<SchemaRegistrationOrchestrator> logger)
```
with:
```csharp
public sealed class SchemaRegistrationOrchestrator(
    IEmbeddingServiceResolver resolver,
    SchemaRegistry registry,
    ISchemaRegistrationExecutor executor,
    ILogger<SchemaRegistrationOrchestrator> logger)
```

In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
            var priorRegistered = registry.Get(typeDesc.TypeName);
            if (priorRegistered is not null && priorRegistered.OwnerTenantId != ownerTenantId)
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    $"Type '{typeDesc.TypeName}' is registered to another tenant and cannot be re-registered here."));
```
with:
```csharp
            // The fast pre-check, against this process's cache. The worker repeats it against the
            // stored row under the registration lock (SchemaRegistrationApplier), which is the check
            // that holds when two registrations race.
            var priorRegistered = registry.Get(typeDesc.TypeName);
            if (priorRegistered is not null && priorRegistered.OwnerTenantId != ownerTenantId)
                throw PrivilegedRequests.SchemaRegistrationApplier.OwnedByAnotherTenant(typeDesc.TypeName);
```

In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
        // registry.Get still returns the PRIOR descriptor (registry.RegisterAsync in phase 3
        // below overwrites it). Compared on DocumentTemplateSource — the raw template string —
```
with:
```csharp
        // registry.Get still returns the PRIOR descriptor (this process's registry changes only by
        // reloading once phase 3 succeeds). Compared on DocumentTemplateSource — the raw template string —
```

In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace:
```csharp
        // Phase 3: apply + register. Only reached once every type in the request has passed
        // every validation above — an invalid template never applies DDL or persists.
        var registered = new List<string>();
        foreach (var descriptor in descriptors)
        {
            try
            {
                await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
            }
            catch (SchemaDriftException ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
            }

            var stamped = descriptor with { OwnerTenantId = ownerTenantId };
            await registry.RegisterAsync(stamped);
            registered.Add(stamped.TypeName);
        }

        // A changed template invalidates every document of that type, because the rendered
        // text is derived data with no stored copy — a type-level row is how "re-render
        // everything of this type" is represented before the key set is known (T8 expands it
        // by paging every entity of the type).
        foreach (var typeName in changedTemplateTypes)
            await rerenderQueue.EnqueueTypeAsync(typeName);

        return registered;
```
with:
```csharp
        // Phase 3: the privileged effects — the DDL, the registry write and the re-render
        // enqueue — which the worker runs (CSR round 12 §5). Only reached once every type in the
        // request has passed every validation above — an invalid template never applies DDL or
        // persists. The descriptors are owner-stamped here and go in request order.
        var stamped = descriptors.Select(d => d with { OwnerTenantId = ownerTenantId }).ToList();
        await executor.ExecuteAsync(stamped, changedTemplateTypes, ct);

        return stamped.Select(d => d.TypeName).ToList();
```

**3k. The table.** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestSchema.cs`:
```csharp
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// The privileged-request queue (CSR round 12 §5.1), created at startup through ApplySchemaAsync
/// like the other plumbing tables. It has no tenant column, so it has no RLS policy.
/// </summary>
internal static class PrivilegedRequestSchema
{
    public const string TableName = "IversonPrivilegedRequests";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema>
        {
            new("Kind",        "text", false),
            new("Payload",     "jsonb", false),
            new("RequestedBy", "text", true),
            new("Status",      "text", false),
            new("ResultJson",  "jsonb", true),
            new("RequestedAt", "timestamptz", false),
            new("ExpiresAt",   "timestamptz", true),
            new("CompletedAt", "timestamptz", true),
            new("ClaimedAt",   "timestamptz", true),
        });
}
```

**3l. Kinds, payload and result.** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs`:
```csharp
using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

internal static class PrivilegedRequestKinds
{
    public const string RegisterSchema = "RegisterSchema";
}

/// <summary>
/// One RegisterSchema call's privileged half: the owner-stamped descriptors in request order and
/// the type names whose document template changed. Serialised with
/// <see cref="SchemaRegistry.JsonOptions"/>, as <c>_iverson_schema</c> rows are.
/// </summary>
internal sealed record RegisterSchemaPayload(
    IReadOnlyList<SchemaDescriptor> Descriptors,
    IReadOnlyList<string> ChangedTemplateTypes);

/// <summary>
/// What the worker records as a request's ResultJson. <see cref="StatusCode.OK"/> is success; any
/// other code is the status the api answers with, and <see cref="Message"/> its detail.
/// </summary>
public sealed record PrivilegedRequestResult(StatusCode Code, string? Message = null, int? Count = null)
{
    public static PrivilegedRequestResult Ok(int? count = null) => new(StatusCode.OK, null, count);

    public string ToJson() => JsonSerializer.Serialize(this, SchemaRegistry.JsonOptions);

    /// <summary>The recorded result, or null while the request has none.</summary>
    public static PrivilegedRequestResult? From(PrivilegedRequestRow request) =>
        request.ResultJson is null
            ? null
            : JsonSerializer.Deserialize<PrivilegedRequestResult>(request.ResultJson, SchemaRegistry.JsonOptions);
}
```

**3m. The api's waits.** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs`:
```csharp
namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// How long the api waits on a privileged request (CSR round 12 §5.2). RegisterSchema's claim and
/// run waits add to 25 s, below every edge's default timeout on the gRPC path (nginx 60 s, ALB 60 s
/// idle, GCE and AGIC 30 s); a healthy worker answers in about 1 s. A DI singleton, so tests can
/// shorten them.
/// </summary>
internal sealed record PrivilegedRequestWaits
{
    /// <summary>How long RegisterSchema waits for the worker to claim it; also the row's expiry.</summary>
    public TimeSpan RegisterClaim { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How much longer RegisterSchema waits once the worker has claimed it.</summary>
    public TimeSpan RegisterRun { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
```

**3n. The api's submit and poll.** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestClient.cs`:
```csharp
using System.Diagnostics;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>The api's side of a privileged request (CSR round 12 §5.2): queue it, then poll it.</summary>
internal sealed class PrivilegedRequestClient(IPrivilegedRequestRepository requests, PrivilegedRequestWaits waits)
{
    public PrivilegedRequestWaits Waits => waits;

    public async Task<Guid> SubmitAsync(string kind, string payloadJson, string? requestedBy, TimeSpan? expiresAfter)
    {
        var id = Guid.CreateVersion7();
        await requests.InsertAsync(id, kind, payloadJson, requestedBy, expiresAfter);
        return id;
    }

    public async Task<PrivilegedRequestRow> ReadAsync(Guid id) =>
        await requests.GetAsync(id)
        ?? throw new InvalidOperationException($"Privileged request {id} is no longer recorded.");

    /// <summary>
    /// Re-reads the request every <see cref="PrivilegedRequestWaits.PollInterval"/> while its status
    /// satisfies <paramref name="keepWaiting"/>, for at most <paramref name="timeout"/>, and returns
    /// the last row read. What a status that still satisfies it means is the caller's to decide.
    /// </summary>
    public async Task<PrivilegedRequestRow> WaitWhileAsync(
        Guid id, Func<string, bool> keepWaiting, TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var row = await ReadAsync(id);
            if (!keepWaiting(row.Status) || Stopwatch.GetElapsedTime(started) >= timeout)
                return row;
            await Task.Delay(waits.PollInterval, ct);
        }
    }

    public Task<bool> ExpireIfPendingAsync(Guid id) => requests.ExpireIfPendingAsync(id);
}
```

**3o. The api's executor.** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs`:
```csharp
using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// RegisterSchema's privileged half, from the api (CSR round 12 §5.2): queue the owner-stamped
/// descriptors, wait up to <see cref="PrivilegedRequestWaits.RegisterClaim"/> for the worker to
/// claim them and then up to <see cref="PrivilegedRequestWaits.RegisterRun"/> for the result. On
/// success this process's registry is reloaded before the caller is answered, so the caller's next
/// write on this replica sees the type; other replicas catch up on their 30 s poll.
/// </summary>
internal sealed class QueuedSchemaRegistrationExecutor(
    PrivilegedRequestClient requests,
    SchemaRegistry registry) : ISchemaRegistrationExecutor
{
    private const string NotPickedUp = "The worker did not pick the registration up; retry.";

    public async Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct)
    {
        var waits = requests.Waits;
        var payload = JsonSerializer.Serialize(
            new RegisterSchemaPayload(descriptors, changedTemplateTypes), SchemaRegistry.JsonOptions);

        // The row expires with the claim wait, so a worker that only gets to it after the api has
        // given up can never run a registration nobody is waiting for.
        var id = await requests.SubmitAsync(
            PrivilegedRequestKinds.RegisterSchema,
            payload,
            requestedBy: descriptors.Select(d => d.OwnerTenantId).FirstOrDefault(),
            expiresAfter: waits.RegisterClaim);

        var request = await requests.WaitWhileAsync(
            id, status => status == PrivilegedRequestStatus.Pending, waits.RegisterClaim, ct);

        if (request.Status == PrivilegedRequestStatus.Pending)
        {
            // Conditional on 'pending': if the worker's claim got there first, this changes nothing
            // and the claimed request is waited on below.
            if (await requests.ExpireIfPendingAsync(id))
                throw new RpcException(new Status(StatusCode.Unavailable, NotPickedUp));
            request = await requests.ReadAsync(id);
        }

        if (request.Status == PrivilegedRequestStatus.Running)
            request = await requests.WaitWhileAsync(
                id, status => status == PrivilegedRequestStatus.Running, waits.RegisterRun, ct);

        switch (request.Status)
        {
            case PrivilegedRequestStatus.Succeeded:
                await registry.LoadAsync(ct);
                return;
            case PrivilegedRequestStatus.Running:
                throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                    "The registration is still being applied. It completes in the worker; registering " +
                    "again once it has is idempotent."));
            case PrivilegedRequestStatus.Failed:
                var result = PrivilegedRequestResult.From(request)!;
                throw new RpcException(new Status(result.Code, result.Message ?? string.Empty));
            default:
                throw new RpcException(new Status(StatusCode.Unavailable, NotPickedUp));
        }
    }
}
```

**3p. The worker's executor (§5.3).** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs`:
```csharp
using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// RegisterSchema's privileged half, in the worker (CSR round 12 §5.3), per descriptor in batch
/// order: the stored owner (#7), the adoption check (#1), the DDL and the conditional registry
/// write; then the re-renders. The caller holds the registration lock.
/// </summary>
internal sealed class SchemaRegistrationApplier(
    ISchemaRegistryRepository registryRepository,
    IExistingTableInspector tables,
    IRecordStoreSchemaManager schemaManager,
    IDocumentRerenderQueueRepository rerenderQueue) : ISchemaRegistrationExecutor
{
    internal static RpcException OwnedByAnotherTenant(string typeName) =>
        new(new Status(StatusCode.PermissionDenied,
            $"Type '{typeName}' is registered to another tenant and cannot be re-registered here."));

    public async Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct)
    {
        foreach (var descriptor in descriptors)
        {
            // 1. The stored owner, from the row itself rather than any process's cache. Every case
            // variant of the name counts, because the registry folds them into one type.
            var storedOwners = await registryRepository.GetOwnersAsync(descriptor.TypeName);
            if (storedOwners.Any(owner => owner != descriptor.OwnerTenantId))
                throw OwnedByAnotherTenant(descriptor.TypeName);

            // 2. A type with no registry row whose table already exists would adopt that table.
            if (storedOwners.Count == 0)
                await RequireAdoptableAsync(descriptor);

            // 3. The DDL.
            try
            {
                await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
            }
            catch (SchemaDriftException ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
            }

            // 4. The registry write, which replaces a row only of the same owner.
            var json = JsonSerializer.Serialize(descriptor, SchemaRegistry.JsonOptions);
            if (!await registryRepository.UpsertIfOwnerAsync(descriptor.TypeName, json, descriptor.OwnerTenantId))
                throw OwnedByAnotherTenant(descriptor.TypeName);
        }

        // 5. A changed template invalidates every document of that type, because the rendered
        // text is derived data with no stored copy — a type-level row is how "re-render
        // everything of this type" is represented before the key set is known.
        foreach (var typeName in changedTemplateTypes)
            await rerenderQueue.EnqueueTypeAsync(typeName);
    }

    // Adopting is right only for this type's own table, the documented model-change recovery
    // (delete the registry row, then register again). A plumbing table, a view, or a table that
    // holds another tenant's rows is refused.
    private async Task RequireAdoptableAsync(SchemaDescriptor descriptor)
    {
        var existing = await tables.DescribeAsync(descriptor.TableName, descriptor.TenantColumn);
        if (existing is null)
            return;

        if (!existing.IsOrdinaryTable)
            throw NotAdoptable(descriptor, "is not an ordinary table");
        if (!existing.HasTenantColumn)
            throw NotAdoptable(descriptor, $"has no {descriptor.TenantColumn} column");
        if (descriptor.OwnerTenantId is { } owner &&
            await tables.HasRowsOfAnotherTenantAsync(descriptor.TableName, descriptor.TenantColumn, owner))
            throw NotAdoptable(descriptor, "holds rows of another tenant");
    }

    private static RpcException NotAdoptable(SchemaDescriptor descriptor, string reason) =>
        new(new Status(StatusCode.AlreadyExists,
            $"Type '{descriptor.TypeName}' is not registered, but its table '{descriptor.TableName}' already " +
            $"exists and {reason}, so the registration cannot take it over. Register the type under another name."));
}
```

**3q. Running one claimed request.** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs`:
```csharp
using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

public interface IPrivilegedRequestExecutor
{
    /// <summary>Runs one claimed request. Never throws: every outcome is a result to record.</summary>
    Task<PrivilegedRequestResult> RunAsync(PrivilegedRequestRow request, CancellationToken ct);
}

/// <summary>
/// Runs a claimed privileged request in the worker (CSR round 12 §5.1). It first reloads this
/// process's registry, so a type registered moments ago resolves; then each kind takes its own
/// session advisory lock. A refusal is recorded with its status and message; an unexpected
/// exception is recorded as Internal with a generic message, and its details go to the log.
/// </summary>
internal sealed class PrivilegedRequestExecutor(
    SchemaRegistry registry,
    SchemaRegistrationApplier registration,
    IPrivilegedRequestLocks locks,
    ILogger<PrivilegedRequestExecutor> logger) : IPrivilegedRequestExecutor
{
    public async Task<PrivilegedRequestResult> RunAsync(PrivilegedRequestRow request, CancellationToken ct)
    {
        try
        {
            await registry.LoadAsync(ct);

            return request.Kind switch
            {
                PrivilegedRequestKinds.RegisterSchema => await RegisterSchemaAsync(request, ct),
                _ => throw new InvalidOperationException($"Unknown privileged request kind '{request.Kind}'.")
            };
        }
        catch (RpcException ex)
        {
            return new PrivilegedRequestResult(ex.StatusCode, ex.Status.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PrivilegedRequestResult(StatusCode.Unavailable,
                "The worker stopped before the request finished; retry.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Privileged request {RequestId} ({Kind}) failed", request.Id, request.Kind);
            return new PrivilegedRequestResult(StatusCode.Internal,
                "The request failed in the worker; the worker log has the details.");
        }
    }

    // One global lock: registrations run one at a time across every worker replica, so one
    // registration's owner read and registry write never interleave with another's.
    private async Task<PrivilegedRequestResult> RegisterSchemaAsync(PrivilegedRequestRow request, CancellationToken ct)
    {
        var payload = Deserialize<RegisterSchemaPayload>(request);

        await using var registrationLock = await locks.AcquireRegistrationAsync(ct);
        await registration.ExecuteAsync(payload.Descriptors, payload.ChangedTemplateTypes, ct);
        return PrivilegedRequestResult.Ok();
    }

    private static T Deserialize<T>(PrivilegedRequestRow request) where T : class =>
        JsonSerializer.Deserialize<T>(request.Payload, SchemaRegistry.JsonOptions)
        ?? throw new InvalidOperationException($"Privileged request {request.Id} has an empty payload.");
}
```

**3r. The hosted worker.** Create `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs`:
```csharp
using System.Collections.Concurrent;
using Grpc.Core;
using Iverson.Api.Consumers;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// Runs the privileged requests the api queues (CSR round 12 §5.1), in the worker role only. Every
/// second it lists pending, unexpired requests, oldest first, and claims each with a conditional
/// UPDATE, so two worker replicas never run the same one. Each claimed request runs on its own
/// task while the loop keeps claiming; what serialises them is only the executor's advisory locks.
/// Every hour it fails requests still 'running' more than an hour after their claim, and deletes
/// finished requests older than a day.
/// </summary>
internal sealed class PrivilegedRequestWorker(
    IPrivilegedRequestRepository requests,
    IPrivilegedRequestExecutor executor,
    ILogger<PrivilegedRequestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval      = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CleanupInterval   = TimeSpan.FromHours(1);
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(1);
    private const int ClaimBatchSize = 16;

    // A claim this old belongs to a worker that stopped mid-request: no caller waits longer than
    // 60 s. Without this the row would stay 'running' for good, because reconcile and replay
    // requests never expire. Unavailable is the code the executor records when the worker stops.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);
    private static readonly PrivilegedRequestResult Abandoned =
        new(StatusCode.Unavailable, "The worker stopped before finishing this request.");

    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await ConsumerResilience.RunWithRestartAsync(() => PollLoopAsync(ct), logger, "PrivilegedRequests", ct);

        // Each claimed request records its own outcome. Let the ones still running do so before the
        // host stops; they see the same token, so a cancellable one ends promptly.
        await WhenIdleAsync();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            await Task.Delay(PollInterval, ct);
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        foreach (var (id, task) in _inFlight)
            if (task.IsCompleted)
                _inFlight.TryRemove(id, out _);

        if (DateTimeOffset.UtcNow - _lastCleanup >= CleanupInterval)
        {
            await requests.FailAbandonedAsync(AbandonedAfter, Abandoned.ToJson());
            await requests.DeleteFinishedOlderThanAsync(FinishedRetention);
            _lastCleanup = DateTimeOffset.UtcNow;
        }

        foreach (var request in await requests.ListClaimableAsync(ClaimBatchSize))
        {
            if (!await requests.TryClaimAsync(request.Id))
                continue;

            _inFlight[request.Id] = Task.Run(() => RunClaimedAsync(request, ct), CancellationToken.None);
        }
    }

    internal Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values);

    private async Task RunClaimedAsync(PrivilegedRequestRow request, CancellationToken ct)
    {
        var result = await executor.RunAsync(request, ct);
        try
        {
            await requests.CompleteAsync(
                request.Id,
                result.Code == StatusCode.OK ? PrivilegedRequestStatus.Succeeded : PrivilegedRequestStatus.Failed,
                result.ToJson());
        }
        catch (Exception ex)
        {
            // The request ran; only recording it failed. Its row stays 'running', so its caller
            // gets DeadlineExceeded (RegisterSchema) or 202 (the admin routes).
            logger.LogError(ex, "Privileged request {RequestId} ran, but its outcome could not be recorded", request.Id);
        }
    }
}
```

**3s. `Program.cs`.** In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
builder.Services.AddSingleton<ISchemaRegistrationOrchestrator, SchemaRegistrationOrchestrator>();
```
with:
```csharp
builder.Services.AddSingleton<ISchemaRegistrationOrchestrator, SchemaRegistrationOrchestrator>();
// CSR round 12 §5: RegisterSchema's DDL and registry write run in the worker. The api queues them
// in IversonPrivilegedRequests and waits; the worker-only services below run them.
builder.Services.AddSingleton<IPrivilegedRequestRepository>(sp => new PrivilegedRequestRepository(
    Iverson.Api.PrivilegedRequests.PrivilegedRequestSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton(new Iverson.Api.PrivilegedRequests.PrivilegedRequestWaits());
builder.Services.AddSingleton<Iverson.Api.PrivilegedRequests.PrivilegedRequestClient>();
builder.Services.AddSingleton<ISchemaRegistrationExecutor, Iverson.Api.PrivilegedRequests.QueuedSchemaRegistrationExecutor>();
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DocumentRerenderQueueWorker>();
}
```
with:
```csharp
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DocumentRerenderQueueWorker>();

    // CSR round 12 §5.1: the worker runs the privileged requests the api queues.
    builder.Services.AddSingleton<Iverson.Api.PrivilegedRequests.SchemaRegistrationApplier>();
    builder.Services.AddSingleton<Iverson.Api.PrivilegedRequests.IPrivilegedRequestExecutor, Iverson.Api.PrivilegedRequests.PrivilegedRequestExecutor>();
    builder.Services.AddHostedService<Iverson.Api.PrivilegedRequests.PrivilegedRequestWorker>();
}
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
await schemaManager.ApplySchemaAsync(Iverson.Api.Tenancy.TenantSchema.Table);
```
with:
```csharp
await schemaManager.ApplySchemaAsync(Iverson.Api.Tenancy.TenantSchema.Table);
await schemaManager.ApplySchemaAsync(Iverson.Api.PrivilegedRequests.PrivilegedRequestSchema.Table);
```

- [ ] **Step 4: Run the tests and see them pass**

Unit tests (no containers):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.SchemaRegistrationApplierTests.|FullyQualifiedName~.QueuedSchemaRegistrationExecutorTests.|FullyQualifiedName~.PrivilegedRequestExecutorTests.|FullyQualifiedName~.PrivilegedRequestWorkerTests."
```
Expected: 27 passed, 0 failed (11 + 6 + 5 + 5).

Then the classes whose orchestrator construction changed, and the rest of the non-container suite:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failed. `SchemaRegistrationOrchestratorTests` has 2 more tests than before this task; `DocumentTemplateValidationTests` and `ObjectMappingGrpcServiceTests` keep their counts. Then:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failed (`Iverson.Sql.Tests` has no test of the changed files' new members; this proves the project still builds and nothing else moved).

Container tests (**heavy: each command starts containers. Run each alone, one after the other.**):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.PrivilegedRequestPostgresIntegrationTests."
```
Expected: 21 passed, 0 failed.
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.RegisterSchemaAuthorizationIntegrationTests.|FullyQualifiedName~.SchemaRegistryPostgresRoundTripTests."
```
Expected: 2 passed, 0 failed (this starts Postgres, StarRocks and Qdrant for the first class).

- [ ] **Step 5: Mutation proof** (make each mutation by hand, undo it by hand; M1, M2 and M6 run the unit command from Step 4, M3, M4 and M5 the `PrivilegedRequestPostgresIntegrationTests` command, alone)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | The worker's owner read (#7) | In `SchemaRegistrationApplier.ExecuteAsync`, change `storedOwners.Any(owner => owner != descriptor.OwnerTenantId)` to `storedOwners.Any(owner => false)` | `AnotherTenantOwnsTheStoredRow_IsPermissionDenied_AndNothingIsApplied`, `PrivilegedRequestExecutorTests.ARefusal_IsRecordedWithItsStatusAndMessage` |
| M2 | Adoption refuses another tenant's rows (#1) | In `RequireAdoptableAsync`, change `throw NotAdoptable(descriptor, "holds rows of another tenant");` to `return;` | `NoStoredRowAndAnExistingTableHoldingAnotherTenantsRows_IsAlreadyExists` |
| M3 | The conditional registry write | In `SchemaRegistryRepository.UpsertIfOwnerAsync`, delete the line `WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner` | `UpsertIfOwner_ARowOfAnotherOwner_IsLeftUnchanged`, `UpsertIfOwner_ALegacyRowWithNoOwnerKey_CountsAsOwnerless` |
| M4 | The claim is conditional on 'pending' | In `PrivilegedRequestRepository.TryClaimAsync`, change `WHERE "Id" = @Id AND "Status" = '{PrivilegedRequestStatus.Pending}'` to `WHERE "Id" = @Id` | `TryClaim_TheSameRequestTwice_SucceedsOnce`, `ARequestTheApiExpired_CannotBeClaimed` |
| M5 | Only claims older than the cutoff are failed | In `PrivilegedRequestRepository.FailAbandonedAsync`, change `AND "ClaimedAt" < now() - @AgeSeconds * interval '1 second'` to `AND "ClaimedAt" IS NOT NULL` | `FailAbandoned_FailsOnlyRequestsClaimedLongerAgoThanTheCutoff` |
| M6 | Cleanup ends abandoned claims | In `PrivilegedRequestWorker.PollOnceAsync`, delete the line `await requests.FailAbandonedAsync(AbandonedAfter, Abandoned.ToJson());` | `PollOnce_FailsRequestsClaimedOverAnHourAgo_AsUnavailable_AtMostOncePerInterval` |

Record the failing test names under each mutation, revert it, and re-run the same command: all green.

- [ ] **Step 6: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add Iverson.Server/Iverson.Sql/PrivilegedRequestRow.cs Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs Iverson.Server/Iverson.Sql/ExistingTableInspector.cs Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs Iverson.Server/Iverson.Sql/ServiceCollectionExtensions.cs Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs Iverson.Server/Iverson.Api/Grpc/ISchemaRegistrationExecutor.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestSchema.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestClient.cs Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Helpers/InProcessSchemaRegistrationExecutor.cs Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestTestData.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/SchemaRegistrationApplierTests.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/QueuedSchemaRegistrationExecutorTests.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestWorkerTests.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs
git commit -m "run registerschema's privileged effects in the worker

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Reconcile and DLQ replay through the worker

**Spec:** §5.2 (`/admin/reconcile/{type}`, `/admin/dlq/{id}/replay`), §5.1 (locks: a per-type lock for reconcile, none for replay), §9 (reconcile 200/202/404, replay 200/202/404). Part of #13: the api no longer runs the maintenance-role reconcile or reads `MessageValue`.

**Files:**
- Modify: `Iverson.Server/Iverson.Sql/DlqRow.cs:7`. `DlqReplayCheckRow(SourceTopic, TenantId)` after `DlqReplayRow`.
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:226`. `IDlqRepository.GetUnreplayedSummaryAsync`.
- Modify: `Iverson.Server/Iverson.Sql/DlqRepository.cs:43-50`. The new read, after `GetUnreplayedByIdAsync`, naming only `"SourceTopic"` and `"TenantId"`.
- Modify: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs` (Task 7's file): the two kinds and their payloads.
- Modify: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs` (Task 7's file): `Reconcile` (60 s) and `Replay` (30 s).
- Modify: `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs` (Task 7's file): constructor, dispatch, and the two handlers.
- Modify: `Iverson.Server/Iverson.Api/Program.cs`: `/admin/reconcile/{typeName}` (`:599-621`) and `/admin/dlq/{id}/replay` (`:659-696`) queue and wait. No DI change: `ReconciliationService`, `IDlqRepository` and `IEventProducer` are registered for both roles (`Program.cs:398`, `:383-385`, `Iverson.Events/ServiceCollectionExtensions.cs:33`).
- Modify (tests): `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` (`NoOpDlqRepository`), `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs` (constructor + five tests), `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs` (`NewExecutor`), `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs:61,193,208`, `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs:144-158`, `Iverson.Server/Iverson.Sql.Tests/DlqRepositoryTests.cs` (one test before `MarkReplayedAsync_SetsReplayedTrueById`).
- Test (create): `Iverson.Server/Iverson.Api.Tests/PrivilegedAdminEndpointTests.cs`. The two routes through the real `Program.cs` pipeline with the request queue faked.

**Interfaces:**
- Consumes: Task 7's `PrivilegedRequestClient`, `PrivilegedRequestResult`, `PrivilegedRequestKinds`, `PrivilegedRequestWaits`, `PrivilegedRequestExecutor`, `IPrivilegedRequestLocks.AcquireTypeAsync`, and the test files Task 7 created. Quotes in those files match them as Task 7 leaves them.
- Produces, for Task 9: the replay route reads the DLQ through `GetUnreplayedSummaryAsync` (`"SourceTopic", "TenantId"`) only; `GetUnreplayedByIdAsync` (which reads `"MessageValue"`) and `MarkReplayedAsync` are now called only by the worker's executor. `/admin/reconcile` no longer touches `ReconciliationService` in the api. Task 9 edits `DlqRepository.cs` and the DLQ routes after this task and quotes them as this task leaves them; the `/admin/dlq` list route is untouched here.
- The routes keep their auth, Operator, active-tenant and tenant-match checks and their 404 text for a missing DLQ row, all unchanged and in their current order.
- New class `PrivilegedAdminEndpointTests`: `git grep -n "PrivilegedAdminEndpoint" -- '*.cs'` finds nothing at `6b942e1c`.

- [ ] **Step 1: Write the failing tests**

**1a. The executor's two new kinds.** In `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs`, replace:
```csharp
    private readonly ILogger<PrivilegedRequestExecutor> _logger = Substitute.For<ILogger<PrivilegedRequestExecutor>>();
    private readonly SchemaRegistry _registry;
```
with:
```csharp
    private readonly ILogger<PrivilegedRequestExecutor> _logger = Substitute.For<ILogger<PrivilegedRequestExecutor>>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IDlqRepository _dlq = Substitute.For<IDlqRepository>();
    private readonly IEventProducer _events = Substitute.For<IEventProducer>();
    private readonly SchemaRegistry _registry;
```

In `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs`, replace:
```csharp
        _sut = new PrivilegedRequestExecutor(
            _registry,
            new SchemaRegistrationApplier(_registryRepository, _tables, _schemaManager, Substitute.For<IDocumentRerenderQueueRepository>()),
            _locks,
            _logger);
    }
```
with:
```csharp
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<EntityAccess>())
            .Returns(Enumerable.Empty<KeyedTenantRow>());
        _sut = new PrivilegedRequestExecutor(
            _registry,
            new SchemaRegistrationApplier(_registryRepository, _tables, _schemaManager, Substitute.For<IDocumentRerenderQueueRepository>()),
            new ReconciliationService(
                _registry, _entities, Substitute.For<IReconciliationQueueRepository>(), _events,
                NullLogger<ReconciliationService>.Instance),
            _dlq,
            _events,
            _locks,
            _logger);
    }

    private static PrivilegedRequestRow ReconcileRow(string typeName) => new(
        Guid.NewGuid(), PrivilegedRequestKinds.ReconcileType,
        JsonSerializer.Serialize(new ReconcileTypePayload(typeName), SchemaRegistry.JsonOptions),
        PrivilegedRequestStatus.Running, null);

    private static PrivilegedRequestRow ReplayRow(Guid dlqId) => new(
        Guid.NewGuid(), PrivilegedRequestKinds.ReplayDlq,
        JsonSerializer.Serialize(new ReplayDlqPayload(dlqId), SchemaRegistry.JsonOptions),
        PrivilegedRequestStatus.Running, null);

    private void AuthorIsRegistered() =>
        _registryRepository.LoadAllAsync().Returns(
            [("Author", JsonSerializer.Serialize(Helpers.SchemaFixtures.AuthorSchema(), SchemaRegistry.JsonOptions))]);

    [Fact]
    public async Task ReconcileType_ReloadsTheRegistry_TakesOnlyTheTypeLock_AndAnswersTheCount()
    {
        AuthorIsRegistered();

        var result = await _sut.RunAsync(ReconcileRow("Author"), CancellationToken.None);

        result.Should().Be(PrivilegedRequestResult.Ok(0));
        await _locks.Received(1).AcquireTypeAsync(Arg.Is("Author"), Arg.Any<CancellationToken>());
        await _locks.DidNotReceive().AcquireRegistrationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileType_AnUnknownType_IsNotFound()
    {
        var result = await _sut.RunAsync(ReconcileRow("NoSuchType"), CancellationToken.None);

        result.Code.Should().Be(StatusCode.NotFound);
        result.Message.Should().Be("No schema registered for 'NoSuchType'");
    }

    [Fact]
    public async Task ReconcileType_StoppedByWorkerShutdown_IsUnavailable_NotAPartialCount()
    {
        // ReconcileTypeAsync ends its page loop when the token fires and returns the count so far.
        AuthorIsRegistered();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _sut.RunAsync(ReconcileRow("Author"), cts.Token);

        result.Code.Should().Be(StatusCode.Unavailable);
    }

    [Fact]
    public async Task ReplayDlq_AnUnreplayedRow_IsProducedThenMarked_WithoutALock()
    {
        var dlqId = Guid.NewGuid();
        _dlq.GetUnreplayedByIdAsync(dlqId).Returns(new DlqReplayRow("iverson.entity-events", "key-1", "value-1", "tenant-a"));

        var result = await _sut.RunAsync(ReplayRow(dlqId), CancellationToken.None);

        result.Should().Be(PrivilegedRequestResult.Ok());
        Received.InOrder(() =>
        {
            _events.ProduceAsync("iverson.entity-events", "key-1", "value-1");
            _dlq.MarkReplayedAsync(dlqId);
        });
        await _locks.DidNotReceive().AcquireTypeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _locks.DidNotReceive().AcquireRegistrationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplayDlq_ARowAlreadyReplayed_IsNotFound_AndNothingIsProduced()
    {
        var dlqId = Guid.NewGuid();
        _dlq.GetUnreplayedByIdAsync(dlqId).Returns((DlqReplayRow?)null);

        var result = await _sut.RunAsync(ReplayRow(dlqId), CancellationToken.None);

        result.Code.Should().Be(StatusCode.NotFound);
        await _events.DidNotReceive().ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _dlq.DidNotReceive().MarkReplayedAsync(Arg.Any<Guid>());
    }
```

In `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs`, replace:
```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
```
with:
```csharp
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
```

**1b. The integration file's executor.** In `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs`, replace:
```csharp
    private PrivilegedRequestExecutor NewExecutor() => new(
        NewRegistry(),
        Applier(),
        new PostgresPrivilegedRequestLocks(fixture.ConnectionString),
        NullLogger<PrivilegedRequestExecutor>.Instance);
```
with:
```csharp
    private PrivilegedRequestExecutor NewExecutor()
    {
        var registry = NewRegistry();
        return new PrivilegedRequestExecutor(
            registry,
            Applier(),
            new Iverson.Api.Reconciliation.ReconciliationService(
                registry, Substitute.For<IEntityRepository>(), Substitute.For<IReconciliationQueueRepository>(),
                Substitute.For<Iverson.Events.IEventProducer>(), NullLogger<Iverson.Api.Reconciliation.ReconciliationService>.Instance),
            Substitute.For<IDlqRepository>(),
            Substitute.For<Iverson.Events.IEventProducer>(),
            new PostgresPrivilegedRequestLocks(fixture.ConnectionString),
            NullLogger<PrivilegedRequestExecutor>.Instance);
    }
```

**1c. The routes, through the pipeline.** Create `Iverson.Server/Iverson.Api.Tests/PrivilegedAdminEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;
using RpcStatusCode = Grpc.Core.StatusCode;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §5.2: <c>/admin/reconcile/{type}</c> and <c>/admin/dlq/{id}/replay</c> queue their
/// work for the worker and answer from the request's outcome: 200 when it finished, 404 when the
/// type or row is gone, 202 with the request id while it is still running. Through the real
/// <c>Program.cs</c> pipeline, with the request queue faked so each test decides what the worker
/// recorded, and with the waits shortened.
/// </summary>
public class PrivilegedAdminEndpointTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string ActiveTenant = "tenant_active";

    private readonly AuthTestWebApplicationFactory _baseFactory;
    private readonly IPrivilegedRequestRepository _requests = Substitute.For<IPrivilegedRequestRepository>();
    private readonly IDlqRepository _dlq = Substitute.For<IDlqRepository>();
    private (Guid Id, string Kind, string Payload, TimeSpan? ExpiresAfter)? _inserted;

    public PrivilegedAdminEndpointTests(AuthTestWebApplicationFactory factory)
    {
        _baseFactory = factory;
        _requests.InsertAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _inserted = ((Guid)ci[0], (string)ci[1], (string)ci[2], (TimeSpan?)ci[4]));
    }

    private HttpClient CreateClient()
    {
        var tenantStatusCache = Substitute.For<ITenantStatusCache>();
        tenantStatusCache.GetStatusAsync(ActiveTenant).Returns(Task.FromResult<string?>("active"));

        return _baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPrivilegedRequestRepository>();
            services.AddSingleton(_requests);
            services.RemoveAll<PrivilegedRequestWaits>();
            services.AddSingleton(new PrivilegedRequestWaits
            {
                Reconcile    = TimeSpan.FromMilliseconds(300),
                Replay       = TimeSpan.FromMilliseconds(300),
                PollInterval = TimeSpan.FromMilliseconds(10)
            });
            services.RemoveAll<IDlqRepository>();
            services.AddSingleton(_dlq);
            services.RemoveAll<ITenantStatusCache>();
            services.AddSingleton(tenantStatusCache);
        })).CreateClient();
    }

    private static HttpRequestMessage OperatorPost(string path)
    {
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user",
            extraClaims: [new Claim("tenant_id", ActiveTenant), new Claim("groups", "operators")]);

        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");
        return request;
    }

    private void WorkerRecords(string status, PrivilegedRequestResult? result = null) =>
        _requests.GetAsync(Arg.Any<Guid>()).Returns(
            new PrivilegedRequestRow(Guid.Empty, "any", "{}", status, result?.ToJson()));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // ── /admin/reconcile/{typeName} ─────────────────────────────────────────

    [Fact]
    public async Task Reconcile_TheWorkerFinished_Returns200WithTheCount()
    {
        WorkerRecords(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok(7));

        var response = await CreateClient().SendAsync(OperatorPost("/admin/reconcile/Widget"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("reconciledCount").GetInt32().Should().Be(7);
        body.GetProperty("typeName").GetString().Should().Be("Widget");
        _inserted!.Value.Kind.Should().Be(PrivilegedRequestKinds.ReconcileType);
        JsonSerializer.Deserialize<ReconcileTypePayload>(_inserted.Value.Payload, SchemaRegistry.JsonOptions)!
            .TypeName.Should().Be("Widget");
        _inserted.Value.ExpiresAfter.Should().BeNull("a reconcile is idempotent, so it never expires");
    }

    [Fact]
    public async Task Reconcile_AnUnknownType_Returns404()
    {
        WorkerRecords(PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(RpcStatusCode.NotFound, "No schema registered for 'Widget'"));

        var response = await CreateClient().SendAsync(OperatorPost("/admin/reconcile/Widget"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyAsync(response)).GetProperty("error").GetString().Should().Be("No schema registered for 'Widget'");
    }

    [Fact]
    public async Task Reconcile_StillRunningAfterTheWait_Returns202WithTheRequestId()
    {
        WorkerRecords(PrivilegedRequestStatus.Running);

        var response = await CreateClient().SendAsync(OperatorPost("/admin/reconcile/Widget"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await BodyAsync(response)).GetProperty("requestId").GetGuid().Should().Be(_inserted!.Value.Id);
    }

    // ── /admin/dlq/{id}/replay ──────────────────────────────────────────────

    [Fact]
    public async Task Replay_TheWorkerReplayed_Returns200_AndTheApiNeverReadsTheMessage()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", ActiveTenant));
        WorkerRecords(PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok());

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyAsync(response);
        body.GetProperty("replayed").GetBoolean().Should().BeTrue();
        body.GetProperty("topic").GetString().Should().Be("iverson.entity-events");
        _inserted!.Value.Kind.Should().Be(PrivilegedRequestKinds.ReplayDlq);
        JsonSerializer.Deserialize<ReplayDlqPayload>(_inserted.Value.Payload, SchemaRegistry.JsonOptions)!
            .Id.Should().Be(id);
        await _dlq.DidNotReceive().GetUnreplayedByIdAsync(Arg.Any<Guid>());
        await _dlq.DidNotReceive().MarkReplayedAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Replay_TheWorkerFoundTheRowAlreadyReplayed_Returns404()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", ActiveTenant));
        WorkerRecords(PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(RpcStatusCode.NotFound, $"No unreplayed DLQ row with id '{id}'"));

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replay_StillPendingAfterTheWait_Returns202WithTheRequestId()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", ActiveTenant));
        WorkerRecords(PrivilegedRequestStatus.Pending);

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await BodyAsync(response)).GetProperty("requestId").GetGuid().Should().Be(_inserted!.Value.Id);
    }

    [Fact]
    public async Task Replay_ARowOfAnotherTenant_Returns403_AndNothingIsQueued()
    {
        var id = Guid.NewGuid();
        _dlq.GetUnreplayedSummaryAsync(id).Returns(new DlqReplayCheckRow("iverson.entity-events", "tenant_other"));

        var response = await CreateClient().SendAsync(OperatorPost($"/admin/dlq/{id}/replay"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _requests.DidNotReceive().InsertAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>());
    }
}
```

**1d. The DLQ repository's new read.** In `Iverson.Server/Iverson.Sql.Tests/DlqRepositoryTests.cs`, replace:
```csharp
    [Fact]
    public async Task MarkReplayedAsync_SetsReplayedTrueById()
```
with:
```csharp
    [Fact]
    public async Task GetUnreplayedSummaryAsync_ReadsTopicAndTenantOnly_NeverTheMessage()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<DlqReplayCheckRow>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new DlqReplayCheckRow("t", "tenant-a"));
        var repo = new DlqRepository(TableName, sql);

        var result = await repo.GetUnreplayedSummaryAsync(Guid.NewGuid());

        result.Should().Be(new DlqReplayCheckRow("t", "tenant-a"));
        await sql.Received(1).QuerySingleOrDefaultAsync<DlqReplayCheckRow>(
            Arg.Is<string>(s => s.Contains("\"Id\" = @Id AND \"Replayed\" = false") && !s.Contains("MessageValue")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task MarkReplayedAsync_SetsReplayedTrueById()
```

**1e. Existing tests that name the old read, or expect the old in-process reconcile.** In `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs`, replace:
```csharp
        dlq.GetUnreplayedByIdAsync(Arg.Any<Guid>()).Returns((DlqReplayRow?)null);
```
with:
```csharp
        dlq.GetUnreplayedSummaryAsync(Arg.Any<Guid>()).Returns((DlqReplayCheckRow?)null);
```

In `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs`, replace:
```csharp
        await dlq.DidNotReceive().GetUnreplayedByIdAsync(Arg.Any<Guid>());
```
with:
```csharp
        await dlq.DidNotReceive().GetUnreplayedSummaryAsync(Arg.Any<Guid>());
```

In `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs`, replace:
```csharp
        await dlq.Received(1).GetUnreplayedByIdAsync(Arg.Any<Guid>());
```
with:
```csharp
        await dlq.Received(1).GetUnreplayedSummaryAsync(Arg.Any<Guid>());
```

In `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`, replace:
```csharp
        // without exercising the full reconciliation pipeline.
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user",
            extraClaims: [new Claim("tenant_id", "tenant-a"), new Claim("groups", "operators")]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/reconcile/SomeType");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```
with:
```csharp
        // without exercising the full reconciliation pipeline. The reconcile runs in the worker
        // (CSR round 12 §5.2), which this host does not run, so the request queue is faked to
        // answer as the worker does for a type it does not know.
        var requests = Substitute.For<IPrivilegedRequestRepository>();
        requests.GetAsync(Arg.Any<Guid>()).Returns(new PrivilegedRequestRow(
            Guid.Empty, "ReconcileType", "{}", PrivilegedRequestStatus.Failed,
            new Iverson.Api.PrivilegedRequests.PrivilegedRequestResult(
                global::Grpc.Core.StatusCode.NotFound, "No schema registered for 'SomeType'").ToJson()));
        var client = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPrivilegedRequestRepository>();
            services.AddSingleton(requests);
        })).CreateClient();

        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user",
            extraClaims: [new Claim("tenant_id", "tenant-a"), new Claim("groups", "operators")]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/reconcile/SomeType");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```

**1f. The no-op DLQ fake.** In `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs`, replace:
```csharp
    public Task<DlqReplayRow?> GetUnreplayedByIdAsync(Guid id) => Task.FromResult<DlqReplayRow?>(null);
```
with:
```csharp
    public Task<DlqReplayRow?> GetUnreplayedByIdAsync(Guid id) => Task.FromResult<DlqReplayRow?>(null);
    public Task<DlqReplayCheckRow?> GetUnreplayedSummaryAsync(Guid id) => Task.FromResult<DlqReplayCheckRow?>(null);
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | command grep -E "error CS" | sort -u | head -30
```
Expected: the test project does not compile: `CS0246` for `DlqReplayCheckRow`, `ReconcileTypePayload` and `ReplayDlqPayload`; `CS0117` for `PrivilegedRequestKinds.ReconcileType`/`ReplayDlq` and the `Reconcile`/`Replay` waits; `CS1061` for `GetUnreplayedSummaryAsync`; `CS1729` for the 7-argument `PrivilegedRequestExecutor` construction. Record the distinct codes. The behavioural red is the counterfactual of Step 5's mutations; on the unmodified routes the new pipeline tests cannot compile.

- [ ] **Step 3: Implement**

**3a. The DLQ read without the message.** In `Iverson.Server/Iverson.Sql/DlqRow.cs`, replace:
```csharp
public sealed record DlqReplayRow(string SourceTopic, string MessageKey, string MessageValue, string? TenantId);
```
with:
```csharp
public sealed record DlqReplayRow(string SourceTopic, string MessageKey, string MessageValue, string? TenantId);

/// <summary>What the api's replay checks read: the row's topic and tenant, never its message.</summary>
public sealed record DlqReplayCheckRow(string SourceTopic, string? TenantId);
```

In `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, replace:
```csharp
    Task<DlqReplayRow?> GetUnreplayedByIdAsync(Guid id);
```
with:
```csharp
    Task<DlqReplayRow?> GetUnreplayedByIdAsync(Guid id);
    Task<DlqReplayCheckRow?> GetUnreplayedSummaryAsync(Guid id);
```

In `Iverson.Server/Iverson.Sql/DlqRepository.cs`, replace:
```csharp
            SELECT "SourceTopic", "MessageKey", "MessageValue", "TenantId"
            FROM "{tableName}"
            WHERE "Id" = @Id AND "Replayed" = false
            """,
            new { Id = id });
```
with:
```csharp
            SELECT "SourceTopic", "MessageKey", "MessageValue", "TenantId"
            FROM "{tableName}"
            WHERE "Id" = @Id AND "Replayed" = false
            """,
            new { Id = id });

    // The api's replay checks (CSR round 12 §5.2): the row's tenant and topic only. The worker
    // reads the whole row when it replays.
    public Task<DlqReplayCheckRow?> GetUnreplayedSummaryAsync(Guid id) =>
        sql.QuerySingleOrDefaultAsync<DlqReplayCheckRow>(
            $"""
            SELECT "SourceTopic", "TenantId"
            FROM "{tableName}"
            WHERE "Id" = @Id AND "Replayed" = false
            """,
            new { Id = id });
```

**3b. Kinds and payloads.** In `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs`, replace:
```csharp
internal static class PrivilegedRequestKinds
{
    public const string RegisterSchema = "RegisterSchema";
}
```
with:
```csharp
internal static class PrivilegedRequestKinds
{
    public const string RegisterSchema = "RegisterSchema";
    public const string ReconcileType  = "ReconcileType";
    public const string ReplayDlq      = "ReplayDlq";
}

/// <summary><c>/admin/reconcile/{typeName}</c>'s request.</summary>
internal sealed record ReconcileTypePayload(string TypeName);

/// <summary><c>/admin/dlq/{id}/replay</c>'s request: the DLQ row's id.</summary>
internal sealed record ReplayDlqPayload(Guid Id);
```

**3c. The waits.** In `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs`, replace:
```csharp
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
```
with:
```csharp
    /// <summary>
    /// How long <c>/admin/reconcile</c> waits before answering 202. The route is reached only
    /// through port-forward, so no edge timeout applies.
    /// </summary>
    public TimeSpan Reconcile { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long <c>/admin/dlq/{id}/replay</c> waits before answering 202.</summary>
    public TimeSpan Replay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
```

**3d. The executor.** In `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs`, replace:
```csharp
using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Sql;
```
with:
```csharp
using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
```

In `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs`, replace:
```csharp
    SchemaRegistrationApplier registration,
    IPrivilegedRequestLocks locks,
```
with:
```csharp
    SchemaRegistrationApplier registration,
    ReconciliationService reconciliation,
    IDlqRepository dlq,
    IEventProducer events,
    IPrivilegedRequestLocks locks,
```

In `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs`, replace:
```csharp
                PrivilegedRequestKinds.RegisterSchema => await RegisterSchemaAsync(request, ct),
```
with:
```csharp
                PrivilegedRequestKinds.RegisterSchema => await RegisterSchemaAsync(request, ct),
                PrivilegedRequestKinds.ReconcileType  => await ReconcileTypeAsync(request, ct),
                PrivilegedRequestKinds.ReplayDlq      => await ReplayDlqAsync(request),
```

In `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs`, replace:
```csharp
    private static T Deserialize<T>(PrivilegedRequestRow request) where T : class =>
```
with:
```csharp
    // A per-type lock, not the registration lock: a reconcile is a full-table job, and holding the
    // registration lock for it would stall every registration behind it. Two reconciles of one type
    // run one at a time; nothing else waits. The token is the worker's, so a client disconnecting
    // no longer stops the reconcile; only the worker stopping does.
    private async Task<PrivilegedRequestResult> ReconcileTypeAsync(PrivilegedRequestRow request, CancellationToken ct)
    {
        var payload = Deserialize<ReconcileTypePayload>(request);

        await using var typeLock = await locks.AcquireTypeAsync(payload.TypeName, ct);
        var count = await reconciliation.ReconcileTypeAsync(payload.TypeName, ct);

        // ReconcileTypeAsync ends its page loop when the token fires and returns the count so far,
        // which is not a finished reconcile.
        ct.ThrowIfCancellationRequested();

        return count is null
            ? new PrivilegedRequestResult(StatusCode.NotFound, $"No schema registered for '{payload.TypeName}'")
            : PrivilegedRequestResult.Ok(count);
    }

    // No lock: a replay touches one DLQ row. Replayed=false is checked again here, because the api's
    // check ran before the request was queued. Produce, then mark: at-least-once, as before.
    private async Task<PrivilegedRequestResult> ReplayDlqAsync(PrivilegedRequestRow request)
    {
        var payload = Deserialize<ReplayDlqPayload>(request);

        var row = await dlq.GetUnreplayedByIdAsync(payload.Id);
        if (row is null)
            return new PrivilegedRequestResult(StatusCode.NotFound, $"No unreplayed DLQ row with id '{payload.Id}'");

        await events.ProduceAsync(row.SourceTopic, row.MessageKey, row.MessageValue);
        await dlq.MarkReplayedAsync(payload.Id);
        return PrivilegedRequestResult.Ok();
    }

    private static T Deserialize<T>(PrivilegedRequestRow request) where T : class =>
```

**3e. The reconcile route.** In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
    Iverson.Api.Reconciliation.ReconciliationService reconciliation,
```
with:
```csharp
    Iverson.Api.PrivilegedRequests.PrivilegedRequestClient privileged,
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
    var count = await reconciliation.ReconcileTypeAsync(typeName, httpContext.RequestAborted);
    if (count is null)
        return Results.NotFound(new { error = $"No schema registered for '{typeName}'" });

    audit.AdminOperation(httpContext.User, "Reconcile", typeName);
    return Results.Ok(new { reconciledCount = count, typeName });
```
with:
```csharp
    // CSR round 12 §5.2: the reconcile reads every tenant's rows under the maintenance role, so it
    // runs in the worker. It is idempotent, so the request never expires: past the wait the worker
    // still completes it, and the caller gets 202 with the request id. The request's abort ends
    // only this wait, never the reconcile.
    var requestId = await privileged.SubmitAsync(
        Iverson.Api.PrivilegedRequests.PrivilegedRequestKinds.ReconcileType,
        JsonSerializer.Serialize(new Iverson.Api.PrivilegedRequests.ReconcileTypePayload(typeName), SchemaRegistry.JsonOptions),
        actingUserResult.Principal.FindFirst("tenant_id")?.Value,
        expiresAfter: null);
    var reconcileRequest = await privileged.WaitWhileAsync(
        requestId,
        status => status is PrivilegedRequestStatus.Pending or PrivilegedRequestStatus.Running,
        privileged.Waits.Reconcile,
        httpContext.RequestAborted);
    var reconcileResult = Iverson.Api.PrivilegedRequests.PrivilegedRequestResult.From(reconcileRequest);

    if (reconcileResult is null)
    {
        audit.AdminOperation(httpContext.User, "Reconcile", typeName);
        return Results.Json(new { requestId }, statusCode: StatusCodes.Status202Accepted);
    }
    if (reconcileResult.Code == global::Grpc.Core.StatusCode.NotFound)
        return Results.NotFound(new { error = reconcileResult.Message });
    if (reconcileResult.Code != global::Grpc.Core.StatusCode.OK)
        return Results.Json(new { error = reconcileResult.Message }, statusCode: StatusCodes.Status500InternalServerError);

    audit.AdminOperation(httpContext.User, "Reconcile", typeName);
    return Results.Ok(new { reconciledCount = reconcileResult.Count, typeName });
```

**3f. The replay route.** In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, IEventProducer events, Iverson.Api.Tenancy.ITenantStatusCache tenantStatusCache, AuditLog audit, HttpContext httpContext) =>
```
with:
```csharp
app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, Iverson.Api.PrivilegedRequests.PrivilegedRequestClient privileged, Iverson.Api.Tenancy.ITenantStatusCache tenantStatusCache, AuditLog audit, HttpContext httpContext) =>
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
    var row = await dlq.GetUnreplayedByIdAsync(id);
    if (row is null) return Results.NotFound(new { error = $"No unreplayed DLQ row with id '{id}'" });

    if (row.TenantId != actingTenantId && !(row.TenantId is null && isOperator))
        return Results.Forbid();

    await events.ProduceAsync(row.SourceTopic, row.MessageKey, row.MessageValue);
    await dlq.MarkReplayedAsync(id);
    audit.AdminOperation(httpContext.User, "ReplayDlq", id.ToString());

    return Results.Ok(new { replayed = true, id, topic = row.SourceTopic });
```
with:
```csharp
    // The api's checks read the row's tenant and topic only, never MessageValue.
    var row = await dlq.GetUnreplayedSummaryAsync(id);
    if (row is null) return Results.NotFound(new { error = $"No unreplayed DLQ row with id '{id}'" });

    if (row.TenantId != actingTenantId && !(row.TenantId is null && isOperator))
        return Results.Forbid();

    // CSR round 12 §5.2: the worker produces the message and marks the row replayed, checking
    // Replayed=false again. The request never expires: a late replay of an unreplayed row is
    // still the replay that was asked for.
    var requestId = await privileged.SubmitAsync(
        Iverson.Api.PrivilegedRequests.PrivilegedRequestKinds.ReplayDlq,
        JsonSerializer.Serialize(new Iverson.Api.PrivilegedRequests.ReplayDlqPayload(id), SchemaRegistry.JsonOptions),
        actingTenantId,
        expiresAfter: null);
    var replayRequest = await privileged.WaitWhileAsync(
        requestId,
        status => status is PrivilegedRequestStatus.Pending or PrivilegedRequestStatus.Running,
        privileged.Waits.Replay,
        httpContext.RequestAborted);
    var replayResult = Iverson.Api.PrivilegedRequests.PrivilegedRequestResult.From(replayRequest);

    if (replayResult is null)
    {
        audit.AdminOperation(httpContext.User, "ReplayDlq", id.ToString());
        return Results.Json(new { requestId }, statusCode: StatusCodes.Status202Accepted);
    }
    if (replayResult.Code == global::Grpc.Core.StatusCode.NotFound)
        return Results.NotFound(new { error = replayResult.Message });
    if (replayResult.Code != global::Grpc.Core.StatusCode.OK)
        return Results.Json(new { error = replayResult.Message }, statusCode: StatusCodes.Status500InternalServerError);

    audit.AdminOperation(httpContext.User, "ReplayDlq", id.ToString());

    return Results.Ok(new { replayed = true, id, topic = row.SourceTopic });
```

- [ ] **Step 4: Run the tests and see them pass**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.PrivilegedRequestExecutorTests.|FullyQualifiedName~.PrivilegedAdminEndpointTests.|FullyQualifiedName~.InactiveTenantHttpEndpointTests.|FullyQualifiedName~.AuthenticationPipelineTests."
```
Expected: 0 failed. `PrivilegedRequestExecutorTests` 10 (5 from Task 7 + 5), `PrivilegedAdminEndpointTests` 7; the other two classes keep their counts.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~.DlqRepositoryTests."
```
Expected: 0 failed, one test more than before this task.

Then the whole non-container suite of both projects:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failed.

Container suite (**heavy, alone**): `NewExecutor` changed, so re-run Task 7's integration class.
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.PrivilegedRequestPostgresIntegrationTests."
```
Expected: 21 passed, 0 failed.

- [ ] **Step 5: Mutation proof** (unit and pipeline only, with the first command of Step 4; make each mutation by hand and undo it by hand)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | Replay re-checks Replayed=false in the worker | In `ReplayDlqAsync`, change `if (row is null)` to `if (row is null && false)` | `ReplayDlq_ARowAlreadyReplayed_IsNotFound_AndNothingIsProduced` (a `NullReferenceException` becomes an Internal result) |
| M2 | Reconcile takes the per-type lock | In `ReconcileTypeAsync`, change `await locks.AcquireTypeAsync(payload.TypeName, ct)` to `await locks.AcquireRegistrationAsync(ct)` | `ReconcileType_ReloadsTheRegistry_TakesOnlyTheTypeLock_AndAnswersTheCount` |
| M3 | A shutdown is not a finished reconcile | In `ReconcileTypeAsync`, delete `ct.ThrowIfCancellationRequested();` | `ReconcileType_StoppedByWorkerShutdown_IsUnavailable_NotAPartialCount` |
| M4 | Unfinished answers 202, not a guessed outcome | In the reconcile route, change `if (reconcileResult is null)` to `if (reconcileResult is null && false)` | `Reconcile_StillRunningAfterTheWait_Returns202WithTheRequestId` (a `NullReferenceException` gives 500) |

Record the failing test names under each mutation, revert, and re-run: all green.

- [ ] **Step 6: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add Iverson.Server/Iverson.Sql/DlqRow.cs Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Sql/DlqRepository.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs Iverson.Server/Iverson.Api.Tests/PrivilegedAdminEndpointTests.cs Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs Iverson.Server/Iverson.Sql.Tests/DlqRepositoryTests.cs
git commit -m "run reconcile and dlq replay through the worker

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: Postgres credential split, code

**Spec:** §6.2, §6.3, §6.4, and §9's §6 tests (closes the Postgres half of #13 in code; the chart and compose half is Task 12)

**Decisions taken in this task:**
- **The api's login name is the fixed constant `iverson_api`**, `PostgresSchemaManager.ApiLoginName`, not a configuration value.
  - Every role name the code uses today is a compile-time constant: `iverson_runtime` and `iverson_maintenance` (`PostgresSchemaManager.cs:230,237`, `IRecordStoreRoles.cs:104,113`), and `iverson_provisioner` and `iverson_app` in the StarRocks GRANTs (`EngagementRepository.cs:523,531`). No role name is read from configuration anywhere.
  - Nothing needs it to differ: the chart's CNPG managed role and compose's worker both create exactly `iverson_api` (spec §6.1, §8), and every Testcontainers class gets its own cluster.
  - The readiness probe never names the login. It uses the one-argument `has_table_privilege`, which checks `current_user`. For the api that is `iverson_api`, which is exactly the spec's check. For the worker, which owns the table, it is always true.
- **Who creates the login.** On Helm it is a CNPG managed role (Task 12), and the worker only waits for it, for up to one minute: CNPG creates managed roles asynchronously, which is why `job-revoke-cross-db.yaml:60-67` polls for `authentik`. On compose the worker connects as the superuser and creates the login itself, with the password in the new key `Postgres:ApiLoginPassword` (spec §8, "the superuser worker creates the login, membership and grants at startup"). The worker reads that key only when the login is missing.
- **`IversonStarRocksTenants` is granted by Task 10, not here.** Task 10 creates that table, and the provisioned-tenant directory is its only reader. Task 10 adds one `ApplySchemaAsync` line to `WorkerStoreBootstrap.RunAsync` and one `GRANT SELECT` line to `ApiLoginGrants.Statements`, both anchored below. If Task 9 created the table instead, it would have to define a schema whose columns and only consumer belong to Task 10, and Task 10 would depend on Task 9's copy of its own table.
- **The DLQ.** The api's list query already names its columns without `MessageValue` (`DlqRepository.cs:31-41`). The replay check is rewritten by Task 8, which moves the `MessageValue` read and the `Replayed` update to the worker. So Task 9 changes no DLQ query. It grants `SELECT` on the ten other columns, and its integration test proves that `MessageValue` and `SELECT *` are denied to the api's login. A leftover api call that reads `MessageValue` would fail with 42501.
- **The api's background registry load is `SchemaRefreshWorker`.** It already runs `LoadAsync` every 30 s in both roles (`Program.cs:485`). The api tries once at startup, logs a failure instead of crashing, and `/health` stays not-ready until a load has succeeded (`SchemaRegistry.HasLoaded`). No second retry loop is added.
- **Readiness also runs the `iverson_runtime` probe** (spec §6.3, amended in `a8d18301`). `/health` calls `IRecordStoreSchemaManager.EnsureCanEnterRuntimeRoleAsync`, the same probe the api's startup runs. It does a `SET LOCAL ROLE iverson_runtime` in a transaction that it rolls back, so it writes nothing, and it sits behind the existing 2 s `health-composite` cache. A login that cannot enter the role, whether because its CNPG membership has not landed or has been lost, keeps the api not-ready instead of letting every data RPC fail with 42501. The startup probe stays tolerant: it is only logged.
- **The worker's startup DDL moves into `WorkerStoreBootstrap.RunAsync`.** Program.cs and the integration fixture both call it, so the test's tables and grants are exactly the ones production creates.
- **The dead branches fail with `Internal`.** `ObjectRetrievalGrpcService.GetMany` and `ObjectMappingGrpcService.Delete` refuse a non-denied decision with no tenant column with `RpcException(Internal)`, before any store call. `OutboxWriter.UpsertAndEnqueueOutboxAsync` refuses a null tenant with `ArgumentNullException`, before the transaction. Its signature keeps `string? tenantId = null`: the four callers pass `decision.TenantValue`, which is `string?`.

**Files:**
- Create: `Iverson.Server/Iverson.Api/ApiLoginGrants.cs`. The §6.2 grant statements and the readiness probe SQL.
- Create: `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs`. The worker's startup DDL, then the api login and its grants.
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`:
  - `:13-14`: the `RecordStoreRole.Connection` doc;
  - `:70-77`: two new `IRecordStoreSchemaManager` members.
- Modify: `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs`:
  - `:12`: the `ApiLoginName` constant;
  - after `:270`: `EnsureCanEnterRuntimeRoleAsync`, `EnsureApiLoginAsync` and two private helpers;
  - `:308-312`: `EnsureRoleAsync` uses the shared existence helper.
- Modify: `Iverson.Server/Iverson.Sql/OutboxWriter.cs`:
  - `:18-21`: the null-tenant refusal;
  - `:52-60`: the role scope becomes unconditional.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`:
  - `:19`: `HasLoaded`;
  - `:67-71`: `LoadAsync` loses `EnsureTableAsync`;
  - `:225`: the flag is set.
- Modify: `Iverson.Server/Iverson.Api/Program.cs`:
  - `:552-586`: `/health` adds the grants probe, the `iverson_runtime` probe and the registry flag;
  - `:723-724`: the role split, with the worker's bootstrap, seed and self-heal, and the api's tolerant load;
  - `:732-766`: the old worker-and-api DDL block is removed.
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:112-120`.
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:338-348`.
- Modify: `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs:49-56`. The doc paragraph that counts the `TenantColumn` guards.
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:68`. The no-op schema manager implements the two new members.
- Test (modify): `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs`:
  - `:30-52`: the null-tenant test becomes the refusal test;
  - `:83`: the double-quote test passes a tenant;
  - `:262-269`: the null-tenant payload test is deleted.
- Test (modify): `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs:364-378`. The cache test stubs and counts the new grants probe.
- Test (create): `Iverson.Server/Iverson.Api.Tests/Grpc/TenantlessDecisionRefusalTests.cs`.
- Test (create): `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryStartupTests.cs`.
- Test (create): `Iverson.Server/Iverson.Api.Tests/ApiStartupReadinessPipelineTests.cs`.
- Test (create): `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs`. Heavy: it uses Testcontainers Postgres.

**Interfaces:**
- **Consumes from Task 7:**
  - The plumbing table `IversonPrivilegedRequests`, with spec §5.1's columns (`Id`, `Kind`, `Payload` jsonb, `RequestedBy`, `Status`, `ResultJson` jsonb, `RequestedAt`, `ExpiresAt`, `CompletedAt`), created by the worker's startup through `ApplySchemaAsync`.
  - Task 9 names the table by its literal, because Task 7's schema symbol is not known at drafting time.
  - The api's request queries need only `INSERT`, `SELECT` and `UPDATE ("Status")`.
- **Rebase rule for Program.cs's hydration block (Task 7).** Step 5h's old text is the block as it stands at `6b942e1c`. If Task 7 inserted its `await schemaManager.ApplySchemaAsync(<requests schema>.Table);` line into that block (after the `TenantSchema` line), the assembler does two things:
  - adds that line to Step 5h's old text at the same place;
  - adds it, re-indented and with the `Iverson.Api.…` qualifier matching that file's usings, to `WorkerStoreBootstrap.RunAsync` in Step 5b, directly after `await schemaManager.ApplySchemaAsync(TenantSchema.Table);`.

  Step 5h's new text does not change. The same applies to any other plumbing DDL line Task 7 or 8 adds there.
- **Consumes from Task 8:**
  - The api's replay route no longer calls `IDlqRepository.GetUnreplayedByIdAsync` (which reads `MessageValue`) or `MarkReplayedAsync`. Both are worker-side after Task 8.
  - Task 9 does not edit `DlqRepository.cs`.
- **Consumes from Tasks 1 and 5.** `SchemaRegistry.LoadAsync`'s first three lines and its final `logger.LogInformation("SchemaRegistry loaded …")` line are as at `6b942e1c`. Tasks 1 and 5 edit the loop between them.
- **Consumes from Task 4.**
  - The `PopularitySignalValidator.ValidateAtStartup(...)` call at `Program.cs:726-730` is not quoted, so Task 4 may change its arguments.
  - It now runs after the role split. In the api, a registry that has not loaded yet makes it skip and warn for each signal, which its own doc comment already tolerates (`PopularitySignalOptions.cs:60-69`).
- **Produces for Task 10:**
  - In `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs`, the anchor line `        await schemaManager.ApplySchemaAsync(TenantSchema.Table);`. Task 10 inserts its `ApplySchemaAsync` line after it.
  - In `Iverson.Server/Iverson.Api/ApiLoginGrants.cs`, the anchor:
    ```
            $"""GRANT UPDATE ("Status") ON "IversonPrivilegedRequests" TO {Login}""",
        ];
    ```
    Task 10 inserts `$"""GRANT SELECT ON "IversonStarRocksTenants" TO {Login}""",` before the `];` line, with a one-line comment.
  - In `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs`, the anchor line `            "IversonPrivilegedRequests:INSERT", "IversonPrivilegedRequests:SELECT",`. Task 10 adds `"IversonStarRocksTenants:SELECT",` after it, or `ApiLogin_HoldsExactlyTheListedPrivileges` fails.
- **Produces for Task 12:**
  - The fixed login name `iverson_api`.
  - The worker-only configuration key `Postgres:ApiLoginPassword` (env `Postgres__ApiLoginPassword`). Compose sets it on `iverson-worker` to the same literal as the api's `ConnectionStrings__Postgres` password. Helm never sets it.
  - The worker now refuses to start when `iverson_api` is still missing a minute after startup and no password is configured. So Task 12's CNPG managed role (Helm) or compose's worker env var is required on this branch. **Between this commit and Task 12's, the worker on compose and Helm fails startup by design.** No live check runs before Task 13.
  - `Tenancy__SeedLegacyTenants` is read by the worker only (compose already sets it on the worker, `docker-compose.yml:640`).
  - The api's `/health` is not-ready until the worker has run, which is what makes "api `depends_on` worker healthy" (spec §8) correct.
- **Produces for Task 13.** `/health`'s `checks` object gains `apiGrants`, `runtimeRole` and `registry` booleans.
- **Shared-file notes:**
  - **`Program.cs`.** Three hunks: `/health` (`:552-586`), the registry load (`:723-724`) and the old DDL block (`:732-766`). Task 8 edits the `/admin` routes between them (`:599-696`), and Task 10 edits DI (`:254-322`). Neither overlaps.
  - **`PostgresSchemaManager.cs`.** Anchored on `CreateConnection`, the end of `EnsureRolesAsync`, and `EnsureRoleAsync`'s head.
  - **`IRecordStoreRoles.cs`.** Anchored on the `Connection` doc and `IRecordStoreSchemaManager`. Task 7 may append a new repository interface elsewhere in the file.
- **Tests that build a `SchemaRegistry` on a fresh database.** `LoadAsync` no longer creates `_iverson_schema`. At `6b942e1c` every real-Postgres test creates it first through `RegisterAsync`, which keeps its own `EnsureTableAsync` call:
  - `RegisterSchemaAuthorizationIntegrationTests.cs:257,277`;
  - `UpdateCarryForwardPostgresIntegrationTests.cs:54`;
  - `ReconciliationQueuePostgresIntegrationTests.cs:83`.

  New tests from Tasks 6 and 7 that call `LoadAsync` on a fresh database must call `SchemaRegistryRepository.EnsureTableAsync()` first.

- [ ] **Step 1: Write the failing tests for the fail-closed branches**

These tests compile against today's code, so they show the counterfactual before any implementation.

In `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs`, replace:
```csharp
    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_RunsInsideOneTransaction()
    {
        var id = await _sut.UpsertAndEnqueueOutboxAsync(ArticleSchema, "Article", Guid.NewGuid().ToString(), "{}");

        id.Should().NotBe(Guid.Empty);
        await _txRunner.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_NullTenantId_DoesNotIssueSetLocalRoleOrResetRole()
    {
        var tx = Substitute.For<IDbTransactionContext>();
        var calls = new List<string>();
        tx.WhenForAnyArgs(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
          .Do(call => calls.Add(call.ArgAt<string>(0)));
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(tx));

        await _sut.UpsertAndEnqueueOutboxAsync(ArticleSchema, "Article", Guid.NewGuid().ToString(), "{}");

        calls.Should().HaveCount(2); // upsert, then outbox insert — no role switch at all
        calls.Should().NotContain(s => s.Contains("ROLE"));
    }
```
with:
```csharp
    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_RunsInsideOneTransaction()
    {
        var id = await _sut.UpsertAndEnqueueOutboxAsync(
            ArticleSchema, "Article", Guid.NewGuid().ToString(), "{}", tenantId: "tenant-a");

        id.Should().NotBe(Guid.Empty);
        await _txRunner.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_NullTenantId_IsRefusedBeforeTheTransaction()
    {
        // CSR round 12 §6.4. Every caller passes the tenant of a non-denied authorization decision,
        // which is never null. A null used to run the upsert on the connection's own role, which
        // was the owner; nothing may fall back to that, so a null fails closed.
        var act = () => _sut.UpsertAndEnqueueOutboxAsync(ArticleSchema, "Article", Guid.NewGuid().ToString(), "{}");

        await act.Should().ThrowAsync<ArgumentNullException>();
        await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
    }
```

In `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs`, replace:
```csharp
        var act = () => _sut.UpsertAndEnqueueOutboxAsync(schema, "Article", Guid.NewGuid().ToString(), "{}");
```
with:
```csharp
        var act = () => _sut.UpsertAndEnqueueOutboxAsync(
            schema, "Article", Guid.NewGuid().ToString(), "{}", tenantId: "tenant-a");
```

In `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs`, replace:
```csharp
    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_NullTenantId_LeavesThePayloadUntouched()
    {
        var json = await CaptureUpsertJsonAsync(
            TenantArticleSchema, """{"Id":"a","Title":"Hello"}""", tenantId: null);

        json.Should().Be("""{"Id":"a","Title":"Hello"}""");
    }

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_SchemaWithoutATenantColumn_LeavesThePayloadUntouched()
```
with:
```csharp
    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_SchemaWithoutATenantColumn_LeavesThePayloadUntouched()
```

Create `Iverson.Server/Iverson.Api.Tests/Grpc/TenantlessDecisionRefusalTests.cs`:
```csharp
using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR round 12 §6.4: the api never enters BYPASSRLS. GetMany and Mapping.Delete used to pick
/// <c>iverson_maintenance</c> for a decision with no tenant column. The real evaluator never
/// produces such a decision when it allows the call: every loaded descriptor has a tenant column,
/// and its only non-denied return carries it. So a stub evaluator stands in here, and the call must
/// be refused with Internal before it reaches the store.
/// </summary>
public class TenantlessDecisionRefusalTests
{
    private static readonly string AuthorId   = "11111111-0000-0000-0000-000000000001";
    private static readonly string AuthorJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"test-tenant"}""";

    // Allowed, with no owner scoping, no field restriction and no tenant boundary.
    private static readonly AuthorizationDecision TenantlessDecision = new(false, false, null, null, null, null, null);

    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IRecordStoreTransactionRunner _txRunner = Substitute.For<IRecordStoreTransactionRunner>();
    private readonly IRowFieldAuthorizationEvaluator _evaluator = Substitute.For<IRowFieldAuthorizationEvaluator>();
    private readonly IActingUserAccessor _actingUser = new ActingUserAccessor
        { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
    private readonly AuditLog _auditLog = new(NullLogger<AuditLog>.Instance);
    private readonly SchemaRegistry _registry = new(
        new SchemaRegistryRepository(Substitute.For<IRecordStoreQueryExecutor>()),
        NullLogger<SchemaRegistry>.Instance);

    public TenantlessDecisionRefusalTests()
    {
        _evaluator
            .Evaluate(Arg.Any<SchemaDescriptor>(), Arg.Any<ClaimsPrincipal?>(), Arg.Any<AuthorizationAction>())
            .Returns(TenantlessDecision);
    }

    [Fact]
    public async Task GetMany_WithAnAllowedDecisionThatHasNoTenantColumn_IsRefusedBeforeTheRead()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var sut = new ObjectRetrievalGrpcService(
            _entities, _registry, NullLogger<ObjectRetrievalGrpcService>.Instance, _actingUser, _evaluator, _auditLog);

        var act = () => sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            Substitute.For<IServerStreamWriter<RetrievalResponse>>(),
            TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Internal);
        await _entities.DidNotReceiveWithAnyArgs().FetchManyByKeysAsync(default!, default!, default);
    }

    [Fact]
    public async Task Delete_WithAnAllowedDecisionThatHasNoTenantColumn_IsRefusedBeforeTheTransaction()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(AuthorJson);
        var sut = new ObjectMappingGrpcService(
            _entities,
            _txRunner,
            Substitute.For<IOutboxPublisher>(),
            _registry,
            Substitute.For<IRelationValidator>(),
            Substitute.For<IPayloadSizeValidator>(),
            Substitute.For<IEntityKeyAccessor>(),
            Substitute.For<IOutboxWriter>(),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUser,
            _evaluator,
            Substitute.For<IEntityRelationResolver>(),
            Substitute.For<ISchemaRegistrationOrchestrator>(),
            _auditLog,
            EngagementQueryLimitOptions.Default);

        var act = () => sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId }, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Internal);
        await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
        await _entities.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default!, default);
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~.OutboxWriterTests."
```
Expected:
```
Failed!  - Failed:     1, Passed:    12, Skipped:     0, Total:    13
```
The failing test is `UpsertAndEnqueueOutboxAsync_NullTenantId_IsRefusedBeforeTheTransaction`: no exception is thrown, because today a null tenant runs the upsert on the connection's role.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.TenantlessDecisionRefusalTests."
```
Expected:
```
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
```
Neither call throws `RpcException(Internal)`: GetMany reads under `CrossTenantMaintenance`, and Delete opens its transaction.

- [ ] **Step 3: Write the remaining failing tests**

In `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`, replace:
```csharp
        using var factory = new HealthCacheTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
        factory.StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.Healthy);
```
with:
```csharp
        using var factory = new HealthCacheTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
        // The api login's grants probe (CSR round 12 §6.3), also behind the cache.
        factory.Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>()).Returns(true);
        factory.StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.Healthy);
```

In `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`, replace:
```csharp
        await factory.Db.Received(1).QuerySingleOrDefaultAsync<int>(Arg.Any<string>());
```
with:
```csharp
        await factory.Db.Received(1).QuerySingleOrDefaultAsync<int>(Arg.Any<string>());
        await factory.Db.Received(1).QuerySingleOrDefaultAsync<bool>(Arg.Any<string>());
```

Create `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryStartupTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// CSR round 12 §6.3. <see cref="SchemaRegistry.LoadAsync"/> runs in the api at startup and every
/// 30 s, and the api's login may not run DDL, so the load no longer creates its own table: the
/// worker does that once, at startup. And because the api can start before the worker, its first
/// load may fail; <see cref="SchemaRegistry.HasLoaded"/> is what /health waits for.
/// </summary>
public class SchemaRegistryStartupTests
{
    private readonly ISchemaRegistryRepository _repository = Substitute.For<ISchemaRegistryRepository>();
    private readonly SchemaRegistry _sut;

    public SchemaRegistryStartupTests() =>
        _sut = new SchemaRegistry(_repository, NullLogger<SchemaRegistry>.Instance);

    [Fact]
    public async Task LoadAsync_RunsNoDdl()
    {
        _repository.LoadAllAsync().Returns(Enumerable.Empty<(string TypeName, string SchemaJson)>());

        await _sut.LoadAsync();

        await _repository.DidNotReceive().EnsureTableAsync();
        await _repository.Received(1).LoadAllAsync();
    }

    [Fact]
    public void HasLoaded_IsFalseBeforeAnyLoad() =>
        _sut.HasLoaded.Should().BeFalse();

    [Fact]
    public async Task HasLoaded_StaysFalseWhileLoadsFail_AndTurnsTrueOnTheFirstSuccess()
    {
        _repository.LoadAllAsync()
            .ThrowsAsync(new InvalidOperationException("relation \"_iverson_schema\" does not exist"));

        var failingLoad = () => _sut.LoadAsync();
        await failingLoad.Should().ThrowAsync<InvalidOperationException>();
        _sut.HasLoaded.Should().BeFalse();

        _repository.LoadAllAsync().Returns(Enumerable.Empty<(string TypeName, string SchemaJson)>());
        await _sut.LoadAsync();

        _sut.HasLoaded.Should().BeTrue();
    }
}
```

Create `Iverson.Server/Iverson.Api.Tests/ApiStartupReadinessPipelineTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §6.3, through the real Program.cs in the api role (the test host's default). The
/// api runs no DDL and no seed, and probes only <c>iverson_runtime</c>. It starts even when its
/// registry cannot load yet. /health reports not-ready until its login holds the worker's grants,
/// can enter <c>iverson_runtime</c>, and the registry has loaded.
/// </summary>
public class ApiStartupReadinessPipelineTests
{
    private sealed class ApiStartupTestFactory : AuthTestWebApplicationFactory
    {
        public readonly IRecordStoreQueryExecutor Db = Substitute.For<IRecordStoreQueryExecutor>();
        public readonly IEngagementStoreHealthCheck StarRocks = Substitute.For<IEngagementStoreHealthCheck>();
        public readonly IVectorSchemaManager Vector = Substitute.For<IVectorSchemaManager>();
        public readonly IEventBrokerHealthCheck Kafka = Substitute.For<IEventBrokerHealthCheck>();
        public readonly IRecordStoreSchemaManager SchemaManager = Substitute.For<IRecordStoreSchemaManager>();
        public readonly ISchemaRegistryRepository RegistryRepository = Substitute.For<ISchemaRegistryRepository>();
        public readonly IEnrichmentStateRepository EnrichmentState = Substitute.For<IEnrichmentStateRepository>();
        public readonly IDocumentRerenderQueueRepository RerenderQueue = Substitute.For<IDocumentRerenderQueueRepository>();
        public readonly ITokenRevocationRepository TokenRevocations = Substitute.For<ITokenRevocationRepository>();
        public readonly ITenantRepository Tenants = Substitute.For<ITenantRepository>();

        public ApiStartupTestFactory(bool registryLoadFails = false)
        {
            if (registryLoadFails)
                RegistryRepository.LoadAllAsync()
                    .ThrowsAsync(new InvalidOperationException("relation \"_iverson_schema\" does not exist"));
            else
                RegistryRepository.LoadAllAsync().Returns(Enumerable.Empty<(string TypeName, string SchemaJson)>());

            Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
            Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>()).Returns(true);
            StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.Healthy);
            Vector.PingAsync().Returns(true);
            Kafka.PingAsync().Returns(true);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // On, so the startup test can show the api ignores it: seeding is the worker's.
            builder.UseSetting("Tenancy:SeedLegacyTenants", "true");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRecordStoreQueryExecutor>();
                services.AddSingleton(Db);
                services.RemoveAll<IEngagementStoreHealthCheck>();
                services.AddSingleton(StarRocks);
                services.RemoveAll<IVectorSchemaManager>();
                services.AddSingleton(Vector);
                services.RemoveAll<IEventBrokerHealthCheck>();
                services.AddSingleton(Kafka);
                services.RemoveAll<IRecordStoreSchemaManager>();
                services.AddSingleton(SchemaManager);
                services.RemoveAll<ISchemaRegistryRepository>();
                services.AddSingleton(RegistryRepository);
                services.RemoveAll<IEnrichmentStateRepository>();
                services.AddSingleton(EnrichmentState);
                services.RemoveAll<IDocumentRerenderQueueRepository>();
                services.AddSingleton(RerenderQueue);
                services.RemoveAll<ITokenRevocationRepository>();
                services.AddSingleton(TokenRevocations);
                services.RemoveAll<ITenantRepository>();
                services.AddSingleton(Tenants);
            });
        }
    }

    private static async Task<JsonElement> ChecksAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("checks").Clone();
    }

    [Fact]
    public async Task GetHealth_WithTheGrantsHeldAndTheRegistryLoaded_IsReady()
    {
        using var factory = new ApiStartupTestFactory();

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var checks = await ChecksAsync(response);
        checks.GetProperty("apiGrants").GetBoolean().Should().BeTrue();
        checks.GetProperty("runtimeRole").GetBoolean().Should().BeTrue();
        checks.GetProperty("registry").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_WhenTheLoginLacksTheOutboxInsertGrant_IsNotReady()
    {
        using var factory = new ApiStartupTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>()).Returns(false);

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var checks = await ChecksAsync(response);
        checks.GetProperty("apiGrants").GetBoolean().Should().BeFalse();
        checks.GetProperty("postgres").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_WhenTheGrantProbeFails_IsNotReady()
    {
        // The table the probe names does not exist yet on a fresh install: has_table_privilege
        // raises 42P01 rather than answering false.
        using var factory = new ApiStartupTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("relation \"IversonReconciliationQueue\" does not exist"));

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ChecksAsync(response)).GetProperty("apiGrants").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetHealth_WhenTheRuntimeRoleProbeFails_IsNotReady_EvenWithTheGrantsAndTheRegistry()
    {
        using var factory = new ApiStartupTestFactory();
        var client = factory.CreateClient(); // startup's own probe succeeds, so the registry loads

        // Only now does the probe fail: the login has lost iverson_runtime (or the CNPG membership
        // has not landed yet). The grants and the registry are fine, so only the probe can make
        // /health not-ready.
        factory.SchemaManager.EnsureCanEnterRuntimeRoleAsync()
            .ThrowsAsync(new InvalidOperationException("Role iverson_runtime exists but this connection is not a member of it"));

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var checks = await ChecksAsync(response);
        checks.GetProperty("runtimeRole").GetBoolean().Should().BeFalse();
        checks.GetProperty("apiGrants").GetBoolean().Should().BeTrue();
        checks.GetProperty("registry").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ApiHost_WhenTheRegistryCannotLoadAtStartup_StillStartsAndReportsNotReady()
    {
        using var factory = new ApiStartupTestFactory(registryLoadFails: true);
        var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ChecksAsync(response)).GetProperty("registry").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ApiStartup_RunsNoDdlAndNoSeed_AndProbesOnlyTheRuntimeRole()
    {
        using var factory = new ApiStartupTestFactory();

        _ = factory.CreateClient(); // boots the host, which runs Program.cs's startup block

        await factory.SchemaManager.Received(1).EnsureCanEnterRuntimeRoleAsync();
        await factory.SchemaManager.DidNotReceive().EnsureRolesAsync();
        await factory.SchemaManager.DidNotReceiveWithAnyArgs().ApplySchemaAsync(default!, default);
        await factory.SchemaManager.DidNotReceiveWithAnyArgs().EnsureApiLoginAsync(default, default!, default);
        await factory.RegistryRepository.DidNotReceive().EnsureTableAsync();
        await factory.RegistryRepository.Received(1).LoadAllAsync();
        await factory.EnrichmentState.DidNotReceive().EnsureTableAsync();
        await factory.RerenderQueue.DidNotReceive().EnsureTableAsync();
        await factory.TokenRevocations.DidNotReceive().EnsureTableAsync();
        await factory.Tenants.DidNotReceiveWithAnyArgs().SeedIfMissingAsync(default!, default!, default!);
    }
}
```

Create `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Api.Reconciliation;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// The worker's startup against a real Postgres. <see cref="WorkerStoreBootstrap"/> creates the
/// plumbing tables and gives the api's login its grants, exactly as Program.cs runs it. The
/// container's own login is a superuser, standing in for the owner. The password path, which
/// creates the login, is compose's.
/// </summary>
public sealed class ApiLoginGrantsPostgresContainerFixture : IAsyncLifetime
{
    public const string ApiPassword = "api-login-test-only";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    public string OwnerConnectionString => _container.GetConnectionString();
    public string ApiConnectionString { get; private set; } = null!;
    public PostgresRepository Owner { get; private set; } = null!;
    public PostgresSchemaManager OwnerSchemaManager { get; private set; } = null!;
    public PostgresRepository Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Owner = new PostgresRepository(OwnerConnectionString, NullLogger<PostgresRepository>.Instance);
        OwnerSchemaManager = new PostgresSchemaManager(OwnerConnectionString, NullLogger<PostgresSchemaManager>.Instance);

        await WorkerStoreBootstrap.RunAsync(
            OwnerSchemaManager,
            new SchemaRegistryRepository(Owner),
            new EnrichmentStateRepository(Owner),
            new DocumentRerenderQueueRepository(Owner),
            new TokenRevocationRepository(Owner),
            ApiPassword,
            TimeSpan.Zero);

        ApiConnectionString = new NpgsqlConnectionStringBuilder(OwnerConnectionString)
        {
            Username = PostgresSchemaManager.ApiLoginName,
            Password = ApiPassword
        }.ConnectionString;
        Api = new PostgresRepository(ApiConnectionString, NullLogger<PostgresRepository>.Instance);
    }

    /// <summary>The worker's grant step again, with the password: recreates the login if a test dropped it.</summary>
    public Task RestoreApiLoginAsync() =>
        OwnerSchemaManager.EnsureApiLoginAsync(ApiPassword, ApiLoginGrants.Statements, TimeSpan.Zero);

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// CSR round 12 §6 and §9: the api's repositories run as a login that is not the owner, holds no
/// BYPASSRLS, and has exactly the §6.2 grants. Every store call the api makes succeeds, and reading
/// outbox payloads or <c>MessageValue</c>, entering <c>iverson_maintenance</c>, and DDL are each
/// denied.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ApiLoginGrantsPostgresIntegrationTests(ApiLoginGrantsPostgresContainerFixture fixture)
    : IClassFixture<ApiLoginGrantsPostgresContainerFixture>
{
    private const string TenantColumn = "__TenantId";
    private const string PrivilegedRequestsTable = "IversonPrivilegedRequests";

    private readonly PostgresRepository _owner = fixture.Owner;
    private readonly PostgresRepository _api = fixture.Api;

    private static TableSchema WidgetSchema(string table) => new(
        table,
        new ColumnSchema("Id", "uuid", IsNullable: false),
        [
            new ColumnSchema("Name", "text", IsNullable: false),
            new ColumnSchema("ParentId", "uuid", IsNullable: true),
            new ColumnSchema(TenantColumn, "text", IsNullable: false),
        ],
        TenantColumn: TenantColumn);

    private static async Task DeniedAsync(Func<Task> act) =>
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

    private string ConnectionStringFor(string login, string password) =>
        new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Username = login, Password = password }
            .ConnectionString;

    [Fact]
    public async Task ApiLogin_OwnsNothing_HoldsNoBypassRls_AndIsAMemberOfTheRuntimeRoleOnly()
    {
        var attributes = await _owner.QuerySingleOrDefaultAsync<(bool Super, bool BypassRls, bool CreateRole, bool CreateDb)>(
            "SELECT rolsuper, rolbypassrls, rolcreaterole, rolcreatedb FROM pg_roles WHERE rolname = 'iverson_api'");

        attributes.Super.Should().BeFalse();
        attributes.BypassRls.Should().BeFalse();
        attributes.CreateRole.Should().BeFalse();
        attributes.CreateDb.Should().BeFalse();
        (await _owner.QuerySingleOrDefaultAsync<bool>(
            "SELECT pg_has_role('iverson_api', 'iverson_runtime', 'MEMBER')")).Should().BeTrue();
        (await _owner.QuerySingleOrDefaultAsync<bool>(
            "SELECT pg_has_role('iverson_api', 'iverson_maintenance', 'MEMBER')")).Should().BeFalse();
        (await _owner.QuerySingleOrDefaultAsync<long>(
            "SELECT count(*) FROM pg_class WHERE relowner = 'iverson_api'::regrole")).Should().Be(0);
    }

    [Fact]
    public async Task ApiLogin_HoldsExactlyTheListedPrivileges()
    {
        var tablePrivileges = await _owner.QueryAsync<string>(
            """
            SELECT c.relname || ':' || a.privilege_type
            FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
            WHERE a.grantee = 'iverson_api'::regrole
            """);
        var columnPrivileges = await _owner.QueryAsync<string>(
            """
            SELECT c.relname || '.' || att.attname || ':' || a.privilege_type
            FROM pg_attribute att
            JOIN pg_class c ON c.oid = att.attrelid
            CROSS JOIN LATERAL aclexplode(att.attacl) a
            WHERE a.grantee = 'iverson_api'::regrole
            """);
        var connect = await _owner.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_database d CROSS JOIN LATERAL aclexplode(d.datacl) a
                WHERE d.datname = current_database()
                  AND a.grantee = 'iverson_api'::regrole
                  AND a.privilege_type = 'CONNECT')
            """);

        tablePrivileges.Should().BeEquivalentTo(new[]
        {
            "IversonReconciliationQueue:INSERT", "IversonReconciliationQueue:DELETE",
            "IversonTenants:SELECT", "IversonTenants:INSERT", "IversonTenants:UPDATE", "IversonTenants:DELETE",
            "iverson_token_revocations:SELECT", "iverson_token_revocations:INSERT", "iverson_token_revocations:UPDATE",
            "_iverson_schema:SELECT",
            "IversonPrivilegedRequests:INSERT", "IversonPrivilegedRequests:SELECT",
        });
        columnPrivileges.Should().BeEquivalentTo(new[]
        {
            "IversonReconciliationQueue.Id:SELECT",
            "IversonDlqMessages.Id:SELECT", "IversonDlqMessages.SourceTopic:SELECT",
            "IversonDlqMessages.ConsumerGroup:SELECT", "IversonDlqMessages.MessageKey:SELECT",
            "IversonDlqMessages.ExceptionType:SELECT", "IversonDlqMessages.ExceptionMessage:SELECT",
            "IversonDlqMessages.Attempts:SELECT", "IversonDlqMessages.FailedAt:SELECT",
            "IversonDlqMessages.Replayed:SELECT", "IversonDlqMessages.TenantId:SELECT",
            "IversonPrivilegedRequests.Status:UPDATE",
        });
        connect.Should().BeTrue("the Authentik hook revokes PUBLIC's CONNECT on Helm, so the worker grants it");
    }

    [Fact]
    public async Task DataPath_WritesReadsAndDeletes_WithTheOutboxInTheSameTransaction()
    {
        var table = "widgets_" + Guid.NewGuid().ToString("N")[..8];
        var schema = WidgetSchema(table);
        await fixture.OwnerSchemaManager.ApplySchemaAsync(schema);

        var writer = new OutboxWriter(ReconciliationSchema.TableName, _api, _api);
        var entities = new EntityRepository(_api);
        var key = Guid.NewGuid().ToString();
        var parentId = Guid.NewGuid().ToString();
        var json = $$"""{"Id":"{{key}}","Name":"w","ParentId":"{{parentId}}"}""";

        // Post and Update: the upsert under iverson_runtime, then the outbox insert on the login.
        var outboxRowId = await writer.UpsertAndEnqueueOutboxAsync(schema, "Widget", key, json, "tenant-a");

        // Get, GetMany and OneToMany hydration, each filtered to the caller's tenant by RLS.
        (await entities.FetchByKeyAsync(schema, key, EntityAccess.ForTenant("tenant-a")))
            .Should().Contain("\"Name\":\"w\"");
        (await entities.FetchByKeyAsync(schema, key, EntityAccess.ForTenant("tenant-b"))).Should().BeNull();
        (await entities.FetchManyByKeysAsync(schema, [key], EntityAccess.ForTenant("tenant-a"))).Should().ContainSingle();
        (await entities.FetchByColumnAsync(schema, "ParentId", parentId, EntityAccess.ForTenant("tenant-a")))
            .Should().ContainSingle();

        // The fast path deletes its own outbox row by Id.
        await writer.DeleteOutboxRowIfPresentAsync(outboxRowId);
        (await _owner.QuerySingleOrDefaultAsync<long>(
            $"""SELECT count(*) FROM "{ReconciliationSchema.TableName}" WHERE "Id" = @Id""", new { Id = outboxRowId }))
            .Should().Be(0);

        // Delete: the entity delete under iverson_runtime and the Deleted outbox row, in one transaction.
        var deleteRowId = Guid.CreateVersion7();
        await _api.ExecuteInTransactionAsync(async tx =>
        {
            await entities.DeleteAsync(tx, schema, key, EntityAccess.ForTenant("tenant-a"));
            await writer.EnqueueDeleteOutboxRowAsync(tx, deleteRowId, "Widget", key, json);
        });

        (await _owner.QuerySingleOrDefaultAsync<long>($"""SELECT count(*) FROM "{table}" """)).Should().Be(0);
        (await _owner.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "EventType" FROM "{ReconciliationSchema.TableName}" WHERE "Id" = @Id""", new { Id = deleteRowId }))
            .Should().Be("Deleted");
    }

    [Fact]
    public async Task TenantLifecycle_EveryCallTheApiMakes_Succeeds()
    {
        var tenants = new TenantRepository(TenantSchema.TableName, _api);
        var id = "tenant_" + Guid.NewGuid().ToString("N")[..8];

        await tenants.InsertAsync(id, "Grants probe", "active");
        (await tenants.GetAsync(id))!.Status.Should().Be("active");
        (await tenants.ListAsync()).Should().Contain(t => t.Id == id);
        await tenants.UpdateStatusAsync(id, "suspended");
        (await tenants.UpdateStatusUnlessDeletedAsync(id, "active")).Should().BeTrue();

        // CreateTenant's rollback when the IdP call fails.
        await tenants.DeleteAsync(id);
        (await tenants.GetAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task TokenRevocation_RevokeTwiceAndList_Succeeds()
    {
        var revocations = new TokenRevocationRepository(_api);
        var sub = "sub-" + Guid.NewGuid().ToString("N");

        await revocations.RevokeAsync(sub);
        await revocations.RevokeAsync(sub); // the ON CONFLICT update arm

        (await revocations.ListAsync()).Should().Contain(r => r.Sub == sub);
    }

    [Fact]
    public async Task SchemaRegistry_LoadsButCannotBeWritten()
    {
        var registry = new SchemaRegistryRepository(_api);

        var load = () => registry.LoadAllAsync();
        await load.Should().NotThrowAsync();

        // Registration writes go through the worker (spec §5).
        await DeniedAsync(() => registry.UpsertAsync("Widget", "{}"));
    }

    [Fact]
    public async Task Dlq_TheListAndTheReplayCheckColumnsAreReadable_ButMessageValueIsNot()
    {
        var messageKey = "key-" + Guid.NewGuid().ToString("N");
        await new DlqRepository(DlqSchema.TableName, _owner).InsertAsync(new DlqMessage(
            "iverson.entity-events", "projection", messageKey, "{\"payload\":true}",
            "System.Exception", "boom", 3, DateTime.UtcNow, "tenant-a"));

        var dlq = new DlqRepository(DlqSchema.TableName, _api);
        var row = (await dlq.ListUnreplayedAsync(200)).Should().Contain(r => r.MessageKey == messageKey).Which;

        // The replay route's checks (spec §5.2) read the row's tenant and replay flag, by name.
        var check = await _api.QuerySingleOrDefaultAsync<(string? TenantId, bool Replayed)>(
            $"""SELECT "TenantId", "Replayed" FROM "{DlqSchema.TableName}" WHERE "Id" = @Id""", new { row.Id });
        check.TenantId.Should().Be("tenant-a");
        check.Replayed.Should().BeFalse();

        await DeniedAsync(() => _api.QueryAsync<string>($"""SELECT "MessageValue" FROM "{DlqSchema.TableName}" """));
        await DeniedAsync(() => _api.QueryAsync<dynamic>($"""SELECT * FROM "{DlqSchema.TableName}" """));
    }

    [Fact]
    public async Task PrivilegedRequests_InsertPollAndExpireSucceed_ButOnlyStatusIsWritable()
    {
        var id = Guid.NewGuid();
        await _api.ExecuteAsync(
            $"""
            INSERT INTO "{PrivilegedRequestsTable}"
                ("Id", "Kind", "Payload", "RequestedBy", "Status", "RequestedAt", "ExpiresAt")
            VALUES (@Id, 'ReconcileType', @Payload::jsonb, 'operator', 'pending', now(), NULL)
            """,
            new { Id = id, Payload = "{}" });

        (await _api.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "Status" FROM "{PrivilegedRequestsTable}" WHERE "Id" = @Id""", new { Id = id }))
            .Should().Be("pending");
        (await _api.ExecuteAsync(
            $"""UPDATE "{PrivilegedRequestsTable}" SET "Status" = 'expired' WHERE "Id" = @Id AND "Status" = 'pending'""",
            new { Id = id })).Should().Be(1);

        await DeniedAsync(() => _api.ExecuteAsync(
            $"""UPDATE "{PrivilegedRequestsTable}" SET "ResultJson" = @Result::jsonb WHERE "Id" = @Id""",
            new { Id = id, Result = "{}" }));
    }

    [Fact]
    public async Task Outbox_PayloadsAreUnreadable()
    {
        await DeniedAsync(() => _api.QueryAsync<string>(
            $"""SELECT "Payload" FROM "{ReconciliationSchema.TableName}" """));
        await DeniedAsync(() => _api.QueryAsync<dynamic>(
            $"""SELECT * FROM "{ReconciliationSchema.TableName}" """));
    }

    [Fact]
    public async Task MaintenanceRole_CannotBeEntered_AndTheApisRoleCheckProbesOnlyTheRuntimeRole()
    {
        await DeniedAsync(() => _api.QueryAsync<int>("SELECT 1", null, RecordStoreRole.Maintenance));

        var asApi = new PostgresSchemaManager(fixture.ApiConnectionString, NullLogger<PostgresSchemaManager>.Instance);

        var runtimeProbe = () => asApi.EnsureCanEnterRuntimeRoleAsync();
        await runtimeProbe.Should().NotThrowAsync();

        // The worker's full check would refuse this login: it is no member of iverson_maintenance.
        var workerCheck = () => asApi.EnsureRolesAsync();
        (await workerCheck.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("GRANT iverson_maintenance TO iverson_api;");
    }

    [Fact]
    public async Task Ddl_IsDenied()
    {
        await DeniedAsync(() => _api.ExecuteAsync($"CREATE TABLE api_ddl_probe_{Guid.NewGuid():N} (id int)"));
        await DeniedAsync(() => _api.ExecuteAsync($"""ALTER TABLE "{TenantSchema.TableName}" ADD COLUMN "Probe" text"""));
    }

    [Fact]
    public async Task ReadinessProbe_IsFalseForALoginWithoutTheGrants_AndTrueForTheApiLogin()
    {
        var login = "nogrants_" + Guid.NewGuid().ToString("N")[..8];
        await _owner.ExecuteAsync($"CREATE ROLE \"{login}\" LOGIN PASSWORD 'probe-only'");

        try
        {
            var bare = new PostgresRepository(ConnectionStringFor(login, "probe-only"), NullLogger<PostgresRepository>.Instance);

            (await bare.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql)).Should().BeFalse();
            (await _api.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql)).Should().BeTrue();

            // The api's role check names the grant a login outside iverson_runtime is missing.
            var asBare = new PostgresSchemaManager(ConnectionStringFor(login, "probe-only"), NullLogger<PostgresSchemaManager>.Instance);
            var probe = () => asBare.EnsureCanEnterRuntimeRoleAsync();
            (await probe.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain($"GRANT iverson_runtime TO {login};");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await _owner.ExecuteAsync($"DROP ROLE \"{login}\"");
        }
    }

    [Fact]
    public async Task EnsureApiLogin_RunAgain_WithAndWithoutAPassword_IsIdempotent()
    {
        var withPassword = () => fixture.OwnerSchemaManager.EnsureApiLoginAsync(
            ApiLoginGrantsPostgresContainerFixture.ApiPassword, ApiLoginGrants.Statements, TimeSpan.Zero);
        var withoutPassword = () => fixture.OwnerSchemaManager.EnsureApiLoginAsync(
            null, ApiLoginGrants.Statements, TimeSpan.Zero);

        await withPassword.Should().NotThrowAsync();
        await withoutPassword.Should().NotThrowAsync();
        (await _api.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql)).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureApiLogin_WhenTheLoginIsMissingAndNoPasswordIsConfigured_ThrowsNamingTheLogin()
    {
        NpgsqlConnection.ClearAllPools();
        await _owner.ExecuteAsync("DROP OWNED BY iverson_api");
        await _owner.ExecuteAsync("DROP ROLE iverson_api");

        try
        {
            var act = () => fixture.OwnerSchemaManager.EnsureApiLoginAsync(null, ApiLoginGrants.Statements, TimeSpan.Zero);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("iverson_api").And.Contain("Postgres__ApiLoginPassword");
        }
        finally
        {
            await fixture.RestoreApiLoginAsync();
        }
    }
}
```

- [ ] **Step 4: Build and see the new tests fail to compile**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | command grep -E "error CS" | sort -u | head -20
```
Expected: errors only for the symbols this task adds, and no others:
```
error CS1061: 'SchemaRegistry' does not contain a definition for 'HasLoaded'
error CS1061: 'IRecordStoreSchemaManager' does not contain a definition for 'EnsureCanEnterRuntimeRoleAsync'
error CS1061: 'IRecordStoreSchemaManager' does not contain a definition for 'EnsureApiLoginAsync'
error CS1061: 'PostgresSchemaManager' does not contain a definition for 'EnsureCanEnterRuntimeRoleAsync'
error CS1061: 'PostgresSchemaManager' does not contain a definition for 'EnsureApiLoginAsync'
error CS0117: 'PostgresSchemaManager' does not contain a definition for 'ApiLoginName'
error CS0103: The name 'WorkerStoreBootstrap' does not exist in the current context
error CS0103: The name 'ApiLoginGrants' does not exist in the current context
```
(each with file and line prefixes).

- [ ] **Step 5: Implement**

**5a. The grant list and the readiness probe.**

Create `Iverson.Server/Iverson.Api/ApiLoginGrants.cs`:
```csharp
using Iverson.Api.Reconciliation;
using Iverson.Api.Tenancy;
using Iverson.Sql;

namespace Iverson.Api;

/// <summary>
/// What the api's Postgres login, <see cref="PostgresSchemaManager.ApiLoginName"/>, may do beyond
/// its <c>iverson_runtime</c> membership, which covers every entity table (CSR round 12 §6.2). The
/// worker issues these at startup, as the database owner, in one transaction
/// (<see cref="WorkerStoreBootstrap"/>), and every one is idempotent. Nothing else grants the login
/// anything: it owns nothing, holds no BYPASSRLS, is no member of <c>iverson_maintenance</c>, and
/// has no CREATE on any schema. Tables not listed here, the document re-render queue and the
/// enrichment state, are the worker's alone.
/// </summary>
internal static class ApiLoginGrants
{
    private const string Login = PostgresSchemaManager.ApiLoginName;

    public static readonly IReadOnlyList<string> Statements =
    [
        // On Helm the Authentik hook revokes PUBLIC's CONNECT on this database, and post-upgrade
        // hooks only run once the release is ready, which needs this login connected: so the worker
        // grants it. The database name differs between Helm, compose and the test containers.
        $"DO $$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO {Login}', current_database()); END $$",

        // The outbox: rows inserted in the same transaction as each entity write (including Delete's
        // EventType and Payload), and the fast path's delete of its own row by Id. Payloads stay
        // unreadable.
        $"""GRANT INSERT, DELETE ON "{ReconciliationSchema.TableName}" TO {Login}""",
        $"""GRANT SELECT ("Id") ON "{ReconciliationSchema.TableName}" TO {Login}""",

        // The /admin/dlq list and the replay route's checks: every column except MessageValue, which
        // only the worker reads. A query that selects * is therefore refused, so the api's DLQ
        // queries name their columns.
        $"""GRANT SELECT ("Id", "SourceTopic", "ConsumerGroup", "MessageKey", "ExceptionType", "ExceptionMessage", "Attempts", "FailedAt", "Replayed", "TenantId") ON "{DlqSchema.TableName}" TO {Login}""",

        // Tenant lifecycle (including CreateTenant's rollback delete), the status cache and the
        // console readers.
        $"""GRANT SELECT, INSERT, UPDATE, DELETE ON "{TenantSchema.TableName}" TO {Login}""",

        // RemoveUser and SetTenantAdmin's revocation upsert, and the revocation cache.
        $"GRANT SELECT, INSERT, UPDATE ON iverson_token_revocations TO {Login}",

        // Loading the registry. Registration writes it through the worker (spec §5).
        $"GRANT SELECT ON _iverson_schema TO {Login}",

        // Spec §5's privileged requests (Task 7's table): the api queues them, polls them, and
        // expires its own.
        $"""GRANT INSERT, SELECT ON "IversonPrivilegedRequests" TO {Login}""",
        $"""GRANT UPDATE ("Status") ON "IversonPrivilegedRequests" TO {Login}""",
    ];

    /// <summary>
    /// /health's grants check. One-argument <c>has_table_privilege</c> checks <c>current_user</c>:
    /// for the api that is its own login, and for the worker, which owns the table, it is always
    /// true. The worker grants everything in <see cref="Statements"/> in one transaction, so this
    /// one privilege stands for all of them. It raises 42P01, which /health reads as false, until
    /// the worker has created the table.
    /// </summary>
    public static readonly string ReadinessProbeSql =
        $"""SELECT has_table_privilege('"{ReconciliationSchema.TableName}"', 'INSERT')""";
}
```

**5b. The worker's startup DDL.**

Create `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs`:
```csharp
using Iverson.Api.Reconciliation;
using Iverson.Api.Tenancy;
using Iverson.Sql;

namespace Iverson.Api;

/// <summary>
/// The worker's startup DDL (CSR round 12 §6.3). Only the worker runs it: it connects as the
/// database owner, while the api's login owns nothing and may create nothing. It creates every
/// plumbing table, ensures the two entity roles, and then gives the api's login exactly
/// <see cref="ApiLoginGrants.Statements"/>, so the grants always follow the tables they name.
/// Program.cs calls it, and so does the integration fixture that proves the grants.
/// </summary>
internal static class WorkerStoreBootstrap
{
    public static async Task RunAsync(
        IRecordStoreSchemaManager schemaManager,
        ISchemaRegistryRepository schemaRegistry,
        IEnrichmentStateRepository enrichmentState,
        IDocumentRerenderQueueRepository rerenderQueue,
        ITokenRevocationRepository tokenRevocations,
        string? apiLoginPassword,
        TimeSpan apiLoginWait)
    {
        // The schema registry's table. SchemaRegistry.LoadAsync no longer creates it: the api loads
        // the registry at startup and every 30 s, and may not run DDL.
        await schemaRegistry.EnsureTableAsync();

        // The enrichment loop breaker's table.
        await enrichmentState.EnsureTableAsync();

        // The document re-render queue: raw DDL, because it needs the two partial unique indexes
        // ApplySchemaAsync cannot express.
        await rerenderQueue.EnsureTableAsync();

        // Token revocations (CSR round-10 #11): raw DDL, the same shape.
        await tokenRevocations.EnsureTableAsync();

        // EnsureRolesAsync must run before ANY ApplySchemaAsync call, since that DDL GRANTs to
        // iverson_maintenance on every table (and to iverson_runtime on the tenant-scoped ones) —
        // both roles have to exist first.
        await schemaManager.EnsureRolesAsync();
        await schemaManager.ApplySchemaAsync(ReconciliationSchema.Table);
        await schemaManager.ApplySchemaAsync(DlqSchema.Table);
        await schemaManager.ApplySchemaAsync(TenantSchema.Table);
        await schemaManager.ApplySchemaAsync(Iverson.Api.PrivilegedRequests.PrivilegedRequestSchema.Table);

        // Last, once every table the grants name exists (CSR round 12 §6.2).
        await schemaManager.EnsureApiLoginAsync(apiLoginPassword, ApiLoginGrants.Statements, apiLoginWait);
    }
}
```

**5c. The schema manager contract.**

In `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, replace:
```csharp
    /// <summary>
    /// The connection's own role — the table owner. Correct only for the plumbing tables, which
```
with:
```csharp
    /// <summary>
    /// The connection's own role: the table owner in the worker, and in the api its own login,
    /// <c>iverson_api</c>, which owns nothing and holds direct grants on the plumbing tables it uses
    /// (CSR round 12 §6.2). Correct only for the plumbing tables, which
```

In `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, replace:
```csharp
    /// </summary>
    Task EnsureRolesAsync();
}
```
with:
```csharp
    /// </summary>
    Task EnsureRolesAsync();

    /// <summary>
    /// The api's role check (CSR round 12 §6.3), run at startup and by /health's readiness. It
    /// proves this connection can enter <c>iverson_runtime</c>, the only role the api's login belongs
    /// to, in a transaction it rolls back, so it writes nothing. It never creates a role, and never
    /// probes <c>iverson_maintenance</c>, which the api may not enter.
    /// </summary>
    Task EnsureCanEnterRuntimeRoleAsync();

    /// <summary>
    /// The worker's part of the api's login (CSR round 12 §6.1–6.2). It makes sure the login exists,
    /// either by waiting up to <paramref name="roleWait"/> for a managed one or, when
    /// <paramref name="password"/> is given, by creating it. It then makes the login a member of
    /// <c>iverson_runtime</c> and grants it every statement in <paramref name="grantStatements"/>,
    /// all in one transaction. Idempotent. Run it after the plumbing DDL, since the statements name
    /// those tables.
    /// </summary>
    Task EnsureApiLoginAsync(string? password, IReadOnlyList<string> grantStatements, TimeSpan roleWait);
}
```

**5d. The schema manager.**

In `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs`, replace:
```csharp
    private NpgsqlConnection CreateConnection() => new(connectionString);
```
with:
```csharp
    /// <summary>
    /// The api's own login (CSR round 12 §6.1). A constant, like every other role name here. The
    /// chart's CNPG managed role and compose's worker both create exactly this name.
    /// </summary>
    public const string ApiLoginName = "iverson_api";

    private NpgsqlConnection CreateConnection() => new(connectionString);
```

In `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs`, replace:
```csharp
        await EnsureCanEnterRoleAsync(conn, "iverson_runtime");
        await EnsureCanEnterRoleAsync(conn, "iverson_maintenance");
    }
```
with:
```csharp
        await EnsureCanEnterRoleAsync(conn, "iverson_runtime");
        await EnsureCanEnterRoleAsync(conn, "iverson_maintenance");
    }

    public async Task EnsureCanEnterRuntimeRoleAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await EnsureCanEnterRoleAsync(conn, "iverson_runtime");
    }

    public async Task EnsureApiLoginAsync(string? password, IReadOnlyList<string> grantStatements, TimeSpan roleWait)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        // On Helm the login is a CNPG managed role, which CNPG creates asynchronously (as
        // job-revoke-cross-db.yaml's wait for authentik shows), so a worker that starts first waits
        // for it instead of crash-looping. Compose has no CNPG: there the worker is the superuser
        // and creates the login itself, from the password it is given.
        if (string.IsNullOrEmpty(password))
            await WaitForRoleAsync(conn, ApiLoginName, roleWait);

        await using var tx = await conn.BeginTransactionAsync();

        // Serialises worker replicas. Concurrent GRANTs on one table fail with XX000 "tuple
        // concurrently updated" (see ApplySchemaAsync), and two replicas could both try CREATE ROLE.
        // Transaction-scoped, so the lock is never left on a pooled connection.
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('iverson_api_login')::bigint)", null, tx);

        try
        {
            if (!await RoleExistsAsync(conn, ApiLoginName, tx))
            {
                // Reached only with a password, since the wait above throws otherwise. CREATE ROLE
                // takes no bind parameters, so format's %L quotes the password as a literal.
                var createRole = await conn.QuerySingleAsync<string>(
                    $"SELECT format('CREATE ROLE {ApiLoginName} LOGIN PASSWORD %L', @Password::text)",
                    new { Password = password }, tx);
                await conn.ExecuteAsync(createRole, null, tx);
            }

            var isRuntimeMember = await conn.QuerySingleAsync<bool>(
                $"SELECT pg_has_role('{ApiLoginName}', 'iverson_runtime', 'MEMBER')", null, tx);
            if (!isRuntimeMember)
                await conn.ExecuteAsync($"GRANT iverson_runtime TO {ApiLoginName}", null, tx);

            foreach (var statement in grantStatements)
                await conn.ExecuteAsync(statement, null, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            throw new InvalidOperationException(
                $"This connection ({conn.UserName}) cannot set up the api's login {ApiLoginName}: "
                + $"{ex.MessageText}. On Helm the login and its iverson_runtime membership come from the "
                + "CNPG managed role in charts/postgres/templates/cluster.yaml, and the worker, as the "
                + "database owner, only grants table privileges: check the Cluster's status. On compose "
                + "the worker must connect as the superuser.", ex);
        }

        await tx.CommitAsync();
    }

    private static async Task WaitForRoleAsync(NpgsqlConnection conn, string roleName, TimeSpan wait)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (!await RoleExistsAsync(conn, roleName))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"Login {roleName} does not exist, and this worker has no Postgres:ApiLoginPassword "
                    + "(env Postgres__ApiLoginPassword) to create it with. On Helm it is a CNPG managed role "
                    + "(charts/postgres/templates/cluster.yaml): check the Cluster's status for a role "
                    + "reconciliation error. On compose, set Postgres__ApiLoginPassword on the worker.");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static Task<bool> RoleExistsAsync(NpgsqlConnection conn, string roleName, NpgsqlTransaction? tx = null) =>
        conn.QuerySingleAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @Role)", new { Role = roleName }, tx);
```

In `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs`, replace:
```csharp
    private static async Task EnsureRoleAsync(NpgsqlConnection conn, string roleName, string createSql)
    {
        var exists = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @Role)", new { Role = roleName });
        if (exists) return;
```
with:
```csharp
    private static async Task EnsureRoleAsync(NpgsqlConnection conn, string roleName, string createSql)
    {
        if (await RoleExistsAsync(conn, roleName)) return;
```

In `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs`, replace:
```csharp
    public Task EnsureRolesAsync() => Task.CompletedTask;
}
```
with:
```csharp
    public Task EnsureRolesAsync() => Task.CompletedTask;
    public Task EnsureCanEnterRuntimeRoleAsync() => Task.CompletedTask;
    public Task EnsureApiLoginAsync(string? password, IReadOnlyList<string> grantStatements, TimeSpan roleWait) =>
        Task.CompletedTask;
}
```

**5e. The outbox writer fails closed on a null tenant.**

In `Iverson.Server/Iverson.Sql/OutboxWriter.cs`, replace:
```csharp
        TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null)
    {
        // Schema-derived names are quoted as identifiers; outboxTableName below is a constant.
```
with:
```csharp
        TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null)
    {
        // CSR round 12 §6.4. Every caller passes the tenant of a non-denied authorization decision,
        // which is never null. A null used to run the upsert on the connection's own role, which was
        // the owner. Nothing may fall back to that, so a null fails closed.
        ArgumentNullException.ThrowIfNull(tenantId);

        // Schema-derived names are quoted as identifiers; outboxTableName below is a constant.
```

In `Iverson.Server/Iverson.Sql/OutboxWriter.cs`, replace:
```csharp
        await txRunner.ExecuteInTransactionAsync(async tx =>
        {
            if (tenantId is not null)
            {
                await tx.EnterTenantScopeAsync(tenantId);
            }
            await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
            if (tenantId is not null)
                await tx.ExitRoleScopeAsync();
```
with:
```csharp
        await txRunner.ExecuteInTransactionAsync(async tx =>
        {
            await tx.EnterTenantScopeAsync(tenantId);
            await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
            await tx.ExitRoleScopeAsync();
```

**5f. The two api request paths that chose BYPASSRLS fail closed.**

In `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs`, replace:
```csharp
        var rows = await _entities.FetchManyByKeysAsync(
            SchemaBuilder.ToTableSchema(schema),
            keys,
            // A type that declares no tenant column carries no RLS policy and no iverson_runtime
            // grant, so a tenant-scoped read of it would be 42501, not a filtered read — and an
            // unfiltered read of a type with no tenant column IS cross-tenant. Say so explicitly.
            decision.TenantColumn is not null
                ? EntityAccess.ForTenant(decision.TenantValue)
                : EntityAccess.CrossTenantMaintenance);
```
with:
```csharp
        // CSR round 12 §6.4. Every loaded descriptor has a tenant column, and every non-denied
        // decision carries it, so this never fires. If it ever did, the read is refused rather than
        // run across tenants: the api never enters BYPASSRLS.
        if (decision.TenantColumn is null)
            throw new RpcException(new Status(StatusCode.Internal, "The type declares no tenant boundary."));

        var rows = await _entities.FetchManyByKeysAsync(
            SchemaBuilder.ToTableSchema(schema),
            keys,
            EntityAccess.ForTenant(decision.TenantValue));
```

In `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, replace:
```csharp
        await _txRunner.ExecuteInTransactionAsync(async tx =>
        {
            await _entities.DeleteAsync(
                tx,
                SchemaBuilder.ToTableSchema(schema),
                request.Key,
                // A type with no tenant column carries no RLS policy and no iverson_runtime grant,
                // so a tenant-scoped delete of it would be 42501 rather than a filtered delete.
                decision.TenantColumn is not null
                    ? EntityAccess.ForTenant(decision.TenantValue)
                    : EntityAccess.CrossTenantMaintenance);
```
with:
```csharp
        // CSR round 12 §6.4. Every loaded descriptor has a tenant column, and every non-denied
        // decision carries it, so this never fires. If it ever did, the delete is refused before its
        // transaction rather than run across tenants: the api never enters BYPASSRLS.
        if (decision.TenantColumn is null)
            throw new RpcException(new Status(StatusCode.Internal, "The type declares no tenant boundary."));

        await _txRunner.ExecuteInTransactionAsync(async tx =>
        {
            await _entities.DeleteAsync(
                tx,
                SchemaBuilder.ToTableSchema(schema),
                request.Key,
                EntityAccess.ForTenant(decision.TenantValue));
```

In `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs`, replace:
```csharp
    /// <para>
    /// There are NINE such guards in total: the three above, which are reached with a null on
    /// every denied read, plus six (<c>ObjectRetrievalGrpcService</c> x2,
    /// <c>ObjectMappingGrpcService</c> x1, <c>EntityRelationResolver</c> x1,
    /// <c>AuthorizationFieldMasking</c> x2) that sit after a <c>Denied</c> return or behind
    /// <c>decision.Denied ||</c> and so are non-null by flow. The declaration is shared, so all
    /// nine stay.
    /// </para>
```
with:
```csharp
    /// <para>
    /// The other guards sit after a <c>Denied</c> return or behind <c>decision.Denied ||</c>, and so
    /// are non-null by flow. The declaration is shared, so they stay. Two such sites used to choose
    /// <c>iverson_maintenance</c> for a null column: the entity read in
    /// <c>ObjectRetrievalGrpcService.GetMany</c> and the entity delete in
    /// <c>ObjectMappingGrpcService.Delete</c>. Since CSR round 12 §6.4 they refuse a null with
    /// Internal instead, because the api never enters BYPASSRLS.
    /// </para>
```

**5g. The registry loads without DDL, and records its first success.**

In `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`, replace:
```csharp
    public bool IsRegistered(string typeName) => _schemas.ContainsKey(typeName);
```
with:
```csharp
    public bool IsRegistered(string typeName) => _schemas.ContainsKey(typeName);

    // CSR round 12 §6.3. The api can start before the worker has created or granted the registry
    // table, so its first load may fail; SchemaRefreshWorker retries it every 30 s. /health reports
    // not-ready until one load has succeeded.
    private volatile bool _hasLoaded;

    /// <summary>True once a <see cref="LoadAsync"/> has completed.</summary>
    public bool HasLoaded => _hasLoaded;
```

In `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`, replace:
```csharp
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await repository.EnsureTableAsync();

        var rows = await repository.LoadAllAsync();
```
with:
```csharp
    public async Task LoadAsync(CancellationToken ct = default)
    {
        // No EnsureTableAsync here (CSR round 12 §6.3). This runs in the api at startup and every
        // 30 s, and the api's login may not run DDL. The worker creates the table once, at startup
        // (WorkerStoreBootstrap).
        var rows = await repository.LoadAllAsync();
```

In `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`, replace:
```csharp
        logger.LogInformation("SchemaRegistry loaded {Count} schema(s)", _schemas.Count);
    }
```
with:
```csharp
        _hasLoaded = true;
        logger.LogInformation("SchemaRegistry loaded {Count} schema(s)", _schemas.Count);
    }
```

**5h. Program.cs: readiness, then the role split.**

In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
app.MapGet("/health", async (
    IRecordStoreQueryExecutor db,
    IEngagementStoreHealthCheck sr,
    IVectorSchemaManager vector,
    IEventBrokerHealthCheck kafka,
    IOptions<EngagementStoreOptions> engagementOptions,
    IMemoryCache cache) =>
{
    if (cache.TryGetValue("health-composite", out IResult? cached))
        return cached!;

    // CSR finding #7: this endpoint is AllowAnonymous, reachable by anything that can reach the
    // port — so every check here must be passive. Postgres reads (never writes), StarRocks'
    // CheckHealthAsync is SELECT 1 + a backend-status read, Qdrant's PingAsync lists collections
    // (never creates one), and Kafka's PingAsync reads broker metadata (never produces). None of
    // the four performs a write; do not reintroduce one here.
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    var srTask     = sr.CheckHealthAsync();
    var vectorTask = vector.PingAsync();
    var kafkaTask  = kafka.PingAsync();

    await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);

    var srStatus = await srTask;
    var engagementEnabled = engagementOptions.Value.Enabled;
    var checks = new
    {
        postgres  = pgTask.Result,
        starrocks = engagementEnabled ? (object)(srStatus == EngagementHealthStatus.Healthy) : "disabled",
        qdrant    = vectorTask.Result,
        kafka     = kafkaTask.Result
    };

    var readiness = ReadinessPolicy.Evaluate(
        pgTask.Result, srStatus, vectorTask.Result, kafkaTask.Result, engagementEnabled);
```
with:
```csharp
app.MapGet("/health", async (
    IRecordStoreQueryExecutor db,
    IEngagementStoreHealthCheck sr,
    IVectorSchemaManager vector,
    IEventBrokerHealthCheck kafka,
    IOptions<EngagementStoreOptions> engagementOptions,
    SchemaRegistry registry,
    IRecordStoreSchemaManager storeRoles,
    IMemoryCache cache) =>
{
    if (cache.TryGetValue("health-composite", out IResult? cached))
        return cached!;

    // CSR finding #7: this endpoint is AllowAnonymous, reachable by anything that can reach the
    // port — so every check here must be passive. Postgres reads (never writes), StarRocks'
    // CheckHealthAsync is SELECT 1 + a backend-status read, Qdrant's PingAsync lists collections
    // (never creates one), and Kafka's PingAsync reads broker metadata (never produces). None of
    // the four performs a write; do not reintroduce one here.
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    // CSR round 12 §6.3. The worker grants the api's login its privileges at startup, so an api can
    // be up before they exist. Readiness waits for them; for the login's iverson_runtime membership,
    // probed with the same SET LOCAL ROLE the api's startup runs, in a transaction that is rolled
    // back; and for the registry's first load, which the api retries in the background instead of
    // crash-looping. A read, a rolled-back role switch and a flag, so the endpoint stays passive,
    // and all three sit behind the 2 s cache below. For the worker all three hold once its own
    // startup has run: it owns the tables and is a member of iverson_runtime.
    var grantsTask = db.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql).ContinueWith(t => t.IsCompletedSuccessfully && t.Result);
    var roleTask   = storeRoles.EnsureCanEnterRuntimeRoleAsync().ContinueWith(t => t.IsCompletedSuccessfully);
    var srTask     = sr.CheckHealthAsync();
    var vectorTask = vector.PingAsync();
    var kafkaTask  = kafka.PingAsync();

    await Task.WhenAll(pgTask, grantsTask, roleTask, srTask, vectorTask, kafkaTask);

    var srStatus = await srTask;
    var engagementEnabled = engagementOptions.Value.Enabled;
    var registryLoaded = registry.HasLoaded;
    var checks = new
    {
        postgres    = pgTask.Result,
        apiGrants   = grantsTask.Result,
        runtimeRole = roleTask.Result,
        registry    = registryLoaded,
        starrocks   = engagementEnabled ? (object)(srStatus == EngagementHealthStatus.Healthy) : "disabled",
        qdrant      = vectorTask.Result,
        kafka       = kafkaTask.Result
    };

    var readiness = ReadinessPolicy.Evaluate(
        pgTask.Result && grantsTask.Result && roleTask.Result && registryLoaded,
        srStatus, vectorTask.Result, kafkaTask.Result, engagementEnabled);
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:
```csharp
var schemaRegistry = app.Services.GetRequiredService<SchemaRegistry>();
await schemaRegistry.LoadAsync();
```
with:
```csharp
var schemaRegistry = app.Services.GetRequiredService<SchemaRegistry>();
var schemaManager  = app.Services.GetRequiredService<IRecordStoreSchemaManager>();

if (workloadRole == "worker")
{
    // CSR round 12 §6.3. All startup DDL and the owner's startup writes run here, in the worker,
    // which connects as the database owner. WorkerStoreBootstrap creates the plumbing tables,
    // ensures the entity roles, and grants the api's login what it may do. On Helm that login is a
    // CNPG managed role that can appear shortly after the worker starts, so the worker waits up to
    // a minute for it. Compose has no CNPG, and its superuser worker creates the login from
    // Postgres:ApiLoginPassword.
    await WorkerStoreBootstrap.RunAsync(
        schemaManager,
        app.Services.GetRequiredService<ISchemaRegistryRepository>(),
        app.Services.GetRequiredService<IEnrichmentStateRepository>(),
        app.Services.GetRequiredService<IDocumentRerenderQueueRepository>(),
        app.Services.GetRequiredService<ITokenRevocationRepository>(),
        cfg["Postgres:ApiLoginPassword"],
        TimeSpan.FromMinutes(1));

    if (cfg.GetValue("Tenancy:SeedLegacyTenants", false))
    {
        var tenantRepository = app.Services.GetRequiredService<ITenantRepository>();
        foreach (var legacyTenantId in new[] { "tenant_loadtest", "tenant_webtest", "tenant_admin", "tenant_smoke_test", "tenant_bypass" })
            await tenantRepository.SeedIfMissingAsync(legacyTenantId, legacyTenantId, "active");
    }

    await schemaRegistry.LoadAsync();

    // Self-heal RLS state for tables whose descriptor was registered before this change shipped —
    // their physical DDL predates the tenant policy/RLS/grant this schema manager now applies.
    foreach (var descriptor in schemaRegistry.All.Values)
        await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));
}
else
{
    // CSR round 12 §6.3. The api runs no DDL, and its role check is only the iverson_runtime probe.
    // On a fresh install it can start before the worker has created the tables or granted its
    // login, so a failure here is logged, not thrown. SchemaRefreshWorker retries the load every
    // 30 s, and /health reports not-ready until the registry has loaded and the login holds its
    // grants and can enter iverson_runtime (/health repeats this probe).
    try
    {
        await schemaManager.EnsureCanEnterRuntimeRoleAsync();
        await schemaRegistry.LoadAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "The api's Postgres access is not ready yet, most likely because the worker has not "
            + "finished its startup. The schema registry load retries in the background, and /health "
            + "reports not-ready until it succeeds.");
    }
}
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace (see the Interfaces rebase rule if Task 7 added a line to this block):
```csharp
// Plumbing table for the enrichment loop breaker — created the same way SchemaRegistry creates
// its own backing table (SchemaRegistry.LoadAsync → repository.EnsureTableAsync).
await app.Services.GetRequiredService<IEnrichmentStateRepository>().EnsureTableAsync();

// Plumbing table for the document re-render queue — same bootstrap shape as the enrichment
// loop breaker above; raw DDL because it needs the two partial unique indexes ApplySchemaAsync
// cannot express.
await app.Services.GetRequiredService<IDocumentRerenderQueueRepository>().EnsureTableAsync();

// Token revocations (CSR round-10 #11) — same bootstrap shape: raw DDL, run as the owner role
// through the plain executor, so no grants are needed.
await app.Services.GetRequiredService<ITokenRevocationRepository>().EnsureTableAsync();

// EnsureRolesAsync must run before ANY ApplySchemaAsync call, since that DDL now GRANTs to
// iverson_maintenance on every table (and to iverson_runtime on the tenant-scoped ones) — both
// roles have to exist first.
var schemaManager = app.Services.GetRequiredService<IRecordStoreSchemaManager>();
await schemaManager.EnsureRolesAsync();
await schemaManager.ApplySchemaAsync(Iverson.Api.Reconciliation.ReconciliationSchema.Table);
await schemaManager.ApplySchemaAsync(Iverson.Api.Reconciliation.DlqSchema.Table);
await schemaManager.ApplySchemaAsync(Iverson.Api.Tenancy.TenantSchema.Table);
await schemaManager.ApplySchemaAsync(Iverson.Api.PrivilegedRequests.PrivilegedRequestSchema.Table);

if (cfg.GetValue("Tenancy:SeedLegacyTenants", false))
{
    var tenantRepository = app.Services.GetRequiredService<ITenantRepository>();
    foreach (var legacyTenantId in new[] { "tenant_loadtest", "tenant_webtest", "tenant_admin", "tenant_smoke_test", "tenant_bypass" })
        await tenantRepository.SeedIfMissingAsync(legacyTenantId, legacyTenantId, "active");
}

// Self-heal RLS state for tables whose descriptor was registered before this change shipped —
// their physical DDL predates the tenant policy/RLS/grant this schema manager now applies.
foreach (var descriptor in schemaRegistry.All.Values)
    await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));

// ── gRPC endpoints ─────────────────────────────────────────────────────────────
```
with:
```csharp
// ── gRPC endpoints ─────────────────────────────────────────────────────────────
```

Check that nothing else in the startup path still runs DDL or seeds in the api, and that the old helper text is gone:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && command grep -n "EnsureTableAsync\|EnsureRolesAsync\|SeedIfMissingAsync\|ApplySchemaAsync" Iverson.Api/Program.cs Iverson.Api/Schema/SchemaRegistry.cs
```
Expected: only these four lines remain:
- `Iverson.Api/Program.cs`: `            await tenantRepository.SeedIfMissingAsync(...)` and `        await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));`, both inside the `if (workloadRole == "worker")` block;
- `Iverson.Api/Schema/SchemaRegistry.cs`: `        await repository.EnsureTableAsync();` inside `RegisterAsync`, which only the worker reaches after Task 7, plus the `// No EnsureTableAsync here` comment.

- [ ] **Step 6: Run the tests and see them pass**

Unit (not heavy, but run them one at a time):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failed. `OutboxWriterTests` passes 13 of 13.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.ApiStartupReadinessPipelineTests.|FullyQualifiedName~.SchemaRegistryStartupTests.|FullyQualifiedName~.TenantlessDecisionRefusalTests.|FullyQualifiedName~.AuthenticationPipelineTests."
```
Expected: 0 failed. The new classes pass 6, 3 and 2 (`ApiStartupReadinessPipelineTests`, `SchemaRegistryStartupTests`, `TenantlessDecisionRefusalTests`); every `AuthenticationPipelineTests` case passes.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failed. This covers every other `WebApplicationFactory` host, which now goes through the api branch with the no-op schema manager. It also covers the mapping and retrieval suites, whose real evaluator still yields a tenant.

Integration (**heavy, run each alone**):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.ApiLoginGrantsPostgresIntegrationTests."
```
Expected: `Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14`.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~.PostgresIntegrationTests."
```
Expected: 0 failed. The `EnsureRolesAsync_*` cases cover `EnsureRoleAsync`'s switch to `RoleExistsAsync`.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~.TenantScopedAccessIntegrationTests."
```
Expected: 0 failed. These tests cover the outbox writer's now-unconditional role scope against real RLS.

- [ ] **Step 7: Mutation proof** (make each change by hand, run the named command, then undo it by hand before the next)

Step 2's red runs are the counterfactual for the three fail-closed guards (the outbox writer, GetMany and Delete), so they are not repeated here.

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | The registry load runs no DDL | In `SchemaRegistry.LoadAsync`, insert `await repository.EnsureTableAsync();` before `var rows = await repository.LoadAllAsync();` | `SchemaRegistryStartupTests.LoadAsync_RunsNoDdl`, `ApiStartupReadinessPipelineTests.ApiStartup_RunsNoDdlAndNoSeed_AndProbesOnlyTheRuntimeRole` (unit command from Step 6, second block) |
| M2 | Readiness waits for the grants | In `/health`, change `pgTask.Result && grantsTask.Result && roleTask.Result && registryLoaded,` to `pgTask.Result && roleTask.Result && registryLoaded,` | `GetHealth_WhenTheLoginLacksTheOutboxInsertGrant_IsNotReady`, `GetHealth_WhenTheGrantProbeFails_IsNotReady` (same command) |
| M5 | Readiness waits for the `iverson_runtime` probe | In `/health`, change `pgTask.Result && grantsTask.Result && roleTask.Result && registryLoaded,` to `pgTask.Result && grantsTask.Result && registryLoaded,` | `GetHealth_WhenTheRuntimeRoleProbeFails_IsNotReady_EvenWithTheGrantsAndTheRegistry` (same command) |
| M3 | The api probes only `iverson_runtime` | In Program.cs's api branch, change `await schemaManager.EnsureCanEnterRuntimeRoleAsync();` to `await schemaManager.EnsureRolesAsync();` | `ApiStartup_RunsNoDdlAndNoSeed_AndProbesOnlyTheRuntimeRole` (same command) |
| M4 | The DLQ grant excludes `MessageValue` | In `ApiLoginGrants.Statements`, replace the DLQ column grant with `$"""GRANT SELECT ON "{DlqSchema.TableName}" TO {Login}""",` | `Dlq_TheListAndTheReplayCheckColumnsAreReadable_ButMessageValueIsNot`, `ApiLogin_HoldsExactlyTheListedPrivileges` (integration command from Step 6, **heavy, alone**) |

Record the failing test names for each. After undoing M4, re-run the integration command once: it should pass 14 of 14.

- [ ] **Step 8: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add Iverson.Server/Iverson.Api/ApiLoginGrants.cs Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs Iverson.Server/Iverson.Sql/OutboxWriter.cs Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/TenantlessDecisionRefusalTests.cs Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryStartupTests.cs Iverson.Server/Iverson.Api.Tests/ApiStartupReadinessPipelineTests.cs Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs
git commit -m "run startup ddl only in the worker and give the api its own postgres login

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Per-tenant StarRocks logins

**Spec:** §7.1, §7.2 (connections, `SET ROLE` removal), §7.3, §7.4; §9 "§7, against the StarRocks container fixture" (closes #13, StarRocks half, code side)

**Decisions this task makes (each listed again in `D5-assumptions.md`):**
- **The configuration key** is `StarRocks:TenantKey` (env `StarRocks__TenantKey`), the name D6's Task 12 already renders. Both roles read it; startup fails without it when `Engagement:Enabled` is true, as for `ConnectionStrings:StarRocks`. This task adds the Development literal to `appsettings.Development.json` (the same literal Task 12 uses), because every `WebApplicationFactory` host in `Iverson.Api.Tests` boots in Development with engagement on and would otherwise fail at startup. Task 12 wires the Secret, the Helm env, compose and `generate-compose-secrets.sh`.
- **The password** is `Convert.ToHexStringLower(HMACSHA256.HashData(UTF8(key), UTF8("starrocks-tenant:" + tenantId)))`: 64 lower-case hex characters.
- **A tenant's connection string** also clears `Database`. The Development base string names `Database=iverson`, which a tenant login cannot use, and a default database the user cannot use fails the handshake (`StarRocksSplitLogins` doc). Every data statement is already qualified.
- **A refused tenant login becomes `EngagementNotReadyException`**, wrapped where the tenant connection opens, the same way round 11 wraps the provisioner login. Every gRPC read path already maps that exception to `Unavailable` (`ObjectSearchGrpcService.cs:114-116, 789-791, 837-839, 918-920`; `ObjectSearchGrpcService.MatchPattern.cs:171-173`). No service code changes. The projection consumers treat it as transient, so a worker with a wrong key halts and redelivers instead of dead-lettering. That is round 11's posture for the provisioner login.
- **`DeleteAsync` also consults the directory** and does nothing for an unrecorded tenant. Without this, a delete event whose create was dropped ("no authoritative row") for a never-provisioned tenant would fail at login and, through the wrap above, stall the engagement consumer. Today the same event dead-letters. This is a **forced decision**, recorded in `D5-assumptions.md`.
- **A repository built without the key or the directory** refuses every tenant-scoped data call with `InvalidOperationException`, before it connects. This matches round 11's refusal when the provisioner string is missing. The authz-null paths and the public executor (P3) need neither and keep running on the base login.
- **The `IversonStarRocksTenants` table and its grant are added here, to Task 9's anchors**, as D4 planned: one `ApplySchemaAsync` line in `WorkerStoreBootstrap.RunAsync` after `TenantSchema`, and one `GRANT SELECT` in `ApiLoginGrants.Statements`. The worker therefore creates the table before it issues the grants, in the same bootstrap. Task 9's exact-privileges test gains the new row.
- **Repeated provisioning calls** (once per tenant and table) re-run `CREATE USER IF NOT EXISTS` and `ALTER USER … SET PROPERTIES`. Both are idempotent (spec Verified assumption 24), so repeats are accepted rather than keyed per tenant.

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/IProvisionedTenantDirectory.cs`: the injected record of provisioned tenants.
- Modify: `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`: `LoginName` and `LoginPassword`, after `Fingerprint` (`:95-114`).
- Modify: `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`:
  - `:1`: drop `using System.Data;` (its only use, `ConnectionState`, goes with `SET ROLE NONE`);
  - `:15-21`: two trailing constructor parameters;
  - after `:38`: the tenant pool constants, `TenantConnectionString`, `TenantConnectionStringFor` and `IsRecordedAsync`;
  - `:125-136`: `OpenProvisionerConnectionAsync` shares a body with the new `OpenTenantConnectionAsync`;
  - `:191-233`: `IsExpectedMissingResourceError` loses its role arm;
  - `:248-284` (`RunTenantScopedAsync`), `:305-347` (`StreamTenantScopedAsync`), `:400-422` (`ReleaseAsync`): no `SET ROLE` or `SET ROLE NONE`, and the tenant's own connection;
  - `:434-436` (`MatchRowsAsync`), `:484` (`DeleteAsync`), `:594`, `:635`, `:695` and `:726` (the four readers): the directory check;
  - `:496-503`, `:523` and `:560-561` (`EnsureTenantProvisionedAsync`): the login and its caps replace the `iverson_app` grant, and the tenant is recorded after success.
- Modify: `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs:8-30`: the `tenantLoginKey` parameter and the registered directory.
- Create: `Iverson.Server/Iverson.Api/Tenancy/StarRocksTenantSchema.cs`: the table's schema.
- Create: `Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs`: the Postgres implementation, caching positive answers.
- Modify: `Iverson.Server/Iverson.Api/Program.cs`: the key guard (after `:280`), the `AddStarRocks` argument (`:322`), and the directory registration (after `:386-388`).
- Modify: `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs` (created by Task 9): the table, after `TenantSchema`.
- Modify: `Iverson.Server/Iverson.Api/ApiLoginGrants.cs` (created by Task 9): the api's `SELECT` on it.
- Modify: `Iverson.Server/Iverson.Api/appsettings.Development.json`: the Development key.
- Create test: `Iverson.Server/Iverson.StarRocks.Tests/InMemoryProvisionedTenantDirectory.cs`.
- Create test: `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryTenantLoginTests.cs` (light).
- Create test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs` (heavy).
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs`: login name and password tests, appended.
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs`: the two role cases leave `Exempt` and are pinned as no longer exempt.
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/ServiceCollectionExtensionsTests.cs`: the provisioner test gets a key and a directory, and a DI test is added.
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs:125-127`: the streaming-token test gets a key.
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs`: `TenantLoginKey`.
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs:26-34, 49-50`: key and directory, and a comment.
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs:26-31, 320-370`: key and directory, and the two pooled-connection tests re-pointed to per-tenant pools.
- Modify test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs:43-44`: key and directory.
- Modify test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`: Search, Aggregate and GroupBy answer a refused login with `Unavailable` (after `Pipeline_StarRocksNotReady_ThrowsUnavailable`, `:2370-2386`).
- Create test: `Iverson.Server/Iverson.Api.Tests/Tenancy/PostgresProvisionedTenantDirectoryTests.cs` (light).
- Create test: `Iverson.Server/Iverson.Api.Tests/Tenancy/ProvisionedTenantDirectoryPostgresIntegrationTests.cs` (heavy; Task 9's Postgres fixture).
- Modify test: `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs` (created by Task 9): `IversonStarRocksTenants:SELECT` joins the exact-privileges list.

**Interfaces:**
- **Produces:**
  - the configuration key `StarRocks:TenantKey`, env `StarRocks__TenantKey`: an opaque non-empty string whose UTF-8 bytes are the HMAC key. Task 12 sets it in the api and worker Deployments from `<release>-starrocks-tenant-key`, and in compose's `iverson-api` and `iverson-worker` from the `.env` value `generate-compose-secrets.sh` emits. **Task 12's `appsettings.Development.json` edit must be rebased**: this task already adds `"StarRocks": { "TenantKey": "dev-only-not-for-production-starrocks-tenant-key-0123456789" }`. Task 12's old text becomes the file as this task leaves it, and its new text only drops `Database=iverson;` from the `StarRocks` connection string;
  - the login `'iverson_t_<25 base-36 digits>'@'%'`, with default role the tenant role and the properties `'session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'` (`EngagementRepository.TenantSessionProperties`, which must stay equal to `job-create-user.yaml`'s `ALTER USER 'iverson_app' SET PROPERTIES`);
  - `EngagementRepository` no longer runs `GRANT <role> TO USER 'iverson_app'@'%'`. Task 12's post-upgrade revoke in `job-create-user.yaml` and compose `starrocks-init` relies on nothing re-granting;
  - the table `IversonStarRocksTenants ("TenantId" text PK, "ProvisionedAt" timestamptz NOT NULL)`, with `GRANT SELECT … TO iverson_api`;
  - `IProvisionedTenantDirectory`, `AddStarRocks(…, string? tenantLoginKey = null)`, and `EngagementRepository(…, string? tenantLoginKey = null, IProvisionedTenantDirectory? provisionedTenants = null)`.
- **Consumed by:** Task 11 (`TenantPoolSize`, the integration class), Task 12 (Secret, env, compose, runbook) and Task 13 (live checks).
- **Assumes from Task 9.**
  - Task 9 neither creates nor grants on `IversonStarRocksTenants`; this task does both, at the anchors D4 published (D4 **Interfaces**, "Produces for Task 10"):
    - `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs`: `        await schemaManager.ApplySchemaAsync(TenantSchema.Table);`;
    - `Iverson.Server/Iverson.Api/ApiLoginGrants.cs`: the `GRANT UPDATE ("Status") ON "IversonPrivilegedRequests"` line and the `];` after it;
    - `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs`: `            "IversonPrivilegedRequests:INSERT", "IversonPrivilegedRequests:SELECT",`.
  - Task 7's own `ApplySchemaAsync` line may also follow the `TenantSchema` line in `WorkerStoreBootstrap.RunAsync`. This task's replace keeps the anchor line, so the two inserts stack in either order.
  - `ApiLoginGrantsPostgresContainerFixture` (Task 9) runs `WorkerStoreBootstrap.RunAsync` against a real Postgres and exposes `Owner` and `Api`. This task's `ProvisionedTenantDirectoryPostgresIntegrationTests` reuses it.
- **Assumes from Task 3:** Task 3 changes only `UpsertAsync`'s column selection in `EngagementRepository.cs`. No hunk here touches `UpsertAsync`. If Task 3 adds a test that builds its own `EngagementRepository` and calls a tenant-scoped method, that call must also pass `tenantLoginKey: StarRocksSplitLogins.TenantLoginKey, provisionedTenants: new InMemoryProvisionedTenantDirectory()`.
- **Shared files:** `Program.cs` (Tasks 7, 8, 9): this task anchors only on the StarRocks block and the `ITenantRepository` registration, which D4 leaves alone (D4: "Task 10 edits DI (`:254-322`). Neither overlaps"). `WorkerStoreBootstrap.cs`, `ApiLoginGrants.cs` and `ApiLoginGrantsPostgresIntegrationTests.cs` (Task 9): see above. `ObjectSearchGrpcServiceTests.cs` (Task 4, Task 11): this task inserts after `Pipeline_StarRocksNotReady_ThrowsUnavailable`. `EngagementRepository.cs` (Task 3): see above; D1's `UpsertEntries` extraction leaves every anchor here intact (scripted replay of D1–D4, then D5).
- **Ordering:** Tasks 3 and 9 come before this task. Task 11 comes after it. Task 12 comes after it: compose and Helm boot only once Task 12 sets the key.

- [ ] **Step 1: Write the failing tests**

Record the baseline test counts of the classes this task changes. `--list-tests` starts no container.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --list-tests 2>/dev/null | command grep -cE '\.(TenantIsolationIntegrationTests|MatchRowsIntegrationTests|StarRocksCredentialSplitIntegrationTests)\.'
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --list-tests 2>/dev/null | command grep -cE '\.(TenantIdentifierTests|EngagementRepositoryLivenessRewrapTests|ServiceCollectionExtensionsTests|EngagementRepositoryTests)\.'
```

Expected: two numbers. Record them as `B_int` and `B_unit`.

**(a)** Create `Iverson.Server/Iverson.StarRocks.Tests/InMemoryProvisionedTenantDirectory.cs`:

```csharp
using System.Collections.Concurrent;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §7.3: the provisioned-tenant record held in memory, standing in for the worker's Postgres
/// table so the StarRocks tests need no Postgres. <see cref="EngagementRepository.EnsureTenantProvisionedAsync"/>
/// records into it and the readers consult it, exactly as they do the Postgres one. Thread-safe, because the
/// integration tests provision and read from several tasks at once.
/// </summary>
internal sealed class InMemoryProvisionedTenantDirectory : IProvisionedTenantDirectory
{
    private readonly ConcurrentDictionary<string, byte> _recorded = new(StringComparer.Ordinal);

    public Task<bool> IsProvisionedAsync(string tenantId) => Task.FromResult(_recorded.ContainsKey(tenantId));

    public Task RecordProvisionedAsync(string tenantId)
    {
        _recorded.TryAdd(tenantId, 0);
        return Task.CompletedTask;
    }
}
```

**(b)** In `Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs`, replace:

```csharp
    internal const string AppPassword = "test_pw";
```

with:

```csharp
    internal const string AppPassword = "test_pw";

    /// <summary>The key every test repository derives tenant login passwords from (CSR round 12 §7.1).</summary>
    internal const string TenantLoginKey = "test-tenant-login-key";
```

**(c)** Create `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryTenantLoginTests.cs`:

```csharp
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §7.2/§7.3: per-tenant StarRocks logins and the provisioned-tenant directory, without a server.
/// Port 1 refuses at once, so any call that reaches a connection waits out the readiness gate's 2 s and answers
/// <see cref="EngagementNotReadyException"/>; a call that answers anything else never connected.
/// </summary>
public class EngagementRepositoryTenantLoginTests
{
    private const string Unreachable = "Server=127.0.0.1;Port=1;User ID=iverson_app;Password=x;Connection Timeout=1";
    private const string Key = "test-tenant-login-key";

    private static readonly EngagementQuerySchema Schema = new("Probe", "probe", "Id", ["Name"]);

    private static readonly EngagementTableSchema TableSchema =
        new("probe", new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Name", "STRING", true)]);

    private static EngagementRepository Repo(IProvisionedTenantDirectory? directory, string? key = Key) =>
        new(Unreachable, NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(2) },
            provisionerConnectionString: Unreachable,
            tenantLoginKey: key,
            provisionedTenants: directory);

    private static Dictionary<string, AuthorizationConstraint> AuthzFor(string tenantId) =>
        new() { ["Probe"] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId) };

    // MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo grant to this
    // assembly; built via reflection against the exact overload (StarRocksResiliencePipelineFactoryTests).
    private static MySqlException CreateMySqlException(MySqlErrorCode errorCode, string message) =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, null, message, null]);

    // ── the tenant's connection string ──────────────────────────────────────────────────────────

    [Fact]
    public void TenantConnectionString_LogsInAsTheTenantsOwnUser_WithItsOwnPoolBounds()
    {
        const string baseString =
            "Server=sr-fe;Port=9030;User ID=iverson_app;Password=app-pw;Database=iverson;SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt";

        var built = new MySqlConnectionStringBuilder(EngagementRepository.TenantConnectionString(baseString, "acme", Key));

        built.UserID.Should().Be(TenantIdentifier.LoginName("acme"));
        built.Password.Should().Be(TenantIdentifier.LoginPassword("acme", Key));
        built.Database.Should().BeEmpty();
        built.MaximumPoolSize.Should().Be(10u);
        built.ConnectionIdleTimeout.Should().Be(60u);
        built.ConnectionReset.Should().BeFalse();
        built.Server.Should().Be("sr-fe");
        built.Port.Should().Be(9030u);
        built.SslMode.Should().Be(MySqlSslMode.VerifyFull);
        built.SslCa.Should().Be("/etc/starrocks-tls/ca.crt");
    }

    [Fact]
    public void TenantConnectionString_IsIdenticalForOneTenant_AndDistinctAcrossTenants()
    {
        // MySqlConnector keys its pools by connection string: one string per tenant is one pool per tenant.
        EngagementRepository.TenantConnectionString(Unreachable, "acme", Key)
            .Should().Be(EngagementRepository.TenantConnectionString(Unreachable, "acme", Key));
        EngagementRepository.TenantConnectionString(Unreachable, "acme", Key)
            .Should().NotBe(EngagementRepository.TenantConnectionString(Unreachable, "acme-2", Key));
    }

    // ── opening it ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AccessDenied_OnOpeningATenantConnection_SurfacesAsNotReady()
    {
        var original = CreateMySqlException(MySqlErrorCode.AccessDenied,
            "Access denied for user 'iverson_t_0123456789abcdefghijklmno'@'10.0.0.1' (using password: YES)");

        var act = () => EngagementRepository.OpenTenantConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<EngagementNotReadyException>())
            .Which.InnerException.Should().BeSameAs(original);
    }

    [Fact]
    public async Task OtherFailure_OnOpeningATenantConnection_IsRethrownUnchanged()
    {
        var original = CreateMySqlException(MySqlErrorCode.UnableToConnectToHost,
            "Unable to connect to any of the specified MySQL hosts.");

        var act = () => EngagementRepository.OpenTenantConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<MySqlException>()).Which.Should().BeSameAs(original);
    }

    // ── the provisioned-tenant directory ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnrecordedTenant_EveryReaderAnswersEmpty_WithoutConnecting()
    {
        var repo = Repo(new InMemoryProvisionedTenantDirectory());
        var registry = TestSchemaRegistry.BuildRegistry(Schema);
        var groupBy = new GroupByRequest { TypeName = "Probe", Keys = { "Name" } };
        groupBy.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });
        var matchRows = new MatchRowsRequest(
            [], SearchLogic.And, [], [new SearchSort { Property = "Name" }], [], [], true, [], 100);

        (await repo.SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"))).Should().BeEmpty();
        (await repo.AggregateAsync(Schema, null, new AggregationDescriptor("cnt", AggregationKind.Count, "Name"),
            authz: AuthzFor("t1"))).Should().BeNull();
        (await repo.GroupByAsync(Schema, groupBy, registry, AuthzFor("t1"))).Should().BeEmpty();
        (await repo.PipelineAsync(Schema, new PipelineRequest { TypeName = "Probe" }, registry, AuthzFor("t1")))
            .Should().BeEmpty();
        var rows = new List<IDictionary<string, object?>>();
        await foreach (var row in repo.MatchRowsAsync(Schema, matchRows, AuthzFor("t1")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task UnrecordedTenant_DeleteHasNothingToRemove_AndDoesNotConnect()
    {
        var act = () => Repo(new InMemoryProvisionedTenantDirectory()).DeleteAsync("probe", "Id", "k1", "t1");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordedTenant_IsConnectedFor()
    {
        var directory = new InMemoryProvisionedTenantDirectory();
        await directory.RecordProvisionedAsync("t1");

        var act = () => Repo(directory).SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"));

        await act.Should().ThrowAsync<EngagementNotReadyException>();
    }

    [Fact]
    public async Task NoTenantLoginKey_RefusesTenantScopedReadsAndWrites_BeforeConnecting()
    {
        var directory = new InMemoryProvisionedTenantDirectory();
        await directory.RecordProvisionedAsync("t1");
        var repo = Repo(directory, key: null);

        var read = () => repo.SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"));
        var write = () => repo.UpsertAsync(TableSchema, JsonSerializer.Serialize(new { Id = "k1", Name = "n" }), "t1");

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");
    }

    [Fact]
    public async Task NoDirectory_RefusesReads_BeforeConnecting()
    {
        var act = () => Repo(directory: null).SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");
    }

    // ── provisioning ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProvisioningThatFails_DoesNotRecordTheTenant()
    {
        var directory = new InMemoryProvisionedTenantDirectory();

        var act = () => Repo(directory).EnsureTenantProvisionedAsync("t1", TableSchema);

        await act.Should().ThrowAsync<EngagementNotReadyException>();
        (await directory.IsProvisionedAsync("t1")).Should().BeFalse();
    }

    [Fact]
    public async Task ProvisioningWithoutADirectory_RefusesBeforeConnecting()
    {
        var act = () => Repo(directory: null).EnsureTenantProvisionedAsync("t1", TableSchema);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");
    }
}
```

**(d)** In `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs`, replace:

```csharp
    [Fact]
    public void Qualify_WithEmptyStringTenantDatabase_TreatsAsNonNull()
    {
        // Empty string is not null, so it should be treated as a database name
        TenantIdentifier.Qualify("", "articles").Should().Be("``.`articles`");
    }
}
```

with:

```csharp
    [Fact]
    public void Qualify_WithEmptyStringTenantDatabase_TreatsAsNonNull()
    {
        // Empty string is not null, so it should be treated as a database name
        TenantIdentifier.Qualify("", "articles").Should().Be("``.`articles`");
    }

    // ── LoginName / LoginPassword (CSR round 12 §7.1) ──────────────────────────────────────────

    [Fact]
    public void LoginName_IsThePrefixPlusTheRoleNamesFingerprint()
    {
        // The fingerprint of "acme" was computed outside .NET (SHA-256, low 128 bits, 25 base-36 digits).
        TenantIdentifier.LoginName("acme").Should().Be("iverson_t_7pfddyte668561tz83en42m5m");
        TenantIdentifier.RoleName("acme").Should().EndWith("_7pfddyte668561tz83en42m5m");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("tenant-bypass")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz")]   // 52 characters, the maximum
    public void LoginName_Is35Characters_WithNoHyphen(string tenantId)
    {
        var login = TenantIdentifier.LoginName(tenantId);

        login.Should().HaveLength(35);
        login.Should().MatchRegex("^iverson_t_[0-9a-z]{25}$");
    }

    [Fact]
    public void LoginName_KeepsApartTenantsTheReadableRolePartCollapses()
    {
        TenantIdentifier.LoginName("a-b").Should().NotBe(TenantIdentifier.LoginName("a_b"));
    }

    [Fact]
    public void LoginPassword_IsTheHexHmacOfTheTenantIdUnderTheKey()
    {
        // HMAC-SHA256 under the UTF-8 key "test-tenant-login-key" of "starrocks-tenant:acme", computed outside .NET.
        TenantIdentifier.LoginPassword("acme", "test-tenant-login-key")
            .Should().Be("401cfbf7dd97a41c7b4d514d4c625ddb59f3789a8c91cec743ac4451c30c1aa3");
    }

    [Fact]
    public void LoginPassword_Is64LowerCaseHexCharacters_AndDependsOnTheKeyAndTheTenant()
    {
        var password = TenantIdentifier.LoginPassword("acme", "k1");

        password.Should().MatchRegex("^[0-9a-f]{64}$");
        password.Should().NotBe(TenantIdentifier.LoginPassword("acme", "k2"));
        password.Should().NotBe(TenantIdentifier.LoginPassword("acme-2", "k1"));
    }
}
```

**(e)** In `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs`, replace:

```csharp
    private static readonly Dictionary<string, Func<Exception>> Exempt = new()
    {
        ["missing resource: cannot find role"]  = () => CreateMySqlException(MySqlErrorCode.ParseError, "cannot find role role_tenant_x"),
        ["missing resource: is not granted to"] = () => CreateMySqlException(MySqlErrorCode.ParseError, "Role role_tenant_x is not granted to 'iverson_app'@'%'"),
        ["missing resource: unknown table"]     = () => CreateMySqlException((MySqlErrorCode)5502, "Unknown table 'iverson.articles'"),
```

with:

```csharp
    private static readonly Dictionary<string, Func<Exception>> Exempt = new()
    {
        ["missing resource: unknown table"]     = () => CreateMySqlException((MySqlErrorCode)5502, "Unknown table 'iverson.articles'"),
```

In `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs`, replace:

```csharp
    private sealed class ProbeFailedException() : Exception("connection refused");
}
```

with:

```csharp
    // CSR round 12 §7.3: no data connection runs SET ROLE any more, so the role errors SET ROLE raised are no
    // longer the expected empty-tenant shape (an unrecorded tenant is answered before connecting). Should one
    // occur, it is a real fault and pays the liveness check like any other non-transient failure.
    public static TheoryData<string> FormerRoleArmCases() => new(FormerRoleArm.Keys);

    private static readonly Dictionary<string, Func<Exception>> FormerRoleArm = new()
    {
        ["cannot find role"]  = () => CreateMySqlException(MySqlErrorCode.ParseError, "cannot find role role_tenant_x"),
        ["is not granted to"] = () => CreateMySqlException(MySqlErrorCode.ParseError, "Role role_tenant_x is not granted to 'iverson_app'@'%'"),
    };

    [Theory]
    [MemberData(nameof(FormerRoleArmCases))]
    public async Task FormerRoleArm_IsNoLongerExempt_AndPaysTheLivenessCheck(string name)
    {
        var check = new FakeCheck(_ => Task.FromResult(true));

        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(FormerRoleArm[name](), check.Invoke, Bound);

        result.Should().BeNull(name);
        check.Calls.Should().Be(1, name);
    }

    private sealed class ProbeFailedException() : Exception("connection refused");
}
```

**(f)** In `Iverson.Server/Iverson.StarRocks.Tests/ServiceCollectionExtensionsTests.cs`, replace:

```csharp
        services.AddStarRocks(
            unreachable,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(2) },
            provisionerConnectionString: unreachable);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IEngagementStoreEntityStore>();
        var schema = new EngagementTableSchema("probe", new EngagementColumnSchema("Id", "VARCHAR(36)", false), []);

        var act = () => store.EnsureTenantProvisionedAsync("t1", schema);

        await act.Should().ThrowAsync<EngagementNotReadyException>();
    }
}
```

with:

```csharp
        // CSR round 12 §7.1: provisioning also needs the tenant login key and the directory, or it refuses
        // before connecting.
        services.AddSingleton<IProvisionedTenantDirectory>(new InMemoryProvisionedTenantDirectory());
        services.AddStarRocks(
            unreachable,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(2) },
            provisionerConnectionString: unreachable,
            tenantLoginKey: "k");

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IEngagementStoreEntityStore>();
        var schema = new EngagementTableSchema("probe", new EngagementColumnSchema("Id", "VARCHAR(36)", false), []);

        var act = () => store.EnsureTenantProvisionedAsync("t1", schema);

        await act.Should().ThrowAsync<EngagementNotReadyException>();
    }

    // CSR round 12 §7.2/§7.3: the tenant login key and the registered IProvisionedTenantDirectory reach the
    // repository. Without the directory, every tenant-scoped read refuses with InvalidOperationException; with
    // it, an unrecorded tenant reads as empty without connecting. Without the key, a recorded tenant's read
    // refuses the same way; with it, the read gets as far as the readiness gate, which cannot reach port 1.
    [Fact]
    public async Task AddStarRocks_HandsTheTenantLoginKeyAndTheRegisteredDirectoryToTheRepository()
    {
        const string unreachable = "Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1";
        var directory = new InMemoryProvisionedTenantDirectory();
        await directory.RecordProvisionedAsync("recorded");
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<EngagementRepository>>(NullLogger<EngagementRepository>.Instance);
        services.AddSingleton<IProvisionedTenantDirectory>(directory);
        services.AddStarRocks(
            unreachable,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(2) },
            tenantLoginKey: "k");

        using var provider = services.BuildServiceProvider();
        var search = provider.GetRequiredService<IEngagementStoreSearchService>();
        var schema = new EngagementQuerySchema("Probe", "probe", "Id", ["Name"]);
        static Dictionary<string, AuthorizationConstraint> AuthzFor(string tenantId) =>
            new() { ["Probe"] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId) };

        (await search.SearchAsync(schema, null, 0, 10, authz: AuthzFor("unrecorded"))).Should().BeEmpty();
        var act = () => search.SearchAsync(schema, null, 0, 10, authz: AuthzFor("recorded"));
        await act.Should().ThrowAsync<EngagementNotReadyException>();
    }
}
```

**(g)** In `Iverson.Server/Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs`, replace:

```csharp
        var repo = new EngagementRepository("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1",
            NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(3) });
```

with:

```csharp
        // A tenant login key, so the read gets past the tenant-login refusal (CSR round 12 §7.2) to the gate.
        var repo = new EngagementRepository("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1",
            NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(3) },
            tenantLoginKey: "k");
```

**(h)** In `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs`, replace:

```csharp
        // The two production logins (CSR round 11 §3), set up exactly as job-create-user.yaml sets them
        // up. StarRocksSplitLogins explains the grants, why they are safe to re-run in any class order,
        // and why the connection strings name no default database. _appRepo queries as iverson_app and
        // provisions as iverson_provisioner, as the worker does.
        var (appConnectionString, provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();

        _appRepo = new EngagementRepository(appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString);
```

with:

```csharp
        // The two production logins (CSR round 11 §3), set up exactly as job-create-user.yaml sets them
        // up. StarRocksSplitLogins explains the grants, why they are safe to re-run in any class order,
        // and why the connection strings name no default database. _appRepo provisions as
        // iverson_provisioner, which creates each tenant's own login, and reads and writes as that login
        // (CSR round 12 §7.2), recording tenants in memory as the worker records them in Postgres.
        var (appConnectionString, provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();

        _appRepo = new EngagementRepository(appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString,
            tenantLoginKey: StarRocksSplitLogins.TenantLoginKey,
            provisionedTenants: new InMemoryProvisionedTenantDirectory());
```

In `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs`, replace:

```csharp
    // TenantColumn deliberately left null throughout this file: the point of these tests is to
    // prove the SET ROLE / GRANT boundary (Tasks 2-6) blocks cross-tenant reads on its own, not
```

with:

```csharp
    // TenantColumn deliberately left null throughout this file: the point of these tests is to
    // prove the per-tenant login / GRANT boundary blocks cross-tenant reads on its own, not
```

**(i)** In `Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs`, replace:

```csharp
        _repo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString);
```

with:

```csharp
        _repo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString,
            tenantLoginKey: StarRocksSplitLogins.TenantLoginKey,
            provisionedTenants: new InMemoryProvisionedTenantDirectory());
```

**(j)** In `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs`, replace:

```csharp
        _appRepo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString);
```

with:

```csharp
        _appRepo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString,
            tenantLoginKey: StarRocksSplitLogins.TenantLoginKey,
            provisionedTenants: new InMemoryProvisionedTenantDirectory());
```

Then replace the two pooled-connection tests. Their premise was `SET ROLE` and `SET ROLE NONE`, which this task removes (spec §7.2, `verify-section45.md` D15).

In `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs`, replace:

```csharp
    // CIR-1 Finding 2(a): StreamTenantScopedAsync's ReleaseAsync runs `SET ROLE NONE` before returning its
    // connection to the pool. `MaximumPoolSize=1` forces the very next connection opened on this same
    // connection string to be that exact physical connection; `ConnectionReset=false` stops MySqlConnector's
    // own pool-return reset from clearing session state itself, so only our own `SET ROLE NONE` (or its
    // absence, under the M4 mutant deleting it) decides what `CURRENT_ROLE()` reports next.
    // `SELECT CURRENT_ROLE()` was verified live against this StarRocks 4.1.1 image before writing this
    // assertion: it returns the bare role name string when a role is active, and the literal string
    // "NONE" (not SQL NULL) once `SET ROLE NONE` has run.
    [Fact]
    public async Task Stopping_a_read_early_still_clears_the_tenant_role_before_the_connection_is_reused()
    {
        var (tenant, schema) = await SeedAsync(Events);
        var pooledConnectionString = new MySqlConnectionStringBuilder(_appConnectionString)
        {
            MaximumPoolSize = 1, ConnectionReset = false
        }.ToString();
        var pooledRepo = new EngagementRepository(pooledConnectionString, NullLogger<EngagementRepository>.Instance);

        var reader = pooledRepo.MatchRowsAsync(schema, Req(oneRow: false), Authz(tenant)).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();   // one row read, then stop early
        await reader.DisposeAsync();

        await using var conn = new MySqlConnection(pooledConnectionString);
        await conn.OpenAsync();   // MaximumPoolSize=1: this is necessarily the same physical connection
        var role = await conn.ExecuteScalarAsync<string>("SELECT CURRENT_ROLE()");
        role.Should().Be("NONE");
    }

    // CIR-1 Finding 2(b): the open-phase catch in StreamTenantScopedAsync must release its connection even
    // when `SET ROLE` itself fails (an unprovisioned tenant), or that connection is never returned to the
    // pool. `MaximumPoolSize=1` makes a leak observable: under the M6 mutant (deleting the open-phase
    // `ReleaseAsync(c, null)` call), the pool's one slot stays held forever and the second read below never
    // gets a connection to open.
    [Fact]
    public async Task A_failed_SET_ROLE_releases_its_connection_so_the_next_read_can_still_use_the_pool()
    {
        var (tenant, schema) = await SeedAsync(Events);
        var pooledConnectionString = new MySqlConnectionStringBuilder(_appConnectionString)
        {
            MaximumPoolSize = 1
        }.ToString();
        var pooledRepo = new EngagementRepository(pooledConnectionString, NullLogger<EngagementRepository>.Instance);
        var neverProvisionedTenant = "m" + Guid.NewGuid().ToString("N")[..16];

        (await ReadAllAsync(pooledRepo.MatchRowsAsync(schema, Req(), Authz(neverProvisionedTenant))))
            .Should().BeEmpty();   // SET ROLE fails for a valid-but-never-provisioned tenant id

        var rows = await ReadAllAsync(pooledRepo.MatchRowsAsync(schema, Req(), Authz(tenant)))
            .WaitAsync(TimeSpan.FromSeconds(30));   // a leaked connection would hold the only pool slot
        rows.Should().HaveCount(4);
    }
```

with:

```csharp
    // CSR round 12 §7.2, replacing CIR-1 Finding 2(a)'s SET ROLE NONE test: tenant connection strings turn off
    // MySqlConnector's pooled reset, because StarRocks' reset would put the login's caps back to the global
    // defaults (spec Verified assumption 26). So whatever one read leaves on a connection is seen by the next
    // read from the same pool. Per-tenant logins give every tenant a pool of its
    // own, so that state stays inside the tenant. The first assertion checks the premise: this test can see
    // leftover state at all.
    [Fact]
    public async Task Session_state_a_read_leaves_behind_stays_inside_that_tenants_own_pool()
    {
        var (tenantA, _) = await SeedAsync(Events);
        var (tenantB, _) = await SeedAsync(Events);

        await ReadAllAsync(_appRepo.StreamTenantScopedAsync(
            "sr.test", tenantA, "SET query_timeout = 7", new DynamicParameters(), _ => false, default));

        (await QueryTimeoutAsync(tenantA)).Should().Be(7, "tenant pools do not reset session state");
        (await QueryTimeoutAsync(tenantB)).Should().Be(180, "tenant B's pool is its own, and its login carries the cap");

        async Task<long> QueryTimeoutAsync(string tenant)
        {
            var rows = await ReadAllAsync(_appRepo.StreamTenantScopedAsync(
                "sr.test", tenant, "SELECT @@query_timeout AS t", new DynamicParameters(), _ => false, default));
            return Convert.ToInt64(rows.Single()["t"]);
        }
    }

    // CIR-1 Finding 2(b), re-pointed to per-tenant pools (CSR round 12 §7.2): the open-phase catch in
    // StreamTenantScopedAsync must release its connection when the query fails to start, or that slot never
    // returns to the pool. A tenant's pool holds EngagementRepository.TenantPoolSize connections, so that many
    // failed starts would fill it under a leak, and the read after them could never open.
    [Fact]
    public async Task Failed_query_starts_release_their_connections_so_the_tenants_pool_never_fills()
    {
        var (tenant, schema) = await SeedAsync(Events);

        for (var i = 0; i < EngagementRepository.TenantPoolSize; i++)
            (await ReadAllAsync(_appRepo.StreamTenantScopedAsync(
                "sr.test", tenant, "SELEC 1", new DynamicParameters(), _ => true, default))).Should().BeEmpty();

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(tenant)))
            .WaitAsync(TimeSpan.FromSeconds(30));   // a leak would hold every slot: the open waits, then fails
        rows.Should().HaveCount(4);
    }
```

**(k)** This class is heavy: it shares the one StarRocks container.

Create `Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs`:

```csharp
using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §7: per-tenant StarRocks logins against a live engine. iverson_provisioner provisions each
/// tenant and creates its own login; reads and writes then log in as that tenant. iverson_app holds no tenant
/// role here (StarRocksSplitLogins), so every data statement that succeeds ran as a tenant login.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StarRocksCollection.Name)]
public sealed class StarRocksTenantLoginIntegrationTests
{
    private readonly StarRocksContainerFixture _fx;
    private readonly string _appConnectionString;
    private readonly string _provisionerConnectionString;
    private readonly InMemoryProvisionedTenantDirectory _directory = new();
    private readonly EngagementRepository _repo;

    public StarRocksTenantLoginIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;
        (_appConnectionString, _provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();
        _repo = Repository(StarRocksSplitLogins.TenantLoginKey, _directory);
    }

    private EngagementRepository Repository(string tenantLoginKey, IProvisionedTenantDirectory directory) =>
        new(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: _provisionerConnectionString,
            tenantLoginKey: tenantLoginKey,
            provisionedTenants: directory);

    private static string UniqueTenantId() => "t" + Guid.NewGuid().ToString("N")[..16];
    private static string UniqueTable() => "tbl_" + Guid.NewGuid().ToString("N")[..8];

    private static EngagementTableSchema ProbeSchema(string table) =>
        new(table, new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Name", "STRING", true)]);

    private static EngagementQuerySchema ProbeQuerySchema(string table) => new("Probe", table, "Id", ["Name"]);

    private static Dictionary<string, AuthorizationConstraint> AuthzFor(string tenantId) =>
        new() { ["Probe"] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId) };

    private static string ProbePayload(string id, string name) =>
        System.Text.Json.JsonSerializer.Serialize(new { Id = id, Name = name });

    private static string Qualified(string tenant, string table) =>
        TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenant), table);

    /// <summary>A fresh tenant with one table holding one row, provisioned and written through the repository.</summary>
    private async Task<(string Tenant, string Table)> ProvisionWithRowAsync(string name)
    {
        var tenant = UniqueTenantId();
        var table = UniqueTable();
        await _repo.EnsureTenantProvisionedAsync(tenant, ProbeSchema(table));
        await _repo.UpsertAsync(ProbeSchema(table), ProbePayload(Guid.NewGuid().ToString(), name), tenant);
        return (tenant, table);
    }

    private async Task<MySqlConnection> OpenAsTenantAsync(string tenant)
    {
        var conn = new MySqlConnection(
            EngagementRepository.TenantConnectionString(_appConnectionString, tenant, StarRocksSplitLogins.TenantLoginKey));
        await conn.OpenAsync();
        return conn;
    }

    // ── what a tenant login can do ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantLogin_HasItsRoleActiveAtLogin_AndReadsItsOwnTable()
    {
        var (tenant, table) = await ProvisionWithRowAsync("Own");
        await using var conn = await OpenAsTenantAsync(tenant);

        (await conn.ExecuteScalarAsync<string>("SELECT CURRENT_ROLE()")).Should().Contain(TenantIdentifier.RoleName(tenant));
        (await conn.QueryAsync<string>($"SELECT Name FROM {Qualified(tenant, table)}")).Should().Equal("Own");
    }

    [Fact]
    public async Task TenantLogin_CarriesTheSessionCapsTheAppLoginCarries()
    {
        var (tenant, _) = await ProvisionWithRowAsync("Caps");
        await using var conn = await OpenAsTenantAsync(tenant);

        (await conn.ExecuteScalarAsync<long>("SELECT @@query_timeout")).Should().Be(180);
        (await conn.ExecuteScalarAsync<long>("SELECT @@exec_mem_limit")).Should().Be(4294967296);
    }

    // ── what it cannot do ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantLogin_IsDeniedAnotherTenantsTable()
    {
        var (tenantA, _) = await ProvisionWithRowAsync("A");
        var (tenantB, tableB) = await ProvisionWithRowAsync("B");
        await using var conn = await OpenAsTenantAsync(tenantA);

        var act = () => conn.QueryAsync<string>($"SELECT Name FROM {Qualified(tenantB, tableB)}");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    [Fact]
    public async Task TenantLogin_CannotTakeOnAnotherTenantsRole()
    {
        var (tenantA, _) = await ProvisionWithRowAsync("A");
        var (tenantB, tableB) = await ProvisionWithRowAsync("B");
        await using var conn = await OpenAsTenantAsync(tenantA);

        var setRole = () => conn.ExecuteAsync($"SET ROLE `{TenantIdentifier.RoleName(tenantB)}`");
        await setRole.Should().ThrowAsync<MySqlException>();

        await conn.ExecuteAsync("SET ROLE ALL");
        var read = () => conn.QueryAsync<string>($"SELECT Name FROM {Qualified(tenantB, tableB)}");
        await read.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    [Fact]
    public async Task Provisioning_GrantsTheAppLoginNoTenantRole()
    {
        var (tenant, _) = await ProvisionWithRowAsync("NoGrant");

        var grants = await _fx.Repository.QueryAsync<dynamic>("SHOW GRANTS FOR 'iverson_app'@'%'");
        var text = string.Join("\n", grants
            .SelectMany(g => ((IDictionary<string, object>)g).Values)
            .Select(v => v?.ToString()));

        text.Should().NotContain(TenantIdentifier.RoleName(tenant));
    }

    // ── the provisioned-tenant record ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Provisioning_RecordsTheTenant()
    {
        var tenant = UniqueTenantId();
        (await _directory.IsProvisionedAsync(tenant)).Should().BeFalse();

        await _repo.EnsureTenantProvisionedAsync(tenant, ProbeSchema(UniqueTable()));

        (await _directory.IsProvisionedAsync(tenant)).Should().BeTrue();
    }

    [Fact]
    public async Task UnrecordedTenant_ReadsAsEmpty_EvenWhenItsDataExists()
    {
        var (tenant, table) = await ProvisionWithRowAsync("Hidden");
        var unrecorded = Repository(StarRocksSplitLogins.TenantLoginKey, new InMemoryProvisionedTenantDirectory());

        (await unrecorded.SearchAsync(ProbeQuerySchema(table), null, 0, 50, authz: AuthzFor(tenant))).Should().BeEmpty();
        (await unrecorded.AggregateAsync(ProbeQuerySchema(table), null,
            new AggregationDescriptor("cnt", AggregationKind.Count, "Name"), authz: AuthzFor(tenant))).Should().BeNull();
    }

    [Fact]
    public async Task RecordedTenant_WhoseLoginIsRefused_FailsAsNotReady()
    {
        // The gRPC read paths answer EngagementNotReadyException with Unavailable (ObjectSearchGrpcServiceTests).
        var (tenant, table) = await ProvisionWithRowAsync("WrongKey");
        var wrongKey = Repository("a-different-tenant-login-key", _directory);

        var act = () => wrongKey.SearchAsync(ProbeQuerySchema(table), null, 0, 50, authz: AuthzFor(tenant));

        (await act.Should().ThrowAsync<EngagementNotReadyException>())
            .Which.InnerException.Should().BeOfType<MySqlException>()
            .Which.ErrorCode.Should().Be(MySqlErrorCode.AccessDenied);
    }

    // ── the worker's writes and aggregates ──────────────────────────────────────────────────────

    [Fact]
    public async Task WorkerWritesAndAggregates_RunUnderTheTenantLogin()
    {
        var tenant = UniqueTenantId();
        var table = UniqueTable();
        await _repo.EnsureTenantProvisionedAsync(tenant, ProbeSchema(table));
        await _repo.UpsertAsync(ProbeSchema(table), ProbePayload("40000000-0000-0000-0000-000000000001", "One"), tenant);
        await _repo.UpsertAsync(ProbeSchema(table), ProbePayload("40000000-0000-0000-0000-000000000002", "Two"), tenant);
        await _repo.DeleteAsync(table, "Id", "40000000-0000-0000-0000-000000000001", tenant);

        var count = await _repo.AggregateAsync(ProbeQuerySchema(table), null,
            new AggregationDescriptor("cnt", AggregationKind.Count, "Name"), authz: AuthzFor(tenant));

        count.Should().NotBeNull();
        count!.MetricValue.Should().Be(1);
    }
}
```

**(l)** In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:

```csharp
        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task Pipeline_EngagementStoreDisabled_ThrowsFailedPrecondition()
```

with:

```csharp
        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    // CSR round 12 §7.3: a recorded tenant whose StarRocks login is refused surfaces from EngagementRepository
    // as EngagementNotReadyException (StarRocksTenantLoginIntegrationTests). Every read RPC must answer it with
    // Unavailable, not Unknown; Pipeline and MatchPattern are pinned above and in MatchPatternGrpcServiceTests.
    private const string TenantLoginRefused = "StarRocks refused the tenant's login.";

    [Fact]
    public async Task Search_TenantLoginRefused_ThrowsUnavailable()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementNotReadyException(TenantLoginRefused));

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task Aggregate_TenantLoginRefused_ThrowsUnavailable()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<SrAggResult?>>(_ => throw new EngagementNotReadyException(TenantLoginRefused));

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec { Name = "name_terms", Type = AggregationType.Terms, Field = "Name" });
        var act = async () => await _sut.Aggregate(request, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task GroupBy_TenantLoginRefused_ThrowsUnavailable()
    {
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));
        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementNotReadyException(TenantLoginRefused));

        var request = new GroupByRequest { TypeName = "Owned", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });
        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task Pipeline_EngagementStoreDisabled_ThrowsFailedPrecondition()
```

**(m)** Create `Iverson.Server/Iverson.Api.Tests/Tenancy/PostgresProvisionedTenantDirectoryTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round 12 §7.3: the Postgres-backed provisioned-tenant record caches a tenant once it is found, and only
/// then. Provisioning never undoes itself, but a tenant not yet recorded may be recorded by the worker at any
/// moment, so a negative answer is never kept.
/// </summary>
public class PostgresProvisionedTenantDirectoryTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();

    private void PostgresAnswers(bool first, params bool[] rest) =>
        _sql.QuerySingleOrDefaultAsync<bool>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
            .Returns(first, rest);

    [Fact]
    public async Task ARecordedTenant_IsCached_SoPostgresIsAskedOnce()
    {
        PostgresAnswers(true);
        var directory = new PostgresProvisionedTenantDirectory(_sql);

        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();
        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();

        await _sql.Received(1).QuerySingleOrDefaultAsync<bool>(
            Arg.Is<string>(s => s.Contains("\"IversonStarRocksTenants\"")), Arg.Any<object?>(),
            Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AnUnrecordedTenant_IsAskedAgain_BecauseTheWorkerMayRecordItAtAnyMoment()
    {
        PostgresAnswers(false, true);
        var directory = new PostgresProvisionedTenantDirectory(_sql);

        (await directory.IsProvisionedAsync("acme")).Should().BeFalse();
        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();

        await _sql.Received(2).QuerySingleOrDefaultAsync<bool>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Recording_InsertsIdempotently_AndCachesTheTenant()
    {
        var directory = new PostgresProvisionedTenantDirectory(_sql);

        await directory.RecordProvisionedAsync("acme");

        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("INSERT INTO \"IversonStarRocksTenants\"")
                             && s.Contains("ON CONFLICT (\"TenantId\") DO NOTHING")),
            Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();
        await _sql.DidNotReceive().QuerySingleOrDefaultAsync<bool>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }
}
```

**(n)** This class is heavy: it starts a Postgres container (Task 9's fixture, one per class).

Create `Iverson.Server/Iverson.Api.Tests/Tenancy/ProvisionedTenantDirectoryPostgresIntegrationTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Api.Tenancy;
using Npgsql;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round 12 §7.3 and §6.2: the provisioned-tenant record against a real Postgres. The table and the api's
/// grant come from the worker's startup, run exactly as Program.cs runs it
/// (<see cref="ApiLoginGrantsPostgresContainerFixture"/>), and the record is read through the api's own login.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ProvisionedTenantDirectoryPostgresIntegrationTests(ApiLoginGrantsPostgresContainerFixture fixture)
    : IClassFixture<ApiLoginGrantsPostgresContainerFixture>
{
    private static string UniqueTenantId() => "t" + Guid.NewGuid().ToString("N")[..16];

    [Fact]
    public async Task ATenantTheWorkerRecords_IsProvisioned_ForTheApiLogin()
    {
        var tenantId = UniqueTenantId();
        var api = new PostgresProvisionedTenantDirectory(fixture.Api);

        (await api.IsProvisionedAsync(tenantId)).Should().BeFalse();
        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);

        (await api.IsProvisionedAsync(tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task Recording_IsIdempotent()
    {
        var tenantId = UniqueTenantId();

        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);
        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);

        (await fixture.Owner.QuerySingleOrDefaultAsync<long>(
                """SELECT count(*) FROM "IversonStarRocksTenants" WHERE "TenantId" = @TenantId""",
                new { TenantId = tenantId }))
            .Should().Be(1);
    }

    [Fact]
    public async Task TheApiLogin_CanOnlyRead()
    {
        var tenantId = UniqueTenantId();
        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);

        Func<Task> record = () => new PostgresProvisionedTenantDirectory(fixture.Api).RecordProvisionedAsync(UniqueTenantId());
        Func<Task> update = () => fixture.Api.ExecuteAsync(
            """UPDATE "IversonStarRocksTenants" SET "ProvisionedAt" = now() WHERE "TenantId" = @TenantId""",
            new { TenantId = tenantId });
        Func<Task> delete = () => fixture.Api.ExecuteAsync(
            """DELETE FROM "IversonStarRocksTenants" WHERE "TenantId" = @TenantId""",
            new { TenantId = tenantId });

        foreach (var act in new[] { record, update, delete })
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj 2>&1 | command grep -E 'error CS' | sed -E 's/.*error (CS[0-9]+): //' | sort -u
```

Expected: the build fails. This is the red state; there is nothing to run yet.
- `CS0246`: `IProvisionedTenantDirectory` could not be found (in `InMemoryProvisionedTenantDirectory`, `EngagementRepositoryTenantLoginTests`, `StarRocksTenantLoginIntegrationTests` and `ServiceCollectionExtensionsTests`).
- `CS1739`: no parameter named `tenantLoginKey` or `provisionedTenants` on the `EngagementRepository` constructor or on `AddStarRocks`.
- `CS0117`: `TenantIdentifier` has no `LoginName` or `LoginPassword`, and `EngagementRepository` has no `TenantConnectionString`, `OpenTenantConnectionAsync` or `TenantPoolSize`.
- `CS1061` cascades from the above, such as `FunctionAssertions<?>` having no `ThrowAsync`.

No other error should appear. Any other error means a test file is wrong; fix the test, not the expectation.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | command grep -E 'error CS' | sed -E 's/.*error (CS[0-9]+): //' | sort -u
```

Expected: the build fails with only `CS0246`, for `PostgresProvisionedTenantDirectory` (in the two new `Tenancy` test classes). The three new `ObjectSearchGrpcServiceTests` facts compile. They pin a mapping that already exists, which the spec now relies on, so they pass from the start; Step 5's mutation M5 proves they can fail.

- [ ] **Step 3: Implement**

**(a)** Create `Iverson.Server/Iverson.StarRocks/IProvisionedTenantDirectory.cs`:

```csharp
namespace Iverson.StarRocks;

/// <summary>
/// CSR round 12 §7.3: which tenants have a StarRocks login. The worker records a tenant once provisioning it
/// succeeds, so a recorded tenant's login exists; every StarRocks reader asks before connecting and answers an
/// unrecorded tenant as empty. This project references no Postgres project, so the record is injected:
/// Iverson.Api implements it over the <c>IversonStarRocksTenants</c> table.
/// </summary>
public interface IProvisionedTenantDirectory
{
    /// <summary>True once the tenant has been recorded as provisioned.</summary>
    Task<bool> IsProvisionedAsync(string tenantId);

    /// <summary>Records the tenant as provisioned. Idempotent. Called only after the tenant's login exists.</summary>
    Task RecordProvisionedAsync(string tenantId);
}
```

**(b)** In `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs`, replace:

```csharp
        return new string(buffer);
    }

    internal static string Qualify(string? tenantDatabase, string tableName) =>
```

with:

```csharp
        return new string(buffer);
    }

    // ── tenant logins (CSR round 12 §7.1) ─────────────────────────────────────────────────────

    private const string LoginPrefix = "iverson_t_";

    /// <summary>
    /// The tenant's own StarRocks login, <c>iverson_t_{fingerprint}</c>: the same 25-digit fingerprint
    /// <see cref="RoleName"/> ends in, so 35 characters for every tenant. The fingerprint alone keeps tenants
    /// apart (see <see cref="RoleName"/>); a login name has no readable part, because nobody greps for it.
    /// </summary>
    internal static string LoginName(string tenantId) => LoginPrefix + Fingerprint(tenantId);

    /// <summary>
    /// The tenant login's password: lower-case hex HMAC-SHA256 of <c>"starrocks-tenant:" + tenantId</c> under
    /// the UTF-8 bytes of <paramref name="tenantLoginKey"/>, 64 characters. Deterministic, so the api and the
    /// worker derive the same password from the key they share with nothing stored, and every caller builds the
    /// identical connection string for a tenant, hence the same pool.
    /// </summary>
    internal static string LoginPassword(string tenantId, string tenantLoginKey) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(tenantLoginKey),
            Encoding.UTF8.GetBytes("starrocks-tenant:" + tenantId)));

    internal static string Qualify(string? tenantDatabase, string tableName) =>
```

**(c)** `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`.

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
using System.Data;
using System.Data.Common;
```

with:

```csharp
using System.Data.Common;
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
    EngagementQueryLimitOptions? queryLimitOptions = null,
    string? provisionerConnectionString = null)
    : IEngagementStoreQueryExecutor, IEngagementStoreEntityStore, IEngagementStoreSearchService
```

with:

```csharp
    EngagementQueryLimitOptions? queryLimitOptions = null,
    string? provisionerConnectionString = null,
    string? tenantLoginKey = null,
    IProvisionedTenantDirectory? provisionedTenants = null)
    : IEngagementStoreQueryExecutor, IEngagementStoreEntityStore, IEngagementStoreSearchService
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
    private MySqlConnection CreateProvisionerConnection() => new(provisionerConnectionString);
```

with:

```csharp
    private MySqlConnection CreateProvisionerConnection() => new(provisionerConnectionString);

    // ── Per-tenant logins (CSR round 12 §7.2) ───────────────────────────────────
    // Every tenant-scoped data connection logs in as the tenant's own StarRocks user, whose default role is
    // the tenant's role, so it reaches that tenant's database and nothing else. The base login
    // (connectionString, iverson_app) keeps only the readiness gate, the liveness probe and the authz-null
    // executor paths, and holds no data grant.

    /// <summary>The most connections one tenant's pool may hold in this process (spec §7.2).</summary>
    internal const uint TenantPoolSize = 10;

    /// <summary>Seconds an idle pooled tenant connection lives before it is reaped (spec §7.2), so an idle
    /// tenant's pool drains to zero connections.</summary>
    internal const uint TenantPoolIdleSeconds = 60;

    /// <summary>The per-session caps every tenant login carries: the pair job-create-user.yaml sets on
    /// iverson_app (spec §7.1). Keep the two equal.</summary>
    internal const string TenantSessionProperties =
        "'session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'";

    private const string NoTenantLoginsMessage =
        "Tenant-scoped StarRocks access needs the tenant login key (StarRocks:TenantKey) and the " +
        "provisioned-tenant directory, and this repository was not given both.";

    /// <summary>
    /// One tenant's connection string: <paramref name="baseConnectionString"/> with the tenant's own user and
    /// derived password, no default database (every statement is qualified, and a default database the user
    /// cannot use fails the handshake), and the per-tenant pool bounds. MySqlConnector's pooled reset is off:
    /// StarRocks' reset puts system session variables back to their global defaults, which would drop the caps
    /// the login carries (spec Verified assumption 26). Deterministic, so every call for one tenant yields the
    /// identical string and therefore MySqlConnector's one pool for it. Another tenant gets another string and
    /// a pool of its own, so session state a pooled connection keeps across checkouts never reaches another
    /// tenant.
    /// </summary>
    internal static string TenantConnectionString(string baseConnectionString, string tenantId, string tenantLoginKey) =>
        new MySqlConnectionStringBuilder(baseConnectionString)
        {
            UserID                = TenantIdentifier.LoginName(tenantId),
            Password              = TenantIdentifier.LoginPassword(tenantId, tenantLoginKey),
            Database              = "",
            MaximumPoolSize       = TenantPoolSize,
            ConnectionIdleTimeout = TenantPoolIdleSeconds,
            ConnectionReset       = false,
        }.ToString();

    private string TenantConnectionStringFor(string tenantId) =>
        TenantConnectionString(connectionString, tenantId,
            tenantLoginKey ?? throw new InvalidOperationException(NoTenantLoginsMessage));

    // CSR round 12 §7.3: a tenant the worker has not recorded as provisioned has no login yet, so the readers
    // answer it as empty, and DeleteAsync, which has nothing to remove, returns, all without connecting.
    private Task<bool> IsRecordedAsync(string tenantId) =>
        (provisionedTenants ?? throw new InvalidOperationException(NoTenantLoginsMessage))
            .IsProvisionedAsync(tenantId);
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
    internal static async Task OpenProvisionerConnectionAsync(Func<Task> open)
    {
        try
        {
            await open().ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
        {
            throw new EngagementNotReadyException(
                "StarRocks refused the provisioner login; it may not have been created yet.", ex);
        }
    }
```

with:

```csharp
    internal static Task OpenProvisionerConnectionAsync(Func<Task> open) =>
        OpenOrSurfaceRefusalAsync(open, "StarRocks refused the provisioner login; it may not have been created yet.");

    /// <summary>
    /// Opens a tenant's own connection (CSR round 12 §7.3). Readers connect only for a tenant the worker has
    /// recorded, and the worker records it only after creating its login, so a refusal here is a real fault (a
    /// login removed by hand, or a tenant login key other than the one the login was created with), never an
    /// empty tenant. It surfaces as <see cref="EngagementNotReadyException"/>: the gRPC read paths answer that
    /// with <c>Unavailable</c> rather than <c>Unknown</c>, and the projection consumers redeliver instead of
    /// dead-lettering. Every other failure propagates unchanged.
    /// </summary>
    internal static Task OpenTenantConnectionAsync(Func<Task> open) =>
        OpenOrSurfaceRefusalAsync(open, "StarRocks refused the tenant's login.");

    private static async Task OpenOrSurfaceRefusalAsync(Func<Task> open, string refusal)
    {
        try
        {
            await open().ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
        {
            throw new EngagementNotReadyException(refusal, ex);
        }
    }
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
    // Two distinct "no data yet" shapes on the read path, both treated as fail-closed-empty
    // rather than propagated as exceptions:
    //
    // 1. Unprovisioned tenant: a tenant that has never written ANY data was never lazily
    //    provisioned by EnsureTenantProvisionedAsync, so its `role_tenant_{id}` either doesn't
    //    exist yet or was never granted to `iverson_app`. `SET ROLE` inside RunTenantScopedAsync
    //    then fails outright. Verified empirically against a live starrocks/allin1-ubuntu
    //    container: both "role doesn't exist" and "role exists but isn't granted to this user"
    //    raise MySqlConnector.MySqlException with ErrorCode == MySqlErrorCode.ParseError
    //    (StarRocks reuses MySQL's generic 1064 syntax-error code for this) and a message
    //    containing either "cannot find role" or "is not granted to".
    //
    // 2. Unwritten type within a provisioned tenant: EnsureTenantProvisionedAsync creates exactly
    //    one table per call (on that type's own first write), not every table a tenant will ever
    //    use — so a tenant that has written type A but queries type B (never written) has a
    //    perfectly valid role/database, but the specific table doesn't exist. Verified
    //    empirically against the same container: querying a nonexistent table under a database
    //    the active role legitimately has grants on raises MySqlException with ErrorCode 5502
    //    (SqlState 42602 — StarRocks-specific, no named MySqlErrorCode member) and a message
    //    containing "Unknown table". Deliberately distinct from — and must never accidentally
    //    match — two adjacent error shapes that must keep propagating as real failures: querying
    //    a wrong/nonexistent *database* name (a real bug, e.g. a tenant-ID-to-database-name
    //    mapping defect) raises a different message, "Unknown database" (ErrorCode 5501); and an
    //    actual permissions problem on a table that legitimately exists raises yet another
    //    message, "Access denied ... privilege(s) on TABLE ..." (ErrorCode 5203). Confirmed both
    //    do NOT contain "Unknown table" and so are correctly left unmatched here.
    //
    // Both cases are matched here as specific, verified StarRocks/MySqlConnector error shapes
    // (not a bare catch) so the 4 read paths can treat them the same as every other "no data"
    // case in this repository (invalid tenant ID, missing tenant claim: both already return
    // empty), while any other StarRocks failure (real outage, network issue, a genuinely
    // different privilege or schema problem) still propagates as an exception. Deliberately
    // scoped to the READ path only — UpsertAsync/DeleteAsync must keep throwing, since they're
    // only ever called after EnsureTenantProvisionedAsync has already run for that exact type in
    // the same code path (EngagementStoreConsumer), so hitting either shape there would mean a
    // real invariant violation, not a legitimate empty tenant.
    private static bool IsExpectedMissingResourceError(Exception ex) =>
        ex is MySqlException mex &&
        ((mex.ErrorCode == MySqlErrorCode.ParseError &&
          (mex.Message.Contains("cannot find role", StringComparison.OrdinalIgnoreCase) ||
           mex.Message.Contains("is not granted to", StringComparison.OrdinalIgnoreCase))) ||
         (mex.ErrorCode == (MySqlErrorCode)5502 &&
          mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase)));
```

with:

```csharp
    // The one "no data yet" shape on the read path that is treated as fail-closed-empty rather
    // than propagated as an exception: an unwritten type within a provisioned tenant.
    // EnsureTenantProvisionedAsync creates exactly one table per call (on that type's own first
    // write), not every table a tenant will ever use — so a tenant that has written type A but
    // queries type B (never written) has a perfectly valid login, role and database, but the
    // specific table doesn't exist. Verified empirically against a live container: querying a
    // nonexistent table under a database the active role legitimately has grants on raises
    // MySqlException with ErrorCode 5502 (SqlState 42602 — StarRocks-specific, no named
    // MySqlErrorCode member) and a message containing "Unknown table". Deliberately distinct
    // from — and must never accidentally match — two adjacent error shapes that must keep
    // propagating as real failures: querying a wrong/nonexistent *database* name (a real bug,
    // e.g. a tenant-ID-to-database-name mapping defect) raises a different message, "Unknown
    // database" (ErrorCode 5501); and an actual permissions problem on a table that legitimately
    // exists raises yet another message, "Access denied ... privilege(s) on TABLE ..." (ErrorCode
    // 5203). Confirmed both do NOT contain "Unknown table" and so are correctly left unmatched.
    //
    // An unprovisioned tenant is no longer an error shape (CSR round 12 §7.3): every reader asks
    // IProvisionedTenantDirectory first and answers a tenant the worker has not recorded as empty,
    // without connecting. The role arm this predicate used to carry ("cannot find role" / "is not
    // granted to", ParseError 1064 from SET ROLE) went with SET ROLE itself, so a recorded tenant
    // whose login or role is missing now surfaces as the fault it is.
    //
    // Matched as a specific, verified StarRocks/MySqlConnector error shape (not a bare catch) so
    // the read paths can treat it the same as every other "no data" case in this repository
    // (invalid tenant ID, missing tenant claim, unrecorded tenant: all return empty), while any
    // other StarRocks failure still propagates as an exception. Deliberately scoped to the READ
    // path only — UpsertAsync/DeleteAsync must keep throwing, since UpsertAsync only ever runs
    // after EnsureTenantProvisionedAsync has created that exact type's table in the same code path
    // (EngagementStoreConsumer), so hitting it there would mean a real invariant violation.
    private static bool IsExpectedMissingResourceError(Exception ex) =>
        ex is MySqlException mex &&
        mex.ErrorCode == (MySqlErrorCode)5502 &&
        mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase);
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
        // Defensive re-validation: every current caller already checks TenantIdentifier.IsValid
        // before calling in, so this never fires today. It exists purely so a future caller that
        // forgets that check fails loudly and immediately, rather than splicing an unvalidated
        // tenantId straight into the `SET ROLE` statement below.
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        try
        {
            var result = await RunAsync(async () =>
            {
                await using var conn = CreateConnection();
                await conn.OpenAsync();
                await conn.ExecuteAsync($"SET ROLE `{TenantIdentifier.RoleName(tenantId)}`");
                try
                {
                    return await operation(conn);
                }
                finally
                {
                    try
                    {
                        await conn.ExecuteAsync("SET ROLE NONE");
                    }
                    catch (Exception ex)
                    {
                        // The connection is being disposed either way; a broken connection here must
                        // never replace the operation's own result or exception — same discipline as
                        // PostgresRepository.ExecuteInTransactionAsync's rollback-failure handling.
                        logger.LogWarning(ex, "SET ROLE NONE failed while releasing a tenant-scoped StarRocks connection");
                    }
                }
            });
```

with:

```csharp
        // Defensive re-validation: every current caller already checks TenantIdentifier.IsValid
        // before calling in, so this never fires today. It exists purely so a future caller that
        // forgets that check fails loudly and immediately, rather than deriving a login for an
        // unvalidated tenantId.
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        var tenantConnectionString = TenantConnectionStringFor(tenantId);

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        try
        {
            var result = await RunAsync(async () =>
            {
                // CSR round 12 §7.2: the tenant's own login, whose default role is already active.
                // Nothing here changes the connection's role or session state, and the pool is this
                // tenant's alone, so whatever a pooled connection keeps stays inside the tenant.
                await using var conn = new MySqlConnection(tenantConnectionString);
                await OpenTenantConnectionAsync(() => conn.OpenAsync());
                return await operation(conn);
            });
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
    /// <summary>
    /// Streams a tenant-scoped query row by row (spec §3.2). Unlike <see cref="RunTenantScopedAsync{T}"/>, the
    /// resilience pipeline wraps only open, <c>SET ROLE</c> and query start; a failure after that propagates. The
    /// connection is held until enumeration ends, then <c>SET ROLE NONE</c> runs with the same failure-swallowing
    /// discipline. <paramref name="ct"/> reaches the query start and every read, and is checked before each row.
    /// </summary>
    internal async IAsyncEnumerable<IDictionary<string, object?>> StreamTenantScopedAsync(
        string activityName, string tenantId, string sql, object param,
        Func<Exception, bool> isExpectedMissingResource, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        MySqlConnection? conn = null;
        DbDataReader? reader = null;
        try
        {
            (conn, reader) = await RunAsync(async () =>
            {
                var c = CreateConnection();
                try
                {
                    await c.OpenAsync(ct);
                    await c.ExecuteAsync(new CommandDefinition(
                        $"SET ROLE `{TenantIdentifier.RoleName(tenantId)}`", cancellationToken: ct));
                    var r = await c.ExecuteReaderAsync(new CommandDefinition(sql, param, cancellationToken: ct));
                    return (c, r);
                }
                catch
                {
                    await ReleaseAsync(c, null);
                    throw;
                }
            }, ct);
        }
        catch (Exception ex) when (isExpectedMissingResource(ex))
        {
            activity?.SetStatus(ActivityStatusCode.Ok);   // an unprovisioned tenant or missing table: empty, as SearchAsync
        }
```

with:

```csharp
    /// <summary>
    /// Streams a tenant-scoped query row by row (spec §3.2). Unlike <see cref="RunTenantScopedAsync{T}"/>, the
    /// resilience pipeline wraps only open and query start; a failure after that propagates. The connection, on
    /// the tenant's own login (CSR round 12 §7.2), is held until enumeration ends and then returned to that
    /// tenant's pool. <paramref name="ct"/> reaches the query start and every read, and is checked before each row.
    /// </summary>
    internal async IAsyncEnumerable<IDictionary<string, object?>> StreamTenantScopedAsync(
        string activityName, string tenantId, string sql, object param,
        Func<Exception, bool> isExpectedMissingResource, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        var tenantConnectionString = TenantConnectionStringFor(tenantId);

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        MySqlConnection? conn = null;
        DbDataReader? reader = null;
        try
        {
            (conn, reader) = await RunAsync(async () =>
            {
                var c = new MySqlConnection(tenantConnectionString);
                try
                {
                    await OpenTenantConnectionAsync(() => c.OpenAsync(ct));
                    var r = await c.ExecuteReaderAsync(new CommandDefinition(sql, param, cancellationToken: ct));
                    return (c, r);
                }
                catch
                {
                    await ReleaseAsync(c, null);
                    throw;
                }
            }, ct);
        }
        catch (Exception ex) when (isExpectedMissingResource(ex))
        {
            activity?.SetStatus(ActivityStatusCode.Ok);   // a missing table: empty, as SearchAsync
        }
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
    private async Task ReleaseAsync(MySqlConnection conn, DbDataReader? reader)
    {
        try
        {
            if (reader is not null) await reader.DisposeAsync();
            // A connection that never reached Open (e.g. OpenAsync itself timed out or threw) must not be
            // reopened here: Dapper's ExecuteAsync opens a closed connection by itself, which against a
            // frozen/unreachable server makes a second, uncancellable connection attempt on top of the
            // one that already failed or was cancelled — CIR-1 Finding 1. Only a connection that is
            // actually Open can have an active role to clear.
            if (conn.State == ConnectionState.Open)
                await conn.ExecuteAsync("SET ROLE NONE");
        }
        catch (Exception ex)
        {
            // Same discipline as RunTenantScopedAsync: releasing must never replace the read's own outcome.
            logger.LogWarning(ex, "Releasing a tenant-scoped StarRocks reader failed (SET ROLE NONE or reader disposal)");
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
```

with:

```csharp
    private async Task ReleaseAsync(MySqlConnection conn, DbDataReader? reader)
    {
        try
        {
            if (reader is not null) await reader.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Releasing must never replace the read's own outcome.
            logger.LogWarning(ex, "Releasing a tenant-scoped StarRocks reader failed (reader disposal)");
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
        var (sql, param) = MatchRowsQueryBuilder.Build(schema, request, authz, TenantIdentifier.DatabaseName(tenantId));

        await foreach (var row in StreamTenantScopedAsync("sr.match_rows", tenantId, sql, param, IsExpectedMissingResourceError, ct))
```

with:

```csharp
        var (sql, param) = MatchRowsQueryBuilder.Build(schema, request, authz, TenantIdentifier.DatabaseName(tenantId));

        if (!await IsRecordedAsync(tenantId))
            yield break;

        await foreach (var row in StreamTenantScopedAsync("sr.match_rows", tenantId, sql, param, IsExpectedMissingResourceError, ct))
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
    public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;
```

with:

```csharp
    public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;

        // CSR round 12 §7.3: a tenant never recorded as provisioned has no login and no rows to remove. A
        // delete for it (one whose create was dropped before projection) is a no-op, not a refused login.
        if (!await IsRecordedAsync(tenantId)) return;
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
        if (provisionerConnectionString is null)
            throw new InvalidOperationException(
                "Tenant provisioning needs the StarRocks provisioner connection string, and none was configured.");

        var dbName   = TenantIdentifier.DatabaseName(tenantId);
```

with:

```csharp
        if (provisionerConnectionString is null)
            throw new InvalidOperationException(
                "Tenant provisioning needs the StarRocks provisioner connection string, and none was configured.");

        // CSR round 12 §7.1/§7.3: provisioning creates the tenant's own login and then records the tenant, so
        // it needs both halves of what a reader needs. Both are checked before connecting.
        var directory = provisionedTenants ?? throw new InvalidOperationException(NoTenantLoginsMessage);
        var loginPassword = TenantIdentifier.LoginPassword(
            tenantId, tenantLoginKey ?? throw new InvalidOperationException(NoTenantLoginsMessage));
        var loginName = TenantIdentifier.LoginName(tenantId);

        var dbName   = TenantIdentifier.DatabaseName(tenantId);
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
                await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_app'@'%'");
```

with:

```csharp
                // CSR round 12 §7.1: the tenant's own login replaces the grant of the tenant role to
                // iverson_app. DEFAULT ROLE assigns the role created above and activates it at every login,
                // so no data connection runs SET ROLE. IF NOT EXISTS leaves an existing login as it is. The
                // caps are re-applied on every call, which SET PROPERTIES allows.
                await conn.ExecuteAsync(
                    $"CREATE USER IF NOT EXISTS '{loginName}'@'%' IDENTIFIED BY '{loginPassword}' DEFAULT ROLE '{roleName}'");
                await conn.ExecuteAsync($"ALTER USER '{loginName}'@'%' SET PROPERTIES ({TenantSessionProperties})");
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
                    logger.LogWarning(ex, "SET ROLE NONE failed while releasing a tenant-provisioning StarRocks connection");
                }
            }
        });
        activity?.SetStatus(ActivityStatusCode.Ok);
    }
```

with:

```csharp
                    logger.LogWarning(ex, "SET ROLE NONE failed while releasing a tenant-provisioning StarRocks connection");
                }
            }
        });

        // Only after the login exists: a recorded tenant is one readers connect for.
        await directory.RecordProvisionedAsync(tenantId);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }
```

Now the four readers. Each check runs after the SQL is built, so a malformed request still fails with its translation error for an empty tenant, as it does today.

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
            TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        try
        {
            return await RunTenantScopedAsync(
                "sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param), IsExpectedMissingResourceError);
        }
        catch (Exception ex) when (IsExpectedMissingResourceError(ex))
        {
            return [];
        }
    }

    public async Task<AggregationResult?> AggregateAsync(
```

with:

```csharp
            TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        if (!await IsRecordedAsync(tenantId))
            return [];

        try
        {
            return await RunTenantScopedAsync(
                "sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param), IsExpectedMissingResourceError);
        }
        catch (Exception ex) when (IsExpectedMissingResourceError(ex))
        {
            return [];
        }
    }

    public async Task<AggregationResult?> AggregateAsync(
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
                TenantIdentifier.DatabaseName(tenantId), _queryLimits);
            try
            {
                rows = (await RunTenantScopedAsync(
```

with:

```csharp
                TenantIdentifier.DatabaseName(tenantId), _queryLimits);

            if (!await IsRecordedAsync(tenantId))
                return null;

            try
            {
                rows = (await RunTenantScopedAsync(
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
            schema.TableName, schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        try
        {
```

with:

```csharp
            schema.TableName, schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        if (!await IsRecordedAsync(tenantId))
            return [];

        try
        {
```

In `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`, replace:

```csharp
            schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId), _queryLimits);
        try
        {
            var rows = await RunTenantScopedAsync(
```

with:

```csharp
            schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        if (!await IsRecordedAsync(tenantId))
            return [];

        try
        {
            var rows = await RunTenantScopedAsync(
```

`CreateConnection()` stays: `QueryAsync` and `ExecuteAsync` (the authz-null branches and the public executor, spec §7.2) still use the base login. `RunAsync`'s readiness gate and liveness probe keep `connectionString`; a tenant login has no `OPERATE ON SYSTEM` for `SHOW BACKENDS`.

**(d)** In `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs`, replace:

```csharp
        EngagementQueryLimitOptions? queryLimitOptions = null,
        string? provisionerConnectionString = null)
```

with:

```csharp
        EngagementQueryLimitOptions? queryLimitOptions = null,
        string? provisionerConnectionString = null,
        string? tenantLoginKey = null)
```

In `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs`, replace:

```csharp
            return new EngagementRepository(
                connectionString, logger, resilienceOptions, resolvedQueryLimits, provisionerConnectionString);
```

with:

```csharp
            // CSR round 12 §7.3: the provisioned-tenant directory comes from the host (Iverson.Api registers the
            // Postgres one). Without it, tenant-scoped data calls refuse rather than connect.
            return new EngagementRepository(
                connectionString, logger, resilienceOptions, resolvedQueryLimits, provisionerConnectionString,
                tenantLoginKey, sp.GetService<IProvisionedTenantDirectory>());
```

**(e)** Create `Iverson.Server/Iverson.Api/Tenancy/StarRocksTenantSchema.cs`:

```csharp
using Iverson.Sql;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round 12 §7.3: the record of which tenants have a StarRocks login. The worker creates the table at
/// startup (<see cref="WorkerStoreBootstrap"/>) and inserts a row after each successful provisioning; every
/// StarRocks reader consults it through <see cref="PostgresProvisionedTenantDirectory"/>. It has no tenant
/// column, so no RLS: the api's login reads it through its one grant (<see cref="ApiLoginGrants"/>) and never
/// writes it.
/// </summary>
internal static class StarRocksTenantSchema
{
    public const string TableName = "IversonStarRocksTenants";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("TenantId", "text", false),
        new List<ColumnSchema>
        {
            new("ProvisionedAt", "timestamptz", false),
        });
}
```

**(f)** Create `Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs`:

```csharp
using System.Collections.Concurrent;
using Iverson.Sql;
using Iverson.StarRocks;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round 12 §7.3: <see cref="IProvisionedTenantDirectory"/> over <see cref="StarRocksTenantSchema"/>'s table.
/// The api reads it through its SELECT grant; the worker, the table's owner, also records into it.
/// </summary>
internal sealed class PostgresProvisionedTenantDirectory(IRecordStoreQueryExecutor sql) : IProvisionedTenantDirectory
{
    // Positive answers only. Provisioning never undoes itself, so a tenant once found stays provisioned for the
    // life of the process; an absent tenant is asked again every time, because the worker may record it at any
    // moment.
    private readonly ConcurrentDictionary<string, byte> _provisioned = new(StringComparer.Ordinal);

    public async Task<bool> IsProvisionedAsync(string tenantId)
    {
        if (_provisioned.ContainsKey(tenantId)) return true;

        var found = await sql.QuerySingleOrDefaultAsync<bool>(
            $"""SELECT EXISTS (SELECT 1 FROM "{StarRocksTenantSchema.TableName}" WHERE "TenantId" = @TenantId)""",
            new { TenantId = tenantId });
        if (found) _provisioned.TryAdd(tenantId, 0);
        return found;
    }

    public async Task RecordProvisionedAsync(string tenantId)
    {
        await sql.ExecuteAsync(
            $"""
            INSERT INTO "{StarRocksTenantSchema.TableName}" ("TenantId", "ProvisionedAt")
            VALUES (@TenantId, now())
            ON CONFLICT ("TenantId") DO NOTHING
            """,
            new { TenantId = tenantId });
        _provisioned.TryAdd(tenantId, 0);
    }
}
```

**(g)** `Iverson.Server/Iverson.Api/Program.cs`.

In `Iverson.Server/Iverson.Api/Program.cs`, replace:

```csharp
    starRocksProvisionerConnectionString = null;
}

builder.Services.AddStarRocks(
```

with:

```csharp
    starRocksProvisionerConnectionString = null;
}

// CSR round 12 §7.1: every StarRocks data connection logs in as the tenant's own user, whose password is
// derived from this key. Both roles derive it: the api to read, the worker to provision, write and aggregate.
var starRocksTenantLoginKey = cfg["StarRocks:TenantKey"];
if (string.IsNullOrWhiteSpace(starRocksTenantLoginKey))
{
    if (engagementStoreEnabledAtStartup)
        throw new InvalidOperationException(
            "StarRocks:TenantKey is required when Engagement:Enabled is true and was not configured.");
    starRocksTenantLoginKey = null;
}

builder.Services.AddStarRocks(
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:

```csharp
    provisionerConnectionString: starRocksProvisionerConnectionString);
```

with:

```csharp
    provisionerConnectionString: starRocksProvisionerConnectionString,
    tenantLoginKey: starRocksTenantLoginKey);
```

In `Iverson.Server/Iverson.Api/Program.cs`, replace:

```csharp
builder.Services.AddSingleton<ITenantRepository>(sp => new TenantRepository(
    Iverson.Api.Tenancy.TenantSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
```

with:

```csharp
builder.Services.AddSingleton<ITenantRepository>(sp => new TenantRepository(
    Iverson.Api.Tenancy.TenantSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
// CSR round 12 §7.3: which tenants have a StarRocks login. AddStarRocks hands it to EngagementRepository, whose
// readers consult it before connecting and whose provisioning (worker only) records into it.
builder.Services.AddSingleton<IProvisionedTenantDirectory>(sp =>
    new Iverson.Api.Tenancy.PostgresProvisionedTenantDirectory(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
```

In `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs`, replace:

```csharp
        await schemaManager.ApplySchemaAsync(TenantSchema.Table);
```

with:

```csharp
        await schemaManager.ApplySchemaAsync(TenantSchema.Table);

        // CSR round 12 §7.3: which tenants have a StarRocks login. Before the grants below, which name it.
        await schemaManager.ApplySchemaAsync(StarRocksTenantSchema.Table);
```

In `Iverson.Server/Iverson.Api/ApiLoginGrants.cs`, replace:

```csharp
        $"""GRANT UPDATE ("Status") ON "IversonPrivilegedRequests" TO {Login}""",
    ];
```

with:

```csharp
        $"""GRANT UPDATE ("Status") ON "IversonPrivilegedRequests" TO {Login}""",

        // Spec §7.3's provisioned-tenant check before every StarRocks read. Only the worker records.
        $"""GRANT SELECT ON "{StarRocksTenantSchema.TableName}" TO {Login}""",
    ];
```

In `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs`, replace:

```csharp
            "IversonPrivilegedRequests:INSERT", "IversonPrivilegedRequests:SELECT",
```

with:

```csharp
            "IversonPrivilegedRequests:INSERT", "IversonPrivilegedRequests:SELECT",
            "IversonStarRocksTenants:SELECT",
```

No test hosts the worker role, so the key guard has no unit test. The `WebApplicationFactory` hosts boot through it with the Development key below (Step 4's Api.Tests run), and Task 13's compose run boots the worker. The table and grant are proved by Task 9's `ApiLoginGrantsPostgresIntegrationTests` (exact privileges) and this task's `ProvisionedTenantDirectoryPostgresIntegrationTests`, both of which run `WorkerStoreBootstrap.RunAsync`.

**(h)** In `Iverson.Server/Iverson.Api/appsettings.Development.json`, replace:

```json
    "StarRocksProvisioner": "Server=localhost;Port=9030;User Id=root;Password=;"
  }
}
```

with:

```json
    "StarRocksProvisioner": "Server=localhost;Port=9030;User Id=root;Password=;"
  },
  "StarRocks": {
    "TenantKey": "dev-only-not-for-production-starrocks-tenant-key-0123456789"
  }
}
```

- [ ] **Step 4: Run the tests and see them pass**

Unit tests (light):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.EngagementRepositoryTenantLoginTests.|FullyQualifiedName~.TenantIdentifierTests.|FullyQualifiedName~.EngagementRepositoryLivenessRewrapTests.|FullyQualifiedName~.ServiceCollectionExtensionsTests.|FullyQualifiedName~.EngagementRepositoryTests."
```

Expected: 0 failed. `EngagementRepositoryTenantLoginTests` has 11 tests. The other four classes total `B_unit` + 8:
- `TenantIdentifierTests` +7: 1 + 3 theory cases + 1 + 1 + 1;
- `EngagementRepositoryLivenessRewrapTests` +0: −2 `Exempt` cases, +2 `FormerRoleArm` cases;
- `ServiceCollectionExtensionsTests` +1;
- `EngagementRepositoryTests` +0.

Every unit test in the StarRocks project (light; the filter excludes every Testcontainers class):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName!~IntegrationTests"
```

Expected: 0 failed.

The Api unit tests (light). They include every `WebApplicationFactory` host, which now boots through the key guard:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```

Expected: 0 failed. The run includes `PostgresProvisionedTenantDirectoryTests` (3) and the three new `ObjectSearchGrpcServiceTests` facts.

Integration tests (HEAVY; one StarRocks container shared by the four classes). Run nothing else alongside:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.StarRocksTenantLoginIntegrationTests.|FullyQualifiedName~.TenantIsolationIntegrationTests.|FullyQualifiedName~.MatchRowsIntegrationTests.|FullyQualifiedName~.StarRocksCredentialSplitIntegrationTests."
```

Expected: 0 failed.
- `StarRocksTenantLoginIntegrationTests`: 9 passed.
- The other three classes: `B_int` passed. The two re-pointed `MatchRowsIntegrationTests` replace the two they remove.

Postgres integration (HEAVY; two Postgres containers, one per class, run one after the other through `ContainerCollection`):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.ProvisionedTenantDirectoryPostgresIntegrationTests.|FullyQualifiedName~.ApiLoginGrantsPostgresIntegrationTests."
```

Expected: 0 failed. `ProvisionedTenantDirectoryPostgresIntegrationTests` has 3 tests. `ApiLoginGrantsPostgresIntegrationTests` keeps Task 9's count, and `ApiLogin_HoldsExactlyTheListedPrivileges` now includes `IversonStarRocksTenants:SELECT`.

Facts only a live engine confirms (spec Verified assumptions 24 and 26 probed them; these tests pin them):
- **`TenantLogin_CarriesTheSessionCapsTheAppLoginCarries`** reads `@@query_timeout` and `@@exec_mem_limit`. If either reads back differently while `SHOW PROPERTY FOR '<login>'` lists the caps, pin the observed read form and record it in the report. If the caps are missing from `SHOW PROPERTY`, STOP and report it: that is a defect in the `ALTER USER` statement.
- **`Session_state_a_read_leaves_behind_stays_inside_that_tenants_own_pool`.** Its first assertion is the premise (Verified assumption 26). If it fails with 300, the reset is on: check `ConnectionReset = false` in `TenantConnectionString`. If it fails with 180, the pool handed out a fresh connection. In either case STOP and report it; do not delete the assertion.
- **`TenantLogin_HasItsRoleActiveAtLogin_AndReadsItsOwnTable`** relies on `CURRENT_ROLE()` naming the default role. If the role is reported in another form, pin the observed form; the read in the same test is what proves the role is active.

- [ ] **Step 5: Mutation proof**

Before the first mutation, run Step 6's `git add …` command now (not the `git commit`), so `git checkout -- <file>` restores this task's staged version. Apply each mutation alone, run the named filter, confirm the named test FAILS, then revert with `git checkout -- <file>`. Record each output line in the report.

| # | File | Mutation | Run (filter) | Must fail |
|---|---|---|---|---|
| M1 | `EngagementRepository.cs` | inside `SearchAsync`'s body, delete its two lines `if (!await IsRecordedAsync(tenantId))` and `return [];` | `~.EngagementRepositoryTenantLoginTests.` (light) | `UnrecordedTenant_EveryReaderAnswersEmpty_WithoutConnecting` (gets `EngagementNotReadyException` after ~2 s) |
| M2 | `EngagementRepository.cs` | in `OpenOrSurfaceRefusalAsync`, `when (ex.ErrorCode == MySqlErrorCode.AccessDenied)` → `when (false)` | `~.EngagementRepositoryTenantLoginTests.\|~.EngagementRepositoryProvisioningTests.` (light) | `AccessDenied_OnOpeningATenantConnection_SurfacesAsNotReady`, `AccessDenied_OnOpen_SurfacesAsNotReady` |
| M3 | `EngagementRepository.cs` | move `await directory.RecordProvisionedAsync(tenantId);` to just above `await RunAsync(async () =>` in `EnsureTenantProvisionedAsync` | `~.EngagementRepositoryTenantLoginTests.` (light) | `ProvisioningThatFails_DoesNotRecordTheTenant` |
| M4 | `EngagementRepository.cs` | delete ` DEFAULT ROLE '{roleName}'` from the `CREATE USER` statement | `~.StarRocksTenantLoginIntegrationTests.` (HEAVY) | at least `TenantLogin_HasItsRoleActiveAtLogin_AndReadsItsOwnTable` and `WorkerWritesAndAggregates_RunUnderTheTenantLogin` (the tenant login has no role, so its reads and writes are denied) |
| M5 | `ObjectSearchGrpcService.cs` | in `Search`, change the `EngagementNotReadyException` catch's status from `StatusCode.Unavailable` to `StatusCode.Internal` (test-validity proof for the three characterisation facts) | `~.ObjectSearchGrpcServiceTests.` (light) | `Search_TenantLoginRefused_ThrowsUnavailable` |
| M6 | `EngagementRepository.cs` | in `TenantConnectionString`, delete the line `ConnectionReset       = false,` | `~.EngagementRepositoryTenantLoginTests.` (light), then `~.StarRocksTenantLoginIntegrationTests.\|~.MatchRowsIntegrationTests.` (HEAVY) | `TenantConnectionString_LogsInAsTheTenantsOwnUser_WithItsOwnPoolBounds`; then `TenantLogin_CarriesTheSessionCapsTheAppLoginCarries` and `Session_state_a_read_leaves_behind_stays_inside_that_tenants_own_pool` (both read 300) |

Command shape, with `<project>` and `<filter>` from the table (the `\|` in the table is `|`):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.EngagementRepositoryTenantLoginTests." 2>&1 | command grep -E '^\s+Failed |Passed!|Failed!'
```

For M5, use `Iverson.Api.Tests/Iverson.Api.Tests.csproj` and `--filter "FullyQualifiedName~.ObjectSearchGrpcServiceTests."`. Run M4, and M6's heavy filter, alone. After the last revert, check that the source is clean:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git diff HEAD --stat
```

Expected: only this task's files are listed, and the diff matches Step 3 plus Step 1. (M5 edits `ObjectSearchGrpcService.cs`, which this task does not change: revert it with `git checkout -- Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, and confirm it is absent from the stat.)

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add \
  Iverson.Server/Iverson.StarRocks/IProvisionedTenantDirectory.cs \
  Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs \
  Iverson.Server/Iverson.StarRocks/EngagementRepository.cs \
  Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs \
  Iverson.Server/Iverson.Api/Tenancy/StarRocksTenantSchema.cs \
  Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs \
  Iverson.Server/Iverson.Api/Program.cs \
  Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs \
  Iverson.Server/Iverson.Api/ApiLoginGrants.cs \
  Iverson.Server/Iverson.Api/appsettings.Development.json \
  Iverson.Server/Iverson.StarRocks.Tests/InMemoryProvisionedTenantDirectory.cs \
  Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryTenantLoginTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/ServiceCollectionExtensionsTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs \
  Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/Tenancy/PostgresProvisionedTenantDirectoryTests.cs \
  Iverson.Server/Iverson.Api.Tests/Tenancy/ProvisionedTenantDirectoryPostgresIntegrationTests.cs \
  Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs
git commit -m "use a per-tenant starrocks login for every data connection

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: StarRocks resilience under per-tenant pools

**Spec:** §7.2 (the Aggregate RPC's concurrency cap of 4, and pool exhaustion kept out of the transient classification that feeds the circuit breaker); §9 "pool exhaustion doesn't open the breaker"

**Decisions this task makes:**
- **The cap** is a `SemaphoreSlim(4)` around each spec's `RunAggregationAsync`. The specs are still converted up front, so a bad spec fails before any runs, as today. `Task.WhenAll` keeps the results in request order.
- **One classification feeds both the breaker and the retry** (`StarRocksResiliencePipelineFactory.cs:19, 37`). Excluding a full pool from it also stops the retry, which would only wait out the connect timeout twice more. Recorded in `D5-assumptions.md`.
- **A full pool is recognised** as MySqlConnector 2.6.2 raises it: `MySqlException(UnableToConnectToHost, "Connect Timeout expired. All pooled connections are in use.")`, from `MySqlConnection.CreateSessionAsync`, read by decompiling the package. A plain `"Connect Timeout expired."` with a free pool still counts.
- **The consumer classification is unchanged.** `TransientFailures.IsTransient` still treats a full pool as transient, so a projection consumer redelivers. Nothing here maps a full pool to a gRPC status; it surfaces as today's raw `MySqlException`. Recorded as an open item.

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:741-752`: the bounded fan-out in `Aggregate`.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs:19, 37, 39-40`: one predicate, `IsBackendTransient`, for both strategies.
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`: the cap and the order (after `Aggregate_WithMultipleSpecs_QueriesAllConcurrently`, `:760-805`).
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksResiliencePipelineFactoryTests.cs`: a full pool is neither retried nor counted; a connect timeout with a free pool still counts.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs` (Task 10's class): one tenant's full pool against a live engine.

**Interfaces:**
- **Consumes:** Task 10's `EngagementRepository.TenantPoolSize`, its tenant connection strings (which inherit `ConnectionTimeout` from the base string), `InMemoryProvisionedTenantDirectory`, and the `StarRocksTenantLoginIntegrationTests` class. This test is appended after Task 10's last test.
- **Shared files:** `ObjectSearchGrpcService.cs` (Task 4 edits ranking, the response builder and decay; Task 10 edits nothing here). This task touches only `Aggregate`'s fan-out. `ObjectSearchGrpcServiceTests.cs` (Tasks 4 and 10): this task inserts after `Aggregate_WithMultipleSpecs_QueriesAllConcurrently`, which no other task touches.
- **Ordering:** after Task 10.

- [ ] **Step 1: Write the failing tests**

**(a)** In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, replace:

```csharp
        callCount.Should().Be(3);
        response.Results.Should().HaveCount(3);
    }
```

with:

```csharp
        callCount.Should().Be(3);
        response.Results.Should().HaveCount(3);
    }

    // CSR round 12 §7.2: every spec of one request runs as the same tenant, whose pool holds ten connections
    // per process, so at most four run at once. Each stub call holds its slot for 50 ms, so with no cap all
    // ten would overlap.
    [Fact]
    public async Task Aggregate_WithMoreSpecsThanTheCap_RunsAtMostFourAtOnce_AndKeepsTheirOrder()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var running = 0;
        var peak = 0;
        var peakLock = new object();

        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(call => RunOneAsync(call.ArgAt<AggregationDescriptor>(2)));

        var request = new AggregateRequest { TypeName = "Author" };
        for (var i = 0; i < 10; i++)
            request.Aggregations.Add(new AggregationSpec { Name = $"a{i}", Field = "Name", Type = AggregationType.Terms });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        peak.Should().Be(4);
        response.Results.Select(r => r.Name).Should().Equal(Enumerable.Range(0, 10).Select(i => $"a{i}"));

        async Task<SrAggResult?> RunOneAsync(AggregationDescriptor spec)
        {
            var now = System.Threading.Interlocked.Increment(ref running);
            lock (peakLock) peak = Math.Max(peak, now);
            await Task.Delay(50);
            System.Threading.Interlocked.Decrement(ref running);
            return new SrAggResult(spec.Name, AggregationKind.Terms);
        }
    }
```

**(b)** In `Iverson.Server/Iverson.StarRocks.Tests/StarRocksResiliencePipelineFactoryTests.cs`, replace:

```csharp
        attempts.Should().Be(2, "exceptions that aren't MySqlException must never be retried or trip the breaker");
    }
}
```

with:

```csharp
        attempts.Should().Be(2, "exceptions that aren't MySqlException must never be retried or trip the breaker");
    }

    // CSR round 12 §7.2: MySqlConnector 2.6.2 reports a full pool as UnableToConnectToHost (1042, which it marks
    // transient) with this message (MySqlConnection.CreateSessionAsync). One tenant's pool filling up says
    // nothing about StarRocks, so the process's one breaker must not count it, and a retry would only wait out
    // the connect timeout again.
    private static MySqlException PoolExhaustedException() =>
        CreateMySqlException(MySqlErrorCode.UnableToConnectToHost, "Connect Timeout expired. All pooled connections are in use.");

    [Fact]
    public async Task Build_DoesNotRetryOrTripBreaker_OnPoolExhaustion()
    {
        var pipeline = StarRocksResiliencePipelineFactory.Build(FastTestOptions(), NullLogger.Instance);
        var attempts = 0;

        Func<Task> call = () => pipeline.ExecuteAsync<int>(async _ =>
        {
            attempts++;
            throw PoolExhaustedException();
        }).AsTask();

        await Assert.ThrowsAsync<MySqlException>(call);
        await Assert.ThrowsAsync<MySqlException>(call);
        await Assert.ThrowsAsync<MySqlException>(call);

        attempts.Should().Be(3, "a full pool must never be retried");

        await Assert.ThrowsAsync<MySqlException>(call);
        attempts.Should().Be(4, "the circuit must never open on a full pool");
    }

    [Fact]
    public async Task Build_StillTripsBreaker_OnAConnectTimeoutWithAFreePool()
    {
        // Long durations, as in Build_OpensCircuit_AfterFailureThreshold_AndFailsFastWithoutInvokingOperation:
        // this test never waits for the break to elapse.
        var pipeline = StarRocksResiliencePipelineFactory.Build(
            new EngagementCircuitBreakerOptions
            {
                FailureRatio      = 1.0,
                MinimumThroughput = 2,
                SamplingDuration  = TimeSpan.FromMinutes(1),
                BreakDuration     = TimeSpan.FromMinutes(1),
            },
            NullLogger.Instance);

        Func<Task> call = () => pipeline.ExecuteAsync<int>(async _ =>
        {
            throw CreateMySqlException(MySqlErrorCode.UnableToConnectToHost, "Connect Timeout expired.");
        }).AsTask();

        await Assert.ThrowsAsync<MySqlException>(call);
        await Assert.ThrowsAsync<MySqlException>(call);
        await Assert.ThrowsAsync<BrokenCircuitException>(call);
    }
}
```

**(c)** In `Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs`, replace:

```csharp
        count.Should().NotBeNull();
        count!.MetricValue.Should().Be(1);
    }
}
```

with:

```csharp
        count.Should().NotBeNull();
        count!.MetricValue.Should().Be(1);
    }

    // ── one tenant's full pool (CSR round 12 §7.2) ──────────────────────────────────────────────

    // A full pool says nothing about StarRocks' health, so it must not count toward the process's one circuit
    // breaker, or one busy tenant would fail every tenant fast. Streams parked mid-read hold tenant A's whole
    // pool; A's next reads wait out the 2 s connect timeout and fail, and the same repository still serves B.
    [Fact]
    public async Task OneTenantsFullPool_FailsOnlyThatTenant_AndNeverOpensTheBreaker()
    {
        var repo = new EngagementRepository(
            new MySqlConnectionStringBuilder(_appConnectionString) { ConnectionTimeout = 2 }.ToString(),
            NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions
            {
                CircuitBreaker = new EngagementCircuitBreakerOptions
                {
                    FailureRatio      = 0.5,
                    MinimumThroughput = 2,
                    SamplingDuration  = TimeSpan.FromMinutes(1),
                    BreakDuration     = TimeSpan.FromMinutes(1),
                },
            },
            provisionerConnectionString: _provisionerConnectionString,
            tenantLoginKey: StarRocksSplitLogins.TenantLoginKey,
            provisionedTenants: _directory);
        var (tenantA, tableA) = await ProvisionWithRowAsync("A");
        var (tenantB, tableB) = await ProvisionWithRowAsync("B");
        var oneRow = new MatchRowsRequest(
            [], SearchLogic.And, [], [new SearchSort { Property = "Name" }], [], [], true, [], 100);

        var held = new List<IAsyncEnumerator<IDictionary<string, object?>>>();
        try
        {
            for (var i = 0; i < EngagementRepository.TenantPoolSize; i++)
            {
                var stream = repo.MatchRowsAsync(ProbeQuerySchema(tableA), oneRow, AuthzFor(tenantA)).GetAsyncEnumerator();
                held.Add(stream);
                (await stream.MoveNextAsync()).Should().BeTrue();
            }

            for (var i = 0; i < 3; i++)
            {
                var act = () => repo.SearchAsync(ProbeQuerySchema(tableA), null, 0, 50, authz: AuthzFor(tenantA));
                await act.Should().ThrowAsync<MySqlException>().WithMessage("*All pooled connections are in use*");
            }

            (await repo.SearchAsync(ProbeQuerySchema(tableB), null, 0, 50, authz: AuthzFor(tenantB)))
                .Should().ContainSingle();
        }
        finally
        {
            foreach (var stream in held)
                await stream.DisposeAsync();
        }
    }
}
```

- [ ] **Step 2: Run them and see them fail**

Light:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.StarRocksResiliencePipelineFactoryTests." 2>&1 | command grep -E '^\s+Failed |Passed!|Failed!'
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.ObjectSearchGrpcServiceTests.Aggregate_" 2>&1 | command grep -E '^\s+Failed |Passed!|Failed!'
```

Expected:
- `Build_DoesNotRetryOrTripBreaker_OnPoolExhaustion` fails. The third call meets an open breaker (`BrokenCircuitException`), after 3 attempts per call. `Build_StillTripsBreaker_OnAConnectTimeoutWithAFreePool` passes: today's predicate already counts it.
- `Aggregate_WithMoreSpecsThanTheCap_RunsAtMostFourAtOnce_AndKeepsTheirOrder` fails on `peak`: expected 4, found 10. Every other `Aggregate_` test passes.

HEAVY:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.StarRocksTenantLoginIntegrationTests.OneTenantsFullPool_" 2>&1 | command grep -E '^\s+Failed |Passed!|Failed!'
```

Expected: `OneTenantsFullPool_FailsOnlyThatTenant_AndNeverOpensTheBreaker` fails. The third read of A throws `EngagementNotReadyException` ("circuit breaker open") instead of `MySqlException`.

- [ ] **Step 3: Implement**

**(a)** In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`, replace:

```csharp
        var aggTasks = request.Aggregations
            .Select(spec => RunAggregationAsync(
                schema,
                request.Query,
                ProtoToEngagementSpec(spec),
                having,
                request.Joins,
                auth.Constraints))
            .ToList();

        var aggResults = await Task.WhenAll(aggTasks);
```

with:

```csharp
        // CSR round 12 §7.2: at most four specs run at once. Every spec of a request runs as the same tenant,
        // whose pool holds ten connections per process, so an unbounded fan-out could take the whole pool. The
        // specs are converted up front, as before, so a bad one fails before any runs; Task.WhenAll keeps the
        // results in request order.
        const int maxConcurrentAggregations = 4;
        var specs = request.Aggregations.Select(ProtoToEngagementSpec).ToList();
        using var aggregationSlots = new SemaphoreSlim(maxConcurrentAggregations);
        var aggTasks = specs
            .Select(async spec =>
            {
                await aggregationSlots.WaitAsync();
                try
                {
                    return await RunAggregationAsync(
                        schema,
                        request.Query,
                        spec,
                        having,
                        request.Joins,
                        auth.Constraints);
                }
                finally
                {
                    aggregationSlots.Release();
                }
            })
            .ToList();

        var aggResults = await Task.WhenAll(aggTasks);
```

**(b)** In `Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs`, replace:

```csharp
                ShouldHandle      = new PredicateBuilder().Handle<MySqlException>(ex => ex.IsTransient),
```

with:

```csharp
                ShouldHandle      = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient),
```

In `Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs`, replace:

```csharp
                ShouldHandle     = new PredicateBuilder().Handle<MySqlException>(ex => ex.IsTransient)
            })
            .Build();
}
```

with:

```csharp
                ShouldHandle     = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient)
            })
            .Build();

    /// <summary>
    /// The failures the breaker counts and the retry repeats: MySqlConnector's transient set, less a full
    /// connection pool (CSR round 12 §7.2). Pools are per tenant, so a full one says only that this tenant is
    /// busy in this process. Counting it would let one tenant open the one breaker every tenant shares, and
    /// retrying it would only wait out the connect timeout again.
    /// </summary>
    internal static bool IsBackendTransient(MySqlException ex) => ex.IsTransient && !IsPoolExhausted(ex);

    // MySqlConnector 2.6.2 raises a full pool as UnableToConnectToHost with "Connect Timeout expired. All
    // pooled connections are in use." (MySqlConnection.CreateSessionAsync). The same code without that suffix
    // is a real connect timeout, which still counts.
    private static bool IsPoolExhausted(MySqlException ex) =>
        ex.ErrorCode == MySqlErrorCode.UnableToConnectToHost &&
        ex.Message.Contains("All pooled connections are in use", StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run the tests and see them pass**

Light:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName!~IntegrationTests"
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~.ObjectSearchGrpcServiceTests."
```

Expected: 0 failed in both runs. `StarRocksResiliencePipelineFactoryTests` has 7 tests: 5 existing and 2 new.

HEAVY (run alone):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.StarRocksTenantLoginIntegrationTests."
```

Expected: 10 passed (Task 10's 9 plus `OneTenantsFullPool_FailsOnlyThatTenant_AndNeverOpensTheBreaker`).

- [ ] **Step 5: Mutation proof**

As in Task 10: stage first (Step 6's `git add`), then apply each mutation alone, run, confirm the named test FAILS, and revert with `git checkout -- <file>`.

| # | File | Mutation | Run (filter) | Must fail |
|---|---|---|---|---|
| M1 | `ObjectSearchGrpcService.cs` | `const int maxConcurrentAggregations = 4;` → `= 10;` | `~.ObjectSearchGrpcServiceTests.Aggregate_` (light, Api.Tests) | `Aggregate_WithMoreSpecsThanTheCap_RunsAtMostFourAtOnce_AndKeepsTheirOrder` |
| M2 | `StarRocksResiliencePipelineFactory.cs` | `ex.IsTransient && !IsPoolExhausted(ex)` → `ex.IsTransient` | `~.StarRocksResiliencePipelineFactoryTests.` (light) | `Build_DoesNotRetryOrTripBreaker_OnPoolExhaustion` |
| M3 | `StarRocksResiliencePipelineFactory.cs` | `"All pooled connections are in use"` → `"Connect Timeout expired"` | `~.StarRocksResiliencePipelineFactoryTests.` (light) | `Build_StillTripsBreaker_OnAConnectTimeoutWithAFreePool` |
| M4 | `StarRocksResiliencePipelineFactory.cs` | the retry's `Handle<MySqlException>(IsBackendTransient)` → `Handle<MySqlException>(ex => ex.IsTransient)` (the breaker keeps the new predicate) | `~.StarRocksResiliencePipelineFactoryTests.` (light) | `Build_DoesNotRetryOrTripBreaker_OnPoolExhaustion` (`attempts` is 9, not 3) |

Command shape:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server && export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~.StarRocksResiliencePipelineFactoryTests." 2>&1 | command grep -E '^\s+Failed |Passed!|Failed!'
```

For M1, use `Iverson.Api.Tests/Iverson.Api.Tests.csproj` and `--filter "FullyQualifiedName~.ObjectSearchGrpcServiceTests.Aggregate_"`. The integration test is the live counterpart of M2 and needs no separate mutation run. After the last revert:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git diff HEAD --stat
```

Expected: only this task's five files.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && git add \
  Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
  Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksResiliencePipelineFactoryTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs
git commit -m "cap aggregate fan-out and keep a full tenant pool out of the starrocks breaker

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 12: Deployment: the api's Postgres login, the tenant key, the role revoke and compose order

**Spec:** §6.1, §6.2 (the CONNECT row), §6.3 (the seed moves to the worker), §7.1 (the tenant key), §7.5, §8 (closes #13's deploy side)

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/secret-api.yaml`. The `<release>-postgres-api` Secret (basic-auth, lookup-or-generate, `keep`), shaped like the Authentik chart's Postgres Secret.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml`
  - `:7`: the `enableSuperuserAccess` comment;
  - `:27-28`, `:38`: "the API's startup" becomes "the worker's startup";
  - `:48-54`: the managed role `iverson_api` (`login`, `inRoles: [iverson_runtime]`, `passwordSecret`).
- Create: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-tenant-key.yaml`. The `<release>-starrocks-tenant-key` Secret (Opaque, key `tenant-key`, lookup-or-generate, `keep`). It renders only where StarRocks does: the umbrella chart gates the subchart on `global.engagementEnabled`.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml`
  - `:117`: the comment that says the provisioner grants iverson_app its tenant roles;
  - after `:138`, the end of the script: read `SHOW GRANTS FOR 'iverson_app'@'%'` and revoke every role it lists.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`
  - `:84-94`: `POSTGRES_API_PASSWORD` from `<release>-postgres-api`, and `Username=iverson_api`; `<release>-postgres-app` goes;
  - `:102-107`: `StarRocks__TenantKey` inside the engagement block; `Tenancy__SeedLegacyTenants` goes.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml:93-97`: `StarRocks__TenantKey` inside the engagement block, and `Tenancy__SeedLegacyTenants` after `Engagement__Enabled`.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/job-revoke-cross-db.yaml`
  - `:65-67`: also wait for the `iverson_api` role;
  - `:80-83`: the comment naming the logins that keep CONNECT;
  - `:89-90`: also `GRANT CONNECT ON DATABASE iverson TO iverson_api`.
- Modify: `Iverson.Server/docker-compose.yml`
  - `:7`: the security note's credential list;
  - `:92-94`: the `starrocks-init` comment;
  - `:115-117`: the role revoke after the `starrocks-init` batch;
  - `:479-480`: the api's `ConnectionStrings__Postgres` as `iverson_api`;
  - `:497-501`: the api's StarRocks comment, and `StarRocks__TenantKey`;
  - `:526-527`: the api's `Tenancy__SeedLegacyTenants` goes;
  - `:541-546`: the api healthcheck comment;
  - `:579-582`: the api `depends_on` the worker being healthy;
  - `:607-608`: the worker's `Postgres__ApiLoginPassword`;
  - `:614-620`: the worker's StarRocks comment, and `StarRocks__TenantKey`;
  - `:643-644`: the worker healthcheck (`GET /health` on 8081, HTTP 200);
  - `:667-684`: the worker no longer `depends_on` the api.
- Modify: `scripts/generate-compose-secrets.sh:27-28`: emit `IVERSON_STARROCKS_TENANT_KEY`.
- Modify: `Iverson.Server/Iverson.Api/appsettings.Development.json`: `Database=iverson;` leaves the Development StarRocks string. Task 10 already added the dev `StarRocks:TenantKey`.
- Create: `docs/runbooks/csr12-server.md`. The upgrade note covers:
  - the new login, what the worker grants, what the hooks revoke and grant, and how to check;
  - the tenant key, which must never change, and the by-hand re-key if it did;
  - the `stack.py query` tier's need for a volume a full stack has run on;
  - the release notes (the size guard, and registration needing a worker).
- Test (scratch, not committed):
  - `$SCR/check_t12.py`, the per-overlay render checks;
  - `$SCR/check_t12_compose.py`, the compose, secrets-script and Development checks, and the job-versus-`starrocks-init` revoke parity;
  - `$SCR/t12_job.sh`, which runs the rendered create-user script against a stub `mysql` with three `SHOW GRANTS` fixtures;
  - `$SCR/t12_render.sh`, `$SCR/t12_ci.sh` and `$SCR/t12_mutate.sh`, the drivers.

**Interfaces:**
- Consumes from Task 9 (D4), the Postgres split:
  - the worker grants CONNECT and every §6.2 table grant to `iverson_api` at startup, after its plumbing DDL;
  - startup DDL and the legacy-tenant seed (`Tenancy:SeedLegacyTenants`) run only in the worker role;
  - the api's `/health` stays not-ready until `has_table_privilege('iverson_api', '"IversonReconciliationQueue"', 'INSERT')` holds and the registry has loaded;
  - **compose only:** when `iverson_api` does not exist and the worker's login can create roles (compose's `iverson` is the superuser), the worker runs `CREATE ROLE iverson_api LOGIN PASSWORD <p> IN ROLE iverson_runtime`, with `<p>` read from the config key **`Postgres:ApiLoginPassword`** (env `Postgres__ApiLoginPassword`). On Helm the key is unset and CNPG creates the role. **The assembler must reconcile this key name with D4's Task 9.** If D4 chose another name, change the two `Postgres__ApiLoginPassword` occurrences in this task (the compose worker env and `check_t12_compose.py`).
- Consumes from Task 10 (D5), the per-tenant StarRocks logins:
  - the master key is read from **`StarRocks:TenantKey`** (env `StarRocks__TenantKey`) in both roles, as an opaque non-empty string whose UTF-8 bytes are the HMAC key. It is required only when `Engagement:Enabled` is true, as `ConnectionStrings:StarRocks` is (`Program.cs:283-288`), because the chart renders it only where StarRocks renders (laptop has none). **The assembler must reconcile this key name and its required-when rule with D5's Task 10.** The chart's key is 64 alphanumerics, and compose's is 64 hex digits from `openssl rand -hex 32`. Neither may be hex-decoded.
  - `EnsureTenantProvisionedAsync` no longer runs `GRANT <role> TO USER 'iverson_app'@'%'`, so nothing re-grants what this task's revoke removes.
  - the per-tenant connection string is built from `ConnectionStrings:StarRocks` by replacing `UserID` and `Password`. That is why the Development string loses `Database=iverson`: a tenant login has no grant on `iverson`.
- Produces for Task 13:
  - the rendered create-user script, which reads `ROOT_PW`, `APP_PW` and `PROVISIONER_PW`;
  - the compose project's ordering: `starrocks-init`, then `iverson-worker` healthy, then `iverson-api`;
  - the generated `IVERSON_STARROCKS_TENANT_KEY`.
- **Ordering.** Run this task after Tasks 9 and 10. It changes no code, but compose's worker-first order and the api's `iverson_api` login only start once those tasks' code is in. No other task edits these files.

Rules for these steps:
- **Helm.** `t12_render.sh` runs `helm dependency build` before rendering, because a stale `charts/*.tgz` renders old subchart content. Each cloud overlay renders with its `*.ci-override.yaml`, as `.github/workflows/deploy-validate.yml` does.
- **No live stack.** Nothing in this task starts a container or a cluster. Task 13 runs the rendered job against a throwaway StarRocks, and runs the isolated compose project and kind.
- **Scratch only.** The check drivers write only under `$SCR`. The mutation proof edits copies under `$SCR/t12-mut`, never the worktree.

- [ ] **Step 1: Write the failing checks**

Create the six scratch files. Each heredoc delimiter is unique, and none appears in the content.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
mkdir -p $SCR
cat > $SCR/check_t12.py <<'T12CHECK'
"""CSR round 12 §6.1, §7.1, §7.5 and §8 render checks: python3 check_t12.py <overlay> < render.yaml"""
import base64, sys, yaml
name = sys.argv[1]
docs = [d for d in yaml.safe_load_all(sys.stdin) if d]
by = {(d["kind"], d["metadata"]["name"]): d for d in docs}
fails = []
def check(cond, msg):
    if not cond: fails.append(msg)

def containers(kind, n):
    d = by.get((kind, n))
    return [] if d is None else d["spec"]["template"]["spec"]["containers"]

def env_of(kind, n):
    return [e for c in containers(kind, n) for e in c.get("env", [])]

val = lambda env, k: next((e.get("value") for e in env if e["name"] == k), None)
ref = lambda env, k: next((e.get("valueFrom", {}).get("secretKeyRef") for e in env if e["name"] == k), None)
names = lambda env: [e["name"] for e in env]

api, worker = env_of("Deployment", "iverson-api"), env_of("Deployment", "iverson-worker")
check(bool(api) and bool(worker), "api or worker Deployment missing")

# §6.1: the api's login is iverson_api, from its own Secret; nothing in the api pod names the owner's Secret.
check(ref(api, "POSTGRES_API_PASSWORD") == {"name": "iverson-postgres-api", "key": "password"},
      "api POSTGRES_API_PASSWORD secretKeyRef wrong")
pg = val(api, "ConnectionStrings__Postgres") or ""
check("Username=iverson_api;Password=$(POSTGRES_API_PASSWORD);" in pg, f"api ConnectionStrings__Postgres not iverson_api: {pg}")
check("postgres-app" not in yaml.safe_dump(by.get(("Deployment", "iverson-api"))), "api Deployment still references <release>-postgres-app")
check("Username=iverson;Password=$(POSTGRES_APP_PASSWORD);" in (val(worker, "ConnectionStrings__Postgres") or ""),
      "worker ConnectionStrings__Postgres is no longer the owner")

sec = by.get(("Secret", "iverson-postgres-api"))
check(sec is not None, "Secret iverson-postgres-api missing")
if sec:
    check(sec.get("type") == "kubernetes.io/basic-auth", "iverson-postgres-api is not basic-auth")
    check((sec["metadata"].get("annotations") or {}).get("helm.sh/resource-policy") == "keep", "iverson-postgres-api lacks keep")
    check(base64.b64decode(sec.get("data", {}).get("username", "")) == b"iverson_api", "iverson-postgres-api username is not iverson_api")
    check(bool(sec.get("data", {}).get("password")), "iverson-postgres-api password empty")

cl = by.get(("Cluster", "iverson-postgres"))
roles = {r["name"]: r for r in ((cl or {}).get("spec", {}).get("managed", {}).get("roles") or [])}
r = roles.get("iverson_api")
check(r is not None, "managed role iverson_api missing")
if r:
    check(r == {"name": "iverson_api", "ensure": "present", "login": True, "inRoles": ["iverson_runtime"],
                "passwordSecret": {"name": "iverson-postgres-api"}}, f"managed role iverson_api wrong: {r}")
check("authentik" in roles, "managed role authentik lost")

# §6.3 / §8: the legacy-tenant seed moves to the worker.
check("Tenancy__SeedLegacyTenants" not in names(api), "api still sets Tenancy__SeedLegacyTenants")
want_seed = "true" if name in ("values-local", "values-laptop") else "false"
check(val(worker, "Tenancy__SeedLegacyTenants") == want_seed,
      f"worker Tenancy__SeedLegacyTenants != {want_seed}: {val(worker, 'Tenancy__SeedLegacyTenants')}")

# §8 belt-and-braces: the Authentik hook waits for iverson_api, then grants it CONNECT.
hook = containers("Job", "iverson-authentik-revoke-cross-db")
check(bool(hook), "revoke-cross-db Job missing")
if hook:
    s = hook[0]["args"][0]
    check("rolname='iverson_api'" in s, "revoke-cross-db does not wait for iverson_api")
    check('-c "GRANT CONNECT ON DATABASE iverson TO iverson_api;"' in s, "revoke-cross-db does not grant CONNECT to iverson_api")
    check("rolname='iverson_api'" not in s or "TO iverson_api;" not in s
          or s.index("rolname='iverson_api'") < s.index("TO iverson_api;"), "revoke-cross-db grants before it waits")

# §7.1: the tenant key is its own Secret, in both Deployments, only where StarRocks renders.
engagement = name != "values-laptop"
key = by.get(("Secret", "iverson-starrocks-tenant-key"))
want_ref = {"name": "iverson-starrocks-tenant-key", "key": "tenant-key"}
if not engagement:
    check(key is None and "StarRocks__TenantKey" not in names(api) + names(worker), "laptop renders the tenant key")
else:
    check(key is not None, "Secret iverson-starrocks-tenant-key missing")
    if key:
        check(key.get("type") == "Opaque", "tenant key Secret is not Opaque")
        check((key["metadata"].get("annotations") or {}).get("helm.sh/resource-policy") == "keep", "tenant key Secret lacks keep")
        check(list(key.get("data", {})) == ["tenant-key"] and bool(key["data"]["tenant-key"]), "tenant key Secret data wrong")
    check(ref(api, "StarRocks__TenantKey") == want_ref, "api StarRocks__TenantKey secretKeyRef wrong")
    check(ref(worker, "StarRocks__TenantKey") == want_ref, "worker StarRocks__TenantKey secretKeyRef wrong")
    check(all("provisioner" not in str(e).lower() for e in api), "api env mentions the provisioner")
    # §7.5: the create-user hook revokes every role SHOW GRANTS lists for iverson_app, after the login batch.
    job = containers("Job", "iverson-starrocks-create-user")
    check(bool(job), "create-user Job missing")
    if job:
        s = job[0]["args"][0]
        check("SHOW GRANTS FOR 'iverson_app'@'%';" in s, "create-user Job does not read iverson_app's grants")
        check("fe -e \"REVOKE $ROLE FROM 'iverson_app'@'%';\"" in s, "create-user Job does not revoke the roles it reads")
        check("SHOW GRANTS" not in s or s.index("SHOW GRANTS") > s.index("ALTER USER 'iverson_app' SET PROPERTIES"),
              "the role revoke runs before the login batch")

print(f"[{name}] {'PASS' if not fails else 'FAIL'}", *fails, sep="\n  ")
sys.exit(bool(fails))
T12CHECK
cat > $SCR/check_t12_compose.py <<'T12COMPOSE'
"""CSR round 12 §7.1, §7.5 and §8 compose checks: python3 check_t12_compose.py <repo root>"""
import json, re, sys, yaml
root = sys.argv[1]
S = f"{root}/Iverson.Server"
comp = yaml.safe_load(open(f"{S}/docker-compose.yml"))["services"]
fails = []
def check(cond, msg):
    if not cond: fails.append(msg)

def env(svc):
    return dict(e.split("=", 1) if "=" in e else (e, None) for e in comp[svc]["environment"])

api, wk = env("iverson-api"), env("iverson-worker")
m = re.fullmatch(r"Host=postgres;Port=5432;Database=iverson;Username=iverson_api;Password=(\S+)", api.get("ConnectionStrings__Postgres") or "")
check(m is not None, f"api ConnectionStrings__Postgres not iverson_api: {api.get('ConnectionStrings__Postgres')}")
check(m is not None and wk.get("Postgres__ApiLoginPassword") == m.group(1), "worker Postgres__ApiLoginPassword does not match the api's password")
check(wk.get("ConnectionStrings__Postgres") == "Host=postgres;Port=5432;Database=iverson;Username=iverson;Password=iverson",
      "worker ConnectionStrings__Postgres is no longer the owner")
key = "${IVERSON_STARROCKS_TENANT_KEY:?run scripts/generate-compose-secrets.sh first}"
check(api.get("StarRocks__TenantKey") == key, "api StarRocks__TenantKey missing or wrong")
check(wk.get("StarRocks__TenantKey") == key, "worker StarRocks__TenantKey missing or wrong")
check("Tenancy__SeedLegacyTenants" not in api, "api still sets Tenancy__SeedLegacyTenants")
check(wk.get("Tenancy__SeedLegacyTenants") == "true", "worker lost Tenancy__SeedLegacyTenants")
check(comp["iverson-api"]["depends_on"].get("iverson-worker") == {"condition": "service_healthy"}, "api does not wait for a healthy worker")
check("iverson-api" not in comp["iverson-worker"]["depends_on"], "worker still waits for the api")
hc = comp["iverson-worker"].get("healthcheck", {}).get("test", [])
check(len(hc) == 4 and hc[:3] == ["CMD", "bash", "-c"] and "/dev/tcp/127.0.0.1/8081" in hc[3]
      and "GET /health HTTP/1.1" in hc[3] and "grep -q ' 200 '" in hc[3], f"worker healthcheck is not a /health 200 check: {hc}")

# §7.5 parity: starrocks-init runs the chart job's revoke word for word ($$ is compose's escaped $).
job = open(f"{S}/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml").read()
init = comp["starrocks-init"]["command"][0].replace("$$", "$")
def block(s):
    ls = [l.strip() for l in s.splitlines()]
    i = next((k for k, l in enumerate(ls) if l.startswith("APP_GRANTS=")), None)
    return None if i is None or "done" not in ls[i:] else ls[i:ls.index("done", i) + 1]
check(block(job) is not None and block(job) == block(init), f"revoke differs\n   job={block(job)}\n  init={block(init)}")
check('fe() { mysql -h starrocks -P 9030 -u root "$@"; }' in init, "starrocks-init defines no fe()")

gen = open(f"{root}/scripts/generate-compose-secrets.sh").read()
check(re.search(r"^\s+IVERSON_STARROCKS_TENANT_KEY$", gen, re.M) is not None, "generate-compose-secrets.sh does not emit IVERSON_STARROCKS_TENANT_KEY")
dev = json.load(open(f"{S}/Iverson.Api/appsettings.Development.json"))
check(bool((dev.get("StarRocks") or {}).get("TenantKey")), "appsettings.Development.json has no StarRocks:TenantKey")
check("Database=" not in dev["ConnectionStrings"]["StarRocks"], "the Development StarRocks string still names a default database")

print(f"[compose] {'PASS' if not fails else 'FAIL'}", *fails, sep="\n  ")
sys.exit(bool(fails))
T12COMPOSE
cat > $SCR/t12_job.sh <<'T12JOB'
#!/usr/bin/env bash
# Runs the create-user script rendered in $1 (a values-local render) under sh, with a stub mysql on PATH, against three
# SHOW GRANTS fixtures. Prints one PASS or FAIL line per fixture. Usage: t12_job.sh <render.yaml>
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
D=$SCR/t12-job; rm -rf $D; mkdir -p $D/bin
python3 - "$1" > $D/job.sh <<'T12EXTRACT'
import sys, yaml
d = next(d for d in yaml.safe_load_all(open(sys.argv[1])) if d and d["kind"] == "Job" and d["metadata"]["name"] == "iverson-starrocks-create-user")
print(d["spec"]["template"]["spec"]["containers"][0]["args"][0], end="")
T12EXTRACT
cat > $D/bin/mysql <<'T12STUB'
#!/bin/sh
# Records every -e statement, answers the job's FE and BE probes, and prints $STUB_GRANTS for SHOW GRANTS.
stmt=""
while [ $# -gt 0 ]; do case "$1" in -e) stmt="$2"; shift 2 ;; *) shift ;; esac; done
printf '%s\n' "$stmt" >> "$STUB_LOG"
case "$stmt" in
  "SHOW BACKENDS;") printf 'be-0\n' ;;
  *"SHOW GRANTS FOR"*) cat "$STUB_GRANTS" ;;
esac
exit 0
T12STUB
chmod +x $D/bin/mysql
T=$(printf '\t')
U="'iverson_app'@'%'"
printf '%s\n' "$U${T}NULL${T}GRANT 'role_tenant_tenant_smoke_test_0123456789abcdefghijklmno', 'role_tenant_tenant_smoke_test', 'public' TO $U" \
              "$U${T}default_catalog${T}GRANT OPERATE ON SYSTEM TO USER $U" > $D/roles.txt
printf '%s\n' "$U${T}default_catalog${T}GRANT OPERATE ON SYSTEM TO USER $U" > $D/none.txt
printf '%s\n' "$U${T}NULL${T}GRANT 'role-with-hyphen' TO $U" > $D/badname.txt
want_roles="REVOKE role_tenant_tenant_smoke_test_0123456789abcdefghijklmno FROM $U;|REVOKE role_tenant_tenant_smoke_test FROM $U;|"
for c in roles none badname; do
  : > $D/$c.log
  STUB_LOG=$D/$c.log STUB_GRANTS=$D/$c.txt ROOT_PW=r APP_PW=a PROVISIONER_PW=p PATH=$D/bin:$PATH sh $D/job.sh > $D/$c.out 2>&1
  rc=$?
  got="$(command grep -E '^REVOKE [^ ]+ FROM ' $D/$c.log | tr '\n' '|')"
  case $c in
    roles)   [ $rc = 0 ] && [ "$got" = "$want_roles" ] && ok=PASS || ok=FAIL ;;
    none)    [ $rc = 0 ] && [ -z "$got" ] && command grep -q "SHOW GRANTS FOR" $D/$c.log && ok=PASS || ok=FAIL ;;
    badname) [ $rc != 0 ] && [ -z "$got" ] && command grep -q "unexpected role name granted to iverson_app: role-with-hyphen" $D/$c.out && ok=PASS || ok=FAIL ;;
  esac
  echo "[job:$c] $ok exit=$rc revokes=[$got]"
done
T12JOB
cat > $SCR/t12_render.sh <<'T12RENDER'
#!/usr/bin/env bash
# Renders the chart in $2 (default: the worktree's) for each overlay named after it (default: all five) into
# $SCR/t12/<label>-<overlay>.yaml and prints check_t12.py's verdict for each. Usage: t12_render.sh <label> [chart] [overlay...]
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
L=$1; CH=${2:-$WT/Iverson.Server/deploy/helm/iverson}; shift; [ $# -gt 0 ] && shift
OV="${*:-values-local values-laptop values-aws values-azure values-gcp}"
O=$SCR/t12; mkdir -p $O
cd "$CH" && helm dependency build . >/dev/null || { echo "helm dependency build failed in $CH"; exit 1; }
for v in $OV; do
  o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $O/$L-$v.yaml || { echo "[$v] helm template failed"; continue; }
  python3 $SCR/check_t12.py $v < $O/$L-$v.yaml
done
true
T12RENDER
cat > $SCR/t12_ci.sh <<'T12CI'
#!/usr/bin/env bash
# CI's three chart checks per overlay on $SCR/t12/<label>-<overlay>.yaml: helm lint, kubeconform, and kube-score with
# deploy-validate.yml's flags (CRITICAL lines saved, sorted). With a second label, also diffs the CRITICAL sets.
# Usage: t12_ci.sh <label> [baseline label]
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
L=$1; B=${2:-}; O=$SCR/t12; CH=$WT/Iverson.Server/deploy/helm/iverson
cd $CH && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do
  o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  lint="$(helm lint . $o 2>&1 | tail -1)"
  kc="$(kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas < $O/$L-$v.yaml 2>&1 | tail -1)"
  kube-score score --ignore-test pod-networkpolicy --ignore-test container-image-pull-policy \
    --ignore-test container-security-context-user-group-id --output-format ci - < $O/$L-$v.yaml 2>/dev/null \
    | command grep '^\[CRITICAL\]' | sort > $O/$L-$v.ks
  d=""; [ -n "$B" ] && d=" | criticals changed: $(diff $O/$B-$v.ks $O/$L-$v.ks | command grep -c '^[<>]')"
  echo "[$v] lint: $lint | $kc | criticals: $(wc -l < $O/$L-$v.ks)$d"
done
T12CI
cat > $SCR/t12_mutate.sh <<'T12MUT'
#!/usr/bin/env bash
# Task 12's mutation proof, on copies under $SCR/t12-mut (never the worktree). Each mutation replaces exactly one
# occurrence of a string in one copied file, runs the check that must catch it, and restores the copy.
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
M=$SCR/t12-mut; rm -rf $M; mkdir -p $M/Iverson.Server/Iverson.Api $M/Iverson.Server/deploy/helm $M/scripts
cp -r $WT/Iverson.Server/deploy/helm/iverson $M/Iverson.Server/deploy/helm/
cp $WT/Iverson.Server/docker-compose.yml $M/Iverson.Server/
cp $WT/Iverson.Server/Iverson.Api/appsettings.Development.json $M/Iverson.Server/Iverson.Api/
cp $WT/scripts/generate-compose-secrets.sh $M/scripts/
CH=$M/Iverson.Server/deploy/helm/iverson; T=$CH/charts
mutate() {  # file, old, new: exactly one occurrence, else the mutation is reported as not applied
  python3 - "$1" "$2" "$3" > $M/tmp <<'T12PY'
import sys
s = open(sys.argv[1]).read()
if s.count(sys.argv[2]) != 1:
    sys.exit(f"mutation not applied: {s.count(sys.argv[2])} occurrences in {sys.argv[1]}")
print(s.replace(sys.argv[2], sys.argv[3]), end="")
T12PY
  local rc=$?; [ $rc = 0 ] && cp "$1" $M/orig && mv $M/tmp "$1"; return $rc
}
restore() { mv $M/orig "$1"; }
render_local() { bash $SCR/t12_render.sh mut $CH values-local | sed 's/^/    /'; }
job_check() { bash $SCR/t12_render.sh mut $CH values-local >/dev/null; bash $SCR/t12_job.sh $SCR/t12/mut-values-local.yaml | sed 's/^/    /'; }
compose_check() { python3 $SCR/check_t12_compose.py $M | sed 's/^/    /'; }
run() {  # id, file, old, new, check
  echo "$1"; if mutate "$2" "$3" "$4"; then $5; restore "$2"; fi
}
J=$T/starrocks/templates/job-create-user.yaml
C=$M/Iverson.Server/docker-compose.yml
run M1 $J "                  public) ;;
" "" job_check
run M2 $J '/ ON /d; ' '' job_check
run M3 $J "                  *[!A-Za-z0-9_]*) echo \"unexpected role name granted to iverson_app: \$ROLE\" >&2; exit 1 ;;
" "" job_check
run M4 $T/api/templates/deployment.yaml 'Username=iverson_api;Password=$(POSTGRES_API_PASSWORD)' 'Username=iverson;Password=$(POSTGRES_API_PASSWORD)' render_local
run M5 $T/postgres/templates/cluster.yaml "        inRoles:
          - iverson_runtime
" "" render_local
run M6 $T/worker/templates/deployment.yaml '{{ .Release.Name }}-starrocks-tenant-key' '{{ .Release.Name }}-starrocks-app' render_local
run M7 $T/authentik/templates/job-revoke-cross-db.yaml '-c "GRANT CONNECT ON DATABASE iverson TO iverson_api;"' '-c "SELECT 1;"' render_local
run M8 $T/worker/templates/deployment.yaml '- name: Tenancy__SeedLegacyTenants' '- name: Tenancy__SeedLegacyTenantsUnused' render_local
run M9 $C "      iverson-worker:
        condition: service_healthy
" "      iverson-worker:
        condition: service_started
" compose_check
run M10 $C "            public) ;;
" "" compose_check
T12MUT
ls $SCR/check_t12.py $SCR/check_t12_compose.py $SCR/t12_job.sh $SCR/t12_render.sh $SCR/t12_ci.sh $SCR/t12_mutate.sh | wc -l
```

Expected: `6`.

- [ ] **Step 2: Run them against the unmodified tree and see them fail**

Run from the worktree. This also saves the `base` renders and kube-score CRITICAL sets that Step 4 compares against.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
bash $SCR/t12_render.sh base
bash $SCR/t12_ci.sh base
bash $SCR/t12_job.sh $SCR/t12/base-values-local.yaml
python3 $SCR/check_t12_compose.py $WT | head -20
```

Expected:
- Every overlay prints `FAIL`. All five list the missing `iverson-postgres-api` Secret and managed role, the api's `iverson` login and `<release>-postgres-app` reference, the api's seed, the worker's missing seed, and the revoke-cross-db hook's missing wait and grant. The four overlays other than laptop also list the missing tenant-key Secret, both `StarRocks__TenantKey` refs, and the create-user Job's missing `SHOW GRANTS` and revoke.
- `t12_ci.sh base` prints, for each overlay, `lint: 1 chart(s) linted, 0 chart(s) failed`, kubeconform `Invalid: 0, Errors: 0`, and `criticals: 0`. At plan time the Valid counts were 92/68/99/99/99 for local/laptop/aws/azure/gcp.
- `[job:roles] FAIL exit=0 revokes=[]`, `[job:none] FAIL exit=0 revokes=[]` (the script never reads `SHOW GRANTS`) and `[job:badname] FAIL exit=0 revokes=[]`.
- `[compose] FAIL`, listing every check in `check_t12_compose.py` except three that already hold at this point:
  - `worker ConnectionStrings__Postgres is no longer the owner` and `worker lost Tenancy__SeedLegacyTenants` hold today;
  - `appsettings.Development.json has no StarRocks:TenantKey` holds since Task 10 added the key.

- [ ] **Step 3: Implement**

**(a) The api's Postgres Secret.** It follows `charts/authentik/templates/secret-postgres.yaml`: one `lookup` per Secret, `keep`, and basic-auth with `username` and `password`. The username must equal the role name, because CNPG reads `passwordSecret` for that role.

Create `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/secret-api.yaml`:

```yaml
{{- /*
CSR round 12 §6.1: the password of iverson_api, the api's own Postgres login (cluster.yaml's managed
role). It is a Secret of its own: <release>-postgres-app is CNPG's, for the owner login, which only
the worker and the deploy hooks use.
*/}}
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-postgres-api
  annotations:
    "helm.sh/resource-policy": keep
type: kubernetes.io/basic-auth
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-postgres-api" .Release.Name) }}
{{- if $existing }}
  username: {{ index $existing.data "username" }}
  password: {{ index $existing.data "password" }}
{{- else }}
  username: {{ "iverson_api" | b64enc }}
  password: {{ randAlphaNum 32 | b64enc }}
{{- end }}
```

**(b) The managed role, and the comments that name the api's startup.**

In `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml`, replace:

```yaml
  enableSuperuserAccess: false   # least privilege — api never authenticates as `postgres`, only the app user below
```

with:

```yaml
  enableSuperuserAccess: false   # least privilege — nothing authenticates as `postgres`: the worker and the hooks use the owner `iverson`, the api uses iverson_api below
```

In `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml`, replace:

```yaml
      # Run once, as superuser, right after initdb. The API's startup (PostgresSchemaManager
```

with:

```yaml
      # Run once, as superuser, right after initdb. The worker's startup (PostgresSchemaManager
```

In `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml`, replace:

```yaml
      # and granting membership at bootstrap makes the API's own CREATEs a no-op.
```

with:

```yaml
      # and granting membership at bootstrap makes the worker's own CREATEs a no-op.
```

In `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml`, replace:

```yaml
      - name: authentik
        ensure: present
        login: true
        passwordSecret:
          name: {{ .Release.Name }}-authentik-postgres
```

with:

```yaml
      - name: authentik
        ensure: present
        login: true
        passwordSecret:
          name: {{ .Release.Name }}-authentik-postgres
      # CSR round 12 §6.1: the api's own login. A member of iverson_runtime, which
      # postInitApplicationSQL above creates at initdb, so it exists before CNPG reconciles this.
      # No BYPASSRLS, no iverson_maintenance, owns nothing. CNPG creates it on an existing cluster
      # too. Its CONNECT and table grants come from the worker at startup, as the database owner.
      - name: iverson_api
        ensure: present
        login: true
        inRoles:
          - iverson_runtime
        passwordSecret:
          name: {{ .Release.Name }}-postgres-api
```

**(c) The tenant key.** It is a Secret of its own: `<release>-starrocks-app` and `<release>-starrocks-provisioner` copy only their existing keys when they already exist, so a key added to either would render empty on every upgraded cluster (spec Verified assumption 31).

Create `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-tenant-key.yaml`:

```yaml
{{- /*
CSR round 12 §7.1: the master key every tenant's StarRocks login password is derived from (hex
HMAC-SHA256 of "starrocks-tenant:<tenantId>"). The api and the worker both read it. It is a Secret
of its own, not a new key in <release>-starrocks-app or -provisioner: those copy their existing keys
when they already exist, so a key added there would render empty on every upgraded cluster.
Never change it: provisioning creates a tenant's login only when it is missing and never resets its
password, so a new key locks every existing tenant out.
*/}}
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-starrocks-tenant-key
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-starrocks-tenant-key" .Release.Name) }}
{{- if $existing }}
  tenant-key: {{ index $existing.data "tenant-key" }}
{{- else }}
  tenant-key: {{ randAlphaNum 64 | b64enc }}
{{- end }}
```

**(d) The create-user hook revokes every role `iverson_app` still holds.**

How the revoke reads the roles:
- **Which rows.** `SHOW GRANTS` prints one row per grant: `UserIdentity`, `Catalog`, `Grants`. A privilege grant always names its object with ` ON ` (`GRANT OPERATE ON SYSTEM TO USER …`). A role grant does not (`GRANT 'role_a', 'role_b' TO 'iverson_app'@'%'`).
- **Parsing.** The `sed` drops every row with ` ON `, keeps what sits between `GRANT ` and ` TO `, and `tr` strips quotes, backticks and spaces and splits on commas. That covers the hashed `role_tenant_<readable>_<hash>` names and the legacy verbatim `role_tenant_<tenantId>` ones alike.
- **`public`** is every user's implicit role, so it is left alone.
- **Unexpected names.** A name outside `[A-Za-z0-9_]` stops the job rather than being spliced into a statement. `set -f` stops the unquoted `for` list from globbing.
- **Errors.** Under `set -e`, a failed `SHOW GRANTS` fails the job, because the assignment takes the status of its command substitution. A failed `REVOKE` fails it too.

In `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml`, replace:

```yaml
              # against a live FE (4.1.1). Its tenant roles are granted by the provisioner.
```

with:

```yaml
              # against a live FE (4.1.1). It holds no tenant role (CSR round 12): see the revoke below.
```

In `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml`, replace:

```yaml
              # are per-connection session defaults for this user, not global settings — every
              # other tenant/service is unaffected. Idempotent like the GRANTs above: reapplied
              # on every post-install/post-upgrade run of this job.
```

with:

```yaml
              # are per-connection session defaults for this user, not global settings — every
              # other tenant/service is unaffected. Idempotent like the GRANTs above: reapplied
              # on every post-install/post-upgrade run of this job.
              # CSR round 12 §7.5: iverson_app holds no tenant role any more. Every data
              # connection logs in as its tenant's own login (iverson_t_<suffix>), and
              # provisioning no longer grants the tenant role to iverson_app. This revokes every
              # role an earlier version granted it, hashed and legacy verbatim names alike, by
              # reading them back from SHOW GRANTS: a role grant is a row without " ON " (every
              # privilege grant names its object with ON). `public` is every user's implicit
              # role. It runs here, after the rollout, so no pod of the previous release is left
              # to re-grant a role or to read through one. A role name outside [A-Za-z0-9_]
              # stops the job instead of being spliced into a statement.
              APP_GRANTS="$(fe --batch --skip-column-names -e "SHOW GRANTS FOR 'iverson_app'@'%';")"
              APP_ROLES="$(printf '%s\n' "$APP_GRANTS" | sed -n "/ ON /d; s/.*GRANT \(.*\) TO .*/\1/p" | tr -d "' \`" | tr ',' '\n')"
              set -f
              for ROLE in $APP_ROLES; do
                case "$ROLE" in
                  public) ;;
                  *[!A-Za-z0-9_]*) echo "unexpected role name granted to iverson_app: $ROLE" >&2; exit 1 ;;
                  *) echo "revoking role $ROLE from iverson_app"; fe -e "REVOKE $ROLE FROM 'iverson_app'@'%';" ;;
                esac
              done
```

**(e) The api Deployment.**

In `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`, replace:

```yaml
            - name: POSTGRES_APP_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: {{ .Release.Name }}-postgres-app
                  key: password
            - name: ConnectionStrings__Postgres
              # CNPG's own `uri` secret key is a postgresql:// URI, which Npgsql's
              # connection-string parser rejects outright (it only accepts ADO.NET
              # key=value syntax, not libpq-style URIs) — composing it here mirrors the
              # ConnectionStrings__StarRocks pattern below.
              value: "Host={{ .Release.Name }}-postgres-rw;Port=5432;Database=iverson;Username=iverson;Password=$(POSTGRES_APP_PASSWORD);SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt"
```

with:

```yaml
            # CSR round 12 §6.1: the api's own login, iverson_api (charts/postgres: the managed role
            # and <release>-postgres-api). It is not the owner, holds no BYPASSRLS and runs no DDL;
            # the owner's Secret, <release>-postgres-app, is the worker's and the hooks' only.
            - name: POSTGRES_API_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: {{ .Release.Name }}-postgres-api
                  key: password
            - name: ConnectionStrings__Postgres
              # CNPG's own `uri` secret key is a postgresql:// URI, which Npgsql's
              # connection-string parser rejects outright (it only accepts ADO.NET
              # key=value syntax, not libpq-style URIs) — composing it here mirrors the
              # ConnectionStrings__StarRocks pattern below.
              value: "Host={{ .Release.Name }}-postgres-rw;Port=5432;Database=iverson;Username=iverson_api;Password=$(POSTGRES_API_PASSWORD);SSL Mode=VerifyFull;Root Certificate=/etc/postgres-tls/ca.crt"
```

In `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml`, replace:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt;"
            {{- end }}
            - name: Engagement__Enabled
              value: {{ .Values.global.engagementEnabled | quote }}
            - name: Tenancy__SeedLegacyTenants
              value: {{ .Values.global.provisionTestIdentities | quote }}
```

with:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt;"
            # CSR round 12 §7.1: every StarRocks data connection logs in as the caller's tenant,
            # iverson_t_<suffix>, whose password is derived from this key. iverson_app above is
            # left only /health and the readiness probes, and holds no tenant role.
            - name: StarRocks__TenantKey
              valueFrom:
                secretKeyRef:
                  name: {{ .Release.Name }}-starrocks-tenant-key
                  key: tenant-key
            {{- end }}
            - name: Engagement__Enabled
              value: {{ .Values.global.engagementEnabled | quote }}
```

**(f) The worker Deployment.** It keeps the owner login.

In `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml`, replace:

```yaml
            - name: ConnectionStrings__StarRocksProvisioner
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_provisioner;Password=$(STARROCKS_PROVISIONER_PASSWORD);SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt;"
            {{- end }}
            - name: Engagement__Enabled
              value: {{ .Values.global.engagementEnabled | quote }}
```

with:

```yaml
            - name: ConnectionStrings__StarRocksProvisioner
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_provisioner;Password=$(STARROCKS_PROVISIONER_PASSWORD);SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt;"
            # CSR round 12 §7.1: the api's master key. The worker's upserts and popularity
            # aggregates log in as each tenant, and provisioning creates each tenant's login with
            # the password derived from it.
            - name: StarRocks__TenantKey
              valueFrom:
                secretKeyRef:
                  name: {{ .Release.Name }}-starrocks-tenant-key
                  key: tenant-key
            {{- end }}
            - name: Engagement__Enabled
              value: {{ .Values.global.engagementEnabled | quote }}
            # CSR round 12 §6.3: the legacy-tenant seed is part of the startup DDL, which only the
            # worker runs. Moved from the api Deployment.
            - name: Tenancy__SeedLegacyTenants
              value: {{ .Values.global.provisionTestIdentities | quote }}
```

**(g) The Authentik hook also grants `iverson_api` CONNECT.** It connects as the owner, which can grant CONNECT. This grant is belt-and-braces: the hook runs only after `--wait` has seen the api Ready, so the worker's startup grant is the one the api depends on (spec §6.2, Verified assumption 22).

In `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/job-revoke-cross-db.yaml`, replace:

```yaml
              until psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='authentik';" 2>/dev/null | grep -q 1; do
                echo "waiting for authentik role to exist..."; sleep 5
              done
```

with:

```yaml
              until psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='authentik';" 2>/dev/null | grep -q 1; do
                echo "waiting for authentik role to exist..."; sleep 5
              done
              # CSR round 12 §6.1: iverson_api, the api's login, is another CNPG managed role, so
              # the GRANT below waits for it the same way.
              until psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='iverson_api';" 2>/dev/null | grep -q 1; do
                echo "waiting for iverson_api role to exist..."; sleep 5
              done
```

In `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/job-revoke-cross-db.yaml`, replace:

```yaml
              # correct fix is to revoke CONNECT from PUBLIC itself, then explicitly re-grant it
              # to the one role that legitimately needs it (`iverson`, used by both the api and
              # worker Deployments — see charts/api and charts/worker's ConnectionStrings__Postgres).
              # This also closes the door for any future role, not just `authentik`.
```

with:

```yaml
              # correct fix is to revoke CONNECT from PUBLIC itself, then explicitly re-grant it
              # to the two logins that legitimately need it: `iverson`, the owner, used by the
              # worker Deployment, and `iverson_api`, the api Deployment's login (CSR round 12; see
              # charts/api and charts/worker's ConnectionStrings__Postgres). The worker grants
              # iverson_api CONNECT at startup too, and that is the grant the api depends on.
              # This also closes the door for any future role, not just `authentik`.
```

In `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/job-revoke-cross-db.yaml`, replace:

```yaml
              psql -v ON_ERROR_STOP=1 -c "REVOKE CONNECT ON DATABASE iverson FROM PUBLIC;" \
                   -c "GRANT CONNECT ON DATABASE iverson TO iverson;"
```

with:

```yaml
              psql -v ON_ERROR_STOP=1 -c "REVOKE CONNECT ON DATABASE iverson FROM PUBLIC;" \
                   -c "GRANT CONNECT ON DATABASE iverson TO iverson;" \
                   -c "GRANT CONNECT ON DATABASE iverson TO iverson_api;"
```

**(h) Compose.**

What changes, and why:
- **The api's login.** Compose's `iverson` is the Postgres superuser, and compose has no CNPG. So iverson-worker creates `iverson_api` at startup (Task 9's compose path), with the password in `Postgres__ApiLoginPassword`. iverson-api's connection string uses the same literal.
- **The init SQL is not touched.** `docker-entrypoint-initdb.d` runs only on an empty data directory, before anything has created `iverson_runtime`, so an init clause conditioned on that role could never run (CDR-1 A17, CDR-2 A14). The spec, amended in `a8d18301`, says the same in §8: the superuser worker creates the login, its membership and its grants.
- **The order is reversed.** iverson-worker starts first and gets a real readiness check: HTTP 200 from `/health` on 8081, which it serves only after its startup block. iverson-api `depends_on` it being healthy.
- **The revoke.** `starrocks-init` runs the chart job's revoke word for word, with compose's `$$` escape for every `$`, and defines `fe()` for it.

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
#   - PostgreSQL: user/password = iverson/iverson
```

with:

```yaml
#   - PostgreSQL: user/password = iverson/iverson (the owner, used by iverson-worker), and the api's
#     own login iverson_api with password dev-only-not-for-production-postgres-api-pw-0123456789,
#     which iverson-worker creates at startup (CSR round 12)
#   - StarRocks tenant logins: their master key is generated per-checkout, required via
#     ${IVERSON_STARROCKS_TENANT_KEY:?...} — run scripts/generate-compose-secrets.sh first
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
  # The trailing ALTER USER ... SET PROPERTIES line (CSR finding #6, defense-in-depth) is
  # likewise carried over from the chart job — see its comment there for the rationale.
  starrocks-init:
```

with:

```yaml
  # The trailing ALTER USER ... SET PROPERTIES line (CSR finding #6, defense-in-depth) is
  # likewise carried over from the chart job — see its comment there for the rationale.
  # So is the role revoke after the batch (CSR round 12 §7.5), word for word: it reads
  # iverson_app's role grants back from SHOW GRANTS and revokes each one. Compose has no
  # post-upgrade hook, so it runs on every `up`, before iverson-worker and iverson-api start, and
  # nothing grants iverson_app a tenant role any more.
  starrocks-init:
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
          ALTER USER 'iverson_app' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296');
        "
    restart: "no"
```

with:

```yaml
          ALTER USER 'iverson_app' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296');
        "
        fe() { mysql -h starrocks -P 9030 -u root "$$@"; }
        APP_GRANTS="$$(fe --batch --skip-column-names -e "SHOW GRANTS FOR 'iverson_app'@'%';")"
        APP_ROLES="$$(printf '%s\n' "$$APP_GRANTS" | sed -n "/ ON /d; s/.*GRANT \(.*\) TO .*/\1/p" | tr -d "' \`" | tr ',' '\n')"
        set -f
        for ROLE in $$APP_ROLES; do
          case "$$ROLE" in
            public) ;;
            *[!A-Za-z0-9_]*) echo "unexpected role name granted to iverson_app: $$ROLE" >&2; exit 1 ;;
            *) echo "revoking role $$ROLE from iverson_app"; fe -e "REVOKE $$ROLE FROM 'iverson_app'@'%';" ;;
          esac
        done
    restart: "no"
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=iverson;Username=iverson;Password=iverson
```

with:

```yaml
      - ASPNETCORE_ENVIRONMENT=Production
      # CSR round 12 §6.1: the api's own login, as on the chart's api Deployment: not the owner, no
      # BYPASSRLS, no DDL. iverson-worker creates it with this password (Postgres__ApiLoginPassword)
      # and grants it what the api uses, before its healthcheck passes.
      - ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=iverson;Username=iverson_api;Password=dev-only-not-for-production-postgres-api-pw-0123456789
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
      # Matches the chart's api/worker deployments (deploy/helm/iverson/charts/api/templates/
      # deployment.yaml): connect as iverson_app, the query login starrocks-init creates, not root,
      # and with no default database (iverson_app holds no grant on `iverson`).
      - ConnectionStrings__StarRocks=Server=starrocks;Port=9030;Uid=iverson_app;Pwd=dev-only-not-for-production-starrocks-app-pw-0123456789;
      - Qdrant__ApiKey=${QDRANT__SERVICE__API_KEY:?run scripts/generate-compose-secrets.sh first}
```

with:

```yaml
      # Matches the chart's api/worker deployments (deploy/helm/iverson/charts/api/templates/
      # deployment.yaml): iverson_app, the login starrocks-init creates, with no default database.
      # It serves only /health and the readiness probes (CSR round 12 §7.4): every data connection
      # logs in as its tenant's own login, whose password is derived from StarRocks__TenantKey.
      - ConnectionStrings__StarRocks=Server=starrocks;Port=9030;Uid=iverson_app;Pwd=dev-only-not-for-production-starrocks-app-pw-0123456789;
      # CSR round 12 §7.1: the master key, shared with iverson-worker. Per-checkout, from
      # scripts/generate-compose-secrets.sh.
      - StarRocks__TenantKey=${IVERSON_STARROCKS_TENANT_KEY:?run scripts/generate-compose-secrets.sh first}
      - Qdrant__ApiKey=${QDRANT__SERVICE__API_KEY:?run scripts/generate-compose-secrets.sh first}
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
      - Jaeger__OtlpHttpUrl=http://jaeger:4318
      - Tenancy__SeedLegacyTenants=true
      # Iverson.AdminUI's Vite dev server (npm run dev) runs at localhost:5173
```

with:

```yaml
      - Jaeger__OtlpHttpUrl=http://jaeger:4318
      # Iverson.AdminUI's Vite dev server (npm run dev) runs at localhost:5173
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
    # Kestrel does not begin listening until Program.cs's startup block has finished — the
    # bootstrap DDL (EnsureRolesAsync, the plumbing-table EnsureTableAsync calls, and every
    # ApplySchemaAsync) is awaited BEFORE app.Run(). A successful TCP connect to 8080 is therefore
    # a precise "this replica's bootstrap DDL is complete" signal, which is exactly what
    # iverson-worker's depends_on needs. A bash /dev/tcp redirect rather than curl/wget: the
    # aspnet:10.0 runtime image ships neither (verified), but does ship bash.
```

with:

```yaml
    # A TCP connect to 8080 says Kestrel is listening, and that is all this healthcheck claims.
    # The api runs no bootstrap DDL (CSR round 12 §6.3): iverson-worker does, and this service waits
    # for it below. A bash /dev/tcp redirect rather than curl/wget: the aspnet:10.0 runtime image
    # ships neither (verified), but does ship bash.
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
      tei-embed:
        condition: service_healthy

  # The worker role. Every Kafka consumer and background reconciliation worker in Iverson.Api is
```

with:

```yaml
      tei-embed:
        condition: service_healthy
      # CSR round 12 §6.3: the worker starts first. It runs the bootstrap DDL, creates iverson_api
      # and issues its grants before it listens, so a passing worker healthcheck means this
      # service's login can connect and read the schema registry.
      iverson-worker:
        condition: service_healthy

  # The worker role. Every Kafka consumer and background reconciliation worker in Iverson.Api is
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
      - Otel__Endpoint=http://jaeger:4317
      - ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=iverson;Username=iverson;Password=iverson
```

with:

```yaml
      - Otel__Endpoint=http://jaeger:4317
      - ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=iverson;Username=iverson;Password=iverson
      # CSR round 12 §6.1: compose has no CNPG to create iverson_api, so this worker, connected as
      # the superuser above, creates it at startup with this password; iverson-api logs in with it.
      - Postgres__ApiLoginPassword=dev-only-not-for-production-postgres-api-pw-0123456789
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
      # Matches the chart's api/worker deployments (deploy/helm/iverson/charts/api/templates/
      # deployment.yaml): connect as iverson_app, the query login starrocks-init creates, not root,
      # and with no default database (iverson_app holds no grant on `iverson`).
      - ConnectionStrings__StarRocks=Server=starrocks;Port=9030;Uid=iverson_app;Pwd=dev-only-not-for-production-starrocks-app-pw-0123456789;
      # CSR round 11 §3: tenant provisioning's own login, as on the chart's worker. Only the
      # worker provisions, so iverson-api has no provisioner connection string.
      - ConnectionStrings__StarRocksProvisioner=Server=starrocks;Port=9030;Uid=iverson_provisioner;Pwd=dev-only-not-for-production-starrocks-provisioner-pw-0123456789;
```

with:

```yaml
      # Matches the chart's api/worker deployments (deploy/helm/iverson/charts/api/templates/
      # deployment.yaml): iverson_app, the login starrocks-init creates, with no default database.
      # It serves only /health and the readiness probes (CSR round 12 §7.4): every data connection
      # logs in as its tenant's own login, whose password is derived from StarRocks__TenantKey.
      - ConnectionStrings__StarRocks=Server=starrocks;Port=9030;Uid=iverson_app;Pwd=dev-only-not-for-production-starrocks-app-pw-0123456789;
      # CSR round 11 §3: tenant provisioning's own login, as on the chart's worker. Only the
      # worker provisions, so iverson-api has no provisioner connection string.
      - ConnectionStrings__StarRocksProvisioner=Server=starrocks;Port=9030;Uid=iverson_provisioner;Pwd=dev-only-not-for-production-starrocks-provisioner-pw-0123456789;
      # CSR round 12 §7.1: iverson-api's master key. Provisioning creates each tenant's login with
      # the password derived from it, and the worker's upserts and aggregates log in with it.
      - StarRocks__TenantKey=${IVERSON_STARROCKS_TENANT_KEY:?run scripts/generate-compose-secrets.sh first}
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
    depends_on:
      # The same backing-service conditions iverson-api uses...
```

with:

```yaml
    # CSR round 12 §6.3: iverson-api waits for this. The worker's startup block (the plumbing and
    # entity-table DDL, the roles, iverson_api and its grants, the legacy-tenant seed) is awaited
    # before Kestrel listens, so an HTTP 200 from /health on 8081 means it has finished and the
    # stores answer. bash /dev/tcp again: the runtime image has no curl or wget.
    healthcheck:
      test: ["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8081 && printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
      interval: 5s
      timeout: 10s
      retries: 60
      start_period: 30s
    depends_on:
      # The same backing-service conditions iverson-api uses...
```

In `Iverson.Server/docker-compose.yml`, replace:

```yaml
      tei-embed:
        condition: service_healthy
      # ...plus one more: wait for iverson-api to finish its OWN bootstrap before starting this
      # one. Both roles run the identical startup block (it sits outside Program.cs's
      # `workloadRole == "api"` guard), and that block is genuinely concurrency-safe by design —
      # every statement is CREATE/ALTER ... IF [NOT] EXISTS or ON CONFLICT DO NOTHING, and the two
      # CREATE-then-catch paths (EnsureRolesAsync, the RLS policy) both swallow SQLSTATE
      # 42710 with an explicit "another replica created it concurrently" comment. Helm already
      # runs api x2 plus a worker against one database on that basis.
      #
      # It is sequenced anyway because "safe by design" is not the same as "raced on every single
      # `docker compose up -d`". Bare CREATE TABLE/INDEX IF NOT EXISTS is still not atomic against
      # an identical concurrent statement in Postgres — it can surface as 23505 on a pg_catalog
      # unique index, and that SQLSTATE is not caught anywhere. Starting second closes the window
      # completely rather than relying on losing a race that is cheap to avoid: by the time this
      # container starts, every IF NOT EXISTS check it runs is a no-op.
      iverson-api:
        condition: service_healthy
```

with:

```yaml
      tei-embed:
        condition: service_healthy
```

**(i) The compose secrets script** emits the tenant key. A re-run appends only missing keys, so an existing `.env` gains it and keeps its other values.

In `scripts/generate-compose-secrets.sh`, replace:

```bash
    AUTHENTIK_BOOTSTRAP_TOKEN
)
```

with:

```bash
    AUTHENTIK_BOOTSTRAP_TOKEN
    # CSR round 12 §7.1: the master key every StarRocks tenant login's password is derived from.
    # Never change it on a stack that has tenant logins: provisioning only creates missing ones.
    IVERSON_STARROCKS_TENANT_KEY
)
```

**(j) Development settings.** No default database (Task 10 already added the dev `StarRocks:TenantKey`): each tenant's connection string is built from this one with its own login, which has no grant on `iverson`. The chart and compose strings dropped `Database=iverson` in round 11.

In `Iverson.Server/Iverson.Api/appsettings.Development.json`, replace:

```json
    "StarRocks": "Server=localhost;Port=9030;Database=iverson;User Id=root;Password=;",
```

with:

```json
    "StarRocks": "Server=localhost;Port=9030;User Id=root;Password=;",
```

**(k) The runbook.**

Create `docs/runbooks/csr12-server.md`:

````markdown
# CSR Round-12 Server — Operator Runbook

**Why:** the api process no longer holds a credential that reaches across tenants.
- **Postgres.** The api logs in as `iverson_api`: a member of `iverson_runtime`, not the owner, with no BYPASSRLS, no `iverson_maintenance` and no DDL.
- **StarRocks.** Every data connection logs in as its tenant's own login, `iverson_t_<suffix>`, which holds only that tenant's role. `iverson_app` keeps only `OPERATE ON SYSTEM`, for `/health` and the readiness probes.
- **The worker.** The owner, maintenance and provisioning credentials stay with the worker and the deploy hooks. The worker also runs the privileged request-driven work: RegisterSchema's DDL and registry write, `/admin/reconcile/{type}` and DLQ replay.

## 1. Before the upgrade

- **StarRocks must hold no tenant data written before this release.** There is no backfill. A tenant's login is created by its first write after the upgrade, and a tenant with no login reads as empty. On compose, run `docker compose down -v` before the first `up` of this release. On kind, create a new cluster.
- **Run `helm dependency build`** in the chart directory. A stale `charts/*.tgz` renders the old subcharts.
- **Compose:** run `scripts/generate-compose-secrets.sh` once. It adds `IVERSON_STARROCKS_TENANT_KEY` to `.env` and leaves the existing values alone.
- **The `stack.py query` benchmark tier** (`Iverson.Server/Iverson.LoadTest/scripts/stack.py`) starts `iverson-api` with `--no-deps`, so the worker does not run. The api's login `iverson_api` and its grants exist only once the worker has started on that Postgres volume. On a fresh volume, run the full stack once (`docker compose up -d`, until `iverson-api` is healthy) before the first `stack.py query`. Registering a type also needs the worker (§2, release notes).

## 2. What the upgrade does

1. **Two new Secrets.** Both are lookup-or-generate and kept on uninstall.
   - `<release>-postgres-api` (`kubernetes.io/basic-auth`) holds `iverson_api`'s password.
   - `<release>-starrocks-tenant-key` (profiles with StarRocks) holds the master key. Each tenant login's password is the hex HMAC-SHA256 of `starrocks-tenant:<tenantId>` under it. **Never change or delete it** (§4). Provisioning creates a login only when it is missing and never resets a password, so a new key makes every existing tenant's reads and writes fail with `Unavailable`. Deleting the Secret has the same effect, because the next upgrade generates a new key.
2. **CNPG creates the managed role `iverson_api`**, as a member of `iverson_runtime` (`charts/postgres/templates/cluster.yaml`). It does this on existing clusters too.
3. **The worker, at startup and as the database owner:**
   - creates the plumbing tables;
   - seeds the legacy tenants where `global.provisionTestIdentities` is true (the api no longer does);
   - grants `iverson_api`:

   | Object | Grant |
   |---|---|
   | database `iverson` | `CONNECT` |
   | entity tables | through `iverson_runtime`, as before |
   | `IversonReconciliationQueue` | `INSERT`, `DELETE`, `SELECT ("Id")` |
   | `IversonDlqMessages` | `SELECT` on every column except `MessageValue` |
   | `IversonTenants` | `SELECT`, `INSERT`, `UPDATE`, `DELETE` |
   | `iverson_token_revocations` | `SELECT`, `INSERT`, `UPDATE` |
   | `_iverson_schema` | `SELECT` |
   | `IversonPrivilegedRequests` | `INSERT`, `SELECT`, `UPDATE ("Status")` |
   | `IversonStarRocksTenants` | `SELECT` |

   A worker that starts before CNPG has created `iverson_api` fails its first grant and is restarted. It succeeds once the role exists.
4. **The api connects as `iverson_api` and runs no DDL.**
   - A new api pod stays not-ready until the worker's grants are in place and the pod's schema registry has loaded, so the order in which pods restart does not matter.
   - The api no longer mounts `<release>-postgres-app` (the owner's Secret).
5. **The hooks, after the rollout:**
   - `<release>-authentik-revoke-cross-db` also waits for `iverson_api` and grants it `CONNECT`. This repeats the worker's grant, which is the one the api relies on.
   - `<release>-starrocks-create-user` (profiles with StarRocks) revokes every role still granted to `iverson_app`, as read from `SHOW GRANTS`. That covers the hashed `role_tenant_<readable>_<hash>` names and the legacy `role_tenant_<tenantId>` ones. Every pod of the previous release is gone by then, so nothing re-grants them.
   - If this hook fails with `unexpected role name granted to iverson_app`, a role whose name is not `[A-Za-z0-9_]` is granted to `iverson_app`. Iverson never creates one. Revoke it by hand, quoting the name with backticks, then re-run the upgrade.

**Release notes (behaviour changes for operators and callers):**
- **The StarRocks tenant key must never change** on a stack with tenant logins. See §4.
- **Registration needs a running worker.**
  - With no worker, `RegisterSchema` returns `Unavailable` after 15 s.
  - A registration that the worker has claimed but not finished within a further 10 s returns `DeadlineExceeded`, and completes in the worker. Registering again is safe.
- **The admin routes can answer before the worker finishes.** `/admin/reconcile/{type}` returns `202 {requestId}` when the reconcile is still running after 60 s, and `/admin/dlq/{id}/replay` does the same after 30 s. The worker finishes both.
- **Oversized writes from .NET clients** now fail at the API with `InvalidArgument`, instead of dead-lettering later in StarRocks. The size guard now recognises their camelCase keys as the columns they name.

## 3. Checking

Postgres:

```bash
kubectl cnpg psql <release>-postgres -n <ns> -- -d iverson -c '\du iverson_api' -c '\drg iverson_api'
kubectl cnpg psql <release>-postgres -n <ns> -- -d iverson -tA -c "SELECT
  has_database_privilege('iverson_api', 'iverson', 'CONNECT'),
  has_table_privilege('iverson_api', '\"IversonReconciliationQueue\"', 'INSERT'),
  has_column_privilege('iverson_api', '\"IversonReconciliationQueue\"', '\"Payload\"', 'SELECT'),
  has_column_privilege('iverson_api', '\"IversonDlqMessages\"', '\"MessageValue\"', 'SELECT'),
  pg_has_role('iverson_api', 'iverson_maintenance', 'MEMBER')"
```

Expected:
- `\du` lists `iverson_api` with no attributes: no Superuser, Create role, Create DB or Bypass RLS.
- `\drg` shows it as a member of `iverson_runtime` only.
- The query prints `t|t|f|f|f`.

StarRocks, as root. The root password is the `root-password` key of `<release>-starrocks-app`.

```bash
kubectl -n <ns> port-forward svc/<release>-starrocks-fe-service 9030:9030
mysql -h 127.0.0.1 -P 9030 -u root -p --ssl-mode=REQUIRED -e "SHOW GRANTS FOR 'iverson_app'@'%'; SHOW USERS;"
```

Expected:
- `iverson_app` holds `OPERATE ON SYSTEM`, and no role other than `public`.
- `SHOW USERS` lists `'iverson_t_<suffix>'@'%'` for each tenant that has written since the upgrade.

Compose:

```bash
docker compose exec postgres psql -U iverson -d iverson -c '\du iverson_api' -c '\drg iverson_api'
docker compose exec starrocks mysql -h 127.0.0.1 -P 9030 -u root -e "SHOW GRANTS FOR 'iverson_app'@'%';"
```

On compose, iverson-worker starts first, and iverson-api waits until the worker's `/health` answers 200. The worker connects as the superuser `iverson`, and creates `iverson_api` with the dev-only password in `docker-compose.yml`. `starrocks-init` runs the same role revoke as the chart's hook on every `up`.

## 4. The StarRocks tenant key

**Never change the key on a stack that has tenant logins.** It lives in `<release>-starrocks-tenant-key` (Helm) or as `IVERSON_STARROCKS_TENANT_KEY` in `.env` (compose).
- Each tenant's login, `iverson_t_<suffix>`, is created once, by its first provisioning, with a password derived from the key.
- Provisioning only creates logins that are missing. It never resets a password.
- So after a key change, every existing tenant's StarRocks reads and writes fail with `Unavailable` until its login is re-keyed. Tenants first provisioned after the change are unaffected.

**If the key has changed anyway**, set each recorded tenant's password to the one the new key derives. Run this as root or as `iverson_provisioner`, which holds `user_admin`.

1. **List the recorded tenants.** Every provisioned tenant has a row:

   ```bash
   kubectl cnpg psql <release>-postgres -n <ns> -- -d iverson -tA -c 'SELECT "TenantId" FROM "IversonStarRocksTenants" ORDER BY 1' > tenants.txt
   ```

2. **Derive each login's name and password from the current key.** The name is `iverson_t_` plus the same 25-digit fingerprint as the tenant's role name: the first 16 bytes of SHA-256 over the tenant id, read big-endian, written as 25 base-36 digits. The password is the lower-case hex HMAC-SHA256 of `starrocks-tenant:<tenantId>` under the key's UTF-8 bytes. This prints one statement per tenant:

   ```bash
   TENANT_KEY="$(kubectl -n <ns> get secret <release>-starrocks-tenant-key -o jsonpath='{.data.tenant-key}' | base64 -d)" \
   python3 -c '
   import hashlib, hmac, os, sys
   key, digits = os.environ["TENANT_KEY"].encode(), "0123456789abcdefghijklmnopqrstuvwxyz"
   for tenant in (l.strip() for l in sys.stdin if l.strip()):
       n, fp = int.from_bytes(hashlib.sha256(tenant.encode()).digest()[:16], "big"), ""
       for _ in range(25):
           fp, n = digits[n % 36] + fp, n // 36
       pw = hmac.new(key, ("starrocks-tenant:" + tenant).encode(), hashlib.sha256).hexdigest()
       print(f"ALTER USER '"'"'iverson_t_{fp}'"'"'@'"'"'%'"'"' IDENTIFIED BY '"'"'{pw}'"'"';")
   ' < tenants.txt > rekey.sql
   ```

3. **Run `rekey.sql` against the FE** as root, as in §3. Then delete `tenants.txt` and `rekey.sql`: the statements carry the passwords.

The derivation matches the server's (`TenantIdentifier.LoginName` and `LoginPassword`). For the tenant id `acme` and the key `test-tenant-login-key`, it gives `iverson_t_7pfddyte668561tz83en42m5m` and a password starting `401cfbf7`, the vector the unit tests pin.
````

- [ ] **Step 4: Run the checks and see them pass**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
bash $SCR/t12_render.sh new
bash $SCR/t12_ci.sh new base
bash $SCR/t12_job.sh $SCR/t12/new-values-local.yaml
python3 $SCR/check_t12_compose.py $WT
python3 -c "import json; json.load(open('$WT/Iverson.Server/Iverson.Api/appsettings.Development.json')); print('dev json parses')"
bash -n $WT/scripts/generate-compose-secrets.sh && echo "secrets script parses"
git -C $WT status --short
```

Expected:
- `[values-local] PASS`, `[values-laptop] PASS`, `[values-aws] PASS`, `[values-azure] PASS` and `[values-gcp] PASS`.
- For each overlay: `lint: 1 chart(s) linted, 0 chart(s) failed`, kubeconform `Invalid: 0, Errors: 0`, `criticals: 0` and `criticals changed: 0`. The Valid counts rise by the new Secrets: 94/69/101/101/101 at plan time.
- `[job:roles] PASS exit=0 revokes=[REVOKE role_tenant_tenant_smoke_test_0123456789abcdefghijklmno FROM 'iverson_app'@'%';|REVOKE role_tenant_tenant_smoke_test FROM 'iverson_app'@'%';|]`, then `[job:none] PASS exit=0 revokes=[]` and `[job:badname] PASS exit=1 revokes=[]`.
- `[compose] PASS`.
- `dev json parses` and `secrets script parses`.
- `git status` lists exactly this task's files: three new (`??`) and eight modified (` M`). `docs/runbooks/` is tracked, not gitignored (`git check-ignore` finds no rule for it), so the runbook shows with the others.

The parse check through `docker compose config` runs in Task 13 Step 5, where docker is allowed. Here `check_t12_compose.py` loads the file with PyYAML.

- [ ] **Step 5: Mutation proof**

`t12_mutate.sh` copies the chart, the compose file, the Development settings and the secrets script to `$SCR/t12-mut`. It applies each mutation alone to its copy, runs the check that must catch it, and restores the copy. The worktree is never edited.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
bash $SCR/t12_mutate.sh 2>&1 | tee $SCR/t12-mutations.log
git -C $WT status --short
```

| # | Mutation (on the copy) | Expected failing check |
|---|---|---|
| M1 | `job-create-user.yaml`: delete the `public) ;;` arm | `[job:roles] FAIL`, whose revokes include `REVOKE public` |
| M2 | `job-create-user.yaml`: delete `/ ON /d; ` from the `sed` | `[job:roles] FAIL` and `[job:none] FAIL`, revoking `OPERATEONSYSTEM` |
| M3 | `job-create-user.yaml`: delete the `*[!A-Za-z0-9_]*)` arm | `[job:badname] FAIL exit=0`, revoking `role-with-hyphen` |
| M4 | api `deployment.yaml`: `Username=iverson_api` becomes `Username=iverson` | `[values-local] FAIL`: `api ConnectionStrings__Postgres not iverson_api` |
| M5 | `cluster.yaml`: delete `inRoles: [iverson_runtime]` | `[values-local] FAIL`: `managed role iverson_api wrong` |
| M6 | worker `deployment.yaml`: the tenant key's Secret becomes `-starrocks-app` | `[values-local] FAIL`: `worker StarRocks__TenantKey secretKeyRef wrong` |
| M7 | `job-revoke-cross-db.yaml`: the `iverson_api` CONNECT grant becomes `SELECT 1` | `[values-local] FAIL`: `revoke-cross-db does not grant CONNECT to iverson_api` |
| M8 | worker `deployment.yaml`: rename the `Tenancy__SeedLegacyTenants` env | `[values-local] FAIL`: `worker Tenancy__SeedLegacyTenants != true` |
| M9 | `docker-compose.yml`: the api's `iverson-worker` condition becomes `service_started` | `[compose] FAIL`: `api does not wait for a healthy worker` |
| M10 | `docker-compose.yml`: delete `starrocks-init`'s `public) ;;` arm | `[compose] FAIL`: `revoke differs` |

Expected:
- Each `M<n>` line is followed by the failure named in the table. No line reads `mutation not applied`.
- `git status --short` lists the same files as Step 4: the worktree is untouched.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server && C=Iverson.Server/deploy/helm/iverson && git add \
  $C/charts/postgres/templates/secret-api.yaml \
  $C/charts/postgres/templates/cluster.yaml \
  $C/charts/starrocks/templates/secret-tenant-key.yaml \
  $C/charts/starrocks/templates/job-create-user.yaml \
  $C/charts/api/templates/deployment.yaml \
  $C/charts/worker/templates/deployment.yaml \
  $C/charts/authentik/templates/job-revoke-cross-db.yaml \
  Iverson.Server/docker-compose.yml \
  scripts/generate-compose-secrets.sh \
  Iverson.Server/Iverson.Api/appsettings.Development.json \
  docs/runbooks/csr12-server.md
git commit -m "give the api its own postgres login and the tenant key, revoke iverson_app's tenant roles, and start the compose worker first

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
git status --short
```

Expected: the commit lists 11 files, and `git status --short` prints nothing for them. The `charts/*.tgz` files that `helm dependency build` writes are gitignored (`.gitignore`, `Iverson.Server/deploy/helm/**/charts/*.tgz`), and `Chart.lock` is unchanged.

### Task 13: Live verification

**Spec:** §9 "Live", and the deployment half of §10.

**Files:** none. There are no commits. Every live resource is throwaway, uniquely named, and deleted at the end.

**Interfaces:**
- Consumes every earlier task. It runs last, by construction.
- Config keys it relies on, which the assembler must reconcile with D4 and D5 (see Task 12's **Interfaces**): `Postgres__ApiLoginPassword`, `StarRocks__TenantKey`, and the table `"IversonStarRocksTenants"` with column `"TenantId"` (Task 10).

Rules for this task:
- **One heavy process at a time.** Check `free -g` before each heavy step. The compose and kind checks run one after the other, never together. The laptop kind profile has no StarRocks (spec §9), so StarRocks is exercised in compose (its image is `starrocks/allin1-ubuntu:4.1.6`) and in a throwaway 4.1.6 container.
- **The user's stacks are off-limits.**
  - Never start, stop or modify their `iversonserver` containers, volumes, networks or images, or another session's `rfdt-*` containers or `tei_models` volume.
  - If `docker ps` lists anything, or `kind get clusters` lists a cluster, at Step 3, **stop and ask the user**. Compose binds host ports 5432, 8080, 8081, 9000 and 9030, and kind binds 8080 and 8443.
- **Slow steps** (`up --build`, kind create, image builds, `helm upgrade --install`) run with `run_in_background`, and a Monitor waits for them.
- **Processes.** Kill processes by PID, never with `pkill -f`.

- [ ] **Step 1: Full suites.** These run one at a time. The container-backed classes are inside them.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Server
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
for p in Iverson.Api.Tests Iverson.ClientConformance.Tests Iverson.Embeddings.Tests Iverson.Events.Tests Iverson.LoadTest.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  echo "$p: $(systemd-run --user --scope -q -p MemoryMax=6G dotnet test $p/$p.csproj 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"; done
cd ../Iverson.Clients/DotNet && for p in Iverson.Client.Core.Tests Iverson.Client.Search.Tests; do
  echo "$p: $(systemd-run --user --scope -q -p MemoryMax=4G dotnet test $p/$p.csproj 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"; done
dotnet build-server shutdown >/dev/null 2>&1
```

Expected: every line reads `Passed!` with 0 failed.

- [ ] **Step 2: Final renders, chart checks and tfsec on all five overlays.** `t12_render.sh` and `t12_ci.sh` are Task 12's. `base` is Task 12 Step 2's pre-change render. tfsec runs on the worktree and on main: this branch changes no Terraform, so the two runs must agree.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
bash $SCR/t12_render.sh final
bash $SCR/t12_ci.sh final base
tfsec $WT/Iverson.Server/deploy/terraform/ --no-colour > $SCR/tfsec-branch.txt 2>&1; echo "tfsec branch exit=$?"
tfsec /home/ben/repositories/Iverson/Iverson.Server/deploy/terraform/ --no-colour > $SCR/tfsec-main.txt 2>&1; echo "tfsec main exit=$?"
diff <(command grep -E '^ +(passed|ignored|critical|high|medium|low)' $SCR/tfsec-main.txt) <(command grep -E '^ +(passed|ignored|critical|high|medium|low)' $SCR/tfsec-branch.txt) && echo tfsec-unchanged
git -C $WT log --format='%h %s | %(trailers:key=Co-Authored-By,valueonly,separator=)' main..HEAD
```

Expected:
- All five overlays print `PASS`.
- Every overlay shows `lint: 1 chart(s) linted, 0 chart(s) failed`, kubeconform `Invalid: 0, Errors: 0`, and `criticals changed: 0`.
- The two tfsec exit codes are equal, and `tfsec-unchanged` prints.
- One commit per task (Tasks 1–12), each with the `Claude Opus 5.5 (1M context)` trailer.

- [ ] **Step 3: Record the user's state.** First check that nothing is running: `docker ps --format '{{.Names}}'` and `KIND_EXPERIMENTAL_PROVIDER=podman kind get clusters`. If either lists anything, **stop and ask the user**.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh; mkdir -p $LIVE
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
git -C /home/ben/repositories/Iverson rev-parse main > $LIVE/main-sha.txt; cat $LIVE/main-sha.txt
```

- [ ] **Step 4: The Helm create-user job against a throwaway StarRocks 4.1.6.** Run it as a fresh install, plant two tenant roles on `iverson_app` (one hashed-style, one legacy verbatim), then run it again as an upgrade.
  - **The script.** It is rendered from the branch chart on the local profile. FE TLS is a chart concern that the allin1 image does not serve, so `TLS` is emptied and `FE_HOST` points at the container's own FE. The step asserts both substitutions.
  - **The first live check.** This step is also the first live check of the `SHOW GRANTS` row format that the revoke parses.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh; mkdir -p $LIVE/job
python3 - $SCR/t12/final-values-local.yaml > $LIVE/job/script.sh <<'T13JOB'
import sys, yaml
d = next(d for d in yaml.safe_load_all(open(sys.argv[1])) if d and d["kind"] == "Job" and d["metadata"]["name"] == "iverson-starrocks-create-user")
s = d["spec"]["template"]["spec"]["containers"][0]["args"][0]
tls = 'TLS="--ssl-mode=VERIFY_IDENTITY --ssl-ca=/etc/starrocks-tls/ca.crt"'
fe = 'FE_HOST="iverson-starrocks-fe-service"'
assert s.count(tls) == 1 and s.count(fe) == 1, "the job script's TLS or FE_HOST line changed"
print(s.replace(tls, 'TLS=""').replace(fe, 'FE_HOST="127.0.0.1"'), end="")
T13JOB
free -g | sed -n 2p
docker run -d --name csr12-jobcheck --memory 6g --memory-swap 6g starrocks/allin1-ubuntu:4.1.6 >/dev/null
for i in $(seq 1 60); do docker exec csr12-jobcheck mysql -h127.0.0.1 -P9030 -uroot --batch --skip-column-names -e "SHOW BACKENDS" 2>/dev/null | awk -F'\t' '{print $9}' | command grep -q true && break; sleep 5; done
docker cp $LIVE/job/script.sh csr12-jobcheck:/tmp/job.sh
J="docker exec -e ROOT_PW=jobcheck-root -e APP_PW=jobcheck-app -e PROVISIONER_PW=jobcheck-prov csr12-jobcheck sh /tmp/job.sh"
$J > $LIVE/job/fresh.log 2>&1; echo "fresh exit=$?"
docker exec -e MYSQL_PWD=jobcheck-root csr12-jobcheck mysql -h127.0.0.1 -P9030 -uroot --batch -e "
  CREATE ROLE IF NOT EXISTS role_tenant_tenant_smoke_test_0123456789abcdefghijklmno;
  CREATE ROLE IF NOT EXISTS role_tenant_tenant_smoke_test;
  GRANT role_tenant_tenant_smoke_test_0123456789abcdefghijklmno TO USER 'iverson_app'@'%';
  GRANT role_tenant_tenant_smoke_test TO USER 'iverson_app'@'%';
  SHOW GRANTS FOR 'iverson_app'@'%';" > $LIVE/job/planted.txt 2>&1; cat $LIVE/job/planted.txt
$J > $LIVE/job/rerun.log 2>&1; echo "rerun exit=$?"; command grep '^revoking role' $LIVE/job/rerun.log
$J > $LIVE/job/rerun2.log 2>&1; echo "second rerun exit=$? revokes=$(command grep -c '^revoking role' $LIVE/job/rerun2.log)"
docker exec -e MYSQL_PWD=jobcheck-root csr12-jobcheck mysql -h127.0.0.1 -P9030 -uroot --batch -e "SHOW GRANTS FOR 'iverson_app'@'%'; SHOW ROLES;"
docker exec -e MYSQL_PWD=jobcheck-app csr12-jobcheck mysql -h127.0.0.1 -P9030 -uiverson_app --batch --skip-column-names -e "SHOW BACKENDS" | wc -l
docker rm -f csr12-jobcheck >/dev/null && echo removed
```

Expected:
- `fresh exit=0`.
- `planted.txt` shows a row with `Catalog` `NULL` whose `Grants` reads `GRANT 'role_tenant_tenant_smoke_test_0123456789abcdefghijklmno', 'role_tenant_tenant_smoke_test' TO 'iverson_app'@'%'` (the role order may differ, and `'public'` may appear), plus the `OPERATE ON SYSTEM` row. **If the role row has another shape**, record it, stop, and report it: Task 12's `sed` and its `t12_job.sh` fixtures assume this one.
- `rerun exit=0`, with two `revoking role` lines, one per planted role.
- `second rerun exit=0 revokes=0`: the revoke is idempotent.
- `SHOW GRANTS` now lists only `OPERATE ON SYSTEM` (and at most `public`). `SHOW ROLES` still lists both planted roles: they are revoked from the user, not dropped.
- The `SHOW BACKENDS` line count as `iverson_app` is `1`: `/health` keeps working.
- `removed`.

- [ ] **Step 5: Stand up the isolated compose project `csr12` from the worktree.** It uses round 11's pattern:
  - a separate project name, with `container_name:` stripped;
  - `--project-directory` on the worktree;
  - the worktree's own generated `.env`, which `generate-compose-secrets.sh` writes into the worktree, never into the main checkout's `Iverson.Server/.env`;
  - its own image name, so the user's `iverson-api` images are never re-tagged.

  Run `$DC up` with `run_in_background`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
cat >> /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh <<'T13ENV'
ENVF=$WT/Iverson.Server/.env
DC="docker compose -p csr12 -f $LIVE/compose.yml --project-directory $WT/Iverson.Server --env-file $ENVF"
val() { command grep "^$1=" "$ENVF" | cut -d= -f2-; }
scratch_env() {
  export HOME=$LIVE/home NUGET_PACKAGES=/home/ben/.nuget/packages PYTHONUSERBASE=/home/ben/.local
  export GOMODCACHE=/home/ben/go/pkg/mod MAVEN_OPTS=-Dmaven.repo.local=/home/ben/.m2/repository npm_config_cache=/home/ben/.npm
}
T13ENV
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh; mkdir -p $LIVE/home; umask 077
bash $WT/scripts/generate-compose-secrets.sh >/dev/null
echo "tenant key length: $(val IVERSON_STARROCKS_TENANT_KEY | tr -d '\n' | wc -c)"
command grep -v container_name $WT/Iverson.Server/docker-compose.yml | sed 's/^    image: iverson-api$/    image: csr12-api/' > $LIVE/compose.yml
command grep -c 'image: csr12-api' $LIVE/compose.yml
$DC config --quiet && echo compose-parses
free -g | sed -n 2p
$DC up -d --build > $LIVE/up.log 2>&1; echo "up exit=$?" >> $LIVE/up.log
```

Expected:
- `tenant key length: 64`;
- `2` (the api and worker images renamed);
- `compose-parses`;
- `up exit=0` at the end of `up.log`, and within 15 minutes `docker inspect -f '{{.State.Health.Status}}' csr12-iverson-api-1` prints `healthy`.

- [ ] **Step 6: Compose: order, the Postgres split and the revoke on a real stack.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
echo "C1 init: $(docker inspect -f '{{.State.ExitCode}}' csr12-starrocks-init-1) worker: $(docker inspect -f '{{.State.Health.Status}}' csr12-iverson-worker-1) api: $(docker inspect -f '{{.State.Health.Status}}' csr12-iverson-api-1)"
python3 -c "
import subprocess, datetime
t = lambda c: subprocess.run(['docker', 'inspect', '-f', '{{.State.StartedAt}}', c], capture_output=True, text=True).stdout.strip()[:26]
w, a = t('csr12-iverson-worker-1'), t('csr12-iverson-api-1')
print('C1 api started after worker:', a > w, w, a)"
PSQL="docker exec csr12-postgres-1 psql -U iverson -d iverson -tA"
echo "C2 role: $($PSQL -c "SELECT rolcanlogin, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb FROM pg_roles WHERE rolname = 'iverson_api'")"
echo "C2 member: $($PSQL -c "SELECT pg_has_role('iverson_api', 'iverson_runtime', 'MEMBER'), pg_has_role('iverson_api', 'iverson_maintenance', 'MEMBER')")"
echo "C2 owns: $($PSQL -c "SELECT count(*) FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = 'iverson_api'")"
echo "C3 grants: $($PSQL -c "SELECT has_database_privilege('iverson_api', 'iverson', 'CONNECT'), has_table_privilege('iverson_api', '\"IversonReconciliationQueue\"', 'INSERT'), has_column_privilege('iverson_api', '\"IversonReconciliationQueue\"', '\"Payload\"', 'SELECT'), has_column_privilege('iverson_api', '\"IversonDlqMessages\"', '\"MessageValue\"', 'SELECT'), has_table_privilege('iverson_api', '_iverson_schema', 'SELECT'), has_table_privilege('iverson_api', '_iverson_schema', 'INSERT')")"
echo "C4 sessions: $($PSQL -c "SELECT string_agg(DISTINCT usename, ',' ORDER BY usename) FROM pg_stat_activity WHERE datname = 'iverson' AND usename IN ('iverson', 'iverson_api')")"
echo "C5 seed: $($PSQL -c "SELECT count(*) FROM \"IversonTenants\" WHERE \"Id\" IN ('tenant_loadtest', 'tenant_webtest', 'tenant_admin', 'tenant_smoke_test', 'tenant_bypass')")"
docker logs csr12-iverson-api-1 2>&1 | command grep -c -E '42501|permission denied' | sed 's/^/C6 api permission errors: /'
docker exec csr12-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch -e "SHOW GRANTS FOR 'iverson_app'@'%';"
```

Expected:
- `C1 init: 0 worker: healthy api: healthy`, and `C1 api started after worker: True`.
- `C2 role: t|f|f|f|f`, `C2 member: t|f`, `C2 owns: 0`.
- `C3 grants: t|t|f|f|t|f`: CONNECT, outbox insert and registry read are granted; outbox payloads, `MessageValue` and registry writes are not.
- `C4 sessions: iverson,iverson_api`: the worker as the owner, the api as its own login.
- `C5 seed: 5`: the worker seeded the legacy tenants.
- `C6 api permission errors: 0`.
- `iverson_app`'s grants are `OPERATE ON SYSTEM` only, plus at most `public`.

- [ ] **Step 7: Compose: register, write, read and cross-tenant denial.** Build a scratch console. It does the following:
  1. registers `Csr12Note` with the loadtest service token, which has `schema_admin`. The registration goes through the worker's queue (Task 7), and the console prints its latency.
  2. The type is owner-scoped (`OwnerId`) and gives the `iverson-loadtest-bypass` group `CanReadAll`, so the bypass user reads every row of its own tenant. Its denial of the smoke-test user's row is therefore the tenant boundary, not ownership.
  3. Posts one note as the smoke-test user (`tenant_smoke_test`) and one as the bypass user (`tenant_bypass`).
  4. Reads them back through Postgres (`Get`) and StarRocks (`Search`).

  The acting-user tokens are minted with `HOME=$LIVE/home`, so their TOTP enrolment cache is this stack's own.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
mkdir -p $LIVE/probe && cd $LIVE/probe
cat > probe.csproj <<'T13PROJ'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="/home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" /></ItemGroup></Project>
T13PROJ
cat > Program.cs <<'T13PROG'
// env: TOKEN (service access token), ACTING (acting-user token). args: grpcUrl mode(register|post|get|search) [title|key]
using Google.Protobuf.WellKnownTypes; using Grpc.Core; using Grpc.Net.Client; using Iverson.Client.Contracts;
using var channel = GrpcChannel.ForAddress(args[0]);
var service = new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" } };
var acting = new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" },
                            { "x-acting-user-authorization", $"Bearer {Environment.GetEnvironmentVariable("ACTING")}" } };
var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
var search = new ObjectSearchService.ObjectSearchServiceClient(channel);
switch (args[1])
{
    case "register":
        var td = new TypeDescriptor { TypeName = "Csr12Note", Authorization = new AuthorizationRules { OwnerField = "OwnerId" } };
        td.Authorization.RowPermissions.Add(new RowPermission { Role = "iverson-loadtest-bypass", CanReadAll = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor { Name = "Title", ObjectType = ObjectType.String });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await mapping.RegisterSchemaAsync(new SchemaRequest { RootType = td }, service);
        Console.WriteLine($"registered in {sw.ElapsedMilliseconds} ms"); break;
    case "post":
        var posted = await mapping.PostAsync(new MappingWriteRequest { TypeName = "Csr12Note",
            Payload = new Struct { Fields = { ["Title"] = Value.ForString(args[2]) } } }, acting);
        Console.WriteLine($"posted id={posted.Data.Fields["Id"].StringValue}"); break;
    case "get":
        try
        {
            var got = await mapping.GetAsync(new MappingGetRequest { TypeName = "Csr12Note", Key = args[2] }, acting);
            Console.WriteLine($"get title={got.Data.Fields["Title"].StringValue}");
        }
        catch (RpcException ex) { Console.WriteLine($"get status={ex.StatusCode}"); }
        break;
    case "search":
        var titles = new List<string>();
        using (var call = search.Search(new SearchRequest { TypeName = "Csr12Note", PageSize = 50 }, acting))
            await foreach (var r in call.ResponseStream.ReadAllAsync())
                titles.Add(r.Data.Fields["Title"].StringValue);
        Console.WriteLine($"search rows={titles.Count} titles={string.Join(",", titles.Order())}"); break;
}
T13PROG
systemd-run --user --scope -q -p MemoryMax=3G dotnet build -c Release -p:UseSharedCompilation=false 2>&1 | command grep -E " error |Build succeeded" | head -3; dotnet build-server shutdown >/dev/null 2>&1
cat > $LIVE/tokens.sh <<'T13TOK'
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
export TOKEN="$(curl -s -H 'Host: authentik-server:9000' -d grant_type=client_credentials -d client_id=dev-iverson-loadtest-client-id \
  --data-urlencode client_secret="$(val IVERSON_LOADTEST_CLIENT_SECRET)" -d scope="schema_admin tenant_id_loadtest" \
  http://localhost:9000/application/o/token/ | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
MINT=$WT/Iverson.Server/deploy/scripts/mint_acting_user_token.py
export SMOKE="$(HOME=$LIVE/home IVERSON_ACTING_USER_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)" python3 $MINT --target compose 2>/dev/null | tail -1)"
export BYPASS="$(HOME=$LIVE/home IVERSON_ACTING_USER_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)" python3 $MINT --target compose --username iverson-loadtest-bypass-user 2>/dev/null | tail -1)"
T13TOK
source $LIVE/tokens.sh
P="dotnet $LIVE/probe/bin/Release/net10.0/probe.dll http://localhost:8080"
$P register
SID="$(ACTING=$SMOKE $P post csr12-smoke | sed -n 's/^posted id=//p')"; echo "smoke id=$SID"
BID="$(ACTING=$BYPASS $P post csr12-bypass | sed -n 's/^posted id=//p')"; echo "bypass id=$BID"
echo "L1 smoke reads own:     $(ACTING=$SMOKE $P get $SID)"
echo "L2 bypass reads smoke:  $(ACTING=$BYPASS $P get $SID)"
echo "L3 smoke reads bypass:  $(ACTING=$SMOKE $P get $BID)"
for i in $(seq 1 36); do s="$(ACTING=$SMOKE $P search 2>&1)"; b="$(ACTING=$BYPASS $P search 2>&1)"
  echo "$s" | command grep -q 'rows=1' && echo "$b" | command grep -q 'rows=1' && break; sleep 5; done
echo "L4 smoke search:  $s"
echo "L5 bypass search: $b"
docker exec csr12-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch --skip-column-names -e "SHOW USERS;" | command grep -c "^'iverson_t_" | sed 's/^/L6 tenant logins: /'
U="$(docker exec csr12-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch --skip-column-names -e "SHOW USERS;" | command grep "^'iverson_t_" | head -1)"
N="$(echo "$U" | sed "s/^'\([^']*\)'.*/\1/")"; echo "L6 $N is ${#N} characters"
docker exec csr12-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch -e "SHOW GRANTS FOR $U; SHOW PROPERTY FOR '$N' LIKE 'session.%'; SHOW GRANTS FOR 'iverson_app'@'%';"
echo "L7 recorded: $(docker exec csr12-postgres-1 psql -U iverson -d iverson -tA -c "SELECT string_agg(\"TenantId\", ',' ORDER BY \"TenantId\") FROM \"IversonStarRocksTenants\"")"
```

Expected:
- `Build succeeded`.
- `registered in N ms`, with N well under 25000 (about 1000 on a healthy worker).
- `smoke id=<uuid>` and `bypass id=<uuid>`.
- `L1 smoke reads own:     get title=csr12-smoke`.
- `L2 bypass reads smoke:  get status=NotFound`. The bypass user's group has `CanReadAll`, so this is the tenant boundary under `iverson_api` and RLS.
- `L3 smoke reads bypass:  get status=NotFound`.
- Within three minutes, `L4 smoke search:  search rows=1 titles=csr12-smoke` and `L5 bypass search: search rows=1 titles=csr12-bypass`. Each tenant's StarRocks read, through its own login, sees only its own row.
- `L6 tenant logins: 2`, and the login name is `35 characters`.
- That login's `SHOW GRANTS` shows its tenant role (`role_tenant_tenant_…`) and nothing else, and `SHOW PROPERTY` shows `session.query_timeout` `180` and `session.exec_mem_limit` `4294967296`. `iverson_app` still holds `OPERATE ON SYSTEM` only.
- `L7 recorded: tenant_bypass,tenant_smoke_test`.

- [ ] **Step 8: Compose: `starrocks-init` revokes a role granted on a running stack.** Grant the smoke-test tenant's role to `iverson_app` by hand, as an earlier release did, and re-run `starrocks-init`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
R="$(docker exec csr12-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch --skip-column-names -e "SHOW ROLES;" | command grep '^role_tenant_tenant_smoke_test_' | head -1)"; echo "role=$R"
docker exec csr12-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot -e "GRANT $R TO USER 'iverson_app'@'%';"
$DC up --no-deps --force-recreate starrocks-init > $LIVE/init-rerun.log 2>&1; echo "rerun exit=$(docker inspect -f '{{.State.ExitCode}}' csr12-starrocks-init-1)"
docker logs csr12-starrocks-init-1 2>&1 | command grep '^revoking role'
docker exec csr12-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch -e "SHOW GRANTS FOR 'iverson_app'@'%';"
source $LIVE/tokens.sh; echo "L8 smoke search after: $(ACTING=$SMOKE dotnet $LIVE/probe/bin/Release/net10.0/probe.dll http://localhost:8080 search)"
```

Expected:
- `role=role_tenant_tenant_smoke_test_<25 base-36 characters>`.
- `rerun exit=0`, and one `revoking role role_tenant_tenant_smoke_test_…` line.
- `iverson_app` is back to `OPERATE ON SYSTEM` only.
- `L8 smoke search after: search rows=1 titles=csr12-smoke`: the tenant reads through its own login, unaffected.

- [ ] **Step 9: The full conformance harness** against the isolated stack, with every scenario and every language. It drives every data RPC through the api's `iverson_api` login. The environment follows the harness's own help text (`Iverson.ClientConformance/Program.cs:320-347`).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
( scratch_env
  export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id IVERSON_CLIENT_SECRET="$(val IVERSON_LOADTEST_CLIENT_SECRET)"
  export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/ IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)" IVERSON_OTHER_TENANT_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  cd $WT/Iverson.Server/Iverson.ClientConformance && systemd-run --user --scope -q -p MemoryMax=4G dotnet run ) > $LIVE/conformance.out 2>&1; echo "harness exit=$?"
tail -25 $LIVE/conformance.out
docker logs csr12-iverson-api-1 2>&1 | command grep -c -E '42501|permission denied' | sed 's/^/api permission errors after the harness: /'
```

Expected:
- `harness exit=0`, and every scenario row reads `ok` in all five language columns.
- `api permission errors after the harness: 0`. No RPC needed a grant beyond §6.2.

- [ ] **Step 10: Tear the compose project down.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
$DC down -v > $LIVE/down.log 2>&1; echo "down exit=$?"
docker rmi csr12-api localhost/csr12-api >/dev/null 2>&1; true
rm -f $WT/Iverson.Server/.env
docker ps -a --format '{{.Names}}' | command grep -c '^csr12-' | sed 's/^/csr12 containers left: /'
```

Expected: `down exit=0` and `csr12 containers left: 0`.

- [ ] **Step 11: Kind: a fresh install of the branch chart.** This is the laptop profile with Ollama off. The precedent is round 11's transport-network Task 9 Step 6. Run the slow commands with `run_in_background`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
cd $WT; free -g | sed -n 2p
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr12 iverson
python3 -c "import yaml; d=yaml.safe_load(open('Iverson.Server/deploy/helm/iverson/values-laptop.yaml')); d.setdefault('ollama',{})['enabled']=False; print(yaml.safe_dump(d), end='')" > $LIVE/laptop-no-ollama.yaml
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr12 iverson --model-images --values $LIVE/laptop-no-ollama.yaml
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson --create-namespace -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr12 --set worker.image.tag=csr12 --wait --timeout 30m; echo "install exit=$?"
kubectl -n iverson get pods --no-headers | awk '$3!="Running" && $3!="Completed"{print "NOT READY:", $0}'
ok=no; for i in $(seq 1 30); do [ "$(kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak shell -c "from authentik.flows.models import Flow; print(Flow.objects.filter(slug='default-authentication-flow').count())" 2>/dev/null | tail -1)" = "1" ] && { ok=yes; break; }; sleep 10; done; echo "stock flow present: $ok"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml > $LIVE/apply1.log 2>&1; echo "apply static exit=$?"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml > $LIVE/apply2.log 2>&1; echo "apply templated exit=$?"
```

Expected:
- `install exit=0`: `--wait` completed with the api Ready as `iverson_api`, before any hook ran.
- No `NOT READY` lines.
- `stock flow present: yes`.
- `apply static exit=0` and `apply templated exit=0`, following `docs/runbooks/csr11-identity-plane.md` §2. A 137 exit is a memory kill: re-run that command.

- [ ] **Step 12: Kind: the Postgres split, the hook and the readiness gating.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
cat > $LIVE/kind-checks.sh <<'T13KIND'
# Usage: bash kind-checks.sh <label>. The Postgres split, the hook and the api pod's env, on the running kind release.
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
L=$1
PSQL="kubectl -n iverson exec iverson-postgres-1 -c postgres -- psql -d iverson -tA"
echo "$L K1 role: $($PSQL -c "SELECT rolcanlogin, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb FROM pg_roles WHERE rolname = 'iverson_api'")"
echo "$L K1 member: $($PSQL -c "SELECT pg_has_role('iverson_api', 'iverson_runtime', 'MEMBER'), pg_has_role('iverson_api', 'iverson_maintenance', 'MEMBER')")"
echo "$L K1 owns: $($PSQL -c "SELECT count(*) FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = 'iverson_api'")"
echo "$L K2 grants: $($PSQL -c "SELECT has_database_privilege('iverson_api', 'iverson', 'CONNECT'), has_table_privilege('iverson_api', '\"IversonReconciliationQueue\"', 'INSERT'), has_column_privilege('iverson_api', '\"IversonReconciliationQueue\"', '\"Payload\"', 'SELECT'), has_column_privilege('iverson_api', '\"IversonDlqMessages\"', '\"MessageValue\"', 'SELECT'), has_table_privilege('iverson_api', '_iverson_schema', 'SELECT'), has_table_privilege('iverson_api', '_iverson_schema', 'INSERT')")"
echo "$L K3 sessions: $($PSQL -c "SELECT string_agg(DISTINCT usename, ',' ORDER BY usename) FROM pg_stat_activity WHERE datname = 'iverson' AND usename IN ('iverson', 'iverson_api')")"
echo "$L K3 seed: $($PSQL -c "SELECT count(*) FROM \"IversonTenants\" WHERE \"Id\" IN ('tenant_loadtest', 'tenant_webtest', 'tenant_admin', 'tenant_smoke_test', 'tenant_bypass')")"
echo "$L K4 hook: $(kubectl -n iverson get job iverson-authentik-revoke-cross-db -o jsonpath='{.status.succeeded}')"
echo "$L K5 api env: $(kubectl -n iverson get deploy iverson-api -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="ConnectionStrings__Postgres")].value}' | command grep -o 'Username=[a-z_]*') postgres-app refs: $(kubectl -n iverson get deploy iverson-api -o yaml | command grep -c postgres-app)"
echo "$L K5 worker restarts: $(kubectl -n iverson get pods -l app=iverson-worker -o jsonpath='{.items[*].status.containerStatuses[0].restartCount}')"
T13KIND
bash $LIVE/kind-checks.sh fresh
```

Expected:
- `fresh K1 role: t|f|f|f|f`, `fresh K1 member: t|f` and `fresh K1 owns: 0`.
- `fresh K2 grants: t|t|f|f|t|f`.
- `fresh K3 sessions: iverson,iverson_api` and `fresh K3 seed: 5`.
- `fresh K4 hook: 1`.
- `fresh K5 api env: Username=iverson_api postgres-app refs: 0`.
- `fresh K5 worker restarts:` any number. A restart before CNPG created the role is expected (Task 12's runbook, §2 item 3). Record it.

**Readiness gating.** Remove the api's outbox grant as the superuser, then roll the api: its new pod must stay not-ready. Then roll the worker, whose startup grants it again: the api's rollout must then complete.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
kubectl -n iverson exec iverson-postgres-1 -c postgres -- psql -d iverson -c 'REVOKE INSERT ON "IversonReconciliationQueue" FROM iverson_api'
kubectl -n iverson rollout restart deploy/iverson-api
kubectl -n iverson rollout status deploy/iverson-api --timeout=120s > /dev/null 2>&1; echo "G1 api rollout without the grant exit=$?"
kubectl -n iverson get pods -l app=iverson-api --no-headers | awk '{print "G1", $1, $2, $3}'
kubectl -n iverson rollout restart deploy/iverson-worker
kubectl -n iverson rollout status deploy/iverson-worker --timeout=10m > /dev/null 2>&1; echo "G2 worker rollout exit=$?"
kubectl -n iverson rollout status deploy/iverson-api --timeout=10m > /dev/null 2>&1; echo "G2 api rollout after the worker re-grants exit=$?"
echo "G2 grant: $(kubectl -n iverson exec iverson-postgres-1 -c postgres -- psql -d iverson -tA -c "SELECT has_table_privilege('iverson_api', '\"IversonReconciliationQueue\"', 'INSERT')")"
```

Expected:
- `G1 api rollout without the grant exit=1`. The `G1` lines show the new api pod `0/1 Running`, and the old pod still present: the rollout cannot replace it.
- `G2 worker rollout exit=0`, then `G2 api rollout after the worker re-grants exit=0`, and `G2 grant: t`.

- [ ] **Step 13: Kind: `CreateTenant` through the GRPCS edge, as `iverson_api`.** This reuses round 11's transport-network Task 9 Step 8 console, which calls `CreateTenant` through ingress-nginx (`https://iverson.local`, connected to `127.0.0.1:8443`, pinning the edge certificate). CreateTenant inserts into `IversonTenants` under the api's login.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
mkdir -p $LIVE/createtenant && cd $LIVE/createtenant
cat > createtenant.csproj <<'T13CTPROJ'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="/home/ben/repositories/Iverson/.worktrees/csr12-server/Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" /></ItemGroup></Project>
T13CTPROJ
cat > Program.cs <<'T13CTPROG'
// args: tenantId ; env TOKEN (admin-automation access token), EDGE_CERT (PEM file of the kind edge
// certificate, Secret iverson-api-tls). Calls CreateTenant through ingress-nginx (https://iverson.local,
// connected to 127.0.0.1:8443). Prints only the recovery link's host and path.
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Client.Contracts;

var edgeCert = X509Certificate2.CreateFromPem(File.ReadAllText(Environment.GetEnvironmentVariable("EDGE_CERT")!));
var handler = new SocketsHttpHandler
{
    ConnectCallback = async (_, ct) =>
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("127.0.0.1", 8443, ct);
        return new NetworkStream(socket, ownsSocket: true);
    },
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && cert.GetCertHashString() == edgeCert.GetCertHashString(),
    },
};
using var channel = GrpcChannel.ForAddress("https://iverson.local", new GrpcChannelOptions { HttpHandler = handler });
var client = new TenantLifecycleGrpcService.TenantLifecycleGrpcServiceClient(channel);
var tenant = await client.CreateTenantAsync(new CreateTenantRequest { TenantId = args[0], DisplayName = "CSR12 live check",
    AdminUsername = args[0] + "-admin", AdminEmail = args[0] + "-admin@iverson.local" },
    new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" } });
var link = new Uri(tenant.AdminRecoveryLink);
Console.WriteLine($"link host={link.Scheme}://{link.Authority} path={link.AbsolutePath}");
T13CTPROG
systemd-run --user --scope -q -p MemoryMax=3G dotnet build -c Release -p:UseSharedCompilation=false 2>&1 | command grep -E " error |Build succeeded" | head -3; dotnet build-server shutdown >/dev/null 2>&1
cat > $LIVE/createtenant.sh <<'T13CT'
# Usage: bash createtenant.sh <tenantId>. Mints an admin-automation token and calls CreateTenant through the kind edge.
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
kubectl -n iverson port-forward svc/iverson-authentik 19000:9000 > $LIVE/pf-ak.log 2>&1 & PF=$!
sleep 3
kubectl -n iverson get secret iverson-api-tls -o jsonpath='{.data.tls\.crt}' | base64 -d > $LIVE/edge-cert.pem
CID="$(kubectl -n iverson get secret iverson-authentik-admin-automation-client -o jsonpath='{.data.client-id}' | base64 -d)"
export TOKEN="$(curl -s -H 'Host: iverson-authentik:9000' http://127.0.0.1:19000/application/o/token/ -d grant_type=client_credentials -d client_id="$CID" \
  -d client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer \
  --data-urlencode client_assertion="$(kubectl -n iverson create token iverson-admin-automation --audience iverson-authentik-admin-automation --duration 10m)" \
  -d scope="admin schema_admin tenant_id_admin" | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
EDGE_CERT=$LIVE/edge-cert.pem dotnet $LIVE/createtenant/bin/Release/net10.0/createtenant.dll "$1"
kill $PF
echo "row: $(kubectl -n iverson exec iverson-postgres-1 -c postgres -- psql -d iverson -tA -c "SELECT \"Id\" || '|' || \"Status\" FROM \"IversonTenants\" WHERE \"Id\" = '$1'")"
T13CT
bash $LIVE/createtenant.sh csr12-fresh
```

Expected:
- `Build succeeded`.
- `link host=http://authentik.iverson.local path=/if/flow/iverson-recovery/`.
- `row: csr12-fresh|active`.

- [ ] **Step 14: Kind: an upgrade from current `main`.** Delete the fresh cluster, install `main`'s chart and image on a new one, then `helm upgrade` to the branch. `main` is exported with `git archive` into `$LIVE/main-src`, so no worktree is added. `build-and-load-image.sh` builds from the tree its own copy sits in.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
KIND_EXPERIMENTAL_PROVIDER=podman kind delete cluster --name iverson
rm -rf $LIVE/main-src && mkdir -p $LIVE/main-src
git -C /home/ben/repositories/Iverson archive --format=tar $(cat $LIVE/main-sha.txt) | tar -x -C $LIVE/main-src
cd $LIVE/main-src; free -g | sed -n 2p
KIND_EXPERIMENTAL_PROVIDER=podman kind create cluster --name iverson --config Iverson.Server/deploy/kind/kind-config.yaml
bash Iverson.Server/deploy/kind/setup.sh
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr12main iverson
TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr12main iverson --model-images --values $LIVE/laptop-no-ollama.yaml
cd $LIVE/main-src/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade --install iverson . -n iverson --create-namespace -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr12main --set worker.image.tag=csr12main --wait --timeout 30m; echo "main install exit=$?"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/recovery-flow.yaml custom/reputation.yaml > $LIVE/apply3.log 2>&1; echo "apply static exit=$?"
kubectl -n iverson exec deploy/iverson-authentik-worker -c worker -- ak apply_blueprint custom/superusers.yaml custom/orchestrator-federation.yaml custom/service-clients/service-clients.yaml custom/service-clients/admin-automation.yaml > $LIVE/apply4.log 2>&1; echo "apply templated exit=$?"
echo "pre api login: $(kubectl -n iverson get deploy iverson-api -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="ConnectionStrings__Postgres")].value}' | command grep -o 'Username=[a-z_]*') role exists: $(kubectl -n iverson exec iverson-postgres-1 -c postgres -- psql -d iverson -tA -c "SELECT count(*) FROM pg_roles WHERE rolname = 'iverson_api'")"
bash $LIVE/createtenant.sh csr12-pre
cd $WT; TMPDIR=/var/tmp bash Iverson.Server/deploy/kind/build-and-load-image.sh csr12 iverson
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm upgrade iverson . -n iverson -f values-laptop.yaml --set ollama.enabled=false \
  --set api.hpa.maxReplicas=1 --set api.image.tag=csr12 --set worker.image.tag=csr12 --wait --timeout 30m; echo "upgrade exit=$?"
kubectl -n iverson get pods --no-headers | awk '$3!="Running" && $3!="Completed"{print "NOT READY:", $0}'
bash $LIVE/kind-checks.sh upgrade
bash $LIVE/createtenant.sh csr12-post
echo "pre-upgrade tenant: $(kubectl -n iverson exec iverson-postgres-1 -c postgres -- psql -d iverson -tA -c "SELECT \"Id\" || '|' || \"Status\" FROM \"IversonTenants\" WHERE \"Id\" = 'csr12-pre'")"
```

Expected:
- `main install exit=0`, `apply static exit=0` and `apply templated exit=0`.
- `pre api login: Username=iverson role exists: 0`: main's api uses the owner.
- `link host=… path=/if/flow/iverson-recovery/` and `row: csr12-pre|active`.
- `upgrade exit=0`. `--wait` completed: CNPG created `iverson_api` on the existing cluster, the new worker granted it, and the new api pod became Ready on it. The hooks ran after that. No `NOT READY` lines.
- The `upgrade K1`–`K5` lines read as Step 12's `fresh` ones: `Username=iverson_api postgres-app refs: 0`, and `K4 hook: 1` after the upgrade's run of the hook.
- `link host=… path=/if/flow/iverson-recovery/` and `row: csr12-post|active`.
- `pre-upgrade tenant: csr12-pre|active`: a row written by main's owner login is read back through the upgraded stack.

- [ ] **Step 15: Tear down and diff the user's state.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/a8dc481d-50d9-445a-974d-18c6594e3306/scratchpad/csr12-sdd/env.sh
KIND_EXPERIMENTAL_PROVIDER=podman kind delete cluster --name iverson; KIND_EXPERIMENTAL_PROVIDER=podman kind get clusters
docker rmi docker.io/library/iverson-api:csr12 localhost/iverson-api:csr12 docker.io/library/iverson-api:csr12main localhost/iverson-api:csr12main >/dev/null 2>&1; true
rm -rf $LIVE/main-src
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
out="$(docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -23 $LIVE/before-images.txt -)"; [ -z "$out" ] && echo no-image-removed-or-repointed || printf 'CHANGED:\n%s\n' "$out"
echo "new images:"; docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -13 $LIVE/before-images.txt -
git -C $WT status --porcelain
```

Expected:
- `No kind clusters found`.
- `containers-unchanged`, `volumes-unchanged` and `networks-unchanged`.
- `no-image-removed-or-repointed`.
- An empty `git status`.

Then remove whatever this task built:
- any `*csr12*` image still listed under "new images";
- any `iverson-tei-model:*` image this task built;
- any untagged `<none>` image created during this task's build windows (check `docker image inspect <id> --format '{{.Created}}'`).

Newly pulled base images (the kind node, Calico, StarRocks 4.1.6 if it was not present) are acceptable; list them.

## Known issues inherited from spec

- **No StarRocks tenant data from before round 12.** No stack holds any (user, 2026-10-16, CDR-1 §3.1), so the design has no backfill. An existing dev stack must start StarRocks fresh (compose `down -v`, or a new kind cluster).
- **A compromised api or worker process can still reach every tenant.** The api derives any tenant's StarRocks password; the worker keeps owner, BYPASSRLS and provisioning rights. This is inherent to a single multi-tenant service (user decision 2026-10-16).
- **Model-change recovery for some owned types.** Owned types whose tables still hold rows another tenant wrote before `02abddb3` (16 Sep – 9 Oct) can't be adopted until those rows are removed.
- **Tenant deletion leaves the StarRocks database, role and login in place**, as today. The api refuses deleted tenants.
- **`OPERATE ON SYSTEM` stays on `iverson_app`,** which now holds no data rights. Round 11's accepted residual is closed in substance.
- **Registration needs a running worker.** With the worker down, RegisterSchema returns `Unavailable` after 15 s.
- **The size guard now applies to .NET camelCase writes** (release note, §2.1).
- **FE connections** grow with concurrently active tenants: at most 10 per tenant per pod, against 4,096 per FE and a per-user default of 1,024.
