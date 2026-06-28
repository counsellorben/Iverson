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
    kind: str                     # 'key' | 'search_key' | 'large_field' | relation kinds
    order: int = 0                # for search_key
    related_type: str | None = None  # for relation kinds


# ── Public factory helpers ─────────────────────────────────────────────────────

def iverson_key() -> FieldMeta:
    """Mark the primary key field of an entity."""
    return FieldMeta(kind="key")


def iverson_search_key(order: int = 0) -> FieldMeta:
    """Mark a field used as a search/sort key in StarRocks MV.

    Args:
        order: position in the composite search key (0-based).
    """
    return FieldMeta(kind="search_key", order=order)


def iverson_large_field() -> FieldMeta:
    """Mark a field as large (excluded from materialized views)."""
    return FieldMeta(kind="large_field")


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


def iverson_entity(cls: type) -> type:
    """Class decorator that collects ``FieldMeta`` annotations into metadata.

    After decoration:
    - ``cls._iverson_meta`` is a dict with keys:
        - ``type_name`` (str): simple class name
        - ``key_field`` (str | None): name of the ``iverson_key()`` field
        - ``search_keys`` (list[tuple[str, int]]): [(field_name, order), ...]
        - ``large_fields`` (list[str]): field names marked iverson_large_field
        - ``relations`` (list[dict]): each dict has 'field', 'kind', 'related_type'
        - ``fields`` (list[str]): all annotated field names (key + plain + large)
    - Every ``FieldMeta`` class attribute is replaced with ``None`` so instances
      can set it normally.
    """
    annotations: dict[str, Any] = {}
    # Walk MRO to gather inherited annotations (excluding object)
    for base in reversed(cls.__mro__):
        if base is object:
            continue
        annotations.update(getattr(base, "__annotations__", {}))

    key_field: str | None = None
    search_keys: list[tuple[str, int]] = []
    large_fields: list[str] = []
    relations: list[dict] = []
    plain_fields: list[str] = []

    for field_name, _type_hint in annotations.items():
        default = getattr(cls, field_name, None)
        if isinstance(default, FieldMeta):
            meta: FieldMeta = default
            # Replace the FieldMeta sentinel with None so the attribute is usable
            setattr(cls, field_name, None)

            if meta.kind == "key":
                key_field = field_name
                plain_fields.append(field_name)
            elif meta.kind == "search_key":
                search_keys.append((field_name, meta.order))
                plain_fields.append(field_name)
            elif meta.kind == "large_field":
                large_fields.append(field_name)
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
        "relations": relations,
        "fields": plain_fields,
    }

    return cls
