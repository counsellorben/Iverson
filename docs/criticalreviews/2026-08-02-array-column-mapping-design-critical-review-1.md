# Critical Design Review: 2026-08-02-array-column-mapping-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-02-array-column-mapping-design.md`
**Verified Assumptions section:** present (22 rows)

A live Postgres 16 container was available, so the four assumptions the spec marks **reasoned**
(B2, B7, B11-B13) were settled empirically rather than left unverified. One of them failed.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem | → §0 note below — the causal claim is correct for `string[]` and **wrong for the other six** |
| Goal | ok — three clauses, each traceable to a design section |
| Design §1 (mappings) | ok — all 18 SQL type strings resolve against live Postgres; `SqlTypeMap`'s duplicate-key hazard re-checked, see B4 |
| Design §2 (drift detection) | → §2.1 — the normalization count is wrong once the spec's own array types exist |
| Design §3 (read/write path) | ok — round-trip executed against live Postgres, both directions, plus a negative control reproducing the current bug |
| Design §4 (client detection) | → §2.2 (Python) and §3.1 (the forced decision its fix creates). Java, Go, TypeScript rows checked clean |
| Design §5 (testing) | → §2.1 — the `TIMESTAMPTZ` assertion is specified in the singular |
| Out of scope | ok — five exclusions, each matching a decision recorded during design |
| Known issues | ok — `ClrTypeToEngagementType`'s `"STRING"` default (`SchemaBuilder.cs:278-279`) re-read and as described |
| Verified assumptions | → §1 |

**Problem-section note (dropped as a finding, recorded for accuracy).** The spec states the
consequence is "a broken read rather than a degraded one." Executed against live Postgres:
`json_populate_record` into a scalar `TEXT` column **succeeds**, storing `["a","b"]` as text, and
`row_to_json` returns `{"tags":"[\"a\",\"b\"]"}` — a JSON string, exactly as the spec says. But into
a scalar `INTEGER` column it **fails outright**: `ERROR: invalid input syntax for type integer:
"[1,2]"`. So `string[]` is a broken read; the six numeric/bool/datetime arrays are failed *writes*
and never land a row at all. Fails the literal-wrongness test — the design's outcome is unaffected
and the fix is identical — but the distinction matters for how urgent this is and for what data
exists to migrate, so it is recorded rather than silently dropped.

### Rules and operands

| Row | Disposition |
|---|---|
| `ClrTypeToSql` array branch — over/under-inclusion | ok — `ArrayTypeOverrides.TryGetValue` then fallthrough (`SchemaBuilder.cs:269-275`); post-change the table is total over `ClrType`, so the fallthrough becomes unreachable for arrays. No type gains an array mapping it shouldn't have |
| `SqlTypeMap` duplicate-key hazard | ok — `:262-265` is `ScalarTypeMap.Values.Concat(ArrayTypeOverrides.Values).ToDictionary(m => m.SqlType, …, OrdinalIgnoreCase)`, which **throws** on duplicates. The 18 post-change strings are pairwise distinct under case-insensitive comparison. Verified by enumeration, not assertion |
| Drift comparison — false positives | → §2.1 — a **correct** `TIMESTAMPTZ[]` column would be rejected |
| Drift comparison — false negatives | ok — `format_type` is exact for the other 16; a genuinely differing type cannot compare equal |
| Bytes carve-out (all five clients) | ok — .NET `SchemaRegistrar.cs:241`; the spec mandates the equivalent for Java `byte[]`, Python `bytes`, Go `[]byte` before the array unwrap, which is the correct ordering |
| Payload-kind reachability for arrays | ok — candidate generated (arrays are rejected as *metadata* at `SchemaBuilder.cs:94`, so are their payload kinds dead?). **Dropped:** `ToCollectionSchema` (`:222`) builds payload indexes from `d.ScalarColumns`, which includes array columns; metadata rejection doesn't reach it. Element-typed kinds are live |

### Data-flow arrows

