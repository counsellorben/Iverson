# Agent-Facing Schema Retrieval (`GetSchema`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-02-agent-facing-schema-retrieval-design.md` (commit SHA: `9ab31e6`)

**Goal:** One RPC returning the catalog of registered types the calling identity may read, with fields filtered by the same field-level authorization that governs every data read, plus a client method in all five languages.

**Architecture:** A new `GetSchema` RPC on the existing `ObjectMappingService`, returning purpose-built `SchemaType`/`SchemaField`/`SchemaRelation` messages rather than round-tripping the registration `TypeDescriptor`. The server projects each `SchemaDescriptor` in `SchemaRegistry.All` through `IRowFieldAuthorizationEvaluator`, omitting denied types and intersecting fields with `AllowedFields`. Each client gains one method on its data-plane client returning the generated proto type.

**Tech stack:** proto3 + gRPC across .NET 10 (Grpc.Tools), Java 21 (protobuf-maven-plugin), Python (grpc_tools), Go (protoc-gen-go), TypeScript (ts-proto); xUnit + FluentAssertions + NSubstitute server-side.

---

## Global Constraints

Copied from the spec; every task holds to these.

- **No change to any existing RPC, no change to the persisted schema model, no new storage.**
- **`ClrType` and `RelationKind` are reused, not redefined** — both already live in `object_mapping.proto`.
- **`GetSchema` carries no policy attribute.** It inherits the ambient `RequireAuthenticatedUser()` fallback. Gating it behind `SchemaAdmin` would put discovery out of reach of the callers it exists to serve.
- **Deliberately excluded from the response:** `TableName`, `CollectionName`, SQL type strings, `LargeFieldColumns`, the tenant column, and `AuthorizationRules`.
- **`search_key_order` is the rank, not the declared value.** The declared number is never persisted — only the resulting sequence.
- **An absent acting user yields an empty catalog, not a full one.** `RowFieldAuthorizationEvaluator` returns `Denied` for a null acting user, so every type is omitted.
- **No audit logging on this path.** `AuditLog` records data access; a schema-shape read is not that, and a per-type denial entry on every catalog call would be noise. Do not add one.

## File Structure

**Modify — proto**
- `Iverson.Clients/Common/Proto/object_mapping.proto` — one RPC, four messages, one enum

**Regenerate — committed stubs** (`.NET` and Java generate at build time and commit nothing)
- `Iverson.Clients/Go/generated/object_mapping.pb.go`, `object_mapping_grpc.pb.go`
- `Iverson.Clients/Python/iverson_client/generated/object_mapping_pb2.py`, `_pb2_grpc.py`, `_pb2.pyi`
- `Iverson.Clients/TypeScript/generated/object_mapping.ts`

**Modify — server**
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — `SqlTypeToClr` inverse
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — `GetSchema` override

**Create / Modify — clients**
- Create `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaCatalogClient.cs`
- Modify `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs`
- Modify `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`
- Modify `Iverson.Clients/TypeScript/src/core.ts`
- Modify `Iverson.Clients/Python/iverson_client/core.py`
- Modify `Iverson.Clients/Go/iverson/coordinator.go`

**Test**
- `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` — type recovery
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — projection and filtering
- One existing suite per client (see P11)

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here:

