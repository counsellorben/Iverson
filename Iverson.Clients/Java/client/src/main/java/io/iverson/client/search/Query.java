package io.iverson.client.search;

/**
 * Entry point for the Iverson search DSL.
 *
 * <pre>{@code
 * SearchRequest req = Query.of(Article.class)
 *     .where("category").eq("sports")
 *     .orderBy("publishedAt")
 *     .limit(20)
 *     .build();
 * }</pre>
 */
public final class Query {

    private Query() {}

    /**
     * Creates a {@link QueryBuilder} scoped to the given entity class.
     * The type name is derived from {@code entityClass.getSimpleName()}.
     */
    public static <T> QueryBuilder<T> of(Class<T> entityClass) {
        return new QueryBuilder<>(entityClass.getSimpleName());
    }

    /**
     * Creates a {@link QueryBuilder} scoped to the given type name string.
     * Use this when working with the type name directly.
     */
    public static <T> QueryBuilder<T> ofType(String typeName) {
        return new QueryBuilder<>(typeName);
    }

    /**
     * Creates a {@link GroupByBuilder} scoped to the given type name string.
     * Use for compound GROUP BY queries (multiple metrics, HAVING, joins) via
     * {@link GroupByBuilder#build()}.
     */
    public static GroupByBuilder groupBy(String typeName) {
        return new GroupByBuilder(typeName);
    }
}
