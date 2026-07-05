using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Iverson.Client.Sample.Models;
using Iverson.Client.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Iverson.Client.Search.SearchOperators;

// ── DI Setup ───────────────────────────────────────────────────────────────────
var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .AddIversonClient(
        grpcEndpoint: "https://localhost:7142",
        typeof(Article).Assembly)
    .BuildServiceProvider();

await services.GetRequiredService<SchemaRegistrar>().RegisterAllAsync();

var authors      = services.GetRequiredService<EntityCoordinator<Author>>();
var articles     = services.GetRequiredService<EntityCoordinator<Article>>();
var tags         = services.GetRequiredService<EntityCoordinator<Tag>>();
var users        = services.GetRequiredService<EntityCoordinator<User>>();
var userArticles = services.GetRequiredService<EntityCoordinator<UserArticle>>();

// Direct search client for Qdrant paths (SearchSimilar / SearchChunks are
// separate RPCs not yet surfaced on EntityCoordinator).
var searchClient = services.GetRequiredService<ObjectSearchService.ObjectSearchServiceClient>();

// ── Seed Data ─────────────────────────────────────────────────────────────────

Console.WriteLine("\n=== Seed Data ===");

// Authors
var authorAiId   = Guid.NewGuid();
var authorKobeId = Guid.NewGuid();

await authors.PersistAsync(new Author
{
    Id    = authorAiId,
    Name  = "Allen Iverson",
    Email = "ai@iverson.dev",
    Bio   = "The original AI. Point guard. Hall of Famer."
});
await authors.PersistAsync(new Author
{
    Id    = authorKobeId,
    Name  = "Kobe Bryant",
    Email = "kb@iverson.dev",
    Bio   = "The Black Mamba. Five-time NBA champion."
});
Console.WriteLine($"Created authors: {authorAiId}, {authorKobeId}");

// Tags
var tagBballId  = Guid.NewGuid();
var tagCultureId = Guid.NewGuid();
var tagLegacyId  = Guid.NewGuid();

await tags.PersistAsync(new Tag { Id = tagBballId,   Label = "Basketball",   Slug = "basketball"   });
await tags.PersistAsync(new Tag { Id = tagCultureId, Label = "Culture",      Slug = "culture"      });
await tags.PersistAsync(new Tag { Id = tagLegacyId,  Label = "Legacy",       Slug = "legacy"       });
Console.WriteLine($"Created tags: basketball, culture, legacy");

// Articles (PostMappedAsync so the server resolves Author and Tags and the full
// hydrated document is emitted to Kafka for StarRocks/Qdrant indexing).
var article1Id = Guid.NewGuid();
var article2Id = Guid.NewGuid();

var article1 = await articles.PostMappedAsync(new Article
{
    Id          = article1Id,
    AuthorId    = authorAiId,
    TagIds      = [tagBballId, tagCultureId],
    Title       = "The Original AI: Allen Iverson's Legacy",
    Body        = "Before large language models, Allen Iverson was already doing the impossible on the hardwood.",
    PublishedAt = DateTime.UtcNow.AddDays(-7),
    IsPublished = true
});
var article2 = await articles.PostMappedAsync(new Article
{
    Id          = article2Id,
    AuthorId    = authorKobeId,
    TagIds      = [tagBballId, tagLegacyId],
    Title       = "The Black Mamba Mentality",
    Body        = "Kobe Bryant's relentless work ethic and Mamba Mentality redefined what dedication to the game looks like.",
    PublishedAt = DateTime.UtcNow.AddDays(-3),
    IsPublished = true
});
Console.WriteLine($"Created articles: '{article1?.Title}', '{article2?.Title}'");

// User
var userId = Guid.NewGuid();
await users.PersistAsync(new User
{
    Id        = userId,
    Name      = "Test User",
    Email     = "test@example.com",
    Username  = "testuser",
    CreatedAt = DateTime.UtcNow
});
Console.WriteLine($"Created user: {userId}");

// UserArticles — PostMappedAsync so the server emits a fully hydrated document
// (User + Article data) to Kafka for StarRocks indexing. UserArticle is the
// primary entity to search because it carries a rich, denormalized view.
var ua1Id = Guid.NewGuid();
var ua2Id = Guid.NewGuid();

await userArticles.PostMappedAsync(new UserArticle
{
    Id        = ua1Id,
    UserId    = userId,
    ArticleId = article1Id,
    CreatedAt = DateTime.UtcNow
});
await userArticles.PostMappedAsync(new UserArticle
{
    Id        = ua2Id,
    UserId    = userId,
    ArticleId = article2Id,
    CreatedAt = DateTime.UtcNow
});
Console.WriteLine($"Created user-articles: {ua1Id}, {ua2Id}");