- **A1–A4** `object_mapping.proto` is the sole definition of `ObjectMappingService`; `ClrType` and `RelationKind` live there and are reusable; `trace_id` is the request convention; the six new message/enum names are free.
- **A5** `SchemaRegistry.All` exposes every registered descriptor (`SchemaRegistry.cs:13`).
- **A6, A7** `AuthorizationAction.Read` exists, `Evaluate` takes `(schema, actingUser, action)`, and `AllowedFields` spans key + scalars + FKs + vector/chunk source names.
- **A8** The service requires authentication ambiently (`Program.cs:143-145`, `:426`).
- **A9, A20** `SchemaDescriptor` carries every member the projection reads; `ScalarColumns` and `FkColumns` overlap, so the candidate set is key + scalars only.
- **A10–A13** `ScalarTypeMap` is injective on SQL type; every array SQL type ends in `[]` and no scalar does; `SearchKeyColumns` is ordered by rank; `RelationDescriptor` and `EnrichmentKind` shapes.
- **A14, A15, A21** Four clients hold a non-generic data-plane client with a mapping stub and bind identity at construction or on the context; .NET has neither, hence `SchemaCatalogClient` with a constructor-injected acting-user token provider.
- **A16, A17** Each suite has an `ObjectMappingService` mock-stub pattern; generated proto types are already public per client.
- **A18** `ScalarTypeMap`/`ArrayTypeOverrides` are `private static readonly`, so tests drive the enum through `ClrTypeToSql` and the new inverse rather than enumerating the maps.
- **A19** Nothing breaks when a second read-only consumer of `SchemaRegistry.All` is added.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `main@9ab31e6`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The proto and all six modified source files exist at the cited paths | Each read directly; see File Structure |
| P2 | File path | The three generated directories are the ones the scripts write | `Go/scripts/generate_protos.sh` writes `$GO_DIR/generated`; Python's writes `iverson_client/generated`; TypeScript's writes `generated/` |
| P3 | File path | `SchemaCatalogClient.cs` does not exist yet | `find . -name "SchemaCatalogClient*"` and `grep -rn SchemaCatalogClient --include=*.cs` both return nothing |
| P4 | File path | Both server test files exist | `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` and `Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` both present |
| P5 | Signature | **Generated service-base RPC methods are `virtual`, not `abstract`** | The generated `ObjectMappingServiceBase` is an `abstract partial class` but each RPC is `public virtual … Task<T> Method(...)` returning Unimplemented by default. Task 1 therefore compiles on its own — the server is not forced to implement `GetSchema` in the same task |
| P6 | Signature | `ClrTypeMapping` is `(string SqlType, string StarRocksType, PayloadIndexKind PayloadKind)` | `SchemaBuilder.cs:227`, `private readonly record struct` |
| P7 | Signature | `ScalarTypeMap`/`ArrayTypeOverrides` are `IReadOnlyDictionary<ClrType, ClrTypeMapping>`, so the inverse can project `kv.Key` and `kv.Value.SqlType` | `SchemaBuilder.cs:233`, `:249` |
| P8 | Signature | `SqlTypeToClr` is a free name | `grep -rn "SqlTypeToClr" --include=*.cs` returns nothing |
| P9 | Signature | .NET `SchemaRegistrar` takes `ObjectMappingService.ObjectMappingServiceClient`, and `SchemaCatalogClient` mirrors it | `SchemaRegistrar.cs:14-17` |
| P10 | Signature | Each client's mapping stub member name | Python `self._mapping_stub` (`core.py:586`); TypeScript `IversonClient._mappingClient` (`core.ts:531`, **not** `:322`, which is `SchemaRegistrar`'s own field); Java `mappingStub` (`IversonClient.java:31`); Go exported `MappingStub` (`coordinator.go:67`) |
| P11 | File path | Each client's test file exists | `Iverson.Client.Core.Tests/`, `SchemaRegistrarTest.java`, `schema-registrar.test.ts`, `test_auth.py`, `registrar_test.go` |
| P21 | **Consumer impact** | **The suites' mock-stub pattern injects a stub into the class under test — which works for four clients and not for Java's `IversonClient`** | `SchemaRegistrarTest.java:30-34` mocks the gRPC stub and passes it to `SchemaRegistrar`, which *takes* one. `IversonClient.mappingStub` is `final` package-private (`IversonClient.java:31`), assigned from a `ManagedChannel` in all four constructors (`:40`, `:47`, `:60`, `:70`); none accepts a stub, and `grep -rn "new IversonClient" …/src/test/` returns nothing. Python escapes this because `client._mapping_stub` is assignable on the instance; TypeScript, Go and .NET all receive or expose their stub. Task 4 therefore adds a package-private test constructor |
| P12 | Command | The three generation scripts are runnable here | `protoc` at `~/sdk/protoc/bin/protoc` (present, executable); `protoc-gen-go` and `protoc-gen-go-grpc` on PATH; `python3 -c "import grpc_tools"` succeeds; `protoc-gen-ts_proto` in TypeScript's `node_modules/.bin` |
| P13 | Command | Build/test commands are valid | `dotnet build Iverson.Clients/DotNet/Iverson.Client.Contracts` (has `<Protobuf Include="../../Common/Proto/*.proto" GrpcServices="Both" />` at `:17`); `mvn -f Iverson.Clients/Java/pom.xml`; `dotnet test` on the two named test projects; `pytest`; `go test -count=1 ./...`; `npm test` / `npm run build` |
| P14 | **Consumer impact** | **Inserting `actingUserTokenProvider` before the trailing `params Assembly[]` breaks neither caller** | Two callers exist. `Iverson.Client.Sample/Program.cs:13-15` passes `grpcEndpoint:` and `entityAssemblies:` — both named. `Iverson.LoadTest/Program.cs:119-122` passes three positionally (`grpcUrl`, `clientCredentials`, a provider) then `entityAssemblies:` named. The new parameter sits after `dataPlaneTokenProvider`, so both continue to bind as before |
| P22 | Signature | **A non-null `AllowedFields` always contains the key column, so the candidate-set intersection can never empty** | `RowFieldAuthorizationEvaluator.cs:65` filters the key out of `excluded` (`.Where(f => !string.Equals(f, schema.KeyColumn.Name, OrdinalIgnoreCase))`); `:71` seeds `allFields` with `schema.KeyColumn.Name` before subtracting `excluded`. `IRowFieldAuthorizationEvaluator.cs:21-25` states the same contract in prose. The plan's candidate set also always contains the key column, so the intersection retains at least it for any non-denied type |
| P15 | **Consumer impact** | **The committed stubs are already out of sync with the proto — regeneration produces unrelated diff** | Running all three scripts with **no** proto change modifies `Go/generated/object_search.pb.go` (+22/−9) and `TypeScript/generated/object_search.ts` (+20/−1). `object_search.proto` last changed at `71b59f7`; the Go stub was last regenerated at `4b249fb`. The diff is doc-comment propagation only (fused-score and MMR-diversification comments) — no wire or API change. Task 1 Step 1 commits this separately so the `GetSchema` commit stays clean |
| P16 | Code validity | `repeated` enum fields and adding an `rpc` to an existing service are valid proto3 | `object_mapping.proto:1` is `syntax = "proto3"`; the file already contains `repeated` fields and a five-`rpc` service block at `:10-16` |
| P17 | Ordering | Tasks 3–7 depend on Task 1 only | Each client task consumes only generated types; none imports a symbol another client task creates, and none calls the server code Task 2 adds — the client tests run against mock stubs |
| P18 | Ordering | Task 2 depends on Task 1 | Its projection returns `GetSchemaResponse` and its override signature names `GetSchemaRequest`, both introduced by Task 1 |
| P19 | Signature | `callUnary(method, request, callCredentials?, actingUserToken?)` — Task 5's code block matches this order | `core.ts:130-140`; the `registerSchema` call site at `:331-335` uses the same shape. `callUnary` resolves `actingUserToken` into metadata internally (`:141`), so Task 5 adds no acting-user plumbing |
| P20 | **Consumer impact** | **`SchemaType` must be added to `src/index.ts` — it is not reachable otherwise** | `index.ts:31` exports exactly one generated type, `ClrType`, added by the array-column-mapping work. Without adding `SchemaType`, `getSchema`'s return type cannot be named by a consumer |

