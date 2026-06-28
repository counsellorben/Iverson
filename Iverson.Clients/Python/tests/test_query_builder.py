"""Tests for QueryBuilder — verifies correct SearchRequest proto for each operator."""
from __future__ import annotations

import pytest

from iverson_client.search import QueryBuilder
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
        assert req.page == 1
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

    def test_must_clause(self):
        req = QueryBuilder("Article").must("category").eq("tech").build()
        assert req.query.clauses[0].clause_type == pb.MUST

    def test_should_clause(self):
        req = QueryBuilder("Article").should("category").eq("tech").build()
        assert req.query.clauses[0].clause_type == pb.SHOULD

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
