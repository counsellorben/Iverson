/**
 * Sample demonstrating how to use @iverson/client.
 * Not meant to be run without a live server.
 */
import 'reflect-metadata';
import { IversonClient } from '../src/core.js';
import { QueryBuilder } from '../src/search.js';
import { Article } from './models/Article.js';
import { Author } from './models/Author.js';

async function main() {
    const client = new IversonClient('localhost', 5000);

    // Register schemas
    const registrar = client.registrar(Article, Author);
    await registrar.registerAll('sample-trace');

    // Build a query
    const req = new QueryBuilder('Article')
        .where('Category').eq('tech')
        .orderByDesc('PublishedAt')
        .limit(20)
        .offset(0)
        .build();

    console.log('SearchRequest:', JSON.stringify(req, null, 2));

    // CRUD via coordinator
    const articles = client.coordinator(Article);

    const article = new Article();
    article.id = crypto.randomUUID();
    article.title = 'Hello Iverson';
    article.category = 'tech';
    article.wordCount = 500;

    const key = await articles.persist(article);
    console.log('Persisted with key:', key);

    client.close();
}

main().catch(console.error);