## Tasks

### Task 1: Proto surface and regenerated stubs

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto`
- Modify: `Iverson.Clients/Go/generated/object_mapping.pb.go`, `object_mapping_grpc.pb.go`
- Modify: `Iverson.Clients/Python/iverson_client/generated/object_mapping_pb2.py`, `object_mapping_pb2_grpc.py`, `object_mapping_pb2.pyi`
- Modify: `Iverson.Clients/TypeScript/generated/object_mapping.ts`

**Interfaces — Produces:** `GetSchemaRequest`, `GetSchemaResponse`, `SchemaType`, `SchemaField`, `SchemaRelation`, `SchemaEnrichmentKind` in all five languages. Every other task consumes these.

- [ ] **Step 1: Clear the pre-existing stub drift first, in its own commit.**
The committed Go and TypeScript stubs lag `object_search.proto` by doc-comment content added at `71b59f7`. Regenerating for `GetSchema` would otherwise sweep that unrelated churn into this task's commit.

```bash
(cd Iverson.Clients/Go         && bash scripts/generate_protos.sh)
(cd Iverson.Clients/Python     && bash scripts/generate_protos.sh)
(cd Iverson.Clients/TypeScript && bash scripts/generate_protos.sh)
git add Iverson.Clients/Go/generated Iverson.Clients/TypeScript/generated Iverson.Clients/Python/iverson_client/generated
git commit -m "regenerate go and typescript stubs to match proto comments"
```

If this produces no diff, skip the commit — the drift may already have been cleared.

- [ ] **Step 2: Add the RPC to the existing service block.**
After `RegisterSchema`, inside `service ObjectMappingService`:

```proto
    rpc GetSchema      (GetSchemaRequest)     returns (GetSchemaResponse);
