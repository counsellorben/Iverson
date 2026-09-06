from unittest.mock import MagicMock

import grpc
import pytest

from iverson_client import iverson_entity, iverson_key, iverson_metadata, iverson_chunk, iverson_summary
from iverson_client.generated import object_search_pb2 as pb
from iverson_agent.schema import ValidFilter
from iverson_agent.retrieval import (
    DocumentContext, RetrievalError, RetrievalUnavailable, assemble, chunks_request, locate,
    render_context,
)


@iverson_entity
class Doc:
    id: str = iverson_key()
    title: str = None
    source: str = iverson_metadata()
    published_at: str = iverson_metadata()
    body: str = iverson_chunk()
    summary: str = iverson_summary()


def chunk(parent, text, score):
    return pb.ChunkSearchResponse(parent_key=parent, chunk_text=text, score=score)


def rpc_error(code):
    err = grpc.RpcError()
    err.code = lambda: code
    err.details = lambda: str(code)
    return err


def test_chunks_request_carries_every_filter_with_canonical_names():
    req = chunks_request("Doc", "Body", "q", 20,
                         [ValidFilter("Source", "legal"), ValidFilter("PublishedAt", "2025-11-02")], "t")
    assert (req.type_name, req.property, req.query, req.top_k, req.trace_id) == ("Doc", "Body", "q", 20, "t")
    assert [(c.property, c.operator, c.clause_type) for c in req.filter] == [
        ("Source", pb.EQUALS, pb.FILTER), ("PublishedAt", pb.EQUALS, pb.FILTER)]
    assert req.filter[0].value.string_val == "legal"
    assert req.filter_logic == pb.AND


def test_locate_groups_by_parent_ranks_by_max_and_takes_k():
    coord = MagicMock()
    coord.search_chunks.return_value = [
        chunk("A", "a1", 0.3), chunk("B", "b1", 0.9), chunk("A", "a2", 0.8), chunk("C", "c1", 0.5)]
    parents, empty = locate(coord, "Doc", "Body", [("q", [])], k=2, fanout=4, trace_id="t")
    assert [p.key for p in parents] == ["B", "A"]
    assert parents[1].best_score == pytest.approx(0.8)
    assert [t for _, t in parents[1].chunks] == ["a2", "a1"]
    assert coord.search_chunks.call_args.args[0].top_k == 8
    assert empty == []


def test_locate_merges_queries_and_reports_empty_ones():
    coord = MagicMock()
    coord.search_chunks.side_effect = [[chunk("A", "a1", 0.4)], []]
    parents, empty = locate(coord, "Doc", "Body", [("q1", []), ("q2", [])], k=5, fanout=4, trace_id="t")
    assert [p.key for p in parents] == ["A"] and empty == ["q2"]


def test_locate_retries_once_without_filters_on_invalid_argument():
    coord = MagicMock()
    coord.search_chunks.side_effect = [rpc_error(grpc.StatusCode.INVALID_ARGUMENT), [chunk("A", "a", 0.1)]]
    parents, _ = locate(coord, "Doc", "Body", [("q", [ValidFilter("Source", "x")])], k=5, fanout=4, trace_id="t")
    assert [p.key for p in parents] == ["A"]
    assert coord.search_chunks.call_args_list[1].args[0].filter == []


def test_locate_maps_unavailable_and_unfiltered_invalid_argument():
    coord = MagicMock()
    coord.search_chunks.side_effect = rpc_error(grpc.StatusCode.UNAVAILABLE)
    with pytest.raises(RetrievalUnavailable):
        locate(coord, "Doc", "Body", [("q", [])], k=5, fanout=4, trace_id="t")
    coord.search_chunks.side_effect = rpc_error(grpc.StatusCode.INVALID_ARGUMENT)
    with pytest.raises(RetrievalError):
        locate(coord, "Doc", "Body", [("q", [])], k=5, fanout=4, trace_id="t")


def test_locate_skips_a_persistently_invalid_query_and_keeps_the_others():
    # §6 row 1: the filters were dropped and the retry recurred, so this ONE query is skipped
    # (reported as empty to the model) — the session is not failed while another query works.
    coord = MagicMock()
    coord.search_chunks.side_effect = [
        rpc_error(grpc.StatusCode.INVALID_ARGUMENT),        # q1, filtered
        rpc_error(grpc.StatusCode.INVALID_ARGUMENT),        # q1, unfiltered retry
        [chunk("A", "a1", 0.7)],                            # q2
    ]
    parents, empty = locate(coord, "Doc", "Body",
                            [("q1", [ValidFilter("Source", "x")]), ("q2", [])],
                            k=5, fanout=4, trace_id="t")
    assert [p.key for p in parents] == ["A"]
    assert empty == ["q1"]


def test_locate_fails_the_session_when_every_query_is_rejected():
    # §3.4 row 3 (masked chunk field) recurs on every query, so nothing is left to answer from.
    coord = MagicMock()
    coord.search_chunks.side_effect = rpc_error(grpc.StatusCode.INVALID_ARGUMENT)
    with pytest.raises(RetrievalError):
        locate(coord, "Doc", "Body", [("q1", []), ("q2", [])], k=5, fanout=4, trace_id="t")


