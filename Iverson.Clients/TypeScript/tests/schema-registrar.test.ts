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
    IversonMetadata,
    IversonDescription,
    IversonSummary,
    IversonKeywords,
    IversonExtracted,
    IversonTenant,
    IversonArray,
    ManyToOne,
    OneToMany,
} from '../src/annotations.js';
import { IversonClient, SchemaRegistrar } from '../src/core.js';
import {
    ClrType,
    GetSchemaResponse,
    ObjectMappingServiceClient,
    RelationKind,
    SchemaEnrichmentKind,
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
IversonTenant()(RegAuthor.prototype, 'name');

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
    @IversonTenant()
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
                @IversonTenant()
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

// ── Metadata / description entity ─────────────────────────────────────────────

@IversonEntity()
@IversonDescription('Documents ingested for retrieval.')
class RegDoc {
    @IversonKey()
    @IversonDescription('Stable document identifier.')
    id: string = '';

    @IversonMetadata()
    @IversonDescription('Publishing source.')
    source: string = '';

    @IversonMetadata()
    region: string = '';

    @IversonTenant()
    title: string = '';
}

describe('_buildRequest — metadata and descriptions', () => {
    function propsOf(cls: Function) {
        const registrar = new SchemaRegistrar(makeStub(), [cls]);
        const req = registrar._buildRequest(cls);
        return {
            req,
            props: Object.fromEntries(req.rootType!.properties.map(p => [p.name, p])),
        };
    }

    it('sets isMetadata only on marked properties', () => {
        const { props } = propsOf(RegDoc);
        expect(props['Source'].isMetadata).toBe(true);
        expect(props['Region'].isMetadata).toBe(true);
        expect(props['Title'].isMetadata).toBe(false);
        expect(props['Id'].isMetadata).toBe(false);
    });

    it('sets property descriptions, including on the key property', () => {
        const { props } = propsOf(RegDoc);
        expect(props['Id'].description).toBe('Stable document identifier.');
        expect(props['Source'].description).toBe('Publishing source.');
        expect(props['Title'].description).toBe('');
    });

    it('sets the type-level description', () => {
        const { req } = propsOf(RegDoc);
        expect(req.rootType!.description).toBe('Documents ingested for retrieval.');
    });

    it('carries metadata and descriptions across the wire encoding', () => {
        const { req } = propsOf(RegDoc);
        const decoded = SchemaRequest.decode(SchemaRequest.encode(req).finish());
        const props = Object.fromEntries(decoded.rootType!.properties.map(p => [p.name, p]));

        expect(decoded.rootType!.description).toBe('Documents ingested for retrieval.');
        // Regression guard: a description on the KEY property must not be dropped.
        expect(props['Id'].description).toBe('Stable document identifier.');
        expect(props['Source'].description).toBe('Publishing source.');
        expect(props['Source'].isMetadata).toBe(true);
        expect(props['Region'].isMetadata).toBe(true);
        expect(props['Title'].isMetadata).toBe(false);
    });

    it('leaves descriptions and isMetadata empty for entities without them', () => {
        const { req, props } = propsOf(RegArticle);
        expect(req.rootType!.description).toBe('');
        expect(props['Category'].isMetadata).toBe(false);
        expect(props['Category'].description).toBe('');
    });
});

// ── Ingest enrichment targets ──────────────────────────────────────────────────

@IversonEntity()
class RegEnriched {
    @IversonKey()
    id: string = '';

    @IversonSummary()
    summary: string = '';

    @IversonKeywords()
    keywords: string = '';

    @IversonExtracted('the invoice total amount')
    total: string = '';

    @IversonChunk(256, 32, { contextual: true })
    body: string = '';

    @IversonTenant()
    plainField: string = '';
}

describe('_buildRequest — ingest enrichment targets', () => {
    function propsOf(cls: Function) {
        const registrar = new SchemaRegistrar(makeStub(), [cls]);
        const req = registrar._buildRequest(cls);
        return Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));
    }

    it('sets isSummaryTarget only on the @IversonSummary() property', () => {
        const props = propsOf(RegEnriched);
        expect(props['Summary'].isSummaryTarget).toBe(true);
        expect(props['Keywords'].isSummaryTarget).toBe(false);
        expect(props['PlainField'].isSummaryTarget).toBe(false);
    });

    it('sets isKeywordsTarget only on the @IversonKeywords() property', () => {
        const props = propsOf(RegEnriched);
        expect(props['Keywords'].isKeywordsTarget).toBe(true);
        expect(props['Summary'].isKeywordsTarget).toBe(false);
        expect(props['PlainField'].isKeywordsTarget).toBe(false);
    });

    it('sets extractHint only on the @IversonExtracted() property', () => {
        const props = propsOf(RegEnriched);
        expect(props['Total'].extractHint).toBe('the invoice total amount');
        expect(props['Summary'].extractHint).toBe('');
        expect(props['PlainField'].extractHint).toBe('');
    });

    it('sets chunkContextual from the IversonChunk contextual option', () => {
        const props = propsOf(RegEnriched);
        expect(props['Body'].chunkContextual).toBe(true);
        expect(props['Body'].isChunk).toBe(true);
    });

    it('defaults chunkContextual to false when not specified', () => {
        const props = propsOf(RegArticle);
        expect(props['Summary'].isChunk).toBe(true);
        expect(props['Summary'].chunkContextual).toBe(false);
    });

    it('leaves an undeclared property with none of the four enrichment targets', () => {
        const props = propsOf(RegEnriched);
        expect(props['PlainField'].isSummaryTarget).toBe(false);
        expect(props['PlainField'].isKeywordsTarget).toBe(false);
        expect(props['PlainField'].extractHint).toBe('');
        expect(props['PlainField'].chunkContextual).toBe(false);
    });

    it('carries the enrichment targets across the wire encoding', () => {
        const req = new SchemaRegistrar(makeStub(), [RegEnriched])._buildRequest(RegEnriched);
        const decoded = SchemaRequest.decode(SchemaRequest.encode(req).finish());
        const props = Object.fromEntries(decoded.rootType!.properties.map(p => [p.name, p]));
        expect(props['Summary'].isSummaryTarget).toBe(true);
        expect(props['Keywords'].isKeywordsTarget).toBe(true);
        expect(props['Total'].extractHint).toBe('the invoice total amount');
        expect(props['Body'].chunkContextual).toBe(true);
    });

    it('rejects a blank extraction hint at decoration time', () => {
        expect(() => {
            class BadEntity {
                @IversonKey()
                id: string = '';

                @IversonExtracted('   ')
                total: string = '';
            }
            void BadEntity;
        }).toThrow(/non-blank extraction hint/);
    });

    it('rejects an empty-string extraction hint at decoration time', () => {
        expect(() => {
            class BadEntity2 {
                @IversonKey()
                id: string = '';

                @IversonExtracted('')
                total: string = '';
            }
            void BadEntity2;
        }).toThrow(/non-blank extraction hint/);
    });
});

