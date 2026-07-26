"""Tests for EntityCoordinator's search-family execution methods (search, search_similar,
group_by, aggregate, search_chunks, pipeline). No existing EntityCoordinator execution
tests existed before this task (only builder tests) — this file establishes that pattern,
following test_schema_registrar.py's mocking convention."""
from __future__ import annotations

from unittest.mock import MagicMock

import grpc
from google.protobuf import struct_pb2

from iverson_client.annotations import iverson_entity, iverson_key
from iverson_client.core import EntityCoordinator
from iverson_client.generated import (
    object_search_pb2 as pb,
    object_search_pb2_grpc as search_grpc,
)


# ── Test entity ────────────────────────────────────────────────────────────────

@iverson_entity
class CoordArticle:
    id: str = iverson_key()
    title: str = None


# ── Fixtures ───────────────────────────────────────────────────────────────────

def make_coordinator() -> EntityCoordinator:
    # A bare insecure_channel never actually connects until an RPC is issued, so this
    # is safe to use purely to satisfy stub construction; _search is swapped for a mock
    # immediately after, since no real server is available in this test.
    channel = grpc.insecure_channel("localhost:1")
    coordinator = EntityCoordinator(CoordArticle, channel)
    coordinator._search = MagicMock()
    return coordinator


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
        assert results[0].id == "1"
        assert results[0].title == "Hello"

    def test_search_similar_converts_rows_via_from_struct(self):
        coordinator = make_coordinator()
        coordinator._search.SearchSimilar.return_value = iter([_row("2", "World")])

        results = coordinator.search_similar(
            pb.SearchSimilarRequest(type_name="CoordArticle", property="Title", query="hi")
        )

        assert len(results) == 1
        assert results[0].id == "2"
        assert results[0].title == "World"

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
