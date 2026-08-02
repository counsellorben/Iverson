# Critical Design Review: 2026-08-02-go-composability-and-key-field-validation-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-02-go-composability-and-key-field-validation-design.md`
**Verified Assumptions section:** present (28 rows)

## 0. Coverage enumeration

Enumeration re-derived from the amended spec before consulting round 1.

### Sections

| Row | Disposition |
|---|---|
| Problem §1 (Go axis) | ok — `tags.go:88-98` constants and `registrar.go:85-91` comparisons re-read; unchanged and as described |
| Problem §2 (silent-drop) | ok — `SchemaBuilder.cs:53`, `:50-51`, and the `:47-49` comment re-read verbatim |
| Goals | ok — three goals, each traceable to a design section |
| Design §1 (Go grammar) | ok — the amended five-site table and four-site rename group re-checked against the code; `tags.go:250` is now correctly described as relation-vs-scalar routing with the `default:` branch unchanged |
| Design §2 (key-field check) | ok — rejected set and per-client feasibility both re-checked, see rules and arrows below |
| Design §3 (doc corrections) | ok — all seven cited references re-read, including the `:247` error string added in round 1 |
| Testing | ok — both-halves assertions specified for the Go composition test and for each client's check |
| Migration | ok — surface re-counted; the loud-failure paragraph added in round 1 verified against `tags.go:194-195` and `:228-231` |
| Verified assumptions | → §1 — one citation does not resolve |
| Known issues | ok — three items, each matching a decision recorded during design |

### Rules and operands

| Row | Disposition |
|---|---|
| Key-field rejection — over-inclusion | ok — `description` stays legal; `tenant` excluded (`SchemaBuilder.cs:172` sources `TenantColumn` from `typeDesc.TenantField`); enrichment excluded (`SchemaRegistrationOrchestrator.cs:142-150` already throws) |
| Key-field rejection — under-inclusion | ok — re-enumerated every user-declarable `PropertyDescriptor` flag against the non-key loop. `{search_key, large_field, embedding, chunk, metadata}` is complete; `chunk_contextual` cannot reach the key independently (`tags.go:246-248`); `is_array`/`is_nullable`/`vector_dim` are derived |
| `iverson_chunk` value grammar | ok — mirrors `tags.go:166-183`; no new separator |
| `iverson_search_key` integer-only | ok — both directions: a bad value fails `strconv.Atoi` and `InspectType` returns the error; an absent tag yields `""` and leaves the flag unset |
| `iverson_key:"true"` boolean convention | ok — matches the six existing independent keys' `== "true"` test |
| Relation-vs-scalar routing (`tags.go:250`) | ok — the amended text prescribes `fm.RelationKind != ""` with `default:` unchanged, which matches `tags.go:250-262` |

### Data-flow arrows

| Row | Disposition |
|---|---|
| struct tag → `ParseTag` → `FieldMeta` | ok — exported signature unchanged; the five new keys are read at the assembly point |
| struct tag → assembly point → `FieldMeta` | ok — `tags.go:228-235` reads the independent keys via `sf.Tag.Get`; `:228-231` propagates `ParseTag`'s error |
| `FieldMeta` → `EntityMeta` routing | ok — `tags.go:250-262`, relation cases plus `default:` carrying tenant collection and the `meta.Fields` append |
| `EntityMeta` → `registrar.go:85-91` → `PropertyDescriptor` | ok — five flag reads plus `IsNullable` at `:86`, all boolean reads after the change |
| `FieldMeta` → `coordinator.go:427` *(second ParseTag caller)* | ok — checked as its own call site; discards the error, switches on relation kinds only, and a post-change scalar tag leaves the field unskipped, which is correct |
| **key-field check → .NET registration** *(per-call-site row)* | ok — **the parameters exist where the check would run.** `SchemaRegistrar.cs:63` builds the key via a *separate* `BuildKeyDescriptor(descriptor.KeyProperty)` and `:68` skips the key in the main loop — but `BuildKeyDescriptor` (`:115-128`) calls the same `AddAnnotations(descriptor, prop)` helper as `TryBuildPropertyDescriptor` (`:130-144`), so the key's flags are populated and available |
| **key-field check → Java registration** *(per-call-site row)* | ok — same shape: `SchemaRegistrar.java:85-95` identifies `keyField` in a first pass, `:103` builds it via `buildKeyDescriptor(keyField)`, `:107` skips it in the main loop. `keyField` is a `Field` with all annotations reachable at the check point |
| key-field check → TypeScript registration | ok — `core.ts` exposes `getKeyField`, `getMetadataFields`, `getSearchKeys`, `getLargeFields`, `getEmbeddingFields`, `getChunkFields` as module functions taking the class, all callable at the `:254-273` tenant-resolution point |
| key-field check → Python registration | ok — `core.py:133-143` binds `key_field` alongside `search_keys_by_field`, `large_fields_set`, `embedding_fields_set`, `chunk_fields_by_name` and `metadata_fields_set` before the property loop |
| key-field check → Go registration | ok — `InspectType`'s loop holds `fm.IsKey` and every independent flag on the same `FieldMeta` |
| No persistence boundary | ok — every arrow is in-process within one client; nothing is written and re-read |
| **`.NET`/`Java` separate key-descriptor builders drop the flags before sending** | **dropped** — candidate generated and tested against both. Both key builders invoke the same annotation helper as their non-key sibling (`SchemaRegistrar.cs:126`; `SchemaRegistrar.java:103` → `buildKeyDescriptor`), so the flags reach the wire and the discard is server-side exactly as the spec states. Had either skipped annotations, the spec's premise would have been wrong for that client |
| **Test fixtures may trip the tenant check before the key check** | **dropped** — every client raises on a missing tenant field, and Go's tenant validation runs at the end of `InspectType` (`:264-270`) while Python's runs early (`core.py:144`), so a fixture lacking a tenant could raise the wrong error and pass a bare "raises" assertion vacuously. This is test-construction, which `critical-implementation-review` owns after a plan exists; the spec's Testing section already mandates both-halves assertions. Fails literal-wrongness for the design |
| **Python-spec citation `:231-233` spans one line more than the claim** | **dropped** — the parity claim occupies `:231-232`; the citation's first line is exact and lands on the right text. No reader is misdirected |