```

- [ ] **Step 3: Add the messages and the enum**, verbatim from spec §1 — `GetSchemaRequest`, `GetSchemaResponse`, `SchemaType`, `SchemaField`, `SchemaRelation`, `SchemaEnrichmentKind`. Place them after the schema-registration block, following the file's existing comment-banner style. `ClrType` and `RelationKind` are referenced, not redefined.

- [ ] **Step 4: Regenerate the three committed stub sets.**
```bash
(cd Iverson.Clients/Go         && bash scripts/generate_protos.sh)
(cd Iverson.Clients/Python     && bash scripts/generate_protos.sh)
(cd Iverson.Clients/TypeScript && bash scripts/generate_protos.sh)
```

- [ ] **Step 5: Prove .NET and Java generation.** Neither commits generated artifacts — .NET generates from `<Protobuf Include>` and Java from `protobuf-maven-plugin`, so a successful compile is the only evidence they picked up the new messages.
```bash
dotnet build Iverson.Clients/DotNet/Iverson.Client.Contracts
mvn -f Iverson.Clients/Java/pom.xml -DskipTests compile
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Clients/Go/generated Iverson.Clients/Python/iverson_client/generated Iverson.Clients/TypeScript/generated
git commit -m "add GetSchema RPC and schema-catalog messages to object_mapping proto"
```

---

### Task 2: Server — `SqlTypeToClr` inverse and the `GetSchema` override

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces — Consumes:** Task 1's generated request/response types.

- [ ] **Step 1: Add the `SqlType → ClrType` inverse to `SchemaBuilder`.**
Place it beside `SqlTypeMap`. Built from the same two source maps at static-init so it cannot drift from `ClrTypeToSql`.

```csharp
    // Inverse of ClrTypeToSql, for the GetSchema read path: a persisted ColumnDescriptor carries
    // only the SQL type string, but the catalog reports clr_type + is_array. Built from the same
    // two maps ClrTypeToSql reads, so the two cannot disagree.
    private static readonly IReadOnlyDictionary<string, (ClrType Type, bool IsArray)> SqlTypeToClrMap =
        ScalarTypeMap.Select(kv => (Sql: kv.Value.SqlType, Clr: kv.Key, IsArray: false))
            .Concat(ArrayTypeOverrides.Select(kv => (Sql: kv.Value.SqlType, Clr: kv.Key, IsArray: true)))
            .ToDictionary(x => x.Sql, x => (x.Clr, x.IsArray), StringComparer.OrdinalIgnoreCase);

    internal static (ClrType Type, bool IsArray) SqlTypeToClr(string sqlType) =>
        SqlTypeToClrMap.TryGetValue(sqlType, out var mapping)
            ? mapping
            : throw new ArgumentOutOfRangeException(nameof(sqlType), sqlType,
                $"Unhandled SQL type — add an entry to {nameof(SchemaBuilder)}.{nameof(ScalarTypeMap)}.");
