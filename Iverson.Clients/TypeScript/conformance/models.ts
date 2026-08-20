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
    IversonEntity,
    IversonGuid,
    IversonKey,
    IversonTenant,
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

    @IversonTenant()
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

    @IversonTenant()
    tenantId: string = '';

    ownerId: string = '';

    label: string = '';
}

@IversonEntity()
export class TsArticle {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    @IversonTenant()
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

    @IversonTenant()
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

    @IversonTenant()
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

    @IversonTenant()
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

    @IversonTenant()
    tenantId: string = '';

    ownerId: string = '';

    marker: string = '';

    label: string = '';
}
