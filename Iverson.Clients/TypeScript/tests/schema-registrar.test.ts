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
    IversonPopularitySignal,
    IversonDescription,
    IversonSummary,
    IversonKeywords,
    IversonExtracted,
    IversonArray,
    IversonGuid,
    IversonType,
    ManyToOne,
    ManyToMany,
    OneToMany,
} from '../src/annotations.js';
import { IversonClient, SchemaRegistrar } from '../src/core.js';
import {
    AuthorizationRules,
    ObjectType,
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
    regAuthorId: string = '';
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

        it('attaches per-type authorization rules by type name, leaving an unlisted type undefined', async () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle, RegAuthor]);

            const articleRules: AuthorizationRules = { ownerField: 'authorId', rowPermissions: [], fieldPermissions: [] };
            const authorRules: AuthorizationRules = { ownerField: 'ownerId', rowPermissions: [], fieldPermissions: [] };

            await registrar.registerAll('trace', { RegArticle: articleRules, RegAuthor: authorRules });

            const calls = (stub.registerSchema as ReturnType<typeof vi.fn>).mock.calls;
            const byTypeName = new Map(
                calls.map(call => {
                    const req = call[0] as SchemaRequest;
                    return [req.rootType!.typeName, req.rootType!.authorization] as const;
                }),
            );
            expect(byTypeName.get('RegArticle')?.ownerField).toBe('authorId');
            expect(byTypeName.get('RegAuthor')?.ownerField).toBe('ownerId');
        });

        it('leaves authorization undefined for a type with no entry in the map', async () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);

            await registrar.registerAll('trace', { RegAuthor: { ownerField: 'ownerId', rowPermissions: [], fieldPermissions: [] } });

            const capturedReq = (stub.registerSchema as ReturnType<typeof vi.fn>).mock.calls[0][0] as SchemaRequest;
            expect(capturedReq.rootType!.authorization).toBeUndefined();
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

        it('gives a ManyToOne relation a property name distinct from its FK', () => {
            @IversonEntity()
            class Author {
                @IversonKey()
                id: string = '';
            }

            @IversonEntity()
            class NavArticle {
                @IversonKey()
                id: string = '';

                @ManyToOne(() => Author)
                authorId: string = '';
            }

            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [NavArticle]);
            const req = registrar._buildRequest(NavArticle);
            const rel = req.rootType!.relations[0];
            // The navigation property name must NOT collide with the FK column, or a
            // depth-resolved read overwrites the FK value with the hydrated entity.
            expect(rel.propertyName).not.toBe(rel.foreignKey);
            expect(rel.propertyName).toBe('Author');
            expect(rel.foreignKey).toBe('AuthorId');
        });

        it('gives a ManyToMany relation a property name distinct from its FK', () => {
            @IversonEntity()
            class RegTag {
                @IversonKey()
                id: string = '';
            }

            @IversonEntity()
            class NavTagArticle {
                @IversonKey()
                id: string = '';

                @ManyToMany(() => RegTag)
                regTagIds: string[] = [];
            }

            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [NavTagArticle]);
            const req = registrar._buildRequest(NavTagArticle);
            const rel = req.rootType!.relations[0];
            // The navigation property name must NOT collide with the FK column, or a
            // depth-resolved read overwrites the FK value with the hydrated entity.
            expect(rel.propertyName).not.toBe(rel.foreignKey);
            expect(rel.propertyName).toBe('RegTags');
            expect(rel.foreignKey).toBe('RegTagIds');

            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));
            expect(props['RegTagIds']).toBeDefined();
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

        it('registers a @IversonGuid property as GUID and leaves untagged strings alone', () => {
            @IversonEntity()
            class GuidKeyEntity {
                @IversonKey() @IversonGuid()
                id: string = '';
                name: string = '';
                tenantId: string = '';
            }

            // Delete design:type metadata to exercise the fallback branch when metadata is unavailable.
            // Oxc now emits this metadata by default, so we delete it to verify the code path for
            // builds that don't emit metadata (e.g. esbuild-based consumer builds).
            Reflect.deleteMetadata('design:type', GuidKeyEntity.prototype, 'id');
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [GuidKeyEntity]);
            const req = registrar._buildRequest(GuidKeyEntity);
            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

            expect(props['Id'].objectType).toBe(ObjectType.GUID);
            expect(props['Name'].objectType).toBe(ObjectType.STRING);
        });

        it('rejects @IversonGuid() on a non-string property', () => {
            @IversonEntity()
            class GuidOnNumberEntity {
                @IversonKey()
                id: string = '';
                @IversonGuid()
                wordCount: number = 0;
                tenantId: string = '';
            }

            Reflect.deleteMetadata('design:type', GuidOnNumberEntity.prototype, 'wordCount');
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [GuidOnNumberEntity]);
            expect(() => registrar._buildRequest(GuidOnNumberEntity)).toThrow(/wordCount/);
            expect(() => registrar._buildRequest(GuidOnNumberEntity)).toThrow(/IversonGuid/);
        });

        it('rejects @IversonGuid() on an array property, pointing at @IversonArray(ObjectType.GUID)', () => {
            @IversonEntity()
            class GuidOnArrayEntity {
                @IversonKey()
                id: string = '';
                @IversonArray(ObjectType.STRING)
                @IversonGuid()
                tagIds: string[] = [];
                tenantId: string = '';
            }

            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [GuidOnArrayEntity]);
            expect(() => registrar._buildRequest(GuidOnArrayEntity)).toThrow(/tagIds/);
            expect(() => registrar._buildRequest(GuidOnArrayEntity)).toThrow(/IversonArray\(ObjectType\.GUID\)/);
        });

        it('accepts @IversonGuid() when design:type metadata says String (tsc production path)', () => {
            @IversonEntity()
            class GuidMetadataStringEntity {
                @IversonKey() @IversonGuid()
                id: string = '';
                tenantId: string = '';
            }

            // Explicit defineMetadata is redundant with Oxc's own emission under the current toolchain,
            // but is kept because it makes the "metadata says X" premise explicit and toolchain-independent.
            Reflect.defineMetadata('design:type', String, GuidMetadataStringEntity.prototype, 'id');

            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [GuidMetadataStringEntity]);
            const req = registrar._buildRequest(GuidMetadataStringEntity);
            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));
            expect(props['Id'].objectType).toBe(ObjectType.GUID);
        });

        it('rejects @IversonGuid() when design:type metadata says Number (tsc production path)', () => {
            @IversonEntity()
            class GuidMetadataNumberEntity {
                @IversonKey()
                id: string = '';
                @IversonGuid()
                wordCount: number = 0;
                tenantId: string = '';
            }

            // Explicit defineMetadata is redundant with Oxc's own emission under the current toolchain,
            // but is kept because it makes the "metadata says X" premise explicit and toolchain-independent.
            Reflect.defineMetadata('design:type', Number, GuidMetadataNumberEntity.prototype, 'wordCount');

            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [GuidMetadataNumberEntity]);
            expect(() => registrar._buildRequest(GuidMetadataNumberEntity)).toThrow(/wordCount/);
            expect(() => registrar._buildRequest(GuidMetadataNumberEntity)).toThrow(/IversonGuid/);
        });

        it('accepts @IversonGuid() on an initializer-less string property (no design:type, undefined runtime value)', () => {
            @IversonEntity()
            class GuidNoInitializerEntity {
                @IversonKey() @IversonGuid()
                id!: string;
                tenantId: string = '';
            }

            Reflect.deleteMetadata('design:type', GuidNoInitializerEntity.prototype, 'id');
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [GuidNoInitializerEntity]);
            const req = registrar._buildRequest(GuidNoInitializerEntity);
            const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));
            expect(props['Id'].objectType).toBe(ObjectType.GUID);
        });

        it('synthesizes relation foreign keys as GUID', () => {
            const stub = makeStub();
            const registrar = new SchemaRegistrar(stub, [RegArticle]);
            const props = Object.fromEntries(
                registrar._buildRequest(RegArticle).rootType!.properties.map(p => [p.name, p]));
            expect(props['RegAuthorId'].objectType).toBe(ObjectType.GUID);
            expect(props['RegAuthorId'].isArray).toBe(false);

            @IversonEntity()
            class TaggedPost {
                @IversonKey() id: string = '';
                tenantId: string = '';
                @ManyToMany(() => RegAuthor)
                regAuthorIds: string[] = [];
            }

            const mtm = Object.fromEntries(
                new SchemaRegistrar(makeStub(), [TaggedPost])._buildRequest(TaggedPost).rootType!.properties.map(p => [p.name, p]));
            expect(mtm['RegAuthorIds'].objectType).toBe(ObjectType.GUID);
            expect(mtm['RegAuthorIds'].isArray).toBe(true);
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

    it('sets isPopularitySignal only on the marked property', () => {
        @IversonEntity()
        class RegInteraction {
            @IversonKey()
            id: string = '';

            @IversonPopularitySignal()
            interactedAt: Date = new Date();

            plain: Date = new Date();
        }

        const { props } = propsOf(RegInteraction);
        expect(props['InteractedAt'].isPopularitySignal).toBe(true);
        expect(props['Id'].isPopularitySignal).toBe(false);
        expect(props['Plain'].isPopularitySignal).toBe(false);
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

// ── Array fields ────────────────────────────────────────────────────────────

describe('_buildRequest — array fields', () => {
    it('registers a decorated array property with isArray=true and the declared objectType', () => {
        @IversonEntity()
        class WithArray {
            @IversonKey()
            id: string = '';

            orgId: string = '';

            @IversonArray(ObjectType.STRING)
            tags: string[] = [];
        }

        const stub = makeStub();
        const registrar = new SchemaRegistrar(stub, [WithArray]);
        const req = registrar._buildRequest(WithArray);
        const props = Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));

        expect(props['Tags'].isArray).toBe(true);
        expect(props['Tags'].objectType).toBe(ObjectType.STRING);
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

describe('_buildRequest — scalar type resolution', () => {
    function propsOf(cls: Function) {
        const registrar = new SchemaRegistrar(makeStub(), [cls]);
        const req = registrar._buildRequest(cls);
        return Object.fromEntries(req.rootType!.properties.map(p => [p.name, p]));
    }

    it('infers undecorated number, boolean and Date from their initializers', () => {
        @IversonEntity()
        class ScalarInitialized {
            @IversonKey()
            id: string = '';

            n: number = 0;
            b: boolean = true;
            d: Date = new Date();
        }

        const props = propsOf(ScalarInitialized);
        expect(props['N'].objectType).toBe(ObjectType.DOUBLE);
        expect(props['B'].objectType).toBe(ObjectType.BOOL);
        expect(props['D'].objectType).toBe(ObjectType.DATETIME);
    });

    it('maps a decorated number to DOUBLE', () => {
        @IversonEntity()
        class DecoratedNumber {
            @IversonKey()
            id: string = '';

            @IversonDescription('x')
            n: number = 0;
        }

        expect(propsOf(DecoratedNumber)['N'].objectType).toBe(ObjectType.DOUBLE);
    });

    it('resolves a string | number union from its string initializer', () => {
        @IversonEntity()
        class StringOrNumber {
            @IversonKey()
            id: string = '';

            x: string | number = '';
        }

        expect(propsOf(StringOrNumber)['X'].objectType).toBe(ObjectType.STRING);
    });

    it('resolves a decorated nullable union from its non-null initializer', () => {
        @IversonEntity()
        class InitializedUnion {
            @IversonKey()
            id: string = '';

            @IversonDescription('x')
            x: number | null = 5;
        }

        expect(propsOf(InitializedUnion)['X'].objectType).toBe(ObjectType.DOUBLE);
    });

    it('prefers design:type over the initializer when both are known', () => {
        @IversonEntity()
        class StringTypedNumberInit {
            @IversonKey()
            id: string = '';

            @IversonDescription('x')
            x: string = 0 as unknown as string;
        }

        expect(propsOf(StringTypedNumberInit)['X'].objectType).toBe(ObjectType.STRING);
    });

    it('lets @IversonType override the inferred type', () => {
        @IversonEntity()
        class DeclaredInt {
            @IversonKey()
            id: string = '';

            @IversonType(ObjectType.INT32)
            n: number = 0;
        }

        expect(propsOf(DeclaredInt)['N'].objectType).toBe(ObjectType.INT32);
    });

    it('lets @IversonType declare a nullable Date', () => {
        @IversonEntity()
        class DeclaredNullableDate {
            @IversonKey()
            id: string = '';

            @IversonType(ObjectType.DATETIME)
            d: Date | null = null;
        }

        expect(propsOf(DeclaredNullableDate)['D'].objectType).toBe(ObjectType.DATETIME);
    });

    it('registers an undecorated number initializer as DOUBLE, not STRING', () => {
        expect(propsOf(RegArticle)['WordCount'].objectType).toBe(ObjectType.DOUBLE);
    });

    it('resolves @IversonType and initializers when the build emits no decorator metadata', () => {
        // No syntactic decorators, so no build emits design:type for this class; the decorators
        // are applied by hand, as an esbuild-style consumer build would leave them.
        class NoMetadata {
            id: string = '';
            n: number = 0;
            d: Date = new Date();
            b: boolean = true;
            e?: Date;
        }
        IversonEntity()(NoMetadata);
        IversonKey()(NoMetadata.prototype, 'id');
        IversonType(ObjectType.DATETIME)(NoMetadata.prototype, 'e');

        const props = propsOf(NoMetadata);
        expect(props['N'].objectType).toBe(ObjectType.DOUBLE);
        expect(props['D'].objectType).toBe(ObjectType.DATETIME);
        expect(props['B'].objectType).toBe(ObjectType.BOOL);
        expect(props['E'].objectType).toBe(ObjectType.DATETIME);
    });

    it('throws when an optional number has no initializer', () => {
        @IversonEntity()
        class OptionalNumber {
            @IversonKey()
            id: string = '';

            x?: number;
        }

        expect(() => propsOf(OptionalNumber)).toThrow(/OptionalNumber\.x has no type .*cannot be inferred/);
        expect(() => propsOf(OptionalNumber)).toThrow(/initializer.*@IversonType\(ObjectType\..*@IversonArray\(ObjectType\./);
    });

    it('throws when an undecorated nullable Date is initialized to null', () => {
        @IversonEntity()
        class NullDate {
            @IversonKey()
            id: string = '';

            x: Date | null = null;
        }

        expect(() => propsOf(NullDate)).toThrow(/NullDate\.x has no type .*cannot be inferred/);
    });

    it('throws when an optional string has no initializer', () => {
        @IversonEntity()
        class OptionalString {
            @IversonKey()
            id: string = '';

            x?: string;
        }

        expect(() => propsOf(OptionalString)).toThrow(/OptionalString\.x has no type .*cannot be inferred/);
    });

    it('names @IversonArray when an undecorated nullable array is initialized to null', () => {
        @IversonEntity()
        class NullArray {
            @IversonKey()
            id: string = '';

            tags: string[] | null = null;
        }

        expect(() => propsOf(NullArray)).toThrow(/NullArray\.tags has no type .*@IversonArray\(ObjectType\./);
    });

    it('does not infer an undecorated Uint8Array initializer', () => {
        @IversonEntity()
        class UndecoratedBytes {
            @IversonKey()
            id: string = '';

            data: Uint8Array = new Uint8Array();
        }

        expect(() => propsOf(UndecoratedBytes)).toThrow(/UndecoratedBytes\.data has no type .*cannot be inferred/);
    });

    it('throws when @IversonType sits on a nullable @IversonArray property', () => {
        @IversonEntity()
        class TypedNullableArray {
            @IversonKey()
            id: string = '';

            @IversonArray(ObjectType.STRING)
            @IversonType(ObjectType.STRING)
            x: string[] | null = null;
        }

        expect(() => propsOf(TypedNullableArray)).toThrow(/@IversonType\(\) is scalar-only/);
    });

    it('throws when @IversonType sits on an array property without @IversonArray', () => {
        @IversonEntity()
        class TypedArray {
            @IversonKey()
            id: string = '';

            @IversonType(ObjectType.STRING)
            x: string[] = [];
        }

        expect(() => propsOf(TypedArray)).toThrow(/@IversonType\(\) is scalar-only/);
    });

    it('throws when @IversonType and @IversonGuid both declare the type', () => {
        @IversonEntity()
        class TypedGuid {
            @IversonKey()
            id: string = '';

            @IversonGuid()
            @IversonType(ObjectType.GUID)
            x: string = '';
        }

        expect(() => propsOf(TypedGuid)).toThrow(/both @IversonGuid\(\) and @IversonType\(\)/);
    });
});

// ── IversonClient.getSchema ────────────────────────────────────────────────

describe('IversonClient.getSchema', () => {
    it('returns response.types via the unary GetSchema call', async () => {
        const response: GetSchemaResponse = {
            types: [
                {
                    name: 'Article',
                    description: '',
                    fields: [
                        {
                            name: 'category',
                            description: '',
                            objectType: ObjectType.STRING,
                            isArray: false,
                            isKey: false,
                            isNullable: false,
                            isMetadata: false,
                            isSearchKey: true,
                            searchKeyOrder: 0,
                            isEmbedding: false,
                            isChunk: false,
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

        const client = new IversonClient('localhost', 0, false);
        (client as unknown as { _mappingClient: unknown })._mappingClient = stub;

        const types = await client.getSchema('trace-1');

        expect(types).toHaveLength(1);
        expect(types[0].name).toBe('Article');
        expect(types[0].fields).toHaveLength(1);
        expect(types[0].fields[0].name).toBe('category');
        expect(types[0].fields[0].objectType).toBe(ObjectType.STRING);
        expect(types[0].fields[0].isSearchKey).toBe(true);
        expect(types[0].fields[0].searchKeyOrder).toBe(0);
        expect(getSchema).toHaveBeenCalledTimes(1);
        const capturedReq = getSchema.mock.calls[0][0];
        expect(capturedReq).toEqual({ traceId: 'trace-1' });

        client.close();
    });
});

describe('toolchain configuration', () => {
    it('the test toolchain emits design:type (oxc.decorator.emitDecoratorMetadata)', () => {
        @IversonEntity()
        class MetadataProbe {
            @IversonKey() id: string = '';
        }
        expect(Reflect.getMetadata('design:type', MetadataProbe.prototype, 'id')).toBe(String);
    });
});