// ── Object Mapping — PostgreSQL CRUD with server-side graph resolution ─────────

Console.WriteLine("\n=== Object Mapping (PostgreSQL) ===");

// Author: GetMapped with depth=1 returns the Author with inline Articles list.
var fetchedAuthor = await authors.GetMappedAsync(authorAiId.ToString(), depth: 1);
Console.WriteLine($"Author: {fetchedAuthor?.Name} ({fetchedAuthor?.Articles.Count} articles)");

// Article: GetMapped with depth=1 returns Author + Tags resolved.
var fetchedArticle = await articles.GetMappedAsync(article1Id.ToString(), depth: 1);
Console.WriteLine($"Article: '{fetchedArticle?.Title}' by {fetchedArticle?.Author?.Name} [{fetchedArticle?.Tags.Count} tags]");

// Article: depth=2 also recurses into Author's Articles.
var deepArticle = await articles.GetMappedAsync(article1Id.ToString(), depth: 2);
Console.WriteLine($"Article (depth=2): author has {deepArticle?.Author?.Articles.Count} article(s)");

// UserArticle: GetMapped with depth=1 returns both ManyToOne relations resolved.
var fetchedUa = await userArticles.GetMappedAsync(ua1Id.ToString(), depth: 1);
Console.WriteLine($"UserArticle: user='{fetchedUa?.User?.Name}' article='{fetchedUa?.Article?.Title}'");

// Update an article title.
if (fetchedArticle is not null)
{
    fetchedArticle.Title = "The Original AI: Allen Iverson's Enduring Legacy";
    var updated = await articles.UpdateMappedAsync(fetchedArticle);
    Console.WriteLine($"Updated article: '{updated?.Title}'");
}

// Tag: GetMapped (no relations to resolve, just demonstrates the mapping path).
var fetchedTag = await tags.GetMappedAsync(tagBballId.ToString());
Console.WriteLine($"Tag: {fetchedTag?.Label} (slug: {fetchedTag?.Slug})");

// ── Object Retrieval — PostgreSQL key-based, client assembles the graph ────────

Console.WriteLine("\n=== Object Retrieval (PostgreSQL) ===");

// Author — client side graph assembly via GraphAssembler.
var retrievedAuthor = await authors.GetAsync(authorKobeId.ToString());
Console.WriteLine($"Retrieved author: {retrievedAuthor?.Name}, articles={retrievedAuthor?.Articles.Count}");

// Tags — streaming via GetManyAsync.
Console.WriteLine("Tags (GetMany):");
var tagIds = new[] { tagBballId.ToString(), tagCultureId.ToString(), tagLegacyId.ToString() };
await foreach (var tag in tags.GetManyAsync(tagIds))
    Console.WriteLine($"  - {tag.Label} ({tag.Slug})");

// User — simple key lookup.
var retrievedUser = await users.GetAsync(userId.ToString());
Console.WriteLine($"Retrieved user: {retrievedUser?.Name} ({retrievedUser?.Email})");

// UserArticle — key lookup with both ManyToOne relations assembled client-side.
var retrievedUa = await userArticles.GetAsync(ua2Id.ToString());
Console.WriteLine($"Retrieved user-article: user={retrievedUa?.User?.Name}, article='{retrievedUa?.Article?.Title}'");

// ── Object Search — StarRocks ─────────────────────────────────────────────────

// Kafka consumers need time to process events and project rows into StarRocks.
// In a production test harness, await a projection-complete signal instead.
Console.WriteLine("\n=== Object Search — StarRocks ===");
Console.WriteLine("(Waiting 3 s for Kafka consumers to project rows into StarRocks...)");
await Task.Delay(TimeSpan.FromSeconds(3));

// UserArticle is the fully hydrated model: the StarRocks row carries User + Article
// data denormalized into a single record, making it the richest search surface.

// Find all UserArticles for a specific user (EqualTo on the Guid FK).
// The StarRocks row is fully denormalized: User + Article data is stored flat,
// so it is searchable by any column at query time.
var uaUserQuery = Query.For<UserArticle>()
    .Where(ua => ua.UserId, EqualTo, userId)
    .OrderBy(ua => ua.CreatedAt, descending: true)
    .Page(0, size: 10);

Console.WriteLine($"UserArticles for user {userId}:");
await foreach (var result in userArticles.SearchAsync(uaUserQuery))
    Console.WriteLine($"  [{result.Score:F2}] user={result.Entity.User?.Name} article='{result.Entity.Article?.Title}'");

// Search Articles by title keyword.
var articleTextQuery = Query.For<Article>()
    .Where(a => a.Title,       Contains,    "Mamba")
    .Where(a => a.IsPublished, EqualTo,     true)
    .OrderBy(a => a.PublishedAt, descending: true)
    .Page(0, size: 10);

