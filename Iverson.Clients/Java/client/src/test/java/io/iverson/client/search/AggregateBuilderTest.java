package io.iverson.client.search;

import iverson.ObjectSearch.AggregateRequest;
import iverson.ObjectSearch.AggregationSpec;
import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.JoinSpec;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Unit tests for {@link AggregateBuilder}. No server required — tests just inspect
 * the produced {@link AggregateRequest} proto messages.
 */
class AggregateBuilderTest {

    // ── Simple metrics ──────────────────────────────────────────────────────────

    @Test
    void sum_addsAggregationSpec() {
        AggregateRequest req = Query.aggregate("LineItem")
            .sum("extendedPrice", "sum_price")
            .build();

        assertEquals(1, req.getAggregationsCount());
        AggregationSpec spec = req.getAggregations(0);
        assertEquals("sum_price", spec.getName());
        assertEquals(AggregationType.SUM, spec.getType());
        assertEquals("extendedPrice", spec.getField());
    }

    @Test
    void avg_addsAggregationSpec() {
        AggregateRequest req = Query.aggregate("LineItem")
            .avg("quantity", "avg_qty")
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("avg_qty", spec.getName());
        assertEquals(AggregationType.AVG, spec.getType());
        assertEquals("quantity", spec.getField());
    }

    @Test
    void min_addsAggregationSpec() {
        AggregateRequest req = Query.aggregate("LineItem")
            .min("quantity", "min_qty")
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("min_qty", spec.getName());
        assertEquals(AggregationType.MIN, spec.getType());
        assertEquals("quantity", spec.getField());
    }

    @Test
    void max_addsAggregationSpec() {
        AggregateRequest req = Query.aggregate("LineItem")
            .max("quantity", "max_qty")
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("max_qty", spec.getName());
        assertEquals(AggregationType.MAX, spec.getType());
        assertEquals("quantity", spec.getField());
    }

    @Test
    void count_addsAggregationSpec() {
        AggregateRequest req = Query.aggregate("LineItem")
            .count("orderId", "order_count")
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("order_count", spec.getName());
        assertEquals(AggregationType.COUNT, spec.getType());
        assertEquals("orderId", spec.getField());
    }

    @Test
    void countAll_producesEmptyFieldSpec() {
        AggregateRequest req = Query.aggregate("LineItem")
            .countAll("total")
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("total", spec.getName());
        assertEquals(AggregationType.COUNT, spec.getType());
        assertEquals("", spec.getField());
    }

    // ── Terms ───────────────────────────────────────────────────────────────────

    @Test
    void terms_defaultsSizeTo10() {
        AggregateRequest req = Query.aggregate("Article")
            .terms("category", "by_category")
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("by_category", spec.getName());
        assertEquals(AggregationType.TERMS, spec.getType());
        assertEquals("category", spec.getField());
        assertEquals(10, spec.getSize());
    }

    @Test
    void terms_withCustomSize() {
        AggregateRequest req = Query.aggregate("Article")
            .terms("category", "by_category", 25)
            .build();

        assertEquals(25, req.getAggregations(0).getSize());
    }

    // ── DateHistogram ───────────────────────────────────────────────────────────

    @Test
    void dateHistogram_addsAggregationSpec() {
        AggregateRequest req = Query.aggregate("Article")
            .dateHistogram("publishedAt", "by_month", "month")
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("by_month", spec.getName());
        assertEquals(AggregationType.DATE_HISTOGRAM, spec.getType());
        assertEquals("publishedAt", spec.getField());
        assertEquals("month", spec.getCalendarInterval());
        assertEquals("", spec.getTimeZone());
    }

    @Test
    void dateHistogram_withTimeZone() {
        AggregateRequest req = Query.aggregate("Article")
            .dateHistogram("publishedAt", "by_month", "month", "America/New_York")
            .build();

        assertEquals("America/New_York", req.getAggregations(0).getTimeZone());
    }

    // ── Range ───────────────────────────────────────────────────────────────────

    @Test
    void range_addsAggregationSpecWithBuckets() {
        AggregateRequest req = Query.aggregate("Article")
            .range("wordCount", "by_length",
                new AggregateBuilder.RangeBucket("short", null, 500d),
                new AggregateBuilder.RangeBucket("medium", 500d, 2000d),
                new AggregateBuilder.RangeBucket("long", 2000d, null))
            .build();

        AggregationSpec spec = req.getAggregations(0);
        assertEquals("by_length", spec.getName());
        assertEquals(AggregationType.RANGE, spec.getType());
        assertEquals("wordCount", spec.getField());
        assertEquals(3, spec.getRangeBucketsCount());

        assertEquals("short", spec.getRangeBuckets(0).getKey());
        assertFalse(spec.getRangeBuckets(0).hasFrom());
        assertEquals(500d, spec.getRangeBuckets(0).getTo().getValue(), 0.001);

        assertEquals("medium", spec.getRangeBuckets(1).getKey());
        assertEquals(500d, spec.getRangeBuckets(1).getFrom().getValue(), 0.001);
        assertEquals(2000d, spec.getRangeBuckets(1).getTo().getValue(), 0.001);

        assertEquals("long", spec.getRangeBuckets(2).getKey());
        assertEquals(2000d, spec.getRangeBuckets(2).getFrom().getValue(), 0.001);
        assertFalse(spec.getRangeBuckets(2).hasTo());
    }

