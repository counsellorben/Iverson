/**
 * @iverson/client — TypeScript gRPC client for Iverson.
 */
export {
    IversonEntity,
    IversonKey,
    IversonSearchKey,
    IversonLargeField,
    ManyToOne,
    ManyToMany,
    OneToMany,
    OneToOne,
    getKeyField,
    getSearchKeys,
    getLargeFields,
    getRelations,
    isIversonEntity,
} from './annotations.js';

export type { RelationMeta, SearchKeyMeta, RelationKindString } from './annotations.js';

export { IversonClient, EntityCoordinator, SchemaRegistrar } from './core.js';

export { QueryBuilder, FieldCondition, SearchOperator, SearchLogic, SearchClauseType, JoinKind } from './search.js';

export { GroupByBuilder, groupBy } from './group-by.js';
