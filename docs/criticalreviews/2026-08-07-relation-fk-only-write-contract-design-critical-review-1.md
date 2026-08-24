# Critical Design Review: 2026-08-07-relation-fk-only-write-contract-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-07-relation-fk-only-write-contract-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem (incl. client table, Go defect, declaration-model split) | ok — re-read `coordinator.go:440-447` and `registrar.go:266-269`; the skip covers `KindManyToOne` and the field name is the FK, so the "null FK on every Go write" claim holds |
| Contract (the four-kind FK key table) | ok — checked each kind against `inferFK`/`inferFk`/Java `relatedTypeName + "Ids"`; all four clients agree on the same four names |
| Server (deletions, remaining checks) | ok — traced every named symbol (`ValidateNestedObject`, `ReadNestedKey`, `KeyColumnNameFor`, `RemoveField`, `Capture`/`RestoreNavProperties`) to its declaration and callers |
| Server → Ordering constraint | ok — `ObjectMappingGrpcService.cs:294` precedes `:299`; carve-out present at `RowFieldAuthorizationEvaluator.cs:92-95` |
| Clients (classification rule) | → §2.1 |
| Clients → Go | ok — OneToMany exclusion is required; `author.go:8` is a real `[]string` one_to_many that would otherwise emit under `AuthorId` |
| Clients → Java | ok — `toStruct` iterates `getAllFields`; `isRelationField` exists; no `Collection` branch in `toValue` |
| Clients → .NET | ok — `EntityDescriptor.Relations` exists; `_descriptor` in scope at all four write sites |
| Clients → Python / TypeScript | → §2.1 |
| Consequences (Go behavior, StarRocks eligibility, ship-together) | ok — verified the eligibility mechanism at `SchemaBuilder.cs:106-112`; see the rules table below |
| Testing | → §1 span check (server-side test impact understated) |
| Verified assumptions | ok — cross-checked in §1 |
| Known issues | ok — A22's consequence is stated accurately; no new claim introduced |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Classification: "member holds ids" vs "member holds entities"** | misclassify a nav collection as ids → emits a phantom/clearing FK | misclassify an FK member as nav → FK silently dropped | → §2.1 — decidable in Go (`reflect` static field type) and .NET/Java (declared type), **not decidable in Python or TypeScript** |
| **Server rejection predicate: "nav key present and distinct from FK"** | a `NullValue` nav key counts as present → rejects callers that serialize every property | `PropertyName == ForeignKey` collision → never rejected | → §3.1 (over-inclusion direction); under-inclusion is correct by design — the collision case has no separate key to reject |
| **FK-name inference per kind** | OneToMany would emit `{ThisType}Id`, a column on the *related* row | ManyToMany field name ≠ FK column name → ids lost | ok — spec excludes OneToMany explicitly and maps m2m through `inferFK`; both directions covered |
| **Required-relation check (non-nullable FK column)** | fires when the column is merely absent from the schema | misses ManyToMany entirely | ok — unchanged from current behavior; ManyToMany has no required check today and the spec adds none |
| **StoreTargeting m2m eligibility** — producers of `FkColumns` | — | a producer the spec didn't name | ok — grep'd the single producer: `SchemaBuilder.cs:106-112` registers **by name suffix** (`Id`/`Ids`) over all declared properties regardless of type, so `Guid[] ArticleIds` and `List<UUID> tagIds` both register. The spec's eligibility claim holds and is not defeated by the array type |

### Data-flow arrows

