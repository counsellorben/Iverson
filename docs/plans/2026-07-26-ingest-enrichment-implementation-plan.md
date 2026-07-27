# Ingest Enrichment (Ollama) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Task subagents run on Opus 5 low effort by default.

**Source spec:** `docs/specs/2026-07-26-ingest-enrichment-design.md` (commit SHA: `9b06389`)

**Goal:** Populate server-derived fields at ingest — object summary, keywords, extracted metadata, and per-chunk contextual prefixes — using a generative Ollama model.

**Architecture:** A new `EnrichmentConsumer` subscribes to `entity.created`/`entity.updated`, re-derives the authoritative row, and skips work when a SHA-256 over (source text + enrichment specification) matches a stored hash. Otherwise it calls Ollama `/api/generate`, writes only the enrichment target columns back in a tenant-scoped transaction alongside a state row and an outbox row, then re-fetches and publishes `entity.updated` so the existing Engagement and Intelligence consumers converge StarRocks and Qdrant. Contextual chunk prefixes are generated separately, inside `IntelligenceStoreConsumer`, and never written back.

**Tech stack:** .NET (net10.0), Postgres via Dapper-style `IRecordStoreQueryExecutor`, Kafka, Qdrant, StarRocks, Ollama. Clients in .NET, Java, Python, Go, TypeScript.

**Base branch:** `metadata-foundation` (part 1, implemented but not merged to `main`). Branch this work from there, not from `main`.

---

## Global Constraints

- **Target framework is net10.0.** (`TargetFramework` in every `Iverson.Server` csproj.)
- **New `SchemaDescriptor` members must be defaulted, never `required`**, and any `HashSet<string>` must re-apply `StringComparer.OrdinalIgnoreCase` in its `init` accessor — `SchemaRegistry.LoadAsync` deserializes legacy `_iverson_schema` rows with `System.Text.Json`. Both hazards documented at `SchemaDescriptor.cs:21-33`.
- **Tenant-scoped transactions must exit tenant scope before any plumbing-table statement.** `SET LOCAL ROLE iverson_runtime` persists for the whole transaction and the outbox/state tables have no grant for that role.
- **Commit messages use Conventional Commits** — the repo's log shows `docs(specs):`, `docs(plans):`, `fix(ts-client):`, `fix(admin-ui):`.
- **Enrichment must never block or fail an object's projection.** Ollama failures log and return; they never throw `PoisonMessageException`.

## File Structure

**Create**
- `Iverson.Server/Iverson.Embeddings/IEnrichmentService.cs` — generative service contract.
- `Iverson.Server/Iverson.Embeddings/EnrichmentService.cs` — Ollama `/api/generate` wrapper.
- `Iverson.Server/Iverson.Embeddings/EnrichmentServiceOptions.cs` — `Enrichment` config section.
- `Iverson.Server/Iverson.Embeddings/EnrichmentPrompts.cs` — the four server-side prompts.
- `Iverson.Server/Iverson.Sql/EnrichmentStateRepository.cs` — `iverson_enrichment_state` table + hash get/upsert/delete.
- `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs` — the pipeline.
- `Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs`
- `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`

**Modify**
- `Iverson.Clients/Common/Proto/object_mapping.proto` — `PropertyDescriptor` fields 19-22.
- `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` — `EnrichmentTargets`, `ChunkDescriptor.Contextual`.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — map the new proto fields.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — five validation rules.
- `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` — `UpdateColumnsAsync` on `IEntityRepository`.
- `Iverson.Server/Iverson.Sql/EntityRepository.cs` — its implementation.
- `Iverson.Server/Iverson.Sql/OutboxWriter.cs` — tx-scoped non-delete enqueue.
- `Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs` — `AddEnrichment`.
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — contextual prefixes.
- `Iverson.Server/Iverson.Api/Program.cs` — register service, consumer, state table.
- `Iverson.Server/docker-compose.yml`, `deploy/helm/iverson/charts/ollama/templates/statefulset.yaml`, `deploy/helm/iverson/values.yaml` — model provisioning.
- Per-language client attribute + registrar files (Tasks 7-11).

