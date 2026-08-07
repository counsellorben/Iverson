# Critical Design Review: 2026-08-07-relation-fk-only-write-contract-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-07-relation-fk-only-write-contract-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem (client table, Go defect, declaration-model split) | ok — re-read `coordinator.go:435-447`; the skip and the `fm.Name`-is-the-FK claim both hold |
| Contract (four-kind key table) | ok — checked each kind against all five registrars' inference: .NET `SchemaRegistrar.cs:268-276`, Go `inferFK`, Java `:333-339`, Python `_infer_fk`, TS `inferFk`. All five agree on the same four names |
| Server (deletions, three remaining checks) | ok — every named symbol traced to declaration and callers |
| Server → `NullValue` ruling (new this round) | ok — matches `RelationValidator.cs:78-81`; the rule it mirrors is unchanged |
| Server → Ordering constraint | ok — `:294` precedes `:299`; carve-out intact at `RowFieldAuthorizationEvaluator.cs:92-95` |
| Clients → kind-first rule (new this round) | → §2.1 |
| Clients → Per-client: Go | ok — see the `relationPropertyName` row below; Go's descriptor shape differs from Python/TS but the design still resolves correctly |
| Clients → Per-client: Java, .NET | ok — `toValue` Collection branch covers `List<UUID>`; .NET `Guid[]` round-trips through `JsonParser` as a ListValue |
| Clients → Per-client: Python, TypeScript | → §2.1 (Python); TS ok — `entityToPayload` assigns arrays raw and the proto layer converts them |
| Consequences | ok — eligibility mechanism re-verified at `SchemaBuilder.cs:106-112` |
| Testing | ok — the two `ObjectMappingGrpcServiceTests` cases and the `AuthorizationFieldMaskingTests` read are named |
| Verified assumptions | ok — cross-checked in §1 |
| Known issues | → §2.1 — the "Python `str()` generally" bullet is now load-bearing in a way it wasn't when written |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Kind-first classification** | omit a member that is the FK → FK dropped | emit a nav member → rejected write | ok on classification itself — kind is available at every decision point (`meta["relations"]["kind"]`, `RelationMeta.kind`, `fm.RelationKind`), and OneToMany-always-omitted removes the ambiguous case. The **value** side of the rule is where it breaks → §2.1 |
| **Server rejection: nav key present and distinct from FK** | `NullValue` counts as present | `PropertyName == ForeignKey` collision never rejects | ok — the `NullValue` direction is now explicitly ruled absent; the collision direction is correct (Python and TS both send `PropertyName == ForeignKey == AuthorId`, so there is no separate key to reject) |
| **`relationPropertyName` — Go's descriptor shape** | Go strips the trailing `Id` (`registrar.go:281-285`), so Go sends `PropertyName=Author`, `ForeignKey=AuthorId` — **distinct**, unlike Python and TS which send them equal | — | ok — the spec groups Go with Python/TS as "marks the FK-bearing member", which is true of the *field* but produces a different descriptor. It still resolves: Go emits only `AuthorId`, so the `Author` key is absent and no rejection fires. Worth knowing, not wrong |
| **FK-name inference per kind, per client** | OneToMany emitting `{ThisType}Id` | m2m field name ≠ FK column | ok — spec excludes OneToMany universally now; m2m maps through inference in all three FK-on-field clients |
| **StoreTargeting m2m eligibility** — producers of `FkColumns` | — | a producer the spec didn't name | ok — single producer, `SchemaBuilder.cs:106-112`, registers by name suffix over all declared properties regardless of type; `Guid[] ArticleIds` and `List<UUID> tagIds` both register |

### Data-flow arrows

| Arrow → consuming operation | Disposition |
|---|---|
| Python marked member → `s.fields[pascal]` assignment (**serialization boundary**) | → §2.1 — the operation is a per-type branch assignment, and there is no branch for the type the kind-first rule now routes to it |
| TypeScript marked member → `payload[key]` → proto Struct (**serialization boundary**) | ok — `core.ts:358-371` assigns raw; arrays survive as ListValue through the proto layer |
| Java marked member → `toValue` → `putFields` (**serialization boundary**) | ok — the spec's `Collection` branch is what makes `List<UUID>` a ListValue; without it `toString()` |
| .NET entity → `ToStruct` JSON round-trip → Struct (**serialization boundary**) | ok — `Guid[]` serializes to a JSON array, `JsonParser` yields ListValue |
| Go field → `goValueToProtoValue` → `fields[key]` (**serialization boundary**) | ok — `[]string` already has a list path; this is the same converter the non-relation fields use |
| payload → `ValidateAndNormalizeRelations` → `SerializePayload` → outbox/Kafka (**persistence boundary**) | ok — unchanged by this design; `payloadJson` computed from the Struct, no consumer reads a relation `PropertyName` |
| `StructConverter.ToStruct` **call site 1** — `EntityCoordinator.cs:54,70,105,121` | ok — `_descriptor` in scope; the site that gains omission |
| `StructConverter.ToStruct` **call site 2** — `GraphAssembler.cs:95,209` | ok — reads FK/joinKey values only, never omitted |

## 1. Verified-assumptions cross-check

