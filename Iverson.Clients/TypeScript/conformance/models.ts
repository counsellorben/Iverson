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
}
