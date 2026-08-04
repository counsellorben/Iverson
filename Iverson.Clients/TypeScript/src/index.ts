/**
 * @iverson/client — TypeScript gRPC client for Iverson.
 */
export {
    IversonEntity,
    IversonKey,
    IversonSearchKey,
    IversonLargeField,
    IversonEmbedding,
    IversonChunk,
    IversonMetadata,
    IversonDescription,
    IversonArray,
    ManyToOne,
    ManyToMany,
    OneToMany,
    OneToOne,
    getKeyField,
    getSearchKeys,
    getLargeFields,
    getEmbeddingFields,
    getChunkFields,
    getMetadataFields,
    getTypeDescription,
    getPropertyDescriptions,
    getRelations,
    getArrayFields,
    isIversonEntity,
} from './annotations.js';

export { ClrType } from '../generated/object_mapping.js';

export type { SchemaType, SchemaField, SchemaRelation } from '../generated/object_mapping.js';

export type { RelationMeta, SearchKeyMeta, RelationKindString, ChunkMeta } from './annotations.js';

export { IversonClient, EntityCoordinator, SchemaRegistrar } from './core.js';

export type { SearchResult } from './core.js';

export { QueryBuilder, FieldCondition, SearchOperator, SearchLogic, SearchClauseType, JoinKind } from './search.js';

export { GroupByBuilder, groupBy } from './group-by.js';

export { AggregateBuilder, aggregate } from './aggregate.js';

export { PipelineBuilder, PipelineStepBuilder, SelectSpecBuilder, pipeline } from './pipeline.js';

export { SimilarBuilder, ChunksBuilder, similar, chunks } from './vector-search.js';

export { createOAuth2ClientCredentials, createActingUserMetadata } from './auth.js';
