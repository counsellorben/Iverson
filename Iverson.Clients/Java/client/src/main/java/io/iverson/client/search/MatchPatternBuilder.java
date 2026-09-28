package io.iverson.client.search;

import iverson.ObjectSearch.AfterMatchSkip;
import iverson.ObjectSearch.AfterMatchSkipKind;
import iverson.ObjectSearch.MatchPatternRequest;
import iverson.ObjectSearch.NamedExpr;
import iverson.ObjectSearch.PatternRowSource;
import iverson.ObjectSearch.PatternSubset;
import iverson.ObjectSearch.RowsPerMatch;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSort;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Fluent builder that compiles to a {@link MatchPatternRequest} (SQL:2016 row pattern
 * recognition). One method per request field; pattern, DEFINE and MEASURES strings pass
 * through unchanged, because the server validates them. Instantiate via
 * {@link Query#matchPattern(String)}.
 *
 * <p>Does not require a live server — {@link #build()} simply returns the compiled proto.</p>
 */
public final class MatchPatternBuilder {

    private final String typeName;
    private final List<SearchClause>  where       = new ArrayList<>();
    private final List<String>        partitionBy = new ArrayList<>();
    private final List<SearchSort>    orderBy     = new ArrayList<>();
    private final List<PatternSubset> subsets     = new ArrayList<>();
    private final List<NamedExpr>     define      = new ArrayList<>();
    private final List<NamedExpr>     measures    = new ArrayList<>();
    private PatternRowSource source        = PatternRowSource.TYPE_ROWS;
    private String           chunkProperty = "";
    private SearchLogic      whereLogic    = SearchLogic.AND;
    private String           pattern       = "";
    private RowsPerMatch     rowsPerMatch  = RowsPerMatch.ONE_ROW;
    private AfterMatchSkip   afterMatch;   // null: unset, so the server default (PAST_LAST_ROW) applies
    private int              limit;        // 0: the server default

    MatchPatternBuilder(String typeName) {
        this.typeName = typeName;
    }

    /** Matches over the chunks of the given {@code @IversonChunk} property instead of the type's rows. */
    public MatchPatternBuilder chunks(String chunkProperty) {
        this.source = PatternRowSource.CHUNKS;
        this.chunkProperty = chunkProperty;
        return this;
    }

    /** Adds a WHERE (FILTER) pre-filter clause. */
    public MatchPatternBuilder where(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.FILTER);
    }

    /** Adds a MUST_NOT pre-filter clause. */
    public MatchPatternBuilder not(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    /** Sets the logic combining the pre-filter clauses. Default: AND. */
    public MatchPatternBuilder withLogic(SearchLogic logic) {
        this.whereLogic = logic;
        return this;
    }

    /** Appends PARTITION BY columns. */
    public MatchPatternBuilder partitionBy(String... fields) {
        partitionBy.addAll(Arrays.asList(fields));
        return this;
    }

    /** Appends an ascending ORDER BY column. */
    public MatchPatternBuilder orderBy(String field) {
        return orderBy(field, false);
    }

    /** Appends an ORDER BY column. */
    public MatchPatternBuilder orderBy(String field, boolean descending) {
        orderBy.add(SearchSort.newBuilder().setProperty(field).setDescending(descending).build());
        return this;
    }

    /** Sets the row pattern, e.g. {@code "A B+"}. */
    public MatchPatternBuilder pattern(String pattern) {
        this.pattern = pattern;
        return this;
    }

    /** Adds a SUBSET: a union variable over the given pattern variables. */
    public MatchPatternBuilder subset(String name, String... variables) {
        subsets.add(PatternSubset.newBuilder().setName(name).addAllVariables(Arrays.asList(variables)).build());
        return this;
    }

    /** Adds a DEFINE: {@code variable AS expr}. */
    public MatchPatternBuilder define(String variable, String expr) {
        define.add(NamedExpr.newBuilder().setName(variable).setExpr(expr).build());
        return this;
    }

    /** Adds a MEASURE: {@code expr AS name}. */
    public MatchPatternBuilder measure(String name, String expr) {
        measures.add(NamedExpr.newBuilder().setName(name).setExpr(expr).build());
        return this;
    }

    /** Sets ONE ROW / ALL ROWS PER MATCH. Default: ONE_ROW. */
    public MatchPatternBuilder rowsPerMatch(RowsPerMatch mode) {
        this.rowsPerMatch = mode;
        return this;
    }

    /** Sets AFTER MATCH SKIP with no variable. Unset unless called. */
    public MatchPatternBuilder afterMatch(AfterMatchSkipKind kind) {
        return afterMatch(kind, "");
    }

    /** Sets AFTER MATCH SKIP; {@code variable} applies to TO_FIRST and TO_LAST. Unset unless called. */
    public MatchPatternBuilder afterMatch(AfterMatchSkipKind kind, String variable) {
        this.afterMatch = AfterMatchSkip.newBuilder().setKind(kind).setVariable(variable).build();
        return this;
    }

    /** Output row limit. Default: unset (0), so the server default applies. */
    public MatchPatternBuilder limit(int n) {
        this.limit = n;
        return this;
    }

    /** Compiles to the {@link MatchPatternRequest} proto. */
    public MatchPatternRequest build() {
        return build("");
    }

    /** Compiles to the {@link MatchPatternRequest} proto with the given trace ID. */
    public MatchPatternRequest build(String traceId) {
        MatchPatternRequest.Builder request = MatchPatternRequest.newBuilder()
            .setTypeName(typeName)
            .setSource(source)
            .setChunkProperty(chunkProperty)
            .addAllWhere(where)
            .setWhereLogic(whereLogic)
            .addAllPartitionBy(partitionBy)
            .addAllOrderBy(orderBy)
            .setPattern(pattern)
            .addAllSubsets(subsets)
            .addAllDefine(define)
            .addAllMeasures(measures)
            .setRowsPerMatch(rowsPerMatch)
            .setLimit(limit)
            .setTraceId(traceId == null ? "" : traceId);
        if (afterMatch != null) request.setAfterMatch(afterMatch);
        return request.build();
    }

    private MatchPatternBuilder addClause(
            String field, SearchOperator op, Object value, SearchClauseType clauseType) {
        where.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(clauseType)
            .build());
        return this;
    }
}
