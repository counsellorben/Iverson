package io.iverson.client.search;

import iverson.ObjectSearch;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.JoinSpec;
import iverson.ObjectSearch.RepeatedFloat;
import iverson.ObjectSearch.RepeatedString;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchQuery;
import iverson.ObjectSearch.SearchRequest;
import iverson.ObjectSearch.SearchSort;
import iverson.ObjectSearch.SearchValue;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Fluent DSL builder that produces a {@link SearchRequest} proto message.
 *
 * <p>Does not require a live server — {@link #build()} simply returns the
 * compiled proto. Instantiate via {@link Query#of(Class)} or {@link Query#ofType(String)}.</p>
 *
 * <pre>{@code
 * SearchRequest req = Query.of(Article.class)
 *     .where("category").eq("sports")
 *     .where("wordCount").gt(500)
 *     .orderBy("publishedAt").desc()
 *     .limit(20)
 *     .offset(0)
 *     .build();
 * }</pre>
 */
public final class QueryBuilder<T> {

    private final String typeName;
    private final List<SearchClause> clauses    = new ArrayList<>();
    private final List<SearchSort>   sorts      = new ArrayList<>();
    private final List<String>       fields     = new ArrayList<>();
    private final List<JoinSpec>     joins      = new ArrayList<>();
    private SearchLogic logic    = SearchLogic.AND;
    private int         page     = 0;
    private int         pageSize = 20;

    QueryBuilder(String typeName) {
        this.typeName = typeName;
    }

    // ── Clause entry points ────────────────────────────────────────────────────

    /**
     * Begins a FILTER clause on the named field.
     * Returns a {@link FieldCondition} on which you call the operator method.
     */
    public FieldCondition where(String field) {
        return new FieldCondition(this, field, SearchClauseType.FILTER);
    }

    /** Begins a MUST clause. */
    public FieldCondition and(String field) {
        return new FieldCondition(this, field, SearchClauseType.MUST);
    }

    /** Begins a SHOULD clause. */
    public FieldCondition or(String field) {
        return new FieldCondition(this, field, SearchClauseType.SHOULD);
    }

    /** Begins a MUST_NOT clause. */
    public FieldCondition not(String field) {
        return new FieldCondition(this, field, SearchClauseType.MUST_NOT);
    }

    /** Restricts the response to only the named fields. Empty (default) returns all fields. */
    public QueryBuilder<T> fields(String... names) {
        fields.addAll(Arrays.asList(names));
        return this;
    }

    // ── Joins ─────────────────────────────────────────────────────────────────

    /** Adds an INNER join from this type to {@code rightType} on the given fields. */
    public QueryBuilder<T> join(String leftField, String rightType, String rightField) {
        return join(leftField, rightType, rightField, JoinKind.INNER);
    }

    /** Adds a join of the given {@link JoinKind} from this type to {@code rightType}. */
    public QueryBuilder<T> join(String leftField, String rightType, String rightField, JoinKind kind) {
        joins.add(JoinSpec.newBuilder()
            .setLeftType(typeName)
            .setRightType(rightType)
            .setLeftField(leftField)
            .setRightField(rightField)
            .setKind(kind)
            .build());
        return this;
    }

    // ── Sorting ────────────────────────────────────────────────────────────────

    /** Adds an ascending sort on the given field. */
    public QueryBuilder<T> orderBy(String field) {
        sorts.add(SearchSort.newBuilder().setProperty(field).setDescending(false).build());
        return this;
    }

    /** Adds a descending sort on the given field. */
    public QueryBuilder<T> orderByDesc(String field) {
        sorts.add(SearchSort.newBuilder().setProperty(field).setDescending(true).build());
        return this;
    }

    // ── Paging ────────────────────────────────────────────────────────────────

    /** Sets the page size (number of results per page). Defaults to 20. */
    public QueryBuilder<T> limit(int n) {
        this.pageSize = n;
        return this;
    }

    /** Sets the zero-based offset in pages (NOT in rows). Maps to proto {@code page}. */
    public QueryBuilder<T> offset(int page) {
        this.page = page;
        return this;
    }

    /** Sets the logic used to combine top-level clauses. Defaults to AND. */
    public QueryBuilder<T> withLogic(SearchLogic logic) {
        this.logic = logic;
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /** Builds and returns the {@link SearchRequest} proto message. */
    public SearchRequest build() {
        SearchQuery query = SearchQuery.newBuilder()
            .addAllClauses(clauses)
            .addAllSort(sorts)
            .setLogic(logic)
            .build();

        return SearchRequest.newBuilder()
            .setTypeName(typeName)
            .setQuery(query)
            .setPage(page)
            .setPageSize(pageSize)
            .addAllFields(fields)
            .addAllJoins(joins)
            .build();
    }

    // ── Internal clause addition ───────────────────────────────────────────────

    void addClause(String field, SearchOperator op, SearchValue value, SearchClauseType clauseType) {
        clauses.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(value)
            .setClauseType(clauseType)
            .build());
    }

    // ── FieldCondition ─────────────────────────────────────────────────────────

    /**
     * Intermediate object returned by {@link #where}/{@link #and}/{@link #or}/{@link #not}
     * that lets the caller specify the operator and value before returning to the builder.
     */
    public final class FieldCondition {
        private final QueryBuilder<T> parent;
        private final String field;
        private final SearchClauseType clauseType;

        FieldCondition(QueryBuilder<T> parent, String field, SearchClauseType clauseType) {
            this.parent     = parent;
            this.field      = field;
            this.clauseType = clauseType;
        }

        /** Equals (=). */
        public QueryBuilder<T> eq(Object value) {
            return addOp(SearchOperator.EQUALS, value);
        }

        /** Not equals (!=). */
        public QueryBuilder<T> neq(Object value) {
            return addOp(SearchOperator.NOT_EQUALS, value);
        }

        /** Greater than (>). */
        public QueryBuilder<T> gt(Object value) {
            return addOp(SearchOperator.GREATER_THAN, value);
        }

        /** Greater than or equal (>=). */
        public QueryBuilder<T> gte(Object value) {
            return addOp(SearchOperator.GREATER_THAN_OR_EQUALS, value);
        }

        /** Less than (<). */
        public QueryBuilder<T> lt(Object value) {
            return addOp(SearchOperator.LESS_THAN, value);
        }

        /** Less than or equal (<=). */
        public QueryBuilder<T> lte(Object value) {
            return addOp(SearchOperator.LESS_THAN_OR_EQUALS, value);
        }

        /** CONTAINS — array contains value. */
        public QueryBuilder<T> contains(Object value) {
            return addOp(SearchOperator.CONTAINS, value);
        }

        /** STARTS_WITH — string field starts with value. */
        public QueryBuilder<T> startsWith(String value) {
            return addOp(SearchOperator.STARTS_WITH, value);
        }

        /** ENDS_WITH — string field ends with value. */
        public QueryBuilder<T> endsWith(String value) {
            return addOp(SearchOperator.ENDS_WITH, value);
        }

        /** IN — field value is one of the supplied list. */
        public QueryBuilder<T> in(List<String> values) {
            SearchValue sv = SearchValue.newBuilder()
                .setStringList(RepeatedString.newBuilder().addAllValues(values).build())
                .build();
            parent.addClause(field, SearchOperator.IN, sv, clauseType);
            return parent;
        }

        /** IN — varargs overload for convenience. */
        public QueryBuilder<T> in(String... values) {
            return in(Arrays.asList(values));
        }

        /** Vector similarity search with a pre-computed embedding. */
        public QueryBuilder<T> vectorSimilar(float[] vector) {
            RepeatedFloat rf = RepeatedFloat.newBuilder()
                .addAllValues(floatArrayToList(vector))
                .build();
            SearchValue sv = SearchValue.newBuilder().setFloatList(rf).build();
            parent.addClause(field, SearchOperator.VECTOR_SIMILAR, sv, clauseType);
            return parent;
        }

        private QueryBuilder<T> addOp(SearchOperator op, Object value) {
            parent.addClause(field, op, SearchValues.toSearchValue(value), clauseType);
            return parent;
        }

        private static List<Float> floatArrayToList(float[] arr) {
            List<Float> list = new ArrayList<>(arr.length);
            for (float f : arr) list.add(f);
            return list;
        }
    }
}