**Test**
- `Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`, `IntelligenceStoreConsumerTests.cs`, `Grpc/SchemaRegistrationOrchestratorTests.cs`, `Iverson.Sql.Tests/`, `Iverson.Embeddings.Tests/`, plus each client's registrar test.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time; **not** re-verified here. See the spec's `Verified assumptions` table (A1-A18) for evidence. Summarised:

- A1-A3: `FetchByKeyAsync` exists and no column-update method does; a transaction context can compose entity + state + outbox writes; RLS permits system-actor writes, with the constraint that tenant scope must be exited before plumbing-table writes.
- A4-A6: a third consumer on the same topic is supported; server-owned tables have a creation mechanism; proto fields 19-22 are free.
- A7-A9: all five clients have an attribute + registrar path; `SchemaDescriptor` is extensible; enrichment columns project automatically via `ScalarColumns`.
- A10-A12: the chunk `context` key was dropped as write-only; the extra republish breaks nothing; field authorization is unaffected.
- A13-A15: the embedding service pattern is copyable; Ollama supports `format:"json"` and `docker-compose.yml:96-103` needs restructuring; the base branch is `metadata-foundation`.
- A16-A18: no tx-scoped non-delete outbox enqueue exists; an outbox row alone converges via reconciliation (30s poll); the enricher generates its own outbox row Guid.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against the `metadata-foundation` worktree.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | Registry/builder/proto paths exist as cited | `SchemaDescriptor.cs:3,45,49,51`; `SchemaBuilder.cs`; `Iverson.Clients/Common/Proto/object_mapping.proto` |
| P2 | File path | Registration validation belongs in `SchemaRegistrationOrchestrator.cs` | Existing `ValidateIdentifier`/`ValidateFieldReference` helpers throwing `RpcException(InvalidArgument)` at `:50, :74, :92, :103, :110` |
| P3 | File path | Every new server file's path is unoccupied | `Iverson.Embeddings/` has no `Enrichment*`; `Iverson.Api/Consumers/` holds only `ConsumerResilience`, `EngagementStoreConsumer`, `IntelligenceStoreConsumer` |
| P4 | File path | All five clients have attribute + registrar + test paths | DotNet `Iverson.Client.Attributes/`, `Iverson.Client.Core/SchemaRegistrar.cs`; Java `client/src/main/java/io/iverson/client/{annotations,core}/`; Python `iverson_client/{annotations,core}.py`; Go `iverson/registrar.go`; TypeScript `src/annotations.ts` |
| P5 | File path | Generated proto output differs by language | .NET regenerates at build via `Grpc.Tools` (`Iverson.Client.Contracts.csproj:12,17`); Java via `protobuf-maven-plugin` (`Java/client/pom.xml:98-102`); Python/Go/TypeScript have **checked-in** `generated/` dirs |
| P6 | File path | Infra paths as cited | `docker-compose.yml:96-103`; `charts/ollama/templates/statefulset.yaml:44-59`; `values.yaml:76-85` |
| P7 | Signature | `AddEmbeddings` shape is copyable — **with one caveat** | `ServiceCollectionExtensions.cs:8-23`. `Telemetry.HttpClientName` is a single const `"iverson.ollama"` whose `BaseAddress` binds to `EmbeddingServiceOptions`; enrichment must register its **own** named client or `Enrichment__BaseUrl` is silently ignored |
| P8 | Signature | `ConsumerResilience.RunWithRestartAsync(Func<Task>, ILogger, string, CancellationToken, TimeSpan? = null)` | `ConsumerResilience.cs:12-17` |
| P9 | Signature | Transaction primitives exist as the plan calls them | `IRecordStoreRoles.cs:16-26` (`IRecordStoreTransactionRunner` void + generic overloads; `IDbTransactionContext.ExecuteAsync`/`QuerySingleOrDefaultAsync`) |
| P10 | Signature | `StoreTargeting.DetermineTargetStores` is reachable from the consumer | `internal static` in `Iverson.Api/Schema/StoreTargeting.cs`; consumer lives in the same assembly |
| P11 | Signature | `Telemetry.Source` usable by a sibling service | `Iverson.Embeddings/Telemetry.cs` — `internal static ActivitySource Source` |
| P12 | Command | Solutions are `.slnx`; TFM is net10.0 | `Iverson.slnx`, `Iverson.Server/Iverson.Server.slnx`, `Iverson.Clients/DotNet/Iverson.Client.slnx` |
| P13 | Command | Per-language test commands | .NET `dotnet test <slnx>`; Java Maven (`pom.xml`); Python `pytest` (`[tool.pytest.ini_options] testpaths=["tests"]`); Go `go test ./...`; TypeScript `npm test` → `vitest run` (`package.json`) |
| P14 | Command | Python/Go/TypeScript require an explicit codegen run | each has `scripts/generate_protos.sh`; their `generated/` output is committed, unlike .NET/Java which regenerate at build |
| P15 | Command | Commit convention is Conventional Commits | `git log -15`: `docs(specs):`, `docs(plans):`, `fix(ts-client):`, `fix(admin-ui):` |
| P16 | Ordering | No task consumes a symbol a later task introduces | T4 consumes T1/T2/T3; T5 consumes T1/T2; T7-11 consume only T1's proto and nothing from one another; T6 is independent |
| P17 | Code validity | `/api/generate` with `stream:false` returns the text in the `response` field | Official Ollama `api.md` non-streaming example |
| P18 | Code validity | `SHA256.HashData` is available | net10.0 |
| P19 | Consumer impact | **`ChunkDescriptor` is a positional record** — `Contextual` must be a trailing optional parameter | `SchemaDescriptor.cs:51` declares 5 positional params; 5 construction sites: `SchemaBuilder.cs:58`, `IntelligenceStoreConsumerTests.cs:598,873`, `ObjectSearchGrpcServiceTests.cs:207`, `SchemaFixtures.cs:39` |
| P20 | Consumer impact | Exactly one implementer each of `IEntityRepository` and `IOutboxWriter` | `EntityRepository.cs:3`; `OutboxWriter.cs:13`. No hand-written fakes — tests substitute the interfaces |
| P21 | Consumer impact | One construction site for `IntelligenceStoreConsumer` | `IntelligenceStoreConsumerTests.BuildSut()` at `:72-81` (target-typed `new`); `Program.cs:240` resolves via DI |
| P22 | Consumer impact | `SchemaDescriptor` additions are additive | uses `{ get; init; }` members, not positional params — unlike P19 |

