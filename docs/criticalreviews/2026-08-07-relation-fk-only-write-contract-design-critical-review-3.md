# Critical Design Review: 2026-08-07-relation-fk-only-write-contract-design (Round 3)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-07-relation-fk-only-write-contract-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem → client table | ok — the three "**Never**" cells re-verified against `tags.go:315-325` + `registrar.go:64`, `core.py:187-188`, `core.ts:238`, and `SchemaBuilder.cs:53-57` (columns built from non-key properties) |
| Problem → The Go defect | ok — `coordinator.go:440-447` skip confirmed |
| Problem → The undeclared foreign-key column (new) | ok — every cited line re-read; the claim that no FK column exists for these three clients holds |
| Problem → declaration-model split | ok — unchanged |
| Contract | ok — four-kind table still agrees with all five registrars' inference functions |
| Server (deletions, three checks, `NullValue`) | ok — symbols re-traced; `NullValue` rule matches `RelationValidator.cs:78-81` |
| Server → Ordering constraint | ok — `:294` before `:299`, carve-out intact |
| **Registration → Declaration (new)** | → §2.1, §2.2 |
| **Registration → Validation (new)** | ok — `ScalarColumns` membership is the right target: `SchemaBuilder.cs:53-57` puts every non-key property there, and an FK is never the key. `ValidateFieldReference` correctly rejected as a reuse target (`:109-115` string-valued `SqlType` gate) |
| Clients → kind-first rule | ok — unchanged this round |
| Clients → per-client (Go, Java, .NET, Python, TS) | ok — Registration cross-references added to Go/Python/TS read consistently with the new section |
| Consequences | ok — the three-client behavior change, the added column, and the broadened StarRocks eligibility all follow from the Registration section as written |
| Testing | ok — registration tests named per client and per kind, including the array-FK case that would trip `ValidateFieldReference` |
| Verified assumptions | ok — cross-checked in §1; A28–A31 renumbered into order |
| Known issues | ok — the A22 bullet is correctly gone; the remaining three make no new claim |

### Rules and operands

| Rule | Over-inclusion | Under-inclusion | Disposition |
|---|---|---|---|
| **Declaration: which relation kinds declare an FK property** | OneToMany declaring `{ThisType}Id` → a column belonging to the related row | m2o/o2o/m2m not declaring → the A29 defect | ok on the *kind* split — OneToMany is exempt in both declaration and validation. The *mechanism* for making the exclusion conditional is where it breaks → §2.1, §2.2 |
| **Declaration: property named by inferred FK, not field name** | — | Go m2m field `Articles` declared as `Articles` rather than `ArticleIds` → validation then fails | ok as specified — the spec says "under the inferred FK column name"; only m2m differs from the field name, and Go/Python/TS m2o already coincide |
| **Validation: FK matches a declared column** | rejects a legitimate array FK | accepts a phantom FK | ok — membership-only test over `ScalarColumns`; array FKs pass because the string-valued gate is explicitly not reused |
| **Validation: OneToMany exemption** | exempting a kind that should be checked | — | ok — `OneToMany`'s FK is a column on the related type; checking it here would reject every valid OneToMany. Consistent with the declaration rule |
| **Server rejection: nav key present and distinct from FK** | `NullValue` | `PropertyName == ForeignKey` collision | ok — settled rounds 1–2; evidence unchanged |

### Data-flow arrows

| Arrow → consuming operation | Disposition |
|---|---|
| Go relation field → `meta.Fields` / `meta.Relations` split → property loop | → §2.1 — the split has a second consumer the spec's change does not account for |
| TypeScript relation field → property loop → `PropertyDescriptor` construction | → §2.2 — the loop contains a hard failure for array properties |
| Python relation field → property loop → `_python_type_to_clr` | ok — `list[str]` yields `(CLR_STRING, True)`; the bare-`list` annotation in the sample is OneToMany and stays excluded |
| declared property → `SchemaBuilder` → `ScalarColumns` → `ApplySchemaAsync` (**persistence boundary**) | ok — `:53-57` non-key properties become columns; the new FK column flows through the existing drift path the spec names |
| relation descriptor → `SchemaRegistrationOrchestrator` validation → `ScalarColumns` lookup | ok — both operands exist on `SchemaDescriptor`; `:53-66` is the established location for such checks |
| payload FK key → validator → `SerializePayload` → stores (**persistence boundary**) | ok — with the column now declared, the key has a destination; this is the arrow A29 showed was broken |

## 1. Verified-assumptions cross-check

All 31 assumptions reconfirmed. Fresh reads on the four added since round 1:

- **A28** — `core.py:330-341` still has no list branch; the other four clients' list paths re-confirmed.
- **A29** — all three exclusion sites re-read and still exclude unconditionally; `SchemaBuilder.cs:53-57` confirms columns come from properties.
- **A30** — `SchemaRegistrationOrchestrator.cs:109-115` still gates on a string-valued `SqlType`, so it would reject a `UUID[]` FK. Correctly ruled out as a reuse target.
- **A31** — `:50-66` performs `ValidateEnrichmentTargets`, the `owner_field` check and the mandatory `tenant_field` check, all throwing `RpcException(InvalidArgument)`. The pattern is as described.

### Span check — one uncovered dependency

**No assumption covers whether the FK-bearing field can safely enter each client's property loop.** A29 establishes that the three clients *exclude* it; nothing establishes what happens when the exclusion is lifted. Each property loop has its own preconditions and side effects, and two of the three have consequences the Registration section does not account for — that gap is what §2.1 and §2.2 fall through. Verified in-round; no forced decision needed.

