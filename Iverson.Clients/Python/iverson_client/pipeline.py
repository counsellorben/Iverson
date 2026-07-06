"""
Fluent pipeline (CTE chain) builder that compiles to a ``PipelineRequest`` proto.

Each ``.step(name, fn)`` is exactly one CTE in the generated StarRocks query; steps
read the previous step by default, or any earlier named step via ``reads``. String-
addressed like ``GroupByBuilder``. ``build()`` never needs a live server.

Usage:
    request = (
        pipeline("Article")
        .where("IsPublished", pb.EQUALS, True)
        .step("by_author", lambda s: s.group_by("AuthorId").count_all("articles"))
        .step("ranked", lambda s: s.row_number("rank", order_by="articles", descending=True))
        .sort_on("rank")
        .limit(10)
        .build()
    )
"""
from __future__ import annotations

from typing import Callable, List, Optional, Tuple

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value

_BASE_STEP_NAME = "base"


class SelectSpecBuilder:
    """Builds a joined step's projection: which columns survive the join."""

    def __init__(self) -> None:
        self._items: list[_pb.SelectItem] = []

    def all_from(self, source: str) -> "SelectSpecBuilder":
        """All columns from a source ("base", a step name, or a joined type name)."""
        self._items.append(_pb.SelectItem(source=source, all=True))
        return self

    def pick(self, source: str, column: str, alias: Optional[str] = None) -> "SelectSpecBuilder":
        """One column from a source, optionally renamed."""
        self._items.append(_pb.SelectItem(source=source, column=column, alias=alias or ""))
        return self


