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

import { ClrType } from '../generated/object_mapping.js';

// ── Metadata symbol keys ───────────────────────────────────────────────────────

const IVERSON_ENTITY_KEY   = Symbol('iverson:entity');
const IVERSON_KEY_KEY      = Symbol('iverson:key');
const IVERSON_SEARCH_KEYS  = Symbol('iverson:search_keys');
const IVERSON_LARGE_FIELDS = Symbol('iverson:large_fields');
const IVERSON_EMBEDDING_FIELDS = Symbol('iverson:embedding_fields');
const IVERSON_CHUNK_FIELDS     = Symbol('iverson:chunk_fields');
const IVERSON_RELATIONS    = Symbol('iverson:relations');
const IVERSON_METADATA_FIELDS  = Symbol('iverson:metadata_fields');
const IVERSON_SUMMARY_FIELDS   = Symbol('iverson:summary_fields');
const IVERSON_KEYWORDS_FIELDS  = Symbol('iverson:keywords_fields');
const IVERSON_EXTRACTED_FIELDS = Symbol('iverson:extracted_fields');
const IVERSON_PROPERTY_DESCRIPTIONS = Symbol('iverson:property_descriptions');
const IVERSON_TYPE_DESCRIPTION      = Symbol('iverson:type_description');
const IVERSON_TENANT_FIELDS    = Symbol('iverson:tenant_fields');

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

// ── @IversonEmbedding() ──────────────────────────────────────────────────────

export function IversonEmbedding(): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: string[] =
            Reflect.getMetadata(IVERSON_EMBEDDING_FIELDS, target.constructor) ?? [];
        existing.push(String(propertyKey));
        Reflect.defineMetadata(IVERSON_EMBEDDING_FIELDS, existing, target.constructor);
    };
}

export function getEmbeddingFields(target: Function): string[] {
    return Reflect.getMetadata(IVERSON_EMBEDDING_FIELDS, target) ?? [];
}

// ── @IversonChunk(maxTokens, overlap) ────────────────────────────────────────

export interface ChunkMeta {
    field: string;
    maxTokens: number;
    overlap: number;
    contextual: boolean;
}

export interface ChunkOptions {
    contextual?: boolean;
}

export function IversonChunk(maxTokens: number = 512, overlap: number = 64, options: ChunkOptions = {}): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: ChunkMeta[] =
            Reflect.getMetadata(IVERSON_CHUNK_FIELDS, target.constructor) ?? [];
        existing.push({ field: String(propertyKey), maxTokens, overlap, contextual: options.contextual ?? false });
        Reflect.defineMetadata(IVERSON_CHUNK_FIELDS, existing, target.constructor);
    };
}

export function getChunkFields(target: Function): ChunkMeta[] {
    return Reflect.getMetadata(IVERSON_CHUNK_FIELDS, target) ?? [];
}

// ── @IversonSummary() ─────────────────────────────────────────────────────────

/** Marks a property as the target for an Ollama-driven summary during ingest enrichment. */
export function IversonSummary(): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: string[] =
            Reflect.getMetadata(IVERSON_SUMMARY_FIELDS, target.constructor) ?? [];
        existing.push(String(propertyKey));
        Reflect.defineMetadata(IVERSON_SUMMARY_FIELDS, existing, target.constructor);
    };
}

export function getSummaryFields(target: Function): string[] {
    return Reflect.getMetadata(IVERSON_SUMMARY_FIELDS, target) ?? [];
}

// ── @IversonKeywords() ────────────────────────────────────────────────────────

/** Marks a property as the target for Ollama-driven keyword extraction during ingest enrichment. */
export function IversonKeywords(): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: string[] =
            Reflect.getMetadata(IVERSON_KEYWORDS_FIELDS, target.constructor) ?? [];
        existing.push(String(propertyKey));
        Reflect.defineMetadata(IVERSON_KEYWORDS_FIELDS, existing, target.constructor);
    };
}

export function getKeywordsFields(target: Function): string[] {
    return Reflect.getMetadata(IVERSON_KEYWORDS_FIELDS, target) ?? [];
}

// ── @IversonExtracted(hint) ───────────────────────────────────────────────────

export interface ExtractedMeta {
    field: string;
    hint: string;
}

