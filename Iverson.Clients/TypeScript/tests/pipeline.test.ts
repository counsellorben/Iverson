import { describe, expect, it } from 'vitest';
import { pipeline } from '../src/pipeline.js';
import {
    AggregationType,
    DateTrunc,
    JoinKind,
    SearchOperator,
    WindowFunctionKind,
} from '../generated/object_search.js';

describe('PipelineBuilder', () => {
    it('compiles a full pipeline to the expected proto', () => {
        const request = pipeline('Article')
            .where('IsPublished', SearchOperator.EQUALS, true)
            .step('by_author', s => s
                .groupBy('AuthorId')
                .countAll('articles')
                .having('articles', SearchOperator.GREATER_THAN, 5))
            .step('ranked', s => s
                .rowNumber('rank', { orderBy: 'articles', descending: true }))
            .step('named', s => s
                .join('Author', 'AuthorId', 'Id')
                .select(sel => sel.allFrom('ranked').pick('Author', 'Name', 'author_name')))
            .sortOnDesc('rank')
            .limit(5)
            .build();

        expect(request.typeName).toBe('Article');
        expect(request.baseWhere).toHaveLength(1);
        expect(request.steps).toHaveLength(3);

        const agg = request.steps[0];
        expect(agg.name).toBe('by_author');
        expect(agg.groupBy[0]).toMatchObject({ field: 'AuthorId', dateTrunc: DateTrunc.NONE });
        expect(agg.metrics[0]).toMatchObject({ name: 'articles', type: AggregationType.COUNT });
        expect(agg.having[0].property).toBe('articles');

        const win = request.steps[1];
        expect(win.windows[0]).toMatchObject({
            alias: 'rank', kind: WindowFunctionKind.ROW_NUMBER,
            orderBy: 'articles', descending: true,
        });

        const joined = request.steps[2];
        expect(joined.joins[0]).toMatchObject({ source: 'Author', kind: JoinKind.INNER });
        expect(joined.joins[0].on[0]).toMatchObject({ left: 'AuthorId', right: 'Id' });
        expect(joined.select[0].all).toBe(true);
        expect(joined.select[1].alias).toBe('author_name');

        expect(request.limit).toBe(5);
    });

    it('carries explicit reads and defaults the limit', () => {
        const request = pipeline('Article')
            .step('a', s => s.derive('x', 'WordCount + 1'))
            .step('b', s => s.reads('base').derive('y', 'WordCount + 2'))
            .build();

        expect(request.steps[1].reads).toBe('base');
        expect(request.limit).toBe(10_000);
    });

    it('throws on duplicate step names', () => {
        const b = pipeline('Article').step('x', s => s.derive('a', 'WordCount'));
        expect(() => b.step('X', s => s.derive('b', 'WordCount'))).toThrow(/X/);
    });

    it('throws on reads of an unknown step', () => {
        expect(() => pipeline('Article').step('a', s => s.reads('nope'))).toThrow(/nope/);
    });

    it('throws when windows and groupBy share a step', () => {
        expect(() => pipeline('Article').step('bad', s => s
            .rowNumber('rn', { orderBy: 'Id' })
            .groupBy('AuthorId').countAll('n'))).toThrow(/bad/);
    });

    it('throws on a join without a select', () => {
        expect(() => pipeline('Article').step('bad', s => s.join('Author', 'AuthorId', 'Id')))
            .toThrow(/select/);
    });

    it('throws on duplicate aliases within a step', () => {
        expect(() => pipeline('Article').step('bad', s => s
            .rowNumber('x', { orderBy: 'Id' })
            .derive('X', 'WordCount + 1'))).toThrow(/X/);
    });
});
