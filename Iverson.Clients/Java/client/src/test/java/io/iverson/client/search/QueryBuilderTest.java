package io.iverson.client.search;

import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.JoinSpec;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchRequest;
import iverson.ObjectSearch.SearchSort;
import org.junit.jupiter.api.Test;

import java.time.OffsetDateTime;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Unit tests for {@link QueryBuilder}. No server required — tests just inspect
 * the produced {@link SearchRequest} proto messages.
 */
class QueryBuilderTest {

    // ── Type name ─────────────────────────────────────────────────────────────

    static class Article {}

    @Test
    void build_setsTypeName_fromClass() {
        SearchRequest req = Query.of(Article.class).build();
        assertEquals("Article", req.getTypeName());
    }

    @Test
    void build_setsTypeName_fromString() {
        SearchRequest req = Query.<Article>ofType("MyType").build();
        assertEquals("MyType", req.getTypeName());
    }

    // ── Defaults ──────────────────────────────────────────────────────────────

    @Test
    void build_defaultsToPage0Size20() {
        SearchRequest req = Query.of(Article.class).build();
        assertEquals(0, req.getPage());
        assertEquals(20, req.getPageSize());
    }

    @Test
    void build_defaultsToAndLogic() {
        SearchRequest req = Query.of(Article.class).build();
        assertEquals(SearchLogic.AND, req.getQuery().getLogic());
    }

    // ── Paging ────────────────────────────────────────────────────────────────

    @Test
    void limit_setsPageSize() {
        SearchRequest req = Query.of(Article.class).limit(50).build();
        assertEquals(50, req.getPageSize());
    }

    @Test
    void offset_setsPage() {
        SearchRequest req = Query.of(Article.class).offset(3).build();
        assertEquals(3, req.getPage());
    }

    // ── Logic ─────────────────────────────────────────────────────────────────

    @Test
    void withLogic_setsOrLogic() {
        SearchRequest req = Query.of(Article.class).withLogic(SearchLogic.OR).build();
        assertEquals(SearchLogic.OR, req.getQuery().getLogic());
    }

    // ── Clause types ──────────────────────────────────────────────────────────

    @Test
    void where_producesFilterClause() {
        SearchRequest req = Query.of(Article.class)
            .where("title").eq("test")
            .build();
        assertEquals(SearchClauseType.FILTER, req.getQuery().getClauses(0).getClauseType());
    }

    @Test
    void and_producesMustClause() {
        SearchRequest req = Query.of(Article.class)
            .and("title").eq("test")
            .build();
        assertEquals(SearchClauseType.MUST, req.getQuery().getClauses(0).getClauseType());
    }

    @Test
    void or_producesShouldClause() {
        SearchRequest req = Query.of(Article.class)
            .or("title").eq("test")
            .build();
        assertEquals(SearchClauseType.SHOULD, req.getQuery().getClauses(0).getClauseType());
    }

    @Test
    void not_producesMustNotClause() {
        SearchRequest req = Query.of(Article.class)
            .not("title").eq("test")
            .build();
        assertEquals(SearchClauseType.MUST_NOT, req.getQuery().getClauses(0).getClauseType());
    }

    // ── Operators ────────────────────────────────────────────────────────────

    @Test
    void eq_producesEqualsOperator() {
        SearchRequest req = Query.of(Article.class).where("f").eq("v").build();
        assertEquals(SearchOperator.EQUALS, req.getQuery().getClauses(0).getOperator());
    }

    @Test
    void neq_producesNotEqualsOperator() {
        SearchRequest req = Query.of(Article.class).where("f").neq("v").build();
        assertEquals(SearchOperator.NOT_EQUALS, req.getQuery().getClauses(0).getOperator());
    }

    @Test
    void gt_producesGreaterThanOperator() {
        SearchRequest req = Query.of(Article.class).where("count").gt(5).build();
        assertEquals(SearchOperator.GREATER_THAN, req.getQuery().getClauses(0).getOperator());
    }