/**
 * Marks a property as the target for an Ollama-driven extraction during
 * ingest enrichment, guided by `hint`.
 *
 * The hint is mandatory: the server only treats a property as an extraction
 * target when a non-empty hint is present (`SchemaBuilder.cs` only creates
 * the Extracted target when the hint is non-empty), so a blank hint would be
 * silently dropped server-side. This decorator rejects that case up front —
 * at decoration time, not just at the type level, since JS callers bypass
 * TypeScript's compile-time checks entirely.
 */
export function IversonExtracted(hint: string): PropertyDecorator {
    return (target, propertyKey) => {
        if (hint === undefined || hint === null || hint.trim() === '') {
            throw new Error(
                `@IversonExtracted() on ${target.constructor.name}.${String(propertyKey)} requires a ` +
                'non-blank extraction hint; the server treats an empty hint as "not an extraction ' +
                'target" and would silently drop it.',
            );
        }
        const existing: ExtractedMeta[] =
            Reflect.getMetadata(IVERSON_EXTRACTED_FIELDS, target.constructor) ?? [];
        existing.push({ field: String(propertyKey), hint });
        Reflect.defineMetadata(IVERSON_EXTRACTED_FIELDS, existing, target.constructor);
    };
}

export function getExtractedFields(target: Function): ExtractedMeta[] {
    return Reflect.getMetadata(IVERSON_EXTRACTED_FIELDS, target) ?? [];
}

// ── @IversonMetadata() ───────────────────────────────────────────────────────

/** Marks a scalar property as chunk metadata — denormalized onto chunk points. */
export function IversonMetadata(): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: string[] =
            Reflect.getMetadata(IVERSON_METADATA_FIELDS, target.constructor) ?? [];
        existing.push(String(propertyKey));
        Reflect.defineMetadata(IVERSON_METADATA_FIELDS, existing, target.constructor);
    };
}

export function getMetadataFields(target: Function): string[] {
    return Reflect.getMetadata(IVERSON_METADATA_FIELDS, target) ?? [];
}

// ── @IversonArray(elementType) ─────────────────────────────────────────────────

const IVERSON_ARRAY_KEY = Symbol('iverson:array');

/**
 * Declares a property as an array column, naming its element type explicitly.
 * TypeScript cannot infer the element type: `emitDecoratorMetadata` erases it
 * (design:type reports only the `Array` constructor), and an initialized `[]`
 * carries no element type either. Without this decorator, the registrar would
 * either silently fall back to a scalar CLR_STRING column or, for undecorated
 * declarations, skip detection entirely.
 */
export function IversonArray(elementType: ClrType): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: Map<string, ClrType> =
            Reflect.getMetadata(IVERSON_ARRAY_KEY, target.constructor) ?? new Map();
        existing.set(String(propertyKey), elementType);
        Reflect.defineMetadata(IVERSON_ARRAY_KEY, existing, target.constructor);
    };
}

export function getArrayFields(target: Function): Map<string, ClrType> {
    return Reflect.getMetadata(IVERSON_ARRAY_KEY, target) ?? new Map();
}

// ── @IversonGuid() ──────────────────────────────────────────────────────────

const IVERSON_GUID_KEY = Symbol('iverson:guid');

/**
 * Declares a property as a UUID column. TypeScript has no UUID type — a GUID is
 * carried as a `string` — so the runtime cannot distinguish it from any other
 * string. The server requires key and foreign-key columns to be UUID.
 */
export function IversonGuid(): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: Set<string> =
            Reflect.getMetadata(IVERSON_GUID_KEY, target.constructor) ?? new Set();
        existing.add(String(propertyKey));
        Reflect.defineMetadata(IVERSON_GUID_KEY, existing, target.constructor);
    };
}

export function getGuidFields(target: Function): Set<string> {
    return Reflect.getMetadata(IVERSON_GUID_KEY, target) ?? new Set();
}

// ── @IversonTenant() ────────────────────────────────────────────────────────

/**
 * Marks the scalar property that carries the tenant identifier. Exactly one
 * property per entity must carry this decorator: the server rejects schema
 * registration when `tenant_field` is missing, and this decorator's own
 * marked-property count is validated in `describeEntity` (zero or more than
 * one is a client-side error, since the server only ever sees a single name
 * on the wire and cannot detect that duplication itself).
 */
