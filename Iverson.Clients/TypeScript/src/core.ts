/**
 * Core client classes: IversonClient, SchemaRegistrar, EntityCoordinator.
 */
import 'reflect-metadata';

import * as grpc from '@grpc/grpc-js';

import {
    ClrType,
    MappingDeleteRequest,
    ObjectMappingServiceClient,
    PropertyDescriptor,
    RelationDescriptor,
    RelationKind,
    SchemaRequest,
    SchemaResponse,
    TypeDescriptor,
} from '../generated/object_mapping.js';

import {
    ObjectPersistenceServiceClient,
    PersistRequest,
    PersistResponse,
} from '../generated/object_persistence.js';

import {
    ObjectRetrievalServiceClient,
    RetrievalManyRequest,
    RetrievalRequest,
    RetrievalResponse,
} from '../generated/object_retrieval.js';

import {
    getChunkFields,
    getEmbeddingFields,
    getKeyField,
    getLargeFields,
    getRelations,
    getSearchKeys,
    isIversonEntity,
    RelationKindString,
} from './annotations.js';

// ── Type helpers ──────────────────────────────────────────────────────────────

/** Convert a JS type name string to a ClrType enum value. */
function jsTypeToClr(typeName: string): ClrType {
    switch (typeName) {
        case 'String':   return ClrType.CLR_STRING;
        case 'Number':   return ClrType.CLR_FLOAT;
        case 'Boolean':  return ClrType.CLR_BOOL;
        case 'Date':     return ClrType.CLR_DATETIME;
        case 'Buffer':
        case 'Uint8Array': return ClrType.CLR_BYTES;
        default:         return ClrType.CLR_STRING;
    }
}

/** Convert camelCase or PascalCase to PascalCase. */
function toPascalCase(field: string): string {
    if (!field) return field;
    return field.charAt(0).toUpperCase() + field.slice(1);
}

/** Infer FK column name from relation metadata. */
function inferFk(kind: RelationKindString, relatedType: string, thisTypeName: string): string {
    switch (kind) {
        case 'many_to_one':
        case 'one_to_one':
            return `${relatedType}Id`;
        case 'many_to_many':
            return `${relatedType}Ids`;
        case 'one_to_many':
            return `${thisTypeName}Id`;
    }
}

const RELATION_KIND_MAP: Record<RelationKindString, RelationKind> = {
    one_to_one:   RelationKind.ONE_TO_ONE,
    one_to_many:  RelationKind.ONE_TO_MANY,
    many_to_one:  RelationKind.MANY_TO_ONE,
    many_to_many: RelationKind.MANY_TO_MANY,
};

// ── Promisify callback-style gRPC calls ───────────────────────────────────────

function callUnary<Req, Res>(
    method: (req: Req, cb: (err: grpc.ServiceError | null, res: Res) => void) => grpc.ClientUnaryCall,
    request: Req,
): Promise<Res> {
    return new Promise((resolve, reject) => {
        method(request, (err, res) => {
            if (err) reject(err);
            else resolve(res as Res);
        });
    });
}

// ── SchemaRegistrar ───────────────────────────────────────────────────────────

/**
 * Reflects on @IversonEntity classes and registers their schemas
 * with the server via ObjectMappingService.RegisterSchema.
 */
export class SchemaRegistrar {
    constructor(
        private readonly _mappingClient: ObjectMappingServiceClient,
        private readonly _entityClasses: Function[],
    ) {}

    /** Register all entity schemas. */
    async registerAll(traceId: string = ''): Promise<void> {
        for (const cls of this._entityClasses) {
            const request = this._buildRequest(cls, traceId);
            const response = await callUnary<SchemaRequest, SchemaResponse>(
                (req, cb) => this._mappingClient.registerSchema(req, cb),
                request,
            );
            if (!response.success) {
                throw new Error(
                    `Schema registration failed for ${cls.name}: ${response.error}`,
                );
            }
        }
    }