| Arrow → consuming operation | Disposition |
|---|---|
| client member → payload key (**serialization boundary**) | → §2.1 — the operation is "emit under the inferred FK name or omit", and its deciding parameter is not derivable in two clients |
| payload → `ValidateAndNormalizeRelations` | ok — all 4 call sites confirmed; the operation's parameters (`payload`, `schema`) are unchanged by this design |
| payload → `SerializePayload` → outbox/Kafka → stores (**persistence boundary**) | ok — `payloadJson` is computed from the Struct before any echo; no consumer reads a relation `PropertyName` (the `PropertyName` hits in `IntelligenceStoreConsumer` are vector/chunk fields) |
| read path hydration → user object → write-back | ok — `EntityRelationResolver.cs:96,137,176` inject nav into read results; under this design the client omits them on the way back out, so round-trip does not trip the new rejection |
| `StructConverter.ToStruct` **call site 1** — `EntityCoordinator.cs:54,70,105,121` (write) | ok — `_descriptor` available; this is the site that gains omission |
| `StructConverter.ToStruct` **call site 2** — `GraphAssembler.cs:95,209` (read-side FK extraction) | ok — reads `joinKey`/FK values only, which are never omitted; a defaulted parameter keeps both compiling. Separately confirms .NET `Tag`'s m2m *read* is also empty today for want of `ArticleIds` — consistent with A11, not a new defect |
| Java `toStruct` → `toValue` (collection element conversion) | ok — no `Collection` branch exists; every list currently reaches `val.toString()` at `StructConverter.java:102` |

## 1. Verified-assumptions cross-check

All 26 listed assumptions reconfirmed against their cited evidence. Spot-checks that mattered:

- **A2** re-grep'd across `Iverson.Server/` including test projects: `RemoveField` has exactly two hits — its declaration and `RelationValidator.cs:51`. Deletion claim holds.
- **A7** reconfirmed: `Candidates` survives via `EntityKeyAccessor.cs:15,23`, which is untouched by this design.
- **A12** reconfirmed by a different route than the spec used: `SchemaBuilder.cs:106-112` registers FK columns by name suffix over all declared properties, so the newly-added fields will register and eligibility will flip as the spec's Consequences section states.
- **A24** extended to the four non-.NET clients: no test in `Go/iverson_test`, `Python/tests`, `TypeScript/tests` or `Java/client/src/test` references `entityToStruct`, `_entity_to_struct`, `toStruct` or `entityToPayload`. The claim holds, and more strongly than stated — there is currently **no write-serialization test coverage in any client**.

### Span check — one uncovered dependency

**The spec's Testing section understates server-side test impact.** A8 covers `RelationValidatorTests` only. Deleting `Capture`/`RestoreNavProperties` also invalidates two tests in a different file that assert the *echoed response retains the nav property*:
`ObjectMappingGrpcServiceTests.cs:725` (`MappingPost_NavPropertyPresentInResponsePayload`) and `:753`
(`MappingPost_CamelCaseNavPropertyKeyPreservedInResponsePayload`). Both must be deleted, not adapted — under this design their scenario is rejected before a response exists. `AuthorizationFieldMaskingTests.cs` also references nav property names and needs a read.

This is a completeness gap in the spec, not a design defect: the echoed-payload behavior these tests pin is deliberately made unreachable by the rejection rule. Verified in-round, so it needs no forced decision — the spec's Testing section should name these files.

## 2. Literal-wrongness findings

### §2.1 — The type-based classification rule is not decidable in Python or TypeScript, and one misclassification silently clears a relation

**Description.** The Clients section rejects a name-based test in favor of a type-based one: *"the distinguishing question is not what the relation-marked member is called but what it holds"* — ids versus entities. That is decidable in Go (`reflect` exposes the static field type: `string`/`[]string` versus a struct), and in .NET and Java (the declared property/field type). It is **not decidable in Python or TypeScript**, which are the two clients where the spec applies the rule to distinguish an FK member from a nav member:

- **TypeScript** erases types at runtime. `entityToPayload` sees only values. `tagIds: string[] = []` and a hypothetical `tags: Tag[] = []` are both an empty array — indistinguishable. The spec gives the implementer no answer for the empty case, which is the default state of every unpopulated collection.
- **Python** has no element type in the annotation at all: the sample declares `articles: list = one_to_many("Article")` (`sample/models.py:29`). Worse, an unset field returns the **class-level `FieldMeta` sentinel**, not a list — `_entity_to_struct` does `getattr(entity, field_name, None)` (`core.py:325`), and the class attribute is the `FieldMeta` returned by `one_to_many(...)`. So the value is neither "ids" nor "entities", and the rule has no branch for it.

