import 'reflect-metadata';
import { IversonEntity, IversonGuid, IversonKey, IversonTenant } from '../../src/annotations.js';

@IversonEntity()
export class Author {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    @IversonTenant()
    tenantId: string = '';

    name: string = '';
}
