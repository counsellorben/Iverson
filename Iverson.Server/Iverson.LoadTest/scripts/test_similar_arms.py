"""pytest suite for similar_arms.py's pure functions."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import similar_arms  # noqa: E402


def test_compose_query_applies_the_prefix_verbatim():
    assert similar_arms.compose_query("Represent this sentence for searching relevant passages: ", "q") == \
        "Represent this sentence for searching relevant passages: q"


def test_rank_hits_reads_docid_and_keeps_qdrant_order():
    hits = [{"id": 1, "score": 0.9, "payload": {"docId": "A"}}, {"id": 2, "score": 0.8, "payload": {"docId": "B"}}]
    assert similar_arms.rank_hits(hits, "q1", 2) == [("A", 0.9), ("B", 0.8)]


def test_rank_hits_fails_loud_on_missing_docid_or_short_list():
    with pytest.raises(SystemExit):
        similar_arms.rank_hits([{"id": 1, "score": 0.9, "payload": {}}], "q1", 1)
    with pytest.raises(SystemExit):
        similar_arms.rank_hits([{"id": 1, "score": 0.9, "payload": {"docId": "A"}}], "q1", 2)


def _write_queries(run_dir, ids_and_texts):
    beir_dir = run_dir / "beir"
    beir_dir.mkdir()
    with open(beir_dir / "queries.jsonl", "w", encoding="utf-8") as f:
        for qid, text in ids_and_texts:
            f.write(f'{{"_id": "{qid}", "text": "{text}"}}\n')


def test_main_exits_on_empty_queries(tmp_path, monkeypatch):
    """multivector.py's cmd_query has `if not queries: sys.exit(...)`; similar_arms.py had no
    such guard and would silently write two empty .trec files and exit 0. Copy the sibling's
    guard shape."""
    _write_queries(tmp_path, [])
    monkeypatch.setattr(similar_arms.multivector, "require_collection", lambda name: None)
    monkeypatch.setattr(sys, "argv", [
        "similar_arms.py", "--run-dir", str(tmp_path), "--model", "m",
        "--embed-url", "http://x", "--query-prefix", "",
    ])
    with pytest.raises(SystemExit) as e:
        similar_arms.main()
    assert "no queries" in str(e.value)


def test_partial_run_leaves_both_arms_covering_the_same_queries(tmp_path, monkeypatch):
    """If the second arm's Qdrant search fails mid-query, the first arm's rows for that SAME
    query must not have already been committed to its accumulator -- otherwise the two
    finally-written .trec files end up covering different query sets (head-raw one query
    ahead of centroid-raw). Simulates: q1 succeeds on both arms; q2 succeeds on head-raw
    (processed first, per ARMS' declaration order) then fails on centroid-raw. Against the
    unfixed script (each arm's lines extended immediately after its own search, inside the
    per-arm loop) head-raw.similar.trec would contain q1 AND q2 while centroid-raw.similar.trec
    contains only q1."""
    _write_queries(tmp_path, [("q1", "a"), ("q2", "b")])
    monkeypatch.setattr(similar_arms.multivector, "require_collection", lambda name: None)
    monkeypatch.setattr(similar_arms.ingest, "embed", lambda text, model, prefix, url: [0.1, 0.2])

    hits = [{"id": i, "score": 1.0, "payload": {"docId": f"d{i}"}} for i in range(similar_arms.DOCUMENT_BUDGET)]
    calls = {"n": 0}

    def fake_qdrant_request(method, path, body):
        calls["n"] += 1
        # Call order: q1/head-raw (1), q1/centroid-raw (2), q2/head-raw (3), q2/centroid-raw (4).
        if calls["n"] == 4:
            return 500, {"error": "boom"}
        return 200, {"result": hits}

    monkeypatch.setattr(similar_arms.ingest, "qdrant_request", fake_qdrant_request)
    monkeypatch.setattr(sys, "argv", [
        "similar_arms.py", "--run-dir", str(tmp_path), "--model", "m",
        "--embed-url", "http://x", "--query-prefix", "",
    ])

    with pytest.raises(SystemExit) as e:
        similar_arms.main()
    assert "HTTP 500" in str(e.value)

    runs_dir = tmp_path / "runs"
    head_queries = {line.split()[0] for line in (runs_dir / "head-raw.similar.trec").read_text().splitlines()}
    centroid_queries = {line.split()[0] for line in (runs_dir / "centroid-raw.similar.trec").read_text().splitlines()}
    assert head_queries == centroid_queries == {"q1"}, (head_queries, centroid_queries)
