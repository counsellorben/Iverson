package iverson

import (
	"fmt"
	"strings"

	pb "github.com/iverson/clients/go/generated"
)

// GroupByBuilder builds a GroupByRequest using a fluent API.
//
// Unlike QueryBuilder, this builder runs a compound GROUP BY — multiple
// metrics (SUM/AVG/MIN/MAX/COUNT) over the same grouping in a single
// round-trip, with optional WHERE, HAVING, JOIN, ORDER BY, and LIMIT.
//
// Example:
//
//	req, err := iverson.NewGroupBy("LineItem").
//	    Keys("ReturnFlag", "LineStatus").
//	    Sum("Quantity", "SumQty").
//	    Avg("Quantity", "AvgQty").
//	    CountAll("CountOrder").
//	    Having("SumQty", pb.SearchOperator_GREATER_THAN, numberValue(0)).
//	    OrderBy("ReturnFlag").
//	    Build()
type GroupByBuilder struct {
	typeName    string
	keys        []string
	metrics     []*pb.MetricSpec
	orderBy     []*pb.SearchSort
	where       []*pb.SearchClause
	having      []*pb.SearchClause
	joins       []*pb.JoinSpec
	whereLogic  pb.SearchLogic
	havingLogic pb.SearchLogic
	limit       int32
	err         error
}

// NewGroupBy creates a GroupByBuilder for the given entity type name.
func NewGroupBy(typeName string) *GroupByBuilder {
	return &GroupByBuilder{
		typeName:    typeName,
		whereLogic:  pb.SearchLogic_AND,
		havingLogic: pb.SearchLogic_AND,
		limit:       10_000,
	}
}

// ── Group-by keys ────────────────────────────────────────────────────────────

// Key adds a single GROUP BY column.
func (g *GroupByBuilder) Key(field string) *GroupByBuilder {
	g.keys = append(g.keys, field)
	return g
}

// Keys adds multiple GROUP BY columns.
func (g *GroupByBuilder) Keys(fields ...string) *GroupByBuilder {
	g.keys = append(g.keys, fields...)
	return g
}

// ── WHERE filter (applied before grouping) ──────────────────────────────────

