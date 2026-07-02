"""
Fluent GROUP BY builder that compiles to a ``GroupByRequest`` proto.

Unlike ``QueryBuilder``, this builder runs a compound GROUP BY — multiple
metrics (SUM/AVG/MIN/MAX/COUNT) over the same grouping in a single SQL
round-trip, with optional WHERE, HAVING, JOIN, ORDER BY, and LIMIT.

Usage:
    request = (
        group_by("LineItem")
        .keys("return_flag", "line_status")
        .sum("quantity", "sum_qty")
        .avg("quantity", "avg_qty")
        .count_all("count_order")
        .having("sum_qty", SearchOperator.GREATER_THAN, 0)
        .order_by("return_flag")
        .build()
    )
"""
from __future__ import annotations

from typing import Optional

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value


class GroupByBuilder:
    """Fluent DSL builder that compiles to a ``GroupByRequest`` proto message.

    Does not require a live server — ``build()`` simply returns the compiled
    proto. Instantiate via the module-level ``group_by(type_name)`` factory.
    """

    def __init__(self, type_name: str) -> None:
        self._type_name = type_name
        self._keys: list[str] = []
        self._metrics: list[_pb.MetricSpec] = []
        self._order_by: list[_pb.SearchSort] = []
        self._where: list[_pb.SearchClause] = []
        self._having: list[_pb.SearchClause] = []
        self._joins: list[_pb.JoinSpec] = []
        self._where_logic = _pb.AND
        self._limit = 10_000

    # ── Keys ───────────────────────────────────────────────────────────────────

    def key(self, field: str) -> "GroupByBuilder":
        """Add a single GROUP BY key."""
        self._keys.append(field)
        return self

    def keys(self, *fields: str) -> "GroupByBuilder":
        """Add multiple GROUP BY keys."""
        self._keys.extend(fields)
        return self

    # ── WHERE filter (raw field strings, same value encoding as QueryBuilder) ──

    def where(self, field: str, op: int, value: object) -> "GroupByBuilder":
        """Add a WHERE (FILTER) clause."""
        self._where.append(_pb.SearchClause(
            property=field,
            operator=op,
            value=_to_search_value(value),
            clause_type=_pb.FILTER,
        ))
        return self

    def with_logic(self, logic: int) -> "GroupByBuilder":
        """Set the logic used to combine top-level WHERE clauses. Default: AND."""
        self._where_logic = logic
        return self

    # ── HAVING (references output alias names) ──────────────────────────────────

    def having(self, alias: str, op: int, value: object) -> "GroupByBuilder":
        """Add a HAVING clause. ``alias`` must match a metric's output alias."""
        self._having.append(_pb.SearchClause(
            property=alias,
            operator=op,
            value=_to_search_value(value),
            clause_type=_pb.FILTER,
        ))
        return self

    # ── JOIN ──────────────────────────────────────────────────────────────────

    def join(self, left_field: str, right_type: str, right_field: str,
              kind: int = _pb.JoinKind.INNER) -> "GroupByBuilder":
        """Add a join from this type to ``right_type`` on the given fields."""
        self._joins.append(_pb.JoinSpec(
            left_type=self._type_name,
            right_type=right_type,
            left_field=left_field,
            right_field=right_field,
            kind=kind,
        ))
        return self

    # ── Metrics — simple field ───────────────────────────────────────────────────

    def sum(self, field: str, alias: Optional[str] = None) -> "GroupByBuilder":
        return self._add_metric(alias or f"{field}_sum", _pb.SUM, field, None)

    def avg(self, field: str, alias: Optional[str] = None) -> "GroupByBuilder":
        return self._add_metric(alias or f"{field}_avg", _pb.AVG, field, None)

    def min(self, field: str, alias: Optional[str] = None) -> "GroupByBuilder":
        return self._add_metric(alias or f"{field}_min", _pb.MIN, field, None)

    def max(self, field: str, alias: Optional[str] = None) -> "GroupByBuilder":
        return self._add_metric(alias or f"{field}_max", _pb.MAX, field, None)

    def count(self, field: str, alias: Optional[str] = None) -> "GroupByBuilder":
        return self._add_metric(alias or f"{field}_count", _pb.COUNT, field, None)

    def count_all(self, alias: str = "count") -> "GroupByBuilder":
        """COUNT(*) — leaves the metric's field empty."""
        return self._add_metric(alias, _pb.COUNT, None, None)

    # ── Metrics — expression (raw SQL) ───────────────────────────────────────────

    def sum_expr(self, expression: str, alias: str) -> "GroupByBuilder":
        """SUM over a raw SQL expression, e.g. ``price * (1 - discount)``."""
        return self._add_metric(alias, _pb.SUM, None, expression)

    def avg_expr(self, expression: str, alias: str) -> "GroupByBuilder":
        """AVG over a raw SQL expression."""
        return self._add_metric(alias, _pb.AVG, None, expression)

    # ── Ordering and limit ────────────────────────────────────────────────────────

    def order_by(self, field: str, descending: bool = False) -> "GroupByBuilder":
        """Add a sort clause."""
        self._order_by.append(_pb.SearchSort(property=field, descending=descending))
        return self

    def order_by_desc(self, field: str) -> "GroupByBuilder":
        """Add a descending sort clause."""
        return self.order_by(field, descending=True)

    def limit(self, n: int) -> "GroupByBuilder":
        """Set the row limit. Default: 10000."""
        self._limit = n
        return self

    # ── Build ──────────────────────────────────────────────────────────────────

    def build(self, trace_id: str = "") -> _pb.GroupByRequest:
        """Compile to a ``GroupByRequest`` proto message."""
        query = _pb.SearchQuery(
            clauses=self._where,
            logic=self._where_logic,
        )
        having = _pb.SearchQuery(
            clauses=self._having,
            logic=_pb.AND,
        )
        return _pb.GroupByRequest(
            type_name=self._type_name,
            query=query,
            keys=self._keys,
            metrics=self._metrics,
            having=having,
            order_by=self._order_by,
            limit=self._limit,
            joins=self._joins,
            trace_id=trace_id,
        )

    # ── Internal helpers ──────────────────────────────────────────────────────────

    def _add_metric(self, alias: str, agg_type: int, field: Optional[str],
                     expression: Optional[str]) -> "GroupByBuilder":
        self._metrics.append(_pb.MetricSpec(
            name=alias,
            type=agg_type,
            field=field or "",
            expression=expression or "",
        ))
        return self
