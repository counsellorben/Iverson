/**
 * Tests for SchemaRegistrar — verifies correct SchemaRequest is built from entity metadata.
 */
import 'reflect-metadata';
import { describe, it, expect, vi } from 'vitest';

import {
    IversonEntity,
    IversonKey,
    IversonSearchKey,
    IversonLargeField,
    IversonEmbedding,
    IversonChunk,
    ManyToOne,
    OneToMany,
} from '../src/annotations.js';
import { SchemaRegistrar } from '../src/core.js';
import {
    ObjectMappingServiceClient,
    RelationKind,
    SchemaRequest,
    SchemaResponse,
} from '../generated/object_mapping.js';

// ── Test entities ─────────────────────────────────────────────────────────────

class RegAuthor {
    id: string = '';
    name: string = '';
}

// Apply decorators manually (so the class definition above has the real properties)
IversonEntity()(RegAuthor);
IversonKey()(RegAuthor.prototype, 'id');

@IversonEntity()
class RegArticle {
    @IversonKey()
    id: string = '';

    @IversonEmbedding()
    title: string = '';

    @IversonChunk(256, 32)
    summary: string = '';

    @IversonLargeField()
    body: string = '';

    @IversonSearchKey(0)
    category: string = '';

    wordCount: number = 0;

    @IversonSearchKey(1)
    publishedAt: Date = new Date();

    @ManyToOne(() => RegAuthor)
    authorId: string = '';
}

// ── Mock helpers ──────────────────────────────────────────────────────────────

function makeSuccessResponse(): SchemaResponse {
    return {
        success: true,
        traceId: '',
        error: '',
        registered: [],
    };
}

function makeStub(overrideResponse?: Partial<SchemaResponse>): ObjectMappingServiceClient {
    const response: SchemaResponse = { ...makeSuccessResponse(), ...overrideResponse };
    const stub = {
        registerSchema: vi.fn(
            (req: SchemaRequest, _metadata: unknown, _options: unknown, cb: (err: null, res: SchemaResponse) => void) => {
                cb(null, response);
                return {} as any;
            },
        ),
    } as unknown as ObjectMappingServiceClient;
    return stub;
}

function makeFailingStub(errorMsg: string): ObjectMappingServiceClient {
    const response: SchemaResponse = {
        success: false,
        traceId: '',
        error: errorMsg,
        registered: [],
    };
    const stub = {
        registerSchema: vi.fn(
            (req: SchemaRequest, _metadata: unknown, _options: unknown, cb: (err: null, res: SchemaResponse) => void) => {
                cb(null, response);
                return {} as any;
            },
        ),
    } as unknown as ObjectMappingServiceClient;
    return stub;
}

// ── Tests ─────────────────────────────────────────────────────────────────────

describe('SchemaRegistrar', () => {
    describe('registerAll', () => {
        it('calls registerSchema once per entity class', async () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle, RegAuthor]);
            await registrar.registerAll();
            expect(stub.registerSchema).toHaveBeenCalledTimes(2);
        });

        it('throws when response.success is false', async () => {
            const stub = makeFailingStub('table already exists');
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            await expect(registrar.registerAll()).rejects.toThrow('table already exists');
        });

        it('throws when class is not decorated with @IversonEntity()', async () => {
            class Plain { id: string = ''; }
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [Plain]);
            await expect(registrar.registerAll()).rejects.toThrow('@IversonEntity()');
        });

        it('passes traceId through to the request', async () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegAuthor]);
            await registrar.registerAll('test-trace-123');

            const capturedReq = (stub.registerSchema as ReturnType<typeof vi.fn>).mock.calls[0][0] as SchemaRequest;
            expect(capturedReq.traceId).toBe('test-trace-123');
        });
    });

    describe('_buildRequest — type name', () => {
        it('sets root_type type_name to the class name', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            expect(req.rootType!.typeName).toBe('RegArticle');
        });
    });

    describe('_buildRequest — properties', () => {
        it('includes the key field with isKey=true', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

            expect(props['Id']).toBeDefined();
            expect(props['Id'].isKey).toBe(true);
        });

        it('marks body as isLargeField', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

            expect(props['Body']).toBeDefined();
            expect(props['Body'].isLargeField).toBe(true);
        });

        it('marks title as isEmbedding', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const request = registrar._buildRequest(RegArticle);
            const props = Object.fromEntries(
                request.rootType!.properties.map(p => [p.name, p]),
            );
            expect(props['Title'].isEmbedding).toBe(true);
        });

        it('marks summary as isChunk with maxTokens/overlap', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const request = registrar._buildRequest(RegArticle);
            const props = Object.fromEntries(
                request.rootType!.properties.map(p => [p.name, p]),
            );
            expect(props['Summary'].isChunk).toBe(true);
            expect(props['Summary'].chunkMaxTokens).toBe(256);
            expect(props['Summary'].chunkOverlap).toBe(32);
        });

        it('marks category as isSearchKey with order 0', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

            expect(props['Category']).toBeDefined();
            expect(props['Category'].isSearchKey).toBe(true);
            expect(props['Category'].searchKeyOrder).toBe(0);
        });

        it('marks publishedAt as isSearchKey with order 1', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

            expect(props['PublishedAt']).toBeDefined();
            expect(props['PublishedAt'].isSearchKey).toBe(true);
            expect(props['PublishedAt'].searchKeyOrder).toBe(1);
        });

        it('converts field names to PascalCase', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            const propNames = req.rootType!.properties.map(p => p.name);

            expect(propNames).toContain('WordCount');
            expect(propNames).toContain('PublishedAt');
        });

        it('does not include relation fields in properties list', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            const propNames = req.rootType!.properties.map(p => p.name);

            expect(propNames).not.toContain('AuthorId');
        });
    });

    describe('_buildRequest — relations', () => {
        it('includes a ManyToOne relation for authorId', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);

            expect(req.rootType!.relations).toHaveLength(1);
            const rel = req.rootType!.relations[0];
            expect(rel.relatedType).toBe('RegAuthor');
            expect(rel.kind).toBe(RelationKind.MANY_TO_ONE);
        });

        it('infers FK as {RelatedType}Id for ManyToOne', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const req = registrar._buildRequest(RegArticle);
            const rel = req.rootType!.relations[0];
            expect(rel.foreignKey).toBe('RegAuthorId');
        });

        it('infers FK as {ThisType}Id for OneToMany', () => {
            @IversonEntity()
            class Post {
                @IversonKey()
                id: string = '';

                @OneToMany(() => RegAuthor)
                comments: string = '';
            }

            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [Post]);
            const req = registrar._buildRequest(Post);
            const rel = req.rootType!.relations[0];
            expect(rel.foreignKey).toBe('PostId');
            expect(rel.kind).toBe(RelationKind.ONE_TO_MANY);
        });
    });
});
