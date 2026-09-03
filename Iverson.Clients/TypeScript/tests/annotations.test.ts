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
    IversonEmbedding,
    IversonChunk,
    IversonSummary,
    IversonKeywords,
    IversonExtracted,
    IversonMetadata,
    IversonArray,
    IversonGuid,
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
    getEmbeddingFields,
    getChunkFields,
    getSummaryFields,
    getKeywordsFields,
    getExtractedFields,
    getMetadataFields,
    getArrayFields,
    getGuidFields,
    getTypeDescription,
    getPropertyDescriptions,
    getRelations,
    getRelationsWithFactory,
    getEmbeddingModel,
} from '../src/annotations.js';
import { ClrType } from '../generated/object_mapping.js';

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
// collection before the subclass's own `defineMetadata` call ever runs — corrupting the
// parent's collection and leaking entries between unrelated sibling subclasses.
//
// This is table-driven rather than one copy-pasted block per site: all twelve accumulate
// sites share the identical bug shape (read via the prototype chain, mutate what came back,
// write back onto the subclass), so the twelve blocks would differ only in which decorator
// and which accessor they call. A table keeps that one shared scenario — parent + two
// siblings + an undecorated grandchild — expressed once, and drives it with the "apply a
// representative decoration" and "read the resulting collection" functions particular to
// each site. `has`/`size` are deliberately generic over the underlying collection shape
// (array, Map, Set, or plain object) rather than forcing every site through array equality.
interface AccumulateSite {
    label: string;
    /** Applies the decorator, with representative arguments, to one property of `klass`. */
    decorate: (klass: Function, propertyKey: string) => void;
    /** Whether the accessor's collection for `target` contains an entry for `propertyKey`. */
    has: (target: Function, propertyKey: string) => boolean;
    /** The number of entries in the accessor's collection for `target`. */
    size: (target: Function) => number;
}

// Referenced by the relation-decorator table row below; its own content is irrelevant, only
// its identity (name) as a relation target matters.
@IversonEntity()
class LeakRelatedTarget {
    @IversonKey()
    id: string = '';
}

const accumulateSites: AccumulateSite[] = [
    {
        label: 'IversonSearchKey / getSearchKeys',
        decorate: (klass, key) => { IversonSearchKey(0)(klass.prototype, key); },
        has: (target, key) => getSearchKeys(target).some(k => k.field === key),
        size: (target) => getSearchKeys(target).length,
    },
    {
        label: 'IversonLargeField / getLargeFields',
        decorate: (klass, key) => { IversonLargeField()(klass.prototype, key); },
        has: (target, key) => getLargeFields(target).includes(key),
        size: (target) => getLargeFields(target).length,
    },
    {
        label: 'IversonEmbedding / getEmbeddingFields',
        decorate: (klass, key) => { IversonEmbedding()(klass.prototype, key); },
        has: (target, key) => getEmbeddingFields(target).includes(key),
        size: (target) => getEmbeddingFields(target).length,
    },
    {
        label: 'IversonChunk / getChunkFields',
        decorate: (klass, key) => { IversonChunk(256, 32)(klass.prototype, key); },
        has: (target, key) => getChunkFields(target).some(c => c.field === key),
        size: (target) => getChunkFields(target).length,
    },
    {
        label: 'IversonSummary / getSummaryFields',
        decorate: (klass, key) => { IversonSummary()(klass.prototype, key); },
        has: (target, key) => getSummaryFields(target).includes(key),
        size: (target) => getSummaryFields(target).length,
    },
    {
        label: 'IversonKeywords / getKeywordsFields',
        decorate: (klass, key) => { IversonKeywords()(klass.prototype, key); },
        has: (target, key) => getKeywordsFields(target).includes(key),
        size: (target) => getKeywordsFields(target).length,
    },
    {
        label: 'IversonExtracted / getExtractedFields',
        decorate: (klass, key) => { IversonExtracted('extract me')(klass.prototype, key); },
        has: (target, key) => getExtractedFields(target).some(e => e.field === key),
        size: (target) => getExtractedFields(target).length,
    },
    {
        label: 'IversonMetadata / getMetadataFields',
        decorate: (klass, key) => { IversonMetadata()(klass.prototype, key); },
        has: (target, key) => getMetadataFields(target).includes(key),
        size: (target) => getMetadataFields(target).length,
    },
    {
        label: 'IversonArray / getArrayFields (Map)',
        decorate: (klass, key) => { IversonArray(ClrType.CLR_STRING)(klass.prototype, key); },
        has: (target, key) => getArrayFields(target).has(key),
        size: (target) => getArrayFields(target).size,
    },
    {
        label: 'IversonGuid / getGuidFields (Set)',
        decorate: (klass, key) => { IversonGuid()(klass.prototype, key); },
        has: (target, key) => getGuidFields(target).has(key),
        size: (target) => getGuidFields(target).size,
    },
    {
        label: 'IversonDescription (property mode) / getPropertyDescriptions (object)',
        decorate: (klass, key) => { IversonDescription('a description')(klass.prototype, key); },
        has: (target, key) => Object.prototype.hasOwnProperty.call(getPropertyDescriptions(target), key),
        size: (target) => Object.keys(getPropertyDescriptions(target)).length,
    },
    {
        label: 'ManyToOne / getRelations',
        decorate: (klass, key) => { ManyToOne(() => LeakRelatedTarget)(klass.prototype, key); },
        has: (target, key) => getRelations(target).some(r => r.field === key),
        size: (target) => getRelations(target).length,
    },
];

describe.each(accumulateSites)('Sibling subclasses do not leak accumulated decorator metadata: $label', (site) => {
    class LeakParent {}
    class LeakSiblingA extends LeakParent {}
    class LeakSiblingB extends LeakParent {}
    class LeakSiblingNoOwnDecorator extends LeakParent {
        // Declares no decorator of its own — reading must still walk the prototype chain
        // and see the parent's entry. Only mutation-on-read changes, not reading.
    }

    site.decorate(LeakParent, 'parentField');
    site.decorate(LeakSiblingA, 'siblingAField');
    site.decorate(LeakSiblingB, 'siblingBField');

    it("the parent's collection still contains exactly its own entry after both siblings are defined", () => {
        expect(site.size(LeakParent)).toBe(1);
        expect(site.has(LeakParent, 'parentField')).toBe(true);
    });

    it("sibling A's view includes its own entry and the parent's, but excludes sibling B's", () => {
        expect(site.has(LeakSiblingA, 'parentField')).toBe(true);
        expect(site.has(LeakSiblingA, 'siblingAField')).toBe(true);
        expect(site.has(LeakSiblingA, 'siblingBField')).toBe(false);
    });

    it("sibling B's view includes its own entry and the parent's, but excludes sibling A's", () => {
        expect(site.has(LeakSiblingB, 'parentField')).toBe(true);
        expect(site.has(LeakSiblingB, 'siblingBField')).toBe(true);
        expect(site.has(LeakSiblingB, 'siblingAField')).toBe(false);
    });

    it('a subclass with no own decorator still inherits only the parent entry', () => {
        expect(site.size(LeakSiblingNoOwnDecorator)).toBe(1);
        expect(site.has(LeakSiblingNoOwnDecorator, 'parentField')).toBe(true);
    });
});
