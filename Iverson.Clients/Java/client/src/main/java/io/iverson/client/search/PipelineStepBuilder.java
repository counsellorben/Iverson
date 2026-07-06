package io.iverson.client.search;

import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.DateTrunc;
import iverson.ObjectSearch.DeriveColumn;
import iverson.ObjectSearch.GroupKey;
import iverson.ObjectSearch.JoinCondition;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.MetricSpec;
import iverson.ObjectSearch.PipelineJoin;
import iverson.ObjectSearch.PipelineStep;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SelectItem;
import iverson.ObjectSearch.WindowFunction;
import iverson.ObjectSearch.WindowFunctionKind;

import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.function.Consumer;

/**
 * Builds one pipeline step (= one CTE): WHERE + (window functions XOR
 * GROUP BY/metrics/HAVING) + derived columns + joins + projection.
 * The input-step selector is named {@code reads} (not {@code from}) to keep the method name
 * consistent across every Iverson client language, since {@code from} is a reserved word in
 * some of them (e.g. Python).
 */
public final class PipelineStepBuilder {

    private final String name;
    private final List<String> earlierSteps;
    private final PipelineStep.Builder step;

    PipelineStepBuilder(String name, List<String> earlierSteps) {
        this.name = name;
        this.earlierSteps = earlierSteps;
        this.step = PipelineStep.newBuilder().setName(name);
    }

    /** Reads any earlier named step (or "base") instead of the previous step. */
    public PipelineStepBuilder reads(String stepName) {
        boolean known = earlierSteps.stream().anyMatch(s -> s.equalsIgnoreCase(stepName));
        if (!known)
            throw new IllegalArgumentException(
                "Step '" + name + "': reads '" + stepName + "' does not name an earlier step.");
        step.setReads(stepName);
        return this;
    }