    @Test
    void gte_producesGreaterThanOrEqualsOperator() {
        SearchRequest req = Query.of(Article.class).where("count").gte(5).build();
        assertEquals(SearchOperator.GREATER_THAN_OR_EQUALS, req.getQuery().getClauses(0).getOperator());
    }

    @Test
    void lt_producesLessThanOperator() {
        SearchRequest req = Query.of(Article.class).where("count").lt(5).build();
        assertEquals(SearchOperator.LESS_THAN, req.getQuery().getClauses(0).getOperator());
    }

    @Test
    void lte_producesLessThanOrEqualsOperator() {
        SearchRequest req = Query.of(Article.class).where("count").lte(5).build();
        assertEquals(SearchOperator.LESS_THAN_OR_EQUALS, req.getQuery().getClauses(0).getOperator());
    }

    @Test
    void contains_producesContainsOperator() {
        SearchRequest req = Query.of(Article.class).where("tags").contains("nba").build();
        assertEquals(SearchOperator.CONTAINS, req.getQuery().getClauses(0).getOperator());
    }

    @Test
    void endsWith_producesEndsWithOperator() {
        SearchRequest req = Query.of(Article.class).where("title").endsWith("recap").build();
        SearchClause clause = req.getQuery().getClauses(0);
        assertEquals(SearchOperator.ENDS_WITH, clause.getOperator());
        assertEquals("recap", clause.getValue().getStringVal());
    }

    @Test
    void in_producesInOperator_withStringList() {
        SearchRequest req = Query.of(Article.class)
            .where("category").in("sports", "news")
            .build();
        SearchClause clause = req.getQuery().getClauses(0);
        assertEquals(SearchOperator.IN, clause.getOperator());
        assertEquals(List.of("sports", "news"), clause.getValue().getStringList().getValuesList());
    }

    @Test
    void in_listOverload_producesInOperator() {
        SearchRequest req = Query.of(Article.class)
            .where("category").in(List.of("a", "b", "c"))
            .build();
        SearchClause clause = req.getQuery().getClauses(0);
        assertEquals(SearchOperator.IN, clause.getOperator());
        assertEquals(3, clause.getValue().getStringList().getValuesCount());
    }

    @Test
    void vectorSimilar_producesVectorSimilarOperator_withFloatList() {
        float[] vec = {0.1f, 0.2f, 0.3f};
        SearchRequest req = Query.of(Article.class)
            .where("embedding").vectorSimilar(vec)
            .build();
        SearchClause clause = req.getQuery().getClauses(0);
        assertEquals(SearchOperator.VECTOR_SIMILAR, clause.getOperator());
        assertEquals(3, clause.getValue().getFloatList().getValuesCount());
        assertEquals(0.1f, clause.getValue().getFloatList().getValues(0), 0.001f);
    }

    // ── Value encoding ────────────────────────────────────────────────────────

    @Test
    void eq_encodesStringValue_asStringVal() {
        SearchRequest req = Query.of(Article.class).where("title").eq("hello").build();
        assertEquals("hello", req.getQuery().getClauses(0).getValue().getStringVal());
    }

    @Test
    void eq_encodesIntValue_asNumberVal() {
        SearchRequest req = Query.of(Article.class).where("count").eq(42).build();
        assertEquals(42.0, req.getQuery().getClauses(0).getValue().getNumberVal(), 0.001);
    }

    @Test
    void eq_encodesDoubleValue_asNumberVal() {
        SearchRequest req = Query.of(Article.class).where("rating").eq(4.5).build();
        assertEquals(4.5, req.getQuery().getClauses(0).getValue().getNumberVal(), 0.001);
    }

    @Test
    void eq_encodesBoolValue_asBoolVal() {
        SearchRequest req = Query.of(Article.class).where("published").eq(true).build();
        assertTrue(req.getQuery().getClauses(0).getValue().getBoolVal());
    }

    @Test
    void eq_encodesOffsetDateTimeValue_asIso8601String() {
        OffsetDateTime dt = OffsetDateTime.parse("2024-03-15T12:00:00Z");
        SearchRequest req = Query.of(Article.class).where("publishedAt").eq(dt).build();
        String encoded = req.getQuery().getClauses(0).getValue().getStringVal();
        assertTrue(encoded.contains("2024-03-15"), "should contain date: " + encoded);
    }

