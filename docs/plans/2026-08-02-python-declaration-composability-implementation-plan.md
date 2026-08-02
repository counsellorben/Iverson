# Python Declaration Composability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-01-python-declaration-composability-design.md` (commit SHA: `0a26877`)

**Goal:** Make `kind` stop being the composition axis for scalar field declarations in the Python client, so no future declaration can be non-composable by construction.

**Architecture:** One file changes. `FieldMeta` becomes a flat record of independent flags mirroring the proto's `PropertyDescriptor`; `iverson_field(...)` becomes the single real constructor holding every flag; the eleven named scalar factories become one-line presets over it; the decorator's `if/elif` dispatch becomes independent `if`s. `core.py` already consumes `_iverson_meta` by independent set-membership and needs no change.

**Tech stack:** Python ≥3.11 (running 3.14.4), pytest 9.1.1, stdlib `dataclasses`. No new dependency.

---

## Global Constraints

From the spec's scope line and design rules. Both tasks must hold to these.

- **Scope is `annotations.py` and its tests.** No server change, no proto change, no other client language.
- **`_iverson_meta`'s output shape does not change** — the same fifteen keys, same value types. `core.py` and `sample/main.py` are its consumers and neither may need an edit.
- **The relations dict keeps its `"kind"` key** even though the `FieldMeta` field is renamed to `relation_kind` — `core.py:69` and `core.py:188` read `rel["kind"]`.
- **Presets take only their own parameters.** The cross-cutting kwargs (`metadata=`, `tenant=`, `summary=`, `keywords=`, `extract_hint=`) are removed from `iverson_search_key`, `iverson_metadata`, `iverson_large_field` and `iverson_description`. All composition goes through `iverson_field`. Re-adding any of them rebuilds the O(n²) surface this work exists to remove.
- **No client-side validation of server-rejected combinations** — see Known issues.
- **Every composition test asserts both halves.** A one-sided test lets the other declaration be silently dropped.

## File Structure

**Modify**
- `Iverson.Clients/Python/iverson_client/annotations.py` — flatten `FieldMeta`, add `iverson_field`, convert the eleven scalar factories to presets, convert the decorator's dispatch to independent `if`s, delete `_enrichment_kwargs` and `_RELATION_KINDS`.

**Test**
- `Iverson.Clients/Python/tests/test_schema_registrar.py` — migrate the eleven cross-kwarg call sites (Task 1); add the composition tests (Task 2).

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time (A1–A18) and **not** re-verified here. The load-bearing ones:

- **A4:** `core.py:156-179` builds every proto flag by independent set-membership, so a field in multiple flag sets produces one property with multiple flags set. The registrar is already composition-ready and needs no change.
- **A5:** `core.py:147` iterates `meta["fields"]` directly, so a duplicate entry in `plain_fields` emits a duplicate `PropertyDescriptor` on the wire.
- **A2:** `FieldMeta.kind` is read only at `annotations.py:338-370`; every other `.kind` hit is an unrelated join kind or the relations dict.
- **A12:** `kind` (`annotations.py:28`) is the only `FieldMeta` field without a default, so removing it leaves an all-defaults dataclass with no ordering constraint.
- **A14:** `test_annotations.py` asserts only on `rel["kind"]` (`:80`, `:86`, `:136`, `:147`) — the preserved relations dict.
- **A18:** `large_field`+`chunk` and `metadata`+`tenant` are legal at the server; `large_field`+`metadata` and `chunk`/`embedding`+`metadata` are rejected (`SchemaBuilder.cs:94`, `:123-126`).

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `main@2d3e725`. Baseline reproduced: `python3 -m pytest tests/ -q` → **158 passed**.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | Both touched files exist at the cited paths | `ls` of `iverson_client/annotations.py` (15265 B) and `tests/test_schema_registrar.py` (17765 B) |
| P2 | Command | `python3 -m pytest tests/ -q` is the repo's real invocation | `pyproject.toml` `[tool.pytest.ini_options] testpaths = ["tests"]`; no `Makefile`/`tox.ini`/`justfile`/`pytest.ini`/`setup.cfg` exists. **`python` is not on PATH — `python3` is required** |
| P3 | Command | Toolchain versions support the plan's code | `python3 --version` → 3.14.4 (`requires-python >=3.11`); `pytest --version` → 9.1.1 |
| P4 | Code validity | `str \| None` annotations are legal | `from __future__ import annotations` at `annotations.py:14`, and 3.14 supports the syntax natively |
| P5 | Signature *(sibling set — all 15 factories)* | Every factory's current public signature is known, so presets preserve param names and defaults | `annotations.py:44` `iverson_key(description="")`; `:77` `iverson_search_key(order=0, …)`; `:108` `iverson_metadata(…)`; `:132` `iverson_description(description, …)`; `:154` `iverson_large_field(…)`; `:176` `iverson_embedding(description="")`; `:185` `iverson_chunk(max_tokens=512, overlap=64, description="", contextual=False)`; `:208` `iverson_summary(description="")`; `:218` `iverson_keywords(description="")`; `:228` `iverson_extracted(hint, description="")`; `:251` `iverson_tenant(description="")`; `:260`/`:265`/`:270`/`:275` relation factories all `(type_name: str)` |
| P6 | Consumer impact | `_RELATION_KINDS` is safe to delete | Three references total: declaration `:282`, and `:338`/`:367` — both inside the dispatch this plan replaces |
| P7 | Consumer impact | `_enrichment_kwargs` is safe to delete | Six references, all in `annotations.py`: declaration `:53` and the five factory bodies (`:104`, `:128`, `:150`, `:172`, `:247`) that this plan rewrites |
| P8 | Consumer impact **(Cat 6 — required, plan has `Modify:` entries)** | No caller of the eleven scalar factories exists outside the Python client | `grep -rln` over all `*.py` in the repo for the nine non-trivial factory names returned **nothing** outside `Iverson.Clients/Python` |
| P9 | Consumer impact | `test_annotations.py` and `test_entity_coordinator.py` need no migration | `test_annotations.py:6-9` imports only `iverson_entity`/`iverson_key`/`iverson_search_key`/`iverson_large_field`, called with `order=` only; `test_entity_coordinator.py:12` imports only `iverson_entity`/`iverson_key` |
| P10 | Consumer impact | `__init__.py`'s six named factory imports all survive | `__init__.py:4-16` imports `iverson_entity`, `iverson_key`, `iverson_search_key`, `iverson_large_field`, `iverson_metadata`, `iverson_description`, the four relation factories and `FieldMeta` — every one is retained by this plan |
| P11 | Code validity | The exact current text of the eleven migration targets is known | `region` (`iverson_search_key(order=0, metadata=True, description="Publication region.")`), `tenant_id` (`iverson_search_key(order=0, tenant=True)`), `summary_key`, `keywords_meta`, `hint_field`, `described_summary` in `RegComposedEnrichmentArticle`, plus the five guard tests |
| P12 | Code validity | The blank-hint guard's exact expression and message are known | `annotations.py:64-69` — `if extract_hint is None or (extract_hint != "" and not extract_hint.strip())`, message contains "extraction hint" |
| P13 | Code validity | Collapsing the four kwarg guard tests loses no coverage | `iverson_extracted("")` (`:337`) and `iverson_extracted("   ")` (`:341`) already exist as separate mandatory-hint tests, and are untouched by this plan |
| P14 | Code validity | New tests can follow an established both-halves pattern | `TestEnrichmentTargets` at `:300` already contains four such tests (`:355-375`), each asserting two properties on one field via `props = {p.name: p for p in self._request(X).root_type.properties}` |
| P15 | Code validity | A per-class `_request` helper is the file's convention, not a new one | Defined twice already — `:258` (`TestMetadataAndDescription`) and `:301` (`TestEnrichmentTargets`) — with identical bodies |
| P16 | Command | `refactor(python-client):` and `test(python-client):` match the repo's convention | `git log -- Iverson.Clients/Python`: `fix(python-client)` ×3, `feat(python-client)` ×6, `test(clients)`, `docs(clients)`. Scope `python-client` is established; both types exist repo-wide |
| P17 | Ordering | Tasks 1 and 2 are strictly ordered, with no reverse dependency | Task 2 only appends tests that call `iverson_field`, which Task 1 introduces. Task 1 references nothing Task 2 creates |