// Where adds a WHERE (FILTER) clause.
func (g *GroupByBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *GroupByBuilder {
	if val == nil {
		g.err = fmt.Errorf("field %q: nil search value for operator %v", field, op)
		return g
	}
	g.where = append(g.where, &pb.SearchClause{
		Property:   field,
		Operator:   op,
		Value:      val,
		ClauseType: pb.SearchClauseType_FILTER,
	})
	return g
}

// Not adds a MUST_NOT WHERE clause (excludes matches before grouping).
func (g *GroupByBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *GroupByBuilder {
	if val == nil {
		g.err = fmt.Errorf("field %q: nil search value for operator %v", field, op)
		return g
	}
	g.where = append(g.where, &pb.SearchClause{
		Property:   field,
		Operator:   op,
		Value:      val,
		ClauseType: pb.SearchClauseType_MUST_NOT,
	})
	return g
}

// WithLogic sets the logic used to combine top-level WHERE clauses. Default: AND.
func (g *GroupByBuilder) WithLogic(logic pb.SearchLogic) *GroupByBuilder {
	g.whereLogic = logic
	return g
}

// ── HAVING (applied after grouping; references output alias names) ─────────

// Having adds a HAVING clause. alias must match a metric's output alias.
func (g *GroupByBuilder) Having(alias string, op pb.SearchOperator, val *pb.SearchValue) *GroupByBuilder {
	if val == nil {
		g.err = fmt.Errorf("having %q: nil search value for operator %v", alias, op)
		return g
	}
	g.having = append(g.having, &pb.SearchClause{
		Property:   alias,
		Operator:   op,
		Value:      val,
		ClauseType: pb.SearchClauseType_FILTER,
	})
	return g
}

// WithHavingLogic sets the logic combining HAVING clauses. Default: AND.
func (g *GroupByBuilder) WithHavingLogic(logic pb.SearchLogic) *GroupByBuilder {
	g.havingLogic = logic
	return g
}

// ── JOIN ──────────────────────────────────────────────────────────────────

// Join adds a join from this type to rightType on the given fields.
// The join kind defaults to INNER; pass an explicit pb.JoinKind to override.
func (g *GroupByBuilder) Join(leftField, rightType, rightField string, opts ...pb.JoinKind) *GroupByBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	g.joins = append(g.joins, &pb.JoinSpec{
		LeftType:   g.typeName,
		RightType:  rightType,
		LeftField:  leftField,
		RightField: rightField,
		Kind:       kind,
	})
	return g
}

// ── Metrics — simple field ──────────────────────────────────────────────────

// Sum adds a SUM metric. Default alias: "{field}_sum".
func (g *GroupByBuilder) Sum(field string, alias ...string) *GroupByBuilder {
	return g.addMetric(pb.AggregationType_SUM, field, "", resolveAlias(alias, field, "sum"))
}

// Avg adds an AVG metric. Default alias: "{field}_avg".
func (g *GroupByBuilder) Avg(field string, alias ...string) *GroupByBuilder {
	return g.addMetric(pb.AggregationType_AVG, field, "", resolveAlias(alias, field, "avg"))
}

// Min adds a MIN metric. Default alias: "{field}_min".
func (g *GroupByBuilder) Min(field string, alias ...string) *GroupByBuilder {
	return g.addMetric(pb.AggregationType_MIN, field, "", resolveAlias(alias, field, "min"))
}

// Max adds a MAX metric. Default alias: "{field}_max".
func (g *GroupByBuilder) Max(field string, alias ...string) *GroupByBuilder {
	return g.addMetric(pb.AggregationType_MAX, field, "", resolveAlias(alias, field, "max"))
}

// Count adds a COUNT metric on a specific field. Default alias: "{field}_count".
func (g *GroupByBuilder) Count(field string, alias ...string) *GroupByBuilder {
	return g.addMetric(pb.AggregationType_COUNT, field, "", resolveAlias(alias, field, "count"))
}

// CountAll adds a COUNT(*) metric — no field. Default alias: "count".
func (g *GroupByBuilder) CountAll(alias ...string) *GroupByBuilder {
	name := "count"
	if len(alias) > 0 {
		name = alias[0]
	}
	return g.addMetric(pb.AggregationType_COUNT, "", "", name)
}

// ── Metrics — expression (raw SQL) ──────────────────────────────────────────

// SumExpr adds a SUM metric over a raw SQL expression, e.g. "Price * (1 - Discount)".
func (g *GroupByBuilder) SumExpr(expression, alias string) *GroupByBuilder {
	return g.addMetric(pb.AggregationType_SUM, "", expression, alias)
}

// AvgExpr adds an AVG metric over a raw SQL expression.
func (g *GroupByBuilder) AvgExpr(expression, alias string) *GroupByBuilder {
	return g.addMetric(pb.AggregationType_AVG, "", expression, alias)
}

// ── Ordering and limit ───────────────────────────────────────────────────────

// OrderBy adds a sort clause (typically referencing a group-by key or metric alias).
// Pass true as the optional descending argument for a descending sort.
func (g *GroupByBuilder) OrderBy(field string, descending ...bool) *GroupByBuilder {
	desc := false
	if len(descending) > 0 {
		desc = descending[0]
	}
	g.orderBy = append(g.orderBy, &pb.SearchSort{Property: field, Descending: desc})
	return g
}

// OrderByDesc adds a descending sort clause.
func (g *GroupByBuilder) OrderByDesc(field string) *GroupByBuilder {
	return g.OrderBy(field, true)
}

// Limit sets the max number of grouped rows returned. Default: 10000.
func (g *GroupByBuilder) Limit(n int32) *GroupByBuilder {
	g.limit = n
	return g
}

// ── Build ─────────────────────────────────────────────────────────────────

// Build constructs the GroupByRequest proto. An optional traceId may be supplied.
func (g *GroupByBuilder) Build(traceId ...string) (*pb.GroupByRequest, error) {
	if g.err != nil {
		return nil, g.err
	}

	aliases := map[string]bool{}
	for _, m := range g.metrics {
		key := strings.ToLower(m.Name)
		if aliases[key] {
			return nil, fmt.Errorf("duplicate metric alias %q", m.Name)
		}
		aliases[key] = true
	}
	keys := map[string]bool{}
	for _, k := range g.keys {
		key := strings.ToLower(k)
		if keys[key] {
			return nil, fmt.Errorf("duplicate key %q", k)
		}
		keys[key] = true
		if aliases[key] {
			return nil, fmt.Errorf("key %q collides with an existing metric alias", k)
		}
		aliases[key] = true
	}
	for _, h := range g.having {
		if !aliases[strings.ToLower(h.Property)] {
			return nil, fmt.Errorf(
				"HAVING references %q, which is neither a metric alias nor a key", h.Property)
		}
	}
	for _, s := range g.orderBy {
		if !aliases[strings.ToLower(s.Property)] {
			return nil, fmt.Errorf(
				"OrderBy references %q, which is neither a metric alias nor a key", s.Property)
		}
	}

	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.GroupByRequest{
		TypeName: g.typeName,
		Query: &pb.SearchQuery{
			Clauses: g.where,
			Logic:   g.whereLogic,
		},
		Keys:    g.keys,
		Metrics: g.metrics,
		Having: &pb.SearchQuery{
			Clauses: g.having,
			Logic:   g.havingLogic,
		},
		OrderBy: g.orderBy,
		Limit:   g.limit,
		Joins:   g.joins,
		TraceId: id,
	}, nil
}

// ── Internal helpers ─────────────────────────────────────────────────────────

func (g *GroupByBuilder) addMetric(aggType pb.AggregationType, field, expression, alias string) *GroupByBuilder {
	g.metrics = append(g.metrics, &pb.MetricSpec{
		Name:       alias,
		Type:       aggType,
		Field:      field,
		Expression: expression,
	})
	return g
}

// resolveAlias returns the first element of alias if present, else "{field}_{suffix}".
func resolveAlias(alias []string, field, suffix string) string {
	if len(alias) > 0 {
		return alias[0]
	}
	return fmt.Sprintf("%s_%s", field, suffix)
}
