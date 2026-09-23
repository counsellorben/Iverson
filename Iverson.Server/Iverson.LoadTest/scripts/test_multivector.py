"""pytest suite for multivector.py's pure functions. Run with:

        python3 -m pytest Iverson.Server/Iverson.LoadTest/scripts/test_multivector.py -q

No Qdrant, no TEI: the request bodies are pinned by the spec's live probes (spec §10 rows 3-5),
so these tests cover the logic between them -- regrouping, parent resolution, collapse, TREC
formatting and the latency summary."""
import json
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
    # Asymmetric sample (n=10) so mean != median and the p95 index is discriminating -- a
    # symmetric sample lets mean->median and an index-formula change both survive undetected.
    # sorted:            [1.0, 1.0, 1.0, 1.0, 2.0, 3.0, 4.0, 5.0, 50.0, 100.0]
    # index:               0    1    2    3    4    5    6    7    8      9
    # pct(p) = ordered[min(n-1, int(round(p*(n-1))))], n-1 = 9:
    #   p50: round(0.5*9)  = round(4.5) = 4 (banker's rounding, 4 is even) -> ordered[4] = 2.0
    #   p95: round(0.95*9) = round(8.55) = 9                               -> ordered[9] = 100.0
    # mean: (1+1+1+1+2+3+4+5+50+100)/10 = 168/10 = 16.8
    s = multivector.summarize_latency([1.0, 1.0, 1.0, 1.0, 2.0, 3.0, 4.0, 5.0, 50.0, 100.0])
    assert s == {"n": 10, "p50_ms": 2.0, "p95_ms": 100.0, "mean_ms": 16.8}


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


def mvpoint(doc_id, score):
    return {"id": 1, "score": score, "payload": {"docId": doc_id}}


def test_rank_multivector_points_dedupes_by_docid_keeping_max():
    # Two points sharing a docId is what the gate warned would produce a malformed run.
    ranked = multivector.rank_multivector_points(
        [mvpoint("d1", 0.3), mvpoint("d2", 0.5), mvpoint("d1", 0.9)], 10)
    assert ranked == [("d1", 0.9), ("d2", 0.5)]


def test_rank_multivector_points_truncates_after_the_collapse():
    # Truncate-before-collapse would keep only d1.
    ranked = multivector.rank_multivector_points(
        [mvpoint("d1", 0.9), mvpoint("d1", 0.8), mvpoint("d2", 0.7)], 2)
    assert ranked == [("d1", 0.9), ("d2", 0.7)]


def test_existing_run_files_reports_only_the_two_run_files(tmp_path):
    runs = tmp_path / "runs"
    runs.mkdir()
    (runs / f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec").write_text("x")
    (runs / "unrelated.txt").write_text("x")
    found = multivector.existing_run_files(str(runs))
    assert [f.rsplit("/", 1)[-1] for f in found] == [f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec"]


def test_existing_run_files_empty_on_a_fresh_directory(tmp_path):
    assert multivector.existing_run_files(str(tmp_path)) == []


def test_tail_query_ids_are_the_queries_whose_value_differs():
    control = {"a": 1.0, "b": 0.5, "c": 0.25}
    arm = {"a": 1.0, "b": 0.75, "c": 0.25}
    assert multivector.tail_query_ids(control, arm) == ["b"]


def test_tail_query_ids_counts_a_query_missing_from_one_side():
    assert multivector.tail_query_ids({"a": 1.0, "b": 0.5}, {"a": 1.0}) == ["b"]


def test_probe_set_is_bulk_plus_tail_deduped_in_corpus_order():
    ids = ["q1", "q2", "q3", "q4", "q5"]
    assert multivector.probe_set(ids, ["q5", "q2"], 2) == ["q1", "q2", "q5"]


def test_probe_set_refuses_a_tail_id_absent_from_the_corpus():
    with pytest.raises(ValueError):
        multivector.probe_set(["q1", "q2"], ["q9"], 1)


def test_config_snapshot_extracts_ef_construct_and_indexing_threshold():
    info = {"config": {"hnsw_config": {"ef_construct": 512},
                        "optimizer_config": {"indexing_threshold": 20000}}}
    assert multivector.config_snapshot(info) == {"ef_construct": 512, "indexing_threshold": 20000}


def test_query_run_refusal_allows_a_fresh_directory(tmp_path):
    assert multivector.query_run_refusal(str(tmp_path)) is None


def test_query_run_refusal_blocks_when_no_sidecar_present(tmp_path):
    (tmp_path / f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec").write_text("x")
    (tmp_path / f"{multivector.MULTIVECTOR_RUN_LABEL}.chunks.trec").write_text("x")
    refusal = multivector.query_run_refusal(str(tmp_path))
    assert refusal is not None and "no raw-latency.json sidecar" in refusal


def test_query_run_refusal_blocks_a_completed_prior_run(tmp_path):
    (tmp_path / f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec").write_text("x")
    (tmp_path / f"{multivector.MULTIVECTOR_RUN_LABEL}.chunks.trec").write_text("x")
    (tmp_path / "raw-latency.json").write_text(json.dumps({"complete": True}))
    refusal = multivector.query_run_refusal(str(tmp_path))
    assert refusal is not None and "does not mark the prior run incomplete" in refusal


def test_query_run_refusal_blocks_a_sidecar_missing_the_complete_key(tmp_path):
    # A sidecar written before this flag existed has no "complete" key -- ambiguous, so this
    # must fail closed the same as an explicit complete=True.
    (tmp_path / f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec").write_text("x")
    (tmp_path / f"{multivector.MULTIVECTOR_RUN_LABEL}.chunks.trec").write_text("x")
    (tmp_path / "raw-latency.json").write_text(json.dumps({"queries": 300}))
    refusal = multivector.query_run_refusal(str(tmp_path))
    assert refusal is not None and "does not mark the prior run incomplete" in refusal


def test_query_run_refusal_allows_retry_of_an_incomplete_prior_run(tmp_path):
    # A run that died mid-loop still leaves both .trec files (write_outputs runs in a
    # finally) -- the identical retry command must not be locked out by its own failure.
    (tmp_path / f"{multivector.CHUNKS_RUN_LABEL}.chunks.trec").write_text("x")
    (tmp_path / f"{multivector.MULTIVECTOR_RUN_LABEL}.chunks.trec").write_text("x")
    (tmp_path / "raw-latency.json").write_text(json.dumps({"complete": False}))
    assert multivector.query_run_refusal(str(tmp_path)) is None


def test_mv_search_params_uses_hnsw_ef_when_not_exact():
    assert multivector.mv_search_params(False, 65) == {"hnsw_ef": 65}


def test_mv_search_params_exact_drops_hnsw_ef_entirely():
    # Not "a very large hnsw_ef": measured, HNSW over max_sim points never converges to exact
    # at any beam, so sending both keys would misdescribe what the arm ran.
    assert multivector.mv_search_params(True, 65) == {"exact": True}
