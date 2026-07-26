package io.iverson.client.search;

import com.google.protobuf.DoubleValue;
import iverson.ObjectSearch.AggregateRequest;
import iverson.ObjectSearch.AggregationSpec;
import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.JoinSpec;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchQuery;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * Fluent DSL builder that compiles to an {@link AggregateRequest} proto message.
 *
 * <p>Not generic on a type parameter — joins bring multiple registered types into scope,
 * so filters and aggregation fields are addressed by raw field-name strings, same as
 * {@link GroupByBuilder}. Unlike {@link GroupByBuilder} (one compound SELECT with all
 * metrics as columns), each entry here becomes its own {@link AggregationSpec} (one SQL
 * query per spec on the server).</p>
 *
 * <p>Does not require a live server — {@link #build()} simply returns the compiled proto.
 * Instantiate via {@link Query#aggregate(String)}.</p>
 */
public final class AggregateBuilder {

    private final String typeName;
    private final List<AggregationSpec> aggregations = new ArrayList<>();
    private final List<SearchClause>    where        = new ArrayList<>();
    private final List<SearchClause>    having       = new ArrayList<>();
    private final List<JoinSpec>        joins        = new ArrayList<>();
    private SearchLogic whereLogic  = SearchLogic.AND;
    private SearchLogic havingLogic = SearchLogic.AND;

    AggregateBuilder(String typeName) {
        this.typeName = typeName;
    }

    /** One bucket boundary for {@link #range}. A {@code null} {@code from}/{@code to} means unbounded. */
    public record RangeBucket(String key, Double from, Double to) {}

    // ── WHERE filter (raw field strings, same operators/encoding as QueryBuilder) ─

    /** Adds a WHERE (FILTER) clause. Uses the same value encoding as {@link QueryBuilder}. */
    public AggregateBuilder where(String field, SearchOperator op, Object value) {
        where.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    /** Adds a MUST_NOT WHERE clause (excludes matches before aggregating). */
    public AggregateBuilder not(String field, SearchOperator op, Object value) {
        where.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.MUST_NOT)
            .build());
        return this;
    }

    /** Sets the logic used to combine top-level WHERE clauses. Defaults to AND. */
    public AggregateBuilder withLogic(SearchLogic logic) {
        this.whereLogic = logic;
        return this;
    }

    // ── HAVING (references output alias names) ──────────────────────────────────

    /** Adds a HAVING clause. {@code alias} must match an aggregation's output name. */
    public AggregateBuilder having(String alias, SearchOperator op, Object value) {
        having.add(SearchClause.newBuilder()
            .setProperty(alias)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    /** Sets the logic combining HAVING clauses. Defaults to AND. */
    public AggregateBuilder withHavingLogic(SearchLogic logic) {
        this.havingLogic = logic;
        return this;
    }

    // ── JOIN ──────────────────────────────────────────────────────────────────

    /** Adds an INNER join from this type to {@code rightType} on the given fields. */
    public AggregateBuilder join(String leftField, String rightType, String rightField) {
        return join(leftField, rightType, rightField, JoinKind.INNER);
    }

    /** Adds a join of the given {@link JoinKind} from this type to {@code rightType}. */
    public AggregateBuilder join(String leftField, String rightType, String rightField, JoinKind kind) {
        joins.add(JoinSpec.newBuilder()
            .setLeftType(typeName)
            .setRightType(rightType)
            .setLeftField(leftField)
            .setRightField(rightField)
            .setKind(kind)
            .build());
        return this;
    }

    // ── Aggregations — bucketing ────────────────────────────────────────────────

    public AggregateBuilder terms(String field, String name) {
        return terms(field, name, 10);
    }

    public AggregateBuilder terms(String field, String name, int size) {
        return addMetric(name, AggregationType.TERMS, field, size, null, null);
    }

    public AggregateBuilder dateHistogram(String field, String name, String calendarInterval) {
        return dateHistogram(field, name, calendarInterval, "");
    }

    public AggregateBuilder dateHistogram(String field, String name, String calendarInterval, String timeZone) {
        return addMetric(name, AggregationType.DATE_HISTOGRAM, field, null, calendarInterval, timeZone);
    }

    /** RANGE bucketing over {@code field}, with the given {@link RangeBucket} boundaries. */
    public AggregateBuilder range(String field, String name, RangeBucket... buckets) {
        AggregationSpec.Builder spec = AggregationSpec.newBuilder()
            .setName(name)
            .setType(AggregationType.RANGE)
            .setField(field);
        for (RangeBucket b : buckets) {
            iverson.ObjectSearch.RangeBucket.Builder rb = iverson.ObjectSearch.RangeBucket.newBuilder()
                .setKey(b.key() == null ? "" : b.key());
            if (b.from() != null) rb.setFrom(DoubleValue.of(b.from()));
            if (b.to() != null) rb.setTo(DoubleValue.of(b.to()));
            spec.addRangeBuckets(rb.build());
        }
        aggregations.add(spec.build());
        return this;
    }

    // ── Aggregations — metrics ─────────────────────────────────────────────────

    public AggregateBuilder avg(String field, String name) {
        return addMetric(name, AggregationType.AVG, field, null, null, null);
    }

    public AggregateBuilder sum(String field, String name) {
        return addMetric(name, AggregationType.SUM, field, null, null, null);
    }

    public AggregateBuilder min(String field, String name) {
        return addMetric(name, AggregationType.MIN, field, null, null, null);
    }

    public AggregateBuilder max(String field, String name) {
        return addMetric(name, AggregationType.MAX, field, null, null, null);
    }

    public AggregateBuilder count(String field, String name) {
        return addMetric(name, AggregationType.COUNT, field, null, null, null);
    }

    /** COUNT(*) — leaves the spec's field empty. */
    public AggregateBuilder countAll(String name) {
        return addMetric(name, AggregationType.COUNT, null, null, null, null);
    }

    // ── Build ─────────────────────────────────────────────────────────────────────

    /** Builds and returns the {@link AggregateRequest} proto message. */
    public AggregateRequest build() {
        return build("");
    }

    /** Builds and returns the {@link AggregateRequest} proto message with the given trace ID. */
    public AggregateRequest build(String traceId) {
        Set<String> names = new HashSet<>();
        for (AggregationSpec a : aggregations)
            if (!names.add(a.getName().toLowerCase(Locale.ROOT)))
                throw new IllegalStateException("Duplicate aggregation name '" + a.getName() + "'.");

        SearchQuery query = SearchQuery.newBuilder()
            .addAllClauses(where)
            .setLogic(whereLogic)
            .build();

        SearchQuery havingQuery = SearchQuery.newBuilder()
            .addAllClauses(having)
            .setLogic(havingLogic)
            .build();

        return AggregateRequest.newBuilder()
            .setTypeName(typeName)
            .setQuery(query)
            .addAllAggregations(aggregations)
            .setHaving(havingQuery)
            .addAllJoins(joins)
            .setTraceId(traceId == null ? "" : traceId)
            .build();
    }

    // ── Internal helpers ─────────────────────────────────────────────────────────

    private AggregateBuilder addMetric(
            String name, AggregationType type, String field,
            Integer size, String calendarInterval, String timeZone) {
        AggregationSpec.Builder spec = AggregationSpec.newBuilder()
            .setName(name)
            .setType(type);
        if (field != null)            spec.setField(field);
        if (size != null)             spec.setSize(size);
        if (calendarInterval != null) spec.setCalendarInterval(calendarInterval);
        if (timeZone != null)         spec.setTimeZone(timeZone);
        aggregations.add(spec.build());
        return this;
    }
}
