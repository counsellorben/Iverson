/**
 * Tests for IversonClient's execution-level behavior:
 *  - acting-user token threading through EntityCoordinator's CRUD calls (persist/getMany)
 *  - the 6 search-family execution methods (search/searchSimilar/searchChunks/groupBy/aggregate/pipeline)
 *  - non-breaking constructor shape
 */
import 'reflect-metadata';
import { EventEmitter } from 'node:events';
import * as grpc from '@grpc/grpc-js';
import { describe, expect, it, vi } from 'vitest';

import {
    IversonChunk,
    IversonDescription,
    IversonEmbedding,
    IversonEmbeddingModel,
    IversonEntity,
    IversonExtracted,
    IversonKey,
    IversonKeywords,
    IversonLargeField,
    IversonMetadata,
    IversonSearchKey,
    IversonSummary,
    ManyToMany,
    ManyToOne,
    OneToMany,
    OneToOne,
} from '../src/annotations.js';
import { ACTING_USER_METADATA_KEY } from '../src/auth.js';
import { describeEntity, EntityCoordinator, IversonClient } from '../src/core.js';

import {
    MappingGetRequest,
    MappingResponse,
    MappingWriteRequest,
} from '../generated/object_mapping.js';
import {
    ObjectPersistenceServiceClient,
    PersistRequest,
    PersistResponse,
} from '../generated/object_persistence.js';
import {
    ObjectRetrievalServiceClient,
    RetrievalManyRequest,
    RetrievalResponse,
} from '../generated/object_retrieval.js';
import {
    AggregateRequest,
    AggregateResponse,
    ChunkSearchResponse,
    GroupByRequest,
    PipelineRequest,
    SearchChunksRequest,
    SearchLogic,
    SearchRequest,
    SearchResponse,
    SearchSimilarRequest,
} from '../generated/object_search.js';

// ── Test entities ─────────────────────────────────────────────────────────────

@IversonEntity()
class TestEntity {
    @IversonKey()
    id: string = '';
    name: string = '';
}

class SearchArticle {
    id: string = '';
    title: string = '';
    wordCount: number = 0;
}

// ── Stub helpers ──────────────────────────────────────────────────────────────

interface CapturedCall<Req> {
    req: Req;
    metadata: grpc.Metadata;
    options: unknown;
}

/** Fakes a unary gRPC method: `(req, metadata, options, cb) => ClientUnaryCall`. */
function makeUnaryStub<Req, Res>(response: Res) {
    const calls: CapturedCall<Req>[] = [];
    const fn = vi.fn(
        (req: Req, metadata: grpc.Metadata, options: unknown, cb: (err: null, res: Res) => void) => {
            calls.push({ req, metadata, options });
            cb(null, response);
            return {} as grpc.ClientUnaryCall;
        },
    );
    return { fn, calls };
}

/** Fakes a server-streaming gRPC method: `(req, metadata, options) => ClientReadableStream`. */
function makeStreamStub<Req, Res>(rows: Res[]) {
    const calls: CapturedCall<Req>[] = [];
    const fn = vi.fn((req: Req, metadata: grpc.Metadata, options: unknown) => {
        calls.push({ req, metadata, options });
        const stream = new EventEmitter();
        // setImmediate (a macrotask), not queueMicrotask: the caller attaches its 'data'/'end'
        // listeners after an `await` inside openStream(), so emitting on the microtask queue
        // would race ahead of listener attachment and the events would be silently dropped.
        setImmediate(() => {
            for (const row of rows) stream.emit('data', row);
            stream.emit('end');
        });
        return stream as unknown as grpc.ClientReadableStream<Res>;
    });
    return { fn, calls };
}

function makeClientLike(overrides: Record<string, unknown>): IversonClient {
    return {
        _mappingClient: {},
        _persistenceClient: {},
        _retrievalClient: {},
        ...overrides,
    } as unknown as IversonClient;
}

// ── IversonClient construction ──────────────────────────────────────────────

describe('IversonClient — construction', () => {
    it('supports the existing 2-positional-arg call shape (non-breaking)', () => {
        const client = new IversonClient('localhost', 5000);
        expect(client).toBeInstanceOf(IversonClient);
        client.close();
    });

    it('stores an optional 5th actingUserToken argument', () => {
        const client = new IversonClient('localhost', 5000, false, undefined, 'tok-static');
        expect((client as unknown as { _actingUserToken: unknown })._actingUserToken).toBe('tok-static');
        client.close();
    });
});

