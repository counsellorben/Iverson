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
    IversonMetadata,
    IversonDescription,
    IversonEmbeddingModel,
    ManyToOne,
    ManyToMany,
    OneToMany,
    OneToOne,
    isIversonEntity,
    getKeyField,
    getSearchKeys,
    getLargeFields,
    getMetadataFields,
    getTypeDescription,
    getPropertyDescriptions,
    getRelations,
    getRelationsWithFactory,
    getEmbeddingModel,
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

// ── Forward-reference entities ──────────────────────────────────────────────────
//
// Each of these decorated classes is declared BEFORE the class its relation
// decorator names (`LaterDeclared`, below). Property decorators run at
// class-definition time, so if a relation decorator called its `typeFactory`
// eagerly, evaluating `() => LaterDeclared` here would throw
// `ReferenceError: Cannot access 'LaterDeclared' before initialization` (TDZ)
// while THIS MODULE is still loading — i.e. before any test body even runs.
// Against the fixed (lazy) implementation, the factory is only invoked inside
// `getRelations`, by which point the whole module has finished initializing,
// so `LaterDeclared` is available and these classes construct without error.

@IversonEntity()
class ForwardManyToOne {
    @IversonKey()
    id: string = '';

    @ManyToOne(() => LaterDeclared)
    laterId: string = '';
}

@IversonEntity()
class ForwardManyToMany {
    @IversonKey()
    id: string = '';

    @ManyToMany(() => LaterDeclared)
    laterIds: string = '';
}

@IversonEntity()
class ForwardOneToMany {
    @IversonKey()
    id: string = '';

    @OneToMany(() => LaterDeclared)
    laters: string = '';
}

@IversonEntity()
class ForwardOneToOne {
    @IversonKey()
    id: string = '';

    @OneToOne(() => LaterDeclared)
    laterId: string = '';
}

@IversonEntity()
class LaterDeclared {
    @IversonKey()
    id: string = '';
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

describe('getRelationsWithFactory', () => {
    it('returns the unresolved typeFactory alongside field and kind', () => {
        const relations = getRelationsWithFactory(TestArticle);
        expect(relations).toHaveLength(1);
        expect(relations[0].field).toBe('authorId');
        expect(relations[0].kind).toBe('many_to_one');
        expect(typeof relations[0].typeFactory).toBe('function');
        // Unlike getRelations, the factory itself is handed back, not collapsed to a name.
        expect(relations[0].typeFactory()).toBe(TestAuthor);
    });

    it('returns empty array when no relations', () => {
        expect(getRelationsWithFactory(TestAuthor)).toHaveLength(0);
    });
});

describe('Relation decorators with forward references', () => {
    // These prove the typeFactory is resolved lazily (at getRelations() call
    // time) rather than eagerly (at class-decoration time). All four classes
    // above are declared BEFORE `LaterDeclared`; if any decorator called its
    // factory eagerly, importing this test file would itself throw a
    // ReferenceError before any `it()` here could even run.

    it('ManyToOne resolves a class declared later in the module', () => {
        const relations = getRelations(ForwardManyToOne);
        expect(relations).toHaveLength(1);
        expect(relations[0]).toMatchObject({
            field: 'laterId',
            kind: 'many_to_one',
            relatedType: 'LaterDeclared',
        });
    });

    it('ManyToMany resolves a class declared later in the module', () => {
        const relations = getRelations(ForwardManyToMany);
        expect(relations).toHaveLength(1);
        expect(relations[0]).toMatchObject({
            field: 'laterIds',
            kind: 'many_to_many',
            relatedType: 'LaterDeclared',
        });
    });

    it('OneToMany resolves a class declared later in the module', () => {
        const relations = getRelations(ForwardOneToMany);
        expect(relations).toHaveLength(1);
        expect(relations[0]).toMatchObject({
            field: 'laters',
            kind: 'one_to_many',
            relatedType: 'LaterDeclared',
        });
    });

    it('OneToOne resolves a class declared later in the module', () => {
        const relations = getRelations(ForwardOneToOne);
        expect(relations).toHaveLength(1);
        expect(relations[0]).toMatchObject({
            field: 'laterId',
            kind: 'one_to_one',
            relatedType: 'LaterDeclared',
        });
    });
});

// ── Metadata / description decorators ─────────────────────────────────────────

@IversonEntity()
@IversonDescription('A product catalog entry.')
class DescribedProduct {
    @IversonKey()
    @IversonDescription('Stable product identifier.')
    id: string = '';

    @IversonMetadata()
    @IversonDescription('Merchandising category.')
    category: string = '';

    @IversonMetadata()
    region: string = '';

