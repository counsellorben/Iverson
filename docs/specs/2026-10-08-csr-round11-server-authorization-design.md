# CSR Round 11 — Server Authorization and Query Safety Design (Sub-project A)

**Source review:** `docs/criticalreviews/2026-10-08-iverson-critical-security-review-11.md`, reviewed at `19ab57c0`.

**Goal:** sub-project A of the round-11 remediation fixes the server-side authorization and query-safety findings.

- **Mediums:**
  - #1, pipeline outer joins drop the joined type's row ownership;
  - #2, the OneToMany foreign-key name reaches Postgres SQL unvalidated;
  - #3, OneToMany hydration filters on a foreign key the reader may not read.
- **Lows and Info:**
  - #10, the expression validator is a token scan, and its login can cross tenants. Both halves are fixed here: a parser, and a credential split;
  - #11, enrichment write-back ignores field permissions;
  - #12, the template guard admits only deny-all targets;
  - #13, the RemoveUser revocation window;
  - #14, Update's owner-mismatch existence oracle;
  - #20, the type-name existence oracle;
  - #21, tenant lifecycle is enforced per path.

Sub-projects B (identity plane), C (transport and network) and D (clients, tooling and CI) cover the rest of round 11.

**Decisions (user, 2026-10-08):**
- **Split.** Round 11 is split into A–D, and A goes first. A includes #10 (both halves) and #20.
- **The six forks** are all resolved with the recommended option:
  - derived-table joins;
  - an AST expression parser;
  - two StarRocks logins;
  - template relation traversal removed;
  - owner-private types answered like unregistered ones, with no new flag;
  - per-path lifecycle fixes.
- **After verification:**
  - aggregates in a derive expression require `OVER`;
  - Mapping Get and Delete on an invisible type answer exactly like an absent key;
  - `iverson_app` keeps `OPERATE ON SYSTEM` as its one direct grant, with the residual recorded.

**Global constraints**
- **One branch, one merge.** No proto change and no SDK change.
- **Conformance.** No requirement changes are expected (Verified assumption 30). If one proves necessary, the project's rule applies: retire the requirement and add a successor.
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation.
- **`docs/` is gitignored:** commit with `git add -f`.
- **The user's running compose stack** (`iversonserver` containers, volumes, networks and images) is never started, stopped or modified, and neither are another session's `rfdt-*` containers or `tei_models` volume. Live checks use throwaway resources that the check creates and deletes.
- **Memory.** One heavy process at a time; a WSL OOM kills the VM. Builds, tests and StarRocks containers run memory-capped.

---

## 1. Pipeline outer joins (closes #1)

**Today.** For a join whose source is a registered type that no earlier step produced (a "fresh" type), `StarRocksPipelineBuilder` appends the caller's owner and tenant predicates to the join's `ON` clause (`StarRocksPipelineBuilder.cs:640-679`). For RIGHT and FULL joins, rows of the joined table that fail those predicates are still emitted, NULL-extended. The step's own `WHERE` holds only the caller's filters, so nothing removes them. A live StarRocks 4.1.1 probe confirmed this: with the owner check in `ON`, another owner's row came back on RIGHT and FULL joins (Verified assumption 2).

**Change.** When the fresh type's constraint carries an owner column or a tenant column, the builder joins a derived table that applies both predicates in its own `WHERE`:

```
{KIND} JOIN (SELECT * FROM `<tenant_db>`.`<table>` WHERE `<owner>` = @sN_jM_owner AND `<tenant>` = @sN_jM_tenant) AS `<Src>` ON <conds>
```

- Parameter names stay the existing per-step, per-join ones.
- Inside the derived table, the predicates are written unqualified.
- Joins to an earlier step are unchanged, because that source is already filtered upstream.
- What the step may select from the joined source is still governed by `ColumnsFor` and AllowedFields.

`StarRocksQueryBuilder` (Search, Aggregate, GroupBy) is unchanged. Its primary-type tenant predicate, and owner predicate, sit in the outer `WHERE` on every path that can carry a join (Verified assumption 4), and that drops any row only an outer join would keep. New tests pin this for RIGHT and FULL.

## 2. Expression parser (closes #10, first half)

**Today.** Client expressions are spliced into StarRocks SQL as text, after a character denylist and a token scan (`StarRocksPipelineBuilder.cs:23-53, 395-422`; `StarRocksQueryBuilder.cs:248-256, 336-340, 526-539`). There are four emission sites:

| Site | Emitted as | Field |
|---|---|---|
| `BuildAggregate` (`QB:339-345`) | `FN(expr)`, or `COUNT(DISTINCT expr)` | `AggregationSpec.expression` |
| `BuildMetricExpr`, GroupBy (`QB:541-542`) | `FN(expr) AS …` | `MetricSpec.expression` |
| `EmitMetric`, Pipeline (`PB:706-707`) | `FN(expr) AS …` | `MetricSpec.expression` |
| Derive (`PB:623`) | `(expr) AS …` | `DeriveColumn.expr` |