    public PipelineStepBuilder where(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.FILTER);
    }

    public PipelineStepBuilder not(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    public PipelineStepBuilder withLogic(SearchLogic logic) {
        step.setWhereLogic(logic);
        return this;
    }

    // ── Window functions ─────────────────────────────────────────────────────────

    public PipelineStepBuilder rowNumber(String alias, String orderBy, boolean descending) {
        return rowNumber(alias, orderBy, descending, null);
    }

    public PipelineStepBuilder rowNumber(String alias, String orderBy, boolean descending, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.ROW_NUMBER, "", orderBy, descending, partitionBy, 1);
    }

    public PipelineStepBuilder rank(String alias, String orderBy, boolean descending) {
        return rank(alias, orderBy, descending, null);
    }

    public PipelineStepBuilder rank(String alias, String orderBy, boolean descending, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.RANK, "", orderBy, descending, partitionBy, 1);
    }

    public PipelineStepBuilder denseRank(String alias, String orderBy, boolean descending) {
        return denseRank(alias, orderBy, descending, null);
    }

    public PipelineStepBuilder denseRank(String alias, String orderBy, boolean descending, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.DENSE_RANK, "", orderBy, descending, partitionBy, 1);
    }

    public PipelineStepBuilder runningSum(String alias, String field, String orderBy) {
        return runningSum(alias, field, orderBy, null);
    }

    public PipelineStepBuilder runningSum(String alias, String field, String orderBy, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.RUNNING_SUM, field, orderBy, false, partitionBy, 1);
    }

    public PipelineStepBuilder runningAvg(String alias, String field, String orderBy) {
        return runningAvg(alias, field, orderBy, null);
    }

    public PipelineStepBuilder runningAvg(String alias, String field, String orderBy, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.RUNNING_AVG, field, orderBy, false, partitionBy, 1);
    }

    /** LAG with the default offset of 1 (same default the server substitutes for offset 0). */
    public PipelineStepBuilder lag(String alias, String field, String orderBy) {
        return lag(alias, field, orderBy, 1, null);
    }

    public PipelineStepBuilder lag(String alias, String field, String orderBy, int offset) {
        return lag(alias, field, orderBy, offset, null);
    }

    public PipelineStepBuilder lag(String alias, String field, String orderBy, int offset, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.LAG, field, orderBy, false, partitionBy, offset);
    }

    /** LEAD with the default offset of 1 (same default the server substitutes for offset 0). */
    public PipelineStepBuilder lead(String alias, String field, String orderBy) {
        return lead(alias, field, orderBy, 1, null);
    }

    public PipelineStepBuilder lead(String alias, String field, String orderBy, int offset) {
        return lead(alias, field, orderBy, offset, null);
    }

    public PipelineStepBuilder lead(String alias, String field, String orderBy, int offset, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.LEAD, field, orderBy, false, partitionBy, offset);
    }

    // ── Aggregation ─────────────────────────────────────────────────────────────

    public PipelineStepBuilder groupBy(String field) {
        return groupBy(field, DateTrunc.NONE);
    }

    public PipelineStepBuilder groupBy(String field, DateTrunc dateTrunc) {
        step.addGroupBy(GroupKey.newBuilder().setField(field).setDateTrunc(dateTrunc).build());
        return this;
    }

    public PipelineStepBuilder sum(String field)                { return addMetric(field + "_sum", AggregationType.SUM, field, null); }
    public PipelineStepBuilder sum(String field, String alias)  { return addMetric(alias, AggregationType.SUM, field, null); }
    public PipelineStepBuilder avg(String field)                { return addMetric(field + "_avg", AggregationType.AVG, field, null); }
    public PipelineStepBuilder avg(String field, String alias)  { return addMetric(alias, AggregationType.AVG, field, null); }
    public PipelineStepBuilder min(String field)                { return addMetric(field + "_min", AggregationType.MIN, field, null); }
    public PipelineStepBuilder min(String field, String alias)  { return addMetric(alias, AggregationType.MIN, field, null); }
    public PipelineStepBuilder max(String field)                { return addMetric(field + "_max", AggregationType.MAX, field, null); }
    public PipelineStepBuilder max(String field, String alias)  { return addMetric(alias, AggregationType.MAX, field, null); }
    public PipelineStepBuilder count(String field)               { return count(field, field + "_count"); }
    public PipelineStepBuilder count(String field, String alias) { return addMetric(alias, AggregationType.COUNT, field, null); }
    public PipelineStepBuilder countAll()                        { return countAll("count"); }
    public PipelineStepBuilder countAll(String alias)            { return addMetric(alias, AggregationType.COUNT, null, null); }
    public PipelineStepBuilder sumExpr(String expression, String alias) { return addMetric(alias, AggregationType.SUM, null, expression); }
    public PipelineStepBuilder avgExpr(String expression, String alias) { return addMetric(alias, AggregationType.AVG, null, expression); }

    public PipelineStepBuilder having(String alias, SearchOperator op, Object value) {
        step.addHaving(SearchClause.newBuilder()
            .setProperty(alias)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    // ── Derived columns, joins, projection ──────────────────────────────────────

    public PipelineStepBuilder derive(String alias, String expr) {
        step.addDerive(DeriveColumn.newBuilder().setAlias(alias).setExpr(expr).build());
        return this;
    }

    public PipelineStepBuilder join(String source, String onLeft, String onRight) {
        return join(source, onLeft, onRight, JoinKind.INNER);
    }

    public PipelineStepBuilder join(String source, String onLeft, String onRight, JoinKind kind) {
        step.addJoins(PipelineJoin.newBuilder()
            .setSource(source)
            .setKind(kind)
            .addOn(JoinCondition.newBuilder().setLeft(onLeft).setRight(onRight).build())
            .build());
        return this;
    }

    /** Composite-key join: {@code on} is a list of {@code {left, right}} field-name pairs. */
    public PipelineStepBuilder join(String source, List<String[]> on) {
        return join(source, on, JoinKind.INNER);
    }

    public PipelineStepBuilder join(String source, List<String[]> on, JoinKind kind) {
        PipelineJoin.Builder joinBuilder = PipelineJoin.newBuilder().setSource(source).setKind(kind);
        for (String[] pair : on) {
            joinBuilder.addOn(JoinCondition.newBuilder().setLeft(pair[0]).setRight(pair[1]).build());
        }
        step.addJoins(joinBuilder.build());
        return this;
    }

    public PipelineStepBuilder select(Consumer<SelectSpecBuilder> configure) {
        SelectSpecBuilder builder = new SelectSpecBuilder();
        configure.accept(builder);
        step.addAllSelect(builder.items());
        return this;
    }

    // ── Build + validation ──────────────────────────────────────────────────────

    PipelineStep buildStep() {
        PipelineStep built = step.build();
        boolean isAggregate = built.getGroupByCount() > 0
            || built.getMetricsCount() > 0 || built.getHavingCount() > 0;

        if (built.getWindowsCount() > 0 && isAggregate)
            throw new IllegalArgumentException(
                "Step '" + name + "': window functions and GROUP BY/metrics/HAVING cannot share a step.");
        if ((built.getMetricsCount() > 0 || built.getHavingCount() > 0) && built.getGroupByCount() == 0)
            throw new IllegalArgumentException(
                "Step '" + name + "': metrics/HAVING require at least one groupBy key.");
        if (built.getJoinsCount() > 0 && built.getSelectCount() == 0)
            throw new IllegalArgumentException(
                "Step '" + name + "': a step with joins requires a select projection.");

        Set<String> aliases = new HashSet<>();
        for (WindowFunction w : built.getWindowsList()) requireUnique(aliases, w.getAlias());
        for (DeriveColumn d : built.getDeriveList())   requireUnique(aliases, d.getAlias());
        for (MetricSpec m : built.getMetricsList())    requireUnique(aliases, m.getName());
        for (SelectItem s : built.getSelectList())
            if (!s.getAlias().isEmpty()) requireUnique(aliases, s.getAlias());

        return built;
    }

    private void requireUnique(Set<String> seen, String alias) {
        if (!seen.add(alias.toLowerCase(Locale.ROOT)))
            throw new IllegalArgumentException(
                "Step '" + name + "': duplicate output alias '" + alias + "'.");
    }

    private PipelineStepBuilder addClause(
            String field, SearchOperator op, Object value, SearchClauseType clauseType) {
        step.addWhere(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(clauseType)
            .build());
        return this;
    }

    private PipelineStepBuilder addWindow(
            String alias, WindowFunctionKind kind, String field, String orderBy,
            boolean descending, String partitionBy, int offset) {
        step.addWindows(WindowFunction.newBuilder()
            .setAlias(alias)
            .setKind(kind)
            .setField(field)
            .setOrderBy(orderBy)
            .setDescending(descending)
            .setPartitionBy(partitionBy == null ? "" : partitionBy)
            .setOffset(offset)
            .build());
        return this;
    }

    private PipelineStepBuilder addMetric(
            String alias, AggregationType type, String field, String expression) {
        MetricSpec.Builder spec = MetricSpec.newBuilder().setName(alias).setType(type);
        if (field != null) spec.setField(field);
        if (expression != null) spec.setExpression(expression);
        step.addMetrics(spec.build());
        return this;
    }
}