    // ── Field name passthrough ─────────────────────────────────────────────────

    @Test
    void where_passesFieldNameThrough_unchanged() {
        SearchRequest req = Query.of(Article.class).where("publishedAt").eq("x").build();
        assertEquals("publishedAt", req.getQuery().getClauses(0).getProperty());
    }

    // ── Sorting ────────────────────────────────────────────────────────────────

    @Test
    void orderBy_addsAscendingSort() {
        SearchRequest req = Query.of(Article.class).orderBy("title").build();
        SearchSort sort = req.getQuery().getSort(0);
        assertEquals("title", sort.getProperty());
        assertFalse(sort.getDescending());
    }

    @Test
    void orderByDesc_addsDescendingSort() {
        SearchRequest req = Query.of(Article.class).orderByDesc("publishedAt").build();
        SearchSort sort = req.getQuery().getSort(0);
        assertEquals("publishedAt", sort.getProperty());
        assertTrue(sort.getDescending());
    }

    @Test
    void multipleSorts_arePreservedInOrder() {
        SearchRequest req = Query.of(Article.class)
            .orderBy("category")
            .orderByDesc("publishedAt")
            .build();
        assertEquals(2, req.getQuery().getSortCount());
        assertEquals("category",    req.getQuery().getSort(0).getProperty());
        assertEquals("publishedAt", req.getQuery().getSort(1).getProperty());
        assertFalse(req.getQuery().getSort(0).getDescending());
        assertTrue( req.getQuery().getSort(1).getDescending());
    }

    // ── Multiple clauses ──────────────────────────────────────────────────────

    @Test
    void multipleClauses_arePreservedInOrder() {
        SearchRequest req = Query.of(Article.class)
            .where("category").eq("sports")
            .and("wordCount").gt(500)
            .not("draft").eq(true)
            .build();
        assertEquals(3, req.getQuery().getClausesCount());
        assertEquals("category",  req.getQuery().getClauses(0).getProperty());
        assertEquals("wordCount", req.getQuery().getClauses(1).getProperty());
        assertEquals("draft",     req.getQuery().getClauses(2).getProperty());
    }

    // ── Joins ─────────────────────────────────────────────────────────────────

    @Test
    void join_addsJoinSpec_toSearchRequest() {
        SearchRequest req = Query.of(Article.class)
            .join("authorId", "Author", "id")
            .build();
        assertEquals(1, req.getJoinsCount());
        JoinSpec join = req.getJoins(0);
        assertEquals("Article", join.getLeftType());
        assertEquals("Author", join.getRightType());
        assertEquals("authorId", join.getLeftField());
        assertEquals("id", join.getRightField());
        assertEquals(JoinKind.INNER, join.getKind());
    }

    @Test
    void join_withExplicitKind_setsKind() {
        SearchRequest req = Query.of(Article.class)
            .join("authorId", "Author", "id", JoinKind.LEFT)
            .build();
        assertEquals(JoinKind.LEFT, req.getJoins(0).getKind());
    }

    // ── SearchOperators constants ─────────────────────────────────────────────

    @Test
    void searchOperatorsConstants_referToCorrectProtoValues() {
        assertEquals(SearchOperator.EQUALS,                  SearchOperators.EQ);
        assertEquals(SearchOperator.NOT_EQUALS,              SearchOperators.NEQ);
        assertEquals(SearchOperator.GREATER_THAN,            SearchOperators.GT);
        assertEquals(SearchOperator.GREATER_THAN_OR_EQUALS,  SearchOperators.GTE);
        assertEquals(SearchOperator.LESS_THAN,               SearchOperators.LT);
        assertEquals(SearchOperator.LESS_THAN_OR_EQUALS,     SearchOperators.LTE);
        assertEquals(SearchOperator.CONTAINS,                SearchOperators.CONTAINS);
        assertEquals(SearchOperator.IN,                      SearchOperators.IN);
        assertEquals(SearchOperator.STARTS_WITH,             SearchOperators.STARTS_WITH);
    }
}