**Change.** All four sites go through a new parser in `Iverson.StarRocks/Expressions/`. The denylist (`RejectForbiddenCharacters`), `TokenRx`, `IsNonColumnToken` and the function and keyword lists are deleted; their only users are these four sites (Verified assumption 9). `IdentifierRx` stays, for step and alias names.

- **Lexer.** It accepts:
  - numbers, which are decimal; a value that doesn't fit is refused, with no `FormatException` or `OverflowException` escaping;
  - identifiers matching `[A-Za-z_][A-Za-z0-9_]*`;
  - the operators `+ - * / %`;
  - parentheses and comma;
  - `*`, only as the sole argument of `COUNT`.

  Any other character is refused. That covers quotes, backticks, backslash, `;`, `#`, comment openers, comparison and bitwise operators, and non-ASCII.
- **Grammar.** Recursive descent.
  - **Precedence.** Lowest first: additive `+ -`, then multiplicative `* / %`, then unary `-`, then primary.
  - **Primary:** a number, `NULL`, a column, a call, a window, or a parenthesized expression.
  - **Reserved words:** `NULL OVER PARTITION BY ORDER ASC DESC`, never read as column names.
- **Limits.**
  - The existing 1,000-character cap runs first, before lexing.
  - Nesting depth is capped at 32.
  - Every failure is an `EngagementQueryTranslationException`, which maps to InvalidArgument. It names the token and its position.
- **Contexts:**
  - **Aggregate argument** (aggregation and metric expressions, which the site wraps in an aggregate). Scalar functions only (`COALESCE`, `NULLIF`, `ROUND`, `ABS`), with no window and no aggregate.
  - **Derive.**
    - All nine functions: the four scalar functions above plus `SUM`, `AVG`, `MIN`, `MAX` and `COUNT`, with `COUNT(*)` allowed.
    - The five aggregates **require** `OVER (…)`, with an optional `PARTITION BY col, …` and an optional `ORDER BY col [ASC|DESC], …`. An empty `OVER ()` is allowed.
    - A bare aggregate is InvalidArgument, as `docs/one-query-five-languages.md:754-757` already promises; today it fails inside StarRocks.
- **Columns.** Every column node, including `PARTITION BY` and `ORDER BY` columns, goes through a per-site callback: `string ResolveQuoted(string name)`. The callback resolves and authorizes the name, then returns the quoted SQL identifier, or throws with the site's existing message.
  - This follows `BuildWhere`'s `resolveQuoted` precedent (`QB:569-584`).
  - The three existing mechanisms become three callbacks: `ResolveStrict` with `IsFieldAllowed` for Aggregate, `ResolveColumn(tableMap)` with `IsFieldAllowed` for GroupBy, and step-dictionary membership for Pipeline. Each keeps its pinned error text.
  - No new reference forms are introduced: `Type.Field` stays unavailable.
- **Emitter.** SQL is written from the AST, never from the input text:
  - columns come from the callback;
  - function names come from the allow-list, upper-cased;
  - numbers are re-printed with `CultureInfo.InvariantCulture`;
  - every operator and its parenthesization come from the tree.

**Test fallout.** These are mechanical changes.
- The 18 theory cases that assert `SUM(Amount)` spellings and `… OVER (…)` are accepted in metric and aggregation expressions are re-pointed to scalar functions. Those forms nest an aggregate in an aggregate, which is invalid SQL, and nothing outside these tests uses them. The cases are at `QBT:932-944`, `QBT:2883-2893` and `PBT:1011-1021`.
- The five bare-aggregate derive cases at `PBT:1043-1047` become rejections.
- "Forbidden character" message assertions and raw-text SQL-shape assertions are re-baselined to the parser's messages and canonical output.

**Side-fix (latent, in code this section touches).** `RangeBucket.from` and `RangeBucket.to` are spliced through culture-sensitive interpolation (`QB:1001-1003`), so `NaN` and `±Infinity` come out as bare words. Non-finite bounds become InvalidArgument, and finite bounds are formatted with `InvariantCulture`.

## 3. StarRocks credential split (closes #10, second half)

**Today.** One login, `iverson_app`, holds `user_admin`, `CREATE DATABASE ON CATALOG`, `OPERATE ON SYSTEM`, direct DML and DDL on the `iverson` database, and every tenant role (`job-create-user.yaml:68-77`; `EngagementRepository.cs:481-500`).

The live probe established two facts:
- StarRocks activates only the roles named in `SET ROLE`.
- Direct user grants apply whatever role is active.

So the direct grants are what a single-statement bypass, or a stolen query credential, could use (Verified assumptions 13 and 14).

**Change: two logins.**

