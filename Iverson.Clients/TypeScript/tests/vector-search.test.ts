import { describe, it, expect } from 'vitest';
import { similar, chunks } from '../src/vector-search.js';
import { SearchOperator } from '../src/search.js';

describe('SimilarBuilder', () => {
    it('build happy path produces expected request', () => {
        const req = similar('Article', 'Title')
            .text('machine learning')
            .topK(10)
            .where('Category', SearchOperator.EQUALS, 'Tech')
            .build();

        expect(req.typeName).toBe('Article');
        expect(req.property).toBe('Title');
        expect(req.query).toBe('machine learning');
        expect(req.topK).toBe(10);
        expect(req.filter).toHaveLength(1);
        expect(req.filter[0].property).toBe('Category');
    });

    it('where throws on CONTAINS operator', () => {
        const b = similar('Article', 'Title');
        expect(() => b.where('Category', SearchOperator.CONTAINS, 'x')).toThrow();
    });

    it('where throws on VECTOR_SIMILAR operator', () => {
        const b = similar('Article', 'Title');
        expect(() => b.where('Category', SearchOperator.VECTOR_SIMILAR, 'x')).toThrow();
    });
});

describe('ChunksBuilder', () => {
    it('build happy path produces expected request', () => {
        const req = chunks('Article', 'Body')
            .text('neural networks')
            .topK(5)
            .where('Id', SearchOperator.EQUALS, 'parent-123')
            .build();

        expect(req.typeName).toBe('Article');
        expect(req.property).toBe('Body');
        expect(req.topK).toBe(5);
        expect(req.filter).toHaveLength(1);
        expect(req.filter[0].property).toBe('Id');
    });

    it('where throws on non-EQUALS operator', () => {
        const b = chunks('Article', 'Body');
        expect(() => b.where('Id', SearchOperator.GREATER_THAN, 'x')).toThrow();
    });

    it('where throws on a second call', () => {
        const b = chunks('Article', 'Body').where('Id', SearchOperator.EQUALS, 'a');
        expect(() => b.where('Id', SearchOperator.EQUALS, 'b')).toThrow();
    });
});
