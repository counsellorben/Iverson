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

func TestWhere_VectorSimilar(t *testing.T) {
	vec := []float32{0.1, 0.2, 0.3}
	req, err := iverson.NewQuery("Article").Where("Body").VectorSimilar(vec).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	assertSingleClause(t, req, "Body", pb.SearchOperator_VECTOR_SIMILAR)
	fl := req.Query.Clauses[0].Value.GetFloatList()
	if fl == nil {
		t.Fatal("expected float_list, got nil")
	}
	if len(fl.Values) != 3 {
		t.Errorf("expected 3 floats, got %d", len(fl.Values))
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

func TestQueryBuilder_Must(t *testing.T) {
	req, err := iverson.NewQuery("Article").Must("Category").Eq("tech").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_MUST {
		t.Errorf("expected MUST clause type, got %v", req.Query.Clauses[0].ClauseType)
	}
}

func TestQueryBuilder_Should(t *testing.T) {
	req, err := iverson.NewQuery("Article").Should("Category").Eq("tech").Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_SHOULD {
		t.Errorf("expected SHOULD clause type, got %v", req.Query.Clauses[0].ClauseType)
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

// ── Helpers ───────────────────────────────────────────────────────────────────

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