- **`iverson_provisioner`@'%' (new).**
  - It is granted `CREATE DATABASE ON CATALOG default_catalog`, directly, because a role grant is inactive at that point, and `user_admin`.
  - Only `EngagementRepository.EnsureTenantProvisionedAsync` uses it. Its sole production caller is the worker's engagement consumer (Verified assumption 11).
  - That method keeps its statements and adds two grants under `SET ROLE user_admin`: `GRANT <tenant role> TO USER 'iverson_provisioner'@'%'` and `GRANT ALTER ON ALL TABLES IN DATABASE <tenant_db> TO USER 'iverson_provisioner'@'%'`.
  - The first grant lets the existing `SET ROLE user_admin, <tenant role>` step work for tenants created before the split.
  - The second gives `WidenLargeTextColumnsAsync`'s `ALTER TABLE … MODIFY COLUMN` a privilege. The probe showed none exists today; it covers tables created after the grant.
  - Re-running either grant is harmless.
- **`iverson_app`.**
  - It loses its direct DML and DDL on `iverson`, `CREATE DATABASE` and `user_admin`.
  - It keeps the tenant roles the provisioner grants it.
  - It keeps one direct grant, `OPERATE ON SYSTEM`. `SHOW BACKENDS` needs it in both roles' readiness gate and in `/health`, which run with no role active (`EngagementHealthChecker.cs:72`; `EngagementRepository.cs:719-725`).
- **Default database.** `Database=iverson` is removed from all four app connection strings: `charts/api/templates/deployment.yaml:100`, `charts/worker/templates/deployment.yaml:85` and `docker-compose.yml:514, 632`. Once its grants are revoked, `iverson_app` can't connect with that default. No production statement targets `iverson`: every tenant statement is qualified to the tenant database.

**Wiring.**
- **`AddStarRocks`** gains an optional trailing `string? provisionerConnectionString = null`. `EngagementRepository` gets a second connection factory, used only for provisioning.
- **The `Program.cs:267-273` guard** becomes role-aware: the provisioner connection string is required only when the workload role is `worker` and Engagement is enabled. The API pod never receives it.
- **First-upgrade gap** (CDR-1 §3.1, user choice B). The post-upgrade hook creates the provisioner after the upgraded worker is already serving, and the worker's provisioned set is per-process memory (Verified assumption 39).
  - `EnsureTenantProvisionedAsync` wraps a `MySqlException { ErrorCode: AccessDenied }` from opening the provisioner connection as `EngagementNotReadyException`.
  - `TransientFailures` classifies that exception as transient (`TransientFailures.cs:48`), so the consumer halts without committing and redelivers once the hook has run, instead of dead-lettering.
  - This mirrors the readiness checker's AuthPending treatment of the same not-yet-created-by-hook case (`EngagementHealthChecker.cs:32-45`).
  - Every other provisioning failure keeps its current classification.
- **Helm.**
  - A new `<rel>-starrocks-provisioner` Secret, lookup-or-generate with `keep`. This follows round 10's precedent of new Secrets; adding a key to the existing Secret renders empty on upgrade (Verified assumption 16).
  - The worker deployment gets `ConnectionStrings__StarRocksProvisioner`.
  - `job-create-user` creates the provisioner and revokes `iverson_app`'s direct grants, except `OPERATE ON SYSTEM`. `REVOKE` of a privilege or a role already gone is silent (Verified assumption 14), so the job stays safe to re-run on upgrades. It keeps `CREATE DATABASE IF NOT EXISTS iverson` (as root) ahead of the revokes. A database-scoped `REVOKE` naming a database that doesn't exist fails and aborts the batch (Verified assumption 14), so a fresh install needs the empty `iverson` database to exist (CDR-2 §3.1, user choice B: verified, then kept). The stale comment about `EnsureDatabaseAsync` (`:88-93`) is corrected.
- **Compose.** `starrocks-init` mirrors the job, including keeping `CREATE DATABASE IF NOT EXISTS iverson` ahead of the revokes. The provisioner uses a dev-only literal password, following the existing `iverson_app` literal (compose root is passwordless and loopback-only). The compose worker gets the provisioner connection string.
- **Test fixtures.**
  - `TenantIsolationIntegrationTests` and `MatchRowsIntegrationTests` create both users and pass both connection strings.
  - Fixtures that run as root are unchanged.
  - Because users persist across classes in the shared container, the fixtures grant and revoke in a way that doesn't depend on run order.

## 4. Schema registration (closes #2, #12, #11)