// ── EntityCoordinator — acting-user token threading ─────────────────────────

describe('EntityCoordinator — acting-user token threading', () => {
    it('persist() attaches a static acting-user token to call metadata', async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn }, _actingUserToken: 'tok-1' });
        const coordinator = new EntityCoordinator(TestEntity, client);

        await coordinator.persist(new TestEntity());

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer tok-1']);
    });

    it('persist() resolves a function-valued acting-user token', async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({
            _persistenceClient: { post: fn },
            _actingUserToken: async () => 'tok-fn',
        });
        const coordinator = new EntityCoordinator(TestEntity, client);

        await coordinator.persist(new TestEntity());

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer tok-fn']);
    });

    it('persist() sends metadata with no acting-user header when unconfigured', async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn } });
        const coordinator = new EntityCoordinator(TestEntity, client);

        await coordinator.persist(new TestEntity());

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual([]);
    });

    it('getMany() attaches the acting-user token to the streaming call metadata', async () => {
        const { fn, calls } = makeStreamStub<RetrievalManyRequest, RetrievalResponse>([]);
        const client = makeClientLike({ _retrievalClient: { getMany: fn }, _actingUserToken: 'tok-2' });
        const coordinator = new EntityCoordinator(TestEntity, client);

        await coordinator.getMany(['1']);

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer tok-2']);
    });
});

// ── EntityCoordinator — acting-user identity resolution (per-call → bound → ambient → none) ──

describe('EntityCoordinator — acting-user identity resolution', () => {
    it("withActingUser() binds an identity that wins over the client's ambient one", async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn }, _actingUserToken: 'ambient' });
        const coordinator = new EntityCoordinator(TestEntity, client).withActingUser('bound');

        await coordinator.persist(new TestEntity());

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer bound']);
    });

    it("the client's ambient identity applies when nothing is bound", async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn }, _actingUserToken: 'ambient' });
        const coordinator = new EntityCoordinator(TestEntity, client);

        await coordinator.persist(new TestEntity());

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer ambient']);
    });

    it('no identity anywhere emits no acting-user header', async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn } });
        const coordinator = new EntityCoordinator(TestEntity, client);

        await coordinator.persist(new TestEntity());

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual([]);
    });

    it('withActingUser() does not mutate the receiver', async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn }, _actingUserToken: 'ambient' });
        const coordinator = new EntityCoordinator(TestEntity, client);

        coordinator.withActingUser('bound');
        await coordinator.persist(new TestEntity());

        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer ambient']);
    });
});

// ── EntityCoordinator — mapped CRUD ──────────────────────────────────────────

describe('EntityCoordinator — mapped CRUD', () => {
    it('getMapped() passes depth through', async () => {
        const { fn, calls } = makeUnaryStub<MappingGetRequest, MappingResponse>({
            success: true, data: { Id: '1', Name: 'n' }, error: '', traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { get: fn } });
        const coordinator = new EntityCoordinator(TestEntity, client);

        await coordinator.getMapped('1', 2);

        expect((calls[0].req as MappingGetRequest).depth).toBe(2);
    });

    it('postMapped() returns an entity hydrated from Data', async () => {
        const { fn } = makeUnaryStub<MappingWriteRequest, MappingResponse>({
            success: true, data: { Id: 'server-assigned-id', Name: 'n' }, error: '', traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { post: fn } });
        const coordinator = new EntityCoordinator(TestEntity, client);

        const entity = new TestEntity();
        const result = await coordinator.postMapped(entity);

        expect(result.id).toBe('server-assigned-id');
    });

    it('updateMapped() sends the key it was given', async () => {
        const { fn, calls } = makeUnaryStub<MappingWriteRequest, MappingResponse>({
            success: true, data: { Id: 'k1', Name: 'n' }, error: '', traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { update: fn } });
        const coordinator = new EntityCoordinator(TestEntity, client);

        const entity = new TestEntity();
        entity.id = 'k1';
        await coordinator.updateMapped(entity);

        expect((calls[0].req as MappingWriteRequest).payload!.Id).toBe('k1');
    });
});

// ── IversonClient — search-family execution methods ─────────────────────────

