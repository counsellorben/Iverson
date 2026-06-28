/**
 * Tests for the annotation/decorator system.
 * Verifies that Reflect.getMetadata reads correctly via the public getters.
 */
import 'reflect-metadata';
import { describe, it, expect } from 'vitest';
import {
    IversonEntity,
    IversonKey,
    IversonSearchKey,
    IversonLargeField,
    ManyToOne,
    ManyToMany,
    OneToMany,
    isIversonEntity,
    getKeyField,
    getSearchKeys,
    getLargeFields,
    getRelations,
} from '../src/annotations.js';

// ── Test entities ─────────────────────────────────────────────────────────────

class PlainAuthor {
    id: string = '';
    name: string = '';
}

@IversonEntity()
class TestAuthor {
    @IversonKey()
    id: string = '';

    name: string = '';
}

@IversonEntity()
class TestArticle {
    @IversonKey()
    id: string = '';

    title: string = '';

    @IversonLargeField()
    body: string = '';

    @IversonSearchKey(0)
    category: string = '';

    wordCount: number = 0;

    @IversonSearchKey(1)
    publishedAt: Date = new Date();

    @ManyToOne(() => TestAuthor)
    authorId: string = '';
}

@IversonEntity()
class TestPost {
    @IversonKey()
    id: string = '';

    @OneToMany(() => TestAuthor)
    comments: string = '';

    @ManyToMany(() => TestAuthor)
    tags: string = '';
}

// ── Tests ─────────────────────────────────────────────────────────────────────

describe('IversonEntity decorator', () => {
    it('marks a class as an Iverson entity', () => {
        expect(isIversonEntity(TestArticle)).toBe(true);
    });

    it('returns false for plain (undecorated) classes', () => {
        expect(isIversonEntity(PlainAuthor)).toBe(false);
    });
});

describe('IversonKey decorator', () => {
    it('stores the key field name', () => {
        expect(getKeyField(TestArticle)).toBe('id');
    });

    it('stores key field on a simple entity', () => {
        expect(getKeyField(TestAuthor)).toBe('id');
    });
});

describe('IversonSearchKey decorator', () => {
    it('stores search key fields with their order', () => {
        const keys = getSearchKeys(TestArticle);
        expect(keys).toHaveLength(2);
        expect(keys[0]).toEqual({ field: 'category', order: 0 });
        expect(keys[1]).toEqual({ field: 'publishedAt', order: 1 });
    });

    it('returns search keys sorted by order', () => {
        @IversonEntity()
        class OrderTest {
            @IversonSearchKey(2)
            third: string = '';

            @IversonSearchKey(0)
            first: string = '';

            @IversonSearchKey(1)
            second: string = '';
        }

        const keys = getSearchKeys(OrderTest);
        expect(keys.map(k => k.field)).toEqual(['first', 'second', 'third']);
    });

    it('returns empty array when no search keys', () => {
        expect(getSearchKeys(TestAuthor)).toHaveLength(0);
    });
});

describe('IversonLargeField decorator', () => {
    it('stores large field names', () => {
        const fields = getLargeFields(TestArticle);
        expect(fields).toContain('body');
    });

    it('returns empty array when no large fields', () => {
        expect(getLargeFields(TestAuthor)).toHaveLength(0);
    });
});

describe('Relation decorators', () => {
    it('ManyToOne stores relation metadata', () => {
        const relations = getRelations(TestArticle);
        expect(relations).toHaveLength(1);
        expect(relations[0]).toMatchObject({
            field: 'authorId',
            kind: 'many_to_one',
            relatedType: 'TestAuthor',
        });
    });

    it('OneToMany stores relation metadata', () => {
        const relations = getRelations(TestPost);
        const otm = relations.find(r => r.kind === 'one_to_many');
        expect(otm).toBeDefined();
        expect(otm!.field).toBe('comments');
    });

    it('ManyToMany stores relation metadata', () => {
        const relations = getRelations(TestPost);
        const mtm = relations.find(r => r.kind === 'many_to_many');
        expect(mtm).toBeDefined();
        expect(mtm!.field).toBe('tags');
    });

    it('returns empty array when no relations', () => {
        expect(getRelations(TestAuthor)).toHaveLength(0);
    });
});
