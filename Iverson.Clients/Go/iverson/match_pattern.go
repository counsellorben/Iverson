package iverson

import (
	pb "github.com/iverson/clients/go/generated"
)

// MatchPatternBuilder builds a MatchPatternRequest (SQL:2016 row pattern recognition) using a
// fluent API. It performs no validation: pattern and expression strings pass through unchanged
// and the server validates the request, so Build has no error return.
type MatchPatternBuilder struct {
	typeName      string
	source        pb.PatternRowSource
	chunkProperty string
	where         []*pb.SearchClause
	whereLogic    pb.SearchLogic
	partitionBy   []string
	orderBy       []*pb.SearchSort
	pattern       string
	subsets       []*pb.PatternSubset
	define        []*pb.NamedExpr
	measures      []*pb.NamedExpr
	rowsPerMatch  pb.RowsPerMatch
	afterMatch    *pb.AfterMatchSkip
	limit         int32
}

// NewMatchPattern creates a MatchPatternBuilder for the given entity type name. Every field
// starts at its proto default: TYPE_ROWS, AND, ONE_ROW, no AFTER MATCH clause (the server
// applies PAST_LAST_ROW) and limit 0 (the server applies its default).
func NewMatchPattern(typeName string) *MatchPatternBuilder {
	return &MatchPatternBuilder{typeName: typeName}
}

// Chunks matches over the chunks of the given [IversonChunk] property instead of the type's rows.
func (m *MatchPatternBuilder) Chunks(chunkProperty string) *MatchPatternBuilder {
	m.source = pb.PatternRowSource_CHUNKS
	m.chunkProperty = chunkProperty
	return m
}

// Where adds a pre-filter (FILTER) clause.
func (m *MatchPatternBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *MatchPatternBuilder {
	return m.addClause(field, op, val, pb.SearchClauseType_FILTER)
}

// Not adds a MUST_NOT pre-filter clause.
func (m *MatchPatternBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *MatchPatternBuilder {
	return m.addClause(field, op, val, pb.SearchClauseType_MUST_NOT)
}

// WithLogic sets the logic combining the pre-filter clauses. Default: AND.
func (m *MatchPatternBuilder) WithLogic(logic pb.SearchLogic) *MatchPatternBuilder {
	m.whereLogic = logic
	return m
}

// PartitionBy appends PARTITION BY columns.
func (m *MatchPatternBuilder) PartitionBy(fields ...string) *MatchPatternBuilder {
	m.partitionBy = append(m.partitionBy, fields...)
	return m
}

// OrderBy appends an ORDER BY column.
func (m *MatchPatternBuilder) OrderBy(field string, descending bool) *MatchPatternBuilder {
	m.orderBy = append(m.orderBy, &pb.SearchSort{Property: field, Descending: descending})
	return m
}

// Pattern sets the PATTERN, e.g. "A B+".
func (m *MatchPatternBuilder) Pattern(pattern string) *MatchPatternBuilder {
	m.pattern = pattern
	return m
}

// Subset appends a SUBSET name = (variables...).
func (m *MatchPatternBuilder) Subset(name string, variables ...string) *MatchPatternBuilder {
	m.subsets = append(m.subsets, &pb.PatternSubset{Name: name, Variables: variables})
	return m
}

// Define appends a DEFINE variable AS expr.
func (m *MatchPatternBuilder) Define(variable, expr string) *MatchPatternBuilder {
	m.define = append(m.define, &pb.NamedExpr{Name: variable, Expr: expr})
	return m
}

// Measure appends a MEASURES expr AS name.
func (m *MatchPatternBuilder) Measure(name, expr string) *MatchPatternBuilder {
	m.measures = append(m.measures, &pb.NamedExpr{Name: name, Expr: expr})
	return m
}

// RowsPerMatch sets ONE ROW or one of the ALL ROWS modes. Default: ONE_ROW.
func (m *MatchPatternBuilder) RowsPerMatch(mode pb.RowsPerMatch) *MatchPatternBuilder {
	m.rowsPerMatch = mode
	return m
}

// AfterMatch sets AFTER MATCH SKIP. The optional variable names the target of TO_FIRST/TO_LAST.
func (m *MatchPatternBuilder) AfterMatch(kind pb.AfterMatchSkipKind, variable ...string) *MatchPatternBuilder {
	v := ""
	if len(variable) > 0 {
		v = variable[0]
	}
	m.afterMatch = &pb.AfterMatchSkip{Kind: kind, Variable: v}
	return m
}

// Limit caps the output rows. Default: 0, which leaves the limit to the server.
func (m *MatchPatternBuilder) Limit(n int32) *MatchPatternBuilder {
	m.limit = n
	return m
}

// Build constructs the MatchPatternRequest proto. An optional traceId may be supplied.
func (m *MatchPatternBuilder) Build(traceId ...string) *pb.MatchPatternRequest {
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.MatchPatternRequest{
		TypeName:      m.typeName,
		Source:        m.source,
		ChunkProperty: m.chunkProperty,
		Where:         m.where,
		WhereLogic:    m.whereLogic,
		PartitionBy:   m.partitionBy,
		OrderBy:       m.orderBy,
		Pattern:       m.pattern,
		Subsets:       m.subsets,
		Define:        m.define,
		Measures:      m.measures,
		RowsPerMatch:  m.rowsPerMatch,
		AfterMatch:    m.afterMatch,
		Limit:         m.limit,
		TraceId:       id,
	}
}

func (m *MatchPatternBuilder) addClause(
	field string, op pb.SearchOperator, val *pb.SearchValue, ct pb.SearchClauseType) *MatchPatternBuilder {
	m.where = append(m.where, &pb.SearchClause{
		Property: field, Operator: op, Value: val, ClauseType: ct,
	})
	return m
}