describe('IversonClient — search-family execution methods', () => {
    it('search() converts each row into a T instance via the shared Struct-conversion path and preserves score', async () => {
        const rows: SearchResponse[] = [
            { data: { Id: '1', Title: 'A', WordCount: 10 }, score: 0.75, traceId: '' },
            { data: { Id: '2', Title: 'B', WordCount: 20 }, score: 0, traceId: '' },
        ];
        const { fn, calls } = makeStreamStub<SearchRequest, SearchResponse>(rows);
        const client = new IversonClient('localhost', 0);
        (client as unknown as { _searchClient: unknown })._searchClient = { search: fn, close: vi.fn() };
        (client as unknown as { _actingUserToken: unknown })._actingUserToken = 'tok';

        const req: SearchRequest = {
            typeName: 'SearchArticle', query: undefined, page: 0, pageSize: 20, traceId: '', fields: [], joins: [],
        };
        const results = await client.search(req, SearchArticle);

        expect(results).toHaveLength(2);
        expect(results[0].entity).toBeInstanceOf(SearchArticle);
        expect(results[0].entity).toMatchObject({ id: '1', title: 'A', wordCount: 10 });
        expect(results[0].score).toBe(0.75);
        expect(results[1].entity).toMatchObject({ id: '2', title: 'B', wordCount: 20 });
        expect(results[1].score).toBe(0);
        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer tok']);

        client.close();
    });

    it('searchSimilar() converts each row into a T instance via the shared Struct-conversion path and preserves score', async () => {
        const rows: SearchResponse[] = [{ data: { Id: '9', Title: 'Vec', WordCount: 5 }, score: 0.9, traceId: '' }];
        const { fn } = makeStreamStub<SearchSimilarRequest, SearchResponse>(rows);
        const client = new IversonClient('localhost', 0);
        (client as unknown as { _searchClient: unknown })._searchClient = { searchSimilar: fn, close: vi.fn() };

        const req: SearchSimilarRequest = {
            typeName: 'SearchArticle', property: 'Title', query: 'q', topK: 10, traceId: '', filter: [],
            filterLogic: SearchLogic.AND,
        };
        const results = await client.searchSimilar(req, SearchArticle);

        expect(results).toHaveLength(1);
        expect(results[0].entity).toBeInstanceOf(SearchArticle);
        expect(results[0].entity).toMatchObject({ id: '9', title: 'Vec', wordCount: 5 });
        expect(results[0].score).toBe(0.9);

        client.close();
    });

    it('groupBy() returns plain records — no entity conversion applied', async () => {
        const rows: SearchResponse[] = [{ data: { category: 'tech', count: 5 }, score: 0, traceId: '' }];
        const { fn } = makeStreamStub<GroupByRequest, SearchResponse>(rows);
        const client = new IversonClient('localhost', 0);
        (client as unknown as { _searchClient: unknown })._searchClient = { groupBy: fn, close: vi.fn() };

        const req: GroupByRequest = {
            typeName: 'SearchArticle', query: undefined, keys: ['category'], metrics: [], having: undefined,
            orderBy: [], limit: 10_000, joins: [], traceId: '',
        };
        const results = await client.groupBy(req);

        expect(results).toEqual([{ category: 'tech', count: 5 }]);
        expect(results[0]).not.toBeInstanceOf(SearchArticle);

        client.close();
    });

    it('pipeline() returns plain records — no entity conversion applied', async () => {
        const rows: SearchResponse[] = [{ data: { rank: 1, total: 100 }, score: 0, traceId: '' }];
        const { fn } = makeStreamStub<PipelineRequest, SearchResponse>(rows);
        const client = new IversonClient('localhost', 0);
        (client as unknown as { _searchClient: unknown })._searchClient = { pipeline: fn, close: vi.fn() };

        const req: PipelineRequest = {
            typeName: 'SearchArticle', baseWhere: [], baseLogic: SearchLogic.AND, steps: [], orderBy: [],
            limit: 10_000, traceId: '',
        };
        const results = await client.pipeline(req);

        expect(results).toEqual([{ rank: 1, total: 100 }]);

        client.close();
    });

    it('searchChunks() is a typed pass-through of ChunkSearchResponse rows', async () => {
        const rows: ChunkSearchResponse[] = [{ parentKey: 'p1', chunkText: 'hello', score: 0.9, traceId: '' }];
        const { fn } = makeStreamStub<SearchChunksRequest, ChunkSearchResponse>(rows);
        const client = new IversonClient('localhost', 0);
        (client as unknown as { _searchClient: unknown })._searchClient = { searchChunks: fn, close: vi.fn() };

        const req: SearchChunksRequest = {
            typeName: 'SearchArticle', property: 'Body', query: 'q', topK: 5, traceId: '', filter: [],
            filterLogic: SearchLogic.AND,
        };
        const results = await client.searchChunks(req);

        expect(results).toEqual(rows);

        client.close();
    });

    it('aggregate() is a typed pass-through of the unary AggregateResponse', async () => {
        const response: AggregateResponse = { results: [], total: 42, traceId: '' };
        const { fn, calls } = makeUnaryStub<AggregateRequest, AggregateResponse>(response);
        const client = new IversonClient('localhost', 0);
        (client as unknown as { _searchClient: unknown })._searchClient = { aggregate: fn, close: vi.fn() };
        (client as unknown as { _actingUserToken: unknown })._actingUserToken = 'tok-agg';

        const req: AggregateRequest = {
            typeName: 'SearchArticle', query: undefined, aggregations: [], traceId: '', having: undefined, joins: [],
        };
        const result = await client.aggregate(req);

        expect(result).toEqual(response);
        expect(calls[0].metadata.get(ACTING_USER_METADATA_KEY)).toEqual(['Bearer tok-agg']);

        client.close();
    });
});

