# Python Declaration Composability — Design

**Date:** 2026-08-01
**Status:** Approved, awaiting critical design review
**Scope:** `Iverson.Clients/Python/iverson_client/annotations.py` and its tests. No server change, no proto change, no other client.

## Problem

The proto wire model is a flat set of independent booleans. `PropertyDescriptor`
(`Iverson.Clients/Common/Proto/object_mapping.proto:43-66`) carries `is_key`, `is_search_key`,
`is_large_field`, `is_embedding`, `is_chunk`, `is_metadata`, `is_summary_target`,
`is_keywords_target`, `extract_hint` and `description` as separate fields, all independently
settable. The .NET client mirrors this exactly: one `Attribute` per declaration, freely combined
on a property.

Python alone collapses six of those independent flags into a single mutually-exclusive
`kind: str` (`annotations.py:28`), dispatched through an `if/elif` chain (`annotations.py:352-374`).
A field therefore carries exactly one structural declaration, and every combination is
inexpressible unless someone remembers to add a bypass kwarg to the other factory.

**This has produced four bugs, three of them in Python.** Each previous fix added a kwarg to close
one combination, treating the instance rather than the axis:

1. Go's `metadata` as a tag *kind* — fixed `e4a77ff` by moving to an independent `iverson_meta` tag key.
2. Python's tenant marker non-composable — fixed 2026-07-28 by adding a `tenant` kwarg to `iverson_search_key`.
3. Python's three enrichment declarations as kinds — fixed 2026-07-28 (`6c3a1d9`) by adding
   `summary`/`keywords`/`extract_hint` kwargs to four factories.
4. **Open, and the subject of this spec:** `kind` is still the composition axis, so
   `large_field`+`chunk`, `large_field`+`metadata`, `chunk`/`embedding`+`metadata`, and
   `metadata`+`tenant` remain inexpressible in Python while the proto and .NET allow them.

The pattern is structural, not incidental: a Python `FieldMeta` *is* the attribute's default value,
so a field carries exactly one, and composability exists only where a flag is *also* exposed as a
kwarg on every other factory. That is an O(n²) maintenance surface which has silently fallen behind
three times.

## Goal

Make `kind` stop being the composition axis for scalar properties, so that no future declaration
can be non-composable by construction.

Non-goal: closing only the four known-missing combinations. That is what the previous three fixes
did, and it is why there is a fourth.

## Design

### 1. `FieldMeta` becomes a flat record of independent flags

Delete `kind` for scalar properties. Retain the relation axis as `relation_kind: str | None`,
because relations are genuinely exclusive — they serialize to a different proto message
(`RelationDescriptor`), and a field is either a scalar property or a relation, never both.

Flags, isomorphic to `PropertyDescriptor`:

| Field | Type | Notes |
|---|---|---|
| `key` | bool | |
| `search_key` | bool | with `search_key_order: int = 0` |
| `large_field` | bool | |
| `embedding` | bool | |
| `chunk` | bool | with `chunk_max_tokens: int = 512`, `chunk_overlap: int = 64`, `chunk_contextual: bool = False` |
| `metadata` | bool | |
| `tenant` | bool | |
| `summary` | bool | |
| `keywords` | bool | |
| `extract_hint` | str | `""` = not declared |
| `description` | str | |
| `relation_kind` | str \| None | relation axis only |
| `related_type` | str \| None | relation axis only |

Every field carries a default, so the dataclass has no ordering constraint (`kind` is currently the
only field without one — `annotations.py:28`).

### 2. `iverson_field(...)` is the single real constructor

One factory taking the full flag set. This is the one place flags are defined, which is precisely
what makes the bug class structurally impossible: there is no second signature that can fall behind.

```python
body: str = iverson_field(large_field=True, chunk=True, chunk_max_tokens=512)
```

### 3. Named factories become one-line presets

```python
def iverson_key(description: str = "") -> FieldMeta:
    return iverson_field(key=True, description=description)
```

