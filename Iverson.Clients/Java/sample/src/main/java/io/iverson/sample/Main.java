package io.iverson.sample;

import io.iverson.client.core.EntityCoordinator;
import io.iverson.client.core.IversonClient;
import io.iverson.client.core.SchemaRegistrar;
import io.iverson.client.search.Query;
import io.iverson.sample.models.Article;
import io.iverson.sample.models.Author;
import io.iverson.sample.models.Tag;
import iverson.ObjectSearch.SearchRequest;

import java.time.OffsetDateTime;
import java.util.UUID;

/**
 * Sample application demonstrating the Iverson Java client API.
 *
 * <p>This class is intentionally <em>not</em> run as part of CI — it requires
 * a live Iverson server. It compiles cleanly and serves as API documentation.</p>
 *
 * <p>To run against a local server:</p>
 * <pre>
 *   java -cp target/iverson-sample-*.jar io.iverson.sample.Main
 * </pre>
 */
public class Main {

    public static void main(String[] args) throws Exception {
        // ── Connect ────────────────────────────────────────────────────────────
        try (IversonClient client = new IversonClient("localhost", 5000)) {

            // ── Register schemas ───────────────────────────────────────────────
            SchemaRegistrar registrar = new SchemaRegistrar(client);
            registrar.registerAll(Author.class, Tag.class, Article.class);

            // ── Persist an author ──────────────────────────────────────────────
            EntityCoordinator<Author> authorCoordinator =
                new EntityCoordinator<>(client, Author.class);

            UUID authorId = UUID.randomUUID();
            Author author = new Author(authorId, "Jane Smith", "jane@example.com");
            String persistedAuthorId = authorCoordinator.persist(author);
            System.out.println("Persisted author: " + persistedAuthorId);

            // ── Persist an article ─────────────────────────────────────────────
            EntityCoordinator<Article> articleCoordinator =
                new EntityCoordinator<>(client, Article.class);

            Article article = new Article(
                UUID.randomUUID(),
                "The Rise of Functional Programming",
                "Functional programming is transforming how we write software...",
                "technology",
                850,
                OffsetDateTime.now(),
                authorId
            );

            String articleId = articleCoordinator.persist(article);
            System.out.println("Persisted article: " + articleId);

            // ── Retrieve by key ────────────────────────────────────────────────
            Article fetched = articleCoordinator.get(articleId);
            System.out.println("Fetched: " + fetched);

            // ── Search with QueryBuilder ───────────────────────────────────────
            SearchRequest searchRequest = Query.of(Article.class)
                .where("category").eq("technology")
                .and("wordCount").gt(500)
                .orderByDesc("publishedAt")
                .limit(10)
                .build();

            System.out.println("Search request type: " + searchRequest.getTypeName());
            System.out.println("Search clauses:      " + searchRequest.getQuery().getClausesCount());
            System.out.println("Search sorts:        " + searchRequest.getQuery().getSortCount());

            // Execute search (streams results from server)
            var results = articleCoordinator.search(
                Query.of(Article.class)
                    .where("category").eq("technology")
                    .orderByDesc("publishedAt")
                    .limit(5)
            );

            results.forEach(r ->
                System.out.printf("  score=%.3f  article=%s%n", r.score(), r.entity()));
        }
    }
}
