# Critical Design Review: 2026-08-24-vector-search-entity-binding-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson-followups/docs/specs/2026-08-24-vector-search-entity-binding-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections
| Row | Disposition |
|---|---|
| The problem | ok — matrix figures match the live run (38 ok / 9 skip / 3 FAIL); vector-search is the only failing scenario |
| Root cause | ok — the quoted loop matches `ObjectSearchGrpcService.cs:278-280` verbatim; the three cited client binders read as described |
| Design 1 — canonical key names | → §2.1 (rule covers one of four payload-key producers) |
| Design 2 — typed values | → §2.1 (type rule has no defined behaviour for keys outside ScalarColumns) |
| Design 3 — masking | ok — `AuthorizationFieldMasking.cs:208` compares `UpperFirst(key)`; after the change `UpperFirst` is identity on descriptor names, so `AllowedFields` still matches, and the spec correctly requires `exemptField` to become the key column's name |
| Why the server and not the clients | ok — both cited binders re-read: `StructConverter.cs:15` sets `PropertyNameCaseInsensitive = true`; `StructConverter.java:68` lowercases both sides |
| Testing | ok — `Iverson.Api.Tests/Grpc/` contains both named test classes |
| Out of scope | ok — the 9 skips and the `Iverson.Vector` flattening are genuinely separate; 47 `.Payload` consumers confirmed |
| Known issues | ok — both entries state real accepted trade-offs with the decision attributed |
| Verified assumptions | see §1 |

### Rules and operands
| Row | Disposition |
|---|---|
| R1 name resolution — under-inclusion (payload key absent from the lookup) | → §2.1 — the payload carries keys from four producers (`:417` literal `key`, `:422` VectorFields, `:435` ScalarColumns, `:438` FkColumns); the design's lookup covers `:435` plus the key column |
| R1 name resolution — over-inclusion (two descriptor names colliding on one camelCase key) | dropped — `ToCamelCase` only lowercases index 0, so a collision needs two descriptor names differing solely in first-letter case; not reachable in the spec's outcome and not created by this design |
| R2 `key` identity rule — over-merge | dropped — a schema declaring its own `Key` scalar property would have `pointPayload["key"]` overwritten at WRITE time (`:417` then `:435`), so the ambiguity is pre-existing on the write path and not introduced or worsened by this design |
| R3 `UpperFirst` fallback correctness | ok — `ProtoPayloadHelper.cs:13` `char.ToUpperInvariant(s[0]) + s[1..]` is the exact inverse of `NamingExtensions.cs:17` `char.ToLowerInvariant(name[0]) + name[1..]`, so FK columns and vector fields resolve to correct NAMES through the fallback |
| R3 `StructSerializer.UpperFirst` is a real, reachable symbol | ok — declared in `ProtoPayloadHelper.cs` (file name differs from the class), `internal` in namespace `Iverson.Api.Grpc`, same assembly as the change site |
| R4 SqlType → Value kind — unlisted SqlType | ok — the spec enumerates all 15 values in `ScalarTypeMap`/`ArrayTypeOverrides` exhaustively and states the array carve-out |
| R4 SqlType → Value kind — unparseable value | ok — spec states the string-form fallback |
| R4 array claim ("no list to reconstruct") | ok — `ToCanonicalString` (`IntelligenceVectorService.cs:202-209`) has no `ListValue` case and falls through to `v.ToString()`; the claim is true |
| R5 tenant-column masking survives the rename | ok — `IsTenantColumn` compares `OrdinalIgnoreCase` and `ToCamelCase("__TenantId")` is unchanged (index 0 is `_`), so the column is stripped identically before and after |

### Data-flow arrows
| Row | Disposition |
|---|---|
| Qdrant point → `VectorSearchResult.Payload` **(serialization boundary)** | ok — `IntelligenceVectorService.cs:107` flattens typed Qdrant values to string; `IVectorRoles.cs:52` types the DTO as `IReadOnlyDictionary<string,string>`. This is the loss the spec repairs downstream, stated in Known issues |
| `r.Payload` → `protoStruct` | → §2.1 (the change site) |
| `protoStruct` → `MaskDisallowedFields` → wire | ok — parameters traced: `AllowedFields` holds descriptor names, compared via `UpperFirst(key)`; `exemptField` must change, and the spec says so |
| wire → client binders (5) | ok — python `_entity_from_struct` PascalCases and matches; .NET and Java are casing-agnostic; both gain the previously-unbound identifier rather than losing anything |
| descriptor → lookup | ok — `schema` is in scope (`ObjectSearchGrpcService.cs:136`); `ScalarColumns` and `KeyColumn` are separate members of `SchemaDescriptor` (`:27-28`), so the spec correctly names both |
| wire → orchestrator's own probe **(second caller of the same operation)** | ok — `CountSimilarVisibleAsync` (`VectorSearchScenario.cs:456-475`) only counts `ResponseStream.MoveNext`; it reads no field name, so renaming cannot affect the projection wait |
| python client unit test → fixture-built Struct | dropped — `test_entity_coordinator.py:72-76` builds `Id`/`Title` while the server sends `key`/`title`, so the test is green over the production defect. Real, but the fix does not depend on it and the fixture becomes accidentally correct afterwards; no literal-wrongness against the spec's outcome |

