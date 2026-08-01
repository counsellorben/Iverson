# Critical Design Review: 2026-08-01-python-declaration-composability-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-01-python-declaration-composability-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration re-derived from the amended spec before consulting round 1.

### Sections

| Row | Disposition |
|---|---|
| Problem | ok — the amended bullet 4 claims only that the two combinations are legal and inexpressible. Both re-verified: legal per `SchemaBuilder.cs` (no rule pairs them), inexpressible per the `elif` chain at `annotations.py:352-374` |
| Goal / Non-goal | ok — "four known-missing combinations" is still accurate; all four are inexpressible, two legal and two rejected |
| Design §1 (flat `FieldMeta`) | ok — 13 fields re-checked. `tenant`/`relation_kind`/`related_type` are correctly outside `PropertyDescriptor` (they map to `TypeDescriptor.TenantField` and `RelationDescriptor`); the other 10 map 1:1 |
| Design §2 (`iverson_field`) | ok — no name collision: `grep -rn "iverson_field"` over the Python client returns nothing |
| Design §3 (presets, cross-kwargs removed) | ok — all 11 scalar presets re-derived; each expressible as a single-flag delegation |
| Design §4 (hint guard) | ok — truthiness re-checked: `iverson_extracted` passes `hint or None`, so `""` → `None` → rejected; the kwarg default `""` stays falsy and unrejected |
| Design §5 (independent `if`s) | ok — every one of the 11 flag harvests traced to its collection; `extract_hint`/`description` correctly guarded on truthiness, matching `annotations.py:341`,`:349`; tuple order `(name, max_tokens, overlap, contextual)` matches `core.py:137`'s unpack |
| Design §6 (output shape) | ok — 15 keys re-counted at `annotations.py:380-396` |
| Testing | ok — both mandated composition tests are assertable at `_iverson_meta` level and both combinations register |
| Migration (11 sites) | ok — re-counted, and each rewrite's target combination checked for server legality (see rules table) |
| Verified assumptions (A1–A18) | ok — see §1 |
| Known issues | ok — the claim is scoped to its `metadata`+`large_field` example and is true as written for it |

### Rules and operands

| Row | Disposition |
|---|---|
| `metadata` × {embedding, chunk, array, large_field} rejection | ok — `SchemaBuilder.cs:94`,`:123-126`; spec's amended text matches |
| `search_key` × large-field-set rejection | ok — `SchemaBuilder.cs:117-121`. Note `largeFields` includes embedding (`:63`) and chunk (`:76`) fields, so `search_key` conflicts with all three. No spec claim contradicts this |
| Enrichment-target eligibility — **every producer enumerated** | ok — three producers: `IsSummaryTarget` (`:79`), `IsKeywordsTarget` (`:82`), non-empty `ExtractHint` (`:85`). Rules at `SchemaRegistrationOrchestrator.cs:136-166`: must be string; not key/tenant/owner; not embedding/chunk; non-blank hint. `large_field` is **not** in the rejection set, so the migration of `iverson_large_field(extract_hint=…)` at `test_schema_registrar.py:102` stays legal |
| Migration rewrites × server legality (both directions) | ok — all 11 checked. `search_key`+`metadata` (`:52`), `search_key`+`tenant` (`:86`), `search_key`+`summary` (`:100`), `metadata`+`keywords` (`:101`), `large_field`+`extract_hint` (`:102`), `description`+`summary` (`:103`), and the five `iverson_field(extract_hint="   ")` guard tests — none hits `badMetadata`, the search-key/large-field conflict, or an enrichment rule |
| `plain_fields` append, over- and under-inclusion | ok — over: single unconditional append replaces per-branch appends; under: the outer `else` at `annotations.py:375-376` for non-`FieldMeta` fields is outside the block and untouched |
| Relation exclusion (`continue`) | ok — both directions. Relations get no `plain_fields` entry today (no append in the `:367` branch) and none after; flag harvest skipped today via `:338` and after via `continue` |
| Identity rule: `key_field` scalar assignment | ok — last-writer-wins on multiple `key=True` today and after; no change in conflation behavior |
| **`large_field`+`chunk` — does it deliver distinct server behavior?** | **dropped** — `IsChunk` already adds to `largeFields` (`SchemaBuilder.cs:76`), and `largeFields` is a `HashSet` (`:40`), so adding `IsLargeField` produces a byte-identical `SchemaDescriptor`. Further, `LargeFieldColumns` (`:170`) is **written but never read** in production — the only reads are `SchemaBuilderTests.cs:77,80`. So `large_field`'s entire observable effect is participating in two rejection rules. The combination is therefore a semantic no-op. Fails literal-wrongness: the spec claims only that it is *legal* and *inexpressible* — both true — and never claims distinct server semantics. The mandated test asserts both flags at the `_iverson_meta` level, which is real and passes |
| **Newly-expressible combinations that silently no-op rather than reject** | **dropped** — `key`+`metadata` and `key`+`search_key` become expressible; `SchemaBuilder.cs:53` iterates `Where(p => !p.IsKey)`, so both are silently ignored server-side rather than rejected. Fails literal-wrongness: the spec never claims these combinations, and its Known issues sentence about loud failure is scoped to its `metadata`+`large_field` example, where it is accurate |
| **`iverson_field` absent from `__init__.py`'s export list** | **dropped** — `__init__.py:4-16`,`:25-36` already omits 6 of the 11 scalar factories (`iverson_embedding`, `iverson_chunk`, `iverson_summary`, `iverson_keywords`, `iverson_extracted`, `iverson_tenant`). Both `sample/models.py:6` and `tests/test_schema_registrar.py:9` import from `iverson_client.annotations` directly, so the established convention reaches `iverson_field` unchanged. Fails literal-wrongness: nothing is unreachable |

