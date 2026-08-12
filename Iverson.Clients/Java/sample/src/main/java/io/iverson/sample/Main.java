package io.iverson.sample;

import io.iverson.client.core.EntityCoordinator;
import io.iverson.client.core.IversonClient;
import io.iverson.client.core.OAuth2ClientCredentials;
import io.iverson.client.core.SchemaRegistrar;
import io.iverson.client.search.Query;
import io.iverson.sample.models.Article;
import io.iverson.sample.models.Author;
import io.iverson.sample.models.Tag;
import iverson.ObjectMapping;
import iverson.ObjectSearch.SearchRequest;

import java.time.OffsetDateTime;
import java.util.Map;

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

    /** Every row must carry the tenant it belongs to. */
    private static final String TENANT_ID = "sample-tenant";

    public static void main(String[] args) throws Exception {
        String actingUserToken = System.getenv("IVERSON_ACTING_USER_TOKEN");
        if (actingUserToken == null || actingUserToken.isBlank()) {
            System.err.println(
                "IVERSON_ACTING_USER_TOKEN is not set. Every Iverson write is denied without an\n"
                    + "acting user, so this sample cannot seed anything. Export a user access token and re-run.");
            return;
        }

        // ── Connect ────────────────────────────────────────────────────────────
        try (IversonClient client = new IversonClient(
                "localhost", 5000,
                new OAuth2ClientCredentials(
                    System.getenv("IVERSON_CLIENT_ID"),
                    System.getenv("IVERSON_CLIENT_SECRET"),
                    System.getenv("IVERSON_TOKEN_ENDPOINT"),
                    "admin schema_admin"))) {

            // ── Register schemas ───────────────────────────────────────────────
            ObjectMapping.AuthorizationRules sampleRules = ObjectMapping.AuthorizationRules.newBuilder()
                .addRowPermissions(ObjectMapping.RowPermission.newBuilder()
                    .setRole("iverson-sample-bypass")
                    .setCanReadAll(true).setCanWriteAll(true).setCanDeleteAll(true))
                .build();

            SchemaRegistrar registrar = new SchemaRegistrar(client);
            registrar.registerAll(
                Map.of("Author", sampleRules, "Tag", sampleRules, "Article", sampleRules),
                Author.class, Tag.class, Article.class);

            // ── Persist an author ──────────────────────────────────────────────
            EntityCoordinator<Author> authorCoordinator =
                new EntityCoordinator<>(client, Author.class);

            // The server assigns the key and returns the stored entity. Write order is
            // load-bearing: the author must exist before an article can reference it.
            Author author = new Author(null, "Jane Smith", "jane@example.com");
            author.setTenantId(TENANT_ID);
            Author persistedAuthor = authorCoordinator.postMapped(author, actingUserToken);
            System.out.println("Persisted author: " + persistedAuthor.getId());

            // ── Persist an article ─────────────────────────────────────────────
            EntityCoordinator<Article> articleCoordinator =
                new EntityCoordinator<>(client, Article.class);

            Article article = new Article(
                null,
                "The Rise of Functional Programming",
                "Functional programming is transforming how we write software...",
                "technology",
                850,
                OffsetDateTime.now(),
                persistedAuthor.getId()
            );
            article.setTenantId(TENANT_ID);

            Article persistedArticle = articleCoordinator.postMapped(article, actingUserToken);
            System.out.println("Persisted article: " + persistedArticle.getId());

            // ── Retrieve by key ────────────────────────────────────────────────
            Article fetched = articleCoordinator.getMapped(
                persistedArticle.getId().toString(), 1, actingUserToken);
            System.out.println("Fetched: " + fetched);

            // ── Search with QueryBuilder ───────────────────────────────────────
            SearchRequest searchRequest = Query.of(Article.class)
                .where("Category").eq("technology")
                .where("WordCount").gt(500)
                .orderByDesc("PublishedAt")
                .limit(10)
                .build();

            System.out.println("Search request type: " + searchRequest.getTypeName());
            System.out.println("Search clauses:      " + searchRequest.getQuery().getClausesCount());
            System.out.println("Search sorts:        " + searchRequest.getQuery().getSortCount());

            // Execute search (streams results from server)
            var results = articleCoordinator.search(
                Query.of(Article.class)
                    .where("Category").eq("technology")
                    .orderByDesc("PublishedAt")
                    .limit(5)
            );

            results.forEach(r ->
                System.out.printf("  score=%.3f  article=%s%n", r.score(), r.entity()));
        }
    }
}
