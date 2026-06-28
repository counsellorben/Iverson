import 'reflect-metadata';
import { IversonEntity, IversonKey } from '../../src/annotations.js';

@IversonEntity()
export class Tag {
    @IversonKey()
    id: string = '';

    label: string = '';
}
