import 'reflect-metadata';
import { IversonEntity, IversonGuid, IversonKey } from '../../src/annotations.js';

@IversonEntity()
export class Tag {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    label: string = '';
}