### 4a. Relation identifiers (#2)
- **Registration.** `RegisterAsync` runs `ValidateIdentifier` on every relation's `PropertyName`, `ForeignKey` and `RelatedType`, for all four kinds. It runs after `RejectReservedTenantName`, so the reserved-name rejections keep their specific message.
- **Rehydration.** `SchemaRegistry.LoadAsync` adds the same three fields to its rehydration check (`SchemaRegistry.cs:189-216`), so a stored descriptor that fails takes the existing reject-and-log path.
- **Quoting helper.** One identifier helper in `Iverson.Sql` returns `"name"` and refuses `"` or NUL. It is applied to every interpolation of a client-derived identifier: `EntityRepository`, `OutboxWriter` and the entity DDL in `PostgresSchemaManager`. The parameter names built in `UpdateColumnsAsync` (`@{c}`, `EntityRepository.cs:105`) get the identifier pattern instead. Names read back from the Postgres catalog, and constant table names, are left as they are.

### 4b. Template relations removed (#12)
- **Why.** The guard `RequireNotRowOwned` admits only targets with no `OwnerField` and no `RowPermissions`, and the evaluator denies every caller on exactly those types. Template traversal can therefore reach only data no caller may read. Only server unit tests use it (Verified assumptions 23 and 24).
- **Registration.** Registration rejects any template with a OneHop (`{Rel.Prop}`) or Block (`{#Rel}…{/Rel}`) segment, with InvalidArgument: "document templates may reference only the type's own properties."
- **Legacy templates.** The parser and the segment types stay, because stored descriptors hold parsed segments and `LoadAsync` doesn't re-validate templates. `DocumentRenderer` renders OneHop and Block segments as empty and fetches no related rows. A legacy template therefore stops copying related data on its next render.
- **Removed:**
  - the re-render consumer's relation-dependent enqueueing;
  - phase-2 re-validation of dependent templates when a target changes;
  - `RequireNotRowOwned` and its helpers.
- **Kept:** the re-render queue and worker, which still serve the type-level backfill.

### 4c. Enrichment (#11)
`ValidateEnrichmentTargets` gains two rules, both InvalidArgument at registration. An empty role list means unrestricted, so each rule acts only on non-empty lists.
Both rules use the evaluator's own comparisons (`RowFieldAuthorizationEvaluator.cs:35, 69-80`): a FieldPermission's `FieldName` matches a property `OrdinalIgnoreCase`, and role names compare ordinally, the way the evaluator matches `groups`.

1. **Write.** No `FieldPermission` with a non-empty `WritableRoles` may name an enrichment target. The model is the target's only writer, so that restriction couldn't be enforced.
2. **Read.** For **each** FieldPermission naming an embedding or chunk source property with a non-empty `ReadableRoles`, every enrichment target must carry **at least one** FieldPermission with a non-empty `ReadableRoles` that is an ordinal subset of that source FieldPermission's roles. The evaluator excludes a field when any FieldPermission naming it excludes the caller, so this condition is exactly "every reader of the target can read every source".

No conformance, LoadTest or test model is rejected (Verified assumption 26).

## 5. Runtime authorization (closes #20, #3, #13, #14, #21)

### 5a. Owner-private types (#20)
A type is **visible** to a caller when it is registered and is not `IsForeignTo` the caller (`SchemaTenantScope.cs:22`). Unowned types, which are what SDK registrars produce (Verified assumption 31), stay visible to every tenant.

One helper, `TryGetVisible(string typeName, ClaimsPrincipal? actingUser)`, replaces every by-name lookup a data RPC makes (Verified assumption 28):
- **Primary type:**
  - the three `RequireSchema` helpers (Mapping, Persistence, Search; MatchPattern uses Search's);
  - Retrieval's inline lookups (`ObjectRetrievalGrpcService.cs:30-32, 82-90`).
- **Other types a request names:**
  - the joined-type lookup in `EvaluateAuthorization` (`ObjectSearchGrpcService.cs:1091-1095`);
  - the pipeline-source filter (`:879-883`);
  - relation hydration (`EntityRelationResolver.cs:153, 189, 232`), where a foreign related type is treated like a missing one.
- **Builder lambdas.** The registry lambda handed to every StarRocks builder call (`ObjectSearchGrpcService.cs:105, 827, 905`; `AggregateReader.cs:114`). This closes the step-name collision check (`StarRocksPipelineBuilder.cs:151`) as an existence signal.

The visibility check runs first in each RPC, before key extraction, the fetch and write authorization. The only earlier check is Mapping Get's depth limit, which depends on neither identity nor type. Both invisible cases, unregistered and foreign-owned, get the same answer:

| RPC | Answer for an invisible type |
|---|---|
| Mapping Get, Mapping Delete | Completed call, `Success=false`, `Error = "'{request.TypeName}:{request.Key}' not found."`. This is exactly an absent key's answer (IVC-ERR-004's shape). Today an unregistered type answers FAILED_PRECONDITION. |
| Mapping Update, Persistence Update | NOT_FOUND with the key-less message `"'{request.TypeName}' row not found."`. The key column of an invisible type is unknown, so no key is read. |
| Retrieval Get / GetMany | `Found=false`, using the unregistered branch exactly: one result per raw `request.Keys` entry, no de-duplication, no GUID check. |
| Mapping/Persistence Post; Search, Aggregate, GroupBy, Pipeline, SearchSimilar, SearchChunks, MatchPattern; any type named in Search, Aggregate or GroupBy `Joins` | FAILED_PRECONDITION with the existing message `"No schema registered for '{typeName}'. Call RegisterSchema first."` (the joined form is unified to the same text). |
| A pipeline join source | INVALID_ARGUMENT with the builder's existing message, `"Step '{step}': join source '{source}' is neither an earlier step nor a registered type."` (`StarRocksPipelineBuilder.cs:342-345`). A pipeline source may be an earlier step's name, so the source filter drops invisible names rather than throwing, and the builder answers. Unregistered sources already get this answer today. |