All eleven scalar factories become presets: `iverson_key`, `iverson_search_key`, `iverson_metadata`,
`iverson_description`, `iverson_large_field`, `iverson_embedding`, `iverson_chunk`,
`iverson_summary`, `iverson_keywords`, `iverson_extracted`, `iverson_tenant`. The four relation
factories (`many_to_one`, `many_to_many`, `one_to_many`, `one_to_one`) construct `relation_kind`
directly and are not presets over `iverson_field` — they are on the other axis.

**Each preset takes only its own parameters** — `iverson_search_key(order=...)`,
`iverson_chunk(max_tokens=...)`, plus `description`. The cross-cutting kwargs added by fixes 2 and 3
(`metadata=`, `tenant=`, `summary=`, `keywords=`, `extract_hint=` on `iverson_search_key`,
`iverson_metadata`, `iverson_large_field`, `iverson_description`) are **removed**. Any combination
goes through `iverson_field()`:

```python
# before: iverson_search_key(order=0, metadata=True)
# after:  iverson_field(search_key=True, search_key_order=0, metadata=True)
```

This rule is the point of the design. Keeping those kwargs would rebuild the combinatorial
per-factory surface that generated the bug four times — presets would drift out of sync with
`iverson_field` exactly as they drifted from each other.

### 4. The blank-hint guard moves inside `iverson_field`

`_enrichment_kwargs` (`annotations.py:53-74`) rejects a `None` or blank-but-non-empty
`extract_hint`, because the server treats an empty hint as "not an extraction target" and would
silently drop it. `""` remains the "not declared" default and must not raise.

Moving the guard into `iverson_field` means it covers every declaration path by construction,
rather than by four separate factories each remembering to call it. `iverson_extracted(hint)` keeps
its mandatory-hint semantics by passing `hint or None`, so an empty string is rejected there while
remaining the opt-out default elsewhere.

### 5. The decorator's `if/elif` chain becomes independent `if`s

This is the actual fix; everything above is what makes it safe.

```python
if meta.relation_kind:
    relations.append({"field": field_name, "kind": meta.relation_kind,
                      "related_type": meta.related_type})
    continue          # relations are exclusive; skip all scalar handling

if meta.key:          key_field = field_name
if meta.search_key:   search_keys.append((field_name, meta.search_key_order))
if meta.large_field:  large_fields.append(field_name)
if meta.embedding:    embedding_fields.append(field_name)
if meta.chunk:        chunk_fields.append((field_name, meta.chunk_max_tokens,
                                           meta.chunk_overlap, meta.chunk_contextual))
if meta.metadata:     metadata_fields.append(field_name)
if meta.tenant:       tenant_fields.append(field_name)
if meta.summary:      summary_fields.append(field_name)
if meta.keywords:     keywords_fields.append(field_name)
if meta.extract_hint: extracted_fields[field_name] = meta.extract_hint
if meta.description:  descriptions[field_name] = meta.description

plain_fields.append(field_name)   # every scalar field, exactly once
```

**The `plain_fields` append is the one place this refactor can silently corrupt output.** Today it
happens inside each `elif` branch *and* in the terminal `else`; under independent `if`s that would
double-append a field carrying two flags. It moves to a single unconditional append after the flag
checks. `core.py:147` iterates `meta["fields"]` directly, so a duplicate there emits a duplicate
`PropertyDescriptor` on the wire. This gets a dedicated test.

Note the relations dict keeps its `"kind"` key even though the `FieldMeta` field is renamed —
`core.py:69` and `core.py:188` read `rel["kind"]`.

### 6. `_iverson_meta`'s output shape does not change

Same fifteen keys, same value types. `core.py` and `sample/main.py` are the only consumers and
neither needs an edit. This also makes the change directly assertable against `_iverson_meta`.

## Testing

Every composition test asserts **both** halves. A one-sided test lets the other declaration be
silently dropped, which is how instance 3 survived its own test suite.

- The four previously-inexpressible combinations: `large_field`+`chunk`, `large_field`+`metadata`,
  `chunk`/`embedding`+`metadata`, `metadata`+`tenant`.
- `plain_fields` contains each field exactly once under a multi-flag declaration (guards §5's
  known hazard).
