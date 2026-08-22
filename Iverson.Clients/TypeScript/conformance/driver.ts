/**
 * The TypeScript conformance driver.
 *
 * Mirrors the .NET driver's shape (`Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/`)
 * and the Python driver's port of it (`Iverson.Clients/Python/conformance/driver.py`): reports,
 * never asserts. Every step's failure is data — `ok: false` with an error message — and the
 * process still exits 0. A non-zero exit means the driver itself broke (bad flags, unsupported
 * scenario, unwritable `--out`).
 *
 * Invoked as `node dist-conformance/conformance/driver.js <flags>` with cwd
 * `Iverson.Clients/TypeScript`, after `npx tsc -p tsconfig.conformance.json`
 * (`DriverRunner.cs:101-103`). Output goes to `--out` only — never stdout, since `console.log`
 * from client code would corrupt the harness's read of it.
 */
import 'reflect-metadata';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as grpc from '@grpc/grpc-js';

import { IversonClient, SchemaRegistrar } from '../src/core.js';
import { createOAuth2ClientCredentials, createActingUserMetadata } from '../src/auth.js';
import {
    ObjectMappingServiceClient,
    type SchemaRequest,
    type SchemaResponse,
    type SchemaType,
    TypeDescriptor,
} from '../generated/object_mapping.js';

import { IdentityDoc, QueryDoc, SharedArticle, SharedAuthor, TsArticle, TsAuthor, TsBadArticle, TsTag, VectorDoc } from './models.js';
import { QueryBuilder, SearchOperator } from '../src/search.js';
import { aggregate } from '../src/aggregate.js';
import { chunks as chunksBuilder, similar as similarBuilder } from '../src/vector-search.js';

const LANGUAGE = 'typescript';
// naming-rejected (S2) is register-phase-only: the orchestrator never invokes this driver for
// any other phase under it. interop (S4) is register-phase-NEVER for this driver: only .NET
// registers SharedAuthor/SharedArticle (register-once rule).
// schema-catalog (S5) uses only the register and read phases: this driver registers TsAuthor and
// then fetches the catalogue back through IversonClient.getSchema().
// query (S6): register (dotnet-only, register-once), write and read — this driver seeds one
// QueryDoc row and then issues a filtered search and a count aggregate through the client
// library's own QueryBuilder/AggregateBuilder.
// vector-search (S7): register (dotnet-only, register-once), write and read — this driver seeds
// one VectorDoc row and then issues a SearchSimilar and a SearchChunks through the client library's
// own vector-search builders.
const SCENARIOS = new Set([
    'crud-roundtrip', 'naming-rejected', 'interop', 'schema-catalog', 'query', 'vector-search',
    'identity',
]);

/** S8 identity: the tenant value every driver stamps on the IdentityDoc row it creates —
 *  deliberately NOT the acting user's tenant. The server force-sets the tenant column from the
 *  acting-user token, so the read-back must show the acting tenant instead; an assertion that would
 *  agree by construction if the driver sent the right value here. Must stay in step with
 *  `IdentityScenario.WrongTenantValue`. */
const IDENTITY_WRONG_TENANT = 'tenant_not_the_acting_user';

/** The Label every VectorDoc row this driver writes carries, and the value the orchestrator's
 *  similarity comparison grades on. Must stay in step with `VectorSearchScenario.LabelFor`. */
const VECTOR_DOC_LABEL = `vec-${LANGUAGE}`;
/** Shared verbatim by all five drivers: a per-language query text would make a disagreement between
 *  two cells un-attributable to the client libraries, and a top-k below the seeded row count would
 *  turn the orchestrator's exact set comparisons into prefix comparisons. */
const VECTOR_QUERY_TEXT = 'a short note about vector search conformance';
const VECTOR_TOP_K = 50;

// ── Argument parsing ────────────────────────────────────────────────────────────

/** Minimal `--flag value` parser, mirroring the .NET/Python drivers' `Args`. */
class Args {
    private readonly values = new Map<string, string>();

    constructor(argv: string[]) {
        let i = 0;
        while (i < argv.length) {
            const flag = argv[i];
            if (!flag.startsWith('--')) {
                i += 1;
                continue;
            }
            // The next argument is the value whatever it looks like: the harness always emits
            // `--flag <value>` pairs (empty string included), and legitimate values — a base64
            // token, a JSON blob — can begin with `--`. Treating a leading `--` as "no value"
            // would silently drop them.
            if (i + 1 < argv.length) {
                this.values.set(flag, argv[i + 1]);
                i += 2;
            } else {
                this.values.set(flag, '');
                i += 1;
            }
        }
    }

