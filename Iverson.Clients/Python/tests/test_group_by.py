"""Tests for GroupByBuilder — Not/HavingLogic and build-time validation."""
from __future__ import annotations

import pytest

from iverson_client import group_by
from iverson_client.generated import object_search_pb2 as pb


def test_not_adds_must_not_clause():
    req = (group_by("Article").keys("Category").count_all("n")
           .not_("Category", pb.EQUALS, "spam").build())
    assert req.query.clauses[0].clause_type == pb.MUST_NOT


def test_with_having_logic_or_is_carried():
    req = (group_by("Article").keys("Category").count_all("n")
           .having("n", pb.GREATER_THAN, 5)
           .with_having_logic(pb.OR).build())
    assert req.having.logic == pb.OR


def test_duplicate_metric_alias_raises():
    b = group_by("Article").keys("Category").sum("WordCount").sum("WordCount")
    with pytest.raises(ValueError, match="WordCount_sum"):
        b.build()


def test_having_unknown_alias_raises():
    b = (group_by("Article").keys("Category").count_all("n")
         .having("misspelled", pb.GREATER_THAN, 5))
    with pytest.raises(ValueError, match="misspelled"):
        b.build()


def test_having_on_key_is_allowed():
    (group_by("Article").keys("Category").count_all("n")
     .having("Category", pb.EQUALS, "tech").build())


def test_order_by_unknown_alias_raises():
    b = group_by("Article").keys("Category").count_all("n").order_by("nope")
    with pytest.raises(ValueError, match="nope"):
        b.build()


def test_key_collides_with_metric_alias_raises():
    b = group_by("Article").keys("total").sum("Price", "total")
    with pytest.raises(ValueError):
        b.build()


def test_having_references_metric_alias_case_insensitive_is_allowed():
    b = (group_by("Article").keys("Category").sum("WordCount", "Total")
         .having("TOTAL", pb.GREATER_THAN, 100))
    b.build()  # should not raise


def test_order_by_references_key_case_insensitive_is_allowed():
    b = group_by("Article").keys("Category").count_all("n").order_by("CATEGORY")
    b.build()  # should not raise
