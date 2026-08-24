"""Tests for EntityCoordinator's search-family execution methods (search, search_similar,
group_by, aggregate, search_chunks, pipeline). No existing EntityCoordinator execution
tests existed before this task (only builder tests) — this file establishes that pattern,
following test_schema_registrar.py's mocking convention."""
from __future__ import annotations

from unittest.mock import MagicMock

import grpc
from google.protobuf import struct_pb2

from iverson_client.annotations import (
    iverson_entity,
    iverson_key,
    many_to_one,
    many_to_many,
    one_to_many,
    one_to_one,
)
from iverson_client.core import EntityCoordinator, _entity_to_struct
from iverson_client.generated import (
    object_mapping_pb2 as mapping_pb,
    object_retrieval_pb2 as retrieval_pb,
    object_search_pb2 as pb,
    object_search_pb2_grpc as search_grpc,
)


# ── Test entity ────────────────────────────────────────────────────────────────

@iverson_entity
class CoordArticle:
    id: str = iverson_key()
    title: str = None


@iverson_entity
class CoordAuthor:
    id: str = iverson_key()
    name: str = None
    tag_ids: list[str] = many_to_many("CoordTag")
    coord_article_id: str = many_to_one("CoordArticle")
    articles: list = one_to_many("CoordArticle")


# ── Fixtures ───────────────────────────────────────────────────────────────────

def make_coordinator() -> EntityCoordinator:
    # A bare insecure_channel never actually connects until an RPC is issued, so this
    # is safe to use purely to satisfy stub construction; _search is swapped for a mock
    # immediately after, since no real server is available in this test.
    channel = grpc.insecure_channel("localhost:1")
    coordinator = EntityCoordinator(CoordArticle, channel)
    coordinator._search = MagicMock()
    return coordinator


def make_mapped_coordinator() -> EntityCoordinator:
    channel = grpc.insecure_channel("localhost:1")
    coordinator = EntityCoordinator(CoordArticle, channel)
    coordinator._mapping = MagicMock()
    return coordinator


def _mapped_response(**fields) -> mapping_pb.MappingResponse:
    s = struct_pb2.Struct()
    for name, value in fields.items():
        s.fields[name].string_value = value
    return mapping_pb.MappingResponse(success=True, data=s)


def _row(id_: str, title: str) -> pb.SearchResponse:
    s = struct_pb2.Struct()
    s.fields["Id"].string_value = id_
    s.fields["Title"].string_value = title
    return pb.SearchResponse(data=s, score=1.0)


# ── Tests ──────────────────────────────────────────────────────────────────────