## 2. Literal-wrongness findings

Both findings are in the Registration section's Declaration rule, and both share one corrected mechanism (given after §2.2).

### §2.1 — Making Go's exclusion kind-conditional also disables the tenant-declaration guard

**Description.** The spec says *"the existing exclusions (`tags.go:315-325`, `core.py:187-188`, `core.ts:238`) become kind-conditional rather than unconditional."* In Go, that exclusion is not only a property filter — it is the mechanism that enforces a documented tenant rule. The code says so explicitly:

```go
if fm.RelationKind != "" {
    // Relations never reach meta.Fields, which is where the tenant field
    // is looked up on registration — so a tenant marker on a relation is
    // not a tenant declaration at all and must not satisfy the check.
    meta.Relations = append(meta.Relations, fm)
} else {
    if fm.Tenant {
        tenantFields = append(tenantFields, sf.Name)
    }
    meta.Fields = append(meta.Fields, fm)
}
```

`tenantFields` is collected **inside the else branch**. Routing ManyToOne/OneToOne/ManyToMany fields into that branch so they reach `meta.Fields` also routes their `iverson_tenant:"true"` markers into `tenantFields` — which the comment states must not happen. Since exactly one tenant field is required (`tags.go:328-333`), a relation field carrying a tenant marker would now satisfy the mandatory tenant boundary check, and a type declaring both a real tenant field and a tenant-marked relation would newly fail with "multiple fields marked".

This is not a hypothetical about future code: it is a guard the codebase added deliberately and documented, which following the spec's instruction literally removes.

**Evidence.** `Iverson.Clients/Go/iverson/tags.go:315-325` (the split and the comment), `:321-323` (`tenantFields` inside the else), `:328-333` (exactly-one enforcement).

### §2.2 — TypeScript cannot declare a ManyToMany FK property; the property loop throws on any array without `@IversonArray`

**Description.** The same instruction applied to `core.ts:238` routes the relation-marked field into a loop whose next check is a hard failure for array properties:

```ts
const looksArray = designType === Array || Array.isArray(instance[fieldName]);
if (looksArray && arrayElement === undefined) {
    throw new Error(
        `${typeName}.${fieldName} is an array property but has no @IversonArray(elementType) `
        + 'decorator; TypeScript erases the element type, so it cannot be inferred. …',
    );
}
```

A ManyToMany foreign key is necessarily an array of ids, so `looksArray` is true. Unless the user also adds `@IversonArray(ClrType.CLR_STRING)` to the relation field, **schema registration throws** — the entity cannot be registered at all. The relation decorator alone carries no element type, and the spec's Registration section neither requires the extra decorator nor supplies the element type another way.

The failure is loud rather than silent, but it makes the design's stated outcome — TypeScript declares its FK columns — impossible for the ManyToMany kind as written.

**Evidence.** `Iverson.Clients/TypeScript/src/core.ts:242-250` (the array guard), `annotations.ts:250` (`IversonArray` is a separate decorator), `annotations.test.ts:72` (`@ManyToMany` is applied without it).

### Proposed fix (covers both)

Declare the FK property by **synthesis from the relation metadata**, not by relaxing the field-loop exclusion. In each of the three clients, leave the existing exclusion unconditional and append a `PropertyDescriptor` built from the relation descriptor after the field loop:

- name — the inferred FK column name (which the spec already requires, and which the field loop would have got wrong for Go's m2m anyway, since it names properties after the field);
- element/CLR type — known from the kind rather than reflected: ids are strings, `isArray` true for ManyToMany and false for ManyToOne/OneToOne;
- nothing emitted for OneToMany.

This resolves §2.1 by leaving `tags.go`'s `meta.Fields`/`meta.Relations` split — and therefore the tenant guard — untouched; and §2.2 by never routing an array through the `@IversonArray` reflection check, since the element type is supplied directly. It also removes the Registration section's dependence on three different property loops behaving compatibly, replacing it with one construction rule stated once.

Suggested replacement for the Declaration paragraph's second sentence: *"Each client appends the FK property after its existing field loop, built from the relation descriptor — named by the inferred FK column name, typed as a string id (an array of them for ManyToMany), and omitted entirely for OneToMany. The existing exclusions stay unconditional: Go's also enforces that a tenant marker on a relation is not a tenant declaration (`tags.go:316-318`), and TypeScript's field loop rejects any array property lacking `@IversonArray` (`core.ts:242-250`), which a reflected ManyToMany FK would trip."*

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1 (type-based classification undecidable in Python/TypeScript)** — resolved; the Clients rule is kind-first with the type test scoped to .NET and Java.
- **Round 1 §3.1 (`NullValue` nav key)** — resolved; ruled absent-and-tolerated, then reversed to that position after an initial strict ruling, with the rationale recorded inline.
- **Round 1 §1 span check (server-side test impact)** — resolved; Testing names the two `ObjectMappingGrpcServiceTests` cases and A27 records the failed assumption.
- **Round 2 §2.1 (Python cannot serialize a ManyToMany id list)** — resolved; the Python paragraph specifies a list branch and the stale Known-issues bullet was rescoped.
- **Round 2 §1 span check (FK value-type serialization)** — resolved; A28 covers all five clients.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

Both findings are in the Declaration paragraph of the new Registration section and share a single corrected mechanism, so the edit is one paragraph plus a Testing line. Everything else in the section — the kind split, the validation target, the decision not to reuse `ValidateFieldReference` — checks out. §3 is empty.