## Tasks

### Task 1: Replace the `kind` axis with independent flags, and migrate the call sites

Atomic by necessity: the module is non-functional between the `FieldMeta` change and the factory rewrite, and the test suite is red until the eleven call sites migrate. One commit.

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/annotations.py`
- Test: `Iverson.Clients/Python/tests/test_schema_registrar.py`

- [ ] **Step 1: Flatten `FieldMeta`** (`annotations.py:20-39`).

Replace the dataclass body. Every field carries a default (P4, and spec A12).

```python
@dataclass
class FieldMeta:
    """Descriptor object attached as a class attribute default.

    ``@iverson_entity`` inspects ``__annotations__`` and replaces every
    ``FieldMeta`` default with ``None`` so the attribute behaves normally
    at runtime while preserving the metadata on ``cls._iverson_meta``.

    Every scalar declaration is an INDEPENDENT flag, mirroring the proto's
    ``PropertyDescriptor``. There is deliberately no ``kind`` discriminator:
    a single mutually-exclusive axis is what made declarations non-composable
    and produced the same bug four times. ``relation_kind`` is the one
    exception — relations serialize to ``RelationDescriptor``, a different
    message, so a field is either a scalar property or a relation.
    """
    key: bool = False
    search_key: bool = False
    search_key_order: int = 0
    large_field: bool = False
    embedding: bool = False
    chunk: bool = False
    chunk_max_tokens: int = 512
    chunk_overlap: int = 64
    chunk_contextual: bool = False
    metadata: bool = False
    tenant: bool = False
    summary: bool = False
    keywords: bool = False
    extract_hint: str = ""
    description: str = ""
    relation_kind: str | None = None
    related_type: str | None = None
```

- [ ] **Step 2: Add `iverson_field` and delete `_enrichment_kwargs`.**

Replace `_enrichment_kwargs` (`annotations.py:53-74`) with `iverson_field`. The blank-hint guard moves inside it, so it now covers every declaration path by construction rather than by four factories each remembering to call it.

The guard's message no longer names a calling factory: with one constructor there is no caller to name, and re-plumbing one would rebuild the per-factory threading this design removes. The existing tests match on `"extract"` (P12), which the new message still satisfies.

```python
def iverson_field(
    *,
    key: bool = False,
    search_key: bool = False,
    search_key_order: int = 0,
    large_field: bool = False,
    embedding: bool = False,
    chunk: bool = False,
    chunk_max_tokens: int = 512,
    chunk_overlap: int = 64,
    chunk_contextual: bool = False,
    metadata: bool = False,
    tenant: bool = False,
    summary: bool = False,
    keywords: bool = False,
    extract_hint: str = "",
    description: str = "",
) -> FieldMeta:
    """Declare a field with any combination of scalar declarations.

    This is the single constructor for scalar declarations; the named
    factories below are one-line presets over it. Use this directly whenever
    a field carries MORE THAN ONE declaration.

    Args:
        key: primary key field.
        search_key / search_key_order: StarRocks MV search/sort key and its
            0-based position in the composite key.
        large_field: excluded from materialized views.
        embedding: source for a whole-field vector embedding.
        chunk / chunk_max_tokens / chunk_overlap / chunk_contextual: source
            for chunk-level vector embeddings and its windowing.
        metadata: a property that describes or qualifies the entity rather
            than carrying its primary content.
        tenant: the field holding the row's tenant id. Exactly one per entity.
        summary / keywords: Ollama enrichment targets.
        extract_hint: Ollama extraction target, guided by this hint. ``""``
            means "not an extraction target"; a blank-but-non-empty hint is
            rejected.
        description: human-readable field description.
    """
    # The server treats an empty hint as "not an extraction target" and would
    # silently drop it, so a None or blank-but-non-empty hint is rejected here.
    # An empty string is the "not declared" default and must NOT raise.
    if extract_hint is None or (extract_hint != "" and not extract_hint.strip()):
        raise ValueError(
            "extract_hint must be non-blank when provided; the server treats "
            "an empty hint as \"not an extraction target\" and would silently "
            "drop it."
        )

    return FieldMeta(
        key=key,
        search_key=search_key,
        search_key_order=search_key_order,
        large_field=large_field,
        embedding=embedding,
        chunk=chunk,
        chunk_max_tokens=chunk_max_tokens,
        chunk_overlap=chunk_overlap,
        chunk_contextual=chunk_contextual,
        metadata=metadata,
        tenant=tenant,
        summary=summary,
        keywords=keywords,
        extract_hint=extract_hint,
        description=description,
    )
