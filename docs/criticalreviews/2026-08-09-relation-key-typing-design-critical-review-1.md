# Critical Design Review: 2026-08-09-relation-key-typing-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson-fk-only/docs/specs/2026-08-09-relation-key-typing-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Header / Related | ok — both referenced specs exist at the cited paths |
| Problem — text key columns cannot be read | ok — `EntityRepository.cs:9,16,26,39,64` confirm the five cited sites; live evidence matches |
| Problem — text FK columns break one-to-many | ok — `EntityRelationResolver.cs:154` → `FetchByColumnAsync`; live column dump confirms |
| Contract | ok — `RelationValidator.cs:88,110` and `KeyToUlong` corroborate the UUID invariant |
| Contract — alternative considered and rejected | ok — rejection rationale is sound: `RelationValidator` requires GUID FK values, so TEXT-keyed types could never be relation targets |
| Server — the guard | → §2.1 |
| Clients — Go tag | ok — `go.mod` has no UUID dep; tag vocabulary (`iverson_key`, `iverson_tenant`, `iverson_search_key`) is per-tag, so a new one is additive |
| Clients — TypeScript decorator | ok — `annotations.ts:250` `@IversonArray(elementType)` is the stated precedent and does exactly this |
| Clients — Python | ok — `core.py:37-38` maps `"uuid"`/`"UUID"` → `CLR_GUID`; no new mechanism needed |
| Clients — synthesized FK retype | ok — `core.py:254` `clr_type=CLR_STRING`, `registrar.go`, `core.ts:318` all confirmed as the current value |
| Clients — .NET and Java need no change | ok — `Article.cs:9` `Guid Id`, `Article.java:17` `UUID id` |
| Samples | ok — Go/TS/Python samples all declare string keys as stated |
| Testing | ok — enumerated cases map to the two defects and the two new client mechanisms |
| Consequences | ok — breaking-change framing matches the guard's reach |
| Verified assumptions B1–B8 | see §1 |
| Known issues | ok — migration gap, LoadTest's hardcoded cast, and the retained `::uuid` casts are each real and disclosed |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Key column SQL type must be `UUID`** | rejects a legitimate key | admits a broken key | ok — `ClrTypeToSql(t, isArray:false)`; a key is never an array (`SchemaBuilder.cs:163` passes `false`), so `UUID` is the only conforming value. Breadth over relation-free types is intentional: `FetchByKeyAsync` casts for every type |
| **Relation FK column SQL type must be `UUID`** | rejects a legitimate FK | admits a broken FK | → §2.1 (over-inclusion: rejects every ManyToMany) |
| `OneToMany` exempt from the FK half | — | leaves an FK unchecked | ok — the FK column lives on the related type and is checked when that type registers its reciprocal `ManyToOne`. Verified reciprocals exist in Go (`author.go:8` ↔ `article.go:13`) and Python (`models.py:29` ↔ `:41`). TypeScript's sample declares no `one_to_many` at all, so nothing is left unchecked there |
| Go tag → `CLR_GUID` | tags a non-key property | misses the key | ok — orthogonal to `iverson_key`; both are independent tags on the same field |
| TS decorator → `CLR_GUID` | — | array properties | ok — a GUID key is scalar, so the loop's `@IversonArray` throw is not engaged |
| Synthesized FK `CLR_STRING` → `CLR_GUID` | changes StarRocks/Qdrant typing | — | ok — verified identical: `ArrayTypeOverrides` maps both `ClrGuid` and `ClrString` arrays to StarRocks `STRING` / `PayloadIndexKind.Keyword` (`SchemaBuilder.cs:252-253`); scalar `ClrGuid` and `ClrString` are both `Keyword` (`:236-237`). No index retyping |

### Data-flow arrows

| Arrow | Disposition |
|---|---|
| registration → guard reads column SQL type | ok — `SchemaDescriptor.KeyColumn` is a `ColumnDescriptor` carrying `SqlType` (`SchemaDescriptor.cs:9`), and the FK column is in `ScalarColumns` (`:10`), the same collection the FK-only work's existing check enumerates |
| ManyToOne read → `FetchByKeyAsync(relatedSchema, fkValue)` | ok — casts the FK *value* against the related type's key column; needs the related **key** to be UUID, which the guard now enforces |
| OneToMany read → `FetchByColumnAsync(relatedSchema, relation.ForeignKey, keyValue)` | ok — this is the arrow that requires the **FK column** be UUID; the guard on the reciprocal ManyToOne covers it |
| ManyToMany read → `FetchManyByKeysAsync(relatedSchema, ids)` | ok — **the FK column type is never compared in SQL here**; `EntityRelationResolver.cs:106` reads the id list from the payload and `EntityRepository.cs:16-21` matches the related table's key via `= ANY(@Keys)` with `Guid[]`. Informs §2.1 |
| persisted `_iverson_schema` → `SchemaRegistry` load at boot | ok — the guard runs at registration only, so already-persisted text-keyed schemas keep loading and keep failing at read. Disclosed under Known issues ("no migration path"), so not a §2 |
| retyped FK → `StoreTargeting.IsEngagementEligible` / StarRocks projection | ok — no SQL-type change for StarRocks (both array mappings are `STRING`), so eligibility and projection are unaffected |

