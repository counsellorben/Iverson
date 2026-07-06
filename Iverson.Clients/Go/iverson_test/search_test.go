package iverson_test

import (
	"testing"

	"github.com/iverson/clients/go/iverson"
	pb "github.com/iverson/clients/go/generated"
)

// ── QueryBuilder tests ─────────────────────────────────────────────────────────

func TestNewQuery_TypeName(t *testing.T) {
	req, err := iverson.NewQuery("Article").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TypeName != "Article" {
		t.Errorf("expected TypeName=Article, got %q", req.TypeName)
	}
}

func TestNewQuery_DefaultPageSize(t *testing.T) {
	req, err := iverson.NewQuery("Article").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.PageSize != 20 {
		t.Errorf("expected default PageSize=20, got %d", req.PageSize)
	}
}

func TestQueryBuilder_Limit(t *testing.T) {
	req, err := iverson.NewQuery("Article").Limit(25).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.PageSize != 25 {
		t.Errorf("expected PageSize=25, got %d", req.PageSize)
	}
}

func TestQueryBuilder_Offset(t *testing.T) {
	req, err := iverson.NewQuery("Article").Offset(3).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Page != 3 {
		t.Errorf("expected Page=3, got %d", req.Page)
	}
}

func TestQueryBuilder_Fields(t *testing.T) {
	req, err := iverson.NewQuery("Article").Fields("Title", "Category").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Fields) != 2 {
		t.Fatalf("expected 2 fields, got %d", len(req.Fields))
	}
}

func TestQueryBuilder_OrderBy(t *testing.T) {
	req, err := iverson.NewQuery("Article").OrderBy("Title").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Query.Sort) != 1 {
		t.Fatalf("expected 1 sort, got %d", len(req.Query.Sort))
	}
	if req.Query.Sort[0].Property != "Title" {
		t.Errorf("expected Property=Title, got %q", req.Query.Sort[0].Property)
	}
	if req.Query.Sort[0].Descending {
		t.Error("expected ascending sort")
	}
}

func TestQueryBuilder_OrderByDesc(t *testing.T) {
	req, err := iverson.NewQuery("Article").OrderByDesc("PublishedAt").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if !req.Query.Sort[0].Descending {
		t.Error("expected descending sort")
	}
	if req.Query.Sort[0].Property != "PublishedAt" {
		t.Errorf("expected Property=PublishedAt, got %q", req.Query.Sort[0].Property)
	}
}

func TestQueryBuilder_MultipleSorts(t *testing.T) {
	req, err := iverson.NewQuery("Article").
		OrderByDesc("PublishedAt").
		OrderBy("Title").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Query.Sort) != 2 {
		t.Errorf("expected 2 sorts, got %d", len(req.Query.Sort))
	}
}

// ── Operator tests ────────────────────────────────────────────────────────────

func TestWhere_Eq_String(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("Category").Eq("tech").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "Category", pb.SearchOperator_EQUALS)
	sv := req.Query.Clauses[0].Value.GetStringVal()
	if sv != "tech" {
		t.Errorf("expected string_val=tech, got %q", sv)
	}
}

func TestWhere_Eq_Int(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("WordCount").Eq(100).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "WordCount", pb.SearchOperator_EQUALS)
	nv := req.Query.Clauses[0].Value.GetNumberVal()
	if nv != 100 {
		t.Errorf("expected number_val=100, got %f", nv)
	}
}

func TestWhere_NotEq(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("Category").NotEq("spam").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "Category", pb.SearchOperator_NOT_EQUALS)
}

func TestWhere_Contains(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("Body").Contains("golang").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "Body", pb.SearchOperator_CONTAINS)
}

func TestWhere_StartsWith(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("Title").StartsWith("Go ").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "Title", pb.SearchOperator_STARTS_WITH)
}

func TestEndsWith_ProducesEndsWithOperator(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("Title").EndsWith(" Guide").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "Title", pb.SearchOperator_ENDS_WITH)
	sv := req.Query.Clauses[0].Value.GetStringVal()
	if sv != " Guide" {
		t.Errorf("expected string_val=%q, got %q", " Guide", sv)
	}
}