class TestEntityCoordinatorSearchFamily:
    def test_search_stub_is_object_search_service_stub(self):
        channel = grpc.insecure_channel("localhost:1")
        coordinator = EntityCoordinator(CoordArticle, channel)
        assert isinstance(coordinator._search, search_grpc.ObjectSearchServiceStub)

    def test_search_converts_rows_via_from_struct(self):
        coordinator = make_coordinator()
        coordinator._search.Search.return_value = iter([_row("1", "Hello")])

        results = coordinator.search(pb.SearchRequest(type_name="CoordArticle"))

        assert len(results) == 1
        assert results[0].entity.id == "1"
        assert results[0].entity.title == "Hello"
        assert results[0].score == 1.0

    def test_search_similar_converts_rows_via_from_struct(self):
        coordinator = make_coordinator()
        coordinator._search.SearchSimilar.return_value = iter([_row("2", "World")])

        results = coordinator.search_similar(
            pb.SearchSimilarRequest(type_name="CoordArticle", property="Title", query="hi")
        )

        assert len(results) == 1
        assert results[0].entity.id == "2"
        assert results[0].entity.title == "World"
        assert results[0].score == 1.0

    def test_group_by_converts_rows_via_struct_to_dict(self):
        coordinator = make_coordinator()
        s = struct_pb2.Struct()
        s.fields["Category"].string_value = "tech"
        s.fields["ArticleCount"].number_value = 3
        coordinator._search.GroupBy.return_value = iter([pb.SearchResponse(data=s)])

        results = coordinator.group_by(pb.GroupByRequest(type_name="CoordArticle"))

        assert results == [{"Category": "tech", "ArticleCount": 3.0}]

    def test_pipeline_converts_rows_via_struct_to_dict(self):
        coordinator = make_coordinator()
        s = struct_pb2.Struct()
        s.fields["Rank"].number_value = 1
        coordinator._search.Pipeline.return_value = iter([pb.SearchResponse(data=s)])

        results = coordinator.pipeline(pb.PipelineRequest(type_name="CoordArticle"))

        assert results == [{"Rank": 1.0}]

    def test_search_chunks_returns_flat_messages_as_is(self):
        coordinator = make_coordinator()
        chunk = pb.ChunkSearchResponse(parent_key="1", chunk_text="passage", score=0.9)
        coordinator._search.SearchChunks.return_value = iter([chunk])

        results = coordinator.search_chunks(
            pb.SearchChunksRequest(type_name="CoordArticle", property="Body", query="q")
        )

        assert results == [chunk]

    def test_aggregate_returns_response_as_is(self):
        coordinator = make_coordinator()
        response = pb.AggregateResponse(total=42)
        coordinator._search.Aggregate.return_value = response

        result = coordinator.aggregate(pb.AggregateRequest(type_name="CoordArticle"))

        assert result is response


# ── Relation write/read contract ────────────────────────────────────────────────

class TestRelationForeignKeyOnlyContract:
    def test_write_payload_carries_fk_not_nav_property_name(self):
        author = CoordAuthor()
        author.id = "1"
        author.name = "Ada"
        author.coord_article_id = "art-1"
        author.tag_ids = ["t1", "t2"]

        s = _entity_to_struct(author)

        assert "CoordArticleId" in s.fields
        assert s.fields["CoordArticleId"].string_value == "art-1"

    def test_write_payload_omits_one_to_many_member(self):
        author = CoordAuthor()
        author.id = "1"
        author.name = "Ada"
        author.coord_article_id = "art-1"
        author.tag_ids = ["t1", "t2"]
        author.articles = ["some-article-object"]

        s = _entity_to_struct(author)

        # a one_to_many member carries no foreign key on the write side at all;
        # without the skip, _infer_fk would emit it under "CoordAuthorId"
        assert "Articles" not in s.fields
        assert "CoordAuthorId" not in s.fields

    def test_many_to_many_id_list_arrives_as_list_value_not_string(self):
        author = CoordAuthor()
        author.id = "1"
        author.name = "Ada"
        author.coord_article_id = "art-1"
        author.tag_ids = ["t1", "t2"]

        s = _entity_to_struct(author)

        field = s.fields["CoordTagIds"]
        assert field.WhichOneof("kind") == "list_value"
        assert [v.string_value for v in field.list_value.values] == ["t1", "t2"]

    def test_many_to_many_round_trips_through_struct(self):
        coordinator = make_coordinator_for(CoordAuthor)
        author = CoordAuthor()
        author.id = "1"
        author.name = "Ada"
        author.coord_article_id = "art-1"
        author.tag_ids = ["t1", "t2"]

        s = _entity_to_struct(author)
        restored = coordinator._from_struct(s)

        assert restored.tag_ids == ["t1", "t2"]
        assert restored.coord_article_id == "art-1"


def make_coordinator_for(cls: type) -> EntityCoordinator:
    channel = grpc.insecure_channel("localhost:1")
    coordinator = EntityCoordinator(cls, channel)
    coordinator._search = MagicMock()
    return coordinator


