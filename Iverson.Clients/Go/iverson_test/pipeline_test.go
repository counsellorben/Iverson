package iverson_test

import (
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
)

func numberVal(n float64) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_NumberVal{NumberVal: n}}
}

func boolVal(b bool) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_BoolVal{BoolVal: b}}
}

func TestPipelineBuildFull(t *testing.T) {
	req, err := iverson.NewPipeline("Article").
		Where("IsPublished", pb.SearchOperator_EQUALS, boolVal(true)).
		Step("by_author", func(s *iverson.PipelineStepBuilder) {
			s.GroupBy("AuthorId").
				CountAll("articles").
				Having("articles", pb.SearchOperator_GREATER_THAN, numberVal(5))
		}).
		Step("ranked", func(s *iverson.PipelineStepBuilder) {
			s.RowNumber("rank", "", "articles", true)
		}).
		Step("named", func(s *iverson.PipelineStepBuilder) {
			s.Join("Author", "AuthorId", "Id").
				SelectAllFrom("ranked").
				SelectPick("Author", "Name", "author_name")
		}).
		SortOnDesc("rank").
		Limit(5).
		Build()

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.TypeName != "Article" || len(req.Steps) != 3 || req.Limit != 5 {
		t.Fatalf("unexpected request shape: %+v", req)
	}
	if req.Steps[0].GroupBy[0].Field != "AuthorId" {
		t.Errorf("groupBy field = %q", req.Steps[0].GroupBy[0].Field)
	}
	if req.Steps[1].Windows[0].Kind != pb.WindowFunctionKind_ROW_NUMBER ||
		!req.Steps[1].Windows[0].Descending {
		t.Errorf("window = %+v", req.Steps[1].Windows[0])
	}
	if req.Steps[2].Joins[0].Source != "Author" ||
		req.Steps[2].Joins[0].On[0].Left != "AuthorId" {
		t.Errorf("join = %+v", req.Steps[2].Joins[0])
	}
	if !req.Steps[2].Select[0].All || req.Steps[2].Select[1].Alias != "author_name" {
		t.Errorf("select = %+v", req.Steps[2].Select)
	}
}

func TestPipelineDuplicateStepNameErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("x", func(s *iverson.PipelineStepBuilder) { s.Derive("a", "WordCount") }).
		Step("X", func(s *iverson.PipelineStepBuilder) { s.Derive("b", "WordCount") }).
		Build()
	if err == nil {
		t.Fatal("expected duplicate step name error")
	}
}

func TestPipelineReadsUnknownStepErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("a", func(s *iverson.PipelineStepBuilder) { s.Reads("nope") }).
		Build()
	if err == nil {
		t.Fatal("expected unknown reads error")
	}
}

func TestPipelineWindowAndGroupByErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("bad", func(s *iverson.PipelineStepBuilder) {
			s.RowNumber("rn", "", "Id", false).GroupBy("AuthorId").CountAll("n")
		}).
		Build()
	if err == nil {
		t.Fatal("expected windows-XOR-aggregation error")
	}
}

func TestPipelineJoinWithoutSelectErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("bad", func(s *iverson.PipelineStepBuilder) {
			s.Join("Author", "AuthorId", "Id")
		}).
		Build()
	if err == nil {
		t.Fatal("expected join-requires-select error")
	}
}

func TestPipelineDuplicateAliasErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("bad", func(s *iverson.PipelineStepBuilder) {
			s.RowNumber("x", "", "Id", false).Derive("X", "WordCount + 1")
		}).
		Build()
	if err == nil {
		t.Fatal("expected duplicate alias error")
	}
}