- The blank-hint guard still rejects `None` and blank-but-non-empty hints, and still accepts `""`.
- The existing relation tests continue to pass unchanged, confirming the relation axis is intact.

## Migration

The break is confined to one file. `sample/models.py` uses no cross-cutting kwargs and needs zero
edits. Eleven call sites in `tests/test_schema_registrar.py` use the removed kwargs and must be
rewritten to `iverson_field(...)`: `:52`, `:86`, `:100`, `:101`, `:102`, `:103`, and the five
blank-hint guard tests at `:379`, `:383`, `:387`, `:391`, `:395`.

`FieldMeta` is exported from `iverson_client/__init__.py:15,36`, so removing `kind` is a **public**
API break, not merely an internal one. Accepted: Ben authorized breaking changes for this work, and
`FieldMeta` is a descriptor users receive from factories rather than construct themselves.

## Verified assumptions

Verified against `main@4522745`. Baseline: `python3 -m pytest tests/ -q` → **158 passed in 0.41s**.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `FieldMeta` is defined once and constructed only in `annotations.py` | Defined `annotations.py:21`; 15 constructions, all in that file. **But exported at `__init__.py:15,36`** — public API |
| A2 | `FieldMeta.kind` is read nowhere else | Only `annotations.py:338-370`. Other `.kind` hits are unrelated join kinds (`pipeline.py`, `search.py`, `aggregate.py`) or the relations dict |
| A3 | `_iverson_meta`'s keys are exactly as designed | `core.py:132-198` reads precisely those keys; `sample/main.py:53-55` reads `_iverson_meta` wholesale |
| A4 | `core.py` tolerates a field in multiple flag sets | `core.py:156-179` builds every flag by independent set-membership. **Already fully composition-ready — needs no change** |
| A5 | A duplicate in `plain_fields` would emit a duplicate property | `core.py:147` — `for field_name in meta["fields"]`. Risk confirmed real |
| A6 | Relation fields are excluded from properties | `core.py:148-149` — `if field_name in relation_fields: continue` |
| A7 | `key` composes with other flags | `core.py:159-160` derives `is_key`/`is_nullable` by equality to the scalar `key_field` |
| A8 | *(recurrence — whole factory set)* All 15 factories are expressible under the new model | 11 scalar factories become presets; the 4 relation factories set `relation_kind` directly |
| A9 | Size of the break | 11 cross-kwarg call sites, **all** in `tests/test_schema_registrar.py` |
| A10 | `sample/models.py` impact | Uses only plain presets (`:19-42`) — zero edits needed |
| A11 | Blank-hint guard semantics survive the move | Guard at `annotations.py:53-74`; its 5 tests (`:379-395`) call through the removed kwargs and must be rewritten |
| A12 | Dataclass ordering is safe | `@dataclass` at `:20`; `kind` (`:28`) is the only field lacking a default |
| A13 | No external dependency on Python's kind strings | No codegen for `annotations.py` (`scripts/` holds only `generate_protos.sh`); no other language reads them |
| A14 | Which tests assert on `kind` | `test_annotations.py` asserts only `rel["kind"]` (`:80`, `:86`, `:136`, `:147`) — the preserved relations dict |
| A15 | `_resolve_tenant_field` is unaffected | `core.py:204` takes `list[str]`; unchanged |
| A16 | *(dependents)* Nothing assumes exactly-one-set membership | `core.py:156-179` tests each set independently |
| A17 | A description-only field still reaches `plain_fields` | Today via `kind="plain"` → terminal `else`; under the new model via the unconditional append |

## Known issues / accepted as out of scope

**Combinations the server rejects become expressible.** `kind` was accidentally blocking some of
them — e.g. an enrichment target that is also a chunk or embedding. The server rejects these with a
clear `InvalidArgument` (`SchemaRegistrationOrchestratorTests.cs:316`, and key/tenant-as-enrichment-target
at `:301`), so the failure stays loud and immediate. Client-side validation is deliberately **not**
added: it would duplicate the server's rules in a fifth place, and nothing is literally wrong
without it. Ben accepted this.

**This fixes Python only.** The other four clients already compose correctly; Go was fixed at
`e4a77ff` and .NET has always used independent attributes.
