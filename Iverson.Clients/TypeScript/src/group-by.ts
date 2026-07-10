/**
 * Fluent GROUP BY query builder that compiles to a GroupByRequest proto.
 *
 * Usage:
 *   const req = groupBy('LineItem')
 *     .key('orderStatus')
 *     .sum('quantity')
 *     .having('quantity_sum', SearchOperator.GREATER_THAN, 100)
 *     .build();
 */

import {
    AggregationType,
    GroupByRequest,
    JoinKind,
    JoinSpec,
    MetricSpec,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchQuery,
    SearchSort,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

// ── GroupByBuilder ────────────────────────────────────────────────────────────

/**
 * Fluent DSL builder that compiles to a GroupByRequest proto.
 */
export class GroupByBuilder {
    private readonly _typeName: string;
    private readonly _keys: string[] = [];
    private readonly _metrics: MetricSpec[] = [];
    private readonly _orderBy: SearchSort[] = [];
    private readonly _where: SearchClause[] = [];
    private readonly _having: SearchClause[] = [];
    private readonly _joins: JoinSpec[] = [];
    private _whereLogic: SearchLogic = SearchLogic.AND;
    private _havingLogic: SearchLogic = SearchLogic.AND;
    private _limit = 10_000;

    constructor(typeName: string) {
        this._typeName = typeName;
    }

    // ── Group-by keys ─────────────────────────────────────────────────────────

    /** Add a single GROUP BY column. */
    key(field: string): this {
        this._keys.push(field);
        return this;
    }

    /** Add multiple GROUP BY columns. */
    keys(...fields: string[]): this {
        this._keys.push(...fields);
        return this;
    }

    // ── Filtering ─────────────────────────────────────────────────────────────

    /** Add a WHERE clause (applied before grouping). */
    where(field: string, op: SearchOperator, value: unknown): this {
        this._where.push({
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.FILTER,
        });
        return this;
    }

    /** Add a MUST_NOT WHERE clause (excludes matches before grouping). */
    not(field: string, op: SearchOperator, value: unknown): this {
        this._where.push({
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.MUST_NOT,
        });
        return this;
    }

    /** Set the top-level WHERE clause logic (AND / OR). Default: AND. */
    withLogic(logic: SearchLogic): this {
        this._whereLogic = logic;
        return this;
    }

    /** Add a HAVING clause (applied after grouping); `alias` references a metric output alias. */
    having(alias: string, op: SearchOperator, value: unknown): this {
        this._having.push({
            property: alias,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.FILTER,
        });
        return this;
    }

    /** Set the logic combining HAVING clauses. Default: AND. */
    withHavingLogic(logic: SearchLogic): this {
        this._havingLogic = logic;
        return this;
    }

    // ── Joins ─────────────────────────────────────────────────────────────────

    /** Add an inner/left/right join to another registered type. */
    join(leftField: string, rightType: string, rightField: string, kind: JoinKind = JoinKind.INNER): this {
        this._joins.push({
            leftType: this._typeName,
            rightType,
            leftField,
            rightField,
            kind,
        });
        return this;
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    private _addMetric(type: AggregationType, field: string, alias: string | undefined): this {
        this._metrics.push({
            name: alias ?? `${field}_${AggregationType[type].toLowerCase()}`,
            type,
            field,
            expression: '',
        });
        return this;
    }

    /** SUM metric. Default alias: `{field}_sum`. */
    sum(field: string, alias?: string): this {
        return this._addMetric(AggregationType.SUM, field, alias);
    }

    /** AVG metric. Default alias: `{field}_avg`. */
    avg(field: string, alias?: string): this {
        return this._addMetric(AggregationType.AVG, field, alias);
    }

    /** MIN metric. Default alias: `{field}_min`. */
    min(field: string, alias?: string): this {
        return this._addMetric(AggregationType.MIN, field, alias);
    }

    /** MAX metric. Default alias: `{field}_max`. */
    max(field: string, alias?: string): this {
        return this._addMetric(AggregationType.MAX, field, alias);
    }

    /** COUNT metric on a specific field. Default alias: `{field}_count`. */
    count(field: string, alias?: string): this {
        return this._addMetric(AggregationType.COUNT, field, alias);
    }

    /** COUNT(*) metric — no field. Default alias: `count`. */
    countAll(alias = 'count'): this {
        this._metrics.push({
            name: alias,
            type: AggregationType.COUNT,
            field: '',
            expression: '',
        });
        return this;
    }

    /** SUM over a raw SQL expression. */
    sumExpr(expression: string, alias: string): this {
        this._metrics.push({
            name: alias,
            type: AggregationType.SUM,
            field: '',
            expression,
        });
        return this;
    }

    /** AVG over a raw SQL expression. */
    avgExpr(expression: string, alias: string): this {
        this._metrics.push({
            name: alias,
            type: AggregationType.AVG,
            field: '',
            expression,
        });
        return this;
    }

    // ── Sorting and limiting ──────────────────────────────────────────────────

    /** Add a sort clause (typically referencing a group-by key or metric alias). */
    orderBy(field: string, descending = false): this {
        this._orderBy.push({ property: field, descending });
        return this;
    }

    /** Add a descending sort clause. */
    orderByDesc(field: string): this {
        return this.orderBy(field, true);
    }

    /** Set the max number of grouped rows returned. Default: 10000. */
    limit(n: number): this {
        this._limit = n;
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /** Compile to a GroupByRequest proto message. */
    build(traceId = ''): GroupByRequest {
        const aliases = new Set<string>();
        for (const m of this._metrics) {
            const key = m.name.toLowerCase();
            if (aliases.has(key)) throw new Error(`Duplicate metric alias '${m.name}'.`);
            aliases.add(key);
        }
        const keys = new Set<string>();
        for (const k of this._keys) {
            const key = k.toLowerCase();
            if (keys.has(key)) throw new Error(`Duplicate key '${k}'.`);
            keys.add(key);
            if (aliases.has(key)) throw new Error(`Key '${k}' collides with an existing metric alias.`);
            aliases.add(key);
        }

        for (const h of this._having) {
            if (!aliases.has(h.property.toLowerCase())) {
                throw new Error(
                    `HAVING references '${h.property}', which is neither a metric alias nor a key.`);
            }
        }
        for (const s of this._orderBy) {
            if (!aliases.has(s.property.toLowerCase())) {
                throw new Error(
                    `orderBy references '${s.property}', which is neither a metric alias nor a key.`);
            }
        }

        const query: SearchQuery = {
            clauses: [...this._where],
            logic: this._whereLogic,
            sort: [],
        };
        const having: SearchQuery = {
            clauses: [...this._having],
            logic: this._havingLogic,
            sort: [],
        };
        return {
            typeName: this._typeName,
            query,
            keys: [...this._keys],
            metrics: [...this._metrics],
            having,
            orderBy: [...this._orderBy],
            limit: this._limit,
            joins: [...this._joins],
            traceId,
        };
    }
}

/** Start a fluent GROUP BY query for the given entity type. */
export function groupBy(typeName: string): GroupByBuilder {
    return new GroupByBuilder(typeName);
}