## 1. Verified-assumptions cross-check

All thirteen listed assumptions reconfirmed under a fresh read:

- Only one site turns a Qdrant payload into a response Struct — reconfirmed and **widened**: repo-wide grep for `.Payload` reaching a `Struct`/`Fields[` returns only `ObjectSearchGrpcService.cs:278`; the `StructSerializer.SerializePayload(request.Payload)` hits in `ObjectPersistenceGrpcService` and `ObjectMappingGrpcService` are the inbound write payload, a different object.
- StarRocks paths already emit descriptor names and typed values — reconfirmed at `:115`, `:611`, `:689`.
- Payload keys are the camelCase of descriptor names — reconfirmed at `:435`, **but incomplete as scoped**; see the span check.
- The payload's key field is literally `key` — reconfirmed at `IntelligenceStoreConsumer.cs:417`.
- The mapped read path emits descriptor property names — **upgraded from behavioural inference to direct evidence**: `ObjectRetrievalGrpcService.cs:43` parses the stored row JSON into a Struct verbatim, and that JSON's keys are UpperFirst'd by `StructSerializer.SerializePayload`. The spec cited only "python's crud passes"; the file:line now exists.
- `schema` in scope; PascalCase safe for the passing clients; masking semantics; payload values are strings; 47 `.Payload` consumers; bounded SqlType vocabulary; no reusable parser; test project exists; harness independent of `key` — all reconfirmed at the cited locations.

**Span check — one uncovered dependency:**

The design depends on knowing every producer of a Qdrant payload key, because its rules are defined per-key. No listed assumption covers that set. The assumption as written ("payload keys are the camelCase of descriptor names", evidenced at `:435`) verifies one producer; `BuildPointPayload` has four (`:417`, `:422`, `:435`, `:438`). Verified in-round; promoted to §2.1.

## 2. Literal-wrongness findings

### 2.1 The design's rules cover one of four payload-key producers, and the type rule has no defined behaviour for the other three

**Description.** `BuildPointPayload` (`IntelligenceStoreConsumer.cs:412-442`) writes the Qdrant payload from four sources: the literal `key` (`:417`), vector fields keyed `vf.PropertyName.ToCamelCase()` (`:422`), scalar columns keyed `col.Name.ToCamelCase()` (`:435`), and **foreign-key columns keyed `fk.ColumnName.ToCamelCase()` (`:438`)**. The spec's lookup is built "over the schema's scalar columns and its key column" and its type rule reads "the descriptor column's `SqlType`".

The NAME half survives: FK columns and vector fields miss the lookup, hit the `UpperFirst` fallback, and — because `UpperFirst` is the exact inverse of `ToCamelCase` — come out correct. The spec is right by accident here, and describes the fallback as existing "because an unmapped field is a diagnosis the caller should still be able to make", when it is in fact the primary path for two entire classes of payload key.

The TYPE half does not survive. `SchemaDescriptor.FkColumns` and `VectorFields` are separate members from `ScalarColumns`, so for those keys there is no `SqlType` to consult and the spec states no rule. An implementer reading the spec literally has three defensible readings, and one of them — skip any key with no descriptor column, mirroring how the lookup is described — silently drops every foreign key from vector-search results. That is precisely the silent-drop class the spec exists to eliminate, reintroduced one field over.

**Evidence.**
- `IntelligenceStoreConsumer.cs:417,422,435,438` — the four producers.
- `SchemaDescriptor.cs:27-28` and the `FkColumns` / `VectorFields` members — FK columns and vector fields are not in `ScalarColumns`, so neither the lookup nor the SqlType is available for them.
- `IntelligenceStoreConsumer.cs:439` — FK values are written with `ExtractTypedValue(payload, fk.ColumnName, "TEXT")`, i.e. always text.
- Spec, Design §1 and §2 — both rules are scoped to scalar columns plus the key column.

**Proposed fix.** State the rule over all four producers rather than one. The name rule already works; say so explicitly, so the fallback is documented as load-bearing rather than diagnostic. For the type rule, add: a payload key with no descriptor column emits `Value.ForString` — correct for all three uncovered producers, since FK columns are written as `TEXT` (`:439`), vector fields as extracted text (`:422`), and `key` is a string (`:417`). Naming the default also removes the reading in which such keys are dropped.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 has one item, §3 is empty. The design's central diagnosis, its single-site scoping, its choice of PascalCase, and its masking change all hold under fresh reading. §2.1 is a completeness gap in how the rules are stated, not a wrong mechanism: fix the wording and the implementer cannot arrive at the dropping reading.
