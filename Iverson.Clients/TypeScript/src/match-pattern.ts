/**
 * Fluent row-pattern-matching builder that compiles to a MatchPatternRequest proto.
 *
 * Pattern and expression strings pass through unchanged; the server validates them.
 * `build()` never needs a live server.
 */

import {
    AfterMatchSkip,
    AfterMatchSkipKind,
    MatchPatternRequest,
    NamedExpr,
    PatternRowSource,
    PatternSubset,
    RowsPerMatch,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchSort,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

/** Fluent DSL builder that compiles to a MatchPatternRequest proto. */
export class MatchPatternBuilder {
    private readonly _typeName: string;
    private _source: PatternRowSource = PatternRowSource.TYPE_ROWS;
    private _chunkProperty = '';
    private readonly _where: SearchClause[] = [];
    private _whereLogic: SearchLogic = SearchLogic.AND;
    private readonly _partitionBy: string[] = [];
    private readonly _orderBy: SearchSort[] = [];
    private _pattern = '';
    private readonly _subsets: PatternSubset[] = [];
    private readonly _define: NamedExpr[] = [];
    private readonly _measures: NamedExpr[] = [];
    private _rowsPerMatch: RowsPerMatch = RowsPerMatch.ONE_ROW;
    private _afterMatch: AfterMatchSkip | undefined = undefined;
    private _limit = 0;

    constructor(typeName: string) {
        this._typeName = typeName;
    }

    /** Match over the chunks of an `@IversonChunk` property instead of the type's rows. */
    chunks(chunkProperty: string): this {
        this._source = PatternRowSource.CHUNKS;
        this._chunkProperty = chunkProperty;
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

    partitionBy(...fields: string[]): this {
        this._partitionBy.push(...fields);
        return this;
    }

    orderBy(field: string, descending = false): this {
        this._orderBy.push({ property: field, descending });
        return this;
    }

    pattern(pattern: string): this {
        this._pattern = pattern;
        return this;
    }

    subset(name: string, ...variables: string[]): this {
        this._subsets.push({ name, variables });
        return this;
    }

    define(variable: string, expr: string): this {
        this._define.push({ name: variable, expr });
        return this;
    }

    measure(name: string, expr: string): this {
        this._measures.push({ name, expr });
        return this;
    }

    rowsPerMatch(mode: RowsPerMatch): this {
        this._rowsPerMatch = mode;
        return this;
    }

    afterMatch(kind: AfterMatchSkipKind, variable = ''): this {
        this._afterMatch = { kind, variable };
        return this;
    }

    limit(n: number): this {
        this._limit = n;
        return this;
    }

    build(traceId = ''): MatchPatternRequest {
        return {
            typeName: this._typeName,
            source: this._source,
            chunkProperty: this._chunkProperty,
            where: [...this._where],
            whereLogic: this._whereLogic,
            partitionBy: [...this._partitionBy],
            orderBy: [...this._orderBy],
            pattern: this._pattern,
            subsets: [...this._subsets],
            define: [...this._define],
            measures: [...this._measures],
            rowsPerMatch: this._rowsPerMatch,
            afterMatch: this._afterMatch,
            limit: this._limit,
            traceId,
        };
    }

    private _addClause(
        field: string, op: SearchOperator, value: unknown, clauseType: SearchClauseType): this {
        this._where.push({ property: field, operator: op, value: toSearchValue(value), clauseType });
        return this;
    }
}

/** Start a fluent row-pattern match for the given entity type. */
export function matchPattern(typeName: string): MatchPatternBuilder {
    return new MatchPatternBuilder(typeName);
}
