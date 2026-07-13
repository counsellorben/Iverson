# Row/Field-Level Authorization — Foundation (Part 5a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/superpowers/specs/2026-07-13-row-field-authorization-foundation-design.md` (commit SHA: `4d7e5ef`)

**Goal:** Give schema registration authorization metadata (owner field + role→permission rules), give acting-user tokens a `groups` claim, and build a shared store-agnostic evaluator that later plans (5b Postgres, 5c StarRocks, 5d Qdrant) will consume — with zero enforcement wired into any live RPC yet.

**Architecture:** New `AuthorizationRules` proto/C# metadata attaches per-`TypeDescriptor` and flows through the existing `SchemaBuilder`/`SchemaRegistry` pipeline unchanged. A new pure-function `IRowFieldAuthorizationEvaluator` (schema + acting-user principal + action → declarative decision) lives alongside the existing `OperatorAuthorizationPolicy`. Authentik's `iverson-loadtest-human` provider gains the same `groups` scope-mapping binding `iverson-oidc-default` already has.

**Tech stack:** .NET/C# (Iverson.Api), protobuf, Authentik (YAML blueprints), Python (token-minting script), xUnit/FluentAssertions/NSubstitute/TestContainers (existing test stack).

---

## File Structure

- **Create:**
  - `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs` — interface, `AuthorizationAction` enum, `AuthorizationDecision` record
  - `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs` — implementation
  - `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs` — unit tests
  - `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs` — TestContainers integration test + composite fixture
