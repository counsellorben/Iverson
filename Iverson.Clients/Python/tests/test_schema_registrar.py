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
    iverson_embedding,
    iverson_chunk,
    iverson_metadata,
    iverson_description,
    iverson_summary,
    iverson_keywords,
    iverson_extracted,
    iverson_tenant,
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
    title: str = iverson_embedding()
    body: str = iverson_large_field()
    category: str = iverson_search_key(order=0)
    word_count: int = None
    published_at: datetime = iverson_search_key(order=1)
    author_id: str = many_to_one("RegAuthor")
    summary: str = iverson_chunk(max_tokens=256, overlap=32)
    tenant_id: str = iverson_tenant()


@iverson_entity(description="An article with metadata signals.")
class RegDescribedArticle:
    id: str = iverson_key(description="Stable article identifier.")
    source: str = iverson_metadata(description="Originating feed.")
    language: str = iverson_metadata()
    region: str = iverson_search_key(order=0, metadata=True, description="Publication region.")
    title: str = iverson_description("Headline text.")
    word_count: int = None
    tenant_id: str = iverson_tenant()


@iverson_entity
class RegAuthor:
    id: str = iverson_key()
    name: str = None
    tenant_id: str = iverson_tenant()


@iverson_entity
class RegEnrichedArticle:
    id: str = iverson_key()
    title: str = None
    abstract: str = iverson_summary()
    tags: str = iverson_keywords()
    entities: str = iverson_extracted("Extract named entities as a JSON array.")
    body: str = iverson_chunk(max_tokens=256, overlap=32, contextual=True)
    tenant_id: str = iverson_tenant()


@iverson_entity
class RegNoTenantArticle:
    id: str = iverson_key()
    title: str = None


@iverson_entity
class RegMultiTenantArticle:
    id: str = iverson_key()
    tenant_id: str = iverson_tenant()
    org_id: str = iverson_tenant()


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

    def test_embedding_flagged(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        props = {p.name: p for p in request.root_type.properties}
        assert "Title" in props
        assert props["Title"].is_embedding is True

    def test_chunk_flagged_with_params(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        props = {p.name: p for p in request.root_type.properties}
        assert "Summary" in props
        assert props["Summary"].is_chunk is True
        assert props["Summary"].chunk_max_tokens == 256
        assert props["Summary"].chunk_overlap == 32

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


class TestMetadataAndDescription:
    def _request(self, cls) -> mapping_pb.SchemaRequest:
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        SchemaRegistrar(stub, cls).register_all()
        return stub.RegisterSchema.call_args[0][0]

    def test_type_description_populated(self):
        request = self._request(RegDescribedArticle)
        assert request.root_type.description == "An article with metadata signals."

    def test_type_description_defaults_empty(self):
        request = self._request(RegArticle)
        assert request.root_type.description == ""

    def test_metadata_fields_flagged(self):
        props = {p.name: p for p in self._request(RegDescribedArticle).root_type.properties}
        assert props["Source"].is_metadata is True
        assert props["Language"].is_metadata is True

    def test_metadata_composes_with_search_key(self):
        props = {p.name: p for p in self._request(RegDescribedArticle).root_type.properties}
        assert props["Region"].is_metadata is True
        assert props["Region"].is_search_key is True
        assert props["Region"].search_key_order == 0
        assert props["Region"].description == "Publication region."

    def test_key_field_description_carried(self):
        props = {p.name: p for p in self._request(RegDescribedArticle).root_type.properties}
        assert props["Id"].is_key is True
        assert props["Id"].description == "Stable article identifier."

    def test_plain_field_description_carried(self):
        props = {p.name: p for p in self._request(RegDescribedArticle).root_type.properties}
        assert props["Title"].description == "Headline text."

    def test_undeclared_fields_default_to_false_and_empty(self):
        props = {p.name: p for p in self._request(RegDescribedArticle).root_type.properties}
        assert props["WordCount"].is_metadata is False
        assert props["WordCount"].description == ""
        assert props["Language"].description == ""


class TestEnrichmentTargets:
    def _request(self, cls) -> mapping_pb.SchemaRequest:
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        SchemaRegistrar(stub, cls).register_all()
        return stub.RegisterSchema.call_args[0][0]

    def test_summary_target_flagged(self):
        props = {p.name: p for p in self._request(RegEnrichedArticle).root_type.properties}
        assert props["Abstract"].is_summary_target is True

    def test_keywords_target_flagged(self):
        props = {p.name: p for p in self._request(RegEnrichedArticle).root_type.properties}
        assert props["Tags"].is_keywords_target is True

    def test_extracted_hint_carried(self):
        props = {p.name: p for p in self._request(RegEnrichedArticle).root_type.properties}
        assert props["Entities"].extract_hint == "Extract named entities as a JSON array."

    def test_chunk_contextual_flagged(self):
        props = {p.name: p for p in self._request(RegEnrichedArticle).root_type.properties}
        assert props["Body"].is_chunk is True
        assert props["Body"].chunk_contextual is True

    def test_chunk_contextual_defaults_false(self):
        props = {p.name: p for p in self._request(RegArticle).root_type.properties}
        assert props["Summary"].chunk_contextual is False

    def test_undeclared_property_has_no_enrichment_targets(self):
        props = {p.name: p for p in self._request(RegEnrichedArticle).root_type.properties}
        assert props["Title"].is_summary_target is False
        assert props["Title"].is_keywords_target is False
        assert props["Title"].extract_hint == ""
        assert props["Title"].chunk_contextual is False

    def test_blank_extraction_hint_rejected(self):
        with pytest.raises(ValueError, match="extract"):
            iverson_extracted("")

    def test_whitespace_extraction_hint_rejected(self):
        with pytest.raises(ValueError, match="extract"):
            iverson_extracted("   ")


class TestTenantField:
    def test_tenant_field_name_on_descriptor(self):
        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        assert request.root_type.tenant_field == "TenantId"

    def test_zero_tenant_markers_raises(self):
        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegNoTenantArticle)
        with pytest.raises(ValueError, match="RegNoTenantArticle"):
            registrar.register_all()

    def test_multiple_tenant_markers_raises(self):
        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegMultiTenantArticle)
        with pytest.raises(ValueError, match="tenant_id.*org_id|org_id.*tenant_id"):
            registrar.register_all()