`request.TypeName` is echoed, never the canonical `schema.TypeName`, so a foreign type's casing doesn't leak.

**Conformance pins.**
- IVC-IDN-008 still answers code 5: the other tenant's Update now meets an invisible type.
- IVC-ERR-005 still answers code 9: Post to an unregistered type.
- No other scenario crosses tenants (Verified assumption 30).
- Requirement prose that describes a header-less PERMISSION_DENIED for owned types (`IdentityScenario.cs:100-108`, `Requirements.cs:592-593`) is corrected in comments only; no assertion changes.

### 5b. OneToMany hydration (#3)
- **Resolver.** `ResolveOneToManyAsync` evaluates the child's Read decision before fetching. When `decision.AllowedFields` is non-null and excludes `relation.ForeignKey`, the relation is skipped and the nav property is left absent, as ManyToOne does when it resolves nothing.
- **Catalog.** `SchemaCatalogReader.ForeignKeyIsReadable` widens to receive the child's decision, from the pass-one `survivors` map. It applies the same test before advertising a OneToMany edge, instead of returning `true` unconditionally (`SchemaCatalogReader.cs:147-162`).

### 5c. RemoveUser (#13)
After `DeactivateUserAsync` succeeds, `RemoveUser` calls `RevokeAsync(user.Uid)` and `Invalidate()` again, mirroring `SetTenantAdmin` (`TenantAdminGrpcService.cs:84-90`). `RevokeAsync` is an upsert where the later `now()` wins.

### 5d. Update owner mismatch (#14)
- **The answer.** On the Update path, an owner mismatch raises the same NOT_FOUND, with the same message, as an absent row (`AuthorizationFieldMasking.cs:110-112`). The `OwnerMismatch` audit entry is kept.
- **The order.** The owner-mismatch check moves ahead of the tenant-immutability check (`:148-153`). On legacy schemas whose tenant column is client-declared, that check would otherwise answer PERMISSION_DENIED for another owner's row and NOT_FOUND for an absent one.

### 5e. Tenant lifecycle (#21)
- **Terminal deletion.** `SetStatusAsync` refuses, with FAILED_PRECONDITION, a tenant whose current status is `deleted`, before it writes. Both `SuspendTenant` and `ReactivateTenant` go through it (`TenantLifecycleGrpcService.cs:67-71`), so neither can revive a deleted tenant.
- **`/v1/traces`.** After the `ConsoleClient` policy, the handler reads the token's `tenant_id`. When it is present and `TenantStatusCache.GetStatusAsync` doesn't return `active`, the answer is 403. A token with no `tenant_id` passes.
- **`/admin/dlq` and `/admin/dlq/{id}/replay`.** The acting principal's `tenant_id`, when present, gets the same check.

## 6. Testing

**Unit tests (no containers):**
- **Pipeline builder:**
  - exact SQL for INNER, LEFT, RIGHT and FULL joins against an owner- and tenant-scoped fresh type;
  - a self-named join source;
  - a later step reading an outer-joined step.

  The query builder gets RIGHT and FULL cases pinning the outer-`WHERE` predicates.
- **Parser:**
  - an accepted corpus (every expression the suites use today) with canonical SQL pinned per site;
  - a rejected corpus: quotes, comments, `;`, comparison and bitwise operators, unknown functions, a window or aggregate in the aggregate-argument context, a bare aggregate in derive, the depth and length caps (length checked before parse), non-ASCII, numeric overflow, and unauthorized columns in arguments, `PARTITION BY` and `ORDER BY`;
  - range bounds: non-finite values rejected, finite values formatted invariantly under a non-invariant culture.
- **Registration:**
  - non-identifier `PropertyName`, `ForeignKey` and `RelatedType` rejected for each relation kind, with reserved-name messages unchanged;
  - rehydration rejects the same;
  - the quoting helper refuses `"` and NUL;
  - relation templates rejected;
  - a legacy stored relation template renders its relation segments empty and fetches nothing;
  - both enrichment rules, including empty role lists passing, a target role that differs from the source's only by case (rejected), and a source with two FieldPermissions where the target covers only one (rejected).
