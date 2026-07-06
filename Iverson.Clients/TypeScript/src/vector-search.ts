/**
 * Fluent builders for Qdrant vector search (SearchSimilar/SearchChunks).
 */
import {
    SearchChunksRequest,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchSimilarRequest,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

const UNSUPPORTED_SIMILAR_OPERATORS = new Set([
    SearchOperator.CONTAINS,
    SearchOperator.STARTS_WITH,
    SearchOperator.ENDS_WITH,
    SearchOperator.VECTOR_SIMILAR,
]);

/**
 * Fluent builder for a SearchSimilarRequest (Qdrant vector similarity search).
 * Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
 * LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR are rejected.
 */
export class SimilarBuilder {
    private _query = '';
    private _topK = 10;
    private _logic: SearchLogic = SearchLogic.AND;
    private _filter: SearchClause[] = [];

    constructor(private readonly typeName: string, private readonly property: string) {}

    text(query: string): this { this._query = query; return this; }
    topK(topK: number): this { this._topK = topK; return this; }
    withLogic(logic: SearchLogic): this { this._logic = logic; return this; }

    where(field: string, op: SearchOperator, value: unknown): this {
        if (UNSUPPORTED_SIMILAR_OPERATORS.has(op)) {
            throw new Error(
                `Operator ${op} is not supported by SearchSimilar filters. Supported operators: ` +
                'EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS, ' +
                'LESS_THAN_OR_EQUALS, IN.');
        }
        this._filter.push({
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.FILTER,
        });
        return this;
    }

    build(traceId = ''): SearchSimilarRequest {
        return {
            typeName: this.typeName,
            property: this.property,
            query: this._query,
            topK: this._topK,
            filter: this._filter,
            filterLogic: this._logic,
            traceId,
        };
    }
}

/**
 * Fluent builder for a SearchChunksRequest. Supports at most one filter clause: an EQUALS
 * match on the entity's primary-key property.
 */
export class ChunksBuilder {
    private _query = '';
    private _topK = 10;
    private _filter?: SearchClause;

    constructor(private readonly typeName: string, private readonly property: string) {}

    text(query: string): this { this._query = query; return this; }
    topK(topK: number): this { this._topK = topK; return this; }

    where(field: string, op: SearchOperator, value: unknown): this {
        if (op !== SearchOperator.EQUALS) {
            throw new Error(`SearchChunks only supports an EQUALS filter on the primary-key property; got ${op}.`);
        }
        if (this._filter !== undefined) {
            throw new Error('SearchChunks supports at most one filter clause.');
        }
        this._filter = {
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.FILTER,
        };
        return this;
    }

    build(traceId = ''): SearchChunksRequest {
        return {
            typeName: this.typeName,
            property: this.property,
            query: this._query,
            topK: this._topK,
            filter: this._filter !== undefined ? [this._filter] : [],
            filterLogic: SearchLogic.AND,
            traceId,
        };
    }
}

export function similar(typeName: string, property: string): SimilarBuilder {
    return new SimilarBuilder(typeName, property);
}

export function chunks(typeName: string, property: string): ChunksBuilder {
    return new ChunksBuilder(typeName, property);
}
