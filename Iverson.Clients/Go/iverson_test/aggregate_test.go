package iverson_test

import (
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
)

func TestAggregateNotAddsMustNot(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		CountAll("n").
		Not("Category", pb.SearchOperator_EQUALS, strVal("spam")).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_MUST_NOT {
		t.Errorf("clause type = %v", req.Query.Clauses[0].ClauseType)
	}
}

func TestAggregateWhereAddsFilter(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		CountAll("n").
		Where("Category", pb.SearchOperator_EQUALS, strVal("tech")).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_FILTER {
		t.Errorf("clause type = %v", req.Query.Clauses[0].ClauseType)
	}
}

func TestAggregateWithLogicOr(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		CountAll("n").
		Where("Category", pb.SearchOperator_EQUALS, strVal("tech")).
		WithLogic(pb.SearchLogic_OR).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Query.Logic != pb.SearchLogic_OR {
		t.Errorf("query logic = %v", req.Query.Logic)
	}
}

func TestAggregateWithHavingLogicOr(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		CountAll("n").
		Having("metric_val", pb.SearchOperator_GREATER_THAN, numberVal(5)).
		WithHavingLogic(pb.SearchLogic_OR).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Having.Logic != pb.SearchLogic_OR {
		t.Errorf("having logic = %v", req.Having.Logic)
	}
}

func TestAggregateJoinDefaultsToInner(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		CountAll("n").
		Join("AuthorId", "Author", "Id").
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(req.Joins) != 1 {
		t.Fatalf("expected 1 join, got %d", len(req.Joins))
	}
	j := req.Joins[0]
	if j.LeftType != "Article" || j.RightType != "Author" || j.LeftField != "AuthorId" || j.RightField != "Id" {
		t.Errorf("unexpected join: %+v", j)
	}
	if j.Kind != pb.JoinKind_INNER {
		t.Errorf("kind = %v, want INNER", j.Kind)
	}
}

func TestAggregateJoinExplicitKind(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		CountAll("n").
		Join("AuthorId", "Author", "Id", pb.JoinKind_LEFT).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Joins[0].Kind != pb.JoinKind_LEFT {
		t.Errorf("kind = %v, want LEFT", req.Joins[0].Kind)
	}
}

func TestAggregateTermsDefaultSize(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		Terms("Category", "byCategory").
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	spec := req.Aggregations[0]
	if spec.Type != pb.AggregationType_TERMS {
		t.Errorf("type = %v, want TERMS", spec.Type)
	}
	if spec.Field != "Category" {
		t.Errorf("field = %q", spec.Field)
	}
	if spec.Name != "byCategory" {
		t.Errorf("name = %q", spec.Name)
	}
	if spec.Size != 10 {
		t.Errorf("size = %d, want default 10", spec.Size)
	}
}

func TestAggregateTermsExplicitSize(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		Terms("Category", "byCategory", 25).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Aggregations[0].Size != 25 {
		t.Errorf("size = %d, want 25", req.Aggregations[0].Size)
	}
}

func TestAggregateDateHistogramDefaultsUTC(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		DateHistogram("PublishedAt", "byMonth", "month").
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	spec := req.Aggregations[0]
	if spec.Type != pb.AggregationType_DATE_HISTOGRAM {
		t.Errorf("type = %v, want DATE_HISTOGRAM", spec.Type)
	}
	if spec.CalendarInterval != "month" {
		t.Errorf("calendar interval = %q", spec.CalendarInterval)
	}
	if spec.TimeZone != "" {
		t.Errorf("time zone = %q, want empty (server default UTC)", spec.TimeZone)
	}
}

func TestAggregateDateHistogramExplicitTimeZone(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		DateHistogram("PublishedAt", "byMonth", "month", "America/New_York").
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Aggregations[0].TimeZone != "America/New_York" {
		t.Errorf("time zone = %q", req.Aggregations[0].TimeZone)
	}
}

func TestAggregateRangeBuckets(t *testing.T) {
	low := 0.0
	mid := 100.0
	req, err := iverson.NewAggregate("Article").
		Range("Price", "byPrice",
			iverson.RangeBucket{Key: "cheap", From: &low, To: &mid},
			iverson.RangeBucket{Key: "expensive", From: &mid},
		).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	spec := req.Aggregations[0]
	if spec.Type != pb.AggregationType_RANGE {
		t.Errorf("type = %v, want RANGE", spec.Type)
	}
	if len(spec.RangeBuckets) != 2 {
		t.Fatalf("expected 2 buckets, got %d", len(spec.RangeBuckets))
	}
	b0 := spec.RangeBuckets[0]
	if b0.Key != "cheap" || b0.From.GetValue() != 0 || b0.To.GetValue() != 100 {
		t.Errorf("unexpected bucket 0: %+v", b0)
	}
	b1 := spec.RangeBuckets[1]
	if b1.Key != "expensive" || b1.From.GetValue() != 100 || b1.To != nil {
		t.Errorf("unexpected bucket 1: %+v", b1)
	}
}

func TestAggregateMetrics(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		Avg("WordCount", "avgWords").
		Sum("WordCount", "sumWords").
		Min("WordCount", "minWords").
		Max("WordCount", "maxWords").
		Count("Category", "catCount").
		CountAll("total").
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(req.Aggregations) != 6 {
		t.Fatalf("expected 6 aggregations, got %d", len(req.Aggregations))
	}
	wantTypes := []pb.AggregationType{
		pb.AggregationType_AVG, pb.AggregationType_SUM, pb.AggregationType_MIN,
		pb.AggregationType_MAX, pb.AggregationType_COUNT, pb.AggregationType_COUNT,
	}
	for i, want := range wantTypes {
		if req.Aggregations[i].Type != want {
			t.Errorf("aggregation %d: type = %v, want %v", i, req.Aggregations[i].Type, want)
		}
	}
	if req.Aggregations[5].Field != "" {
		t.Errorf("CountAll field = %q, want empty", req.Aggregations[5].Field)
	}
}

func TestAggregateDuplicateNameErrors(t *testing.T) {
	_, err := iverson.NewAggregate("Article").
		CountAll("n").
		Sum("WordCount", "n").
		Build()
	if err == nil {
		t.Fatal("expected duplicate aggregation name error")
	}
}

func TestAggregateBuildSetsTraceId(t *testing.T) {
	req, err := iverson.NewAggregate("Article").
		CountAll("n").
		Build("trace-123")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.TraceId != "trace-123" {
		t.Errorf("trace id = %q", req.TraceId)
	}
}

func TestAggregateNilValuePropagatesBuildError(t *testing.T) {
	_, err := iverson.NewAggregate("Article").
		CountAll("n").
		Where("Category", pb.SearchOperator_EQUALS, nil).
		Build()
	if err == nil {
		t.Fatal("expected error for nil search value")
	}
}