```

- [ ] **Step 2: Add the `GetSchema` override**, beside `RegisterSchema` on `ObjectMappingGrpcService`, carrying **no** `[Authorize]` attribute so it inherits the ambient authenticated-user fallback.

The algorithm is necessarily two-pass: the spec's cross-type-consistency rule drops any relation whose `related_type` was itself omitted, which cannot be decided until every type has been evaluated.

Pass one, per `SchemaDescriptor` in `_registry.All.Values`:
1. `var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);`
2. `decision.Denied` → omit the type entirely.
3. Candidate fields = `KeyColumn` prepended to `ScalarColumns`. **Do not add `FkColumns`** — every FK property is already a scalar column, and only the `ColumnDescriptor` form carries the `SqlType` and `IsNullable` the projection needs. When `decision.AllowedFields` is non-null, intersect on column name.
4. Empty field set → omit the type. This is a guard, not a live path: the evaluator always retains the key column in `AllowedFields` (`RowFieldAuthorizationEvaluator.cs:65,71`) and the candidate set always contains it, so the intersection cannot empty for a type the evaluator did not deny. Keep the check so a future change to either side fails closed rather than emitting a nameless type.
5. Project the surviving fields (Step 3) and record the type name as surviving.

Pass two: for each surviving type, emit its `Relations` entries, dropping any whose `RelatedTypeName` is not in the surviving set.

`OwnershipRequired` is ignored by design — it constrains which rows a caller sees, not which fields exist.

- [ ] **Step 3: Implement the field projection.**
Per column, from `SchemaDescriptor`:

| `SchemaField` | Source |
|---|---|
| `name` | `ColumnDescriptor.Name` |
| `description` | `FieldDescriptions[name]`, empty string when absent |
| `clr_type`, `is_array` | `SqlTypeToClr(col.SqlType)` |
| `is_key` | `name == KeyColumn.Name` |
| `is_nullable` | `ColumnDescriptor.IsNullable` |
| `is_metadata` | `MetadataColumns.Contains(name)` |
| `is_search_key`, `search_key_order` | membership and **index** in `SearchKeyColumns` — the index is the rank |
| `is_embedding`, `is_chunk` | `VectorFields`/`ChunkFields` match on `PropertyName` |
| `enrichment` | every `EnrichmentTargets` entry whose `ColumnName` matches, mapped to `SchemaEnrichmentKind` — a column with two or three targets reports all of them |

`SchemaType.description` comes from `SchemaDescriptor.Description`, which is `string?`; coalesce to empty string, since proto3 string fields reject null.

- [ ] **Step 4: Add the type-recovery test** to `SchemaBuilderTests.cs`. Table-driven over `Enum.GetValues<ClrType>()`, asserting for every `ClrType` that `SqlTypeToClr(ClrTypeToSql(t, isArray: false))` returns `(t, false)` and `SqlTypeToClr(ClrTypeToSql(t, isArray: true))` returns `(t, true)`.

Driving the enum rather than the maps is deliberate: the maps are `private static readonly` and unreachable from tests even with `InternalsVisibleTo`, and a newly added `ClrType` with no `ScalarTypeMap` entry fails here through `ClrTypeToSql`'s `ArgumentOutOfRangeException`, whereas iterating a map would silently skip it.

- [ ] **Step 5: Add the projection and filtering tests** to `ObjectMappingGrpcServiceTests.cs`, following that file's existing NSubstitute mock pattern:
  1. An unrestricted caller receives every registered type with every field.
  2. A denied type is **omitted**, asserted on absence of the name — returning the name is itself the disclosure.
  3. A caller with a restricted `AllowedFields` sees only those fields, **and** the excluded field's `description` appears nowhere in the response. Asserted separately rather than riding on the field-list check, since the description leak is the specific thing this prevents.
  4. A relation whose `related_type` was omitted is dropped from the surviving type.
  5. Flag composition: a field declared both `metadata` and `search_key` reports both, and a field carrying both a `Summary` and a `Keywords` enrichment target reports both kinds.

- [ ] **Step 6: Run the tests**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "return the authorized schema catalog from a new GetSchema RPC"
```

---