| Row | Disposition |
|---|---|
| client → `PropertyDescriptor.is_array` → `SchemaBuilder` | ok — the arrow is what §4 exists to create; four clients currently emit nothing (spec B14) |
| `ColumnDescriptor.SqlType` → Postgres DDL | ok — `PostgresSchemaManager.cs:37` emits `SqlType` verbatim into `CREATE TABLE` |
| JSON payload → `json_populate_record` → array column *(persistence boundary)* | ok — **executed**: `text[]`, `integer[]` and `timestamptz[]` all populate from JSON arrays |
| array column → `row_to_json` → `JsonParser` → `ListValue` *(persistence boundary)* | ok — **executed**: `{"tags":["a","b"],"nums":[1,2],"stamps":["2026-08-02T00:00:00+00:00"]}` |
| `SqlType` → `ClrTypeToEngagementType` → StarRocks column | ok — `:278-279` returns `"STRING"` for every array entry as designed |
| **array value → StarRocks INSERT parameter** *(second consumer, own row)* | ok — candidate generated: does the write path handle a JSON array bound to a `STRING` column? `EngagementRepository.cs:232-234` filters only `Object`/`Undefined`, so arrays pass; `JsonElementToObject` (`:521-529`) has an explicit `JsonValueKind.Array => el.GetRawText()` arm. Binds as JSON text. Works |
| `SqlType` → `SqlTypeToPayloadKind` → Qdrant index | ok — `:222` over `ScalarColumns`, element-typed kinds as designed |

## 1. Verified-assumptions cross-check

19 of 22 reconfirmed on a fresh read. Spot-checks: B1 (`ArrayTypeOverrides` consumed only at `:264`
and `:269`), B3 (`:227`), B4 (re-derived, see §0), B5 (`SchemaRegistrar.cs:241`), B6/B9
(`PostgresSchemaManager.cs:24-30`, result used only as a local `HashSet` at `:51` and `:67`), B8
(both call sites), B10, B14 (all four client sites re-read), B15, B16-B20 (client mechanisms),
B21 (`SchemaBuilder.cs:191-193`), B22 (`StarRocksSchemaManager.cs:16`, no `ALTER` in the project).

**Three marked "reasoned" are now executed:**

- **B2 — confirmed.** All 18 type strings resolve via `format_type(t::regtype, null)`; every scalar
  has an array form.
- **B11-B13 — confirmed.** Round-trip executed in both directions, plus a negative control that
  reproduces the reported bug exactly.
- **B7 — FAILED.** See §2.1.

**Span check: no uncovered dependency.** The design's dependencies — the mapping table's totality
(B1-B5), the drift query's extensibility and its two call sites (B6, B8-B10), the round-trip
(B11-B13), each client's detection mechanism (B14, B16-B20), and the two downstream stores
(B21-B22) — are each covered by a listed item as scoped. The StarRocks *write*-path arrow was
initially uncovered (B21 covers the schema, not the INSERT), but it verified clean in-round and is
recorded in §0 rather than escalated.

## 2. Literal-wrongness findings

### §2.1 — Two SQL types need normalization, not one; the drift check would reject a correct `TIMESTAMPTZ[]` column

**Description.** Design §2 states that `format_type` "matches our `SqlType` strings
case-insensitively for eight of nine; `TIMESTAMPTZ` comes back as `timestamp with time zone`", and
§5 specifies that "the `TIMESTAMPTZ` case is asserted specifically" — singular in both places.

That count is drawn from the nine *scalar* types and predates the nine *array* types this spec
itself introduces. Post-change there are 18 SQL type strings and **two** of them mismatch:

```
 ours               | canonical                  | matches
 TIMESTAMPTZ        | timestamp with time zone   | f
 TIMESTAMPTZ[]      | timestamp with time zone[] | f
```

`NormalizePgType` built to the spec as written handles `TIMESTAMPTZ` and leaves `TIMESTAMPTZ[]`
unnormalized. Drift detection then compares `TIMESTAMPTZ[]` against `timestamp with time zone[]`,
finds them unequal, and **throws on a correct column** — failing registration for any type
declaring a `DateTime[]`, which is one of the nine array types this design exists to enable. §5's
singular assertion would not catch it: the scalar case passes while the array case is untested.

**Evidence.**
- Executed against the live Postgres 16 container:
  `SELECT t, format_type(t::regtype, null), lower(t) = format_type(t::regtype,null) FROM unnest(ARRAY[…]) t;`
  — 18 rows, 16 matching, `TIMESTAMPTZ` and `TIMESTAMPTZ[]` both `f`.
- Spec §2, "Type-name canonicalization" — "eight of nine".
- Spec §5, "Drift, both directions" — "The `TIMESTAMPTZ` case is asserted specifically".

