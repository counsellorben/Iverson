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
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass
class FieldMeta:
    """Descriptor object attached as a class attribute default.

    ``@iverson_entity`` inspects ``__annotations__`` and replaces every
    ``FieldMeta`` default with ``None`` so the attribute behaves normally
    at runtime while preserving the metadata on ``cls._iverson_meta``.
    """
    kind: str                     # 'key' | 'search_key' | 'metadata' | 'large_field' | 'embedding' | 'chunk' | relation kinds
    order: int = 0                # for search_key
    related_type: str | None = None  # for relation kinds
    max_tokens: int = 512         # for chunk
    overlap: int = 64             # for chunk
    metadata: bool = False        # metadata signal marker
    description: str = ""         # human-readable field description
    contextual: bool = False      # for chunk: include surrounding context
    is_summary_target: bool = False    # marker for summary enrichment
    is_keywords_target: bool = False   # marker for keywords enrichment
    extract_hint: str = ""        # for extraction enrichment; mandatory when kind == 'extracted'
    tenant: bool = False          # marks the field holding the row's tenant id


# ── Public factory helpers ─────────────────────────────────────────────────────

def iverson_key(description: str = "") -> FieldMeta:
    """Mark the primary key field of an entity.

    Args:
        description: human-readable description of the field.
    """
    return FieldMeta(kind="key", description=description)


def iverson_search_key(
    order: int = 0,
    metadata: bool = False,
    tenant: bool = False,
    description: str = "",
) -> FieldMeta:
    """Mark a field used as a search/sort key in StarRocks MV.

    Args:
        order: position in the composite search key (0-based).
        metadata: also mark the field as a metadata signal.
        tenant: also mark the field as the row's tenant id field.
        description: human-readable description of the field.
    """
    return FieldMeta(
        kind="search_key",
        order=order,
        metadata=metadata,
        tenant=tenant,
        description=description,
    )


def iverson_metadata(description: str = "") -> FieldMeta:
    """Mark a field as a metadata signal — a property that describes or
    qualifies the entity rather than carrying its primary content.

    Args:
        description: human-readable description of the field.
    """
    return FieldMeta(kind="metadata", metadata=True, description=description)


def iverson_description(description: str) -> FieldMeta:
    """Supply a human-readable description for an otherwise plain field.

    Args:
        description: the description text.
    """
    return FieldMeta(kind="plain", description=description)


def iverson_large_field(description: str = "") -> FieldMeta:
    """Mark a field as large (excluded from materialized views).

    Args:
        description: human-readable description of the field.
    """
    return FieldMeta(kind="large_field", description=description)


def iverson_embedding(description: str = "") -> FieldMeta:
    """Mark a string field as a source for a whole-field vector embedding.

    Args:
        description: human-readable description of the field.
    """
    return FieldMeta(kind="embedding", description=description)


def iverson_chunk(
    max_tokens: int = 512,
    overlap: int = 64,
    description: str = "",
    contextual: bool = False,
) -> FieldMeta:
    """Mark a string field as a source for chunk-level vector embeddings.

    Args:
        max_tokens: approximate window size in tokens (1 token ~ 4 chars).
        overlap: tokens shared between adjacent windows.
        description: human-readable description of the field.
        contextual: whether chunk embeddings should include surrounding context.
    """
    return FieldMeta(
        kind="chunk",
        max_tokens=max_tokens,
        overlap=overlap,
        description=description,
        contextual=contextual,
    )


def iverson_summary(description: str = "") -> FieldMeta:
    """Mark a field as the target for an Ollama-driven summary during ingest
    enrichment.

    Args:
        description: human-readable description of the field.
    """
    return FieldMeta(kind="summary", is_summary_target=True, description=description)


def iverson_keywords(description: str = "") -> FieldMeta:
    """Mark a field as the target for Ollama-driven keyword extraction during
    ingest enrichment.

    Args:
        description: human-readable description of the field.
    """
    return FieldMeta(kind="keywords", is_keywords_target=True, description=description)


