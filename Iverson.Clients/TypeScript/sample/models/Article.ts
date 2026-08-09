import 'reflect-metadata';
import {
    IversonEntity,
    IversonGuid,
    IversonKey,
    IversonLargeField,
    IversonSearchKey,
    IversonTenant,
    ManyToOne,
} from '../../src/annotations.js';
import { Author } from './Author.js';

@IversonEntity()
export class Article {
    @IversonKey()
    @IversonGuid()
    id: string = '';

    @IversonTenant()
    tenantId: string = '';

    title: string = '';

    @IversonLargeField()
    body: string = '';

    @IversonSearchKey(0)
    category: string = '';

    wordCount: number = 0;

    @IversonSearchKey(1)
    publishedAt: Date = new Date();

    @ManyToOne(() => Author)
    authorId: string = '';
}
