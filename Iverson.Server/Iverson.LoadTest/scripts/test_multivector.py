"""pytest suite for multivector.py's pure functions. Run with:

        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q

No Qdrant, no TEI: the request bodies are pinned by the spec's live probes (spec §10 rows 3-5),
so these tests cover the logic between them -- regrouping, parent resolution, collapse, TREC
formatting and the latency summary."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import multivector  # noqa: E402

# Live object point verified in the spec (§10 row 10): key -> point id.
KEY = "29beccc1-3d1c-577b-a9a2-114337e70200"
OID = 817174487868073


def chunk(parent, index, row):
    return {"id": 1, "vector": {"body_vector": row}, "payload": {"parent_id": parent, "chunk_index": str(index)}}


def test_group_rows_orders_by_integer_index_not_string():
    groups = multivector.group_rows([chunk(KEY, 10, [10.0]), chunk(KEY, 2, [2.0]), chunk(KEY, 9, [9.0])])
    assert groups == {KEY: [[2.0], [9.0], [10.0]]}


def test_group_rows_keeps_parents_apart():
    groups = multivector.group_rows([chunk("a", 0, [1.0]), chunk("b", 0, [2.0]), chunk("a", 1, [3.0])])
    assert groups == {"a": [[1.0], [3.0]], "b": [[2.0]]}


def test_resolve_parents_builds_points_from_the_object_point():
    points, missing = multivector.resolve_parents({KEY: [[1.0], [2.0]]}, {OID: {"key": KEY, "docId": "15830352"}})
    assert missing == []
    assert points == [{"id": OID, "vector": [[1.0], [2.0]],
                       "payload": {"key": KEY, "docId": "15830352", "chunk_count": 2}}]


def test_resolve_parents_reports_missing_object_point():
    points, missing = multivector.resolve_parents({KEY: [[1.0]]}, {})
    assert points == [] and missing == [KEY]


def test_collapse_keeps_max_not_first_seen():
    assert multivector.collapse_by_doc([("d1", 0.3), ("d1", 0.9), ("d2", 0.5)], 10) == [("d1", 0.9), ("d2", 0.5)]


def test_collapse_truncates_after_collapse_not_before():
    # Three rows, two docs: a truncate-before-collapse would keep only d1.
    assert multivector.collapse_by_doc([("d1", 0.9), ("d1", 0.8), ("d2", 0.7)], 2) == [("d1", 0.9), ("d2", 0.7)]


def test_collapse_sorts_descending_and_first_seen_wins_ties():
    assert multivector.collapse_by_doc([("d2", 0.5), ("d1", 0.5), ("d3", 0.6)], 10) == [("d3", 0.6), ("d2", 0.5), ("d1", 0.5)]


def test_trec_lines_match_trecrunwriter_format():
    assert multivector.trec_lines("1", [("15830352", 0.8123456), ("4983", 0.7)], "gte-multivector-raw") == [
        "1 Q0 15830352 1 0.812346 gte-multivector-raw",
        "1 Q0 4983 2 0.700000 gte-multivector-raw",
    ]


def test_summarize_latency():
    s = multivector.summarize_latency([5.0, 1.0, 3.0, 2.0, 4.0])
    assert s == {"n": 5, "p50_ms": 3.0, "p95_ms": 5.0, "mean_ms": 3.0}


def test_summarize_latency_rejects_empty():
    with pytest.raises(ValueError):
        multivector.summarize_latency([])


def test_rank_chunk_hits_collapses_through_the_parent_map():
    hits = [{"id": 1, "score": 0.9, "payload": {"parent_id": "ka"}},
            {"id": 2, "score": 0.8, "payload": {"parent_id": "kb"}},
            {"id": 3, "score": 0.95, "payload": {"parent_id": "ka"}}]
    assert multivector.rank_chunk_hits(hits, {"ka": "A", "kb": "B"}, 50) == [("A", 0.95), ("B", 0.8)]


def test_rank_chunk_hits_fails_loud_on_unknown_parent():
    with pytest.raises(SystemExit):
        multivector.rank_chunk_hits([{"id": 1, "score": 0.9, "payload": {"parent_id": "zz"}}], {}, 50)
