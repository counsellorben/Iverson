# Dafny Plan Verification: 2026-10-16-csr-round12-server-implementation-plan (Round 1)

**Plan:** /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md
**Source spec:** docs/specs/2026-10-16-csr-round12-server-design.md
**Plan SHA:** c79d7de067519edb3b325fbc0aa53332944c6d1c | **Replay base:** c79d7de067519edb3b325fbc0aa53332944c6d1c | **Dafny:** 4.11.0+fcb2042d6d043a2634f0854338c08feeaaaf4ae2 | **Z3:** 4.12.1
**Dafny files:** docs/formal/2026-10-16-csr-round12-server-implementation-plan/

## 0. Coverage enumeration

### 0.1 Coverage

Every scanned block: 31 candidates (96 obligations) and 163 excluded blocks.

**Candidates:**

| Candidate | Task/step | File | Member |
|---|---|---|---|
| `SchemaBuilder.NormalizeDeclaredNames` | T1 S3 | `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` | `NormalizeDeclaredNames` |
| `SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties` | T1 S3 | `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` | `RejectCaseDuplicateProperties` |
| `SchemaRegistrationOrchestrator.NavigationColumnCheck` | T1 S3 | `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` | `RegisterAsync`: the navigation/column loop that 3e adds after the navigation/foreign-key collision loop |
| `PayloadKeyCanonicalizer.Canonicalize` | T2 S3 | `Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs` | `Canonicalize` |
| `StructSerializer.FoldKeys` | T2 S3 | `Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs` | `FoldKeys`, `ProtoValueToObject`, and `SerializePayload`'s column set |
| `EngagementRepository.UpsertEntries` | T3 S3 | `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` | `UpsertEntries` |
| `EngagementStoreConsumer.WithOwnerValue` | T3 S3 | `Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs` | `WithOwnerValue` |
| `PopularitySignalEligibility.IsEligible` | T4 S5 | `Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs` | both `IsEligible` overloads and `Readers` |
| `ObjectSearchGrpcService.EligiblePopularitySignal` | T4 S6 | `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` | `EligiblePopularitySignal` |
| `ObjectSearchGrpcService.ReadableDecayField` | T4 S6 | `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` | `ReadableDecayField` |
| `ObjectSearchGrpcService.SimilarResponseAllowList` | T4 S6 | `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` | `WriteSimilarResponsesAsync`, lines from `var columnLookup = …` through the per-row `foreach (var kvp in payload)` loop, before `MaskDisallowedFields` |
| `PopularitySignalValidator.SignalKeyCollision` | T4 S6 | `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs` | `PopularitySignalValidator.ValidateAtStartup`: the per-signal loop as T4 leaves it; the new collision check is the logic under test |
| `EnrichmentTargetRules.Violation` | T5 S3 | `Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs` | `Violation` |
| `SchemaRegistryRepository.OwnerQueries` | T7 S3 | `Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs` | `GetOwnersAsync`, `UpsertIfOwnerAsync`, `ExistsAsync` |
| `PrivilegedRequestRepository.Transitions` | T7 S3 | `Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs` | `InsertAsync`, `ExpireIfPendingAsync`, `ListClaimableAsync`, `TryClaimAsync`, `FailAbandonedAsync`, `DeleteFinishedOlderThanAsync` |
| `PostgresPrivilegedRequestLocks.Keys` | T7 S3 | `Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs` | `RegistrationKey`, `TypeKeyBase`, and the key expressions of `AcquireRegistrationAsync` and `AcquireTypeAsync` |
| `SchemaRegistrationApplier.ExecuteAsync` | T7 S3 | `Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs` | `ExecuteAsync`, `RequireAdoptableAsync`, `NotAdoptable`, `OwnedByAnotherTenant` |
| `QueuedSchemaRegistrationExecutor.ExecuteAsync` | T7 S3 | `Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs` | `ExecuteAsync` |
| `PrivilegedRequestExecutor.RunAsync` | T7 S3, T8 S3 | `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs` | `RunAsync`, `RegisterSchemaAsync`, `ReconcileTypeAsync`, `ReplayDlqAsync`, `Deserialize` |
| `PrivilegedRequestWorker.PollOnceAsync` | T7 S3 | `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs` | `PollOnceAsync`, `RunClaimedAsync` |
| `Program.PrivilegedAdminRoutes` | T8 S3 | `Iverson.Server/Iverson.Api/Program.cs` | the `/admin/reconcile/{typeName}` handler from `privileged.SubmitAsync` to its return (3e), and the `/admin/dlq/{id}/replay` handler from `dlq.GetUnreplayedSummaryAsync` to its return (3f) |
| `Program.Health` | T9 S5 | `Iverson.Server/Iverson.Api/Program.cs` | the `/health` handler body (5h, first hunk, with the unchanged tail that picks 200 or 503) |
| `ObjectRetrievalGrpcService.GetManyTenantGuard` | T9 S5 | `Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs` | `GetMany`, lines from the new `if (decision.TenantColumn is null)` guard through the `FetchManyByKeysAsync` call (5f) |
| `ObjectMappingGrpcService.DeleteTenantGuard` | T9 S5 | `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` | `Delete`, lines from the new `if (decision.TenantColumn is null)` guard through the `ExecuteInTransactionAsync` block (5f) |
| `OutboxWriter.UpsertAndEnqueueOutboxAsync` | T9 S5 | `Iverson.Server/Iverson.Sql/OutboxWriter.cs` | `UpsertAndEnqueueOutboxAsync` (5e) |
| `EngagementRepository.TenantScopedAccess` | T10 S3 | `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` | `TenantConnectionStringFor`, `IsRecordedAsync`, `RunTenantScopedAsync`, `StreamTenantScopedAsync`, and the tenant-scoped paths of `SearchAsync`, `AggregateAsync`, `GroupByAsync`, `PipelineAsync`, `MatchRowsAsync`, `UpsertAsync`, `DeleteAsync` |
| `EngagementRepository.OpenOrSurfaceRefusalAsync` | T10 S3 | `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` | `OpenOrSurfaceRefusalAsync`, `OpenTenantConnectionAsync`, `OpenProvisionerConnectionAsync` |
| `EngagementRepository.IsExpectedMissingResourceError` | T10 S3 | `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` | `IsExpectedMissingResourceError` |
| `EngagementRepository.EnsureTenantProvisionedAsync` | T10 S3 | `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` | `EnsureTenantProvisionedAsync` (as T10 leaves it) |
| `PostgresProvisionedTenantDirectory.IsProvisionedAsync` | T10 S3 | `Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs` | `IsProvisionedAsync`, `RecordProvisionedAsync` |
| `StarRocksResiliencePipelineFactory.IsBackendTransient` | T11 S3 | `Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs` | `IsBackendTransient`, `IsPoolExhausted`, and the two `ShouldHandle` predicates in `Build` |

**Excluded:**

