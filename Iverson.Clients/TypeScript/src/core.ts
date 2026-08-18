/**
 * Core client classes: IversonClient, SchemaRegistrar, EntityCoordinator.
 */
import 'reflect-metadata';

import * as grpc from '@grpc/grpc-js';

import {
    AuthorizationRules,
    ClrType,
    GetSchemaRequest,
    GetSchemaResponse,
    MappingDeleteRequest,
    MappingGetRequest,
    MappingResponse,
    MappingWriteRequest,
    ObjectMappingServiceClient,
    PropertyDescriptor,
    RelationDescriptor,
    RelationKind,
    SchemaRequest,
    SchemaResponse,
    SchemaType,
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
    AggregateRequest,
    AggregateResponse,
    ChunkSearchResponse,
    GroupByRequest,
    ObjectSearchServiceClient,
    PipelineRequest,
    SearchChunksRequest,
    SearchRequest,
    SearchResponse,
    SearchSimilarRequest,
} from '../generated/object_search.js';

import {
    getArrayFields,
    getGuidFields,
    getChunkFields,
    getEmbeddingFields,
    getExtractedFields,
    getKeyField,
    getKeywordsFields,
    getLargeFields,
    getMetadataFields,
    getPropertyDescriptions,
    getRelations,
    getRelationsWithFactory,
    getSearchKeys,
    getSummaryFields,
    getTenantFields,
    getTypeDescription,
    isIversonEntity,
    RelationKindString,
} from './annotations.js';

import { createActingUserMetadata } from './auth.js';

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

/**
 * Derive the navigation property name from the relation's member name.
 * For many_to_one / one_to_one the member itself is the foreign key
 * (authorId → AuthorId), so strip the trailing "Id" to get the navigation
 * property name (Author). For many_to_many the member itself is the foreign
 * key (regTagIds → RegTagIds), so strip the trailing "Ids" to get the
 * navigation property name (RegTags). Other kinds use the PascalCase member
 * name as-is.
 */
function relationPropertyName(kind: RelationKindString, field: string): string {
    const pascal = toPascalCase(field);
    if (kind === 'many_to_one' || kind === 'one_to_one') {
        if (pascal.length > 2 && pascal.endsWith('Id')) {
            return pascal.slice(0, -2);
        }
    }
    if (kind === 'many_to_many') {
        if (pascal.length > 3 && pascal.endsWith('Ids')) {
            return pascal.slice(0, -3) + 's';
        }
    }
    return pascal;
}

/**
 * Derive the JS member name a hydrated relation lands on (read path) — the same
 * suffix-strip-plus-plural derivation as `relationPropertyName`, but applied directly to the
 * camelCase declared field (no Pascal round trip), since this names an actual JS instance
 * property rather than a wire column. many_to_one/one_to_one strip the trailing "Id";
 * many_to_many strips "Ids" and appends "s" (so `tsTagIds` -> `tsTags` stays distinct from
 * `tsTagId` -> `tsTag`, the same-related-type collision the conformance model exercises).
 * one_to_many has no derived name: its declared member is already the navigation member.
 */
