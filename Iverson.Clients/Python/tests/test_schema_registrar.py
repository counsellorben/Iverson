"""Tests for SchemaRegistrar — verifies correct RegisterSchema proto is built."""
from __future__ import annotations

from unittest.mock import MagicMock, call
from datetime import datetime

import pytest

from iverson_client.annotations import (
    iverson_entity,
    iverson_field,
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
    many_to_one,
    many_to_many,
    one_to_many,
    one_to_one,
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
    reg_author_id: str = many_to_one("RegAuthor")
    summary: str = iverson_chunk(max_tokens=256, overlap=32)


@iverson_entity(description="An article with metadata signals.")
class RegDescribedArticle:
    id: str = iverson_key(description="Stable article identifier.")
    source: str = iverson_metadata(description="Originating feed.")
    language: str = iverson_metadata()
    region: str = iverson_field(search_key=True, search_key_order=0, metadata=True,
                                description="Publication region.")
    title: str = iverson_description("Headline text.")
    word_count: int = None


@iverson_entity
class RegAuthor:
    id: str = iverson_key()
    name: str = None


@iverson_entity
class RegEnrichedArticle:
    id: str = iverson_key()
    title: str = None
    abstract: str = iverson_summary()
    tags: str = iverson_keywords()
    entities: str = iverson_extracted("Extract named entities as a JSON array.")
    body: str = iverson_chunk(max_tokens=256, overlap=32, contextual=True)


@iverson_entity
class RegComposedEnrichmentArticle:
    id: str = iverson_key()
    title: str = None
    summary_key: str = iverson_field(search_key=True, search_key_order=0, summary=True)
    keywords_meta: str = iverson_field(metadata=True, keywords=True)
    hint_field: str = iverson_field(large_field=True, extract_hint="Extract the price.")
    described_summary: str = iverson_field(description="A summary field.", summary=True)


@iverson_entity
class RegComposedDeclarationArticle:
    id: str = iverson_key()
    title: str = None
    body: str = iverson_field(large_field=True, chunk=True,
                              chunk_max_tokens=256, chunk_overlap=32)
    tenant_id: str = iverson_field(metadata=True)


@iverson_entity
class RegMetadataOnKeyArticle:
    id: str = iverson_field(key=True, metadata=True)


@iverson_entity
class RegSummaryOnKeyArticle:
    id: str = iverson_field(key=True, summary=True)


@iverson_entity
class RegMultiDeclarationKeyArticle:
    id: str = iverson_field(key=True, search_key=True, search_key_order=0,
                            large_field=True, embedding=True, chunk=True,
                            metadata=True, summary=True, keywords=True,
                            extract_hint="hint")


@iverson_entity
class RegDescribedKeyArticle:
    id: str = iverson_key(description="Stable identifier.")


@iverson_entity
class RegArrayArticle:
    id: str = iverson_key()
    tags: list[str] = None
    counts: list[int] = None
    blob: bytes = None


class _CustomElement:
    pass


@iverson_entity
class RegNestedArrayArticle:
    id: str = iverson_key()
    matrix: list[list[str]] = None


@iverson_entity
class RegUnsupportedElementArticle:
    id: str = iverson_key()
    widgets: list[_CustomElement] = None


@iverson_entity(embedding_model="nomic-embed-text")
class RegModelBothFlagsArticle:
    """Declares a model and carries one property that is BOTH an embedding and a chunk
    source, plus one property that is NEITHER — the neither-flags property is the guard-off
    control that proves the stamp does not leak onto every property once a model is declared."""

    id: str = iverson_key()
    title: str = iverson_field(embedding=True, chunk=True)
    category: str = iverson_metadata()


@iverson_entity(embedding_model="nomic-embed-text")
class RegModelAsymmetricArticle:
    """Declares a model with an embedding-ONLY property and a chunk-ONLY property. This is the
    shape that catches a guard swap (``is_chunk`` accidentally guarding ``model_id`` or vice
    versa): with only a both-flags property and a neither-flags property, a swapped guard
    produces identical output and goes undetected."""

    id: str = iverson_key()
    title: str = iverson_embedding()
    body: str = iverson_chunk()


@iverson_entity
class RegModelUndeclaredArticle:
    """No ``embedding_model`` declared. Carries both an embedding and a chunk property so the
    undeclared arm is pinned on ``chunk_model_id`` as well as ``model_id`` — a fixture with only
    an embedded property cannot catch a stamp that leaks onto the chunk field alone."""

    id: str = iverson_key()
    title: str = iverson_embedding()
    body: str = iverson_chunk()


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


def register_request(cls) -> mapping_pb.SchemaRequest:
    """Register `cls` against a stub and return the SchemaRequest that was sent."""
    stub = make_stub()
    SchemaRegistrar(stub, cls).register_all()
    return stub.RegisterSchema.call_args[0][0]


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

    def test_fk_column_declared_for_non_one_to_many_kinds(self):
        @iverson_entity
        class RegRelKindsArticle:
            id: str = iverson_key()
            reg_author_id: str = many_to_one("RegAuthor")
            reg_tag_ids: list[str] = many_to_many("RegTag")
            reg_comments: list = one_to_many("RegComment")

        @iverson_entity
        class RegRelKindsNote:
            id: str = iverson_key()
            reg_author_id: str = one_to_one("RegAuthor")

        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegRelKindsArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        props = {p.name: p for p in request.root_type.properties}

        assert "RegAuthorId" in props  # many_to_one
        fk_prop = props["RegAuthorId"]
        assert fk_prop.clr_type == mapping_pb.CLR_GUID
        assert fk_prop.is_array is False
        assert fk_prop.is_nullable is True
        assert fk_prop.is_key is False

        assert "RegTagIds" in props  # many_to_many
        mtm_prop = props["RegTagIds"]
        assert mtm_prop.clr_type == mapping_pb.CLR_GUID
        assert mtm_prop.is_array is True
        assert mtm_prop.is_nullable is True
        assert mtm_prop.is_key is False

        # one_to_many declares no FK column at all
        assert "RegCommentId" not in props
        assert "RegRelKindsArticleId" not in props

        stub2 = make_stub()
        stub2.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar2 = SchemaRegistrar(stub2, RegRelKindsNote)
        registrar2.register_all()

        request2: mapping_pb.SchemaRequest = stub2.RegisterSchema.call_args[0][0]
        props2 = {p.name: p for p in request2.root_type.properties}
        assert "RegAuthorId" in props2  # one_to_one
        assert props2["RegAuthorId"].is_array is False

    def test_many_to_one_property_name_differs_from_foreign_key(self):
        @iverson_entity
        class RegNavArticle:
            id: str = iverson_key()
            author_id: str = many_to_one("Author")

        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegNavArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        rel = request.root_type.relations[0]
        # The navigation property name must NOT collide with the FK column, or a
        # depth-resolved read overwrites the FK value with the hydrated entity.
        assert rel.property_name != rel.foreign_key
        assert rel.property_name == "Author"
        assert rel.foreign_key == "AuthorId"

    def test_correctly_named_many_to_one_registers(self):
        @iverson_entity
        class RegGoodArticle:
            id: str = iverson_key()
            reg_author_id: str = many_to_one("RegAuthor")

        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegGoodArticle)
        registrar.register_all()  # should not raise

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        assert request.root_type.relations[0].foreign_key == "RegAuthorId"

    def test_many_to_many_property_name_differs_from_foreign_key(self):
        @iverson_entity
        class RegNavTagArticle:
            id: str = iverson_key()
            reg_tag_ids: list[str] = many_to_many("RegTag")

        stub = make_stub()
        stub.RegisterSchema.return_value = mapping_pb.SchemaResponse(success=True)
        registrar = SchemaRegistrar(stub, RegNavTagArticle)
        registrar.register_all()

        request: mapping_pb.SchemaRequest = stub.RegisterSchema.call_args[0][0]
        rel = request.root_type.relations[0]
        # The navigation property name must NOT collide with the FK column, or a
        # depth-resolved read overwrites the FK value with the hydrated entity.
        assert rel.property_name != rel.foreign_key
        assert rel.property_name == "RegTags"
        assert rel.foreign_key == "RegTagIds"

        props = {p.name: p for p in request.root_type.properties}
        assert "RegTagIds" in props

    def test_misnamed_many_to_one_is_rejected(self):
        @iverson_entity
        class RegBadArticle:
            id: str = iverson_key()
            writer_id: str = many_to_one("RegAuthor")

        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegBadArticle)
        with pytest.raises(ValueError, match="WriterId") as exc_info:
            registrar.register_all()
        assert "RegAuthorId" in str(exc_info.value)

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
    def test_type_description_populated(self):
        request = register_request(RegDescribedArticle)
        assert request.root_type.description == "An article with metadata signals."

    def test_type_description_defaults_empty(self):
        request = register_request(RegArticle)
        assert request.root_type.description == ""

    def test_metadata_fields_flagged(self):
        props = {p.name: p for p in register_request(RegDescribedArticle).root_type.properties}
        assert props["Source"].is_metadata is True
        assert props["Language"].is_metadata is True

    def test_metadata_composes_with_search_key(self):
        props = {p.name: p for p in register_request(RegDescribedArticle).root_type.properties}
        assert props["Region"].is_metadata is True
        assert props["Region"].is_search_key is True
        assert props["Region"].search_key_order == 0
        assert props["Region"].description == "Publication region."

    def test_key_field_description_carried(self):
        props = {p.name: p for p in register_request(RegDescribedArticle).root_type.properties}
        assert props["Id"].is_key is True
        assert props["Id"].description == "Stable article identifier."

    def test_plain_field_description_carried(self):
        props = {p.name: p for p in register_request(RegDescribedArticle).root_type.properties}
        assert props["Title"].description == "Headline text."

    def test_undeclared_fields_default_to_false_and_empty(self):
        props = {p.name: p for p in register_request(RegDescribedArticle).root_type.properties}
        assert props["WordCount"].is_metadata is False
        assert props["WordCount"].description == ""
        assert props["Language"].description == ""


