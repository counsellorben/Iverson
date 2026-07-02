package io.iverson.client.search;

import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.GroupByRequest;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.JoinSpec;
import iverson.ObjectSearch.MetricSpec;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchOperator;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Unit tests for {@link GroupByBuilder}. No server required — tests just inspect
 * the produced {@link GroupByRequest} proto messages.
 */
class GroupByBuilderTest {

    @Test
    void keys_addsGroupByFields() {
        GroupByRequest req = Query.groupBy("LineItem")
            .keys("returnFlag", "lineStatus")
            .build();
        assertEquals(2, req.getKeysCount());
        assertEquals("returnFlag", req.getKeys(0));
        assertEquals("lineStatus", req.getKeys(1));
    }

    @Test
    void sum_addsMetricWithAutoAlias() {
        GroupByRequest req = Query.groupBy("LineItem")
            .sum("quantity")
            .build();
        assertEquals(1, req.getMetricsCount());
        MetricSpec metric = req.getMetrics(0);
        assertEquals("quantity_sum", metric.getName());
        assertEquals(AggregationType.SUM, metric.getType());
        assertEquals("quantity", metric.getField());
    }

    @Test
    void sumExpr_addsRawExpression() {
        GroupByRequest req = Query.groupBy("LineItem")
            .sumExpr("price * (1 - discount)", "revenue")
            .build();
        assertEquals(1, req.getMetricsCount());
        MetricSpec metric = req.getMetrics(0);
        assertEquals("revenue", metric.getName());
        assertEquals(AggregationType.SUM, metric.getType());
        assertEquals("price * (1 - discount)", metric.getExpression());
        assertEquals("", metric.getField());
    }

    @Test
    void countAll_producesEmptyFieldMetric() {
        GroupByRequest req = Query.groupBy("LineItem")
            .countAll()
            .build();
        assertEquals(1, req.getMetricsCount());
        MetricSpec metric = req.getMetrics(0);
        assertEquals("count", metric.getName());
        assertEquals(AggregationType.COUNT, metric.getType());
        assertEquals("", metric.getField());
        assertEquals("", metric.getExpression());
    }

    @Test
    void having_addsHavingClause() {
        GroupByRequest req = Query.groupBy("LineItem")
            .sum("quantity", "total_qty")
            .having("total_qty", SearchOperator.GREATER_THAN, 100)
            .build();
        assertEquals(1, req.getHaving().getClausesCount());
        SearchClause clause = req.getHaving().getClauses(0);
        assertEquals("total_qty", clause.getProperty());
        assertEquals(SearchOperator.GREATER_THAN, clause.getOperator());
        assertEquals(SearchClauseType.FILTER, clause.getClauseType());
        assertEquals(100.0, clause.getValue().getNumberVal(), 0.001);
    }

    @Test
    void join_addsJoinSpec() {
        GroupByRequest req = Query.groupBy("LineItem")
            .join("orderId", "Order", "id")
            .build();
        assertEquals(1, req.getJoinsCount());
        JoinSpec join = req.getJoins(0);
        assertEquals("LineItem", join.getLeftType());
        assertEquals("Order", join.getRightType());
        assertEquals("orderId", join.getLeftField());
        assertEquals("id", join.getRightField());
        assertEquals(JoinKind.INNER, join.getKind());
    }

    @Test
    void join_withExplicitKind_setsKind() {
        GroupByRequest req = Query.groupBy("LineItem")
            .join("orderId", "Order", "id", JoinKind.LEFT)
            .build();
        assertEquals(JoinKind.LEFT, req.getJoins(0).getKind());
    }

    @Test
    void build_setsTraceId() {
        GroupByRequest req = Query.groupBy("LineItem")
            .build("trace-123");
        assertEquals("trace-123", req.getTraceId());
    }

    @Test
    void build_defaultTraceId_isEmpty() {
        GroupByRequest req = Query.groupBy("LineItem").build();
        assertEquals("", req.getTraceId());
    }

    // ── TPC-H Q1 style compound query ────────────────────────────────────────

    @Test
    void q1Style_allFieldsPresent() {
        GroupByRequest req = Query.groupBy("LineItem")
            .where("shipDate", SearchOperator.LESS_THAN_OR_EQUALS, "1998-12-01")
            .keys("returnFlag", "lineStatus")
            .sum("quantity", "sum_qty")
            .sum("extendedPrice", "sum_base_price")
            .sumExpr("extendedPrice * (1 - discount)", "sum_disc_price")
            .avg("quantity", "avg_qty")
            .countAll("count_order")
            .having("sum_qty", SearchOperator.GREATER_THAN, 0)
            .orderBy("returnFlag")
            .orderByDesc("lineStatus")
            .limit(500)
            .build("q1-trace");

        assertEquals("LineItem", req.getTypeName());
        assertEquals(1, req.getQuery().getClausesCount());
        assertEquals("shipDate", req.getQuery().getClauses(0).getProperty());
        assertEquals(2, req.getKeysCount());
        assertEquals(5, req.getMetricsCount());
        assertEquals(1, req.getHaving().getClausesCount());
        assertEquals(2, req.getOrderByCount());
        assertEquals("returnFlag", req.getOrderBy(0).getProperty());
        assertFalse(req.getOrderBy(0).getDescending());
        assertEquals("lineStatus", req.getOrderBy(1).getProperty());
        assertTrue(req.getOrderBy(1).getDescending());
        assertEquals(500, req.getLimit());
        assertEquals("q1-trace", req.getTraceId());
    }
}
