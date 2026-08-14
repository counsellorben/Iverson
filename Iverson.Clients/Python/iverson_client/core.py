"""
Core client classes: IversonClient, SchemaRegistrar, EntityCoordinator.
"""
from __future__ import annotations

import copy
import re
import uuid
from dataclasses import dataclass
from typing import Generic, List, Mapping, Optional, TypeVar, get_args, get_origin, get_type_hints

import grpc
from google.protobuf import struct_pb2

from iverson_client.auth import (
    ACTING_USER_METADATA_KEY,
    IversonClientCredentials,
    _CachedTokenProvider,
    _BearerTokenAuthPlugin,
)
from iverson_client.generated import (
    object_mapping_pb2 as mapping_pb,
    object_mapping_pb2_grpc as mapping_grpc,
    object_persistence_pb2 as persist_pb,
    object_persistence_pb2_grpc as persist_grpc,
    object_retrieval_pb2 as retrieval_pb,
    object_retrieval_pb2_grpc as retrieval_grpc,
    object_search_pb2 as search_pb,
    object_search_pb2_grpc as search_grpc,
)

T = TypeVar("T")

# ── Type mapping helpers ───────────────────────────────────────────────────────

_PY_TO_CLR: dict[str, int] = {
    "str":      mapping_pb.CLR_STRING,
    "uuid":     mapping_pb.CLR_GUID,
    "UUID":     mapping_pb.CLR_GUID,
    "int":      mapping_pb.CLR_INT32,
    "float":    mapping_pb.CLR_FLOAT,
    "bool":     mapping_pb.CLR_BOOL,
    "datetime": mapping_pb.CLR_DATETIME,
    "bytes":    mapping_pb.CLR_BYTES,
}

_RELATION_KIND_MAP: dict[str, int] = {
    "one_to_one":   mapping_pb.ONE_TO_ONE,
    "one_to_many":  mapping_pb.ONE_TO_MANY,
    "many_to_one":  mapping_pb.MANY_TO_ONE,
    "many_to_many": mapping_pb.MANY_TO_MANY,
}


def _python_type_to_clr(type_hint: str | type | None, prop_name: str = "") -> tuple[int, bool]:
    """Map a Python type annotation to (ClrType enum value, is_array).

    An array whose element is itself a generic (e.g. ``list[list[str]]``) or is not a supported
    scalar is REJECTED rather than silently falling back to ``str``: the server would register a
    1-D ``TEXT[]`` column against a payload that is a nested/complex JSON array, and
    ``json_populate_record`` fails on the first insert.
    """
    if type_hint is None:
        return mapping_pb.CLR_STRING, False
    # bytes is a scalar — check before the array unwrap.
    if type_hint is bytes:
        return mapping_pb.CLR_BYTES, False
    if get_origin(type_hint) is list:
        args = get_args(type_hint)
        element = args[0] if args else None
        if element is bytes:
            return mapping_pb.CLR_BYTES, True
        where = f" on property '{prop_name}'" if prop_name else ""
        if element is None:
            raise ValueError(
                f"Array type {type_hint!r}{where} must declare an element type, "
                "e.g. list[str]."
            )
        if get_origin(element) is not None:
            raise ValueError(
                f"Nested array element type {element!r}{where} is not supported; "
                "declare a list of a supported scalar type."
            )
        name = getattr(element, "__name__", None)
        if name is None or name not in _PY_TO_CLR:
            raise ValueError(
                f"Array element type {element!r}{where} is not a supported scalar; "
                f"supported element types are: {', '.join(sorted(_PY_TO_CLR))}."
            )
        return _PY_TO_CLR[name], True
    name = type_hint if isinstance(type_hint, str) else getattr(type_hint, "__name__", str(type_hint))
    return _PY_TO_CLR.get(name, mapping_pb.CLR_STRING), False


def _to_pascal_case(snake: str) -> str:
    """Convert snake_case field name to PascalCase column name (e.g. author_id → AuthorId)."""
    return "".join(part.capitalize() for part in snake.split("_"))


