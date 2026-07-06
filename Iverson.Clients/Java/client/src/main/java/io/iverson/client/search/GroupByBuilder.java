package io.iverson.client.search;

import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.JoinSpec;
import iverson.ObjectSearch.MetricSpec;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchQuery;
import iverson.ObjectSearch.SearchSort;
import iverson.ObjectSearch.GroupByRequest;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Fluent DSL builder that compiles to a {@link GroupByRequest} proto message.
 *
 * <p>Unlike {@link QueryBuilder}, this builder is not generic on a type parameter —
 * joins bring multiple registered types into scope, so keys, filters, and metrics are
 * addressed by raw field-name strings.</p>
 *
 * <p>Does not require a live server — {@link #build()} simply returns the compiled proto.
 * Instantiate via {@link Query#groupBy(String)}.</p>
 *
 * <pre>{@code
 * GroupByRequest req = Query.groupBy("LineItem")
 *     .keys("returnFlag", "lineStatus")
 *     .sum("quantity")
 *     .avg("price")
 *     .countAll()
 *     .orderBy("returnFlag")
 *     .build();
 * }</pre>
 */
public final class GroupByBuilder {

    private final String typeName;
    private final List<String>       keys     = new ArrayList<>();
    private final List<MetricSpec>   metrics  = new ArrayList<>();
    private final List<SearchSort>   orderBy  = new ArrayList<>();
    private final List<SearchClause> where    = new ArrayList<>();
    private final List<SearchClause> having   = new ArrayList<>();
    private final List<JoinSpec>     joins    = new ArrayList<>();
    private SearchLogic whereLogic  = SearchLogic.AND;
    private SearchLogic havingLogic = SearchLogic.AND;
    private int         limit       = 10_000;

    GroupByBuilder(String typeName) {
        this.typeName = typeName;
    }

    // ── Keys ──────────────────────────────────────────────────────────────────

    /** Adds a single GROUP BY key. */
    public GroupByBuilder key(String field) {
        keys.add(field);
        return this;
    }

    /** Adds multiple GROUP BY keys. */
    public GroupByBuilder keys(String... fields) {
        keys.addAll(Arrays.asList(fields));
        return this;
    }

    // ── WHERE filter (raw field strings, same operators/encoding as QueryBuilder) ─

    /** Adds a WHERE (FILTER) clause. Uses the same value encoding as {@link QueryBuilder}. */
    public GroupByBuilder where(String field, SearchOperator op, Object value) {
        where.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    /** Adds a MUST_NOT WHERE clause (excludes matches before grouping). */
    public GroupByBuilder not(String field, SearchOperator op, Object value) {
        where.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.MUST_NOT)
            .build());
        return this;
    }

    /** Sets the logic used to combine top-level WHERE clauses. Defaults to AND. */
    public GroupByBuilder withLogic(SearchLogic logic) {
        this.whereLogic = logic;
        return this;
    }

    // ── HAVING (references output alias names) ──────────────────────────────────

    /** Adds a HAVING clause. {@code alias} must match a metric's output alias. */
    public GroupByBuilder having(String alias, SearchOperator op, Object value) {
        having.add(SearchClause.newBuilder()
            .setProperty(alias)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    /** Sets the logic combining HAVING clauses. Defaults to AND. */
    public GroupByBuilder withHavingLogic(SearchLogic logic) {
        this.havingLogic = logic;
        return this;
    }

    // ── JOIN ──────────────────────────────────────────────────────────────────

    /** Adds an INNER join from this type to {@code rightType} on the given fields. */
    public GroupByBuilder join(String leftField, String rightType, String rightField) {
        return join(leftField, rightType, rightField, JoinKind.INNER);
    }

    /** Adds a join of the given {@link JoinKind} from this type to {@code rightType}. */
    public GroupByBuilder join(String leftField, String rightType, String rightField, JoinKind kind) {
        joins.add(JoinSpec.newBuilder()
            .setLeftType(typeName)
            .setRightType(rightType)
            .setLeftField(leftField)
            .setRightField(rightField)
            .setKind(kind)
            .build());
        return this;
    }

    // ── Metrics — simple field ───────────────────────────────────────────────────

    public GroupByBuilder sum(String field) {
        return sum(field, field + "_sum");
    }

    public GroupByBuilder sum(String field, String alias) {
        return addMetric(alias, AggregationType.SUM, field, null);
    }

    public GroupByBuilder avg(String field) {
        return avg(field, field + "_avg");
    }

    public GroupByBuilder avg(String field, String alias) {
        return addMetric(alias, AggregationType.AVG, field, null);
    }

    public GroupByBuilder min(String field) {
        return min(field, field + "_min");
    }

    public GroupByBuilder min(String field, String alias) {
        return addMetric(alias, AggregationType.MIN, field, null);
    }

    public GroupByBuilder max(String field) {
        return max(field, field + "_max");
    }

    public GroupByBuilder max(String field, String alias) {
        return addMetric(alias, AggregationType.MAX, field, null);
    }

    public GroupByBuilder count(String field) {
        return count(field, field + "_count");
    }

    public GroupByBuilder count(String field, String alias) {
        return addMetric(alias, AggregationType.COUNT, field, null);
    }

    /** COUNT(*) — leaves the metric's field empty. Alias defaults to {@code "count"}. */
    public GroupByBuilder countAll() {
        return countAll("count");
    }

    /** COUNT(*) — leaves the metric's field empty. */
    public GroupByBuilder countAll(String alias) {
        return addMetric(alias, AggregationType.COUNT, null, null);
    }

    // ── Metrics — expression (raw SQL) ───────────────────────────────────────────

    /** SUM over a raw SQL expression, e.g. {@code "price * (1 - discount)"}. */
    public GroupByBuilder sumExpr(String expression, String alias) {
        return addMetric(alias, AggregationType.SUM, null, expression);
    }

    /** AVG over a raw SQL expression. */
    public GroupByBuilder avgExpr(String expression, String alias) {
        return addMetric(alias, AggregationType.AVG, null, expression);
    }

    // ── Sorting and limit ─────────────────────────────────────────────────────────

    public GroupByBuilder orderBy(String field) {
        return orderBy(field, false);
    }

    public GroupByBuilder orderByDesc(String field) {
        return orderBy(field, true);
    }

    public GroupByBuilder orderBy(String field, boolean desc) {
        orderBy.add(SearchSort.newBuilder().setProperty(field).setDescending(desc).build());
        return this;
    }

    public GroupByBuilder limit(int n) {
        this.limit = n;
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────────

    /** Builds and returns the {@link GroupByRequest} proto message. */
    public GroupByRequest build() {
        return build("");
    }

    /** Builds and returns the {@link GroupByRequest} proto message with the given trace ID. */
    public GroupByRequest build(String traceId) {
        java.util.Set<String> aliases = new java.util.HashSet<>();
        for (MetricSpec m : metrics)
            if (!aliases.add(m.getName().toLowerCase(java.util.Locale.ROOT)))
                throw new IllegalStateException("Duplicate metric alias '" + m.getName() + "'.");
        for (String k : keys) aliases.add(k.toLowerCase(java.util.Locale.ROOT));

        for (SearchClause h : having)
            if (!aliases.contains(h.getProperty().toLowerCase(java.util.Locale.ROOT)))
                throw new IllegalStateException("HAVING references '" + h.getProperty()
                    + "', which is neither a metric alias nor a key.");
        for (SearchSort s : orderBy)
            if (!aliases.contains(s.getProperty().toLowerCase(java.util.Locale.ROOT)))
                throw new IllegalStateException("orderBy references '" + s.getProperty()
                    + "', which is neither a metric alias nor a key.");

        SearchQuery query = SearchQuery.newBuilder()
            .addAllClauses(where)
            .setLogic(whereLogic)
            .build();

        SearchQuery havingQuery = SearchQuery.newBuilder()
            .addAllClauses(having)
            .setLogic(havingLogic)
            .build();

        return GroupByRequest.newBuilder()
            .setTypeName(typeName)
            .setQuery(query)
            .addAllKeys(keys)
            .addAllMetrics(metrics)
            .setHaving(havingQuery)
            .addAllOrderBy(orderBy)
            .setLimit(limit)
            .addAllJoins(joins)
            .setTraceId(traceId == null ? "" : traceId)
            .build();
    }

    // ── Internal helpers ─────────────────────────────────────────────────────────

    private GroupByBuilder addMetric(String alias, AggregationType type, String field, String expression) {
        MetricSpec.Builder spec = MetricSpec.newBuilder()
            .setName(alias)
            .setType(type);
        if (field != null) spec.setField(field);
        if (expression != null) spec.setExpression(expression);
        metrics.add(spec.build());
        return this;
    }
}