- **Provisioning:** an AccessDenied from opening the provisioner connection surfaces as `EngagementNotReadyException`. Every other provisioning failure keeps its current exception.
- **Runtime:**
  - a table-driven test asserting identical status, detail and response shape for "unregistered" and "foreign-owned" on every RPC in §5a's table, including malformed keys and GetMany duplicates. The pipeline-join-source row is asserted at builder level (`StarRocksPipelineBuilder.TrackAndValidate` with the visible-registry lambda, both cases), because an RPC test against a mocked search service can't observe the builder's answer;
  - an unowned type stays visible across tenants;
  - the step-name collision with a foreign type answers like an unregistered one;
  - the OneToMany skip and the catalog;
  - the RemoveUser double revoke;
  - Update owner-mismatch NOT_FOUND, including on a legacy tenant-column schema;
  - Suspend and Reactivate refusing a deleted tenant;
  - the `/v1/traces` and `/admin/dlq*` tenant checks.
- **Mutation proof.** For each new guard, its predicate is removed or inverted once, and a test must fail. The guards are the derived-table filter, each parser rule, relation identifier validation, template rejection, the enrichment rules, `TryGetVisible`, the OneToMany skip, the second revoke, owner-mismatch ordering, the deleted-terminal check, both tenant checks, and the provisioner AccessDenied wrap.

**Integration tests.** These use the StarRocks Testcontainers fixtures, one at a time, memory-capped.
- A live RIGHT and FULL pipeline join returns only the caller's rows.
- Under the split:
  - `iverson_app` can't `CREATE DATABASE`, `SET ROLE user_admin` or read `iverson.*`;
  - `iverson_app` passes the readiness gate;
  - the provisioner provisions a new tenant, re-provisions one created before the split, and widens a large text column.

**Conformance.** The full harness runs at the end, with no requirement changes expected.

**Deployment.**
- **Render checks.** `helm template` and the round-10 guard scripts on all five profiles: the new Secret, worker-only provisioner wiring, `Database=` removed, and the job's grants and revokes.
- **Live upgrade on a throwaway kind cluster:**
  1. install `main`'s chart;
  2. upgrade to the branch;
  3. provision a tenant;
  4. read and write through the API, including a pipeline join;
  5. confirm the job is safe to re-run;
  6. confirm `iverson_app`'s grants are tenant roles plus `OPERATE ON SYSTEM` only.

## Known issues / accepted as out of scope

- **A wrong provisioner password stalls the worker's engagement consumer** instead of dead-lettering (user choice, CDR-1 §3.1 B). AccessDenied is wire-level ambiguous: the same code covers "not yet created by the hook" and "wrong password". The readiness checker already accepts the same ambiguity (`EngagementHealthChecker.cs:32-45`).
- **`OPERATE ON SYSTEM` stays on `iverson_app`** (user decision, 2026-10-08). `SHOW BACKENDS` needs it, and it also permits `ADMIN SET FRONTEND CONFIG`, so a stolen query credential can change FE configuration. It gives no data access and can't create users or nodes (live probe). Expression injection can't issue statements, and putting it behind a role wouldn't stop a stolen credential.
- **RegisterSchema's cross-owner PermissionDenied** (`SchemaRegistrationOrchestrator.cs:78-81`) still tells a SchemaAdmin that a foreign type exists. It is privileged-only.
- **`POST /admin/reconcile/{typeName}`** resolves any registered type, foreign included. It is Operator-only and cross-tenant by design.
- **An empty, ungranted `iverson` database remains on every install** (CDR-2 §3.1). The job and `starrocks-init` keep creating it, because their revokes name it and StarRocks refuses a revoke on a missing database (Verified assumption 14).
- **Compose's provisioner password is a dev-only literal**, like `iverson_app`'s. Compose root is passwordless and loopback-only.
- **`WidenLargeTextColumnsAsync`'s `ALTER`** had no proven privilege before this change. No test drives it today; §6 adds one.
- **Plaintext StarRocks transport**, including the job's password literals, belongs to sub-project C (finding #4).

## Verified assumptions

Evidence was gathered on 2026-10-08 at `19ab57c0` by reading code, rendering the Helm chart, and running a throwaway StarRocks 4.1.1 container (`starrocks/allin1-ubuntu:4.1.1`, memory-capped, removed afterwards).

**S1. Query builders**

1. **Join source shape.** A fresh registered source is emitted as `{kind} JOIN <qualified> AS <Src>`, and select items reference `<Src>`. A derived table needs no other change if the moved predicates are unqualified. (`StarRocksPipelineBuilder.cs:640-675`)
2. **Engine semantics (live probe):**
   - **Leak confirmed.** With the owner predicate in `ON`, RIGHT and FULL returned the other owner's row.
   - **Fix confirmed.** A derived table removed it on RIGHT and FULL, and LEFT results were identical.
   - **Dialect.** StarRocks accepts an aliased derived table on every join kind.
