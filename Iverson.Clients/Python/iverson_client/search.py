"""
Fluent query builder that compiles to a ``SearchRequest`` proto.

Usage:
    request = (
        QueryBuilder("Article")
        .where("category").eq("tech")
        .where("word_count").gte(500)
        .order_by("published_at", descending=True)
        .limit(20)
        .build()
    )
"""
from __future__ import annotations

from typing import Any, List

from iverson_client.generated import object_search_pb2 as _pb


# ── Operator constants (mirror SearchOperator enum) ────────────────────────────

class SearchOperator:
    EQUALS                 = _pb.EQUALS
    NOT_EQUALS             = _pb.NOT_EQUALS
    CONTAINS               = _pb.CONTAINS
    STARTS_WITH            = _pb.STARTS_WITH
    ENDS_WITH              = _pb.ENDS_WITH
    GREATER_THAN           = _pb.GREATER_THAN
    LESS_THAN              = _pb.LESS_THAN
    GREATER_THAN_OR_EQUALS = _pb.GREATER_THAN_OR_EQUALS
    LESS_THAN_OR_EQUALS    = _pb.LESS_THAN_OR_EQUALS
    IN                     = _pb.IN
    VECTOR_SIMILAR         = _pb.VECTOR_SIMILAR


# ── Value conversion helper ────────────────────────────────────────────────────

def _to_search_value(value: Any) -> _pb.SearchValue:
    if value is None:
        return _pb.SearchValue()
    if isinstance(value, bool):
        return _pb.SearchValue(bool_val=value)
    if isinstance(value, str):
        return _pb.SearchValue(string_val=value)
    if isinstance(value, (int, float)):
        return _pb.SearchValue(number_val=float(value))
    if isinstance(value, list) and value and isinstance(value[0], float):
        return _pb.SearchValue(float_list=_pb.RepeatedFloat(values=value))
    if isinstance(value, list):
        # Treat as string list (IN operator)
        return _pb.SearchValue(string_list=_pb.RepeatedString(values=[str(v) for v in value]))
    # Fallback: stringify
    return _pb.SearchValue(string_val=str(value))


# ── FieldCondition (returned by QueryBuilder.where()) ─────────────────────────

class FieldCondition:
    """Represents a pending clause for a specific field. Call an operator
    method to add the clause to the parent ``QueryBuilder``."""

    def __init__(self, builder: "QueryBuilder", field: str,
                 clause_type: int = _pb.FILTER) -> None:
        self._builder = builder
        self._field = field
        self._clause_type = clause_type

    def _add(self, operator: int, value: Any) -> "QueryBuilder":
        clause = _pb.SearchClause(
            property=self._field,
            operator=operator,
            value=_to_search_value(value),
            clause_type=self._clause_type,
        )
        self._builder._clauses.append(clause)
        return self._builder

    # Operator methods ─────────────────────────────────────────────────────────

    def eq(self, value: Any) -> "QueryBuilder":
        """EQUALS"""
        return self._add(_pb.EQUALS, value)

    def neq(self, value: Any) -> "QueryBuilder":
        """NOT_EQUALS"""
        return self._add(_pb.NOT_EQUALS, value)

    def gt(self, value: Any) -> "QueryBuilder":
        """GREATER_THAN"""
        return self._add(_pb.GREATER_THAN, value)

    def gte(self, value: Any) -> "QueryBuilder":
        """GREATER_THAN_OR_EQUALS"""
        return self._add(_pb.GREATER_THAN_OR_EQUALS, value)

    def lt(self, value: Any) -> "QueryBuilder":
        """LESS_THAN"""
        return self._add(_pb.LESS_THAN, value)

    def lte(self, value: Any) -> "QueryBuilder":
        """LESS_THAN_OR_EQUALS"""
        return self._add(_pb.LESS_THAN_OR_EQUALS, value)

    def contains(self, value: str) -> "QueryBuilder":
        """CONTAINS"""
        return self._add(_pb.CONTAINS, value)

    def starts_with(self, value: str) -> "QueryBuilder":
        """STARTS_WITH"""
        return self._add(_pb.STARTS_WITH, value)

    def ends_with(self, value: str) -> "QueryBuilder":
        """ENDS_WITH"""
        return self._add(_pb.ENDS_WITH, value)

    def in_(self, values: List[str]) -> "QueryBuilder":
        """IN — accepts a list of strings."""
        clause = _pb.SearchClause(
            property=self._field,
            operator=_pb.IN,
            value=_pb.SearchValue(string_list=_pb.RepeatedString(values=values)),
            clause_type=self._clause_type,
        )
        self._builder._clauses.append(clause)
        return self._builder

    def vector_similar(self, query_vector: List[float]) -> "QueryBuilder":
        """VECTOR_SIMILAR — accepts a float list."""
        clause = _pb.SearchClause(
            property=self._field,
            operator=_pb.VECTOR_SIMILAR,
            value=_pb.SearchValue(float_list=_pb.RepeatedFloat(values=query_vector)),
            clause_type=self._clause_type,
        )
        self._builder._clauses.append(clause)
        return self._builder


