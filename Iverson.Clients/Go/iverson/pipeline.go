package iverson

import (
	"fmt"
	"strings"

	pb "github.com/iverson/clients/go/generated"
)

const baseStepName = "base"

// PipelineBuilder builds a PipelineRequest using a fluent API. Each Step is exactly
// one CTE in the generated StarRocks query; steps read the previous step by default,
// or any earlier named step via Reads. Errors accumulate and surface at Build(),
// matching GroupByBuilder.
type PipelineBuilder struct {
	typeName  string
	baseWhere []*pb.SearchClause
	baseLogic pb.SearchLogic
	steps     []*pb.PipelineStep
	orderBy   []*pb.SearchSort
	limit     int32
	err       error
}

// NewPipeline creates a PipelineBuilder for the given entity type name.
func NewPipeline(typeName string) *PipelineBuilder {
	return &PipelineBuilder{
		typeName:  typeName,
		baseLogic: pb.SearchLogic_AND,
		limit:     10_000,
	}
}

// Where adds a WHERE (FILTER) clause on the implicit base step.
func (p *PipelineBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineBuilder {
	return p.addBaseClause(field, op, val, pb.SearchClauseType_FILTER)
}

// Not adds a MUST_NOT clause on the implicit base step.
func (p *PipelineBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineBuilder {
	return p.addBaseClause(field, op, val, pb.SearchClauseType_MUST_NOT)
}

// WithLogic sets the logic combining base-step WHERE clauses. Default: AND.
func (p *PipelineBuilder) WithLogic(logic pb.SearchLogic) *PipelineBuilder {
	p.baseLogic = logic
	return p
}

// Step adds one named step (= one CTE).
func (p *PipelineBuilder) Step(name string, configure func(*PipelineStepBuilder)) *PipelineBuilder {
	if name == "" {
		p.fail(fmt.Errorf("step name must be non-empty"))
		return p
	}
	if strings.EqualFold(name, baseStepName) {
		p.fail(fmt.Errorf("step name %q is reserved for the implicit base step", name))
		return p
	}
	for _, s := range p.steps {
		if strings.EqualFold(s.Name, name) {
			p.fail(fmt.Errorf("duplicate step name %q", name))
			return p
		}
	}

	earlier := []string{baseStepName}
	for _, s := range p.steps {
		earlier = append(earlier, s.Name)
	}

	sb := &PipelineStepBuilder{name: name, earlier: earlier,
		step: &pb.PipelineStep{Name: name, WhereLogic: pb.SearchLogic_AND}}
	configure(sb)
	built, err := sb.buildStep()
	if err != nil {
		p.fail(err)
		return p
	}
	p.steps = append(p.steps, built)
	return p
}

// SortOn adds a final ORDER BY on the last step's output.
func (p *PipelineBuilder) SortOn(field string, descending ...bool) *PipelineBuilder {
	desc := false
	if len(descending) > 0 {
		desc = descending[0]
	}
	p.orderBy = append(p.orderBy, &pb.SearchSort{Property: field, Descending: desc})
	return p
}

// SortOnDesc adds a descending final sort.
func (p *PipelineBuilder) SortOnDesc(field string) *PipelineBuilder {
	return p.SortOn(field, true)
}

// Limit sets the final row limit. Default: 10000.
func (p *PipelineBuilder) Limit(n int32) *PipelineBuilder {
	p.limit = n
	return p
}

// Build constructs the PipelineRequest proto. An optional traceId may be supplied.
func (p *PipelineBuilder) Build(traceId ...string) (*pb.PipelineRequest, error) {
	if p.err != nil {
		return nil, p.err
	}
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.PipelineRequest{
		TypeName:  p.typeName,
		BaseWhere: p.baseWhere,
		BaseLogic: p.baseLogic,
		Steps:     p.steps,
		OrderBy:   p.orderBy,
		Limit:     p.limit,
		TraceId:   id,
	}, nil
}

func (p *PipelineBuilder) addBaseClause(
	field string, op pb.SearchOperator, val *pb.SearchValue, ct pb.SearchClauseType) *PipelineBuilder {
	if val == nil {
		p.fail(fmt.Errorf("field %q: nil search value for operator %v", field, op))
		return p
	}
	p.baseWhere = append(p.baseWhere, &pb.SearchClause{
		Property: field, Operator: op, Value: val, ClauseType: ct,
	})
	return p
}

func (p *PipelineBuilder) fail(err error) {
	if p.err == nil {
		p.err = err
	}
}

// PipelineStepBuilder builds one pipeline step (= one CTE). Go deviations from the
// other clients, matching this package's conventions: Where/Having take raw
// *pb.SearchValue; window partitionBy is an explicit arg where "" means none; the
// projection uses flat SelectAllFrom/SelectPick instead of a lambda sub-builder.
type PipelineStepBuilder struct {
	name    string
	earlier []string
	step    *pb.PipelineStep
	err     error
}

// Reads selects any earlier named step (or "base") instead of the previous step.
func (s *PipelineStepBuilder) Reads(stepName string) *PipelineStepBuilder {
	known := false
	for _, e := range s.earlier {
		if strings.EqualFold(e, stepName) {
			known = true
			break
		}
	}
	if !known {
		s.fail(fmt.Errorf("step %q: reads %q does not name an earlier step", s.name, stepName))
		return s
	}
	s.step.Reads = stepName
	return s
}

// Where adds a WHERE (FILTER) clause against the step's input columns.
func (s *PipelineStepBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineStepBuilder {
	return s.addClause(field, op, val, pb.SearchClauseType_FILTER)
}

// Not adds a MUST_NOT clause.
func (s *PipelineStepBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineStepBuilder {
	return s.addClause(field, op, val, pb.SearchClauseType_MUST_NOT)
}

// WithLogic sets the step's WHERE clause logic. Default: AND.
func (s *PipelineStepBuilder) WithLogic(logic pb.SearchLogic) *PipelineStepBuilder {
	s.step.WhereLogic = logic
	return s
}

// RowNumber adds ROW_NUMBER() OVER (...). partitionBy may be "" for none.
func (s *PipelineStepBuilder) RowNumber(alias, partitionBy, orderBy string, descending bool) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_ROW_NUMBER, "", partitionBy, orderBy, descending, 1)
}

// Rank adds RANK() OVER (...).
func (s *PipelineStepBuilder) Rank(alias, partitionBy, orderBy string, descending bool) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_RANK, "", partitionBy, orderBy, descending, 1)
}