def _relation_property_name(relation: dict) -> str:
    """Derive the navigation property name from the relation's member name.

    For many_to_one / one_to_one the member itself is the foreign key
    (author_id → AuthorId), so strip the trailing "Id" to get the navigation
    property name (Author). Other kinds use the PascalCase member name as-is.
    """
    pascal = _to_pascal_case(relation["field"])
    if relation["kind"] in ("many_to_one", "one_to_one"):
        if len(pascal) > 2 and pascal.endswith("Id"):
            return pascal[:-2]
    return pascal


def _infer_fk(relation: dict, this_type_name: str) -> str:
    """Infer the FK column name from relation metadata."""
    kind = relation["kind"]
    related = relation.get("related_type") or ""
    if kind in ("many_to_one", "one_to_one"):
        return f"{related}Id"
    if kind == "many_to_many":
        return f"{related}Ids"
    if kind == "one_to_many":
        return f"{this_type_name}Id"
    return ""


# ── SchemaRegistrar ────────────────────────────────────────────────────────────

class SchemaRegistrar:
    """Reflects on ``@iverson_entity`` classes and registers their schemas
    with the server via ``ObjectMappingService.RegisterSchema``.

    Args:
        mapping_stub: a ``ObjectMappingServiceStub`` (real or mock).
        entity_classes: one or more ``@iverson_entity``-decorated classes.
    """

    def __init__(
        self,
        mapping_stub: mapping_grpc.ObjectMappingServiceStub,
        *entity_classes: type,
    ) -> None:
        self._stub = mapping_stub
        self._classes = list(entity_classes)

    def register_all(
        self,
        trace_id: str = "",
        authorization_by_type_name: Optional[Mapping[str, mapping_pb.AuthorizationRules]] = None,
    ) -> None:
        """Synchronously register all entity schemas."""
        for cls in self._classes:
            request = self._build_request(cls, trace_id, authorization_by_type_name)
            response = self._stub.RegisterSchema(request)
            if not response.success:
                raise RuntimeError(
                    f"Schema registration failed for {cls.__name__}: {response.error}"
                )

    async def register_all_async(
        self,
        trace_id: str = "",
        authorization_by_type_name: Optional[Mapping[str, mapping_pb.AuthorizationRules]] = None,
    ) -> None:
        """Asynchronously register all entity schemas (requires async channel)."""
        for cls in self._classes:
            request = self._build_request(cls, trace_id, authorization_by_type_name)
            response = await self._stub.RegisterSchema(request)
            if not response.success:
                raise RuntimeError(
                    f"Schema registration failed for {cls.__name__}: {response.error}"
                )

    def _build_request(
        self,
        cls: type,
        trace_id: str,
        authorization_by_type_name: Optional[Mapping[str, mapping_pb.AuthorizationRules]] = None,
    ) -> mapping_pb.SchemaRequest:
        meta = getattr(cls, "_iverson_meta", None)
        if meta is None:
            raise ValueError(
                f"{cls.__name__} is not decorated with @iverson_entity"
            )

        annotations = get_type_hints(cls)

        type_name = meta["type_name"]
        key_field = meta["key_field"]
        search_keys_by_field = {f: o for f, o in meta["search_keys"]}
        large_fields_set = set(meta["large_fields"])
        embedding_fields_set = set(meta["embedding_fields"])
        chunk_fields_by_name = {f: (mt, ov, ctx) for f, mt, ov, ctx in meta["chunk_fields"]}
        relation_fields = {r["field"] for r in meta["relations"]}
        metadata_fields_set = set(meta.get("metadata_fields", []))
        descriptions_by_field = meta.get("descriptions", {})
        summary_fields_set = set(meta.get("summary_fields", []))
        keywords_fields_set = set(meta.get("keywords_fields", []))
        extracted_fields_by_name = meta.get("extracted_fields", {})
        self._validate_key_declarations(
            type_name,
            key_field,
            search_keys_by_field,
            large_fields_set,
            embedding_fields_set,
            chunk_fields_by_name,
            metadata_fields_set,
            summary_fields_set,
            keywords_fields_set,
            extracted_fields_by_name,
        )
        for rel in meta["relations"]:
            if rel["kind"] in ("many_to_one", "one_to_one"):
                actual = _to_pascal_case(rel["field"])
                expected = f'{rel["related_type"]}Id'
                if actual != expected:
                    raise ValueError(
                        f'{type_name}.{rel["field"]} declares a {rel["kind"]} relation to '
                        f'{rel["related_type"]} but is named {actual!r} on the wire; '
                        f"a {rel['kind']} foreign-key field must be named {expected!r} "
                        f"(rename the member to match)."
                    )
        tenant_field = self._resolve_tenant_field(type_name, meta.get("tenant_fields", []))

        properties: list[mapping_pb.PropertyDescriptor] = []
        for field_name in meta["fields"]:
            if field_name in relation_fields:
                continue
            type_hint = annotations.get(field_name)
            clr_type, is_array = _python_type_to_clr(type_hint, field_name)
            is_chunk = field_name in chunk_fields_by_name
            chunk_max_tokens, chunk_overlap, chunk_contextual = chunk_fields_by_name.get(
                field_name, (0, 0, False)
            )
            prop = mapping_pb.PropertyDescriptor(
                name=_to_pascal_case(field_name),
                clr_type=clr_type,
                is_key=(field_name == key_field),
                is_nullable=(field_name != key_field),
                is_array=is_array,
                is_search_key=(field_name in search_keys_by_field),
                search_key_order=search_keys_by_field.get(field_name, 0),
                is_metadata=(field_name in metadata_fields_set),
                description=descriptions_by_field.get(field_name, ""),
                is_large_field=(field_name in large_fields_set),
                is_embedding=(field_name in embedding_fields_set),
                vector_dim=0,
                model_id="",
                is_chunk=is_chunk,
                chunk_max_tokens=chunk_max_tokens,
                chunk_overlap=chunk_overlap,
                chunk_model_id="",
                chunk_vector_dim=0,
                chunk_contextual=chunk_contextual,
                is_summary_target=(field_name in summary_fields_set),
                is_keywords_target=(field_name in keywords_fields_set),
                extract_hint=extracted_fields_by_name.get(field_name, ""),
            )
            properties.append(prop)

        for rel in meta["relations"]:
            if rel["kind"] == "one_to_many":
                continue
            fk_name = _infer_fk(rel, type_name)
            properties.append(
                mapping_pb.PropertyDescriptor(
                    name=fk_name,
                    clr_type=mapping_pb.CLR_GUID,
                    is_key=False,
                    is_nullable=True,
                    is_array=(rel["kind"] == "many_to_many"),
                )
            )

        relations: list[mapping_pb.RelationDescriptor] = []
        for rel in meta["relations"]:
            fk = _infer_fk(rel, type_name)
            relations.append(
                mapping_pb.RelationDescriptor(
                    property_name=_relation_property_name(rel),
                    kind=_RELATION_KIND_MAP.get(rel["kind"], mapping_pb.MANY_TO_ONE),
                    related_type=rel.get("related_type") or "",
                    foreign_key=fk,
                )
            )

        rules = (authorization_by_type_name or {}).get(type_name)
        type_descriptor = mapping_pb.TypeDescriptor(
            type_name=type_name,
            properties=properties,
            relations=relations,
            description=meta.get("description", ""),
            tenant_field=_to_pascal_case(tenant_field),
            **({"authorization": rules} if rules is not None else {}),
        )
        return mapping_pb.SchemaRequest(root_type=type_descriptor, trace_id=trace_id)

    @staticmethod
    def _validate_key_declarations(
        type_name: str,
        key_field: str | None,
        search_keys_by_field: dict,
        large_fields_set: set,
        embedding_fields_set: set,
        chunk_fields_by_name: dict,
        metadata_fields_set: set,
        summary_fields_set: set,
        keywords_fields_set: set,
        extracted_fields_by_name: dict,
    ) -> None:
        """Reject declarations the server silently discards on a key field.

        The server builds every per-property declaration from non-key properties
        only, so anything but a description on the key is accepted and dropped.
        """
        if key_field is None:
            return

        rejected: list[str] = []
        if key_field in search_keys_by_field:
            rejected.append("iverson_search_key()")
        if key_field in large_fields_set:
            rejected.append("iverson_large_field()")
        if key_field in embedding_fields_set:
            rejected.append("iverson_embedding()")
        if key_field in chunk_fields_by_name:
            rejected.append("iverson_chunk()")
        if key_field in metadata_fields_set:
            rejected.append("iverson_metadata()")
        if key_field in summary_fields_set:
            rejected.append("iverson_summary()")
        if key_field in keywords_fields_set:
            rejected.append("iverson_keywords()")
        if key_field in extracted_fields_by_name:
            rejected.append("iverson_extracted()")

        if not rejected:
            return

        raise ValueError(
            f"{type_name}.{key_field} is the primary key and also declares "
            f"{', '.join(rejected)}; the server builds every per-property declaration "
            "from non-key properties only, so this would be accepted and silently "
            "discarded. Remove it from the key field. (Only a description is valid "
            "on a key.)"
        )

    @staticmethod
    def _resolve_tenant_field(type_name: str, tenant_fields: list[str]) -> str:
        if len(tenant_fields) == 0:
            raise ValueError(
                f"{type_name} has no field marked with iverson_tenant(); the server "
                "requires every schema to declare a tenant boundary and will reject "
                "registration without one."
            )
        if len(tenant_fields) > 1:
            raise ValueError(
                f"{type_name} has multiple fields marked with iverson_tenant() "
                f"({', '.join(tenant_fields)}); exactly one field must carry the "
                "tenant marker."
            )
        return tenant_fields[0]