    require(flag: string): string {
        const value = this.values.get(flag);
        if (!value) {
            throw new Error(`missing required flag ${flag}`);
        }
        return value;
    }

    optional(flag: string): string | undefined {
        const value = this.values.get(flag);
        return value ? value : undefined;
    }
}

// ── Step result / phase document ─────────────────────────────────────────────────

interface StepResult {
    name: string;
    ok: boolean;
    error: string | null;
    typeDescriptor: unknown | null;
    keys: Record<string, string> | null;
    entity: unknown | null;
}

/**
 * The deliberately minimal, cross-language-identical projection of a GetSchema catalogue that all
 * five drivers report. Copies names verbatim out of the SchemaType messages the client library
 * returned; filters nothing and decides nothing.
 */
function catalogueToReport(types: SchemaType[]): unknown {
    return {
        types: types.map((t) => ({
            name: t.name,
            fields: t.fields.map((f) => ({ name: f.name })),
            relations: t.relations.map((r) => ({ propertyName: r.propertyName })),
        })),
    };
}

function step(
    name: string,
    ok: boolean,
    opts: Partial<Pick<StepResult, 'error' | 'typeDescriptor' | 'keys' | 'entity'>> = {},
): StepResult {
    return {
        name,
        ok,
        error: opts.error ?? null,
        typeDescriptor: opts.typeDescriptor ?? null,
        keys: opts.keys ?? null,
        entity: opts.entity ?? null,
    };
}

/** Serializes an entity with its declared property names, mirroring the .NET/Python drivers'
 * choice to report what the client library actually holds rather than re-casing it. */
function entityToPlain(entity: object | null): Record<string, unknown> | null {
    if (entity === null) return null;
    const out: Record<string, unknown> = {};
    for (const name of Object.getOwnPropertyNames(entity)) {
        out[name] = (entity as Record<string, unknown>)[name];
    }
    return out;
}

/** Renders a caught error the same way whether it's a gRPC `ServiceError` (code + details) or a
 * plain `Error` thrown by the client library itself (e.g. `EntityCoordinator`'s `!success` path,
 * `SchemaRegistrar.registerAll`'s `!success` path). */
function describe(err: unknown): string {
    if (err && typeof err === 'object' && 'code' in err && 'details' in err) {
        const svcErr = err as grpc.ServiceError;
        return `${grpc.status[svcErr.code]}: ${svcErr.details}`;
    }
    if (err instanceof Error) {
        return `${err.name}: ${err.message}`;
    }
    return String(err);
}

/** Deterministic per-run key: distinct across runs because `--id-prefix` is. Only needs to be
 * consistent within this driver's own fallback path — cross-language key equality is not
 * required, since `--keys` is language-qualified (each language reads only its own slice). */
function deriveKey(idPrefix: string, logicalName: string): string {
    const digest = crypto.createHash('md5').update(`${idPrefix}:${logicalName}`).digest();
    const hex = digest.toString('hex');
    return [
        hex.slice(0, 8),
        hex.slice(8, 12),
        hex.slice(12, 16),
        hex.slice(16, 20),
        hex.slice(20, 32),
    ].join('-');
}

/**
 * Splits a gRPC endpoint into the `host` / `port` pair `@grpc/grpc-js` needs, accepting both the
 * scheme-qualified form the harness sends (`http://localhost:8080`) and a bare `host:port`.
 * Throws on anything unusable so the driver fails as a driver (non-zero exit) rather than
 * silently dialing `NaN` and reporting it as a client-library conformance failure.
 */
export function parseGrpcAddress(addr: string): { host: string; port: number } {
    const withScheme = addr.includes('://') ? addr : `http://${addr}`;
    let url: URL;
    try {
        url = new URL(withScheme);
    } catch {
        throw new Error(`unusable --grpc value '${addr}'`);
    }
    if (!url.hostname) throw new Error(`unusable --grpc value '${addr}'`);
    const port = url.port ? Number(url.port) : url.protocol === 'https:' ? 443 : 80;
    if (!Number.isInteger(port) || port <= 0) throw new Error(`unusable --grpc port in '${addr}'`);
    return { host: url.hostname, port };
}