- T0 S1 `/tmp/.../csr12-sdd/env.sh` (scratch environment file, outside the repo) — Config / declarative
- T1 S1 `Iverson.Server/Iverson.Api.Tests/Schema/DeclaredNameNormalizationTests.cs` — Tests
- T1 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (1b, three refusal tests) — Tests
- T1 S3 `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` (3a, and 3b's `});`: `BuildDescriptor` returns through `NormalizeDeclaredNames`) — Glue
- T1 S3 `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` (3c, `LoadAsync` calls `NormalizeDeclaredNames`) — Glue
- T1 S3 `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (3d, the `RejectCaseDuplicateProperties(typeDesc);` call) — Glue
- T2 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/CanonicalPayloadKeyRpcTests.cs` — Tests
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs` (`UpperFirst`, unchanged; `SerializePayload`'s `JsonSerializer.Serialize` call) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (3c, Post's `Canonicalize` call) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (3d, Post's `SerializePayload(…, schema)`) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (3e, Update's `Canonicalize` call) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (3f, Update's `SerializePayload(…, schema)`) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (3g, Post's `Canonicalize` call) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (3h, Post's `SerializePayload(…, schema)`) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (3i, Update's `Canonicalize` call) — Glue
- T2 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (3j, Update's `SerializePayload(…, schema)`) — Glue
- T3 S1 `Iverson.Server/Iverson.StarRocks.Tests/EngagementUpsertEntriesTests.cs` — Tests
- T3 S1 `Iverson.Server/Iverson.Api.Tests/Consumers/EngagementStoreConsumerTests.cs` — Tests
- T3 S3 `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (3a, `UpsertAsync` calls `UpsertEntries`) — Glue
- T4 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (1a, the eight re-fixtured ranking tests) — Tests
- T4 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (1b, helpers and gate/allow-list/decay tests) — Tests
- T4 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs` (1c) — Tests
- T4 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalOptionsTests.cs` (1d) — Tests
- T4 S1 `Iverson.Server/Iverson.Api.Tests/Consumers/PopularitySignalConsumerTests.cs` (1e) — Tests
- T4 S1 `Iverson.Server/Iverson.Api.Tests/Reconciliation/PopularitySignalReconciliationWorkerTests.cs` (1e) — Tests
- T4 S3 `Iverson.Server/Iverson.Api.Tests/Grpc/PopularitySignalEligibilityTests.cs` — Tests
- T4 S5 `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` (the `PopularityUpdateOutcome` doc comment) — Config / declarative
- T4 S5 `Iverson.Server/Iverson.Api/Consumers/PopularitySignalConsumer.cs` (`UpdateAsync`'s early `Skipped` on `!IsEligible(…)`; borderline) — Glue
- T4 S6 `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (SearchSimilar's `decayField`/`popularitySignal`/`rerankIsIdentity` hunk) — Glue
- T4 S6 `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`PopularityFor(popularitySignal, …)` call) — Glue
- T4 S6 `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`SearchChunksFusedAsync`'s two helper calls) — Glue
- T4 S6 `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`RetrievePopularityOrDegradeAsync` takes the signal) — Glue
- T4 S6 `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`PopularityFor` takes the signal) — Glue
- T4 S6 `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs` (the "not eligible" `LogWarning`; borderline) — Glue
- T5 S1 `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryTests.cs` (1a) — Tests
- T5 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (1b) — Tests
- T5 S1 `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs` (1c) — Tests
- T5 S3 `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (`ValidateEnrichmentTargets(descriptor);` call) — Glue
- T5 S3 `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (`ValidateEnrichmentTargets` throws on `Violation`) — Glue
- T5 S3 `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` (`LoadAsync` strips the targets of a violating descriptor and logs; borderline) — Glue
- T5 S3 `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs` (the hint passes through `EscapeUntrustedText`) — Glue
- T6 S1 `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryPostgresRoundTripTests.cs` — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestTestData.cs` (1a) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/SchemaRegistrationApplierTests.cs` (1b) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/QueuedSchemaRegistrationExecutorTests.cs` (1c) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs` (1d) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestWorkerTests.cs` (1e) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs` (1f) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (1g, two executor tests) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Helpers/InProcessSchemaRegistrationExecutor.cs` (1h) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` (1h, construction) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs` (1h) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` (1h) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs` (1h) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` (1i) — Tests
- T7 S1 `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs` (1i) — Tests
- T7 S3 `Iverson.Server/Iverson.Sql/PrivilegedRequestRow.cs` (3a) — Config / declarative
- T7 S3 `Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs` (3b, `GetAsync`, `CompleteAsync`) — I/O-bound
- T7 S3 `Iverson.Server/Iverson.Sql/ExistingTableInspector.cs` (3c, catalog lookup and the foreign-row `EXISTS`) — I/O-bound
- T7 S3 `Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs` (3d, `AcquireAsync` and `Lease`: connections and advisory locks) — I/O-bound
- T7 S3 `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (3e, interfaces) — Config / declarative
- T7 S3 `Iverson.Server/Iverson.Sql/ServiceCollectionExtensions.cs` (3g) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` (3h, `JsonOptions`) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/Grpc/ISchemaRegistrationExecutor.cs` (3i) — Config / declarative
- T7 S3 `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (3j, constructor) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (3j, owner pre-check exception) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (3j, phase-2 comment) — Config / declarative
- T7 S3 `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` (3j, phase 3 stamps and hands off) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestSchema.cs` (3k) — Config / declarative
- T7 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs` (3l, kinds, payload record, `PrivilegedRequestResult` JSON round trip) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs` (3m) — Config / declarative
- T7 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestClient.cs` (3n, submit and the clock-and-DB poll loop; borderline) — I/O-bound
- T7 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs` (3r, `ExecuteAsync`, `PollLoopAsync`, `WhenIdleAsync`) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/Program.cs` (3s, DI for both roles) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/Program.cs` (3s, worker-only DI) — Glue
- T7 S3 `Iverson.Server/Iverson.Api/Program.cs` (3s, the table's startup DDL line) — Glue
- T8 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestExecutorTests.cs` (1a) — Tests
- T8 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedRequests/PrivilegedRequestPostgresIntegrationTests.cs` (1b) — Tests
- T8 S1 `Iverson.Server/Iverson.Api.Tests/PrivilegedAdminEndpointTests.cs` (1c) — Tests
- T8 S1 `Iverson.Server/Iverson.Sql.Tests/DlqRepositoryTests.cs` (1d) — Tests
- T8 S1 `Iverson.Server/Iverson.Api.Tests/InactiveTenantHttpEndpointTests.cs` (1e, three replacements) — Tests
- T8 S1 `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs` (1e) — Tests
- T8 S1 `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` (1f) — Tests
- T8 S3 `Iverson.Server/Iverson.Sql/DlqRow.cs` (3a) — Config / declarative
- T8 S3 `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (3a) — Config / declarative
- T8 S3 `Iverson.Server/Iverson.Sql/DlqRepository.cs` (3a, `GetUnreplayedSummaryAsync`) — I/O-bound
- T8 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestContracts.cs` (3b, kinds and payload records) — Config / declarative
- T8 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWaits.cs` (3c) — Config / declarative
- T8 S3 `Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs` (3d, usings and constructor parameters) — Glue
- T8 S3 `Iverson.Server/Iverson.Api/Program.cs` (3e, the reconcile route's parameter) — Glue
- T8 S3 `Iverson.Server/Iverson.Api/Program.cs` (3f, the replay route's signature) — Glue
- T9 S1 `Iverson.Server/Iverson.Sql.Tests/OutboxWriterTests.cs` (three replacements) — Tests
- T9 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/TenantlessDecisionRefusalTests.cs` — Tests
- T9 S3 `Iverson.Server/Iverson.Api.Tests/AuthenticationPipelineTests.cs` (two replacements) — Tests
- T9 S3 `Iverson.Server/Iverson.Api.Tests/Schema/SchemaRegistryStartupTests.cs` — Tests
- T9 S3 `Iverson.Server/Iverson.Api.Tests/ApiStartupReadinessPipelineTests.cs` — Tests
- T9 S3 `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs` — Tests
- T9 S5 `Iverson.Server/Iverson.Api/ApiLoginGrants.cs` (5a, SQL grant list and probe SQL) — Config / declarative
- T9 S5 `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs` (5b, ordered startup DDL) — Glue
- T9 S5 `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` (5c, doc and two interface members) — Config / declarative
- T9 S5 `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` (5d, `ApiLoginName`) — Config / declarative
- T9 S5 `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` (5d, `EnsureCanEnterRuntimeRoleAsync`, `EnsureApiLoginAsync`, `WaitForRoleAsync`, `RoleExistsAsync`) — I/O-bound
- T9 S5 `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` (5d, `EnsureRoleAsync` uses `RoleExistsAsync`) — I/O-bound
- T9 S5 `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs` (5d) — Tests
- T9 S5 `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` (5f, doc paragraph) — Config / declarative
- T9 S5 `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` (5g, `HasLoaded` flag) — Glue
- T9 S5 `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` (5g, `LoadAsync` drops `EnsureTableAsync`) — Glue
- T9 S5 `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` (5g, `_hasLoaded = true`) — Glue
- T9 S5 `Iverson.Server/Iverson.Api/Program.cs` (5h, the startup role split) — Glue
- T9 S5 `Iverson.Server/Iverson.Api/Program.cs` (5h, the old startup DDL block removed) — Glue
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/InMemoryProvisionedTenantDirectory.cs` (a) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksSplitLogins.cs` (b) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryTenantLoginTests.cs` (c) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/TenantIdentifierTests.cs` (d) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/EngagementRepositoryLivenessRewrapTests.cs` (e, two replacements) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/ServiceCollectionExtensionsTests.cs` (f) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksRepositoryTests.cs` (g) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/TenantIsolationIntegrationTests.cs` (h) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksCredentialSplitIntegrationTests.cs` (i) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs` (j) — Tests
- T10 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs` (k) — Tests
- T10 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (l) — Tests
- T10 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/PostgresProvisionedTenantDirectoryTests.cs` (m) — Tests
- T10 S1 `Iverson.Server/Iverson.Api.Tests/Tenancy/ProvisionedTenantDirectoryPostgresIntegrationTests.cs` (n) — Tests
- T10 S3 `Iverson.Server/Iverson.StarRocks/IProvisionedTenantDirectory.cs` (a) — Config / declarative
- T10 S3 `Iverson.Server/Iverson.StarRocks/TenantIdentifier.cs` (b, `LoginName`, `LoginPassword`: branch-free composition over `Fingerprint` and HMAC; borderline) — Glue
- T10 S3 `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (c, `using System.Data;` removed) — Config / declarative
- T10 S3 `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (c, constructor parameters) — Glue
- T10 S3 `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (c, `TenantPoolSize`, `TenantPoolIdleSeconds`, `TenantSessionProperties`, `NoTenantLoginsMessage`) — Config / declarative
- T10 S3 `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (c, `TenantConnectionString`: `MySqlConnectionStringBuilder` plumbing; borderline) — Glue
- T10 S3 `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (c, `ReleaseAsync`: reader and connection disposal) — I/O-bound
- T10 S3 `Iverson.Server/Iverson.StarRocks/ServiceCollectionExtensions.cs` (d, two replacements) — Glue
- T10 S3 `Iverson.Server/Iverson.Api/Tenancy/StarRocksTenantSchema.cs` (e) — Config / declarative
- T10 S3 `Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs` (f, the two SQL statements) — I/O-bound
- T10 S3 `Iverson.Server/Iverson.Api/Program.cs` (g, `StarRocks:TenantKey` startup guard: configuration check with no spec or test citation) — Glue
- T10 S3 `Iverson.Server/Iverson.Api/Program.cs` (g, `AddStarRocks(…, tenantLoginKey)`) — Glue
- T10 S3 `Iverson.Server/Iverson.Api/Program.cs` (g, `IProvisionedTenantDirectory` registration) — Glue
- T10 S3 `Iverson.Server/Iverson.Api/WorkerStoreBootstrap.cs` (g, the table's `ApplySchemaAsync`) — Glue
- T10 S3 `Iverson.Server/Iverson.Api/ApiLoginGrants.cs` (g, the `GRANT SELECT` line) — Config / declarative
- T10 S3 `Iverson.Server/Iverson.Api.Tests/ApiLoginGrantsPostgresIntegrationTests.cs` (g) — Tests
- T10 S3 `Iverson.Server/Iverson.Api/appsettings.Development.json` (h) — Config / declarative
- T11 S1 `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (a) — Tests
- T11 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksResiliencePipelineFactoryTests.cs` (b) — Tests
- T11 S1 `Iverson.Server/Iverson.StarRocks.Tests/StarRocksTenantLoginIntegrationTests.cs` (c) — Tests
- T11 S3 `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (a, Aggregate's fan-out cap: the bound is `SemaphoreSlim`'s and the order `Task.WhenAll`'s, with no sequential logic to transliterate; borderline) — Glue
- T12 S1 `$SCR/check_t12.py` (scratch render checks, not committed) — Tests
- T12 S1 `$SCR/check_t12_compose.py` (scratch, not committed) — Tests
- T12 S1 `$SCR/t12_job.sh` (scratch, not committed) — Tests
- T12 S1 `$SCR/t12_render.sh` (scratch, not committed) — Tests
- T12 S1 `$SCR/t12_ci.sh` (scratch, not committed) — Tests
- T12 S1 `$SCR/t12_mutate.sh` (scratch, not committed) — Tests
- T12 S3 `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/secret-api.yaml` — Config / declarative
- T12 S3 `Iverson.Server/deploy/helm/iverson/charts/postgres/templates/cluster.yaml` (four replacements, the managed role among them) — Config / declarative
- T12 S3 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/secret-tenant-key.yaml` — Config / declarative
- T12 S3 `Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml` (comment, and the `SHOW GRANTS` revoke, shell in YAML; borderline) — Config / declarative
- T12 S3 `Iverson.Server/deploy/helm/iverson/charts/api/templates/deployment.yaml` (two replacements) — Config / declarative
- T12 S3 `Iverson.Server/deploy/helm/iverson/charts/worker/templates/deployment.yaml` — Config / declarative
- T12 S3 `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/job-revoke-cross-db.yaml` (three replacements) — Config / declarative
- T12 S3 `Iverson.Server/docker-compose.yml` (twelve replacements, `starrocks-init`'s revoke among them) — Config / declarative
- T12 S3 `scripts/generate-compose-secrets.sh` — Config / declarative
- T12 S3 `Iverson.Server/Iverson.Api/appsettings.Development.json` — Config / declarative
- T12 S3 `docs/runbooks/csr12-server.md` — Config / declarative
- T13 S1–S15 (no files; live verification commands, plus the scratch consoles of S7 and S13, not committed) — Tests

### 0.2 Approved trusted base

Per candidate, its approved axioms verbatim. `SchemaBuilder.NormalizeDeclaredNames`'s `OrdinalUpper` gained `ensures |OrdinalUpper(s)| == |s|`, approved by the user at the gate after P1 could not be proved without it (see that candidate's correspondence notes).

- `SchemaBuilder.NormalizeDeclaredNames`:

  ```dafny
  function OrdinalUpper(s: seq<char>): seq<char>
    ensures |OrdinalUpper(s)| == |s|
  ```

- `SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties`:

  ```dafny
  function OrdinalUpper(s: seq<char>): seq<char>
  ```

- `SchemaRegistrationOrchestrator.NavigationColumnCheck`:

  ```dafny
  function OrdinalUpper(s: seq<char>): seq<char>
  ```

- `PayloadKeyCanonicalizer.Canonicalize`:

  ```dafny
  function OrdinalUpper(s: seq<char>): seq<char>

  type Value(==)
  ```

- `StructSerializer.FoldKeys`:

  ```dafny
  function CharToUpperInvariant(c: char): char

  type Double
  ```

- `EngagementRepository.UpsertEntries`:

  ```dafny
  function DeserializeToDictionary(json: CsString): Parsed
  ```

- `EngagementStoreConsumer.WithOwnerValue`:

  ```dafny
  function ParseJsonObject(json: CsString): Option<JsonObj>

  function ToJsonString(o: JsonObj): CsString

  function OrdinalUpper(s: seq<char>): seq<char>
  ```

- `PopularitySignalEligibility.IsEligible`:

  ```dafny
  function OrdinalUpper(s: seq<char>): seq<char>
  ```

- `ObjectSearchGrpcService.EligiblePopularitySignal`:

  ```dafny
  function OrdinalUpper(s: seq<char>): seq<char>
  ```

- `ObjectSearchGrpcService.ReadableDecayField`:

  ```dafny
  function ResolveDecayField(schema: Desc): Option<CsString>

  function OrdinalUpper(s: seq<char>): seq<char>
  ```

- `ObjectSearchGrpcService.SimilarResponseAllowList`:

  ```dafny
  function CharToLowerInvariant(c: char): char

  type PValue

  function ConvertPayloadValue(raw: CsString, sqlType: CsString): PValue
  ```

- `PopularitySignalValidator.SignalKeyCollision`:

  ```dafny
  predicate HasVectorOrChunkFields(d: Desc)

  predicate IsEngagementEligible(d: Desc)

  function CharToLowerInvariant(c: char): char

  function OrdinalUpper(s: seq<char>): seq<char>
  ```

- `EnrichmentTargetRules.Violation`:

  ```dafny
  function OrdinalUpper(s: seq<char>): seq<char>

  predicate CharIsWhiteSpace(c: char)
  ```

- `SchemaRegistryRepository.OwnerQueries`:

  ```dafny
  function JsonOwner(schemaJson: CsString): Option<CsString>

  function {:axiom} PgLower(s: CsString): CsString
    ensures (forall i :: 0 <= i < |s| ==> s[i] < 128 as char) ==>
      |PgLower(s)| == |s| &&
      forall i :: 0 <= i < |s| ==> PgLower(s)[i] == (if 'A' <= s[i] <= 'Z' then (s[i] as int + 32) as char else s[i])

  function {:axiom} CharToLowerInvariant(c: char): char
    ensures c < 128 as char ==> CharToLowerInvariant(c) == (if 'A' <= c <= 'Z' then (c as int + 32) as char else c)

  predicate CharIsUpper(c: char)

  function {:axiom} OrdinalUpper(s: seq<char>): seq<char>
    ensures (forall i :: 0 <= i < |s| ==> s[i] < 128 as char) ==>
      |OrdinalUpper(s)| == |s| &&
      forall i :: 0 <= i < |s| ==> OrdinalUpper(s)[i] == (if 'a' <= s[i] <= 'z' then (s[i] as int - 32) as char else s[i])
  ```

- `PrivilegedRequestRepository.Transitions`:

  ```dafny
  function {:axiom} OrderByRequestedAtLimit(rows: seq<Req>, limit: int): seq<Req>
    ensures forall r :: r in OrderByRequestedAtLimit(rows, limit) ==> r in rows

  type Guid(==)
  ```

- `PostgresPrivilegedRequestLocks.Keys`:

  ```dafny
  function HashText(s: CsString): Int32

  function PgLower(s: CsString): CsString
  ```

- `SchemaRegistrationApplier.ExecuteAsync`:

  ```dafny
  function SerializeDescriptor(d: Desc): CsString
  ```

- `QueuedSchemaRegistrationExecutor.ExecuteAsync`:

  ```dafny
  function SerializeRegisterPayload(ds: seq<Desc>, changed: seq<CsString>): CsString
  ```

- `PrivilegedRequestExecutor.RunAsync`:

  ```dafny
  type Guid(==)
  ```

- `PrivilegedRequestWorker.PollOnceAsync`:

  ```dafny
  function ResultToJson(r: PResult): CsString

  type Guid(==)
  ```

- `Program.PrivilegedAdminRoutes`:

  ```dafny
  function SerializeReconcilePayload(typeName: CsString): CsString

  function SerializeReplayPayload(id: Guid): CsString

  type Guid(==)
  ```

- `Program.Health`: none

- `ObjectRetrievalGrpcService.GetManyTenantGuard`: none

- `ObjectMappingGrpcService.DeleteTenantGuard`: none

- `OutboxWriter.UpsertAndEnqueueOutboxAsync`:

  ```dafny
  function WithTenantColumnJson(payload: CsString, tenantColumn: Option<CsString>, tenant: CsString): CsString
  ```

- `EngagementRepository.TenantScopedAccess`:

  ```dafny
  function TenantConnectionString(baseCs: CsString, tenantId: CsString, key: CsString): CsString

  predicate IsValidTenant(tenantId: CsString)
  ```

- `EngagementRepository.OpenOrSurfaceRefusalAsync`: none

- `EngagementRepository.IsExpectedMissingResourceError`:

  ```dafny
  predicate ContainsIgnoreCase(s: CsString, sub: seq<char>)
  ```

- `EngagementRepository.EnsureTenantProvisionedAsync`:

  ```dafny
  function RoleName(tenantId: CsString): CsString

  function LoginName(tenantId: CsString): CsString

  function LoginPassword(tenantId: CsString, key: CsString): CsString

  function CreateTableDdl(table: EngTable, qualifiedTable: CsString): CsString

  function DatabaseName(tenantId: CsString): CsString

  predicate IsValidTenant(tenantId: CsString)
  ```

- `PostgresProvisionedTenantDirectory.IsProvisionedAsync`: none

- `StarRocksResiliencePipelineFactory.IsBackendTransient`:

  ```dafny
  predicate IsTransient(e: MySqlEx)
  ```

### 0.3 Run notes

- **Replay.** The plan replayed through Task 11 (the highest task touching a candidate's file) with exit 0; every candidate file was written by an applied step.
- **Verifier setting deviation.** Every model was gated (G0–G3) with `--manual-lemma-induction` added to the skill's verify command. During G5, Dafny 4.11's automatic lemma induction was found to prove false statements: a standalone reproducer verifies `ensures false` (a lemma with `{:induction dict}` over `ToDictionaryFrom`; without the attribute, and with `--manual-lemma-induction`, it correctly fails), and a G5 mutant of `NormalizeDeclaredNames` that made P3 false verified under default flags but failed under `--manual-lemma-induction`. G2's "contradictory assumptions" warning did not flag it. Two models needed explicit-induction repairs to verify without automatic induction (`SchemaRegistryRepository.OwnerQueries`, helper `GetOwnersHasMatch`; `StructSerializer.FoldKeys`, helper `FoldLoopFacts`); the other 29 verified unchanged. Every `.dfy`'s `Re-run:` line includes the flag. G5 kills found under default flags remain valid (a mutant that fails with more proof power fails with less); the kills through the two repaired helpers were re-checked under the flag.
- **G4 judgements.** `EngagementRepository.TenantScopedAccess`: P1–P3 are proved for its `TenantOp`, which fixes a single-key Aggregate and an Upsert naming at least one column (inputs absent from the approved header signature); for the two excluded inputs a proved helper (`ExcludedInputsDoNotConnect`) shows no data connection is opened, and P4 is proved for every input shape (see that candidate's controller note). `OutboxWriter.UpsertAndEnqueueOutboxAsync`: `PostgresIdentifier.Quote`'s throw and `WithTenantColumn`'s malformed-JSON throw are not modelled (approved axiom / model note); both occur before the transaction and fail closed. Other deviations are listed in each correspondence table.
- **Witnesses.** No obligation failed, so no witness tests were run (Steps 11–12 had nothing to confirm).

### 0.4 Correspondence tables and G5 kills

#### `SchemaBuilder.NormalizeDeclaredNames`

Worktree: SchemaBuilder.cs, T1 S3 unless marked pre-plan.

| Worktree file:line | Introduced by | Dafny lines | Note |
|---|---|---|---|
| SchemaBuilder.cs:279–280 signature | T1 S3 | 196 | exception → Threw. |
| :281–283 Select(Name).Prepend(KeyColumn.Name) | T1 S3 | 43–51, 202 | ColumnNames(d). |
| :284 Where(!IsTenantColumn) | T1 S3 | 62–69, 201 | |
| :285 Distinct(Ordinal) | T1 S3 | 72–80, 200 | first occurrence, in order. |
| :286 GroupBy(OrdinalIgnoreCase) | T1 S3 | 82–111, 199 | Lookup model: key = first element's, groupings in creation order. |
| :287 Where(Count()==1) | T1 S3 | 114–121, 198 | |
| :288 ToDictionary(Key, Single(), OrdinalIgnoreCase) | T1 S3 | 124–149, 197–205 | Single() and Add throws → Threw. |
| :290–291 ColumnSpelling / TryGetValue | T1 S3 | 152–174 | |
| :293–297 Relations rewrite (OneToMany kept) | T1 S3 | 177–181, 208–209 | |
| :298–304 Authorization rewrite | T1 S3 | 184–187, 210–221 | |
| SchemaDescriptor.cs:162–166 OwnerField init accessor | pre-plan | 14, 32, 190–194, 215 | Deviation: OwnerField typed Option<NonEmptyCsString> (constructor and init accessor rule out ""; a type invariant, not an added precondition). |
| SchemaDescriptor.cs:20–21 IsTenantColumn | pre-plan | 25 | |
| SchemaDescriptor.cs records | pre-plan | 27–40 | shared-vocabulary datatypes. |

Re-proof after the approved `OrdinalUpper` contract (summary of the return's appended section). The first proof left P1 unproved: with `OrdinalUpper` uncontracted, a countermodel exists (`OrdinalUpper("") == OrdinalUpper("a")`, a key column named "", OwnerField "a", so the owner is rewritten to "" and normalised to null), which real .NET `OrdinalIgnoreCase` never produces. After the user approved `ensures |OrdinalUpper(s)| == |s|` at the gate, the conditional helper `OwnerSpellingNonEmptyIfOrdinalUpperPreservesLength` became the unconditional `OwnerSpellingNonEmpty(d, x)`, which P1's body calls; the verifier reports 52 verified, 0 errors. Frozen property text, P2, P3 and the transliteration are unchanged; Dafny line numbers in the table from line 21 onward shift by +1. The one remaining warning ("ensures clause is part of a bodyless function") comes from the approved axiom's own text.

G5:

- P1_OwnerAndFieldPermissionNamesTakeTheColumnsSpelling: killed by P1_OwnerAndFieldPermissionNamesTakeTheColumnsSpelling.3 (line 173, ColumnSpelling: `case Some(k) => columnSpelling[k].Value` → `case Some(k) => name`, so a matched declared name keeps its declared spelling; failed row: ColumnSpellingRenames (correctness), the helper P1's proof calls). Mutants .1 and .2 did not resolve (type errors: Option<NonEmptyCsString> vs Option<CsString>; `as` unsupported on subset types) and killed nothing. (Controller-run, after the re-proof under the approved OrdinalUpper length contract.)
- P2_OnlyNonOneToManyForeignKeysAreRewritten: killed by P2_OnlyNonOneToManyForeignKeysAreRewritten.1 (line 181, SelectRelations: flipped `rs[i].Kind == OneToMany` to `rs[i].Kind != OneToMany`, so OneToMany foreign keys are rewritten and the others kept; failed row: P2_OnlyNonOneToManyForeignKeysAreRewritten (correctness))
- P3_NormalizationNeverFails: killed by P3_NormalizationNeverFails.2 (line 101, AddToGrouping: dropped the matched-group arm, `case Some(i) => gs[i := gs[i].(Elements := gs[i].Elements + [x])]` -> `case Some(i) => gs + [Grouping(x, [x])]`, so case-variant spellings get separate groups with case-equal keys and ToDictionary's Add throws; failed row: AddInv (correctness), the helper P3's proof calls through DictFacts -> GroupsInv -> GroupFromInv)

#### `SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties`

Worktree file: Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs (all rows T1 S3)

| Worktree lines | Introduced by | Dafny lines | Note |
|---|---|---|---|
| :452 `private static void RejectCaseDuplicateProperties(TypeDescriptor typeDesc)` | T1 S3 | 95 `function RejectCaseDuplicateProperties(td: TypeDesc): Check`; types 29, 32, 35 | void-or-throw becomes `Check`: a normal return is `Pass`, the throw is `Refused(code, detail)` (exceptions are outcomes). `TypeDescriptor` is modelled as `TypeDesc(TypeName, Properties)` and `PropertyDescriptor` as `Prop(Name)`, the only fields read. Proto3 strings and repeated fields are never null, so there are no `Option`s and no added `requires`. |
| :454-455 `typeDesc.Properties.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)` | T1 S3 | 97 (call); 41-71 (`Group`, `FindGroup`, `AddTo`, `GroupByName`) | This models how LINQ's `Lookup` is built. Groups come out in the order their keys first appear. A group's `Key` is its first element's key. Each element joins the first group whose stored key is `EqIC` to its own key, or else opens a new group at the end. Elements keep their source order inside a group. `StringComparer.OrdinalIgnoreCase.Equals` is `EqIC` (22), over the approved `OrdinalUpper` (19). The model assumes `GetHashCode` agrees with `Equals`, which the BCL comparer guarantees. Hash buckets are not modelled because they do not change the result. |
| :456 `.FirstOrDefault(g => g.Count() > 1)` | T1 S3 | 97 (call); 73-80 `FirstOrDefaultDup` | `null` (the default) is `None`. |
| :457 `if (duplicate is null) return;` | T1 S3 | 98 `if duplicate.None? then Pass` | Same branch order. |
| :459-462 `throw new RpcException(new Status(StatusCode.InvalidArgument, $"...{string.Join(", ", duplicate.Select(p => $"'{p.Name}'"))}..."))` | T1 S3 | 100-103 `Refused(InvalidArgument, ...)`; 82-87 `Join`; 89-93 `QuotedNames` | The message is typed `seq<char>`, following the shared vocabulary for built strings. `string.Join` and `Select` are modelled structurally. |
| (proof only) | — | 111-280 | Helpers: `Filter` (the matching elements in order), `GroupInv`, and lemmas `FilterSnoc`, `FilterEmptyNoMatch`, `FilterNonEmptyHasMatch`, `FilterNoMatch`, `FilterTwoIff`, `AddToInv`, `GroupByInv`, `FirstOrDefaultDupSome`. Every helper contract is proved, with no `assume`, `expect` or extra axioms. |
| (property) | spec §2.4 | 282-304 `P1_PropertiesEqualIgnoringCaseAreRefused` | The frozen header is copied verbatim, and the body opens on the line after the last clause. |

Deviations: none in the logic. The only liberty is modelling the hashed lookup as a linear search over groups, which gives the same result because hashing agrees with `Equals` (see the GroupBy row).

Context, outside this candidate: the call site at :85 runs after `RejectDeclaredTenantField`, `RejectReservedTenantName` and the `ValidateIdentifier` loops (:66-81). An input those earlier checks refuse is refused by them first. P1 is about this member alone.

G5:

- P1_PropertiesEqualIgnoringCaseAreRefused: killed by P1_PropertiesEqualIgnoringCaseAreRefused.1 (line 77: `FirstOrDefaultDup` tests `|gs[0].Elems| > 2` instead of `> 1`, so a group of exactly two case-equal names is not a duplicate; failed row: FirstOrDefaultDupSome (correctness))

#### `SchemaRegistrationOrchestrator.NavigationColumnCheck`

Worktree file: Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs; all of 273-291 introduced by T1 S3, sub-step 3e, per `git diff c79d7de0`.

| Worktree lines | Task/step | Dafny lines | Note |
|---|---|---|---|
| 273-277 (comment) | T1 S3 | — | Comment only. |
| 278 `foreach (var relation in descriptor.Relations)` | T1 S3 | 69-72, 74, 82, 85-88 | foreach as index recursion `NavigationColumnLoop(d, i)` from 0; loop exit = `Pass`. `NavigationColumnCheck(d) = NavigationColumnLoop(d, 0)`. |
| 280-282 `ScalarColumns.Select(c => c.Name).Prepend(KeyColumn.Name)` | T1 S3 | 31-43, 75 | `ColumnNames(d) = [KeyColumn.Name] + ScalarNames(ScalarColumns)`, key first then scalars in order, per shared vocabulary. The tenant column, when the built descriptor carries it, is an entry of ScalarColumns and so is included (the C# does not filter it). |
| 283 `.FirstOrDefault(name => string.Equals(name, relation.PropertyName, OrdinalIgnoreCase))` | T1 S3 | 59-66, 75 | `FirstMatchIC`: first element with `EqIC(name, propertyName)` (same argument order), `None` for the null default. |
| 284 `if (column is not null)` | T1 S3 | 76 | Column.Name is a non-nullable `string`, so FirstOrDefault returns null exactly when no element matched; `Some?` is `is not null`. |
| 286-289 `throw new RpcException(new Status(StatusCode.InvalidArgument, $"..."))` | T1 S3 | 47, 49-57, 77-80 | Exception as outcome `Refused(InvalidArgument, detail)`. Message transliterated as concatenation; `{relation.Kind}` interpolation (enum ToString) modelled by `KindName`. |
| 290-291 (fall-through to next relation) | T1 S3 | 81-82 | Early return preserved: first matching relation refuses, else continue. |

Deviations / scope notes:
- Only the 3e loop is modelled, applied to the built descriptor; the preceding RegisterAsync steps (BuildDescriptor, the FK membership/type/naming loop, the nav/FK collision loop at 262-271) are outside the candidate. A descriptor refused earlier never reaches this loop; the property is about this loop's verdict on the descriptor it receives.
- `Desc` omits fields no header names (VectorFields, ChunkFields, Authorization, TenantColumn, PopularitySignalColumn, OwnerTenantId, EnrichmentTargets, HasDocumentTemplate), as the guidance permits. `Column` and `Relation` carry all their vocabulary fields.
- `StatusCode` is the full gRPC enum from the shared vocabulary.

G5:

- P1_ANavigationNamedLikeAnyColumnIsRefused: killed by P1_ANavigationNamedLikeAnyColumnIsRefused.1 (line 75: the loop searches `ScalarNames(d.ScalarColumns)` instead of `ColumnNames(d)`, dropping the key column from the names checked; failed row: LoopSpec (correctness))

#### `PayloadKeyCanonicalizer.Canonicalize`

Worktree: Iverson.Server/Iverson.Api/Grpc/PayloadKeyCanonicalizer.cs, T2 S3.

| C# lines | Dafny lines | Note |
|---|---|---|
| 30 signature | 182 Canonicalize(d, p): CanonResult; 31 Desc, 42 Payload | in-place mutation returned as Canonical(p); Desc keeps TypeName, KeyColumn, ScalarColumns. |
| 32 Dictionary(OrdinalIgnoreCase) | 89 ColumnDict, 91-102 IcTryGet, 183 | association list looked up by EqIC, keyed by the first spelling. |
| 33 Select(Name).Prepend(KeyColumn.Name) | 34-36 ColumnNames | same order. |
| 33-40 foreach | 109-127 BuildColumns | recursion with accumulator. |
| 35 IsTenantColumn continue | 115 | |
| 36-37 TryGetValue / new list | 117-124 | |
| 38-39 !Contains(Ordinal) then Add | 121-122, 125-126 | shared List reference as in-place entry update. |
| 42 keyFor (Ordinal) | map param of Scan (138) | |
| 43 renames | seq param of Scan | |
| 44-67 loop over payload keys | 138-165 Scan | index recursion in insertion order. |
| 46 !TryGetValue continue | 145-146 | |
| 48-54 Count > 1 throws InvalidArgument | 149-153 ScanRefused | message via QuoteJoin (129-133). |
| 56 spellings[0] | 154, 156 | Deviation: explicit ScanFaulted for the impossible empty-list ArgumentOutOfRange; ScanNoFault proves it unreachable. |
| 57-62 keyFor.TryGetValue throws InvalidArgument | 157-160 | |
| 64-66 keyFor[column] = key; rename if not ordinal-equal | 162-164 | |
| 69-74 apply renames | 168-180 ApplyRenames | |
| 71 payload.Fields[from] | 175-178 PIndex (57-68) | None models KeyNotFound → Faulted (189); never reached (P5). |
| 72 Fields.Remove(from) | 71-76 PRemove | order kept. |
| 73 Fields[to] = value | 78-82 PSet | replace in place or append. |
| RpcException throws | 50 CanonResult = Canonical/Refused/Faulted; 185-190 | Deviation: added Faulted variant for the two impossible non-Rpc throws. |
| Shared vocabulary | 18-26, 44-55 | as given; Present an added helper. |
| P1-P5 | 198, 225, 235, 245, 266 | helpers 275-988. |

G5:

- P1_AKeyNamingOneColumnTakesItsSpelling: killed by P1_AKeyNamingOneColumnTakesItsSpelling.1 (line 163, Scan: flipped the rename guard `if key != column then` to `if key == column then`, so a key spelled differently from its one column is never renamed; failed row: ScanOkIfGood (correctness), the helper P1's proof calls through ApplyPrep; also ScanRefusesPair (correctness))
- P2_TwoKeysNamingOneColumnAreRefused: killed by P2_TwoKeysNamingOneColumnAreRefused.1 (line 162, Scan: dropped the claim on the column, `keyFor[column := key]` to `keyFor`, so a second key naming the same column is not detected; failed row: ScanRefusesPair (correctness), the helper P2 calls)
- P3_AKeyMatchingSeveralColumnsIsRefused: killed by P3_AKeyMatchingSeveralColumnsIsRefused.1 (line 149, Scan: flipped the multi-match comparison `|spellings| > 1` to `|spellings| > 2`, so a key matching exactly two case-duplicate columns is not refused; failed row: ScanRefusesMulti (correctness), the helper P3 calls)
- P4_KeysNamingNoColumnAndTheTenantKeyAreLeftAlone: killed by P4_KeysNamingNoColumnAndTheTenantKeyAreLeftAlone.1 (line 115, BuildColumns: removed the tenant-column exclusion, `if IsTenantColumn(name) then` to `if false then`, so `__TenantId` enters the column map and a tenant key can be renamed; failed row: BuildColumnsInv (correctness), the helper P4's proof calls through ApplyPrep -> ScanOkIfGood -> ColsFacts)
- P5_NothingElseIsRefused: killed by P5_NothingElseIsRefused.1 (line 149, Scan: flipped the multi-match comparison `|spellings| > 1` to `|spellings| >= 1`, so every key naming a column is refused; failed row: ScanOkIfGood (correctness), the helper P5's proof calls through ApplyPrep)

#### `StructSerializer.FoldKeys`

All worktree lines are in `Iverson.Server/Iverson.Api/Grpc/ProtoPayloadHelper.cs`, the file as it stands after T2 S3. "T2 S3" marks lines that step introduced or changed; "pre-existing" marks lines the plan leaves as they were.

| Worktree file:line | Introduced by | Dafny lines | Notes / deviations |
|---|---|---|---|
| 1-7 (usings, namespace) | pre-existing; `using Iverson.Api.Schema` T2 S3 | (none) | No code. |
| 9 `class StructSerializer` | pre-existing | (none) | Static class; members become top-level functions (no modules, per brief). |
| 12 `s_noColumns = []` | T2 S3 | 70 (`{}` in ColumnSet), 121 (`FoldKeys(fs, {})`) | An empty ordinal `HashSet<string>` is the empty `set<seq<char>>`. |
| 22-25 `SerializePayload(payload, schema)` | T2 S3 | 68-72 `ColumnSet`, 75-78 `SerializeFold` | `Struct payload` is non-nullable, so the model takes `payload.Fields` directly as `fields`. `schema is null` becomes `Option<Desc>.None`. `ScalarColumns.Select(c=>c.Name).Append(KeyColumn.Name).ToHashSet(StringComparer.Ordinal)` is the set comprehension over the `ScalarColumns` names plus `{KeyColumn.Name}`. Ordinal string equality is `seq<char>` equality, so `HashSet.Contains` is `in`. `JsonSerializer.Serialize` is outside the member, per the model notes, so `SerializeFold` returns the folded dictionary rather than the JSON string. |
| 27-28 `UpperFirst` | pre-existing | 53-56 | `char.ToUpperInvariant` is the approved axiom `CharToUpperInvariant` (line 10). `s[1..]` and string `+` are modelled as seq slice and concatenation, which preserves length, so the result is a `CsString` with no added precondition. |
| 30 `FoldKeys(MapField<string,Value>, IReadOnlySet<string>)` | T2 S3 (signature) | 81-85 | `MapField` iteration order becomes `seq<(CsString, Val)>` order. A real MapField has distinct keys. The model does not assume this. A duplicate key folds to the same `upper` and is Refused at line 98, so `Folded?` already excludes duplicates. |
| 32 `originalNameOf = new Dictionary<string,string>()` | pre-existing | 84 (`map[]`) | Modelled as `map<seq<char>, seq<char>>`. |
| 33 `folded = new Dictionary<string,object?>()` | pre-existing | 84 (`[]`), 59-65 `DictSet` | Modelled as an insertion-ordered seq of pairs. The indexer set is modelled faithfully as "overwrite in place if present, else append". Lemma `DictSetAppendsFreshKey` (166-175) proves it always appends here. |
| 34 `foreach (var (key, value) in fields)` | pre-existing | 88-108 `FoldLoop` (one iteration per call, `idx`) | Tail recursion stands in for the loop. |
| 36 `upper = columnNames.Contains(key) ? key : UpperFirst(key)` | T2 S3 | 97 | Same order of the conditional. |
| 37-42 `TryGetValue` collision, then `throw RpcException(InvalidArgument, …)` | pre-existing | 98-101 | The throw becomes the `Refused(InvalidArgument, detail)` outcome. The interpolated message is transliterated verbatim as `seq<char>` concatenation. |
| 43 `originalNameOf[upper] = key` | pre-existing | 103 | A plain map update. `upper` is known absent, so it is an insert. |
| 44 `folded[upper] = ProtoValueToObject(value)` | pre-existing | 105-107 | The right-hand side is evaluated first. A propagated exception (`Raised`) becomes `Refused` with the same code and detail. Otherwise `DictSet` runs and the loop continues. |
| 46 `return folded` | pre-existing | 93 | |
| 49-58 `ProtoValueToObject` switch | pre-existing; line 56 T2 S3 | 111-124 | Arms are in the same order. **Deviation:** the `_ => null` arm (KindCase `None`, a `Value` with no kind set) has no counterpart, because the brief's `Val` datatype has no "unset" variant. That arm returns `null` and cannot throw, and it is never a `StructValue`, so neither property depends on it. |
| 55 `ListValue.Values.Select(ProtoValueToObject).ToList()` | pre-existing | 127-137 `ConvertList` | Elements are converted in order. The first exception propagates (`Raised`). Neither property depends on lists. |
| 56 `FoldKeys(v.StructValue.Fields, s_noColumns)` | T2 S3 | 120-123 | A nested `Refused` becomes `Raised` with the same code and detail (the exception propagates). |
| (termination) | n/a | 82, 91, 112, 129 | The `decreases VStruct(fields)` / `VList(items)` wrappers put every mutually recursive call under one datatype rank. They are proof-only. |
| (vocabulary) | n/a | 18-32, 39-50 | `Desc` omits every field no header of this candidate names. It keeps `TypeName`, `TableName`, `KeyColumn` and `ScalarColumns`. `Result<T>` is unused and so not declared. |

Helper lemmas (proof only, each proved):
- `ColumnSetIsColumnNames` (143-164) bridges the code's ordinal set to the vocabulary's `ColumnNames` seq.
- `DictSetAppendsFreshKey` (166-175) shows the indexer set appends when the key is new.
- `KeyInAppend` (177-192).
- `FoldLoopFacts` (196-230) is the loop invariant. The result extends `folded`, and each remaining field i sits at some index under its folded key, holding `ProtoValueToObject(fields[i].1)`.
- `FoldKeysFacts` (232-241).

Frozen lemmas: P1 at 248-256, P2 at 259-278.

Re-proof without automatic induction (summary of the return's appended section). Under `--manual-lemma-induction` the helper `FoldLoopFacts` failed one chained-equality assertion (old line 227); only its body changed, replacing the two closing asserts with explicit prefix-of-prefix and element steps (one repair round, no `{:induction}` attributes). Result: 21 verified, 0 errors, with and without the flag. No frozen text, transliteration code or other helper changed. Lines from 227 onward moved down by 4 (`FoldKeysFacts` 236-245, P1 252-260, P2 263-282). The remaining "unusual indentation" warning sits inside the frozen P2 `ensures` clause.

G5:

- P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted: killed by P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted.1 (line 97: dropped the exact-column branch, `if key in columnNames then key else UpperFirst(key)` -> `UpperFirst(key)`; failed row: FoldLoopFacts (correctness), the helper P1's proof calls through FoldKeysFacts)
- P2_NestedKeysAreAlwaysUpperFirsted: killed by P2_NestedKeysAreAlwaysUpperFirsted.1 (line 122: a nested struct converts to an empty dictionary, `Converted(ODict(es))` -> `Converted(ODict([]))`; failed row: P2_NestedKeysAreAlwaysUpperFirsted (correctness))
  - (controller re-check under --manual-lemma-induction after the FoldLoopFacts re-proof: P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted.2, line 97 same mutation; failed rows: FoldLoopFacts (correctness),Failed;)

#### `EngagementRepository.UpsertEntries`

All worktree lines are in `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` and were introduced by T3 S3 (3b). The call site at :488 comes from T3 S3 (3a) and is outside the candidate.

| Worktree file:line | Task/step | Dafny lines | Note |
|---|---|---|---|
| EngagementTableSchema.cs:3-9 (`EngagementTableSchema`) | pre-existing | 29 (`EngTable`) | `SortKey` is omitted because no header names it. |
| EngagementTableSchema.cs:11-14 (`EngagementColumnSchema`) | pre-existing | 27 (`EngCol`) | `SrType` is modelled as `SqlType`, the name from the model guidance. The field is unused. |
| (System.Text.Json `JsonValueKind`, `JsonElement`) | library | 16, 18 | `JsonEl(kind, raw)` from the model notes. Only `kind` is read. |
| :521 signature | T3 S3 | 63 | Both parameters are non-nullable, so they become `EngTable` and `CsString` with no `requires`. `List<KeyValuePair<string, JsonElement>>` becomes `Result<seq<(CsString, JsonEl)>>`. |
| :523 `JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)` | T3 S3 | 32 (approved axiom), 67-68 | `ParseFailed` (a `JsonException`) becomes `Threw`, following *Exceptions are outcomes*. The dictionary's enumeration order and its duplicate-key behaviour are left to the uninterpreted axiom, through `entries: seq<...>`. |
| :524 `?? new Dictionary<string, JsonElement>()` | T3 S3 | 70 | `ParsedNull` becomes an empty row. |
| :526-529 `schema.Columns.Select(c => c.Name).Append(schema.KeyColumn.Name).ToHashSet(StringComparer.Ordinal)` | T3 S3 | 35-45 (`ColumnNameSet`, `KnownCols`), 72 | Ordinal equality is exact equality of UTF-16 code-unit sequences. A `set<CsString>` therefore models the `HashSet` with the ordinal comparer exactly. Element order and duplicates do not matter to `Contains`. |
| :531-535 `row.Where(kv => knownCols.Contains(kv.Key) && kv.Value.ValueKind != Object && != Undefined).ToList()` | T3 S3 | 48-53 (`Keep`), 56-61 (`FilterEntries`), 74 | The filter preserves order. The conjunct order matches the C# `&&` chain. |
| (proof) | n/a | 79-87 `FilterEntriesMembership`, 89-101 `KnownColsMembership`, 106-124 `P1_...` | Helper lemmas, each proved. No `assume`, `expect`, or `{:axiom}` is used, and no axiom besides `DeserializeToDictionary`. |

Deviations:
1. The column field is called `SqlType`, where the C# field is `SrType`, following the model guidance. It is unused.
2. `SortKey` is omitted.

Neither deviation affects the obligation.

G5:

- P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected: killed by P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected.1 (line 51, Keep: removed the conjunct `&& kv.1.kind != JObject` (replaced by `&& true`), so object-valued entries are selected; failed row: P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected (correctness))

#### `EngagementStoreConsumer.WithOwnerValue`

Worktree file: Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs (all lines below introduced or rewritten by T3 S3 unless noted).

| Worktree lines | Task/step | Dafny lines | Note |
|---|---|---|---|
| 130 (signature `WithOwnerValue(string, string, string?)`) | T3 S3 (pre-existing signature, unchanged) | 88 | `string?` → `Option<CsString>`; return `Result<CsString>` so the throw is an outcome. |
| 132 `JsonNode.Parse(payloadJson)!.AsObject()` | T3 S3 (pre-existing line) | 90–91 | Approved axiom `ParseJsonObject`; `None` = any throw (JsonException from Parse, NullReferenceException on a `null` literal, InvalidOperationException from `AsObject` on a non-object) → `Threw`. |
| 133–136 `node.Select(kv => kv.Key).Where(k => string.Equals(k, ownerField, OrdinalIgnoreCase)).ToList()` | T3 S3 | 39–50 (`Keys`, `FilterEqIC`), used at 82 | `Keys` = enumeration order of the JsonObject; `FilterEqIC` = `Where` with `EqIC`; `.ToList()` snapshots, so the removal loop iterates a fixed list (no enumerate-while-mutate); modelled by computing the list from the original `o`. |
| 137 `node.Remove(key)` inside `foreach` | T3 S3 | 54–66 (`RemoveKey`, `RemoveAll`), used at 82 | `JsonObject.Remove` is ordinal exact-key removal (default `JsonNodeOptions`, `PropertyNameCaseInsensitive = false`; the file's `PropertyNameCaseInsensitive = true` at 163 is the serializer options for `Deserialize`, not node options). `RemoveKey` removes every entry with that exact key, which equals removing the single one under distinct keys. |
| 139–140 `if (authoritativeOwnerValue is not null) node[ownerField] = JsonValue.Create(authoritativeOwnerValue);` | T3 S3 | 70–75 (`SetKey`), 83–84 | Indexer set modelled as replace-in-place if the exact key is present, else append; `JsonValue.Create(string)` → `JStr`. Branch order kept (Some → set, None → unchanged). |
| 142 `return node.ToJsonString();` | T3 S3 (pre-existing line) | 92 | Approved axiom `ToJsonString`. |

Deviations: none beyond the library modelling above. Observation: neither property needed `DistinctKeysJ(o)` — both proofs go through without using the precondition (it is still present, as frozen).

G5:

- P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey: killed by P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey.1 (line 82: dropped the owner-key removal loop, `var node := o;`; failed row: P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey (correctness))
- P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains: killed by P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains.1 (line 84: swapped the no-owner return `node` for the unfiltered `o`; failed row: P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains (correctness))

#### `PopularitySignalEligibility.IsEligible`

Worktree: Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs, T4 S5.

| Worktree lines | Dafny lines | Note |
|---|---|---|
| :21-22 IsEligible(parent, relation, SchemaRegistry) → registry.Get | 53-56 IsEligibleIn | calls the Option<Desc> overload with RegistryGet. |
| SchemaRegistry.cs:12,16-17 Get (OrdinalIgnoreCase ConcurrentDictionary) | 41-46 RegistryGet | first descriptor whose TypeName is EqIC (unique keys). |
| :24 overload signature | 94 | child SchemaDescriptor? → Option<Desc>. |
| :28-29 not OneToMany or child null → false | 96 | same order. |
| :32-33 parent.Authorization null → true | 97 | |
| :37-38 OwnerField not null → false | 99-100 | init "" → null; both test only null/None?, no effective deviation. |
| :41-42 child.Authorization null → false | 101 | |
| :46-48 Readers(parent).IsSubsetOf(Readers(child)) | 103-105 | ordinal set <=. |
| :53-57 FieldPermissions.Where(...).All(...) | 72-77 WhereKeep, 80-83, 86-91 WhereAll, 106 | in-order fold; EqualsICNullable 66-69 for nullable PopularitySignalColumn. |
| :60-61 Readers | 60-63 | set comprehension. |
| SchemaDescriptor.cs records | 22-35 | Desc keeps TypeName, Authorization, PopularitySignalColumn. |

G5:

- P1_NotOneToManyOrMissingChild_IsNeverEligible: killed by P1_NotOneToManyOrMissingChild_IsNeverEligible.1 (line 96: swapped the structural-check return, `relation.Kind != OneToMany || child.None? then true`; failed row: P1_NotOneToManyOrMissingChild_IsNeverEligible (correctness))
- P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow: killed by P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow.1 (line 105: negated the reader-subset guard, `if (parentReaders <= Readers(childRules)) then false`; failed row: P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow (correctness))
- P3_AnUnregisteredChildIsNeverEligible: killed by P3_AnUnregisteredChildIsNeverEligible.1 (line 55: replaced the registry lookup `RegistryGet(registry, relation.RelatedTypeName)` with `Some(parent)`; failed row: P3_AnUnregisteredChildIsNeverEligible (correctness))

#### `ObjectSearchGrpcService.EligiblePopularitySignal`

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:1092 (T4 S6) signature | 129 | Instance method over `_popularitySignal.Signals` and the injected `registry`; both made explicit parameters `signals`, `registry`. `PopularitySignalEntry?` result -> `Option<Signal>`. |
| ObjectSearchGrpcService.cs:1094-1095 (T4 S6) `Signals.FirstOrDefault(... ParentType ... OrdinalIgnoreCase)` | 112-118 (`FirstSignalFor`), 131 | First match in list order under `EqIC`. Signal entries are non-nullable record elements (`List<PopularitySignalEntry>`), so a null element (NRE on `s.ParentType`) is not modelled. |
| ObjectSearchGrpcService.cs:1096 (T4 S6) `if (signal is null) return null;` | 132 | Same early return. |
| ObjectSearchGrpcService.cs:1098-1099 (T4 S6) `schema.Relations.FirstOrDefault(... PropertyName ... signal.Relation ...)` | 121-127 (`FirstRelationNamed`), 134 | First match in list order under `EqIC`. `signal.Relation` is a non-nullable `string` (record parameter), modelled as `CsString`. |
| ObjectSearchGrpcService.cs:1100-1102 (T4 S6) `relation is not null && IsEligible(schema, relation, registry) ? signal : null` | 135-137 | `&&` short-circuit preserved (`relation.Some? && ...`). |
| Iverson.Server/Iverson.Api/Grpc/PopularitySignalEligibility.cs:21-22 (T4 S5) registry overload | 55-58 (`IsEligibleIn`) | Copied from the PopularitySignalEligibility.IsEligible candidate's model, as the brief directs. |
| PopularitySignalEligibility.cs:24-61 (T4 S5) descriptor overload + `Readers` | 61-104 | Copied verbatim from that candidate's model (proved there); used here only as an opaque-to-the-property definition. |
| Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs:16-17 (pre-existing at replay base) `Get` | 43-48 (`RegistryGet`) | OrdinalIgnoreCase dictionary modelled as first EqIC match over a sequence, as in that candidate. |
| Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs:10 (pre-existing) `record PopularitySignalEntry(string ParentType, string Relation)` | 36 | `Signal` datatype. |
| SchemaDescriptor (TypeName, Relations, Authorization, PopularitySignalColumn) | 33 | `Desc` holds only the fields this candidate and IsEligible read (allowed by the vocabulary: omitted fields are named by no header). |

Deviations: none beyond the explicit-parameter lifting of the instance field and injected registry, and omitted unused `Desc` fields.

G5:

- P1_OnlyAnEligibleSignalIsRankedOn: killed by P1_OnlyAnEligibleSignalIsRankedOn.1 (line 135: removed the conjunct `&& IsEligibleIn(schema, relation.value, registry)` from the guard, leaving `if relation.Some?`; failed row: P1_OnlyAnEligibleSignalIsRankedOn (correctness))

#### `ObjectSearchGrpcService.ReadableDecayField`

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:1112 signature `string? ReadableDecayField(SchemaDescriptor schema, AuthorizationDecision decision)` (T4 S6) | 42 | Deviation: `decision` is narrowed to the one member read, `decision.AllowedFields` (`IReadOnlySet<string>?`), modelled as `allowed: Option<seq<CsString>>` per model notes. Instance method's `logger` is not modelled (only used for logging inside the resolver). |
| ObjectSearchGrpcService.cs:1114 `var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);` (T4 S6) | 44 (call), 28 (approved axiom) | Approved axiom `ResolveDecayField(schema: Desc)`; the `logger` argument is dropped (side effect only; the resolver's ConditionalWeakTable cache makes the value a function of the schema instance). |
| ObjectSearchGrpcService.cs:1115 `decayField is not null && decision.AllowedFields is not null && !decision.AllowedFields.Contains(decayField)` (T4 S6) | 45, 36-39 | Same conjunct order. `AllowedFields.Contains` is `ContainsIC` (EqIC membership) because the set is built with `StringComparer.OrdinalIgnoreCase` (RowFieldAuthorizationEvaluator.cs:121). No exception path: `Contains` on a non-null set with a non-null argument cannot throw. |
| ObjectSearchGrpcService.cs:1116 `? null` (T4 S6) | 46 | |
| ObjectSearchGrpcService.cs:1117 `: decayField;` (T4 S6) | 47 | |
| (vocabulary) `string`, nullable, `SchemaDescriptor` | 8-24 | Shared vocabulary as given; `EqIC` over the approved axiom `OrdinalUpper` (line 30). |

G5:

- P1_ADeniedDecayColumnGivesNoDecay: killed by P1_ADeniedDecayColumnGivesNoDecay.1 (line 45: negated the readability guard `!ContainsIC(allowed.value, decayField.value)` to `ContainsIC(...)`; failed row: P1_ADeniedDecayColumnGivesNoDecay (correctness))
- P2_AReadableOrUnrestrictedDecayFieldIsKept: killed by P2_AReadableOrUnrestrictedDecayFieldIsKept.1 (line 45: negated the readability guard `!ContainsIC(allowed.value, decayField.value)` to `ContainsIC(...)`; failed row: P2_AReadableOrUnrestrictedDecayFieldIsKept (correctness))

#### `ObjectSearchGrpcService.SimilarResponseAllowList`

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.Api/NamingExtensions.cs:16-17 `ToCamelCase` (pre-existing, unchanged by the plan) | 36-41 `ToCamelCase` | `char + string` is C# string concatenation, so it becomes `[CharToLowerInvariant(name[0])] + name[1..]`. `char.ToLowerInvariant` is the approved axiom `CharToLowerInvariant`. The length is preserved, so the result is still a CsString. |
| Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:468 `new Dictionary<string, ColumnDescriptor>(StringComparer.Ordinal)` (T4 S6) | 56 | Becomes `map<CsString, Column>`. Ordinal comparison is equality of seq<char>. |
| ObjectSearchGrpcService.cs:469-470 `foreach (var col in schema.ScalarColumns) columnLookup[col.Name.ToCamelCase()] = col;` (T4 S6) | 43-51 `AddScalarColumns`, 57 | Kept in code order: the recursion walks from the head of the list, and a later indexer assignment overwrites an earlier one, as `m[k := v]` does. |
| ObjectSearchGrpcService.cs:471 `columnLookup[schema.KeyColumn.Name.ToCamelCase()] = schema.KeyColumn;` (T4 S6) | 58 | Direct. |
| ObjectSearchGrpcService.cs:472 `columnLookup["key"] = schema.KeyColumn;` (T4 S6) | 59 | Direct. It is the last assignment, so it overwrites any scalar column whose camel-cased name is "key". |
| ObjectSearchGrpcService.cs:474 `foreach (var (payload, score) in rows)` (T4 S6) | (not modelled) | Each row is independent. `ResponseFields` models one row's Struct, as the model notes instruct. `score` is not read before `MaskDisallowedFields`. |
| ObjectSearchGrpcService.cs:476 `var protoStruct = new Struct();` (T4 S6) | 83 (`map[]` argument) | An empty Struct.Fields becomes an empty map. |
| ObjectSearchGrpcService.cs:477-481 `foreach (var kvp in payload) if (columnLookup.TryGetValue(kvp.Key, out var col)) protoStruct.Fields[col.Name] = ConvertPayloadValue(kvp.Value, col.SqlType);` (T4 S6) | 62-78 `FillRow` | Deviation: the payload is an `IReadOnlyDictionary<string,string>`, modelled as `seq<(CsString, CsString)>` in enumeration order. Key uniqueness is not assumed, so the model is strictly more general. `TryGetValue` becomes `kvp.0 in columnLookup` plus `columnLookup[kvp.0]`. The MapField indexer set becomes a map update that overwrites. That indexer throws on a null key or value: the key `col.Name` is a non-nullable string, and the value comes from `ConvertPayloadValue`. That function (ObjectSearchGrpcService.cs:1353-1366) always returns a `Value.For*` object and is the approved axiom, so the model has no throwing path. |
| Combined row result (T4 S6) | 80-84 `ResponseFields` | `FillRow(BuildColumnLookup(schema), map[], payload)`. |
| SchemaDescriptor / ColumnDescriptor | 12, 15 | `Desc` keeps only `KeyColumn` and `ScalarColumns`, the fields this candidate reads. The vocabulary allows omitting fields that no header names. `Column(Name, SqlType, IsNullable)` follows the vocabulary. |
| Shared vocabulary `ColumnNames` | 17-20 | `[KeyColumn.Name] + each ScalarColumns[i].Name` in order. `EqIC`, `IsTenantColumn`, `Option`, `Result`, `StatusCode` and `Int32` are unused here and left out. |
| Helper lemmas | 90-169 | `AddScalarColumnsValues`, `LookupValuesAreColumns`, `FillRowKeys`, `FillRowMonotone` and `FillRowHit` are all proved, with no assume, expect or axiom. |
| Frozen properties | 175-191 | Headers and clauses are copied verbatim. |

G5:

- P1_ResponsesCarryOnlyDescriptorColumns: killed by P1_ResponsesCarryOnlyDescriptorColumns.1 (line 75: FillRow writes the raw payload key `fields[kvp.0 := ...]` instead of the looked-up column name `fields[columnLookup[kvp.0].Name := ...]`; failed row: FillRowKeys (correctness), a helper P1's proof calls)
- P2_ThePayloadKeyIsAnsweredAsTheKeyColumn: killed by P2_ThePayloadKeyIsAnsweredAsTheKeyColumn.1 (line 59: dropped the `"key"` alias, `columnLookup2["key" := schema.KeyColumn]` -> `columnLookup2`; failed row: P2_ThePayloadKeyIsAnsweredAsTheKeyColumn (correctness))

#### `PopularitySignalValidator.SignalKeyCollision`

Worktree paths are relative to `.../dpv-replay/wt`. File: `Iverson.Server/Iverson.Api/Grpc/PopularitySignalOptions.cs` unless named otherwise.

| Worktree file:line | Introduced by | Dafny lines | Note |
|---|---|---|---|
| :10 `PopularitySignalEntry(ParentType, Relation)` | pre-plan | 63 | Both fields are non-nullable strings, so they are `CsString`. |
| :14 `Section`; `Iverson.StarRocks/EngagementStoreOptions.cs:5` `Section` | pre-plan | 121-122 | Constants. |
| :70-71 signature | pre-plan | 164-167 | `options` is reduced to `Signals` plus the bool `recencyBoostPositive` (`RecencyBoost > 0`, a double comparison, per the brief). `logger` is dropped because logging is a no-op. The void return is `Returned`, and every throw is `InvalidOperation(msg)`. |
| :73 `HashSet<string>(OrdinalIgnoreCase)` | pre-plan | 125-128, 166 (`[]`), 183 | `seen` is a `seq<CsString>`. A failing `Add` is `SeenContains` (some element is EqIC). A successful `Add` appends. |
| :74 `foreach` | pre-plan | 170-228 (`Loop`, recursion on `j`) | Each iteration is one call, and `continue`/fall-through recurse with `j + 1`. |
| :76-81 duplicate ParentType throw | pre-plan | 177-181 | |
| :83-87 engagement-disabled throw | pre-plan | 184-187 | |
| :89-99 `registry.Get`, null → warn + `continue` | pre-plan | 189-191; `RegistryGet` 49-54 (`SchemaRegistry.cs:16-17`) | The log is a no-op. |
| :101-104 `HasVectorOrChunkFields` throw | pre-plan | 194-196 | Approved axiom (line 10). |
| :110 `countKey = signal.Relation.ToCamelCase() + "Count"` | **T4 S6** | 198; `ToCamelCase` 57-60 (`NamingExtensions.cs:16-17`) | `char + string` concatenates. `char.ToLowerInvariant` is the approved axiom `CharToLowerInvariant` (line 14). |
| :111-113 `ScalarColumns.Append(KeyColumn).FirstOrDefault(c => EqualsIC(c.Name, countKey) \|\| EqualsIC(c.Name, countKey + "Buckets"))` | **T4 S6** | 199; `Collides` 141-144; `FirstColliding` 147-152 | `Append` puts KeyColumn last, which is `ScalarColumns + [KeyColumn]`. `FirstOrDefault` is the first match in order, or None. `string.Equals(..., OrdinalIgnoreCase)` is `EqIC`. |
| :114-119 `collidingColumn is not null` → throw | **T4 S6** | 200-204 | The message is concatenated as interpolated. |
| :121-125 relation lookup `?? throw` | pre-plan | 206-209; `FirstRelation` 155-160 | |
| :127-131 not-OneToMany throw | pre-plan | 210-213; `KindName` 131-138 | `{relation.Kind}` is enum `ToString()`, modelled as `KindName`. |
| :133-136 child `registry.Get ?? throw` | pre-plan | 215-218 | |
| :138-142 `IsEngagementEligible` throw | pre-plan | 219-222 | Approved axiom (line 12). |
| :146-154 `IsEligible(...)` → LogWarning | **T4 S6** | 224; `IsEligible` and its helpers 72-117 (`PopularitySignalEligibility.cs:24-61`) | The value only selects a log line, so the outcome does not change. `IsEligible` is copied from the PopularitySignalEligibility.IsEligible candidate's model. |
| :156-162 RecencyBoost warning | pre-plan | 225 | Log only. `recencyWarn` is computed and then unused. |
| :163 loop end → next iteration / :164 return | pre-plan | 226, 175 (`Returned`) | |

Deviations:
- `Desc` omits the fields this candidate never reads directly: `TableName`, `VectorFields`, `ChunkFields`, `TenantColumn`, `OwnerTenantId`, `EnrichmentTargets` and `HasDocumentTemplate`. `VectorFields` and `ChunkFields` are read only through the approved `HasVectorOrChunkFields`.
- No added preconditions. `Loop`'s `requires 0 <= j <= |signals|` covers only the model's own recursion index.
- Nothing on these paths throws anything except `InvalidOperationException`. Every input is non-nullable, and `IsEligible` and the logging cannot throw.

Helper lemmas (proved): `FirstCollidingFinds` (lines 233-240) and `LoopRefusesAt` (lines 244-268). `LoopRefusesAt` is an induction on `i - j`: every iteration before `i` either throws `InvalidOperation` or continues, and iteration `i` reaches the collision check unless it threw earlier. The P1 body bridges `(k + "Count") + "Buckets" == k + "CountBuckets"`.

G5:

- P1_ASignalKeyCollidingWithAParentColumnIsRefused: killed by P1_ASignalKeyCollidingWithAParentColumnIsRefused.1 (line 143, Collides: removed the disjunct `|| EqIC(c.Name, countKey + "Buckets")`, so a column matching `{relation}CountBuckets` is no longer refused; failed row: P1_ASignalKeyCollidingWithAParentColumnIsRefused (correctness))

#### `EnrichmentTargetRules.Violation`

All rows: Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs, T5 S3.

| Worktree lines | Code | Dafny lines | Note / deviation |
|---|---|---|---|
| 19 | SyntheticDocumentField = "Document" | 69 | |
| 25 | string? Violation(SchemaDescriptor) | 208 | Option<seq<char>>. |
| 27-28 | EnrichmentTargets.Count == 0 → null | 210 | |
| 30 | Authorization?.OwnerField | 116-119, 212 | OwnerFieldOf; model allows Some("") (record init normalises "" → null) — harmless since line 74 tests IsNullOrEmpty. |
| 35-40 | sourceNames HashSet(OrdinalIgnoreCase) | 77-97, 213 | Deviation: HashSet as the sequence it is built from; Contains → ContainsIC; Count == 0 → empty; neither depends on dedup. |
| 42 | foreach target, early return | 149-155, 214-215 | recursion, order and first violation kept. |
| 47-51 | ScalarColumns.FirstOrDefault(EqIC) ?? key | 100-113 | FirstMatchingColumn, ColumnOf. |
| 52-56 | not-a-column message | 125-127 | |
| 61-66 | not TEXT / TEXT[] | 128-130 | |
| 72-79 | key / tenant / owner | 131-135 | |
| 81-87 | sourceNames.Contains(target) | 136-140 | ContainsIC. |
| 89-93 | Extracted + IsNullOrWhiteSpace(Hint) | 141-143 | NullOrWhiteSpace over approved CharIsWhiteSpace. |
| 98-102 | sourceNames.Count == 0 | 216-218 | |
| 104-106 | targetNames | 158-161, 220 | |
| 112 | FieldPermissions ?? [] | 221 | |
| 116-123 | write-restricted check | 165-170, 222-226 | |
| 129-130 | outer loop over read-restricted sources | 197-205, 228 | |
| 132-147 | inner target loop, !covered | 183-193 | |
| 134-137 | Any(... EqIC && ReadableRoles.All(source.Contains Ordinal)) | 175-180 | Covered. |
| 150 | return null | 199, 185 | |

Non-nullable members taken as non-null; only Hint, Authorization, OwnerField are Option.

G5:

- P1_AdmittedTargetsAreTextOrTextArrayColumns: killed by P1_AdmittedTargetsAreTextOrTextArrayColumns.1 (line 128: negated the TEXT/TEXT[] column-type guard, `!(!EqIC(SqlType,"TEXT") && !EqIC(SqlType,"TEXT[]"))`; failed row: P1_AdmittedTargetsAreTextOrTextArrayColumns (correctness))
- P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner: killed by P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner.1 (line 131: removed the `EqIC(t.ColumnName, d.KeyColumn.Name) ||` disjunct from the key/tenant/owner guard; failed row: P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner (correctness))
- P3_AdmittedTargetsHaveASourceAndAreNotSources: killed by P3_AdmittedTargetsHaveASourceAndAreNotSources.1 (line 136: negated the source-name guard, `else if !ContainsIC(sourceNames, t.ColumnName)`; failed row: P3_AdmittedTargetsHaveASourceAndAreNotSources (correctness))
- P4_AdmittedExtractedTargetsHaveAHint: killed by P4_AdmittedExtractedTargetsHaveAHint.1 (line 141: negated the hint test, `t.Kind == Extracted && !NullOrWhiteSpace(t.Hint)`; failed row: P4_AdmittedExtractedTargetsHaveAHint (correctness))
- P5_NoAdmittedTargetIsWriteRestricted: killed by P5_NoAdmittedTargetIsWriteRestricted.1 (line 223: negated the write-restriction guard, `if writeRestricted.None? then`; failed row: NoneFacts (correctness), the helper P5 calls; the mutant also fails Violation (well-formedness), since the message reads writeRestricted.value on None)
  - (controller re-check: P5_NoAdmittedTargetIsWriteRestricted.2 — line 223 guard `if writeRestricted.Some? then` → `if false then`, well-formed; failed row: NoneFacts (correctness), the helper P5 calls)
- P6_EveryTargetReaderCanReadEverySource: killed by P6_EveryTargetReaderCanReadEverySource.1 (line 186: negated the coverage guard, `else if Covered(fps, source, ts[0])`; failed row: InnerLoopNone (correctness), the helper P6's proof calls through NoneFacts -> ReadLoopNone -> InnerLoopNone)
- P7_ADescriptorKeepingEveryRuleIsAdmitted: killed by P7_ADescriptorKeepingEveryRuleIsAdmitted.1 (line 141: flipped the kind comparison, `t.Kind != Extracted && NullOrWhiteSpace(t.Hint)`; failed row: P7_ADescriptorKeepingEveryRuleIsAdmitted (correctness))

#### `SchemaRegistryRepository.OwnerQueries`

| Worktree file:line | Introduced by | Dafny lines | Notes |
|---|---|---|---|
| Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs:40-43 `GetOwnersAsync` | T7 S3 | 41 (`LowerMatches`), 46-55 (`GetOwners`) | Deviation: the result list is a `multiset<Option<CsString>>`, one element per matching row (SQL gives no row order); ghost recursive function over the map using `:\|`. `->>'ownerTenantId'` is the approved `JsonOwner`; `lower()` is the approved `PgLower`. |
| SchemaRegistryRepository.cs:47-57 `UpsertIfOwnerAsync` | T7 S3 | 61-72 | Same branch order as the SQL: no conflict inserts (1 row, true); a conflict where the WHERE holds updates (1 row, true); otherwise 0 rows, false, table unchanged. `IS NOT DISTINCT FROM` is `Option` equality. Deviation: the `@Json::jsonb` cast's throw on invalid JSON and DB faults are not modelled; `updated_at` not modelled; stored jsonb identified with the json text. |
| SchemaRegistryRepository.cs:63-70 `ExistsAsync` | T7 S3 | 75 (`Substr2`), 80-83 (`Exists`) | Deviation: `substr(x, 2)` is `if \|x\| == 0 then [] else x[1..]` (Postgres returns '' for ''). |
| Iverson.Server/Iverson.Api/NamingExtensions.cs:5-14 `ToSnakeCase` | existing code | 88-98 | Loop as recursion; `char.IsUpper` is the approved bodiless `CharIsUpper`; `char.ToLowerInvariant` is the approved `CharToLowerInvariant`; StringBuilder overflow throw not modelled (support code used only in properties). |
| Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:31 `ToTableName` | existing code | 101 | `ToSnakeCase(n) + "s"`. |
| (none) | helpers | 105-155 | AsciiLower/AsciiUpper; lemmas UpperEqImpliesLowerEq, EqICImpliesPgLowerEq, GetOwnersHasMatch, SnakeFromEq — all proved. |
| (none) | properties | P1 160-166, P2 169-173, P3 176-198, P4 201-221 | Frozen headers. |

P3 holds with `CharIsUpper` uninterpreted.

Re-proof without automatic induction (summary of the return's appended section). Under `--manual-lemma-induction` the helper `GetOwnersHasMatch` failed its postcondition on the empty `k == n` branch (25 verified, 1 error), which automatic induction had covered. Only that helper's body changed: it now names `rest := GetOwners(tbl - {k}, t)` and asserts the multiset split and singleton membership (one repair round, no `{:induction}` attributes). Result: 26 verified, 0 errors, no warnings. Frozen text, axioms and transliteration unchanged; helpers are now 105-159, P1 164-170, P2 173-177, P3 180-202, P4 205-225.

G5:

- P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds: killed by P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds.1 (line 54, GetOwners: negated the row guard `if LowerMatches(k, t)` to `if !LowerMatches(k, t)`; failed row: GetOwnersHasMatch (correctness))
- P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner: killed by P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner.1 (line 66, UpsertIfOwner: flipped the conflict WHERE `JsonOwner(tbl[t]) == owner` to `!=`; failed row: P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner (correctness))
- P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable: killed by P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable.1 (line 82, Exists: removed the conjunct `&& Substr2(k) == Substr2(t)`; failed row: P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable (correctness))
- P4_AStoredFirstLetterVariantCountsAsTheSameType: killed by P4_AStoredFirstLetterVariantCountsAsTheSameType.1 (line 82, Exists: flipped `Substr2(k) == Substr2(t)` to `!=`; failed row: P4_AStoredFirstLetterVariantCountsAsTheSameType (correctness))
  - (controller re-check under --manual-lemma-induction after the GetOwnersHasMatch re-proof: P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds.2, same mutation on line 54; failed row: GetOwnersHasMatch (correctness))

#### `PrivilegedRequestRepository.Transitions`

Worktree: Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs, T7 S3 unless noted.

| Worktree file:line | Dafny lines | Notes / deviations |
|---|---|---|
| PrivilegedRequestRow.cs:8-15 status constants (T7) | 29 ReqStatus | one constructor per distinct constant. |
| PrivilegedRequestSchema.cs:13-27; PrivilegedRequestRow.cs:6 (T7) | 33-34 Req | Kind, Payload, RequestedBy, CompletedAt omitted (no property names them); instants are int. |
| cs:16-30 InsertAsync | 43-48 Insert | Both now() same instant; NULL ExpiresAfterSeconds → NULL ExpiresAt. Deviations: TimeSpan.TotalSeconds (double) × interval modelled as int interval (G4); insert failure paths not modelled (frozen header returns Req). |
| cs:43-49 ExpireIfPendingAsync | 52-56 | Single row by Id; bool = rows affected == 1; missing Id not modelled. |
| cs:56-57 WHERE of ListClaimableAsync | 60-63 ClaimableWhere, 65-71 FilterClaimable | proved helper keeping order. |
| cs:51-61 ListClaimableAsync, ORDER BY/LIMIT 58-59 | 74-77 | ORDER BY/LIMIT via approved axiom. Deviation: negative LIMIT throw not representable; P3 unaffected. |
| cs:64-71 TryClaimAsync | 81-86 | WHERE/SET one to one; bool == 1. |
| cs:86-94 FailAbandonedAsync | 91-96 | Per-row model; NULL ClaimedAt selects nothing. Deviations: AgeSeconds double interval → int (G4); ::jsonb normalisation/throw not modelled; CompletedAt omitted. |
| cs:98-106 DeleteFinishedOlderThanAsync | 103-108 DeletesRow | Per-row predicate; three-valued logic: NULL ExpiresAt keeps the row (ExpiresAt.Some? && value < now). Age interval int (G4). |

G5:

- P1_AClaimTakesOnlyAPendingUnexpiredRequest: killed by P1_AClaimTakesOnlyAPendingUnexpiredRequest.1 (line 83, TryClaim: flipped the expiry comparison `r.ExpiresAt.value > now` to `>= now`; failed row: P1_AClaimTakesOnlyAPendingUnexpiredRequest (correctness))
- P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires: killed by P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires.1 (line 54, ExpireIfPending: negated the guard `if r.Status == Pending` to `if r.Status != Pending`; failed row: P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires (correctness))
- P3_OnlyClaimableRequestsAreListed: killed by P3_OnlyClaimableRequestsAreListed.1 (line 62, ClaimableWhere: removed the conjunct `r.Status == Pending &&`; failed row: P3_OnlyClaimableRequestsAreListed (correctness))
- P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed: killed by P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed.1 (line 93, FailAbandoned: flipped the cutoff comparison `r.ClaimedAt.value < now - age` to `<=`; failed row: P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed (correctness))
- P5_CleanupDeletesOnlyFinishedRequests: killed by P5_CleanupDeletesOnlyFinishedRequests.1 (line 107, DeletesRow: flipped the pending-expired comparison `r.ExpiresAt.value < now` to `>= now`; failed row: P5_CleanupDeletesOnlyFinishedRequests (correctness))
- P6_ARequestExpiresAfterItsWaitOrNever: killed by P6_ARequestExpiresAfterItsWaitOrNever.1 (line 46, Insert: replaced the Some arm's `Some(now + expiresAfter.value)` with the else arm's `None`; failed row: P6_ARequestExpiresAfterItsWaitOrNever (correctness))

#### `PostgresPrivilegedRequestLocks.Keys`

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs:18 `RegistrationKey = 1L << 41` (T7 S3) | 22-23 | `long` becomes Dafny `int` with the value 0x200_0000_0000 (2^41). This is exact: 1L << 41 does not overflow, and the shift count 41 is below the 64-bit mask. P1 clause 3 proves the bigint range separately. |
| PostgresPrivilegedRequestLocks.cs:19 `TypeKeyBase = 1L << 40` (T7 S3) | 25-26 | `long` becomes `int` with the value 0x100_0000_0000 (2^40), also exact. |
| PostgresPrivilegedRequestLocks.cs:21-22 `AcquireRegistrationAsync` key `@Key`, `{ Key = RegistrationKey }` (T7 S3) | 28-30 | The key is the bigint parameter, so the value is `RegistrationKey`. `RegistrationLockKey()` names it, but the frozen lemmas use `RegistrationKey` directly, which is the same value. |
| PostgresPrivilegedRequestLocks.cs:26-27 `AcquireTypeAsync` key `@Base + hashtext(lower(@TypeName))`, `{ Base = TypeKeyBase, TypeName = typeName }` (T7 S3) | 32-39 | `TypeKey`. Npgsql binds the C# `long` Base as `bigint`. `hashtext` gives `int4`, and `bigint + int4` is `int84pl`, computed exactly; Postgres would raise on overflow, and P1 clause 3 rules that out. `lower` is the axiom `PgLower` and `hashtext` is the axiom `HashText`, both at lines 14-18. |
| PostgresPrivilegedRequestLocks.cs:29-64 `AcquireAsync` / `Lease` (T7 S3) | none | Not modelled. The candidate covers only the key expressions. The unlock in `Lease.DisposeAsync` (line 57) reuses the same `keySql` and `param` as the lock, so it computes the same key. |

Deviations: none from the model guidance. The only addition is the unused `RegistrationLockKey()` at line 30, which documents the registration key expression.

Sanity mutant (in a throwaway copy, since deleted): setting `TypeKeyBase` to 2^41 makes the run report `4 verified, 1 error` (P2 fails). So the proofs are not vacuous.

G5:

- P1_EveryLockKeyLiesOutsideTheInt4Range: killed by P1_EveryLockKeyLiesOutsideTheInt4Range.1 (line 26: `TypeKeyBase` set to `0x100_0000` (1 << 24) instead of `0x100_0000_0000` (1 << 40), so a type key can fall inside the int4 range; failed row: P1_EveryLockKeyLiesOutsideTheInt4Range (correctness))
- P2_NoTypeLockIsTheRegistrationLock: killed by P2_NoTypeLockIsTheRegistrationLock.1 (line 26: `TypeKeyBase` set to `0x200_0000_0000`, equal to `RegistrationKey`, so a type whose hash is 0 takes the registration key; failed row: P2_NoTypeLockIsTheRegistrationLock (correctness))
- P3_ATypeLockFoldsTheNamesCase: killed by P3_ATypeLockFoldsTheNamesCase.1 (line 38: `TypeKey` hashes `typeName` instead of `PgLower(typeName)`; failed row: P3_ATypeLockFoldsTheNamesCase (correctness))

#### `SchemaRegistrationApplier.ExecuteAsync`

Source: wt/Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs (whole file from T7 S3, step 3p).

| Worktree file:line (T7 S3) | Dafny lines | Note |
|---|---|---|
| :20-22 `OwnedByAnotherTenant` | 50-53 | Exception is outcome `Rpc(PermissionDenied, msg)`; interpolation as concatenation. |
| :24-28 `ExecuteAsync` signature | 131 | `ct` dropped (unread). Result `(Outcome, seq<Effect>)`; thrown exception is the non-`Done` outcome. |
| :29, :58 first `foreach` | 110-120 `ExecuteDescriptors` | Recursion in batch order; stops at first non-`Done`, keeping earlier effects. |
| :33-35 step 1 GetOwnersAsync/Any/throw | 89-91, `AnyOwnerDiffers` 65-70 | `Any(owner => owner != OwnerTenantId)` recursive; `string? !=` ordinal and null-aware as `Option` inequality. |
| :41-42 step 2 ExistsAsync then RequireAdoptableAsync | 93-95 | RequireAdoptableAsync returns `Option<Outcome>` (None = returned, Some(o) = threw o). |
| :45-52 step 3 ApplySchemaAsync catching SchemaDriftException | 97-100 | `db.ddl(TableName)` → Applied / Drift(m) / DdlFault; Drift(m) → Rpc(FailedPrecondition, m); DdlFault → Fault with no Ddl effect. Deviation: `SchemaBuilder.ToTableSchema(descriptor)` folded into the oracle keyed by table name (pure projection, no throwing path). |
| :55 JsonSerializer.Serialize | 103 | Calls approved axiom `SerializeDescriptor`; value unused (upsert oracle does not take the JSON). |
| :56-57 step 4 UpsertIfOwnerAsync/throw | 104-106 | false → OwnedByAnotherTenant with effects [Ddl]; true → Done with [Ddl, Write]. |
| :63-64 step 5 EnqueueTypeAsync loop | 123-128 `EnqueueAll`, 133-135 | Runs only once every descriptor is Done; enqueue exception not modelled per the brief. |
| :70-83 `RequireAdoptableAsync` | 73-83 | Branch order preserved: null, !IsOrdinaryTable, !HasTenantColumn, owned + foreign rows. Deviation: `describe`/`foreignRows` not keyed by TenantColumn (real calls also pass it). |
| :85-88 `NotAdoptable` | 56-61 | Rpc(AlreadyExists, msg). |

Other deviations: `Desc` keeps only TypeName, TableName, TenantColumn, OwnerTenantId. Helpers AnyOwnerDiffersIff, SingleDescriptor, EnqueueAllShape, DescriptorsShape — all proved.

G5:

- P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything: killed by P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything.1 (line 91, ApplyDescriptor: negated the owner guard `if AnyOwnerDiffers(...)` to `if !AnyOwnerDiffers(...)`; failed row: P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything (correctness))
- P2_AnUnregisteredTypeNeverAdoptsAForeignTable: killed by P2_AnUnregisteredTypeNeverAdoptsAForeignTable.1 (line 94, ApplyDescriptor: negated the adoption-check guard `if !db.exists_(...)` to `if db.exists_(...)`; failed row: P2_AnUnregisteredTypeNeverAdoptsAForeignTable (correctness))
- P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable: killed by P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable.1 (line 77, RequireAdoptableAsync: negated the guard `!existing.value.IsOrdinaryTable` to `existing.value.IsOrdinaryTable`; failed row: P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable (correctness))
- P4_DriftIsFailedPreconditionAndNothingIsWritten: killed by P4_DriftIsFailedPreconditionAndNothingIsWritten.1 (line 99, ApplyDescriptor: replaced the Drift arm's result `(Rpc(FailedPrecondition, m), [])` with the DdlFault arm's `(Fault, [])`; failed row: P4_DriftIsFailedPreconditionAndNothingIsWritten (correctness))
- P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied: killed by P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied.1 (line 104, ApplyDescriptor: negated the write guard `if !db.upsert(...)` to `if db.upsert(...)`; failed row: P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied (correctness))
- P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders: killed by P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders.1 (line 135, ExecuteAsync: swapped the effect order `r.1 + EnqueueAll(changed)` to `EnqueueAll(changed) + r.1`; failed row: P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders (correctness))

#### `QueuedSchemaRegistrationExecutor.ExecuteAsync`

All ranges in Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs, T7 S3.

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| :16-18 class, :22-25 signature | 72 `Execute(ds, changed, w, a)` | `ct` not modelled; cancellation covered only for LoadAsync as `reloadOk == false`. |
| :20 `NotPickedUp` | 62 | |
| :27 `var waits = requests.Waits` | 74-75 | TimeSpan as int ticks (`Waits`, line 43). |
| :28-29 serialize payload | 76-77 | Approved axiom; value computed, goes into no Call (Submit carries only expiresAfter, per guidance). |
| :33-37 SubmitAsync(..., requestedBy, expiresAfter: RegisterClaim) | 78-81, FirstOwner 65-68 | requestedBy computed, not recorded; insert DB exceptions not modelled. |
| :39-40 claim WaitWhileAsync(RegisterClaim) | 82-84 | WaitClaim(RegisterClaim); answer `a.afterClaim` unconstrained oracle (poll loop PrivilegedRequestClient.cs:27-38 not transliterated). Task.Delay OCE and ReadAsync missing-row IOE not modelled. |
| :42 if Pending | 85-86 | |
| :46 ExpireIfPendingAsync | 87-89 | Expire; answer `a.expired`. |
| :47 throw Unavailable | 90-91 | Rpc(Unavailable, NotPickedUp). |
| :48 ReadAsync | 93-96 | Reread; answer `a.reread`. |
| :51-53 run WaitWhileAsync(RegisterRun) | 104-108 (AfterClaim, 102) | Lines 51-69 split into helper AfterClaim, branch order kept. |
| :55 switch | 109-110 | Status strings as ReqStatus. |
| :57-59 Succeeded: LoadAsync, return | 111-113 | Records Reload; Done if reloadOk else Fault. |
| :60-63 Running: DeadlineExceeded | 114-118 | Same two-literal concatenation. |
| :64-66 Failed: From(request)!, Status(Code, Message ?? "") | 119-127 | ReqRow.Result stands for From(row); JSON round trip not modelled; null result → Fault. |
| :67-68 default: Unavailable | 128-130 | Pending and Expired. |

Other: Desc keeps only TypeName, OwnerTenantId.

G5:

- P1_SuccessIsAnsweredOnlyAfterTheApiReloads: killed by P1_SuccessIsAnsweredOnlyAfterTheApiReloads.1 (line 113, AfterClaim: negated the reload guard `if a.reloadOk then Done else Fault` to `if !a.reloadOk then ...`; failed row: P1_SuccessIsAnsweredOnlyAfterTheApiReloads (correctness))
- P2_ARequestNeverClaimedIsExpiredAndUnavailable: killed by P2_ARequestNeverClaimedIsExpiredAndUnavailable.1 (line 89, Execute: negated the expiry guard `if a.expired then` to `if !a.expired then`; failed row: P2_ARequestNeverClaimedIsExpiredAndUnavailable (correctness))
- P3_AClaimedRequestStillRunningIsDeadlineExceeded: killed by P3_AClaimedRequestStillRunningIsDeadlineExceeded.1 (line 116, AfterClaim: the Running arm's code `DeadlineExceeded` swapped to the default arm's `Unavailable`; failed row: P3_AClaimedRequestStillRunningIsDeadlineExceeded (correctness))
- P4_AFailedRequestAnswersWithItsStoredStatusAndMessage: killed by P4_AFailedRequestAnswersWithItsStoredStatusAndMessage.1 (line 127, AfterClaim: the Failed arm throws `Rpc(Unavailable, ...)` instead of the stored `Rpc(result.Code, ...)`; failed row: P4_AFailedRequestAnswersWithItsStoredStatusAndMessage (correctness))
- P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded: killed by P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded.1 (line 81, Execute: submit without the expiry, `Submit(Some(waits.RegisterClaim))` to `Submit(None)`; failed row: P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded (correctness))

#### `PrivilegedRequestExecutor.RunAsync`

All code in Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs (RunAsync, RegisterSchemaAsync, ReplayDlqAsync, Deserialize from T7 S3; ReconcileTypeAsync from T8 S3).

| Worktree file:line | Task/step | Dafny lines | Notes / deviations |
|---|---|---|---|
| Contracts.cs:34-36 `PrivilegedRequestResult`, `Ok(int? count)` | T7 | 61, 67-70 | `int?` → `Option<Int32>`; `string?` Message → `Option<seq<char>>`. |
| Contracts.cs:10-12 kind constants | T7 | 101-103 | |
| Contracts.cs:16,19,26-28 payload records | T7/T8 | 37 `Payload` | Only TypeName and Id kept; a JSON-null TypeName is not representable (lock and reconcile outcomes are oracles either way). |
| :31-60 `RunAsync` | T7 S3 | 74-93 | |
| :33-35 try / LoadAsync | T7 S3 | 96-100 | `Loaded` prepended only when the load completes. |
| :37-43 kind switch | T7 S3 | 101-109 | Ordinal string switch; null Kind not representable but behaves like any non-matching string (default arm → OtherEx). |
| :45-48 catch RpcException | T7 S3 | 81-82 | `ex.Status.Detail` non-null `seq<char>`. |
| :49-53 catch OCE when ct.IsCancellationRequested | T7 S3 | 83-86 | |
| filter false then catch (Exception) | T7 S3 | 87-89 | |
| :54-59 catch (Exception) | T7 S3 | 91-92 | LogError assumed not to throw (not modelled). |
| :64-71 RegisterSchemaAsync | T7 S3 | 113-130 | |
| :68, :81 `await using` lock disposal | T7 S3 / T8 S3 | (none) | Not modelled: dispose assumed not to throw. |
| :77-91 ReconcileTypeAsync | T8 S3 | 133-155 | |
| :86 ct.ThrowIfCancellationRequested() | T8 S3 | 144-145 | Raises Canceled when o.cancelled, after Reconciled(t) recorded. |
| :95-106 ReplayDlqAsync | T7 S3 | 158-181 | GetUnreplayedByIdAsync records no effect. |
| :108-110 Deserialize<T> | T7 S3 | 117,127-129;137,154;162,180 | Inlined; JSON throw, null result or wrong-kind payload → Raised(OtherEx). Deviation: wrong-kind payload treated as a throw (real STJ might deserialize with null fields and fail later, after the lock). |
| :101 interpolation with payload.Id | T7 S3 | 55 (`guidText`), 170 | Deviation: Oracle gains field `guidText: Guid -> seq<char>` for Guid.ToString() (external call, no extra axiom); no header constructs an Oracle. |

G5:

- P1_ARefusalIsRecordedWithItsStatusAndMessage: killed by P1_ARefusalIsRecordedWithItsStatusAndMessage.1 (line 82, RunAsync: the RpcException catch drops the detail, `PResult(code, Some(detail), None)` to `PResult(code, None, None)`; failed row: P1_ARefusalIsRecordedWithItsStatusAndMessage (correctness))
- P2_AnUnexpectedFailureIsInternalWithAGenericMessage: killed by P2_AnUnexpectedFailureIsInternalWithAGenericMessage.1 (line 92, RunAsync: the catch (Exception) arm's code `Internal` swapped to `Unavailable`; failed row: P2_AnUnexpectedFailureIsInternalWithAGenericMessage (correctness))
- P3_StoppingTheWorkerIsUnavailableNeverAPartialResult: killed by P3_StoppingTheWorkerIsUnavailableNeverAPartialResult.1 (line 85, RunAsync: negated the cancellation filter `if o.cancelled` to `if !o.cancelled`; failed row: P3_StoppingTheWorkerIsUnavailableNeverAPartialResult (correctness))
- P4_EachKindTakesOnlyItsOwnLock: killed by P4_EachKindTakesOnlyItsOwnLock.1 (line 126, RegisterSchemaAsync: swapped the effects `[RegistrationLocked, Applied]` to `[Applied, RegistrationLocked]`; failed row: P4_EachKindTakesOnlyItsOwnLock (correctness))
- P5_TheRegistryIsReloadedBeforeAnything: killed by P5_TheRegistryIsReloadedBeforeAnything.1 (line 109, TryBlock: moved the load effect after the arm's, `[Loaded] + arm.1` to `arm.1 + [Loaded]`; failed row: P5_TheRegistryIsReloadedBeforeAnything (correctness))
- P6_AReplayRechecksTheRowThenProducesBeforeMarking: killed by P6_AReplayRechecksTheRowThenProducesBeforeMarking.1 (line 179, ReplayDlqAsync: swapped the effects `[Produced, Marked]` to `[Marked, Produced]`; failed row: P6_AReplayRechecksTheRowThenProducesBeforeMarking (correctness))
- P7_AReconcileRecordsItsCountOrNotFound: killed by P7_AReconcileRecordsItsCountOrNotFound.1 (line 153, ReconcileTypeAsync: the Some(n) arm returns `Ok(None)` instead of `Ok(Some(n))`; failed row: P7_AReconcileRecordsItsCountOrNotFound (correctness))

#### `PrivilegedRequestWorker.PollOnceAsync`

Worktree: Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs, T7 S3.

| Worktree file:line | Dafny lines | Note |
|---|---|---|
| :21-23, 29 intervals | 47-53 | TimeSpans as int ticks; PollInterval and ClaimBatchSize=16 not modelled (listed unbounded). |
| :30-31 Abandoned | 56 | |
| PrivilegedRequestContracts.cs:34 result record | 34 | Deviation: Message Option<seq<char>> (frozen P3 builds Some("<literal>")); Count Option<Int32>. |
| PrivilegedRequestRow.cs:6, 12-13 | 37, 59-60 | Req keeps Id, Kind. |
| :33, 56-58 _inFlight pruning | — | Not modelled (no repository/executor effect). |
| :34 _lastCleanup = MinValue | 89 | MinValue as 0 ticks. |
| :60-65 cleanup branch | 91-95 | Deviation: UtcNow read twice, modelled as one instant `now` (real _lastCleanup is the second read, ≥ now). |
| :62 FailAbandonedAsync | 93 | FailAbandoned effect. |
| :63 DeleteFinishedOlderThanAsync | 93 | DeleteFinished effect. |
| :67-73 list/claim loop, continue | 74-84 ClaimLoop, 96 | Deviation: repository calls modelled as not throwing (a throw ends the poll; RunWithRestartAsync restarts). |
| :72 Task.Run(RunClaimedAsync) | 83 | Deviation: runs inline in list order; concurrency not modelled. |
| :78-94 RunClaimedAsync | 66-71 | executor.RunAsync as total oracle (PrivilegedRequestExecutor.cs:45-59 catches every exception); CompleteAsync's caught exception still records Completed. |
| :85 status ternary | 70 | |
| :86 result.ToJson() | 70 | approved axiom. |

Helpers ClaimLoop_RanOnlyClaimed, ClaimLoop_CompletesClaimed, ClaimLoop_NoCleanup — proved.

G5:

- P1_OnlyClaimedRequestsRun: killed by P1_OnlyClaimedRequestsRun.1 (line 80: negated the claim guard, `if claims(request.Id) then` continue; failed row: ClaimLoop_RanOnlyClaimed (correctness), the helper P1 calls)
- P2_EachClaimedRunIsRecordedSucceededOnlyWhenOk: killed by P2_EachClaimedRunIsRecordedSucceededOnlyWhenOk.1 (line 70: flipped the status comparison, `if result.Code != OK then Succeeded else Failed`; failed row: ClaimLoop_CompletesClaimed (correctness), the helper P2 calls)
- P3_CleanupRunsAtMostHourly: killed by P3_CleanupRunsAtMostHourly.1 (line 92: flipped the cleanup comparison, `now - lastCleanup > CleanupInterval`; failed row: P3_CleanupRunsAtMostHourly (correctness))

#### `Program.PrivilegedAdminRoutes`

Worktree: wt/Iverson.Server/Iverson.Api/Program.cs, T8 S3.

| Worktree file:line | Code | Dafny lines | Note |
|---|---|---|---|
| :664-668 (3e) | SubmitAsync(ReconcileType, Serialize(payload), tenant claim, expiresAfter: null) | 66-67 | approved SerializeReconcilePayload; Submitted records kind and expiresAfter only; requestId an input. |
| :669-673 | WaitWhileAsync(..., Waits.Reconcile, RequestAborted) | 67 | int ticks; polling loop and its exceptions outside the model. |
| :674 | PrivilegedRequestResult.From | 68 | the `waited` input. |
| :676-680 | null → 202 {requestId} | 69-70 | audit outside range. |
| :681-682 | NotFound → 404 {error} | 71-72 | |
| :683-684 | Code != OK → 500 {error} | 73-74 | |
| :686-687 | Ok({reconciledCount, typeName}) | 75-76 | audit omitted. |
| :753 (3f) | GetUnreplayedSummaryAsync | 84 | `summary` input. |
| :754 | row null → 404 message with id | 85-86 | Guid.ToString() as ghost GuidToString by Hilbert choice (no trusted declaration). |
| :756-757 | tenant mismatch && !(null tenant && operator) → Forbid | 87-88 | ordinal string? != as Option equality. |
| :762-766 | SubmitAsync(ReplayDlq, ...) | 90-91 | approved SerializeReplayPayload. |
| :767-771 | WaitWhileAsync(Waits.Replay) | 91 | |
| :772 | From | 92 | |
| :774-778 | null → 202 | 93-94 | |
| :779-780 | NotFound → 404 | 95-96 | |
| :781-782 | != OK → 500 | 97-98 | |
| :784-786 | Ok({replayed, id, topic}) | 99-100 | constant replayed field omitted. |
| PrivilegedRequestContracts.cs:35 | result record | 33 | Message Option<seq<char>>, Count Option<Int32>. |
| PrivilegedRequestContracts.cs:10-11 | kinds | 49-50 | |

Deviations: routes are ghost functions (GuidToString ghost). Exceptions from SubmitAsync/WaitWhileAsync/From occur before `waited` exists, uncaught in range — outside every obligation. From returning null on "null" folds into waited = None. Out-of-enum numeric StatusCode not representable. 202 decision follows the code (ResultJson recorded or not).

G5:

- P1_AnUnfinishedRequestIsAnswered202WithItsId: killed by P1_AnUnfinishedRequestIsAnswered202WithItsId.1 (line 70: swapped the reconcile pending return `Accepted202(requestId)` for `Error500(None)`; failed row: P1_AnUnfinishedRequestIsAnswered202WithItsId (correctness))
- P2_AWorkerNotFoundIs404: killed by P2_AWorkerNotFoundIs404.1 (line 71: flipped the reconcile NotFound comparison, `reconcileResult.value.Code != NotFound`; failed row: P2_AWorkerNotFoundIs404 (correctness))
- P3_AnyOtherWorkerFailureIs500WithItsMessage: killed by P3_AnyOtherWorkerFailureIs500WithItsMessage.1 (line 74: swapped the reconcile failure return `Error500(...Message)` for `NotFound404(...Message)`; failed row: P3_AnyOtherWorkerFailureIs500WithItsMessage (correctness))
- P4_AFinishedRequestIs200: killed by P4_AFinishedRequestIs200.1 (line 73: flipped the reconcile OK comparison, `reconcileResult.value.Code == OK`; failed row: P4_AFinishedRequestIs200 (correctness))
- P5_TheReplayChecksRunBeforeAnythingIsQueued: killed by P5_TheReplayChecksRunBeforeAnythingIsQueued.1 (line 87: flipped the tenant comparison in the replay 403 guard, `row.value.TenantId == actingTenant`; failed row: P5_TheReplayChecksRunBeforeAnythingIsQueued (correctness))
- P6_AdminRequestsNeverExpire: killed by P6_AdminRequestsNeverExpire.1 (line 67: gave the reconcile submit an expiry, `Submitted(ReconcileTypeKind, Some(w.Reconcile))`; failed row: P6_AdminRequestsNeverExpire (correctness))

#### `Program.Health`

Worktree = dpv-replay/wt; Program.cs lines are post-plan.

| Worktree file:line | Introduced by | Dafny lines | Note |
|---|---|---|---|
| Iverson.Server/Iverson.StarRocks/EngagementHealthStatus.cs:3-… | existing | 9 (`SrStatus`) | Enum members map one to one. |
| Iverson.Server/Iverson.Api/ReadinessPolicy.cs:5 (`ReadinessResult`) | existing | 17 | |
| Iverson.Server/Iverson.Api/ReadinessPolicy.cs:14-33 (`Evaluate`) | existing | 20-31 | Same branch order: `!engagementEnabled` early return, then ready/fullyHealthy. The default parameter value `engagementEnabled = true` is irrelevant because the handler always passes it. |
| Program.cs:581-589 (handler parameters; `registry`, `storeRoles` at 587-588) | T9 S5 (587-588); rest existing | 51 (`Health` parameters) | DI services are replaced by their observed results: `db` → `pg`, `grants`; `storeRoles` → `role`; `registry.HasLoaded` → `registryLoaded`; `sr` → `sr`; `vector`, `kafka` → `qdrant`, `kafka` as bools; `engagementOptions.Value.Enabled` → `engagementEnabled`; `cache` → `cached`. |
| Program.cs:591-592 (cache hit returns `cached!`) | existing | 53-54 | `cached` is `Option<HealthResponse>` (the model note's 2 s cache input). |
| Program.cs:599 (`pgTask`: `IsCompletedSuccessfully && Result == 1`) | existing | 33-36, 56 | |
| Program.cs:600-607 comment, 607 (`grantsTask`: `IsCompletedSuccessfully && Result`) | T9 S5 | 39-42, 57 | The probe runs `ApiLoginGrants.ReadinessProbeSql` = `SELECT has_table_privilege('"<ReconciliationSchema.TableName>"', 'INSERT')` (ApiLoginGrants.cs:64-65): the two-argument form, which checks the connected login (the api login) instead of naming `'iverson_api'` as the spec text does. The SQL itself is outside the model; the probe's outcome is the `Probe<bool>` input. `QuerySingleOrDefaultAsync<bool>` returning default `false` for no row is covered by `Answered(false)`. |
| Program.cs:608 (`roleTask`: `IsCompletedSuccessfully`) | T9 S5 | 45-48, 58 | `Probe<()>`: a faulted `EnsureCanEnterRuntimeRoleAsync` is `Faulted`. |
| Program.cs:609-613 (`srTask`, `vectorTask`, `kafkaTask`, `Task.WhenAll`) | existing (613 edited by T9 S5 to add the two new tasks) | 59 and the `sr`, `qdrant`, `kafka` inputs | Per the model notes, StarRocks/Qdrant/Kafka are modelled as answering; their exceptions (which make `Task.WhenAll` throw) are not modelled. pg/grants/role tasks cannot fault after their `ContinueWith`, so they never make `WhenAll` throw (the continuation itself does not throw: it reads `Result` only after `IsCompletedSuccessfully`). |
| Program.cs:615-617 (`srStatus`, `engagementEnabled`, `registryLoaded`) | 617 by T9 S5 | 59, inputs | |
| Program.cs:618-627 (`checks` object) | T9 S5 (new fields) | not modelled | Response body only; it does not affect the status code or `FullyHealthy`. |
| Program.cs:629-631 (`ReadinessPolicy.Evaluate(pg && grants && role && registryLoaded, …)`) | T9 S5 | 60-62 | Same conjunction order. |
| Program.cs:633-635 (200 `Ok` vs 503 `Json`) | existing | 63-67 | `HealthResponse(200, readiness.FullyHealthy)` / `HealthResponse(503, false)`. The 503 body's status is always "degraded", so `FullyHealthy` is false there (a modelling choice; no lemma reads it). |
| Program.cs:637-638 (`cache.Set`, return) | existing | 67 | The cache write is not modelled (it affects later calls only). |

G5:

- P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry: killed by P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry.1 (line 61: the `&& registryLoaded` conjunct is removed from the postgres-healthy argument to `Evaluate`; failed row: P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry (correctness))
- P2_StarRocksAuthPendingKeepsTheApiReady: killed by P2_StarRocksAuthPendingKeepsTheApiReady.1 (line 27: `Evaluate`'s engagement-enabled readiness requires `starRocksStatus == Healthy` instead of `!= Unhealthy`, dropping the AuthPending tolerance; failed row: P2_StarRocksAuthPendingKeepsTheApiReady (correctness))

#### `ObjectRetrievalGrpcService.GetManyTenantGuard`

| Worktree file:line (introduced by) | Dafny lines | Notes |
|---|---|---|
| Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs:16-59 `AuthorizationDecision(bool Denied, ..., string? TenantColumn, string? TenantValue)` (pre-existing, context) | 17 | Only the three fields the range reads are modelled; `string?` becomes `Option<CsString>`. Other fields omitted (no header names them). |
| Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:152-181 `EntityAccess` record struct, `ForTenant(string?)` = `new(false, t)`, `CrossTenantMaintenance` = `new(true, null)` (pre-existing, context) | 19-22 | Record-struct value equality over (CrossTenant, TenantId): `ForTenant(t1) == ForTenant(t2)` iff `t1 == t2`, and `ForTenant(_) != CrossTenantMaintenance` because CrossTenant differs. The datatype's constructor equality matches this exactly. |
| ObjectRetrievalGrpcService.cs:115 `if (decision.TenantColumn is null)` (T9 S5) | 31-32 | Exact. |
| ObjectRetrievalGrpcService.cs:116 `throw new RpcException(new Status(StatusCode.Internal, "The type declares no tenant boundary."))` (T9 S5) | 33-34 | Exception as outcome: `Rpc(Internal, detail)` with no fetch access recorded. |
| ObjectRetrievalGrpcService.cs:118-121 `await _entities.FetchManyByKeysAsync(SchemaBuilder.ToTableSchema(schema), keys, EntityAccess.ForTenant(decision.TenantValue))` (T9 S5 replaced the former `TenantColumn is not null ? ForTenant(..) : CrossTenantMaintenance` ternary) | 35-37 | Only the `EntityAccess` argument is recorded (the second tuple component); table schema and keys do not bear on the properties and are not modelled. The fetch's own result/exceptions lie after the range and are not modelled; outcome `Proceeded`. |
| ObjectRetrievalGrpcService.cs:112-114 (comment, T9 S5) | 27-28 | Comment only. |

Deviations: (a) the range begins after the `decision.Denied` early return (lines 97-102) and the GUID validation loop (lines 104-108); per the model note the range is reached only with `!dec.Denied` and valid keys, so `GetManyRead` does not branch on `Denied`. P2 has no `requires`, so it is proved also for `Denied` decisions the real code never brings to this range; that is strictly stronger and harmless. (b) No other deviation.

G5:

- P1_ATenantlessDecisionIsRefusedBeforeTheRead: killed by P1_ATenantlessDecisionIsRefusedBeforeTheRead.1 (line 32: the guard is negated to `dec.TenantColumn.Some?`; failed row: P1_ATenantlessDecisionIsRefusedBeforeTheRead (correctness))
- P2_TheReadNeverRunsAcrossTenants: killed by P2_TheReadNeverRunsAcrossTenants.1 (line 37: the read passes `CrossTenantMaintenance` instead of `ForTenant(dec.TenantValue)`; failed row: P2_TheReadNeverRunsAcrossTenants (correctness))

#### `ObjectMappingGrpcService.DeleteTenantGuard`

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs:291-340 (pre-existing; owner/tenant mismatch from earlier rounds) | 16-19 (comment), not transliterated | Outside the range. Per the model notes the range is reached only after `decision.Denied \|\| ownerMismatch \|\| tenantMismatch` is false. `Decision` keeps `Denied` because the frozen P1 header reads it; `DeleteWrite` does not branch on it (the range does not). No `requires` added. |
| ObjectMappingGrpcService.cs:342-343 `targetStores`, `outboxRowId` (pre-existing) | none | Not read by the range's effects in a way the properties observe; the outbox row id is an opaque argument of `EnqueueDeleteOutboxRowAsync`, recorded as `OutboxDeleteRow`. |
| ObjectMappingGrpcService.cs:345-349 `if (decision.TenantColumn is null) throw new RpcException(new Status(StatusCode.Internal, "The type declares no tenant boundary."));` (T9 S5, new) | 37-39 | Thrown `RpcException` is an outcome: `Rpc(Internal, "The type declares no tenant boundary.")` with an empty effect list (the transaction is never entered). `decision.TenantColumn` (string?) is `Option<CsString>`; `is null` is `None?`. |
| ObjectMappingGrpcService.cs:351 `await _txRunner.ExecuteInTransactionAsync(async tx => {` (pre-existing) | 41, 45 (`TxBegun`) | Library boundary, modelled as the recorded effect `TxBegun`. |
| ObjectMappingGrpcService.cs:353-357 `_entities.DeleteAsync(tx, ToTableSchema(schema), request.Key, EntityAccess.ForTenant(decision.TenantValue))` (T9 S5: the former `TenantColumn is not null ? ForTenant(...) : CrossTenantMaintenance` ternary replaced by unconditional `ForTenant`) | 42, 45 (`EntityDelete(ForTenant(dec.TenantValue))`) | Library boundary, recorded by its access argument only. `ToTableSchema`, `tx` and `request.Key` are not read by the properties. `EntityAccess` keeps `CrossTenantMaintenance` as a constructor so P2 is non-vacuous over the type; the code no longer constructs it here. |
| ObjectMappingGrpcService.cs:359-364 `_outboxWriter.EnqueueDeleteOutboxRowAsync(tx, outboxRowId, request.TypeName, request.Key, rowJson)` (pre-existing) | 43, 45 (`OutboxDeleteRow`) | Library boundary, recorded as `OutboxDeleteRow`. |
| ObjectMappingGrpcService.cs:365 `});` | 44-45 (`Proceeded`) | Falling out of the range (to `PublishAsync` at :374) is `Proceeded`. A throw from inside the modelled library calls is not modelled (those calls are recorded effects, none axiomatised); neither property depends on it, since a throw only truncates the effect list. |

Deviations: none from the scan's model guidance. Datatypes `Decision`, `EntityAccess`, `GuardOutcome` taken verbatim from the GetManyTenantGuard model notes (which "as above" refers to in the scan, scan-draft.md:1016).

G5:

- P1_ATenantlessDecisionIsRefusedBeforeTheTransaction: killed by P1_ATenantlessDecisionIsRefusedBeforeTheTransaction.1 (line 38: negated the tenantless guard `dec.TenantColumn.None?` to `dec.TenantColumn.Some?`; failed row: P1_ATenantlessDecisionIsRefusedBeforeTheTransaction (correctness))
- P2_TheDeleteNeverRunsAcrossTenants: killed by P2_TheDeleteNeverRunsAcrossTenants.1 (line 45: replaced the delete's `EntityDelete(ForTenant(dec.TenantValue))` with `EntityDelete(CrossTenantMaintenance)`; failed row: P2_TheDeleteNeverRunsAcrossTenants (correctness))

#### `OutboxWriter.UpsertAndEnqueueOutboxAsync`

Worktree file: `Iverson.Server/Iverson.Sql/OutboxWriter.cs` (replayed worktree `dpv-replay/wt`).

| Worktree lines | Introduced by | Dafny lines | Note |
|---|---|---|---|
| `:18-19` signature `(TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null)` | pre-existing | 42 | `tenantId` (`string?`) becomes `Option<CsString>`. `payloadJson` becomes `payload: CsString`. `schema.TenantColumn` (`string?`, `IRecordStoreRoles.cs:350`) becomes `tenantColumn: Option<CsString>`. `schema.TableName`, `schema.KeyColumn`, `schema.Columns`, `typeName` and `key` are omitted, because no frozen header names them (see the deviation notes). |
| `:21-24` `ArgumentNullException.ThrowIfNull(tenantId);` | T9 S5 (5e) | 44-45 | The throw becomes the outcome `ArgumentNull`. It is the first statement, so nothing, including the transaction, runs before it. |
| `:26-32` `table`, `keyColumn`, `allCols`, `updateSet` (`PostgresIdentifier.Quote`) | pre-existing | 48-49 (comment only) | Not modelled, following the model note ("the SQL texts are built before the transaction and not read"). **Deviation:** `PostgresIdentifier.Quote` (`PostgresIdentifier.cs:30-37`) throws `ArgumentException` on a name that contains `"` or NUL. That throw happens before the transaction and is outside this model. It cannot make the upsert run outside the tenant scope, and it is not reachable for a null tenant (ThrowIfNull runs first). |
| `:34-47` `upsertSql`, `outboxSql` | pre-existing | 48-49 (comment only) | Not modelled (model note). `UpsertRow` and `OutboxInsert` carry no SQL text. |
| `:49` `Guid.CreateVersion7()` | pre-existing | 54 (comment) | The returned id is not modelled. `Written` stands for "returned outboxRowId". |
| `:55` `payloadJson = WithTenantColumn(payloadJson, schema.TenantColumn, tenantId);` | pre-existing (argument now non-null after T9 S5) | 50-51 | Calls the approved axiom `WithTenantColumnJson` (Dafny 29). `tenantId` is passed as a `CsString`, since ThrowIfNull has already run. **Deviation:** the axiom is total. The real `WithTenantColumn` (`:89-103`) can throw from `JsonNode.Parse` on malformed JSON when the schema has a tenant column. That throw happens before the transaction, so it fails closed and does not touch P1. Under the approved axiom, P2 does not cover the malformed-JSON input. |
| `:57` `await txRunner.ExecuteInTransactionAsync(async tx => { ... });` | pre-existing | 52-53 | The transaction runner is modelled as running its body once and returning normally. Database failures inside the body are not modelled. |
| `:59` `await tx.EnterTenantScopeAsync(tenantId);` | T9 S5 (5e): the `if (tenantId is not null)` guard was removed | 35 | `EnterTenantScope(tenantId)`. |
| `:60` `await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });` | pre-existing | 36 | `UpsertRow`. `payloadJson` is a parameter of `TransactionBody` but is not recorded (model note). |
| `:61` `await tx.ExitRoleScopeAsync();` | T9 S5 (5e): the `if (tenantId is not null)` guard was removed | 37 | `ExitRoleScope`. |
| `:62-68` `await tx.ExecuteAsync(outboxSql, new { Id, TypeName, EntityKey, EnqueuedAt });` | pre-existing | 38 | `OutboxInsert`. The parameter object is not modelled. |
| `:71` `return outboxRowId;` | pre-existing | 54-55 | `Written(steps)`. |
| `:89-103` `WithTenantColumn` | pre-existing | 28-29 | The approved axiom. It is not transliterated. |

