/**
 * Fluent aggregate builder that compiles to an AggregateRequest proto.
 *
 * Unlike GroupByBuilder (one compound SELECT with all metrics as columns),
 * AggregateRequest runs one bucketed/metric aggregation per AggregationSpec
 * (TERMS/DATE_HISTOGRAM/RANGE buckets, or a single AVG/SUM/MIN/MAX/COUNT value),
 * with optional WHERE, HAVING, and JOIN — same shape as GroupByBuilder otherwise.
 *
 * Note: AggregationSpec's groupByFields/expression override fields (multi-key
 * TERMS and raw-SQL-expression aggregations) are not covered by this builder.
 *
 * Usage:
 *   const req = aggregate('Article')
 *     .terms('category', 'byCategory', 5)
 *     .avg('wordCount', 'avgWords')
 *     .where('isPublished', SearchOperator.EQUALS, true)
 *     .build();
 */

import {
    AggregateRequest,
    AggregationSpec,
    AggregationType,
    JoinKind,
    JoinSpec,
    RangeBucket,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchQuery,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

export interface RangeBucketInput {
    key?: string;
    from?: number;
    to?: number;
}

// ── AggregateBuilder ──────────────────────────────────────────────────────────

/**
 * Fluent DSL builder that compiles to an AggregateRequest proto.
 */
export class AggregateBuilder {
    private readonly _typeName: string;
    private readonly _aggregations: AggregationSpec[] = [];
    private readonly _where: SearchClause[] = [];
    private readonly _having: SearchClause[] = [];
    private readonly _joins: JoinSpec[] = [];
    private _whereLogic: SearchLogic = SearchLogic.AND;
    private _havingLogic: SearchLogic = SearchLogic.AND;

    constructor(typeName: string) {
        this._typeName = typeName;
    }

    // ── Filtering ─────────────────────────────────────────────────────────────

    /** Add a WHERE clause (applied before aggregating). */
    where(field: string, op: SearchOperator, value: unknown): this {
        this._where.push({
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.FILTER,
        });
        return this;
    }

    /** Add a MUST_NOT WHERE clause (excludes matches before aggregating). */
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

    /**
     * Add a HAVING clause (applied after aggregating). `alias` must be one of the
     * server's fixed output column aliases — `metric_val` for Avg/Sum/Min/Max/Count,
     * or `doc_count`/`bucket_key` for Terms/DateHistogram/Range — never the `name`
     * passed to a metric/bucket builder method. HAVING applies to every aggregation
     * in the request, not just this one.
     */
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

    // ── Aggregations — bucketing ────────────────────────────────────────────────

    /** Bucket by distinct values of `field` (up to `size` buckets). Default size: 10. */
    terms(field: string, name: string, size = 10): this {
        this._aggregations.push({
            name,
            type: AggregationType.TERMS,
            field,
            size,
            calendarInterval: '',
            timeZone: '',
            rangeBuckets: [],
            groupByFields: [],
            expression: '',
        });
        return this;
    }

    /** Bucket a datetime `field` into calendar intervals (e.g. "day", "month"). */
    dateHistogram(field: string, name: string, calendarInterval: string, timeZone = ''): this {
        this._aggregations.push({
            name,
            type: AggregationType.DATE_HISTOGRAM,
            field,
            size: 0,
            calendarInterval,
            timeZone,
            rangeBuckets: [],
            groupByFields: [],
            expression: '',
        });
        return this;
    }

    /** Bucket `field` into explicit ranges. A bucket's `from`/`to` left unset means unbounded on that side. */
    range(field: string, name: string, buckets: RangeBucketInput[]): this {
        const rangeBuckets: RangeBucket[] = buckets.map(b => ({
            key: b.key ?? '',
            from: b.from,
            to: b.to,
        }));
        this._aggregations.push({
            name,
            type: AggregationType.RANGE,
            field,
            size: 0,
            calendarInterval: '',
            timeZone: '',
            rangeBuckets,
            groupByFields: [],
            expression: '',
        });
        return this;
    }

    // ── Aggregations — metrics ─────────────────────────────────────────────────

    private _addMetric(name: string, type: AggregationType, field: string): this {
        this._aggregations.push({
            name,
            type,
            field,
            size: 0,
            calendarInterval: '',
            timeZone: '',
            rangeBuckets: [],
            groupByFields: [],
            expression: '',
        });
        return this;
    }

    /** AVG metric. */
    avg(field: string, name: string): this {
        return this._addMetric(name, AggregationType.AVG, field);
    }

    /** SUM metric. */
    sum(field: string, name: string): this {
        return this._addMetric(name, AggregationType.SUM, field);
    }

    /** MIN metric. */
    min(field: string, name: string): this {
        return this._addMetric(name, AggregationType.MIN, field);
    }

    /** MAX metric. */
    max(field: string, name: string): this {
        return this._addMetric(name, AggregationType.MAX, field);
    }

    /** COUNT metric on a specific field. */
    count(field: string, name: string): this {
        return this._addMetric(name, AggregationType.COUNT, field);
    }

    /** COUNT(*) metric — no field. Default alias: `count`. */
    countAll(name = 'count'): this {
        return this._addMetric(name, AggregationType.COUNT, '');
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /** Compile to an AggregateRequest proto message. */
    build(traceId = ''): AggregateRequest {
        const names = new Set<string>();
        for (const a of this._aggregations) {
            const key = a.name.toLowerCase();
            if (names.has(key)) throw new Error(`Duplicate aggregation name '${a.name}'.`);
            names.add(key);
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
            aggregations: [...this._aggregations],
            having,
            joins: [...this._joins],
            traceId,
        };
    }
}

/** Start a fluent aggregate query for the given entity type. */
export function aggregate(typeName: string): AggregateBuilder {
    return new AggregateBuilder(typeName);
}
