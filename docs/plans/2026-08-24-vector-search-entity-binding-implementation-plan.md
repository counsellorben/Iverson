# Vector-search entity binding — implementation plan

**Source spec:** `docs/specs/2026-08-24-vector-search-entity-binding-design.md` (commit SHA: 1faae1c)
**Worktree:** `/home/ben/repositories/Iverson-followups`, branch `tenant-followups`
**Drift:** none — the spec's last-modifying commit is HEAD.

## Inherited from spec (NOT re-verified)

The spec's `Verified assumptions` section is ground truth for this plan: the five clients' casing
behaviour, PascalCase being canonical on the mapped read path, the Qdrant payload arriving as
`IReadOnlyDictionary<string,string>`, and the write-path producers in `IntelligenceStoreConsumer`.

## Tasks NOT in this plan

Preserved from the spec's "Out of scope": the nine remaining conformance-matrix skips, and the
payload flattening in `Iverson.Vector`.

## Known issues inherited from spec

From the spec's "Known issues / accepted as out of scope": type information is lost at the Qdrant
boundary and is repaired downstream rather than at the source, and the two case-insensitive clients
stay case-insensitive.

---

## Task 1 — Emit canonical names and typed values from the SearchSimilar response loop

**Modify:** `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
**Test:** `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`

### Steps

1. **Write the failing test first**, in the existing Qdrant-backed class
   `ObjectSearchVectorIntegrationTests`. It must drive the real RPC
   (`await sut.SearchSimilar(request, writer, TestServerCallContext.Create())`) so it pins the
   **call site** at `:278-282`, not a helper in isolation. Model it on
   `SearchSimilar_WithRangeFilter_ReturnsOnlyMatchingTypedPayload` (`:96-133`), which already seeds
   `["wordCount"] = 100L` via `_vector.UpsertNamedAsync` and uses `MakeStream<SearchResponse>()`
   (`:87`). The new test asserts on `written[0].Data` — which no existing test inspects — covering:
   - the emitted key is `WordCount`, not `wordCount`;
   - `Data.Fields["WordCount"].NumberValue` is `100`, i.e. `KindCase` is `NumberValue`, not
     `StringValue`;
   - the identity field is emitted as the key column's name (`Id`) and survives masking. This
     requires seeding `["key"]` in the payload dictionaries — the existing test seeds only
     `wordCount` and `tenantId`, so without it the assertion has no subject.

   **The schema under test must carry both an uppercase and a lowercase `SqlType`** — e.g. keep
   `new ColumnDescriptor("WordCount", "integer", false)` and add one declared `"BIGINT"`, seeding a
   payload value for it in both `UpsertNamedAsync` calls and asserting its emitted `KindCase` is
   `NumberValue` — declaring the column alone puts no field in the emitted `Struct`. Production
   emits uppercase (`SchemaBuilder.cs:351-359`) while `SchemaFixtures` uses lowercase
   (`SchemaFixtures.cs:57-64`); a test exercising only one case cannot falsify a
   comparison that handles only the other.

2. **Run the test and watch it fail** for the stated reason (string-typed value / camelCase key),
   not for a setup error.

3. **Implement** at `:278-282`. Replace the
   `foreach (var kvp in r.Payload) protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);` loop
   with a resolution through a lookup built **once per request**, before the streaming loop
   (`schema` is in scope from `:130`):

   - **Name lookup** — camelCased descriptor name to descriptor name, over `schema.ScalarColumns`
     and `schema.KeyColumn`, plus the explicit special case `"key"` to `schema.KeyColumn.Name`.
     This special case is load-bearing and is **not** derivable from the lookup:
     `ToCamelCase("Id")` is `"id"`, but `IntelligenceStoreConsumer.cs:417` writes the identity
     entry under the literal payload key `"key"`, and `SchemaBuilder.cs:53`
     (`Where(p => !p.IsKey)`) keeps the key column out of `ScalarColumns`. A key not in the lookup
     falls back to `StructSerializer.UpperFirst` (same namespace `Iverson.Api.Grpc`, no using
     needed) — load-bearing for vector fields (`:422`) and FK columns (`:440`).
   - **Type mapping** — from the resolved column's `SqlType`, per spec §2's exhaustive vocabulary.
     Two constraints the vocabulary alone does not state:
     - Compare **case-insensitively** (`StringComparer.OrdinalIgnoreCase`, matching the existing
       `SqlTypeMap` at `SchemaBuilder.cs:391`). A C# `switch` on `string` is ordinal, so a literal
       switch over the spec's uppercase vocabulary silently falls through to string for every
       lowercase-declared column.
     - Match the SqlType **exactly, never by prefix**. `"DOUBLE PRECISION[]"` prefix-matches
       `"DOUBLE PRECISION"`; array types are absent from the vocabulary and must reach the default
       `Value.ForString`, per spec §2 ("Array types keep the string form").
     - A value that does not parse falls back to `Value.ForString` rather than failing the row.
     - A payload key with no descriptor column emits `Value.ForString`.
   - **Add `using System.Globalization;`** to the file — it is not currently imported, and the
     numeric parses take `CultureInfo.InvariantCulture`.
   - **Masking** — leave `exemptField: "Key"` unchanged; it is inert for the identity field once
     the keys are canonical, so this task does not touch it.

   Do **not** reuse `SchemaBuilder.SqlTypeToPayloadKind`: it is private to `SchemaBuilder`, and it
   assigns `INTEGER[]` the `Integer` kind (`SchemaBuilder.cs:369`) although an array's payload value
   is a serialized string — reusing it would mis-type arrays as numbers.

4. **Re-run the test class** — expect green.

5. **Run the full `Iverson.Api.Tests` suite** and read the counts off the process before any shell
   chaining. Reap testcontainers afterwards (`scripts/reap-testcontainers.sh`).

6. **Commit** — lowercase imperative subject, no prefix (repo convention, `git log --format=%s`),
   e.g. `bind vector-search results to descriptor names and typed values`.

---

## Task 2 — Verify against the live conformance matrix

**Modify:** nothing. Verification only.

### Steps

1. Rebuild the API image and redeploy the dev stack.
2. Run the client conformance matrix (`docs/runbooks/client-conformance-matrix.md`).
3. Confirm `vector-search` is `ok` for all five languages, and that the full-matrix run exits 0
   with no `FAIL: this was a full-matrix run...` line on stderr — the harness prints an untouched
   line only when `untouched.Count > 0` (`Program.cs:252-257`) and expresses success through
   `Report.RunSucceeded`'s exit code (`:260`).
4. Report the actual cell states read off the run. Do not report a cell not personally read to
   completion.

**Ordering:** Task 2 strictly follows Task 1 — it verifies Task 1's change against a live stack.
Task 1 introduces no symbol Task 2 defines.

---

## Verified plan-level assumptions

| # | Assumption | Evidence | Status |
|---|---|---|---|
| A1 | `ObjectSearchGrpcService.cs` exists at the cited path | 44,519 b | holds |
| A2 | `ObjectSearchVectorIntegrationTests.cs` exists at the cited path | 8,028 b | holds |
| A3 | `SchemaDescriptor` exposes `KeyColumn` / `ScalarColumns` | `SchemaDescriptor.cs:27-28` | holds |
| A4 | `ColumnDescriptor` carries `Name` and `SqlType` | `SchemaDescriptor.cs:99` | holds |
| A5 | `StructSerializer.UpperFirst` is reachable | `ProtoPayloadHelper.cs:4,12` — `internal static`, namespace `Iverson.Api.Grpc`, same as the target file | holds |
| A6 | `ToCamelCase` is reachable | `NamingExtensions.cs`, namespace `Iverson.Api` (parent); file already calls `.ToSnakeCase()` with no using | holds |
| A7 | `Value.ForString` / `ForNumber` / `ForBool` exist | `ObjectSearchGrpcService.cs:935-943` uses `Value.ForString`; `Google.Protobuf.WellKnownTypes` imported at `:1` | holds |
| A8 | `MaskDisallowedFields` takes a third `exemptField` arg | `AuthorizationFieldMasking.cs:195-198` | holds |
| A9 | `r.Payload` is `IReadOnlyDictionary<string,string>` | `Iverson.Vector/IVectorRoles.cs:49-52` | holds |
| A10 | `schema` is in scope before the streaming loop | declared `:130`; `SearchSimilar` declared `:125`; loop at `:278` in the same method body | holds |
| A11 | `Iverson.Api.Tests` is a real project in the solution | `Iverson.slnx` contains it; `Iverson.Api.Tests.csproj` present | holds |
| A12 | Test host class is Qdrant-backed | `ObjectSearchVectorIntegrationTests` uses `QdrantGrpcContainerFixture` | holds |
| A13 | Testcontainer reaping script exists | `scripts/reap-testcontainers.sh` | holds |
| A14 | Commit convention is lowercase imperative, no prefix | `git log --format=%s -15` | holds |
| A15 | Task ordering has no hidden cross-dependency | Task 2 is verification-only; introduces no symbols | holds |
| A16 | `Google.Protobuf.WellKnownTypes` is imported | `ObjectSearchGrpcService.cs:1` | holds |
| A17 | `System.Globalization` is imported | **FAILED** — `grep -c` returns 0. Task 1 step 3 now adds the using. | failed, folded in |
| A18 | The test fixture seeds payloads via `UpsertNamedAsync` with a numeric value | `ObjectSearchVectorIntegrationTests.cs:115-120` (`["wordCount"] = 100L`) | holds |
| A19 | `MakeStream<SearchResponse>()` exists | `ObjectSearchVectorIntegrationTests.cs:87` | holds |
| A20 | (Cat 6) The payload's identity key is written as literal `"key"` | `IntelligenceStoreConsumer.cs:417` | holds |
| A22 | SqlType can be compared with a plain `switch` | **FAILED** — production emits uppercase (`SchemaBuilder.cs:351-359`), fixtures lowercase (`SchemaFixtures.cs:57-64`); the existing `SqlTypeMap` uses `OrdinalIgnoreCase` (`SchemaBuilder.cs:391`). Task 1 now mandates case-insensitive exact matching, and step 1 mandates a test covering both cases. | failed, folded in |
| A23 | `ArticleSchema()`'s columns support the new test | `SchemaFixtures.cs:52-71` — `KeyColumn` `Id`/`uuid`, scalars `Title`/`Body`/`AuthorId`; the existing test already appends `WordCount` | holds |
| A24 | The flattened payload string parses back to the value the mapping needs | `IntelligenceVectorService.ToCanonicalString:202-207` — `IntegerValue`/`DoubleValue` via `CultureInfo.InvariantCulture`, `BoolValue` as lowercase `"true"`/`"false"`; accepted by `double.TryParse(..., InvariantCulture)` and `bool.TryParse` | holds |
| A25 | Renaming payload keys to descriptor names cannot leak the tenant column | `RemoveTenantColumn` (`AuthorizationFieldMasking.cs:171-178`) filters on `IsTenantColumn`, which compares `OrdinalIgnoreCase` against `"__TenantId"` (`SchemaDescriptor.cs:13,20-21`); it runs before the `allowedFields is null` early return, and matches the renamed form equally | holds |
| A26 | The live matrix verifies the naming half only | `VectorDoc` is `id: uuid.UUID` plus six `str` fields (`Iverson.Clients/Python/conformance/models.py:133-139`) — no numeric, boolean, or timestamp property, so Task 2 cannot detect a broken typed-value mapping; typed values rest on Task 1's unit test | holds |

**Sibling sweep.** Every payload-emitting site in the target file: `Value.ForString(kvp.Value)`
appears once (`:280`) and `MaskDisallowedFields` once (`:282`) — the other three hits are comments
(`:102`, `:603`, `:681`). The StarRocks-backed paths at `:115`, `:611`, `:689` already go through
`DictToProtoStruct`. There is no second broken path to fix. Every `SqlType` string the mapping
compares was enumerated from `ScalarTypeMap` and `ArrayTypeOverrides`
(`SchemaBuilder.cs:351-380`), which is what produced the array and casing findings above.