## 1. Verified-assumptions cross-check

27 of 28 reconfirmed on a fresh read. Spot-checks: G1 (field list), G3 (`coordinator.go:425-432`), G5 as amended (five boolean sites, four rename sites — re-counted), G6/G7, G10 including the `:247` addition, G11 (`tags.go:194-195` `default:` case and `:228-231` propagation — exact), V5–V9, D1, D2.

**One citation does not resolve:**

- **G12 — "The Go assembly point can fail"** — the claim is true: `InspectType` is declared `func InspectType(v interface{}) (EntityMeta, error)` and returns errors for a blank extract hint and the tenant-count rules. But the row cites **`tags.go:249`** for the declaration, and the declaration is at **`tags.go:213`**. Line 249 is blank. The two supporting citations in the same row (`:242` blank-hint, `:265-268` tenant rules) are correct.

  Not escalated to §2: the underlying fact holds and nothing in the design depends on the line number. But the Verified-assumptions section exists so a reader can check the evidence, and this pointer sends them to an empty line. Correct `:249` → `:213`.

**Span check: no uncovered dependency.** The design's dependencies — Go's tag-parsing and routing sites (G5, G6, G7), the assembly point's existence and its ability to raise (G4, G12), migration loudness (G11), the doc-reference set (G10), per-client check feasibility and data availability (V1–V3, plus the five arrow rows above), the server's drop/reject/ignore behavior (V5–V8), and non-breakage of existing declarations (V9) — are each covered by a listed item as scoped. Round 1's two span gaps are closed by G11 and G12.

## 2. Literal-wrongness findings

No literal-wrongness findings.

Three candidates were generated and dropped; each is recorded in §0 with its evidence and the reason it failed the test. The most substantive was whether .NET's and Java's separate key-descriptor builders strip the flags before sending — if either had, the spec's premise ("the server accepts and discards") would have been false for that client and the check would need to inspect attributes rather than the built descriptor. Both call the same annotation helper as their non-key path, so the premise holds.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — resolved. `tags.go:250` is out of the boolean-rewrite table (now titled "Five call sites") and into the rename group, described accurately as relation-vs-scalar routing plus tenant collection, with the misroute hazard stated: rewriting it against the scalar booleans would send every plain field into `meta.Relations`.
- **Round 1 §1 (G10 incomplete)** — resolved. `tags.go:247` added to G10's enumeration and to §3's rewrite list, with the escaping explanation recorded so the omission is not repeated.
- **Round 1 span gap (migration failure mode)** — resolved. `G11` added, and the Migration section now states that un-migrated tags fail loudly at registration rather than leaving it to inference.
- **Round 1 span gap (assembly point can raise)** — resolved by `G12`, subject to the citation correction in §1.
- **Round 1 `G5` consistency** — resolved. Updated from "Six sites" to "Five," keeping the assumption table in step with the amended body.

## 5. Recommendation

✅ **Approve as-is**

§2 and §3 are both empty, and every §0 row is disposed. One §1 citation needs correcting — `G12`'s `tags.go:249` → `tags.go:213` — but the fact it attests is true, nothing in the design rests on the line number, and implementation is not blocked by it. Fix it on the next edit to the spec; the spec is ready for implementation planning either way.