class PipelineStepBuilder:
    """One pipeline step (= one CTE): WHERE + (windows XOR group_by/metrics/having)
    + derived columns + joins + projection. The input-step parameter is named
    ``reads`` because ``from`` is a Python keyword."""

    def __init__(self, name: str, earlier_steps: list[str]) -> None:
        self._name = name
        self._earlier = earlier_steps
        self._reads = ""
        self._where: list[_pb.SearchClause] = []
        self._where_logic = _pb.AND
        self._windows: list[_pb.WindowFunction] = []
        self._group_by: list[_pb.GroupKey] = []
        self._metrics: list[_pb.MetricSpec] = []
        self._having: list[_pb.SearchClause] = []
        self._derive: list[_pb.DeriveColumn] = []
        self._joins: list[_pb.PipelineJoin] = []
        self._select: list[_pb.SelectItem] = []

    # ── Input selection ──────────────────────────────────────────────────────

    def reads(self, step_name: str) -> "PipelineStepBuilder":
        """Read any earlier named step (or "base") instead of the previous step."""
        if step_name.lower() not in (s.lower() for s in self._earlier):
            raise ValueError(
                f"Step '{self._name}': reads '{step_name}' does not name an earlier step.")
        self._reads = step_name
        return self

    # ── Filtering ────────────────────────────────────────────────────────────

    def where(self, field: str, op: int, value: object) -> "PipelineStepBuilder":
        return self._add_clause(field, op, value, _pb.FILTER)

    def not_(self, field: str, op: int, value: object) -> "PipelineStepBuilder":
        return self._add_clause(field, op, value, _pb.MUST_NOT)

    def with_logic(self, logic: int) -> "PipelineStepBuilder":
        self._where_logic = logic
        return self

    # ── Window functions ─────────────────────────────────────────────────────

    def row_number(self, alias: str, *, order_by: str, descending: bool = False,
                   partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.ROW_NUMBER, "", order_by, descending, partition_by)

    def rank(self, alias: str, *, order_by: str, descending: bool = False,
             partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.RANK, "", order_by, descending, partition_by)

    def dense_rank(self, alias: str, *, order_by: str, descending: bool = False,
                   partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.DENSE_RANK, "", order_by, descending, partition_by)

    def running_sum(self, alias: str, field: str, *, order_by: str,
                    descending: bool = False,
                    partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.RUNNING_SUM, field, order_by, descending, partition_by)

    def running_avg(self, alias: str, field: str, *, order_by: str,
                    descending: bool = False,
                    partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.RUNNING_AVG, field, order_by, descending, partition_by)

    def lag(self, alias: str, field: str, *, order_by: str, offset: int = 1,
            descending: bool = False,
            partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.LAG, field, order_by, descending, partition_by, offset)

    def lead(self, alias: str, field: str, *, order_by: str, offset: int = 1,
             descending: bool = False,
             partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.LEAD, field, order_by, descending, partition_by, offset)

    # ── Aggregation ──────────────────────────────────────────────────────────

    def group_by(self, field: str, date_trunc: int = _pb.NONE) -> "PipelineStepBuilder":
        self._group_by.append(_pb.GroupKey(field=field, date_trunc=date_trunc))
        return self

    def sum(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_sum", _pb.SUM, field, None)

    def avg(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_avg", _pb.AVG, field, None)

    def min(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_min", _pb.MIN, field, None)

    def max(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_max", _pb.MAX, field, None)

    def count(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_count", _pb.COUNT, field, None)

    def count_all(self, alias: str = "count") -> "PipelineStepBuilder":
        return self._add_metric(alias, _pb.COUNT, None, None)

    def sum_expr(self, expression: str, alias: str) -> "PipelineStepBuilder":
        return self._add_metric(alias, _pb.SUM, None, expression)

    def avg_expr(self, expression: str, alias: str) -> "PipelineStepBuilder":
        return self._add_metric(alias, _pb.AVG, None, expression)

    def having(self, alias: str, op: int, value: object) -> "PipelineStepBuilder":
        self._having.append(_pb.SearchClause(
            property=alias, operator=op,
            value=_to_search_value(value), clause_type=_pb.FILTER))
        return self

    # ── Derived columns, joins, projection ───────────────────────────────────

    def derive(self, alias: str, expr: str) -> "PipelineStepBuilder":
        self._derive.append(_pb.DeriveColumn(alias=alias, expr=expr))
        return self

    def join(self, source: str, on_left: str, on_right: str,
             kind: int = _pb.JoinKind.INNER) -> "PipelineStepBuilder":
        self._joins.append(_pb.PipelineJoin(
            source=source, kind=kind,
            on=[_pb.JoinCondition(left=on_left, right=on_right)]))
        return self

    def join_all(self, source: str, on: List[Tuple[str, str]],
                 kind: int = _pb.JoinKind.INNER) -> "PipelineStepBuilder":
        self._joins.append(_pb.PipelineJoin(
            source=source, kind=kind,
            on=[_pb.JoinCondition(left=l, right=r) for l, r in on]))
        return self

    def select(self, configure: Callable[[SelectSpecBuilder], object]) -> "PipelineStepBuilder":
        builder = SelectSpecBuilder()
        configure(builder)
        self._select.extend(builder._items)
        return self

    # ── Build + validation ───────────────────────────────────────────────────

    def _build_step(self) -> _pb.PipelineStep:
        is_aggregate = bool(self._group_by or self._metrics or self._having)
        if self._windows and is_aggregate:
            raise ValueError(
                f"Step '{self._name}': window functions and group_by/metrics/having "
                "cannot share a step.")
        if (self._metrics or self._having) and not self._group_by:
            raise ValueError(
                f"Step '{self._name}': metrics/having require at least one group_by key.")
        if self._joins and not self._select:
            raise ValueError(
                f"Step '{self._name}': a step with joins requires a select projection.")

        seen: set[str] = set()
        aliases = ([w.alias for w in self._windows]
                   + [d.alias for d in self._derive]
                   + [m.name for m in self._metrics]
                   + [s.alias for s in self._select if s.alias])
        for a in aliases:
            if a.lower() in seen:
                raise ValueError(f"Step '{self._name}': duplicate output alias '{a}'.")
            seen.add(a.lower())

        return _pb.PipelineStep(
            name=self._name, reads=self._reads,
            where=self._where, where_logic=self._where_logic,
            windows=self._windows, group_by=self._group_by,
            metrics=self._metrics, having=self._having,
            derive=self._derive, joins=self._joins, select=self._select)

    def _add_clause(self, field: str, op: int, value: object,
                    clause_type: int) -> "PipelineStepBuilder":
        self._where.append(_pb.SearchClause(
            property=field, operator=op,
            value=_to_search_value(value), clause_type=clause_type))
        return self

    def _add_window(self, alias: str, kind: int, field: str, order_by: str,
                    descending: bool, partition_by: Optional[str],
                    offset: int = 1) -> "PipelineStepBuilder":
        self._windows.append(_pb.WindowFunction(
            alias=alias, kind=kind, field=field, order_by=order_by,
            descending=descending, partition_by=partition_by or "", offset=offset))
        return self

    def _add_metric(self, alias: str, agg_type: int, field: Optional[str],
                    expression: Optional[str]) -> "PipelineStepBuilder":
        self._metrics.append(_pb.MetricSpec(
            name=alias, type=agg_type, field=field or "", expression=expression or ""))
        return self


class PipelineBuilder:
    """Fluent DSL builder that compiles to a ``PipelineRequest`` proto message.
    Instantiate via the module-level ``pipeline(type_name)`` factory."""

    def __init__(self, type_name: str) -> None:
        self._type_name = type_name
        self._base_where: list[_pb.SearchClause] = []
        self._base_logic = _pb.AND
        self._steps: list[_pb.PipelineStep] = []
        self._order_by: list[_pb.SearchSort] = []
        self._limit = 10_000

    def where(self, field: str, op: int, value: object) -> "PipelineBuilder":
        return self._add_base_clause(field, op, value, _pb.FILTER)

    def not_(self, field: str, op: int, value: object) -> "PipelineBuilder":
        return self._add_base_clause(field, op, value, _pb.MUST_NOT)

    def with_logic(self, logic: int) -> "PipelineBuilder":
        self._base_logic = logic
        return self

    def step(self, name: str,
             configure: Callable[[PipelineStepBuilder], object]) -> "PipelineBuilder":
        if not name:
            raise ValueError("Step name must be non-empty.")
        if name.lower() == _BASE_STEP_NAME:
            raise ValueError(f"Step name '{name}' is reserved for the implicit base step.")
        if name.lower() in (s.name.lower() for s in self._steps):
            raise ValueError(f"Duplicate step name '{name}'.")

        earlier = [_BASE_STEP_NAME] + [s.name for s in self._steps]
        builder = PipelineStepBuilder(name, earlier)
        configure(builder)
        self._steps.append(builder._build_step())
        return self

    def sort_on(self, field: str, descending: bool = False) -> "PipelineBuilder":
        self._order_by.append(_pb.SearchSort(property=field, descending=descending))
        return self

    def sort_on_desc(self, field: str) -> "PipelineBuilder":
        return self.sort_on(field, descending=True)

    def limit(self, n: int) -> "PipelineBuilder":
        self._limit = n
        return self

    def build(self, trace_id: str = "") -> _pb.PipelineRequest:
        return _pb.PipelineRequest(
            type_name=self._type_name,
            base_where=self._base_where,
            base_logic=self._base_logic,
            steps=self._steps,
            order_by=self._order_by,
            limit=self._limit,
            trace_id=trace_id)

    def _add_base_clause(self, field: str, op: int, value: object,
                         clause_type: int) -> "PipelineBuilder":
        self._base_where.append(_pb.SearchClause(
            property=field, operator=op,
            value=_to_search_value(value), clause_type=clause_type))
        return self


def pipeline(type_name: str) -> PipelineBuilder:
    """Start a fluent pipeline (CTE chain) for the given entity type."""
    return PipelineBuilder(type_name)
