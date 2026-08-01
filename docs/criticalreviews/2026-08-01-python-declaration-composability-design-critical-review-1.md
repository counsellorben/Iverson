# Critical Design Review: 2026-08-01-python-declaration-composability-design (Round 1)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-01-python-declaration-composability-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem | → §2.1 — the claim that the four combinations are allowed by "the proto and .NET" is false for two of them |
| Goal | ok — "stop `kind` being the composition axis" is well-formed and survives §2.1 |
| Design §1 (flat `FieldMeta`) | ok — all 13 listed fields map to real `PropertyDescriptor` fields (`object_mapping.proto:43-66`) except `tenant`/`relation_kind`/`related_type`, which correctly map to `TypeDescriptor.TenantField` and `RelationDescriptor` |
| Design §2 (`iverson_field`) | ok — single-constructor claim checked against the 15 construction sites in `annotations.py` |
| Design §3 (presets) | ok — 11 scalar + 4 relation factories enumerated and counted against `annotations.py:44-277`; count is correct |
| Design §4 (hint guard) | ok — `_enrichment_kwargs` semantics at `annotations.py:53-74` re-read; `""` accept / `None`+blank reject is stated correctly, and `iverson_extracted`'s `hint or None` preserves mandatory-hint behavior |
| Design §5 (independent `if`s) | ok — the `plain_fields` double-append hazard is correctly identified as the refactor's one corruption path |
| Design §6 (output shape) | ok — 15 keys counted at `annotations.py:380-396`; matches |
| Testing | → §2.1 — two of the four mandated composition tests assert a capability the server rejects |
| Migration | ok — 11 cross-kwarg sites re-counted in `tests/test_schema_registrar.py`; `sample/models.py:19-42` confirmed clean |
| Verified assumptions | → §1 — all 17 reconfirmed; one uncovered dependency found |
| Known issues | → §3.1 — the no-client-validation decision rests on a premise §2.1 falsifies |

### Rules and operands

| Row | Disposition |
|---|---|
| Flag dispatch: `elif` chain → independent `if`s | ok — each of the 11 scalar flags traced to its collection list; no flag loses its harvest |
| `plain_fields` append (over-inclusion) | ok — spec catches the double-append; `core.py:147` iterates `meta["fields"]` directly, so a duplicate would emit a duplicate `PropertyDescriptor`. Correctly flagged for a dedicated test |
| `plain_fields` append (under-inclusion) | ok — the outer `else` for non-`FieldMeta` fields (`annotations.py:375-376`) is outside the shown block and untouched; description-only fields still land via the unconditional append (A17) |
| Relation exclusion (`continue`) | ok — both directions. Relation fields are excluded from `plain_fields` today (no append in the `elif` branch at `:367`) and the `continue` preserves that; flag harvest is skipped for relations today via `:338` and by `continue` after |
| Relation dict `"kind"` key | ok — `core.py:69` and `core.py:188` read `rel["kind"]`; spec explicitly preserves the key despite renaming the `FieldMeta` field |
| Blank-hint guard, both directions | ok — over-rejection: `""` must not raise, preserved; under-rejection: `None` and whitespace must raise, preserved. 5 existing guard tests identified for rewrite |
| `search_keys.sort` | ok — at `annotations.py:378`, outside the loop the spec modifies; untouched |
| **Eligibility predicate: which combinations are legal server-side** | **→ §2.1** — enumerated every producer of a rejection in `SchemaBuilder.cs` and `SchemaRegistrationOrchestrator.cs`; two of the spec's four targets are rejected |
| Identity/exclusion: `key_field` scalar assignment | ok — `if meta.key: key_field = field_name`; `core.py:159-160` derives `is_key`/`is_nullable` by equality, so key+other flags is coherent |

### Data-flow arrows

| Row | Disposition |
|---|---|
| `FieldMeta` → decorator loop | ok — every flag read has a corresponding collection append |
| decorator → `_iverson_meta` (15 keys) | ok — key set unchanged; shape assertions hold |
| `_iverson_meta` → `core.py:_build_request` → `PropertyDescriptor` | ok — `core.py:156-179` sources every parameter by independent set-membership; a field in multiple sets produces one property with multiple flags set |
| `_iverson_meta` → `core.py:224` (`_entity_to_struct`) *(second caller)* | ok — uses `meta` only as a presence check, then iterates `__annotations__`; reads no flag key |
| `_iverson_meta` → `core.py:287` (`EntityCoordinator.__init__`) *(third caller)* | ok — reads only `type_name` and `key_field`, both unchanged |
| `_iverson_meta` → `sample/main.py:53-55` | ok — reads `_iverson_meta` wholesale for display; no key dependency |
| `PropertyDescriptor` → server `SchemaBuilder` | → §2.1 — this arrow is where the spec's target combinations are actually adjudicated, and the spec never traced it |

## 1. Verified-assumptions cross-check

All 17 assumptions reconfirmed on a fresh read of the cited evidence. Spot-checks against the citations:

- **A4** holds and is the spec's strongest claim — `core.py:156-179` builds every flag by independent set-membership; no change needed there.
- **A5** holds — `core.py:147` iterates `meta["fields"]`; the duplicate hazard is real.
- **A9/A10** re-counted: 11 cross-kwarg sites, all in `tests/test_schema_registrar.py`; `sample/models.py` clean.
- **A12** holds — `kind` (`annotations.py:28`) is the only field without a default.
- **A16** holds — nothing assumes exactly-one-set membership.

**Span check — one uncovered dependency:**

