"""Tests for SchemaRegistrar — verifies correct RegisterSchema proto is built."""
from __future__ import annotations

from unittest.mock import MagicMock, call
from datetime import datetime

import pytest

from iverson_client.annotations import (
    iverson_entity,
    iverson_key,
    iverson_search_key,
    iverson_large_field,
    many_to_one,
    one_to_many,
)
from iverson_client.core import SchemaRegistrar
from iverson_client.generated import (
    object_mapping_pb2 as mapping_pb,
    object_mapping_pb2_grpc as mapping_grpc,
)


# ── Test entities ──────────────────────────────────────────────────────────────

@iverson_entity
class RegArticle:
    id: str = iverson_key()
    title: str = None
    body: str = iverson_large_field()
    category: str = iverson_search_key(order=0)
    word_count: int = None
    published_at: datetime = iverson_search_key(order=1)
    author_id: str = many_to_one("RegAuthor")


@iverson_entity
class RegAuthor:
    id: str = iverson_key()
    name: str = None


# ── Fixtures ───────────────────────────────────────────────────────────────────

def make_stub() -> MagicMock:
    # Don't use spec= here: gRPC stubs set their methods as *instance* attributes
    # in __init__ (not class attributes), so MagicMock(spec=...) won't see them.
    stub = MagicMock()
    stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(
        success=True,
        registered=["RegArticle"],
    )
    return stub


# ── Tests ──────────────────────────────────────────────────────────────────────

class TestSchemaRegistrar:
    def test_register_all_calls_stub_once_per_class(self):
        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegArticle, RegAuthor)
        registrar.register_all()
        assert stub.RegisterSchema.call_count == 2

    def test_request_type_name(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        assert request.root_type.type_name == "RegArticle"

    def test_key_property_in_request(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        props = {p.name: p for p in request.root_type.properties}
        assert "Id" in props
        assert props["Id"].is_key is True

    def test_large_field_flagged(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        props = {p.name: p for p in request.root_type.properties}
        assert "Body" in props
        assert props["Body"].is_large_field is True

    def test_search_key_flagged_with_order(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        props = {p.name: p for p in request.root_type.properties}

        assert "Category" in props
        assert props["Category"].is_search_key is True
        assert props["Category"].search_key_order == 0

        assert "PublishedAt" in props
        assert props["PublishedAt"].is_search_key is True
        assert props["PublishedAt"].search_key_order == 1

    def test_relation_included(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        relations = request.root_type.relations
        assert len(relations) == 1
        rel = relations[0]
        assert rel.related_type == "RegAuthor"
        assert rel.kind == mapping_pb.MANY_TO_ONE
        # FK inferred as {RelatedType}Id
        assert rel.foreign_key == "RegAuthorId"

    def test_field_names_pascal_case(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        prop_names = [p.name for p in request.root_type.properties]
        # snake_case → PascalCase
        assert "WordCount" in prop_names
        assert "PublishedAt" in prop_names

    def test_raises_on_failure_response(self):
        stub = MagicMock()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(
            success=False,
            error="table already exists",
        )
        registrar = SchemaRegistrar(stub, RegArticle)
        with pytest.raises(RuntimeError, match="table already exists"):
            registrar.register_all()

    def test_raises_when_not_iverson_entity(self):
        class Plain:
            id: str = None

        stub = make_stub()
        registrar = SchemaRegistrar(stub, Plain)  # type: ignore
        with pytest.raises(ValueError, match="@iverson_entity"):
            registrar.register_all()

    def test_trace_id_passed_through(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegAuthor)
        registrar.register_all(trace_id="test-trace-123")

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        assert request.trace_id == "test-trace-123"