// ── Declarations the server silently discards on the key field ────────────────

describe('describeEntity key-field validation', () => {
    it('rejects a key field that also declares metadata', () => {
        @IversonEntity()
        class MetadataOnKeyEntity {
            @IversonKey() @IversonMetadata()
            id: string = '';
            tenantId: string = '';
        }

        expect(() => describeEntity(MetadataOnKeyEntity)).toThrow(
            /MetadataOnKeyEntity\.id is the primary key and also declares/,
        );
        expect(() => describeEntity(MetadataOnKeyEntity)).toThrow(/@IversonMetadata\(\)/);
        expect(() => describeEntity(MetadataOnKeyEntity)).toThrow(/silently discarded/);
    });

    it('rejects a key field that also declares summary', () => {
        @IversonEntity()
        class SummaryOnKeyEntity {
            @IversonKey() @IversonSummary()
            id: string = '';
            tenantId: string = '';
        }

        expect(() => describeEntity(SummaryOnKeyEntity)).toThrow(
            /SummaryOnKeyEntity\.id is the primary key and also declares/,
        );
        expect(() => describeEntity(SummaryOnKeyEntity)).toThrow(/@IversonSummary\(\)/);
        expect(() => describeEntity(SummaryOnKeyEntity)).toThrow(/silently discarded/);
    });

    it('names every rejected declaration in one error', () => {
        @IversonEntity()
        class MultiDeclarationKeyEntity {
            @IversonKey() @IversonSearchKey(0) @IversonLargeField() @IversonEmbedding()
            @IversonChunk() @IversonMetadata() @IversonSummary() @IversonKeywords()
            @IversonExtracted('hint')
            id: string = '';
            tenantId: string = '';
        }

        let message = '';
        try {
            describeEntity(MultiDeclarationKeyEntity);
        } catch (err) {
            message = (err as Error).message;
        }
        expect(message).toContain('@IversonSearchKey()');
        expect(message).toContain('@IversonLargeField()');
        expect(message).toContain('@IversonEmbedding()');
        expect(message).toContain('@IversonChunk()');
        expect(message).toContain('@IversonMetadata()');
        expect(message).toContain('@IversonSummary()');
        expect(message).toContain('@IversonKeywords()');
        expect(message).toContain('@IversonExtracted()');
    });

    it('still accepts a key field carrying only a description', () => {
        @IversonEntity()
        class DescribedKeyEntity {
            @IversonKey() @IversonDescription('Stable identifier.')
            id: string = '';
            tenantId: string = '';
        }

        const descriptor = describeEntity(DescribedKeyEntity);
        const key = descriptor.properties.find(p => p.name === 'Id');
        expect(key?.isKey).toBe(true);
        expect(key?.description).toBe('Stable identifier.');
    });
});

// ── FK-only write contract ──────────────────────────────────────────────────

@IversonEntity()
class FkAuthor {
    @IversonKey()
    id: string = '';
    name: string = '';
}

@IversonEntity()
class FkArticle {
    @IversonKey()
    id: string = '';
    tenantId: string = '';

