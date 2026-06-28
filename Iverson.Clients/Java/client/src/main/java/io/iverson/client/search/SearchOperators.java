package io.iverson.client.search;

import iverson.ObjectSearch.SearchOperator;

/**
 * Static constants for the Iverson search DSL. Import statically so callers can write:
 * <pre>{@code
 * import static io.iverson.client.search.SearchOperators.*;
 * ...
 * Query.of(Article.class)
 *     .where("title").eq("basketball")
 *     .where("wordCount").gt(100)
 *     .build();
 * }</pre>
 */
public final class SearchOperators {

    private SearchOperators() {}

    public static final SearchOperator EQ          = SearchOperator.EQUALS;
    public static final SearchOperator NEQ         = SearchOperator.NOT_EQUALS;
    public static final SearchOperator GT          = SearchOperator.GREATER_THAN;
    public static final SearchOperator GTE         = SearchOperator.GREATER_THAN_OR_EQUALS;
    public static final SearchOperator LT          = SearchOperator.LESS_THAN;
    public static final SearchOperator LTE         = SearchOperator.LESS_THAN_OR_EQUALS;
    public static final SearchOperator LIKE        = SearchOperator.CONTAINS;
    public static final SearchOperator CONTAINS    = SearchOperator.CONTAINS;
    public static final SearchOperator IN          = SearchOperator.IN;
    public static final SearchOperator NOT_IN      = SearchOperator.NOT_EQUALS;   // nearest mapping
    public static final SearchOperator EXISTS      = SearchOperator.EQUALS;       // sentinel usage
    public static final SearchOperator NOT_EXISTS  = SearchOperator.NOT_EQUALS;   // sentinel usage
    public static final SearchOperator STARTS_WITH = SearchOperator.STARTS_WITH;
    public static final SearchOperator CARDINALITY = SearchOperator.EQUALS;       // aggregate operator
}
