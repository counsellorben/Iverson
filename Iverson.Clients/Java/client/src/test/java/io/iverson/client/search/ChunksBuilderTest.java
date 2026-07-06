package io.iverson.client.search;

import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchChunksRequest;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class ChunksBuilderTest {

    @Test
    void build_happyPath_producesExpectedRequest() {
        SearchChunksRequest req = Query.chunks("Article", "Body")
            .text("neural networks")
            .topK(5)
            .where("Id", SearchOperator.EQUALS, "parent-123")
            .build();

        assertEquals("Article", req.getTypeName());
        assertEquals("Body", req.getProperty());
        assertEquals(5, req.getTopK());
        assertEquals(1, req.getFilterCount());
        assertEquals("Id", req.getFilter(0).getProperty());
    }

    @Test
    void where_nonEqualsOperator_throws() {
        ChunksBuilder b = Query.chunks("Article", "Body");
        assertThrows(IllegalStateException.class,
            () -> b.where("Id", SearchOperator.GREATER_THAN, "x"));
    }

    @Test
    void where_calledTwice_throwsOnSecondCall() {
        ChunksBuilder b = Query.chunks("Article", "Body").where("Id", SearchOperator.EQUALS, "a");
        assertThrows(IllegalStateException.class,
            () -> b.where("Id", SearchOperator.EQUALS, "b"));
    }
}