    @ManyToOne(() => FkAuthor)
    fkAuthorId: string = '';

    @ManyToMany(() => FkAuthor)
    contributorIds: string[] = [];

    @OneToMany(() => FkAuthor)
    comments: string = '';
}

describe('entityToPayload — FK-only write contract', () => {
    it('writes the FK column, not the nav member name', async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn } });
        const coordinator = new EntityCoordinator(FkArticle, client);

        const entity = new FkArticle();
        entity.fkAuthorId = 'auth-1';
        entity.comments = 'ignored-nav-value';
        await coordinator.persist(entity);

        const payload = calls[0].req.payload as Record<string, unknown>;
        expect(payload['FkAuthorId']).toBe('auth-1');
        expect(Object.keys(payload)).not.toContain('AuthorId');
        expect(Object.keys(payload)).not.toContain('FkArticleId');
        expect(Object.keys(payload)).not.toContain('Comments');
    });
});

describe('describeEntity — declared FK columns', () => {
    it('declares the FK column under the inferred name for the three non-OneToMany kinds, and nothing for OneToMany', () => {
        const descriptor = describeEntity(FkArticle);
        const propNames = descriptor.properties.map(p => p.name);

        expect(propNames).toContain('FkAuthorId');
        expect(propNames).toContain('FkAuthorIds');
        expect(propNames).not.toContain('FkArticleId');
    });
});

describe('describeEntity — many-to-one/one-to-one FK naming enforcement', () => {
    it('accepts a correctly-named many_to_one member', () => {
        @IversonEntity()
        class GoodArticle {
            @IversonKey()
            id: string = '';
            @ManyToOne(() => FkAuthor)
            fkAuthorId: string = '';
        }

        expect(() => describeEntity(GoodArticle)).not.toThrow();
    });

    it('throws when a many_to_one member is misnamed, naming both names', () => {
        @IversonEntity()
        class BadArticle {
            @IversonKey()
            id: string = '';
            @ManyToOne(() => FkAuthor)
            writerId: string = '';
        }

        let message = '';
        try {
            describeEntity(BadArticle);
        } catch (err) {
            message = (err as Error).message;
        }
        expect(message).toContain('WriterId');
        expect(message).toContain('FkAuthorId');
    });
});

// ── @IversonEmbeddingModel — stamping onto modelId/chunkModelId ────────────────
//
// describeEntity stamps a class-level @IversonEmbeddingModel() onto exactly two of the four
// sites that write modelId/chunkModelId: the non-relation property loop, guarded on that
// property's own isEmbedding/chunkMeta values. The relation foreign-key loop's literal hardcodes
// isEmbedding: false / isChunk: false and is left untouched — a model there is something the
// spec's transport rule excludes and no other client sends.

@IversonEntity()
class ModelDeclaredAuthor {
    @IversonKey()
    id: string = '';
}

/** Shape 1: a declared type whose one property is BOTH embedded and chunked. */
@IversonEntity()
@IversonEmbeddingModel('nomic-embed-text')
class ModelBothFlagsArticle {
    @IversonKey()
    id: string = '';

    @IversonEmbedding()
    @IversonChunk()
    title: string = '';
}

/**
 * Shape 2: a declared type with ASYMMETRIC properties — one embedding-only, one chunk-only.
 * Without this shape a swapped guard (isEmbedding accidentally gating chunkModelId, or vice
 * versa) produces identical output against a both/neither pair and goes undetected.
 */
@IversonEntity()
@IversonEmbeddingModel('nomic-embed-text')
class ModelAsymmetricArticle {
    @IversonKey()
    id: string = '';

    @IversonEmbedding()
    title: string = '';

    @IversonChunk()
    body: string = '';
}

/**
 * Shape 3: a declared type whose relation foreign-key property must NOT receive the model —
 * the TypeScript-specific exclusion. `modelDeclaredAuthorId` PascalCases to
 * `ModelDeclaredAuthorId`, satisfying the many_to_one naming check.
 */
@IversonEntity()
@IversonEmbeddingModel('nomic-embed-text')
class ModelRelationArticle {
    @IversonKey()
    id: string = '';

    @ManyToOne(() => ModelDeclaredAuthor)
    modelDeclaredAuthorId: string = '';
}

/** Shape 4: undeclared, but carrying both an embedded and a chunked property, so the undeclared
 * arm is pinned on chunkModelId as well as modelId — a fixture with only an embedded property
 * cannot catch a stamp that leaks onto the chunk field alone. */