## Tasks

### Task 1: Declaration plumbing — proto, registry, builder, validation

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`

**Interfaces:**
- Produces: `PropertyDescriptor` fields 19-22; `SchemaDescriptor.EnrichmentTargets`; `ChunkDescriptor.Contextual`. Every other task depends on these.

- [ ] **Step 1: Add the four proto fields to `PropertyDescriptor`**
```proto
bool   is_summary_target  = 19;  // [IversonSummary]
bool   is_keywords_target = 20;  // [IversonKeywords]
string extract_hint       = 21;  // [IversonExtracted]; empty = absent
bool   chunk_contextual   = 22;  // [IversonChunk(Contextual = true)]
```

- [ ] **Step 2: Add the registry members**

Add to `SchemaDescriptor` — defaulted, never `required` (see Global Constraints):
```csharp
public IReadOnlyList<EnrichmentTarget> EnrichmentTargets { get; init; } = [];
```
```csharp
public enum EnrichmentKind { Summary, Keywords, Extracted }

public sealed record EnrichmentTarget(string ColumnName, EnrichmentKind Kind, string? Hint);
```

`ChunkDescriptor` is a **positional** record with five existing construction sites (P19). Add `Contextual` as a **trailing optional parameter** so those sites keep compiling:
```csharp
public sealed record ChunkDescriptor(
    string PropertyName, int MaxTokens, int Overlap, string ModelId, int Dimension,
    bool Contextual = false);