**Why this is literal-wrongness rather than an implementation detail.** The two possible readings produce observably different wire output, and one of them loses data. If TypeScript classifies an empty `tags: Tag[] = []` as "holds ids", it emits `TagIds: []`. The server writes that empty list into the FK column, replacing whatever was stored — so a caller who simply never populated the nav collection **silently clears the entity's existing many-to-many relation on update**. The spec's own Contract section says a nav property "must not appear in a write payload"; following the spec's stated mechanism can produce the opposite of its stated contract.

**Evidence.**
- `Iverson.Clients/TypeScript/src/core.ts:358-372` — `entityToPayload` iterates `Object.getOwnPropertyNames` and reads values; no type information is available.
- `Iverson.Clients/Python/iverson_client/core.py:325` — `value = getattr(entity, field_name, None)`; `Iverson.Clients/Python/sample/models.py:29` — `articles: list = one_to_many("Article")`.
- `Iverson.Clients/Go/iverson/coordinator.go:435-437` — Go reads `t.Field(i)` and has the static type, so Go is unaffected.

**Proposed fix.** Make the classification **kind-first** rather than type-first, and state it as such:

1. `OneToMany` → always omitted, in every client. The spec already mandates exactly this for Go and justifies it correctly (`inferFK` returns a column on the related row); generalizing it removes the only case where Python's and TypeScript's ambiguity is reachable.
2. `ManyToOne` / `OneToOne` / `ManyToMany` → in the clients that mark the FK-bearing member (Go, Python, TypeScript) the marked member *is* the FK by construction; emit it under the inferred FK name with no type test.
3. Keep the type test only for .NET and Java, where a nav member and an FK field genuinely coexist and the declared type is available at the decision point.

The relation kind is already present at every decision point: `meta["relations"]` carries `kind` (`core.py:166` builds a set from it), TypeScript's `getRelations()` returns `RelationMeta.kind`, and Go's `ParseTag` yields `fm.RelationKind`.

## 3. Forced decisions

### §3.1 — Does a `NullValue` nav key count as "present" for the rejection?

**The choice.** The Server section specifies rejection when *"the nav property key is present and distinct from the FK."* It does not say whether a key whose value is protobuf `NullValue` counts as present.

**Why it is forced.** The codebase already had to answer this exact question in the other direction, and answered it explicitly. `RelationValidator.cs:78-81` treats a `NullValue` **foreign key** as ABSENT, with a comment recording why: *"The .NET client serializes every property, so a null nullable FK arrives as `authorId: null`"* — and treating it as present made a legitimate omittable FK fail GUID validation. Java's converter has the same property: `toStruct` puts every field and `toValue(null)` emits `NullValue` explicitly. So the two clients that carry separate nav members are also the two that emit nulls for unset ones, and the design's answer determines whether a caller sending `Author: null` is rejected or ignored. Leaving it unstated means the implementer picks silently, and the existing FK precedent points one way while a literal reading of "present" points the other.

**The options.**
- **`NullValue` counts as absent** (consistent with the existing FK rule): a null nav key is tolerated. Any client that serializes all properties keeps working even before it is updated, softening the ship-together constraint. Cost: a caller that meant to send an embedded object but produced a null gets no diagnostic.
- **`NullValue` counts as present** (literal reading): a null nav key is rejected. Maximally strict and self-describing; catches a partially-updated client immediately. Cost: breaks any direct caller that serializes nulls, including a .NET or Java caller not built against the updated client.

## 5. Recommendation

🛑 **Surface forced decisions to user**

§3.1 needs your ruling before planning. §2.1 requires a spec edit to the Clients section — the fix is contained (state the rule kind-first, keep the type test for .NET and Java only) and does not disturb any other section. The §1 span gap is a one-line addition to Testing naming the two `ObjectMappingGrpcServiceTests` cases and a read of `AuthorizationFieldMaskingTests`.
