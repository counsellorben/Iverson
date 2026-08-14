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
        String clientId        = System.getenv("IVERSON_CLIENT_ID");
        String clientSecret    = System.getenv("IVERSON_CLIENT_SECRET");
        String tokenEndpoint   = System.getenv("IVERSON_TOKEN_ENDPOINT");

        StringBuilder missingEnvVars = new StringBuilder();
        if (actingUserToken == null || actingUserToken.isBlank()) missingEnvVars.append("IVERSON_ACTING_USER_TOKEN, ");
        if (clientId == null || clientId.isBlank())               missingEnvVars.append("IVERSON_CLIENT_ID, ");
        if (clientSecret == null || clientSecret.isBlank())       missingEnvVars.append("IVERSON_CLIENT_SECRET, ");
        if (tokenEndpoint == null || tokenEndpoint.isBlank())     missingEnvVars.append("IVERSON_TOKEN_ENDPOINT, ");
        if (missingEnvVars.length() > 0) {
            System.err.println(
                missingEnvVars.substring(0, missingEnvVars.length() - 2) + " not set. Every Iverson write is\n"
                    + "denied without an acting user, and the client needs its OAuth2 credentials to talk to the\n"
                    + "server, so this sample cannot seed anything. Export the missing variable(s) and re-run.");
            return;
        }

        // ── Connect ────────────────────────────────────────────────────────────
        try (IversonClient client = new IversonClient(
                "localhost", 5000,
                new OAuth2ClientCredentials(
                    clientId,
                    clientSecret,
                    tokenEndpoint,
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
