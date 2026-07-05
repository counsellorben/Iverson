/**
 * Fluent pipeline (CTE chain) builder that compiles to a PipelineRequest proto.
 *
 * Each `.step(name, fn)` is exactly one CTE in the generated StarRocks query; steps
 * read the previous step by default, or any earlier named step via `reads`.
 * String-addressed like GroupByBuilder; `build()` never needs a live server.
 */

import {
    AggregationType,
    DateTrunc,
    DeriveColumn,
    GroupKey,
    JoinKind,
    MetricSpec,
    PipelineJoin,
    PipelineRequest,
    PipelineStep,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchSort,
    SelectItem,
    WindowFunction,
    WindowFunctionKind,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

const BASE_STEP_NAME = 'base';

interface WindowOptions {
    orderBy: string;
    descending?: boolean;
    partitionBy?: string;
    offset?: number;
}

/** Builds a joined step's projection: which columns survive the join. */
export class SelectSpecBuilder {
    readonly items: SelectItem[] = [];

    /** All columns from a source ('base', a step name, or a joined type name). */
    allFrom(source: string): this {
        this.items.push({ source, column: '', all: true, alias: '' });
        return this;
    }

    /** One column from a source, optionally renamed. */
    pick(source: string, column: string, alias = ''): this {
        this.items.push({ source, column, all: false, alias });
        return this;
    }
}

/** One pipeline step (= one CTE). */
export class PipelineStepBuilder {
    private readonly _name: string;
    private readonly _earlier: string[];
    private _reads = '';
    private readonly _where: SearchClause[] = [];
    private _whereLogic: SearchLogic = SearchLogic.AND;
    private readonly _windows: WindowFunction[] = [];
    private readonly _groupBy: GroupKey[] = [];
    private readonly _metrics: MetricSpec[] = [];
    private readonly _having: SearchClause[] = [];
    private readonly _derive: DeriveColumn[] = [];
    private readonly _joins: PipelineJoin[] = [];
    private readonly _select: SelectItem[] = [];

    constructor(name: string, earlier: string[]) {
        this._name = name;
        this._earlier = earlier;
    }

    /** Read any earlier named step (or 'base') instead of the previous step. */
    reads(stepName: string): this {
        if (!this._earlier.some(s => s.toLowerCase() === stepName.toLowerCase())) {
            throw new Error(
                `Step '${this._name}': reads '${stepName}' does not name an earlier step.`);
        }
        this._reads = stepName;
        return this;
    }

    where(field: string, op: SearchOperator, value: unknown): this {
        return this._addClause(field, op, value, SearchClauseType.FILTER);
    }

    not(field: string, op: SearchOperator, value: unknown): this {
        return this._addClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    withLogic(logic: SearchLogic): this {
        this._whereLogic = logic;
        return this;
    }

    rowNumber(alias: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.ROW_NUMBER, '', opts);
    }

    rank(alias: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.RANK, '', opts);
    }

    denseRank(alias: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.DENSE_RANK, '', opts);
    }

    runningSum(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.RUNNING_SUM, field, opts);
    }

    runningAvg(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.RUNNING_AVG, field, opts);
    }

    lag(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.LAG, field, opts);
    }

    lead(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.LEAD, field, opts);
    }

    groupBy(field: string, dateTrunc: DateTrunc = DateTrunc.NONE): this {
        this._groupBy.push({ field, dateTrunc });
        return this;
    }

    sum(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_sum`, AggregationType.SUM, field, '');
    }

    avg(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_avg`, AggregationType.AVG, field, '');
    }

    min(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_min`, AggregationType.MIN, field, '');
    }

    max(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_max`, AggregationType.MAX, field, '');
    }

    count(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_count`, AggregationType.COUNT, field, '');
    }

    countAll(alias = 'count'): this {
        return this._addMetric(alias, AggregationType.COUNT, '', '');
    }

    sumExpr(expression: string, alias: string): this {
        return this._addMetric(alias, AggregationType.SUM, '', expression);
    }

    avgExpr(expression: string, alias: string): this {
        return this._addMetric(alias, AggregationType.AVG, '', expression);
    }

    having(alias: string, op: SearchOperator, value: unknown): this {
        this._having.push({
            property: alias, operator: op,
            value: toSearchValue(value), clauseType: SearchClauseType.FILTER,
        });
        return this;
    }

    derive(alias: string, expr: string): this {
        this._derive.push({ alias, expr });
        return this;
    }

    join(source: string, onLeft: string, onRight: string, kind: JoinKind = JoinKind.INNER): this {
        this._joins.push({ source, kind, on: [{ left: onLeft, right: onRight }] });
        return this;
    }

    select(configure: (sel: SelectSpecBuilder) => unknown): this {
        const builder = new SelectSpecBuilder();
        configure(builder);
        this._select.push(...builder.items);
        return this;
    }

    /** @internal */
    buildStep(): PipelineStep {
        const isAggregate =
            this._groupBy.length > 0 || this._metrics.length > 0 || this._having.length > 0;

        if (this._windows.length > 0 && isAggregate) {
            throw new Error(
                `Step '${this._name}': window functions and groupBy/metrics/having cannot share a step.`);
        }
        if ((this._metrics.length > 0 || this._having.length > 0) && this._groupBy.length === 0) {
            throw new Error(
                `Step '${this._name}': metrics/having require at least one groupBy key.`);
        }
        if (this._joins.length > 0 && this._select.length === 0) {
            throw new Error(
                `Step '${this._name}': a step with joins requires a select projection.`);
        }

        const seen = new Set<string>();
        const aliases = [
            ...this._windows.map(w => w.alias),
            ...this._derive.map(d => d.alias),
            ...this._metrics.map(m => m.name),
            ...this._select.filter(s => s.alias !== '').map(s => s.alias),
        ];
        for (const a of aliases) {
            const key = a.toLowerCase();
            if (seen.has(key)) {
                throw new Error(`Step '${this._name}': duplicate output alias '${a}'.`);
            }
            seen.add(key);
        }

        return {
            name: this._name,
            reads: this._reads,
            where: this._where,
            whereLogic: this._whereLogic,
            windows: this._windows,
            groupBy: this._groupBy,
            metrics: this._metrics,
            having: this._having,
            derive: this._derive,
            joins: this._joins,
            select: this._select,
        };
    }

    private _addClause(
        field: string, op: SearchOperator, value: unknown, clauseType: SearchClauseType): this {
        this._where.push({ property: field, operator: op, value: toSearchValue(value), clauseType });
        return this;
    }

    private _addWindow(
        alias: string, kind: WindowFunctionKind, field: string, opts: WindowOptions): this {
        this._windows.push({
            alias, kind, field,
            orderBy: opts.orderBy,
            descending: opts.descending ?? false,
            partitionBy: opts.partitionBy ?? '',
            offset: opts.offset ?? 1,
        });
        return this;
    }

    private _addMetric(
        alias: string, type: AggregationType, field: string, expression: string): this {
        this._metrics.push({ name: alias, type, field, expression });
        return this;
    }
}

/** Fluent DSL builder that compiles to a PipelineRequest proto. */
export class PipelineBuilder {
    private readonly _typeName: string;
    private readonly _baseWhere: SearchClause[] = [];
    private _baseLogic: SearchLogic = SearchLogic.AND;
    private readonly _steps: PipelineStep[] = [];
    private readonly _orderBy: SearchSort[] = [];
    private _limit = 10_000;

    constructor(typeName: string) {
        this._typeName = typeName;
    }

    where(field: string, op: SearchOperator, value: unknown): this {
        return this._addBaseClause(field, op, value, SearchClauseType.FILTER);
    }

    not(field: string, op: SearchOperator, value: unknown): this {
        return this._addBaseClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    withLogic(logic: SearchLogic): this {
        this._baseLogic = logic;
        return this;
    }

    step(name: string, configure: (s: PipelineStepBuilder) => unknown): this {
        if (name === '') throw new Error('Step name must be non-empty.');
        if (name.toLowerCase() === BASE_STEP_NAME) {
            throw new Error(`Step name '${name}' is reserved for the implicit base step.`);
        }
        if (this._steps.some(s => s.name.toLowerCase() === name.toLowerCase())) {
            throw new Error(`Duplicate step name '${name}'.`);
        }

        const earlier = [BASE_STEP_NAME, ...this._steps.map(s => s.name)];
        const builder = new PipelineStepBuilder(name, earlier);
        configure(builder);
        this._steps.push(builder.buildStep());
        return this;
    }

    sortOn(field: string, descending = false): this {
        this._orderBy.push({ property: field, descending });
        return this;
    }

    sortOnDesc(field: string): this {
        return this.sortOn(field, true);
    }

    limit(n: number): this {
        this._limit = n;
        return this;
    }

    build(traceId = ''): PipelineRequest {
        return {
            typeName: this._typeName,
            baseWhere: [...this._baseWhere],
            baseLogic: this._baseLogic,
            steps: [...this._steps],
            orderBy: [...this._orderBy],
            limit: this._limit,
            traceId,
        };
    }

    private _addBaseClause(
        field: string, op: SearchOperator, value: unknown, clauseType: SearchClauseType): this {
        this._baseWhere.push({ property: field, operator: op, value: toSearchValue(value), clauseType });
        return this;
    }
}

/** Start a fluent pipeline (CTE chain) for the given entity type. */
export function pipeline(typeName: string): PipelineBuilder {
    return new PipelineBuilder(typeName);
}