class TestEnrichmentTargets:
    def test_summary_target_flagged(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
        assert props["Abstract"].is_summary_target is True

    def test_keywords_target_flagged(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
        assert props["Tags"].is_keywords_target is True

    def test_extracted_hint_carried(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
        assert props["Entities"].extract_hint == "Extract named entities as a JSON array."

    def test_chunk_contextual_flagged(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
        assert props["Body"].is_chunk is True
        assert props["Body"].chunk_contextual is True

    def test_chunk_contextual_defaults_false(self):
        props = {p.name: p for p in register_request(RegArticle).root_type.properties}
        assert props["Summary"].chunk_contextual is False

    def test_undeclared_property_has_no_enrichment_targets(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
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

    def test_standalone_summary_still_works(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
        assert props["Abstract"].is_summary_target is True
        assert props["Abstract"].is_search_key is False

    def test_standalone_keywords_still_works(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
        assert props["Tags"].is_keywords_target is True

    def test_standalone_extracted_still_works(self):
        props = {p.name: p for p in register_request(RegEnrichedArticle).root_type.properties}
        assert props["Entities"].extract_hint == "Extract named entities as a JSON array."

    def test_summary_composes_with_search_key(self):
        props = {p.name: p for p in register_request(RegComposedEnrichmentArticle).root_type.properties}
        assert props["SummaryKey"].is_summary_target is True
        assert props["SummaryKey"].is_search_key is True
        assert props["SummaryKey"].search_key_order == 0

    def test_keywords_composes_with_metadata(self):
        props = {p.name: p for p in register_request(RegComposedEnrichmentArticle).root_type.properties}
        assert props["KeywordsMeta"].is_keywords_target is True
        assert props["KeywordsMeta"].is_metadata is True

    def test_extract_hint_composes_with_large_field(self):
        props = {p.name: p for p in register_request(RegComposedEnrichmentArticle).root_type.properties}
        assert props["HintField"].extract_hint == "Extract the price."
        assert props["HintField"].is_large_field is True

    def test_summary_composes_with_description(self):
        props = {p.name: p for p in register_request(RegComposedEnrichmentArticle).root_type.properties}
        assert props["DescribedSummary"].is_summary_target is True
        assert props["DescribedSummary"].description == "A summary field."

    def test_blank_extract_hint_rejected(self):
        with pytest.raises(ValueError, match="extract"):
            iverson_field(extract_hint="   ")

    def test_none_extract_hint_rejected(self):
        with pytest.raises(ValueError, match="extract"):
            iverson_field(extract_hint=None)


class TestDeclarationComposition:
    def test_large_field_composes_with_chunk(self):
        props = {p.name: p for p in
                 register_request(RegComposedDeclarationArticle).root_type.properties}
        assert props["Body"].is_large_field is True
        assert props["Body"].is_chunk is True
        assert props["Body"].chunk_max_tokens == 256
        assert props["Body"].chunk_overlap == 32

    def test_multi_flag_field_emits_exactly_one_property(self):
        request = register_request(RegComposedDeclarationArticle)
        names = [p.name for p in request.root_type.properties]
        assert names.count("Body") == 1
        assert names.count("TenantId") == 1


class TestKeyFieldDeclarations:
    def test_metadata_on_key_raises(self):
        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegMetadataOnKeyArticle)
        with pytest.raises(ValueError) as ex:
            registrar.register_all()
        assert "RegMetadataOnKeyArticle.id is the primary key and also declares" in str(ex.value)
        assert "iverson_metadata()" in str(ex.value)
        assert "silently discarded" in str(ex.value)

    def test_summary_on_key_raises(self):
        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegSummaryOnKeyArticle)
        with pytest.raises(ValueError) as ex:
            registrar.register_all()
        assert "RegSummaryOnKeyArticle.id is the primary key and also declares" in str(ex.value)
        assert "iverson_summary()" in str(ex.value)
        assert "silently discarded" in str(ex.value)

    def test_every_rejected_declaration_named_in_one_error(self):
        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegMultiDeclarationKeyArticle)
        with pytest.raises(ValueError) as ex:
            registrar.register_all()
        message = str(ex.value)
        assert "iverson_search_key()" in message
        assert "iverson_large_field()" in message
        assert "iverson_embedding()" in message
        assert "iverson_chunk()" in message
        assert "iverson_metadata()" in message
        assert "iverson_summary()" in message
        assert "iverson_keywords()" in message
        assert "iverson_extracted()" in message

    def test_description_on_key_still_registers(self):
        request = register_request(RegDescribedKeyArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Id"].is_key is True
        assert props["Id"].description == "Stable identifier."


class TestArrayProperties:
    def test_list_str_flagged_as_array_of_string(self):
        request = register_request(RegArrayArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Tags"].is_array is True
        assert props["Tags"].clr_type == mapping_pb.CLR_STRING

    def test_list_int_flagged_as_array_of_int32(self):
        request = register_request(RegArrayArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Counts"].is_array is True
        assert props["Counts"].clr_type == mapping_pb.CLR_INT32

    def test_bytes_still_scalar_not_array(self):
        request = register_request(RegArrayArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Blob"].is_array is False
        assert props["Blob"].clr_type == mapping_pb.CLR_BYTES

    def test_nested_array_rejected(self):
        # Silently collapsing list[list[str]] to a TEXT[] column would register a schema the
        # server accepts and json_populate_record then fails on at the first insert.
        with pytest.raises(ValueError) as exc:
            register_request(RegNestedArrayArticle)
        message = str(exc.value)
        assert "matrix" in message
        assert "Nested array" in message

    def test_unsupported_element_type_rejected(self):
        with pytest.raises(ValueError) as exc:
            register_request(RegUnsupportedElementArticle)
        message = str(exc.value)
        assert "widgets" in message
        assert "_CustomElement" in message


class TestAuthorizationRules:
    def test_each_type_gets_its_own_rules_and_unlisted_type_gets_none(self):
        stub = make_stub()
        registrar = SchemaRegistrar(stub, RegArticle, RegAuthor, RegDescribedKeyArticle)
        article_rules = mapping_pb.AuthorizationRules(owner_field="OwnerOne")
        author_rules = mapping_pb.AuthorizationRules(owner_field="OwnerTwo")

        registrar.register_all(
            authorization_by_type_name={
                "RegArticle": article_rules,
                "RegAuthor": author_rules,
            }
        )

        requests = {
            c.args[0].root_type.type_name: c.args[0]
            for c in stub.RegisterSchema.call_args_list
        }
        assert requests["RegArticle"].root_type.authorization.owner_field == "OwnerOne"
        assert requests["RegAuthor"].root_type.authorization.owner_field == "OwnerTwo"
        assert requests["RegDescribedKeyArticle"].root_type.HasField("authorization") is False


class TestEmbeddingModelDeclaration:
    """``@iverson_entity(embedding_model=...)`` is stamped onto ``model_id``/``chunk_model_id``
    in place of the ``""`` literals in ``SchemaRegistrar._build_request``, guarded per property
    on that property's own ``is_embedding``/``is_chunk`` values."""

    def test_declared_model_stamped_on_both_flags_property(self):
        request = register_request(RegModelBothFlagsArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Title"].model_id == "nomic-embed-text"
        assert props["Title"].chunk_model_id == "nomic-embed-text"

    def test_declared_model_not_stamped_on_neither_flag_property(self):
        request = register_request(RegModelBothFlagsArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Category"].model_id == ""
        assert props["Category"].chunk_model_id == ""

    def test_declared_model_stamped_only_on_model_id_for_embedding_only_property(self):
        request = register_request(RegModelAsymmetricArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Title"].model_id == "nomic-embed-text"
        assert props["Title"].chunk_model_id == ""

    def test_declared_model_stamped_only_on_chunk_model_id_for_chunk_only_property(self):
        request = register_request(RegModelAsymmetricArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Body"].model_id == ""
        assert props["Body"].chunk_model_id == "nomic-embed-text"

    def test_undeclared_model_leaves_model_id_and_chunk_model_id_empty(self):
        request = register_request(RegModelUndeclaredArticle)
        props = {p.name: p for p in request.root_type.properties}
        assert props["Title"].model_id == ""
        assert props["Title"].chunk_model_id == ""
        assert props["Body"].model_id == ""
        assert props["Body"].chunk_model_id == ""
