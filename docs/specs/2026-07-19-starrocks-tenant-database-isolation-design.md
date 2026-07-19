# StarRocks Tenant Database Isolation (Part B3) — Design Spec

**Status:** Draft, pending user review
**Date:** 2026-07-19
**Part of:** Tenant Data Isolation & Compliance Readiness initiative, Part B (DB-level defense-in-depth), sub-part B3 of 3 — see [Scope and decomposition](#scope-and-decomposition)

## Motivation

Part A (`2026-07-17-mandatory-tenant-boundary-design.md`) made every registered schema's tenant boundary mandatory and enforced it at the application layer. Part B1 (`2026-07-18-postgres-row-level-security-design.md`) added a database-enforced backstop for Postgres; Part B2 (`2026-07-18-qdrant-tenant-collection-isolation-design.md`) did the equivalent for Qdrant, via a per-tenant collection topology. This spec does the equivalent for StarRocks: today, `StarRocksQueryBuilder`/`StarRocksPipelineBuilder` inject a tenant-value `WHERE` predicate at the application layer, but nothing beneath that layer enforces it — if the app forgot to build the predicate, or computed the wrong tenant value, a shared StarRocks table would return another tenant's rows with nothing to stop it.

## Scope and decomposition

See B1's spec for the full three-way split of Part B and why StarRocks (B3) was deferred as materially larger. B1's spec named the deferred plan as "Apache Ranger integration plus the per-tenant StarRocks authentication redesign it depends on." **This design does not use Ranger** — see "Why not Apache Ranger" below for the investigation and reasoning. The user explicitly chose to scope this as one spec covering both the identity foundation and the enforcement mechanism together, rather than splitting further into sub-parts the way B1/B2 themselves were split out of the original "Part B."

Explicitly out of scope for this spec: any change to Part A's application-level `WHERE`-clause enforcement (unchanged, this is an additive backstop only); backfilling tenant values or re-homing rows for pre-existing data in the current shared `iverson` StarRocks database; StarRocks transport TLS (a real pre-existing gap, structurally identical to the one B2 closed for Qdrant, but not requested as part of this design — see Known limitations).

## Design

### Why not Apache Ranger

B1's spec found StarRocks has no native row-level security and named Apache Ranger as "the only documented path." Re-verified fresh for this spec rather than trusted from B1's citation (the same discipline that caught B2's Qdrant premise being wrong):

- StarRocks still ships no native row-level security today. A `policy_references` system view exists, referencing "row access policy" and "column masking policy," but StarRocks' own docs mark that feature as **announced, not yet available** — this premise still holds.
- Apache Ranger integration is real and version-compatible (from v3.1.9; this repo runs 4.1.1) — but StarRocks' own docs confirm it passes the connecting user's identity to Ranger per query, meaning a per-tenant identity mechanism is a genuine prerequisite for tenant-differentiated row filters, not an implementation detail Ranger handles on its own.
- Two additional problems surfaced during this design's own investigation, not just B1's: (1) Ranger's own service-definition JSON (`ranger-servicedef-starrocks.json`) scopes its `rowFilterDef` by resource (catalog/database/table) and evaluates against the connecting principal, with no confirmed role-based scoping for row filters specifically — meaning the only session-identity mechanism proven safe for connection pooling (`SET ROLE`, reversible mid-session) has **unverified** behavior with Ranger's row-filter evaluation, while the mechanism proven to change what Ranger sees (`EXECUTE AS ... WITH NO REVERT`) is irreversible per-connection and would force either disabling connection pooling or a custom pool-partitioning scheme; (2) Ranger would be the first component in this entire initiative requiring a brand-new external service (its own admin service, database, and web UI) — B1 and B2 both landed on native, already-shipped mechanisms with zero new infrastructure.

Given that, this design uses **per-tenant physical databases with StarRocks' own native RBAC** — the same "give each tenant an isolated physical resource, enforced by an already-shipped mechanism" pattern B2 used for Qdrant collections, adapted to what StarRocks actually offers (grantable database/table objects, not a payload filter).

### The core mechanism: per-tenant database + per-tenant role

One physical StarRocks database per tenant, `iverson_tenant_{tenantId}`, holding every registered type's table for that tenant (StarRocks' natural schema unit is a database containing many tables — unlike Qdrant, there is no reason to fragment further to one physical resource per (type, tenant)). One StarRocks role per tenant, `role_tenant_{tenantId}`, granted:

```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON `iverson_tenant_{tenantId}`.* TO ROLE `role_tenant_{tenantId}`;
GRANT CREATE TABLE ON DATABASE `iverson_tenant_{tenantId}` TO ROLE `role_tenant_{tenantId}`;
```

The existing shared connection (`iverson_app`, unchanged connection string, unchanged `Database=iverson` default) holds every tenant role once that tenant has been provisioned, plus the built-in `user_admin` role (for provisioning — see below). **None of these roles are active by default.** Each tenant-scoped call activates exactly one role for its duration:

`SET ROLE` changes which grants are active; it does not change which database a query addresses. Every table reference this design generates is therefore **fully qualified with the tenant database name** — `` `iverson_tenant_{tenantId}`.`{tableName}` `` — rather than relying on `USE` or the connection's current database (which stays `iverson`, unchanged, throughout):

```sql
SET ROLE `role_tenant_{tenantId}`;
SELECT * FROM `iverson_tenant_{tenantId}`.`article` WHERE ...;
SET ROLE NONE;
```

This mirrors B1's Postgres `SET LOCAL ROLE` pattern structurally (activate → scoped operation → deactivate on the same connection) rather than B2's per-call credential-minting pattern — StarRocks' native RBAC checks a session's currently active role(s) directly, with no analog to Qdrant's JWT needed. `SET ROLE`/`SET ROLE NONE` is fully reversible within a session and does not change `CURRENT_USER()`, so the existing single connection string and existing ADO.NET connection-pooling behavior in `StarRocksRepository` need no structural change — no per-tenant password, no per-tenant connection string, no pool partitioning.

**Fail-closed is the structural default, not a manufactured sentinel, and does not depend on `iverson_app`'s pre-existing standing grant on the old shared `iverson` database.** A connection with no role active has zero grant on *every* tenant database — confirmed directly. This is a stronger default than B1/B2 needed to build: neither a fabricated nonexistent name (B2's `__no-tenant-claim__` sentinel) nor an explicit app-level null guard is required here. When a tenant-scoped call is reached with a null tenant value, the design simply **does not issue `SET ROLE`, and has no tenant database name to fully-qualify a table reference with** — there is no valid query to build, not merely a denied one. (Part A's evaluator already denies upstream whenever a tenant-scoped schema's request is missing a `tenant_id` claim, so this should be structurally unreachable in practice — this is defense in depth, mirroring B1/B2's own stated posture, not a live path.) Because every table reference this design generates is fully qualified with the tenant database name, `iverson_app`'s pre-existing standing grant on the shared `iverson` database (`job-create-user.yaml:72`, untouched by this design) is never reachable by any query this design builds — fail-closed does not rest on that grant's absence or removal.

### Tenant-ID-as-SQL-identifier risk (new — B1/B2 didn't have this)

B1 used `tenant_id` as a bound query *parameter* (`set_config('app.tenant_id', ...)`); B2 used it inside a JSON/protobuf field value. This design splices `tenant_id` directly into **SQL identifier text** — database names, role names, `SET ROLE` statements — none of which StarRocks/MySqlConnector can parameterize. Authentik's `tenant_id` claim is operator-set free text (`user.attributes.get("tenant_id")`, `blueprints-configmap-service-clients.yaml:45`), with no format constraint enforced anywhere today. Without validation, a crafted `tenant_id` value (containing a backtick, semicolon, or SQL comment sequence) is a genuine SQL-injection vector into DDL/DCL.

This design adds a strict allowlist check — `^[A-Za-z0-9_-]{1,52}$` — applied everywhere a `tenant_id` value is turned into a database name, role name, or `SET ROLE`/`GRANT` statement fragment. A `tenant_id` that fails this check is treated as absent for the purposes of this design (fail-closed denial via the no-role-active default above), not silently sanitized or truncated. The 52-character bound (not the round number 64) is deliberate: StarRocks caps general identifiers — including role names — at 64 characters total, and the longer of this design's two constructed prefixes, `role_tenant_` (12 characters), must fit inside that same 64-character budget alongside the tenant_id fragment itself. This validation is new; Part A's existing evaluator checks only for the claim's *presence*, not its shape.

### Provisioning: lazy, on first write, per (tenant, type)

Mirrors B2's `EnsureCollectionAsync` pattern: there is no tenant registry (`tenant_id` is a claim value, not a row anywhere Part A created), so a tenant's database/role cannot be provisioned at schema-registration time — only discovered on first use. `EngagementStoreConsumer` gains a per-process idempotency cache (`HashSet<(string TenantId, string TableName)>`, mirroring the shape of B2's `_ensuredCollections`); on a cache miss, before the actual write:

```sql
SET ROLE user_admin;
CREATE DATABASE IF NOT EXISTS `iverson_tenant_{tenantId}`;
CREATE ROLE IF NOT EXISTS `role_tenant_{tenantId}`;
GRANT SELECT, INSERT, UPDATE, DELETE ON `iverson_tenant_{tenantId}`.* TO ROLE `role_tenant_{tenantId}`;
GRANT CREATE TABLE ON DATABASE `iverson_tenant_{tenantId}` TO ROLE `role_tenant_{tenantId}`;
GRANT ROLE `role_tenant_{tenantId}` TO USER 'iverson_app'@'%';
-- CREATE TABLE IF NOT EXISTS `iverson_tenant_{tenantId}`.`<type's table>`, same DDL
-- StarRocksSchemaManager.BuildCreateTableDdl already generates, fully qualified with the
-- tenant database name instead of the bare table name it emits today
SET ROLE NONE;
```

Every statement is idempotent (`IF NOT EXISTS`/re-`GRANT` — re-granting an already-held role succeeds silently, verified directly) so a second event for an already-provisioned (tenant, type) pair is a correctly-skipped no-op via the process cache, and even a cache miss on an already-provisioned pair (e.g. after a restart) is safe to re-run.

The database-level wildcard grant (`ON iverson_tenant_{tenantId}.*`) is issued once, when the tenant's database is first created — **not** re-issued when a second type's table is later created in that same tenant's database. Confirmed directly: a table created after the wildcard grant is immediately covered by it, with no additional `GRANT` needed.

`SchemaRegistrationOrchestrator` **stops** eagerly calling `starRocks.ApplyTableAsync` (`SchemaRegistrationOrchestrator.cs:61` today) for the same reason B2 stopped eagerly creating Qdrant collections — there is no longer a single "the" database for a type's table to be created in at registration time. Registration still validates schema shape (`tenant_field` mandatory presence, `ValidateFieldReference`, etc.) exactly as it does today; only the StarRocks table-creation side effect moves to the lazy path. **Unlike B2's equivalent change**, an existing test currently asserts the eager behavior (`SchemaRegistrationOrchestratorTests.cs:138`, `RegisterAsync_CallsApplyTableAsync_WithMatchingTableName`) and will need to be removed or rewritten — a concrete implementation-time task this design surfaces, not a design gap.

### Call-site wiring (6 wrap points)

**Read path — `StarRocksRepository.SearchAsync`/`AggregateAsync`/`GroupByAsync`/`PipelineAsync`** (`StarRocksRepository.cs:147,162,207,217`, all reached via `ObjectSearchGrpcService`): each method already receives an `authz` dictionary (`IReadOnlyDictionary<string, AuthorizationConstraint>?`) carrying the primary type's `TenantColumn`/`TenantValue`, built once per request at `ObjectSearchGrpcService.cs` (the `Constraints` dictionary construction, ~lines 476–494) and passed through unchanged since Part A's StarRocks enforcement task. The tenant *value* needs no new plumbing to reach these call sites — but the tenant *database name* it resolves to must additionally reach `StarRocksQueryBuilder.BuildSearch`/`BuildAggregate`/`BuildGroupBy` and `StarRocksPipelineBuilder.Build`, the functions that actually generate each `FROM` table reference (`StarRocksQueryBuilder.cs:81,157,714`): today bare (`` `{tableName}` ``), each becomes fully qualified (`` `iverson_tenant_{tenantId}`.`{tableName}` ``) using the same `TenantValue` these methods already receive. Each method wraps its existing `QueryAsync<T>` call in `SET ROLE`/`SET ROLE NONE` using the primary type's `TenantValue`, or skips both `SET ROLE` and query construction entirely when it's null (the fail-closed default above).

**Write path — `StarRocksRepository.UpsertAsync`/`DeleteAsync`** (`StarRocksRepository.cs:100,142`, reached only via `EngagementStoreConsumer`): both currently take no tenant parameter; both gain one. Both also gain the same fully-qualified-reference change as the read path: `UpsertAsync`'s `` INSERT INTO `{schema.TableName}` `` (`:132`) and `DeleteAsync`'s `` DELETE FROM `{tableName}` `` (`:144`) become `` INSERT INTO `iverson_tenant_{tenantId}`.`{schema.TableName}` `` / `` DELETE FROM `iverson_tenant_{tenantId}`.`{tableName}` `` respectively.

- `EngagementStoreConsumer.HandleUpsertAsync` (`:41`) already re-derives the ownership value from the authoritative Postgres row rather than trusting the event payload (`FetchAuthoritativeOwnerValueAsync`, `:100`) — that helper is already generic over field name (`ownerField` is just a `string` parameter). This design reuses it directly for tenant sourcing: `FetchAuthoritativeOwnerValueAsync(schema, schema.TenantColumn, ev.Key, ct)`, no new helper.
- `EngagementStoreConsumer.HandleDeleteAsync` (`:77`) currently does no payload parsing at all — it calls `sr.DeleteAsync(schema.TableName, schema.KeyColumn.Name, ev.Key)` (`:89`) directly. It gains a tenant-value extraction from `ev.PayloadJson`, the same pre-delete row snapshot `ObjectMappingGrpcService.Delete` publishes: `rowJson` is fetched and checked non-null (`ObjectMappingGrpcService.cs:192,195`), then flows unchanged into `_outboxPublisher.PublishAsync(EventType.Deleted, ..., rowJson, ...)` (`:239`) and from there into `EntityEvent.PayloadJson` (`OutboxPublisher.cs:37`) — the same chain B2 already traced and verified for the identical `ObjectMappingGrpcService.Delete` method, so `PayloadJson` is guaranteed non-null on every delete event `EngagementStoreConsumer` receives, exactly as it is for `IntelligenceStoreConsumer`.

Total: 4 read + 2 write = **6 wrap points**, the same count B2 arrived at for Qdrant, for the analogous structural reason (multiple read query shapes, one upsert-shaped write, one delete-shaped write).

### Admin/health-check paths are unaffected

`StarRocksHealthChecker.CheckHealthAsync` (`StarRocksHealthChecker.cs`) runs only `SELECT 1` and `SHOW BACKENDS` against the base connection, with no `USE <tenant-db>` and no role activation — confirmed by direct read, this backs the k8s readiness probe and must stay fast and tenant-agnostic. It is untouched by this design.

### Deployment/configuration

`job-create-user.yaml`'s existing post-install/post-upgrade Helm hook (already idempotent — `CREATE USER IF NOT EXISTS`, etc.) gains one line:

```sql
GRANT user_admin TO 'iverson_app'@'%';
```

Confirmed idempotent directly: re-running this grant on an already-granted user succeeds silently (no error), safe for repeated `helm upgrade` runs. `iverson_app` already holds `GRANT CREATE DATABASE ON CATALOG default_catalog` (`job-create-user.yaml:75`, existing) — reused as-is; no new catalog-level grant is needed.

**No new external service, no new secret.** This is the deliberate outcome of rejecting Ranger — the only new grant `iverson_app` needs is `user_admin`, a narrow, already-shipped built-in role.

### Known limitations (surfaced, not fixed by this design)

- **Database-count growth.** Every tenant now costs one physical StarRocks database (holding all of that tenant's type tables) — a real per-tenant resource-count cost, the same category of tradeoff B2 accepted for Qdrant's collection-per-tenant growth. No live tenants exist and tenant-count expectations are not yet known, so no mitigation (e.g. capping, sharding) is designed here — an accepted operational risk to revisit once real scale expectations exist.
- **The pre-existing shared `iverson` database's fate is not addressed.** Rows in the current single shared database predate this design; no backfill or migration path into per-tenant databases is designed. Whether the old shared database is kept as a dead relic or dropped is a deploy-time decision outside this spec's scope.
- **StarRocks transport TLS.** The MySQL wire protocol connection between the app and StarRocks is not TLS'd today — the same category of gap B2 found and closed for Qdrant, but not requested as part of this design. `SET ROLE` statements and query results cross the wire in plaintext, unchanged by this design. Flagged for awareness, not solved here.
- **Pre-Part-A legacy rows with a null tenant value.** Mirrors B1/B2's identical carve-out: such a row's upsert-path tenant re-derivation finds null, so the write is skipped via the fail-closed no-role-active default rather than reaching an unfiltered/unscoped table. Moot in practice today — no live tenant data exists yet.

## Verified assumptions

| Assumption | Verification | Result |
|---|---|---|
| This repo's StarRocks version (no native RLS, Ranger scope) | Read `docker-compose.yml:49` and `deploy/helm/iverson/charts/starrocks/values.yaml:3,12` directly | Confirmed — `4.1.1` in both, matching B1's stated "v4.1 line" premise. |
| StarRocks still has no native row-level security today (re-verified fresh, not trusted from B1's citation) | WebFetch of `docs.starrocks.io/docs/sql-reference/sys/policy_references/` | Confirmed — a `policy_references` view exists referencing row access/column masking policies, but the docs explicitly mark the feature itself as not yet available. |
| Ranger row-filter policies key off the connecting principal's identity, making per-tenant identity a genuine prerequisite (not an optional implementation choice) | WebFetch of `docs.starrocks.io/docs/administration/user_privs/authorization/ranger_plugin/`; WebFetch of the actual `ranger-servicedef-starrocks.json` service definition from GitHub | Confirmed — StarRocks "passes user information and required privileges to Apache Ranger" per query; the service def's `rowFilterDef` shows no confirmed role-based scoping for row filters specifically. |
| `SET ROLE`/`SET ROLE NONE` is fully reversible mid-session and does not change `CURRENT_USER()` | **Empirically tested** against a live throwaway `starrocks/allin1-ubuntu:4.1.1` container: activated `role_tenant_a`, then `role_tenant_b`, then `NONE`, on the same connection, asserting `CURRENT_ROLE()`/`CURRENT_USER()` after each | Confirmed — `CURRENT_USER()` stayed `app_svc` throughout; `CURRENT_ROLE()` switched freely and reversibly. |
| A connection with no role active has zero privilege on any tenant database (the fail-closed default this design relies on instead of a manufactured sentinel) | **Empirically tested** on the same live container: `app_svc` with no `SET ROLE` issued, attempting to read a tenant's table | Confirmed — denied with `Access denied ... ANY privilege(s) on DATABASE`, `Current role(s): NONE`. |
| `GRANT SELECT,INSERT,UPDATE,DELETE ON <db>.* TO ROLE <role>` (issued once, at database-creation time) covers a table created in that database *after* the grant, not only tables that existed at grant time | **Empirically tested**, waiting for the StarRocks BE node to fully register before creating the second table (an earlier attempt without waiting failed for an unrelated "no backend alive" reason, not a privilege error) | Confirmed — a table created after the wildcard grant was immediately insertable/selectable under the pre-existing role, no new grant needed. |
| `user_admin` + `db_admin` built-in roles together are the right provisioning grant, without over-granting | **Empirically tested**, then corrected: `db_admin` active (even without any tenant role) could directly read a tenant's table contents — a real privilege-escalation window during provisioning. Isolated by re-testing with `user_admin` alone (no `db_admin`) plus the existing `CREATE DATABASE ON CATALOG default_catalog` grant | **Wrong, corrected.** `db_admin` is dropped from the design entirely. `user_admin` alone (for `CREATE ROLE`/`GRANT ... TO ROLE`) plus the already-held catalog grant fully provisions a tenant, and `user_admin` alone — with no tenant role active — is correctly denied on tenant data, confirmed directly. |
| `StarRocksRepository.SearchAsync`/`AggregateAsync`/`GroupByAsync`/`PipelineAsync` are the only 4 read call sites, and no other caller invokes the underlying `QueryAsync`/`ExecuteAsync` directly (bypassing tenant scoping) | Repo-wide grep for `QueryAsync\b`/`ExecuteAsync\b` call sites in `Iverson.Api` (excluding tests) | Confirmed — no production caller besides `StarRocksRepository`'s own 4 read methods (which call `QueryAsync` internally) and the write path's `ExecuteAsync` (called only from `UpsertAsync`/`DeleteAsync`). |
| `AuthorizationConstraint.TenantValue` for the primary type is already available at each of the 4 read call sites before reaching `StarRocksRepository` | Read `ObjectSearchGrpcService.cs`'s `Search` (`:64`, passes `authz: auth.Constraints`) and the shared `Constraints` dictionary construction (`primaryDecision.TenantColumn, primaryDecision.TenantValue`, ~`:481-483`) directly; confirmed `Aggregate`/`GroupBy`/`Pipeline` follow the identical `authz:` parameter pattern | Confirmed — no new plumbing needed; this is Part A's existing StarRocks-authorization plumbing, reused as-is. |
| `EngagementStoreConsumer`'s existing `FetchAuthoritativeOwnerValueAsync` helper is generic enough to reuse directly for tenant re-derivation on upsert | Read `EngagementStoreConsumer.cs:100-116` directly — `ownerField` is a plain `string` parameter, used only as `doc.RootElement.TryGetProperty(ownerField, ...)` | Confirmed — reusable via `FetchAuthoritativeOwnerValueAsync(schema, schema.TenantColumn, ev.Key, ct)`, no new helper. |
| `ev.PayloadJson` is guaranteed non-null on every delete event `EngagementStoreConsumer.HandleDeleteAsync` receives | Traced `ObjectMappingGrpcService.Delete`'s `rowJson` fetch-and-null-check (`:192,195`) through to `PublishAsync(EventType.Deleted, ..., rowJson, ...)` (`:239`) and `OutboxPublisher.cs:37`'s `EntityEvent(..., payloadJson, ...)` construction — the identical method and field B2 already traced and verified for the same guarantee on `IntelligenceStoreConsumer`'s delete path | Confirmed — same event, same guarantee, applies unchanged to this design's consumer. |
| Removing `SchemaRegistrationOrchestrator`'s eager `starRocks.ApplyTableAsync` call (`:61`) doesn't silently break anything beyond one identifiable test | Read `SchemaRegistrationOrchestrator.cs:52-65` directly; grepped `SchemaRegistrationOrchestratorTests.cs` for `ApplyTableAsync`/`starRocks.` references | **Found a real difference from B2's equivalent check** (which found no such test): `RegisterAsync_CallsApplyTableAsync_WithMatchingTableName` (`:138`) currently asserts eager creation and will need updating or removal at implementation time — not a design blocker, but a concrete task this design surfaces. |
| Authentik's `tenant_id` claim has no existing format constraint, motivating this design's new validation | Read `deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml:45` (`return {"tenant_id": request.user.attributes.get("tenant_id")}`) directly | Confirmed — free-text user attribute, no format enforced anywhere today. |
| `IEngagementStoreQueryExecutor`/`IEngagementStoreEntityStore`/`IEngagementStoreSearchService` have exactly one production implementation | Repo-wide grep for `: IEngagementStore` / interface implementations | Confirmed — `StarRocksRepository` is the sole implementer; `StarRocksSchemaManager`/`StarRocksHealthChecker` implement different, unrelated interfaces (`IEngagementStoreSchemaManager`/`IEngagementStoreHealthCheck`) that this design doesn't touch. |
| Only `EngagementStoreConsumer` calls `StarRocksRepository.UpsertAsync`/`DeleteAsync` today | Repo-wide grep for `IEngagementStoreEntityStore` consumers in `Iverson.Api` (excluding tests) | Confirmed — `EngagementStoreConsumer.cs` is the only production caller; changing both methods' signatures has exactly one call site each to update. |
| `job-create-user.yaml`'s existing idempotent-hook pattern safely accommodates adding `GRANT user_admin TO 'iverson_app'@'%'` without erroring on Helm-upgrade re-runs | **Empirically tested** on a live container: issued the same `GRANT ... TO` a user twice in a row | Confirmed — second run succeeds silently, no error. |
| `StarRocksHealthChecker`/health-probe code paths never touch a tenant-specific database or table | Read `StarRocksHealthChecker.cs` in full | Confirmed — only `SELECT 1` and `SHOW BACKENDS` against the base connection; no `USE`, no role activation. Correctly out of scope. |
| New database/role names (`iverson_tenant_{id}`, `role_tenant_{id}`) don't collide with any existing StarRocks object name | Repo-wide grep for `Database=iverson`/`CREATE DATABASE` usages | Confirmed — the current shared database is the bare name `iverson` (`job-create-user.yaml:71`, connection strings), distinct from any `iverson_tenant_*` name by construction. |
| A 64-character `tenant_id` allowlist bound leaves room for this design's constructed prefixes without overflowing StarRocks' own identifier length limit | WebFetch of StarRocks' `System_limit` docs page | **Found during self-review, corrected.** General identifiers (including role names) are capped at 64 characters total, not per-fragment — the initially-drafted `{1,64}` bound for the `tenant_id` fragment alone would let a legitimate long tenant_id overflow the 64-char role-name budget once prefixed with `role_tenant_` (12 chars). Narrowed to `{1,52}`. |
| An active tenant role is denied on every *other* tenant's database, not just denied when no role is active at all (this design's central cross-tenant-isolation property, distinct from the no-role-active case above) | **Empirically tested** on the same live container used for the other role-based tests: with `role_tenant_x` active, attempted to read `iverson_tenant_y` | Confirmed — denied with `Access denied ... ANY privilege(s) on DATABASE iverson_tenant_y`, `Current role(s): [role_tenant_x]`. (Found missing from this table by critical-design-review round 1's span check — the evidence already existed from this same verification session but had not been written in; added here rather than left as an uncovered dependency.) |

## Testing approach

- Integration tests (a real StarRocks container, extending the existing `Iverson.StarRocks.Tests` conventions) proving the privilege boundary itself, not application logic: a session with `role_tenant_a` active is denied reading/writing `iverson_tenant_b`, independent of any `WHERE`-clause filter.
- Every generated statement (`StarRocksQueryBuilder`, `StarRocksPipelineBuilder`, `UpsertAsync`/`DeleteAsync`, `BuildCreateTableDdl`) references the fully-qualified tenant database name, not a bare table name — locking in the routing fix found during critical-design-review (round 1): a query for tenant A must never resolve against the connection's default database regardless of what standing grants that database happens to carry.
- The fail-closed default on both read and write: a session with no role active (the null-tenant-value carve-out) is denied on every tenant database — confirming this design needs no manufactured sentinel to fail closed.
- The wildcard grant genuinely covers a table created after the grant — the real-world case of a tenant's second registered type getting its first write.
- Lazy provisioning is idempotent: two events for the same (tenant, type) pair provision once, not twice, and a cache-miss re-run against an already-provisioned pair doesn't error.
- `user_admin` alone (the corrected, narrowed provisioning role), with no tenant role active, cannot read or write any tenant's data — locking in the privilege-escalation fix found during verification.
- `tenant_id` format validation rejects a malformed/malicious value (e.g. containing a backtick or semicolon) via fail-closed denial rather than executing it as part of a database/role name.
- `EngagementStoreConsumer`'s delete path correctly sources its tenant value from `ev.PayloadJson` and routes to the real tenant's database, not a Postgres re-fetch (which would find the row already gone).
- `SchemaRegistrationOrchestrator` no longer eagerly creates a StarRocks table at registration time; the existing `RegisterAsync_CallsApplyTableAsync_WithMatchingTableName` test is updated to reflect the new lazy-creation behavior.
- Part A's existing `WHERE`-clause tenant/owner filtering continues to function unchanged within a single tenant's database (no regression to the orthogonal, already-existing application-layer enforcement).
