# Metadata Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Model selection:** Dispatch every task subagent on Opus 5 at low reasoning effort (`model: opus`). Drop to a lower tier only when certain it suffices for that specific task; when in doubt, stay on Opus 5.

**Source spec:** `docs/specs/2026-07-26-metadata-foundation-design.md` (commit SHA: `96f616e`)

**Goal:** Let types declare metadata fields and type/field descriptions in all five client languages; persist both in the schema registry; denormalize metadata onto Qdrant chunk points; make chunk metadata filterable via `SearchChunks`; fix the non-string payload round-trip bug.

**Architecture:** Additive proto fields (`is_metadata`, `description`) flow through each client's declaration idiom into `SchemaBuilder` → `SchemaDescriptor` (three new defaulted members). `IntelligenceStoreConsumer` denormalizes metadata values onto chunk points (owner field excluded); `BuildChunksFilter` gains metadata-column EQUALS clauses; `IntelligenceVectorService` result mapping widens to canonical strings.

**Tech stack:** .NET 9 / Grpc.Tools build-time codegen (C# server+client), Qdrant.Client 1.18.1, Java (Maven), Python (pytest, committed `generated/`), Go, TypeScript (vitest).

---

## File Structure

**Modify (server):**
- `Iverson.Clients/Common/Proto/object_mapping.proto` — new fields 17/18 (PropertyDescriptor), 6 (TypeDescriptor)
- `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` — `MetadataColumns`, `Description`, `FieldDescriptions`
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — populate new members; validation rejections
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — chunk denorm loop
- `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs` — canonical-string payload mapping
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — `BuildChunksFilter` extension

**Modify (clients, one task each):**
- DotNet: `Iverson.Clients/DotNet/Iverson.Client.Attributes/` (2 new attribute files), `Iverson.Client.Core/SchemaRegistrar.cs`
- Java: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/` (2 new), `core/SchemaRegistrar.java`
- Python: `Iverson.Clients/Python/iverson_client/core.py` (+ regenerated `generated/`)
- Go: `Iverson.Clients/Go/iverson/tags.go`, `registrar.go` (+ regenerated pb)
- TypeScript: `Iverson.Clients/TypeScript/src/annotations.ts`, `core.ts` (+ regenerated protos)

**Test files (all existing, extended):** `Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`, `Schema/SchemaRegistryTests.cs`, `Consumers/IntelligenceStoreConsumerTests.cs`, `Grpc/ObjectSearchGrpcServiceTests.cs`, `Iverson.Vector.Tests/QdrantVectorServiceTests.cs`, `Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`, Java `AnnotationTest.java`/`SchemaRegistrarTest.java`, Python `tests/test_schema_registrar.py`, Go `iverson_test/tags_test.go`+`registrar_test.go`, TS `tests/annotations.test.ts`+`schema-registrar.test.ts`.

## Inherited from spec

The spec's 12 verified assumptions are trusted as ground truth (see its "Verified assumptions" section): free proto field numbers; `SchemaBuilder.Build` as single mapping point; registry legacy-JSON tolerance of defaulted members; `ToQdrantValue` coverage; chunk-block payload scope; filter-builder arbitrary-key support; StringValue-only result mapping (both methods); five extendable client mechanisms; absent schema-read RPC; key/tenant payload presence; additive-member safety across all 16 descriptor consumers; `BuildChunksFilter` PK-only restriction.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | Path | Server consumes the proto via `Iverson.Client.Contracts` build-time codegen — no committed C# generated code to update | Iverson.Api.csproj:37 project-references Contracts; Contracts.csproj:17 `<Protobuf Include="../../Common/Proto/*.proto" GrpcServices="Both" />` |
| 2 | Path | Server test files exist to extend | Api.Tests/Schema/SchemaBuilderTests.cs, SchemaRegistryTests.cs; Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs; Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs; Vector.Tests/QdrantVectorServiceTests.cs (dir listings) |
| 3 | Command | Server + DotNet client tests run via `dotnet test` on the respective test csproj | test csprojs present in both trees |
| 4 | Command | Codegen: Python/Go/TS via `scripts/generate_protos.sh` (TS also `npm run generate`); Java via protoc plugin in `client/pom.xml`; Python `generated/` is committed | script paths + TS package.json:16 + Java pom grep; Python `iverson_client/generated/object_mapping_pb2.py` committed |
| 5 | Command | Client test commands: TS `npm test` (`vitest run`, package.json:15); Java `mvn test` in `Iverson.Clients/Java/client`; Python `pytest` in `Iverson.Clients/Python`; Go `go test ./...` in `Iverson.Clients/Go` | package.json, pom.xml, tests/ and iverson_test/ dirs |
| 6 | Code validity | Qdrant `Value` oneof exposes `KindCase` with `StringValue`/`IntegerValue`/`DoubleValue`/`BoolValue` in Qdrant.Client 1.18.1 | nuget XML doc members `Value.DoubleValue`, `Value.IntegerValueFieldNumber`, … |
| 7 | Consumer impact | Widening payload mapping to canonical strings breaks no consumer: SearchSimilar maps via `Value.ForString(kvp.Value)` (ObjectSearchGrpcService.cs:212); chunk path reads only `text`/`parent_id` (:303-304) | reads cited |
| 8 | Consumer impact | `BuildChunksFilter` has exactly one caller (SearchChunks, ObjectSearchGrpcService.cs:256); `SchemaBuilder.Build` is called from SchemaRegistrationOrchestrator | grep |
| 9 | Ordering | Tasks 2–4 consume only Task 1's `SchemaDescriptor.MetadataColumns` plus pre-existing code; Tasks 5–9 consume only Task 1's proto; Tasks 2–9 are mutually independent | file structure above; no shared new symbols across 2–9 |
| 10 | Convention | Commit messages use `type(scope): summary` | git log (`fix(ts-client): …`, `docs(specs): …`) |
| 11 | Path | Per-language declaration sites and search-key precedents | DotNet `IversonSearchKeyAttribute.cs`; Java `annotations/IversonSearchKey.java`; Python core.py:134-155 (`meta["search_keys"]` → `is_search_key` on wire); Go tags.go:6-12 tag grammar + ParseTag:54; TS src/annotations.ts |
| 12 | Code validity | Go `FieldMeta.Kind` is single-valued ("one of the Kind* constants", tags.go:42-43) and entities reach the registrar as bare struct values (registrar.go:28) — so Task 8 models metadata as a new mutually-exclusive Kind, field descriptions as a separate `iverson_desc` tag, and the type-level description as an optional `IversonDescription() string` interface | reads cited |
| 13 | Code validity | `IntelligenceFilterBuilder.BuildEqualityCondition` is `private static` in the Iverson.Vector assembly (IntelligenceFilterBuilder.cs:95) — Task 4 therefore adds the public `MatchEquality` wrapper rather than calling it directly | read cited |

## Tasks

### Task 1: Proto fields + server schema model + validation

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`, `SchemaRegistryTests.cs`

**Interfaces:**
- Produces: proto fields `PropertyDescriptor.is_metadata=17`, `PropertyDescriptor.description=18`, `TypeDescriptor.description=6` (Tasks 5–9); `SchemaDescriptor.MetadataColumns` / `Description` / `FieldDescriptions` (Tasks 2, 4).

- [ ] **Step 1: Write failing tests** in `SchemaBuilderTests.cs` following the file's existing patterns: (a) `is_metadata`+`description` on a scalar property and a type-level description map into `MetadataColumns`/`FieldDescriptions`/`Description`; (b) `is_metadata` on an embedding, chunk, `is_array`, and `is_large_field` property each throws `InvalidOperationException` naming the property; (c) in `SchemaRegistryTests.cs`: deserializing descriptor JSON lacking the new members yields empty `MetadataColumns`/`FieldDescriptions` and null `Description`.

- [ ] **Step 2: Proto change** — append to `PropertyDescriptor`:
```proto
    bool   is_metadata = 17;  // [IversonMetadata] present — denormalized onto chunk points
    string description = 18;  // [IversonDescription] text; empty = none
```
and to `TypeDescriptor`: `string description = 6;` (comment: type-level `[IversonDescription]`).

- [ ] **Step 3: Extend `SchemaDescriptor`** with defaulted members (same legacy-JSON rationale as the file's existing TenantColumn comment):
```csharp
    public HashSet<string>            MetadataColumns   { get; init; } = [];
    public string?                    Description       { get; init; }
    public Dictionary<string, string> FieldDescriptions { get; init; } = [];
```

- [ ] **Step 4: Extend `SchemaBuilder.Build`** — in the per-property loop collect `metadataColumns` (OrdinalIgnoreCase) and `fieldDescriptions`; after the loop, throw `InvalidOperationException` if any metadata-flagged property is an embedding/chunk/array/large-field (mirror the existing search-key conflict message style at :75-79); set the three members plus `Description = typeDesc.Description` (empty → null) in the returned descriptor.

- [ ] **Step 5: Run** `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~Schema"` — all green.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Server/Iverson.Api/Schema/ Iverson.Server/Iverson.Api.Tests/Schema/
git commit -m "feat(schema): metadata flags and descriptions in proto, SchemaDescriptor, SchemaBuilder"
```

### Task 2: Chunk-point metadata denormalization

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` (chunk payload construction, ~:193-201)
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

**Interfaces:**
- Consumes: `SchemaDescriptor.MetadataColumns` (Task 1).

- [ ] **Step 1: Write failing tests**: (a) schema with a metadata-flagged scalar → chunk points' payload contains `fieldName.ToCamelCase()` with the typed value; (b) object-level point payload is unchanged by the flag; (c) schema whose owner field is metadata-flagged → chunk payload owner key holds the authoritative (Postgres-derived) value, not the event-payload value; (d) a metadata property named `Text` leaves the chunk's reserved `text` payload entry (the passage text) intact.

- [ ] **Step 2: Implement** — after the existing `chunkPayload` construction, per metadata column:
```csharp
foreach (var name in schema.MetadataColumns)
{
    if (ownerField is not null && string.Equals(name, ownerField, StringComparison.OrdinalIgnoreCase))
        continue; // authoritative owner write above covers this key (CSR #7)
    var camelKey = name.ToCamelCase();
    if (camelKey is "text" or "parent_id" or "field" or "chunk_index")
        continue; // reserved chunk payload keys must not be clobbered by metadata
    var sqlType = schema.ScalarColumns.FirstOrDefault(c =>
        string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.SqlType ?? "TEXT";
    var val = ExtractTypedValue(payload, name, sqlType);
    if (val is not null) chunkPayload[name.ToCamelCase()] = val;
}
```

- [ ] **Step 3: Run** `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~IntelligenceStoreConsumer"` — green.

- [ ] **Step 4: Commit** — `feat(consumer): denormalize metadata columns onto Qdrant chunk points`

### Task 3: Canonical-string payload result mapping

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs` (:80, :112 + new helper)
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs`

- [ ] **Step 1: Write failing test**: payload with integer/double/bool/string values round-trips through the result mapping as `"42"` / `"3.5"` / `"true"` / original string (mock/fake per the file's existing conventions).

- [ ] **Step 2: Implement** private helper and use at both sites:
```csharp
private static string ToCanonicalString(Value v) => v.KindCase switch
{
    Value.KindOneofCase.StringValue  => v.StringValue,
    Value.KindOneofCase.IntegerValue => v.IntegerValue.ToString(CultureInfo.InvariantCulture),
    Value.KindOneofCase.DoubleValue  => v.DoubleValue.ToString(CultureInfo.InvariantCulture),
    Value.KindOneofCase.BoolValue    => v.BoolValue ? "true" : "false",
    _                                => v.ToString()
};
```

- [ ] **Step 3: Run** `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj` — green.

- [ ] **Step 4: Commit** — `fix(vector): map non-string Qdrant payload values to canonical strings in search results`

### Task 4: BuildChunksFilter metadata clauses

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`BuildChunksFilter`, :576-585), `Iverson.Server/Iverson.Vector/IntelligenceFilterBuilder.cs` (new public wrapper), and the stale restriction comment at `Iverson.Clients/Common/Proto/object_search.proto:112`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `SchemaDescriptor.MetadataColumns` (Task 1).

- [ ] **Step 1: Write failing tests**: EQUALS clause on a metadata column is accepted (translated to a payload condition on the camelCase key); PK EQUALS clause still accepted; clause on a non-metadata, non-key property still throws `InvalidArgument`; multiple clauses mixing PK + metadata accepted.

- [ ] **Step 2: Implement** — first add a public wrapper to `IntelligenceFilterBuilder` (its `BuildEqualityCondition` is `private static` in the Iverson.Vector assembly and unreachable from Iverson.Api):
```csharp
public static Condition MatchEquality(string property, SearchValue value) =>
    BuildEqualityCondition(property, value);
```
Then rework `BuildChunksFilter`: for each clause, PK property keeps its existing translation; a property in `schema.MetadataColumns` (OrdinalIgnoreCase) becomes `IntelligenceFilterBuilder.MatchEquality(property.ToCamelCase(), clause.Value)`; anything else throws the (updated) `InvalidArgument` message. Drop the count>1 rejection. Update the proto comment to describe the new contract. (`MatchEquality`'s `FilterTranslationException` on unsupported value kinds surfaces through the existing SearchChunks error handling.)

- [ ] **Step 3: Run** `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~ObjectSearchGrpcService"` — green.

- [ ] **Step 4: Commit** — `feat(search): allow metadata-column EQUALS filters in SearchChunks`

### Task 5: C# client attributes

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Attributes/IversonMetadataAttribute.cs`, `IversonDescriptionAttribute.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/SchemaRegistrarTests.cs`

- [ ] **Step 1: Write failing tests** mirroring the existing `IversonSearchKey` registrar tests: `[IversonMetadata]` sets `is_metadata`; `[IversonDescription("…")]` on a property sets `description`; on the class sets `TypeDescriptor.description`.
- [ ] **Step 2: Implement** the two attributes (pattern-match `IversonSearchKeyAttribute.cs`; `IversonDescriptionAttribute` targets Class | Property with a string ctor arg) and map them in `SchemaRegistrar` where `IsSearchKey` is mapped.
- [ ] **Step 3: Run** `dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj` — green (proto regeneration is automatic via Grpc.Tools).
- [ ] **Step 4: Commit** — `feat(dotnet-client): IversonMetadata and IversonDescription attributes`

### Task 6: Java client annotations

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/annotations/IversonMetadata.java`, `IversonDescription.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java`
- Test: `.../test/java/io/iverson/client/annotations/AnnotationTest.java`, `.../core/SchemaRegistrarTest.java`

- [ ] **Step 1: Write failing tests** mirroring the `IversonSearchKey` cases (annotation presence → wire fields; class-level description → `TypeDescriptor.description`).
- [ ] **Step 2: Implement** annotations (pattern-match `IversonSearchKey.java`; `IversonDescription` targets TYPE and FIELD with a `String value()`), map in `SchemaRegistrar` (proto classes regenerate via the pom's protoc plugin at build).
- [ ] **Step 3: Run** `cd Iverson.Clients/Java/client && mvn test` — green.
- [ ] **Step 4: Commit** — `feat(java-client): IversonMetadata and IversonDescription annotations`

### Task 7: Python client

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py`; regenerate `iverson_client/generated/` via `bash scripts/generate_protos.sh`
- Test: `Iverson.Clients/Python/tests/test_schema_registrar.py`

- [ ] **Step 1: Regenerate protos** (`bash scripts/generate_protos.sh` from `Iverson.Clients/Python`) and commit-stage the regenerated files with the task.
- [ ] **Step 2: Write failing tests** mirroring the search-key registrar cases: metadata/description declared via the field-descriptor `meta` mechanism (core.py:134-155 pattern) populate `is_metadata`/`description`; type-level description populates `TypeDescriptor.description`.
- [ ] **Step 3: Implement** in `core.py`, following exactly how `search_keys` flows from `meta` into `PropertyDescriptor` kwargs.
- [ ] **Step 4: Run** `cd Iverson.Clients/Python && pytest` — green.
- [ ] **Step 5: Commit** — `feat(python-client): metadata and description declarations`

### Task 8: Go client

**Files:**
- Modify: `Iverson.Clients/Go/iverson/tags.go`, `iverson/registrar.go`; regenerate pb via `bash scripts/generate_protos.sh`
- Test: `Iverson.Clients/Go/iverson_test/tags_test.go`, `registrar_test.go`

- [ ] **Step 1: Regenerate protos** (`bash scripts/generate_protos.sh` from `Iverson.Clients/Go`).
- [ ] **Step 2: Write failing tests**: `iverson:"metadata"` tag (new `KindMetadata` — Go's `FieldMeta.Kind` is single-valued per tags.go:42-43, so metadata is mutually exclusive with other kinds, matching the client's existing tag model) → `IsMetadata`; new `iverson_desc:"…"` struct tag on a field (independent of Kind) → `Description`; type-level description via an optional interface `interface{ IversonDescription() string }` implemented by the entity struct → `TypeDescriptor.description`.
- [ ] **Step 3: Implement** — extend the tag grammar in `tags.go` (documented list at :6-12 and `ParseTag` at :54), add `Description` to `FieldMeta`, check the optional interface in `registrar.go` where `TypeDescriptor` is built (:112), and map both onto the proto.
- [ ] **Step 4: Run** `cd Iverson.Clients/Go && go test ./...` — green.
- [ ] **Step 5: Commit** — `feat(go-client): metadata and description tags`

### Task 9: TypeScript client

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/annotations.ts`, `src/core.ts`, `src/index.ts` (exports); regenerate via `npm run generate`
- Test: `Iverson.Clients/TypeScript/tests/annotations.test.ts`, `tests/schema-registrar.test.ts`

- [ ] **Step 1: Regenerate protos** (`cd Iverson.Clients/TypeScript && npm run generate`).
- [ ] **Step 2: Write failing tests** mirroring the search-key decorator cases: `@IversonMetadata()` → `is_metadata`; `@IversonDescription("…")` on property and class → the two description fields.
- [ ] **Step 3: Implement** decorators in `annotations.ts` (pattern-match the existing search-key decorator, reflect-metadata storage) and map in `core.ts`'s registrar; export from `index.ts`.
- [ ] **Step 4: Run** `cd Iverson.Clients/TypeScript && npm test` — green.
- [ ] **Step 5: Commit** — `feat(ts-client): IversonMetadata and IversonDescription decorators`

## Tasks NOT in this plan

Inherited verbatim from the spec's "Out of scope": Ingest enrichment via Ollama, tensor re-ranking/fusion, derived vector signals, agent-facing schema retrieval, and any metadata-only (non-column) fields.