# ── QueryBuilder ───────────────────────────────────────────────────────────────

class QueryBuilder:
    """Fluent DSL builder that compiles to a ``SearchRequest`` proto.

    Operators supported (matching ``SearchOperator`` enum exactly):
        eq, neq, contains, starts_with, ends_with, gt, lt, gte, lte, in_, vector_similar
    """

    def __init__(self, type_name: str) -> None:
        self._type_name = type_name
        self._clauses: list[_pb.SearchClause] = []
        self._sorts: list[_pb.SearchSort] = []
        self._fields: list[str] = []
        self._joins: list[_pb.JoinSpec] = []
        self._logic: int = _pb.AND
        self._page: int = 1
        self._page_size: int = 20

    # ── Clause entry points ────────────────────────────────────────────────────

    def where(self, field: str) -> FieldCondition:
        """Start a FILTER clause for the given field."""
        return FieldCondition(self, field, _pb.FILTER)

    def must(self, field: str) -> FieldCondition:
        """Start a MUST clause (required for a match)."""
        return FieldCondition(self, field, _pb.MUST)

    def should(self, field: str) -> FieldCondition:
        """Start a SHOULD clause (boosts score but not required)."""
        return FieldCondition(self, field, _pb.SHOULD)

    def must_not(self, field: str) -> FieldCondition:
        """Start a MUST_NOT clause (excludes matches)."""
        return FieldCondition(self, field, _pb.MUST_NOT)

    def fields(self, *names: str) -> "QueryBuilder":
        """Restrict the response to only the named fields. Empty (default) returns all fields."""
        self._fields.extend(names)
        return self

    # ── Joins ──────────────────────────────────────────────────────────────────

    def join(self, left_field: str, right_type: str, right_field: str,
              kind: int = _pb.JoinKind.INNER) -> "QueryBuilder":
        """Add a join from this type to ``right_type`` on the given fields."""
        self._joins.append(_pb.JoinSpec(
            left_type=self._type_name,
            right_type=right_type,
            left_field=left_field,
            right_field=right_field,
            kind=kind,
        ))
        return self

    # ── Sorting and paging ─────────────────────────────────────────────────────

    def order_by(self, field: str, descending: bool = False) -> "QueryBuilder":
        """Add a sort clause."""
        self._sorts.append(_pb.SearchSort(property=field, descending=descending))
        return self

    def order_by_desc(self, field: str) -> "QueryBuilder":
        """Add a descending sort clause."""
        return self.order_by(field, descending=True)

    def limit(self, n: int) -> "QueryBuilder":
        """Set the page size."""
        self._page_size = n
        return self

    def offset(self, page: int) -> "QueryBuilder":
        """Set the 1-based page number."""
        self._page = page
        return self

    def with_logic(self, logic: int) -> "QueryBuilder":
        """Set the top-level clause logic (AND / OR). Default: AND."""
        self._logic = logic
        return self

    # ── Build ──────────────────────────────────────────────────────────────────

    def build(self) -> _pb.SearchRequest:
        """Compile to a ``SearchRequest`` proto message."""
        query = _pb.SearchQuery(
            clauses=self._clauses,
            logic=self._logic,
            sort=self._sorts,
        )
        return _pb.SearchRequest(
            type_name=self._type_name,
            query=query,
            page=self._page,
            page_size=self._page_size,
            fields=self._fields,
            joins=self._joins,
        )


# ── GroupByBuilder factory ──────────────────────────────────────────────────────

def group_by(type_name: str) -> "GroupByBuilder":
    """Start a ``GroupByBuilder`` for the given entity type."""
    from iverson_client.group_by import GroupByBuilder
    return GroupByBuilder(type_name)