func TestJoin_AddsJoinSpec_ToSearchRequest(t *testing.T) {
	req, err := iverson.NewQuery("Order").
		Join("CustomerId", "Customer", "Id").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Joins) != 1 {
		t.Fatalf("expected 1 join, got %d", len(req.Joins))
	}
	j := req.Joins[0]
	if j.LeftType != "Order" {
		t.Errorf("expected LeftType=Order, got %q", j.LeftType)
	}
	if j.RightType != "Customer" {
		t.Errorf("expected RightType=Customer, got %q", j.RightType)
	}
	if j.LeftField != "CustomerId" {
		t.Errorf("expected LeftField=CustomerId, got %q", j.LeftField)
	}
	if j.RightField != "Id" {
		t.Errorf("expected RightField=Id, got %q", j.RightField)
	}
	if j.Kind != pb.JoinKind_INNER {
		t.Errorf("expected default JoinKind=INNER, got %v", j.Kind)
	}
}

func TestJoin_WithFullKind_SetsKind(t *testing.T) {
	req, err := iverson.NewQuery("Order").
		Join("CustomerId", "Customer", "Id", pb.JoinKind_FULL).
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Joins[0].Kind != pb.JoinKind_FULL {
		t.Errorf("expected JoinKind=FULL, got %v", req.Joins[0].Kind)
	}
}

func TestWhere_Gt(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("WordCount").Gt(500).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "WordCount", pb.SearchOperator_GREATER_THAN)
	if req.Query.Clauses[0].Value.GetNumberVal() != 500 {
		t.Errorf("expected 500, got %f", req.Query.Clauses[0].Value.GetNumberVal())
	}
}

func TestWhere_Lt(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("WordCount").Lt(1000).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "WordCount", pb.SearchOperator_LESS_THAN)
}

func TestWhere_Gte(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("WordCount").Gte(100).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "WordCount", pb.SearchOperator_GREATER_THAN_OR_EQUALS)
}

func TestWhere_Lte(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("WordCount").Lte(9999).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "WordCount", pb.SearchOperator_LESS_THAN_OR_EQUALS)
}

func TestWhere_In(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("Category").In("tech", "science").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "Category", pb.SearchOperator_IN)
	list := req.Query.Clauses[0].Value.GetStringList()
	if list == nil {
		t.Fatal("expected string_list, got nil")
	}
	if len(list.Values) != 2 {
		t.Errorf("expected 2 values, got %d", len(list.Values))
	}
}

func TestWhere_BoolValue(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("IsPublished").Eq(true).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	bv := req.Query.Clauses[0].Value.GetBoolVal()
	if !bv {
		t.Error("expected bool_val=true")
	}
}

func TestQueryBuilder_Chaining(t *testing.T) {
	req, err := iverson.NewQuery("Article").
		Where("Category").Eq("tech").
		Where("WordCount").Gt(100).
		OrderByDesc("PublishedAt").
		Limit(20).
		Offset(1).
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Query.Clauses) != 2 {
		t.Errorf("expected 2 clauses, got %d", len(req.Query.Clauses))
	}
	if req.PageSize != 20 {
		t.Errorf("expected PageSize=20, got %d", req.PageSize)
	}
	if req.Page != 1 {
		t.Errorf("expected Page=1, got %d", req.Page)
	}
}

func TestQueryBuilder_ClauseType_FILTER(t *testing.T) {
	req, err := iverson.NewQuery("Article").Where("Category").Eq("tech").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_FILTER {
		t.Errorf("expected FILTER clause type, got %v", req.Query.Clauses[0].ClauseType)
	}
}

func TestQueryBuilder_MustNot(t *testing.T) {
	req, err := iverson.NewQuery("Article").MustNot("Category").Eq("spam").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_MUST_NOT {
		t.Errorf("expected MUST_NOT clause type, got %v", req.Query.Clauses[0].ClauseType)
	}
}

func TestQueryBuilder_LogicOR(t *testing.T) {
	req, err := iverson.NewQuery("Article").
		Logic(pb.SearchLogic_OR).
		Where("Category").Eq("tech").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Query.Logic != pb.SearchLogic_OR {
		t.Errorf("expected OR logic, got %v", req.Query.Logic)
	}
}

