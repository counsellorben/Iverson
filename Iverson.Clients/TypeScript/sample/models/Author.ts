import 'reflect-metadata';
import { IversonEntity, IversonGuid, IversonKey } from '../../src/annotations.js';

@IversonEntity()
export class Author {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    name: string = '';
}