**Proposed fix.** Correct §2's count to "sixteen of eighteen; `TIMESTAMPTZ` and `TIMESTAMPTZ[]`
both differ", and change §5 to assert **both** the scalar and array datetime cases. The
enum-driven normalization-completeness test already specified in §5 should additionally iterate
each `ClrType` in **both** its scalar and array form, so the pair is covered by construction rather
than by two hand-written cases.

### §2.2 — Python's prescribed detection mechanism fails whenever annotations are strings

**Description.** Design §4 specifies Python array detection as "`typing.get_origin`/`get_args` over
the existing `__annotations__` hint (`core.py:162`)."

`__annotations__` does not reliably hold type *objects*. Under `from __future__ import annotations`
— or PEP 563 semantics, or any quoted annotation — it holds **strings**. `typing.get_origin('list[str]')`
returns `None`, because its argument is a `str`, not a generic alias. So the prescribed mechanism
detects nothing and every array falls through to the existing `CLR_STRING` default.

This is not a hypothetical style: `iverson_client/core.py:4` is itself `from __future__ import annotations`,
so it is the house convention a user's entity module is most likely to follow. And the registrar
reads annotations **raw**, never resolved:

```python
annotations = {}
for base in reversed(cls.__mro__):
    if base is object: continue
    annotations.update(getattr(base, "__annotations__", {}))
```

The existing scalar path survives this only by accident — `_python_type_to_clr` explicitly accepts
`str | type` (`:54`) and `_PY_TO_CLR` keys on bare names (`:35-44`), so the string `'str'` happens
to hit the `"str"` key. `'list[str]'` matches no key and has no `get_origin`.

**Evidence.**
- `Iverson.Clients/Python/iverson_client/core.py:4` — `from __future__ import annotations`.
- `core.py:126-130` — raw `__annotations__` walk, no `typing.get_type_hints`.
- `core.py:54-59` — `_python_type_to_clr` accepts `str`; `_PY_TO_CLR.get(name, CLR_STRING)`.
- `core.py:35-44` — `_PY_TO_CLR` keys: `str`, `uuid`, `UUID`, `int`, `float`, `bool`, `datetime`,
  `bytes`. No parameterized forms.

**Proposed fix.** The mechanism must resolve annotations before inspecting them, or parse their
string form. Which of those to adopt is not a detail — it carries a real risk either way, so it is
surfaced as §3.1 rather than picked here.

## 3. Forced decisions

### §3.1 — Resolving Python annotations: `get_type_hints` versus string parsing

**The choice.** §2.2's fix requires one of two mechanisms, and they fail differently.

**Why it's forced.** The spec's stated mechanism does not work on string annotations, and string
annotations are the client's own house style. Something must change; both candidates carry a cost
the spec has not weighed.

**The options.**

- **`typing.get_type_hints(cls)`** — resolves string annotations into real objects, after which
  `get_origin`/`get_args` work exactly as §4 describes. But it *evaluates* every annotation in the
  class's module namespace, so it raises `NameError` on any forward reference that isn't importable
  at runtime — including the `if TYPE_CHECKING:` import pattern commonly used for relation types.
  Entity classes that register successfully today could begin failing. Whether any actually would
  needs checking against real Python entity definitions; the registrar's current raw-`__annotations__`
  walk is immune to this by construction, so adopting `get_type_hints` trades a silent miss for a
  possible hard failure on unrelated classes.
- **Parse the string form** — extend `_python_type_to_clr` to recognise `list[X]` / `List[X]` /
  `X[]` textually and map the inner name through the existing `_PY_TO_CLR`. No evaluation, so no
  `NameError` risk and no behaviour change for any currently-working class. The cost is
  hand-rolled parsing of a type grammar, which handles the common forms and will not handle
  arbitrary nesting or aliases.

A third possibility — `get_type_hints` with a fallback to string parsing on `NameError` — gets the
precision of the first without the regression risk, at the cost of both code paths existing.

## 5. Recommendation

🛑 **Surface forced decisions to user**

§3 is non-empty, which blocks regardless of §2's state. §2 carries two findings: §2.1 is a
miscount whose fix is a corrected sentence and a broadened test, and §2.2 invalidates the mechanism
§4 prescribes for one of the four clients.

Neither changes the design's shape — the mapping table, the drift-detection split, the read/write
conclusion and the other three clients' mechanisms all verified clean, several of them executed
against a live database rather than reasoned. The Postgres behaviour the spec flagged as its own
weakest point turned out correct in three of four respects; the fourth (B7) failed in exactly the
place the spec's self-assessment pointed at, which is a good argument for having run it.