> **The design depends on its four target combinations being legal at the server, and no listed assumption covers that.** A8 verifies that all 15 factories are *expressible* under the new model; A13 verifies nothing external reads Python's kind strings. Neither establishes that a Python client which *can* emit `large_field`+`metadata` will have that schema *accepted*. The assumption set is scoped to the client; the design's justification reaches into the server. Verified in-round — see §2.1.

## 2. Literal-wrongness findings

### §2.1 — Two of the four target combinations are rejected by the server, so the spec's motivating claim is false and two of its mandated tests cannot assert what they claim

**Description.** The Problem section states that `large_field`+`chunk`, `large_field`+`metadata`, `chunk`/`embedding`+`metadata`, and `metadata`+`tenant` "remain inexpressible in Python while the proto and .NET allow them." The proto permits the bit combinations, but .NET registration goes through the same server validation, and that validation rejects two of the four. The Testing section then mandates a test per combination, asserting both halves compose.

Full legality map, derived by enumerating every rejection producer in the registration path:

| Combination | Verdict | Evidence |
|---|---|---|
| `large_field`+`chunk` | **Legal** | `SchemaBuilder.cs:66-77` and `:88-89` both add to `largeFields`, a `HashSet` (`:40`); no rule pairs them |
| `metadata`+`tenant` | **Legal** | `tenant` maps to `TypeDescriptor.TenantField`, not a property bool; `badMetadata` (`:94`) does not test it |
| `large_field`+`metadata` | **REJECTED** | `SchemaBuilder.cs:94` adds to `badMetadata`; `:123-126` throws `InvalidOperationException` — "cannot have both [IversonMetadata] and an embedding, chunk, array, or large-field annotation" |
| `chunk`/`embedding`+`metadata` | **REJECTED** | Same rule, `SchemaBuilder.cs:94`, `:123-126` |

The consequence for the mandated tests is the sharp part. The spec's tests assert on `_iverson_meta` / `PropertyDescriptor`, which are client-side artifacts — so tests for the two rejected combinations **would pass while asserting a capability that can never register**. They would encode as a guarantee precisely the thing the server forbids, and a green suite would say the feature works.

There is a second-order effect worth naming: Python's `kind` axis was *accidentally enforcing* the `badMetadata` rule. `iverson_metadata()` produced `kind="metadata"`, which structurally prevented a field from also being `large_field`/`chunk`/`embedding`. Removing the axis removes that accidental enforcement — correctly, since it was enforcement by accident rather than by design, but it means the failure moves from "unrepresentable" to "rejected at registration."

**Note this does not invalidate the design.** The structural goal stands on its own: `kind` should stop being the composition axis, two of the four target combinations are genuinely legal and genuinely blocked today, and the O(n²) kwarg surface is a real defect regardless. What is wrong is the spec's justification set and its test list.

**Evidence.**
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:91-98` — the `IsMetadata` block populating `badMetadata`.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:123-126` — the throw.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:40` — `largeFields` is a `HashSet`, so `large_field`+`chunk` double-add is harmless.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:142-160` — the separate enrichment-target rules (key/tenant/owner, embedding/chunk), which the spec's Known issues already gestures at.

**Proposed fix.**
1. Correct the Problem section's combination list to the two that are actually legal — `large_field`+`chunk` and `metadata`+`tenant` — and drop the "the proto and .NET allow them" framing for the other two, which .NET cannot register either.
2. Correct the Testing section: composition tests for `large_field`+`chunk` and `metadata`+`tenant` only. If coverage of the rejected pair is wanted, it must assert *rejection* — and note that the rejection is server-side, so a client-only test cannot express it without adding client validation (see §3.1).
3. Keep the structural design unchanged.

## 3. Forced decisions

### §3.1 — Whether to replace the accidental `badMetadata` enforcement that removing `kind` gives up

**The choice.** The spec's Known issues section decides *not* to add client-side validation for server-rejected combinations, on the reasoning that the server rejects loudly and duplicating its rules in a fifth place is not worth it. Ben accepted this. That decision was made under the framing that such combinations were incidental side effects of the restructure. §2.1 shows the framing was wrong: one specific server rule — `metadata` vs `embedding`/`chunk`/`array`/`large_field` — is currently enforced *by construction* in Python because `metadata` is a `kind`, and this design removes that enforcement while advertising two of the newly-expressible combinations as goals.

**Why it's forced.** The codebase constraint is real and specific: `SchemaBuilder.cs:94` is a rule Python satisfies today for free and will not satisfy after the change. The user cannot both remove `kind` and keep the accidental enforcement — a choice has to be made about what replaces it, including the choice to replace it with nothing.

**The options.**
- **Accept the regression.** No client validation; a Python user writing `iverson_field(metadata=True, large_field=True)` learns at registration time via `InvalidArgument`. Consistent with the spec's stated no-fifth-place principle, and with the other four clients, none of which validate this either.
- **Add the one guard in `iverson_field`.** Reject `metadata` combined with `embedding`/`chunk`/`large_field` at declaration time. Narrow (one rule, one place, applied by construction to every path), but it does put a copy of a server rule in the client, and would need to stay in sync.
- **Add the guard and accept the divergence explicitly.** Same as above, plus a note that Python is deliberately stricter than the other four clients on this one rule, since it is the only client that used to enforce it.

Not picked here.

## 5. Recommendation

🛑 **Surface forced decisions to user**

§2.1 requires correcting the spec's motivating combination list and its test list — the design itself is sound and needs no structural change. §3.1 needs Ben's input before the spec is final, because the "no client-side validation" ruling in Known issues was made on a premise §2.1 falsifies.