### Data-flow arrows

| Row | Disposition |
|---|---|
| `FieldMeta` → decorator loop | ok — every flag in the §1 table has a harvest line in §5; none orphaned |
| decorator → `_iverson_meta` | ok — 15 keys preserved; `search_keys.sort` at `annotations.py:378` is outside the modified loop and untouched |
| `_iverson_meta` → `core.py:_build_request` → `PropertyDescriptor` *(caller 1)* | ok — `core.py:156-179` sources all 22 proto fields; every one traced to a `_iverson_meta` key or a constant |
| `_iverson_meta` → `core.py:224` `_entity_to_struct` *(caller 2)* | ok — presence check only, then iterates `__annotations__`; reads no flag key |
| `_iverson_meta` → `core.py:287` `EntityCoordinator.__init__` *(caller 3)* | ok — reads `type_name` and `key_field` only |
| `_iverson_meta` → `sample/main.py:53-55` *(caller 4)* | ok — wholesale read for display |
| `PropertyDescriptor` → `SchemaBuilder` → `SchemaDescriptor` | ok — this arrow was round 1's finding; re-traced against the amended spec, which now matches the server's actual rules |
| `SchemaDescriptor.LargeFieldColumns` → *(no production consumer)* | ok — established by grep; informs the `large_field`+`chunk` disposition above, not a finding on its own |

## 1. Verified-assumptions cross-check

All 18 assumptions reconfirmed on a fresh read of the cited evidence. Spot-checks:

- **A4** — `core.py:156-179` still builds every flag by independent set-membership.
- **A5** — `core.py:147` still iterates `meta["fields"]`; the duplicate hazard remains real and remains flagged.
- **A9/A10** — 11 cross-kwarg sites re-counted in `tests/test_schema_registrar.py`; `sample/models.py:19-42` still clean.
- **A13** — re-grepped: no codegen for `annotations.py`; `scripts/` holds only `generate_protos.sh`.
- **A18** *(added round 1)* — re-verified against `SchemaBuilder.cs:40`, `:76`, `:94`, `:123-126`. Holds, including the `HashSet` detail that makes `large_field`+`chunk` legal.

**Span check: no uncovered dependency.** The design's dependencies — client-side expressibility (A8), consumer tolerance of multi-set membership (A4, A16), output-shape stability (A3), migration surface (A9, A10, A14), and server legality of the target combinations (A18) — are each covered by a listed item as scoped. Round 1's gap is closed by A18.

## 2. Literal-wrongness findings

No literal-wrongness findings.

Three substantive candidates were generated and dropped; each is recorded in §0 with its evidence and the reason it failed the test. The most notable is that `large_field`+`chunk` is a semantic no-op server-side — real, verified, and not a defect in the spec, because the spec claims only legality and inexpressibility, both of which hold.

## 3. Forced decisions

No forced decisions found.

## 4. Previously addressed

- **Round 1 §2.1** — resolved. Problem bullet 4 now names `large_field`+`chunk` and `metadata`+`tenant` as the legal pair and identifies `large_field`+`metadata` and `chunk`/`embedding`+`metadata` as server-rejected, citing `SchemaBuilder.cs:94`,`:123-126`. The Testing section no longer mandates the two impossible composition tests and explains why a client-side assertion would have passed vacuously.
- **Round 1 §3.1** — resolved. Ben picked "accept the regression." Known issues now states that `kind` has been satisfying the `badMetadata` rule by construction and that removing it gives that up, records the declined guard alternative, and keeps the no-client-validation decision.
- **Round 1 §1 span check** — resolved. `A18` added to `Verified assumptions`, covering server legality of the target combinations.

## 5. Recommendation

✅ **Approve as-is**

§2 and §3 are both empty. The round-1 findings are correctly applied, the span check now closes, and the surface enumerated in §0 is fully disposed. The spec is ready for implementation planning.