def entity(key, title="T", summary="S"):
    e = object.__new__(Doc)
    e.id, e.title, e.source, e.published_at, e.body, e.summary = key, title, "legal", "2025-11-02", "B", summary
    return e


def test_assemble_hydrates_tops_up_thin_parents_and_keys_metadata_by_schema_name():
    coord = MagicMock()
    coord.get_many.return_value = [entity("A"), entity("B")]
    # Scores deliberately out of order relative to the existing passage (a1, 0.9): a-top1 is
    # lower and a-top2 is higher, so a naive append would leave the list unsorted and only the
    # post-top-up sort in `assemble` produces the "best first" order asserted below.
    coord.search_chunks.return_value = [chunk("A", "a-top1", 0.5), chunk("A", "a-top2", 0.95)]
    from iverson_agent.retrieval import RankedParent
    parents = [RankedParent("A", 0.9, [(0.9, "a1")]), RankedParent("B", 0.5, [(0.5, "b1"), (0.4, "b2"), (0.3, "b3")])]
    ctx = assemble(coord, Doc, "Doc", "Body", parents, "question", m=3, trace_id="t", title_field="title")
    assert [c.key for c in ctx] == ["A", "B"]
    assert [t for _, t in ctx[0].passages] == ["a-top2", "a1", "a-top1"]     # topped up, re-sorted best-first
    assert [t for _, t in ctx[1].passages] == ["b1", "b2", "b3"]             # already >= m: no call
    coord.search_chunks.assert_called_once()
    req = coord.search_chunks.call_args.args[0]
    assert req.query == "question" and req.top_k == 3
    assert (req.filter[0].property, req.filter[0].value.string_val) == ("Id", "A")
    assert ctx[0].metadata == {"Source": "legal", "PublishedAt": "2025-11-02"}
    assert ctx[0].summary == "S" and ctx[0].title == "T"


def test_assemble_drops_parents_get_many_did_not_return():
    coord = MagicMock()
    coord.get_many.return_value = [entity("A")]
    coord.search_chunks.return_value = []
    from iverson_agent.retrieval import RankedParent
    ctx = assemble(coord, Doc, "Doc", "Body", [RankedParent("A", 0.9, [(0.9, "a")] * 3), RankedParent("Z", 0.1, [])],
                   "q", m=3, trace_id="t", title_field="title")
    assert [c.key for c in ctx] == ["A"]


def test_render_context_numbers_documents_and_drops_lowest_passages_first():
    ctx = [DocumentContext("A", "TA", {"Source": "legal"}, "sum-A", [(0.9, "x" * 400), (0.2, "y" * 400)], 0.9),
           DocumentContext("B", "TB", {}, "sum-B", [(0.5, "z" * 400)], 0.5)]
    full = render_context(ctx, budget_tokens=10_000)
    assert "[doc 1]" in full and "[doc 2]" in full and "Source=legal" in full and "y" * 400 in full
    tight = render_context(ctx, budget_tokens=260)
    assert "y" * 400 not in tight and "x" * 400 in tight          # lowest score dropped first
    assert [t for _, t in ctx[0].passages] == ["x" * 400]         # pruned in place: citations see only the page
    tiny = render_context(ctx, budget_tokens=60)
    assert "sum-A" in tiny and "[doc 2]" in tiny                  # summary fallback; documents never dropped


def test_topup_invalid_argument_is_not_retried_without_its_pk_filter():
    # The top-up's only filter is the parent-key equality that scopes it to ONE document. Stage
    # 1's drop-all-filters retry must not apply here: an unfiltered retry would return the
    # question's best chunks from ANY document and merge them into this parent's context.
    coord = MagicMock()
    coord.get_many.return_value = [entity("A")]
    coord.search_chunks.side_effect = rpc_error(grpc.StatusCode.INVALID_ARGUMENT)
    from iverson_agent.retrieval import RankedParent
    with pytest.raises(RetrievalError):
        assemble(coord, Doc, "Doc", "Body", [RankedParent("A", 0.9, [(0.9, "a1")])],
                 "q", m=3, trace_id="t", title_field="title")
    coord.search_chunks.assert_called_once()
    assert [(c.property, c.value.string_val) for c in coord.search_chunks.call_args.args[0].filter] == [("Id", "A")]


def test_topup_request_is_built_like_a_stage_one_request():
    coord = MagicMock()
    coord.get_many.return_value = [entity("A")]
    coord.search_chunks.return_value = []
    from iverson_agent.retrieval import RankedParent
    assemble(coord, Doc, "Doc", "Body", [RankedParent("A", 0.9, [(0.9, "a1")])],
             "question", m=3, trace_id="t", title_field="title")
    req = coord.search_chunks.call_args.args[0]
    assert (req.type_name, req.property, req.query, req.top_k, req.trace_id) == ("Doc", "Body", "question", 3, "t")
    assert req.filter_logic == pb.AND and len(req.filter) == 1
