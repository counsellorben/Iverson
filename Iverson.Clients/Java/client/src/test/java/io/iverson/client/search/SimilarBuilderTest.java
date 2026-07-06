package io.iverson.client.search;

import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSimilarRequest;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class SimilarBuilderTest {

    @Test
    void build_happyPath_producesExpectedRequest() {
        SearchSimilarRequest req = Query.similar("Article", "Title")
            .text("machine learning")
            .topK(10)
            .where("Category", SearchOperator.EQUALS, "Tech")
            .build();

        assertEquals("Article", req.getTypeName());
        assertEquals("Title", req.getProperty());
        assertEquals("machine learning", req.getQuery());
        assertEquals(10, req.getTopK());
        assertEquals(1, req.getFilterCount());
        assertEquals("Category", req.getFilter(0).getProperty());
    }

    @Test
    void where_containsOperator_throws() {
        SimilarBuilder b = Query.similar("Article", "Title");
        assertThrows(IllegalStateException.class,
            () -> b.where("Category", SearchOperator.CONTAINS, "x"));
    }

    @Test
    void where_vectorSimilarOperator_throws() {
        SimilarBuilder b = Query.similar("Article", "Title");
        assertThrows(IllegalStateException.class,
            () -> b.where("Category", SearchOperator.VECTOR_SIMILAR, "x"));
    }
}
