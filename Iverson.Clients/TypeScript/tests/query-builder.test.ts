/**
 * Tests for QueryBuilder — verifies build() returns correct SearchRequest for each operator.
 */
import { describe, it, expect } from 'vitest';
import { QueryBuilder } from '../src/search.js';
import { GroupByBuilder, groupBy } from '../src/group-by.js';
import { SearchOperator, SearchLogic, SearchClauseType, JoinKind, AggregationType } from '../generated/object_search.js';

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

        it('endsWith builds ENDS_WITH clause', () => {
            const req = new QueryBuilder('Article').where('title').endsWith('Guide').build();
            const clause = req.query!.clauses[0];
            expect(clause.operator).toBe(SearchOperator.ENDS_WITH);
            expect(clause.value!.stringVal).toBe('Guide');
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

    describe('join', () => {
        it('join adds JoinSpec to SearchRequest', () => {
            const req = new QueryBuilder('Order')
                .join('customerId', 'Customer', 'id')
                .build();
            expect(req.joins).toHaveLength(1);
            const join = req.joins[0];
            expect(join.leftType).toBe('Order');
            expect(join.rightType).toBe('Customer');
            expect(join.leftField).toBe('customerId');
            expect(join.rightField).toBe('id');
            expect(join.kind).toBe(JoinKind.INNER);
        });

        it('join accepts an explicit JoinKind', () => {
            const req = new QueryBuilder('Order')
                .join('customerId', 'Customer', 'id', JoinKind.LEFT)
                .build();
            expect(req.joins[0].kind).toBe(JoinKind.LEFT);
        });
    });
});

describe('GroupByBuilder', () => {
    it('key() adds group-by field', () => {
        const req = groupBy('LineItem').key('orderStatus').build();
        expect(req.keys).toEqual(['orderStatus']);
    });

    it('keys() adds multiple group-by fields', () => {
        const req = groupBy('LineItem').keys('returnFlag', 'lineStatus').build();
        expect(req.keys).toEqual(['returnFlag', 'lineStatus']);
    });

    it('sum() adds metric with auto alias', () => {
        const req = groupBy('LineItem').sum('quantity').build();
        expect(req.metrics).toHaveLength(1);
        expect(req.metrics[0]).toMatchObject({
            name: 'quantity_sum',
            type: AggregationType.SUM,
            field: 'quantity',
            expression: '',
        });
    });

    it('sum() honors an explicit alias', () => {
        const req = groupBy('LineItem').sum('quantity', 'total_qty').build();
        expect(req.metrics[0].name).toBe('total_qty');
    });

    it('sumExpr() adds raw expression', () => {
        const req = groupBy('LineItem')
            .sumExpr('extendedPrice * (1 - discount)', 'disc_price')
            .build();
        expect(req.metrics).toHaveLength(1);
        expect(req.metrics[0]).toMatchObject({
            name: 'disc_price',
            type: AggregationType.SUM,
            field: '',
            expression: 'extendedPrice * (1 - discount)',
        });
    });

    it('countAll() produces empty-field metric', () => {
        const req = groupBy('LineItem').countAll().build();
        expect(req.metrics).toHaveLength(1);
        expect(req.metrics[0]).toMatchObject({
            name: 'count',
            type: AggregationType.COUNT,
            field: '',
            expression: '',
        });
    });

    it('having() adds having clause', () => {
        const req = groupBy('LineItem')
            .key('orderStatus')
            .sum('quantity')
            .having('quantity_sum', SearchOperator.GREATER_THAN, 100)
            .build();
        expect(req.having!.clauses).toHaveLength(1);
        const clause = req.having!.clauses[0];
        expect(clause.property).toBe('quantity_sum');
        expect(clause.operator).toBe(SearchOperator.GREATER_THAN);
        expect(clause.value!.numberVal).toBe(100);
    });

    it('join() adds JoinSpec', () => {
        const req = groupBy('Order')
            .join('customerId', 'Customer', 'id')
            .build();
        expect(req.joins).toHaveLength(1);
        const join = req.joins[0];
        expect(join.leftType).toBe('Order');
        expect(join.rightType).toBe('Customer');
        expect(join.leftField).toBe('customerId');
        expect(join.rightField).toBe('id');
        expect(join.kind).toBe(JoinKind.INNER);
    });

    it('build() sets trace ID', () => {
        const req = groupBy('LineItem').build('trace-123');
        expect(req.traceId).toBe('trace-123');
    });

    it('build() defaults limit to 10000', () => {
        const req = groupBy('LineItem').build();
        expect(req.limit).toBe(10_000);
    });

    it('limit() overrides the default', () => {
        const req = groupBy('LineItem').limit(50).build();
        expect(req.limit).toBe(50);
    });

    it('groupBy() factory returns a GroupByBuilder', () => {
        expect(groupBy('LineItem')).toBeInstanceOf(GroupByBuilder);
    });

    it('Q1-style build has 2 keys and 9 metrics', () => {
        // Mirrors TPC-H Q1 (pricing summary report): group by two flags,
        // aggregate 9 metrics (sums, avgs, and a count).
        const req = groupBy('LineItem')
            .where('shipDate', SearchOperator.LESS_THAN_OR_EQUALS, '1998-12-01')
            .key('returnFlag')
            .key('lineStatus')
            .sum('quantity')
            .sum('extendedPrice')
            .sumExpr('extendedPrice * (1 - discount)', 'disc_price')
            .sumExpr('extendedPrice * (1 - discount) * (1 + tax)', 'charge')
            .avg('quantity')
            .avg('extendedPrice')
            .avg('discount')
            .countAll()
            .count('quantity', 'order_count')
            .orderBy('returnFlag')
            .orderBy('lineStatus')
            .build();

        expect(req.keys).toHaveLength(2);
        expect(req.metrics).toHaveLength(9);
    });
});