@IversonEntity()
class ModelUndeclaredArticle {
    @IversonKey()
    id: string = '';

    @IversonEmbedding()
    title: string = '';

    @IversonChunk()
    body: string = '';
}

describe('describeEntity — embedding model declaration', () => {
    it('stamps the declared model onto both modelId and chunkModelId for a both-flags property', () => {
        const descriptor = describeEntity(ModelBothFlagsArticle);
        const title = descriptor.properties.find(p => p.name === 'Title')!;
        expect(title.modelId).toBe('nomic-embed-text');
        expect(title.chunkModelId).toBe('nomic-embed-text');
    });

    it('stamps modelId only, not chunkModelId, for an embedding-only property', () => {
        const descriptor = describeEntity(ModelAsymmetricArticle);
        const title = descriptor.properties.find(p => p.name === 'Title')!;
        expect(title.modelId).toBe('nomic-embed-text');
        expect(title.chunkModelId).toBe('');
    });

    it('stamps chunkModelId only, not modelId, for a chunk-only property', () => {
        const descriptor = describeEntity(ModelAsymmetricArticle);
        const body = descriptor.properties.find(p => p.name === 'Body')!;
        expect(body.modelId).toBe('');
        expect(body.chunkModelId).toBe('nomic-embed-text');
    });

    it('leaves the relation foreign-key property at empty string on both fields despite a declared model', () => {
        const descriptor = describeEntity(ModelRelationArticle);
        const fk = descriptor.properties.find(p => p.name === 'ModelDeclaredAuthorId')!;
        expect(fk).toBeDefined();
        expect(fk.modelId).toBe('');
        expect(fk.chunkModelId).toBe('');
    });

    it('leaves modelId and chunkModelId at empty string on both an embedded and a chunked property when undeclared', () => {
        const descriptor = describeEntity(ModelUndeclaredArticle);
        const title = descriptor.properties.find(p => p.name === 'Title')!;
        const body = descriptor.properties.find(p => p.name === 'Body')!;
        expect(title.modelId).toBe('');
        expect(title.chunkModelId).toBe('');
        expect(body.modelId).toBe('');
        expect(body.chunkModelId).toBe('');
    });
});

describe('read/write round-trip — ManyToMany FK column symmetry', () => {
    it('reads back the same ids set on write under the same member', async () => {
        const { fn: postFn, calls: postCalls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'k1', error: '', traceId: '',
        });
        const getResponse: RetrievalResponse = {
            found: true,
            data: {},
            traceId: '',
        };
        const { fn: getFn } = makeUnaryStub<RetrievalManyRequest, RetrievalResponse>(getResponse);

        const client = makeClientLike({
            _persistenceClient: { post: postFn },
            _retrievalClient: { get: getFn },
        });
        const coordinator = new EntityCoordinator(FkArticle, client);

        const entity = new FkArticle();
        entity.contributorIds = ['a1', 'a2'];
        await coordinator.persist(entity);

        const payload = postCalls[0].req.payload as Record<string, unknown>;
        expect(payload['FkAuthorIds']).toEqual(['a1', 'a2']);

        // Feed the written payload back through the read path and confirm symmetry.
        getResponse.data = payload;
        const readBack = await coordinator.get('k1');
        expect(readBack?.contributorIds).toEqual(['a1', 'a2']);
    });
});

// ── Depth-resolved read hydration ────────────────────────────────────────────

@IversonEntity()
class HydTag {
    @IversonKey()
    id: string = '';
    label: string = '';
}

@IversonEntity()
class HydArticle {
    @IversonKey()
    id: string = '';

    @OneToMany(() => HydArticle)
    hydArticles: HydArticle[] = [];
}

@IversonEntity()
class HydAuthor {
    @IversonKey()
    id: string = '';
    name: string = '';

    @OneToMany(() => HydArticle)
    hydArticles: HydArticle[] = [];
}

@IversonEntity()
class HydArticleFull {
    @IversonKey()
    id: string = '';
    title: string = '';

    @ManyToOne(() => HydAuthor)
    hydAuthorId: string = '';

    @ManyToMany(() => HydTag)
    hydTagIds: string[] = [];

