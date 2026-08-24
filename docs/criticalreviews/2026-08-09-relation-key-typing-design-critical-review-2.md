# Critical Design Review: 2026-08-09-relation-key-typing-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson-fk-only/docs/specs/2026-08-09-relation-key-typing-design.md`
**Verified Assumptions section:** present

Round 1's finding (§2.1, the guard rejecting every ManyToMany) was applied to the spec at
`a199df2`. This round re-derived the coverage enumeration against the current spec text before
consulting that history, so the fixed clause is one row among many rather than the search area.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Header / Related | ok — both referenced specs exist at the cited paths, unchanged since round 1 |
| Problem — text key columns | ok — `EntityRepository.cs:9,16,26,39,64` still carry the five casts |
| Problem — text FK columns | ok — `EntityRelationResolver.cs:154` is still the only `FetchByColumnAsync` call site |
| Contract | ok — `RelationValidator.cs:88,110` unchanged |
| Contract — alternative rejected | ok — rationale unchanged |
| Server — the three-bullet guard (revised) | ok — see the three rule rows below; the revision is satisfiable by all five clients |
| Server — "Only the `OneToMany` reverse lookup compares a foreign-key column in SQL" | ok — **negative claim, grep'd**: `EntityRelationResolver.cs:39-47` routes `OneToOne` into the same branch as `ManyToOne` (`FetchByKeyAsync`, `:81`), `ManyToMany` to `FetchManyByKeysAsync` (`:114`), and only `OneToMany` to `FetchByColumnAsync` (`:154`). No fourth branch exists |
| Server — guard placement "beside `:53-66`" | ok — `SchemaRegistrationOrchestrator.cs:50` builds the `SchemaDescriptor` *before* the `owner_field`/`tenant_field` checks at `:54-66`, so a guard placed there has SQL types available. The loop at `:33` covers `RootType` **and** `Dependents`, so dependent types are guarded too |
| Server — `OneToMany` exemption | ok — `:71` already excludes `OneToMany` from the existing membership check; the new guard inherits the same predicate |
| Clients — Go tag | ok — `go.mod` unchanged (grpc + protobuf only); tag vocabulary still per-tag |
| Clients — TypeScript decorator | ok — `annotations.ts:250` precedent intact |
| Clients — Python | ok — `core.py:90` resolves the annotation via `get_type_hints` → `__name__`, so `uuid.UUID` yields `"UUID"` → `CLR_GUID` (`:37`). The mechanism the spec relies on is name-based and the sample's `uuid.UUID` form hits it |
| Clients — synthesized FK retype | → §1 span check (array flag); mechanism otherwise ok |
| Clients — ".NET and Java need no change" | → §1 span check (m2m arm); verified true |
| Samples | ok — Go `Id string`, TS `id: string`, Python `sample/models.py:19,26,35` `id: str` are the three that change |
| Testing | ok — the four server cases map 1:1 to the revised guard's three bullets plus the `OneToMany` exemption |
| Consequences | ok — breaking-change framing covers the LoadTest entities too (all Guid-keyed; `BenchmarkArticle.cs:8,11` `Guid Id` / `Guid BenchmarkAuthorId`), so nothing in-repo breaks silently |
| Verified assumptions B1–B9 | see §1 |
| Known issues | ok — unchanged and still accurate |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Key column SQL type must be `UUID`** | rejects a legitimate key | **admits a broken key / is vacuous** | ok — `SchemaBuilder.cs:163` sets `KeyColumn = new ColumnDescriptor(keyProp.Name, ClrTypeToSql(keyProp.ClrType, false), false)`. The SQL type is derived from the declared `ClrType`, **not** hardcoded — so the guard is falsifiable and a `CLR_STRING` key really does present as `TEXT`. (The vacuity direction is the one that would have silently left B8 unfixed; checked explicitly.) |
| **`ManyToOne`/`OneToOne` FK column must be `UUID`** | rejects a conforming FK | admits a `TEXT` FK | ok — Go/Python/TS synthesize the FK with `isArray = (kind == many_to_many)`, i.e. **false** for m2o/o2o, so `CLR_GUID` renders scalar `UUID` (`SchemaBuilder.cs:236`). .NET `Article.cs:22` `Guid AuthorId`, Java `Article.java` `UUID authorId` → `UUID` |
| **`ManyToMany` FK column must be `UUID[]`** | rejects a conforming m2m | admits a `TEXT[]` m2m | ok — both directions checked against all five clients; see §1 span check. This is the clause round 1 corrected, and it is satisfiable everywhere |
| Go tag → `CLR_GUID` | tags a non-key property | misses the key | ok — independent of `iverson_key`; `registrar.go:87-88` already threads `(clrType, isArray)` per property, so an override slot exists |
| TS decorator → `CLR_GUID` | — | array properties | ok — a GUID key is scalar |
| Synthesized FK `CLR_STRING` → `CLR_GUID` | retypes StarRocks/Qdrant indexes | — | ok — reconfirmed: `ArrayTypeOverrides` maps both `ClrGuid` and `ClrString` arrays to StarRocks `STRING`/`Keyword` (`SchemaBuilder.cs:252-253`); scalars likewise (`:236-237`) |

### Data-flow arrows

