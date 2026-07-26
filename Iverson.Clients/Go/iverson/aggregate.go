package iverson

import (
	"fmt"
	"strings"

	pb "github.com/iverson/clients/go/generated"
	"google.golang.org/protobuf/types/known/wrapperspb"
)

// AggregateBuilder builds an AggregateRequest using a fluent API.
//
// Unlike GroupByBuilder (one compound SELECT with all metrics as columns), each entry here
// becomes its own AggregationSpec (one SQL query per spec on the server). Not generic on a
// type parameter — joins bring multiple registered types into scope, so filters and
// aggregation fields are addressed by raw field-name strings, same as GroupByBuilder.
//
// Example:
//
//	req, err := iverson.NewAggregate("Article").
//	    Terms("Category", "byCategory").
//	    Avg("WordCount", "avgWords").
//	    Build()
type AggregateBuilder struct {
	typeName     string
	aggregations []*pb.AggregationSpec
	where        []*pb.SearchClause
	having       []*pb.SearchClause
	joins        []*pb.JoinSpec
	whereLogic   pb.SearchLogic
	havingLogic  pb.SearchLogic
	err          error
}

// NewAggregate creates an AggregateBuilder for the given entity type name.
func NewAggregate(typeName string) *AggregateBuilder {
	return &AggregateBuilder{
		typeName:    typeName,
		whereLogic:  pb.SearchLogic_AND,
		havingLogic: pb.SearchLogic_AND,
	}
}

// ── WHERE filter (applied before aggregating) ───────────────────────────────