Shared vocabulary used: `CsString` (Dafny 11) and `Option` (Dafny 13). Candidate datatypes `TxStep` (22) and `WriteOutcome` (26) follow the model note exactly. `TransactionBody` (33-39) is a helper with no contract, which keeps the lambda's four statements in order on separate lines.

G5:

- P1_ANullTenantIsRefusedBeforeTheTransaction: killed by P1_ANullTenantIsRefusedBeforeTheTransaction.1 (line 45: the null-tenant arm returns `Written([])` instead of `ArgumentNull`; failed row: P1_ANullTenantIsRefusedBeforeTheTransaction (correctness))
- P2_TheUpsertAlwaysRunsInsideTheTenantScope: killed by P2_TheUpsertAlwaysRunsInsideTheTenantScope.1 (line 35: dropped the `EnterTenantScope(tenantId)` step from the transaction body, `[EnterTenantScope(tenantId)]` to `[]`; failed row: P2_TheUpsertAlwaysRunsInsideTheTenantScope (correctness))

#### `EngagementRepository.TenantScopedAccess`

All worktree lines are in `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`. "pre" means the line already exists at the replay base and the plan leaves it unchanged.

| Worktree file:line | Task/step | Dafny lines | Note |
|---|---|---|---|
| :14-21 primary constructor (`connectionString`, `tenantLoginKey`, `provisionedTenants`) | T10 S3 (params 20-21) | 33 `Repo` | The model keeps only the fields these paths read. `provisionedTenants` becomes `HasDirectory`. |
| :57-59 `NoTenantLoginsMessage` | T10 S3 | 38 `InvalidOperation` | The message text is not modelled. Both throws use the same message. |
| :72-82 `TenantConnectionString` | T10 S3 | 20 (approved axiom) | |
| :84-86 `TenantConnectionStringFor` | T10 S3 | 81-85 | The `?? throw` becomes `Raise(InvalidOperation, false)`. |
| :90-92 `IsRecordedAsync` | T10 S3 | 91-95 | The directory's answer is the `recorded` parameter. A throw from `IsProvisionedAsync` itself is not modelled, because the directory is another candidate. |
| :100-122 `RunAsync` | pre (rewrap pre-existing) | 60-65 `DbRun`, 109-113 `RunAsyncRethrow`, 121-131 `RunTenantBody` | Deviation: this member is outside the candidate and is abstracted. The gate or an open circuit is `BeforeRunNotReady`, with no effects. The pipeline runs the operation once, as the guidance says. `RewrapIfBackendUnavailableAsync` becomes `rewrap: bool`, which can fire only for a MySql exception that is not the missing-table shape, as at :141. |
| :187-188, :193-203 `OpenTenantConnectionAsync` / `OpenOrSurfaceRefusalAsync` | T10 S3 / pre | 100-104 | AccessDenied (1045) becomes `NotReady`. Anything else is rethrown unchanged. |
| :277-298 `IsExpectedMissingResourceError` | pre / T10 S3 (comment only) | 73-76 | The message `Contains("Unknown table", OrdinalIgnoreCase)` becomes the `unknownTable` bit that every thrown `Outcome` carries (47). |
| :301-347 `RunTenantScopedAsync` | T10 S3 (SET ROLE removed; tenant login :312, :325-327) | 136-147 (+121-131) | Branch order is kept: re-validate (:308-309, `OtherEx`), then the key (:312), then RunAsync{new connection on the tenant string, open, operation}. The activity/telemetry branches (:313-315, :330, :332-345) change only the activity status and always rethrow, so they are not modelled. |
| :355-410 `StreamTenantScopedAsync` | T10 S3 (:362, :373-374, :377 changed) | 154-172 (+121-131) | Same order as above. The `catch … when (isExpectedMissingResource)` at :386-389 sets `reader` null, and the method then yields nothing (`Empty`). The other catch rethrows. Deviation: the iterator is modelled as enumerated to the end, and a read failure after the reader starts (`ReadRowAsync`, :401-404) is abstracted, because it adds no effect. `ReleaseAsync` adds no data statement. |
| :640-682 `SearchAsync` (tenant-scoped part :664-681) | T10 S3 (:670-671 added) | 181-198 `ScopedRead` | Order: tenant null/valid, then `BuildSearch` (`sqlBuilt`), then `IsRecordedAsync`, then `RunTenantScopedAsync` with the missing-table catch. |
| :684-754 `AggregateAsync` (:693-695 multi-key throw; else-branch :709-730) | pre / T10 S3 (:715-716) | 203-207 `AggregateScoped`, 181-198 | Deviation: the multi-key `GROUP BY` throw (:693-695) comes before the tenant check, and its input is not in `TenantOp`'s signature. It is the `aggregateSingleKey` parameter of `TenantOpFull`, which `TenantOp` fixes to `true`. Bucket and metric decoding (:732-753) is abstracted to `Rows`. |
| :756-790 `GroupByAsync` (:770-789) | T10 S3 (:778-779) | 181-198 | |
| :792-831 `PipelineAsync` (:802-830) | T10 S3 (:813-814) | 181-198 | `MaskPipelineRows` (:833-) does not connect and is abstracted to `Rows`. |
| :461-478 `MatchRowsAsync` | T10 S3 (:473-474) | 216-225 `MatchRowsScoped` (+154-172) | |
| :480-507 `UpsertAsync` (:486, :488 `UpsertEntries` from T3 S3, :490, :503-506) | T10 S3 / T3 S3 (:488) | 234-242 `UpsertScoped` | `sqlBuilt == false` covers a throw from `UpsertEntries` (:488) or `EnsureAllPlaceholdersBound` (:505). Deviation: `entries.Count == 0` (:490) is the `upsertHasEntries` parameter of `TenantOpFull`, which `TenantOp` fixes to `true`. `UpsertAsync` never calls `IsRecordedAsync`, as the code has it. |
| :538-550 `DeleteAsync` | T10 S3 (:542-544 added) | 250-261 `DeleteScoped` | Order: valid, then `IsRecordedAsync` (no directory faults, unrecorded returns), then the SQL build and guard (`sqlBuilt`), then `RunTenantScopedAsync`. A plain return is `Empty`. |
| (dispatcher) | n/a | 264-275 `TenantOpFull`, 279-282 `TenantOp` | `TenantOp` = `TenantOpFull(…, aggregateSingleKey := true, upsertHasEntries := true, db := QueryOk)`. |
| (proof) | n/a | 288-348 | Helpers, each proved. `TenantOpFullOwnLogin` proves P4 for every aggregate shape, every payload and every `DbRun`. `P1toP3ForEveryDbRun` proves P1 to P3 for every `DbRun`. `ExcludedInputsDoNotConnect` shows that the inputs `TenantOp` fixes never connect: a multi-key Aggregate is `(Fault(OtherEx), [])` for every repository, and an Upsert with no known column is `(Empty, [])`. No `assume`, `expect`, `{:axiom}` or `include` is used, and no trusted declaration besides the two approved axioms. |

