package iverson_test

import (
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
)

func strVal(s string) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_StringVal{StringVal: s}}
}

func TestGroupByNotAddsMustNot(t *testing.T) {
	req, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Not("Category", pb.SearchOperator_EQUALS, strVal("spam")).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_MUST_NOT {
		t.Errorf("clause type = %v", req.Query.Clauses[0].ClauseType)
	}
}

func TestGroupByWithHavingLogicOr(t *testing.T) {
	req, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Having("n", pb.SearchOperator_GREATER_THAN, numberVal(5)).
		WithHavingLogic(pb.SearchLogic_OR).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Having.Logic != pb.SearchLogic_OR {
		t.Errorf("having logic = %v", req.Having.Logic)
	}
}

func TestGroupByDuplicateMetricAliasErrors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").Sum("WordCount").Sum("WordCount").Build()
	if err == nil {
		t.Fatal("expected duplicate alias error")
	}
}

func TestGroupByHavingUnknownAliasErrors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Having("misspelled", pb.SearchOperator_GREATER_THAN, numberVal(5)).
		Build()
	if err == nil {
		t.Fatal("expected unknown having alias error")
	}
}

func TestGroupByHavingOnKeyAllowed(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Having("Category", pb.SearchOperator_EQUALS, strVal("tech")).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestGroupByOrderByUnknownAliasErrors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").OrderBy("nope").Build()
	if err == nil {
		t.Fatal("expected unknown orderBy alias error")
	}
}

func TestGroupByKeyCollidesWithMetricAlias_Errors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("total").Sum("Price", "total").Build()
	if err == nil {
		t.Fatal("expected key/metric-alias collision error")
	}
}

func TestGroupByHaving_ReferencesMetricAlias_CaseInsensitive_IsAllowed(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").Sum("WordCount", "Total").
		Having("TOTAL", pb.SearchOperator_GREATER_THAN, numberVal(100)).
		Build()
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}
}

func TestGroupByOrderBy_ReferencesKey_CaseInsensitive_IsAllowed(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").OrderBy("CATEGORY").
		Build()
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}
}