```

- [ ] **Step 3: Write the validation tests first**

In `SchemaRegistrationOrchestratorTests`, one test per rule — each asserting `RpcException` with `StatusCode.InvalidArgument`:
non-text target; key/tenant/owner target; target that also carries `[IversonEmbedding]`/`[IversonChunk]`; a type with enrichment targets but no source property; empty `[IversonExtracted]` hint.

- [ ] **Step 4: Implement the five validation rules**

In `SchemaRegistrationOrchestrator`, following the existing `ValidateFieldReference` style. The third rule is what enforces the loop-prevention invariant in Task 4 — a target that is also a source property would make the writeback mutate the hashed text, so the enricher would re-enrich its own republished event without bound. Reference that in the failure message.

- [ ] **Step 5: Map the proto fields in `SchemaBuilder`**

Populate `EnrichmentTargets` from `is_summary_target` / `is_keywords_target` / `extract_hint`, and pass `chunk_contextual` into the `ChunkDescriptor` construction at `SchemaBuilder.cs:58`.

- [ ] **Step 6: Run tests**
```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs
git commit -m "feat(enrichment): declare enrichment targets in proto, registry, and registration validation"
```

---

### Task 2: `IEnrichmentService` — Ollama generative wrapper

**Files:**
- Create: `Iverson.Server/Iverson.Embeddings/IEnrichmentService.cs`, `EnrichmentService.cs`, `EnrichmentServiceOptions.cs`, `EnrichmentPrompts.cs`
- Modify: `Iverson.Server/Iverson.Embeddings/ServiceCollectionExtensions.cs`
- Test: `Iverson.Server/Iverson.Embeddings.Tests/EnrichmentServiceTests.cs`

**Interfaces:**
- Produces: `IEnrichmentService`, consumed by Tasks 4 and 5.

- [ ] **Step 1: Options**
```csharp
public sealed class EnrichmentServiceOptions
{
    public const string Section = "Enrichment";
    public string BaseUrl  { get; set; } = "http://localhost:11434";
    public string ModelId  { get; set; } = "qwen2.5:3b";
    public bool   Enabled  { get; set; } = true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
```

- [ ] **Step 2: Contract and prompts**

`IEnrichmentService` exposes one generate call returning the model's text, plus a JSON-mode variant for extraction. `EnrichmentPrompts` holds the four prompts — summary, keywords, extraction, chunk context — as constants. Not configurable; the spec excludes prompt templating.

- [ ] **Step 3: Implement `EnrichmentService`**

Mirror `EmbeddingService` — `IHttpClientFactory`, `Telemetry.Source` activity, `EnsureSuccessStatusCode`, stream-parsed response. POST `/api/generate` with `stream: false` and, for extraction, `format: "json"`. The generated text is in the **`response`** field (P17).

- [ ] **Step 4: Register DI with its own named HttpClient**

`AddEnrichment(cfg)` mirrors `AddEmbeddings` (`ServiceCollectionExtensions.cs:8-23`) but **must not** reuse `Telemetry.HttpClientName`. That const is bound to `EmbeddingServiceOptions.BaseUrl`; reusing it would silently ignore `Enrichment__BaseUrl` (P7). Add a distinct const, e.g. `iverson.ollama.enrichment`.

- [ ] **Step 5: Tests + run**

Follow `EmbeddingServiceTests`: a stubbed handler asserting the request body carries `stream:false` and the configured model, and that the `response` field is what gets returned.
```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Embeddings Iverson.Server/Iverson.Embeddings.Tests
git commit -m "feat(enrichment): add Ollama generative service with its own named HTTP client"
```

---

### Task 3: Persistence primitives — targeted update, tx-scoped enqueue, state repository

**Files:**
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, `EntityRepository.cs`, `OutboxWriter.cs`
- Create: `Iverson.Server/Iverson.Sql/EnrichmentStateRepository.cs`
- Test: `Iverson.Server/Iverson.Sql.Tests/`

**Interfaces:**
- Produces: `UpdateColumnsAsync`, the tx-scoped outbox enqueue, and `IEnrichmentStateRepository` — all consumed by Task 4 only.

- [ ] **Step 1: `UpdateColumnsAsync` on `IEntityRepository`**

Add to the interface (`IRecordStoreRoles.cs:49-56`) and implement in `EntityRepository`. Issues `UPDATE "<table>" SET "col" = @p... WHERE "<key>" = @Key` over only the supplied columns. Column names come from the registry, never from client input — interpolate them the way the existing repository code does, and parameterize the values. Exactly one implementer exists (P20), so no fakes need updating.

- [ ] **Step 2: Tx-scoped, non-delete outbox enqueue**

`EnqueueDeleteOutboxRowAsync` (`OutboxWriter.cs:69-77`) takes an `IDbTransactionContext` but hardcodes `'Deleted'` (A16). Add a sibling that takes the same `tx` and enqueues an `Updated` row. It must accept the caller-supplied `Guid` so the enricher can pass it to `PublishAsync` for cleanup (A18).

- [ ] **Step 3: `EnrichmentStateRepository`**

Mirror `SchemaRegistryRepository.cs:5-8`:
```sql
CREATE TABLE IF NOT EXISTS iverson_enrichment_state (
    tenant_id  TEXT NOT NULL,
    type_name  TEXT NOT NULL,
    entity_key TEXT NOT NULL,
    source_hash TEXT NOT NULL,
    enriched_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, type_name, entity_key)
)
```
Expose `EnsureTableAsync`, `GetHashAsync`, a tx-scoped `UpsertAsync(tx, ...)`, and `DeleteAsync`. A plumbing table — no RLS, written after `ExitTenantScopeAsync()`. `tenant_id` is `NOT NULL`, which is safe because Task 4 skips null-tenant objects before reaching this table.

- [ ] **Step 4: Tests + run**
```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Sql Iverson.Server/Iverson.Sql.Tests
git commit -m "feat(enrichment): add targeted column update, tx-scoped outbox enqueue, and enrichment state repository"
```

---

### Task 4: `EnrichmentConsumer`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs`

