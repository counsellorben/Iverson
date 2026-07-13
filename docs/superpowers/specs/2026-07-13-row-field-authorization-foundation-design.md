# Row/Field-Level Authorization — Foundation (Part 5a) — Design

## Context

This is Part 5 of the 5-part identity-management initiative. Parts 1-4 are complete and merged:

1. **IdP deployment** (Authentik as infrastructure)
2. **Admin endpoint protection** (`/admin/*` operator policy)
3. **Client API service authentication** (per-service Bearer tokens on gRPC)
4. **End-user identity propagation** (`x-acting-user-authorization` header, `ActingUserInterceptor`, `IActingUserAccessor` — plumbing only, no enforcement)

Part 5, "row/field-level authorization," is comparable in size to the Pipeline CTE DSL project and has been split into four sequential sub-projects:

- **5a — Authorization foundation** (this spec)
- **5b — Postgres enforcement** (write path: `ObjectMappingGrpcService`/`ObjectPersistenceGrpcService`; read path: `ObjectMappingGrpcService.Get` + `ObjectRetrievalGrpcService`)
- **5c — StarRocks read-path enforcement** (Search/Aggregate/GroupBy/Pipeline)
- **5d — Qdrant read-path enforcement** (SearchSimilar/SearchChunks)

5a builds the schema metadata, the acting-user role claim, and a shared decision-evaluator that 5b/5c/5d will each consume. **5a wires nothing into a live RPC — it adds zero enforcement.**

## Goals

1. Schema registration gains authorization metadata: an optional owner field per type, plus role→permission rules for rows and fields.
2. Authentik emits a `groups` claim on acting-user tokens (the acting-user analog of the existing service-JWT operator-policy mechanism).
3. A store-agnostic `IRowFieldAuthorizationEvaluator` produces a declarative decision; 5b/5c/5d each translate that decision into their own store's enforcement mechanism.

## Non-goals (explicitly out of scope for 5a)

- Calling the evaluator from any gRPC service.
- Translating a decision into a Postgres `WHERE`, a StarRocks `WHERE`, or a Qdrant `Filter`.
- Rejecting requests, masking response fields, or validating write payloads.
- Any UI/tooling for authoring `AuthorizationRules` — schema registration remains a raw proto call.
- Migrating any existing registered schema to add rules.

## Design

### 1. Schema authorization metadata

**Proto** (`Iverson.Clients/Common/Proto/object_mapping.proto`) — new messages, and a new field on `TypeDescriptor` (not `SchemaRequest`; see "Verified assumptions" for why):

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

message TypeDescriptor {
    string                      type_name      = 1;
    repeated PropertyDescriptor properties     = 2;
    repeated RelationDescriptor relations      = 3;
    AuthorizationRules          authorization  = 4;  // new; optional
}
```

Each `TypeDescriptor` (the root type and every dependent) carries its own `AuthorizationRules` — matching the fact that root + each dependent already produce independent `SchemaDescriptor`s and independent store provisioning today (`ObjectMappingGrpcService.RegisterSchema:54`, looping over `root_type` + `dependents`).

`owner_field`, when set, must name an existing scalar (non-relation) field already declared in that same type's `properties` list. It stores the acting user's `sub` value directly — not a foreign key to a separate Users type. `RegisterSchema` validates this: an `owner_field` that doesn't match a declared scalar property name is a registration error, rejected the same way `RegisterSchema` already rejects a missing `root_type` (`ObjectMappingGrpcService.cs:49-50`) — `throw new RpcException(new Status(StatusCode.InvalidArgument, ...))` — this prevents a typo'd `owner_field` from silently producing an evaluator that can never match ownership.

**C# model** (`Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`):

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

`SchemaBuilder.BuildDescriptor(TypeDescriptor, IEmbeddingService)` (`Schema/SchemaBuilder.cs:13`) gains a mapping from `typeDesc.Authorization` to `SchemaDescriptor.Authorization` (null passthrough when the proto field is unset).

**Baseline semantics**: a row is accessible to its owner (`OwnerField == actingUser.sub`) unconditionally, independent of role. Beyond that, a role's `RowPermission` entry grants access to *all* rows of the type for whichever actions it flags `*_all = true`. If `owner_field` is unset, there is no implicit-owner path — access is purely role-driven via `row_permissions`.

### 2. Acting-user role plumbing

**Authentik**: `iverson-loadtest-human` (the acting-user OAuth2 provider from Part 4, blueprinted in `deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml` and mirrored in `deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`) currently has no `property_mappings` at all. Add:

```yaml
      property_mappings:
        - !Find [authentik_providers_oauth2.scopemapping, [scope_name, groups]]
