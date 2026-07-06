package iverson

import (
	"fmt"

	pb "github.com/iverson/clients/go/generated"
)

// SimilarBuilder builds a SearchSimilarRequest (Qdrant vector similarity search) using a
// fluent API. Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN,
// GREATER_THAN_OR_EQUALS, LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/
// VECTOR_SIMILAR are rejected at Build() time.
type SimilarBuilder struct {
	typeName string
	property string
	query    string
	topK     uint32
	logic    pb.SearchLogic
	filter   []*pb.SearchClause
	err      error
}

// NewSimilar creates a SimilarBuilder for the given entity type and embedded property.
func NewSimilar(typeName, property string) *SimilarBuilder {
	return &SimilarBuilder{typeName: typeName, property: property, topK: 10, logic: pb.SearchLogic_AND}
}

func (s *SimilarBuilder) Text(query string) *SimilarBuilder              { s.query = query; return s }
func (s *SimilarBuilder) TopK(topK uint32) *SimilarBuilder               { s.topK = topK; return s }
func (s *SimilarBuilder) WithLogic(logic pb.SearchLogic) *SimilarBuilder { s.logic = logic; return s }

func (s *SimilarBuilder) Where(field string, op pb.SearchOperator, value interface{}) *SimilarBuilder {
	switch op {
	case pb.SearchOperator_CONTAINS, pb.SearchOperator_STARTS_WITH,
		pb.SearchOperator_ENDS_WITH, pb.SearchOperator_VECTOR_SIMILAR:
		s.err = fmt.Errorf("operator %v is not supported by SearchSimilar filters", op)
		return s
	}
	s.filter = append(s.filter, &pb.SearchClause{
		Property: field, Operator: op, Value: toSearchValue(value), ClauseType: pb.SearchClauseType_FILTER,
	})
	return s
}

func (s *SimilarBuilder) Build(traceId ...string) (*pb.SearchSimilarRequest, error) {
	if s.err != nil {
		return nil, s.err
	}
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.SearchSimilarRequest{
		TypeName: s.typeName, Property: s.property, Query: s.query, TopK: s.topK,
		Filter: s.filter, FilterLogic: s.logic, TraceId: id,
	}, nil
}

// ChunksBuilder builds a SearchChunksRequest (Qdrant chunk/RAG search). Supports at most one
// filter clause: an EQUALS match on the entity's primary-key property.
type ChunksBuilder struct {
	typeName string
	property string
	query    string
	topK     uint32
	filter   *pb.SearchClause
	err      error
}

// NewChunks creates a ChunksBuilder for the given entity type and chunked property.
func NewChunks(typeName, property string) *ChunksBuilder {
	return &ChunksBuilder{typeName: typeName, property: property, topK: 10}
}

func (c *ChunksBuilder) Text(query string) *ChunksBuilder { c.query = query; return c }
func (c *ChunksBuilder) TopK(topK uint32) *ChunksBuilder  { c.topK = topK; return c }

func (c *ChunksBuilder) Where(field string, op pb.SearchOperator, value interface{}) *ChunksBuilder {
	if op != pb.SearchOperator_EQUALS {
		c.err = fmt.Errorf("SearchChunks only supports an EQUALS filter on the primary-key property; got %v", op)
		return c
	}
	if c.filter != nil {
		c.err = fmt.Errorf("SearchChunks supports at most one filter clause")
		return c
	}
	c.filter = &pb.SearchClause{
		Property: field, Operator: op, Value: toSearchValue(value), ClauseType: pb.SearchClauseType_FILTER,
	}
	return c
}

func (c *ChunksBuilder) Build(traceId ...string) (*pb.SearchChunksRequest, error) {
	if c.err != nil {
		return nil, c.err
	}
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	var filters []*pb.SearchClause
	if c.filter != nil {
		filters = []*pb.SearchClause{c.filter}
	}
	return &pb.SearchChunksRequest{
		TypeName: c.typeName, Property: c.property, Query: c.query, TopK: c.topK,
		Filter: filters, TraceId: id,
	}, nil
}
