import 'reflect-metadata';
import { IversonEntity, IversonKey } from '../../src/annotations.js';

@IversonEntity()
export class Author {
    @IversonKey()
    id: string = '';

    name: string = '';
}