    _buildRequest(cls: Function, traceId: string = ''): SchemaRequest {
        if (!isIversonEntity(cls)) {
            throw new Error(`${cls.name} is not decorated with @IversonEntity()`);
        }

        const typeName = cls.name;
        const keyField = getKeyField(cls);
        const searchKeys = getSearchKeys(cls);
        const searchKeysByField = new Map(searchKeys.map(sk => [sk.field, sk.order]));
        const largeFields = new Set(getLargeFields(cls));
        const embeddingFields = new Set(getEmbeddingFields(cls));
        const chunkFieldsByName = new Map(getChunkFields(cls).map(c => [c.field, c]));
        const relations = getRelations(cls);
        const relationFields = new Set(relations.map(r => r.field));

        // Reflect on instance property types via design:type metadata
        // We use a temporary instance approach: instantiate to get prototype
        // then reflect on each property. design:type is set by TypeScript
        // when emitDecoratorMetadata=true.
        const proto = cls.prototype as Record<string, unknown>;
        const allFields = Object.getOwnPropertyNames(new (cls as any)());

        const properties: PropertyDescriptor[] = [];
        for (const fieldName of allFields) {
            if (relationFields.has(fieldName)) continue;

            // Reflect design:type from emitDecoratorMetadata
            const designType = Reflect.getMetadata('design:type', proto, fieldName) as Function | undefined;
            const clrType = designType ? jsTypeToClr(designType.name) : ClrType.CLR_STRING;

            const isKey = fieldName === keyField;
            const isSearchKey = searchKeysByField.has(fieldName);
            const isLargeField = largeFields.has(fieldName);
            const isEmbedding = embeddingFields.has(fieldName);
            const chunkMeta = chunkFieldsByName.get(fieldName);

            properties.push({
                name: toPascalCase(fieldName),
                clrType,
                isKey,
                isNullable: !isKey,
                isArray: false,
                isEmbedding,
                vectorDim: 0,
                modelId: '',
                isChunk: chunkMeta !== undefined,
                chunkMaxTokens: chunkMeta?.maxTokens ?? 0,
                chunkOverlap: chunkMeta?.overlap ?? 0,
                chunkModelId: '',
                chunkVectorDim: 0,
                isSearchKey,
                searchKeyOrder: searchKeysByField.get(fieldName) ?? 0,
                isLargeField,
            });
        }

        const relationDescriptors: RelationDescriptor[] = relations.map(rel => ({
            propertyName: toPascalCase(rel.field),
            kind: RELATION_KIND_MAP[rel.kind] ?? RelationKind.MANY_TO_ONE,
            relatedType: rel.relatedType,
            foreignKey: inferFk(rel.kind, rel.relatedType, typeName),
        }));

        const typeDescriptor: TypeDescriptor = {
            typeName,
            properties,
            relations: relationDescriptors,
        };

        return {
            rootType: typeDescriptor,
            dependents: [],
            traceId,
        };
    }
}

// ── Struct conversion helpers ─────────────────────────────────────────────────

function entityToPayload(entity: object): Record<string, unknown> {
    const payload: Record<string, unknown> = {};
    const allFields = Object.getOwnPropertyNames(entity);
    for (const field of allFields) {
        const value = (entity as Record<string, unknown>)[field];
        if (value === undefined) continue;
        const key = toPascalCase(field);
        if (value instanceof Date) {
            payload[key] = value.toISOString();
        } else {
            payload[key] = value;
        }
    }
    return payload;
}

function payloadToEntity<T extends object>(cls: new () => T, data: Record<string, unknown>): T {
    const instance = Object.create(cls.prototype) as Record<string, unknown>;
    const template = new cls();
    const allFields = Object.getOwnPropertyNames(template);
    for (const field of allFields) {
        const key = toPascalCase(field);
        if (key in data) {
            instance[field] = data[key];
        }
    }
    return instance as T;
}

// ── EntityCoordinator<T> ──────────────────────────────────────────────────────

/**
 * High-level coordinator for a single entity type.
 * Wraps ObjectMappingService, ObjectPersistenceService, and ObjectRetrievalService.
 */
export class EntityCoordinator<T extends object> {
    private readonly _typeName: string;
    private readonly _keyField: string | undefined;
    private readonly _mapping: ObjectMappingServiceClient;
    private readonly _persistence: ObjectPersistenceServiceClient;
    private readonly _retrieval: ObjectRetrievalServiceClient;

    constructor(
        private readonly _cls: new () => T,
        private readonly _client: IversonClient,
    ) {
        if (!isIversonEntity(_cls)) {
            throw new Error(`${_cls.name} is not decorated with @IversonEntity()`);
        }
        this._typeName = _cls.name;
        this._keyField = getKeyField(_cls);
        this._mapping = _client._mappingClient;
        this._persistence = _client._persistenceClient;
        this._retrieval = _client._retrievalClient;
    }

