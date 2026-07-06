package iverson_test

import (
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
)

func TestSimilarBuild_HappyPath_ProducesExpectedRequest(t *testing.T) {
	req, err := iverson.NewSimilar("Article", "Title").
		Text("machine learning").
		TopK(10).
		Where("Category", pb.SearchOperator_EQUALS, "Tech").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TypeName != "Article" || req.Property != "Title" || req.Query != "machine learning" || req.TopK != 10 {
		t.Errorf("unexpected request: %+v", req)
	}
	if len(req.Filter) != 1 || req.Filter[0].Property != "Category" {
		t.Errorf("unexpected filter: %+v", req.Filter)
	}
}

func TestSimilarWhere_ContainsOperator_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewSimilar("Article", "Title").
		Where("Category", pb.SearchOperator_CONTAINS, "x").
		Build()
	if err == nil {
		t.Fatal("expected an error for CONTAINS operator")
	}
}

func TestSimilarWhere_VectorSimilarOperator_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewSimilar("Article", "Title").
		Where("Category", pb.SearchOperator_VECTOR_SIMILAR, "x").
		Build()
	if err == nil {
		t.Fatal("expected an error for VECTOR_SIMILAR operator")
	}
}

func TestChunksBuild_HappyPath_ProducesExpectedRequest(t *testing.T) {
	req, err := iverson.NewChunks("Article", "Body").
		Text("neural networks").
		TopK(5).
		Where("Id", pb.SearchOperator_EQUALS, "parent-123").
		Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if req.TypeName != "Article" || req.Property != "Body" || req.TopK != 5 {
		t.Errorf("unexpected request: %+v", req)
	}
	if len(req.Filter) != 1 || req.Filter[0].Property != "Id" {
		t.Errorf("unexpected filter: %+v", req.Filter)
	}
}

func TestChunksWhere_NonEqualsOperator_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewChunks("Article", "Body").
		Where("Id", pb.SearchOperator_GREATER_THAN, "x").
		Build()
	if err == nil {
		t.Fatal("expected an error for non-EQUALS operator")
	}
}

func TestChunksWhere_CalledTwice_ReturnsBuildError(t *testing.T) {
	_, err := iverson.NewChunks("Article", "Body").
		Where("Id", pb.SearchOperator_EQUALS, "a").
		Where("Id", pb.SearchOperator_EQUALS, "b").
		Build()
	if err == nil {
		t.Fatal("expected an error for a second filter clause")
	}
}
