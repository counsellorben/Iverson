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
import { createOAuth2ClientCredentials } from '../src/auth.js';
import {
    ObjectMappingServiceClient,
    type SchemaRequest,
    type SchemaResponse,
    TypeDescriptor,
} from '../generated/object_mapping.js';

import { TsArticle, TsAuthor, TsTag } from './models.js';

const LANGUAGE = 'typescript';
const SCENARIO = 'crud-roundtrip';

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
            if (i + 1 < argv.length && !argv[i + 1].startsWith('--')) {
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

    constructor(private readonly real: ObjectMappingServiceClient) {}

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
        return this.real.registerSchema(request, metadata, options, callback);
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
    if (scenario !== SCENARIO) {
        process.stderr.write(
            `unsupported scenario '${scenario}'; this driver implements only '${SCENARIO}'\n`,
        );
        return 2;
    }

    const phase = args.require('--phase');
    const tenant = args.require('--tenant');
    const ownerId = args.require('--owner-id');
    const idPrefix = args.require('--id-prefix');
    const outPath = args.require('--out');
    const typeHint = args.optional('--type');

    const grpcAddr = args.require('--grpc');
    const [host, portStr] = grpcAddr.includes(':') ? grpcAddr.split(':') : [grpcAddr, '5000'];
    const port = portStr ? Number(portStr) : 5000;

    const clientId = args.optional('--client-id');
    const clientSecret = args.optional('--client-secret');
    const tokenEndpoint = args.optional('--token-endpoint');
    const actingToken = args.optional('--acting-token');

    const callCredentials =
        clientId && clientSecret && tokenEndpoint
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
    const capture = new CapturingMappingClient(realMappingClient);

    const priorKeys = parseKeys(args.optional('--keys'), LANGUAGE);
    const keyFor = (logicalName: string): string => priorKeys[logicalName] ?? deriveKey(idPrefix, logicalName);

    const steps: StepResult[] = [];

    if (phase === 'register') {
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
                [TsArticle, TsAuthor, TsTag],
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
    } else if (phase === 'write') {
        const authorKey = deriveKey(idPrefix, 'author');
        const tagKey = deriveKey(idPrefix, 'tag');
        const articleKey = deriveKey(idPrefix, 'article');

        // One step per row: a denied or failed write must not abort the others, and each row's key
        // is reported unconditionally so later phases can address the row even when the write that
        // produced it failed.
        const writers: Array<[string, string, string, () => Promise<StepResult>]> = [
            ['write_author', 'author', authorKey, async () => {
                const entity = new TsAuthor();
                entity.id = authorKey;
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.name = `author-${idPrefix}`;
                await client.coordinator(TsAuthor).persist(entity);
                return step('write_author', true, { entity: entityToPlain(entity) });
            }],
            ['write_tag', 'tag', tagKey, async () => {
                const entity = new TsTag();
                entity.id = tagKey;
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.label = `tag-${idPrefix}`;
                await client.coordinator(TsTag).persist(entity);
                return step('write_tag', true, { entity: entityToPlain(entity) });
            }],
            ['write_article', 'article', articleKey, async () => {
                const entity = new TsArticle();
                entity.id = articleKey;
                entity.tenantId = tenant;
                entity.ownerId = ownerId;
                entity.title = `title-${idPrefix}`;
                entity.tsAuthorId = authorKey;
                entity.tsTagIds = [tagKey];
                await client.coordinator(TsArticle).persist(entity);
                return step('write_article', true, { entity: entityToPlain(entity) });
            }],
        ];

        for (const [name, keyName, keyValue, body] of writers) {
            let result: StepResult;
            try {
                result = await body();
            } catch (err) {
                result = step(name, false, { error: describe(err) });
            }
            result.keys = { [keyName]: keyValue };
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
    } else if (phase === 'update') {
        try {
            const entity = new TsArticle();
            entity.id = keyFor('article');
            entity.tenantId = tenant;
            entity.ownerId = ownerId;
            entity.title = `title-${idPrefix}-updated`;
            entity.tsAuthorId = keyFor('author');
            entity.tsTagIds = [keyFor('tag')];
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
