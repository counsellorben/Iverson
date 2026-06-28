/**
 * Tests for QueryBuilder — verifies build() returns correct SearchRequest for each operator.
 */
import { describe, it, expect } from 'vitest';
import { QueryBuilder } from '../src/search.js';
import { SearchOperator, SearchLogic, SearchClauseType } from '../generated/object_search.js';

describe('QueryBuilder', () => {
    describe('build() defaults', () => {
        it('returns a SearchRequest with default values', () => {
            const req = new QueryBuilder('Article').build();
            expect(req.typeName).toBe('Article');
            expect(req.page).toBe(1);
            expect(req.pageSize).toBe(20);
            expect(req.query).toBeDefined();
            expect(req.query!.clauses).toHaveLength(0);
            expect(req.query!.logic).toBe(SearchLogic.AND);
            expect(req.query!.sort).toHaveLength(0);
        });
    });

    describe('where() operators', () => {
        it('eq builds EQUALS clause with string value', () => {
            const req = new QueryBuilder('Article').where('category').eq('tech').build();
            const clause = req.query!.clauses[0];
            expect(clause.property).toBe('category');
            expect(clause.operator).toBe(SearchOperator.EQUALS);
            expect(clause.value!.stringVal).toBe('tech');
            expect(clause.clauseType).toBe(SearchClauseType.FILTER);
        });

        it('eq builds EQUALS clause with number value', () => {
            const req = new QueryBuilder('Article').where('wordCount').eq(500).build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.EQUALS);
            expect(clause.value!.numberVal).toBe(500);
        });

        it('neq builds NOT_EQUALS clause', () => {
            const req = new QueryBuilder('Article').where('status').neq('draft').build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.NOT_EQUALS);
            expect(clause.value!.stringVal).toBe('draft');
        });

        it('gt builds GREATER_THAN clause', () => {
            const req = new QueryBuilder('Article').where('wordCount').gt(100).build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.GREATER_THAN);
            expect(clause.value!.numberVal).toBe(100);
        });

        it('gte builds GREATER_THAN_OR_EQUALS clause', () => {
            const req = new QueryBuilder('Article').where('wordCount').gte(100).build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.GREATER_THAN_OR_EQUALS);
            expect(clause.value!.numberVal).toBe(100);
        });

        it('lt builds LESS_THAN clause', () => {
            const req = new QueryBuilder('Article').where('wordCount').lt(5000).build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.LESS_THAN);
            expect(clause.value!.numberVal).toBe(5000);
        });

        it('lte builds LESS_THAN_OR_EQUALS clause', () => {
            const req = new QueryBuilder('Article').where('wordCount').lte(5000).build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.LESS_THAN_OR_EQUALS);
            expect(clause.value!.numberVal).toBe(5000);
        });

        it('contains builds CONTAINS clause', () => {
            const req = new QueryBuilder('Article').where('title').contains('TypeScript').build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.CONTAINS);
            expect(clause.value!.stringVal).toBe('TypeScript');
        });

        it('startsWith builds STARTS_WITH clause', () => {
            const req = new QueryBuilder('Article').where('title').startsWith('How').build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.STARTS_WITH);
            expect(clause.value!.stringVal).toBe('How');
        });

        it('in builds IN clause with string list', () => {
            const req = new QueryBuilder('Article').where('category').in(['tech', 'science']).build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.IN);
            expect(clause.value!.stringList).toBeDefined();
            expect(clause.value!.stringList!.values).toEqual(['tech', 'science']);
        });

        it('vectorSimilar builds VECTOR_SIMILAR clause with float list', () => {
            const vec = [0.1, 0.2, 0.3];
            const req = new QueryBuilder('Article').where('embedding').vectorSimilar(vec).build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.VECTOR_SIMILAR);
            expect(clause.value!.floatList).toBeDefined();
            expect(clause.value!.floatList!.values).toEqual(vec);
        });
    });

    describe('clause types', () => {
        it('must() sets MUST clause type', () => {
            const req = new QueryBuilder('Article').must('category').eq('tech').build();
            expect(req.query!.clauses[0].clauseType).toBe(SearchClauseType.MUST);
        });

        it('should() sets SHOULD clause type', () => {
            const req = new QueryBuilder('Article').should('category').eq('tech').build();
            expect(req.query!.clauses[0].clauseType).toBe(SearchClauseType.SHOULD);
        });

        it('mustNot() sets MUST_NOT clause type', () => {
            const req = new QueryBuilder('Article').mustNot('category').eq('spam').build();
            expect(req.query!.clauses[0].clauseType).toBe(SearchClauseType.MUST_NOT);
        });
    });

    describe('sorting', () => {
        it('orderByDesc adds descending sort', () => {
            const req = new QueryBuilder('Article').orderByDesc('publishedAt').build();
            expect(req.query!.sort).toHaveLength(1);
            expect(req.query!.sort[0].property).toBe('publishedAt');
            expect(req.query!.sort[0].descending).toBe(true);
        });

        it('orderBy adds ascending sort by default', () => {
            const req = new QueryBuilder('Article').orderBy('publishedAt').build();
            expect(req.query!.sort[0].descending).toBe(false);
        });

        it('supports multiple sorts', () => {
            const req = new QueryBuilder('Article')
                .orderByDesc('publishedAt')
                .orderBy('title')
                .build();
            expect(req.query!.sort).toHaveLength(2);
        });
    });

    describe('paging', () => {
        it('limit sets pageSize', () => {
            const req = new QueryBuilder('Article').limit(50).build();
            expect(req.pageSize).toBe(50);
        });

        it('offset(0) sets page to 1', () => {
            const req = new QueryBuilder('Article').offset(0).build();
            expect(req.page).toBe(1);
        });

        it('offset(1) sets page to 2', () => {
            const req = new QueryBuilder('Article').offset(1).build();
            expect(req.page).toBe(2);
        });
    });

    describe('chaining', () => {
        it('chains multiple clauses', () => {
            const req = new QueryBuilder('Article')
                .where('category').eq('tech')
                .where('wordCount').gte(500)
                .orderByDesc('publishedAt')
                .limit(20)
                .offset(0)
                .build();

            expect(req.query!.clauses).toHaveLength(2);
            expect(req.query!.sort).toHaveLength(1);
            expect(req.pageSize).toBe(20);
            expect(req.page).toBe(1);
        });
    });

    describe('withLogic', () => {
        it('sets OR logic', () => {
            const req = new QueryBuilder('Article').withLogic(SearchLogic.OR).build();
            expect(req.query!.logic).toBe(SearchLogic.OR);
        });
    });
});