    private _getKey(entity: T): string {
        if (!this._keyField) {
            throw new Error(`No key field defined for ${this._typeName}`);
        }
        const value = (entity as Record<string, unknown>)[this._keyField];
        if (value === null || value === undefined) {
            throw new Error(`Key field '${this._keyField}' is null on entity`);
        }
        return String(value);
    }

    /** Persist a new entity. Returns the assigned key. */
    async persist(entity: T, traceId: string = ''): Promise<string> {
        const request: PersistRequest = {
            typeName: this._typeName,
            payload: entityToPayload(entity),
            traceId,
        };
        const response = await callUnary<PersistRequest, PersistResponse>(
            (req, cb) => this._persistence.post(req, cb),
            request,
        );
        if (!response.success) {
            throw new Error(`persist failed: ${response.error}`);
        }
        return response.key;
    }

    /** Update an existing entity. */
    async update(entity: T, traceId: string = ''): Promise<void> {
        const request: PersistRequest = {
            typeName: this._typeName,
            payload: entityToPayload(entity),
            traceId,
        };
        const response = await callUnary<PersistRequest, PersistResponse>(
            (req, cb) => this._persistence.update(req, cb),
            request,
        );
        if (!response.success) {
            throw new Error(`update failed: ${response.error}`);
        }
    }

    /** Delete an entity by key. */
    async delete(id: string, traceId: string = ''): Promise<void> {
        const request: MappingDeleteRequest = {
            typeName: this._typeName,
            key: id,
            traceId,
        };
        const response = await callUnary(
            (req: MappingDeleteRequest, cb: (err: grpc.ServiceError | null, res: any) => void) =>
                this._mapping.delete(req, cb),
            request,
        );
        if (!response.success) {
            throw new Error(`delete failed: ${response.error}`);
        }
    }

    /** Retrieve an entity by key. Returns null if not found. */
    async get(id: string, traceId: string = ''): Promise<T | null> {
        const request: RetrievalRequest = {
            typeName: this._typeName,
            key: id,
            traceId,
        };
        const response = await callUnary<RetrievalRequest, RetrievalResponse>(
            (req, cb) => this._retrieval.get(req, cb),
            request,
        );
        if (!response.found) return null;
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }

    /** Retrieve multiple entities by key. */
    async getMany(ids: string[], traceId: string = ''): Promise<T[]> {
        return new Promise((resolve, reject) => {
            const request: RetrievalManyRequest = {
                typeName: this._typeName,
                keys: ids,
                traceId,
            };
            const stream = this._retrieval.getMany(request);
            const results: T[] = [];
            stream.on('data', (response: RetrievalResponse) => {
                if (response.found) {
                    results.push(payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>));
                }
            });
            stream.on('error', reject);
            stream.on('end', () => resolve(results));
        });
    }
}

// ── IversonClient ─────────────────────────────────────────────────────────────

/**
 * Top-level client. Creates gRPC clients and exposes coordinators and registrar.
 */
export class IversonClient {
    readonly _mappingClient: ObjectMappingServiceClient;
    readonly _persistenceClient: ObjectPersistenceServiceClient;
    readonly _retrievalClient: ObjectRetrievalServiceClient;

    constructor(
        host: string = 'localhost',
        port: number = 5000,
        useTls: boolean = false,
    ) {
        const address = `${host}:${port}`;
        const credentials = useTls
            ? grpc.credentials.createSsl()
            : grpc.credentials.createInsecure();

        this._mappingClient = new ObjectMappingServiceClient(address, credentials);
        this._persistenceClient = new ObjectPersistenceServiceClient(address, credentials);
        this._retrievalClient = new ObjectRetrievalServiceClient(address, credentials);
    }

    /** Close all underlying gRPC clients. */
    close(): void {
        this._mappingClient.close();
        this._persistenceClient.close();
        this._retrievalClient.close();
    }

    /** Return an EntityCoordinator for the given entity class. */
    coordinator<T extends object>(entityClass: new () => T): EntityCoordinator<T> {
        return new EntityCoordinator(entityClass, this);
    }

    /** Return a SchemaRegistrar for the given entity classes. */
    registrar(...entityClasses: Function[]): SchemaRegistrar {
        return new SchemaRegistrar(this._mappingClient, entityClasses);
    }
}