function parseKeys(keysJson: string | undefined, language: string): Record<string, string> {
    if (!keysJson) return {};
    try {
        const byLanguage = JSON.parse(keysJson);
        if (byLanguage && typeof byLanguage === 'object' && language in byLanguage) {
            return byLanguage[language] as Record<string, string>;
        }
    } catch {
        // fall through to empty
    }
    return {};
}

/** The full language-qualified `--keys` map, unlike `parseKeys` which slices out one language. S4
 * interop's read phase needs every language's reported `shared_article` key, not just this
 * driver's own. */
function parseKeysAll(keysJson: string | undefined): Record<string, Record<string, string>> {
    if (!keysJson) return {};
    try {
        const byLanguage = JSON.parse(keysJson);
        return byLanguage && typeof byLanguage === 'object' ? byLanguage : {};
    } catch {
        return {};
    }
}

// ── Descriptor capture (sanctioned seam: SchemaRegistrar's mapping-client constructor param) ──

/**
 * Wraps a real `ObjectMappingServiceClient` and records the outgoing `SchemaRequest.rootType` of
 * every `RegisterSchema` call — before forwarding, so it is captured even if the RPC itself
 * fails. Only `registerSchema` is intercepted; every other member is forwarded via prototypal
 * delegation so the object still satisfies `ObjectMappingServiceClient`'s shape for
 * `SchemaRegistrar`'s constructor. Nothing is judged here — the JSON is reported verbatim.
 */
class CapturingMappingClient {
    private readonly captured: Array<{ typeName: string; json: unknown }> = [];

    /**
     * @param actingToken the acting-user token to stamp on every forwarded registration.
     *   `SchemaRegistrar` accepts only `callCredentials` (the service identity) and has no
     *   acting-user parameter, so without this the TypeScript register phase would be the only
     *   one of the five sending a single identity — and a server that scopes registration by
     *   acting user would fail TypeScript alone, reading as a TypeScript client defect. The other
     *   four drivers all carry both identities into registration.
     */
    constructor(
        private readonly real: ObjectMappingServiceClient,
        private readonly actingToken?: string,
    ) {}

    registerSchema(
        request: SchemaRequest,
        metadata: grpc.Metadata,
        options: Partial<grpc.CallOptions>,
        callback: (error: grpc.ServiceError | null, response: SchemaResponse) => void,
    ): grpc.ClientUnaryCall {
        if (request.rootType) {
            this.captured.push({
                typeName: request.rootType.typeName,
                json: TypeDescriptor.toJSON(request.rootType),
            });
        }
        const outgoing = metadata ?? new grpc.Metadata();
        if (this.actingToken) {
            const actingMetadata = createActingUserMetadata(this.actingToken);
            for (const [key, values] of Object.entries(actingMetadata.getMap())) {
                outgoing.set(key, values as grpc.MetadataValue);
            }
        }
        return this.real.registerSchema(request, outgoing, options, callback);
    }

    /** The descriptor for the first of `preferredTypeNames` actually sent under that exact name,
     * or `undefined` if none of them was. Never substitutes a different type's descriptor. */
    select(...preferredTypeNames: Array<string | undefined>): unknown | undefined {
        for (const preferred of preferredTypeNames) {
            if (!preferred) continue;
            const hit = this.captured.find((c) => c.typeName.toLowerCase() === preferred.toLowerCase());
            if (hit) return hit.json;
        }
        return undefined;
    }
}

// ── Main ──────────────────────────────────────────────────────────────────────────

