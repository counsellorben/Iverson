import 'reflect-metadata';
import { IversonEntity, IversonGuid, IversonKey, IversonTenant } from '../../src/annotations.js';

@IversonEntity()
export class Tag {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    @IversonTenant()
    tenantId: string = '';

    label: string = '';
}