### Task 3: .NET `SchemaCatalogClient`

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaCatalogClient.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaCatalogClientTests.cs`

**Interfaces — Consumes:** Task 1's generated types.

- [ ] **Step 1: Create `SchemaCatalogClient`.**
A small non-generic type taking the mapping client, mirroring `SchemaRegistrar`'s constructor shape (`SchemaRegistrar.cs:14-17`). It binds the acting user at construction and applies `WithActingUser` itself, so the method signature stays entity- and identity-free.

```csharp
public sealed class SchemaCatalogClient(
    ObjectMappingService.ObjectMappingServiceClient mapping,
    Func<Task<string>>? actingUserTokenProvider = null)
{
    public async Task<IReadOnlyList<SchemaType>> GetSchemaAsync(
        string traceId = "", CancellationToken ct = default)
    {
        var headers = new Metadata();
        if (actingUserTokenProvider is not null)
            headers.WithActingUser(await actingUserTokenProvider());

        var response = await mapping.GetSchemaAsync(
            new GetSchemaRequest { TraceId = traceId }, headers, cancellationToken: ct);

        return response.Types;
    }
}
```

With no provider configured no acting-user header is sent, and the server returns an empty catalog — an authorization outcome, not an empty registry.

- [ ] **Step 2: Register it and add the DI parameter.**
In `ServiceCollectionExtensions.AddIversonClient`, add `Func<Task<string>>? actingUserTokenProvider = null` **after** `dataPlaneTokenProvider` and **before** the trailing `params Assembly[] entityAssemblies`. Both existing callers keep binding as before — one passes everything by name, the other passes three positionally then names `entityAssemblies:`.

Register beside `SchemaRegistrar` (`:76`):
```csharp
        services.AddSingleton(sp => new SchemaCatalogClient(
            sp.GetRequiredService<ObjectMappingService.ObjectMappingServiceClient>(),
            actingUserTokenProvider));
```

- [ ] **Step 3: Add tests**, following `Iverson.Client.Core.Tests`'s existing mock-stub pattern: the call issues `GetSchema` against a mock stub and surfaces the returned types; and a client constructed **with** a provider reaches the stub carrying the `x-acting-user-authorization` metadata key.

- [ ] **Step 4: Run the tests**
```bash
dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Core/SchemaCatalogClient.cs Iverson.Clients/DotNet/Iverson.Client.Core/ServiceCollectionExtensions.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaCatalogClientTests.cs
git commit -m "add SchemaCatalogClient to the dotnet client"
```

---

### Task 4: Java `getSchema`

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`

**Interfaces — Consumes:** Task 1's generated types.

- [ ] **Step 1: Add the method** to `IversonClient`, using the existing `mappingStub` field (`:31`), which already carries whatever call credentials the client was constructed with (`:71`):

```java
    public List<SchemaType> getSchema(String traceId) {
        GetSchemaResponse response = mappingStub.getSchema(
            GetSchemaRequest.newBuilder().setTraceId(traceId).build());
        return response.getTypesList();
    }
```

- [ ] **Step 2: Add a package-private test seam.**
The suite mocks the gRPC stub and hands it to the class under test, but `IversonClient` builds its own stubs from a `ManagedChannel` and `mappingStub` is `final`. Add a fifth constructor beside the four public ones — package-private, so only the same-package tests can reach it:

```java
    /**
     * Test seam: builds a client over a pre-made mapping stub, bypassing channel construction.
     * The channel and the other three stubs are null, so a client built this way serves only
     * mapping calls and must not be closed.
     */
    IversonClient(ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mappingStub) {
        this.channel         = null;
        this.mappingStub     = mappingStub;
        this.persistenceStub = null;
        this.retrievalStub   = null;
        this.searchStub      = null;
    }
```

`close()` dereferences `channel`, so the test must not call it — hence the Javadoc note. All five fields are `final` and must be assigned in every constructor.

- [ ] **Step 3: Add a test** using that constructor with a Mockito-mocked `ObjectMappingServiceBlockingStub`, matching `SchemaRegistrarTest`'s style: the method issues `GetSchema` and surfaces the returned types.

- [ ] **Step 4: Run the tests**
```bash
mvn -f Iverson.Clients/Java/pom.xml test
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/IversonClient.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java
git commit -m "add getSchema to the java client"
```

---

### Task 5: TypeScript `getSchema`

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/core.ts`
- Test: `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