| Arrow | Disposition |
|---|---|
| `RegisterAsync` → `SchemaBuilder.BuildDescriptor` → guard reads `SqlType` | ok — `:50` precedes `:54`; `ColumnDescriptor` carries `SqlType`; the FK column is in `ScalarColumns`, which the shipped `:71-80` membership check already enumerates (including the m2m `UUID[]` case — `:69` says so explicitly) |
| client property loop → `PropertyDescriptor.is_array` → `ClrTypeToSql(t, isArray)` | **crosses the wire (serialization boundary)** — ok — checked at the emitting side in all three clients, not inferred from the server type: `core.py:257`, `core.ts:319`, `registrar.go:129` each set the array flag on the synthesized FK from the relation kind |
| Java `List<UUID>` field → `DetectedType` → `PropertyDescriptor` | ok — `SchemaRegistrar.java:300` unwraps `ParameterizedType`, `:319` maps `java.util.UUID` → `CLR_GUID`, `:199` sets `isArray` |
| Python sample `id: uuid.UUID` → `_entity_to_struct` → `Struct` | ok — `core.py:404-405` has an explicit `isinstance(value, uuid.UUID)` branch emitting `str(value)`, so the mandated sample change serializes |
| Python `Struct` → `_from_struct` → entity field | dropped — `:616` writes a `str` into a `uuid.UUID`-annotated field, so the round trip is asymmetric. Nothing breaks at runtime (Python annotations are unenforced, and re-serializing the `str` hits the `isinstance(value, str)` branch), and no sample call site passes an id back into `get`/`delete`. Fails literal-wrongness |
| retyped FK → StarRocks projection / Qdrant index | ok — no SQL-type change on either target |
| persisted `_iverson_schema` → `SchemaRegistry.LoadAsync` | ok — guard is registration-time only; disclosed under Known issues |

## 1. Verified-assumptions cross-check

All nine reconfirmed under a fresh read; B7 and B8 remain the two documented failures this spec fixes.

- **B1** — Go's tag vocabulary is still per-tag; `registrar.go:87-88` threads `(clrType, isArray)` per property, giving the override a place to land.
- **B2** — `annotations.ts:250` `IversonArray(elementType)` unchanged.
- **B3** — `core.py:37-38` maps `"uuid"`/`"UUID"`; `:90` derives the key via `__name__`, so `uuid.UUID` resolves.
- **B4** — unchanged; fixtures mock the transport.
- **B5** — `Article.cs:9` `Guid Id`; `Article.java:17` `UUID id`.
- **B6** — `SchemaRegistrationOrchestrator.cs:54-66`, and `:50` builds the descriptor first.
- **B7** — ❌ as recorded; `EntityRelationResolver.cs:154` is still the sole column-comparing read.
- **B8** — ❌ as recorded; `SchemaBuilder.cs:163` confirms the key's SQL type follows the declared `ClrType`, which is why a `CLR_STRING` key becomes `TEXT`.
- **B9** — `SchemaBuilder.cs:236/252` still distinguish `UUID` from `UUID[]`.

### Span check — two uncovered dependencies, both verified in-round

**1. No assumption covers that the three clients set `is_array` on the *synthesized* many-to-many
foreign key.** B9 establishes that the *server* renders `CLR_GUID` + array as `UUID[]`; the revised
guard's ManyToMany bullet is only satisfiable if the clients actually emit the array flag alongside
the retyped `CLR_GUID`. If any of them emitted a scalar, retyping to `CLR_GUID` would produce `UUID`
and the m2m bullet would reject it — the failure mode round 1's fix was written to prevent, one
layer down. Verified: `core.py:257` `is_array=(rel["kind"] == "many_to_many")`,
`core.ts:319` `isArray: rel.kind === 'many_to_many'`, `registrar.go:129`
`IsArray: fm.RelationKind == KindManyToMany`. All three hold.

**2. No assumption covers .NET's and Java's *many-to-many* foreign-key field types.** B5 verifies
only that their **keys** are `Guid`/`UUID`. The spec's ".NET and Java need no change" is a
load-bearing negative claim that now has to survive the m2m arm of the guard as well as the key and
m2o arms. Verified: .NET `Article.cs:23` `Guid[] TagIds`, `Tag.cs:16` `Guid[] ArticleIds` → `UUID[]`;
Java `Article.java` `List<UUID> tagIds`, resolved through `SchemaRegistrar.java:300`
(`ParameterizedType` unwrap) and `:319` (`java.util.UUID` → `CLR_GUID`) with `:199` setting
`isArray`. The claim holds.

## 2. Literal-wrongness findings

No literal-wrongness findings.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — the guard's blanket "every non-`OneToMany` relation's foreign-key column must
  be `UUID`" rejected every ManyToMany. Resolved: the Server section now carries three kind-aware
  bullets, with `ManyToMany` requiring `UUID[]`, and the accompanying paragraph records that no read
  path compares a many-to-many foreign-key column in SQL.
- **Round 1 span check** — "what SQL type a ManyToMany foreign-key column receives" is now covered
  by B9.

## 5. Recommendation

✅ **Approve as-is**

The round-1 fix holds and its neighbourhood checks out: all five clients can satisfy the revised
three-bullet guard, and the two spans the assumption table left open (client-side array flag on the
synthesized m2m FK; .NET/Java m2m field types) were verified in-round rather than assumed. The two
directions most likely to have hidden a silent miss — a vacuous key guard (`KeyColumn`'s SQL type
turning out to be hardcoded) and the "only `OneToMany` compares an FK column" negative claim — were
each checked at the source and hold. Ready for implementation planning.
