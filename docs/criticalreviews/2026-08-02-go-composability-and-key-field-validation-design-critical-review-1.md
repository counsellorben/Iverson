# Critical Design Review: 2026-08-02-go-composability-and-key-field-validation-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-02-go-composability-and-key-field-validation-design.md`
**Verified Assumptions section:** present (26 rows)

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem §1 (Go axis) | ok — `tags.go:88-98` nine `Kind*` on one `FieldMeta.Kind string`; `registrar.go:85-91` derives flags by comparison; the "kinds are mutually exclusive" claim is at `:68-69`. All as described |
| Problem §2 (silent-drop) | ok — `SchemaBuilder.cs:53` `Where(p => !p.IsKey)`, `:50-51` key description, and the `:47-49` comment stating the rule. Verified verbatim |
| Goals | ok — three goals, each traceable to a design section |
| Design §1 (Go grammar) | → §2.1 — one row of the six-site table mischaracterizes the site and prescribes a rewrite that misroutes |
| Design §2 (key-field check) | ok — rejected set checked for completeness in both directions, see rules below |
| Design §3 (doc corrections) | → §1 — the enumeration of old-form references is incomplete |
| Testing | ok — both-halves assertions specified for the Go composition test and for each client's check; the "description on key still registers" half is what stops a client from passing by rejecting everything |
| Migration | ok — surface re-counted: 3 sample models, `coordinator_test.go:27`, 8 tags in `registrar_test.go`, ~12 `ParseTag` calls in `tags_test.go`. Failure mode verified loud, see §1 span check |
| Verified assumptions | → §1 |
| Known issues | ok — three items, each matching a decision recorded earlier in the session |
| **Two-concerns / decomposition** | **dropped** — candidate generated. The spec does cover two independent concerns and says so. But the decomposition was surfaced to Ben before the spec was written, he chose one spec knowingly, and the spec records both the choice and its rationale under Known issues. Re-raising a settled, documented decision is re-litigation, not review. Not 🚧 |

### Rules and operands

| Row | Disposition |
|---|---|
| Key-field rejection — **over**-inclusion | ok — `description` stays legal (`SchemaBuilder.cs:50-51`) and Java's `AnnotationTest.java:22-24` already relies on it; `tenant` correctly excluded (`SchemaBuilder.cs:172` sources `TenantColumn` from `typeDesc.TenantField`, never the per-property loop); enrichment correctly excluded (`SchemaRegistrationOrchestrator.cs:142-150` already throws) |
| Key-field rejection — **under**-inclusion | ok — enumerated every user-declarable flag on `PropertyDescriptor` against the non-key loop. Dropped-on-key set is exactly `{search_key, large_field, embedding, chunk, metadata}`, all collected only inside `Where(p => !p.IsKey)`. `chunk_contextual` cannot reach the key independently — `tags.go:246-248` already rejects contextual without chunk. `is_array`/`is_nullable`/`vector_dim` are derived, not declared. Set is complete |
| `iverson_chunk` value grammar (`true` / `N` / `N:M`) | ok — mirrors the existing `chunk[:max[:overlap]]` sub-grammar at `tags.go:166-183`; no new separator introduced |
| `iverson_search_key` integer-only narrowing | ok — both directions. A bad value (`"true"`) fails `strconv.Atoi` and the assembly point returns an error, so the narrowing is loud, not silent; absent tag yields `Get` → `""` → flag unset |
| `iverson_key:"true"` boolean convention | ok — matches the six existing independent keys (`== "true"`), so `"false"` and malformed values leave the flag unset, consistent with `iverson_meta`/`iverson_tenant` today |
| Relation-vs-scalar routing at `tags.go:250` | → §2.1 |

### Data-flow arrows