class TestEntityCoordinatorMappedCrud:
    def test_get_mapped_passes_depth_through(self):
        coordinator = make_mapped_coordinator()
        coordinator._mapping.Get.return_value = _mapped_response(Id="k")

        coordinator.get_mapped("k", depth=2)

        request = coordinator._mapping.Get.call_args[0][0]
        assert request.depth == 2
        assert request.key == "k"

    def test_post_mapped_returns_entity_hydrated_from_data(self):
        coordinator = make_mapped_coordinator()
        coordinator._mapping.Post.return_value = _mapped_response(Id="server-assigned")
        entity = CoordArticle()
        entity.title = "Hello"

        result = coordinator.post_mapped(entity)

        assert result.id == "server-assigned"

    def test_update_mapped_sends_the_key_it_was_given(self):
        coordinator = make_mapped_coordinator()
        coordinator._mapping.Update.return_value = _mapped_response(Id="k1")
        entity = CoordArticle()
        entity.id = "k1"
        entity.title = "Hello"

        coordinator.update_mapped(entity)

        request = coordinator._mapping.Update.call_args[0][0]
        assert request.payload.fields["Id"].string_value == "k1"


# ── Acting-user identity resolution ─────────────────────────────────────────────

class TestEntityCoordinatorActingUserIdentity:
    def test_a_per_call_bound_token_takes_precedence_over_the_ambient_default(self):
        """with_acting_user's bound token wins over whatever ambient token the
        coordinator was constructed with."""
        channel = grpc.insecure_channel("localhost:1")
        coordinator = EntityCoordinator(CoordArticle, channel, "ambient-token")
        coordinator._retrieval = MagicMock()
        coordinator._retrieval.Get.return_value = retrieval_pb.RetrievalResponse(found=False)

        bound = coordinator.with_acting_user("bound-token")
        bound.get("some-id")

        sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
        assert sent_metadata == (("x-acting-user-authorization", "Bearer bound-token"),)

    def test_the_clients_ambient_identity_applies_when_nothing_is_bound(self):
        channel = grpc.insecure_channel("localhost:1")
        coordinator = EntityCoordinator(CoordArticle, channel, "ambient-token")
        coordinator._retrieval = MagicMock()
        coordinator._retrieval.Get.return_value = retrieval_pb.RetrievalResponse(found=False)

        coordinator.get("some-id")

        sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
        assert sent_metadata == (("x-acting-user-authorization", "Bearer ambient-token"),)

    def test_no_token_anywhere_emits_no_acting_user_header(self):
        channel = grpc.insecure_channel("localhost:1")
        coordinator = EntityCoordinator(CoordArticle, channel)
        coordinator._retrieval = MagicMock()
        coordinator._retrieval.Get.return_value = retrieval_pb.RetrievalResponse(found=False)

        coordinator.get("some-id")

        sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
        assert sent_metadata == ()

    def test_an_empty_string_token_still_emits_the_header_and_fails_loudly(self):
        """An empty-string token is a caller error, not "no identity": it must still
        produce a `Bearer ` header (with an empty token) so the server rejects the
        call with Unauthenticated, rather than being swallowed into rule 4 (no header,
        silent unauthenticated read)."""
        channel = grpc.insecure_channel("localhost:1")
        coordinator = EntityCoordinator(CoordArticle, channel, "")
        coordinator._retrieval = MagicMock()
        coordinator._retrieval.Get.return_value = retrieval_pb.RetrievalResponse(found=False)

        coordinator.get("some-id")

        sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
        assert sent_metadata == (("x-acting-user-authorization", "Bearer "),)

    def test_with_acting_user_does_not_mutate_the_receiver(self):
        """The non-mutation test: with_acting_user must return a new bound coordinator,
        never rebind the receiver it was called on."""
        channel = grpc.insecure_channel("localhost:1")
        coordinator = EntityCoordinator(CoordArticle, channel, "ambient-token")
        coordinator._retrieval = MagicMock()
        coordinator._retrieval.Get.return_value = retrieval_pb.RetrievalResponse(found=False)

        bound = coordinator.with_acting_user("bound-token")
        assert bound is not coordinator

        coordinator.get("some-id")

        sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
        assert sent_metadata == (("x-acting-user-authorization", "Bearer ambient-token"),)


