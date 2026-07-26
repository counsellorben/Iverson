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

import { IversonEntity, IversonKey } from '../src/annotations.js';
import { ACTING_USER_METADATA_KEY } from '../src/auth.js';
import { EntityCoordinator, IversonClient } from '../src/core.js';

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