- **Modify:**
  - `Iverson.Clients/Common/Proto/object_mapping.proto` — new messages + `TypeDescriptor.authorization` field
  - `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` — new records + `SchemaDescriptor.Authorization` property
  - `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — map `typeDesc.Authorization` into the built descriptor
  - `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — `owner_field` validation in `RegisterSchema`
  - `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — new validation test
  - `Iverson.Server/Iverson.Api/Program.cs` — DI registration
  - `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` — `groups` scope binding
  - `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml` — same
  - `Iverson.Server/deploy/scripts/mint_acting_user_token.py` — request `groups` scope + assert its presence

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are trusted as ground truth here (see the spec's own "Verified assumptions" section for full evidence):

- `AuthorizationRules` attaches to proto `TypeDescriptor` (each of `root_type` + every dependent produces its own independent `SchemaDescriptor` via `SchemaBuilder.BuildDescriptor`, looped per-type in `ObjectMappingGrpcService.RegisterSchema`).
- `SchemaDescriptor` round-trips through `System.Text.Json` via `SchemaRegistry`; a new non-`required` property is backward-compatible with old persisted rows.
- All three provisioning methods (`PostgresSchemaManager.ApplySchemaAsync`, `StarRocksSchemaManager.ApplyTableAsync`, `QdrantCollectionManager.ApplyCollectionAsync`) are structurally inert to the new `Authorization` property — none receives `SchemaDescriptor` directly; each receives a narrower projection that only copies specific named fields.
- `groups` is the exact claim name already in production use (`OperatorAuthorizationPolicy.IsSatisfiedBy`, `Program.cs:140`).
- `IActingUserAccessor.ActingUser` is `ClaimsPrincipal?`.
- `iverson-loadtest-human` (compose-only blueprint) has no `property_mappings` today; `iverson-oidc-default` already binds `groups`. `mint_acting_user_token.py` requests `scope: "openid"` only.
- TestContainers is an established pattern in this repo (per-store integration test projects already use it).
- `RegisterSchema`'s only existing validation-error convention is `throw RpcException(new Status(StatusCode.InvalidArgument, ...))` (the `root_type is required` check) — `RelationValidator` is not called from `RegisterSchema` and is not this RPC's precedent.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | `Iverson.Clients/Common/Proto/object_mapping.proto`, `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`, `Schema/SchemaBuilder.cs`, `Grpc/ObjectMappingGrpcService.cs`, `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` all exist; `TypeDescriptor`'s existing fields use numbers 1-3, so field number 4 is free for `authorization` | Direct file check, all 5 present; proto read confirms `TypeDescriptor { type_name=1; properties=2; relations=3; }` |
| 2 | Function signature | `ColumnDescriptor` is `record ColumnDescriptor(string Name, string SqlType, bool IsNullable)` — usable for matching `owner_field` against `SchemaDescriptor.ScalarColumns[].Name` | `SchemaDescriptor.cs:20` |
| 3 | Consumer impact (Cat 6) | All 5 existing `new SchemaDescriptor` call sites use object-initializer syntax (`new SchemaDescriptor { ... }`), not positional construction — unaffected by adding a new optional `Authorization` property | grep across repo: `SchemaBuilder.cs:82`, `ObjectSearchGrpcServiceTests.cs:424`, `IntelligenceStoreConsumerTests.cs:284,329`, `SchemaBuilderTests.cs:77` — all `new SchemaDescriptor` followed by object-initializer `{` |
| 4 | File path / signature | `ObjectMappingGrpcService.RegisterSchema`'s per-type loop and `root_type is required` check are exactly where the spec cites them | `ObjectMappingGrpcService.cs:49-50` (check), `:54` (loop) |
| 5 | File path | `Iverson.Server/Iverson.Api/Program.cs:171` has `AddSingleton<IRelationValidator, RelationValidator>()` as the DI-registration precedent to mirror | Read of `Program.cs:168-175` |
| 6 | File path | `Iverson.Server/Iverson.Api.Tests/OperatorAuthorizationPolicyTests.cs` exists with the `[Fact]`/FluentAssertions/`Method_Scenario_Expected` style to mirror | File read in full |
| 7 | Test/build command | `Iverson.Api.Tests.csproj` references xUnit 2.9.3, FluentAssertions 7.0.0, NSubstitute 5.3.0, `Testcontainers`/`Testcontainers.PostgreSql` 3.9.0 | `Iverson.Api.Tests.csproj:9-27` |
| 8 | Consumer impact (Cat 6) / file path | `Iverson.Api.Tests.csproj`'s `ProjectReference`s do NOT include `Iverson.Sql.Tests`/`Iverson.StarRocks.Tests`/`Iverson.Vector.Tests` — no existing combined 3-store fixture is reachable from `Iverson.Api.Tests`; the sole precedent (`QdrantGrpcContainerFixture` in `ObjectSearchVectorIntegrationTests.cs`) is itself a locally-duplicated fixture, confirming this repo's convention of duplicating fixtures per test project rather than cross-referencing test projects | `Iverson.Api.Tests.csproj:30-35`; `ObjectSearchVectorIntegrationTests.cs:19-40` |
| 9 | Code validity (lib versions) | `Iverson.StarRocks.csproj` references `MySqlConnector` 2.4.0 with no `PrivateAssets` restriction — transitively available to `Iverson.Api.Tests` via its existing `ProjectReference` to `Iverson.StarRocks` | `Iverson.StarRocks.csproj:17-18` |
| 10 | Function signature (sibling set, 3 members) | `PostgresSchemaManager(string, ILogger<...>) : IRecordStoreSchemaManager`; `StarRocksSchemaManager(string, ILogger<...>, StarRocksResilienceOptions?) : IEngagementStoreSchemaManager` (builds its own `MySqlConnectionStringBuilder` internally — the fixture only needs to pass a raw connection string); `QdrantCollectionManager(QdrantClient, ILogger<...>) : IVectorSchemaManager` — all three exactly match the interface types `ObjectMappingGrpcService`'s constructor expects | `PostgresSchemaManager.cs:8-10`, `StarRocksSchemaManager.cs:10-16`, `QdrantCollectionManager.cs:9-11` |
| 11 | Function signature | `ObjectMappingGrpcService`'s primary constructor takes 12 params: `IEntityRepository, IRecordStoreTransactionRunner, IRecordStoreSchemaManager, IVectorSchemaManager, IEventProducer, SchemaRegistry, IEmbeddingService, IEngagementStoreSchemaManager, IRelationValidator, IEntityKeyAccessor, IOutboxWriter, ILogger<...>` — only 4 need real store-backed instances for the integration test (the schema managers + a real `SchemaRegistry`); the rest can be NSubstitute fakes, matching existing unit-test convention | `ObjectMappingGrpcService.cs:23-36` |
| 12 | Function signature | `SchemaRegistryRepository(IRecordStoreQueryExecutor sql) : ISchemaRegistryRepository`; `PostgresRepository(string connectionString, ILogger<...>) : IRecordStoreQueryExecutor, IRecordStoreTransactionRunner` — composable into a real-Postgres-backed `SchemaRegistry` for the round-trip assertion | `SchemaRegistryRepository.cs:3-6`, `PostgresRepository.cs:8-10` |
| 13 | Code validity (sibling set, 2 members — **corrected during verification**) | The two Authentik blueprint files use *different* indentation for the `iverson-loadtest-human` provider block: compose-only uses 6-space `attrs:` indent (`property_mappings:` at 6, `- !Find` at 8); the Helm ConfigMap template uses 10-space `attrs:` indent (`property_mappings:` at 10, `- !Find` at 12), because it's wrapped in an extra `data:`/`blueprint.yaml` template nesting level. A single copy-pasted snippet across both files (as first drafted) would have broken the template file's YAML structure. | `blueprints/compose-only/service-clients.yaml:100-114`; `templates/blueprints-configmap-service-clients.yaml:78-93` (the sibling `iverson-oidc-default` block in the same template file, lines 68-71, confirms the 10/12-space pattern already in use) |
| 14 | File path / consumer impact (Cat 6) | `mint_acting_user_token.py:309` has `"scope": "openid"` unchanged since spec-write time; no `.cs`/`.sh` consumer invokes the script programmatically (only doc references) — it's run manually via `python3 ... --target compose\|kind` with the token captured from stdout; `log()` (`:82-83`) writes to `stderr`, so adding a `groups`-claim assertion via `log()` doesn't disturb the stdout token contract | `mint_acting_user_token.py:309` (scope), `:82-83` (`log` → stderr), repo-wide grep for `mint_acting_user_token` (only docs + the script itself reference it by name) |
| 15 | Code validity — **found during plan self-review** | Task 1's new proto messages (`Iverson.Client.Contracts.AuthorizationRules`/`RowPermission`/`FieldPermission`) collide by name with Task 1's new domain records (`Iverson.Api.Schema.AuthorizationRules`/`RowPermission`/`FieldPermission`). `SchemaBuilder.cs` already `using`s both namespaces and already resolves an identical pre-existing collision for `RelationKind` via file-level aliases | `SchemaBuilder.cs:1,6-7` (`using Iverson.Client.Contracts;` + `using ContractsRelationKind = Iverson.Client.Contracts.RelationKind; using SchemaRelationKind = Iverson.Api.Schema.RelationKind;`) |
| 16 | Code validity — **found during plan self-review** | `ObjectMappingGrpcServiceTests.cs` already `using`s both `Iverson.Client.Contracts` and `Iverson.Api.Schema` and already resolves an identical pre-existing collision (`RelationDescriptor` exists in both) via inline partial-qualification rather than a file-level alias | `ObjectMappingGrpcServiceTests.cs:1-16` (both `using`s), `:174,195` (`new Client.Contracts.RelationDescriptor { ... }`) |

## Tasks

### Task 1: Schema authorization metadata (proto + C# model + validation)

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Produces: `SchemaDescriptor.Authorization` — consumed by Task 2 (evaluator) and Task 3 (integration test)

- [ ] **Step 1: Add proto messages and the `TypeDescriptor.authorization` field**
```proto
message RowPermission {
    string role          = 1;  // free-form; matches an Authentik group name
    bool   can_read_all  = 2;  // bypasses ownership for Read
    bool   can_write_all = 3;  // bypasses ownership for Write
    bool   can_delete_all = 4; // bypasses ownership for Delete
}

message FieldPermission {
    string          field_name     = 1;
    repeated string readable_roles = 2;  // empty = inherits row-level decision
    repeated string writable_roles = 3;  // empty = inherits row-level decision
}

message AuthorizationRules {
    string                    owner_field       = 1;  // optional; empty = no ownership dimension
    repeated RowPermission    row_permissions   = 2;
    repeated FieldPermission  field_permissions = 3;
}
```
Add `AuthorizationRules authorization = 4;` to the existing `TypeDescriptor` message (field numbers 1-3 are taken; 4 is free).

- [ ] **Step 2: Add the C# model**
In `SchemaDescriptor.cs`, add:
```csharp
public sealed record SchemaDescriptor
{
    // ...existing properties unchanged...
    public AuthorizationRules? Authorization { get; init; }
}

public sealed record AuthorizationRules(
    string? OwnerField,
    IReadOnlyList<RowPermission> RowPermissions,
    IReadOnlyList<FieldPermission> FieldPermissions);

public sealed record RowPermission(string Role, bool CanReadAll, bool CanWriteAll, bool CanDeleteAll);

public sealed record FieldPermission(
    string FieldName,
    IReadOnlyList<string> ReadableRoles,
    IReadOnlyList<string> WritableRoles);
```

- [ ] **Step 3: Map the proto field in `SchemaBuilder.BuildDescriptor`**
The new proto messages (`Iverson.Client.Contracts.AuthorizationRules`/`RowPermission`/`FieldPermission`) share names with the new domain records from Step 2 (`Iverson.Api.Schema.AuthorizationRules`/`RowPermission`/`FieldPermission`) — `SchemaBuilder.cs` already `using`s both namespaces and already resolves an identical collision for `RelationKind` via file-level aliases (`SchemaBuilder.cs:6-7`: `ContractsRelationKind`/`SchemaRelationKind`). Add the same pattern for the three new types:
```csharp
using ContractsAuthorizationRules = Iverson.Client.Contracts.AuthorizationRules;
using SchemaAuthorizationRules    = Iverson.Api.Schema.AuthorizationRules;
using ContractsRowPermission      = Iverson.Client.Contracts.RowPermission;
using SchemaRowPermission         = Iverson.Api.Schema.RowPermission;
using ContractsFieldPermission    = Iverson.Client.Contracts.FieldPermission;
using SchemaFieldPermission       = Iverson.Api.Schema.FieldPermission;
```
Then, inside `BuildDescriptor`, map `typeDesc.Authorization` (a `ContractsAuthorizationRules`) to the new `SchemaDescriptor.Authorization` property — null passthrough when the proto message is unset (protobuf message fields default to a non-null-but-empty instance; treat a `ContractsAuthorizationRules` with an empty `owner_field` and zero `row_permissions`/`field_permissions` as "unset" and map to `null`), otherwise projecting each `ContractsRowPermission`/`ContractsFieldPermission` into its `Schema*` counterpart.

- [ ] **Step 4: Validate `owner_field` in `RegisterSchema`**
In `ObjectMappingGrpcService.RegisterSchema`'s per-type loop (`ObjectMappingGrpcService.cs:54`), immediately after `var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, _embedding);`, add:
```csharp
var ownerField = descriptor.Authorization?.OwnerField;
if (!string.IsNullOrEmpty(ownerField) &&
    !descriptor.ScalarColumns.Any(c => string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase)))
{
    throw new RpcException(new Status(StatusCode.InvalidArgument,
        $"owner_field '{ownerField}' on '{descriptor.TypeName}' does not match any declared scalar property."));
}
```
This mirrors the existing `root_type is required` check (`ObjectMappingGrpcService.cs:49-50`) — same exception type, same status code, same "validate before provisioning" placement.

- [ ] **Step 5: Add a unit test**
In `ObjectMappingGrpcServiceTests.cs`, add `RegisterSchema_WithInvalidOwnerField_ThrowsInvalidArgument`, mirroring `RegisterSchema_WithNullRootType_ThrowsInvalidArgument`'s shape: build a `TypeDescriptor` via the existing `SimpleType(...)` helper, set `.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "DoesNotExist" }` (this file already `using`s both `Iverson.Client.Contracts` and `Iverson.Api.Schema`, which now both declare an `AuthorizationRules` type — the `Client.Contracts.` qualification disambiguates, matching this file's existing `Client.Contracts.RelationDescriptor` convention a few lines away), assert the call throws `RpcException` with `StatusCode.InvalidArgument`.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "feat(api): add schema authorization metadata (owner field, row/field permissions)"
```

