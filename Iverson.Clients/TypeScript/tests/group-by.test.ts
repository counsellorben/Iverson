import { describe, expect, it } from 'vitest';
import { groupBy } from '../src/group-by.js';
import { SearchClauseType, SearchLogic, SearchOperator } from '../generated/object_search.js';

describe('GroupByBuilder validation and additions', () => {
    it('not() adds a MUST_NOT clause', () => {
        const req = groupBy('Article').keys('Category').countAll('n')
            .not('Category', SearchOperator.EQUALS, 'spam').build();
        expect(req.query!.clauses[0].clauseType).toBe(SearchClauseType.MUST_NOT);
    });

    it('withHavingLogic(OR) is carried', () => {
        const req = groupBy('Article').keys('Category').countAll('n')
            .having('n', SearchOperator.GREATER_THAN, 5)
            .withHavingLogic(SearchLogic.OR).build();
        expect(req.having!.logic).toBe(SearchLogic.OR);
    });

    it('throws on duplicate metric aliases', () => {
        const b = groupBy('Article').keys('Category').sum('WordCount').sum('WordCount');
        expect(() => b.build()).toThrow(/WordCount_sum/);
    });

    it('throws on HAVING with an unknown alias', () => {
        const b = groupBy('Article').keys('Category').countAll('n')
            .having('misspelled', SearchOperator.GREATER_THAN, 5);
        expect(() => b.build()).toThrow(/misspelled/);
    });

    it('allows HAVING on a key', () => {
        expect(() => groupBy('Article').keys('Category').countAll('n')
            .having('Category', SearchOperator.EQUALS, 'tech').build()).not.toThrow();
    });

    it('throws on orderBy with an unknown alias', () => {
        const b = groupBy('Article').keys('Category').countAll('n').orderBy('nope');
        expect(() => b.build()).toThrow(/nope/);
    });
});
