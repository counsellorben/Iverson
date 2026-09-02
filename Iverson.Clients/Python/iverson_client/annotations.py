"""
Decorator-based annotation system for Iverson entities.

Usage:
    @iverson_entity
    class Article:
        id: str = iverson_key()
        title: str = None
        body: str = iverson_large_field()
        category: str = iverson_search_key(order=0)
        published_at: datetime = iverson_search_key(order=1)
        author_id: str = many_to_one('Author')
        transcript: str = iverson_field(large_field=True, chunk=True)
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(kw_only=True)
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
    summary: bool = False
    keywords: bool = False
    extract_hint: str | None = ""
    description: str = ""
    relation_kind: str | None = None
    related_type: str | None = None


# ── Public factory helpers ─────────────────────────────────────────────────────

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
    summary: bool = False,
    keywords: bool = False,
    extract_hint: str | None = "",
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
        summary=summary,
        keywords=keywords,
        extract_hint=extract_hint,
        description=description,
    )


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


# ── Entity registry ─────────────────────────────────────────────────────────────

#: Maps ``cls.__name__`` (the same string ``_iverson_meta["type_name"]`` carries,
#: and the same string a relation's ``related_type`` holds) to the decorated
#: class itself. Populated at import time by ``iverson_entity``, before any read
#: can occur, so the read-path hydration pass can resolve a relation's
#: ``related_type`` back to a concrete class.
ENTITY_REGISTRY: dict[str, type] = {}


# ── @iverson_entity decorator ──────────────────────────────────────────────────


def iverson_entity(cls: type | None = None, *, description: str = "", embedding_model: str | None = None):
    """Class decorator that collects ``FieldMeta`` annotations into metadata.

    Usable bare (``@iverson_entity``) or called
    (``@iverson_entity(description="...")`` to describe the type itself).

    Args:
        description: human-readable description of the type itself.
        embedding_model: the Ollama model this type's embedding/chunk properties are
            generated with. One model per TYPE, never per property — every
            ``iverson_embedding()``/``iverson_chunk()`` field on this class is stamped with
            the same value. Three cases: ``None`` (the default, i.e. the argument was not
            supplied) means "not declared HERE" — if a decorated base class in this class's MRO
            declares a non-empty model, the nearest such declaration is inherited. An explicit
            ``""`` means "declared HERE as opted OUT" — no MRO walk happens, and this type's
            properties are stamped with ``""`` even if a base declares a model, matching how the
            other Iverson clients treat an explicit empty value. A non-empty value is used as-is
            and overrides any inherited one. If resolution (supplied or inherited) ends in ``""``
            or ``None`` with no declaring base, the server resolves the deployment's configured
            default, exactly as it does for a client that predates this parameter — so an
            un-updated caller keeps working with no server-side special-casing. Only
            ``embedding_model`` inherits this way from a decorated base — its ``FieldMeta``
            sentinels (including ``iverson_key()``) are replaced with ``None`` on the base (see
            "After decoration" below) and do NOT carry into subclasses, so the declaring base
            must be field-less, or the child must redeclare every field it needs.

    After decoration:
    - ``cls._iverson_meta`` is a dict with keys:
        - ``type_name`` (str): simple class name
        - ``key_field`` (str | None): name of the ``iverson_key()`` field
        - ``search_keys`` (list[tuple[str, int]]): [(field_name, order), ...]
        - ``large_fields`` (list[str]): field names marked iverson_large_field
        - ``relations`` (list[dict]): each dict has 'field', 'kind', 'related_type'
        - ``fields`` (list[str]): all annotated field names (key + plain + large)
        - ``metadata_fields`` (list[str]): field names marked as metadata signals
        - ``descriptions`` (dict[str, str]): field name → description text
        - ``description`` (str): description of the type itself
        - ``embedding_model`` (str): the declared embedding model, or ``""`` if undeclared
    - Every ``FieldMeta`` class attribute is replaced with ``None`` so instances
      can set it normally.
    """
    if cls is None:
        def _decorate(inner_cls: type) -> type:
            return iverson_entity(
                inner_cls, description=description, embedding_model=embedding_model,
            )
        return _decorate

    annotations: dict[str, Any] = {}
    # Walk MRO to gather inherited annotations (excluding object)
    for base in reversed(cls.__mro__):
        if base is object:
            continue
        annotations.update(getattr(base, "__annotations__", {}))

    if embedding_model is None:
        # None (not supplied) means "inherit from the MRO"; an explicit "" is a deliberate
        # opt-out and must NOT walk the MRO — see the docstring's three cases. Unlike the
        # annotation gather above (which walks farthest-first to accumulate every inherited
        # field), this walks NEAREST-first — cls.__mro__[1:] is parent, then grandparent, ... —
        # so a middle class's own declaration wins over an ancestor's. Skip object and any
        # undecorated base (neither carries `_iverson_meta`). If no declaring base is found,
        # resolve to "" (the "undeclared" sentinel `_iverson_meta["embedding_model"]` carries).
        embedding_model = ""
        for base in cls.__mro__[1:]:
            base_meta = getattr(base, "_iverson_meta", None)
            if base_meta and base_meta.get("embedding_model"):
                embedding_model = base_meta["embedding_model"]
                break

    key_field: str | None = None
    search_keys: list[tuple[str, int]] = []
    large_fields: list[str] = []
    embedding_fields: list[str] = []
    chunk_fields: list[tuple[str, int, int, bool]] = []
    relations: list[dict] = []
    plain_fields: list[str] = []
    metadata_fields: list[str] = []
    descriptions: dict[str, str] = {}
    summary_fields: list[str] = []
    keywords_fields: list[str] = []
    extracted_fields: dict[str, str] = {}

    for field_name, _type_hint in annotations.items():
        default = getattr(cls, field_name, None)
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

    search_keys.sort(key=lambda t: t[1])

    cls._iverson_meta = {
        "type_name": cls.__name__,
        "key_field": key_field,
        "search_keys": search_keys,
        "large_fields": large_fields,
        "embedding_fields": embedding_fields,
        "chunk_fields": chunk_fields,
        "relations": relations,
        "fields": plain_fields,
        "metadata_fields": metadata_fields,
        "descriptions": descriptions,
        "description": description,
        "embedding_model": embedding_model,
        "summary_fields": summary_fields,
        "keywords_fields": keywords_fields,
        "extracted_fields": extracted_fields,
    }

    ENTITY_REGISTRY[cls.__name__] = cls

    return cls
