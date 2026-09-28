"""
Fluent row-pattern-matching builder that compiles to a ``MatchPatternRequest`` proto.

Pattern and expression strings pass through unchanged; the server validates them.
``build()`` never needs a live server.

Usage:
    request = (
        match_pattern("Order")
        .where("Region", pb.EQUALS, "EU")
        .partition_by("CustomerId")
        .order_by("PlacedAt")
        .pattern("A B+")
        .define("B", "Total > PREV(Total)")
        .measure("n", "COUNT(*)")
        .build()
    )
"""
from __future__ import annotations

from typing import Optional

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value


class MatchPatternBuilder:
    """Fluent DSL builder that compiles to a ``MatchPatternRequest`` proto message.
    Instantiate via the module-level ``match_pattern(type_name)`` factory."""

    def __init__(self, type_name: str) -> None:
        self._type_name = type_name
        self._source = _pb.TYPE_ROWS
        self._chunk_property = ""
        self._where: list[_pb.SearchClause] = []
        self._where_logic = _pb.AND
        self._partition_by: list[str] = []
        self._order_by: list[_pb.SearchSort] = []
        self._pattern = ""
        self._subsets: list[_pb.PatternSubset] = []
        self._define: list[_pb.NamedExpr] = []
        self._measures: list[_pb.NamedExpr] = []
        self._rows_per_match = _pb.ONE_ROW
        self._after_match: Optional[_pb.AfterMatchSkip] = None
        self._limit = 0

    def chunks(self, chunk_property: str) -> "MatchPatternBuilder":
        """Match over the chunks of an ``[IversonChunk]`` property instead of the type's rows."""
        self._source = _pb.CHUNKS
        self._chunk_property = chunk_property
        return self

    def where(self, field: str, op: int, value: object) -> "MatchPatternBuilder":
        return self._add_clause(field, op, value, _pb.FILTER)

    def not_(self, field: str, op: int, value: object) -> "MatchPatternBuilder":
        return self._add_clause(field, op, value, _pb.MUST_NOT)

    def with_logic(self, logic: int) -> "MatchPatternBuilder":
        self._where_logic = logic
        return self

    def partition_by(self, *fields: str) -> "MatchPatternBuilder":
        self._partition_by.extend(fields)
        return self

    def order_by(self, field: str, descending: bool = False) -> "MatchPatternBuilder":
        self._order_by.append(_pb.SearchSort(property=field, descending=descending))
        return self

    def pattern(self, pattern: str) -> "MatchPatternBuilder":
        self._pattern = pattern
        return self

    def subset(self, name: str, *variables: str) -> "MatchPatternBuilder":
        self._subsets.append(_pb.PatternSubset(name=name, variables=variables))
        return self

    def define(self, variable: str, expr: str) -> "MatchPatternBuilder":
        self._define.append(_pb.NamedExpr(name=variable, expr=expr))
        return self

    def measure(self, name: str, expr: str) -> "MatchPatternBuilder":
        self._measures.append(_pb.NamedExpr(name=name, expr=expr))
        return self

    def rows_per_match(self, mode: int) -> "MatchPatternBuilder":
        self._rows_per_match = mode
        return self

    def after_match(self, kind: int, variable: str = "") -> "MatchPatternBuilder":
        self._after_match = _pb.AfterMatchSkip(kind=kind, variable=variable)
        return self

    def limit(self, n: int) -> "MatchPatternBuilder":
        self._limit = n
        return self

    def build(self, trace_id: str = "") -> _pb.MatchPatternRequest:
        return _pb.MatchPatternRequest(
            type_name=self._type_name,
            source=self._source,
            chunk_property=self._chunk_property,
            where=self._where,
            where_logic=self._where_logic,
            partition_by=self._partition_by,
            order_by=self._order_by,
            pattern=self._pattern,
            subsets=self._subsets,
            define=self._define,
            measures=self._measures,
            rows_per_match=self._rows_per_match,
            after_match=self._after_match,
            limit=self._limit,
            trace_id=trace_id)

    def _add_clause(self, field: str, op: int, value: object,
                    clause_type: int) -> "MatchPatternBuilder":
        self._where.append(_pb.SearchClause(
            property=field, operator=op,
            value=_to_search_value(value), clause_type=clause_type))
        return self


def match_pattern(type_name: str) -> MatchPatternBuilder:
    """Start a fluent row-pattern match for the given entity type."""
    return MatchPatternBuilder(type_name)