// ── Tenant field ────────────────────────────────────────────────────────────

describe('_buildRequest — tenant field', () => {
    it('sets tenantField to the PascalCased decorated property name', () => {
        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [RegArticle]);
        const req = registrar._buildRequest(RegArticle);
        expect(req.rootType!.tenantField).toBe('Category');
    });

    it('composes the tenant marker with other declarations on the same property (search key)', () => {
        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [RegArticle]);
        const req = registrar._buildRequest(RegArticle);
        const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

        expect(req.rootType!.tenantField).toBe('Category');
        expect(props['Category'].isSearchKey).toBe(true);
        expect(props['Category'].searchKeyOrder).toBe(0);
    });

    it('throws naming the type when no property is decorated with @IversonTenant()', () => {
        @IversonEntity()
        class NoTenant {
            @IversonKey()
            id: string = '';
        }

        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [NoTenant]);
        expect(() => registrar._buildRequest(NoTenant)).toThrow(/NoTenant/);
        expect(() => registrar._buildRequest(NoTenant)).toThrow(/@IversonTenant/);
    });

    it('throws naming both properties when two are decorated with @IversonTenant()', () => {
        @IversonEntity()
        class TwoTenants {
            @IversonKey()
            id: string = '';

            @IversonTenant()
            orgId: string = '';

            @IversonTenant()
            accountId: string = '';
        }

        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [TwoTenants]);
        expect(() => registrar._buildRequest(TwoTenants)).toThrow(/orgId/);
        expect(() => registrar._buildRequest(TwoTenants)).toThrow(/accountId/);
    });
});