# ── Related-object hydration on the read path ────────────────────────────────────

@iverson_entity
class HydTag:
    id: str = iverson_key()
    label: str = None


@iverson_entity
class HydAuthor:
    id: str = iverson_key()
    name: str = None
    hyd_articles: list = one_to_many("HydArticle")


@iverson_entity
class HydArticle:
    id: str = iverson_key()
    title: str = None
    hyd_author_id: str = many_to_one("HydAuthor")
    # A many_to_many and a one_to_one to the SAME related type, mirroring the
    # conformance model's py_tag_ids/py_tag_id pair: derivation must land the
    # many_to_many on the plural "hyd_tags" and the one_to_one on the singular
    # "hyd_tag" without one clobbering the other.
    hyd_tag_ids: str = many_to_many("HydTag")
    hyd_tag_id: str = one_to_one("HydTag")


@iverson_entity
class HydUnregisteredArticle:
    id: str = iverson_key()
    hyd_ghost_id: str = many_to_one("HydGhostNeverRegistered")


@iverson_entity
class HydCollisionArticle:
    """``hyd_author`` is BOTH a declared annotated field and the name
    ``hyd_author_id``'s many_to_one relation would derive — a model error."""

    id: str = iverson_key()
    hyd_author_id: str = many_to_one("HydAuthor")
    hyd_author: str = None


def _nested_struct(**fields) -> struct_pb2.Struct:
    s = struct_pb2.Struct()
    for name, value in fields.items():
        s.fields[name].string_value = value
    return s


class TestRelationHydration:
    def test_many_to_one_hydrates_a_typed_instance_on_the_derived_singular_member(self):
        coordinator = make_coordinator_for(HydArticle)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "art-1"
        s.fields["Title"].string_value = "Hello"
        s.fields["HydAuthorId"].string_value = "auth-1"
        s.fields["HydAuthor"].struct_value.CopyFrom(_nested_struct(Id="auth-1", Name="Ada"))

        restored = coordinator._from_struct(s)

        assert isinstance(restored.hyd_author, HydAuthor)
        assert restored.hyd_author.id == "auth-1"
        assert restored.hyd_author.name == "Ada"

    def test_many_to_many_hydrates_typed_instances_on_the_plural_member(self):
        coordinator = make_coordinator_for(HydArticle)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "art-1"
        tags_list = s.fields["HydTags"].list_value
        tags_list.values.add().struct_value.CopyFrom(_nested_struct(Id="tag-1", Label="a"))
        tags_list.values.add().struct_value.CopyFrom(_nested_struct(Id="tag-2", Label="b"))

        restored = coordinator._from_struct(s)

        assert [t.id for t in restored.hyd_tags] == ["tag-1", "tag-2"]
        assert all(isinstance(t, HydTag) for t in restored.hyd_tags)

    def test_one_to_one_hydrates_a_typed_instance_on_the_singular_member_without_colliding(self):
        coordinator = make_coordinator_for(HydArticle)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "art-1"
        s.fields["HydTag"].struct_value.CopyFrom(_nested_struct(Id="tag-9", Label="solo"))
        tags_list = s.fields["HydTags"].list_value
        tags_list.values.add().struct_value.CopyFrom(_nested_struct(Id="tag-1", Label="a"))

        restored = coordinator._from_struct(s)

        assert isinstance(restored.hyd_tag, HydTag)
        assert restored.hyd_tag.id == "tag-9"
        # the many_to_many plural member is unaffected by the one_to_one singular one
        assert [t.id for t in restored.hyd_tags] == ["tag-1"]

    def test_one_to_many_hydrates_typed_instances_in_the_declared_navigation_member(self):
        coordinator = make_coordinator_for(HydAuthor)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "auth-1"
        s.fields["Name"].string_value = "Ada"
        articles_list = s.fields["HydArticles"].list_value
        articles_list.values.add().struct_value.CopyFrom(_nested_struct(Id="art-1", Title="First"))
        articles_list.values.add().struct_value.CopyFrom(_nested_struct(Id="art-2", Title="Second"))

        restored = coordinator._from_struct(s)

        assert all(isinstance(a, HydArticle) for a in restored.hyd_articles)
        assert [a.title for a in restored.hyd_articles] == ["First", "Second"]

    def test_unregistered_related_type_falls_back_to_untyped_child(self):
        coordinator = make_coordinator_for(HydUnregisteredArticle)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "art-1"
        s.fields["HydGhost"].struct_value.CopyFrom(_nested_struct(Id="ghost-1"))

        restored = coordinator._from_struct(s)

        assert restored.hyd_ghost == {"Id": "ghost-1"}

    def test_derived_member_colliding_with_a_declared_field_raises(self):
        coordinator = make_coordinator_for(HydCollisionArticle)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "art-1"
        s.fields["HydAuthorId"].string_value = "auth-1"

        import pytest
        with pytest.raises(ValueError, match="HydCollisionArticle.*hyd_author"):
            coordinator._from_struct(s)

    def test_write_payload_after_hydration_carries_fk_and_not_the_hydrated_member(self):
        """The round-trip guarantee: hydrating ``hyd_author``/``hyd_tags``/``hyd_tag``
        onto an instance dynamically (not through ``__annotations__``) must not leak
        into a subsequent write, since ``_entity_to_struct`` iterates only
        ``__annotations__`` and these members were never declared there."""
        coordinator = make_coordinator_for(HydArticle)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "art-1"
        s.fields["Title"].string_value = "Hello"
        s.fields["HydAuthorId"].string_value = "auth-1"
        s.fields["HydAuthor"].struct_value.CopyFrom(_nested_struct(Id="auth-1", Name="Ada"))
        tags_list = s.fields["HydTags"].list_value
        tags_list.values.add().struct_value.CopyFrom(_nested_struct(Id="tag-1", Label="a"))

        restored = coordinator._from_struct(s)
        assert isinstance(restored.hyd_author, HydAuthor)

        payload = _entity_to_struct(restored)

        assert payload.fields["HydAuthorId"].string_value == "auth-1"
        assert "HydAuthor" not in payload.fields
        assert "HydTags" not in payload.fields