// DenseRank adds DENSE_RANK() OVER (...).
func (s *PipelineStepBuilder) DenseRank(alias, partitionBy, orderBy string, descending bool) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_DENSE_RANK, "", partitionBy, orderBy, descending, 1)
}

// RunningSum adds SUM(field) OVER (PARTITION BY ... ORDER BY ...). partitionBy may be "" for none.
func (s *PipelineStepBuilder) RunningSum(alias, partitionBy, field, orderBy string) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_RUNNING_SUM, field, partitionBy, orderBy, false, 1)
}

// RunningAvg adds AVG(field) OVER (PARTITION BY ... ORDER BY ...). partitionBy may be "" for none.
func (s *PipelineStepBuilder) RunningAvg(alias, partitionBy, field, orderBy string) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_RUNNING_AVG, field, partitionBy, orderBy, false, 1)
}

// Lag adds LAG(field, offset) OVER (PARTITION BY ... ORDER BY ...). partitionBy may be "" for none.
func (s *PipelineStepBuilder) Lag(alias, partitionBy, field, orderBy string, offset int32) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_LAG, field, partitionBy, orderBy, false, offset)
}

// Lead adds LEAD(field, offset) OVER (PARTITION BY ... ORDER BY ...). partitionBy may be "" for none.
func (s *PipelineStepBuilder) Lead(alias, partitionBy, field, orderBy string, offset int32) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_LEAD, field, partitionBy, orderBy, false, offset)
}

