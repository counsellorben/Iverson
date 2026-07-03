"""
Core client classes: IversonClient, SchemaRegistrar, EntityCoordinator.
"""
from __future__ import annotations

import re
import uuid
from typing import Generic, List, Optional, TypeVar

import grpc
from google.protobuf import struct_pb2

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


def _python_type_to_clr(type_hint: str | type | None) -> int:
    """Map a Python type annotation to a ClrType enum value."""
    if type_hint is None:
        return mapping_pb.CLR_STRING
    name = type_hint if isinstance(type_hint, str) else getattr(type_hint, "__name__", str(type_hint))
    return _PY_TO_CLR.get(name, mapping_pb.CLR_STRING)


def _to_pascal_case(snake: str) -> str:
    """Convert snake_case field name to PascalCase column name (e.g. author_id → AuthorId)."""
    return "".join(part.capitalize() for part in snake.split("_"))


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

    def register_all(self, trace_id: str = "") -> None:
        """Synchronously register all entity schemas."""
        for cls in self._classes:
            request = self._build_request(cls, trace_id)
            response = self._stub.RegisterSchema(request)
            if not response.success:
                raise RuntimeError(
                    f"Schema registration failed for {cls.__name__}: {response.error}"
                )

    async def register_all_async(self, trace_id: str = "") -> None:
        """Asynchronously register all entity schemas (requires async channel)."""
        for cls in self._classes:
            request = self._build_request(cls, trace_id)
            response = await self._stub.RegisterSchema(request)
            if not response.success:
                raise RuntimeError(
                    f"Schema registration failed for {cls.__name__}: {response.error}"
                )

    def _build_request(self, cls: type, trace_id: str) -> mapping_pb.SchemaRequest:
        meta = getattr(cls, "_iverson_meta", None)
        if meta is None:
            raise ValueError(
                f"{cls.__name__} is not decorated with @iverson_entity"
            )

        annotations = {}
        for base in reversed(cls.__mro__):
            if base is object:
                continue
            annotations.update(getattr(base, "__annotations__", {}))

        type_name = meta["type_name"]
        key_field = meta["key_field"]
        search_keys_by_field = {f: o for f, o in meta["search_keys"]}
        large_fields_set = set(meta["large_fields"])
        embedding_fields_set = set(meta["embedding_fields"])
        chunk_fields_by_name = {f: (mt, ov) for f, mt, ov in meta["chunk_fields"]}
        relation_fields = {r["field"] for r in meta["relations"]}

        properties: list[mapping_pb.PropertyDescriptor] = []
        for field_name in meta["fields"]:
            if field_name in relation_fields:
                continue
            type_hint = annotations.get(field_name)
            clr_type = _python_type_to_clr(type_hint)
            is_chunk = field_name in chunk_fields_by_name
            chunk_max_tokens, chunk_overlap = chunk_fields_by_name.get(field_name, (0, 0))
            prop = mapping_pb.PropertyDescriptor(
                name=_to_pascal_case(field_name),
                clr_type=clr_type,
                is_key=(field_name == key_field),
                is_nullable=(field_name != key_field),
                is_array=False,
                is_search_key=(field_name in search_keys_by_field),
                search_key_order=search_keys_by_field.get(field_name, 0),
                is_large_field=(field_name in large_fields_set),
                is_embedding=(field_name in embedding_fields_set),
                vector_dim=0,
                model_id="",
                is_chunk=is_chunk,
                chunk_max_tokens=chunk_max_tokens,
                chunk_overlap=chunk_overlap,
                chunk_model_id="",
                chunk_vector_dim=0,
            )
            properties.append(prop)

        relations: list[mapping_pb.RelationDescriptor] = []
        for rel in meta["relations"]:
            fk = _infer_fk(rel, type_name)
            relations.append(
                mapping_pb.RelationDescriptor(
                    property_name=_to_pascal_case(rel["field"]),
                    kind=_RELATION_KIND_MAP.get(rel["kind"], mapping_pb.MANY_TO_ONE),
                    related_type=rel.get("related_type") or "",
                    foreign_key=fk,
                )
            )

        type_descriptor = mapping_pb.TypeDescriptor(
            type_name=type_name,
            properties=properties,
            relations=relations,
        )
        return mapping_pb.SchemaRequest(root_type=type_descriptor, trace_id=trace_id)


# ── StructConverter ────────────────────────────────────────────────────────────

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

    for field_name in annotations:
        value = getattr(entity, field_name, None)
        if value is None:
            continue
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
        else:
            s.fields[pascal].string_value = str(value)
    return s


def _struct_to_dict(s: struct_pb2.Struct) -> dict:
    return dict(s)


# ── EntityCoordinator ──────────────────────────────────────────────────────────

class EntityCoordinator(Generic[T]):
    """High-level coordinator for a single entity type.

    Wraps ObjectMappingService for full CRUD with relation traversal
    and ObjectPersistenceService for lightweight writes.

    Args:
        entity_class: the ``@iverson_entity``-decorated class.
        channel: an open ``grpc.Channel``.
    """

    def __init__(self, entity_class: type, channel: grpc.Channel) -> None:
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
            )
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
            )
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
            )
        )
        if not response.success:
            raise RuntimeError(f"delete failed: {response.error}")

    def get(self, id: str, trace_id: str = "") -> Optional[T]:
        """Retrieve an entity by key. Returns None if not found."""
        response = self._retrieval.Get(
            retrieval_pb.RetrievalRequest(
                type_name=self._type_name,
                key=id,
                trace_id=trace_id,
            )
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
            )
        ):
            if response.found:
                results.append(self._from_struct(response.data))
        return results

    def _from_struct(self, s: struct_pb2.Struct) -> T:
        """Construct an entity instance from a Struct proto."""
        obj = object.__new__(self._cls)
        annotations = {}
        for base in reversed(self._cls.__mro__):
            if base is object:
                continue
            annotations.update(getattr(base, "__annotations__", {}))

        for field_name in annotations:
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
    """

    def __init__(
        self,
        host: str = "localhost",
        port: int = 5000,
        use_tls: bool = False,
    ) -> None:
        address = f"{host}:{port}"
        if use_tls:
            self._channel = grpc.secure_channel(address, grpc.ssl_channel_credentials())
        else:
            self._channel = grpc.insecure_channel(address)

        self._mapping_stub = mapping_grpc.ObjectMappingServiceStub(self._channel)

    def close(self) -> None:
        """Close the underlying gRPC channel."""
        self._channel.close()

    def __enter__(self) -> "IversonClient":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def coordinator(self, entity_class: type) -> EntityCoordinator:
        """Return an ``EntityCoordinator`` for the given entity class."""
        return EntityCoordinator(entity_class, self._channel)

    def registrar(self, *entity_classes: type) -> SchemaRegistrar:
        """Return a ``SchemaRegistrar`` for the given entity classes."""
        return SchemaRegistrar(self._mapping_stub, *entity_classes)