## 1. Verified-assumptions cross-check

All eight reconfirmed under a fresh read:

- **B1** — `tags.go` parses `iverson_key`, `iverson_tenant`, `iverson_search_key` as independent tags; a new one is additive.
- **B2** — `annotations.ts:250` `export function IversonArray(elementType: ClrType): PropertyDecorator`.
- **B3** — `core.py:37-38`, both `"uuid"` and `"UUID"` → `CLR_GUID`.
- **B4** — client fixtures mock the transport; a server-side guard never evaluates them. The spec's own carve-out for `clr_type`-asserting fixtures is correct and necessary.
- **B5** — `Article.cs:9` `public Guid Id`; `Article.java:17` `private UUID id`.
- **B6** — `SchemaRegistrationOrchestrator.cs:54-56` performs `owner_field` validation with `tenant_field` immediately after, both throwing `RpcException(InvalidArgument)`.
- **B7** — reconfirmed live this session: `fk_t_articles` shows `FkTAuthorId | text`; author `depth=1` throws `42883`.
- **B8** — reconfirmed live: text-keyed type accepted a write, then failed `depth=0` *and* `depth=1`.

### Span check — one uncovered dependency

**No assumption covers what SQL type a ManyToMany foreign-key column receives.** B7 establishes that the synthesized FK is `CLR_STRING` and must become `CLR_GUID`, but nothing states what `CLR_GUID` renders as when `IsArray` is true — which is precisely what the guard must accept. Verified in-round: `SchemaBuilder.cs:252` `ArrayTypeOverrides[ClrGuid] = new("UUID[]", …)`. That gap is what §2.1 falls through.

## 2. Literal-wrongness findings

### §2.1 — The registration guard, as specified, rejects every ManyToMany relation

**Description.** The Server section requires that "every non-`OneToMany` relation's foreign-key column's SQL type must be `UUID`". A ManyToMany foreign key is a list of ids, declared with `IsArray` true, and `ClrTypeToSql` renders `ClrGuid` + array as **`UUID[]`**, not `UUID`. Taken as written, the guard rejects every ManyToMany relation in every client — including the ones the spec's own client changes produce, since those retype the synthesized FK to `CLR_GUID` while leaving it an array for ManyToMany.

The spec's stated outcome is that conforming schemas register and non-conforming ones are rejected. Under this wording, a fully conforming ManyToMany schema cannot register at all, so the asked-for behavior is impossible.

This is the same trap the foreign-key-only design already documented one layer up, where it explained why `ValidateFieldReference` could not be reused: *"which a ManyToMany's `UUID[]` foreign key is not."* The new guard reintroduces the assumption that spec was written to avoid.

**Evidence.**
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:252` — `ArrayTypeOverrides[ClrType.ClrGuid] = new("UUID[]", "STRING", PayloadIndexKind.Keyword)`.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:236` — scalar `ClrGuid` → `"UUID"`; the two are distinct strings.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:55` — `ClrTypeToSql(prop.ClrType, prop.IsArray)`, so the array variant is what a ManyToMany FK column carries.
- `docs/specs/2026-08-07-relation-fk-only-write-contract-design.md`, Registration §Validation — records the identical `UUID[]` hazard for `ValidateFieldReference`.

**Proposed fix.** Make the FK half of the guard kind-aware, mirroring the shape the FK-only work already uses for `IsArray`:

> - `ManyToOne` and `OneToOne`: the foreign-key column's SQL type must be `UUID`.
> - `ManyToMany`: the foreign-key column's SQL type must be `UUID[]`.
> - `OneToMany`: exempt, as already specified.

Worth recording alongside it, because it bounds how much the FK half is actually load-bearing: **no read path compares a ManyToMany foreign-key column in SQL.** Many-to-many resolution reads the id list from the payload (`EntityRelationResolver.cs:106`) and matches the *related type's key* via `= ANY(@Keys)` with a `Guid[]` (`EntityRepository.cs:16-21`). Only the `OneToMany` reverse lookup compares an FK column directly. The `UUID[]` requirement is therefore a consistency rule, not a correctness one — which is fine, but the spec should not imply the read path depends on it.

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

One finding, confined to a single clause of the Server section, with a mechanical fix. The design's two motivating defects are both reconfirmed live, the alternative-rejected rationale holds up, and the blast-radius analysis (fixtures mocked, samples not) is correct as written.
