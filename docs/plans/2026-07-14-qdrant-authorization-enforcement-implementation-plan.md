# Qdrant Authorization Enforcement (Part 5d) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/superpowers/specs/2026-07-14-qdrant-authorization-enforcement-design.md` (commit SHA: `e4c7955`)

**Goal:** Wire the existing `IRowFieldAuthorizationEvaluator` into `ObjectSearchGrpcService`'s two Qdrant-backed RPCs (`SearchSimilar`, `SearchChunks`), enforcing row-level ownership (as a Qdrant `Filter` predicate) and field-level restrictions (reject-on-reference for the searched property and filter clauses; post-fetch masking for `SearchSimilar`'s response) on every query these RPCs can generate. This is the last part of the 5-part identity-management initiative's Part 5 (row/field-level authorization).

**Architecture:** Unlike Part 5c (StarRocks), neither RPC has a join concept, so authorization is evaluated once per call via a direct `authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read)` call — no `AuthorizationConstraint`/joined-types dictionary needed. Row-level ownership is a `Conditions.MatchKeyword` condition appended to the Qdrant `Filter.Must` list, which is independent of `Should`/`MustNot` and therefore needs no SQL-style wrap-and-AND treatment. Field-level restriction is enforced by reject-on-reference (the searched vector/chunk property, and `SearchSimilar`'s filter clauses) or post-fetch masking (`SearchSimilar`'s response only — `SearchChunks`' response shape has no generic per-field payload to mask). `SearchChunks`' row-level ownership requires the chunk-upsert write path (`IntelligenceStoreConsumer`) to start writing the owner field's value into chunk payloads, which it does not today.

**Tech stack:** C# / .NET 10, gRPC, Qdrant.Client, xUnit + NSubstitute + FluentAssertions.

---

## Global Constraints

- All field-level rejections (searched-property, filter-clause) throw `RpcException(StatusCode.InvalidArgument, ...)` directly — matching the existing style of `SearchSimilar`'s "has no [IversonEmbedding] annotation" check and `ValidateFilterProperty`, not a new exception type.
- Denial (`decision.Denied` — no `AuthorizationRules` configured, or rules configured but no acting-user identity) is empty-stream, never an exception, for both RPCs — evaluated immediately after `RequireSchema`, before any other request-content-dependent check (property resolution, `CollectionName` check, filter validation), so a fully-denied caller cannot learn whether a property/collection/filter target exists.
- Ownership and field-level checks reuse `IRowFieldAuthorizationEvaluator`/`AuthorizationFieldMasking` unchanged in their core logic. `MaskDisallowedFields` gains one new optional `exemptField` parameter (mirroring `RejectDisallowedFields`'s existing shape) — no new evaluator variant, no duplicated authorization logic.
- The Qdrant ownership condition is always merged into the existing `Filter.Must` list (creating a fresh `Filter` if the caller supplied none) — never replaces caller-supplied filter conditions.

## File Structure

- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — `RegisterSchema`'s `owner_field` validation gains 2 new checks (non-string `SqlType` rejection, reserved-chunk-key collision rejection)
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` — `MaskDisallowedFields` gains an optional `exemptField` parameter
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — `ToChunkCollectionSchema` gains a conditional owner-field `PayloadIndex`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — chunk-upsert loop writes the owner field's value into each chunk point's payload
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — `SearchSimilar`/`SearchChunks` gain authorization evaluation, ownership-filter merging, reject-on-reference checks, and (SearchSimilar only) response masking
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — 3 new `RegisterSchema` validation tests
- Modify: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` — 2 new `ToChunkCollectionSchema` tests
- Modify: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — 2 new chunk-payload owner-write tests
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — new Qdrant-capable owned-schema fixture + per-RPC authorization test cases, plus one existing-test fixture fix

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time (see the spec's own `Verified assumptions` table, items 1–23) and are **not** re-verified here:
- `AuthorizationDecision` shape (`Denied`/`OwnershipRequired`/`OwnerFieldName`/`OwnerValue`/`AllowedFields`) is unchanged from 5a/5c (item 1)
- `RegisterSchema` already validates `owner_field` against `descriptor.ScalarColumns` (item 2)
- Qdrant's `Filter` has independent `Must`/`Should`/`MustNot` repeated fields; `Must` is always AND'd (item 3)
- `Conditions.MatchKeyword(string, string)` exists and returns a `Condition` (item 4)
- Chunk-collection payload today is exactly `{text, parent_id, field, chunk_index}` (item 5)
- `SearchSimilar`'s entity-collection payload includes every scalar column (item 6)
- `SchemaBuilder.ToCollectionSchema` already creates a `PayloadIndex` per scalar column (item 7)
- `SchemaBuilder.ToChunkCollectionSchema` only indexes `parent_id` today (item 8)
- The entity-collection payload's point-identifier is always the literal string `"key"`, never `schema.KeyColumn.Name.ToCamelCase()` (item 9)
- `AllowedFields` does **not** always contain the key column's name — the `exemptField: "Key"` masking design is a fixed constant, not derived from this (corrected item 10)
- `StructSerializer.UpperFirst` is the canonicalization function both masking methods use (item 11)
- `RejectDisallowedFields` already has an `exemptField` parameter; `MaskDisallowedFields` currently takes 2 params (item 12)
- `ValidateFilterProperty` throws `RpcException` directly, not `FilterTranslationException` (item 13)
- `BuildChunksFilter` hard-constrains `SearchChunks`' filter clause to `EQUALS` on `schema.KeyColumn.Name` (item 14)
- Neither `SearchSimilarRequest` nor `SearchChunksRequest` has a `Joins`-style field (item 15)
- `ObjectSearchGrpcService`'s constructor already has `IActingUserAccessor`/`IRowFieldAuthorizationEvaluator` wired (item 16)
- `ExtractString` tries the PascalCase name first, then a camelCase fallback (item 17)
- `CollectionSchema` record shape: `(string CollectionName, IReadOnlyList<NamedVector> Vectors, IReadOnlyList<PayloadIndex> PayloadIndexes)` (item 18)
- No existing caller of `MaskDisallowedFields` breaks from an added optional trailing parameter (item 19)
- `IntelligenceStoreConsumerTests.cs` already has a precedent for bespoke inline `SchemaDescriptor`/`TypeDescriptor` construction (item 20)
- Existing `ObjectSearchGrpcServiceTests.cs` `SearchSimilar`/`SearchChunks` tests use `SchemaFixtures` schemas carrying a permissive bypass `AuthorizationRules` (item 21)
- `ObjectRetrievalGrpcService.Get`/`GetMany` never check a caller-supplied key value against `AllowedFields` (item 22)
- `Search`/`GroupBy`/`Pipeline` (5c) evaluate authorization as the only thing between `RequireSchema` and any other request-content-dependent check (item 23)

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path / ordering | `ObjectMappingGrpcService.cs`'s existing `owner_field` validation is at lines 61-67, immediately followed by `await _schemaManager.ApplySchemaAsync(...)` at line 69 — new checks can be inserted between, guaranteed to run before any store is touched | Read `ObjectMappingGrpcService.cs:45-97` |
| 2 | Function signature | `ColumnDescriptor(string Name, string SqlType, bool IsNullable)`; `SchemaDescriptor.Authorization` is typed `AuthorizationRules?` (the `SchemaAuthorizationRules`/`ContractsAuthorizationRules` names seen in `SchemaBuilder.cs` are local `using`-aliases, not the real type names) | Read `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` in full |
| 3 | Function signature | `AuthorizationFieldMasking.MaskDisallowedFields(Struct, IReadOnlySet<string>?)` — exactly 2 params today; `RejectDisallowedFields`'s existing 3rd-param pattern is `string? exemptField = null` compared via `canonical != exemptField` | Read `AuthorizationFieldMasking.cs:73-96` |
| 4 | Consumer impact (Cat 6) | `MaskDisallowedFields` has exactly 6 call sites, all 2-arg: 4 in `ObjectMappingGrpcService.cs` (lines 134, 411, 447, 481), 2 in `ObjectRetrievalGrpcService.cs` (lines 47, 103) — an added optional 3rd parameter is additive and breaks none of them | `grep -rn "MaskDisallowedFields(" Iverson.Server/Iverson.Api/Grpc/*.cs` |
| 5 | Function signature | `SchemaBuilder.ToChunkCollectionSchema(SchemaDescriptor d)` (lines 139-142) is currently expression-bodied (`=> new(...)`); adding a conditional `PayloadIndex` requires converting it to a block body | Read `SchemaBuilder.cs:139-150` |
| 6 | File path / ordering | `IntelligenceStoreConsumer.cs`'s chunk-upsert loop is at lines 133-174, inside `if (schema.ChunkFields.Count > 0)`; `payload`/`ev`/`schema` (needed for `ExtractString(payload, ownerField)`) are all in scope from `HandleAsync`'s top (line 58) | Read `IntelligenceStoreConsumer.cs:58-174` |
| 7 | Consumer impact (Cat 6) | `IntelligenceStoreConsumer.EnsureChunkCollectionAsync` (lines 210-223) independently constructs its own `CollectionSchema` (used only as a lazy re-ensure, guarded by an in-memory `_ensuredChunkCollections` cache) and will **not** include this plan's new owner-field `PayloadIndex`. Verified this needs no fix: (a) `QdrantCollectionManager.ApplyCollectionAsync`'s payload-index application (`ApplyPayloadIndexesAsync`) is purely additive — it never diffs against or removes existing indexes, so a later call with a shorter index list cannot strip an index `RegisterSchema` already created; (b) `RegisterSchema` always provisions the chunk collection (with the new owner index, via Task 1) at `ObjectMappingGrpcService.cs:84-85` before any entity event can reach the consumer, since events are only produced for already-registered types | Read `QdrantCollectionManager.cs`'s `ApplyCollectionAsync`/`ApplyPayloadIndexesAsync` (no `DeletePayloadIndex` call anywhere in `Iverson.Vector`, confirmed via repo-wide grep); read `ObjectMappingGrpcService.cs:82-85` |
| 8 | Task ordering | `SearchSimilar` (lines 101-175) and `SearchChunks` (lines 179-231) both currently have nothing between `RequireSchema` and their next check (`vectorDesc`/`chunkDesc` resolution) — the new denial check can be inserted directly after `RequireSchema` with no other code to reorder around it | Read `ObjectSearchGrpcService.cs:101-231` |
| 9 | Consumer impact (Cat 6) | `SearchSimilar_ThrowsRpcException_WhenNoCollection`'s inline `SchemaDescriptor` (`ObjectSearchGrpcServiceTests.cs:923-934`) has no `Authorization` set (defaults to `null`) — once `SearchSimilar` gains the Denied-short-circuit, this caller (no `AuthorizationRules` configured) will hit `decision.Denied` and return an empty stream *before* reaching the `CollectionName is null` check this test exists to exercise, breaking the test's assertion (`FailedPrecondition` expected, but no exception would be thrown). Must add a bypass `Authorization` to this inline schema so the test still reaches the check it's testing | Read `ObjectSearchGrpcServiceTests.cs:919-944`; cross-referenced against `RowFieldAuthorizationEvaluator.Evaluate`'s `rules is null → Denied` branch |
| 10 | Test/build command | `cd Iverson.Server && dotnet build Iverson.Api && dotnet test Iverson.Api.Tests --filter "..."` is the working invocation pattern (used successfully by the 5c plan in this same repo) | Read `docs/plans/2026-07-13-starrocks-authorization-enforcement-implementation-plan.md`'s own "Run tests and commit" steps |
| 11 | Commit convention | `feat(api): ...` — not `feat(vector)`/`feat(qdrant)` — is the correct scope: every production file this plan touches (`ObjectMappingGrpcService.cs`, `AuthorizationFieldMasking.cs`, `SchemaBuilder.cs`, `IntelligenceStoreConsumer.cs`, `ObjectSearchGrpcService.cs`) lives under `Iverson.Api`, matching the existing `feat(api): enforce row/field authorization on ObjectRetrievalGrpcService.Get/GetMany`-style commits from 5a/5b, none of which used a `vector`/`qdrant` scope despite also touching Qdrant-adjacent code paths | `git log --oneline --all \| grep -iE "feat\((api\|vector\|starrocks)\)"` — 13 matching commits, all `feat(api)` or `feat(starrocks)`, zero `feat(vector)`/`feat(qdrant)` |
| 12 | Function signature | `RowFieldAuthorizationEvaluator.Evaluate`'s field-exclusion logic only excludes a field when its `FieldPermission.ReadableRoles.Count > 0` **and** the caller's groups don't intersect — an empty `ReadableRoles` list does *not* restrict the field. New tests constructing a "restricted field" `FieldPermission` must use a non-empty `ReadableRoles` naming a role the test's acting user lacks (e.g. `new("Name", ["admin"], [])`), matching the existing `Search_RestrictedFields_MasksDisallowedFieldFromResponse` precedent | Read `RowFieldAuthorizationEvaluator.cs`'s `excluded` computation; read `ObjectSearchGrpcServiceTests.cs:271-292` |
| 13 | Function signature | `ActingUserFixtures.Principal(sub, params groups)` sets the `"sub"` claim to `sub` and `"groups"` claims to `groups`; the test constructor's default acting user is `Principal("test-user", "test-bypass")`, so `decision.OwnerValue` for an ownership-required, non-bypass test caller is the literal string `"test-user"` | Read `ActingUserFixtures.cs`; read `ObjectSearchGrpcServiceTests.cs:63-64` |
| 14 | Sibling sweep — `NamingExtensions.ToCamelCase()` reachability | Of the 4 reserved chunk-payload keys (`text`, `parent_id`, `field`, `chunk_index`), only `text`/`field` are reachable as a `ToCamelCase()` output of a PascalCase owner-field name (`"Text"`/`"Field"`) — `parent_id`/`chunk_index` contain underscores, which `ToCamelCase` (lowercases only the first character, no underscore insertion) can never produce. The new `RegisterSchema` collision test uses `OwnerField = "Text"` accordingly | Read `NamingExtensions.cs:12-13`; matches the design spec's own already-flagged observation (same file, "General lesson" section of `project-qdrant-authorization-enforcement` memory) |
| 15 | Function signature / compile validity | Adding a bare `using Qdrant.Client.Grpc;` to `ObjectSearchGrpcService.cs` fails to compile — `Qdrant.Client.Grpc.Struct`/`Value` collide with `Google.Protobuf.WellKnownTypes.Struct`/`Value`, already used unqualified in `SearchSimilar`. A narrow `using Conditions = Qdrant.Client.Grpc.Conditions;` alias avoids the collision (mirrors the file's existing `Filter` alias and `QdrantFilterBuilder.cs`'s `Range` alias) | Compiled a throwaway `net10.0` project referencing `Qdrant.Client` 1.18.1 with both usings plus unqualified `Struct`/`Value.ForString` — `CS0104` ambiguous-reference error on both types |

## Tasks

### Task 1: Schema-registration hardening, chunk-payload owner write, masking helper

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

**Interfaces:**
- Produces: `MaskDisallowedFields`'s new `exemptField` parameter, consumed by Task 2; `RegisterSchema`'s hardened `owner_field` validation and `ToChunkCollectionSchema`'s owner-field index, both consumed at runtime by Task 3's `SearchChunks` ownership filter
- Consumes: existing `IRowFieldAuthorizationEvaluator`/`AuthorizationDecision` (Part 5a), existing `ExtractString`/`ExtractTypedValue` helpers

- [ ] **Step 1: `RegisterSchema` — reject non-string owner-field `SqlType`, reject reserved-chunk-key collision**

In `ObjectMappingGrpcService.cs`, immediately after the existing owner-field-not-a-scalar-column check (after line 67's closing brace, before line 69's `await _schemaManager.ApplySchemaAsync(...)`):
```csharp
if (!string.IsNullOrEmpty(ownerField))
{
    var ownerColumn = descriptor.ScalarColumns.First(c =>
        string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase));

    // Allow-list, not a reject-list: IntelligenceStoreConsumer.ExtractTypedValue's default branch
    // only produces a clean scalar string for these 4 SqlTypes. Every other SqlType — including
    // the array variants UUID[]/REAL[] that SchemaBuilder.ArrayTypeOverrides can also produce for
    // a scalar column — falls through to JsonElement.ToString(), which for a non-string JSON value
    // (a number, bool, or array) produces something that can never equal a real caller's identity
    // value, silently excluding every caller (including the legitimate owner) from every result.
    var stringValuedSqlTypes = new[] { "TEXT", "UUID", "BYTEA", "TIMESTAMPTZ" };
    if (!stringValuedSqlTypes.Contains(ownerColumn.SqlType.ToUpperInvariant()))
    {
        throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"owner_field '{ownerField}' on '{descriptor.TypeName}' has SqlType '{ownerColumn.SqlType}', " +
            "which is not string-valued; Qdrant ownership filtering requires a string-valued owner field."));
    }

    if (descriptor.ChunkFields.Count > 0)
    {
        var reservedChunkKeys = new[] { "text", "parent_id", "field", "chunk_index" };
        var camelOwnerField = ownerField.ToCamelCase();
        if (reservedChunkKeys.Contains(camelOwnerField))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"owner_field '{ownerField}' on '{descriptor.TypeName}' camelCases to '{camelOwnerField}', " +
                $"which collides with a reserved chunk-payload key ({string.Join(", ", reservedChunkKeys)})."));
        }
    }
}
```
The `.First(...)` is safe here: the preceding existing check already threw if no matching scalar column exists, so by this point a match is guaranteed when `ownerField` is non-empty.

- [ ] **Step 2: `MaskDisallowedFields` gains `exemptField`**

In `AuthorizationFieldMasking.cs`, change:
```csharp
public static void MaskDisallowedFields(Struct payload, IReadOnlySet<string>? allowedFields)
{
    if (allowedFields is null) return;

    var toRemove = payload.Fields.Keys
        .Where(key => !allowedFields.Contains(StructSerializer.UpperFirst(key)))
        .ToList();
    foreach (var key in toRemove)
        payload.Fields.Remove(key);
}
```
to:
```csharp
public static void MaskDisallowedFields(Struct payload, IReadOnlySet<string>? allowedFields, string? exemptField = null)
{
    if (allowedFields is null) return;

    var toRemove = payload.Fields.Keys
        .Where(key => !allowedFields.Contains(StructSerializer.UpperFirst(key)) && StructSerializer.UpperFirst(key) != exemptField)
        .ToList();
    foreach (var key in toRemove)
        payload.Fields.Remove(key);
}
```

- [ ] **Step 3: `ToChunkCollectionSchema` gains a conditional owner-field `PayloadIndex`**

In `SchemaBuilder.cs`, change the expression-bodied method to a block body:
```csharp
internal static CollectionSchema ToChunkCollectionSchema(SchemaDescriptor d)
{
    var indexes = new List<PayloadIndex> { new("parent_id", PayloadIndexKind.Keyword) };
    if (d.Authorization?.OwnerField is { } ownerField)
        indexes.Add(new PayloadIndex(ownerField.ToCamelCase(), PayloadIndexKind.Keyword));

    return new CollectionSchema(
        d.CollectionName! + "_chunks",
        d.ChunkFields.Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_vector", c.Dimension)).ToList(),
        indexes);
}
```

- [ ] **Step 4: `IntelligenceStoreConsumer`'s chunk-upsert loop writes the owner field's value**

In `IntelligenceStoreConsumer.cs`, inside the `if (schema.ChunkFields.Count > 0)` block (line 133), extract the owner value once before the `foreach (var cf in schema.ChunkFields)` loop, and add it into each chunk point's payload:
```csharp
if (schema.ChunkFields.Count > 0)
{
    var chunksCollection = schema.CollectionName + "_chunks";
    await EnsureChunkCollectionAsync(chunksCollection, schema, ct);

    var ownerValue = schema.Authorization?.OwnerField is { } ownerField
        ? ExtractString(payload, ownerField)
        : null;

    foreach (var cf in schema.ChunkFields)
    {
        var text = ExtractString(payload, cf.PropertyName);
        if (string.IsNullOrWhiteSpace(text)) continue;

        var vectorName = $"{cf.PropertyName.ToSnakeCase()}_vector";
        var chunks     = SplitIntoChunks(text, cf.MaxTokens, cf.Overlap).ToList();

        var chunkTasks = chunks.Select(async chunk =>
        {
            var (chunkText, chunkIndex) = chunk;
            var chunkVector = await embedding.EmbedAsync(chunkText, ct);
            var chunkId     = ComputeChunkPointId(pointId, cf.PropertyName, chunkIndex);
            return (chunkVector, chunkId, chunkText, chunkIndex);
        }).ToList();

        var chunkResults = await Task.WhenAll(chunkTasks);

        foreach (var (chunkVector, chunkId, chunkText, chunkIndex) in chunkResults)
        {
            var chunkPayload = new Dictionary<string, object>
            {
                ["text"]        = chunkText,
                ["parent_id"]   = ev.Key,
                ["field"]       = cf.PropertyName,
                ["chunk_index"] = chunkIndex.ToString()
            };
            if (ownerValue is not null)
                chunkPayload[schema.Authorization!.OwnerField!.ToCamelCase()] = ownerValue;

            await vectorWrite.UpsertNamedAsync(
                chunksCollection,
                chunkId,
                new Dictionary<string, float[]> { [vectorName] = chunkVector },
                chunkPayload);
        }

        logger.LogInformation("[Intelligence] Ingested {Count} chunk(s) for {Type}:{Key} field={Field}",
            chunks.Count, ev.TypeName, ev.Key, cf.PropertyName);
    }
}
```

- [ ] **Step 5: Add `RegisterSchema` validation tests**

In `ObjectMappingGrpcServiceTests.cs`, add 3 tests near the existing `RegisterSchema_WithInvalidOwnerField_ThrowsInvalidArgument` (which they mirror in style):
```csharp
[Fact]
public async Task RegisterSchema_WithNonStringOwnerFieldSqlType_ThrowsInvalidArgument()
{
    var td = new TypeDescriptor { TypeName = "Widget" };
    td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
    td.Properties.Add(new PropertyDescriptor { Name = "Count", ClrType = ClrType.ClrInt32 });
    td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "Count" };

    var act = () => _sut.RegisterSchema(
        new SchemaRequest { RootType = td }, TestServerCallContext.Create());

    var ex = await act.Should().ThrowAsync<RpcException>();
    ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
}

[Fact]
public async Task RegisterSchema_WithOwnerFieldCollidingWithReservedChunkPayloadKey_ThrowsInvalidArgument()
{
    var td = new TypeDescriptor { TypeName = "Widget" };
    td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
    td.Properties.Add(new PropertyDescriptor { Name = "Text", ClrType = ClrType.ClrString });
    td.Properties.Add(new PropertyDescriptor
        { Name = "Body", ClrType = ClrType.ClrString, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
    td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "Text" }; // "Text".ToCamelCase() == "text"

    var act = () => _sut.RegisterSchema(
        new SchemaRequest { RootType = td }, TestServerCallContext.Create());

    var ex = await act.Should().ThrowAsync<RpcException>();
    ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
}

[Fact]
public async Task RegisterSchema_WithGuidTypedOwnerField_DoesNotThrow()
{
    var td = new TypeDescriptor { TypeName = "Widget" };
    td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
    td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ClrType = ClrType.ClrGuid });
    td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "OwnerId" };

    var response = await _sut.RegisterSchema(
        new SchemaRequest { RootType = td }, TestServerCallContext.Create());

    response.Success.Should().BeTrue();
}
```

- [ ] **Step 6: Add `ToChunkCollectionSchema` tests**

In `SchemaBuilderTests.cs`, add 2 tests near `ToCollectionSchema_PayloadIndexNames_AreCamelCase`:
```csharp
[Fact]
public void ToChunkCollectionSchema_IncludesPayloadIndex_ForOwnerField_WhenConfigured()
{
    var descriptor = SchemaFixtures.ArticleSchema() with
    {
        Authorization = new AuthorizationRules(
            "Title",
            new List<RowPermission> { new("test-bypass", true, true, true) },
            new List<FieldPermission>())
    };

    var schema = SchemaBuilder.ToChunkCollectionSchema(descriptor);

    schema.PayloadIndexes.Should().ContainSingle(p => p.FieldName == "title" && p.Kind == PayloadIndexKind.Keyword);
}

[Fact]
public void ToChunkCollectionSchema_OmitsOwnerFieldIndex_WhenNotConfigured()
{
    var descriptor = SchemaFixtures.ArticleSchema(); // BypassAuthorization() has OwnerField == null

    var schema = SchemaBuilder.ToChunkCollectionSchema(descriptor);

    schema.PayloadIndexes.Should().ContainSingle(p => p.FieldName == "parent_id");
}
```

- [ ] **Step 7: Add chunk-payload owner-write tests**

In `IntelligenceStoreConsumerTests.cs`, add 2 tests near `HandleCreated_WithChunkField_SplitsTextAndUpserts`:
```csharp
[Fact]
public async Task HandleCreated_WithChunkFieldAndOwnerField_WritesOwnerValueIntoChunkPayload()
{
    var schema = SchemaFixtures.ArticleSchema() with
    {
        Authorization = new AuthorizationRules(
            "AuthorId",
            new List<RowPermission> { new("test-bypass", true, true, true) },
            new List<FieldPermission>())
    };
    await _registry.RegisterAsync(schema);

    var longBody = new string('x', 3000);
    var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
    var ev = new EntityEvent(
        EventType: EntityEventType.Created, TypeName: "Article", Key: Guid.NewGuid().ToString(),
        PayloadJson: payload, TraceId: "trace-owner", SchemaVersion: "1",
        OccurredAt: DateTimeOffset.UtcNow, TargetStores: StoreTarget.Intelligence);

    IReadOnlyDictionary<string, object>? capturedPayload = null;
    _vectorWrite.UpsertNamedAsync(
            "articles_chunks", Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
        .Returns(Task.CompletedTask);

    await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

    capturedPayload.Should().NotBeNull();
    capturedPayload!["authorId"].Should().Be("00000000-0000-0000-0000-000000000001");
}

[Fact]
public async Task HandleCreated_WithChunkFieldAndNoOwnerField_OmitsOwnerKeyFromChunkPayload()
{
    await _registry.RegisterAsync(SchemaFixtures.ArticleSchema()); // BypassAuthorization() has OwnerField == null

    var longBody = new string('x', 3000);
    var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
    var ev = new EntityEvent(
        EventType: EntityEventType.Created, TypeName: "Article", Key: Guid.NewGuid().ToString(),
        PayloadJson: payload, TraceId: "trace-no-owner", SchemaVersion: "1",
        OccurredAt: DateTimeOffset.UtcNow, TargetStores: StoreTarget.Intelligence);

    IReadOnlyDictionary<string, object>? capturedPayload = null;
    _vectorWrite.UpsertNamedAsync(
            "articles_chunks", Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
        .Returns(Task.CompletedTask);

    await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

    capturedPayload.Should().NotBeNull();
    capturedPayload!.Should().NotContainKey("authorId");
}
```

- [ ] **Step 8: Run tests and commit**
```bash
cd Iverson.Server
dotnet build Iverson.Api
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~SchemaBuilderTests|FullyQualifiedName~IntelligenceStoreConsumerTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "feat(api): harden owner_field registration, write owner value into chunk payloads, extend MaskDisallowedFields"
```

### Task 2: `SearchSimilar` enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`SearchSimilar`, lines 101-175; imports)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `MaskDisallowedFields`'s `exemptField` parameter (Task 1)

- [ ] **Step 1: Add `Conditions` alias**

In `ObjectSearchGrpcService.cs`, add `using Conditions = Qdrant.Client.Grpc.Conditions;` to the top-level `using` block. A bare `using Qdrant.Client.Grpc;` cannot be used here: that namespace defines its own `Struct`/`Value` types, which collide with `Google.Protobuf.WellKnownTypes.Struct`/`Value` — already imported in this file and already used unqualified in `SearchSimilar`'s existing body (`new Struct()`, `Value.ForString(...)`). This mirrors the file's own existing `using Filter = Qdrant.Client.Grpc.Filter;` alias and `QdrantFilterBuilder.cs`'s `using Range = Qdrant.Client.Grpc.Range;` alias, both narrow aliases used for the identical reason.

- [ ] **Step 2: Rewrite `SearchSimilar`**

Replace the method body with:
```csharp
public override async Task SearchSimilar(
    SearchSimilarRequest request,
    IServerStreamWriter<SearchResponse> responseStream,
    ServerCallContext context)
{
    var schema = RequireSchema(request.TypeName);

    var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
    if (decision.Denied)
        return; // empty stream — Qdrant never queried

    var vectorDesc = schema.VectorFields.FirstOrDefault(v =>
        string.Equals(v.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
        ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"Property '{request.Property}' on '{request.TypeName}' has no [IversonEmbedding] annotation."));

    if (schema.CollectionName is null)
        throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"Type '{request.TypeName}' has no Qdrant collection."));

    if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(vectorDesc.PropertyName))
        throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"Property '{request.Property}' on '{request.TypeName}' is not authorized for this caller."));

    Filter? filter = null;
    if (request.Filter.Count > 0)
    {
        var camelCased = request.Filter.Select(c =>
        {
            ValidateFilterProperty(schema, c.Property, "SearchSimilar");
            if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(c.Property))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"SearchSimilar: filter property '{c.Property}' is not authorized for this caller."));
            return new SearchClause
            {
                Property   = c.Property.ToCamelCase(),
                Operator   = c.Operator,
                Value      = c.Value,
                ClauseType = c.ClauseType
            };
        }).ToList();

        try
        {
            filter = QdrantFilterBuilder.Build(camelCased, request.FilterLogic, "SearchSimilar");
        }
        catch (FilterTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    if (decision.OwnershipRequired)
    {
        filter ??= new Filter();
        filter.Must.Add(Conditions.MatchKeyword(schema.Authorization!.OwnerField!.ToCamelCase(), decision.OwnerValue!));
    }

    logger.LogInformation("[SearchSimilar] type={Type} property={Prop} topK={K} filtered={Filtered}",
        request.TypeName, request.Property, request.TopK, filter is not null);

    float[] queryVector;
    try
    {
        queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        throw new RpcException(new Status(StatusCode.Unavailable,
            $"Embedding service unavailable: {ex.Message}"));
    }

    var vectorName = vectorDesc.PropertyName.ToSnakeCase() + "_vector";
    var topK       = (ulong)Math.Max(1, (int)request.TopK);
    var results    = await vector.SearchNamedAsync(schema.CollectionName, vectorName, queryVector, topK, filter);

    foreach (var r in results)
    {
        var protoStruct = new Struct();
        foreach (var kvp in r.Payload)
            protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);

        AuthorizationFieldMasking.MaskDisallowedFields(protoStruct, decision.AllowedFields, exemptField: "Key");

        await responseStream.WriteAsync(
            new SearchResponse
            {
                Data    = protoStruct,
                Score   = (float)r.Score,
                TraceId = request.TraceId
            },
            context.CancellationToken);
    }
}
```

- [ ] **Step 3: Fix `SearchSimilar_ThrowsRpcException_WhenNoCollection`'s inline schema**

In `ObjectSearchGrpcServiceTests.cs`, the inline `SchemaDescriptor` at lines 923-934 has no `Authorization` set, so it would now hit the new `Denied` short-circuit before reaching the `CollectionName is null` check it's meant to exercise. Add a bypass `Authorization`:
```csharp
var schema = new SchemaDescriptor
{
    TypeName       = "VecNoCollection",
    TableName      = "vec_no_collection",
    CollectionName = null,
    KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
    ScalarColumns  = [],
    FkColumns      = [],
    VectorFields   = [new VectorDescriptor("Title", 1536, "text-embedding-3-small")],
    ChunkFields    = [],
    Relations      = [],
    Authorization  = new Iverson.Api.Schema.AuthorizationRules(
        null,
        new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
        new List<Iverson.Api.Schema.FieldPermission>())
};
```

- [ ] **Step 4: Add a Qdrant-capable owned-schema fixture**

Add a private helper near the existing `OwnedSchema` (which is StarRocks-only shaped: `CollectionName = null`, no `VectorFields`/`ChunkFields`):
```csharp
private static SchemaDescriptor OwnedQdrantSchema(
    string typeName, string? ownerField, IReadOnlyList<Iverson.Api.Schema.FieldPermission>? fieldPermissions = null,
    string bypassRole = "test-bypass") => new()
{
    TypeName       = typeName,
    TableName      = typeName.ToLowerInvariant() + "s",
    CollectionName = typeName.ToLowerInvariant() + "s",
    KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
    ScalarColumns  = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("Secret", "text", true)],
    FkColumns      = [],
    VectorFields   = [new VectorDescriptor("Name", 768, "nomic-embed-text")],
    ChunkFields    = [new ChunkDescriptor("Secret", 512, 64, "nomic-embed-text", 768)],
    Relations      = [],
    Authorization  = new Iverson.Api.Schema.AuthorizationRules(
        ownerField,
        new List<Iverson.Api.Schema.RowPermission> { new(bypassRole, true, true, true) },
        fieldPermissions?.ToList() ?? [])
};
```

- [ ] **Step 5: Add `SearchSimilar` authorization tests**

Near the existing `SearchSimilar` tests. Update the section-header comment at `ObjectSearchGrpcServiceTests.cs:891` from `// ── SearchSimilar / SearchChunks — Qdrant paths unchanged ─────────────────` to `// ── SearchSimilar — authorization ──────────────────────────────────────────` (matching the `// ── Search — authorization ───` heading style already used for the StarRocks RPCs), and add the new tests below it:
```csharp
[Fact]
public async Task SearchSimilar_NoAuthorizationRules_ReturnsEmptyStream_WithoutQueryingQdrant()
{
    var schema = SchemaFixtures.ArticleSchema() with { Authorization = null };
    await _registry.RegisterAsync(schema);

    var (writer, written) = MakeStream<SearchResponse>();
    await _sut.SearchSimilar(
        new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q" },
        writer, TestServerCallContext.Create());

    written.Should().BeEmpty();
    await _vector.DidNotReceive().SearchNamedAsync(
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>());
}

[Fact]
public async Task SearchSimilar_NoActingUserIdentity_ReturnsEmptyStream()
{
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId"));
    _actingUserAccessor.ActingUser = null;

    var (writer, written) = MakeStream<SearchResponse>();
    await _sut.SearchSimilar(
        new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
        writer, TestServerCallContext.Create());

    written.Should().BeEmpty();
}

[Fact]
public async Task SearchSimilar_OwnershipRequired_AddsMatchKeywordConditionToFilter()
{
    // No caller-supplied filter clause — also proves a fresh Filter is constructed when none exists.
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId", bypassRole: "other-bypass"));
    _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
    _vector.SearchNamedAsync("owneds", "name_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
           .Returns(new List<VectorSearchResult>().AsReadOnly());

    var (writer, _) = MakeStream<SearchResponse>();
    await _sut.SearchSimilar(
        new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
        writer, TestServerCallContext.Create());

    var call = _vector.ReceivedCalls()
        .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
        .Subject;
    var captured = (Filter?)call.GetArguments()[4];
    captured.Should().NotBeNull();
    captured!.Must.Should().ContainSingle(c => c.Field.Key == "ownerId" && c.Field.Match.Keyword == "test-user");
}

[Fact]
public async Task SearchSimilar_BypassRole_NoOwnershipFilterAdded()
{
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId")); // bypassRole defaults to "test-bypass"
    _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
    _vector.SearchNamedAsync("owneds", "name_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
           .Returns(new List<VectorSearchResult>().AsReadOnly());

    var (writer, _) = MakeStream<SearchResponse>();
    await _sut.SearchSimilar(
        new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
        writer, TestServerCallContext.Create());

    var call = _vector.ReceivedCalls()
        .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
        .Subject;
    ((Filter?)call.GetArguments()[4]).Should().BeNull();
}

[Fact]
public async Task SearchSimilar_RestrictedSearchedProperty_ThrowsInvalidArgument()
{
    var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Name", ["admin"], []) };
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));

    var (writer, _) = MakeStream<SearchResponse>();
    var act = async () => await _sut.SearchSimilar(
        new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
        writer, TestServerCallContext.Create());

    (await act.Should().ThrowAsync<RpcException>())
        .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
}

[Fact]
public async Task SearchSimilar_RestrictedFilterClauseProperty_ThrowsInvalidArgument()
{
    var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Secret", ["admin"], []) };
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));
    _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

    var request = new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" };
    request.Filter.Add(new SearchClause
    {
        Property = "Secret", Operator = SearchOperator.Equals,
        Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
    });

    var (writer, _) = MakeStream<SearchResponse>();
    var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

    (await act.Should().ThrowAsync<RpcException>())
        .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
}

[Fact]
public async Task SearchSimilar_RestrictedField_MaskedFromResponse_ButKeyEntrySurvives()
{
    var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Secret", ["admin"], []) };
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));
    _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

    var vectorResult = new VectorSearchResult(
        Id: 1, Score: 0.9,
        Payload: new Dictionary<string, string> { ["key"] = "point-key-1", ["name"] = "visible", ["secret"] = "hidden" });
    _vector.SearchNamedAsync("owneds", "name_vector", Arg.Any<float[]>(), Arg.Any<ulong>())
           .Returns(new List<VectorSearchResult> { vectorResult }.AsReadOnly());

    var (writer, written) = MakeStream<SearchResponse>();
    await _sut.SearchSimilar(
        new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
        writer, TestServerCallContext.Create());

    written.Should().HaveCount(1);
    written[0].Data.Fields.Should().ContainKey("key");   // schema's real key column is "Id", not "Key" — survives via the fixed exemptField constant
    written[0].Data.Fields.Should().ContainKey("name");
    written[0].Data.Fields.Should().NotContainKey("secret");
}
```

- [ ] **Step 6: Run tests and commit**
```bash
cd Iverson.Server
dotnet build Iverson.Api
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(api): enforce row/field authorization on ObjectSearchGrpcService.SearchSimilar"
```

### Task 3: `SearchChunks` enforcement

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`SearchChunks`, lines 179-231)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `OwnedQdrantSchema` fixture (Task 2); `RegisterSchema`'s hardened validation + `IntelligenceStoreConsumer`'s chunk-payload owner write (Task 1, exercised at runtime, not directly called by this task's own code)

- [ ] **Step 1: Rewrite `SearchChunks`**

Replace the method body with:
```csharp
public override async Task SearchChunks(
    SearchChunksRequest request,
    IServerStreamWriter<ChunkSearchResponse> responseStream,
    ServerCallContext context)
{
    var schema = RequireSchema(request.TypeName);

    var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
    if (decision.Denied)
        return; // empty stream — Qdrant never queried

    var chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
        string.Equals(c.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
        ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"Property '{request.Property}' on '{request.TypeName}' has no [IversonChunk] annotation."));

    if (schema.CollectionName is null)
        throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"Type '{request.TypeName}' has no Qdrant collection."));

    if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(chunkDesc.PropertyName))
        throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"Property '{request.Property}' on '{request.TypeName}' is not authorized for this caller."));

    var filter = BuildChunksFilter(schema, request);

    if (decision.OwnershipRequired)
    {
        filter ??= new Filter();
        filter.Must.Add(Conditions.MatchKeyword(schema.Authorization!.OwnerField!.ToCamelCase(), decision.OwnerValue!));
    }

    logger.LogInformation("[SearchChunks] type={Type} property={Prop} topK={K} filtered={Filtered}",
        request.TypeName, request.Property, request.TopK, filter is not null);

    float[] queryVector;
    try
    {
        queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        throw new RpcException(new Status(StatusCode.Unavailable,
            $"Embedding service unavailable: {ex.Message}"));
    }

    var vectorName       = chunkDesc.PropertyName.ToSnakeCase() + "_vector";
    var chunksCollection = schema.CollectionName + "_chunks";
    var topK             = (ulong)Math.Max(1, (int)request.TopK);
    var results          = await vector.SearchNamedAsync(chunksCollection, vectorName, queryVector, topK, filter);

    foreach (var r in results)
    {
        r.Payload.TryGetValue("text",      out var chunkText);
        r.Payload.TryGetValue("parent_id", out var parentId);

        await responseStream.WriteAsync(
            new ChunkSearchResponse
            {
                ParentKey = parentId  ?? string.Empty,
                ChunkText = chunkText ?? string.Empty,
                Score     = (float)r.Score,
                TraceId   = request.TraceId
            },
            context.CancellationToken);
    }
}
```

- [ ] **Step 2: Add `SearchChunks` authorization tests**

Update the section-header comment at `ObjectSearchGrpcServiceTests.cs:1021` from `// ── SearchChunks — Qdrant path unchanged ──────────────────────────────────` to `// ── SearchChunks — authorization ───────────────────────────────────────────`, and add the new tests below it:
```csharp
[Fact]
public async Task SearchChunks_NoAuthorizationRules_ReturnsEmptyStream_WithoutQueryingQdrant()
{
    var schema = SchemaFixtures.ArticleSchema() with { Authorization = null };
    await _registry.RegisterAsync(schema);

    var (writer, written) = MakeStream<ChunkSearchResponse>();
    await _sut.SearchChunks(
        new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q" },
        writer, TestServerCallContext.Create());

    written.Should().BeEmpty();
    await _vector.DidNotReceive().SearchNamedAsync(
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>());
}

[Fact]
public async Task SearchChunks_NoActingUserIdentity_ReturnsEmptyStream()
{
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId"));
    _actingUserAccessor.ActingUser = null;

    var (writer, written) = MakeStream<ChunkSearchResponse>();
    await _sut.SearchChunks(
        new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" },
        writer, TestServerCallContext.Create());

    written.Should().BeEmpty();
}

[Fact]
public async Task SearchChunks_RestrictedSearchedProperty_ThrowsInvalidArgument()
{
    var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Secret", ["admin"], []) };
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));

    var (writer, _) = MakeStream<ChunkSearchResponse>();
    var act = async () => await _sut.SearchChunks(
        new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" },
        writer, TestServerCallContext.Create());

    (await act.Should().ThrowAsync<RpcException>())
        .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
}

[Fact]
public async Task SearchChunks_KeyColumnFilterClause_NeverRejected_RegardlessOfAllowedFields()
{
    // "Name" is restricted, but the request neither searches nor filters on it — proves
    // BuildChunksFilter's single EQUALS-on-key-column clause needs no AllowedFields check.
    var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Name", ["admin"], []) };
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));
    _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
    _vector.SearchNamedAsync("owneds_chunks", "secret_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
           .Returns(new List<VectorSearchResult>().AsReadOnly());

    var request = new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" };
    request.Filter.Add(new SearchClause
    {
        Property = "Id", Operator = SearchOperator.Equals,
        Value = new SearchValue { StringVal = "parent-1" }, ClauseType = SearchClauseType.Filter
    });

    var (writer, _) = MakeStream<ChunkSearchResponse>();
    var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

    await act.Should().NotThrowAsync();
}

[Fact]
public async Task SearchChunks_OwnershipRequired_MergesMatchKeywordConditionWithKeyFilter()
{
    await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId", bypassRole: "other-bypass"));
    _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
    _vector.SearchNamedAsync("owneds_chunks", "secret_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
           .Returns(new List<VectorSearchResult>().AsReadOnly());

    var request = new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" };
    request.Filter.Add(new SearchClause
    {
        Property = "Id", Operator = SearchOperator.Equals,
        Value = new SearchValue { StringVal = "parent-1" }, ClauseType = SearchClauseType.Filter
    });

    var (writer, _) = MakeStream<ChunkSearchResponse>();
    await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

    var call = _vector.ReceivedCalls()
        .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
        .Subject;
    var captured = (Filter?)call.GetArguments()[4];
    captured.Should().NotBeNull();
    captured!.Must.Should().Contain(c => c.Field.Key == "ownerId" && c.Field.Match.Keyword == "test-user");
    captured.Must.Should().Contain(c => c.Field.Key == "parent_id" && c.Field.Match.Keyword == "parent-1");
}
```

- [ ] **Step 3: Run tests and commit**
```bash
cd Iverson.Server
dotnet build Iverson.Api
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(api): enforce row/field authorization on ObjectSearchGrpcService.SearchChunks"
```

## Tasks NOT in this plan

- A backfill mechanism for Qdrant data written before this change ships.
- Any change to `IRowFieldAuthorizationEvaluator`'s own logic or `AuthorizationDecision` shape (Part 5a's contract is reused as-is).
- Fixing the pre-existing, cross-cutting gap where `RowFieldAuthorizationEvaluator`'s `AllowedFields` construction doesn't protect the key column from exclusion if a schema admin configures a `FieldPermission` naming it — this affects 5b's and 5c's already-shipped masking/reject-on-reference code equally and is out of scope for this part.

## Known issues inherited from spec

- **Stale Qdrant data lacks the owner field.** Data written to Qdrant before this change ships (or before a schema had `AuthorizationRules` configured at all) won't have the owner field in its payload, for either `SearchSimilar` or `SearchChunks` — it will fail the ownership filter and be excluded from results until the source entity is next created/updated (re-triggering `IntelligenceStoreConsumer` ingestion). No backfill mechanism is built in this part.
- **Same 2 pre-existing consequences as 5a/5b/5c**: a schema with no `AuthorizationRules` rejects every call; a rules-configured schema with no acting-user identity also rejects. Neither is new or fixed here.
- **Cross-cutting gap discovered during verification, not introduced here, not fixed here**: `RowFieldAuthorizationEvaluator.Evaluate`'s `AllowedFields` construction has no special protection preventing a `FieldPermission` from naming the key column and thereby excluding it. If a schema admin ever configured this, 5b's `ObjectMapping`/`ObjectRetrieval` response masking and 5c's `Search` response masking would silently strip the key column from their responses too — the only place in the codebase that defends against this is `StarRocksPipelineBuilder.ColumnsFor`'s own defensive re-seeding (5c, Pipeline-specific). This part's own masking (the `exemptField: "Key"` constant) is unaffected by this gap since it doesn't rely on `AllowedFields` containing the key column's name at all. Fixing the underlying gap would touch already-shipped, already-reviewed code in 5b and 5c and is out of scope for this part.