**Interfaces:**
- Consumes: Task 1's `EnrichmentTargets`, Task 2's `IEnrichmentService`, Task 3's three persistence primitives.

- [ ] **Step 1: Write the behavioral tests first**

Following `IntelligenceStoreConsumerTests`' fixture style:
- Hash unchanged → no LLM call, no write. **The loop breaker — the most important test here.**
- Changing an `[IversonExtracted]` hint (with source text untouched) → does re-enrich.
- Targeted update preserves a concurrent client edit to a non-enrichment column.
- Null tenant → object skipped, **no state row written**.
- LLM failure → object intact and unenriched, no `PoisonMessageException`.
- The transaction exits tenant scope before the state and outbox writes.
- `Enrichment__Enabled=false` → consumer does nothing.

- [ ] **Step 2: Consumer skeleton**

`BackgroundService`, group id `iverson.consumer.enrichment`, subscribing to `EntityTopics.Events` via `ConsumerResilience.RunWithRestartAsync` (P8) — same shape as the two existing consumers. Gate on `schema.EnrichmentTargets.Count > 0`. Handle `Deleted` by removing the state row.

- [ ] **Step 3: Fetch, hash, compare**

Fetch the authoritative row with `FetchByKeyAsync` (not the event payload). Build the source text from the type's `[IversonEmbedding]` and `[IversonChunk]` properties, then hash **source text + the ordered (column, kind, hint) set from `EnrichmentTargets`** with `SHA256.HashData`. Including the specification is what makes a newly declared target or an edited hint re-enrich existing objects. Compare against `GetHashAsync`; equal → return.

- [ ] **Step 4: Null-tenant guard**

