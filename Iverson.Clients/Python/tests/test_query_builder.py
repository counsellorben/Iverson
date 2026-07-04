"""Tests for QueryBuilder — verifies correct SearchRequest proto for each operator."""
from __future__ import annotations

import pytest

from iverson_client.search import QueryBuilder, group_by
from iverson_client.group_by import GroupByBuilder
from iverson_client.generated import object_search_pb2 as pb


class TestQueryBuilderBasics:
    def test_build_returns_search_request(self):
        req = QueryBuilder("Article").build()
        assert isinstance(req, pb.SearchRequest)

    def test_type_name_set(self):
        req = QueryBuilder("Article").build()
        assert req.type_name == "Article"

    def test_defaults(self):
        req = QueryBuilder("Article").build()
        assert req.page == 0
        assert req.page_size == 20

    def test_limit(self):
        req = QueryBuilder("Article").limit(50).build()
        assert req.page_size == 50

    def test_offset(self):
        req = QueryBuilder("Article").offset(3).build()
        assert req.page == 3

    def test_order_by_asc(self):
        req = QueryBuilder("Article").order_by("published_at").build()
        assert len(req.query.sort) == 1
        assert req.query.sort[0].property == "published_at"
        assert req.query.sort[0].descending is False

    def test_order_by_desc(self):
        req = QueryBuilder("Article").order_by_desc("published_at").build()
        assert req.query.sort[0].descending is True

    def test_order_by_with_descending_flag(self):
        req = QueryBuilder("Article").order_by("score", descending=True).build()
        assert req.query.sort[0].descending is True

    def test_no_clauses_by_default(self):
        req = QueryBuilder("Article").build()
        assert len(req.query.clauses) == 0

    def test_logic_default_and(self):
        req = QueryBuilder("Article").build()
        assert req.query.logic == pb.AND

    def test_logic_or(self):
        req = QueryBuilder("Article").with_logic(pb.OR).build()
        assert req.query.logic == pb.OR


class TestOperatorEq:
    def test_eq_string(self):
        req = QueryBuilder("Article").where("category").eq("tech").build()
        clause = req.query.clauses[0]
        assert clause.operator == pb.EQUALS
        assert clause.value.string_val == "tech"
        assert clause.property == "category"

    def test_eq_number(self):
        req = QueryBuilder("Article").where("word_count").eq(500).build()
        clause = req.query.clauses[0]
        assert clause.operator == pb.EQUALS
        assert clause.value.number_val == 500.0

    def test_eq_bool(self):
        req = QueryBuilder("Article").where("published").eq(True).build()
        clause = req.query.clauses[0]
        assert clause.value.bool_val is True


class TestOperatorNeq:
    def test_neq(self):
        req = QueryBuilder("Article").where("category").neq("spam").build()
        assert req.query.clauses[0].operator == pb.NOT_EQUALS
        assert req.query.clauses[0].value.string_val == "spam"


class TestOperatorGt:
    def test_gt(self):
        req = QueryBuilder("Article").where("word_count").gt(100).build()
        assert req.query.clauses[0].operator == pb.GREATER_THAN
        assert req.query.clauses[0].value.number_val == 100.0


class TestOperatorGte:
    def test_gte(self):
        req = QueryBuilder("Article").where("word_count").gte(500).build()
        assert req.query.clauses[0].operator == pb.GREATER_THAN_OR_EQUALS
        assert req.query.clauses[0].value.number_val == 500.0


class TestOperatorLt:
    def test_lt(self):
        req = QueryBuilder("Article").where("score").lt(0.5).build()
        assert req.query.clauses[0].operator == pb.LESS_THAN
        assert req.query.clauses[0].value.number_val == pytest.approx(0.5)


class TestOperatorLte:
    def test_lte(self):
        req = QueryBuilder("Article").where("score").lte(1.0).build()
        assert req.query.clauses[0].operator == pb.LESS_THAN_OR_EQUALS