```

- [ ] **Step 3: Rewrite the eleven scalar factories as presets** (`annotations.py:44-257`).

Each keeps its current public parameters exactly (P5) and adds none. Docstrings shrink to one line each; the parameter documentation now lives on `iverson_field`.

```python
def iverson_key(description: str = "") -> FieldMeta:
    """Mark the primary key field of an entity."""
    return iverson_field(key=True, description=description)


def iverson_search_key(order: int = 0, description: str = "") -> FieldMeta:
    """Mark a field used as a search/sort key in the StarRocks MV."""
    return iverson_field(search_key=True, search_key_order=order, description=description)


def iverson_metadata(description: str = "") -> FieldMeta:
    """Mark a field as a metadata signal."""
    return iverson_field(metadata=True, description=description)


def iverson_description(description: str) -> FieldMeta:
    """Supply a human-readable description for an otherwise plain field."""
    return iverson_field(description=description)


def iverson_large_field(description: str = "") -> FieldMeta:
    """Mark a field as large (excluded from materialized views)."""
    return iverson_field(large_field=True, description=description)


def iverson_embedding(description: str = "") -> FieldMeta:
    """Mark a string field as a source for a whole-field vector embedding."""
    return iverson_field(embedding=True, description=description)


def iverson_chunk(
    max_tokens: int = 512,
    overlap: int = 64,
    description: str = "",
    contextual: bool = False,
) -> FieldMeta:
    """Mark a string field as a source for chunk-level vector embeddings."""
    return iverson_field(
        chunk=True,
        chunk_max_tokens=max_tokens,
        chunk_overlap=overlap,
        chunk_contextual=contextual,
        description=description,
    )


def iverson_summary(description: str = "") -> FieldMeta:
    """Mark a field as the target for an Ollama-driven summary."""
    return iverson_field(summary=True, description=description)


def iverson_keywords(description: str = "") -> FieldMeta:
    """Mark a field as the target for Ollama-driven keyword extraction."""
    return iverson_field(keywords=True, description=description)


def iverson_extracted(hint: str, description: str = "") -> FieldMeta:
    """Mark a field as an Ollama extraction target, guided by ``hint``.

    The hint is mandatory here, unlike the optional ``extract_hint`` kwarg on
    ``iverson_field`` where ``""`` means "not declared". Normalizing ``""`` to
    ``None`` routes an empty hint into the shared guard's rejection path.
    """
    return iverson_field(extract_hint=hint or None, description=description)


def iverson_tenant(description: str = "") -> FieldMeta:
    """Mark the field holding the row's tenant id. Exactly one per entity."""
    return iverson_field(tenant=True, description=description)
```

- [ ] **Step 4: Move the relation factories onto `relation_kind`** (`annotations.py:260-277`).

Signatures unchanged (P5); only the constructed field is renamed.

```python
def many_to_one(type_name: str) -> FieldMeta:
    """Declare a many-to-one relation field (FK on this entity)."""
    return FieldMeta(relation_kind="many_to_one", related_type=type_name)


def many_to_many(type_name: str) -> FieldMeta:
    """Declare a many-to-many relation field."""
    return FieldMeta(relation_kind="many_to_many", related_type=type_name)


def one_to_many(type_name: str) -> FieldMeta:
    """Declare a one-to-many relation field (FK on the related entity)."""
    return FieldMeta(relation_kind="one_to_many", related_type=type_name)


def one_to_one(type_name: str) -> FieldMeta:
    """Declare a one-to-one relation field."""
    return FieldMeta(relation_kind="one_to_one", related_type=type_name)
