# CSR Round 11 Sub-project A: Server Authorization and Query Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-10-08-csr-round11-server-authorization-design.md` (commit SHA: `1b7968aa`)

**Goal:** close the round-11 server-side authorization and query-safety findings:
- **Mediums:**
  - #1, pipeline outer joins drop the joined type's row ownership;
  - #2, the OneToMany foreign-key name reaches Postgres SQL unvalidated;
  - #3, OneToMany hydration filters on a foreign key the reader may not read.
- **The rest:** #10 (both halves), #11, #12, #13, #14, #20 and #21, as the spec defines them.

**Architecture:**
- **StarRocks:**
  - a fresh registered pipeline join source becomes a derived table filtered by owner and tenant;
  - client expressions are parsed to an AST and re-emitted, never spliced;
  - tenant provisioning moves to a separate `iverson_provisioner` login, and `iverson_app` keeps only its tenant roles plus `OPERATE ON SYSTEM`.
- **Registration:**
  - relation names are identifier-validated, and Postgres identifiers are quoted by one refusing helper;
  - document templates lose relation traversal;
  - enrichment targets inherit their sources' read restrictions.
- **Runtime:**
  - one `TryGetVisible` lookup makes foreign-owned types answer exactly like unregistered ones;
  - OneToMany hydration respects the child's foreign-key permission;
  - plus RemoveUser, owner-mismatch and tenant-lifecycle fixes.

**Tech stack:**
- .NET 10: `Iverson.Api`, `Iverson.StarRocks`, `Iverson.Sql`, tested with xUnit 2.9, FluentAssertions 8 and NSubstitute 5.3;
- MySqlConnector, Dapper, Npgsql;
- StarRocks 4.1.1 (`starrocks/allin1-ubuntu:4.1.1` in tests and compose);
- Helm 3, with the umbrella chart at `Iverson.Server/deploy/helm/iverson`, and docker compose (podman).

---

## Global Constraints

**From the spec:**
- **One branch, one merge.** No proto change and no SDK change.
- **Conformance.** No requirement changes are expected (Verified assumption 30). If one proves necessary, the project's rule applies: retire the requirement and add a successor.
- **Security write-ups** (comments, test names, docs) describe conditions and behaviour, never step-by-step exploitation. Tests use plainly descriptive malformed inputs, never exploit payloads.
- **`docs/` is gitignored.** Commit docs files with `git add -f`.
- **The user's running compose stack is off-limits.**
  - Never start, stop or modify its `iversonserver` containers, volumes, networks or images (`iverson-api:latest`, `iverson-api:0.1.0`, `localhost/iverson-api:0.1.0`).
  - The same goes for another session's `rfdt-*` containers and its `tei_models` volume.
  - Never run `docker compose up` against `Iverson.Server/docker-compose.yml` without the isolated `-p csr11a` project of Task 11.
  - Live checks use throwaway resources, which they delete afterwards.
- **Memory.** Run one heavy process at a time, because a WSL OOM kills the whole VM. Wrap builds and test runs in `systemd-run --user --scope -q -p MemoryMax=…`. Container-backed test classes (`[Trait("Category", "Integration")]`) count as heavy.

**Session rules:**
- **Location.** Work in the worktree `/home/ben/repositories/Iverson/.worktrees/csr11-server-authz` on branch `csr11-server-authz`. Use absolute paths, and never `cd` to the main checkout.
- **Line numbers.** Line numbers cited in a task are at `1b7968aa`. Earlier tasks shift them, so **match every edit on the quoted text**, not on the number.
- **Commands.**
  - Use `command grep`, because plain `grep` is a gitignore-aware ugrep that skips `docs/`.
  - Every repo-wide grep excludes `.env*`, and nobody reads the main checkout's `Iverson.Server/.env`.
  - The docker CLI is podman.
  - Kill processes by PID, never `pkill -f`.
- **Helm.** Run `helm dependency build` in `Iverson.Server/deploy/helm/iverson` before every `helm template` or `helm lint`, because a stale `charts/*.tgz` renders old subchart content.
- **Commits.**
  - Add the task's files by explicit path. Never use `-A` or `.`, and never touch `.superpowers/` or `docs/security/tma.md`.
  - Messages are lowercase imperative with no prefix, and end with exactly `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Do not use your own model name in the trailer.
- **Scratch.** Scratch files go under `$SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd` (Task 0 creates it), never in the repo.
- **Shell state.** Each bash block runs in a fresh shell: no variable or working directory carries over. Blocks that use `$SCR`, `$WT` or `$LIVE` start with `source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh`. Blocks with repo-relative paths start with a `cd` to the worktree, or to its `Iverson.Server`.
- **Test filters.** `--filter "FullyQualifiedName~X"` is a substring match. Each task's filters were checked for collisions; a new class name must not be a substring of an existing one.

## File Structure

**Task 1: Pipeline joins to registered types become filtered derived tables**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs:638-676`. This is `EmitStep`'s join loop. A fresh registered join source whose constraint carries an owner column or a tenant column is now emitted as `{KIND} JOIN (SELECT * FROM <qualified table> WHERE <owner> AND <tenant>) AS `<Src>` ON <conds>`. The predicates are unqualified and keep the parameter names `s{step}_j{join}_owner` and `s{step}_j{join}_tenant`. Nothing is appended to `ON` any more.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`:
  - re-baseline the four join-predicate assertions at `:1184-1198`, `:1293-1308`, `:1327-1328` and `:1589`;
  - add new tests after `Build_JoinedTypeWildcard_LastColsMatchesTheEmittedProjection`, which ends at `:1605`.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`. Append one theory as the last member of the class, after `BuildGroupBy_DefaultResolvedLimit_MatchesMaxGroupByLimitDefault_DoesNotThrow`.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/PipelineIntegrationTests.cs`. Add one live theory, RIGHT and FULL, against Testcontainers StarRocks.

**Task 2: Client expressions are parsed and re-emitted, not spliced**
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/ClientExpression.cs`. The `ExpressionContext` enum and the `ClientExpression.ToSql` entry point, which runs the length cap, then the lexer, the parser and the emitter.
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionLexer.cs`. The token kinds, the token record and the lexer.
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionParser.cs`. The AST records and the recursive-descent parser: contexts, reserved words, the function allow-list and the depth cap.
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionEmitter.cs`. Writes SQL from the AST.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`:
  - `StepColumns` (`:8-12`) gains `DeriveSql`;
  - delete `TokenRx`, `DeriveFunctions`, `DeriveKeywords` and `IsNonColumnToken` (`:24-53`);
  - `TrackAndValidate` (`:124-130`);
  - `ValidateStepAndComputeOutput`'s return type and returns (`:165`, `:255`, `:313-325`);
  - metric validation (`:239-249`);
  - delete `RejectForbiddenCharacters` and `ValidateDeriveExpr` (`:385-422`), replacing them with `MetricExpressionSql` and `DeriveExpressionSql`;
  - `Build`'s `EmitStep` call (`:512-514`), and `EmitStep`'s signature, metric line and derive line (`:537-547`, `:575`, `:622-623`);
  - `EmitMetric` (`:682-708` at `1b7968aa`; `:689-715` after Task 1).
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`:
  - usings (`:1-5`);
  - `BuildAggregate`'s expression validation and its five emissions (`:248-256`, `:333-345`);
  - `BuildMetricExpr`'s header comment, validation and emission (`:472-478`, `:526-542`);
  - range bounds (`:998-1004`), plus a new `RangeBound` helper.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs` and `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`: the re-baselines in Step 1.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/ClientExpressionTests.cs` (new). The accepted and rejected corpora, emitter canonical form, each site's canonical SQL and pinned messages, and range bounds.

**Task 3: StarRocks credential split, code**
- Modify: `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`
  - `:15-19`: add the trailing constructor parameter.
  - `:33`: add the provisioner connection factory.
  - After `:108`: add `OpenProvisionerConnectionAsync`.
  - `:464-518`: add the null refusal, the provisioner connection, and the two self-grants.
- Modify: `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs:8-28`: add the optional trailing `provisionerConnectionString` parameter and pass it to the repository.
- Modify: `Iverson.Server/Iverson.Api/Program.cs:267-306`: the role-aware provisioner guard, and the new argument to `AddStarRocks`.
- Modify: `Iverson.Server/Iverson.Api/appsettings.Development.json`: add `StarRocksProvisioner`, following the existing Development `StarRocks` entry (root, empty password), so a local worker run with Engagement on still starts.
- Create: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs`: shared fixture helper that creates both logins with `job-create-user.yaml`'s statements and returns both connection strings.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs:20-72, 323-324`: use the helper and pass both strings.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs:17-38`: use the helper and pass both strings.
- Test (create): `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryProvisioningTests.cs`: unit tests for the AccessDenied wrap and the null refusal.
- Test (modify): `Iverson.Server/Iverson.StarRocks.Tests/ServiceCollectionExtensionsTests.cs`: `AddStarRocks` hands the provisioner string to the repository.
- Test (create): `Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs`: heavy Testcontainers tests:
  - what `iverson_app` can no longer do;
  - the readiness gate;
  - new-tenant provisioning;
  - re-provisioning a tenant created before the split;
  - widening, for a table created after the grant and for one created before it.

**Task 4: StarRocks credential split, deploy**
- Create: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-provisioner.yaml`: the `<rel>-starrocks-provisioner` Secret, lookup-or-generate with `keep`.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml`
  - `:39-43`: the `PROVISIONER_PW` env.
  - `:70-76`: create the provisioner, and revoke the app login's direct grants.
  - `:79-93`: rewrite the comment, removing the stale `EnsureDatabaseAsync` paragraph.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml:100`: drop `Database=iverson;`.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml:84-85`: drop `Database=iverson;`, and add the provisioner password env and `ConnectionStrings__StarRocksProvisioner`.
- Modify: `Iverson.Server/docker-compose.yml`
  - `:82-89`: the comment.
  - `:103-109`: the `starrocks-init` statements.
  - `:510-514`: the api connection string and its comment.
  - `:628-632`: the worker connection string and its comment, plus the provisioner connection string.
- Test (scratch, not committed): `$SCR/check_t4.py`, which asserts the rendered chart per overlay, and a job-versus-compose statement parity check.

**Task 5: Relation identifiers and Postgres identifier quoting**
- Create: `Iverson.Server/Iverson.Sql/PostgresIdentifier.cs` — the one identifier helper: `Quote` (returns `"name"`, refuses `"` and NUL) and `IsPlain` (the client identifier pattern, moved here from the orchestrator so `Iverson.Sql` can use it; `Iverson.Sql` references no other project).
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:1, 23-29` — drop the `Regex` field and its `using`; keep the rationale comment, pointing at `PostgresIdentifier.IsPlain`.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:74-76` — `ValidateIdentifier` on every relation's `PropertyName`, `ForeignKey` and `RelatedType`, after `RejectReservedTenantName`.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:192-204, 573-578, 615-619` — comments that the change makes false.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:796` — `IsValidIdentifier(string?)` delegates to `PostgresIdentifier.IsPlain`.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:197` — the rehydration disjunction gains the three relation fields.
- Modify: `Iverson.Server/Iverson.Sql/EntityRepository.cs` (whole file, 127 lines) — every client-derived identifier interpolation goes through `PostgresIdentifier.Quote`; `UpdateColumnsAsync` checks each column key with `IsPlain` before it becomes `@{c}`. Sites: `:9` (table, key), `:18-20` (key, table, key), `:26` (table, relation FK `columnName`), `:37` (table, relation FK `columnName`), `:41` (table), `:51` (tenant column), `:58-60` and `:68-71` (key, table, key), `:85` (table, key), `:105` (column, and the `@{c}` parameter name), `:119` (table, key).
- Modify: `Iverson.Server/Iverson.Sql/OutboxWriter.cs:21-31` — column, key and table names (`:23-24`, `:28-30`) quoted. `outboxTableName` (`:35, :104, :113, :131`) is a constant and is left alone.
- Modify: `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:14-16, 68-77, 116-119, 134-135, 145-149, 158, 171-174, 182, 191, 193` — entity DDL. Left alone: the catalog-read `orphan` (`:135`), the `SqlType` and `DEFAULT` literals, the constant `SET LOCAL ROLE {roleName}` (`:275`).
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIdentifierTests.cs` (new) — `Quote` wraps, refuses `"` and NUL; `IsPlain` shape.
- Test: `Iverson.Server/Iverson.Sql.Tests/EntityRepositoryTests.cs` — the relation-FK sink refuses a quoted name; `UpdateColumnsAsync` refuses a non-plain parameter name before any statement.
- Test: `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs` — a column name with `"` is refused before the transaction.
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresSchemaManagerTests.cs` — a table name with `"` is refused before connecting.
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs` — a column name with `"` is refused and no table is created (container).
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — non-identifier relation names rejected for every kind and field; reserved-name FK message unchanged for every kind; two comments corrected.
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — rehydration rejects each non-identifier relation field; an identifier-shaped OneToMany still loads.

**Task 6: Template relations removed**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — Phase-1 comment (orig. `:58-61`); the `priorDescriptor` comment's reference to phase 2's `effectiveDescriptors` (orig. `:102-104`); phase-2 cross-type block (orig. `:266-291`) replaced by a per-type template check; `ValidateDocumentTemplate`/`ValidateDocumentSegment`/`RequireScalarProperty` lose their `allDescriptors`/`statusCode` parameters and OneHop/Block are rejected (orig. `:366-488`); `RequireNotRowOwned`, `RequireRelation`, `RequireTargetDescriptor` deleted (orig. `:490-530`); closed-enumeration comment in `RejectReservedTenantName` (orig. `:590-594`).
- Modify: `Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs` (whole file) — parameterless; OneHop and Block render empty; the fetch helpers go.
- Delete: `Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs` — the relation-dependent enqueueing (the whole class is that).
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (`:1019` at `1b7968aa`) — one stale comment naming phase 2's `effectiveDescriptors`.
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (`:453` at `1b7968aa`; Task 3 edits `:267-306` first, so match on the quoted line) — its hosted-service registration.
- Modify: `Iverson.Server/docker-compose.yml` (`:597-599` at `1b7968aa`; Task 4 edits other hunks of this file first, so match on the quoted text) — the worker-role comment that lists the deleted consumer.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:14-20, 75-81, 233, 246, 258-291` (line numbers at `1b7968aa`; Task 5 adds 8 lines at `:197`, so after it these are `:241, :254, :266-299`. Locate by the quoted text) — `_reverseIndex`, `GetDependents`, `RebuildReverseIndex` and its two calls; their only reader was the deleted consumer.
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:170` and `Iverson.Server/Iverson.Sql/EntityRepository.cs` (`FetchByArrayContainsAsync` as Task 5 left it) — deleted; its only caller was the deleted consumer.
- Modify: `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs:160-164, 211-212, 276, 292-295` — comments and crefs naming the deleted class and `GetDependents`.
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/DocumentRerenderQueueWorker.cs:16-20` — doc comment cref to the deleted class. The worker and queue stay (type-level backfill).
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs` — relation templates rejected; relation-rule tests deleted or re-pointed (list below).
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRendererTests.cs` (whole file) — relation tests deleted; legacy stored relation segments render empty.
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — legacy template: own properties only, no related-row fetch; `BuildSut` constructor call.
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs:275` — constructor call only (container suite; compile-only here).
- Delete: `Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs`.
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — `GetDependents` test deleted.

**Task 7: Enrichment target field permissions**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — `ValidateEnrichmentTargets` (orig. `:718-794`): the leading comment's rule count, and two rules appended inside its `if (descriptor.EnrichmentTargets.Count > 0)` block. No other task touches this method.
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — a fixture builder and six tests (one a two-case theory) after `RegisterAsync_WithEmptyExtractHint_ThrowsInvalidArgument`.

**Task 8: Owner-private types answer like unregistered ones**
- Create: `Iverson.Server/Iverson.Api/Schema/SchemaVisibility.cs`. Holds `TryGetVisible`, a static extension on `SchemaRegistry`. Every service already receives the concrete `SchemaRegistry` by injection, and `SchemaTenantScope.IsForeignTo` is a static extension too, so this takes the same shape.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`:
  - `:94` (Get), `:196` (Update) and `:277` (Delete) get an inline visible lookup and their per-RPC answer;
  - `:360-362`: `RequireSchema`, now used only by Post, becomes visibility-aware.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`:
  - `:98` (Update) gets an inline visible lookup and NOT_FOUND;
  - `:169-172`: `RequireSchema` (Post) becomes visibility-aware.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:30` (Get) and `:82` (GetMany): the visible lookup, with the unregistered branches kept exactly.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`:
  - the builder lambdas at `:105-107` (Search), `:827-829` (GroupBy) and `:905-907` (Pipeline);
  - the pipeline-source filter at `:875-883`;
  - `RunAggregationAsync` at `:774-782`, which passes the acting user;
  - `RequireSchema` at `:1067-1071`, which also serves MatchPattern (`ObjectSearchGrpcService.MatchPattern.cs:32`);
  - `EvaluateAuthorization`'s joined lookup at `:1091-1095`, with its message unified.
- Modify `Iverson.Server/Iverson.Api/Search/AggregateReader.cs`:
  - `:99-115`: `RunAsync` gains `ClaimsPrincipal? actingUser`, and its lambda at `:114` becomes visible;
  - `:82-83`: `CountRowsAsync` passes its `actingUser`.
- Modify `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs:153, 189, 232`: `registry.Get(relation.RelatedTypeName)` becomes `registry.TryGetVisible(relation.RelatedTypeName, actingUser)`.
- Modify `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs:100-108` and `Iverson.Server/Iverson.ClientConformance/Requirements.cs:590-593`. Comments only.
- Create test `Iverson.Server/Iverson.Api.Tests/Grpc/OwnerPrivateTypeVisibilityTests.cs` with:
  - the table-driven equal-answer theory over every RPC in §5a's table;
  - the builder-lambda test;
  - relation hydration for three kinds;
  - unowned and owner visibility.
- Test `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`: builder-level join-source and step-name rows, run through `TrackAndValidate` with a visible-registry lambda.
- Modify test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs:1075-1084, 1758-1768, 2348-2357`, and test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs:557-571`. These four tests pin the old FAILED_PRECONDITION answer for unregistered keyed RPCs; see Step 1c.

**Task 9: OneToMany hydration and catalog follow the child's foreign-key permission**
- Modify `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs:220-262`. `ResolveOneToManyAsync` evaluates the child's Read decision before `FetchByColumnAsync`, and skips the relation when `AllowedFields` excludes `relation.ForeignKey`.
- Modify `Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs:54-58, 86-87, 99-104, 126-162`:
  - pass one keeps each survivor's decision by name;
  - `ForeignKeyIsReadable` takes that map and tests a OneToMany's foreign key against the child's decision.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`:
  - a `Mapping.Get` at depth 1 with a restricted child foreign key (skipped, never fetched);
  - a readable child foreign key (still resolves);
  - the catalog not advertising the restricted edge.

**Task 10: Runtime fixes for RemoveUser, owner mismatch and tenant lifecycle**
- Modify `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs:59-71`. `RemoveUser` revokes and invalidates a second time after `DeactivateUserAsync`.
- Modify `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:45-49, 105-171`:
  - an owner mismatch answers the absent-row NOT_FOUND, with the same message, built once;
  - the check moves between TenantMismatch and TenantImmutable;
  - the `deniedMessage` doc is corrected.
- Modify `Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs:86-93`. `SetStatusAsync` refuses a `deleted` tenant with FAILED_PRECONDITION.
- Modify `Iverson.Server/Iverson.Api/Program.cs`:
  - `:608` and `:631`: both DLQ handlers take `ITenantStatusCache` and refuse an inactive acting tenant;
  - `:795`: `/v1/traces` gains `.AddEndpointFilter<ActiveTenantEndpointFilter>()`.
- Modify `Iverson.Server/Iverson.Api/Console/ActiveTenantEndpointFilter.cs:7-8` (doc only). It now also guards `/v1/traces`.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs`. Two RemoveUser order tests are re-pointed to the full double-revoke sequence.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs`. A legacy tenant-column owner-mismatch test.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` and `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`:
  - the owner-mismatch tests are re-pointed to NOT_FOUND;
  - a legacy tenant-column RPC test is added to Mapping.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs`. Suspend and Reactivate of a deleted tenant.
- Create test `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs`. Covers `/v1/traces`, `/admin/dlq` and `/admin/dlq/{id}/replay` through the real pipeline.
- Modify test `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`. `ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds` uses tenant `tenant-a`, which the host's no-op tenant repository doesn't know. It must now fake the cache as active, or it gets 403.

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` (and the CDR rounds' probes) at spec-write time. They are NOT re-verified here; they are trusted as ground truth. They are copied verbatim from the spec's "Verified assumptions" section.

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

## Verified plan-level assumptions

These were newly introduced by this plan and verified at plan-write time, on 2026-10-08, against `1b7968aa`. Every table below was produced by reading code, grepping, rendering Helm on a scratch copy, or decompiling a referenced package, never by recollection. Mechanical errors found during verification were fixed in the task text.

### Verified for Tasks 0 and 11

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path | No `csr11-server-authz` branch or worktree exists yet | `git branch --list csr11-server-authz` returns nothing; `.worktrees/` holds no such directory |
| 2 | Path | The nine suite projects in Task 11 Step 1 exist | `ls -d Iverson.Server/*.Tests` lists Api, ClientConformance, Embeddings, Events, LoadTest, Patterns, Sql, StarRocks and Vector |
| 3 | Command | `generate-compose-secrets.sh` writes the `.env` beside its own repo copy | `scripts/generate-compose-secrets.sh:12` `ENV_FILE="$(dirname "$0")/../Iverson.Server/.env"`; `:16-21` lists `IVERSON_LOADTEST_CLIENT_SECRET`, `IVERSON_SMOKE_TEST_PASSWORD` and `IVERSON_BYPASS_PASSWORD` |
| 4 | Path | Compose service names give `csr11a-iverson-api-1`, `csr11a-iverson-worker-1`, `csr11a-starrocks-1` and `csr11a-starrocks-init-1` | `docker-compose.yml:53, 92, 480, 604` define the services `starrocks`, `starrocks-init`, `iverson-api` and `iverson-worker`; there are two `image: iverson-api` lines (`:484, :608`) |
| 5 | Code validity | The loadtest client is an accepted audience with `schema_admin`; compose seeds legacy tenants | `docker-compose.yml:521` `ValidAudiences__1=dev-iverson-loadtest-client-id`, `:541` `Tenancy__SeedLegacyTenants=true`; harness help (`ClientConformance/Program.cs:323-327`) says the scope is `schema_admin tenant_id_loadtest` |
| 6 | Signature | `mint_acting_user_token.py` prints the token last; its default user is the smoke-test user in `tenant_smoke_test` | `deploy/scripts/mint_acting_user_token.py:67, 527`; `--target` accepts `compose` or `kind` (`:442`); the smoke-test user has `tenant_id: tenant_smoke_test` (`charts/authentik/templates/secret-service-clients.yaml:483-491`) |
| 7 | Code validity | The tenant DB and table names | `TenantIdentifier.cs:19` `iverson_tenant_{tenantId}`; `SchemaBuilder.cs:31` gives `ToSnakeCase()+"s"`, so `probe_authors` |
| 8 | Code validity | A Guid key joins a String column in StarRocks | `SchemaBuilder.cs:390-391`: Guid maps to `VARCHAR(36)`, String to `STRING` |
| 9 | Code validity | The probe console's contract types exist | `object_mapping.proto:44-205` and `object_search.proto:260-301` define every message and field used; cross-check `verified-cross` found that all types resolve |
| 10 | Command | The harness runs every scenario and language by default | `ClientConformance/Program.cs:312, 316`: `--languages`/`--scenarios` "Default: all" |
| 11 | Command | The `scratch_env` toolchain caches exist | `ls -d /home/ben/go/pkg/mod /home/ben/.m2/repository /home/ben/.npm /home/ben/.nuget/packages` lists all four |
| 12 | Command | The allin1 image ships `sh` and `mysql`, and the rendered job keeps `FE_HOST="iverson-starrocks-fe-service"` in `args[0]` | The spec's probes ran `mysql` inside `starrocks/allin1-ubuntu:4.1.1`; `job-create-user.yaml:44-48` has `command: ["sh","-c"]` with `FE_HOST="{{ .Release.Name }}-starrocks-fe-service"`; Task 4 keeps both, and its Step 4 render checks the job |
| 13 | Ordering | Task 11's live check needs Tasks 1–10 merged on the branch | It runs last, by construction |

### Verified for Tasks 1 and 2

| # | Category (1-6) | Assumption | Evidence |
|---|---|---|---|
| 1 | 1 | The five source and test files and `TestSchemaRegistry.cs` exist at the stated paths | `ls Iverson.Server/Iverson.StarRocks{,.Tests}/`: all present. `wc -l`: PB 777, QB 1110, PBT 1865, QBT 3736, PIT 187 |
| 2 | 1 | `Iverson.StarRocks/Expressions/` and the four new files don't exist | `ls Iverson.StarRocks/Expressions`: "No such file or directory". A grep for `ClientExpression\|ExpressionContext\|SqlExpression\|SqlNode\|CallNode\|WindowNode\|ColumnNode` across `Iverson.Server` and `Iverson.Clients/DotNet` (excluding obj and bin) found no hits |
| 3 | 1 | `ClientExpressionTests.cs` doesn't exist, and no type name contains `ClientExpressionTests`, `ClientExpression` or `ExpressionContext` | Same grep as #2. The test-class list (`grep "class "`) has no such class. **HOLDS** |
| 4 | 1 | The SDK default compile glob picks up `Expressions/*.cs` | `Iverson.StarRocks.csproj` has no `EnableDefaultCompileItems` and no `<Compile Remove>`. **HOLDS** |
| 5 | 1 | `InternalsVisibleTo("Iverson.StarRocks.Tests")` | `Iverson.StarRocks.csproj:8-10` (`AssemblyAttribute InternalsVisibleTo`, `_Parameter1=Iverson.StarRocks.Tests`). **HOLDS** |
| 6 | 1 | PB anchors | `:8-12` StepColumns, `:23` IdentifierRx, `:24-53` TokenRx through IsNonColumnToken, `:124-130`, `:165`, `:239-249`, `:255`, `:385-422`, `:512-514`, `:545`, `:575`, `:622-623`, `:638-676` (diffed exactly against draft lines 332-370), `:682`, `:699-707`: all **HOLD**. `:313-325` **FIXED** to `:313-326`, because the quoted block includes the closing brace at 326. `:682` and `:699-707` **FIXED** to note that they sit at `:689` and `:706-714` after Task 1 (see Fixes) |
| 7 | 1 | QB anchors | `:1-5` usings, `:248-256`, `:333-345`, `:472-478`, `:526-542`, `:998-1004`; `BuildRangeSql` ends at `:1013`. All matched by the read and the block diff. **HOLDS** |
| 8 | 1 | PBT anchors, and 6 `"forbidden character"` occurrences | All quoted lines match (block diff). `grep -c 'forbidden character' PBT` gives 6: `:717,735,773,793,1030,1062`. `Build_JoinedTypeWildcard_LastColsMatchesTheEmittedProjection` ends at `:1605`, and `// ── A LEGACY schema` is at `:1607`. **HOLDS** |
| 9 | 1 | QBT anchors; the exact `.Where(...forbidden character)` line occurs 6 times; the last member | `grep -c -x '            .Where(e => e.Message.Contains("forbidden character"));' QBT` gives 6, at `:867,889,958,2281,2308,2906`, and this is the only form in QBT. The class ends `:3725-3735` with `BuildGroupBy_DefaultResolvedLimit_MatchesMaxGroupByLimitDefault_DoesNotThrow`, then `}` at `:3736`. **HOLDS**. The step text "932-937" and "2883-2888" were **FIXED** to `932-938` and `2883-2889`, to match the quoted blocks and the cold list |
| 10 | 4 | Task 1 changes the test files only at the anchors (same line count), after PBT `:1605`, and at the end of QBT | The 1a edits replace one line with one line. 1b inserts after `:1605`, and the highest PBT anchor in Task 2 is `:1062`. 1c is appended after QBT `:3735`, and the highest QBT anchor in Task 2 is `:3300`. **HOLDS** for the test files. The PB side is **FIXED**: Task 1's block is 46 lines replacing 39, so `EmitMetric` moves +7 (see Fixes) |
| 11 | 2 | `Build(...)` signature and tuple return | `PB:448-454`. **HOLDS** |
| 12 | 2/6 | `TrackAndValidate` returns `IReadOnlyList<StepColumns>`; its only callers are `Build` and tests | `PB:109-113`. Grep: the production caller is `PB:458`. There are 51 test call sites in StarRocks.Tests and none in `Iverson.Api`. **HOLDS** |
| 13 | 2 | `BuildSearch`, `BuildAggregate` and `BuildGroupBy` signatures | `QB:34-45`, `QB:169-179`, `QB:360-367`. **HOLDS** |
| 14 | 2 | `AuthorizationConstraint` is a positional record with optional tenant parameters | `AuthorizationConstraint.cs:3-8`. **HOLDS** |
| 15 | 2 | `RangeBucketDescriptor(string Key, double? From, double? To)` | `Aggregation.cs:26`. **HOLDS** |
| 16 | 2 | `AggregationDescriptor` has the named parameters `RangeBuckets` and `Expression` | `Aggregation.cs:15-24` (`RangeBuckets` is `IReadOnlyList<RangeBucketDescriptor>?`, `Expression` is `string?`). **HOLDS** |
| 17 | 2 | `TenantIdentifier.Qualify` | `TenantIdentifier.cs:116-117`. **HOLDS** |
| 18 | 2 | `EngagementQueryTranslationException` is public and sealed, with a `(string)` constructor | `EngagementQueryTranslationException.cs:9` (`public sealed class ...(string message) : Exception(message)`). **HOLDS** |
| 19 | 2 | PBT and QBT helper shapes | PBT: `ArticleSchema :10`, `BuildRegistry :14`, `AuthorSchemaLocal :22` (`authors`, Name), `RegistryWithAuthor :25`, `Request :34`, `AssertInvalid :41`, `NormalizeWs :304`, `JoinedStep :478` (INNER, AuthorId=Id, All plus `Author.Name AS author_name`), `LedgerSchema :970`, `LedgerAuthzWithoutSum :973`, `LedgerRequest :978`, `MetricStep :985` (agg/m/Avg), `DeriveStep :993` (d/x), `TenantCol :1396`, `TagSchemaLocal :28` (`tags`). QBT: `AuthorSchema :12` (Name, Bio, Rating, PublishedAt), `ArticleSchema :15` (Title, Body), `BuildRegistry :1673`, `TenantCol :3152`, `TenantAuthorSchema :3154`, `TenantAuthz(params string[]) :3160`. **HOLDS** |
| 20 | 2 | `TestSchemaRegistry.BuildRegistry(params ...)` is internal static | `TestSchemaRegistry.cs:12-19`. **HOLDS** |
| 21 | 2 | Fixture `Repository`, `QueryAsync` and `ExecuteAsync` run as root on `iverson_test`; the trait and collection | `StarRocksIntegrationTests.cs:60,70-71,81,210-212`; `EngagementRepository.cs:110` (`QueryAsync<T>(string, object? = null)`) and `:136` (`ExecuteAsync`); `PipelineIntegrationTests.cs:7-8`. **HOLDS** |
| 22 | 2 | `QueryAsync` runs the placeholder guard, which accepts `DynamicParameters` | `EngagementRepository.cs:112`; `StarRocksParameterGuard.cs:59-61` (`case DynamicParameters`). **HOLDS** |
| 23 | 2 | `DynamicParameters.Get<string>` on input parameters | Already used by `PBT:335` (`param.Get<bool>`) and `PBT:1590` (`param!.Get<string>("s1_j0_tenant")`). **HOLDS** |
| 24 | 2 | The contract types are public, and `JoinKind` values work in `[InlineData]` | `object_search.proto:142-147` (`enum JoinKind { INNER, LEFT, RIGHT, FULL }`, which become `JoinKind.Inner/Left/Right/Full`); `JoinSpec :149`, `MetricSpec :231`, `PipelineJoin :284`, `DeriveColumn :307`. All are used from tests today. **HOLDS** |
| 25 | 5 | .NET 10 BCL facts | Both projects target `net10.0`, so the default language version is C# 14. `char.IsAscii*` exists since .NET 7. `HashSet<T>.TryGetValue` exists since netcore 2.0 and returns the stored element. A `decimal` default value is legal (`DecimalConstantAttribute`). `decimal.TryParse` returns false on overflow. `decimal` keeps its scale (`100.0`). `CultureInfo.Clone()` is writable. **HOLDS** (by reading; not run) |
| 26 | 5 | A throw expression is legal in `?:` and in a switch arm | Both are C# 7+. **HOLDS** |
| 27 | 5 | `Assert.Throws<T>` checks the exact type | xUnit 2.9.3 (`Iverson.StarRocks.Tests.csproj`). `PBT:43` relies on the same behaviour. **HOLDS** |
| 28 | 5 | Base `WHERE` with and without a primary constraint | `PB:465-503`: an empty `baseWhere` gives `` `OwnerId` = @__ownerVal ``, then the wrap gives `` (`OwnerId` = @__ownerVal) AND `TenantId` = @__tenantVal ``. `PBT:1287` pins the same text. **HOLDS** |
| 29 | 5 | `EmitSelectItem` output | `PB:743-775`. `All` on the input in a join step gives `` `base`.* ``. A sourced aliased column gives `` `Src`.`Col` AS `alias` ``. **HOLDS** |
| 30 | 5 | `joinIdx` starts at 0 per step, and `stepIdx = i + 1` | `PB:627`, `PB:513`. **HOLDS** |
| 31 | 5 | `BuildSelectColumns` with a primary alias | `QB:142-157`. Key first, then the schema columns in order, alias-qualified. **HOLDS** |
| 32 | 5 | `BuildFromWithJoins` parameter naming and alias | `QB:857` (primary alias is the table name), `QB:915,927` (`__owner{map.Count}`, `__tenant{map.Count}`, where `map.Count` is 1 for the first join). **HOLDS** |
| 33 | 5 | `BuildSearch` tail | `QB:134-137`: `LIMIT {limit} OFFSET {offset}`. A null query gives an empty `BuildWhere` (`QB:625`). **HOLDS** |
| 34 | 5 | GroupBy `tableMap` and `QuoteQualified` | `QB:379`, which is always called, and `QB:857` (alias `authors`); `QB:817-825`. `` `authors`.`Rating` `` is already pinned at `QBT:2192`. **HOLDS** |
| 35 | 5 | `Quote`, `ResolveStrict` and `CheckFieldAllowed` are in scope; their messages | These are local functions at `QB:217,223,235-242`, in scope anywhere in `BuildAggregate`. Messages are at `QB:238,241`. **HOLDS** |
| 36 | 5 | `ResolveColumn(schema, …)` | `QB:763-772`: case-insensitive, excludes the tenant column, returns the canonical name. **HOLDS** |
| 37 | 6 | The gRPC layer maps an empty expression to null | `ObjectSearchGrpcService.cs:1210`. **HOLDS** |
| 38 | 5 | Window emission and `SELECT *, …` | `PB:614-625`, `PB:710-731`; `PBT:360` pins the same shape. **HOLDS** |
| 39 | 5 | The derive scope admits window and earlier-derive aliases case-insensitively | `PB:314-322` (`OrdinalIgnoreCase` copy of `output`, then `TryAdd(d.Alias, d.Alias)`). **HOLDS** |
| 40 | 5 | Pipeline aggregate step shape | `PB:575-579`; `PBT:383-384` pins the same shape. **HOLDS** |
| 41 | 5 | The engine accepts the derived table on RIGHT and FULL, and the canonical derive SQL | Spec Verified assumption 2 (ground truth; not re-verified). The derive SQL is exercised live by `DerivedRatio_PercentOfTotal` in Task 2 Step 6. **HOLDS** (per the spec) |
| 42 | 5 | A Dapper dynamic row's `(string)r.author_id` | The same pattern is used at `PipelineIntegrationTests.cs:70` (`(string)r.Id`). **HOLDS** |
| 43 | 6 | Where the deleted helpers are referenced | A repo-wide grep (excluding docs, `.claude/worktrees`, bin and obj) finds code references only in `PB` and `QB` (`QB:250-253,336,474-475,528-531`). Test-comment references are at `QBT:847-851,2168,2263` and `PBT:725,754-759`, and Task 2 rewrites all of them. No script, chart, SDK or other project references them. **HOLDS** |
| 44 | 6 | `StepColumns` is constructed only in PB, at `:119`, `:129` and `:347` | `grep -rn "StepColumns("`: exactly those three plus the declaration at `:12`. No test constructs or deconstructs it; tests read only `.Name` and `.Columns`. **HOLDS**. The record exists exactly as the draft extends it: `internal sealed record StepColumns(string Name, Dictionary<string, string> Columns);` |
| 45 | 6 | `EmitStep`, `EmitMetric` and `ValidateStepAndComputeOutput` each have exactly one caller | `PB:512`, `PB:575`, `PB:128`. All three are private. **HOLDS** |
| 46 | 6 | No other test asserts expression SQL, join-predicate placement, `s1_j0_*` or "forbidden character" | `grep 'forbidden character'` over `Iverson.Server` (excluding bin and obj) finds only the 12 lines in QBT and PBT. A grep for expression-text assertions finds only the lines the draft re-baselines (QBT `:178,903,2198-2200,2257`; PBT `:425,439,807`). `_j0_`/`_j1_` occur only at PBT `:1195-1197,1305-1307,1327-1331,1589-1590`, all handled. The Api.Tests expression tests are mocks: `ObjectSearchGrpcServiceTests.cs:960-985` and `:2253-2276` stub `_search` to throw. **HOLDS** |
| 47 | 6 | Only `PipelineIntegrationTests.cs:143` runs an expression live | `PIT:143` is the `Expr = "100.0 * n / SUM(n) OVER ()"` line (method at `:134`). A grep for `Expression =`/`Expr =` in LoadTest, ClientConformance, ClientConformance.Tests and Api finds only MatchPattern `NamedExpr`, which is a different builder. The SDK corpora are covered by spec Verified assumptions 6, 7 and 9. **HOLDS** |
| 48 | 6 | `Iverson.Api` doesn't use the removed internals | `grep StarRocksPipelineBuilder\.\|StarRocksQueryBuilder\.` in `Iverson.Api`, `Iverson.Api.Tests`, `Iverson.Agents` and `Iverson.Clients` finds comments only (`OSGT:286,479-480`; `SchemaBuilderTests.cs:385`). **HOLDS** |
| 49 | 3 | Filter substrings | The class list for `Iverson.StarRocks.Tests` (`grep "class "`) has no class whose name contains `StarRocksPipelineBuilderTests`, `StarRocksQueryBuilderTests`, `ClientExpressionTests` or `PipelineIntegrationTests` other than the class itself. `OuterJoinToRegisteredType` appears nowhere today. `PipelineIntegrationTests` has 5 `[Fact]`s (`:45,73,101,133,157`), so 5 plus 2 gives 7. **HOLDS** |
| 50 | 3 | `Category!=Integration` excludes every container-starting class | Trait at `MatchRowsIntegrationTests.cs:13`, `PipelineIntegrationTests.cs:7`, `StarRocksIntegrationTests.cs:215` and `TenantIsolationIntegrationTests.cs:16`. `StarRocksContainerReadinessTests` only calls the static `HasReportedDiskCapacity` and starts no container. **HOLDS** |
| 51 | 3 | `systemd-run` is available | `command -v systemd-run` returns `/usr/bin/systemd-run`. **HOLDS** |
| 52 | 4 | The worktree exists, created by Task 0 | `.worktrees/csr11-server-authz` doesn't exist yet, which is expected because Task 0 creates it. The `origin/main` and `main` StarRocks trees are identical (see header). **HOLDS** as an ordering precondition |
| 53 | 4 | Task 1 runs before Task 2, and nothing else edits these five files in between | Task 2 quotes Task 1's state only through the PB line shift, now documented. The Task 2 Step 1 re-baselines compile before the parser exists, and Step 4's red state is a compile error by design. **HOLDS** |
| S1 | 5 | Parser, lexer and emitter compile under C# 14 with `Nullable=enable` | Implicit usings (`ImplicitUsings=enable`) supply `System`, `System.Collections.Generic` and `System.Linq`. `System.Globalization` is imported explicitly where it is used. `TryGetValue` out variables are non-null on the true branch. `string function` is definitely assigned (the else branch throws). `_pos` never moves past `End`: every increment is guarded by a non-`End` kind check. A `public` member of an `internal` class returning an internal type is legal. Switch-statement `case … when` sections each end in `return` or `throw`. **HOLDS** |
| S2 | 5 | Every ClientExpressionTests expected string matches the drafted code | Each position was hand-traced. Lexer: `"` at 17, `\` at 7, `--`/`/*` at 7, `.` at 4, `U+00A0` at 6. Parser: `OVER` at 20 and 12, `SUM` at 12, `ABS` at 31, `DESC` at 39, `Amount` at 24, `Order` at 31. Depth: `Nest(32)` gives position 32, and `Negations(32)` gives position 64 (63 characters of minuses and spaces, then a space, then `a`). Emitter: `a - (b - c)`, `- -a` → `-(-`a`)`, `007` → `7`. The site tests' pinned messages trace through `CheckFieldAllowed` (`QB:238-241`), the `BuildMetricExpr` lambda and the PB callbacks. **HOLDS** |
| S3 | 5 | Every existing test that the draft leaves unchanged still passes under the parser | Hand-traced: QBT `:762-843` and `:2762-2849` (all `*Bio*`, `*NoSuchColumn*` and allowed cases); PBT `:219-250` (`Bogus`; `;` contains `r`; window derive accepted); `:880-963`; `:1001-1008`; `:1033-1040`; `:738-751`; and the `Q1StyleRequest` consumer `:2179` (its only caller). The re-pointed `…CallsFunction_DoesNotThrow` cases and `COALESCE(__TenantId, 0)` pass on today's code (`ABS`, `ROUND` and `NULLIF` are in `DeriveFunctions`; `__TenantId` is unknown to `ResolveColumn`). **HOLDS** |
| S4 | 5 | Task 1's new and re-baselined expected SQL matches the drafted code | Traced for INNER, LEFT, RIGHT and FULL with `tenant_db`: the base has no `WHERE` because `authz` lacks Article. In the self-join, base and joined copy are both filtered, and `isFreshType` is true because `emitted` holds only `base`. In the kept-step test, `s2_p0` comes from `BuildWhere` with no joins. The `TenantAuthzWithAuthor` Author constraint has no owner (`PBT:1537`), so `:1589`'s new assertion holds. `SelectLists` (`PBT:1658-1669`) sees `*` for the derived table, which contains neither `` `Author` `` nor `.*`. **HOLDS** |
| S5 | 6 | Behaviour consumers of the four emission sites | Production reaches them only via `EngagementRepository` (Aggregate, GroupBy, Pipeline). Spec Verified assumptions 6, 7 and 9 cover production, SDK, conformance and LoadTest. Within tests, `grep "Expression *=\|Expr *=\|Expression:"` across StarRocks.Tests (52 hits) was swept one by one. Every hit is either re-baselined by the draft or traced under S3. No missed bare-aggregate derive, aggregate-in-metric, raw-SQL or forbidden-character assertion |

### Verified for Tasks 3 and 4

### Cold list

| # | Cat | Assumption | Evidence |
|---|---|---|---|
| 1 | 1 | `EngagementRepository.cs` anchors: ctor `:15-19` (4 params), `CreateConnection` `:33`, `RewrapIfBackendUnavailableAsync` ends `:108`, `EnsureTenantProvisionedAsync` `:464-518` verbatim | HOLDS. `sed -n` shows the ctor at 15-19, line 33, the closing brace at 108 and `QueryAsync<T>` at 110, the method at 464-518. The apply script matched every quoted before-block exactly once. |
| 2 | 1 | `ServiceCollectionExtensions.cs:8-13` signature, `:27` repository line | HOLDS. The signature is at 8-13 and `return new EngagementRepository(connectionString, logger, resilienceOptions, resolvedQueryLimits);` is at 27, 12-space indent, matched once. |
| 3 | 1 | `Program.cs` `:37` `workloadRole`, `:254` `engagementStoreEnabledAtStartup`, `:267-268`, call ends `:305-306`, then a blank line and `AddQdrant` | HOLDS. Lines 37, 254, 267, 268, 305, 306, 307 (blank) and 308 (`builder.Services.AddQdrant(`). Both before-quotes matched once. |
| 4 | 1 | `TenantIsolationIntegrationTests`: ctor `:23-72`; `using MySqlConnector` used only there; `:323-324` comment | HOLDS. Line 23 is the ctor and line 72 is `}`. `grep MySql` finds `:5` (the using), `:59` (comment) and `:64` (the builder) only. The "(see constructor remarks)" comment is at 323-324. After the edit, no `MySql` is left in the file. |
| 5 | 1 | `MatchRowsIntegrationTests`: ctor `:22-38` verbatim; `_appConnectionString` is a readonly string; `MySql*` used at `:338-349` | **FIXED.** The ctor and the field (`:20`) hold. The other uses are actually `MySqlConnectionStringBuilder` at `:338` and `:363`, and `MySqlConnection` at `:348`, not `:338-341`/`:349`. Corrected in Step 1(c) and in cold item 5. |
| 6 | 1 | The three new test files don't exist | HOLDS. `ls Iverson.StarRocks.Tests` shows none of them. |
| 7 | 2 | Fixture exposes `ConnectionString` (root, `Database=iverson_test`) and `Repository`; multi-statement `ExecuteAsync` with `'user'@'%'` passes the guard | HOLDS. `StarRocksIntegrationTests.cs:59-60, 66-81` (`Database = "iverson_test"` at `:69`). The guard regex `(?<!@)@(?!@)(\w+)` (`StarRocksParameterGuard.cs:24-25`) skips `@'%'`. Multi-statement precedent: `TenantIsolationIntegrationTests.cs:45-50`. |
| 8 | 2 | `StarRocksCollection.Name` exists and shares one container | HOLDS. `StarRocksIntegrationTests.cs:209-212` has `[CollectionDefinition(StarRocksCollection.Name)]`, `ICollectionFixture<StarRocksContainerFixture>` and `Name = "starrocks"`. |
| 9 | 2 | `InternalsVisibleTo` gives the tests `TenantIdentifier.DatabaseName/RoleName/Qualify`, `StarRocksSchemaManager.BuildCreateTableDdl` and the new internal method | HOLDS. `Iverson.StarRocks.csproj:8-10`. Internal members: `TenantIdentifier.cs:19, 63, 116` (`Qualify(string?, string)`) and `StarRocksSchemaManager.cs:5`. |
| 10 | 2 | `EngagementHealthChecker(string).CheckHealthAsync()` returns a public `EngagementHealthStatus` with `Healthy` | HOLDS. `EngagementHealthChecker.cs:5, 12`; `EngagementHealthStatus.cs:3-9`. |
| 11 | 2 | `EngagementTableSchema` and `EngagementColumnSchema` are positional records | HOLDS. `EngagementTableSchema.cs:3-14` defines `(TableName, KeyColumn, IReadOnlyList Columns)` and `(Name, SrType, IsNullable)`. |
| 12 | 2 | `SearchAsync(schema, null, 0, 50, authz: …)` and `AuthorizationConstraint(null, null, null, TenantValue: …)` | HOLDS. `EngagementRepository.cs:520-528`; `AuthorizationConstraint.cs:3-8`. The call shape is copied from `TenantIsolationIntegrationTests.cs:90, 103`. |
| 13 | 2 | `UpsertAsync(EngagementTableSchema, string, string)` | HOLDS. `IEngagementStoreRoles.cs:19`; `EngagementRepository.cs:412`. |
| 14 | 5 | The reflection ctor `MySqlException(MySqlErrorCode, string?, string, Exception?)` exists; `(MySqlErrorCode)5203` | HOLDS. Precedent at `EngagementRepositoryLivenessRewrapTests.cs:20-27`; a 5502 cast precedent is at `:168`. |
| 15 | 2 | The gate throws `EngagementNotReadyException` after the timeout against port 1; Polly handles only transient `MySqlException` | HOLDS. `StarRocksReadinessGate.cs:62`; precedent `StarRocksRepositoryTests.cs:120-141`; `StarRocksResiliencePipelineFactory.cs:19, 37` (`Handle<MySqlException>(ex => ex.IsTransient)`). The `RunAsync` catches (`EngagementRepository.cs:51, 56`) don't catch `EngagementNotReadyException`. |
| 16 | 5 | FluentAssertions 8.11 API | HOLDS. `csproj` pins FA 8.11.0. Precedents: `ThrowAsync<T>().WithMessage("*x*")` at `MatchRowsIntegrationTests.cs:194`; `ContainSingle().Which.Should().Be(1)` at `StarRocksIntegrationTests.cs:258-259`; `BeSameAs` at `EngagementRepositoryLivenessRewrapTests.cs:54`; `TheoryData<string>(keys)` at `:162`. |
| 17 | 5 | Dapper extensions are available in StarRocks.Tests | HOLDS. They come transitively from `Iverson.StarRocks.csproj:16` (Dapper 2.1.89, no PrivateAssets). `MatchRowsIntegrationTests.cs:4` already has `using Dapper;`. |
| 18 | 5 | Mixed deconstruction `(_field, var x) = …` in a ctor | HOLDS. This is a C# 10 feature; the project targets net10.0 (`csproj:3`), whose default LangVersion is 14. |
| 19 | 4 | The app login with only OPERATE passes the handshake (no default DB) and SHOW BACKENDS; the provisioner runs every provisioning statement, including the back-quoted `GRANT ALTER ON ALL TABLES IN DATABASE` | HOLDS per spec VA12/VA14 and `srprobe2.sh` P4 (new tenant and pre-existing tenant, self-grant plus app grant) and P6. Back-quoted DB names in GRANT are already production practice (`EngagementRepository.cs:486-487`). |
| 20 | 4 | `ALL TABLES IN DATABASE` covers a pre-existing table | HOLDS as flagged: it is live-only, and Step 4 tells the implementer to STOP if it fails. VA14 proves the wildcard covers tables created later, so it is very likely a database-level wildcard, but this is not proven. |
| 21 | 4 | `SET ROLE user_admin` after revoke gives a message containing "is not granted to" | HOLDS as flagged: live-only. The same wording is documented for a tenant role that exists but isn't granted (`EngagementRepository.cs:167-173`; `EngagementRepositoryLivenessRewrapTests.cs:167`). Step 4 says to pin whatever wording is observed. |
| 22 | 4 | CREATE DATABASE and SELECT on `iverson.*` without a grant give "Access denied" | HOLDS. The CREATE DATABASE wording is recorded at `TenantIsolationIntegrationTests.cs:41-43`. The table-SELECT wording, "Access denied … privilege(s) on TABLE" (5203), is recorded at `EngagementRepository.cs:185-187`. |
| 23 | 4 | `CHARACTER_MAXIMUM_LENGTH` gives 65533 and then 1048576; root can read it; the widen finishes within 120 s | HOLDS. Widths: production reads the same column (`EngagementRepository.cs:764-771`), and its doc comment records a live 4.1.1 verification (`:747-752`). Constants: `StarRocksLimits.cs:21, 28, 31`. The 120 s bound is live-only. |
| 24 | 6 | Only the helper and the new class's ctor touch `iverson_app` and `iverson_provisioner` | HOLDS. `grep iverson_app` in StarRocks.Tests finds TenantIsolation and MatchRows (both replaced) plus string literals in `StarRocksParameterGuardTests.cs:77` and `EngagementRepositoryLivenessRewrapTests.cs:167`. Pipeline and StarRocksIntegration tests use `fx.Repository` (root). |
| 25 | 3 | Filters match only their intended classes (3→4 SCE tests, 8 split tests) | HOLDS. There is no other StarRocks.Tests class name containing `EngagementRepositoryProvisioningTests`, `StarRocksCredentialSplitIntegrationTests` or `Iverson.StarRocks.Tests.ServiceCollectionExtensionsTests`. `Iverson.Vector.Tests` has its own `ServiceCollectionExtensionsTests`, but it is a different project and namespace. The existing SCE class has 3 `[Fact]`s, and the new class has 8 `[Fact]`s. |
| 26 | 3 | `FullyQualifiedName!~IntegrationTests` excludes every Testcontainers class and keeps both new unit classes | HOLDS. The container users are `StarRocksIntegrationTests`, `PipelineIntegrationTests`, `TenantIsolationIntegrationTests`, `MatchRowsIntegrationTests` and the new `StarRocksCredentialSplitIntegrationTests`, and all contain "IntegrationTests". `StarRocksContainerReadinessTests` only calls a static helper (`:23-31`). |
| 27 | 3 | The Api.Tests build compiles `Program.cs` and `StarRocksReadinessIntegrationTests.cs:67` unchanged | HOLDS. `:67-70` passes 3 positional args, and the 5th parameter is optional. |
| 28 | 6 | Every `EngagementRepository` ctor caller is positional with ≤4 args | HOLDS. The repo-wide grep over Server, Clients and Agents gives exactly the draft's list: `ServiceCollectionExtensions.cs:27`, `StarRocksIntegrationTests.cs:81`, `StarRocksRepositorySearchTests.cs:12`, `StarRocksRepositoryTests.cs:125`, `MatchRowsIntegrationTests.cs:37, 342, 367`, `TenantIsolationIntegrationTests.cs:71`, `StarRocksReadinessIntegrationTests.cs:67`. Nothing in Clients or Agents. |
| 29 | 6 | `AddStarRocks` callers | HOLDS. `Program.cs:268` and `ServiceCollectionExtensionsTests.cs:19, 36, 62`, plus a historic plan doc (not code). |
| 30 | 6 | `EnsureTenantProvisionedAsync` callers | HOLDS. Production: `EngagementStoreConsumer.cs:71`, registered only when `isWorker && Engagement:Enabled` (`:164-170`; `Program.cs:448`). Mocks: `EngagementStoreConsumerTests.cs:44, 443, 474, 505` and `EngagementStoreConsumerKafkaOrderingTests.cs:89`. `LoadTest/Program.cs:125, 418` is an unrelated local function. Other hits are comments only. |
| 31 | 6 | Nothing else reads `StarRocksProvisioner`; no Api.Tests host sets `WORKLOAD_ROLE` or `WorkloadRole` | HOLDS. A grep of .cs, .json, .yaml, .sh and .tf (excluding worktrees) finds `WORKLOAD_ROLE`/`WorkloadRole` only in `Program.cs`, compose and the worker chart, and finds no `StarRocksProvisioner` anywhere. See the consumer note about `appsettings.Development.json`. |
| 32 | 4 | No other task edits these regions | HOLDS. Other drafts touch `Program.cs:453` (t5-t7), `:374, :608-659, :767-796` (t8-t10) and `docker-compose.yml:599` (comment left alone, t5-t7). None overlaps `:254-306`, `EngagementRepository.EnsureTenantProvisionedAsync`, `AddStarRocks`, or compose `:82-109, 510-514, 628-632`. |
| 33 | 1 | The chart dir holds job and secret only; job env `:39-43`, statements `:68-78`, comment `:79-104`, verbatim | HOLDS. `ls templates/` gives job-create-user, pdb-be, pdb-fe, secret, starrockscluster. The apply script matched (b), (c) and (d) once each. |
| 34 | 1 | api `deployment.yaml:100` and worker `:84-86` | HOLDS. api is at `:99-100`; worker is at `:84-85` with `{{- end }}` at `:86`. Both are inside `{{- if .Values.global.engagementEnabled }}` (api `:93`, worker `:78`). |
| 35 | 1 | Compose anchors and `command[0]` | **FIXED.** `:82-89` and `:103-109` hold, and `services.starrocks-init.command[0]` parses. The two connection-string blocks start at `:510` and `:628` (the `# Matches the chart's…` line), not `:511`/`:629`. Corrected in Files, Step 3(i) and cold item 35. |
| 36 | 1 | Rendered names; `args[0]` starts with `set -e`; laptop renders no StarRocks; other overlays do | HOLDS. The checker gives PASS ×5 on the edited copy (local, laptop, aws, azure, gcp). The starrocks dependency has `condition: global.engagementEnabled` (`Chart.yaml`), and `values-laptop.yaml:23` sets it false. |
| 37 | 1 | Overlay files | HOLDS. `values-{local,laptop,aws,azure,gcp}.yaml` and `values-{aws,azure,gcp}.ci-override.yaml` exist; there is none for local or laptop. |
| 38 | 3 | Tools on PATH | HOLDS. `helm` v3.16.4, `kubeconform`, `kube-score`, python3 with PyYAML 6.0.3, and `docker compose config --no-interpolate --env-file /dev/null` (it ran and printed `compose-parses`). |
| 39 | 3 | `charts/*.tgz` must not be committed | **FIXED.** They are gitignored (`.gitignore:76` `Iverson.Server/deploy/helm/**/charts/*.tgz`, and `git ls-files 'charts/*.tgz'` is empty), so they never show in `git status`, and `git checkout -- $C/charts/*.tgz` would fail with "pathspec did not match". `Chart.lock` is tracked and unchanged after `helm dependency build` (diffed). Step 6's note and cold item 39 are rewritten. |
| 40 | 1 | `$SCR` created by the steps | HOLDS. Step 1 runs `mkdir -p $SCR`. |
| 41 | 4 | Statement forms match the VA14 probes; the `mysql -e` batch stops at the first error; `CREATE USER IF NOT EXISTS` keeps the password | HOLDS. The provisioner GRANT forms are identical to `srprobe2.sh` P4. All four REVOKE forms are identical to P5, which ran twice; the second pass shows revoking an absent privilege is silent. The DB-scoped revoke on a missing DB fails and aborts the batch (VA14, third probe), and `CREATE DATABASE IF NOT EXISTS iverson` precedes it in both the job and compose. An absent-role revoke doesn't stop later statements (VA14, second probe), so `ALTER USER … SET PROPERTIES` still runs. `CREATE USER IF NOT EXISTS` keeping the password is today's behaviour, which the job already relies on. |
| 42 | 4 | A non-hook Secret exists before the post-install/upgrade hook; the worker secretKeyRef resolves | HOLDS. The Secret is a plain manifest in the starrocks subchart, and the Job and SA are `post-install,post-upgrade` hooks (`job-create-user.yaml:6, 15`). Helm applies manifests before post-hooks. |
| 43 | 4 | `lookup` is nil under `helm template` | HOLDS. The rendered Secret has a generated 32-char value on every overlay (`password` non-empty check PASS). |
| 44 | 3 | No kube-score critical changes | HOLDS. `criticals changed: 0` ×5 (base 69/56/70/70/70 criticals). |
| 45 | 3 | Parity check passes before and after | HOLDS. "job and starrocks-init statements match" on both the base and the edited copy (11 statements after). Mutation D7 makes it print `DIFFER`. |
| 46 | 4 | Task 3 before Task 4 | HOLDS. Task 4's job revokes `iverson_app`'s `user_admin` (draft `:1125`), and the pre-Task-3 code provisions with it (`EngagementRepository.cs:479-488`). |

### Sibling sweep and extra focus items

| # | Cat | Assumption | Evidence |
|---|---|---|---|
| S1 | 1 | Step 3(d) heading range | **FIXED.** It said `:464-503`; the method spans `:464-518` (503 is `return true;`). |
| S2 | 5 | `check_t4.py` reports a missing `keep` annotation (mutation D5) | **FIXED.** Deleting the only annotation leaves `annotations:` as YAML null. `.get("annotations", {})` then returns `None`, and the checker crashed with `AttributeError` instead of printing `provisioner Secret lacks keep`. It now uses `(… .get("annotations") or {})`. Re-run D5: `FAIL provisioner Secret lacks keep`. |
| S3 | 5 | The order check reports only real misordering | **FIXED.** `idx == sorted(idx)` also fired whenever a statement was missing (the -1 entries), so D2 printed a spurious "out of order" and the base render printed it too. It now compares only the found indices. Re-run: D1 gives "out of order", D2 gives only "missing statements", and the base gives 0 "out of order" lines. |
| S4 | 3 | Task 4 Step 4 `git status` expectation | **FIXED.** Files lists 5 paths (1 new, 4 modified), not "six … five modified". The gitignored tgz never appear. |
| S5 | 5 | M5's "must fail" list | **FIXED** (clarified, no shape change). `srprobe2.sh` P4 issued the self-grant for the brand-new tenant too, so it is unverified whether a role's creator can activate it without one. If not, M5 also breaks the new-tenant and earlier-provisioning tests. The row now says "at least" and asks the implementer to record which tests fail. |
| S6 | 2 | Every before-quote matches the file at `1b7968aa` exactly once | HOLDS. `apply.py`/`apply3.py` replaced each Task 3 and Task 4 before-block with `count == 1`; the compose connection-string block matched `count == 2`, as the draft says. |
| S7 | 2 | `MySqlErrorCode.AccessDenied` name; `OpenAsync` throws `MySqlException{ErrorCode: AccessDenied}` for a refused login | HOLDS. MySqlConnector 2.4.0 (`Iverson.StarRocks.csproj:17`) documents `F:MySqlConnector.MySqlErrorCode.AccessDenied` (`MySqlConnector.xml:2389`). Production already classifies the not-yet-created-user case by exactly `ex is MySqlException { ErrorCode: MySqlErrorCode.AccessDenied }` on `OpenAsync` (`EngagementHealthChecker.cs:32-45`), which the spec cites. Caveat: a disallowed **default database** surfaces as `MySqlEndOfStreamException` at the handshake (`TenantIsolationIntegrationTests.cs:52-63`), not AccessDenied. That is moot here, because every provisioner string in the draft (chart, compose, test helper) omits `Database=`. `UnableToConnectToHost` and `ParseError` are documented at `xml:2374, 2484`. |
| S8 | 4 | The `Program.cs` guard is placed after `workloadRole` and `engagementStoreEnabledAtStartup` are computed, and gates on the same predicate as the consumer registration | HOLDS. `workloadRole` is at `:37-39`, and the enabled flag at `:254`. The guard is inserted after `:267` (`Program.cs` copy `:269-281`). The consumer registers only on `isWorker && Engagement:Enabled` (`EngagementStoreConsumer.cs:167`), the same key read the same way. The worker chart sets `WorkloadRole=worker` (`worker/deployment.yaml:65-66`) and compose sets `WORKLOAD_ROLE=worker` (`:620`); both reach the `?? cfg["WorkloadRole"]` lookup. |
| S9 | 5 | The edited `Program.cs` is syntactically sound | HOLDS by reading the applied copy: braces balance, and the `AddStarRocks` call ends `}, provisionerConnectionString: …);` after positional args. |
| S10 | 3 | Draft Step 2/Step 4 render commands and `check_t4.py` work (re-confirmed) | HOLDS. Base: local, aws, azure and gcp FAIL; laptop PASS. Edited: PASS ×5, kubeconform `Invalid: 0, Errors: 0` ×5, `helm lint` `0 chart(s) failed` ×5. Mutations D1–D7 each produce their named message (D2 and D5 after the S2/S3 fixes). One gotcha I hit myself: rendering without first re-running `helm dependency build` used the stale tgz and showed a reverted mutation. The draft's command shapes always rebuild, so they are correct. |
| S11 | 6 | No other config, compose or deploy surface carries a StarRocks app connection string | HOLDS. A grep for `ConnectionStrings__StarRocks`, `"StarRocks":` and `iverson_app` outside worktrees and docs finds only the chart's api and worker, compose's api and worker, `appsettings.Development.json` (root, see note), and tests. The kind `setup.sh` only installs the operator. LoadTest uses `iverson` as root (unaffected). |

### Verified for Tasks 5, 6 and 7

| # | Cat | Assumption | Evidence |
|---|---|---|---|
| 1 | 1 | `Iverson.Sql/PostgresIdentifier.cs` absent; no `PostgresIdentifier` type or `IsPlain` member | HOLDS. `ls Iverson.Sql/*.cs`: no such file. Grep for `PostgresIdentifier\|IsPlain` in `*.cs`: only `Iverson.Vector.Tests/ResultDiversifierTests.cs:226`, where it appears inside a method name (`…_IsPlainTakeTopK_…`). |
| 2 | 1 | `PostgresIdentifierTests.cs` absent; no class-name collision | HOLDS. No file. Built the FQN list of every class in Api.Tests and Sql.Tests: nothing contains `Iverson.Sql.Tests.PostgresIdentifierTests`. |
| 3 | 1 | The test project paths | HOLDS. `Iverson.Api.Tests/Iverson.Api.Tests.csproj` and `Iverson.Sql.Tests/Iverson.Sql.Tests.csproj` exist; versions are xunit 2.9.3, NSubstitute 5.3.0 and FluentAssertions 8.11.0. |
| 4 | 1 | Every cited line exists at `1b7968aa` | HOLDS, with one fix. Orchestrator `:1`, `:23-29`, `:58-61`, `:74-76`, `:192-204`, `:266-291`, `:366-530`, `:573-578`, `:590-594`, `:615-619`, `:718-794` and `:796` were read and match the quotes. `SchemaRegistry.cs:14-20` (plus blank `:21`), `:75-81`, `:189-216`, `:233`, `:246` and `:258-291` match. `EntityRepository.cs` is 127 lines. `OutboxWriter.cs:21-31` matches. `PostgresSchemaManager.cs` `:14-16`, `:68-77`, `:116-119`, `:134-135`, `:146-149`, `:158`, `:171-174`, `:182`, `:191` and `:193` match. `IRecordStoreRoles.cs:170`, `Program.cs:453`, `DocumentRerenderQueueWorker.cs:16-20` and `ObjectSearchVectorIntegrationTests.cs:275` match. `PopularitySignalConsumer.cs:160-164`, `:211-212`, `:276` and `:292-295` match. FIXED: the Task 6 Files list said `:292-294`; Step 3e and this item say `:292-295`. |
| 5 | 4 | Task 5 shifts the orchestrator; Tasks 6 and 7 anchor on quoted text | HOLDS. Every Task 6 and Task 7 orchestrator edit quotes unique text, and Task 5 never edits the quoted regions (`:58-61`, `:266-291`, `:366-530`, `:590-594`, `:718-720`, `:787-794`). |
| 6 | 1 | `Iverson.Sql` references no project; Api references Sql; Sql has no `InternalsVisibleTo` | HOLDS. `Iverson.Sql.csproj` has only Dapper and Npgsql PackageReferences. `Iverson.Api.csproj:43` references Sql. `InternalsVisibleTo` appears only in `Iverson.Api.csproj:10-13`. |
| 7 | 3 | No TreatWarningsAsErrors or GenerateDocumentationFile | HOLDS. Grep of the four csproj files and every `*.props`/`*.targets` in the repo finds neither, and there is no `Directory.Build.*`. |
| 8 | 5 | `using System.Text.RegularExpressions;` has no other reader in the orchestrator | HOLDS. `grep Regex SchemaRegistrationOrchestrator.cs` matches only `:29`. |
| 9 | 6 | `IdentifierPattern` is read only by `IsValidIdentifier`; the `IsValidIdentifier` callers | HOLDS. Grep across `Iverson.Server` and `Iverson.Agents` finds `:29`, `:796`, `:800`, and `SchemaRegistry.cs:189, 196, 197, 205`. No test reads `IdentifierPattern`. |
| 10 | 5 | `?? []` target-types to `IReadOnlyList<…>`; `IReadOnlyList<string>.Contains(x, comparer)` binds to LINQ | HOLDS. The orchestrator already writes `segment.Inner ?? []` against an `IReadOnlyList<DocumentSegment>?` (`:453`). `Iverson.Api` has ImplicitUsings, so `System.Linq` is in scope. |
| 11 | 5 | The Npgsql connection string `Port=1;…;Timeout=1` fails with a non-ArgumentException | HOLDS. Npgsql 10.0.3 (`Iverson.Sql.csproj`) accepts the `Timeout` keyword. A refused connect raises `NpgsqlException` (a `DbException`), which is not an `ArgumentException`. The test never reaches the connect unless M11 or M6 is applied. |
| 12 | 2 | Proto `RelationDescriptor` setters; server record supports `with` | HOLDS. Existing tests set `PropertyName`, `Kind`, `RelatedType` and `ForeignKey` (`SchemaRegistrationOrchestratorTests.cs:211-217`). The server record is positional: `SchemaDescriptor.cs:127-131 RelationDescriptor(string PropertyName, RelationKind Kind, string RelatedTypeName, string ForeignKey)`. |
| 13 | 2 | Proto `FieldPermission` has repeated role lists; `AuthorizationRules.FieldPermissions` takes `Add` | HOLDS. `SchemaRegistrationOrchestratorTests.cs:345-354` uses `FieldPermissions = { new … { FieldName, ReadableRoles = { "admin" } } }`. `SchemaBuilder.cs:213` reads `fp.ReadableRoles` and `fp.WritableRoles`. |
| 14 | 2 | BuildDescriptor maps FieldPermissions in order; `IsSummaryTarget` becomes `EnrichmentTarget(Name, Summary, null)` | HOLDS. `SchemaBuilder.cs:206-214` (a `Select…ToList()` with the same order and names) and `:86-87`. |
| 15 | 2 | `DocumentSegmentKind {Literal, Scalar, OneHop, Block}`; the parser shapes; JSON round-trip | HOLDS. `DocumentTemplate.cs:7` defines the enum and `:19-24` is a flat positional record with no converters. `SchemaRegistryTests.cs:386` (`RoundTripsDocumentTemplateWithAllSegmentKinds`) already round-trips OneHop and Block segments through camelCase options. |
| 16 | 2 | `IntelligenceStoreConsumer` renders from the event payload and writes `"field"`/`"text"`; `"Plain tagline"` becomes exactly one chunk | HOLDS. `IntelligenceStoreConsumer.cs:103-104` parses `ev.PayloadJson`. `:196-198` calls `documentRenderer.RenderAsync(schema, payload, authoritativeTenantValue, ct)`, with the tenant taken from the authoritative row. `:292-294` writes the payload keys `["text"] = chunkText` and `["field"] = cf.PropertyName`. `ChunkWindow(10, 0)` at `:613-618` gives 40 chars, step 40. `SplitIntoChunks` (`:622-642`) yields one trimmed window for 13 chars. The object-point payload has no `"field"` key, so the test's filter sees chunk points only. The red side also holds: the old text is `"Plain taglineRelated name[Related label]"` (40 chars), one chunk ≠ `"Plain tagline"`, and `FetchManyByKeysAsync` is received (`DocumentRenderer.cs:112, 145`). |
| 17 | 2 | `FetchManyByKeysAsync` returns `Task<IEnumerable<KeyedRow>>`; `KeyedRow(string Key, string Data)` | HOLDS. `IRecordStoreRoles.cs:168` and `KeyedRow.cs:3`. |
| 18 | 2 | `TableSchema` and `ColumnSchema` signatures | HOLDS. `IRecordStoreRoles.cs:251-257`. |
| 19 | 6 | The `UpdateColumnsAsync` keys are enrichment target names | HOLDS. The sole production caller is `EnrichmentConsumer.cs:193`, and the keys come from `columns[target.ColumnName]` (`:332`). Test callers pass only `["Title"]`, `["Body"]` and `["name"]`, all plain. |
| 20 | 2 | Evaluator semantics | HOLDS. `RowFieldAuthorizationEvaluator.cs`: `userGroups` is a default `HashSet<string>` (ordinal). `excluded` takes any FieldPermission with `roles.Count > 0 && !roles.Any(userGroups.Contains)`, and `.ToHashSet(StringComparer.OrdinalIgnoreCase)` makes FieldName case-insensitive. |
| 21 | 5 | `SchemaRegistrationOrchestratorTests` fixtures and anchors | HOLDS. `_registry` and `_sut` (`:21-22`); `SimpleType` at `:44` adds `Id` and `TenantId`. The anchors are at `:300`, `:520`, `:1137`, `:204` and `:263`, and the two quoted comments match `:206-208` and `:265-269` verbatim. The file imports both `Iverson.Api.Schema` and `Iverson.Client.Contracts`, so the draft's `Client.Contracts.` qualification is required and correct. |
| 22 | 5 | `SchemaRegistryTests` helpers | HOLDS. `_repository` (substitute, `:25`), `_sut` (`:26`), `_logs` (a private `RecordingLogger<T>` with `Entries` of `(Level, Message, …)`, `:21`, `:763`), `SerializeAsRegistryWould` (`:755`), and the anchor at `:594`. `ArticleSchema()` is admitted by `LoadAsync` (`:504-511`). `ArticleWithOneToManySchema()` (`SchemaFixtures.cs:77-93`) has identifier-shaped names throughout. |
| 23 | 5 | `DocumentTemplateValidationTests` helpers and name resolution | HOLDS. `_sut`, `_registry`, `_schemaManager`, `WidgetType`, `SimpleType` (`:20-66`). `:400-414` already uses unqualified `SchemaDescriptor`, `ColumnDescriptor` and `ChunkDescriptor`, plus `Iverson.Api.Schema.`-qualified authorization records. A bare `Schema.X` would resolve to namespace `Iverson.Api.Tests.Schema`, so the full `Iverson.Api.Schema.` prefix is needed. |
| 24 | 5 | `IntelligenceStoreConsumerTests` helpers | HOLDS. `_registry`, `_entities`, `_vectorWrite`, `JsonOptions` (`:44`), `Serialize` (`:103`), `BuildSut` (`:105-117`, renderer at `:113`), `TemplateOnlyDocSchema()` (`:2173`), `TemplatedDocEvent` (`:2188`) and the anchor at `:2285`. `SchemaFixtures.AuthorSchema()` (`:39`, TypeName `Author`) and `TagSchema()` (`:212`, TypeName `Tag`, column `Label`). The default `FetchByKeyAsync` stub returns `"TenantId":"test-tenant"` (`:95-96`). The file does not import `Iverson.Client.Contracts`, so `RelationDescriptor` is unambiguous. |
| 25 | 5 | `EntityRepositoryTests.ArticleSchema`, `OutboxWriterTests._sut/_txRunner`, `PostgresIntegrationTests.UniqueTable/_repo/_schemaManager` | HOLDS. `EntityRepositoryTests.cs:9-12`, with anchors at `:48` and `:285` (last). `OutboxWriterTests.cs:12-21`, anchor `:55`. `PostgresIntegrationTests.cs:42-47`, anchor `:52`, and `_repo.QuerySingleOrDefaultAsync<int>(sql)` is used the same way at `:63`. |
| 26 | 5 | NSubstitute auto-completes un-stubbed `Task` members | HOLDS. The existing tests rely on it: `SchemaRegistry.RegisterAsync` over a substitute `_sql` (`DocumentTemplateValidationTests.cs:419`), and a substitute `_schemaManager` in every orchestrator test. |
| 27 | 5 | FA `ThrowAsync` captures a synchronous throw from `Func<Task>` | HOLDS. FA 8's `InvokeWithInterceptionAsync` invokes the delegate inside its try. `FetchByColumnAsync`, `FetchAllAsync` and the other block or expression bodies throw synchronously, and `UpdateColumnsAsync` and `OutboxWriter` are `async`, so the throw lands in the Task. Either way the throw is captured. |
| 28 | 3 | No filter collides | HOLDS. I built every `namespace.class` FQN in Api.Tests and Sql.Tests. Each of the 11 filter strings matches exactly one class (itself). |
| 29 | 3 | `Category!=Integration` starts no container | HOLDS. Every container-starting class carries `[Trait("Category", "Integration")]`. Sql.Tests: `PostgresIntegrationTests:37`, `TenantScopedAccessIntegrationTests:21`, `DlqRepository…:54`, `TenantRepository…:51`, `TokenRevocation…:34`. Api.Tests: `EngagementStoreConsumerKafkaOrdering`, `UpdateCarryForwardPostgres`, `ObjectSearchVectorIntegration`, `RegisterSchemaAuthorizationIntegration`, `SimilarityResolverIntegration`, `DocumentRerenderQueuePostgres`, `ReconciliationQueuePostgres`, `StarRocksReadinessIntegration`, `AuthentikRecoveryFlowIntegration`, one trait each. The untraited `IClassFixture` users are `WebApplicationFactory` hosts with no-op fakes (`AuthentikTrustTests:94` `StartAsync` is a Kestrel app, not a container). |
| 30 | 3 | The two Postgres suites each start their own container | HOLDS. `PostgresIntegrationTests:38-40` uses `[Collection(ContainerCollection.Name)]` and `IClassFixture<PostgresContainerFixture>`. `TenantScopedAccessIntegrationTests:21-23` uses `IClassFixture<PostgresContainerFixture>` with no collection. |
| 31 | 3 | The Task 5 Step 3 grep prints only the `orphan` line | FIXED. The pattern `"{schema\.` also matches `PostgresSchemaManager.cs:162` `var policyName = $"{schema.TableName}_tenant_isolation";`, which the draft rightly leaves alone (it becomes a parameter value, and its DDL use is quoted). The expected output now names both lines. No other line in the three files matches after the draft's rewrites. |
| 32 | 4 | Ordering 5 → 6 → 7; no other task edits these files | HOLDS. Grep of `draft-t0-t11`, `t1-t2`, `t3-t4` and `t8-t10` finds no edits to the orchestrator, `SchemaRegistry.cs`, `EntityRepository.cs`, `OutboxWriter.cs`, `PostgresSchemaManager.cs`, `DocumentRenderer.cs`, `IRecordStoreRoles.cs`, or these test files. They only mention `SchemaRegistry.cs` in prose. |
| 33 | 4 | `Program.cs` and `docker-compose.yml` hunks don't overlap | FIXED. Task 3 edits `Program.cs:267-306` (not `:267-273`) and Task 4 edits compose `:82-89`, `:103-109`, `:511-514` and `:629-632` (`draft-t3-t4.md:33, :1501`), so `Program.cs:453` shifts before Task 6 runs. Task 6 now matches on the quoted line. The stale compose comment (see consumer impact) is now edited by quoted text too. |
| 34 | 4 | Tasks 8-10 keep `FetchByColumnAsync`'s signature and don't call the removed members | HOLDS. `draft-t8-t10.md:151, 1427, 1446, 1487` call `FetchByColumnAsync(TableSchema, string, string, EntityAccess)` unchanged. No `FetchByArrayContainsAsync`, `GetDependents` or `DocumentRenderer(` appears there. |
| 35 | 6 | `FetchByArrayContainsAsync` callers; sole implementation | HOLDS. The callers are `DocumentRerenderConsumer.cs:155` and `DocumentRerenderConsumerTests.cs:237, 394`, nothing else. The only `: IEntityRepository` is `EntityRepository.cs:5`. Sql.Tests has no test of it. |
| 36 | 6 | `GetDependents` and `RebuildReverseIndex` readers | HOLDS. `DocumentRerenderConsumer.cs:42` and `SchemaRegistryTests.cs:244`; the `PopularitySignalConsumer.cs:163` cref is removed by Step 3e. The Go and Java `GetDependents` hits are proto-generated `SchemaRequest.dependents` accessors, unrelated. |
| 37 | 6 | `new DocumentRenderer(` sites; DI | HOLDS. `DocumentRendererTests.cs:32`, `IntelligenceStoreConsumerTests.cs:113`, `ObjectSearchVectorIntegrationTests.cs:275`; `Program.cs:342` `AddSingleton<DocumentRenderer>()`. |
| 38 | 6 | `DocumentRerenderConsumer` references | HOLDS. Code: `Program.cs:453`. Crefs: `PopularitySignalConsumer.cs:160` and `DocumentRerenderQueueWorker.cs:19`. Prose: `PopularitySignalConsumer.cs:211, 276, 292`, `SchemaRegistry.cs:78`, `docker-compose.yml:599`, `PopularitySignalConsumerTests.cs:456, 469`. Only historical `docs/` and AdminUI hits remain; AdminUI reads the queue-depth gauge, not the consumer. No Helm, Prometheus or deploy file names it, and the Kafka ACL uses the `iverson.consumer.*` prefix (`charts/kafka/templates/kafkauser.yaml:14`). |
| 39 | 6 | `SuppressRerenderCascade` is read only by the consumer and still set by the worker | HOLDS. Its readers are `DocumentRerenderConsumer.cs:40` and the worker test `DocumentRerenderQueueWorkerTests.cs:135`, which asserts the flag the worker sets. The worker sets it at `DocumentRerenderQueueWorker.cs:117`. |
| 40 | 6 | No fixture or model has a non-identifier relation name; no other test registers a relation template | HOLDS. VA 19 is ground truth. In addition, every server `RelationDescriptor(...)` construction in Api.Tests uses plain names (one uses lowercase `"author"`, which is still plain). The only relation templates outside `DocumentTemplateValidationTests` are `DocumentRerenderConsumerTests.cs:93` (deleted), `SchemaRegistryTests.cs:229` (deleted) and `:391` (registry-direct, unchanged), plus parser unit tests. |
| 41 | 6 | The conformance fake's fidelity only | HOLDS. `TenantRejectedScenarioTests.cs:94-102` drops one arm and falls through. `RunAsync_OneArmDropped_…` (`:226-241`) asserts only that site's assertions redden, and no assertion depends on the real server's generic message. |
| X1 | 6 | Task 6 scope: does `DocumentRerenderConsumer` do anything besides relation-dependent enqueueing? | HOLDS: no. `DocumentRerenderConsumer.cs:35-129`: loop breaker, `registry.GetDependents`, then three per-kind `EnqueueEntityAsync` paths. Nothing else (no type-level enqueue, no metrics, no other side effect). |
| X2 | 6 | Do the queue and worker stay, and do they still have a producer? | HOLDS. The producer is `SchemaRegistrationOrchestrator.cs:360-361` (`rerenderQueue.EnqueueTypeAsync(typeName)` on template add or change), which Task 6 keeps; `DocumentTemplateValidationTests.cs:514-570` pins it. The worker is registered at `Program.cs:461-463` (untouched) and expands type rows with `EnqueueEntityAsync` (`DocumentRerenderQueueWorker.cs:149`). `IDocumentRerenderQueueRepository` is unchanged. |
| X3 | 6 | Other references to the deleted symbols (tests enumerating hosted services, DI, fakes) | HOLDS. The hosted-service enumeration tests only check `EnrichmentConsumer` and `EngagementStoreConsumer`. `StartupNoOpFakes` and `AuthTestWebApplicationFactory` fake only the queue repository, which stays. |
| X4 | 6 | `DocumentRerenderConsumerTests` count | Resolved: 19 cases (16 `[Fact]`s plus the `[Theory]` `Dispatch_AllEventTypes_EnqueueDependent` with 3 `InlineData`). FIXED in Task 6 Step 4: the net Api change is −46 + 6 = −40. |
| X5 | 5 | Kept `DocumentRendererTests` bodies and `WidgetSchema` match the original | HOLDS. A diff of the 5 kept tests and of `WidgetSchema` against `DocumentRendererTests.cs:40-71, 116-186, 484-505` shows identical bodies; only comments are dropped. The file has 21 facts (5 kept + 16 deleted). |
| X6 | 5 | `DocumentTemplateValidationTests` deletions number 10 | HOLDS. Rules 3, 4, 5 and 8, four Rule-11 tests, Rule 10, and "root references later dependent" (`:97, 118, 138, 198, 226, 249, 275, 297, 344, 375`). |
| X7 | 5 | Task 6 red/green claims | HOLDS. Today the undeclared-relation message is `"…which is not a declared relation."` (`:510`). The theory's three templates register today: User and Gadget are batched and carry `Name`, and no auth. The legacy re-registration gets FailedPrecondition from `:285-291`. The legacy renderer test is green today because `_registry` lacks the targets (`DocumentRenderer.cs:106-107, 135-136`). |
| X8 | 5 | Task 7 mutation table | HOLDS. Each of M1-M11 was traced against the six tests and kills its named test (for example, M3 Ordinal `targetNames` misses `"summary"`, so the WritableRoles test registers; M8 Ordinal FieldName misses `"summary"`, so NoMoreReadable throws). |
| X9 | 5 | The Task 5–7 mutation must-fail lists hold when executed | Executed at plan-update time (CIR-1 §3.2 A) in a scratch worktree, unit-only (`Category!=Integration`): T5 M1–M10, T6 M1/M3, T7 M1–M6/M8–M11, 22 runs on green baselines (T5 115 Api + 47 Sql, T6 124, T7 89). 19 matched exactly. T5 M4, T7 M2 and T7 M11 also failed further tests, which their rows now name. Every listed test fails under its mutation. Record: `scratchpad/csr11-a/uip1/mutations.md`. |

### Verified for Tasks 8, 9 and 10

| # | Category (1-6) | Assumption | Evidence |
|---|---|---|---|
| 1 | 4 | Task 6 doesn't modify `EntityRelationResolver.cs` | HOLDS. `grep EntityRelationResolver draft-t5-t7.md` finds only `:2158`, a note saying §5a/§5b keep `FetchByColumnAsync`. Task 6's files are orchestrator, `SchemaRegistry`, `EntityRepository`, `Program.cs:453`, … (`draft-t5-t7.md:2118`). |
| 2 | 4 | Tasks 1 and 2 keep the `TrackAndValidate` signature, the join-source and collision messages, the PBT helpers and the `Validate_JoinForwardStepSource_Throws` anchor | HOLDS. `draft-t1-t2.md:495` says "signature is unchanged". Task 2's only `TrackAndValidate` edit is the step loop (`:2002-2020`): `ValidateStepName(step, steps, registry)` stays, and `StepColumns` keeps `Name`/`Columns`. No draft greps for "earlier step nor" or for `Validate_JoinForwardStepSource`. Helpers are listed as existing at `draft-t1-t2.md:2511`. |
| 3 | 4 | No earlier task changes the quoted text this draft replaces | HOLDS. Grepping drafts t0–t7 for each target file finds hits only for `Program.cs` (Task 3 `:267-306`, Task 6 `:453`) and `StarRocksPipelineBuilderTests.cs` (Task 1 inserts after `:1605`). Both are disjoint from this draft's anchors (`Program.cs:608-659, 767-796`; PBT `:622`). |
| 4 | 4 | Task 9 after Task 8, Task 10 after Task 9 | HOLDS. Task 9's old text matches `EntityRelationResolver.cs` exactly once after a simulated Task 8 replace-all (python count = 1). Task 10's edits don't touch any Task 8/9 hunk. |
| 5 | 4 | No other task creates `SchemaVisibility.cs`, `OwnerPrivateTypeVisibilityTests.cs` or `InactiveTenantHttpEndpointTests.cs` | HOLDS. Grepping the other drafts returns no hits, and `grep -r "SchemaVisibility\|TryGetVisible"` over the repo is empty. |
| 6 | 2 | `SchemaRegistry.Get` returns `SchemaDescriptor?` and is OrdinalIgnoreCase; `RegisterAsync` overwrites by `TypeName` | HOLDS. `SchemaRegistry.cs:12` `ConcurrentDictionary(... OrdinalIgnoreCase)`, `:24-25` `Get`, `:245` `_schemas[descriptor.TypeName] = descriptor`. |
| 7 | 2 | `IsForeignTo(this SchemaDescriptor, ClaimsPrincipal?)` is public static in `Iverson.Api.Schema` | HOLDS. `SchemaTenantScope.cs:3, 20-24`. |
| 8 | 2 | `OwnerTenantId` is init-settable and `SchemaDescriptor` is a record | HOLDS. `SchemaDescriptor.cs:3` `public sealed record SchemaDescriptor`; `:93` `public string? OwnerTenantId { get; init; }`. |
| 9 | 2 | Accessor field names per service | HOLDS. Mapping ctor has `_registry` and `_actingUserAccessor` (`ObjectMappingGrpcService.cs:25,31`). Persistence has `registry`/`actingUserAccessor` (`:18,25`). Retrieval has `registry`/`actingUserAccessor` (`:17,19`). Search has `registry`/`actingUserAccessor` (`:33,38`). |
| 10 | 2 | The three private resolvers have a parameter named `actingUser` | HOLDS. `EntityRelationResolver.cs:146, 180, 225`. |
| 11 | 2 | Proto fields as listed | HOLDS. `object_mapping.proto`: `MappingResponse{success,data,trace_id,error}`, `MappingDeleteResponse{success,trace_id,error}`. `object_retrieval.proto`: `RetrievalResponse{found,data,trace_id}`. `object_search.proto`: `SearchRequest.joins`, `GroupByRequest.keys/joins`, `AggregateRequest.aggregations/joins`, `PipelineRequest.steps`, `SearchSimilar/ChunksRequest.property/query`, `MatchPatternRequest.pattern/order_by`, `JoinSpec` 5 fields, `PipelineJoin{source,kind,on}`, `JoinCondition{left,right}`, `SelectItem.all`, `AggregationSpec{name,type,field}`, `AggregationType.TERMS`. |
| 12 | 2 | SearchChunks streams `ChunkSearchResponse`; MatchPattern streams `MatchPatternResponse` | HOLDS. `object_search.proto:12,16`. |
| 13 | 5 | Each invisible answer precedes the Aggregate count check, the MatchPattern limits/compile, the vector property checks and the relation resolver | HOLDS. `RequireSchema` is the first statement at `ObjectSearchGrpcService.cs:712` (count check `:714`), `:159`, `:503`, and `MatchPattern.cs:32` (limits `:35`). The Mapping.Get lookup at `:94` precedes the resolver at `:126`. |
| 14 | 2 | The depth check is Mapping.Get's only earlier check; `MaxRelationDepth` default ≥ 1 | HOLDS. `ObjectMappingGrpcService.cs:90-94`; `EngagementQueryLimitOptions.cs:90` `= 5`, `:92` `Default`. |
| 15 | 5 | Read `AllowedFields` includes `FkColumns` in a case-sensitive set; `UserArticleSchema` lists `ArticleId` | HOLDS. `RowFieldAuthorizationEvaluator.cs:84-92, 116` (`.ToHashSet()`, default comparer); `SchemaFixtures.cs:103`. |
| 16 | 2 | `FieldPermission(FieldName, ReadableRoles, WritableRoles)` | HOLDS. `SchemaDescriptor.cs:171-174`. |
| 17 | 2 | `AuthorizationDecision` positional names | HOLDS. `IRowFieldAuthorizationEvaluator.cs:16-…`; used by name at `AuthorizationFieldMaskingTests.cs:585-587`. |
| 18 | 1 | Existing-row branch order: TenantMismatch, TenantImmutable, OwnerMismatch, OwnerImmutable in `else` `:126-188` | HOLDS. `AuthorizationFieldMasking.cs:126, 143-147, 149-153, 166-171, 181-186, 188`. |
| 19 | 2 | `ITenantStatusCache` is a DI singleton in `Iverson.Api.Tenancy`, resolvable as a handler parameter | HOLDS. `Program.cs:374` is unconditional (not inside a role `if`), and the DLQ maps at `:608/:631` are unconditional too. `ITenantStatusCache.cs:1`. |
| 20 | 5 | `AddEndpointFilter<ActiveTenantEndpointFilter>()` resolves its dependencies; filters run after authz; CORS answers preflights first | HOLDS. The same registration is used 4× at `AdminConsoleEndpoints.cs:57,73,78,83`. Pipeline order is `UseCors` `Program.cs:509`, then `UseAuthentication` `:511`, then `UseAuthorization` `:512`. `AdminConsoleCorsPipelineTests.cs:119-127` expects the OPTIONS `/v1/traces` preflight → 204 (answered before auth, so the filter is never reached). |
| 21 | 5 | `Results.Forbid()` yields 403 in the test host | HOLDS. `AuthenticationPipelineTests.cs:150-170` expects Forbidden from the same `Results.Forbid()` path (`Program.cs:622-623`). |
| 22 | 2 | `TenantRow` is a record with `Status` | HOLDS. `IRecordStoreRoles.cs:249`. |
| 23 | 5 | The statuses written are exactly active, suspended and deleted | HOLDS. The only writes are `TenantLifecycleGrpcService.cs:28,68,71,80` and `Program.cs:721`. The predicate `is null or "suspended" or "deleted"` appears at `ActingUserInterceptor.cs:48`, `TenantAdminGrpcService.cs:101` and `ActiveTenantEndpointFilter.cs:21`. |
| 24 | 6 | `AggregateReader.RunAsync` has exactly two callers and no test calls it | HOLDS. `grep "AggregateReader.RunAsync\|RunAsync("` finds `ObjectSearchGrpcService.cs:774` and `AggregateReader.cs:82` only. Test references to `AggregateReader` are confined to `AdminConsoleDataVolumeEndpointTests.cs` (via `CountRowsAsync`). Nothing in `Iverson.Clients` or `Iverson.Agents` references it. The draft updates both callers (§3.5, §3.6). |
| 25 | 6 | Each `RequireSchema` is private | HOLDS. `ObjectMappingGrpcService.cs:360`, `ObjectPersistenceGrpcService.cs:169`, `ObjectSearchGrpcService.cs:1067` (`private`). MatchPattern uses Search's (`MatchPattern.cs:32`). |
| 26 | 6 | `ForeignKeyIsReadable` is private, and `BuildCatalog` is its only caller | HOLDS. `SchemaCatalogReader.cs:104,147`. `BuildCatalog` callers are `ObjectMappingGrpcService.cs:75` and `ReadCatalog` `:42` (console `AdminConsoleEndpoints.cs:129`). |
| 27 | 6 | The builder lambdas are the only ones in Search/AggregateReader | HOLDS. `grep "registry.Get"` in `ObjectSearchGrpcService*.cs`/`AggregateReader.cs` → `:105, 827, 883, 905, 1068, 1091`, `AggregateReader.cs:52, 114`. `:52` is `CountRowsAsync`, which already checks `IsForeignTo` (`:58`). |
| 28 | 6 | The only tests pinning FAILED_PRECONDITION for an unregistered keyed RPC are Mapping `{Get,Update,Delete}_WhenSchemaNotRegistered_*` and Persistence `Update_ThrowsRpcException_WhenSchemaNotRegistered` | HOLDS. A `grep FailedPrecondition` over Api/Conformance/StarRocks tests finds only those four keyed sites (`ObjectMappingGrpcServiceTests.cs:1083,1767,2356`; `ObjectPersistenceGrpcServiceTests.cs:570`) plus Post/search-family/Aggregate-pipeline sites, which keep it. A script check of every Mapping/Persistence/Retrieval test that calls Get/Update/Delete/GetMany without `RegisterAsync` found only those four plus Retrieval's two (unchanged). |
| 29 | 6 | No test asserts the joined-type text without the suffix | HOLDS. `grep "No schema registered"` over all `.cs` finds only `ErrorContractScenarioTests.cs:24`, which uses the suffixed Post text. |
| 30 | 6 | Only Mapping/Persistence `Update_WithNonMatchingOwner_ThrowsPermissionDenied` assert PERMISSION_DENIED on owner mismatch; the audit tests assert only `ThrowAsync<RpcException>` | HOLDS. I swept every `someone-else`/`OwnershipRequired: true` test. The rest are bypass (`withBypassRole: true`), owner-matching (`OwnerImmutable`), Get/Delete, or the audit tests (`ObjectMappingGrpcServiceTests.cs:2118`, `ObjectPersistenceGrpcServiceTests.cs:861`). `UpdateCarryForwardPostgresIntegrationTests` has no PermissionDenied. |
| 31 | 6 | TenantMismatch staying first keeps the 4 listed tests green | HOLDS. `ObjectMappingGrpcServiceTests.cs:1877` (owner matches, tenant mismatch), `:1900` (bypass), `:1925` (owner matches → TenantImmutable), and `AuthorizationFieldMaskingTests.cs:565` (`OwnershipRequired: false`). |
| 32 | 6 | `ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds` is the only test sending a `tenant_id` acting user to `/admin/dlq*` and expecting success; no `/v1/traces` test carries `tenant_id` | HOLDS. A `grep "admin/dlq\|/v1/traces"` over Api.Tests finds `AuditingAuthorizationMiddlewareResultHandlerTests` (no acting user), `PreAuthPartitionPipelineTests` (no token), `AdminConsoleCorsPipelineTests` (preflights) and `TracesRelayEndpointTests`. The last mints `TestJwtFactory.CreateToken(audience, subject)` (`:65`), which adds only `sub` (`TestJwtFactory.cs:17`). The real cache over `NoOpTenantRepository` would answer null → 403, so the draft's (f) edit is required. |
| 33 | 6 | No non-Integration test registers an owned type and then calls a data RPC cross-tenant | HOLDS. `OwnerTenantId` appears only in `ObjectMappingGrpcServiceTests.cs:595-614` (GetSchema), `AdminConsoleDataVolumeEndpointTests` and `AdminConsoleSchemaEndpointTests`. |
| 34 | 6 | No Integration test reads another tenant's owned type through a data RPC | HOLDS. None of the 9 Integration-tagged classes mention `OwnerTenantId`/`ownerTenantId`. |
| 35 | 5 | `Received.InOrder` fails once a second matching revoke exists | **HOLDS (resolved).** Decompiled `NSubstitute.Core.SequenceChecking.SequenceInOrderAssertion.Assert` (5.3.0): it throws `CallSequenceNotFoundException` when `matchingCallsInOrder.Length != querySpec.Length`. `NSubstitute.Core.Query.RegisterCall` unions every received call satisfying any listed spec into a `HashSet` keyed by sequence number. So the old 2-spec lists see 3 (or 4) matching calls and fail. The new lists hold 3 and 5 specs against 3 and 5 distinct matching calls, in order, so they pass. A repeated spec doesn't double-count, and removing the second revoke gives 2≠3 and 4≠5 (the mutation fails). The existing `SetTenantAdmin_Revoke_*` pair (`TenantAdminGrpcServiceTests.cs:298-321`) relies on the same semantics. |
| 36 | 1/2 | `ActingUserFixtures`, `TestServerCallContext.Create`, `TestJwtFactory.CreateToken`, `ConsoleAudience` | HOLDS. `ActingUserFixtures.cs:11-21`; `TestServerCallContext.cs:26`; `TestJwtFactory.cs:13-14`; `AuthTestWebApplicationFactory.cs:24`. All are in `Iverson.Api.Tests.Helpers`. |
| 37 | 1/2 | `SchemaFixtures` shapes | HOLDS. `SchemaFixtures.cs:35-36` (bypass `test-bypass`), `:39-52` (Author: Name, Bio, `TenantColumn="TenantId"`, `TableName="authors"`), `:77-93`, `:96-112`. |
| 38 | 1/2 | Members of `ObjectMappingGrpcServiceTests`/`ObjectPersistenceGrpcServiceTests` | HOLDS. Mapping: `AuthorId`/`ArticleId` `:41-42`, `MakePayload` `:115`, `MakeContext` `:146`, `OwnedAuthorSchema(bool withBypassRole=false)` `:1272`, anchors `:352`, `:1530`. Persistence: `MakePayload` `:79`, `OwnedAuthorSchema` `:86`. |
| 39 | 1/2 | `AuthorizationFieldMaskingTests`: `Schema()` (BenchmarkTag, `TenantColumn="TenantId"`), private static `EvaluatorReturning`, anchor | HOLDS. `:34-53`, `:55`, `:565`. |
| 40 | 1/2 | `TenantAdminGrpcServiceTests` members | HOLDS. `:25,27,28` (substitutes), `:35` `CallerTenantUser = new("user-1","alice",…,"uid-alice")`, `:53` `ContextForCallerTenant()`. |
| 41 | 1/2 | `TenantLifecycleGrpcServiceTests` members | HOLDS. `:17` `_tenantRepository`, `:23-24` `ExistingTenant` (id `acme`), `:32` `ContextWithUser()`, anchors `:201`, `:231`. |
| 42 | 1/2 | `AuthenticationPipelineTests` exposes `_factory`, imports NSubstitute and DI.Extensions | HOLDS. `:32-38`, `:16-17`. |
| 43 | 1/2 | `KeyedRow`, `IDlqRepository`, `DlqRow` and `DlqReplayRow` are in `Iverson.Sql` | HOLDS. `KeyedRow.cs:3`, `DlqRow.cs:3,7`, `IRecordStoreRoles.cs:223-227`. |
| 44 | 5 | `.Returns(T)` on `Task<T>` members | HOLDS. This is the pattern throughout, e.g. `ObjectMappingGrpcServiceTests.cs:1483` `.Returns(new[] { new KeyedRow(...) })`. |
| 45 | 5 | `ReceivedCalls()` excludes stub configuration | HOLDS. `ObjectSearchGrpcServiceTests.cs:240` asserts `_search.ReceivedCalls().Should().BeEmpty()` after its constructor stubbed all four `_search` members (`:50-69`). |
| 46 | 5 | xUnit 2.9 honours `IAsyncLifetime` on test classes | HOLDS. `Iverson.Api.Tests.csproj` pins `xunit 2.9.3`. The v2 `IAsyncLifetime` signature `Task InitializeAsync()/Task DisposeAsync()` matches the draft. |
| 47 | 5 | `IMessage.ToString()` is deterministic | HOLDS. Generated `ObjectMapping.cs:638-640` `return pb::JsonFormatter.ToDiagnosticString(this);`. |
| 48 | 3 | `Category!=Integration` excludes every container-backed Api class | HOLDS. Every file using a `ContainerCollection` fixture carries the trait (8 classes). The other two hits are not container users: `ObjectSearchGrpcServiceTests.cs:4711` (`ServiceCollectionContainerBuilderExtensions`) and `AuthentikOrchestratorRoleBlueprintTests` (comment only). |
| 49 | 3 | Filter prefixes match only the intended classes | HOLDS. Each class is alone in its namespace (`Iverson.Api.Tests.Grpc.*` for TenantAdmin/TenantLifecycle/AuthorizationFieldMasking/Mapping/Persistence; `Iverson.Api.Tests.AuthenticationPipelineTests`, whose nested `HealthCacheTestFactory` holds no tests). The trailing dot excludes `TenantAdminGrpcServiceAuthorizationPipelineTests`. Method-prefix filters are unique: `GetSchema_WithOneToManyRelation*` matches 1 existing + 1 new, `Get_AtDepth1_*` matches 0 existing, and each `*_WhenSchemaNotRegistered` matches 1. |
| 50 | 3 | `Iverson.ClientConformance.csproj` builds standalone | HOLDS. The file exists (`Exe`, net10.0, refs LoadTest + Client.Core). |
| 51 | 3 | `systemd-run` available | HOLDS. `/usr/bin/systemd-run`. |
| E1 | 6 | Extra focus: every `AggregateReader.RunAsync` caller is updated | HOLDS. See row 24. `CountRowsAsync` has `actingUser` in scope (`AggregateReader.cs:50`), and `RunAggregationAsync` uses the `actingUserAccessor` primary-ctor parameter. |
| E2 | 6 | Extra focus: re-pointed tests for FAILED_PRECONDITION / "No schema registered" on keyed RPCs | HOLDS. The draft covers all four (rows 28, 29). The Aggregate-based comments in `TokenRevocationPipelineTests.cs:112,125,157,173`, `ActingUserInterceptorTests.cs:47` and `ActingUserInterceptorSuspensionTests.cs:119` still see FailedPrecondition, because `new AggregateRequest()` has an empty type name. |
| E3 | 6 | Extra focus: PERMISSION_DENIED on Update owner mismatch | HOLDS. Row 30. |
| E4 | 6 | Extra focus: RemoveUser ordering | HOLDS. Only `TenantAdminGrpcServiceTests.cs:184,200` use InOrder on RemoveUser. No `Received(1).RevokeAsync/Invalidate` exists anywhere. `RemoveUser_RevocationFails_*` throws on the first revoke, so it is unaffected. Quoted production text `TenantAdminGrpcService.cs:66-69` matches. |
| E5 | 6 | Extra focus: `/v1/traces` and `/admin/dlq` tests that would now need an active tenant | HOLDS. Row 32. Only `ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds` is affected, and the draft's (f) fixes it. |
| E6 | 5 | Extra focus: `ActiveTenantEndpointFilter` reuse on `/v1/traces` | HOLDS. The filter reads `HttpContext.User`, which on `/v1/traces` is the console token authenticated by the default scheme under `ConsoleClient`. That is the same principal kind the four console endpoints already filter. Its null/empty-`tenant_id` pass (`ActiveTenantEndpointFilter.cs:17-18`) gives the spec's "no tenant_id passes". It short-circuits with `Results.StatusCode(403)`. With a filter present, RDF executes an IResult returned from a `Task`-returning handler; the handler's own writes still apply when it isn't short-circuited. Construction is unaffected: `ITenantStatusCache` and `AuditLog` are app-wide singletons, and `/v1/traces` sits inside `if (workloadRole == "api")` (`Program.cs:730-797`), where both are registered. Preflights never reach it (row 20). `TracesRelayEndpointTests` tokens carry no `tenant_id` (row 32). |
| S1 | 3 | Step 0's expected output: "each grep prints one line" | FIXED. The helper grep matches five lines (`StarRocksPipelineBuilderTests.cs:10,14,22,34,41`). Step 0 now says the first two greps print one line each and the helper grep prints five. |
| S2 | 1 | Replay DLQ handler at `Program.cs:633` | FIXED. It is at `:631` (`MapPost("/admin/dlq/{id}/replay", …)`). Corrected in the Task 10 Files list and in Step 3.4. |
| S3 | 5 | §3.5 quotes `ActiveTenantEndpointFilter.cs` doc as two lines ending "already do." | FIXED. Line 8 continues "… already do. The rule is <c>ActingUserInterceptor</c>'s: a caller with a", so the quoted block (with its trailing newline) matched 0 times (python count). It now quotes full lines 7–8, re-wraps the replacement, and the range is updated to `:7-8`. |
| S4 | 5 | Task 8 mutation 3 "Must fail" list | FIXED. Under `_registry.Get(request.TypeName)` the foreign `Mapping.Update carrying the tenant column` row reaches `EnforceWriteAuthorization`'s reserved-column check and answers INVALID_ARGUMENT, not NOT_FOUND, so it also fails. Added to the list. |

### Sibling sweep notes (no fix needed)

- **Name resolution in the new test files.** I checked every type in `OwnerPrivateTypeVisibilityTests` against the draft's `using` list:
  - `IOutboxWriter`, `IRecordStoreTransactionRunner`, `TableSchema`, `EntityAccess` and `KeyedRow` resolve through `Iverson.Sql`;
  - `IOutboxPublisher`, `AuditLog`, `RelationValidator`, `PayloadSizeValidator`, `EntityKeyAccessor`, `ISchemaRegistrationOrchestrator`, `DecayOptions` and `PopularitySignalOptions` through `Iverson.Api.Grpc`;
  - `IntelligenceTenantScope`, `ResultReranker`, `ResultDiversifier` and `VectorRankingOptions` through `Iverson.Vector`;
  - `AuthorizationConstraint`, `EngagementQuerySchema` and `AggregationDescriptor` through `Iverson.StarRocks`;
  - `ActingUserAccessor` comes from the enclosing namespace `Iverson.Api`.

  There are no ambiguous simple names: `JoinKind`, `AggregationType`, `AggregationSpec`, `PipelineStep`, `PipelineJoin`, `JoinCondition`, `SelectItem`, `SearchSort`, `JoinSpec`, `GroupByRequest` and `PipelineRequest` exist only in `Iverson.Client.Contracts`. `AggregationResult`, `RelationKind` and `RelationDescriptor` are aliased. The project-wide `Value` alias is `Iverson.Api.Tests.csproj` `<Using … Alias="Value">`. `InactiveTenantHttpEndpointTests` mirrors `TracesRelayEndpointTests` and `AdminConsoleActiveTenantPipelineTests`.
- **Constructor argument lists.** These match the primary constructors: Mapping (15 params), Persistence (11), Retrieval (6), Search (14 required; the optional `patternLimits` and `chunkRows` are omitted) and `EntityRelationResolver(registry, entities, authEvaluator)`.
- **Quoted "before" text.** A python check of every replace-target block against the `1b7968aa` files finds exactly one match per target file. The exceptions:
  - the GroupBy/Pipeline lambda text matches twice by design, hence `replace_all`;
  - the Persistence `RequireSchema` text also occurs in the Search file but once per target file;
  - the `EntityRelationResolver` line matches three times by design;
  - Task 9 §3.1 matches once in the post-Task-8 state;
  - S3 was the one miss.
- **Builder tests.** These resolve as claimed:
  - Ghost and Ledger under `VisibleRegistry` hit `ResolveJoinSources`' throw with the exact message (`StarRocksPipelineBuilder.cs:342-345`);
  - under `FullRegistry` the Ledger join validates: Left `AuthorId` is in Article, Right `Id` is Ledger's key (`ColumnsFor` `:63-66`), and `select All` with an empty source copies input only;
  - the step-name collision text is at `:152`, and `BaseStepName = "base"` (`:21`).
- **Task 9 tests.**
  - `UserArticle` with `FieldPermission("ArticleId", ["editor"], [])` yields Read `AllowedFields = {Id, UserId}` for `test-bypass`. It survives pass one on its key column, so the catalog drops only the OneToMany edge.
  - The Get-at-depth-1 test's Article row has no `AuthorId`, so the ManyToOne resolver returns before fetching (`EntityRelationResolver.cs:150-151`), and `DidNotReceive().FetchByColumnAsync` isolates the OneToMany skip.

## Tasks

### Task 0: Branch

- [ ] **Step 1: Create the worktree and the scratch environment.**

```bash
cd /home/ben/repositories/Iverson
git worktree add -b csr11-server-authz .worktrees/csr11-server-authz main
mkdir -p /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd
cat > /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh <<'X'
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd
WT=/home/ben/repositories/Iverson/.worktrees/csr11-server-authz
LIVE=$SCR/live
X
git -C .worktrees/csr11-server-authz log --oneline -1
```

Expected: the worktree is on `csr11-server-authz`, and HEAD equals `main` (`1b7968aa` or later).

### Task 1: Pipeline joins to registered types become filtered derived tables

**Spec:** §1, §6 (closes #1)

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs:638-676`. This is `EmitStep`'s join loop. A fresh registered join source whose constraint carries an owner column or a tenant column is now emitted as `{KIND} JOIN (SELECT * FROM <qualified table> WHERE <owner> AND <tenant>) AS `<Src>` ON <conds>`. The predicates are unqualified and keep the parameter names `s{step}_j{join}_owner` and `s{step}_j{join}_tenant`. Nothing is appended to `ON` any more.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`:
  - re-baseline the four join-predicate assertions at `:1184-1198`, `:1293-1308`, `:1327-1328` and `:1589`;
  - add new tests after `Build_JoinedTypeWildcard_LastColsMatchesTheEmittedProjection`, which ends at `:1605`.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`. Append one theory as the last member of the class, after `BuildGroupBy_DefaultResolvedLimit_MatchesMaxGroupByLimitDefault_DoesNotThrow`.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/PipelineIntegrationTests.cs`. Add one live theory, RIGHT and FULL, against Testcontainers StarRocks.

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: the new `EmitStep` join-loop text. Task 2 edits other parts of `StarRocksPipelineBuilder.cs` (`EmitStep`'s signature, its derive and metric lines, and `EmitMetric`), and its quotes match the file as Task 1 leaves it.
- Line numbers in this task are as of `1b7968aa`. Task 1 adds new tests only after `:1605` of `StarRocksPipelineBuilderTests.cs` and at the end of `StarRocksQueryBuilderTests.cs`, so the line numbers Task 2 cites before those points stay valid.

- [ ] **Step 1: Write the failing tests**

**1a. Re-baseline the existing pipeline join-predicate tests** (`StarRocksPipelineBuilderTests.cs`).

In `Build_JoinedTypeOwnership_AppendsOwnerConditionToJoinOn` (`:1184-1198`), make two changes.

Rename the test:
```csharp
    public void Build_JoinedTypeOwnership_AppendsOwnerConditionToJoinOn()
```
becomes
```csharp
    public void Build_JoinedTypeOwnership_FiltersTheJoinedTableInADerivedTable()
```
Replace the `ON` assertion:
```csharp
        n.Should().Contain("ON `base`.`AuthorId` = `Author`.`Id` AND `Author`.`OwnerId` = @s1_j0_owner");
```
becomes
```csharp
        n.Should().Contain("INNER JOIN (SELECT * FROM `authors` WHERE `OwnerId` = @s1_j0_owner) AS `Author` ON `base`.`AuthorId` = `Author`.`Id`)");
```

In `Build_JoinedTypeTenant_AppendsTenantConditionToJoinOn` (`:1293-1308`), make two changes.

Rename the test:
```csharp
    public void Build_JoinedTypeTenant_AppendsTenantConditionToJoinOn()
```
becomes
```csharp
    public void Build_JoinedTypeTenant_FiltersTheJoinedTableInADerivedTable()
```
Replace the `ON` assertion:
```csharp
        n.Should().Contain("ON `base`.`AuthorId` = `Author`.`Id` AND `Author`.`TenantId` = @s1_j0_tenant");
```
becomes
```csharp
        n.Should().Contain("INNER JOIN (SELECT * FROM `authors` WHERE `TenantId` = @s1_j0_tenant) AS `Author` ON `base`.`AuthorId` = `Author`.`Id`)");
```

In `Build_TwoJoinsInSameStep_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames` (`:1327-1328`), replace two lines:
```csharp
        n.Should().Contain("ON `base`.`AuthorId` = `Author`.`Id` AND `Author`.`TenantId` = @s1_j0_tenant");
        n.Should().Contain("ON `base`.`AuthorId` = `Tag`.`Id` AND `Tag`.`TenantId` = @s1_j1_tenant");
```
They become:
```csharp
        n.Should().Contain("INNER JOIN (SELECT * FROM `authors` WHERE `TenantId` = @s1_j0_tenant) AS `Author` ON `base`.`AuthorId` = `Author`.`Id`");
        n.Should().Contain("INNER JOIN (SELECT * FROM `tags` WHERE `TenantId` = @s1_j1_tenant) AS `Tag` ON `base`.`AuthorId` = `Tag`.`Id`)");
```

In `Build_JoinedTypeWildcard_StillEmitsTheJoinedTenantPredicate` (`:1589`), replace:
```csharp
        sql.Should().Contain($"`Author`.`{TenantCol}` = @s1_j0_tenant");
```
with:
```csharp
        sql.Should().Contain($"INNER JOIN (SELECT * FROM `authors` WHERE `{TenantCol}` = @s1_j0_tenant) AS `Author`");
```

These tests need no change, and here is why:
- `Build_JoinAgainstEntityTable_EmitsAliasedJoin` (`:513-522`) has no authz, so it keeps the plain `INNER JOIN `authors` AS `Author`` form.
- `Build_JoinedTypeWildcard_*` and `SelectLists` still pass. The derived table's select list is `*`, which contains neither `` `Author` `` nor `.*`.
- The step-to-step join tests (`:1201-1234`, `:1335-1362`) are unchanged.

**1b. New pipeline builder tests.** Insert them in `StarRocksPipelineBuilderTests.cs` immediately after `Build_JoinedTypeWildcard_LastColsMatchesTheEmittedProjection`, which ends at `:1605`, and before the `// ── A LEGACY schema` section comment. They reuse the existing helpers `JoinedStep()` (`:478`), `ArticleSchema()` (`:10`), `RegistryWithAuthor()` (`:25`), `BuildRegistry(...)` (`:14`), `Request(...)` (`:34`) and `NormalizeWs(...)` (`:304`).

```csharp
    // ── Joined-type ownership: a fresh registered type joins as a filtered derived table ──
    //
    // RIGHT and FULL joins keep a joined-side row whose ON condition fails, NULL-extended, so an
    // owner or tenant predicate placed in ON cannot keep another owner's or tenant's joined rows
    // out of the result. A fresh registered type is therefore joined as a derived table that
    // applies both predicates in its own WHERE, for every join kind.

    private static Dictionary<string, AuthorizationConstraint> OwnerAndTenantAuthzFor(string typeName) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [typeName] = new AuthorizationConstraint(null, "OwnerId", "alice", "TenantId", "tenant-1")
        };

    private static PipelineStep JoinedStepOfKind(JoinKind kind)
    {
        var step = JoinedStep();
        step.Joins[0].Kind = kind;
        return step;
    }

    [Theory]
    [InlineData(JoinKind.Inner, "INNER")]
    [InlineData(JoinKind.Left, "LEFT")]
    [InlineData(JoinKind.Right, "RIGHT")]
    [InlineData(JoinKind.Full, "FULL")]
    public void Build_JoinToOwnerAndTenantScopedType_JoinsAFilteredDerivedTable(JoinKind kind, string keyword)
    {
        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(JoinedStepOfKind(kind)), RegistryWithAuthor(),
            OwnerAndTenantAuthzFor("Author"), tenantDatabase: "tenant_db");

        NormalizeWs(sql).Should().Be(
            "WITH `base` AS (SELECT `Id`, `Title`, `Category`, `WordCount`, `IsPublished`, `PublishedAt`, `AuthorId` " +
            "FROM `tenant_db`.`articles`), " +
            "`named` AS (SELECT `base`.*, `Author`.`Name` AS `author_name` FROM `base` " +
            $"{keyword} JOIN (SELECT * FROM `tenant_db`.`authors` WHERE `OwnerId` = @s1_j0_owner AND `TenantId` = @s1_j0_tenant) AS `Author` " +
            "ON `base`.`AuthorId` = `Author`.`Id`) " +
            "SELECT * FROM `named` LIMIT 10000");
        param.Get<string>("s1_j0_owner").Should().Be("alice");
        param.Get<string>("s1_j0_tenant").Should().Be("tenant-1");
    }

    [Fact]
    public void Build_JoinToTheQueriedTypeItself_FiltersBothTheBaseAndTheJoinedCopy()
    {
        // The queried type is also the join source. The base CTE carries the primary predicates;
        // the joined copy is a fresh registered type and gets its own derived table.
        var step = new PipelineStep { Name = "peers" };
        var join = new PipelineJoin { Source = "Article", Kind = JoinKind.Right };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { Column = "Id" });
        step.Select.Add(new SelectItem { Source = "Article", Column = "Id", Alias = "peer_id" });

        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(step), BuildRegistry(ArticleSchema()),
            OwnerAndTenantAuthzFor("Article"), tenantDatabase: "tenant_db");

        NormalizeWs(sql).Should().Be(
            "WITH `base` AS (SELECT `Id`, `Title`, `Category`, `WordCount`, `IsPublished`, `PublishedAt`, `AuthorId` " +
            "FROM `tenant_db`.`articles` WHERE (`OwnerId` = @__ownerVal) AND `TenantId` = @__tenantVal), " +
            "`peers` AS (SELECT `base`.`Id`, `Article`.`Id` AS `peer_id` FROM `base` " +
            "RIGHT JOIN (SELECT * FROM `tenant_db`.`articles` WHERE `OwnerId` = @s1_j0_owner AND `TenantId` = @s1_j0_tenant) AS `Article` " +
            "ON `base`.`AuthorId` = `Article`.`AuthorId`) " +
            "SELECT * FROM `peers` LIMIT 10000");
        param.Get<string>("__ownerVal").Should().Be("alice");
        param.Get<string>("__tenantVal").Should().Be("tenant-1");
        param.Get<string>("s1_j0_owner").Should().Be("alice");
        param.Get<string>("s1_j0_tenant").Should().Be("tenant-1");
    }

    [Fact]
    public void Build_StepReadingAnOuterJoinedStep_AddsNoPredicateOfItsOwn()
    {
        // The derived table filters the joined rows once, inside the joining step. A later step
        // reads that step's CTE, which is already filtered, so it adds nothing.
        var kept = new PipelineStep { Name = "kept" };
        kept.Where.Add(new SearchClause
        {
            Property = "author_name", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "Ann" }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(JoinedStepOfKind(JoinKind.Full), kept), RegistryWithAuthor(),
            OwnerAndTenantAuthzFor("Author"), tenantDatabase: "tenant_db");

        NormalizeWs(sql).Should().Be(
            "WITH `base` AS (SELECT `Id`, `Title`, `Category`, `WordCount`, `IsPublished`, `PublishedAt`, `AuthorId` " +
            "FROM `tenant_db`.`articles`), " +
            "`named` AS (SELECT `base`.*, `Author`.`Name` AS `author_name` FROM `base` " +
            "FULL JOIN (SELECT * FROM `tenant_db`.`authors` WHERE `OwnerId` = @s1_j0_owner AND `TenantId` = @s1_j0_tenant) AS `Author` " +
            "ON `base`.`AuthorId` = `Author`.`Id`), " +
            "`kept` AS (SELECT * FROM `named` WHERE `author_name` = @s2_p0) " +
            "SELECT * FROM `kept` LIMIT 10000");
        param.Get<string>("s2_p0").Should().Be("Ann");
    }
```

**1c. Query-builder pin for RIGHT and FULL in Search.** Append this as the last member of `StarRocksQueryBuilderTests`, after `BuildGroupBy_DefaultResolvedLimit_MatchesMaxGroupByLimitDefault_DoesNotThrow` and before the class's closing `}`. It reuses `AuthorSchema()` (`:12`), `ArticleSchema()` (`:15`) and `BuildRegistry(...)` (`:1673`). `using Dapper;` is already present.

```csharp
    // ── BuildSearch — outer joins keep the primary type's boundary in the outer WHERE ────
    // A RIGHT or FULL join emits joined-side rows with no primary-side match, NULL-extended. The
    // primary type's owner and tenant predicates sit in the outer WHERE, where a NULL primary
    // column fails them, so those rows never come back. The joined type's predicates stay in ON.

    [Theory]
    [InlineData(JoinKind.Right, "RIGHT")]
    [InlineData(JoinKind.Full, "FULL")]
    public void BuildSearch_OuterJoin_KeepsPrimaryOwnerAndTenantPredicatesInOuterWhere(JoinKind kind, string keyword)
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = kind }
        };
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"]  = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1", TenantColumn: "TenantId", TenantValue: "tenant-1"),
            ["Article"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1", TenantColumn: "TenantId", TenantValue: "tenant-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            "authors", AuthorSchema(), null, 0, 10, joins: joins, registry: registry, authz: authz);

        sql.Should().Be(
            "SELECT `authors`.`Id`, `authors`.`Name`, `authors`.`Bio`, `authors`.`Rating`, `authors`.`PublishedAt` " +
            $"FROM `authors` {keyword} JOIN `articles` ON `authors`.`Id` = `articles`.`Id` " +
            "AND `articles`.`OwnerId` = @__owner1 AND `articles`.`TenantId` = @__tenant1 " +
            "WHERE (`authors`.`OwnerId` = @__ownerVal) AND `authors`.`TenantId` = @__tenantVal " +
            "LIMIT 10 OFFSET 0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("user-1");
        lookup["__tenantVal"].Should().Be("tenant-1");
    }
```

**1d. Live integration test.** Add it to `PipelineIntegrationTests.cs` after `JoinCteAgainstBase_EnrichesRowsWithAggregates` and before the class's closing `}`. It reuses `_fx` (the `StarRocksContainerFixture` from the `StarRocksCollection`), `_fx.Repository.ExecuteAsync` and `_fx.Repository.QueryAsync<dynamic>` (`EngagementRepository.cs:110,136`), and `TestSchemaRegistry.BuildRegistry` (`TestSchemaRegistry.cs`).

```csharp
    private static EngagementQuerySchema OwnedArticleSchema() =>
        new("PipeOwnedArticle", "pipe_owned_articles", "Id", ["AuthorId"]);

    private static EngagementQuerySchema OwnedAuthorSchema() =>
        new("PipeOwnedAuthor", "pipe_owned_authors", "Id", ["Name"]);

    private async Task SeedOwnedAsync()
    {
        // Both tables carry OwnerId and TenantId columns that the schemas above leave out of their
        // column lists: the builder takes the owner and tenant predicates from the
        // AuthorizationConstraint, not from the schema. Article 1 and authors a1 and a4 belong to
        // the caller (alice, t1). a2 belongs to another owner, and a3 to another tenant.
        await _fx.Repository.ExecuteAsync("DROP TABLE IF EXISTS pipe_owned_articles");
        await _fx.Repository.ExecuteAsync("DROP TABLE IF EXISTS pipe_owned_authors");
        await _fx.Repository.ExecuteAsync("""
            CREATE TABLE pipe_owned_articles (
                Id VARCHAR(64) NOT NULL, AuthorId VARCHAR(64), OwnerId VARCHAR(64), TenantId VARCHAR(64)
            ) ENGINE=OLAP
            PRIMARY KEY (Id) DISTRIBUTED BY HASH(Id) BUCKETS 4
            PROPERTIES ("replication_num" = "1")
            """);
        await _fx.Repository.ExecuteAsync("""
            CREATE TABLE pipe_owned_authors (
                Id VARCHAR(64) NOT NULL, Name VARCHAR(64), OwnerId VARCHAR(64), TenantId VARCHAR(64)
            ) ENGINE=OLAP
            PRIMARY KEY (Id) DISTRIBUTED BY HASH(Id) BUCKETS 4
            PROPERTIES ("replication_num" = "1")
            """);
        await _fx.Repository.ExecuteAsync("""
            INSERT INTO pipe_owned_articles VALUES
            ('1','a1','alice','t1'), ('2','a2','bob','t1'), ('3','a3','alice','t2')
            """);
        await _fx.Repository.ExecuteAsync("""
            INSERT INTO pipe_owned_authors VALUES
            ('a1','Ann','alice','t1'), ('a2','Bob','bob','t1'), ('a3','Cat','alice','t2'), ('a4','Dee','alice','t1')
            """);
    }

    [Theory]
    [InlineData(JoinKind.Right)]
    [InlineData(JoinKind.Full)]
    public async Task OuterJoinToRegisteredType_ReturnsOnlyTheCallersJoinedRows(JoinKind kind)
    {
        await SeedOwnedAsync();

        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = "PipeOwnedAuthor", Kind = kind };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { Column = "Id", Alias = "article_id" });
        step.Select.Add(new SelectItem { Source = "PipeOwnedAuthor", Column = "Id", Alias = "author_id" });

        var request = new PipelineRequest { TypeName = "PipeOwnedArticle" };
        request.Steps.Add(step);

        var authz = new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            ["PipeOwnedArticle"] = new(null, "OwnerId", "alice", "TenantId", "t1"),
            ["PipeOwnedAuthor"]  = new(null, "OwnerId", "alice", "TenantId", "t1")
        };

        // Built and run directly, not through PipelineAsync. PipelineAsync would route the query to
        // a per-tenant database under a tenant role. This fixture connects as root, so the
        // generated predicates are the only thing separating the caller's rows from the others.
        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            OwnedArticleSchema(), request,
            TestSchemaRegistry.BuildRegistry(OwnedArticleSchema(), OwnedAuthorSchema()), authz);
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        // a1 matches the caller's article. a4 is the caller's own author with no article, and an
        // outer join still returns it. a2 (another owner) and a3 (another tenant) must not appear.
        ((IEnumerable<dynamic>)rows).Select(r => (string)r.author_id).Should().BeEquivalentTo(["a1", "a4"]);
    }
```

- [ ] **Step 2: Run them and see them fail**

Unit tests (no containers):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests|FullyQualifiedName~StarRocksQueryBuilderTests"
```
Expected: 10 failures, all in `StarRocksPipelineBuilderTests`. In each one, the emitted SQL has the joined table's predicates in `ON` instead of a derived table:
- `Build_JoinToOwnerAndTenantScopedType_JoinsAFilteredDerivedTable` (4 cases);
- `Build_JoinToTheQueriedTypeItself_FiltersBothTheBaseAndTheJoinedCopy`;
- `Build_StepReadingAnOuterJoinedStep_AddsNoPredicateOfItsOwn`;
- `Build_JoinedTypeOwnership_FiltersTheJoinedTableInADerivedTable`;
- `Build_JoinedTypeTenant_FiltersTheJoinedTableInADerivedTable`;
- `Build_TwoJoinsInSameStep_EachJoinedTypeTenantConstraint_UsesDistinctParameterNames`;
- `Build_JoinedTypeWildcard_StillEmitsTheJoinedTenantPredicate`.

`BuildSearch_OuterJoin_KeepsPrimaryOwnerAndTenantPredicatesInOuterWhere` (2 cases) **passes**. It pins existing, unchanged behaviour (spec §1: the query builder is unchanged). Record that it passed.

Live test (**heavy: starts a StarRocks Testcontainers container. Nothing else may run at the same time.**):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~PipelineIntegrationTests.OuterJoinToRegisteredType"
```
Expected: both cases (Right, Full) fail. `author_id` is `{a1, a2, a3, a4}`: with the predicates in `ON`, the other owner's author (a2) and the other tenant's author (a3) come back NULL-extended. This is the leak the task closes. Record the actual sets in the report.

- [ ] **Step 3: Implement**

In `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`, `EmitStep`'s join loop, replace lines 638-676. The block runs from the comment `// Entity sources join the physical table aliased as the type name;` through `joinIdx++;`:

```csharp
            // Entity sources join the physical table aliased as the type name;
            // step sources are CTEs joined by their own name (no alias needed).
            var joinedSchema = registry(src.Name);
            var isFreshType = joinedSchema is not null && emitted.All(s => !s.Name.Equals(src.Name, StringComparison.OrdinalIgnoreCase));
            var target = isFreshType
                ? $"{TenantIdentifier.Qualify(tenantDatabase, joinedSchema!.TableName)} AS `{src.Name}`"
                : $"`{src.Name}`";
            var conds = join.On.Select(c =>
                $"`{input.Name}`.`{input.Columns[c.Left]}` = `{src.Name}`.`{src.Columns[c.Right]}`");

            // Joined-type ownership: appended to this JOIN's own ON clause (never the outer
            // WHERE) — same reasoning as BuildFromWithJoins in StarRocksQueryBuilder (a WHERE-
            // clause condition on the joined side would collapse LEFT/RIGHT/FULL to INNER
            // semantics). Only applies to a fresh registered type, never a prior step — a
            // step-to-step join needs no new predicate; that source was already filtered
            // upstream (same "already filtered upstream" reasoning already established for
            // Pipeline steps' own `where`).
            var ownerCond = "";
            if (isFreshType && authz is not null && authz.TryGetValue(src.Name, out var joinedConstraint) && joinedConstraint.OwnerColumn is not null)
            {
                var pName = $"s{stepIdx}_j{joinIdx}_owner";
                param.Add(pName, joinedConstraint.OwnerValue);
                ownerCond = $" AND `{src.Name}`.`{joinedConstraint.OwnerColumn}` = @{pName}";
            }

            // Tenant boundary is additive and unconditional — gated only on TenantColumn being
            // present, independently of the ownership block above. Every joined type is now
            // guaranteed to carry a tenant column; the step/join-indexed parameter name is
            // already unique per join, mirroring the owner predicate's discipline.
            var tenantCond = "";
            if (isFreshType && authz is not null && authz.TryGetValue(src.Name, out var joinedTenantConstraint) && joinedTenantConstraint.TenantColumn is not null)
            {
                var tName = $"s{stepIdx}_j{joinIdx}_tenant";
                param.Add(tName, joinedTenantConstraint.TenantValue);
                tenantCond = $" AND `{src.Name}`.`{joinedTenantConstraint.TenantColumn}` = @{tName}";
            }

            sb.Append($" {kind} JOIN {target} ON {string.Join(" AND ", conds)}{ownerCond}{tenantCond}");
            joinIdx++;
```
with:
```csharp
            // Entity sources join the physical table aliased as the type name;
            // step sources are CTEs joined by their own name (no alias needed).
            var joinedSchema = registry(src.Name);
            var isFreshType = joinedSchema is not null && emitted.All(s => !s.Name.Equals(src.Name, StringComparison.OrdinalIgnoreCase));
            var conds = join.On.Select(c =>
                $"`{input.Name}`.`{input.Columns[c.Left]}` = `{src.Name}`.`{src.Columns[c.Right]}`");

            // Joined-type ownership and tenant boundary. A fresh registered type is joined as a
            // derived table that applies both predicates in its own WHERE, so a row of the joined
            // table that fails them never reaches the join. ON is not enough: a RIGHT or FULL join
            // emits a joined-side row whose ON condition fails, NULL-extended, and this step's
            // WHERE carries only the caller's own filters. Inside the derived table the predicates
            // are unqualified, because the outer alias is not in scope there. The derived table
            // selects *, but what the step may read from it is still ColumnsFor's tracked set, and
            // EmitSelectItem expands a fresh type's wildcard from that set. A step-to-step join
            // needs neither predicate: that source was already filtered upstream.
            var sourcePredicates = new List<string>();
            if (isFreshType && authz is not null && authz.TryGetValue(src.Name, out var joinedConstraint))
            {
                if (joinedConstraint.OwnerColumn is not null)
                {
                    var pName = $"s{stepIdx}_j{joinIdx}_owner";
                    param.Add(pName, joinedConstraint.OwnerValue);
                    sourcePredicates.Add($"`{joinedConstraint.OwnerColumn}` = @{pName}");
                }

                // Tenant boundary is additive and unconditional — gated only on TenantColumn being
                // present, independently of the ownership predicate above. The step/join-indexed
                // parameter names are unique per join.
                if (joinedConstraint.TenantColumn is not null)
                {
                    var tName = $"s{stepIdx}_j{joinIdx}_tenant";
                    param.Add(tName, joinedConstraint.TenantValue);
                    sourcePredicates.Add($"`{joinedConstraint.TenantColumn}` = @{tName}");
                }
            }

            var target = !isFreshType
                ? $"`{src.Name}`"
                : sourcePredicates.Count > 0
                    ? $"(SELECT * FROM {TenantIdentifier.Qualify(tenantDatabase, joinedSchema!.TableName)} " +
                      $"WHERE {string.Join(" AND ", sourcePredicates)}) AS `{src.Name}`"
                    : $"{TenantIdentifier.Qualify(tenantDatabase, joinedSchema!.TableName)} AS `{src.Name}`";

            sb.Append($" {kind} JOIN {target} ON {string.Join(" AND ", conds)}");
            joinIdx++;
```

Nothing else in the file changes for this task. `StarRocksQueryBuilder.cs` does not change (spec §1).

- [ ] **Step 4: Run the tests and see them pass**

Same unit command as Step 2. Expected: 0 failures in `StarRocksPipelineBuilderTests` and `StarRocksQueryBuilderTests`. Then run the whole non-container suite of the project:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures.

Live test (**heavy, alone**): same live command as Step 2. Expected: 2 passed. For both Right and Full, `author_id` is exactly `{a1, a4}`.

Then, still alone and heavy, run the rest of the class to show the existing pipeline integration tests are unaffected:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~PipelineIntegrationTests"
```
Expected: 7 passed, the 5 existing tests plus 2 new cases.

- [ ] **Step 5: Mutation proof** (unit suite only; revert each mutation before the next)

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| M1 | Derived-table filter | In the `target` expression, change `: sourcePredicates.Count > 0` to `: sourcePredicates.Count < 0` | `Build_JoinToOwnerAndTenantScopedType_JoinsAFilteredDerivedTable` (4 cases), `Build_JoinToTheQueriedTypeItself_…`, `Build_StepReadingAnOuterJoinedStep_…`, `Build_JoinedTypeOwnership_FiltersTheJoinedTableInADerivedTable`, `Build_JoinedTypeTenant_…`, `Build_TwoJoinsInSameStep_…`, `Build_JoinedTypeWildcard_StillEmitsTheJoinedTenantPredicate` |
| M2 | Tenant predicate in the derived table | Delete the line ``sourcePredicates.Add($"`{joinedConstraint.TenantColumn}` = @{tName}");`` | `Build_JoinToOwnerAndTenantScopedType_…` (4 cases), `Build_JoinedTypeTenant_…`, `Build_TwoJoinsInSameStep_…` |
| M3 | Owner predicate in the derived table | Delete the line ``sourcePredicates.Add($"`{joinedConstraint.OwnerColumn}` = @{pName}");`` | `Build_JoinToOwnerAndTenantScopedType_…` (4 cases), `Build_JoinedTypeOwnership_…` |

Run the unit command from Step 2 for each mutation, record the failing test names, and revert with `git -C /home/ben/repositories/Iverson/.worktrees/csr11-server-authz checkout -- Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`, then re-apply Step 3. A safer alternative is to make each mutation by hand and undo it by hand. Do not re-run the live test under mutation. Step 2's red run on unmodified code is the live counterfactual.

- [ ] **Step 6: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git add Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs Iverson.Server/Iverson.StarRocks.Tests/PipelineIntegrationTests.cs
git commit -m "filter pipeline joins to registered types in a derived table

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Client expressions are parsed and re-emitted, not spliced

**Spec:** §2, §6 (closes #10, first half)

**Runs after Task 1.** Both tasks edit `StarRocksPipelineBuilder.cs`, `StarRocksPipelineBuilderTests.cs` and `StarRocksQueryBuilderTests.cs`. Every quote below is the text as Task 1 leaves it. Task 1 changed only the `EmitStep` join loop, `StarRocksPipelineBuilderTests.cs:1184-1198`, `:1293-1308`, `:1327-1328` and `:1589`, inserted tests after `:1605`, and appended at the end of `StarRocksQueryBuilderTests.cs`. So every test-file line number cited below, all before those points, is still valid at `1b7968aa`. In `StarRocksPipelineBuilder.cs`, Task 1 replaced the 39-line join block `:638-676` with 46 lines, so every line after it moves down by 7: `EmitMetric`, cited below at its `1b7968aa` lines `:682` and `:699-707`, sits at `:689` and `:706-714` when this task runs. Every other `StarRocksPipelineBuilder.cs` line cited below is before `:638` and unchanged. Locate every edit by its quoted text, not by its line number.

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/ClientExpression.cs`. The `ExpressionContext` enum and the `ClientExpression.ToSql` entry point, which runs the length cap, then the lexer, the parser and the emitter.
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionLexer.cs`. The token kinds, the token record and the lexer.
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionParser.cs`. The AST records and the recursive-descent parser: contexts, reserved words, the function allow-list and the depth cap.
- Create: `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionEmitter.cs`. Writes SQL from the AST.
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`:
  - `StepColumns` (`:8-12`) gains `DeriveSql`;
  - delete `TokenRx`, `DeriveFunctions`, `DeriveKeywords` and `IsNonColumnToken` (`:24-53`);
  - `TrackAndValidate` (`:124-130`);
  - `ValidateStepAndComputeOutput`'s return type and returns (`:165`, `:255`, `:313-325`);
  - metric validation (`:239-249`);
  - delete `RejectForbiddenCharacters` and `ValidateDeriveExpr` (`:385-422`), replacing them with `MetricExpressionSql` and `DeriveExpressionSql`;
  - `Build`'s `EmitStep` call (`:512-514`), and `EmitStep`'s signature, metric line and derive line (`:537-547`, `:575`, `:622-623`);
  - `EmitMetric` (`:682-708` at `1b7968aa`; `:689-715` after Task 1).
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs`:
  - usings (`:1-5`);
  - `BuildAggregate`'s expression validation and its five emissions (`:248-256`, `:333-345`);
  - `BuildMetricExpr`'s header comment, validation and emission (`:472-478`, `:526-542`);
  - range bounds (`:998-1004`), plus a new `RangeBound` helper.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs` and `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`: the re-baselines in Step 1.
- Test: `Iverson.Server/Iverson.StarRocks.Tests/ClientExpressionTests.cs` (new). The accepted and rejected corpora, emitter canonical form, each site's canonical SQL and pinned messages, and range bounds.

**Interfaces:**
- Consumes: Task 1's `EmitStep` join loop, which this task does not touch.
- Produces:
  - `internal static string ClientExpression.ToSql(string text, ExpressionContext context, Func<string, string> resolveQuoted, string errorContext)` in namespace `Iverson.StarRocks.Expressions`;
  - `internal enum ExpressionContext { AggregateArgument, Derive }`;
  - `StepColumns.DeriveSql` (`IReadOnlyList<string>`, an init-only property, defaulting to empty).

  No other task consumes them. The `StarRocksPipelineBuilder.TrackAndValidate` signature is unchanged, which matters to any later task that calls it.

**Design decisions this task fixes (spec §2 leaves them open; recorded for the reviewer):**
- **An aggregate's own argument is parsed in the aggregate-argument context**, even inside a derive expression. So `SUM(SUM(n) OVER ()) OVER ()` is refused, which is also invalid SQL. Scalar-function arguments keep the enclosing context, so `ROUND(AVG(x) OVER (), 2)` is a valid derive.
- **`--` and `/*` are refused by the lexer** even though each of their characters is an operator. `- -a`, with a space, is unary minus twice. The emitter writes a negated non-primary as `-(…)`, so it can never produce `--`.
- **Numbers are parsed as C# `decimal`.** `100.0` re-prints as `100.0`, because decimal keeps its scale, and `007` as `7`. A value `decimal.TryParse` rejects is refused. There are no exponents, no leading `.`, and no trailing `.`.
- **The emitter parenthesizes by precedence.** A child is wrapped when its precedence is lower than its position needs, and an equal-precedence right operand is wrapped as well. The output therefore parses back to the same tree. Unary `+` is not in the grammar.
- **Derive SQL is compiled once, in `TrackAndValidate`, against the scope it was validated in, and carried to `EmitStep` on `StepColumns.DeriveSql`.** That scope is input columns, plus this step's select outputs, window aliases and earlier derive aliases. Rebuilding it at emit time would duplicate the scope logic. Metric SQL is compiled again in `EmitMetric`, because its callback (`input.Columns`) is the same in both passes.
- **Message shapes.** Every message is prefixed with the site's existing context string:
  - lexer refusals end `… is not allowed in an expression.`, as in `character ';' at position 6` and `comment sequence '--' at position 7`. Non-printable characters and non-ASCII appear as `U+XXXX`;
  - parser failures say `unexpected '<token>' at position N`, `unexpected end of expression at position N`, `unknown function '<name>' at position N`, `aggregate function 'SUM' at position N is not allowed here; only COALESCE, NULLIF, ROUND and ABS may be called`, `aggregate function 'SUM' at position N requires an OVER clause`, or `expression nests deeper than 32 levels at position N`;
  - the length cap keeps today's text, `<context>: expression exceeds 1000 characters.`
  - Column errors keep each site's pinned text, through its callback.
- **Depth.** The root counts as level 1. Each parenthesized expression, call argument and unary minus adds a level, and level 33 is refused.

- [ ] **Step 1: Re-baseline the existing tests**

Make these edits first. They are anchored by test method name, because several theories share identical `InlineData` lines.

**`StarRocksQueryBuilderTests.cs`**

1. `BuildAggregate_ExpressionMetric_UsesRawExpressionInAggFn`, line 178:
   ```csharp
           sql.Should().Contain("SUM(Rating * 2)");
   ```
   becomes:
   ```csharp
           sql.Should().Contain("SUM(`Rating` * 2)");
   ```
   (`:179` `NotContain("SUM(`Rating`)")` stays and still holds.)

2. Section comment, lines 846-852:
   ```csharp
       // ── BuildAggregate — Expression forbidden-character denylist (CSR Finding #3) ──
       // spec.Expression previously only ran the TokenRx identifier allow-list check (see the
       // tests above), which inspects identifier-shaped substrings only — punctuation, quotes,
       // semicolons, and SQL comment sequences pass through unchecked because they never match
       // TokenRx in the first place. These tests lock in the stricter denylist (shared with
       // StarRocksPipelineBuilder.ValidateDeriveExpr via RejectForbiddenCharacters) that closes
       // that gap.
   ```
   becomes:
   ```csharp
       // ── BuildAggregate — Expression characters outside the grammar (CSR Finding #3) ──
       // spec.Expression is lexed by ClientExpression, which refuses every character outside its
       // grammar (quotes, backslash, ';', '#') and the comment openers "--" and "/*" before any
       // column is resolved.
   ```

3. The "forbidden character" assertion appears in exactly six tests in this file:
   - `BuildAggregate_ExpressionWithForbiddenSequence_ThrowsTranslationException` (`:867`);
   - `BuildAggregate_ExpressionCommentAttemptsToStripOwnershipPredicate_ThrowsInsteadOfProducingUnsafeSql` (`:889`);
   - `BuildAggregate_ExpressionWithDoubleQuoteOrBackslash_ThrowsTranslationException` (`:958`);
   - `BuildGroupBy_MetricExpressionWithSqlComment_ThrowsTranslationException` (`:2281`);
   - `BuildGroupBy_MetricExpressionCommentAttemptsToStripOwnershipPredicate_ThrowsInsteadOfProducingUnsafeSql` (`:2308`);
   - `BuildGroupBy_MetricExpressionWithDoubleQuoteOrBackslash_ThrowsTranslationException` (`:2906`).

   In each one:
   ```csharp
               .Where(e => e.Message.Contains("forbidden character"));
   ```
   becomes:
   ```csharp
               .Where(e => e.Message.Contains("is not allowed in an expression"));
   ```
   A replace-all of that exact line in this file is correct. Verify afterwards with `command grep -c 'forbidden character' Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`, which should print `0`. Every input in these tests fails in the lexer, which finishes before parsing begins: `--`, `;`, `/*`, `#`, `"` and `\`. So all of them get the shared phrase.

4. `BuildAggregate_NormalExpression_StillProducesExpectedSql`, line 903:
   ```csharp
           sql.Should().Contain("SUM(WordCount * 2) AS metric_val");
   ```
   becomes:
   ```csharp
           sql.Should().Contain("SUM(`WordCount` * 2) AS metric_val");
   ```

5. `BuildAggregate_ExpressionCallsFunction_DoesNotThrow`, lines 932-938. These 6 of the 18 cases are re-pointed to scalar functions:
   ```csharp
       [InlineData("SUM(Amount)")]
       [InlineData("SUM (Amount)")]
       [InlineData("SUM\t(Amount)")]
       [InlineData("SUM\n(Amount)")]
       [InlineData("sum(Amount)")]
       [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
       public void BuildAggregate_ExpressionCallsFunction_DoesNotThrow(string expr)
   ```
   becomes:
   ```csharp
       [InlineData("ABS(Amount)")]
       [InlineData("ABS (Amount)")]
       [InlineData("ABS\t(Amount)")]
       [InlineData("ABS\n(Amount)")]
       [InlineData("abs(Amount)")]
       [InlineData("ROUND(NULLIF(Amount, 0), 2)")]
       public void BuildAggregate_ExpressionCallsFunction_DoesNotThrow(string expr)
   ```

6. `Q1StyleRequest()` comment, lines 2168-2171:
   ```csharp
           // COALESCE is a DeriveFunctions-recognized function (unlike LENGTH, which the new
           // authz-driven expression-token check below would now reject as an unresolvable,
           // non-whitelisted identifier) — swapped in here so this pre-existing structural test
           // keeps passing under Task 4's stricter Expression validation.
   ```
   becomes:
   ```csharp
           // COALESCE is on the expression parser's scalar allow-list (LENGTH is not, and is
           // rejected as an unknown function).
   ```

7. `BuildGroupBy_SingleTable_ProducesCompoundSelect`, lines 2198-2200:
   ```csharp
           sql.Should().Contain("SUM(Rating * (1 - 0)) AS `net_rating`");
           sql.Should().Contain("SUM(Rating * (1 - 0) * (1 + 0)) AS `charge`");
           sql.Should().Contain("AVG(COALESCE(Rating, 0)) AS `avg_name_len`");
   ```
   becomes:
   ```csharp
           sql.Should().Contain("SUM(`authors`.`Rating` * (1 - 0)) AS `net_rating`");
           sql.Should().Contain("SUM(`authors`.`Rating` * (1 - 0) * (1 + 0)) AS `charge`");
           sql.Should().Contain("AVG(COALESCE(`authors`.`Rating`, 0)) AS `avg_name_len`");
   ```

8. `BuildGroupBy_ExpressionMetric_PassesRawExpression`, line 2257:
   ```csharp
           sql.Should().Contain("SUM(Rating * 2) AS `revenue`");
   ```
   becomes:
   ```csharp
           sql.Should().Contain("SUM(`authors`.`Rating` * 2) AS `revenue`");
   ```

9. Section comment, lines 2261-2264:
   ```csharp
       // ── BuildGroupBy — MetricSpec.expression forbidden-character denylist (CSR Finding #3) ──
       // Same gap as BuildAggregate's spec.Expression above: metric.Expression previously only
       // ran the TokenRx identifier allow-list, which never inspects punctuation/quotes/semicolons/
       // comment sequences because they don't match the identifier pattern to begin with.
   ```
   becomes:
   ```csharp
       // ── BuildGroupBy — MetricSpec.expression characters outside the grammar (CSR Finding #3) ──
       // Same lexer as BuildAggregate's spec.Expression above.
   ```

10. `BuildGroupBy_MetricExpressionCallsFunction_DoesNotThrow`, lines 2883-2889:
    ```csharp
        [InlineData("SUM(Amount)")]
        [InlineData("SUM (Amount)")]
        [InlineData("SUM\t(Amount)")]
        [InlineData("SUM\n(Amount)")]
        [InlineData("sum(Amount)")]
        [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
        public void BuildGroupBy_MetricExpressionCallsFunction_DoesNotThrow(string expr)
    ```
    becomes:
    ```csharp
        [InlineData("ABS(Amount)")]
        [InlineData("ABS (Amount)")]
        [InlineData("ABS\t(Amount)")]
        [InlineData("ABS\n(Amount)")]
        [InlineData("abs(Amount)")]
        [InlineData("ROUND(NULLIF(Amount, 0), 2)")]
        public void BuildGroupBy_MetricExpressionCallsFunction_DoesNotThrow(string expr)
    ```

11. `BuildAggregate_ExpressionReferencingTheTenantColumn_IsRejected`, line 3300. `COUNT` can no longer appear in an aggregation expression; it would be refused as an aggregate before the column is resolved. The test's subject is the tenant column, so it moves into a scalar call:
    ```csharp
                "smuggled", AggregationKind.Count, "Name", Expression: $"COUNT({TenantCol})");
    ```
    becomes:
    ```csharp
                "smuggled", AggregationKind.Count, "Name", Expression: $"COALESCE({TenantCol}, 0)");
    ```
    Its assertion, `Contains("Unknown aggregation field")`, is unchanged.

**`StarRocksPipelineBuilderTests.cs`**

12. `Build_RunningSumAndDerive_EmitOverAndExpression`, line 425:
    ```csharp
            n.Should().Contain("(100.0 * n / SUM(n) OVER ()) AS `pct`");
    ```
    becomes:
    ```csharp
            n.Should().Contain("(100.0 * `n` / SUM(`n`) OVER ()) AS `pct`");
    ```

13. `Build_ReadsSkipsIntermediateStep`, line 439:
    ```csharp
            NormalizeWs(sql).Should().Contain("`b` AS (SELECT *, (WordCount + 2) AS `y` FROM `base`)");
    ```
    becomes:
    ```csharp
            NormalizeWs(sql).Should().Contain("`b` AS (SELECT *, (`WordCount` + 2) AS `y` FROM `base`)");
    ```

14. `Build_DeriveExprWithSqlCommentSequence_Throws`, lines 717-718. **Do this before item 16.**
    ```csharp
                .Where(e => e.Message.Contains("forbidden character")
                         && e.Message.Contains("SQL comment sequences"));
    ```
    becomes:
    ```csharp
                .Where(e => e.Message.Contains("comment sequence")
                         && e.Message.Contains("is not allowed in an expression"));
    ```

15. `Build_DeriveExprWithHashLineCommentToken_Throws` comment, line 725:
    ```csharp
            // dialect, alongside "--" and "/* */" — RejectForbiddenCharacters previously missed it.
    ```
    becomes:
    ```csharp
            // dialect, alongside "--" and "/* */"; the expression lexer refuses it like every other character outside its grammar.
    ```

16. The remaining five `"forbidden character"` occurrences in this file:
    - `Build_DeriveExprWithHashLineCommentToken_Throws` `:735`;
    - `Validate_MetricExpressionWithForbiddenSequence_Throws` `:773`;
    - `Build_MetricExpressionCommentAttemptsToCloseAggregateAndStripTrailingSql_ThrowsInsteadOfProducingUnsafeSql` `:793`;
    - `Validate_MetricExpressionWithDoubleQuoteOrBackslash_Throws` `:1030`;
    - `Validate_DeriveWithDoubleQuoteOrBackslash_Throws` `:1062`.

    In each, the string literal `"forbidden character"` becomes `"is not allowed in an expression"`. The exact old lines are:
    ```csharp
                .Where(e => e.Message.Contains("forbidden character"));
                ArticleSchema(), Request(step), EmptyRegistry()), "forbidden character");
                LedgerSchema(), LedgerRequest(MetricStep(expr)), EmptyRegistry()), "forbidden character");
                LedgerSchema(), LedgerRequest(DeriveStep(expr)), EmptyRegistry()), "forbidden character");
    ```
    The first of these occurs twice, at `:735` and `:793`. The new lines are:
    ```csharp
                .Where(e => e.Message.Contains("is not allowed in an expression"));
                ArticleSchema(), Request(step), EmptyRegistry()), "is not allowed in an expression");
                LedgerSchema(), LedgerRequest(MetricStep(expr)), EmptyRegistry()), "is not allowed in an expression");
                LedgerSchema(), LedgerRequest(DeriveStep(expr)), EmptyRegistry()), "is not allowed in an expression");
    ```
    Verify afterwards with `command grep -c 'forbidden character' Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`, which should print `0`.

17. Section comment, lines 753-760:
    ```csharp
        // ── Metric expression forbidden-character denylist (Task 9 / CSR Finding #3) ──
        // m.Expression previously only ran the TokenRx identifier allow-list check (see the
        // "Authorization — metric MetricSpec.Expression check" tests below), which inspects
        // identifier-shaped substrings only — punctuation, quotes, semicolons, and SQL comment
        // sequences pass through unchecked because they never match TokenRx in the first place.
        // These tests lock in the stricter denylist (RejectForbiddenCharacters, shared with
        // ValidateDeriveExpr and StarRocksQueryBuilder's BuildAggregate/BuildMetricExpr) applied
        // to this third call site.
    ```
    becomes:
    ```csharp
        // ── Metric expression characters outside the grammar (Task 9 / CSR Finding #3) ──
        // m.Expression goes through the same expression lexer as derive expressions and
        // StarRocksQueryBuilder's BuildAggregate/BuildMetricExpr, which refuses every character
        // outside its grammar and the comment openers "--" and "/*".
    ```

18. `Build_NormalMetricExpression_StillProducesExpectedSql`, line 807:
    ```csharp
            NormalizeWs(sql).Should().Contain("SUM(WordCount * 2) AS `wc2`");
    ```
    becomes:
    ```csharp
            NormalizeWs(sql).Should().Contain("SUM(`WordCount` * 2) AS `wc2`");
    ```

19. `Validate_MetricExpressionCallsFunction_DoesNotThrow`, lines 1011-1017. These are the last 6 of the 18 cases:
    ```csharp
        [InlineData("SUM(Amount)")]
        [InlineData("SUM (Amount)")]
        [InlineData("SUM\t(Amount)")]
        [InlineData("SUM\n(Amount)")]
        [InlineData("sum(Amount)")]
        [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
        public void Validate_MetricExpressionCallsFunction_DoesNotThrow(string expr)
    ```
    becomes:
    ```csharp
        [InlineData("ABS(Amount)")]
        [InlineData("ABS (Amount)")]
        [InlineData("ABS\t(Amount)")]
        [InlineData("ABS\n(Amount)")]
        [InlineData("abs(Amount)")]
        [InlineData("ROUND(NULLIF(Amount, 0), 2)")]
        public void Validate_MetricExpressionCallsFunction_DoesNotThrow(string expr)
    ```

20. `Validate_DeriveCallsFunction_DoesNotThrow`, lines 1042-1055. The five bare-aggregate cases become rejections in a new theory. The window case stays accepted. The whole old block:
    ```csharp
        [Theory]
        [InlineData("SUM(Amount)")]
        [InlineData("SUM (Amount)")]
        [InlineData("SUM\t(Amount)")]
        [InlineData("SUM\n(Amount)")]
        [InlineData("sum(Amount)")]
        [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
        public void Validate_DeriveCallsFunction_DoesNotThrow(string expr)
        {
            var act = () => StarRocksPipelineBuilder.TrackAndValidate(
                LedgerSchema(), LedgerRequest(DeriveStep(expr)), EmptyRegistry(), LedgerAuthzWithoutSum());

            act.Should().NotThrow();
        }
    ```
    becomes:
    ```csharp
        [Theory]
        [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)")]
        public void Validate_DeriveCallsFunction_DoesNotThrow(string expr)
        {
            var act = () => StarRocksPipelineBuilder.TrackAndValidate(
                LedgerSchema(), LedgerRequest(DeriveStep(expr)), EmptyRegistry(), LedgerAuthzWithoutSum());

            act.Should().NotThrow();
        }

        // A derive step has no GROUP BY, so an aggregate without OVER is invalid SQL; it is now
        // InvalidArgument instead of an engine error.
        [Theory]
        [InlineData("SUM(Amount)")]
        [InlineData("SUM (Amount)")]
        [InlineData("SUM\t(Amount)")]
        [InlineData("SUM\n(Amount)")]
        [InlineData("sum(Amount)")]
        public void Validate_DeriveCallsAggregateWithoutOver_Throws(string expr) =>
            AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
                LedgerSchema(), LedgerRequest(DeriveStep(expr)), EmptyRegistry(), LedgerAuthzWithoutSum()),
                "Step 'd': derive 'x': aggregate function 'SUM' at position 0 requires an OVER clause.");
    ```

**Unchanged tests whose messages stay pinned through the callbacks.** These are cited so the reviewer can check them:
- QBT `:922-928` `"Aggregation field 'Sum' on 'Ledger' is not authorized for this caller."`;
- QBT `:2876-2879` `"Field 'Sum' on 'Ledger' referenced by metric 'm' expression is not authorized for this caller."`;
- PBT `:1001-1007` and `:1033-1039`, the "neither an input column nor a whitelisted function" texts;
- PBT `:742-750` `"exceeds 1000 characters"`;
- PBT `:228-234`, which asserts only `"r"`.

- [ ] **Step 2: Run the re-baselined tests and see them fail**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests|FullyQualifiedName~StarRocksQueryBuilderTests"
```
Expected failures. Each one fails because today's output is the raw input text, or because today's message is "forbidden character".

In `StarRocksQueryBuilderTests`:
- `BuildAggregate_ExpressionMetric_UsesRawExpressionInAggFn`;
- `BuildAggregate_ExpressionWithForbiddenSequence_ThrowsTranslationException` (4 cases);
- `BuildAggregate_ExpressionCommentAttemptsToStripOwnershipPredicate_…`;
- `BuildAggregate_NormalExpression_StillProducesExpectedSql`;
- `BuildAggregate_ExpressionWithDoubleQuoteOrBackslash_…` (2 cases);
- `BuildGroupBy_SingleTable_ProducesCompoundSelect`;
- `BuildGroupBy_ExpressionMetric_PassesRawExpression`;
- `BuildGroupBy_MetricExpressionWithSqlComment_…`;
- `BuildGroupBy_MetricExpressionCommentAttemptsToStripOwnershipPredicate_…`;
- `BuildGroupBy_MetricExpressionWithDoubleQuoteOrBackslash_…` (2 cases).

In `StarRocksPipelineBuilderTests`:
- `Build_RunningSumAndDerive_EmitOverAndExpression`;
- `Build_ReadsSkipsIntermediateStep`;
- `Build_DeriveExprWithSqlCommentSequence_Throws` (2 cases);
- `Build_DeriveExprWithHashLineCommentToken_Throws`;
- `Validate_MetricExpressionWithForbiddenSequence_Throws` (3 cases);
- `Build_MetricExpressionCommentAttemptsToCloseAggregate…`;
- `Build_NormalMetricExpression_StillProducesExpectedSql`;
- `Validate_MetricExpressionWithDoubleQuoteOrBackslash_Throws` (2 cases);
- `Validate_DeriveWithDoubleQuoteOrBackslash_Throws` (2 cases);
- `Validate_DeriveCallsAggregateWithoutOver_Throws` (5 cases). Today these pass validation, so nothing is thrown.

The re-pointed `…CallsFunction_DoesNotThrow` theories and `BuildAggregate_ExpressionReferencingTheTenantColumn_IsRejected` **pass** today and keep passing. Record that.

- [ ] **Step 3: Write the new test class**

Create `Iverson.Server/Iverson.StarRocks.Tests/ClientExpressionTests.cs`. It reuses `TestSchemaRegistry.BuildRegistry` (`TestSchemaRegistry.cs`). The rejection inputs reuse these existing test inputs:
- `COALESCE(Amount, "a")` and `Amount \ 2` (QBT `:949-950`, PBT `:1026-1027`, `:1058-1059`);
- the `Rating -- comment` shape (QBT `:2275`);
- `WordCount /* comment */ + 1` (PBT `:705`);
- `SUM(Amount)` and its spellings (PBT `:1043-1047`);
- `SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)` (QBT `:937`);
- `LENGTH` (QBT `:2168` comment);
- the 1,001-`A` string (PBT `:742`);
- `Sum` as a restricted column (QBT `:26-32`).

The other inputs are minimal shape-only expressions.

```csharp
using System.Globalization;
using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.StarRocks.Expressions;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// The client-expression parser behind every expression emission site: AggregationSpec.expression
/// (BuildAggregate), MetricSpec.expression (BuildGroupBy and Pipeline metrics) and
/// DeriveColumn.expr (Pipeline derive). The SQL is written from the parse tree, never from the
/// input text.
/// </summary>
public class ClientExpressionTests
{
    private const string Context = "Test expression";

    private static string QuoteAny(string name) => $"`{name}`";

    private static string Sql(string text, ExpressionContext context) =>
        ClientExpression.ToSql(text, context, QuoteAny, Context);

    // Assert.Throws checks the exact type: nothing but EngagementQueryTranslationException may escape.
    private static string Rejection(
        string text, ExpressionContext context, Func<string, string>? resolveQuoted = null) =>
        Assert.Throws<EngagementQueryTranslationException>(
            () => ClientExpression.ToSql(text, context, resolveQuoted ?? QuoteAny, Context)).Message;

    private static CultureInfo CommaDecimalCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }

    // ── Accepted corpus: every expression the suites and SDKs use today ───────────

    [Theory]
    [InlineData("Rating * 2", "`Rating` * 2")]
    [InlineData("WordCount * 2", "`WordCount` * 2")]
    [InlineData("Rating", "`Rating`")]
    [InlineData("COALESCE(Rating, 0)", "COALESCE(`Rating`, 0)")]
    [InlineData("Rating * (1 - 0)", "`Rating` * (1 - 0)")]
    [InlineData("Rating * (1 - 0) * (1 + 0)", "`Rating` * (1 - 0) * (1 + 0)")]
    [InlineData("price * (1 - discount)", "`price` * (1 - `discount`)")]
    [InlineData("extended_price * (1 - discount)", "`extended_price` * (1 - `discount`)")]
    [InlineData("ExtendedPrice * (1 - Discount) * (1 + Tax)", "`ExtendedPrice` * (1 - `Discount`) * (1 + `Tax`)")]
    [InlineData("ABS(Amount)", "ABS(`Amount`)")]
    [InlineData("ABS (Amount)", "ABS(`Amount`)")]
    [InlineData("ABS\t(Amount)", "ABS(`Amount`)")]
    [InlineData("ABS\n(Amount)", "ABS(`Amount`)")]
    [InlineData("abs(Amount)", "ABS(`Amount`)")]
    [InlineData("ROUND(NULLIF(Amount, 0), 2)", "ROUND(NULLIF(`Amount`, 0), 2)")]
    [InlineData("COALESCE(Amount, NULL, 0)", "COALESCE(`Amount`, NULL, 0)")]
    public void AggregateArgument_AcceptedCorpus_EmitsCanonicalSql(string text, string expected) =>
        Sql(text, ExpressionContext.AggregateArgument).Should().Be(expected);

    [Theory]
    [InlineData("WordCount * 2", "`WordCount` * 2")]
    [InlineData("WordCount + 1", "`WordCount` + 1")]
    [InlineData("WordCount", "`WordCount`")]
    [InlineData("100.0 * n / SUM(n) OVER ()", "100.0 * `n` / SUM(`n`) OVER ()")]
    [InlineData("100.0 * article_count / SUM(article_count) OVER ()", "100.0 * `article_count` / SUM(`article_count`) OVER ()")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)", "SUM(`Amount`) OVER (PARTITION BY `Region` ORDER BY `Amount` DESC)")]
    [InlineData("sum (Amount) over (partition by Region, Kind order by Amount asc, Id)", "SUM(`Amount`) OVER (PARTITION BY `Region`, `Kind` ORDER BY `Amount` ASC, `Id`)")]
    [InlineData("COUNT(*) OVER (PARTITION BY Region)", "COUNT(*) OVER (PARTITION BY `Region`)")]
    [InlineData("ROUND(AVG(Amount) OVER (), 2)", "ROUND(AVG(`Amount`) OVER (), 2)")]
    [InlineData("MIN(Amount) OVER () - MAX(Amount) OVER ()", "MIN(`Amount`) OVER () - MAX(`Amount`) OVER ()")]
    public void Derive_AcceptedCorpus_EmitsCanonicalSql(string text, string expected) =>
        Sql(text, ExpressionContext.Derive).Should().Be(expected);

    // ── Emitter: operators, parentheses and numbers come from the tree ─────────────

    [Theory]
    [InlineData("a - (b - c)", "`a` - (`b` - `c`)")]
    [InlineData("(a - b) - c", "`a` - `b` - `c`")]
    [InlineData("a * (b + c)", "`a` * (`b` + `c`)")]
    [InlineData("(a * b) + c", "`a` * `b` + `c`")]
    [InlineData("a / (b * c)", "`a` / (`b` * `c`)")]
    [InlineData("a % 7", "`a` % 7")]
    [InlineData("-a", "-`a`")]
    [InlineData("- -a", "-(-`a`)")]
    [InlineData("a - -1", "`a` - -1")]
    [InlineData("-(a + b)", "-(`a` + `b`)")]
    [InlineData("((a))", "`a`")]
    [InlineData("  a\t*\n2  ", "`a` * 2")]
    [InlineData("007", "7")]
    [InlineData("100.0", "100.0")]
    [InlineData("null", "NULL")]
    public void Emitter_WritesOperatorsParenthesesAndNumbersFromTheTree(string text, string expected) =>
        Sql(text, ExpressionContext.Derive).Should().Be(expected);

    [Fact]
    public void Emitter_PrintsNumbersInvariantly_UnderACommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaDecimalCulture();
            Sql("2.5 * a", ExpressionContext.AggregateArgument).Should().Be("2.5 * `a`");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Emitter_TakesEveryColumnFromTheCallback_IncludingWindowColumns()
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["amount"] = "`t`.`Amount`",
            ["region"] = "`t`.`Region`",
        };
        var seen = new List<string>();
        string Resolve(string name)
        {
            seen.Add(name);
            return canonical[name];
        }

        ClientExpression.ToSql(
                "SUM(amount) OVER (PARTITION BY REGION ORDER BY Amount DESC)", ExpressionContext.Derive, Resolve, Context)
            .Should().Be("SUM(`t`.`Amount`) OVER (PARTITION BY `t`.`Region` ORDER BY `t`.`Amount` DESC)");
        seen.Should().Equal("amount", "REGION", "Amount");
    }

    // ── Rejected corpus: the lexer ─────────────────────────────────────────────────

    [Theory]
    [InlineData("COALESCE(Amount, \"a\")", "character '\"' at position 17")]
    [InlineData("COALESCE(Amount, 'a')", "character ''' at position 17")]
    [InlineData("Amount \\ 2", "character '\\' at position 7")]
    [InlineData("Amount;", "character ';' at position 6")]
    [InlineData("Amount #", "character '#' at position 7")]
    [InlineData("Amount`", "character '`' at position 6")]
    [InlineData("Amount -- comment", "comment sequence '--' at position 7")]
    [InlineData("Amount /* comment */ + 1", "comment sequence '/*' at position 7")]
    [InlineData("Amount > 0", "character '>' at position 7")]
    [InlineData("Amount = 0", "character '=' at position 7")]
    [InlineData("Amount < 0", "character '<' at position 7")]
    [InlineData("Amount & 1", "character '&' at position 7")]
    [InlineData("Amount | 1", "character '|' at position 7")]
    [InlineData("Amount ^ 1", "character '^' at position 7")]
    [InlineData("~Amount", "character '~' at position 0")]
    [InlineData("Type.Field", "character '.' at position 4")]
    [InlineData("\u00C9tat * 2", "character U+00C9 at position 0")]
    [InlineData("Amount\u00A0* 2", "character U+00A0 at position 6")]
    public void Lexer_RefusesCharactersOutsideTheGrammar(string text, string expected) =>
        Rejection(text, ExpressionContext.Derive)
            .Should().Be($"{Context}: {expected} is not allowed in an expression.");

    [Fact]
    public void Lexer_RefusesANumberThatDoesNotFit_WithoutAnOverflowException()
    {
        var digits = new string('9', 40);
        Rejection(digits, ExpressionContext.AggregateArgument)
            .Should().Be($"{Context}: number '{digits}' at position 0 is out of range.");
    }

    // ── Rejected corpus: the parser ────────────────────────────────────────────────

    [Theory]
    [InlineData("LENGTH(Amount)", "unknown function 'LENGTH' at position 0")]
    [InlineData("Order * 2", "unexpected 'Order' at position 0")]
    [InlineData("Amount Amount", "unexpected 'Amount' at position 7")]
    [InlineData("COALESCE(*)", "unexpected '*' at position 9")]
    [InlineData("+Amount", "unexpected '+' at position 0")]
    [InlineData("Amount *", "unexpected end of expression at position 8")]
    [InlineData("(Amount", "unexpected end of expression at position 7")]
    [InlineData("ABS()", "unexpected ')' at position 4")]
    [InlineData("1.", "number at position 0 has no digits after its decimal point")]
    public void Parser_RefusesMalformedExpressions(string text, string expected) =>
        Rejection(text, ExpressionContext.Derive).Should().Be($"{Context}: {expected}.");

    [Theory]
    [InlineData("SUM(Amount)", "SUM", 0)]
    [InlineData("sum (Amount)", "SUM", 0)]
    [InlineData("COUNT(*)", "COUNT", 0)]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region ORDER BY Amount DESC)", "SUM", 0)]
    [InlineData("COALESCE(MAX(Amount), 0)", "MAX", 9)]
    public void AggregateArgument_RefusesAggregatesAndTheirWindows(string text, string function, int position) =>
        Rejection(text, ExpressionContext.AggregateArgument).Should().Be(
            $"{Context}: aggregate function '{function}' at position {position} is not allowed here; " +
            "only COALESCE, NULLIF, ROUND and ABS may be called.");

    [Fact]
    public void AggregateArgument_RefusesAWindowAfterAScalarFunction() =>
        Rejection("COALESCE(Amount, 0) OVER ()", ExpressionContext.AggregateArgument)
            .Should().Be($"{Context}: unexpected 'OVER' at position 20.");

    [Fact]
    public void Derive_RefusesAWindowAfterAScalarFunction() =>
        Rejection("ABS(Amount) OVER ()", ExpressionContext.Derive)
            .Should().Be($"{Context}: unexpected 'OVER' at position 12.");

    [Theory]
    [InlineData("SUM(Amount)", "SUM", 0)]
    [InlineData("sum(Amount)", "SUM", 0)]
    [InlineData("COUNT(*)", "COUNT", 0)]
    [InlineData("100.0 * n / SUM(n)", "SUM", 12)]
    public void Derive_RefusesAnAggregateWithoutOver(string text, string function, int position) =>
        Rejection(text, ExpressionContext.Derive)
            .Should().Be($"{Context}: aggregate function '{function}' at position {position} requires an OVER clause.");

    [Fact]
    public void Derive_RefusesAnAggregateInsideAnAggregatesArgument() =>
        Rejection("SUM(SUM(n) OVER ()) OVER ()", ExpressionContext.Derive).Should().Be(
            $"{Context}: aggregate function 'SUM' at position 4 is not allowed here; " +
            "only COALESCE, NULLIF, ROUND and ABS may be called.");

    [Theory]
    [InlineData("SUM(*) OVER ()", 4)]
    [InlineData("COUNT(*, Amount) OVER ()", 6)]
    [InlineData("COUNT(* + 1) OVER ()", 6)]
    public void Derive_AllowsStarOnlyAsCountsSoleArgument(string text, int position) =>
        Rejection(text, ExpressionContext.Derive).Should().Be($"{Context}: unexpected '*' at position {position}.");

    [Theory]
    [InlineData("SUM(Amount) OVER (PARTITION BY ABS(Region))", "unexpected 'ABS' at position 31")]
    [InlineData("SUM(Amount) OVER (ORDER BY Amount DESC DESC)", "unexpected 'DESC' at position 39")]
    [InlineData("SUM(Amount) OVER (ORDER Amount)", "unexpected 'Amount' at position 24")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Order)", "unexpected 'Order' at position 31")]
    public void Derive_WindowClauseTakesOnlyColumns(string text, string expected) =>
        Rejection(text, ExpressionContext.Derive).Should().Be($"{Context}: {expected}.");

    // ── Limits: depth, then length (length is checked before anything is parsed) ───

    private static string Nest(int levels) => new string('(', levels) + "Amount" + new string(')', levels);

    private static string Negations(int count) => string.Join(" ", Enumerable.Repeat("-", count)) + " a";

    [Fact]
    public void Parser_AcceptsNestingAtTheCap() =>
        Sql(Nest(31), ExpressionContext.Derive).Should().Be("`Amount`");

    [Fact]
    public void Parser_RefusesNestingOverTheCap() =>
        Rejection(Nest(32), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression nests deeper than 32 levels at position 32.");

    [Fact]
    public void Parser_CountsUnaryMinusTowardTheCap()
    {
        Sql(Negations(31), ExpressionContext.Derive).Should().StartWith("-(-(");
        Rejection(Negations(32), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression nests deeper than 32 levels at position 64.");
    }

    [Fact]
    public void LengthCap_RunsBeforeParsing()
    {
        // 1,001 opening parentheses would trip the depth cap if parsing ran first.
        Rejection(new string('(', 1001), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression exceeds 1000 characters.");
    }

    [Fact]
    public void LengthCap_RefusesAValidIdentifierOverTheCap() =>
        Rejection(new string('A', 1001), ExpressionContext.Derive)
            .Should().Be($"{Context}: expression exceeds 1000 characters.");

    [Fact]
    public void LengthCap_AcceptsExactly1000Characters() =>
        Sql(new string('A', 1000), ExpressionContext.Derive).Should().Be($"`{new string('A', 1000)}`");

    // ── Columns in arguments, PARTITION BY and ORDER BY all go through the callback ─

    private static string DenySecret(string name) =>
        name == "Secret"
            ? throw new EngagementQueryTranslationException("column 'Secret' is not authorized")
            : $"`{name}`";

    [Theory]
    [InlineData("COALESCE(Secret, 0)")]
    [InlineData("Amount + ABS(Secret)")]
    public void AggregateArgument_ColumnsInArgumentsGoThroughTheCallback(string text) =>
        Rejection(text, ExpressionContext.AggregateArgument, DenySecret).Should().Be("column 'Secret' is not authorized");

    [Theory]
    [InlineData("SUM(Secret) OVER ()")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Secret)")]
    [InlineData("SUM(Amount) OVER (PARTITION BY Region, Secret)")]
    [InlineData("SUM(Amount) OVER (ORDER BY Secret DESC)")]
    public void Derive_ColumnsInArgumentsAndWindowsGoThroughTheCallback(string text) =>
        Rejection(text, ExpressionContext.Derive, DenySecret).Should().Be("column 'Secret' is not authorized");

    // ── Each emission site: canonical SQL, and its pinned messages through its callback ─

    private static EngagementQuerySchema AuthorSchema() => new("Author", "authors", "Id", ["Name", "Bio", "Rating"]);

    private static EngagementQuerySchema ArticleSchema() => new("Article", "articles", "Id", ["Title", "WordCount", "AuthorId"]);

    // "Sum" is a column named like a function, and the caller may not read it.
    private static EngagementQuerySchema LedgerSchema() => new("Ledger", "ledgers", "Id", ["Sum", "Amount", "Region"]);

    private static Dictionary<string, AuthorizationConstraint> LedgerAuthzWithoutSum() => new()
    {
        ["Ledger"] = new(AllowedFields: new HashSet<string> { "Id", "Amount", "Region" }, OwnerColumn: null, OwnerValue: null)
    };

    private static PipelineRequest PipelineRequestFor(string typeName, params PipelineStep[] steps)
    {
        var request = new PipelineRequest { TypeName = typeName };
        request.Steps.AddRange(steps);
        return request;
    }

    [Fact]
    public void BuildAggregate_EmitsTheParsedExpressionWithResolvedColumns()
    {
        var spec = new AggregationDescriptor("total", AggregationKind.Sum, "Rating", Expression: "rating * 2");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Be("SELECT SUM(`Rating` * 2) AS metric_val FROM `authors`");
    }

    [Fact]
    public void BuildAggregate_WithAJoin_QualifiesEveryExpressionColumn()
    {
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };
        var spec = new AggregationDescriptor(
            "total", AggregationKind.Count, "Rating", Expression: "COALESCE(WordCount, Rating)");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, joins: joins,
            registry: TestSchemaRegistry.BuildRegistry(AuthorSchema(), ArticleSchema()));

        sql.Should().Be(
            "SELECT COUNT(DISTINCT COALESCE(`articles`.`WordCount`, `authors`.`Rating`)) AS metric_val " +
            "FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
    }

    [Fact]
    public void BuildAggregate_RestrictedColumnInsideAFunction_KeepsThePinnedMessage()
    {
        var spec = new AggregationDescriptor("total", AggregationKind.Avg, "Amount", Expression: "COALESCE(Sum, 0)");

        var act = () => StarRocksQueryBuilder.BuildAggregate(
            "ledgers", LedgerSchema(), null, spec, authz: LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Aggregation field 'Sum' on 'Ledger' is not authorized for this caller.");
    }

    [Fact]
    public void BuildGroupBy_EmitsTheParsedExpressionWithQualifiedColumns()
    {
        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "revenue", Type = AggregationType.Sum, Expression = "rating * 2" });

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy(
            "authors", AuthorSchema(), request, TestSchemaRegistry.BuildRegistry(AuthorSchema()));

        sql.Should().Contain("SUM(`authors`.`Rating` * 2) AS `revenue`");
    }

    [Theory]
    [InlineData("COALESCE(Sum, 0)", "Field 'Sum' on 'Ledger' referenced by metric 'm' expression is not authorized for this caller.")]
    [InlineData("COALESCE(Nope, 0)", "Metric 'm' expression references unknown field 'Nope'.")]
    public void BuildGroupBy_ColumnsInsideAFunction_KeepThePinnedMessages(string expression, string message)
    {
        var request = new GroupByRequest { TypeName = "Ledger", Keys = { "Region" } };
        request.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Avg, Expression = expression });

        var act = () => StarRocksQueryBuilder.BuildGroupBy(
            "ledgers", LedgerSchema(), request, TestSchemaRegistry.BuildRegistry(LedgerSchema()),
            authz: LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>().WithMessage(message);
    }

    [Fact]
    public void PipelineMetric_EmitsTheParsedExpressionWithResolvedColumns()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "wc2", Type = AggregationType.Sum, Expression = "wordcount * 2" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), PipelineRequestFor("Article", step), TestSchemaRegistry.BuildRegistry());

        sql.Should().Contain("`agg` AS (SELECT `AuthorId`, SUM(`WordCount` * 2) AS `wc2` FROM `base` GROUP BY `AuthorId`)");
    }

    [Fact]
    public void PipelineMetric_RestrictedColumnInsideAFunction_KeepsThePinnedMessage()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "Region" });
        step.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Avg, Expression = "COALESCE(Sum, 0)" });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), PipelineRequestFor("Ledger", step), TestSchemaRegistry.BuildRegistry(), LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Step 'agg': metric 'm' expression references 'Sum', which is neither an input column nor a whitelisted function.");
    }

    [Fact]
    public void PipelineDerive_EmitsTheParsedExpressionWithResolvedColumns_IncludingAnEarlierWindowAlias()
    {
        var step = new PipelineStep { Name = "d" };
        step.Windows.Add(new WindowFunction { Alias = "rn", Kind = WindowFunctionKind.RowNumber, OrderBy = "WordCount" });
        step.Derive.Add(new DeriveColumn { Alias = "x", Expr = "wordcount + 1" });
        step.Derive.Add(new DeriveColumn { Alias = "y", Expr = "RN * X" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), PipelineRequestFor("Article", step), TestSchemaRegistry.BuildRegistry());

        sql.Should().Contain(
            "`d` AS (SELECT *, ROW_NUMBER() OVER (ORDER BY `WordCount` ASC) AS `rn`, " +
            "(`WordCount` + 1) AS `x`, (`rn` * `x`) AS `y` FROM `base`)");
    }

    [Theory]
    [InlineData("SUM(Amount) OVER (PARTITION BY Sum)")]
    [InlineData("SUM(Amount) OVER (ORDER BY Sum DESC)")]
    [InlineData("SUM(Sum) OVER ()")]
    public void PipelineDerive_RestrictedColumnAnywhereInTheWindow_KeepsThePinnedMessage(string expression)
    {
        var step = new PipelineStep { Name = "d" };
        step.Derive.Add(new DeriveColumn { Alias = "x", Expr = expression });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            LedgerSchema(), PipelineRequestFor("Ledger", step), TestSchemaRegistry.BuildRegistry(), LedgerAuthzWithoutSum());

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Step 'd': derive 'x' references 'Sum', which is neither an input column nor a whitelisted function.");
    }

    // ── Range bucket bounds (QB BuildRangeSql) ─────────────────────────────────────

    [Theory]
    [InlineData(double.NaN, null)]
    [InlineData(null, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 3.0)]
    public void BuildAggregate_RangeBucketWithANonFiniteBound_IsRejected(double? from, double? to)
    {
        var spec = new AggregationDescriptor(
            "r", AggregationKind.Range, "Rating", RangeBuckets: [new RangeBucketDescriptor("b", from, to)]);

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        act.Should().Throw<EngagementQueryTranslationException>()
            .WithMessage("Range bucket 0 has a non-finite bound; bounds must be finite numbers.");
    }

    [Fact]
    public void BuildAggregate_RangeBounds_AreFormattedInvariantly_UnderACommaDecimalCulture()
    {
        var spec = new AggregationDescriptor(
            "r", AggregationKind.Range, "Rating", RangeBuckets: [new RangeBucketDescriptor("mid", 2.5, 7.5)]);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaDecimalCulture();

            var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

            sql.Should().Contain("WHEN `Rating` >= 2.5 AND `Rating` < 7.5 THEN @__rb0");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
```

- [ ] **Step 4: Run the new class and see it fail**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~ClientExpressionTests"
```
Expected: the test project fails to compile with CS0234 or CS0246: namespace `Iverson.StarRocks.Expressions`, `ClientExpression` and `ExpressionContext` do not exist. That is the red state for the new class. The re-baselines were already shown red in Step 2.

- [ ] **Step 5: Implement**

**5a. Create `Iverson.Server/Iverson.StarRocks/Expressions/ClientExpression.cs`:**
```csharp
namespace Iverson.StarRocks.Expressions;

/// <summary>
/// Which emission site a client expression is compiled for; it decides which functions the
/// expression may call.
/// </summary>
internal enum ExpressionContext
{
    /// <summary>
    /// <c>AggregationSpec.expression</c> and <c>MetricSpec.expression</c>. The site wraps the result
    /// in an aggregate, so only the scalar functions COALESCE, NULLIF, ROUND and ABS may be called:
    /// an aggregate or a window there would nest one aggregate inside another.
    /// </summary>
    AggregateArgument,

    /// <summary>
    /// <c>DeriveColumn.expr</c>. The scalar functions plus SUM, AVG, MIN, MAX and COUNT (with
    /// <c>COUNT(*)</c>). A derive step has no GROUP BY, so the five aggregates must carry an
    /// <c>OVER (…)</c> clause. An aggregate's own argument is an aggregate argument.
    /// </summary>
    Derive,
}

/// <summary>
/// Compiles a client-supplied expression into StarRocks SQL. The text is length-checked, lexed and
/// parsed into a tree, and the SQL is written from the tree:
/// <list type="bullet">
/// <item><description>columns come from <c>resolveQuoted</c>;</description></item>
/// <item><description>function names come from the allow-list;</description></item>
/// <item><description>numbers are re-printed invariantly;</description></item>
/// <item><description>operators and parentheses come from the tree's shape.</description></item>
/// </list>
/// Nothing of the input text is copied to the output. Every failure is an
/// <see cref="EngagementQueryTranslationException"/>, which the RPC layer maps to InvalidArgument.
/// </summary>
internal static class ClientExpression
{
    internal const int MaxLength = 1000;

    /// <param name="text">The client's expression.</param>
    /// <param name="context">The emission site's rules.</param>
    /// <param name="resolveQuoted">
    /// Called for every column the expression names, including <c>PARTITION BY</c> and
    /// <c>ORDER BY</c> columns. It resolves and authorizes the name for the site, and returns its
    /// quoted SQL identifier or throws with the site's own message.
    /// </param>
    /// <param name="errorContext">Prefix for every message, naming the site and field.</param>
    public static string ToSql(
        string text, ExpressionContext context, Func<string, string> resolveQuoted, string errorContext)
    {
        // Before lexing: a valid identifier can be any length, so only this cap rejects it.
        if (text.Length > MaxLength)
            throw new EngagementQueryTranslationException(
                $"{errorContext}: expression exceeds {MaxLength} characters.");

        var tokens = SqlExpressionLexer.Tokenize(text, errorContext);
        var root = SqlExpressionParser.Parse(tokens, context, errorContext);
        return SqlExpressionEmitter.Emit(root, resolveQuoted);
    }
}
```

**5b. Create `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionLexer.cs`:**
```csharp
using System.Globalization;

namespace Iverson.StarRocks.Expressions;

internal enum SqlTokenKind
{
    Identifier,
    Number,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    LParen,
    RParen,
    Comma,
    End,
}

/// <param name="Text">The token's spelling: an identifier's name, a number's digits, or the operator.</param>
/// <param name="Number">The parsed value of a <see cref="SqlTokenKind.Number"/> token.</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Position, decimal Number = 0m);

/// <summary>
/// Tokenizes a client expression. It accepts:
/// <list type="bullet">
/// <item><description>ASCII whitespace (space, tab, CR, LF);</description></item>
/// <item><description>identifiers <c>[A-Za-z_][A-Za-z0-9_]*</c>;</description></item>
/// <item><description>unsigned decimal numbers <c>\d+(\.\d+)?</c>;</description></item>
/// <item><description>the operators <c>+ - * / %</c>, parentheses and comma.</description></item>
/// </list>
/// Every other character is refused. So are the comment openers <c>--</c> and <c>/*</c>, although
/// each of their characters is an operator.
/// </summary>
internal static class SqlExpressionLexer
{
    public static List<SqlToken> Tokenize(string text, string errorContext)
    {
        var tokens = new List<SqlToken>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is ' ' or '\t' or '\r' or '\n')
            {
                i++;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SqlToken(SqlTokenKind.Identifier, text[start..i], start));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                tokens.Add(ReadNumber(text, ref i, errorContext));
                continue;
            }

            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if ((c == '-' && next == '-') || (c == '/' && next == '*'))
                throw Refused(errorContext, $"comment sequence '{c}{next}' at position {i}");

            var kind = c switch
            {
                '+' => SqlTokenKind.Plus,
                '-' => SqlTokenKind.Minus,
                '*' => SqlTokenKind.Star,
                '/' => SqlTokenKind.Slash,
                '%' => SqlTokenKind.Percent,
                '(' => SqlTokenKind.LParen,
                ')' => SqlTokenKind.RParen,
                ',' => SqlTokenKind.Comma,
                _ => throw Refused(errorContext, $"character {Describe(c)} at position {i}"),
            };
            tokens.Add(new SqlToken(kind, c.ToString(), i));
            i++;
        }

        tokens.Add(new SqlToken(SqlTokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private static SqlToken ReadNumber(string text, ref int i, string errorContext)
    {
        var start = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;

        if (i < text.Length && text[i] == '.')
        {
            i++;
            if (i >= text.Length || !char.IsAsciiDigit(text[i]))
                throw new EngagementQueryTranslationException(
                    $"{errorContext}: number at position {start} has no digits after its decimal point.");
            while (i < text.Length && char.IsAsciiDigit(text[i]))
                i++;
        }

        var spelling = text[start..i];

        // TryParse, never Parse: a value too large for decimal must come back as a refusal, not as
        // an OverflowException that the RPC layer would answer with UNKNOWN.
        if (!decimal.TryParse(spelling, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new EngagementQueryTranslationException(
                $"{errorContext}: number '{spelling}' at position {start} is out of range.");

        return new SqlToken(SqlTokenKind.Number, spelling, start, value);
    }

    // Printable ASCII is shown as itself; control characters and non-ASCII by code point.
    private static string Describe(char c) =>
        c is >= ' ' and <= '~' ? $"'{c}'" : $"U+{(int)c:X4}";

    private static EngagementQueryTranslationException Refused(string errorContext, string what) =>
        new($"{errorContext}: {what} is not allowed in an expression.");
}
```

**5c. Create `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionParser.cs`:**
```csharp
namespace Iverson.StarRocks.Expressions;

/// <summary>A node of a parsed client expression. <see cref="Position"/> is the source offset of its first token.</summary>
internal abstract record SqlNode(int Position);

internal sealed record NumberNode(decimal Value, int Position) : SqlNode(Position);

internal sealed record NullNode(int Position) : SqlNode(Position);

internal sealed record ColumnNode(string Name, int Position) : SqlNode(Position);

internal sealed record NegateNode(SqlNode Operand, int Position) : SqlNode(Position);

/// <param name="Operator">One of <c>+ - * / %</c>.</param>
internal sealed record BinaryNode(char Operator, SqlNode Left, SqlNode Right, int Position) : SqlNode(Position);

/// <param name="Function">The allow-list's own upper-case spelling, never the input's.</param>
/// <param name="CountStar">True for <c>COUNT(*)</c>; <see cref="Arguments"/> is then empty.</param>
/// <param name="Over">The window clause; present exactly when the function is an aggregate.</param>
internal sealed record CallNode(
    string Function, IReadOnlyList<SqlNode> Arguments, bool CountStar, WindowNode? Over, int Position)
    : SqlNode(Position);

internal sealed record WindowNode(IReadOnlyList<ColumnNode> PartitionBy, IReadOnlyList<OrderKeyNode> OrderBy);

/// <param name="Direction"><c>"ASC"</c>, <c>"DESC"</c>, or null when the input gave none.</param>
internal sealed record OrderKeyNode(ColumnNode Column, string? Direction);

/// <summary>
/// Recursive-descent parser for client expressions. Precedence, lowest first:
/// <code>
/// expression     := multiplicative (('+' | '-') multiplicative)*
/// multiplicative := unary (('*' | '/' | '%') unary)*
/// unary          := '-' unary | primary
/// primary        := number | NULL | column | call | '(' expression ')'
/// call           := function '(' (expression (',' expression)* | '*') ')' [over]
/// over           := OVER '(' [PARTITION BY column (',' column)*]
///                            [ORDER BY column [ASC | DESC] (',' column [ASC | DESC])*] ')'
/// </code>
/// <list type="bullet">
/// <item><description><c>*</c> is an argument only as COUNT's sole argument.</description></item>
/// <item><description><c>over</c> is required after an aggregate and allowed nowhere else.</description></item>
/// <item><description>Aggregates are allowed only in <see cref="ExpressionContext.Derive"/>.</description></item>
/// <item><description>The reserved words are never column names.</description></item>
/// <item><description>Nesting is capped at <see cref="MaxDepth"/> levels. The levels are the root,
/// each parenthesized expression or call argument, and each unary minus. So recursion is bounded
/// whatever the input.</description></item>
/// </list>
/// </summary>
internal sealed class SqlExpressionParser
{
    internal const int MaxDepth = 32;

    private static readonly HashSet<string> ScalarFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "COALESCE", "NULLIF", "ROUND", "ABS"
    };

    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "MIN", "MAX", "COUNT"
    };

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL", "OVER", "PARTITION", "BY", "ORDER", "ASC", "DESC"
    };

    private readonly List<SqlToken> _tokens;
    private readonly string _errorContext;
    private int _pos;
    private int _depth;

    private SqlExpressionParser(List<SqlToken> tokens, string errorContext)
    {
        _tokens = tokens;
        _errorContext = errorContext;
    }

    public static SqlNode Parse(List<SqlToken> tokens, ExpressionContext context, string errorContext)
    {
        var parser = new SqlExpressionParser(tokens, errorContext);
        var root = parser.ParseExpression(context);
        if (parser.Current.Kind != SqlTokenKind.End)
            throw parser.Unexpected();
        return root;
    }

    private SqlToken Current => _tokens[_pos];

    private SqlToken Peek(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

    private SqlNode ParseExpression(ExpressionContext context)
    {
        Enter();
        var left = ParseMultiplicative(context);
        while (Current.Kind is SqlTokenKind.Plus or SqlTokenKind.Minus)
        {
            var op = Current;
            _pos++;
            left = new BinaryNode(op.Kind == SqlTokenKind.Plus ? '+' : '-', left, ParseMultiplicative(context), op.Position);
        }
        _depth--;
        return left;
    }

    private SqlNode ParseMultiplicative(ExpressionContext context)
    {
        var left = ParseUnary(context);
        while (Current.Kind is SqlTokenKind.Star or SqlTokenKind.Slash or SqlTokenKind.Percent)
        {
            var op = Current;
            _pos++;
            var symbol = op.Kind switch
            {
                SqlTokenKind.Star => '*',
                SqlTokenKind.Slash => '/',
                _ => '%',
            };
            left = new BinaryNode(symbol, left, ParseUnary(context), op.Position);
        }
        return left;
    }

    private SqlNode ParseUnary(ExpressionContext context)
    {
        if (Current.Kind != SqlTokenKind.Minus)
            return ParsePrimary(context);

        var minus = Current;
        _pos++;
        Enter();
        var operand = ParseUnary(context);
        _depth--;
        return new NegateNode(operand, minus.Position);
    }

    private SqlNode ParsePrimary(ExpressionContext context)
    {
        var token = Current;
        switch (token.Kind)
        {
            case SqlTokenKind.Number:
                _pos++;
                return new NumberNode(token.Number, token.Position);

            case SqlTokenKind.LParen:
            {
                _pos++;
                var inner = ParseExpression(context);
                Expect(SqlTokenKind.RParen);
                return inner;
            }

            case SqlTokenKind.Identifier when token.Text.Equals("NULL", StringComparison.OrdinalIgnoreCase):
                _pos++;
                return new NullNode(token.Position);

            case SqlTokenKind.Identifier when ReservedWords.Contains(token.Text):
                throw Unexpected();

            case SqlTokenKind.Identifier when Peek(1).Kind == SqlTokenKind.LParen:
                return ParseCall(context);

            case SqlTokenKind.Identifier:
                _pos++;
                return new ColumnNode(token.Text, token.Position);

            default:
                throw Unexpected();
        }
    }

    private SqlNode ParseCall(ExpressionContext context)
    {
        var name = Current;
        string function;
        bool isAggregate;
        if (AggregateFunctions.TryGetValue(name.Text, out var aggregate))
        {
            function = aggregate;
            isAggregate = true;
        }
        else if (ScalarFunctions.TryGetValue(name.Text, out var scalar))
        {
            function = scalar;
            isAggregate = false;
        }
        else
        {
            throw Invalid($"unknown function '{name.Text}' at position {name.Position}");
        }

        if (isAggregate && context == ExpressionContext.AggregateArgument)
            throw Invalid($"aggregate function '{function}' at position {name.Position} is not allowed here; " +
                          "only COALESCE, NULLIF, ROUND and ABS may be called");

        _pos += 2; // the name and its '('

        // An aggregate's argument is itself an aggregate argument: no aggregate or window inside it.
        var argumentContext = isAggregate ? ExpressionContext.AggregateArgument : context;
        var arguments = new List<SqlNode>();
        var countStar = false;
        if (function == "COUNT" && Current.Kind == SqlTokenKind.Star && Peek(1).Kind == SqlTokenKind.RParen)
        {
            countStar = true;
            _pos++;
        }
        else
        {
            arguments.Add(ParseExpression(argumentContext));
            while (Current.Kind == SqlTokenKind.Comma)
            {
                _pos++;
                arguments.Add(ParseExpression(argumentContext));
            }
        }
        Expect(SqlTokenKind.RParen);

        WindowNode? over = null;
        if (isAggregate)
        {
            if (!IsWord("OVER"))
                throw Invalid($"aggregate function '{function}' at position {name.Position} requires an OVER clause");
            over = ParseOver();
        }

        return new CallNode(function, arguments, countStar, over, name.Position);
    }

    private WindowNode ParseOver()
    {
        _pos++; // OVER
        Expect(SqlTokenKind.LParen);

        var partitionBy = new List<ColumnNode>();
        if (IsWord("PARTITION"))
        {
            _pos++;
            ExpectWord("BY");
            partitionBy.Add(ParseColumn());
            while (Current.Kind == SqlTokenKind.Comma)
            {
                _pos++;
                partitionBy.Add(ParseColumn());
            }
        }

        var orderBy = new List<OrderKeyNode>();
        if (IsWord("ORDER"))
        {
            _pos++;
            ExpectWord("BY");
            orderBy.Add(ParseOrderKey());
            while (Current.Kind == SqlTokenKind.Comma)
            {
                _pos++;
                orderBy.Add(ParseOrderKey());
            }
        }

        Expect(SqlTokenKind.RParen);
        return new WindowNode(partitionBy, orderBy);
    }

    private OrderKeyNode ParseOrderKey()
    {
        var column = ParseColumn();
        string? direction = null;
        if (IsWord("ASC"))
        {
            direction = "ASC";
            _pos++;
        }
        else if (IsWord("DESC"))
        {
            direction = "DESC";
            _pos++;
        }
        return new OrderKeyNode(column, direction);
    }

    private ColumnNode ParseColumn()
    {
        var token = Current;
        if (token.Kind != SqlTokenKind.Identifier || ReservedWords.Contains(token.Text)
            || Peek(1).Kind == SqlTokenKind.LParen)
            throw Unexpected();
        _pos++;
        return new ColumnNode(token.Text, token.Position);
    }

    private bool IsWord(string word) =>
        Current.Kind == SqlTokenKind.Identifier && Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private void ExpectWord(string word)
    {
        if (!IsWord(word))
            throw Unexpected();
        _pos++;
    }

    private void Expect(SqlTokenKind kind)
    {
        if (Current.Kind != kind)
            throw Unexpected();
        _pos++;
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
            throw Invalid($"expression nests deeper than {MaxDepth} levels at position {Current.Position}");
    }

    private EngagementQueryTranslationException Unexpected() =>
        Current.Kind == SqlTokenKind.End
            ? Invalid($"unexpected end of expression at position {Current.Position}")
            : Invalid($"unexpected '{Current.Text}' at position {Current.Position}");

    private EngagementQueryTranslationException Invalid(string message) =>
        new($"{_errorContext}: {message}.");
}
```

**5d. Create `Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionEmitter.cs`:**
```csharp
using System.Globalization;

namespace Iverson.StarRocks.Expressions;

/// <summary>
/// Writes StarRocks SQL from a parsed client expression. Nothing is copied from the input text:
/// <list type="bullet">
/// <item><description>columns come from the site's callback;</description></item>
/// <item><description>function names come from the parser's allow-list;</description></item>
/// <item><description>numbers are re-printed with <see cref="CultureInfo.InvariantCulture"/>;</description></item>
/// <item><description>every operator and parenthesis comes from the tree.</description></item>
/// </list>
/// A child is parenthesized when its precedence is lower than its position needs, so the output
/// parses back to the same tree.
/// </summary>
internal static class SqlExpressionEmitter
{
    private const int AdditivePrecedence = 1;
    private const int MultiplicativePrecedence = 2;
    private const int UnaryPrecedence = 3;
    private const int PrimaryPrecedence = 4;

    public static string Emit(SqlNode node, Func<string, string> resolveQuoted) => node switch
    {
        NumberNode number => number.Value.ToString(CultureInfo.InvariantCulture),
        NullNode => "NULL",
        ColumnNode column => resolveQuoted(column.Name),
        // The operand is parenthesized unless it is a primary, so a negated negation is written
        // "-(-x)". Two adjacent minus signs would open a SQL line comment.
        NegateNode negate => "-" + Wrap(negate.Operand, PrimaryPrecedence, resolveQuoted),
        // Left-associative: an equal-precedence right operand keeps its parentheses, so
        // "a - (b - c)" is not rewritten as "a - b - c".
        BinaryNode binary =>
            $"{Wrap(binary.Left, Precedence(binary), resolveQuoted)} {binary.Operator} " +
            $"{Wrap(binary.Right, Precedence(binary) + 1, resolveQuoted)}",
        CallNode call => EmitCall(call, resolveQuoted),
        _ => throw new EngagementQueryTranslationException(
            $"Unsupported expression node '{node.GetType().Name}'."),
    };

    private static string Wrap(SqlNode child, int minimumPrecedence, Func<string, string> resolveQuoted)
    {
        var sql = Emit(child, resolveQuoted);
        return Precedence(child) < minimumPrecedence ? $"({sql})" : sql;
    }

    private static int Precedence(SqlNode node) => node switch
    {
        BinaryNode { Operator: '+' or '-' } => AdditivePrecedence,
        BinaryNode => MultiplicativePrecedence,
        NegateNode => UnaryPrecedence,
        _ => PrimaryPrecedence,
    };

    private static string EmitCall(CallNode call, Func<string, string> resolveQuoted)
    {
        var arguments = call.CountStar
            ? "*"
            : string.Join(", ", call.Arguments.Select(argument => Emit(argument, resolveQuoted)));
        var sql = $"{call.Function}({arguments})";
        if (call.Over is null)
            return sql;

        var clauses = new List<string>();
        if (call.Over.PartitionBy.Count > 0)
            clauses.Add("PARTITION BY " +
                        string.Join(", ", call.Over.PartitionBy.Select(column => resolveQuoted(column.Name))));
        if (call.Over.OrderBy.Count > 0)
            clauses.Add("ORDER BY " + string.Join(", ", call.Over.OrderBy.Select(key =>
                key.Direction is null
                    ? resolveQuoted(key.Column.Name)
                    : $"{resolveQuoted(key.Column.Name)} {key.Direction}")));
        return $"{sql} OVER ({string.Join(" ", clauses)})";
    }
}
```

**5e. `StarRocksPipelineBuilder.cs`** (quotes match the file after Task 1):

Add `using Iverson.StarRocks.Expressions;` after `using Iverson.Client.Contracts;` (`:4`).

`:8-12`, replace:
```csharp
/// <summary>
/// One pipeline step's output column set: <see cref="Columns"/> maps a referenced
/// name (case-insensitive) to the canonical output column name emitted in SQL.
/// </summary>
internal sealed record StepColumns(string Name, Dictionary<string, string> Columns);
```
with:
```csharp
/// <summary>
/// One pipeline step's output column set: <see cref="Columns"/> maps a referenced
/// name (case-insensitive) to the canonical output column name emitted in SQL.
/// </summary>
internal sealed record StepColumns(string Name, Dictionary<string, string> Columns)
{
    /// <summary>
    /// This step's derive expressions as SQL, in <c>derive</c> order. The expression parser writes
    /// them during validation, against the scope each one was checked in. That scope is the input
    /// columns, this step's select outputs and window aliases, and earlier derive aliases, and it
    /// exists only during validation.
    /// </summary>
    public IReadOnlyList<string> DeriveSql { get; init; } = [];
}
```

`:24-53`, delete these lines entirely:
- the `TokenRx` field;
- the blank line;
- the four-line comment beginning `// Identifiers a raw expression may use besides input columns.`;
- `DeriveFunctions`;
- `DeriveKeywords`;
- the `IsNonColumnToken` doc comment and method.

Keep `:23` `private static readonly Regex IdentifierRx = …;` and the blank line after the deleted block, before `// Column-introduction filtering`.

`TrackAndValidate`, `:124-130`, replace:
```csharp
        foreach (var step in request.Steps)
        {
            ValidateStepName(step, steps, registry);
            var input = ResolveInput(step, steps);
            var output = ValidateStepAndComputeOutput(step, input, steps, registry, authz);
            steps.Add(new StepColumns(step.Name, output));
        }
```
with:
```csharp
        foreach (var step in request.Steps)
        {
            ValidateStepName(step, steps, registry);
            var input = ResolveInput(step, steps);
            steps.Add(ValidateStepAndComputeOutput(step, input, steps, registry, authz));
        }
```

`:165`, replace:
```csharp
    private static Dictionary<string, string> ValidateStepAndComputeOutput(
```
with:
```csharp
    private static StepColumns ValidateStepAndComputeOutput(
```

Metric expression validation, `:239-249`, replace:
```csharp
                if (!string.IsNullOrEmpty(m.Expression))
                {
                    RejectForbiddenCharacters(m.Expression, $"Step '{step.Name}': metric '{m.Name}' expression");
                    foreach (Match tok in TokenRx.Matches(m.Expression))
                    {
                        if (IsNonColumnToken(m.Expression, tok)) continue;
                        if (!input.Columns.ContainsKey(tok.Value))
                            throw Invalid($"Step '{step.Name}': metric '{m.Name}' expression references " +
                                          $"'{tok.Value}', which is neither an input column nor a whitelisted function.");
                    }
                }
```
with:
```csharp
                if (!string.IsNullOrEmpty(m.Expression))
                    MetricExpressionSql(step.Name, m, input.Columns);
```

The aggregate branch's return, `:255`, replace:
```csharp
            return output;
        }

        // Non-aggregate step.
```
with:
```csharp
            return new StepColumns(step.Name, output);
        }

        // Non-aggregate step.
```

The derive loop and final return, `:313-326`, replace:
```csharp
        // Derive sees input columns plus window aliases already added this step.
        var deriveScope = new Dictionary<string, string>(output, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in input.Columns) deriveScope.TryAdd(kv.Key, kv.Value);
        foreach (var d in step.Derive)
        {
            if (string.IsNullOrEmpty(d.Alias) || !IdentifierRx.IsMatch(d.Alias))
                throw Invalid($"Step '{step.Name}': derive alias '{d.Alias}' is not a valid identifier.");
            ValidateDeriveExpr(step.Name, d, deriveScope);
            AddOutput(d.Alias);
            deriveScope.TryAdd(d.Alias, d.Alias);
        }

        return output;
    }
```
with:
```csharp
        // Derive sees input columns plus window aliases already added this step.
        var deriveScope = new Dictionary<string, string>(output, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in input.Columns) deriveScope.TryAdd(kv.Key, kv.Value);
        var deriveSql = new List<string>();
        foreach (var d in step.Derive)
        {
            if (string.IsNullOrEmpty(d.Alias) || !IdentifierRx.IsMatch(d.Alias))
                throw Invalid($"Step '{step.Name}': derive alias '{d.Alias}' is not a valid identifier.");
            deriveSql.Add(DeriveExpressionSql(step.Name, d, deriveScope));
            AddOutput(d.Alias);
            deriveScope.TryAdd(d.Alias, d.Alias);
        }

        return new StepColumns(step.Name, output) { DeriveSql = deriveSql };
    }
```

`:385-422`, replace everything from the `RejectForbiddenCharacters` doc comment (`/// <summary>` / `/// Rejects a raw SQL fragment that contains a semicolon, …`) through the end of `ValidateDeriveExpr` (its closing `}`) with:
```csharp
    /// <summary>
    /// A pipeline metric's expression as SQL. <see cref="TrackAndValidate"/> calls it to reject a
    /// bad expression before any SQL is built, and <see cref="EmitMetric"/> calls it again to emit.
    /// Both passes use the same input-column scope, so they cannot disagree. Column access is
    /// already authorized: <see cref="ColumnsFor"/> leaves restricted fields out of every step's
    /// columns.
    /// </summary>
    private static string MetricExpressionSql(string stepName, MetricSpec m, Dictionary<string, string> input) =>
        ClientExpression.ToSql(
            m.Expression, ExpressionContext.AggregateArgument,
            name => input.TryGetValue(name, out var column)
                ? $"`{column}`"
                : throw Invalid($"Step '{stepName}': metric '{m.Name}' expression references " +
                                $"'{name}', which is neither an input column nor a whitelisted function."),
            $"Step '{stepName}': metric '{m.Name}' expression");

    private static string DeriveExpressionSql(
        string stepName, DeriveColumn d, Dictionary<string, string> available) =>
        ClientExpression.ToSql(
            d.Expr, ExpressionContext.Derive,
            name => available.TryGetValue(name, out var column)
                ? $"`{column}`"
                : throw Invalid($"Step '{stepName}': derive '{d.Alias}' references '{name}', which is " +
                                "neither an input column nor a whitelisted function."),
            $"Step '{stepName}': derive '{d.Alias}'");
```

`Build`'s `EmitStep` call, `:512-514`, replace:
```csharp
            EmitStep(
                sb, step, input, emitted, registry, param, stepIdx: i + 1,
                outputAliases: byName[step.Name].Columns, authz: authz, tenantDatabase: tenantDatabase);
```
with:
```csharp
            EmitStep(
                sb, step, input, emitted, registry, param, stepIdx: i + 1,
                outputAliases: byName[step.Name].Columns, deriveSql: byName[step.Name].DeriveSql,
                authz: authz, tenantDatabase: tenantDatabase);
```

`EmitStep`'s signature, `:545`, replace:
```csharp
        IReadOnlyDictionary<string, string> outputAliases,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null,
```
with:
```csharp
        IReadOnlyDictionary<string, string> outputAliases,
        IReadOnlyList<string> deriveSql,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null,
```

`:575`, replace:
```csharp
            var metricExprs = step.Metrics.Select(m => EmitMetric(m, input.Columns));
```
with:
```csharp
            var metricExprs = step.Metrics.Select(m => EmitMetric(step.Name, m, input.Columns));
```

`:622-623`, replace:
```csharp
        foreach (var d in step.Derive)
            selectParts.Add($"({d.Expr}) AS `{d.Alias}`");
```
with:
```csharp
        // The parser's SQL from TrackAndValidate, never d.Expr itself.
        for (var k = 0; k < step.Derive.Count; k++)
            selectParts.Add($"({deriveSql[k]}) AS `{step.Derive[k].Alias}`");
```

`EmitMetric`, `:682` and `:699-707` at `1b7968aa` (`:689` and `:706-714` after Task 1's +7 lines). Replace the signature line:
```csharp
    private static string EmitMetric(MetricSpec m, Dictionary<string, string> input)
```
with:
```csharp
    private static string EmitMetric(string stepName, MetricSpec m, Dictionary<string, string> input)
```
Then replace:
```csharp
        // m.Expression is a client-settable field on the public PipelineStep.metrics proto
        // contract (reuses MetricSpec, same as GroupByRequest.metrics) — it IS reachable by any
        // caller with read access to this type via the Pipeline RPC. Validated during step
        // processing (ValidateStepAndComputeOutput, called from TrackAndValidate — always run
        // before Build reaches this method) via RejectForbiddenCharacters + the
        // TokenRx/IsNonColumnToken identifier allow-list; do not weaken either check based on an
        // assumption that this field is trusted or server-only.
        var arg = !string.IsNullOrEmpty(m.Expression) ? m.Expression : $"`{input[m.Field]}`";
```
with:
```csharp
        // m.Expression is a client-settable field on the public PipelineStep.metrics proto
        // contract (reuses MetricSpec, same as GroupByRequest.metrics) — it IS reachable by any
        // caller with read access to this type via the Pipeline RPC. Only its parsed, re-emitted
        // form reaches the SQL; never splice m.Expression itself.
        var arg = !string.IsNullOrEmpty(m.Expression) ? MetricExpressionSql(stepName, m, input) : $"`{input[m.Field]}`";
```

Keep `using System.Text.RegularExpressions;` (`:2`). `IdentifierRx` still uses it.

**5f. `StarRocksQueryBuilder.cs`:**

Usings, `:1-5`, replace:
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Iverson.Client.Contracts;
```
with:
```csharp
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using Iverson.Client.Contracts;
using Iverson.StarRocks.Expressions;
```
(`Match` was the file's only Regex use.)

`BuildAggregate`, `:248-256`, replace:
```csharp
        if (!string.IsNullOrEmpty(spec.Expression))
        {
            StarRocksPipelineBuilder.RejectForbiddenCharacters(spec.Expression, $"Aggregation '{spec.Name}' expression");
            foreach (Match m in StarRocksPipelineBuilder.TokenRx.Matches(spec.Expression))
            {
                if (StarRocksPipelineBuilder.IsNonColumnToken(spec.Expression, m)) continue;
                CheckFieldAllowed(m.Value);
            }
        }
```
with:
```csharp
        // spec.Expression is parsed, and its SQL is written from the parse tree: every column goes
        // through CheckFieldAllowed and comes back quoted. It is parsed for every kind, although
        // only Avg/Sum/Min/Max/Count emit it, so a disallowed reference is rejected whichever kind
        // carries it.
        string? expressionSql = null;
        if (!string.IsNullOrEmpty(spec.Expression))
        {
            expressionSql = ClientExpression.ToSql(
                spec.Expression, ExpressionContext.AggregateArgument,
                field =>
                {
                    CheckFieldAllowed(field);
                    return Quote(ResolveStrict(field)!);
                },
                $"Aggregation '{spec.Name}' expression");
        }
```

`:333-345`, replace:
```csharp
            // spec.Expression is a client-settable field on the public AggregateRequest proto
            // contract (object_search.proto's AggregationSpec.expression) — it IS reachable by
            // any caller with read access to this type via the Aggregate RPC. Validated above via
            // RejectForbiddenCharacters + the TokenRx/IsNonColumnToken identifier allow-list before
            // being spliced into the aggregate function; do not weaken either check based on an
            // assumption that this field is trusted or server-only.
            AggregationKind.Avg   => $"SELECT AVG({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Sum   => $"SELECT SUM({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Min   => $"SELECT MIN({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Max   => $"SELECT MAX({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Count => isCountAll
                ? $"SELECT COUNT(*) AS metric_val {from}{wc}{hc}"
                : $"SELECT COUNT(DISTINCT {spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
```
with:
```csharp
            // spec.Expression is a client-settable field on the public AggregateRequest proto
            // contract (object_search.proto's AggregationSpec.expression) — it IS reachable by
            // any caller with read access to this type via the Aggregate RPC. Only its parsed,
            // re-emitted form (expressionSql, above) reaches the SQL; never splice spec.Expression.
            AggregationKind.Avg   => $"SELECT AVG({expressionSql ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Sum   => $"SELECT SUM({expressionSql ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Min   => $"SELECT MIN({expressionSql ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Max   => $"SELECT MAX({expressionSql ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Count => isCountAll
                ? $"SELECT COUNT(*) AS metric_val {from}{wc}{hc}"
                : $"SELECT COUNT(DISTINCT {expressionSql ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
```

`BuildMetricExpr`'s header comment, `:472-478`, replace:
```csharp
    // metric.expression is a client-settable field on the public GroupByRequest proto contract
    // (object_search.proto's MetricSpec.expression) — it IS reachable by any caller with read
    // access to this type via the GroupBy RPC. Validated below via RejectForbiddenCharacters +
    // the TokenRx/IsNonColumnToken identifier allow-list, and subject to the same field
    // reject-on-reference check as metric.Field (see remarks ahead of that check for why the
    // two are validated independently); do not weaken any of these based on an assumption that
    // this field is trusted or server-only.
```
with:
```csharp
    // metric.expression is a client-settable field on the public GroupByRequest proto contract
    // (object_search.proto's MetricSpec.expression) — it IS reachable by any caller with read
    // access to this type via the GroupBy RPC. It is parsed by ClientExpression and its SQL is
    // written from the parse tree, each column resolved and field-checked like metric.Field (see
    // remarks ahead of that check for why the two are validated independently). Never splice
    // metric.Expression itself.
```

`:526-542`, replace:
```csharp
        if (!string.IsNullOrEmpty(metric.Expression))
        {
            StarRocksPipelineBuilder.RejectForbiddenCharacters(metric.Expression, $"Metric '{metric.Name}' expression");
            foreach (Match m in StarRocksPipelineBuilder.TokenRx.Matches(metric.Expression))
            {
                if (StarRocksPipelineBuilder.IsNonColumnToken(metric.Expression, m)) continue;
                var resolvedToken = ResolveColumn(tableMap, m.Value)
                    ?? throw new EngagementQueryTranslationException(
                        $"Metric '{metric.Name}' expression references unknown field '{m.Value}'.");
                if (!IsFieldAllowed(resolvedToken, schema, tableMap, authz, out var typeName))
                    throw new EngagementQueryTranslationException(
                        $"Field '{m.Value}' on '{typeName}' referenced by metric '{metric.Name}' expression is not authorized for this caller.");
            }
        }

        if (!string.IsNullOrEmpty(metric.Expression))
            return $"{fn}({metric.Expression}) AS {quotedName}";
```
with:
```csharp
        if (!string.IsNullOrEmpty(metric.Expression))
        {
            var expressionSql = ClientExpression.ToSql(
                metric.Expression, ExpressionContext.AggregateArgument,
                field =>
                {
                    var resolvedToken = ResolveColumn(tableMap, field)
                        ?? throw new EngagementQueryTranslationException(
                            $"Metric '{metric.Name}' expression references unknown field '{field}'.");
                    if (!IsFieldAllowed(resolvedToken, schema, tableMap, authz, out var typeName))
                        throw new EngagementQueryTranslationException(
                            $"Field '{field}' on '{typeName}' referenced by metric '{metric.Name}' expression is not authorized for this caller.");
                    return QuoteQualified(resolvedToken);
                },
                $"Metric '{metric.Name}' expression");
            return $"{fn}({expressionSql}) AS {quotedName}";
        }
```

Range bounds side-fix, `:998-1004`, replace:
```csharp
        var cases = buckets.Select((b, i) =>
        {
            var when =
                b.From is null && b.To is not null     ? $"WHEN {quotedCol} < {b.To.Value}"
                : b.From is not null && b.To is null    ? $"WHEN {quotedCol} >= {b.From.Value}"
                : b.From is not null && b.To is not null ? $"WHEN {quotedCol} >= {b.From.Value} AND {quotedCol} < {b.To.Value}"
                : null;
```
with:
```csharp
        var cases = buckets.Select((b, i) =>
        {
            // lower/upper, not from/to: BuildRangeSql's own `from` parameter is the FROM clause.
            var lower = b.From is { } f ? RangeBound(f, i) : null;
            var upper = b.To is { } t ? RangeBound(t, i) : null;
            var when =
                lower is null && upper is not null     ? $"WHEN {quotedCol} < {upper}"
                : lower is not null && upper is null    ? $"WHEN {quotedCol} >= {lower}"
                : lower is not null && upper is not null ? $"WHEN {quotedCol} >= {lower} AND {quotedCol} < {upper}"
                : null;
```
Add this method directly after `BuildRangeSql`'s closing brace:
```csharp
    // A range bound is spliced as a numeric literal, so it is printed with the invariant culture
    // (a decimal comma would split it into two tokens) and must be finite: NaN and ±Infinity have
    // no literal form and would come out as bare words.
    private static string RangeBound(double value, int bucketIndex) =>
        double.IsFinite(value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : throw new EngagementQueryTranslationException(
                $"Range bucket {bucketIndex} has a non-finite bound; bounds must be finite numbers.");
```

Afterwards, verify the deletions:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && command grep -rn --include=*.cs "RejectForbiddenCharacters\|TokenRx\|IsNonColumnToken\|DeriveFunctions\|DeriveKeywords\|ValidateDeriveExpr" Iverson.Server | grep -v "/obj/\|/bin/"
```
Expected: no output.

- [ ] **Step 6: Run the tests and see them pass**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~ClientExpressionTests|FullyQualifiedName~StarRocksPipelineBuilderTests|FullyQualifiedName~StarRocksQueryBuilderTests"
```
Expected: 0 failures. Every test named in Step 2 now passes, and so does every `ClientExpressionTests` case. Then run the whole non-container suite:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures.

Live check of canonical derive SQL on the engine (**heavy: Testcontainers StarRocks. Run it alone.**):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~PipelineIntegrationTests"
```
Expected: 7 passed. `DerivedRatio_PercentOfTotal` now runs `(100.0 * `n` / SUM(`n`) OVER ()) AS `pct`` and still yields 66.67 and 33.33.

- [ ] **Step 7: Mutation proof**

Make each mutation by hand, run the Step 6 unit command, record which tests fail, then undo it by hand. Each must make at least the named test fail.

| # | Guard | One-line mutation | Must fail |
|---|---|---|---|
| E1 | Length cap first | `ClientExpression.ToSql`: `if (text.Length > MaxLength)` → `if (text.Length > MaxLength + 1000)` | `LengthCap_RunsBeforeParsing`, `LengthCap_RefusesAValidIdentifierOverTheCap`, PBT `Build_DeriveExprOverLengthLimit_Throws` |
| E2 | Comment openers | Lexer: `if ((c == '-' && next == '-') \|\| (c == '/' && next == '*'))` → `if (false)` | `Lexer_RefusesCharactersOutsideTheGrammar` (`--`, `/*` cases), PBT `Build_DeriveExprWithSqlCommentSequence_Throws` |
| E3 | Character refusal | Lexer switch: add the arm `'<' => SqlTokenKind.Minus,` | `Lexer_RefusesCharactersOutsideTheGrammar` (`Amount < 0`) |
| E4 | ASCII-only whitespace | Lexer: `if (c is ' ' or '\t' or '\r' or '\n')` → `if (char.IsWhiteSpace(c))` | `Lexer_RefusesCharactersOutsideTheGrammar` (U+00A0 case) |
| E5 | ASCII-only identifiers | Lexer: `if (char.IsAsciiLetter(c) \|\| c == '_')` → `if (char.IsLetter(c) \|\| c == '_')` | `Lexer_RefusesCharactersOutsideTheGrammar` (U+00C9 case) |
| E6 | Numeric overflow refused | Lexer `ReadNumber`: replace the `if (!decimal.TryParse(…))` line with `var value = decimal.Parse(spelling, CultureInfo.InvariantCulture); if (false)` | `Lexer_RefusesANumberThatDoesNotFit_WithoutAnOverflowException` (an `OverflowException` escapes, and `Assert.Throws` checks the exact type) |
| E7 | Reserved words | Parser `ReservedWords`: delete `"ORDER", ` | `Parser_RefusesMalformedExpressions` (`Order * 2`), `Derive_WindowClauseTakesOnlyColumns` (`PARTITION BY Order`) |
| E8 | Function allow-list | Parser `ScalarFunctions`: add `"LENGTH"` | `Parser_RefusesMalformedExpressions` (`LENGTH(Amount)`) |
| E9 | No aggregate in aggregate-argument context | Parser: `if (isAggregate && context == ExpressionContext.AggregateArgument)` → `if (false)` | `AggregateArgument_RefusesAggregatesAndTheirWindows` (the message becomes "requires an OVER clause"), `Derive_RefusesAnAggregateInsideAnAggregatesArgument` |
| E10 | Aggregate requires OVER | Parser `ParseCall`: change `if (!IsWord("OVER"))` to `if (false)`, and change `over = ParseOver();` to `over = IsWord("OVER") ? ParseOver() : null;` | `Derive_RefusesAnAggregateWithoutOver`, PBT `Validate_DeriveCallsAggregateWithoutOver_Throws` |
| E11 | An aggregate's argument is an aggregate argument | Parser: `var argumentContext = isAggregate ? ExpressionContext.AggregateArgument : context;` → `var argumentContext = context;` | `Derive_RefusesAnAggregateInsideAnAggregatesArgument` |
| E12 | `*` only for COUNT | Parser: delete `function == "COUNT" && ` from the `countStar` condition | `Derive_AllowsStarOnlyAsCountsSoleArgument` (`SUM(*)`) |
| E13 | Depth cap | Parser: `internal const int MaxDepth = 32;` → `= 64;` | `Parser_RefusesNestingOverTheCap`, `Parser_CountsUnaryMinusTowardTheCap` |
| E14 | Unary counts toward depth | Parser `ParseUnary`: delete the line `Enter();`, and the `_depth--;` two lines below it | `Parser_CountsUnaryMinusTowardTheCap` |
| E15 | `-(-x)` never `--x` | Emitter: `NegateNode negate => "-" + Wrap(negate.Operand, PrimaryPrecedence, resolveQuoted),` → `NegateNode negate => "-" + Emit(negate.Operand, resolveQuoted),` | `Emitter_WritesOperatorsParenthesesAndNumbersFromTheTree` (`- -a`, `-(a + b)`) |
| E16 | Right-operand parentheses | Emitter: `Precedence(binary) + 1` → `Precedence(binary)` | `Emitter_…FromTheTree` (`a - (b - c)`) |
| E17 | Columns from the callback | Emitter: `ColumnNode column => resolveQuoted(column.Name),` → ``ColumnNode column => $"`{column.Name}`",`` | `Emitter_TakesEveryColumnFromTheCallback_…`, `AggregateArgument_ColumnsInArgumentsGoThroughTheCallback`, `BuildAggregate_EmitsTheParsedExpressionWithResolvedColumns`, and the site pinned-message tests |
| E18 | Window columns from the callback | Emitter: `call.Over.PartitionBy.Select(column => resolveQuoted(column.Name))` → ``call.Over.PartitionBy.Select(column => $"`{column.Name}`")`` | `Derive_ColumnsInArgumentsAndWindowsGoThroughTheCallback` (PARTITION BY cases), `PipelineDerive_RestrictedColumnAnywhereInTheWindow_…` |
| E19 | Numbers printed invariantly | Emitter: `number.Value.ToString(CultureInfo.InvariantCulture)` → `number.Value.ToString()` | `Emitter_PrintsNumbersInvariantly_UnderACommaDecimalCulture` |
| E20 | Derive emits parsed SQL | PB `EmitStep`: `selectParts.Add($"({deriveSql[k]}) AS `{step.Derive[k].Alias}`");` → `selectParts.Add($"({step.Derive[k].Expr}) AS `{step.Derive[k].Alias}`");` | PBT `Build_RunningSumAndDerive_EmitOverAndExpression`, `Build_ReadsSkipsIntermediateStep`, `PipelineDerive_EmitsTheParsedExpression…` |
| E21 | Pipeline metric emits parsed SQL | PB `EmitMetric`: `? MetricExpressionSql(stepName, m, input)` → `? m.Expression` | PBT `Build_NormalMetricExpression_StillProducesExpectedSql`, `PipelineMetric_EmitsTheParsedExpression…` |
| E22 | Aggregate emits parsed SQL | QB: the `AggregationKind.Sum` arm `expressionSql ?? Quote(col)` → `spec.Expression ?? Quote(col)` | QBT `BuildAggregate_ExpressionMetric_UsesRawExpressionInAggFn`, `BuildAggregate_EmitsTheParsedExpressionWithResolvedColumns` |
| E23 | GroupBy metric emits parsed SQL | QB: `return $"{fn}({expressionSql}) AS {quotedName}";` → `return $"{fn}({metric.Expression}) AS {quotedName}";` | QBT `BuildGroupBy_SingleTable_ProducesCompoundSelect`, `BuildGroupBy_ExpressionMetric_PassesRawExpression`, `BuildGroupBy_EmitsTheParsedExpressionWithQualifiedColumns` |
| E24 | Non-finite range bounds | QB `RangeBound`: `double.IsFinite(value)` → `true` | `BuildAggregate_RangeBucketWithANonFiniteBound_IsRejected` (3 cases) |
| E25 | Invariant range bounds | QB `RangeBound`: `value.ToString(CultureInfo.InvariantCulture)` → `value.ToString()` | `BuildAggregate_RangeBounds_AreFormattedInvariantly_UnderACommaDecimalCulture` |

Record each mutation's failing test names in the report. Confirm that `git diff` shows only the intended Task 2 changes after the last revert.

- [ ] **Step 8: Commit**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git add Iverson.Server/Iverson.StarRocks/Expressions/ClientExpression.cs Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionLexer.cs Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionParser.cs Iverson.Server/Iverson.StarRocks/Expressions/SqlExpressionEmitter.cs Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.StarRocks.Tests/ClientExpressionTests.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs
git commit -m "parse client expressions and emit sql from the parse tree

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 3: StarRocks credential split, code

**Spec:** §3 (closes #10, second half), §6 "Provisioning" and the integration tests "Under the split"

**Decision: what a null provisioner connection string does.**
- `EngagementRepository` takes a new trailing optional constructor parameter, `string? provisionerConnectionString = null`.
- When it is null, `EnsureTenantProvisionedAsync` throws `InvalidOperationException` before it opens any connection, and before the readiness gate. It never falls back to the query login, which no longer holds `user_admin`.
  - An invalid tenant id still returns early, as today, before this check.
- Why this works:
  - The API role is handed null by `Program.cs`, so it can never provision. No API-role code calls the method (spec Verified assumption 11).
  - Every existing constructor call compiles unchanged, because the parameter is optional and trailing:
    - `StarRocksIntegrationTests.cs:81` (the fixture's root repository);
    - `StarRocksRepositorySearchTests.cs:12`;
    - `StarRocksRepositoryTests.cs:125`;
    - `MatchRowsIntegrationTests.cs:342, 367`;
    - `Iverson.Api.Tests/StarRocks/StarRocksReadinessIntegrationTests.cs:67`.
  - None of those calls provisions. The only test callers of `EnsureTenantProvisionedAsync` on a real repository are `TenantIsolationIntegrationTests` and `MatchRowsIntegrationTests`. This task passes them both strings.

**What triggers `WidenLargeTextColumnsAsync`'s `ALTER`** (`EngagementRepository.cs:755-785`):
- the schema passed in has a column whose `SrType` is `StarRocksLimits.WideTextColumnType` (`"VARCHAR(1048576)"`);
- that column already exists in the table with a smaller width;
- and `CREATE TABLE IF NOT EXISTS` left that table untouched.

The tests below build exactly that: first a table with `Body VARCHAR(65533)`, then a provisioning call whose schema declares `Body` as `VARCHAR(1048576)`.

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`
  - `:15-19`: add the trailing constructor parameter.
  - `:33`: add the provisioner connection factory.
  - After `:108`: add `OpenProvisionerConnectionAsync`.
  - `:464-518`: add the null refusal, the provisioner connection, and the two self-grants.
- Modify: `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs:8-28`: add the optional trailing `provisionerConnectionString` parameter and pass it to the repository.
- Modify: `Iverson.Server/Iverson.Api/Program.cs:267-306`: the role-aware provisioner guard, and the new argument to `AddStarRocks`.
- Modify: `Iverson.Server/Iverson.Api/appsettings.Development.json`: add `StarRocksProvisioner`, following the existing Development `StarRocks` entry (root, empty password), so a local worker run with Engagement on still starts.
- Create: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs`: shared fixture helper that creates both logins with `job-create-user.yaml`'s statements and returns both connection strings.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs:20-72, 323-324`: use the helper and pass both strings.
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs:17-38`: use the helper and pass both strings.
- Test (create): `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryProvisioningTests.cs`: unit tests for the AccessDenied wrap and the null refusal.
- Test (modify): `Iverson.Server/Iverson.StarRocks.Tests/ServiceCollectionExtensionsTests.cs`: `AddStarRocks` hands the provisioner string to the repository.
- Test (create): `Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs`: heavy Testcontainers tests:
  - what `iverson_app` can no longer do;
  - the readiness gate;
  - new-tenant provisioning;
  - re-provisioning a tenant created before the split;
  - widening, for a table created after the grant and for one created before it.

**Interfaces:**
- Produces:
  - the configuration key `ConnectionStrings:StarRocksProvisioner` (env `ConnectionStrings__StarRocksProvisioner`), read only when `workloadRole == "worker"`;
  - the login name `iverson_provisioner`@`'%'`, hard-coded in the two new GRANT statements;
  - `AddStarRocks(..., string? provisionerConnectionString = null)`.
- Consumed by: Task 4 (Helm and compose set the key and create the login) and Task 11 (the throwaway job-script run and the isolated compose live check).
- **Ordering.** This task must land before Task 4 on the branch. Task 4's job revokes `iverson_app`'s `user_admin`, and the pre-split code provisions with it.
- **Conflicts.** `Program.cs` is also edited by the §5e task (the `/v1/traces` handler), in a different region. Anchor these edits on the quoted text, not on line numbers.

- [ ] **Step 1: Write the failing tests**

Before writing anything, record the baseline test counts of the two existing integration classes:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --list-tests 2>/dev/null | command grep -cE 'TenantIsolationIntegrationTests|MatchRowsIntegrationTests'
```

Record the number. `--list-tests` starts no container.

**(a) Create `Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs`.**

The statements are `job-create-user.yaml`'s as Task 4 leaves them, without the root password change and without `ALTER USER … SET PROPERTIES`.

```csharp
using MySqlConnector;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// The two StarRocks logins production uses (CSR round 11 §3), set up through the fixture's root
/// repository with the statements job-create-user.yaml runs. iverson_app runs queries and keeps
/// OPERATE ON SYSTEM as its only direct grant; iverson_provisioner provisions tenants and holds
/// user_admin plus a direct CREATE DATABASE.
///
/// <para><b>Why one helper.</b> The users live in the one container every class in
/// <see cref="StarRocksCollection"/> shares, so they outlive any one class. Every class that needs them
/// calls <see cref="EnsureAsync"/> from its constructor, and the statements end in the same state
/// whatever state they start from: CREATE ... IF NOT EXISTS and a repeated GRANT are no-ops, and a
/// REVOKE of something the user does not hold succeeds silently. The one REVOKE form that can fail, a
/// database-scoped one naming a database that does not exist, comes after CREATE DATABASE IF NOT
/// EXISTS iverson, as in the job.</para>
///
/// <para><b>Why OPERATE ON SYSTEM is direct.</b> StarRocks activates only the roles a session names in
/// SET ROLE, and EngagementRepository's readiness gate runs SHOW BACKENDS before any role is
/// active.</para>
///
/// <para><b>Why the connection strings name no default database.</b> Neither login holds a grant on a
/// database outside the tenant roles, and a connection whose default database the user cannot use
/// fails at the handshake. Every statement either login runs is qualified to its database.</para>
/// </summary>
internal static class StarRocksSplitLogins
{
    private const string AppPassword = "test_pw";
    private const string ProvisionerPassword = "test_provisioner_pw";

    public static async Task<(string App, string Provisioner)> EnsureAsync(StarRocksContainerFixture fx)
    {
        await fx.Repository.ExecuteAsync($"""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '{AppPassword}';
            CREATE USER IF NOT EXISTS 'iverson_provisioner'@'%' IDENTIFIED BY '{ProvisionerPassword}';
            CREATE DATABASE IF NOT EXISTS iverson;
            GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_provisioner'@'%';
            GRANT user_admin TO 'iverson_provisioner'@'%';
            REVOKE SELECT, INSERT, UPDATE, DELETE ON iverson.* FROM 'iverson_app'@'%';
            REVOKE CREATE TABLE, DROP, ALTER ON DATABASE iverson FROM 'iverson_app'@'%';
            REVOKE CREATE DATABASE ON CATALOG default_catalog FROM 'iverson_app'@'%';
            REVOKE user_admin FROM 'iverson_app'@'%';
            """);

        return (ConnectionStringFor(fx, "iverson_app", AppPassword),
                ConnectionStringFor(fx, "iverson_provisioner", ProvisionerPassword));
    }

    private static string ConnectionStringFor(StarRocksContainerFixture fx, string user, string password) =>
        new MySqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = user,
            Password = password,
            Database = ""
        }.ToString();
}
```

**(b) `TenantIsolationIntegrationTests.cs`.** Replace lines 23-72, from `public TenantIsolationIntegrationTests(StarRocksContainerFixture fx)` through `_appRepo = new EngagementRepository(appConnectionString, NullLogger<EngagementRepository>.Instance);` and the constructor's closing brace, with:

```csharp
    public TenantIsolationIntegrationTests(StarRocksContainerFixture fx)
    {
        _rootRepo = fx.Repository;

        // The two production logins (CSR round 11 §3), set up exactly as job-create-user.yaml sets them
        // up. StarRocksSplitLogins explains the grants, why they are safe to re-run in any class order,
        // and why the connection strings name no default database. _appRepo queries as iverson_app and
        // provisions as iverson_provisioner, as the worker does.
        var (appConnectionString, provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();

        _appRepo = new EngagementRepository(appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString);
    }
```

Also replace this, in `SearchAsync_WithAuthzNull_UsesUnscopedSharedDatabase`:

```csharp
        // Uses _rootRepo, not _appRepo: _appRepo's connection string deliberately has no default
        // database (see constructor remarks), so it cannot run the unqualified DDL/DML this
```

with:

```csharp
        // Uses _rootRepo, not _appRepo: _appRepo's connection string deliberately has no default
        // database (see StarRocksSplitLogins), so it cannot run the unqualified DDL/DML this
```

The `using MySqlConnector;` at the top of the file becomes unused; remove it. Its only use was `MySqlConnectionStringBuilder` in the replaced constructor. Grep the file for `MySql` after the edit; if any use remains, keep the using.

**(c) `MatchRowsIntegrationTests.cs`.** Replace lines 22-38. The before text is:

```csharp
    public MatchRowsIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;
        _rootRepo = fx.Repository;
        // Same grants and connection-string shape as TenantIsolationIntegrationTests (see its comments).
        fx.Repository.ExecuteAsync("""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'test_pw';
            GRANT user_admin TO 'iverson_app'@'%';
            GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
            """).GetAwaiter().GetResult();
        _appConnectionString = new MySqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = "iverson_app", Password = "test_pw", Database = ""
        }.ToString();
        _appRepo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance);
    }
```

The after text is:

```csharp
    public MatchRowsIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;
        _rootRepo = fx.Repository;
        // The two production logins, set up by the same helper as TenantIsolationIntegrationTests (see
        // StarRocksSplitLogins). The pooled repositories below reuse the query login only.
        (_appConnectionString, var provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();
        _appRepo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString);
    }
```

Keep `using MySqlConnector;`: the file still uses `MySqlConnectionStringBuilder` at `:338` and `:363`, and `MySqlConnection` at `:348`.

**(d) Create `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryProvisioningTests.cs`.** It follows `EngagementRepositoryLivenessRewrapTests.cs`: a reflection-built `MySqlException`, fake delegates, and a name-keyed `TheoryData`.

```csharp
using System.Reflection;
using FluentAssertions;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 11 §3: tenant provisioning runs under its own login. On the first upgrade the
/// post-upgrade hook creates that login after the upgraded worker is already serving, so a refusal as
/// AccessDenied while opening it surfaces as <see cref="EngagementNotReadyException"/>, which the
/// projection consumers treat as transient. Every other failure keeps its own exception. A repository
/// given no provisioner connection string refuses to provision at all. No server is needed.
/// </summary>
public class EngagementRepositoryProvisioningTests
{
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

    [Fact]
    public async Task AccessDenied_OnOpen_SurfacesAsNotReady()
    {
        var original = CreateMySqlException(MySqlErrorCode.AccessDenied,
            "Access denied for user 'iverson_provisioner'@'10.0.0.1' (using password: YES)");

        var act = () => EngagementRepository.OpenProvisionerConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<EngagementNotReadyException>())
            .Which.InnerException.Should().BeSameAs(original);
    }

    public static TheoryData<string> UnchangedCases() => new(Unchanged.Keys);

    private static readonly Dictionary<string, Func<Exception>> Unchanged = new()
    {
        ["library-transient MySqlException"] = () => CreateMySqlException(MySqlErrorCode.UnableToConnectToHost,
            "Unable to connect to any of the specified MySQL hosts."),
        ["statement-level privilege error"]  = () => CreateMySqlException((MySqlErrorCode)5203,
            "Access denied; you need (at least one of) the CREATE DATABASE privilege(s) on CATALOG default_catalog for this operation."),
        ["parse error"]                      = () => CreateMySqlException(MySqlErrorCode.ParseError,
            "You have an error in your SQL syntax"),
        ["not a MySqlException"]             = () => new InvalidOperationException("boom"),
    };

    [Theory]
    [MemberData(nameof(UnchangedCases))]
    public async Task OtherFailure_OnOpen_IsRethrownUnchanged(string name)
    {
        var original = Unchanged[name]();

        var act = () => EngagementRepository.OpenProvisionerConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(original, name);
    }

    [Fact]
    public async Task Open_Succeeds_DoesNotThrow()
    {
        var opened = false;

        await EngagementRepository.OpenProvisionerConnectionAsync(() => { opened = true; return Task.CompletedTask; });

        opened.Should().BeTrue();
    }

    [Fact]
    public async Task NoProvisionerConnectionString_RefusesProvisioningBeforeConnecting()
    {
        // Port 1 refuses at once. A refusal that came after the readiness gate instead would wait out the
        // gate's 2 s timeout and answer EngagementNotReadyException.
        var repo = new EngagementRepository("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1",
            NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(2) });
        var schema = new EngagementTableSchema("probe", new EngagementColumnSchema("Id", "VARCHAR(36)", false), []);

        var act = () => repo.EnsureTenantProvisionedAsync("t1", schema);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioner*");
    }
}
```

**(e) `ServiceCollectionExtensionsTests.cs`.** Add after `AddStarRocks_WithEngagementDisabled_RegistersDisabledHealthCheck`, before the class's closing brace:

```csharp
    // CSR round 11 §3: the provisioner connection string reaches the repository. Without it,
    // EnsureTenantProvisionedAsync refuses with InvalidOperationException before connecting
    // (EngagementRepositoryProvisioningTests). With it, the call gets past that refusal to the readiness
    // gate, which cannot reach port 1 and gives up after BackendReadyTimeout. Port 1, not ConnString:
    // nothing here may reach a real StarRocks.
    [Fact]
    public async Task AddStarRocks_WithProvisionerConnectionString_HandsItToTheRepository()
    {
        const string unreachable = "Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1";
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<EngagementRepository>>(NullLogger<EngagementRepository>.Instance);
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
```

**(f) Create `Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs`.** These tests are heavy: they share the one StarRocks Testcontainers container.

```csharp
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 11 §3: the StarRocks credential split, against a live engine. The constructor first gives
/// iverson_app the grants that earlier versions of job-create-user.yaml gave it, then applies the current
/// job's statements through <see cref="StarRocksSplitLogins"/>, so every test here sees an upgraded
/// install: the revokes have something to remove. Other classes in the collection call the same helper,
/// so whichever class runs last leaves the logins in the same state.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StarRocksCollection.Name)]
public sealed class StarRocksCredentialSplitIntegrationTests
{
    private readonly StarRocksContainerFixture _fx;
    private readonly string _appConnectionString;
    private readonly EngagementRepository _repo;

    public StarRocksCredentialSplitIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;

        // The grant set job-create-user.yaml issued to iverson_app before the split. The password matches
        // StarRocksSplitLogins'; CREATE USER IF NOT EXISTS leaves an existing user's password alone.
        fx.Repository.ExecuteAsync("""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'test_pw';
            CREATE DATABASE IF NOT EXISTS iverson;
            GRANT SELECT, INSERT, UPDATE, DELETE ON iverson.* TO 'iverson_app'@'%';
            GRANT CREATE TABLE, DROP, ALTER ON DATABASE iverson TO 'iverson_app'@'%';
            GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
            GRANT user_admin TO 'iverson_app'@'%';
            """).GetAwaiter().GetResult();

        (_appConnectionString, var provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();

        _repo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString);
    }

    private static string UniqueTenantId() => "t" + Guid.NewGuid().ToString("N")[..16];
    private static string UniqueTable() => "tbl_" + Guid.NewGuid().ToString("N")[..8];

    private static EngagementTableSchema ProbeSchema(string tableName) =>
        new(tableName, new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Name", "STRING", true)]);

    private static string ProbePayload(string id, string name) =>
        System.Text.Json.JsonSerializer.Serialize(new { Id = id, Name = name });

    private async Task<List<dynamic>> SearchAsTenantAsync(string table, string tenantId) =>
        (await _repo.SearchAsync(new EngagementQuerySchema("Probe", table, "Id", ["Name"]), null, 0, 50,
            authz: new Dictionary<string, AuthorizationConstraint>
            {
                ["Probe"] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId)
            })).ToList();

    /// <summary>Opens a connection as iverson_app and proves the login itself works, so a later refusal is
    /// about the statement, not about connecting.</summary>
    private async Task<MySqlConnection> OpenAppConnectionAsync()
    {
        var conn = new MySqlConnection(_appConnectionString);
        await conn.OpenAsync();
        (await conn.QueryAsync<int>("SELECT 1")).Should().ContainSingle().Which.Should().Be(1);
        return conn;
    }

    /// <summary>
    /// What the query login's own provisioning left behind before the split: the database, the role and
    /// its grants, the role granted to iverson_app, and one table. iverson_provisioner holds none of it.
    /// </summary>
    private async Task CreateTenantAsBeforeTheSplitAsync(string tenantId, EngagementTableSchema schema)
    {
        var db = TenantIdentifier.DatabaseName(tenantId);
        var role = TenantIdentifier.RoleName(tenantId);
        await _fx.Repository.ExecuteAsync($"""
            CREATE DATABASE IF NOT EXISTS `{db}`;
            CREATE ROLE IF NOT EXISTS `{role}`;
            GRANT SELECT, INSERT, UPDATE, DELETE ON `{db}`.* TO ROLE `{role}`;
            GRANT CREATE TABLE ON DATABASE `{db}` TO ROLE `{role}`;
            GRANT `{role}` TO USER 'iverson_app'@'%';
            """);
        await _fx.Repository.ExecuteAsync(
            StarRocksSchemaManager.BuildCreateTableDdl(schema, TenantIdentifier.Qualify(db, schema.TableName)));
    }

    private async Task<long?> ColumnMaxLengthAsync(string tenantId, string table, string column) =>
        (await _fx.Repository.QueryAsync<long?>(
            """
            SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.columns
            WHERE TABLE_SCHEMA = @Db AND TABLE_NAME = @Table AND COLUMN_NAME = @Column
            """,
            new { Db = TenantIdentifier.DatabaseName(tenantId), Table = table, Column = column })).Single();

    // MODIFY COLUMN can run as a StarRocks schema-change job, so the new width may appear shortly after
    // the statement returns.
    private async Task<long?> WaitForColumnMaxLengthAsync(string tenantId, string table, string column, long expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        long? current;
        while ((current = await ColumnMaxLengthAsync(tenantId, table, column)) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromSeconds(2));
        return current;
    }

    private static EngagementTableSchema BodySchema(string table, string bodyType) =>
        new(table, new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Body", bodyType, true)]);

    // ── what the query login can no longer do ───────────────────────────────────────────────────

    [Fact]
    public async Task AppLogin_CannotCreateADatabase()
    {
        await using var conn = await OpenAppConnectionAsync();

        var act = () => conn.ExecuteAsync($"CREATE DATABASE `split_probe_{Guid.NewGuid():N}`");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    [Fact]
    public async Task AppLogin_CannotActivateUserAdmin()
    {
        await using var conn = await OpenAppConnectionAsync();

        var act = () => conn.ExecuteAsync("SET ROLE user_admin");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*is not granted to*");
    }

    [Fact]
    public async Task AppLogin_CannotReadTheSharedDatabase()
    {
        await _fx.Repository.ExecuteAsync(
            StarRocksSchemaManager.BuildCreateTableDdl(ProbeSchema("split_probe"), "`iverson`.`split_probe`"));
        await _fx.Repository.ExecuteAsync(
            "INSERT INTO `iverson`.`split_probe` VALUES ('10000000-0000-0000-0000-000000000001', 'Shared')");
        await using var conn = await OpenAppConnectionAsync();

        var act = () => conn.QueryAsync("SELECT * FROM `iverson`.`split_probe`");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    // ── what it still does ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppLogin_PassesTheReadinessGateAndTheHealthCheck()
    {
        // A fresh repository: its first operation goes through the cold-start readiness gate (SHOW BACKENDS,
        // no role active), which needs the one direct grant iverson_app keeps.
        var repo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(30) });

        (await repo.QueryAsync<int>("SELECT 1")).Should().ContainSingle().Which.Should().Be(1);
        (await new EngagementHealthChecker(_appConnectionString).CheckHealthAsync())
            .Should().Be(EngagementHealthStatus.Healthy);
    }

    // ── what the provisioner does ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Provisioner_ProvisionsANewTenant_ThatTheAppLoginCanUse()
    {
        var tenantId = UniqueTenantId();
        var table = UniqueTable();
        var schema = ProbeSchema(table);

        await _repo.EnsureTenantProvisionedAsync(tenantId, schema);
        await _repo.UpsertAsync(schema, ProbePayload("20000000-0000-0000-0000-000000000001", "New"), tenantId);

        var rows = await SearchAsTenantAsync(table, tenantId);
        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("New");
    }

    [Fact]
    public async Task Provisioner_ReprovisionsATenantCreatedBeforeTheSplit()
    {
        var tenantId = UniqueTenantId();
        var oldTable = UniqueTable();
        await CreateTenantAsBeforeTheSplitAsync(tenantId, ProbeSchema(oldTable));
        await _fx.Repository.ExecuteAsync(
            $"INSERT INTO {TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenantId), oldTable)} " +
            "VALUES ('30000000-0000-0000-0000-000000000001', 'BeforeSplit')");

        // A new type for the existing tenant: provisioning must activate the tenant role, which only the
        // provisioner's own grant of that role to itself makes possible.
        var newTable = UniqueTable();
        await _repo.EnsureTenantProvisionedAsync(tenantId, ProbeSchema(newTable));
        await _repo.UpsertAsync(ProbeSchema(newTable),
            ProbePayload("30000000-0000-0000-0000-000000000002", "AfterSplit"), tenantId);

        var oldRows = await SearchAsTenantAsync(oldTable, tenantId);
        var newRows = await SearchAsTenantAsync(newTable, tenantId);
        oldRows.Should().ContainSingle();
        ((string)oldRows[0].Name).Should().Be("BeforeSplit");
        newRows.Should().ContainSingle();
        ((string)newRows[0].Name).Should().Be("AfterSplit");
    }

    [Fact]
    public async Task Provisioner_WidensALargeTextColumn_LeftNarrowByAnEarlierProvisioning()
    {
        var tenantId = UniqueTenantId();
        var table = UniqueTable();

        // The first call creates the table with the narrower column. The second asks for the wide type,
        // which CREATE TABLE IF NOT EXISTS leaves alone, so only the widening step can change it.
        await _repo.EnsureTenantProvisionedAsync(tenantId, BodySchema(table, "VARCHAR(65533)"));
        (await ColumnMaxLengthAsync(tenantId, table, "Body")).Should().Be(StarRocksLimits.StringAliasBytes);

        await _repo.EnsureTenantProvisionedAsync(tenantId, BodySchema(table, StarRocksLimits.WideTextColumnType));

        (await WaitForColumnMaxLengthAsync(tenantId, table, "Body", StarRocksLimits.MaxVarcharBytes))
            .Should().Be(StarRocksLimits.MaxVarcharBytes);
    }

    [Fact]
    public async Task Provisioner_WidensALargeTextColumn_OnATableCreatedBeforeTheSplit()
    {
        // The case widening exists for: a table from an older deployment, which predates the provisioner's
        // ALTER grant.
        var tenantId = UniqueTenantId();
        var table = UniqueTable();
        await CreateTenantAsBeforeTheSplitAsync(tenantId, BodySchema(table, "VARCHAR(65533)"));
        (await ColumnMaxLengthAsync(tenantId, table, "Body")).Should().Be(StarRocksLimits.StringAliasBytes);

        await _repo.EnsureTenantProvisionedAsync(tenantId, BodySchema(table, StarRocksLimits.WideTextColumnType));

        (await WaitForColumnMaxLengthAsync(tenantId, table, "Body", StarRocksLimits.MaxVarcharBytes))
            .Should().Be(StarRocksLimits.MaxVarcharBytes);
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj 2>&1 | command grep -E 'error CS' | sort -u
```

Expected: the build fails. This is the red state; there is nothing to run yet.
- `CS1739`: the best overload for `EngagementRepository` / `AddStarRocks` has no parameter named `provisionerConnectionString`. This hits `StarRocksSplitLogins`' callers in all three integration classes, and `ServiceCollectionExtensionsTests`.
- `CS0117`: `EngagementRepository` does not contain a definition for `OpenProvisionerConnectionAsync`.
- `CS1061`: `FunctionAssertions<?>` has no `ThrowAsync`, at the two `act.Should().ThrowAsync` lines in `EngagementRepositoryProvisioningTests` that call `OpenProvisionerConnectionAsync`. It cascades from the CS0117 and disappears once Step 3 adds the method.

No other error should appear. Any other error means a test file is wrong; fix the test, not the expectation.

- [ ] **Step 3: Implement**

**(a) `EngagementRepository.cs`, constructor (`:15-19`).** Before:

```csharp
public sealed class EngagementRepository(
    string connectionString,
    ILogger<EngagementRepository> logger,
    EngagementResilienceOptions? resilienceOptions = null,
    EngagementQueryLimitOptions? queryLimitOptions = null)
```

After:

```csharp
public sealed class EngagementRepository(
    string connectionString,
    ILogger<EngagementRepository> logger,
    EngagementResilienceOptions? resilienceOptions = null,
    EngagementQueryLimitOptions? queryLimitOptions = null,
    string? provisionerConnectionString = null)
```

**(b) Connection factory (`:33`).** Before:

```csharp
    private MySqlConnection CreateConnection() => new(connectionString);
```

After:

```csharp
    private MySqlConnection CreateConnection() => new(connectionString);

    // CSR round 11 §3: tenant provisioning's own login, which only the worker is given. Used by
    // EnsureTenantProvisionedAsync and nothing else; every query runs on CreateConnection's login.
    private MySqlConnection CreateProvisionerConnection() => new(provisionerConnectionString);
```

**(c) Insert after `RewrapIfBackendUnavailableAsync`.** Put it after that method's closing brace (`:108`), before `public async Task<IEnumerable<T>> QueryAsync<T>`:

```csharp
    /// <summary>
    /// Opens the provisioning connection (CSR round 11 §3). On a cluster's first upgrade to the split
    /// logins, the post-upgrade hook that creates <c>iverson_provisioner</c> runs after the upgraded worker
    /// is already serving, so StarRocks refuses the login as AccessDenied until it does. That refusal
    /// becomes an <see cref="EngagementNotReadyException"/>, which the projection consumers treat as
    /// transient: they halt and redeliver instead of dead-lettering. The readiness checker treats the
    /// same not-yet-created case the same way (<see cref="EngagementHealthChecker"/>). AccessDenied is
    /// wire-level ambiguous, so a wrong password stalls the consumer the same way (spec, Known issues).
    /// Every other failure propagates unchanged.
    /// </summary>
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

**(d) `EnsureTenantProvisionedAsync` (`:464-518`).** Before:

```csharp
    public async Task EnsureTenantProvisionedAsync(string tenantId, EngagementTableSchema schema)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;

        var dbName   = TenantIdentifier.DatabaseName(tenantId);
```

After:

```csharp
    public async Task EnsureTenantProvisionedAsync(string tenantId, EngagementTableSchema schema)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;

        // CSR round 11 §3: provisioning runs only under the provisioner login. An instance built without
        // one (the api role, which never provisions) refuses outright rather than falling back to the
        // query login, which holds no user_admin and no CREATE DATABASE.
        if (provisionerConnectionString is null)
            throw new InvalidOperationException(
                "Tenant provisioning needs the StarRocks provisioner connection string, and none was configured.");

        var dbName   = TenantIdentifier.DatabaseName(tenantId);
```

Then, inside the same method, before:

```csharp
        await RunAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("SET ROLE user_admin");
            try
            {
                await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{dbName}`");
                await conn.ExecuteAsync($"CREATE ROLE IF NOT EXISTS `{roleName}`");
                await conn.ExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON `{dbName}`.* TO ROLE `{roleName}`");
                await conn.ExecuteAsync($"GRANT CREATE TABLE ON DATABASE `{dbName}` TO ROLE `{roleName}`");
                await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_app'@'%'");
```

After:

```csharp
        await RunAsync(async () =>
        {
            await using var conn = CreateProvisionerConnection();
            await OpenProvisionerConnectionAsync(() => conn.OpenAsync());
            await conn.ExecuteAsync("SET ROLE user_admin");
            try
            {
                await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{dbName}`");
                await conn.ExecuteAsync($"CREATE ROLE IF NOT EXISTS `{roleName}`");
                await conn.ExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON `{dbName}`.* TO ROLE `{roleName}`");
                await conn.ExecuteAsync($"GRANT CREATE TABLE ON DATABASE `{dbName}` TO ROLE `{roleName}`");
                await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_app'@'%'");

                // CSR round 11 §3: the provisioner grants itself what the next steps need. Re-granting either
                // is a no-op.
                // - The tenant role, so `SET ROLE user_admin, <role>` below also works for a tenant whose role
                //   was created before the provisioner login existed.
                // - ALTER on the tenant's tables, for WidenLargeTextColumnsAsync's MODIFY COLUMN. Neither
                //   user_admin nor the tenant role carries ALTER. A direct grant is active whatever role is.
                await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_provisioner'@'%'");
                await conn.ExecuteAsync($"GRANT ALTER ON ALL TABLES IN DATABASE `{dbName}` TO USER 'iverson_provisioner'@'%'");
```

The rest of the method is unchanged: the comment block, `SET ROLE user_admin, …`, `CREATE TABLE`, `WidenLargeTextColumnsAsync`, and the `finally`.

`RunAsync`'s readiness gate and liveness re-check keep using the query login's `connectionString`. Both run `SHOW BACKENDS`, which the provisioner has no grant for.

**(e) `ServiceCollectionExtensions.cs`.** Before:

```csharp
        bool engagementEnabled = true,
        EngagementQueryLimitOptions? queryLimitOptions = null)
```

After:

```csharp
        bool engagementEnabled = true,
        EngagementQueryLimitOptions? queryLimitOptions = null,
        string? provisionerConnectionString = null)
```

Then, before:

```csharp
            return new EngagementRepository(connectionString, logger, resilienceOptions, resolvedQueryLimits);
```

After:

```csharp
            return new EngagementRepository(
                connectionString, logger, resilienceOptions, resolvedQueryLimits, provisionerConnectionString);
```

`EngagementHealthChecker` keeps `connectionString`.

**(f) `Program.cs`.** `workloadRole` is declared at `:37` and `engagementStoreEnabledAtStartup` at `:254`; both precede this block. Before:

```csharp
var starRocksConnectionString = cfg.GetConnectionString("StarRocks");
builder.Services.AddStarRocks(
```

After:

```csharp
var starRocksConnectionString = cfg.GetConnectionString("StarRocks");

// CSR round 11 §3: tenant provisioning runs under its own StarRocks login, and only in the worker's
// engagement consumer (AddEngagementStoreConsumer below). The worker needs that connection string
// whenever engagement is on. The api role never provisions, so it is never handed one, and its
// repository refuses provisioning outright.
var starRocksProvisionerConnectionString =
    workloadRole == "worker" ? cfg.GetConnectionString("StarRocksProvisioner") : null;
if (string.IsNullOrWhiteSpace(starRocksProvisionerConnectionString))
{
    if (workloadRole == "worker" && engagementStoreEnabledAtStartup)
        throw new InvalidOperationException(
            "ConnectionStrings:StarRocksProvisioner is required for the worker role when Engagement:Enabled is true and was not configured.");
    starRocksProvisionerConnectionString = null;
}

builder.Services.AddStarRocks(
```

Then, at the end of the same call, before:

```csharp
        MaxRelationDepth   = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxRelationDepth", EngagementQueryLimitOptions.Default.MaxRelationDepth)
    });

builder.Services.AddQdrant(
```

After:

```csharp
        MaxRelationDepth   = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxRelationDepth", EngagementQueryLimitOptions.Default.MaxRelationDepth)
    },
    provisionerConnectionString: starRocksProvisionerConnectionString);

builder.Services.AddQdrant(
```

The Api.Tests `WebApplicationFactory` hosts never set `WORKLOAD_ROLE` or `WorkloadRole`, so they run as `api` and need no new setting.

**`appsettings.Development.json`.** Add the provisioner string next to the existing `StarRocks` entry. Development already connects to StarRocks as root with an empty password. Replace

```json
    "StarRocks": "Server=localhost;Port=9030;Database=iverson;User Id=root;Password=;"
```

with

```json
    "StarRocks": "Server=localhost;Port=9030;Database=iverson;User Id=root;Password=;",
    "StarRocksProvisioner": "Server=localhost;Port=9030;User Id=root;Password=;"
```

- [ ] **Step 4: Run the tests and see them pass**

Unit tests (light):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~Iverson.StarRocks.Tests.EngagementRepositoryProvisioningTests|FullyQualifiedName~Iverson.StarRocks.Tests.ServiceCollectionExtensionsTests"
```

Expected: 11 passed, 0 failed.
- `EngagementRepositoryProvisioningTests`: 7, which is 1 + 4 theory cases + 1 + 1.
- `ServiceCollectionExtensionsTests`: 4, which is 3 existing + 1.

Every unit test in the project (light; the filter excludes every Testcontainers class):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName!~IntegrationTests"
```

Expected: 0 failed.

The Api build, which picks up the `Program.cs` change and the `StarRocksReadinessIntegrationTests` constructor call (light):

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -3
```

Expected: `0 Error(s)`.

Integration tests. This is a HEAVY run: one StarRocks container, shared by the three classes in one process. Run nothing else alongside it.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~StarRocksCredentialSplitIntegrationTests|FullyQualifiedName~TenantIsolationIntegrationTests|FullyQualifiedName~MatchRowsIntegrationTests"
```

Expected: 0 failed.
- `StarRocksCredentialSplitIntegrationTests`: 8 passed.
- `TenantIsolationIntegrationTests` + `MatchRowsIntegrationTests`: the baseline count from Step 1, all passed. Their provisioning now runs as `iverson_provisioner`, and their queries as an `iverson_app` that holds only `OPERATE ON SYSTEM` plus tenant roles.

Two of the new tests depend on facts that only a live engine can confirm:
- **`Provisioner_WidensALargeTextColumn_OnATableCreatedBeforeTheSplit`.** It depends on `ALL TABLES IN DATABASE` covering a table that existed before the grant. Spec Verified assumption 14 proved coverage only for tables created afterwards.
  - If this one test fails with an `Access denied … ALTER` error while the "earlier provisioning" variant passes, STOP and report it. That is a spec gap. Do not weaken the test.
- **`AppLogin_CannotActivateUserAdmin`.** It pins StarRocks' "is not granted to" wording for a revoked role. The repository's own `IsExpectedMissingResourceError` matches the same wording for tenant roles.
  - If the refusal is worded differently, pin the observed wording. Do not reduce the test to a bare `ThrowAsync<MySqlException>()`, and record the change in the report.

- [ ] **Step 5: Mutation proof**

Before the first mutation, run Step 6's `git add …` command now (not the `git commit`), so that `git checkout -- <file>` restores this task's staged version, new files included. Apply each mutation alone, run the named test, confirm it FAILS, then revert with `git checkout -- <file>` before the next. Record each output line in the report.

| # | File | Mutation | Run (filter) | Must fail |
|---|---|---|---|---|
| M1 | `EngagementRepository.cs` | `when (ex.ErrorCode == MySqlErrorCode.AccessDenied)` → `when (false)` | `~EngagementRepositoryProvisioningTests` (light) | `AccessDenied_OnOpen_SurfacesAsNotReady` |
| M2 | `EngagementRepository.cs` | delete ` when (ex.ErrorCode == MySqlErrorCode.AccessDenied)` (catch every `MySqlException`) | `~EngagementRepositoryProvisioningTests` (light) | `OtherFailure_OnOpen_IsRethrownUnchanged` for the three `MySqlException` cases |
| M3 | `EngagementRepository.cs` | delete the `if (provisionerConnectionString is null) throw …;` statement | `~EngagementRepositoryProvisioningTests` (light) | `NoProvisionerConnectionString_RefusesProvisioningBeforeConnecting` (it gets `EngagementNotReadyException` after ~2 s) |
| M4 | `ServiceCollectionExtensions.cs` | `resolvedQueryLimits, provisionerConnectionString)` → `resolvedQueryLimits)` | `~Iverson.StarRocks.Tests.ServiceCollectionExtensionsTests` (light) | `AddStarRocks_WithProvisionerConnectionString_HandsItToTheRepository` |
| M5 | `EngagementRepository.cs` | delete the line ``await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_provisioner'@'%'");`` | `~StarRocksCredentialSplitIntegrationTests` (HEAVY) | at least `Provisioner_ReprovisionsATenantCreatedBeforeTheSplit` and `Provisioner_WidensALargeTextColumn_OnATableCreatedBeforeTheSplit` (`SET ROLE user_admin, <role>` refused). The new-tenant provisioning tests may fail too: the live probe always issued the self-grant, so whether StarRocks lets a role's creator activate it without one is unverified. Record which tests fail |
| M6 | `EngagementRepository.cs` | delete the `GRANT ALTER ON ALL TABLES IN DATABASE …` line | `~StarRocksCredentialSplitIntegrationTests` (HEAVY) | both `Provisioner_WidensALargeTextColumn_*` tests (`ALTER` refused) |
| M7 | `StarRocksSplitLogins.cs` | delete the three REVOKE lines `ON iverson.*`, `ON CATALOG default_catalog` and `REVOKE user_admin` (test-validity proof that the tests see the revokes) | `~StarRocksCredentialSplitIntegrationTests` (HEAVY) | `AppLogin_CannotCreateADatabase`, `AppLogin_CannotActivateUserAdmin`, `AppLogin_CannotReadTheSharedDatabase` |

Command shape, with `<filter>` from the table:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName<filter>" 2>&1 | command grep -E '^\s+Failed |Passed!|Failed!'
```

Run the HEAVY mutations one at a time. After the last revert, check that the source is clean:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git diff HEAD --stat
```

Expected: only this task's files are listed, and the diff matches Step 3. (Plain `git diff --stat` prints nothing here, because the task's files are staged.) There is no `Program.cs` guard test: no test hosts the worker role, and Task 11's isolated compose run boots the worker with compose's provisioner connection string (a dev-only literal; compose has no Secret).

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git add \
  Iverson.Server/Iverson.StarRocks/EngagementRepository.cs \
  Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs \
  Iverson.Server/Iverson.Api/Program.cs \
  Iverson.Server/Iverson.Api/appsettings.Development.json \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs \
  Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryProvisioningTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/ServiceCollectionExtensionsTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs
git commit -m "provision starrocks tenants under a separate provisioner login

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: StarRocks credential split, deploy

**Spec:** §3 "Helm", "Compose" and "Default database", §6 "Deployment / Render checks" (closes #10, second half, deploy side)

**Files:**
- Create: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-provisioner.yaml`: the `<rel>-starrocks-provisioner` Secret, lookup-or-generate with `keep`.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml`
  - `:39-43`: the `PROVISIONER_PW` env.
  - `:70-76`: create the provisioner, and revoke the app login's direct grants.
  - `:79-93`: rewrite the comment, removing the stale `EnsureDatabaseAsync` paragraph.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml:100`: drop `Database=iverson;`.
- Modify: `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml:84-85`: drop `Database=iverson;`, and add the provisioner password env and `ConnectionStrings__StarRocksProvisioner`.
- Modify: `Iverson.Server/docker-compose.yml`
  - `:82-89`: the comment.
  - `:103-109`: the `starrocks-init` statements.
  - `:510-514`: the api connection string and its comment.
  - `:628-632`: the worker connection string and its comment, plus the provisioner connection string.
- Test (scratch, not committed): `$SCR/check_t4.py`, which asserts the rendered chart per overlay, and a job-versus-compose statement parity check.

**Interfaces:**
- Consumes from Task 3:
  - the config key `ConnectionStrings__StarRocksProvisioner`;
  - the login name `iverson_provisioner`, which Task 3's code grants roles to;
  - the code no longer needing `Database=iverson`, `user_admin` or `CREATE DATABASE` on `iverson_app`.
- Produces for Task 11 (the throwaway job-script run and the isolated compose live check):
  - the job script rendered from the local profile, reading `PROVISIONER_PW`;
  - the job's post-split grant set.
- **Ordering.** Task 3 first: this task's job strips `iverson_app`'s `user_admin`.

Every bash block below starts with these variables, because no shell state carries between blocks:

```bash
WT=/home/ben/repositories/Iverson/.worktrees/csr11-server-authz
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd
```

Rules for these steps:
- **Helm.** Run `helm dependency build` in the chart directory before every `helm template` or `helm lint`: a stale `charts/*.tgz` renders old subchart content.
- **Cloud overlays.** Render each one with its `*.ci-override.yaml`, as `.github/workflows/deploy-validate.yml` and the round-10 plans do.
- **No live cluster or stack.** Do not start, stop or modify any cluster or compose stack in this task; Task 11 runs the rendered job script against a throwaway StarRocks, and the isolated compose live check. `docker compose … config` below only parses the file.

- [ ] **Step 1: Write the failing checks**

`mkdir -p $SCR`, then create `$SCR/check_t4.py` with the Write tool, not a heredoc. Content:

```python
"""CSR round 11 §3 render checks: python3 check_t4.py <overlay> < render.yaml"""
import sys, yaml
name = sys.argv[1]
docs = [d for d in yaml.safe_load_all(sys.stdin) if d]
by = {(d["kind"], d["metadata"]["name"]): d for d in docs}
fails = []
def check(cond, msg):
    if not cond: fails.append(msg)

def env_of(kind, n):
    d = by.get((kind, n))
    if d is None: return None
    spec = d["spec"]["template"]["spec"]
    return [e for c in spec.get("containers", []) + spec.get("initContainers", []) for e in c.get("env", [])]

api, worker = env_of("Deployment", "iverson-api"), env_of("Deployment", "iverson-worker")
check(api is not None and worker is not None, "api or worker Deployment missing")
api, worker = api or [], worker or []
val = lambda env, k: next((e.get("value") for e in env if e["name"] == k), None)
ref = lambda env, k: next((e.get("valueFrom", {}).get("secretKeyRef") for e in env if e["name"] == k), None)

# Every overlay: no StarRocks connection string names a default database, and the api never sees the provisioner.
for role, env in (("api", api), ("worker", worker)):
    cs = val(env, "ConnectionStrings__StarRocks")
    check(cs is None or "Database=" not in cs, f"{role} ConnectionStrings__StarRocks still sets Database=: {cs}")
check(all("provisioner" not in str(e).lower() for e in api), "api env mentions the provisioner")

engagement = name != "values-laptop"
sec = by.get(("Secret", "iverson-starrocks-provisioner"))
job = by.get(("Job", "iverson-starrocks-create-user"))
wcs = val(worker, "ConnectionStrings__StarRocksProvisioner")
if not engagement:
    check(sec is None and job is None and wcs is None, "laptop renders StarRocks provisioner resources")
else:
    check(sec is not None, "provisioner Secret missing")
    if sec:
        check((sec["metadata"].get("annotations") or {}).get("helm.sh/resource-policy") == "keep", "provisioner Secret lacks keep")
        check(bool(sec.get("data", {}).get("password")), "provisioner Secret password empty")
    check(ref(worker, "STARROCKS_PROVISIONER_PASSWORD") == {"name": "iverson-starrocks-provisioner", "key": "password"},
          "worker STARROCKS_PROVISIONER_PASSWORD secretKeyRef wrong")
    check(wcs == "Server=iverson-starrocks-fe-service;Port=9030;User Id=iverson_provisioner;Password=$(STARROCKS_PROVISIONER_PASSWORD);",
          f"worker ConnectionStrings__StarRocksProvisioner wrong: {wcs}")
    check(job is not None, "create-user Job missing")
    if job:
        c = job["spec"]["template"]["spec"]["containers"][0]
        jenv = c.get("env", [])
        check(ref(jenv, "PROVISIONER_PW") == {"name": "iverson-starrocks-provisioner", "key": "password"},
              "Job PROVISIONER_PW secretKeyRef wrong")
        script = "\n".join(c["args"])
        lines = [l.strip() for l in script.splitlines()]
        want = [
            "CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '$APP_PW';",
            "CREATE USER IF NOT EXISTS 'iverson_provisioner'@'%' IDENTIFIED BY '$PROVISIONER_PW';",
            "CREATE DATABASE IF NOT EXISTS iverson;",
            "GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';",
            "GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_provisioner'@'%';",
            "GRANT user_admin TO 'iverson_provisioner'@'%';",
            "REVOKE SELECT, INSERT, UPDATE, DELETE ON iverson.* FROM 'iverson_app'@'%';",
            "REVOKE CREATE TABLE, DROP, ALTER ON DATABASE iverson FROM 'iverson_app'@'%';",
            "REVOKE CREATE DATABASE ON CATALOG default_catalog FROM 'iverson_app'@'%';",
            "REVOKE user_admin FROM 'iverson_app'@'%';",
        ]
        idx = [lines.index(w) if w in lines else -1 for w in want]
        check(-1 not in idx, f"Job missing statements: {[w for w, i in zip(want, idx) if i < 0]}")
        found = [i for i in idx if i >= 0]
        check(found == sorted(found), "Job statements out of order (CREATE DATABASE iverson must precede the revokes)")
        grants_to_app = [l for l in lines if l.startswith("GRANT") and "TO 'iverson_app'" in l]
        check(grants_to_app == ["GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';"], f"Job grants iverson_app more than OPERATE: {grants_to_app}")
        check("EnsureDatabaseAsync" not in script, "Job still names EnsureDatabaseAsync")
        check(lines[0] == "set -e", "Job script no longer starts with set -e")

print(f"[{name}] {'PASS' if not fails else 'FAIL'}", *fails, sep="\n  ")
sys.exit(bool(fails))
```

The job-versus-compose parity check is inline in Step 2 and Step 4. It compares every `CREATE`/`GRANT`/`REVOKE`/`ALTER USER` line in the job with `starrocks-init`'s, with passwords masked.

- [ ] **Step 2: Run them against the unmodified chart and see them fail**

```bash
WT=/home/ben/repositories/Iverson/.worktrees/csr11-server-authz
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/base-$v.yaml
  kube-score score --output-format ci $SCR/base-$v.yaml 2>/dev/null | command grep '^\[CRITICAL\]' | sort > $SCR/base-$v.ks
  python3 $SCR/check_t4.py $v < $SCR/base-$v.yaml | head -1; done
```

Expected:
- `[values-laptop] PASS`. Engagement is off on laptop, so no StarRocks resources render, before or after.
- `[values-local] FAIL`, `[values-aws] FAIL`, `[values-azure] FAIL` and `[values-gcp] FAIL`. The full output lists `Database=` on both connection strings, the missing Secret and env, the missing job statements, the five grants to `iverson_app`, and `EnsureDatabaseAsync`.

```bash
WT=/home/ben/repositories/Iverson/.worktrees/csr11-server-authz
cd $WT/Iverson.Server && python3 - <<'PY'
import re, yaml
job = open("deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml").read()
comp = yaml.safe_load(open("docker-compose.yml"))["services"]["starrocks-init"]["command"][0]
norm = lambda s: [re.sub(r"IDENTIFIED BY '[^']*'", "IDENTIFIED BY <pw>", l.strip()) for l in s.splitlines()
                  if re.match(r"\s*(CREATE|GRANT|REVOKE|ALTER USER)\b", l)]
j, c = norm(job), norm(comp)
print("job and starrocks-init statements match" if j == c else f"DIFFER\n job={j}\n compose={c}")
PY
command grep -c 'Database=iverson;Uid=iverson_app' docker-compose.yml
```

Expected:
- `job and starrocks-init statements match`. They match today, and the check must still pass after Step 3.
- `2`: compose's two app connection strings still name `Database=iverson`.

- [ ] **Step 3: Implement**

**(a) Create `charts/starrocks/templates/secret-provisioner.yaml`.**

It is a Secret of its own, following round 10's new-Secret precedent (`charts/authentik/templates/secret-service-clients.yaml`: one `lookup` per Secret). A key added to `secret.yaml`'s existing Secret would render empty on upgrade (spec Verified assumption 16). The body copies `secret.yaml`'s lookup shape.

```yaml
{{- /*
CSR round 11 §3: the password of iverson_provisioner, the StarRocks login that only the worker's
tenant provisioning uses. It is a Secret of its own, not a new key in <release>-starrocks-app:
that Secret copies its existing keys when it already exists, so a key added there would render
empty on every upgraded cluster.
*/}}
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-starrocks-provisioner
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-starrocks-provisioner" .Release.Name) }}
{{- if $existing }}
  password: {{ index $existing.data "password" }}
{{- else }}
  password: {{ randAlphaNum 32 | b64enc }}
{{- end }}
```

**(b) `job-create-user.yaml`, env.** Before:

```yaml
            - name: APP_PW
              valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-app, key: app-password } }
```

After:

```yaml
            - name: APP_PW
              valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-app, key: app-password } }
            - name: PROVISIONER_PW
              valueFrom: { secretKeyRef: { name: {{ .Release.Name }}-starrocks-provisioner, key: password } }
```

**(c) `job-create-user.yaml`, statements.** Before:

```
                CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '$APP_PW';
                CREATE DATABASE IF NOT EXISTS iverson;
                GRANT SELECT, INSERT, UPDATE, DELETE ON iverson.* TO 'iverson_app'@'%';
                GRANT CREATE TABLE, DROP, ALTER ON DATABASE iverson TO 'iverson_app'@'%';
                GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
                GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
                GRANT user_admin TO 'iverson_app'@'%';
                ALTER USER 'iverson_app' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296');
```

After:

```
                CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '$APP_PW';
                CREATE USER IF NOT EXISTS 'iverson_provisioner'@'%' IDENTIFIED BY '$PROVISIONER_PW';
                CREATE DATABASE IF NOT EXISTS iverson;
                GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
                GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_provisioner'@'%';
                GRANT user_admin TO 'iverson_provisioner'@'%';
                REVOKE SELECT, INSERT, UPDATE, DELETE ON iverson.* FROM 'iverson_app'@'%';
                REVOKE CREATE TABLE, DROP, ALTER ON DATABASE iverson FROM 'iverson_app'@'%';
                REVOKE CREATE DATABASE ON CATALOG default_catalog FROM 'iverson_app'@'%';
                REVOKE user_admin FROM 'iverson_app'@'%';
                ALTER USER 'iverson_app' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296');
```

**Re-run safety under `set -e`** (spec Verified assumption 14):
- `CREATE … IF NOT EXISTS` and a repeated `GRANT` are silent.
- A `REVOKE` of a privilege or role the user no longer holds, or never held, exits 0. That covers every upgrade after the first, and every fresh install.
- The only failing `REVOKE` form is a database-scoped one on a missing database, and `CREATE DATABASE IF NOT EXISTS iverson` precedes it.
- The `mysql -e` batch stops at its first error, so ordering matters. The checker pins it.

**(d) `job-create-user.yaml`, comment.** Replace the comment lines from `# StarRocks' GRANT dialect isn't MySQL's: …` through `# database this job already created was right there.`. That is the 15 lines at `:79-93`, quoted here:

```
              # StarRocks' GRANT dialect isn't MySQL's: DDL privileges (CREATE TABLE/DROP/ALTER)
              # are scoped ON DATABASE, not ON db.*, and CREATE must be written as the two-word
              # "CREATE TABLE" — a bare "CREATE" in the combined list used to fail with "No
              # viable statement", confirmed empirically against a live FE (4.1.1).
              # OPERATE ON SYSTEM is needed for `SHOW BACKENDS`, which
              # EngagementRepository.IsHealthyAsync() runs as this user to back /health —
              # without it every readiness probe fails with "Access denied; you need (at
              # least one of) the OPERATE OR NODE privilege(s) on SYSTEM", confirmed
              # empirically against a live FE (4.1.1).
              # CREATE DATABASE ON CATALOG default_catalog is needed because
              # EngagementRepository.EnsureDatabaseAsync() runs `CREATE DATABASE IF NOT
              # EXISTS iverson` as iverson_app on every RegisterSchema call — StarRocks
              # checks the privilege before checking whether the database already exists,
              # so this failed on literally every schema registration even though the
              # database this job already created was right there.
```

With:

```
              # CSR round 11 §3: two logins. iverson_app runs every query; iverson_provisioner
              # runs only tenant provisioning (EngagementRepository.EnsureTenantProvisionedAsync),
              # and only the worker is given its password.
              # iverson_app keeps one direct grant, OPERATE ON SYSTEM, needed for `SHOW BACKENDS`:
              # /health and every EngagementRepository's readiness gate run it as this user with
              # no role active, and without it they fail with "Access denied; you need (at least
              # one of) the OPERATE OR NODE privilege(s) on SYSTEM", confirmed empirically
              # against a live FE (4.1.1). Its tenant roles are granted by the provisioner.
              # iverson_provisioner holds user_admin and a direct CREATE DATABASE ON CATALOG
              # default_catalog. The catalog privilege must be direct: provisioning runs
              # `CREATE DATABASE IF NOT EXISTS` for each new tenant database, StarRocks checks the
              # privilege before checking whether the database exists, and a role granted to a
              # user is not active until SET ROLE names it.
              # The REVOKEs remove what earlier versions of this job granted iverson_app. A REVOKE
              # of a privilege or role the user does not hold succeeds silently, so they are safe
              # on every run. A database-scoped REVOKE naming a database that does not exist fails
              # and aborts the batch, so `CREATE DATABASE IF NOT EXISTS iverson` stays ahead of
              # them; the database itself is empty and nothing is granted on it.
```

The "StarRocks' GRANT dialect …" paragraph goes, because the job no longer grants DDL ON DATABASE. Keep the `# CSR finding #6 (defense-in-depth): session.query_timeout…` paragraph that follows it unchanged.

**(e) `charts/api/templates/deployment.yaml:100`.** Before:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;Database=iverson;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);"
```

After:

```yaml
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);"
```

**(f) `charts/worker/templates/deployment.yaml:84-85`.** Before:

```yaml
            - name: ConnectionStrings__StarRocks
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;Database=iverson;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);"
            {{- end }}
```

After (everything stays inside the existing `{{- if .Values.global.engagementEnabled }}` block):

```yaml
            - name: ConnectionStrings__StarRocks
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_app;Password=$(STARROCKS_APP_PASSWORD);"
            # CSR round 11 §3: tenant provisioning's own login. Only the worker provisions, so
            # only the worker gets it; the api Deployment has no reference to this Secret.
            - name: STARROCKS_PROVISIONER_PASSWORD
              valueFrom:
                secretKeyRef:
                  name: {{ .Release.Name }}-starrocks-provisioner
                  key: password
            - name: ConnectionStrings__StarRocksProvisioner
              value: "Server={{ .Release.Name }}-starrocks-fe-service;Port=9030;User Id=iverson_provisioner;Password=$(STARROCKS_PROVISIONER_PASSWORD);"
            {{- end }}
```

**(g) `docker-compose.yml`, `starrocks-init` comment (`:82-89`).** Before:

```yaml
  # Every grant here is carried over unweakened from the chart job, including
  # `GRANT CREATE DATABASE ON CATALOG default_catalog`, which the chart's own comment explains is
  # required because EngagementRepository.EnsureTenantProvisionedAsync issues
  # `CREATE DATABASE IF NOT EXISTS` as iverson_app on every tenant's first provisioning, and
  # StarRocks checks the catalog-level privilege before checking whether the database already
  # exists. `GRANT user_admin TO 'iverson_app'@'%'` is likewise required: that same method runs
  # `SET ROLE user_admin` on its connection, and the app's StarRocks connection string below
  # authenticates as iverson_app (matching the chart's api/worker deployments), not root.
```

After:

```yaml
  # Every statement here is carried over unweakened from the chart job, which explains each one
  # (CSR round 11 §3): two logins, iverson_app for queries and iverson_provisioner for tenant
  # provisioning; iverson_app keeps only OPERATE ON SYSTEM as a direct grant, and the REVOKEs
  # strip what earlier versions of this service granted it. A REVOKE of something the user does
  # not hold succeeds silently, but a database-scoped REVOKE on a missing database fails and
  # aborts the batch, so `CREATE DATABASE IF NOT EXISTS iverson` stays ahead of them. Both
  # passwords are dev-only literals: root is passwordless and published on loopback only.
```

The two following lines, `# The trailing ALTER USER ... SET PROPERTIES line …`, stay.

**(h) `docker-compose.yml`, `starrocks-init` statements (`:103-109`).** Before:

```yaml
          CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'dev-only-not-for-production-starrocks-app-pw-0123456789';
          CREATE DATABASE IF NOT EXISTS iverson;
          GRANT SELECT, INSERT, UPDATE, DELETE ON iverson.* TO 'iverson_app'@'%';
          GRANT CREATE TABLE, DROP, ALTER ON DATABASE iverson TO 'iverson_app'@'%';
          GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
          GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
          GRANT user_admin TO 'iverson_app'@'%';
```

After:

```yaml
          CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'dev-only-not-for-production-starrocks-app-pw-0123456789';
          CREATE USER IF NOT EXISTS 'iverson_provisioner'@'%' IDENTIFIED BY 'dev-only-not-for-production-starrocks-provisioner-pw-0123456789';
          CREATE DATABASE IF NOT EXISTS iverson;
          GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
          GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_provisioner'@'%';
          GRANT user_admin TO 'iverson_provisioner'@'%';
          REVOKE SELECT, INSERT, UPDATE, DELETE ON iverson.* FROM 'iverson_app'@'%';
          REVOKE CREATE TABLE, DROP, ALTER ON DATABASE iverson FROM 'iverson_app'@'%';
          REVOKE CREATE DATABASE ON CATALOG default_catalog FROM 'iverson_app'@'%';
          REVOKE user_admin FROM 'iverson_app'@'%';
```

**(i) `docker-compose.yml`, both app connection strings.** These are `:510-514` in `iverson-api` and `:628-632` in `iverson-worker`, and the before text is identical in both:

```yaml
      # Matches the chart's api/worker deployments (deploy/helm/iverson/charts/api/templates/
      # deployment.yaml): connect as iverson_app, the user starrocks-init provisions, not root —
      # EngagementRepository.EnsureTenantProvisionedAsync's `SET ROLE user_admin` depends on
      # authenticating as the user that role was granted to.
      - ConnectionStrings__StarRocks=Server=starrocks;Port=9030;Database=iverson;Uid=iverson_app;Pwd=dev-only-not-for-production-starrocks-app-pw-0123456789;
```

In **`iverson-api`** (the first occurrence) replace it with:

```yaml
      # Matches the chart's api/worker deployments (deploy/helm/iverson/charts/api/templates/
      # deployment.yaml): connect as iverson_app, the query login starrocks-init creates, not root,
      # and with no default database (iverson_app holds no grant on `iverson`).
      - ConnectionStrings__StarRocks=Server=starrocks;Port=9030;Uid=iverson_app;Pwd=dev-only-not-for-production-starrocks-app-pw-0123456789;
```

In **`iverson-worker`** (the second occurrence) replace it with:

```yaml
      # Matches the chart's api/worker deployments (deploy/helm/iverson/charts/api/templates/
      # deployment.yaml): connect as iverson_app, the query login starrocks-init creates, not root,
      # and with no default database (iverson_app holds no grant on `iverson`).
      - ConnectionStrings__StarRocks=Server=starrocks;Port=9030;Uid=iverson_app;Pwd=dev-only-not-for-production-starrocks-app-pw-0123456789;
      # CSR round 11 §3: tenant provisioning's own login, as on the chart's worker. Only the
      # worker provisions, so iverson-api has no provisioner connection string.
      - ConnectionStrings__StarRocksProvisioner=Server=starrocks;Port=9030;Uid=iverson_provisioner;Pwd=dev-only-not-for-production-starrocks-provisioner-pw-0123456789;
```

`scripts/generate-compose-secrets.sh` is unchanged: the provisioner password is a literal, like `iverson_app`'s.

- [ ] **Step 4: Run the checks and see them pass**

```bash
WT=/home/ben/repositories/Iverson/.worktrees/csr11-server-authz
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
for v in values-local values-laptop values-aws values-azure values-gcp; do o="-f $v.yaml"; [ -f $v.ci-override.yaml ] && o="$o -f $v.ci-override.yaml"
  helm template iverson . -n iverson $o > $SCR/new-$v.yaml
  python3 $SCR/check_t4.py $v < $SCR/new-$v.yaml
  kubeconform -kubernetes-version 1.30.0 -summary -ignore-missing-schemas $SCR/new-$v.yaml | tail -1
  kube-score score --output-format ci $SCR/new-$v.yaml 2>/dev/null | command grep '^\[CRITICAL\]' | sort > $SCR/new-$v.ks
  echo "  criticals changed: $(diff $SCR/base-$v.ks $SCR/new-$v.ks | command grep -c '^[<>]')"
  helm lint . $o | tail -1; done
```

Expected, for each of the five overlays:
- `[<overlay>] PASS`;
- kubeconform `Invalid: 0, Errors: 0`;
- `criticals changed: 0`;
- `1 chart(s) linted, 0 chart(s) failed`.

Compose:

```bash
WT=/home/ben/repositories/Iverson/.worktrees/csr11-server-authz
cd $WT/Iverson.Server && docker compose -p csr11-config-check --env-file /dev/null -f docker-compose.yml config --no-interpolate --quiet && echo compose-parses
python3 - <<'PY'
import re, yaml
job = open("deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml").read()
comp = yaml.safe_load(open("docker-compose.yml"))["services"]["starrocks-init"]["command"][0]
norm = lambda s: [re.sub(r"IDENTIFIED BY '[^']*'", "IDENTIFIED BY <pw>", l.strip()) for l in s.splitlines()
                  if re.match(r"\s*(CREATE|GRANT|REVOKE|ALTER USER)\b", l)]
j, c = norm(job), norm(comp)
print("job and starrocks-init statements match" if j == c else f"DIFFER\n job={j}\n compose={c}")
PY
command grep -c 'Database=iverson;Uid=iverson_app' docker-compose.yml
command grep -c 'ConnectionStrings__StarRocksProvisioner' docker-compose.yml
git -C $WT status --short
```

`--env-file /dev/null` and `--no-interpolate` keep `config` from reading `.env`. The `-p` name keeps it clear of the user's `iversonserver` project. `config` only parses the file; it creates nothing.

Expected:
- `compose-parses`;
- `job and starrocks-init statements match`;
- `0`: no app connection string names `Database=iverson`;
- `1`: only the worker has the provisioner string;
- `git status` shows exactly the five files in **Files**: one new file and four modified.

- [ ] **Step 5: Mutation proof**

Before the first mutation, run Step 6's `git add …` command now (not the `git commit`), so that `git checkout -- <file>` restores this task's staged version, new files included. Apply each mutation alone and re-render only `values-local`. Confirm the checker prints `FAIL` with the named message, then revert with `git checkout -- <file>`, which restores the staged new file too.

| # | Mutation | Expected checker message |
|---|---|---|
| D1 | In `job-create-user.yaml`, move `CREATE DATABASE IF NOT EXISTS iverson;` below `REVOKE user_admin FROM 'iverson_app'@'%';` | `Job statements out of order` |
| D2 | In `job-create-user.yaml`, delete `REVOKE user_admin FROM 'iverson_app'@'%';` | `Job missing statements: ["REVOKE user_admin FROM 'iverson_app'@'%';"]` |
| D3 | In `charts/worker/templates/deployment.yaml`, change `-starrocks-provisioner` to `-starrocks-app` in the new `secretKeyRef` | `worker STARROCKS_PROVISIONER_PASSWORD secretKeyRef wrong` |
| D4 | Copy the worker's two new env entries into `charts/api/templates/deployment.yaml` after its `ConnectionStrings__StarRocks` | `api env mentions the provisioner` |
| D5 | In `secret-provisioner.yaml`, delete the `"helm.sh/resource-policy": keep` line | `provisioner Secret lacks keep` |
| D6 | In `charts/api/templates/deployment.yaml`, restore `Database=iverson;` | `api ConnectionStrings__StarRocks still sets Database=` |

Command shape:

```bash
WT=/home/ben/repositories/Iverson/.worktrees/csr11-server-authz
SCR=/tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null && helm template iverson . -n iverson -f values-local.yaml | python3 $SCR/check_t4.py values-local
```

For compose:
- **D7.** Delete the `REVOKE user_admin` line from `starrocks-init`. The parity check must print `DIFFER`.

After the last revert, re-run Step 4 and expect the same results.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && C=Iverson.Server/deploy/helm/iverson && git add \
  $C/charts/starrocks/templates/secret-provisioner.yaml \
  $C/charts/starrocks/templates/job-create-user.yaml \
  $C/charts/api/templates/deployment.yaml \
  $C/charts/worker/templates/deployment.yaml \
  Iverson.Server/docker-compose.yml
git commit -m "create the starrocks provisioner login and strip the app login's direct grants on deploy

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

The `charts/*.tgz` files that `helm dependency build` writes are gitignored (`.gitignore:76`, `Iverson.Server/deploy/helm/**/charts/*.tgz`), so they never appear in `git status` and cannot be staged by the `git add` above. `Chart.lock` is tracked and `helm dependency build` leaves it unchanged. Confirm with `git status --short` after the commit: it should print nothing for this task's paths.

### Task 5: Relation identifiers and Postgres identifier quoting

**Spec:** §4a (closes #2)

**Files:**
- Create: `Iverson.Server/Iverson.Sql/PostgresIdentifier.cs` — the one identifier helper: `Quote` (returns `"name"`, refuses `"` and NUL) and `IsPlain` (the client identifier pattern, moved here from the orchestrator so `Iverson.Sql` can use it; `Iverson.Sql` references no other project).
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:1, 23-29` — drop the `Regex` field and its `using`; keep the rationale comment, pointing at `PostgresIdentifier.IsPlain`.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:74-76` — `ValidateIdentifier` on every relation's `PropertyName`, `ForeignKey` and `RelatedType`, after `RejectReservedTenantName`.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:192-204, 573-578, 615-619` — comments that the change makes false.
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:796` — `IsValidIdentifier(string?)` delegates to `PostgresIdentifier.IsPlain`.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:197` — the rehydration disjunction gains the three relation fields.
- Modify: `Iverson.Server/Iverson.Sql/EntityRepository.cs` (whole file, 127 lines) — every client-derived identifier interpolation goes through `PostgresIdentifier.Quote`; `UpdateColumnsAsync` checks each column key with `IsPlain` before it becomes `@{c}`. Sites: `:9` (table, key), `:18-20` (key, table, key), `:26` (table, relation FK `columnName`), `:37` (table, relation FK `columnName`), `:41` (table), `:51` (tenant column), `:58-60` and `:68-71` (key, table, key), `:85` (table, key), `:105` (column, and the `@{c}` parameter name), `:119` (table, key).
- Modify: `Iverson.Server/Iverson.Sql/OutboxWriter.cs:21-31` — column, key and table names (`:23-24`, `:28-30`) quoted. `outboxTableName` (`:35, :104, :113, :131`) is a constant and is left alone.
- Modify: `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:14-16, 68-77, 116-119, 134-135, 145-149, 158, 171-174, 182, 191, 193` — entity DDL. Left alone: the catalog-read `orphan` (`:135`), the `SqlType` and `DEFAULT` literals, the constant `SET LOCAL ROLE {roleName}` (`:275`).
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIdentifierTests.cs` (new) — `Quote` wraps, refuses `"` and NUL; `IsPlain` shape.
- Test: `Iverson.Server/Iverson.Sql.Tests/EntityRepositoryTests.cs` — the relation-FK sink refuses a quoted name; `UpdateColumnsAsync` refuses a non-plain parameter name before any statement.
- Test: `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs` — a column name with `"` is refused before the transaction.
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresSchemaManagerTests.cs` — a table name with `"` is refused before connecting.
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs` — a column name with `"` is refused and no table is created (container).
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — non-identifier relation names rejected for every kind and field; reserved-name FK message unchanged for every kind; two comments corrected.
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — rehydration rejects each non-identifier relation field; an identifier-shaped OneToMany still loads.

**Interfaces:**
- Produces: `public static class Iverson.Sql.PostgresIdentifier` with `string Quote(string name)` (throws `ArgumentException`) and `bool IsPlain(string? name)`.
- Produces: `SchemaRegistrationOrchestrator.IsValidIdentifier(string? name)` (was `string name`); `SchemaRegistrationOrchestrator.IdentifierPattern` is removed (its only reader was `IsValidIdentifier`).
- Produces: the quoted `EntityRepository.FetchByArrayContainsAsync` text that Task 6 deletes.
- Consumes: nothing from other tasks.

- [ ] **Step 1: Write the failing tests**

Shape-only malformed names, per the brief: a name with a space (`"Two Words"`), a name containing a double-quote character, a name containing NUL. No other malformed strings.

1a. Create `Iverson.Server/Iverson.Sql.Tests/PostgresIdentifierTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.Sql.Tests;

public class PostgresIdentifierTests
{
    [Fact]
    public void Quote_WrapsTheNameInDoubleQuotes()
    {
        PostgresIdentifier.Quote("articles").Should().Be("\"articles\"");
        // The server-owned tenant column fails the client pattern but is a legal quoted name.
        PostgresIdentifier.Quote("__TenantId").Should().Be("\"__TenantId\"");
    }

    [Fact]
    public void Quote_NameContainingADoubleQuote_IsRefused()
    {
        var act = () => PostgresIdentifier.Quote("Na\"me");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Quote_NameContainingANulCharacter_IsRefused()
    {
        var act = () => PostgresIdentifier.Quote("Na\0me");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Title", true)]
    [InlineData("Widget2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Two Words", false)]
    [InlineData("snake_case", false)]
    [InlineData("2Title", false)]
    [InlineData("Title\n", false)]
    public void IsPlain_AcceptsOnlyALetterFollowedByLettersAndDigits(string? name, bool expected) =>
        PostgresIdentifier.IsPlain(name).Should().Be(expected);
}
```

1b. `Iverson.Server/Iverson.Sql.Tests/EntityRepositoryTests.cs` — add after `FetchByColumnAsync_FiltersByGivenColumn_NotTheKeyColumn`:

```csharp
    [Fact]
    public async Task FetchByColumnAsync_ColumnNameContainingADoubleQuote_IsRefusedBeforeAnyQuery()
    {
        // The column here is a relation's foreign key, which reaches this method from a registered
        // schema. It is quoted as an identifier, so a name the quoting helper refuses never reaches SQL.
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new EntityRepository(sql);

        var act = () => repo.FetchByColumnAsync(ArticleSchema, "Author\"Id", "a1", EntityAccess.ForTenant("tenant-a"));

        await act.Should().ThrowAsync<ArgumentException>();
        sql.ReceivedCalls().Should().BeEmpty();
    }
```

and add after `UpdateColumnsAsync_SetsOnlySuppliedColumns_AndFiltersByKeyColumn` (end of class):

```csharp
    [Fact]
    public async Task UpdateColumnsAsync_ColumnNameThatIsNotAPlainIdentifier_IsRefusedBeforeAnyStatement()
    {
        // Each column name is also the parameter name (@Title). "Two Words" quotes cleanly as an
        // identifier, so only the plain-identifier check on the parameter name can refuse it.
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var repo = new EntityRepository(sql);

        var act = () => repo.UpdateColumnsAsync(
            tx, ArticleSchema, "k1",
            new Dictionary<string, object?> { ["Two Words"] = "value" },
            EntityAccess.ForTenant("tenant-a"));

        await act.Should().ThrowAsync<ArgumentException>();
        tx.ReceivedCalls().Should().BeEmpty();
    }
```

1c. `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs` — add after `UpsertAndEnqueueOutboxAsync_WithTenantId_SwitchesRoleForUpsert_ThenResetsBeforeOutboxInsert`:

```csharp
    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_ColumnNameContainingADoubleQuote_IsRefusedBeforeTheTransaction()
    {
        var schema = new TableSchema(
            "articles",
            new ColumnSchema("Id", "uuid", false),
            new List<ColumnSchema> { new("Ti\"tle", "text", false) });

        var act = () => _sut.UpsertAndEnqueueOutboxAsync(schema, "Article", Guid.NewGuid().ToString(), "{}");

        await act.Should().ThrowAsync<ArgumentException>();
        await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());
    }
```

1d. `Iverson.Server/Iverson.Sql.Tests/PostgresSchemaManagerTests.cs` — add `using Microsoft.Extensions.Logging.Abstractions;` after `using FluentAssertions;`, and add after `PostgresSchemaManager_ImplementsIPostgresSchemaManager`:

```csharp
    [Fact]
    public async Task ApplySchemaAsync_TableNameContainingADoubleQuote_IsRefusedBeforeConnecting()
    {
        // Nothing listens on port 1, so reaching the connection would fail with a connection error
        // instead. An ArgumentException means the name was refused before any connection was made.
        var manager = new PostgresSchemaManager(
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1",
            NullLogger<PostgresSchemaManager>.Instance);
        var schema = new TableSchema(
            "play\"ers",
            new ColumnSchema("id", "uuid", IsNullable: false),
            new List<ColumnSchema> { new("name", "text", IsNullable: false) });

        var act = () => manager.ApplySchemaAsync(schema);

        await act.Should().ThrowAsync<ArgumentException>();
    }
```

1e. `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs` — add after `ApplySchemaAsync_CreatesTable_WhenNotExists`:

```csharp
    [Fact]
    public async Task ApplySchemaAsync_ColumnNameContainingADoubleQuote_IsRefusedAndCreatesNoTable()
    {
        var table = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [new ColumnSchema("na\"me", "text", IsNullable: false)]);

        var act = async () => await _schemaManager.ApplySchemaAsync(schema);

        await act.Should().ThrowAsync<ArgumentException>();
        var count = await _repo.QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}'");
        count.Should().Be(0);
    }
```

1f. `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — add after `RegisterAsync_ARelationNavigationPropertyNamedLikeTheTenantColumn_IsRejectedForEveryKind`:

```csharp
    [Theory]
    [InlineData(Client.Contracts.RelationKind.OneToOne)]
    [InlineData(Client.Contracts.RelationKind.OneToMany)]
    [InlineData(Client.Contracts.RelationKind.ManyToOne)]
    [InlineData(Client.Contracts.RelationKind.ManyToMany)]
    public async Task RegisterAsync_ARelationForeignKeyNamedLikeTheTenantColumn_KeepsTheReservedNameMessageForEveryKind(
        Client.Contracts.RelationKind kind)
    {
        // The reserved name also fails the identifier pattern (leading underscore), and relation
        // names are identifier-checked. This pins that the reserved-name guard still answers first,
        // for every kind, with the message that names the reservation.
        var td = SimpleType("Widget", "Name");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner",
            Kind         = kind,
            RelatedType  = "User",
            ForeignKey   = SchemaDescriptor.TenantColumnName,
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith("Relation foreign key '__TenantId' on 'Widget'");
        ex.Which.Status.Detail.Should().NotContain("is not a valid identifier");
    }
```

and add after `RegisterAsync_WithATrailingNewlineTypeName_ThrowsInvalidArgument`:

```csharp
    [Theory]
    [InlineData(Client.Contracts.RelationKind.OneToOne,   "PropertyName", "relation navigation property")]
    [InlineData(Client.Contracts.RelationKind.OneToOne,   "ForeignKey",   "relation foreign key")]
    [InlineData(Client.Contracts.RelationKind.OneToOne,   "RelatedType",  "relation related type")]
    [InlineData(Client.Contracts.RelationKind.OneToMany,  "PropertyName", "relation navigation property")]
    [InlineData(Client.Contracts.RelationKind.OneToMany,  "ForeignKey",   "relation foreign key")]
    [InlineData(Client.Contracts.RelationKind.OneToMany,  "RelatedType",  "relation related type")]
    [InlineData(Client.Contracts.RelationKind.ManyToOne,  "PropertyName", "relation navigation property")]
    [InlineData(Client.Contracts.RelationKind.ManyToOne,  "ForeignKey",   "relation foreign key")]
    [InlineData(Client.Contracts.RelationKind.ManyToOne,  "RelatedType",  "relation related type")]
    [InlineData(Client.Contracts.RelationKind.ManyToMany, "PropertyName", "relation navigation property")]
    [InlineData(Client.Contracts.RelationKind.ManyToMany, "ForeignKey",   "relation foreign key")]
    [InlineData(Client.Contracts.RelationKind.ManyToMany, "RelatedType",  "relation related type")]
    public async Task RegisterAsync_ARelationNameThatIsNotAnIdentifier_IsRejectedForEveryKind(
        Client.Contracts.RelationKind kind, string field, string label)
    {
        // Every relation name is identifier-checked, for all four kinds: the foreign key reaches
        // SQL as a column name (for OneToMany, a column on the related type's table), the
        // navigation property becomes a payload key, and the related type is looked up by name.
        const string notAnIdentifier = "Two Words";
        var td = SimpleType("Widget", "Name");
        td.Properties.Add(new PropertyDescriptor { Name = "UserId", ObjectType = ObjectType.Guid });
        td.Properties.Add(new PropertyDescriptor { Name = "UserIds", ObjectType = ObjectType.Guid, IsArray = true });
        var relation = new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner",
            Kind         = kind,
            RelatedType  = "User",
            ForeignKey   = kind == Client.Contracts.RelationKind.ManyToMany ? "UserIds" : "UserId",
        };
        switch (field)
        {
            case "PropertyName": relation.PropertyName = notAnIdentifier; break;
            case "ForeignKey":   relation.ForeignKey   = notAnIdentifier; break;
            case "RelatedType":  relation.RelatedType  = notAnIdentifier; break;
        }
        td.Relations.Add(relation);

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().StartWith(
            $"{label} on type 'Widget' '{notAnIdentifier}' is not a valid identifier");
        _registry.Get("Widget").Should().BeNull();
    }
```

Also correct two comments in the same file that the change makes false (assertions untouched).

In `RegisterAsync_WithARelationForeignKeyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument`, replace:

```csharp
        // The foreign-key arm is the one ValidateIdentifier does NOT cover — relation names are
        // never identifier-checked — so without this guard it reaches the FK lookup and is
        // rejected as "not a declared property", which is a misleading answer.
```

with:

```csharp
        // Relation names are identifier-checked too, so without this guard the leading underscore
        // is rejected with ValidateIdentifier's generic message, which never names the reservation.
```

In `RegisterAsync_WithARelationNavigationPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument`, replace:

```csharp
        // Ruling 24. The navigation-property name is the FIFTH way to address the reserved name and
        // is covered by nothing else: ValidateIdentifier runs on TypeName and Properties[].Name
        // only, the FK-naming rule constrains ForeignKey only, and RelationCollisionCheck merely
        // compares PropertyName to ForeignKey — so this fixture, whose ForeignKey is the perfectly
        // legal "AuthorId", registered CLEANLY before this guard existed. The nav property is not a
```

with:

```csharp
        // Ruling 24. The navigation-property name is the FIFTH way to address the reserved name.
        // Before relation names were identifier-checked nothing else covered it, so this fixture,
        // whose ForeignKey is the perfectly legal "AuthorId", registered CLEANLY before this guard
        // existed; ValidateIdentifier now refuses it as well, but only with its generic message,
        // which never names the reservation. The nav property is not a
```

1g. `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — add after `LoadAsync_DescriptorWithInvalidFkColumnIdentifier_IsSkippedAndLogsError`:

```csharp
    // Relation names are not ScalarColumns, and a OneToMany foreign key is not one of this
    // descriptor's FkColumns (it names a column on the related type's table), so none of the
    // clauses above sees them. Registration identifier-checks all three; rehydration must too.
    [Theory]
    [InlineData("PropertyName")]
    [InlineData("ForeignKey")]
    [InlineData("RelatedTypeName")]
    public async Task LoadAsync_DescriptorWithNonIdentifierRelationName_IsSkippedAndLogsError(string field)
    {
        const string notAnIdentifier = "Two Words";
        var relation = new RelationDescriptor("UserArticles", RelationKind.OneToMany, "UserArticle", "ArticleId");
        relation = field switch
        {
            "PropertyName"    => relation with { PropertyName = notAnIdentifier },
            "ForeignKey"      => relation with { ForeignKey = notAnIdentifier },
            "RelatedTypeName" => relation with { RelatedTypeName = notAnIdentifier },
            _                 => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
        var badSchema = SchemaFixtures.ArticleSchema() with { Relations = [relation] };

        _repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", SerializeAsRegistryWould(badSchema))
        });

        await _sut.LoadAsync();

        _sut.IsRegistered("Article").Should().BeFalse(
            "a relation name that fails SchemaRegistrationOrchestrator's own identifier pattern must never be admitted on rehydration");
        _logs.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("failed identifier/key-type validation on rehydration") &&
            e.Message.Contains("Article"));
    }

    [Fact]
    public async Task LoadAsync_DescriptorWithIdentifierShapedOneToManyRelation_IsAdmitted()
    {
        _repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", SerializeAsRegistryWould(SchemaFixtures.ArticleWithOneToManySchema()))
        });

        await _sut.LoadAsync();

        _sut.IsRegistered("Article").Should().BeTrue();
        _logs.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.SchemaRegistrationOrchestratorTests|FullyQualifiedName~Iverson.Api.Tests.Schema.SchemaRegistryTests"
```
Expected: the 12 `RegisterAsync_ARelationNameThatIsNotAnIdentifier_IsRejectedForEveryKind` cases fail (no relation name is identifier-checked today, so each case either registers or fails later with a different message), and the 3 `LoadAsync_DescriptorWithNonIdentifierRelationName_IsSkippedAndLogsError` cases fail (the row is admitted). `RegisterAsync_ARelationForeignKeyNamedLikeTheTenantColumn_KeepsTheReservedNameMessageForEveryKind` (4 cases) and `LoadAsync_DescriptorWithIdentifierShapedOneToManyRelation_IsAdmitted` pass already; they are guards for Step 3.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~Iverson.Sql.Tests.PostgresIdentifierTests|FullyQualifiedName~Iverson.Sql.Tests.EntityRepositoryTests|FullyQualifiedName~Iverson.Sql.Tests.OutboxWriterTests|FullyQualifiedName~Iverson.Sql.Tests.PostgresSchemaManagerTests"
```
Expected: build failure, `PostgresIdentifier` does not exist.

- [ ] **Step 3: Implement**

3a. Create `Iverson.Server/Iverson.Sql/PostgresIdentifier.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Iverson.Sql;

/// <summary>
/// Identifier handling for the parts of a Postgres statement that cannot be parameters: table and
/// column names taken from a registered schema. Values always travel as parameters.
/// </summary>
public static class PostgresIdentifier
{
    // The shape every client-declared type, property and relation name must have
    // (SchemaRegistrationOrchestrator.IsValidIdentifier delegates here). No underscores, because
    // NamingExtensions.ToSnakeCase inserts its own when deriving table names; \A and \z, not ^ and
    // $, so a trailing newline is refused; an empty string never matches.
    private static readonly Regex PlainPattern = new(@"\A[A-Za-z][A-Za-z0-9]*\z", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="name"/> is a letter followed by letters and digits. Null-tolerant
    /// because SchemaRegistry.LoadAsync passes names straight from deserialized JSON, where a
    /// non-nullable string can still arrive null, and a throw there would abort the whole load.
    /// </summary>
    public static bool IsPlain(string? name) => name is not null && PlainPattern.IsMatch(name);

    /// <summary>
    /// <paramref name="name"/> as a delimited identifier. Refuses a double quote, which would end
    /// the identifier early, and NUL, which Postgres cannot store in an identifier.
    /// </summary>
    public static string Quote(string name)
    {
        if (name.Contains('"') || name.Contains('\0'))
            throw new ArgumentException(
                "A Postgres identifier may not contain a double quote or a NUL character.", nameof(name));

        return $"\"{name}\"";
    }
}
```

3b. `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`.

Delete line 1:
```csharp
using System.Text.RegularExpressions;
```

Replace lines 23-29:
```csharp
    // TypeName/property names are string-interpolated unescaped into CREATE TABLE/ALTER TABLE
    // DDL by PostgresSchemaManager/StarRocksSchemaManager after only a case transformation
    // (NamingExtensions.ToSnakeCase, which does not escape or reject anything). Validate at
    // the source — every descriptor that reaches SchemaBuilder.BuildDescriptor must already be
    // a safe DDL identifier. No underscores are permitted in the input because ToSnakeCase
    // inserts its own; this pattern also naturally rejects an empty string.
    internal static readonly Regex IdentifierPattern = new(@"\A[A-Za-z][A-Za-z0-9]*\z", RegexOptions.Compiled);
```
with:
```csharp
    // Type, property and relation names reach DDL and query text (PostgresSchemaManager,
    // EntityRepository, StarRocksSchemaManager) after only a case transformation
    // (NamingExtensions.ToSnakeCase, which does not escape or reject anything). Validate at the
    // source — every descriptor that reaches SchemaBuilder.BuildDescriptor must already be a safe
    // DDL identifier. The pattern is PostgresIdentifier.IsPlain: no underscores, because
    // ToSnakeCase inserts its own, and an empty string is rejected.
```

Replace lines 74-76:
```csharp
            ValidateIdentifier(typeDesc.TypeName, "type_name");
            foreach (var property in typeDesc.Properties)
                ValidateIdentifier(property.Name, $"property name on type '{typeDesc.TypeName}'");
```
with:
```csharp
            ValidateIdentifier(typeDesc.TypeName, "type_name");
            foreach (var property in typeDesc.Properties)
                ValidateIdentifier(property.Name, $"property name on type '{typeDesc.TypeName}'");

            // All four relation kinds. The foreign key reaches SQL as a column name (a OneToMany
            // foreign key is a column on the related type's table, filtered on by relation
            // hydration), the navigation property becomes a payload key, and the related type is
            // looked up by name. After RejectReservedTenantName, so a reserved name keeps its message.
            foreach (var relation in typeDesc.Relations)
            {
                ValidateIdentifier(relation.PropertyName, $"relation navigation property on type '{typeDesc.TypeName}'");
                ValidateIdentifier(relation.ForeignKey, $"relation foreign key on type '{typeDesc.TypeName}'");
                ValidateIdentifier(relation.RelatedType, $"relation related type on type '{typeDesc.TypeName}'");
            }
```

Replace the comment at lines 192-204 (inside the FK-lookup loop):
```csharp
                // ScalarColumns position: EXCLUDE __TenantId. UNREACHABLE BY CONSTRUCTION from this
                // path — RejectReservedTenantName, run above on the inbound typeDesc before
                // BuildDescriptor, already rejects any relation whose ForeignKey is __TenantId, so
                // no registration can reach this lookup with that name and no test can discriminate
                // the clause. It is kept deliberately, NOT dead code to delete: this lookup must
                // answer IDENTICALLY to its live twin in RelationValidator.ValidateSingleRelation,
                // which IS still reachable (SchemaRegistry.LoadAsync rehydrates descriptors without
                // re-running BuildDescriptor or the guard above) and IS still covered by a test.
                // Delete it here and the twins diverge; then any future change that reorders or
                // removes the upfront guard silently restores the original defect — the lookup
                // resolves the server-owned column, the relation falls through to the requiredSqlType
                // check below, and the caller is told to redeclare as a GUID a column it never
                // declared and cannot address.
```
with:
```csharp
                // ScalarColumns position: EXCLUDE __TenantId. UNREACHABLE BY CONSTRUCTION from this
                // path — RejectReservedTenantName and the relation ValidateIdentifier calls, run above
                // on the inbound typeDesc before BuildDescriptor, both reject a relation whose
                // ForeignKey is __TenantId, so no registration can reach this lookup with that name
                // and no test can discriminate the clause. It is kept deliberately, NOT dead code to
                // delete: this lookup must answer IDENTICALLY to its twin in
                // RelationValidator.ValidateSingleRelation, which is still covered by a test. Since
                // SchemaRegistry.LoadAsync refuses non-identifier relation names, rehydration no
                // longer reaches that twin with this name either; only a hand-built descriptor does.
                // Delete it here and the twins diverge; then any future change that reorders or
                // removes the upfront guards silently restores the original defect — the lookup
                // resolves the server-owned column, the relation falls through to the requiredSqlType
                // check below, and the caller is told to redeclare as a GUID a column it never
                // declared and cannot address.
```

Replace in the `RejectReservedTenantName` doc comment (lines 573-578):
```csharp
    /// Deliberately placed BEFORE <c>ValidateIdentifier</c>. A property named "__TenantId" would
    /// otherwise be rejected by the identifier pattern (which forbids a leading underscore) with a
    /// generic "not a valid identifier" message that never tells the caller the name is reserved.
    /// The foreign-key arm is not covered by ValidateIdentifier at all — relation names are never
    /// identifier-checked — and would otherwise surface at the FK lookup as a misleading
    /// "which is not a declared property".
```
with:
```csharp
    /// Deliberately placed BEFORE <c>ValidateIdentifier</c>. A property, relation foreign key or
    /// relation navigation property named "__TenantId" would otherwise be rejected by the
    /// identifier pattern (which forbids a leading underscore) with a generic "not a valid
    /// identifier" message that never tells the caller the name is reserved.
```

Replace inside `RejectReservedTenantName(TypeDescriptor)` (lines 615-619):
```csharp
            // Ruling 24. NOTHING else covers the navigation-property name: ValidateIdentifier runs
            // on TypeName and Properties[].Name only, the FK-naming rule constrains ForeignKey
            // only, and RelationCollisionCheck merely compares the two names to each other — so a
            // ManyToOne with PropertyName '__TenantId' and a well-formed ForeignKey registers
            // cleanly. The nav property is not a column, which is exactly why it is dangerous: on a
```
with:
```csharp
            // Ruling 24. ValidateIdentifier now refuses a navigation property named '__TenantId'
            // too, but only with its generic message; this arm is what names the reservation.
            // Before relation names were identifier-checked, a ManyToOne with PropertyName
            // '__TenantId' and a well-formed ForeignKey registered cleanly. The nav property is
            // not a column, which is exactly why it is dangerous: on a
```

Replace line 796:
```csharp
    internal static bool IsValidIdentifier(string name) => IdentifierPattern.IsMatch(name);
```
with:
```csharp
    // The single definition of the client identifier shape lives in Iverson.Sql, which needs it
    // for parameter names and cannot reference this assembly.
    internal static bool IsValidIdentifier(string? name) => PostgresIdentifier.IsPlain(name);
```
(`using Iverson.Sql;` is already at line 5.)

3c. `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` — replace line 197:
```csharp
                    descriptor.FkColumns.Any(c => !SchemaRegistrationOrchestrator.IsValidIdentifier(c.ColumnName)) ||
```
with:
```csharp
                    descriptor.FkColumns.Any(c => !SchemaRegistrationOrchestrator.IsValidIdentifier(c.ColumnName)) ||
                    // Relation names, which RegisterAsync identifier-checks on the way in. A OneToMany
                    // ForeignKey names a column on the related type's table that relation hydration
                    // filters on, and it is not one of this descriptor's FkColumns, so the clause
                    // above never sees it.
                    descriptor.Relations.Any(r =>
                        !SchemaRegistrationOrchestrator.IsValidIdentifier(r.PropertyName) ||
                        !SchemaRegistrationOrchestrator.IsValidIdentifier(r.ForeignKey) ||
                        !SchemaRegistrationOrchestrator.IsValidIdentifier(r.RelatedTypeName)) ||
```

3d. Replace the whole of `Iverson.Server/Iverson.Sql/EntityRepository.cs` with:

```csharp
using Dapper;

namespace Iverson.Sql;

// Every table and column name in these statements comes from a registered schema, so each one goes
// through PostgresIdentifier.Quote; values always travel as parameters.
public sealed class EntityRepository(IRecordStoreQueryExecutor sql) : IEntityRepository
{
    public Task<string?> FetchByKeyAsync(TableSchema schema, string key, EntityAccess access)
    {
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        return sql.QuerySingleOrDefaultAsync<string>(
            $"SELECT row_to_json(t)::text FROM {table} t WHERE {keyColumn} = @Key::uuid",
            new { Key = key }, access.Role, access.TenantId);
    }

    public Task<IEnumerable<KeyedRow>> FetchManyByKeysAsync(TableSchema schema, IReadOnlyList<string> keys, EntityAccess access)
    {
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);

        // Guid[], not string[]: Npgsql sends string[] as text[], which blocks Postgres from using
        // the uuid primary key index for ANY(...) — see this plan's Global Constraints.
        var keyGuids = keys.Select(Guid.Parse).ToArray();
        return sql.QueryAsync<KeyedRow>(
            $"SELECT {keyColumn}::text AS key, row_to_json(t)::text AS data " +
            $"FROM {table} t " +
            $"WHERE {keyColumn} = ANY(@Keys)",
            new { Keys = keyGuids }, access.Role, access.TenantId);
    }

    public Task<IEnumerable<string>> FetchByColumnAsync(TableSchema schema, string columnName, string value, EntityAccess access)
    {
        var table  = PostgresIdentifier.Quote(schema.TableName);
        var column = PostgresIdentifier.Quote(columnName);
        return sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM {table} t WHERE {column} = @Key::uuid",
            new { Key = value }, access.Role, access.TenantId);
    }

    // ManyToMany reverse lookup: the FK column is a uuid[] living on the DECLARING row (the
    // opposite direction from FetchByColumnAsync's OneToMany usage), so finding declaring rows
    // whose array contains a given target key needs Postgres array containment (@>), not
    // equality. Guid[], not string[] — Npgsql sends string[] as text[], which blocks Postgres
    // from using the uuid[] GIN/index for @> — see this plan's Global Constraints and
    // FetchManyByKeysAsync above.
    public Task<IEnumerable<string>> FetchByArrayContainsAsync(TableSchema schema, string columnName, string value, EntityAccess access)
    {
        var table  = PostgresIdentifier.Quote(schema.TableName);
        var column = PostgresIdentifier.Quote(columnName);
        return sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM {table} t WHERE {column} @> @Keys",
            new { Keys = new[] { Guid.Parse(value) } }, access.Role, access.TenantId);
    }

    public Task<IEnumerable<string>> FetchAllAsync(TableSchema schema, EntityAccess access) =>
        sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM {PostgresIdentifier.Quote(schema.TableName)} t",
            null, access.Role, access.TenantId);

    // A type-level re-render row means "every entity of this type, across every tenant", so its
    // one caller passes EntityAccess.CrossTenantMaintenance — scoping this to one tenant would
    // silently backfill only that tenant. The parameter is still required rather than hard-wired:
    // the exemption belongs at the call site, where a reviewer reads it. Keyset pagination ordered
    // by the key column, not OFFSET, so this stays stable and cheap as the table grows.
    public Task<IEnumerable<KeyedTenantRow>> FetchKeysAndTenantsPagedAsync(TableSchema schema, string? afterKey, int pageSize, EntityAccess access)
    {
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        var tenantSelect = schema.TenantColumn is not null
            ? $"{PostgresIdentifier.Quote(schema.TenantColumn)}::text AS \"TenantId\""
            : "NULL AS \"TenantId\"";

        if (afterKey is null)
        {
            return sql.QueryAsync<KeyedTenantRow>(
                $"""
                SELECT {keyColumn}::text AS "Key", {tenantSelect}
                FROM {table}
                ORDER BY {keyColumn}
                LIMIT @PageSize
                """,
                new { PageSize = pageSize }, access.Role, access.TenantId);
        }

        return sql.QueryAsync<KeyedTenantRow>(
            $"""
            SELECT {keyColumn}::text AS "Key", {tenantSelect}
            FROM {table}
            WHERE {keyColumn} > @AfterKey::uuid
            ORDER BY {keyColumn}
            LIMIT @PageSize
            """,
            new { AfterKey = afterKey, PageSize = pageSize }, access.Role, access.TenantId);
    }

    public async Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key, EntityAccess access)
    {
        // Quoted before the role switch, so a refused name leaves no role scope behind.
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);

        if (access.CrossTenant)
            await tx.EnterMaintenanceScopeAsync();
        else
            await tx.EnterTenantScopeAsync(access.TenantId);

        await tx.ExecuteAsync(
            $"DELETE FROM {table} WHERE {keyColumn} = @Key::uuid",
            new { Key = key });

        // SET LOCAL ROLE persists for the rest of the transaction, not just this statement.
        // Callers (e.g. ObjectMappingGrpcService.Delete) go on to write to plumbing tables
        // (the reconciliation/outbox queue) in this same transaction, and neither entity role has
        // a grant on those — reset back to the connection's own role before returning.
        await tx.ExitRoleScopeAsync();
    }

    // Enters and exits the role itself, exactly like DeleteAsync above. Its one caller
    // (EnrichmentConsumer) used to hand-roll the EnterTenantScopeAsync/ExitRoleScopeAsync pair
    // around this call — the very duplication TenantScopeTransactionExtensions' doc comment warns
    // is how the pairing gets missed — and leaving this method as the only IEntityRepository
    // member that does not name its role would have left a future caller one forgotten Enter away
    // from the silent unscoped write this batch exists to prevent.
    public async Task UpdateColumnsAsync(
        IDbTransactionContext tx, TableSchema schema, string key,
        IReadOnlyDictionary<string, object?> columns, EntityAccess access)
    {
        // Each column name is also its parameter name (@Title), and a parameter name cannot be
        // quoted, so it must have the plain identifier shape. The only caller's keys are
        // enrichment target property names, which registration already holds to that shape.
        foreach (var column in columns.Keys)
        {
            if (!PostgresIdentifier.IsPlain(column))
                throw new ArgumentException(
                    $"Column '{column}' cannot be bound as a parameter: a parameter name must be a letter followed by letters and digits.",
                    nameof(columns));
        }

        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        var setClause = string.Join(", ", columns.Keys.Select(c => $"{PostgresIdentifier.Quote(c)} = @{c}"));
        var parameters = new DynamicParameters();
        foreach (var (column, value) in columns)
        {
            parameters.Add(column, value);
        }
        parameters.Add("Key", key);

        if (access.CrossTenant)
            await tx.EnterMaintenanceScopeAsync();
        else
            await tx.EnterTenantScopeAsync(access.TenantId);

        await tx.ExecuteAsync(
            $"UPDATE {table} SET {setClause} WHERE {keyColumn} = @Key::uuid",
            parameters);

        // SET LOCAL ROLE persists for the rest of the transaction. EnrichmentConsumer goes on to
        // write the enrichment-state row and the outbox row in this same transaction, and neither
        // entity role has a grant on those plumbing tables — reset before returning.
        await tx.ExitRoleScopeAsync();
    }
}
```

3e. `Iverson.Server/Iverson.Sql/OutboxWriter.cs` — replace lines 21-31:
```csharp
        var allCols   = schema.Columns.Select(c => c.Name).ToList();
        var updateSet = allCols.Count > 0
            ? string.Join(", ", allCols.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
            : $"\"{schema.KeyColumn.Name}\" = EXCLUDED.\"{schema.KeyColumn.Name}\"";

        var upsertSql =
            $"""
            INSERT INTO "{schema.TableName}"
            SELECT * FROM json_populate_record(null::"{schema.TableName}", @Json::json)
            ON CONFLICT ("{schema.KeyColumn.Name}") DO UPDATE SET {updateSet}
            """;
```
with:
```csharp
        // Schema-derived names are quoted as identifiers; outboxTableName below is a constant.
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        var allCols   = schema.Columns.Select(c => PostgresIdentifier.Quote(c.Name)).ToList();
        var updateSet = allCols.Count > 0
            ? string.Join(", ", allCols.Select(c => $"{c} = EXCLUDED.{c}"))
            : $"{keyColumn} = EXCLUDED.{keyColumn}";

        var upsertSql =
            $"""
            INSERT INTO {table}
            SELECT * FROM json_populate_record(null::{table}, @Json::json)
            ON CONFLICT ({keyColumn}) DO UPDATE SET {updateSet}
            """;
```

3f. `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs`:

Replace lines 14-16:
```csharp
    public async Task ApplySchemaAsync(TableSchema schema, SchemaDriftPolicy driftPolicy = SchemaDriftPolicy.Warn)
    {
        using var activity = Telemetry.Source.StartActivity("db.apply_schema", ActivityKind.Client);
```
with:
```csharp
    public async Task ApplySchemaAsync(TableSchema schema, SchemaDriftPolicy driftPolicy = SchemaDriftPolicy.Warn)
    {
        // Quoted before any connection is opened, so a table or key name the helper refuses never
        // reaches the advisory lock or any DDL. Column names are quoted where they are used.
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);

        using var activity = Telemetry.Source.StartActivity("db.apply_schema", ActivityKind.Client);
```

Replace lines 68-77:
```csharp
                var keySql   = $"\"{schema.KeyColumn.Name}\" {schema.KeyColumn.SqlType} PRIMARY KEY";
                var colsSql  = schema.Columns.Select(c =>
                    $"\"{c.Name}\" {c.SqlType}{(c.IsNullable ? "" : " NOT NULL")}");

                var ddl = $"""
                    CREATE TABLE IF NOT EXISTS "{schema.TableName}" (
                        {keySql},
                        {string.Join(",\n    ", colsSql)}
                    )
                    """;
```
with:
```csharp
                var keySql   = $"{keyColumn} {schema.KeyColumn.SqlType} PRIMARY KEY";
                var colsSql  = schema.Columns.Select(c =>
                    $"{PostgresIdentifier.Quote(c.Name)} {c.SqlType}{(c.IsNullable ? "" : " NOT NULL")}");

                var ddl = $"""
                    CREATE TABLE IF NOT EXISTS {table} (
                        {keySql},
                        {string.Join(",\n    ", colsSql)}
                    )
                    """;
```

Replace lines 116-119:
```csharp
                    var alterSql = $"""
                        ALTER TABLE "{schema.TableName}"
                        ADD COLUMN IF NOT EXISTS "{col.Name}" {col.SqlType}{(col.IsNullable ? "" : $" NOT NULL DEFAULT ('{GetDefaultForType(col.SqlType)}')")}
                        """;
```
with:
```csharp
                    var alterSql = $"""
                        ALTER TABLE {table}
                        ADD COLUMN IF NOT EXISTS {PostgresIdentifier.Quote(col.Name)} {col.SqlType}{(col.IsNullable ? "" : $" NOT NULL DEFAULT ('{GetDefaultForType(col.SqlType)}')")}
                        """;
```

Replace lines 134-135:
```csharp
                    await conn.ExecuteAsync(
                        $"ALTER TABLE \"{schema.TableName}\" DROP COLUMN IF EXISTS \"{orphan}\"");
```
with:
```csharp
                    // orphan is read back from pg_attribute, not taken from the schema, so it is not
                    // passed through PostgresIdentifier.Quote.
                    await conn.ExecuteAsync(
                        $"ALTER TABLE {table} DROP COLUMN IF EXISTS \"{orphan}\"");
```

Replace lines 146-149:
```csharp
                await conn.ExecuteAsync($"""
                    CREATE INDEX IF NOT EXISTS "{idxName}"
                    ON "{schema.TableName}" ("{col.Name}")
                    """);
```
with:
```csharp
                await conn.ExecuteAsync($"""
                    CREATE INDEX IF NOT EXISTS {PostgresIdentifier.Quote(idxName)}
                    ON {table} ({PostgresIdentifier.Quote(col.Name)})
                    """);
```

Replace line 158:
```csharp
            await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON "{schema.TableName}" TO iverson_maintenance""");
```
with:
```csharp
            await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO iverson_maintenance""");
```

Replace lines 171-174:
```csharp
                        await conn.ExecuteAsync($"""
                            CREATE POLICY "{policyName}" ON "{schema.TableName}"
                            USING ("{schema.TenantColumn}" = current_setting('app.tenant_id', true))
                            """);
```
with:
```csharp
                        await conn.ExecuteAsync($"""
                            CREATE POLICY {PostgresIdentifier.Quote(policyName)} ON {table}
                            USING ({PostgresIdentifier.Quote(schema.TenantColumn)} = current_setting('app.tenant_id', true))
                            """);
```
(`policyName` stays raw where it is a parameter value at `:163-165`.)

Replace line 182:
```csharp
                await conn.ExecuteAsync($"""ALTER TABLE "{schema.TableName}" ENABLE ROW LEVEL SECURITY""");
```
with:
```csharp
                await conn.ExecuteAsync($"""ALTER TABLE {table} ENABLE ROW LEVEL SECURITY""");
```

Replace line 191:
```csharp
                await conn.ExecuteAsync($"""ALTER TABLE "{schema.TableName}" FORCE ROW LEVEL SECURITY""");
```
with:
```csharp
                await conn.ExecuteAsync($"""ALTER TABLE {table} FORCE ROW LEVEL SECURITY""");
```

Replace line 193:
```csharp
                await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON "{schema.TableName}" TO iverson_runtime""");
```
with:
```csharp
                await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO iverson_runtime""");
```

Then confirm no schema-derived interpolation was missed. Expected output: exactly two `PostgresSchemaManager.cs` lines. One is the `orphan` `DROP COLUMN` statement (`:135` at `1b7968aa`). The other is `var policyName = $"{schema.TableName}_tenant_isolation";` (`:162` at `1b7968aa`), which builds a value that is a query parameter at `:163-165` and passes through `PostgresIdentifier.Quote` at its one DDL use. Any other line is a miss:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && command grep -n '\\"{\|"{schema\.\|"{col\.\|"{idx\|"{policy' Iverson.Sql/EntityRepository.cs Iverson.Sql/OutboxWriter.cs Iverson.Sql/PostgresSchemaManager.cs
```

- [ ] **Step 4: Run the tests and see them pass**

The two commands from Step 2. Expected: all pass, including the 12 + 4 + 3 + 1 new Api cases and the new Sql unit tests.

Then the container-backed Postgres suites. **Heavy: each starts a Postgres Testcontainer. Run them one at a time, never in parallel.**
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~Iverson.Sql.Tests.PostgresIntegrationTests"
```
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "FullyQualifiedName~Iverson.Sql.Tests.TenantScopedAccessIntegrationTests"
```
Expected: all pass, including `ApplySchemaAsync_ColumnNameContainingADoubleQuote_IsRefusedAndCreatesNoTable`. The rest prove the quoted DDL and DML are byte-identical for valid names (RLS policy, FORCE, grants, index, `UpdateColumnsAsync` with `["name"]`).

Then the container-free regression for both projects:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures. If a container starts during a `Category!=Integration` run, stop and report it.

- [ ] **Step 5: Mutation proof**

Before the first mutation, run Step 6's `git add …` command now (not the `git commit`), so that `git checkout -- <file>` restores this task's staged version, new files included. Apply each mutation alone, run the named test's command from Step 2 (or Step 4 for the container one), record the failing test names, and revert with `git checkout -- <file>` before the next.

| # | Mutation (one line) | Must fail |
|---|---|---|
| M1 | Orchestrator: delete `ValidateIdentifier(relation.PropertyName, …)` | `RegisterAsync_ARelationNameThatIsNotAnIdentifier_IsRejectedForEveryKind` (the 4 `PropertyName` cases) |
| M2 | Orchestrator: delete `ValidateIdentifier(relation.ForeignKey, …)` | same theory, the 4 `ForeignKey` cases |
| M3 | Orchestrator: delete `ValidateIdentifier(relation.RelatedType, …)` | same theory, the 4 `RelatedType` cases |
| M4 | Orchestrator: move `RejectReservedTenantName(typeDesc);` to just after the relation `foreach` | `RegisterAsync_ARelationForeignKeyNamedLikeTheTenantColumn_KeepsTheReservedNameMessageForEveryKind` (all 4); `RegisterAsync_WithARelationForeignKeyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument`; `RegisterAsync_WithARelationNavigationPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument`; `RegisterAsync_ARelationNavigationPropertyNamedLikeTheTenantColumn_IsRejectedForEveryKind` (both cases); `RegisterAsync_RelationForeignKeyNamingTheServerOwnedTenantColumn_IsRejectedAsReserved`; and, because the property-name check also runs before the moved call, `RegisterAsync_WithAScalarPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument`, `RegisterAsync_WithADifferentlyCasedTenantColumnProperty_ThrowsInvalidArgument`, `RegisterAsync_WithAKeyPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument` and `RegisterAsync_WithADependentPropertyNamedLikeTheServerOwnedTenantColumn_ThrowsInvalidArgument` (13 in all) |
| M5 | SchemaRegistry: replace the `descriptor.Relations.Any(…)` clause with `false` | `LoadAsync_DescriptorWithNonIdentifierRelationName_IsSkippedAndLogsError` (all 3) |
| M6 | PostgresIdentifier: `if (name.Contains('"') \|\| name.Contains('\0'))` → `if (name.Contains('\0'))` | `Quote_NameContainingADoubleQuote_IsRefused`, `FetchByColumnAsync_ColumnNameContainingADoubleQuote_IsRefusedBeforeAnyQuery`, `UpsertAndEnqueueOutboxAsync_ColumnNameContainingADoubleQuote_IsRefusedBeforeTheTransaction`, `ApplySchemaAsync_TableNameContainingADoubleQuote_IsRefusedBeforeConnecting` |
| M7 | PostgresIdentifier: `if (name.Contains('"') \|\| name.Contains('\0'))` → `if (name.Contains('"'))` | `Quote_NameContainingANulCharacter_IsRefused` |
| M8 | EntityRepository.FetchByColumnAsync: `var column = PostgresIdentifier.Quote(columnName);` → `var column = $"\"{columnName}\"";` | `FetchByColumnAsync_ColumnNameContainingADoubleQuote_IsRefusedBeforeAnyQuery` |
| M9 | EntityRepository.UpdateColumnsAsync: `if (!PostgresIdentifier.IsPlain(column))` → `if (false)` | `UpdateColumnsAsync_ColumnNameThatIsNotAPlainIdentifier_IsRefusedBeforeAnyStatement` |
| M10 | OutboxWriter: `schema.Columns.Select(c => PostgresIdentifier.Quote(c.Name))` → `schema.Columns.Select(c => $"\"{c.Name}\"")` | `UpsertAndEnqueueOutboxAsync_ColumnNameContainingADoubleQuote_IsRefusedBeforeTheTransaction` |
| M11 | PostgresSchemaManager: `var table = PostgresIdentifier.Quote(schema.TableName);` → `var table = $"\"{schema.TableName}\"";` | `ApplySchemaAsync_TableNameContainingADoubleQuote_IsRefusedBeforeConnecting` |
| M12 | PostgresSchemaManager: `$"{PostgresIdentifier.Quote(c.Name)} {c.SqlType}…` → `$"\"{c.Name}\" {c.SqlType}…` (CREATE TABLE column list) | `ApplySchemaAsync_ColumnNameContainingADoubleQuote_IsRefusedAndCreatesNoTable` (container; run alone) |

Record each run's failing test names in the report.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git add \
  Iverson.Server/Iverson.Sql/PostgresIdentifier.cs \
  Iverson.Server/Iverson.Sql/EntityRepository.cs \
  Iverson.Server/Iverson.Sql/OutboxWriter.cs \
  Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs \
  Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
  Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs \
  Iverson.Server/Iverson.Sql.Tests/PostgresIdentifierTests.cs \
  Iverson.Server/Iverson.Sql.Tests/EntityRepositoryTests.cs \
  Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs \
  Iverson.Server/Iverson.Sql.Tests/PostgresSchemaManagerTests.cs \
  Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs
git commit -m "validate relation names and quote postgres identifiers

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Template relations removed

**Spec:** §4b (closes #12)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — Phase-1 comment (orig. `:58-61`); the `priorDescriptor` comment's reference to phase 2's `effectiveDescriptors` (orig. `:102-104`); phase-2 cross-type block (orig. `:266-291`) replaced by a per-type template check; `ValidateDocumentTemplate`/`ValidateDocumentSegment`/`RequireScalarProperty` lose their `allDescriptors`/`statusCode` parameters and OneHop/Block are rejected (orig. `:366-488`); `RequireNotRowOwned`, `RequireRelation`, `RequireTargetDescriptor` deleted (orig. `:490-530`); closed-enumeration comment in `RejectReservedTenantName` (orig. `:590-594`).
- Modify: `Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs` (whole file) — parameterless; OneHop and Block render empty; the fetch helpers go.
- Delete: `Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs` — the relation-dependent enqueueing (the whole class is that).
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (`:1019` at `1b7968aa`) — one stale comment naming phase 2's `effectiveDescriptors`.
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (`:453` at `1b7968aa`; Task 3 edits `:267-306` first, so match on the quoted line) — its hosted-service registration.
- Modify: `Iverson.Server/docker-compose.yml` (`:597-599` at `1b7968aa`; Task 4 edits other hunks of this file first, so match on the quoted text) — the worker-role comment that lists the deleted consumer.
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:14-20, 75-81, 233, 246, 258-291` (line numbers at `1b7968aa`; Task 5 adds 8 lines at `:197`, so after it these are `:241, :254, :266-299`. Locate by the quoted text) — `_reverseIndex`, `GetDependents`, `RebuildReverseIndex` and its two calls; their only reader was the deleted consumer.
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:170` and `Iverson.Server/Iverson.Sql/EntityRepository.cs` (`FetchByArrayContainsAsync` as Task 5 left it) — deleted; its only caller was the deleted consumer.
- Modify: `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs:160-164, 211-212, 276, 292-295` — comments and crefs naming the deleted class and `GetDependents`.
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/DocumentRerenderQueueWorker.cs:16-20` — doc comment cref to the deleted class. The worker and queue stay (type-level backfill).
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs` — relation templates rejected; relation-rule tests deleted or re-pointed (list below).
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRendererTests.cs` (whole file) — relation tests deleted; legacy stored relation segments render empty.
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — legacy template: own properties only, no related-row fetch; `BuildSut` constructor call.
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs:275` — constructor call only (container suite; compile-only here).
- Delete: `Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs`.
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — `GetDependents` test deleted.

**Test changes, one by one:**

| Test | Change | New expectation / reason |
|---|---|---|
| `DocumentTemplateValidationTests.RegisterAsync_TemplateReferencesUndeclaredRelation_ThrowsInvalidArgument` | re-pointed | still InvalidArgument; now also asserts the relation-template message |
| `…RegisterAsync_OneHopScalarNotOnTargetType_ThrowsInvalidArgument` | deleted | the target-scalar rule no longer exists; any `{Rel.Prop}` is refused (new theory) |
| `…RegisterAsync_OneHopOnCollectionRelation_ThrowsInvalidArgument` | deleted | single-vs-collection rule gone; covered by the new theory |
| `…RegisterAsync_BlockOnSingleValuedRelation_ThrowsInvalidArgument` | deleted | same |
| `…RegisterAsync_TemplateReferencesFieldPermissionCarryingPropertyOnOneHopTarget_ThrowsInvalidArgument` | deleted | one-hop targets no longer resolved; the declaring-type FieldPermission rule (Rule 7) stays tested |
| `…RegisterAsync_OneHopTraversesToRowOwnedTypeWithOwnerField_ThrowsInvalidArgument` | deleted | `RequireNotRowOwned` removed |
| `…RegisterAsync_OneHopTraversesToRowOwnedTypeWithRowPermissions_ThrowsInvalidArgument` | deleted | same |
| `…RegisterAsync_BlockTraversesToRowOwnedTypeWithOwnerField_ThrowsInvalidArgument` | deleted | same |
| `…RegisterAsync_BlockTraversesToRowOwnedTypeWithRowPermissions_ThrowsInvalidArgument` | deleted | same |
| `…RegisterAsync_RetargetingTypeDropsPropertyADependentTemplateReferences_ThrowsFailedPrecondition` | re-pointed and renamed `RegisterAsync_ReRegisteringATypeThatALegacyStoredTemplateReferences_Succeeds` | phase-2 re-validation removed: a stored legacy template no longer blocks its target's re-registration |
| `…RegisterAsync_RootTemplateReferencesLaterDependent_Succeeds` | deleted | that registration is now rejected; the new theory covers it |
| `…RegisterAsync_UnclosedBlock_ThrowsInvalidArgumentNotUnknown` | unchanged | parser failure, still InvalidArgument |
| `DocumentRendererTests` — `RenderAsync_OneHop_RendersTargetScalarProperty`, `RenderAsync_OneHop_NullForeignKey_RendersEmptyAndDoesNotFetch`, `RenderAsync_OneHop_DeletedTargetRow_RendersEmpty`, `RenderAsync_OneHop_ExplicitNonConventionalForeignKey_ReadsDeclaredColumn`, `RenderAsync_BlockOverOneToMany_RendersEachRowConcatenated`, `RenderAsync_BlockOverOneToMany_EmptyCollection_EmitsNothingIncludingBlockLiterals`, `RenderAsync_BlockOverManyToMany_RendersEachRowConcatenated`, `RenderAsync_BlockRows_SortedByKeyRegardlessOfFetchOrder`, `RenderAsync_ManyToManyBlockRows_SortedByKeyRegardlessOfFetchOrder`, `RenderAsync_ThreePlaceholdersOnSameRelation_IssuesExactlyOneFetch`, `RenderAsync_OneHop_PassesTenantScopedTrueAndAuthoritativeTenantId`, `RenderAsync_OneHop_RowInAnotherTenant_DoesNotRender`, `RenderAsync_OneHop_RelationNameCaseMismatch_ResolvesRelationCaseInsensitively`, `RenderAsync_Block_RelationNameCaseMismatch_ResolvesRelationCaseInsensitively`, `RenderAsync_OneHop_RelationMissingEntirely_RendersEmpty_DoesNotThrow`, `RenderAsync_Block_RelationMissingEntirely_RendersNothing_DoesNotThrow` | deleted (16) | all pin relation fetching, batching, sorting, tenant scoping or relation lookup, none of which exists now; the block-literal suppression moves into the new legacy test |
| `DocumentRendererTests` — the 5 scalar/literal tests | unchanged | |
| `DocumentRerenderConsumerTests` (whole class) | deleted | the class under test is deleted |
| `SchemaRegistryTests.LoadAsync_RebuildsReverseIndex_SoStartupRehydratedTemplateDependentsAreFound` | deleted | `GetDependents` and the reverse index are deleted |
| `SchemaRegistryTests.RegisterAsync_ThenLoadAsync_RoundTripsDocumentTemplateWithAllSegmentKinds` | unchanged | stored descriptors still carry OneHop/Block segments, and they must still round-trip |
| `IntelligenceStoreConsumerTests` (all existing) | constructor call only | |
| `PopularitySignalConsumerTests:456, 469` | unchanged | prose mentions of the old class in a `because` string and a comment; no compile dependency |

**Interfaces:**
- Consumes: Task 5's text of `SchemaRegistrationOrchestrator.cs`, `SchemaRegistry.cs` and `EntityRepository.cs` (in particular the quoted `FetchByArrayContainsAsync`).
- Produces: `DocumentRenderer` with a parameterless constructor and an unchanged `Task<string> RenderAsync(SchemaDescriptor, JsonElement, string, CancellationToken)`.
- Produces: `IEntityRepository` without `FetchByArrayContainsAsync`; `SchemaRegistry` without `GetDependents`.

- [ ] **Step 1: Write the failing tests (and remove the obsolete ones that still compile)**

1a. `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`

Delete these methods with their section headers where the section empties: `RegisterAsync_OneHopScalarNotOnTargetType_ThrowsInvalidArgument` (and the `// ── Rule 3 …` header), `RegisterAsync_OneHopOnCollectionRelation_ThrowsInvalidArgument` (`// ── Rule 4 …`), `RegisterAsync_BlockOnSingleValuedRelation_ThrowsInvalidArgument` (`// ── Rule 5 …`), `RegisterAsync_TemplateReferencesFieldPermissionCarryingPropertyOnOneHopTarget_ThrowsInvalidArgument` (`// ── Rule 8 …`), the four `…TraversesToRowOwnedType…` tests (`// ── Rule 11 …`), `RegisterAsync_RetargetingTypeDropsPropertyADependentTemplateReferences_ThrowsFailedPrecondition` (`// ── Rule 10 …`), and `RegisterAsync_RootTemplateReferencesLaterDependent_Succeeds` (`// ── Plus: root template referencing a type that appears later in dependents ────`).

Replace the Rule 2 test:
```csharp
    // ── Rule 2: undeclared relation ─────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_TemplateReferencesUndeclaredRelation_ThrowsInvalidArgument()
    {
        var td = WidgetType("{Owner.Name}"); // no relation named "Owner" declared

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
```
with:
```csharp
    // ── Rule 2: relation placeholders are refused ───────────────────────────────

    [Fact]
    public async Task RegisterAsync_TemplateReferencesUndeclaredRelation_ThrowsInvalidArgument()
    {
        var td = WidgetType("{Owner.Name}"); // no relation named "Owner" declared

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        // Refused as a relation placeholder, whether or not the relation exists.
        ex.Which.Status.Detail.Should().Contain("document templates may reference only the type's own properties.");
    }

    [Theory]
    [InlineData("{Owner.Name}")]
    [InlineData("{#Children}{Name}{/Children}")]
    [InlineData("{Name} by {Owner.Name}")]
    public async Task RegisterAsync_TemplateWithARelationPlaceholder_ThrowsInvalidArgument(string template)
    {
        // Both relations are declared and both targets are registered in the same request with no
        // row-level authorization, so the placeholder is refused for what it is, not for what it
        // would reach.
        var root = WidgetType(template);
        root.Properties.Add(new PropertyDescriptor { Name = "UserId", ObjectType = ObjectType.Guid });
        root.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner", Kind = Client.Contracts.RelationKind.ManyToOne, RelatedType = "User", ForeignKey = "UserId"
        });
        root.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Children", Kind = Client.Contracts.RelationKind.OneToMany, RelatedType = "Gadget", ForeignKey = "WidgetId"
        });

        var act = () => _sut.RegisterAsync(
            new SchemaRequest { RootType = root, Dependents = { SimpleType("User", "Name"), SimpleType("Gadget", "Name") } },
            null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("document templates may reference only the type's own properties.");
        _registry.Get("Widget").Should().BeNull();
        await _schemaManager.DidNotReceive().ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>());
    }
```

Add, where `RegisterAsync_RetargetingTypeDropsPropertyADependentTemplateReferences_ThrowsFailedPrecondition` was (before `SearchChunks_ForDocumentProperty_SucceedsDespiteUnrelatedFieldPermission`):
```csharp
    // ── Other types' stored templates are not re-validated ──────────────────────

    [Fact]
    public async Task RegisterAsync_ReRegisteringATypeThatALegacyStoredTemplateReferences_Succeeds()
    {
        // A descriptor stored before relation placeholders were refused: Article's parsed template
        // still reads {Author.Bio} through a ManyToOne to User. Registration checks only the types
        // in its own request, so dropping Bio from User is not blocked by that stored template.
        await _registry.RegisterAsync(new SchemaDescriptor
        {
            TypeName      = "Article",
            TableName     = "articles",
            KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
            ScalarColumns = [new ColumnDescriptor("UserId", "UUID", true)],
            FkColumns     = [],
            VectorFields  = [],
            ChunkFields   = [new ChunkDescriptor("Document", 512, 64, "nomic-embed-text", 768)],
            Relations     =
            [
                new Iverson.Api.Schema.RelationDescriptor(
                    "Author", Iverson.Api.Schema.RelationKind.ManyToOne, "User", "UserId")
            ],
            TenantColumn           = SchemaDescriptor.TenantColumnName,
            DocumentTemplate       = DocumentTemplateParser.Parse("{Author.Bio}"),
            DocumentTemplateSource = "{Author.Bio}",
        });
        await _sut.RegisterAsync(
            new SchemaRequest { RootType = SimpleType("User", "Name", "Bio") }, null, CancellationToken.None);

        var act = () => _sut.RegisterAsync(
            new SchemaRequest { RootType = SimpleType("User", "Name") }, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
```

1b. Replace the whole of `Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRendererTests.cs` with the following. The `_entities` field and the two-argument constructor call stay in this step so the file compiles against today's renderer; Step 3 removes them.

```csharp
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class DocumentRendererTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly SchemaRegistry _registry;
    private readonly DocumentRenderer _sut;

    private const string Tenant = "test-tenant";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string WidgetId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string AuthorId  = "22222222-0000-0000-0000-000000000002";
    private static readonly string CategoryA = "44444444-0000-0000-0000-0000000000aa";

    public DocumentRendererTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new DocumentRenderer(_registry, _entities);
    }

    // ── Schema fixture ───────────────────────────────────────────────────────
    // A Widget declares a ManyToOne Author, a ManyToOne Editor, a OneToMany Comments and a
    // ManyToMany Categories. Registration no longer accepts a template that references them, but a
    // descriptor stored before that change can still carry such segments.

    private static SchemaDescriptor WidgetSchema(string template) => new()
    {
        TypeName      = "Widget",
        TableName     = "widgets",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Name",       "TEXT", true),
            new ColumnDescriptor("Rating",     "DOUBLE PRECISION", true),
            new ColumnDescriptor("Count",      "INTEGER", true),
            new ColumnDescriptor("Active",     "BOOLEAN", true),
            new ColumnDescriptor("CreatedAt",  "TIMESTAMPTZ", true),
            new ColumnDescriptor("Tags",       "TEXT[]", true),
            new ColumnDescriptor("Ref",        "UUID", true),
            new ColumnDescriptor("AuthorId",   "UUID", true),
            new ColumnDescriptor("EditorRef",  "UUID", true),
            new ColumnDescriptor("CategoryIds","UUID[]", true),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    =
        [
            new RelationDescriptor("Author",     RelationKind.ManyToOne,  "Author",   "AuthorId"),
            new RelationDescriptor("Editor",     RelationKind.ManyToOne,  "Author",   "EditorRef"),
            new RelationDescriptor("Comments",   RelationKind.OneToMany,  "Comment",  "WidgetId"),
            new RelationDescriptor("Categories", RelationKind.ManyToMany,"Category", "CategoryIds"),
        ],
        TenantColumn       = "TenantId",
        DocumentTemplate       = DocumentTemplateParser.Parse(template),
        DocumentTemplateSource = template,
    };

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    // ── Scalar + literal + escape ────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_ScalarPlaceholder_RendersVerbatimStringValue()
    {
        await _registry.RegisterAsync(WidgetSchema("Name is {Name}."));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Sprocket","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Name is Sprocket.");
    }

    [Fact]
    public async Task RenderAsync_EscapedBrace_RendersLiteralBraceNotAsPlaceholder()
    {
        await _registry.RegisterAsync(WidgetSchema("{{Name}} = {Name}"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Sprocket","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("{Name}} = Sprocket");
    }

    [Fact]
    public async Task RenderAsync_MissingOrNullScalar_RendersEmptyString()
    {
        await _registry.RegisterAsync(WidgetSchema("[{Name}]"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":null,"TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("[]");
    }

    // ── Each scalar type's invariant rendering ───────────────────────────────

    [Fact]
    public async Task RenderAsync_AllScalarTypes_RenderCultureInvariantly()
    {
        await _registry.RegisterAsync(
            WidgetSchema("{Name}|{Rating}|{Count}|{Active}|{CreatedAt}|{Ref}|{Tags}"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""
                {
                  "Id":"{{WidgetId}}",
                  "Name":"Sprocket",
                  "Rating":3.5,
                  "Count":42,
                  "Active":true,
                  "CreatedAt":"2026-08-21T10:15:30Z",
                  "Ref":"22222222-0000-0000-0000-000000000002",
                  "Tags":["a","b","c"],
                  "TenantId":"{{Tenant}}"
                }
                """),
            Tenant, Ct);

        result.Should().Be(
            "Sprocket|3.5|42|true|2026-08-21T10:15:30Z|22222222-0000-0000-0000-000000000002|a, b, c");
    }

    // ── Case-insensitive resolution (final-review Finding 1) ────────────────

    [Fact]
    public async Task RenderAsync_Scalar_PropertyNameCaseMismatch_ResolvesCaseInsensitively()
    {
        // Template spells the scalar "{NAME}" in a case matching neither the declared column
        // ("Name") nor its camelCase form ("name").
        await _registry.RegisterAsync(WidgetSchema("Value: {NAME}"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Widget One","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Value: Widget One");
    }

    // ── Legacy relation segments ─────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_StoredTemplateWithRelationSegments_RendersThemEmptyIncludingBlockLiterals()
    {
        // Loaded from stored JSON exactly as SchemaRegistry.LoadAsync loads it, which does not
        // re-validate templates, so the parsed OneHop and Block segments survive. Each renders as
        // nothing, including a block's own literal text.
        const string template =
            "Name: {Name}|By {Author.Name}|{#Comments}prefix-{Body}{/Comments}|{#Categories}{Label}{/Categories}|end";
        var repository = Substitute.For<ISchemaRegistryRepository>();
        repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Widget", JsonSerializer.Serialize(
                WidgetSchema(template),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
        });
        var stored = new SchemaRegistry(repository, NullLogger<SchemaRegistry>.Instance);
        await stored.LoadAsync();
        var schema = stored.Get("Widget")!;
        schema.DocumentTemplate!.Segments.Select(s => s.Kind).Should()
            .Contain(DocumentSegmentKind.OneHop).And.Contain(DocumentSegmentKind.Block);

        var result = await _sut.RenderAsync(
            schema,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Sprocket","AuthorId":"{{AuthorId}}","CategoryIds":["{{CategoryA}}"],"TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Name: Sprocket|By |||end");
    }
}
```

1c. `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — add after `HandleCreated_DocumentFieldBecomesEmpty_DeletesFieldScopedPointsButLeavesOtherFieldUntouched`:

```csharp
    [Fact]
    public async Task HandleCreated_LegacyTemplateWithRelationSegments_RendersOwnPropertiesOnly_AndFetchesNoRelatedRows()
    {
        // A descriptor stored before relation placeholders were refused at registration, round-
        // tripped through JSON the way SchemaRegistry stores it, still carries parsed OneHop and
        // Block segments. Both relation targets are registered and the entity store would answer
        // for them, so any related-row fetch, or any related text in the document, shows up below.
        const string relatedId = "00000000-0000-0000-0000-000000000002";
        var legacy = TemplateOnlyDocSchema() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Tagline", "text", true),
                new ColumnDescriptor("AuthorId", "uuid", true),
                new ColumnDescriptor("TagIds", "uuid[]", true)
            ],
            Relations =
            [
                new RelationDescriptor("Author", RelationKind.ManyToOne, "Author", "AuthorId"),
                new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")
            ],
            DocumentTemplate = DocumentTemplateParser.Parse("{Tagline}{Author.Name}{#Tags}[{Label}]{/Tags}")
        };
        var stored = JsonSerializer.Deserialize<SchemaDescriptor>(JsonSerializer.Serialize(legacy, JsonOptions), JsonOptions)!;
        await _registry.RegisterAsync(stored);
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.TagSchema());

        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<EntityAccess>())
            .Returns([new KeyedRow(relatedId, $$"""{"Id":"{{relatedId}}","Name":"Related name","Label":"Related label"}""")]);
        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns([$$"""{"Id":"{{relatedId}}","Name":"Related name","Label":"Related label"}"""]);

        var payloads = new List<IReadOnlyDictionary<string, object>>();
        _vectorWrite
            .UpsertNamedAsync(
                Arg.Any<string>(),
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => { if (p is not null) payloads.Add(p); }))
            .Returns(Task.CompletedTask);

        var ev = TemplatedDocEvent(
            Guid.NewGuid().ToString(),
            $$"""{"Tagline":"Plain tagline","AuthorId":"{{relatedId}}","TagIds":["{{relatedId}}"],"TenantId":"test-tenant"}""",
            "trace-legacy-relation-template");

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        payloads
            .Where(p => p.TryGetValue("field", out var field) && Equals(field, "Document"))
            .Select(p => p["text"])
            .Should().Equal("Plain tagline");
        await _entities.DidNotReceiveWithAnyArgs().FetchManyByKeysAsync(default!, default!, default);
        await _entities.DidNotReceiveWithAnyArgs().FetchByColumnAsync(default!, default!, default!, default);
    }
```

1d. `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — nothing new here; the `GetDependents` test is deleted in Step 3, together with `GetDependents`.

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Schema.DocumentTemplateValidationTests|FullyQualifiedName~Iverson.Api.Tests.Consumers.DocumentRendererTests|FullyQualifiedName~Iverson.Api.Tests.Consumers.IntelligenceStoreConsumerTests"
```
Expected failures:
- `RegisterAsync_TemplateWithARelationPlaceholder_ThrowsInvalidArgument` (3 cases): the registrations succeed today.
- `RegisterAsync_TemplateReferencesUndeclaredRelation_ThrowsInvalidArgument`: today's message is "…which is not a declared relation."
- `RegisterAsync_ReRegisteringATypeThatALegacyStoredTemplateReferences_Succeeds`: phase 2 answers FailedPrecondition.
- `HandleCreated_LegacyTemplateWithRelationSegments_RendersOwnPropertiesOnly_AndFetchesNoRelatedRows`: the document text includes the related name and label, and `FetchManyByKeysAsync` is received.

`RenderAsync_StoredTemplateWithRelationSegments_RendersThemEmptyIncludingBlockLiterals` passes already: its relation targets are not registered in the renderer's registry, so today's renderer also finds nothing. It pins the renderer-level shape for Step 3; the consumer test above carries the red.

- [ ] **Step 3: Implement**

3a. `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`.

Replace (orig. `:58-61`):
```csharp
        // Phase 1: build + per-type validate. No DDL, no registry writes — a root's document
        // template can reference a dependent that hasn't been built yet if this were a single
        // pass, and applying/registering before every type in the request is known-good would
        // persist an invalid template.
```
with:
```csharp
        // Phase 1: build + per-type validate. No DDL, no registry writes — applying/registering
        // before every type in the request is known-good would persist part of a request that is
        // then rejected.
```

Replace (orig. `:102-104`, inside the phase-1 loop; the phase-2 `effectiveDescriptors` it names is deleted below):
```csharp
            // descriptor by the time a second is reached (populated below, after BuildDescriptor), so
            // checking it first mirrors the phase-2 cross-validation's effectiveDescriptors move: the
            // registry alone is stale during a batch.
```
with:
```csharp
            // descriptor by the time a second is reached (populated below, after BuildDescriptor), so
            // it is checked first: the registry alone is stale during a batch.
```

Replace (orig. `:266-291`):
```csharp
        // Phase 2: cross-type validate. Every type in this request is now built, so a root's
        // {Rel.Prop} reference into a dependent (in either declaration order) can resolve.
        // Resolution also reaches types already registered from an earlier call, not just this
        // request's batch — effectiveDescriptors is the registry's current view with this
        // request's freshly-built descriptors overlaid on top.
        var effectiveDescriptors = new Dictionary<string, SchemaDescriptor>(registry.All, StringComparer.OrdinalIgnoreCase);
        foreach (var (typeName, descriptor) in batchDescriptors)
            effectiveDescriptors[typeName] = descriptor;

        // This request's own descriptors: a validation failure here is this request's own bad
        // submission.
        foreach (var descriptor in descriptors)
            ValidateDocumentTemplate(descriptor, effectiveDescriptors, StatusCode.InvalidArgument);

        // Every OTHER already-registered type that carries a document template: this request
        // didn't touch it directly, but it may reference (via a one-hop/block relation) a type
        // this request just changed. If its template no longer resolves against the effective
        // view, this request is breaking an established contract — FailedPrecondition, the same
        // status SchemaDriftException already uses below for breaking an existing schema.
        foreach (var (typeName, descriptor) in effectiveDescriptors)
        {
            if (batchDescriptors.ContainsKey(typeName) || descriptor.DocumentTemplate is null)
                continue;

            ValidateDocumentTemplate(descriptor, effectiveDescriptors, StatusCode.FailedPrecondition);
        }
```
with:
```csharp
        // Phase 2: document templates. A template may reference only its own type's scalar
        // properties, so each is checked against its own descriptor; no other registered type's
        // template depends on anything this request changes.
        foreach (var descriptor in descriptors)
            ValidateDocumentTemplate(descriptor);
```

Replace the block from the comment above `ValidateDocumentTemplate` through the end of `RequireTargetDescriptor` (orig. `:366-530`: `ValidateDocumentTemplate`, `ValidateDocumentSegment`, `RequireScalarProperty`, `RequireNotRowOwned`, `RequireRelation`, `RequireTargetDescriptor`) with:
```csharp
    // Sits beside ValidateFieldReference/ValidateEnrichmentTargets. The parser (T1) knows
    // nothing about schemas — this is where "does this property actually exist" is enforced.
    private static void ValidateDocumentTemplate(SchemaDescriptor descriptor)
    {
        if (descriptor.DocumentTemplate is null)
            return;

        // "Document", "document", and "DOCUMENT" all derive "document_vector".
        var duplicate = descriptor.ChunkFields
            .GroupBy(c => c.PropertyName.ToSnakeCase(), StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"'{descriptor.TypeName}' declares chunk fields " +
                $"{string.Join(", ", duplicate.Select(c => $"'{c.PropertyName}'"))} that all derive the same " +
                $"Qdrant vector name '{duplicate.Key}_vector'."));
        }

        // The companion rule needs no code: RowFieldAuthorizationEvaluator's allFields already
        // concatenates ChunkFields property names, so "Document" lands in AllowedFields by
        // construction once a FieldPermission can never exclude it. Reject only the exclusion.
        if (descriptor.Authorization?.FieldPermissions.Any(fp =>
                string.Equals(fp.FieldName, "Document", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"'{descriptor.TypeName}' declares a FieldPermission naming 'Document', which is reserved for " +
                "the synthetic document chunk field and can never be excluded from a caller's AllowedFields."));
        }

        foreach (var segment in descriptor.DocumentTemplate.Segments)
            ValidateDocumentSegment(segment, descriptor);
    }

    private static void ValidateDocumentSegment(DocumentSegment segment, SchemaDescriptor declaring)
    {
        switch (segment.Kind)
        {
            case DocumentSegmentKind.Literal:
                break;

            case DocumentSegmentKind.Scalar:
                RequireScalarProperty(declaring, segment.PropertyName!);
                break;

            // Relation placeholders were removed: the only targets the old guard admitted were
            // types whose rows no caller may read. The parser still produces these segments and
            // stored descriptors still carry them; DocumentRenderer renders them as empty.
            case DocumentSegmentKind.OneHop:
            case DocumentSegmentKind.Block:
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Document template on '{declaring.TypeName}' references relation '{segment.RelationName}': " +
                    "document templates may reference only the type's own properties."));
        }
    }

    // Resolves a top-level {Prop} against the declaring type.
    private static void RequireScalarProperty(SchemaDescriptor context, string propertyName)
    {
        // ScalarColumns position: EXCLUDE __TenantId. A template referencing {__TenantId} would
        // render the server-owned tenant value into the chunk text that SearchChunks returns
        // verbatim, putting the value back on the wire and defeating the whole "server owns the
        // tenant column, it never appears on the wire" decision. Excluded at the lookup rather than
        // special-cased below so the rejection is the SAME "not a declared scalar property" error
        // any unknown name gets — the reserved name is not even distinguishable from a typo.
        var column = context.ScalarColumns.FirstOrDefault(c =>
            !SchemaDescriptor.IsTenantColumn(c.Name) &&
            string.Equals(c.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Document template references property '{propertyName}' on '{context.TypeName}', which is not " +
                "a declared scalar property."));
        }

        if (context.Authorization?.FieldPermissions.Any(fp =>
                string.Equals(fp.FieldName, propertyName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Document template references property '{propertyName}' on '{context.TypeName}', which " +
                "carries a FieldPermission; a document template cannot selectively exclude fields per caller."));
        }
    }
```

In `RejectReservedTenantName(TypeDescriptor)`, replace (orig. `:590-594`):
```csharp
        //   CHECKED ELSEWHERE: TenantField (RejectDeclaredTenantField, run immediately before
        //     this); DocumentTemplate placeholders ({Prop}, {Rel.Prop}, {#Rel}) —
        //     RequireScalarProperty resolves scalar names against ScalarColumns with __TenantId
        //     EXCLUDED, and the relation half resolves against Relations[].PropertyName, which
        //     this method now covers.
```
with:
```csharp
        //   CHECKED ELSEWHERE: TenantField (RejectDeclaredTenantField, run immediately before
        //     this); DocumentTemplate placeholders — RequireScalarProperty resolves {Prop} against
        //     ScalarColumns with __TenantId EXCLUDED, and relation placeholders ({Rel.Prop},
        //     {#Rel}) are rejected outright by ValidateDocumentSegment.
```

3b. Replace the whole of `Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs` with:
```csharp
using System.Text;
using System.Text.Json;
using Iverson.Api.Schema;

namespace Iverson.Api.Consumers;

/// <summary>
/// Renders a type's parsed <see cref="DocumentTemplate"/> (T1) into plain text by substituting
/// its own scalar properties. Registration validates every placeholder; this type does not
/// re-validate any of it.
///
/// Relation placeholders (<c>{Rel.Prop}</c> and <c>{#Rel}…{/Rel}</c>) are refused at
/// registration, but a descriptor stored before that change still carries the parsed
/// <see cref="DocumentSegmentKind.OneHop"/> and <see cref="DocumentSegmentKind.Block"/> segments,
/// and <c>SchemaRegistry.LoadAsync</c> does not re-validate templates. Both render as nothing —
/// a block's own literal text included — and nothing is fetched for them, so such a template
/// stops copying related data on its next render.
///
/// Rendering is deterministic and culture-invariant (see the plan's Global Constraints): the
/// same row must render byte-identically on any node at any time. Scalar formatting never
/// touches <see cref="System.Globalization.CultureInfo"/> — it reads JSON literals, which are
/// culture-agnostic by construction.
/// </summary>
public sealed class DocumentRenderer
{
    // Task-returning so IntelligenceStoreConsumer's call site is unchanged; nothing here awaits.
    public Task<string> RenderAsync(
        SchemaDescriptor schema, JsonElement payload, string tenantId, CancellationToken ct)
    {
        var template = schema.DocumentTemplate;
        if (template is null) return Task.FromResult(string.Empty);

        var sb = new StringBuilder();
        foreach (var segment in template.Segments)
        {
            switch (segment.Kind)
            {
                case DocumentSegmentKind.Literal:
                    sb.Append(segment.Text);
                    break;

                case DocumentSegmentKind.Scalar:
                    sb.Append(FormatProperty(payload, segment.PropertyName!));
                    break;

                case DocumentSegmentKind.OneHop:
                case DocumentSegmentKind.Block:
                    // Legacy relation segments render as nothing; see the class doc.
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(segment.Kind), segment.Kind,
                        $"Unhandled {nameof(DocumentSegmentKind)} value — add a case above.");
            }
        }

        return Task.FromResult(sb.ToString());
    }

    private static string FormatProperty(JsonElement context, string propertyName)
    {
        var value = ExtractElement(context, propertyName);
        return value is null ? string.Empty : FormatScalar(value.Value);
    }

    // Fixed per type, culture-invariant (Global Constraints). Every value the renderer ever
    // reads arrives as a JsonElement produced by System.Text.Json — which serializes Guid as
    // lowercase-D, DateTime/DateTimeOffset as ISO 8601, and numerics without group separators,
    // all independent of thread culture — so JsonValueKind alone is enough to reproduce the
    // required formatting without inspecting the declared object/SQL type:
    //   string        -> verbatim
    //   number/bool   -> the raw JSON literal (already invariant, no group separators)
    //   array         -> elements formatted the same way, joined with ", "
    //   null/missing  -> empty string
    private static string FormatScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array  => string.Join(", ", value.EnumerateArray().Select(FormatScalar)),
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => value.GetRawText(),
        _ => string.Empty
    };

    private static JsonElement? ExtractElement(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var v)) return v;

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (payload.TryGetProperty(camel, out var vc)) return vc;

        foreach (var prop in payload.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        }

        return null;
    }
}
```
`Program.cs:342` (`builder.Services.AddSingleton<DocumentRenderer>();`) needs no change.

3c. Constructor call sites:
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` (in `BuildSut`): `new DocumentRenderer(_registry, _entities),` → `new DocumentRenderer(),`
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs:275`: `new DocumentRenderer(_registry, entities),` → `new DocumentRenderer(),`
- `Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRendererTests.cs`: delete the line `private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();` and replace `_sut = new DocumentRenderer(_registry, _entities);` with `_sut = new DocumentRenderer();`.

3d. Delete the relation-dependent enqueueing and what only it used:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git rm Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs
```

`Iverson.Server/Iverson.Api/Program.cs` — delete this line (`:453` at `1b7968aa`; Task 3's edit to `:267-306` shifts it, so match on the text):
```csharp
    builder.Services.AddHostedService<Iverson.Api.Consumers.DocumentRerenderConsumer>();
```

`Iverson.Server/docker-compose.yml` — in the `iverson-worker` comment (`:597-599` at `1b7968aa`; Task 4 edits other hunks of this file first, so match on the text), replace:
```yaml
  # gated on `workloadRole == "worker"` (Iverson.Api/Program.cs) — IntelligenceStoreConsumer,
  # DocumentRerenderConsumer, the DLQ/reconciliation workers, and AddEngagementStoreConsumer's
```
with:
```yaml
  # gated on `workloadRole == "worker"` (Iverson.Api/Program.cs) — IntelligenceStoreConsumer,
  # the DLQ/reconciliation and document re-render workers, and AddEngagementStoreConsumer's
```

`Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`:
- delete lines 14-20 (the `// Reverse-dependency index: …` comment, the `_reverseIndex` field, and the blank line after it);
- delete lines 75-81 plus the following blank line (the `GetDependents` doc comment and method, from `/// <summary>` / `/// The (declaringType, relation) pairs whose document template references …` to `_reverseIndex.TryGetValue(typeName, out var deps) ? deps : [];`);
- in `LoadAsync`, delete `        RebuildReverseIndex();` and the blank line after it (just above `logger.LogInformation("SchemaRegistry loaded {Count} schema(s)", _schemas.Count);`);
- in `RegisterAsync`, delete `        RebuildReverseIndex();` (between `_schemas[descriptor.TypeName] = descriptor;` and `logger.LogInformation("Registered schema for {TypeName}", descriptor.TypeName);`);
- delete lines 258-291 at `1b7968aa` (266-299 after Task 5; locate by the quoted text) plus the blank line after (the `// Derived from schema.DocumentTemplate.Segments …` comment and the whole `RebuildReverseIndex` method), leaving `s_jsonOptions` as the last member.

`Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` — delete `LoadAsync_RebuildsReverseIndex_SoStartupRehydratedTemplateDependentsAreFound` (the whole `[Fact]`, orig. `:210-246`).

`Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` — delete line 170:
```csharp
    Task<IEnumerable<string>> FetchByArrayContainsAsync(TableSchema schema, string columnName, string value, EntityAccess access);
```

`Iverson.Server/Iverson.Sql/EntityRepository.cs` — delete the `// ManyToMany reverse lookup: …` comment (6 lines) and the `FetchByArrayContainsAsync` method Task 5 wrote, through its closing brace and the blank line after:
```csharp
    // ManyToMany reverse lookup: the FK column is a uuid[] living on the DECLARING row (the
    // opposite direction from FetchByColumnAsync's OneToMany usage), so finding declaring rows
    // whose array contains a given target key needs Postgres array containment (@>), not
    // equality. Guid[], not string[] — Npgsql sends string[] as text[], which blocks Postgres
    // from using the uuid[] GIN/index for @> — see this plan's Global Constraints and
    // FetchManyByKeysAsync above.
    public Task<IEnumerable<string>> FetchByArrayContainsAsync(TableSchema schema, string columnName, string value, EntityAccess access)
    {
        var table  = PostgresIdentifier.Quote(schema.TableName);
        var column = PostgresIdentifier.Quote(columnName);
        return sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM {table} t WHERE {column} @> @Keys",
            new { Keys = new[] { Guid.Parse(value) } }, access.Role, access.TenantId);
    }
```

3e. Comments that name the deleted code.

`Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` — replace `:160-164`:
```csharp
/// Structured on <see cref="DocumentRerenderConsumer"/>'s exact pattern for OneToMany FK
/// extraction (current + prior payload, old/new parent on reassignment) and its Created/Updated-
/// vs-Deleted tenant resolution split — but keyed off the configured signal list (via
/// <see cref="SchemaDescriptor.Relations"/>) instead of <see cref="SchemaRegistry.GetDependents"/>,
/// since only explicitly configured relations feed a popularity count.
```
with:
```csharp
/// Structured on the former <c>DocumentRerenderConsumer</c>'s pattern (removed with document-
/// template relation traversal) for OneToMany FK extraction (current + prior payload, old/new
/// parent on reassignment) and its Created/Updated-vs-Deleted tenant resolution split — keyed off
/// the configured signal list (via <see cref="SchemaDescriptor.Relations"/>), since only
/// explicitly configured relations feed a popularity count.
```
replace `:211-212`:
```csharp
            // mirrors DocumentRerenderConsumer.cs:68 — an unresolvable tenant must never reach a
            // StarRocks-bound call downstream
```
with:
```csharp
            // An unresolvable tenant must never reach a StarRocks-bound call downstream.
```
delete `:276`:
```csharp
    // Identical shape to DocumentRerenderConsumer.cs:195.
```
and replace `:292-295`:
```csharp
    // A deliberate additional copy of the pattern in DocumentRerenderConsumer.cs (and
    // DocumentRenderer.cs, EnrichmentConsumer.cs, IntelligenceStoreConsumer.cs) — the spec
    // authorizes adding a copy here rather than extracting a shared helper and touching any of
    // those files.
```
with:
```csharp
    // A deliberate additional copy of the pattern in EnrichmentConsumer.cs and
    // IntelligenceStoreConsumer.cs — the spec authorizes adding a copy here rather than
    // extracting a shared helper and touching either of those files.
```

`Iverson.Server/Iverson.Api/Reconciliation/DocumentRerenderQueueWorker.cs` — replace `:16-20`:
```csharp
/// Every republished event carries <see cref="EntityEvent.SuppressRerenderCascade"/> = true and
/// <see cref="StoreTarget.Intelligence"/> only: this is a document re-render, not a full
/// re-projection, so it must not re-run the Postgres/StarRocks projections, and it must not be
/// picked back up by <see cref="DocumentRerenderConsumer"/> as a fresh dependency change — that
/// would be an infinite loop.
```
with:
```csharp
/// Every republished event carries <see cref="EntityEvent.SuppressRerenderCascade"/> = true and
/// <see cref="StoreTarget.Intelligence"/> only: this is a document re-render, not a full
/// re-projection, so it must not re-run the Postgres/StarRocks projections. The flag's former
/// reader, relation-dependent re-render detection, was removed; the flag still marks a republish
/// as a re-render rather than a client write.
```

In `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, replace the comment line
```
    // falling back to registry.Get, mirroring phase 2's effectiveDescriptors move.
```
with
```
    // falling back to registry.Get: the registry alone is stale during a batch.
```

Then confirm nothing still names the removed members (expected: no output):
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && command grep -rn --include=*.cs -e 'GetDependents' -e 'RebuildReverseIndex' -e 'FetchByArrayContainsAsync' -e 'RequireNotRowOwned' -e 'RequireTargetDescriptor' -e 'new DocumentRenderer(_' -e 'cref="DocumentRerenderConsumer' -e 'effectiveDescriptors' . | command grep -v '/bin/\|/obj/'; command grep -n 'DocumentRerenderConsumer' docker-compose.yml
```

- [ ] **Step 4: Run the tests and see them pass**

The Step 2 command, adding `SchemaRegistryTests`:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Schema.DocumentTemplateValidationTests|FullyQualifiedName~Iverson.Api.Tests.Consumers.DocumentRendererTests|FullyQualifiedName~Iverson.Api.Tests.Consumers.IntelligenceStoreConsumerTests|FullyQualifiedName~Iverson.Api.Tests.Schema.SchemaRegistryTests"
```
Expected: all pass. Then the container-free regressions, one at a time:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Sql.Tests/Iverson.Sql.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures. Against the Task 5 baseline, the Api count drops by the deleted tests (16 in `DocumentRendererTests`, 10 in `DocumentTemplateValidationTests`, 19 in `DocumentRerenderConsumerTests` — 16 facts plus the 3-case `Dispatch_AllEventTypes_EnqueueDependent` theory — and 1 in `SchemaRegistryTests`, 46 in all) and rises by 6 cases (the 3-case relation-placeholder theory, the legacy re-registration fact, the legacy consumer fact, the legacy renderer fact; the re-pointed undeclared-relation fact is not new): a net drop of 40. Record both counts and check the difference against this. `ObjectSearchVectorIntegrationTests` only needs to compile here.

- [ ] **Step 5: Mutation proof**

Before the first mutation, run Step 6's `git add …` command now (not the `git commit`), so that `git checkout -- <file>` restores this task's staged version, new files included. Apply each alone, run the Step 4 filtered command, record the failing tests, revert with `git checkout -- <file>`.

| # | Mutation (one line) | Must fail |
|---|---|---|
| M1 | Orchestrator `ValidateDocumentSegment`: replace the OneHop/Block `throw new RpcException(…)` statement with `break;` | `RegisterAsync_TemplateWithARelationPlaceholder_ThrowsInvalidArgument` (3 cases); `RegisterAsync_TemplateReferencesUndeclaredRelation_ThrowsInvalidArgument` |
| M2 | Orchestrator phase 2: `foreach (var descriptor in descriptors)` → `foreach (var descriptor in registry.All.Values.Concat(descriptors))` (re-validates other registered types' templates) | `RegisterAsync_ReRegisteringATypeThatALegacyStoredTemplateReferences_Succeeds` |
| M3 | DocumentRenderer: in the OneHop/Block arm, replace `break;` with `sb.Append(segment.RelationName); break;` | `RenderAsync_StoredTemplateWithRelationSegments_RendersThemEmptyIncludingBlockLiterals`; `HandleCreated_LegacyTemplateWithRelationSegments_RendersOwnPropertiesOnly_AndFetchesNoRelatedRows` |

The no-fetch property is structural: `DocumentRenderer` no longer takes an entity store. The consumer test's `DidNotReceiveWithAnyArgs` assertions are what fail if a fetch is ever reintroduced on that path.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git add \
  Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
  Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs \
  Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs \
  Iverson.Server/Iverson.Api/Reconciliation/DocumentRerenderQueueWorker.cs \
  Iverson.Server/Iverson.Api/Program.cs \
  Iverson.Server/docker-compose.yml \
  Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs \
  Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs \
  Iverson.Server/Iverson.Sql/EntityRepository.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs \
  Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRendererTests.cs \
  Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs \
  Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
git commit -m "refuse relation placeholders in document templates and render legacy ones empty

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```
(The two deleted files were staged by `git rm` in Step 3.)

---

### Task 7: Enrichment target field permissions

**Spec:** §4c (closes #11)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — `ValidateEnrichmentTargets` (orig. `:718-794`): the leading comment's rule count, and two rules appended inside its `if (descriptor.EnrichmentTargets.Count > 0)` block. No other task touches this method.
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — a fixture builder and six tests (one a two-case theory) after `RegisterAsync_WithEmptyExtractHint_ThrowsInvalidArgument`.

**Interfaces:**
- Consumes: the orchestrator as Tasks 5 and 6 leave it (neither edits `ValidateEnrichmentTargets`).
- Produces: nothing other tasks use.

- [ ] **Step 1: Write the failing tests**

Add to `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`, after `RegisterAsync_WithEmptyExtractHint_ThrowsInvalidArgument`. These follow that file's existing enrichment tests (`RegisterAsync_WithEnrichmentTargetAndChunkSourceProperty_DoesNotThrow` is the shape: a chunk source `Body` and a summary target `Summary`) and its FieldPermission initializer style (`RegisterAsync_WithAFieldPermissionNamingTheServerOwnedTenantColumn_ThrowsInvalidArgument`).

```csharp
    // ── Enrichment targets and FieldPermissions ────────────────────────────────
    // Body is the chunk source and Summary the enrichment target in every case below; only the
    // FieldPermissions vary.
    private static TypeDescriptor EnrichedWidget(params Client.Contracts.FieldPermission[] fieldPermissions)
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Body", ObjectType = ObjectType.String, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Summary", ObjectType = ObjectType.String, IsSummaryTarget = true });
        td.Authorization = new Client.Contracts.AuthorizationRules();
        foreach (var fieldPermission in fieldPermissions)
            td.Authorization.FieldPermissions.Add(fieldPermission);
        return td;
    }

    [Fact]
    public async Task RegisterAsync_EnrichmentTargetWithWritableRoles_ThrowsInvalidArgument()
    {
        // "summary", not "Summary": a FieldPermission names a property case-insensitively, as the
        // evaluator matches it.
        var td = EnrichedWidget(
            new Client.Contracts.FieldPermission { FieldName = "summary", WritableRoles = { "editors" } });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("cannot be enforced");
        _registry.Get("Widget").Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterAsync_EnrichmentTargetMoreReadableThanItsSource_ThrowsInvalidArgument(
        bool targetHasUnrestrictedPermission)
    {
        // A FieldPermission whose readable_roles list is empty restricts nothing, so it counts the
        // same as no permission at all.
        var permissions = new List<Client.Contracts.FieldPermission>
        {
            new Client.Contracts.FieldPermission { FieldName = "Body", ReadableRoles = { "editors" } }
        };
        if (targetHasUnrestrictedPermission)
            permissions.Add(new Client.Contracts.FieldPermission { FieldName = "Summary" });
        var td = EnrichedWidget(permissions.ToArray());

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("a subset of them");
        _registry.Get("Widget").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_EnrichmentPermissionsWithEmptyRoleLists_Registers()
    {
        // Empty role lists restrict nothing: the source restricts only writes, and the target's
        // permission restricts nothing at all, so every reader of the target can read the source.
        var td = EnrichedWidget(
            new Client.Contracts.FieldPermission { FieldName = "Body", WritableRoles = { "editors" } },
            new Client.Contracts.FieldPermission { FieldName = "Summary" });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _registry.Get("Widget").Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_EnrichmentTargetRoleDifferingFromTheSourceOnlyByCase_ThrowsInvalidArgument()
    {
        // Role names compare ordinally, as the evaluator compares them with the caller's groups:
        // "Editors" and "editors" are different groups.
        var td = EnrichedWidget(
            new Client.Contracts.FieldPermission { FieldName = "Body", ReadableRoles = { "editors" } },
            new Client.Contracts.FieldPermission { FieldName = "Summary", ReadableRoles = { "Editors" } });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("a subset of them");
    }

    [Fact]
    public async Task RegisterAsync_EnrichmentTargetCoveringOnlyOneOfTwoSourcePermissions_ThrowsInvalidArgument()
    {
        // Two permissions name Body, so a reader must pass both. The target is covered against the
        // first only: a caller in "editors" but not "reviewers" could read the summary of a body
        // they cannot read. "body" is lower-cased on purpose (names match case-insensitively).
        var td = EnrichedWidget(
            new Client.Contracts.FieldPermission { FieldName = "Body", ReadableRoles = { "editors" } },
            new Client.Contracts.FieldPermission { FieldName = "body", ReadableRoles = { "reviewers" } },
            new Client.Contracts.FieldPermission { FieldName = "Summary", ReadableRoles = { "editors" } });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("a subset of them");
    }

    [Fact]
    public async Task RegisterAsync_EnrichmentTargetNoMoreReadableThanItsSource_Registers()
    {
        // The target's readers are a subset of the source's. "summary" is lower-cased on purpose.
        var td = EnrichedWidget(
            new Client.Contracts.FieldPermission { FieldName = "Body", ReadableRoles = { "editors", "reviewers" } },
            new Client.Contracts.FieldPermission { FieldName = "summary", ReadableRoles = { "editors" } });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _registry.Get("Widget").Should().NotBeNull();
    }
```

- [ ] **Step 2: Run them and see them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.SchemaRegistrationOrchestratorTests"
```
Expected failures (no rule exists, so these register): `RegisterAsync_EnrichmentTargetWithWritableRoles_ThrowsInvalidArgument`, both cases of `RegisterAsync_EnrichmentTargetMoreReadableThanItsSource_ThrowsInvalidArgument`, `RegisterAsync_EnrichmentTargetRoleDifferingFromTheSourceOnlyByCase_ThrowsInvalidArgument`, `RegisterAsync_EnrichmentTargetCoveringOnlyOneOfTwoSourcePermissions_ThrowsInvalidArgument`. The two `…_Registers` tests pass already; they guard against over-rejection in Step 3.

- [ ] **Step 3: Implement**

In `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`, replace the comment above `ValidateEnrichmentTargets`:
```csharp
    // Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
    // are columns the enrichment pipeline writes to, not reads from. Five rules keep that
    // invariant sound at registration time rather than failing obscurely at ingest.
```
with:
```csharp
    // Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
    // are columns the enrichment pipeline writes to, not reads from. Five rules keep that
    // invariant sound at registration time rather than failing obscurely at ingest, and two more
    // keep a target's FieldPermissions consistent with that: the pipeline is its only writer, and
    // its text is derived from the source properties.
```

Replace the end of the method:
```csharp
            if (!hasSourceProperty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"'{descriptor.TypeName}' declares enrichment targets but has no source text property " +
                    "for the enrichment pipeline to read from."));
            }
        }
    }
```
with:
```csharp
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
(`targetNames`, declared at the top of the same `if` block with `StringComparer.OrdinalIgnoreCase`, has no other reader today; rule 1 uses it.)

- [ ] **Step 4: Run the tests and see them pass**

The Step 2 command. Expected: all pass, including the 7 new cases and every existing enrichment test (none declares Authorization). Then the container-free regression:
```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```
Expected: 0 failures (no registered test model combines enrichment with a FieldPermission these rules reject).

- [ ] **Step 5: Mutation proof**

Before the first mutation, run Step 6's `git add …` command now (not the `git commit`), so that `git checkout -- <file>` restores this task's staged version, new files included. Apply each alone, run the Step 2 command, record the failing tests, revert with `git checkout -- Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`.

| # | Mutation (one line) | Must fail |
|---|---|---|
| M1 | `fp.WritableRoles.Count > 0 && targetNames.Contains(fp.FieldName)` → `false` | `RegisterAsync_EnrichmentTargetWithWritableRoles_ThrowsInvalidArgument` |
| M2 | same expression → `targetNames.Contains(fp.FieldName)` (drops the non-empty test) | `RegisterAsync_EnrichmentPermissionsWithEmptyRoleLists_Registers`; `RegisterAsync_EnrichmentTargetCoveringOnlyOneOfTwoSourcePermissions_ThrowsInvalidArgument`; `RegisterAsync_EnrichmentTargetMoreReadableThanItsSource_ThrowsInvalidArgument(targetHasUnrestrictedPermission: True)`; `RegisterAsync_EnrichmentTargetNoMoreReadableThanItsSource_Registers`; `RegisterAsync_EnrichmentTargetRoleDifferingFromTheSourceOnlyByCase_ThrowsInvalidArgument` (5 in all) |
| M3 | `.ToHashSet(StringComparer.OrdinalIgnoreCase)` → `.ToHashSet(StringComparer.Ordinal)` on **`targetNames`** | `RegisterAsync_EnrichmentTargetWithWritableRoles_ThrowsInvalidArgument` |
| M4 | source filter `fp.ReadableRoles.Count > 0 && sourceNames.Contains(fp.FieldName)` → `sourceNames.Contains(fp.FieldName)` | `RegisterAsync_EnrichmentPermissionsWithEmptyRoleLists_Registers` |
| M5 | `.ToHashSet(StringComparer.OrdinalIgnoreCase)` → `.ToHashSet(StringComparer.Ordinal)` on **`sourceNames`** | `RegisterAsync_EnrichmentTargetCoveringOnlyOneOfTwoSourcePermissions_ThrowsInvalidArgument` |
| M6 | `foreach (var source in fieldPermissions.Where(…))` → `foreach (var source in fieldPermissions.Where(…).Take(1))` | `RegisterAsync_EnrichmentTargetCoveringOnlyOneOfTwoSourcePermissions_ThrowsInvalidArgument` |
| M7 | in `covered`, delete the line `fp.ReadableRoles.Count > 0 &&` | `RegisterAsync_EnrichmentTargetMoreReadableThanItsSource_ThrowsInvalidArgument(targetHasUnrestrictedPermission: True)` |
| M8 | in `covered`, `StringComparison.OrdinalIgnoreCase` → `StringComparison.Ordinal` | `RegisterAsync_EnrichmentTargetNoMoreReadableThanItsSource_Registers` |
| M9 | `source.ReadableRoles.Contains(role, StringComparer.Ordinal)` → `source.ReadableRoles.Contains(role, StringComparer.OrdinalIgnoreCase)` | `RegisterAsync_EnrichmentTargetRoleDifferingFromTheSourceOnlyByCase_ThrowsInvalidArgument` |
| M10 | `fp.ReadableRoles.All(role => source.ReadableRoles.Contains(role, StringComparer.Ordinal))` → `source.ReadableRoles.All(role => fp.ReadableRoles.Contains(role, StringComparer.Ordinal))` (subset inverted) | `RegisterAsync_EnrichmentTargetNoMoreReadableThanItsSource_Registers` |
| M11 | `if (!covered)` → `if (false)` | `RegisterAsync_EnrichmentTargetMoreReadableThanItsSource_ThrowsInvalidArgument` (both cases); `RegisterAsync_EnrichmentTargetCoveringOnlyOneOfTwoSourcePermissions_ThrowsInvalidArgument`; `RegisterAsync_EnrichmentTargetRoleDifferingFromTheSourceOnlyByCase_ThrowsInvalidArgument` (4 in all) |

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && git add \
  Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
git commit -m "hold enrichment target field permissions to the enrichment pipeline's semantics

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 8: Owner-private types answer like unregistered ones

**Spec:** §5a, §6 Runtime bullets 1–3, §6 Mutation proof (`TryGetVisible`) (closes #20)

**Files:**
- Create: `Iverson.Server/Iverson.Api/Schema/SchemaVisibility.cs`. Holds `TryGetVisible`, a static extension on `SchemaRegistry`. Every service already receives the concrete `SchemaRegistry` by injection, and `SchemaTenantScope.IsForeignTo` is a static extension too, so this takes the same shape.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`:
  - `:94` (Get), `:196` (Update) and `:277` (Delete) get an inline visible lookup and their per-RPC answer;
  - `:360-362`: `RequireSchema`, now used only by Post, becomes visibility-aware.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs`:
  - `:98` (Update) gets an inline visible lookup and NOT_FOUND;
  - `:169-172`: `RequireSchema` (Post) becomes visibility-aware.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs:30` (Get) and `:82` (GetMany): the visible lookup, with the unregistered branches kept exactly.
- Modify `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`:
  - the builder lambdas at `:105-107` (Search), `:827-829` (GroupBy) and `:905-907` (Pipeline);
  - the pipeline-source filter at `:875-883`;
  - `RunAggregationAsync` at `:774-782`, which passes the acting user;
  - `RequireSchema` at `:1067-1071`, which also serves MatchPattern (`ObjectSearchGrpcService.MatchPattern.cs:32`);
  - `EvaluateAuthorization`'s joined lookup at `:1091-1095`, with its message unified.
- Modify `Iverson.Server/Iverson.Api/Search/AggregateReader.cs`:
  - `:99-115`: `RunAsync` gains `ClaimsPrincipal? actingUser`, and its lambda at `:114` becomes visible;
  - `:82-83`: `CountRowsAsync` passes its `actingUser`.
- Modify `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs:153, 189, 232`: `registry.Get(relation.RelatedTypeName)` becomes `registry.TryGetVisible(relation.RelatedTypeName, actingUser)`.
- Modify `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs:100-108` and `Iverson.Server/Iverson.ClientConformance/Requirements.cs:590-593`. Comments only.
- Create test `Iverson.Server/Iverson.Api.Tests/Grpc/OwnerPrivateTypeVisibilityTests.cs` with:
  - the table-driven equal-answer theory over every RPC in §5a's table;
  - the builder-lambda test;
  - relation hydration for three kinds;
  - unowned and owner visibility.
- Test `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`: builder-level join-source and step-name rows, run through `TrackAndValidate` with a visible-registry lambda.
- Modify test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs:1075-1084, 1758-1768, 2348-2357`, and test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs:557-571`. These four tests pin the old FAILED_PRECONDITION answer for unregistered keyed RPCs; see Step 1c.

**Interfaces:**
- **Produces:**
  - `public static SchemaDescriptor? TryGetVisible(this SchemaRegistry registry, string typeName, ClaimsPrincipal? actingUser)` in namespace `Iverson.Api.Schema`. Task 9 relies on line `:232` of `EntityRelationResolver.cs` already calling it.
  - `AggregateReader.RunAsync` gains a third parameter, `ClaimsPrincipal? actingUser`, after `registry`. Its only callers are `ObjectSearchGrpcService.RunAggregationAsync` and `AggregateReader.CountRowsAsync`.
- **Consumes** (from Tasks 1 and 2): `StarRocksPipelineBuilder.TrackAndValidate(EngagementQuerySchema, PipelineRequest, Func<string, EngagementQuerySchema?>, IReadOnlyDictionary<string, AuthorizationConstraint>?)` keeps its signature. The pipeline-builder test helpers `BuildRegistry`, `Request`, `ArticleSchema`, `AuthorSchemaLocal` and `AssertInvalid` still exist, as does the join-source message `"Step '{step}': join source '{source}' is neither an earlier step nor a registered type."`.

- [ ] **Step 0: Check the preconditions**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && \
git log --oneline 1b7968aa..HEAD -- Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs && \
command grep -n "internal static IReadOnlyList<StepColumns> TrackAndValidate" Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs && \
command grep -n "is neither \"" Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs && \
command grep -n "private static Func<string, EngagementQuerySchema?> BuildRegistry\|private static EngagementQuerySchema AuthorSchemaLocal\|private static PipelineRequest Request\|private static void AssertInvalid\|private static EngagementQuerySchema ArticleSchema" Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs
```

Expected:
- the first command prints nothing, because no earlier task touched `EntityRelationResolver.cs`;
- the `TrackAndValidate` grep and the `is neither "` grep each print one line;
- the helper grep prints five lines, one per helper (`ArticleSchema`, `BuildRegistry`, `AuthorSchemaLocal`, `Request`, `AssertInvalid`).

If either check fails, stop and report it.

- [ ] **Step 1a: Write the failing RPC tests**

Create `Iverson.Server/Iverson.Api.Tests/Grpc/OwnerPrivateTypeVisibilityTests.cs`. The construction follows existing tests:
- `ObjectSearchGrpcServiceTests`' constructor;
- `MatchPatternGrpcServiceTests.Sut`;
- `ObjectRetrievalGrpcServiceTests.FakeStream<T>` (`:752-759`);
- acting users from `ActingUserFixtures.Principal` and `PrincipalWithTenant` (`Helpers/ActingUserFixtures.cs`);
- owned fixtures as `SchemaFixtures.AuthorSchema() with { OwnerTenantId = … }`, as in `ObjectMappingGrpcServiceTests.GetSchema_OwnerTenantIdSet_ExcludesOtherTenantsCatalog_ButIncludesUnscopedTypes`.

```csharp
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;
using SchemaRelationKind       = Iverson.Api.Schema.RelationKind;
using SrAggResult              = Iverson.StarRocks.AggregationResult;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR round 11 #20: a type registered to another tenant is answered exactly as a type nobody
/// registered, on every data RPC, so a caller cannot use a type name to learn that another tenant
/// has it. The table below is spec §5a's: each row runs the same request against an unregistered
/// name and a foreign-owned one and expects one answer, written once as a function of the name the
/// request carried.
/// <para>
/// The foreign type is registered as "Ledger" and requested as "ledger": the registry matches
/// names case-insensitively, so an answer that echoed the registered spelling would show here.
/// Its table holds a row the caller's tenant can read, so a call that reached the store would
/// answer differently and the comparison could not pass by accident.
/// </para>
/// </summary>
public sealed class OwnerPrivateTypeVisibilityTests : IAsyncLifetime
{
    private const string Unregistered = "ghost";
    private const string Foreign      = "ledger";
    private const string Key          = "33333333-0000-0000-0000-000000000003";
    private const string MalformedKey = "not-a-guid";
    private const string InvoiceId    = "44444444-0000-0000-0000-000000000004";
    private const string TraceId      = "t";

    // Readable by the default acting user's tenant ("test-tenant").
    private static readonly string LedgerJson = $$"""{"Id":"{{Key}}","Name":"Ledger row","TenantId":"test-tenant"}""";

    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IOutboxWriter _outboxWriter = Substitute.For<IOutboxWriter>();
    private readonly IOutboxPublisher _outboxPublisher = Substitute.For<IOutboxPublisher>();
    private readonly IEngagementStoreSearchService _search = Substitute.For<IEngagementStoreSearchService>();
    private readonly IVectorQueryService _vector = Substitute.For<IVectorQueryService>();
    private readonly IEmbeddingServiceResolver _resolver = Substitute.For<IEmbeddingServiceResolver>();
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
    private readonly ActingUserAccessor _actingUserAccessor = new()
        { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
    private readonly SchemaRegistry _registry;
    private readonly ObjectMappingGrpcService _mapping;
    private readonly ObjectPersistenceGrpcService _persistence;
    private readonly ObjectRetrievalGrpcService _retrieval;
    private readonly ObjectSearchGrpcService _searchService;

    public OwnerPrivateTypeVisibilityTests()
    {
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);

        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns((string?)null);
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(LedgerJson);
        _entities
            .FetchManyByKeysAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<EntityAccess>())
            .Returns(new[] { new KeyedRow(Key, LedgerJson) });
        _entities
            .FetchByColumnAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(new[] { LedgerJson });

        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((SrAggResult?)null);
        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());

        var auditLog = new AuditLog(NullLogger<AuditLog>.Instance);
        _mapping = new ObjectMappingGrpcService(
            _entities,
            Substitute.For<IRecordStoreTransactionRunner>(),
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            new EntityRelationResolver(_registry, _entities, _authEvaluator),
            Substitute.For<ISchemaRegistrationOrchestrator>(),
            auditLog,
            EngagementQueryLimitOptions.Default);
        _persistence = new ObjectPersistenceGrpcService(
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectPersistenceGrpcService>.Instance,
            _entities,
            _actingUserAccessor,
            _authEvaluator,
            auditLog);
        _retrieval = new ObjectRetrievalGrpcService(
            _entities,
            _registry,
            NullLogger<ObjectRetrievalGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            auditLog);
        _searchService = new ObjectSearchGrpcService(
            _registry, _search, _vector, _resolver,
            NullLogger<ObjectSearchGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator, new IntelligenceTenantScope("test-signing-key-0123456789abcdef"),
            new ResultReranker(Options.Create(new VectorRankingOptions())), new ResultDiversifier(),
            Options.Create(new VectorRankingOptions()),
            Options.Create(new DecayOptions()),
            Options.Create(new PopularitySignalOptions()),
            EngagementQueryLimitOptions.Default);
    }

    public async Task InitializeAsync()
    {
        // Unowned, as every SDK registration is: visible to every tenant.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        // Another tenant's type. "test-bypass" may read and write it, so only visibility stops the
        // default acting user (tenant "test-tenant") from using it.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with
        {
            TypeName = "Ledger", TableName = "ledgers", OwnerTenantId = "tenant-b"
        });
        // The default acting user's own tenant's type.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with
        {
            TypeName = "OwnLedger", TableName = "own_ledgers", OwnerTenantId = "test-tenant"
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Outcome capture ───────────────────────────────────────────────────────

    /// <summary>Status, detail and the response messages, as one comparable value.</summary>
    private sealed record Outcome(StatusCode? Code, string? Detail, string Responses);

    private static Outcome Completed(params IMessage[] responses) =>
        new(null, null, string.Join("\n", responses.Select(r => r.ToString())));

    private static Outcome Failed(StatusCode code, string detail) => new(code, detail, string.Empty);

    private static async Task<Outcome> UnaryAsync(Func<Task<IMessage>> call)
    {
        try
        {
            return Completed(await call());
        }
        catch (RpcException ex)
        {
            return Failed(ex.StatusCode, ex.Status.Detail);
        }
    }

    private static async Task<Outcome> StreamAsync<T>(Func<IServerStreamWriter<T>, Task> call) where T : IMessage
    {
        var stream = new FakeStream<T>();
        try
        {
            await call(stream);
            return Completed(stream.Written.Cast<IMessage>().ToArray());
        }
        catch (RpcException ex)
        {
            return Failed(ex.StatusCode, ex.Status.Detail);
        }
    }

    private sealed class FakeStream<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) { Written.Add(message); return Task.CompletedTask; }
        public Task WriteAsync(T message, CancellationToken cancellationToken)
            { Written.Add(message); return Task.CompletedTask; }
    }

    private static TestServerCallContext Ctx() => TestServerCallContext.Create();

    // ── Request and answer builders ───────────────────────────────────────────

    private static Struct Payload(params (string Key, string Value)[] fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = Value.ForString(v);
        return s;
    }

    private static JoinSpec JoinTo(string type) => new()
    {
        LeftType = "Author", RightType = type, LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner
    };

    private static AggregateRequest AggregateRequestFor(string type, JoinSpec? join = null)
    {
        var request = new AggregateRequest { TypeName = type, TraceId = TraceId };
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });
        if (join is not null) request.Joins.Add(join);
        return request;
    }

    private static PipelineRequest PipelineJoining(string source)
    {
        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = source, Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "Id", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { All = true });
        var request = new PipelineRequest { TypeName = "Author", TraceId = TraceId };
        request.Steps.Add(step);
        return request;
    }

    private static string NoSchema(string type) => $"No schema registered for '{type}'. Call RegisterSchema first.";

    private static Outcome MappingAbsent(string type, string key) => Completed(new MappingResponse
        { Success = false, Error = $"'{type}:{key}' not found.", TraceId = TraceId });

    private static Outcome DeleteAbsent(string type, string key) => Completed(new MappingDeleteResponse
        { Success = false, Error = $"'{type}:{key}' not found.", TraceId = TraceId });

    private static Outcome RowNotFound(string type) => Failed(StatusCode.NotFound, $"'{type}' row not found.");

    private static Outcome NotFoundResults(int count) => Completed(
        Enumerable.Repeat<IMessage>(new RetrievalResponse { Found = false, TraceId = TraceId }, count).ToArray());

    // ── The table (spec §5a) ──────────────────────────────────────────────────

    private sealed record RpcCase(
        Func<string, Task<Outcome>> Run, Func<string, Outcome> Expected, bool QueriesTheSearchStore = false);

    public static TheoryData<string> RpcsAnsweringAnInvisibleType() => new()
    {
        "Mapping.Get",
        "Mapping.Get with a malformed key",
        "Mapping.Get at depth 1",
        "Mapping.Delete",
        "Mapping.Delete with a malformed key",
        "Mapping.Update",
        "Mapping.Update with no key",
        "Mapping.Update with a malformed key",
        "Mapping.Update carrying the tenant column",
        "Persistence.Update",
        "Persistence.Update with no key",
        "Persistence.Update with a malformed key",
        "Mapping.Post",
        "Persistence.Post",
        "Retrieval.Get",
        "Retrieval.Get with a malformed key",
        "Retrieval.GetMany with a duplicate and a malformed key",
        "Search",
        "Aggregate",
        "GroupBy",
        "Pipeline",
        "SearchSimilar",
        "SearchChunks",
        "MatchPattern",
        "Search joining the type",
        "Aggregate joining the type",
        "GroupBy joining the type",
        "Pipeline joining the type",
    };

    private RpcCase CaseFor(string rpc) => rpc switch
    {
        "Mapping.Get" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Get(
                new MappingGetRequest { TypeName = t, Key = Key, TraceId = TraceId }, Ctx())),
            t => MappingAbsent(t, Key)),
        "Mapping.Get with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Get(
                new MappingGetRequest { TypeName = t, Key = MalformedKey, TraceId = TraceId }, Ctx())),
            t => MappingAbsent(t, MalformedKey)),
        "Mapping.Get at depth 1" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Get(
                new MappingGetRequest { TypeName = t, Key = Key, Depth = 1, TraceId = TraceId }, Ctx())),
            t => MappingAbsent(t, Key)),
        "Mapping.Delete" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Delete(
                new MappingDeleteRequest { TypeName = t, Key = Key, TraceId = TraceId }, Ctx())),
            t => DeleteAbsent(t, Key)),
        "Mapping.Delete with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Delete(
                new MappingDeleteRequest { TypeName = t, Key = MalformedKey, TraceId = TraceId }, Ctx())),
            t => DeleteAbsent(t, MalformedKey)),
        "Mapping.Update" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Id", Key), ("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Update with no key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Update with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Id", MalformedKey)), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Update carrying the tenant column" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest
                {
                    TypeName = t,
                    Payload  = Payload(("Id", Key), (SchemaDescriptor.TenantColumnName, "other-tenant")),
                    TraceId  = TraceId
                }, Ctx())),
            RowNotFound),
        "Persistence.Update" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Update(
                new PersistRequest { TypeName = t, Payload = Payload(("Id", Key), ("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Persistence.Update with no key" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Update(
                new PersistRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Persistence.Update with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Update(
                new PersistRequest { TypeName = t, Payload = Payload(("Id", MalformedKey)), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Post" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Post(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Persistence.Post" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Post(
                new PersistRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Retrieval.Get" => new RpcCase(
            t => UnaryAsync(async () => await _retrieval.Get(
                new RetrievalRequest { TypeName = t, Key = Key, TraceId = TraceId }, Ctx())),
            _ => NotFoundResults(1)),
        "Retrieval.Get with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _retrieval.Get(
                new RetrievalRequest { TypeName = t, Key = MalformedKey, TraceId = TraceId }, Ctx())),
            _ => NotFoundResults(1)),
        "Retrieval.GetMany with a duplicate and a malformed key" => new RpcCase(
            t => StreamAsync<RetrievalResponse>(w => _retrieval.GetMany(
                new RetrievalManyRequest { TypeName = t, Keys = { Key, Key, MalformedKey }, TraceId = TraceId }, w, Ctx())),
            _ => NotFoundResults(3)),
        "Search" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Search(
                new SearchRequest { TypeName = t, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Aggregate" => new RpcCase(
            t => UnaryAsync(async () => await _searchService.Aggregate(AggregateRequestFor(t), Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "GroupBy" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.GroupBy(
                new GroupByRequest { TypeName = t, Keys = { "Name" }, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Pipeline" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Pipeline(
                new PipelineRequest { TypeName = t, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "SearchSimilar" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.SearchSimilar(
                new SearchSimilarRequest { TypeName = t, Property = "Name", Query = "q", TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "SearchChunks" => new RpcCase(
            t => StreamAsync<ChunkSearchResponse>(w => _searchService.SearchChunks(
                new SearchChunksRequest { TypeName = t, Property = "Bio", Query = "q", TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "MatchPattern" => new RpcCase(
            t => StreamAsync<MatchPatternResponse>(w => _searchService.MatchPattern(
                new MatchPatternRequest
                {
                    TypeName = t, Pattern = "A", TraceId = TraceId, OrderBy = { new SearchSort { Property = "Id" } }
                }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Search joining the type" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Search(
                new SearchRequest { TypeName = "Author", Joins = { JoinTo(t) }, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Aggregate joining the type" => new RpcCase(
            t => UnaryAsync(async () => await _searchService.Aggregate(AggregateRequestFor("Author", JoinTo(t)), Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "GroupBy joining the type" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.GroupBy(
                new GroupByRequest { TypeName = "Author", Keys = { "Name" }, Joins = { JoinTo(t) }, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        // A pipeline source may name an earlier step, so an invisible source is dropped from the
        // authorization pass rather than refused; the builder answers it (asserted at builder
        // level in StarRocksPipelineBuilderTests, since _search is a mock here).
        "Pipeline joining the type" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Pipeline(PipelineJoining(t), w, Ctx())),
            _ => Completed(),
            QueriesTheSearchStore: true),
        _ => throw new ArgumentOutOfRangeException(nameof(rpc), rpc, "No such row in the table.")
    };

    [Theory]
    [MemberData(nameof(RpcsAnsweringAnInvisibleType))]
    public async Task An_unregistered_type_and_another_tenants_type_get_the_same_answer(string rpc)
    {
        var rpcCase = CaseFor(rpc);

        var unregistered = await rpcCase.Run(Unregistered);
        var foreign      = await rpcCase.Run(Foreign);

        unregistered.Should().Be(rpcCase.Expected(Unregistered));
        foreign.Should().Be(rpcCase.Expected(Foreign));

        // Answered before any key is read and before any store is asked.
        _entities.ReceivedCalls().Should().BeEmpty();
        _outboxWriter.ReceivedCalls().Should().BeEmpty();
        _outboxPublisher.ReceivedCalls().Should().BeEmpty();
        _vector.ReceivedCalls().Should().BeEmpty();
        if (!rpcCase.QueriesTheSearchStore)
            _search.ReceivedCalls().Should().BeEmpty();
    }

    // ── The registry handed to every query builder ────────────────────────────

    [Fact]
    public async Task The_registry_every_query_builder_receives_resolves_only_types_the_caller_can_see()
    {
        var lookups = new List<Func<string, EngagementQuerySchema?>>();
        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Do<Func<string, EngagementQuerySchema?>?>(f => lookups.Add(f!)),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Do<Func<string, EngagementQuerySchema?>?>(f => lookups.Add(f!)),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((SrAggResult?)null);
        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(),
                Arg.Do<Func<string, EngagementQuerySchema?>>(lookups.Add),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(),
                Arg.Do<Func<string, EngagementQuerySchema?>>(lookups.Add),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());

        await StreamAsync<SearchResponse>(w => _searchService.Search(new SearchRequest { TypeName = "Author" }, w, Ctx()));
        await _searchService.Aggregate(AggregateRequestFor("Author"), Ctx());
        await StreamAsync<SearchResponse>(w => _searchService.GroupBy(
            new GroupByRequest { TypeName = "Author", Keys = { "Name" } }, w, Ctx()));
        await StreamAsync<SearchResponse>(w => _searchService.Pipeline(new PipelineRequest { TypeName = "Author" }, w, Ctx()));

        lookups.Should().HaveCount(4, "Search, Aggregate, GroupBy and Pipeline each hand the builder one");
        foreach (var lookup in lookups)
        {
            lookup(Unregistered).Should().BeNull();
            lookup(Foreign).Should().BeNull();
            lookup("Author").Should().NotBeNull();
            lookup("OwnLedger").Should().NotBeNull();
        }
    }

    // ── Relation hydration ────────────────────────────────────────────────────

    private static SchemaDescriptor InvoiceRelatedTo(SchemaRelationKind kind, string relatedType) =>
        SchemaFixtures.AuthorSchema() with
        {
            TypeName  = "Invoice",
            TableName = "invoices",
            Relations =
            [
                new SchemaRelationDescriptor("Target", kind, relatedType, kind switch
                {
                    SchemaRelationKind.ManyToMany => "TargetIds",
                    SchemaRelationKind.OneToMany  => "InvoiceId",
                    _                             => "TargetId"
                })
            ]
        };

    [Theory]
    [InlineData(SchemaRelationKind.ManyToOne)]
    [InlineData(SchemaRelationKind.ManyToMany)]
    [InlineData(SchemaRelationKind.OneToMany)]
    public async Task Relation_hydration_leaves_another_tenants_related_type_unresolved_like_an_unregistered_one(
        SchemaRelationKind kind)
    {
        var resolver = new EntityRelationResolver(_registry, _entities, _authEvaluator);
        var resolved = new List<Struct>();
        foreach (var relatedType in new[] { "Ghost", "Ledger" })
        {
            var invoice = JsonParser.Default.Parse<Struct>(
                $$"""{"Id":"{{InvoiceId}}","Name":"Inv","TargetId":"{{Key}}","TargetIds":["{{Key}}"],"TenantId":"test-tenant"}""");
            await resolver.ResolveRelationsAsync(
                invoice, InvoiceRelatedTo(kind, relatedType), depth: 1, _actingUserAccessor.ActingUser, CancellationToken.None);
            resolved.Add(invoice);
        }

        resolved[1].Should().Be(resolved[0]);
        resolved[0].Fields.Should().NotContainKey("Target");
        _entities.ReceivedCalls().Should().BeEmpty();
    }

    // ── What stays visible ────────────────────────────────────────────────────

    [Theory]
    [InlineData("test-tenant")]
    [InlineData("tenant-b")]
    public async Task An_unowned_type_stays_visible_to_every_tenant(string tenant)
    {
        _actingUserAccessor.ActingUser = ActingUserFixtures.PrincipalWithTenant("test-user", tenant, "test-bypass");
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{Key}}","Name":"Alice","TenantId":"{{tenant}}"}""");

        var response = await _retrieval.Get(new RetrievalRequest { TypeName = "Author", Key = Key, TraceId = TraceId }, Ctx());

        response.Found.Should().BeTrue();
        response.Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task An_owned_type_is_visible_to_its_own_tenant()
    {
        _actingUserAccessor.ActingUser = ActingUserFixtures.PrincipalWithTenant("test-user", "tenant-b", "test-bypass");
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{Key}}","Name":"Ledger row","TenantId":"tenant-b"}""");

        var response = await _retrieval.Get(new RetrievalRequest { TypeName = Foreign, Key = Key, TraceId = TraceId }, Ctx());

        response.Found.Should().BeTrue();
        response.Data.Fields["Name"].StringValue.Should().Be("Ledger row");
    }
}
```

- [ ] **Step 1b: Write the builder-level tests**

In `Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs`, insert the block below immediately after the test method `Validate_JoinForwardStepSource_Throws` (in the `// ── Joins ──` section). It reuses the existing helpers `BuildRegistry`, `AuthorSchemaLocal`, `ArticleSchema`, `Request` and `AssertInvalid`.

```csharp
    // ── Owner-private types: the builder sees the caller's visible registry (CSR round 11 #20) ──
    //
    // ObjectSearchGrpcService hands the builder a registry lambda that resolves only the types the
    // caller may see: another tenant's type resolves to null, exactly as an unregistered name does.
    // Iverson.Api.Tests' OwnerPrivateTypeVisibilityTests pins that lambda; these pin the builder's
    // answer to it at the join-source and step-name checks. Each hidden-type row has a companion
    // proving the hidden type is really registered, so the rows compare a hidden type with a
    // missing one rather than two missing ones.

    private static EngagementQuerySchema ForeignLedgerSchema() => new(
        "Ledger", "ledgers", "Id", ["Amount"]);

    // Every registered type, the other tenant's included.
    private static Func<string, EngagementQuerySchema?> FullRegistry() =>
        BuildRegistry(AuthorSchemaLocal(), ForeignLedgerSchema());

    // The same registry as this caller sees it.
    private static Func<string, EngagementQuerySchema?> VisibleRegistry() =>
        BuildRegistry(AuthorSchemaLocal());

    private static PipelineStep StepJoining(string source)
    {
        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = source, Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { All = true });
        return step;
    }

    [Theory]
    [InlineData("Ghost")]
    [InlineData("Ledger")]
    public void Validate_JoinSourceTheCallerCannotSee_GetsTheUnregisteredSourceAnswer(string source)
    {
        var ex = Assert.Throws<EngagementQueryTranslationException>(() =>
            StarRocksPipelineBuilder.TrackAndValidate(ArticleSchema(), Request(StepJoining(source)), VisibleRegistry()));

        ex.Message.Should().Be(
            $"Step 'named': join source '{source}' is neither an earlier step nor a registered type.");
    }

    [Fact]
    public void Validate_JoinSourceOfAnotherTenant_ResolvesWhenTheRegistryIsUnfiltered()
    {
        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(StepJoining("Ledger")), FullRegistry());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Ghost")]
    [InlineData("Ledger")]
    public void Validate_StepNamedAfterATypeTheCallerCannotSee_IsAcceptedLikeAnUnregisteredName(string name)
    {
        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(new PipelineStep { Name = name }), VisibleRegistry());

        cols.Select(c => c.Name).Should().Equal("base", name);
        cols[1].Columns.Keys.Should().BeEquivalentTo(cols[0].Columns.Keys);
    }

    [Fact]
    public void Validate_StepNamedAfterAnotherTenantsType_CollidesWhenTheRegistryIsUnfiltered() =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(new PipelineStep { Name = "Ledger" }), FullRegistry()),
            "collides with a registered type name");
```

- [ ] **Step 1c: Re-point the four tests that pin FAILED_PRECONDITION for an unregistered keyed RPC**

The spec accepts this change (§5a table; Verified assumption 38). The tests that keep FAILED_PRECONDITION are left alone:
- Post: `ObjectMappingGrpcServiceTests.Post_WhenSchemaNotRegistered_ThrowsFailedPrecondition` and `ObjectPersistenceGrpcServiceTests.Post_ThrowsRpcException_WhenSchemaNotRegistered`;
- the search family: `ObjectSearchGrpcServiceTests.{Search,Aggregate,SearchSimilar,SearchChunks,Pipeline}_ThrowsRpcException_WhenSchemaNotRegistered` and `MatchPatternGrpcServiceTests.An_unknown_type_is_FailedPrecondition`;
- the Aggregate-based `TokenRevocationPipelineTests`, `ActingUserInterceptorTests` and `ActingUserInterceptorSuspensionTests` comments.

Retrieval's `Get_WhenSchemaNotRegistered_ReturnsNotFound` and `GetMany_WhenSchemaNotRegistered_StreamsNotFoundForAllKeys` keep their behaviour.

1. In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`, replace the whole of `Get_WhenSchemaNotRegistered_ThrowsFailedPrecondition` (old expectation: FAILED_PRECONDITION; new: an absent key's completed `Success=false` answer):

```csharp
    [Fact]
    public async Task Get_WhenSchemaNotRegistered_AnswersLikeAnAbsentKey()
    {
        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Ghost", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Be($"'Ghost:{AuthorId}' not found.");
    }
```

2. Same file, replace the whole of `Update_WhenSchemaNotRegistered_ThrowsFailedPrecondition` (old: FAILED_PRECONDITION; new: NOT_FOUND with the key-less message):

```csharp
    [Fact]
    public async Task Update_WhenSchemaNotRegistered_ThrowsNotFound()
    {
        var payload = MakePayload(new() { ["Id"] = Value.ForString(AuthorId) });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Ghost", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Be("'Ghost' row not found.");
    }
```

3. Same file, replace the whole of `Delete_WhenSchemaNotRegistered_ThrowsFailedPrecondition` (old: FAILED_PRECONDITION; new: an absent key's completed `Success=false` answer):

```csharp
    [Fact]
    public async Task Delete_WhenSchemaNotRegistered_AnswersLikeAnAbsentKey()
    {
        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Ghost", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Be($"'Ghost:{AuthorId}' not found.");
    }
```

4. In `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`, `Update_ThrowsRpcException_WhenSchemaNotRegistered` (old: FAILED_PRECONDITION; new: NOT_FOUND). Replace

```csharp
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    // ── Update authorization ──────────────────────────────────────────────────
```

with

```csharp
        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.NotFound
                     && e.Status.Detail == "'Ghost' row not found.");
    }

    // ── Update authorization ──────────────────────────────────────────────────
```

(The `// ── Update authorization` anchor makes the match unique. The same two-line `ThrowAsync … FailedPrecondition` shape also appears in the Post test at `:310-311`, which must not change.)

- [ ] **Step 2: Run the tests and watch them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.OwnerPrivateTypeVisibilityTests.|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests.Get_WhenSchemaNotRegistered|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests.Update_WhenSchemaNotRegistered|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests.Delete_WhenSchemaNotRegistered|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectPersistenceGrpcServiceTests.Update_ThrowsRpcException_WhenSchemaNotRegistered"
```

Expected failures:
- **The theory** fails every row except `Pipeline joining the type`, which already completes with an empty stream for both names.
  - Unregistered keyed rows fail on FAILED_PRECONDITION.
  - Foreign rows reach the stub row or the evaluator.
  - The joined rows fail on the old message, which lacks the "Call RegisterSchema first." suffix.
- `The_registry_every_query_builder_receives_…` fails because `lookup("ledger")` is not null.
- `Relation_hydration_…` fails all three kinds, because `Target` is resolved for `Ledger`.
- The four re-pointed tests fail.

Expected passes: `An_unowned_type_stays_visible_…` (both) and `An_owned_type_is_visible_…`. They pin behaviour that must survive.

Then run the builder tests:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.StarRocks.Tests/Iverson.StarRocks.Tests.csproj --filter "FullyQualifiedName~Iverson.StarRocks.Tests.StarRocksPipelineBuilderTests."
```

Expected: all pass, including the six new cases. They pin the builder's existing answer to a lambda that resolves an invisible name to null. The builder code doesn't change in this task, and the RPC-side lambda test above is the one that goes red.

- [ ] **Step 3: Implement**

**3.1 Create `Iverson.Server/Iverson.Api/Schema/SchemaVisibility.cs`:**

```csharp
using System.Security.Claims;

namespace Iverson.Api.Schema;

/// <summary>
/// The by-name type lookup every data RPC makes (CSR round 11 #20). A type is <b>visible</b> to a
/// caller when it is registered and is not <see cref="SchemaTenantScope.IsForeignTo"/> that
/// caller, so an unowned type is visible to everyone.
/// <para>
/// An invisible type, unregistered or owned by another tenant, comes back <c>null</c>. Every call
/// site answers both cases from that one branch, so a type name tells a caller nothing about
/// another tenant. <see cref="SchemaRegistry.Get"/> stays for the server's consumers and workers,
/// which act across tenants on the server's own behalf.
/// </para>
/// </summary>
public static class SchemaVisibility
{
    public static SchemaDescriptor? TryGetVisible(
        this SchemaRegistry registry, string typeName, ClaimsPrincipal? actingUser) =>
        registry.Get(typeName) is { } schema && !schema.IsForeignTo(actingUser) ? schema : null;
}
```

**3.2 `ObjectMappingGrpcService.cs`.**

Get (`:94`). Replace

```csharp
        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
        if (rowJson is null)
            return new MappingResponse
```

with

```csharp
        // An invisible type, unregistered or another tenant's, gets exactly an absent key's answer
        // (CSR round 11 #20), before the key reaches the store.
        var schema = _registry.TryGetVisible(request.TypeName, _actingUserAccessor.ActingUser);
        if (schema is null)
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
        if (rowJson is null)
            return new MappingResponse
```

Update (`:196`). Replace

```csharp
        _logger.LogInformation("[Mapping.Update] type={Type}", request.TypeName.SanitizeForLog());

        var schema = RequireSchema(request.TypeName);
```

with

```csharp
        _logger.LogInformation("[Mapping.Update] type={Type}", request.TypeName.SanitizeForLog());

        // An invisible type, unregistered or another tenant's, is answered before the key is read
        // (CSR round 11 #20). Its key column is unknown, so the message names no key.
        var schema = _registry.TryGetVisible(request.TypeName, _actingUserAccessor.ActingUser)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"'{request.TypeName}' row not found."));
```

Delete (`:277`). Replace

```csharp
        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
        if (rowJson is null)
            return new MappingDeleteResponse
```

with

```csharp
        // An invisible type gets exactly an absent key's answer (CSR round 11 #20).
        var schema = _registry.TryGetVisible(request.TypeName, _actingUserAccessor.ActingUser);
        if (schema is null)
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
        if (rowJson is null)
            return new MappingDeleteResponse
```

`RequireSchema` (`:360-362`, now used only by Post). Replace

```csharp
    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));
```

with

```csharp
    // Another tenant's type gets the unregistered answer (CSR round 11 #20).
    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.TryGetVisible(typeName, _actingUserAccessor.ActingUser) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));
```

**3.3 `ObjectPersistenceGrpcService.cs`.**

Update (`:98`). Replace

```csharp
        var schema = RequireSchema(request.TypeName);

        var key = keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
```

with

```csharp
        // An invisible type, unregistered or another tenant's, is answered before the key is read
        // (CSR round 11 #20). Its key column is unknown, so the message names no key.
        var schema = registry.TryGetVisible(request.TypeName, actingUserAccessor.ActingUser)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"'{request.TypeName}' row not found."));

        var key = keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
```

`RequireSchema` (`:169-172`). Replace

```csharp
    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(
```

with

```csharp
    // Another tenant's type gets the unregistered answer (CSR round 11 #20).
    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.TryGetVisible(typeName, actingUserAccessor.ActingUser) ?? throw new RpcException(
```

**3.4 `ObjectRetrievalGrpcService.cs`.**

Get (`:30`). Replace

```csharp
        var schema = registry.Get(request.TypeName);
        if (schema is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };
```

with

```csharp
        // Another tenant's type takes the unregistered branch (CSR round 11 #20).
        var schema = registry.TryGetVisible(request.TypeName, actingUserAccessor.ActingUser);
        if (schema is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };
```

GetMany (`:82`). Replace

```csharp
        var schema = registry.Get(request.TypeName);

        if (schema is null)
        {
```

with

```csharp
        // Another tenant's type takes the unregistered branch exactly, ahead of Distinct and the
        // GUID check: one Found=false per raw key (CSR round 11 #20).
        var schema = registry.TryGetVisible(request.TypeName, actingUserAccessor.ActingUser);

        if (schema is null)
        {
```

**3.5 `ObjectSearchGrpcService.cs`.**

Search lambda (`:105-107`). Replace

```csharp
                registry: t => registry.Get(t) is { } d
                    ? SchemaBuilder.ToEngagementQuerySchema(d)
                    : null,
```

with

```csharp
                registry: t => registry.TryGetVisible(t, actingUserAccessor.ActingUser) is { } d
                    ? SchemaBuilder.ToEngagementQuerySchema(d)
                    : null,
```

The GroupBy (`:827-829`) and Pipeline (`:905-907`) lambdas have identical text. In each, replace

```csharp
                t => registry.Get(t) is { } d
                    ? SchemaBuilder.ToEngagementQuerySchema(d)
                    : null,
```

with

```csharp
                t => registry.TryGetVisible(t, actingUserAccessor.ActingUser) is { } d
                    ? SchemaBuilder.ToEngagementQuerySchema(d)
                    : null,
```

(`replace_all` on this exact three-line text is safe. Exactly two occurrences remain after the Search edit, because the Search one carries the `registry: ` prefix.)

The pipeline-source filter (`:875-883`). Replace

```csharp
        // A PipelineJoin.Source can name either a registered type or a prior step (CTE) — only
        // the former needs an authorization check here; a step name is validated later by
        // StarRocksPipelineBuilder itself. Evaluated once across every step's joins, same as
        // Search/Aggregate/GroupBy evaluate once across their own request-level Joins.
        var joinedTypes = request.Steps
            .SelectMany(s => s.Joins)
            .Select(j => j.Source)
            .Distinct()
            .Where(src => registry.Get(src) is not null);
```

with

```csharp
        // A PipelineJoin.Source can name either a registered type or a prior step (CTE) — only
        // the former needs an authorization check here; a step name is validated later by
        // StarRocksPipelineBuilder itself. Evaluated once across every step's joins, same as
        // Search/Aggregate/GroupBy evaluate once across their own request-level Joins. Only a type
        // the caller can see counts: another tenant's type is dropped here like an unregistered
        // name, and the builder, handed the same visible registry, answers both alike (CSR round 11
        // #20).
        var joinedTypes = request.Steps
            .SelectMany(s => s.Joins)
            .Select(j => j.Source)
            .Distinct()
            .Where(src => registry.TryGetVisible(src, actingUserAccessor.ActingUser) is not null);
```

`RunAggregationAsync` (`:774-782`). Replace

```csharp
            return await AggregateReader.RunAsync(
                search,
                registry,
                schema,
```

with

```csharp
            return await AggregateReader.RunAsync(
                search,
                registry,
                actingUserAccessor.ActingUser,
                schema,
```

`RequireSchema` (`:1067-1071`). Replace

```csharp
    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(
            new Status(
                StatusCode.FailedPrecondition,
                $"No schema registered for '{typeName}'. Call RegisterSchema first."));
```

with

```csharp
    // Another tenant's type gets the unregistered answer (CSR round 11 #20).
    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.TryGetVisible(typeName, actingUserAccessor.ActingUser) ?? throw new RpcException(
            new Status(
                StatusCode.FailedPrecondition,
                $"No schema registered for '{typeName}'. Call RegisterSchema first."));
```

`EvaluateAuthorization`'s joined lookup (`:1091-1095`). Replace

```csharp
            var joinedSchema = registry.Get(typeName)
                ?? throw new RpcException(
                    new Status(
                        StatusCode.FailedPrecondition,
                        $"No schema registered for '{typeName}'."));
```

with

```csharp
            // Same answer, and the same text, as an invisible primary type (CSR round 11 #20).
            var joinedSchema = registry.TryGetVisible(typeName, actingUserAccessor.ActingUser)
                ?? throw new RpcException(
                    new Status(
                        StatusCode.FailedPrecondition,
                        $"No schema registered for '{typeName}'. Call RegisterSchema first."));
```

**3.6 `AggregateReader.cs`.**

`CountRowsAsync` (`:82-83`). Replace

```csharp
        var result = await RunAsync(
            search, registry, schema, query: null, spec, having: null, joins: null, authz: constraints);
```

with

```csharp
        var result = await RunAsync(
            search, registry, actingUser, schema, query: null, spec, having: null, joins: null, authz: constraints);
```

`RunAsync` (`:95-115`). Replace

```csharp
    /// <summary>
    /// Executes one aggregation against <paramref name="schema"/>. Authorization must already have
    /// been evaluated by the caller and passed in as <paramref name="authz"/>.
    /// </summary>
    internal static Task<EngagementAggResult?> RunAsync(
        IEngagementStoreSearchService search,
        SchemaRegistry registry,
        SchemaDescriptor schema,
```

with

```csharp
    /// <summary>
    /// Executes one aggregation against <paramref name="schema"/>. Authorization must already have
    /// been evaluated by the caller and passed in as <paramref name="authz"/>. The builder resolves
    /// joined type names only among the types <paramref name="actingUser"/> can see (CSR round 11
    /// #20).
    /// </summary>
    internal static Task<EngagementAggResult?> RunAsync(
        IEngagementStoreSearchService search,
        SchemaRegistry registry,
        ClaimsPrincipal? actingUser,
        SchemaDescriptor schema,
```

and replace

```csharp
            t => registry.Get(t) is { } d ? SchemaBuilder.ToEngagementQuerySchema(d) : null,
```

with

```csharp
            t => registry.TryGetVisible(t, actingUser) is { } d ? SchemaBuilder.ToEngagementQuerySchema(d) : null,
```

(`System.Security.Claims` and `Iverson.Api.Schema` are already imported.)

**3.7 `EntityRelationResolver.cs` (`:153, 189, 232`).** Replace all three occurrences of

```csharp
        var relatedSchema = registry.Get(relation.RelatedTypeName);
```

with

```csharp
        var relatedSchema = registry.TryGetVisible(relation.RelatedTypeName, actingUser);
```

Then, at the first site only (`ResolveSingleRelationAsync`), put this comment on the line above it:

```csharp
        // Another tenant's related type is treated as a missing one (CSR round 11 #20), here and in
        // the two collection resolvers below.
```

**3.8 Conformance comments.** No assertion changes.

In `Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs`, replace lines `:100-108`:

```csharp
/// <para><b>A header-less caller and a wrong-tenant caller get different answers, by design.</b> A
/// caller with no acting-user token at all is denied before this row is ever read
/// (<c>RowFieldAuthorizationEvaluator.Evaluate</c> returns <c>Denied</c> on a null acting user,
/// <c>reason=AccessDenied</c>) and is answered <c>PermissionDenied</c> (7). A wrong-tenant caller
/// passes that check, reaches the tenant-scoped read, finds no visible row, and is answered
/// <c>NotFound</c> (5). The order is deliberate: the denial check runs before the existence check,
/// so a caller who is refused anyway is refused the same way whether or not the key exists. Only
/// the wrong-tenant answer is graded; the header-less one is a Deferred area in the standard's IDN
/// coverage ledger.</para>
```

with

```csharp
/// <para><b>What a header-less caller and a wrong-tenant caller are answered.</b> The identity type
/// is registered under tenant A's acting header, so tenant A owns it, and to any other caller it
/// does not exist (<c>SchemaVisibility.TryGetVisible</c>). A wrong-tenant caller and a caller with
/// no acting-user token at all are therefore both answered before any key is read or any row is
/// fetched, with the <c>NotFound</c> (5) an update of an unregistered type receives. On a type no
/// tenant owns the two still differ: the header-less caller is denied before the row is read
/// (<c>RowFieldAuthorizationEvaluator.Evaluate</c> returns <c>Denied</c> on a null acting user,
/// <c>reason=AccessDenied</c>) and is answered <c>PermissionDenied</c> (7), while the wrong-tenant
/// caller reaches the tenant-scoped read, finds no visible row, and is answered <c>NotFound</c> (5).
/// Only the wrong-tenant answer is graded; the header-less one is a Deferred area in the standard's
/// IDN coverage ledger.</para>
```

In `Iverson.Server/Iverson.ClientConformance/Requirements.cs`, replace lines `:590-593`:

```csharp
    /// exists nowhere receives". The assertion accepts code 5 and nothing else: no status at all is
    /// an accepted update, and PERMISSION_DENIED (7) is the answer to a caller refused before any
    /// row is read, such as a call that arrived carrying no acting-user identity rather than
    /// another tenant's.</para>
```

with

```csharp
    /// exists nowhere receives". The assertion accepts code 5 and nothing else: no status at all is
    /// an accepted update, and PERMISSION_DENIED (7) is the answer to a caller refused before any
    /// row is read, such as a call carrying no acting-user identity against a type no tenant owns.
    /// Against a tenant-owned type, the harness's case, another tenant's call and a call with no
    /// acting-user identity both meet a type they cannot see and are answered 5 before any key is
    /// read, as an update of an unregistered type is.</para>
```

- [ ] **Step 4: Run the tests and watch them pass**

Re-run both Step 2 commands.

Expected:
- the Api filter is all green: 28 theory rows, 1 lambda fact, 3 relation rows, 2 + 1 visibility tests, and 4 re-pointed tests;
- the StarRocks filter is all green.

Then run the non-container Api suite, which covers every existing RPC test touched by the lookup swap:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```

Expected: 0 failed. Then confirm the conformance project still builds after the comment edits:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet build Iverson.ClientConformance/Iverson.ClientConformance.csproj
```

Expected: build succeeded, 0 errors.

- [ ] **Step 5: Mutation proof**

Before each mutation, record `git diff | md5sum`. Apply the one edit, then run the Api filter from Step 2 (call it **F8**). Confirm the named test fails, restore the exact original text, and confirm `git diff | md5sum` matches the recorded value. Record each mutation's failing test names in the report.

1. **The `TryGetVisible` guard.**
   - **Mutation:** in `SchemaVisibility.cs`, change `registry.Get(typeName) is { } schema && !schema.IsForeignTo(actingUser) ? schema : null` to `registry.Get(typeName)`.
   - **Must fail:** every `An_unregistered_type_and_another_tenants_type_get_the_same_answer` row except `Pipeline joining the type`, plus `The_registry_every_query_builder_receives_…` and all three `Relation_hydration_…` rows.
2. **Retrieval.GetMany.**
   - **Mutation:** in `ObjectRetrievalGrpcService.GetMany`, change `registry.TryGetVisible(request.TypeName, actingUserAccessor.ActingUser)` to `registry.Get(request.TypeName)`.
   - **Must fail:** row `Retrieval.GetMany with a duplicate and a malformed key`.
3. **Mapping.Update.**
   - **Mutation:** in `ObjectMappingGrpcService.Update`, change `_registry.TryGetVisible(request.TypeName, _actingUserAccessor.ActingUser)` to `_registry.Get(request.TypeName)`.
   - **Must fail:** rows `Mapping.Update`, `Mapping.Update with no key`, `Mapping.Update with a malformed key` and `Mapping.Update carrying the tenant column` (the last answers INVALID_ARGUMENT from the reserved-column check instead of NOT_FOUND).
4. **`EvaluateAuthorization`.**
   - **Mutation:** change `registry.TryGetVisible(typeName, actingUserAccessor.ActingUser)` to `registry.Get(typeName)`.
   - **Must fail:** rows `Search joining the type`, `Aggregate joining the type` and `GroupBy joining the type`.
5. **The pipeline-source filter.**
   - **Mutation:** change `.Where(src => registry.TryGetVisible(src, actingUserAccessor.ActingUser) is not null)` to `.Where(src => registry.Get(src) is not null)`.
   - **Must fail:** row `Pipeline joining the type`. The foreign source now reaches `EvaluateAuthorization` and answers FAILED_PRECONDITION.
6. **The Pipeline builder lambda.**
   - **Mutation:** in `ObjectSearchGrpcService.Pipeline`, change that lambda's `registry.TryGetVisible(t, actingUserAccessor.ActingUser)` to `registry.Get(t)`.
   - **Must fail:** `The_registry_every_query_builder_receives_resolves_only_types_the_caller_can_see`.
7. **The `AggregateReader` lambda.**
   - **Mutation:** change `registry.TryGetVisible(t, actingUser)` to `registry.Get(t)`.
   - **Must fail:** the same lambda test.
8. **Relation hydration, OneToMany site.**
   - **Mutation:** in `EntityRelationResolver.ResolveOneToManyAsync`, change `registry.TryGetVisible(relation.RelatedTypeName, actingUser)` to `registry.Get(relation.RelatedTypeName)`.
   - **Must fail:** `Relation_hydration_…(kind: OneToMany)`.
   - **Repeat** the same mutation at the ManyToOne/OneToOne site (`ResolveSingleRelationAsync`) and the ManyToMany site; the matching row must fail each time.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && \
git add Iverson.Server/Iverson.Api/Schema/SchemaVisibility.cs \
  Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs \
  Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs \
  Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs \
  Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs \
  Iverson.Server/Iverson.Api/Search/AggregateReader.cs \
  Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs \
  Iverson.Server/Iverson.ClientConformance/Scenarios/IdentityScenario.cs \
  Iverson.Server/Iverson.ClientConformance/Requirements.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/OwnerPrivateTypeVisibilityTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs \
  Iverson.Server/Iverson.StarRocks.Tests/StarRocksPipelineBuilderTests.cs && \
git commit -m "answer another tenant's type exactly as an unregistered one

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: OneToMany hydration and catalog follow the child's foreign-key permission

**Spec:** §5b, §6 Runtime bullet "the OneToMany skip and the catalog" (closes #3)

**Files:**
- Modify `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs:220-262`. `ResolveOneToManyAsync` evaluates the child's Read decision before `FetchByColumnAsync`, and skips the relation when `AllowedFields` excludes `relation.ForeignKey`.
- Modify `Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs:54-58, 86-87, 99-104, 126-162`:
  - pass one keeps each survivor's decision by name;
  - `ForeignKeyIsReadable` takes that map and tests a OneToMany's foreign key against the child's decision.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`:
  - a `Mapping.Get` at depth 1 with a restricted child foreign key (skipped, never fetched);
  - a readable child foreign key (still resolves);
  - the catalog not advertising the restricted edge.

**Interfaces:**
- Consumes: Task 8's `SchemaVisibility.TryGetVisible`. `EntityRelationResolver.cs:232` already reads `var relatedSchema = registry.TryGetVisible(relation.RelatedTypeName, actingUser);`.
- Produces: nothing for other tasks. `ForeignKeyIsReadable` is private and its only caller is `BuildCatalog`.

- [ ] **Step 1: Write the failing tests**

All three go in `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`. They reuse:
- `SchemaFixtures.ArticleWithOneToManySchema()`, whose relation `UserArticles` is a OneToMany to `UserArticle` with foreign key `ArticleId`;
- `SchemaFixtures.UserArticleSchema()`;
- `SchemaFixtures.AuthorSchema()`;
- the class's `_registry`, `_entities`, `_sut`, `ArticleId`, `MakeContext()`.

The class wires the real `EntityRelationResolver` (constructor, `_relationResolver`). The default acting user `ActingUserFixtures.Principal("test-user", "test-bypass")` holds no `editor` group.

(a) Insert immediately after `GetSchema_WithOneToManyRelation_UnderActiveFieldPermission_KeepsTheRelation`:

```csharp
    // UserArticle readable by the default caller, but its ArticleId (the FK of Article's OneToMany
    // UserArticles) readable only by "editor".
    private static SchemaDescriptor UserArticleWithUnreadableArticleId() => SchemaFixtures.UserArticleSchema() with
    {
        Authorization = new Iverson.Api.Schema.AuthorizationRules(
            null,
            new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
            new List<Iverson.Api.Schema.FieldPermission>
            {
                new("ArticleId", new List<string> { "editor" }, new List<string>())
            })
    };

    [Fact]
    public async Task GetSchema_WithOneToManyRelation_WhoseChildForeignKeyIsUnreadable_DropsTheRelation()
    {
        // CSR round 11 #3: a OneToMany's FK is a column on the CHILD, so the child's own decision
        // governs it. Advertising the edge would name a column this caller cannot read, and would
        // promise a relation that Get leaves absent.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(UserArticleWithUnreadableArticleId());
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithOneToManySchema());

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        response.Types_.Select(t => t.Name).Should().Contain("UserArticle"); // the child itself survives
        var article = response.Types_.Single(t => t.Name == "Article");
        article.Relations.Should().ContainSingle().Which.PropertyName.Should().Be("Author");
    }
```

(b) Insert immediately after `Get_WithFieldPermissionExcludingFkColumn_OmitsRelatedEntity`:

```csharp
    private const string UserArticleId = "55555555-0000-0000-0000-000000000005";

    private void ArticleWithOneUserArticle()
    {
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "articles"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{ArticleId}}","Title":"Hello","TenantId":"test-tenant"}""");
        _entities
            .FetchByColumnAsync(Arg.Is<TableSchema>(s => s.TableName == "user_articles"), "ArticleId", ArticleId, Arg.Any<EntityAccess>())
            .Returns(new[] { $$"""{"Id":"{{UserArticleId}}","ArticleId":"{{ArticleId}}","TenantId":"test-tenant"}""" });
    }

    [Fact]
    public async Task Get_AtDepth1_WithOneToManyChildForeignKeyUnreadable_OmitsTheRelationWithoutFetchingIt()
    {
        // CSR round 11 #3: the OneToMany fetch filters the child's table on its FK. A caller who may
        // not read that column must not learn which children point at this entity, so the
        // relation is skipped before the fetch and the property left absent.
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithOneToManySchema());
        await _registry.RegisterAsync(UserArticleWithUnreadableArticleId());
        ArticleWithOneUserArticle();

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 1 }, MakeContext());

        response.Success.Should().BeTrue();
        response.Data.Fields.Should().NotContainKey("UserArticles");
        await _entities.DidNotReceive().FetchByColumnAsync(
            Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityAccess>());
    }

    [Fact]
    public async Task Get_AtDepth1_WithOneToManyChildForeignKeyReadable_ResolvesTheRelation()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithOneToManySchema());
        await _registry.RegisterAsync(SchemaFixtures.UserArticleSchema());
        ArticleWithOneUserArticle();

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 1 }, MakeContext());

        response.Success.Should().BeTrue();
        response.Data.Fields["UserArticles"].ListValue.Values
            .Should().ContainSingle()
            .Which.StructValue.Fields["Id"].StringValue.Should().Be(UserArticleId);
    }
```

- [ ] **Step 2: Run them and watch them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests.GetSchema_WithOneToManyRelation|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests.Get_AtDepth1_WithOneToManyChildForeignKey"
```

Expected failures:
- `GetSchema_WithOneToManyRelation_WhoseChildForeignKeyIsUnreadable_DropsTheRelation` fails: two relations are listed, because OneToMany is always advertised today.
- `Get_AtDepth1_WithOneToManyChildForeignKeyUnreadable_OmitsTheRelationWithoutFetchingIt` fails: `UserArticles` is present as a one-element list with `ArticleId` masked, and `FetchByColumnAsync` was received.

Expected passes: `Get_AtDepth1_WithOneToManyChildForeignKeyReadable_ResolvesTheRelation` and the existing `GetSchema_WithOneToManyRelation_UnderActiveFieldPermission_KeepsTheRelation`.

- [ ] **Step 3: Implement**

**3.1 `EntityRelationResolver.ResolveOneToManyAsync`.** Replace

```csharp
        var relatedSchema = registry.TryGetVisible(relation.RelatedTypeName, actingUser);
        if (relatedSchema is null) return;

        var rows = await entities.FetchByColumnAsync(
            SchemaBuilder.ToTableSchema(relatedSchema),
            relation.ForeignKey,
            keyValue,
            EntityAccess.ForTenant(actingUser?.FindFirst("tenant_id")?.Value));

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);
```

with

```csharp
        var relatedSchema = registry.TryGetVisible(relation.RelatedTypeName, actingUser);
        if (relatedSchema is null) return;

        // The child's decision comes first (CSR round 11 #3). The fetch filters the child's table
        // on relation.ForeignKey, so a caller field-restricted away from that column would learn
        // which children reference this entity. Skipped, the property is left absent, as
        // ResolveSingleRelationAsync leaves it when it resolves nothing. SchemaCatalogReader drops
        // the same edge on the same test.
        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);
        if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(relation.ForeignKey))
            return;

        var rows = await entities.FetchByColumnAsync(
            SchemaBuilder.ToTableSchema(relatedSchema),
            relation.ForeignKey,
            keyValue,
            EntityAccess.ForTenant(actingUser?.FindFirst("tenant_id")?.Value));
```

(The first two lines are the state Task 8 left. If `:232` still reads `registry.Get(...)`, stop: Task 8 is missing.)

**3.2 `SchemaCatalogReader.BuildCatalog`.**

Replace (`:54-58`)

```csharp
        var survivors = new List<(SchemaDescriptor Schema, List<SchemaField> Fields, AuthorizationDecision Decision)>();
        // OrdinalIgnoreCase to match SchemaRegistry's own keying (SchemaRegistry.cs) and every
        // other RelationDescriptor.RelatedTypeName lookup (EntityRelationResolver), so a relation
        // declaring a differently-cased related type is not silently dropped from the catalog.
        var survivingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
```

with

```csharp
        var survivors = new List<(SchemaDescriptor Schema, List<SchemaField> Fields, AuthorizationDecision Decision)>();
        // Each surviving type's Read decision, by name: pass two needs membership, and a OneToMany
        // needs its related type's own decision. OrdinalIgnoreCase to match SchemaRegistry's own
        // keying (SchemaRegistry.cs) and every other RelationDescriptor.RelatedTypeName lookup
        // (EntityRelationResolver), so a relation declaring a differently-cased related type is not
        // silently dropped from the catalog.
        var survivingDecisions = new Dictionary<string, AuthorizationDecision>(StringComparer.OrdinalIgnoreCase);
```

Replace (`:86-87`)

```csharp
            survivors.Add((schema, fields, decision));
            survivingNames.Add(schema.TypeName);
```

with

```csharp
            survivors.Add((schema, fields, decision));
            survivingDecisions[schema.TypeName] = decision;
```

Replace (`:101-104`)

```csharp
                    // Two conditions. The related type must have survived pass one, and — only
                    // where the FK column is local to this type — the FK must itself be readable.
                    .Where(r => survivingNames.Contains(r.RelatedTypeName)
                             && ForeignKeyIsReadable(r, decision))
```

with

```csharp
                    // Two conditions. The related type must have survived pass one, and the FK
                    // must be readable under the decision that governs its column: this type's,
                    // or for a OneToMany the related type's.
                    .Where(r => survivingDecisions.ContainsKey(r.RelatedTypeName)
                             && ForeignKeyIsReadable(r, decision, survivingDecisions))
```

Replace the whole of `ForeignKeyIsReadable`, including its doc comment (`:126-162`), with

```csharp
    /// <summary>
    /// Whether <paramref name="relation"/>'s foreign key may be disclosed to this caller.
    /// <para>
    /// Every FK property is also an ordinary scalar column, so a <c>FieldPermission</c> that removed
    /// it from <c>fields</c> would otherwise still leak its exact name as <c>foreign_key</c>. Which
    /// type's decision governs that column depends on the relation kind (see
    /// <c>EntityRelationResolver</c>):
    /// </para>
    /// <list type="bullet">
    /// <item><c>OneToOne</c> / <c>ManyToOne</c> — reads <c>ForeignKey</c> off the declaring entity.</item>
    /// <item><c>ManyToMany</c> — reads <c>ForeignKey</c> as a list off the declaring entity.</item>
    /// <item><c>OneToMany</c> — <c>ForeignKey</c> is a column on the <em>related</em> type's table,
    /// matched against this type's key, so the related type's own decision governs it (CSR round 11
    /// #3). <c>EntityRelationResolver</c> skips the relation on the same test, so the catalog never
    /// advertises an edge that hydration leaves absent.</item>
    /// </list>
    /// <para>
    /// <c>AllowedFields</c> only ever holds its own schema's members (<c>RowFieldAuthorizationEvaluator</c>),
    /// which is why a OneToMany's FK is tested against the related type's decision, never this one's.
    /// </para>
    /// </summary>
    private static bool ForeignKeyIsReadable(
        RelationDescriptor relation,
        AuthorizationDecision decision,
        IReadOnlyDictionary<string, AuthorizationDecision> survivingDecisions)
    {
        if (relation.Kind == SchemaRelationKind.OneToMany)
        {
            // Present: the caller checked survivingDecisions.ContainsKey first.
            var child = survivingDecisions[relation.RelatedTypeName];
            return child.AllowedFields is null || child.AllowedFields.Contains(relation.ForeignKey);
        }

        if (decision.AllowedFields is null)
            return true;

        return relation.Kind switch
        {
            SchemaRelationKind.OneToOne or
            SchemaRelationKind.ManyToOne or
            SchemaRelationKind.ManyToMany => decision.AllowedFields.Contains(relation.ForeignKey),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation.Kind,
                $"Unhandled {nameof(SchemaRelationKind)} value — add a case above.")
        };
    }
```

- [ ] **Step 4: Run the tests and watch them pass**

Re-run the Step 2 command. Expected: 4 passed, 0 failed. Then:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```

Expected: 0 failed. That includes `EntityRelationResolverTests`, `AdminConsoleSchemaEndpointTests` (the catalog through the console) and `OwnerPrivateTypeVisibilityTests` from Task 8.

- [ ] **Step 5: Mutation proof**

Record `git diff | md5sum` first, and restore it after each mutation, as in Task 8. Run the Step 2 filter.

1. **The resolver skip.**
   - **Mutation:** in `ResolveOneToManyAsync`, change `if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(relation.ForeignKey))` to `if (false)`.
   - **Must fail:** `Get_AtDepth1_WithOneToManyChildForeignKeyUnreadable_OmitsTheRelationWithoutFetchingIt`.
2. **The catalog test.**
   - **Mutation:** in `ForeignKeyIsReadable`, change `return child.AllowedFields is null || child.AllowedFields.Contains(relation.ForeignKey);` to `return true;`.
   - **Must fail:** `GetSchema_WithOneToManyRelation_WhoseChildForeignKeyIsUnreadable_DropsTheRelation`.
3. **The catalog reads the child's decision, not the parent's.**
   - **Mutation:** change `var child = survivingDecisions[relation.RelatedTypeName];` to `var child = decision;`.
   - **Must fail:** the same catalog test. Article's own decision has `AllowedFields == null`.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && \
git add Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs \
  Iverson.Server/Iverson.Api/Schema/SchemaCatalogReader.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs && \
git commit -m "skip a one-to-many relation whose foreign key the reader may not read

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Runtime fixes for RemoveUser, owner mismatch and tenant lifecycle

**Spec:** §5c, §5d, §5e, §6 Runtime bullets 5–8 (closes #13, #14, #21)

**Files:**
- Modify `Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs:59-71`. `RemoveUser` revokes and invalidates a second time after `DeactivateUserAsync`.
- Modify `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:45-49, 105-171`:
  - an owner mismatch answers the absent-row NOT_FOUND, with the same message, built once;
  - the check moves between TenantMismatch and TenantImmutable;
  - the `deniedMessage` doc is corrected.
- Modify `Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs:86-93`. `SetStatusAsync` refuses a `deleted` tenant with FAILED_PRECONDITION.
- Modify `Iverson.Server/Iverson.Api/Program.cs`:
  - `:608` and `:631`: both DLQ handlers take `ITenantStatusCache` and refuse an inactive acting tenant;
  - `:795`: `/v1/traces` gains `.AddEndpointFilter<ActiveTenantEndpointFilter>()`.
- Modify `Iverson.Server/Iverson.Api/Console/ActiveTenantEndpointFilter.cs:7-8` (doc only). It now also guards `/v1/traces`.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs`. Two RemoveUser order tests are re-pointed to the full double-revoke sequence.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs`. A legacy tenant-column owner-mismatch test.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` and `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs`:
  - the owner-mismatch tests are re-pointed to NOT_FOUND;
  - a legacy tenant-column RPC test is added to Mapping.
- Test `Iverson.Server/Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs`. Suspend and Reactivate of a deleted tenant.
- Create test `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs`. Covers `/v1/traces`, `/admin/dlq` and `/admin/dlq/{id}/replay` through the real pipeline.
- Modify test `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs`. `ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds` uses tenant `tenant-a`, which the host's no-op tenant repository doesn't know. It must now fake the cache as active, or it gets 403.

**Interfaces:** none produced or consumed across tasks.

- [ ] **Step 1: Write the failing tests**

**(a) RemoveUser.** In `TenantAdminGrpcServiceTests.cs`, replace the whole of `RemoveUser_RevokesTheUsersTokensBeforeDeactivatingInAuthentik` and `RemoveUser_InvalidatesTheRevocationCacheAfterRevoking`. They list only the first revoke, and NSubstitute's `Received.InOrder` fails when the matching calls outnumber the listed ones. Use these, which follow the `SetTenantAdmin_Revoke_*` pair:

```csharp
    // CSR round-10 #11: revoke first. If Authentik then fails, the user only has to log in again;
    // the reverse order could leave a deactivated user's tokens valid. CSR round 11 #13: revoke
    // again afterwards, for a token refreshed between the first revoke and the deactivation.
    [Fact]
    public async Task RemoveUser_RevokesTheUsersTokensBeforeAndAfterDeactivatingInAuthentik()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantUser]));

        await _sut.RemoveUser(new RemoveUserRequest { UserId = "user-1" }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _authentikAdminClient.DeactivateUserAsync("user-1");
            _tokenRevocations.RevokeAsync("uid-alice");
        });
    }

    // This process's revocation cache is invalidated after each revoke so it takes effect here at once.
    [Fact]
    public async Task RemoveUser_InvalidatesTheRevocationCacheAfterEachRevoke()
    {
        _authentikAdminClient.ListUsersByTenantAsync("acme")
            .Returns(Task.FromResult<IEnumerable<IdpUser>>([CallerTenantUser]));

        await _sut.RemoveUser(new RemoveUserRequest { UserId = "user-1" }, ContextForCallerTenant());

        Received.InOrder(() =>
        {
            _tokenRevocations.RevokeAsync("uid-alice");
            _tokenRevocationCache.Invalidate();
            _authentikAdminClient.DeactivateUserAsync("user-1");
            _tokenRevocations.RevokeAsync("uid-alice");
            _tokenRevocationCache.Invalidate();
        });
    }
```

**(b) Owner mismatch at the masking layer, on a legacy schema.**

`AuthorizationFieldMaskingTests.Schema()` puts the tenant boundary on the client-declared `TenantId`, the legacy shape `SchemaRegistry.LoadAsync` still admits. `EvaluatorReturning` stubs the decision. Insert after `EnforceWriteAuthorization_TenantMismatchWithOversizedPayload_StillGetsPermissionDenied`:

```csharp
    // ── Owner mismatch answers like an absent row (CSR round 11 #14) ─────────────────────────

    [Fact]
    public void EnforceWriteAuthorization_OwnerMismatchOnALegacyTenantColumnSchema_AnswersLikeAnAbsentRow()
    {
        // Schema()'s tenant boundary is the client-declared "TenantId", so a payload may carry it,
        // and a value other than the caller's tenant reaches the immutability check. Another
        // owner's row must still get the answer an absent row gets, not PermissionDenied.
        var decision = new AuthorizationDecision(
            Denied: false, OwnershipRequired: true, OwnerFieldName: "OwnerId", OwnerValue: "user-1",
            AllowedFields: null, TenantColumn: "TenantId", TenantValue: "acting-users-tenant");

        RpcException Attempt(string? existingRowJson) =>
            Assert.Throws<RpcException>(() => AuthorizationFieldMasking.EnforceWriteAuthorization(
                EvaluatorReturning(decision),
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test")),
                Schema(),
                new Struct
                {
                    Fields =
                    {
                        ["Id"]       = Value.ForString("tag-1"),
                        ["Name"]     = Value.ForString("new"),
                        ["TenantId"] = Value.ForString("a-different-tenant"),
                    }
                },
                AuthorizationAction.Write,
                "Not authorized to update this entity.",
                existingRowJson,
                requireExistingRow: true,
                new AuditLog(NullLogger<AuditLog>.Instance),
                new PayloadSizeValidator()));

        var anotherOwnersRow = Attempt("""{"Id":"tag-1","Name":"old","OwnerId":"someone-else","TenantId":"acting-users-tenant"}""");
        var absentRow        = Attempt(existingRowJson: null);

        anotherOwnersRow.StatusCode.Should().Be(StatusCode.NotFound);
        anotherOwnersRow.Status.Detail.Should().Be("'BenchmarkTag:tag-1' not found.");
        anotherOwnersRow.StatusCode.Should().Be(absentRow.StatusCode);
        anotherOwnersRow.Status.Detail.Should().Be(absentRow.Status.Detail);
    }
```

**(c) Owner mismatch through the RPCs.**

In `ObjectMappingGrpcServiceTests.cs`, replace the whole of `Update_WithNonMatchingOwner_ThrowsPermissionDenied` (old expectation: PERMISSION_DENIED; new: NOT_FOUND with the absent-row message), and add the legacy-schema test after it. `OwnedAuthorSchema()` (`:1272`) declares `TenantColumn = "TenantId"` as a scalar column, which is the legacy shape:

```csharp
    [Fact]
    public async Task Update_WithNonMatchingOwner_AnswersNotFoundLikeAnAbsentRow()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Be($"'Author:{AuthorId}' not found.");
    }

    [Fact]
    public async Task Update_WithNonMatchingOwnerAndADifferentTenantValue_OnALegacyTenantColumnSchema_AnswersLikeAnAbsentRow()
    {
        // OwnedAuthorSchema's tenant boundary is the client-declared "TenantId". A payload naming a
        // different tenant used to reach the tenant-immutability refusal for another owner's row
        // while an absent row got NotFound (CSR round 11 #14).
        await _registry.RegisterAsync(OwnedAuthorSchema());

        MappingWriteRequest Request() => new()
        {
            TypeName = "Author",
            Payload  = MakePayload(new()
            {
                ["Id"]       = Value.ForString(AuthorId),
                ["Name"]     = Value.ForString("Alice Updated"),
                ["TenantId"] = Value.ForString("other-tenant")
            })
        };

        var absent = await FluentActions.Invoking(() => _sut.Update(Request(), TestServerCallContext.Create()))
            .Should().ThrowAsync<RpcException>();

        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""");
        var anotherOwners = await FluentActions.Invoking(() => _sut.Update(Request(), TestServerCallContext.Create()))
            .Should().ThrowAsync<RpcException>();

        anotherOwners.Which.StatusCode.Should().Be(StatusCode.NotFound);
        anotherOwners.Which.StatusCode.Should().Be(absent.Which.StatusCode);
        anotherOwners.Which.Status.Detail.Should().Be(absent.Which.Status.Detail);
    }
```

In `ObjectPersistenceGrpcServiceTests.cs`, replace the whole of `Update_WithNonMatchingOwner_ThrowsPermissionDenied` (old: PERMISSION_DENIED; new: NOT_FOUND):

```csharp
    [Fact]
    public async Task Update_WithNonMatchingOwner_AnswersNotFoundLikeAnAbsentRow()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var authorId = Guid.NewGuid().ToString();
        var ownedJson = $$"""{"Id":"{{authorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(authorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.NotFound
                     && e.Status.Detail == $"'Author:{authorId}' not found.");
    }
```

The `Update_OwnerMismatch_LogsAuditDeniedWithOwnerMismatch` tests (Mapping `:2101`, Persistence `:843`) stay as they are and still pin the `OwnerMismatch` audit entry. The tenant-mismatch and immutability PERMISSION_DENIED tests also stay unchanged: TenantMismatch still runs first.

**(d) Deleted is terminal.** In `TenantLifecycleGrpcServiceTests.cs`, insert after `SuspendTenant_UnknownTenant_ThrowsNotFoundAndDoesNotUpdate`:

```csharp
    [Fact]
    public async Task SuspendTenant_DeletedTenant_ThrowsFailedPreconditionAndDoesNotUpdate()
    {
        _tenantRepository.GetAsync("acme").Returns(Task.FromResult<TenantRow?>(ExistingTenant with { Status = "deleted" }));

        var act = () => _sut.SuspendTenant(new SuspendTenantRequest { TenantId = "acme" }, ContextWithUser());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        await _tenantRepository.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>());
    }
```

and after `ReactivateTenant_UnknownTenant_ThrowsNotFoundAndDoesNotUpdate`:

```csharp
    // CSR round 11 #21: deletion is terminal. Reactivating a deleted tenant would restore its data
    // plane while its Authentik users stay deactivated.
    [Fact]
    public async Task ReactivateTenant_DeletedTenant_ThrowsFailedPreconditionAndDoesNotUpdate()
    {
        _tenantRepository.GetAsync("acme").Returns(Task.FromResult<TenantRow?>(ExistingTenant with { Status = "deleted" }));

        var act = () => _sut.ReactivateTenant(new ReactivateTenantRequest { TenantId = "acme" }, ContextWithUser());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        await _tenantRepository.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>());
    }
```

**(e) HTTP endpoints.** Create `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs`. It boots the real `Program.cs` through `AuthTestWebApplicationFactory`, as `TracesRelayEndpointTests` does, and swaps the `JaegerOtlpHttp` primary handler the same way. The fixed-status cache follows `AdminConsoleActiveTenantPipelineTests.FixedTenantStatusCache`. The service-plus-acting-user headers follow `AuthenticationPipelineTests.ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds`.

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 11 #21: <c>/v1/traces</c> and <c>/admin/dlq*</c> refuse a caller whose tenant is not
/// active, as the gRPC surfaces and the admin console already do. A caller whose token carries no
/// <c>tenant_id</c> (an operator's) passes. Through the real <c>Program.cs</c> pipeline, with the
/// tenant status cache fixed per tenant id.
/// </summary>
public class InactiveTenantHttpEndpointTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;

    public InactiveTenantHttpEndpointTests(AuthTestWebApplicationFactory factory) => _baseFactory = factory;

    /// <summary>One tenant per status; any other id is unknown (null).</summary>
    private sealed class FixedTenantStatusCache : ITenantStatusCache
    {
        public Task<string?> GetStatusAsync(string tenantId) => Task.FromResult(tenantId switch
        {
            "tenant_active"    => "active",
            "tenant_suspended" => "suspended",
            "tenant_deleted"   => "deleted",
            _                  => (string?)null
        });
    }

    private sealed class FakeJaegerHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private (HttpClient Client, FakeJaegerHandler Jaeger, IDlqRepository Dlq) CreateClient()
    {
        var jaeger = new FakeJaegerHandler();
        var dlq = Substitute.For<IDlqRepository>();
        dlq.ListUnreplayedAsync(Arg.Any<int>()).Returns(Enumerable.Empty<DlqRow>());
        dlq.GetUnreplayedByIdAsync(Arg.Any<Guid>()).Returns((DlqReplayRow?)null);

        var factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITenantStatusCache>();
            services.AddSingleton<ITenantStatusCache, FixedTenantStatusCache>();
            services.RemoveAll<IDlqRepository>();
            services.AddSingleton(dlq);
            services.AddHttpClient("JaegerOtlpHttp").ConfigurePrimaryHttpMessageHandler(() => jaeger);
        }));
        return (factory.CreateClient(), jaeger, dlq);
    }

    private static Task<HttpResponseMessage> PostTracesAsync(HttpClient client, string? tenantId)
    {
        var claims = new List<Claim>();
        if (tenantId is not null)
            claims.Add(new Claim("tenant_id", tenantId));
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"resourceSpans":[]}"""))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            TestJwtFactory.CreateToken(AuthTestWebApplicationFactory.ConsoleAudience, "traces-tenant-check-user", extraClaims: claims));
        return client.SendAsync(request);
    }

    private static HttpRequestMessage DlqRequest(HttpMethod method, string path, string? actingTenantId)
    {
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingClaims = new List<Claim> { new("groups", "operators") };
        if (actingTenantId is not null)
            actingClaims.Add(new Claim("tenant_id", actingTenantId));
        var actingUserToken = TestJwtFactory.CreateToken("test-actinguser-audience", "test-user", extraClaims: actingClaims);

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");
        return request;
    }

    // ── /v1/traces ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("tenant_unknown")]
    [InlineData("tenant_suspended")]
    [InlineData("tenant_deleted")]
    public async Task Traces_ConsoleTokenOfATenantThatIsNotActive_Returns403_AndIsNeverForwarded(string tenantId)
    {
        var (client, jaeger, _) = CreateClient();

        var response = await PostTracesAsync(client, tenantId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        jaeger.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("tenant_active")]
    [InlineData(null)]
    public async Task Traces_ConsoleTokenOfAnActiveTenantOrWithNoTenant_IsForwarded(string? tenantId)
    {
        var (client, jaeger, _) = CreateClient();

        var response = await PostTracesAsync(client, tenantId);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        jaeger.CallCount.Should().Be(1);
    }

    // ── /admin/dlq and /admin/dlq/{id}/replay ────────────────────────────────

    [Theory]
    [InlineData("tenant_unknown")]
    [InlineData("tenant_suspended")]
    [InlineData("tenant_deleted")]
    public async Task ListDlq_ActingUserOfATenantThatIsNotActive_Returns403_AndNeverReadsTheQueue(string tenantId)
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(DlqRequest(HttpMethod.Get, "/admin/dlq", tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await dlq.DidNotReceive().ListUnreplayedAsync(Arg.Any<int>());
    }

    [Theory]
    [InlineData("tenant_active")]
    [InlineData(null)]
    public async Task ListDlq_ActingUserOfAnActiveTenantOrWithNoTenant_Returns200(string? tenantId)
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(DlqRequest(HttpMethod.Get, "/admin/dlq", tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await dlq.Received(1).ListUnreplayedAsync(Arg.Any<int>());
    }

    [Theory]
    [InlineData("tenant_unknown")]
    [InlineData("tenant_suspended")]
    [InlineData("tenant_deleted")]
    public async Task ReplayDlq_ActingUserOfATenantThatIsNotActive_Returns403_BeforeTheRowIsRead(string tenantId)
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(
            DlqRequest(HttpMethod.Post, $"/admin/dlq/{Guid.NewGuid()}/replay", tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await dlq.DidNotReceive().GetUnreplayedByIdAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task ReplayDlq_ActingUserOfAnActiveTenant_ReachesTheRowLookup()
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(
            DlqRequest(HttpMethod.Post, $"/admin/dlq/{Guid.NewGuid()}/replay", "tenant_active"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound); // past the gate; the fake has no row
        await dlq.Received(1).GetUnreplayedByIdAsync(Arg.Any<Guid>());
    }
}
```

**(f) The existing DLQ success test.** In `AuthenticationPipelineTests.cs`, add `using Iverson.Api.Tenancy;` to the usings. Then replace the whole of `ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds` with:

```csharp
    [Fact]
    public async Task ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds()
    {
        // tenant-a must be active: /admin/dlq refuses an acting user whose tenant is not (CSR
        // round 11 #21), and this host's no-op tenant repository knows no tenant at all.
        var tenantStatusCache = Substitute.For<ITenantStatusCache>();
        tenantStatusCache.GetStatusAsync("tenant-a").Returns(Task.FromResult<string?>("active"));
        var client = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITenantStatusCache>();
            services.AddSingleton(tenantStatusCache);
        })).CreateClient();

        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user",
            extraClaims: [new Claim("tenant_id", "tenant-a"), new Claim("groups", "operators")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
```

(This test passes before and after the change. It is edited only so it keeps passing once the DLQ check lands.)

- [ ] **Step 2: Run them and watch them fail**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Iverson.Api.Tests.Grpc.TenantAdminGrpcServiceTests.|FullyQualifiedName~Iverson.Api.Tests.Grpc.AuthorizationFieldMaskingTests.|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectMappingGrpcServiceTests.Update_|FullyQualifiedName~Iverson.Api.Tests.Grpc.ObjectPersistenceGrpcServiceTests.Update_|FullyQualifiedName~Iverson.Api.Tests.Grpc.TenantLifecycleGrpcServiceTests.|FullyQualifiedName~Iverson.Api.Tests.InactiveTenantHttpEndpointTests.|FullyQualifiedName~Iverson.Api.Tests.AuthenticationPipelineTests."
```

Expected failures:
- the two re-pointed `RemoveUser_*` order tests: fewer calls than listed;
- `EnforceWriteAuthorization_OwnerMismatchOnALegacyTenantColumnSchema_AnswersLikeAnAbsentRow`: PERMISSION_DENIED "Tenant field is immutable.";
- Mapping `Update_WithNonMatchingOwner_AnswersNotFoundLikeAnAbsentRow` and `Update_WithNonMatchingOwnerAndADifferentTenantValue_OnALegacyTenantColumnSchema_AnswersLikeAnAbsentRow`;
- Persistence `Update_WithNonMatchingOwner_AnswersNotFoundLikeAnAbsentRow`;
- both `*_DeletedTenant_*` tests;
- all three `Traces_…NotActive…` cases, which are forwarded with 202;
- all three `ListDlq_…NotActive…` cases (200);
- all three `ReplayDlq_…NotActive…` cases (404, with the row lookup received).

Everything else in the filter passes. That includes `Traces_…ActiveTenantOrWithNoTenant…`, `ListDlq_…ActiveTenantOrWithNoTenant…`, `ReplayDlq_…ReachesTheRowLookup` and the edited `ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds`.

These suites boot `WebApplicationFactory` hosts but no containers.

- [ ] **Step 3: Implement**

**3.1 `TenantAdminGrpcService.RemoveUser` (`:66-70`).** Replace

```csharp
        await tokenRevocations.RevokeAsync(user.Uid);
        tokenRevocationCache.Invalidate();
        await authentikAdminClient.DeactivateUserAsync(request.UserId);
        auditLog.AdminOperation(context.GetHttpContext().User, "RemoveUser", request.UserId);
```

with

```csharp
        await tokenRevocations.RevokeAsync(user.Uid);
        tokenRevocationCache.Invalidate();
        await authentikAdminClient.DeactivateUserAsync(request.UserId);
        // Revoke again (CSR round 11 #13): a token refreshed between the first revoke and the
        // deactivation has an iat after the first revocation. As in SetTenantAdmin's demotion.
        await tokenRevocations.RevokeAsync(user.Uid);
        tokenRevocationCache.Invalidate();
        auditLog.AdminOperation(context.GetHttpContext().User, "RemoveUser", request.UserId);
```

**3.2 `AuthorizationFieldMasking.EnforceWriteAuthorization`.**

Doc for `deniedMessage` (`:45-49`). Replace

```csharp
    /// <param name="deniedMessage">
    /// Exception message used both when the caller has no access at all and — for the
    /// existing-row branch — when an ownership mismatch is detected. Callers pass the
    /// create- or update-specific wording ("Not authorized to create/update this entity.").
    /// </param>
```

with

```csharp
    /// <param name="deniedMessage">
    /// Exception message used when the caller has no access at all and — for the existing-row
    /// branch — on a tenant mismatch. Callers pass the create- or update-specific wording ("Not
    /// authorized to create/update this entity."). An ownership mismatch is answered as an absent
    /// row instead (CSR round 11 #14).
    /// </param>
```

The absent-row throw (`:110-112`). Replace

```csharp
        if (requireExistingRow && existingRowJson is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"'{schema.TypeName}:{resourceKey}' not found."));
```

with

```csharp
        // One construction for both NotFound answers below, so another owner's row and an absent
        // one cannot drift apart.
        RpcException RowNotFound() =>
            new(new Status(StatusCode.NotFound, $"'{schema.TypeName}:{resourceKey}' not found."));

        if (requireExistingRow && existingRowJson is null)
            throw RowNotFound();
```

The tenant and owner block (`:135-171`). Replace

```csharp
            // Tenant match + immutability are unconditional — they apply even to bypass
            // callers, unlike the ownership check below.
            if (decision.TenantColumn is not null)
            {
                // Unreachable today (CSR round 9 Finding #5): both Update call sites now narrow
                // their existing-row read to the acting tenant, so existingRowJson never carries a
                // foreign tenant's value here. Kept as a fail-closed backstop for any future or
                // out-of-band caller that passes a foreign-tenant existingRowJson — do not delete.
                if (StructFieldAccess.GetFieldString(existingStruct, decision.TenantColumn) != decision.TenantValue)
                {
                    auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "TenantMismatch");
                    throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
                }
                var attemptedTenant = StructFieldAccess.GetFieldString(payload, decision.TenantColumn);
```

with

```csharp
            // Tenant match + immutability are unconditional — they apply even to bypass
            // callers, unlike the ownership check between them.
            //
            // Unreachable today (CSR round 9 Finding #5): both Update call sites now narrow
            // their existing-row read to the acting tenant, so existingRowJson never carries a
            // foreign tenant's value here. Kept as a fail-closed backstop for any future or
            // out-of-band caller that passes a foreign-tenant existingRowJson — do not delete.
            if (decision.TenantColumn is not null &&
                StructFieldAccess.GetFieldString(existingStruct, decision.TenantColumn) != decision.TenantValue)
            {
                auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "TenantMismatch");
                throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
            }

            // Another owner's row gets exactly an absent row's answer (CSR round 11 #14), so the
            // status cannot tell a caller that a key exists under someone else; the audit entry
            // keeps the real reason. BEFORE tenant immutability: on a legacy schema whose tenant
            // column is client-declared, a payload naming a different tenant would otherwise be
            // refused as immutable for another owner's row and answered NotFound for an absent one.
            if (decision.OwnershipRequired &&
                StructFieldAccess.GetFieldString(existingStruct, decision.OwnerFieldName!) != decision.OwnerValue)
            {
                auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "OwnerMismatch");
                throw RowNotFound();
            }

            if (decision.TenantColumn is not null)
            {
                var attemptedTenant = StructFieldAccess.GetFieldString(payload, decision.TenantColumn);
```

Then delete the old owner-mismatch block, which now follows the closing brace of the tenant block (formerly `:166-171`):

```csharp
            if (decision.OwnershipRequired &&
                StructFieldAccess.GetFieldString(existingStruct, decision.OwnerFieldName!) != decision.OwnerValue)
            {
                auditLog.Denied(actingUser, auditAction, schema.TypeName, resourceKey, "OwnerMismatch");
                throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
            }

```

Leave the remainder of the tenant block in place:
- the `TenantImmutable` throw;
- the `SetAuthoritativeField(payload, decision.TenantColumn, decision.TenantValue!)` force-set and its comment;
- its closing brace.

After the edit, the order inside `else { … }` is:
1. `PreserveExactIntegers`;
2. TenantMismatch;
3. OwnerMismatch (NotFound);
4. TenantImmutable plus the tenant force-set;
5. OwnerImmutable.

Verify with `command grep -n '"TenantMismatch"\|"OwnerMismatch"\|"TenantImmutable"\|"OwnerImmutable"' Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`, which should print ascending line numbers in that order.

**3.3 `TenantLifecycleGrpcService.SetStatusAsync` (`:88-90`).** Replace

```csharp
        var existing = await tenantRepository.GetAsync(tenantId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Tenant '{tenantId}' not found."));
        await tenantRepository.UpdateStatusAsync(tenantId, status);
```

with

```csharp
        var existing = await tenantRepository.GetAsync(tenantId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Tenant '{tenantId}' not found."));
        // Deletion is terminal (CSR round 11 #21): neither SuspendTenant nor ReactivateTenant may
        // move a deleted tenant, whose Authentik users DeleteTenant has already deactivated.
        if (existing.Status == "deleted")
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Tenant '{tenantId}' is deleted."));
        await tenantRepository.UpdateStatusAsync(tenantId, status);
```

**3.4 `Program.cs`.**

`/admin/dlq` (`:608`). Replace

```csharp
app.MapGet("/admin/dlq", async (IDlqRepository dlq, AuditLog audit, HttpContext httpContext) =>
```

with

```csharp
app.MapGet("/admin/dlq", async (IDlqRepository dlq, Iverson.Api.Tenancy.ITenantStatusCache tenantStatusCache, AuditLog audit, HttpContext httpContext) =>
```

In the same handler, replace

```csharp
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // An acting user with NO tenant_id claim must not fall through as though they matched every
```

with

```csharp
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // CSR round 11 #21: an acting user whose tenant is not active is refused, as on the gRPC
    // surfaces and the admin console. ActiveTenantEndpointFilter reads HttpContext.User, which here
    // is the service token, so the acting principal is checked inline. No tenant_id passes.
    if (!string.IsNullOrEmpty(actingTenantId)
        && await tenantStatusCache.GetStatusAsync(actingTenantId) is null or "suspended" or "deleted")
        return Results.Forbid();

    // An acting user with NO tenant_id claim must not fall through as though they matched every
```

`/admin/dlq/{id}/replay` (`:631`). Replace

```csharp
app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, IEventProducer events, AuditLog audit, HttpContext httpContext) =>
```

with

```csharp
app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, IEventProducer events, Iverson.Api.Tenancy.ITenantStatusCache tenantStatusCache, AuditLog audit, HttpContext httpContext) =>
```

and in that handler replace

```csharp
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // Same null-tenant_id-claim bypass as /admin/dlq (see comment there), checked BEFORE the row
```

with

```csharp
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // Same inactive-tenant refusal as /admin/dlq (see comment there), BEFORE the row fetch.
    if (!string.IsNullOrEmpty(actingTenantId)
        && await tenantStatusCache.GetStatusAsync(actingTenantId) is null or "suspended" or "deleted")
        return Results.Forbid();

    // Same null-tenant_id-claim bypass as /admin/dlq (see comment there), checked BEFORE the row
```

(Each replacement's first line, together with the comment line that follows, is unique to its handler.)

`/v1/traces` (`:795`). Replace

```csharp
    }).RequireAuthorization("ConsoleClient").RequireRateLimiting("traces")
```

with

```csharp
    }).RequireAuthorization("ConsoleClient")
        // CSR round 11 #21: the console token's tenant must be active; no tenant_id passes.
        .AddEndpointFilter<ActiveTenantEndpointFilter>()
        .RequireRateLimiting("traces")
```

(`using Iverson.Api.Console;` is already at the top of `Program.cs`.)

**3.5 `ActiveTenantEndpointFilter.cs` doc (`:7-8`).** Replace (line 8 continues past "already do.", so quote both full lines)

```csharp
/// CSR round-10 #14: the admin console's endpoints refuse a caller whose tenant is not active,
/// as the gRPC surfaces already do. The rule is <c>ActingUserInterceptor</c>'s: a caller with a
```

with

```csharp
/// CSR round-10 #14: the admin console's endpoints refuse a caller whose tenant is not active,
/// as the gRPC surfaces already do; CSR round 11 #21 adds <c>/v1/traces</c>. The rule is
/// <c>ActingUserInterceptor</c>'s: a caller with a
```

- [ ] **Step 4: Run the tests and watch them pass**

Re-run the Step 2 command. Expected: 0 failed. Then run the non-container suite:

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server && systemd-run --user --scope -q -p MemoryMax=6G dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "Category!=Integration"
```

Expected: 0 failed. That includes:
- `TracesRelayEndpointTests`, whose tokens carry no `tenant_id`;
- `AdminConsoleCorsPipelineTests`, whose preflights are answered by CORS before the endpoint runs;
- `AdminConsoleActiveTenantPipelineTests`;
- `OwnerPrivateTypeVisibilityTests`.

- [ ] **Step 5: Mutation proof**

Record `git diff | md5sum` first and restore it after each mutation. Run the Step 2 filter.

1. **The second revoke.**
   - **Mutation:** delete the second `await tokenRevocations.RevokeAsync(user.Uid);` in `RemoveUser`, the one after `DeactivateUserAsync`.
   - **Must fail:** `RemoveUser_RevokesTheUsersTokensBeforeAndAfterDeactivatingInAuthentik` and `RemoveUser_InvalidatesTheRevocationCacheAfterEachRevoke`.
2. **Owner-mismatch status.**
   - **Mutation:** in the OwnerMismatch branch, change `throw RowNotFound();` to `throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));`.
   - **Must fail:** both `Update_WithNonMatchingOwner_AnswersNotFoundLikeAnAbsentRow` tests, the Mapping legacy test, and the masking legacy test.
3. **Owner-mismatch ordering.**
   - **Mutation:** move the whole OwnerMismatch `if` block to sit after the closing brace of the `if (decision.TenantColumn is not null) { var attemptedTenant … }` block.
   - **Must fail:** `EnforceWriteAuthorization_OwnerMismatchOnALegacyTenantColumnSchema_AnswersLikeAnAbsentRow` and `Update_WithNonMatchingOwnerAndADifferentTenantValue_OnALegacyTenantColumnSchema_AnswersLikeAnAbsentRow`. Both get PERMISSION_DENIED "Tenant field is immutable.".
4. **Deleted is terminal.**
   - **Mutation:** change `if (existing.Status == "deleted")` to `if (existing.Status == "never")`.
   - **Must fail:** `SuspendTenant_DeletedTenant_ThrowsFailedPreconditionAndDoesNotUpdate` and `ReactivateTenant_DeletedTenant_ThrowsFailedPreconditionAndDoesNotUpdate`.
5. **The traces filter.**
   - **Mutation:** delete the line `.AddEndpointFilter<ActiveTenantEndpointFilter>()` on `/v1/traces`.
   - **Must fail:** all three `Traces_ConsoleTokenOfATenantThatIsNotActive_Returns403_AndIsNeverForwarded` cases.
6. **The DLQ list check.**
   - **Mutation:** in the `/admin/dlq` handler, change `if (!string.IsNullOrEmpty(actingTenantId)` to `if (false && !string.IsNullOrEmpty(actingTenantId)`.
   - **Must fail:** all three `ListDlq_ActingUserOfATenantThatIsNotActive_*` cases.
7. **The DLQ replay check.**
   - **Mutation:** the same change in the replay handler.
   - **Must fail:** all three `ReplayDlq_ActingUserOfATenantThatIsNotActive_*` cases.

- [ ] **Step 6: Commit**

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz && \
git add Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs \
  Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs \
  Iverson.Server/Iverson.Api/Grpc/TenantLifecycleGrpcService.cs \
  Iverson.Server/Iverson.Api/Program.cs \
  Iverson.Server/Iverson.Api/Console/ActiveTenantEndpointFilter.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/TenantAdminGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/AuthorizationFieldMaskingTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/Grpc/TenantLifecycleGrpcServiceTests.cs \
  Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs \
  Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs && \
git commit -m "close the removal, owner-mismatch and tenant-lifecycle gaps

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

### Task 11: Verification

No commits. Live checks use an isolated compose project and a throwaway StarRocks container, never kind (user decision at plan time). The laptop kind profile has no StarRocks, and the local profile's StarRocks requests about 8Gi on a 9GB VM. Run one heavy process at a time, and check `free -g` before each.

- [ ] **Step 1: Full suites.** These run one at a time; the container-backed classes are inside them.

```bash
cd /home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Server
for p in Iverson.Api.Tests Iverson.ClientConformance.Tests Iverson.Embeddings.Tests Iverson.Events.Tests Iverson.LoadTest.Tests Iverson.Patterns.Tests Iverson.Sql.Tests Iverson.StarRocks.Tests Iverson.Vector.Tests; do
  echo "$p: $(systemd-run --user --scope -q -p MemoryMax=7G dotnet test $p/$p.csproj 2>&1 | command grep -E '^(Passed|Failed)!' | tail -1)"; done
```

Expected: every line reads `Passed!`, with 0 failed.

- [ ] **Step 2: Mutation proofs not already run.** Read every task report under `$SCR`. For each guard in spec §6 "Mutation proof" whose task report records no failing test under its mutation:
  1. apply that mutation in the worktree;
  2. run that task's test filter and record the failing test;
  3. revert with `git checkout -- <file>`.

  End with `git status --porcelain` empty.

- [ ] **Step 3: Record the user's state.** First check that the user's stack is down: `docker ps --format '{{.Names}}'`. If the list isn't empty, **stop and ask the user**. Never stop their containers yourself: the isolated project binds the same host ports.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh; mkdir -p $LIVE
docker ps -a --format '{{.Names}}' | sort > $LIVE/before-containers.txt
docker volume ls -q | sort > $LIVE/before-volumes.txt
docker network ls --format '{{.Name}}' | sort > $LIVE/before-networks.txt
docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort > $LIVE/before-images.txt
```

- [ ] **Step 4: Run the Helm job's own SQL against a throwaway StarRocks, as a fresh install and again as a re-run.**
  1. Render the job from the branch chart on the local profile, which has StarRocks.
  2. Extract its script, point `FE_HOST` at the container's own FE, and run it inside a memory-capped `starrocks/allin1-ubuntu:4.1.1`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh; mkdir -p $LIVE/job
cd $WT/Iverson.Server/deploy/helm/iverson && helm dependency build . >/dev/null
helm template iverson . -n iverson -f values-local.yaml > $LIVE/job/local.yaml
python3 - <<PY
import yaml
docs=[d for d in yaml.safe_load_all(open("$LIVE/job/local.yaml")) if d and d.get("kind")=="Job" and d["metadata"]["name"]=="iverson-starrocks-create-user"]
s=docs[0]["spec"]["template"]["spec"]["containers"][0]["args"][0]
s=s.replace('FE_HOST="iverson-starrocks-fe-service"','FE_HOST="127.0.0.1"')
assert 'FE_HOST="127.0.0.1"' in s
open("$LIVE/job/script.sh","w").write(s)
PY
free -g | sed -n 2p
docker run -d --name csr11a-jobcheck --memory 6g --memory-swap 6g starrocks/allin1-ubuntu:4.1.1 >/dev/null
for i in $(seq 1 40); do docker exec csr11a-jobcheck mysql -h127.0.0.1 -P9030 -uroot --batch --skip-column-names -e "SHOW BACKENDS" 2>/dev/null | awk -F'\t' '{print $9}' | command grep -q true && break; sleep 5; done
docker cp $LIVE/job/script.sh csr11a-jobcheck:/tmp/job.sh
for run in fresh rerun; do
  docker exec -e ROOT_PW=jobcheck-root -e APP_PW=jobcheck-app -e PROVISIONER_PW=jobcheck-prov csr11a-jobcheck sh /tmp/job.sh > $LIVE/job/$run.log 2>&1; echo "$run exit=$?"
done
docker exec -e MYSQL_PWD=jobcheck-root csr11a-jobcheck mysql -h127.0.0.1 -P9030 -uroot --batch -e "SHOW GRANTS FOR 'iverson_app'@'%'; SHOW GRANTS FOR 'iverson_provisioner'@'%'; SHOW DATABASES LIKE 'iverson';"
docker rm -f csr11a-jobcheck >/dev/null && echo removed
```

Expected:
- `fresh exit=0` and `rerun exit=0`;
- `iverson_app`'s grants are `OPERATE ON SYSTEM` only. There is no `iverson.*` grant, no `CREATE DATABASE` and no `user_admin`.
- `iverson_provisioner`'s grants are `CREATE DATABASE ON CATALOG default_catalog` and `user_admin`.
- the database `iverson` is listed;
- `removed`.

- [ ] **Step 5: Stand up the isolated compose project from the worktree.**
  - **What the setup does.** It uses C1's pattern: a separate project name, `container_name:` stripped, `--project-directory` on the worktree, and the worktree's own generated `.env`. `generate-compose-secrets.sh` writes that `.env` into the worktree, never into the main checkout's `Iverson.Server/.env`.
  - **Running it.** Run `$DC up` with `run_in_background`.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
cat >> /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh <<'X'
ENVF=$WT/Iverson.Server/.env
DC="docker compose -p csr11a -f $LIVE/compose.yml --project-directory $WT/Iverson.Server --env-file $ENVF"
val() { command grep "^$1=" "$ENVF" | cut -d= -f2-; }
scratch_env() {
  export HOME=$LIVE/home NUGET_PACKAGES=/home/ben/.nuget/packages PYTHONUSERBASE=/home/ben/.local
  export GOMODCACHE=/home/ben/go/pkg/mod MAVEN_OPTS=-Dmaven.repo.local=/home/ben/.m2/repository npm_config_cache=/home/ben/.npm
}
X
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh; mkdir -p $LIVE/home; umask 077
bash $WT/scripts/generate-compose-secrets.sh >/dev/null
command grep -v container_name $WT/Iverson.Server/docker-compose.yml | sed 's/^    image: iverson-api$/    image: csr11a-api/' > $LIVE/compose.yml
command grep -c 'image: csr11a-api' $LIVE/compose.yml
free -g | sed -n 2p
$DC up -d --build > $LIVE/up.log 2>&1; echo "up exit=$?" >> $LIVE/up.log
```

Expected:
- `grep -c` prints `2`;
- `up exit=0`;
- within 10 minutes, `docker inspect -f '{{.State.Health.Status}}' csr11a-iverson-api-1` prints `healthy`;
- `docker inspect -f '{{.State.ExitCode}}' csr11a-starrocks-init-1` prints `0`.

- [ ] **Step 6: Re-run `starrocks-init`, then check the grants.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
$DC up --no-deps --force-recreate starrocks-init > $LIVE/init-rerun.log 2>&1; echo "rerun exit=$(docker inspect -f '{{.State.ExitCode}}' csr11a-starrocks-init-1)"
docker exec csr11a-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch -e "SHOW GRANTS FOR 'iverson_app'@'%'; SHOW GRANTS FOR 'iverson_provisioner'@'%';"
docker logs csr11a-iverson-worker-1 2>&1 | command grep -c -i "StarRocksProvisioner.*required"
```

Expected:
- `rerun exit=0`;
- the grants are as in Step 4: tenant roles appear on `iverson_app` only after Step 7;
- the last grep prints `0`, meaning the worker started with its provisioner string.

- [ ] **Step 7: Tenant traffic through the API, with the pipeline join.** Build a scratch console that does the following:
  1. registers two owner-scoped types as unowned types, using the loadtest service token (which has `schema_admin`);
  2. posts an author and a book as the compose smoke-test acting user;
  3. waits for the StarRocks projection, which the worker writes through `iverson_provisioner` on that tenant's first event;
  4. runs a pipeline RIGHT join.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
mkdir -p $LIVE/probe && cd $LIVE/probe
cat > probe.csproj <<'X'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="/home/ben/repositories/Iverson/.worktrees/csr11-server-authz/Iverson.Clients/DotNet/Iverson.Client.Core/Iverson.Client.Core.csproj" /></ItemGroup></Project>
X
cat > Program.cs <<'X'
// env: TOKEN (service access token), ACTING (acting-user token). args: grpcUrl mode(register|post|query)
using Google.Protobuf.WellKnownTypes; using Grpc.Core; using Grpc.Net.Client; using Iverson.Client.Contracts;
using var channel = GrpcChannel.ForAddress(args[0]);
var service = new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" } };
var acting = new Metadata { { "authorization", $"Bearer {Environment.GetEnvironmentVariable("TOKEN")}" },
                            { "x-acting-user-authorization", $"Bearer {Environment.GetEnvironmentVariable("ACTING")}" } };
var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
var search = new ObjectSearchService.ObjectSearchServiceClient(channel);
static PropertyDescriptor P(string n, ObjectType t, bool key = false) => new() { Name = n, ObjectType = t, IsKey = key };
static TypeDescriptor T(string name, params PropertyDescriptor[] props)
{
    var td = new TypeDescriptor { TypeName = name, Authorization = new AuthorizationRules { OwnerField = "OwnerId" } };
    td.Properties.Add(props); return td;
}
switch (args[1])
{
    case "register":
        await mapping.RegisterSchemaAsync(new SchemaRequest { RootType = T("ProbeAuthor", P("Id", ObjectType.Guid, true), P("OwnerId", ObjectType.String), P("Name", ObjectType.String)) }, service);
        await mapping.RegisterSchemaAsync(new SchemaRequest { RootType = T("ProbeBook", P("Id", ObjectType.Guid, true), P("OwnerId", ObjectType.String), P("Title", ObjectType.String), P("ProbeAuthorRef", ObjectType.String)) }, service);
        Console.WriteLine("registered"); break;
    case "post":
        var author = await mapping.PostAsync(new MappingWriteRequest { TypeName = "ProbeAuthor",
            Payload = new Struct { Fields = { ["Name"] = Value.ForString("own-author") } } }, acting);
        var authorId = author.Data.Fields["Id"].StringValue;
        await mapping.PostAsync(new MappingWriteRequest { TypeName = "ProbeBook",
            Payload = new Struct { Fields = { ["Title"] = Value.ForString("own-book"), ["ProbeAuthorRef"] = Value.ForString(authorId) } } }, acting);
        Console.WriteLine($"posted author={authorId}"); break;
    case "query":
        var step = new PipelineStep { Name = "joined" };
        var join = new PipelineJoin { Source = "ProbeAuthor", Kind = JoinKind.Right };
        join.On.Add(new JoinCondition { Left = "ProbeAuthorRef", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { Source = "ProbeAuthor", Column = "Name", Alias = "author_name" });
        var req = new PipelineRequest { TypeName = "ProbeBook" }; req.Steps.Add(step);
        using (var call = search.Pipeline(req, acting))
            await foreach (var r in call.ResponseStream.ReadAllAsync())
                Console.WriteLine($"row author_name={r.Data.Fields["author_name"]}");
        break;
}
X
systemd-run --user --scope -q -p MemoryMax=3G dotnet build -c Release 2>&1 | command grep -E "error|Build succeeded" | head -3
cat > $LIVE/tokens.sh <<'X'
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
export TOKEN="$(curl -s -H 'Host: authentik-server:9000' -d grant_type=client_credentials -d client_id=dev-iverson-loadtest-client-id \
  --data-urlencode client_secret="$(val IVERSON_LOADTEST_CLIENT_SECRET)" -d scope="schema_admin tenant_id_loadtest" \
  http://localhost:9000/application/o/token/ | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')"
export ACTING="$(python3 $WT/Iverson.Server/deploy/scripts/mint_acting_user_token.py --target compose --password "$(val IVERSON_SMOKE_TEST_PASSWORD)" 2>/dev/null | tail -1)"
X
source $LIVE/tokens.sh
P=$LIVE/probe/bin/Release/net10.0/probe.dll
dotnet $P http://localhost:8080 register && dotnet $P http://localhost:8080 post
for i in $(seq 1 24); do out="$(dotnet $P http://localhost:8080 query 2>&1)"; echo "$out" | command grep -q own-author && break; sleep 5; done; echo "$out"
docker exec csr11a-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch -e "SHOW GRANTS FOR 'iverson_app'@'%'; SHOW GRANTS FOR 'iverson_provisioner'@'%';"
```

Expected:
- `registered`;
- `posted author=<uuid>`;
- within two minutes, a `row author_name="own-author"` line;
- `iverson_app` now holds `OPERATE ON SYSTEM` plus `role_tenant_tenant_smoke_test_…`;
- `iverson_provisioner` holds the same tenant role, plus `ALTER ON ALL TABLES IN DATABASE iverson_tenant_tenant_smoke_test`.

- [ ] **Step 8: Live owner isolation on the RIGHT join.** As root, insert one `ProbeAuthor` row owned by another user directly into the tenant's table, then repeat the query.

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
docker exec csr11a-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot --batch -e "DESCRIBE iverson_tenant_tenant_smoke_test.probe_authors"
```

Read the `DESCRIBE` output. Insert one row with:
- every non-nullable column set;
- `Id` set to a fresh UUID;
- `OwnerId` set to `someone-else`;
- `Name` set to `foreign-author`;
- the tenant column set to `tenant_smoke_test`.

Use `docker exec csr11a-starrocks-1 mysql -h127.0.0.1 -P9030 -uroot -e "INSERT INTO iverson_tenant_tenant_smoke_test.probe_authors (<columns>) VALUES (<values>)"`. Then:

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
source $LIVE/tokens.sh
dotnet $LIVE/probe/bin/Release/net10.0/probe.dll http://localhost:8080 query
```

Expected: the query prints `own-author`, and no `foreign-author` line.

- [ ] **Step 9: The full conformance harness** against the isolated stack, with every scenario and every language. The environment follows the harness's own help text (`Iverson.ClientConformance/Program.cs:320-337`).

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
( scratch_env
  export IVERSON_CLIENT_ID=dev-iverson-loadtest-client-id IVERSON_CLIENT_SECRET="$(val IVERSON_LOADTEST_CLIENT_SECRET)"
  export IVERSON_TOKEN_ENDPOINT=http://localhost:9000/application/o/token/ IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"
  export IVERSON_ACTING_USER_BYPASS_PASSWORD="$(val IVERSON_BYPASS_PASSWORD)" IVERSON_OTHER_TENANT_PASSWORD="$(val IVERSON_SMOKE_TEST_PASSWORD)"
  cd $WT/Iverson.Server/Iverson.ClientConformance && systemd-run --user --scope -q -p MemoryMax=4G dotnet run ) > $LIVE/conformance.out 2>&1; echo "harness exit=$?"
tail -25 $LIVE/conformance.out
```

Expected: `harness exit=0`, and every scenario row reads `ok` in all five language columns. That includes `identity`, which carries IVC-IDN-008, and the error-contract scenario, which carries IVC-ERR-005.

- [ ] **Step 10: Tear down and diff the user's state.**

```bash
source /tmp/claude-1000/-home-ben-repositories-Iverson/13ed280c-3679-411c-b94f-88727abea9b2/scratchpad/csr11-sdd/env.sh
$DC down -v > $LIVE/down.log 2>&1; echo "down exit=$?"
docker rmi csr11a-api localhost/csr11a-api >/dev/null 2>&1; true
docker ps -a --format '{{.Names}}' | sort | diff $LIVE/before-containers.txt - && echo containers-unchanged
docker volume ls -q | sort | diff $LIVE/before-volumes.txt - && echo volumes-unchanged
docker network ls --format '{{.Name}}' | sort | diff $LIVE/before-networks.txt - && echo networks-unchanged
out="$(docker images --format '{{.Repository}}:{{.Tag}} {{.ID}}' | sort | comm -23 $LIVE/before-images.txt -)"; [ -z "$out" ] && echo no-image-removed-or-repointed || printf 'CHANGED:\n%s\n' "$out"
```

Expected:
- `down exit=0`;
- `containers-unchanged`, `volumes-unchanged` and `networks-unchanged`;
- `no-image-removed-or-repointed`.

Remove any other image this task built (`*csr11a*`) and list the new base images pulled.

## Known issues inherited from spec

- **A wrong provisioner password stalls the worker's engagement consumer** instead of dead-lettering (user choice, CDR-1 §3.1 B). AccessDenied is wire-level ambiguous: the same code covers "not yet created by the hook" and "wrong password". The readiness checker already accepts the same ambiguity (`EngagementHealthChecker.cs:32-45`).
- **`OPERATE ON SYSTEM` stays on `iverson_app`** (user decision, 2026-10-08). `SHOW BACKENDS` needs it, and it also permits `ADMIN SET FRONTEND CONFIG`, so a stolen query credential can change FE configuration. It gives no data access and can't create users or nodes (live probe). Expression injection can't issue statements, and putting it behind a role wouldn't stop a stolen credential.
- **RegisterSchema's cross-owner PermissionDenied** (`SchemaRegistrationOrchestrator.cs:78-81`) still tells a SchemaAdmin that a foreign type exists. It is privileged-only.
- **`POST /admin/reconcile/{typeName}`** resolves any registered type, foreign included. It is Operator-only and cross-tenant by design.
- **An empty, ungranted `iverson` database remains on every install** (CDR-2 §3.1). The job and `starrocks-init` keep creating it, because their revokes name it and StarRocks refuses a revoke on a missing database (Verified assumption 14).
- **Compose's provisioner password is a dev-only literal**, like `iverson_app`'s. Compose root is passwordless and loopback-only.
- **`WidenLargeTextColumnsAsync`'s `ALTER`** had no proven privilege before this change. No test drives it today; §6 adds one.
- **Plaintext StarRocks transport**, including the job's password literals, belongs to sub-project C (finding #4).