class TestOperatorContains:
    def test_contains(self):
        req = QueryBuilder("Article").where("body").contains("python").build()
        assert req.query.clauses[0].operator == pb.CONTAINS
        assert req.query.clauses[0].value.string_val == "python"


class TestOperatorStartsWith:
    def test_starts_with(self):
        req = QueryBuilder("Article").where("title").starts_with("How to").build()
        assert req.query.clauses[0].operator == pb.STARTS_WITH
        assert req.query.clauses[0].value.string_val == "How to"


class TestOperatorEndsWith:
    def test_ends_with_produces_ends_with_operator(self):
        req = QueryBuilder("Article").where("title").ends_with("Guide").build()
        assert req.query.clauses[0].operator == pb.ENDS_WITH
        assert req.query.clauses[0].value.string_val == "Guide"


class TestOperatorIn:
    def test_in(self):
        req = QueryBuilder("Article").where("category").in_(["tech", "science"]).build()
        clause = req.query.clauses[0]
        assert clause.operator == pb.IN
        assert list(clause.value.string_list.values) == ["tech", "science"]

    def test_in_uses_string_list(self):
        req = QueryBuilder("Article").where("status").in_(["draft", "published"]).build()
        clause = req.query.clauses[0]
        assert clause.value.HasField("string_list")


class TestOperatorVectorSimilar:
    def test_vector_similar(self):
        vec = [0.1, 0.2, 0.3]
        req = QueryBuilder("Article").where("embedding").vector_similar(vec).build()
        clause = req.query.clauses[0]
        assert clause.operator == pb.VECTOR_SIMILAR
        assert list(clause.value.float_list.values) == pytest.approx(vec)

    def test_vector_uses_float_list(self):
        req = QueryBuilder("Article").where("emb").vector_similar([1.0, 2.0]).build()
        assert req.query.clauses[0].value.HasField("float_list")


class TestClauseTypes:
    def test_where_is_filter(self):
        req = QueryBuilder("Article").where("category").eq("tech").build()
        assert req.query.clauses[0].clause_type == pb.FILTER

    def test_must_not_clause(self):
        req = QueryBuilder("Article").must_not("category").eq("spam").build()
        assert req.query.clauses[0].clause_type == pb.MUST_NOT


class TestChaining:
    def test_multiple_clauses(self):
        req = (
            QueryBuilder("Article")
            .where("category").eq("tech")
            .where("word_count").gte(500)
            .where("published").eq(True)
            .build()
        )
        assert len(req.query.clauses) == 3

    def test_chaining_returns_query_builder(self):
        """Operator methods must return the QueryBuilder for further chaining."""
        qb = QueryBuilder("Article")
        result = qb.where("x").eq("y")
        assert isinstance(result, QueryBuilder)

    def test_complex_query(self):
        req = (
            QueryBuilder("Article")
            .where("category").in_(["tech", "ai"])
            .where("word_count").gte(200)
            .where("body").contains("machine learning")
            .order_by("published_at", descending=True)
            .limit(10)
            .offset(2)
            .build()
        )
        assert req.type_name == "Article"
        assert len(req.query.clauses) == 3
        assert req.page_size == 10
        assert req.page == 2
        assert req.query.sort[0].property == "published_at"
        assert req.query.sort[0].descending is True

    def test_multiple_sorts(self):
        req = (
            QueryBuilder("Article")
            .order_by("score", descending=True)
            .order_by("published_at")
            .build()
        )
        assert len(req.query.sort) == 2
        assert req.query.sort[0].property == "score"
        assert req.query.sort[1].property == "published_at"


class TestJoin:
    def test_join_adds_join_spec_to_search_request(self):
        req = (
            QueryBuilder("LineItem")
            .join("order_id", "Order", "id")
            .build()
        )
        assert len(req.joins) == 1
        join = req.joins[0]
        assert join.left_type == "LineItem"
        assert join.right_type == "Order"
        assert join.left_field == "order_id"
        assert join.right_field == "id"
        assert join.kind == pb.JoinKind.INNER

    def test_join_with_explicit_kind(self):
        req = (
            QueryBuilder("LineItem")
            .join("order_id", "Order", "id", kind=pb.JoinKind.LEFT)
            .build()
        )
        assert req.joins[0].kind == pb.JoinKind.LEFT

    def test_join_with_full_kind(self):
        req = (
            QueryBuilder("LineItem")
            .join("order_id", "Order", "id", kind=pb.JoinKind.FULL)
            .build()
        )
        assert req.joins[0].kind == pb.JoinKind.FULL