Before any write: if `schema.TenantColumn` is null, or the tenant value re-derived from the row is null, log and **return without writing a state row**. Fail-closed, matching `EngagementStoreConsumer.cs:55-60`. Writing a state row here would mark the object enriched forever while the RLS-blocked `UPDATE` silently matched zero rows.

- [ ] **Step 5: Generate and write back**

Call `IEnrichmentService` per target. Then, in one transaction, in this exact order:
1. `EnterTenantScopeAsync(tenantId)`
2. `UpdateColumnsAsync` — enrichment target columns only
3. `ExitTenantScopeAsync()`
4. state upsert + outbox enqueue (with a caller-generated `Guid`)

Steps 3-4 are not optional (see Global Constraints).

- [ ] **Step 6: Publish**

After the transaction commits, **re-fetch the row** with `FetchByKeyAsync` and pass that as `payloadJson` to `IOutboxPublisher.PublishAsync`, with `targetStores` from `StoreTargeting.DetermineTargetStores(schema)` (P10) and the outbox row `Guid` from step 5. Do **not** publish the step-3 snapshot with enriched columns merged in — it predates any client update that landed during the LLM call, and publishing it would carry stale values to StarRocks and Qdrant, winning over the client's own event.

- [ ] **Step 7: Failure handling**

Wrap generation and writeback so a failure logs and returns without throwing `PoisonMessageException`, leaving no state row. Enrichment must never block projection.

- [ ] **Step 8: Register in `Program.cs`**

`AddEnrichment(cfg)`, `AddHostedService<EnrichmentConsumer>()` beside the two existing consumers (`Program.cs:239-240`), and call the state table's `EnsureTableAsync` at startup. Gate the hosted service on `Enrichment__Enabled`.