    // Second relation to the many_to_many's own related type, through the singular FK, to
    // prove `hydTagIds` -> `hydTags` and `hydTagId` -> `hydTag` land on distinct members.
    @OneToOne(() => HydTag)
    hydTagId: string = '';
}

@IversonEntity()
class HydCollisionArticle {
    @IversonKey()
    id: string = '';

    // Declared field collides with the member `hydAuthorId` would derive to.
    hydAuthor: string = 'declared-value';

    @ManyToOne(() => HydAuthor)
    hydAuthorId: string = '';
}

describe('EntityCoordinator — depth-resolved relation hydration (read path)', () => {
    it('many_to_one hydrates a typed instance on the derived singular member', async () => {
        const { fn } = makeUnaryStub<MappingGetRequest, MappingResponse>({
            success: true,
            data: {
                Id: 'a1',
                Title: 'T',
                HydAuthorId: 'auth-1',
                HydAuthor: { Id: 'auth-1', Name: 'Ada' },
            },
            error: '',
            traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { get: fn } });
        const coordinator = new EntityCoordinator(HydArticleFull, client);

        const result = await coordinator.getMapped('a1', 1);

        expect(result!.hydAuthorId).toBe('auth-1');
        expect((result as unknown as Record<string, unknown>)['hydAuthor']).toBeInstanceOf(HydAuthor);
        expect(((result as unknown as Record<string, unknown>)['hydAuthor'] as HydAuthor).name).toBe('Ada');
    });

    it('many_to_many hydrates typed instances on the plural member, distinct from one_to_one', async () => {
        const { fn } = makeUnaryStub<MappingGetRequest, MappingResponse>({
            success: true,
            data: {
                Id: 'a1',
                Title: 'T',
                HydTagIds: ['t1', 't2'],
                HydTags: [{ Id: 't1', Label: 'x' }, { Id: 't2', Label: 'y' }],
                HydTagId: 't3',
                HydTag: { Id: 't3', Label: 'z' },
            },
            error: '',
            traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { get: fn } });
        const coordinator = new EntityCoordinator(HydArticleFull, client);

        const result = await coordinator.getMapped('a1', 1);
        const row = result as unknown as Record<string, unknown>;

        expect(row['hydTags']).toBeInstanceOf(Array);
        expect((row['hydTags'] as HydTag[]).map(t => t.label)).toEqual(['x', 'y']);
        expect(row['hydTag']).toBeInstanceOf(HydTag);
        expect((row['hydTag'] as HydTag).label).toBe('z');
        // Distinct members: the plural relation didn't overwrite the singular one, or vice versa.
        expect(row['hydTag']).not.toBe(row['hydTags']);
    });

    it('one_to_many hydrates typed instances in place at the declared member', async () => {
        const { fn } = makeUnaryStub<MappingGetRequest, MappingResponse>({
            success: true,
            data: {
                Id: 'auth-1',
                Name: 'Ada',
                HydArticles: [{ Id: 'a1' }, { Id: 'a2' }],
            },
            error: '',
            traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { get: fn } });
        const coordinator = new EntityCoordinator(HydAuthor, client);

        const result = await coordinator.getMapped('auth-1', 1);

        expect(result!.hydArticles).toHaveLength(2);
        expect(result!.hydArticles[0]).toBeInstanceOf(HydArticle);
        expect(result!.hydArticles.map(a => a.id)).toEqual(['a1', 'a2']);
    });

    it('a derived navigation member colliding with a declared field throws', async () => {
        const { fn } = makeUnaryStub<MappingGetRequest, MappingResponse>({
            success: true,
            data: { Id: 'a1', HydAuthorId: 'auth-1', HydAuthor: { Id: 'auth-1', Name: 'Ada' } },
            error: '',
            traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { get: fn } });
        const coordinator = new EntityCoordinator(HydCollisionArticle, client);

        await expect(coordinator.getMapped('a1', 1)).rejects.toThrow(/hydAuthor/);
    });

    it('getMapped -> updateMapped round trip sends the FK, not the hydrated navigation member', async () => {
        const { fn: getFn } = makeUnaryStub<MappingGetRequest, MappingResponse>({
            success: true,
            data: {
                Id: 'a1',
                Title: 'T',
                HydAuthorId: 'auth-1',
                HydAuthor: { Id: 'auth-1', Name: 'Ada' },
                HydTagIds: ['t1'],
                HydTags: [{ Id: 't1', Label: 'x' }],
                HydTagId: 't3',
                HydTag: { Id: 't3', Label: 'z' },
            },
            error: '',
            traceId: '',
        });
        const { fn: updateFn, calls: updateCalls } = makeUnaryStub<MappingWriteRequest, MappingResponse>({
            success: true,
            data: { Id: 'a1', Title: 'T', HydAuthorId: 'auth-1', HydTagIds: ['t1'], HydTagId: 't3' },
            error: '',
            traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { get: getFn, update: updateFn } });
        const coordinator = new EntityCoordinator(HydArticleFull, client);

        const article = await coordinator.getMapped('a1', 1);
        expect((article as unknown as Record<string, unknown>)['hydAuthor']).toBeInstanceOf(HydAuthor);
        // one_to_one nav member is actually assigned here — proving its exclusion from the
        // outgoing payload below is bound to real behavior, not trivially true from an unset field.
        expect((article as unknown as Record<string, unknown>)['hydTag']).toBeInstanceOf(HydTag);

        await coordinator.updateMapped(article!);

        const payload = updateCalls[0].req.payload as Record<string, unknown>;
        expect(payload['HydAuthorId']).toBe('auth-1');
        expect(payload['HydTagIds']).toEqual(['t1']);
        expect(payload['HydTagId']).toBe('t3');
        expect(Object.keys(payload)).not.toContain('Author');
        expect(Object.keys(payload)).not.toContain('HydAuthor');
        expect(Object.keys(payload)).not.toContain('HydTags');
        expect(Object.keys(payload)).not.toContain('HydTag');
    });
});