async function main(argv: string[]): Promise<number> {
    const args = new Args(argv);

    const scenario = args.require('--scenario');
    if (!SCENARIOS.has(scenario)) {
        process.stderr.write(
            `unsupported scenario '${scenario}'; this driver implements [${[...SCENARIOS].join(', ')}]\n`,
        );
        return 2;
    }

    const phase = args.require('--phase');
    const tenant = args.require('--tenant');
    const ownerId = args.require('--owner-id');
    const idPrefix = args.require('--id-prefix');
    const outPath = args.require('--out');
    const typeHint = args.optional('--type');

    // The harness normalizes --grpc to `scheme://host:port` (DriverRunner.NormalizeGrpcUrl),
    // because .NET and Java cannot dial without the scheme. @grpc/grpc-js dials a bare
    // `host:port`, so the scheme is stripped back off here rather than split on naively — a
    // `split(':')` of `http://localhost:8080` yields host `http` and port NaN.
    const grpcAddr = args.require('--grpc');
    const { host, port } = parseGrpcAddress(grpcAddr);

    const clientId = args.optional('--client-id');
    const clientSecret = args.optional('--client-secret');
    const tokenEndpoint = args.optional('--token-endpoint');
    const actingToken = args.optional('--acting-token');

    const serviceToken = args.optional('--service-token');

    // A pre-minted service token wins over the client-credentials trio. Authentik stamps the
    // JWT's `iss` from the request's Host header and grants scopes only when the token request
    // asks for them, so a token this driver minted for itself would be rejected by the API on
    // issuer validation (401) and would carry no `schema_admin` scope (403 on RegisterSchema).
    // The orchestrator mints one correctly and passes it via --service-token.
    const callCredentials = serviceToken
        ? grpc.credentials.createFromMetadataGenerator((_options, callback) => {
              const metadata = new grpc.Metadata();
              metadata.add('authorization', `Bearer ${serviceToken}`);
              callback(null, metadata);
          })
        : clientId && clientSecret && tokenEndpoint
          ? createOAuth2ClientCredentials(clientId, clientSecret, tokenEndpoint)
          : undefined;

    const client = new IversonClient(host, port, false, callCredentials, actingToken);

    // A second, independent ObjectMappingServiceClient built entirely from the public generated
    // module — the sanctioned capture seam per the plan: SchemaRegistrar takes its mapping client
    // as a public constructor parameter. IversonClient's own `_mappingClient` is
    // underscore-prefixed and not part of the public surface even though TypeScript leaves it
    // accessible, so it is not reused here (mirrors the Python driver's `build_registration_channel`
    // and the .NET driver's `Auth.BuildInvoker`, both of which build their own channel/stub rather
    // than reach into the client's internals).
    const registrationChannelCreds = grpc.credentials.createInsecure();
    const realMappingClient = new ObjectMappingServiceClient(`${host}:${port}`, registrationChannelCreds);
    // Both identities go into registration: the service identity via `callCredentials` on the
    // registrar, the acting-user identity via the capture wrapper's per-call metadata.
    const capture = new CapturingMappingClient(realMappingClient, actingToken);

    const priorKeys = parseKeys(args.optional('--keys'), LANGUAGE);
    const keyFor = (logicalName: string): string => priorKeys[logicalName] ?? deriveKey(idPrefix, logicalName);

    const steps: StepResult[] = [];

    if (phase === 'register' && scenario === 'naming-rejected') {
        // TsBadArticle's writerId member fails SchemaRegistrar's naming check before any
        // RegisterSchema call is issued — the capture wrapper never sees a request to record, so
        // there is no typeDescriptor to report either.
        let error: string | null = null;
        try {
            const registrar = new SchemaRegistrar(
                capture as unknown as ObjectMappingServiceClient,
                [TsBadArticle],
                callCredentials,
            );
            await registrar.registerAll();
        } catch (err) {
            error = describe(err);
        }
        steps.push(step('register', error === null, { error }));
    } else if (phase === 'register' && scenario === 'schema-catalog') {
        // S5 schema-catalog: one relation-free type, registered WITHOUT an authorization block on
        // purpose — the orchestrator re-registers it with one before the read phase, and until it
        // does the type is Denied for Read and GetSchema omits it entirely. TsAuthor is this
        // language's own type name, so all five languages registering concurrently overwrite
        // nothing.
        let error: string | null = null;
        try {
            const registrar = new SchemaRegistrar(
                capture as unknown as ObjectMappingServiceClient,
                [TsAuthor],
                callCredentials,
            );
            await registrar.registerAll();
        } catch (err) {
            error = describe(err);
        }
        steps.push(
            step('register_schema_type', error === null, {
                error,
                typeDescriptor: capture.select('TsAuthor') ?? null,
            }),
        );
    } else if (phase === 'read' && scenario === 'schema-catalog') {
        // The catalogue is fetched through the client library's own public getSchema(); the driver
        // reports what came back verbatim and judges none of it.
        try {
            const catalogue = await client.getSchema();
            steps.push(step('get_schema', true, { entity: catalogueToReport(catalogue) }));
        } catch (err) {
            steps.push(step('get_schema', false, { error: describe(err) }));
        }
    } else if (phase === 'register') {
        // SchemaRegistrar.registerAll() issues one RegisterSchema call per type, sequentially, and
        // throws on the first failure (an `Error` on `!response.success`, or the underlying
        // `ServiceError` on a transport failure) — so the sequence aborts at the first failing
        // type. All three steps share that aborted sequence's outcome; `typeDescriptor` presence
        // (recorded by `CapturingMappingClient` before each call is sent) is what tells the
        // orchestrator which types were actually sent.
        let error: string | null = null;
        try {
            const registrar = new SchemaRegistrar(
                capture as unknown as ObjectMappingServiceClient,
                // Author, then tag, then article — the same order in all five drivers, so the
                // types the article's relations reference already exist when the article is
                // sent. Registration aborts at the first failure, so the order is observable.
                [TsAuthor, TsTag, TsArticle],
                callCredentials,
            );
            await registrar.registerAll();
        } catch (err) {
            error = describe(err);
        }

        const addRegisterStep = (name: string, ...preferred: Array<string | undefined>) => {
            steps.push(
                step(name, error === null, { error, typeDescriptor: capture.select(...preferred) ?? null }),
            );
        };

        addRegisterStep('register', typeHint, 'TsArticle');
        addRegisterStep('register_author', 'TsAuthor');
        addRegisterStep('register_tag', 'TsTag');
    } else if (phase === 'write' && scenario === 'interop') {
        // S4 interop: writes SharedAuthor then SharedArticle, reporting keys "shared_author" and
        // "shared_article".
        let sharedAuthorKey: string | undefined;
        let sharedArticleKey: string | undefined;

        const sharedWriters: Array<[string, string, () => Promise<StepResult>]> = [
            ['write_shared_author', 'shared_author', async () => {
                const entity = new SharedAuthor();
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.name = `shared-author-${idPrefix}`;
                sharedAuthorKey = await client.coordinator(SharedAuthor).persist(entity);
                return step('write_shared_author', true, { entity: entityToPlain(entity) });
            }],
            ['write_shared_article', 'shared_article', async () => {
                const entity = new SharedArticle();
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.title = `shared-title-${idPrefix}`;
                if (sharedAuthorKey !== undefined) entity.sharedAuthorId = sharedAuthorKey;
                sharedArticleKey = await client.coordinator(SharedArticle).persist(entity);
                return step('write_shared_article', true, { entity: entityToPlain(entity) });
            }],
        ];

        const sharedKeyValues: Record<string, () => string | undefined> = {
            shared_author: () => sharedAuthorKey,
            shared_article: () => sharedArticleKey,
        };

        for (const [name, keyName, body] of sharedWriters) {
            let result: StepResult;
            try {
                result = await body();
            } catch (err) {
                result = step(name, false, { error: describe(err) });
            }
            const keyValue = sharedKeyValues[keyName]();
            if (keyValue !== undefined) result.keys = { [keyName]: keyValue };
            steps.push(result);
        }
    } else if (phase === 'read' && scenario === 'interop') {
        // Iterates every language's reported "shared_article" key from the full --keys map (not
        // just this driver's own slice), so this one driver invocation reads all five languages'
        // rows — the fan-out that produces 25 reads across the five drivers.
        const allKeys = parseKeysAll(args.optional('--keys'));
        for (const writerLanguage of Object.keys(allKeys).sort()) {
            const key = allKeys[writerLanguage]?.shared_article;
            if (!key) continue;
            const name = `read_shared_article_${writerLanguage}`;
            try {
                const article = await client.coordinator(SharedArticle).get(key);
                steps.push(step(name, true, { entity: entityToPlain(article) }));
            } catch (err) {
                steps.push(step(name, false, { error: describe(err) }));
            }
        }
    } else if (phase === 'register' && scenario === 'identity') {
        // Only the .NET driver ever runs this phase for identity (register-once rule); this branch
        // exists so a hand-run of this driver behaves the same way, and reports the descriptor the
        // orchestrator would re-register with row permissions.
        let error: string | null = null;
        try {
            const registrar = new SchemaRegistrar(
                capture as unknown as ObjectMappingServiceClient, [IdentityDoc], callCredentials);
            await registrar.registerAll();
        } catch (err) {
            error = describe(err);
        }
        steps.push(step('register_identity_doc', error === null, {
            error,
            typeDescriptor: capture.select('IdentityDoc') ?? null,
        }));
    } else if (phase === 'write' && scenario === 'identity') {
        // One row, created under this driver's OWN acting user and carrying a deliberately wrong
        // tenant value (see IDENTITY_WRONG_TENANT). The key is reported whenever persist() resolved
        // with one: the orchestrator's backstop is exactly "this language reported a key", and the
        // negative leg below is only a denial while the row exists.
        let identityKey: string | undefined;
        let result: StepResult;
        try {
            const entity = new IdentityDoc();
            entity.tenantId = IDENTITY_WRONG_TENANT;
            entity.ownerId = ownerId;
            entity.label = `identity-${LANGUAGE}-${idPrefix}`;
            identityKey = await client.coordinator(IdentityDoc).persist(entity);
            result = step('write_identity_doc', true, { entity: entityToPlain(entity) });
        } catch (err) {
            result = step('write_identity_doc', false, { error: describe(err) });
        }
        if (identityKey !== undefined) result.keys = { identity_doc: identityKey };
        steps.push(result);
    } else if (phase === 'read' && scenario === 'identity') {
        const rowKey = keyFor('identity_doc');

        // The positive leg, reported as the deliberately minimal, cross-language-identical
        // projection all five drivers emit — a driver-native serialization would differ per
        // language and make a naming difference render as a conformance failure.
        try {
            const readBack = await client.coordinator(IdentityDoc).getMapped(rowKey, 0);
            steps.push(step('read_identity_doc', true, {
                entity: {
                    key: readBack?.id ?? null,
                    tenant: readBack?.tenantId ?? null,
                    owner: readBack?.ownerId ?? null,
                },
            }));
        } catch (err) {
            steps.push(step('read_identity_doc', false, { error: describe(err) }));
        }

        // The negative leg: a SECOND client carrying --wrong-acting-token in place of this driver's
        // own acting-user token. The service identity is unchanged, so the only thing that differs
        // between this call and an allowed one is which end user it acts as. The status code is
        // DATA to report, never an error to judge — that is the orchestrator's job.
        const wrongClient = new IversonClient(
            host, port, false, callCredentials, args.optional('--wrong-acting-token'));
        try {
            // The update payload carries the ACTING user's real tenant, not IDENTITY_WRONG_TENANT:
            // on an EXISTING row the server rejects a payload tenant that differs from the caller's
            // claim as "Tenant field is immutable" — also PermissionDenied (7). That denial would
            // fire for ANY caller, including the right one, and would make this step green while
            // proving nothing about which end user is calling.
            const entity = new IdentityDoc();
            entity.id = rowKey;
            entity.tenantId = tenant;
            entity.ownerId = ownerId;
            entity.label = `identity-${LANGUAGE}-${idPrefix}-updated-by-the-wrong-user`;
            await wrongClient.coordinator(IdentityDoc).updateMapped(entity);
            // No error: the server accepted the wrong acting user's write. Reported as a missing
            // status code rather than judged here.
            steps.push(step('denied_update_wrong_acting_user', true, {
                entity: { statusCode: null, status: 'succeeded' },
            }));
        } catch (err) {
            const code = (err as { code?: unknown }).code;
            steps.push(typeof code === 'number'
                ? step('denied_update_wrong_acting_user', true, {
                    entity: { statusCode: code, status: grpc.status[code] ?? String(code), detail: describe(err) },
                })
                // Not a gRPC status at all — the attempt never produced an observation.
                : step('denied_update_wrong_acting_user', false, { error: describe(err) }));
        } finally {
            wrongClient.close();
        }
    } else if (phase === 'write' && scenario === 'query') {
        // One row, stamped with the run's marker. The key is reported whenever persist() resolved
        // with one — it is the orchestrator's expected-set accounting, and a row seeded but never
        // reported would silently shrink what every language is graded against.
        let queryDocKey: string | undefined;
        let result: StepResult;
        try {
            const entity = new QueryDoc();
            entity.tenantId = tenant;
            entity.ownerId = ownerId;
            entity.marker = idPrefix;
            entity.label = `doc-${LANGUAGE}`;
            queryDocKey = await client.coordinator(QueryDoc).persist(entity);
            result = step('write_query_doc', true, { entity: entityToPlain(entity) });
        } catch (err) {
            result = step('write_query_doc', false, { error: describe(err) });
        }
        if (queryDocKey !== undefined) result.keys = { query_doc: queryDocKey };
        steps.push(result);
    } else if (phase === 'read' && scenario === 'query') {
        // The filter and the aggregation are both built with the client library's own builder API
        // (QueryBuilder / AggregateBuilder) and executed through IversonClient's own search and
        // aggregate entry points, never through a raw generated stub. Row keys and the metric
        // value are reported verbatim; the orchestrator decides what they mean.
        try {
            const request = new QueryBuilder('QueryDoc').where('marker').eq(idPrefix).limit(100).build();
            const hits = await client.search(request, QueryDoc);
            steps.push(step('search_by_marker', true, { entity: { keys: hits.map((h) => h.entity.id) } }));
        } catch (err) {
            steps.push(step('search_by_marker', false, { error: describe(err) }));
        }

        try {
            const aggregateRequest = aggregate('QueryDoc')
                .where('marker', SearchOperator.EQUALS, idPrefix)
                .countAll('count')
                .build();
            const response = await client.aggregate(aggregateRequest);
            const value = response.results.length > 0 ? response.results[0].metricValue : null;
            steps.push(step('aggregate_count', true, { entity: { value, total: response.total } }));
        } catch (err) {
            steps.push(step('aggregate_count', false, { error: describe(err) }));
        }
    } else if (phase === 'write' && scenario === 'vector-search') {
        // One row, stamped with the run's marker and this language's label. The key is reported
        // whenever persist() resolved with one — it is the orchestrator's expected-set accounting
        // for BOTH vector requirements.
        let vectorDocKey: string | undefined;
        let result: StepResult;
        try {
            const entity = new VectorDoc();
            entity.tenantId = tenant;
            entity.ownerId = ownerId;
            entity.marker = idPrefix;
            entity.title = `vector search conformance note from ${LANGUAGE}`;
            entity.body = `This passage exists so the ${LANGUAGE} conformance driver has a chunked `
                + 'body to retrieve. It is short on purpose: one window per row keeps the '
                + "orchestrator's parent-key comparison exact.";
            entity.label = VECTOR_DOC_LABEL;
            vectorDocKey = await client.coordinator(VectorDoc).persist(entity);
            result = step('write_vector_doc', true, { entity: entityToPlain(entity) });
        } catch (err) {
            result = step('write_vector_doc', false, { error: describe(err) });
        }
        if (vectorDocKey !== undefined) result.keys = { vector_doc: vectorDocKey };
        steps.push(result);
    } else if (phase === 'read' && scenario === 'vector-search') {
        // Both requests are built with the client library's own vector-search builders and executed
        // through IversonClient's own searchSimilar/searchChunks entry points, never through a raw
        // generated stub. Row labels and chunk parent keys are reported verbatim; the orchestrator
        // decides what they mean.
        try {
            const request = similarBuilder('VectorDoc', 'Title')
                .text(VECTOR_QUERY_TEXT)
                .topK(VECTOR_TOP_K)
                .where('Marker', SearchOperator.EQUALS, idPrefix)
                .build();
            const hits = await client.searchSimilar(request, VectorDoc);
            steps.push(step('search_similar_by_title', true, { entity: { labels: hits.map((h) => h.entity.label) } }));
        } catch (err) {
            steps.push(step('search_similar_by_title', false, { error: describe(err) }));
        }

        try {
            const request = chunksBuilder('VectorDoc', 'Body')
                .text(VECTOR_QUERY_TEXT)
                .topK(VECTOR_TOP_K)
                .where('Marker', SearchOperator.EQUALS, idPrefix)
                .build();
            const found = await client.searchChunks(request);
            steps.push(step('search_chunks_by_marker', true, { entity: { parentKeys: found.map((c) => c.parentKey) } }));
        } catch (err) {
            steps.push(step('search_chunks_by_marker', false, { error: describe(err) }));
        }
    } else if (phase === 'write') {
        // Keys are server-assigned: create requests must omit id entirely, and each row's key is
        // only known — and only reported — once persist() resolves with it. authorKey/tagKey are
        // filled in by the closures below and read by the write_article closure, which runs after
        // them in the same sequential loop.
        let authorKey: string | undefined;
        let tagKey: string | undefined;
        let articleKey: string | undefined;

        // One step per row: a denied or failed write must not abort the others.
        const writers: Array<[string, string, () => Promise<StepResult>]> = [
            ['write_author', 'author', async () => {
                const entity = new TsAuthor();
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.name = `author-${idPrefix}`;
                authorKey = await client.coordinator(TsAuthor).persist(entity);
                return step('write_author', true, { entity: entityToPlain(entity) });
            }],
            ['write_tag', 'tag', async () => {
                const entity = new TsTag();
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.label = `tag-${idPrefix}`;
                tagKey = await client.coordinator(TsTag).persist(entity);
                return step('write_tag', true, { entity: entityToPlain(entity) });
            }],
            ['write_article', 'article', async () => {
                const entity = new TsArticle();
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.title = `title-${idPrefix}`;
                if (authorKey !== undefined) entity.tsAuthorId = authorKey;
                if (tagKey !== undefined) entity.tsTagIds = [tagKey];
                if (tagKey !== undefined) entity.tsTagId = tagKey;
                articleKey = await client.coordinator(TsArticle).persist(entity);
                return step('write_article', true, { entity: entityToPlain(entity) });
            }],
        ];

        const keyValues: Record<string, () => string | undefined> = {
            author: () => authorKey,
            tag: () => tagKey,
            article: () => articleKey,
        };

        for (const [name, keyName, body] of writers) {
            let result: StepResult;
            try {
                result = await body();
            } catch (err) {
                result = step(name, false, { error: describe(err) });
            }
            const keyValue = keyValues[keyName]();
            if (keyValue !== undefined) result.keys = { [keyName]: keyValue };
            steps.push(result);
        }
    } else if (phase === 'read') {
        // Two gets at depth 0 (EntityCoordinator.get performs no relation traversal), reported
        // separately so a failure on one is not conflated with the other.
        try {
            const article = await client.coordinator(TsArticle).get(keyFor('article'));
            steps.push(step('get', true, { entity: entityToPlain(article) }));
        } catch (err) {
            steps.push(step('get', false, { error: describe(err) }));
        }

        try {
            const author = await client.coordinator(TsAuthor).get(keyFor('author'));
            steps.push(step('get_author', true, { entity: entityToPlain(author) }));
        } catch (err) {
            steps.push(step('get_author', false, { error: describe(err) }));
        }

        // IVC-LIFE-006/IVC-LIFE-008: a depth-1 read through this driver's OWN client library,
        // reported as its own step — proves the CLIENT can express the request (LIFE-006) and
        // materialize the hydrated result (LIFE-007), distinct from the orchestrator's own
        // depth-1 MappingGet which only proves the SERVER hydrates.
        try {
            const articleDepth1 = await client.coordinator(TsArticle).getMapped(keyFor('article'), 1);
            steps.push(step('get_depth1', true, { entity: entityToPlain(articleDepth1) }));
        } catch (err) {
            steps.push(step('get_depth1', false, { error: describe(err) }));
        }
    } else if (phase === 'update') {
        try {
            const entity = new TsArticle();
            entity.id = keyFor('article');
            entity.tenantId = tenant;
            entity.ownerId = ownerId;
            entity.title = `title-${idPrefix}-updated`;
            entity.tsAuthorId = keyFor('author');
            entity.tsTagIds = [keyFor('tag')];
            entity.tsTagId = keyFor('tag');
            // EntityCoordinator.update() returns nothing (unlike .NET's mapped update, which
            // returns the server's response entity) — the entity reported here is what the driver
            // sent, which is the only observable this API surface offers.
            await client.coordinator(TsArticle).update(entity);
            steps.push(step('update', true, { entity: entityToPlain(entity) }));
        } catch (err) {
            steps.push(step('update', false, { error: describe(err) }));
        }
    } else if (phase === 'delete') {
        const deleteKey = keyFor('article');

        try {
            await client.coordinator(TsArticle).delete(deleteKey);
            steps.push(step('delete', true));
        } catch (err) {
            steps.push(step('delete', false, { error: describe(err) }));
        }

        // The read-back is its own step, carrying `entity` (null when nothing came back) and the
        // client's own error text when the read itself fails — a null entity alone cannot
        // distinguish "gone" from "read denied" from a transport error.
        try {
            const after = await client.coordinator(TsArticle).get(deleteKey);
            steps.push(step('get_after_delete', true, { entity: entityToPlain(after) }));
        } catch (err) {
            steps.push(step('get_after_delete', false, { error: describe(err) }));
        }
    } else {
        client.close();
        realMappingClient.close();
        process.stderr.write(`unknown phase '${phase}'\n`);
        return 2;
    }

    const document = { language: LANGUAGE, phase, steps };
    fs.writeFileSync(outPath, JSON.stringify(document), 'utf-8');

    client.close();
    realMappingClient.close();
    return 0;
}

main(process.argv.slice(2))
    .then((code) => {
        process.exitCode = code;
    })
    .catch((err) => {
        process.stderr.write(`${describe(err)}\n`);
        process.exitCode = 1;
    });
