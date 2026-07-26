"""
Fluent aggregate builder that compiles to an ``AggregateRequest`` proto.

Unlike ``GroupByBuilder`` (one compound SELECT with all metrics as columns),
``AggregateRequest`` runs one bucketed/metric aggregation per ``AggregationSpec``
(TERMS/DATE_HISTOGRAM/RANGE buckets, or a single AVG/SUM/MIN/MAX/COUNT value),
with optional WHERE, HAVING, and JOIN — same shape as ``GroupByBuilder`` otherwise.

Usage:
    request = (
        aggregate("Article")
        .terms("category", "by_category", size=5)
        .avg("word_count", "avg_words")
        .where("is_published", SearchOperator.EQUALS, True)
        .build()
    )
"""
from __future__ import annotations

from typing import Optional, Sequence, Tuple

from google.protobuf.wrappers_pb2 import DoubleValue

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value


class AggregateBuilder:
    """Fluent DSL builder that compiles to an ``AggregateRequest`` proto message.

    Does not require a live server — ``build()`` simply returns the compiled
    proto. Instantiate via the module-level ``aggregate(type_name)`` factory.

    Note: ``AggregationSpec``'s ``group_by_fields``/``expression`` override
    fields (multi-key TERMS and raw-SQL-expression aggregations) are not
    covered by this builder.
    """

    def __init__(self, type_name: str) -> None:
        self._type_name = type_name
        self._aggregations: list[_pb.AggregationSpec] = []
        self._where: list[_pb.SearchClause] = []
        self._having: list[_pb.SearchClause] = []
        self._joins: list[_pb.JoinSpec] = []
        self._where_logic = _pb.AND
        self._having_logic = _pb.AND

    # ── WHERE filter (raw field strings, same value encoding as QueryBuilder) ──

    def where(self, field: str, op: int, value: object) -> "AggregateBuilder":
        """Add a WHERE (FILTER) clause."""
        self._where.append(_pb.SearchClause(
            property=field,
            operator=op,
            value=_to_search_value(value),
            clause_type=_pb.FILTER,
        ))
        return self

    def not_(self, field: str, op: int, value: object) -> "AggregateBuilder":
        """Add a MUST_NOT WHERE clause (excludes matches before aggregating)."""
        self._where.append(_pb.SearchClause(
            property=field,
            operator=op,
            value=_to_search_value(value),
            clause_type=_pb.MUST_NOT,
        ))
        return self

    def with_logic(self, logic: int) -> "AggregateBuilder":
        """Set the logic used to combine top-level WHERE clauses. Default: AND."""
        self._where_logic = logic
        return self

    # ── HAVING (references output alias names) ──────────────────────────────────

    def having(self, alias: str, op: int, value: object) -> "AggregateBuilder":
        """Add a HAVING clause. ``alias`` must match an aggregation's output name."""
        self._having.append(_pb.SearchClause(
            property=alias,
            operator=op,
            value=_to_search_value(value),
            clause_type=_pb.FILTER,
        ))
        return self

    def with_having_logic(self, logic: int) -> "AggregateBuilder":
        """Set the logic combining HAVING clauses. Default: AND."""
        self._having_logic = logic
        return self

    # ── JOIN ──────────────────────────────────────────────────────────────────

    def join(self, left_field: str, right_type: str, right_field: str,
              kind: int = _pb.JoinKind.INNER) -> "AggregateBuilder":
        """Add a join from this type to ``right_type`` on the given fields."""
        self._joins.append(_pb.JoinSpec(
            left_type=self._type_name,
            right_type=right_type,
            left_field=left_field,
            right_field=right_field,
            kind=kind,
        ))
        return self

    # ── Buckets ──────────────────────────────────────────────────────────────────

    def terms(self, field: str, name: str, size: int = 10) -> "AggregateBuilder":
        """Bucket by distinct values of ``field`` (up to ``size`` buckets)."""
        self._aggregations.append(_pb.AggregationSpec(
            name=name, type=_pb.TERMS, field=field, size=size,
        ))
        return self

    def date_histogram(self, field: str, name: str, calendar_interval: str,
                        time_zone: str = "") -> "AggregateBuilder":
        """Bucket a datetime ``field`` into calendar intervals (e.g. "day", "month")."""
        self._aggregations.append(_pb.AggregationSpec(
            name=name, type=_pb.DATE_HISTOGRAM, field=field,
            calendar_interval=calendar_interval, time_zone=time_zone,
        ))
        return self

    def range(self, field: str, name: str,
              buckets: Sequence[Tuple[Optional[str], Optional[float], Optional[float]]],
              ) -> "AggregateBuilder":
        """Bucket ``field`` into explicit ``(key, from, to)`` ranges. ``from``/``to`` of
        ``None`` means unbounded on that side."""
        range_buckets: list[_pb.RangeBucket] = []
        for key, lo, hi in buckets:
            kwargs: dict = {"key": key or ""}
            if lo is not None:
                kwargs["from"] = DoubleValue(value=lo)
            if hi is not None:
                kwargs["to"] = DoubleValue(value=hi)
            range_buckets.append(_pb.RangeBucket(**kwargs))
        self._aggregations.append(_pb.AggregationSpec(
            name=name, type=_pb.RANGE, field=field, range_buckets=range_buckets,
        ))
        return self

    # ── Metrics ──────────────────────────────────────────────────────────────────

    def avg(self, field: str, name: str) -> "AggregateBuilder":
        return self._add_metric(name, _pb.AVG, field)

    def sum(self, field: str, name: str) -> "AggregateBuilder":
        return self._add_metric(name, _pb.SUM, field)

    def min(self, field: str, name: str) -> "AggregateBuilder":
        return self._add_metric(name, _pb.MIN, field)

    def max(self, field: str, name: str) -> "AggregateBuilder":
        return self._add_metric(name, _pb.MAX, field)

    def count(self, field: str, name: str) -> "AggregateBuilder":
        return self._add_metric(name, _pb.COUNT, field)

    def count_all(self, name: str = "count") -> "AggregateBuilder":
        """COUNT(*) — leaves the aggregation's field empty."""
        return self._add_metric(name, _pb.COUNT, None)

    # ── Build ──────────────────────────────────────────────────────────────────

    def build(self, trace_id: str = "") -> _pb.AggregateRequest:
        """Compile to an ``AggregateRequest`` proto message.

        Raises:
            ValueError: If an aggregation ``name`` is duplicated.
        """
        names: set[str] = set()
        for a in self._aggregations:
            key = a.name.lower()
            if key in names:
                raise ValueError(f"Duplicate aggregation name '{a.name}'.")
            names.add(key)

        query = _pb.SearchQuery(
            clauses=self._where,
            logic=self._where_logic,
        )
        having = _pb.SearchQuery(
            clauses=self._having,
            logic=self._having_logic,
        )
        return _pb.AggregateRequest(
            type_name=self._type_name,
            query=query,
            aggregations=self._aggregations,
            having=having,
            joins=self._joins,
            trace_id=trace_id,
        )

    # ── Internal helpers ──────────────────────────────────────────────────────────

    def _add_metric(self, name: str, agg_type: int, field: Optional[str]) -> "AggregateBuilder":
        self._aggregations.append(_pb.AggregationSpec(
            name=name,
            type=agg_type,
            field=field or "",
        ))
        return self


def aggregate(type_name: str) -> AggregateBuilder:
    """Start an ``AggregateBuilder`` for the given entity type."""
    return AggregateBuilder(type_name)