function relationNavMember(kind: RelationKindString, field: string): string {
    if ((kind === 'many_to_one' || kind === 'one_to_one') && field.length > 2 && field.endsWith('Id')) {
        return field.slice(0, -2);
    }
    if (kind === 'many_to_many' && field.length > 3 && field.endsWith('Ids')) {
        return field.slice(0, -3) + 's';
    }
    return field;
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

// ── Acting-user token ─────────────────────────────────────────────────────────

/** A pre-minted acting-user token, or a function that resolves one (e.g. from a token cache). */
export type ActingUserToken = string | (() => Promise<string>);

// ── Search results ─────────────────────────────────────────────────────────────

/** A single search-family result row: the converted entity plus its relevance score. */
export interface SearchResult<T> {
    entity: T;
    score: number;
}

/** Resolve an optional acting-user token (awaiting it if it's a function) into call metadata. */
async function resolveActingUserMetadata(actingUserToken?: ActingUserToken): Promise<grpc.Metadata> {
    if (actingUserToken === undefined) return new grpc.Metadata();
    const token = typeof actingUserToken === 'function' ? await actingUserToken() : actingUserToken;
    return createActingUserMetadata(token);
}

// ── Promisify callback-style and streaming gRPC calls ─────────────────────────

async function callUnary<Req, Res>(
    method: (
        req: Req,
        metadata: grpc.Metadata,
        options: Partial<grpc.CallOptions>,
        cb: (err: grpc.ServiceError | null, res: Res) => void,
    ) => grpc.ClientUnaryCall,
    request: Req,
    callCredentials?: grpc.CallCredentials,
    actingUserToken?: ActingUserToken,
): Promise<Res> {
    const metadata = await resolveActingUserMetadata(actingUserToken);
    return new Promise((resolve, reject) => {
        const options: Partial<grpc.CallOptions> = callCredentials ? { credentials: callCredentials } : {};
        method(request, metadata, options, (err, res) => {
            if (err) reject(err);
            else resolve(res as Res);
        });
    });
}

/** Open a server-streaming gRPC call, resolving the acting-user token into metadata first. */
async function openStream<Req, Res>(
    method: (req: Req, metadata: grpc.Metadata, options: Partial<grpc.CallOptions>) => grpc.ClientReadableStream<Res>,
    request: Req,
    callCredentials?: grpc.CallCredentials,
    actingUserToken?: ActingUserToken,
): Promise<grpc.ClientReadableStream<Res>> {
    const metadata = await resolveActingUserMetadata(actingUserToken);
    const options: Partial<grpc.CallOptions> = callCredentials ? { credentials: callCredentials } : {};
    return method(request, metadata, options);
}

/** Collect every row of a server-streaming gRPC response into an array, applying `map` to each. */
function collectStream<Res, T>(stream: grpc.ClientReadableStream<Res>, map: (row: Res) => T): Promise<T[]> {
    return new Promise((resolve, reject) => {
        const results: T[] = [];
        stream.on('data', (row: Res) => results.push(map(row)));
        stream.on('error', reject);
        stream.on('end', () => resolve(results));
    });
}

// ── Entity reflection ──────────────────────────────────────────────────────────

/**
 * Reflects on an @IversonEntity class and builds its TypeDescriptor: properties
 * (with key/search-key/large-field/embedding/chunk metadata) and relations.
 * Shared by SchemaRegistrar._buildRequest and any other caller that needs a
 * class's registered shape without building a full SchemaRequest.
 */
export function describeEntity(cls: Function): TypeDescriptor {
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
    const metadataFields = new Set(getMetadataFields(cls));
    const summaryFields = new Set(getSummaryFields(cls));
    const keywordsFields = new Set(getKeywordsFields(cls));
    const extractedByField = new Map(getExtractedFields(cls).map(e => [e.field, e]));
    const arrayFields = getArrayFields(cls);
    const guidFields = getGuidFields(cls);
    if (keyField !== undefined) {
        // The server builds every per-property declaration from non-key properties only, so
        // anything but a description on the key is accepted and silently dropped.
        const rejected: string[] = [];
        if (searchKeysByField.has(keyField)) rejected.push('@IversonSearchKey()');
        if (largeFields.has(keyField)) rejected.push('@IversonLargeField()');
        if (embeddingFields.has(keyField)) rejected.push('@IversonEmbedding()');
        if (chunkFieldsByName.has(keyField)) rejected.push('@IversonChunk()');
        if (metadataFields.has(keyField)) rejected.push('@IversonMetadata()');
        if (summaryFields.has(keyField)) rejected.push('@IversonSummary()');
        if (keywordsFields.has(keyField)) rejected.push('@IversonKeywords()');
        if (extractedByField.has(keyField)) rejected.push('@IversonExtracted()');

        if (rejected.length > 0) {
            throw new Error(
                `${typeName}.${keyField} is the primary key and also declares ` +
                `${rejected.join(', ')}; the server builds every per-property declaration ` +
                'from non-key properties only, so this would be accepted and silently discarded. ' +
                'Remove it from the key field. (Only a description is valid on a key.)',
            );
        }
    }

    const propertyDescriptions = getPropertyDescriptions(cls);
    const relations = getRelations(cls);
    const relationFields = new Set(relations.map(r => r.field));

    for (const rel of relations) {
        if (rel.kind !== 'many_to_one' && rel.kind !== 'one_to_one') continue;
        const required = `${rel.relatedType}Id`;
        if (toPascalCase(rel.field) !== required) {
            throw new Error(
                `${typeName}.${rel.field} is a ${rel.kind} relation to ${rel.relatedType}; ` +
                `its member name PascalCases to '${toPascalCase(rel.field)}' but must PascalCase to ` +
                `'${required}'. Rename ${rel.field} to '${required.charAt(0).toLowerCase()}${required.slice(1)}'.`,
            );
        }
    }

    // Reflect on instance property types via design:type metadata
    // We use a temporary instance approach: instantiate to get prototype
    // then reflect on each property. design:type is set by TypeScript
    // when emitDecoratorMetadata=true.
    const proto = cls.prototype as Record<string, unknown>;
    const instance = new (cls as any)();
    const allFields = Object.getOwnPropertyNames(instance);

    const properties: PropertyDescriptor[] = [];
    for (const fieldName of allFields) {
        if (relationFields.has(fieldName)) continue;

        // Reflect design:type from emitDecoratorMetadata
        const designType = Reflect.getMetadata('design:type', proto, fieldName) as Function | undefined;
        const arrayElement = arrayFields.get(fieldName);
        const looksArray = designType === Array || Array.isArray(instance[fieldName]);
        if (looksArray && arrayElement === undefined) {
            throw new Error(
                `${typeName}.${fieldName} is an array property but has no @IversonArray(elementType) ` +
                'decorator; TypeScript erases the element type, so it cannot be inferred. ' +
                'Add @IversonArray(ClrType.CLR_…) naming the element type.',
            );
        }
        if (guidFields.has(fieldName)) {
            if (looksArray) {
                throw new Error(
                    `${typeName}.${fieldName} is decorated with @IversonGuid() but is an array property; ` +
                    '@IversonGuid() is scalar-only. Use @IversonArray(ClrType.CLR_GUID) to declare a UUID array column.',
                );
            }
            // design:type is only populated when the consumer's build emits decorator metadata
            // (tsc with emitDecoratorMetadata); under esbuild-based test tooling it is undefined,
            // so fall back to the runtime type of the field's own initializer, mirroring the
            // Array.isArray(...) fallback `looksArray` already uses above for the same reason.
            const runtimeType = typeof instance[fieldName];
            // When design:type is unavailable and the field has no initializer (runtimeType
            // 'undefined'), the underlying type is genuinely unknown at this point — accept
            // rather than false-reject. Every case this check needs to catch (a number
            // initializer, an array, etc.) has an observable non-string runtime value.
            const isStringType = designType !== undefined
                ? designType === String
                : (runtimeType === 'string' || runtimeType === 'undefined');
            if (!isStringType) {
                const observed = designType?.name ?? runtimeType;
                throw new Error(
                    `${typeName}.${fieldName} is decorated with @IversonGuid() but its type is ` +
                    `${observed}, not string; @IversonGuid() only applies to string-typed properties, ` +
                    'since a GUID is carried as a string. Remove @IversonGuid() or change the property to a string.',
                );
            }
        }

        const isArray = arrayElement !== undefined;
        const clrType = arrayElement
            ?? (guidFields.has(fieldName)
                ? ClrType.CLR_GUID
                : (designType ? jsTypeToClr(designType.name) : ClrType.CLR_STRING));

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
            isArray,
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
            isMetadata: metadataFields.has(fieldName),
            description: propertyDescriptions[fieldName] ?? '',
            isSummaryTarget: summaryFields.has(fieldName),
            isKeywordsTarget: keywordsFields.has(fieldName),
            extractHint: extractedByField.get(fieldName)?.hint ?? '',
            chunkContextual: chunkMeta?.contextual ?? false,
        });
    }

    for (const rel of relations) {
        if (rel.kind === 'one_to_many') continue;
        properties.push({
            name: inferFk(rel.kind, rel.relatedType, typeName),
            clrType: ClrType.CLR_GUID,
            isKey: false,
            isNullable: true,
            isArray: rel.kind === 'many_to_many',
            isEmbedding: false,
            vectorDim: 0,
            modelId: '',
            isChunk: false,
            chunkMaxTokens: 0,
            chunkOverlap: 0,
            chunkModelId: '',
            chunkVectorDim: 0,
            isSearchKey: false,
            searchKeyOrder: 0,
            isLargeField: false,
            isMetadata: false,
            description: '',
            isSummaryTarget: false,
            isKeywordsTarget: false,
            extractHint: '',
            chunkContextual: false,
        });
    }

    const relationDescriptors: RelationDescriptor[] = relations.map(rel => ({
        propertyName: relationPropertyName(rel.kind, rel.field),
        kind: RELATION_KIND_MAP[rel.kind] ?? RelationKind.MANY_TO_ONE,
        relatedType: rel.relatedType,
        foreignKey: inferFk(rel.kind, rel.relatedType, typeName),
    }));

    const tenantFields = getTenantFields(cls);
    if (tenantFields.length === 0) {
        throw new Error(
            `${typeName} has no property decorated with @IversonTenant(); schema registration ` +
            'requires exactly one tenant field and the server rejects a request missing it.',
        );
    }
    if (tenantFields.length > 1) {
        throw new Error(
            `${typeName} has multiple properties decorated with @IversonTenant() ` +
            `(${tenantFields.join(', ')}); schema registration requires exactly one tenant field.`,
        );
    }

    return {
        typeName,
        properties,
        relations: relationDescriptors,
        authorization: undefined,
        tenantField: toPascalCase(tenantFields[0]),
        description: getTypeDescription(cls),
    };
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
        private readonly _callCredentials?: grpc.CallCredentials,
    ) {}

    /** Register all entity schemas, optionally attaching per-type authorization rules. */
    async registerAll(
        traceId: string = '',
        authorizationByTypeName?: Record<string, AuthorizationRules>,
    ): Promise<void> {
        for (const cls of this._entityClasses) {
            const request = this._buildRequest(cls, traceId, authorizationByTypeName);
            const response = await callUnary<SchemaRequest, SchemaResponse>(
                (req, metadata, options, cb) => this._mappingClient.registerSchema(req, metadata, options, cb),
                request,
                this._callCredentials,
            );
            if (!response.success) {
                throw new Error(
                    `Schema registration failed for ${cls.name}: ${response.error}`,
                );
            }
        }
    }

    _buildRequest(
        cls: Function,
        traceId: string = '',
        authorizationByTypeName?: Record<string, AuthorizationRules>,
    ): SchemaRequest {
        const rootType = describeEntity(cls);
        const rules = authorizationByTypeName?.[rootType.typeName];
        if (rules !== undefined) rootType.authorization = rules;
        return { rootType, dependents: [], traceId };
    }
}