# ── C1 regression: many_to_many member whose name doesn't end in "_ids" ─────────
#
# many_to_many is not subject to naming enforcement (the wire FK column comes from
# _infer_fk(kind, related_type, ...), not from the member name), so a many_to_many member
# named e.g. "contributors" is legal. _relation_nav_member_name's fallback then returns the
# field unchanged, which must NOT be treated as a "suffix was stripped" derivation, or the read
# path raises ValueError on every read of such a type.

@iverson_entity
class M2mNoSuffixAuthor:
    id: str = iverson_key()


@iverson_entity
class M2mNoSuffixArticle:
    id: str = iverson_key()
    contributors: list = many_to_many("M2mNoSuffixAuthor")


class TestC1ManyToManyMemberWithoutIdsSuffix:
    def test_read_does_not_raise_and_does_not_invent_a_nav_member(self):
        coordinator = make_coordinator_for(M2mNoSuffixArticle)
        s = struct_pb2.Struct()
        s.fields["Id"].string_value = "art-1"
        contributors_list = s.fields["Contributors"].list_value
        contributors_list.values.add().struct_value.CopyFrom(_nested_struct(Id="a1"))
        contributors_list.values.add().struct_value.CopyFrom(_nested_struct(Id="a2"))

        restored = coordinator._from_struct(s)

        # The declared field is overwritten in place by the hydrated typed instances — no
        # separate/invented nav member, and no collision ValueError.
        assert [a.id for a in restored.contributors] == ["a1", "a2"]
        assert all(isinstance(a, M2mNoSuffixAuthor) for a in restored.contributors)
        assert not hasattr(restored, "contributor")
