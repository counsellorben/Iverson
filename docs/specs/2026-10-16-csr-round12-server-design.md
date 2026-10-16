# CSR round 12, sub-project 1: Server — design

**Source review:** `docs/criticalreviews/2026-10-16-iverson-critical-security-review-12.md` (at `f7eeba79`).
**Findings in scope:** #1 (table adoption), #2 (re-cased owner key), #7 (schema-owner race), #8 (popularity counts), #9 (decay score), #13 (TMA F3, store logins that span tenants), #18 (TMA F8, the enrichment half). Side fixes found during verification are listed in §2 and §5.
**Sibling sub-projects:** 2, Platform (#3–#6, #11, #12, #14–#16); 3, Supply chain (#10, #17).

## Decisions (user, 2026-10-16)

1. Fix every round-12 finding, split into three sub-projects. This is the first.
2. #13 must both **contain query bugs** (request-path SQL cannot leave the caller's tenant, enforced by connection rights) and **narrow the api** (the api process holds no owner, BYPASSRLS, DDL or provisioning credential).
3. **Approach α:** the worker runs every privileged request-driven operation (RegisterSchema's DDL and registry write, `/admin/reconcile/{type}`, DLQ replay). The api queues each one and waits.
4. Recommended options for #1, #2, #7, #8, #9 and #18, as set out below.
5. The revoke of `iverson_app`'s tenant roles runs in the **post-upgrade hook**, and new api pods become ready only after the worker's backfill.
6. RegisterSchema's wait is **capped below every edge's default timeout** (§5).
7. **Case-duplicate columns** (`Url` and `URL` on one type) are refused at registration. A stored type that already has them stays loaded, but a payload key that matches more than one column is refused.

## 1. Goals and non-goals

**Goals.**
- Close #1, #2, #7, #8, #9 and #18 in code.
- Close #13 as far as a single multi-tenant service allows:
  - the api's Postgres login is not the owner, has no BYPASSRLS and runs no DDL;
  - every StarRocks data connection carries exactly one tenant's role;
  - owner, maintenance and provisioning credentials exist only in the worker and the deploy hooks.

**Non-goals.**
- Stopping a compromised api or worker process from reaching every tenant. The api must derive any tenant's StarRocks password to serve that tenant, and the worker keeps owner and BYPASSRLS rights to re-derive tenants from authoritative rows (§10).
- Anything in sub-projects 2 and 3.

## 2. Payload keys (#2, plus side fixes)

### 2.1 Canonical keys at RPC entry

A new step, `PayloadKeyCanonicalizer.Canonicalize(schema, payload)`, runs first in each of the four client write RPCs: `ObjectMappingService.Post/Update` and `ObjectPersistenceService.Post/Update`. It runs before `ExtractKey`, the existing-row fetch and `EnforceWriteAuthorization`.
- It builds a case-insensitive map from `KeyColumn.Name` and `ScalarColumns[].Name` to the registered spelling. The tenant column is excluded: its smuggling check (`AuthorizationFieldMasking.cs:91-98`) keeps running on the raw key and keeps its message.
- It renames each payload key that matches exactly one column to that column's spelling.
- It refuses with `InvalidArgument` two payload keys that map to the same column, and a key that matches more than one column (a stored type with case-duplicate columns).
- Keys that match no column are left alone (unchanged behaviour). Navigation keys are never in the map.

Effects:
- A re-cased owner key (`OWNERID`) becomes `OwnerId`, so the existing `OwnerImmutable` check refuses it.
- .NET properties whose names start with an acronym now persist. `URLPath`, sent as `urlPath` by `JsonNamingPolicy.CamelCase`, used to reach Postgres as `UrlPath` and be stored as NULL. Keys such as `ID` also work on Update now.
- `PayloadSizeValidator` (exact column names) now applies to .NET camelCase payloads. Oversized .NET writes therefore fail up front with `InvalidArgument` instead of dead-lettering later in StarRocks. This needs a release note.
- Mapping Post and Update echo the request payload, so their response keys become canonical. The .NET and Java readers are case-insensitive; the other SDKs already send canonical spellings.

### 2.2 Declared names normalised to the column's spelling (side fix: owner spoofing)

Registration accepts `Authorization.OwnerField`, `FieldPermissions[].FieldName` and `Relations[].ForeignKey` case-insensitively, but stores the caller's spelling. The write gate then looks them up exact or lower-first (`StructFieldAccess.Candidates`). So with an owner field spelled differently from its column beyond the first letter:
- an owner-scoped caller can set the owner on Create (`SetAuthoritativeField` adds a second key beside the client's);
- bypass writers skip `OwnerImmutable`.

Fix: `SchemaBuilder.BuildDescriptor` rewrites these three to the matched column's spelling. `SchemaRegistry.LoadAsync` applies the same rewrite to stored descriptors (`descriptor with { … }`).

### 2.3 Serialisation and the sinks

- `ProtoPayloadHelper.FoldKeys` stops upper-casing keys that already equal a column name; other keys keep `UpperFirst`. A lower-case-first column (`url`) currently serialises as `Url` and is dropped by `json_populate_record`.
- `EngagementRepository.UpsertAsync` selects entries by exact column name (ordinal).
- `EngagementStoreConsumer.WithOwnerValue` removes every case variant of the owner key, then writes the authoritative value under the column's spelling, as `OutboxWriter.WithTenantColumn` already does.

### 2.4 Registration refusals

`RegisterSchema` refuses with `InvalidArgument`:
- two properties, or the key and a property, whose names differ only by case;
- a relation navigation property whose name matches any column case-insensitively.

## 3. Read-path leaks (#8, #9)

### 3.1 Popularity eligibility (#8)

One predicate, `PopularitySignalEligibility.IsEligible(parent, relation, registry)`. Let `PR(X)` be the roles with `CanReadAll` in `X.Authorization.RowPermissions`, compared ordinally. The predicate is true when either:
- `parent.Authorization` is null (nobody can read the parent, so nothing leaks); or
- all of the following hold:
  - `parent.Authorization.OwnerField` is null;
  - the child, resolved through the registry from the `OneToMany` relation, has non-null `Authorization`;
  - `PR(parent) ⊆ PR(child)`;
  - every `FieldPermission` of the child on the FK column or on `PopularitySignalColumn` with non-empty `ReadableRoles` satisfies `PR(parent) ⊆ ReadableRoles`.

A missing child or a relation that isn't `OneToMany` makes the signal ineligible.

Where it applies:
- `PopularitySignalUpdater` (the consumer and the sweep) skips ineligible signals.
- `ObjectSearchGrpcService` skips ineligible signals when ranking (`PopularityFor`, `RetrievePopularityOrDegradeAsync`).
- `PopularitySignalValidator.ValidateAtStartup` logs each ineligible signal once. It also refuses a configured signal whose key (`{relation camelCase}Count` or `…CountBuckets`) case-insensitively matches a parent column.

SearchSimilar builds its response objects from an **allow-list**: the descriptor's columns plus `key`. Every other Qdrant payload key is dropped. That covers popularity keys and keys left over from removed config. No consumer reads them.

### 3.2 Decay from readable fields only (#9)

At the three decay sites (SearchSimilar, SearchChunks, and SearchSimilar via chunks), the decay field is set to null when `decision.AllowedFields` is non-null and does not contain the decay column (case-insensitive, as `AllowedFields` already is).

## 4. Enrichment (#18)

- The target-permission rule in `SchemaRegistrationOrchestrator.ValidateEnrichmentTargets` is restated as a pure predicate over `SchemaDescriptor`: TEXT and TEXT[] targets, with the synthetic `Document` excluded.
- Registration keeps throwing on it.
- `SchemaRegistry.LoadAsync` runs the predicate on every stored descriptor. A failing descriptor loads with `EnrichmentTargets = []` and logs an error naming the type. `EnrichmentConsumer` already returns early for a type with no targets (`:97`, `:266`).
- The extraction prompt passes `target.Hint` through `EnrichmentPrompts.EscapeUntrustedText`. That function rewrites only `<<<` and `>>>`, and the content hash still uses the raw hint, so stored hashes stay valid.

## 5. Privileged requests through the worker (#1, #7; part of #13)

### 5.1 Table and worker

New plumbing table `IversonPrivilegedRequests`. The worker creates it through `ApplySchemaAsync`. It has no tenant column, so it has no RLS.
- Columns: `Id uuid PK`, `Kind text` (`RegisterSchema`, `ReconcileType` or `ReplayDlq`), `Payload jsonb`, `RequestedBy text`, `Status text` (`pending`, `running`, `succeeded`, `failed` or `expired`), `ResultJson jsonb`, `RequestedAt`, `ExpiresAt`, `CompletedAt`.

`PrivilegedRequestWorker` runs in the worker role only:
- **Polling:** every 1 s for `Status='pending' AND "ExpiresAt" > now()`, oldest first.
- **Claiming:** `UPDATE … SET "Status"='running' WHERE "Id"=@Id AND "Status"='pending' AND "ExpiresAt" > now()`. Zero rows means it lost the race.
- **Locks:** session advisory locks on dedicated pooled connections, released in `finally`, with keys chosen outside the `int4` `hashtext` range used elsewhere.
  - `RegisterSchema` takes one global registration lock, which serialises it across worker replicas.
  - `ReconcileType` takes a per-type lock, so a long reconcile never blocks registration.
  - `ReplayDlq` takes none.
- **Registry:** the worker reloads its registry (`LoadAsync`) before running a request, so a type registered moments ago resolves.
- **Errors:** an unexpected exception records `Internal` with a generic message; the details go to the log.
- **Cleanup:** finished rows older than one day are deleted.

### 5.2 How the api waits

The api inserts the row, then polls its status every 250 ms.

**RegisterSchema.**
- Validation and descriptor building stay in the api. That includes the embedding-service call that sets the vector dimension, and the fast owner pre-check against the cache.
- The payload is the ordered, owner-stamped descriptors, serialised with the registry's JSON options (they round-trip), plus the changed-template type names.
- Waits:
  - **15 s** for the worker to claim the request. If the api then changes `pending` to `expired`, the worker will never run it, and the caller gets `Unavailable` ("the worker did not pick the registration up; retry").
  - If the worker had already claimed it, a further **10 s**.
  - This 25 s maximum is below every edge default on the gRPC path: nginx 60 s, ALB 60 s idle, GCE backend 30 s, AGIC 30 s. A healthy worker answers in about 1 s.
  - If the claimed request still hasn't finished after 10 s, the caller gets `DeadlineExceeded` ("registration is still being applied") and the request completes in the worker. The caller can re-register, which is idempotent once applied.
- On `succeeded`, the api reloads its own registry (`LoadAsync`) before answering, so the caller's next write on that replica sees the type. Other replicas pick it up on their 30 s poll, as today.
- On `failed`, the api returns the stored status code and message.

**`/admin/reconcile/{type}`.** Operator is checked as today. The api waits up to 60 s:
- the worker finished: `200 {reconciledCount, typeName}`, as today;
- the type is unknown: `404`;
- still running: `202 {requestId}`, and the worker completes it. Reconcile is idempotent, so the request has no expiry.

These routes are reached only through port-forward, so no edge timeout applies. A client disconnect no longer cancels the reconcile.

**`/admin/dlq/{id}/replay`.**
- The api keeps every check it runs today: Operator, the active tenant, the row's `TenantId` against the caller's tenant, and `Replayed=false`. It reads those columns without `MessageValue`.
- It queues the replay and waits up to 30 s:
  - `200`: replayed;
  - `404`: the row is gone or was already replayed (the worker re-checks `Replayed=false`);
  - `202 {requestId}`: still pending.
- The worker produces the message and marks the row replayed. That is at-least-once, as today. The api writes the audit entry and the response.

### 5.3 The worker's RegisterSchema, per descriptor in batch order

1. **Owner (#7).** It reads the stored owner from `_iverson_schema`: `schema_json->>'ownerTenantId'`, matched with `lower(type_name) = lower(@TypeName)` (the registry is case-insensitive; the column isn't). A different owner fails with `PermissionDenied`, using today's message.
2. **Adoption (#1).** This applies when the type has no registry row but its derived table exists in `public`. The worker refuses with `AlreadyExists` unless:
   - the relation is an ordinary table (`relkind = 'r'`) with a `__TenantId` column; and
   - for an owned type, no row in it has a `__TenantId` other than the owner's (one `EXISTS` query under `iverson_maintenance`).

   The documented model-change recovery (delete the registry row, then re-register) keeps working for types whose tables hold only the owner's rows. The plumbing table `iverson_token_revocations`, a view, and another tenant's leftover table are refused.
3. **DDL.** `ApplySchemaAsync` with `SchemaDriftPolicy.Throw`. Drift fails with `FailedPrecondition`, as today.
4. **Registry write.** `INSERT … ON CONFLICT (type_name) DO UPDATE … WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner`. Zero rows fails with `PermissionDenied`.
5. **Re-renders.** It queues re-renders for changed templates.

## 6. The Postgres credential split (#13, Postgres half)

### 6.1 Logins

**`iverson_api` (new):**
- a CNPG managed role in `charts/postgres/templates/cluster.yaml` (CNPG 1.30.0, chart 0.29.0, supports managed roles), created on existing clusters too: `login: true`, `inRoles: [iverson_runtime]`;
- its password comes from a new lookup-or-generate Secret `<release>-postgres-api` (`kubernetes.io/basic-auth`, `username`/`password`, `helm.sh/resource-policy: keep`), following `charts/authentik/templates/secret-postgres.yaml`;
- no BYPASSRLS, no maintenance membership, owns nothing.

**`iverson` (owner, unchanged):** the worker and the revoke-cross-db hook keep it. The api Deployment no longer mounts `<release>-postgres-app`.

### 6.2 Grants (issued by the worker at startup, idempotent, after its plumbing DDL)

The worker issues all of these itself, as database owner, and none comes from a hook. A post-upgrade hook only runs after `--wait` succeeds, so it can't gate the api's readiness.

| Object | Grant to `iverson_api` | Used by |
|---|---|---|
| database `iverson` | `CONNECT` | login. PUBLIC's CONNECT is revoked by the Authentik hook. |
| entity tables | through `iverson_runtime`, as today | every data RPC |
| `IversonReconciliationQueue` (outbox) | `INSERT`, `DELETE`, `SELECT ("Id")` | outbox inserts in the same transaction as entity writes, including `EventType` and `Payload`; deleting its own row after the immediate publish. Payloads are unreadable. |
| `IversonDlqMessages` | `SELECT` on every column except `MessageValue` | the `/admin/dlq` list and replay checks. Queries name their columns, because `SELECT *` is denied. |
| `IversonTenants` | `SELECT`, `INSERT`, `UPDATE`, `DELETE` | tenant lifecycle (including CreateTenant's rollback), the status cache and the console readers |
| `iverson_token_revocations` | `SELECT`, `INSERT`, `UPDATE` | RemoveUser and SetTenantAdmin, and the revocation cache |
| `_iverson_schema` | `SELECT` | loading the registry |
| `IversonPrivilegedRequests` | `INSERT`, `SELECT`, `UPDATE ("Status")` | §5's requests, waits and expiry |
| `IversonStarRocksTenants`, `IversonStarRocksBackfill` | `SELECT` | §7's provisioned-tenant check, and the readiness check for the backfill marker |
| `DocumentRerenderQueue`, `iverson_enrichment_state` | none | worker only |

### 6.3 The api runs no DDL and starts tolerantly

- Startup DDL (`Program.cs:723-764`) is gated to `WORKLOAD_ROLE=worker`. It covers:
  - `EnsureRolesAsync`;
  - the plumbing tables, including the new ones;
  - re-applying each registered type's table and RLS policy;
  - the legacy-tenant seed (`Tenancy:SeedLegacyTenants`);
  - the §6.2 grants.
- `SchemaRegistryRepository.EnsureTableAsync` leaves `SchemaRegistry.LoadAsync`, which today runs it at startup, every 30 s and on register. The worker calls it once at startup.
- The api's role check only runs the `SET LOCAL ROLE iverson_runtime` probe.
- The api no longer crash-loops on a missing table or grant. Its startup registry load retries in the background, and `/health` reports not-ready while any of these hold:
  - `has_table_privilege('iverson_api', '"IversonReconciliationQueue"', 'INSERT')` is false;
  - the registry hasn't loaded;
  - the StarRocks backfill marker (§7.5) is absent.
- `ReadinessPolicy` keeps its `AuthPending` tolerance.

### 6.4 Dead code removed

The api's maintenance-role reads and deletes for types without a tenant column, and the outbox writer's owner-role branch for a null tenant, become fail-closed errors:
- `ObjectRetrievalGrpcService.cs:112-120`;
- `ObjectMappingGrpcService.cs:340-348`;
- `OutboxWriter.cs:54-58`.

Every loaded descriptor has a tenant column, and every non-denied decision carries a tenant value. `/admin/reconcile`'s maintenance work moves to the worker (§5). **The api never enters BYPASSRLS.**

## 7. Per-tenant StarRocks logins (#13, StarRocks half)

### 7.1 Logins

- **Name:** `iverson_t_<suffix>`, where `<suffix>` is the 25-character base-36 SHA-256 suffix the tenant's role name already uses (`TenantIdentifier`). 35 characters in all.
- **Password:** hex HMAC-SHA256 of `"starrocks-tenant:" + tenantId` under a master key (64 characters).
  - The key comes from a new Opaque lookup-or-generate Secret, `<release>-starrocks-tenant-key` (`keep`). It must not be a new key in an existing Secret, which would render empty on upgrade.
  - The api and the worker both mount it.
  - Compose uses a literal produced by `generate-compose-secrets.sh`; `appsettings.Development.json` uses a dev literal.
- **Provisioning** (`EnsureTenantProvisionedAsync`, as `iverson_provisioner` with `user_admin` active, after the tenant role exists):
  1. `CREATE USER IF NOT EXISTS 'iverson_t_…'@'%' IDENTIFIED BY '<derived>' DEFAULT ROLE '<tenant role>'`;
  2. `ALTER USER … SET PROPERTIES ('session.query_timeout'='180', 'session.exec_mem_limit'='4294967296')`, the same caps `iverson_app` carries.
- `GRANT <role> TO USER 'iverson_app'@'%'` (`EngagementRepository.cs:523`) is removed.
- The provisioner keeps its self-granted tenant role and ALTER. They're still needed for CREATE TABLE and widen.

### 7.2 Connections

- `EngagementRepository` builds each tenant's connection string deterministically from the base string with `MySqlConnectionStringBuilder`, replacing `UserID` and `Password` and setting `MaximumPoolSize=10` and `ConnectionIdleTimeout=60`. A distinct string means a distinct pool, and an idle tenant's pool drains to zero connections.
- `SET ROLE` and `SET ROLE NONE` are removed from `RunTenantScopedAsync` and `StreamTenantScopedAsync`. Nothing may change a data connection's role or session state. Live-probed: MySqlConnector's pooled reset does not clear StarRocks session state. With per-tenant pools, leftover state can't cross tenants.
- The Aggregate RPC runs its specs with concurrency capped at 4 per request (today `Task.WhenAll` is unbounded).
- Pool exhaustion ("All pooled connections are in use") is excluded from the transient classification that feeds the per-process circuit breaker, so one tenant can't open the breaker for all tenants.
- The authz-null branches and the public executor keep using `iverson_app`. Their only callers are tests, and they now have no data rights.

### 7.3 Provisioned-tenant record

- New plumbing table `IversonStarRocksTenants (TenantId text PK, ProvisionedAt timestamptz)`. The worker inserts a row after each successful provisioning and backfill.
- `Iverson.StarRocks` takes an injected `IProvisionedTenantDirectory`, implemented over Postgres in `Iverson.Api` and cached per process once a tenant is found. Every StarRocks reader consults it before connecting: the api's read paths and the worker's popularity aggregates.
  - An unrecorded tenant reads as empty: `[]`, `null` or an empty stream, as today.
  - A recorded tenant whose login fails is a real error. The gRPC services map it to `Unavailable`, not `Unknown`.
- `IsExpectedMissingResourceError` loses its role arm ("cannot find role", "is not granted to") and keeps its 5502 "Unknown table" arm.

### 7.4 `iverson_app`

It keeps only `OPERATE ON SYSTEM`, for `/health`, the readiness gate and the post-failure liveness probe (`SHOW BACKENDS`). It holds no tenant data rights.

### 7.5 Backfill and revoke

**Backfill**, in the worker at startup, idempotent, as the provisioner:
1. Enumerate the `iverson_tenant_%` databases. The provisioner sees every fully provisioned tenant database through its direct `ALTER` grant, and the tenant id is the suffix of the database name.
2. For each tenant:
   - `CREATE ROLE IF NOT EXISTS` and the role's grants (reusing the provisioning code);
   - `CREATE USER IF NOT EXISTS … DEFAULT ROLE`;
   - `GRANT <role> TO USER` (required before `ALTER USER … DEFAULT ROLE`);
   - `ALTER USER … IDENTIFIED BY <derived>` (keeps passwords aligned with the key);
   - `ALTER USER … DEFAULT ROLE`;
   - the session caps;
   - insert the tenant into `IversonStarRocksTenants`.
3. Mark the backfill complete in a single-row plumbing table, `IversonStarRocksBackfill (Id int PRIMARY KEY CHECK (Id = 1), CompletedAt timestamptz)`. A separate table avoids a sentinel id that a real tenant id could collide with. The api's readiness requires this row.

**On a fresh install** the provisioner login doesn't exist until the post-install hook, which runs after `--wait`. So when the provisioner login is refused and `IversonTenants` is empty, the worker writes the marker without a backfill (nothing to backfill). On an upgrade, the provisioner already exists.

**Revoke:** `job-create-user.yaml` (post-install and post-upgrade, as root, after the rollout) revokes every role still granted to `iverson_app`, which after this change should hold none. It parses `SHOW GRANTS FOR 'iverson_app'@'%'`, which covers the hashed and the legacy verbatim role names. Old pods are gone by then, so there's no window of empty results, dead-lettered writes or re-grants. Compose has no hooks: its `starrocks-init` runs the same revoke, and the compose worker starts before the api.

## 8. Deployment and compose

**Chart:**
- `postgres/cluster.yaml`: the managed role `iverson_api`.
- New Secrets `<release>-postgres-api` and `<release>-starrocks-tenant-key`.
- The api Deployment: `ConnectionStrings__Postgres` uses `Username=iverson_api` with the new Secret; it mounts the tenant key and drops `<release>-postgres-app`.
- The worker Deployment mounts the tenant key.
- `job-create-user.yaml` gains the revoke.
- `job-revoke-cross-db.yaml` also polls for `iverson_api` and grants it CONNECT, as belt-and-braces; the worker's grant is the one the api depends on.

**Compose:**
- The init SQL creates `iverson_api` only when `iverson_runtime` exists. Otherwise the superuser worker creates the login, membership and grants at startup.
- The api uses `iverson_api`.
- The **worker starts first**: the api `depends_on` the worker being healthy, which reverses today's order.
- `generate-compose-secrets.sh` emits the tenant key.

**Runbook:** an upgrade note covering the new login, what the worker grants and backfills, what the hook revokes, and how to check (`SHOW GRANTS FOR 'iverson_app'@'%'`, `\du iverson_api`).

## 9. Testing and verification

**Unit and integration:**
- §2: re-cased owner key refused on both Update RPCs; acronym key persists on Post and Update; two keys or an ambiguous key refused; normalised declared names stop owner spoofing on Create and the skipped `OwnerImmutable`; `FoldKeys` keeps `url`; StarRocks upsert exact; `WithOwnerValue` drops variants; the registration refusals.
- §3: eligibility cases, including an owner-scoped parent, a missing child and field permissions; the response allow-list; equal scores for callers who differ only in access to the decay column.
- §4: a reloaded descriptor that breaks the rule loads with no targets; the hint is escaped.
- §5: the worker's owner race, adoption (refused for a plumbing table, a view and another tenant's rows; the model-change recovery still adopts) and drift; the api's waits (succeeded, failed, expiry, timeout after claim, reconcile 200/202/404, replay 200/202/404); two owners racing to register a new name.
- §6: the api's repositories run as a non-owner login with exactly the §6.2 grants across every data RPC, the outbox fast path, tenant lifecycle, revocation, the DLQ list and requests. Negative cases: reading outbox payloads, `MessageValue`, `SET ROLE iverson_maintenance` and DDL are each denied. Readiness waits for the grants.
- §7, against the StarRocks container fixture:
  - tenant A's login can't read tenant B and needs no `SET ROLE`;
  - an unrecorded tenant reads as empty;
  - a recorded tenant with a wrong key returns `Unavailable`;
  - worker upserts and aggregates use the tenant login;
  - backfill creates logins and records tenants;
  - the hook revoke leaves `iverson_app` with no roles;
  - `/health` still passes;
  - pool exhaustion doesn't open the breaker.
- Updated: the 10–14 popularity ranking tests and the orchestrator tests, which now expect the queued path.

**Live:**
- compose: register, write, read, cross-tenant denial, and backfill on a populated StarRocks;
- kind laptop: install from scratch and an upgrade from current `main`, for the Postgres split, the readiness gating and the hooks;
- throwaway StarRocks 4.1.6 containers for the per-tenant mechanics, since the laptop profile deploys no StarRocks;
- kube-score, kubeconform, `helm lint` and tfsec on all five overlays.

## 10. Known issues / accepted as out of scope

- **A compromised api or worker process can still reach every tenant.** The api derives any tenant's StarRocks password; the worker keeps owner, BYPASSRLS and provisioning rights. This is inherent to a single multi-tenant service (user decision 2026-10-16).
- **Model-change recovery for some owned types.** Owned types whose tables still hold rows another tenant wrote before `02abddb3` (16 Sep – 9 Oct) can't be adopted until those rows are removed.
- **Tenant deletion leaves the StarRocks database, role and login in place**, as today. The api refuses deleted tenants.
- **`OPERATE ON SYSTEM` stays on `iverson_app`,** which now holds no data rights. Round 11's accepted residual is closed in substance.
- **Registration needs a running worker.** With the worker down, RegisterSchema returns `Unavailable` after 15 s.
- **The size guard now applies to .NET camelCase writes** (release note, §2.1).
- **FE connections** grow with concurrently active tenants: at most 10 per tenant per pod, against 4,096 per FE and a per-user default of 1,024.

## Verified assumptions

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
16. **Edge timeouts:** nothing overrides the nginx (60 s) or ALB (60 s idle) defaults. GCE and AGIC default to 30 s, though gRPC through them already fails today (`values-gcp.yaml:102`). Kestrel has no request timeout. `/admin` reconcile and DLQ routes are port-forward only (`Program.cs:621,657,696`; `admin-api-ingress.yaml:167-173`). Defaults are from vendor documentation and were not re-checked live.
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
26. **Pooled reuse keeps session state.** Probed with MySqlConnector 2.6.2 on StarRocks 4.1.6:
    - the pool reuses the connection;
    - the default role persists across reuse;
    - a session variable set on one checkout persists to the next, because the reset doesn't clear StarRocks session state;
    - `SET ROLE NONE` before return leaves the next checkout with no role.
27. **MySqlConnector 2.6.2 pool options behave as documented:** `MaximumPoolSize` (default 100), `ConnectionIdleTimeout` (default 180 s, reaped every 30–60 s), `MinimumPoolSize` 0, and a full pool waits up to `ConnectionTimeout` (15 s) and then throws. That a distinct connection string gets a distinct pool is library behaviour that the repo's tests rely on (`MatchRowsIntegrationTests.cs:332-345`).
28. **StarRocks 4.1.6 FE limits.** Probed: `qe_max_connection` = 4096 (not mutable), and the per-user default is `max_user_connections` 1024.
29. **Every StarRocks connection site** is enumerated in `verify-section45.md` D15: data paths P1–P2, authz-null and executor P3, provisioner P4, readiness and liveness P5–P6, `/health` P7, dev appsettings P8. LoadTest and DirectSeeder use root against the legacy shared `iverson` database and are unaffected.
30. **The provisioner still needs its self-granted role and ALTER** (`EngagementRepository.cs:529-544`). `DEFAULT ROLE` requires the role to be granted first (StarRocks ALTER USER docs).
31. **The tenant key must be its own Secret.** Adding a key to an existing lookup Secret renders empty on upgrade (`secret-provisioner.yaml:17-21`).
32. **Only the api and worker Deployments, the Authentik hook and compose reference `<release>-postgres-app` or `ConnectionStrings__Postgres`.** ClientConformance and LoadTest have their own owner strings, outside the api pod.