3. **Parameter names.** Owner and tenant parameters are per-step, per-join and unique: `s{step}_j{join}_owner`/`_tenant`. (`:656-674`)
4. **Search, Aggregate and GroupBy.** The primary tenant and owner predicates are in the outer `WHERE` on every join-carrying path. (`StarRocksQueryBuilder.cs:91-107, 268-287, 386-401`)
5. **Splice sites.** There are four sites (table in §2), and range bounds are spliced unescaped. (`QB:339-345, 541-542, 1001-1003`; `PB:623, 706-707`)
6. **Aggregate-argument context.**
   - The emission wraps the expression in an aggregate.
   - No production, SDK, conformance, LoadTest, agent or docs expression nests an aggregate or window there.
   - 18 unit-test cases do, and they are re-pointed.
7. **Derive corpus.** It needs empty `OVER ()`, `PARTITION BY`, `ORDER BY` with `DESC`, decimals, and case-insensitive names. Bare aggregates in derive pass validation today but fail in the engine. (`PBT:244, 418, 1043-1048`; `PipelineIntegrationTests.cs:143`)
8. **Resolvers.** There are three resolution mechanisms with pinned messages. A per-site `resolveQuoted` callback fits all three. (`QB:217-242, 532-537, 569-622`; `PB:245, 418`)
9. **Validator dependents.**
   - Comparisons, AND/OR/NOT and string literals have no positive users.
   - The window keywords do have users.
   - `TokenRx`, `IsNonColumnToken` and `RejectForbiddenCharacters` are used only at the four sites.
10. **Error mapping.** `EngagementQueryTranslationException` maps to InvalidArgument at all three RPC entry points. (`OSG:784-786, 832-834, 910-912`)

**S2. Credential split**

11. **Provisioning caller.** The sole production caller of `EnsureTenantProvisionedAsync` is the worker's `EngagementStoreConsumer`. (`EngagementStoreConsumer.cs:71, 164-170`; `Program.cs:448`)
12. **Statements outside a tenant role.**
    - `SHOW BACKENDS` needs `OPERATE ON SYSTEM`.
    - The handshake default `Database=iverson` needs a grant on `iverson`.
    - `WidenLargeTextColumnsAsync` needs `ALTER`.
    - No other schema-change DDL exists.
    - The unscoped paths have no production callers.
13. **Role activation (live probe).**
    - Only `SET ROLE`-named roles are active, and no default role is set.
    - Direct grants applied while `role_t1` was active: another tenant's DB was denied, `iverson.shared` was readable, and `CREATE DATABASE` succeeded.
14. **Grant and revoke semantics (live probe):**
    - **Provisioner flow.** The provisioner (`user_admin` plus direct `CREATE DATABASE`) provisioned a new tenant and re-provisioned a pre-existing one, by granting the tenant role to itself and to `iverson_app`.
    - **Idempotence.** Re-grants are silent, and so is `REVOKE` of an absent privilege.
    - **Role revocation** (second probe, CDR-1 §3.2, same image). `REVOKE user_admin FROM <user>` exits 0 whether the user holds the role, held it before, or never had it. Revoking a custom role that was never granted also exits 0. In a multi-statement batch, a statement after the absent-role revoke still runs.
    - **Missing database** (third probe, CDR-2 §3.1, same image, fresh instance with no `iverson` database). `REVOKE … ON iverson.*`, `REVOKE … ON DATABASE iverson` and `REVOKE … ON ALL TABLES IN DATABASE iverson` each fail with exit 1 (`cannot find db: iverson`), and the failure aborts the rest of a multi-statement batch.
    - **After revocation:**
      - a connection with `-D iverson` fails at handshake;
      - `SHOW BACKENDS` needs `OPERATE`/`NODE`;
      - `SET ROLE user_admin` is refused.
    - **What `OPERATE ON SYSTEM` permits.** It allows `SHOW BACKENDS`, `SHOW PROC` and `ADMIN SET FRONTEND CONFIG`. It does not allow data reads, `CREATE USER` or `ALTER SYSTEM`.
    - **`ALTER` for widening.**
      - Neither `user_admin` nor the tenant role holds `ALTER` on tenant tables.
      - A self-granted `GRANT ALTER ON ALL TABLES IN DATABASE … TO USER provisioner` covers tables created afterwards.
15. **`AddStarRocks` extension.** It can take an optional trailing connection string without breaking callers. (`ServiceCollectionExtensions.cs:8-13`)
16. **Helm Secret pattern.** The existing pattern renders a new key empty on upgrade (`helm template` probe), so a separate Secret is used.
17. **Compose wiring.** Compose's `starrocks-init` and its connection strings are at `docker-compose.yml:92-108, 514, 632`. `generate-compose-secrets.sh` has no StarRocks entries.
18. **Test fixtures.** `TenantIsolationIntegrationTests` and `MatchRowsIntegrationTests` create `iverson_app` through root. Api.Tests StarRocks fixtures use root.