Deviations, collected:
1. `Result<T>` is replaced by `Outcome<T> = Ret | Raise(ex, unknownTable)`. The headers need the exception kind, and the missing-table catch needs the message bit, and the shared `Threw` carries neither.
2. `RunAsync`, the readiness gate, the breaker, the retry and the liveness rewrap are abstracted into `DbRun`, with one run of the operation (per the model notes).
3. Two inputs that are missing from `TenantOp`'s signature are explicit parameters of `TenantOpFull`, fixed in `TenantOp`:
   - **Multi-key Aggregate** (`singleKey = false`): with this input, P1 and P2 do not hold as stated. The call throws `EngagementQueryTranslationException` for every tenant and repository, before anything else, and still connects to nothing. This is a request rejection that existed before the plan, not a tenant-access defect.
   - **Upsert whose payload names no column** (`hasEntries = false`): with this input, P3 does not hold as stated. The call returns without refusing, but it also connects to nothing.

   Neither case is attributed to the code.
4. Stream enumeration is modelled to the end. Failures in mid-stream reads and in post-processing (aggregate decoding, pipeline masking) are abstracted, because they add no data effect.

Controller G4 note (verbatim from the return): P1–P3 are proved for TenantOp, which fixes aggregateSingleKey = true and upsertHasEntries = true (inputs absent from the approved header signature). For the two excluded inputs (a multi-key Aggregate, rejected by translation before any tenant check; an Upsert naming no known column, a no-op), the proved helper ExcludedInputsDoNotConnect shows no data connection is opened; P4 is proved over TenantOpFull for every input shape. Judged justified; P1–P3's scope is narrowed accordingly.