    plain: string = '';
}

@IversonEntity()
class UndescribedProduct {
    @IversonKey()
    id: string = '';
}

describe('@IversonMetadata', () => {
    it('collects every marked field', () => {
        expect(getMetadataFields(DescribedProduct).sort()).toEqual(['category', 'region']);
    });

    it('returns empty array when none are marked', () => {
        expect(getMetadataFields(UndescribedProduct)).toHaveLength(0);
    });
});

describe('@IversonDescription', () => {
    it('stores the class-level description', () => {
        expect(getTypeDescription(DescribedProduct)).toBe('A product catalog entry.');
    });

    it('stores property descriptions, including on the key property', () => {
        const descriptions = getPropertyDescriptions(DescribedProduct);
        expect(descriptions['id']).toBe('Stable product identifier.');
        expect(descriptions['category']).toBe('Merchandising category.');
    });

    it('omits undecorated properties', () => {
        expect(getPropertyDescriptions(DescribedProduct)['plain']).toBeUndefined();
    });

    it('returns empty defaults when absent', () => {
        expect(getTypeDescription(UndescribedProduct)).toBe('');
        expect(getPropertyDescriptions(UndescribedProduct)).toEqual({});
    });
});

// ── @IversonEmbeddingModel(modelId) ────────────────────────────────────────────
//
// Class-level, never per-property; the stamping onto modelId/chunkModelId is pinned separately
// in tests/core.test.ts's `describeEntity — embedding model declaration` block. This only pins
// that the decorator itself carries the value through Reflect.defineMetadata.

@IversonEntity()
@IversonEmbeddingModel('nomic-embed-text')
class ModelDeclaredProduct {
    @IversonKey()
    id: string = '';
}

describe('@IversonEmbeddingModel', () => {
    it('stores the declared model, readable via getEmbeddingModel', () => {
        expect(getEmbeddingModel(ModelDeclaredProduct)).toBe('nomic-embed-text');
    });

    it('defaults to empty string when never declared', () => {
        expect(getEmbeddingModel(UndescribedProduct)).toBe('');
    });
});

// ── Sibling subclasses do not leak accumulated decorator metadata ─────────────
//
// Every accumulate-style decorator (ManyToOne/ManyToMany/OneToMany/OneToOne, IversonMetadata,
// and their siblings in src/annotations.ts) reads via `Reflect.getMetadata(KEY, ctor) ?? []`
// then pushes and writes back. `Reflect.getMetadata` walks the prototype chain, so on a
// SUBCLASS the first decorated field would read (and, pre-fix, mutate in place) the PARENT's
// array before the subclass's own `defineMetadata` call ever runs — corrupting the parent's
// list and leaking entries between unrelated sibling subclasses. This section pins that a
// sibling's decoration cannot affect another sibling or the shared parent.

@IversonEntity()
class LeakParentAuthor {
    @IversonKey()
    id: string = '';
}

@IversonEntity()
class LeakParent {
    @IversonKey()
    id: string = '';

    @ManyToOne(() => LeakParentAuthor)
    parentAuthorId: string = '';

    @IversonMetadata()
    parentRegion: string = '';
}

@IversonEntity()
class LeakSiblingA extends LeakParent {
    @ManyToOne(() => LeakParentAuthor)
    siblingAAuthorId: string = '';

    @IversonMetadata()
    siblingAOnly: string = '';
}

@IversonEntity()
class LeakSiblingB extends LeakParent {
    @ManyToOne(() => LeakParentAuthor)
    siblingBAuthorId: string = '';

    @IversonMetadata()
    siblingBOnly: string = '';
}

@IversonEntity()
class LeakSiblingNoOwnDecorator extends LeakParent {
    // Declares no relation/metadata decorator of its own — reading must still walk the
    // prototype chain and see the parent's entries. Only mutation-on-read changes, not reading.
}

describe('Sibling subclasses do not leak accumulated decorator metadata', () => {
    it("the parent's relation list still has exactly its own entry after both siblings are defined", () => {
        const relations = getRelations(LeakParent);
        expect(relations).toHaveLength(1);
        expect(relations[0].field).toBe('parentAuthorId');
    });

    it("getRelations(SiblingA) does not contain SiblingB's relation", () => {
        const fields = getRelations(LeakSiblingA).map(r => r.field);
        expect(fields).toContain('parentAuthorId');
        expect(fields).toContain('siblingAAuthorId');
        expect(fields).not.toContain('siblingBAuthorId');
    });

    it("getRelations(SiblingB) does not contain SiblingA's relation", () => {
        const fields = getRelations(LeakSiblingB).map(r => r.field);
        expect(fields).toContain('parentAuthorId');
        expect(fields).toContain('siblingBAuthorId');
        expect(fields).not.toContain('siblingAAuthorId');
    });

    it('a subclass with no own relation decorator still inherits the parents', () => {
        const fields = getRelations(LeakSiblingNoOwnDecorator).map(r => r.field);
        expect(fields).toEqual(['parentAuthorId']);
    });

    // Pin the same guarantee on a second, non-relation accumulate list (IversonMetadata's
    // string[]) so the copy-on-read fix is verified generally, not only for relations.

    it("the parent's metadata-field list still has exactly its own entry after both siblings are defined", () => {
        expect(getMetadataFields(LeakParent)).toEqual(['parentRegion']);
    });

    it("getMetadataFields(SiblingA) does not contain SiblingB's field", () => {
        const fields = getMetadataFields(LeakSiblingA);
        expect(fields).toContain('parentRegion');
        expect(fields).toContain('siblingAOnly');
        expect(fields).not.toContain('siblingBOnly');
    });

    it("getMetadataFields(SiblingB) does not contain SiblingA's field", () => {
        const fields = getMetadataFields(LeakSiblingB);
        expect(fields).toContain('parentRegion');
        expect(fields).toContain('siblingBOnly');
        expect(fields).not.toContain('siblingAOnly');
    });

    it('a subclass with no own metadata decorator still inherits the parents', () => {
        expect(getMetadataFields(LeakSiblingNoOwnDecorator)).toEqual(['parentRegion']);
    });
});