    // ── Where / Not / WithLogic ─────────────────────────────────────────────────

    @Test
    void where_addsFilterClause() {
        AggregateRequest req = Query.aggregate("Article")
            .where("category", SearchOperator.EQUALS, "tech")
            .countAll("n")
            .build();

        SearchClause clause = req.getQuery().getClauses(0);
        assertEquals("category", clause.getProperty());
        assertEquals(SearchOperator.EQUALS, clause.getOperator());
        assertEquals("tech", clause.getValue().getStringVal());
        assertEquals(SearchClauseType.FILTER, clause.getClauseType());
    }

    @Test
    void not_addsMustNotClause() {
        AggregateRequest req = Query.aggregate("Article")
            .not("category", SearchOperator.EQUALS, "spam")
            .countAll("n")
            .build();

        assertEquals(SearchClauseType.MUST_NOT, req.getQuery().getClauses(0).getClauseType());
        assertEquals("category", req.getQuery().getClauses(0).getProperty());
    }

    @Test
    void withLogic_setsQueryLogic() {
        AggregateRequest req = Query.aggregate("Article")
            .where("category", SearchOperator.EQUALS, "tech")
            .where("wordCount", SearchOperator.GREATER_THAN, 100)
            .withLogic(SearchLogic.OR)
            .countAll("n")
            .build();

        assertEquals(SearchLogic.OR, req.getQuery().getLogic());
    }

    // ── Having / WithHavingLogic ────────────────────────────────────────────────

    @Test
    void having_addsHavingClause() {
        AggregateRequest req = Query.aggregate("Article")
            .sum("wordCount", "total")
            .having("metric_val", SearchOperator.GREATER_THAN, 1000)
            .build();

        SearchClause clause = req.getHaving().getClauses(0);
        assertEquals("metric_val", clause.getProperty());
        assertEquals(SearchOperator.GREATER_THAN, clause.getOperator());
        assertEquals(1000.0, clause.getValue().getNumberVal(), 0.001);
    }

    @Test
    void withHavingLogic_or_isCarried() {
        AggregateRequest req = Query.aggregate("Article")
            .countAll("n")
            .having("metric_val", SearchOperator.GREATER_THAN, 5)
            .having("metric_val", SearchOperator.LESS_THAN, 2)
            .withHavingLogic(SearchLogic.OR)
            .build();

        assertEquals(SearchLogic.OR, req.getHaving().getLogic());
    }

    // ── Join ────────────────────────────────────────────────────────────────────

    @Test
    void join_addsJoinSpec() {
        AggregateRequest req = Query.aggregate("LineItem")
            .join("orderId", "Orders", "orderId", JoinKind.LEFT)
            .countAll("n")
            .build();

        JoinSpec join = req.getJoins(0);
        assertEquals("LineItem", join.getLeftType());
        assertEquals("Orders", join.getRightType());
        assertEquals("orderId", join.getLeftField());
        assertEquals("orderId", join.getRightField());
        assertEquals(JoinKind.LEFT, join.getKind());
    }

    @Test
    void join_withoutExplicitKind_defaultsToInner() {
        AggregateRequest req = Query.aggregate("LineItem")
            .join("orderId", "Orders", "orderId")
            .countAll("n")
            .build();

        assertEquals(JoinKind.INNER, req.getJoins(0).getKind());
    }

    // ── Build metadata ──────────────────────────────────────────────────────────

    @Test
    void build_setsTraceId() {
        AggregateRequest req = Query.aggregate("Article").countAll("n").build("trace-abc");

        assertEquals("trace-abc", req.getTraceId());
    }

    @Test
    void build_withNoTraceId_defaultsToEmpty() {
        AggregateRequest req = Query.aggregate("Article").countAll("n").build();

        assertEquals("", req.getTraceId());
    }

    @Test
    void build_setsTypeName() {
        AggregateRequest req = Query.aggregate("Article").countAll("n").build();

        assertEquals("Article", req.getTypeName());
    }

    // ── Build validation ────────────────────────────────────────────────────────

    @Test
    void build_duplicateAggregationName_throws() {
        AggregateBuilder builder = Query.aggregate("Article")
            .sum("wordCount", "total")
            .avg("wordCount", "total");

        IllegalStateException ex = assertThrows(IllegalStateException.class, builder::build);
        assertTrue(ex.getMessage().contains("total"));
    }

    @Test
    void build_duplicateAggregationName_caseInsensitive_throws() {
        AggregateBuilder builder = Query.aggregate("Article")
            .sum("wordCount", "Total")
            .avg("wordCount", "TOTAL");

        assertThrows(IllegalStateException.class, builder::build);
    }
}
