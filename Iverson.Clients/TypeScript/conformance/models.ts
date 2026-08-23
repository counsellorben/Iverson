/**
 * S1 `crud-roundtrip` entity models for the TypeScript conformance driver.
 *
 * Mirrors the .NET driver's `DotNetArticle`/`DotNetAuthor`/`DotNetTag` triple (see
 * `Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/`), adapted to the TypeScript
 * client's declaration style: the relation lives directly on the foreign-key member
 * (`tsAuthorId`, `tsTagIds`) via `@ManyToOne`/`@ManyToMany`, per `sample/models/Article.ts`,
 * rather than as a separate FK field plus an annotated navigation property.
 */
import 'reflect-metadata';
import {
    IversonChunk,
    IversonEmbedding,
    IversonEntity,
    IversonGuid,
    IversonKey,
    IversonMetadata,
    ManyToMany,
    ManyToOne,
    OneToMany,
    OneToOne,
} from '../src/annotations.js';

/**
 * S1's "one" side. Carries the reverse `OneToMany` navigation the foreign-key-only write
 * contract work broke, so the harness observes it end to end.
 */
@IversonEntity()
export class TsAuthor {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    name: string = '';

    @OneToMany(() => TsArticle)
    tsArticles: TsArticle[] = [];
}

@IversonEntity()
export class TsTag {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    label: string = '';
}

@IversonEntity()
export class TsArticle {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    title: string = '';

    @ManyToOne(() => TsAuthor)
    tsAuthorId: string = '';

    @ManyToMany(() => TsTag)
    tsTagIds: string[] = [];

    // IVC-REL-001/002/003's one_to_one fixture: a second relation to TsTag (the many_to_many
    // relation's own related type), through the SINGULAR "tsTagId" foreign key so it does not
    // collide with the many_to_many's plural "tsTagIds" — exercising one_to_one end to end
    // without a whole new entity type.
    @OneToOne(() => TsTag)
    tsTagId: string = '';
}

/**
 * S4 `interop`'s "one" side. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule), so this driver's own
 * `SchemaRegistrar` is never invoked for it.
 */
@IversonEntity()
export class SharedAuthor {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    name: string = '';
}

/** S4 `interop`'s root type. */
@IversonEntity()
export class SharedArticle {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    title: string = '';

    @ManyToOne(() => SharedAuthor)
    sharedAuthorId: string = '';
}

/**
 * Exists only for the naming-rejected (S2) conformance scenario. `writerId` declares a
 * `ManyToOne` relation to `TsAuthor` but is not named `authorId` — the name `SchemaRegistrar`
 * requires, since the field itself IS the foreign key. Registering this type must fail
 * client-side, before any RPC (see `src/core.ts`'s relation-naming check).
 */
@IversonEntity()
export class TsBadArticle {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    @ManyToOne(() => TsAuthor)
    writerId: string = '';
}

/**
 * S6 `query`'s subject type. Every one of the five drivers declares the same type name and shape;
 * only the .NET driver ever registers it (register-once rule), and every driver writes one row
 * into it and then queries it.
 *
 * Deliberately relation-free: the scenario's exact result-set comparison is over row keys, and a
 * relation would drag hydration into what a search returns without adding anything the QRY axis
 * asserts. `marker` carries the run's `--id-prefix` and is the property every driver filters on —
 * unique per run, so the expected result set is exactly this run's rows.
 */
@IversonEntity()
export class QueryDoc {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    marker: string = '';

    label: string = '';
}

/**
 * S7 `vector-search`'s subject type. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule), and every driver writes one
 * row into it and then searches it.
 *
 * Deliberately relation-free, and deliberately without any enrichment annotation (summary,
 * keywords, contextual chunking): the scenario's exact set comparisons must not depend on
 * generative output that differs run to run.
 *
 * `marker` carries the run's `--id-prefix` and is the property both queries filter on. It is
 * `@IversonMetadata()` so that one value scopes BOTH stores: the object collection filters it as an
 * ordinary scalar payload clause, and the chunks collection can filter it only because metadata
 * columns are denormalized onto every chunk point. `title` is the embedding source `SearchSimilar`
 * searches; `body` is the chunk source `SearchChunks` searches, short enough to produce a single
 * window per row. `label` is the row's per-language identity — `SearchSimilar` streams the Qdrant
 * payload, whose row key lives under a reserved `key` entry no typed projection binds to `id` — and
 * its spelling must match `VectorSearchScenario.LabelFor`.
 */
@IversonEntity()
export class VectorDoc {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    @IversonMetadata()
    marker: string = '';

    @IversonEmbedding()
    title: string = '';

    @IversonChunk(256, 32)
    body: string = '';

    label: string = '';
}

/**
 * S8 `identity`'s subject type. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule), and every driver writes one
 * row into it, reads that row back, and then attempts one update under a deliberately wrong acting
 * user.
 *
 * Deliberately relation-free and search-free: the axis is about WHOSE identity the server resolves
 * a row's tenant and owner from, and a relation or a vector field would only add ways for the
 * scenario to go red for reasons that are not about identity.
 */
@IversonEntity()
export class IdentityDoc {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    label: string = '';
}

/**
 * S9 `error-contract`'s subject type. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule), and every driver seeds one
 * row into it, reads that row back as a positive control, and then reads a key no row exists under.
 *
 * Deliberately relation-free and search-free: the axis is about what the server's two error shapes
 * look like when they reach a caller, and a relation or a vector field would only add ways for the
 * scenario to go red for reasons that are not about the error contract.
 */
@IversonEntity()
export class ErrorDoc {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    label: string = '';
}

/**
 * S9 `error-contract`'s unregistered fixture: declared by all five drivers and registered by
 * NOTHING — no driver, no scenario, no orchestrator, in this run or any other. A mapped write
 * against it must be refused with `FAILED_PRECONDITION` (`ObjectMappingGrpcService.RequireSchema`),
 * which is the whole observation.
 *
 * Do not add this class to any `SchemaRegistrar` type list. This driver's registrar is always
 * handed an explicit list, so it is never registered by accident; registering it would destroy the
 * fixture `IVC-ERR-005` depends on.
 */
@IversonEntity()
export class ErrorUnregisteredDoc {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    tenantId: string = '';

    ownerId: string = '';

    label: string = '';
}
