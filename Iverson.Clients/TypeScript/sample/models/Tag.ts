import 'reflect-metadata';
import { IversonEntity, IversonKey, IversonTenant } from '../../src/annotations.js';

@IversonEntity()
export class Tag {
    @IversonKey()
    id: string = '';

    @IversonTenant()
    tenantId: string = '';

    label: string = '';
}