**S3. Schema registration**

19. **Relation names.** Every relation name in the SDK registrars, the conformance models (5 languages), LoadTest and the test fixtures matches the identifier pattern. Validation must follow `RejectReservedTenantName`.
20. **Rehydration reject path.** `LoadAsync` has a reject-and-log path for invalid identifiers. (`SchemaRegistry.cs:189-216`)
21. **Quoting helper.** No Postgres quoting helper exists. The client-derived interpolations are in `EntityRepository`, `OutboxWriter` and `PostgresSchemaManager`, plus `@{c}` parameter names (`EntityRepository.cs:105`).
22. **Template storage.** Templates are parsed at registration and stored as parsed segments, and the renderer reads those segments. `LoadAsync` does not re-validate them.
23. **Template relation dependents.** Template relation rendering, relation-dependent re-render enqueueing, phase-2 dependent-template re-validation and `RequireNotRowOwned` serve only template relations. The re-render queue and worker also serve the type-level backfill.
24. **No client dependents.** No SDK, conformance scenario or LoadTest model uses relation template segments. Only server unit tests do. (`DocumentTemplateValidationTests.cs:351`)
25. **Enrichment source model.** Enrichment sources are the embedding and chunk properties (`BuildSourceText`). FieldPermissions are visible at registration, and an empty role list means unrestricted.
26. **Enrichment rules reject nothing legitimate.** No model combines enrichment with a FieldPermission that the new rules would reject. The harness's rules carry no FieldPermissions. (`Reregistrar.cs:47-52`)

**S4. Runtime authorization**

27. **`IsForeignTo`.** `SchemaTenantScope.IsForeignTo(schema, ClaimsPrincipal?)` is "owned by a tenant other than the caller's; unowned is never foreign". (`SchemaTenantScope.cs:22`)
28. **Lookup inventory.** The by-name lookups are the full set listed in §5a.
29. **Keyed answers.** The visibility check can precede every distinguishing step: key extraction, the fetch (where a malformed key would give 22P02/UNKNOWN), write authorization, GetMany's GUID check and `Distinct`. Absent keys on Mapping Get and Delete answer `Success=false`, as pinned by IVC-ERR-004. (`ObjectMappingGrpcService.cs:98-104`)
30. **Conformance.**
    - The only cross-tenant step is `denied_update_wrong_acting_user`, a Mapping.Update that expects code 5 (IVC-IDN-008).
    - `write_unregistered_type` is a Post that expects code 9 (IVC-ERR-005).
    - Every other scenario runs as tenant A. (`IdentityScenario.cs:233, 466-489`; `Reregistrar.cs:59-74`)
31. **Registrations are unowned in production.** SDK registrars attach no acting header, so production registrations are unowned. Owned types arise only when the channel carries an acting header (the conformance drivers and the experiment scripts), and there the owner is the only user. The console and the agent register nothing.
32. **OneToMany evaluation order.** The evaluator is a pure function of (schema, principal, action), so it can run before the OneToMany fetch. The catalog hook needs the child's decision. (`EntityRelationResolver.cs:220-262`; `SchemaCatalogReader.cs:53-104, 147-162`)
33. **Repeat revocation.** `RevokeAsync` is an upsert where the later `now()` wins (`TokenRevocationRepository.cs:21-29`). `SetTenantAdmin` already revokes twice.
34. **Owner-mismatch branch.** It is its own branch with its own audit reason. Tenant-immutability runs before it, which matters only for legacy tenant-column schemas. (`AuthorizationFieldMasking.cs:143-186`)
35. **Status transitions.** `SuspendTenant` and `ReactivateTenant` both route through `SetStatusAsync`, which reads the current status before writing. `DeleteTenant` writes directly. (`TenantLifecycleGrpcService.cs:67-93`)
36. **Tenant status cache.** `TenantStatusCache.GetStatusAsync(string)` is injectable into both handlers. (`TenantStatusCache.cs:18`)

**Cross-cutting**

37. **No proto or SDK change** is needed.
38. **No production consumer** depends on FAILED_PRECONDITION for unregistered keyed RPCs, or on PERMISSION_DENIED for Update owner mismatch. The test fallout is listed in §2 and §6.
39. **First-upgrade ordering** (CDR-1 §3.1).
    - `job-create-user` is a `post-install,post-upgrade` hook (`job-create-user.yaml:6,15`), so it runs after the upgraded worker is serving.
    - The worker's provisioned tenant/table set is per-process memory (`EngagementStoreConsumer.cs:19,68-72`), so the first event of each tenant and type after a rollout provisions again.
    - `EngagementNotReadyException` is classified transient (`TransientFailures.cs:48`), and the transient arm halts without dead-lettering.
    - The readiness checker already maps `MySqlErrorCode.AccessDenied` to AuthPending for the hook-not-yet-run case (`EngagementHealthChecker.cs:32-45`).