All 27 listed assumptions reconfirmed. A27 (added after round 1) checked against fresh reads of `ObjectMappingGrpcServiceTests.cs:725,753` — both still assert the echoed response retains the nav property, so the ❌ status is correct as written.

Two re-verified by a different route than the spec used:

- **A12** — `SchemaRegistrar.java:92-94` collects relation fields into `navFieldNames`; combined with `SchemaBuilder.cs:106-112` registering FK columns by name suffix, Java `Article` has no `TagIds` column today. Holds.
- **A25** — confirmed all four kinds present in .NET (`SchemaRegistrar.cs:271-274`), Go, Java (`:334-337`), Python (`_infer_fk`) and TS (`inferFk`).

### Span check — one uncovered dependency

**No assumption covers whether each client can serialize the FK *value type* it is now required to emit.** The listed assumptions cover where relation metadata lives (A13, A15, A17, A19) and where the omission decision is made, but none covers what happens to the value once the decision is "emit this under the FK name". For ManyToMany that value is a list, and the five clients have five different list paths — one of which does not exist. This gap is what §2.1 falls through. Verified in-round; no forced decision needed.

## 2. Literal-wrongness findings

### §2.1 — Python cannot serialize a ManyToMany id list, and the kind-first rule newly routes one through it

**Description.** The Contract requires a ManyToMany foreign key to be `{RelatedType}Ids` — *"a list of id strings"*. The kind-first rule assigns Python the job of producing it: *"in the clients that mark the FK-bearing member (Go, Python, TypeScript) the marked member is the foreign key by construction; serialize its value under the inferred FK column name."* For a Python ManyToMany the marked member is a list.

`_entity_to_struct` has **no list branch**. Its type ladder is `bool → int → float → str → uuid.UUID → else: str(value)` (`core.py:330-341`). A list therefore reaches the `else` and is emitted as the *string* `"['a', 'b']"` under `TagIds`, not a `ListValue`.

Downstream, the new validator reads `fkValue?.ListValue`, which is null for a string, so nothing validates it — and with the nav branch deleted there is no second path to catch it either. The stringified list is written into an array column. This is precisely the defect the spec identifies in Java and fixes there (`toValue`'s `Collection` branch), left unfixed in the one other client the same rule now points at the same way.

**Why it is not already covered by Known issues.** The bullet *"Python `str()` serialization generally … Python's converter still falls through to `str(value)` for any other unhandled type"* was written against the earlier, type-based design, in which Python's relation members were **omitted**. Under kind-first they are **emitted**, so a mechanism the spec previously accepted as background noise is now on the design's own critical path, contradicting the Contract section. The Known-issues wording is also stale for the same reason: it says *"this design omits entity-valued relation members"*, which is now the .NET/Java rule only — Python and TypeScript omit by kind.

**Reachability.** `many_to_many` is a supported Python declaration (`annotations.py:203-205`) and `_infer_fk` handles the kind (`core.py:105-106`). No sample entity declares one, so this is latent rather than currently firing — but the design mandates the behavior, and a Python user following the contract hits it immediately.

**Evidence.**
- `Iverson.Clients/Python/iverson_client/core.py:330-341` — the type ladder, ending in `else: s.fields[pascal].string_value = str(value)`.
- `Iverson.Clients/Python/iverson_client/annotations.py:203-205` — `many_to_many` is a declarable relation.
- `Iverson.Clients/Python/iverson_client/core.py:105-106` — `_infer_fk` returns `f"{related}Ids"`.
- Contrast: `Iverson.Clients/TypeScript/src/core.ts:358-371` assigns arrays raw and they survive as `ListValue`; Go's `goValueToProtoValue` already handles `[]string`.

**Proposed fix.** Add a list branch to `_entity_to_struct` alongside the `Collection` branch the spec already specifies for Java, converting elements recursively through the same ladder:

> **Python** — `_entity_to_struct` omits `OneToMany` members and maps the rest through the inferred FK name. It gains a list branch emitting a `ListValue` of recursively-converted elements — without it a ManyToMany id list reaches the `str(value)` fallback (`core.py:341`) and arrives as a string, which the validator's `fkValue?.ListValue` read silently ignores. This is the same fix as Java's `Collection` branch. It already reads `_iverson_meta` (`core.py:314`), and `core.py:166` establishes the precedent of building a relation-field set from it.

And correct the stale Known-issues bullet to scope its claim to the clients that still omit by type:

> **Python `str()` serialization for non-relation types.** The list branch above covers relation id lists; Python's converter still falls through to `str(value)` for any other unhandled type.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1 (type-based classification undecidable in Python/TypeScript)** — resolved. The Clients section is now kind-first, `OneToMany` is omitted universally, and the type test is scoped to .NET and Java where the declared type is available. The Python and TypeScript per-client paragraphs were updated to match.
- **Round 1 §3.1 (`NullValue` nav key)** — resolved. Ruled absent-and-tolerated, with the rationale and the accepted cost recorded inline at Server step 1.
- **Round 1 §1 span check (server-side test impact)** — resolved. Testing now names `ObjectMappingGrpcServiceTests.cs:725,753` and the `AuthorizationFieldMaskingTests` read, and A27 records the failed assumption.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

§2.1 is a two-line spec edit — one sentence added to the Python per-client paragraph, one Known-issues bullet rescoped. No other section is affected, and §3 is empty.
