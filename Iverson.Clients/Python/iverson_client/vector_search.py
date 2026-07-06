"""
Fluent builders for Qdrant vector search (SearchSimilar/SearchChunks).
"""
from __future__ import annotations

from typing import Any, List, Optional

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value

_UNSUPPORTED_SIMILAR_OPERATORS = {
    _pb.CONTAINS, _pb.STARTS_WITH, _pb.ENDS_WITH, _pb.VECTOR_SIMILAR,
}


class SimilarBuilder:
    """Fluent builder for a ``SearchSimilarRequest`` (Qdrant vector similarity search).

    Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
    LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR are rejected.
    """

    def __init__(self, type_name: str, property: str) -> None:
        self._type_name = type_name
        self._property = property
        self._query = ""
        self._top_k = 10
        self._logic = _pb.AND
        self._filter: List[_pb.SearchClause] = []

    def text(self, query: str) -> "SimilarBuilder":
        self._query = query
        return self

    def top_k(self, top_k: int) -> "SimilarBuilder":
        self._top_k = top_k
        return self

    def with_logic(self, logic: int) -> "SimilarBuilder":
        self._logic = logic
        return self

    def where(self, field: str, op: int, value: Any) -> "SimilarBuilder":
        if op in _UNSUPPORTED_SIMILAR_OPERATORS:
            raise ValueError(
                f"Operator {op} is not supported by SearchSimilar filters. Supported operators: "
                "EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS, "
                "LESS_THAN_OR_EQUALS, IN.")
        self._filter.append(_pb.SearchClause(
            property=field, operator=op, value=_to_search_value(value), clause_type=_pb.FILTER))
        return self

    def build(self, trace_id: str = "") -> _pb.SearchSimilarRequest:
        return _pb.SearchSimilarRequest(
            type_name=self._type_name, property=self._property, query=self._query,
            top_k=self._top_k, filter=self._filter, filter_logic=self._logic, trace_id=trace_id)


class ChunksBuilder:
    """Fluent builder for a ``SearchChunksRequest``. At most one EQUALS filter on the PK property."""

    def __init__(self, type_name: str, property: str) -> None:
        self._type_name = type_name
        self._property = property
        self._query = ""
        self._top_k = 10
        self._filter: Optional[_pb.SearchClause] = None

    def text(self, query: str) -> "ChunksBuilder":
        self._query = query
        return self

    def top_k(self, top_k: int) -> "ChunksBuilder":
        self._top_k = top_k
        return self

    def where(self, field: str, op: int, value: Any) -> "ChunksBuilder":
        if op != _pb.EQUALS:
            raise ValueError(
                f"SearchChunks only supports an EQUALS filter on the primary-key property; got {op}.")
        if self._filter is not None:
            raise ValueError("SearchChunks supports at most one filter clause.")
        self._filter = _pb.SearchClause(
            property=field, operator=op, value=_to_search_value(value), clause_type=_pb.FILTER)
        return self

    def build(self, trace_id: str = "") -> _pb.SearchChunksRequest:
        filters = [self._filter] if self._filter is not None else []
        return _pb.SearchChunksRequest(
            type_name=self._type_name, property=self._property, query=self._query,
            top_k=self._top_k, filter=filters, trace_id=trace_id)


def similar(type_name: str, property: str) -> SimilarBuilder:
    """Start a ``SimilarBuilder`` for the given entity type and embedded property."""
    return SimilarBuilder(type_name, property)


def chunks(type_name: str, property: str) -> ChunksBuilder:
    """Start a ``ChunksBuilder`` for the given entity type and chunked property."""
    return ChunksBuilder(type_name, property)