func TestQueryBuilder_NoClauses(t *testing.T) {
	req, err := iverson.NewQuery("Article").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Query.Clauses) != 0 {
		t.Errorf("expected 0 clauses, got %d", len(req.Query.Clauses))
	}
}

// ── GroupByBuilder tests ─────────────────────────────────────────────────────

func TestGroupBy_KeyAddsGroupByField(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").Key("ReturnFlag").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Keys) != 1 {
		t.Fatalf("expected 1 key, got %d", len(req.Keys))
	}
	if req.Keys[0] != "ReturnFlag" {
		t.Errorf("expected key=ReturnFlag, got %q", req.Keys[0])
	}
}

func TestGroupBy_SumAddsMetricWithAutoAlias(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").Sum("Quantity").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Metrics) != 1 {
		t.Fatalf("expected 1 metric, got %d", len(req.Metrics))
	}
	m := req.Metrics[0]
	if m.Name != "Quantity_sum" {
		t.Errorf("expected auto alias=Quantity_sum, got %q", m.Name)
	}
	if m.Type != pb.AggregationType_SUM {
		t.Errorf("expected SUM, got %v", m.Type)
	}
	if m.Field != "Quantity" {
		t.Errorf("expected Field=Quantity, got %q", m.Field)
	}
}

func TestGroupBy_SumExprAddsRawExpression(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").
		SumExpr("Price * (1 - Discount)", "Revenue").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Metrics) != 1 {
		t.Fatalf("expected 1 metric, got %d", len(req.Metrics))
	}
	m := req.Metrics[0]
	if m.Name != "Revenue" {
		t.Errorf("expected alias=Revenue, got %q", m.Name)
	}
	if m.Type != pb.AggregationType_SUM {
		t.Errorf("expected SUM, got %v", m.Type)
	}
	if m.Expression != "Price * (1 - Discount)" {
		t.Errorf("expected raw expression, got %q", m.Expression)
	}
	if m.Field != "" {
		t.Errorf("expected empty Field for expression metric, got %q", m.Field)
	}
}

func TestGroupBy_CountAllProducesEmptyFieldMetric(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").CountAll("CountOrder").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Metrics) != 1 {
		t.Fatalf("expected 1 metric, got %d", len(req.Metrics))
	}
	m := req.Metrics[0]
	if m.Name != "CountOrder" {
		t.Errorf("expected alias=CountOrder, got %q", m.Name)
	}
	if m.Type != pb.AggregationType_COUNT {
		t.Errorf("expected COUNT, got %v", m.Type)
	}
	if m.Field != "" {
		t.Errorf("expected empty Field for COUNT(*), got %q", m.Field)
	}
}

func TestGroupBy_CountAllDefaultAlias(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").CountAll().Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Metrics[0].Name != "count" {
		t.Errorf("expected default alias=count, got %q", req.Metrics[0].Name)
	}
}

func TestGroupBy_HavingAddsHavingClause(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").
		Sum("Quantity", "SumQty").
		Having("SumQty", pb.SearchOperator_GREATER_THAN, iversonNumberValue(0)).
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Having == nil {
		t.Fatal("expected non-nil Having")
	}
	if len(req.Having.Clauses) != 1 {
		t.Fatalf("expected 1 having clause, got %d", len(req.Having.Clauses))
	}
	c := req.Having.Clauses[0]
	if c.Property != "SumQty" {
		t.Errorf("expected Property=SumQty, got %q", c.Property)
	}
	if c.Operator != pb.SearchOperator_GREATER_THAN {
		t.Errorf("expected GREATER_THAN, got %v", c.Operator)
	}
	if c.Value.GetNumberVal() != 0 {
		t.Errorf("expected number_val=0, got %f", c.Value.GetNumberVal())
	}
}

