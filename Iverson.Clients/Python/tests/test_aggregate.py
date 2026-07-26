"""Tests for AggregateBuilder — bucket/metric construction and build-time validation."""
from __future__ import annotations

import pytest

from iverson_client import aggregate
from iverson_client.generated import object_search_pb2 as pb


# ── Buckets (TERMS / DATE_HISTOGRAM / RANGE) ────────────────────────────────────

def test_terms_adds_aggregation_spec():
    req = aggregate("Article").terms("Category", "by_category", size=5).build()
    assert len(req.aggregations) == 1
    spec = req.aggregations[0]
    assert spec.name == "by_category"
    assert spec.type == pb.TERMS
    assert spec.field == "Category"
    assert spec.size == 5


def test_terms_default_size():
    req = aggregate("Article").terms("Category", "by_category").build()
    assert req.aggregations[0].size == 10


def test_date_histogram_adds_aggregation_spec():
    req = (aggregate("Article")
           .date_histogram("PublishedAt", "by_month", "month", "America/New_York")
           .build())
    spec = req.aggregations[0]
    assert spec.type == pb.DATE_HISTOGRAM
    assert spec.field == "PublishedAt"
    assert spec.calendar_interval == "month"
    assert spec.time_zone == "America/New_York"


def test_date_histogram_default_time_zone():
    req = aggregate("Article").date_histogram("PublishedAt", "by_month", "month").build()
    assert req.aggregations[0].time_zone == ""


def test_range_adds_buckets_with_bounds():
    req = (aggregate("Article")
           .range("WordCount", "by_length", [("short", None, 500.0), ("long", 500.0, None)])
           .build())
    spec = req.aggregations[0]
    assert spec.type == pb.RANGE
    assert spec.field == "WordCount"
    assert len(spec.range_buckets) == 2

    short = spec.range_buckets[0]
    assert short.key == "short"
    assert short.HasField("from") is False
    assert short.to.value == 500.0

    long_ = spec.range_buckets[1]
    assert long_.key == "long"
    assert getattr(long_, "from").value == 500.0
    assert long_.HasField("to") is False


def test_range_bucket_without_key_is_empty_string():
    req = aggregate("Article").range("WordCount", "by_length", [(None, 0.0, 100.0)]).build()
    assert req.aggregations[0].range_buckets[0].key == ""


# ── Metrics (AVG / SUM / MIN / MAX / COUNT / COUNT_ALL) ─────────────────────────

def test_avg_sum_min_max_count_and_count_all():
    req = (aggregate("Article")
           .avg("WordCount", "avg_wc")
           .sum("WordCount", "sum_wc")
           .min("WordCount", "min_wc")
           .max("WordCount", "max_wc")
           .count("WordCount", "count_wc")
           .count_all("total")
           .build())
    by_name = {a.name: a for a in req.aggregations}
    assert by_name["avg_wc"].type == pb.AVG
    assert by_name["sum_wc"].type == pb.SUM
    assert by_name["min_wc"].type == pb.MIN
    assert by_name["max_wc"].type == pb.MAX
    assert by_name["count_wc"].type == pb.COUNT
    assert by_name["total"].type == pb.COUNT
    assert by_name["total"].field == ""


# ── WHERE / HAVING / JOIN ────────────────────────────────────────────────────────

def test_where_adds_filter_clause():
    req = aggregate("Article").where("Category", pb.EQUALS, "tech").count_all("n").build()
    assert req.query.clauses[0].property == "Category"
    assert req.query.clauses[0].clause_type == pb.FILTER


def test_not_adds_must_not_clause():
    req = aggregate("Article").not_("Category", pb.EQUALS, "spam").count_all("n").build()
    assert req.query.clauses[0].clause_type == pb.MUST_NOT


def test_with_logic_or_is_carried():
    req = (aggregate("Article").where("A", pb.EQUALS, 1).where("B", pb.EQUALS, 2)
           .with_logic(pb.OR).count_all("n").build())
    assert req.query.logic == pb.OR


def test_having_adds_clause():
    req = aggregate("Article").count_all("n").having("n", pb.GREATER_THAN, 5).build()
    assert req.having.clauses[0].property == "n"
    assert req.having.clauses[0].clause_type == pb.FILTER


def test_with_having_logic_or_is_carried():
    req = (aggregate("Article").count_all("n")
           .having("n", pb.GREATER_THAN, 5).having("m", pb.LESS_THAN, 1)
           .with_having_logic(pb.OR).build())
    assert req.having.logic == pb.OR


def test_join_adds_join_spec():
    req = aggregate("Article").join("AuthorId", "Author", "Id").count_all("n").build()
    assert len(req.joins) == 1
    assert req.joins[0].left_type == "Article"
    assert req.joins[0].right_type == "Author"
    assert req.joins[0].left_field == "AuthorId"
    assert req.joins[0].right_field == "Id"
    assert req.joins[0].kind == pb.JoinKind.INNER


# ── Build validation ─────────────────────────────────────────────────────────────

def test_duplicate_aggregation_name_raises():
    b = aggregate("Article").sum("WordCount", "wc").sum("Price", "wc")
    with pytest.raises(ValueError, match="wc"):
        b.build()


def test_trace_id_passed_through():
    req = aggregate("Article").count_all("n").build("trace-123")
    assert req.trace_id == "trace-123"