// Where adds a WHERE (FILTER) clause.
func (a *AggregateBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *AggregateBuilder {
	if val == nil {
		a.err = fmt.Errorf("field %q: nil search value for operator %v", field, op)
		return a
	}
	a.where = append(a.where, &pb.SearchClause{
		Property:   field,
		Operator:   op,
		Value:      val,
		ClauseType: pb.SearchClauseType_FILTER,
	})
	return a
}

// Not adds a MUST_NOT WHERE clause (excludes matches before aggregating).
func (a *AggregateBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *AggregateBuilder {
	if val == nil {
		a.err = fmt.Errorf("field %q: nil search value for operator %v", field, op)
		return a
	}
	a.where = append(a.where, &pb.SearchClause{
		Property:   field,
		Operator:   op,
		Value:      val,
		ClauseType: pb.SearchClauseType_MUST_NOT,
	})
	return a
}

// WithLogic sets the logic used to combine top-level WHERE clauses. Default: AND.
func (a *AggregateBuilder) WithLogic(logic pb.SearchLogic) *AggregateBuilder {
	a.whereLogic = logic
	return a
}

// ── HAVING (applied after aggregating; references output alias names) ──────

// Having adds a HAVING clause. alias must match an aggregation's output name.
func (a *AggregateBuilder) Having(alias string, op pb.SearchOperator, val *pb.SearchValue) *AggregateBuilder {
	if val == nil {
		a.err = fmt.Errorf("having %q: nil search value for operator %v", alias, op)
		return a
	}
	a.having = append(a.having, &pb.SearchClause{
		Property:   alias,
		Operator:   op,
		Value:      val,
		ClauseType: pb.SearchClauseType_FILTER,
	})
	return a
}

// WithHavingLogic sets the logic combining HAVING clauses. Default: AND.
func (a *AggregateBuilder) WithHavingLogic(logic pb.SearchLogic) *AggregateBuilder {
	a.havingLogic = logic
	return a
}

// ── JOIN ──────────────────────────────────────────────────────────────────

// Join adds a join from this type to rightType on the given fields.
// The join kind defaults to INNER; pass an explicit pb.JoinKind to override.
func (a *AggregateBuilder) Join(leftField, rightType, rightField string, opts ...pb.JoinKind) *AggregateBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	a.joins = append(a.joins, &pb.JoinSpec{
		LeftType:   a.typeName,
		RightType:  rightType,
		LeftField:  leftField,
		RightField: rightField,
		Kind:       kind,
	})
	return a
}

// ── Aggregations — bucketing ────────────────────────────────────────────────

// Terms adds a TERMS bucket aggregation. Default size: 10.
func (a *AggregateBuilder) Terms(field, name string, size ...int32) *AggregateBuilder {
	n := int32(10)
	if len(size) > 0 {
		n = size[0]
	}
	a.aggregations = append(a.aggregations, &pb.AggregationSpec{
		Name:  name,
		Type:  pb.AggregationType_TERMS,
		Field: field,
		Size:  n,
	})
	return a
}

// DateHistogram adds a DATE_HISTOGRAM bucket aggregation. timeZone defaults to
// empty, which the server interprets as UTC.
func (a *AggregateBuilder) DateHistogram(field, name, calendarInterval string, timeZone ...string) *AggregateBuilder {
	tz := ""
	if len(timeZone) > 0 {
		tz = timeZone[0]
	}
	a.aggregations = append(a.aggregations, &pb.AggregationSpec{
		Name:             name,
		Type:             pb.AggregationType_DATE_HISTOGRAM,
		Field:            field,
		CalendarInterval: calendarInterval,
		TimeZone:         tz,
	})
	return a
}

// RangeBucket is a client-side range-bucket definition for AggregateBuilder.Range.
// From/To are nil for an unbounded side.
type RangeBucket struct {
	Key  string
	From *float64
	To   *float64
}

// Range adds a RANGE bucket aggregation over one or more RangeBucket definitions.
func (a *AggregateBuilder) Range(field, name string, buckets ...RangeBucket) *AggregateBuilder {
	spec := &pb.AggregationSpec{Name: name, Type: pb.AggregationType_RANGE, Field: field}
	for _, b := range buckets {
		rb := &pb.RangeBucket{Key: b.Key}
		if b.From != nil {
			rb.From = wrapperspb.Double(*b.From)
		}
		if b.To != nil {
			rb.To = wrapperspb.Double(*b.To)
		}
		spec.RangeBuckets = append(spec.RangeBuckets, rb)
	}
	a.aggregations = append(a.aggregations, spec)
	return a
}

// ── Aggregations — metrics ─────────────────────────────────────────────────

// Avg adds an AVG metric aggregation.
func (a *AggregateBuilder) Avg(field, name string) *AggregateBuilder {
	return a.addMetric(pb.AggregationType_AVG, field, name)
}

// Sum adds a SUM metric aggregation.
func (a *AggregateBuilder) Sum(field, name string) *AggregateBuilder {
	return a.addMetric(pb.AggregationType_SUM, field, name)
}

// Min adds a MIN metric aggregation.
func (a *AggregateBuilder) Min(field, name string) *AggregateBuilder {
	return a.addMetric(pb.AggregationType_MIN, field, name)
}

// Max adds a MAX metric aggregation.
func (a *AggregateBuilder) Max(field, name string) *AggregateBuilder {
	return a.addMetric(pb.AggregationType_MAX, field, name)
}

// Count adds a COUNT metric aggregation on a specific field.
func (a *AggregateBuilder) Count(field, name string) *AggregateBuilder {
	return a.addMetric(pb.AggregationType_COUNT, field, name)
}

// CountAll adds a COUNT(*) metric aggregation — no field.
func (a *AggregateBuilder) CountAll(name string) *AggregateBuilder {
	return a.addMetric(pb.AggregationType_COUNT, "", name)
}

// ── Build ─────────────────────────────────────────────────────────────────

// Build constructs the AggregateRequest proto. An optional traceId may be supplied.
func (a *AggregateBuilder) Build(traceId ...string) (*pb.AggregateRequest, error) {
	if a.err != nil {
		return nil, a.err
	}

	names := map[string]bool{}
	for _, spec := range a.aggregations {
		key := strings.ToLower(spec.Name)
		if names[key] {
			return nil, fmt.Errorf("duplicate aggregation name %q", spec.Name)
		}
		names[key] = true
	}

	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.AggregateRequest{
		TypeName: a.typeName,
		Query: &pb.SearchQuery{
			Clauses: a.where,
			Logic:   a.whereLogic,
		},
		Aggregations: a.aggregations,
		Having: &pb.SearchQuery{
			Clauses: a.having,
			Logic:   a.havingLogic,
		},
		Joins:   a.joins,
		TraceId: id,
	}, nil
}

// ── Internal helpers ─────────────────────────────────────────────────────────

func (a *AggregateBuilder) addMetric(aggType pb.AggregationType, field, name string) *AggregateBuilder {
	a.aggregations = append(a.aggregations, &pb.AggregationSpec{
		Name:  name,
		Type:  aggType,
		Field: field,
	})
	return a
}
