package io.iverson.client.search;

import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSimilarRequest;

import java.util.ArrayList;
import java.util.List;

/**
 * Fluent builder for a {@link SearchSimilarRequest} (Qdrant vector similarity search).
 * Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
 * LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR are rejected.
 */
public final class SimilarBuilder {
    private final String typeName;
    private final String property;
    private String query = "";
    private int topK = 10;
    private SearchLogic logic = SearchLogic.AND;
    private final List<SearchClause> filter = new ArrayList<>();

    SimilarBuilder(String typeName, String property) {
        this.typeName = typeName;
        this.property = property;
    }

    public SimilarBuilder text(String query) { this.query = query; return this; }
    public SimilarBuilder topK(int topK) { this.topK = topK; return this; }
    public SimilarBuilder withLogic(SearchLogic logic) { this.logic = logic; return this; }

    public SimilarBuilder where(String field, SearchOperator op, Object value) {
        if (op == SearchOperator.CONTAINS || op == SearchOperator.STARTS_WITH
                || op == SearchOperator.ENDS_WITH || op == SearchOperator.VECTOR_SIMILAR) {
            throw new IllegalStateException("Operator " + op + " is not supported by SearchSimilar "
                + "filters. Supported operators: EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, "
                + "GREATER_THAN_OR_EQUALS, LESS_THAN_OR_EQUALS, IN.");
        }
        filter.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    public SearchSimilarRequest build() { return build(""); }

    public SearchSimilarRequest build(String traceId) {
        SearchSimilarRequest.Builder builder = SearchSimilarRequest.newBuilder()
            .setTypeName(typeName)
            .setProperty(property)
            .setQuery(query)
            .setTopK(topK)
            .setFilterLogic(logic)
            .setTraceId(traceId);
        builder.addAllFilter(filter);
        return builder.build();
    }
}