// GroupBy adds a GROUP BY key without date truncation.
func (s *PipelineStepBuilder) GroupBy(field string) *PipelineStepBuilder {
	return s.GroupByTrunc(field, pb.DateTrunc_NONE)
}

// GroupByTrunc adds a GROUP BY key with a DATE_TRUNC interval.
func (s *PipelineStepBuilder) GroupByTrunc(field string, trunc pb.DateTrunc) *PipelineStepBuilder {
	s.step.GroupBy = append(s.step.GroupBy, &pb.GroupKey{Field: field, DateTrunc: trunc})
	return s
}

// Sum adds a SUM metric. Default alias: "{field}_sum".
func (s *PipelineStepBuilder) Sum(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_SUM, field, "", resolveAlias(alias, field, "sum"))
}

// Avg adds an AVG metric. Default alias: "{field}_avg".
func (s *PipelineStepBuilder) Avg(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_AVG, field, "", resolveAlias(alias, field, "avg"))
}

// Min adds a MIN metric. Default alias: "{field}_min".
func (s *PipelineStepBuilder) Min(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_MIN, field, "", resolveAlias(alias, field, "min"))
}

// Max adds a MAX metric. Default alias: "{field}_max".
func (s *PipelineStepBuilder) Max(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_MAX, field, "", resolveAlias(alias, field, "max"))
}

// Count adds a COUNT metric on a field. Default alias: "{field}_count".
func (s *PipelineStepBuilder) Count(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_COUNT, field, "", resolveAlias(alias, field, "count"))
}

// CountAll adds COUNT(*). Default alias: "count".
func (s *PipelineStepBuilder) CountAll(alias ...string) *PipelineStepBuilder {
	name := "count"
	if len(alias) > 0 {
		name = alias[0]
	}
	return s.addMetric(pb.AggregationType_COUNT, "", "", name)
}

// SumExpr adds SUM over a raw SQL expression.
func (s *PipelineStepBuilder) SumExpr(expression, alias string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_SUM, "", expression, alias)
}

// AvgExpr adds AVG over a raw SQL expression.
func (s *PipelineStepBuilder) AvgExpr(expression, alias string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_AVG, "", expression, alias)
}

// Having adds a HAVING clause; alias must match a metric alias in this step.
func (s *PipelineStepBuilder) Having(alias string, op pb.SearchOperator, val *pb.SearchValue) *PipelineStepBuilder {
	if val == nil {
		s.fail(fmt.Errorf("step %q: having %q: nil search value", s.name, alias))
		return s
	}
	s.step.Having = append(s.step.Having, &pb.SearchClause{
		Property: alias, Operator: op, Value: val, ClauseType: pb.SearchClauseType_FILTER,
	})
	return s
}

// Derive adds a validated scalar expression column.
func (s *PipelineStepBuilder) Derive(alias, expr string) *PipelineStepBuilder {
	s.step.Derive = append(s.step.Derive, &pb.DeriveColumn{Alias: alias, Expr: expr})
	return s
}

// Join joins the step's input against an earlier step's CTE or a registered entity type.
// The join kind defaults to INNER; pass an explicit pb.JoinKind to override.
func (s *PipelineStepBuilder) Join(source, onLeft, onRight string, opts ...pb.JoinKind) *PipelineStepBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	s.step.Joins = append(s.step.Joins, &pb.PipelineJoin{
		Source: source, Kind: kind,
		On: []*pb.JoinCondition{{Left: onLeft, Right: onRight}},
	})
	return s
}

// JoinCondition is a client-side left/right column pair for a composite-key join.
type JoinCondition struct {
	Left  string
	Right string
}

