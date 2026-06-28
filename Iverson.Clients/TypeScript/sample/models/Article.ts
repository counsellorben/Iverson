import 'reflect-metadata';
import {
    IversonEntity,
    IversonKey,
    IversonLargeField,
    IversonSearchKey,
    ManyToOne,
} from '../../src/annotations.js';
import { Author } from './Author.js';

@IversonEntity()
export class Article {
    @IversonKey()
    id: string = '';

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