# ── StructConverter ────────────────────────────────────────────────────────────

def _append_list_value(list_value: struct_pb2.ListValue, item: object) -> None:
    """Append a scalar element (recursively converted) to a protobuf ListValue."""
    v = list_value.values.add()
    if isinstance(item, bool):
        v.bool_value = item
    elif isinstance(item, int):
        v.number_value = float(item)
    elif isinstance(item, float):
        v.number_value = item
    elif isinstance(item, str):
        v.string_value = item
    elif isinstance(item, uuid.UUID):
        v.string_value = str(item)
    else:
        v.string_value = str(item)


def _entity_to_struct(entity: object) -> struct_pb2.Struct:
    """Convert an @iverson_entity instance to a google.protobuf.Struct."""
    meta = getattr(type(entity), "_iverson_meta", None)
    if meta is None:
        raise ValueError(f"{type(entity).__name__} is not an @iverson_entity")

    s = struct_pb2.Struct()
    annotations = {}
    for base in reversed(type(entity).__mro__):
        if base is object:
            continue
        annotations.update(getattr(base, "__annotations__", {}))

    type_name = meta["type_name"]
    relation_by_field = {r["field"]: r for r in meta["relations"]}

    for field_name in annotations:
        value = getattr(entity, field_name, None)
        if value is None:
            continue

        relation = relation_by_field.get(field_name)
        if relation is not None:
            if relation["kind"] == "one_to_many":
                continue
            pascal = _infer_fk(relation, type_name)
        else:
            pascal = _to_pascal_case(field_name)

        if isinstance(value, bool):
            s.fields[pascal].bool_value = value
        elif isinstance(value, int):
            s.fields[pascal].number_value = float(value)
        elif isinstance(value, float):
            s.fields[pascal].number_value = value
        elif isinstance(value, str):
            s.fields[pascal].string_value = value
        elif isinstance(value, uuid.UUID):
            s.fields[pascal].string_value = str(value)
        elif isinstance(value, (list, tuple)):
            list_value = s.fields[pascal].list_value
            for item in value:
                _append_list_value(list_value, item)
        else:
            s.fields[pascal].string_value = str(value)
    return s


