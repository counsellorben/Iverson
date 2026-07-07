package iverson_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
	"google.golang.org/protobuf/encoding/protojson"
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

// ── Cross-language golden-fixture contract ─────────────────────────────────
// Golden fixture generated from the C# builder (the reference implementation), checked
// in at Iverson.Clients/Common/testdata/groupby-contract-1.json. Same logical request,
// built here via Go's iverson.NewGroupBy(...), must serialize to the same JSON structure.
//
// If a legitimate proto/DSL change requires updating this fixture, regenerate it from the
// C# reference builder invocation (Iverson.Client.Search.Tests/GroupByBuilderTests.cs) —
// do not hand-edit the JSON file.

func TestGroupByBuild_MatchesGoldenFixture_Contract1(t *testing.T) {
	req, err := iverson.NewGroupBy("Article").
		Keys("Category").
		Sum("WordCount", "TotalWords").
		CountAll("ArticleCount").
		Having("TotalWords", pb.SearchOperator_GREATER_THAN, numberVal(1000)).
		OrderBy("TotalWords", true).
		Limit(50).
		Build("fixture-trace-id")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	actualBytes, err := protojson.Marshal(req)
	if err != nil {
		t.Fatalf("protojson.Marshal: %v", err)
	}
	var actual map[string]interface{}
	if err := json.Unmarshal(actualBytes, &actual); err != nil {
		t.Fatalf("unmarshal actual: %v", err)
	}

	goldenPath := filepath.Join("..", "..", "Common", "testdata", "groupby-contract-1.json")
	goldenBytes, err := os.ReadFile(goldenPath)
	if err != nil {
		t.Fatalf("read golden fixture: %v", err)
	}
	var expected map[string]interface{}
	if err := json.Unmarshal(goldenBytes, &expected); err != nil {
		t.Fatalf("unmarshal golden fixture: %v", err)
	}

	if !reflect.DeepEqual(actual, expected) {
		t.Errorf("golden fixture mismatch:\n  actual:   %s\n  expected: %s", actualBytes, goldenBytes)
	}
}