- [ ] **Step 9: Run tests**
```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 10: Commit**
```bash
git add Iverson.Server/Iverson.Api/Consumers/EnrichmentConsumer.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Consumers/EnrichmentConsumerTests.cs
git commit -m "feat(enrichment): add EnrichmentConsumer with specification-inclusive hashing and tenant-scoped writeback"
```

---

### Task 5: Contextual chunk prefixes

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

**Interfaces:**
- Consumes: Task 1's `ChunkDescriptor.Contextual`, Task 2's `IEnrichmentService`.

- [ ] **Step 1: Tests**

Add to `IntelligenceStoreConsumerTests`: a `Contextual = true` chunk field embeds prefixed text while the payload's `text` key stays the raw chunk; a `Contextual = false` field is unchanged; with no summary present, the prefix falls back to truncated parent text.

- [ ] **Step 2: Inject `IEnrichmentService`**

Add the constructor parameter. Exactly one construction site needs updating — `BuildSut()` at `IntelligenceStoreConsumerTests.cs:72-81` (P21); `Program.cs:240` resolves via DI.

- [ ] **Step 3: Generate prefixes in the chunk loop**

In the chunk block (`IntelligenceStoreConsumer.cs:173-225`), when `cf.Contextual`, generate a short situating sentence per chunk and prepend it **to the text passed to `EmbedAsync` only**. The chunk payload is unchanged — `text` stays the raw chunk, and the prefix is **not** stored under any key, since `SearchChunks` reads only `text` and `parent_id`.

Condition the prompt on the object's summary, located via the type's `EnrichmentTargets`. When absent — which is always true on first ingest — fall back to a truncated slice of the parent text. The enricher's republish then drives a second pass that regenerates prefixes summary-conditioned; `ComputeChunkPointId` is deterministic, so it overwrites rather than duplicating.

- [ ] **Step 4: Run tests**
```bash
dotnet test Iverson.Server/Iverson.Server.slnx
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "feat(enrichment): condition contextual chunk embeddings on the object summary"
```

---

### Task 6: Model provisioning

**Files:**
- Modify: `Iverson.Server/docker-compose.yml`, `Iverson.Server/deploy/helm/iverson/charts/ollama/templates/statefulset.yaml`, `Iverson.Server/deploy/helm/iverson/values.yaml`

Independent of every other task.

- [ ] **Step 1: Helm**

The `pull-model` init container is already a shell script (`statefulset.yaml:53-59`) — add one `ollama pull` line for the generative model. Surface the model name in `values.yaml` beside the existing ollama block.

- [ ] **Step 2: docker-compose**

`ollama-init` is a single `curl` argv (`docker-compose.yml:96-103`) and cannot issue two pulls as written. Restructure it to run both — either a shell entrypoint issuing two `curl` calls, or a second init service. Add `Enrichment__BaseUrl` and `Enrichment__ModelId` to the api/worker environment blocks beside the existing `Embeddings__*` keys.

- [ ] **Step 3: Commit**
```bash
git add Iverson.Server/docker-compose.yml Iverson.Server/deploy/helm/iverson
git commit -m "chore(enrichment): pull the generative model in compose and helm"
```

---

### Tasks 7-11: Client declarations (one per language)

Tasks 7-11 are **independent of each other** and depend only on Task 1. They can run in parallel.

Each task adds the four declarations, maps them in that language's `SchemaRegistrar`, and extends the registrar tests — mirroring exactly what part 1 did for `[IversonMetadata]`.

Per language:

| Task | Language | Declaration site | Registrar | Tests | Codegen |
|---|---|---|---|---|---|
| 7 | .NET | `DotNet/Iverson.Client.Attributes/` (one file per attribute) | `DotNet/Iverson.Client.Core/SchemaRegistrar.cs` | `Iverson.Client.Core.Tests` | automatic at build (`Grpc.Tools`) |
| 8 | Java | `Java/client/src/main/java/io/iverson/client/annotations/` | `.../core/SchemaRegistrar.java` | `client/src/test/.../SchemaRegistrarTest.java` | automatic at build (`protobuf-maven-plugin`) |
| 9 | Python | `Python/iverson_client/annotations.py` | `Python/iverson_client/core.py` | `Python/tests/test_schema_registrar.py` | **run `scripts/generate_protos.sh`, commit `generated/`** |
| 10 | Go | `Go/iverson/` | `Go/iverson/registrar.go` | `Go/iverson_test/registrar_test.go` | **run `scripts/generate_protos.sh`, commit `generated/`** |
| 11 | TypeScript | `TypeScript/src/annotations.ts` | `TypeScript/src/` | `TypeScript/tests/schema-registrar.test.ts` | **run `scripts/generate_protos.sh`, commit `generated/`** |

Steps for each task:

- [ ] **Step 1:** Add the four declarations in that language's idiom — summary, keywords, extracted (with hint), and a `Contextual` option on the existing chunk declaration.
- [ ] **Step 2:** Regenerate protos if the language requires it (Tasks 9-11 only; P14).
- [ ] **Step 3:** Map the declarations onto proto fields 19-22 in the registrar.
- [ ] **Step 4:** Extend the registrar tests to assert each declaration reaches the descriptor.
- [ ] **Step 5:** Run that language's tests — .NET `dotnet test Iverson.Clients/DotNet/Iverson.Client.slnx`; Java Maven; Python `pytest`; Go `go test ./...`; TypeScript `npm test` (P13).
- [ ] **Step 6:** Commit, scoped to that language's directory, e.g. `feat(python-client): declare enrichment targets in the schema registrar`.

## Tasks NOT in this plan

Tensor re-ranking and fusion, derived vector signals, agent-facing schema retrieval, GPU support in the Ollama chart, configurable prompt templates, and any enrichment output beyond the four named above.

## Known issues inherited from spec

Neither the Go nor the TypeScript client populates `TypeDescriptor.tenant_field`, which the proto marks REQUIRED and `SchemaRegistrationOrchestrator` rejects. Schema registration from those two clients already fails against a current server. This was found during part 1's execution and is unrelated to enrichment, but it is adjacent: this design adds registrar work in those same two files, and end-to-end verification of the new declarations from Go or TypeScript will not be possible until it is fixed. It needs its own task.