export function IversonTenant(): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: string[] =
            Reflect.getMetadata(IVERSON_TENANT_FIELDS, target.constructor) ?? [];
        existing.push(String(propertyKey));
        Reflect.defineMetadata(IVERSON_TENANT_FIELDS, existing, target.constructor);
    };
}

export function getTenantFields(target: Function): string[] {
    return Reflect.getMetadata(IVERSON_TENANT_FIELDS, target) ?? [];
}

// ── @IversonDescription(text) ────────────────────────────────────────────────

/**
 * Attaches free-form descriptive text. Usable on an entity class (populates
 * TypeDescriptor.description) or on any property (populates
 * PropertyDescriptor.description), including the key property.
 */
export function IversonDescription(text: string): ClassDecorator & PropertyDecorator {
    return ((target: any, propertyKey?: string | symbol) => {
        if (propertyKey === undefined) {
            // Class decorator: target is the constructor.
            Reflect.defineMetadata(IVERSON_TYPE_DESCRIPTION, text, target);
            return;
        }
        const ctor = target.constructor;
        const existing: Record<string, string> =
            Reflect.getMetadata(IVERSON_PROPERTY_DESCRIPTIONS, ctor) ?? {};
        existing[String(propertyKey)] = text;
        Reflect.defineMetadata(IVERSON_PROPERTY_DESCRIPTIONS, existing, ctor);
    }) as ClassDecorator & PropertyDecorator;
}

export function getTypeDescription(target: Function): string {
    return Reflect.getMetadata(IVERSON_TYPE_DESCRIPTION, target) ?? '';
}

export function getPropertyDescriptions(target: Function): Record<string, string> {
    return Reflect.getMetadata(IVERSON_PROPERTY_DESCRIPTIONS, target) ?? {};
}

// ── Relation decorators ────────────────────────────────────────────────────────

export interface PendingRelationMeta {
    field: string;
    kind: RelationKindString;
    typeFactory: () => Function;
}

function addRelation(target: object, propertyKey: string | symbol, kind: RelationKindString, typeFactory: () => Function): void {
    const ctor = (target as any).constructor;
    const existing: PendingRelationMeta[] =
        Reflect.getMetadata(IVERSON_RELATIONS, ctor) ?? [];
    existing.push({ field: String(propertyKey), kind, typeFactory });
    Reflect.defineMetadata(IVERSON_RELATIONS, existing, ctor);
}

/**
 * Each of these decorators stores the raw `typeFactory` rather than calling it
 * immediately. Property decorators run at class-definition time, and calling
 * the factory eagerly would dereference the related class before it exists
 * for any forward reference (including the two halves of a genuine mutual
 * reference, where reordering the classes cannot help either direction).
 * Resolution is deferred to `getRelations`, which runs at schema-registration
 * time after every class in the module has finished initializing.
 */
export function ManyToOne(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        addRelation(target, propertyKey, 'many_to_one', typeFactory);
    };
}

export function ManyToMany(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        addRelation(target, propertyKey, 'many_to_many', typeFactory);
    };
}

export function OneToMany(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        addRelation(target, propertyKey, 'one_to_many', typeFactory);
    };
}

export function OneToOne(typeFactory: () => Function): PropertyDecorator {
    return (target, propertyKey) => {
        addRelation(target, propertyKey, 'one_to_one', typeFactory);
    };
}

export function getRelations(target: Function): RelationMeta[] {
    const pending: PendingRelationMeta[] = Reflect.getMetadata(IVERSON_RELATIONS, target) ?? [];
    return pending.map(({ field, kind, typeFactory }) => ({
        field,
        kind,
        relatedType: typeFactory().name,
    }));
}

/**
 * Like `getRelations`, but returns the raw, unresolved `typeFactory` instead of collapsing it
 * to a name. `getRelations` is public API with production and test call sites that depend on
 * its name-only shape, so it is left untouched; the read path needs the actual related class
 * (not just its name) to construct and recursively hydrate a typed instance, and the only place
 * that can hand that out is here, since `IVERSON_RELATIONS` is module-private to this file.
 */
export function getRelationsWithFactory(target: Function): PendingRelationMeta[] {
    return Reflect.getMetadata(IVERSON_RELATIONS, target) ?? [];
}