### Task 2: Shared evaluator (interface, implementation, DI, unit tests)

**Files:**
- Create: `Iverson.Server/Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs`
- Create: `Iverson.Server/Iverson.Api/Authorization/RowFieldAuthorizationEvaluator.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Create: `Iverson.Server/Iverson.Api.Tests/Authorization/RowFieldAuthorizationEvaluatorTests.cs`

**Interfaces:**
- Consumes: `SchemaDescriptor.Authorization` (Task 1)
- Produces: `IRowFieldAuthorizationEvaluator` — not called by any production RPC in this plan (5a wires zero enforcement)

- [ ] **Step 1: Create the interface and decision contract**
```csharp
public enum AuthorizationAction { Read, Write, Delete }

public interface IRowFieldAuthorizationEvaluator
{
    AuthorizationDecision Evaluate(
        SchemaDescriptor schema,
        ClaimsPrincipal? actingUser,
        AuthorizationAction action);
}

public sealed record AuthorizationDecision(
    bool Denied,
    bool OwnershipRequired,
    string? OwnerFieldName,
    string? OwnerValue,
    IReadOnlySet<string>? AllowedFields);
```

- [ ] **Step 2: Implement the decision logic**
```csharp
public sealed class RowFieldAuthorizationEvaluator : IRowFieldAuthorizationEvaluator
{
    public AuthorizationDecision Evaluate(SchemaDescriptor schema, ClaimsPrincipal? actingUser, AuthorizationAction action)
    {
        var rules = schema.Authorization;
        if (rules is null)
            return new AuthorizationDecision(true, false, null, null, null);

        if (actingUser is null)
            return new AuthorizationDecision(true, false, null, null, null);

        var userGroups = actingUser.FindAll("groups").Select(c => c.Value).ToHashSet();
        var bypass = rules.RowPermissions.Any(p => userGroups.Contains(p.Role) && action switch
        {
            AuthorizationAction.Read   => p.CanReadAll,
            AuthorizationAction.Write  => p.CanWriteAll,
            AuthorizationAction.Delete => p.CanDeleteAll,
            _ => false
        });

        bool ownershipRequired;
        string? ownerFieldName = null, ownerValue = null;

        if (bypass)
        {
            ownershipRequired = false;
        }
        else if (!string.IsNullOrEmpty(rules.OwnerField))
        {
            ownershipRequired = true;
            ownerFieldName = rules.OwnerField;
            ownerValue = actingUser.FindFirst("sub")?.Value;
        }
        else
        {
            return new AuthorizationDecision(true, false, null, null, null);
        }

        IReadOnlySet<string>? allowedFields = null;
        if (action != AuthorizationAction.Delete && rules.FieldPermissions.Count > 0)
        {
            var excluded = rules.FieldPermissions
                .Where(fp =>
                {
                    var roles = action == AuthorizationAction.Read ? fp.ReadableRoles : fp.WritableRoles;
                    return roles.Count > 0 && !roles.Any(userGroups.Contains);
                })
                .Select(fp => fp.FieldName)
                .ToHashSet();

            if (excluded.Count > 0)
            {
                var allFields = schema.ScalarColumns.Select(c => c.Name);
                allowedFields = allFields.Where(f => !excluded.Contains(f)).ToHashSet();
            }
        }

        return new AuthorizationDecision(false, ownershipRequired, ownerFieldName, ownerValue, allowedFields);
    }
}
```
(Field-level logic implements the CDR-corrected algorithm: a field is excluded from `AllowedFields` only when it has a non-empty, non-matching role list for the given action; `AllowedFields` stays `null` — unrestricted — whenever nothing ends up excluded.)

- [ ] **Step 3: Register in DI**
In `Program.cs`, add `builder.Services.AddSingleton<IRowFieldAuthorizationEvaluator, RowFieldAuthorizationEvaluator>();` alongside the other `Schema`-adjacent registrations (near `AddSingleton<IRelationValidator, RelationValidator>()` at line 171).

- [ ] **Step 4: Unit tests**
Create `RowFieldAuthorizationEvaluatorTests.cs` mirroring `OperatorAuthorizationPolicyTests.cs`'s style (`[Fact]`, FluentAssertions, `Method_Scenario_Expected` naming). Add a small inline helper (no shared fixture exists for this in the test project):
```csharp
private static ClaimsPrincipal ActingUser(string sub, params string[] groups)
{
    var claims = new List<Claim> { new("sub", sub) };
    claims.AddRange(groups.Select(g => new Claim("groups", g)));
    return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
}
```
Cover: no-rules → Denied; no-identity → Denied; owner-match → not denied, `OwnershipRequired=true`; role-bypass per action (Read/Write/Delete separately); role-present-but-no-bypass-and-no-owner-field → Denied; role-present-but-no-bypass-with-owner-field → falls to ownership; field-level entry excludes a non-matching-role field; field-level empty-role-list entry never excludes; field-level no-entry-for-a-field never excludes; field-level nothing-excluded collapses `AllowedFields` to `null`.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Authorization/ Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Authorization/
git commit -m "feat(api): add shared row/field authorization evaluator"
```