| Row | Disposition |
|---|---|
| struct tag → `ParseTag` → `FieldMeta` | ok — `ParseTag` keeps its exported signature; the five new keys are read at the assembly point, so no caller's parameters change |
| struct tag → assembly point (`tags.go:228-235`) → `FieldMeta` | ok — the seven independent keys are read here via `sf.Tag.Get`; the five new keys join the same pattern. `InspectType` returns `(EntityMeta, error)`, so a bad new-key value can fail loudly |
| `FieldMeta` → `EntityMeta` routing (`tags.go:250`) | → §2.1 |
| `EntityMeta` → `registrar.go:85-91` → `PropertyDescriptor` | ok — five flag reads plus `IsNullable: fm.Kind != KindKey` at `:86`; all become boolean reads. (The spec's row says "and four siblings" where five follow; the prescribed fix is unchanged, so this is imprecision, not a defect) |
| `FieldMeta` → `coordinator.go:427` (relation skip) *(second caller of ParseTag)* | ok — checked as its own call site. It does `fm, _ := ParseTag(...)` and switches over relation kinds only. Post-change, an un-migrated scalar tag errors, `fm` is zero-valued, the switch does not match, and the field is *not* skipped — which is correct for a scalar field. No breakage from the discarded error |
| `FieldMeta` → `coordinator.go:125`, `:151` (key lookup) | ok — both are `f.Kind == KindKey` → `f.IsKey`, mechanical |
| key-field check → registration, per client *(one row per client)* | ok — .NET `SchemaRegistrar.cs:75,93`; Java `SchemaRegistrar.java:113,125-127`; TypeScript `core.ts:254-273`; Python `core.py:204`; Go `tags.go:265-268`. Each has the assembled type and an established throw/return-error idiom at that point |
| No persistence boundary in this design | ok — every arrow is in-process within a client; nothing is written and re-read |

## 1. Verified-assumptions cross-check

25 of 26 reconfirmed on a fresh read. Spot-checks: G1 (`tags.go:100-132` field list, no collision), G3 (`coordinator.go:425-432` relation-only switch), G5 (six sites, re-counted), G6/G7 (constant referents), V5–V8 (server behavior), V9 (no existing declaration breaks), D1/D2.

**One assumption is incomplete:**

- **G10 — "every doc/comment referencing the old scalar tag forms"** — the enumeration lists `tags.go:6-12`, `:29-30`, `:32-39`, `:68-69`, `:83-84`, `:134`. It **misses `tags.go:247`**, a runtime `fmt.Errorf` string that reads `…is not a chunk field (iverson:\"chunk...\")…`. The miss is mechanical: the source escapes the quote, so a `grep 'iverson:"'` cannot match `iverson:\"`. A grep for `'iverson:\\"'` returns this line and only this line. After §1 lands, that error tells users to fix their tag using a syntax that no longer exists.

  Not escalated to §2: the contextual validation still functions and the design's stated outcome (Go declarations compose) holds. It is a documentation-completeness gap in an assumption that claims completeness — add `tags.go:247` to G10's enumeration and to §3's rewrite list.

**Span check — two uncovered dependencies, both verified in-round, neither escalating:**

1. **The hard migration's failure mode.** The spec states "no compatibility shim" but never says what happens to an un-migrated `iverson:"key"` tag — load-bearing, because a silent degradation would reproduce the exact failure class §2 of the spec exists to eliminate. Verified: `ParseTag`'s `default:` case at `tags.go:194-195` returns `fmt.Errorf("iverson tag %q: unknown kind %q", …)`, and `InspectType` propagates it. Migration failures are loud at registration. Worth stating in the spec's Migration section rather than leaving a reader to infer it.
2. **The assembly point can fail.** The new key parsing and the new key-field check both need somewhere to raise. Verified: `InspectType` is declared `(EntityMeta, error)` and already returns errors for blank extract hints (`:242`) and the tenant-count rules (`:265-268`).

## 2. Literal-wrongness findings

### §2.1 — The six-site table mischaracterizes `tags.go:250`, and its prescribed fix would misroute every plain field

**Description.** Design §1's table describes `tags.go:250` as `switch fm.Kind` **(validation)** and prescribes "rewritten against the booleans."

Both halves are wrong, and the second is actively harmful. The site is not validation — it is the relation-vs-scalar **routing** step that decides whether a field lands in `meta.Relations` or `meta.Fields`, and it also collects the tenant fields:

```go
switch fm.Kind {
case KindManyToOne, KindManyToMany, KindOneToMany, KindOneToOne:
    meta.Relations = append(meta.Relations, fm)
default:
    if fm.Tenant { tenantFields = append(tenantFields, sf.Name) }
    meta.Fields = append(meta.Fields, fm)
}
```

The correct rewrite keys off `RelationKind`: `if fm.RelationKind != "" { … } else { … }`. An implementer who follows the spec literally and rewrites this "against the booleans" would produce something like `if fm.IsKey || fm.IsSearchKey || fm.IsLargeField || fm.IsEmbedding || fm.IsChunk { meta.Fields } else { meta.Relations }` — which sends **every plain, untagged field into `meta.Relations`**. Those fields then never reach `registrar.go`'s property loop, so their columns vanish from the registered schema, and `InspectType`'s tenant collection never runs for them either, so an entity whose tenant field is otherwise plain would additionally fail the tenant check.

The spec is also internally inconsistent here: Design §1 states elsewhere that relations route on `RelationKind` and that `registrar.go:106`, `:227`, `:244` "follow the `RelationKind` rename mechanically." `tags.go:250` is the same kind of site and belongs in that group, not in the boolean-rewrite group.

**Evidence.**
- `Iverson.Clients/Go/iverson/tags.go:250-262` — the switch, its relation cases, and the `default:` branch carrying both the tenant collection and the `meta.Fields` append.
- `tags.go:255-257` — the comment on the relation case explains why relations must not reach `meta.Fields` ("a tenant marker on a relation is not a tenant declaration at all"), confirming the site's purpose is routing.
- Design §1's own table row for `registrar.go:106/227/244`, which correctly groups relation-kind switches under the rename.

**Proposed fix.** Move `tags.go:250` out of the six-site boolean table and into the `RelationKind`-rename group, describing it accurately: *"relation-vs-scalar routing plus tenant collection; `switch fm.Kind` becomes a `RelationKind != ""` test, with the `default:` branch unchanged."* The six-site table then correctly holds five boolean-rewrite sites (`registrar.go:85-91`, `coordinator.go:125`, `:151`, `tags.go:246`, `sample/main.go:24`) and the rename group holds four (`registrar.go:106`, `:227`, `:244`, `tags.go:250`).

## 3. Forced decisions

No forced decisions found.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

§3 is empty. §2 carries one finding whose fix is a corrected table row, not a design change — the design's shape (independent tag keys, `RelationKind` for relations, a key-field check sited at each client's registrar step) is sound and every other enumerated row disposed clean. Address §2.1 and G10's missing `tags.go:247`, then the spec is ready for implementation planning.
