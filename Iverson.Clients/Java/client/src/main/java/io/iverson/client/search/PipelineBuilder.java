package io.iverson.client.search;

import iverson.ObjectSearch.PipelineRequest;
import iverson.ObjectSearch.PipelineStep;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSort;

import java.util.ArrayList;
import java.util.List;
import java.util.function.Consumer;

/**
 * Fluent DSL builder that compiles to a {@link PipelineRequest}. Each {@code step(...)} is
 * exactly one CTE in the generated StarRocks query; steps read the previous step by default
 * or any earlier step via {@link PipelineStepBuilder#reads(String)}. String-addressed like
 * {@link GroupByBuilder}. Instantiate via {@link Query#pipeline(String)}.
 *
 * <p>Does not require a live server — {@link #build()} simply returns the compiled proto.</p>
 */
public final class PipelineBuilder {

    static final String BASE_STEP_NAME = "base";

    private final String typeName;
    private final List<SearchClause> baseWhere = new ArrayList<>();
    private final List<PipelineStep> steps     = new ArrayList<>();
    private final List<SearchSort>   orderBy   = new ArrayList<>();
    private SearchLogic baseLogic = SearchLogic.AND;
    private int         limit     = 10_000;

    PipelineBuilder(String typeName) {
        this.typeName = typeName;
    }

    /** Adds a WHERE (FILTER) clause on the implicit base step. */
    public PipelineBuilder where(String field, SearchOperator op, Object value) {
        return addBaseClause(field, op, value, SearchClauseType.FILTER);
    }

    /** Adds a MUST_NOT clause on the implicit base step. */
    public PipelineBuilder not(String field, SearchOperator op, Object value) {
        return addBaseClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    /** Sets the logic combining base-step WHERE clauses. Default: AND. */
    public PipelineBuilder withLogic(SearchLogic logic) {
        this.baseLogic = logic;
        return this;
    }

    /** Adds one named step (= one CTE). */
    public PipelineBuilder step(String name, Consumer<PipelineStepBuilder> configure) {
        if (name == null || name.isEmpty())
            throw new IllegalStateException("Step name must be non-empty.");
        if (name.equalsIgnoreCase(BASE_STEP_NAME))
            throw new IllegalStateException(
                "Step name '" + name + "' is reserved for the implicit base step.");
        for (PipelineStep s : steps)
            if (s.getName().equalsIgnoreCase(name))
                throw new IllegalStateException("Duplicate step name '" + name + "'.");

        List<String> earlier = new ArrayList<>();
        earlier.add(BASE_STEP_NAME);
        for (PipelineStep s : steps) earlier.add(s.getName());

        PipelineStepBuilder builder = new PipelineStepBuilder(name, earlier);
        configure.accept(builder);
        steps.add(builder.buildStep());
        return this;
    }

    /** Final ORDER BY on the last step's output. */
    public PipelineBuilder sortOn(String field) {
        return sortOn(field, false);
    }

    public PipelineBuilder sortOnDesc(String field) {
        return sortOn(field, true);
    }

    public PipelineBuilder sortOn(String field, boolean descending) {
        orderBy.add(SearchSort.newBuilder().setProperty(field).setDescending(descending).build());
        return this;
    }

    /** Final row limit. Default: 10000. */
    public PipelineBuilder limit(int n) {
        this.limit = n;
        return this;
    }

    /** Compiles to the {@link PipelineRequest} proto. */
    public PipelineRequest build() {
        return build("");
    }

    /** Compiles to the {@link PipelineRequest} proto with the given trace ID. */
    public PipelineRequest build(String traceId) {
        return PipelineRequest.newBuilder()
            .setTypeName(typeName)
            .addAllBaseWhere(baseWhere)
            .setBaseLogic(baseLogic)
            .addAllSteps(steps)
            .addAllOrderBy(orderBy)
            .setLimit(limit)
            .setTraceId(traceId == null ? "" : traceId)
            .build();
    }

    private PipelineBuilder addBaseClause(
            String field, SearchOperator op, Object value, SearchClauseType clauseType) {
        baseWhere.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(clauseType)
            .build());
        return this;
    }
}