```

- [ ] **Step 5: Delete `_RELATION_KINDS`** (`annotations.py:282`). Its only two uses are in the dispatch replaced by the next step (P6).

- [ ] **Step 6: Convert the decorator's dispatch to independent `if`s** (`annotations.py:331-376`).

Replace the body of the `if isinstance(default, FieldMeta):` branch. The outer `else` that appends non-`FieldMeta` fields to `plain_fields` (`:375-376`) stays exactly as it is.

```python
        if isinstance(default, FieldMeta):
            meta: FieldMeta = default
            # Replace the FieldMeta sentinel with None so the attribute is usable
            setattr(cls, field_name, None)

            if meta.relation_kind:
                # Relations are the one exclusive axis: they serialize to
                # RelationDescriptor, not PropertyDescriptor. The dict keeps its
                # "kind" key because core.py:69 and core.py:188 read rel["kind"].
                relations.append({
                    "field": field_name,
                    "kind": meta.relation_kind,
                    "related_type": meta.related_type,
                })
                continue

            if meta.key:
                key_field = field_name
            if meta.search_key:
                search_keys.append((field_name, meta.search_key_order))
            if meta.large_field:
                large_fields.append(field_name)
            if meta.embedding:
                embedding_fields.append(field_name)
            if meta.chunk:
                chunk_fields.append(
                    (field_name, meta.chunk_max_tokens, meta.chunk_overlap, meta.chunk_contextual))
            if meta.metadata:
                metadata_fields.append(field_name)
            if meta.tenant:
                tenant_fields.append(field_name)
            if meta.summary:
                summary_fields.append(field_name)
            if meta.keywords:
                keywords_fields.append(field_name)
            if meta.extract_hint:
                extracted_fields[field_name] = meta.extract_hint
            if meta.description:
                descriptions[field_name] = meta.description

            # Exactly one append per scalar field. Previously this happened inside
            # each elif branch AND in the terminal else; under independent ifs that
            # would double-append any field carrying two flags, and core.py:147
            # iterates this list directly — a duplicate emits a duplicate
            # PropertyDescriptor on the wire.
            plain_fields.append(field_name)
        else:
            plain_fields.append(field_name)
```

`search_keys.sort(key=lambda t: t[1])` at `:378` is outside this branch and must remain untouched.

- [ ] **Step 7: Migrate the eleven cross-kwarg call sites** in `tests/test_schema_registrar.py`.

Add `iverson_field` to the import block at `:9-24`. Then rewrite:

```python
# RegDescribedArticle
region: str = iverson_field(search_key=True, search_key_order=0, metadata=True,
                            description="Publication region.")

# RegComposedTenantArticle
tenant_id: str = iverson_field(search_key=True, search_key_order=0, tenant=True)

# RegComposedEnrichmentArticle
summary_key: str = iverson_field(search_key=True, search_key_order=0, summary=True)
keywords_meta: str = iverson_field(metadata=True, keywords=True)
hint_field: str = iverson_field(large_field=True, extract_hint="Extract the price.")
described_summary: str = iverson_field(description="A summary field.", summary=True)
```

Then collapse the four blank-hint guard tests (`test_blank_extract_hint_kwarg_rejected_on_search_key` / `_on_metadata` / `_on_large_field` / `_on_description`) into one, and keep the `None` case. With the cross-cutting kwargs gone all four would be the identical call, and `iverson_extracted("")` / `iverson_extracted("   ")` already cover the mandatory-hint path separately (P13):

```python
    def test_blank_extract_hint_rejected(self):
        with pytest.raises(ValueError, match="extract"):
            iverson_field(extract_hint="   ")

    def test_none_extract_hint_rejected(self):
        with pytest.raises(ValueError, match="extract"):
            iverson_field(extract_hint=None)
