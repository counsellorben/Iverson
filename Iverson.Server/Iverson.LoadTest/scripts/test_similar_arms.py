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
