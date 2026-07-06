package io.iverson.client.search;

import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.DateTrunc;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.PipelineJoin;
import iverson.ObjectSearch.PipelineRequest;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.WindowFunctionKind;
import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

class PipelineBuilderTest {

    @Test
    void buildFullPipelineCompilesToExpectedProto() {
        PipelineRequest req = Query.pipeline("Article")
            .where("IsPublished", SearchOperator.EQUALS, true)
            .step("by_author", s -> s
                .groupBy("AuthorId")
                .countAll("articles")
                .having("articles", SearchOperator.GREATER_THAN, 5))
            .step("ranked", s -> s
                .rowNumber("rank", "articles", true))
            .step("named", s -> s
                .join("Author", "AuthorId", "Id")
                .select(sel -> sel.allFrom("ranked").pick("Author", "Name", "author_name")))
            .sortOnDesc("rank")
            .limit(5)
            .build();

        assertEquals("Article", req.getTypeName());
        assertEquals(1, req.getBaseWhereCount());
        assertEquals(3, req.getStepsCount());

        var agg = req.getSteps(0);
        assertEquals("by_author", agg.getName());
        assertEquals("AuthorId", agg.getGroupBy(0).getField());
        assertEquals(DateTrunc.NONE, agg.getGroupBy(0).getDateTrunc());
        assertEquals(AggregationType.COUNT, agg.getMetrics(0).getType());
        assertEquals("articles", agg.getHaving(0).getProperty());

        var win = req.getSteps(1);
        assertEquals(WindowFunctionKind.ROW_NUMBER, win.getWindows(0).getKind());
        assertTrue(win.getWindows(0).getDescending());

        var joined = req.getSteps(2);
        assertEquals("Author", joined.getJoins(0).getSource());
        assertEquals(JoinKind.INNER, joined.getJoins(0).getKind());
        assertEquals("AuthorId", joined.getJoins(0).getOn(0).getLeft());
        assertTrue(joined.getSelect(0).getAll());
        assertEquals("author_name", joined.getSelect(1).getAlias());

        assertEquals(5, req.getLimit());
    }

    @Test
    void rankWithPartitionByCompilesToExpectedProto() {
        PipelineRequest req = Query.pipeline("Article")
            .step("ranked", s -> s
                .rank("author_rank", "Score", true, "AuthorId"))
            .build();

        var win = req.getSteps(0).getWindows(0);
        assertEquals(WindowFunctionKind.RANK, win.getKind());
        assertEquals("AuthorId", win.getPartitionBy());
        assertEquals("Score", win.getOrderBy());
        assertTrue(win.getDescending());
    }

    @Test
    void duplicateStepNameThrows() {
        var b = Query.pipeline("Article").step("x", s -> s.derive("a", "WordCount"));
        assertThrows(IllegalArgumentException.class,
            () -> b.step("X", s -> s.derive("b", "WordCount")));
    }

    @Test
    void readsUnknownStepThrows() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("a", s -> s.reads("nope")));
    }

    @Test
    void windowAndGroupByInOneStepThrows() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("bad", s -> s
                .rowNumber("rn", "Id", false)
                .groupBy("AuthorId").countAll("n")));
    }

    @Test
    void joinWithoutSelectThrows() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("bad", s -> s.join("Author", "AuthorId", "Id")));
    }

    @Test
    void duplicateAliasesThrow() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("bad", s -> s
                .rowNumber("x", "Id", false)
                .derive("X", "WordCount + 1")));
    }

    @Test
    void join_withCompositeKey_addsMultipleConditions() {
        PipelineRequest req = Query.pipeline("Article")
            .step("enriched", s -> s
                .join("Author", List.of(new String[]{"AuthorId", "Id"}, new String[]{"TenantId", "TenantId"}))
                .select(sel -> sel.allFrom("base")))
            .build();

        PipelineJoin join = req.getSteps(0).getJoins(0);
        assertEquals(2, join.getOnCount());
        assertEquals("AuthorId", join.getOn(0).getLeft());
        assertEquals("Id", join.getOn(0).getRight());
        assertEquals("TenantId", join.getOn(1).getLeft());
        assertEquals("TenantId", join.getOn(1).getRight());
    }
}
