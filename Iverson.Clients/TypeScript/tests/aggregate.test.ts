import { describe, expect, it } from 'vitest';
import { aggregate } from '../src/aggregate.js';
import {
    AggregationType,
    JoinKind,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
} from '../generated/object_search.js';

describe('AggregateBuilder — buckets (TERMS / DATE_HISTOGRAM / RANGE)', () => {
    it('terms() adds a TERMS aggregation spec', () => {
        const req = aggregate('Article').terms('Category', 'byCategory', 5).build();
        expect(req.aggregations).toHaveLength(1);
        const spec = req.aggregations[0];
        expect(spec.name).toBe('byCategory');
        expect(spec.type).toBe(AggregationType.TERMS);
        expect(spec.field).toBe('Category');
        expect(spec.size).toBe(5);
    });

    it('terms() defaults size to 10', () => {
        const req = aggregate('Article').terms('Category', 'byCategory').build();
        expect(req.aggregations[0].size).toBe(10);
    });

    it('dateHistogram() adds a DATE_HISTOGRAM aggregation spec', () => {
        const req = aggregate('Article')
            .dateHistogram('PublishedAt', 'byMonth', 'month', 'America/New_York')
            .build();
        const spec = req.aggregations[0];
        expect(spec.type).toBe(AggregationType.DATE_HISTOGRAM);
        expect(spec.field).toBe('PublishedAt');
        expect(spec.calendarInterval).toBe('month');
        expect(spec.timeZone).toBe('America/New_York');
    });

    it('dateHistogram() defaults timeZone to empty string', () => {
        const req = aggregate('Article').dateHistogram('PublishedAt', 'byMonth', 'month').build();
        expect(req.aggregations[0].timeZone).toBe('');
    });

    it('range() adds buckets with bounds', () => {
        const req = aggregate('Article')
            .range('WordCount', 'byLength', [
                { key: 'short', to: 500 },
                { key: 'long', from: 500 },
            ])
            .build();
        const spec = req.aggregations[0];
        expect(spec.type).toBe(AggregationType.RANGE);
        expect(spec.field).toBe('WordCount');
        expect(spec.rangeBuckets).toHaveLength(2);
        expect(spec.rangeBuckets[0]).toMatchObject({ key: 'short', to: 500 });
        expect(spec.rangeBuckets[0].from).toBeUndefined();
        expect(spec.rangeBuckets[1]).toMatchObject({ key: 'long', from: 500 });
        expect(spec.rangeBuckets[1].to).toBeUndefined();
    });

    it('range() bucket without a key defaults to an empty string', () => {
        const req = aggregate('Article')
            .range('WordCount', 'byLength', [{ from: 0, to: 100 }])
            .build();
        expect(req.aggregations[0].rangeBuckets[0].key).toBe('');
    });
});

describe('AggregateBuilder — metrics (AVG / SUM / MIN / MAX / COUNT / countAll)', () => {
    it('avg/sum/min/max/count/countAll add the expected metric specs', () => {
        const req = aggregate('Article')
            .avg('WordCount', 'avgWc')
            .sum('WordCount', 'sumWc')
            .min('WordCount', 'minWc')
            .max('WordCount', 'maxWc')
            .count('WordCount', 'countWc')
            .countAll('total')
            .build();

        const byName = Object.fromEntries(req.aggregations.map(a => [a.name, a]));
        expect(byName.avgWc.type).toBe(AggregationType.AVG);
        expect(byName.sumWc.type).toBe(AggregationType.SUM);
        expect(byName.minWc.type).toBe(AggregationType.MIN);
        expect(byName.maxWc.type).toBe(AggregationType.MAX);
        expect(byName.countWc.type).toBe(AggregationType.COUNT);
        expect(byName.total.type).toBe(AggregationType.COUNT);
        expect(byName.total.field).toBe('');
    });

    it('countAll() defaults its alias to "count"', () => {
        const req = aggregate('Article').countAll().build();
        expect(req.aggregations[0].name).toBe('count');
    });
});

describe('AggregateBuilder — WHERE / HAVING / JOIN', () => {
    it('where() adds a FILTER clause', () => {
        const req = aggregate('Article').where('Category', SearchOperator.EQUALS, 'tech').countAll('n').build();
        expect(req.query!.clauses[0].property).toBe('Category');
        expect(req.query!.clauses[0].clauseType).toBe(SearchClauseType.FILTER);
    });

    it('not() adds a MUST_NOT clause', () => {
        const req = aggregate('Article').not('Category', SearchOperator.EQUALS, 'spam').countAll('n').build();
        expect(req.query!.clauses[0].clauseType).toBe(SearchClauseType.MUST_NOT);
    });

    it('withLogic(OR) is carried onto the WHERE query', () => {
        const req = aggregate('Article')
            .where('A', SearchOperator.EQUALS, 1)
            .where('B', SearchOperator.EQUALS, 2)
            .withLogic(SearchLogic.OR)
            .countAll('n')
            .build();
        expect(req.query!.logic).toBe(SearchLogic.OR);
    });

    it('having() adds a FILTER clause referencing an aggregation alias', () => {
        const req = aggregate('Article').countAll('n').having('n', SearchOperator.GREATER_THAN, 5).build();
        expect(req.having!.clauses[0].property).toBe('n');
        expect(req.having!.clauses[0].clauseType).toBe(SearchClauseType.FILTER);
    });

    it('withHavingLogic(OR) is carried onto the HAVING query', () => {
        const req = aggregate('Article').countAll('n')
            .having('n', SearchOperator.GREATER_THAN, 5)
            .having('m', SearchOperator.LESS_THAN, 1)
            .withHavingLogic(SearchLogic.OR)
            .build();
        expect(req.having!.logic).toBe(SearchLogic.OR);
    });

    it('join() adds a JoinSpec inferring leftType from this builder\'s own type', () => {
        const req = aggregate('Article').join('AuthorId', 'Author', 'Id').countAll('n').build();
        expect(req.joins).toHaveLength(1);
        expect(req.joins[0]).toMatchObject({
            leftType: 'Article',
            rightType: 'Author',
            leftField: 'AuthorId',
            rightField: 'Id',
            kind: JoinKind.INNER,
        });
    });
});

describe('AggregateBuilder — build validation', () => {
    it('throws on duplicate aggregation names', () => {
        const b = aggregate('Article').sum('WordCount', 'wc').sum('Price', 'wc');
        expect(() => b.build()).toThrow(/wc/);
    });

    it('passes traceId through to the request', () => {
        const req = aggregate('Article').countAll('n').build('trace-123');
        expect(req.traceId).toBe('trace-123');
    });
});