```

— mirroring the existing `iverson-oidc-default` provider (the admin-operator human client), which already binds this exact scope mapping. Update **both** files (compose-only blueprint + kind ConfigMap template) — the same two-file duplication the earlier `grant_types` fix (Part 4 Task 11) had to touch.

**Token minting**: `mint_acting_user_token.py` currently requests `scope: "openid"` only (line ~309). Per the Part 2+3 finding that scope *binding* doesn't guarantee *inclusion* in the issued token, add `groups` to the requested scope string here (and in any SDK-side acting-user token acquisition that requests scope explicitly).

**Claim exposure**: no new accessor type. `IActingUserAccessor.ActingUser` (`Iverson.Api/IActingUserAccessor.cs`) is already `ClaimsPrincipal?`, populated by `ActingUserInterceptor` whenever the header is present. The evaluator reads `actingUser.FindAll("groups")` directly off that principal — the exact same claim-read pattern `OperatorAuthorizationPolicy.IsSatisfiedBy` already uses for the service principal (`Program.cs:140`), just pointed at a different `ClaimsPrincipal` instance. No change to `ActingUserInterceptor` itself.

### 3. Shared evaluator

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
    bool Denied,                          // true => reject outright, nothing accessible
    bool OwnershipRequired,               // true => caller must additionally restrict to OwnerFieldName == OwnerValue
    string? OwnerFieldName,
    string? OwnerValue,                   // acting user's sub, when OwnershipRequired
    IReadOnlySet<string>? AllowedFields); // null = unrestricted; non-null = explicit allow-list for this action
```

Decision logic (pure function, no I/O):

1. No `AuthorizationRules` on the schema at all → `Denied = true`.
2. `AuthorizationRules` present but `actingUser` is null → `Denied = true`.
3. Otherwise, read `actingUser`'s `groups` claims, look up matching `RowPermission` entries. If any matched role has the relevant `*_all` flag set for `action` → `OwnershipRequired = false` (role bypass). Else if `owner_field` is set → `OwnershipRequired = true`, `OwnerFieldName`/`OwnerValue` populated from the schema + the `sub` claim. Else (no bypass role, no owner field) → `Denied = true`.
4. Field-level (Read/Write only — Delete always returns `AllowedFields = null`, no field granularity for a whole-row delete): a field is excluded from `AllowedFields` only if it has a `FieldPermission` entry whose role list for the given action is both non-empty and does not intersect the acting user's roles — a field with no entry at all, or an entry with an empty role list for that action, is never excluded (matching the `FieldPermission` proto's own "empty = inherits row-level decision" comment). If no field ends up excluded (including the case where the schema declares no field-level rules at all), `AllowedFields = null` (unrestricted); otherwise `AllowedFields` is the schema's full field-name set minus the excluded ones.

**Location**: new `Iverson.Server/Iverson.Api/Authorization/` folder, co-located with the existing `OperatorAuthorizationPolicy`. Registered `AddSingleton<IRowFieldAuthorizationEvaluator, RowFieldAuthorizationEvaluator>()` — pure function over its inputs, no per-request state.

## Testing plan

- **Unit tests** for `RowFieldAuthorizationEvaluator` covering every branch above: no-rules-deny, no-identity-deny, owner-match, role-bypass per action, role-restricted-to-owned, field-level allow-list intersection, field-level unrestricted fallback.
- **TestContainers integration test**: `RegisterSchema` with `AuthorizationRules` set on a `TypeDescriptor`, run against real Postgres/StarRocks/Qdrant containers (this repo already has established TestContainers-based integration test projects for all three — e.g. `Iverson.Sql.Tests/PostgresIntegrationTests.cs`, `Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs`, `Iverson.Vector.Tests/QdrantIntegrationTests.cs`), proving `ApplySchemaAsync`/`ApplyTableAsync`/`ApplyCollectionAsync` provisioning still succeeds unmodified, and that `SchemaDescriptor.Authorization` round-trips through the real Postgres-backed `SchemaRegistry` reload. This does not test the evaluator's decision logic — that's unit-tested — only that the new metadata doesn't disturb the existing 3-store provisioning pipeline.
- **Authentik smoke test**: extend the existing acting-user harness (`mint_acting_user_token.py` / the `acting-user-smoke-test` LoadTest command from Part 4 Task 11) to assert the minted token's `groups` claim is present and non-empty when the test user has group memberships.

## Known consequence, accepted by user

Once 5b/5c/5d wire the evaluator into live enforcement, any existing registered schema with no `AuthorizationRules` will start denying all row/field access (per the deny-by-default decision below) — 5a does not migrate or grandfather existing schemas. This is a known, deliberate consequence of the chosen default, not a gap in 5a's scope.

## Verified assumptions