// JoinOn joins the step's input against an earlier step's CTE or a registered entity type
// using one or more equality conditions (AND-ed) — the composite-key form of Join.
func (s *PipelineStepBuilder) JoinOn(source string, on []JoinCondition, opts ...pb.JoinKind) *PipelineStepBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	conditions := make([]*pb.JoinCondition, 0, len(on))
	for _, c := range on {
		conditions = append(conditions, &pb.JoinCondition{Left: c.Left, Right: c.Right})
	}
	s.step.Joins = append(s.step.Joins, &pb.PipelineJoin{Source: source, Kind: kind, On: conditions})
	return s
}

// SelectAllFrom projects all columns from a source ("base", a step name, or a joined type).
func (s *PipelineStepBuilder) SelectAllFrom(source string) *PipelineStepBuilder {
	s.step.Select = append(s.step.Select, &pb.SelectItem{Source: source, All: true})
	return s
}

// SelectPick projects one column from a source, optionally renamed.
func (s *PipelineStepBuilder) SelectPick(source, column string, alias ...string) *PipelineStepBuilder {
	a := ""
	if len(alias) > 0 {
		a = alias[0]
	}
	s.step.Select = append(s.step.Select, &pb.SelectItem{Source: source, Column: column, Alias: a})
	return s
}

func (s *PipelineStepBuilder) buildStep() (*pb.PipelineStep, error) {
	if s.err != nil {
		return nil, s.err
	}
	isAggregate := len(s.step.GroupBy) > 0 || len(s.step.Metrics) > 0 || len(s.step.Having) > 0

	if len(s.step.Windows) > 0 && isAggregate {
		return nil, fmt.Errorf(
			"step %q: window functions and GroupBy/metrics/Having cannot share a step", s.name)
	}
	if (len(s.step.Metrics) > 0 || len(s.step.Having) > 0) && len(s.step.GroupBy) == 0 {
		return nil, fmt.Errorf("step %q: metrics/Having require at least one GroupBy key", s.name)
	}
	if len(s.step.Joins) > 0 && len(s.step.Select) == 0 {
		return nil, fmt.Errorf("step %q: a step with joins requires a select projection", s.name)
	}

	seen := map[string]bool{}
	var aliases []string
	for _, w := range s.step.Windows {
		aliases = append(aliases, w.Alias)
	}
	for _, d := range s.step.Derive {
		aliases = append(aliases, d.Alias)
	}
	for _, m := range s.step.Metrics {
		aliases = append(aliases, m.Name)
	}
	for _, sel := range s.step.Select {
		if sel.Alias != "" {
			aliases = append(aliases, sel.Alias)
		}
	}
	for _, a := range aliases {
		key := strings.ToLower(a)
		if seen[key] {
			return nil, fmt.Errorf("step %q: duplicate output alias %q", s.name, a)
		}
		seen[key] = true
	}

	return s.step, nil
}

func (s *PipelineStepBuilder) addClause(
	field string, op pb.SearchOperator, val *pb.SearchValue, ct pb.SearchClauseType) *PipelineStepBuilder {
	if val == nil {
		s.fail(fmt.Errorf("step %q: field %q: nil search value", s.name, field))
		return s
	}
	s.step.Where = append(s.step.Where, &pb.SearchClause{
		Property: field, Operator: op, Value: val, ClauseType: ct,
	})
	return s
}

func (s *PipelineStepBuilder) addWindow(
	alias string, kind pb.WindowFunctionKind, field, partitionBy, orderBy string,
	descending bool, offset int32) *PipelineStepBuilder {
	s.step.Windows = append(s.step.Windows, &pb.WindowFunction{
		Alias: alias, Kind: kind, Field: field,
		PartitionBy: partitionBy, OrderBy: orderBy,
		Descending: descending, Offset: offset,
	})
	return s
}

func (s *PipelineStepBuilder) addMetric(
	aggType pb.AggregationType, field, expression, alias string) *PipelineStepBuilder {
	s.step.Metrics = append(s.step.Metrics, &pb.MetricSpec{
		Name: alias, Type: aggType, Field: field, Expression: expression,
	})
	return s
}

func (s *PipelineStepBuilder) fail(err error) {
	if s.err == nil {
		s.err = err
	}
}
