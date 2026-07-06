package io.iverson.client.search;

import iverson.ObjectSearch.SearchChunksRequest;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchOperator;

/**
 * Fluent builder for a {@link SearchChunksRequest} (Qdrant chunk/RAG search). Supports at most
 * one filter clause: an EQUALS match on the entity's primary-key property.
 */
public final class ChunksBuilder {
    private final String typeName;
    private final String property;
    private String query = "";
    private int topK = 10;
    private SearchClause filter;

    ChunksBuilder(String typeName, String property) {
        this.typeName = typeName;
        this.property = property;
    }

    public ChunksBuilder text(String query) { this.query = query; return this; }
    public ChunksBuilder topK(int topK) { this.topK = topK; return this; }

    public ChunksBuilder where(String field, SearchOperator op, Object value) {
        if (op != SearchOperator.EQUALS) {
            throw new IllegalStateException(
                "SearchChunks only supports an EQUALS filter on the primary-key property; got " + op + ".");
        }
        if (filter != null) {
            throw new IllegalStateException("SearchChunks supports at most one filter clause.");
        }
        filter = SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build();
        return this;
    }

    public SearchChunksRequest build() { return build(""); }

    public SearchChunksRequest build(String traceId) {
        SearchChunksRequest.Builder builder = SearchChunksRequest.newBuilder()
            .setTypeName(typeName)
            .setProperty(property)
            .setQuery(query)
            .setTopK(topK)
            .setTraceId(traceId);
        if (filter != null) builder.addFilter(filter);
        return builder.build();
    }
}