def iverson_extracted(hint: str, description: str = "") -> FieldMeta:
    """Mark a field as the target for an Ollama-driven extraction during
    ingest enrichment, guided by ``hint``.

    The hint is mandatory: the server only treats a property as an
    extraction target when a non-empty hint is present (``SchemaBuilder.cs``
    only creates the Extracted target when the hint is non-empty), so an
    empty or blank hint would be silently dropped server-side. This factory
    rejects that case up front.

    Args:
        hint: the extraction hint guiding the Ollama prompt. Required;
            must not be blank.
        description: human-readable description of the field.
    """
    if hint is None or not hint.strip():
        raise ValueError(
            "iverson_extracted() requires a non-blank extraction hint; the "
            "server treats an empty hint as \"not an extraction target\" and "
            "would silently drop it."
        )
    return FieldMeta(kind="extracted", extract_hint=hint, description=description)


def iverson_tenant(description: str = "") -> FieldMeta:
    """Mark the field holding the row's tenant id.

    The server requires every schema to declare a tenant boundary and rejects
    registration without one, so exactly one field per entity must carry this.
    """
    return FieldMeta(kind="", tenant=True, description=description)


def many_to_one(type_name: str) -> FieldMeta:
    """Declare a many-to-one relation field (FK on this entity)."""
    return FieldMeta(kind="many_to_one", related_type=type_name)


def many_to_many(type_name: str) -> FieldMeta:
    """Declare a many-to-many relation field."""
    return FieldMeta(kind="many_to_many", related_type=type_name)


def one_to_many(type_name: str) -> FieldMeta:
    """Declare a one-to-many relation field (FK on the related entity)."""
    return FieldMeta(kind="one_to_many", related_type=type_name)


def one_to_one(type_name: str) -> FieldMeta:
    """Declare a one-to-one relation field."""
    return FieldMeta(kind="one_to_one", related_type=type_name)


# ── @iverson_entity decorator ──────────────────────────────────────────────────

_RELATION_KINDS = {"many_to_one", "many_to_many", "one_to_many", "one_to_one"}


def iverson_entity(cls: type | None = None, *, description: str = ""):
    """Class decorator that collects ``FieldMeta`` annotations into metadata.

    Usable bare (``@iverson_entity``) or called
    (``@iverson_entity(description="...")`` to describe the type itself).

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
    - Every ``FieldMeta`` class attribute is replaced with ``None`` so instances
      can set it normally.
    """
    if cls is None:
        def _decorate(inner_cls: type) -> type:
            return iverson_entity(inner_cls, description=description)
        return _decorate

    annotations: dict[str, Any] = {}
    # Walk MRO to gather inherited annotations (excluding object)
    for base in reversed(cls.__mro__):
        if base is object:
            continue
        annotations.update(getattr(base, "__annotations__", {}))

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
    tenant_fields: list[str] = []

    for field_name, _type_hint in annotations.items():
        default = getattr(cls, field_name, None)
        if isinstance(default, FieldMeta):
            meta: FieldMeta = default
            # Replace the FieldMeta sentinel with None so the attribute is usable
            setattr(cls, field_name, None)

            if meta.kind not in _RELATION_KINDS:
                if meta.metadata:
                    metadata_fields.append(field_name)
                if meta.description:
                    descriptions[field_name] = meta.description
                if meta.tenant:
                    tenant_fields.append(field_name)

            if meta.kind == "key":
                key_field = field_name
                plain_fields.append(field_name)
            elif meta.kind == "search_key":
                search_keys.append((field_name, meta.order))
                plain_fields.append(field_name)
            elif meta.kind == "large_field":
                large_fields.append(field_name)
                plain_fields.append(field_name)
            elif meta.kind == "embedding":
                embedding_fields.append(field_name)
                plain_fields.append(field_name)
            elif meta.kind == "chunk":
                chunk_fields.append((field_name, meta.max_tokens, meta.overlap, meta.contextual))
                plain_fields.append(field_name)
            elif meta.kind == "summary":
                summary_fields.append(field_name)
                plain_fields.append(field_name)
            elif meta.kind == "keywords":
                keywords_fields.append(field_name)
                plain_fields.append(field_name)
            elif meta.kind == "extracted":
                extracted_fields[field_name] = meta.extract_hint
                plain_fields.append(field_name)
            elif meta.kind in _RELATION_KINDS:
                relations.append({
                    "field": field_name,
                    "kind": meta.kind,
                    "related_type": meta.related_type,
                })
            else:
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
        "summary_fields": summary_fields,
        "keywords_fields": keywords_fields,
        "extracted_fields": extracted_fields,
        "tenant_fields": tenant_fields,
    }

    return cls
