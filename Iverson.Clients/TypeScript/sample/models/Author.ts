import 'reflect-metadata';
import { IversonEntity, IversonKey, IversonTenant } from '../../src/annotations.js';

@IversonEntity()
export class Author {
    @IversonKey()
    id: string = '';

    @IversonTenant()
    tenantId: string = '';

    name: string = '';
}