**Interfaces — Consumes:** Task 1's generated types.

- [ ] **Step 1: Add the method** to `IversonClient` (class at `core.ts:530`), using its `_mappingClient` field (`:531`) through the existing `callUnary` helper (`:130`), which already resolves `_actingUserToken` into metadata:

```ts
    async getSchema(traceId = ''): Promise<SchemaType[]> {
        const response = await callUnary<GetSchemaRequest, GetSchemaResponse>(
            (req, metadata, options, cb) => this._mappingClient.getSchema(req, metadata, options, cb),
            { traceId },
            this._callCredentials,
            this._actingUserToken,
        );
        return response.types;
    }
```

- [ ] **Step 2: Export `SchemaType`** from `src/index.ts`. It is required, not conditional: `index.ts:31` currently exports exactly one generated type — `export { ClrType } from '../generated/object_mapping.js';` — so a consumer cannot otherwise name this method's return type. Add `SchemaType` (and `SchemaField`/`SchemaRelation` if the tests reference them) to that same export.

- [ ] **Step 3: Add a test** following the suite's existing mock-stub pattern.

- [ ] **Step 4: Run the tests and build**
```bash
cd Iverson.Clients/TypeScript && npm test && npm run build
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/src/index.ts Iverson.Clients/TypeScript/tests/schema-registrar.test.ts
git commit -m "add getSchema to the typescript client"
```

---

### Task 6: Python `get_schema`

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Test: `Iverson.Clients/Python/tests/test_auth.py`

**Interfaces — Consumes:** Task 1's generated types.

- [ ] **Step 1: Add the method** to `IversonClient`, using `self._mapping_stub` (`core.py:586`). The channel already carries the acting-user token installed at construction, so no per-call plumbing is needed:

```python
    def get_schema(self, trace_id: str = "") -> list[mapping_pb.SchemaType]:
        """Return the catalog of registered types this identity may read."""
        response = self._mapping_stub.GetSchema(
            mapping_pb.GetSchemaRequest(trace_id=trace_id)
        )
        return list(response.types)
```

- [ ] **Step 2: Add a test** following the suite's existing mock-stub pattern.

- [ ] **Step 3: Run the tests**
```bash
cd Iverson.Clients/Python && python3 -m pytest -q
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/tests/test_auth.py
git commit -m "add get_schema to the python client"
```

---

### Task 7: Go `GetSchema`

**Files:**
- Modify: `Iverson.Clients/Go/iverson/coordinator.go`
- Test: `Iverson.Clients/Go/iverson_test/registrar_test.go`

**Interfaces — Consumes:** Task 1's generated types.

- [ ] **Step 1: Add the method** to `IversonClient`, using the exported `MappingStub` field (`coordinator.go:67`). Identity travels on the context, so the caller attaches it with `WithActingUserToken(ctx, …)`:

```go
// GetSchema returns the catalog of registered types the calling identity may read.
// Attach an acting user with WithActingUserToken(ctx, token) — without one the
// server returns an empty catalog.
func (c *IversonClient) GetSchema(ctx context.Context, traceID string) ([]*pb.SchemaType, error) {
	resp, err := c.MappingStub.GetSchema(ctx, &pb.GetSchemaRequest{TraceId: traceID})
	if err != nil {
		return nil, fmt.Errorf("GetSchema: %w", err)
	}
	return resp.Types, nil
}
```

- [ ] **Step 2: Add a test** following the suite's existing mock-stub pattern.

- [ ] **Step 3: Run the tests**
```bash
cd Iverson.Clients/Go && go test -count=1 ./...
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Clients/Go/iverson/coordinator.go Iverson.Clients/Go/iverson_test/registrar_test.go
git commit -m "add GetSchema to the go client"
```

## Tasks NOT in this plan

Inherited from the spec's "Out of scope":

The MCP server (`docs/specs/2026-07-22-mcp-server-design.md`) — it is a separate, already-specified consumer of this RPC and is not absorbed here. Any richer query surface beyond the six search RPCs that already exist. A single-type request filter. Persisting `is_array` or the declared `SearchKeyOrder` value to improve response fidelity.