// ── Struct conversion helpers ─────────────────────────────────────────────────

function entityToPayload(entity: object, cls: Function): Record<string, unknown> {
    const payload: Record<string, unknown> = {};
    const typeName = cls.name;
    const relations = getRelations(cls);
    const relationByField = new Map(relations.map(r => [r.field, r] as const));
    // Members the read path may have hydrated (payloadToEntity) onto a derived navigation name
    // must never be written back — entityToPayload walks the live instance's own property names,
    // so a hydrated `tsAuthor` would otherwise round-trip as `TsAuthor`, violating the FK-only
    // write contract (A23). one_to_many has no derived name; it's excluded below via its kind.
    // Only exclude when a suffix was actually stripped — a many_to_many member that doesn't end
    // in "Ids" (legal: describeEntity only validates many_to_one/one_to_one naming) derives an
    // unchanged navMember === field. Excluding that would drop the member itself from the
    // payload, silently discarding its foreign keys.
    const excludedNavMembers = new Set<string>();
    for (const r of relations) {
        if (r.kind === 'one_to_many') continue;
        const navMember = relationNavMember(r.kind, r.field);
        if (navMember !== r.field) excludedNavMembers.add(navMember);
    }
    const allFields = Object.getOwnPropertyNames(entity);
    for (const field of allFields) {
        if (excludedNavMembers.has(field)) continue;
        const value = (entity as Record<string, unknown>)[field];
        if (value === undefined) continue;
        const rel = relationByField.get(field);
        if (rel?.kind === 'one_to_many') continue;
        const key = rel !== undefined ? inferFk(rel.kind, rel.relatedType, typeName) : toPascalCase(field);
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
    const typeName = (cls as unknown as Function).name;
    const relations = getRelations(cls as unknown as Function);
    const relationByField = new Map(relations.map(r => [r.field, r] as const));
    const allFields = Object.getOwnPropertyNames(template);
    for (const field of allFields) {
        const rel = relationByField.get(field);
        const key = rel?.kind === 'many_to_many'
            ? inferFk(rel.kind, rel.relatedType, typeName)
            : toPascalCase(field);
        if (key in data) {
            instance[field] = data[key];
        }
    }

    // Hydrate typed relation children for a depth-resolved read. Runs after the scalar pass
    // above, which already assigned the raw FK scalar/id-list under its declared field; a
    // hydrated relation lands on a distinct derived navigation member (many_to_one, one_to_one,
    // many_to_many) or overwrites the declared member in place (one_to_many, which has no
    // derived name). The wire key for the nested value is the schema's navigation property
    // name (relationPropertyName), a separate wire column from the FK scalar/id-list.
    for (const rel of getRelationsWithFactory(cls as unknown as Function)) {
        if (rel.kind === 'one_to_many') {
            const wireKey = relationPropertyName(rel.kind, rel.field);
            const raw = data[wireKey];
            if (!Array.isArray(raw)) continue;
            const relatedCls = rel.typeFactory() as new () => object;
            instance[rel.field] = raw.map(item => payloadToEntity(relatedCls, item as Record<string, unknown>));
            continue;
        }

        const navMember = relationNavMember(rel.kind, rel.field);
        // Only a genuine derivation (navMember !== rel.field) can collide with a declared field
        // by definition — when no suffix was stripped, navMember equals the relation's own
        // declared field, which is trivially "already declared" and must not raise here.
        if (navMember !== rel.field && navMember in template) {
            throw new Error(
                `${typeName}: relation '${rel.field}' would hydrate into member '${navMember}', ` +
                `but '${navMember}' is already a declared field on ${typeName}. Rename one of them.`,
            );
        }

        const wireKey = relationPropertyName(rel.kind, rel.field);
        const raw = data[wireKey];
        if (raw === undefined || raw === null) continue;
        const relatedCls = rel.typeFactory() as new () => object;

        if (rel.kind === 'many_to_many') {
            if (!Array.isArray(raw)) continue;
            instance[navMember] = raw.map(item => payloadToEntity(relatedCls, item as Record<string, unknown>));
        } else {
            if (typeof raw !== 'object' || Array.isArray(raw)) continue;
            instance[navMember] = payloadToEntity(relatedCls, raw as Record<string, unknown>);
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
    private _boundActingUser?: ActingUserToken;

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

    /**
     * Returns a coordinator bound to `token`, leaving this one untouched. The bound identity
     * outranks the client's ambient one; an explicit per-call token still outranks both.
     */
    withActingUser(token: ActingUserToken): EntityCoordinator<T> {
        const bound = new EntityCoordinator(this._cls, this._client);
        bound._boundActingUser = token;
        return bound;
    }

    private _identity(): ActingUserToken | undefined {
        return this._boundActingUser ?? this._client._actingUserToken;
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
            payload: entityToPayload(entity, this._cls),
            traceId,
        };
        const response = await callUnary<PersistRequest, PersistResponse>(
            (req, metadata, options, cb) => this._persistence.post(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._identity(),
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
            payload: entityToPayload(entity, this._cls),
            traceId,
        };
        const response = await callUnary<PersistRequest, PersistResponse>(
            (req, metadata, options, cb) => this._persistence.update(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._identity(),
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
            (
                req: MappingDeleteRequest,
                metadata: grpc.Metadata,
                options: Partial<grpc.CallOptions>,
                cb: (err: grpc.ServiceError | null, res: any) => void,
            ) => this._mapping.delete(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._identity(),
        );
        if (!response.success) {
            throw new Error(`delete failed: ${response.error}`);
        }
    }

    /** Retrieve an entity by key with server-side relation resolution to `depth`. */
    async getMapped(id: string, depth: number = 1, traceId: string = ''): Promise<T | null> {
        const request: MappingGetRequest = {
            typeName: this._typeName,
            key: id,
            depth,
            traceId,
        };
        const response = await callUnary<MappingGetRequest, MappingResponse>(
            (req, metadata, options, cb) => this._mapping.get(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._identity(),
        );
        if (!response.success) return null;
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }

    /**
     * Create an entity through the mapping path, which resolves its relations server-side.
     * Resolves to the entity hydrated from the response, carrying the server-assigned key —
     * the caller never assigns one.
     */
    async postMapped(entity: T, traceId: string = ''): Promise<T> {
        const request: MappingWriteRequest = {
            typeName: this._typeName,
            payload: entityToPayload(entity, this._cls),
            traceId,
        };
        const response = await callUnary<MappingWriteRequest, MappingResponse>(
            (req, metadata, options, cb) => this._mapping.post(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._identity(),
        );
        if (!response.success) {
            throw new Error(`postMapped failed: ${response.error}`);
        }
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }

    /** Update an existing entity through the mapping path. */
    async updateMapped(entity: T, traceId: string = ''): Promise<T> {
        const request: MappingWriteRequest = {
            typeName: this._typeName,
            payload: entityToPayload(entity, this._cls),
            traceId,
        };
        const response = await callUnary<MappingWriteRequest, MappingResponse>(
            (req, metadata, options, cb) => this._mapping.update(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._identity(),
        );
        if (!response.success) {
            throw new Error(`updateMapped failed: ${response.error}`);
        }
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }

    /** Retrieve an entity by key. Returns null if not found. */
    async get(id: string, traceId: string = ''): Promise<T | null> {
        const request: RetrievalRequest = {
            typeName: this._typeName,
            key: id,
            traceId,
        };
        const response = await callUnary<RetrievalRequest, RetrievalResponse>(
            (req, metadata, options, cb) => this._retrieval.get(req, metadata, options, cb),
            request,
            this._client._callCredentials,
            this._identity(),
        );
        if (!response.found) return null;
        return payloadToEntity(this._cls, (response.data ?? {}) as Record<string, unknown>);
    }

    /** Retrieve multiple entities by key. */
    async getMany(ids: string[], traceId: string = ''): Promise<T[]> {
        const request: RetrievalManyRequest = {
            typeName: this._typeName,
            keys: ids,
            traceId,
        };
        const stream = await openStream<RetrievalManyRequest, RetrievalResponse>(
            (req, metadata, options) => this._retrieval.getMany(req, metadata, options),
            request,
            this._client._callCredentials,
            this._identity(),
        );
        return new Promise((resolve, reject) => {
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
    readonly _searchClient: ObjectSearchServiceClient;
    readonly _callCredentials?: grpc.CallCredentials;
    readonly _actingUserToken?: ActingUserToken;

    constructor(
        host: string = 'localhost',
        port: number = 5000,
        useTls: boolean = false,
        callCredentials?: grpc.CallCredentials,
        actingUserToken?: ActingUserToken,
    ) {
        const address = `${host}:${port}`;
        const credentials = useTls
            ? grpc.credentials.createSsl()
            : grpc.credentials.createInsecure();

        this._mappingClient = new ObjectMappingServiceClient(address, credentials);
        this._persistenceClient = new ObjectPersistenceServiceClient(address, credentials);
        this._retrievalClient = new ObjectRetrievalServiceClient(address, credentials);
        this._searchClient = new ObjectSearchServiceClient(address, credentials);
        this._callCredentials = callCredentials;
        this._actingUserToken = actingUserToken;
    }

    /** Close all underlying gRPC clients. */
    close(): void {
        this._mappingClient.close();
        this._persistenceClient.close();
        this._retrievalClient.close();
        this._searchClient.close();
    }

    /** Return an EntityCoordinator for the given entity class. */
    coordinator<T extends object>(entityClass: new () => T): EntityCoordinator<T> {
        return new EntityCoordinator(entityClass, this);
    }

    /** Return a SchemaRegistrar for the given entity classes. */
    registrar(...entityClasses: Function[]): SchemaRegistrar {
        return new SchemaRegistrar(this._mappingClient, entityClasses, this._callCredentials);
    }

    /** Fetch the tenant's authorized schema catalog. */
    async getSchema(traceId = ''): Promise<SchemaType[]> {
        const response = await callUnary<GetSchemaRequest, GetSchemaResponse>(
            (req, metadata, options, cb) => this._mappingClient.getSchema(req, metadata, options, cb),
            { traceId },
            this._callCredentials,
            this._actingUserToken,
        );
        return response.types;
    }

    // ── Search-family execution ──────────────────────────────────────────────

    /** Execute a Search request. Rows are genuinely `T`-shaped, so each is converted via payloadToEntity;
     * each result also carries the row's relevance score. */
    async search<T extends object>(request: SearchRequest, cls: new () => T): Promise<SearchResult<T>[]> {
        return this._collectSearchStream(
            (req, metadata, options) => this._searchClient.search(req, metadata, options),
            request,
            (row) => ({ entity: payloadToEntity(cls, (row.data ?? {}) as Record<string, unknown>), score: row.score }),
        );
    }

    /** Execute a SearchSimilar (vector) request. Rows are genuinely `T`-shaped, so each is converted via
     * payloadToEntity; each result also carries the row's relevance score. */
    async searchSimilar<T extends object>(request: SearchSimilarRequest, cls: new () => T): Promise<SearchResult<T>[]> {
        return this._collectSearchStream(
            (req, metadata, options) => this._searchClient.searchSimilar(req, metadata, options),
            request,
            (row) => ({ entity: payloadToEntity(cls, (row.data ?? {}) as Record<string, unknown>), score: row.score }),
        );
    }

    /** Execute a SearchChunks request. Returns the flat chunk messages as-is. */
    async searchChunks(request: SearchChunksRequest): Promise<ChunkSearchResponse[]> {
        const stream = await openStream<SearchChunksRequest, ChunkSearchResponse>(
            (req, metadata, options) => this._searchClient.searchChunks(req, metadata, options),
            request,
            this._callCredentials,
            this._actingUserToken,
        );
        return collectStream(stream, (row) => row);
    }

    /** Execute a GroupBy request. Columns are aggregated/aliased and don't match any entity's own
     * fields, so each row is returned as a plain record instead of being converted via payloadToEntity. */
    async groupBy(request: GroupByRequest): Promise<Record<string, unknown>[]> {
        return this._collectSearchStream<GroupByRequest, Record<string, unknown>>(
            (req, metadata, options) => this._searchClient.groupBy(req, metadata, options),
            request,
            (row) => (row.data ?? {}) as Record<string, unknown>,
        );
    }

    /** Execute an Aggregate request. Single unary call; returns the AggregateResponse as-is. */
    async aggregate(request: AggregateRequest): Promise<AggregateResponse> {
        return callUnary<AggregateRequest, AggregateResponse>(
            (req, metadata, options, cb) => this._searchClient.aggregate(req, metadata, options, cb),
            request,
            this._callCredentials,
            this._actingUserToken,
        );
    }

    /** Execute a Pipeline request. Columns are derived/aliased and don't match any entity's own
     * fields, so each row is returned as a plain record instead of being converted via payloadToEntity. */
    async pipeline(request: PipelineRequest): Promise<Record<string, unknown>[]> {
        return this._collectSearchStream<PipelineRequest, Record<string, unknown>>(
            (req, metadata, options) => this._searchClient.pipeline(req, metadata, options),
            request,
            (row) => (row.data ?? {}) as Record<string, unknown>,
        );
    }

    /**
     * Shared streaming path for the search-family RPCs (Search/SearchSimilar/GroupBy/Pipeline, all
     * of which respond with SearchResponse): opens the stream with the acting-user token resolved
     * into metadata, then applies the caller-supplied `map` to each response row. Search/SearchSimilar
     * map to a `SearchResult<T>` (entity converted via payloadToEntity, plus the row's score); GroupBy/
     * Pipeline map to a plain record, since aggregated/derived columns don't correspond to any entity's
     * own fields or carry a meaningful per-row score.
     */
    private async _collectSearchStream<Req, T>(
        method: (
            req: Req,
            metadata: grpc.Metadata,
            options: Partial<grpc.CallOptions>,
        ) => grpc.ClientReadableStream<SearchResponse>,
        request: Req,
        map: (row: SearchResponse) => T,
    ): Promise<T[]> {
        const stream = await openStream(method, request, this._callCredentials, this._actingUserToken);
        return collectStream(stream, map);
    }
}