G5:

- P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting: killed by P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting.1 (line 188: negated ScopedRead's unrecorded-tenant guard `else if !rec.value then` to `else if rec.value then`; failed row: P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting (correctness))
- P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting: killed by P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting.1 (line 93: negated IsRecorded's missing-directory guard `if !repo.HasDirectory then` to `if repo.HasDirectory then`; failed row: P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting (correctness))
- P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting: killed by P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting.1 (line 83: TenantConnectionStringFor's missing-key arm returns `Ret(repo.BaseCs)` instead of `Raise(InvalidOperation, false)`; failed row: P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting (correctness))
- P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange: killed by P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange.1 (line 146: RunTenantScoped runs the body on the base login, `RunTenantBody(csr.value, db)` to `RunTenantBody(repo.BaseCs, db)`; failed row: RunTenantScopedOwnLogin (correctness), a helper P4's proof calls via TenantOpFullOwnLogin)

#### `EngagementRepository.OpenOrSurfaceRefusalAsync`

Worktree file: Iverson.Server/Iverson.StarRocks/EngagementRepository.cs (all ranges introduced or reshaped by T10 S3).

| Worktree lines | Task/step | Dafny lines | Note |
|---|---|---|---|
| (MySqlConnector) `MySqlErrorCode.AccessDenied` | external | 13 | `const AccessDenied: int := 1045` (ER_ACCESS_DENIED_ERROR); the enum's underlying int. |
| exception kinds used at 196-202 | T10 S3 | 15-24 | `OpenExn` / `OpenStep` as in the model notes. **Deviation:** `NotReady`'s first field is named `notReadyMessage`, not `message`: Dafny rejects one destructor name with two types across constructors (`MySqlEx.message: CsString` vs `NotReady.message: seq<char>`). No frozen header names that field; constructors are positional in the headers. |
| 193 `private static async Task OpenOrSurfaceRefusalAsync(Func<Task> open, string refusal)` | T10 S3 | 30-31 | `open` abstracted to the outcome of `await open()`: a synchronous throw from `open()` and a faulted Task are both inside the `try`, so both are `OpenThrew(e)`. `refusal: string` → `CsString`. |
| 195-197 `try { await open().ConfigureAwait(false); }` (success) | T10 S3 | 33-34 | `Opened` → `Opened`. |
| 199 `catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)` | T10 S3 (moved from round 11's OpenProvisionerConnectionAsync) | 35-36 | |
| 201 `throw new EngagementNotReadyException(refusal, ex);` | T10 S3 | 37-38 | `NotReady(refusal, open.e)`: message is `refusal`, inner is the same exception. |
| uncaught exceptions (filter false / non-MySqlException) | T10 S3 | 39-40 | propagate unchanged: `open`. |
| 179-180 `OpenProvisionerConnectionAsync(open) => OpenOrSurfaceRefusalAsync(open, "StarRocks refused the provisioner login; it may not have been created yet.")` | T10 S3 | 50-57, helper 59-65 | Literal bound to a local and a proved helper lemma (`ProvisionerRefusalIsACsString`) discharges the `CsString` subset check (Z3 did not unfold the 74-unit literal's length unaided). Proof repair only; value unchanged. |
| 190-191 `OpenTenantConnectionAsync(open) => OpenOrSurfaceRefusalAsync(open, "StarRocks refused the tenant's login.")` | T10 S3 | 43-48 | |
| — | — | 67-90 | frozen property lemmas P1 (72-76), P2 (79-83), P3 (86-90), copied verbatim; bodies empty. |

Sanity mutants (scratch, deleted): dropping `&& open.e.code == AccessDenied` kills P2; wrapping `OtherOpenEx` instead of `open.e` kills P1 and P3.

G5:

- P1_ARefusedTenantLoginSurfacesAsNotReady: killed by P1_ARefusedTenantLoginSurfacesAsNotReady.1 (line 36: the catch filter is flipped to `open.e.code != AccessDenied`; failed row: P1_ARefusedTenantLoginSurfacesAsNotReady (correctness))
- P2_EveryOtherOpenOutcomeIsUnchanged: killed by P2_EveryOtherOpenOutcomeIsUnchanged.1 (line 36: the catch filter is widened to `open.e.code >= AccessDenied`, so other MySql codes above 1045 are wrapped as NotReady; failed row: P2_EveryOtherOpenOutcomeIsUnchanged (correctness))
- P3_ARefusedProvisionerLoginStillSurfacesAsNotReady: killed by P3_ARefusedProvisionerLoginStillSurfacesAsNotReady.1 (line 56: `OpenProvisionerConnection` passes the tenant refusal literal "StarRocks refused the tenant's login." instead of `refusal`; failed row: P3_ARefusedProvisionerLoginStillSurfacesAsNotReady (correctness))

#### `EngagementRepository.IsExpectedMissingResourceError`

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| (C# `string`, signature-forced) | 7 | `CsString`, shared vocabulary; `Length` is an `int`. |
| EngagementRepository.cs:290 `Contains(..., StringComparison.OrdinalIgnoreCase)` (T10 S3) | 10 | Approved axiom `ContainsIgnoreCase`, copied verbatim; bodiless (library boundary). |
| EngagementRepository.cs:287 parameter `Exception ex` (T10 S3) | 14 | `datatype Ex = MySqlEx(code: int, message: CsString) \| OtherEx`. Deviation (modelling only): `MySqlException.ErrorCode` is the `MySqlErrorCode` enum, modelled as its underlying `int` value per the brief's model notes; `Exception.Message` is non-nullable `string`, so no `Option`. Every non-MySqlException is collapsed to `OtherEx`, which is sound because the predicate only inspects the `is MySqlException` test. |
| EngagementRepository.cs:287 `private static bool IsExpectedMissingResourceError(Exception ex) =>` (T10 S3) | 21 | `predicate IsExpectedMissingResource(e: Ex)` (name from model notes). |
| EngagementRepository.cs:288 `ex is MySqlException mex &&` (T10 S3) | 23 | `e.MySqlEx? &&` |
| EngagementRepository.cs:289 `mex.ErrorCode == (MySqlErrorCode)5502 &&` (T10 S3) | 24 | `e.code == 5502 &&` |
| EngagementRepository.cs:290 `mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase);` (T10 S3) | 25 | `ContainsIgnoreCase(e.message, "Unknown table")`; same left-to-right `&&` order, no other arm (the role arm is gone in the post-plan file). |
| (property, frozen) | 29-33 | P1 header/clauses verbatim. |
| (property, frozen) | 36-40 | P2 header/clauses verbatim. |

No exceptions are thrown on any path of this expression-bodied member (short-circuit `&&` guards the `mex` accesses; `Message` is non-null), so there is no `Result` outcome to model.

G5:

- P1_AnUnknownTableIsStillAnExpectedEmptyTenant: killed by P1_AnUnknownTableIsStillAnExpectedEmptyTenant.1 (line 24: the error-code conjunct is flipped to `e.code != 5502`; failed row: P1_AnUnknownTableIsStillAnExpectedEmptyTenant (correctness))
- P2_NothingElseIsAnExpectedEmptyTenant: killed by P2_NothingElseIsAnExpectedEmptyTenant.1 (line 24: the `e.code == 5502` conjunct is removed (replaced by `true`), so any MySqlException code with "Unknown table" qualifies; failed row: P2_NothingElseIsAnExpectedEmptyTenant (correctness))

#### `EngagementRepository.EnsureTenantProvisionedAsync`

Worktree file: `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs`. "pre-plan" means the line is in the replay base `c79d7de0`, and "T10 S3" means T10 S3 introduced it (from `git diff HEAD` on the replayed worktree).

| Worktree lines | Introduced by | Dafny lines | Note |
|---|---|---|---|
| 14-21 ctor params `provisionerConnectionString`, `tenantLoginKey`, `provisionedTenants` | pre-plan / T10 (key, directory) | 25 `ProvRepo` | Each nullable becomes `Option` or a presence bool. The brief's model note says only the directory's presence matters. |
| 53-54 `TenantSessionProperties` | T10 (earlier step) | 63-64 const | Copied verbatim. |
| TenantIdentifier.cs:138-139 `Qualify` | pre-plan | 56-60 | Transliterated. |
| 552 signature | pre-plan | 115 `Provision` | `EngagementTableSchema` becomes `EngTable(TableName)`, the only field read outside opaque calls. |
| 554 `if (!IsValid) return;` | pre-plan | 117 | `IsValidTenant` (axiom). A normal return becomes `(Provisioned, [])`. |
| 559-561 provisioner null, throw IOE | pre-plan | 118 | `ProvFault(InvalidOperation)`, no effects. |
| 565 `provisionedTenants ?? throw` | T10 S3 | 119 | The same order as the code: the directory is checked before the key. |
| 566-567 `tenantLoginKey ?? throw`, `LoginPassword` | T10 S3 | 120, 122 | |
| 568 `LoginName` | T10 S3 | 123 | Axiom. |
| 570-571 `DatabaseName`, `RoleName` | pre-plan | 124-125 | Axioms. |
| 572 `Qualify` | pre-plan | 126 | |
| 573 `BuildCreateTableDdl` | pre-plan | 129 | **Deviation:** the axiom `CreateTableDdl` takes a `CsString`, and the model's concatenations are unbounded `seq<char>` (shared vocabulary). So the DDL is computed only when the qualified name fits, and it feeds the opaque `CreatedTable` either way. Two cases are not modelled: the OutOfMemory on a qualified name of 2^31 or more characters, and any throw from `BuildCreateTableDdl` itself before connecting. |
| 575-577, 637 telemetry `activity` | pre-plan | — | Omitted; it has no effect on the outcome or the effects. |
| 579, 633 `RunAsync(...)` | pre-plan | 130, 101-111 `ProvisionBody` | Modelled as running the body once, per the brief. The readiness-gate throw, the Polly retries (which would repeat effects) and the rewrap to `EngagementNotReadyException` are not modelled. Any body throw becomes `ProvFault(OtherEx)` (131), standing for "whatever the statement threw, possibly rewrapped". No property constrains the kind of that fault. |
| 581-582 `CreateProvisionerConnection` + `OpenProvisionerConnectionAsync` | pre-plan | 77 `OpenedProvisioner` (index 0) | |
| 583 `SET ROLE user_admin` | pre-plan | 78 (index 1) | Comes before `try`, so a throw here skips `finally` (105). |
| 586-589 CREATE DATABASE, CREATE ROLE, GRANT SELECT…, GRANT CREATE TABLE | pre-plan | 84-87 (indices 2-5) | |
| (removed) `GRANT {role} TO USER 'iverson_app'@'%'` | removed by T10 S3 | — | Absent, as the code now has it. |
| 594-595 CREATE USER … DEFAULT ROLE | T10 S3 | 88 (index 6) | The interpolation is expanded to `+` concatenation. |
| 596 ALTER USER … SET PROPERTIES ({TenantSessionProperties}) | T10 S3 | 89 (index 7) | Keeps the const reference. The proof bridges it to the property's literal in `SessionPropertiesTail` (152). |
| 604-605 GRANT role / GRANT ALTER to iverson_provisioner | pre-plan | 90-91 (indices 8-9) | |
| 617 `SET ROLE user_admin, role` | pre-plan | 92 (index 10) | |
| 618 `ExecuteAsync(createTableDdl)` | pre-plan | 93 `CreatedTable` (index 11) | Opaque, per the brief. |
| 619 `WidenLargeTextColumnsAsync` | pre-plan | 94 `Widened` (index 12) | One effect, per the brief. |
| 620 `return true` | pre-plan | 110 `Ok(true)` | |
| 622-632 `finally { try SET ROLE NONE catch log }` | pre-plan | 98 `FinallyBlock`, 108 | Runs after the try both on success and on a throw inside the try. Its own throw is swallowed, so it has no `failAt` index. |
| 636 `directory.RecordProvisionedAsync` | T10 S3 | 133 `Recorded(tenantId)`; 132 | **Deviation (indexing):** `failAt` indexes 0-12 as above, and RecordProvisionedAsync is index 13. Any `failAt` of 13 or more means the record throws, so every `Some(n)` is a failure, as the brief's "`None` means every statement succeeds" requires. |
| — | — | 68-72 `RunStatements` | Modelling helper: a statement that throws does not record its own effect, and only the statements before it took effect. |

G5:

- P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds: killed by P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds.1 (line 132, Provision: negated the record-failure guard `else if failAt.Some? then` to `else if failAt.None? then`, so a failed RecordProvisionedAsync records the tenant and a clean run faults; failed row: P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds (correctness))
- P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting: killed by P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting.1 (line 119, Provision: negated the directory guard `else if !repo.HasDirectory then` to `else if repo.HasDirectory then`, so a missing directory with a key present proceeds to connect; failed row: P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting (correctness))
- P3_TheTenantLoginReplacesTheAppGrant: killed by P3_TheTenantLoginReplacesTheAppGrant.1 (line 90, TryBlock: the role grant's grantee `'iverson_provisioner'@'%'` swapped to `'iverson_app'@'%'`, restoring the removed app grant; failed row: P3_TheTenantLoginReplacesTheAppGrant (correctness))

#### `PostgresProvisionedTenantDirectory.IsProvisionedAsync`

Worktree file Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs; all lines from T10 S3.

| Worktree lines | Dafny lines | Note |
|---|---|---|
| :16 `_provisioned` ConcurrentDictionary<string,byte>(StringComparer.Ordinal) | 12-13, `cache: set<CsString>` | Modelled as the set of its keys. Ordinal comparison is `seq<char>` equality, and the byte value (always 0) is never read. Concurrency is not modelled: the model covers one call at a time. `TryAdd` on a key that is already there is a no-op, which matches set union. |
| :18 signature `IsProvisionedAsync(string tenantId)` | 18 (`IsProvisionedOutcome`), 30 (`IsProvisioned`) | `tenantId` is a non-nullable `string`, so it becomes `CsString` with no `requires`. A null argument, which would make `ContainsKey` throw, is ruled out by the signature. |
| :20 `if (ContainsKey) return true;` | 20 | Early return. The cache is unchanged and Postgres is not asked (`.2 == false`). |
| :22-24 `await sql.QuerySingleOrDefaultAsync<bool>(SELECT EXISTS ...)` | 22-24 | The external call is modelled as an input, `query: Result<bool>`. The `Threw` outcome propagates with the cache untouched and Postgres asked. The SQL text is not modelled, because no property names it. |
| :25 `if (found) _provisioned.TryAdd(tenantId, 0);` | 25 | |
| :26 `return found;` | 26 | |
| (none) | 30-34 `IsProvisioned` | Deviation, as the model notes require. The frozen headers take `found: bool` and read a plain tuple, so `IsProvisioned` is the non-throwing projection of `IsProvisionedOutcome` with `Ok(found)`. The throwing path stays modelled in `IsProvisionedOutcome`. |
| :29 signature `RecordProvisionedAsync(string tenantId)` | 37 (`RecordOutcome`), 45 (`Record`) | |
| :31-37 `await sql.ExecuteAsync(INSERT ... ON CONFLICT DO NOTHING)` | 39-40 | The external call is modelled as an input, `exec: Result<()>`. `Threw` propagates with the cache untouched. Idempotence on the database side is not modelled: no property is about database state. |
| :38 `_provisioned.TryAdd(tenantId, 0);` | 41 | |
| (none) | 45-48 `Record` | Deviation: the non-throwing projection (`Ok(())`) that the frozen P2 header calls. |

No axioms were used, and none were approved ("none").

G5:

- P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain: killed by P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain.1 (line 25: negated the cache-on-found guard `if found then` to `if !found then`; failed row: P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain (correctness))
- P2_RecordingCachesTheTenant: killed by P2_RecordingCachesTheTenant.1 (line 41: dropped the TryAdd on a successful insert, `(Ok(()), cache + {t})` to `(Ok(()), cache)`; failed row: P2_RecordingCachesTheTenant (correctness))

#### `StarRocksResiliencePipelineFactory.IsBackendTransient`

| Worktree file:line (task/step) | Dafny lines | Note |
|---|---|---|
| Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs (MySqlException type, MySqlConnector library) | 9-10 | `datatype MySqlEx(code: int, message: CsString)`: `ErrorCode` as its int value, `Message` as a non-nullable C# string. Only the two fields the code reads are modelled. |
| StarRocksResiliencePipelineFactory.cs:53 `MySqlErrorCode.UnableToConnectToHost` (T11 S3) | 12-13 | `const UnableToConnectToHost: int := 1042` (MySqlConnector enum value). |
| `MySqlException.IsTransient` (library property, read at :47) | 15-16 | Approved axiom `predicate IsTransient(e: MySqlEx)`, copied verbatim, bodiless. |
| :54 `string.Contains(..., StringComparison.Ordinal)` | 18-22 | `ContainsOrdinal` per the model notes. **Deviation:** an explicit `{:trigger s[i..i + \|sub\|]}` is added to the existential; without it Dafny 4.11 emits a "could not find a trigger" warning and `dafny verify` exits 2 ("warnings were found and --allow-warnings is false"). The meaning is unchanged. |
| :52-54 `IsPoolExhausted` (T11 S3) | 24-32 | Same structure: `ErrorCode == UnableToConnectToHost && Message.Contains(...)`. No throwing path, since `Message` is non-null and `&&` short-circuits. |
| :47 `IsBackendTransient` (T11 S3) | 34-39 | `IsTransient(e) && !IsPoolExhausted(e)`, same operand order. |
| :37 retry `ShouldHandle = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient)` (T11 S3) | 41-47 | `RetryHandles(e) := IsBackendTransient(e)`. This models the predicate applied to an outcome whose exception is a `MySqlException`. Non-MySqlException outcomes are not handled, and they are outside the frozen headers (which take `MySqlEx`). |
| :19 breaker `ShouldHandle = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient)` (T11 S3) | 49-54 | `BreakerHandles(e) := IsBackendTransient(e)`, the same modelling as the retry. |
| (frozen properties) | 56-72 | P1, P2 and P3, with headers and clauses copied verbatim; each body is empty. |

G5:

- P1_AFullPoolIsNotTransient: killed by P1_AFullPoolIsNotTransient.1 (line 38: removed the `&& !IsPoolExhausted(e)` conjunct from IsBackendTransient, leaving `IsTransient(e)`; failed row: P1_AFullPoolIsNotTransient (correctness))
- P2_EveryOtherFailureKeepsItsTransientClassification: killed by P2_EveryOtherFailureKeepsItsTransientClassification.1 (line 30: removed the `e.code == UnableToConnectToHost &&` conjunct from IsPoolExhausted, so any message match excludes; failed row: P2_EveryOtherFailureKeepsItsTransientClassification (correctness))
- P3_OnePredicateFeedsTheRetryAndTheBreaker: killed by P3_OnePredicateFeedsTheRetryAndTheBreaker.1 (line 46: RetryHandles uses `IsTransient(e)` instead of `IsBackendTransient(e)`; failed row: P3_OnePredicateFeedsTheRetryAndTheBreaker (correctness))

### 0.5 Obligations

| Candidate | Obligation | Citation | Status |
|---|---|---|---|
| `SchemaBuilder.NormalizeDeclaredNames` | P1_OwnerAndFieldPermissionNamesTakeTheColumnsSpelling | "Fix: `SchemaBuilder.BuildDescriptor` rewrites these to the matched column's spelling." (spec §2.2) and "descriptor.Authorization!.OwnerField.Should().Be("OwnerId");" (plan T1 S1a, `BuildDescriptor_RewritesDeclaredNamesToTheMatchedColumnsSpelling`) | proved |
|  | P2_OnlyNonOneToManyForeignKeysAreRewritten | "For relations, only the foreign keys of `OneToOne`, `ManyToOne` and `ManyToMany` relations are rewritten: a `OneToMany` foreign key names a column of the related type, not of this one." (spec §2.2) | proved |
|  | P3_NormalizationNeverFails | "A stored type that already has them stays loaded, but a payload key that matches more than one column is refused." (spec, Decisions 7) and "registry.IsRegistered("Note").Should().BeTrue();" (plan T1 S1a, `LoadAsync_ANameMatchingCaseDuplicateColumns_IsLeftAsDeclared_AndTheTypeStillLoads`) | proved |
| `SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties` | P1_PropertiesEqualIgnoringCaseAreRefused | "`RegisterSchema` refuses with `InvalidArgument`:" / "two properties, or the key and a property, whose names differ only by case;" (spec §2.4) | proved |
| `SchemaRegistrationOrchestrator.NavigationColumnCheck` | P1_ANavigationNamedLikeAnyColumnIsRefused | "`RegisterSchema` refuses with `InvalidArgument`:" / "a relation navigation property whose name matches any column case-insensitively." (spec §2.4) | proved |
| `PayloadKeyCanonicalizer.Canonicalize` | P1_AKeyNamingOneColumnTakesItsSpelling | "It renames each payload key that matches exactly one column to that column's spelling." (spec §2.1) | proved |
|  | P2_TwoKeysNamingOneColumnAreRefused | "It refuses with `InvalidArgument` two payload keys that map to the same column, and a key that matches more than one column (a stored type with case-duplicate columns)." (spec §2.1) | proved |
|  | P3_AKeyMatchingSeveralColumnsIsRefused | "It refuses with `InvalidArgument` two payload keys that map to the same column, and a key that matches more than one column (a stored type with case-duplicate columns)." (spec §2.1) | proved |
|  | P4_KeysNamingNoColumnAndTheTenantKeyAreLeftAlone | "Keys that match no column are left alone (unchanged behaviour)." (spec §2.1) and "The reserved tenant column `__TenantId` is excluded: its smuggling check (`AuthorizationFieldMasking.cs:91-98`) keeps running on the raw key and keeps its message." (spec §2.1) | proved |
|  | P5_NothingElseIsRefused | "It refuses with `InvalidArgument` two payload keys that map to the same column, and a key that matches more than one column (a stored type with case-duplicate columns)." (spec §2.1) and "Keys that match no column are left alone (unchanged behaviour)." (spec §2.1) | proved |
| `StructSerializer.FoldKeys` | P1_ExactColumnKeysKeepTheirSpelling_OtherTopLevelKeysAreUpperFirsted | "`ProtoPayloadHelper.FoldKeys` stops upper-casing keys that already equal a column name; other keys keep `UpperFirst`." (spec §2.3) | proved |
|  | P2_NestedKeysAreAlwaysUpperFirsted | "row.GetProperty("Meta").GetProperty("Url").GetString().Should().Be("nested");" (plan T2 S1, `Post_KeysThatAreNotColumns_AreStillUpperFirstedAtEveryLevel`, generalised) | proved |
| `EngagementRepository.UpsertEntries` | P1_OnlyExactColumnKeysWithNonObjectValuesAreSelected | "`EngagementRepository.UpsertAsync` selects entries by exact column name (ordinal)." (spec §2.3) and "entries.Select(e => e.Key).Should().Equal("Id");" (plan T3 S1a, `AKeyThatMatchesAColumnOnlyIgnoringCase_IsNotSelected`) | proved |
| `EngagementStoreConsumer.WithOwnerValue` | P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey | "`EngagementStoreConsumer.WithOwnerValue` removes every case variant of the owner key, then writes the authoritative value under the column's spelling, as `OutboxWriter.WithTenantColumn` already does." (spec §2.3) | proved |
|  | P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains | "Fail closed: with no authoritative owner, no spelling of the owner key may reach StarRocks." (plan T3 S1b, the comment heading `HandleUpsert_WithReCasedOwnerKeysAndNoAuthoritativeOwner_UpsertsNoOwnerKeyAtAll`, whose assertion on the case-insensitive owner keys ends ".Should().BeEmpty();") | proved |
| `PopularitySignalEligibility.IsEligible` | P1_NotOneToManyOrMissingChild_IsNeverEligible | "A missing child or a relation that isn't `OneToMany` makes the signal ineligible, even when `parent.Authorization` is null: that structural check comes first." (spec §3.1) | proved |
|  | P2_EligibleExactlyWhenEveryParentReaderReadsEveryCountedRow | "Let `PR(X)` be the roles with `CanReadAll` in `X.Authorization.RowPermissions`, compared ordinally. The predicate is true when either:" / "`parent.Authorization` is null (nobody can read the parent, so nothing leaks); or" / "all of the following hold:" / "`parent.Authorization.OwnerField` is null;" / "the child, resolved through the registry from the `OneToMany` relation, has non-null `Authorization`;" / "`PR(parent) ⊆ PR(child)`;" / "every `FieldPermission` of the child on the FK column or on `PopularitySignalColumn` with non-empty `ReadableRoles` satisfies `PR(parent) ⊆ ReadableRoles`." (spec §3.1; " / " separates source lines) | proved |
|  | P3_AnUnregisteredChildIsNeverEligible | "the child, resolved through the registry from the `OneToMany` relation, has non-null `Authorization`;" (spec §3.1) and "PopularitySignalEligibility.IsEligible(Article(ReadableBy("readers")), Comments, registry).Should().BeFalse();" (plan T4 S3, `IsEligible_ChildNotRegistered_ReturnsFalse`) | proved |
| `ObjectSearchGrpcService.EligiblePopularitySignal` | P1_OnlyAnEligibleSignalIsRankedOn | "`ObjectSearchGrpcService` skips ineligible signals when ranking (`PopularityFor`, `RetrievePopularityOrDegradeAsync`)." (spec §3.1) | proved |
| `ObjectSearchGrpcService.ReadableDecayField` | P1_ADeniedDecayColumnGivesNoDecay | "the decay field is set to null when `decision.AllowedFields` is non-null and does not contain the decay column (case-insensitive, as `AllowedFields` already is)." (spec §3.2) | proved |
|  | P2_AReadableOrUnrestrictedDecayFieldIsKept | "the decay field is set to null when `decision.AllowedFields` is non-null and does not contain the decay column (case-insensitive, as `AllowedFields` already is)." (spec §3.2) | proved |
| `ObjectSearchGrpcService.SimilarResponseAllowList` | P1_ResponsesCarryOnlyDescriptorColumns | "SearchSimilar builds its response objects from an **allow-list**: the descriptor's columns plus `key`. Every other Qdrant payload key is dropped." (spec §3.1) | proved |
|  | P2_ThePayloadKeyIsAnsweredAsTheKeyColumn | "the descriptor's columns plus `key`" (spec §3.1) and "written[0].Data.Fields.Keys.Should().BeEquivalentTo(["Id", "Title"]);" (plan T4 S1b, `SearchSimilar_StreamsOnlyDescriptorColumnsAndTheKey`) | proved |
| `PopularitySignalValidator.SignalKeyCollision` | P1_ASignalKeyCollidingWithAParentColumnIsRefused | "It also refuses a configured signal whose key (`{relation camelCase}Count` or `…CountBuckets`) case-insensitively matches a parent column." (spec §3.1) | proved |
| `EnrichmentTargetRules.Violation` | P1_AdmittedTargetsAreTextOrTextArrayColumns | "The whole rule set in `SchemaRegistrationOrchestrator.ValidateEnrichmentTargets` (target types, sources, write restrictions and read-role subsets) is restated as a pure predicate over `SchemaDescriptor`: TEXT and TEXT[] targets, with the synthetic `Document` excluded." (spec §4) | proved |
|  | P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner | "The whole rule set in `SchemaRegistrationOrchestrator.ValidateEnrichmentTargets` (target types, sources, write restrictions and read-role subsets) is restated as a pure predicate over `SchemaDescriptor`" (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, `LoadAsync_DescriptorBreakingAnEnrichmentRule_LoadsWithNoTargetsAndLogsError`, cases "target-is-the-key" and "target-is-the-tenant-column") | proved |
|  | P3_AdmittedTargetsHaveASourceAndAreNotSources | "TEXT and TEXT[] targets, with the synthetic `Document` excluded." (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, cases "target-is-a-source" and "only-source-is-the-synthetic-document") | proved |
|  | P4_AdmittedExtractedTargetsHaveAHint | "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, case "extracted-no-hint") | proved |
|  | P5_NoAdmittedTargetIsWriteRestricted | "(target types, sources, write restrictions and read-role subsets)" (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, case "target-write-restricted") | proved |
|  | P6_EveryTargetReaderCanReadEverySource | "(target types, sources, write restrictions and read-role subsets)" (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, case "target-more-readable-than-its-source") | proved |
|  | P7_ADescriptorKeepingEveryRuleIsAdmitted | "_sut.Get("Article")!.EnrichmentTargets.Should().BeEquivalentTo(stored.EnrichmentTargets);" (plan T5 S1a, `LoadAsync_DescriptorKeepingTheEnrichmentRules_KeepsItsTargets`, cases "text", "text-array" and "template-beside-a-real-source", generalised) and "TEXT and TEXT[] targets, with the synthetic `Document` excluded." (spec §4) | proved |
| `SchemaRegistryRepository.OwnerQueries` | P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds | "**Owner (#7).** It reads the stored owner from `_iverson_schema`: `schema_json->>'ownerTenantId'`, matched with `lower(type_name) = lower(@TypeName)` (the registry is case-insensitive; the column isn't)." (spec §5.3) | proved |
|  | P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner | "**Registry write.** `INSERT … ON CONFLICT (type_name) DO UPDATE … WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner`. Zero rows fails with `PermissionDenied`." (spec §5.3) | proved |
|  | P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable | "**Adoption (#1).** This applies when the type has no registry row but its derived table exists in `public`." (spec §5.3) and "StatusCode.AlreadyExists, "holds rows of another tenant");" (plan T7 S1f, `ACaseVariantOfTheOwnersOwnType_StillRunsTheAdoptionCheck`, generalised) | proved |
|  | P4_AStoredFirstLetterVariantCountsAsTheSameType | "// "Firstletterprobe" and "firstletterprobe" derive the same table, firstletterprobes, which" / "// the stored row adopted. Another tenant's earlier row in it must not block its owner." (plan T7 S1f, `AFirstLetterVariantOfTheOwnersOwnType_SkipsTheAdoptionCheck`) | proved |
| `PrivilegedRequestRepository.Transitions` | P1_AClaimTakesOnlyAPendingUnexpiredRequest | "**Claiming:** `UPDATE … SET "Status"='running' WHERE "Id"=@Id AND "Status"='pending' AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())`. Zero rows means it lost the race." (spec §5.1) | proved |
|  | P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires | "If the api then changes `pending` to `expired`, the worker will never run it" (spec §5.2) and "with the worker's claim holding the row, the api's `UPDATE … WHERE Status='pending'` waits, re-evaluates and affects 0 rows; the status stays `running`." (spec, Verified assumption 17) | proved |
|  | P3_OnlyClaimableRequestsAreListed | "**Polling:** every 1 s for `Status='pending' AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())`, oldest first." (spec §5.1) | proved |
|  | P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed | "A row still `running` more than one hour after it was claimed is marked failed ("the worker stopped before finishing this request"), so a worker that dies mid-request never leaves a request pending forever." (spec §5.1) | proved |
|  | P5_CleanupDeletesOnlyFinishedRequests | "**Cleanup:** finished rows older than one day are deleted." (spec §5.1) and "(await Requests.GetAsync(oldRunning)).Should().NotBeNull();" (plan T7 S1f, `DeleteFinishedOlderThan_RemovesOnlyOldFinishedRequests`) | proved |
|  | P6_ARequestExpiresAfterItsWaitOrNever | "**Expiry:** `ExpiresAt` is `RequestedAt + 15 s` for RegisterSchema and NULL for ReconcileType and ReplayDlq." (spec §5.1) | proved |
| `PostgresPrivilegedRequestLocks.Keys` | P1_EveryLockKeyLiesOutsideTheInt4Range | "**Locks:** session advisory locks on dedicated pooled connections, released in `finally`, with keys chosen outside the `int4` `hashtext` range used elsewhere." (spec §5.1) | proved |
|  | P2_NoTypeLockIsTheRegistrationLock | "`ReconcileType` takes a per-type lock, so a long reconcile never blocks registration." (spec §5.1) | proved |
|  | P3_ATypeLockFoldsTheNamesCase | "sameTypeOtherCase.IsCompleted.Should().BeFalse("a type lock folds the name's case");" (plan T7 S1f, `ATypeLock_SerialisesOneTypeIgnoringCase_AndBlocksNeitherAnotherTypeNorRegistration`) | proved |
| `SchemaRegistrationApplier.ExecuteAsync` | P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything | "A different owner fails with `PermissionDenied`, using today's message." (spec §5.3, step 1) | proved |
|  | P2_AnUnregisteredTypeNeverAdoptsAForeignTable | "The worker refuses with `AlreadyExists` unless:" / "the relation is an ordinary table (`relkind = 'r'`) with a `__TenantId` column; and" / "for an owned type, no row in it has a `__TenantId` other than the owner's (one `EXISTS` query under `iverson_maintenance`)." (spec §5.3) | proved |
|  | P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable | "The documented model-change recovery (delete the registry row, then re-register) keeps working for types whose tables hold only the owner's rows." (spec §5.3) | proved |
|  | P4_DriftIsFailedPreconditionAndNothingIsWritten | "**DDL.** `ApplySchemaAsync` with `SchemaDriftPolicy.Throw`. Drift fails with `FailedPrecondition`, as today." (spec §5.3) | proved |
|  | P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied | "**Registry write.** `INSERT … ON CONFLICT (type_name) DO UPDATE … WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner`. Zero rows fails with `PermissionDenied`." (spec §5.3) | proved |
|  | P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders | "### 5.3 The worker's RegisterSchema, per descriptor in batch order" / "**Re-renders.** It queues re-renders for changed templates." (spec §5.3) and the `Received.InOrder` of plan T7 S1b `ChangedTemplates_AreEnqueuedAfterEveryDescriptorIsWritten` | proved |
| `QueuedSchemaRegistrationExecutor.ExecuteAsync` | P1_SuccessIsAnsweredOnlyAfterTheApiReloads | "On `succeeded`, the api reloads its own registry (`LoadAsync`) before answering, so the caller's next write on that replica sees the type." (spec §5.2) | proved |
|  | P2_ARequestNeverClaimedIsExpiredAndUnavailable | "**15 s** for the worker to claim the request. If the api then changes `pending` to `expired`, the worker will never run it, and the caller gets `Unavailable` ("the worker did not pick the registration up; retry")." (spec §5.2) | proved |
|  | P3_AClaimedRequestStillRunningIsDeadlineExceeded | "If the claimed request still hasn't finished after 10 s, the caller gets `DeadlineExceeded` ("registration is still being applied") and the request completes in the worker." (spec §5.2) | proved |
|  | P4_AFailedRequestAnswersWithItsStoredStatusAndMessage | "On `failed`, the api returns the stored status code and message." (spec §5.2) | proved |
|  | P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded | "**Expiry:** `ExpiresAt` is `RequestedAt + 15 s` for RegisterSchema" (spec §5.1) and "If the worker had already claimed it, a further **10 s**." / "This 25 s maximum is below every edge default on the gRPC path" (spec §5.2) | proved |
| `PrivilegedRequestExecutor.RunAsync` | P1_ARefusalIsRecordedWithItsStatusAndMessage | "result.Code.Should().Be(StatusCode.PermissionDenied);" / "result.Message.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");" (plan T7 S1d, `ARefusal_IsRecordedWithItsStatusAndMessage`, generalised) | proved |
|  | P2_AnUnexpectedFailureIsInternalWithAGenericMessage | "**Errors:** an unexpected exception records `Internal` with a generic message; the details go to the log." (spec §5.1) | proved |
|  | P3_StoppingTheWorkerIsUnavailableNeverAPartialResult | "result.Code.Should().Be(StatusCode.Unavailable);" (plan T8 S1a, `ReconcileType_StoppedByWorkerShutdown_IsUnavailable_NotAPartialCount`, and T7 S1d, `StoppedByWorkerShutdown_IsRecordedAsUnavailable`) | proved |
|  | P4_EachKindTakesOnlyItsOwnLock | "`RegisterSchema` takes one global registration lock, which serialises it across worker replicas." / "`ReconcileType` takes a per-type lock, so a long reconcile never blocks registration." / "`ReplayDlq` takes none." (spec §5.1) | proved |
|  | P5_TheRegistryIsReloadedBeforeAnything | "**Registry:** the worker reloads its registry (`LoadAsync`) before running a request, so a type registered moments ago resolves." (spec §5.1) | proved |
|  | P6_AReplayRechecksTheRowThenProducesBeforeMarking | "`404`: the row is gone or was already replayed (the worker re-checks `Replayed=false`);" / "The worker produces the message and marks the row replayed. That is at-least-once, as today." (spec §5.2) | proved |
|  | P7_AReconcileRecordsItsCountOrNotFound | "the worker finished: `200 {reconciledCount, typeName}`, as today;" / "the type is unknown: `404`;" (spec §5.2) and "result.Message.Should().Be("No schema registered for 'NoSuchType'");" (plan T8 S1a, `ReconcileType_AnUnknownType_IsNotFound`) | proved |
| `PrivilegedRequestWorker.PollOnceAsync` | P1_OnlyClaimedRequestsRun | "Zero rows means it lost the race." (spec §5.1, Claiming) and "await _executor.DidNotReceive().RunAsync(Arg.Is(lost), Arg.Any<CancellationToken>());" (plan T7 S1e, `PollOnce_RunsOnlyTheRequestsItClaims`) | proved |
|  | P2_EachClaimedRunIsRecordedSucceededOnlyWhenOk | "status.Should().Be(PrivilegedRequestStatus.Failed);" (plan T7 S1e, `AFailedOutcome_IsRecordedAsFailed_WithItsResult`) and "_completed[won.Id].Status.Should().Be(PrivilegedRequestStatus.Succeeded);" (plan T7 S1e, `PollOnce_RunsOnlyTheRequestsItClaims`) | proved |
|  | P3_CleanupRunsAtMostHourly | "**Cleanup:** finished rows older than one day are deleted. A row still `running` more than one hour after it was claimed is marked failed ("the worker stopped before finishing this request"), so a worker that dies mid-request never leaves a request pending forever." (spec §5.1) | proved |
| `Program.PrivilegedAdminRoutes` | P1_AnUnfinishedRequestIsAnswered202WithItsId | "still running: `202 {requestId}`, and the worker completes it. Reconcile is idempotent, so the request has no expiry;" / "`202 {requestId}`: still pending." (spec §5.2) | proved |
|  | P2_AWorkerNotFoundIs404 | "the type is unknown: `404`;" / "`404`: the row is gone or was already replayed (the worker re-checks `Replayed=false`);" (spec §5.2) | proved |
|  | P3_AnyOtherWorkerFailureIs500WithItsMessage | "any other worker failure: `500 {error}` with the worker's recorded message (the same applies to replay)." (spec §5.2) | proved |
|  | P4_AFinishedRequestIs200 | "the worker finished: `200 {reconciledCount, typeName}`, as today;" / "`200`: replayed;" (spec §5.2) | proved |
|  | P5_TheReplayChecksRunBeforeAnythingIsQueued | "The api keeps every check it runs today: Operator, the active tenant, the row's `TenantId` against the caller's tenant, and `Replayed=false`. It reads those columns without `MessageValue`." (spec §5.2) and "await _requests.DidNotReceive().InsertAsync(" (plan T8 S1c, `Replay_ARowOfAnotherTenant_Returns403_AndNothingIsQueued`) | proved |
|  | P6_AdminRequestsNeverExpire | "Reconcile is idempotent, so the request has no expiry;" / "The api waits up to 60 s:" / "It queues the replay and waits up to 30 s:" (spec §5.2) and "_inserted.Value.ExpiresAfter.Should().BeNull("a reconcile is idempotent, so it never expires");" (plan T8 S1c, `Reconcile_TheWorkerFinished_Returns200WithTheCount`) | proved |
| `Program.Health` | P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry | "`/health` reports not-ready while any of these hold:" / "`has_table_privilege('iverson_api', '"IversonReconciliationQueue"', 'INSERT')` is false;" / "the registry hasn't loaded;" / "a `SET LOCAL ROLE iverson_runtime` probe fails." (spec §6.3) | proved |
|  | P2_StarRocksAuthPendingKeepsTheApiReady | "`ReadinessPolicy` keeps its `AuthPending` tolerance." (spec §6.3) | proved |
| `ObjectRetrievalGrpcService.GetManyTenantGuard` | P1_ATenantlessDecisionIsRefusedBeforeTheRead | "The api's maintenance-role reads and deletes for types without a tenant column, and the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4) | proved |
|  | P2_TheReadNeverRunsAcrossTenants | "**The api never enters BYPASSRLS.**" (spec §6.4) | proved |
| `ObjectMappingGrpcService.DeleteTenantGuard` | P1_ATenantlessDecisionIsRefusedBeforeTheTransaction | "The api's maintenance-role reads and deletes for types without a tenant column, and the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4) | proved |
|  | P2_TheDeleteNeverRunsAcrossTenants | "**The api never enters BYPASSRLS.**" (spec §6.4) | proved |
| `OutboxWriter.UpsertAndEnqueueOutboxAsync` | P1_ANullTenantIsRefusedBeforeTheTransaction | "The api's maintenance-role reads and deletes for types without a tenant column, and the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4) and "await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());" (plan T9 S1, `UpsertAndEnqueueOutboxAsync_NullTenantId_IsRefusedBeforeTheTransaction`) | proved |
|  | P2_TheUpsertAlwaysRunsInsideTheTenantScope | "the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4) | proved |
| `EngagementRepository.TenantScopedAccess` | P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting | "An unrecorded tenant reads as empty: `[]`, `null` or an empty stream, as today." / "A Delete for an unrecorded tenant does nothing: there is no StarRocks data to delete." (spec §7.3) | proved |
|  | P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting | "Every StarRocks reader consults it before connecting: the api's read paths and the worker's popularity aggregates." (spec §7.3) and "await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");" (plan T10 S1c, `NoDirectory_RefusesReads_BeforeConnecting`) | proved |
|  | P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting | "await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");" / "await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");" (plan T10 S1c, `NoTenantLoginKey_RefusesTenantScopedReadsAndWrites_BeforeConnecting`) | proved |
|  | P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange | "every StarRocks data connection carries exactly one tenant's role;" (spec §1) and "`SET ROLE` and `SET ROLE NONE` are removed from `RunTenantScopedAsync` and `StreamTenantScopedAsync`. Nothing may change a data connection's role or session state." (spec §7.2) | proved |
| `EngagementRepository.OpenOrSurfaceRefusalAsync` | P1_ARefusedTenantLoginSurfacesAsNotReady | "A recorded tenant whose login fails is a real error: it raises `EngagementNotReadyException`, which the gRPC services map to `Unavailable` and which halts the worker's consumer rather than dead-lettering (as round 11 does for a refused provisioner login)." (spec §7.3) | proved |
|  | P2_EveryOtherOpenOutcomeIsUnchanged | "(await act.Should().ThrowAsync<MySqlException>()).Which.Should().BeSameAs(original);" (plan T10 S1c, `OtherFailure_OnOpeningATenantConnection_IsRethrownUnchanged`, generalised) | proved |
|  | P3_ARefusedProvisionerLoginStillSurfacesAsNotReady | "(as round 11 does for a refused provisioner login)" (spec §7.3) | proved |
| `EngagementRepository.IsExpectedMissingResourceError` | P1_AnUnknownTableIsStillAnExpectedEmptyTenant | "`IsExpectedMissingResourceError` loses its role arm ("cannot find role", "is not granted to") and keeps its 5502 "Unknown table" arm." (spec §7.3) | proved |
|  | P2_NothingElseIsAnExpectedEmptyTenant | "`IsExpectedMissingResourceError` loses its role arm ("cannot find role", "is not granted to") and keeps its 5502 "Unknown table" arm." (spec §7.3) | proved |
| `EngagementRepository.EnsureTenantProvisionedAsync` | P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds | "New plumbing table `IversonStarRocksTenants (TenantId text PK, ProvisionedAt timestamptz)`. The worker inserts a row after each successful provisioning." (spec §7.3) and "(await directory.IsProvisionedAsync("t1")).Should().BeFalse();" (plan T10 S1c, `ProvisioningThatFails_DoesNotRecordTheTenant`) | proved |
|  | P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting | "await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");" (plan T10 S1c, `ProvisioningWithoutADirectory_RefusesBeforeConnecting`, generalised to the key) | proved |
|  | P3_TheTenantLoginReplacesTheAppGrant | "**Provisioning** (`EnsureTenantProvisionedAsync`, as `iverson_provisioner` with `user_admin` active, after the tenant role exists):" / "`CREATE USER IF NOT EXISTS 'iverson_t_…'@'%' IDENTIFIED BY '<derived>' DEFAULT ROLE '<tenant role>'`;" / "`ALTER USER … SET PROPERTIES ('session.query_timeout'='180', 'session.exec_mem_limit'='4294967296')`, the same caps `iverson_app` carries." / "`GRANT <role> TO USER 'iverson_app'@'%'` (`EngagementRepository.cs:523`) is removed." (spec §7.1) | proved |
| `PostgresProvisionedTenantDirectory.IsProvisionedAsync` | P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain | "`Iverson.StarRocks` takes an injected `IProvisionedTenantDirectory`, implemented over Postgres in `Iverson.Api` and cached per process once a tenant is found." (spec §7.3) and "await _sql.Received(2).QuerySingleOrDefaultAsync<bool>(" (plan T10 S1m, `AnUnrecordedTenant_IsAskedAgain_BecauseTheWorkerMayRecordItAtAnyMoment`) | proved |
|  | P2_RecordingCachesTheTenant | "(await directory.IsProvisionedAsync("acme")).Should().BeTrue();" (plan T10 S1m, `Recording_InsertsIdempotently_AndCachesTheTenant`) | proved |
| `StarRocksResiliencePipelineFactory.IsBackendTransient` | P1_AFullPoolIsNotTransient | "Pool exhaustion ("All pooled connections are in use") is excluded from the transient classification that feeds the per-process circuit breaker, so one tenant can't open the breaker for all tenants." (spec §7.2) | proved |
|  | P2_EveryOtherFailureKeepsItsTransientClassification | "await Assert.ThrowsAsync<BrokenCircuitException>(call);" (plan T11 S1b, `Build_StillTripsBreaker_OnAConnectTimeoutWithAFreePool`, generalised) | proved |
|  | P3_OnePredicateFeedsTheRetryAndTheBreaker | "Pool exhaustion is excluded from the retry as well as the circuit breaker (one predicate feeds both)." (spec §7.3) | proved |

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

✅ Approve as-is. Every obligation (96 of 96) is proved and passed G5; §2 and §3 are empty.
