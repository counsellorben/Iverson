/**
 * Decorator-based annotation system for Iverson entities.
 *
 * Usage:
 *   @IversonEntity()
 *   class Article {
 *     @IversonKey()
 *     id: string = '';
 *
 *     @IversonLargeField()
 *     body: string = '';
 *
 *     @IversonSearchKey(0)
 *     category: string = '';
 *
 *     @ManyToOne(() => Author)
 *     authorId: string = '';
 *   }
 */
import 'reflect-metadata';

// ── Metadata symbol keys ───────────────────────────────────────────────────────

const IVERSON_ENTITY_KEY   = Symbol('iverson:entity');
const IVERSON_KEY_KEY      = Symbol('iverson:key');
const IVERSON_SEARCH_KEYS  = Symbol('iverson:search_keys');
const IVERSON_LARGE_FIELDS = Symbol('iverson:large_fields');
const IVERSON_RELATIONS    = Symbol('iverson:relations');

// ── Public relation kind constants ─────────────────────────────────────────────

export type RelationKindString = 'many_to_one' | 'many_to_many' | 'one_to_many' | 'one_to_one';

export interface RelationMeta {
    field: string;
    kind: RelationKindString;
    relatedType: string;
}

export interface SearchKeyMeta {
    field: string;
    order: number;
}

// ── @IversonEntity() ───────────────────────────────────────────────────────────

export function IversonEntity(): ClassDecorator {
    return (target) => {
        Reflect.defineMetadata(IVERSON_ENTITY_KEY, true, target);
    };
}

export function isIversonEntity(target: Function): boolean {
    return Reflect.getMetadata(IVERSON_ENTITY_KEY, target) === true;
}

// ── @IversonKey() ──────────────────────────────────────────────────────────────

export function IversonKey(): PropertyDecorator {
    return (target, propertyKey) => {
        Reflect.defineMetadata(IVERSON_KEY_KEY, String(propertyKey), target.constructor);
    };
}

export function getKeyField(target: Function): string | undefined {
    return Reflect.getMetadata(IVERSON_KEY_KEY, target);
}

// ── @IversonSearchKey(order) ───────────────────────────────────────────────────

export function IversonSearchKey(order: number): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: SearchKeyMeta[] =
            Reflect.getMetadata(IVERSON_SEARCH_KEYS, target.constructor) ?? [];
        existing.push({ field: String(propertyKey), order });
        Reflect.defineMetadata(IVERSON_SEARCH_KEYS, existing, target.constructor);
    };
}

export function getSearchKeys(target: Function): SearchKeyMeta[] {
    const keys: SearchKeyMeta[] = Reflect.getMetadata(IVERSON_SEARCH_KEYS, target) ?? [];
    return [...keys].sort((a, b) => a.order - b.order);
}

// ── @IversonLargeField() ──────────────────────────────────────────────────────

export function IversonLargeField(): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: string[] =
            Reflect.getMetadata(IVERSON_LARGE_FIELDS, target.constructor) ?? [];
        existing.push(String(propertyKey));
        Reflect.defineMetadata(IVERSON_LARGE_FIELDS, existing, target.constructor);
    };
}

export function getLargeFields(target: Function): string[] {
    return Reflect.getMetadata(IVERSON_LARGE_FIELDS, target) ?? [];
}

// ── Relation decorators ────────────────────────────────────────────────────────

function addRelation(target: object, propertyKey: string | symbol, kind: RelationKindString, relatedType: string): void {
    const ctor = (target as any).constructor;
    const existing: RelationMeta[] =
        Reflect.getMetadata(IVERSON_RELATIONS, ctor) ?? [];
    existing.push({ field: String(propertyKey), kind, relatedType });
    Reflect.defineMetadata(IVERSON_RELATIONS, existing, ctor);
}

export function ManyToOne(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        const related = typeFactory();
        addRelation(target, propertyKey, 'many_to_one', related.name);
    };
}

export function ManyToMany(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        const related = typeFactory();
        addRelation(target, propertyKey, 'many_to_many', related.name);
    };
}

export function OneToMany(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        const related = typeFactory();
        addRelation(target, propertyKey, 'one_to_many', related.name);
    };
}

export function OneToOne(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        const related = typeFactory();
        addRelation(target, propertyKey, 'one_to_one', related.name);
    };
}

export function getRelations(target: Function): RelationMeta[] {
    return Reflect.getMetadata(IVERSON_RELATIONS, target) ?? [];
}