// ── Array fields ────────────────────────────────────────────────────────────

describe('_buildRequest — array fields', () => {
    it('registers a decorated array property with isArray=true and the declared clrType', () => {
        @IversonEntity()
        class WithArray {
            @IversonKey()
            id: string = '';

            @IversonTenant()
            orgId: string = '';

            @IversonArray(ClrType.CLR_STRING)
            tags: string[] = [];
        }

        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [WithArray]);
        const req = registrar._buildRequest(WithArray);
        const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

        expect(props['Tags'].isArray).toBe(true);
        expect(props['Tags'].clrType).toBe(ClrType.CLR_STRING);
    });

    it('throws when an array property is decorated but not with @IversonArray', () => {
        @IversonEntity()
        class DecoratedNotArray {
            @IversonKey()
            id: string = '';

            @IversonMetadata()
            tags: string[] = [];
        }

        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [DecoratedNotArray]);
        expect(() => registrar._buildRequest(DecoratedNotArray)).toThrow(/@IversonArray/);
    });

    it('throws when an array property is fully undecorated', () => {
        @IversonEntity()
        class UndecoratedArray {
            @IversonKey()
            id: string = '';

            tags: string[] = [];
        }

        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [UndecoratedArray]);
        expect(() => registrar._buildRequest(UndecoratedArray)).toThrow(/@IversonArray/);
    });

    it('leaves a non-array property unaffected', () => {
        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [RegArticle]);
        const req = registrar._buildRequest(RegArticle);
        const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

        expect(props['Title'].isArray).toBe(false);
    });
});

// ── IversonClient.getSchema ────────────────────────────────────────────────

describe('IversonClient.getSchema', () => {
    it('returns response.types via the unary GetSchema call', async () => {
        const response: GetSchemaResponse = {
            types: [
                {
                    typeName: 'Article',
                    description: '',
                    fields: [
                        {
                            name: 'category',
                            clrType: ClrType.CLR_STRING,
                            isKey: false,
                            isSearchKey: true,
                            searchKeyOrder: 0,
                            isMetadata: false,
                            description: '',
                            enrichment: [SchemaEnrichmentKind.ENRICHMENT_NONE],
                        },
                    ],
                    relations: [],
                },
            ],
        };
        const getSchema = vi.fn(
            (req: unknown, _metadata: unknown, _options: unknown, cb: (err: null, res: GetSchemaResponse) => void) => {
                cb(null, response);
                return {} as any;
            },
        );
        const stub = { getSchema, close: vi.fn() } as unknown as ObjectMappingServiceClient;

        const client = new IversonClient('localhost', 0);
        (client as unknown as { _mappingClient: unknown })._mappingClient = stub;

        const types = await client.getSchema('trace-1');

        expect(types).toEqual(response.types);
        expect(getSchema).toHaveBeenCalledTimes(1);
        const capturedReq = getSchema.mock.calls[0][0];
        expect(capturedReq).toEqual({ traceId: 'trace-1' });

        client.close();
    });
});
