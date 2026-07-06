package iverson

import (
	"fmt"

	pb "github.com/iverson/clients/go/generated"
)

// QueryBuilder builds a SearchRequest using a fluent API.
//
// Example:
//
//	req, err := iverson.NewQuery("Article").
//	    Where("Category").Eq("tech").
//	    OrderByDesc("PublishedAt").
//	    Limit(20).
//	    Offset(0).
//	    Build()
type QueryBuilder struct {
	typeName string
	clauses  []*pb.SearchClause
	sorts    []*pb.SearchSort
	logic    pb.SearchLogic
	page     int32
	pageSize int32
	fields   []string
	joins    []*pb.JoinSpec
	err      error
}

// NewQuery creates a QueryBuilder for the given entity type name.
func NewQuery(typeName string) *QueryBuilder {
	return &QueryBuilder{
		typeName: typeName,
		logic:    pb.SearchLogic_AND,
		pageSize: 20,
	}
}

// Logic sets the logical operator (AND / OR) for combining filter clauses.
func (q *QueryBuilder) Logic(l pb.SearchLogic) *QueryBuilder {
	q.logic = l
	return q
}

// Limit sets the page size.
func (q *QueryBuilder) Limit(n int) *QueryBuilder {
	q.pageSize = int32(n)
	return q
}

// Offset sets the page number (0-based).
func (q *QueryBuilder) Offset(page int) *QueryBuilder {
	q.page = int32(page)
	return q
}

// Fields restricts the returned properties to the named fields.
func (q *QueryBuilder) Fields(names ...string) *QueryBuilder {
	q.fields = append(q.fields, names...)
	return q
}

// OrderBy adds an ascending sort on the given field.
func (q *QueryBuilder) OrderBy(field string) *QueryBuilder {
	q.sorts = append(q.sorts, &pb.SearchSort{Property: field, Descending: false})
	return q
}

// OrderByDesc adds a descending sort on the given field.
func (q *QueryBuilder) OrderByDesc(field string) *QueryBuilder {
	q.sorts = append(q.sorts, &pb.SearchSort{Property: field, Descending: true})
	return q
}

// Where begins a filter clause for the given field name.
// Returns a *FieldCondition that accepts one operator call to complete the clause.
func (q *QueryBuilder) Where(field string) *FieldCondition {
	return &FieldCondition{qb: q, field: field, clauseType: pb.SearchClauseType_FILTER}
}

// MustNot begins a MUST_NOT clause.
func (q *QueryBuilder) MustNot(field string) *FieldCondition {
	return &FieldCondition{qb: q, field: field, clauseType: pb.SearchClauseType_MUST_NOT}
}

// Join adds a join from this type to rightType on the given fields.
// The join kind defaults to INNER; pass an explicit pb.JoinKind to override.
func (q *QueryBuilder) Join(leftField, rightType, rightField string, opts ...pb.JoinKind) *QueryBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	q.joins = append(q.joins, &pb.JoinSpec{
		LeftType:   q.typeName,
		RightType:  rightType,
		LeftField:  leftField,
		RightField: rightField,
		Kind:       kind,
	})
	return q
}

// Build constructs the SearchRequest proto.
func (q *QueryBuilder) Build() (*pb.SearchRequest, error) {
	if q.err != nil {
		return nil, q.err
	}
	return &pb.SearchRequest{
		TypeName: q.typeName,
		Query: &pb.SearchQuery{
			Clauses: q.clauses,
			Logic:   q.logic,
			Sort:    q.sorts,
		},
		Page:     q.page,
		PageSize: q.pageSize,
		Fields:   q.fields,
		Joins:    q.joins,
	}, nil
}

// addClause appends a completed SearchClause to the builder.
func (q *QueryBuilder) addClause(clause *pb.SearchClause) {
	q.clauses = append(q.clauses, clause)
}

// ── FieldCondition ─────────────────────────────────────────────────────────────