```

- [ ] **Step 8: Run the suite.** All pre-existing tests must pass. The count drops from **158 to 155**: the four blank-hint kwarg tests collapse into one (−3). Task 2 restores it to 158 by adding three. A count other than 155 here means something else changed — investigate before committing.

```bash
cd Iverson.Clients/Python && python3 -m pytest tests/ -q
```

- [ ] **Step 9: Commit.**

```bash
git add Iverson.Clients/Python/iverson_client/annotations.py Iverson.Clients/Python/tests/test_schema_registrar.py
git commit -m "refactor(python-client): replace the kind axis with independent declaration flags"
```

---

### Task 2: Cover the newly-expressible combinations

**Files:**
- Test: `Iverson.Clients/Python/tests/test_schema_registrar.py`

**Interfaces:**
- Consumes: `iverson_field` from Task 1. Nothing in Task 1 depends on this task (P17).

- [ ] **Step 1: Add the fixture entity** beside the other `@iverson_entity` fixtures (`:34-107`).

`body` carries the `large_field`+`chunk` pair and `tenant_id` the `metadata`+`tenant` pair — the two combinations spec A18 confirms are legal at the server. Non-default chunk values make the windowing assertions meaningful.

```python
@iverson_entity
class RegComposedDeclarationArticle:
    id: str = iverson_key()
    title: str = None
    body: str = iverson_field(large_field=True, chunk=True,
                              chunk_max_tokens=256, chunk_overlap=32)
    tenant_id: str = iverson_field(metadata=True, tenant=True)
```

- [ ] **Step 2: Add the test class.** Matches the file's per-class `_request` convention (P15) and the both-halves assertion shape already used in `TestEnrichmentTargets` (P14).

```python
class TestDeclarationComposition:
    def _request(self, cls) -> mapping_pb.SchemaRequest:
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        SchemaRegistrar(stub, cls).register_all()
        return stub.RegisterSchema.call_args[0][0]

    def test_large_field_composes_with_chunk(self):
        props = {p.name: p for p in
                 self._request(RegComposedDeclarationArticle).root_type.properties}
        assert props["Body"].is_large_field is True
        assert props["Body"].is_chunk is True
        assert props["Body"].chunk_max_tokens == 256
        assert props["Body"].chunk_overlap == 32

    def test_metadata_composes_with_tenant(self):
        request = self._request(RegComposedDeclarationArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["TenantId"].is_metadata is True
        assert request.root_type.tenant_field == "TenantId"

    def test_multi_flag_field_emits_exactly_one_property(self):
        request = self._request(RegComposedDeclarationArticle)
        names = [p.name for p in request.root_type.properties]
        assert names.count("Body") == 1
        assert names.count("TenantId") == 1
```

The third test is the guard for Step 6's known hazard: a double-append to `plain_fields` would surface here as a duplicate property, since `core.py:147` iterates that list directly (spec A5).

- [ ] **Step 3: Run the suite.**

```bash
cd Iverson.Clients/Python && python3 -m pytest tests/ -q
```

- [ ] **Step 4: Commit.**

```bash
git add Iverson.Clients/Python/tests/test_schema_registrar.py
git commit -m "test(python-client): cover the newly-expressible declaration combinations"
```

## Known issues inherited from spec

**Removing `kind` gives up an accidental enforcement, and that regression is accepted.** Because `metadata` is currently a `kind`, Python cannot express `metadata` together with `embedding`/`chunk`/`large_field` — which is exactly what the server rejects at `SchemaBuilder.cs:94`, `:123-126`. Python has therefore been satisfying that rule by construction, for free, and will stop once `kind` is gone. The same applies to combinations the server rejects elsewhere, e.g. an enrichment target that is also a chunk or embedding (`SchemaRegistrationOrchestratorTests.cs:316`, and key/tenant-as-enrichment-target at `:301`).

Client-side validation is deliberately **not** added to replace it. A Python user writing `iverson_field(metadata=True, large_field=True)` will learn at registration time via a clear `InvalidArgument`, so the failure stays loud and immediate. This keeps the server's rules out of a fifth place and matches the other four clients, none of which validate this either. Ben was shown the alternative — a single guard inside `iverson_field` — and chose to accept the regression.

**This fixes Python only.** The other four clients already compose correctly; Go was fixed at `e4a77ff` and .NET has always used independent attributes.

**`FieldMeta` is a public export** (`iverson_client/__init__.py:15,36`), so removing `kind` is a public API break, not merely an internal one. Accepted: Ben authorized breaking changes for this work, and `FieldMeta` is a descriptor users receive from factories rather than construct themselves.