Console.WriteLine("Articles — title contains 'Mamba', IsPublished=true:");
await foreach (var result in articles.SearchAsync(articleTextQuery))
    Console.WriteLine($"  [{result.Score:F2}] '{result.Entity.Title}'");

// Search Authors by name.
var authorTextQuery = Query.For<Author>()
    .Where(a => a.Name, Contains, "Allen")
    .Page(0, size: 5);

Console.WriteLine("Authors — name contains 'Allen':");
await foreach (var result in authors.SearchAsync(authorTextQuery))
    Console.WriteLine($"  [{result.Score:F2}] {result.Entity.Name}");

// Tag search by slug using OR logic.
var tagQuery = Query.For<Tag>()
    .Where(t => t.Slug, Contains, "basketball")
    .Where(t => t.Slug, Contains, "culture")
    .Where(t => t.Slug, Contains, "legacy")
    .WithLogic(SearchLogic.Or)
    .OrderBy(t => t.Label);

Console.WriteLine("Tags — slug basketball|culture|legacy:");
await foreach (var result in tags.SearchAsync(tagQuery))
    Console.WriteLine($"  {result.Entity.Label} ({result.Entity.Slug})");

// ── Object Search — Pipeline (CTE chain, StarRocks) ───────────────────────────

Console.WriteLine("\n=== Object Search — Pipeline (CTE chain) ===");

// One CTE step: group published Articles by author, count them, sorted descending.
var articleCountsByAuthor = Pipeline.For<Article>()
    .Where("IsPublished", EqualTo, true)
    .Step("counts", s => s
        .GroupBy("AuthorId")
        .CountAll("total"))
    .SortOn("total", descending: true);

Console.WriteLine("Article counts by author (Pipeline, untyped rows):");
await foreach (var row in articles.PipelineAsync(articleCountsByAuthor))
    Console.WriteLine($"  author={row["AuthorId"]} total={row["total"]}");

Console.WriteLine("Same pipeline, typed rows (AuthorArticleCount):");
await foreach (var typedRow in articles.PipelineAsync<AuthorArticleCount>(articleCountsByAuthor))
    Console.WriteLine($"  author={typedRow.AuthorId} total={typedRow.Total}");

// ── Object Search — Qdrant Vector ─────────────────────────────────────────────

// Article has [IversonEmbedding] on Title → "title_vector" named vector in Qdrant.
// Article has [IversonChunk] on Body    → stored chunked in the "article_chunks" collection.
//
// SearchSimilar and SearchChunks are separate gRPC RPCs. EntityCoordinator.SearchAsync
// routes to StarRocks via ObjectSearchService.Search; call the gRPC client
// directly for Qdrant vector operations (SearchSimilar / SearchChunks).

Console.WriteLine("\n=== Object Search — Qdrant (vector similarity) ===");

try
{
    var similarStream = searchClient.SearchSimilar(new SearchSimilarRequest
    {
        TypeName = "Article",
        Property = "Title",
        Query    = "AI basketball legend",
        TopK     = 5,
        TraceId  = Guid.NewGuid().ToString()
    });

    Console.WriteLine("Articles similar to 'AI basketball legend' (Title vector):");
    await foreach (var response in similarStream.ResponseStream.ReadAllAsync())
        Console.WriteLine($"  [score={response.Score:F4}] {response.Data?.Fields.GetValueOrDefault("title")?.StringValue}");
}
catch (RpcException ex)
{
    Console.WriteLine($"  (Vector search unavailable: {ex.Status.Detail})");
}

try
{
    var chunkStream = searchClient.SearchChunks(new SearchChunksRequest
    {
        TypeName = "Article",
        Property = "Body",
        Query    = "dedication work ethic basketball",
        TopK     = 3,
        TraceId  = Guid.NewGuid().ToString()
    });

    Console.WriteLine("Article body chunks similar to 'dedication work ethic basketball':");
    await foreach (var response in chunkStream.ResponseStream.ReadAllAsync())
        Console.WriteLine($"  [score={response.Score:F4}] parent={response.ParentKey} | \"{response.ChunkText[..Math.Min(80, response.ChunkText.Length)]}...\"");
}
catch (RpcException ex)
{
    Console.WriteLine($"  (Chunk search unavailable: {ex.Status.Detail})");
}

// ── Cleanup ────────────────────────────────────────────────────────────────────

Console.WriteLine("\n=== Cleanup ===");

await userArticles.DeleteAsync(ua1Id.ToString());
await userArticles.DeleteAsync(ua2Id.ToString());
await articles.DeleteAsync(article1Id.ToString());
await articles.DeleteAsync(article2Id.ToString());

Console.WriteLine("Deleted user-articles and articles. Authors, tags, and user left in place.");
Console.WriteLine("\nDone.");
