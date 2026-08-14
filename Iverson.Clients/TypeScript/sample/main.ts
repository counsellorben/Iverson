/**
 * Sample demonstrating how to use @iverson/client.
 * Not meant to be run without a live server.
 */
import 'reflect-metadata';
import { IversonClient } from '../src/core.js';
import { QueryBuilder } from '../src/search.js';
import { AuthorizationRules } from '../generated/object_mapping.js';
import { Article } from './models/Article.js';
import { Author } from './models/Author.js';

async function main() {
    // Every Iverson write is authorized against an acting user; there is no anonymous write.
    const actingUserToken = process.env.IVERSON_ACTING_USER_TOKEN;
    if (!actingUserToken || actingUserToken.trim() === '') {
        console.error(
            'IVERSON_ACTING_USER_TOKEN is not set. Every Iverson write is denied without an\n' +
            'acting user, so this sample cannot seed anything. Export a user access token and re-run.');
        return;
    }

    const client = new IversonClient('localhost', 5000, false, undefined, actingUserToken);

    // OwnerField is left empty — neither sample model carries an owner column — so a single
    // bypass role carries authorization. The acting user behind IVERSON_ACTING_USER_TOKEN must
    // belong to this Authentik group AND carry a tenant_id claim — the evaluator denies on a
    // missing tenant claim before it even consults roles.
    const sampleRules: AuthorizationRules = {
        ownerField: '',
        rowPermissions: [
            {
                role: 'iverson-sample-bypass',
                canReadAll: true,
                canWriteAll: true,
                canDeleteAll: true,
            },
        ],
        fieldPermissions: [],
    };

    // Register schemas
    const registrar = client.registrar(Article, Author);
    await registrar.registerAll('sample-trace', { Article: sampleRules, Author: sampleRules });

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
    article.tenantId = 'sample-tenant';
    article.title = 'Hello Iverson';
    article.category = 'tech';
    article.wordCount = 500;

    const key = await articles.persist(article);
    console.log('Persisted with key:', key);

    client.close();
}

main().catch(console.error);