### Task 3: TestContainers integration test — `RegisterSchema` with `AuthorizationRules`

**Files:**
- Create: `Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs`

**Interfaces:**
- Consumes: `SchemaDescriptor.Authorization`, `TypeDescriptor.authorization`, `ObjectMappingGrpcService` (Task 1)

- [ ] **Step 1: Composite fixture**
Define `AllStoresContainerFixture : IAsyncLifetime` in the new file, combining three containers built the same way the existing per-store fixtures already do:
- Postgres: `new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build()` (matches `Iverson.Sql.Tests/PostgresIntegrationTests.cs`), exposing a `PostgresSchemaManager`, a `PostgresRepository`, and the container's `ConnectionString` (the latter two needed by Step 3 to build a second, independent `SchemaRegistry`).
- StarRocks: `new ContainerBuilder().WithImage("starrocks/allin1-ubuntu:latest").WithPortBinding(9030, true).WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(9030)).Build()`, plus the same query-ready retry loop `StarRocksIntegrationTests.cs` uses (a bare port-open check isn't sufficient — StarRocks accepts TCP connections well before FE/BE bootstrap finishes), exposing a `StarRocksSchemaManager` constructed directly from the connection string (it builds its own `MySqlConnectionStringBuilder` internally).
- Qdrant: `new ContainerBuilder().WithImage("qdrant/qdrant:v1.18.2")...` matching the existing local `QdrantGrpcContainerFixture` in `ObjectSearchVectorIntegrationTests.cs`, exposing a `QdrantCollectionManager`.

- [ ] **Step 2: Wire a real `ObjectMappingGrpcService` + real `SchemaRegistry`**
```csharp
var registry = new SchemaRegistry(
    new SchemaRegistryRepository(fixture.PostgresRepository),
    NullLogger<SchemaRegistry>.Instance);

var sut = new ObjectMappingGrpcService(
    Substitute.For<IEntityRepository>(),
    Substitute.For<IRecordStoreTransactionRunner>(),
    fixture.PostgresSchemaManager,
    fixture.QdrantCollectionManager,
    Substitute.For<IEventProducer>(),
    registry,
    Substitute.For<IEmbeddingService>(),
    fixture.StarRocksSchemaManager,
    Substitute.For<IRelationValidator>(),
    Substitute.For<IEntityKeyAccessor>(),
    Substitute.For<IOutboxWriter>(),
    NullLogger<ObjectMappingGrpcService>.Instance);
```

- [ ] **Step 3: The test**
Build a `TypeDescriptor` (via a `SimpleType`-style local helper, extended with an extra `OwnerId` scalar property and `.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "OwnerId", RowPermissions = { new Client.Contracts.RowPermission { Role = "admin", CanReadAll = true } } }`), call `sut.RegisterSchema(...)`, assert `response.Success` is true. The `Client.Contracts.` qualification disambiguates from `Iverson.Api.Schema.AuthorizationRules`/`RowPermission` (Task 1 Step 2) — this file needs both namespaces (`Iverson.Client.Contracts` for the proto request, `Iverson.Api.Schema` for `SchemaDescriptor`), matching `ObjectMappingGrpcServiceTests.cs`'s existing `Client.Contracts.RelationDescriptor` disambiguation for the same underlying collision. Then build a **second, fresh** `SchemaRegistry` instance pointed at the same Postgres connection (`new SchemaRegistry(new SchemaRegistryRepository(new PostgresRepository(fixture.ConnectionString, ...)), ...)`), call `LoadAsync()`, and assert `.Get(typeName).Authorization` matches what was registered — this forces an actual JSON deserialize round-trip through Postgres rather than reading back the first instance's in-memory cache.

- [ ] **Step 4: Tag and register the fixture**
```csharp
[Trait("Category", "Integration")]
public sealed class RegisterSchemaAuthorizationIntegrationTests(AllStoresContainerFixture fixture)
    : IClassFixture<AllStoresContainerFixture>
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api.Tests/Grpc/RegisterSchemaAuthorizationIntegrationTests.cs
git commit -m "test(api): add TestContainers integration test for schema authorization provisioning"
```

### Task 4: Authentik acting-user `groups` claim + token-minting scope

**Files:**
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- Modify: `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- Modify: `Iverson.Server/deploy/scripts/mint_acting_user_token.py`

- [ ] **Step 1: Compose-only blueprint**
In `blueprints/compose-only/service-clients.yaml`, in the `iverson-loadtest-human` provider's `attrs:` block (after `grant_types: ["authorization_code", "refresh_token"]`, 6-space indent), add:
```yaml
      property_mappings:
        - !Find [authentik_providers_oauth2.scopemapping, [scope_name, groups]]
```

- [ ] **Step 2: Kind ConfigMap template**
In `templates/blueprints-configmap-service-clients.yaml`, in the `iverson-loadtest-human` provider's `attrs:` block (after `grant_types: ["authorization_code", "refresh_token"]`, **10-space indent** — this file nests one level deeper than the compose-only blueprint), add:
```yaml
          property_mappings:
            - !Find [authentik_providers_oauth2.scopemapping, [scope_name, groups]]
```
(Matches the indentation already used for `iverson-oidc-default`'s equivalent binding earlier in the same file.)

- [ ] **Step 3: Token-minting scope**
In `mint_acting_user_token.py`, change `authorize_and_get_code`'s `"scope": "openid"` (line 309) to `"scope": "openid groups"`.

- [ ] **Step 4: Assert the claim**
Add a helper and an assertion in `run()`:
```python
def decode_jwt_claims(token: str) -> dict:
    payload_b64 = token.split(".")[1]
    padded = payload_b64 + "=" * (-len(payload_b64) % 4)
    return json.loads(base64.urlsafe_b64decode(padded))
```
Immediately after `access_token = exchange_code_for_token(...)` in `run()`, before the final `print(access_token)`:
```python
claims = decode_jwt_claims(access_token)
if not claims.get("groups"):
    log(f"WARNING: minted token has no non-empty 'groups' claim: {claims.get('groups')!r}")
else:
    log(f"groups claim present: {claims['groups']!r}")
```
(A `log()` call, not a hard failure — the smoke-test user's group membership is environment-provisioned state this script doesn't control; a missing claim here is diagnostic signal, not this script's own bug.)

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml Iverson.Server/deploy/scripts/mint_acting_user_token.py
git commit -m "feat(authentik): bind groups scope mapping to acting-user provider, assert in smoke test"
```

## Tasks NOT in this plan

- Calling the evaluator from any gRPC service.
- Translating a decision into a Postgres `WHERE`, a StarRocks `WHERE`, or a Qdrant `Filter`.
- Rejecting requests, masking response fields, or validating write payloads.
- Any UI/tooling for authoring `AuthorizationRules` — schema registration remains a raw proto call.
- Migrating any existing registered schema to add rules.

## Known issues inherited from spec

Once 5b/5c/5d wire the evaluator into live enforcement, any existing registered schema with no `AuthorizationRules` will start denying all row/field access (per the deny-by-default decision in the evaluator's logic) — this plan does not migrate or grandfather existing schemas. This is a known, deliberate consequence of the chosen default, not a gap in this plan's scope.