func TestGroupBy_JoinAddsJoinSpec(t *testing.T) {
	req, err := iverson.NewGroupBy("Order").
		Join("CustomerId", "Customer", "Id").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if len(req.Joins) != 1 {
		t.Fatalf("expected 1 join, got %d", len(req.Joins))
	}
	j := req.Joins[0]
	if j.LeftType != "Order" {
		t.Errorf("expected LeftType=Order, got %q", j.LeftType)
	}
	if j.RightType != "Customer" {
		t.Errorf("expected RightType=Customer, got %q", j.RightType)
	}
	if j.Kind != pb.JoinKind_INNER {
		t.Errorf("expected default JoinKind=INNER, got %v", j.Kind)
	}
}

func TestGroupBy_JoinWithFullKind_SetsKind(t *testing.T) {
	req, err := iverson.NewGroupBy("Order").
		Join("CustomerId", "Customer", "Id", pb.JoinKind_FULL).
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Joins[0].Kind != pb.JoinKind_FULL {
		t.Errorf("expected JoinKind=FULL, got %v", req.Joins[0].Kind)
	}
}

func TestGroupBy_BuildSetsTraceId(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").Build("trace-123")
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TraceId != "trace-123" {
		t.Errorf("expected TraceId=trace-123, got %q", req.TraceId)
	}
}

func TestGroupBy_BuildDefaultTraceId(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TraceId != "" {
		t.Errorf("expected empty TraceId, got %q", req.TraceId)
	}
}

func TestGroupBy_Q1StyleAllFieldsPresent(t *testing.T) {
	req, err := iverson.NewGroupBy("LineItem").
		Keys("ReturnFlag", "LineStatus").
		Where("ShipDate", pb.SearchOperator_LESS_THAN_OR_EQUALS, iversonStringValue("1998-12-01")).
		Sum("Quantity", "SumQty").
		Sum("ExtendedPrice", "SumBasePrice").
		SumExpr("ExtendedPrice * (1 - Discount)", "SumDiscPrice").
		Avg("Quantity", "AvgQty").
		CountAll("CountOrder").
		Having("SumQty", pb.SearchOperator_GREATER_THAN, iversonNumberValue(0)).
		OrderBy("ReturnFlag").
		OrderByDesc("LineStatus").
		Limit(100).
		Build("trace-q1")
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TypeName != "LineItem" {
		t.Errorf("expected TypeName=LineItem, got %q", req.TypeName)
	}
	if len(req.Keys) != 2 {
		t.Errorf("expected 2 keys, got %d", len(req.Keys))
	}
	if req.Query == nil || len(req.Query.Clauses) != 1 {
		t.Fatalf("expected 1 where clause, got %v", req.Query)
	}
	if len(req.Metrics) != 5 {
		t.Errorf("expected 5 metrics, got %d", len(req.Metrics))
	}
	if req.Having == nil || len(req.Having.Clauses) != 1 {
		t.Fatalf("expected 1 having clause, got %v", req.Having)
	}
	if len(req.OrderBy) != 2 {
		t.Errorf("expected 2 order-by clauses, got %d", len(req.OrderBy))
	}
	if req.Limit != 100 {
		t.Errorf("expected Limit=100, got %d", req.Limit)
	}
	if req.TraceId != "trace-q1" {
		t.Errorf("expected TraceId=trace-q1, got %q", req.TraceId)
	}
}

func TestGroupBy_WhereNilValueSetsError(t *testing.T) {
	_, err := iverson.NewGroupBy("LineItem").
		Where("ShipDate", pb.SearchOperator_LESS_THAN_OR_EQUALS, nil).
		Build()
	if err == nil {
		t.Fatal("expected error for nil search value, got nil")
	}
}

// ── Helpers ───────────────────────────────────────────────────────────────────

func iversonNumberValue(n float64) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_NumberVal{NumberVal: n}}
}

func iversonStringValue(s string) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_StringVal{StringVal: s}}
}

func assertSingleClause(t *testing.T, req *pb.SearchRequest, property string, op pb.SearchOperator) {
	t.Helper()
	if len(req.Query.Clauses) != 1 {
		t.Fatalf("expected 1 clause, got %d", len(req.Query.Clauses))
	}
	c := req.Query.Clauses[0]
	if c.Property != property {
		t.Errorf("expected Property=%q, got %q", property, c.Property)
	}
	if c.Operator != op {
		t.Errorf("expected operator=%v, got %v", op, c.Operator)
	}
}
