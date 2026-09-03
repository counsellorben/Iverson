"""Schema discovery under the end user's identity (spec §3.3) and planner-filter validation (§4.1).

Field names are the GetSchema names — the PascalCase wire spelling. That is the one canonical
spelling everywhere the model sees or emits a field name.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from typing import Callable, Iterable

from iverson_client import IversonClient
from iverson_client.generated import object_mapping_pb2 as mpb

log = logging.getLogger(__name__)


class TypeNotAccessible(Exception):
    """The requested type is not in the schema the end user may see (§3.4 row 1)."""


@dataclass(frozen=True)
class ValidFilter:
    field: str                  # canonical GetSchema spelling
    value: str | float | bool


@dataclass
class SchemaCache:
    """Per-user schema, fetched through a per-user IversonClient on cache miss (§3.3)."""
    ttl: timedelta
    client_factory: Callable[[str], IversonClient]   # end_user_token -> client bound to that user
    _entries: dict[str, tuple[datetime, list[mpb.SchemaType]]] = field(default_factory=dict)

    def get(self, user_key: str, end_user_token: str) -> list[mpb.SchemaType]:
        now = datetime.utcnow()
        hit = self._entries.get(user_key)
        if hit and now - hit[0] < self.ttl:
            return hit[1]
        with self.client_factory(end_user_token) as per_user:
            types = per_user.get_schema()
        self._entries[user_key] = (now, types)
        return types


def resolve_type(types: Iterable[mpb.SchemaType], type_name: str) -> mpb.SchemaType:
    for t in types:
        if t.name.lower() == type_name.lower():
            return t
    raise TypeNotAccessible(type_name)


def metadata_fields(schema_type: mpb.SchemaType) -> list[mpb.SchemaField]:
    return [f for f in schema_type.fields if f.is_metadata]


def render_schema(schema_type: mpb.SchemaType) -> str:
    """The compact rendering the planner reads: type, description, filterable fields only."""
    lines = [f"Type: {schema_type.name}", f"Description: {schema_type.description}",
             "Filterable fields (equality only):"]
    for f in metadata_fields(schema_type):
        lines.append(f"  - {f.name} ({mpb.ClrType.Name(f.clr_type)}): {f.description}")
    return "\n".join(lines)


_NUMERIC = {mpb.CLR_INT32, mpb.CLR_INT64, mpb.CLR_DOUBLE, mpb.CLR_FLOAT}
_TEXTUAL = {mpb.CLR_STRING, mpb.CLR_GUID, mpb.CLR_DATETIME}


def validate_filters(filters: Iterable[tuple[str, object]], schema_type: mpb.SchemaType,
                     trace_id: str) -> list[ValidFilter]:
    """Local validation before anything reaches the wire (§4.1). Every drop is logged."""
    by_lower = {f.name.lower(): f for f in schema_type.fields}
    valid: list[ValidFilter] = []
    for name, value in filters:
        f = by_lower.get(str(name).lower())
        if f is None:
            log.info("[plan] trace=%s dropped filter %r: unknown field", trace_id, name)
            continue
        if not f.is_metadata:
            log.info("[plan] trace=%s dropped filter %r: not a metadata field", trace_id, f.name)
            continue
        try:
            if f.clr_type in _NUMERIC:
                coerced: str | float | bool = float(value)
            elif f.clr_type == mpb.CLR_BOOL:
                coerced = value if isinstance(value, bool) else str(value).lower() == "true"
            elif f.clr_type in _TEXTUAL:
                coerced = str(value)
            else:
                raise ValueError(f"unsupported CLR type {mpb.ClrType.Name(f.clr_type)}")
        except (TypeError, ValueError) as exc:
            log.info("[plan] trace=%s dropped filter %r=%r: %s", trace_id, f.name, value, exc)
            continue
        valid.append(ValidFilter(f.name, coerced))
    return valid