- `object_mapping.proto`'s `SchemaRequest` has no per-request body suitable for authorization rules — it's `{ root_type, dependents, trace_id }`, each a `TypeDescriptor`. **Correction from the originally-presented design**: `AuthorizationRules` attaches to `TypeDescriptor`, not `SchemaRequest`. This is a proto-attachment-point fix, not a semantic change — `SchemaBuilder.BuildDescriptor` (`SchemaBuilder.cs:13`) already takes one `TypeDescriptor` and produces one `SchemaDescriptor`; `ObjectMappingGrpcService.RegisterSchema` (`Grpc/ObjectMappingGrpcService.cs:54`) already loops over `root_type` + `dependents`, provisioning each type independently — so per-type authorization rules were always the correct granularity.
- `SchemaDescriptor` (`Schema/SchemaDescriptor.cs`) is a `sealed record`; `SchemaRegistry` (`Schema/SchemaRegistry.cs:31,51`) serializes/deserializes it directly via `System.Text.Json` (`JsonSerializer.Serialize/Deserialize<SchemaDescriptor>`, camelCase). A new non-`required` property is backward-compatible — old persisted rows deserialize with `Authorization = null`. Confirmed no second JSON consumer of `SchemaDescriptor` exists in the repo (grepped for all `JsonSerializer`/`SchemaDescriptor>(` call sites).
- All three provisioning methods are structurally inert to the new property, for two independent reasons: (a) none of them receive `SchemaDescriptor` at all — they receive narrower projections (`TableSchema` for Postgres, `StarRocksTableSchema`, `CollectionSchema` for Qdrant) built by `SchemaBuilder.ToTableSchema`/`ToStarRocksTableSchema`/`ToCollectionSchema`/`ToChunkCollectionSchema` (`SchemaBuilder.cs:98-133`); (b) those four conversion methods only copy specific named properties (`TableName`, `KeyColumn`, `ScalarColumns`, `SortKey`, vector/chunk fields, `FkColumns`) — none uses reflection or whole-object serialization. Read in full: `PostgresSchemaManager.ApplySchemaAsync` (`Iverson.Sql/PostgresSchemaManager.cs:14-97`), `StarRocksSchemaManager.ApplyTableAsync` (`Iverson.StarRocks/StarRocksSchemaManager.cs:66-106`), `QdrantCollectionManager.ApplyCollectionAsync` (`Iverson.Vector/QdrantCollectionManager.cs:39-102`).
- `groups` is confirmed the exact claim name already in production use: `OperatorAuthorizationPolicy.IsSatisfiedBy(IEnumerable<string> groupClaims, ...)` and `Program.cs:140` (`context.User.FindAll("groups")`).
- `IActingUserAccessor.ActingUser` (`Iverson.Api/IActingUserAccessor.cs`) is confirmed `ClaimsPrincipal?`.
- Confirmed the exact Authentik gap via direct read of `blueprints/compose-only/service-clients.yaml`: `iverson-oidc-default` (lines ~80-93) already binds the `groups` scope mapping; `iverson-loadtest-human` (lines ~100-114) has no `property_mappings` at all. `mint_acting_user_token.py` requests `scope: "openid"` only (confirmed via grep). Both `blueprints/compose-only/service-clients.yaml` and `templates/blueprints-configmap-service-clients.yaml` reference `iverson-loadtest-human` and need the identical fix (confirmed via grep — same two-file pattern as the earlier `grant_types` fix from Part 4 Task 11).
- TestContainers is already an established pattern in this repo, not new tooling: confirmed existing integration test projects using it for Postgres (`Iverson.Sql.Tests/PostgresIntegrationTests.cs`), StarRocks (`Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs`), and Qdrant (`Iverson.Vector.Tests/QdrantIntegrationTests.cs`).
- Proto changes propagate to all 5 client SDKs (.NET/Java/Python/Go/TypeScript) via each language's own existing generation mechanism (`Iverson.Clients/{Python,TypeScript,Go}/scripts/generate_protos.sh`, Java's `pom.xml` protobuf-maven-plugin, .NET's `Grpc.Tools` build-time codegen in `Iverson.Client.Contracts.csproj`) — there is no single unified command, but a pure proto-schema addition requires zero new hand-written per-language code, only re-running each language's existing generation step (unlike Part 4's OAuth2 credentials work, which needed genuinely new per-language logic).
- `RegisterSchema`'s only existing validation-error convention (within this exact method) is to `throw RpcException(new Status(StatusCode.InvalidArgument, ...))` — confirmed at `ObjectMappingGrpcService.cs:49-50` (missing `root_type`). `RelationValidator` (`RelationValidator.cs:38-40`) also throws `RpcException`, but is only called from `Post`/`Update` (`ObjectMappingGrpcService.cs:124,191`), not `RegisterSchema` — it is not this RPC's precedent. `RegisterSchema`'s one `SchemaResponse` construction (line 80-85) is unconditionally `Success = true`; the `success = false` pattern used elsewhere in this file (`Get` line 103, `Delete` line 249) is on a different response type (`MappingResponse`) for a different kind of failure (not-found, not validation).