// ── C1 regression: many_to_many member whose name doesn't end in "Ids" ──────
//
// describeEntity only validates member naming for many_to_one/one_to_one (a many_to_many's wire
// column comes from inferFk(kind, relatedType, ...), not from the member name), so a
// many_to_many member named e.g. `contributors` is legal and registers without error.
// relationNavMember's fallback then returns the field unchanged, which must NOT be treated as a
// "suffix was stripped" derivation.

@IversonEntity()
class M2mNoSuffixAuthor {
    @IversonKey()
    id: string = '';
}

@IversonEntity()
class M2mNoSuffixArticle {
    @IversonKey()
    id: string = '';
    tenantId: string = '';

    @ManyToMany(() => M2mNoSuffixAuthor)
    contributors: string[] = [];
}

describe('C1 — many_to_many member not ending in "Ids"', () => {
    it('write path: the foreign key still reaches the payload (no silent data loss)', async () => {
        const { fn, calls } = makeUnaryStub<PersistRequest, PersistResponse>({
            success: true, key: 'a1', error: '', traceId: '',
        });
        const client = makeClientLike({ _persistenceClient: { post: fn } });
        const coordinator = new EntityCoordinator(M2mNoSuffixArticle, client);

        const entity = new M2mNoSuffixArticle();
        entity.id = 'a1';
        entity.tenantId = 't';
        entity.contributors = ['x', 'y'];
        await coordinator.persist(entity);

        const payload = calls[0].req.payload as Record<string, unknown>;
        expect(payload['M2mNoSuffixAuthorIds']).toEqual(['x', 'y']);
    });

    it('read path: hydration does not throw and does not invent a nav member', async () => {
        const { fn } = makeUnaryStub<MappingGetRequest, MappingResponse>({
            success: true,
            data: {
                Id: 'a1',
                TenantId: 't',
                M2mNoSuffixAuthorIds: ['x', 'y'],
                Contributors: [{ Id: 'x' }, { Id: 'y' }],
            },
            error: '',
            traceId: '',
        });
        const client = makeClientLike({ _mappingClient: { get: fn } });
        const coordinator = new EntityCoordinator(M2mNoSuffixArticle, client);

        const result = await coordinator.getMapped('a1', 1);

        expect(result).not.toBeNull();
        // The declared field is overwritten in place by the hydrated typed instances — no
        // separate/invented nav member is created, and no collision error is thrown.
        const contributors = (result as unknown as Record<string, unknown>)['contributors'] as unknown[];
        expect(contributors).toHaveLength(2);
        expect(contributors[0]).toBeInstanceOf(M2mNoSuffixAuthor);
        expect(Object.getOwnPropertyNames(result as object)).not.toContain('contributor');
        expect(Object.getOwnPropertyNames(result as object)).not.toContain('contributorsHydrated');
    });
});
