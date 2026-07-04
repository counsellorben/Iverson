/**
 * Fluent query builder that compiles to a SearchRequest proto.
 *
 * Usage:
 *   const req = new QueryBuilder('Article')
 *     .where('category').eq('tech')
 *     .orderByDesc('publishedAt')
 *     .limit(20)
 *     .offset(0)
 *     .build();
 */

import {
    JoinKind,
    JoinSpec,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchQuery,
    SearchRequest,
    SearchSort,
    SearchValue,
    RepeatedString,
    RepeatedFloat,
} from '../generated/object_search.js';

// Re-export for consumers
export { SearchOperator, SearchLogic, SearchClauseType, JoinKind };

// ── Value conversion helper ───────────────────────────────────────────────────

export function toSearchValue(value: unknown): SearchValue {
    if (value === null || value === undefined) {
        return {};
    }
    if (typeof value === 'boolean') {
        return { boolVal: value };
    }
    if (typeof value === 'string') {
        return { stringVal: value };
    }
    if (typeof value === 'number') {
        return { numberVal: value };
    }
    if (Array.isArray(value)) {
        if (value.length > 0 && typeof value[0] === 'number') {
            const floatList: RepeatedFloat = { values: value as number[] };
            return { floatList };
        }
        // treat as string list (IN operator)
        const stringList: RepeatedString = { values: (value as unknown[]).map(String) };
        return { stringList };
    }
    return { stringVal: String(value) };
}

// ── FieldCondition ────────────────────────────────────────────────────────────

/**
 * Represents a pending clause for a specific field.
 * Call an operator method to add the clause to the parent QueryBuilder.
 */
export class FieldCondition {
    constructor(
        private readonly _builder: QueryBuilder,
        private readonly _field: string,
        private readonly _clauseType: SearchClauseType = SearchClauseType.FILTER,
    ) {}

    private _add(operator: SearchOperator, value: SearchValue): QueryBuilder {
        const clause: SearchClause = {
            property: this._field,
            operator,
            value,
            clauseType: this._clauseType,
        };
        this._builder._addClause(clause);
        return this._builder;
    }

    /** EQUALS */
    eq(value: unknown): QueryBuilder {
        return this._add(SearchOperator.EQUALS, toSearchValue(value));
    }

    /** NOT_EQUALS */
    neq(value: unknown): QueryBuilder {
        return this._add(SearchOperator.NOT_EQUALS, toSearchValue(value));
    }

    /** GREATER_THAN */
    gt(value: number | string): QueryBuilder {
        return this._add(SearchOperator.GREATER_THAN, toSearchValue(value));
    }

    /** GREATER_THAN_OR_EQUALS */
    gte(value: number | string): QueryBuilder {
        return this._add(SearchOperator.GREATER_THAN_OR_EQUALS, toSearchValue(value));
    }

    /** LESS_THAN */
    lt(value: number | string): QueryBuilder {
        return this._add(SearchOperator.LESS_THAN, toSearchValue(value));
    }

    /** LESS_THAN_OR_EQUALS */
    lte(value: number | string): QueryBuilder {
        return this._add(SearchOperator.LESS_THAN_OR_EQUALS, toSearchValue(value));
    }

    /** CONTAINS */
    contains(value: string): QueryBuilder {
        return this._add(SearchOperator.CONTAINS, toSearchValue(value));
    }

    /** STARTS_WITH */
    startsWith(value: string): QueryBuilder {
        return this._add(SearchOperator.STARTS_WITH, toSearchValue(value));
    }

    /** ENDS_WITH */
    endsWith(value: string): QueryBuilder {
        return this._add(SearchOperator.ENDS_WITH, toSearchValue(value));
    }

    /** IN — accepts a list of strings */
    in(values: string[]): QueryBuilder {
        const sv: SearchValue = { stringList: { values } };
        return this._add(SearchOperator.IN, sv);
    }

    /** VECTOR_SIMILAR — accepts a float list */
    vectorSimilar(queryVector: number[]): QueryBuilder {
        const sv: SearchValue = { floatList: { values: queryVector } };
        return this._add(SearchOperator.VECTOR_SIMILAR, sv);
    }
}

// ── QueryBuilder ──────────────────────────────────────────────────────────────

/**
 * Fluent DSL builder that compiles to a SearchRequest proto.
 *
 * Operators supported (matching SearchOperator enum exactly):
 *   eq, neq, contains, startsWith, gt, lt, gte, lte, in, vectorSimilar
 */
export class QueryBuilder {
    private readonly _clauses: SearchClause[] = [];
    private readonly _sorts: SearchSort[] = [];
    private readonly _fields: string[] = [];
    private readonly _joins: JoinSpec[] = [];
    private _logic: SearchLogic = SearchLogic.AND;
    private _page: number = 0;
    private _pageSize: number = 20;
    private _typeName: string = '';
    private _traceId: string = '';

    constructor(typeName: string = '') {
        this._typeName = typeName;
    }

    /** Internal: called by FieldCondition to register a clause. */
    _addClause(clause: SearchClause): void {
        this._clauses.push(clause);
    }

    // ── Clause entry points ───────────────────────────────────────────────────

    /** Start a FILTER clause for the given field. */
    where(field: string): FieldCondition {
        return new FieldCondition(this, field, SearchClauseType.FILTER);
    }

    /** Start a MUST_NOT clause (excludes matches). */
    mustNot(field: string): FieldCondition {
        return new FieldCondition(this, field, SearchClauseType.MUST_NOT);
    }

    /** Restrict the response to only the named fields. Empty (default) returns all fields. */
    fields(...names: string[]): QueryBuilder {
        this._fields.push(...names);
        return this;
    }

    // ── Sorting and paging ────────────────────────────────────────────────────

    /** Add a sort clause. */
    orderBy(field: string, descending: boolean = false): QueryBuilder {
        this._sorts.push({ property: field, descending });
        return this;
    }

    /** Add a descending sort clause. */
    orderByDesc(field: string): QueryBuilder {
        return this.orderBy(field, true);
    }

    /** Set the page size. */
    limit(n: number): QueryBuilder {
        this._pageSize = n;
        return this;
    }

    /** Set the zero-based page offset (offset(0) = first page). */
    offset(page: number): QueryBuilder {
        this._page = page;
        return this;
    }

    /** Set the entity type name. */
    forType(typeName: string): QueryBuilder {
        this._typeName = typeName;
        return this;
    }

    /** Set the trace ID. */
    withTraceId(traceId: string): QueryBuilder {
        this._traceId = traceId;
        return this;
    }

    /** Set the top-level clause logic (AND / OR). Default: AND. */
    withLogic(logic: SearchLogic): QueryBuilder {
        this._logic = logic;
        return this;
    }

    /** Add an inner/left/right join to another registered type. */
    join(leftField: string, rightType: string, rightField: string, kind: JoinKind = JoinKind.INNER): QueryBuilder {
        this._joins.push({
            leftType: this._typeName,
            rightType,
            leftField,
            rightField,
            kind,
        });
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /** Compile to a SearchRequest proto message. */
    build(): SearchRequest {
        const query: SearchQuery = {
            clauses: [...this._clauses],
            logic: this._logic,
            sort: [...this._sorts],
        };
        return {
            typeName: this._typeName,
            query,
            page: this._page,
            pageSize: this._pageSize,
            traceId: this._traceId,
            fields: [...this._fields],
            joins: [...this._joins],
        };
    }
}