// FieldCondition holds a pending filter clause awaiting an operator call.
// Each operator method finalises the clause and returns the parent *QueryBuilder
// for continued chaining.
type FieldCondition struct {
	qb         *QueryBuilder
	field      string
	clauseType pb.SearchClauseType
}

// Eq adds an EQUALS clause: field == value.
func (f *FieldCondition) Eq(value interface{}) *QueryBuilder {
	return f.addClause(pb.SearchOperator_EQUALS, toSearchValue(value))
}

// NotEq adds a NOT_EQUALS clause.
func (f *FieldCondition) NotEq(value interface{}) *QueryBuilder {
	return f.addClause(pb.SearchOperator_NOT_EQUALS, toSearchValue(value))
}

// Contains adds a CONTAINS clause (substring or array membership).
func (f *FieldCondition) Contains(value string) *QueryBuilder {
	return f.addClause(pb.SearchOperator_CONTAINS, stringValue(value))
}

// StartsWith adds a STARTS_WITH clause.
func (f *FieldCondition) StartsWith(value string) *QueryBuilder {
	return f.addClause(pb.SearchOperator_STARTS_WITH, stringValue(value))
}

// EndsWith adds an ENDS_WITH clause.
func (f *FieldCondition) EndsWith(value string) *QueryBuilder {
	return f.addClause(pb.SearchOperator_ENDS_WITH, stringValue(value))
}

// Gt adds a GREATER_THAN clause.
func (f *FieldCondition) Gt(value interface{}) *QueryBuilder {
	return f.addClause(pb.SearchOperator_GREATER_THAN, toSearchValue(value))
}

// Lt adds a LESS_THAN clause.
func (f *FieldCondition) Lt(value interface{}) *QueryBuilder {
	return f.addClause(pb.SearchOperator_LESS_THAN, toSearchValue(value))
}

// Gte adds a GREATER_THAN_OR_EQUALS clause.
func (f *FieldCondition) Gte(value interface{}) *QueryBuilder {
	return f.addClause(pb.SearchOperator_GREATER_THAN_OR_EQUALS, toSearchValue(value))
}

// Lte adds a LESS_THAN_OR_EQUALS clause.
func (f *FieldCondition) Lte(value interface{}) *QueryBuilder {
	return f.addClause(pb.SearchOperator_LESS_THAN_OR_EQUALS, toSearchValue(value))
}

// In adds an IN clause (value must be one of the provided strings).
func (f *FieldCondition) In(values ...string) *QueryBuilder {
	sv := &pb.SearchValue{
		Kind: &pb.SearchValue_StringList{
			StringList: &pb.RepeatedString{Values: values},
		},
	}
	return f.addClause(pb.SearchOperator_IN, sv)
}

// addClause finalises this FieldCondition as a SearchClause and returns the builder.
func (f *FieldCondition) addClause(op pb.SearchOperator, value *pb.SearchValue) *QueryBuilder {
	if value == nil {
		f.qb.err = fmt.Errorf("field %q: nil search value for operator %v", f.field, op)
		return f.qb
	}
	clause := &pb.SearchClause{
		Property:   f.field,
		Operator:   op,
		Value:      value,
		ClauseType: f.clauseType,
	}
	f.qb.addClause(clause)
	return f.qb
}

// ── Value helpers ─────────────────────────────────────────────────────────────

// toSearchValue converts a Go scalar value to a SearchValue proto.
func toSearchValue(v interface{}) *pb.SearchValue {
	switch val := v.(type) {
	case string:
		return stringValue(val)
	case int:
		return numberValue(float64(val))
	case int32:
		return numberValue(float64(val))
	case int64:
		return numberValue(float64(val))
	case float32:
		return numberValue(float64(val))
	case float64:
		return numberValue(val)
	case bool:
		return boolValue(val)
	default:
		return stringValue(fmt.Sprintf("%v", v))
	}
}

func stringValue(s string) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_StringVal{StringVal: s}}
}

func numberValue(n float64) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_NumberVal{NumberVal: n}}
}

func boolValue(b bool) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_BoolVal{BoolVal: b}}
}
