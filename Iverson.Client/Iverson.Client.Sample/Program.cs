using Iverson.Client.Core;
using Iverson.Client.Sample.Models;
using Iverson.Client.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Iverson.Client.Search.SearchOperators;

// ── DI Setup ───────────────────────────────────────────────────────────────────
var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug))
    .AddIversonClient(
        grpcEndpoint: "https://localhost:5001",
        typeof(Article).Assembly)
    .BuildServiceProvider();

var articles = services.GetRequiredService<EntityCoordinator<Article>>();
var authors  = services.GetRequiredService<EntityCoordinator<Author>>();
var tags     = services.GetRequiredService<EntityCoordinator<Tag>>();

// ── Object Mapping — full CRUD with server-side graph resolution ───────────────

Console.WriteLine("\n=== Object Mapping ===");

var newArticle = new Article
{
    Id          = Guid.NewGuid(),
    AuthorId    = Guid.Parse("11111111-0000-0000-0000-000000000001"),
    Title       = "The Original AI",
    Body        = "Before LLMs, Allen Iverson was already doing the impossible.",
    PublishedAt = DateTime.UtcNow,
    IsPublished = true
};

var created = await articles.PostMappedAsync(newArticle);
Console.WriteLine($"Created: {created?.Title} by {created?.Author?.Name}");

// GET with depth=1 so the server also returns the Author inline
var fetched = await articles.GetMappedAsync(newArticle.Id.ToString(), depth: 1);
Console.WriteLine($"Fetched: {fetched?.Title}, author={fetched?.Author?.Name}, tags={fetched?.Tags.Count}");

// UPDATE
if (fetched is not null)
{
    fetched.Title = "The Original AI (Updated)";
    var updated = await articles.UpdateMappedAsync(fetched);
    Console.WriteLine($"Updated title: {updated?.Title}");
}

// DELETE
var deleted = await articles.DeleteAsync(newArticle.Id.ToString());
Console.WriteLine($"Deleted: {deleted}");

// ── Object Persistence — lightweight write, no graph traversal ─────────────────

Console.WriteLine("\n=== Object Persistence ===");

var author = new Author
{
    Id    = Guid.NewGuid(),
    Name  = "Allen Iverson",
    Email = "ai@iverson.dev",
    Bio   = "The original AI."
};

var key = await authors.PersistAsync(author);
Console.WriteLine($"Persisted author, key={key}");

// Update with a modified copy (class — must assign to a local)
author.Bio = "Point guard. Hall of Famer.";
var updatedKey = await authors.UpdateAsync(author);
Console.WriteLine($"Updated author, key={updatedKey}");

// ── Object Retrieval — key-based fetch, client assembles the graph ─────────────

Console.WriteLine("\n=== Object Retrieval ===");

var retrieved = await articles.GetAsync(newArticle.Id.ToString());
Console.WriteLine($"Retrieved: {retrieved?.Title}, author={retrieved?.Author?.Name}");

var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
await foreach (var article in articles.GetManyAsync(ids))
    Console.WriteLine($"  Got: {article.Title}");

// ── Object Search — DSL ────────────────────────────────────────────────────────

Console.WriteLine("\n=== Object Search ===");

// Full-text + date filter (EqualTo avoids collision with object.Equals)
var textQuery = Query.For<Article>()
    .Where(a => a.Title,       Contains,    "AI")
    .And(a   => a.IsPublished, EqualTo,     true)
    .And(a   => a.PublishedAt, GreaterThan, DateTime.UtcNow.AddDays(-30))
    .OrderBy(a => a.PublishedAt, descending: true)
    .Page(1, size: 10);

await foreach (var result in articles.SearchAsync(textQuery))
    Console.WriteLine($"  [{result.Score:F2}] {result.Entity.Title}");

// Vector similarity search
float[] queryEmbedding = [0.1f, 0.2f, 0.3f, 0.4f];

var vectorQuery = Query.For<Article>()
    .OrVectorSimilar(a => a.Title, queryEmbedding)
    .Page(1, size: 5);

await foreach (var result in articles.SearchAsync(vectorQuery))
    Console.WriteLine($"  [score={result.Score:F4}] {result.Entity.Title}");

// Tag search using Contains across multiple slugs
var tagQuery = Query.For<Tag>()
    .Where(t => t.Slug, Contains, "basketball")
    .Or(t   => t.Slug, Contains,  "ai")
    .Or(t   => t.Slug, Contains,  "culture")
    .WithLogic(Iverson.Client.Contracts.SearchLogic.Or)
    .OrderBy(t => t.Label);

await foreach (var result in tags.SearchAsync(tagQuery))
    Console.WriteLine($"  Tag: {result.Entity.Label}");

Console.WriteLine("\nDone.");