class TestGroupByBuilder:
    def test_keys_adds_group_by_fields(self):
        req = group_by("LineItem").keys("return_flag", "line_status").build()
        assert list(req.keys) == ["return_flag", "line_status"]

    def test_sum_adds_metric_with_auto_alias(self):
        req = group_by("LineItem").sum("quantity").build()
        assert len(req.metrics) == 1
        metric = req.metrics[0]
        assert metric.name == "quantity_sum"
        assert metric.type == pb.SUM
        assert metric.field == "quantity"

    def test_sum_expr_adds_raw_expression(self):
        req = group_by("LineItem").sum_expr("price * (1 - discount)", "revenue").build()
        assert len(req.metrics) == 1
        metric = req.metrics[0]
        assert metric.name == "revenue"
        assert metric.type == pb.SUM
        assert metric.expression == "price * (1 - discount)"
        assert metric.field == ""

    def test_count_all_produces_empty_field_metric(self):
        req = group_by("LineItem").count_all().build()
        assert len(req.metrics) == 1
        metric = req.metrics[0]
        assert metric.name == "count"
        assert metric.type == pb.COUNT
        assert metric.field == ""
        assert metric.expression == ""

    def test_having_adds_having_clause(self):
        req = (
            group_by("LineItem")
            .sum("quantity", "total_qty")
            .having("total_qty", pb.GREATER_THAN, 100)
            .build()
        )
        assert len(req.having.clauses) == 1
        clause = req.having.clauses[0]
        assert clause.property == "total_qty"
        assert clause.operator == pb.GREATER_THAN
        assert clause.clause_type == pb.FILTER
        assert clause.value.number_val == pytest.approx(100.0)

    def test_join_adds_join_spec(self):
        req = group_by("LineItem").join("order_id", "Order", "id").build()
        assert len(req.joins) == 1
        join = req.joins[0]
        assert join.left_type == "LineItem"
        assert join.right_type == "Order"
        assert join.left_field == "order_id"
        assert join.right_field == "id"
        assert join.kind == pb.JoinKind.INNER

    def test_join_with_full_kind(self):
        req = group_by("LineItem").join("order_id", "Order", "id", kind=pb.JoinKind.FULL).build()
        assert req.joins[0].kind == pb.JoinKind.FULL

    def test_build_sets_trace_id(self):
        req = group_by("LineItem").build(trace_id="trace-123")
        assert req.trace_id == "trace-123"

    def test_q1_style_all_fields_present(self):
        req = (
            group_by("LineItem")
            .where("ship_date", pb.LESS_THAN_OR_EQUALS, "1998-12-01")
            .keys("return_flag", "line_status")
            .sum("quantity", "sum_qty")
            .sum("extended_price", "sum_base_price")
            .sum_expr("extended_price * (1 - discount)", "sum_disc_price")
            .avg("quantity", "avg_qty")
            .count_all("count_order")
            .having("sum_qty", pb.GREATER_THAN, 0)
            .order_by("return_flag")
            .order_by_desc("line_status")
            .limit(500)
            .build(trace_id="q1-trace")
        )
        assert req.type_name == "LineItem"
        assert len(req.query.clauses) == 1
        assert req.query.clauses[0].property == "ship_date"
        assert len(req.keys) == 2
        assert len(req.metrics) == 5
        assert len(req.having.clauses) == 1
        assert len(req.order_by) == 2
        assert req.order_by[0].property == "return_flag"
        assert req.order_by[0].descending is False
        assert req.order_by[1].property == "line_status"
        assert req.order_by[1].descending is True
        assert req.limit == 500
        assert req.trace_id == "q1-trace"