def _list_value_to_list(list_value: struct_pb2.ListValue) -> list:
    """Convert a protobuf ListValue back to a plain Python list of scalars."""
    result = []
    for v in list_value.values:
        kind = v.WhichOneof("kind")
        if kind == "string_value":
            result.append(v.string_value)
        elif kind == "number_value":
            result.append(v.number_value)
        elif kind == "bool_value":
            result.append(v.bool_value)
        else:
            result.append(None)
    return result


def _struct_to_dict(s: struct_pb2.Struct) -> dict:
    return dict(s)


# ── SearchResult ────────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class SearchResult(Generic[T]):
    """An entity paired with its relevance score from a search-family RPC.

    Mirrors the DotNet ``SearchResult<T>`` record, Go ``SearchResult[T]`` struct,
    and Java ``SearchResult<T>`` record.
    """

    entity: T
    score: float


# ── EntityCoordinator ──────────────────────────────────────────────────────────

class EntityCoordinator(Generic[T]):
    """High-level coordinator for a single entity type.

    Wraps ObjectMappingService for full CRUD with relation traversal
    and ObjectPersistenceService for lightweight writes.

    Args:
        entity_class: the ``@iverson_entity``-decorated class.
        channel: an open ``grpc.Channel``.
    """

    def __init__(
        self,
        entity_class: type,
        channel: grpc.Channel,
        acting_user_token: str | None = None,
    ) -> None:
        meta = getattr(entity_class, "_iverson_meta", None)
        if meta is None:
            raise ValueError(
                f"{entity_class.__name__} is not decorated with @iverson_entity"
            )
        self._cls = entity_class
        self._type_name: str = meta["type_name"]
        self._key_field: Optional[str] = meta.get("key_field")
        self._mapping = mapping_grpc.ObjectMappingServiceStub(channel)
        self._persistence = persist_grpc.ObjectPersistenceServiceStub(channel)
        self._retrieval = retrieval_grpc.ObjectRetrievalServiceStub(channel)
        self._search = search_grpc.ObjectSearchServiceStub(channel)
        self._acting_user_token = acting_user_token

    def with_acting_user(self, token: str) -> "EntityCoordinator[T]":
        """Return a coordinator bound to ``token``, leaving this one untouched."""
        bound = copy.copy(self)
        bound._acting_user_token = token
        return bound

    def _acting_user_metadata(self) -> tuple[tuple[str, str], ...]:
        # Use `is None`, not a falsy check: an empty-string token is a caller
        # error that must reach the server and be rejected loudly, not be
        # silently downgraded to an unauthenticated call.
        if self._acting_user_token is None:
            return ()
        return ((ACTING_USER_METADATA_KEY, f"Bearer {self._acting_user_token}"),)

    def _get_key(self, entity: T) -> str:
        if self._key_field is None:
            raise ValueError(f"No key field defined for {self._type_name}")
        value = getattr(entity, self._key_field, None)
        if value is None:
            raise ValueError(f"Key field '{self._key_field}' is None on entity")
        return str(value)

    def persist(self, entity: T, trace_id: str = "") -> str:
        """Persist a new entity. Returns the assigned key."""
        payload = _entity_to_struct(entity)
        response = self._persistence.Post(
            persist_pb.PersistRequest(
                type_name=self._type_name,
                payload=payload,
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        )
        if not response.success:
            raise RuntimeError(f"persist failed: {response.error}")
        return response.key

    def update(self, entity: T, trace_id: str = "") -> None:
        """Update an existing entity."""
        payload = _entity_to_struct(entity)
        response = self._persistence.Update(
            persist_pb.PersistRequest(
                type_name=self._type_name,
                payload=payload,
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        )
        if not response.success:
            raise RuntimeError(f"update failed: {response.error}")

    def delete(self, id: str, trace_id: str = "") -> None:
        """Delete an entity by key."""
        response = self._mapping.Delete(
            mapping_pb.MappingDeleteRequest(
                type_name=self._type_name,
                key=id,
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        )
        if not response.success:
            raise RuntimeError(f"delete failed: {response.error}")

    def get_mapped(self, id: str, depth: int = 1, trace_id: str = "") -> Optional[T]:
        """Retrieve an entity by key with server-side relation resolution to ``depth``.
        Returns None if not found."""
        response = self._mapping.Get(
            mapping_pb.MappingGetRequest(
                type_name=self._type_name,
                key=id,
                depth=depth,
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        )
        if not response.success:
            return None
        return self._from_struct(response.data)

    def post_mapped(self, entity: T, trace_id: str = "") -> Optional[T]:
        """Create an entity through the mapping path, which resolves its relations
        server-side. Returns the entity hydrated from the response, carrying the
        server-assigned key — the caller never assigns one."""
        response = self._mapping.Post(
            mapping_pb.MappingWriteRequest(
                type_name=self._type_name,
                payload=_entity_to_struct(entity),
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        )
        if not response.success:
            raise RuntimeError(f"post_mapped failed: {response.error}")
        return self._from_struct(response.data)

    def update_mapped(self, entity: T, trace_id: str = "") -> Optional[T]:
        """Update an existing entity through the mapping path."""
        response = self._mapping.Update(
            mapping_pb.MappingWriteRequest(
                type_name=self._type_name,
                payload=_entity_to_struct(entity),
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        )
        if not response.success:
            raise RuntimeError(f"update_mapped failed: {response.error}")
        return self._from_struct(response.data)

    def get(self, id: str, trace_id: str = "") -> Optional[T]:
        """Retrieve an entity by key. Returns None if not found."""
        response = self._retrieval.Get(
            retrieval_pb.RetrievalRequest(
                type_name=self._type_name,
                key=id,
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        )
        if not response.found:
            return None
        return self._from_struct(response.data)

    def get_many(self, ids: List[str], trace_id: str = "") -> List[T]:
        """Retrieve multiple entities by key."""
        results = []
        for response in self._retrieval.GetMany(
            retrieval_pb.RetrievalManyRequest(
                type_name=self._type_name,
                keys=ids,
                trace_id=trace_id,
            ),
            metadata=self._acting_user_metadata(),
        ):
            if response.found:
                results.append(self._from_struct(response.data))
        return results

    # ── Search-family execution ────────────────────────────────────────────────

    def search(self, request: search_pb.SearchRequest) -> List[SearchResult[T]]:
        """Execute a Search request. Rows are genuinely ``T``-shaped, so each is
        converted via ``_from_struct`` and paired with its relevance score in a
        ``SearchResult``."""
        return [
            SearchResult(self._from_struct(row.data), row.score)
            for row in self._search.Search(request, metadata=self._acting_user_metadata())
        ]

    def search_similar(self, request: search_pb.SearchSimilarRequest) -> List[SearchResult[T]]:
        """Execute a SearchSimilar (vector) request. Rows are genuinely ``T``-shaped,
        so each is converted via ``_from_struct`` and paired with its relevance score
        in a ``SearchResult``."""
        return [
            SearchResult(self._from_struct(row.data), row.score)
            for row in self._search.SearchSimilar(request, metadata=self._acting_user_metadata())
        ]

    def search_chunks(self, request: search_pb.SearchChunksRequest) -> List[search_pb.ChunkSearchResponse]:
        """Execute a SearchChunks request. Returns the flat chunk messages as-is."""
        return list(self._search.SearchChunks(request, metadata=self._acting_user_metadata()))

    def group_by(self, request: search_pb.GroupByRequest) -> List[dict]:
        """Execute a GroupBy request. Columns are aggregated/aliased and don't match
        ``T``'s own fields, so each row is converted via ``_struct_to_dict`` instead
        of ``_from_struct``."""
        return [_struct_to_dict(row.data) for row in self._search.GroupBy(request, metadata=self._acting_user_metadata())]

    def aggregate(self, request: search_pb.AggregateRequest) -> search_pb.AggregateResponse:
        """Execute an Aggregate request. Single call; returns the ``AggregateResponse``
        as-is."""
        return self._search.Aggregate(request, metadata=self._acting_user_metadata())

    def pipeline(self, request: search_pb.PipelineRequest) -> List[dict]:
        """Execute a Pipeline request. Columns are aggregated/aliased and don't match
        ``T``'s own fields, so each row is converted via ``_struct_to_dict`` instead
        of ``_from_struct``."""
        return [_struct_to_dict(row.data) for row in self._search.Pipeline(request, metadata=self._acting_user_metadata())]

    def _from_struct(self, s: struct_pb2.Struct) -> T:
        """Construct an entity instance from a Struct proto."""
        obj = object.__new__(self._cls)
        annotations = {}
        for base in reversed(self._cls.__mro__):
            if base is object:
                continue
            annotations.update(getattr(base, "__annotations__", {}))

        meta = getattr(self._cls, "_iverson_meta", None)
        relation_by_field = {r["field"]: r for r in (meta or {}).get("relations", [])}
        type_name = (meta or {}).get("type_name", self._type_name)

        for field_name in annotations:
            relation = relation_by_field.get(field_name)
            if relation is not None and relation["kind"] == "many_to_many":
                pascal = _infer_fk(relation, type_name)
            else:
                pascal = _to_pascal_case(field_name)

            if pascal in s.fields:
                field = s.fields[pascal]
                kind = field.WhichOneof("kind")
                if kind == "string_value":
                    setattr(obj, field_name, field.string_value)
                elif kind == "number_value":
                    setattr(obj, field_name, field.number_value)
                elif kind == "bool_value":
                    setattr(obj, field_name, field.bool_value)
                elif kind == "list_value":
                    setattr(obj, field_name, _list_value_to_list(field.list_value))
                else:
                    setattr(obj, field_name, None)
            else:
                setattr(obj, field_name, None)
        return obj  # type: ignore[return-value]


# ── IversonClient ──────────────────────────────────────────────────────────────

class IversonClient:
    """Top-level client. Creates a channel and exposes coordinators and registrar.

    Args:
        host: gRPC server host (default: ``localhost``).
        port: gRPC server port (default: ``5000``).
        use_tls: whether to use TLS (default: ``False`` for h2c).
        credentials: optional OAuth2 client-credentials for authenticated calls.
        acting_user_token: optional pre-minted acting-user token, propagated on
            every call as ``x-acting-user-authorization`` metadata.
    """

    def __init__(
        self,
        host: str = "localhost",
        port: int = 5000,
        use_tls: bool = False,
        *,
        credentials: IversonClientCredentials | None = None,
        acting_user_token: str | None = None,
    ) -> None:
        address = f"{host}:{port}"

        if credentials is not None:
            call_creds_list = []
            provider = _CachedTokenProvider(credentials)
            call_creds_list.append(
                grpc.metadata_call_credentials(_BearerTokenAuthPlugin(provider))
            )
            # grpcio rejects CallCredentials on a bare insecure_channel with
            # "UNAUTHENTICATED: Established channel does not have a sufficient security
            # level to transfer call credential" — confirmed live. Some ChannelCredentials
            # is therefore always required as the base here. When use_tls is True we use
            # real TLS via ssl_channel_credentials(); otherwise we fall back to
            # local_channel_credentials(), a lightweight "trusted network" designation
            # (NOT real TLS/encryption) that satisfies the check without requiring actual
            # certificates.
            base_creds = (
                grpc.ssl_channel_credentials() if use_tls else grpc.local_channel_credentials()
            )
            channel_creds = grpc.composite_channel_credentials(base_creds, *call_creds_list)
            self._channel = grpc.secure_channel(address, channel_creds)
        elif use_tls:
            self._channel = grpc.secure_channel(address, grpc.ssl_channel_credentials())
        else:
            self._channel = grpc.insecure_channel(address)

        self._mapping_stub = mapping_grpc.ObjectMappingServiceStub(self._channel)
        self._acting_user_token = acting_user_token

    def _acting_user_metadata(self) -> tuple[tuple[str, str], ...]:
        """Per-call metadata carrying the ambient acting-user identity, or empty when none."""
        # Use `is None`, not a falsy check: an empty-string token is a caller
        # error that must reach the server and be rejected loudly, not be
        # silently downgraded to an unauthenticated call.
        if self._acting_user_token is None:
            return ()
        return ((ACTING_USER_METADATA_KEY, f"Bearer {self._acting_user_token}"),)

    def close(self) -> None:
        """Close the underlying gRPC channel."""
        self._channel.close()

    def __enter__(self) -> "IversonClient":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def coordinator(self, entity_class: type) -> EntityCoordinator:
        """Return an ``EntityCoordinator`` for the given entity class."""
        return EntityCoordinator(entity_class, self._channel, self._acting_user_token)

    def get_schema(self, trace_id: str = "") -> list[mapping_pb.SchemaType]:
        """Return the catalog of registered types this identity may read."""
        response = self._mapping_stub.GetSchema(
            mapping_pb.GetSchemaRequest(trace_id=trace_id),
            metadata=self._acting_user_metadata(),
        )
        return list(response.types)

    def registrar(self, *entity_classes: type) -> SchemaRegistrar:
        """Return a ``SchemaRegistrar`` for the given entity classes."""
        return SchemaRegistrar(self._mapping_stub, *entity_classes)
